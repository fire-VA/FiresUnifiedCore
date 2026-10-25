using UnityEngine;
using System;
using System.Collections.Generic;
using FiresCore.Npc.Combat;
using FiresCore.Npc.Movement;
using FiresCore.Npc.NpcMode;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Custom AI for companions that replaces MonsterAI.
    /// Extends BaseAI to get pathfinding, detection, and movement utilities,
    /// but implements our own state machine and behavior logic.
    /// 
    /// PARTIAL CLASS STRUCTURE:
    /// - CompanionAI.cs - Core fields, settings, initialization, Unity lifecycle, state machine
    /// - CompanionAI.Targeting.cs - Target detection, scoring, and switching
    /// - CompanionAI.Combat.cs - Combat state updates, fleeing, kiting
    /// - CompanionAI.Idle.cs - Idle state, wander logic
    /// - CompanionAI.Pathfinding.cs - Movement, pathfinding, stuck detection
    /// </summary>
    public partial class CompanionAI : BaseAI
    {
        #region State Machine

        public enum AIState
        {
            Idle,
            Following,
            Combat,
            Returning,
            Fleeing
        }

        [Header("Current State")]
        [SerializeField] private AIState _currentState = AIState.Idle;
        public AIState CurrentState => _currentState;
        
        private AIState _previousState;
        private float _stateStartTime;
        private float _lastStateChangeTime;

        #endregion

        #region Configuration

        [Header("Follow Settings - Buffer Zones")]
        // Distance bands that drive DetermineFollowSpeed. Base gaits are walk=2, jog(m_speed)=4, run=7.
        // A following owner normally moves at RUN pace, so the companion MUST reach the run tier while
        // only a few metres back or it cruises in the jog tier (4) behind a 7-speed owner and drifts
        // ever further away — the "never runs / always slow-walking, never catches up" report. Keep the
        // run threshold at a tight follow distance so it runs the moment it trails. These are overridden
        // per-spawn in CompanionController.ConfigureAI / CompanionPrefabManager — keep all three in sync.
        public float stopDistanceInner = 2f;
        public float stopDistanceOuter = 4f;
        public float walkDistanceOuter = 5f;
        public float runDistanceInner = 4f;
        public float runDistanceOuter = 6f;
        public float catchUpDistance = 12f;
        public float playerIdleThreshold = 8f;   // was 3 s, which made idle flap

        // Run-tier catch-up boosts (multipliers on the archetype run speed, applied by CompanionSpeedRamp).
        // An owner following at RUN pace moves as fast as the companion's own run speed, so a plain run
        // only HOLDS the gap - it never closes. A small Running boost lets the follower gently tuck back in;
        // the Sprinting (emergency, > catchUpDistance) boost closes a large gap fast even vs a buffed owner.
        // This is what makes "run to us when far, regardless of our current speed" actually reachable.
        private const float RunCatchUpBoost = 1.15f;
        private const float SprintCatchUpBoost = 1.5f;

        // Owner-speed -> matched-gait thresholds (m/s). Once tucked into the trail band the companion mirrors how
        // fast the owner is ACTUALLY moving (pace matching) instead of always running at range. Measured owner
        // speed is the source of truth (the IsRunning flag reads stale), with stance flags layered for crouch/walk.
        private const float OwnerStandingSpeed = 0.5f;   // below this the owner is standing -> companion stops
        private const float OwnerWalkSpeed     = 2.6f;   // walk / crouch-walk pace -> companion walks
        private const float OwnerJogSpeed      = 4.6f;   // medium pace -> companion jogs; at/above -> runs
        
        internal enum FollowSpeed { Stopped, Sneaking, Walking, Jogging, Running, Sprinting }
        private FollowSpeed _currentFollowSpeed = FollowSpeed.Stopped;
        private float _lastFollowDiag; // throttle for the always-on [CompanionFollowDiag] line
        
        // Owner stance matching - companion mirrors the player's movement stance when following
        private bool _isOwnerSneaking = false;
        private bool _isOwnerWalking = false;
        private bool _isOwnerRunning = false;
        private bool _isCompanionSneaking = false;
        private bool _isOwnerProne = false;
        private float _ownerProneSpeed;
        private Movement.CompanionStance _stance;
        private float _lastOwnerStanceCheck = -10f;
        private float _ownerSpeed = 0f;      // owner horizontal speed (m/s), sampled in UpdateOwnerStance -> pace matching
        private bool _isClosingGap = false;  // hysteresis: true while catching up to the trail band, until tucked in
        private const float OwnerStanceCheckInterval = 0.2f;

        [Header("Combat Settings")]
        // Detection radius for the threat scan loop. Lowered from 20 to 15 m
        // because the owner-defense gate in CompanionAI.Targeting now decides
        // which detected threats are actually engaged (see
        // OwnerDefenseRadius); a wider scan radius is just perf overhead
        // for threats we'd ignore anyway.
        public float aggroRange = 15f;
        public float attackRange = 2.5f;
        public float maxChaseDistance = 25f;
        public float combatLeashDistance = 10f;
        public float targetSwitchCooldown = 3f;
        public float giveUpTime = 10f;

        [Header("Protection Settings")]
        public float ownerProtectionRange = 15f;
        public float interceptPriority = 3f;
        public bool proactiveProtection = true;

        [Header("Self-Preservation")]
        public float fleeHealthPercent = 0.2f;
        public float fleeTime = 8f;
        public float healReturnPercent = 0.5f;

        [Header("Idle Settings")]
        public float idleWanderRadius = 5f;
        public float idleWanderInterval = 10f;

        #endregion

        #region References

        private CompanionController _companion;
        private CompanionStateController _stateController;
        private Humanoid _humanoid;
        private ZSyncAnimation _zanim;
        
        // Combat
        private Character _targetCreature;
        private Vector3 _lastKnownTargetPos;
        private float _lastTargetScanTime;
        private float _lastTargetSwitchTime;
        private float _timeSinceTargetSeen;
        private float _timeSinceAttacking;
        private bool _beenAtLastTargetPos;

        // After a long-distance teleport, target detection pauses until this time so the companion regroups with the
        // player instead of picking a fight on arrival. Movement authority is unaffected.
        private float _combatSuppressedUntilTime = 0f;
        private const float PostTeleportCombatSuppressSeconds = 3.0f;
        
        // Weapon type caching
        private bool _isRangedWeapon = false;
        private CompanionCombat _combatRef;
        
        // Threat analysis
        private ThreatAnalyzer _threatAnalyzer;
        
        // Terrain awareness
        private TerrainAwareness _terrainAwareness;
        
        // CACHED COMPONENT REFERENCES - avoid GetComponent every frame
        // These are looked up once in Awake/OnEnable and reused in UpdateAI, Combat, Targeting, etc.
        private CompanionIdleBehavior _idleBehavior;
        private CompanionCombatMovement _combatMovement;
        private CompanionNpcModule _npcModule;
        private StaminaManager _staminaManager;
        private Archetypes.ArchetypeController _archetypeController;
        private EnemyAttackRecognition _attackRecognition;
        private WeaponSwapManager _weaponSwapManager;
        private Interactions.CompanionInteractionBehavior _interactionBehavior;
        private Rigidbody _rigidbody;

        // Following
        private GameObject _followTarget;
        private Player _ownerPlayer;
        private Vector3 _stayPosition;
        private bool _shouldFollow = false;
        private bool _hasHomePositionSet = false;
        
        // Stay mode aggro control
        private float _lastDirectlyDamagedTime = -100f;
        private const float StayModeAggroRange = 5f;
        private const float DirectDamageAlertDuration = 15f;

        // In Follow mode an aggressive enemy is only engaged within this distance of the owner, unless it is targeting
        // the owner, a player or this companion, or the companion was hit recently.
        private const float OwnerDefenseRadius = 8f;

        // Timers
        private float _lastIdleWanderTime;
        private float _fleeStartTime;

        // Idle destination
        private Vector3 _idleDestination;
        private bool _hasIdleDestination = false;
        
        // Player idle tracking
        private Vector3 _lastOwnerPosition;
        private float _ownerIdleStartTime;
        private bool _isOwnerIdle = false;
        private float _lastOwnerIdleCheck;

        // Cached for performance
        private const float TargetScanInterval = 0.25f;
        private const float PositionReachedThreshold = 1.5f;
        private static readonly List<Character> _tempCharacterList = new List<Character>();
        
        // STATE OSCILLATION PREVENTION
        private const float MinCombatStateTime = 1.0f;
        private const float MinReturningStateTime = 0.5f;
        
        // Pathfinding
        private Vector3 _lastPathfindingPos;
        private float _lastPathfindingTime;
        private float _lastPathRecalcTime;
        private int _consecutiveStuckFrames;
        private int _pathfindingAttempts;
        private Vector3 _currentMoveTarget;
        private float _lastProgressTime;
        private float _lastDistanceToTarget = float.MaxValue;
        
        private const float PathRecalcInterval = 0.5f;
        private const float StuckCheckInterval = 1.0f;
        private const float StuckMovementThreshold = 0.5f;
        private const int StuckFramesBeforeRecalc = 3;
        private const float ProgressTimeout = 5.0f;
        private const float DirectMoveDistance = 3f;
        private const float WaypointSearchRadius = 5f;
        private const int MaxPathfindingAttempts = 5;

        #endregion

        #region Debugging

        public static bool VerboseLogging = false;
        public static bool StateTransitionLogging = false;
        public static bool ArchetypeCombatLogging = true;
        
        private float _lastStateLogTime = -100f;
        private const float StateLogInterval = 5f;
        private AIState _lastLoggedState = AIState.Idle;
        private bool _lastLoggedCriticalRecovery = false;
        private bool _lastLoggedRecovery = false;
        private float _lastArchetypeLogTime = -100f;
        private const float ArchetypeLogInterval = 10f;

        #endregion

        #region Unity Lifecycle

        public override void Awake()
        {
            base.Awake();

            _companion = GetComponent<CompanionController>();
            _stateController = GetComponent<CompanionStateController>();
            _humanoid = GetComponent<Humanoid>();
            _zanim = GetComponent<ZSyncAnimation>();
            _stance = GetComponent<Movement.CompanionStance>();
            _combatRef = GetComponent<CompanionCombat>();
            _threatAnalyzer = GetComponent<ThreatAnalyzer>();
            _terrainAwareness = GetComponent<TerrainAwareness>();
            
            // Cache frequently-used components to avoid per-frame GetComponent calls
            _idleBehavior = GetComponent<CompanionIdleBehavior>();
            _combatMovement = GetComponent<CompanionCombatMovement>();
            _npcModule = GetComponent<CompanionNpcModule>();
            _staminaManager = GetComponent<StaminaManager>();
            _archetypeController = GetComponent<Archetypes.ArchetypeController>();
            _attackRecognition = GetComponent<EnemyAttackRecognition>();
            _weaponSwapManager = GetComponent<WeaponSwapManager>();
            _interactionBehavior = GetComponent<Interactions.CompanionInteractionBehavior>();
            _rigidbody = GetComponent<Rigidbody>();
            
            _stayPosition = transform.position;
        }

        public override void OnEnable()
        {
            base.OnEnable();

            m_avoidWater = false;
            m_pathAgentType = Pathfinding.AgentType.Humanoid;
            
            _lastPathfindingPos = transform.position;
            _lastPathfindingTime = Time.time;
            _lastPathRecalcTime = 0f;
            _consecutiveStuckFrames = 0;
            _pathfindingAttempts = 0;
            _currentMoveTarget = Vector3.zero;
            _lastProgressTime = Time.time;
            _lastDistanceToTarget = float.MaxValue;
            
            SetState(AIState.Idle);
        }

        #endregion

        #region BaseAI Overrides

        // A follower could stop while its owner ran away. While it should follow,
        // its owner is over 6 m away and it is not moving, say once per
        // change (and every 5 s) WHAT holds it - an early gate in UpdateAI, or the state / command it is in. Diagnostic, Info level.
        private string _holdWhy; private float _holdAt;
        private bool FollowingButStill()
        {
            if (!_shouldFollow || _followTarget == null || m_character == null) return false;
            if (Vector3.Distance(transform.position, _followTarget.transform.position) <= 6f) return false;
            return m_character.GetVelocity().magnitude < 0.3f;
        }
        private bool Held(string why)
        {
            if (FollowingButStill() && (why != _holdWhy || Time.time >= _holdAt))
            {
                _holdWhy = why; _holdAt = Time.time + 5f;
                FiresCore.Logging.FiresLogger.LogInfo($"[FollowHold] {m_character?.m_name}: owner {Vector3.Distance(transform.position, _followTarget.transform.position):0.0} m away, not moving - held by {why}");
            }
            return true;
        }
        private void LogHoldState()
        {
            if (!FollowingButStill()) return;
            string why = $"state {_currentState}, target {(_targetCreature != null ? _targetCreature.m_name : "none")}, priority command {(_stateController != null && _stateController.HasAbsolutePriorityCommand)}, combat-move priority {(_combatMovement != null && _combatMovement.HasCommandPriority)}";
            // In Following, what the mover has - speed tier, path points, the movement authority holder, the animator in locomotion
            if (_currentState == AIState.Following)
            {
                var auth = _companion != null ? _companion.GetMovementAuthority() : null;
                why += $"; follow speed {_currentFollowSpeed}, path {(m_path != null ? m_path.Count : -1)} pt(s), authority {(auth == null ? "none" : auth.HasAuthority(AIAuthorityOwner) ? "ours" : auth.CanAcquireAuthority(GetAIMovementSource()) ? "free" : "held by another")}, in locomotion {(_stateController != null && _stateController.IsAnimatorInLocomotion())}";
            }
            Held(why);
        }
        // Combat, Returning and Fleeing could hold followers
        // while the owner ran off. Owner "far" = over 20 m, or over 12 m and moving away. Then follow wins, except while the foe
        // is hitting the owner (defending the owner beats the leash). Re-engaging waits until within 8 m.
        private float _skipSince = -1f, _recallUntil, _recallLogAt;
        private Character _recalledFoe; private float _recalledFoeUntil, _reengageLogAt;
        private const float RecalledFoeIgnoreSeconds = 20f;   // the foe a recall dropped is left alone this long
        private const float ReengageLeash = 12f;              // a following companion only picks a new foe this close to its owner (recall is at 20 m)

        /// <summary>True while a player is interacting with this companion (its menu / inventory open, or the local player hovering it):
        /// it holds still, no combat or recall movement (Fire 10-23: "hard to even interact with them").</summary>
        private bool BeingInteracted()
        {
            if (_stateController != null && _stateController.CurrentState == CompanionStateController.CompanionState.UIInteraction) return true;
            var lp = Player.m_localPlayer;
            var hover = lp != null ? lp.GetHoverObject() : null;
            return hover != null && m_character != null && hover.GetComponentInParent<Character>() == m_character;
        }

        /// <summary>Why a following companion must not take this foe now, or null. The foe attacking the owner or this companion always passes.</summary>
        private string ReengageBlock(Character target)
        {
            if (target == null || !_shouldFollow || _followTarget == null) return null;
            bool attacksUs = (_npcModule != null && _npcModule.WasDirectlyAttacked && _npcModule.LastAttacker == target) || FoeHitsOwner(target);
            if (attacksUs) return null;
            if (target == _recalledFoe && Time.time < _recalledFoeUntil) return $"dropped by a follow recall {RecalledFoeIgnoreSeconds - (_recalledFoeUntil - Time.time):0} s ago";
            float fromOwner = Vector3.Distance(target.transform.position, _followTarget.transform.position);
            if (fromOwner > ReengageLeash) return $"{fromOwner:0} m from the owner (re-engage leash {ReengageLeash:0} m)";
            return null;
        }
        internal bool FollowRecallActive => Time.time < _recallUntil && OwnerDistance() > 8f;
        private float OwnerDistance() => _followTarget != null ? Vector3.Distance(transform.position, _followTarget.transform.position) : 0f;
        private bool OwnerFar()
        {
            if (!_shouldFollow || _followTarget == null) return false;
            float dist = OwnerDistance();
            if (dist > 20f) return true;
            if (dist <= 12f) return false;
            var oc = _followTarget.GetComponent<Character>();
            if (oc == null) return false;
            Vector3 away = _followTarget.transform.position - transform.position; away.y = 0f;
            return Vector3.Dot(oc.GetVelocity(), away.normalized) > 1f;
        }
        private bool FoeHitsOwner(Character foe)
        {
            if (foe == null || _followTarget == null) return false;
            var oc = _followTarget.GetComponent<Character>();
            var ai = foe.GetBaseAI() as MonsterAI;
            return oc != null && ai != null && ai.GetTargetCreature() == oc
                && Vector3.Distance(foe.transform.position, _followTarget.transform.position) < 6f;
        }
        private void RecallToFarOwner()
        {
            if (BeingInteracted()) return;
            if (_currentState != AIState.Combat && _currentState != AIState.Fleeing && _currentState != AIState.Returning) return;
            if (!OwnerFar() || FoeHitsOwner(_targetCreature)) return;
            var was = _currentState; string foe = _targetCreature != null ? _targetCreature.m_name : "none";
            // Fire 10-23: recall at 20 m, then the same boar was re-acquired as soon as the owner was back in range = a ping-pong
            // every few seconds. The foe a recall dropped is ignored for a while (unless it attacks the owner or this companion).
            if (_targetCreature != null) { _recalledFoe = _targetCreature; _recalledFoeUntil = Time.time + RecalledFoeIgnoreSeconds; }
            ClearTarget();
            SetState(AIState.Following);
            _recallUntil = Time.time + 6f;
            if (Time.time >= _recallLogAt)
            {
                _recallLogAt = Time.time + 2f;
                FiresCore.Logging.FiresLogger.LogInfo($"[FollowRecall] {m_character?.m_name}: owner {OwnerDistance():0.0} m away, left {was} (target {foe}) to follow");
            }
        }
        private string SkipDetail() => _stateController == null ? "" :
            $"state {_stateController.CurrentState}, anim {_stateController.CurrentAnimationState} set by {_stateController.AnimStateSetBy} {Time.time - _stateController.AnimStateSince:0.0} s ago";
        private bool UnstickForFarOwner()
        {
            if (!OwnerFar()) { _skipSince = -1f; return false; }
            if (_skipSince < 0f) _skipSince = Time.time;
            var sc = _stateController;
            // An emote never holds a companion whose owner is leaving; other freezes wait 1.5 s
            if (sc.CurrentState != CompanionStateController.CompanionState.Emote && Time.time - _skipSince < 1.5f) return false;
            // only stale animation / emote freezes; a real chair sit or UI interaction is left alone
            if (sc.CurrentState == CompanionStateController.CompanionState.ChairSit || sc.CurrentState == CompanionStateController.CompanionState.UIInteraction) return false;
            FiresCore.Logging.FiresLogger.LogInfo($"[FollowUnstick] {m_character?.m_name}: owner {OwnerDistance():0.0} m away, AI skipped {Time.time - _skipSince:0.0} s - {SkipDetail()}; cleared");
            if (sc.CurrentState == CompanionStateController.CompanionState.Emote) sc.ForceStopEmote();
            sc.ResetAnimationState();
            _skipSince = -1f;
            return !sc.ShouldSkipAIUpdate;
        }
        // A companion on its own body kept the prefab's animator culling, so on the peer that runs its AI but
        // does not render it (headless peer, or behind / far from the camera) the attack end event was skipped, InAttack() stayed true and
        // the companion froze). Every animator of a companion THIS peer owns animates always; when the
        // ownership moves away its prefab mode comes back. Checked once a second (a swap adds animators; they are picked up).
        private readonly Dictionary<Animator, AnimatorCullingMode> _cullOriginal = new Dictionary<Animator, AnimatorCullingMode>();
        private float _cullCheckAt;
        private bool _cullOwned, _cullLogged;
        private void LateUpdate()
        {
            if (Time.time < _cullCheckAt) return;
            _cullCheckAt = Time.time + 1f;
            var nv = m_nview;
            bool owned = nv != null && nv.IsValid() && nv.IsOwner();
            if (owned)
            {
                int set = 0;
                foreach (var a in GetComponentsInChildren<Animator>(true))
                {
                    if (a == null || a.cullingMode == AnimatorCullingMode.AlwaysAnimate) continue;
                    if (!_cullOriginal.ContainsKey(a)) _cullOriginal[a] = a.cullingMode;
                    a.cullingMode = AnimatorCullingMode.AlwaysAnimate; set++;
                }
                if (set > 0 && !_cullLogged) { _cullLogged = true; FiresCore.Logging.FiresLogger.LogInfo($"[CompanionAnim] {m_character?.m_name}: owned here - {set} animator(s) set to AlwaysAnimate"); }
            }
            else if (_cullOwned && _cullOriginal.Count > 0)
            {
                foreach (var kv in _cullOriginal) if (kv.Key != null) kv.Key.cullingMode = kv.Value;
                _cullOriginal.Clear(); _cullLogged = false;
            }
            _cullOwned = owned;
        }

        public override bool UpdateAI(float dt)
        {
            // Guard: MonoUpdaters can call UpdateAI on a component whose GameObject has already
            // been destroyed (stale reference in the update list). GetComponent<T>() on a
            // destroyed Component throws a native NullReferenceException, so bail out first.
            if (this == null || !gameObject) return false;

            // CompanionCombatMovement adds the StaminaManager after this Awake cached null, so every stamina check here was
            // skipped, so companions drained themselves, then couldn't follow or
            // attack). Picked up once it exists.
            if (_staminaManager == null) _staminaManager = GetComponent<StaminaManager>();

            // CRITICAL: Check stationed state BEFORE base.UpdateAI runs.
            // BaseAI.UpdateAI resets m_lookDir every tick via UpdateTarget/UpdateRotation,
            // which overrides any look direction we set (e.g., face-nearest-player).
            // For stationed non-wandering NPCs, skip the base AI entirely.
            if (_npcModule == null)
                _npcModule = GetComponent<CompanionNpcModule>();

            // SPAWN-WINDOW GUARD ONLY. Until StaticNpcInitializer finishes setting up a placed NPC (stationed
            // position + facing applied ~1s after spawn), skip the AI so base.UpdateAI can't bake a north-facing
            // into the ZDO. After setup, a stationed NPC runs the SAME state machine as a companion: its idle is
            // delegated to its patrol/idle sub-behavior (single movement writer preserved) and its combat runs
            // the full path (see the stationed self-defense dispatch below). Wandering stationed NPCs
            // (AllowsIdleBehaviors=true) already ran the FSM and are unaffected.
            if (_companion != null && _companion.isStaticPlacement
                && (_npcModule == null || !_npcModule.IsStationedWithPosition))
            {
                return Held("static spawn window");
            }

            // Skip base.UpdateAI entirely when the companion is physically attached (chair sit,
            // saddle, etc.) or the interaction behavior is attached. BaseAI.UpdateAI calls
            // m_character.SetMoveDir / ApplyMovement which tries to assign velocity to the
            // kinematic rigidbody that Character.AttachStart sets up, producing a flood of
            // "Setting linear/angular velocity of a kinematic body" warnings every physics tick.
            if (_idleBehavior == null)
                _idleBehavior = GetComponent<CompanionIdleBehavior>();
            if (_interactionBehavior == null)
                _interactionBehavior = GetComponent<Interactions.CompanionInteractionBehavior>();

            bool isPhysicallyAttached = (_idleBehavior != null && _idleBehavior.IsSitting)
                                     || (_interactionBehavior != null && _interactionBehavior.IsAttached)
                                     || (m_character != null && m_character.IsAttached());
            if (isPhysicallyAttached)
                return Held("physically attached (sit / saddle / interaction)");

            if (!base.UpdateAI(dt))
                return false;

            if (_stateController == null)
                _stateController = GetComponent<CompanionStateController>();

            if (_stateController != null) _stateController.ReleaseAttackEarly = OwnerFar();
            if (_stateController != null && _stateController.ShouldSkipAIUpdate)
            {
                if (UnstickForFarOwner()) { /* cleared: run this frame */ }
                else return Held("state controller: ShouldSkipAIUpdate (" + SkipDetail() + ")");
            }
            else _skipSince = -1f;

            // _idleBehavior already resolved above

            if (_idleBehavior != null && _idleBehavior.IsPlayerInteracting)
            {
                return Held("a player is interacting with it");
            }

            if (_idleBehavior != null && _idleBehavior.IsEmoteFrozen)
            {
                return Held("emote frozen");
            }

            // STATIONED SELF-DEFENSE. A stationed NPC (patrol/decorative) holds its sub-behavior alive via
            // command priority, which makes the normal target-scan + combat path bail. Run a dedicated,
            // post-bounded combat pass here so it defends its post, pursues threats, and only hands back to the
            // sub-behavior once the area is clear (+ a buffer). Gated on stationed → companions are untouched.
            if (_npcModule != null && _npcModule.IsStationedWithPosition && !_npcModule.AllowsIdleBehaviors)
            {
                UpdateStationedCombat(dt);
                if (_currentState == AIState.Combat)
                    return true;   // combat owns movement this frame; PatrolBehavior self-holds while IsInCombat
                // not engaged → fall through: the sub-behavior (patrol) drives, or a decorative NPC holds in idle
            }

            // CRITICAL FIX (Bug #12): Skip AI movement when a sub-behavior is active
            // Sub-behaviors have their own movement logic and authority - CompanionAI should NOT
            // interfere by trying to follow or wander while a sub-behavior is running.
            if (_idleBehavior != null && _idleBehavior.IsInSubBehavior)
            {
                // Sub-behavior is handling all movement - CompanionAI should do nothing
                // Just let the sub-behavior run via its Update() method
                return Held("idle sub-behavior running (" + _idleBehavior.GetType().Name + ")");
            }

            // _interactionBehavior already resolved above; re-check attached state post-base
            if (_interactionBehavior != null && _interactionBehavior.IsAttached)
            {
                return Held("interaction attached");
            }
            
            RecallToFarOwner();   // follow beats Combat / Fleeing / Returning while the owner runs off (not while a foe hits the owner)
            LogHoldState();   // past every early gate, still not moving while it should follow: the state / command holds it
            // NOTE: Out-of-range teleporting is handled exclusively by
            // CompanionController.CheckFollowTeleport(), which runs every second with proper
            // throttling, loop detection, velocity zeroing, ZDO sync, and RPC broadcast.
            // A duplicate teleport here (at 50 Hz, no cooldown, no network sync) was causing
            // 6000-velocity rubber-banding and preventing the controller path from ever firing.
            
            if (_shouldFollow && (_followTarget == null || !_followTarget.activeInHierarchy))
            {
                var owner = _companion?.GetOwner();
                if (owner != null && owner.gameObject != null && owner.gameObject.activeInHierarchy)
                {
                    _followTarget = owner.gameObject;
                    if (VerboseLogging)
                        Debug.Log($"[CompanionAI] {m_character?.m_name} reconnected to owner {owner.GetPlayerName()}");
                }
            }

            if (_npcModule == null)
                _npcModule = GetComponent<CompanionNpcModule>();

            // Wandering stationed NPCs: set up home position for idle behaviors
            if (_npcModule != null && _npcModule.IsStationedAsNpc)
            {
                if (!_hasHomePositionSet)
                {
                    _stayPosition = _npcModule.StationedPosition;
                    _shouldFollow = false;
                    _followTarget = null;
                    _hasHomePositionSet = true;
                    idleWanderRadius = _npcModule.EffectiveWanderRadius;
                    
                    if (VerboseLogging)
                        Debug.Log($"[CompanionAI] Stationed NPC {m_character?.m_name} configured for idle wandering at {_stayPosition}, radius={idleWanderRadius}m");
                }
            }

            if (_combatMovement == null)
                _combatMovement = GetComponent<CompanionCombatMovement>();
            if (_combatMovement != null && _combatMovement.HasCommandPriority)
            {
                if (!_wasInCommandPriority)
                {
                    _wasInCommandPriority = true;
                    if (VerboseLogging)
                    {
                        Debug.Log($"[CompanionAI] {m_character?.m_name} entered command priority: " +
                            $"hasDestination={_hasCommandDestination}, " +
                            $"hasPriorityTarget={_combatMovement.HasPriorityTarget}, " +
                            $"isInSubBehavior={_idleBehavior?.IsInSubBehavior ?? false}");
                    }
                }
                
                if (_hasCommandDestination)
                {
                    float distToDest = Vector3.Distance(transform.position, _commandDestination);
                    bool shouldWalk = _combatMovement.ShouldWalkForCommand;
                    
                    if (VerboseLogging)
                        Debug.Log($"[CompanionAI] {m_character?.m_name} executing command movement to {_commandDestination} (dist: {distToDest:F1}m, walk: {shouldWalk})");
                    
                    // CRITICAL: Use MoveToThroughAuthority instead of BaseAI.MoveTo()
                    // BaseAI.MoveTo() calls SetMoveDir() directly, bypassing our authority system
                    // and getting blocked by the Harmony patch
                    if (MoveToThroughAuthority(_commandDestination, run: !shouldWalk))
                    {
                        if (!_loggedReachedDestination)
                        {
                            _loggedReachedDestination = true;
                            
                            if (VerboseLogging && Time.time - _lastDestinationReachedLogTime >= DestinationReachedLogInterval)
                            {
                                _lastDestinationReachedLogTime = Time.time;
                                Debug.Log($"[CompanionAI] {m_character?.m_name} reached command destination");
                            }
                        }
                        
                        _hasCommandDestination = false;
                        _combatMovement.ClearMoveDestination();
                    }
                    return true;
                }
                
                if (_combatMovement.HasPriorityTarget)
                {
                    Character priorityTarget = _combatMovement.PriorityTarget;
                    
                    if (_targetCreature != priorityTarget)
                    {
                        SetTarget(priorityTarget);
                    }
                    
                    if (_currentState != AIState.Combat)
                    {
                        SetState(AIState.Combat);
                    }
                    
                    if (_targetCreature != null && !_targetCreature.IsDead())
                    {
                        UpdateCombatMovement(dt);
                    }
                    else
                    {
                        _combatMovement.ClearCommandPriority();
                    }
                    return true;
                }
                
                bool hasActiveSubBehavior = _idleBehavior != null && _idleBehavior.IsInSubBehavior;
                
                if (hasActiveSubBehavior)
                {
                    return true;
                }
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionAI] {m_character?.m_name} has command priority but no destination/target/subbehavior - CLEARING PRIORITY");
                _combatMovement.ClearCommandPriority();
                _wasInCommandPriority = false;
            }
            else if (_wasInCommandPriority)
            {
                _wasInCommandPriority = false;
            }

            UpdateTargetDetection(dt);

            switch (_currentState)
            {
                case AIState.Idle:
                    UpdateIdleState(dt);
                    break;
                case AIState.Following:
                    UpdateFollowingState(dt);
                    break;
                case AIState.Combat:
                    UpdateCombatState(dt);
                    break;
                case AIState.Returning:
                    UpdateReturningState(dt);
                    break;
                case AIState.Fleeing:
                    UpdateFleeingState(dt);
                    break;
            }

            return true;
        }

        // An accidental hit from the owner must not make the companion swing at him instead of the enemy: the owner and the party
        // (ClassTargeting.IsPartyMember: allied owners and their companions) never alert, retarget or scare it; it keeps its enemy
        private readonly System.Collections.Generic.Dictionary<Character, float> _friendlyHitSaid = new System.Collections.Generic.Dictionary<Character, float>();

        public override void OnDamaged(float damage, Character attacker)
        {
            if (attacker != null && attacker != m_character && FiresCore.Npc.Archetypes.ClassTargeting.IsPartyMember(m_character, attacker))
            {
                if (!_friendlyHitSaid.TryGetValue(attacker, out float said) || Time.time - said > 10f)
                {
                    _friendlyHitSaid[attacker] = Time.time;
                    FiresCore.Logging.FiresLogger.LogDebug($"[FriendlyHit] {m_character?.m_name}: hit by {(attacker.IsPlayer() ? ((Player)attacker).GetPlayerName() : attacker.m_name)} (party) for {damage:0.#} - ignored; keeps target {(_targetCreature != null ? _targetCreature.m_name : "none")}");
                }
                return;
            }
            base.OnDamaged(damage, attacker);

            _lastDirectlyDamagedTime = Time.time;
            SetAlerted(true);
            
            // Break sneak when hit - enemy has found us
            if (_isCompanionSneaking && m_character != null)
            {
                _isCompanionSneaking = false;
                SetCompanionCrouch(false);
            }
            
            if (attacker != null && !attacker.IsDead() && IsEnemy(attacker))
            {
                ConsiderTargetSwitch(attacker, true);
            }
            else if (attacker != null && !attacker.IsDead() && attacker.IsPlayer())
            {
                // Wild (untamed) companions must always retaliate against a player
                // attacker regardless of what IsEnemy() currently returns. The faction
                // applied by WildCompanionDresser may still be syncing from the ZDO
                // (race at first spawn), leaving m_faction = Players and causing
                // IsEnemy() to require PvP. Bypassing that check here means they
                // fight back correctly the moment they take damage.
                var ctrl = GetComponent<CompanionController>();
                if (ctrl != null && !ctrl.isTamed)
                    ConsiderTargetSwitch(attacker, true);
            }

            // PLAYER COMMAND OVERRIDES FLEE.
            // If the player has issued a move/attack command we obey it even when
            // we would otherwise panic - "do what they're told, even unto death".
            // The flee branch is only allowed when no command is active.
            bool underPlayerCommand = _combatMovement != null && _combatMovement.HasCommandPriority;
            if (!underPlayerCommand && ShouldFlee())
            {
                SetState(AIState.Fleeing);
            }
        }

        public override Character GetTargetCreature()
        {
            return _targetCreature;
        }

        public override bool IsSleeping()
        {
            return false;
        }

        #endregion

        #region State Machine

        private void SetState(AIState newState)
        {
            if (_currentState == newState)
                return;

            OnExitState(_currentState);

            _previousState = _currentState;
            _currentState = newState;
            _stateStartTime = Time.time;
            _lastStateChangeTime = Time.time;

            OnEnterState(newState);

            // Also on the follow diagnostic: the Following <-> Idle <-> Combat transitions are the whole story when
            // a companion stops following, and they are event-driven, not per-frame.
            if (StateTransitionLogging || VerboseLogging
                || FiresCore.Config.ConfigManager.Instance?.configCompanionFollowDiag?.Value == true)
            {
                string targetInfo = _targetCreature != null ? _targetCreature.m_name : "none";
                string staminaInfo = _staminaManager != null ? 
                    $"stamina={_staminaManager.GetStaminaPercent():P0}, critical={_staminaManager.IsInCriticalRecovery()}, recovering={_staminaManager.IsRecovering()}" : 
                    "no stamina mgr";
                Debug.Log($"[CompanionAI] STATE CHANGE: {m_character?.m_name}: {_previousState} -> {newState} (target: {targetInfo}, {staminaInfo})");
                _lastLoggedState = newState;
            }
        }

        private void OnEnterState(AIState state)
        {
            switch (state)
            {
                case AIState.Idle:
                    _lastIdleWanderTime = Time.time;
                    if (m_character != null)
                    {
                        m_character.SetWalk(false);
                        m_character.SetRun(false);
                        // Only clear sneak if the owner is NOT currently crouching.
                        // If the companion is put into Stay mode while the player sneaks,
                        // the companion should remain crouched.
                        if (!_isOwnerSneaking)
                        {
                            SetCompanionCrouch(false);
                            _isCompanionSneaking = false;
                        }
                    }
                    if (m_character != null && (_rigidbody == null || !_rigidbody.isKinematic))
                        m_character.SetMoveDir(Vector3.zero);
                    break;

                case AIState.Following:
                    _beenAtLastTargetPos = false;
                    break;

                case AIState.Combat:
                    SetAlerted(true);
                    _timeSinceAttacking = 0f;
                    _timeSinceTargetSeen = 0f;
                    _beenAtLastTargetPos = false;
                    
                    // If companion was sneaking, preserve sneak until alerted by enemy.
                    // The combat update will break sneak when the companion attacks or gets hit.
                    if (!_isCompanionSneaking && m_character != null)
                    {
                        SetCompanionCrouch(false);
                    }
                    
                    if (ArchetypeCombatLogging)
                    {
                        LogArchetypeCombatEntry();
                    }
                    break;

                case AIState.Returning:
                    ClearTarget();
                    break;

                case AIState.Fleeing:
                    _fleeStartTime = Time.time;
                    
                    // Always break sneak when fleeing - need full speed
                    if (_isCompanionSneaking && m_character != null)
                    {
                        _isCompanionSneaking = false;
                        SetCompanionCrouch(false);
                    }
                    
                    if (_staminaManager != null && _staminaManager.IsInCriticalRecovery())
                    {
                        Debug.Log($"[CompanionAI] {m_character?.m_name} entering FLEE with critical stamina - activating enhanced recovery!");
                        _staminaManager.ForceStaminaRecoveryForFlee();
                    }
                    break;
            }
        }

        private void OnExitState(AIState state)
        {
            switch (state)
            {
                case AIState.Combat:
                    var combat = _companion?.GetCombat();
                    ClearAlertedState();
                    break;
                    
                case AIState.Fleeing:
                    if (_staminaManager != null)
                    {
                        _staminaManager.ExitFleeState();
                    }
                    
                    if (_combatMovement != null)
                    {
                        _combatMovement.ClearFleeRequest();
                    }
                    break;
            }
        }
        
        private void LogArchetypeCombatEntry()
        {
            if (_companion == null) return;
            
            // Rate-limit combat entry logging to prevent spam when state oscillates rapidly
            if (Time.time - _lastArchetypeLogTime < ArchetypeLogInterval)
                return;
            _lastArchetypeLogTime = Time.time;
            
            if (_archetypeController == null)
                _archetypeController = GetComponent<Archetypes.ArchetypeController>();
            if (_archetypeController == null)
            {
                Debug.Log($"[CompanionAI] COMBAT ENTRY: {_companion.companionName} - NO ARCHETYPE CONTROLLER");
                return;
            }
            
            var archetype = _archetypeController.CurrentArchetypeClass;
            var isTank = _archetypeController.IsTank;
            var isSupport = _archetypeController.IsSupport;
            
            var groupManager = FiresCore.Npc.Archetypes.GroupRoleManager.Instance;
            var groupTank = groupManager.GetTank(_companion.ownerPlayerId);
            bool isGroupTank = groupTank != null && groupTank.companionId == _companion.companionId;
            
            string roleInfo = isGroupTank ? "[GROUP TANK]" : (isTank ? "[TANK-CAPABLE]" : (isSupport ? "[SUPPORT]" : "[DPS]"));
            
            string abilityInfo = "";
            if (archetype == FiresCore.Npc.Archetypes.ArchetypeClass.Tank)
            {
                abilityInfo = $" | TauntReady: {!_archetypeController.IsTauntOnCooldown}";
            }
            else if (archetype == FiresCore.Npc.Archetypes.ArchetypeClass.Healer)
            {
                abilityInfo = " | SanctuaryReady: Available";
            }
            else if (archetype == FiresCore.Npc.Archetypes.ArchetypeClass.Ranger)
            {
                abilityInfo = " | HuntersMarkReady: Available";
            }
            else if (archetype == FiresCore.Npc.Archetypes.ArchetypeClass.Berserker)
            {
                abilityInfo = $" | BerserkActive: {_archetypeController.IsBerserkActive}";
            }
            
            Debug.Log($"[CompanionAI] COMBAT ENTRY: {_companion.companionName} - Archetype: {archetype} {roleInfo}{abilityInfo} | Target: {_targetCreature?.m_name ?? "none"}");
        }
        
        private void ClearAlertedState()
        {
            CancelInvoke(nameof(DelayedClearAlert));
            Invoke(nameof(DelayedClearAlert), 3.0f);
            
            if (VerboseLogging)
                Debug.Log($"[CompanionAI] {m_character?.m_name} scheduled alert state clear in 3 seconds");
        }
        
        private void DelayedClearAlert()
        {
            if (_currentState != AIState.Combat && _targetCreature == null)
            {
                SetAlerted(false);
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionAI] {m_character?.m_name} cleared alert state");
            }
        }

        #endregion

        #region Public API

        public void SetFollowTarget(GameObject target)
        {
            if (target != null && !target.activeInHierarchy)
            {
                Debug.LogWarning($"[CompanionAI] {m_character?.m_name} rejecting follow target - object is inactive");
                return;
            }
            
            if (target != null)
            {
                var player = target.GetComponent<Player>();
                if (player == null)
                {
                    Debug.LogWarning($"[CompanionAI] {m_character?.m_name} rejecting follow target - not a player");
                    return;
                }
            }
            
            _followTarget = target;
            _shouldFollow = target != null;
            _hasHomePositionSet = false;
            _ownerPlayer = target != null ? target.GetComponent<Player>() : null;

            if (_shouldFollow && _currentState == AIState.Idle)
            {
                SetState(AIState.Following);
            }
            else if (!_shouldFollow && _currentState == AIState.Following)
            {
                SetState(AIState.Idle);
            }
        }
        
        public void SetShouldFollow(bool shouldFollow)
        {
            bool wasFollowing = _shouldFollow;
            
            if (_shouldFollow != shouldFollow)
            {
                Debug.Log($"[CompanionAI] {m_character?.m_name} SetShouldFollow changing from {_shouldFollow} to {shouldFollow}");
                
                if (shouldFollow && VerboseLogging)
                {
                    Debug.Log($"[CompanionAI] SetShouldFollow(true) called - stack trace for debugging:\n{System.Environment.StackTrace}");
                }
            }
            
            _shouldFollow = shouldFollow;
            
            // CRITICAL BUG #10 FIX: When told to stop following (stay mode), release any held authority
            // and transition to Idle state. This prevents Following authority from blocking
            // sub-behaviors that need SubBehavior authority.
            if (wasFollowing && !shouldFollow)
            {
                // Release any authority we're holding - this allows sub-behaviors to acquire authority
                var authority = _companion?.GetMovementAuthority();
                if (authority != null)
                {
                    authority.ReleaseAuthority(AIAuthorityOwner);
                    if (VerboseLogging)
                        Debug.Log($"[CompanionAI] {m_character?.m_name} released authority when entering stay mode");
                }
                
                _followTarget = null;
                
                // Force transition to Idle state
                if (_currentState == AIState.Following)
                {
                    SetState(AIState.Idle);
                }
            }
            else if (!shouldFollow)
            {
                _followTarget = null;
                if (_currentState == AIState.Following)
                {
                    SetState(AIState.Idle);
                }
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionAI] {m_character?.m_name} SetShouldFollow({shouldFollow})");
            }
        }

        public GameObject GetFollowTarget()
        {
            return _followTarget;
        }

        public void SetStayPosition(Vector3 position)
        {
            _stayPosition = position;
            _shouldFollow = false;
            _followTarget = null;
            _hasHomePositionSet = true;
            SetState(AIState.Idle);
        }
        
        public void ClearStayPosition()
        {
            _stayPosition = Vector3.zero;
            _shouldFollow = true;
            _hasHomePositionSet = false;
            ClearIdleDestination();
            
            if (_followTarget != null)
            {
                SetState(AIState.Following);
            }
            
            if (VerboseLogging)
                Debug.Log($"[CompanionAI] {m_character?.m_name} stay position cleared - now following");
        }

        public void ForceTarget(Character target)
        {
            if (target != null && !target.IsDead() && IsEnemy(target))
            {
                SetTarget(target);
                if (_targetCreature == target) SetState(AIState.Combat);   // SetTarget refuses a non-hostile ally
            }
        }
        
        public void ClearForceTarget()
        {
            SetTarget(null);
        }

        public bool ShouldBeFollowing => _shouldFollow;
        public bool IsInCombat => _currentState == AIState.Combat;

        /// <summary>The follow gait this AI picked on its last tick — matched to the owner's measured pace and
        /// stance. The single source for "how fast is this companion following"; CompanionCombatMovement mirrors it
        /// into its MovementIntent rather than running a second set of distance tiers that could disagree.</summary>
        internal FollowSpeed CurrentFollowSpeed => _currentFollowSpeed;

        /// <summary>True while the FSM is actually following, as opposed to idling, fighting or returning.</summary>
        public bool IsFollowingState => _currentState == AIState.Following;

        /// <summary>The single owner-AFK answer: the owner has held still for <see cref="playerIdleThreshold"/>
        /// seconds, so the companion may stand down and do its own thing.</summary>
        public bool IsOwnerIdle => _isOwnerIdle;
        public float TimeSinceStateChange => Time.time - _lastStateChangeTime;
        public bool IsUsingRangedWeapon => _isRangedWeapon;
        
        public void SetIdleDestination(Vector3 destination)
        {
            _idleDestination = destination;
            _hasIdleDestination = true;
            
            if (VerboseLogging)
                Debug.Log($"[CompanionAI] Set idle destination: {destination}");
        }
        
        public void ClearIdleDestination()
        {
            _hasIdleDestination = false;
        }
        
        public bool HasIdleDestination => _hasIdleDestination;
        
        // Command destination
        private Vector3 _commandDestination;
        private bool _hasCommandDestination = false;
        private float _commandDestinationTime = 0f;
        private bool _wasInCommandPriority = false;
        private bool _loggedReachedDestination = false;
        
        private static float _lastDestinationReachedLogTime = -100f;
        private const float DestinationReachedLogInterval = 10f;
        
        public void SetCommandDestination(Vector3 destination)
        {
            if (!_hasCommandDestination || Vector3.Distance(_commandDestination, destination) > 1f)
            {
                _loggedReachedDestination = false;
            }
            
            _commandDestination = destination;
            _hasCommandDestination = true;
            _commandDestinationTime = Time.time;
            
            if (VerboseLogging)
                Debug.Log($"[CompanionAI] {m_character?.m_name} command destination set: {destination} (follow state preserved: {_shouldFollow})");
        }
        
        public void ClearCommandDestination()
        {
            _hasCommandDestination = false;
            if (VerboseLogging)
                Debug.Log($"[CompanionAI] {m_character?.m_name} command destination cleared");
        }
        
        public bool HasCommandDestination => _hasCommandDestination;
        
        /// <summary>
        /// Pathfinding movement for behaviors: acquires movement authority for <paramref name="authorityOwner"/> at
        /// <paramref name="authoritySource"/> and lets vanilla MoveTo find the way. Call every frame until it returns
        /// true on arrival.
        /// </summary>
        public bool RequestPathfindingMovement(
            Vector3 destination, 
            bool run, 
            float reachDistance = 1.5f,
            Core.UnifiedMovementAuthority.MovementSource authoritySource = Core.UnifiedMovementAuthority.MovementSource.SubBehavior,
            string authorityOwner = "Behavior")
        {
            if (m_character == null) return true;
            NoteIntent(destination, authorityOwner);
            
            // Check distance first - already there?
            float distance = Vector3.Distance(transform.position, destination);
            // Close in a straight line is not "there" when a wall / door frame stands between (Fire 10-23: companions stopped in
            // doorways and at walls): the straight-line early arrival needs a clear line to the goal too.
            if (distance <= reachDistance)
            {
                if (Core.CompanionReach.CanSee(transform, destination, out string blocker)) { NoteStop(destination, distance, "arrived (clear line)", null); return true; }
                NoteStop(destination, distance, "close but blocked; walking on", blocker);
                reachDistance = Mathf.Min(reachDistance, BlockedReach);   // vanilla MoveTo would call it done at this distance: walk the path in
            }
            
            // OPTIMIZATION (Bug #12 Fix): Only try to acquire authority if we don't already have it
            // This prevents log spam from re-acquiring authority every frame
            var authority = _companion?.GetMovementAuthority();
            if (authority != null)
            {
                // Check if we already have authority
                bool hasAuthority = authority.HasAuthority(authorityOwner);
                if (!hasAuthority)
                {
                    // Need to acquire - this will log
                    if (!authority.TryAcquireAuthority(authoritySource, authorityOwner, 5f))
                    {
                        // Can't acquire authority - another system has priority
                        if (VerboseLogging)
                            Debug.Log($"[CompanionAI] {m_character?.m_name} RequestPathfindingMovement blocked - {authority.CurrentAuthorityOwner} has authority");
                        return false;
                    }
                }
                // If we already have authority, just continue - TryAcquireAuthority will silently extend duration
            }
            
            // USE VANILLA PATHFINDING - this is the key!
            // BaseAI.MoveTo() uses FindPath() and m_path waypoints for obstacle avoidance.
            // Escape hatch (see MoveToThroughAuthority): open the single-writer gate around the vanilla
            // MoveTo so its direct SetMoveDir is allowed through; try/finally always re-closes it.
            bool done;
            try
            {
                authority?.DisableExternalBlocking();
                done = MoveTo(Time.deltaTime, destination, reachDistance, run);
            }
            finally
            {
                authority?.EnableExternalBlocking();
            }
            if (!done) StepAssist(destination);
            // Vanilla MoveTo also says "done" at the end of a PARTIAL path or when it finds none. Short of the destination by more
            // than PartialSlack that is not an arrival: report false (the behaviour's own timeout ends it honestly) and say so once
            // (BaseAI's path can end short, e.g. at a lip).
            PathShortBy = 0f;
            if (done)
            {
                // 3D, not XZ only: a goal up a raised threshold / floor (1 m higher) was "done" 6 m short (Fire 10-23, small companions at a step)
                float left = Mathf.Max(Utils.DistanceXZ(transform.position, destination), Vector3.Distance(transform.position, destination) - 0.5f);
                if (left > reachDistance + PartialSlack) StepAssist(destination);
                if (left > reachDistance + PartialSlack)
                {
                    PathShortBy = left;
                    if (Vector3.Distance(destination, _shortLoggedFor) > 1f)
                    {
                        _shortLoggedFor = destination;
                        Debug.Log($"[CompanionAI] {m_character?.m_name}: path ends {left:0.0} m short of {destination} ({authorityOwner}); not arrived");
                    }
                    return false;
                }
            }
            if (done) NoteStop(destination, Vector3.Distance(transform.position, destination), "path done", null);
            return done;
        }

        // One Debug line per stop (per destination): goal, straight distance, path points and status, the blocker if any.
        private Vector3 _stopLoggedFor = new Vector3(float.NaN, 0f, 0f);

        // ---- step assist: a scaled-down body's capsule can't ride up a threshold / step a full-size one walks over (Fire 10-23:
        // small companions stuck on the doorway step). Stalled (no progress for StallSeconds) with a low step right ahead and room
        // above it, the body is lifted onto the step. The step limit is absolute (StepMaxHeight), whatever the companion's scale.
        private const float StepMaxHeight = 0.6f, StallSeconds = 0.75f, StallMove = 0.1f, StepProbe = 0.7f;
        private Vector3 _stallPos; private float _stallSince = -1f, _stepLogAt;
        private static int s_stepMask = -1;
        private void StepAssist(Vector3 destination)
        {
            if (m_character == null || m_character.m_body == null || !m_character.IsOnGround()) { _stallSince = -1f; return; }
            Vector3 pos = transform.position;
            if (_stallSince < 0f || (pos - _stallPos).sqrMagnitude > StallMove * StallMove) { _stallPos = pos; _stallSince = Time.time; return; }
            if (Time.time - _stallSince < StallSeconds) return;
            _stallSince = Time.time; _stallPos = pos;
            Vector3 target = m_path != null && m_path.Count > 0 ? m_path[0] : destination;
            Vector3 dir = target - pos; dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) { dir = destination - pos; dir.y = 0f; }
            if (dir.sqrMagnitude < 0.01f) return;
            dir.Normalize();
            if (s_stepMask == -1) s_stepMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");
            // something at ankle height ahead, and clear air at step height
            if (!Physics.Raycast(pos + Vector3.up * 0.05f, dir, StepProbe, s_stepMask, QueryTriggerInteraction.Ignore)) return;
            if (Physics.Raycast(pos + Vector3.up * (StepMaxHeight + 0.05f), dir, StepProbe, s_stepMask, QueryTriggerInteraction.Ignore)) return;
            // the step's top, just ahead
            Vector3 probe = pos + dir * Mathf.Min(StepProbe, 0.45f) + Vector3.up * (StepMaxHeight + 0.1f);
            if (!Physics.Raycast(probe, Vector3.down, out RaycastHit top, StepMaxHeight + 0.2f, s_stepMask, QueryTriggerInteraction.Ignore)) return;
            float rise = top.point.y - pos.y;
            if (rise < 0.05f || rise > StepMaxHeight) return;
            Vector3 onto = new Vector3(top.point.x, top.point.y + 0.05f, top.point.z);
            m_character.m_body.position = onto;
            transform.position = onto;
            if (Time.time >= _stepLogAt) { _stepLogAt = Time.time + 2f; FiresCore.Logging.FiresLogger.LogDebug($"[StepAssist] {m_character.m_name} (scale {transform.localScale.y:0.00}): stalled at a {rise:0.00} m step; lifted onto it at {onto:F1}"); }
        }
        private const float BlockedReach = 0.5f;   // m: a goal behind a door frame / wall is reached by walking right up to it
        private void NoteStop(Vector3 destination, float straight, string what, string blocker)
        {
            if ((destination - _stopLoggedFor).sqrMagnitude < 0.25f) return;
            _stopLoggedFor = destination;
            int pts = m_path != null ? m_path.Count : 0;
            string status = pts == 0 ? "none" : Utils.DistanceXZ(m_path[pts - 1], destination) > 1.5f ? "partial" : "complete";
            FiresCore.Logging.FiresLogger.LogDebug($"[CompanionAI] {m_character?.m_name}: stop at goal {destination:F1}: {what}; scale {transform.localScale.y:0.00}, goal {destination.y - transform.position.y:+0.0;-0.0} m up, straight {straight:0.0} m, path {pts} pt(s) {status}{(pts > 0 ? $" ending {m_path[pts - 1]:F1}" : "")}{(blocker != null ? $", blocked by {blocker}" : "")}");
        }

        /// <summary>How far short of its destination the last pathfinding move stopped (0 = reached or still walking).</summary>
        public float PathShortBy { get; private set; }

        // ---- intent view (Fire 10-24: "a way to view a companions intent and debug them"): read-only, what the movement already knows
        public Vector3 IntentGoal { get; private set; }
        public float IntentGoalAt { get; private set; } = -1f;
        public string IntentGoalOwner { get; private set; } = "";
        public int IntentPathPoints => m_path != null ? m_path.Count : 0;
        public Vector3 IntentPathEnd => m_path != null && m_path.Count > 0 ? m_path[m_path.Count - 1] : Vector3.zero;
        /// <summary>Seconds since the distance to the goal last shrank by 0.3 m (0 when not walking to a goal).</summary>
        public float IntentStuckSeconds => IntentGoalAt < 0f || Time.time - IntentGoalAt > 1f ? 0f : _intentWatch.Seconds;
        private readonly Core.ProgressWatch _intentWatch = new Core.ProgressWatch(0.3f);
        private void NoteIntent(Vector3 goal, string owner)
        {
            if (Time.time - IntentGoalAt > 2f) _intentWatch.Reset();
            _intentWatch.Tick(transform.position, goal);
            IntentGoal = goal; IntentGoalAt = Time.time; IntentGoalOwner = owner ?? "";
        }
        private const float PartialSlack = 1.5f;
        private Vector3 _shortLoggedFor = new Vector3(float.NaN, 0f, 0f);

        /// <summary>
        /// Grounded DIRECT move toward <paramref name="destination"/> — vanilla BaseAI.MoveTowards (SetLookDir +
        /// SetMoveDir, NO FindPath). Physics carries the NPC onto whatever collider is underfoot, terrain OR a
        /// player-built piece, so this is how a patrol crosses a BRIDGE / raised floor that Valheim's terrain
        /// navmesh doesn't include — RequestPathfindingMovement (FindPath) would snap the target off the piece
        /// to the nearest baked navmesh point and route the NPC under it. Uses the same single-writer escape
        /// hatch (DisableExternalBlocking) as the pathfinding move so the direct SetMoveDir lands; for a static
        /// NPC there is no authority and vanilla's raw SetMoveDir applies (nothing else writes it).
        /// </summary>
        public bool RequestDirectMovement(
            Vector3 destination,
            bool run,
            Core.UnifiedMovementAuthority.MovementSource authoritySource = Core.UnifiedMovementAuthority.MovementSource.SubBehavior,
            string authorityOwner = "Behavior")
        {
            if (m_character == null) return true;
            NoteIntent(destination, authorityOwner);
            Vector3 dir = destination - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return true;

            var authority = _companion?.GetMovementAuthority();
            if (authority != null && !authority.HasAuthority(authorityOwner))
            {
                if (!authority.TryAcquireAuthority(authoritySource, authorityOwner, 5f))
                    return false;   // another system holds priority
            }
            try
            {
                authority?.DisableExternalBlocking();
                MoveTowards(dir, run);
            }
            finally
            {
                authority?.EnableExternalBlocking();
            }
            return false;
        }
        
        /// <summary>
        /// Releases movement authority held by a specific owner.
        /// Call this when a behavior stops or completes.
        /// </summary>
        public void ReleasePathfindingMovement(string authorityOwner)
        {
            var authority = _companion?.GetMovementAuthority();
            authority?.ReleaseAuthority(authorityOwner);
            
            // Also stop movement
            StopMoving();
        }
        
        public void RequestPathRecalculation()
        {
            ForcePathRecalculation();
            _consecutiveStuckFrames = 0;
            _pathfindingAttempts = 0;
            _lastProgressTime = Time.time;
        }
        
        public void ResetPathfindingState()
        {
            _lastPathfindingPos = transform.position;
            _lastPathfindingTime = Time.time;
            _consecutiveStuckFrames = 0;
            _pathfindingAttempts = 0;
            _lastProgressTime = Time.time;
            _lastDistanceToTarget = float.MaxValue;
            _currentMoveTarget = Vector3.zero;
            // Drop the vanilla waypoints + cached search too, so a teleported body does not steer back
            // along a path computed from where it used to be.
            if (m_path != null) m_path.Clear();
            InvalidatePathCache();
        }

        public bool IsStuck => _consecutiveStuckFrames >= StuckFramesBeforeRecalc;
        public int StuckCheckCount => _consecutiveStuckFrames;
        
        /// <summary>
        /// Sets the companion's crouch/sneak state. For a non-player Humanoid the replicated "crouching" animator bool IS the
        /// crouch state (Character.SetCrouch is an empty virtual and IsCrouching is false for non-players), so
        /// <see cref="Movement.CompanionStance"/> owns it. Standing up also ends prone.
        /// </summary>
        private void SetCompanionCrouch(bool crouch)
        {
            if (m_character == null || _stance == null) return;
            _stance.SetCrouch(crouch);
        }

        #endregion
    }
}
