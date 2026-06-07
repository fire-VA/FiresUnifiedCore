using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Global registry of all containers in the game world.
    /// 
    /// INSPIRED BY: SmartContainers mod's ContainersTracker
    /// 
    /// WHY THIS EXISTS:
    /// - Physics.OverlapSphere every frame is expensive
    /// - Containers don't move, so we can track them globally
    /// - Much faster lookup for nearby containers
    /// 
    /// HOW IT WORKS:
    /// - Harmony patches Container.Awake to register containers
    /// - Harmony patches Container.OnDestroyed to unregister containers
    /// - GetNearby() filters the cached list by distance
    /// 
    /// USAGE:
    /// Instead of: ChestHelper.FindNearbyChests(pos, radius)
    /// Use:        ContainerRegistry.GetNearby(pos, radius)
    /// </summary>
    public static class ContainerRegistry
    {
        #region Fields
        
        private static HashSet<Container> _allContainers = new HashSet<Container>();
        private static bool _initialized = false;
        private static float _lastCleanupTime = 0f;
        private const float CLEANUP_INTERVAL = 60f;
        
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
        /// Registers a container to be tracked.
        /// Called from Harmony patch on Container.Awake.
        /// </summary>
        public static void Register(Container container)
        {
            if (container == null) return;
            
            // Skip treasure chests (not player-usable)
            string containerName = container.name?.ToLowerInvariant() ?? "";
            if (containerName.StartsWith("treasure")) return;
            
            // Skip containers without an inventory
            if (container.GetInventory() == null) return;
            
            var nview = container.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;
            
            // CRITICAL FIX: Accept containers based on multiple criteria
            var zdo = nview.GetZDO();
            if (zdo == null) return;
            
            long creator = zdo.GetLong(ZDOVars.s_creator, 0L);
            long owner = zdo.GetOwner();
            
            // Check layer
            int containerLayer = container.gameObject.layer;
            bool isPlayerPiece = containerLayer == LayerMask.NameToLayer("piece") ||
                                containerLayer == LayerMask.NameToLayer("piece_nonsolid");
            
            // Also check by prefab name - common chest prefab patterns
            bool isChestPrefab = containerName.Contains("chest") ||
                                containerName.Contains("karve") ||    // Ship storage
                                containerName.Contains("longship") || // Ship storage  
                                containerName.Contains("cart") ||     // Cart storage
                                containerName.Contains("storage") ||
                                containerName.Contains("box");
            
            // Accept if any indicator of player ownership OR is a known chest prefab
            if (creator == 0L && owner == 0L && !isPlayerPiece && !isChestPrefab) 
            {
                if (VerboseLogging)
                    Debug.Log($"[ContainerRegistry] SKIPPED: {container.name} (creator={creator}, owner={owner}, layer={LayerMask.LayerToName(containerLayer)}, isChestPrefab={isChestPrefab})");
                return;
            }
            
            if (_allContainers.Add(container))
            {
                if (VerboseLogging)
                    Debug.Log($"[ContainerRegistry] Registered: {container.name} at {container.transform.position} (creator={creator}, owner={owner}, layer={LayerMask.LayerToName(containerLayer)}, isChestPrefab={isChestPrefab}, total: {_allContainers.Count})");
            }
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
            
            int pieceLayer = LayerMask.NameToLayer("piece");
            int pieceNonsolidLayer = LayerMask.NameToLayer("piece_nonsolid");
            
            Debug.Log($"[ContainerRegistry] Initialize: Found {existingContainers.Length} Container components in scene");
            
            foreach (var container in existingContainers)
            {
                if (container == null) continue;
                
                string containerName = container.name?.ToLowerInvariant() ?? "";
                if (containerName.StartsWith("treasure")) { skipped++; continue; }
                if (container.GetInventory() == null) { skipped++; continue; }
                
                var nview = container.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) { skipped++; continue; }
                
                var zdo = nview.GetZDO();
                if (zdo == null) { skipped++; continue; }
                
                long creator = zdo.GetLong(ZDOVars.s_creator, 0L);
                long owner = zdo.GetOwner();
                int containerLayer = container.gameObject.layer;
                bool isPlayerPiece = containerLayer == pieceLayer ||
                                    containerLayer == pieceNonsolidLayer;
                
                // Also check by prefab name - common chest prefab patterns
                bool isChestPrefab = containerName.Contains("chest") ||
                                    containerName.Contains("karve") ||
                                    containerName.Contains("longship") ||
                                    containerName.Contains("cart") ||
                                    containerName.Contains("storage") ||
                                    containerName.Contains("box");
                
                // Accept if any indicator of player ownership OR is a known chest prefab
                if (creator == 0L && owner == 0L && !isPlayerPiece && !isChestPrefab) 
                { 
                    skipped++; 
                    continue; 
                }
                
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
            if (Time.time - _lastCleanupTime < CLEANUP_INTERVAL) return;
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
                if (distSq <= radiusSq)
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
                if (distSq < closestDistSq)
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
                if (distSq >= closestDistSq) continue;
                
                var inv = container.GetInventory();
                if (inv == null) continue;
                
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
                if (distSq > radiusSq) continue;
                
                var inv = container.GetInventory();
                if (inv == null) continue;
                
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
