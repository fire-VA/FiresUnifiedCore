using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using FiresCore.Logging;

namespace FiresCore.Npc.Races
{
    /// <summary>
    /// Companion races (Tools\COMPANION_RACES_DESIGN.md, Fire 10-13): a wild companion rolls a race once, on the server, when it is first
    /// dressed; the race is stored in the "companion_race" ZDO string and only changes the companion's look and small traits. Faction,
    /// AI and targeting never read it. Existing companions (empty key) count as Human and are never rolled.
    /// </summary>
    public static class CompanionRaces
    {
        public const string ZdoKey = "companion_race";
        public const string Human = "Human";

        /// <summary>Every race the code knows. A race is offered only when it is also in [Companions.Races] Allowed races.</summary>
        public static readonly string[] Known = { Human, "Dvergr", "Draugr", "Skeleton", "Goblin", "Greyling" };

        private static ConfigEntry<string> s_allowed;
        private static ConfigEntry<string> s_weights;

        internal static void Bind(ConfigFile config, FiresCore.Sync.ConfigSync configSync)
        {
            s_allowed = config.Bind("Companions.Races", "Allowed races", "Human, Dvergr, Draugr, Skeleton, Goblin",
                "SERVER. Races a wild companion can roll (and the Shapeshifter's Draught can pick). Known: " + string.Join(", ", Known) +
                ". Greyling only once its armour fit is proven. Human is always allowed.");
            s_weights = config.Bind("Companions.Races", "Race weights", "Human:60, Dvergr:12, Draugr:8, Skeleton:8, Goblin:8, Greyling:4",
                "SERVER. Weighted roll for a new wild companion's race, as Race:weight pairs. Only allowed races take part; 0 removes a race.");
            configSync?.AddConfigEntry(s_allowed);
            configSync?.AddConfigEntry(s_weights);
            FiresCore.Config.OwnerGatedConfig.Gate(FiresCore.Config.OwnerGatedConfig.Owners.COMP, true, s_allowed, s_weights);
        }

        /// <summary>The allowed races (Human always included), in Known order.</summary>
        public static IReadOnlyList<string> Allowed()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Human };
            foreach (var part in (s_allowed?.Value ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                set.Add(part.Trim());
            return Known.Where(set.Contains).ToList();
        }

        /// <summary>The race stored on a companion; Human when the key is empty or unknown.</summary>
        public static string Of(ZDO zdo)
        {
            string race = zdo?.GetString(ZdoKey, string.Empty) ?? string.Empty;
            return Known.FirstOrDefault(k => string.Equals(k, race, StringComparison.OrdinalIgnoreCase)) ?? Human;
        }

        /// <summary>Server: rolls and stores a race for a freshly dressed wild companion (only while the key is empty).</summary>
        internal static string RollInto(ZDO zdo, Random rng, string who)
        {
            if (zdo == null) return Human;
            string existing = zdo.GetString(ZdoKey, string.Empty);
            if (!string.IsNullOrEmpty(existing))
            {
                FiresLogger.LogDebug($"[CompanionRaces] {who} kept {existing}");
                return existing;
            }
            var allowed = new HashSet<string>(Allowed(), StringComparer.OrdinalIgnoreCase);
            var table = Weights().Where(w => allowed.Contains(w.Key) && w.Value > 0).ToList();
            string race = Human;
            int total = table.Sum(w => w.Value);
            if (total > 0)
            {
                int pick = rng.Next(total);
                foreach (var w in table)
                {
                    if (pick < w.Value) { race = w.Key; break; }
                    pick -= w.Value;
                }
            }
            zdo.Set(ZdoKey, race);
            FiresLogger.LogDebug($"[CompanionRaces] {who} {zdo.m_uid} rolled {race} (weights {string.Join(", ", table.Select(w => w.Key + ":" + w.Value))})");
            return race;
        }

        private static List<KeyValuePair<string, int>> Weights()
        {
            var list = new List<KeyValuePair<string, int>>();
            foreach (var part in (s_weights?.Value ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':');
                if (kv.Length != 2 || !int.TryParse(kv[1].Trim(), out int w)) continue;
                string name = Known.FirstOrDefault(k => string.Equals(k, kv[0].Trim(), StringComparison.OrdinalIgnoreCase));
                if (name != null) list.Add(new KeyValuePair<string, int>(name, Math.Max(0, w)));
            }
            return list;
        }
    }
}
