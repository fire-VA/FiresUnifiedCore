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

        public static readonly string[] PatchedMethods = { "LevelTerrain", "RaiseTerrain", "ApplyToHeightmap", HeightmapLevelTerrain };

        // 0.2.971: Heightmap.LevelTerrain (TerrainModifier / location proxy levelling, every heightmap rebuild) has its own +/-8 m clamp
        internal const string HeightmapLevelTerrain = "Heightmap.LevelTerrain";
        private static System.Type TypeOf(string method) => method == HeightmapLevelTerrain ? typeof(Heightmap) : typeof(TerrainComp);
        private static string NameOf(string method) => method == HeightmapLevelTerrain ? "LevelTerrain" : method;

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
                if (ReportMethod(method)) patched.Add($"{(method == HeightmapLevelTerrain ? method : "TerrainComp." + method)} ({s_found[method].Lower} clamp(s))");

            if (patched.Count > 0)
                FiresLogger.LogDebug($"{Tag} Patched {string.Join(", ", patched)}.");
        }

        /// <summary>Logs the limits currently in force and why.</summary>
        public static void ReportLimits()
        {
            if (!HeightmapOverrideLimits.IsEnabled())
            {
                FiresLogger.LogInfo($"{Tag} Disabled - vanilla +/-{HeightmapOverrideLimits.VanillaClamp} m.");
                return;
            }

            if (!HeightmapOverrideLimits.ServerAllows())
            {
                FiresLogger.LogInfo($"{Tag} Enabled here, but the server has not sent Core's config (a vanilla server, or not synced yet) - vanilla +/-{HeightmapOverrideLimits.VanillaClamp} m until it does.");
                return;
            }

            string suppressor = HeightmapOverrideLimits.ActiveSuppressor();
            if (suppressor != null)
            {
                FiresLogger.LogInfo($"{Tag} Suppressed by {suppressor} - vanilla +/-{HeightmapOverrideLimits.VanillaClamp} m.");
                return;
            }

            FiresLogger.LogDebug($"{Tag} Active - terrain may rise {HeightmapOverrideLimits.Max()} m and sink "
                + $"{HeightmapOverrideLimits.MinAbs()} m from its generated height.");
        }

        // A warning that says why, once per method. Nothing
        // breaks either way: the method stays vanilla, so terrain keeps vanilla's +/-8 m limit from its generated height there.
        private static bool ReportMethod(string method)
        {
            if (!s_found.TryGetValue(method, out var count))
            {
                FiresLogger.LogWarning($"{Tag} {(method == HeightmapLevelTerrain ? method : "TerrainComp." + method)} not patched ({WhyNotTranspiled(method)}); "
                    + $"terrain keeps vanilla's +/-{HeightmapOverrideLimits.VanillaClamp} m height limit there.");
                return false;
            }

            if (count.IsPaired) return true;

            FiresLogger.LogWarning($"{Tag} {(method == HeightmapLevelTerrain ? method : "TerrainComp." + method)}: expected paired +/-{HeightmapOverrideLimits.VanillaClamp} m "
                + $"bounds, found {count.Lower} lower / {count.Upper} upper. Left VANILLA (+/-{HeightmapOverrideLimits.VanillaClamp} m). "
                + $"Valheim's terrain code has changed, or another mod already replaced these bounds{OtherPatchers(method)}.");
            return false;
        }

        // Why a transpiler never ran: the method missing or overloaded in this Valheim build, or its patch class failing to attach
        // (FiresMod logs "Harmony patch class '...' failed to attach: <reason>" for that), with whoever else patches the method.
        private static string WhyNotTranspiled(string method)
        {
            System.Reflection.MethodInfo original;
            try { original = HarmonyLib.AccessTools.DeclaredMethod(TypeOf(method), NameOf(method)); }
            catch (System.Reflection.AmbiguousMatchException) { return $"this Valheim build has more than one {TypeOf(method).Name}.{NameOf(method)}, so the patch could not pick one"; }
            if (original == null) return $"this Valheim build has no {TypeOf(method).Name}.{NameOf(method)}";
            return $"its patch class did not attach - look for \"Harmony patch class 'FiresCore.Terrain.HeightmapOverridePatches+{(method == HeightmapLevelTerrain ? "HeightmapLevelTerrain" : method)}Patch' failed to attach\" "
                + $"above{OtherPatchers(method)}";
        }

        // ", other patches there: Owner (prefix), Owner (transpiler)" for every patch on TerrainComp.method not Core's own; "" when none.
        private static string OtherPatchers(string method)
        {
            try
            {
                var original = HarmonyLib.AccessTools.DeclaredMethod(TypeOf(method), NameOf(method));
                var info = original != null ? HarmonyLib.Harmony.GetPatchInfo(original) : null;
                if (info == null) return "";
                var others = new List<string>();
                void Add(IEnumerable<HarmonyLib.Patch> patches, string kind)
                {
                    foreach (var p in patches)
                        if (p.owner != global::FiresCore.FiresUnifiedCore.PluginGUID) others.Add($"{p.owner} ({kind})");
                }
                Add(info.Prefixes, "prefix");
                Add(info.Transpilers, "transpiler");
                Add(info.Postfixes, "postfix");
                Add(info.Finalizers, "finalizer");
                return others.Count == 0 ? "" : $"; other patches on it: {string.Join(", ", others)}";
            }
            catch (System.Exception) { return ""; }
        }
    }
}
