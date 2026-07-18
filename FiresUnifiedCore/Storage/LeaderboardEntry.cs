using System.Collections.Generic;
using LiteDB;

namespace FiresCore.Storage
{
    // Per-player tracked stats for the "Leaderboard" collection. Field names match Marketplace's
    // DB.Player_Leaderboard exactly (the five counters are public FIELDS, which is why VaultDatabase sets
    // BsonMapper IncludeFields = true). Owner is "{hostName}_{playerName}".Trim() as Marketplace keys it.
    public class LeaderboardEntry
    {
        public Dictionary<string, int> KilledCreatures = new Dictionary<string, int>();
        public Dictionary<string, int> BuiltStructures = new Dictionary<string, int>();
        public Dictionary<string, int> ItemsCrafted = new Dictionary<string, int>();
        public Dictionary<string, int> KilledBy = new Dictionary<string, int>();
        public Dictionary<string, int> Harvested = new Dictionary<string, int>();

        // Per-weapon-prefab kill tallies (key = weapon prefab name, value = kills). The "favorite weapon"
        // is the highest-count entry here, resolved at read time (mirrors Marketplace's weapon_kills:* rows).
        public Dictionary<string, int> WeaponKills = new Dictionary<string, int>();

        // Latest known skill level per skill (key = Skills.SkillType name, value = level 0..100). Owner-set,
        // latest-wins — the client snapshots its live skill list and the server overwrites with the newest.
        public Dictionary<string, int> Skills = new Dictionary<string, int>();

        // Adventure-milestone capture (drives the leaderboard Progression timeline). Boss kills / firsts are
        // DERIVED from the counters above; these two are the events that aren't otherwise recorded:
        //   BiomesReached — key = Heightmap.Biome name (e.g. "Swamp"), value = 1 once first entered.
        //   Tamed — running count of creatures tamed. >0 satisfies the "first tame" milestone.
        public Dictionary<string, int> BiomesReached = new Dictionary<string, int>();
        public int Tamed;

        // Tool-use tallies, kept OUT of BuiltStructures so terraforming/farming don't inflate the build count.
        //   Terraformed — hoe terrain ops (raise / level / path / paved road), coalesced client-side.
        //   Cultivated — cultivator ops (cultivate farmland + plant seeds/saplings), coalesced client-side.
        public int Terraformed;
        public int Cultivated;

        public int _id { get; set; }
        public string Owner { get; set; }
        public float MapExplored { get; set; }
        public int DeathAmount { get; set; }
        public string PlayerName { get; set; }

        // Season this row belongs to. Only meaningful in the LeaderboardArchive collection (set when a season is
        // rolled). Live rows leave it 0 — they always represent the current season.
        public int Season { get; set; }

        [BsonIgnore]
        public int KilledPlayers =>
            KilledCreatures.TryGetValue("Player", out int count) ? count : 0;
    }
}
