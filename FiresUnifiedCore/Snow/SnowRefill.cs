using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using FiresCore.Logging;

namespace FiresCore.Snow
{
    /// <summary>
    /// 0.2.1061 ([wishbone], Fire's order: trails "fill back in during storms"; method read from vanilla + Fimbulvetr, no code copied): on
    /// the TerrainComp OWNER only (the paint is saved in the ZDO and synced to every peer), every RefillSeconds the modified paint texels a
    /// SNOW op lowered move back toward their natural snow - the base mask G SnowBaseMask writes (vanilla's in the real Deep North):
    ///  - rate by the weather (SnowTicker's): calm 1, snowfall 5, snowstorm 25 cm per minute of world time (G x 2 m scale);
    ///  - only texels with no dirt / paving (r and b under 0.1) and natural snow above 0; a texel ABOVE its natural G (cultivated soil,
    ///    a pile) is never touched;
    ///  - a texel back at its natural G is unset (m_modifiedPaint false), so it is world-gen snow again;
    ///  - Save() + a paint-only repaint only after a visible step (1/255 x 3 or a texel unset).
    /// Budgeted (MaxMsPerFrame, one TerrainComp per frame, nearest the local player first). [Snow] Snow in the natural ground off = nothing.
    /// Debug: "[Snow] refill last 30 s: N pass(es), T texel(s) moved, U unset, S save(s), X ms (max Y ms), weather W".
    /// </summary>
    internal static class SnowRefill
    {
        private const float RefillSeconds = 2f, MaxMsPerFrame = 1f, VisibleStep = 3f / 255f, MetresPerG = 2f;
        private const float CalmCmPerMin = 1f, SnowCmPerMin = 5f, StormCmPerMin = 25f;
        private static readonly AccessTools.FieldRef<List<TerrainComp>> s_instances =
            AccessTools.StaticFieldRefAccess<List<TerrainComp>>(AccessTools.Field(typeof(TerrainComp), "s_instances"));
        private static readonly System.Reflection.MethodInfo s_save = AccessTools.Method(typeof(TerrainComp), "Save");
        private static readonly Dictionary<TerrainComp, double> s_lastPass = new Dictionary<TerrainComp, double>();
        private static readonly List<TerrainComp> s_order = new List<TerrainComp>();
        private static int s_cursor;
        private static bool s_voxelOffSaid;   // 0.2.1078
        private static int s_meltedOut;       // 0.2.1080

