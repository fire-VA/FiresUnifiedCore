using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using FiresCore.Logging;

namespace FiresCore.Snow
{
    /// <summary>
    /// 0.2.1061 ([wishbone], Fire's order "snow trails work the Deep North way everywhere there is snow"; lead read of vanilla 10-30): Core's
    /// season / painted / Mountain snow is written where vanilla keeps the Deep North's: the heightmap BUILD DATA base mask G
    /// (HeightmapBuilder.HMBuildData.m_baseMask, which Heightmap.Generate copies into m_paintMask before TerrainComp's paint lies on top),
    /// on vanilla's raw scale (G x 2 m, as WorldGenerator.GetDeepNorthHeight's 0.3..0.6). So vanilla's snow_walk / shovel ops lower it in
    /// TerrainComp paint exactly as in the Deep North (trails), and SnowRefill moves touched texels back toward this base.
    ///  - Where world gen already put snow (G > 0, the real Deep North) the texel is left exactly vanilla.
    ///  - Applied on the main thread when Generate takes the build data (RequestTerrainSync postfix), and again by <see cref="Refresh"/>
    ///    when the snow changes (SnowTicker's zone announce -> SnowHeightmapInject.Repaint). The vanilla G of every texel is kept per build
    ///    data, so a refresh never compounds.
    ///  - [Snow] Snow in the natural ground (SnowConfig.NaturalSnow) off = nothing here (the older inject runs).
    /// Debug: "[Snow] base mask last 10 s: N build(s), T texel(s) raised, X ms (max Y ms)".
    /// </summary>
    internal static class SnowBaseMask
    {
        private sealed class Vanilla { public float[] G; }
        private static readonly ConditionalWeakTable<HeightmapBuilder.HMBuildData, Vanilla> s_vanilla = new ConditionalWeakTable<HeightmapBuilder.HMBuildData, Vanilla>();
        private static readonly AccessTools.FieldRef<Heightmap, HeightmapBuilder.HMBuildData> s_buildData =
            AccessTools.FieldRefAccess<Heightmap, HeightmapBuilder.HMBuildData>("m_buildData");
        private static int s_farBuilds, s_farTexels, s_nearBuilds, s_nearTexels; private const int FarBiomeStride = 4; private static double s_farBiomeMs, s_farMs, s_farCopyMs, s_nearMs, s_nearCopyMs;
        private static int s_builds, s_raised, s_fatSkipped; private static double s_ms, s_maxMs; private static float s_sayAt;

        [HarmonyPatch(typeof(HeightmapBuilder), nameof(HeightmapBuilder.RequestTerrainSync))]
        private static class HeightmapBuilder_RequestTerrainSync_Snow
        {
            private static void Postfix(HeightmapBuilder.HMBuildData __result)
            {
                try { if (__result != null) Apply(__result); }
                catch (Exception e) { FiresLogger.LogDebug($"[Snow] base mask skipped: {e.Message}"); }
            }
        }

        // 0.2.1180 (Fire: "all mud and then the snow pops in"): the first build skips the ground rules (SnowGround.FactorAt), because paint and heights are unfilled until vanilla Generate ends. Here both are real, so a chunk the rules can change is worked out again and vanilla's Generate re-run from its build data, before Regenerate builds the mesh. Most chunks stop at the check.
        private static readonly System.Func<Heightmap, bool> s_generate = HarmonyLib.AccessTools.MethodDelegate<System.Func<Heightmap, bool>>(HarmonyLib.AccessTools.Method(typeof(Heightmap), "Generate"));
        private static bool[] s_ruleMarks = new bool[0]; private static int s_markedTexels;
        private static bool s_secondPass; private static int s_firstBuilds, s_secondPasses, s_secondChanged; private static double s_firstCheckMs, s_secondMs, s_secondWorstMs; private static float s_firstSayAt;

