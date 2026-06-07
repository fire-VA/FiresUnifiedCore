using UnityEngine;
using System;
using FiresCore.Npc.Combat;
using FiresCore.Npc.Movement;
using FiresCore.Npc.AI;
using FiresCore.Npc.Core;

namespace FiresCore.Npc
{
    /// <summary>
    /// Handles movement for companion NPCs during combat and following.
    /// Coordinates with handlers: FleeMovementHandler, CommandMovementHandler, 
    /// PlayerIdleHandler, StuckDetectionHandler, CombatMovementHandler.
    /// 
    /// PARTIAL CLASS STRUCTURE:
    /// - CompanionCombatMovement.cs - Core fields, settings, initialization, Unity lifecycle
    /// - CompanionCombatMovement.Combat.cs - Combat state, commitment system, emergency actions
    /// - CompanionCombatMovement.Following.cs - Follow state, player idle handling, non-combat intent
    /// - CompanionCombatMovement.Movement.cs - Movement mode application, velocity clamping
    /// - CompanionCombatMovement.Jump.cs - Jump logic, stuck detection, grounded state
    /// - CompanionCombatMovement.Commands.cs - Command handling, priority targets, movement lock
    /// </summary>
    public partial class CompanionCombatMovement : MonoBehaviour
    {
        #region Settings

        [Header("Follow Distance Thresholds - Buffer Zones")]
        public float followStopDistanceInner = 2f;
        public float followStopDistanceOuter = 3.5f;
        public float followWalkDistanceInner = 3.5f;
        public float followWalkDistanceOuter = 5f;
        public float followJogDistanceInner = 6f;
        public float followJogDistanceOuter = 10f;
        public float followRunDistanceInner = 12f;
        public float followRunDistanceOuter = 18f;
        public float followSprintDistance = 30f;
        
        [Header("Player Idle Settings")]
        public float playerIdleTime = 3f;
        public float idleStopDistance = 6f;
        public float idleLookAroundChance = 0.15f;
        public float idleWanderChance = 0.05f;
        public float idleMaxWanderDistance = 6f;
        public float idleStopTimeMin = 2f;
        public float idleStopTimeMax = 10f;

        [Header("Combat Movement Settings")]
        public float meleeStafeDistance = 3f;
        public float strafeDirectionChangeInterval = 2f;
        public float maxStrafeDistance = 3f;

        [Header("Interception Settings")]
        public float ownerProtectionRadius = 15f;
        public float interceptionLeadTime = 0.5f;

        [Header("Combat Commitment Settings")]
        public float approachCommitmentDuration = 1.5f;
        public float strafeCommitmentDuration = 1.0f;
        public float maxCommitmentDuration = 4.0f;
        public float damageReassessDelay = 0.2f;
        public float projectileDetectionRange = 10f;

        [Header("State Transition Settings")]
        public float stateTransitionGracePeriod = 0.5f;
        public float minStateDuration = 0.3f;
        public float movementBlendSpeed = 5f;

        [Header("Combat End Detection")]
        public float combatEndCheckRange = 15f;
        public float combatEndGracePeriod = 3f;
        public float combatEndCheckInterval = 1f;
        
        [Header("Owner Distance Limits")]
        [Tooltip("Maximum distance from owner before companion abandons combat and returns")]
        public float maxOwnerCombatDistance = 25f;
        [Tooltip("Distance at which companion prefers to stay near owner during combat")]
        public float ownerLeashDistance = 18f;
        [Tooltip("Distance at which companion actively returns to owner")]
        public float ownerReturnDistance = 22f;

        [Header("Jump Settings")]
        public float stuckDetectionTime = 8.0f;
        public float stuckMovementThreshold = 2.0f;
        public float jumpCooldown = 5.0f;
        public float jumpForce = 8f;
        public float jumpForwardBoost = 3f;
        public float maxJumpableHeight = 1.5f;
        public float obstacleCheckDistance = 1.5f;
        public float minOwnerHeightDiffForJump = 1.0f;
        public int stuckChecksBeforeJump = 3;
        public float jumpAnimationDuration = 0.8f;

        [Header("Slow Movement Settings")]
        public float slowWalkSpeedMultiplier = 0.5f;
        public float slowdownDistance = 3f;

