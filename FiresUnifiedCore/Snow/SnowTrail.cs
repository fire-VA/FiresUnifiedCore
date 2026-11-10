using System.Collections.Generic;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// 0.2.1051 (Fire 10-30: "we will go with Core's raised terrain so it can remain vanilla compatible when it's not over a voxel chunk";
    /// lead / [saddles]: walk trails must cut Core's snow there): the fine walk-trail mask. 0.5 m cells (128 x 128 per zone, FAT's trail
    /// resolution); a stamp packs the snow in its circle, the inject (<see cref="SnowHeightmapInject"/>) lowers the target there to
    /// <see cref="PackedKeep"/> of its depth - packed snow, never bare soil - and the trail fills back in over world time at the path
    /// mask's rate (<see cref="SnowPathMask.Refilled"/>, faster in a blizzard). Local per peer: every peer stamps the trails it sees (FAT
    /// calls <see cref="Stamp"/>), cosmetic, no ZDO / RPC. A stamp re-bakes each loaded chunk its circle touches (both sides of a zone edge),
    /// at most every <see cref="PokeGap"/> s per chunk; chunks with trails still filling back are re-baked every <see cref="RefillPoke"/> s.
    /// Add-only contract.
    /// </summary>
    public static class SnowTrail
    {
        public const int Cells = 128;
        public const float CellSize = ZoneSystem.c_ZoneSize / (float)Cells;   // 0.5 m
        internal const float PackedKeep = 0.2f;   // a trail keeps a fifth of the snow (packed)
        private const float PokeGap = 0.5f, RefillPoke = 20f, CleanupEvery = 60f;

        // per zone: the world time (s) each cell was last stamped, 0 = never
        private static readonly Dictionary<Vector2s, double[]> s_zones = new Dictionary<Vector2s, double[]>();
        private static readonly Dictionary<Heightmap, float> s_lastPoke = new Dictionary<Heightmap, float>();
        private static readonly HashSet<Heightmap> s_dirty = new HashSet<Heightmap>();
        private static float s_nextRefill, s_nextCleanup, s_summaryAt;
        private static int s_stamps, s_cells, s_pokes;
        private static double s_pokeMs;

        /// <summary>Packs the snow in a circle (world XZ, radius in metres, clamped 0.25..4). Call on every peer that sees the walker.</summary>
        public static void Stamp(Vector3 pos, float radius)
        {
            if (!SnowField.Enabled || ZNet.instance == null) return;
            radius = Mathf.Clamp(radius, 0.25f, 4f);
            double now = ZNet.instance.GetTimeSeconds();
            float r2 = radius * radius;
            int n = 0;
            for (float x = pos.x - radius; x <= pos.x + radius + CellSize; x += CellSize)
                for (float z = pos.z - radius; z <= pos.z + radius + CellSize; z += CellSize)
                {
                    var p = new Vector3(x, pos.y, z);
                    var zone = ZoneSystem.GetZone(p);
                    if (!Index(zone, p, out int i)) continue;
                    Vector3 cc = CellCentre(zone, i);
                    float dx = cc.x - pos.x, dz = cc.z - pos.z;
                    if (dx * dx + dz * dz > r2) continue;
                    if (!s_zones.TryGetValue(zone, out var cells)) s_zones[zone] = cells = new double[Cells * Cells];
                    if (cells[i] != now) { cells[i] = now; n++; }
                }
            if (n == 0) return;
            s_stamps++; s_cells += n;
            // every loaded chunk the circle touches (a trail on a zone edge re-bakes both sides)
            var all = Heightmap.GetAllHeightmaps();
            if (all == null) return;
            for (int k = 0; k < all.Count; k++)
            {
                var hm = all[k];
                if (hm == null || hm.IsDistantLod) continue;
                float half = hm.m_width * hm.m_scale * 0.5f;
                Vector3 c = hm.transform.position;
                if (Mathf.Abs(pos.x - c.x) <= half + radius && Mathf.Abs(pos.z - c.z) <= half + radius) s_dirty.Add(hm);
            }
        }

        /// <summary>Trail strength 0..1 at a point now (1 = just stamped, 0 = none or filled back).</summary>
        public static float TrailAt(Vector3 p)
        {
            if (s_zones.Count == 0) return 0f;
            var zone = ZoneSystem.GetZone(p);
            if (!s_zones.TryGetValue(zone, out var cells) || !Index(zone, p, out int i)) return 0f;
            double t = cells[i];
            return t <= 0 ? 0f : 1f - SnowPathMask.Refilled(t);
        }

        /// <summary>The share of the snow target a texel keeps under trails (1 = no trail, PackedKeep = a fresh trail).</summary>
        internal static float KeepAt(Vector3 p)
        {
            float t = TrailAt(p);
            return t <= 0f ? 1f : 1f - (1f - PackedKeep) * t;
        }

        /// <summary>Runs the throttled re-bakes (SnowTicker.Update, every frame; cheap when idle).</summary>
        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now >= s_nextRefill && s_zones.Count > 0)
            {
                s_nextRefill = now + RefillPoke;
                MarkFilling();
            }
            if (s_dirty.Count > 0)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                List<Heightmap> done = null;
                foreach (var hm in s_dirty)
                {
                    if (hm == null) { (done ?? (done = new List<Heightmap>())).Add(hm); continue; }
                    if (s_lastPoke.TryGetValue(hm, out float at) && now - at < PokeGap) continue;
                    s_lastPoke[hm] = now;
                    hm.Poke(0);   // vanilla rebuilds the chunk, then the inject reads the trail mask
                    s_pokes++;
                    (done ?? (done = new List<Heightmap>())).Add(hm);
                }
                if (done != null) foreach (var hm in done) s_dirty.Remove(hm);
                s_pokeMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            }
            if (now >= s_nextCleanup) { s_nextCleanup = now + CleanupEvery; Cleanup(); }
            if (now >= s_summaryAt)
            {
                if (s_stamps > 0 || s_pokes > 0)
                    FiresLogger.LogDebug($"[Snow] walk trails last 10 s: {s_stamps} stamp(s), {s_cells} cell(s) packed, {s_pokes} chunk re-bake(s) ({s_pokeMs:0.0} ms), {s_zones.Count} zone(s) with trails");
                s_stamps = s_cells = s_pokes = 0; s_pokeMs = 0; s_summaryAt = now + 10f;
            }
        }

        // chunks whose trails are still filling back get one re-bake per RefillPoke so the snow visibly closes over them
        private static void MarkFilling()
        {
            var all = Heightmap.GetAllHeightmaps();
            if (all == null) return;
            for (int k = 0; k < all.Count; k++)
            {
                var hm = all[k];
                if (hm == null || hm.IsDistantLod) continue;
                if (s_zones.TryGetValue(ZoneSystem.GetZone(hm.transform.position), out var cells) && AnyFilling(cells)) s_dirty.Add(hm);
            }
        }

        private static bool AnyFilling(double[] cells)
        {
            for (int i = 0; i < cells.Length; i++)
                if (cells[i] > 0 && SnowPathMask.Refilled(cells[i]) < 1f) return true;
            return false;
        }

        // drops zones whose trails have all filled back, and chunks no longer loaded
        private static void Cleanup()
        {
            List<Vector2s> drop = null;
            foreach (var kv in s_zones) if (!AnyFilling(kv.Value)) (drop ?? (drop = new List<Vector2s>())).Add(kv.Key);
            if (drop != null) foreach (var z in drop) s_zones.Remove(z);
            if (s_lastPoke.Count > 512) s_lastPoke.Clear();
        }

        private static bool Index(Vector2s zone, Vector3 p, out int i)
        {
            Vector3 c = ZoneSystem.GetZonePos(zone);
            int x = Mathf.FloorToInt((p.x - (c.x - ZoneSystem.c_ZoneSizeHalf)) / CellSize), z = Mathf.FloorToInt((p.z - (c.z - ZoneSystem.c_ZoneSizeHalf)) / CellSize);
            i = z * Cells + x;
            return x >= 0 && z >= 0 && x < Cells && z < Cells;
        }

        private static Vector3 CellCentre(Vector2s zone, int i)
        {
            Vector3 c = ZoneSystem.GetZonePos(zone);
            return new Vector3(c.x - ZoneSystem.c_ZoneSizeHalf + (i % Cells + 0.5f) * CellSize, 0f, c.z - ZoneSystem.c_ZoneSizeHalf + (i / Cells + 0.5f) * CellSize);
        }
    }
}
