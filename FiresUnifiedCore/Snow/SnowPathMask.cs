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

        /// <summary>0.2.841 ([seasons], Fire 10-26 "building terrain and rock up around my hut"): ClearAt bilinear over the 4 m cells
        /// (cell centres), so a path / a levelled build spot fades over a cell instead of a 0 -> full step.</summary>
        public static float ClearAtSmooth(Vector3 p)
        {
            float cell = ZoneSystem.c_ZoneSize / SnowField.MaskSize;
            float fx = p.x / cell - 0.5f, fz = p.z / cell - 0.5f;
            int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
            float tx = fx - x0, tz = fz - z0;
            float C(int x, int z) => ClearAt(new Vector3((x + 0.5f) * cell, p.y, (z + 0.5f) * cell));
            return Mathf.Lerp(Mathf.Lerp(C(x0, z0), C(x0 + 1, z0), tx), Mathf.Lerp(C(x0, z0 + 1), C(x0 + 1, z0 + 1), tx), tz);
        }

        /// <summary>The zone's raw mask (256 bytes, before refill), or null when it has none.</summary>
        public static byte[] ClearMask(Vector2s zone) => s_cache.TryGetValue(zone, out var e) && e.Values != null ? (byte[])e.Values.Clone() : null;

        internal static float Refilled(double stamp)   // 0.2.1051: internal, SnowTrail fills back at the same rate
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
                    if (!SnowField.Enabled || modifier == null) return;
                    // 0.2.1024 ([saddles]): any terrain op with paint changes the paved / path factor around it (Core's 0.5 m cache only
                    // notices after its 5 s expiry); its paint circle + the snow edge goes out as CellsChanged, path stamp or not
                    if ((modifier.m_paintCleared || modifier.m_level || modifier.m_raise || modifier.m_smooth) && ZoneSystem.instance != null && ZoneSystem.instance.IsZoneLoaded(pos))
                    {
                        float pr = Mathf.Max(modifier.m_paintRadius, Mathf.Max(modifier.m_levelRadius, Mathf.Max(modifier.m_raiseRadius, modifier.m_smoothRadius))) + 1f;
                        SnowField.NoteCellsChanged(ZoneSystem.GetZone(pos), new Rect(pos.x - pr, pos.z - pr, 2f * pr, 2f * pr));
                    }
                    if (!modifier.m_paintCleared) return;
                    var t = modifier.m_paintType;
                    if (t != TerrainModifier.PaintType.Dirt && t != TerrainModifier.PaintType.Paved && t != TerrainModifier.PaintType.Cultivate
                        && t != TerrainModifier.PaintType.Reset) return;
                    if (SnowField.BaseAt(pos) <= 0f) return;
                    // 0.2.830 (Fire 10-26: "forced into first person for a frame, then the snow changes"): vanilla's deep-snow WALK
                    // trail (Character.m_deepSnowWalkObj, one per few metres while wading) is vanilla's own trail; stamping it cleared
                    // whole 4 m cells and repainted the chunk on every step. Only real digs / paths stamp now.
                    if (IsWalkTrail(modifier) || CreatureSnowTrail.IsCreatureTrail(modifier))   // 0.2.904: creature trails ([worldgen]) are walk trails too
                    {
                        s_walkSkipped++;
                        // 0.2.885 (lead 10-27, Fire "just as wide as the player, no wider"): walk trails have never stamped since 0.2.830, on
                        // any zone; this line proves it on FAT zones (FAT's voxel snow draws the trail).
                        if (s_walkFatLogged < 3 && SnowFat.OwnsZone(ZoneSystem.GetZone(pos))) { s_walkFatLogged++; FiresLogger.LogDebug($"[Snow] walk op on a FAT zone: Core path mask skipped (at {pos.x:0},{pos.z:0}, paint {modifier.m_paintType} r {modifier.m_paintRadius:0.0})"); }
                        return;
                    }
                    // 0.2.868 (Fire 10-27 "the shovel flattens a massive area not just a small area around the click"): the stamp clears whole
                    // 4 m cells within the op radius + 2 m, so vanilla's r ~3 shovel op cleared ~10 m across, ClearAtSmooth faded 4 m further and
                    // the depth slope limit ramped the rest. On a zone FAT owns, FAT 1101 digs the shovel patch itself at 0.25 m, so the mask is
                    // skipped there; heightmap zones keep it (vanilla terrain has nothing finer).
                    if (IsShovel(modifier) && SnowFat.OwnsZone(ZoneSystem.GetZone(pos)))
                    {
                        s_shovelSkipped++;
                        if (s_shovelLogged++ < 3) FiresLogger.LogDebug($"[Snow] shovel op at {pos.x:0},{pos.z:0} on a FAT zone: Core path mask skipped (FAT digs it at 0.25 m)");
                        return;
                    }
                    // 0.2.895 (lead / [seasons] 10-27: vanilla Fireplace.UpdateSnowMelt spawns m_snowMelter - snow_fire_big, Cultivate r 10 -
                    // every interval while a Deep North fire burns): a fire's melt circle is not a dug path, so it never stamps the mask on
                    // any zone (Core's own fire melter still melts around burning fires).
                    if (IsFireMelter(modifier))
                    {
                        s_meltSkipped++;
                        if (s_meltLogged < 3) { s_meltLogged++; FiresLogger.LogDebug($"[Snow] fireplace melter op at {pos.x:0},{pos.z:0}: not a path (vanilla fire melt)"); }
                        return;
                    }
                    // 0.2.894 (lead pre-approved 10-27; round #18: a repeating "paint Cultivate r 10.0" op at 265,808 on a FAT zone stamped
                    // ~26 4 m cells each, and Core's DepthAt at the SFSC3 hut fell 0.67-0.97 m -> 0.08 -> 0.00): on a zone FAT owns, NO op
                    // stamps Core's mask (FAT's voxel snow draws every dig, path and trail itself); heightmap zones unchanged.
                    if (SnowFat.OwnsZone(ZoneSystem.GetZone(pos)))
                    {
                        s_fatSkipped++;
                        if (s_fatLogged < 5) { s_fatLogged++; FiresLogger.LogDebug($"[Snow] op on a FAT zone at {pos.x:0},{pos.z:0} (paint {modifier.m_paintType} r {modifier.m_paintRadius:0.0}): Core path mask skipped (FAT draws it)"); }
                        return;
                    }
                    var nview = s_nview(__instance);
                    if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;
                    // 0.2.885: which ops still stamp (paint / radius / level / smooth, FAT zone or not), the first 8 per session.
                    if (s_stampLogged < 8) { s_stampLogged++; FiresLogger.LogDebug($"[Snow] path stamp op at {pos.x:0},{pos.z:0}: paint {modifier.m_paintType} r {modifier.m_paintRadius:0.0}, level {modifier.m_level} r {modifier.m_levelRadius:0.0}, smooth {modifier.m_smooth}, raise {modifier.m_raise}, {(SnowFat.OwnsZone(ZoneSystem.GetZone(pos)) ? "FAT zone" : "heightmap zone")}"); }
                    Stamp(__instance, nview.GetZDO(), pos, Mathf.Max(1f, modifier.m_paintRadius));
                }
                catch (Exception e) { FiresLogger.LogDebug($"[Snow] path stamp skipped: {e.Message}"); }
            }
        }

        // 0.2.826 ([seasons], Deep North perf): every attack in snow is a shovel op, so stamps came ~9/s, each one 2 ZDO.Set on the
        // synced TerrainComp ZDO and one Debug line. Now: a stamp that changes no cell writes nothing; a zone writes at most once per
        // WriteGapSeconds (later stamps merge into a pending mask, flushed by the 2 s poll); one summary line per SummarySeconds.
        private const float WriteGapSeconds = 2f, SummarySeconds = 5f;
        private sealed class Pending { public ZDO Zdo; public byte[] Mask; public float LastWrite; public bool Dirty; }
        private static readonly Dictionary<Vector2s, Pending> s_pending = new Dictionary<Vector2s, Pending>();
        private static int s_ops, s_noops, s_writes, s_merged, s_cellsSum;
        private static float s_summaryAt;

        /// <summary>Writes merged stamps whose write gap has passed (called by the 2 s poll).</summary>
        internal static void FlushPending()
        {
            float now = Time.unscaledTime;
            List<Vector2s> drop = null;
            foreach (var kv in s_pending)
            {
                var p = kv.Value;
                if (p.Zdo == null || !p.Zdo.IsValid() || !p.Zdo.IsOwner()) { { if (drop == null) drop = new List<Vector2s>(); drop.Add(kv.Key); }; continue; }
                if (p.Dirty && now - p.LastWrite >= WriteGapSeconds) Write(p, now);
                else if (!p.Dirty && now - p.LastWrite > 30f) { if (drop == null) drop = new List<Vector2s>(); drop.Add(kv.Key); };
            }
            if (drop != null) foreach (var k in drop) s_pending.Remove(k);
            if ((s_ops > 0 || s_shovelSkipped > 0 || s_walkSkipped > 0 || s_fatSkipped > 0 || s_meltSkipped > 0) && now >= s_summaryAt)   // 0.2.869 ([seasons] review): skipped-only sessions still print
            {
                FiresLogger.LogVerbose($"[Snow] path stamps last {SummarySeconds:0} s: {s_ops} op(s), {s_cellsSum} cell(s) cleared, {s_noops} no-op(s) skipped, {s_merged} merged, {s_writes} ZDO write(s), {s_walkSkipped} walk-trail op(s) left to vanilla, {s_shovelSkipped} shovel op(s) left to FAT, {s_fatSkipped} other op(s) on FAT zones skipped, {s_meltSkipped} fireplace melter op(s) not paths");
                s_ops = s_noops = s_writes = s_merged = s_cellsSum = 0; s_walkSkipped = 0; s_shovelSkipped = 0; s_fatSkipped = 0; s_meltSkipped = 0;
                s_summaryAt = now + SummarySeconds;
            }
        }

        private static void Write(Pending p, float now)
        {
            p.Zdo.Set(KeyMask, Encode(p.Mask));
            p.Zdo.Set(KeyStamp, (long)(ZNet.instance.GetTimeSeconds() * 1000.0));
            p.LastWrite = now; p.Dirty = false; s_writes++;
        }

        // 0.2.868: vanilla's snow shovel ops (Player.m_snowShovelDefault / m_snowShovelStrong, the objects Attack spawns), read once
        // from the Player prefab by reflection and matched like the walk trail.
        private static readonly List<TerrainOp.Settings> s_shovels = new List<TerrainOp.Settings>();
        private static bool s_shovelLooked;
        internal static int s_shovelSkipped;
        private static int s_shovelLogged, s_walkFatLogged, s_stampLogged;   // 0.2.885: + walk-on-FAT and stamp-op proof lines
        internal static int s_fatSkipped; private static int s_fatLogged;   // 0.2.894: every op on a FAT zone
        internal static int s_meltSkipped; private static int s_meltLogged;   // 0.2.895: vanilla fireplace snow melters

        // 0.2.895: every Fireplace prefab's m_snowMelter op (read once from ZNetScene by reflection), matched like the walk trail.
        private static readonly List<TerrainOp.Settings> s_melters = new List<TerrainOp.Settings>();
        private static bool s_meltLooked;
        private static bool IsFireMelter(TerrainOp.Settings m)
        {
            if (!s_meltLooked && ZNetScene.instance != null)
            {
                s_meltLooked = true;
                var fi = AccessTools.Field(typeof(Fireplace), "m_snowMelter");
                if (fi != null)
                    foreach (var go in ZNetScene.instance.m_prefabs)
                    {
                        var fp = go != null ? go.GetComponent<Fireplace>() : null;
                        var obj = fp != null ? fi.GetValue(fp) as GameObject : null;
                        var s = obj != null ? obj.GetComponentInChildren<TerrainOp>(true)?.m_settings : null;
                        if (s != null && !s_melters.Exists(x => Same(x, s))) s_melters.Add(s);
                    }
                FiresLogger.LogDebug($"[Snow] path stamps skip vanilla fireplace snow melters: {(s_melters.Count > 0 ? string.Join(", ", s_melters.ConvertAll(s => $"paint {s.m_paintType} r {s.m_paintRadius:0.0} strength {s.m_paintStrength:0.##}")) : (fi == null ? "Fireplace.m_snowMelter not found" : "none on any Fireplace prefab"))}");
            }
            if (m == null) return false;
            foreach (var s in s_melters) if (m == s || Same(m, s)) return true;
            return false;
        }
        private static bool Same(TerrainOp.Settings a, TerrainOp.Settings b) =>
            a.m_paintType == b.m_paintType && Mathf.Approximately(a.m_paintRadius, b.m_paintRadius) && a.m_level == b.m_level && a.m_raise == b.m_raise
            && a.m_smooth == b.m_smooth && Mathf.Approximately(a.m_paintStrength, b.m_paintStrength);
        private static bool IsShovel(TerrainOp.Settings m)
        {
            if (!s_shovelLooked)
            {
                var pl = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Player") : null;
                var player = pl != null ? pl.GetComponent<Player>() : null;
                if (player != null)
                {
                    s_shovelLooked = true;
                    foreach (string f in new[] { "m_snowShovelDefault", "m_snowShovelStrong" })
                    {
                        var fi = AccessTools.Field(typeof(Player), f);
                        var go = fi != null ? fi.GetValue(player) as GameObject : null;
                        var s = go != null ? go.GetComponentInChildren<TerrainOp>(true)?.m_settings : null;
                        if (s != null) s_shovels.Add(s);
                    }
                    FiresLogger.LogDebug($"[Snow] path stamps: snow shovel ops {(s_shovels.Count > 0 ? string.Join(", ", s_shovels.ConvertAll(s => $"paint {s.m_paintType} r {s.m_paintRadius:0.0}")) : "not found on the Player prefab (stamped as before)")}");
                }
            }
            if (m == null) return false;
            foreach (var s in s_shovels)
                if (m == s || (m.m_paintType == s.m_paintType && Mathf.Approximately(m.m_paintRadius, s.m_paintRadius)
                    && m.m_level == s.m_level && m.m_raise == s.m_raise && m.m_smooth == s.m_smooth
                    && Mathf.Approximately(m.m_paintStrength, s.m_paintStrength))) return true;
            return false;
        }

        private static TerrainOp.Settings s_walk;
        private static bool s_walkLooked;
        internal static int s_walkSkipped;
        internal static bool IsWalkTrail(TerrainOp.Settings m)
        {
            if (!s_walkLooked)
            {
                var pl = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Player") : null;
                var obj = pl != null ? pl.GetComponent<Player>()?.m_deepSnowWalkObj : null;
                s_walk = obj != null ? obj.GetComponentInChildren<TerrainOp>(true)?.m_settings : null;
                if (pl != null) s_walkLooked = true;
                FiresLogger.LogDebug($"[Snow] path stamps skip vanilla's deep-snow walk trail: {(s_walk != null ? $"found (paint {s_walk.m_paintType} r {s_walk.m_paintRadius:0.0})" : "no trail op on the Player prefab")}");
            }
            if (s_walk == null || m == null) return false;
            return m == s_walk || (m.m_paintType == s_walk.m_paintType && Mathf.Approximately(m.m_paintRadius, s_walk.m_paintRadius)
                && m.m_level == s_walk.m_level && m.m_raise == s_walk.m_raise && m.m_smooth == s_walk.m_smooth
                && Mathf.Approximately(m.m_paintStrength, s_walk.m_paintStrength));
        }

        private static void Stamp(TerrainComp tc, ZDO zdo, Vector3 pos, float radius)
        {
            var zone = ZoneSystem.GetZone(tc.transform.position);
            Vector3 c = ZoneSystem.GetZonePos(zone);
            s_ops++;
            if (s_summaryAt <= 0f) s_summaryAt = Time.unscaledTime + SummarySeconds;
            s_pending.TryGetValue(zone, out var pend);
            if (pend != null && pend.Zdo != zdo) { s_pending.Remove(zone); pend = null; }
            // bring the existing mask to "now" (apply the refill so far), then add the new clear and re-stamp; a pending (unwritten)
            // mask is already "now"
            byte[] cur = pend != null && pend.Dirty ? (byte[])pend.Mask.Clone() : Decode(zdo.GetByteArray(KeyMask, null)) ?? new byte[Cells];
            float refilled = pend != null && pend.Dirty ? 0f : Refilled(zdo.GetLong(KeyStamp, 0L) / 1000.0);
            byte[] before = (byte[])cur.Clone();
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
            s_cellsSum += changedCells;
            bool same = true;
            for (int i = 0; i < Cells; i++) if (cur[i] != before[i]) { same = false; break; }
            // nothing moved: the cells were already clear and no refill to fold in -> no write (the stamp time stays, so the refill runs on)
            if (same) { s_noops++; return; }
            float now = Time.unscaledTime;
            if (pend == null) { pend = new Pending { Zdo = zdo, LastWrite = -999f }; s_pending[zone] = pend; }
            pend.Mask = cur; pend.Dirty = true;
            if (now - pend.LastWrite >= WriteGapSeconds) Write(pend, now);
            else s_merged++;
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
