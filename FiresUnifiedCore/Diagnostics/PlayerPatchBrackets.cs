using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// 0.2.878 ([wishbone], lead 10-27: round #5 had the one local player at 6.5-6.8 ms/s in Character FixedUpdate and Climbing at 0.13 ms/s):
    /// times the other patches on the player's FixedUpdate path one by one. Same switch as the FixedUpdate profiler (the file
    /// BepInEx\cache\FiresUnifiedCore\fixedupdate_profile.on): the first time it is seen on, each suspect patch method (found by
    /// Harmony.GetPatchInfo on Character.CustomFixedUpdate / UpdateMotion / UpdateWalking and Player.FixedUpdate, matched by class name)
    /// gets a FIRST prefix + LAST postfix of its own; they stay attached and cost one bool test while off. Covers every body the patch runs
    /// for (a patch method has no common instance argument). Every 10 s while on:
    /// "[Perf] player-path patches (all bodies): owner:Class.Method on Target x.xx ms/s (n calls), ..." sorted by cost. Log-only.
    /// </summary>
    internal static class PlayerPatchBrackets
    {
        private static readonly string[] Suspects = { "PossessionProxy", "PossessionEngine", "BroadBrackets", "PlayerCurrentPatch", "TerritoryRuntime", "HoldOnUnsolidSkyland", "SlowFall", "CharacterSnowCover", "FiresMountGaitRampPatch", "Patch_Trigger", "CompanionSpeedRamp", "SwimDepthBySize", "Headroom" };
        private static readonly (Type t, string m)[] Targets = { (typeof(Character), "CustomFixedUpdate"), (typeof(Character), "UpdateMotion"), (typeof(Character), "UpdateWalking"), (typeof(Player), "FixedUpdate") };

        private sealed class Slot { public string Label; public long Ticks; public int Calls; }
        private static readonly Dictionary<MethodBase, Slot> s_slots = new Dictionary<MethodBase, Slot>();
        private static bool s_on, s_attached;
        private static float s_nextCheck, s_windowStart = -1f;
        private static Harmony s_harmony;
        private static string FlagPath => Path.Combine(Path.Combine(BepInEx.Paths.CachePath, "FiresUnifiedCore"), "fixedupdate_profile.on");

        /// <summary>Called once per local-player physics step (from Climb's local cost hook).</summary>
        internal static void Tick()
        {
            float now = Time.unscaledTime;
            if (now >= s_nextCheck)
            {
                s_nextCheck = now + 5f;
                bool was = s_on;
                try { s_on = File.Exists(FlagPath); } catch { s_on = false; }
                if (s_on && !s_attached) Attach();
                if (s_on != was) { Reset(now); FiresCore.Logging.FiresLogger.LogDebug($"[Perf] player-path patch brackets {(s_on ? "ON" : "OFF")} ({s_slots.Count} patch method(s))"); }
            }
            if (!s_on) return;
            if (s_windowStart < 0f) { Reset(now); return; }
            if (now - s_windowStart >= 10f) Log(now);
        }

        private static void Reset(float now) { foreach (var s in s_slots.Values) { s.Ticks = 0; s.Calls = 0; } s_windowStart = now; }

        private static void Attach()
        {
            s_attached = true;
            try
            {
                s_harmony = new Harmony("com.Fire.FiresUnifiedCore.patchbrackets");
                var pre = new HarmonyMethod(typeof(PlayerPatchBrackets).GetMethod(nameof(Pre), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.First };
                var post = new HarmonyMethod(typeof(PlayerPatchBrackets).GetMethod(nameof(Post), BindingFlags.Static | BindingFlags.NonPublic)) { priority = Priority.Last };
                foreach (var (t, m) in Targets)
                {
                    var target = AccessTools.Method(t, m);
                    var info = target != null ? Harmony.GetPatchInfo(target) : null;
                    if (info == null) continue;
                    var all = new List<Patch>(); all.AddRange(info.Prefixes); all.AddRange(info.Postfixes);
                    foreach (var p in all)
                    {
                        var pm = p.PatchMethod;
                        if (pm == null || s_slots.ContainsKey(pm)) continue;
                        string cls = pm.DeclaringType?.Name ?? "?";
                        if (Array.FindIndex(Suspects, s => cls.IndexOf(s, StringComparison.Ordinal) >= 0) < 0) continue;
                        try
                        {
                            s_harmony.Patch(pm, prefix: pre, postfix: post);
                            s_slots[pm] = new Slot { Label = $"{p.owner}:{cls}.{pm.Name} on {t.Name}.{m}" };
                        }
                        catch (Exception ex) { FiresCore.Logging.FiresLogger.LogDebug($"[Perf] could not bracket {p.owner}:{cls}.{pm.Name}: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex) { FiresCore.Logging.FiresLogger.LogDebug($"[Perf] player-path patch brackets failed: {ex.Message}"); }
        }

        private static void Pre(out long __state) => __state = s_on ? Stopwatch.GetTimestamp() : 0L;

        private static void Post(MethodBase __originalMethod, long __state)
        {
            if (__state == 0L || __originalMethod == null) return;
            if (s_slots.TryGetValue(__originalMethod, out var s)) { s.Ticks += Stopwatch.GetTimestamp() - __state; s.Calls++; }
        }

        private static void Log(float now)
        {
            float secs = Mathf.Max(1f, now - s_windowStart);
            var rows = new List<Slot>(s_slots.Values);
            rows.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
            var parts = new List<string>();
            foreach (var s in rows) parts.Add($"{s.Label} {s.Ticks * 1000.0 / Stopwatch.Frequency / secs:0.00} ms/s ({s.Calls} calls)");
            FiresCore.Logging.FiresLogger.LogInfo($"[Perf] player-path patches (all bodies) over {secs:0} s: " + (parts.Count == 0 ? "none bracketed" : string.Join(", ", parts)));
            Reset(now);
        }
    }
}
