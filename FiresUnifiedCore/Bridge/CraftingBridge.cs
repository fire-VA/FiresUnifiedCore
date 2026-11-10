using System;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Item rarity/affix seam for FiresRPGCrafting (Tools\FIRESRPGCRAFTING_DESIGN.md section 5). The crafting mod assigns the
    /// delegates at its Awake; Core never references that mod and runs nothing crafting-specific. When unassigned,
    /// every member is a no-op: callers null-check or use the Try* helpers, so Core and the other mods work unchanged.
    /// </summary>
    // Added for FiresRPGCrafting (rarities first).
    public static class CraftingBridge
    {
        /// <summary>0-4 (Common..Legendary), -1 = not a crafting item.</summary>
        public static Func<ItemDrop.ItemData, int> GetRarity;

        /// <summary>The rolled display name ("Keen Bronze Sword of the Wolf"), or null for the vanilla name.</summary>
        public static Func<ItemDrop.ItemData, string> GetDisplayName;

        /// <summary>(wearer, stat id) the wearer's summed bonus, for UIs. 0 when the stat is unknown.</summary>
        public static Func<Character, string, float> GetStat;

        /// <summary>(item, min rarity, luck multiplier): rolls rarity + affixes onto a freshly made item (FMO/FAP/RPGmaker loot).</summary>
        public static Action<ItemDrop.ItemData, int, float> RollItem;

        /// <summary>Raised by the crafting mod after a roll (achievements/leaderboards).</summary>
        public static event Action<Character, ItemDrop.ItemData> ItemRolled;

        public static bool IsAvailable => GetRarity != null;

        public static int TryGetRarity(ItemDrop.ItemData item) => item != null && GetRarity != null ? GetRarity(item) : -1;

        public static string TryGetDisplayName(ItemDrop.ItemData item) => item != null && GetDisplayName != null ? GetDisplayName(item) : null;

        public static float TryGetStat(Character wearer, string statId) => wearer != null && GetStat != null ? GetStat(wearer, statId) : 0f;

        public static bool TryRollItem(ItemDrop.ItemData item, int minRarity, float luck)
        {
            if (item == null || RollItem == null) return false;
            RollItem(item, minRarity, luck);
            return true;
        }

        /// <summary>For the crafting mod: an event can only be raised by its declaring class.</summary>
        public static void RaiseItemRolled(Character roller, ItemDrop.ItemData item)
        {
            var handler = ItemRolled;
            if (handler == null || item == null) return;
            foreach (Action<Character, ItemDrop.ItemData> listener in handler.GetInvocationList())
            {
                try { listener(roller, item); }
                catch (Exception ex) { FiresCore.FiresUnifiedCore.Log?.LogDebug($"[CraftingBridge] ItemRolled listener threw: {ex.Message}"); }
            }
        }
    }
}
