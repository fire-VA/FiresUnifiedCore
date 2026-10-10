using UnityEngine;
using System;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Movement through the authority, vanilla pathfinding (BaseAI.MoveTo) and its path cache.
    /// 
    /// UNIFIED MOVEMENT AUTHORITY INTEGRATION:
    /// All direct SetMoveDir calls now route through UnifiedMovementAuthority.
    /// </summary>
    public partial class CompanionAI
    {
        #region Movement Authority
        
        private const string AIAuthorityOwner = "CompanionAI";
        
        /// <summary>
        /// Gets the movement authority from the companion controller.
        /// </summary>
        private UnifiedMovementAuthority GetMovementAuthority()
        {
            return _companion?.GetMovementAuthority();
        }
        
        /// <summary>
        /// Gets the appropriate movement source based on current AI state.
        /// </summary>
        private UnifiedMovementAuthority.MovementSource GetAIMovementSource()
        {
            // CRITICAL: Check for active sub-behavior first - sub-behaviors have higher priority than following
            if (_idleBehavior != null && _idleBehavior.IsInSubBehavior)
            {
                // If the sub-behavior was command-initiated, use PlayerCommand priority
                // Otherwise use SubBehavior priority
                return UnifiedMovementAuthority.MovementSource.SubBehavior;
            }
            
            switch (_currentState)
            {
                case AIState.Combat:
                case AIState.Fleeing:
                    return UnifiedMovementAuthority.MovementSource.Combat;
                case AIState.Following:
                    return UnifiedMovementAuthority.MovementSource.Following;
                case AIState.Idle:
                default:
                    return UnifiedMovementAuthority.MovementSource.IdleWander;
            }
        }
        
        /// <summary>
        /// Sets movement direction through the UnifiedMovementAuthority.
        /// </summary>
        private void SetMoveDirThroughAuthority(Vector3 direction, bool run)
        {
            if (m_character == null) return;
            
            var authority = GetMovementAuthority();
            if (authority != null)
            {
                var source = GetAIMovementSource();
                if (authority.TryAcquireAuthority(source, AIAuthorityOwner, 2f))
                {
                    authority.SetMoveDirection(AIAuthorityOwner, direction, walk: !run, run: run);
                }
                return;
            }
            
            // No authority component (only on a malformed prefab). PARK — do not raw-write SetMoveDir;
            // the single-writer rule must hold and a real companion always has the authority. Waiting a
            // frame for it to resolve is correct; racing the authority with a raw write is not.
        }
        
        /// <summary>
        /// WRAPPER FOR BaseAI.MoveTo() - Acquires authority first, then uses vanilla pathfinding.
        /// 
        /// This is the CORRECT approach:
        /// 1. Acquire authority to ensure only one system moves the companion
        /// 2. Use BaseAI.MoveTo() which has proper pathfinding (FindPath, m_path waypoints)
        /// 3. Let vanilla handle obstacle avoidance
        /// 
        /// Returns true when destination is reached, false while still moving.
        /// </summary>
        private bool MoveToThroughAuthority(Vector3 destination, bool run, float reachDistance = 0f)
        {
            if (m_character == null) return true;
            
            // First, try to acquire authority - if we can't, don't move
            var authority = GetMovementAuthority();
            if (authority != null)
            {
                var source = GetAIMovementSource();
                if (!authority.TryAcquireAuthority(source, AIAuthorityOwner, 2f))
                {
                    // Can't acquire authority - another system has priority
                    return false;
                }
            }
            
            // Set walk/run mode
            m_character.SetRun(run);
            m_character.SetWalk(!run);
            
            // USE VANILLA PATHFINDING - this is the key!
            // BaseAI.MoveTo() uses FindPath() to calculate a path around obstacles
            // and follows waypoints in m_path, not a direct line to target.
            // Escape hatch: vanilla MoveTo calls SetMoveDir directly (not via the authority's apply), so
            // open the gate around it — the single-writer prefix (when enabled) lets this legitimate
            // pathfinding write through. try/finally guarantees the gate re-closes even if FindPath throws.
            try
            {
                authority?.DisableExternalBlocking();
                return MoveTo(Time.deltaTime, destination, reachDistance, run);
            }
            finally
            {
                authority?.EnableExternalBlocking();
            }
        }
        
        #endregion

        #region Movement

        // Audit slice 4 (T1, 0.2.329): the stuck-recovery chain that ended in TeleportNearTarget (transform.position written
        // with no ZDO sync or physics reset) is gone. It hung off a private MoveToward that nothing called; live movement goes
        // through MoveToThroughAuthority -> vanilla BaseAI.MoveTo, which drops the goal when there is no path. The one teleport
        // kept is the follow-stranded one in CompanionController (T4, a mod feature with ZDO + RPC sync).

        /// <summary>BaseAI.FindPath returns its cached result for 1-5 s (BaseAI.cs:711-716); forgetting the last search
        /// time makes the next MoveTo path again.</summary>
        internal void InvalidatePathCache() => m_lastFindPathTime = float.NegativeInfinity;

        private void ForcePathRecalculation()
        {
            if (Time.time - _lastPathRecalcTime < PathRecalcInterval)
                return;
                
            _lastPathRecalcTime = Time.time;
            InvalidatePathCache();

            if (VerboseLogging)
            {
                Debug.Log($"[CompanionAI] {m_character?.m_name} forcing path recalculation");
            }
        }

        #endregion
    }
}
