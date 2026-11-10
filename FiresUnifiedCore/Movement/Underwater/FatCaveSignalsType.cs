using System;
using FiresCore.Compat;
using FiresCore.Logging;

namespace FiresCore.Movement.Underwater
{
    /// <summary>
    /// 0.2.1207 ([wishbone], [classes-ui] report from Fire's BlueHills hand test without FAT): FAT's
    /// VerdantsAscent.Terrain.Voxel.FatCaveSignals for the underwater code (WaterSurface.TrueY / CaveFactor, SubmersionState), found
    /// without HarmonyLib.AccessTools.TypeByName (which logs a HarmonyX Warning players see when FAT is missing). Resolved once; when FAT
    /// is not installed it logs one Info line and every caller skips its member lookups.
    /// 0.2.1208: the scan is Core's one silent lookup, Compat.OptionalTypes.Find (1207 had its own copy of the same loop).
    /// </summary>
    internal static class FatCaveSignalsType
    {
        private const string TypeName = "VerdantsAscent.Terrain.Voxel.FatCaveSignals";
        private static bool s_resolved;
        private static Type s_type;

        /// <summary>FAT's FatCaveSignals type, or null when FAT is not installed.</summary>
        public static Type Get()
        {
            if (s_resolved) return s_type;
            s_resolved = true;
            s_type = OptionalTypes.Find(TypeName);
            if (s_type == null)
                FiresLogger.LogInfo("[Underwater] FAT not installed: underwater cave checks stand down (no FAT inland water surface, cave factor or shaft test; sea + vanilla water level only)");
            return s_type;
        }
    }
}
