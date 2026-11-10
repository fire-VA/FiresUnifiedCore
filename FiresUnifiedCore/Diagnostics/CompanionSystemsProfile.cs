using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// 0.2.942 ([ghost], lead 10-27 perf at the homestead: FPS 12.3, FixedUpdate.Character 44.4 ms/s, Update.VisEquipment 18.7 ms/s):
    /// measuring only, on the same switch as CharacterFixedUpdateProfile (BepInEx\cache\FiresUnifiedCore\fixedupdate_profile.on;
    /// off = one bool read). Every 5 s at Debug: "[Perf] companion systems: <system> x ms/s (n calls)" for
    ///  - CompanionAI.UpdateAI (the whole companion AI tick, owner-side) total and per companion;
    ///  - its parts in RequestPathfindingMovement: unstuck, route move, pace (CompanionAI calls Measure);
    ///  - the combat-move chooser, the snow / rebuild / defence runners (their Update);
    ///  - VisEquipment.Update split player / companion / other, with the companion count.
    /// </summary>
    internal static class CompanionSystemsProfile
    {
        private static readonly Dictionary<string, (long ticks, int calls)> s_sys = new Dictionary<string, (long, int)>();
        private static readonly Dictionary<string, long> s_byCompanion = new Dictionary<string, long>();
        private static float s_windowStart = -1f;
        private static double Ms(long ticks, float secs) => ticks * 1000.0 / Stopwatch.Frequency / Mathf.Max(0.001f, secs);

        internal static bool On => CharacterFixedUpdateProfile.On;
        internal static long Begin() => On ? Stopwatch.GetTimestamp() : 0L;
        internal static void End(string system, long t0, string who = null)
        {
            if (t0 == 0L) return;
            long d = Stopwatch.GetTimestamp() - t0;
            s_sys.TryGetValue(system, out var v); s_sys[system] = (v.ticks + d, v.calls + 1);
            if (who != null) { s_byCompanion.TryGetValue(who, out long w); s_byCompanion[who] = w + d; }
            Tick();
        }

        private static void Tick()
        {
            if (s_windowStart < 0f) { s_windowStart = Time.unscaledTime; return; }
            float secs = Time.unscaledTime - s_windowStart;
            if (secs < 5f) return;
            string sys = string.Join(", ", s_sys.OrderByDescending(kv => kv.Value.ticks).Select(kv => $"{kv.Key} {Ms(kv.Value.ticks, secs):0.0} ms/s ({kv.Value.calls / secs:0}/s)"));
            string per = string.Join(", ", s_byCompanion.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key} {Ms(kv.Value, secs):0.0}"));
            FiresCore.Logging.FiresLogger.LogDebug($"[Perf] companion systems {secs:0} s: {sys}; AI per companion (ms/s): {per}{VisSummary(secs)}");
            s_sys.Clear(); s_byCompanion.Clear(); s_windowStart = Time.unscaledTime;
        }

        [HarmonyPatch(typeof(FiresCore.Npc.AI.CompanionAI), nameof(FiresCore.Npc.AI.CompanionAI.UpdateAI))]
        private static class P_UpdateAI
        {
            [HarmonyPriority(Priority.First)] private static void Prefix(out long __state) => __state = Begin();
            [HarmonyPriority(Priority.Last)] private static void Postfix(FiresCore.Npc.AI.CompanionAI __instance, long __state) { if (__state != 0L) End("CompanionAI.UpdateAI", __state, __instance.GetComponent<Character>()?.m_name); }
        }

        // 0.2.944 (lead 10-27, FDT AllocDiag in Fire's session: 14 of 24 GC world stops inside Humanoid.SetupVisEquipment): who calls it,
        // how often per character and how long it takes (all patches included); every 50th call's caller frames are tallied, so the
        // re-runner is named in the 5 s line: "SetupVisEquipment <name> n/s x ms/s; callers: <frames> xN"
        private static readonly Dictionary<string, (long ticks, int calls)> s_vis = new Dictionary<string, (long, int)>();
        private static readonly Dictionary<string, int> s_visCallers = new Dictionary<string, int>();
        private static int s_visN;
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.SetupVisEquipment))]
        private static class P_SetupVisEquipment
        {
            [HarmonyPriority(Priority.First)] private static void Prefix(out long __state) => __state = Begin();
            [HarmonyPriority(Priority.Last)] private static void Postfix(Humanoid __instance, long __state)
            {
                if (__state == 0L || __instance == null) return;
                long d = Stopwatch.GetTimestamp() - __state;
                string who = __instance is Player ? "player" : __instance.GetComponent<FiresCore.Npc.CompanionController>() != null ? "companion " + __instance.m_name : "other " + Utils.GetPrefabName(__instance.gameObject);
                s_vis.TryGetValue(who, out var v); s_vis[who] = (v.ticks + d, v.calls + 1);
                if (++s_visN % 50 == 1)
                {
                    var st = new StackTrace(2, false); var frames = new List<string>();
                    for (int i = 0; i < st.FrameCount && frames.Count < 3; i++)
                    {
                        var m = st.GetFrame(i)?.GetMethod();
                        if (m == null || m.DeclaringType == null || m.Name.StartsWith("DMD") || m.DeclaringType.Name.Contains("Harmony")) continue;
                        frames.Add(m.DeclaringType.Name + "." + m.Name);
                    }
                    string key = string.Join(" < ", frames);
                    s_visCallers[key] = (s_visCallers.TryGetValue(key, out int n) ? n : 0) + 1;
                }
                End("SetupVisEquipment (all)", __state);
            }
        }
        internal static string VisSummary(float secs)
        {
            if (s_vis.Count == 0) return "";
            string per = string.Join(", ", s_vis.OrderByDescending(kv => kv.Value.calls).Take(8).Select(kv => $"{kv.Key} {kv.Value.calls / secs:0.0}/s {Ms(kv.Value.ticks, secs):0.0} ms/s"));
            string callers = string.Join("; ", s_visCallers.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Key} x{kv.Value * 50}"));
            s_vis.Clear(); s_visCallers.Clear();
            return $"; SetupVisEquipment by character: {per}; callers (sampled 1/50): {callers}";
        }

        [HarmonyPatch(typeof(VisEquipment), nameof(VisEquipment.CustomUpdate))]   // 0.2.942 [wishbone]: 1.0 has no VisEquipment.Update (Verify PATCH GONE); CustomUpdate is its per-frame update
        private static class P_VisEquipment
        {
            [HarmonyPriority(Priority.First)] private static void Prefix(out long __state) => __state = Begin();
            [HarmonyPriority(Priority.Last)] private static void Postfix(VisEquipment __instance, long __state)
            {
                if (__state == 0L) return;
                var c = __instance.GetComponent<Character>();
                string kind = c is Player ? "VisEquipment (players)" : c != null && c.GetComponent<FiresCore.Npc.CompanionController>() != null ? "VisEquipment (companions)" : "VisEquipment (other)";
                End(kind, __state);
            }
        }
    }
}
