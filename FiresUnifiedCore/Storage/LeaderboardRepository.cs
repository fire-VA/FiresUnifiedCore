using System;
using System.Collections.Generic;
using FiresCore.Logging;
using LiteDB;

namespace FiresCore.Storage
{
    // Leaderboard storage over the "Leaderboard" collection. Operations mirror Marketplace's DB.cs.
    // Owner is keyed as "{hostName}_{playerName}".Trim() by Marketplace — callers pass that same key.
    public static class LeaderboardRepository
    {
        private const string LogPrefix = "[VaultLeaderboard]";

        public static LeaderboardEntry GetOrCreate(string ownerKey, string playerName)
        {
            try
            {
                using var db = VaultDatabase.Open();
                var col = db.GetCollection<LeaderboardEntry>(VaultDatabase.LeaderboardCollection, BsonAutoId.ObjectId);
                col.EnsureIndex(x => x.Owner);

                var entry = col.FindOne(e => e.Owner == ownerKey);
                if (entry != null) return entry;

                entry = new LeaderboardEntry { Owner = ownerKey, PlayerName = playerName };
                col.Insert(entry);
                return entry;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} GetOrCreate failed: {ex.Message}");
                return null;
            }
        }

        public static Dictionary<string, LeaderboardEntry> GetAll()
        {
            var all = new Dictionary<string, LeaderboardEntry>();
            try
            {
                using var db = VaultDatabase.Open();
                foreach (var entry in db.GetCollection<LeaderboardEntry>(VaultDatabase.LeaderboardCollection, BsonAutoId.ObjectId).FindAll())
                    all[entry.Owner] = entry;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetAll failed: {ex.Message}"); }
            return all;
        }

        public static void Update(LeaderboardEntry entry)
        {
            try
            {
                using var db = VaultDatabase.Open();
                db.GetCollection<LeaderboardEntry>(VaultDatabase.LeaderboardCollection, BsonAutoId.ObjectId).Update(entry);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} Update failed: {ex.Message}"); }
        }
    }
}
