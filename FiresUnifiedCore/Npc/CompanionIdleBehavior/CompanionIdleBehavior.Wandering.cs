using UnityEngine;
using FiresCore.Npc.Core;

namespace FiresCore.Npc
{
    // Wandering, path preference, terrain checking, and obstacle avoidance
    public partial class CompanionIdleBehavior
    {
        #region Obstacle Avoidance

        /// <summary>
        /// Validates that a wander destination is reachable by checking:
        /// 1. NavMesh pathfinding (uses Valheim's built-in Pathfinding system)
        /// 2. Direct-line obstacle raycast for walls/structures
        /// 3. Height difference sanity (don't try to walk through floors/ceilings)
        /// Returns true if the destination is safe to walk to.
        /// </summary>
        private bool IsDestinationReachable(Vector3 destination)
        {
            Vector3 origin = transform.position;
            float heightDiff = Mathf.Abs(destination.y - origin.y);

            // Reject extreme height changes - likely a different floor
            if (heightDiff > 4f)
                return false;

            // Check Valheim's NavMesh pathfinding
            if (Pathfinding.instance != null)
            {
                if (!Pathfinding.instance.HavePath(origin, destination, Pathfinding.AgentType.Humanoid))
                    return false;
            }

            // Raycast at chest height to detect walls/structures in the direct line
            Vector3 rayOrigin = origin + Vector3.up * 0.8f;
            Vector3 rayTarget = destination + Vector3.up * 0.8f;
            Vector3 direction = rayTarget - rayOrigin;
            float distance = direction.magnitude;

            if (distance > 0.5f)
            {
                // Use a SphereCast for more reliable wall detection
                if (Physics.SphereCast(rayOrigin, 0.3f, direction.normalized, out RaycastHit hit, distance,
                    LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid")))
                {
                    // Hit something - check if it's a structure/wall (not terrain or the companion itself)
                    if (hit.collider != null && hit.collider.gameObject != gameObject)
                    {
                        var piece = hit.collider.GetComponentInParent<Piece>();
                        if (piece != null)
                        {
                            if (VerboseLogging)
                                Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} wander blocked by {piece.m_name} at {hit.point}");
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Tries to find a valid wander destination with obstacle avoidance.
        /// Attempts up to maxAttempts random positions, falling back to the best scored one.
        /// Returns the best reachable position, or null if none found.
        /// </summary>
        private Vector3? FindValidWanderDestination(int maxAttempts = 5)
        {
            Vector3? bestCandidate = null;
            float bestScore = -1f;

            for (int i = 0; i < maxAttempts; i++)
            {
                Vector3 candidate = GetCommittedWanderPosition();

                if (_hasHomePosition)
                {
                    Vector3 currentPos = transform.position;
                    Vector3 dir = (candidate - currentPos).normalized;
                    dir = GetHomeBiasedDirection(currentPos, dir);
                    float dist = Vector3.Distance(currentPos, candidate);
                    candidate = currentPos + dir * dist;

                    if (!IsWithinWanderRadius(candidate))
                    {
                        Vector3 toHome = (_homePosition - currentPos).normalized;
                        candidate = currentPos + toHome * UnityEngine.Random.Range(idleWanderMinDistance, idleWanderMaxDistance);
                    }

                    if (ZoneSystem.instance != null)
                    {
                        float groundHeight;
                        if (ZoneSystem.instance.GetGroundHeight(candidate, out groundHeight))
                            candidate.y = groundHeight;
                    }
                }

                if (!IsDestinationReachable(candidate))
                    continue;

                // Score this candidate - prefer paved/built surfaces
                float score = ScorePositionForPath(candidate);

                // Bonus for staying on the same surface type
                if (_isOnPath && score >= pavedPathBonus * 0.5f)
                    score += 2f;

                // SPATIAL SPREAD: penalize positions that are crowded with other
                // companions/players so a group in stay mode disperses around the
                // homestead instead of clumping together at one waypoint.
                score -= ScoreCrowdingPenalty(candidate);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestCandidate = candidate;
                }

                // If we found a path position with no crowding, use it immediately.
                // (Crowding can drag the score below this floor, which is intentional —
                // we keep searching for a less-crowded option.)
                if (score >= pavedPathBonus * 0.5f)
                    break;
            }

            return bestCandidate;
        }

        /// <summary>
        /// Returns a positive penalty proportional to how many other companions are
        /// within personal-space range of <paramref name="candidate"/>.  Used to
        /// spread companions out when wandering in stay mode.
        /// </summary>
        private float ScoreCrowdingPenalty(Vector3 candidate)
        {
            const float CrowdRadius    = 4f;   // companion's personal space when picking a waypoint
            const float PerCompanion   = 3f;   // penalty per other companion within CrowdRadius
            float radiusSq = CrowdRadius * CrowdRadius;
            float penalty = 0f;

            if (CompanionController.AllCompanions != null)
            {
                foreach (var other in CompanionController.AllCompanions)
                {
                    if (other == null) continue;
                    if (other == _companion) continue;
                    if ((other.transform.position - candidate).sqrMagnitude < radiusSq)
                        penalty += PerCompanion;
                }
            }
            return penalty;
        }

        #endregion

        #region Terrain Checking

        private void PerformTerrainCheck()
        {
            _currentTerrainScore = ScorePositionForPath(transform.position);
            _isOnPath = _currentTerrainScore >= pavedPathBonus * 0.5f;

            if (VerboseLogging)
                Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} terrain check: score={_currentTerrainScore:F1}, onPath={_isOnPath}");
        }

        private float ScorePositionForPath(Vector3 position)
        {
            float score = 0f;

            Heightmap heightmap = Heightmap.FindHeightmap(position);
            if (heightmap == null)
                return score;

            try
            {
                TerrainComp terrainComp = TerrainComp.FindTerrainCompiler(position);
                if (terrainComp != null)
                {
                    Color paintColor = GetTerrainPaintColor(terrainComp, position);

                    if (paintColor.b > 0.5f)
                        score += pavedPathBonus;
                    else if (paintColor.g > 0.5f)
                        score += cultivatedBonus;
                }

                if (HasNearbyPathPieces(position))
                    score += pavedPathBonus;
            }
            catch { }

            return score;
        }

        private Color GetTerrainPaintColor(TerrainComp terrainComp, Vector3 worldPos)
        {
            try
            {
                if (terrainComp == null) return Color.black;

                Heightmap heightmap = terrainComp.GetComponent<Heightmap>();
                if (heightmap == null) return Color.black;

                int width = heightmap.m_width;
                Vector3 localPos = worldPos - heightmap.transform.position;

                float scale = heightmap.m_scale;
                float normalizedX = (localPos.x / scale + width / 2f) / width;
                float normalizedZ = (localPos.z / scale + width / 2f) / width;

                normalizedX = Mathf.Clamp01(normalizedX);
                normalizedZ = Mathf.Clamp01(normalizedZ);

                var paintMaskField = typeof(TerrainComp).GetField("m_paintMask",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (paintMaskField != null)
                {
                    var paintMask = paintMaskField.GetValue(terrainComp) as Color[];
                    if (paintMask != null && paintMask.Length > 0)
                    {
                        int index = Mathf.FloorToInt(normalizedZ * (width + 1)) * (width + 1) + Mathf.FloorToInt(normalizedX * (width + 1));
                        if (index >= 0 && index < paintMask.Length)
                        {
                            return paintMask[index];
                        }
                    }
                }
            }
            catch { }

            return Color.black;
        }

        private bool HasNearbyPathPieces(Vector3 position)
        {
            string[] pathPrefabs = new string[]
            {
                "stone_floor", "wood_floor", "stone_path", "wood_path",
                "stonecutter", "iron_floor", "crystal_floor", "dvergr_floor",
                "goblin_floor", "ashwood_floor", "darkwood_floor"
            };

            Collider[] nearby = Physics.OverlapSphere(position, 2f);
            foreach (var collider in nearby)
            {
                if (collider == null) continue;

                string objName = collider.gameObject.name.ToLowerInvariant();
                foreach (var pathPrefab in pathPrefabs)
                {
                    if (objName.Contains(pathPrefab))
                        return true;
                }

                var piece = collider.GetComponent<Piece>();
                if (piece != null)
                {
                    string pieceName = piece.m_name?.ToLowerInvariant() ?? "";
                    if (pieceName.Contains("floor") || pieceName.Contains("path") || pieceName.Contains("stone") || pieceName.Contains("tile"))
                        return true;
                }
            }

            return false;
        }

        #endregion

        #region Wandering

        private void StartIdleWander()
        {
            if (Time.time - _lastWanderCompleteTime < MinWanderCooldown)
            {
                if (VerboseLogging)
                    Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} wander cooldown active, skipping");
                return;
            }

            _totalWanderLegs = UnityEngine.Random.Range(1, maxWanderLegs + 1);
            _currentWanderLeg = 0;
            
            if (VerboseLogging)
                Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} starting idle wander with {_totalWanderLegs} waypoints, maxRadius={maxWanderRadius}m");

            if (_isOnPath && _lastWanderDirection.sqrMagnitude > 0.1f && UnityEngine.Random.value < pathContinueDirectionChance)
            {
                float variation = UnityEngine.Random.Range(-15f, 15f);
                _lastWanderDirection = Quaternion.Euler(0, variation, 0) * _lastWanderDirection;
            }
            else
            {
                float angle = UnityEngine.Random.Range(-120f, 120f);
                _lastWanderDirection = Quaternion.Euler(0, transform.eulerAngles.y + angle, 0) * Vector3.forward;
            }


            // Initialize stuck progress tracking
            _lastWanderProgressPosition = transform.position;
            _lastWanderProgressTime = Time.time;

            StartNextWanderLeg();
            SetIdleState(IdleState.Wandering);
        }

        private void StartNextWanderLeg()
        {
            // SOFT LIMIT: If outside the wander radius, force direction back toward home
            if (useSoftWanderLimit && IsOutsideWanderRadius() && _hasHomePosition)
            {
                Vector3 toHome = (_homePosition - transform.position).normalized;
                float distHome = Vector3.Distance(transform.position, _homePosition);
                
                float targetDist = Mathf.Min(distHome - maxWanderRadius * 0.5f, idleWanderMaxDistance);
                targetDist = Mathf.Max(targetDist, idleWanderMinDistance);
                
                Vector3 targetPos = transform.position + toHome * targetDist;
                
                if (ZoneSystem.instance != null)
                {
                    float groundHeight;
                    if (ZoneSystem.instance.GetGroundHeight(targetPos, out groundHeight))
                        targetPos.y = groundHeight;
                }
                
                _lastWanderDirection = toHome;
                SetDestination(targetPos);
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} returning home from {distHome:F1}m out");
                
                return;
            }
            
            // Normal wander logic - use obstacle-aware destination finding
            Vector3? validDestination = FindValidWanderDestination(5);

            if (validDestination.HasValue)
            {
                _lastWanderDirection = (validDestination.Value - transform.position).normalized;
                SetDestination(validDestination.Value);
            }
            else
            {
                // All attempts blocked - stay put and try again next cycle
                if (VerboseLogging)
                    Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} no valid wander destination found, staying put");
                _hasActiveDestination = false;
                SetIdleState(IdleState.Standing);
            }
        }

        private Vector3 GetCommittedWanderPosition()
        {
            float distance = UnityEngine.Random.Range(idleWanderMinDistance, idleWanderMaxDistance);
            distance = Mathf.Max(distance, 5f);

            Vector3 direction;
            if (_currentWanderLeg > 0 && _lastWanderDirection.sqrMagnitude > 0.1f)
            {
                float turnAngle = UnityEngine.Random.Range(minTurnAngle, maxTurnAngle);
                if (UnityEngine.Random.value < 0.5f) turnAngle = -turnAngle;

                turnAngle = Mathf.Clamp(turnAngle, -90f, 90f);

                if (_isOnPath && UnityEngine.Random.value < pathContinueDirectionChance)
                    turnAngle *= 0.3f;

                direction = Quaternion.Euler(0, turnAngle, 0) * _lastWanderDirection;
            }
            else
            {
                direction = _lastWanderDirection.sqrMagnitude > 0.1f ? _lastWanderDirection : transform.forward;
            }

            Vector3 targetPos = transform.position + direction * distance;

            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (ZoneSystem.instance.GetGroundHeight(targetPos, out groundHeight))
                    targetPos.y = groundHeight;
            }

            return targetPos;
        }

