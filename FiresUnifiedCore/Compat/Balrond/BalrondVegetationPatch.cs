using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Compat.Balrond
{
    /// <summary>
    /// Neutralizes three placement-level Balrond/Expand-World annoyances by editing
    /// <c>ZoneSystem.m_vegetation</c> after both Expand World Data and BalrondAmazingNature have
    /// finished populating it (we run as a <c>[HarmonyAfter]</c> postfix on
    /// <see cref="ZoneSystem.SetupLocations"/>, which is the same point Balrond's own vegetation edits
    /// land):
    ///
    /// <list type="bullet">
    /// <item><description><b>MistArea / MistArea_edge</b> — the mistlands mist volumes are force-placed
    ///   across Mountain / BlackForest / Swamp / Plains by the shipped Expand World config. We AND their
    ///   biome mask down to Mistlands only, so the mist stops appearing everywhere.</description></item>
    /// <item><description><b>vfx_swamp_mist</b> — the new swamp ground-fog vegetation is disabled
    ///   outright.</description></item>
    /// <item><description><b>PoisonGeyser*</b> — placement density (m_min / m_max) is scaled by the
    ///   configured factor (default 0.5).</description></item>
    /// </list>
    ///
    /// The biome-clear and disable are idempotent, but the density scale is not, so a per-ZoneSystem
    /// instance guard prevents a second SetupLocations call from compounding the multiplier.
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
                    var v = veg[i];
                    if (v == null || v.m_prefab == null) continue;
                    string name = v.m_prefab.name;

                    if (restrictMist && name.StartsWith("MistArea", StringComparison.Ordinal))
                    {
                        var before = v.m_biome;
                        v.m_biome &= Heightmap.Biome.Mistlands; // keep only the Mistlands bit
                        if (v.m_biome != before) mistFixed++;
                        continue;
                    }

                    if (removeSwampFog && string.Equals(name, "vfx_swamp_mist", StringComparison.Ordinal))
                    {
                        if (v.m_enable) { v.m_enable = false; swampFogDisabled++; }
                        continue;
                    }

                    if (scaleGeysers && name.StartsWith("PoisonGeyser", StringComparison.Ordinal))
                    {
                        v.m_min *= geyserScale;
                        v.m_max *= geyserScale;
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
