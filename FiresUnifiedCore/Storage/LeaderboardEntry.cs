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

        public int _id { get; set; }
        public string Owner { get; set; }
        public float MapExplored { get; set; }
        public int DeathAmount { get; set; }
        public string PlayerName { get; set; }

        [BsonIgnore]
        public int KilledPlayers =>
            KilledCreatures.TryGetValue("Player", out int count) ? count : 0;
    }
}
