using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Compat.Balrond
{
    /// <summary>
    /// Runs after Expand World Data and BalrondAmazingNature have filled ZoneSystem.m_vegetation, and restricts
    /// MistArea and MistArea_edge to Mistlands, disables vfx_swamp_mist, and scales PoisonGeyser density by the
    /// configured factor. A per-ZoneSystem guard keeps a second SetupLocations call from compounding the scale.
    /// </summary>
    [HarmonyPatch(typeof(ZoneSystem), "SetupLocations")]
    [HarmonyAfter("expand_world_data", "balrond.astafaraios.BalrondAmazingNature")]
    internal static class BalrondVegetationPatch
    {
        private static readonly HashSet<int> _processed = new HashSet<int>();

        private static void Postfix(ZoneSystem __instance)
        {
            try
            {
                if (__instance == null || __instance.m_vegetation == null) return;

                int instanceId = __instance.GetInstanceID();
                if (!_processed.Add(instanceId)) return; // already handled this ZoneSystem instance

                bool restrictMist = BalrondCompatConfig.RestrictMistAreaVegetation != null
                                    && BalrondCompatConfig.RestrictMistAreaVegetation.Value;
                bool removeSwampFog = BalrondCompatConfig.RemoveSwampFog != null
                                      && BalrondCompatConfig.RemoveSwampFog.Value;
                float geyserScale = BalrondCompatConfig.PoisonGeyserDensityScale != null
                                    ? BalrondCompatConfig.PoisonGeyserDensityScale.Value
                                    : 1f;
                bool scaleGeysers = geyserScale < 0.999f;

                int mistFixed = 0, swampFogDisabled = 0, geysersScaled = 0;
                var veg = __instance.m_vegetation;
                for (int i = 0; i < veg.Count; i++)
                {
                    var vegetation = veg[i];
                    if (vegetation == null || vegetation.m_prefab == null) continue;
                    string name = vegetation.m_prefab.name;

                    if (restrictMist && name.StartsWith("MistArea", StringComparison.Ordinal))
                    {
                        var before = vegetation.m_biome;
                        vegetation.m_biome &= Heightmap.Biome.Mistlands; // keep only the Mistlands bit
                        if (vegetation.m_biome != before) mistFixed++;
                        continue;
                    }

                    if (removeSwampFog && string.Equals(name, "vfx_swamp_mist", StringComparison.Ordinal))
                    {
                        if (vegetation.m_enable) { vegetation.m_enable = false; swampFogDisabled++; }
                        continue;
                    }

                    if (scaleGeysers && name.StartsWith("PoisonGeyser", StringComparison.Ordinal))
                    {
                        vegetation.m_min *= geyserScale;
                        vegetation.m_max *= geyserScale;
                        geysersScaled++;
                    }
                }

                if (mistFixed > 0 || swampFogDisabled > 0 || geysersScaled > 0)
                    Debug.Log($"[FiresCompat/Balrond] Vegetation neutralized: MistArea→Mistlands x{mistFixed}, " +
                              $"swampFog disabled x{swampFogDisabled}, PoisonGeyser scaled x{geysersScaled} (x{geyserScale:0.##}).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresCompat/Balrond] Vegetation postfix failed: {ex.Message}");
            }
        }
    }
}