        #endregion

        #region Components

        private CompanionController _companion;
        private CompanionCombat _combat;
        private CompanionIdleBehavior _idleBehavior;
        private CompanionAI _companionAI;
        private CompanionStateController _stateController;
        private UnifiedMovementAuthority _movementAuthority;
        private Character _character;
        private Animator _animator;
        private ZSyncAnimation _zanim;
        private Rigidbody _rigidbody;
        private CombatContext _combatContext;

        // PERF: Cache reflection FieldInfo statically — avoids per-frame typeof().GetField() calls
        private static System.Reflection.FieldInfo _cachedContextField;
        private static bool _contextFieldResolved;
        private bool _combatContextLookupDone;

        private EnemyAttackRecognition _attackRecognition;
        private StaminaManager _staminaManager;
        private TerrainAwareness _terrainAwareness;
   
        // Ranged weapon behavior references for movement coordination
        private BowBehavior _bowBehavior;
        private CrossbowBehavior _crossbowBehavior;
        private StaffBehavior _staffBehavior;
        private bool _rangedBehaviorsSubscribed = false;

        // Extracted helper classes for cleaner code organization
        private MovementModeController _movementModeController;
        private FollowBehavior _followBehavior;
        
        // Refactored handlers - moving logic out of this monolithic class
        private FleeMovementHandler _fleeHandler;
        private CommandMovementHandler _commandHandler;
        private PlayerIdleHandler _playerIdleHandler;
        private StuckDetectionHandler _stuckHandler;
        private CombatMovementHandler _combatHandler;
        private StateTransitionHandler _stateTransitionHandler;
        private RangedMovementHandler _rangedHandler;
        private FollowIntentController _followIntentController;
        
        // State machine for cleaner Update() logic
        private MovementStateMachine _stateMachine;
        private MovementStateContext _stateContext;

        #endregion

        #region State

        private MovementIntent _currentIntent = MovementIntent.Idle;
        private MovementIntent _previousIntent = MovementIntent.Idle;
        private Character _currentTarget;
        private bool _isInCombat;
        private bool _wasInCombat;

        // SMOOTH TRANSITION STATE
        private float _lastStateChangeTime;
        private Vector3 _lastMoveDirection = Vector3.zero;
        private Vector3 _currentMoveDirection = Vector3.zero;
        private Vector3 _targetMoveDirection = Vector3.zero;
        private bool _isInTransition = false;
        private float _transitionStartTime;

        // COMMITTED COMBAT STATE
        private Character _committedTarget;
        private MovementIntent _committedIntent;
        private float _commitmentStartTime;
        private float _commitmentDuration;
        private bool _hasActiveCommitment = false;
        private float _lastDamageTime = -10f;
        private float _lastReassessTime;
        private Vector3 _committedMoveDirection;
  
        // RANGED WEAPON MOVEMENT REQUEST
        private bool _hasRangedMovementRequest = false;
        private float _rangedRequestTime = 0f;
        private const float RANGED_REQUEST_TIMEOUT = 0.5f;

        // Strafe state
        private int _strafeDirection;
        private float _strafeCommitEndTime;
        private bool _isStrafeCommitted = false;

        // COMBAT END DETECTION
        private float _lastEnemyKillTime = -100f;
        private float _lastCombatEndCheck;
        private bool _isInCombatCooldown = false;
        
        // PLAYER IDLE DETECTION
        private bool _isPlayerIdle = false;
        private bool _isRelaxedFollowing = false;

        // STUCK DETECTION
        private bool _isGrounded;
        private bool _shouldCheckStuck = false;
        
        // FOLLOW STATE
        private MovementIntent _lastFollowIntent = MovementIntent.Idle;
        private float _followIntentChangeTime = -10f;

        // Home position
        private Vector3 _homePosition;
        private bool _hasHomePosition = false;

        // Animation
        private static readonly int Hash_inair = Animator.StringToHash("inair");

        // MOVEMENT MODE TRACKING - Avoid spamming SetWalk/SetRun every frame
        private bool _lastWalkState = false;
        private bool _lastRunState = false;
        private bool _movementModeSet = false;
        
