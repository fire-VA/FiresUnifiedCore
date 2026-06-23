using UnityEngine;
using System;
using FiresCore.Npc.Movement;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Npc
{
    // Chair sitting logic
    public partial class CompanionIdleBehavior
    {
        #region Chair Sitting

        private bool TryFindAndSitOnChair()
        {
            if (_character == null) return false;

            // First, try the new interaction behavior if available
            if (_interactionBehavior != null && _interactionBehavior.TryFindAndSit())
            {
                _isSittingOnChair = true;
                SetIdleState(IdleState.SittingOnChair);
                
                float sitDuration = UnityEngine.Random.Range(chairSitDurationMin, chairSitDurationMax);
                _chairSitEndTime = Time.time + sitDuration;
                _chairHardTimeoutTime = Time.time + chairSitHardTimeout;
                
                return true;
            }

            // Fallback to original logic
            Collider[] colliders = Physics.OverlapSphere(transform.position, chairDetectionRadius);

            foreach (var collider in colliders)
            {
                var chair = collider.GetComponent<Chair>();
                if (chair == null)
                    chair = collider.GetComponentInParent<Chair>();

                if (chair != null && !IsChairOccupied(chair))
                {
                    return SitOnChair(chair);
                }
            }

            return false;
        }

        private bool IsChairOccupied(Chair chair)
        {
            if (chair == null) return true;

            Transform attachPoint = chair.m_attachPoint;
            if (attachPoint == null) return true;
            
            // Check using the centralized occupancy manager
            if (InteractableOccupancyManager.IsOccupied(chair.gameObject, _character))
                return true;
            
            // Check if position is crowded
            if (InteractableOccupancyManager.IsPositionCrowded(attachPoint.position, 
                InteractableOccupancyManager.PERSONAL_SPACE_RADIUS, _character))
                return true;

            // Check physics overlap
            Collider[] nearby = Physics.OverlapSphere(attachPoint.position, 0.5f);
            foreach (var col in nearby)
            {
                var character = col.GetComponent<Character>();
                if (character != null && character != _character)
                    return true;
                    
                var player = col.GetComponent<Player>();
                if (player != null)
                    return true;
            }
            
            // Check all other companions
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null || companion == _companion) continue;
                
                var otherIdleBehavior = companion.GetComponent<CompanionIdleBehavior>();
                if (otherIdleBehavior != null && otherIdleBehavior.IsSitting)
                {
                    if (otherIdleBehavior._currentChairObject == chair.gameObject)
                        return true;
                    
                    float dist = Vector3.Distance(companion.transform.position, attachPoint.position);
                    if (dist < 1.0f)
                        return true;
                }
            }
            
            // Check if any player is attached to this chair
            foreach (var player in Player.GetAllPlayers())
            {
                if (player == null) continue;
                if (player.IsAttached())
                {
                    float dist = Vector3.Distance(player.transform.position, attachPoint.position);
                    if (dist < 1.0f)
                        return true;
                }
            }
            
            return false;
        }

        private bool SitOnChair(Chair chair)
        {
            if (chair == null) return false;

            try
            {
                Transform attachPoint = chair.m_attachPoint;
                if (attachPoint == null)
                {
                    Debug.LogWarning($"[CompanionIdleBehavior] Chair {chair.name} has no attach point");
                    return false;
                }
                
                // Check distance
                float distToChair = Vector3.Distance(transform.position, attachPoint.position);
                if (distToChair > chair.m_useDistance * 1.5f)
                {
                    if (VerboseLogging)
                        Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} too far from chair ({distToChair:F1}m > {chair.m_useDistance}m)");
                    return false;
                }
                
                // Try to register occupancy
                float sitDuration = UnityEngine.Random.Range(chairSitDurationMin, chairSitDurationMax);
                if (!InteractableOccupancyManager.TryOccupy(chair.gameObject, _character, sitDuration + 10f))
                {
                    if (VerboseLogging)
                        Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} cannot sit on {chair.name} - already occupied");
                    return false;
                }

                string attachAnimation = chair.m_attachAnimation;
                if (string.IsNullOrEmpty(attachAnimation))
                {
                    attachAnimation = "attach_chair";
                }

                if (_combatMovement != null)
                {
                    _combatMovement.LockMovement("ChairSit", sitDuration + 5f);
                }

                ZeroVelocityAndStopMovement();
                
                StartCoroutine(AttachToChairCoroutine(chair, attachPoint, attachAnimation, sitDuration));

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionIdleBehavior] Failed to sit on chair: {ex.Message}\n{ex.StackTrace}");
                
                InteractableOccupancyManager.Release(chair.gameObject, _character);
                
                if (_combatMovement != null && _combatMovement.IsMovementLocked)
                {
                    _combatMovement.UnlockMovement();
                }
                
                return false;
            }
        }
        
        /// <summary>
        /// Coroutine to properly attach to chair after physics settles.
        /// </summary>
        private System.Collections.IEnumerator AttachToChairCoroutine(Chair chair, Transform attachPoint, string attachAnimation, float sitDuration)
        {
            if (_character != null && (_rigidbody == null || !_rigidbody.isKinematic))
            {
                _character.SetMoveDir(Vector3.zero);
                _character.SetWalk(false);
                _character.SetRun(false);
            }
            
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            
            if (chair == null || IsChairOccupied(chair))
            {
                if (_combatMovement != null && _combatMovement.IsMovementLocked)
                {
                    _combatMovement.UnlockMovement();
                }
                yield break;
            }
            
            transform.position = attachPoint.position;
            transform.rotation = attachPoint.rotation;
            
            if (_rigidbody != null && !_rigidbody.isKinematic)
            {
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }
            
            yield return null;
            
            if (_character != null)
            {
                // Pass chair.gameObject as attachTo — vanilla Chair.Interact does the same so the
                // Character records the ZDO of the attached object for network sync.
                // AttachStart handles useGravity=false, velocity zeroing, and the animation bool.
                _character.AttachStart(attachPoint, chair.gameObject, false, false, chair.m_inShip,
                    attachAnimation, chair.m_detachOffset, null);
            }

            _isSittingOnChair = true;
            _chairSitEndTime = Time.time + sitDuration;
            _chairHardTimeoutTime = Time.time + chairSitHardTimeout;
            _currentChairObject = chair.gameObject;

            SetIdleState(IdleState.SittingOnChair);

            if (VerboseLogging)
                Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} attached to {chair.name} " +
                    $"with animation '{attachAnimation}', duration: {sitDuration:F1}s");
        }


        private void StandUpFromChair()
        {
            if (!_isSittingOnChair) return;

            try
            {
                if (_currentChairObject != null)
                {
                    InteractableOccupancyManager.Release(_currentChairObject, _character);
                }
                
                if (_combatMovement != null && _combatMovement.IsMovementLocked)
                {
                    _combatMovement.UnlockMovement();
                }
                
                if (_combatMovement != null)
                {
                    _combatMovement.ClearMoveDestination();
                }
                
                if (_companionAI != null)
                {
                    _companionAI.ClearIdleDestination();
                    _companionAI.ClearCommandDestination();
                }
                
                if (_character != null)
                    _character.AttachStop();

                ForceAnimationStateReset();

                _isSittingOnChair = false;
                _currentChairObject = null;
                
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

                if (VerboseLogging)
                    Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} stood up from chair - movement states cleared");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionIdleBehavior] Failed to stand: {ex.Message}");
                _isSittingOnChair = false;
                _currentChairObject = null;
                
                if (_combatMovement != null && _combatMovement.IsMovementLocked)
                {
                    _combatMovement.UnlockMovement();
                }
                
                try { ForceAnimationStateReset(); } catch { }
            }
        }

        #endregion
    }
}
