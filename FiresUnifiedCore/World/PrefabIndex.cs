using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>
    /// Targets from the world's own records (Fire 2026-09-29: "the server and world know the exact position of every single prefab
    /// and its center point"). <see cref="Collect"/> / <see cref="Nearest"/> read the ZDOs this peer knows in the 64 m sectors round a
    /// point: prefab and position, loaded into the scene or not, with no physics query. <see cref="HitPoint"/> gives the point on an
    /// instance's own solid colliders to walk to and swing at (the trunk, not the canopy's bounds). One brain: the FDT bot's verbs
    /// and the companions' pickers ask the same thing.
    /// </summary>
    public static class PrefabIndex
    {
        /// <summary>One known object: its record, where it is, how far from the asking point (flat metres).</summary>
        public struct Known
        {
            public ZDO Zdo;
            public int Prefab;
            public Vector3 Position;
            public float Distance;
        }

        private const float SectorSize = 64f;
        private static readonly List<ZDO> s_sector = new List<ZDO>();
        private static readonly List<Known> s_found = new List<Known>();

        /// <summary>Stable hashes of prefab names, for the prefab set arguments.</summary>
        public static HashSet<int> Hashes(IEnumerable<string> prefabNames)
        {
            var set = new HashSet<int>();
            if (prefabNames != null)
                foreach (string name in prefabNames)
                    if (!string.IsNullOrEmpty(name)) set.Add(name.GetStableHashCode());
            return set;
        }

        /// <summary>
        /// Every known object whose prefab is in <paramref name="prefabs"/> (null = any) within <paramref name="radius"/> m of
        /// <paramref name="at"/>, nearest first, at most <paramref name="max"/>. The list is reused: copy it to keep it.
        /// </summary>
        public static IReadOnlyList<Known> Collect(Vector3 at, float radius, ICollection<int> prefabs, int max = 32)
        {
            s_found.Clear();
            ZDOMan zdos = ZDOMan.instance;
            if (zdos == null || ZoneSystem.instance == null) return s_found;
            int rings = Mathf.Max(0, Mathf.CeilToInt(radius / SectorSize));
            s_sector.Clear();
            zdos.FindSectorObjects(ZoneSystem.GetZone(at), new SimulationDistance(rings, 0, classic: true), s_sector);
            float r2 = radius * radius;
            foreach (ZDO zdo in s_sector)
            {
                if (zdo == null || !zdo.IsValid()) continue;
                int prefab = zdo.GetPrefab();
                if (prefabs != null && !prefabs.Contains(prefab)) continue;
                Vector3 p = zdo.GetPosition();
                float dx = p.x - at.x, dz = p.z - at.z;
                float d2 = dx * dx + dz * dz;
                if (d2 > r2) continue;
                s_found.Add(new Known { Zdo = zdo, Prefab = prefab, Position = p, Distance = Mathf.Sqrt(d2) });
            }
            s_found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            if (s_found.Count > max) s_found.RemoveRange(max, s_found.Count - max);
            return s_found;
        }

        /// <summary>The nearest known object with a prefab in <paramref name="prefabs"/> within <paramref name="radius"/> m.</summary>
        public static bool Nearest(Vector3 at, float radius, ICollection<int> prefabs, out Known found)
        {
            var list = Collect(at, radius, prefabs, 1);
            found = list.Count > 0 ? list[0] : default;
            return list.Count > 0;
        }

        /// <summary>The scene instance of a known object, or null while it isn't loaded.</summary>
        public static GameObject Instance(ZDO zdo) =>
            zdo != null && ZNetScene.instance != null ? ZNetScene.instance.FindInstance(zdo.m_uid) : null;

        /// <summary>
        /// Where to STAND to work on <paramref name="target"/> from <paramref name="from"/>'s side: its <see cref="HitPoint"/>
        /// snapped to the Humanoid navmesh within <paramref name="reach"/> (vanilla carves the navmesh round trunks and rocks, so a
        /// walk to the pivot never arrives: R67 "couldn't reach the tree in 23 s (8.5 m away)"). <paramref name="hit"/> is the point
        /// to measure reach against and swing at; <paramref name="method"/> says how both were found ("… , navmesh" or "… , no navmesh").
        /// </summary>
        public static Vector3 ApproachPoint(GameObject target, Vector3 from, float reach, out Vector3 hit, out string method)
        {
            hit = HitPoint(target, from, out method);
            if (Pathfinding.instance != null
                && Pathfinding.instance.FindValidPoint(out Vector3 stand, hit, reach, Pathfinding.AgentType.Humanoid))
            {
                method += ", navmesh";
                return stand;
            }
            method += ", no navmesh";
            return hit;
        }

        /// <summary>
        /// The point on <paramref name="target"/>'s own solid colliders nearest <paramref name="from"/>, and how it was found
        /// (for the log): "closest point" on a primitive or convex collider; "bounds" on a concave mesh, whose ClosestPoint gives the
        /// query point back (R65: the bot "arrived" at once and swung at air for 420 s); "trunk" for a tree (its pivot, at the
        /// trunk's foot, at <paramref name="from"/>'s height band); "pivot" when it has no solid collider.
        /// </summary>
        public static Vector3 HitPoint(GameObject target, Vector3 from, out string method)
        {
            method = "pivot";
            if (target == null) return from;
            Vector3 pivot = target.transform.position;
            if (target.GetComponentInParent<TreeBase>() != null)
            {
                method = "trunk";
                return new Vector3(pivot.x, Mathf.Clamp(from.y, pivot.y, pivot.y + 2f), pivot.z);
            }
            Vector3 best = pivot;
            float bestD = float.MaxValue;
            foreach (Collider c in target.GetComponentsInChildren<Collider>())
            {
                if (c == null || !c.enabled || c.isTrigger) continue;
                bool exact = !(c is MeshCollider mesh) || mesh.convex;
                Vector3 p = exact ? c.ClosestPoint(from) : c.bounds.ClosestPoint(from);
                float d = (p - from).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = p;
                    method = exact ? "closest point" : "bounds";
                }
            }
            return best;
        }
    }
}