        // MOVE DIRECTION TRACKING - Avoid spamming SetMoveDir every frame  
        private Vector3 _lastSetMoveDir = Vector3.zero;
        private bool _moveDirSet = false;
        private const float MOVE_DIR_CHANGE_THRESHOLD = 0.05f;

        public static bool VerboseLogging = false;
        
        // MOVEMENT LOCK SYSTEM - Delegates to CompanionStateController
        // Local fields only used as fallback if _stateController is null
        private bool _localMovementLocked = false;
        private string _localMovementLockReason = "";
        private float _localMovementLockEndTime = 0f;

        #endregion

        #region Enums

        public enum MovementIntent
        {
            Idle,
            FollowingClose,
            FollowingMedium,
            FollowingFar,
            CatchingUp,
            CombatApproach,
            CombatStrafe,
            CombatIntercept,
            CombatRetreat,
            CombatChase,
            PlantedFiring,
            Repositioning,
            CombatDodge,
            CombatBlock,
            Transitioning,
        }

        public enum MovementMode
        {
            Stop,
            Walk,
            Jog,
            Run
        }

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _combat = GetComponent<CompanionCombat>();
            _idleBehavior = GetComponent<CompanionIdleBehavior>();
            _companionAI = GetComponent<CompanionAI>();
            _stateController = GetComponent<CompanionStateController>();
            _character = GetComponent<Character>();
            _animator = GetComponentInChildren<Animator>(true);
            _zanim = GetComponent<ZSyncAnimation>();
            _rigidbody = GetComponent<Rigidbody>();
            
            // Note: _movementAuthority is retrieved in Start() because it may be added
            // by CompanionController.InitializeCompanion() which runs after Awake()

            if (_character != null)
            {
                _character.m_onDamaged += OnDamageTaken;
            }
        }

        private void OnDestroy()
        {
            if (_character != null)
            {
                _character.m_onDamaged -= OnDamageTaken;
            }
            
            // Unsubscribe from ranged behaviors
            UnsubscribeFromRangedBehaviors();
        }

        private void Start()
        {
            _lastStateChangeTime = Time.time;

            if (_idleBehavior == null)
            {
                _idleBehavior = gameObject.AddComponent<CompanionIdleBehavior>();
            }
            
            // CRITICAL: Get movement authority here - it's added by CompanionController.InitializeCompanion()
            // which runs after Awake() but before Start()
            if (_movementAuthority == null)
            {
                _movementAuthority = GetComponent<UnifiedMovementAuthority>();
            }
            
            // If still null, try getting it from CompanionController
            if (_movementAuthority == null && _companion != null)
            {
                _movementAuthority = _companion.GetMovementAuthority();
            }

            InitializeAdvancedCombatSystems();
        }

        private void InitializeAdvancedCombatSystems()
        {
            _attackRecognition = GetComponent<EnemyAttackRecognition>();
            if (_attackRecognition == null)
                _attackRecognition = gameObject.AddComponent<EnemyAttackRecognition>();

            _staminaManager = GetComponent<StaminaManager>();
            if (_staminaManager == null)
                _staminaManager = gameObject.AddComponent<StaminaManager>();

            _terrainAwareness = GetComponent<TerrainAwareness>();
            if (_terrainAwareness == null)
                _terrainAwareness = gameObject.AddComponent<TerrainAwareness>();

            if (_companionAI == null)
            {
                _companionAI = GetComponent<CompanionAI>();
            }
  
            if (_character != null && _rigidbody != null)
            {
                _movementModeController = new MovementModeController(_character, _rigidbody);
                _movementModeController.SlowWalkSpeedMultiplier = slowWalkSpeedMultiplier;
                _movementModeController.SlowdownDistance = slowdownDistance;
                _movementModeController.MovementBlendSpeed = movementBlendSpeed;
            }
 
            if (_character != null)
            {
                _followBehavior = new FollowBehavior(transform, _character, _rigidbody, _zanim, _animator);
                
                // Sync buffer zone thresholds
                _followBehavior.StopDistanceInner = followStopDistanceInner;
                _followBehavior.StopDistanceOuter = followStopDistanceOuter;
                _followBehavior.WalkDistanceInner = followWalkDistanceInner;
                _followBehavior.WalkDistanceOuter = followWalkDistanceOuter;
                _followBehavior.JogDistanceInner = followJogDistanceInner;
                _followBehavior.JogDistanceOuter = followJogDistanceOuter;
                _followBehavior.RunDistanceInner = followRunDistanceInner;
                _followBehavior.RunDistanceOuter = followRunDistanceOuter;
                _followBehavior.SprintDistance = followSprintDistance;
                
                // Stuck detection settings
                _followBehavior.StuckDetectionTime = stuckDetectionTime;
                _followBehavior.StuckMovementThreshold = stuckMovementThreshold;
                _followBehavior.StuckChecksBeforeJump = stuckChecksBeforeJump;
                
                // Jump settings
                _followBehavior.JumpCooldown = jumpCooldown;
                _followBehavior.JumpForce = jumpForce;
                _followBehavior.JumpForwardBoost = jumpForwardBoost;
                _followBehavior.MaxJumpableHeight = maxJumpableHeight;
                _followBehavior.ObstacleCheckDistance = obstacleCheckDistance;
                _followBehavior.MinOwnerHeightDiffForJump = minOwnerHeightDiffForJump;
                _followBehavior.JumpAnimationDuration = jumpAnimationDuration;
            }
            
            InitializeMovementHandlers();
        }
        
