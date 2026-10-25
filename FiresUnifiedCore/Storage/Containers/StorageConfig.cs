using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.Storage
{
    /// <summary>
    /// The craft-from-containers keys, section "Storage", all advanced (Core's convention).
    /// Server-authoritative keys are synced; personal ones are not. Bound once from StorageFeatures.Bind.
    /// </summary>
    internal static class StorageConfig
    {
        internal static ConfigEntry<bool> CraftFromContainers;
        internal static ConfigEntry<float> Range;
        internal static ConfigEntry<string> ItemBlacklist;
        internal static ConfigEntry<bool> IgnoreShips, IgnoreCarts;
        // Sort / quick stack / restock
        internal static ConfigEntry<bool> AutoSort, LockHotbar;
        internal static ConfigEntry<InventorySort.Mode> SortMode;
        internal static ConfigEntry<float> QuickStackRange;
        internal static ConfigEntry<string> RestockItems;
        internal static ConfigEntry<KeyboardShortcut> SortKey, QuickStackKey, RestockKey;
        // Hopper chests
        internal static ConfigEntry<bool> Hoppers;
        internal static ConfigEntry<float> HopperMaxRadius, HopperSweepSeconds, HopperSortRadius;   // + sort radius
        internal static ConfigEntry<float> NetworkRadius;   // The storage network without a ward
        internal static ConfigEntry<bool> SignLabels;

        private static HashSet<string> s_blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string s_blacklistRaw;

        internal static void Bind(ConfigFile config, FiresCore.Sync.ConfigSync sync)
        {
            if (CraftFromContainers != null) return;
            CraftFromContainers = config.Bind("Storage", "Craft From Containers", true,
                StorageFeatures.Advanced("SERVER. With Storage Features on: crafting at a station uses the materials in containers within range of the station."));
            Range = config.Bind("Storage", "Range", 20f,
                StorageFeatures.Advanced("SERVER. Metres from the crafting station within which containers lend their materials.", new AcceptableValueRange<float>(2f, 60f)));
            ItemBlacklist = config.Bind("Storage", "Item Blacklist", "",
                StorageFeatures.Advanced("SERVER. Comma-separated item prefab names never taken from containers (e.g. Coins,DragonEgg)."));
            IgnoreShips = config.Bind("Storage", "Ignore Ship Containers", true,
                StorageFeatures.Advanced("SERVER. Ship holds never lend materials."));
            IgnoreCarts = config.Bind("Storage", "Ignore Cart Containers", true,
                StorageFeatures.Advanced("SERVER. Carts never lend materials."));
            sync?.AddConfigEntry(CraftFromContainers); sync?.AddConfigEntry(Range); sync?.AddConfigEntry(ItemBlacklist);
            sync?.AddConfigEntry(IgnoreShips); sync?.AddConfigEntry(IgnoreCarts);
            AutoSort = config.Bind("Storage", "Auto Sort", true,
                StorageFeatures.Advanced("SERVER. With Storage Features on: sorting, quick stack and restock (keys below, with the inventory open)."));
            QuickStackRange = config.Bind("Storage", "Quick Stack Range", 10f,
                StorageFeatures.Advanced("SERVER. Metres from the player within which quick stack and restock reach containers.", new AcceptableValueRange<float>(2f, 30f)));
            sync?.AddConfigEntry(AutoSort); sync?.AddConfigEntry(QuickStackRange);
            Hoppers = config.Bind("Storage", "Hopper Chests", false,
                StorageFeatures.Advanced("SERVER. With Storage Features on: the Hopper Chest on the Hammer pulls dropped items near it. Off: hoppers load as plain chests."));
            HopperMaxRadius = config.Bind("Storage", "Hopper Max Radius", 8f, StorageFeatures.Advanced("SERVER. Largest pull radius a hopper may be set to (metres).", new AcceptableValueRange<float>(1f, 12f)));
            HopperSweepSeconds = config.Bind("Storage", "Hopper Sweep Seconds", 5f, StorageFeatures.Advanced("SERVER. Seconds between a hopper's sweeps for items its events missed.", new AcceptableValueRange<float>(2f, 30f)));
            // The hopper auto-sorts into other containers within this radius.
            HopperSortRadius = config.Bind("Storage", "Hopper Sort Radius", 10f, StorageFeatures.Advanced("SERVER. Metres around a hopper within which it sorts what it pulled into other containers (by their tags, else by what they already hold).", new AcceptableValueRange<float>(2f, 20f)));
            // The network's reach when the anchor stands in no ward the player is permitted on
            NetworkRadius = config.Bind("Storage", "Storage Network Radius", 20f, StorageFeatures.Advanced("SERVER. Metres around a storage terminal (or you) that count as the base when it stands in no ward you are on; inside a ward the whole ward counts.", new AcceptableValueRange<float>(5f, 60f)));
            sync?.AddConfigEntry(Hoppers); sync?.AddConfigEntry(HopperMaxRadius); sync?.AddConfigEntry(HopperSweepSeconds);
            SignLabels = config.Bind("Storage", "Chest Sign Labels", true,
                StorageFeatures.Advanced("SERVER. With Storage Features on: a chest's tags print onto signs attached to it (never over a player's own text)."));
            sync?.AddConfigEntry(SignLabels);
            Hoppers.SettingChanged += (s, e) => { if (Hoppers.Value) HopperChest.EnsurePiece(); else HopperChest.RemovePiece(); };
            SortMode = config.Bind("Storage", "Sort Mode", InventorySort.Mode.Type, StorageFeatures.Advanced("How Sort orders items: Type, Name, Value or Weight."));
            LockHotbar = config.Bind("Storage", "Lock Hotbar", true, StorageFeatures.Advanced("Sort and quick stack never touch the hotbar row."));
            RestockItems = config.Bind("Storage", "Restock Items", "ArrowWood:50,ArrowFlint:50,ArrowBronze:50,ArrowIron:50",
                StorageFeatures.Advanced("Restock tops these up from nearby containers: PrefabName:target count, comma-separated."));
            SortKey = config.Bind("Storage", "Sort Key", new KeyboardShortcut(KeyCode.O),
                StorageFeatures.Advanced("With the inventory open: sort your bag (hold Shift: sort the open container)."));
            QuickStackKey = config.Bind("Storage", "Quick Stack Key", new KeyboardShortcut(KeyCode.P),
                StorageFeatures.Advanced("With the inventory open: put stackable items into nearby containers that already hold them."));
            RestockKey = config.Bind("Storage", "Restock Key", new KeyboardShortcut(KeyCode.U),
                StorageFeatures.Advanced("With the inventory open: top up the Restock Items from nearby containers."));
        }

        /// <summary>True when the item (by prefab name) may never be taken from containers.</summary>
        internal static bool Blacklisted(string prefabName)
        {
            string raw = ItemBlacklist?.Value ?? "";
            if (raw != s_blacklistRaw)
            {
                s_blacklistRaw = raw;
                s_blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string part in raw.Split(',')) { string p = part.Trim(); if (p.Length > 0) s_blacklist.Add(p); }
            }
            return prefabName != null && s_blacklist.Contains(prefabName);
        }
    }
}
