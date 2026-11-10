using System;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>
    /// Alt biome display (ForceBiome areas showed the old biome before). The one biome name Core's
    /// HUD shows (the minimap plate title, the data pill): the player's biome (Player.GetCurrentBiome, which RPGmaker's ForceBiome reports
    /// since 0.0.744), followed by " - <alt>" when the current environment sector of that same biome carries a vanilla 1.0 alt biome
    /// (EnvMan's current BiomeSector; RPGmaker's forced sectors carry theirs since 0.0.745).
    /// </summary>
    internal static class BiomeDisplay
    {
        internal static string CurrentName(Player p)
        {
            if (p == null) return "";
            var biome = p.GetCurrentBiome();
            string name = Localization.instance != null ? Localization.instance.Localize("$biome_" + biome.ToString().ToLowerInvariant()) : biome.ToString();
            try
            {
                var sector = EnvMan.instance != null ? EnvMan.instance.GetBiome() : null;
                if (sector != null && sector.AltBiomes != null && sector.AltBiomes.Count > 0)
                {
                    foreach (var alt in sector.AltBiomes)
                    {
                        if (alt == null || string.IsNullOrEmpty(alt.m_name) || alt.m_biome != biome) continue;
                        string altName = !string.IsNullOrEmpty(alt.m_nameOverride) ? alt.m_nameOverride : alt.m_name;
                        if (Localization.instance != null) altName = Localization.instance.Localize(altName);
                        return altName;   // The alt name alone
                    }
                }
            }
            catch { }
            return name;
        }
    }
}
