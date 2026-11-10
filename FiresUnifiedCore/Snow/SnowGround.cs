using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// 0.2.1003 ([wishbone] + [saddles]; Fire 10-29: "get the seams fixed and the snow working properly everywhere not just the deep
    /// north"): what the ground under a point does to the snow, as one 0..1 factor on Core's snow cover and depth, so every consumer
    /// (FAT's heightmap snow skin samples it per vertex, the Deep North inject, the piece snow) gets the same shape:
    ///  C1 paved ground (vanilla paint blue > 0.5: roads, paved stone) has no snow; bilinear on the paint mask, so it eases to full
    ///     over about one texel (1 m) instead of a cliff at the path edge.
    ///  C2 built pieces: within a placed piece's footprint (+ EdgeMetres) the snow eases to 0, so it tucks under walls and floors
    ///     instead of standing as a slab beside them. Footprints are cached per zone (collider bounds, rebuilt when a piece is
    ///     placed or removed in that zone), so a call is a short list scan.
    /// Both depend only on world position, so the two sides of any zone line compute the same value (C4).
    /// </summary>
    public static class SnowGround
    {
        private const float EdgeMetres = 0.75f;
        private static readonly AccessTools.FieldRef<Heightmap, Texture2D> s_paintMask = AccessTools.FieldRefAccess<Heightmap, Texture2D>("m_paintMask");
        private static readonly Dictionary<Vector2s, Dictionary<long, List<Bounds>>> s_pieces = new Dictionary<Vector2s, Dictionary<long, List<Bounds>>>();
        private static long s_rebuildTicks, s_pavedTicks, s_pieceTicks; private static int s_rebuilds;   // 0.2.1008 split timing
        private static readonly HashSet<Vector2s> s_dirty = new HashSet<Vector2s>();
        private static readonly Dictionary<Vector2s, float> s_builtAt = new Dictionary<Vector2s, float>();
        private static bool s_infoShown;
        private static int s_paved, s_tucked, s_calls, s_hits, s_gen; private static long s_ticks; private static float s_nextWhy, s_nextHere;
        private static readonly Dictionary<long, (float f, int gen, float at)> s_cache = new Dictionary<long, (float, int, float)>();

        // 0.2.1178: 1086 emptied SnowConfig.Bind, the only caller, so this command had not existed since. Registers on vanilla InitTerminal, as core_snow_probe does.
        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class Terminal_InitTerminal_SnowAt { private static bool s_done; private static void Postfix() { if (s_done) return; s_done = true; RegisterCommand(); } }

        private static void RegisterCommand()
        {
            // 0.2.1014 (Fire 10-30: "i want this fucking terrain situation worked out, the seams and the snow trails"; [saddles]' proof
            // round): Core's whole answer at a point, so each of Fire's spots can be read against FAT's skin in one line per point
            new Terminal.ConsoleCommand("fires_snow_at", "[x z ...] - Core snow at each point (default: where you stand): depth, cover, build-up, paved / piece ground factor, vanilla paint G on both sides of a nearby zone border (Info lines)", args =>
            {
                var pts = new List<Vector3>();
                for (int i = 1; i + 1 < args.Length; i += 2)
                    if (float.TryParse(args[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                        && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z)) pts.Add(new Vector3(x, 0f, z));
                if (pts.Count == 0 && Player.m_localPlayer != null) pts.Add(Player.m_localPlayer.transform.position);
                foreach (var p in pts)
                {
                    string line = "[Snow] at " + Probe(p);
                    FiresLogger.LogInfo(line); args.Context?.AddString(line);
                    foreach (var c in Colliders(p)) { FiresLogger.LogInfo(c); args.Context?.AddString(c); }
                }
            });
        }

        // 0.2.1020 ([lead] / [saddles] 10-30: with 1019 the well rim at (-349,152) still read piece 1.00 and StartTemple's zone listed
        // 0 big location colliders): every collider within 1 m of the point, ground - 1 m to ground + 3 m, with what owns it, so the
        // log says why Footprints doesn't see it (not under a Location, a Location in another zone, a size / trigger / layer filter)
        internal static List<string> Colliders(Vector3 p)
        {
            var lines = new List<string>();
            try
            {
                float g = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(p) : p.y;
                var hits = Physics.OverlapCapsule(new Vector3(p.x, g - 1f, p.z), new Vector3(p.x, g + 3f, p.z), 1f, ~0, QueryTriggerInteraction.Collide);
                lines.Add($"[Snow] colliders at ({p.x:0.0},{p.z:0.0}) ground {g:0.00}: {hits.Length}");
                foreach (var col in hits)
                {
                    if (col == null) continue;
                    var b = col.bounds; var loc = col.GetComponentInParent<Location>();
                    string chain = ""; var t = col.transform;
                    for (int i = 0; t != null && i < 8; i++, t = t.parent) chain += (i > 0 ? " < " : "") + t.name;
                    if (t != null) chain += " < ...";
                    string owner = loc != null ? $"Location '{loc.name}' at ({loc.transform.position.x:0},{loc.transform.position.z:0}) zone {ZoneSystem.GetZone(loc.transform.position).x},{ZoneSystem.GetZone(loc.transform.position).y}, listed {(s_allLocations().Contains(loc) ? "yes" : "NO")}" : "no Location above";
                    lines.Add($"[Snow]   '{col.name}' {col.GetType().Name} layer {LayerMask.LayerToName(col.gameObject.layer)} size {b.size.x:0.0} x {b.size.y:0.0} x {b.size.z:0.0} top {b.max.y - g:0.00} m over ground, trigger {col.isTrigger}, enabled {col.enabled}; {owner}; WearNTear {(col.GetComponentInParent<WearNTear>() != null ? "yes" : "no")}, Heightmap {(col.GetComponentInParent<Heightmap>() != null ? "yes" : "no")}; {chain}");
                }
                Location near = null; float nd = float.MaxValue;
                foreach (var l in s_allLocations()) { if (l == null) continue; float d = (l.transform.position - p).sqrMagnitude; if (d < nd) { nd = d; near = l; } }
                if (near != null) lines.Add($"[Snow]   nearest listed Location '{near.name}' {Mathf.Sqrt(nd):0} m away, zone {ZoneSystem.GetZone(near.transform.position).x},{ZoneSystem.GetZone(near.transform.position).y}, {near.GetComponentsInChildren<Collider>(true).Length} child collider(s)");
            }
            catch (Exception e) { lines.Add($"[Snow] colliders at ({p.x:0},{p.z:0}): failed: {e.Message}"); }
            return lines;
        }

        internal static string Probe(Vector3 p)
        {
            try
            {
                s_lastWhy = null;
                float pv = Paved(p), pf = PieceFactor(p);
                string ground = $"ground factor {(1f - pv) * pf:0.00} = paved {pv:0.00}, piece {pf:0.00}{(pf < 1f ? " from " + s_lastWhy : "")}";
                // the vanilla paint G (snow depth in Deep North / Core's inject) here, and on each side of a zone border within 2 m
                string G(Vector3 q) { var hm = Heightmap.FindHeightmap(q); var t = hm != null ? s_paintMask(hm) : null; if (t == null) return "-"; float s = hm.m_width * hm.m_scale; Vector3 c = hm.transform.position; try { return t.GetPixelBilinear((q.x - (c.x - s * 0.5f)) / s, (q.z - (c.z - s * 0.5f)) / s).g.ToString("0.000"); } catch { return "?"; } }
                string border = "";
                float zs = ZoneSystem.c_ZoneSize, half = zs * 0.5f;
                float bx = Mathf.Round((p.x - half) / zs) * zs + half, bz = Mathf.Round((p.z - half) / zs) * zs + half;   // nearest zone edge lines
                if (Mathf.Abs(p.x - bx) < 2f) border += $"; border x {bx:0}: G {G(new Vector3(bx - 0.3f, 0f, p.z))} | {G(new Vector3(bx + 0.3f, 0f, p.z))}";
                if (Mathf.Abs(p.z - bz) < 2f) border += $"; border z {bz:0}: G {G(new Vector3(p.x, 0f, bz - 0.3f))} | {G(new Vector3(p.x, 0f, bz + 0.3f))}";
                // 0.2.1022 ([lead] / [saddles] 10-30, StartTemple well on a 1.3 m straight-edged block): the ground here vs the world
                // generator's height, so a saved terrain edit (raise / level, or a location's flatten) shows as its own number
                float gnd = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(p) : 0f, gen = WorldGenerator.instance != null ? WorldGenerator.instance.GetHeight(p.x, p.z) : 0f;
                return $"{SnowField.Unbudgeted(() => SnowField.Explain(p))}; {ground}; paint G {G(p)}, zone {ZoneSystem.GetZone(p).x},{ZoneSystem.GetZone(p).y}{border}; ground {gnd:0.00} vs generated {gen:0.00} (edit {gnd - gen:+0.00;-0.00} m)";
            }
            catch (Exception e) { return $"({p.x:0},{p.z:0}): probe failed: {e.Message}"; }
        }

        /// <summary>0..1: 0 on paved ground and inside a piece footprint, 1 in the open.</summary>
        public static float FactorAt(Vector3 p)
        {
            if (!SnowConfig.P("Snow clears paved ground and meets buildings", true)) return 1f;
            // 0.2.1180 (proof: unfilled paint read 260 of 260 paved, blue 0.804): vanilla Generate asks for the base mask before it fills the paint mask and the heights, so neither can be read on a chunk's first build. No ground rule here and nothing cached; SnowBaseMask's Generate postfix runs the rules on the real paint and heights in the same frame.
            // 0.2.1182: the unbuilt-chunk check moved to the cache miss below; it ran on every sample before.
            s_calls++;
            // 0.2.1007 (Fire 10-30 via [lead]: standing fps; fire_hand_0005 showed 91,000-128,000 samples per 30 s standing still, each
            // two heightmap lookups + a paint-mask read + 9 zone lists): the answer is cached per 0.5 m cell, dropped when a piece
            // changes (PieceChanged) and after 5 s (paint: paths / cultivate), so a standing player costs a dictionary lookup per sample
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long key = ((long)Mathf.FloorToInt(p.x * 2f) << 32) ^ (uint)Mathf.FloorToInt(p.z * 2f);
            float now = Time.unscaledTime, f;
            if (SnowConfig.P("Cache snow ground rule per half metre", true) && s_cache.TryGetValue(key, out var c) && c.gen == s_gen && now - c.at < 5f) { f = c.f; s_hits++; }
            else
            {
                if (SnowConfig.FirstBuildSnow) { var first = HmAt(p); if (first != null && s_hmBuildData(first) == null) { s_unbuiltReads++; return 1f; } }
                f = 1f - Paved(p);
                long t1 = System.Diagnostics.Stopwatch.GetTimestamp(); s_pavedTicks += t1 - t0;
                if (f > 0f) f *= PieceFactor(p);
                s_pieceTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t1;   // includes footprint rebuilds (also counted on their own)
                if (s_cache.Count > 200000) s_cache.Clear();
                s_cache[key] = (f, s_gen, now);
            }
            s_ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            if (!s_infoShown && f < 1f) { s_infoShown = true; FiresLogger.LogInfo("[Snow] ground rules on: no snow on paved ground, snow eases to nothing at buildings"); }
            // 0.2.1006: the proof at the PLAYER's own spot every 10 s (what Fire's trails depend on)
            if (Time.unscaledTime >= s_nextHere && Player.m_localPlayer != null)
            {
                s_nextHere = Time.unscaledTime + 10f;
                Vector3 me = Player.m_localPlayer.transform.position;
                s_lastWhy = null; float pv = Paved(me), pf = PieceFactor(me);
                FiresLogger.LogDebug($"[Snow] at the player ({me.x:0},{me.z:0}): ground factor {(1f - pv) * pf:0.00} = paved {pv:0.00} (clears), piece {pf:0.00}{(pf < 1f ? " from " + s_lastWhy : "")}; world build-up {SnowField.WorldBuildUp:0.00}, winter cover {SnowField.WinterCover:0.00}");
            }
            if (Time.unscaledTime >= s_nextWhy)
            {
                s_nextWhy = Time.unscaledTime + 30f;
                if (s_calls > 0) FiresLogger.LogDebug($"[Snow] ground rules last 30 s: {s_calls} sample(s) ({s_hits} from the 0.5 m cache), {s_ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 30.0:0.000} ms/s main thread (paved {s_pavedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 30.0:0.000}, pieces {s_pieceTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 30.0:0.000} of which {s_rebuilds} footprint rebuild(s) {s_rebuildTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / 30.0:0.000}), {s_paved} on paved ground (cleared), {s_tucked} next to / under a piece (eased); {s_pieces.Count} zone footprint list(s), {s_cache.Count} cell(s) cached");
                if (s_unbuiltReads > 0) FiresLogger.LogDebug($"[SnowLoad] ground rules last 30 s: {s_unbuiltReads} read(s) on a chunk still in its first build");
                s_unbuiltReads = 0;
                s_calls = s_paved = s_tucked = s_hits = s_rebuilds = 0; s_ticks = s_pavedTicks = s_pieceTicks = s_rebuildTicks = 0;
            }
            return f;
        }

        // 0.2.1011 (Fire 10-30 live on 1010: the paved read was the top cost, 26-32 ms/s of 44-51): Heightmap.FindHeightmap walks every
        // loaded heightmap per call, twice per sample (paved + ground). Samples come in runs over one zone (a skin, a chunk), so the last
        // heightmap is kept while p stays inside its square; a destroyed one (zone unload) reads as null and is looked up again.
        private static Heightmap s_hm; private static float s_hmMinX, s_hmMinZ, s_hmMaxX, s_hmMaxZ;
        private static Heightmap HmAt(Vector3 p)
        {
            if (s_hm != null && p.x >= s_hmMinX && p.x < s_hmMaxX && p.z >= s_hmMinZ && p.z < s_hmMaxZ) return s_hm;
            var hm = Heightmap.FindHeightmap(p);
            if (hm != null)
            {
                float half = hm.m_width * hm.m_scale * 0.5f; Vector3 c = hm.transform.position;
                s_hm = hm; s_hmMinX = c.x - half; s_hmMaxX = c.x + half; s_hmMinZ = c.z - half; s_hmMaxZ = c.z + half;
            }
            return hm;
        }

        // 0.2.1179 ([wishbone], Fire: "all mud and then the snow pops in"): Debug only, reads only. Vanilla Generate makes the paint mask, then RequestTerrainSync (our base mask reads Paved here), and only then SetPixels. Counts paved reads of a chunk with no build data yet, with the blue value read, to prove whether unfilled paint reads as paved.
        private static readonly AccessTools.FieldRef<Heightmap, HeightmapBuilder.HMBuildData> s_hmBuildData = AccessTools.FieldRefAccess<Heightmap, HeightmapBuilder.HMBuildData>("m_buildData");
        private static int s_unbuiltReads;
        private static int s_unfilledReads, s_unfilledPaved, s_filledReads, s_filledPaved; private static float s_unfilledBlue, s_unfilledSayAt;
        private static void NoteUnfilled(Heightmap hm, float blue, float paved)
        {
            if (s_hmBuildData(hm) == null) { s_unfilledReads++; s_unfilledBlue = blue; if (paved > 0.5f) s_unfilledPaved++; }
            else { s_filledReads++; if (paved > 0.5f) s_filledPaved++; }
            float now = Time.unscaledTime;
            if (now < s_unfilledSayAt) return;
            s_unfilledSayAt = now + 10f;
            if (s_unfilledReads > 0) FiresLogger.LogDebug($"[SnowLoad] paved reads last 10 s: unfilled paint {s_unfilledReads} read(s), {s_unfilledPaved} paved, last blue {s_unfilledBlue:0.000}; filled paint {s_filledReads} read(s), {s_filledPaved} paved");
            s_unfilledReads = s_unfilledPaved = s_filledReads = s_filledPaved = 0;
        }

        /// <summary>0.2.1181: marks the texels of a freshly built chunk the ground rules can touch: paved paint (with its 1 m bilinear ease) or a 4 m footprint cell of this zone or the 8 around it. Same cells PieceFactor reads. False when there are none.</summary>
        internal static bool MarkRuleTexels(Heightmap hm, Vector3 origin, float scale, int n, bool[] marks)
        {
            System.Array.Clear(marks, 0, marks.Length);
            bool any = false;
            var tex = s_paintMask(hm);
            if (tex != null)
            {
                var px = tex.GetPixels32();
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        int i = y * n + x;
                        if (i >= px.Length || px[i].b <= 63) continue;   // 63 = the 0.25 where Paved starts
                        any = true;
                        for (int a = Mathf.Max(0, y - 1); a <= Mathf.Min(n - 1, y + 1); a++)
                            for (int b = Mathf.Max(0, x - 1); b <= Mathf.Min(n - 1, x + 1); b++) marks[a * n + b] = true;
                    }
            }
            var z = ZoneSystem.GetZone(hm.transform.position);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    var zone = new Vector2s(z.x + dx, z.y + dy);
                    var grid = Footprints(zone);
                    if (grid != null) foreach (var k in grid.Keys) any |= MarkCell(k, origin, scale, n, marks);
                    if (s_big.TryGetValue(zone, out var big) && big != null) foreach (var k in big.Keys) any |= MarkCell(k, origin, scale, n, marks);
                }
            return any;
        }

        private static bool MarkCell(long key, Vector3 origin, float scale, int n, bool[] marks)
        {
            float wx = (int)(key >> 32) * GridM, wz = (int)(uint)key * GridM;
            int x0 = Mathf.Max(0, Mathf.CeilToInt((wx - origin.x) / scale)), x1 = Mathf.Min(n - 1, Mathf.FloorToInt((wx + GridM - origin.x) / scale));
            int y0 = Mathf.Max(0, Mathf.CeilToInt((wz - origin.z) / scale)), y1 = Mathf.Min(n - 1, Mathf.FloorToInt((wz + GridM - origin.z) / scale));
            if (x0 > x1 || y0 > y1) return false;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++) marks[y * n + x] = true;
            return true;
        }

        // C1: the vanilla paint mask's paved channel at p (bilinear = a ~1 m ease at the edge)
        private static float Paved(Vector3 p)
        {
            var hm = HmAt(p);   // 0.2.1011
            if (hm == null) return 0f;
            var tex = s_paintMask(hm); if (tex == null) return 0f;
            float size = hm.m_width * hm.m_scale; Vector3 c = hm.transform.position;
            float u = (p.x - (c.x - size * 0.5f)) / size, v = (p.z - (c.z - size * 0.5f)) / size;
            float b;
            try { b = tex.GetPixelBilinear(u, v).b; } catch { return 0f; }
            float paved = Mathf.Clamp01((b - 0.25f) / 0.35f);   // vanilla paved = blue 1; the ease runs over the bilinear ramp
            if (FiresLogger.DebugEnabled) NoteUnfilled(hm, b, paved);
            if (paved > 0.5f) s_paved++;
            return paved;
        }

        // C2: distance (x / z) from p to the nearest piece footprint of its zone and the 8 around it.
        // 0.2.1006 (Fire 10-30 live on 1005: "no snow trails in the meadows snow"; FAT's log: "player on Meadows at (-327,130), depth
        // there 0.00 m", and 1005's ground rules counted 9,377 samples "next to / under a piece"): the per-piece UNION box of a town
        // piece (towers, roofs, long walls) covered whole yards, so open ground read "under a piece" = 0 snow = no trail. Now each
        // COLLIDER is its own footprint, boxes wider than MaxFootprint are skipped, and only a box standing ON the ground counts
        // (its bottom within GroundBand of the ground under p: a wall / post, not a floor or roof above you); p.y is not trusted
        // (callers pass any y), the ground height is read here.
        private const float MaxFootprint = 6f, GroundBand = 0.5f;
        internal static string s_lastWhy;
        private static float PieceFactor(Vector3 p)
        {
            var z = ZoneSystem.GetZone(p);
            float ground = Ground(p);
            float best = EdgeMetres; Bounds hit = default;
            long cell = CellKey(Mathf.FloorToInt(p.x / GridM), Mathf.FloorToInt(p.z / GridM));
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                {
                    var grid = Footprints(new Vector2s(z.x + dx, z.y + dy));
                    if (grid == null || !grid.TryGetValue(cell, out var list)) continue;
                    foreach (var b in list)
                    {
                        if (b.min.y > ground + GroundBand || b.max.y < ground - 0.25f) continue;   // floating above / sunk below the ground here
                        float ox = Mathf.Max(0f, Mathf.Max(b.min.x - p.x, p.x - b.max.x)), oz = Mathf.Max(0f, Mathf.Max(b.min.z - p.z, p.z - b.max.z));
                        float d = Mathf.Sqrt(ox * ox + oz * oz);
                        if (d < best) { best = d; hit = b; if (best <= 0f) { s_tucked++; s_lastWhy = Describe(hit); return 0f; } }
                    }
                }
            // 0.2.1019: big location colliders (a combined temple mesh) by their real shape: spheres of growing radius at 0.35 m over the
            // ground (inside the snow layer); the smallest one that touches the collider is the distance to it (0, 0.25, 0.5, 0.75 m)
            if (best > 0f)
            {
                Vector3 q = new Vector3(p.x, ground + 0.35f, p.z); bool bigHit = false;
                for (int dx = -1; dx <= 1 && best > 0f; dx++)
                    for (int dy = -1; dy <= 1 && best > 0f; dy++)
                    {
                        if (!s_big.TryGetValue(new Vector2s(z.x + dx, z.y + dy), out var bg) || bg == null || !bg.TryGetValue(cell, out var cols)) continue;
                        foreach (var col in cols)
                        {
                            if (col == null || !col.enabled) continue;
                            for (int ri = 0; ri < BigRadii.Length && BigRadii[ri] < best; ri++)
                            {
                                int n = Physics.OverlapSphereNonAlloc(q, Mathf.Max(0.05f, BigRadii[ri]), s_overlap, 1 << col.gameObject.layer, QueryTriggerInteraction.Ignore);
                                bool touch = false; for (int i = 0; i < n; i++) if (s_overlap[i] == col) { touch = true; break; }
                                if (!touch) continue;
                                best = BigRadii[ri]; bigHit = true; s_lastWhy = $"location collider '{col.name}' ({col.bounds.size.x:0} x {col.bounds.size.z:0} m) within {best:0.00} m";
                                break;
                            }
                            if (best <= 0f) break;
                        }
                    }
                if (best <= 0f) { s_tucked++; return 0f; }
                if (bigHit && best < EdgeMetres) { s_tucked++; return Mathf.SmoothStep(0f, 1f, best / EdgeMetres); }
            }
            if (best < EdgeMetres) { s_tucked++; s_lastWhy = Describe(hit); return Mathf.SmoothStep(0f, 1f, best / EdgeMetres); }
            return 1f;
        }
        private static readonly float[] BigRadii = { 0f, 0.25f, 0.5f };
        private static readonly Collider[] s_overlap = new Collider[64];
        private static readonly Dictionary<Vector2s, long> s_sig = new Dictionary<Vector2s, long>();   // 0.2.1024: last footprint result per zone
        internal static List<Vector2s> BuiltZones() => new List<Vector2s>(s_sig.Keys);
        private static readonly Dictionary<Vector2s, Dictionary<long, List<Collider>>> s_big =new Dictionary<Vector2s, Dictionary<long, List<Collider>>>();
        private static string Describe(Bounds b) => $"a footprint {b.size.x:0.0} x {b.size.z:0.0} m at {b.center.x:0},{b.center.z:0} (bottom {b.min.y:0.0})";
        private static float Ground(Vector3 p)
        {
            var hm = HmAt(p);   // 0.2.1011
            return hm != null && hm.GetWorldHeight(p, out float h) ? h : p.y;
        }

        // 0.2.1008 (Fire 10-30 standing fps; 1007's own line: 97-289 ms/s main thread for 66k-117k samples / 30 s = ~90 us a sample,
        // while all footprint rebuilds together were ~1 ms/s (110 rebuilds of 1,000-1,500 colliders at 2-3 ms)): every sample walked
        // the WHOLE footprint list of 9 zones (~10,000 boxes in town). Each zone's footprints now sit in a 4 m grid (a box is listed in
        // every cell its EdgeMetres-grown outline touches), so a sample reads only the boxes in its own cell.
        private const float GridM = 4f;
        private static long CellKey(int x, int z) => ((long)x << 32) ^ (uint)z;
        private static Dictionary<long, List<Bounds>> Footprints(Vector2s zone)
        {
            bool have = s_pieces.TryGetValue(zone, out var cached);
            if (have && !s_dirty.Contains(zone)) return cached;
            // a zone loading creates its pieces one by one (each marks it dirty): rebuild at most once a second per zone, so the
            // per-sample cost stays a list scan; until then the previous list answers
            if (have && s_builtAt.TryGetValue(zone, out float at) && Time.unscaledTime - at < 1f) return cached;
            s_dirty.Remove(zone); s_builtAt[zone] = Time.unscaledTime;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var list = new Dictionary<long, List<Bounds>>(); int boxes = 0, statics = 0, bigCount = 0;
            var big = new Dictionary<long, List<Collider>>();
            bool Add(Collider col)
            {
                if (col == null || col.isTrigger || !col.enabled) return false;
                var cb = col.bounds;
                if (cb.size.x > MaxFootprint || cb.size.z > MaxFootprint) return false;   // roofs / big floors / towers: not a snow edge
                int x0 = Mathf.FloorToInt((cb.min.x - EdgeMetres) / GridM), x1 = Mathf.FloorToInt((cb.max.x + EdgeMetres) / GridM);
                int z0 = Mathf.FloorToInt((cb.min.z - EdgeMetres) / GridM), z1 = Mathf.FloorToInt((cb.max.z + EdgeMetres) / GridM);
                for (int gx = x0; gx <= x1; gx++)
                    for (int gz = z0; gz <= z1; gz++)
                    {
                        long k = CellKey(gx, gz);
                        if (!list.TryGetValue(k, out var l)) list[k] = l = new List<Bounds>(4);
                        l.Add(cb);
                    }
                return true;
            }
            foreach (var w in WearNTear.GetAllInstances())
            {
                if (w == null || ZoneSystem.GetZone(w.transform.position) != zone) continue;
                foreach (var col in w.GetComponentsInChildren<Collider>())   // 0.2.1006: each collider is a footprint (no union box)
                    if (Add(col)) boxes++;
            }
            // 0.2.1018 ([saddles] proof 10-30: with 0.66 m winter snow, StartTemple's well rim, low walls and steps were buried -
            // fires_snow_at at 4 spots read piece 1.00, i.e. nothing eased): a location's own static props (its children without a
            // WearNTear, not terrain) are footprints too, by the same rules (each collider, <= 6 m, standing on the ground)
            foreach (var loc in s_allLocations())
            {
                if (loc == null || ZoneSystem.GetZone(loc.transform.position) != zone) continue;
                foreach (var col in loc.GetComponentsInChildren<Collider>())
                {
                    if (col == null || col.GetType().Name == "TerrainCollider" || col.GetComponentInParent<WearNTear>() != null || col.GetComponentInParent<Heightmap>() != null) continue;
                    if (Add(col)) { statics++; continue; }
                    // 0.2.1019 ([saddles] / [visual] 10-30: StartTemple draws its whole architecture as ONE combined static mesh, far wider
                    // than 6 m, so its bounds box can't be a footprint): a big location collider is kept as the COLLIDER, listed in every
                    // 4 m cell its bounds cover, and PieceFactor asks the physics shape itself (an overlap at the ground) instead of a box
                    if (col.isTrigger || !col.enabled) continue;
                    var bb = col.bounds;
                    int bx0 = Mathf.FloorToInt((bb.min.x - EdgeMetres) / GridM), bx1 = Mathf.FloorToInt((bb.max.x + EdgeMetres) / GridM);
                    int bz0 = Mathf.FloorToInt((bb.min.z - EdgeMetres) / GridM), bz1 = Mathf.FloorToInt((bb.max.z + EdgeMetres) / GridM);
                    for (int gx = bx0; gx <= bx1; gx++)
                        for (int gz = bz0; gz <= bz1; gz++)
                        {
                            long k = CellKey(gx, gz);
                            if (!big.TryGetValue(k, out var l)) big[k] = l = new List<Collider>(2);
                            l.Add(col);
                        }
                    bigCount++;
                }
            }
            if (s_pieces.Count > 512) { s_pieces.Clear(); s_builtAt.Clear(); s_big.Clear(); s_sig.Clear(); }
            s_pieces[zone] = list; s_big[zone] = big;
            // 0.2.1024 ([saddles] 10-30, one path so FAT can drop its 1280 verifier): any rebuild whose result differs from the last one
            // raises CellsChanged zone-wide (the first build of a zone counts as a change); an unchanged rebuild stays quiet
            long sig = boxes * 1000003L + statics * 7919L + bigCount * 31L + list.Count;
            foreach (var kv in list) { sig = sig * 31 + kv.Key; foreach (var bb in kv.Value) sig = sig * 31 + Mathf.RoundToInt(bb.center.x * 4f) * 131 + Mathf.RoundToInt(bb.center.z * 4f); }
            if (!s_sig.TryGetValue(zone, out long was) || was != sig) { s_sig[zone] = sig; SnowField.NoteZoneCellsChanged(zone); }
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0; s_rebuildTicks += dt; s_rebuilds++;
            double ms = dt * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms > 2.0 || statics > 0 || bigCount > 0) FiresLogger.LogDebug($"[Snow] piece footprints of zone {zone.x},{zone.y}: {boxes} piece box(es) + {statics} location static(s) + {bigCount} big location collider(s) in {list.Count} 4 m cell(s), {ms:0.0} ms");
            return list;
        }

        private static readonly AccessTools.FieldRef<List<Location>> s_allLocations = AccessTools.StaticFieldRefAccess<List<Location>>(AccessTools.Field(typeof(Location), "s_allLocations"));

        // 0.2.1018: a location loading (its statics appear) rebuilds its zone's footprints like a piece does
        [HarmonyPatch(typeof(Location), "Awake")]
        private static class Location_Awake_SnowGround { private static void Postfix(Location __instance) { if (SnowConfig.P("Snow clears paved ground and meets buildings", true) && __instance != null) { PieceChanged(__instance.transform.position); s_announceWhole.Add(ZoneSystem.GetZone(__instance.transform.position)); } } }   // 0.2.1074: a location's colliders reach zone-wide

        // a piece placed or removed: its zone's footprints are rebuilt on the next sample, and the zone's snow is re-announced
        // (a zone load creates hundreds of pieces: the re-announce is coalesced per zone and sent at most once a second, from Flush)
        private static readonly HashSet<Vector2s> s_announce = new HashSet<Vector2s>();
        private static readonly Dictionary<Vector2s, Rect> s_announceArea = new Dictionary<Vector2s, Rect>();
        private static readonly HashSet<Vector2s> s_announceWhole = new HashSet<Vector2s>();
        private static float s_nextFlush;
        private static bool s_offWorldLogged;
        internal static void PieceChanged(Vector3 pos)
        {
            var z = ZoneSystem.GetZone(pos);
            s_dirty.Add(z); s_announce.Add(z);   // 0.2.1008: no longer wipes the cell cache (a zone load = hundreds of Awakes; the 5 s expiry covers it)
            // 0.2.1021: the piece's own reach (largest small footprint + the snow edge); a big location collider rebuild is zone-wide (Footprints)
            float r = MaxFootprint + EdgeMetres;
            // 0.2.1074: the union of the pieces' reach per zone goes out with ZoneAreaChanged (local rect, not the whole zone)
            var pr = new Rect(pos.x - r, pos.z - r, 2f * r, 2f * r);
            s_announceArea[z] = s_announceArea.TryGetValue(z, out var had) ? Rect.MinMaxRect(Mathf.Min(had.xMin, pr.xMin), Mathf.Min(had.yMin, pr.yMin), Mathf.Max(had.xMax, pr.xMax), Mathf.Max(had.yMax, pr.yMax)) : pr;
            // 0.2.1023 ([saddles] 10-30, FAT 1283 Debug: CellsChanged arrived for zone 1563,1563 at world 100000 and zone 0,0, never
            // the player's zones): pieces Awake off-world too (prefab / preview copies at the origin or far away); only a piece in a
            // loaded zone changes snow anyone draws
            if (ZoneSystem.instance != null && ZoneSystem.instance.IsZoneLoaded(pos)) SnowField.NoteCellsChanged(z, new Rect(pos.x - r, pos.z - r, 2f * r, 2f * r));
            else if (!s_offWorldLogged) { s_offWorldLogged = true; FiresLogger.LogDebug($"[Snow] CellsChanged: a piece / location at {pos.x:0},{pos.z:0} (zone {z.x},{z.y}, not loaded) skipped - an off-world copy, not snow anyone draws"); }
        }
        internal static void Flush()
        {
            if (s_announce.Count == 0 || Time.unscaledTime < s_nextFlush) return;
            s_nextFlush = Time.unscaledTime + 1f;
            foreach (var z in s_announce) SnowField.RaiseZoneChanged(z, !s_announceWhole.Contains(z) && s_announceArea.TryGetValue(z, out var a) ? a : (Rect?)null);
            s_announceArea.Clear(); s_announceWhole.Clear();
            FiresLogger.LogDebug($"[Snow] pieces placed / removed: {s_announce.Count} zone(s) re-announced (snow meets the new footprints)");
            s_announce.Clear();
        }

        // 0.2.1211 ([wishbone], [generator]'s piece-create audit rows 2 + 5, TOWN50): the per-piece hooks read ONE bool, worked out once per
        // frame: the key (was a SnowConfig.P string lookup + lock per piece, 1.45 us of WearNTear.Awake) and the ground-snow owner - while
        // Vedr owns the ground snow (SnowVedrGround.Owns, VedrApi.GroundSnowActive) Vedr's own WearNTear hook marks its zones and Core's
        // copy does nothing. Not unpatched at runtime: an unpatch rebuilds WearNTear.Awake with every other mod's patches (the quit-freeze
        // cost) at each owner flip; what stays is the Harmony call and one static read. Ownership back to Core re-announces the loaded snow
        // zones (SnowTicker), so footprints of pieces placed meanwhile are picked up.
        private static int s_hookFrame = -1;
        private static bool s_hookOn;
        internal static bool PieceHooksOn
        {
            get
            {
                int f = Time.frameCount;
                if (f != s_hookFrame) { s_hookFrame = f; s_hookOn = !SnowVedrGround.Owns && SnowConfig.P("Snow clears paved ground and meets buildings", true); }
                return s_hookOn;
            }
        }

        [HarmonyPatch(typeof(WearNTear), "Awake")]
        private static class WearNTear_Awake_SnowGround { private static void Postfix(WearNTear __instance) { if (PieceHooksOn && __instance != null) PieceChanged(__instance.transform.position); } }

        [HarmonyPatch(typeof(WearNTear), "OnDestroy")]
        private static class WearNTear_OnDestroy_SnowGround { private static void Prefix(WearNTear __instance) { if (PieceHooksOn && __instance != null) PieceChanged(__instance.transform.position); } }
    }
}
