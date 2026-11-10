using UnityEngine;
using System;
using System.Collections.Generic;
using FiresCore.Npc.Combat;
using FiresCore.Npc.IdleBehaviors;
using FiresCore.Npc.Movement;
using FiresCore.Npc.NpcMode;
using FiresCore.Npc.Animation;

namespace FiresCore.Npc
{
    /// <summary>
    /// Idle behavior for companions that are neither fighting nor following: wandering, emotes, chairs, head
    /// look-at, weapon holstering and <see cref="IdleSubBehavior"/> activities, split across partial files by
    /// topic. A destination is committed once per decision and terrain is scored once per idle period, since
    /// per-frame re-evaluation made movement jittery. Emotes and sitting have a hard timeout, holstering moves
    /// real inventory items, and combat cancels everything through CancelAllIdleBehaviors.
    /// </summary>
    public partial class CompanionIdleBehavior : MonoBehaviour
    {
        #region Settings

        [Header("Idle Behavior Settings")]
        public float idleBehaviorInterval = 8f;
        public float idleWanderMinDistance = 5f;
        public float idleWanderMaxDistance = 12f;
        public float idleWanderChance = 0.3f;
        public float idleLookAroundChance = 0.4f;
        [Tooltip("Minimum time to pause at a wander point (look around, rest)")]
        public float wanderPauseDuration = 10f;
        [Tooltip("Maximum time to pause at a wander point (look around, rest)")]
        public float wanderPauseMaxDuration = 90f;

        [Header("Wander Radius Settings")]
        public float maxWanderRadius = 20f;
        public float preferredWanderRadius = 12f;
        public float homePullStrength = 0.7f;
        [Tooltip("Use soft limit instead of hard - allows overshoot but biases back home")]
        public bool useSoftWanderLimit = true;

        [Header("Multi-Leg Wander Settings")]
        public float continueWanderChance = 0.5f;
        public int maxWanderLegs = 4;
        public float minTurnAngle = 30f;
        public float maxTurnAngle = 120f;

        [Header("Path Preference Settings")]
        public bool preferPavedPaths = true;
        public int pathSampleCount = 8;
        public float pathDetectionRadius = 10f;
        public float pavedPathBonus = 5f;
        public float cultivatedBonus = 2f;
        public float seekPathChance = 0.4f;
        public float pathContinueDirectionChance = 0.85f;

        [Header("Head Look-At Settings")]
        public bool enableHeadLookAt = true;
        public float lookAtDetectionRange = 15f;
        public float lookAtDuration = 4f;
        public float lookAtSwitchCooldown = 1.5f;
        public float maxLookAtAngle = 75f;
        public float headRotationSpeed = 3f;
        public float headReturnSpeed = 1.5f;

        [Header("Idle Emote Settings")]
        public float idleEmoteMinInterval = 45f;
        public float idleEmoteMaxInterval = 120f;
        public float idleEmoteChance = 0.5f;
        [Tooltip("Duration for quick one-shot emotes like wave, cheer, point (seconds)")]
        public float quickEmoteDuration = 2.5f;
        [Tooltip("Minimum duration for persistent emotes like sit, relax, vibe (seconds)")]
        public float persistentEmoteDurationMin = 8f;
        [Tooltip("Maximum duration for persistent emotes like sit, relax, vibe (seconds)")]
        public float persistentEmoteDurationMax = 25f;
        [Tooltip("Hard maximum time any emote can play before forced reset")]
        public float maxEmoteHardTimeout = 45f;

        [Header("Chair Sitting Settings")]
        public bool sitOnChairsWhenIdle = true;
        public float chairDetectionRadius = 8f;
        [Tooltip("Minimum time to sit on a chair (seconds)")]
        public float chairSitDurationMin = 30f;
        [Tooltip("Maximum time to sit on a chair (seconds)")]
        public float chairSitDurationMax = 90f;
        public float chairSitCooldown = 60f;
        [Tooltip("Hard maximum time to sit before forced stand - prevents getting stuck")]
        public float chairSitHardTimeout = 120f;
        [Tooltip("Chance to decide to sit when wandering near a chair")]
        public float sitChanceWhileWandering = 0.4f;

        [Header("Weapon Holstering")]
        public bool holsterWeaponsWhenIdle = true;
        public float holsterDelay = 4f;
        [Tooltip("Chance to holster weapons when becoming idle (0-1)")]
        public float holsterChance = 0.7f;

        [Header("Sub-Behavior Settings")]
        [Tooltip("Chance to start a sub-behavior (training, etc.) when idle")]
        public float subBehaviorChance = 0.3f;
        [Tooltip("Minimum time between sub-behavior attempts")]
        public float subBehaviorCooldown = 60f;

        [Header("Rotation Settings")]
        public float idleRotationSpeed = 30f;

        [Header("Combat Cooldown")]
        public float combatCooldownForIdle = 5.0f;

        [Header("Stuck Prevention")]
        [Tooltip("How often to check if we're stuck in an idle state")]
        public float stuckCheckInterval = 5f;
        [Tooltip("Maximum time to be in any non-Standing idle state")]
        public float maxIdleStateDuration = 60f;

        #endregion

        #region Components

        private CompanionController _companion;
        private CompanionCombatMovement _combatMovement;
        private CompanionStateController _stateController;
        private AI.CompanionAI _companionAI;
        private Character _character;
        private Animator _animator;
        private ZSyncAnimation _zanim;
        private CompanionInventory _inventory;
        private NpcVisEquipment _visEquipment;
        private Interactions.CompanionInteractionBehavior _interactionBehavior;
        private CompanionNpcModule _npcModule;

        #endregion

        #region State

        // Current idle state
        private IdleState _currentIdleState = IdleState.Standing;
        private float _currentStateStartTime;

        // Idle behavior timing
        private float _lastIdleBehaviorCheck;
        private float _idleBehaviorEndTime;
        private float _nextEmoteTime;
        private float _idleStartTime;
        private float _lastStuckCheck;

        // Combat timing
        private float _lastCombatTime;
        private float _lastOnCombatStartedCall;
        private const float MinOnCombatStartedInterval = 2f;

        // Look around state
        private float _lookAroundEndTime;

        // Wander state
        private int _currentWanderLeg = 0;
        private int _totalWanderLegs = 1;
        private Vector3 _lastWanderDirection;

