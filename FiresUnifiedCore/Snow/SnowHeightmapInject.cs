using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// Vanilla terrain: after Heightmap.ApplyModifiers has built the chunk's paint mask (world gen + TerrainComp player paint), raise
    /// the Deep North snow-depth channel (G) in this peer's LOCAL runtime texture by the dynamic snow, minus fires and the path mask,
    /// then Apply. Nothing is written to TerrainComp / TerrainModifier (saved, synced data), so there is no traffic or save churn and
    /// players' painted ground is never overwritten. Vanilla reads G as snow depth only in the Deep North (GetHeightOffset 0.1 to 2.1 m,
    /// the SnowDeep / SnowVeryDeep ground), so only Deep North cells are raised; elsewhere G means cultivated. Paved cells (B) stay
    /// clear. FAT voxel zones are skipped (FAT pulls SnowField.FillZoneDepth into its own texture).
    /// </summary>
    internal static class SnowHeightmapInject
    {
        // Vanilla: snow height = G x Character.m_deepSnowSlowMaxHeight (2 m); world gen gives Deep North G 0.3..0.6, so its deepest
        // is G 0.6 = 1.2 m, waist deep (Fire 10-24: full winter = waist deep = vanilla Deep North max). Full cover + full build-up = MaxG.
        private const float MaxG = 0.6f;

        // 0.2.829 ([seasons], Fire 10-26: "usually deep snow would be 1.2m with drifts as deep as 2m or heavy prolonged snowfall
        // allowing it to accumulate up to 2m"). Vanilla snow height = G x 2 m (Character.m_deepSnowSlowMaxHeight); the sink offset
        // is Lerp(0.1, 2.1, G). So in a snow base:
        //   base   G = MaxG (0.6 = 1.2 m) x base x the season cover (summer floor 0.6 -> 0.72 m, deep winter 1.2 m)
        //   heavy  + up to HeavyG (0.35 = +0.7 m) as the zone's build-up passes HeavyFrom (prolonged snow / storms; thaws back)
        //   drift  + up to DriftG (0.35) in the LEE (Fire 10-26: "make drifts pile up behind walls and slopes"): downwind of a rise
        //          or of a wall / building / rock / tree (SnowDrift, from vanilla's wind), plus a little world noise on top
        //          (NoiseShare of DriftG, DriftScale m, its top DriftShare); windward faces lose up to WindwardCut of the base
        //   cap    DeepCapG 0.95 (= 1.9 m on the 2 m scale, ~2 m sink); fires and dug paths clear all of it (keep)
        private const float HeavyFrom = 0.6f, HeavyG = 0.35f, DriftG = 0.35f, NoiseShare = 0.25f, WindwardCut = 0.5f, DriftScale = 28f, DriftShare = 0.35f, DeepCapG = 0.95f;

        /// <summary>Target paint-mask G (snow depth) at a point before fires / paths.</summary>
        /// <summary>0.2.840: the heavy-snowfall share now (one value for every chunk, from the viewer's build-up).</summary>
        // 0.2.853: accumulated snowfall 0..1 (the viewer's build-up from its baseline to full)
        internal const float PackAtBaseline = 0.5f;
        internal static float Accumulation => Mathf.Clamp01((SnowField.ViewerBuildUp - SnowField.BuildUpBaseline) / (1f - SnowField.BuildUpBaseline));

        internal static float HeavyNow() => Mathf.Clamp01((SnowField.ViewerBuildUp - HeavyFrom) / (1f - HeavyFrom));

        internal static float DepthG(Vector3 p, float heavy, float lee, float windward) => DepthGFor(p, heavy, lee, windward, SnowField.DeepGroundWeight(p));

        // 0.2.914 (Fire 10-27 "Mountain biomes should get snow build up as well ... a lower Max depth"): Mountain ground runs the SAME model
        // (season cover, build-up, heavy snow, drifts) on its own weight, scaled by share = Max depth Mountain / the Deep North cap (1.9 m)
        // and capped at Max depth Mountain; Deep North is untouched where no Mountain cell is near. The higher of the two wins at a seam.
        internal const float DeepCapMetres = DeepCapG * 2f;
        private static bool s_mountainLogged;
        internal static float TexelG(Vector3 p, float heavy, float lee, float windward, float dnW, float mW)
        {
            float g = dnW > 0f ? DepthGFor(p, heavy, lee, windward, dnW) : 0f;
            if (mW <= 0f) return g;
            float max = SnowConfig.MountainMaxDepth;
            if (max <= 0f) return g;
            float share = max / DeepCapMetres, capG = max / 2f;   // the 2 m scale (G x 2 m)
            if (!s_mountainLogged) { s_mountainLogged = true; FiresLogger.LogInfo($"[Snow] Mountain buildup: cap {max:0.00} m (share {share:0.00} of the Deep North model; visual + trails only, no Deep North sink / slow)"); }
            return Mathf.Max(g, Mathf.Min(capG, share * DepthGFor(p, heavy, lee, windward, mW)));
        }

        internal static float DepthGFor(Vector3 p, float heavy, float lee, float windward, float deepW)
        {
            // 0.2.830: the base fades over the 4 m cells at a snow base''s edge (bilinear), no 4 m steps
            return DepthGWithBase(p, heavy, lee, windward, SnowField.BaseWeight(p) * deepW);
        }

        // 0.2.1099: the depth once the base weight is known (the far LOD passes its own, no 4 m cells)
        internal static float DepthGWithBase(Vector3 p, float heavy, float lee, float windward, float b)
        {
            if (b <= 0f) return 0f;
            float cover = Mathf.Clamp01(SnowField.GlobalCover);
            float n = Mathf.PerlinNoise(p.x / DriftScale + 311.7f, p.z / DriftScale + 97.3f);
            float drift = Mathf.Clamp01((n - (1f - DriftShare)) / DriftShare);
            drift = drift * drift * (3f - 2f * drift);   // smooth drift edges
            float driftAll = Mathf.Clamp01(lee * (1f - NoiseShare) + drift * NoiseShare);
            // 0.2.853 (Fire 10-27 "grow in snowfall"): the base pack grows with the accumulated snowfall, half deep at the baseline build-up
            // -> full (1.2 m) when filled; heavy snow (build-up past HeavyFrom) and drifts add on top, capped at DeepCapG (1.9 m)
            float pack = Mathf.Lerp(PackAtBaseline, 1f, Accumulation);
            return Mathf.Min(DeepCapG, MaxG * b * cover * pack * (1f - WindwardCut * windward) + HeavyG * heavy * cover + DriftG * driftAll * cover);
        }

        private static readonly AccessTools.FieldRef<Heightmap, Texture2D> s_paintMask = AccessTools.FieldRefAccess<Heightmap, Texture2D>("m_paintMask");
        private static int s_logged;

        [HarmonyPatch(typeof(Heightmap), "ApplyModifiers")]
        private static class Heightmap_ApplyModifiers_Snow
        {
            private static void Postfix(Heightmap __instance)
            {
                try { SnowBaseMask.PushBaseTexture(__instance); SnowBaseMask.ClampPaint(__instance); } catch (Exception e) { FiresLogger.LogDebug($"[Snow] paint clamp failed: {e.Message}"); }
                try { PaintChanged(__instance); } catch { }
                try { long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); Inject(__instance); NoteSlow("terrain inject", t0, 8.0); }
                catch (Exception e) { if (s_logged++ < 3) FiresLogger.LogDebug($"[Snow] terrain inject skipped: {e.Message}"); }
            }
        }

        // 0.2.1024 ([saddles] 10-30: another player's cultivate / path paint reaches a peer as a TerrainComp sync, heights unchanged, and
        // the paved rule changes with it): every peer rebuilds the chunk here, so the chunk's player paint (the same m_modifiedPaint +
        // m_paintMask the 0.2.978 trail keep reads) is hashed; a different hash raises CellsChanged for the zone. The first sight of a
        // chunk only records it (the zone-load footprint build already announces)
        private static readonly Dictionary<Heightmap, long> s_paintSig = new Dictionary<Heightmap, long>();
        private static void PaintChanged(Heightmap hm)
        {
            if (hm == null || !SnowField.Enabled) return;
            var tc = TerrainComp.FindTerrainCompiler(hm.transform.position);
            long sig = 17;
            if (tc != null && tc.m_modifiedPaint != null && tc.m_paintMask != null)
                for (int i = 0; i < tc.m_modifiedPaint.Length && i < tc.m_paintMask.Length; i++)
                    if (tc.m_modifiedPaint[i]) { Color p = tc.m_paintMask[i]; sig = sig * 31 + i; sig = sig * 31 + (int)(p.r * 255f) * 65536 + (int)(p.g * 255f) * 256 + (int)(p.b * 255f); }
            if (s_paintSig.Count > 1024) s_paintSig.Clear();
            bool seen = s_paintSig.TryGetValue(hm, out long was);
            s_paintSig[hm] = sig;
            if (seen && was != sig) SnowField.NoteZoneCellsChanged(ZoneSystem.GetZone(hm.transform.position));
        }

        private static void Inject(Heightmap hm)
        {
            if (!SnowField.CoreGroundOn || (SnowField.GlobalCover <= 0f && SnowField.WinterCover <= 0f) || hm == null) return;   // 0.2.1197: CoreGroundOn
            if (SnowConfig.NaturalSnow) return;   // 0.2.1061: the snow is in the base mask (SnowBaseMask); no paint-G write, no SnowTrail keep
            Vector3 c = hm.transform.position;
            if (SnowFat.OwnsZone(ZoneSystem.GetZone(c))) return;
            int dn = DeepNorthCorners(hm);
            if (dn == 0 && MountainCorners(hm) == 0 && WinterCorners(hm) == 0) return;   // 0.2.914: Mountain chunks get their (lower) buildup too; 0.2.1052: + seasonal winter ground
            // 0.2.826 ([seasons], Deep North perf): a chunk whose centre + 4 corners are all Deep North skips the per-texel GetBiome
            // (the full patched chain, once per texel on every build / poke); mixed chunks keep the exact per-texel test.
            bool allDn = dn == 5;
            var tex = s_paintMask(hm);
            if (tex == null) return;
            // prolonged heavy snowfall: the zone's build-up above HeavyFrom (it grows in snow / storms, settles back in the thaw)
            // 0.2.830: ONE build-up for every chunk (the viewer's; Fire: "hard cut"): a per-zone value stepped +0.35 G at zone borders
            float heavy = Mathf.Clamp01((SnowField.ViewerBuildUp - HeavyFrom) / (1f - HeavyFrom));
            // 0.2.959 ([ghost], [generator] zone-border check 10-28: 9-13 ms inside the 75-81 ms heightmap rebuild frames): the per-texel
            // pass is queued and sliced by rows under the 8 ms budget (Tick), same result a few frames later. A rebuild of a chunk
            // already queued restarts its job from the fresh vanilla mask.
            for (int j = s_jobs.Count - 1; j >= 0; j--) if (s_jobs[j].Hm == hm) s_jobs.RemoveAt(j);
            // 0.2.978 (Fire 10-29: "fix it in Core now", the walk trails flicker; [seasons] + [saddles]): the chunk's player paint (walk
            // trails, cultivate, paths) is read once here; RunRows keeps those texels' vanilla G instead of raising them back up every
            // re-inject (the next walk op lowered them again: the flash)
            bool[] painted = null; int paintW = 0;
            var tc = TerrainComp.FindTerrainCompiler(c);
            if (tc != null && tc.m_modifiedPaint != null) { painted = tc.m_modifiedPaint; paintW = tc.m_width + 1; }
            EdgePaintNeighbours(hm, painted, paintW);   // 0.2.1002
            var job = new Job { Hm = hm, Tex = tex, Px = tex.GetPixels(), AllDn = allDn, Heavy = heavy, Painted = painted, PaintW = paintW };
            ProbeSet(job);   // 0.2.1053 proof: the texel under the local player, if this chunk covers it
            // 0.2.979 ([seasons] frame proof, Fire's clip: "a combo of snow accumulation, and the footsteps"): a sliced job leaves the
            // chunk drawn with the plain vanilla mask until Tick reaches it - the pop. Chunks near the camera / player run in-frame here,
            // so the un-snowed mask is never drawn; far chunks stay sliced.
            if (NearViewer(c))
            {
                // 0.2.1055 (lead: "in-frame near the camera must also respect 8 ms"): near chunks share one budget per frame; a chunk that
                // does not finish inside it goes to the FRONT of the sliced queue (the next frames finish it first)
                if (Time.frameCount != s_inFrameFrame) { s_inFrameFrame = Time.frameCount; s_inFrameStart = System.Diagnostics.Stopwatch.GetTimestamp(); }
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                bool doneNow = MsSince(s_inFrameStart) < TickBudgetMs && RunRows(job, s_inFrameStart);
                job.Ms += MsSince(t0);
                s_inFrameMs += (float)MsSince(t0);
                if (doneNow) { Finish(job); s_inFrame++; return; }
                s_jobs.Insert(0, job); s_inFrameCut++;
                return;
            }
            s_jobs.Add(job);
        }

        // 0.2.959: one queued chunk inject; Row is the next row to paint, the texture is written once when the last row is done
        private sealed class Job
        {
            public Heightmap Hm; public Texture2D Tex; public Color[] Px; public bool AllDn; public float Heavy;
            public int Row, Col, Raised; public double Ms;   // 0.2.1055: + Col (resume mid-row)
            public bool[] Painted; public int PaintW, Kept, EdgeAligned;   // 0.2.978; 0.2.1001 EdgeAligned
            public int EdgeExact;   // 0.2.1050: outer-ring texels of an all-Deep-North chunk computed exactly (not the shortcut)
            public int Trailed;   // 0.2.1051: texels packed by a walk trail
            public int Winter;   // 0.2.1052: texels raised by seasonal winter snow (deeper than the deep model)
            public int TrailLowered;   // 0.2.1053: texels whose written G went DOWN under a trail
            // 0.2.1053 proof (lead, Fire's -358,99): the texel under the local player, when this chunk covers it
            public int ProbeX = -1, ProbeY = -1; public float ProbeBase = -1f, ProbeKeep = -1f, ProbeTarget = -1f; public string ProbeWhy;
            public string ProbeColour; public int PaintSnowed;   // 0.2.1057: the stored colour at the probe; modified texels that got snow (not ground work)
        }
        private static readonly System.Collections.Generic.List<Job> s_jobs = new System.Collections.Generic.List<Job>();
        private const double TickBudgetMs = 6.0;   // under the 8 ms snow budget, leaving room for the SetPixels / Apply
        private static int s_sliced;

        private static double MsSince(long ticks) => (System.Diagnostics.Stopwatch.GetTimestamp() - ticks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>0.2.959: runs the queued chunk injects (SnowTicker.Update), whole rows until the frame's budget is spent.</summary>
        internal static void Tick()
        {
            if (s_jobs.Count == 0) return;
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                while (s_jobs.Count > 0 && MsSince(start) < TickBudgetMs)
                {
                    var job = s_jobs[0];
                    if (job.Hm == null || job.Tex == null || s_paintMask(job.Hm) != job.Tex) { s_jobs.RemoveAt(0); continue; }   // unloaded / mask replaced
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool done = RunRows(job, start);
                    job.Ms += MsSince(t0);
                    if (done) { s_jobs.RemoveAt(0); Finish(job); }
                }
                if (s_jobs.Count > 0) s_sliced++;
            }
            catch (Exception e) { if (s_jobs.Count > 0) s_jobs.RemoveAt(0); if (s_logged++ < 3) FiresLogger.LogDebug($"[Snow] terrain inject skipped: {e.Message}"); }
            NoteSlow("terrain inject tick", start, 8.0, $"{s_jobs.Count} queued");
        }

        private static bool RunRows(Job job, long frameStart)
        {
            var hm = job.Hm; var px = job.Px;
            int w = job.Tex.width, h = job.Tex.height;
            bool allDn = job.AllDn; float heavy = job.Heavy;
            int done = 0;
            while (job.Row < h)
            {
                int y = job.Row;
                for (int x = job.Col; x < w; x++)
                {
                    // 0.2.1055 (lead, Fire's live 1054 log: 201 "slow terrain inject 11-27 ms (budget 8)" - the budget was checked once per
                    // 65-texel row): the budget is checked every 16 texels and the job resumes mid-row (job.Col)
                    if ((++done & 15) == 0 && MsSince(frameStart) >= TickBudgetMs) { job.Col = x; return false; }
                    int i = y * w + x;
                    var col = px[i];
                    bool sample = (i & 7) == 0;   // 0.2.1055: the per-call ms split samples every 8th texel (x8 in the summary)
                    long ts = sample ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L, tn;
                    if (x == job.ProbeX && y == job.ProbeY) { job.ProbeBase = col.g; job.ProbeWhy = col.b > 0.5f ? "paved (skipped)" : "skipped before the target (player paint kept, or no snow weight)"; }   // 0.2.1053 proof
                    if (col.b > 0.5f) continue;   // paved: a built road stays clear
                    Vector3 p = hm.VertexMaskToWorld(x, y);
                    // 0.2.1057 (Fire 10-30, lead + [saddles]' dump: 96 of 156 texels near Fire held G 0 = no snow forever - any vanilla paint op
                    // writes TerrainComp's ABSOLUTE colour over the texel (ApplyToHeightmap SetPixel) and marks it modified, so every ground ever
                    // touched by a level / reset / test op lost its snow): a modified texel keeps vanilla's colour only for real ground work -
                    // dirt (R), paved (B), or real cultivation (G outside the real Deep North) - and always in the real Deep North, where G IS
                    // vanilla's additive snow and its snow_walk trail (TerrainComp.cs:576/:727). Everywhere else it gets Core's snow; trails there
                    // are the SnowTrail mask (0.5 m, packed to 20 %, refills over world time).
                    bool painted = job.Painted != null && x < job.PaintW && y < job.PaintW && job.Painted[y * job.PaintW + x];
                    bool realDn = painted && WorldGenerator.IsDeepnorth(p.x, p.z);
                    bool groundWork = painted && (realDn || col.r > 0.5f || col.b > 0.5f || col.g > 0.5f);
                    if (x == job.ProbeX && y == job.ProbeY) job.ProbeColour = $"stored RGB {col.r:0.00}/{col.g:0.00}/{col.b:0.00}, {(painted ? (groundWork ? (realDn ? "real Deep North paint kept" : "ground work kept") : "modified but snow") : "untouched, snow")}";
                    if (painted && !groundWork) job.PaintSnowed++;
                    if (groundWork)   // 0.2.978: player paint keeps vanilla G (0.2.1057: ground work only)
                    {
                        // 0.2.1001 (Fire 10-29: "still slight terrain tearing on zone borders in deep snow"; border line at 8,8800 read
                        // "G 0.486 | G 0.000"): a chunk's outer-ring texel is the SAME world point as the neighbour's edge texel, but each
                        // zone has its own paint mask - kept on one side, raised on the other = a seam. On the outer ring the keep
                        // applies only when the neighbour marks that point painted too, so both sides always agree.
                        bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                        if (!edge || !SnowConfig.P("Match snow at zone edges", true) || NeighbourPainted(hm, p, x, y, w, h)) { job.Kept++; if (x == job.ProbeX && y == job.ProbeY) job.ProbeWhy = "player paint KEPT (vanilla trodden G, snow_walk)"; continue; }
                        job.EdgeAligned++;
                    }
                    // 0.2.1050 (Fire 10-30 RenderDoc pixel history, the teal seam at -358,99: two vanilla zones' shared edge vertex got a
                    // different G on each side): the all-Deep-North shortcut (weight 1, DepthG) is used inside the chunk only; the outer ring
                    // always takes the exact per-texel weights + TexelG, the same answer the neighbour computes for that world point
                    bool ring = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                    bool fast = allDn && !ring;
                    if (allDn && ring) job.EdgeExact++;
                    float dnW = fast ? 1f : SnowField.DeepGroundWeight(p), mW = fast ? 0f : SnowField.MountainWeight(p);   // 0.2.914: + Mountain
                    if (sample) { tn = System.Diagnostics.Stopwatch.GetTimestamp(); s_tWeights += tn - ts; ts = tn; }
                    // 0.2.1052 (Fire 10-30: "paint seasonal-winter ground as snow"): the seasonal winter depth outside the snow biomes
                    // (SnowField.WinterG: winter cover x build-up x shelter, net of dug paths) raises the vanilla ground too
                    float winterG = fast ? 0f : SnowField.WinterG(p);
                    if (sample) { tn = System.Diagnostics.Stopwatch.GetTimestamp(); s_tWinter += tn - ts; ts = tn; s_tSamples++; }
                    if (dnW <= 0f && mW <= 0f && winterG <= 0f) continue;
                    float melt = 1f - SnowField.MeltAt(p);
                    float keep = melt * (1f - SnowPathMask.ClearAt(p));
                    if (sample) { tn = System.Diagnostics.Stopwatch.GetTimestamp(); s_tMeltClear += tn - ts; ts = tn; }
                    SnowDrift.At(p, out float lee, out float windward);
                    if (sample) { tn = System.Diagnostics.Stopwatch.GetTimestamp(); s_tDrift += tn - ts; ts = tn; }
                    // 0.2.1051: walk trails (SnowTrail, 0.5 m) pack Core's raised snow to a fifth of its depth, filling back over world time
                    float trail = SnowTrail.KeepAt(p);
                    if (trail < 1f) job.Trailed++;
                    if (sample) { tn = System.Diagnostics.Stopwatch.GetTimestamp(); s_tTrail += tn - ts; ts = tn; }
                    float deep = dnW > 0f || mW > 0f ? (fast ? DepthG(p, heavy, lee, windward) : TexelG(p, heavy, lee, windward, dnW, mW)) * keep : 0f;
                    if (sample) { tn = System.Diagnostics.Stopwatch.GetTimestamp(); s_tTarget += tn - ts; ts = tn; }
                    if (winterG > deep) job.Winter++;
                    // 0.2.1060 (lead, Fire's RenderDoc: the terrain domain shader samples m_paintMask as R8G8B8A8_SRGB, so drawn snow =
                    // sRGBToLinear(G) x 2.1 - G 0.144 drew 0.04 m, the trail 0.003 m): Core's own snow (the 2 m-scale target, trail-packed) is
                    // written ENCODED so the GPU draws its real height; the real Deep North keeps vanilla's raw G (its own snow, sink and trails)
                    float own = Mathf.Max(deep, winterG * melt) * trail;
                    float g = Mathf.Clamp01(Mathf.Max(col.g * keep, !WorldGenerator.IsDeepnorth(p.x, p.z) ? Encode(own) : own));
                    if (trail < 1f && g < col.g - 0.004f) job.TrailLowered++;   // 0.2.1053 proof: written below the base G
                    if (x == job.ProbeX && y == job.ProbeY) { job.ProbeKeep = trail; job.ProbeTarget = Mathf.Max(deep, winterG * melt); job.ProbeWhy = "computed"; }   // 0.2.1053 proof
                    if (Mathf.Abs(g - col.g) < 0.004f) continue;
                    col.g = g;
                    px[i] = col;
                    job.Raised++;
                }
                job.Col = 0; job.Row++;
            }
            return true;
        }

        /// <summary>0.2.1060: Core's snow G (2 m scale) -> the stored byte whose sRGB decode on the GPU draws that height
        /// (LinearToGamma(h / 2.1), h = g x 2 m). Off ([Snow] Draw snow at its real height): unchanged.</summary>
        internal static float Encode(float g) => SnowConfig.RealHeight ? Mathf.LinearToGammaSpace(Mathf.Clamp01(g * SnowField.MaxDepthMetres / 2.1f)) : g;
        /// <summary>0.2.1060: a stored G Core wrote -> the fraction of 2.1 m the GPU draws (what the sink / footsteps / slow must use).</summary>
        /// <summary>0.2.1138 (Fire chose "Vanilla curve, deep sink"): the base mask G that vanilla's shader curve, sRGBToLinear(G) x 2.1 m, draws at the model's depth (model G x 2 m): 0.70 m = G 0.61. Raw readers follow vanilla: sink Lerp(0.1, 2.1, G) = 1.33 m there, as he accepted.</summary>
        internal static float EncodeDrawn(float g) => g <= 0f ? 0f : Mathf.LinearToGammaSpace(Mathf.Clamp01(g * SnowField.MaxDepthMetres / 2.1f));
        internal static float Decode(float g) => SnowConfig.RealHeight ? Mathf.GammaToLinearSpace(Mathf.Clamp01(g)) : g;

        // 0.2.1055: the sampled per-call split (Stopwatch ticks of every 8th texel), logged with the 10 s inject summary
        private static long s_tWeights, s_tWinter, s_tMeltClear, s_tDrift, s_tTrail, s_tTarget; private static int s_tSamples;

        // 0.2.1002 (Fire 10-29: "snow paths cause massive tears as well so we just need to make sure that a path that edits one zone
        // properly communicates with the adjacent zone so they both redraw at once properly"): when the player paint on a chunk's
        // OUTER RING changed since its last inject (a trail / path reaching the zone edge), the neighbour chunk(s) on that side are
        // rebuilt + re-injected in the SAME step (Poke now, not later), so both sides of the shared edge are redrawn together. A
        // re-entry guard stops the neighbour from poking back. Only the edge-ring change triggers it, never an interior trail step.
        private static readonly Dictionary<Heightmap, int[]> s_edgeSig = new Dictionary<Heightmap, int[]>();
        private static bool s_rebaking;
        private static float s_nextEdgeWhy;
        private static void EdgePaintNeighbours(Heightmap hm, bool[] painted, int pw)
        {
            if (s_rebaking || !SnowConfig.P("Match snow at zone edges", true)) return;
            if (s_edgeSig.Count > 2048) s_edgeSig.Clear();   // unloaded chunks: start over (a first inject never re-bakes)
            // one signature per edge: south (row 0), north (last row), west (column 0), east (last column)
            var sig = new int[] { 17, 17, 17, 17 };
            if (painted != null)
                for (int i = 0; i < pw; i++)
                {
                    if (painted[i]) sig[0] = sig[0] * 31 + i + 1;
                    if (painted[(pw - 1) * pw + i]) sig[1] = sig[1] * 31 + i + 1;
                    if (painted[i * pw]) sig[2] = sig[2] * 31 + i + 1;
                    if (painted[i * pw + pw - 1]) sig[3] = sig[3] * 31 + i + 1;
                }
            bool known = s_edgeSig.TryGetValue(hm, out var was);
            s_edgeSig[hm] = sig;
            if (!known) return;   // the first inject of this chunk: nothing changed yet
            float size = hm.m_width * hm.m_scale; Vector3 c = hm.transform.position;
            var side = new[] { new Vector3(0f, 0f, -size), new Vector3(0f, 0f, size), new Vector3(-size, 0f, 0f), new Vector3(size, 0f, 0f) };
            int n = 0; long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            s_rebaking = true;
            try
            {
                for (int e = 0; e < 4; e++)
                {
                    if (sig[e] == was[e]) continue;   // this edge's paint did not change
                    var nhm = Heightmap.FindHeightmap(c + side[e]);
                    if (nhm == null || nhm == hm) continue;
                    nhm.Poke(0);   // regenerate now: vanilla rebuilds its mesh from the shared TerrainComp edits, then this inject runs for it
                    n++;
                }
            }
            finally { s_rebaking = false; }
            if (n > 0 && Time.unscaledTime >= s_nextEdgeWhy)
            {
                s_nextEdgeWhy = Time.unscaledTime + 2f;
                var z = ZoneSystem.GetZone(c);
                FiresLogger.LogDebug($"[Snow] edge paint change in zone {z.x},{z.y} -> re-baked {n} neighbour chunk(s) in the same step ({MsSince(t0):0.0} ms)");
            }
        }

        // 0.2.1001: is the world point p (an outer-ring texel of hm) player-painted in the NEIGHBOUR zone's TerrainComp too? A corner
        // texel has two neighbours; it counts as painted only when every neighbour that holds it agrees (else both sides raise).
        internal static bool NeighbourPainted(Heightmap hm, Vector3 p, int x, int y, int w, int h)
        {
            float step = hm.m_scale;
            var outs = new List<Vector3>(2);
            if (x == 0) outs.Add(new Vector3(-step, 0f, 0f)); else if (x == w - 1) outs.Add(new Vector3(step, 0f, 0f));
            if (y == 0) outs.Add(new Vector3(0f, 0f, -step)); else if (y == h - 1) outs.Add(new Vector3(0f, 0f, step));
            foreach (var o in outs)
            {
                var nhm = Heightmap.FindHeightmap(p + o);
                if (nhm == null || nhm == hm) continue;   // no loaded neighbour there: nothing to disagree with
                var ntc = TerrainComp.FindTerrainCompiler(nhm.transform.position);
                if (ntc == null || ntc.m_modifiedPaint == null) return false;
                nhm.WorldToVertexMask(p, out int nx, out int ny);
                int nw = ntc.m_width + 1;
                if (nx < 0 || ny < 0 || nx >= nw || ny >= nw || !ntc.m_modifiedPaint[ny * nw + nx]) return false;
            }
            return true;
        }

        private static void Finish(Job job)
        {
            if (job.Raised > 0)
            {
                job.Tex.SetPixels(job.Px);
                job.Tex.Apply();
            }
            s_kept += job.Kept; s_edgeAligned += job.EdgeAligned;   // 0.2.1001
            s_edgeExact += job.EdgeExact;   // 0.2.1050
            s_trailed += job.Trailed;   // 0.2.1051
            s_winter += job.Winter;   // 0.2.1052
            s_trailLowered += job.TrailLowered;   // 0.2.1053
            s_paintSnowed += job.PaintSnowed;   // 0.2.1057
            EdgeMatchNeighbours(job);   // 0.2.1050
            ProbeLog(job);   // 0.2.1053 proof
            s_chunks++; s_texels += job.Px.Length; s_raised += job.Raised; s_ms += (float)job.Ms; if (job.AllDn) s_fast++;
            if (Time.unscaledTime >= s_summaryAt)
            {
                if (s_inFrame > 0) FiresLogger.LogDebug($"[Snow] inject: {s_inFrame} chunk(s) done in-frame near the camera ({s_inFrameMs:0.0} ms), {s_chunks - s_inFrame} sliced");
                if (s_inFrameCut > 0) FiresLogger.LogDebug($"[Snow] inject: {s_inFrameCut} near chunk(s) over the in-frame budget moved to the front of the sliced queue");
                if (s_tSamples > 0) { double f = 8000.0 / System.Diagnostics.Stopwatch.Frequency; FiresLogger.LogDebug($"[Snow] inject ms split last 10 s (sampled 1 in 8, x8): weights (DeepGround+Mountain) {s_tWeights * f:0.0}, winter (WinterG) {s_tWinter * f:0.0}, melt+path clear {s_tMeltClear * f:0.0}, drift {s_tDrift * f:0.0}, trail {s_tTrail * f:0.0}, target (TexelG/DepthG) {s_tTarget * f:0.0} ms over {s_tSamples * 8} texel(s)"); }
                s_tWeights = s_tWinter = s_tMeltClear = s_tDrift = s_tTrail = s_tTarget = 0; s_tSamples = 0; s_inFrameCut = 0;
                s_inFrame = 0; s_inFrameMs = 0f;
                FiresLogger.LogDebug($"[Snow] inject last 10 s: {s_chunks} chunk(s) ({s_fast} all Deep North, no per-texel biome), {s_texels} texel(s), {s_raised} raised, {s_kept} painted texel(s) kept (trails / player paint), not raised, {s_ms:0.0} ms; sliced over frames {s_sliced} time(s) (budget {TickBudgetMs:0} ms per frame, {s_jobs.Count} queued)");
                if (s_edgeAligned > 0) FiresLogger.LogDebug($"[Snow] zone edges matched last 10 s: {s_edgeAligned} edge texel(s) raised that one zone alone marked as player paint (the neighbour did not; they made a seam)");
                if (s_paintSnowed > 0) FiresLogger.LogDebug($"[Snow] modified texels snowed last 10 s: {s_paintSnowed} (touched by non-ground-work paint ops; dirt, paved, cultivated and real Deep North keep vanilla)");
                if (s_winter > 0) FiresLogger.LogDebug($"[Snow] seasonal winter snow raised {s_winter} texel(s) on vanilla ground in the injects of the last 10 s (winter cover {SnowField.WinterCover:0.00})");
                if (s_trailed > 0) FiresLogger.LogDebug($"[Snow] walk trails packed {s_trailed} texel(s) in the injects of the last 10 s, {s_trailLowered} of them written LOWER than the base G (the visible trail)");
                if (s_edgeExact > 0 || s_edgeRebaked > 0) FiresLogger.LogDebug($"[Snow] zone edge G last 10 s: {s_edgeExact} outer-ring texel(s) of all-Deep-North chunks computed exactly; {s_edgeRebaked} neighbour chunk(s) re-baked for a shared-edge G difference (worst {s_edgeWorst:0.000})");
                s_chunks = s_fast = s_texels = s_raised = s_sliced = s_kept = s_edgeAligned = s_edgeExact = s_edgeRebaked = s_trailed = s_winter = s_trailLowered = s_paintSnowed = 0; s_edgeWorst = 0f; s_ms = 0f; s_summaryAt = Time.unscaledTime + 10f;
            }
        }

        // 0.2.1050 (lead, Fire's RenderDoc capture): when a chunk's inject finishes, each loaded neighbour's mask is read at the shared
        // edge (same world points, by WorldToVertexMask). A G difference (one side injected under an older build-up, or by the old
        // shortcut) re-bakes that neighbour now (Poke, as 1002 does for paint), so both sides of the edge match. The re-entry guard stops a
        // neighbour from poking back from its own Finish, and a pair re-bakes at most once per 5 s (the build-up moves slowly).
        // 0.2.1053 proof (lead, Fire's -358,99: "trails never show on vanilla terrain"; no behaviour change): at most once a second, the inject
        // that covers the local player's position logs the texel under the feet - its base G (vanilla + player paint, before the inject), why
        // it was kept or computed, the trail keep, the target and the G actually WRITTEN, the 3x3 written min / max around it, and how many
        // player-painted (snow_walk) texels the chunk kept. Walking should show the written G dropping (or "player paint KEPT").
        private static float s_probeNext;
        private static void ProbeSet(Job job)
        {
            var pl = Player.m_localPlayer;
            if (pl == null || Time.unscaledTime < s_probeNext) return;
            job.Hm.WorldToVertexMask(pl.transform.position, out int x, out int y);
            if (x < 0 || y < 0 || x >= job.Tex.width || y >= job.Tex.height) return;
            job.ProbeX = x; job.ProbeY = y;
        }

        private static void ProbeLog(Job job)
        {
            if (job.ProbeX < 0 || Time.unscaledTime < s_probeNext) return;
            s_probeNext = Time.unscaledTime + 1f;
            int w = job.Tex.width, h = job.Tex.height;
            float written = job.Px[job.ProbeY * w + job.ProbeX].g, lo = 1f, hi = 0f;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = job.ProbeX + dx, y = job.ProbeY + dy;
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    float v = job.Px[y * w + x].g; if (v < lo) lo = v; if (v > hi) hi = v;
                }
            Vector3 p = job.Hm.VertexMaskToWorld(job.ProbeX, job.ProbeY);
            var z = ZoneSystem.GetZone(p);
            FiresLogger.LogDebug($"[Snow] trail proof at ({p.x:0.0},{p.z:0.0}): base G {job.ProbeBase:0.000}, {job.ProbeColour ?? "-"}, {job.ProbeWhy ?? "?"}, KeepAt {job.ProbeKeep:0.00}, target {job.ProbeTarget:0.000}, written G {written:0.000} (drawn {Mathf.GammaToLinearSpace(written) * 2.1f:0.00} m, the GPU sRGB decode; 3x3 written {lo:0.000}..{hi:0.000}), chunk zone ({z.x},{z.y}); player-paint texels kept in this chunk: {job.Kept}, SnowTrail TrailAt here {SnowTrail.TrailAt(p):0.00}");
        }

        private static readonly Dictionary<Heightmap, float> s_edgeRebakeAt = new Dictionary<Heightmap, float>();
        private const float EdgeTolerance = 0.01f;
        private static void EdgeMatchNeighbours(Job job)
        {
            var hm = job.Hm;
            if (s_rebaking || hm == null || !SnowConfig.P("Match snow at zone edges", true)) return;
            int w = job.Tex.width, h = job.Tex.height;
            float size = hm.m_width * hm.m_scale; Vector3 c = hm.transform.position;
            var side = new[] { new Vector3(0f, 0f, -size), new Vector3(0f, 0f, size), new Vector3(-size, 0f, 0f), new Vector3(size, 0f, 0f) };
            if (s_edgeRebakeAt.Count > 2048) s_edgeRebakeAt.Clear();
            for (int e = 0; e < 4; e++)
            {
                var nhm = Heightmap.FindHeightmap(c + side[e]);
                if (nhm == null || nhm == hm) continue;
                // a neighbour the inject never runs for (no snow corner) would differ forever: re-baking it changes nothing
                if (DeepNorthCorners(nhm) == 0 && MountainCorners(nhm) == 0 && WinterCorners(nhm) == 0) continue;
                bool queued = false; foreach (var j in s_jobs) if (j.Hm == nhm) { queued = true; break; }
                if (queued) continue;   // its own Finish compares the edge when it lands
                var ntex = s_paintMask(nhm);
                if (ntex == null || !ntex.isReadable) continue;
                float worst = 0f;
                for (int k = 0; k < (e < 2 ? w : h); k++)
                {
                    int x = e < 2 ? k : (e == 2 ? 0 : w - 1), y = e < 2 ? (e == 0 ? 0 : h - 1) : k;
                    Vector3 p = hm.VertexMaskToWorld(x, y);
                    nhm.WorldToVertexMask(p, out int nx, out int ny);
                    if (nx < 0 || ny < 0 || nx >= ntex.width || ny >= ntex.height) continue;
                    float d = Mathf.Abs(job.Px[y * w + x].g - ntex.GetPixel(nx, ny).g);
                    if (d > worst) worst = d;
                }
                if (worst <= EdgeTolerance) continue;
                if (s_edgeRebakeAt.TryGetValue(nhm, out float at) && Time.unscaledTime < at) continue;
                s_edgeRebakeAt[nhm] = Time.unscaledTime + 5f;
                if (worst > s_edgeWorst) s_edgeWorst = worst;
                s_rebaking = true;
                try { nhm.Poke(0); s_edgeRebaked++; }
                finally { s_rebaking = false; }
            }
        }

        private static int s_chunks, s_fast, s_texels, s_raised, s_kept, s_edgeAligned, s_edgeExact, s_edgeRebaked, s_trailed, s_winter, s_trailLowered, s_paintSnowed;
        private static float s_edgeWorst;
        private static int s_inFrame; private static float s_inFrameMs;   // 0.2.979
        private static int s_inFrameFrame = -1, s_inFrameCut; private static long s_inFrameStart;   // 0.2.1055
        private const float NearViewerM = 48f;
        private static bool NearViewer(Vector3 chunkCentre)
        {
            Vector3 cam = GameCamera.instance != null ? GameCamera.instance.transform.position : Vector3.positiveInfinity;
            var p = Player.m_localPlayer;
            Vector3 me = p != null ? p.transform.position : Vector3.positiveInfinity;
            Vector2 c = new Vector2(chunkCentre.x, chunkCentre.z);
            float half = 32f;   // a chunk is 64 m: distance to its edge
            return Vector2.Distance(c, new Vector2(cam.x, cam.z)) - half <= NearViewerM || Vector2.Distance(c, new Vector2(me.x, me.z)) - half <= NearViewerM;
        }
        private static float s_ms, s_summaryAt;

        /// <summary>0.2.914: how many of the chunk's centre + 4 corners have Mountain weight (0..5); only asked when none is Deep North.</summary>
        private static int MountainCorners(Heightmap hm)
        {
            float r = hm.m_width * hm.m_scale * 0.5f;
            Vector3 c = hm.transform.position;
            int n = 0;
            foreach (var o in new[] { Vector3.zero, new Vector3(-r, 0, -r), new Vector3(r, 0, -r), new Vector3(-r, 0, r), new Vector3(r, 0, r) })
                if (SnowField.MountainWeight(c + o) > 0f) n++;
            return n;
        }

        // 0.2.998 (lead: "a Debug line sampling a border vertex from both zones' heightmaps"): once per 60 s, the snow paint G that each
        // of the two heightmaps either side of the viewer zone's nearest edge holds at the SAME border point (their shared edge vertex),
        // next to the world build-up. Equal G = the two zones were baked with the same snow; a difference = a seam.
        private static float s_nextBorderWhy;
        internal static void LogBorderProof(Vector3 viewer)
        {
            if (Time.unscaledTime < s_nextBorderWhy) return;
            s_nextBorderWhy = Time.unscaledTime + 60f;
            var z = ZoneSystem.GetZone(viewer); float s = ZoneSystem.c_ZoneSize, edgeX = (z.x + 0.5f) * s, edgeZ = (z.y + 0.5f) * s;
            bool alongX = Mathf.Abs(viewer.x - edgeX) < Mathf.Abs(viewer.z - edgeZ);
            Vector3 border = alongX ? new Vector3(edgeX, viewer.y, viewer.z) : new Vector3(viewer.x, viewer.y, edgeZ);
            Vector3 off = alongX ? new Vector3(1f, 0f, 0f) : new Vector3(0f, 0f, 1f);
            Heightmap a = Heightmap.FindHeightmap(border - off), b = Heightmap.FindHeightmap(border + off);
            string Sample(Heightmap hm)
            {
                if (hm == null) return "not loaded";
                var tex = s_paintMask(hm); if (tex == null) return "no paint";
                float size = hm.m_width * hm.m_scale; Vector3 c = hm.transform.position;
                float u = (border.x - (c.x - size * 0.5f)) / size, v = (border.z - (c.z - size * 0.5f)) / size;
                // 0.2.1001: + whether this zone's TerrainComp marks the border point as player paint (kept at vanilla G) or not (raised)
                string paint = "?";
                try
                {
                    var tc = TerrainComp.FindTerrainCompiler(c);
                    hm.WorldToVertexMask(border, out int vx, out int vy);
                    if (tc == null || tc.m_modifiedPaint == null) paint = "no paint mask (raised)";
                    else { int pw = tc.m_width + 1; paint = vx >= 0 && vy >= 0 && vx < pw && vy < pw && tc.m_modifiedPaint[vy * pw + vx] ? "player-painted (kept)" : "unpainted (raised)"; }
                }
                catch { }
                try { return $"G {tex.GetPixelBilinear(u, v).g:0.000} {paint}"; } catch (Exception e) { return "unreadable (" + e.Message + ")"; }
            }
            string ga = Sample(a), gb = Sample(b);
            FiresLogger.LogDebug($"[Snow] zone border ({(alongX ? $"x {edgeX:0}" : $"z {edgeZ:0}")}, at {border.x:0},{border.z:0}): snow paint {ga} | {gb}{(a != null && a == b ? " (same heightmap)" : "")}; world build-up {SnowField.WorldBuildUp:0.000}");
        }

        /// <summary>How many of the chunk's centre + 4 corners are Deep North (0..5).</summary>
        // 0.2.1052: the chunk's centre + 4 corners on seasonal winter ground (SnowField.WinterGroundAt)
        private static int WinterCorners(Heightmap hm)
        {
            if (SnowField.WinterCover <= 0f) return 0;
            float r = hm.m_width * hm.m_scale * 0.5f;
            Vector3 c = hm.transform.position;
            int n = 0;
            foreach (var o in new[] { Vector3.zero, new Vector3(-r, 0, -r), new Vector3(r, 0, -r), new Vector3(-r, 0, r), new Vector3(r, 0, r) })
                if (SnowField.WinterGroundAt(c + o)) n++;
            return n;
        }

        private static int DeepNorthCorners(Heightmap hm)
        {
            var wg = WorldGenerator.instance;
            float r = hm.m_width * hm.m_scale * 0.5f;
            Vector3 c = hm.transform.position;
            int n = 0;
            // 0.2.829: a deep-snow base (Deep North real or painted, Winter territories), not only the world-gen Deep North
            if (SnowField.DeepGroundAt(c)) n++;
            if (SnowField.DeepGroundAt(c + new Vector3(-r, 0, -r))) n++;
            if (SnowField.DeepGroundAt(c + new Vector3(r, 0, -r))) n++;
            if (SnowField.DeepGroundAt(c + new Vector3(-r, 0, r))) n++;
            if (SnowField.DeepGroundAt(c + new Vector3(r, 0, r))) n++;
            return n;
        }

        /// <summary>Repaints (paint-only) the loaded vanilla chunks in the given zone so the inject reruns.</summary>
        internal static int Repaint(Vector2s zone) => Repaint(zone, 1);

        // 0.2.955 ([ghost], spike report 10-28: six 170-308 ms stalls followed snow steps): a snow pass over budget logs its time at
        // Debug, at most once per 2 s per kind.
        private static readonly System.Collections.Generic.Dictionary<string, float> s_slowLogged = new System.Collections.Generic.Dictionary<string, float>();
        internal static void NoteSlow(string what, long startTicks, double budgetMs, string detail = null)
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms < budgetMs) return;
            if (s_slowLogged.TryGetValue(what, out float at) && Time.unscaledTime - at < 2f) return;
            s_slowLogged[what] = Time.unscaledTime;
            FiresCore.Logging.FiresLogger.LogDebug($"[Snow] slow {what}: {ms:0.0} ms (budget {budgetMs:0} ms){(detail != null ? "; " + detail : "")}");
        }

        /// <summary>0.2.955: with <paramref name="delayFrames"/> the caller staggers a batch (one chunk regenerating per frame instead of all at once).</summary>
        internal static int Repaint(Vector2s zone, int delayFrames) => Repaint(zone, delayFrames, false, "other");
        internal static int Repaint(Vector2s zone, int delayFrames, bool driftOnly) => Repaint(zone, delayFrames, driftOnly, driftOnly ? "drift (wind)" : "other");

        /// <summary>0.2.1189: <paramref name="driftOnly"/> = a wind-sector ease alone (SnowBaseMask may skip a chunk no drift reaches).</summary>
        internal static int Repaint(Vector2s zone, int delayFrames, bool driftOnly, string cause)   // 0.2.1192: + cause, for the per-cause batch line
        {
            int n = 0;
            Vector3 c = ZoneSystem.GetZonePos(zone);
            var hm = Heightmap.FindHeightmap(c);
            if (hm != null && !hm.IsDistantLod)
            {
                // 0.2.1061: the natural snow lives in the build data's base mask; re-apply it first and repaint only when it changed
                if (SnowConfig.NaturalSnow) { SnowBaseMask.QueueRefresh(hm, driftOnly, cause); return n; }   // 0.2.1133: worked out over frames; the batch queues the repaint
                SnowRepaintQueue.Add(hm, false); n++;   // 0.2.1126: the queue spreads them; delayFrames is unused
            }
            return n;
        }
    }
}
