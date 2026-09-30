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

        // Owners known to have a live row this session. Filled once off the main thread (a read; the Shared connection
        // serializes it against the main thread's writes) and by every insert here; a season roll or wipe clears it.
        private static readonly HashSet<string> s_knownOwners = new HashSet<string>(StringComparer.Ordinal);
        private static readonly object s_knownLock = new object();
        private static bool s_preloadStarted;
        private static string s_preloadPath;
        // Owners whose row EnsureRow queued on the VaultWriter and that is not written yet (under s_knownLock).
        private static readonly HashSet<string> s_pendingOwners = new HashSet<string>(StringComparer.Ordinal);

        public static LeaderboardEntry GetOrCreate(string ownerKey, string playerName)
        {
            WaitForQueuedRow(ownerKey);
            return FindOrCreate(ownerKey, playerName);
        }

        private static LeaderboardEntry FindOrCreate(string ownerKey, string playerName)
        {
            try
            {
                using var db = VaultDatabase.Open();
                var collection = db.GetCollection<LeaderboardEntry>(VaultDatabase.LeaderboardCollection, BsonAutoId.ObjectId);
                collection.EnsureIndex(x => x.Owner);

                var entry = collection.FindOne(e => e.Owner == ownerKey);
                if (entry == null)
                {
                    entry = new LeaderboardEntry { Owner = ownerKey, PlayerName = playerName };
                    collection.Insert(entry);
                }
                Know(ownerKey);
                return entry;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} GetOrCreate failed: {ex.Message}");
                return null;
            }
        }

        // The row a connecting character should have, without the entry. Opening the vault, ensuring the index and the
        // lookup cost the dedi's main thread 79-188 ms at every join (R3/R5 LoginDiag) even for rows that existed, so a
        // known owner skips the vault. Any other owner (a new character, or a join before the first preload finishes) has
        // its row found or written on the background VaultWriter: creating it held a new world's first join 50 ms (R23).
        public static void EnsureRow(string ownerKey, string playerName)
        {
            if (string.IsNullOrEmpty(ownerKey)) return;
            StartPreload();
            if (IsKnown(ownerKey)) return;
            lock (s_knownLock)
            {
                if (!s_pendingOwners.Add(ownerKey)) return;
            }
            VaultWriter.Enqueue(() =>
            {
                try { FindOrCreate(ownerKey, playerName); }
                finally { lock (s_knownLock) s_pendingOwners.Remove(ownerKey); }
            });
        }

        // A write that inserts a missing row waits for that owner's queued row first; otherwise both could miss it and the
        // owner would get two rows.
        private static void WaitForQueuedRow(string ownerKey)
        {
            bool queued;
            lock (s_knownLock) queued = ownerKey != null && s_pendingOwners.Contains(ownerKey);
            if (queued) VaultWriter.Drain();
        }

        // VaultDatabase.Configure points the vault at a world's file; on a server the preload starts there, before any join
        // can contend with it.
        internal static void OnVaultConfigured()
        {
            if (ZNet.instance != null && ZNet.instance.IsServer()) StartPreload();
        }

        // One preload per vault file: a host that loads another world starts over with that world's owners.
        private static void StartPreload()
        {
            string path = VaultDatabase.DatabasePath;
            lock (s_knownLock)
            {
                if (s_preloadStarted && s_preloadPath == path) return;
                s_preloadStarted = true;
                s_preloadPath = path;
                s_knownOwners.Clear();
            }
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using var db = VaultDatabase.Open();
                    var owners = db.GetCollection<LeaderboardEntry>(VaultDatabase.LeaderboardCollection, BsonAutoId.ObjectId)
                        .Query().Select(x => x.Owner).ToList();
                    lock (s_knownLock)
                    {
                        if (s_preloadPath != path) return;
                        foreach (string owner in owners) if (!string.IsNullOrEmpty(owner)) s_knownOwners.Add(owner);
                    }
                }
                catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} owner preload failed (joins open the vault as before): {ex.Message}"); }
            });
        }

        private static bool IsKnown(string ownerKey)
        {
            lock (s_knownLock) return s_knownOwners.Contains(ownerKey);
        }

        private static void Know(string ownerKey)
        {
            if (string.IsNullOrEmpty(ownerKey)) return;
            lock (s_knownLock) s_knownOwners.Add(ownerKey);
        }

        private static void ForgetAllOwners()
        {
            lock (s_knownLock) s_knownOwners.Clear();
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

        // Per-weapon-prefab kill tally. The "favorite weapon" is read as the highest-count entry here.
        public static void RecordWeaponKill(string ownerKey, string playerName, string weaponPrefab, int amount = 1)
            => Mutate(ownerKey, playerName, e =>
            {
                if (e.WeaponKills == null) e.WeaponKills = new Dictionary<string, int>();
                Bump(e.WeaponKills, weaponPrefab, amount);
            });

        // Latest-wins skill level for a single skill (key = Skills.SkillType name, level 0..100).
        public static void SetSkillLevel(string ownerKey, string playerName, string skill, int level)
            => Mutate(ownerKey, playerName, e =>
            {
                if (string.IsNullOrEmpty(skill)) return;
                if (e.Skills == null) e.Skills = new Dictionary<string, int>();
                e.Skills[skill] = level;
            });

        // A whole skill batch in one vault round trip. One SetSkillLevel per skill opened the shared LiteDB file, ensured
        // the index, found, updated and closed it once per skill: 849 ms on the server's main thread for one batch.
        public static void SetSkillLevels(string ownerKey, string playerName, IList<KeyValuePair<string, int>> levels)
        {
            if (levels == null || levels.Count == 0) return;
            Mutate(ownerKey, playerName, e =>
            {
                if (e.Skills == null) e.Skills = new Dictionary<string, int>();
                for (int i = 0; i < levels.Count; i++)
                    if (!string.IsNullOrEmpty(levels[i].Key)) e.Skills[levels[i].Key] = levels[i].Value;
            });
        }

        public static void RecordDeath(string ownerKey, string playerName)
            => Mutate(ownerKey, playerName, e => e.DeathAmount++);

        public static void SetMapExplored(string ownerKey, string playerName, float percent)
            => Mutate(ownerKey, playerName, e => { if (percent > e.MapExplored) e.MapExplored = percent; });

        // Set-once flag for the first time a player enters a biome (key = Heightmap.Biome name).
        public static void RecordBiomeReached(string ownerKey, string playerName, string biome)
            => Mutate(ownerKey, playerName, e =>
            {
                if (string.IsNullOrEmpty(biome)) return;
                if (e.BiomesReached == null) e.BiomesReached = new Dictionary<string, int>();
                e.BiomesReached[biome] = 1;
            });

        // Running count of creatures tamed. >0 satisfies the "first tame" milestone.
        public static void RecordTamed(string ownerKey, string playerName, int amount = 1)
            => Mutate(ownerKey, playerName, e => e.Tamed += Math.Max(1, amount));

        // Hoe terrain ops (raise / level / path). Separate from BuiltStructures so terraforming isn't a "build".
        public static void RecordTerraformed(string ownerKey, string playerName, int amount = 1)
            => Mutate(ownerKey, playerName, e => e.Terraformed += Math.Max(1, amount));

        // Cultivator ops (cultivate farmland + plant seeds/saplings).
        public static void RecordCultivated(string ownerKey, string playerName, int amount = 1)
            => Mutate(ownerKey, playerName, e => e.Cultivated += Math.Max(1, amount));

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

        // ── Seasons ─────────────────────────────────────────────────────────────────────────────────
        // The live Leaderboard collection IS the current season. Rolling a season copies every live row into the
        // archive (tagged with the season number), drops the live collection (a clean wipe), and bumps the season
        // counter. Past seasons are read back from the archive; the current season reads live.

        private const int SeasonMetaId = 1;

        public static int GetCurrentSeason()
        {
            try { using var db = VaultDatabase.Open(); return ReadSeasonMeta(db); }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetCurrentSeason failed: {ex.Message}"); return 1; }
        }

        private static int ReadSeasonMeta(LiteDatabase db)
        {
            var doc = db.GetCollection(VaultDatabase.LeaderboardMetaCollection).FindById(SeasonMetaId);
            return doc != null && doc.ContainsKey("Season") ? doc["Season"].AsInt32 : 1;
        }

        private static void WriteSeasonMeta(LiteDatabase db, int season)
            => db.GetCollection(VaultDatabase.LeaderboardMetaCollection)
                 .Upsert(new BsonDocument { ["_id"] = SeasonMetaId, ["Season"] = season });

        /// <summary>Archive every live row under the current season, wipe the live board, advance the season.
        /// Returns the season number that was just archived, or -1 on failure.</summary>
        public static int RollSeason()
        {
            try
            {
                using var db = VaultDatabase.Open();
                int current = ReadSeasonMeta(db);
                var live = db.GetCollection<LeaderboardEntry>(VaultDatabase.LeaderboardCollection, BsonAutoId.ObjectId);
                var archive = db.GetCollection<LeaderboardEntry>(VaultDatabase.LeaderboardArchiveCollection, BsonAutoId.ObjectId);
                archive.EnsureIndex(x => x.Season);
                foreach (var row in live.FindAll().ToList())
                {
                    row._id = 0;            // let the archive assign a fresh id
                    row.Season = current;
                    archive.Insert(row);
                }
                db.DropCollection(VaultDatabase.LeaderboardCollection);
                ForgetAllOwners();
                WriteSeasonMeta(db, current + 1);
                FiresLogger.LogInfo($"{LogPrefix} Rolled season {current} → {current + 1} (archived the live board).");
                return current;
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} RollSeason failed: {ex.Message}"); return -1; }
        }

        /// <summary>Hard-wipe the leaderboard: drop the live board, the season archive, and the season counter.
        /// Unlike <see cref="RollSeason"/> (which archives), this destroys all history and resets the season to 1
        /// (the meta doc is gone, so <see cref="GetCurrentSeason"/> falls back to its default of 1). SERVER-only;
        /// for a fresh-install / new-season-1 reset. Other vault collections (Bank/Marketplace/Mail/Guild/
        /// PlayerIdentity/PlayerAppearance) are untouched.</summary>
        public static void WipeAll()
        {
            try
            {
                using var db = VaultDatabase.Open();
                db.DropCollection(VaultDatabase.LeaderboardCollection);
                ForgetAllOwners();
                db.DropCollection(VaultDatabase.LeaderboardArchiveCollection);
                db.DropCollection(VaultDatabase.LeaderboardMetaCollection);
                FiresLogger.LogInfo($"{LogPrefix} WipeAll — dropped live board, archive, and season meta (reset to season 1).");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} WipeAll failed: {ex.Message}"); }
        }

        /// <summary>Distinct archived season numbers, ascending (does not include the current/live season).</summary>
        public static List<int> GetArchivedSeasons()
        {
            try
            {
                using var db = VaultDatabase.Open();
                return db.GetCollection<LeaderboardEntry>(VaultDatabase.LeaderboardArchiveCollection, BsonAutoId.ObjectId)
                    .FindAll().Select(e => e.Season).Distinct().OrderBy(s => s).ToList();
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetArchivedSeasons failed: {ex.Message}"); return new List<int>(); }
        }

        /// <summary>Rows for a given season: a negative season = the all-time OVERALL aggregate (every season
        /// summed per player); the live board if it's the current season (or newer); else the archive.</summary>
        public static Dictionary<string, LeaderboardEntry> GetSeasonEntries(int season)
        {
            if (season < 0) return GetOverallEntries();
            var all = new Dictionary<string, LeaderboardEntry>();
            try
            {
                using var db = VaultDatabase.Open();
                int current = ReadSeasonMeta(db);
                if (season >= current)
                {
                    foreach (var entry in db.GetCollection<LeaderboardEntry>(VaultDatabase.LeaderboardCollection, BsonAutoId.ObjectId).FindAll())
                        all[entry.Owner] = entry;
                }
                else
                {
                    foreach (var entry in db.GetCollection<LeaderboardEntry>(VaultDatabase.LeaderboardArchiveCollection, BsonAutoId.ObjectId).Find(x => x.Season == season))
                        all[entry.Owner] = entry;
                }
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetSeasonEntries failed: {ex.Message}"); }
            return all;
        }

        /// <summary>All-time aggregate per player across the live board AND every archived season: additive
        /// counters summed; MapExplored and skill levels taken as the max (the same world isn't re-explorable, and
        /// a skill level is a high-water mark, not a per-season sum). Favorite weapon / top skills then read off
        /// the merged dictionaries exactly like a single season.</summary>
        public static Dictionary<string, LeaderboardEntry> GetOverallEntries()
        {
            var merged = new Dictionary<string, LeaderboardEntry>();
            try
            {
                using var db = VaultDatabase.Open();
                var live = db.GetCollection<LeaderboardEntry>(VaultDatabase.LeaderboardCollection, BsonAutoId.ObjectId).FindAll();
                var archived = db.GetCollection<LeaderboardEntry>(VaultDatabase.LeaderboardArchiveCollection, BsonAutoId.ObjectId).FindAll();
                foreach (var entry in live.Concat(archived))
                {
                    if (entry == null || string.IsNullOrEmpty(entry.Owner)) continue;
                    if (!merged.TryGetValue(entry.Owner, out var acc))
                    {
                        acc = new LeaderboardEntry { Owner = entry.Owner, PlayerName = entry.PlayerName };
                        merged[entry.Owner] = acc;
                    }
                    if (!string.IsNullOrEmpty(entry.PlayerName)) acc.PlayerName = entry.PlayerName;
                    MergeInto(acc, entry);
                }
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetOverallEntries failed: {ex.Message}"); }
            return merged;
        }

        private static void MergeInto(LeaderboardEntry acc, LeaderboardEntry entry)
        {
            MergeDict(acc.KilledCreatures, entry.KilledCreatures);
            MergeDict(acc.BuiltStructures, entry.BuiltStructures);
            MergeDict(acc.ItemsCrafted, entry.ItemsCrafted);
            MergeDict(acc.KilledBy, entry.KilledBy);
            MergeDict(acc.Harvested, entry.Harvested);
            MergeDict(acc.WeaponKills, entry.WeaponKills);
            MergeDict(acc.BiomesReached, entry.BiomesReached);
            acc.DeathAmount += entry.DeathAmount;
            acc.Tamed += entry.Tamed;
            acc.Terraformed += entry.Terraformed;
            acc.Cultivated += entry.Cultivated;
            if (entry.MapExplored > acc.MapExplored) acc.MapExplored = entry.MapExplored;
            if (entry.Skills != null)
                foreach (var kv in entry.Skills)
                    if (!acc.Skills.TryGetValue(kv.Key, out var existing) || kv.Value > existing) acc.Skills[kv.Key] = kv.Value;
        }

        private static void MergeDict(Dictionary<string, int> into, Dictionary<string, int> from)
        {
            if (into == null || from == null) return;
            foreach (var kv in from) { into.TryGetValue(kv.Key, out int existing); into[kv.Key] = existing + kv.Value; }
        }

        private static void Mutate(string ownerKey, string playerName, Action<LeaderboardEntry> mutate)
        {
            if (string.IsNullOrEmpty(ownerKey)) return;
            WaitForQueuedRow(ownerKey);
            try
            {
                using var db = VaultDatabase.Open();
                var collection = db.GetCollection<LeaderboardEntry>(VaultDatabase.LeaderboardCollection, BsonAutoId.ObjectId);
                collection.EnsureIndex(x => x.Owner);

                var entry = collection.FindOne(e => e.Owner == ownerKey);
                if (entry == null)
                {
                    entry = new LeaderboardEntry { Owner = ownerKey, PlayerName = playerName };
                    mutate(entry);
                    collection.Insert(entry);
                    Know(ownerKey);
                }
                else
                {
                    if (!string.IsNullOrEmpty(playerName)) entry.PlayerName = playerName;
                    mutate(entry);
                    collection.Update(entry);
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
