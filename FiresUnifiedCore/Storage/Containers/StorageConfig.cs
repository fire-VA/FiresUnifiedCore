using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.Storage
{
    /// <summary>
    /// The storage keys, section "Storage". 0.2.1042 (Fire 10-30: "the code can stay in the core ... But the config settings go to storage
    /// and nothing runs if storage is not installed"): FiresStorageNetwork binds, syncs and Provides them (CoreSettings); Core no longer binds
    /// them. Each setting here reads FSN's value, else the default Core shipped (only reached while FSN is absent, when nothing runs).
    /// </summary>
    internal static class StorageConfig
    {
        /// <summary>One storage setting: FSN's value through CoreSettings, else the default (same name + .Value as the old ConfigEntry, so the
        /// read sites are unchanged).</summary>
        internal sealed class Setting<T>
        {
            private readonly string m_key;
            private readonly T m_default;
            internal Setting(string key, T def) { m_key = key; m_default = def; }
            internal string Key => m_key;
            internal T Value => FiresCore.Config.CoreSettings.Get("Storage", m_key, m_default);
        }

        internal static readonly Setting<bool> CraftFromContainers = new Setting<bool>("Craft From Containers", true);
        internal static readonly Setting<float> Range = new Setting<float>("Range", 20f);
        internal static readonly Setting<string> ItemBlacklist = new Setting<string>("Item Blacklist", "");
        internal static readonly Setting<bool> IgnoreShips = new Setting<bool>("Ignore Ship Containers", true);
        internal static readonly Setting<bool> IgnoreCarts = new Setting<bool>("Ignore Cart Containers", true);
        // Sort / quick stack / restock
        internal static readonly Setting<bool> AutoSort = new Setting<bool>("Auto Sort", true);
        internal static readonly Setting<bool> LockHotbar = new Setting<bool>("Lock Hotbar", true);
        internal static readonly Setting<InventorySort.Mode> SortMode = new Setting<InventorySort.Mode>("Sort Mode", InventorySort.Mode.Type);
        internal static readonly Setting<float> QuickStackRange = new Setting<float>("Quick Stack Range", 10f);
        internal static readonly Setting<string> RestockItems = new Setting<string>("Restock Items", "ArrowWood:50,ArrowFlint:50,ArrowBronze:50,ArrowIron:50");
        internal static readonly Setting<KeyboardShortcut> SortKey = new Setting<KeyboardShortcut>("Sort Key", new KeyboardShortcut(KeyCode.O));
        internal static readonly Setting<KeyboardShortcut> QuickStackKey = new Setting<KeyboardShortcut>("Quick Stack Key", new KeyboardShortcut(KeyCode.P));
        internal static readonly Setting<KeyboardShortcut> RestockKey = new Setting<KeyboardShortcut>("Restock Key", new KeyboardShortcut(KeyCode.U));
        // Hopper chests
        internal static readonly Setting<bool> Hoppers = new Setting<bool>("Hopper Chests", false);
        internal static readonly Setting<float> HopperMaxRadius = new Setting<float>("Hopper Max Radius", 8f);
        internal static readonly Setting<float> HopperSweepSeconds = new Setting<float>("Hopper Sweep Seconds", 5f);
        internal static readonly Setting<float> HopperSortRadius = new Setting<float>("Hopper Sort Radius", 10f);
        internal static readonly Setting<float> NetworkRadius = new Setting<float>("Storage Network Radius", 20f);   // The storage network without a ward
        internal static readonly Setting<bool> SignLabels = new Setting<bool>("Chest Sign Labels", true);

        private static HashSet<string> s_blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string s_blacklistRaw;

        /// <summary>True when the item (by prefab name) may never be taken from containers.</summary>
        internal static bool Blacklisted(string prefabName)
        {
            string raw = ItemBlacklist.Value ?? "";
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
