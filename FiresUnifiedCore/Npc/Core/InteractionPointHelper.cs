using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Finds where a companion should stand to use an object. Pathing to the prefab's center put companions inside
    /// or behind it, so this resolves the actual interaction point (a smelter's switches, a crafting station's use
    /// distance, the chest itself) and stands near that instead.
    /// </summary>
    public static class InteractionPointHelper
    {
        /// <summary>
        /// Default distance to stand from the interactable point.
        /// Valheim uses m_useDistance of ~2m for most things, so standing 1.5m away is safe.
        /// </summary>
        public const float DEFAULT_INTERACTION_DISTANCE = 1.5f;
        
        /// <summary>
        /// How close we need to be to consider ourselves "at" the interaction point.
        /// Set to 2.5m to be forgiving - pathfinding may not reach exact spot due to obstacles.
        /// Valheim's m_useDistance is typically 2m, so 2.5m ensures we're within range.
        /// </summary>
        public const float ARRIVAL_THRESHOLD = 2.5f;
        
        /// <summary>
        /// Maximum distance from which interaction is allowed.
        /// Set to 2.5m to match ARRIVAL_THRESHOLD - if we've "arrived", we can interact.
        /// </summary>
        public const float MAX_INTERACTION_RANGE = 2.5f;
        
        #region Main Entry Point
        
        /// <summary>
        /// Gets the best position to stand when interacting with any interactable.
        /// Automatically detects the type and finds the actual interactable point.
        /// </summary>
        public static Vector3 GetInteractionPoint(GameObject target, Vector3 approachFrom, float standDistance = DEFAULT_INTERACTION_DISTANCE)
        {
            if (target == null)
                return approachFrom;
            
            // Try to find the actual interactable point on this object
            Vector3 interactablePoint = FindInteractablePoint(target);
            if (TryGetPathReachPoint(target, interactablePoint, approachFrom, standDistance, out var reach)) return reach;
            
            // Calculate standing position - offset from interactable toward companion
            Vector3 dirToCompanion = (approachFrom - interactablePoint).normalized;
            dirToCompanion.y = 0;
            
            if (dirToCompanion.sqrMagnitude < 0.01f)
            {
                // Fallback to object's forward direction
                dirToCompanion = -target.transform.forward;
            }
            
            Vector3 standPoint = interactablePoint + dirToCompanion * standDistance;
            return GetGroundPosition(standPoint);
        }
        
        /// <summary>
        /// Finds the actual interactable point on a GameObject.
        /// This searches for Switch components, Hoverable colliders, etc.
        /// </summary>
        public static Vector3 FindInteractablePoint(GameObject target)
        {
            if (target == null)
                return Vector3.zero;
            
            // Priority 1: Check for Smelter with Switch components
            var smelter = target.GetComponent<Smelter>();
            if (smelter != null)
            {
                return FindSmelterInteractablePoint(smelter);
            }
            
            // Priority 2: Check for CraftingStation
            var craftingStation = target.GetComponent<CraftingStation>();
            if (craftingStation != null)
            {
                return FindCraftingStationInteractablePoint(craftingStation);
            }
            
            // Priority 3: Check for Container
            var container = target.GetComponent<Container>();
            if (container != null)
            {
                return FindContainerInteractablePoint(container);
            }
            
            // Priority 4: Look for any Switch component in children
            var switchComp = target.GetComponentInChildren<Switch>();
            if (switchComp != null)
            {
                return switchComp.transform.position;
            }
            
            // Priority 5: Look for any Hoverable collider
            var hoverable = FindHoverableCollider(target);
            if (hoverable != Vector3.zero)
            {
                return hoverable;
            }
            
            // Fallback: Use transform position
            return target.transform.position;
        }
        
        #endregion
        
        #region Smelter Interaction
        
        /// <summary>
        /// Finds the best interactable point on a Smelter (kiln, smelter, etc.)
        /// Smelters have multiple Switch components - we find the most accessible one.
        /// </summary>
        public static Vector3 FindSmelterInteractablePoint(Smelter smelter)
        {
            if (smelter == null)
                return Vector3.zero;
            
            // Smelter has these Switch fields: m_addWoodSwitch, m_addOreSwitch, m_emptyOreSwitch
            // These are the actual interactable points
            
            List<Vector3> switchPositions = new List<Vector3>();
            
            // Use reflection to get the Switch fields since they're public
            if (smelter.m_addOreSwitch != null)
                switchPositions.Add(smelter.m_addOreSwitch.transform.position);
            
            if (smelter.m_addWoodSwitch != null)
                switchPositions.Add(smelter.m_addWoodSwitch.transform.position);
            
            if (smelter.m_emptyOreSwitch != null)
                switchPositions.Add(smelter.m_emptyOreSwitch.transform.position);
            
            // If we found switches, return the average position (center of interaction area)
            if (switchPositions.Count > 0)
            {
                Vector3 avgPos = Vector3.zero;
                foreach (var pos in switchPositions)
                    avgPos += pos;
                avgPos /= switchPositions.Count;
                return avgPos;
            }
            
            // Fallback: Look for child objects that might be switches
            var childSwitches = smelter.GetComponentsInChildren<Switch>();
            if (childSwitches.Length > 0)
            {
                Vector3 avgPos = Vector3.zero;
                foreach (var childSwitch in childSwitches)
                    avgPos += childSwitch.transform.position;
                avgPos /= childSwitches.Length;
                return avgPos;
            }
            
            // Last fallback: smelter position
            return smelter.transform.position;
        }
        
        /// <summary>
        /// Gets the position of a specific Switch on the smelter.
        /// </summary>
        public static Vector3 GetSmelterSwitchPosition(Smelter smelter, SmelterSwitchType switchType)
        {
            if (smelter == null)
                return Vector3.zero;
            
            Switch targetSwitch = null;
            switch (switchType)
            {
                case SmelterSwitchType.AddOre:
                    targetSwitch = smelter.m_addOreSwitch;
                    break;
                case SmelterSwitchType.AddFuel:
                    targetSwitch = smelter.m_addWoodSwitch;
                    break;
                case SmelterSwitchType.Empty:
                    targetSwitch = smelter.m_emptyOreSwitch;
                    break;
            }
            
            return targetSwitch != null ? targetSwitch.transform.position : smelter.transform.position;
        }
        
        public enum SmelterSwitchType
        {
            AddOre,
            AddFuel,
            Empty
        }
        
        #endregion
        
        #region CraftingStation Interaction
        
        /// <summary>
        /// Finds the interactable point on a CraftingStation (workbench, forge, etc.)
        /// CraftingStations use InUseDistance() which checks from transform.position.
        /// </summary>
        public static Vector3 FindCraftingStationInteractablePoint(CraftingStation station)
        {
            if (station == null)
                return Vector3.zero;
            
            // CraftingStation checks distance from transform.position
            // But we should look for any child interactables first
            
            var childSwitches = station.GetComponentsInChildren<Switch>();
            if (childSwitches.Length > 0)
            {
                // Return position of first switch
                return childSwitches[0].transform.position;
            }
            
            // CraftingStation uses transform.position for interaction
            return station.transform.position;
        }
        
        /// <summary>
        /// Gets the use distance for a crafting station (how close player needs to be).
        /// </summary>
        public static float GetCraftingStationUseDistance(CraftingStation station)
        {
            if (station == null)
                return DEFAULT_INTERACTION_DISTANCE;
            
            return station.m_useDistance;
        }
        
        #endregion
        
        #region Container Interaction
        
        /// <summary>
        /// Finds the interactable point on a Container (chest, etc.)
        /// For containers, the interaction is usually at the front where the lid opens.
        /// </summary>
        public static Vector3 FindContainerInteractablePoint(Container container)
        {
            if (container == null)
                return Vector3.zero;
            
            // Containers interact at their transform position
            // But we want to stand in FRONT (where lid opens)
            return container.transform.position;
        }
        
        /// <summary>
        /// Gets the best position to stand when interacting with a container.
        /// Accounts for chest facing direction - stands in FRONT where the lid opens.
        /// </summary>
        public static Vector3 GetContainerInteractionPoint(Container container, Vector3 approachFrom, float standDistance = DEFAULT_INTERACTION_DISTANCE)
        {
            if (container == null)
                return approachFrom;
            
            Transform containerTransform = container.transform;
            Vector3 containerPos = containerTransform.position;
            if (TryGetPathReachPoint(container.gameObject, FindContainerInteractablePoint(container), approachFrom, standDistance, out var reach)) return reach;
            
            // Chests open toward their local forward direction
            Vector3 frontDir = containerTransform.forward;
            
            // Calculate the front interaction point
            Vector3 frontPoint = containerPos + frontDir * standDistance;
            
            // Also calculate side points in case front is blocked
            Vector3 rightPoint = containerPos + containerTransform.right * standDistance;
            Vector3 leftPoint = containerPos - containerTransform.right * standDistance;
            
            // Prefer front, but pick closest accessible point
            Vector3[] candidates = { frontPoint, rightPoint, leftPoint };
            
            Vector3 bestPoint = frontPoint;
            float bestScore = float.MaxValue;
            
            foreach (var point in candidates)
            {
                Vector3 groundPoint = GetGroundPosition(point);
                float distFromApproach = Vector3.Distance(approachFrom, groundPoint);
                bool pathClear = IsPathClear(approachFrom, groundPoint);
                
                float score = distFromApproach;
                if (!pathClear) score += 10f;
                if (point == frontPoint) score -= 2f; // Prefer front
                
                if (score < bestScore)
                {
                    bestScore = score;
                    bestPoint = groundPoint;
                }
            }
            
            return bestPoint;
        }
        
        #endregion
        
        #region Generic Smelter Helper (backwards compatibility)
        
        /// <summary>
        /// Gets the best position to stand when interacting with a smelter.
        /// Uses the Switch positions to find actual interactable points.
        /// </summary>
        public static Vector3 GetSmelterInteractionPoint(Smelter smelter, Vector3 approachFrom, float standDistance = DEFAULT_INTERACTION_DISTANCE)
        {
            if (smelter == null)
                return approachFrom;
            
            // Find the actual interactable point (Switch position)
            Vector3 interactablePoint = FindSmelterInteractablePoint(smelter);
            
            // Calculate standing position
            Vector3 dirToCompanion = (approachFrom - interactablePoint).normalized;
            dirToCompanion.y = 0;
            
            if (dirToCompanion.sqrMagnitude < 0.01f)
            {
                dirToCompanion = -smelter.transform.forward;
            }
            
            Vector3 standPoint = interactablePoint + dirToCompanion * standDistance;
            return GetGroundPosition(standPoint);
        }
        
        #endregion
        
        #region Utility Methods
        
        /// <summary>
        /// Searches for a Hoverable collider in the target's children.
        /// Many interactables use colliders with the Hoverable interface.
        /// </summary>
        private static Vector3 FindHoverableCollider(GameObject target)
        {
            // Look for colliders that might be the interaction point
            var colliders = target.GetComponentsInChildren<Collider>();
            foreach (var collider in colliders)
            {
                // Check if this collider or its parent has Hoverable
                var hoverable = collider.GetComponent<Hoverable>();
                if (hoverable == null)
                    hoverable = collider.GetComponentInParent<Hoverable>();
                
                if (hoverable != null)
                {
                    return collider.bounds.center;
                }
            }
            
            return Vector3.zero;
        }
        
        /// <summary>
        /// Gets multiple interaction points around an object.
        /// Useful for finding an unobstructed approach.
        /// </summary>
        public static List<Vector3> GetInteractionPointCandidates(Transform target, float standDistance = DEFAULT_INTERACTION_DISTANCE)
        {
            var points = new List<Vector3>();
            if (target == null) return points;
            
            Vector3 center = target.position;
            
            // Front (preferred)
            points.Add(GetGroundPosition(center + target.forward * standDistance));
            
            // Sides
            points.Add(GetGroundPosition(center + target.right * standDistance));
            points.Add(GetGroundPosition(center - target.right * standDistance));
            
            // Back (least preferred but valid)
            points.Add(GetGroundPosition(center - target.forward * standDistance));
            
            // Diagonals
            points.Add(GetGroundPosition(center + (target.forward + target.right).normalized * standDistance));
            points.Add(GetGroundPosition(center + (target.forward - target.right).normalized * standDistance));
            
            return points;
        }
        
        /// <summary>
        /// Finds the best interaction point from a list of candidates.
        /// </summary>
        public static Vector3 FindBestInteractionPoint(
            List<Vector3> candidates, 
            Vector3 approachFrom, 
            Vector3 targetCenter,
            Vector3 targetForward)
        {
            if (candidates == null || candidates.Count == 0)
                return approachFrom;
            
            Vector3 bestPoint = candidates[0];
            float bestScore = float.MaxValue;
            
            foreach (var point in candidates)
            {
                float score = 0f;
                float dist = Vector3.Distance(approachFrom, point);
                score += dist;
                
                if (!IsPathClear(approachFrom, point))
                    score += 15f;
                
                Vector3 toPoint = (point - targetCenter).normalized;
                float frontDot = Vector3.Dot(toPoint, targetForward);
                if (frontDot > 0.5f) score -= 3f;
                if (frontDot < -0.5f) score += 3f;
                
                if (score < bestScore)
                {
                    bestScore = score;
                    bestPoint = point;
                }
            }
            
            return bestPoint;
        }
        
        /// <summary>
        /// Gets the best standing position near an interactable point.
        /// </summary>
        public static Vector3 GetStandingPositionNear(Vector3 interactablePoint, Vector3 approachFrom, float standDistance = DEFAULT_INTERACTION_DISTANCE)
        {
            Vector3 dir = (approachFrom - interactablePoint).normalized;
            dir.y = 0;
            
            if (dir.sqrMagnitude < 0.01f)
                dir = Vector3.forward;
            
            Vector3 standPoint = interactablePoint + dir * standDistance;
            return GetGroundPosition(standPoint);
        }
        
        /// <summary>
        /// Adjusts Y position to ground height at the given XZ position.
        /// </summary>
        public static Vector3 GetGroundPosition(Vector3 position)
        {
            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (FiresCore.World.Surface.GroundNear(position, out groundHeight))
                {
                    return new Vector3(position.x, groundHeight, position.z);
                }
            }
            
            if (Physics.Raycast(position + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 20f, LayerMask.GetMask("terrain", "Default", "static_solid")))
            {
                return hit.point;
            }
            
            return position;
        }
        
        /// <summary>
        /// Checks if there's a clear path between two points.
        /// </summary>
        public static bool IsPathClear(Vector3 from, Vector3 to)
        {
            Vector3 start = from + Vector3.up * 0.5f;
            Vector3 end = to + Vector3.up * 0.5f;
            
            Vector3 dir = end - start;
            float dist = dir.magnitude;
            
            if (Physics.Raycast(start, dir.normalized, out RaycastHit hit, dist, LayerMask.GetMask("Default", "static_solid", "piece")))
            {
                if (hit.collider.gameObject.isStatic || hit.collider.GetComponent<Piece>() != null)
                {
                    return false;
                }
            }
            
            return true;
        }
        
        /// <summary>
        /// Checks if a position is valid for standing.
        /// </summary>
        public static bool IsValidStandingPosition(Vector3 position)
        {
            if (!Physics.Raycast(position + Vector3.up * 2f, Vector3.down, out RaycastHit groundHit, 5f))
            {
                return false;
            }
            
            var overlaps = Physics.OverlapSphere(position + Vector3.up * 0.5f, 0.3f, LayerMask.GetMask("static_solid", "Default"));
            if (overlaps.Length > 0)
            {
                foreach (var collider in overlaps)
                {
                    if (collider.gameObject.isStatic)
                        return false;
                }
            }
            
            return true;
        }
        
        #endregion
        
        #region Path-checked reach (Fire 10-24)

        // Fire 10-24: "companions will still come out side and up against the wall because they determine that is the shortest
        // distance to the storage chests that are upstairs". The stand point was the target point pushed straight toward the
        // companion and dropped to the terrain from 5 m up (piece floors ignored): for a chest upstairs or a nest on a wall that is
        // the ground outside / under it. A stand point now counts only when it is on the target's own level, sees the target
        // (nothing solid between chest height and the target) and a walkable path from the companion ends on it.
        private const int ReachRingSteps = 12;
        private const float ReachMaxHeightGap = 2.2f, PathEndSlack = 1.0f, ReachCacheSeconds = 5f;
        private static int s_standMask = -1, s_losMask = -1;
        private static readonly List<Vector3> s_pathBuf = new List<Vector3>();
        private static readonly RaycastHit[] s_rayHits = new RaycastHit[16];
        private static readonly Dictionary<long, (Vector3 point, float at)> s_reachCache = new Dictionary<long, (Vector3, float)>();
        private static readonly HashSet<long> s_reachLogged = new HashSet<long>();

        private static int StandMask => s_standMask != -1 ? s_standMask : (s_standMask = LayerMask.GetMask("terrain", "Default", "static_solid", "piece", "vehicle"));
        private static int LosMask => s_losMask != -1 ? s_losMask : (s_losMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle"));

        /// <summary>The best stand point for <paramref name="target"/> reachable by a walkable path from <paramref name="from"/>
        /// with line of sight to <paramref name="interactable"/>; false when none is (callers fall back to the old point).</summary>
        public static bool TryGetPathReachPoint(GameObject target, Vector3 interactable, Vector3 from, float standDistance, out Vector3 point)
        {
            point = Vector3.zero;
            if (target == null || Pathfinding.instance == null) return false;
            long key = ((long)target.GetInstanceID() << 32) ^ (uint)(Mathf.RoundToInt(from.x / 2f) * 73856093 ^ Mathf.RoundToInt(from.y / 2f) * 19349663 ^ Mathf.RoundToInt(from.z / 2f) * 83492791);
            if (s_reachCache.TryGetValue(key, out var hit) && Time.time - hit.at < ReachCacheSeconds) { point = hit.point; return true; }
            if (s_reachCache.Count > 256) s_reachCache.Clear();

            Transform root = target.transform.root;
            var cands = new List<(Vector3 p, float straight)>();
            foreach (float dist in new[] { standDistance, Mathf.Max(0.9f, standDistance * 0.6f) })
                for (int i = 0; i < ReachRingSteps; i++)
                {
                    float a = i * Mathf.PI * 2f / ReachRingSteps;
                    Vector3 probe = interactable + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * dist;
                    if (!StandOn(probe, interactable.y, root, out Vector3 ground)) continue;
                    if (interactable.y - ground.y > ReachMaxHeightGap) continue;
                    if (!SeesTarget(ground + Vector3.up * 1.2f, interactable, root)) continue;
                    cands.Add((ground, Vector3.Distance(from, ground)));
                }
            cands.Sort((x, y) => x.straight.CompareTo(y.straight));

            float bestLen = float.MaxValue; int tried = 0, pathed = 0;
            foreach (var c in cands)
            {
                if (tried++ >= 8 || pathed >= 3) break;
                s_pathBuf.Clear();
                if (!Pathfinding.instance.GetPath(from, c.p, s_pathBuf, Pathfinding.AgentType.Humanoid, cleanup: true) || s_pathBuf.Count == 0) continue;
                Vector3 end = s_pathBuf[s_pathBuf.Count - 1];
                Vector3 d = end - c.p;
                if (Mathf.Abs(d.y) > PathEndSlack || new Vector2(d.x, d.z).magnitude > PathEndSlack) continue;
                float len = 0f; Vector3 prev = from;
                foreach (var p in s_pathBuf) { len += Vector3.Distance(prev, p); prev = p; }
                pathed++;
                if (len < bestLen) { bestLen = len; point = c.p; }
            }
            bool found = bestLen < float.MaxValue;
            if (found) s_reachCache[key] = (point, Time.time);
            if (s_reachLogged.Add(key))
                FiresCore.Logging.FiresLogger.LogDebug(found
                    ? $"[Reach] {Utils.GetPrefabName(target)}: stand at {point:F1} (path {bestLen:0.0} m; {cands.Count} candidate(s) on its level with line of sight)"
                    : $"[Reach] {Utils.GetPrefabName(target)} at {interactable:F1}: no walkable stand point with line of sight from {from:F1} ({cands.Count} candidate(s), {tried} path(s) tried)");
            return found;
        }

        // The highest walkable surface under the probe that is not above the target's own level (+1 m) and not the target itself.
        private static bool StandOn(Vector3 probe, float levelY, Transform targetRoot, out Vector3 ground)
        {
            ground = probe;
            Vector3 start = new Vector3(probe.x, levelY + 1f, probe.z);
            int n = Physics.RaycastNonAlloc(start, Vector3.down, s_rayHits, ReachMaxHeightGap + 2f, StandMask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var h = s_rayHits[i];
                if (h.collider == null || h.collider.transform.IsChildOf(targetRoot) || h.normal.y < 0.6f) continue;
                if (h.distance < best) { best = h.distance; ground = h.point; }
            }
            return best < float.MaxValue;
        }

        private static bool SeesTarget(Vector3 eye, Vector3 target, Transform targetRoot)
        {
            Vector3 d = target - eye; float len = d.magnitude;
            if (len < 0.05f) return true;
            int n = Physics.RaycastNonAlloc(eye, d / len, s_rayHits, len, LosMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = s_rayHits[i].collider;
                if (c == null || c.transform.IsChildOf(targetRoot) || c.GetComponentInParent<Door>() != null) continue;   // doors never block (0.2.783)
                if (c.GetComponentInParent<Character>() != null) continue;
                return false;
            }
            return true;
        }

        #endregion

        #region Backwards Compatibility
        
        /// <summary>
        /// Gets the best position for generic piece interaction.
        /// </summary>
        public static Vector3 GetPieceInteractionPoint(MonoBehaviour target, Vector3 approachFrom, float standDistance = DEFAULT_INTERACTION_DISTANCE)
        {
            if (target == null)
                return approachFrom;
            
            Transform targetTransform = target.transform;
            if (TryGetPathReachPoint(target.gameObject, FindInteractablePoint(target.gameObject), approachFrom, standDistance, out var reach)) return reach;
            Vector3 frontPoint = targetTransform.position + targetTransform.forward * standDistance;
            return GetGroundPosition(frontPoint);
        }
        
        #endregion
    }
}
