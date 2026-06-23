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
        public float stopDistanceInner = 2f;
        public float stopDistanceOuter = 3.5f;
        public float walkDistanceOuter = 5f;
        public float runDistanceInner = 8f;
        public float runDistanceOuter = 12f;
        public float catchUpDistance = 20f;
        public float playerIdleThreshold = 3f;
        
        private enum FollowSpeed { Stopped, Sneaking, Walking, Jogging, Running, Sprinting }
        private FollowSpeed _currentFollowSpeed = FollowSpeed.Stopped;
        
        // Owner stance matching - companion mirrors the player's movement stance when following
        private bool _isOwnerSneaking = false;
        private bool _isOwnerWalking = false;
        private bool _isOwnerRunning = false;
        private bool _isCompanionSneaking = false;
        private float _lastOwnerStanceCheck = -10f;
        private const float OWNER_STANCE_CHECK_INTERVAL = 0.2f;
        
        // Reflection for Character.SetCrouch (protected method) and m_crouching field
        private static System.Reflection.MethodInfo _setCrouchMethod;
        private static System.Reflection.FieldInfo _crouchingField;
        private static bool _setCrouchMethodResolved = false;

        [Header("Combat Settings")]
        // Detection radius for the threat scan loop. Lowered from 20 to 15 m
        // because the owner-defense gate in CompanionAI.Targeting now decides
        // which detected threats are actually engaged (see
        // OWNER_DEFENSE_RADIUS); a wider scan radius is just perf overhead
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

        // Post-teleport combat suppression.
        // Set by OnTeleportedFar (long-distance teleports: forced stranded-pull,
        // dungeon entry, portal jump, owner respawn).  While Time.time is below
        // this timestamp, UpdateTargetDetection bails out early and any current
        // target is dropped ï¿½ this gives the companion a brief window to walk
        // back to the player and "calm down" instead of immediately re-entering
        // combat with whatever is still nearby.  After the window expires,
        // normal threat scanning resumes; if there are real local threats they
        // get picked up at that point, exactly as before.
        //
        // The suppression deliberately does NOT touch MovementAuthority ï¿½ the
        // existing priority/incumbency rules stay in charge of who drives the
        // motor.  All we change is whether CompanionAI tries to acquire a new
        // _targetCreature for this short window.
        private float _combatSuppressedUntilTime = 0f;
        private const float POST_TELEPORT_COMBAT_SUPPRESS_SECONDS = 3.0f;
        
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
        private const float STAY_MODE_AGGRO_RANGE = 5f;
        private const float DIRECT_DAMAGE_ALERT_DURATION = 15f;

        // OWNER-DEFENSE RADIUS
        // For aggressive (non-passive) enemies in Follow mode, a companion
        // will only engage if the threat is within this radius of its owner,
        // OR is currently targeting the owner / a player / this companion,
        // OR this companion has been directly attacked recently. Outside
        // those conditions the enemy is left alone even though it's inside
        // aggroRange ï¿½ this stops companions from chasing every hostile in
        // the forest while their owner is doing something else.
        //
        // Passive wildlife (boar, deer, neck, etc.) is governed separately
        // by the per-companion Hunting toggle in CompanionBehaviorToggles.
        // Stay-mode is governed by STAY_MODE_AGGRO_RANGE and is unaffected
        // by this constant.
        private const float OWNER_DEFENSE_RADIUS = 8f;

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
        private const float TARGET_SCAN_INTERVAL = 0.25f;
        private const float POSITION_REACHED_THRESHOLD = 1.5f;
        private static readonly List<Character> _tempCharacterList = new List<Character>();
        
        // STATE OSCILLATION PREVENTION
        private const float MIN_COMBAT_STATE_TIME = 1.0f;
        private const float MIN_RETURNING_STATE_TIME = 0.5f;
        
        // Pathfinding
        private Vector3 _lastPathfindingPos;
        private float _lastPathfindingTime;
        private float _lastPathRecalcTime;
        private int _consecutiveStuckFrames;
        private int _pathfindingAttempts;
        private Vector3 _currentMoveTarget;
        private float _lastProgressTime;
        private float _lastDistanceToTarget = float.MaxValue;
        
        private const float PATH_RECALC_INTERVAL = 0.5f;
        private const float STUCK_CHECK_INTERVAL = 1.0f;
        private const float STUCK_MOVEMENT_THRESHOLD = 0.5f;
        private const int STUCK_FRAMES_BEFORE_RECALC = 3;
        private const float PROGRESS_TIMEOUT = 5.0f;
        private const float DIRECT_MOVE_DISTANCE = 3f;
        private const float WAYPOINT_SEARCH_RADIUS = 5f;
        private const int MAX_PATHFINDING_ATTEMPTS = 5;

        #endregion

        #region Debugging

        public static bool VerboseLogging = false;
        public static bool StateTransitionLogging = false;
        public static bool ArchetypeCombatLogging = true;
        
        private float _lastStateLogTime = -100f;
        private const float STATE_LOG_INTERVAL = 5f;
        private AIState _lastLoggedState = AIState.Idle;
        private bool _lastLoggedCriticalRecovery = false;
        private bool _lastLoggedRecovery = false;
        private float _lastArchetypeLogTime = -100f;
        private const float ARCHETYPE_LOG_INTERVAL = 10f;

        #endregion

        #region Unity Lifecycle

        public override void Awake()
        {
            base.Awake();

            _companion = GetComponent<CompanionController>();
            _stateController = GetComponent<CompanionStateController>();
            _humanoid = GetComponent<Humanoid>();
            _zanim = GetComponent<ZSyncAnimation>();
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

        public override bool UpdateAI(float dt)
        {
            // Guard: MonoUpdaters can call UpdateAI on a component whose GameObject has already
            // been destroyed (stale reference in the update list). GetComponent<T>() on a
            // destroyed Component throws a native NullReferenceException, so bail out first.
            if (this == null || !gameObject) return false;

            // CRITICAL: Check stationed state BEFORE base.UpdateAI runs.
            // BaseAI.UpdateAI resets m_lookDir every tick via UpdateTarget/UpdateRotation,
            // which overrides any look direction we set (e.g., face-nearest-player).
            // For stationed non-wandering NPCs, skip the base AI entirely.
            if (_npcModule == null)
                _npcModule = GetComponent<CompanionNpcModule>();
            if (_npcModule != null && _npcModule.IsStationedAsNpc && !_npcModule.AllowsIdleBehaviors)
            {
                return true;
            }

            // Static placed NPCs: skip base AI until CompanionNpcModule is set up by
            // StaticNpcInitializer (1s delay). Without this, base.UpdateAI resets m_lookDir
            // to Vector3.forward during that first second, baking north-facing into the ZDO.
            if (_companion != null && _companion.isStaticPlacement)
            {
                if (_npcModule == null || !_npcModule.AllowsIdleBehaviors)
                    return true;
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
                return true;

            if (!base.UpdateAI(dt))
                return false;

            if (_stateController == null)
                _stateController = GetComponent<CompanionStateController>();

            if (_stateController != null && _stateController.ShouldSkipAIUpdate)
            {
                return true;
            }

            // _idleBehavior already resolved above

            if (_idleBehavior != null && _idleBehavior.IsPlayerInteracting)
            {
                return true;
            }

            if (_idleBehavior != null && _idleBehavior.IsEmoteFrozen)
            {
                return true;
            }

            // CRITICAL FIX (Bug #12): Skip AI movement when a sub-behavior is active
            // Sub-behaviors have their own movement logic and authority - CompanionAI should NOT
            // interfere by trying to follow or wander while a sub-behavior is running.
            if (_idleBehavior != null && _idleBehavior.IsInSubBehavior)
            {
                // Sub-behavior is handling all movement - CompanionAI should do nothing
                // Just let the sub-behavior run via its Update() method
                return true;
            }

            // _interactionBehavior already resolved above; re-check attached state post-base
            if (_interactionBehavior != null && _interactionBehavior.IsAttached)
            {
                return true;
            }
            
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
                            
                            if (VerboseLogging && Time.time - _lastDestinationReachedLogTime >= DESTINATION_REACHED_LOG_INTERVAL)
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

        public override void OnDamaged(float damage, Character attacker)
        {
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
            // we would otherwise panic ï¿½ "do what they're told, even unto death".
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

            if (StateTransitionLogging || VerboseLogging)
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
            if (Time.time - _lastArchetypeLogTime < ARCHETYPE_LOG_INTERVAL)
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
                    authority.ReleaseAuthority(AI_AUTHORITY_OWNER);
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
                SetState(AIState.Combat);
            }
        }
        
        public void ClearForceTarget()
        {
            SetTarget(null);
        }

        public bool ShouldBeFollowing => _shouldFollow;
        public bool IsInCombat => _currentState == AIState.Combat;
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
        private const float DESTINATION_REACHED_LOG_INTERVAL = 10f;
        
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
        /// Requests movement to a destination using vanilla pathfinding with authority coordination.
        /// This is the PUBLIC API for behaviors to request pathfinding-based movement.
        /// 
        /// Call this every frame until it returns true (destination reached).
        /// 
        /// CORRECT ARCHITECTURE:
        /// - Authority = coordination (decides WHO can move)
        /// - Vanilla MoveTo() = pathfinding (handles HOW to move around obstacles)
        /// </summary>
        /// <param name="destination">Target position to reach</param>
        /// <param name="run">True for running, false for walking</param>
        /// <param name="reachDistance">How close to get before considering "reached"</param>
        /// <param name="authoritySource">Movement source priority level</param>
        /// <param name="authorityOwner">Name of the system requesting movement</param>
        /// <returns>True when destination reached, false while still moving</returns>
        public bool RequestPathfindingMovement(
            Vector3 destination, 
            bool run, 
            float reachDistance = 1.5f,
            Core.UnifiedMovementAuthority.MovementSource authoritySource = Core.UnifiedMovementAuthority.MovementSource.SubBehavior,
            string authorityOwner = "Behavior")
        {
            if (m_character == null) return true;
            
            // Check distance first - already there?
            float distance = Vector3.Distance(transform.position, destination);
            if (distance <= reachDistance)
            {
                return true; // Already at destination
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
            // BaseAI.MoveTo() uses FindPath() and m_path waypoints for obstacle avoidance
            return MoveTo(Time.deltaTime, destination, reachDistance, run);
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
        }
        
        public bool IsStuck => _consecutiveStuckFrames >= STUCK_FRAMES_BEFORE_RECALC;
        public int StuckCheckCount => _consecutiveStuckFrames;
        
        /// <summary>
        /// Sets the companion's crouch/sneak state.
        /// 
        /// Valheim's Character.SetCrouch is protected and uses an RPC to sync crouch state.
        /// For companions we need a multi-layered approach:
        ///   1. Try Character.SetCrouch via reflection (handles RPC sync to all clients)
        ///   2. Directly set m_crouching field (ensures IsCrouching() returns correct value)
        ///   3. Set the ZSyncAnimation "crouch" bool (drives the animation)
        ///   4. Set the ZDO "Crouch" value (network persistence)
        /// 
        /// Steps 2-4 act as insurance in case SetCrouch fails or the RPC doesn't
        /// reach all clients properly for modded NPC characters.
        /// </summary>
        private void SetCompanionCrouch(bool crouch)
        {
            if (m_character == null) return;
            
            if (!_setCrouchMethodResolved)
            {
                var bindingFlags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
                _setCrouchMethod = typeof(Character).GetMethod("SetCrouch", bindingFlags);
                _crouchingField = typeof(Character).GetField("m_crouching", bindingFlags);
                _setCrouchMethodResolved = true;
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionAI] SetCrouch reflection: method={(_setCrouchMethod != null ? "found" : "NOT FOUND")}, field={(_crouchingField != null ? "found" : "NOT FOUND")}");
            }
            
            bool setCrouchWorked = false;
            
            // Layer 1: Try the proper SetCrouch method which handles RPC sync
            if (_setCrouchMethod != null)
            {
                try
                {
                    _setCrouchMethod.Invoke(m_character, new object[] { crouch });
                    setCrouchWorked = true;
                }
                catch (Exception ex)
                {
                    if (VerboseLogging)
                        Debug.LogWarning($"[CompanionAI] SetCrouch reflection invoke failed: {ex.Message}");
                }
            }
            
            // Layer 2: Directly set m_crouching field to ensure IsCrouching() works
            // This is critical ï¿½ if SetCrouch uses an RPC that doesn't work for NPCs,
            // at least the local state will be correct.
            if (_crouchingField != null)
            {
                try
                {
                    _crouchingField.SetValue(m_character, crouch);
                }
                catch (Exception ex)
                {
                    if (VerboseLogging)
                        Debug.LogWarning($"[CompanionAI] m_crouching field set failed: {ex.Message}");
                }
            }
            
            // Layer 3: Always set the animation bool ï¿½ this drives the visual crouch
            if (_zanim != null)
            {
                _zanim.SetBool("crouch", crouch);
            }
            
            // Layer 4: Set ZDO for network persistence across clients.
            // Skip during local player respawn / loading screen â€” defensive.
            if (!setCrouchWorked && !FiresCore.Npc.CompanionPatches.AreCompanionTeleportsSuppressed())
            {
                var nview = m_character.GetComponent<ZNetView>();
                if (nview != null && nview.IsValid())
                {
                    var zdo = nview.GetZDO();
                    if (zdo != null)
                    {
                        zdo.Set("Crouch", crouch);
                    }
                }
            }
        }

        #endregion
    }
}
