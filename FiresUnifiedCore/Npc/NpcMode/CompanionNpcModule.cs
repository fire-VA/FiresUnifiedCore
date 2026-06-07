using UnityEngine;
using System;
using System.Collections.Generic;
using FiresCore.Npc.AI;
using FiresCore.Npc.Interactions;
using FiresCore.Npc.Vault;

namespace FiresCore.Npc.NpcMode
{
    /// <summary>
    /// Enables NPC functionality on a companion, allowing it to serve as a quest giver,
    /// info NPC, dialogue NPC, or trader while retaining companion capabilities.
    /// 
    /// CRITICAL INTEGRATION:
    /// When stationed as NPC, this module DISABLES:
    /// - CompanionAI state machine (via IsStationedAsNpc check)
    /// - CompanionCombatMovement (via movement lock)
    /// - Following behavior
    /// 
    /// When stationed as NPC, this module OPTIONALLY ALLOWS:
    /// - Idle wandering within a radius (allowIdleWandering flag)
    /// - Sitting on nearby chairs
    /// 
    /// MODES:
    /// - Companion Mode (default): Normal companion behavior
    /// - Stationed NPC (stationary): Completely locked in place, no movement at all
    /// - Stationed NPC (with idle): Locked to area but can wander/sit within radius
    /// 
    /// HOVER TEXT:
    /// - Stationed: Shows NPC options (Configure, Interact, Toggle Idle)
    /// - Companion: Falls through to CompanionPatches hover text
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
        public NpcType npcType = NpcType.QuestNpc;

        [Header("Profiles")]
        public string dialogueProfile = "";
        public string questProfile = "";
        public string infoProfile = "";
        public string traderProfile = "";

        [Header("Animations")]
        [Tooltip("Animation to play when player approaches")]
        public string greetAnimation = "emote_wave";
        [Tooltip("Animation to play when player leaves")]
        public string byeAnimation = "emote_wave";

        #endregion

        #region State

