using UnityEngine;
using System;
using FiresCore.Npc.Core;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Movement, pathfinding, stuck detection, and teleportation.
    /// 
    /// UNIFIED MOVEMENT AUTHORITY INTEGRATION:
    /// All direct SetMoveDir calls now route through UnifiedMovementAuthority.
    /// </summary>
    public partial class CompanionAI
    {
        #region Movement Authority
        
        private const string AI_AUTHORITY_OWNER = "CompanionAI";
        
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
                if (authority.TryAcquireAuthority(source, AI_AUTHORITY_OWNER, 2f))
                {
                    authority.SetMoveDirection(AI_AUTHORITY_OWNER, direction, walk: !run, run: run);
                }
                return;
            }
            
            // Fallback: No authority system
            m_character.SetMoveDir(direction);
            m_character.SetWalk(!run);
            m_character.SetRun(run);
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
                if (!authority.TryAcquireAuthority(source, AI_AUTHORITY_OWNER, 2f))
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
            // and follows waypoints in m_path, not a direct line to target
            return MoveTo(Time.deltaTime, destination, reachDistance, run);
        }
        
        #endregion

        #region Movement

        private void MoveToward(Vector3 target, float dt, bool run)
        {
            if (StateTransitionLogging && _currentState == AIState.Fleeing)
            {
                Debug.Log($"[CompanionAI] MoveToward (FLEEING): {m_character?.m_name} target={target}, run={run}");
            }
            
            if (_terrainAwareness != null)
            {
                if (_terrainAwareness.IsInAoeHazard())
                {
                    Vector3 escapeDir = _terrainAwareness.GetAoeEscapeDirection();
                    if (escapeDir.sqrMagnitude > 0.01f)
                    {
                        target = transform.position + escapeDir * 8f;
                        run = true;
                        
                        if (VerboseLogging)
                        {
                            Debug.Log($"[CompanionAI] IN AOE! Escaping: {escapeDir}");
                        }
                    }
                }
                else if (_terrainAwareness.IsCurrentPositionDangerous())
                {
                    var hazardMap = _terrainAwareness.GetHazardMap();
                    target = transform.position + hazardMap.SafestDirection * 5f;
                    run = true;
                    
                    if (VerboseLogging)
                    {
                        Debug.Log($"[CompanionAI] In danger! Moving to safety: {hazardMap.SafestDirection}");
                    }
                }
                else
                {
                    Vector3 moveDirection = (target - transform.position).normalized;
                    
                    if (!_terrainAwareness.IsDirectionSafe(moveDirection))
                    {
                        Vector3 safeDirection = _terrainAwareness.GetSafeMovementDirection(moveDirection);
                        float distToTarget = Vector3.Distance(transform.position, target);
                        Vector3 safeTarget = transform.position + safeDirection * Mathf.Min(distToTarget, 5f);
                        
                        if (VerboseLogging)
                        {
                            Debug.Log($"[CompanionAI] Hazard avoidance: redirecting from {moveDirection} to {safeDirection}");
                        }
                        
                        target = safeTarget;
                    }
                }
            }
            
            MoveToWithStuckDetection(target, dt, run);
        }
        
        private void MoveToWithStuckDetection(Vector3 target, float dt, bool run)
        {
            float distToTarget = Vector3.Distance(transform.position, target);
            
            bool targetChanged = Vector3.Distance(target, _currentMoveTarget) > 2f;
            if (targetChanged)
            {
                _currentMoveTarget = target;
                _pathfindingAttempts = 0;
                _consecutiveStuckFrames = 0;
                _lastProgressTime = Time.time;
                _lastDistanceToTarget = distToTarget;
                
                ForcePathRecalculation();
            }
            
            // Check if we've reached the target
            if (distToTarget < POSITION_REACHED_THRESHOLD)
            {
                _consecutiveStuckFrames = 0;
                _pathfindingAttempts = 0;
                return;
            }
            
            // Track progress
            if (distToTarget < _lastDistanceToTarget - 0.3f)
            {
                _lastProgressTime = Time.time;
                _lastDistanceToTarget = distToTarget;
                _consecutiveStuckFrames = 0;
                _pathfindingAttempts = 0;
            }
            
            // Stuck detection
            if (Time.time - _lastPathfindingTime >= STUCK_CHECK_INTERVAL)
            {
                float movementSinceLastCheck = Vector3.Distance(transform.position, _lastPathfindingPos);
                
                if (movementSinceLastCheck < STUCK_MOVEMENT_THRESHOLD && distToTarget > POSITION_REACHED_THRESHOLD)
                {
                    _consecutiveStuckFrames++;
                    
                    if (VerboseLogging)
                    {
                        Debug.Log($"[CompanionAI] {m_character?.m_name} stuck check {_consecutiveStuckFrames}/{STUCK_FRAMES_BEFORE_RECALC} " +
                            $"(moved {movementSinceLastCheck:F2}m, dist to target: {distToTarget:F1}m)");
                    }
                    
                    if (_consecutiveStuckFrames >= STUCK_FRAMES_BEFORE_RECALC)
                    {
                        HandleStuckCondition(target, run);
                    }
                }
                else
                {
                    _consecutiveStuckFrames = 0;
                }
                
                _lastPathfindingPos = transform.position;
                _lastPathfindingTime = Time.time;
            }
            
            if (Time.time - _lastProgressTime > PROGRESS_TIMEOUT && distToTarget > POSITION_REACHED_THRESHOLD)
            {
                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionAI] {m_character?.m_name} no progress for {PROGRESS_TIMEOUT}s, trying alternative route");
                }
                
                TryAlternativeRoute(target, run);
                _lastProgressTime = Time.time;
            }
            
            // CRITICAL: Use MoveToThroughAuthority which uses vanilla pathfinding
            // This properly calculates paths around obstacles
            MoveToThroughAuthority(target, run, POSITION_REACHED_THRESHOLD);
        }
        
        private void HandleStuckCondition(Vector3 target, bool run)
        {
            _pathfindingAttempts++;
            _consecutiveStuckFrames = 0;
            
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionAI] {m_character?.m_name} stuck! Attempt {_pathfindingAttempts}/{MAX_PATHFINDING_ATTEMPTS}");
            }
            
            if (_pathfindingAttempts == 1)
            {
                ForcePathRecalculation();
                return;
            }
            
            if (_pathfindingAttempts == 2)
            {
                TrySidestepObstacle(target);
                return;
            }
            
            if (_pathfindingAttempts == 3)
            {
                TryAlternativeRoute(target, run);
                return;
            }
            
            if (_pathfindingAttempts == 4)
            {
                if (m_character != null && m_character.IsOnGround())
                {
                    TryJumpOverObstacle();
                }
                return;
            }
            
            if (_pathfindingAttempts >= MAX_PATHFINDING_ATTEMPTS)
            {
                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionAI] {m_character?.m_name} max attempts reached, teleporting to target area");
                }
                
                TeleportNearTarget(target);
                _pathfindingAttempts = 0;
            }
        }
        
        private void ForcePathRecalculation()
        {
            if (Time.time - _lastPathRecalcTime < PATH_RECALC_INTERVAL)
                return;
                
            _lastPathRecalcTime = Time.time;
            
            try
            {
                var pathTimerField = typeof(BaseAI).GetField("m_pathTimer",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (pathTimerField != null)
                {
                    pathTimerField.SetValue(this, 999f);
                }
                
                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionAI] {m_character?.m_name} forcing path recalculation");
                }
            }
            catch (Exception ex)
            {
                if (VerboseLogging)
                    Debug.LogWarning($"[CompanionAI] Failed to force path recalc: {ex.Message}");
            }
        }
        
        private void TrySidestepObstacle(Vector3 target)
        {
            Vector3 toTarget = (target - transform.position).normalized;
            
            Vector3 rightDir = Vector3.Cross(Vector3.up, toTarget).normalized;
            Vector3 leftDir = -rightDir;
            
            bool rightClear = !Physics.Raycast(transform.position + Vector3.up * 0.5f, rightDir, 2f);
            bool leftClear = !Physics.Raycast(transform.position + Vector3.up * 0.5f, leftDir, 2f);
            
            Vector3 sideStep;
            if (rightClear && !leftClear)
                sideStep = rightDir;
            else if (leftClear && !rightClear)
                sideStep = leftDir;
            else
                sideStep = UnityEngine.Random.value > 0.5f ? rightDir : leftDir;
            
            Vector3 sideTarget = transform.position + sideStep * 3f;
            
            if (_terrainAwareness != null && !_terrainAwareness.IsDirectionSafe(sideStep))
            {
                sideStep = _terrainAwareness.GetSafeMovementDirection(sideStep);
                sideTarget = transform.position + sideStep * 3f;
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionAI] {m_character?.m_name} sidestepping obstacle: {sideStep}");
            }
            
            // Route through authority system
            SetMoveDirThroughAuthority(sideStep, run: false);
        }
        
        private void TryAlternativeRoute(Vector3 target, bool run)
        {
            Vector3 toTarget = (target - transform.position).normalized;
            float distToTarget = Vector3.Distance(transform.position, target);
            
            Vector3[] offsets = new Vector3[]
            {
                Vector3.Cross(Vector3.up, toTarget) * WAYPOINT_SEARCH_RADIUS,
                -Vector3.Cross(Vector3.up, toTarget) * WAYPOINT_SEARCH_RADIUS,
                toTarget * WAYPOINT_SEARCH_RADIUS + Vector3.Cross(Vector3.up, toTarget) * WAYPOINT_SEARCH_RADIUS * 0.5f,
                toTarget * WAYPOINT_SEARCH_RADIUS - Vector3.Cross(Vector3.up, toTarget) * WAYPOINT_SEARCH_RADIUS * 0.5f
            };
            
            foreach (var offset in offsets)
            {
                Vector3 waypoint = transform.position + offset;
                
                if (ZoneSystem.instance != null)
                {
                    float groundHeight;
                    if (ZoneSystem.instance.GetGroundHeight(waypoint, out groundHeight))
                    {
                        waypoint.y = groundHeight + 0.5f;
                    }
                }
                
                bool waypointSafe = _terrainAwareness == null || 
                    _terrainAwareness.IsDirectionSafe((waypoint - transform.position).normalized);
                    
                if (waypointSafe && HasLineOfSight(waypoint))
                {
                    if (VerboseLogging)
                    {
                        Debug.Log($"[CompanionAI] {m_character?.m_name} found alternative waypoint at {waypoint}");
                    }
                    
                    MoveDirectlyToward(waypoint, run);
                    ForcePathRecalculation();
                    return;
                }
            }
            
            ForcePathRecalculation();
        }
        
        private void TryJumpOverObstacle()
        {
            if (m_character == null) return;
            
            Vector3 forward = transform.forward;
            Vector3 originLow = transform.position + Vector3.up * 0.3f;
            Vector3 originHigh = transform.position + Vector3.up * 1.5f;
            
            bool lowBlocked = Physics.Raycast(originLow, forward, 1.5f);
            bool highClear = !Physics.Raycast(originHigh, forward, 1.5f);
            
            if (lowBlocked && highClear)
            {
                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionAI] {m_character?.m_name} attempting to jump over obstacle");
                }
                
                try
                {
                    var jumpMethod = typeof(Character).GetMethod("Jump",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Instance);
                    jumpMethod?.Invoke(m_character, null);
                }
                catch { }
            }
        }
        
        private void TeleportNearTarget(Vector3 target)
        {
            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f;
                Vector3 offset = Quaternion.Euler(0, angle, 0) * Vector3.forward * 3f;
                Vector3 testPos = target + offset;
                
                if (ZoneSystem.instance != null)
                {
                    float groundHeight;
                    if (ZoneSystem.instance.GetGroundHeight(testPos, out groundHeight))
                    {
                        testPos.y = groundHeight + 0.5f;
                    }
                }
                
                var hazard = CheckTeleportPositionSafety(testPos);
                if (hazard == Combat.TerrainAwareness.HazardType.None)
                {
                    transform.position = testPos;
                    
                    if (VerboseLogging)
                    {
                        Debug.Log($"[CompanionAI] {m_character?.m_name} teleported to {testPos} (near target)");
                    }
                    
                    var owner = _companion?.GetOwner();
                    if (owner != null && owner == Player.m_localPlayer)
                    {
                        MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft,
                            $"{_companion.GetDisplayName()} couldn't find a path - teleported");
                    }
                    
                    return;
                }
            }
            
            transform.position = target;
        }
        
        private void MoveDirectlyToward(Vector3 target, bool run)
        {
            if (m_character == null) return;
            
            Vector3 direction = (target - transform.position).normalized;
            direction.y = 0;
            
            if (direction.sqrMagnitude > 0.01f)
            {
                // Route through authority system
                SetMoveDirThroughAuthority(direction, run);
                LookAt(target);
            }
        }
        
        private bool HasLineOfSight(Vector3 target)
        {
            Vector3 origin = transform.position + Vector3.up * 1f;
            Vector3 direction = target - origin;
            float distance = direction.magnitude;
            
            if (distance < 1f) return true;
            
            if (Physics.Raycast(origin, direction.normalized, out RaycastHit hit, distance))
            {
                if (hit.collider.isTrigger) return true;
                
                if (Vector3.Distance(hit.point, target) < 0.5f) return true;
                
                return false;
            }
            
            return true;
        }

        private Combat.TerrainAwareness.HazardType CheckTeleportPositionSafety(Vector3 pos)
        {
            if (WorldGenerator.instance != null)
            {
                Heightmap.Biome biome = WorldGenerator.instance.GetBiome(pos);
                if (biome == Heightmap.Biome.AshLands)
                {
                    try
                    {
                        WaterVolume waterVolume = null;
                        float waterLevel = Floating.GetWaterLevel(pos, ref waterVolume);
                        float groundHeight = pos.y;
                        if (ZoneSystem.instance != null)
                        {
                            ZoneSystem.instance.GetGroundHeight(pos, out groundHeight);
                        }
                        if (waterLevel > groundHeight + 0.3f)
                        {
                            return Combat.TerrainAwareness.HazardType.Lava;
                        }
                    }
                    catch { }
                }
            }
            
            Collider[] colliders = Physics.OverlapSphere(pos, 1.5f);
            foreach (var col in colliders)
            {
                if (col == null) continue;
                string objName = col.gameObject.name.ToLower();
                
                if (objName.Contains("lava") || objName.Contains("magma"))
                    return Combat.TerrainAwareness.HazardType.Lava;
                if (objName.Contains("fire") || objName.Contains("campfire"))
                    return Combat.TerrainAwareness.HazardType.Fire;
                if (objName.Contains("tar"))
                    return Combat.TerrainAwareness.HazardType.Tar;
            }
            
            return Combat.TerrainAwareness.HazardType.None;
        }

        #endregion
    }
}
