using System;
using System.Collections.Generic;
using FiresCore.Bridge;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// The snow step: never per frame. Every 2 s (real time) it polls the path masks (cheap ZDO revision checks); every 30 s of world
    /// time, on a season stage change (SeasonBridge.VisualsChanged) or an environment change it recomputes the cover, re-reads the
    /// burning fires and announces the snowy loaded zones (ZoneChanged + a paint-only repaint of vanilla chunks).
    /// Cover: season (Vedr SnowAmount01; without Vedr 1, so Mountain / Deep North keep snow) × weather (snowing 1, else 0.75),
    /// eased toward over world time (about a quarter day to settle).
    /// </summary>
    internal sealed class SnowTicker : MonoBehaviour
    {
        private const double StepWorldSeconds = 30.0;
        private const float BuildUpSeconds = 1800f;   // world seconds to fill baseline -> full at x1 (a storm: ~5 min)
        private readonly System.Collections.Generic.Dictionary<Vector2s, float> _announcedBuild = new System.Collections.Generic.Dictionary<Vector2s, float>();
        private static SnowTicker s_instance;
        private double _nextStep;
        private float _nextPoll;
        private string _env = "";
        private bool _seasonDirty = true;
        private double _lastWorld;
        private float _announcedCover = -1f, _announcedWinter = -1f;
        private bool _firstStep = true;

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_Snow
        {
            private static void Postfix(ZNet __instance)
            {
                try
                {
                    SnowConfig.Bind();
                    if (s_instance != null) return;
                    s_instance = __instance.gameObject.AddComponent<SnowTicker>();
                    FiresLogger.LogDebug($"[Snow] ticker started (Dynamic snow {(SnowConfig.On ? "ON" : "off")}, Vedr seasons {(SeasonBridge.IsAvailable ? "found" : "not found: snow biomes keep full cover")})");
                }
                catch (Exception e) { FiresLogger.LogDebug($"[Snow] ticker not started: {e.Message}"); }
            }
        }

        private void OnEnable() { SeasonBridge.VisualsChanged += OnSeason; }
        private void OnDisable() { SeasonBridge.VisualsChanged -= OnSeason; if (s_instance == this) s_instance = null; }
        private void OnSeason(int season, int stage) { _seasonDirty = true; }

        private void Update()
        {
            if (!SnowConfig.On || ZNet.instance == null || WorldGenerator.instance == null) { SnowField.GlobalCover = 0f; SnowField.WinterCover = 0f; return; }
            double now = ZNet.instance.GetTimeSeconds();
            string env = EnvMan.instance != null ? (EnvMan.instance.GetCurrentEnvironment()?.m_name ?? "") : "";
            bool envChanged = env != _env;
            if (envChanged)
            {
                _env = env;
                SnowPathMask.RefillBoost = IsBlizzard(env) ? 4f : IsSnowing(env) ? 2f : 1f;
            }

            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + 2f;
                var masks = SnowPathMask.Poll();
                if (masks.Count > 0) Announce(masks, "path mask");
            }

            if (now < _nextStep && !envChanged && !_seasonDirty) return;
            _nextStep = now + StepWorldSeconds;
            _seasonDirty = false;

            // Fire 10-24: the snow bases (Mountain / Deep North, real or forced, Winter territories) keep snow all year like vanilla's
            // Deep North; the season scales it between SummerFloor and 1. Other biomes Vedr whitens get snow only in winter. The weather
            // sets the rate: falling snow builds it up fast (a blizzard faster), clear weather lets it settle slowly.
            float season = SeasonBridge.IsAvailable && SeasonBridge.IsReady ? SeasonBridge.SnowAmount01 : 1f;
            float baseTarget = Mathf.Lerp(SnowField.SummerFloor, 1f, season);
            float winterTarget = season;
            float rate = IsBlizzard(env) ? 6f : IsSnowing(env) ? 3f : 1f;
            double dt = _lastWorld > 0 ? Math.Max(0.0, now - _lastWorld) : 1e9;
            _lastWorld = now;
            float ease = (float)Math.Min(1.0, dt * rate / 450.0);   // clear weather: ~a quarter game day to settle
            float before = SnowField.GlobalCover, winterBefore = SnowField.WinterCover;
            SnowField.GlobalCover = Mathf.MoveTowards(before, baseTarget, Mathf.Max(ease * Mathf.Abs(baseTarget - before), 0.002f));
            SnowField.WinterCover = Mathf.MoveTowards(winterBefore, winterTarget, Mathf.Max(ease * Mathf.Abs(winterTarget - winterBefore), 0.002f));

            var zones = SnowFires.Refresh();

            // Build-up per loaded snow zone: grows while it snows (storm x6, snow x3; BuildUpSeconds of world time to fill at x1),
            // holds in clear weather, settles back toward the baseline in summer (season < 0.2) at the clear rate.
            float stepSeconds = (float)Math.Min(dt, StepWorldSeconds * 4);
            float buildRate = IsBlizzard(env) ? 6f : IsSnowing(env) ? 3f : 0f;
            int logged = 0;
            foreach (var z in LoadedSnowZones())
            {
                float delta = buildRate > 0f ? stepSeconds * buildRate / BuildUpSeconds
                            : season < 0.2f ? -stepSeconds / BuildUpSeconds : 0f;
                if (delta == 0f) continue;
                float a = SnowField.AdvanceBuildUp(z, delta, SnowField.BuildUpBaseline);
                float b = SnowField.BuildUp(z);
                if (Mathf.Abs(b - a) < 0.0005f) continue;
                if (Mathf.Abs(b - (_announcedBuild.TryGetValue(z, out var ann) ? ann : -1f)) >= 0.02f || b >= 1f && ann < 1f)
                {
                    zones.Add(z);
                    _announcedBuild[z] = b;
                }
                if (logged++ < 6) FiresLogger.LogDebug($"[Snow] zone ({z.x},{z.y}) depth {a:0.000} -> {b:0.000} (rate x{(buildRate > 0f ? buildRate : -1f):0}, weather '{env}')");
            }

            bool coverMoved = Mathf.Abs(SnowField.GlobalCover - _announcedCover) > 0.01f || Mathf.Abs(SnowField.WinterCover - _announcedWinter) > 0.01f;
            if (coverMoved || envChanged || _firstStep) { zones.UnionWith(LoadedSnowZones()); _announcedCover = SnowField.GlobalCover; _announcedWinter = SnowField.WinterCover; }
            _firstStep = false;
            // one line per step (every 30 s of world time, never per frame): the whole formula
            FiresLogger.LogDebug($"[Snow] step: base cover = lerp({SnowField.SummerFloor:0.00}, 1, season {season:0.00}) -> target {baseTarget:0.00}, " +
                $"weather '{env}' rate x{rate:0} -> cover {before:0.00} -> {SnowField.GlobalCover:0.00}; winter-only biomes {winterBefore:0.00} -> {SnowField.WinterCover:0.00}; " +
                $"{SnowFires.Count} burning fire(s), {zones.Count} zone(s) updated");
            Announce(zones, null);
        }

        private static void Announce(HashSet<Vector2s> zones, string why)
        {
            int repaint = 0;
            foreach (var z in zones)
            {
                if (!SnowField.ZoneHasSnow(ZoneSystem.GetZonePos(z)) && why == null) continue;
                SnowField.RaiseZoneChanged(z);
                if (!SnowFat.OwnsZone(z)) repaint += SnowHeightmapInject.Repaint(z);
            }
            if (why != null && zones.Count > 0) FiresLogger.LogDebug($"[Snow] {why}: {zones.Count} zone(s) changed, {repaint} chunk(s) repainted");
        }

        private static HashSet<Vector2s> LoadedSnowZones()
        {
            var set = new HashSet<Vector2s>();
            var p = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : (GameCamera.instance != null ? GameCamera.instance.transform.position : Vector3.zero);
            var c = ZoneSystem.GetZone(p);
            int r = 2;   // the active area around the viewer; distant LOD chunks keep their last paint
            for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                {
                    var z = new Vector2s((short)(c.x + x), (short)(c.y + y));
                    if (SnowField.ZoneHasSnow(ZoneSystem.GetZonePos(z))) set.Add(z);
                }
            return set;
        }

        private static bool IsSnowing(string env) => env.IndexOf("snow", StringComparison.OrdinalIgnoreCase) >= 0 || IsBlizzard(env);
        private static bool IsBlizzard(string env) => env.IndexOf("storm", StringComparison.OrdinalIgnoreCase) >= 0 && env.IndexOf("snow", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
