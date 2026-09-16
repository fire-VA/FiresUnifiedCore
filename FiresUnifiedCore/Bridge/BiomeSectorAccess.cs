using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Turns a <see cref="Heightmap.Biome"/> into the <see cref="BiomeSector"/> Valheim 1.0's environment code expects.
    /// Downstream code reads a sector's extent, neighbours and discovery state, so a hand-built one is wrong; this
    /// returns the world's real sector, as vanilla's own EnvMan.GetBiome override does, and falls back to vanilla's
    /// BiomeSector.Empty statics before sectors exist or when the world has none for that biome.
    /// </summary>
    public static class BiomeSectorAccess
    {
        public static BiomeSector For(Heightmap.Biome biome)
        {
            var data = ZNet.World != null ? ZNet.World.m_biomeData : null;
            if (data != null && data.Biomes != null)
            {
                BiomeTypeInfo info;
                if (data.Biomes.TryGetValue(biome, out info) && info != null && info.Sectors != null && info.Sectors.Count > 0)
                    return info.Sectors[0];
            }

            switch (biome)
            {
                case Heightmap.Biome.Meadows:     return BiomeSector.EmptyMeadows;
                case Heightmap.Biome.BlackForest: return BiomeSector.EmptyBlackForest;
                case Heightmap.Biome.DeepNorth:   return BiomeSector.EmptyEdge;
                case Heightmap.Biome.None:        return BiomeSector.Empty;
                default:                          return BiomeSector.EmptyMeadows;
            }
        }

        /// <summary>The biome a sector stands for, or None when there is no sector.</summary>
        public static Heightmap.Biome BiomeOf(BiomeSector sector)
            => sector != null ? sector.Biome : Heightmap.Biome.None;
    }
}
