using UnityEngine;
using System;
using System.Collections.Generic;
using FiresCore.Npc.Movement;
using FiresCore.Npc.IdleBehaviors;
using FiresCore.Npc.Animation;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.Interactions
{
    /// <summary>
    /// Seats companions on chairs, benches, stools and beds when idle or when the owner sits nearby, and holds
    /// them to a mast or seat while the owner is aboard a ship, with the matching attach animations. Works
    /// alongside CompanionIdleBehavior.
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

        // Where the owner stood when this attachment began — CompanionLeash.ShouldBreakForFollow measures their
        // displacement from it to decide when a follower has to get up.
        private Vector3 _ownerAnchor;
        private float _lastFollowBreakCheck;

        // Kinematic state before attachment — restored on Detach so normal movement resumes
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

        // The pose bool AttachTo set; Detach clears exactly this one, as vanilla Player.AttachStop does.
        private string _currentAttachAnimation;

        private const string MastAttachAnimation = "attach_mast";
        private const string BedAttachAnimation = "attach_bed";
        private static readonly Vector3 BedDetachOffset = new Vector3(0f, 0.5f, 0f);

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
            // doesn't fight a moving rigidbody. Skip if kinematic — Unity logs a
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
                foreach (var collider in colliders)
                {
                    var ship = collider.GetComponentInParent<Ship>();
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
                var holdfast = FindShipHoldfast(_currentShip, owner);
                if (holdfast != null)
                {
                    // Ship attachments are auto-behavior, not commanded
                    return AttachTo(holdfast.m_attachPoint, holdfast.m_attachAnimation, holdfast.m_detachOffset, AttachType.ShipMast, _currentShip.gameObject, isCommanded: false);
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
                var holdfast = FindShipHoldfast(_currentShip, owner);
                if (holdfast != null)
                {
                    return AttachTo(holdfast.m_attachPoint, holdfast.m_attachAnimation, holdfast.m_detachOffset, AttachType.ShipMast, _currentShip.gameObject, isCommanded: false);
                }
            }

            return false;
        }

        /// <summary>The mast holdfast is a Chair on the ship whose pose is attach_mast ($ship_holdfast); Ship.m_mastObject
        /// has no attach point of its own, so its root pinned companions inside the mast.</summary>
        private Chair FindShipHoldfast(Ship ship, Player owner)
        {
            foreach (var chair in ship.GetComponentsInChildren<Chair>(true))
            {
                if (chair.m_attachPoint == null || chair.m_attachAnimation != MastAttachAnimation) continue;
                if (owner != null && IsPlayerAttachedTo(owner, chair.m_attachPoint)) continue;
                if (!IsAttachPointOccupied(chair.m_attachPoint)) return chair;
            }
            return null;
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

            // Vanilla ships seat through Chair components only (benches: attach_sitship, holdfasts: attach_mast and
            // attach_dragon); name-matched children picked rope anchors metres up the mast.
            return null;
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

        // m_attached lives on Player, not Character; Player.IsAttached is public.
        private static bool IsCharacterAttached(Character character) => character is Player player && player.IsAttached();

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
            
            foreach (var collider in colliders)
            {
                if (collider == null) continue;

                // Check for Chair component (standard chairs)
                var chair = collider.GetComponent<Chair>() ?? collider.GetComponentInParent<Chair>();
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

                // Beds are the only non-chair attach target. Accepting any interactable with an "attach" or "seat" child
                // sat companions on item stands, cooking and crafting stations, and fireplaces. Vanilla Bed.Interact
                // lies the sleeper on m_spawnPoint with a (0, 0.5, 0) detach offset.
                var bed = chair == null ? collider.GetComponentInParent<Bed>() : null;
                if (bed != null && bed.m_spawnPoint != null && !IsAttachPointOccupied(bed.m_spawnPoint))
                {
                    if (excludeOwnerSeat != null && IsPlayerAttachedTo(excludeOwnerSeat, bed.m_spawnPoint))
                        continue;

                    float dist = Vector3.Distance(searchCenter, bed.m_spawnPoint.position);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearest = (bed.m_spawnPoint, BedAttachAnimation, BedDetachOffset, AttachType.Bed, bed.gameObject);
                    }
                }
            }

            return nearest;
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
            // Mirrors Chair.Interact for players: AttachStart turns off gravity, zeroes velocity and starts the animation,
            // and Character.FixedUpdate then pins the body to the attach point. Colliders and isKinematic stay untouched,
            // as in vanilla.

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

            // 3. Snap to the attach point (same as vanilla - player is warped to seat)
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
            //    AttachStart on a companion is a no-op — the companion stands at the
            //    chair instead of sitting. We replicate the visual parts of
            //    Player.AttachStart immediately below; the per-frame position lock
            //    lives in FixedUpdate (mirrors Player.UpdateAttach).
            _character.AttachStart(attachPoint, sourceObject, false, false, onShip,
                animation, detachOffset, null);

            // 5a. Trigger the sit/lay animation.
            //     Chair.m_attachAnimation is the bool param the chair uses
            //     (e.g. "attach_chair", "attach_stool", "attach_bed", or chair-emote
            //     names like "emote_sit"). SetBool is idempotent — safe even if the
            //     animator doesn't have the parameter; it's silently ignored.
            if (_zanim != null && !string.IsNullOrEmpty(animation))
            {
                _zanim.SetBool(animation, true);
            }

            // Freeze the body with RigidbodyConstraints.FreezeAll while seated: useGravity alone let
            // Character.FixedUpdate's forces bounce the companion, and isKinematic made every velocity write from
            // Character.UpdateMotion log a warning. The seat position is still written each FixedUpdate.
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
            _currentAttachAnimation = animation;
            _isCommandedAttachment = isCommanded;
            _currentAttachObject   = sourceObject;
            _currentAttachType     = attachType;
            _currentAttachPoint    = attachPoint;
            _attachEndTime         = Time.time + duration;
            _attachHardTimeoutTime = Time.time + attachHardTimeout;
            _targetSitPosition     = attachPoint.position;
            _targetSitRotation     = attachPoint.rotation;
            _ownerAnchor           = _companion?.GetOwner()?.transform.position ?? transform.position;

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

                // Character.AttachStop is an EMPTY virtual on Humanoid, so restore physics here and release the pose
                // the way Player.AttachStop does: the seated states (SitChair, SitThrone, SitShip, HoldDragon, SitDivan,
                // RideLox...) exit only when their own bool goes false, so a fixed list left thrones and benches posed.
                if (_rigidbody != null)
                {
                    _rigidbody.constraints = _preAttachConstraints;
                    _rigidbody.useGravity  = true;
                }

                // Zero out residual velocity so the companion doesn't drift after standing
                if (_rigidbody != null && !_rigidbody.isKinematic)
                {
                    _rigidbody.linearVelocity  = Vector3.zero;
                    _rigidbody.angularVelocity = Vector3.zero;
                }

                ReleaseAttachPose();
                if (_stateController != null)
                    _stateController.ForceStopEmote();

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
                _currentAttachAnimation = null;
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
        
        /// <summary>Clears exactly the pose bool AttachTo set, as vanilla Player.AttachStop does.</summary>
        private void ReleaseAttachPose()
        {
            if (string.IsNullOrEmpty(_currentAttachAnimation)) return;
            if (_zanim != null) _zanim.SetBool(_currentAttachAnimation, false);
            else if (_animator != null) _animator.SetBool(_currentAttachAnimation, false);
        }

        /// <summary>
        /// Settles the body after standing up; the pose itself is already released by Detach.
        /// </summary>
        private System.Collections.IEnumerator TransitionToIdleCoroutine()
        {
            yield return null;

            // DON'T call Character.SetMoveDir() if rigidbody is kinematic
            // Character.SetMoveDir() internally sets velocity which causes Unity 6 warnings
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                if (_character != null)
                {
                    _character.SetMoveDir(Vector3.zero);
                    _character.SetWalk(false);
                    _character.SetRun(false);
                }

                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }

            ForceAnimationStateReset();

            if (VerboseLogging)
            {
                Debug.Log($"[CompanionInteractionBehavior] {_companion?.companionName} transitioned to idle state");
            }
        }

        /// <summary>
        /// Releases every emote and seat pose (PlayerAnimationCatalog.StopAll) and stops the body. The weapon pose
        /// (statef/statei) and the locomotion floats belong to vanilla Humanoid and Character and are left alone.
        /// </summary>
        private void ForceAnimationStateReset()
        {
            if (_stateController != null)
                _stateController.ForceStopEmote();
            PlayerAnimationCatalog.StopAll(_zanim, _animator);

            // DON'T call Character.SetMoveDir() if rigidbody is kinematic
            // Character.SetMoveDir() internally sets velocity which causes Unity 6 warnings
            if (_rigidbody == null || !_rigidbody.isKinematic)
            {
                if (_character != null)
                {
                    _character.SetMoveDir(Vector3.zero);
                    _character.SetWalk(false);
                    _character.SetRun(false);
                }
            }
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
                            Debug.Log($"[CompanionInteractionBehavior] {_companion?.companionName} detaching — ZDO position has diverged from chair (likely server reconcile teleport)");
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

            // A follower stands up when its owner walks off. While attached, CompanionAI.UpdateAI returns before the
            // FSM and CompanionController.CheckFollowTeleport bails on IsInAnimationThatBlocksTeleport, so nothing
            // else can end this: a companion that sat down while the owner was AFK stayed seated no matter how far
            // they went. A player-commanded sit and a stay-mode companion both keep their seat.
            if (!_isCommandedAttachment && Time.time - _lastFollowBreakCheck >= CompanionLeash.BusyBreakCheckInterval)
            {
                _lastFollowBreakCheck = Time.time;
                if (CompanionLeash.ShouldBreakForFollow(_companion, _ownerAnchor))
                {
                    if (VerboseLogging || Config.ConfigManager.Instance?.configCompanionFollowDiag?.Value == true)
                        Debug.Log($"[CompanionFollowDiag] {_companion?.companionName} standing up — owner is past the follow break distance");
                    Detach();
                }
            }
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
            foreach (var collider in nearby)
            {
                // Check for any Character (NPC, companion, monster)
                var character = collider.GetComponent<Character>();
                if (character != null && character != _character)
                    return true;
                    
                // Also explicitly check for Player component in case Character check missed it
                var player = collider.GetComponent<Player>();
                if (player != null)
                    return true;
            }

            return false;
        }

        private static bool IsPlayerAttachedTo(Player player, Transform attachPoint)
            => player != null && attachPoint != null && player.IsAttached() && player.GetAttachPoint() == attachPoint;

        #endregion
    }
}