        private Vector3? FindNearbyPathPosition()
        {
            Vector3 currentPos = transform.position;

            for (float radius = 3f; radius <= pathDetectionRadius; radius += 3f)
            {
                for (int i = 0; i < 8; i++)
                {
                    float angle = i * (360f / 8f) * Mathf.Deg2Rad;
                    Vector3 checkPos = currentPos + new Vector3(Mathf.Cos(angle) * radius, 0, Mathf.Sin(angle) * radius);

                    if (ZoneSystem.instance != null)
                    {
                        float groundHeight;
                        if (ZoneSystem.instance.GetGroundHeight(checkPos, out groundHeight))
                            checkPos.y = groundHeight;
                    }

                    float score = ScorePositionForPath(checkPos);
                    if (score >= pavedPathBonus * 0.5f)
                    {
                        Vector3 direction = (checkPos - currentPos).normalized;
                        float moveDistance = Mathf.Min(radius * 0.7f, idleWanderMaxDistance);
                        return currentPos + direction * moveDistance;
                    }
                }
            }

            return null;
        }

        private void SetDestination(Vector3 destination)
        {
            _currentDestination = destination;
            _hasActiveDestination = true;

            // Try to acquire movement authority for idle wandering
            var movementAuthority = _companion?.GetMovementAuthority();
            if (movementAuthority != null)
            {
                if (!movementAuthority.TryAcquireAuthority(
                    UnifiedMovementAuthority.MovementSource.IdleWander, 
                    "IdleWander", 
                    30f))
                {
                    // Can't acquire authority - higher priority movement in progress
                    if (VerboseLogging)
                        Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} SetDestination blocked - no authority");
                    _hasActiveDestination = false;
                    return;
                }
                
                // Set destination via authority
                movementAuthority.SetMoveDestination("IdleWander", destination, walk: true, run: false);
            }

