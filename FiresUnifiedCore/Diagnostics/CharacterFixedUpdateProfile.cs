using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// 0.2.870 ([ghost], lead 10-27: [worldgen] and [visual] measured MonoUpdaters.FixedUpdate.Character at 85-98 ms/s at the homestead, the top
    /// cost): where that time goes. Brackets Character.CustomFixedUpdate and the sub-methods mods patch on that path (UpdateMotion, UpdateWalking,
    /// UpdateGroundContact, SyncVelocity, UpdateSwimming) with a FIRST-priority prefix and a LAST-priority postfix, so each number includes the
    /// method and every mod's patches on it, split by companion / wild creature / player.
    /// OFF unless the file BepInEx\cache\FiresUnifiedCore\fixedupdate_profile.on exists (checked every 5 s, so it switches with no config edit
    /// while the game runs); when off each bracket is one bool test. Every 30 s while on:
    /// "[Perf] FixedUpdate.Character: T ms/s over N character(s) (companions C ms/s n, creatures W ms/s m, players P ms/s p); by method
    /// (incl. patches): CustomFixedUpdate a, UpdateMotion b, ...; top companion <name> X ms/s".
    /// </summary>
    internal static class CharacterFixedUpdateProfile
    {
        private static bool s_on; private static float s_nextCheck, s_windowStart, s_nextLog;
        private static string FlagPath => Path.Combine(Path.Combine(BepInEx.Paths.CachePath, "FiresUnifiedCore"), "fixedupdate_profile.on");

        internal static bool On   // 0.2.942: internal (CompanionSystemsProfile shares the flag file)
        {
            get
            {
                if (Time.unscaledTime >= s_nextCheck)
                {
                    s_nextCheck = Time.unscaledTime + 5f;
                    bool was = s_on;
                    try { s_on = File.Exists(FlagPath); } catch { s_on = false; }
                    if (s_on != was)
                    {
                        Reset();
                        // 0.2.953: the 0.2.948 per-mod patch timers are NOT installed any more - they re-patched 54 other mods' Harmony patch
                        // methods at runtime, and Fire's client crashed natively (ntdll c0000005, no managed stack) ~98 s after the first live
                        // session that installed them (10-28 03:03). The per-method brackets below stay.
                        FiresCore.Logging.FiresLogger.LogDebug($"[Perf] FixedUpdate.Character profile {(s_on ? "ON" : "OFF")} ({FlagPath})");
                    }
                }
                return s_on;
            }
        }

        private static readonly Dictionary<string, long> s_byMethod = new Dictionary<string, long>();
        private static readonly long[] s_byKind = new long[3];          // 0 companion, 1 creature, 2 player
        private static readonly HashSet<int>[] s_seen = { new HashSet<int>(), new HashSet<int>(), new HashSet<int>() };
        private static readonly Dictionary<Character, long> s_byCompanion = new Dictionary<Character, long>();
        private static int s_depth;   // nested brackets (UpdateMotion inside CustomFixedUpdate) count once for the per-kind totals

        private static void Reset()
        {
            s_byMethod.Clear(); for (int i = 0; i < 3; i++) { s_byKind[i] = 0; s_seen[i].Clear(); } s_byCompanion.Clear();
            s_windowStart = Time.unscaledTime; s_nextLog = Time.unscaledTime + 30f; s_depth = 0;
        }

        private static int KindOf(Character c) => c.IsPlayer() ? 2 : c.GetComponent<FiresCore.Npc.CompanionController>() != null ? 0 : 1;

        private static void Begin(out long t0) { t0 = On ? Stopwatch.GetTimestamp() : 0L; if (t0 != 0L) s_depth++; }

        private static void End(Character c, string method, long t0)
        {
            if (t0 == 0L || c == null) return;
            long d = Stopwatch.GetTimestamp() - t0;
            s_depth--;
            s_byMethod[method] = (s_byMethod.TryGetValue(method, out long m) ? m : 0L) + d;
            if (s_depth == 0)
            {
                int k = KindOf(c);
                s_byKind[k] += d; s_seen[k].Add(c.GetInstanceID());
                if (k == 0) s_byCompanion[c] = (s_byCompanion.TryGetValue(c, out long cc) ? cc : 0L) + d;
            }
            if (Time.unscaledTime >= s_nextLog) Log();
        }

        private static void Log()
        {
            float secs = Mathf.Max(1f, Time.unscaledTime - s_windowStart);
            double ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency / secs;
            long total = s_byKind[0] + s_byKind[1] + s_byKind[2];
            var methods = new List<string>();
            foreach (var kv in s_byMethod) methods.Add($"{kv.Key} {ms(kv.Value):0.0}");
            Character top = null; long topT = 0;
            foreach (var kv in s_byCompanion) if (kv.Key != null && kv.Value > topT) { topT = kv.Value; top = kv.Key; }
            FiresCore.Logging.FiresLogger.LogDebug($"[Perf] FixedUpdate.Character:{ms(total):0.0} ms/s over {s_seen[0].Count + s_seen[1].Count + s_seen[2].Count} character(s) " +
                $"(companions {ms(s_byKind[0]):0.0} ms/s {s_seen[0].Count}, creatures {ms(s_byKind[1]):0.0} ms/s {s_seen[1].Count}, players {ms(s_byKind[2]):0.0} ms/s {s_seen[2].Count}); " +
                $"by method (incl. patches): {string.Join(", ", methods)}" + (top != null ? $"; top companion {top.m_name} {ms(topT):0.0} ms/s" : "")
                + $"; InventoryGui.Update (incl. every mod's patches): open {ms(s_invTicks[1]):0.0} ms/s x{s_invCalls[1]}, closed {ms(s_invTicks[0]):0.0} ms/s x{s_invCalls[0]}");
            s_invTicks[0] = s_invTicks[1] = 0; s_invCalls[0] = s_invCalls[1] = 0;
            Reset();
        }

        [HarmonyPatch(typeof(Character), "CustomFixedUpdate")]
        private static class P_CustomFixedUpdate
        {
            [HarmonyPriority(Priority.First)] private static void Prefix(Character __instance, out long __state) { Begin(out __state); if (__state != 0L && __instance != null) CharacterPatchProfile.KindNow = KindOf(__instance); }   // 0.2.948
            [HarmonyPriority(Priority.Last)] private static void Postfix(Character __instance, long __state) { End(__instance, "CustomFixedUpdate", __state); CharacterPatchProfile.KindNow = 3; }
        }
        // 0.2.953 (lead: InventoryGui.Update 26 ms/s with the inventory open): ONE plain bracket around the whole method (it and every mod's
        // patches on it), split inventory open / closed - no per-patch detours
        private static readonly long[] s_invTicks = new long[2];
        private static readonly int[] s_invCalls = new int[2];
        [HarmonyPatch(typeof(InventoryGui), "Update")]
        private static class P_InventoryGuiUpdate
        {
            [HarmonyPriority(Priority.First)] private static void Prefix(out long __state) => __state = On ? Stopwatch.GetTimestamp() : 0L;
            [HarmonyPriority(Priority.Last)] private static void Postfix(long __state)
            {
                if (__state == 0L) return;
                int k = InventoryGui.IsVisible() ? 1 : 0;
                s_invTicks[k] += Stopwatch.GetTimestamp() - __state; s_invCalls[k]++;
            }
        }

        [HarmonyPatch(typeof(Character), "UpdateMotion")]
        private static class P_UpdateMotion
        {
            [HarmonyPriority(Priority.First)] private static void Prefix(out long __state) => Begin(out __state);
            [HarmonyPriority(Priority.Last)] private static void Postfix(Character __instance, long __state) => End(__instance, "UpdateMotion", __state);
        }
        [HarmonyPatch(typeof(Character), "UpdateWalking")]
        private static class P_UpdateWalking
        {
            [HarmonyPriority(Priority.First)] private static void Prefix(out long __state) => Begin(out __state);
            [HarmonyPriority(Priority.Last)] private static void Postfix(Character __instance, long __state) => End(__instance, "UpdateWalking", __state);
        }
        [HarmonyPatch(typeof(Character), "UpdateGroundContact")]
        private static class P_UpdateGroundContact
        {
            [HarmonyPriority(Priority.First)] private static void Prefix(out long __state) => Begin(out __state);
            [HarmonyPriority(Priority.Last)] private static void Postfix(Character __instance, long __state) => End(__instance, "UpdateGroundContact", __state);
        }
        [HarmonyPatch(typeof(Character), "SyncVelocity")]
        private static class P_SyncVelocity
        {
            [HarmonyPriority(Priority.First)] private static void Prefix(out long __state) => Begin(out __state);
            [HarmonyPriority(Priority.Last)] private static void Postfix(Character __instance, long __state) => End(__instance, "SyncVelocity", __state);
        }
        [HarmonyPatch(typeof(Character), "UpdateSwimming")]
        private static class P_UpdateSwimming
        {
            [HarmonyPriority(Priority.First)] private static void Prefix(out long __state) => Begin(out __state);
            [HarmonyPriority(Priority.Last)] private static void Postfix(Character __instance, long __state) => End(__instance, "UpdateSwimming", __state);
        }
    }
}
