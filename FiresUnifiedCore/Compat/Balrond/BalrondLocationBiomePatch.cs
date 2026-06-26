using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Compat.Balrond
{
    /// <summary>
    /// Reverts BalrondAmazingNature's <see cref="ZoneSystem.SetupLocations"/> postfix that OR-adds
    /// <c>Heightmap.Biome.DeepNorth | Heightmap.Biome.AshLands</c> to every Mistlands location's
    /// allowed-biome filter (see <c>BalrondNature.LocationBuilder.editLocation</c>). The visible
    /// symptom of that change is mistlands fog appearing in DeepNorth and Ashlands, because the
    /// vanilla Mistlands location prefabs carry <c>MistEmitter</c> child GameObjects that fire
    /// regardless of host biome.
    ///
    /// Strategy: run as a <c>[HarmonyAfter]</c> postfix so we land AFTER Balrond's postfix has
    /// finished mutating <c>ZoneSystem.m_locations</c>. For each location whose prefab name is on the
    /// known mist-bearing list, mask off the DeepNorth and Ashlands bits without touching the
    /// vanilla Mistlands bit — the locations stay spawnable in Mistlands, just nowhere else.
    /// </summary>
    [HarmonyPatch(typeof(ZoneSystem), "SetupLocations")]
    [HarmonyAfter("balrond.astafaraios.BalrondAmazingNature")]
    internal static class BalrondLocationBiomePatch
    {
        // Sourced verbatim from BalrondNature.LocationBuilder.deepNorthNames (the names Balrond
        // adds DeepNorth/AshLands to). Only the Mistlands_* entries are listed here — the other
        // entries in Balrond's list are pre-existing non-Mistlands locations whose biome expansion
        // doesn't bring mist with them.
        private static readonly HashSet<string> MistBearingLocations = new HashSet<string>(StringComparer.Ordinal)
        {
            "Mistlands_Swords1",
            "Mistlands_Swords2",
            "Mistlands_Swords3",
            "Mistlands_Viaduct1",
            "Mistlands_Viaduct2",
            "Mistlands_Lighthouse1_new",
            "Mistlands_GuardTower1_new",
            "Mistlands_GuardTower2_new",
            "Mistlands_GuardTower3_new",
            "Mistlands_RockSpire1",
            "Mistlands_RoadPost1",
            "Mistlands_Giant1",
            "Mistlands_Giant2",
            "Mistlands_Harbour1",
        };

        private const Heightmap.Biome BiomesToStrip = Heightmap.Biome.DeepNorth | Heightmap.Biome.AshLands;

        private static void Postfix(ZoneSystem __instance)
        {
            try
            {
                if (BalrondCompatConfig.RestrictMistlandsLocations == null
                    || !BalrondCompatConfig.RestrictMistlandsLocations.Value) return;
                if (__instance == null || __instance.m_locations == null) return;

                int restored = 0;
                var locs = __instance.m_locations;
                for (int i = 0; i < locs.Count; i++)
                {
                    var loc = locs[i];
                    if (loc == null) continue;
                    if (!MistBearingLocations.Contains(loc.m_prefabName)) continue;
                    var before = loc.m_biome;
                    loc.m_biome &= ~BiomesToStrip;
                    if (loc.m_biome != before) restored++;
                }

                if (restored > 0)
                    Debug.Log($"[FiresCompat/Balrond] Restored {restored} Mistlands location(s) to Mistlands-only biome (stripped DeepNorth/AshLands).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresCompat/Balrond] Postfix failed: {ex.Message}");
            }
        }
    }
}
