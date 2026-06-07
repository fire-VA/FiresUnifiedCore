using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Jump logic, stuck detection, and grounded state management.
    /// </summary>
    public partial class CompanionCombatMovement
    {
        #region Stuck Detection

        private void UpdateStuckDetection()
        {
            if (_stuckHandler == null) return;
            
            _stuckHandler.ShouldCheckStuck = _shouldCheckStuck;
            _stuckHandler.UpdateStuckDetection(
                _isInCombat || _isInCombatCooldown,
                _isInTransition,
                _currentIntent == MovementIntent.Idle);
        }

        private void UpdateJumpLogic()
        {
            if (_stuckHandler == null) return;
            
            bool isMoving = GetMovementModeForIntent() != MovementMode.Stop;
            bool jumped = _stuckHandler.UpdateJumpLogic(
                _isInCombat || _isInCombatCooldown,
                _currentIntent == MovementIntent.Idle,
                isMoving);
            
            if (jumped && VerboseLogging)
            {
                Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} jumped!");
            }
        }

        private void UpdateGroundedState()
        {
            if (_stuckHandler != null)
            {
                _stuckHandler.UpdateGroundedState();
                _isGrounded = _stuckHandler.IsGrounded;
            }
            else
            {
                _followBehavior?.UpdateGroundedState();
                _isGrounded = _followBehavior?.IsGrounded ?? _character?.IsOnGround() ?? false;
            }
        }

        #endregion

        #region Public Jump API

        public void ForceJump()
        {
            _stuckHandler?.ForceJump();
        }

        #endregion
    }
}
