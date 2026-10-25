using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// Burning fires melt snow locally (no network: every peer sees the same fire pieces and their fuel). The list of burning fires is
    /// refreshed on the snow step (<see cref="Refresh"/>, every 30 s of world time or on demand), never per frame.
    /// </summary>
    internal static class SnowFires
    {
        private struct Fire { public Vector3 Pos; public float Radius; }
        private static readonly List<Fire> s_fires = new List<Fire>();

        /// <summary>Re-reads the burning fires. Returns the zones whose melt changed (fires lit, went out, or moved).</summary>
        internal static HashSet<Vector2s> Refresh()
        {
            var before = new HashSet<Vector2s>();
            foreach (var f in s_fires) before.Add(ZoneSystem.GetZone(f.Pos));
            var old = new List<Fire>(s_fires);
            s_fires.Clear();
            foreach (var fp in Object.FindObjectsByType<Fireplace>(FindObjectsSortMode.None))
            {
                if (fp == null) continue;
                bool burning;
                try { burning = fp.IsBurning(); } catch { burning = false; }
                if (!burning) continue;
                string n = fp.gameObject.name ?? "";
                float r = n.IndexOf("pyre", System.StringComparison.OrdinalIgnoreCase) >= 0 ? SnowField.PyreMeltRadius : SnowField.FireMeltRadius;
                s_fires.Add(new Fire { Pos = fp.transform.position, Radius = r });
            }
            var changed = new HashSet<Vector2s>();
            if (!SameSet(old, s_fires))
            {
                foreach (var f in s_fires) changed.Add(ZoneSystem.GetZone(f.Pos));
                changed.UnionWith(before);
            }
            return changed;
        }

        private static bool SameSet(List<Fire> a, List<Fire> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if ((a[i].Pos - b[i].Pos).sqrMagnitude > 0.01f || a[i].Radius != b[i].Radius) return false;
            return true;
        }

        /// <summary>Melt 0..1 at a point: 1 inside 60 % of the radius, fading to 0 at the radius (XZ distance).</summary>
        internal static float MeltAt(Vector3 p)
        {
            float m = 0f;
            for (int i = 0; i < s_fires.Count; i++)
            {
                var f = s_fires[i];
                float dx = p.x - f.Pos.x, dz = p.z - f.Pos.z;
                float d2 = dx * dx + dz * dz;
                if (d2 >= f.Radius * f.Radius) continue;
                float t = Mathf.InverseLerp(f.Radius, f.Radius * 0.6f, Mathf.Sqrt(d2));
                if (t > m) m = t;
                if (m >= 1f) break;
            }
            return m;
        }

        internal static int Count => s_fires.Count;
    }
}