        [HarmonyLib.HarmonyPatch(typeof(Heightmap), "Generate")]
        private static class Heightmap_Generate_FirstBuildRules
        {
            private static void Prefix(Heightmap __instance, out bool __state) { __state = !s_secondPass && __instance != null && s_buildData(__instance) == null; }
            private static void Postfix(Heightmap __instance, bool __result, bool __state)
            {
                if (!__state || !__result || __instance.IsDistantLod || !SnowConfig.FirstBuildSnow) return;
                try
                {
                    var data = s_buildData(__instance);
                    if (data == null) return;
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    // 0.2.1181 ([fgn]: mud leg hitches up; log: about 3 us of rules x 4,225 texels on top of the snow model = 16-36 ms a chunk, footprint rebuilds about 1 ms per 30 s, so not those): the rules only ever lower snow, so only a marked texel holding natural snow can change.
                    int n = data.m_width + 1, count = n * n, marked = 0;
                    if (s_ruleMarks.Length != count) s_ruleMarks = new bool[count];
                    Vector3 origin = data.m_center + new Vector3(data.m_width * data.m_scale * -0.5f, 0f, data.m_width * data.m_scale * -0.5f);
                    if (s_vanilla.TryGetValue(data, out var van) && SnowGround.MarkRuleTexels(__instance, origin, data.m_scale, n, s_ruleMarks))
                        for (int i = 0; i < count && i < data.m_baseMask.Length; i++)
                        {
                            if (!s_ruleMarks[i]) continue;
                            if (data.m_baseMask[i].g <= 0f || van.G[i] > 0f) s_ruleMarks[i] = false; else marked++;
                        }
                    bool need = marked > 0; s_markedTexels += marked;
                    long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                    double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                    s_firstBuilds++; s_firstCheckMs += (t1 - t0) * f;
                    if (need)
                    {
                        s_secondPasses++;
                        bool patch = SnowConfig.FirstBuildPatchPaint;
                        if (patch)
                        {
                            if (s_oldBase.Length != count) s_oldBase = new Color[count];
                            for (int i = 0; i < count && i < data.m_baseMask.Length; i++) if (s_ruleMarks[i]) s_oldBase[i] = data.m_baseMask[i];
                        }
                        if (Apply(data, s_ruleMarks))
                        {
                            s_secondChanged++;
                            if (patch) PatchPaint(__instance, data, n, count);
                            else
                            {
                                s_secondPass = true;
                                try { s_generate(__instance); } finally { s_secondPass = false; }
                            }
                        }
                        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * f;
                        s_secondMs += ms; if (ms > s_secondWorstMs) s_secondWorstMs = ms;
                    }
                    if (Time.unscaledTime >= s_firstSayAt)
                    {
                        s_firstSayAt = Time.unscaledTime + 10f;
                        FiresLogger.LogDebug($"[SnowLoad] first builds last 10 s: {s_firstBuilds} chunk(s), check {s_firstCheckMs:0.0} ms; ground rules run on {s_secondPasses}, changed {s_secondChanged}, {s_markedTexels} texel(s), {s_secondMs:0.0} ms (worst {s_secondWorstMs:0.0} ms)");
                        if (s_patchedChunks > 0) FiresLogger.LogDebug($"[SnowLoad] paint patched in place last 10 s (no second Generate): {s_patchedChunks} chunk(s), {s_patchedTexels} texel(s) written, {s_paintedKept} kept (painted over), {s_patchMs:0.00} ms");
                        s_firstBuilds = s_secondPasses = s_secondChanged = s_markedTexels = 0; s_firstCheckMs = s_secondMs = s_secondWorstMs = 0;
                        s_patchedChunks = s_patchedTexels = s_paintedKept = 0; s_patchMs = 0;
                    }
                }
                catch (Exception e) { s_secondPass = false; FiresLogger.LogDebug($"[SnowLoad] first build rules skipped: {e.Message}"); }
            }
        }

        // 0.2.1187: vanilla Generate is SetPixels(base mask) then ApplyModifiers, where TerrainComp / modifiers overwrite only the texels they paint. So a texel
        // whose paint still equals its old base was painted by nothing and takes the new base; any other keeps its paint, which vanilla would keep too.
        // Heights are not touched (the rules change G only). Runs before Regenerate's paint Apply, so the same frame as the second Generate it replaces.
        private static Color[] s_oldBase = new Color[0];
        private static int s_patchedChunks, s_patchedTexels, s_paintedKept; private static double s_patchMs;
        private const float PaintTolerance = 1.5f / 255f;   // the paint mask stores 8 bits a channel
        private static void PatchPaint(Heightmap hm, HeightmapBuilder.HMBuildData data, int n, int count)
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var paint = hm.m_paintMask;
            if (paint == null) return;
            int wrote = 0, kept = 0;
            for (int i = 0; i < count && i < data.m_baseMask.Length; i++)
            {
                if (!s_ruleMarks[i]) continue;
                Color now = data.m_baseMask[i], was = s_oldBase[i];
                if (now == was) continue;
                int x = i % n, y = i / n;
                Color p = paint.GetPixel(x, y);
                if (Mathf.Abs(p.r - was.r) <= PaintTolerance && Mathf.Abs(p.g - was.g) <= PaintTolerance && Mathf.Abs(p.b - was.b) <= PaintTolerance && Mathf.Abs(p.a - was.a) <= PaintTolerance)
                { paint.SetPixel(x, y, now); wrote++; }
                else kept++;
            }
            s_patchedChunks++; s_patchedTexels += wrote; s_paintedKept += kept;
            s_patchMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>Re-applies the snow to a loaded heightmap's build data; true when any texel changed (the caller repaints).</summary>
        internal static bool Refresh(Heightmap hm)
        {
            var data = hm != null ? s_buildData(hm) : null;
            if (data == null) return false; long t0 = System.Diagnostics.Stopwatch.GetTimestamp(); bool changed = Apply(data); SnowPerfTimers.Refreshes++; SnowPerfTimers.RefreshTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; return changed;
        }

        /// <summary>The natural (base mask) G of a heightmap vertex index, or -1 when there is no build data.</summary>
        internal static float BaseG(Heightmap hm, int index)
        {
            var data = hm != null ? s_buildData(hm) : null;
            if (data == null || data.m_baseMask == null || index < 0 || index >= data.m_baseMask.Length) return -1f;
            return data.m_baseMask[index].g;
        }

        // 0.2.1133 (Fire's stalls; 1132 log: slow zone announce 705-736 ms, all but 0.3 ms of it this refresh, 25 zones in one frame): a snow step no longer refreshes on the frame it is announced. One batch at a time
        // works out new G into side buffers, within BatchBudgetMs a frame, and every mask in the batch changes on one frame, so neighbours read the same step. Changed chunks then repaint through the queue.
        private const float BatchBudgetMs = 2f;
        private sealed class BatchJob { public Heightmap Hm; public HeightmapBuilder.HMBuildData Data; public Vanilla V; public float[] G; public int Next; public bool SnowBase; public string Cause; public long Ticks; }
        private static readonly System.Collections.Generic.List<BatchJob> s_batch = new System.Collections.Generic.List<BatchJob>();
        private static readonly System.Collections.Generic.List<Heightmap> s_nextBatch = new System.Collections.Generic.List<Heightmap>();
        private static int s_batchAt, s_batchFrames; private static double s_batchMs, s_batchSwapWorst;

