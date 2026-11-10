using System;
using System.Collections.Generic;
using FiresCore.Bridge;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// Dynamic snow (Fire 10-24): one deterministic field every peer computes the same way, from world time, the season (Vedr through
    /// <see cref="SeasonBridge"/>) and the weather. Nothing streams. The only synced state is the per-zone path clear mask, written by
    /// the zone's terrain owner (<see cref="SnowPathMask"/>). Fires melt locally. Consumers:
    /// - vanilla terrain: <see cref="SnowHeightmapInject"/> raises the Deep North paint-mask G channel in each peer's LOCAL runtime
    ///   texture (never TerrainComp), so the look, the sink (Heightmap.GetHeightOffset) and footsteps follow;
    /// - FAT voxel zones: FAT pulls <see cref="FillZoneDepth"/> into its own depth texture on <see cref="ZoneChanged"/>;
    /// - pieces (FTS): <see cref="CoverageAt"/>.
    /// Add-only API. Ships OFF ([Snow] "Dynamic snow"); with it off every query answers 0 and nothing is patched into the terrain.
    /// </summary>
    public static class SnowField
    {
        /// <summary>Fixed melt radius of a burning fire (m). Fire 10-24: fixed, not config.</summary>
        public const float FireMeltRadius = 4f;
        /// <summary>Fixed melt radius of a pyre-class fire (m).</summary>
        public const float PyreMeltRadius = 20f;
        /// <summary>Path clear mask resolution per zone (cells per side; 64 m zone = 4 m cells).</summary>
        public const int MaskSize = 16;

        /// <summary>True when the feature is on and the world is up.</summary>
        public static bool Enabled => SnowConfig.On && ZNet.instance != null && WorldGenerator.instance != null;

        /// <summary>0.2.1197: Core's vanilla-heightmap snow writers run (Enabled, and Vedr does not draw the ground snow: SnowVedrGround).
        /// Every query (DepthAt, BaseAt, DeepGroundAt, ...) still answers on Enabled alone.</summary>
        internal static bool CoreGroundOn => Enabled && !SnowVedrGround.Owns;

        /// <summary>Year-round snow cover 0..1 for the snow bases (Mountain / Deep North, real or forced, Winter territories): never
        /// below <see cref="SummerFloor"/>, up to 1 in deep winter; the weather sets how fast it moves (Fire 10-24: these places keep
        /// snow all year like vanilla's Deep North; the season only scales it).</summary>
        public static float GlobalCover { get; internal set; }

        /// <summary>Extra winter snow 0..1 for the other biomes Vedr whitens in winter (0 outside winter).</summary>
        public static float WinterCover { get; internal set; }

        /// <summary>The snow bases' cover in high summer.</summary>
        public const float SummerFloor = 0.6f;

        /// <summary>Depth scale: DepthAt 1 × DepthScale 1 = vanilla Deep North's deepest (1.2 m, waist deep). FAT maps depth to metres with MaxDepthMetres.</summary>
        public static float DepthScale => 1f;

        /// <summary>Snow height in metres at DepthAt 1: vanilla Deep North's deepest (G 0.6 x Character.m_deepSnowSlowMaxHeight 2 m = 1.2 m, waist deep; Fire 10-24).</summary>
        // 0.2.840 ([seasons], Fire 10-26 deep snow on voxel ground): 2.0 = vanilla's own scale (G x Character.m_deepSnowSlowMaxHeight 2 m).
        // FillZoneDepth / DeepSnowG now write the same model as the vanilla terrain (base G 0.6 = 1.2 m, drifts / heavy snow to G 0.95),
        // so 1.0 = 2 m; FAT's voxel shader reads the global _FiresSnowMaxDepth (SnowTicker sets it to this).
        public const float MaxDepthMetres = 2.0f;
        /// <summary>0.2.841: the steepest the drawn voxel snow surface may rise, in G per metre (0.25 = 0.5 m per m).</summary>
        internal const float MaxRiseGPerM = 0.25f;

        /// <summary>0.2.840 (add-only): deep-snow depth 0..1 on vanilla's 2 m scale at a point (G: 0.6 = 1.2 m, up to 0.95), the
        /// SAME model the vanilla terrain gets (base x season, heavy snowfall, lee drifts behind walls / slopes), net of dug paths and
        /// before fire melt (shaders melt per pixel). For FAT voxel ground: the depth texture and the sink / slow hook.</summary>
        // 0.2.1189 (Fire 11-06 perf pass, SnowTicker cost): true when the last DeepSnowG took the snow-base branch (Deep North / Mountain weight),
        // the only branch wind drift reaches. Per thread (worker threads call DeepSnowG too); the SnowBaseMask batch reads it right after its own call.
        [ThreadStatic] internal static bool LastSnowBase;

        public static float DeepSnowG(Vector3 p)
        {
            LastSnowBase = false;
            if (!Enabled) return 0f;
            if (FiresLogger.DebugEnabled) return DeepSnowGTimed(p);   // 0.2.1131: the same sum, timed part by part
            float dnW = DeepGroundWeight(p), mW = MountainWeight(p);   // 0.2.914: + Mountain (its own lower cap)
            LastSnowBase = dnW > 0f || mW > 0f;
            if (dnW <= 0f && mW <= 0f) { float wg = WinterG(p); return wg > 0f ? wg * SnowGround.FactorAt(p) : 0f; }   // 0.2.935: seasonal snow outside the snow biomes; 0.2.1003 ground factor
            float heavy = SnowHeightmapInject.HeavyNow();
            SnowDrift.At(p, out float lee, out float windward);
            float g = SnowHeightmapInject.TexelG(p, heavy, lee, windward, dnW, mW);
            float clear = SnowPathMask.ClearAtSmooth(p);   // 0.2.841: smooth (was nearest 4 m cells)
            // 0.2.894: a mask already stamped on a FAT zone (before 894) no longer clears its depth: FAT's voxel snow keeps its own digs
            if (clear > 0f && SnowFat.OwnsZone(ZoneSystem.GetZone(p))) clear = 0f;
            return Mathf.Clamp01(g * (1f - clear) * SnowShelter.ShelterAt(p) * SnowGround.FactorAt(p));   // 0.2.927: closed rooms 0, drift near openings; 0.2.1003 paved + pieces
        }

        private static float DeepSnowGTimed(Vector3 p)
        {
            long t = System.Diagnostics.Stopwatch.GetTimestamp(), n; SnowPerfTimers.Samples++;
            float dnW = DeepGroundWeight(p), mW = MountainWeight(p);
            LastSnowBase = dnW > 0f || mW > 0f;
            if (dnW <= 0f && mW <= 0f)
            {
                float wg = WinterG(p);
                n = System.Diagnostics.Stopwatch.GetTimestamp(); SnowPerfTimers.WinterTicks += n - t; t = n;
                float r = wg > 0f ? wg * SnowGround.FactorAt(p) : 0f;
                SnowPerfTimers.GroundTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t;
                return r;
            }
            float heavy = SnowHeightmapInject.HeavyNow();
            SnowDrift.At(p, out float lee, out float windward);
            n = System.Diagnostics.Stopwatch.GetTimestamp(); SnowPerfTimers.DriftTicks += n - t; t = n;
            float g = SnowHeightmapInject.TexelG(p, heavy, lee, windward, dnW, mW);
            n = System.Diagnostics.Stopwatch.GetTimestamp(); SnowPerfTimers.TexelTicks += n - t; t = n;
            float clear = SnowPathMask.ClearAtSmooth(p);
            if (clear > 0f && SnowFat.OwnsZone(ZoneSystem.GetZone(p))) clear = 0f;
            n = System.Diagnostics.Stopwatch.GetTimestamp(); SnowPerfTimers.PathTicks += n - t; t = n;
            float shelter = SnowShelter.ShelterAt(p);
            n = System.Diagnostics.Stopwatch.GetTimestamp(); SnowPerfTimers.ShelterTicks += n - t; t = n;
            float ground = SnowGround.FactorAt(p);
            SnowPerfTimers.GroundTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t;
            return Mathf.Clamp01(g * (1f - clear) * shelter * ground);
        }

        /// <summary>0.2.1099 (lead, [worldgen]: 1-4 s stalls, the per-metre model on vanilla's far-LOD rebuild, 81 x 81 texels at 10 m): <see cref="DeepSnowG"/>
        /// for far terrain, from the world-gen biome. Same cover, build-up, season and caps; no 4 m cells, wind drift heights, dug paths, shelter or
        /// building rules, none of which a 10 m texel can show.</summary>
        internal static float DeepSnowGDistant(Vector3 p, Heightmap.Biome biome)
        {
            if (!Enabled) return 0f;
            // 0.2.1101: the caller samples the biome on a coarser grid (SnowBaseMask)
            if (biome == Heightmap.Biome.DeepNorth || biome == Heightmap.Biome.Mountain)
            {
                float g = SnowHeightmapInject.DepthGWithBase(p, SnowHeightmapInject.HeavyNow(), 0f, 0f, 1f);
                if (biome == Heightmap.Biome.DeepNorth) return Mathf.Clamp01(g);
                float max = SnowConfig.MountainMaxDepth;
                return max <= 0f ? 0f : Mathf.Clamp01(Mathf.Min(max / 2f, max / SnowHeightmapInject.DeepCapMetres * g));   // as TexelG
            }
            float winter = SnowConfig.WinterMaxDepth;
            if (WinterCover <= 0f || winter <= 0f || biome == Heightmap.Biome.AshLands || biome == Heightmap.Biome.Ocean || !SeasonBridge.IsSnowTerrainBiome(biome)) return 0f;
            return Mathf.Clamp01(WinterCover * BuildUp(ZoneSystem.GetZone(p)) * winter / MaxDepthMetres);
        }

        /// <summary>0.2.935 (Fire 10-27, [worldgen]'s round 35 proof "winter cover 1.000 ... Core depth 0.000"): the WINTER depth outside the
        /// snow biomes - the cover before melt (winter cover x the zone's build-up x the shelter) x [Snow] Winter snow depth (m), net of dug
        /// paths (not on FAT zones: FAT's voxel snow keeps its own digs). Melt stays per pixel (shaders) as for the deep snow.</summary>
        /// <summary>0.2.1052 (Fire 10-30: "paint seasonal-winter ground as snow"; Core's raised terrain is the snow off voxel chunks):
        /// true where seasonal winter snow lies on vanilla ground outside the snow biomes (winter cover, a whitening biome, depth > 0).
        /// FAT BiomePaint ORs it with <see cref="DeepGroundAt"/> to colour the vertices as snow; Core's inject raises G there and
        /// SnowGroundAsDeepNorth gives the vanilla deep-snow sink / footsteps / slow. Add-only. DeepGroundAt itself is unchanged.</summary>
        public static bool WinterGroundAt(Vector3 p) => Enabled && WinterCover > 0f && WinterG(p) > 0f;

        internal static float WinterG(Vector3 p)
        {
            if (WinterCover <= 0f) return 0f;
            float max = SnowConfig.WinterMaxDepth;
            if (max <= 0f) return 0f;
            var biome = SurfaceBiome(p);   // 0.2.944: cached per 4 m cell
            if (biome == Heightmap.Biome.Mountain || biome == Heightmap.Biome.DeepNorth || biome == Heightmap.Biome.AshLands || biome == Heightmap.Biome.Ocean) return 0f;
            float raw = RawCoverAt(p);
            if (raw <= 0f) return 0f;
            float clear = SnowPathMask.ClearAtSmooth(p);
            if (clear > 0f && SnowFat.OwnsZone(ZoneSystem.GetZone(p))) clear = 0f;
            if (!s_winterLogged) { s_winterLogged = true; FiresLogger.LogDebug($"[Snow] winter depth: up to {max:0.00} m at full winter cover outside the snow biomes (first at ({p.x:0},{p.z:0}): biome {biome}, cover {raw:0.00} -> {raw * (1f - clear) * max:0.00} m)"); }
            return Mathf.Clamp01(raw * (1f - clear) * max / MaxDepthMetres);
        }
        private static bool s_winterLogged;

        /// <summary>A zone's snow changed (season stage, weather, the 30 s world-time step, a path edit or a fire). Never per frame.</summary>
        public static event Action<Vector2s> ZoneChanged;

        /// <summary>0.2.1021 ([saddles] / [lead] 10-30, add-only): Core's snow depth changed inside a world-XZ rect of a zone (piece /
        /// location footprints rebuilt or placed / removed, a path-mask stamp). Coalesced per zone, at most once a second, so a skin can
        /// re-read just that rect instead of waiting for its verifier pass. Season / weather / drift changes stay on ZoneChanged.</summary>
        public static event Action<Vector2s, Rect> CellsChanged
        {
            // 0.2.1024 ([saddles]: a late subscriber missed the zone-load footprints): a new listener gets every zone Core has built
            // footprints for, zone-wide, on the next send
            add { s_cellsChanged += value; foreach (var z in SnowGround.BuiltZones()) NoteZoneCellsChanged(z); }
            remove { s_cellsChanged -= value; }
        }
        private static Action<Vector2s, Rect> s_cellsChanged;
        private static readonly Dictionary<Vector2s, int> s_rev = new Dictionary<Vector2s, int>();

        /// <summary>0.2.1024: bumps whenever Core's snow depth changes in part of the zone (every CellsChanged cause, listener or
        /// not); a reader compares it to the value it last drew with.</summary>
        public static int Revision(Vector2s zone) => s_rev.TryGetValue(zone, out int r) ? r : 0;

        private static readonly List<Func<Vector3, float>> s_baseProviders = new List<Func<Vector3, float>>();
        private static readonly List<Func<Vector3, float>> s_melters = new List<Func<Vector3, float>>();

        /// <summary>Adds a snow BASE source (0..1) at a point, e.g. RPGM's Winter territories or a forced snow biome. The highest
        /// source wins. The callback is guarded: one that throws counts as 0.</summary>
        public static void RegisterBaseProvider(Func<Vector3, float> provider) { if (provider != null) lock (s_baseProviders) s_baseProviders.Add(provider); }

        /// <summary>Adds a local melter (0..1 melt at a point, client-local, guarded). Burning fires are built in.</summary>
        public static void RegisterMelter(Func<Vector3, float> melter) { if (melter != null) lock (s_melters) s_melters.Add(melter); }

        /// <summary>Snow base 0..1 at a point before season/weather: 1 in Mountain / Deep North, else the highest provider.</summary>
        public static float BaseAt(Vector3 p) => Enabled ? Cell(p).x : 0f;

        /// <summary>0.2.829 ([seasons], Fire 10-26 "NOTHING like the deep snow im looking for"): the ground here is vanilla Deep North
        /// deep snow: the world-gen chain answers Deep North (real or painted / overridden) or a base provider (RPGM Winter
        /// territories) answers > 0. Real Mountain stays vanilla Mountain ground. Used by <see cref="SnowGroundAsDeepNorth"/> and the
        /// terrain inject.</summary>
        public static bool DeepGroundAt(Vector3 p) => Enabled && Cell(p).y > 0.5f;

        /// <summary>0.2.1075 ([worldgen] / lead: FAT caches DeepGroundAt's bool per cell, so a deferred false stuck as "not Deep North"):
        /// DeepGroundAt keeps 0.2.1073's behaviour exactly (a zone's 25 coarse samples in one call). TryDeepGroundAt is the resumable
        /// path: the coarse build goes one sample at a time under the snow budget, and it returns FALSE when the answer was deferred (ask
        /// again; do not cache <paramref name="deep"/>), TRUE when <paramref name="deep"/> is final.</summary>
        public static bool TryDeepGroundAt(Vector3 p, out bool deep)
        {
            deep = false;
            if (!Enabled) return true;
            t_resumable = true; t_deferred = false;
            try { deep = Cell(p).y > 0.5f; }
            finally { t_resumable = false; }
            if (t_deferred) { s_tryDeferred++; return false; }
            s_tryAnswered++;
            return true;
        }

        [ThreadStatic] private static bool t_resumable, t_deferred;
        private static int s_tryDeferred, s_tryAnswered;

        /// <summary>0.2.830: DeepGroundAt as a smooth 0..1 weight (bilinear + smoothstep over the 4 surrounding 4 m cells), for
        /// depths that fade in at a snow base's edge instead of stepping.</summary>
        public static float DeepGroundWeight(Vector3 p) => Enabled ? Smooth(p, 1) : 0f;

        /// <summary>0.2.914 (Fire 10-27): Mountain ground as a smooth 0..1 weight - its own deep-snow class (visual + trails only:
        /// DeepGroundAt, the Deep North sink / slow / biome flip, stays Deep North only).</summary>
        public static float MountainWeight(Vector3 p) => Enabled && SnowConfig.MountainMaxDepth > 0f ? Smooth(p, 2) : 0f;
        /// <summary>0.2.914: Cell.y for a Mountain cell (a deep class below DeepGroundAt's 0.5).</summary>
        internal const float MountainClass = 0.25f;

        /// <summary>0.2.830: BaseAt, smooth over the 4 surrounding 4 m cells.</summary>
        public static float BaseWeight(Vector3 p) => Enabled ? Smooth(p, 0) : 0f;

        /// <summary>0.2.830: the viewer's zone build-up (SnowTicker sets it on each snow step); every chunk uses this one value.</summary>
        public static float ViewerBuildUp { get => TestBuildUpActive ? s_testBuildUp : s_viewerBuildUp; internal set => s_viewerBuildUp = value; }
        private static float s_viewerBuildUp = BuildUpBaseline;

        // 0.2.919 (lead / [worldgen] 10-27: the Mountain snow tests need the build-up in seconds, never a cfg write): a TEST override of the
        // viewer build-up for at most 300 s, then it expires on its own (TickCount, so worker-thread reads are safe). Setting or ending it
        // re-announces the loaded snow zones (BaseEpoch), so FAT refills and vanilla chunks repaint. Used by core_mountain_snow_test and,
        // by reflection, FAT's fat_mountain_trail_test.
        private static float s_testBuildUp; private static int s_testUntil; private static bool s_testOn;
        private static bool TestBuildUpActive => s_testOn && Environment.TickCount - s_testUntil < 0;
        /// <summary>TEST ONLY: hold the snow build-up at <paramref name="value01"/> (0..1) for <paramref name="seconds"/> (max 300); seconds 0 ends it.</summary>
        public static void TestBuildUp(float value01, float seconds)
        {
            seconds = Mathf.Clamp(seconds, 0f, 300f);
            s_testBuildUp = Mathf.Clamp01(value01); s_testUntil = Environment.TickCount + (int)(seconds * 1000f); s_testOn = seconds > 0f;
            BaseEpoch++;
            FiresCore.Logging.FiresLogger.LogDebug(s_testOn ? $"[Snow] TEST build-up held at {s_testBuildUp:0.00} for {seconds:0} s (loaded snow zones re-announced)" : "[Snow] TEST build-up hold ended");
        }

        /// <summary>0.2.919: every input of the snow depth at a point, for tests and logs.</summary>
        public static string Explain(Vector3 p)
        {
            try
            {
                var c = Cell(p);
                var biome = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(p.x, p.z) : Heightmap.Biome.None;   // 0.2.927: surface biome (y 0 read FAT's cave layer)
                // 0.2.934: + the winter (Vedr) path of the cover, so a "winter cover 1, coverage 0" proves which factor is 0
                string winter = $"winter cover {WinterCover:0.00} (biome {biome} {(SeasonBridge.IsSnowTerrainBiome(biome) ? "whitens" : "does NOT whiten")} in winter), zone build-up {BuildUp(ZoneSystem.GetZone(p)):0.00}, shelter {ShelterAt(p):0.00}, coverage {CoverageNoMeltAt(p):0.00}";
                return $"({p.x:0},{p.z:0}): {winter}; biome {biome}, cell base {c.x:0.00} class {c.y:0.00}, base weight {BaseWeight(p):0.00}, Deep North weight {DeepGroundWeight(p):0.00}, Mountain weight {MountainWeight(p):0.00} (cap {SnowConfig.MountainMaxDepth:0.00} m), cover {GlobalCover:0.00}, build-up {ViewerBuildUp:0.00}{(TestBuildUpActive ? " (TEST hold)" : "")}, enabled {Enabled}, depth {DepthAt(p) * MaxDepthMetres:0.000} m";
            }
            catch (Exception e) { return $"({p.x:0},{p.z:0}): explain failed: {e.Message}"; }
        }

        private static float Smooth(Vector3 p, int mode)   // 0 = base, 1 = Deep North deep ground, 2 = Mountain (0.2.914)
        {
            float fx = p.x / BaseCell - 0.5f, fz = p.z / BaseCell - 0.5f;
            int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
            float tx = fx - x0, tz = fz - z0;
            tx = tx * tx * (3f - 2f * tx); tz = tz * tz * (3f - 2f * tz);
            float V(int x, int z) { var c = Cell(new Vector3((x + 0.5f) * BaseCell, p.y, (z + 0.5f) * BaseCell)); return mode == 0 ? c.x : mode == 1 ? (c.y > 0.5f ? 1f : 0f) : (c.y > 0f && c.y <= 0.5f ? 1f : 0f); }
            return Mathf.Lerp(Mathf.Lerp(V(x0, z0), V(x0 + 1, z0), tx), Mathf.Lerp(V(x0, z0 + 1), V(x0 + 1, z0 + 1), tx), tz);
        }

        /// <summary>0.2.829 (add-only): drops the 4 m / 10 s base cache NOW, so the next BaseAt / DeepGroundAt reads the fresh
        /// paint / territory answer. Call it when a snow base or territory changes, BEFORE FatApi.RefreshBiomeOverrides (else
        /// the re-coloured heightmaps read the old answer for up to 10 s).</summary>
        public static void InvalidateBaseCache() { lock (s_zones) { s_zones.Clear(); } lock (s_biomes) s_biomes.Clear(); BaseEpoch++; }

        // 0.2.944 (lead, Fire's session profile: "SnowField.RawCoverAt 1502/s, SnowField.WinterG 614/s" of 4169 GetBiome calls/s through
        // FAT's biome redirect, ~30 ms/s): the surface biome per 4 m cell, cached. It changes only with paint / territory, which already
        // calls InvalidateBaseCache (it clears this too). Capped: past BiomeCap cells the cache starts over (never a per-frame clear).
        private const int BiomeCap = 200000;
        private static readonly Dictionary<long, Heightmap.Biome> s_biomes = new Dictionary<long, Heightmap.Biome>();
        private static int s_biomeHits, s_biomeMisses, s_biomeSaidAt = Environment.TickCount;
        internal static Heightmap.Biome SurfaceBiome(Vector3 p)
        {
            long k = ((long)Mathf.FloorToInt(p.x / BaseCell) << 32) ^ (uint)Mathf.FloorToInt(p.z / BaseCell);
            lock (s_biomes)
            {
                if (s_biomes.TryGetValue(k, out var b)) { s_biomeHits++; return b; }
            }
            var v = WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(p.x, p.z) : Heightmap.Biome.None;
            lock (s_biomes)
            {
                if (s_biomes.Count >= BiomeCap) s_biomes.Clear();
                s_biomes[k] = v; s_biomeMisses++;
                if (Environment.TickCount - s_biomeSaidAt > 60000)
                {
                    FiresLogger.LogVerbose($"[Snow] biome cache last 60 s: {s_biomeHits} hit(s), {s_biomeMisses} GetBiome call(s), {s_biomes.Count} cell(s)");
                    s_biomeHits = s_biomeMisses = 0; s_biomeSaidAt = Environment.TickCount;
                }
            }
            return v;
        }

        /// <summary>0.2.842 ([seasons], Fire 10-26 sample: FAT "1 fill(s) (0 with snow)" next to "Core base 1.00"): bumps whenever
        /// the snow base may have changed (InvalidateBaseCache, or a zone whose coarse answer flipped on its 10 s re-check, e.g. the
        /// server's paint arriving after the first chunks were built). SnowTicker then re-announces the loaded snow zones, so FAT
        /// refills its voxel depth textures and vanilla chunks repaint; before, a zone filled while the paint was unknown kept 0.</summary>
        internal static volatile int BaseEpoch;

        // x = BaseAt, y = DeepGroundAt (1/0), per 4 m cell
        // 0.2.851 ([seasons], [worldgen] measured DeepGroundAt at 7-15 ms per call, worst 18.8 ms: the real cost behind FAT's
        // BiomeDeepCells 25-65 ms/s and Fire's spikes). A call that hit a zone's 10 s TTL rebuilt its 25-sample coarse answer, 25
        // walks of the patched GetBiome chain, in that one call. Now: zones live ZoneTtlMs (5 min; paint / territory edits already
        // clear everything through InvalidateBaseCache), and ALL lookups (coarse builds + cell fills) share a per-frame TIME budget,
        // BudgetTicks (1 ms); past it a zone with no coarse yet answers zero UNCACHED (asked again next frame) and a cell answers its
        // coarse sample. Every call is timed: "[Snow] base lookups last N s: C call(s), avg X ms, worst Y ms, H hit(s) ...".
        private static Vector2 Cell(Vector3 p)
        {
            long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { return CellInner(p); }
            finally
            {
                long dt = System.Diagnostics.Stopwatch.GetTimestamp() - c0;
                s_calls++; s_callTicks += dt; if (dt > s_callWorst) s_callWorst = dt;
            }
        }

        private static Vector2 CellInner(Vector3 p)
        {
            // 0.2.834 ([seasons], Fire 10-26 1.9-2.7 s stalls, PROVEN in his live log: ~9 "[Snow] base cache: 40001 cell(s) ... 40001
            // miss(es) last 0 s" lines in each stall frame). 0.2.826-833 cleared the WHOLE cache at 40k cells, so a mass clutter regrow
            // (arrival / teleport) evaluated ~360k distinct 4 m cells in one frame, each the full patched GetBiome chain + providers.
            // Now: per-ZONE buckets (64 m = 16 x 16 cells). A zone first gets a COARSE answer from 5 x 5 samples (16 m apart); a zone
            // with no snow base at any sample answers zero for every cell without a lookup (most of a world). Cells of a snowy zone
            // fill lazily, at most MissBudget per frame; past it the zone's nearest coarse sample answers (not cached), so no frame
            // can do more than ~MissBudget lookups. A zone re-checks after ZoneTtlMs (paint / territory changes); zones idle for
            // ZoneIdleMs are dropped when over ZoneCap. Never a clear-all.
            int zx = Mathf.FloorToInt(p.x / ZoneSize), zz = Mathf.FloorToInt(p.z / ZoneSize);
            long zk = ((long)zx << 32) ^ (uint)zz;
            int now = Environment.TickCount, frame = MainFrame;   // thread-safe: no Unity Time API here (callers may be workers)
            ZoneCells z;
            lock (s_zones)
            {
                if (frame != s_frame) { s_frame = frame; s_frameMisses = 0; }
                if (!s_zones.TryGetValue(zk, out var old) || now - old.Built > ZoneTtlMs)
                {
                    z = new ZoneCells { Built = now, PrevAny = old == null || old.Coarse == null ? -1 : (old.AnySnow ? 1 : 0) };
                    s_zones[zk] = z; if (s_zones.Count > ZoneCap) Evict(now);
                }
                else z = old;
                z.Touched = now;
            }
            if (z.Coarse == null)
            {
                if (!SpendBudget()) { s_deferred++; t_deferred = true; Summary(now); return Vector2.zero; }   // uncached: asked again next frame
                BuildCoarse(z, zx, zz);
                // 0.2.1074 ([worldgen], dedi 10-31: one DeepGroundAt = 84-92 ms, the 25 coarse samples in one call): the coarse build now
                // resumes sample by sample under the same budget; until all 25 are in, the answer is the existing deferred one
                if (z.Coarse == null) { s_deferred++; t_deferred = true; Summary(now); return Vector2.zero; }
            }
            if (!z.AnySnow) { s_coarseRejects++; Summary(now); return Vector2.zero; }
            int cx = Mathf.Clamp(Mathf.FloorToInt((p.x - zx * ZoneSize) / BaseCell), 0, CellsPerZone - 1);
            int cz = Mathf.Clamp(Mathf.FloorToInt((p.z - zz * ZoneSize) / BaseCell), 0, CellsPerZone - 1);
            int i = cz * CellsPerZone + cx;
            if (z.Filled[i]) { s_hits++; return z.Cells[i]; }
            lock (s_zones)
            {
                if (!SpendBudget()) { s_deferred++; t_deferred = true; Summary(now); return z.Coarse[Mathf.Clamp(cz / 4, 0, 4) * 5 + Mathf.Clamp(cx / 4, 0, 4)]; }
                s_frameMisses++;
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var v = Evaluate(new Vector3(zx * ZoneSize + (cx + 0.5f) * BaseCell, p.y, zz * ZoneSize + (cz + 0.5f) * BaseCell));
            z.Cells[i] = v; z.Filled[i] = true;
            long spent = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            Spent(spent);
            s_misses++; s_missMs += (float)(spent * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            Summary(now);
            return v;
        }

        private static Vector2 Evaluate(Vector3 c)
        {
            var biome = WorldGenerator.instance.GetBiome(c.x, c.z);   // 0.2.927 ([worldgen] round 34): the surface biome; GetBiome(Vector3) at y 0 read FAT's cave layer
            float prov = ProvidersAt(c);
            return new Vector2(biome == Heightmap.Biome.Mountain || biome == Heightmap.Biome.DeepNorth ? 1f : prov,
                               biome == Heightmap.Biome.DeepNorth || prov > 0f ? 1f : biome == Heightmap.Biome.Mountain ? MountainClass : 0f);   // 0.2.914: Mountain deep class
        }

        /// <summary>0.2.1054 ([saddles] / lead: FAT BiomePaint queued every 4 m cell of every heightmap, 303k, nearly all "no"): false only when
        /// NO cell of this Valheim zone (+ a 4 m margin) can answer <see cref="DeepGroundAt"/> or <see cref="WinterGroundAt"/> true - read from
        /// the base cache's coarse answers (a bucket whose 5 x 5 coarse samples hold no snow base answers zero for every cell, the same rule
        /// DeepGroundAt uses). Unknown (not built yet) or seasonal winter on = true. Cheap: no lookup, no budget. Add-only.</summary>
        public static bool ZoneMayHaveDeepGround(Vector2s zone)
        {
            if (!Enabled) return false;
            if (WinterCover > 0f) return true;   // winter ground can be anywhere a whitening biome is
            return ZoneMayHaveSnowBase(zone);
        }

        /// <summary>0.2.1131 ([worldgen], add-only): <see cref="ZoneMayHaveDeepGround"/> without the winter line: false only when no cell of this zone can answer <see cref="DeepGroundAt"/> / <see cref="TryDeepGroundAt"/> true.
        /// Those answers come from biome + base providers, not the season. Never use it to gate <see cref="WinterGroundAt"/>, which does follow the season.</summary>
        public static bool ZoneMayHaveSnowBase(Vector2s zone)
        {
            if (!Enabled) return false;
            Vector3 c = ZoneSystem.GetZonePos(zone);
            float half = ZoneSystem.c_ZoneSizeHalf + BaseCell;
            int x0 = Mathf.FloorToInt((c.x - half) / ZoneSize), x1 = Mathf.FloorToInt((c.x + half) / ZoneSize);
            int z0 = Mathf.FloorToInt((c.z - half) / ZoneSize), z1 = Mathf.FloorToInt((c.z + half) / ZoneSize);
            lock (s_zones)
            {
                for (int zx = x0; zx <= x1; zx++)
                    for (int zz = z0; zz <= z1; zz++)
                    {
                        if (!s_zones.TryGetValue(((long)zx << 32) ^ (uint)zz, out var b) || b.Coarse == null) return true;   // not known yet
                        if (b.AnySnow) return true;
                    }
            }
            return false;
        }

        private static bool s_reentrySaid;
        private static void BuildCoarse(ZoneCells z, int zx, int zz)
        {
            Vector2[] coarse;
            lock (z)
            {
                if (z.Coarse != null) return;
                if (z.Partial == null) z.Partial = new Vector2[25];
                // one sample at a time, the budget checked BEFORE each (charged as spent); a zone fill (s_fillNoBudget) still runs all 25
                while (z.PartialNext < 25)
                {
                    if (t_resumable && z.PartialNext > 0 && !SpendBudget()) return;   // 0.2.1075: only TryDeepGroundAt resumes; every other caller builds all 25 now (0.2.1073)
                    long b0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    int i = z.PartialNext, j = i / 5, k = i % 5;
                    var sample = Evaluate(new Vector3(zx * ZoneSize + k * (ZoneSize / 4f), 0f, zz * ZoneSize + j * (ZoneSize / 4f)));
                    // 0.2.1084 (dedi round 040308, 2x NullReferenceException here via SnowTicker.LoadedSnowZones -> ZoneHasSnow): Evaluate can
                    // ask the SAME zone again on this thread (the lock is re-entrant); that nested call advanced or finished this build (and
                    // cleared Partial). Its state stands - this call stops without writing.
                    if (z.Coarse != null || z.Partial == null || z.PartialNext != i)
                    {
                        if (!s_reentrySaid) { s_reentrySaid = true; FiresLogger.LogDebug($"[Snow] base cache: coarse build of zone ({zx},{zz}) re-entered from its own sample; the nested build's result is kept (0.2.1084)"); }
                        return;
                    }
                    z.Partial[i] = sample;
                    z.PartialNext++;
                    long dt = System.Diagnostics.Stopwatch.GetTimestamp() - b0;
                    Spent(dt);
                    if (dt > s_coarseWorstTicks) s_coarseWorstTicks = dt;
                }
                coarse = z.Partial; z.Partial = null;
            }
            bool any = false;
            for (int i = 0; i < 25; i++) if (coarse[i].x > 0f || coarse[i].y > 0f) { any = true; break; }
            s_coarseBuilt++;
            if (z.PrevAny >= 0 && (z.PrevAny == 1) != any) BaseEpoch++;   // 0.2.842: this zone's snow base changed (paint / territory)
            z.AnySnow = any;
            z.Coarse = coarse;   // published last: another thread sees either null (and builds its own) or the full array
        }

        private static void Evict(int now)
        {
            List<long> drop = null;
            foreach (var kv in s_zones) if (now - kv.Value.Touched > ZoneIdleMs) { if (drop == null) drop = new List<long>(); drop.Add(kv.Key); }
            if (drop != null) foreach (var k in drop) s_zones.Remove(k);
            s_evicted += drop?.Count ?? 0;
        }

        private static void Summary(int now)
        {
            if (now - s_summaryAt < 60000) return;   // 0.2.874 ([wishbone], soak #1 log census: 7059 lines/h, top Core tag): 60 s windows, was 1 s
            int span = now - s_summaryAt; s_summaryAt = now;
            if (s_calls > 0)
            {
                double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                FiresLogger.LogVerbose($"[Snow] base lookups last {span / 1000f:0.0} s (DeepGroundAt / BaseAt / weights): {s_calls} call(s), avg {s_callTicks * f / s_calls:0.0000} ms, worst {s_callWorst * f:0.00} ms, {s_hits} cell hit(s), {s_coarseRejects} no-snow-zone answer(s), {s_deferred} deferred by the {BudgetMs} ms/frame budget");
                s_calls = 0; s_callTicks = s_callWorst = 0;
            }
            if (s_misses == 0 && s_deferred == 0 && s_coarseBuilt == 0) { s_hits = s_coarseRejects = 0; return; }
            int zones; lock (s_zones) zones = s_zones.Count;
            FiresLogger.LogVerbose($"[Snow] base cache last {span / 1000f:0.0} s: {zones} zone(s), {s_coarseBuilt} coarse built, TryDeepGroundAt {s_tryAnswered} answered / {s_tryDeferred} deferred (worst sample {s_coarseWorstTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.00} ms), {s_coarseRejects} no-snow-zone answer(s), {s_hits} hit(s), {s_misses} cell lookup(s) {s_missMs:0.0} ms, {s_deferred} deferred (budget {MissBudget}/frame), {s_evicted} zone(s) evicted");
            s_hits = s_misses = s_deferred = s_coarseRejects = s_coarseBuilt = s_evicted = 0; s_missMs = 0f; s_coarseWorstTicks = 0; s_tryAnswered = s_tryDeferred = 0;
        }

        private sealed class ZoneCells
        {
            public int Built, Touched, PrevAny = -1; public bool AnySnow; public Vector2[] Coarse;
            public Vector2[] Partial; public int PartialNext;   // 0.2.1074: a coarse build in progress
            public readonly Vector2[] Cells = new Vector2[CellsPerZone * CellsPerZone];
            public readonly bool[] Filled = new bool[CellsPerZone * CellsPerZone];
        }

        private const float BaseCell = 4f, ZoneSize = 64f;
        private const int CellsPerZone = 16, ZoneTtlMs = 300000, ZoneIdleMs = 30000, ZoneCap = 1500, MissBudget = 1500;
        private const double BudgetMs = 1.0;
        private static readonly long BudgetTicks = (long)(BudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);
        private static long s_frameTicks; private static int s_budgetFrame = -1;
        private static int s_calls; private static long s_callTicks, s_callWorst;
        // per-frame lookup budget (MainFrame is stamped by SnowTicker on the main thread)
        private static bool SpendBudget()
        {
            lock (s_zones)
            {
                if (MainFrame != s_budgetFrame) { s_budgetFrame = MainFrame; s_frameTicks = 0; }
                return s_fillNoBudget || s_frameTicks < BudgetTicks;   // 0.2.860: a zone fill is never cut short
            }
        }
        private static long s_coarseWorstTicks;   // 0.2.1074: the slowest single coarse sample (Debug summary)
        private static void Spent(long ticks) { lock (s_zones) s_frameTicks += ticks; }
        private static readonly Dictionary<long, ZoneCells> s_zones = new Dictionary<long, ZoneCells>();
        private static int s_frame = -1, s_frameMisses, s_summaryAt = Environment.TickCount;
        private static int s_hits, s_misses, s_deferred, s_coarseRejects, s_coarseBuilt, s_evicted;
        private static float s_missMs;
        private static int s_fillLogs, s_fillLogAt;
        /// <summary>The main thread's frame number, stamped by SnowTicker.Update (the per-frame miss budget's clock).</summary>
        internal static volatile int MainFrame;

        private static float ProvidersAt(Vector3 p)
        {
            float b = 0f;
            lock (s_baseProviders)
                for (int i = 0; i < s_baseProviders.Count; i++)
                {
                    try { b = Mathf.Max(b, Mathf.Clamp01(s_baseProviders[i](p))); } catch { }
                }
            return b;
        }

        /// <summary>Local melt 0..1 at a point (burning fires within the fixed radii, plus registered melters).</summary>
        public static float MeltAt(Vector3 p)
        {
            float m = SnowFires.MeltAt(p);
            if (m >= 1f) return 1f;
            lock (s_melters)
                for (int i = 0; i < s_melters.Count; i++)
                {
                    try { m = Mathf.Max(m, Mathf.Clamp01(s_melters[i](p))); } catch { }
                }
            return m;
        }

        /// <summary>Snow coverage 0..1 at a point before fire melt: base × cover × build-up, minus the path clear mask.</summary>
        public static float CoverageNoMeltAt(Vector3 p)
        {
            // FTS 0.3.204 melts per pixel in the shader (soft circles + a wet band), so pieces want the cover before fire melt;
            // dug paths still clear it.
            if (!Enabled) return 0f;
            float raw = RawCoverAt(p);
            return raw <= 0f ? 0f : Mathf.Clamp01(raw * (1f - SnowPathMask.ClearAt(p)));
        }

        /// <summary>Snow coverage 0..1 at a point including fire melt (see <see cref="CoverageNoMeltAt"/> for the cover before melt).</summary>
        public static float CoverageAt(Vector3 p)
        {
            if (!Enabled) return 0f;
            float raw = RawCoverAt(p);
            if (raw <= 0f) return 0f;
            return Mathf.Clamp01(raw * (1f - MeltAt(p)) * (1f - SnowPathMask.ClearAt(p)) * SnowGround.FactorAt(p));   // 0.2.1003: paved + pieces
        }

        /// <summary>Cover before melt and paths: a snow base × the year-round cover, else Vedr's winter cover in the biomes it
        /// whitens in winter, else 0 - then × the zone's build-up (<see cref="BuildUp"/>).</summary>
        private static float RawCoverAt(Vector3 p)
        {
            float c;
            float b = BaseAt(p);
            if (b > 0f) c = b * GlobalCover;
            else if (WinterCover <= 0f) return 0f;
            else c = SeasonBridge.IsSnowTerrainBiome(SurfaceBiome(p)) ? WinterCover : 0f;   // 0.2.944: cached per 4 m cell
            return c <= 0f ? 0f : c * BuildUp(ZoneSystem.GetZone(p)) * SnowShelter.ShelterAt(p);   // 0.2.927: the shelter (CoverageAt, FTS tint)
        }

        // ── build-up (Fire 10-24: "snow must visibly build up over in-game time") ──────────────────────────────────────────────
        // Each zone holds a build-up 0..1 that starts at BuildUpBaseline when first seen and grows on the snow step while it snows
        // (a storm fastest), holds in clear weather, and settles back toward the baseline in summer. Cover / depth are scaled by it,
        // so a player watches the snow deepen through a storm. It's local per peer (a zone first seen at different times can
        // differ a little until it fills); cosmetic only.

        /// <summary>A zone's build-up when first seen.</summary>
        public const float BuildUpBaseline = 0.35f;
        // 0.2.998 (Fire 10-29 round R, Deep North: "each zone is building up at a different rate and the zone borders arent matching";
        // [saddles] proof: RPGWorld_buildup.txt 1,135 = 1.000 vs 1,134 = 0.659, a snow step along z 8608. Fire chose: "One world-wide
        // build-up"): the build-up was one value PER ZONE, grown only while that zone was loaded, so neighbours drifted apart and met
        // in a step. Now there is ONE build-up for the whole world, advanced by weather + time, the same for every zone loaded or not;
        // every zone's cover, depth and baked snow read it (the per-zone API is kept and answers the world value).
        private static float s_world = BuildUpBaseline;

        /// <summary>The world's snow build-up 0..1 (scales cover and depth everywhere).</summary>
        public static float WorldBuildUp => s_world;
        /// <summary>A zone's snow build-up 0..1: since 0.2.998 the world build-up, the same for every zone.</summary>
        public static float BuildUp(Vector2s zone) => s_world;

        /// <summary>0.2.998: advances the world build-up by delta (clamped to [baseline floor, 1]); returns the old value.</summary>
        internal static float AdvanceWorldBuildUp(float delta, float floor)
        {
            float a = s_world;
            float b = Mathf.Clamp(a + delta, Mathf.Min(floor, a), 1f);
            if (b != a) s_buildDirty = true;
            s_world = b;
            return a;
        }

        internal static void ForgetBuildUp(Vector2s zone) { }   // 0.2.998: nothing per zone any more

        // 0.2.853 (Fire 10-27 "grow in snowfall"): build-up saved per zone so it survives a relog. Local file per world under
        // BepInEx\cache (one "x,y,value" line per zone above the baseline); nothing goes over the network.
        private static bool s_buildDirty;
        private static string BuildUpPath()
        {
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
            if (string.IsNullOrEmpty(world)) return null;
            foreach (char ch in System.IO.Path.GetInvalidFileNameChars()) world = world.Replace(ch, '_');
            return System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "cache", "FiresUnifiedCore", "Snow", world + "_buildup.txt");
        }

        // 0.2.1157 (Fire: "fall look until the first snowstorm"): seasonal winter cover waits for the first snowfall of this winter. Saved with the build-up, so a relog keeps it; a save from before this counts as fallen, so a world mid-winter keeps its snow.
        internal static bool WinterSnowFell;
        internal static void SetWinterSnowFell(bool fell) { if (WinterSnowFell == fell) return; WinterSnowFell = fell; s_buildDirty = true; FiresLogger.LogDebug($"[Snow] winter's first snowfall: {(fell ? "fallen, cover may grow" : "reset for next winter")}"); }

        internal static void LoadBuildUp()
        {
            string path = BuildUpPath();
            // 0.2.998: the file holds one "world,<value>" line. An older per-zone file ("x,y,value" lines) is migrated to its MAX: the
            // deepest snow anyone saw stays (no zone loses snow; the shallower ones fill up to it), and is rewritten as the world line.
            int n = 0; float max = -1f; bool world = false;
            try
            {
                if (path != null && System.IO.File.Exists(path)) WinterSnowFell = true;
                if (path != null && System.IO.File.Exists(path))
                    foreach (var line in System.IO.File.ReadAllLines(path))
                    {
                        var s = line.Split(',');
                        if (s.Length == 2 && s[0] == "winterSnowed") { WinterSnowFell = s[1] == "1"; continue; }
                        if (s.Length == 2 && s[0] == "world" && float.TryParse(s[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float w))
                        { s_world = Mathf.Clamp(w, BuildUpBaseline, 1f); world = true; }
                        else if (s.Length == 3 && float.TryParse(s[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v))
                        { n++; max = Mathf.Max(max, v); }
                    }
            }
            catch (Exception e) { FiresLogger.LogDebug($"[Snow] ground accumulation: saved build-up not read ({e.Message})"); }
            if (!world && n > 0)
            {
                s_world = Mathf.Clamp(max, BuildUpBaseline, 1f); s_buildDirty = true;
                FiresLogger.LogInfo($"[Snow] snow build-up is now one value for the whole world: {s_world:0.000} (the deepest of {n} saved zone value(s))");
            }
            FiresLogger.LogDebug($"[Snow] ground accumulation: world build-up {s_world:0.000} ({(world ? "saved" : n > 0 ? $"migrated from {n} zone(s), max" : "baseline, nothing saved")}) from {path ?? "(no world)"}");
        }

        // 0.2.962 (lead 10-28, fly53: the 120 s save sat next to a 181 ms frame): the text is built on the main thread and the file is
        // written on a worker; on quit (sync) it is written right away so nothing is lost. One write at a time; a busy worker skips the
        // turn and keeps the data dirty. The worker's write time is reported on the next save's line (no logging off the main thread).
        private static readonly object s_buildWriteLock = new object();
        private static volatile bool s_buildWriting;
        private static double s_lastWriteMs = -1;
        private static string s_lastWriteError;

        internal static void SaveBuildUp() => SaveBuildUp(false);

        internal static void SaveBuildUp(bool sync)
        {
            string path = BuildUpPath();
            if (path == null || !s_buildDirty) return;
            if (!sync && s_buildWriting) { FiresLogger.LogDebug("[Snow] ground accumulation: previous write still running; saving next turn"); return; }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string text; int n = 0;
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("winterSnowed,").Append(WinterSnowFell ? "1" : "0").Append('\n'); sb.Append("world,").Append(s_world.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append('\n'); n = 1;   // 0.2.998
                text = sb.ToString();
            }
            catch (Exception e) { FiresLogger.LogDebug($"[Snow] ground accumulation: build-up not saved ({e.Message})"); return; }
            s_buildDirty = false;
            double buildMs = sw.Elapsed.TotalMilliseconds;
            string prev = s_lastWriteError != null ? $"; previous write FAILED ({s_lastWriteError})" : s_lastWriteMs >= 0 ? $"; previous write {s_lastWriteMs:0.0} ms on a worker" : "";
            s_lastWriteError = null;
            if (sync)
            {
                double ms = WriteBuildUp(path, text);
                FiresLogger.LogDebug($"[Snow] ground accumulation: build-up of {n} zone(s) saved now (quit): text {buildMs:0.0} ms + write {ms:0.0} ms on the main thread{prev}");
                return;
            }
            s_buildWriting = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { s_lastWriteMs = WriteBuildUp(path, text); } finally { s_buildWriting = false; } });
            FiresLogger.LogDebug($"[Snow] ground accumulation: build-up of {n} zone(s) saved: text {buildMs:0.0} ms on the main thread, file written on a worker{prev}");
        }

        // returns the write time in ms (-1 on failure, with the reason kept for the next save's line)
        private static double WriteBuildUp(string path, string text)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                lock (s_buildWriteLock)
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    System.IO.File.WriteAllText(path, text);
                }
                return sw.Elapsed.TotalMilliseconds;
            }
            catch (Exception e) { s_lastWriteError = e.Message; s_buildDirty = true; return -1; }
        }

        /// <summary>Snow depth 0..1 at a point (the same as coverage for now: depth grows with cover; shaders scale by DepthScale).</summary>
        /// <summary>0.2.927 (add-only): the shelter factor 0..1 at a point (1 = open sky, 0 = a closed room); 1 while [Snow] No snow in closed rooms is off.</summary>
        public static float ShelterAt(Vector3 p) => SnowShelter.ShelterAt(p);

        public static float DepthAt(Vector3 p) => DeepSnowG(p);   // 0.2.840: the deep-snow model on the 2 m scale (FAT voxel sink / slow reads this x MaxDepthMetres)

        // ---- 0.2.1083 ([worldgen]: ONE season-thaw read for Core's trail melt-out and FAT's ice; add-only) ----

        /// <summary>The share of spring by which the thaw is done (trails back to grass, ice open), as FAT 1323's IceField used.</summary>
        public const float SpringOpenFrac = 0.35f;

        /// <summary>Vedr's season (0 Spring / 1 Summer / 2 Fall / 3 Winter), -1 = no Vedr calendar (VedrApi ApiVersion &lt; 8 or absent).</summary>
        public static int SeasonIndex() => VedrSeason(out _, out _);

        /// <summary>0 at the start of spring rising to 1 at SpringOpenFrac x the season length; 1 in Summer / Fall; 0 in Winter;
        /// -1 = no Vedr calendar.</summary>
        public static float ThawProgress()
        {
            int idx = VedrSeason(out int days, out float elapsed);
            if (idx < 0) return -1f;
            if (idx == 1 || idx == 2) return 1f;
            if (idx == 3) return 0f;
            return Mathf.Clamp01(elapsed / Mathf.Max(0.01f, SpringOpenFrac * days));
        }

        /// <summary>Vedr's season index, its length in days (fallback 10) and the days elapsed in it.</summary>
        internal static int VedrSeason(out int days, out float elapsedDays)
        {
            days = 10; elapsedDays = 0f;
            if (!s_seasonBound)
            {
                s_seasonBound = true;
                try
                {
                    var api = Type.GetType("Vedr.VedrApi, Norger.Vedr");
                    var ver = api?.GetField("ApiVersion")?.GetValue(null) ?? api?.GetProperty("ApiVersion")?.GetValue(null);
                    if (ver != null && Convert.ToInt32(ver) >= 8)
                    {
                        s_pIdx = api.GetProperty("SeasonIndex"); s_pDays = api.GetProperty("DaysPerSeason"); s_pProg = api.GetProperty("SeasonProgressSmooth");
                    }
                    FiresLogger.LogDebug($"[Snow] season read: Vedr {(s_pIdx != null ? "bound (spring thaw by " + SpringOpenFrac + " x season length, summer / fall at once)" : "not found (no snow cover = at once)")} (0.2.1083 SnowField.ThawProgress)");
                }
                catch (Exception e) { FiresLogger.LogDebug($"[Snow] season read: Vedr bind failed: {e.Message}"); }
            }
            int idx = -1;
            try
            {
                if (s_pIdx != null) idx = Convert.ToInt32(s_pIdx.GetValue(null));
                int d = s_pDays != null ? Convert.ToInt32(s_pDays.GetValue(null)) : 0;
                if (d > 0) days = d;
                if (s_pProg != null) elapsedDays = Mathf.Clamp01(Convert.ToSingle(s_pProg.GetValue(null))) * days;
            }
            catch { idx = -1; }
            if (idx < -1 || idx > 3) idx = -1;
            return idx;
        }
        private static System.Reflection.PropertyInfo s_pIdx, s_pDays, s_pProg;
        private static bool s_seasonBound;

        /// <summary>Fills a size×size R8 buffer with the zone's depth (0..255) at the paint mask's UV layout (row = z, column = x,
        /// covering the 64 m zone corner to corner). Returns false (buffer untouched) when the zone has no snow.</summary>
        public static bool FillZoneDepth(Vector2s zone, byte[] r8, int size)
        {
            if (!Enabled || r8 == null || size < 2 || r8.Length < size * size) return false;
            Vector3 c = ZoneSystem.GetZonePos(zone);
            float half = ZoneSystem.c_ZoneSizeHalf;
            if (!ZoneHasSnow(c))
            {
                if (s_fillLogs < 40 || Environment.TickCount - s_fillLogAt > 5000) { s_fillLogs++; s_fillLogAt = Environment.TickCount; FiresLogger.LogVerbose($"[Snow] voxel zone fill ({zone.x},{zone.y}): no snow (ZoneHasSnow false: cover {GlobalCover:0.00}, centre base {BaseAt(c):0.00})"); }
                return false;
            }
            float step = (half * 2f) / (size - 1);
            bool any = false;
            // 0.2.860 ([seasons], Fire 10-27 shot 555: a straight step in the raised snow along a ZONE border). Two per-zone breaks:
            //  1. the slope limit below ran inside this zone only, so a low spot near the edge lowered this zone's edge texels and not
            //     the neighbour's (which shares the edge points): it now runs on a grid padded FillMargin texels into the neighbours
            //     and the zone is cropped from it, so both zones limit the same world field;
            //  2. cell lookups past the 1 ms/frame budget answered 0 mid-fill (part of a zone filled shallow): a fill is a bounded
            //     one-off job and now ignores that budget.
            int n = size + 2 * FillMargin;
            var g = new float[n * n];
            s_fillNoBudget = true;
            try
            {
                for (int z = 0; z < n; z++)
                    for (int x = 0; x < n; x++)
                        g[z * n + x] = DeepSnowG(new Vector3(c.x - half + (x - FillMargin) * step, 0f, c.z - half + (z - FillMargin) * step));   // 0.2.840: 2 m scale
            }
            finally { s_fillNoBudget = false; }
            // 0.2.841 ([seasons], Fire 10-26 "building terrain and rock up around my hut"): this texture RAISES the voxel surface (FAT
            // shader vertex offset, render only). Next to a levelled / path-cleared spot or in a wall's lee the depth stepped 0 -> 1.9 m
            // within a texel = a cliff the voxel shader paints as rock. Slope limit: the snow never rises faster than MaxRiseGPerM per
            // metre (0.25 G = 0.5 m per m, ~27 degrees), lowering peaks into ramps (never raising anything), 4 passes each way.
            float maxStep = MaxRiseGPerM * step;
            for (int pass = 0; pass < 4; pass++)
            {
                for (int z = 0; z < n; z++)
                    for (int x = 0; x < n; x++)
                    {
                        int i = z * n + x; float v = g[i];
                        if (x > 0) v = Mathf.Min(v, g[i - 1] + maxStep);
                        if (z > 0) v = Mathf.Min(v, g[i - n] + maxStep);
                        g[i] = v;
                    }
                for (int z = n - 1; z >= 0; z--)
                    for (int x = n - 1; x >= 0; x--)
                    {
                        int i = z * n + x; float v = g[i];
                        if (x < n - 1) v = Mathf.Min(v, g[i + 1] + maxStep);
                        if (z < n - 1) v = Mathf.Min(v, g[i + n] + maxStep);
                        g[i] = v;
                    }
            }
            int with = 0; float maxG = 0f;
            var edges = new float[4, size];   // 0 = x min, 1 = x max, 2 = z min, 3 = z max
            for (int z = 0; z < size; z++)
                for (int x = 0; x < size; x++)
                {
                    float gv = g[(z + FillMargin) * n + x + FillMargin];
                    byte v = (byte)Mathf.RoundToInt(Mathf.Clamp01(gv) * 255f);
                    r8[z * size + x] = v;
                    if (v != 0) { any = true; with++; if (gv > maxG) maxG = gv; }
                    if (x == 0) edges[0, z] = gv; if (x == size - 1) edges[1, z] = gv;
                    if (z == 0) edges[2, x] = gv; if (z == size - 1) edges[3, x] = gv;
                }
            float seam = EdgeSeam(zone, edges, size);
            // 0.2.842: one line per zone fill (throttled), so a "0 with snow" proves itself
            if (s_fillLogs < 40 || Environment.TickCount - s_fillLogAt > 5000)
            {
                s_fillLogs++; s_fillLogAt = Environment.TickCount;
                FiresLogger.LogVerbose($"[Snow] voxel zone fill ({zone.x},{zone.y}): {with}/{size * size} texel(s) with snow, max depth {maxG * MaxDepthMetres:0.00} m (cover {GlobalCover:0.00}, build-up {ViewerBuildUp:0.00}, centre base {BaseAt(c):0.00} deep {DeepGroundWeight(c):0.00}); max depth delta across its zone edges vs the neighbours' last fills {(seam >= 0f ? $"{seam * MaxDepthMetres:0.000} m" : "n/a (no neighbour filled yet)")}");
            }
            return any;
        }

        // 0.2.860: FillMargin texels of the neighbours go into the slope limit; the edge rows of each zone's last fill prove the seam
        internal const int FillMargin = 6;
        [ThreadStatic] private static bool s_fillNoBudget;

        /// <summary>0.2.934: runs a bounded one-off read (tests) without the 1 ms/frame cell budget, so no lookup answers the deferred 0.</summary>
        internal static T Unbudgeted<T>(Func<T> read)
        {
            bool was = s_fillNoBudget; s_fillNoBudget = true;
            try { return read(); } finally { s_fillNoBudget = was; }
        }
        private static readonly Dictionary<Vector2s, float[,]> s_lastEdges = new Dictionary<Vector2s, float[,]>();

        /// <summary>Largest |depth G| difference along the shared edges with already-filled neighbours (-1 = none filled).</summary>
        private static float EdgeSeam(Vector2s zone, float[,] edges, int size)
        {
            float worst = -1f;
            lock (s_lastEdges)
            {
                void Cmp(int dx, int dy, int mine, int theirs)
                {
                    if (!s_lastEdges.TryGetValue(new Vector2s(zone.x + dx, zone.y + dy), out var o) || o.GetLength(1) != size) return;
                    for (int i = 0; i < size; i++) worst = Mathf.Max(worst, Mathf.Abs(edges[mine, i] - o[theirs, i]));
                }
                Cmp(-1, 0, 0, 1); Cmp(1, 0, 1, 0); Cmp(0, -1, 2, 3); Cmp(0, 1, 3, 2);
                if (s_lastEdges.Count > 4000) s_lastEdges.Clear();
                s_lastEdges[zone] = edges;
            }
            return worst;
        }

        /// <summary>Cheap test: could this zone (centre) hold snow at all (any corner in a snow base)?</summary>
        internal static bool ZoneHasSnow(Vector3 centre)
        {
            if (GlobalCover <= 0f && WinterCover <= 0f) return false;
            float h = ZoneSystem.c_ZoneSizeHalf;
            return RawCoverAt(centre) > 0f || RawCoverAt(centre + new Vector3(-h, 0, -h)) > 0f || RawCoverAt(centre + new Vector3(h, 0, -h)) > 0f
                || RawCoverAt(centre + new Vector3(-h, 0, h)) > 0f || RawCoverAt(centre + new Vector3(h, 0, h)) > 0f;
        }

        /// <summary>0.2.1074 ([visual], EBM re-split every group of 4 whole zones per piece change): raised just BEFORE every ZoneChanged,
        /// same zone, same frame, with the world XZ rect (x = min X, y = min Z) of what changed, clipped to the zone; wholeZone = true (the
        /// full zone rect) for every cause that is not a local edit. Today only pieces placed / removed send a local rect. Main thread.</summary>
        public static event Action<Vector2s, Rect, bool> ZoneAreaChanged;

        internal static void RaiseZoneChanged(Vector2s zone) => RaiseZoneChanged(zone, null);

        internal static void RaiseZoneChanged(Vector2s zone, Rect? local)
        {
            var ha = ZoneAreaChanged;
            if (ha != null)
            {
                Vector3 c = ZoneSystem.GetZonePos(zone); float hz = ZoneSystem.c_ZoneSizeHalf;
                Rect full = Rect.MinMaxRect(c.x - hz, c.z - hz, c.x + hz, c.z + hz);
                bool whole = local == null;
                Rect area = full;
                if (!whole)
                {
                    var r = local.Value;
                    area = Rect.MinMaxRect(Mathf.Max(r.xMin, full.xMin), Mathf.Max(r.yMin, full.yMin), Mathf.Min(r.xMax, full.xMax), Mathf.Min(r.yMax, full.yMax));
                    if (area.width <= 0f || area.height <= 0f) { area = full; whole = true; }
                }
                foreach (Action<Vector2s, Rect, bool> d in ha.GetInvocationList())
                {
                    try { d(zone, area, whole); } catch (Exception e) { FiresLogger.LogDebug($"[Snow] a ZoneAreaChanged listener threw: {e.Message}"); }
                }
            }
            var h = ZoneChanged;
            if (h == null) return;
            foreach (Action<Vector2s> d in h.GetInvocationList())
            {
                long lt0 = System.Diagnostics.Stopwatch.GetTimestamp(); try { d(zone); } catch (Exception e) { FiresLogger.LogDebug($"[Snow] a ZoneChanged listener threw: {e.Message}"); } SnowPerfTimers.Listener(d, lt0);
            }
        }

        // 0.2.1021: CellsChanged coalescing - rects grow per zone and go out from FlushCells (SnowTicker, every frame) at most once a second
        private static readonly Dictionary<Vector2s, Rect> s_cells = new Dictionary<Vector2s, Rect>();
        private static float s_cellsAt; private static bool s_cellsLogged;

        /// <summary>Queue a changed world-XZ rect; it is clipped to the zone, unioned with what the zone already has queued.</summary>
        internal static void NoteCellsChanged(Vector2s zone, Rect r)
        {
            s_rev[zone] = Revision(zone) + 1;   // 0.2.1024: counted with or without a listener
            if (s_cellsChanged == null) return;
            Vector3 c = ZoneSystem.GetZonePos(zone); float h = ZoneSystem.c_ZoneSizeHalf;
            float x0 = Mathf.Max(r.xMin, c.x - h), z0 = Mathf.Max(r.yMin, c.z - h), x1 = Mathf.Min(r.xMax, c.x + h), z1 = Mathf.Min(r.yMax, c.z + h);
            if (x1 < x0 || z1 < z0) return;
            var clip = Rect.MinMaxRect(x0, z0, x1, z1);
            s_cells[zone] = s_cells.TryGetValue(zone, out var had) ? Rect.MinMaxRect(Mathf.Min(had.xMin, clip.xMin), Mathf.Min(had.yMin, clip.yMin), Mathf.Max(had.xMax, clip.xMax), Mathf.Max(had.yMax, clip.yMax)) : clip;
        }

        internal static void NoteZoneCellsChanged(Vector2s zone)
        {
            Vector3 c = ZoneSystem.GetZonePos(zone); float h = ZoneSystem.c_ZoneSizeHalf;
            NoteCellsChanged(zone, Rect.MinMaxRect(c.x - h, c.z - h, c.x + h, c.z + h));
        }

        internal static void FlushCells()
        {
            if (s_cells.Count == 0 || Time.unscaledTime < s_cellsAt) return;
            s_cellsAt = Time.unscaledTime + 1f;
            var h = s_cellsChanged; int n = s_cells.Count; float area = 0f;
            // 0.2.1031 ([fgn] / [seasons] sweep: "Collection was modified" from this loop, x19 in 3 rounds on 1030): a listener's re-read
            // (FAT reads Core's depth) can queue NEW cells while we send; snapshot + clear first, so those land in the next flush
            var batch = new List<KeyValuePair<Vector2s, Rect>>(s_cells);
            s_cells.Clear();
            if (h != null)
                foreach (var kv in batch)
                {
                    area += kv.Value.width * kv.Value.height;
                    foreach (Action<Vector2s, Rect> d in h.GetInvocationList())
                    {
                        long lt0 = System.Diagnostics.Stopwatch.GetTimestamp(); try { d(kv.Key, kv.Value); } catch (Exception e) { FiresLogger.LogDebug($"[Snow] a CellsChanged listener threw: {e.Message}"); } SnowPerfTimers.CellsTicks += System.Diagnostics.Stopwatch.GetTimestamp() - lt0;
                    }
                }
            if (!s_cellsLogged && h != null) { s_cellsLogged = true; FiresLogger.LogInfo($"[Snow] CellsChanged: first send, {n} zone(s), {area:0} m2 ({h.GetInvocationList().Length} listener(s)); later sends are Debug"); }
            else FiresLogger.LogDebug($"[Snow] CellsChanged: {n} zone(s), {area:0} m2 changed (footprints / pieces / path mask)");
        }
    }
}
