using UnityEngine;
using FiresCore.Npc.Movement;
using FiresCore.Npc.Core;

namespace FiresCore.Npc
{
    /// <summary>
    /// Movement mode application, velocity clamping, and movement behavior.
    /// 
    /// UNIFIED MOVEMENT AUTHORITY INTEGRATION:
    /// This class routes ALL movement through UnifiedMovementAuthority.
    /// SetMoveDirSafe acquires authority and routes through the authority system.
    /// </summary>
    public partial class CompanionCombatMovement
    {
        #region Movement Authority Helpers
        
        private const string AUTHORITY_OWNER = "CompanionCombatMovement";
        
        /// <summary>
        /// Gets the appropriate movement source based on current state.
        /// 
        /// CRITICAL: CompanionCombatMovement should ONLY acquire Combat or PlayerCommand authority!
        /// - Following is handled by CompanionAI with proper vanilla pathfinding
        /// - IdleWander is handled by CompanionAI through CompanionIdleBehavior
        /// 
        /// If we request Following or IdleWander authority here, we fight with CompanionAI
        /// and cause glitchy movement.
        /// </summary>
        private UnifiedMovementAuthority.MovementSource GetCurrentMovementSource()
        {
            // Command priority is highest
            if (HasCommandPriority)
                return UnifiedMovementAuthority.MovementSource.PlayerCommand;
            
            // Combat movement - this is CompanionCombatMovement's PRIMARY role
            if (_isInCombat)
                return UnifiedMovementAuthority.MovementSource.Combat;
            
            // CRITICAL FIX: Do NOT return Following or IdleWander!
            // CompanionAI handles those movement types with proper vanilla pathfinding.
            // If we're not in combat and don't have command priority, we shouldn't be moving.
            // Return None to indicate we shouldn't acquire authority.
            return UnifiedMovementAuthority.MovementSource.None;
        }
        
        /// <summary>
        /// Tries to acquire movement authority for the current operation.
        /// Returns true if authority was acquired or already held.
        /// 
        /// CRITICAL: Returns false if this movement type should be handled by CompanionAI instead.
        /// </summary>
        private bool TryAcquireMovementAuthority()
        {
            if (_movementAuthority == null) return true; // No authority system = allow
            
            var source = GetCurrentMovementSource();
            
            // If source is None, CompanionCombatMovement shouldn't be moving
            // Let CompanionAI handle it
            if (source == UnifiedMovementAuthority.MovementSource.None)
            {
                return false;
            }
            
            return _movementAuthority.TryAcquireAuthority(source, AUTHORITY_OWNER, 2f);
        }
        
        /// <summary>
        /// Releases movement authority if we hold it.
        /// </summary>
        private void ReleaseMovementAuthority()
        {
            _movementAuthority?.ReleaseAuthority(AUTHORITY_OWNER);
        }
        
        /// <summary>
        /// Checks if movement should be allowed based on authority.
        /// 
        /// CRITICAL: Returns false for Following/IdleWander - those are handled by CompanionAI.
        /// </summary>
        private bool ShouldAllowMovement()
        {
            if (_movementAuthority == null) return true;
            
            // If frozen, don't allow
            if (_movementAuthority.IsMovementFrozen) return false;
            
            // Check if this is a movement type CompanionCombatMovement should handle
            var source = GetCurrentMovementSource();
            
            // If source is None, we shouldn't be moving - let CompanionAI handle it
            if (source == UnifiedMovementAuthority.MovementSource.None)
            {
                return false;
            }
            
            // Check if we can acquire authority at our level
            return _movementAuthority.CanAcquireAuthority(source);
        }
        
        #endregion
        #region Movement Mode Application

        private MovementMode GetMovementModeForIntent()
        {
            return _currentIntent switch
            {
                MovementIntent.Idle => MovementMode.Stop,
                MovementIntent.PlantedFiring => MovementMode.Stop,
                MovementIntent.CombatBlock => MovementMode.Stop,
                MovementIntent.FollowingClose => MovementMode.Walk,
                MovementIntent.CombatStrafe => MovementMode.Walk,
                MovementIntent.FollowingMedium => MovementMode.Jog,
                MovementIntent.Repositioning => MovementMode.Jog,
                MovementIntent.FollowingFar => MovementMode.Run,
                MovementIntent.CombatRetreat => MovementMode.Run,
                MovementIntent.CombatChase => MovementMode.Run,
                MovementIntent.CatchingUp => MovementMode.Run,
                MovementIntent.CombatIntercept => MovementMode.Run,
                MovementIntent.CombatApproach => MovementMode.Run,
                MovementIntent.CombatDodge => MovementMode.Run,
                MovementIntent.Transitioning => MovementMode.Jog,
                _ => MovementMode.Jog
            };
        }