        // 0.2.1079: FAT 1323's trail-cut mark (VerdantsAscent.FatApi, TrailCutApiVersion >= 1), bound once by name
        private static Func<TerrainComp, byte[]> s_trailCutMask;
        private static Action<TerrainComp, int> s_clearTrailCut;
        private static bool s_trailCutTried;
        // 0.2.1153 (review of 1152, findings 2 + 3): FAT's mark is the one thing that tells a trail from player paint; value tests cannot (soft cultivated edges, cap snow, a trail's r).
        internal static bool MarksBound => BindTrailCut();
        internal static bool TrailMarked(TerrainComp tc, int i)
        {
            if (!BindTrailCut()) return false;
            byte[] marks = null; try { marks = s_trailCutMask(tc); } catch { }
            return marks != null && (i >> 3) < marks.Length && (marks[i >> 3] & (1 << (i & 7))) != 0;
        }
        private static bool BindTrailCut()
        {
            if (s_trailCutTried) return s_trailCutMask != null;
            s_trailCutTried = true;
            try
            {
                var t = FiresCore.Compat.OptionalTypes.Find("VerdantsAscent.FatApi");
                var ver = t?.GetField("TrailCutApiVersion", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (ver != null && Convert.ToInt32(ver.GetRawConstantValue()) >= 1)
                {
                    var m = AccessTools.Method(t, "TrailCutMask", new[] { typeof(TerrainComp) });
                    var c = AccessTools.Method(t, "ClearTrailCut", new[] { typeof(TerrainComp), typeof(int) });
                    if (m != null && m.ReturnType == typeof(byte[])) s_trailCutMask = (Func<TerrainComp, byte[]>)Delegate.CreateDelegate(typeof(Func<TerrainComp, byte[]>), m);
                    if (c != null) s_clearTrailCut = (Action<TerrainComp, int>)Delegate.CreateDelegate(typeof(Action<TerrainComp, int>), c);
                }
            }
            catch (Exception e) { FiresLogger.LogDebug($"[Snow] refill: FAT trail-cut bind failed: {e.Message}"); }
            FiresLogger.LogDebug($"[Snow] refill: FAT trail-cut mark {(s_trailCutMask != null ? "bound (voxel refill eases marked trail cuts only)" : "not found (voxel refill off)")}");
            return s_trailCutMask != null;
        }
        private static int s_passes, s_moved, s_unset, s_saves; private static double s_ms, s_maxMs; private static float s_sayAt;

        internal static void Tick()
        {
            if (!SnowConfig.NaturalSnow || !SnowField.CoreGroundOn || ZNet.instance == null) return;   // 0.2.1197: CoreGroundOn
            var list = s_instances != null ? s_instances() : null;
            if (list == null || list.Count == 0) return;
            if (s_cursor == 0 || s_cursor >= s_order.Count)
            {
                s_order.Clear(); s_order.AddRange(list);
                Vector3 me = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
                s_order.Sort((a, b) => (a != null ? (a.transform.position - me).sqrMagnitude : float.MaxValue).CompareTo(b != null ? (b.transform.position - me).sqrMagnitude : float.MaxValue));
                s_cursor = 0;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double now = ZNet.instance.GetTimeSeconds();
            float ratePerSec = RateCmPerMin() / 100f / MetresPerG / 60f;   // G per world second
            while (s_cursor < s_order.Count)
            {
                var tc = s_order[s_cursor++];
                if (tc == null || tc.m_nview == null || !tc.m_nview.IsValid() || !tc.m_nview.IsOwner()) continue;
                if (s_lastPass.TryGetValue(tc, out double last) && now - last < RefillSeconds) continue;
                double dt = s_lastPass.TryGetValue(tc, out last) ? Math.Min(now - last, 60.0) : RefillSeconds;
                s_lastPass[tc] = now;
                Pass(tc, (float)(ratePerSec * dt), (float)dt);
                if ((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= MaxMsPerFrame) break;
                break;   // one TerrainComp per frame
            }
            if (s_lastPass.Count > 512) s_lastPass.Clear();
            Say();
        }

        private static void Pass(TerrainComp tc, float step, float dtSec)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var hm = tc.m_hmap;
            var mod = tc.m_modifiedPaint; var paint = tc.m_paintMask;
            if (hm == null || mod == null || paint == null || step <= 0f) return;
            // 0.2.1078 ([worldgen] URGENT, Fire's live log on 1077: "voxel (FAT zones): 284 pass(es), 88515 cut texel(s) eased" in Clear
            // weather; his cultivated plot x318 z938 erased): on voxel ground a player's Cultivate is paint G UP - the same channel as FAT's
            // trail cut - so the voxel refill eased every garden back to base and saved it. The voxel refill is OFF until trail cuts carry
            // their own mark (with FAT); heightmap ground is unaffected (cultivated G is above base there and was never touched).
            if (SnowFat.OwnsZone(ZoneSystem.GetZone(hm.transform.position)))
            {
                // 0.2.1079 (FAT 1323's trail-cut mark, agreed with [worldgen]): only texels FAT marked as a trail cut are eased, never
                // without snow cover in the zone; no mark API / no mask on this comp = nothing, as 1078
                // 0.2.1080 (Fire 10-31, shot 699: "snow trails made ... during the winter show up as cultivated soil in the summer?"): a
                // MARKED trail cut in a zone with no snow cover melts out at once - G back to base, unset, mark cleared, saved - so a snow
                // trail never outlives its snow as a dirt path. Unmarked texels (gardens) are still never touched.
                if (BindTrailCut() && Thawing(hm, dtSec, out float thawV)) MeltOut(tc, hm, mod, paint, thawV);   // 0.2.1081: one melt-out for both grounds; 0.2.1118: texels on real snow go on to the refill below
                if (!BindTrailCut())
                {
                    if (!s_voxelOffSaid) { s_voxelOffSaid = true; FiresLogger.LogInfo($"[Snow] refill: voxel (FAT) ground {(s_trailCutMask == null ? "skipped - FAT's trail-cut mark API not found (cultivated soil is never eased back)" : "eases FAT-marked trail cuts only")}"); }
                    return;
                }
                byte[] marks = null;
                try { marks = s_trailCutMask(tc); } catch { }
                if (marks == null) return;
                if (!s_snowingNow) return;   // 0.2.1120 (Fire: "voxel also should not refill if its not snowing")
                VoxelPass(tc, hm, mod, paint, marks, step, t0);
                return;
            }
            // 0.2.1081 ([worldgen]: FAT 1324 marks every texel a trail op changed in G, up or down, on every TerrainComp): heightmap ground
            // melts out the same way - with no snow cover in the zone, each MARKED texel is unset (back to world-gen paint) and its mark
            // cleared, saved; unmarked texels (gardens, paving) are never touched
            // 0.2.1118 (Fire: "our deep north snow trails are filling in almost immediately?"; [worldgen]'s log: Summer / Fall melted every marked dent back each 2 s pass, even with the Deep
            // North's snow still there): the melt-out only takes texels with no snow left, so dents in real snow go on to the refill. Refill on Fire's rule, as FAT 0.2.1188: only while Vedr's weather snows, no grace then,
            // full depth in about 30 min of snowfall, "Faster in storms" (2x).
            bool marksBound = BindTrailCut();
            if (marksBound && Thawing(hm, dtSec, out float thaw)) MeltOut(tc, hm, mod, paint, thaw);
            float refillStep = s_snowingNow ? dtSec * (s_stormNow ? RefillStormRate : RefillSnowRate) / RefillFullSeconds : 0f;
            int moved = 0, unset = 0; float biggest = 0f;
            for (int i = 0; i < mod.Length && i < paint.Length; i++)
            {
                if (!mod[i]) continue;
                Color c = paint[i];
                if (c.b > 0.1f || (c.r > 0.1f && !marksBound)) continue;   // dirt / paving: the player's, never refilled. 0.2.1155: with marks, a trail's own r no longer stops its refill (review finding 3)
                float natural = SnowBaseMask.BaseG(hm, i);
                // 0.2.1156 (Fire's probe at (-369,80): EDITED r 0 g 0 b 0, base 0.60, drawn 0, "never fills"): my 1153 skip left unmarked edits low for good. G near 0 with no r or b is no paint at all (old cut, reset): back to the base at once, in any weather. Other unmarked paint stays the player's.
                if (marksBound && !TrailMarked(tc, i))
                {
                    if (c.g < 0.05f && c.r <= 0.1f && SnowBaseMask.BaseG(hm, i) > 0f) { mod[i] = false; unset++; s_staleUnset++; }
                    continue;
                }
                if (natural <= 0f || c.g >= natural) continue;   // no natural snow here, or at / above it (cultivated soil, a pile)
                if (refillStep <= 0f) continue;
                float g = Mathf.MoveTowards(c.g, natural, refillStep);
                biggest = Mathf.Max(biggest, g - c.g);
                if (natural - g < 0.002f && (marksBound || c.r < 0.01f) && c.b < 0.01f) { mod[i] = false; unset++; if (marksBound) { try { s_clearTrailCut?.Invoke(tc, i); } catch { } } }   // 0.2.1118: back at base = no longer a trail
                else { c.g = g; paint[i] = c; moved++; }
            }
            s_passes++; s_moved += moved; s_unset += unset;
            if (unset > 0 || biggest >= VisibleStep || moved > 0 && Accumulate(tc, biggest))
            {
                try { s_save?.Invoke(tc, new object[] { false }); s_saves++; } catch (Exception e) { FiresLogger.LogDebug($"[Snow] refill save failed: {e.Message}"); }
                hm.Poke(1, paintOnly: true);
                s_pending.Remove(tc);
            }
            double ms0 = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            s_ms += ms0; if (ms0 > s_maxMs) s_maxMs = ms0;
        }

        // 0.2.1081: the melt-out (heightmap + voxel ground): with no snow cover in the zone, every texel FAT marked as a trail op's is
        // unset - back to the world-gen paint, which is the base - and its mark cleared, then saved; unmarked texels are never touched
        // 0.2.1082 (Fire's pick "Melt out": marked trails fade back to grass over the thaw, same pace as the ice): each pass closes
        // <paramref name="frac"/> of every marked texel's gap to its base G; frac 1 (or within 0.002) = unset + mark cleared.
        private static void MeltOut(TerrainComp tc, Heightmap hm, bool[] mod, Color[] paint, float frac)
        {
            byte[] hmMarks = null;
            try { hmMarks = s_trailCutMask(tc); } catch { }
            if (hmMarks == null) return;
            int melted = 0, eased = 0, kept = 0; float biggest = 0f;
            for (int i = 0; i < mod.Length && i < paint.Length; i++)
            {
                if (!mod[i] || (i >> 3) >= hmMarks.Length || (hmMarks[i >> 3] & (1 << (i & 7))) == 0) continue;
                Color c = paint[i];
                float baseG = Mathf.Max(0f, SnowBaseMask.BaseG(hm, i));
                if (c.b > 0.1f) continue;   // 0.2.1156: paving keeps its paint
                if (baseG > 0f) { kept++; continue; }   // 0.2.1118: a dent in snow that still exists: the refill's, not the thaw's
                float g = frac >= 1f ? baseG : c.g + (baseG - c.g) * frac;
                float mud = frac >= 1f ? 0f : c.r * (1f - frac);   // 0.2.1155: the trail's dirt (r) dries with it, so the texel is back to what was under it when unset
                if (Mathf.Abs(g - baseG) < 0.002f && mud < 0.01f)
                {
                    mod[i] = false; melted++;
                    try { s_clearTrailCut?.Invoke(tc, i); } catch { }
                }
                else { biggest = Mathf.Max(biggest, Mathf.Max(Mathf.Abs(g - c.g), Mathf.Abs(mud - c.r))); c.g = g; c.r = mud; paint[i] = c; eased++; }
            }
            s_meltedOut += melted; s_meltEased += eased; s_meltKept += kept;
            if (melted > 0 || biggest >= VisibleStep || eased > 0 && Accumulate(tc, biggest))
            {
                try { s_save?.Invoke(tc, new object[] { false }); s_saves++; } catch (Exception e) { FiresLogger.LogDebug($"[Snow] refill save failed: {e.Message}"); }
                hm.Poke(1, paintOnly: true); s_pending.Remove(tc);
            }
        }
        private static int s_staleUnset; private static int s_meltEased, s_meltKept;   // 0.2.1118: s_meltKept = marked texels left to the refill, on snow that still exists
        private const float RefillFullSeconds = 1800f, RefillSnowRate = 1f, RefillStormRate = 2f;   // 0.2.1119: FAT's BaseRefillSeconds, SnowRefillRate, StormRefillRate
        private static bool s_snowingNow, s_stormNow;   // set by RateCmPerMin, from Vedr's shown weather when it runs

        // 0.2.1082: the thaw, read from Vedr's calendar exactly as FAT 1323's IceField does (VedrApi.SeasonIndex 0 Spring / 1 Summer /
        // 2 Fall / 3 Winter, DaysPerSeason, SeasonProgressSmooth; ApiVersion >= 8; length fallback 10 days): Spring = marked trails eased
        // so they reach base by 0.35 x the season length (FAT's SpringOpenFrac, ~day 3.5 of 10); Summer / Fall = at once (a forced jump
        // too); Winter = no melt-out (the refill runs). No calendar = 1081's rule (no snow cover in the zone = at once).
        // Returns false = no melt-out; frac = the share of the gap closed this pass.
        private const float MudFadeFrac = 0.5f;   // 0.2.1155 (Fire: "the trials remain as the snow melts into mid spring and dry up and return to the proper terrain"): mid spring; the ice keeps SnowField.SpringOpenFrac
        private static bool Thawing(Heightmap hm, float dtSec, out float frac)
        {
            frac = 0f;
            int idx = SnowField.VedrSeason(out int days, out float elapsed);   // 0.2.1083: the one season read
            if (idx == 3) return false;
            if (idx == 1 || idx == 2) { frac = 1f; return true; }
            if (idx == 0)
            {
                float dayLen = EnvMan.instance != null && EnvMan.instance.m_dayLengthSec > 0 ? EnvMan.instance.m_dayLengthSec : 1800f;
                float left = MudFadeFrac * days - elapsed;
                frac = left <= 0f ? 1f : Mathf.Clamp01(dtSec / dayLen / left);
                return true;
            }
            if (SnowField.ZoneHasSnow(hm.transform.position)) return false;
            frac = 1f; return true;
        }


        // 0.2.1066 (lead GO, agreed with [worldgen] for FAT 1312: ONE refill owner for voxel paint): on a zone FAT's voxel terrain owns, FAT's
        // convention holds - its trail ops write G UP (1 = cut) and it draws cut = saturate((edited G - base G) / (1 - base G)). Here edited
        // texels (r and b under 0.1) above their base G move DOWN toward it at the weather rate and are unset at the base. Saved + synced
        // EVERY pass that moved anything (the owner's TerrainComp is the one truth, so a peer never re-sends an old G); only the redraw is
        // throttled to a visible step. Base G = the build data's (vanilla world gen here: SnowBaseMask leaves FAT zones alone since 1062).
        private static void VoxelPass(TerrainComp tc, Heightmap hm, bool[] mod, Color[] paint, byte[] marks, float step, long t0)
        {
            // (0.2.1072: the depth-relative rate was taken out again - Fire: the rate is fine, "it just hadnt snowed"; the 1066 rate stands)
            int moved = 0, unset = 0; float biggest = 0f;
            for (int i = 0; i < mod.Length && i < paint.Length; i++)
            {
                if (!mod[i]) continue;
                if ((i >> 3) >= marks.Length || (marks[i >> 3] & (1 << (i & 7))) == 0) continue;   // 0.2.1079: not a trail cut (a garden, a pile): never touched
                Color c = paint[i];
                if (c.r > 0.1f || c.b > 0.1f) continue;   // dirt / paving: the player's
                float baseG = Mathf.Max(0f, SnowBaseMask.BaseG(hm, i));
                if (c.g <= baseG) continue;   // no cut here
                float g = Mathf.MoveTowards(c.g, baseG, step);
                biggest = Mathf.Max(biggest, c.g - g);
                if (g - baseG < 0.002f && c.r < 0.01f && c.b < 0.01f) { mod[i] = false; unset++; try { s_clearTrailCut?.Invoke(tc, i); } catch { } }   // 0.2.1079: back at base = no longer a cut
                else { c.g = g; paint[i] = c; moved++; }
            }
            s_passes++; s_voxelPasses++; s_voxelMoved += moved; s_voxelUnset += unset;
            s_voxelStepSum += step; s_voxelStepN++;
            // 0.2.1072: save + sync (and redraw) only on a visible step or an unset - not every pass (225 full TerrainComp writes per 30 s)
            if ((moved > 0 || unset > 0) && (unset > 0 || biggest >= VisibleStep || Accumulate(tc, biggest)))
            {
                long s0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try { s_save?.Invoke(tc, new object[] { false }); s_saves++; } catch (Exception e) { FiresLogger.LogDebug($"[Snow] refill save failed: {e.Message}"); }
                s_saveMs += (System.Diagnostics.Stopwatch.GetTimestamp() - s0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                hm.Poke(1, paintOnly: true); s_pending.Remove(tc);
            }
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            s_ms += ms; if (ms > s_maxMs) s_maxMs = ms;
        }
        private static int s_voxelPasses, s_voxelMoved, s_voxelUnset, s_voxelStepN; private static double s_voxelStepSum, s_saveMs;   // 0.2.1072

        // small steps add up until they are visible, then one save + repaint
        private static readonly Dictionary<TerrainComp, float> s_pending = new Dictionary<TerrainComp, float>();
        private static bool Accumulate(TerrainComp tc, float step)
        {
            s_pending.TryGetValue(tc, out float acc); acc += step;
            if (acc >= VisibleStep) return true;
            s_pending[tc] = acc; return false;
        }

        private static string s_weather = "calm";
        private static float RateCmPerMin()
        {
            // 0.2.1067 ([worldgen] / [seasons]: the real API is Vedr 0.8.69+, ApiVersion >= 8, type "Vedr.VedrApi, Norger.Vedr":
            // static bool GetShownWeather(out string name, out bool isSnowing, out float intensity); intensity: storm / blizzard 1, snow 0.6,
            // light 0.3, none 0): storm = intensity >= 1, snowing = isSnowing, else calm. Without Vedr (or false): the environment's name.
            if (ShownWeather(out string name, out bool snowing, out float intensity))
            {
                bool vStorm = intensity >= 1f;
                s_weather = (vStorm ? "snowstorm" : snowing ? "snowfall" : "calm") + $" (Vedr shown '{name}', {intensity:0.0})";
                s_snowingNow = snowing || vStorm; s_stormNow = vStorm;
                return vStorm ? StormCmPerMin : snowing ? SnowCmPerMin : CalmCmPerMin;
            }
            string env = EnvMan.instance != null ? (EnvMan.instance.GetCurrentEnvironment()?.m_name ?? "") : "";
            bool storm = env.IndexOf("storm", StringComparison.OrdinalIgnoreCase) >= 0 && env.IndexOf("snow", StringComparison.OrdinalIgnoreCase) >= 0;
            bool snow = storm || env.IndexOf("snow", StringComparison.OrdinalIgnoreCase) >= 0;
            s_weather = (storm ? "snowstorm" : snow ? "snowfall" : "calm") + $" (environment '{env}')";
            s_snowingNow = snow; s_stormNow = storm;
            return storm ? StormCmPerMin : snow ? SnowCmPerMin : CalmCmPerMin;
        }

        private static System.Reflection.MethodInfo s_shown; private static bool s_shownTried;
        private static bool ShownWeather(out string name, out bool snowing, out float intensity)
        {
            name = null; snowing = false; intensity = 0f;
            if (!s_shownTried)
            {
                s_shownTried = true;
                try
                {
                    var t = Type.GetType("Vedr.VedrApi, Norger.Vedr") ?? FiresCore.Compat.OptionalTypes.Find("Vedr.VedrApi");
                    int api = -1;
                    if (t != null)
                    {
                        var f = AccessTools.Field(t, "ApiVersion"); var pr = f == null ? AccessTools.Property(t, "ApiVersion") : null;
                        object v = f != null ? f.GetValue(null) : pr?.GetValue(null, null);
                        if (v != null) api = Convert.ToInt32(v);
                        var m = t.GetMethod("GetShownWeather", new[] { typeof(string).MakeByRefType(), typeof(bool).MakeByRefType(), typeof(float).MakeByRefType() });
                        if (api >= 8 && m != null && m.IsStatic && m.ReturnType == typeof(bool)) s_shown = m;
                    }
                    FiresLogger.LogDebug($"[Snow] refill weather: {(s_shown != null ? $"Vedr.VedrApi.GetShownWeather bound (ApiVersion {api})" : $"Vedr shown weather not bound ({(t == null ? "no Vedr.VedrApi" : $"ApiVersion {api}")}); the environment is used")}");
                }
                catch (Exception e) { s_shown = null; FiresLogger.LogDebug("[Snow] refill weather bind failed: " + e.Message); }
            }
            if (s_shown == null) return false;
            try
            {
                var args = new object[] { null, false, 0f };
                if (!(bool)s_shown.Invoke(null, args)) return false;
                name = args[0] as string; snowing = args[1] is bool b && b; intensity = args[2] is float x ? x : 0f;
                return true;
            }
            catch { return false; }
        }
        private static void Say()
        {
            if (Time.unscaledTime < s_sayAt) return;
            if (s_sayAt > 0f && s_passes > 0) FiresLogger.LogDebug($"[Snow] refill last 30 s: {s_passes} pass(es), {s_moved} texel(s) moved, {s_unset} unset, {s_saves} save(s), {s_ms:0.0} ms (max {s_maxMs:0.00} ms), weather {s_weather} ({RateCmPerMin():0} cm/min); heightmap refill {(s_stormNow ? "2x, storm" : s_snowingNow ? "1x, snowing" : "off, not snowing")}; melt-out {s_meltKept} kept (snow still there); voxel (FAT zones): {s_voxelPasses} pass(es), {s_voxelMoved} cut texel(s) eased, {s_voxelUnset} back at base ({s_meltEased} trail texel(s) easing in the thaw, {s_meltedOut} melted out), mean step {(s_voxelStepN > 0 ? s_voxelStepSum / s_voxelStepN : 0):0.0000} G per pass, saves {s_saveMs:0.0} ms");
            if (s_staleUnset > 0) { FiresLogger.LogDebug($"[Snow] refill: {s_staleUnset} unmarked empty edited texel(s) put back to the base"); s_staleUnset = 0; }
            s_sayAt = Time.unscaledTime + 30f; s_passes = s_moved = s_unset = s_saves = 0; s_ms = s_maxMs = 0; s_voxelPasses = s_voxelMoved = s_voxelUnset = s_voxelStepN = s_meltKept = s_meltedOut = s_meltEased = 0; s_voxelStepSum = s_saveMs = 0;
        }
    }
}
