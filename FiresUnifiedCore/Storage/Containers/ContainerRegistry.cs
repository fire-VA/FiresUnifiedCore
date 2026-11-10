using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Storage
{
    /// <summary>
    /// Every loaded Container in a flat XZ grid (8 m cells), fed by Container.Awake /
    /// OnDestroyed (Patches.CraftPullPatches) and seeded once when Storage Features switches on mid-session. Range queries walk only the
    /// cells the circle touches - no FindObjectsOfType per query, no per-frame scan. Carts and ships are skipped at registration when
    /// their ignore keys are on (they move, so they would leave their cell anyway).
    /// Also the counts cache: per crafting centre, item name -> total in lendable containers, rebuilt when the centre moves more than 1 m,
    /// when any registered container's inventory changes (a version bump from Inventory.m_onChanged), or after 1 s.
    /// </summary>
    public static class ContainerRegistry
    {
        private const float Cell = 8f;
        private static readonly Dictionary<Vector2Int, List<Container>> s_grid = new Dictionary<Vector2Int, List<Container>>();
        internal static int GridCount { get { int n = 0; foreach (var l in s_grid.Values) n += l.Count; return n; } }
        private static readonly Dictionary<Container, Vector2Int> s_cellOf = new Dictionary<Container, Vector2Int>();
        private static int s_version;

        internal static readonly AccessTools.FieldRef<Container, ZNetView> NView = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
        internal static readonly AccessTools.FieldRef<Container, Inventory> Inv = AccessTools.FieldRefAccess<Container, Inventory>("m_inventory");
        internal static readonly System.Func<Container, long, bool> CheckAccessFn =
            AccessTools.MethodDelegate<System.Func<Container, long, bool>>(AccessTools.Method(typeof(Container), "CheckAccess"));

        public static int Count => s_cellOf.Count;

        internal static void Add(Container c)
        {
            if (c == null || s_cellOf.ContainsKey(c)) return;
            if (StorageConfig.IgnoreShips != null && StorageConfig.IgnoreShips.Value && c.GetComponentInParent<Ship>() != null) return;
            if (StorageConfig.IgnoreCarts != null && StorageConfig.IgnoreCarts.Value && c.GetComponentInParent<Vagon>() != null) return;
            Vector2Int cell = CellOf(c.transform.position);
            if (!s_grid.TryGetValue(cell, out var list)) s_grid[cell] = list = new List<Container>();
            list.Add(c);
            s_cellOf[c] = cell;
            Inventory inv = Inv(c);
            if (inv != null) inv.m_onChanged += Bump;
            s_version++;
        }

        internal static void Remove(Container c)
        {
            if (c == null || !s_cellOf.TryGetValue(c, out var cell)) return;
            s_cellOf.Remove(c);
            if (s_grid.TryGetValue(cell, out var list)) list.Remove(c);
            Inventory inv = Inv(c);
            if (inv != null) inv.m_onChanged -= Bump;
            s_version++;
        }

        /// <summary>Seeds the containers already loaded when the feature switches on (one FindObjectsOfType, once).</summary>
        internal static void Seed()
        {
            foreach (Container c in Object.FindObjectsOfType<Container>()) { Add(c); ContainerOps.RegisterOn(c); }
        }

        internal static void Clear()
        {
            foreach (var c in new List<Container>(s_cellOf.Keys)) Remove(c);
            s_grid.Clear();
            s_cache.Clear();
        }

        private static void Bump() => s_version++;

        private static Vector2Int CellOf(Vector3 p) => new Vector2Int(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.z / Cell));

        private static readonly List<Container> s_query = new List<Container>();
        /// <summary>Why the last Lendable() query dropped containers, for the station line.</summary>
        internal static int SkipRange, SkipInvalid, SkipAccess, SkipWard, SkipOwner;
        internal static int Registered { get { int n = 0; foreach (var l in s_grid.Values) n += l.Count; return n; } }

        /// <summary>The containers within <paramref name="range"/> of <paramref name="centre"/> that <paramref name="player"/> may take from:
        /// valid, not in someone's hand-carried cart/ship (by config), private access (vanilla CheckAccess), ward (PrivateArea.CheckAccess).
        /// Nearest first. The list is reused: copy it to keep it.</summary>
        internal static List<Container> Lendable(Vector3 centre, float range, Player player, string system = "storage automation")
        {   // System = who asks (auto-locked chests are skipped and logged); null = list them too (the storage terminal)
            s_query.Clear(); SkipRange = SkipInvalid = SkipAccess = SkipWard = SkipOwner = 0;
            if (player == null) return s_query;
            Vector2Int lo = CellOf(centre - new Vector3(range, 0, range)), hi = CellOf(centre + new Vector3(range, 0, range));
            float r2 = range * range;
            long pid = player.GetPlayerID();
            for (int x = lo.x; x <= hi.x; x++)
                for (int z = lo.y; z <= hi.y; z++)
                {
                    if (!s_grid.TryGetValue(new Vector2Int(x, z), out var list)) continue;
                    foreach (Container c in list)
                    {
                        if (c == null) continue;
                        if ((c.transform.position - centre).sqrMagnitude > r2) { SkipRange++; continue; }
                        ZNetView nv = NView(c);
                        if (nv == null || !nv.IsValid() || Inv(c) == null) { SkipInvalid++; continue; }
                        if (!CheckAccessFn(c, pid)) { SkipAccess++; continue; }
                        if (c.m_checkGuardStone && !PrivateArea.CheckAccess(c.transform.position, 0f, false, false)) { SkipWard++; continue; }
                        if (!ContainerOps.OwnerReachable(c)) { SkipOwner++; continue; }
                        if (system != null && ChestTags.SkipLocked(c, system)) continue;   // an owner that doesn't have it loaded never answers
                        s_query.Add(c);
                    }
                }
            s_query.Sort((a, b) => (a.transform.position - centre).sqrMagnitude.CompareTo((b.transform.position - centre).sqrMagnitude));
            return s_query;
        }

        // ---- counts cache ----
        private sealed class CountCache { public Vector3 Centre; public int Version; public float At; public readonly Dictionary<string, int> Counts = new Dictionary<string, int>(); }
        private static readonly Dictionary<long, CountCache> s_cache = new Dictionary<long, CountCache>();

        /// <summary>How many of the item (shared name, any quality the recipe accepts: quality 1 materials) the lendable containers hold.</summary>
        internal static int CountInContainers(string sharedName, string prefabName, Vector3 centre, float range, Player player)
        {
            if (player == null || string.IsNullOrEmpty(sharedName) || StorageConfig.Blacklisted(prefabName)) return 0;
            long pid = player.GetPlayerID();
            if (!s_cache.TryGetValue(pid, out var cache)) s_cache[pid] = cache = new CountCache { Version = -1 };
            if (cache.Version != s_version || (cache.Centre - centre).sqrMagnitude > 1f || Time.time - cache.At > 1f)
            {
                cache.Counts.Clear(); cache.Centre = centre; cache.Version = s_version; cache.At = Time.time;
            }
            if (cache.Counts.TryGetValue(sharedName, out int n)) return n;
            n = 0;
            foreach (Container c in Lendable(centre, range, player, "craft from containers")) n += Inv(c).CountItems(sharedName, -1, false);
            cache.Counts[sharedName] = n;
            return n;
        }
    }
}