        private void ApplyMovementMode()
        {
            if (_character == null) return;

            MovementMode mode = GetMovementModeForIntent();

            bool newWalk = false;
            bool newRun = false;

            switch (mode)
            {
                case MovementMode.Stop:
                    newWalk = false;
                    newRun = false;
                    break;
                case MovementMode.Walk:
                    newWalk = true;
                    newRun = false;
                    break;
                case MovementMode.Jog:
                    newWalk = false;
                    newRun = false;
                    break;
                case MovementMode.Run:
                    newWalk = false;
                    newRun = true;
                    break;
            }

            SetWalkRunSafe(newWalk, newRun);
        }

        private void SetWalkRunSafe(bool walk, bool run)
        {
            if (_character == null) return;

            if (!_movementModeSet || walk != _lastWalkState || run != _lastRunState)
            {
                _character.SetWalk(walk);
                _character.SetRun(run);
                _lastWalkState = walk;
                _lastRunState = run;
                _movementModeSet = true;
            }
        }

        /// <summary>
        /// Sets move direction through the UnifiedMovementAuthority system.
        /// This is the ONLY place in CompanionCombatMovement that should set movement.
        /// Falls back to direct SetMoveDir if authority is unavailable (shouldn't happen).
        /// </summary>
        private void SetMoveDirSafe(Vector3 moveDir)
        {
            if (_character == null) return;
            
            // Don't call SetMoveDir if rigidbody is kinematic
            if (_rigidbody != null && _rigidbody.isKinematic) return;
            
            // Try to get movement authority if we don't have it yet
            // This can happen if Update() runs before Start() completes
            if (_movementAuthority == null)
            {
                _movementAuthority = GetComponent<UnifiedMovementAuthority>();
                if (_movementAuthority == null && _companion != null)
                {
                    _movementAuthority = _companion.GetMovementAuthority();
                }
            }
            
            // UNIFIED MOVEMENT AUTHORITY - Route through authority
            if (_movementAuthority != null)
            {
                // Try to acquire authority
                if (!TryAcquireMovementAuthority())
                {
                    if (VerboseLogging)
                        Debug.Log($"[CompanionCombatMovement] {_companion?.companionName} SetMoveDirSafe blocked - cannot acquire authority");
                    return;
                }
                
                // Determine walk/run from current intent
                MovementMode mode = GetMovementModeForIntent();
                bool walk = mode == MovementMode.Walk;
                bool run = mode == MovementMode.Run;
                
                // Route through authority
                _movementAuthority.SetMoveDirection(AUTHORITY_OWNER, moveDir, walk, run);
                _lastSetMoveDir = moveDir;
                _moveDirSet = true;
                return;
            }
            
            // Fallback: No authority system available - use direct control
            // This allows the Harmony patch (which allows Vector3.zero) to still work
            // Only log once per second to avoid spam
            if (Time.frameCount % 60 == 0)
            {
                Debug.LogWarning($"[CompanionCombatMovement] {_companion?.companionName} SetMoveDirSafe - no movement authority, using fallback");
            }
            
            // Direct fallback - the Harmony patch will allow this through if it's Vector3.zero
            // or if it decides the companion needs to move
            _character.SetMoveDir(moveDir);
            
            MovementMode fallbackMode = GetMovementModeForIntent();
            _character.SetWalk(fallbackMode == MovementMode.Walk);
            _character.SetRun(fallbackMode == MovementMode.Run);
            
            _lastSetMoveDir = moveDir;
            _moveDirSet = true;
        }

        private CompanionFacingAuthority _facingAuthority;
        private CompanionFacingAuthority GetFacingAuthority()
        {
            if (_facingAuthority == null && _companion != null)
                _facingAuthority = _companion.GetFacingAuthority();
            return _facingAuthority;
        }

        private void FaceMovementDirection(Vector3 dir)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return;

