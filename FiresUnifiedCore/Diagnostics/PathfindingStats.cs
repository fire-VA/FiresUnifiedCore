using System;
using System.Diagnostics;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// Counts and times vanilla pathfinding (Tools\AI_PATHFINDING_PLAN.md, phase 0) so the planned fixes are judged on
    /// numbers, not guesses. Path queries are split by who asked: a companion or a monster through BaseAI.FindPath or
    /// HavePath, or code calling Pathfinding directly (companion job checks). Navmesh upkeep is the main-thread time of
    /// Pathfinding.Update, with tile rebuilds counted on their own. One status-box line per interval; measurement only.
    /// Attached explicitly from Setup with a read-back count, like CharacterListLeakGuard.
    /// </summary>
    internal static class PathfindingStats
    {
        private const string StatusSource = "Pathfinding";
        private const string LogPrefix = "[PathfindingStats]";
        private const int Direct = 0, Companion = 1, Monster = 2;
        private static readonly string[] AskerNames = { "direct", "companion", "monster" };

        private static BaseAI _asking;
        private static readonly int[] _calls = new int[3];
        private static readonly long[] _ticks = new long[3];
        private static long _worstQueryTicks;
        private static long _updateTicks;
        private static int _tileBuilds;
        private static long _tileBuildTicks;
        private static bool _layersLogged;

        internal static void Register(Harmony harmony)
        {
            var attached = new StringBuilder();
            Patch(harmony, typeof(BaseAI), "FindPath", new[] { typeof(Vector3) }, nameof(Asker_Prefix), nameof(Asker_Finalizer), attached);
            Patch(harmony, typeof(BaseAI), "HavePath", new[] { typeof(Vector3) }, nameof(Asker_Prefix), nameof(Asker_Finalizer), attached);
            Patch(harmony, typeof(Pathfinding), nameof(Pathfinding.GetPath), null, nameof(Timer_Prefix), nameof(GetPath_Finalizer), attached);
            Patch(harmony, typeof(Pathfinding), "Update", null, nameof(Timer_Prefix), nameof(Update_Finalizer), attached);
            Patch(harmony, typeof(Pathfinding), "BuildTile", null, nameof(Timer_Prefix), nameof(BuildTile_Finalizer), attached);
            if (attached.Length > 0) Debug.Log($"{LogPrefix} measuring {attached} (patch counts verified by read-back).");
            Logging.StatusBanner.Register(StatusSource, StatusLine);
        }

        private static void Asker_Prefix(BaseAI __instance, out BaseAI __state)
        {
            __state = _asking;
            _asking = __instance;
        }

        private static void Asker_Finalizer(BaseAI __state) => _asking = __state;

        private static void Timer_Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void GetPath_Finalizer(long __state)
        {
            long elapsed = Stopwatch.GetTimestamp() - __state;
            int asker = _asking == null ? Direct : _asking is Npc.AI.CompanionAI ? Companion : Monster;
            _calls[asker]++;
            _ticks[asker] += elapsed;
            if (elapsed > _worstQueryTicks) _worstQueryTicks = elapsed;
        }

        private static void Update_Finalizer(long __state) => _updateTicks += Stopwatch.GetTimestamp() - __state;

        private static void BuildTile_Finalizer(long __state)
        {
            _tileBuilds++;
            _tileBuildTicks += Stopwatch.GetTimestamp() - __state;
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        /// <summary>Totals since the last line, then reset. Null in menus and whenever nothing pathed.</summary>
        private static string StatusLine()
        {
            LogLayersOnce();
            int queries = _calls[Direct] + _calls[Companion] + _calls[Monster];
            if (queries == 0 && _tileBuilds == 0) return null;

            var line = new StringBuilder("queries");
            for (int asker = Companion; asker <= Monster; asker++)
                line.Append($" {AskerNames[asker]} {_calls[asker]} in {Ms(_ticks[asker]):F1} ms,");
            line.Append($" {AskerNames[Direct]} {_calls[Direct]} in {Ms(_ticks[Direct]):F1} ms (worst {Ms(_worstQueryTicks):F2} ms)");
            line.Append($" | navmesh upkeep {Ms(_updateTicks):F1} ms, {_tileBuilds} tile rebuilds ({Ms(_tileBuildTicks):F1} ms)");

            Array.Clear(_calls, 0, _calls.Length);
            Array.Clear(_ticks, 0, _ticks.Length);
            _worstQueryTicks = _updateTicks = _tileBuildTicks = 0;
            _tileBuilds = 0;
            return line.ToString();
        }

        /// <summary>Which physics layers the navmesh is baked from, to confirm build pieces and doors are in it.</summary>
        private static void LogLayersOnce()
        {
            if (_layersLogged || Pathfinding.instance == null) return;
            _layersLogged = true;
            var names = new StringBuilder();
            int mask = Pathfinding.instance.m_layers.value;
            for (int layer = 0; layer < 32; layer++)
            {
                if ((mask & (1 << layer)) == 0) continue;
                if (names.Length > 0) names.Append(", ");
                names.Append(LayerMask.LayerToName(layer));
            }
            Debug.Log($"{LogPrefix} navmesh built from layers: {names}");
        }

        private static void Patch(Harmony harmony, Type target, string method, Type[] args, string prefix, string finalizer, StringBuilder attached)
        {
            try
            {
                var original = args != null ? AccessTools.Method(target, method, args) : AccessTools.Method(target, method);
                if (original == null)
                {
                    Debug.LogError($"{LogPrefix} {target.Name}.{method} NOT FOUND, not measured.");
                    return;
                }
                harmony.Patch(original,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(PathfindingStats), prefix)),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(PathfindingStats), finalizer)));

                var info = Harmony.GetPatchInfo(original);
                int mine = 0;
                if (info != null)
                {
                    foreach (var patch in info.Prefixes) if (patch.PatchMethod.DeclaringType == typeof(PathfindingStats)) mine++;
                    foreach (var patch in info.Finalizers) if (patch.PatchMethod.DeclaringType == typeof(PathfindingStats)) mine++;
                }
                if (attached.Length > 0) attached.Append(", ");
                attached.Append($"{target.Name}.{method} x{mine}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix} FAILED to attach {target.Name}.{method}: {ex.Message}");
            }
        }
    }
}