        // COMMITTED terrain state
        private bool _terrainCheckedThisIdleSession = false;
        private bool _isOnPath = false;
        private float _currentTerrainScore = 0f;

        // Smooth movement for idle
        private Vector3 _currentDestination;
        private bool _hasActiveDestination = false;
        private float _destinationReachedThreshold = 2.0f;
        private float _lastWanderCompleteTime;
        private const float MinWanderCooldown = 4f;

        // Smooth rotation
        private float _targetYRotation;
        private bool _isRotating = false;
        private float _rotationStartTime;
        private float _rotationDuration;
        private float _startYRotation;

        // Home position reference
        private Vector3 _homePosition;
        private bool _hasHomePosition = false;
        
        /// <summary>
        /// Gets or sets the home position for wander behavior.
        /// </summary>
        public Vector3 HomePosition
        {
            get => _homePosition;
            set
            {
                _homePosition = value;
                _hasHomePosition = true;
            }
        }
        
        /// <summary>
        /// Returns true if a home position has been set.
        /// </summary>
        public bool HasHomePosition => _hasHomePosition;

        // Fire 10-27 ("It would just be added to the daily jobs and the rotation of work"): a designated homestead-map zone a board job
        // sent this companion to for ONE work run. Chores search inside it; the stay point is untouched; it clears when that chore ends.
        public bool HasWorkZone { get; private set; }
        public Vector3 WorkZoneCenter { get; private set; }
        public float WorkZoneRadius { get; private set; }
        public string WorkZoneFocus { get; private set; }
        public string WorkZoneName { get; private set; }
        public Vector3 WorkCenter => HasWorkZone ? WorkZoneCenter : _homePosition;
        public float WorkRadius(Vector3 home) => HasWorkZone ? WorkZoneRadius : CompanionSettings.GetStayModeWorkSearchRadius(home);
        public void SetWorkZone(Vector3 center, float radius, string focus, string name) => SetWorkZone(center, radius, focus, name, true);
        // 0.2.912: say = false for the quiet "can it start here?" check before a run (no "works" line for a run that never starts)
        public void SetWorkZone(Vector3 center, float radius, string focus, string name, bool say)
        {
            HasWorkZone = true; WorkZoneCenter = center; WorkZoneRadius = radius; WorkZoneFocus = focus; WorkZoneName = name;
            if (say) FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] zone run: {_companion?.GetDisplayName()} works {name} at ({center.x:0}, {center.z:0}) r={radius:0} m for one run (stay point kept)");
        }
        public void ClearWorkZone(string why) => ClearWorkZone(why, true);
        public void ClearWorkZone(string why, bool say)
        {
            if (!HasWorkZone) return;
            if (say) FiresCore.Logging.FiresLogger.LogDebug($"[HomeMap] zone run: {_companion?.GetDisplayName()} finished {WorkZoneName} ({why}); back to the full rotation");
            HasWorkZone = false; WorkZoneFocus = null; WorkZoneName = null;
        }
        
        /// <summary>
        /// Gets the current home position. Returns Vector3.zero if no home is set.
        /// </summary>
        public Vector3 GetHomePosition() => _homePosition;
        