        // 0.2.1189 (Fire 11-06 perf pass; mud3 logs: the batch was 11-14 ms/s of SnowTicker's 19-22, about 70% of its chunks came out unchanged, and
        // 48 wind-ease announces re-baked 695 zones in off1): wind drift reaches DeepSnowG only on snow-base ground (Deep North / Mountain weight;
        // the winter-only branch has no drift). Each finished batch records whether its chunk had any snow-base vertex; a drift-only announce then
        // skips a chunk with none, since its G cannot change. Every other cause (step, path mask, building drift, shelter, fires, base epoch) recomputes.
        private sealed class ChunkDeps { public bool SnowBase; }
        private static readonly ConditionalWeakTable<HeightmapBuilder.HMBuildData, ChunkDeps> s_deps = new ConditionalWeakTable<HeightmapBuilder.HMBuildData, ChunkDeps>();
        private static readonly System.Collections.Generic.HashSet<Heightmap> s_nextForced = new System.Collections.Generic.HashSet<Heightmap>();
        private static int s_skipped, s_skipChecked; private static float s_skipLogAt;

        internal static void QueueRefresh(Heightmap hm) => QueueRefresh(hm, false, "other");
        internal static void QueueRefresh(Heightmap hm, bool driftOnly) => QueueRefresh(hm, driftOnly, driftOnly ? "drift (wind)" : "other");

        // 0.2.1192 (lead 11-06: pick the step re-bake target from measured shares): each queued chunk carries its cause (a forced cause wins over
        // a drift-only one); finished batches add their worked ms per cause; one Debug line every 10 s.
        private static readonly System.Collections.Generic.Dictionary<Heightmap, string> s_nextCause = new System.Collections.Generic.Dictionary<Heightmap, string>();
        private static readonly System.Collections.Generic.Dictionary<string, double[]> s_causeStats = new System.Collections.Generic.Dictionary<string, double[]>();   // ms, chunks, changed
        private static float s_causeLogAt;

        /// <summary>0.2.1189: <paramref name="driftOnly"/> = the cause is a wind-sector ease alone (the chunk may be skipped when no drift can reach it).</summary>
        internal static void QueueRefresh(Heightmap hm, bool driftOnly, string cause)
        {
            if (hm == null || hm.IsDistantLod) return;
            if (!driftOnly) s_nextForced.Add(hm);
            if (!driftOnly || !s_nextCause.ContainsKey(hm)) s_nextCause[hm] = cause ?? "other";
            if (!s_nextBatch.Contains(hm)) s_nextBatch.Add(hm);
        }

