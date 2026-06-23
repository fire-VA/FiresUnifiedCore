using UnityEngine;
using System;
using System.Collections.Generic;
using FiresCore.Npc.Combat;
using FiresCore.Npc.IdleBehaviors;
using FiresCore.Npc.Movement;
using FiresCore.Npc.NpcMode;

namespace FiresCore.Npc
{
    /// <summary>
    /// Handles idle behaviors for companion NPCs when not in combat or actively following.
    /// This includes wandering, emotes, chair sitting, head look-at, weapon holstering,
    /// and modular sub-behaviors like bow training.
    /// 
    /// This is a partial class split across multiple files for maintainability:
    /// - CompanionIdleBehavior.cs: Core class, fields, Unity lifecycle
    /// - CompanionIdleBehavior.SubBehaviors.cs: Sub-behavior system
    /// - CompanionIdleBehavior.Emotes.cs: Emote playback and management
    /// - CompanionIdleBehavior.Chairs.cs: Chair sitting logic
    /// - CompanionIdleBehavior.Wandering.cs: Wandering and path preference
    /// - CompanionIdleBehavior.HeadLook.cs: Head look-at system
    /// - CompanionIdleBehavior.StuckPrevention.cs: Stuck detection and animation reset
    /// 
    /// DESIGN PRINCIPLE:
    /// Idle behaviors use committed movements - set a destination ONCE and let the NPC walk there smoothly.
    /// This creates natural, purposeful movement instead of jittery frame-by-frame updates.
    /// 
    /// TERRAIN CHECKS:
    /// Terrain/path scoring is EXPENSIVE and causes jerky movement if done frequently.
    /// We check terrain ONCE when becoming idle, then commit to decisions until the next idle period.
    /// 
    /// Combat can interrupt idle behaviors at any time via CancelAllIdleBehaviors().
    /// 
    /// SUB-BEHAVIORs:
    /// Complex idle activities (training, crafting, etc.) are handled by IdleSubBehavior classes.
    /// These are checked and potentially started during idle periods.
    /// 
    /// EMOTE TIMEOUT:
    /// All emotes and sitting states have a HARD timeout to prevent getting stuck.
    /// After the timeout, we forcibly reset to Standing state.
    /// 
    /// WEAPON HOLSTERING:
    /// When idle, weapons are moved from hand slots to back slots in CompanionInventory.
    /// This is a real inventory operation, not just visual - prevents duplication.
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
        private const float MIN_ON_COMBAT_STARTED_INTERVAL = 2f;

        // Look around state
        private bool _isLookingAround;
        private float _lookAroundEndTime;

        // Wander state
        private bool _isWandering;
        private bool _isWaitingAtWanderPoint;
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
        private const float MIN_WANDER_COOLDOWN = 4f;

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
        private Quaternion _headBaseRotation;
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
        private float _chairSitEndTime;
        private float _chairHardTimeoutTime;
        private float _lastChairCheckTime;
        private GameObject _currentChairObject;
        
        // UI interaction freeze
        private bool _isPlayerInteracting;
        private float _interactionStartTime;
        
        // STANDING PAUSE - How long to wait before making new idle decisions
        private float _standingPauseStartTime;
        private float _standingPauseMinDuration = 8f;   // Minimum 8 seconds standing still
        private float _standingPauseMaxDuration = 45f;  // Maximum 45 seconds standing still
        private float _nextDecisionTime;
        private bool _isInStandingPause = false;
        private const float MIN_VELOCITY_FOR_STATIONARY = 0.1f;

        // Weapon holstering
        private bool _weaponsHolstered = false;
        private bool _holsterDecisionMade = false;

        // Sub-behaviors
        private List<IdleSubBehavior> _subBehaviors = new List<IdleSubBehavior>();
        private IdleSubBehavior _activeSubBehavior;
        private float _lastSubBehaviorAttempt;

        public static bool VerboseLogging = false;
        
        // ZONE-PROXIMITY GATING - skip expensive idle evaluation when far from all players
        private const float PLAYER_PROXIMITY_CHECK_INTERVAL = 5f;
        private const float PLAYER_PROXIMITY_THRESHOLD = 64f; // Same as zone load distance
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
            // Static placed NPCs have no CompanionController ÃƒÂ¯Ã‚Â¿Ã‚Â½ allow them through
            // if they're stationed with idle wandering enabled
            if (_companion == null)
            {
                if (_npcModule == null)
                    _npcModule = GetComponent<CompanionNpcModule>();
                if (_npcModule == null || !_npcModule.isStaticPlacement || !_npcModule.allowIdleWandering)
                    return;
            }
            else if (_companion.isStaticPlacement)
            {
                // Static placement with CompanionController ÃƒÂ¯Ã‚Â¿Ã‚Â½ only run if wandering is enabled
                if (_npcModule == null)
                    _npcModule = GetComponent<CompanionNpcModule>();
                if (_npcModule == null || (!_npcModule.allowIdleWandering && !HasPatrolRoute()))
                    return;
            }
            else if (!_companion.isTamed)
            {
                return;
            }
            
            if (_stateController == null)
                _stateController = GetComponent<CompanionStateController>();
            
            // ZONE-PROXIMITY GATING: Periodically check if any player is nearby.
            // When far from all players, skip expensive idle evaluation (sub-behavior
            // CanStart checks that do Physics.OverlapSphere, chest scanning, etc.)
            if (Time.time - _lastProximityCheckTime >= PLAYER_PROXIMITY_CHECK_INTERVAL)
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
            CheckForStuckState();

            // Patrol: keep an assigned route running (force-started, not part of the random rotation).
            TryStartPatrolIfAssigned();

