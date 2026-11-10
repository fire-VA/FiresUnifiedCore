using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// 0.2.846 ([ghost], Fire's 11-16 fps on 845: "busiest sites: Pathfinding.GetPath ×146 (longest 2 ms)", 23-28 ms in single frames).
    /// The DIRECT Pathfinding.GetPath calls Core's chore code makes (reach tests in CanStart scans, stand-point searches - not vanilla's
    /// own BaseAI.FindPath / HavePath walking, which is left alone) are:
    ///   - cached per (from 2 m cell, to 1 m cell, agent, requireFullPath) for CacheSeconds, path list included;
    ///   - budgeted at FrameBudget fresh queries per frame: over it, the call answers from the cache or "no path" and sets
    ///     <see cref="Denied"/> so callers don't remember that answer;
    ///   - counted per caller (sampled) into a "[PathBudget]" line every 30 s.
    /// </summary>
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
    internal static class PathBudget
    {
        private const int FrameBudget = 8;
        private const float CacheSeconds = 4f, LogEvery = 30f;
        private const int SampleEvery = 8;   // caller names from a stack frame on every 8th direct call

        /// <summary>True when the last direct GetPath was refused by the frame budget (the answer is not a real "no path").</summary>
        internal static bool Denied { get; private set; }

        private struct Entry { public bool Ok; public List<Vector3> Path; public float At; }
        private static readonly Dictionary<long, Entry> s_cache = new Dictionary<long, Entry>();
        private static int s_inAI, s_frame = -1, s_frameFresh, s_direct, s_fresh, s_cached, s_denied, s_sample;
        private static float s_logAt;
        private static readonly Dictionary<string, int> s_callers = new Dictionary<string, int>();

        private static long Key(Vector3 from, Vector3 to, Pathfinding.AgentType agent, bool full)
        {
            unchecked
            {
                long h = Mathf.RoundToInt(from.x / 2f) * 73856093L ^ Mathf.RoundToInt(from.y / 2f) * 19349663L ^ Mathf.RoundToInt(from.z / 2f) * 83492791L;
                h = h * 31 + (Mathf.RoundToInt(to.x) * 50331653L ^ Mathf.RoundToInt(to.y) * 12582917L ^ Mathf.RoundToInt(to.z) * 201326611L);
                return h * 31 + (int)agent * 2 + (full ? 1 : 0);
            }
        }

        [HarmonyPatch(typeof(BaseAI), "FindPath", new[] { typeof(Vector3) })]
        private static class FindPathScope
        {
            private static void Prefix() => s_inAI++;
            private static void Finalizer() => s_inAI--;
        }

        [HarmonyPatch(typeof(BaseAI), "HavePath", new[] { typeof(Vector3) })]
        private static class HavePathScope
        {
            private static void Prefix() => s_inAI++;
            private static void Finalizer() => s_inAI--;
        }

        [HarmonyPatch(typeof(Pathfinding), nameof(Pathfinding.GetPath))]
        private static class GetPathBudget
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(Vector3 from, Vector3 to, List<Vector3> path, Pathfinding.AgentType agentType, bool requireFullPath, ref bool __result, out long __state)
            {
                __state = 0;
                Denied = false;
                if (s_inAI > 0) return true;   // vanilla walking: untouched
                if (Time.frameCount != s_frame) { s_frame = Time.frameCount; s_frameFresh = 0; }
                s_direct++;
                if (++s_sample >= SampleEvery) { s_sample = 0; NoteCaller(); }
                MaybeLog();
                long key = Key(from, to, agentType, requireFullPath);
                if (s_cache.TryGetValue(key, out Entry e) && Time.time - e.At < CacheSeconds)
                {
                    s_cached++;
                    if (path != null) { path.Clear(); if (e.Path != null) path.AddRange(e.Path); }
                    __result = e.Ok;
                    return false;
                }
                if (s_frameFresh >= FrameBudget)
                {
                    s_denied++;
                    Denied = true;
                    path?.Clear();
                    __result = false;
                    return false;
                }
                s_frameFresh++;
                s_fresh++;
                __state = key;
                return true;
            }

            private static void Postfix(List<Vector3> path, bool __result, long __state)
            {
                if (__state == 0) return;
                if (s_cache.Count > 512) s_cache.Clear();
                s_cache[__state] = new Entry { Ok = __result, Path = path != null ? new List<Vector3>(path) : null, At = Time.time };
            }
        }

        private static void NoteCaller()
        {
            // frames: 0 NoteCaller, 1 Prefix, 2 Harmony's DMD stub, 3+ the caller; the first frame outside Harmony / this class names it
            var st = new System.Diagnostics.StackTrace(2, false);
            string name = "?";
            for (int i = 0; i < st.FrameCount && i < 6; i++)
            {
                var m = st.GetFrame(i)?.GetMethod();
                if (m == null || m.DeclaringType == null) continue;
                string t = m.DeclaringType.Name;
                if (t.StartsWith("Pathfinding") || t == nameof(PathBudget) || m.Name.Contains("DMD")) continue;
                name = $"{t}.{m.Name}";
                break;
            }
            s_callers[name] = (s_callers.TryGetValue(name, out int n) ? n : 0) + SampleEvery;
        }

        private static void MaybeLog()
        {
            if (Time.time - s_logAt < LogEvery) return;
            float span = s_logAt > 0f ? Time.time - s_logAt : LogEvery;
            s_logAt = Time.time;
            if (s_direct == 0) return;
            var sb = new StringBuilder();
            var list = new List<KeyValuePair<string, int>>(s_callers);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < list.Count && i < 6; i++) sb.Append(i == 0 ? "" : ", ").Append(list[i].Key).Append(" ~").Append(list[i].Value);
            FiresCore.Logging.FiresLogger.LogVerbose($"[PathBudget] {s_direct / span:0.0} direct requests/s ({s_fresh} fresh, {s_cached} cached, {s_denied} over the {FrameBudget}/frame budget) by caller: {sb}");
            s_direct = s_fresh = s_cached = s_denied = 0;
            s_callers.Clear();
        }
    }
}