        internal static void BatchTick()
        {
            if (s_batch.Count == 0 && !StartBatch()) return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp(), end = t0 + (long)(BatchBudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
            s_batchFrames++;
            SnowField.Unbudgeted(() =>
            {
                while (s_batchAt < s_batch.Count && System.Diagnostics.Stopwatch.GetTimestamp() < end)
                {
                    var j = s_batch[s_batchAt];
                    if (j.Hm == null || s_buildData(j.Hm) != j.Data) { j.Next = int.MaxValue; s_batchAt++; continue; }
                    long js = System.Diagnostics.Stopwatch.GetTimestamp();
                    int n = j.Data.m_width + 1, stop = Mathf.Min(j.G.Length, j.Next + 64);
                    Vector3 origin = j.Data.m_center + new Vector3(j.Data.m_width * j.Data.m_scale * -0.5f, 0f, j.Data.m_width * j.Data.m_scale * -0.5f);
                    float wl = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
                    for (; j.Next < stop; j.Next++)
                    {
                        int i = j.Next;
                        if (j.V.G[i] > 0f) j.G[i] = j.V.G[i];
                        else if (j.Data.m_baseHeights != null && i < j.Data.m_baseHeights.Count && j.Data.m_baseHeights[i] < wl) j.G[i] = 0f;
                        else
                        {
                            j.G[i] = SnowHeightmapInject.EncodeDrawn(Mathf.Clamp01(SnowField.DeepSnowG(new Vector3(origin.x + i % n * j.Data.m_scale, 0f, origin.z + i / n * j.Data.m_scale))));
                            if (SnowField.LastSnowBase) j.SnowBase = true;   // 0.2.1189
                        }
                    }
                    j.Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - js;   // 0.2.1192
                    if (j.Next >= j.G.Length) s_batchAt++;
                }
                return 0;
            });
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            s_batchMs += (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (s_batchAt < s_batch.Count) return;
            int changed = 0;
            foreach (var j in s_batch)
            {
                if (j.Next == int.MaxValue || j.Hm == null || s_buildData(j.Hm) != j.Data || SnowVedrGround.Owns) continue;   // 0.2.1198: a batch in flight at a Vedr hand-over writes nothing
                s_deps.Remove(j.Data); s_deps.Add(j.Data, new ChunkDeps { SnowBase = j.SnowBase });   // 0.2.1189: what this chunk depends on, for the next drift-only announce
                var mask = j.Data.m_baseMask; bool any = false;
                for (int i = 0; i < j.G.Length && i < mask.Length; i++)
                    if (Mathf.Abs(mask[i].g - j.G[i]) > 0.002f) { var c = mask[i]; c.g = j.G[i]; mask[i] = c; any = true; }
                if (any) { SnowRepaintQueue.Add(j.Hm, false); changed++; }
                if (!s_causeStats.TryGetValue(j.Cause ?? "other", out var cs)) s_causeStats[j.Cause ?? "other"] = cs = new double[3];   // 0.2.1192
                cs[0] += j.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency; cs[1]++; if (any) cs[2]++;
            }
            if (s_causeStats.Count > 0 && Time.unscaledTime >= s_causeLogAt)
            {
                s_causeLogAt = Time.unscaledTime + 10f;
                var sb = new System.Text.StringBuilder();
                foreach (var kv in s_causeStats) sb.Append(sb.Length > 0 ? "; " : "").Append($"{kv.Key} {kv.Value[0]:0} ms / {kv.Value[1]:0} chunk(s), {kv.Value[2]:0} changed");
                FiresLogger.LogDebug($"[Snow] base mask batch by cause last 10 s: {sb}");
                s_causeStats.Clear();
            }
            double swap = (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (swap > s_batchSwapWorst) s_batchSwapWorst = swap;
            FiresLogger.LogDebug($"[Snow] base mask batch: {s_batch.Count} chunk(s), {changed} changed, {s_batchFrames} frame(s), {s_batchMs:0.0} ms worked, swap {swap:0.00} ms (worst {s_batchSwapWorst:0.00})");
            s_batch.Clear(); s_batchAt = 0; s_batchFrames = 0; s_batchMs = 0;
        }

        private static bool StartBatch()
        {
            if (s_nextBatch.Count == 0) return false;
            bool skipOn = SnowConfig.BaseRefreshSkipsUnchanged;
            foreach (var hm in s_nextBatch)
            {
                var data = hm != null ? s_buildData(hm) : null;
                if (data == null || data.m_baseMask == null || data.m_menu || !SnowConfig.NaturalSnow || !SnowField.Enabled) continue;
                // 0.2.1189: a drift-only cause on a chunk whose last full compute (same build data) found no snow-base vertex
                if (!s_nextForced.Contains(hm))
                {
                    s_skipChecked++;
                    if (skipOn && s_deps.TryGetValue(data, out var deps) && !deps.SnowBase) { s_skipped++; continue; }
                }
                if (SnowVedrGround.Owns || SnowFat.OwnsZone(ZoneSystem.GetZone(data.m_center))) { if (Apply(data)) SnowRepaintQueue.Add(hm, false); continue; }   // only puts vanilla's G back: cheap (0.2.1197: also while Vedr draws the ground snow)
                if (!s_vanilla.TryGetValue(data, out var v))
                {
                    v = new Vanilla { G = new float[data.m_baseMask.Length] };
                    for (int i = 0; i < data.m_baseMask.Length; i++) v.G[i] = data.m_baseMask[i].g;
                    s_vanilla.Add(data, v);
                }
                s_batch.Add(new BatchJob { Hm = hm, Data = data, V = v, G = new float[data.m_baseMask.Length], Cause = s_nextCause.TryGetValue(hm, out var why) ? why : "other" });
            }
            s_nextBatch.Clear(); s_nextForced.Clear(); s_nextCause.Clear();
            if (s_skipChecked > 0 && Time.unscaledTime >= s_skipLogAt)
            {
                s_skipLogAt = Time.unscaledTime + 10f;
                FiresLogger.LogDebug($"[Snow] base refresh, drift-only causes last 10 s: {s_skipped} of {s_skipChecked} chunk(s) skipped (no snow-base ground, wind drift cannot change them){(skipOn ? "" : "; key off, none skipped")}");
                s_skipped = 0; s_skipChecked = 0;
            }
            return s_batch.Count > 0;
        }

        // 0.2.1145 (Fire, x 1666 z 291: both raised spots were cultivated, "they may be a mistake all together"; "vanilla cultivated soil is restored correctly" in spring): the terrain shader draws paint G as depth,
        // so saved cultivated G 1 drew about 2.1 m. Under snow the TEXTURE draws no more than the base; TerrainComp's saved paint is never touched, and when the base reaches 0 in spring the next regen shows the soil again.
        private static int s_clamped, s_clampChunks; private static float s_clampSayAt;
        private sealed class Dents { private float[] _g; internal float[] Peek => _g; internal float[] For(int n) { if (_g == null || _g.Length != n) { _g = new float[n]; for (int i = 0; i < n; i++) _g[i] = -1f; } return _g; } }
        private static readonly ConditionalWeakTable<TerrainComp, Dents> s_dents = new ConditionalWeakTable<TerrainComp, Dents>();
        private static readonly System.Collections.Generic.List<Color32> s_colours = new System.Collections.Generic.List<Color32>();
        // No mesh yet (first build): the winter cover decides until the rebuild runs this again.
        private static bool SnowCorner(Heightmap hm, int i, System.Collections.Generic.List<Color32> colours) => i < colours.Count ? colours[i].g > 2 : SnowField.WinterCover > 0f;

        // 0.2.1157 (Fire: "fall look until the first snowstorm", no mud at winter start; [seasons] needs it to tell a dug trail from thin snow): each shader chunk gets its base mask as `_FiresSnowBase`,
        // 65 x 65 RGBA32 sRGB like the paint mask, same UVs, base in G, so the two compare directly. Uploaded only when it changes; set on the chunk's own material.
        private sealed class BaseTex { public Texture2D Tex; public Color32[] Px; }
        private static readonly ConditionalWeakTable<Heightmap, BaseTex> s_baseTex = new ConditionalWeakTable<Heightmap, BaseTex>();
        private static readonly int s_baseTexId = Shader.PropertyToID("_FiresSnowBase");
        private static int s_baseUploads; private static float s_baseSayAt;
        internal static void PushBaseTexture(Heightmap hm)
        {
            if (hm == null || hm.IsDistantLod || !SnowMeshRaise.ShaderDraws(hm)) return;
            var data = s_buildData(hm); var mask = data != null ? data.m_baseMask : null;
            int n = hm.m_width + 1;
            if (mask == null || mask.Length < n * n) return;
            var bt = s_baseTex.GetOrCreateValue(hm);
            if (bt.Tex == null) { bt.Tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "_FiresSnowBase", wrapMode = TextureWrapMode.Clamp }; bt.Px = new Color32[n * n]; }
            bool changed = false;
            for (int i = 0; i < bt.Px.Length; i++)
            {
                byte g = (byte)Mathf.RoundToInt(Mathf.Clamp01(mask[i].g) * 255f);
                if (bt.Px[i].g != g || bt.Px[i].a != 255) { bt.Px[i] = new Color32(0, g, 0, 255); changed = true; }
            }
            if (changed) { bt.Tex.SetPixels32(bt.Px); bt.Tex.Apply(false); s_baseUploads++; }
            if (hm.m_materialInstance != null) hm.m_materialInstance.SetTexture(s_baseTexId, bt.Tex);
            if (FiresLogger.DebugEnabled && s_baseUploads > 0 && Time.unscaledTime >= s_baseSayAt) { s_baseSayAt = Time.unscaledTime + 10f; FiresLogger.LogDebug($"[Snow] base texture: {s_baseUploads} chunk upload(s) for the shader"); s_baseUploads = 0; }
        }
        [HarmonyPatch(typeof(Heightmap), "OnDestroy")]
        private static class Heightmap_OnDestroy_BaseTex
        {
            private static void Postfix(Heightmap __instance)
            {
                if (__instance != null && s_baseTex.TryGetValue(__instance, out var bt) && bt.Tex != null) { UnityEngine.Object.Destroy(bt.Tex); bt.Tex = null; }
            }
        }

        internal static void ClampPaint(Heightmap hm)
        {
            if (hm == null || hm.IsDistantLod || hm.m_paintMask == null || !SnowConfig.NaturalSnow || !SnowField.CoreGroundOn) return;   // 0.2.1197: CoreGroundOn
            if (SnowFat.OwnsZone(ZoneSystem.GetZone(hm.transform.position))) return;
            var tc = TerrainComp.FindTerrainCompiler(hm.transform.position);
            if (tc == null || tc.m_modifiedPaint == null) return;
            var raw = hm.m_paintMask.GetRawTextureData<Color32>();
            // 0.2.1147 (Fire's probe at x 1657 z 309: saved g 1.00, ground 0.00, base 0, drawn 1.00): with no base the old test left soil at G 1, which the snow shader draws as about 2.1 m. On a shader chunk no base means no snow drawn. Without the shader (vanilla after the thaw) soil keeps its G.
            bool shader = SnowMeshRaise.ShaderDraws(hm);
            s_colours.Clear(); if (shader && hm.m_renderMesh != null) hm.m_renderMesh.GetColors(s_colours);
            float[] dent = s_dents.TryGetValue(tc, out var ds) ? ds.Peek : null;
            var mod = tc.m_modifiedPaint; int n = 0;
            for (int i = 0; i < mod.Length && i < raw.Length; i++)
            {
                if (!mod[i]) continue;
                float b = BaseG(hm, i);
                if (b <= 0f && !shader) continue;
                // 0.2.1154 ([seasons]: chunks do not go back to vanilla after winter): the shader raises G only where the vertex colour is snow (Mountain splat, g), which Vedr repaints. So follow what it reads, not the season: snow corners draw no soil depth, and elsewhere soil shows.
                if (b <= 0f && !SnowCorner(hm, i, s_colours))
                {
                    byte saved = (byte)Mathf.RoundToInt(tc.m_paintMask[i].g * 255f);
                    if (raw[i].g != saved) { var u = raw[i]; u.g = saved; raw[i] = u; n++; }
                    continue;
                }
                float top = Mathf.Max(0f, b); if (dent != null && i < dent.Length && dent[i] >= 0f && dent[i] < top) top = dent[i];
                int limit = Mathf.RoundToInt(top * 255f);
                if (raw[i].g <= limit + 2) continue;
                var c = raw[i]; c.g = (byte)limit; raw[i] = c; n++;
            }
            if (n == 0) return;
            hm.m_paintMask.Apply();
            s_clamped += n; s_clampChunks++;
            if (Time.unscaledTime >= s_clampSayAt) { s_clampSayAt = Time.unscaledTime + 10f; FiresLogger.LogDebug($"[Snow] paint under snow drawn at base: {s_clamped} texel(s) over {s_clampChunks} regen(s), saved paint untouched"); s_clamped = s_clampChunks = 0; }
        }

        // 0.2.1145: vanilla's G > 0.5 test, since 1138 true on plain snow deeper than about 0.45 m, and false on soil the clamp lowered. Where there is base snow, only saved paint above the base is cultivated.
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.IsCultivated))]
        private static class Heightmap_IsCultivated_Snow
        {
            private static void Postfix(Heightmap __instance, Vector3 worldPos, ref bool __result)
            {
                try
                {
                    if (__instance == null || !SnowConfig.NaturalSnow || !SnowField.CoreGroundOn || WorldGenerator.IsDeepnorth(worldPos.x, worldPos.z)) return;   // 0.2.1197: CoreGroundOn
                    __instance.WorldToVertexMask(worldPos, out int x, out int y);
                    int i = y * (__instance.m_width + 1) + x;
                    float b = BaseG(__instance, i);
                    if (b <= 0f) { if (!SnowMeshRaise.ShaderDraws(__instance)) return; s_colours.Clear(); if (__instance.m_renderMesh != null) __instance.m_renderMesh.GetColors(s_colours); if (!SnowCorner(__instance, i, s_colours)) return; }
                    b = Mathf.Max(0f, b);
                    var tc = TerrainComp.FindTerrainCompiler(__instance.transform.position);
                    __result = tc != null && tc.m_modifiedPaint != null && i < tc.m_modifiedPaint.Length && tc.m_modifiedPaint[i] && tc.m_paintMask[i].g > 0.5f && (SnowRefill.MarksBound ? !SnowRefill.TrailMarked(tc, i) : tc.m_paintMask[i].g > b + 0.05f);   // 0.2.1153: vanilla's G > 0.5 on saved paint, and not a trail
                }
                catch { }
            }
        }

