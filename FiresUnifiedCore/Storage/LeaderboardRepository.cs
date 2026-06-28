using System;
using System.Collections.Generic;
using System.Linq;
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

        // ── Stat increments (server-authoritative). Each finds-or-creates the owner row and upserts in one op. ──

        public static void RecordKilledCreature(string ownerKey, string playerName, string prefab, int amount = 1)
            => Mutate(ownerKey, playerName, e => Bump(e.KilledCreatures, prefab, amount));

        public static void RecordBuilt(string ownerKey, string playerName, string prefab, int amount = 1)
            => Mutate(ownerKey, playerName, e => Bump(e.BuiltStructures, prefab, amount));

        public static void RecordCrafted(string ownerKey, string playerName, string prefab, int amount = 1)
            => Mutate(ownerKey, playerName, e => Bump(e.ItemsCrafted, prefab, amount));

        public static void RecordKilledBy(string ownerKey, string playerName, string source, int amount = 1)
            => Mutate(ownerKey, playerName, e => Bump(e.KilledBy, source, amount));

        public static void RecordHarvested(string ownerKey, string playerName, string prefab, int amount = 1)
            => Mutate(ownerKey, playerName, e => Bump(e.Harvested, prefab, amount));

        public static void RecordDeath(string ownerKey, string playerName)
            => Mutate(ownerKey, playerName, e => e.DeathAmount++);

        public static void SetMapExplored(string ownerKey, string playerName, float percent)
            => Mutate(ownerKey, playerName, e => { if (percent > e.MapExplored) e.MapExplored = percent; });

        public static LeaderboardEntry GetForPlayer(string ownerKey)
        {
            if (string.IsNullOrEmpty(ownerKey)) return null;
            try
            {
                using var db = VaultDatabase.Open();
                return db.GetCollection<LeaderboardEntry>(VaultDatabase.LeaderboardCollection, BsonAutoId.ObjectId)
                    .FindOne(e => e.Owner == ownerKey);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetForPlayer failed: {ex.Message}"); return null; }
        }

        public static List<LeaderboardEntry> GetTop(Func<LeaderboardEntry, long> selector, int count)
        {
            var result = new List<LeaderboardEntry>();
            if (selector == null || count <= 0) return result;
            try
            {
                using var db = VaultDatabase.Open();
                var all = db.GetCollection<LeaderboardEntry>(VaultDatabase.LeaderboardCollection, BsonAutoId.ObjectId).FindAll();
                result.AddRange(all.OrderByDescending(selector).Take(count));
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetTop failed: {ex.Message}"); }
            return result;
        }

        private static void Mutate(string ownerKey, string playerName, Action<LeaderboardEntry> mutate)
        {
            if (string.IsNullOrEmpty(ownerKey)) return;
            try
            {
                using var db = VaultDatabase.Open();
                var col = db.GetCollection<LeaderboardEntry>(VaultDatabase.LeaderboardCollection, BsonAutoId.ObjectId);
                col.EnsureIndex(x => x.Owner);

                var entry = col.FindOne(e => e.Owner == ownerKey);
                if (entry == null)
                {
                    entry = new LeaderboardEntry { Owner = ownerKey, PlayerName = playerName };
                    mutate(entry);
                    col.Insert(entry);
                }
                else
                {
                    if (!string.IsNullOrEmpty(playerName)) entry.PlayerName = playerName;
                    mutate(entry);
                    col.Update(entry);
                }
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} Mutate failed: {ex.Message}"); }
        }

        private static void Bump(Dictionary<string, int> dict, string key, int amount)
        {
            if (dict == null || string.IsNullOrEmpty(key)) return;
            dict.TryGetValue(key, out int current);
            dict[key] = current + amount;
        }
    }
}
