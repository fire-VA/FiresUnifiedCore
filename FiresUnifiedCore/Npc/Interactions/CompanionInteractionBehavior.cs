using UnityEngine;
using System;
using System.Collections.Generic;
using FiresCore.Npc.Movement;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Npc.Interactions
{
    /// <summary>
    /// Handles companion interactions with world objects like chairs, benches, stools, and ship attach points.
    /// 
    /// FEATURES:
    /// - Sits on chairs/benches/stools when idle (with random 30-90 second duration)
    /// - Sits when owner sits (finds nearby empty seat)
    /// - Attaches to ship masts/seats when owner is on a ship
    /// - Supports variable sit durations
    /// 
    /// ATTACH ANIMATIONS:
    /// - attach_chair: Standard chair sit
    /// - attach_stool: Stool/bench sit
    /// - attach_bed: Lying down
    /// - attach_mast: Holding onto ship mast
    /// 
    /// DESIGN:
    /// - Works alongside CompanionIdleBehavior (integrates with it)
    /// - Monitors owner state for reactive behaviors
    /// - Handles all attach/detach logic cleanly
    /// </summary>
    public class CompanionInteractionBehavior : MonoBehaviour
    {
        #region Settings

        [Header("Seating Settings")]
        [Tooltip("Enable sitting on chairs/benches when idle")]
        public bool enableSeating = true;
        [Tooltip("Radius to search for seats")]
        public float seatDetectionRadius = 8f;
        [Tooltip("Minimum time to sit (seconds)")]
        public float sitDurationMin = 30f;
        [Tooltip("Maximum time to sit (seconds)")]
        public float sitDurationMax = 90f;
        [Tooltip("Cooldown between sitting attempts")]
        public float sitCooldown = 60f;
        [Tooltip("Chance to decide to sit when near a chair while wandering")]
        public float sitChanceWhileWandering = 0.3f;

        [Header("Owner Reaction Settings")]
        [Tooltip("Sit when owner sits")]
        public bool sitWhenOwnerSits = true;
        [Tooltip("How quickly to react when owner sits (seconds)")]
        public float ownerSitReactionDelay = 1.5f;
        [Tooltip("Max distance to search for seat when owner sits")]
        public float ownerSitSearchRadius = 10f;

        [Header("Ship Settings")]
        [Tooltip("Enable ship attachment (mast, seats)")]
        public bool enableShipInteraction = true;
        [Tooltip("Prefer mast over seats on ships")]
        public bool preferMast = true;
        [Tooltip("Time to wait before attaching when getting on ship")]
        public float shipAttachDelay = 3f;

        [Header("Timeout Settings")]
        [Tooltip("Hard timeout for any attachment - must be longer than max sit duration")]
        public float attachHardTimeout = 120f;

        #endregion

        #region State

        private CompanionController _companion;
        private CompanionIdleBehavior _idleBehavior;
        private CompanionCombatMovement _combatMovement;
        private CompanionStateController _stateController;
        private Character _character;
        private ZSyncAnimation _zanim;
        private Animator _animator;
        private Rigidbody _rigidbody;
        private Collider _collider;
        private CapsuleCollider _capsuleCollider;

        // Current attachment state
        private bool _isAttached;
        private float _attachEndTime;
        private float _attachHardTimeoutTime;
        private GameObject _currentAttachObject;
        private AttachType _currentAttachType;
        private Transform _currentAttachPoint;
        private Vector3 _targetSitPosition;
        private Quaternion _targetSitRotation;
        
        // Whether this attachment was commanded by the player (should not auto-detach when owner moves)
        private bool _isCommandedAttachment;

        // Kinematic state before attachment â€” restored on Detach so normal movement resumes
        private RigidbodyConstraints _preAttachConstraints;

        // Cooldowns and timing
        private float _lastSitAttempt;
        private float _lastOwnerSitCheck;
        private bool _ownerWasSitting;
        private float _ownerSatTime;

        // Ship state
        private Ship _currentShip;
        private float _boardedShipTime;
        private bool _isOnShip;

        // Mecanim trigger the looping-emote state machine watches for to
        // exit; required for the reflection fallback path below where
        // _stateController is null and ForceStopEmote can't run.
        private static readonly int EmoteStopTriggerHash = Animator.StringToHash("emote_stop");

        public static bool VerboseLogging = false;

        #endregion

        #region Enums

        public enum AttachType
        {
            None,
            Chair,
            Stool,
            Bench,
            Bed,
            ShipMast,
            ShipSeat
        }

        #endregion

        #region Properties

        public bool IsAttached => _isAttached;
        public AttachType CurrentAttachType => _currentAttachType;
        public bool IsOnShip => _isOnShip;

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _idleBehavior = GetComponent<CompanionIdleBehavior>();
            _combatMovement = GetComponent<CompanionCombatMovement>();
            _stateController = GetComponent<CompanionStateController>();
            _character = GetComponent<Character>();
            _zanim = GetComponent<ZSyncAnimation>();
            _animator = GetComponentInChildren<Animator>(true);
            _rigidbody = GetComponent<Rigidbody>();
            _collider = GetComponent<Collider>();
            _capsuleCollider = GetComponent<CapsuleCollider>();
        }

        private void Update()
        {
            if (_companion == null || !_companion.isTamed) return;
            
            // Ensure state controller reference is valid
            if (_stateController == null)
                _stateController = GetComponent<CompanionStateController>();

            // CRITICAL: If attached, ensure movement stays locked and position stays fixed
            if (_isAttached)
            {
                EnforceAttachmentState();
            }

            // CRITICAL: Skip all spontaneous behavior updates when a player command is active
            // The player has given us a specific task - don't get distracted!
            bool hasPlayerCommand = (_stateController != null && _stateController.IsPlayerCommandActive) ||
                                   (_combatMovement != null && _combatMovement.HasCommandPriority);
            
            if (hasPlayerCommand && !_isAttached)
            {
                // Player command is active and we're not already attached to something
                // Skip all spontaneous behavior updates - focus on the command
                return;
            }

            // Update ship detection
            UpdateShipDetection();

            // Update owner sitting reaction
            UpdateOwnerSittingReaction();

            // Update current attachment
            UpdateAttachment();
        }
        
        /// <summary>
        /// Mirrors Player.UpdateAttach: when attached, the companion's transform is
        /// re-pinned to the chair's attach point every fixed update. Without this
        /// the companion drifts off the seat as soon as anything (combat shove,
        /// idle wander momentum, animator root motion) nudges them.
        /// </summary>
        private void FixedUpdate()
        {
            if (!_isAttached) return;
            if (_currentAttachPoint == null) return;

            transform.position = _currentAttachPoint.position;
            transform.rotation = _currentAttachPoint.rotation;

            // Continuously zero residual physics so the per-frame position assignment
            // doesn't fight a moving rigidbody. Skip if kinematic â€” Unity logs a
            // warning if you write velocity on a kinematic body.
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
        }

        /// <summary>
        /// Enforces the attachment state every frame to prevent other systems from overriding.
        /// NOTE: We DON'T try to set velocity or call Character.SetMoveDir() here because:
        /// 1. The rigidbody is already kinematic during attachment
        /// 2. Unity 6 throws warnings when setting velocity on kinematic bodies
        /// 3. The collider is disabled so physics can't push us anyway
        /// We only ensure the movement lock is maintained so other systems don't try to move us.
        /// </summary>
        private void EnforceAttachmentState()
        {
            // Ensure movement lock is maintained - this prevents AI/movement systems from issuing commands
            if (_combatMovement != null && !_combatMovement.IsMovementLocked)
            {
                _combatMovement.LockMovement($"Attached_{_currentAttachType}", 999f);
            }
            
            // Ensure state controller knows we're in ChairSit state
            // ONLY try to enter if we're NOT already in ChairSit - prevents log spam
            if (_stateController != null && _stateController.CurrentState != CompanionStateController.CompanionState.ChairSit)
            {
                // If we're in PlayerCommand (commanded to sit), that's fine - don't try to override
                if (_stateController.CurrentState != CompanionStateController.CompanionState.PlayerCommand)
                {
                    _stateController.TryEnterState(CompanionStateController.CompanionState.ChairSit, 
                        _attachHardTimeoutTime - Time.time, "ChairSit");
                }
            }
            
            // DON'T set velocity or call Character.SetMoveDir() - the rigidbody is kinematic
            // and the collider is disabled, so there's nothing to zero out.
            // Calling these would cause Unity 6 warnings about setting velocity on kinematic bodies.
        }

        #endregion

        #region Ship Detection

        private void UpdateShipDetection()
        {
            if (!enableShipInteraction) return;

            // Check if companion is on a ship
            bool wasOnShip = _isOnShip;
            _currentShip = GetCurrentShip();
            _isOnShip = _currentShip != null;

            // Just boarded
            if (_isOnShip && !wasOnShip)
            {
                _boardedShipTime = Time.time;
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionInteractionBehavior] {_companion?.companionName} boarded ship");
            }
            // Just disembarked
            else if (!_isOnShip && wasOnShip)
            {
                if (_isAttached && (_currentAttachType == AttachType.ShipMast || _currentAttachType == AttachType.ShipSeat))
                {
                    Detach();
                }
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionInteractionBehavior] {_companion?.companionName} left ship");
            }

            // Try to attach to ship if we've been on it a bit
            if (_isOnShip && !_isAttached && Time.time - _boardedShipTime > shipAttachDelay)
            {
                if (!IsInCombat())
                {
                    TryAttachToShip();
                }
            }
        }

        private Ship GetCurrentShip()
        {
            // Check if character is in a ship volume
            if (_character != null && _character.InNumShipVolumes > 0)
            {
                // Find the ship we're on
                var colliders = Physics.OverlapSphere(transform.position, 3f);
                foreach (var col in colliders)
                {
                    var ship = col.GetComponentInParent<Ship>();
                    if (ship != null) return ship;
                }
            }
            return null;
        }

        private bool TryAttachToShip()
        {
            if (_currentShip == null) return false;

            // Check owner - if owner is controlling the ship, don't attach to controls
            var owner = _companion?.GetOwner();
            
            // Try mast first if preferred
            if (preferMast)
            {
                var mastAttach = FindShipMastAttach(_currentShip);
                if (mastAttach != null && !IsAttachPointOccupied(mastAttach))
                {
                    // Ship attachments are auto-behavior, not commanded
                    return AttachTo(mastAttach, "attach_mast", Vector3.zero, AttachType.ShipMast, _currentShip.gameObject, isCommanded: false);
                }
            }

            // Find available ship seats
            var seatAttach = FindAvailableShipSeat(_currentShip, owner);
            if (seatAttach.HasValue)
            {
                var seat = seatAttach.Value;
                return AttachTo(seat.attachPoint, seat.animation, seat.detachOffset, AttachType.ShipSeat, _currentShip.gameObject, isCommanded: false);
            }

            // If no mast preference, try mast as fallback
            if (!preferMast)
            {
                var mastAttach = FindShipMastAttach(_currentShip);
                if (mastAttach != null && !IsAttachPointOccupied(mastAttach))
                {
                    return AttachTo(mastAttach, "attach_mast", Vector3.zero, AttachType.ShipMast, _currentShip.gameObject, isCommanded: false);
                }
            }

            return false;
        }

        private Transform FindShipMastAttach(Ship ship)
        {
            if (ship.m_mastObject == null) return null;

            // Look for attach point on or near mast
            var mastTransform = ship.m_mastObject.transform;
            
            // Check for explicit attach point
            var attachPoint = mastTransform.Find("attach_point") ?? 
                              mastTransform.Find("Attach") ??
                              mastTransform.Find("MastAttach");

            if (attachPoint != null) return attachPoint;

            // Use mast base as fallback
            return mastTransform;
        }

        private (Transform attachPoint, string animation, Vector3 detachOffset)? FindAvailableShipSeat(Ship ship, Player owner)
        {
            // Search for Chair or other seat components on the ship
            var chairs = ship.GetComponentsInChildren<Chair>(true);
            foreach (var chair in chairs)
            {
                if (chair.m_attachPoint == null) continue;
                
                // Skip if owner is sitting here
                if (owner != null && IsPlayerAttachedTo(owner, chair.m_attachPoint))
                    continue;

                if (!IsAttachPointOccupied(chair.m_attachPoint))
                {
                    return (chair.m_attachPoint, chair.m_attachAnimation, chair.m_detachOffset);
                }
            }

            // Look for generic attach points
            var attachPoints = new List<Transform>();
            FindAttachPointsRecursive(ship.transform, attachPoints);

            foreach (var attachPoint in attachPoints)
            {
                // Skip ship controls
                if (attachPoint.name.ToLowerInvariant().Contains("control")) continue;
                if (attachPoint.name.ToLowerInvariant().Contains("rudder")) continue;
                
                // Skip if owner is here
                if (owner != null && IsPlayerNearPoint(owner, attachPoint.position, 0.5f))
                    continue;

                if (!IsAttachPointOccupied(attachPoint))
                {
                    string anim = attachPoint.name.ToLowerInvariant().Contains("stool") ? "attach_stool" : "attach_chair";
                    return (attachPoint, anim, Vector3.zero);
                }
            }

            return null;
        }

        private void FindAttachPointsRecursive(Transform parent, List<Transform> results)
        {
            foreach (Transform child in parent)
            {
                string name = child.name.ToLowerInvariant();
                if (name.Contains("attach") || name.Contains("seat"))
                {
                    results.Add(child);
                }
                FindAttachPointsRecursive(child, results);
            }
        }

        #endregion

        #region Owner Reaction

        private void UpdateOwnerSittingReaction()
        {
            if (!sitWhenOwnerSits) return;
            if (_isAttached) return;
            if (IsInCombat()) return;
            
            // CRITICAL: Never react to owner sitting when a player command is active
            // The player gave us a task - don't get distracted because owner sat down
            if (_stateController != null && _stateController.IsPlayerCommandActive) return;
            if (_combatMovement != null && _combatMovement.HasCommandPriority) return;

            var owner = _companion?.GetOwner();
            if (owner == null) return;

            // Check periodically
            if (Time.time - _lastOwnerSitCheck < 0.5f) return;
            _lastOwnerSitCheck = Time.time;

            bool ownerSitting = IsCharacterAttached(owner);

            // Owner just sat down
            if (ownerSitting && !_ownerWasSitting)
            {
                _ownerSatTime = Time.time;
            }

            // React after delay
            if (ownerSitting && Time.time - _ownerSatTime > ownerSitReactionDelay)
            {
                TryFindAndSitNearOwner(owner);
            }

            _ownerWasSitting = ownerSitting;
        }

        private bool IsCharacterAttached(Character character)
        {
            if (character == null) return false;
            
            // Check if character is attached (sitting/lying)
            try
            {
                var attachedField = typeof(Character).GetField("m_attached", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (attachedField != null)
                {
                    return (bool)attachedField.GetValue(character);
                }
            }
            catch { }

            // Fallback: check animator state
            var animator = character.GetComponentInChildren<Animator>();
            if (animator != null)
            {
                return animator.GetBool("sitting");
            }

            return false;
        }

        private bool TryFindAndSitNearOwner(Player owner)
        {
            if (owner == null) return false;

            // Find a seat near the owner
            var seat = FindNearestAvailableSeat(owner.transform.position, ownerSitSearchRadius, owner);
            if (seat.HasValue)
            {
                // Move toward the seat first if needed
                float distToSeat = Vector3.Distance(transform.position, seat.Value.attachPoint.position);
                if (distToSeat > 2f)
                {
                    // TODO: Set movement destination to seat
                    // For now, just sit if close enough
                    return false;
                }

                // Sitting because owner sat - this is like a commanded action, don't auto-cancel
                return AttachTo(seat.Value.attachPoint, seat.Value.animation, seat.Value.detachOffset, 
                    seat.Value.attachType, seat.Value.sourceObject, isCommanded: true);
            }

            return false;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Called by CompanionIdleBehavior when wandering near a chair.
        /// </summary>
        public bool TrySitOnNearbyChair()
        {
            if (!enableSeating) return false;
            if (_isAttached) return false;
            if (IsInCombat()) return false;
            if (Time.time - _lastSitAttempt < sitCooldown) return false;
            
            // CRITICAL: Never sit when a player command is active
            if (_stateController != null && _stateController.IsPlayerCommandActive) return false;
            if (_combatMovement != null && _combatMovement.HasCommandPriority) return false;

            _lastSitAttempt = Time.time;

            // Random chance
            if (UnityEngine.Random.value > sitChanceWhileWandering) return false;

            var seat = FindNearestAvailableSeat(transform.position, seatDetectionRadius, _companion?.GetOwner());
            if (seat.HasValue)
            {
                // Spontaneous sitting (not commanded) - can be cancelled if owner moves
                return AttachTo(seat.Value.attachPoint, seat.Value.animation, seat.Value.detachOffset,
                    seat.Value.attachType, seat.Value.sourceObject, isCommanded: false);
            }

            return false;
        }

        /// <summary>
        /// Tries to find and sit on any available seat.
        /// </summary>
        /// <param name="isCommanded">If true, this sit was commanded by the player and should not auto-cancel when owner moves.</param>
        public bool TryFindAndSit(bool isCommanded = false)
        {
            if (!enableSeating) return false;
            if (_isAttached) return false;
            if (IsInCombat()) return false;
            
            // CRITICAL: Never spontaneously sit when a player command is active
            // (unless this IS the commanded sit action)
            if (!isCommanded)
            {
                if (_stateController != null && _stateController.IsPlayerCommandActive) return false;
                if (_combatMovement != null && _combatMovement.HasCommandPriority) return false;
            }

            var seat = FindNearestAvailableSeat(transform.position, seatDetectionRadius, _companion?.GetOwner());
            if (seat.HasValue)
            {
                return AttachTo(seat.Value.attachPoint, seat.Value.animation, seat.Value.detachOffset,
                    seat.Value.attachType, seat.Value.sourceObject, isCommanded);
            }

            return false;
        }

        /// <summary>
        /// Forces the companion to detach from current attachment.
        /// </summary>
        public void ForceDetach()
        {
            Detach();
        }

        /// <summary>
        /// Called when combat starts - detaches immediately.
        /// </summary>
        public void OnCombatStarted()
        {
            // Release all interactable occupancies for this companion
            if (_character != null)
            {
                InteractableOccupancyManager.ReleaseAllForOccupant(_character);
            }
            
            if (_isAttached)
            {
                // Unlock movement before detaching
                if (_combatMovement != null)
                {
                    _combatMovement.UnlockMovement();
                }
                Detach();
            }
        }

        #endregion

        #region Seat Finding

        private (Transform attachPoint, string animation, Vector3 detachOffset, AttachType attachType, GameObject sourceObject)? 
            FindNearestAvailableSeat(Vector3 searchCenter, float radius, Player excludeOwnerSeat)
        {
            float nearestDist = float.MaxValue;
            (Transform attachPoint, string animation, Vector3 detachOffset, AttachType attachType, GameObject sourceObject)? nearest = null;

            var colliders = Physics.OverlapSphere(searchCenter, radius);
            
            foreach (var col in colliders)
            {
                if (col == null) continue;

                // Check for Chair component (standard chairs)
                var chair = col.GetComponent<Chair>() ?? col.GetComponentInParent<Chair>();
                if (chair != null && chair.m_attachPoint != null)
                {
                    if (excludeOwnerSeat != null && IsPlayerAttachedTo(excludeOwnerSeat, chair.m_attachPoint))
                        continue;

                    if (!IsAttachPointOccupied(chair.m_attachPoint))
                    {
                        float dist = Vector3.Distance(searchCenter, chair.m_attachPoint.position);
                        if (dist < nearestDist)
                        {
                            nearestDist = dist;
                            AttachType type = DetermineChairType(chair);
                            nearest = (chair.m_attachPoint, chair.m_attachAnimation, chair.m_detachOffset, type, chair.gameObject);
                        }
                    }
                }

                // Non-Chair attach targets: ONLY real Beds.
                //
                // This branch previously accepted ANY Interactable that happened to expose a
                // child transform named "attach"/"seat" (via FindAttachPointInObject). That
                // heuristic false-matched prop interactables whose item-attach points look like
                // seat anchors — most visibly the boss-trophy ItemStand hooks at the starting
                // temple, which a companion would "sit" on as if it were a stool. CookingStation,
                // CraftingStation and Fireplace are all Interactables with attach-like children
                // too and were equally vulnerable.
                //
                // Vanilla has exactly two things a Character physically attaches to: Chair (sit,
                // handled above) and Bed (lie down). Gating this branch on an explicit Bed
                // component preserves the lie-on-bed behaviour while excluding every prop
                // interactable — ItemStand included — by construction.
                if (chair == null && col.GetComponentInParent<Bed>() != null)
                {
                    var attachPoint = FindAttachPointInObject(col.gameObject);
                    if (attachPoint != null && !IsAttachPointOccupied(attachPoint))
                    {
                        if (excludeOwnerSeat != null && IsPlayerNearPoint(excludeOwnerSeat, attachPoint.position, 0.5f))
                            continue;

                        float dist = Vector3.Distance(searchCenter, attachPoint.position);
                        if (dist < nearestDist)
                        {
                            nearestDist = dist;
                            nearest = (attachPoint, "attach_bed", Vector3.zero, AttachType.Bed, col.gameObject);
                        }
                    }
                }
            }

            return nearest;
        }

        private Transform FindAttachPointInObject(GameObject obj)
        {
            // Look for common attach point names
            string[] attachNames = { "attach_point", "attach", "Attach", "AttachPoint", "seat", "Seat" };
            
            foreach (var name in attachNames)
            {
                var found = obj.transform.Find(name);
                if (found != null) return found;
            }

            // Search recursively
            foreach (Transform child in obj.transform)
            {
                string childName = child.name.ToLowerInvariant();
                if (childName.Contains("attach") || childName.Contains("seat"))
                {
                    return child;
                }
            }

            return null;
        }

        private AttachType DetermineChairType(Chair chair)
        {
            if (chair == null) return AttachType.Chair;

            string objName = chair.gameObject.name.ToLowerInvariant();
            string animName = chair.m_attachAnimation?.ToLowerInvariant() ?? "";

            if (objName.Contains("stool") || animName.Contains("stool"))
                return AttachType.Stool;
            if (objName.Contains("bench"))
                return AttachType.Bench;
            if (objName.Contains("bed") || animName.Contains("bed"))
                return AttachType.Bed;

            return AttachType.Chair;
        }

        #endregion

        #region Attachment

        private bool AttachTo(Transform attachPoint, string animation, Vector3 detachOffset, AttachType attachType, GameObject sourceObject, bool isCommanded = false)
        {
            if (attachPoint == null)
            {
                Debug.LogWarning("[CompanionInteractionBehavior] AttachTo failed: attachPoint is null");
                return false;
            }
            if (_character == null)
            {
                Debug.LogWarning("[CompanionInteractionBehavior] AttachTo failed: _character is null");
                return false;
            }

            try
            {
                // Calculate random sit duration
                float duration = UnityEngine.Random.Range(sitDurationMin, sitDurationMax);
                
                // CRITICAL: Try to register occupancy before attaching
                if (sourceObject != null)
                {
                    if (!InteractableOccupancyManager.TryOccupy(sourceObject, _character, duration + 10f))
                    {
                        if (VerboseLogging)
                        {
                            Debug.Log($"[CompanionInteractionBehavior] {_companion?.companionName} cannot attach to {sourceObject.name} - already occupied");
                        }
                        return false;
                    }
                }

                // Stop all movement before attaching
                StopMovement();

                // Ensure the animation name is valid
                if (string.IsNullOrEmpty(animation))
                {
                    animation = "attach_chair";
                }

                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionInteractionBehavior] {_companion?.companionName} attempting to attach to " +
                        $"{attachPoint.name} with animation '{animation}' at position {attachPoint.position}, commanded={isCommanded}");
                }
                
                // Use coroutine for proper attachment after physics settles
                StartCoroutine(AttachToCoroutine(attachPoint, animation, detachOffset, attachType, sourceObject, duration, isCommanded));

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionInteractionBehavior] Failed to attach: {ex.Message}\n{ex.StackTrace}");
                
                // Release occupancy on failure
                if (sourceObject != null)
                {
                    InteractableOccupancyManager.Release(sourceObject, _character);
                }
                
                return false;
            }
        }
        
        /// <summary>
        /// Coroutine to properly attach after physics settles.
        /// </summary>
        private System.Collections.IEnumerator AttachToCoroutine(Transform attachPoint, string animation, Vector3 detachOffset,
            AttachType attachType, GameObject sourceObject, float duration, bool isCommanded)
        {
            // Mirror what Chair.Interact() does for players:
            //   character.AttachStart(m_attachPoint, gameObject, false, false, m_inShip,
            //                         m_attachAnimation, m_detachOffset, null)
            // AttachStart() itself:
            //   - sets m_attached = true
            //   - sets m_body.useGravity = false
            //   - zeroes m_body.velocity / angularVelocity
            //   - calls m_zanim.SetBool(animation, true)
            //   - records m_attachPoint for FixedUpdate to track
            // Character.FixedUpdate() while attached:
            //   - locks transform.position = m_attachPoint.TransformPoint(m_attachOffset)
            //   - zeroes velocity every tick (non-kinematic, no warning)
            // We do NOT disable the collider or touch isKinematic ï¿½ vanilla never does either.

            // 1. Stop all movement first (before any physics change)
            if (_character != null)
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.linearVelocity  = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }

            // 2. One fixed-update so physics sees the zeroed velocity before we move the transform
            yield return new WaitForFixedUpdate();

            if (attachPoint == null)
            {
                if (_combatMovement != null && _combatMovement.IsMovementLocked)
                    _combatMovement.UnlockMovement();
                if (sourceObject != null) InteractableOccupancyManager.Release(sourceObject, _character);
                yield break;
            }

            // 3. Snap to the attach point (same as vanilla ï¿½ player is warped to seat)
            transform.position = attachPoint.position;
            transform.rotation = attachPoint.rotation;

            yield return null;  // let the engine apply the position before attaching

            // 4. Determine ship flag
            bool onShip = _isOnShip;
            if (sourceObject != null)
            {
                var chair = sourceObject.GetComponent<Chair>();
                if (chair != null) onShip = chair.m_inShip;
            }

            // 5. Call AttachStart exactly as Chair.Interact does.
            //    Passing sourceObject lets the Character record the ZDO of the attached
            //    object for network sync (same as vanilla).
            //
            //    *** CAVEAT *** Character.AttachStart is an EMPTY virtual on the base
            //    class. Player overrides it to do the real work (SetBool on the
            //    animator, useGravity=false, position lock, ignore-collision, etc.).
            //    Companions inherit from Humanoid which does NOT override, so calling
            //    AttachStart on a companion is a no-op â€” the companion stands at the
            //    chair instead of sitting. We replicate the visual parts of
            //    Player.AttachStart immediately below; the per-frame position lock
            //    lives in FixedUpdate (mirrors Player.UpdateAttach).
            _character.AttachStart(attachPoint, sourceObject, false, false, onShip,
                animation, detachOffset, null);

            // 5a. Trigger the sit/lay animation.
            //     Chair.m_attachAnimation is the bool param the chair uses
            //     (e.g. "attach_chair", "attach_stool", "attach_bed", or chair-emote
            //     names like "emote_sit"). SetBool is idempotent â€” safe even if the
            //     animator doesn't have the parameter; it's silently ignored.
            if (_zanim != null && !string.IsNullOrEmpty(animation))
            {
                _zanim.SetBool(animation, true);
            }

            // 5b. Freeze physics completely during attachment.
            //     useGravity=false alone is NOT enough â€” Valheim's Character.FixedUpdate
            //     still calls AddForce() for movement direction every tick, which fights
            //     our per-frame position lock and causes the companion to bounce up/down.
            //
            //     We previously set isKinematic=true here, but Unity 6 logs a warning every
            //     physics tick for any velocity write to a kinematic body â€” and Valheim's
            //     Character.UpdateMotion writes m_body.linearVelocity every FixedUpdate
            //     unconditionally. That's where the "Setting linear/angular velocity of a
            //     kinematic body is not supported" spam came from.
            //
            //     RigidbodyConstraints.FreezeAll achieves the same anchor â€” gravity,
            //     AddForce, and direct velocity writes can all happen without translating
            //     the body â€” and vanilla's velocity writes are no longer illegal so the
            //     warnings stop. Position is locked at whatever it was when we set the
            //     constraint, so we still write transform.position to the sit point in
            //     FixedUpdate to nail the exact pose.
            if (_rigidbody != null)
            {
                _preAttachConstraints      = _rigidbody.constraints;
                _rigidbody.linearVelocity  = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
                _rigidbody.useGravity      = false;
                _rigidbody.constraints     = RigidbodyConstraints.FreezeAll;
            }

            // 6. Record state
            _isAttached            = true;
            _isCommandedAttachment = isCommanded;
            _currentAttachObject   = sourceObject;
            _currentAttachType     = attachType;
            _currentAttachPoint    = attachPoint;
            _attachEndTime         = Time.time + duration;
            _attachHardTimeoutTime = Time.time + attachHardTimeout;
            _targetSitPosition     = attachPoint.position;
            _targetSitRotation     = attachPoint.rotation;

            LockMovementForDuration(duration);

            Debug.Log($"[CompanionInteractionBehavior] {_companion?.companionName} attached to {attachType} " +
                $"({sourceObject?.name}) using animation '{animation}', duration: {duration:F1}s, commanded: {isCommanded}");
        }
        
        private void StopMovement()
        {
            // Lock movement to prevent other systems from fighting
            // We'll update this with the actual duration once we know it
            if (_combatMovement != null)
            {
                _combatMovement.LockMovement("PreAttach", 10f);
            }
            
            // Stop character movement before sitting
            if (_character != null)
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }

            // Stop rigidbody if present - but only if NOT kinematic
            // Unity 6 doesn't allow setting velocity on kinematic rigidbodies
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
        }
        
        /// <summary>
        /// Locks movement for the specified duration.
        /// Called after we know the actual sit duration.
        /// </summary>
        private void LockMovementForDuration(float duration)
        {
            if (_combatMovement != null)
            {
                // Lock for full duration + buffer
                _combatMovement.LockMovement($"Attached_{_currentAttachType}", duration + 5f);
            }
        }

        private void Detach()
        {
            if (!_isAttached) return;

            try
            {
                // CRITICAL: Release occupancy when detaching
                if (_currentAttachObject != null)
                {
                    InteractableOccupancyManager.Release(_currentAttachObject, _character);
                }
                
                // Exit state controller ChairSit state
                if (_stateController != null)
                {
                    _stateController.ExitState(CompanionStateController.CompanionState.Idle);
                }
                
                // Unlock movement first
                if (_combatMovement != null)
                {
                    _combatMovement.UnlockMovement();
                }

                // Character.AttachStop is also an EMPTY virtual on the base class
                // (Player overrides it; Humanoid doesn't). Restore physics state
                // ourselves before calling so movement resumes after standing.
                // Must restore constraints BEFORE AttachStop so the character
                // physics system can apply forces on the next FixedUpdate.
                if (_rigidbody != null)
                {
                    _rigidbody.constraints = _preAttachConstraints;
                    _rigidbody.useGravity  = true;
                }
                if (_character != null)
                {
                    _character.AttachStop();
                }

                // Zero out residual velocity so the companion doesn't drift after standing
                if (_rigidbody != null && !_rigidbody.isKinematic)
                {
                    _rigidbody.linearVelocity  = Vector3.zero;
                    _rigidbody.angularVelocity = Vector3.zero;
                }

                // Belt-and-suspenders: reset all attach animation bools in case
                // the companion's ZSyncAnimation wasn't the one AttachStop used.
                ResetAllAttachAnimations();
                
                // Trigger idle animation to ensure clean transition
                StartCoroutine(TransitionToIdleCoroutine());

                if (VerboseLogging)
                    Debug.Log($"[CompanionInteractionBehavior] {_companion?.companionName} detached from {_currentAttachType}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionInteractionBehavior] Failed to detach: {ex.Message}");
            }
            finally
            {
                _isAttached            = false;
                _isCommandedAttachment = false;
                _currentAttachObject   = null;
                _currentAttachType     = AttachType.None;
                _currentAttachPoint    = null;
                _targetSitPosition     = Vector3.zero;
                _targetSitRotation     = Quaternion.identity;

                // Ensure movement is unlocked even on error
                if (_combatMovement != null && _combatMovement.IsMovementLocked)
                    _combatMovement.UnlockMovement();
            }
        }
        
        /// <summary>
        /// Resets all attach-related animation bools to ensure clean state.
        /// CRITICAL: This must clear ALL sitting/laying/resting bools to prevent sliding.
        /// </summary>
        private void ResetAllAttachAnimations()
        {
            // Vanilla StopEmote clears m_emoteID and the current emote bool.
            // Call it first so the Mecanim state machine exits any active emote.
            if (_stateController != null)
                _stateController.ForceStopEmote();
            else if (_character != null)
            {
                var m = _character.GetType().GetMethod("StopEmote",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                m?.Invoke(_character, null);
            }

            // Vanilla StopEmote only knows about the tracked emote; the chair-attach
            // bools are a separate system and must be cleared manually.
            var zanim = GetComponent<ZSyncAnimation>();
            if (zanim != null)
            {
                zanim.SetBool("attach_chair", false);
                zanim.SetBool("attach_stool", false);
                zanim.SetBool("attach_bed",   false);
                zanim.SetBool("attach_mast",  false);
                zanim.SetBool("sitting",  false);
                zanim.SetBool("resting",  false);
                zanim.SetBool("sleeping", false);
            }

            if (_animator != null)
            {
                foreach (var b in new[]{"attach_chair","attach_stool","attach_bed","attach_mast",
                                        "sitting","resting","sleeping"})
                    if (HasAnimatorParameter(b)) _animator.SetBool(b, false);
            }
        }
        
        /// <summary>
        /// Coroutine to smoothly transition back to idle state after detaching.
        /// </summary>
        private System.Collections.IEnumerator TransitionToIdleCoroutine()
        {
            // Wait a frame for attach stop to complete
            yield return null;
            
            // Reset animations again to ensure clean state
            ResetAllAttachAnimations();
            
            // Wait for animation system to process
            yield return new WaitForSeconds(0.1f);
            
            // DON'T call Character.SetMoveDir() if rigidbody is kinematic
            // Character.SetMoveDir() internally sets velocity which causes Unity 6 warnings
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                // Safe to call SetMoveDir - rigidbody is not kinematic
                if (_character != null)
                {
                    _character.SetMoveDir(Vector3.zero);
                    _character.SetWalk(false);
                    _character.SetRun(false);
                }
                
                // Zero velocity directly
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            
            // CRITICAL: Force full animation state reset to fix sliding/stuck legs
            ForceAnimationStateReset();
            
            // Wait a bit more then force reset again to ensure it takes
            yield return new WaitForSeconds(0.2f);
            ForceAnimationStateReset();
            
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionInteractionBehavior] {_companion?.companionName} transitioned to idle state");
            }
        }
        
        /// <summary>
        /// Forces a complete animation state reset to fix stuck/sliding animations.
        /// This ensures the character returns to proper idle/movement states.
        /// CRITICAL: This MUST clear ALL possible emote/sit bools to prevent sliding.
        /// </summary>
        private void ForceAnimationStateReset()
        {
            // Vanilla StopEmote clears m_emoteID and the active emote bool so the
            // Mecanim state machine exits the looping emote state.
            if (_stateController != null)
                _stateController.ForceStopEmote();
            else if (_character != null)
            {
                var m = _character.GetType().GetMethod("StopEmote",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                m?.Invoke(_character, null);
            }

            // Attach-system bools are separate from the emote system and must be
            // cleared manually ï¿½ vanilla StopEmote does not touch them.
            string[] attachBools = {
                "attach_chair", "attach_stool", "attach_bed", "attach_mast",
                "sitting", "resting", "sleeping"
            };

            if (_zanim != null)
            {
                foreach (var b in attachBools) _zanim.SetBool(b, false);
                _zanim.SetFloat("statef", 0f);
                _zanim.SetFloat("statei", 0f);
                _zanim.SetTrigger("idle");
                // Required for looping-emote exit transitions; covers the
                // _stateController == null fallback above where
                // ForceStopEmote couldn't fire it for us.
                _zanim.SetTrigger("emote_stop");
            }

            if (_animator != null)
            {
                foreach (var b in attachBools)
                    if (HasAnimatorParameter(b)) _animator.SetBool(b, false);
                if (HasAnimatorParameter("forward_speed"))  _animator.SetFloat("forward_speed",  0f);
                if (HasAnimatorParameter("sideways_speed")) _animator.SetFloat("sideways_speed", 0f);
                if (HasAnimatorParameter("turn_speed"))     _animator.SetFloat("turn_speed",     0f);
                if (HasAnimatorParameter("moving"))         _animator.SetBool("moving", false);
                _animator.SetTrigger(EmoteStopTriggerHash);
                _animator.Update(0f);
            }
            
            // DON'T call Character.SetMoveDir() if rigidbody is kinematic
            // Character.SetMoveDir() internally sets velocity which causes Unity 6 warnings
            // Only call these methods when the rigidbody is NOT kinematic
            if (_rigidbody == null || !_rigidbody.isKinematic)
            {
                if (_character != null)
                {
                    _character.SetMoveDir(Vector3.zero);
                    _character.SetWalk(false);
                    _character.SetRun(false);
                }
            }
            
            // Also tell state controller to reset if available
            if (_stateController != null)
            {
                _stateController.ForceStopEmote();
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

        private void UpdateAttachment()
        {
            if (!_isAttached) return;
            
            // CRITICAL: Check for absolute priority player commands (Move/Attack)
            // These MUST detach immediately - player commands override everything
            if (_stateController != null && _stateController.HasAbsolutePriorityCommand)
            {
                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionInteractionBehavior] {_companion?.companionName} detaching due to absolute priority player command");
                }
                Detach();
                return;
            }

            // Check for combat - always interrupt for combat
            if (IsInCombat())
            {
                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionInteractionBehavior] {_companion?.companionName} detaching due to combat");
                }
                Detach();
                return;
            }

            // Check for timeout
            if (Time.time >= _attachEndTime || Time.time >= _attachHardTimeoutTime)
            {
                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionInteractionBehavior] {_companion?.companionName} detaching due to timeout");
                }
                Detach();
                return;
            }

            // Check if source object still exists
            if (_currentAttachObject == null)
            {
                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionInteractionBehavior] {_companion?.companionName} detaching - attach object destroyed");
                }
                Detach();
                return;
            }

            // Server-side reconcile (CompanionTeleportService.OnReconcileRequest)
            // can rewrite this ZDO's position when the owner long-jumps. For
            // cross-zone teleports the source zone unloads and the
            // attach-object-destroyed check above already fires, but for an
            // in-zone teleport the chair stays loaded and the ZDO position
            // diverges from the chair while FixedUpdate keeps pinning the
            // visual transform. Detect the divergence and detach so the
            // companion actually lands at the player.
            if (_currentAttachPoint != null && _character != null)
            {
                var nview = _character.m_nview;
                if (nview != null && nview.IsValid())
                {
                    Vector3 zdoPos = nview.GetZDO().GetPosition();
                    if (Vector3.Distance(zdoPos, _currentAttachPoint.position) > 10f)
                    {
                        if (VerboseLogging)
                        {
                            Debug.Log($"[CompanionInteractionBehavior] {_companion?.companionName} detaching â€” ZDO position has diverged from chair (likely server reconcile teleport)");
                        }
                        Detach();
                        return;
                    }
                }
            }

            // For ships, check if we're still on the ship
            if (_currentAttachType == AttachType.ShipMast || _currentAttachType == AttachType.ShipSeat)
            {
                if (!_isOnShip)
                {
                    Detach();
                    return;
                }
            }

            // Sitting takes precedence over follow distance: a sitting
            // companion ignores the owner walking away. Follow movement
            // requests are no-ops while attached because EnforceAttachmentState
            // keeps CompanionCombatMovement locked. The only valid reasons to
            // get the companion up are already handled above:
            //   - Absolute-priority player command (Move/Attack)
            //   - Combat
            //   - Natural sit-duration timeout
            //   - The chair reference went null. This happens when
            //     Valheim's own zone-streaming system unloads the zone
            //     containing the chair (e.g. the owner long-jumped far
            //     enough that the chair's zone is no longer in the
            //     player's streaming radius). We never destroy chairs
            //     ourselves â€” Detach only stands the companion up and
            //     releases its occupancy slot; the chair GameObject is
            //     untouched and any character can sit in it again.
            // Stay-mode companions (companion_wasfollowing=false) are
            // unaffected â€” they sit by design and continue to.
        }

        #endregion

        #region Helpers

        private bool IsInCombat()
        {
            return _combatMovement != null && _combatMovement.IsInCombat;
        }

        private bool IsAttachPointOccupied(Transform attachPoint)
        {
            if (attachPoint == null) return true;
            
            // Check using the centralized occupancy manager for crowded positions
            if (InteractableOccupancyManager.IsPositionCrowded(attachPoint.position, 
                InteractableOccupancyManager.PERSONAL_SPACE_RADIUS, _character))
                return true;

            var nearby = Physics.OverlapSphere(attachPoint.position, 0.5f);
            foreach (var col in nearby)
            {
                // Check for any Character (NPC, companion, monster)
                var character = col.GetComponent<Character>();
                if (character != null && character != _character)
                    return true;
                    
                // Also explicitly check for Player component in case Character check missed it
                var player = col.GetComponent<Player>();
                if (player != null)
                    return true;
            }

            return false;
        }

        private bool IsPlayerAttachedTo(Player player, Transform attachPoint)
        {
            if (player == null || attachPoint == null) return false;
            return Vector3.Distance(player.transform.position, attachPoint.position) < 0.5f && IsCharacterAttached(player);
        }

        private bool IsPlayerNearPoint(Player player, Vector3 point, float radius)
        {
            if (player == null) return false;
            return Vector3.Distance(player.transform.position, point) < radius;
        }

        #endregion
    }
}
