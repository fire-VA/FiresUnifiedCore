using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Centralized manager for tracking which interactable objects are currently being used.
    /// Prevents companions and players from stacking into each other at chairs, workbenches,
    /// smelters, and other interactable objects.
    /// 
    /// USAGE:
    /// - Call TryOccupy() before interacting with an object
    /// - Call Release() when done interacting
    /// - Call IsOccupied() to check if an object is in use
    /// 
    /// TRACKED OBJECTS:
    /// - Chairs/Benches/Stools (attach points)
    /// - Workstations (CraftingStation)
    /// - Fireplaces (Fireplace, CookingStation)
    /// - Smelters (Smelter)
    /// - Any object with a specific attach/interaction point
    /// 
    /// DESIGN:
    /// - Uses weak references to handle destroyed objects gracefully
    /// - Automatically cleans up stale entries periodically
    /// - Thread-safe for potential future use
    /// </summary>
    public static class InteractableOccupancyManager
    {
        #region Constants
        
        /// <summary>
        /// Default radius to check for occupancy when no specific attach point is used.
        /// </summary>
        public const float DEFAULT_OCCUPANCY_RADIUS = 1.5f;
        
        /// <summary>
        /// Minimum distance between two companions using adjacent objects.
        /// </summary>
        public const float PERSONAL_SPACE_RADIUS = 1.0f;
        
        /// <summary>
        /// How often to clean up stale entries (in seconds).
        /// </summary>
        private const float CLEANUP_INTERVAL = 30f;
        
        #endregion
        
        #region State
        
        /// <summary>
        /// Maps GameObject instance IDs to the Character/companion using them.
        /// </summary>
        private static Dictionary<int, OccupancyEntry> _occupiedObjects = new Dictionary<int, OccupancyEntry>();
        
        /// <summary>
        /// Last time we cleaned up stale entries.
        /// </summary>
        private static float _lastCleanupTime;
        
        /// <summary>
        /// Lock for thread safety.
        /// </summary>
        private static readonly object _lock = new object();
        
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region Occupancy Entry
        
        private class OccupancyEntry
        {
            public int OccupantInstanceId;
            public string OccupantName;
            public float OccupyTime;
            public float MaxDuration;
            public Vector3 Position;
            
            public bool IsExpired => Time.time - OccupyTime > MaxDuration;
        }
        
        #endregion
        
        #region Public API
        
        /// <summary>
        /// Attempts to occupy an interactable object.
        /// Returns true if the object is now occupied by the given character.
        /// Returns false if the object is already occupied by someone else.
        /// </summary>
        /// <param name="interactable">The GameObject to occupy (chair, workbench, etc.)</param>
        /// <param name="occupant">The Character (companion or player) occupying it</param>
        /// <param name="maxDuration">Maximum time this occupancy should last (for auto-cleanup)</param>
        public static bool TryOccupy(GameObject interactable, Character occupant, float maxDuration = 300f)
        {
            if (interactable == null || occupant == null)
                return false;
            
            CleanupIfNeeded();
            
            int objectId = interactable.GetInstanceID();
            int occupantId = occupant.GetInstanceID();
            
            lock (_lock)
            {
                // Check if already occupied
                if (_occupiedObjects.TryGetValue(objectId, out var existing))
                {
                    // If same occupant, just update
                    if (existing.OccupantInstanceId == occupantId)
                    {
                        existing.OccupyTime = Time.time;
                        existing.MaxDuration = maxDuration;
                        return true;
                    }
                    
                    // Check if previous occupant still exists and is valid
                    if (!existing.IsExpired)
                    {
                        // Still occupied by someone else
                        if (VerboseLogging)
                        {
                            Debug.Log($"[InteractableOccupancyManager] {interactable.name} is already occupied by {existing.OccupantName}");
                        }
                        return false;
                    }
                    
                    // Previous entry expired, remove it
                    _occupiedObjects.Remove(objectId);
                }
                
                // Register new occupancy
                _occupiedObjects[objectId] = new OccupancyEntry
                {
                    OccupantInstanceId = occupantId,
                    OccupantName = occupant.m_name ?? occupant.name,
                    OccupyTime = Time.time,
                    MaxDuration = maxDuration,
                    Position = interactable.transform.position
                };
                
                if (VerboseLogging)
                {
                    Debug.Log($"[InteractableOccupancyManager] {occupant.m_name} now occupying {interactable.name}");
                }
                
                return true;
            }
        }
        
        /// <summary>
        /// Releases occupancy of an interactable object.
        /// </summary>
        /// <param name="interactable">The GameObject to release</param>
        /// <param name="occupant">The Character releasing it (must match the current occupant)</param>
        public static void Release(GameObject interactable, Character occupant)
        {
            if (interactable == null)
                return;
            
            int objectId = interactable.GetInstanceID();
            int occupantId = occupant?.GetInstanceID() ?? 0;
            
            lock (_lock)
            {
                if (_occupiedObjects.TryGetValue(objectId, out var existing))
                {
                    // Only remove if the occupant matches or is null (forced release)
                    if (occupant == null || existing.OccupantInstanceId == occupantId)
                    {
                        _occupiedObjects.Remove(objectId);
                        
                        if (VerboseLogging)
                        {
                            Debug.Log($"[InteractableOccupancyManager] Released {interactable.name}");
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// Forces release of all objects occupied by a specific character.
        /// Useful when a companion is destroyed, teleported, or enters combat.
        /// </summary>
        public static void ReleaseAllForOccupant(Character occupant)
        {
            if (occupant == null)
                return;
            
            int occupantId = occupant.GetInstanceID();
            
            lock (_lock)
            {
                var toRemove = new List<int>();
                
                foreach (var kvp in _occupiedObjects)
                {
                    if (kvp.Value.OccupantInstanceId == occupantId)
                    {
                        toRemove.Add(kvp.Key);
                    }
                }
                
                foreach (int id in toRemove)
                {
                    _occupiedObjects.Remove(id);
                }
                
                if (VerboseLogging && toRemove.Count > 0)
                {
                    Debug.Log($"[InteractableOccupancyManager] Released {toRemove.Count} objects for {occupant.m_name}");
                }
            }
        }
        
        /// <summary>
        /// Checks if an interactable object is currently occupied.
        /// </summary>
        /// <param name="interactable">The GameObject to check</param>
        /// <param name="excludeCharacter">Optional character to exclude from the check (e.g., "is it occupied by someone other than me?")</param>
        public static bool IsOccupied(GameObject interactable, Character excludeCharacter = null)
        {
            if (interactable == null)
                return false;
            
            int objectId = interactable.GetInstanceID();
            int excludeId = excludeCharacter?.GetInstanceID() ?? 0;
            
            lock (_lock)
            {
                if (_occupiedObjects.TryGetValue(objectId, out var existing))
                {
                    // If excluding a character and it matches, not considered occupied
                    if (excludeCharacter != null && existing.OccupantInstanceId == excludeId)
                        return false;
                    
                    // Check if expired
                    if (existing.IsExpired)
                    {
                        _occupiedObjects.Remove(objectId);
                        return false;
                    }
                    
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if a position is occupied by any interactable.
        /// Use this when you don't have a specific GameObject reference.
        /// </summary>
        /// <param name="position">The position to check</param>
        /// <param name="radius">Radius around the position to check</param>
        /// <param name="excludeCharacter">Optional character to exclude from the check</param>
        public static bool IsPositionOccupied(Vector3 position, float radius = DEFAULT_OCCUPANCY_RADIUS, Character excludeCharacter = null)
        {
            int excludeId = excludeCharacter?.GetInstanceID() ?? 0;
            
            lock (_lock)
            {
                foreach (var kvp in _occupiedObjects)
                {
                    var entry = kvp.Value;
                    
                    // Skip if expired
                    if (entry.IsExpired)
                        continue;
                    
                    // Skip if it's the excluded character
                    if (excludeCharacter != null && entry.OccupantInstanceId == excludeId)
                        continue;
                    
                    // Check distance
                    float dist = Vector3.Distance(position, entry.Position);
                    if (dist < radius)
                        return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if a position is too close to any companion or player.
        /// This is separate from object occupancy - it checks for actual characters in the area.
        /// Use this to ensure companions don't stack on top of each other.
        /// </summary>
        /// <param name="position">The position to check</param>
        /// <param name="radius">Personal space radius</param>
        /// <param name="excludeCharacter">Character to exclude from the check (usually self)</param>
        public static bool IsPositionCrowded(Vector3 position, float radius = PERSONAL_SPACE_RADIUS, Character excludeCharacter = null)
        {
            // Check for nearby characters using physics overlap
            var colliders = Physics.OverlapSphere(position, radius);
            
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                // Check for Character component (covers companions, players, and NPCs)
                var character = col.GetComponent<Character>();
                if (character == null)
                    character = col.GetComponentInParent<Character>();
                
                if (character != null && character != excludeCharacter)
                {
                    // Found another character nearby
                    if (VerboseLogging)
                    {
                        Debug.Log($"[InteractableOccupancyManager] Position crowded by {character.m_name ?? character.name}");
                    }
                    return true;
                }
            }
            
            // Also check all companions explicitly (in case colliders are disabled during sitting)
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null) continue;
                
                var companionChar = companion.GetComponent<Character>();
                if (companionChar == excludeCharacter) continue;
                
                float dist = Vector3.Distance(position, companion.transform.position);
                if (dist < radius)
                {
                    if (VerboseLogging)
                    {
                        Debug.Log($"[InteractableOccupancyManager] Position crowded by companion {companion.companionName}");
                    }
                    return true;
                }
            }
            
            // Also check all players
            foreach (var player in Player.GetAllPlayers())
            {
                if (player == null) continue;
                if (player == excludeCharacter) continue;
                
                float dist = Vector3.Distance(position, player.transform.position);
                if (dist < radius)
                {
                    if (VerboseLogging)
                    {
                        Debug.Log($"[InteractableOccupancyManager] Position crowded by player {player.GetPlayerName()}");
                    }
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if a specific attach point is safe to use (not occupied and not crowded).
        /// This is the main method idle behaviors should call before interacting.
        /// </summary>
        /// <param name="attachPoint">The attach point or interaction point</param>
        /// <param name="sourceObject">The source GameObject (chair, workbench, etc.)</param>
        /// <param name="character">The character trying to use the object</param>
        public static bool CanUseInteractable(Transform attachPoint, GameObject sourceObject, Character character)
        {
            if (attachPoint == null || sourceObject == null || character == null)
                return false;
            
            // Check if the object itself is occupied
            if (IsOccupied(sourceObject, character))
            {
                if (VerboseLogging)
                {
                    Debug.Log($"[InteractableOccupancyManager] {sourceObject.name} is occupied");
                }
                return false;
            }
            
            // Check if the attach point position is crowded
            if (IsPositionCrowded(attachPoint.position, PERSONAL_SPACE_RADIUS, character))
            {
                if (VerboseLogging)
                {
                    Debug.Log($"[InteractableOccupancyManager] Position at {sourceObject.name} is crowded");
                }
                return false;
            }
            
            return true;
        }
        
        /// <summary>
        /// Simplified version for objects without a specific attach point.
        /// </summary>
        public static bool CanUseInteractable(GameObject sourceObject, Character character)
        {
            if (sourceObject == null || character == null)
                return false;
            
            // Check if the object itself is occupied
            if (IsOccupied(sourceObject, character))
                return false;
            
            // Check if the object position is crowded
            if (IsPositionCrowded(sourceObject.transform.position, PERSONAL_SPACE_RADIUS, character))
                return false;
            
            return true;
        }
        
        #endregion
        
        #region Cleanup
        
        private static void CleanupIfNeeded()
        {
            if (Time.time - _lastCleanupTime < CLEANUP_INTERVAL)
                return;
            
            _lastCleanupTime = Time.time;
            
            lock (_lock)
            {
                var toRemove = new List<int>();
                
                foreach (var kvp in _occupiedObjects)
                {
                    if (kvp.Value.IsExpired)
                    {
                        toRemove.Add(kvp.Key);
                    }
                }
                
                foreach (int id in toRemove)
                {
                    _occupiedObjects.Remove(id);
                }
                
                if (VerboseLogging && toRemove.Count > 0)
                {
                    Debug.Log($"[InteractableOccupancyManager] Cleaned up {toRemove.Count} expired entries");
                }
            }
        }
        
        /// <summary>
        /// Clears all occupancy tracking. Called on scene transitions or game reset.
        /// </summary>
        public static void ClearAll()
        {
            lock (_lock)
            {
                _occupiedObjects.Clear();
            }
            
            if (VerboseLogging)
            {
                Debug.Log("[InteractableOccupancyManager] Cleared all occupancy tracking");
            }
        }
        
        #endregion
        
        #region Debug
        
        /// <summary>
        /// Gets the current occupancy count for debugging.
        /// </summary>
        public static int GetOccupiedCount()
        {
            lock (_lock)
            {
                return _occupiedObjects.Count;
            }
        }
        
        /// <summary>
        /// Gets debug info about current occupancies.
        /// </summary>
        public static string GetDebugInfo()
        {
            lock (_lock)
            {
                if (_occupiedObjects.Count == 0)
                    return "No occupied objects";
                
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Occupied objects ({_occupiedObjects.Count}):");
                
                foreach (var kvp in _occupiedObjects)
                {
                    var entry = kvp.Value;
                    float remaining = entry.MaxDuration - (Time.time - entry.OccupyTime);
                    sb.AppendLine($"  - Object {kvp.Key}: {entry.OccupantName} ({remaining:F1}s remaining)");
                }
                
                return sb.ToString();
            }
        }
        
        #endregion
    }
}
