using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Refreshes ZSyncTransform's cached kinematic flag from the live body before each ClientSync. Vanilla caches it
    /// once at init, but bodies turn kinematic at runtime (sleeping creatures, companion defeat and teleport), and
    /// the stale flag made non-owners write velocities to kinematic bodies every tick, which Unity rejects with a
    /// warning. With the live flag, vanilla takes its own kinematic MovePosition path.
    /// Not an attribute patch; <see cref="KinematicSync.Bind"/> attaches it unless option B is on.
    /// </summary>
    internal static class ZSyncTransformKinematicRefreshPatch
    {
        private static readonly AccessTools.FieldRef<ZSyncTransform, Rigidbody> BodyRef =
            AccessTools.FieldRefAccess<ZSyncTransform, Rigidbody>("m_body");
        private static readonly AccessTools.FieldRef<ZSyncTransform, bool> KinematicFlagRef =
            AccessTools.FieldRefAccess<ZSyncTransform, bool>("m_isKinematicBody");

        // Perf: each instance refreshes only every 8th frame, staggered by its
        // instance id, so the other calls cost one compare. A body that just turned kinematic keeps the old flag for at most 7 frames.
        private const int Stride = 8;
        private static int s_refreshed, s_calls;
        private static float s_nextReport;
        private static bool s_announced;

        internal static void Apply(Harmony harmony) =>
            harmony.Patch(AccessTools.Method(typeof(ZSyncTransform), "ClientSync"), prefix: new HarmonyMethod(typeof(ZSyncTransformKinematicRefreshPatch), nameof(RefreshKinematicFlag)));

        private static void RefreshKinematicFlag(ZSyncTransform __instance)
        {
            s_calls++;
            if (!s_announced)
            {
                s_announced = true;
                FiresCore.Logging.FiresLogger.LogDebug($"[ZSyncKinematic] patch attached: ClientSync kinematic refresh staggered, every {Stride}th frame per object (0.2.471)");
            }
            if (((Time.frameCount + __instance.GetInstanceID()) & (Stride - 1)) != 0) return;
            try
            {
                var body = BodyRef(__instance);
                if (body != null)
                {
                    KinematicFlagRef(__instance) = body.isKinematic;
                    s_refreshed++;
                }
            }
            catch { }
            if (Time.unscaledTime >= s_nextReport)
            {
                if (s_nextReport > 0f && FiresCore.Logging.FiresLogger.DebugEnabled)
                    if (FiresCore.Logging.Diag.Periodic) FiresCore.Logging.FiresLogger.LogDebug($"[ZSyncKinematic] refreshed {s_refreshed} of {s_calls} ClientSync calls in the last minute (every {Stride}th frame per object)");
                s_refreshed = 0; s_calls = 0;
                s_nextReport = Time.unscaledTime + 60f;
            }
        }

        internal static void SetFlag(ZSyncTransform sync, bool kinematic) => KinematicFlagRef(sync) = kinematic;
        internal static bool GetFlag(ZSyncTransform sync) => KinematicFlagRef(sync);
        internal static Rigidbody GetBody(ZSyncTransform sync) => BodyRef(sync);
    }

    /// <summary>
    /// ZSync option B (A/B, default OFF). ON: no per-tick ClientSync prefix.
    /// Core's own runtime kinematic toggles go through <see cref="Set"/> (the body AND its ZSyncTransform flag), and a sweep every 2 s
    /// over ZNetScene's active instances catches vanilla's / other mods' toggles. OFF: the staggered prefix.
    /// Restart to switch.
    /// </summary>
    internal static class KinematicSync
    {
        private static ConfigEntry<bool> s_optionB;
        internal static bool OptionB => s_optionB != null && s_optionB.Value;

        public static void Bind(ConfigFile config, Harmony harmony)
        {
            s_optionB = config.Bind("Performance", "Kinematic sync sweep", false,
                "Restart to apply. On: keeps network-synced bodies' kinematic flag right with a sweep every 2 s plus Core's own toggles, instead of a check on every sync tick (~5 ms/s less). A body another mod freezes may log Unity's kinematic-velocity warning for up to 2 s. Off: the per-tick check.");
            if (OptionB)
            {
                var go = new GameObject("FiresKinematicSync");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<Sweeper>();
                FiresCore.Logging.FiresLogger.LogDebug("[KinematicSync] option B on: no ClientSync prefix; sweep every 2 s + Core's toggles");
            }
            else ZSyncTransformKinematicRefreshPatch.Apply(harmony);
        }

        private static float s_setLogAt;

        /// <summary>Sets <paramref name="body"/>.isKinematic and, under option B, its ZSyncTransform's cached flag in the same call.</summary>
        public static void Set(Rigidbody body, bool kinematic, string site)
        {
            if (body == null) return;
            body.isKinematic = kinematic;
            if (!OptionB) return;
            var sync = body.GetComponent<ZSyncTransform>();
            if (sync != null) ZSyncTransformKinematicRefreshPatch.SetFlag(sync, kinematic);
            if (Time.unscaledTime >= s_setLogAt)
            {
                s_setLogAt = Time.unscaledTime + 1f;
                FiresCore.Logging.FiresLogger.LogDebug($"[KinematicSync] set {body.name} kinematic {kinematic} ({site}{(sync == null ? ", no ZSyncTransform" : "")})");
            }
        }

        private sealed class Sweeper : MonoBehaviour
        {
            private const float Interval = 2f;
            private float _next, _nextReport;
            private int _bodies, _fixed, _sweeps;
            private readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();

            private void Update()
            {
                if (Time.unscaledTime < _next) return;
                _next = Time.unscaledTime + Interval;
                var scene = ZNetScene.instance;
                if (scene == null) return;
                _sw.Start();
                try
                {
                    foreach (KeyValuePair<ZDO, ZNetView> kv in scene.m_instances)
                    {
                        ZNetView nv = kv.Value;
                        if (nv == null) continue;
                        var sync = nv.GetComponent<ZSyncTransform>();
                        if (sync == null) continue;
                        var body = ZSyncTransformKinematicRefreshPatch.GetBody(sync);
                        if (body == null) continue;
                        _bodies++;
                        if (ZSyncTransformKinematicRefreshPatch.GetFlag(sync) != body.isKinematic) { ZSyncTransformKinematicRefreshPatch.SetFlag(sync, body.isKinematic); _fixed++; }
                    }
                }
                catch (Exception ex) { FiresCore.Logging.FiresLogger.LogDebug($"[KinematicSync] sweep skipped: {ex.GetType().Name}: {ex.Message}"); }
                _sw.Stop();
                _sweeps++;
                if (Time.unscaledTime >= _nextReport)
                {
                    if (_nextReport > 0f)
                        FiresCore.Logging.FiresLogger.LogDebug($"[KinematicSync] sweep: {_sweeps} sweep(s), {_bodies} bodies checked, {_fixed} flag(s) fixed, {_sw.Elapsed.TotalMilliseconds:0.0} ms in the last minute");
                    _bodies = 0; _fixed = 0; _sweeps = 0; _sw.Reset();
                    _nextReport = Time.unscaledTime + 60f;
                }
            }
        }
    }
}