        /// <summary>
        /// Initializes the extracted movement handler classes.
        /// </summary>
        private void InitializeMovementHandlers()
        {
            // Flee handler - manages escape movement
            _fleeHandler = new FleeMovementHandler(
                transform,
                _character,
                _companion,
                _terrainAwareness);
            FleeMovementHandler.VerboseLogging = VerboseLogging;
            
            // Command handler - manages player command movement
            _commandHandler = new CommandMovementHandler(
                transform,
                _character,
                _companion,
                _companionAI,
                _idleBehavior);
            CommandMovementHandler.VerboseLogging = VerboseLogging;
            
            // Player idle handler - manages relaxed following behavior  
            _playerIdleHandler = new PlayerIdleHandler(
                transform,
                _character,
                _companion);
            _playerIdleHandler.PlayerIdleTime = playerIdleTime;
            _playerIdleHandler.IdleStopDistance = idleStopDistance;
            _playerIdleHandler.IdleLookAroundChance = idleLookAroundChance;
            _playerIdleHandler.IdleWanderChance = idleWanderChance;
            _playerIdleHandler.IdleMaxWanderDistance = idleMaxWanderDistance;
            _playerIdleHandler.IdleStopTimeMin = idleStopTimeMin;
            _playerIdleHandler.IdleStopTimeMax = idleStopTimeMax;
            PlayerIdleHandler.VerboseLogging = VerboseLogging;
            
            // Stuck detection handler - manages stuck detection and jumping
            _stuckHandler = new StuckDetectionHandler(
                transform,
                _character,
                _rigidbody,
                _companion,
                _followBehavior);
            _stuckHandler.StuckDetectionTime = stuckDetectionTime;
            _stuckHandler.StuckMovementThreshold = stuckMovementThreshold;
            _stuckHandler.StuckChecksBeforeJump = stuckChecksBeforeJump;
            _stuckHandler.JumpCooldown = jumpCooldown;
            _stuckHandler.JumpForce = jumpForce;
            _stuckHandler.JumpForwardBoost = jumpForwardBoost;
            _stuckHandler.MaxJumpableHeight = maxJumpableHeight;
            _stuckHandler.ObstacleCheckDistance = obstacleCheckDistance;
            _stuckHandler.MinOwnerHeightDiffForJump = minOwnerHeightDiffForJump;
            _stuckHandler.JumpAnimationDuration = jumpAnimationDuration;
            StuckDetectionHandler.VerboseLogging = VerboseLogging;
            
            // Combat movement handler - manages combat strafe/retreat/approach logic
            _combatHandler = new CombatMovementHandler(
                transform,
                _character,
                _companion,
                _terrainAwareness);
            _combatHandler.MeleeStafeDistance = meleeStafeDistance;
            _combatHandler.StrafeDirectionChangeInterval = strafeDirectionChangeInterval;
            _combatHandler.MaxStrafeDistance = maxStrafeDistance;
            _combatHandler.OwnerProtectionRadius = ownerProtectionRadius;
            _combatHandler.InterceptionLeadTime = interceptionLeadTime;
            _combatHandler.ApproachCommitmentDuration = approachCommitmentDuration;
            _combatHandler.StrafeCommitmentDuration = strafeCommitmentDuration;
            _combatHandler.MaxCommitmentDuration = maxCommitmentDuration;
            _combatHandler.DamageReassessDelay = damageReassessDelay;
            _combatHandler.ProjectileDetectionRange = projectileDetectionRange;
            _combatHandler.MovementBlendSpeed = movementBlendSpeed;
            _combatHandler.MaxOwnerDistance = maxOwnerCombatDistance;
            _combatHandler.OwnerLeashDistance = ownerLeashDistance;
            _combatHandler.OwnerReturnDistance = ownerReturnDistance;
            CombatMovementHandler.VerboseLogging = VerboseLogging;
            
            // State transition handler - manages combat/idle transitions and victory emotes
            _stateTransitionHandler = new StateTransitionHandler(
                transform, _character, _rigidbody, _zanim, _animator, _companion);
            _stateTransitionHandler.StateTransitionGracePeriod = stateTransitionGracePeriod;
            _stateTransitionHandler.MovementBlendSpeed = movementBlendSpeed;
            _stateTransitionHandler.CombatEndGracePeriod = combatEndGracePeriod;
            StateTransitionHandler.VerboseLogging = VerboseLogging;
            
            // Ranged movement handler - processes bow/crossbow movement requests
            _rangedHandler = new RangedMovementHandler(
                transform, _character, _companion, _terrainAwareness);
            _rangedHandler.ApproachCommitmentDuration = approachCommitmentDuration;
            _rangedHandler.StrafeCommitmentDuration = strafeCommitmentDuration;
            RangedMovementHandler.VerboseLogging = VerboseLogging;
            
            // Follow intent controller - handles follow speed hysteresis
            _followIntentController = new FollowIntentController();
            _followIntentController.StopDistanceInner = followStopDistanceInner;
            _followIntentController.StopDistanceOuter = followStopDistanceOuter;
            _followIntentController.WalkDistanceInner = followWalkDistanceInner;
            _followIntentController.WalkDistanceOuter = followWalkDistanceOuter;
            _followIntentController.JogDistanceInner = followJogDistanceInner;
            _followIntentController.JogDistanceOuter = followJogDistanceOuter;
            _followIntentController.RunDistanceInner = followRunDistanceInner;
            _followIntentController.RunDistanceOuter = followRunDistanceOuter;
            _followIntentController.SprintDistance = followSprintDistance;
            FollowIntentController.VerboseLogging = VerboseLogging;
            
            // State machine for cleaner Update() logic
            _stateMachine = new MovementStateMachine();
            _stateContext = new MovementStateContext();
            MovementStateMachine.VerboseLogging = VerboseLogging;
            
            // Subscribe to state changes for logging/debugging
            _stateMachine.OnStateChanged += OnMovementStateChanged;
        }
        
