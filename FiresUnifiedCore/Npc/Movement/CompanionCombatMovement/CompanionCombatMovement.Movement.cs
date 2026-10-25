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
        
        private const string AuthorityOwner = "CompanionCombatMovement";
        
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
            
            return _movementAuthority.TryAcquireAuthority(source, AuthorityOwner, 2f);
        }
        
        /// <summary>
        /// Releases movement authority if we hold it.
        /// </summary>
        private void ReleaseMovementAuthority()
        {
            _movementAuthority?.ReleaseAuthority(AuthorityOwner);
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

            // Write the live flags unconditionally — SetWalk/SetRun are one-line field setters and the
            // follow gait writes them directly too, so a private walk/run shadow cache went stale across
            // state hops: the skipped combat run request left a walk-flagged companion fighting at
            // m_walkSpeed (vanilla UpdateWalking gives m_walk priority over run).
            _character.SetWalk(walk);
            _character.SetRun(run);
        }

        /// <summary>
        /// Sets move direction through the UnifiedMovementAuthority system.
        /// This is the ONLY place in CompanionCombatMovement that should set movement.
        /// Falls back to direct SetMoveDir if authority is unavailable (shouldn't happen).
        /// </summary>
        private void SetMoveDirSafe(Vector3 moveDir)
        {
            if (_character == null) return;
            moveDir = OwnerLineDeflect(moveDir);
            
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
                _movementAuthority.SetMoveDirection(AuthorityOwner, moveDir, walk, run);
                _lastSetMoveDir = moveDir;
                return;
            }
            
            // No authority component (only on a malformed prefab). PARK — do not raw-write SetMoveDir;
            // the single-writer rule must hold. Keep the throttled warning so it's visible if it happens.
            if (Time.frameCount % 60 == 0)
            {
                Debug.LogWarning($"[CompanionCombatMovement] {_companion?.companionName} SetMoveDirSafe - no movement authority; parking (single-writer)");
            }
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
                if (facing.TryAcquireFacing(UnifiedMovementAuthority.MovementSource.Following, AuthorityOwner, 0.5f))
                    facing.SetLookDirection(AuthorityOwner, dir);
                return;
            }

            // Vanilla SetLookDir (UpdateRotation turns at m_turnSpeed), not a rotation write.
            _character?.SetLookDir(dir.normalized);
        }

        private void ApplyVelocityClamping()
        {
            if (_movementModeController == null) return;
            
            MovementMode mode = GetMovementModeForIntent();
            float targetDistance = GetDistanceToCurrentTarget();
            bool isStrafeIntent = _currentIntent == MovementIntent.CombatStrafe;
            
            // Slow down by walking (vanilla SetWalk), never by clamping the rigidbody's velocity.
            if (_movementModeController.WantsSlowWalk((MovementModeController.MovementMode)(int)mode, targetDistance, isStrafeIntent))
                SetWalkRunSafe(true, false);
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

        // Support companions get no flank slot, so this also keeps them off the line: every companion, any role, standing in its owner's line of attack (within OwnerLineMetres of
        // the segment owner -> the foe nearest the owner, 10-90 % along it) steps sideways off it: the move gets a sideways part, a standing
        // companion a short sidestep. "[OwnerLine]" Debug once per episode with the positions.
        private const float OwnerLineMetres = 1.2f, OwnerLineFoeRange = 25f;
        private bool _ownerLineLogged;
        private Vector3 OwnerLineDeflect(Vector3 moveDir)
        {
            Player owner = _companion != null ? _companion.GetOwner() : null;
            if (owner == null || _character == null || owner.IsDead()) return moveDir;
            Vector3 o = owner.transform.position, me = _character.transform.position;
            Character foe = null; float best = OwnerLineFoeRange * OwnerLineFoeRange;
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == null || c.IsDead() || c == _character || c.IsPlayer() || c.GetComponent<CompanionController>() != null || !BaseAI.IsEnemy(owner, c)) continue;
                float d = (c.transform.position - o).sqrMagnitude;
                if (d < best) { best = d; foe = c; }
            }
            if (foe == null) { _ownerLineLogged = false; return moveDir; }
            Vector3 seg = foe.transform.position - o; seg.y = 0f;
            float len = seg.magnitude; if (len < 2f) return moveDir;
            Vector3 dir = seg / len, rel = me - o; rel.y = 0f;
            float along = Vector3.Dot(rel, dir);
            if (along < len * 0.1f || along > len * 0.9f) { _ownerLineLogged = false; return moveDir; }
            Vector3 side = rel - dir * along;
            if (side.magnitude >= OwnerLineMetres) { _ownerLineLogged = false; return moveDir; }
            Vector3 away = side.sqrMagnitude > 0.01f ? side.normalized : Vector3.Cross(Vector3.up, dir);
            if (!_ownerLineLogged)
            {
                _ownerLineLogged = true;
                FiresCore.Logging.FiresLogger.LogDebug($"[OwnerLine] {_companion.companionName}: in {owner.GetPlayerName()}'s line to {foe.m_name} ({side.magnitude:0.0} m off it, {along:0.0}/{len:0.0} m along; me ({me.x:0.0}, {me.z:0.0}), owner ({o.x:0.0}, {o.z:0.0}), foe ({foe.transform.position.x:0.0}, {foe.transform.position.z:0.0})); stepping {(moveDir.sqrMagnitude > 0.01f ? "sideways while moving" : "aside")}");
            }
            if (moveDir.sqrMagnitude < 0.01f) return away;
            Vector3 flat = new Vector3(moveDir.x, 0f, moveDir.z);
            return (flat.normalized + away * 1.2f).normalized * moveDir.magnitude;
        }

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
                        // No velocity kill; SetMoveDirSafe(0) above lets vanilla UpdateWalking brake.
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
