using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Every container in the loaded world, registered from Container.Awake and removed on destruction, so
    /// GetNearby filters a cached list instead of running a physics overlap each time. Queries return only
    /// player-built storage (<see cref="IsPlayerStorage"/>). Modeled on SmartContainers' ContainersTracker.
    /// </summary>
    public static class ContainerRegistry
    {
        #region Fields
        
        private static HashSet<Container> _allContainers = new HashSet<Container>();
        private static bool _initialized = false;
        private static float _lastCleanupTime = 0f;
        private const float CleanupInterval = 60f;
        
        /// <summary>
        /// Enable verbose logging for debugging.
        /// </summary>
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region Properties
        
        /// <summary>
        /// Total number of tracked containers.
        /// </summary>
        public static int Count => _allContainers.Count;
        
        /// <summary>
        /// Whether the registry has been initialized.
        /// </summary>
        public static bool IsInitialized => _initialized;
        
        #endregion
        
        #region Registration
        
        /// <summary>
        /// Tracks a container from Container.Awake. Queries return only <see cref="IsPlayerStorage"/> ones, checked when
        /// asked: Player.PlacePiece sets the creator after Instantiate has run Awake (Player.cs:2273-2280).
        /// </summary>
        public static void Register(Container container)
        {
            if (container == null || container.GetInventory() == null) return;

            if (_allContainers.Add(container))
            {
                if (VerboseLogging)
                    Debug.Log($"[ContainerRegistry] Registered: {container.name} at {container.transform.position} (total: {_allContainers.Count})");
            }
        }

        /// <summary>
        /// Storage a player built: a chest whose own ZDO carries a Piece creator (ZDOVars.s_creator, set by
        /// Piece.SetCreator on placement). World loot (treasure chests, 1.0 loot_deepNorth_*, Morkhalla chests) has no
        /// creator, tombstones are not storage, and cart/ship containers sit on a child object without the root's ZDO.
        /// </summary>
        public static bool IsPlayerStorage(Container container)
        {
            if (container == null || container.GetInventory() == null || container.GetComponent<TombStone>() != null) return false;
            var nview = container.GetComponent<ZNetView>();
            return nview != null && nview.IsValid() && nview.GetZDO().GetLong(ZDOVars.s_creator, 0L) != 0L;
        }
        
        /// <summary>
        /// Unregisters a container when it's destroyed.
        /// Called from Harmony patch on Container.OnDestroyed.
        /// </summary>
        public static void Unregister(Container container)
        {
            if (container == null) return;
            
            if (_allContainers.Remove(container))
            {
                if (VerboseLogging)
                    Debug.Log($"[ContainerRegistry] Unregistered: {container.name} (total: {_allContainers.Count})");
            }
        }
        
        /// <summary>
        /// Initializes the registry by scanning for existing containers.
        /// Called on game start.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            
            _allContainers.Clear();
            
            var existingContainers = UnityEngine.Object.FindObjectsByType<Container>(UnityEngine.FindObjectsSortMode.None);
            int registered = 0;
            int skipped = 0;

            Debug.Log($"[ContainerRegistry] Initialize: Found {existingContainers.Length} Container components in scene");

            foreach (var container in existingContainers)
            {
                if (container == null || container.GetInventory() == null) { skipped++; continue; }

                _allContainers.Add(container);
                registered++;
            }
            
            _initialized = true;
            Debug.Log($"[ContainerRegistry] Initialized with {registered} containers ({skipped} skipped)");
        }
        
        /// <summary>
        /// Clears the registry. Called on game end or zone change.
        /// </summary>
        public static void Clear()
        {
            _allContainers.Clear();
            _initialized = false;
            Debug.Log("[ContainerRegistry] Cleared");
        }
        
        /// <summary>
        /// Removes invalid/destroyed containers from the registry.
        /// Called periodically to clean up.
        /// </summary>
        public static void Cleanup()
        {
            if (Time.time - _lastCleanupTime < CleanupInterval) return;
            _lastCleanupTime = Time.time;
            
            int before = _allContainers.Count;
            
            _allContainers.RemoveWhere(c => 
                c == null || 
                c.transform == null || 
                c.GetInventory() == null);
            
            int removed = before - _allContainers.Count;
            if (removed > 0 && VerboseLogging)
            {
                Debug.Log($"[ContainerRegistry] Cleanup removed {removed} invalid containers");
            }
        }
        
        #endregion
        
        #region Queries
        
        /// <summary>
        /// Gets all containers within a radius of a position.
        /// Much faster than Physics.OverlapSphere for repeated calls.
        /// NOTE: Allocates a new list. Prefer GetNearby(pos, radius, list) for hot paths.
        /// </summary>
        public static List<Container> GetNearby(Vector3 position, float radius)
        {
            var result = new List<Container>();
            GetNearby(position, radius, result);
            return result;
        }
        
        /// <summary>
        /// Gets all containers within a radius, writing into a caller-provided list.
        /// Avoids allocation on repeated calls (hot-path friendly).
        /// </summary>
        public static void GetNearby(Vector3 position, float radius, List<Container> results)
        {
            results.Clear();
            float radiusSq = radius * radius;
            
            foreach (var container in _allContainers)
            {
                if (container == null || container.transform == null) continue;

                float distSq = (container.transform.position - position).sqrMagnitude;
                if (distSq <= radiusSq && IsPlayerStorage(container))
                {
                    results.Add(container);
                }
            }
        }
        
        /// <summary>
        /// Gets all containers within a radius that are not currently in use.
        /// Note: Container.CheckAccess is private, so we can only check IsInUse and basic validity.
        /// Access control is handled by the Container itself when interacting.
        /// </summary>
        public static List<Container> GetNearbyAvailable(Vector3 position, float radius)
        {
            var nearby = GetNearby(position, radius);
            
            return nearby.Where(c => 
                c != null && 
                !IsInUse(c)
            ).ToList();
        }
        
        /// <summary>
        /// Gets the closest container to a position.
        /// </summary>
        public static Container GetClosest(Vector3 position, float maxRadius = float.MaxValue)
        {
            Container closest = null;
            float closestDistSq = maxRadius * maxRadius;
            
            foreach (var container in _allContainers)
            {
                if (container == null || container.transform == null) continue;

                float distSq = (container.transform.position - position).sqrMagnitude;
                if (distSq < closestDistSq && IsPlayerStorage(container))
                {
                    closestDistSq = distSq;
                    closest = container;
                }
            }
            
            return closest;
        }
        
        /// <summary>
        /// Gets the closest container that has a specific item.
        /// </summary>
        public static Container GetClosestWithItem(Vector3 position, string prefabName, float maxRadius = float.MaxValue)
        {
            Container closest = null;
            float closestDistSq = maxRadius * maxRadius;
            
            foreach (var container in _allContainers)
            {
                if (container == null || container.transform == null) continue;
                
                float distSq = (container.transform.position - position).sqrMagnitude;
                if (distSq >= closestDistSq || !IsPlayerStorage(container)) continue;

                var inv = container.GetInventory();
                
                bool hasItem = false;
                foreach (var item in inv.GetAllItems())
                {
                    if (item?.m_dropPrefab?.name?.Equals(prefabName, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        hasItem = true;
                        break;
                    }
                }
                
                if (hasItem)
                {
                    closestDistSq = distSq;
                    closest = container;
                }
            }
            
            return closest;
        }
        
        /// <summary>
        /// Gets all containers that have a specific item.
        /// </summary>
        public static List<Container> GetAllWithItem(Vector3 position, string prefabName, float maxRadius = float.MaxValue)
        {
            var result = new List<Container>();
            float radiusSq = maxRadius * maxRadius;
            
            foreach (var container in _allContainers)
            {
                if (container == null || container.transform == null) continue;
                
                float distSq = (container.transform.position - position).sqrMagnitude;
                if (distSq > radiusSq || !IsPlayerStorage(container)) continue;

                var inv = container.GetInventory();
                
                foreach (var item in inv.GetAllItems())
                {
                    if (item?.m_dropPrefab?.name?.Equals(prefabName, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        result.Add(container);
                        break;
                    }
                }
            }
            
            return result;
        }
        
        #endregion
        
        #region Utility
        
        /// <summary>
        /// Checks if a container is currently being used by another player.
        /// Uses the "InUse" ZDO flag set by vanilla Valheim, or the Container.IsInUse() method.
        /// </summary>
        public static bool IsInUse(Container container)
        {
            if (container == null) return false;
            
            // Use the public IsInUse() method
            return container.IsInUse();
        }
        
        /// <summary>
        /// Gets the total count of a specific item across all nearby containers.
        /// </summary>
        public static int GetTotalItemCount(Vector3 position, string prefabName, float radius)
        {
            int total = 0;
            var nearby = GetNearby(position, radius);
            
            foreach (var container in nearby)
            {
                if (container == null) continue;
                
                var inv = container.GetInventory();
                if (inv == null) continue;
                
                foreach (var item in inv.GetAllItems())
                {
                    if (item?.m_dropPrefab?.name?.Equals(prefabName, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        total += item.m_stack;
                    }
                }
            }
            
            return total;
        }
        
        /// <summary>
        /// Gets debug information about the registry.
        /// </summary>
        public static string GetDebugInfo()
        {
            return $"ContainerRegistry: {_allContainers.Count} containers tracked, initialized={_initialized}";
        }
        
        #endregion
    }
}
