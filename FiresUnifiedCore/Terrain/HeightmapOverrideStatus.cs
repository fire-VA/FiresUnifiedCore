using System.Collections.Generic;
using FiresCore.Logging;

namespace FiresCore.Terrain
{
    /// <summary>
    /// What each HeightmapOverridePatches transpiler found in Valheim's IL, and the limits in force.
    /// Transpilers record during Harmony patching; the reports run once config and logging exist.
    /// </summary>
    public static class HeightmapOverrideStatus
    {
        private const string Tag = "[HeightmapOverride]";

        public static readonly string[] PatchedMethods = { "LevelTerrain", "RaiseTerrain", "ApplyToHeightmap" };

        private static readonly Dictionary<string, ClampCount> s_found = new Dictionary<string, ClampCount>();

        private readonly struct ClampCount
        {
            public readonly int Lower;
            public readonly int Upper;

            public ClampCount(int lower, int upper)
            {
                Lower = lower;
                Upper = upper;
            }

            public bool IsPaired => IsPairedClamp(Lower, Upper);
        }

        /// <summary>True when the bounds found form complete clamps: at least one, each lower matched by an upper.</summary>
        public static bool IsPairedClamp(int lower, int upper) => lower >= 1 && lower == upper;

        public static void Record(string method, int lower, int upper) => s_found[method] = new ClampCount(lower, upper);

        /// <summary>Logs every patched method's clamp count, and an error for any left vanilla.</summary>
        public static void ReportPatches()
        {
            var patched = new List<string>();
            foreach (var method in PatchedMethods)
                if (ReportMethod(method)) patched.Add($"{method} ({s_found[method].Lower} clamp(s))");

            if (patched.Count > 0)
                FiresLogger.LogInfo($"{Tag} Patched TerrainComp.{string.Join(", ", patched)}.");
        }

        /// <summary>Logs the limits currently in force and why.</summary>
        public static void ReportLimits()
        {
            if (!HeightmapOverrideLimits.IsEnabled())
            {
                FiresLogger.LogInfo($"{Tag} Disabled - vanilla +/-{HeightmapOverrideLimits.VanillaClamp} m.");
                return;
            }

            string suppressor = HeightmapOverrideLimits.ActiveSuppressor();
            if (suppressor != null)
            {
                FiresLogger.LogInfo($"{Tag} Suppressed by {suppressor} - vanilla +/-{HeightmapOverrideLimits.VanillaClamp} m.");
                return;
            }

            FiresLogger.LogInfo($"{Tag} Active - terrain may rise {HeightmapOverrideLimits.Max()} m and sink "
                + $"{HeightmapOverrideLimits.MinAbs()} m from its generated height.");
        }

        private static bool ReportMethod(string method)
        {
            if (!s_found.TryGetValue(method, out var count))
            {
                FiresLogger.LogError($"{Tag} TerrainComp.{method} was never transpiled - its height limit is NOT applied.");
                return false;
            }

            if (count.IsPaired) return true;

            FiresLogger.LogError($"{Tag} TerrainComp.{method}: expected paired +/-{HeightmapOverrideLimits.VanillaClamp} m "
                + $"bounds, found {count.Lower} lower / {count.Upper} upper. Left VANILLA. Valheim's terrain code has "
                + "changed, or another mod (HeightmapUnlimited?) already replaced these bounds.");
            return false;
        }
    }
}
