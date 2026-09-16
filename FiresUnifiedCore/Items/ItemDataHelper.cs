using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Items
{
    /// <summary>
    /// The single place companion systems clone, validate and persist ItemData, so every per-instance field
    /// (stack, durability, quality, variant, crafter, custom data, world level, picked-up) survives transfers
    /// and save/load.
    /// </summary>
    public static class ItemDataHelper
    {
        #region Item Cloning
        
        /// <summary>
        /// Creates a complete clone of an item, preserving ALL data.
        /// This is the SAFE way to clone items for transfers.
        /// 
        /// Valheim's built-in Clone() should handle this, but we verify critical fields.
        /// </summary>
        /// <param name="source">The item to clone</param>
        /// <param name="newStackSize">Optional: Set a different stack size (for partial transfers)</param>
        /// <returns>A complete clone with all data preserved</returns>
        public static ItemDrop.ItemData CloneItem(ItemDrop.ItemData source, int? newStackSize = null)
        {
            if (source == null) return null;
            
            // Use Valheim's built-in Clone which copies all fields
            var clone = source.Clone();
            
            // CRITICAL: Verify the clone has all essential data
            // Sometimes Clone() can fail to copy certain fields depending on game version
            
            // Ensure prefab reference is preserved
            if (clone.m_dropPrefab == null && source.m_dropPrefab != null)
            {
                clone.m_dropPrefab = source.m_dropPrefab;
                Debug.LogWarning("[ItemDataHelper] Clone() lost m_dropPrefab, manually restored");
            }
            
            // Ensure shared data reference is valid
            if (clone.m_shared == null && source.m_shared != null)
            {
                // This shouldn't happen, but if it does, try to restore from prefab
                if (clone.m_dropPrefab != null)
                {
                    var itemDrop = clone.m_dropPrefab.GetComponent<ItemDrop>();
                    if (itemDrop?.m_itemData?.m_shared != null)
                    {
                        clone.m_shared = itemDrop.m_itemData.m_shared;
                        Debug.LogWarning("[ItemDataHelper] Clone() lost m_shared, manually restored from prefab");
                    }
                }
            }
            
            // Ensure custom data is copied (for mod compatibility)
            if (source.m_customData != null && source.m_customData.Count > 0)
            {
                if (clone.m_customData == null)
                {
                    clone.m_customData = new Dictionary<string, string>();
                }
                
                foreach (var kvp in source.m_customData)
                {
                    if (!clone.m_customData.ContainsKey(kvp.Key))
                    {
                        clone.m_customData[kvp.Key] = kvp.Value;
                    }
                }
            }
            
            // Apply new stack size if specified (for partial transfers)
            if (newStackSize.HasValue)
            {
                clone.m_stack = Mathf.Clamp(newStackSize.Value, 1, clone.m_shared?.m_maxStackSize ?? 999);
            }
            
            return clone;
        }
        
        /// <summary>
        /// Creates a clone for a partial stack transfer.
        /// Ensures the clone has the correct stack size while source keeps the remainder.
        /// </summary>
        /// <param name="source">Source item (will NOT be modified)</param>
        /// <param name="amount">Amount to take for the clone</param>
        /// <returns>Clone with specified stack size</returns>
        public static ItemDrop.ItemData CloneForPartialTransfer(ItemDrop.ItemData source, int amount)
        {
            if (source == null) return null;
            if (amount <= 0) return null;
            
            // Clamp to available amount
            int actualAmount = Mathf.Min(amount, source.m_stack);
            
            return CloneItem(source, actualAmount);
        }
        
        #endregion
        
        #region Item Validation
        
        /// <summary>
        /// Result of item validation with details about any issues.
        /// </summary>
        public class ValidationResult
        {
            public bool IsValid;
            public List<string> Issues = new List<string>();
            public bool HasCriticalIssues;
            
            public void AddIssue(string issue, bool critical = false)
            {
                Issues.Add(issue);
                if (critical) HasCriticalIssues = true;
            }
        }
        
        /// <summary>
        /// Validates that an item has all required data for proper handling.
        /// Use this before transfers to catch issues early.
        /// </summary>
        public static ValidationResult ValidateItem(ItemDrop.ItemData item)
        {
            var result = new ValidationResult { IsValid = true };
            
            if (item == null)
            {
                result.IsValid = false;
                result.AddIssue("Item is null", critical: true);
                return result;
            }
            
            // Check prefab reference
            if (item.m_dropPrefab == null)
            {
                result.IsValid = false;
                result.AddIssue("m_dropPrefab is null - item cannot be saved/restored properly", critical: true);
            }
            
            // Check shared data
            if (item.m_shared == null)
            {
                result.IsValid = false;
                result.AddIssue("m_shared is null - item has no base data", critical: true);
            }
            else
            {
                // Validate shared data integrity
                if (string.IsNullOrEmpty(item.m_shared.m_name))
                {
                    result.AddIssue("m_shared.m_name is empty");
                }
                
                if (item.m_shared.m_maxStackSize <= 0)
                {
                    result.AddIssue("m_shared.m_maxStackSize is invalid");
                }
            }
            
            // Check stack
            if (item.m_stack <= 0)
            {
                result.AddIssue("m_stack is zero or negative", critical: true);
            }
            else if (item.m_shared != null && item.m_stack > item.m_shared.m_maxStackSize)
            {
                result.AddIssue($"m_stack ({item.m_stack}) exceeds max ({item.m_shared.m_maxStackSize})");
            }
            
            // Check quality
            if (item.m_quality < 1)
            {
                result.AddIssue("m_quality is less than 1");
            }
            
            // Check variant (for icon display)
            if (item.m_shared?.m_icons != null && item.m_shared.m_icons.Length > 0)
            {
                if (item.m_variant < 0 || item.m_variant >= item.m_shared.m_icons.Length)
                {
                    result.AddIssue($"m_variant ({item.m_variant}) is out of range for icons array (length {item.m_shared.m_icons.Length})");
                }
            }
            
            // Check durability
            if (item.m_shared?.m_useDurability == true)
            {
                if (item.m_durability < 0)
                {
                    result.AddIssue("m_durability is negative");
                }
            }
            
            return result;
        }
        
        /// <summary>
        /// Attempts to fix common issues with item data.
        /// Returns true if fixes were applied.
        /// </summary>
        public static bool TryFixItemData(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            
            bool fixedAnything = false;
            
            // Try to restore prefab reference from ObjectDB
            if (item.m_dropPrefab == null && item.m_shared != null)
            {
                var prefab = TryFindPrefabBySharedName(item.m_shared.m_name);
                if (prefab != null)
                {
                    item.m_dropPrefab = prefab;
                    fixedAnything = true;
                    Debug.Log($"[ItemDataHelper] Restored m_dropPrefab for {item.m_shared.m_name}");
                }
            }
            
            // Fix invalid stack
            if (item.m_stack <= 0)
            {
                item.m_stack = 1;
                fixedAnything = true;
            }
            
            // Fix stack overflow
            if (item.m_shared != null && item.m_stack > item.m_shared.m_maxStackSize)
            {
                item.m_stack = item.m_shared.m_maxStackSize;
                fixedAnything = true;
            }
            
            // Fix quality
            if (item.m_quality < 1)
            {
                item.m_quality = 1;
                fixedAnything = true;
            }
            
            // Fix variant
            if (item.m_shared?.m_icons != null && item.m_shared.m_icons.Length > 0)
            {
                if (item.m_variant < 0 || item.m_variant >= item.m_shared.m_icons.Length)
                {
                    item.m_variant = 0;
                    fixedAnything = true;
                }
            }
            
            // Ensure custom data dictionary exists
            if (item.m_customData == null)
            {
                item.m_customData = new Dictionary<string, string>();
                fixedAnything = true;
            }
            
            return fixedAnything;
        }
        
        /// <summary>
        /// Tries to find an item prefab by its shared name.
        /// </summary>
        private static GameObject TryFindPrefabBySharedName(string sharedName)
        {
            if (string.IsNullOrEmpty(sharedName)) return null;
            if (ObjectDB.instance == null) return null;
            
            foreach (var prefab in ObjectDB.instance.m_items)
            {
                if (prefab == null) continue;
                var itemDrop = prefab.GetComponent<ItemDrop>();
                if (itemDrop?.m_itemData?.m_shared?.m_name == sharedName)
                {
                    return prefab;
                }
            }
            
            return null;
        }
        
        #endregion
        
        #region Item Info
        
        /// <summary>
        /// Gets the prefab name for an item, handling various edge cases.
        /// </summary>
        public static string GetPrefabName(ItemDrop.ItemData item)
        {
            if (item == null) return null;
            
            // First try: direct prefab reference
            if (item.m_dropPrefab != null)
            {
                string name = item.m_dropPrefab.name;
                if (name.EndsWith("(Clone)"))
                    name = name.Substring(0, name.Length - 7).Trim();
                return name;
            }
            
            // Second try: look up by shared name
            if (item.m_shared?.m_name != null && ObjectDB.instance != null)
            {
                var prefab = TryFindPrefabBySharedName(item.m_shared.m_name);
                if (prefab != null)
                    return prefab.name;
            }
            
            return null;
        }
        
        /// <summary>
        /// Gets the localized display name for an item.
        /// </summary>
        public static string GetDisplayName(ItemDrop.ItemData item)
        {
            if (item?.m_shared?.m_name == null) return "Unknown";
            
            string name = item.m_shared.m_name;
            if (name.StartsWith("$") && Localization.instance != null)
            {
                string localized = Localization.instance.Localize(name);
                if (!string.IsNullOrEmpty(localized) && localized != name)
                    return localized;
            }
            
            return name;
        }
        
        /// <summary>
        /// Gets a debug string with all item data for logging.
        /// </summary>
        public static string GetDebugString(ItemDrop.ItemData item)
        {
            if (item == null) return "null";
            
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Item Debug Info:");
            sb.AppendLine($"  Prefab: {item.m_dropPrefab?.name ?? "NULL"}");
            sb.AppendLine($"  Name: {item.m_shared?.m_name ?? "NULL"}");
            sb.AppendLine($"  Stack: {item.m_stack}/{item.m_shared?.m_maxStackSize ?? 0}");
            sb.AppendLine($"  Quality: {item.m_quality}");
            sb.AppendLine($"  Durability: {item.m_durability:F1}/{item.GetMaxDurability():F1}");
            sb.AppendLine($"  Variant: {item.m_variant}");
            sb.AppendLine($"  WorldLevel: {item.m_worldLevel}");
            sb.AppendLine($"  Equipped: {item.m_equipped}");
            sb.AppendLine($"  PickedUp: {item.m_pickedUp}");
            sb.AppendLine($"  GridPos: {item.m_gridPos}");
            sb.AppendLine($"  CrafterID: {item.m_crafterID}");
            sb.AppendLine($"  CrafterName: {item.m_crafterName ?? ""}");
            sb.AppendLine($"  CustomData: {item.m_customData?.Count ?? 0} entries");
            
            if (item.m_customData != null && item.m_customData.Count > 0)
            {
                foreach (var kvp in item.m_customData)
                {
                    sb.AppendLine($"    [{kvp.Key}]: {kvp.Value}");
                }
            }
            
            return sb.ToString();
        }
        
        #endregion
        
        #region Item Comparison
        
        /// <summary>
        /// Checks if two items are the same type and can stack together.
        /// Items must have matching:
        /// - Prefab name (or shared name)
        /// - Quality (for items with quality > 1)
        /// - World level
        /// </summary>
        public static bool CanStack(ItemDrop.ItemData a, ItemDrop.ItemData b)
        {
            if (a == null || b == null) return false;
            if (a.m_shared == null || b.m_shared == null) return false;
            
            // Must be same item type
            if (a.m_shared.m_name != b.m_shared.m_name) return false;
            
            // Must have same world level
            if (a.m_worldLevel != b.m_worldLevel) return false;
            
            // Quality items must match quality
            if (a.m_shared.m_maxQuality > 1 && a.m_quality != b.m_quality) return false;
            
            // Must be stackable
            if (a.m_shared.m_maxStackSize <= 1) return false;
            
            return true;
        }
        
        /// <summary>
        /// Checks if an item matches a prefab name.
        /// </summary>
        public static bool MatchesPrefab(ItemDrop.ItemData item, string prefabName)
        {
            if (item == null || string.IsNullOrEmpty(prefabName)) return false;
            
            string itemPrefab = GetPrefabName(item);
            if (itemPrefab == null) return false;
            
            return itemPrefab.Equals(prefabName, StringComparison.OrdinalIgnoreCase);
        }
        
        #endregion
    }
}
