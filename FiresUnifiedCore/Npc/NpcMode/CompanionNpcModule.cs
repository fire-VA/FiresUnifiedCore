using UnityEngine;
using System;
using System.Collections.Generic;
using FiresCore.Npc.AI;
using FiresCore.Npc.Interactions;
using FiresCore.Npc.Vault;

namespace FiresCore.Npc.NpcMode
{
    /// <summary>
    /// Lets a companion be stationed as a quest giver, info, dialogue or trader NPC. While stationed, the AI
    /// state machine, combat movement and following are disabled; optional idle mode still lets it wander a
    /// radius and use nearby chairs. Stationed hover text offers the NPC options, otherwise it falls through to
    /// the companion hover text.
    /// </summary>
    public class CompanionNpcModule : MonoBehaviour, Interactable, Hoverable
    {
        #region Settings

        [Header("NPC Mode Settings")]
        [Tooltip("Whether this companion is currently stationed as an NPC")]
        public bool isStationedAsNpc = false;

        [Tooltip("Whether this NPC was placed via hammer (static placement - cannot be unstated to companion)")]
        public bool isStaticPlacement = false;

        [Tooltip("Whether to allow idle wandering when stationed (within wanderRadius)")]
        public bool allowIdleWandering = false;

        [Tooltip("Never turn the body toward nearby players (ZDO npc_no_face_player). kg-migrated NPCs set this: kg bodies hold their placed facing, and turning breaks locked crafting poses.")]
        public bool suppressFacePlayer = false;

        [Tooltip("Radius for idle wandering when stationed (ignored if inside a territory)")]
        public float idleWanderRadius = 20f;
        
        [Tooltip("Buffer zone outside the wander radius before forcing return (prevents rubber-banding)")]
        public float wanderBufferZone = 5f;

        [Tooltip("Use territory bounds for wandering instead of fixed radius when inside a territory")]
        public bool useTerritoryBounds = true;

        [Tooltip("Whether to allow NPC interactions even while following (hybrid mode)")]
        public bool allowNpcInteractionsWhileFollowing = false;

        [Header("NPC Identity")]
        public string npcDisplayName = "";
        public NpcType npcType = NpcType.None;

        [Header("Profiles")]
        public string dialogueProfile = "";
        public string questProfile = "";
        public string infoProfile = "";
        public string traderProfile = "";

        [Header("Animations")]
        [Tooltip("Animation to play when player approaches (empty = none; set deliberately)")]
        public string greetAnimation = "";
        [Tooltip("Animation to play when player leaves (empty = none; set deliberately)")]
        public string byeAnimation = "";

        #endregion

        #region State

        private CompanionController _companion;
        private CompanionCombatMovement _combatMovement;
        private CompanionIdleBehavior _idleBehavior;
        private CompanionInteractionBehavior _interactionBehavior;
        private CompanionAI _companionAI;
        private MonsterAI _monsterAI;
        private Character _character;
        private ZNetView _nview;
        private Animator _animator;
        private Rigidbody _rigidbody;
        private float _staticInertNextCheck;   // throttle for the static-fixture AI-off re-assert

        // Stationed position
        private Vector3 _stationedPosition;
        private Quaternion _stationedRotation;
        private bool _hasStationedPosition = false;

        // Proximity detection for greet/bye animations
        private const float ProximityRange = 10f;
        private bool _playerWasInRange = false;
        private float _proximityCheckTimer = 0f;
        private const float ProximityCheckInterval = 0.25f;

        // Placement mode
        private bool _isInPlacementMode = false;

        // Territory caching
        private float? _currentTerritoryRadius;
        private string _currentTerritoryName;
        private float _lastTerritoryCheck;
        private const float TerritoryCheckInterval = 2f;
        
        // Return home enforcement for wandering NPCs
        private bool _isForceReturningHome;
        private float _lastDistanceCheck;
        private const float DistanceCheckInterval = 1f;
        
        // Track if NPC was directly attacked (for alert state control)
        private float _lastDirectlyAttackedTime;
        private const float DirectAttackAlertDuration = 10f;

        // PERF: Track whether we've already zeroed velocity/moveDir to avoid
        // calling SetMoveDir/SetWalk/SetRun every frame (each hits Harmony patches)

        // Face-player: throttle how often we scan for the nearest player
        private float _facePlayerTimer;
        private const float FacePlayerInterval = 0.5f;
        private const float FacePlayerRange = 15f;

        public static bool VerboseLogging = false;

        #endregion

        #region Properties

        /// <summary>
        /// Returns true if this companion is stationed as an NPC.
        /// Other systems should check this to disable their behavior.
        /// </summary>
        public bool IsStationedAsNpc => isStationedAsNpc;

        /// <summary>
        /// Returns true if this companion can provide NPC interactions right now.
        /// </summary>
        public bool CanProvideNpcInteractions
        {
            get
            {
                if (isStationedAsNpc) return true;
                if (allowNpcInteractionsWhileFollowing) return true;
                return false;
            }
        }