        private void OnMovementStateChanged(MovementStateMachine.State oldState, MovementStateMachine.State newState)
        {
            if (_stateMachine.IsSignificantTransition(newState))
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} significant transition: {oldState} -> {newState}");
            }
        }

        private void LateUpdate()
        {
            // PERF: Only attempt combat context lookup once — avoids per-frame reflection
            if (_combatContext == null && _combat != null && !_combatContextLookupDone)
            {
                _combatContextLookupDone = true;
                try
                {
                    if (!_contextFieldResolved)
                    {
                        _cachedContextField = typeof(CompanionCombat).GetField("_context",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        _contextFieldResolved = true;
                    }
                    _combatContext = _cachedContextField?.GetValue(_combat) as CombatContext;

                    if (_combatContext != null)
                    {
                        _stuckHandler?.SetCombatContext(_combatContext);
                        _rangedHandler?.SetDependencies(_staminaManager, _companionAI);
                    }
                }
                catch { }
            }

            if (!_rangedBehaviorsSubscribed && _combat != null)
            {
                SubscribeToRangedBehaviors();
            }
        }

        private void Update()
        {
            if (_stateController == null)
                _stateController = GetComponent<CompanionStateController>();
            
            UpdateGroundedState();
            
            bool animationLocked = _combatContext?.IsAnimationLocked ?? false;
            UpdateStateContext(animationLocked);
            
            var newState = _stateMachine.EvaluateState(_stateContext);
            _stateMachine.TransitionTo(newState, _companion?.companionName);
            
            ExecuteCurrentState(animationLocked);
            
            UpdateAnimator();
        }
        
        private void UpdateStateContext(bool animationLocked)
        {
            _stateContext.Companion = _companion;
            _stateContext.CompanionAI = _companionAI;
            _stateContext.StateController = _stateController;
            _stateContext.IdleBehavior = _idleBehavior;
            _stateContext.IsMovementLocked = IsMovementLocked;
            _stateContext.IsAnimationLocked = animationLocked;
            _stateContext.IsInCombat = _isInCombat;
            _stateContext.IsInTransition = _isInTransition;
            _stateContext.IsInCombatCooldown = _isInCombatCooldown;
            _stateContext.CurrentIntent = _currentIntent;
        }
        
        private void ExecuteCurrentState(bool animationLocked)
        {
            switch (_stateMachine.CurrentState)
            {
                case MovementStateMachine.State.Disabled:
                    return;
                    
                case MovementStateMachine.State.Skipped:
                    return;
                    
                case MovementStateMachine.State.Fleeing:
                    ExecuteFleeingState();
                    return;
                    
                case MovementStateMachine.State.CommandPriority:
                    ExecuteCommandPriorityState();
                    return;
                    
                case MovementStateMachine.State.EmoteFrozen:
                    ExecuteEmoteFrozenState();
                    return;
                    
                case MovementStateMachine.State.MovementLocked:
                    ExecuteMovementLockedState();
                    return;
            }
            
            if (_localMovementLocked && Time.time >= _localMovementLockEndTime)
            {
                _localMovementLocked = false;
                _localMovementLockReason = "";
            }
            
            UpdatePlayerIdleState();
            
            if (_hasRangedMovementRequest && Time.time - _rangedRequestTime > RANGED_REQUEST_TIMEOUT)
                _hasRangedMovementRequest = false;
            
            if (animationLocked)
                return;
            
            UpdateCombatState();
            
            switch (_stateMachine.CurrentState)
            {
                case MovementStateMachine.State.Combat:
                    ExecuteCombatState();
                    break;
                    
                case MovementStateMachine.State.Transitioning:
                    UpdateTransitionMovement();
                    break;
                    
                case MovementStateMachine.State.CombatCooldown:
                    ExecuteCombatCooldownState();
                    break;
                    
                case MovementStateMachine.State.Idle:
                    ExecuteIdleState();
                    break;
                    
                case MovementStateMachine.State.Following:
                    ExecuteFollowingState();
                    break;
            }
            
            UpdateStuckDetection();
            UpdateJumpLogic();
        }

        #endregion

        #region Public API

        public bool IsInCombat => _isInCombat;
        public bool IsGrounded => _isGrounded;
        public bool IsJumping => _combatContext?.CurrentLockedAnimation == "jump";
        public MovementIntent CurrentIntent => _currentIntent;
        public MovementMode CurrentMovementMode => GetMovementModeForIntent();
        public Character CommittedTarget => _committedTarget;
        public bool HasCombatCommitment => _hasActiveCommitment;
        public bool IsInTransition => _isInTransition;

        public EnemyAttackRecognition AttackRecognition => _attackRecognition;
        public StaminaManager Stamina => _staminaManager;
        public TerrainAwareness Terrain => _terrainAwareness;
        public MovementStateMachine.State CurrentMovementState => _stateMachine?.CurrentState ?? MovementStateMachine.State.Disabled;

        public void ForceCombatReassess()
        {
            _hasActiveCommitment = false;
        }

        public void ForceCheckCombatEnd()
        {
            if (!CheckForNearbyThreats())
            {
                _isInCombat = false;
                _currentTarget = null;
                _committedTarget = null;
                _hasActiveCommitment = false;
                _lastEnemyKillTime = Time.time;
                _isInCombatCooldown = true;
                BeginStateTransition();
            }
        }

        /// <summary>
        /// Called by <see cref="CompanionController.TeleportToOwner"/> /
        /// <see cref="CompanionController.TeleportToDestination"/> after a long-distance
        /// teleport (portal jump, dungeon entry, owner respawn at a bed, etc).
        ///
        /// Unconditionally drops combat state — unlike <see cref="ForceCheckCombatEnd"/>
        /// we do NOT first probe nearby threats, because the companion's collider
        /// hasn't moved to the new location at the moment this is called and any
        /// "nearby" scan would still see the OLD environment.  Once the position
        /// is updated, the next combat tick re-scans the new surroundings and will
        /// pick up real local threats from a clean slate.
        /// </summary>
        public void OnTeleportedFar()
        {
            _isInCombat = false;
            _currentTarget = null;
            _committedTarget = null;
            _hasActiveCommitment = false;
            _isInCombatCooldown = false;
            _lastEnemyKillTime = Time.time;
            _movementModeSet = false;
            _moveDirSet = false;
            _hasRangedMovementRequest = false;
        }

        public string GetStatusDescription()
        {
            if (_combatContext?.CurrentLockedAnimation == "jump" || !_isGrounded)
            {
                return "Jumping";
            }

            if (_isInTransition)
            {
                return "Transitioning...";
            }
            
            if (_stateMachine != null)
            {
                switch (_stateMachine.CurrentState)
                {
                    case MovementStateMachine.State.Fleeing:
                        return "Fleeing!";
                    case MovementStateMachine.State.EmoteFrozen:
                        return "Emoting";
                    case MovementStateMachine.State.MovementLocked:
                        return "Busy";
                    case MovementStateMachine.State.CombatCooldown:
                        return "Alert";
                }
            }

            if (_currentIntent == MovementIntent.Idle && _idleBehavior != null && !_isInCombat)
            {
                return _idleBehavior.GetIdleStateDescription();
            }

            return _currentIntent switch
            {
                MovementIntent.Idle => "Standing",
                MovementIntent.FollowingClose => "Following (walking)",
                MovementIntent.FollowingMedium => "Following (jogging)",
                MovementIntent.FollowingFar => "Following (running)",
                MovementIntent.CatchingUp => "Catching up! (sprinting)",
                MovementIntent.CombatApproach => "Charging!",
                MovementIntent.CombatStrafe => "In melee combat",
                MovementIntent.CombatIntercept => "Protecting you!",
                MovementIntent.CombatRetreat => "Creating distance",
                MovementIntent.CombatChase => "Chasing enemy",
                MovementIntent.PlantedFiring => "Taking aim",
                MovementIntent.Repositioning => "Repositioning",
                MovementIntent.CombatDodge => "Dodging!",
                MovementIntent.CombatBlock => "Blocking",
                MovementIntent.Transitioning => "Transitioning...",
                _ => "Unknown"
            };
        }

        #endregion

        #region Home Position Management

        public void SetHomePosition(Vector3 position)
        {
            if (_companion != null && _companion.ShouldBeFollowing)
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} ignoring SetHomePosition - companion is following");
                return;
            }
            
            _homePosition = position;
            _hasHomePosition = true;

            if (VerboseLogging)
                Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} home position set to {position}");
        }

        public void ClearHomePosition()
        {
            _hasHomePosition = false;
            _homePosition = Vector3.zero;
            
            if (VerboseLogging)
                Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} home position CLEARED");
        }

        public bool HasHomePosition => _hasHomePosition && !(_companion?.ShouldBeFollowing ?? false);
        public Vector3 HomePosition => _homePosition;

        #endregion

        #region Helpers

        private bool IsPlayerMoving(Player player)
        {
            if (player == null) return false;
            var velocity = player.GetVelocity();
            return velocity.magnitude > 0.5f;
        }

        private Vector3 GetCharacterVelocity(Character character)
        {
            if (character == null) return Vector3.zero;
            return character.GetVelocity();
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

        #region Animator

        private void UpdateAnimator()
        {
            if (_animator == null && _zanim == null) return;

            if (_zanim != null)
            {
                _zanim.SetBool(Hash_inair, !_isGrounded);
            }
            else if (_animator != null)
            {
                if (HasAnimatorParameter("inair"))
                {
                    _animator.SetBool("inair", !_isGrounded);
                }
            }
        }

        #endregion
    }
}
