using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// 0.2.948 ([wishbone], lead 10-28: Fire's session "FixedUpdate.Character: 47.0 ms/s over 29 characters (... creatures 26.7 ms/s 26, players
    /// 14.4 ms/s 1)"): the per-MOD split. When the FixedUpdate profile flag file turns on (CharacterFixedUpdateProfile), every Harmony prefix /
    /// postfix / finalizer that ANY mod put on the character movement / liquid path (the Targets below, all overloads) gets a timing bracket
    /// of its own, keyed by the patch owner (Harmony id), the patched method and the patch method, split by companion / creature / player
    /// (the kind of the character whose CustomFixedUpdate is running; "outside" when called elsewhere). Transpilers cannot be timed apart and
    /// are listed by owner. Installed once, on the first switch to ON; while the flag is off each bracket is one bool test.
    /// Every 30 s while on, Debug: "[Perf] patches on the character path (ms/s): owner method.patch [kind] X, ... (top 20); transpilers: ...".
    /// </summary>
    internal static class CharacterPatchProfile
    {
        private const string OwnId = "com.Fire.FiresUnifiedCore.patchprofile";
        private static readonly (Type type, string name)[] Targets =
        {
            (typeof(Character), "CustomFixedUpdate"), (typeof(Character), "UpdateMotion"), (typeof(Character), "UpdateWalking"),
            (typeof(Character), "UpdateGroundContact"), (typeof(Character), "SyncVelocity"), (typeof(Character), "UpdateSwimming"),
            (typeof(Character), "UpdateWater"), (typeof(Character), "UpdateLava"), (typeof(Character), "UpdateHeatDamage"),
            (typeof(Character), "ApplyLiquidResistance"), (typeof(Character), "IsSwimming"), (typeof(Character), "InLiquidSwimDepth"),
            (typeof(Character), "InLiquidKneeDepth"), (typeof(Floating), "GetLiquidLevel"), (typeof(Floating), "GetWaterLevel"),
            (typeof(InventoryGui), "Update"),   // 0.2.952 (lead: InventoryGui.Update 26 ms/s on Fire's overlay): split inventory open / closed
        };
        private static readonly string[] KindNames = { "companion", "creature", "player", "outside" };

        private static bool s_installed;
        private static readonly Dictionary<MethodBase, string> s_keyOf = new Dictionary<MethodBase, string>();
        private static readonly Dictionary<string, long> s_ticks = new Dictionary<string, long>();
        private static readonly List<string> s_transpilers = new List<string>();
        private static float s_windowStart, s_nextLog;
        /// <summary>Set by CharacterFixedUpdateProfile around CustomFixedUpdate: 0 companion, 1 creature, 2 player, 3 none.</summary>
        internal static int KindNow = 3;

        /// <summary>Called when the profile flag turns on: puts a bracket on every foreign patch of the Targets (once per session).</summary>
        internal static void OnSwitchedOn()
        {
            s_ticks.Clear(); s_windowStart = Time.unscaledTime; s_nextLog = Time.unscaledTime + 30f;
            if (s_installed) return;
            s_installed = true;
            var h = new Harmony(OwnId);
            var pre = new HarmonyMethod(AccessTools.Method(typeof(CharacterPatchProfile), nameof(TimerPrefix))) { priority = Priority.First };
            var post = new HarmonyMethod(AccessTools.Method(typeof(CharacterPatchProfile), nameof(TimerPostfix))) { priority = Priority.Last };
            int timed = 0, failed = 0;
            foreach (var (type, name) in Targets)
                foreach (var target in AccessTools.GetDeclaredMethods(type).Where(m => m.Name == name))
                {
                    var info = Harmony.GetPatchInfo(target);
                    if (info == null) continue;
                    foreach (var t in info.Transpilers) s_transpilers.Add($"{t.owner} {name}.{t.PatchMethod.Name}");
                    foreach (var p in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                    {
                        if (p.owner == OwnId || p.PatchMethod.DeclaringType?.DeclaringType == typeof(CharacterFixedUpdateProfile)) continue;
                        if (s_keyOf.ContainsKey(p.PatchMethod)) continue;
                        s_keyOf[p.PatchMethod] = $"{p.owner} {name}.{p.PatchMethod.DeclaringType?.Name}.{p.PatchMethod.Name}";
                        try { h.Patch(p.PatchMethod, prefix: pre, postfix: post); timed++; }
                        catch (Exception e) { failed++; s_keyOf.Remove(p.PatchMethod); FiresCore.Logging.FiresLogger.LogDebug($"[Perf] patch timer not attached to {p.owner} {p.PatchMethod.DeclaringType?.Name}.{p.PatchMethod.Name}: {e.Message}"); }
                    }
                }
            FiresCore.Logging.FiresLogger.LogDebug($"[Perf] patch timers on the character path: {timed} patch method(s) timed, {failed} not attachable, {s_transpilers.Count} transpiler(s) (untimed): {string.Join("; ", s_transpilers)}");
        }

        private static void TimerPrefix(out long __state) => __state = CharacterFixedUpdateProfile.On ? Stopwatch.GetTimestamp() : 0L;

        private static void TimerPostfix(MethodBase __originalMethod, long __state)
        {
            if (__state == 0L || __originalMethod == null || !s_keyOf.TryGetValue(__originalMethod, out string key)) return;
            long d = Stopwatch.GetTimestamp() - __state;
            string k = key + " [" + (key.Contains(" Update.") && InventoryGui.instance != null ? (InventoryGui.IsVisible() ? "inventory open" : "inventory closed") : KindNames[Mathf.Clamp(KindNow, 0, 3)]) + "]";
            s_ticks[k] = (s_ticks.TryGetValue(k, out long t) ? t : 0L) + d;
            if (Time.unscaledTime >= s_nextLog) Log();
        }

        private static void Log()
        {
            float secs = Mathf.Max(1f, Time.unscaledTime - s_windowStart);
            var top = s_ticks.OrderByDescending(kv => kv.Value).Take(20).Select(kv => $"{kv.Key} {kv.Value * 1000.0 / Stopwatch.Frequency / secs:0.00}");
            FiresCore.Logging.FiresLogger.LogDebug($"[Perf] patches on the character path (ms/s, last {secs:0} s, top 20): {string.Join(", ", top)}");
            s_ticks.Clear(); s_windowStart = Time.unscaledTime; s_nextLog = Time.unscaledTime + 30f;
        }
    }
}
