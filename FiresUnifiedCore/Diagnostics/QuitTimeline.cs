using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// [ghost] 11-08 ([lead], Fire's 28 s quit freeze at BlueHills, FDT [Hitch] 28,116 ms at logout with ~0.5 s timed): a millisecond
    /// timeline of the quit, because Unity's log only stamps whole seconds. Evidence so far (the 06:47 quit, 7.7 s): between Vedr's plugin
    /// OnDestroy (06:47:45) and "ZNet OnDestroy" (06:47:53) the log shows only transpiler re-runs (Core [GravityUp] / FiresFixes one-off
    /// patch lines x4): plugins calling Harmony.UnpatchSelf in OnDestroy make Harmony rebuild every shared method once per plugin, while the
    /// scene teardown (~136k pieces) runs in the same window. This splits the two with numbers.
    ///
    /// From Application.quitting on (and only then; before it every hook returns on one bool):
    ///  - every Harmony.UnpatchAll(id) (UnpatchSelf calls it): its time and id, one Debug line each;
    ///  - ZNetView.OnDestroy: count + total time (first / last after quitting);
    ///  - ZNetScene.OnDestroy and ZNet.OnDestroy: when they ran, ms after quitting;
    ///  - one Info summary at ZNet.OnDestroy (a world quit):
    ///    "[Quit] timeline at ZNet.OnDestroy: unpatch N mod(s) X ms (top ms: id a, ...), ZNetView.OnDestroy M in Y ms, ...".
    ///    Unpatches after that point (plugins destroyed later) still log their own Debug line with +ms.
    /// Nothing changes behaviour. Debug lines except the summary (a one-time event per quit = Info).
    /// </summary>
    public static class QuitTimeline
    {
        private static bool s_quitting, s_hooked;
        private static readonly Stopwatch s_clock = new Stopwatch();
        private static readonly List<KeyValuePair<string, double>> s_unpatches = new List<KeyValuePair<string, double>>();
        private static double s_unpatchMs;
        private static int s_viewDestroys; private static long s_viewTicks; private static double s_firstView = -1, s_lastView = -1;
        private static double s_sceneAt = -1, s_znetAt = -1;
        private static bool s_said;

        /// <summary>Called once from Core's plugin Awake.</summary>
        public static void Initialize()
        {
            if (s_hooked) return;
            s_hooked = true;
            Application.quitting += () =>
            {
                s_quitting = true;
                s_clock.Restart();
                FiresLogger.LogDebug("[Quit] Application.quitting: timeline started");
            };
        }

        private static double Now => s_clock.Elapsed.TotalMilliseconds;

        // ── Harmony.UnpatchID(string): UnpatchSelf and the legacy UnpatchAll(string) both end here ──
        [ThreadStatic] private static long t_unpatchStart;

        [HarmonyPatch]
        private static class UnpatchPatch
        {
            // 0.2.1210 fix ([ghost], log-proven on 1209's first quit: "unpatch 0 mod(s)" with no per-mod lines): HarmonyX's UnpatchSelf()
            // calls UnpatchID(string), NOT UnpatchAll(string) (0Harmony.dll read with Cecil: UnpatchSelf -> get_Id, UnpatchID; the legacy
            // UnpatchAll(string) also ends in UnpatchID). So UnpatchID is the one point every self-unpatch passes through.
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(Harmony), "UnpatchID", new[] { typeof(string) });
            private static void Prefix() { if (s_quitting) t_unpatchStart = Stopwatch.GetTimestamp(); }
            private static void Postfix(Harmony __instance, string harmonyID)
            {
                if (!s_quitting || t_unpatchStart == 0) return;
                double ms = (Stopwatch.GetTimestamp() - t_unpatchStart) * 1000.0 / Stopwatch.Frequency;
                t_unpatchStart = 0;
                string id = harmonyID ?? __instance?.Id ?? "?";
                s_unpatches.Add(new KeyValuePair<string, double>(id, ms));
                s_unpatchMs += ms;
                FiresLogger.LogDebug($"[Quit] +{Now:0} ms: {id} unpatched itself in {ms:0.0} ms");
            }
        }

        // ── ZNetView.OnDestroy: count and time after quitting ──
        [ThreadStatic] private static long t_viewStart;
        private static MethodBase ViewOnDestroy() => AccessTools.Method(typeof(ZNetView), "OnDestroy");

        [HarmonyPatch]
        private static class ViewDestroyPatch
        {
            private static MethodBase TargetMethod() => ViewOnDestroy();
            private static void Prefix() { if (s_quitting) t_viewStart = Stopwatch.GetTimestamp(); }
            private static void Postfix()
            {
                if (!s_quitting || t_viewStart == 0) return;
                s_viewTicks += Stopwatch.GetTimestamp() - t_viewStart; t_viewStart = 0;
                s_viewDestroys++;
                double now = Now; if (s_firstView < 0) s_firstView = now; s_lastView = now;
            }
        }

        [HarmonyPatch]
        private static class SceneDestroyPatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(ZNetScene), "OnDestroy");
            private static void Prefix() { if (s_quitting) { s_sceneAt = Now; FiresLogger.LogDebug($"[Quit] +{s_sceneAt:0} ms: ZNetScene.OnDestroy"); } }
        }

        [HarmonyPatch]
        private static class ZNetDestroyPatch
        {
            private static MethodBase TargetMethod() => AccessTools.Method(typeof(ZNet), "OnDestroy");
            private static void Prefix() { if (s_quitting) { s_znetAt = Now; Summary("at ZNet.OnDestroy"); } }
        }

        /// <summary>The summary line (Info the first time per quit, Debug after).</summary>
        public static void Summary(string when)
        {
            if (!s_quitting) return;
            var top = new List<KeyValuePair<string, double>>(s_unpatches);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new List<string>();
            for (int i = 0; i < top.Count && i < 6; i++) parts.Add($"{top[i].Key} {top[i].Value:0}");
            double viewMs = s_viewTicks * 1000.0 / Stopwatch.Frequency;
            string line = $"[Quit] timeline {when} (+{Now:0} ms after Application.quitting): unpatch {s_unpatches.Count} mod(s) {s_unpatchMs:0} ms" +
                          (parts.Count > 0 ? $" (top ms: {string.Join(", ", parts.ToArray())})" : "") +
                          $"; ZNetView.OnDestroy {s_viewDestroys} in {viewMs:0} ms" +
                          (s_firstView >= 0 ? $" (+{s_firstView:0}..+{s_lastView:0} ms)" : "") +
                          $"; ZNetScene.OnDestroy {(s_sceneAt >= 0 ? $"+{s_sceneAt:0} ms" : "not seen")}; ZNet.OnDestroy {(s_znetAt >= 0 ? $"+{s_znetAt:0} ms" : "not seen")}";
            if (!s_said) { s_said = true; FiresLogger.LogInfo(line); } else FiresLogger.LogDebug(line);
        }
    }
}
