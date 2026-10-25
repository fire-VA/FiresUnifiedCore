using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Storage
{
    /// <summary>
    /// Quick stack and restock, through the owner-applied ops.
    ///   quick stack  each stackable item in the player's bag that isn't locked (hotbar / favourite) or equipped goes to the nearest
    ///                containers within Quick Stack Range that ALREADY hold that item (chest tags join in at stage 4). The stack is taken
    ///                out of the bag first, put with ContainerOps.Put, and whatever the chests didn't take goes straight back.
    ///   restock      for each "Prefab:target" in Restock Items, the bag is topped up to the target from those containers (ContainerOps.Take).
    /// Containers someone else has open are skipped by quick stack (QuickStackStore's rule: nobody's open grid reshuffles under them).
    /// One run at a time; a HUD line sums it up ("Stored 34 items in 3 containers").
    /// </summary>
    public static class QuickStack
    {
        private const float DefaultRangeMeters = 10f;
        private static bool s_running;
        public static bool Running => s_running;

        internal static IEnumerator Stack(Player me)
        {
            if (s_running || me == null) yield break;
            s_running = true;
            int stored = 0;
            var used = new HashSet<Container>();
            try
            {
                float range = StorageConfig.QuickStackRange?.Value ?? DefaultRangeMeters;
                var chests = new List<Container>(ContainerRegistry.Lendable(me.transform.position, range, me, "quick stack")).Where(c => !c.IsInUse()).ToList();
                Inventory bag = me.GetInventory();
                var candidates = bag.GetAllItems().Where(i => i.m_shared.m_maxStackSize > 1 && !me.IsItemEquiped(i)
                    && !InventorySort.Locked(me, bag, i.m_gridPos) && i.m_dropPrefab != null).ToList();
                foreach (ItemDrop.ItemData item in candidates)
                {
                    string shared = item.m_shared.m_name, prefab = item.m_dropPrefab.name;
                    int quality = item.m_quality;
                    // A chest whose tags REFUSE the item is skipped; one that WANTS it is a target even if it
                    // doesn't hold any yet (sort-into); an untagged chest is a target only if it already holds the item. Best tag score first.
                    var targets = new List<(Container c, int score)>();
                    foreach (Container c in chests)
                    {
                        bool? want = ChestTags.Match(c, item, out int score);
                        if (want == false) continue;
                        bool holds = (ContainerRegistry.Inv(c)?.CountItems(shared, quality, false) ?? 0) > 0;
                        if (want == true || holds) targets.Add((c, want == true ? score + (holds ? 1 : 0) : 0));
                    }
                    var ordered = targets.OrderByDescending(t => t.score).Select(t => t.c).ToList();
                    if (ordered.Count == 0 || item.m_stack <= 0) continue;
                    int left = item.m_stack;
                    bag.RemoveItem(item, left);   // the whole stack out of the bag first; whatever no chest takes comes back at the end
                    foreach (Container c in ordered)
                    {
                        if (left <= 0) break;
                        int got = 0;
                        yield return ContainerOps.Put(c, me, prefab, quality, left, g => got = g);
                        if (got > 0) { stored += got; used.Add(c); left -= got; }
                    }
                    if (left > 0) bag.AddItem(prefab, left, quality, 0, 0L, "", false, false);
                }
            }
            finally { s_running = false; }
            string msg = stored > 0 ? $"Stored {stored} item(s) in {used.Count} container(s)" : "Nothing to stack nearby";
            me.Message(MessageHud.MessageType.TopLeft, msg);
            FiresLogger.LogDebug($"[Storage] quick stack: {msg}");
        }

        internal static IEnumerator Restock(Player me)
        {
            if (s_running || me == null || ObjectDB.instance == null) yield break;
            s_running = true;
            int pulled = 0;
            try
            {
                float range = StorageConfig.QuickStackRange?.Value ?? DefaultRangeMeters;
                foreach (string entry in (StorageConfig.RestockItems?.Value ?? "").Split(','))
                {
                    string[] kv = entry.Split(':');
                    if (kv.Length != 2 || !int.TryParse(kv[1].Trim(), out int target)) continue;
                    GameObject prefab = ObjectDB.instance.GetItemPrefab(kv[0].Trim());
                    if (prefab == null || StorageConfig.Blacklisted(prefab.name)) continue;
                    string shared = prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_name;
                    int have = me.GetInventory().CountItems(shared, -1, false);
                    if (have <= 0 || have >= target) continue;   // restock only what the player already carries
                    foreach (Container c in new List<Container>(ContainerRegistry.Lendable(me.transform.position, range, me, "quick stack")))
                    {
                        int need = target - me.GetInventory().CountItems(shared, -1, false);
                        if (need <= 0) break;
                        int held = ContainerRegistry.Inv(c)?.CountItems(shared, -1, false) ?? 0;
                        if (held <= 0) continue;
                        int got = 0;
                        yield return ContainerOps.Take(c, me, shared, prefab.name, Mathf.Min(need, held), g => got = g);
                        pulled += got;
                    }
                }
            }
            finally { s_running = false; }
            me.Message(MessageHud.MessageType.TopLeft, pulled > 0 ? $"Restocked {pulled} item(s)" : "Nothing to restock nearby");
        }
    }
}
