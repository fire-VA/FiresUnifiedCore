using System;
using System.Collections.Generic;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// The one synced snow state: a per-zone path clear mask (16×16 cells, 0..255) on the zone's TerrainComp ZDO, written ONLY by that
    /// terrain's owner when a dig/paint op lands in snow (TerrainComp.InternalDoOperation runs on the owner). Vanilla ZDO sync carries
    /// it; no RPC. Two keys: the RLE mask and the world time it was stamped at. Readers treat it as untrusted: exactly 256 decoded
    /// bytes, a bounded RLE, values clamped; a bad blob reads as "no paths". A path fills back in over world time (about one game day,
    /// four times faster in a blizzard); the fill rate is the local weather's, so peers may differ slightly (cosmetic).
    /// </summary>
    public static class SnowPathMask
    {
        private const string KeyMask = "fires_snow_clear";
        private const string KeyStamp = "fires_snow_clear_t";
        private const int Cells = SnowField.MaskSize * SnowField.MaskSize;
        private const float BaseRefillSeconds = 1800f;   // one game day of world time

        private sealed class Entry { public byte[] Values; public double Stamp; public uint Rev; public ZDO Zdo; }
        private static readonly Dictionary<Vector2s, Entry> s_cache = new Dictionary<Vector2s, Entry>();

        /// <summary>Path refill speed multiplier from the weather (set by the snow step: 4 in a blizzard, 2 while snowing, else 1).</summary>
        internal static float RefillBoost = 1f;

        /// <summary>Path clear 0..1 at a point right now (after refill).</summary>
        public static float ClearAt(Vector3 p)
        {
            var zone = ZoneSystem.GetZone(p);
            if (!s_cache.TryGetValue(zone, out var e) || e.Values == null) return 0f;
            Vector3 c = ZoneSystem.GetZonePos(zone);
            int cx = Mathf.Clamp(Mathf.FloorToInt((p.x - (c.x - ZoneSystem.c_ZoneSizeHalf)) / (ZoneSystem.c_ZoneSize / SnowField.MaskSize)), 0, SnowField.MaskSize - 1);
            int cz = Mathf.Clamp(Mathf.FloorToInt((p.z - (c.z - ZoneSystem.c_ZoneSizeHalf)) / (ZoneSystem.c_ZoneSize / SnowField.MaskSize)), 0, SnowField.MaskSize - 1);
            float v = e.Values[cz * SnowField.MaskSize + cx] / 255f;
            return v <= 0f ? 0f : Mathf.Clamp01(v - Refilled(e.Stamp));
        }

        /// <summary>The zone's raw mask (256 bytes, before refill), or null when it has none.</summary>
        public static byte[] ClearMask(Vector2s zone) => s_cache.TryGetValue(zone, out var e) && e.Values != null ? (byte[])e.Values.Clone() : null;

        private static float Refilled(double stamp)
        {
            if (ZNet.instance == null) return 0f;
            double dt = ZNet.instance.GetTimeSeconds() - stamp;
            if (dt <= 0) return 0f;
            return (float)Math.Min(1.0, dt * RefillBoost / BaseRefillSeconds);
        }

        /// <summary>Re-reads the masks of loaded TerrainComps whose ZDO changed. Returns the zones that changed.</summary>
        internal static HashSet<Vector2s> Poll()
        {
            var changed = new HashSet<Vector2s>();
            var seen = new HashSet<Vector2s>();
            foreach (var tc in TerrainCompsLoaded())
            {
                var nview = s_nview(tc);
                if (nview == null || !nview.IsValid()) continue;
                var zdo = nview.GetZDO();
                var zone = ZoneSystem.GetZone(tc.transform.position);
                seen.Add(zone);
                if (s_cache.TryGetValue(zone, out var e) && e.Zdo == zdo && e.Rev == zdo.DataRevision) continue;
                byte[] blob = zdo.GetByteArray(KeyMask, null);
                var vals = blob != null ? Decode(blob) : null;
                double stamp = zdo.GetLong(KeyStamp, 0L) / 1000.0;
                bool had = e != null && e.Values != null;
                s_cache[zone] = new Entry { Values = vals, Stamp = stamp, Rev = zdo.DataRevision, Zdo = zdo };
                if (had || vals != null) changed.Add(zone);
            }
            // unloaded zones drop out
            var gone = new List<Vector2s>();
            foreach (var k in s_cache.Keys) if (!seen.Contains(k)) gone.Add(k);
            foreach (var k in gone) s_cache.Remove(k);
            return changed;
        }

        private static readonly AccessTools.FieldRef<TerrainComp, ZNetView> s_nview = AccessTools.FieldRefAccess<TerrainComp, ZNetView>("m_nview");
        private static readonly AccessTools.FieldRef<List<TerrainComp>> s_instances = TryInstances();
        private static AccessTools.FieldRef<List<TerrainComp>> TryInstances()
        {
            try { return AccessTools.StaticFieldRefAccess<List<TerrainComp>>(AccessTools.Field(typeof(TerrainComp), "s_instances")); } catch { return null; }
        }
        private static IEnumerable<TerrainComp> TerrainCompsLoaded()
        {
            List<TerrainComp> list = null;
            try { list = s_instances?.Invoke(); } catch { }
            if (list != null) return list.ToArray();
            return UnityEngine.Object.FindObjectsByType<TerrainComp>(FindObjectsSortMode.None);
        }

        // Owner-side stamping: a dig / paint op that lands in snow clears the cells it covers.
        [HarmonyPatch(typeof(TerrainComp), "InternalDoOperation")]
        private static class TerrainComp_InternalDoOperation_Stamp
        {
            private static void Postfix(TerrainComp __instance, Vector3 pos, TerrainOp.Settings modifier)
            {
                try
                {
                    if (!SnowField.Enabled || modifier == null || !modifier.m_paintCleared) return;
                    var t = modifier.m_paintType;
                    if (t != TerrainModifier.PaintType.Dirt && t != TerrainModifier.PaintType.Paved && t != TerrainModifier.PaintType.Cultivate
                        && t != TerrainModifier.PaintType.Reset) return;
                    if (SnowField.BaseAt(pos) <= 0f) return;
                    var nview = s_nview(__instance);
                    if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;
                    Stamp(__instance, nview.GetZDO(), pos, Mathf.Max(1f, modifier.m_paintRadius));
                }
                catch (Exception e) { FiresLogger.LogDebug($"[Snow] path stamp skipped: {e.Message}"); }
            }
        }

        private static void Stamp(TerrainComp tc, ZDO zdo, Vector3 pos, float radius)
        {
            var zone = ZoneSystem.GetZone(tc.transform.position);
            Vector3 c = ZoneSystem.GetZonePos(zone);
            // bring the existing mask to "now" (apply the refill so far), then add the new clear and re-stamp
            byte[] cur = Decode(zdo.GetByteArray(KeyMask, null)) ?? new byte[Cells];
            float refilled = Refilled(zdo.GetLong(KeyStamp, 0L) / 1000.0);
            float cell = ZoneSystem.c_ZoneSize / SnowField.MaskSize;
            int changedCells = 0;
            for (int z = 0; z < SnowField.MaskSize; z++)
                for (int x = 0; x < SnowField.MaskSize; x++)
                {
                    int i = z * SnowField.MaskSize + x;
                    float v = Mathf.Max(0f, cur[i] / 255f - refilled);
                    float px = c.x - ZoneSystem.c_ZoneSizeHalf + (x + 0.5f) * cell, pz = c.z - ZoneSystem.c_ZoneSizeHalf + (z + 0.5f) * cell;
                    float dx = px - pos.x, dz = pz - pos.z;
                    if (dx * dx + dz * dz <= (radius + cell * 0.5f) * (radius + cell * 0.5f)) { v = 1f; changedCells++; }
                    cur[i] = (byte)Mathf.RoundToInt(v * 255f);
                }
            zdo.Set(KeyMask, Encode(cur));
            zdo.Set(KeyStamp, (long)(ZNet.instance.GetTimeSeconds() * 1000.0));
            FiresLogger.LogDebug($"[Snow] path cleared in zone {zone.x},{zone.y}: {changedCells} cell(s) around ({pos.x:0},{pos.z:0}) r {radius:0.0}");
        }

        // RLE: pairs (run 1..255, value). Decode is bounded: exactly 256 output bytes, anything else is rejected.
        internal static byte[] Encode(byte[] v)
        {
            var o = new List<byte>(64);
            int i = 0;
            while (i < v.Length)
            {
                byte b = v[i]; int run = 1;
                while (i + run < v.Length && v[i + run] == b && run < 255) run++;
                o.Add((byte)run); o.Add(b);
                i += run;
            }
            return o.ToArray();
        }

        internal static byte[] Decode(byte[] blob)
        {
            if (blob == null || blob.Length < 2 || blob.Length > Cells * 2 || (blob.Length & 1) != 0) return null;
            var o = new byte[Cells];
            int n = 0;
            for (int i = 0; i < blob.Length; i += 2)
            {
                int run = blob[i];
                if (run == 0 || n + run > Cells) return null;
                for (int k = 0; k < run; k++) o[n++] = blob[i + 1];
            }
            return n == Cells ? o : null;
        }
    }
}