            if (_character != null)
            {
                _character.SetWalk(true);
                _character.SetRun(false);
            }

            if (_companionAI != null)
            {
                _companionAI.SetIdleDestination(destination);
                if (VerboseLogging)
                    Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} set wander destination to {destination} (dist: {Vector3.Distance(transform.position, destination):F1}m)");
            }
            else
            {
                Debug.LogWarning($"[CompanionIdleBehavior] No CompanionAI found for {_companion?.companionName} - cannot set destination");
            }
        }

        #endregion

        #region Home Position Helpers

        private bool IsWithinWanderRadius(Vector3 position)
        {
            if (!_hasHomePosition) return true;
            
            if (useSoftWanderLimit) return true;
            
            return Vector3.Distance(position, _homePosition) <= maxWanderRadius;
        }
        
        private bool IsOutsideWanderRadius()
        {
            if (!_hasHomePosition) return false;
            return Vector3.Distance(transform.position, _homePosition) > maxWanderRadius;
        }

        private Vector3 GetHomeBiasedDirection(Vector3 currentPos, Vector3 desiredDirection)
        {
            if (!_hasHomePosition) return desiredDirection;

            float distFromHome = Vector3.Distance(currentPos, _homePosition);
            Vector3 toHome = (_homePosition - currentPos).normalized;

            if (useSoftWanderLimit && distFromHome > maxWanderRadius)
            {
                float overDistance = distFromHome - maxWanderRadius;
                float pullFactor = Mathf.Clamp01(0.8f + overDistance * 0.05f);
                
                if (VerboseLogging)
                    Debug.Log($"[CompanionIdleBehavior] {_companion?.companionName} outside radius ({distFromHome:F1}m > {maxWanderRadius}m) - biasing home with {pullFactor:P0}");
                
                return Vector3.Lerp(desiredDirection, toHome, pullFactor).normalized;
            }

            if (distFromHome < preferredWanderRadius)
                return desiredDirection;

            float normalizedDist = Mathf.InverseLerp(preferredWanderRadius, maxWanderRadius, distFromHome);
            float homePull = normalizedDist * homePullStrength;

            return Vector3.Lerp(desiredDirection, toHome, homePull).normalized;
        }

        #endregion
    }
}
