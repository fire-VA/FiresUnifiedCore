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
        public const float MaxDepthMetres = 1.2f;

        /// <summary>A zone's snow changed (season stage, weather, the 30 s world-time step, a path edit or a fire). Never per frame.</summary>
        public static event Action<Vector2s> ZoneChanged;

        private static readonly List<Func<Vector3, float>> s_baseProviders = new List<Func<Vector3, float>>();
        private static readonly List<Func<Vector3, float>> s_melters = new List<Func<Vector3, float>>();

        /// <summary>Adds a snow BASE source (0..1) at a point, e.g. RPGM's Winter territories or a forced snow biome. The highest
        /// source wins. The callback is guarded: one that throws counts as 0.</summary>
        public static void RegisterBaseProvider(Func<Vector3, float> provider) { if (provider != null) lock (s_baseProviders) s_baseProviders.Add(provider); }

        /// <summary>Adds a local melter (0..1 melt at a point, client-local, guarded). Burning fires are built in.</summary>
        public static void RegisterMelter(Func<Vector3, float> melter) { if (melter != null) lock (s_melters) s_melters.Add(melter); }

        /// <summary>Snow base 0..1 at a point before season/weather: 1 in Mountain / Deep North, else the highest provider.</summary>
        public static float BaseAt(Vector3 p)
        {
            if (!Enabled) return 0f;
            float b = 0f;
            var biome = WorldGenerator.instance.GetBiome(p);
            if (biome == Heightmap.Biome.Mountain || biome == Heightmap.Biome.DeepNorth) b = 1f;
            if (b >= 1f) return 1f;
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
            return Mathf.Clamp01(raw * (1f - MeltAt(p)) * (1f - SnowPathMask.ClearAt(p)));
        }

        /// <summary>Cover before melt and paths: a snow base × the year-round cover, else Vedr's winter cover in the biomes it
        /// whitens in winter, else 0 - then × the zone's build-up (<see cref="BuildUp"/>).</summary>
        private static float RawCoverAt(Vector3 p)
        {
            float c;
            float b = BaseAt(p);
            if (b > 0f) c = b * GlobalCover;
            else if (WinterCover <= 0f) return 0f;
            else c = SeasonBridge.IsSnowTerrainBiome(WorldGenerator.instance.GetBiome(p)) ? WinterCover : 0f;
            return c <= 0f ? 0f : c * BuildUp(ZoneSystem.GetZone(p));
        }

        // ── build-up (Fire 10-24: "snow must visibly build up over in-game time") ──────────────────────────────────────────────
        // Each zone holds a build-up 0..1 that starts at BuildUpBaseline when first seen and grows on the snow step while it snows
        // (a storm fastest), holds in clear weather, and settles back toward the baseline in summer. Cover / depth are scaled by it,
        // so a player watches the snow deepen through a storm. It's local per peer (a zone first seen at different times can
        // differ a little until it fills); cosmetic only.

        /// <summary>A zone's build-up when first seen.</summary>
        public const float BuildUpBaseline = 0.35f;
        private static readonly Dictionary<Vector2s, float> s_buildUp = new Dictionary<Vector2s, float>();

        /// <summary>A zone's snow build-up 0..1 (scales cover and depth).</summary>
        public static float BuildUp(Vector2s zone) => s_buildUp.TryGetValue(zone, out var v) ? v : BuildUpBaseline;

        /// <summary>Advances a zone's build-up by delta (clamped to [baseline floor, 1]); returns the old value.</summary>
        internal static float AdvanceBuildUp(Vector2s zone, float delta, float floor)
        {
            float a = BuildUp(zone);
            float b = Mathf.Clamp(a + delta, Mathf.Min(floor, a), 1f);
            s_buildUp[zone] = b;
            return a;
        }

        internal static void ForgetBuildUp(Vector2s zone) => s_buildUp.Remove(zone);

        /// <summary>Snow depth 0..1 at a point (the same as coverage for now: depth grows with cover; shaders scale by DepthScale).</summary>
        public static float DepthAt(Vector3 p) => CoverageAt(p);

        /// <summary>Fills a size×size R8 buffer with the zone's depth (0..255) at the paint mask's UV layout (row = z, column = x,
        /// covering the 64 m zone corner to corner). Returns false (buffer untouched) when the zone has no snow.</summary>
        public static bool FillZoneDepth(Vector2s zone, byte[] r8, int size)
        {
            if (!Enabled || r8 == null || size < 2 || r8.Length < size * size) return false;
            Vector3 c = ZoneSystem.GetZonePos(zone);
            float half = ZoneSystem.c_ZoneSizeHalf;
            if (!ZoneHasSnow(c)) return false;
            float step = (half * 2f) / (size - 1);
            bool any = false;
            for (int z = 0; z < size; z++)
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector3(c.x - half + x * step, 0f, c.z - half + z * step);
                    byte v = (byte)Mathf.RoundToInt(DepthAt(p) * 255f);
                    r8[z * size + x] = v;
                    if (v != 0) any = true;
                }
            return any;
        }

        /// <summary>Cheap test: could this zone (centre) hold snow at all (any corner in a snow base)?</summary>
        internal static bool ZoneHasSnow(Vector3 centre)
        {
            if (GlobalCover <= 0f && WinterCover <= 0f) return false;
            float h = ZoneSystem.c_ZoneSizeHalf;
            return RawCoverAt(centre) > 0f || RawCoverAt(centre + new Vector3(-h, 0, -h)) > 0f || RawCoverAt(centre + new Vector3(h, 0, -h)) > 0f
                || RawCoverAt(centre + new Vector3(-h, 0, h)) > 0f || RawCoverAt(centre + new Vector3(h, 0, h)) > 0f;
        }

        internal static void RaiseZoneChanged(Vector2s zone)
        {
            var h = ZoneChanged;
            if (h == null) return;
            foreach (Action<Vector2s> d in h.GetInvocationList())
            {
                try { d(zone); } catch (Exception e) { FiresLogger.LogDebug($"[Snow] a ZoneChanged listener threw: {e.Message}"); }
            }
        }
    }
}
