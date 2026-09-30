using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Pieces
{
    // Build pieces one mod lends to another mod's build menu without either referencing the other. The owner publishes
    // a named group; a borrower lists the group's pieces on its own piece table. Each placement still runs through the
    // owner's own hooks, which recognise their pieces by name whatever table they were picked from.
    public static class SharedPieceGroups
    {
        // FAP's Clutter tab (paint stand-ins and the clutter clear brush), also shown on FAT's Chaos Hammer.
        public const string Clutter = "fires.clutter";

        private static readonly Dictionary<string, Func<IEnumerable<GameObject>>> s_groups =
            new Dictionary<string, Func<IEnumerable<GameObject>>>(StringComparer.Ordinal);
        private static readonly HashSet<string> s_warned = new HashSet<string>(StringComparer.Ordinal);

        public static void Publish(string group, Func<IEnumerable<GameObject>> pieces)
        {
            if (!string.IsNullOrEmpty(group) && pieces != null) s_groups[group] = pieces;
        }

        public static bool IsPublished(string group) => group != null && s_groups.ContainsKey(group);

        // Adds the group's current pieces to `into`, skipping any already there. Returns how many were added.
        public static int Collect(string group, List<GameObject> into)
        {
            if (into == null || group == null || !s_groups.TryGetValue(group, out var pieces)) return 0;
            int before = into.Count;
            try
            {
                foreach (var piece in pieces())
                    if (piece != null && !into.Contains(piece)) into.Add(piece);
            }
            catch (Exception ex)
            {
                if (s_warned.Add(group))
                    Logging.FiresLogger.LogWarning($"[SharedPieceGroups] '{group}' threw ({ex.GetType().Name}: {ex.Message}); it lends nothing.");
            }
            return into.Count - before;
        }

        // The owner can also say which biomes each of its pieces belongs to (FAP: where each clutter grows), so a
        // borrower can sort them by biome without knowing what they are.
        private static readonly Dictionary<string, Func<GameObject, Heightmap.Biome>> s_biomes =
            new Dictionary<string, Func<GameObject, Heightmap.Biome>>(StringComparer.Ordinal);

        public static void PublishBiomes(string group, Func<GameObject, Heightmap.Biome> biomes)
        {
            if (!string.IsNullOrEmpty(group) && biomes != null) s_biomes[group] = biomes;
        }

        // The biomes a lent piece belongs to, or Heightmap.Biome.None when its owner does not say.
        public static Heightmap.Biome BiomesOf(string group, GameObject piece)
        {
            if (piece == null || group == null || !s_biomes.TryGetValue(group, out var biomes)) return Heightmap.Biome.None;
            try { return biomes(piece); }
            catch (Exception ex)
            {
                if (s_warned.Add(group + ".biomes"))
                    Logging.FiresLogger.LogWarning($"[SharedPieceGroups] '{group}' biomes threw ({ex.GetType().Name}: {ex.Message}); its pieces have none.");
                return Heightmap.Biome.None;
            }
        }
    }
}
