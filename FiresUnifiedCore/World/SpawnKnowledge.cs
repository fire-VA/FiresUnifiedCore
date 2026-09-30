using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>One way an item comes out of the world: the vegetation entry that places a source, and what the source yields.</summary>
    public readonly struct SpawnRule
    {
        /// <summary>The source prefab, e.g. "Pickable_Flint".</summary>
        public readonly string Source;
        /// <summary>Pickable, Rock, Tree or Drop.</summary>
        public readonly string Kind;
        public readonly Heightmap.Biome Biome;
        public readonly Heightmap.BiomeArea Area;
        /// <summary>Altitude band (m above the water level), ocean depth band (0/0 = not checked), ground tilt band (degrees).</summary>
        public readonly float MinAltitude, MaxAltitude, MinOceanDepth, MaxOceanDepth, MinTilt, MaxTilt;
        /// <summary>Placed only in forest (the world generator's forest factor between the two).</summary>
        public readonly bool InForest;
        public readonly float ForestMin, ForestMax;
        /// <summary>How many of the item one source gives (the drop's minimum, at least 1).</summary>
        public readonly int PerItem;

        public SpawnRule(string source, string kind, ZoneSystem.ZoneVegetation v, int perItem)
        {
            Source = source;
            Kind = kind;
            Biome = v.m_biome;
            Area = v.m_biomeArea;
            MinAltitude = v.m_minAltitude;
            MaxAltitude = v.m_maxAltitude;
            MinOceanDepth = v.m_minOceanDepth;
            MaxOceanDepth = v.m_maxOceanDepth;
            MinTilt = v.m_minTilt;
            MaxTilt = v.m_maxTilt;
            InForest = v.m_inForest;
            ForestMin = v.m_forestTresholdMin;
            ForestMax = v.m_forestTresholdMax;
            PerItem = Mathf.Max(1, perItem);
        }

        public string Describe() =>
            $"{Source} ({Biome}, altitude {MinAltitude:0}-{MaxAltitude:0} m" +
            (MinOceanDepth != MaxOceanDepth ? $", water {MinOceanDepth:0}-{MaxOceanDepth:0} m deep" : "") +
            (InForest ? $", forest {ForestMin:0.##}-{ForestMax:0.##}" : "") +
            (MinTilt > 0f || MaxTilt < 90f ? $", tilt {MinTilt:0}-{MaxTilt:0}°" : "") + ")";
    }

    /// <summary>A place where an item's source can spawn, from the world generator (unloaded ground included).</summary>
    public readonly struct SpawnCandidate
    {
        public readonly Vector3 At;
        public readonly Heightmap.Biome Biome;
        public readonly float Distance;
        /// <summary>0..1: how well the place fits the rule (1 = the middle of its bands).</summary>
        public readonly float Score;
        /// <summary>"Pickable_Flint: Meadows, altitude 0-1 m (shore), 140 m N".</summary>
        public readonly string Why;

        public SpawnCandidate(Vector3 at, Heightmap.Biome biome, float distance, float score, string why)
        {
            At = at;
            Biome = biome;
            Distance = distance;
            Score = score;
            Why = why;
        }
    }

    /// <summary>
    /// Where things spawn, from the game's own data only (Core 0.2.203, AUTOPLAY_PROGRESSION §7.14; Fire: "it needs to be able to
    /// know how to go looking for them"). The rules per item come from ZoneSystem.m_vegetation (biome, altitude, ocean depth, tilt,
    /// forest) joined to what each placed prefab yields: a Pickable's item, a rock's drops, a tree's logs, a destructible's drops.
    /// <see cref="Candidates"/> samples the world generator on a <see cref="CellSize"/> m grid round a point, loaded or not, and
    /// returns the cells that fit, nearest first. One brain: the bot's planner and the companions ask the same.
    /// </summary>
    public static class SpawnKnowledge
    {
        /// <summary>The grid the candidates are sampled on (m); the explorer's visited-cells map uses the same.</summary>
        public const float CellSize = 16f;
        /// <summary>The widest search (m): larger radii are capped (the sampling is on the main thread).</summary>
        public const float MaxRadius = 1000f;

        private static Dictionary<string, List<SpawnRule>> s_rules;
        private static readonly HashSet<string> s_logged = new HashSet<string>();
        private static readonly List<SpawnRule> s_none = new List<SpawnRule>();

        /// <summary>The spawn rules that yield <paramref name="itemPrefab"/> (an ObjectDB item prefab name, e.g. "Flint"); empty when none.</summary>
        public static IReadOnlyList<SpawnRule> RulesFor(string itemPrefab)
        {
            if (string.IsNullOrEmpty(itemPrefab) || !Build()) return s_none;
            return s_rules.TryGetValue(itemPrefab, out List<SpawnRule> rules) ? rules : s_none;
        }

        /// <summary>
        /// Cells within <paramref name="radius"/> of <paramref name="from"/> where a source of <paramref name="itemPrefab"/> can spawn,
        /// best first (distance weighted by fit), at most <paramref name="max"/>. <paramref name="biomeAllowed"/> filters biomes
        /// (the gear-tier rule); <paramref name="skipCell"/> skips cells already searched (the explorer's "visited, nothing found").
        /// </summary>
        public static List<SpawnCandidate> Candidates(string itemPrefab, Vector3 from, float radius, int max = 10,
            Func<Heightmap.Biome, bool> biomeAllowed = null, Func<Vector3, bool> skipCell = null)
        {
            var found = new List<SpawnCandidate>();
            IReadOnlyList<SpawnRule> rules = RulesFor(itemPrefab);
            WorldGenerator world = WorldGenerator.instance;
            if (rules.Count == 0 || world == null)
            {
                LogOnce(itemPrefab, rules, 0, radius, -1f);
                return found;
            }
            radius = Mathf.Min(radius, MaxRadius);
            float water = ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f;
            int steps = Mathf.CeilToInt(radius / CellSize);
            float cx = Mathf.Round(from.x / CellSize) * CellSize, cz = Mathf.Round(from.z / CellSize) * CellSize;
            for (int iz = -steps; iz <= steps; iz++)
            for (int ix = -steps; ix <= steps; ix++)
            {
                float x = cx + ix * CellSize, z = cz + iz * CellSize;
                float dx = x - from.x, dz = z - from.z;
                float distance = Mathf.Sqrt(dx * dx + dz * dz);
                if (distance > radius) continue;
                Heightmap.Biome biome = world.GetBiome(x, z);
                if (biomeAllowed != null && !biomeAllowed(biome)) continue;
                SpawnRule? match = null;
                foreach (SpawnRule r in rules) if ((r.Biome & biome) != 0) { match = r; break; }
                if (match == null) continue;
                float height = world.GetHeight(x, z);
                var at = new Vector3(x, height, z);
                if (skipCell != null && skipCell(at)) continue;
                float best = -1f;
                SpawnRule bestRule = default;
                foreach (SpawnRule r in rules)
                {
                    if ((r.Biome & biome) == 0) continue;
                    float score = Fit(world, r, at, water);
                    if (score > best) { best = score; bestRule = r; }
                }
                if (best < 0f) continue;
                string why = $"{bestRule.Source}: {biome}, altitude {bestRule.MinAltitude:0}-{bestRule.MaxAltitude:0} m" +
                             $"{(bestRule.InForest ? ", in forest" : "")}, {distance:0} m {Compass(dx, dz)}";
                found.Add(new SpawnCandidate(at, biome, distance, best, why));
            }
            found.Sort((a, b) => (a.Distance * (2f - a.Score)).CompareTo(b.Distance * (2f - b.Score)));
            int total = found.Count;
            if (found.Count > max) found.RemoveRange(max, found.Count - max);
            LogOnce(itemPrefab, rules, total, radius, found.Count > 0 ? found[0].Distance : -1f);
            return found;
        }

        // 0..1 how well the place fits the rule, or -1 when it doesn't.
        private static float Fit(WorldGenerator world, SpawnRule r, Vector3 at, float water)
        {
            float altitude = at.y - water;
            if (altitude < r.MinAltitude || altitude > r.MaxAltitude) return -1f;
            if (r.MinOceanDepth != r.MaxOceanDepth)
            {
                float depth = water - at.y;
                if (depth < r.MinOceanDepth || depth > r.MaxOceanDepth) return -1f;
            }
            if ((r.Area & world.GetBiomeArea(at)) == 0) return -1f;
            if (r.MinTilt > 0f || r.MaxTilt < 90f)
            {
                float hx = world.GetHeight(at.x + 2f, at.z) - world.GetHeight(at.x - 2f, at.z);
                float hz = world.GetHeight(at.x, at.z + 2f) - world.GetHeight(at.x, at.z - 2f);
                float tilt = Mathf.Atan(Mathf.Sqrt(hx * hx + hz * hz) / 4f) * Mathf.Rad2Deg;
                if (tilt < r.MinTilt || tilt > r.MaxTilt) return -1f;
            }
            float score = 1f;
            if (r.InForest)
            {
                float forest = WorldGenerator.GetForestFactor(at);
                if (forest < r.ForestMin || forest > r.ForestMax) return -1f;
            }
            // Nearer the middle of a narrow altitude band fits better (a shoreline band of 1 m is easy to miss by a few metres).
            float half = (r.MaxAltitude - r.MinAltitude) * 0.5f;
            if (half < 50f) score -= 0.3f * Mathf.Clamp01(Mathf.Abs(altitude - (r.MinAltitude + half)) / Mathf.Max(half, 0.5f));
            return Mathf.Clamp01(score);
        }

        private static string Compass(float dx, float dz)
        {
            float a = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
            string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            return names[Mathf.RoundToInt(Mathf.Repeat(a, 360f) / 45f) % 8];
        }

        private static void LogOnce(string item, IReadOnlyList<SpawnRule> rules, int total, float radius, float nearest)
        {
            if (!s_logged.Add(item ?? "?")) return;
            var sources = new List<string>();
            foreach (SpawnRule r in rules) sources.Add(r.Describe());
            Debug.Log($"[SpawnKnowledge] {item}: {rules.Count} source(s){(rules.Count > 0 ? ": " + string.Join(", ", sources) : " in the world's vegetation")}; " +
                      $"{total} candidate cell(s) within {radius:0} m{(nearest >= 0f ? $", nearest {nearest:0} m" : "")}");
        }

        // ---- rules from the game's data ----

        private static bool Build()
        {
            if (s_rules != null) return true;
            if (ZoneSystem.instance == null || ZoneSystem.instance.m_vegetation == null || ObjectDB.instance == null) return false;
            s_rules = new Dictionary<string, List<SpawnRule>>();
            foreach (ZoneSystem.ZoneVegetation v in ZoneSystem.instance.m_vegetation)
            {
                if (v == null || !v.m_enable || v.m_prefab == null) continue;
                string source = v.m_prefab.name;
                foreach (var (item, count, kind) in Yields(v.m_prefab, 0))
                {
                    if (!s_rules.TryGetValue(item, out List<SpawnRule> list)) s_rules[item] = list = new List<SpawnRule>();
                    list.Add(new SpawnRule(source, kind, v, count));
                }
            }
            Debug.Log($"[SpawnKnowledge] rules built: {s_rules.Count} item(s) from {ZoneSystem.instance.m_vegetation.Count} vegetation entries");
            return true;
        }

        // What a placed prefab yields: (item prefab name, amount, kind). Trees and destructibles follow their log / spawn prefab once more.
        private static IEnumerable<(string, int, string)> Yields(GameObject prefab, int depth)
        {
            if (prefab == null || depth > 2) yield break;
            var pickable = prefab.GetComponentInChildren<Pickable>(true);
            if (pickable != null)
            {
                if (pickable.m_itemPrefab != null) yield return (pickable.m_itemPrefab.name, pickable.m_amount, "Pickable");
                foreach (var d in Drops(pickable.m_extraDrops)) yield return (d.Item1, d.Item2, "Pickable");
            }
            var mine = prefab.GetComponentInChildren<MineRock>(true);
            if (mine != null) foreach (var d in Drops(mine.m_dropItems)) yield return (d.Item1, d.Item2, "Rock");
            var mine5 = prefab.GetComponentInChildren<MineRock5>(true);
            if (mine5 != null) foreach (var d in Drops(mine5.m_dropItems)) yield return (d.Item1, d.Item2, "Rock");
            var tree = prefab.GetComponentInChildren<TreeBase>(true);
            if (tree != null)
            {
                foreach (var d in Drops(tree.m_dropWhenDestroyed)) yield return (d.Item1, d.Item2, "Tree");
                foreach (var y in Yields(tree.m_logPrefab, depth + 1)) yield return (y.Item1, y.Item2, "Tree");
            }
            var log = prefab.GetComponentInChildren<TreeLog>(true);
            if (log != null)
            {
                foreach (var d in Drops(log.m_dropWhenDestroyed)) yield return (d.Item1, d.Item2, "Tree");
                foreach (var y in Yields(log.m_subLogPrefab, depth + 1)) yield return (y.Item1, y.Item2, "Tree");
            }
            var dropper = prefab.GetComponentInChildren<DropOnDestroyed>(true);
            if (dropper != null) foreach (var d in Drops(dropper.m_dropWhenDestroyed)) yield return (d.Item1, d.Item2, "Drop");
            var destructible = prefab.GetComponentInChildren<Destructible>(true);
            if (destructible != null && destructible.m_spawnWhenDestroyed != null && destructible.m_spawnWhenDestroyed != prefab)
                foreach (var y in Yields(destructible.m_spawnWhenDestroyed, depth + 1)) yield return y;
        }

        private static IEnumerable<(string, int)> Drops(DropTable table)
        {
            if (table?.m_drops == null) yield break;
            foreach (DropTable.DropData d in table.m_drops)
                if (d.m_item != null) yield return (d.m_item.name, Mathf.Max(1, d.m_stackMin));
        }
    }
}
