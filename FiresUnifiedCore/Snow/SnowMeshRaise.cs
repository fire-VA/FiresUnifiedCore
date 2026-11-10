using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// 0.2.1124 (Fire 11-03: "there is no buildup being shown", "no snow trails" in Black Forest; his pick of the fix): vanilla's terrain domain shader adds paint G x 2.1 only inside the
    /// Deep North world arc (a world position test, WorldGenerator.IsDeepnorth; Tools\_saddles_rdc\lead_hsds_dump.txt 30-36, 70-71), so Core's correct base mask snow was never raised
    /// outside it. This raises the render mesh there by the same data: G x 2 m, up to the base mask's snow, less dirt and paving as the shader does. Real Deep North stays vanilla's; the
    /// shader and collision are untouched. Event driven: every snow change ends in a vanilla Regenerate. One chunk per frame, nearest first. Near chunks on vanilla ground only.
    /// </summary>
    internal static class SnowMeshRaise
    {
        private const float MetresPerG = 2f, MoveEpsilon = 0.02f, BudgetMs = 2f, SayEvery = 10f;
        private sealed class State { public Vector3[] Plain; public float[] Offset, Last; public bool MustWrite; public string Why; }
        private static readonly ConditionalWeakTable<Heightmap, State> s_states = new ConditionalWeakTable<Heightmap, State>();
        private static readonly List<Heightmap> s_dirty = new List<Heightmap>();
        private static readonly List<Vector3> s_read = new List<Vector3>();
        private static int s_writes, s_skips, s_raised; private static double s_ms, s_worst; private static float s_sayAt;

        // 0.2.1128 (Fire: "the reaction speed is nice we just need it to not flash like that"): the unraised mesh is kept at the moment vanilla really rebuilt it. Read from Regenerate's
        // state, a rebuild FGN's TerrainSpread had put off (its prefix skips Regenerate) took the still-raised mesh for the plain one and raised it again until the real rebuild.
        [HarmonyPatch(typeof(Heightmap), "RebuildRenderMesh")]
        private static class Heightmap_RebuildRenderMesh_SnowMesh
        {
            private static void Postfix(Heightmap __instance)
            {
                try
                {
                    if (__instance == null || __instance.IsDistantLod || __instance.m_renderMesh == null || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
                    var st = s_states.GetOrCreateValue(__instance);
                    s_read.Clear(); __instance.m_renderMesh.GetVertices(s_read);
                    if (st.Plain == null || st.Plain.Length != s_read.Count) { st.Plain = new Vector3[s_read.Count]; st.Offset = new float[s_read.Count]; }
                    s_read.CopyTo(st.Plain);
                    System.Array.Clear(st.Offset, 0, st.Offset.Length);
                    st.Why = "full rebuild"; s_rebuildFrame = Time.frameCount;
                    SnowBaseMask.PushBaseTexture(__instance); SnowBaseMask.ClampPaint(__instance);   // 0.2.1154: the rebuild may bring new vertex colours
                    Apply(__instance);   // before this frame renders: a chunk never draws unraised
                }
                catch (System.Exception e) { FiresLogger.LogDebug($"[SnowMesh] rebuild note failed: {e.Message}"); }
            }
        }

        // 0.2.1177 (Fire: "the time it takes between loading a zone and the snow mesh showing... its really jarring to see all mud and then the snow pops in"): Debug only, reads only, event
        // driven, any zone load (walking, flying, teleport). A chunk's first full rebuild is its first drawn frame: it records what that frame had (base mask snow, raised vertices, the distance).
        // Later rebuilds of the same chunk log once when snow first arrives, or grows by a quarter, with the ms since the first frame. Every 10 s a summary. Regenerate postfix, last, after the raise.
        private sealed class Load { public float At; public int Frame, Base, Raised; public bool Late, Done; }
        private static readonly ConditionalWeakTable<Heightmap, Load> s_loads = new ConditionalWeakTable<Heightmap, Load>();
        private static int s_chunks, s_withSnow, s_noSnow, s_arrived, s_overFrames; private static float s_worstMs, s_sumMs, s_loadSayAt;
        private const int PassFrames = 5;   // Fire: "at least within a few frames rather than a few seconds"
        private const float TrackSeconds = 60f;

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Regenerate))]
        private static class Heightmap_Regenerate_LoadProbe
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Heightmap __instance, bool __runOriginal)
            {
                if (!__runOriginal || !FiresLogger.DebugEnabled || __instance == null || __instance.IsDistantLod || __instance.m_renderMesh == null || !SnowField.Enabled) return;
                try
                {
                    float now = Time.unscaledTime;
                    Load l;
                    if (!s_loads.TryGetValue(__instance, out l))
                    {
                        l = new Load { At = now, Frame = Time.frameCount };
                        s_loads.Add(__instance, l);
                        Count(__instance, out l.Base, out l.Raised);
                        Vector3 c = __instance.transform.position; var p = Player.m_localPlayer;
                        float dist = p != null ? Vector2.Distance(new Vector2(c.x, c.z), new Vector2(p.transform.position.x, p.transform.position.z)) : -1f;
                        s_chunks++; if (l.Base > 0) s_withSnow++; else s_noSnow++;
                        FiresLogger.LogDebug($"[SnowLoad] chunk ({c.x:0},{c.z:0}) first drawn frame {l.Frame}, {dist:0} m from the player: {l.Base} base snow texel(s), {l.Raised} raised vertices, winter cover {SnowField.WinterCover:0.00}, zone may have snow {SnowField.ZoneHasSnow(c)}, deep ground {SnowField.DeepGroundAt(c)}");
                    }
                    else if (!l.Done)
                    {
                        if (now - l.At > TrackSeconds) { l.Done = true; }
                        else
                        {
                            Count(__instance, out int b, out int r);
                            if (b > l.Base + l.Base / 4 + 8 || r > l.Raised + l.Raised / 4 + 8)
                            {
                                float ms = (now - l.At) * 1000f;
                                Vector3 c = __instance.transform.position;
                                FiresLogger.LogDebug($"[SnowLoad] chunk ({c.x:0},{c.z:0}) snow arrived {ms:0} ms ({Time.frameCount - l.Frame} frames) after its first draw: base texels {l.Base} -> {b}, raised vertices {l.Raised} -> {r}");
                                if (!l.Late) { l.Late = true; s_arrived++; s_sumMs += ms; if (ms > s_worstMs) s_worstMs = ms; if (Time.frameCount - l.Frame > PassFrames) s_overFrames++; }
                                l.Base = b; l.Raised = r;
                            }
                        }
                    }
                    if (now >= s_loadSayAt)
                    {
                        s_loadSayAt = now + SayEvery;
                        if (s_chunks > 0 || s_arrived > 0) FiresLogger.LogDebug($"[SnowLoad] last {SayEvery:0} s: {s_chunks} chunk(s) first drawn, {s_withSnow} with snow in place, {s_noSnow} with none; {s_arrived} got it later ({s_overFrames} past the {PassFrames}-frame pass line), avg {(s_arrived > 0 ? s_sumMs / s_arrived : 0f):0} ms, worst {s_worstMs:0} ms");
                        s_chunks = s_withSnow = s_noSnow = s_arrived = s_overFrames = 0; s_sumMs = s_worstMs = 0f;
                    }
                }
                catch (System.Exception e) { FiresLogger.LogDebug($"[SnowLoad] probe failed: {e.Message}"); }
            }
        }

        private static void Count(Heightmap hm, out int baseTexels, out int raised)
        {
            baseTexels = raised = 0;
            int n = hm.m_width + 1, count = n * n;
            for (int i = 0; i < count; i++) if (SnowBaseMask.BaseG(hm, i) > 0f) baseTexels++;
            if (s_states.TryGetValue(hm, out var st) && st.Offset != null)
                for (int i = 0; i < st.Offset.Length; i++) if (st.Offset[i] > 0.02f) raised++;
        }

        [HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Regenerate))]
        private static class Heightmap_Regenerate_PerfTimer
        {
            private static void Prefix(out long __state) { __state = Stopwatch.GetTimestamp(); }
            private static void Postfix(long __state) { if (__state == 0) return;   /* another prefix skipped ours */ SnowPerfTimers.Regenerates++; SnowPerfTimers.RegenerateTicks += Stopwatch.GetTimestamp() - __state; }
        }

        // A paint-only regen changes the paint, not the mesh. It runs after Generate in the same frame, so the mesh follows before rendering. A skipped Regenerate re-reads the same paint: nothing moves.
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.Regenerate))]
        private static class Heightmap_Regenerate_SnowMesh
        {
            // 0.2.1133 (Fire's flash: 145 chunk writes of hundreds of vertices down 0.80 m, up 0.78 m the next frame): a prefix that skips Regenerate still runs this postfix. The paint is then the OLD one against
            // the NEW base mask, so a base 0.01 G lower failed the 'above the natural snow' test and the vertex lost its whole raise for a frame. A skipped Regenerate changed no paint: nothing to do.
            private static void Postfix(Heightmap __instance, bool __runOriginal)
            {
                if (!__runOriginal) { SnowPerfTimers.SkippedRegens++; return; }
                try
                {
                    if (__instance == null || __instance.IsDistantLod || !s_states.TryGetValue(__instance, out var st) || st.Plain == null) return;
                    if (st.Why == null) st.Why = "paint change";
                    AlignRings(__instance); Apply(__instance); if (FiresLogger.DebugEnabled) SeamCheck(__instance);   // 0.2.1153: rings first, so a mesh raise reads them
                }
                catch (System.Exception e) { FiresLogger.LogDebug($"[SnowMesh] regen note failed: {e.Message}"); }
            }
        }

