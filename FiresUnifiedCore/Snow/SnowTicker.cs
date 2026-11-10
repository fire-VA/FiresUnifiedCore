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
        private int _windSector = -1;
        private bool _maxDepthSet;
        private int _seenEpoch;
        private float _nextEaseRepaint, _announcedViewerBuild = -1f, _announcedViewerBuildBefore = -1f;
        private static string s_stepCause = "step";   // 0.2.1192
        private const float SettleShare = 0.25f;   // 0.2.853: clear weather settles the build-up at a quarter of the light-snow rate
        private bool _loadedBuild;
        private bool _saidSeasonWait;   // 0.2.1223: the "season not ready" skip says so once
        private float _nextSave;

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
        private void OnDisable() { SeasonBridge.VisualsChanged -= OnSeason; if (_loadedBuild) SnowField.SaveBuildUp(true); if (s_instance == this) s_instance = null; }
        private void OnSeason(int season, int stage) { _seasonDirty = true; }

        private void Update()
        {
            SnowField.MainFrame = Time.frameCount;   // 0.2.834: the base cache's per-frame budget clock
            SnowBaseMask.BatchTick(); SnowRepaintQueue.Tick();   // 0.2.1133 batch; 0.2.1126: one queued chunk a frame
            SnowMeshRaise.Tick();   // 0.2.1124: one queued chunk a frame
            SnowHeightmapInject.Tick();   // 0.2.959: queued terrain injects, sliced under the 8 ms budget
            SnowTrail.Tick();   // 0.2.1051: walk-trail re-bakes (throttled)
            RaisePendingZone();   // 0.2.961: one announced zone's ZoneChanged per frame
            SnowGround.Flush();   // 0.2.1003: zones whose pieces changed, re-announced at most once a second
            SnowField.FlushCells();   // 0.2.1021: CellsChanged rects, at most once a second
            if (!_maxDepthSet) { _maxDepthSet = true; Shader.SetGlobalFloat("_FiresSnowMaxDepth", SnowField.MaxDepthMetres); }   // 0.2.840: FAT voxel shader scale
            if (!SnowConfig.On || ZNet.instance == null || WorldGenerator.instance == null) { SnowField.GlobalCover = 0f; SnowField.WinterCover = 0f; return; }
            SnowShelter.Tick();   // 0.2.927: the shelter mask's budgeted zone job
            SnowRefill.Tick();   // 0.2.1061: trails fill back in (TerrainComp owner, budgeted)
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
                SnowPathMask.FlushPending();
                // 0.2.1197: Vedr took over (or handed back) the ground snow: one re-bake of the loaded snow zones, so SnowBaseMask puts vanilla G back (or raises it again) at once
                if (SnowVedrGround.Refresh())
                {
                    var loaded = LoadedSnowZones();
                    Announce(loaded, SnowVedrGround.Owns ? "ground snow owner: Vedr" : "ground snow owner: Core");
                    if (!SnowVedrGround.Owns) SnowShelter.MarkZonesDirty(loaded);   // 0.2.1211: Core's piece hooks were idle while Vedr owned it
                }
                var masks = SnowPathMask.Poll();
                if (masks.Count > 0) { Announce(masks, "path mask"); foreach (var mz in masks) SnowField.NoteZoneCellsChanged(mz); }
                // 0.2.829: lee drifts follow the wind sector and new / removed buildings
                // 0.2.842: the snow base changed (paint arrived / changed, territory edit): re-announce the loaded snow zones so FAT
                // refills its voxel depth textures and vanilla chunks repaint
                if (SnowField.BaseEpoch != _seenEpoch) { _seenEpoch = SnowField.BaseEpoch; Announce(LoadedSnowZones(), "snow base changed"); }
                var built = SnowDrift.TakeDirtyZones();
                if (built != null) Announce(built, "drift (building)");
                // 0.2.830: a wind change eases over SnowDrift.BlendSeconds: repaint the loaded snow chunks every ~5 s meanwhile
                if (SnowDrift.WindChanged(ref _windSector) || (SnowDrift.Easing && Time.unscaledTime >= _nextEaseRepaint))
                {
                    _nextEaseRepaint = Time.unscaledTime + 5f;
                    Announce(LoadedSnowZones(), $"drift (wind sector {_windSector}, blend {SnowDrift.Blend:0.00})", true);   // 0.2.1189: drift-only cause
                }
            }

            if (now < _nextStep && !envChanged && !_seasonDirty) return;
            // 0.2.1223 ([generator], lead / [worldgen] 11-10): Vedr present but its season not ready yet = no step at all (no cover change), instead of the
            // full-winter season 1 below, which marked winter snow as fallen for a world that ticked before Vedr was ready. The step is not consumed, so it
            // runs on the first frame Vedr is ready. Debug line once per session.
            if (SeasonBridge.IsAvailable && !SeasonBridge.IsReady)
            {
                if (!_saidSeasonWait) { _saidSeasonWait = true; FiresLogger.LogDebug("[Snow] Vedr's season is not ready yet: snow step skipped (no cover change) until it is"); }
                return;
            }
            _nextStep = now + StepWorldSeconds;
            _seasonDirty = false;

            // Fire 10-24: the snow bases (Mountain / Deep North, real or forced, Winter territories) keep snow all year like vanilla's
            // Deep North; the season scales it between SummerFloor and 1. Other biomes Vedr whitens get snow only in winter. The weather
            // sets the rate: falling snow builds it up fast (a blizzard faster), clear weather lets it settle slowly.
            float season = SeasonBridge.IsAvailable && SeasonBridge.IsReady ? SeasonBridge.SnowAmount01 : 1f;
            float baseTarget = Mathf.Lerp(SnowField.SummerFloor, 1f, season);
            // 0.2.826: without Vedr nothing whitens the other biomes, so winter-only cover is 0 (saves a GetBiome per snow query)
            float winterTarget = SeasonBridge.IsAvailable ? season : 0f;
            if (!_loadedBuild) { _loadedBuild = true; SnowField.LoadBuildUp(); }
            // 0.2.1157: no winter cover until it first snows; summer resets it for next year
            if (winterTarget <= 0.01f && SnowField.WinterCover <= 0f) SnowField.SetWinterSnowFell(false);
            else if (!SnowField.WinterSnowFell) { if (IsSnowing(env) || season >= 0.999f) SnowField.SetWinterSnowFell(true); else winterTarget = 0f; }   // 0.2.1163: Vedr's amount at 1 (forced winter, day 3+) is snow already fallen
            if (!SeasonBridge.IsAvailable) SnowField.WinterCover = 0f;
            float rate = IsBlizzard(env) ? 6f : IsSnowing(env) ? 3f : 1f;
            double dt = _lastWorld > 0 ? Math.Max(0.0, now - _lastWorld) : 1e9;
            _lastWorld = now;
            float ease = (float)Math.Min(1.0, dt * rate / 450.0);   // clear weather: ~a quarter game day to settle
            float before = SnowField.GlobalCover, winterBefore = SnowField.WinterCover;
            SnowField.GlobalCover = Mathf.MoveTowards(before, baseTarget, Mathf.Max(ease * Mathf.Abs(baseTarget - before), 0.002f));
            // 0.2.1165 (Fire, forced Spring at x 3232 z 4613: raised zone with a stepped edge; log: cover still 0.87 in Spring): a jump down follows at once, as a jump up does; the ease left snow G on chunks Vedr had already repainted.
            SnowField.WinterCover = (season >= 0.999f || winterBefore - winterTarget > 0.2f) && SeasonBridge.IsAvailable ? winterTarget : Mathf.MoveTowards(winterBefore, winterTarget, Mathf.Max(ease * Mathf.Abs(winterTarget - winterBefore), 0.002f));   // 0.2.1163 ([seasons], Fire: forced winter must show winter): at full amount no ease

            var zones = SnowFires.Refresh();

            // Build-up per loaded snow zone: grows while it snows (storm x6, snow x3; BuildUpSeconds of world time to fill at x1),
            // settles back toward the baseline in summer (season < 0.2) at the clear rate.
            // 0.2.853 (Fire 10-27 "Yes, grow in snowfall"): clear weather settles it slowly (SettleShare of the light-snow
            // rate: a full pack takes ~2.5 game days back to the baseline); the "Ground snow fill rate" slider multiplies both; build-up is saved per zone.
            float stepSeconds = (float)Math.Min(dt, StepWorldSeconds * 4);
            float buildRate = IsBlizzard(env) ? 6f : IsSnowing(env) ? 3f : 0f;
            float fill = SnowConfig.GroundFillRate;
            if (!_loadedBuild) { _loadedBuild = true; SnowField.LoadBuildUp(); }
            // 0.2.998 (Fire: "One world-wide build-up"): ONE advance per step for the whole world (it was one per loaded zone, so zones
            // loaded longer grew deeper than their neighbours and met in a step at the zone line)
            {
                float delta = buildRate > 0f ? stepSeconds * buildRate * fill / BuildUpSeconds
                            : season < 0.2f ? -stepSeconds * fill / BuildUpSeconds : -stepSeconds * SettleShare * fill / BuildUpSeconds;
                float a = delta != 0f ? SnowField.AdvanceWorldBuildUp(delta, SnowField.BuildUpBaseline) : SnowField.WorldBuildUp;
                if (Mathf.Abs(SnowField.WorldBuildUp - a) >= 0.0005f)
                    FiresLogger.LogVerbose($"[Snow] world build-up {a:0.000} -> {SnowField.WorldBuildUp:0.000} (rate x{(buildRate > 0f ? buildRate : -1f):0}, weather '{env}')");
            }

            // one build-up for every chunk (0.2.830 used the viewer's zone; 0.2.998: the world's); when it moves, every loaded snow zone
            // is re-baked together (below; the repaints are staggered one per frame by Announce)
            {
                var vp = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : (GameCamera.instance != null ? GameCamera.instance.transform.position : Vector3.zero);
                SnowField.ViewerBuildUp = SnowField.WorldBuildUp;
                SnowHeightmapInject.LogBorderProof(vp);
                FiresLogger.LogVerbose($"[Snow] ground accumulation: factor {SnowHeightmapInject.Accumulation:0.00} (build-up {SnowField.ViewerBuildUp:0.00}, weather '{env}', rate x{(buildRate > 0f ? buildRate : -SettleShare):0.##} x fill {fill:0.#}), depth here {SnowField.DepthAt(vp) * SnowField.MaxDepthMetres:0.00} m");
                if (Time.unscaledTime >= _nextSave) { _nextSave = Time.unscaledTime + 120f; SnowField.SaveBuildUp(); }
                _announcedViewerBuildBefore = _announcedViewerBuild;   // 0.2.1192
                if (Mathf.Abs(SnowField.ViewerBuildUp - _announcedViewerBuild) >= 0.02f) { zones.UnionWith(LoadedSnowZones()); _announcedViewerBuild = SnowField.ViewerBuildUp; }
            }
            // 0.2.1052 ([saddles]: FAT BiomePaint colours the vertices as snow from SnowField.WinterGroundAt, baked at a FULL heightmap build;
            // the snow repaints below are paint-only): when the seasonal winter cover starts or ends, every loaded chunk is rebuilt in full
            // once (staggered one per frame), so the snow colour follows the season
            if ((winterBefore <= 0f) != (SnowField.WinterCover <= 0f))
            {
                var all = Heightmap.GetAllHeightmaps();
                int n = 0;
                if (all != null) foreach (var hm in all) // 0.2.1165: with cover 0 the zone filter finds no snow, so nothing would ever lower the base mask: every chunk refreshes here
                if (hm != null && !hm.IsDistantLod) { SnowBaseMask.QueueRefresh(hm, false, "season winter start/end"); SnowRepaintQueue.Add(hm, true); n++; }   // 0.2.1126
                FiresLogger.LogDebug($"[Snow] seasonal winter {(SnowField.WinterCover > 0f ? "started" : "ended")}: {n} loaded chunk(s) rebuilt in full (staggered) so the ground colour follows");
            }
            bool coverMoved =Mathf.Abs(SnowField.GlobalCover - _announcedCover) > 0.01f || Mathf.Abs(SnowField.WinterCover - _announcedWinter) > 0.01f;
            // 0.2.1192: what made this step re-bake, for the per-cause batch line (cover = snow-biome cover, winter = WinterCover, build-up, weather, first; none = fires only)
            {
                var parts = new List<string>();
                if (Mathf.Abs(SnowField.GlobalCover - _announcedCover) > 0.01f) parts.Add("cover");
                if (Mathf.Abs(SnowField.WinterCover - _announcedWinter) > 0.01f) parts.Add("winter");
                if (Mathf.Abs(SnowField.ViewerBuildUp - _announcedViewerBuildBefore) >= 0.02f) parts.Add("build-up");
                if (envChanged) parts.Add("weather");
                if (_firstStep) parts.Add("first");
                s_stepCause = "step (" + (parts.Count > 0 ? string.Join("+", parts) : "fires") + ")";
            }
            if (coverMoved || envChanged || _firstStep) { zones.UnionWith(LoadedSnowZones()); _announcedCover = SnowField.GlobalCover; _announcedWinter = SnowField.WinterCover; }
            _firstStep = false;
            // one line per step (every 30 s of world time, never per frame): the whole formula
            FiresLogger.LogVerbose($"[Snow] step: base cover = lerp({SnowField.SummerFloor:0.00}, 1, season {season:0.00}) -> target {baseTarget:0.00}, " +
                $"weather '{env}' rate x{rate:0} -> cover {before:0.00} -> {SnowField.GlobalCover:0.00}; winter-only biomes {winterBefore:0.00} -> {SnowField.WinterCover:0.00}; " +
                $"{SnowFires.Count} burning fire(s), {zones.Count} zone(s) updated");
            Announce(zones, null);
        }

        private static void Announce(HashSet<Vector2s> zones, string why, bool driftOnly = false)
        {
            int repaint = 0;
            // 0.2.955 ([ghost], spike report 10-28): the listeners (FAT depth fill, EBM / FTS piece snow) are timed, and the vanilla
            // chunk repaints are staggered one per frame instead of all regenerating on the same frame.
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            // 0.2.1074 (Fire 10-31: "the deep snow draws in zones around us first and then fills in our zone"): the player's zone first,
            // then by distance - the staggered repaints and the one-per-frame ZoneChanged go out in that order (same work, same frames)
            long refresh0 = SnowPerfTimers.RefreshTicks; var ordered = new List<Vector2s>(zones);
            Vector2s me = PlayerZone();
            ordered.Sort((a, b) => ZoneDist(a, me).CompareTo(ZoneDist(b, me)));
            foreach (var z in ordered)
            {
                // 0.2.826: path-mask changes too (they raised ZoneChanged + repaints in Meadows)
                if (!SnowField.ZoneHasSnow(ZoneSystem.GetZonePos(z))) continue;
                if (s_pendingSet.Add(z)) s_pending.Add(z);   // 0.2.961: raised one per frame (RaisePendingZone)
                if (!SnowFat.OwnsZone(z)) repaint += SnowHeightmapInject.Repaint(z, 1 + repaint, driftOnly, driftOnly ? "drift (wind)" : why == null ? s_stepCause : why.StartsWith("drift (building") ? "drift (building)" : why);
            }
            SnowHeightmapInject.NoteSlow("zone announce", t0, 8.0, $"{why ?? "step"}: {zones.Count} zone(s), ZoneChanged listeners + {repaint} staggered repaint(s), base mask refresh {SnowPerfTimers.Ms(SnowPerfTimers.RefreshTicks - refresh0):0.0} ms");
            if (zones.Count > 0)   // 0.2.998 (lead): the re-bake cost per step (the queueing here; the repaints run one per frame after)
                FiresLogger.LogDebug($"[Snow] re-bake {(why ?? "step")}: {zones.Count} zone(s) queued, {repaint} vanilla chunk repaint(s) staggered over {repaint} frame(s), queueing {(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.00} ms");
            if (why != null && zones.Count > 0) FiresLogger.LogVerbose($"[Snow] {why}: {zones.Count} zone(s) changed, {repaint} chunk(s) repainted");
        }

        // 0.2.961 ([ghost], fly53 10-28: 150-212 ms frames every 150 s and every ~6 s during a wind ease, all in ScriptRunBehaviourUpdate,
        // no GC): an announce raised ZoneChanged for up to 25 loaded zones at once, and FAT's VoxelSnowDepth drains its whole dirty set
        // the next frame (25 FillZoneDepth + up to 9 texture builds each). The zones now go out one per frame, same fills ~25 frames later.
        private static readonly List<Vector2s> s_pending = new List<Vector2s>();
        private static bool s_firstRaiseLog = true; private static float s_proofAt;

        internal static Vector2s PlayerZone()
        {
            var p = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : (GameCamera.instance != null ? GameCamera.instance.transform.position : Vector3.zero);
            return ZoneSystem.GetZone(p);
        }

        internal static int ZoneDist(Vector2s a, Vector2s b) => Math.Max(Math.Abs(a.x - b.x), Math.Abs(a.y - b.y));
        private static readonly HashSet<Vector2s> s_pendingSet = new HashSet<Vector2s>();
        private static int s_raised; private static float s_raisedLogAt;

        private static void RaisePendingZone()
        {
            if (s_pending.Count == 0) return;
            // 0.2.1074: the nearest pending zone to the player goes out first (was first-queued), so a later announce of the player's own
            // zone never waits behind a ring of outer zones
            Vector2s me = PlayerZone();
            int best = 0;
            for (int i = 1; i < s_pending.Count; i++) if (ZoneDist(s_pending[i], me) < ZoneDist(s_pending[best], me)) best = i;
            var z = s_pending[best];
            s_pending.RemoveAt(best);
            s_pendingSet.Remove(z);
            // 0.2.1077: the proof per announce burst (the first raise after the queue was empty), in the world only (1076's one-shot fired
            // at (0,0) before the player spawned), at most once per 30 s: the first zone out, its ring distance and the player's zone
            bool burstStart = s_firstRaiseLog; s_firstRaiseLog = false;
            if (burstStart && Player.m_localPlayer != null && Time.unscaledTime >= s_proofAt)
            {
                s_proofAt = Time.unscaledTime + 30f;
                FiresLogger.LogDebug($"[Snow] zone changes go out nearest first: first raised ({z.x},{z.y}) ring {ZoneDist(z, me)}, player zone ({me.x},{me.y}), {s_pending.Count} still queued");
            }
            if (s_pending.Count == 0) s_firstRaiseLog = true;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            SnowField.RaiseZoneChanged(z);
            SnowHeightmapInject.NoteSlow("zone changed (listeners)", t0, 8.0, $"zone ({z.x},{z.y}), {s_pending.Count} still queued");
            s_raised++;
            if (s_pending.Count == 0 && Time.unscaledTime >= s_raisedLogAt)
            {
                s_raisedLogAt = Time.unscaledTime + 30f;
                FiresLogger.LogDebug($"[Snow] zone changes raised one per frame: {s_raised} zone(s) since the last line (was all on one frame before 0.2.961)");
                s_raised = 0;
            }
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
