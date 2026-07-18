using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// [SleepDiag] — temporary instrumentation for "sleeping takes forever to wake up".
    /// Logs (1) the skip target + advance speed when a sleep starts, (2) skip progress every ~2s of
    /// real time, (3) the moment the time-skip completes, and (4) whether Game keeps everyone asleep
    /// AFTER the skip is done (wake-gate stall — a different bug class than a slow skip).
    /// No Fires mod touches sleep, so whatever these numbers show is the third-party culprit's shape:
    /// a huge target delta = wrong-morning math; a slow advance = tick/speed interference; a post-skip
    /// stall = a mod holding Game.m_sleeping / blocking SleepStop.
    /// </summary>
    [HarmonyPatch]
    public static class SleepDiag
    {
        private static readonly FieldInfo FSkipTime = AccessTools.Field(typeof(EnvMan), "m_skipTime");
        private static readonly FieldInfo FTotalSeconds = AccessTools.Field(typeof(EnvMan), "m_totalSeconds");
        private static readonly FieldInfo FTimeSkipSpeed = AccessTools.Field(typeof(EnvMan), "m_timeSkipSpeed");
        private static readonly FieldInfo FSleeping = AccessTools.Field(typeof(Game), "m_sleeping");

        private static double _skipStartTotal;
        private static float _skipStartReal;
        private static float _nextProgressLog;
        private static bool _skipActive;
        private static bool _wasSleeping;
        private static float _skipDoneReal = -1f;
        private static float _nextStallLog;

        private static double D(FieldInfo f, object o) { try { return Convert.ToDouble(f?.GetValue(o) ?? 0.0); } catch { return 0.0; } }

        [HarmonyPatch(typeof(EnvMan), "SkipToMorning")]
        [HarmonyPostfix]
        private static void SkipToMorning_Postfix(EnvMan __instance)
        {
            try
            {
                double total = D(FTotalSeconds, __instance);
                double skip = D(FSkipTime, __instance);
                double speed = D(FTimeSkipSpeed, __instance);
                _skipStartTotal = total;
                _skipStartReal = Time.realtimeSinceStartup;
                _skipActive = true;
                _skipDoneReal = -1f;
                _nextProgressLog = _skipStartReal + 2f;
                double eta = speed > 0.01 ? skip / speed : -1.0;
                Debug.Log($"[SleepDiag] SkipToMorning: now={total:F0}s skipAmount={skip:F0} game-sec speed={speed:F1}x fixedDt={Time.fixedDeltaTime:F4} → expected real duration ≈ {eta:F1}s (dayLen={__instance.m_dayLengthSec})");
            }
            catch (Exception ex) { Debug.LogWarning("[SleepDiag] SkipToMorning postfix failed: " + ex.Message); }
        }

        [HarmonyPatch(typeof(EnvMan), "FixedUpdate")]
        [HarmonyPostfix]
        private static void EnvMan_FixedUpdate_Postfix(EnvMan __instance)
        {
            try
            {
                if (!_skipActive) return;
                float now = Time.realtimeSinceStartup;
                bool skipping = __instance.IsTimeSkipping();
                if (skipping)
                {
                    if (now >= _nextProgressLog)
                    {
                        _nextProgressLog = now + 2f;
                        double total = D(FTotalSeconds, __instance);
                        double remain = D(FSkipTime, __instance);
                        double rate = (total - _skipStartTotal) / Math.Max(0.001f, now - _skipStartReal);
                        Debug.Log($"[SleepDiag] skipping… advanced={(total - _skipStartTotal):F0} game-sec in {(now - _skipStartReal):F1} real-s (rate={rate:F0} gs/s) remaining={remain:F0}");
                    }
                }
                else
                {
                    _skipActive = false;
                    _skipDoneReal = now;
                    _nextStallLog = now + 2f;
                    Debug.Log($"[SleepDiag] time-skip COMPLETE after {(now - _skipStartReal):F1} real-s (advanced {(D(FTotalSeconds, __instance) - _skipStartTotal):F0} game-sec)");
                }
            }
            catch { }
        }

        [HarmonyPatch(typeof(Game), "FixedUpdate")]
        [HarmonyPostfix]
        private static void Game_FixedUpdate_Postfix(Game __instance)
        {
            try
            {
                bool sleeping = false;
                try { sleeping = (bool)(FSleeping?.GetValue(__instance) ?? false); } catch { }
                float now = Time.realtimeSinceStartup;
                if (sleeping && !_wasSleeping)
                    Debug.Log("[SleepDiag] Game.m_sleeping = TRUE (sleep started)");
                else if (!sleeping && _wasSleeping)
                    Debug.Log($"[SleepDiag] Game.m_sleeping = FALSE (woke up){(_skipDoneReal >= 0f ? $" — {(now - _skipDoneReal):F1} real-s AFTER the skip completed" : "")}");
                else if (sleeping && !_skipActive && _skipDoneReal >= 0f && now >= _nextStallLog)
                {
                    _nextStallLog = now + 2f;
                    Debug.LogWarning($"[SleepDiag] STALL: skip finished {(now - _skipDoneReal):F1} real-s ago but Game.m_sleeping is still TRUE — something is blocking the wake-up (SleepStop path)");
                }
                _wasSleeping = sleeping;
            }
            catch { }
        }
    }
}