        /// <summary>
        /// Sets the home position for wild/stationary companions.
        /// They will wander around this point within the maxWanderRadius.
        /// </summary>
        public void SetHomePosition(Vector3 position, float? wanderRadius = null)
        {
            _homePosition = position;
            _hasHomePosition = true;
            
            if (wanderRadius.HasValue)
            {
                maxWanderRadius = wanderRadius.Value;
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionIdleBehavior] Set home position: {position}, radius: {maxWanderRadius}m");
            }
        }
        
        /// <summary>
        /// Clears the home position (companion can wander freely or follow owner).
        /// </summary>
        public void ClearHomePosition()
        {
            _hasHomePosition = false;
            _homePosition = Vector3.zero;
        }

        // Head look-at state
        private Transform _lookAtTarget;
        private float _lookAtEndTime;
        private float _lastLookAtSwitch;
        private Vector3 _currentHeadLookDirection;
        private Vector3 _targetHeadLookDirection;
        private Transform _headBone;
        private bool _headBoneFound = false;
        private float _headLookWeight = 0f;
        private float _targetHeadLookWeight = 0f;

        // Emote state
        private bool _isPlayingEmote;
        private float _emoteEndTime;
        private string _currentEmote;
        private bool _isPersistentEmote;
        private bool _wasKinematicBeforeEmote;
        private Rigidbody _rigidbody;

        // Chair sitting
        private bool _isSittingOnChair;
        private ZNetView _nview;
        private bool _wasOwner;
        private float _chairSitEndTime;
        private float _chairHardTimeoutTime;
        private float _lastChairCheckTime;

        // UI interaction freeze
        private bool _isPlayerInteracting;
        private float _interactionStartTime;
        
        // STANDING PAUSE - How long to wait before making new idle decisions
        private float _standingPauseStartTime;
        private float _standingPauseMinDuration = 8f;   // Minimum 8 seconds standing still
        private float _standingPauseMaxDuration = 45f;  // Maximum 45 seconds standing still
        private float _nextDecisionTime;
        private bool _isInStandingPause = false;
        internal bool _lastRoundFoundWork = true;   // 0.2.843: did the last chore round start something (short pauses while there is work)
        internal float _emptyRoundUntil;
        private const float EmptyRoundBackoff = 20f;   // 0.2.843            // 0.2.843: after a round that found nothing, the next round waits until this
        private const float MinVelocityForStationary = 0.1f;

        // Weapon holstering
        private bool _weaponsHolstered = false;
        private bool _holsterDecisionMade = false;

        // Sub-behaviors
        private List<IdleSubBehavior> _subBehaviors = new List<IdleSubBehavior>();
        private IdleSubBehavior _activeSubBehavior;
        private float _lastSubBehaviorAttempt;

        public static bool VerboseLogging = false;
        
        // ZONE-PROXIMITY GATING - skip expensive idle evaluation when far from all players
        private const float PlayerProximityCheckInterval = 5f;
        private const float PlayerProximityThreshold = 64f; // Same as zone load distance
        private float _lastProximityCheckTime = -999f;
        private bool _isNearAnyPlayer = true; // Assume near until first check

        #endregion

        #region Enums

        public enum IdleState
        {
            Standing,
            LookingAround,
            Wandering,
            WaitingAtWanderPoint,
            PlayingEmote,
            SittingOnChair,
            SubBehavior
        }

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _combatMovement = GetComponent<CompanionCombatMovement>();
            _stateController = GetComponent<CompanionStateController>();
            _companionAI = GetComponent<AI.CompanionAI>();
            _character = GetComponent<Character>();
            _animator = GetComponentInChildren<Animator>(true);
            _zanim = GetComponent<ZSyncAnimation>();
            _npcModule = GetComponent<CompanionNpcModule>();
            _nview = GetComponent<ZNetView>();
        }

        private void Start()
        {
            _nextEmoteTime = Time.time + UnityEngine.Random.Range(idleEmoteMinInterval, idleEmoteMaxInterval);
            _lastIdleBehaviorCheck = Time.time;
            _currentHeadLookDirection = transform.forward;
            _targetHeadLookDirection = transform.forward;
            _idleStartTime = Time.time;
            _currentStateStartTime = Time.time;
            _lastStuckCheck = Time.time;

            _inventory = GetComponent<CompanionInventory>();
            _visEquipment = GetComponent<NpcVisEquipment>();
            _rigidbody = GetComponent<Rigidbody>();
            _interactionBehavior = GetComponent<Interactions.CompanionInteractionBehavior>();
            
            if (_interactionBehavior == null)
            {
                _interactionBehavior = gameObject.AddComponent<Interactions.CompanionInteractionBehavior>();
            }

            FindHeadBone();
            InitializeSubBehaviors();
        }

        private void Update()
        {
            // Static placed NPCs have no CompanionController - allow them through
            // if they're stationed with idle wandering enabled
            if (_companion == null)
            {
                if (_npcModule == null)
                    _npcModule = GetComponent<CompanionNpcModule>();
                // AllowIdleWander governs ONLY the homesteading idle behaviors (chores within the
                // wander radius). A patrol route is an explicit assignment and must run regardless,
                // so let a route-carrying static NPC through even with wandering off (mirrors the
                // with-CompanionController static path below).
                if (_npcModule == null || !_npcModule.isStaticPlacement) return;
                if (!_npcModule.allowIdleWandering && !HasPatrolRoute()) { StaticHeadLookOnly(); return; }
            }
            else if (_companion.isStaticPlacement)
            {
                // Static placement with CompanionController - only run if wandering is enabled
                if (_npcModule == null)
                    _npcModule = GetComponent<CompanionNpcModule>();
                if (_npcModule == null) return;
                if (!_npcModule.allowIdleWandering && !HasPatrolRoute()) { StaticHeadLookOnly(); return; }
            }
            else if (!_companion.isTamed)
            {
                return;
            }

            // Only the machine owning the companion lives its idle life. Every other client used to run it too, each
            // playing its own emotes (broadcast triggers) and repeating every chore. Head look-at is local and cosmetic.
            if (_nview == null || !_nview.IsValid()) return;
            if (!_nview.IsOwner())
            {
                if (_wasOwner) ReleaseIdleOnOwnershipLoss();
                _wasOwner = false;
                UpdateHeadLookAt();
                return;
            }
            if (!_wasOwner) ClearInheritedPoses();
            _wasOwner = true;
            ReleaseStaleSeatWhileMoving();

            if (_stateController == null)
                _stateController = GetComponent<CompanionStateController>();
            
            // ZONE-PROXIMITY GATING: Periodically check if any player is nearby.
            // When far from all players, skip expensive idle evaluation (sub-behavior
            // CanStart checks that do Physics.OverlapSphere, chest scanning, etc.)
            if (Time.time - _lastProximityCheckTime >= PlayerProximityCheckInterval)
            {
                _lastProximityCheckTime = Time.time;
                _isNearAnyPlayer = IsAnyPlayerNearby();
            }
            
            // Check centralized state controller freeze
            if (_stateController != null && _stateController.ShouldFreeze())
            {
                // Even while frozen we must let the emote expiry timer fire;
                // the Emote state itself causes ShouldFreeze()=true, so without
                // this the end-time check below is never reached and emotes run forever.
                if (_isPlayingEmote && Time.time >= _emoteEndTime)
                    ForceEndEmote();
                return;
            }
            
            // Check UI interaction freeze
            if (IsPlayerInteractingWithUI())
            {
                EnforceInteractionFreeze();
                return;
            }
            else if (_isPlayerInteracting)
            {
                OnInteractionEnded();
            }
            
            // Emote freeze enforcement
            if (_isPlayingEmote)
            {
                EnforceEmoteFreeze();
                
                if (Time.time >= _emoteEndTime)
                {
                    ForceEndEmote();
                }
                return;
            }
            
            // Skip idle behaviors when player command is active
            if (_stateController != null && _stateController.IsPlayerCommandActive)
            {
                if (_activeSubBehavior != null)
                {
                    UpdateActiveSubBehavior();
                }
                return;
            }
            
            // Secondary check for combat movement command priority
            if (_combatMovement != null && _combatMovement.HasCommandPriority)
            {
                if (_activeSubBehavior != null)
                {
                    UpdateActiveSubBehavior();
                }
                return;
            }

            // Home position sync
            if (_companion != null && _companion.ShouldBeFollowing)
            {
                _hasHomePosition = false;
            }
            else if (_combatMovement != null)
            {
                _hasHomePosition = _combatMovement.HasHomePosition;
                if (_hasHomePosition)
                    _homePosition = _combatMovement.HomePosition;
            }
            
            // Sync wander radius from NPC module or global config
            if (_npcModule == null)
                _npcModule = GetComponent<CompanionNpcModule>();
            if (_npcModule != null && _npcModule.IsStationedAsNpc && _npcModule.allowIdleWandering)
            {
                // NPC-mode: per-NPC radius (ZDO-backed, may differ per companion)
                float effectiveRadius = _npcModule.EffectiveWanderRadius;
                if (effectiveRadius > 0)
                {
                    maxWanderRadius = effectiveRadius;
                    preferredWanderRadius = effectiveRadius * 0.6f;
                    idleWanderMinDistance = Mathf.Min(3f, effectiveRadius * 0.3f);
                    idleWanderMaxDistance = Mathf.Min(effectiveRadius * 0.8f, effectiveRadius - 1f);
                }

                if (!_hasHomePosition)
                {
                    _hasHomePosition = true;
                    _homePosition = _npcModule.StationedPosition;

                    if (VerboseLogging)
                        Debug.Log($"[CompanionIdleBehavior] Set home position from NPC module: {_homePosition}, radius: {maxWanderRadius}m");
                }
            }
            else if (_hasHomePosition && (_companion == null || !_companion.ShouldBeFollowing))
            {
                // Regular stay companion: sync from the global configured wander radius every frame
                // so that changing the config is reflected without relogging.
                float configRadius = CompanionSettings.IdleWanderRadius;
                maxWanderRadius = configRadius;
                preferredWanderRadius = configRadius * 0.6f;
                idleWanderMinDistance = Mathf.Min(3f, configRadius * 0.3f);
                idleWanderMaxDistance = Mathf.Min(configRadius * 0.8f, configRadius - 1f);
            }

            // Check for stuck states periodically
            long tSec = System.Diagnostics.Stopwatch.GetTimestamp();
            CheckForStuckState();

            // Patrol: keep an assigned route running (force-started, not part of the random rotation).
            TryStartPatrolIfAssigned();
            tSec = NoteSection("stuck + patrol", tSec);

            string secSub = _activeSubBehavior != null ? _activeSubBehavior.GetType().Name : "start check";
            UpdateActiveSubBehavior();

            tSec = NoteSection(secSub, tSec);
            if (_activeSubBehavior == null)
                UpdateHeadLookAt();

            tSec = NoteSection("head look", tSec);
            UpdateSmoothRotation();

            // holstering, timed with the rotation
            UpdateWeaponHolstering();
            NoteSection("rotation + holster", tSec);
        }

        // 0.2.1102 (lead, [fgn]'s stall ranking: 2,442 ms over 486 frames, no child timings): Debug, the slowest section across companions every 10 s, and the total per section.
        private static readonly Dictionary<string, double> s_secMs = new Dictionary<string, double>();
        private static double s_secWorst; private static string s_secWorstName; private static float s_secSayAt;
        private static long NoteSection(string name, long since)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double ms = (now - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            s_secMs.TryGetValue(name, out double sum); s_secMs[name] = sum + ms;
            if (ms > s_secWorst) { s_secWorst = ms; s_secWorstName = name; }
            if (Time.unscaledTime >= s_secSayAt)
            {
                s_secSayAt = Time.unscaledTime + 10f;
                var parts = new List<string>();
                foreach (var kv in s_secMs) if (kv.Value >= 1.0) parts.Add($"{kv.Key} {kv.Value:0}");
                if (parts.Count > 0) FiresCore.Logging.FiresLogger.LogDebug($"[CompanionIdle] last 10 s: worst {s_secWorst:0.0} ms in {s_secWorstName}; ms per section: {string.Join(", ", parts)}");
                s_secMs.Clear(); s_secWorst = 0;
            }
            return now;
        }

        private void LateUpdate()
        {
            ApplyHeadLookAt();
        }

        // 0.2.1186 ([ghost], Fire 11-06 placed NPCs "dont look at us"): a static fixture (no wander, no route) skipped the whole
        // Update, head look-at included, so it never turned its head. It now runs ONLY the head look-at (local + cosmetic, on every
        // machine, as for non-owners below); no chores, emotes or movement. RPGM 0.0.833 keeps this component enabled on fixtures.
        private bool _staticLookLogged;
        private void StaticHeadLookOnly()
        {
            if (!_headBoneFound && !_staticLookLogged) FindHeadBone();   // one retry (the body may be dressed after Start), never per frame
            UpdateHeadLookAt();
            if (!_staticLookLogged) { _staticLookLogged = true; FiresCore.Logging.FiresLogger.LogDebug($"[IdleHeadLook] {name}: static NPC - head look-at only (head bone {(_headBoneFound ? "found" : "MISSING")})"); }
        }

        /// <summary>Drops the chore, seat and emote this machine was running once another machine owns the companion.</summary>
        // who called CancelAllIdleBehaviors (the two frames above it), for the sub-behaviour's "cancelled:" end line.
        private static string CallerChain()
        {
            try
            {
                var trace = new System.Diagnostics.StackTrace(2, false);   // 0 = CallerChain, 1 = CancelAllIdleBehaviors
                var parts = new System.Collections.Generic.List<string>();
                for (int i = 0; i < trace.FrameCount && parts.Count < 2; i++)
                {
                    var method = trace.GetFrame(i)?.GetMethod();
                    if (method != null) parts.Add($"{method.DeclaringType?.Name}.{method.Name}");
                }
                return parts.Count > 0 ? string.Join(" < ", parts) : "?";
            }
            catch { return "?"; }
        }

        private void ReleaseIdleOnOwnershipLoss()
        {
            CancelActiveSubBehavior("another machine owns the companion now");
            if (_isSittingOnChair) StandUpFromChair();
            if (_character != null) InteractableOccupancyManager.ReleaseAllForOccupant(_character);
            _isPlayingEmote = false;
            _currentEmote = "";
            _isPersistentEmote = false;
            _hasActiveDestination = false;
        }

        /// <summary>
        /// A new owner inherits the previous owner's seat and emote bools through the ZDO (a saved companion keeps them
        /// too), but none of the seat or emote state that would end them, so they are released unless it really sits.
        /// </summary>
        // Companions could get stuck sitting: a seat / sit bool left set (a Dressing-room preview played on the
        // real NPC, a saved idle key, a seat whose release was missed) was only cleared on an ownership change, and every "end emote"
        // path uses StopEmotes, which by design keeps attach_*. A companion that is MOVING and not attached to a seat can't be sitting:
        // its seat / sit bools are released (StopAll), checked twice a second on the owner. A companion sitting still (its own sit
        // emote, a chair) is untouched. One Debug line per release.
        private static readonly string[] s_seatBools = { "emote_sit", "attach_chair", "attach_throne", "attach_bed", "attach_sitship", "attach_divan" };
        private float _nextSeatCheck;
        private void ReleaseStaleSeatWhileMoving()
        {
            if (Time.time < _nextSeatCheck || _animator == null) return;
            _nextSeatCheck = Time.time + 0.5f;
            if (_interactionBehavior != null && _interactionBehavior.IsAttached) return;
            var body = GetComponent<Character>();
            if (body == null || body.GetVelocity().magnitude < 0.6f) return;
            string held = null;
            foreach (var p in _animator.parameters)
                if (p.type == AnimatorControllerParameterType.Bool && System.Array.IndexOf(s_seatBools, p.name) >= 0 && _animator.GetBool(p.nameHash)) { held = p.name; break; }
            if (held == null) return;
            PlayerAnimationCatalog.StopAll(_zanim, _animator);
            FiresCore.Logging.FiresLogger.LogDebug($"[IdleBehavior] {name}: '{held}' was still set while moving ({body.GetVelocity().magnitude:0.0} m/s, not attached); seat/sit poses released");
        }

        private void ClearInheritedPoses()
        {
            if (_interactionBehavior != null && _interactionBehavior.IsAttached) return;
            PlayerAnimationCatalog.StopAll(_zanim, _animator);
        }

        #endregion

        #region Public API

        private float _fightDropLogAt;   // 0.2.941

        public bool UpdateIdleBehavior()
        {
            // Check if stationed as NPC without idle wandering
            if (_npcModule == null)
                _npcModule = GetComponent<CompanionNpcModule>();
            // A saved patrol route passes these gates like the two above (~359/367) and
            // EnforceStaticInertness do, so a placed NPC with a route walks it even with idle wandering off.
            if (_npcModule != null && _npcModule.IsStationedAsNpc && !_npcModule.allowIdleWandering && !HasPatrolRoute())
            {
                return false;
            }

            // Static placed NPCs should not wander until explicitly enabled (a patrol route counts as enabled)
            if (_companion != null && _companion.isStaticPlacement)
            {
                if (_npcModule == null || (!_npcModule.allowIdleWandering && !HasPatrolRoute()))
                    return false;
            }
            
            // Skip idle behaviors when companion has active player command
            if (_combatMovement != null && _combatMovement.HasCommandPriority)
            {
                if (_activeSubBehavior != null)
                {
                    UpdateActiveSubBehavior();
                    return true;
                }
                return false;
            }

            // 0.2.941 (round #37 melee test: Knut planted turnips 3 times between swings while he had a fight target - the combat
            // movement's IsInCombat flickers off between swings): a live fight target within 40 m also counts as in combat, so chores
            // stop until the fight resolves (staying companions included; lead / Fire 10-27)
            var fightAi = _companion != null ? _companion.GetCompanionAI() : null;
            var fightTarget = fightAi != null ? fightAi.CombatTarget : null;
            bool hasFight = fightTarget != null && !fightTarget.IsDead() && Vector3.Distance(transform.position, fightTarget.transform.position) < 40f;
            if ((_combatMovement != null && _combatMovement.IsInCombat) || hasFight)
            {
                if (hasFight && _activeSubBehavior != null && Time.time >= _fightDropLogAt) { _fightDropLogAt = Time.time + 10f; FiresCore.Logging.FiresLogger.LogDebug($"[CompanionIdleBehavior] {_companion?.companionName}: chore {_activeSubBehavior.BehaviorName} dropped - fighting {fightTarget.m_name} at {Vector3.Distance(transform.position, fightTarget.transform.position):0.0} m"); }
                _lastCombatTime = Time.time;
                CancelAllIdleBehaviors();
                return false;
            }

            if (Time.time - _lastCombatTime < combatCooldownForIdle)
            {
                return false;
            }

            switch (_currentIdleState)
            {
                case IdleState.Standing:
                    HandleStandingState();
                    return false;

                case IdleState.LookingAround:
                    HandleLookingAroundState();
                    return false;

                case IdleState.Wandering:
                    HandleWanderingState();
                    return true;

                case IdleState.WaitingAtWanderPoint:
                    HandleWaitingState();
                    return false;

                case IdleState.PlayingEmote:
                    HandleEmoteState();
                    return false;

                case IdleState.SittingOnChair:
                    HandleChairState();
                    return false;

                case IdleState.SubBehavior:
                    return true;
            }

            return false;
        }

        public void OnCombatStarted()
        {
            if (Time.time - _lastOnCombatStartedCall < MinOnCombatStartedInterval)
                return;
                
            _lastOnCombatStartedCall = Time.time;
            
            bool shouldLog = VerboseLogging && _companion != null && _companion.ShouldBeFollowing;
            if (shouldLog)
                Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} OnCombatStarted - cancelling idle behaviors");
            
            _lastCombatTime = Time.time;
            
            // Clear any active emote: a loop left running here slid across the ground into combat.
            if (_isPlayingEmote && !string.IsNullOrEmpty(_currentEmote))
            {
                PlayerAnimationCatalog.StopEmotes(_zanim, _animator);
                if (shouldLog)
                    Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} cleared combat-interrupted emote: {_currentEmote}");
            }
            _isPlayingEmote = false;
            _currentEmote = "";
            _isPersistentEmote = false;
            
            // Interrupt sub-behaviors
            if (_activeSubBehavior != null && _activeSubBehavior.IsActive)
            {
                if (_activeSubBehavior.SupportsResumption)
                {
                    _activeSubBehavior.InterruptForCombat();
                    
                    if (shouldLog)
                        Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} sub-behavior {_activeSubBehavior.BehaviorName} interrupted for combat - state saved");
                }
                else
                {
                    CancelActiveSubBehavior("combat (it can't resume)");
                }
            }
            else
            {
                CancelAllIdleBehaviors();
            }
        }

        public void OnCombatEnded()
        {
            _lastCombatTime = Time.time;
            _idleStartTime = Time.time;
            _terrainCheckedThisIdleSession = false;
            _holsterDecisionMade = false;
            
            if (_activeSubBehavior != null && _activeSubBehavior.WasInterruptedByCombat && _activeSubBehavior.SupportsResumption)
            {
                _activeSubBehavior.ResumeAfterCombat();
                
                bool shouldLog = VerboseLogging && _companion != null && _companion.ShouldBeFollowing;
                if (shouldLog)
                    Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} resuming {_activeSubBehavior.BehaviorName} after combat");
            }
        }
        
        private bool IsActivelyFollowing()
        {
            if (_companion == null) return false;
            return _companion.ShouldBeFollowing;
        }

        public string GetIdleStateDescription()
        {
            if (_activeSubBehavior != null)
            {
                return _activeSubBehavior.GetStatusDescription();
            }

            return _currentIdleState switch
            {
                IdleState.Standing => _weaponsHolstered ? "Relaxing" : "Standing",
                IdleState.LookingAround => "Looking around",
                IdleState.Wandering => "Wandering",
                IdleState.WaitingAtWanderPoint => "Resting",
                IdleState.PlayingEmote => "Relaxing",
                IdleState.SittingOnChair => "Sitting",
                IdleState.SubBehavior => "Busy",
                _ => "Idle"
            };
        }

        public IdleState CurrentIdleState => _currentIdleState;
        public bool IsPerformingIdleActivity => _currentIdleState != IdleState.Standing;
        public bool IsSitting => _isSittingOnChair;
        public bool IsPlayingEmote => _isPlayingEmote;
        
        /// <summary>
        /// Returns true if the companion is in an emote that requires complete movement freeze.
        /// </summary>
        public bool IsEmoteFrozen => _isPlayingEmote && _isPersistentEmote;
        
        public bool AreWeaponsHolstered => _weaponsHolstered;
        /// <summary>
        /// Returns true if companion is currently executing a sub-behavior.
        /// CRITICAL: This is the SINGLE SOURCE OF TRUTH for sub-behavior status.
        /// Used by CompanionCommandCoordinator.IsCompanionBusy() to check if companion is available.
        /// 
        /// IMPORTANT: Both _activeSubBehavior != null AND IsActive must be true.
        /// This ensures we don't report busy if the behavior finished but wasn't cleared yet.
        /// </summary>
        public bool IsInSubBehavior => _activeSubBehavior != null && _activeSubBehavior.IsActive;
        
        /// <summary>
        /// The currently active sub-behavior, if any.
        /// </summary>
        public IdleSubBehavior ActiveSubBehavior => _activeSubBehavior;
        
        public bool IsPlayerInteracting => _isPlayerInteracting;

        #endregion

        #region State Handlers

        private void HandleStandingState()
        {
            if (!_terrainCheckedThisIdleSession)
            {
                PerformTerrainCheck();
                _terrainCheckedThisIdleSession = true;
            }

            // CRITICAL FIX (Bug #13): Implement proper standing pauses
            // Companions should STOP and WAIT before making new decisions, not constantly move around
            
            bool isStationary = IsCompanionStationary();
            
            // If we're not stationary yet, wait for the companion to stop moving
            if (!isStationary)
            {
                _isInStandingPause = false;
                _standingPauseStartTime = Time.time;
                return;
            }
            
            // Start a standing pause if we just became stationary
            if (!_isInStandingPause)
            {
                _isInStandingPause = true;
                _standingPauseStartTime = Time.time;
                
                // Calculate how long to pause before making the next decision
                // This creates the "stop, wait, then decide" behavior
                float pauseDuration = UnityEngine.Random.Range(_standingPauseMinDuration, _standingPauseMaxDuration);
                // [ghost] 0.2.843 (Fire's log: 199 standing pauses averaging 26 s, up to 45 s, before each decision - 5,160 s of standing
                // across 5 companions in ~20 min): a staying companion whose last chore round found work pauses 2-5 s; after an empty
                // round 6-15 s. Followers keep the old 8-45 s.
                if (_hasHomePosition && _companion != null && !_companion.ShouldBeFollowing)
                    pauseDuration = _lastRoundFoundWork ? UnityEngine.Random.Range(2f, 5f) : UnityEngine.Random.Range(6f, 15f);
                _nextDecisionTime = Time.time + pauseDuration;
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} starting standing pause for {pauseDuration:F1}s");
                
                return;
            }
            
            // We're in a standing pause - wait until it's time to make a decision
            if (Time.time < _nextDecisionTime)
            {
                // During the pause, we can still do minor things like look around occasionally
                // But NO wandering or major behaviors
                if (UnityEngine.Random.value < 0.01f) // 1% chance per frame to look around
                {
                    FindAndLookAtInterest();
                }
                return;
            }
            
            // Standing pause is complete - now we can make an idle behavior decision
            _isInStandingPause = false;
            _lastIdleBehaviorCheck = Time.time;
            TryStartIdleBehavior();
        }
        
        private bool IsCompanionStationary()
        {
            if (_rigidbody != null)
            {
                Vector3 horizontalVel = new Vector3(_rigidbody.linearVelocity.x, 0, _rigidbody.linearVelocity.z);
                if (horizontalVel.magnitude > MinVelocityForStationary)
                    return false;
            }
            
            if (_character != null)
            {
                Vector3 charVel = _character.GetVelocity();
                Vector3 horizontalCharVel = new Vector3(charVel.x, 0, charVel.z);
                if (horizontalCharVel.magnitude > MinVelocityForStationary)
                    return false;
            }
            
            if (_hasActiveDestination)
                return false;
            
            if (_isRotating)
                return false;
            
            return true;
        }
        
        /// <summary>
        /// Checks if any player is within PlayerProximityThreshold meters.
        /// Used for zone-proximity gating to skip expensive idle evaluation
        /// (sub-behavior CanStart, Physics.OverlapSphere, chest scanning) when
        /// companions are far from all players. Companions will still wander
        /// and emote, just won't evaluate complex work behaviors.
        /// </summary>
        private bool IsAnyPlayerNearby()
        {
            Vector3 myPos = transform.position;
            float thresholdSq = PlayerProximityThreshold * PlayerProximityThreshold;
            
            foreach (var player in Player.GetAllPlayers())
            {
                if (player == null) continue;
                float distSq = (player.transform.position - myPos).sqrMagnitude;
                if (distSq <= thresholdSq)
                    return true;
            }
            
            return false;
        }

        private void HandleLookingAroundState()
        {
            if (Time.time >= _lookAroundEndTime)
            {
                SetIdleState(IdleState.Standing);
            }
        }

        private void HandleWanderingState()
        {
            if (_hasActiveDestination)
            {
                float distToTarget = Vector3.Distance(transform.position, _currentDestination);
                if (distToTarget < _destinationReachedThreshold)
                {
                    float pauseDuration = UnityEngine.Random.Range(wanderPauseDuration, wanderPauseMaxDuration);
                    _idleBehaviorEndTime = Time.time + pauseDuration;
                    _hasActiveDestination = false;
                    _currentWanderLeg++;
                    
                    if (_companionAI != null)
                        _companionAI.ClearIdleDestination();
                    
                    SetIdleState(IdleState.WaitingAtWanderPoint);
                }
            }
            else
            {
                SetIdleState(IdleState.Standing);
            }
        }

        private void HandleWaitingState()
        {
            if (Time.time >= _idleBehaviorEndTime)
            {
                if (sitOnChairsWhenIdle && 
                    Time.time - _lastChairCheckTime >= chairSitCooldown &&
                    UnityEngine.Random.value < sitChanceWhileWandering)
                {
                    _lastChairCheckTime = Time.time;
                    if (TryFindAndSitOnChair())
                    {
                        _currentWanderLeg = 0;
                        return;
                    }
                }
                
                if (WanderAllowed && _currentWanderLeg < _totalWanderLegs && UnityEngine.Random.value < continueWanderChance)
                {
                    StartNextWanderLeg();
                    SetIdleState(IdleState.Wandering);
                }
                else
                {
                    _currentWanderLeg = 0;
                    _lastWanderCompleteTime = Time.time;
                    SetIdleState(IdleState.Standing);
                }
            }
        }

        private void HandleEmoteState()
        {
            if (Time.time >= _emoteEndTime)
            {
                ForceEndEmote();
            }
        }

        private void HandleChairState()
        {
            // Enforce movement lock while sitting
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            
            if (_character != null && (_rigidbody == null || !_rigidbody.isKinematic))
            {
                _character.SetMoveDir(Vector3.zero);
            }
            
            if (Time.time >= _chairSitEndTime || Time.time >= _chairHardTimeoutTime)
            {
                ForceStandUp();
            }
        }

        private void SetIdleState(IdleState newState)
        {
            if (newState == _currentIdleState) return;

            bool shouldLog = VerboseLogging && _companion != null && _companion.ShouldBeFollowing;
            if (shouldLog)
                Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} idle state: {_currentIdleState} -> {newState}");

            _currentIdleState = newState;
            _currentStateStartTime = Time.time;
            
            _isInStandingPause = false;
            _standingPauseStartTime = Time.time;
        }

        #endregion

        #region Idle Behavior Triggers

        private void TryStartIdleBehavior()
        {
            // Priority 0: Sub-behaviors (only when near a player - these do expensive
            // Physics.OverlapSphere, chest scanning, etc. in their CanStart checks)
            // [ghost] 0.2.843 (Fire 10-26: "companions ... are all mostly idle"; his log: Knut stood through 41 pauses, 955 s, with NOT ONE
            // chore round - he was 64-180 m from Fire, and this gate skips chores without a player within 64 m; Fire earlier: staying
            // companions "should work regardless of owner around"): a staying companion tries its chores wherever the owner is. The
            // scan cost is bounded by TryStartSubBehavior's cooldown and empty-round back-off.
            bool staying = _hasHomePosition && _companion != null && !_companion.ShouldBeFollowing;
            if ((_isNearAnyPlayer || staying) && TryStartSubBehavior())
                return;

            // Priority 1: Check for chairs
            if (sitOnChairsWhenIdle &&
                Time.time - _lastChairCheckTime >= chairSitCooldown &&
                Time.time - _lastCombatTime >= combatCooldownForIdle * 2f)
            {
                _lastChairCheckTime = Time.time;
                if (TryFindAndSitOnChair())
                    return;
            }

            // Priority 2: Emotes
            if (Time.time >= _nextEmoteTime)
            {
                if (UnityEngine.Random.value < idleEmoteChance)
                {
                    StartIdleEmote();
                    return;
                }
                _nextEmoteTime = Time.time + UnityEngine.Random.Range(idleEmoteMinInterval, idleEmoteMaxInterval);
            }

            // Priority 3: Wander
            if (UnityEngine.Random.value < idleWanderChance)
            {
                StartIdleWander();
                return;
            }

            // Priority 4: Look around
            if (UnityEngine.Random.value < idleLookAroundChance)
            {
                StartLookAround();
            }
        }

        /// <summary>
        /// Cancels all idle behaviors including sub-behaviors, emotes, sitting, etc.
        /// Called when combat starts or when the companion needs to respond to commands.
        /// </summary>
        public void CancelAllIdleBehaviors()
        {
            // Release all interactable occupancies
            if (_character != null)
            {
                InteractableOccupancyManager.ReleaseAllForOccupant(_character);
            }
            
            // Force reset state controller (but not if executing PlayerCommand)
            if (_stateController != null && 
                _stateController.CurrentState >= CompanionStateController.CompanionState.SubBehavior &&
                _stateController.CurrentState < CompanionStateController.CompanionState.PlayerCommand)
            {
                _stateController.ForceReset();
            }
            
            // The emote itself is ended by ForceAnimationStateReset below.
            _isPlayingEmote = false;
            _currentEmote = "";
            _isPersistentEmote = false;
            _currentWanderLeg = 0;
            _hasActiveDestination = false;
            _isRotating = false;
            _terrainCheckedThisIdleSession = false;
            _holsterDecisionMade = false;
            
            if (_combatMovement != null && _combatMovement.IsMovementLocked)
            {
                _combatMovement.UnlockMovement();
            }

            if (_companionAI != null)
                _companionAI.ClearIdleDestination();

            if (_isSittingOnChair)
                StandUpFromChair();

            CancelActiveSubBehavior($"CancelAllIdleBehaviors from {CallerChain()}");

            if (_interactionBehavior != null)
            {
                _interactionBehavior.OnCombatStarted();
            }

            if (_weaponsHolstered)
            {
                UnholsterWeapons();
            }

            ForceAnimationStateReset();

            _currentIdleState = IdleState.Standing;
            _currentStateStartTime = Time.time;
        }

        #endregion

        // a pose (emote, sit, rest, chair) left on while a job runs is ended; logged once per episode
        private object _jobPoseCheckedFor;
        private void EndPoseForJob(string job)
        {
            bool emoting = false;
            try { emoting = FiresCore.Npc.Animation.PlayerAnimationCatalog.IsEmoting(_zanim, _animator); } catch { }
            bool stateSit = _stateController != null && (_stateController.CurrentState == CompanionStateController.CompanionState.Emote || _stateController.CurrentState == CompanionStateController.CompanionState.ChairSit);
            if (!emoting && !stateSit && !_isSittingOnChair) return;
            {
                FiresCore.Logging.FiresLogger.LogDebug($"[JobPose] {_companion?.companionName}: job {job} started/running in a pose (animator emote {emoting}, state {_stateController?.CurrentState}, chair {_isSittingOnChair}); standing up first");
            }
            if (_isSittingOnChair) StandUpFromChair();
            _stateController?.ForceStopEmote();
            if (_stateController == null) { try { FiresCore.Npc.Animation.PlayerAnimationCatalog.StopEmotes(_zanim, _animator); } catch { } }
        }

        #region UI Interaction Freeze
        
        private bool IsPlayerInteractingWithUI()
        {
            if (!FiresCore.Bridge.ModUiRegistry.IsAnyOpen())
                return false;

            if (FiresCore.Bridge.NpcUiBridge.IsInventoryOpenFor(gameObject))
                return true;

            if (FiresCore.Bridge.NpcUiBridge.IsStatsOpenFor(gameObject))
                return true;

            if (_npcModule == null)
                _npcModule = GetComponent<NpcMode.CompanionNpcModule>();
            if (_npcModule != null && _npcModule.IsStationedAsNpc)
            {
                // Any NPC UI being open should freeze ALL stationed NPCs.
                // Without this, stationed NPCs continue full Update loops
                // (AI, combat movement, idle behaviors, head look-at, etc.)
                // which causes severe FPS drops when quest/dialogue/trader UIs are open.
                // Any registered mod UI being open freezes the stationed NPC (conservative; the
                // host's NPC-interaction screens register with ModUiRegistry).
                if (FiresCore.Bridge.ModUiRegistry.IsAnyOpen())
                    return true;
            }

            return false;
        }
        
        private void EnforceInteractionFreeze()
        {
            if (!_isPlayerInteracting)
            {
                _isPlayerInteracting = true;
                _interactionStartTime = Time.time;
                
                CancelAllIdleBehaviors();
                
                if (_stateController != null)
                {
                    _stateController.TryEnterState(CompanionStateController.CompanionState.UIInteraction, 999f, "PlayerInteraction");
                }
                
                if (_combatMovement != null)
                {
                    _combatMovement.LockMovement("PlayerInteraction", 999f);
                }
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} frozen for player interaction");
            }
            
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            
            if (_character != null && (_rigidbody == null || !_rigidbody.isKinematic))
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }
        }
        
        private void OnInteractionEnded()
        {
            _isPlayerInteracting = false;
            
            if (_stateController != null)
            {
                _stateController.ExitState(CompanionStateController.CompanionState.Idle);
            }
            
            if (_combatMovement != null && _combatMovement.IsMovementLocked)
            {
                _combatMovement.UnlockMovement();
            }
            
            _lastIdleBehaviorCheck = Time.time;
            _nextEmoteTime = Time.time + UnityEngine.Random.Range(idleEmoteMinInterval, idleEmoteMaxInterval);
            
            if (VerboseLogging)
                Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} unfrozen after player interaction");
        }
        
        public void FreezeForInteraction()
        {
            _isPlayerInteracting = true;
            _interactionStartTime = Time.time;
            CancelAllIdleBehaviors();
            
            if (_combatMovement != null)
            {
                _combatMovement.LockMovement("PlayerInteraction", 999f);
            }
        }
        
        public void UnfreezeAfterInteraction()
        {
            OnInteractionEnded();
        }
        
        #endregion

        #region Smooth Rotation

        private void StartLookAround()
        {
            float targetAngle = transform.eulerAngles.y + UnityEngine.Random.Range(-90f, 90f);
            StartSmoothRotation(targetAngle, idleRotationSpeed);

            _lookAroundEndTime = Time.time + _rotationDuration + 1f;
            SetIdleState(IdleState.LookingAround);
        }

        private void StartSmoothRotation(float targetYAngle, float speedDegreesPerSec)
        {
            _startYRotation = transform.eulerAngles.y;
            _targetYRotation = targetYAngle;
            _isRotating = true;
            _rotationStartTime = Time.time;

            float angleDiff = Mathf.Abs(Mathf.DeltaAngle(_startYRotation, targetYAngle));
            _rotationDuration = angleDiff / speedDegreesPerSec;
        }
        
        /// <summary>
        /// Finds something interesting to look at during standing pause.
        /// Just uses head look-at, no body rotation.
        /// </summary>
        private void FindAndLookAtInterest()
        {
            if (!enableHeadLookAt) return;
            if (_lookAtTarget != null) return; // Already looking at something
            
            // Look for players, other companions, or interesting objects
            var colliders = Physics.OverlapSphere(transform.position, lookAtDetectionRange);
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                // Check for players
                var player = collider.GetComponent<Player>();
                if (player != null && player != _companion?.GetOwner())
                {
                    Vector3 toTarget = player.transform.position - transform.position;
                    float angle = Vector3.Angle(transform.forward, toTarget);
                    if (angle <= maxLookAtAngle)
                    {
                        _lookAtTarget = player.transform;
                        _lookAtEndTime = Time.time + lookAtDuration;
                        _targetHeadLookWeight = 1f;
                        return;
                    }
                }
                
                // Check for other companions
                var companion = collider.GetComponent<CompanionController>();
                if (companion != null && companion != _companion)
                {
                    Vector3 toTarget = companion.transform.position - transform.position;
                    float angle = Vector3.Angle(transform.forward, toTarget);
                    if (angle <= maxLookAtAngle)
                    {
                        _lookAtTarget = companion.transform;
                        _lookAtEndTime = Time.time + lookAtDuration;
                        _targetHeadLookWeight = 1f;
                        return;
                    }
                }
            }
        }

        private void UpdateSmoothRotation()
        {
            // Single-writer (facing): yield the body's rotation to any active sub-behavior (bow
            // training, gathering, station work) — that behavior owns facing while it runs. Clear the
            // in-flight flag too, so an idle look-around that began before the sub-behavior started
            // does not keep fighting the task's facing frame-for-frame. Mirrors the head-look gate at
            // UpdateHeadLookAt (skipped while _activeSubBehavior != null).
            if (_activeSubBehavior != null) { _isRotating = false; return; }

            if (!_isRotating) return;

            float elapsed = Time.time - _rotationStartTime;
            float progress = Mathf.Clamp01(elapsed / _rotationDuration);

            float newAngle = Mathf.LerpAngle(_startYRotation, _targetYRotation, progress);
            transform.rotation = Quaternion.Euler(0, newAngle, 0);

            if (progress >= 1f)
            {
                _isRotating = false;
            }
        }

        #endregion
    }
}
