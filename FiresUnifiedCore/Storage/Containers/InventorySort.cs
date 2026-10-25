using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Storage
{
    /// <summary>
    /// Sorting an inventory THIS peer owns - the player's own bag, or the container the player
    /// has open (vanilla's open hands its ownership to the opener) - so a sort is a plain local edit, never a remote one.
    ///   locked   never moved: the hotbar row (Lock Hotbar) and favourite slots (Alt+click, per character in Player.m_customData
    ///            "core_fav_slots" as "x,y;x,y") - player bag only
    ///   merge    stackable items of the same name/quality/variant fill each other up to the max stack first (no half stacks left behind)
    ///   order    Sort Mode: Type (item type, then name), Name, Value (high first), Weight (heavy first); then quality high first
    ///   place    row by row into the free unlocked slots, so nothing leaves gaps between same items (QuickStackStore #15)
    /// </summary>
    public static class InventorySort
    {
        public enum Mode { Type, Name, Value, Weight }
        internal const string FavKey = "core_fav_slots";
        private static readonly AccessTools.FieldRef<Inventory, List<ItemDrop.ItemData>> Items = AccessTools.FieldRefAccess<Inventory, List<ItemDrop.ItemData>>("m_inventory");
        private static readonly Action<Inventory, bool, bool> ChangedFn =
            AccessTools.MethodDelegate<Action<Inventory, bool, bool>>(AccessTools.Method(typeof(Inventory), "Changed"));

        internal static HashSet<Vector2i> Favourites(Player p)
        {
            var set = new HashSet<Vector2i>();
            if (p == null || !p.m_customData.TryGetValue(FavKey, out string raw) || string.IsNullOrEmpty(raw)) return set;
            foreach (string e in raw.Split(';'))
            {
                string[] xy = e.Split(',');
                if (xy.Length == 2 && int.TryParse(xy[0], out int x) && int.TryParse(xy[1], out int y)) set.Add(new Vector2i(x, y));
            }
            return set;
        }

        internal static bool ToggleFavourite(Player p, Vector2i pos)
        {
            var set = Favourites(p);
            bool now = !set.Remove(pos);
            if (now) set.Add(pos);
            p.m_customData[FavKey] = string.Join(";", set.Select(v => v.x + "," + v.y).ToArray());
            return now;
        }

        internal static bool Locked(Player owner, Inventory inv, Vector2i pos)
        {
            if (owner == null || inv != owner.GetInventory()) return false;   // only the player's own bag has locks
            if (StorageConfig.LockHotbar != null && StorageConfig.LockHotbar.Value && pos.y == 0) return true;
            return Favourites(owner).Contains(pos);
        }

        /// <summary>Sorts <paramref name="inv"/> in place. <paramref name="owner"/>: the player whose bag it is (locks), or null for a chest.</summary>
        internal static int Sort(Inventory inv, Player owner, Mode mode)
        {
            List<ItemDrop.ItemData> all = Items(inv);
            var movable = all.Where(i => !Locked(owner, inv, i.m_gridPos)).ToList();
            // merge stackables
            foreach (var group in movable.Where(i => i.m_shared.m_maxStackSize > 1).GroupBy(i => (i.m_shared.m_name, i.m_quality, i.m_variant)).ToList())
            {
                var list = group.OrderByDescending(i => i.m_stack).ToList();
                int max = list[0].m_shared.m_maxStackSize, total = list.Sum(i => i.m_stack);
                foreach (var it in list)
                {
                    int put = Mathf.Min(max, total);
                    it.m_stack = put; total -= put;
                }
                foreach (var empty in list.Where(i => i.m_stack <= 0).ToList()) { all.Remove(empty); movable.Remove(empty); }
            }
            IOrderedEnumerable<ItemDrop.ItemData> ordered;
            switch (mode)
            {
                case Mode.Name: ordered = movable.OrderBy(i => Localization.instance.Localize(i.m_shared.m_name)); break;
                case Mode.Value: ordered = movable.OrderByDescending(i => i.m_shared.m_value).ThenBy(i => Localization.instance.Localize(i.m_shared.m_name)); break;
                case Mode.Weight: ordered = movable.OrderByDescending(i => i.m_shared.m_weight).ThenBy(i => Localization.instance.Localize(i.m_shared.m_name)); break;
                default: ordered = movable.OrderBy(i => (int)i.m_shared.m_itemType).ThenBy(i => Localization.instance.Localize(i.m_shared.m_name)); break;
            }
            var queue = new Queue<ItemDrop.ItemData>(ordered.ThenByDescending(i => i.m_quality).ThenByDescending(i => i.m_stack));
            int w = inv.GetWidth(), h = inv.GetHeight(), placed = 0;
            for (int y = 0; y < h && queue.Count > 0; y++)
                for (int x = 0; x < w && queue.Count > 0; x++)
                {
                    var pos = new Vector2i(x, y);
                    if (Locked(owner, inv, pos)) continue;
                    queue.Dequeue().m_gridPos = pos;
                    placed++;
                }
            ChangedFn(inv, true, false);
            return placed;
        }
    }
}
