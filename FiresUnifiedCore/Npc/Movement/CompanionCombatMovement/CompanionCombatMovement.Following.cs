using UnityEngine;
using FiresCore.Npc.Movement;

namespace FiresCore.Npc
{
    /// <summary>
    /// Following state, player idle handling, and non-combat intent management.
    /// </summary>
    public partial class CompanionCombatMovement
    {
        #region Following State

        private void ExecuteIdleState()
        {
            ClearCombatCommitment();
            UpdateNonCombatIntent();
            
            if (_currentIntent == MovementIntent.Idle && _idleBehavior != null)
            {
                // A movement sub-behavior (patrol, farming, smelter, chest, ...) is already driving the body
                // through CompanionAI's vanilla pathfinding. Park here exactly like ExecuteFollowingState parks
                // for follow — otherwise StopMovementGradually() below becomes a SECOND per-frame SetMoveDir
                // writer fighting the sub-behavior, zeroing the move it just set (body reads commanded-but-frozen).
                // The sub-behavior owns its own walk/run while it's active.
                if (_idleBehavior.IsInSubBehavior)
                    return;

                // Let idle behavior handle movement - it uses CompanionAI.RequestPathfindingMovement()
                // which properly integrates with the authority system
                bool idleHandlingMovement = _idleBehavior.UpdateIdleBehavior();
                if (idleHandlingMovement)
                {
                    // IdleBehavior is handling movement - just set walk mode
                    SetWalkRunSafe(true, false);
                }
                else
                {
                    // Not actively moving - gradually stop any residual movement
                    if (_currentMoveDirection.sqrMagnitude > 0.01f)
                        StopMovementGradually();
                }
            }
            else
            {
                // No idle behavior or different intent
                // Don't apply movement behavior - let CompanionAI handle idle wander
                ApplyVelocityClamping();
            }
        }
        
        private void ExecuteFollowingState()
        {
            ClearCombatCommitment();

            // Defense-in-depth (mirrors ExecuteIdleState's guard at the top of this file): if a work
            // sub-behavior owns the body, park — never churn follow intent / relaxed-follow movement that
            // would fight it and pull the companion back to the owner. The FSM should already be Skipped
            // via EvaluateState's IsInSubBehavior gate, but we never drive here regardless.
            if (_idleBehavior != null && _idleBehavior.IsInSubBehavior)
                return;

            // CompanionAI.UpdateFollowMovement owns following: it matches the owner's pace and stance, picks the
            // gait and drives the body through vanilla pathfinding. Nothing here may touch the body — not even
            // velocity clamping, which used to zero the rigidbody outright whenever this class's own distance
            // tiers said "Idle" while the AI was running the companion home. We only mirror the AI's gait into
            // MovementIntent for the HUD label and the stuck check.
            UpdateNonCombatIntent();
        }

        #endregion

        #region Non-Combat Intent

        /// <summary>
        /// Mirrors <see cref="CompanionAI.CurrentFollowSpeed"/> into <see cref="MovementIntent"/>. This class used
        /// to run a second set of follow distance tiers (FollowIntentController) fed by nine serialized fields that
        /// overwrote its tuned defaults; the two disagreed, and the loser clamped the body to a standstill.
        /// </summary>
        private void UpdateNonCombatIntent()
        {
            if (_companionAI == null || !_companionAI.IsFollowingState || !(_companion?.ShouldBeFollowing ?? false))
            {
                SetFollowIntent(MovementIntent.Idle);
                _shouldCheckStuck = false;
                return;
            }

            MovementIntent intent = _companionAI.CurrentFollowSpeed switch
            {
                AI.CompanionAI.FollowSpeed.Sneaking  => MovementIntent.FollowingClose,
                AI.CompanionAI.FollowSpeed.Walking   => MovementIntent.FollowingClose,
                AI.CompanionAI.FollowSpeed.Jogging   => MovementIntent.FollowingMedium,
                AI.CompanionAI.FollowSpeed.Running   => MovementIntent.FollowingFar,
                AI.CompanionAI.FollowSpeed.Sprinting => MovementIntent.CatchingUp,
                _ => MovementIntent.Idle
            };

            SetFollowIntent(intent);
            _shouldCheckStuck = intent == MovementIntent.FollowingFar || intent == MovementIntent.CatchingUp;
        }

        /// <summary>The owner has stood still long enough that the companion may stand down — CompanionAI's
        /// owner-AFK answer, which also drives its own Following → Idle drop, so combat and the FSM agree.</summary>
        public bool ShouldRelaxDueToPlayerIdle() => _companionAI?.IsOwnerIdle ?? false;

        private void SetFollowIntent(MovementIntent intent)
        {
            if (intent != _lastFollowIntent)
            {
                _followIntentChangeTime = Time.time;
                _lastFollowIntent = intent;
            }
            SetIntent(intent);
        }

        #endregion
    }
}
