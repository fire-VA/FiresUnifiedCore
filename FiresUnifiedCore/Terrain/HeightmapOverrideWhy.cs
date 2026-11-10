using System.Collections.Generic;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Terrain
{
    /// <summary>
    /// 0.2.971 ([lead]/Fire 10-28, Arbby: terrain lowered past 8 m looked right, then came back wrong after the area reloaded): the
    /// Debug why-lines that prove where heights beyond vanilla's +/-8 m go.
    /// <para>TerrainComp.Load: per terrain zone load, the saved level delta min / max and how many cells are past +/-8 m (the stored
    /// data, before any clamp), with the limits in force. Heightmap.LevelTerrain (TerrainModifier / location proxy levelling, every
    /// heightmap rebuild): the requested height vs the generated height at the modifier's centre, and whether the limits in force
    /// would clamp it; summed and logged at most every 2 s.</para>
    /// </summary>
    internal static class HeightmapOverrideWhy
    {
        private const string Tag = "[HeightmapOverride]";
        private static int s_levelCalls, s_levelBeyond8, s_levelClamped;
        private static float s_levelWorst;
        private static float s_nextLevelLine;
        private static readonly HashSet<Vector2s> s_zoneLogged = new HashSet<Vector2s>();

        private static string Limits() => HeightmapOverrideLimits.IsActive()
            ? $"active {HeightmapOverrideLimits.Min():0.#}..+{HeightmapOverrideLimits.Max():0.#} m"
            : $"vanilla +/-{HeightmapOverrideLimits.VanillaClamp} m ({(!HeightmapOverrideLimits.IsEnabled() ? "Enabled off" : !HeightmapOverrideLimits.ServerAllows() ? "server has not sent Core's config" : "suppressed by " + HeightmapOverrideLimits.ActiveSuppressor())})";

        [HarmonyPatch(typeof(TerrainComp), "Load")]
        private static class TerrainCompLoad
        {
            private static void Postfix(TerrainComp __instance, bool __result)
            {
                if (!__result || __instance == null || __instance.m_levelDelta == null || __instance.m_modifiedHeight == null) return;
                if (!FiresLogger.DebugEnabled) return;
                var zone = ZoneSystem.GetZone(__instance.transform.position);
                float min = 0f, max = 0f; int cells = 0, beyond = 0;
                for (int i = 0; i < __instance.m_levelDelta.Length; i++)
                {
                    if (!__instance.m_modifiedHeight[i]) continue;
                    float d = __instance.m_levelDelta[i] + __instance.m_smoothDelta[i];
                    cells++;
                    if (d < min) min = d; if (d > max) max = d;
                    if (d < -HeightmapOverrideLimits.VanillaClamp || d > HeightmapOverrideLimits.VanillaClamp) beyond++;
                }
                if (cells == 0) return;
                bool first = s_zoneLogged.Add(zone);
                if (!first && beyond == 0) return;   // every load of a zone with heights past 8 m; others once
                FiresLogger.LogDebug($"{Tag} zone {zone.x},{zone.y} loaded: saved terrain delta {min:0.00}..{max:0.00} m over {cells} edited cell(s), "
                    + $"{beyond} past +/-{HeightmapOverrideLimits.VanillaClamp} m; limits {Limits()}"
                    + (beyond > 0 && !HeightmapOverrideLimits.IsActive() ? " - those cells are drawn clamped to +/-8 m here" : ""));
            }
        }

        [HarmonyPatch(typeof(Heightmap), "LevelTerrain")]
        private static class HeightmapLevelTerrain
        {
            private static void Prefix(Heightmap __instance, Vector3 worldPos, float[] baseHeights, bool playerModifiction)
            {
                if (!playerModifiction || baseHeights == null || __instance == null || !FiresLogger.DebugEnabled) return;
                __instance.WorldToVertexMask(worldPos, out int x, out int y);
                int w = __instance.m_width + 1, i = y * w + x;
                if (x < 0 || y < 0 || x >= w || y >= w || i >= baseHeights.Length) return;
                float d = worldPos.y - __instance.transform.position.y - baseHeights[i];
                s_levelCalls++;
                if (Mathf.Abs(d) > HeightmapOverrideLimits.VanillaClamp) s_levelBeyond8++;
                if (d > HeightmapOverrideLimits.Max() || d < HeightmapOverrideLimits.Min()) s_levelClamped++;
                if (Mathf.Abs(d) > Mathf.Abs(s_levelWorst)) s_levelWorst = d;
                float now = Time.realtimeSinceStartup;
                if (now < s_nextLevelLine) return;
                s_nextLevelLine = now + 2f;
                if (s_levelBeyond8 > 0)
                    FiresLogger.LogDebug($"{Tag} terrain modifier levelling (Heightmap.LevelTerrain, every heightmap rebuild): {s_levelCalls} call(s), "
                        + $"{s_levelBeyond8} asking past +/-{HeightmapOverrideLimits.VanillaClamp} m (worst {s_levelWorst:0.00} m from generated), "
                        + $"{s_levelClamped} clamped by the limits in force ({Limits()})");
                s_levelCalls = s_levelBeyond8 = s_levelClamped = 0; s_levelWorst = 0f;
            }
        }
    }
}