// 0.2.1130 (Fire: "it flashes between the unedited original ground height and the ground height from the snow and back", "it could infact be lighting"): Debug only, reads only.
        // Vanilla draws terrain with the Heightmap's own MeshRenderer + MeshFilter (Heightmap.cs:555), no Graphics.DrawMesh. Just before the main camera culls, read the vertex under the player from
        // the mesh that filter holds. A height that isn't plain + raise logs every frame; a change of state, normal, sun or ambient logs once. Each line lists every tile covering the spot, distant ones too.
        private static bool s_flashHooked; private static string s_flashState, s_writer = "none"; private static Heightmap s_flashHm;
        private static int s_rebuildFrame = -1, s_writeFrame = -1, s_flashVertex = -1;
        private static Vector3 s_flashNormal, s_flashSunDir; private static float s_flashSun; private static Color s_flashAmbient;
        private static readonly List<Vector3> s_flashRead = new List<Vector3>(), s_flashNormals = new List<Vector3>();
        private static void FlashProbe(Camera cam)
        {
            try
            {
                if (!FiresLogger.DebugEnabled || cam == null || cam != Utils.GetMainCamera() || Player.m_localPlayer == null) return;
                Vector3 p = Player.m_localPlayer.transform.position;
                var hm = Heightmap.FindHeightmap(p);
                if (hm == null || hm.IsDistantLod || !s_states.TryGetValue(hm, out var st) || st.Plain == null) return;
                hm.WorldToVertexMask(p, out int x, out int y);
                int i = y * (hm.m_width + 1) + x;
                if (i < 0 || i >= st.Plain.Length) return;
                var mf = hm.GetComponent<MeshFilter>(); var drawn = mf != null ? mf.sharedMesh : null;
                float want = st.Offset[i], delta = 0f; string state = "no mesh"; Vector3 normal = Vector3.zero;
                if (drawn != null)
                {
                    s_flashRead.Clear(); drawn.GetVertices(s_flashRead);
                    if (i >= s_flashRead.Count) state = "mesh has too few vertices";
                    else
                    {
                        delta = s_flashRead[i].y - st.Plain[i].y;
                        state = Mathf.Abs(delta - want) < 0.02f ? (want > 0.02f ? "raised" : "plain, as wanted") : Mathf.Abs(delta) < 0.02f ? "PLAIN, raise missing" : "OTHER height";
                        s_flashNormals.Clear(); drawn.GetNormals(s_flashNormals);
                        if (i < s_flashNormals.Count) normal = s_flashNormals[i];
                    }
                }
                var env = EnvMan.instance; var sun = env != null ? env.m_dirLight : null;
                float sunI = sun != null ? sun.intensity : 0f; Vector3 sunDir = sun != null ? sun.transform.forward : Vector3.zero; Color amb = RenderSettings.ambientLight;
                bool bad = state != "raised" && state != "plain, as wanted";
                string why = null;
                if (bad) why = "height";
                else if (state != s_flashState || hm != s_flashHm) why = "state";
                else if (i == s_flashVertex && Vector3.Angle(normal, s_flashNormal) > 2f) why = "NORMAL";
                else if (Mathf.Abs(sunI - s_flashSun) > 0.02f || Vector3.Angle(sunDir, s_flashSunDir) > 1f) why = "SUN";
                else if (Mathf.Abs(amb.r - s_flashAmbient.r) + Mathf.Abs(amb.g - s_flashAmbient.g) + Mathf.Abs(amb.b - s_flashAmbient.b) > 0.03f) why = "AMBIENT";
                if (why != null)
                {
                    var r = hm.GetComponent<MeshRenderer>();
                    FiresLogger.LogDebug($"[SnowFlash] frame {Time.frameCount} {why}: chunk ({hm.transform.position.x:0},{hm.transform.position.z:0}) vertex {i} '{state}' (was '{s_flashState}'), mesh +{delta:0.00} m, wanted +{want:0.00} m; normal {normal.x:0.000},{normal.y:0.000},{normal.z:0.000} (was {s_flashNormal.x:0.000},{s_flashNormal.y:0.000},{s_flashNormal.z:0.000}); sun {sunI:0.00} (was {s_flashSun:0.00}) ambient {amb.r:0.00},{amb.g:0.00},{amb.b:0.00}; last rebuild frame {s_rebuildFrame}, last write frame {s_writeFrame} by {s_writer}; filter holds m_renderMesh {drawn == hm.m_renderMesh}, renderer on {(r != null && r.enabled)}; tiles here: {TilesAt(p)}");
                }
                s_flashState = state; s_flashHm = hm; s_flashVertex = i; s_flashNormal = normal; s_flashSun = sunI; s_flashSunDir = sunDir; s_flashAmbient = amb;
            }
            catch (System.Exception e) { FiresLogger.LogDebug($"[SnowFlash] probe failed: {e.Message}"); }
        }
        private static string TilesAt(Vector3 p)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var h in Heightmap.GetAllHeightmaps())
            {
                if (h == null) continue;
                Vector3 at = h.transform.position; float half = h.m_width * h.m_scale * 0.5f;
                if (Mathf.Abs(p.x - at.x) > half || Mathf.Abs(p.z - at.z) > half) continue;
                var mf = h.GetComponent<MeshFilter>(); var r = h.GetComponent<MeshRenderer>(); var mesh = mf != null ? mf.sharedMesh : null; string top = "?";
                if (mesh != null)
                {
                    h.WorldToVertexMask(p, out int x, out int y); int i = y * (h.m_width + 1) + x;
                    s_flashRead.Clear(); mesh.GetVertices(s_flashRead);
                    if (i >= 0 && i < s_flashRead.Count) top = (at.y + s_flashRead[i].y).ToString("0.00");
                }
                sb.Append($"[{(h.IsDistantLod ? "distant" : "near")} ({at.x:0},{at.z:0}) y {top} on {(r != null && r.enabled && h.gameObject.activeInHierarchy)}] ");
            }
            return sb.ToString();
        }

        // 0.2.1143 (Fire: "making sure meshes agree on zone borders when we first load the zone", cracks that heal when he walks there): Debug only, reads only. After every real Regenerate, compare each shared edge
        // with the loaded neighbour: base mask G, drawn paint G, ground height, normal. A pair logs when it opens and when it closes, with both chunks' last Regenerate frames, so the log says which layer is wrong and what healed it.
        private static readonly Dictionary<long, string> s_seams = new Dictionary<long, string>();
        private static readonly Dictionary<Heightmap, int> s_regenFrame = new Dictionary<Heightmap, int>();
        private static readonly List<Vector3> s_na = new List<Vector3>(), s_nb = new List<Vector3>();
        // 0.2.1145 (Fire: "making sure meshes agree on zone borders when we first load the zone"; [SnowSeam]: paint differs across a shared edge, base equal, most gaps lasting; ops reach only chunks loaded when they land): on a real Regenerate both
        // chunks DRAW the lower G on the shared ring, as FAT 1302 syncs chunk rings, indexed as vanilla's shared border. Textures only, no Save. Player paint (dirt, paving, above base) keeps its own edge. Runs for whichever chunk loads second too.
        private static int s_ringTexels, s_ringPairs; private static float s_ringSayAt;
        private static void AlignRings(Heightmap a)
        {
            if (a == null || a.IsDistantLod || !SnowConfig.NaturalSnow || !SnowField.CoreGroundOn) return;   // 0.2.1197: CoreGroundOn
            int n = a.m_width + 1; Vector3 c = a.transform.position; float size = a.m_width * a.m_scale;
            AlignPair(a, Heightmap.FindHeightmap(c + new Vector3(size, 0f, 0f)), n, true);
            AlignPair(a, Heightmap.FindHeightmap(c + new Vector3(0f, 0f, size)), n, false);
            AlignPair(Heightmap.FindHeightmap(c - new Vector3(size, 0f, 0f)), a, n, true);
            AlignPair(Heightmap.FindHeightmap(c - new Vector3(0f, 0f, size)), a, n, false);
            if (s_ringTexels > 0 && FiresLogger.DebugEnabled && Time.unscaledTime >= s_ringSayAt) { s_ringSayAt = Time.unscaledTime + 10f; FiresLogger.LogDebug($"[SnowSeam] rings aligned: {s_ringTexels} texel(s) over {s_ringPairs} edge(s), lower G, saved paint untouched"); s_ringTexels = s_ringPairs = 0; }
        }
        // False = leave this texel to player paint (dirt, paving, soil above the base, or no base data).
        private static bool RingTruth(Heightmap hm, TerrainComp tc, int i, out float g)
        {
            g = SnowBaseMask.BaseG(hm, i);
            if (g < 0f) return false;
            if (tc == null || tc.m_modifiedPaint == null || i >= tc.m_modifiedPaint.Length || !tc.m_modifiedPaint[i]) return true;
            Color c = tc.m_paintMask[i];
            if (c.b > 0.1f || c.g > g + 0.02f) return false;   // 0.2.1151 (Fire "mesh breaks" x 1630 z 279; (1600,256) east edge still 0.55 apart): a trail's snow op also sets r (probe: r 0.60 g 0.50), so skipping r left dug rings unaligned
            g = c.g;
            return true;
        }
        private static bool s_ringWhySaid;
        private static string RingEdited(TerrainComp tc, int i) => tc == null || tc.m_modifiedPaint == null ? "no comp" : i < tc.m_modifiedPaint.Length && tc.m_modifiedPaint[i] ? "yes" : "no";

        private static void AlignPair(Heightmap a, Heightmap b, int n, bool east)
        {
            if (a == null || b == null || a == b || a.IsDistantLod || b.IsDistantLod || a.m_paintMask == null || b.m_paintMask == null || b.m_width + 1 != n) return;
            if (SnowFat.OwnsZone(ZoneSystem.GetZone(a.transform.position)) || SnowFat.OwnsZone(ZoneSystem.GetZone(b.transform.position))) return;
            var pa = a.m_paintMask.GetRawTextureData<Color32>(); var pb = b.m_paintMask.GetRawTextureData<Color32>();
            // 0.2.1149 (Fire, a dark cross along zone borders after forcing winter, "it leaves zone edges bare"): comparing the textures ratcheted: a ring lowered against a stale neighbour never rose again, and dragged the next chunk down.
            // Each side's value now comes from its data (saved paint where edited, else the base), which the batch changes together, and may raise the ring as well as lower it.
            var ta = TerrainComp.FindTerrainCompiler(a.transform.position); var tb = TerrainComp.FindTerrainCompiler(b.transform.position);
            bool da = false, db = false; int moved = 0;
            for (int t = 0; t < n; t++)
            {
                int ia = east ? t * n + n - 1 : (n - 1) * n + t, ib = east ? t * n : t;
                if (ia >= pa.Length || ib >= pb.Length) return;
                // 0.2.1152 (Fire: "the hole in the mesh is stil here, if i were to walk over it it would heal properly"; r74 + 1151 log: (1664,512) north edge still 0.41 apart after alignment): a texel left to player paint was skipped, so its
                // neighbour kept a different G. A walk heals because vanilla's op writes both sides the same. Now the ring always matches: a player-paint side keeps what it draws and the other side takes it; player paint is never changed by a non-player side.
                if (SnowBaseMask.BaseG(a, ia) < 0f || SnowBaseMask.BaseG(b, ib) < 0f) continue;   // 0.2.1153: a side with no build data has nothing to give yet
                bool ka = RingTruth(a, ta, ia, out float ga), kb = RingTruth(b, tb, ib, out float gb);
                Color32 ca = pa[ia], cb = pb[ib];
                if (!ka) ga = ca.g / 255f;
                if (!kb) gb = cb.g / 255f;
                if (FiresLogger.DebugEnabled && !s_ringWhySaid && (!ka || !kb) && ca.g != cb.g)
                {
                    s_ringWhySaid = true;
                    FiresLogger.LogDebug($"[SnowSeam] ring why ({a.transform.position.x:0},{a.transform.position.z:0}) {(east ? "east" : "north")} t {t}: this rgb {ca.r},{ca.g},{ca.b} data {ka} base {SnowBaseMask.BaseG(a, ia):0.00} edited {RingEdited(ta, ia)}; other rgb {cb.r},{cb.g},{cb.b} data {kb} base {SnowBaseMask.BaseG(b, ib):0.00} edited {RingEdited(tb, ib)}");
                }
                byte low = !ka && kb ? ca.g : ka && !kb ? cb.g : (byte)Mathf.RoundToInt(Mathf.Min(ga, gb) * 255f);
                if (ca.g != low) { ca.g = low; pa[ia] = ca; da = true; moved++; }
                if (cb.g != low) { cb.g = low; pb[ib] = cb; db = true; moved++; }
            }
            s_ringWhySaid = false;
            if (da) { a.m_paintMask.Apply(); if (!ShaderDraws(a)) Apply(a); }   // 0.2.1153: a raise on a chunk without the shader follows its ring
            if (db) { b.m_paintMask.Apply(); if (!ShaderDraws(b)) Apply(b); }
            if (moved > 0) { s_ringTexels += moved; s_ringPairs++; }
        }

        private static void SeamCheck(Heightmap a)
        {
            s_regenFrame[a] = Time.frameCount;
            int n = a.m_width + 1; Vector3 c = a.transform.position; float size = a.m_width * a.m_scale;
            SeamPair(a, Heightmap.FindHeightmap(c + new Vector3(size, 0f, 0f)), n, true, false);
            SeamPair(a, Heightmap.FindHeightmap(c + new Vector3(0f, 0f, size)), n, false, false);
            SeamPair(Heightmap.FindHeightmap(c - new Vector3(size, 0f, 0f)), a, n, true, true);
            SeamPair(Heightmap.FindHeightmap(c - new Vector3(0f, 0f, size)), a, n, false, true);
        }
        private static void SeamPair(Heightmap a, Heightmap b, int n, bool east, bool bIsNew)
        {
            if (a == null || b == null || a == b || a.IsDistantLod || b.IsDistantLod || a.m_paintMask == null || b.m_paintMask == null) return;
            if (!s_states.TryGetValue(a, out var sa) || !s_states.TryGetValue(b, out var sb) || sa.Plain == null || sb.Plain == null) return;
            var pa = a.m_paintMask.GetRawTextureData<Color32>(); var pb = b.m_paintMask.GetRawTextureData<Color32>();
            Mesh ma = a.m_renderMesh, mb = b.m_renderMesh; s_na.Clear(); s_nb.Clear(); if (ma != null) ma.GetNormals(s_na); if (mb != null) mb.GetNormals(s_nb);
            float dBase = 0f, dPaint = 0f, dHeight = 0f, dNormal = 0f;
            for (int t = 0; t < n; t++)
            {
                int ia = east ? t * n + n - 1 : (n - 1) * n + t, ib = east ? t * n : t;
                if (ia >= pa.Length || ib >= pb.Length || ia >= sa.Plain.Length || ib >= sb.Plain.Length) return;
                dBase = Mathf.Max(dBase, Mathf.Abs(SnowBaseMask.BaseG(a, ia) - SnowBaseMask.BaseG(b, ib)));
                dPaint = Mathf.Max(dPaint, Mathf.Abs(pa[ia].g - pb[ib].g) / 255f);
                dHeight = Mathf.Max(dHeight, Mathf.Abs(sa.Plain[ia].y + a.transform.position.y - sb.Plain[ib].y - b.transform.position.y));
                if (ia < s_na.Count && ib < s_nb.Count) dNormal = Mathf.Max(dNormal, Vector3.Angle(s_na[ia], s_nb[ib]));
            }
            bool open = dBase > 0.01f || dPaint > 0.01f || dHeight > 0.02f || dNormal > 5f;
            Vector3 ca = a.transform.position;
            long key = ((long)Mathf.RoundToInt(ca.x) << 33) ^ ((long)Mathf.RoundToInt(ca.z) << 1) ^ (east ? 1L : 0L);
            string now = open ? $"base {dBase:0.000}, paint {dPaint:0.000}, height {dHeight:0.00} m, normal {dNormal:0} deg" : null;
            s_seams.TryGetValue(key, out string was);
            if (now == was) return;
            if (now == null) s_seams.Remove(key); else s_seams[key] = now;
            s_regenFrame.TryGetValue(a, out int fa); s_regenFrame.TryGetValue(b, out int fb);
            FiresLogger.LogDebug($"[SnowSeam] frame {Time.frameCount} ({ca.x:0},{ca.z:0}) {(east ? "east" : "north")} edge {(open ? "OPEN: " + now : "closed, was: " + was)}; this run regenerated {(bIsNew ? "second" : "first")} chunk; last regen frames {fa} / {fb}; last writer {s_writer}");
        }

        internal static void Tick()
        {
            // 0.2.1170: the per-frame flash probe is off: delete with the rest once Fire is happy.
            if (s_dirty.Count > 0)
            {
                Vector3 me = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : (GameCamera.instance != null ? GameCamera.instance.transform.position : Vector3.zero);
                int best = -1; float bestD = float.MaxValue;
                for (int i = s_dirty.Count - 1; i >= 0; i--)
                {
                    var h = s_dirty[i];
                    if (h == null) { s_dirty.RemoveAt(i); if (best > i) best--; continue; }
                    float d = (h.transform.position - me).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = i; }
                }
                if (best >= 0)
                {
                    var hm = s_dirty[best];
                    s_dirty.RemoveAt(best);
                    try { Apply(hm); } catch (System.Exception e) { FiresLogger.LogDebug($"[SnowMesh] apply failed: {e.Message}"); }
                }
            }
            if (Time.unscaledTime >= s_sayAt)
            {
                if (s_sayAt > 0f && (s_writes > 0 || s_skips > 0)) FiresLogger.LogDebug($"[SnowMesh] last {SayEvery:0} s: {s_writes} chunk(s) written, {s_skips} unchanged, {s_raised} vertices raised, {s_ms:0.0} ms (worst {s_worst:0.00} ms, budget {BudgetMs:0}), {s_dirty.Count} waiting");
                SnowPerfTimers.Say(); s_sayAt = Time.unscaledTime + SayEvery; s_writes = s_skips = s_raised = 0; s_ms = s_worst = 0;
            }
        }

        // 0.2.1127 (Fire on 1126: "rebuilds now constantly flash between states"; log: 4,000+ vertices raised at z 8512, inside the arc): the shader's own test, written out, because
        // FAT patches WorldGenerator.IsDeepnorth (DeepNorthMapPatch) for its own maps. The patched answer said "outside" where the shader raises, so that ground was raised twice.
        private static bool InShaderArc(float x, float z)
        {
            float edge = 12000f + Mathf.Sin(Mathf.Atan2(x, z) * 20f) * 100f;
            return x * x + (z + 4000f) * (z + 4000f) > edge * edge;
        }

        private static readonly int s_shaderDisplace = Shader.PropertyToID("_FiresSnowDisplace");
        internal static bool ShaderDraws(Heightmap hm) { var r = hm != null ? hm.GetComponent<MeshRenderer>() : null; return r != null && r.sharedMaterial != null && r.sharedMaterial.HasProperty(s_shaderDisplace); }

        private static void Apply(Heightmap hm)
        {
            if (!s_states.TryGetValue(hm, out var st) || st.Plain == null || hm.m_renderMesh == null || hm.m_paintMask == null) return;
            long t0 = Stopwatch.GetTimestamp();
            string why = st.Why ?? "paint change"; st.Why = null;
            // 0.2.1135 (Fire chose a shader outside the arc; agreed with [seasons]): a material declaring this draws the snow itself, so the raise clears here. No mod dependency, any load order.
            var drawnBy = hm.GetComponent<MeshRenderer>(); bool shaderDraws = drawnBy != null && drawnBy.sharedMaterial != null && drawnBy.sharedMaterial.HasProperty(s_shaderDisplace);
            bool on = !shaderDraws && SnowConfig.NaturalSnow && SnowField.CoreGroundOn && !SnowFat.OwnsZone(ZoneSystem.GetZone(hm.transform.position));
            int n = hm.m_width + 1, count = n * n;
            var paint = hm.m_paintMask.GetRawTextureData<Color32>();
            // 0.2.1125: the texture has mips, so its raw data is longer than the 65 x 65 top level, which comes first (1124 returned here silently on every chunk: no raise at all)
            if (st.Plain.Length != count || paint.Length < count) { FiresLogger.LogDebug($"[SnowMesh] chunk ({hm.transform.position.x:0},{hm.transform.position.z:0}) skipped: {st.Plain.Length} vertices, {paint.Length} paint texels, want {count}"); return; }
            bool moved = st.MustWrite; int raised = 0; float top = 0f;
            float half = hm.m_width * hm.m_scale * 0.5f; Vector3 c = hm.transform.position;
            bool inArc = true;   // the whole chunk inside the arc: nothing to do
            for (int corner = 0; corner < 4 && inArc; corner++)
                inArc = InShaderArc(c.x + ((corner & 1) == 0 ? -half : half), c.z + ((corner & 2) == 0 ? -half : half));
            for (int i = 0; i < count; i++)
            {
                float h = 0f;
                if (on && !inArc)
                {
                    float natural = SnowBaseMask.BaseG(hm, i);
                    var col = paint[i];
                    float g = col.g / 255f;
                    // above the natural snow is cultivated soil or a pile, not snow; dirt and paving take the raise away as the shader's own term does
                    if (natural > 0f && g > 0f && g <= natural + 0.01f)
                    {
                        float bare = Mathf.Clamp01(col.b / 255f * 10f + col.r / 255f);
                        h = Mathf.GammaToLinearSpace(g) * 2.1f * (1f - bare);   // 0.2.1138: the shader's own curve, so a chunk without the shader draws the same height
                        if (h > 0f && InShaderArc(c.x + st.Plain[i].x, c.z + st.Plain[i].z)) h = 0f;   // vanilla raises it here
                    }
                }
                if (Mathf.Abs(h - st.Offset[i]) >= MoveEpsilon || (h == 0f) != (st.Offset[i] == 0f)) moved = true;
                st.Offset[i] = h;
                if (h > 0f) { raised++; if (h > top) top = h; }
            }
            if (!moved) { s_skips++; Note(t0, hm, why, raised, top, false); return; }
            st.MustWrite = false; if (FiresLogger.DebugEnabled) NoteMoves(hm, st, why, count);
            s_read.Clear();
            for (int i = 0; i < count; i++) { var v = st.Plain[i]; v.y += st.Offset[i]; s_read.Add(v); }
            var mesh = hm.m_renderMesh;
            mesh.SetVertices(s_read);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            s_writes++; s_raised += raised; s_writeFrame = Time.frameCount;
            Note(t0, hm, why, raised, top, true);
        }

        // 0.2.1131 (Fire's flash: a dig only goes down, yet normals go A-B-A): Debug only. Every mesh write compares the whole chunk with the raise of its last write, which survives a full rebuild.
        private static void NoteMoves(Heightmap hm, State st, string why, int count)
        {
            bool first = st.Last == null || st.Last.Length != count;
            if (first) st.Last = new float[count];
            int up = 0, down = 0; float maxUp = 0f, maxDown = 0f;
            for (int i = 0; i < count; i++)
            {
                float d = st.Offset[i] - st.Last[i]; st.Last[i] = st.Offset[i];
                if (d >= MoveEpsilon) { up++; if (d > maxUp) maxUp = d; }
                else if (d <= -MoveEpsilon) { down++; if (-d > maxDown) maxDown = -d; }
            }
            FiresLogger.LogDebug($"[SnowFlash] frame {Time.frameCount} write: chunk ({hm.transform.position.x:0},{hm.transform.position.z:0}) after {why}{(first ? " (first)" : "")}: {up} up (max {maxUp:0.00} m), {down} down (max {maxDown:0.00} m)");
        }

        private static void Note(long t0, Heightmap hm, string why, int raised, float top, bool wrote)
        {
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            s_ms += ms; if (ms > s_worst) s_worst = ms;
            if (FiresLogger.DebugEnabled) s_writer = $"{why} ({(wrote ? "wrote" : "no change")}, chunk {hm.transform.position.x:0},{hm.transform.position.z:0})"; if (FiresLogger.DebugEnabled) FiresLogger.LogDebug($"[SnowMesh] chunk ({hm.transform.position.x:0},{hm.transform.position.z:0}) after {why}: {(wrote ? "mesh written" : "nothing moved")}, {raised} vertices raised, top {top:0.00} m, {ms:0.00} ms");
        }
    }

    /// <summary>0.2.1131 (Fire: "as close to 60fps as possible"; plan: timers first): where winter main-thread time goes. Reads only; the DeepSnowG parts run only with Debug on.</summary>
    internal static class SnowPerfTimers
    {
        internal static long RefreshTicks, RegenerateTicks, DriftTicks, TexelTicks, PathTicks, ShelterTicks, GroundTicks, WinterTicks, CellsTicks;
        internal static int Refreshes, Regenerates, Samples, SkippedRegens;
        private static readonly System.Collections.Generic.Dictionary<string, long> s_listeners = new System.Collections.Generic.Dictionary<string, long>();
        internal static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        internal static void Listener(System.Delegate d, long start)
        {
            string k = d.Method.DeclaringType != null ? d.Method.DeclaringType.FullName + "." + d.Method.Name : d.Method.Name;
            s_listeners.TryGetValue(k, out long was); s_listeners[k] = was + Stopwatch.GetTimestamp() - start;
        }
        internal static void Say()
        {
            if (FiresLogger.DebugEnabled && (Refreshes > 0 || Regenerates > 0 || SkippedRegens > 0 || Samples > 0 || s_listeners.Count > 0 || CellsTicks > 0))
            {
                var sb = new System.Text.StringBuilder();
                foreach (var kv in s_listeners) sb.Append($"{kv.Key} {Ms(kv.Value):0.0}; ");
                FiresLogger.LogDebug($"[SnowPerf] last 10 s: base mask refresh {Refreshes} = {Ms(RefreshTicks):0.0} ms; vanilla Regenerate {Regenerates} = {Ms(RegenerateTicks):0.0} ms, {SkippedRegens} skipped by a prefix; CellsChanged listeners {Ms(CellsTicks):0.0} ms; DeepSnowG {Samples} samples: winter {Ms(WinterTicks):0.0}, drift {Ms(DriftTicks):0.0}, texel {Ms(TexelTicks):0.0}, path {Ms(PathTicks):0.0}, shelter {Ms(ShelterTicks):0.0}, ground {Ms(GroundTicks):0.0} ms; ZoneChanged listeners ms: {sb}");
            }
            RefreshTicks = RegenerateTicks = DriftTicks = TexelTicks = PathTicks = ShelterTicks = GroundTicks = WinterTicks = CellsTicks = 0; Refreshes = Regenerates = Samples = SkippedRegens = 0; s_listeners.Clear();
        }
    }

}