        // 0.2.1145 (Fire: real cultivated + paved come back exactly; "the only added vanilla cultivated soil would be our mud trails"): a snow trail op never overwrites player paint. Texels above the base or with dirt / paving
        // are put back right after the op, before FAT's mark looks at them (its postfix runs last), so no mark, and the trail stops at their edge.
        [HarmonyPatch(typeof(TerrainComp), "InternalDoOperation")]
        private static class TerrainComp_TrailKeepsPlayerPaint
        {
            private static Color[] s_before; private static bool[] s_beforeMod; private static TerrainComp s_comp;
            private const float PackFactor = 3f, DentStep = 0.33f, TrenchRadius = 0.75f;   /* 0.2.1175 (Fire: "were now making wider paths in the snow not thinner ones"): 1.1 since 0.2.1164 made every step about 2.2 m wide */
            // 0.2.1164 (Fire: "we dont draw trails through the deeper snow drifts"; log (3264,4608): only 1-2 texels per step G 0.60 to 0, neighbours barely move, so a one-vertex pit the mesh grid hides in deep snow): a trail step cuts a soft trench around the op, deepest at its centre. Only snow: player paint keeps out as above.
            private static void Trench(TerrainComp tc, Heightmap hm, Vector3 pos, TerrainOp.Settings op, ref int packed, ref float from, ref float to)
            {
                float radius = Mathf.Max(TrenchRadius, op.m_paintRadius);
                int n = hm.m_width + 1, reach = Mathf.CeilToInt(radius / hm.m_scale);
                hm.WorldToVertexMask(pos, out int cx, out int cy);
                Vector3 corner = hm.transform.position - new Vector3(hm.m_width * hm.m_scale * 0.5f, 0f, hm.m_width * hm.m_scale * 0.5f);
                for (int y = Mathf.Max(0, cy - reach); y <= Mathf.Min(n - 1, cy + reach); y++)
                    for (int x = Mathf.Max(0, cx - reach); x <= Mathf.Min(n - 1, cx + reach); x++)
                    {
                        int i = y * n + x;
                        if (i >= tc.m_paintMask.Length) continue;
                        float dx = corner.x + x * hm.m_scale - pos.x, dz = corner.z + y * hm.m_scale - pos.z, d = Mathf.Sqrt(dx * dx + dz * dz) / radius;
                        if (d >= 1f) continue;
                        Color was = s_before[i];
                        if (was.b > 0.1f || (s_beforeMod[i] && (SnowRefill.MarksBound ? !SnowRefill.TrailMarked(tc, i) : was.r > 0.1f))) continue;
                        Color c = tc.m_paintMask[i];
                        float g = Mathf.Min(c.g, was.g * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 1f, d)));   // 0.2.1168: flat floor to 0.5 m, so where the step falls between grid points matters less
                        if (g >= c.g - 0.002f) continue;
                        if (g < to || packed == 0) { from = was.g; to = g; }
                        c.g = g; c.r = Mathf.Max(c.r, 0.6f * (1f - d)); tc.m_paintMask[i] = c; tc.m_modifiedPaint[i] = true; packed++;
                    }
            }
            private static void Prefix(TerrainComp __instance, TerrainOp.Settings modifier)
            {
                s_comp = null;
                try
                {
                    if (__instance == null || modifier == null || __instance.m_paintMask == null || !SnowField.CoreGroundOn || !(SnowPathMask.IsWalkTrail(modifier) || CreatureSnowTrail.IsCreatureTrail(modifier))) return;
                    int n = __instance.m_paintMask.Length;
                    if (s_before == null || s_before.Length < n) { s_before = new Color[n]; s_beforeMod = new bool[n]; }
                    System.Array.Copy(__instance.m_paintMask, s_before, n); System.Array.Copy(__instance.m_modifiedPaint, s_beforeMod, n);
                    s_comp = __instance;
                }
                catch { s_comp = null; }
            }
            [HarmonyPriority(Priority.High)]   // 0.2.1153: before FAT's mark, which runs at Last
            private static void Postfix(TerrainComp __instance, Vector3 pos, TerrainOp.Settings modifier)
            {
                if (s_comp != __instance || __instance == null) return;
                s_comp = null;
                try
                {
                    var hm = __instance.m_hmap; if (hm == null) return;
                    // 0.2.1148 (Fire: "we dont pack them down when walking through still"; probe: one pass only 0.61 to 0.50): on snow-shader chunks each pass digs PackFactor x as much, and over soil a dent is kept for drawing only (this session, not saved); the saved soil stays.
                    bool shader = SnowMeshRaise.ShaderDraws(hm);
                    if (ZNet.instance != null && ZNet.instance.IsDedicated()) shader = true;   // 0.2.1167: a headless server has no shader, but it applies the op on every TerrainComp it owns: without this those chunks got only vanilla's one-vertex pit (a spot with no trail, creatures' trails)
                    float[] dents = null;
                    int kept = 0, packed = 0, dented = 0; float sampleFrom = 0f, sampleTo = 0f;
                    for (int i = 0; i < __instance.m_paintMask.Length; i++)
                    {
                        Color was = s_before[i], now = __instance.m_paintMask[i];
                        if (was == now) continue;
                        float b = Mathf.Max(0f, BaseG(hm, i));
                        if (s_beforeMod[i] && (was.b > 0.1f || (SnowRefill.MarksBound ? !SnowRefill.TrailMarked(__instance, i) : was.r > 0.1f || was.g > b + 0.05f)))   // 0.2.1153: edited + unmarked = player paint; a marked trail keeps digging on later passes
                        {
                            __instance.m_paintMask[i] = was; kept++;
                            if (shader && b > 0f && was.r <= 0.1f && was.b <= 0.1f)
                            {
                                if (dents == null) dents = s_dents.GetOrCreateValue(__instance).For(__instance.m_paintMask.Length);
                                dents[i] = Mathf.Max(0f, (dents[i] >= 0f ? dents[i] : b) - DentStep); dented++;
                            }
                            continue;
                        }
                        if (shader) { __instance.m_paintMask[i] = was; __instance.m_modifiedPaint[i] = s_beforeMod[i]; }   // 0.2.1168 (Fire: "sometimes its fat like 2m wide sometimes its like .5m wide"): vanilla's op snaps to the 1 m grid; the trench below is the one shape
                    }
                    if (shader) Trench(__instance, hm, pos, modifier, ref packed, ref sampleFrom, ref sampleTo);
                    if (FiresLogger.DebugEnabled && (packed > 0 || dented > 0)) FiresLogger.LogDebug($"[SnowTrail] pack: chunk ({hm.transform.position.x:0},{hm.transform.position.z:0}) {packed} texel(s) dug x {PackFactor:0}, deepest G {sampleFrom:0.00} to {sampleTo:0.00}; {kept} player texel(s) kept, {dented} soil dent(s) drawn only");
if (kept > 0) FiresLogger.LogDebug($"[SnowTrail] trail op kept {kept} texel(s) of player paint");
                }
                catch (System.Exception e) { FiresLogger.LogDebug($"[SnowTrail] keep paint failed: {e.Message}"); }
            }
        }

        private sealed class HandOver { public int Epoch; }
        private static readonly ConditionalWeakTable<HeightmapBuilder.HMBuildData, HandOver> s_handedOver = new ConditionalWeakTable<HeightmapBuilder.HMBuildData, HandOver>();
        private static int s_handedRestored;
        /// <summary>0.2.1198: chunks whose Core snow was put back to vanilla at a Vedr hand-over (read and cleared by the owner line's follow-up).</summary>
        internal static int TakeHandedRestored() { int n = s_handedRestored; s_handedRestored = 0; return n; }

        private static bool Apply(HeightmapBuilder.HMBuildData data, bool[] only = null)
        {
            if (!SnowConfig.NaturalSnow || !SnowField.Enabled || data.m_menu || data.m_baseMask == null) return false;
            // 0.2.1062 ([worldgen], Fire's 1309 + 1061 log: FAT builds its voxel paint from the whole heightmap paint mask and read this base
            // snow G as a trail cut - "paint G 0.591 ... after path 0.00 m"): a zone FAT's voxel terrain owns gets no base-mask snow, as the
            // older inject never did (SnowHeightmapInject skips them too); FAT draws its voxel snow from SnowField.DepthAt
            // 0.2.1198 ([seasons] 11-07: Vedr writes its own snow into this same base-mask G): at the hand-over Core puts vanilla's G back ONCE per
            // build data (this hand-over's epoch), then never touches that mask again while Vedr owns the ground. 1197 restored on every re-bake,
            // so each later snow step (cover, drift, path mask) would have wiped Vedr's snow. Chunks built after the hand-over carry no Core snow
            // (no s_vanilla entry yet) and are only marked. s_vanilla is kept, so a hand-back to Core raises from the real vanilla G again.
            if (SnowVedrGround.Owns)
            {
                var handed = s_handedOver.GetOrCreateValue(data);
                if (handed.Epoch == SnowVedrGround.Epoch) return false;
                handed.Epoch = SnowVedrGround.Epoch;
                if (!s_vanilla.TryGetValue(data, out var orig)) return false;
                bool restored = false;
                for (int i = 0; i < data.m_baseMask.Length && i < orig.G.Length; i++)
                    if (data.m_baseMask[i].g != orig.G[i]) { var c = data.m_baseMask[i]; c.g = orig.G[i]; data.m_baseMask[i] = c; restored = true; }
                if (restored) s_handedRestored++;
                return restored;
            }
            if (SnowFat.OwnsZone(ZoneSystem.GetZone(data.m_center)))
            {
                s_fatSkipped++;
                // raised before FAT claimed the zone: put vanilla's G back
                if (!s_vanilla.TryGetValue(data, out var was)) return false;
                bool back = false;
                for (int i = 0; i < data.m_baseMask.Length && i < was.G.Length; i++)
                    if (data.m_baseMask[i].g != was.G[i]) { var c = data.m_baseMask[i]; c.g = was.G[i]; data.m_baseMask[i] = c; back = true; }
                return back;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var mask = data.m_baseMask;
            if (!s_vanilla.TryGetValue(data, out var v))
            {
                v = new Vanilla { G = new float[mask.Length] };
                for (int i = 0; i < mask.Length; i++) v.G[i] = mask[i].g;
                s_vanilla.Add(data, v);
            }
            long tCopied = System.Diagnostics.Stopwatch.GetTimestamp();
            int n = data.m_width + 1;
            Vector3 origin = data.m_center + new Vector3(data.m_width * data.m_scale * -0.5f, 0f, data.m_width * data.m_scale * -0.5f);
            // 0.2.1101 (lead, [worldgen] on 1100: still ~100 ms per far build, 6,561 texels): WorldGenerator.GetBiome for every texel is the main suspect, so far terrain
            // samples it every 4th texel (40 m), 16 x fewer calls. Timed apart in the split line, so the log shows whether that was the cost.
            Heightmap.Biome[] farBiomes = null; int farN = 0;
            long tBiome0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (data.m_distantLod && WorldGenerator.instance != null)
            {
                farN = (n - 1) / FarBiomeStride + 1;
                farBiomes = new Heightmap.Biome[farN * farN];
                for (int a = 0; a < farN; a++)
                    for (int b = 0; b < farN; b++)
                        farBiomes[a * farN + b] = WorldGenerator.instance.GetBiome(origin.x + (b * FarBiomeStride + FarBiomeStride / 2) * data.m_scale, origin.z + (a * FarBiomeStride + FarBiomeStride / 2) * data.m_scale);
            }
            double biomeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - tBiome0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            bool changed = false; int raised = 0;
            SnowField.Unbudgeted(() =>
            {
                for (int k = 0; k < n; k++)
                    for (int l = 0; l < n; l++)
                    {
                        int i = k * n + l;
                        if (i >= mask.Length) continue;
                        if (only != null && !only[i]) continue;   // 0.2.1181: first build, only the texels the ground rules can touch
                        float g;
                        if (v.G[i] > 0f) g = v.G[i];   // world-gen snow (the real Deep North): exactly vanilla
                        else
                        {
                            var p = new Vector3(origin.x + l * data.m_scale, 0f, origin.z + k * data.m_scale);
                            // 0.2.1072 (Fire: "it should not build up under the ice"): ground under the sea level gets no natural snow (so the
                            // refill never raises snow there either); FAT owns inland voxel water / ice (it zeroes those columns itself)
                            float wl = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
                            g = data.m_baseHeights != null && i < data.m_baseHeights.Count && data.m_baseHeights[i] < wl ? 0f : (farBiomes != null ? SnowField.DeepSnowGDistant(p, farBiomes[(k / FarBiomeStride) * farN + l / FarBiomeStride]) : Mathf.Clamp01(SnowField.DeepSnowG(p))); g = SnowHeightmapInject.EncodeDrawn(g);   // 0.2.1138: vanilla's drawn curve
                        }
                        if (Mathf.Abs(mask[i].g - g) > 0.002f) { var c = mask[i]; c.g = g; mask[i] = c; changed = true; }
                        if (g > 0f && v.G[i] <= 0f) raised++;
                    }
                return 0;
            });
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            double copyMs = (tCopied - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (data.m_distantLod) { s_farBuilds++; s_farTexels += mask.Length; s_farMs += ms; s_farCopyMs += copyMs; s_farBiomeMs += biomeMs; }
            else { s_nearBuilds++; s_nearTexels += mask.Length; s_nearMs += ms; s_nearCopyMs += copyMs; }
            s_builds++; s_raised += raised; s_ms += ms; if (ms > s_maxMs) s_maxMs = ms;
            if (Time.unscaledTime >= s_sayAt)
            {
                if (s_sayAt > 0f && s_builds > 0) FiresLogger.LogDebug($"[Snow] base mask last 10 s: {s_builds} build(s), {s_raised} texel(s) raised, {s_ms:0.0} ms (max {s_maxMs:0.0} ms), {s_fatSkipped} FAT voxel build(s) skipped");
                if (s_sayAt > 0f && s_builds > 0) FiresLogger.LogDebug($"[Snow] base mask split: far LOD {s_farBuilds} build(s), {s_farTexels} texel(s), {s_farMs:0.0} ms (copy {s_farCopyMs:0.0}, biome {s_farBiomeMs:0.0}); near {s_nearBuilds} build(s), {s_nearTexels} texel(s), {s_nearMs:0.0} ms (copy {s_nearCopyMs:0.0})");
                s_farBuilds = s_farTexels = s_nearBuilds = s_nearTexels = 0; s_farMs = s_farCopyMs = s_farBiomeMs = s_nearMs = s_nearCopyMs = 0;
                s_sayAt = Time.unscaledTime + 10f; s_builds = s_raised = s_fatSkipped = 0; s_ms = s_maxMs = 0;
            }
            return changed;
        }
    }
}