            // Single facing-writer: request facing through the FacingAuthority at Following priority so it
            // YIELDS to AI's enemy-facing (Combat) — in a fight you face the enemy, not your strafe
            // direction — yet still drives facing when nothing higher wants it (flee / approach with no
            // target). Direct write only as a no-authority fallback.
            var facing = GetFacingAuthority();
            if (facing != null)
            {
                if (facing.TryAcquireFacing(UnifiedMovementAuthority.MovementSource.Following, AUTHORITY_OWNER, 0.5f))
                    facing.SetLookDirection(AUTHORITY_OWNER, dir);
                return;
            }

            dir.Normalize();
            Quaternion rot = Quaternion.LookRotation(dir);
            if (float.IsNaN(rot.x) || float.IsNaN(rot.y) || float.IsNaN(rot.z) || float.IsNaN(rot.w)) return;
            transform.rotation = rot;
        }

        private void ApplyVelocityClamping()
        {
            if (_movementModeController == null) return;
            
            MovementMode mode = GetMovementModeForIntent();
            float targetDistance = GetDistanceToCurrentTarget();
            bool isStrafeIntent = _currentIntent == MovementIntent.CombatStrafe;
            
            _movementModeController.ApplyVelocityClamping(
                (MovementModeController.MovementMode)(int)mode, 
                targetDistance, 
                isStrafeIntent);
        }

        private float GetDistanceToCurrentTarget()
        {
            if (_isInCombat && _committedTarget != null)
            {
                return Vector3.Distance(transform.position, _committedTarget.transform.position);
            }

            var owner = _companion?.GetOwner();
            if (owner != null && _companion.IsFollowing)
            {
                return Vector3.Distance(transform.position, owner.transform.position);
            }

            return float.MaxValue;
        }

        #endregion

        #region Movement Behavior (Non-Combat)

        private void ApplyMovementBehavior()
        {
            // For following, let MonsterAI handle pathfinding
        }

        private void StopMovementGradually()
        {
            if (_character != null)
            {
                _targetMoveDirection = Vector3.zero;
                
                // Check actual rigidbody velocity
                float actualHorizontalSpeed = 0f;
                if (_rigidbody != null && !_rigidbody.isKinematic)
                {
                    Vector3 vel = _rigidbody.linearVelocity;
                    actualHorizontalSpeed = new Vector3(vel.x, 0, vel.z).magnitude;
                }
                
                bool animatorInLocomotion = _stateController?.IsAnimatorInLocomotion() ?? false;
                
                if (_currentMoveDirection.sqrMagnitude > 0.01f)
                {
                    Vector3 blendedDir = Vector3.Lerp(_currentMoveDirection, Vector3.zero, Time.deltaTime * movementBlendSpeed);
                    _currentMoveDirection = blendedDir;
                    
                    if (blendedDir.sqrMagnitude < 0.01f)
                    {
                        SetMoveDirSafe(Vector3.zero);
                        _currentMoveDirection = Vector3.zero;
                        
                        if (actualHorizontalSpeed < 0.15f && !animatorInLocomotion)
                        {
                            SetWalkRunSafe(false, false);
                        }
                        else
                        {
                            SetWalkRunSafe(true, false);
                        }
                    }
                    else
                    {
                        SetMoveDirSafe(blendedDir);
                        SetWalkRunSafe(true, false);
                    }
                }
                else
                {
                    if (_lastSetMoveDir != Vector3.zero)
                    {
                        SetMoveDirSafe(Vector3.zero);
                    }
                    _currentMoveDirection = Vector3.zero;
                    
                    if (actualHorizontalSpeed < 0.15f && !animatorInLocomotion)
                    {
                        SetWalkRunSafe(false, false);
                        
                        if (_rigidbody != null && !_rigidbody.isKinematic)
                        {
                            if (actualHorizontalSpeed > 0.05f)
                            {
                                Vector3 vel = _rigidbody.linearVelocity;
                                _rigidbody.linearVelocity = new Vector3(0, vel.y, 0);
                            }
                        }
                    }
                    else
                    {
                        SetWalkRunSafe(true, false);
                    }
                }
            }
            
            if (_currentIntent != MovementIntent.CombatStrafe)
            {
                _strafeDirection = 0;
            }
        }

        #endregion
    }
}
