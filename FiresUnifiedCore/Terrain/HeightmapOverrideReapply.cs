using System.Collections.Generic;
using FiresCore.Logging;

namespace FiresCore.Terrain
{
    /// <summary>
    /// 0.2.994 ([wishbone]; Fire 10-29: live main-server terrain heights wrong with Core's override, right again with Heightmap
    /// Unlimited): a client builds the heightmaps around it as zones load, and TerrainComp.ApplyToHeightmap clamps with the limits
    /// in force at that moment. Before the server's Core config arrives (<see cref="HeightmapOverrideLimits.ServerAllows"/>) those
    /// are vanilla +/-8 m, and nothing rebuilt those zones once the real limits arrived, so their saved terrain stayed clamped
    /// until the zone reloaded. Whenever the limits in force change (the server's config arrives, or Enabled / Max / Min change
    /// live), every loaded heightmap is rebuilt once with the new limits. No key: it only ever re-applies the server's limits.
    /// </summary>
    internal static class HeightmapOverrideReapply
    {
        private const string Tag = "[HeightmapOverride]";

        // the limits the loaded heightmaps were built with; vanilla until the first change
        private static float s_builtMax = HeightmapOverrideLimits.VanillaClamp, s_builtMin = -HeightmapOverrideLimits.VanillaClamp;
        private static bool s_infoShown;

        internal static void Check(string why)
        {
            float max = HeightmapOverrideLimits.Max(), min = HeightmapOverrideLimits.Min();
            if (max == s_builtMax && min == s_builtMin)
            {
                FiresLogger.LogDebug($"{Tag} limits unchanged ({min}..+{max} m, {why}): loaded heightmaps kept");
                return;
            }
            string was = $"{s_builtMin}..+{s_builtMax}";
            s_builtMax = max; s_builtMin = min;

            List<Heightmap> all = Heightmap.GetAllHeightmaps();
            int n = 0;
            if (all != null)
                foreach (var hm in all)
                {
                    if (hm == null) continue;
                    hm.Poke(1);   // rebuilt in this heightmap's next LateUpdate: TerrainComp.ApplyToHeightmap runs again with the new limits
                    n++;
                }

            FiresLogger.LogDebug($"{Tag} re-applied {n} loaded heightmap(s): limits {was} -> {min}..+{max} m ({why})");
            if (!s_infoShown && n > 0)
            {
                s_infoShown = true;
                FiresLogger.LogInfo($"{Tag} terrain loaded before the server's limits arrived is rebuilt with them ({n} heightmap(s), {min}..+{max} m)");
            }
        }
    }
}
