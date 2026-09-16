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
                if (ZoneSystem.instance.GetGroundHeight(position, out groundHeight))
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
        
        #region Backwards Compatibility
        
        /// <summary>
        /// Gets the best position for generic piece interaction.
        /// </summary>
        public static Vector3 GetPieceInteractionPoint(MonoBehaviour target, Vector3 approachFrom, float standDistance = DEFAULT_INTERACTION_DISTANCE)
        {
            if (target == null)
                return approachFrom;
            
            Transform targetTransform = target.transform;
            Vector3 frontPoint = targetTransform.position + targetTransform.forward * standDistance;
            return GetGroundPosition(frontPoint);
        }
        
        #endregion
    }
}