        /// <summary>
        /// Returns the display name for this NPC/Companion.
        /// </summary>
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(npcDisplayName))
                    return npcDisplayName;
                if (_companion != null && !string.IsNullOrEmpty(_companion.companionName))
                    return _companion.companionName;
                return gameObject.name;
            }
        }

        /// <summary>
        /// Returns true if stationed as NPC with a valid position.
        /// </summary>
        public bool IsStationedWithPosition => isStationedAsNpc && _hasStationedPosition;

        /// <summary>
        /// Returns true if this NPC is stationed but allowed to wander (idle behaviors should run).
        /// Used by CompanionAI to determine if it should skip AI updates.
        /// </summary>
        public bool AllowsIdleBehaviors => isStationedAsNpc && allowIdleWandering;
        
        /// <summary>
        /// Returns true if this stationed NPC is currently returning home.
        /// </summary>
        public bool IsReturningHome => _isForceReturningHome || (_combatMovement != null && _combatMovement.HasMoveDestination);
        
        /// <summary>
        /// Returns true if this stationed NPC was recently directly attacked.
        /// Used to determine if NPC should enter alert/combat state.
        /// Stationed NPCs should ONLY enter alert when directly attacked, not when seeing enemies.
        /// </summary>
        public bool WasDirectlyAttacked => Time.time - _lastDirectlyAttackedTime < DirectAttackAlertDuration;
        
        /// <summary>
        /// Call this when the NPC is directly attacked.
        /// This allows the NPC to enter alert/combat state.
        /// </summary>
        /// <summary>Who hit this NPC last (with OnDirectlyAttacked).</summary>
        public Character LastAttacker { get; private set; }

        /// <summary>as <see cref="OnDirectlyAttacked()"/>, and remembers the attacker so the AI can fight back. Add-only.</summary>
        private float _hitLogAt;
        // RPGmaker tells Core from Character.Damage, which runs on the ATTACKER's machine, while the NPC's AI and
        // defence run on its ZDO owner): a hit reported on a non-owner is forwarded to the owner (ZNetView RPC with the attacker's ZDOID).
        private const string HitRpc = "FiresCore_NpcHitBy";
        private bool _hitRpcRegistered;
        private void EnsureHitRpc()
        {
            if (_hitRpcRegistered || _nview == null || !_nview.IsValid()) return;
            _hitRpcRegistered = true;
            _nview.Register<ZDOID>(HitRpc, (sender, id) =>
            {
                if (!_nview.IsOwner()) return;
                var go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(id) : null;
                var who = go != null ? go.GetComponent<Character>() : null;
                OnDirectlyAttackedLocal(who);
            });
        }

        public void OnDirectlyAttacked(Character attacker)
        {
            EnsureHitRpc();
            if (_nview != null && _nview.IsValid() && !_nview.IsOwner())
            {
                var zid = attacker != null && attacker.GetComponent<ZNetView>() is ZNetView av && av.IsValid() ? av.GetZDO().m_uid : ZDOID.None;
                _nview.InvokeRPC(HitRpc, zid);
                FiresCore.Logging.FiresLogger.LogInfo($"[CompanionAI] {DisplayName} hit by {(attacker != null ? attacker.GetHoverName() : "unknown")}: forwarded to its owner");
                return;
            }
            OnDirectlyAttackedLocal(attacker);
        }

        private void OnDirectlyAttackedLocal(Character attacker)
        {
            // a party member (owner, allied owners, their companions) is never its attacker - a stray hit must not turn it on them
            if (attacker != null && !isStationedAsNpc && FiresCore.Npc.Archetypes.ClassTargeting.IsPartyMember(GetComponent<Character>(), attacker)) return;
            if (attacker != null && attacker.gameObject != gameObject) LastAttacker = attacker;
            // one line per hit burst - who hit it and whether its AI can answer (proof for the defend tests)
            if (Time.time >= _hitLogAt) { _hitLogAt = Time.time + 5f; FiresCore.Logging.FiresLogger.LogInfo($"[CompanionAI] {DisplayName} hit by {(attacker != null ? attacker.GetHoverName() + (attacker is Player ? " (player)" : "") : "unknown")}: stationed {isStationedAsNpc}, static {isStaticPlacement}, CompanionAI {(_companionAI == null ? "none" : _companionAI.enabled ? "on" : "OFF")}, owner here {(_nview != null && _nview.IsValid() && _nview.IsOwner())}"); }
            OnDirectlyAttacked();
            TryStartDefence();
        }

        // ── Static NPC self-defence: a fixture fights back within a short leash of its anchor, adjustable
        // in the NPC stats screen (RPGmaker writes the keys) ─────────────────────────────────────────────
        // Body ZDO keys (add-only): "npc_defend" (bool, default false), "npc_defend_leash" (metres, default 2, 0.5..5).
        // A static fixture with npc_defend that is hit fights its LastAttacker with whatever it holds (its own weapon, else the first
        // weapon in its bag, else fists), never more than the leash from its anchor: at the edge it stops, faces and swings, never
        // chases. When the alert ends or the attacker leaves leash + reach it walks back, faces its rotation and is a fixture again.
        // Owner-side only (the ZDO owner simulates the body). The AI brains stay OFF: this drives the body directly.
        public const string DefendKey = "npc_defend", DefendLeashKey = "npc_defend_leash";
        private const float DefendAlertSeconds = 12f, DefendSwingCooldown = 1.1f, DefendReturnTimeout = 6f;
        private enum DefendPhase { None, Fighting, Returning }
        private DefendPhase _defend;
        private Vector3 _defendAnchor; private Quaternion _defendRot; private float _defendLeash = 2f;
        private RigidbodyConstraints _defendConstraints; private float _defendNextSwing, _defendReturnUntil;
        private ItemDrop.ItemData _defendEquipped;

        /// <summary>True while a static fixture is defending itself or walking back (EnforceStaticInertness leaves it alone).</summary>
        public bool IsDefending => _defend != DefendPhase.None;

        private float _defendWhyAt;
        private void DefendWhy(string why)
        {
            if (Time.time < _defendWhyAt) return; _defendWhyAt = Time.time + 10f;
            FiresCore.Logging.FiresLogger.LogDebug($"[CompanionAI] {(_character != null ? _character.GetHoverName() : name)} hit, not defending: {why}");
        }

        private void TryStartDefence()
        {
            if (!isStaticPlacement || allowIdleWandering || HasPatrolRoute() || _defend == DefendPhase.Fighting) return;
            if (_nview == null || !_nview.IsValid() || !_nview.IsOwner()) { DefendWhy("not the owner of its body on this peer"); return; }
            var zdo = _nview.GetZDO();
            // Defending is ON unless the NPC stats switch turned it OFF (unset = defend), since the switch is
            // often never set
            if (zdo == null || !zdo.GetBool(DefendKey, true)) { DefendWhy("Defend when attacked is OFF"); return; }
            var foe = LastAttacker;
            if (foe == null || foe.IsDead() || _character == null || _character.IsDead()) return;
            _defendLeash = Mathf.Clamp(zdo.GetFloat(DefendLeashKey, 2f), 0.5f, 5f);
            if (_defend == DefendPhase.None)
            {
                _defendAnchor = _hasStationedPosition ? _stationedPosition : transform.position;
                _defendRot = _hasStationedPosition ? _stationedRotation : transform.rotation;
                if (_rigidbody != null) { _defendConstraints = _rigidbody.constraints; _rigidbody.constraints = RigidbodyConstraints.FreezeRotation; }
                _defendEquipped = null;
                if (_character is Humanoid h && h.GetCurrentWeapon() == h.m_unarmedWeapon?.m_itemData)
                {
                    var inv = h.GetInventory();
                    if (inv != null) foreach (var it in inv.GetAllItems())
                        if (it != null && it.IsWeapon() && h.EquipItem(it, true)) { _defendEquipped = it; break; }
                }
            }
            _defend = DefendPhase.Fighting;
            FiresCore.Logging.FiresLogger.LogInfo($"[CompanionNpcModule] '{DisplayName}' defends itself against {foe.GetHoverName()} (leash {_defendLeash:0.#} m{(_defendEquipped != null ? ", drew " + _defendEquipped.m_shared.m_name : "")})");
        }

        // Update: true while defending (the fixture hold is skipped this tick)
        private bool DefenceTick()
        {
            if (_defend == DefendPhase.None) return false;
            if (_character == null || _character.IsDead() || _nview == null || !_nview.IsValid() || !_nview.IsOwner()) { EndDefence("not simulated here / dead"); return false; }
            var h = _character as Humanoid;
            Vector3 self = transform.position;
            if (_defend == DefendPhase.Fighting)
            {
                var foe = LastAttacker;
                var weapon = h != null ? h.GetCurrentWeapon() : null;
                float reach = weapon != null && weapon.m_shared.m_attack != null ? Mathf.Max(1.2f, weapon.m_shared.m_attack.m_attackRange) : 1.5f;
                bool gone = foe == null || foe.IsDead() || Time.time - _lastDirectlyAttackedTime > DefendAlertSeconds
                    || Flat(foe.transform.position - _defendAnchor).magnitude > _defendLeash + reach + 1f;
                if (gone) { _defend = DefendPhase.Returning; _defendReturnUntil = Time.time + DefendReturnTimeout; FiresCore.Logging.FiresLogger.LogInfo($"[CompanionNpcModule] '{DisplayName}' stops defending; back to its anchor"); return true; }
                Vector3 toFoe = Flat(foe.transform.position - self);
                if (toFoe.sqrMagnitude > 0.01f) _character.SetLookDir(toFoe.normalized);
                // where it may stand: toward the foe, clamped to the leash circle
                Vector3 want = _defendAnchor + Vector3.ClampMagnitude(Flat(foe.transform.position - _defendAnchor), _defendLeash);
                Vector3 step = Flat(want - self);
                bool inReach = toFoe.magnitude <= reach;
                _character.SetMoveDir(!inReach && step.magnitude > 0.3f ? step.normalized : Vector3.zero);
                if (inReach && h != null && Time.time >= _defendNextSwing && !h.InAttack())
                {
                    _defendNextSwing = Time.time + DefendSwingCooldown;
                    h.StartAttack(foe, false);
                }
                return true;
            }
            // Returning
            Vector3 home = Flat(_defendAnchor - self);
            if (home.magnitude > 0.25f && Time.time < _defendReturnUntil) { _character.SetLookDir(home.normalized); _character.SetMoveDir(home.normalized); return true; }
            EndDefence("home");
            return false;
        }

        private void EndDefence(string why)
        {
            if (_defend == DefendPhase.None) return;
            _defend = DefendPhase.None;
            if (_character != null) _character.SetMoveDir(Vector3.zero);
            if (_defendEquipped != null && _character is Humanoid h && h.IsItemEquiped(_defendEquipped)) h.UnequipItem(_defendEquipped, false);
            _defendEquipped = null;
            transform.position = _defendAnchor; transform.rotation = _defendRot;
            if (_rigidbody != null) { _rigidbody.position = _defendAnchor; _rigidbody.rotation = _defendRot; _rigidbody.velocity = Vector3.zero; _rigidbody.constraints = _defendConstraints; }
            if (_character != null) _character.SetLookDir(_defendRot * Vector3.forward);
            FiresCore.Logging.FiresLogger.LogInfo($"[CompanionNpcModule] '{DisplayName}' is a fixture again at its anchor ({why})");
        }

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        public void OnDirectlyAttacked()
        {
            _lastDirectlyAttackedTime = Time.time;
            
            if (VerboseLogging)
                Debug.Log($"[CompanionNpcModule] {DisplayName} was directly attacked - allowing alert state");
        }

        /// <summary>
        /// Gets the effective wander radius (territory radius or configured radius).
        /// Uses CompanionSettings.IdleWanderRadius as the default if no per-NPC override.
        /// </summary>
        public float EffectiveWanderRadius
        {
            get
            {
                if (useTerritoryBounds && _currentTerritoryRadius.HasValue)
                {
                    return _currentTerritoryRadius.Value;
                }
                // Use per-NPC override if set, otherwise use global config
                return idleWanderRadius > 0 ? idleWanderRadius : CompanionSettings.IdleWanderRadius;
            }
        }

        /// <summary>
        /// Gets the stationed/home position.
        /// </summary>
        public Vector3 StationedPosition => _stationedPosition;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            if (GetComponent<BodyModelWatcher>() == null) gameObject.AddComponent<BodyModelWatcher>();   // npc_model_override on every peer (BodyModelSwap)
            _combatMovement = GetComponent<CompanionCombatMovement>();
            _idleBehavior = GetComponent<CompanionIdleBehavior>();
            _interactionBehavior = GetComponent<CompanionInteractionBehavior>();
            _companionAI = GetComponent<CompanionAI>();
            _monsterAI = GetComponent<MonsterAI>();
            _character = GetComponent<Character>();
            _nview = GetComponent<ZNetView>();
            _animator = GetComponentInChildren<Animator>(true);
            _rigidbody = GetComponent<Rigidbody>();
        }

        private void Start()
        {
            EnsureHitRpc();   // every peer registers, so the owner can receive forwarded hits
            LoadFromZDO();
            
            // Apply stationed state if loaded from ZDO
            if (isStationedAsNpc)
            {
                ApplyStationedState();
            }
        }

        private void Update()
        {
            if (_isInPlacementMode) return;

            // Static placed NPCs have no CompanionController - allow them through
            if (_companion == null && !isStaticPlacement) return;

            // A body a player is DRIVING (FiresValcast possession) belongs to its driver: no fixture hold, no walk home.
            if (HoldForPossession()) return;

            // a fixture defending itself (npc_defend) is driven by DefenceTick until it is home again
            if (DefenceTick()) return;

            // Static fixtures NEVER run AI — re-assert every tick regardless of the stationed-flag timing.
            EnforceStaticInertness();

            // If stationed as NPC, enforce the stationed state
            if (isStationedAsNpc)
            {
                EnforceStationedState();
                UpdateProximityDetection();
            }
        }

        /// <summary>
        /// A static fixture (placed, NOT a wanderer, NOT on a patrol route) must NEVER aggro, dodge, or face
        /// enemies. On relog various companion/vanilla systems re-enable CompanionAI/MonsterAI in the seconds
        /// after spawn — the body then rolls and looks at enemies. Re-assert the fixture contract on a ~0.5s
        /// throttle (cached refs, cheap): AI brains OFF, m_aiSkipTarget ON, so it stays an inert prop. Wander
        /// and patrol NPCs are excluded — they keep their AI. Runs on every machine (the visual is client-side).
        /// </summary>
        // The static stage hook. RPGmaker's StaticNpcInitializer calls MarkStaticStageApplied() when its
        // ApplyBodyStage has finished, so the ai-guard knows exactly when the fixture contract was applied.
        private bool _staticStageApplied;
        private static bool s_stageHookInUse;

        /// <summary>
        /// The mod that stages this body (RPGmaker's StaticNpcInitializer.ApplyBodyStage) calls this when the static / wander /
        /// patrol stage has been applied. Before it, the ai-guard holds a static fixture's AI off quietly (the prefab's AI is still
        /// on); after it, an AI switched back on is a real re-enable and is warned about.
        /// </summary>
        public void MarkStaticStageApplied()
        {
            s_stageHookInUse = true;
            if (_staticStageApplied) return;
            _staticStageApplied = true;
            FiresCore.Logging.FiresLogger.LogDebug($"[CompanionNpcModule][ai-guard] '{DisplayName}': static stage applied ({Time.time - _loadedAt:0.0} s after load)");
        }

        /// <summary>True once MarkStaticStageApplied has been called for this body.</summary>
        public bool StaticStageApplied => _staticStageApplied;

        // Fire 10-24 (hand-over 56): "ole gorm here is just sliding in place it seems strafing" - a static fixture (AI off, no defend
        // line in the log) playing a walk / strafe. This names what drives it: a move direction someone set, a body velocity, or only
        // the animator's speed floats, with the body vs ground height. A fixture never walks, so a stray move direction is zeroed (logged).
        private float _motionProbeNext;

        // Who sets a fixture's move direction? ([fgn] 10-24: no FGN loop, and a disabled CompanionAI leaves BaseAI.Instances.) The first
        // non-zero SetMoveDir on a static fixture with no defend / possession going, once per fixture per session, logs the caller stack.
        private static readonly HashSet<int> s_moveDirCallerLogged = new HashSet<int>();
        [HarmonyLib.HarmonyPatch(typeof(Character), nameof(Character.SetMoveDir))]
        private static class Character_SetMoveDir_FixtureProbe
        {
            private static void Prefix(Character __instance, Vector3 dir)
            {
                if (__instance == null || dir.sqrMagnitude < 0.0001f || s_moveDirCallerLogged.Contains(__instance.GetInstanceID())) return;
                var m = __instance.GetComponent<CompanionNpcModule>();
                if (m == null || !m.isStaticPlacement || m.allowIdleWandering || m.IsDefending || m._returningFromPossession) return;
                s_moveDirCallerLogged.Add(__instance.GetInstanceID());
                string stack = new System.Diagnostics.StackTrace(1, false).ToString().Replace("\r", "").Replace("\n", " <- ");
                if (stack.Length > 700) stack = stack.Substring(0, 700);
                FiresCore.Logging.FiresLogger.LogDebug($"[CompanionNpcModule][fixture-motion] '{m.DisplayName}' SetMoveDir({dir.x:0.00},{dir.z:0.00}) while a fixture ({(ZNet.instance != null && ZNet.instance.IsServer() ? "server" : "client")}); caller: {stack}");
            }
        }
        private static readonly int s_fwdSpeed = Animator.StringToHash("forward_speed"), s_sideSpeed = Animator.StringToHash("sidestep_speed");
        private void ProbeFixtureMotion()
        {
            if (_character == null || IsDefending || _returningFromPossession) return;
            Vector3 moveDir = _character.GetMoveDir();
            Vector3 vel = _rigidbody != null ? _rigidbody.velocity : Vector3.zero;
            var anim = _character.GetComponentInChildren<Animator>();
            float fwd = 0f, side = 0f;
            try { if (anim != null && anim.isActiveAndEnabled) { fwd = anim.GetFloat(s_fwdSpeed); side = anim.GetFloat(s_sideSpeed); } } catch { }
            bool moving = moveDir.sqrMagnitude > 0.0001f || vel.sqrMagnitude > 0.0025f || Mathf.Abs(fwd) > 0.05f || Mathf.Abs(side) > 0.05f;
            if (!moving) return;
            if (moveDir.sqrMagnitude > 0.0001f) _character.SetMoveDir(Vector3.zero);
            if (Time.time < _motionProbeNext) return;
            _motionProbeNext = Time.time + 10f;
            Vector3 p = transform.position;
            float ground = ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(p, out float g) ? g : float.NaN;
            bool owner = _nview != null && _nview.IsValid() && _nview.IsOwner();
            FiresCore.Logging.FiresLogger.LogDebug($"[CompanionNpcModule][fixture-motion] '{DisplayName}' moves while a fixture: moveDir {moveDir:F2}{(moveDir.sqrMagnitude > 0.0001f ? " (zeroed)" : "")}, " +
                $"body vel {vel:F2}, kinematic {(_rigidbody != null && _rigidbody.isKinematic)}, constraints {(_rigidbody != null ? _rigidbody.constraints.ToString() : "-")}, " +
                $"anim forward {fwd:0.00} side {side:0.00}, y {p.y:0.00} vs ground {ground:0.00}, owner {owner}, onGround {_character.IsOnGround()}");
        }

        private void EnforceStaticInertness()
        {
            if (!isStaticPlacement || allowIdleWandering || HasPatrolRoute()) return;
            if (Time.time < _staticInertNextCheck) return;
            _staticInertNextCheck = Time.time + 0.5f;

            ProbeFixtureMotion();
            bool caught = false;
            if (_companionAI != null && _companionAI.enabled) { _companionAI.enabled = false; caught = true; }
            if (_monsterAI != null && _monsterAI.enabled) { _monsterAI.enabled = false; caught = true; }
            if (_character != null && !_character.m_aiSkipTarget) { _character.m_aiSkipTarget = true; caught = true; }
            // Only logs when it ACTUALLY caught a re-enable — proves the relog dodge/look-at-enemy bug is a
            // downstream re-enable (names the culprit's timing) rather than a willMove data problem.
            if (caught)
            {
                // right after a possession ends the driver's mod puts the AI back as IT saw it; the fixture's own state wins.
                if (Time.time - _releasedAt < ReleaseGraceSeconds)
                    FiresCore.Logging.FiresLogger.LogDebug($"[CompanionNpcModule][ai-guard] '{DisplayName}': AI off again after its possession ended (the fixture's own state)");
                // Nothing re-enabled them. LoadFromZDO reads npc_static_placement
                // at load, which arms this guard at once, while RPGmaker's StaticNpcInitializer turns the AI off only after its
                // DelayedInit (frames + polls, seconds). Until then the AI is still on from the prefab: holding it off is the
                // contract, not a re-enable. A re-enable after this window still warns.
                // exact, not timed. Once the body's owner mod reports its stage (MarkStaticStageApplied), a fixture whose
                // stage hasn't been reported yet is simply not staged yet. The 15 s window remains only for builds that never call it.
                else if (!_staticStageApplied && (s_stageHookInUse || Time.time - _loadedAt < StageGraceSeconds))
                {
                    if (!_preStageLogged)
                    {
                        _preStageLogged = true;
                        FiresCore.Logging.FiresLogger.LogDebug($"[CompanionNpcModule][ai-guard] '{DisplayName}': AI held off before the static stage applied ({Time.time - _loadedAt:0.0} s after load)");
                    }
                }
                else
                    Debug.LogWarning($"[CompanionNpcModule][ai-guard] re-disabled AI on static fixture '{DisplayName}' — something re-enabled it after the static stage was applied");
            }
        }

        // ── A body a player is DRIVING (FiresValcast possession, CompanionPossession) ───────────────────────────────────────
        // a static fixture's Rigidbody is frozen on every axis (FiresNPCs' StaticNpcInitializer, once at spawn), so a possessed
        // fixture could not take a step; its position was unfrozen while driven.
        // Possession could leave the NPC frictionless (sliding into holes), and a released NPC should
        // return to its anchor and resume its normal behaviour:
        // they normally do"):
        // - Driven: the WHOLE walking-NPC body a wandering Fires NPC has (FiresNPCs' willMove stage): rotation-only freeze, gravity,
        //   not kinematic, the Humanoid enabled, rotation synced, an ordinary target. Vanilla's own motion, ground contact and
        //   friction then run on the owner, which is the possessor's peer (claimed here if it isn't). Every property is saved first.
        //   Debug evidence every 2 s on the owner (ground, speed, friction, owner).
        // - Released: the AI comes back exactly as it was before (a fixture: off), and a stationed or static NPC walks back to its
        //   anchor (snapping only when stuck, too far or out of time), takes its placed facing, and its routine resumes: a fixture
        //   re-freezes with every saved property, a stationed NPC takes up its station again. A companion just resumes following.
        //   A re-disable the ai-guard makes right after a release is expected, and logged at Debug.
        private bool _possessionFreed;
        private RigidbodyConstraints _constraintsBeforePossession;
        private bool _gravityBefore, _kinematicBefore, _humanoidBefore, _syncRotationBefore, _aiSkipBefore, _companionAIBefore, _monsterAIBefore;
        private float _possessedReportAt, _releasedAt = -100f;
        private bool _returningFromPossession, _claimedForPossessor;
        // the ai-guard's quiet window after load, while RPGmaker's StaticNpcInitializer has not applied the static stage yet.
        private float _loadedAt = -100f;
        private bool _preStageLogged;
        private const float StageGraceSeconds = 15f;
        private float _returnStart, _returnDeadline, _returnLastProgressAt, _returnBestDistance;
        private const float ReturnArrive = 0.5f, ReturnSnapDistance = 40f, ReturnStuckSeconds = 3f, ReturnSecondsPerMetre = 0.6f,
                            ReturnSlackSeconds = 6f, ReleaseGraceSeconds = 10f, PossessedReportSeconds = 2f;

        private bool HoldForPossession()
        {
            bool possessed = FiresCore.Npc.CompanionPossession.IsPossessed(_nview);
            if (possessed != _possessionFreed)
            {
                if (possessed) BeginPossession();
                else EndPossession();
            }
            if (possessed)
            {
                KeepPossessorOwning();
                ReportPossessed();
                return true;
            }
            if (_returningFromPossession)
            {
                TickReturnHome();
                return true;
            }
            return false;
        }

        private void BeginPossession()
        {
            _possessionFreed = true;
            _returningFromPossession = false;   // a re-possession mid-walk-home: the driver has it again
            _claimedForPossessor = false;
            var sync = GetComponent<ZSyncTransform>();
            if (_rigidbody != null)
            {
                _constraintsBeforePossession = _rigidbody.constraints;
                _gravityBefore = _rigidbody.useGravity;
                _kinematicBefore = _rigidbody.isKinematic;
                FiresCore.Npc.KinematicSync.Set(_rigidbody, false, "NpcModule enter");
                _rigidbody.useGravity = true;
                _rigidbody.constraints = RigidbodyConstraints.FreezeRotation;
            }
            _humanoidBefore = _character == null || _character.enabled;
            if (_character != null) _character.enabled = true;
            _syncRotationBefore = sync == null || sync.m_syncRotation;
            if (sync != null) sync.m_syncRotation = true;
            _aiSkipBefore = _character != null && _character.m_aiSkipTarget;
            if (_character != null) _character.m_aiSkipTarget = false;
            _companionAIBefore = _companionAI != null && _companionAI.enabled;
            _monsterAIBefore = _monsterAI != null && _monsterAI.enabled;
            FiresCore.Logging.FiresLogger.LogDebug($"[CompanionNpcModule] '{DisplayName}' possessed: walking body on (was constraints {_constraintsBeforePossession}, " +
                $"gravity {_gravityBefore}, kinematic {_kinematicBefore}, humanoid {_humanoidBefore}, syncRotation {_syncRotationBefore}, aiSkip {_aiSkipBefore}, " +
                $"AI companion {_companionAIBefore} / monster {_monsterAIBefore}); fixture hold off");
        }

        private void EndPossession()
        {
            _possessionFreed = false;
            _releasedAt = Time.time;
            var sync = GetComponent<ZSyncTransform>();
            if (_character != null) { _character.m_aiSkipTarget = _aiSkipBefore; _character.SetMoveDir(Vector3.zero); }
            if (sync != null) sync.m_syncRotation = _syncRotationBefore;
            // The AI exactly as it was; a companion's controller owns its own AI (it stood down while driven) and takes it back itself.
            if (_companion == null)
            {
                if (_companionAI != null) _companionAI.enabled = _companionAIBefore;
                if (_monsterAI != null) _monsterAI.enabled = _monsterAIBefore;
            }
            bool goesHome = _hasStationedPosition && (isStationedAsNpc || isStaticPlacement);
            if (!goesHome)
            {
                RestoreBody();
                FiresCore.Logging.FiresLogger.LogDebug($"[CompanionNpcModule] '{DisplayName}' released: body and AI as before; routine: {Routine()}");
                return;
            }
            _returningFromPossession = true;
            _returnStart = Time.time;
            _returnBestDistance = FlatDistance(transform.position, _stationedPosition);
            _returnLastProgressAt = Time.time;
            _returnDeadline = Time.time + ReturnSlackSeconds + _returnBestDistance * ReturnSecondsPerMetre;
            FiresCore.Logging.FiresLogger.LogDebug($"[CompanionNpcModule] '{DisplayName}' released {_returnBestDistance:0.0} m from its anchor: walking home " +
                $"(AI companion {_companionAIBefore} / monster {_monsterAIBefore} as before)");
        }

        // Every saved body property back (a fixture: frozen again).
        private void RestoreBody()
        {
            if (_rigidbody != null)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
                _rigidbody.constraints = _constraintsBeforePossession;
                _rigidbody.useGravity = _gravityBefore;
                FiresCore.Npc.KinematicSync.Set(_rigidbody, _kinematicBefore, "NpcModule restore");
            }
            if (_character != null) _character.enabled = _humanoidBefore;
        }

        // The owner walks the body to its anchor through vanilla's own movement; the other peers see it through ZSyncTransform and
        // finish once it is there.
        private void TickReturnHome()
        {
            float distance = FlatDistance(transform.position, _stationedPosition);
            bool owner = _nview != null && _nview.IsValid() && _nview.IsOwner();
            if (!owner)
            {
                if (distance <= ReturnArrive || Time.time > _returnDeadline + ReturnSlackSeconds) FinishReturn("arrived", distance);
                return;
            }
            if (distance <= ReturnArrive) { FinishReturn("walked", distance); return; }
            if (distance > ReturnSnapDistance) { FinishReturn($"snapped (more than {ReturnSnapDistance:0} m away)", distance); return; }
            if (Time.time > _returnDeadline) { FinishReturn("snapped (out of time)", distance); return; }
            if (distance < _returnBestDistance - 0.1f) { _returnBestDistance = distance; _returnLastProgressAt = Time.time; }
            else if (Time.time - _returnLastProgressAt > ReturnStuckSeconds) { FinishReturn($"snapped (stuck {ReturnStuckSeconds:0} s)", distance); return; }
            if (_character == null) return;
            Vector3 dir = _stationedPosition - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return;
            dir.Normalize();
            _character.SetMoveDir(dir);
            _character.SetLookDir(dir);
        }

        private void FinishReturn(string how, float distance)
        {
            _returningFromPossession = false;
            _releasedAt = Time.time;   // the ai-guard's grace runs from here: the fixture hold resumes now
            bool owner = _nview != null && _nview.IsValid() && _nview.IsOwner();
            if (_character != null) _character.SetMoveDir(Vector3.zero);
            if (owner)
            {
                // Exactly on the anchor with the placed facing (the walk ends within ReturnArrive; a snap is the whole way).
                transform.position = _stationedPosition;
                if (_rigidbody != null) _rigidbody.position = _stationedPosition;
                transform.rotation = _stationedRotation;
                if (_rigidbody != null) _rigidbody.rotation = _stationedRotation;
                if (_character != null) _character.SetLookDir(_stationedRotation * Vector3.forward);
            }
            RestoreBody();
            FiresCore.Logging.FiresLogger.LogDebug($"[CompanionNpcModule] '{DisplayName}' back at its anchor ({how}, {distance:0.0} m left, {Time.time - _returnStart:0.0} s" +
                $"{(owner ? "" : ", seen from a non-owner")}); facing {_stationedRotation.eulerAngles.y:0}°; routine: {Routine()}");
        }

        private string Routine()
        {
            if (_companion != null && !isStationedAsNpc) return "companion, follows again";
            if (HasPatrolRoute()) return "patrol resumes";
            if (isStaticPlacement && !allowIdleWandering) return "static fixture, frozen at its anchor";
            return allowIdleWandering ? "stationed, wanders its bounds again" : "stationed, holds its mark again";
        }

        // The possessor's peer simulates the body (vanilla motion runs only on the owner); FGN 1.5.37+ keeps it so, this makes sure.
        private void KeepPossessorOwning()
        {
            if (_nview == null || !_nview.IsValid() || _nview.IsOwner() || Player.m_localPlayer == null) return;
            long possessor = _nview.GetZDO().GetLong(FiresCore.Npc.CompanionPossession.PossessedKey, 0L);
            if (possessor == 0L || possessor != Player.m_localPlayer.GetPlayerID()) return;
            _nview.ClaimOwnership();
            if (!_claimedForPossessor)
            {
                _claimedForPossessor = true;
                FiresCore.Logging.FiresLogger.LogDebug($"[CompanionNpcModule] '{DisplayName}' claimed for its possessor (this peer drives it)");
            }
        }

        // Evidence for a slide: on the owner every PossessedReportSeconds.
        private void ReportPossessed()
        {
            if (Time.time < _possessedReportAt || _character == null || _nview == null || !_nview.IsValid() || !_nview.IsOwner()) return;
            _possessedReportAt = Time.time + PossessedReportSeconds;
            var collider = _character.GetComponent<Collider>();
            var material = collider != null ? collider.sharedMaterial : null;
            Vector3 v = _rigidbody != null ? _rigidbody.linearVelocity : Vector3.zero;
            FiresCore.Logging.FiresLogger.LogDebug(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[CompanionNpcModule] '{0}' driven: at ({1:0.0},{2:0.0},{3:0.0}) onGround {4} speed {5:0.00} m/s moveDir {6:0.00} friction {7} constraints {8} gravity {9} humanoid {10}",
                DisplayName, transform.position.x, transform.position.y, transform.position.z, _character.IsOnGround(), v.magnitude,
                _character.GetMoveDir().magnitude, material != null ? material.dynamicFriction.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "?",
                _rigidbody != null ? _rigidbody.constraints.ToString() : "?", _rigidbody != null && _rigidbody.useGravity, _character.enabled));
        }

        private static float FlatDistance(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

        private void LateUpdate()
        {
            if (!isStationedAsNpc || !_hasStationedPosition || _isInPlacementMode) return;
            if (_possessionFreed || _returningFromPossession) return;   // its driver's look, or the walk home

            // Face nearest player - works WITH Valheim's Character rotation system
            // instead of fighting it. We just set m_lookDir and let Character.UpdateBodyRotation
            // do the smooth turning.
            if (_character == null) return;

            // Per-NPC opt-out (kg-migrated bodies): hold the placed facing, never greet-turn.
            if (suppressFacePlayer) return;

            // Only turn to face a nearby player while STOPPED. Vanilla Character.UpdateRotation turns the body
            // toward m_lookDir only when m_moveDir is zero (otherwise it faces the move heading), so writing
            // look while walking does nothing useful and risks fighting the patrol's heading. Gating on stopped
            // makes the NPC turn to greet a player the moment it halts (the patrol chat-pause, an endpoint wait,
            // or a stationary decorative NPC) without ever disturbing it mid-stride.
            if (_character.m_moveDir.sqrMagnitude > 0.01f) return;

            _facePlayerTimer += Time.deltaTime;
            if (_facePlayerTimer < FacePlayerInterval) return;
            _facePlayerTimer = 0f;

            var lookDir = GetDirectionToNearestPlayer();
            if (lookDir.sqrMagnitude > 0.001f)
            {
                // Set BOTH: Character.UpdateRotation turns the body toward m_lookYaw (Character.cs:835/1178),
                // NOT m_lookDir — writing only m_lookDir (as before) changed the look but never turned the body.
                _character.m_lookDir = lookDir;
                _character.m_lookYaw = Quaternion.LookRotation(lookDir);
            }
        }

        /// <summary>
        /// Returns a normalized horizontal direction toward the nearest player within range,
        /// or Vector3.zero if no player is close enough.
        /// </summary>
        private Vector3 GetDirectionToNearestPlayer()
        {
            Player closest = null;
            float closestDist = FacePlayerRange;

            foreach (var player in Player.GetAllPlayers())
            {
                if (player == null || player.IsDead()) continue;
                float playerDistance = Vector3.Distance(transform.position, player.transform.position);
                if (playerDistance < closestDist)
                {
                    closestDist = playerDistance;
                    closest = player;
                }
            }

            if (closest == null) return Vector3.zero;

            var dir = closest.transform.position - transform.position;
            dir.y = 0f;
            return dir.sqrMagnitude > 0.001f ? dir.normalized : Vector3.zero;
        }

        #endregion

        #region Stationed State Management

        /// <summary>
        /// Applies the stationed state by disabling all movement systems.
        /// Called when loading from ZDO or when stationing.
        /// </summary>
        public void ApplyStationedState()
        {
            if (!isStationedAsNpc) return;

            // Set initial look direction so the NPC starts facing roughly the right way
            if (_hasStationedPosition)
            {
                transform.position = _stationedPosition;
                SetInitialLookDir(_stationedRotation);
            }

            // Disable ZSyncTransform rotation sync for stationed non-wandering NPCs.
            // ZSyncTransform overwrites body rotation from the ZDO every FixedUpdate on
            // non-owner clients, which fights with our face-nearest-player logic.
            // Each client independently controls its own NPC look direction.
            if (!allowIdleWandering)
            {
                var syncTransform = GetComponent<ZSyncTransform>();
                if (syncTransform != null)
                    syncTransform.m_syncRotation = false;
            }

            // Hold position via the AI Stay state + home position — NOT by locking movement or freezing the
            // body. The NPC keeps normal companion physics (gravity on); EnforceWanderBounds walks it back to
            // its mark if it is knocked off. This is exactly a companion told to Stay.
            if (_companionAI != null)
                _companionAI.SetStayPosition(_stationedPosition);
            if (_combatMovement != null && _hasStationedPosition)
                _combatMovement.SetHomePosition(_stationedPosition);

            if (VerboseLogging)
                Debug.Log($"[CompanionNpcModule] Applied stationed state for {DisplayName}");
        }

        /// <summary>
        /// Enforces the stationed state. Velocity/moveDir zeroing is done once
        /// and only re-applied after a position drift snap, to avoid per-frame
        /// SetMoveDir calls that each trigger Harmony patch overhead.
        /// </summary>
        private void EnforceStationedState()
        {
            if (!_hasStationedPosition) return;

            // Never lock movement — a stationed NPC must stay free to walk back to its mark or patrol. The
            // body keeps normal companion physics; position is held by the AI (EnforceWanderBounds), not a snap.
            if (_combatMovement != null && _combatMovement.IsMovementLocked)
                _combatMovement.UnlockMovement();

            // Patrol drives movement itself — don't also pull the NPC home.
            if (HasPatrolRoute()) return;

            // Stationary or wandering: the AI walks the NPC home if it drifts past the return radius (replaces
            // the old per-frame position snap — same as a companion told to Stay that gets knocked away).
            EnforceWanderBounds();
        }

        private bool HasPatrolRoute()
        {
            var assignment = GetComponent<FiresCore.Npc.Patrol.PatrolAssignment>();
            return assignment != null && assignment.HasRoute;
        }

        /// <summary>
        /// Enforces wander bounds for stationed NPCs with idle wandering enabled.
        /// Uses CompanionSettings.ReturnHomeRadius as the trigger distance.
        /// This takes priority over ALL other idle behaviors.
        /// </summary>
        private void EnforceWanderBounds()
        {
            if (!_hasStationedPosition) return;
            
            // Update territory check periodically
            if (useTerritoryBounds && Time.time - _lastTerritoryCheck > TerritoryCheckInterval)
            {
                _lastTerritoryCheck = Time.time;
                _currentTerritoryRadius = FiresCore.Bridge.NpcModeBridge.GetTerritoryRadius(transform.position);
                _currentTerritoryName = FiresCore.Bridge.NpcModeBridge.GetTerritoryName(transform.position);
            }
            
            // Check distance periodically (not every frame)
            if (Time.time - _lastDistanceCheck < DistanceCheckInterval) return;
            _lastDistanceCheck = Time.time;
            
            float distanceFromHome = Vector3.Distance(transform.position, _stationedPosition);
            
            // Use centralized return home radius from CompanionSettings
            float returnRadius = CompanionSettings.ReturnHomeRadius;
            float hardLimit = CompanionSettings.HardWanderLimit;
            float effectiveLimit = Mathf.Min(EffectiveWanderRadius, hardLimit);
            
            // If we're force returning, check if we've arrived
            if (_isForceReturningHome)
            {
                if (distanceFromHome < effectiveLimit * 0.5f)
                {
                    // Arrived home - stop force return
                    _isForceReturningHome = false;
                    
                    if (_combatMovement != null && _combatMovement.HasMoveDestination)
                    {
                        _combatMovement.ClearMoveDestination();
                    }
                    
                    if (VerboseLogging)
                        Debug.Log($"[CompanionNpcModule] {DisplayName} arrived home, resuming normal behavior");
                }
                else
                {
                    // Still returning - ensure destination is set
                    if (_combatMovement != null && !_combatMovement.HasMoveDestination)
                    {
                        _combatMovement.SetMoveDestination(_stationedPosition);
                    }
                }
                return;
            }
            
            // Check if we've wandered too far - use the configured return radius
            if (distanceFromHome > returnRadius)
            {
                // Force return home - this takes priority over everything
                _isForceReturningHome = true;
                
                // Cancel any idle behaviors
                if (_idleBehavior != null)
                {
                    _idleBehavior.OnCombatStarted(); // This cancels all idle behaviors
                }
                
                // Cancel any chair sitting
                if (_interactionBehavior != null && _interactionBehavior.IsAttached)
                {
                    _interactionBehavior.ForceDetach();
                }
                
                // Set movement destination to home
                if (_combatMovement != null)
                {
                    _combatMovement.SetMoveDestination(_stationedPosition);
                }
                
                // Also tell CompanionAI
                if (_companionAI != null)
                {
                    _companionAI.SetCommandDestination(_stationedPosition);
                }
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionNpcModule] {DisplayName} wandered too far ({distanceFromHome:F1}m > {returnRadius:F1}m), forcing return home");
            }
            
            // Always ensure home position is set in combat movement
            if (_combatMovement != null && !_combatMovement.HasHomePosition)
            {
                _combatMovement.SetHomePosition(_stationedPosition);
            }
        }
        
        // Return home behavior is now handled by the standard CompanionAI/CompanionCombatMovement
        // "Stay" behavior - no custom logic needed here

        /// <summary>
        /// Removes the stationed state, allowing normal companion behavior.
        /// </summary>
        private void RemoveStationedState()
        {
            // Re-enable ZSyncTransform rotation sync
            var syncTransform = GetComponent<ZSyncTransform>();
            if (syncTransform != null)
                syncTransform.m_syncRotation = true;

            // Unlock movement
            if (_combatMovement != null)
            {
                _combatMovement.UnlockMovement();
                _combatMovement.ClearHomePosition();
            }

            if (VerboseLogging)
                Debug.Log($"[CompanionNpcModule] Removed stationed state for {DisplayName}");
        }

        #endregion

        #region Proximity Detection

        private void UpdateProximityDetection()
        {
            _proximityCheckTimer += Time.deltaTime;
            if (_proximityCheckTimer < ProximityCheckInterval) return;
            _proximityCheckTimer = 0f;

            var player = Player.m_localPlayer;
            if (player == null) return;

            float distance = Vector3.Distance(transform.position, player.transform.position);
            bool playerInRange = distance <= ProximityRange;

            if (playerInRange && !_playerWasInRange)
            {
                PlayGreetAnimation();
            }
            else if (!playerInRange && _playerWasInRange)
            {
                PlayByeAnimation();
            }

            _playerWasInRange = playerInRange;
        }

        private void PlayGreetAnimation()
        {
            if (_animator == null) return;
            // Greet plays ONLY when an animation was deliberately configured — no default-wave fallback.
            if (string.IsNullOrEmpty(greetAnimation) || greetAnimation == "(none)") return;
            if (HasAnimatorParameter(greetAnimation))
                _animator.SetTrigger(greetAnimation);
        }

        private void PlayByeAnimation()
        {
            if (_animator == null) return;
            // Bye plays ONLY when an animation was deliberately configured — no default-wave fallback.
            if (string.IsNullOrEmpty(byeAnimation) || byeAnimation == "(none)") return;
            if (HasAnimatorParameter(byeAnimation))
                _animator.SetTrigger(byeAnimation);
        }

        private bool HasAnimatorParameter(string paramName)
        {
            if (_animator == null) return false;
            foreach (var param in _animator.parameters)
            {
                if (param.name == paramName) return true;
            }
            return false;
        }

        #endregion

        #region Station/Unstation

        /// <summary>
        /// Stations this companion as an NPC at the current position.
        /// </summary>
        public void StationAtCurrentPosition()
        {
            StationAtPosition(transform.position, transform.rotation);
        }

        /// <summary>
        /// Sets the stationed position and rotation without triggering messages,
        /// vault saves, or ZDO writes. Used by StaticNpcInitializer on all machines
        /// (client + server) to set up in-memory state.
        /// </summary>
        public void SetStationedPositionDirect(Vector3 position, Quaternion rotation)
        {
            _stationedPosition = position;
            _stationedRotation = rotation;
            _hasStationedPosition = true;
            transform.position = position;
            SetInitialLookDir(rotation);
        }

        /// <summary>
        /// Points the Character's look direction toward the stationed forward.
        /// Used on first placement/load so the NPC starts facing roughly the right way.
        /// Valheim's own rotation system will smooth-turn from there.
        /// </summary>
        private void SetInitialLookDir(Quaternion rotation)
        {
            if (_character == null) return;
            var forward = rotation * Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude > 0.001f)
            {
                // Set BOTH, exactly like the greet-turn path above: Character.UpdateRotation turns the BODY
                // toward m_lookYaw (Character.cs:835/1178), NOT m_lookDir. Writing only m_lookDir (as this did
                // before) aimed the head/look but never rotated the body — so a stationed/migrated NPC held its
                // default spawn facing no matter what placement rotation we passed. THIS was the "rotation is
                // still off, and neither offset changes it" bug: the offset went into a field the body ignores.
                _character.m_lookDir = forward.normalized;
                _character.m_lookYaw = Quaternion.LookRotation(forward.normalized);
            }
        }

        /// <summary>
        /// Stations this companion as an NPC at the specified position.
        /// </summary>
        public void StationAtPosition(Vector3 position, Quaternion rotation)
        {
            isStationedAsNpc = true;
            _stationedPosition = position;
            _stationedRotation = rotation;
            _hasStationedPosition = true;

            // Move to position immediately
            transform.position = position;
            SetInitialLookDir(rotation);

            // Apply the stationed state (disables movement systems)
            ApplyStationedState();

            SaveToZDO();

            // CRITICAL: Save to vault with IsStationedAsNpc flag so companion is NOT restored on login
            if (_companion != null)
            {
                _companion.SaveCompanionToVault();
            }

            // ROSTER MIRROR (Phase 3): persistent intent = Stationed.
            // Captured AFTER station position is set so the snapshot's
            // Stationed* fields reflect the new station location.
            if (_companion != null)
            {
                try
                {
                    var ownerForRoster = _companion.GetOwner();
                    if (ownerForRoster != null)
                        CompanionRosterWriter.OnStationedAsNpc(ownerForRoster, _companion);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CompanionNpcModule] Roster write for Stationed failed (non-fatal): {ex.Message}");
                }
            }

            if (VerboseLogging)
                Debug.Log($"[CompanionNpcModule] {DisplayName} stationed at {position}");

            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                $"{DisplayName} has been stationed as an NPC");
        }

        /// <summary>
        /// Removes station mode, returning companion to normal behavior.
        /// Static (hammer-placed) NPCs cannot be unstated - they have no owner to return to.
        /// </summary>
        public void Unstation()
        {
            if (isStaticPlacement)
            {
                Debug.Log($"[CompanionNpcModule] Cannot unstation {DisplayName} - it was placed via hammer (static NPC)");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    $"{DisplayName} is a placed NPC and cannot be converted to a companion");
                return;
            }

            isStationedAsNpc = false;
            _hasStationedPosition = false;
            allowIdleWandering = false;

            // Remove the stationed state (re-enables movement systems)
            RemoveStationedState();

            SaveToZDO();

            // CRITICAL: Save to vault with IsStationedAsNpc = false so companion can be restored normally
            if (_companion != null)
            {
                _companion.SaveCompanionToVault();
            }

            // ROSTER MIRROR (Phase 3): unstationing returns the companion to
            // a Following persistent intent. Captures a fresh snapshot so
            // the entry's Stationed* fields are cleared in the next read.
            if (_companion != null)
            {
                try
                {
                    var ownerForRoster = _companion.GetOwner();
                    if (ownerForRoster != null)
                        CompanionRosterWriter.OnFollowCommand(ownerForRoster, _companion);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CompanionNpcModule] Roster write for Unstation failed (non-fatal): {ex.Message}");
                }
            }

            if (VerboseLogging)
                Debug.Log($"[CompanionNpcModule] {DisplayName} unstated - returning to companion mode");

            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                $"{DisplayName} is now your companion again");
        }

        /// <summary>
        /// Toggles idle wandering for a stationed NPC.
        /// When enabled, the NPC uses the same logic as a companion commanded to Stay:
        /// - Same idle behaviors (wander, emotes, sitting)
        /// - Same combat (defends itself and the area)
        /// - Same movement within the configured radius
        /// </summary>
        public void ToggleIdleWandering()
        {
            if (!isStationedAsNpc) return;

            allowIdleWandering = !allowIdleWandering;
            
            Debug.Log($"[CompanionNpcModule] ToggleIdleWandering: {DisplayName} -> allowIdleWandering={allowIdleWandering}");

            if (allowIdleWandering)
            {
                // Re-enable AI and physics so the NPC can actually move.
                // StaticNpcInitializer.Awake() disables these to prevent rotation reset.
                var monsterAI = GetComponent<MonsterAI>();
                if (monsterAI != null) monsterAI.enabled = true;

                var body = GetComponent<Rigidbody>();
                if (body != null)
                {
                    // Mirror of the stationed-state freeze (FreezeAll constraints) - clear
                    // constraints to release the lock when the NPC starts wandering again.
                    body.constraints = RigidbodyConstraints.None;
                    FiresCore.Npc.KinematicSync.Set(body, false, "NpcModule unstick");
                    body.useGravity = true;
                }

                // Re-enable ZSyncTransform rotation sync so wandering direction syncs
                var syncTransform = GetComponent<ZSyncTransform>();
                if (syncTransform != null) syncTransform.m_syncRotation = true;

                // CRITICAL: Unlock movement so AI can control movement
                if (_combatMovement != null)
                {
                    _combatMovement.UnlockMovement();
                    _combatMovement.SetHomePosition(_stationedPosition);
                    Debug.Log($"[CompanionNpcModule] Set CombatMovement home position to {_stationedPosition}");
                }

                // Enable idle behavior if present (re-fetch if null - it may have been
                // added after CompanionNpcModule.Awake() ran)
                if (_idleBehavior == null)
                    _idleBehavior = GetComponent<CompanionIdleBehavior>();
                if (_idleBehavior != null)
                {
                    _idleBehavior.enabled = true;
                    _idleBehavior.SetHomePosition(_stationedPosition, EffectiveWanderRadius);
                    Debug.Log($"[CompanionNpcModule] Enabled CompanionIdleBehavior");
                }
                else
                {
                    Debug.LogWarning($"[CompanionNpcModule] No CompanionIdleBehavior found on {DisplayName}");
                }
                
                // Check for territory at stationed position
                _currentTerritoryRadius = FiresCore.Bridge.NpcModeBridge.GetTerritoryRadius(_stationedPosition);
                _currentTerritoryName = FiresCore.Bridge.NpcModeBridge.GetTerritoryName(_stationedPosition);
                float effectiveRadius = EffectiveWanderRadius;
                string wanderInfo = _currentTerritoryRadius.HasValue 
                    ? $"within {_currentTerritoryName}" 
                    : $"within {effectiveRadius}m";

                Debug.Log($"[CompanionNpcModule] Effective wander radius: {effectiveRadius}m, Territory: {_currentTerritoryName ?? "none"}");
                Debug.Log($"[CompanionNpcModule] NPC will now use unified Stay behavior (combat + idle)");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
                    $"{DisplayName} will now wander and defend {wanderInfo}");
            }
            else
            {
                // Stay in place — held by the AI Stay state with NORMAL companion physics (gravity on, no
                // freeze). The idle-wander generator is gated off by allowIdleWandering=false, so the NPC
                // stands on its mark; EnforceWanderBounds walks it back if it gets knocked away.
                var body = GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.constraints = RigidbodyConstraints.FreezeRotation;
                    FiresCore.Npc.KinematicSync.Set(body, false, "NpcModule unstick 2");
                    body.useGravity = true;
                }

                _isForceReturningHome = false;
                _currentTerritoryRadius = null;
                _currentTerritoryName = null;

                ApplyStationedState();   // sets Stay position + home + per-client facing

                Debug.Log($"[CompanionNpcModule] {DisplayName} set to stay in place (AI-held, normal physics)");
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
                    $"{DisplayName} will now stay in place");
            }

            SaveToZDO();
        }

        #endregion

        #region Hammer Placement Mode

        /// <summary>
        /// Initiates hammer placement mode for this companion.
        /// </summary>
        public void StartHammerPlacement()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            StartCoroutine(HammerPlacementCoroutine());
        }

        private System.Collections.IEnumerator HammerPlacementCoroutine()
        {
            var player = Player.m_localPlayer;
            if (player == null) yield break;

            _isInPlacementMode = true;
            float placementRotation = transform.eulerAngles.y;
            const float rotationSpeed = 45f; // Degrees per scroll tick
            const float snapAngle = 22.5f; // For shift-snap

            // Unlock movement during placement
            if (_combatMovement != null)
            {
                _combatMovement.UnlockMovement();
            }

            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                $"Place {DisplayName}\nLMB: Place | RMB: Cancel | Scroll: Rotate | Shift+Scroll: Snap Rotate");

            SetGhostMode(true);

            bool placing = true;
            while (placing)
            {
                // Handle scroll wheel rotation (like hammer)
                float scroll = UnityEngine.Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(scroll) > 0.01f)
                {
                    bool shiftHeld = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);
                    
                    if (shiftHeld)
                    {
                        // Snap rotation (like hammer with shift)
                        float snapDir = scroll > 0 ? 1f : -1f;
                        placementRotation = Mathf.Round((placementRotation + snapDir * snapAngle) / snapAngle) * snapAngle;
                    }
                    else
                    {
                        // Free rotation
                        placementRotation += scroll * rotationSpeed * 10f;
                    }
                    
                    // Normalize rotation
                    while (placementRotation < 0f) placementRotation += 360f;
                    while (placementRotation >= 360f) placementRotation -= 360f;
                }

                // Raycast for placement position
                Ray ray = Camera.main.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0f));
                RaycastHit hit;

                if (Physics.Raycast(ray, out hit, 50f, LayerMask.GetMask("terrain", "Default", "static_solid", "piece")))
                {
                    transform.position = hit.point;
                    transform.rotation = Quaternion.Euler(0f, placementRotation, 0f);
                }

                // Place with left-click or Use key
                if (UnityEngine.Input.GetMouseButtonDown(0) || (!FiresCore.Input.FiresInputBlock.IsTyping() && UnityEngine.Input.GetKeyDown(KeyCode.E)))
                {
                    SetGhostMode(false);
                    StationAtCurrentPosition();
                    placing = false;
                }
                // Cancel with right-click, Escape, or middle mouse
                else if (UnityEngine.Input.GetMouseButtonDown(1) || UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetMouseButtonDown(2))
                {
                    SetGhostMode(false);
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center, "Placement cancelled");
                    placing = false;
                }
                // Reset rotation with R key
                else if ((!FiresCore.Input.FiresInputBlock.IsTyping() && UnityEngine.Input.GetKeyDown(KeyCode.R)))
                {
                    placementRotation = 0f;
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, "Rotation reset");
                }
                // Face player with F key
                else if ((!FiresCore.Input.FiresInputBlock.IsTyping() && UnityEngine.Input.GetKeyDown(KeyCode.F)))
                {
                    Vector3 toPlayer = (player.transform.position - transform.position);
                    toPlayer.y = 0;
                    if (toPlayer.sqrMagnitude > 0.01f)
                    {
                        placementRotation = Quaternion.LookRotation(toPlayer).eulerAngles.y;
                        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, "Facing player");
                    }
                }

                yield return null;
            }

            _isInPlacementMode = false;
        }

        private void SetGhostMode(bool ghost)
        {
            var renderers = GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer == null) continue;

                foreach (var material in renderer.materials)
                {
                    if (material == null) continue;

                    try
                    {
                        // Skip materials that don't have a _Color property (Custom/Player shader, particle shaders, etc.)
                        if (!material.HasProperty("_Color"))
                            continue;

                        if (ghost)
                        {
                            Color color = material.color;
                            color.a = 0.5f;
                            material.color = color;
                            
                            // Only set these properties if they exist
                            if (material.HasProperty("_Mode"))
                                material.SetFloat("_Mode", 3);
                            if (material.HasProperty("_SrcBlend"))
                                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                            if (material.HasProperty("_DstBlend"))
                                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                            if (material.HasProperty("_ZWrite"))
                                material.SetInt("_ZWrite", 0);
                            
                            material.DisableKeyword("_ALPHATEST_ON");
                            material.EnableKeyword("_ALPHABLEND_ON");
                            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                            material.renderQueue = 3000;
                        }
                        else
                        {
                            Color color = material.color;
                            color.a = 1f;
                            material.color = color;
                            
                            // Only set these properties if they exist
                            if (material.HasProperty("_Mode"))
                                material.SetFloat("_Mode", 0);
                            if (material.HasProperty("_SrcBlend"))
                                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                            if (material.HasProperty("_DstBlend"))
                                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                            if (material.HasProperty("_ZWrite"))
                                material.SetInt("_ZWrite", 1);
                            
                            material.DisableKeyword("_ALPHATEST_ON");
                            material.DisableKeyword("_ALPHABLEND_ON");
                            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                            material.renderQueue = -1;
                        }
                    }
                    catch (Exception)
                    {
                        // Silently skip materials that cause errors
                    }
                }
            }
        }

        #endregion

        #region Interactable Interface

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (hold) return false;

            var player = user as Player;
            if (player == null) return false;

            bool isAdmin = ZNet.instance?.IsServer() == true ||
                FiresCore.Bridge.NpcConfigBridge.IsAdmin();
            bool shiftHeld = UnityEngine.Input.GetKey(KeyCode.LeftShift) || UnityEngine.Input.GetKey(KeyCode.RightShift);

            // STATIONED NPC INTERACTIONS
            if (isStationedAsNpc)
            {
                // Admin Shift+E: Open NPC configuration
                if (isAdmin && shiftHeld)
                {
                    OpenNpcConfigPanel();
                    return true;
                }

                // Normal E: NPC interaction (quests, dialogue, etc.)
                if (HasAnyProfile())
                {
                    HandleNpcInteraction(player);
                    return true;
                }

                // No profile configured - show message
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    $"{DisplayName} has nothing to say. (Admin: Shift+E to configure)");
                return true;
            }

            // NOT STATIONED - Let CompanionPatches handle normal companion interaction
            // Return false to fall through to Tameable.Interact patch
            return false;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item)
        {
            return false;
        }

        #endregion

        #region Hoverable Interface

        // Valheim 1.0 added GetHoverOffset to the Hoverable interface; it is ADDED to
        // Player.m_maxInteractDistance, so 0 preserves the previous interact range exactly.
        public float GetHoverOffset() => 0f;

        public string GetHoverText()
        {
            if (FiresCore.Bridge.ModUiRegistry.IsAnyOpen())
                return string.Empty;

            // STATIONED NPC - Show NPC hover text
            if (isStationedAsNpc)
            {
                return BuildStationedNpcHoverText();
            }

            // NOT STATIONED - Return empty to let CompanionPatches handle it
            return string.Empty;
        }

        public string GetHoverName()
        {
            if (FiresCore.Bridge.ModUiRegistry.IsAnyOpen())
                return string.Empty;

            // Only return name if stationed, otherwise let CompanionPatches handle it
            if (isStationedAsNpc)
                return DisplayName;

            return string.Empty;
        }

        private string BuildStationedNpcHoverText()
        {
            string name = DisplayName;
            bool isAdmin = ZNet.instance?.IsServer() == true ||
                FiresCore.Bridge.NpcConfigBridge.IsAdmin();

            string text = $"<color=yellow><b>{name}</b></color> <color=#00FFFF>(NPC)</color>";

            // Show interaction hint based on NPC type
            if (HasAnyProfile())
            {
                string interactionHint = npcType switch
                {
                    NpcType.QuestNpc => "Quests",
                    NpcType.InfoNpc => "Info",
                    NpcType.DialogueNpc => "Talk",
                    NpcType.Trader => "Trade",
                    _ => "Interact"
                };
                text += $"\n[<color=yellow><b>$KEY_Use</b></color>] {interactionHint}";
            }
            else
            {
                text += "\n<color=#808080>(No profile configured)</color>";
            }

            // Admin options
            if (isAdmin)
            {
                text += "\n[<color=#00FFFF><b>L.Shift + $KEY_Use</b></color>] Configure NPC";

                // Show idle wandering status with territory info
                if (allowIdleWandering)
                {
                    float displayRadius = EffectiveWanderRadius;
                    if (useTerritoryBounds && _currentTerritoryRadius.HasValue)
                        text += $"\n<color=#00FF00>Idle wandering: ON (in {_currentTerritoryName})</color>";
                    else
                        text += $"\n<color=#00FF00>Idle wandering: ON ({displayRadius:F0}m radius)</color>";
                }
                else
                    text += "\n<color=#808080>Idle wandering: OFF</color>";
            }

            return Localization.instance.Localize(text);
        }

        #endregion

        #region NPC Interaction

        private void OpenNpcConfigPanel()
        {
            // Integrated mode only: the host's NPC framework opens its admin config panel
            // (it builds/syncs its own NpcController from this object). No-op standalone.
            FiresCore.Bridge.NpcModeBridge.RaiseOpenConfigPanel(gameObject);
        }

        private void HandleNpcInteraction(Player player)
        {
            // Integrated mode only: the host's NPC framework routes quest/info/dialogue/trader
            // interaction by npcType. Standalone has no such UI, so this is a no-op there.
            FiresCore.Bridge.NpcModeBridge.RaiseInteraction(gameObject, player);
        }

        private bool HasAnyProfile()
        {
            return !string.IsNullOrEmpty(questProfile) ||
                   !string.IsNullOrEmpty(infoProfile) ||
                   !string.IsNullOrEmpty(dialogueProfile) ||
                   !string.IsNullOrEmpty(traderProfile);
        }

        #endregion

        #region ZDO Persistence

        public void LoadFromZDO()
        {
            if (_nview == null || !_nview.IsValid()) return;

            var zdo = _nview.GetZDO();
            if (zdo == null) return;

            isStationedAsNpc = zdo.GetBool("npc_stationed", false);
            isStaticPlacement = zdo.GetBool("npc_static_placement", false);
            _loadedAt = Time.time;   // the ai-guard's pre-stage window starts here
            allowIdleWandering = zdo.GetBool("npc_allow_idle_wander", false);
            suppressFacePlayer = zdo.GetBool("npc_no_face_player", false);
            allowNpcInteractionsWhileFollowing = zdo.GetBool("npc_allow_while_following", false);
            npcDisplayName = zdo.GetString("npc_display_name", "");
            // Unset = None (decorative) — only the book's role assignment ever writes npc_type.
            npcType = (NpcType)zdo.GetInt("npc_type", (int)NpcType.None);
            dialogueProfile = zdo.GetString("npc_dialogue_profile", "");
            questProfile = zdo.GetString("npc_quest_profile", "");
            infoProfile = zdo.GetString("npc_info_profile", "");
            traderProfile = zdo.GetString("npc_trader_profile", "");
            idleWanderRadius = zdo.GetFloat("npc_idle_wander_radius", 20f);
            wanderBufferZone = zdo.GetFloat("npc_wander_buffer_zone", 5f);
            
            // Migration: if old companions were saved with the old 5m default, upgrade them
            if (idleWanderRadius <= 5f)
            {
                idleWanderRadius = 20f;
                Debug.Log($"[CompanionNpcModule] Migrated {DisplayName ?? gameObject.name} wander radius from 5m to 20m");
            }
            useTerritoryBounds = zdo.GetBool("npc_use_territory_bounds", true);
            greetAnimation = zdo.GetString("npc_greet_animation", "");
            byeAnimation = zdo.GetString("npc_bye_animation", "");
            // Migrate the old hard-coded "emote_wave" default (never a deliberate choice) to none, so NPCs
            // placed before this change also stop auto-waving on approach.
            if (greetAnimation == "emote_wave") greetAnimation = "";
            if (byeAnimation == "emote_wave") byeAnimation = "";

            _hasStationedPosition = zdo.GetBool("npc_has_stationed_pos", false);
            if (_hasStationedPosition)
            {
                _stationedPosition = zdo.GetVec3("npc_stationed_pos", transform.position);
                _stationedRotation = zdo.GetQuaternion("npc_stationed_rot", transform.rotation);
            }

            if (VerboseLogging)
                Debug.Log($"[CompanionNpcModule] Loaded from ZDO - Stationed: {isStationedAsNpc}, IdleWander: {allowIdleWandering}, Type: {npcType}");
        }

        public void SaveToZDO()
        {
            // Reference parent namespace fully - CompanionPatches is in FiresCore.Npc
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;
            if (_nview == null || !_nview.IsValid()) return;

            var zdo = _nview.GetZDO();
            if (zdo == null) return;

            zdo.Set("npc_stationed", isStationedAsNpc);
            zdo.Set("npc_static_placement", isStaticPlacement);
            zdo.Set("npc_allow_idle_wander", allowIdleWandering);
            zdo.Set("npc_allow_while_following", allowNpcInteractionsWhileFollowing);
            zdo.Set("npc_display_name", npcDisplayName ?? "");
            zdo.Set("npc_type", (int)npcType);
            zdo.Set("npc_dialogue_profile", dialogueProfile ?? "");
            zdo.Set("npc_quest_profile", questProfile ?? "");
            zdo.Set("npc_info_profile", infoProfile ?? "");
            zdo.Set("npc_trader_profile", traderProfile ?? "");
            zdo.Set("npc_idle_wander_radius", idleWanderRadius);
            zdo.Set("npc_wander_buffer_zone", wanderBufferZone);
            zdo.Set("npc_use_territory_bounds", useTerritoryBounds);
            zdo.Set("npc_greet_animation", greetAnimation ?? "");
            zdo.Set("npc_bye_animation", byeAnimation ?? "");

            zdo.Set("npc_has_stationed_pos", _hasStationedPosition);
            if (_hasStationedPosition)
            {
                zdo.Set("npc_stationed_pos", _stationedPosition);
                zdo.Set("npc_stationed_rot", _stationedRotation);
            }

            if (VerboseLogging)
                Debug.Log($"[CompanionNpcModule] Saved to ZDO - Stationed: {isStationedAsNpc}, IdleWander: {allowIdleWandering}, Type: {npcType}");
        }

        #endregion

        #region Public API

        /// <summary>
        /// Sets the NPC profile for the specified type.
        /// </summary>
        public void SetProfile(NpcType type, string profile)
        {
            npcType = type;
            switch (type)
            {
                case NpcType.QuestNpc:
                    questProfile = profile;
                    break;
                case NpcType.InfoNpc:
                    infoProfile = profile;
                    break;
                case NpcType.DialogueNpc:
                    dialogueProfile = profile;
                    break;
                case NpcType.Trader:
                    traderProfile = profile;
                    break;
            }
            SaveToZDO();
        }

        /// <summary>
        /// Gets the current profile for the current NPC type.
        /// </summary>
        public string GetCurrentProfile()
        {
            return npcType switch
            {
                NpcType.QuestNpc => questProfile,
                NpcType.InfoNpc => infoProfile,
                NpcType.DialogueNpc => dialogueProfile,
                NpcType.Trader => traderProfile,
                _ => ""
            };
        }

        #endregion
    }
}