        private CompanionController _companion;
        private CompanionCombatMovement _combatMovement;
        private CompanionIdleBehavior _idleBehavior;
        private CompanionInteractionBehavior _interactionBehavior;
        private CompanionAI _companionAI;
        private Character _character;
        private ZNetView _nview;
        private Animator _animator;
        private Rigidbody _rigidbody;

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
        private bool _stationaryEnforced;

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
            _combatMovement = GetComponent<CompanionCombatMovement>();
            _idleBehavior = GetComponent<CompanionIdleBehavior>();
            _interactionBehavior = GetComponent<CompanionInteractionBehavior>();
            _companionAI = GetComponent<CompanionAI>();
            _character = GetComponent<Character>();
            _nview = GetComponent<ZNetView>();
            _animator = GetComponentInChildren<Animator>(true);
            _rigidbody = GetComponent<Rigidbody>();
        }

        private void Start()
        {
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

            // Static placed NPCs have no CompanionController ÃƒÆ’Ã‚Â¯Ãƒâ€šÃ‚Â¿Ãƒâ€šÃ‚Â½ allow them through
            if (_companion == null && !isStaticPlacement) return;

            // If stationed as NPC, enforce the stationed state
            if (isStationedAsNpc)
            {
                EnforceStationedState();
                UpdateProximityDetection();
            }
        }

        private void LateUpdate()
        {
            if (!isStationedAsNpc || !_hasStationedPosition || _isInPlacementMode) return;

            // Position enforcement (non-wandering only)
            if (!allowIdleWandering)
            {
                float dist = Vector3.Distance(transform.position, _stationedPosition);
                if (dist > 0.1f)
                    transform.position = _stationedPosition;
            }

            // Face nearest player ÃƒÆ’Ã‚Â¯Ãƒâ€šÃ‚Â¿Ãƒâ€šÃ‚Â½ works WITH Valheim's Character rotation system
            // instead of fighting it. We just set m_lookDir and let Character.UpdateBodyRotation
            // do the smooth turning.
            if (_character == null) return;

            _facePlayerTimer += Time.deltaTime;
            if (_facePlayerTimer < FacePlayerInterval) return;
            _facePlayerTimer = 0f;

            var lookDir = GetDirectionToNearestPlayer();
            if (lookDir.sqrMagnitude > 0.001f)
                _character.m_lookDir = lookDir;
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
                float d = Vector3.Distance(transform.position, player.transform.position);
                if (d < closestDist)
                {
                    closestDist = d;
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

            // Lock movement in combat movement system
            if (_combatMovement != null)
            {
                _combatMovement.LockMovement("StationedNpc", 999999f);
            }

            // Set home position for idle behavior (if wandering allowed)
            if (_combatMovement != null && _hasStationedPosition)
            {
                _combatMovement.SetHomePosition(_stationedPosition);
            }

            // Disable CompanionAI following
            if (_companionAI != null)
            {
                _companionAI.SetStayPosition(_stationedPosition);
            }

            // Stop character movement - but ONLY if rigidbody is NOT kinematic
            // Unity 6 throws warnings when setting velocity on kinematic bodies,
            // and Character.SetMoveDir() internally sets velocity
            if (_character != null && (_rigidbody == null || !_rigidbody.isKinematic))
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }

            // Stop rigidbody - only if NOT kinematic (Unity 6 doesn't allow setting velocity on kinematic bodies)
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }

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

            // If idle wandering is allowed, DON'T lock movement - let the companion wander
            if (allowIdleWandering)
            {
                // Make sure movement is NOT locked so idle behaviors work
                if (_combatMovement != null && _combatMovement.IsMovementLocked)
                {
                    // Only unlock if locked by us (StationedNpc)
                    _combatMovement.UnlockMovement();
                }

                // Enforce territory or radius bounds
                EnforceWanderBounds();
                return;
            }

            // Stationary mode - keep locked in place
            if (_combatMovement != null && !_combatMovement.IsMovementLocked)
            {
                _combatMovement.LockMovement("StationedNpc", 999999f);
            }

            // PERF: Only zero velocity/moveDir once ÃƒÆ’Ã‚Â¯Ãƒâ€šÃ‚Â¿Ãƒâ€šÃ‚Â½ not every frame.
            // Each SetMoveDir call goes through a Harmony prefix patch that does
            // component lookups on every Character, so avoiding redundant calls
            // saves significant CPU per stationed NPC per frame.
            if (!_stationaryEnforced)
            {
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
                _stationaryEnforced = true;
            }

            // Snap back to position if drifted
            float distance = Vector3.Distance(transform.position, _stationedPosition);
            if (distance > 0.1f)
            {
                transform.position = _stationedPosition;
                // Position was corrected ÃƒÆ’Ã‚Â¯Ãƒâ€šÃ‚Â¿Ãƒâ€šÃ‚Â½ need to re-zero movement next frame
                _stationaryEnforced = false;
            }
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

            // First try the configured greet animation
            if (!string.IsNullOrEmpty(greetAnimation) && HasAnimatorParameter(greetAnimation))
            {
                _animator.SetTrigger(greetAnimation);
                if (VerboseLogging)
                    Debug.Log($"[CompanionNpcModule] {DisplayName} playing configured greet animation: {greetAnimation}");
                return;
            }

            // Fallback to common greet animations
            string[] greetAnims = { "emote_wave", "emote_bow", "emote_thumbsup", "wave", "greet" };
            foreach (var anim in greetAnims)
            {
                if (HasAnimatorParameter(anim))
                {
                    _animator.SetTrigger(anim);
                    // Update the configured animation to what we found
                    greetAnimation = anim;
                    if (VerboseLogging)
                        Debug.Log($"[CompanionNpcModule] {DisplayName} playing fallback greet animation: {anim}");
                    return;
                }
            }
        }

        private void PlayByeAnimation()
        {
            if (_animator == null) return;

            // First try the configured bye animation
            if (!string.IsNullOrEmpty(byeAnimation) && HasAnimatorParameter(byeAnimation))
            {
                _animator.SetTrigger(byeAnimation);
                if (VerboseLogging)
                    Debug.Log($"[CompanionNpcModule] {DisplayName} playing configured bye animation: {byeAnimation}");
                return;
            }

            // Fallback to common bye animations
            string[] byeAnims = { "emote_wave", "wave", "bye" };
            foreach (var anim in byeAnims)
            {
                if (HasAnimatorParameter(anim))
                {
                    _animator.SetTrigger(anim);
                    // Update the configured animation to what we found
                    byeAnimation = anim;
                    if (VerboseLogging)
                        Debug.Log($"[CompanionNpcModule] {DisplayName} playing fallback bye animation: {anim}");
                    return;
                }
            }
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
                _character.m_lookDir = forward.normalized;
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
        /// Static (hammer-placed) NPCs cannot be unstated ÃƒÆ’Ã‚Â¯Ãƒâ€šÃ‚Â¿Ãƒâ€šÃ‚Â½ they have no owner to return to.
        /// </summary>
        public void Unstation()
        {
            if (isStaticPlacement)
            {
                Debug.Log($"[CompanionNpcModule] Cannot unstation {DisplayName} ÃƒÆ’Ã‚Â¯Ãƒâ€šÃ‚Â¿Ãƒâ€šÃ‚Â½ it was placed via hammer (static NPC)");
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
                    // Mirror of the stationed-state freeze (FreezeAll constraints) ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â clear
                    // constraints to release the lock when the NPC starts wandering again.
                    body.constraints = RigidbodyConstraints.None;
                    body.isKinematic = false;
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

                // Enable idle behavior if present (re-fetch if null ÃƒÆ’Ã‚Â¯Ãƒâ€šÃ‚Â¿Ãƒâ€šÃ‚Â½ it may have been
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
                // Lock down completely - NPC stays in place
                ApplyStationedState();

                // Disable AI and physics ÃƒÆ’Ã‚Â¯Ãƒâ€šÃ‚Â¿Ãƒâ€šÃ‚Â½ NPC should not move or rotate on its own
                var monsterAI = GetComponent<MonsterAI>();
                if (monsterAI != null) monsterAI.enabled = false;

                var body = GetComponent<Rigidbody>();
                if (body != null)
                {
                    // FreezeAll instead of isKinematic=true: vanilla Character.UpdateMotion
                    // writes m_body.linearVelocity every FixedUpdate, which spams Unity 6
                    // "kinematic body" warnings if we make the body kinematic. Constraints
                    // anchor the body without forbidding velocity writes.
                    body.constraints = RigidbodyConstraints.FreezeAll;
                    body.useGravity = false;
                }

                // Clear force return state
                _isForceReturningHome = false;

                // Snap back to original position
                transform.position = _stationedPosition;
                transform.rotation = _stationedRotation;
                
                // Clear territory
                _currentTerritoryRadius = null;
                _currentTerritoryName = null;

                Debug.Log($"[CompanionNpcModule] Disabled wandering, snapped to {_stationedPosition}");
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
                if (UnityEngine.Input.GetMouseButtonDown(0) || UnityEngine.Input.GetKeyDown(KeyCode.E))
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
                else if (UnityEngine.Input.GetKeyDown(KeyCode.R))
                {
                    placementRotation = 0f;
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, "Rotation reset");
                }
                // Face player with F key
                else if (UnityEngine.Input.GetKeyDown(KeyCode.F))
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
            allowIdleWandering = zdo.GetBool("npc_allow_idle_wander", false);
            allowNpcInteractionsWhileFollowing = zdo.GetBool("npc_allow_while_following", false);
            npcDisplayName = zdo.GetString("npc_display_name", "");
            npcType = (NpcType)zdo.GetInt("npc_type", 0);
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
            greetAnimation = zdo.GetString("npc_greet_animation", "emote_wave");
            byeAnimation = zdo.GetString("npc_bye_animation", "emote_wave");

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
            // Reference parent namespace fully ÃƒÆ’Ã‚Â¢ÃƒÂ¢Ã¢â‚¬Å¡Ã‚Â¬ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â CompanionPatches is in FiresCore.Npc
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
            zdo.Set("npc_greet_animation", greetAnimation ?? "emote_wave");
            zdo.Set("npc_bye_animation", byeAnimation ?? "emote_wave");

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
