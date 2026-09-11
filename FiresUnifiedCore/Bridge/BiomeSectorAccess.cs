using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// The one place the family turns a <see cref="Heightmap.Biome"/> into the <see cref="BiomeSector"/>
    /// that Valheim 1.0's environment system now speaks in.
    ///
    /// 1.0 widened the biome from a flag enum to an object: <c>EnvMan.GetBiome()</c> returns a
    /// <c>BiomeSector</c> carrying the biome plus its world extent, neighbours, height range, discovery
    /// state and alt-biome list. Anything that used to force a biome by writing
    /// <c>__result = Heightmap.Biome.X</c> now has to hand back a whole sector - and a hand-built one is
    /// wrong, because downstream code reads those extra fields.
    ///
    /// Vanilla's own lookup is <c>ZNet.World.m_biomeData.Biomes[biome].Sectors[0]</c> (see EnvMan.GetBiome's
    /// Ashlands/DeepNorth override), so that is what this returns: the world's REAL sector for that biome.
    /// Before the world's sectors are generated - or for a biome the world has none of - it falls back to
    /// the <c>BiomeSector.Empty*</c> statics vanilla uses for the same situation.
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
