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

        /// <summary>
        /// Seating goes through CompanionInteractionBehavior, which sets the chair's pose bool and pins the body.
        /// Character.AttachStart/AttachStop are empty virtuals on Humanoid, so there is no other way to seat a companion.
        /// </summary>
        private bool TryFindAndSitOnChair()
        {
            if (_character == null || _interactionBehavior == null) return false;
            if (!_interactionBehavior.TryFindAndSit()) return false;

            _isSittingOnChair = true;
            SetIdleState(IdleState.SittingOnChair);

            float sitDuration = UnityEngine.Random.Range(chairSitDurationMin, chairSitDurationMax);
            _chairSitEndTime = Time.time + sitDuration;
            _chairHardTimeoutTime = Time.time + chairSitHardTimeout;

            return true;
        }

        private void StandUpFromChair()
        {
            if (!_isSittingOnChair) return;

            try
            {
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

                // Releases the pose, the pinned body and the seat's occupancy.
                if (_interactionBehavior != null && _interactionBehavior.IsAttached)
                    _interactionBehavior.ForceDetach();

                ForceAnimationStateReset();

                _isSittingOnChair = false;

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