            // Update active sub-behavior
            UpdateActiveSubBehavior();

            // Update head look-at (skip during sub-behaviors)
            if (_activeSubBehavior == null)
                UpdateHeadLookAt();

            // Update smooth rotation
            UpdateSmoothRotation();

            // Update weapon holstering
            UpdateWeaponHolstering();
        }

        private void LateUpdate()
        {
            ApplyHeadLookAt();
        }

        #endregion

        #region Public API

        public bool UpdateIdleBehavior()
        {
            // Check if stationed as NPC without idle wandering
            if (_npcModule == null)
                _npcModule = GetComponent<CompanionNpcModule>();
            if (_npcModule != null && _npcModule.IsStationedAsNpc && !_npcModule.allowIdleWandering)
            {
                return false;
            }

            // Static placed NPCs should not wander until explicitly enabled
            if (_companion != null && _companion.isStaticPlacement)
            {
                if (_npcModule == null || !_npcModule.allowIdleWandering)
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

            if (_combatMovement != null && _combatMovement.IsInCombat)
            {
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
            if (Time.time - _lastOnCombatStartedCall < MIN_ON_COMBAT_STARTED_INTERVAL)
                return;
                
            _lastOnCombatStartedCall = Time.time;
            
            bool shouldLog = VerboseLogging && _companion != null && _companion.ShouldBeFollowing;
            if (shouldLog)
                Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} OnCombatStarted - cancelling idle behaviors");
            
            _lastCombatTime = Time.time;
            
            // Clear any active emote
            if (_isPlayingEmote && !string.IsNullOrEmpty(_currentEmote))
            {
                if (_zanim != null)
                    _zanim.SetBool(_currentEmote, false);
                if (_animator != null && HasAnimatorParameter(_currentEmote))
                    _animator.SetBool(_currentEmote, false);
                    
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
                    CancelActiveSubBehavior();
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
                if (horizontalVel.magnitude > MIN_VELOCITY_FOR_STATIONARY)
                    return false;
            }
            
            if (_character != null)
            {
                Vector3 charVel = _character.GetVelocity();
                Vector3 horizontalCharVel = new Vector3(charVel.x, 0, charVel.z);
                if (horizontalCharVel.magnitude > MIN_VELOCITY_FOR_STATIONARY)
                    return false;
            }
            
            if (_hasActiveDestination)
                return false;
            
            if (_isRotating)
                return false;
            
            return true;
        }
        
        /// <summary>
        /// Checks if any player is within PLAYER_PROXIMITY_THRESHOLD meters.
        /// Used for zone-proximity gating to skip expensive idle evaluation
        /// (sub-behavior CanStart, Physics.OverlapSphere, chest scanning) when
        /// companions are far from all players. Companions will still wander
        /// and emote, just won't evaluate complex work behaviors.
        /// </summary>
        private bool IsAnyPlayerNearby()
        {
            Vector3 myPos = transform.position;
            float thresholdSq = PLAYER_PROXIMITY_THRESHOLD * PLAYER_PROXIMITY_THRESHOLD;
            
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
                _isLookingAround = false;
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
                    _isWaitingAtWanderPoint = true;
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
                        _isWandering = false;
                        _isWaitingAtWanderPoint = false;
                        _currentWanderLeg = 0;
                        return;
                    }
                }
                
                if (_currentWanderLeg < _totalWanderLegs && UnityEngine.Random.value < continueWanderChance)
                {
                    _isWaitingAtWanderPoint = false;
                    StartNextWanderLeg();
                    SetIdleState(IdleState.Wandering);
                }
                else
                {
                    _isWandering = false;
                    _isWaitingAtWanderPoint = false;
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
            // Priority 0: Sub-behaviors (only when near a player ÃƒÂ¯Ã‚Â¿Ã‚Â½ these do expensive
            // Physics.OverlapSphere, chest scanning, etc. in their CanStart checks)
            if (_isNearAnyPlayer && TryStartSubBehavior())
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
            
            // Clear emote animation bool
            if (_isPlayingEmote && !string.IsNullOrEmpty(_currentEmote))
            {
                if (_zanim != null)
                {
                    _zanim.SetBool(_currentEmote, false);
                }
                if (_animator != null && HasAnimatorParameter(_currentEmote))
                {
                    _animator.SetBool(_currentEmote, false);
                }
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} cleared emote bool: {_currentEmote}");
            }
            
            _isLookingAround = false;
            _isWandering = false;
            _isWaitingAtWanderPoint = false;
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

            CancelActiveSubBehavior();

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

        #region UI Interaction Freeze
        
        private bool IsPlayerInteractingWithUI()
        {
            if (!FiresCore.Bridge.ModUiRegistry.IsAnyOpen())
                return false;

            if (FiresCore.Bridge.NpcUiBridge.IsInventoryOpenFor(_companion.gameObject))
                return true;

            if (FiresCore.Bridge.NpcUiBridge.IsStatsOpenFor(_companion.gameObject))
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

            _isLookingAround = true;
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
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                // Check for players
                var player = col.GetComponent<Player>();
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
                var companion = col.GetComponent<CompanionController>();
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
            if (!_isRotating) return;

            float elapsed = Time.time - _rotationStartTime;
            float t = Mathf.Clamp01(elapsed / _rotationDuration);

            float newAngle = Mathf.LerpAngle(_startYRotation, _targetYRotation, t);
            transform.rotation = Quaternion.Euler(0, newAngle, 0);

            if (t >= 1f)
            {
                _isRotating = false;
            }
        }

        #endregion
    }
}
