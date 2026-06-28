using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Logging;
using FiresCore.Storage;
using LiteDB;

namespace FiresCore.Identity
{
    // Player-identity storage over the shared VaultDatabase PlayerIdentity collection. Mirrors
    // LeaderboardRepository's open-per-operation idiom: static, `using var db = VaultDatabase.Open()`,
    // try/catch + FiresLogger warnings, and a guard on VaultDatabase.IsConfigured so a host that never
    // configured the vault no-ops (and warns) instead of throwing. Ported from VikingLands.Core's
    // PlayerIdentityRepository, which used its own LiteDB file — here it is one collection in the shared DB.
    internal static class PlayerIdentityRepository
    {
        private const string LogPrefix = "[PlayerIdentityRepo]";

        public static void UpsertSession(PlayerIdentityRecord incoming)
        {
            if (incoming == null) return;
            if (!Ready("UpsertSession")) return;

            try
            {
                using var db = VaultDatabase.Open();
                var collection = db.GetCollection<PlayerIdentityRecord>(VaultDatabase.PlayerIdentityCollection, BsonAutoId.ObjectId);
                collection.EnsureIndex(x => x.SteamId);
                collection.EnsureIndex(x => x.ConnectionUid);
                collection.EnsureIndex(x => x.PlayerName);
                collection.EnsureIndex(x => x.LastConnectionUtc);

                PlayerIdentityRecord existing = FindBestMatch(collection, incoming);
                DateTime now = DateTime.UtcNow;

                if (existing == null)
                {
                    incoming.CreatedAtUtc = now;
                    incoming.UpdatedAtUtc = now;
                    collection.Insert(incoming);
                    return;
                }

                existing.SteamId = FirstNonEmpty(incoming.SteamId, existing.SteamId);
                existing.ConnectionUid = incoming.ConnectionUid != 0L ? incoming.ConnectionUid : existing.ConnectionUid;
                existing.PlayerName = FirstNonEmpty(incoming.PlayerName, existing.PlayerName);
                existing.LastConnectionUtc = incoming.LastConnectionUtc != default(DateTime) ? incoming.LastConnectionUtc : existing.LastConnectionUtc;
                existing.LastIp = FirstNonEmpty(incoming.LastIp, existing.LastIp);
                existing.IsAdmin = incoming.IsAdmin;
                existing.UpdatedAtUtc = now;
                collection.Update(existing);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} UpsertSession failed: {ex.Message}"); }
        }

        public static List<PlayerIdentityRecord> GetRecent(int limit)
        {
            if (!Ready("GetRecent")) return new List<PlayerIdentityRecord>();
            try
            {
                using var db = VaultDatabase.Open();
                return db.GetCollection<PlayerIdentityRecord>(VaultDatabase.PlayerIdentityCollection, BsonAutoId.ObjectId)
                    .Query()
                    .OrderByDescending(x => x.LastConnectionUtc)
                    .Limit(Math.Max(1, limit))
                    .ToList();
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetRecent failed: {ex.Message}"); return new List<PlayerIdentityRecord>(); }
        }

        public static List<PlayerIdentityRecord> GetAdmins(int limit)
        {
            if (!Ready("GetAdmins")) return new List<PlayerIdentityRecord>();
            try
            {
                using var db = VaultDatabase.Open();
                return db.GetCollection<PlayerIdentityRecord>(VaultDatabase.PlayerIdentityCollection, BsonAutoId.ObjectId)
                    .Query()
                    .Where(x => x.IsAdmin)
                    .OrderByDescending(x => x.LastConnectionUtc)
                    .Limit(Math.Max(1, limit))
                    .ToList();
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetAdmins failed: {ex.Message}"); return new List<PlayerIdentityRecord>(); }
        }

        public static List<PlayerIdentityRecord> SearchByName(string playerName, int limit)
        {
            string needle = (playerName ?? string.Empty).Trim();
            if (needle.Length == 0) return new List<PlayerIdentityRecord>();
            if (!Ready("SearchByName")) return new List<PlayerIdentityRecord>();

            try
            {
                using var db = VaultDatabase.Open();
                return db.GetCollection<PlayerIdentityRecord>(VaultDatabase.PlayerIdentityCollection, BsonAutoId.ObjectId)
                    .FindAll()
                    .Where(x => !string.IsNullOrWhiteSpace(x.PlayerName)
                                && x.PlayerName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    .OrderByDescending(x => x.LastConnectionUtc)
                    .Take(Math.Max(1, limit))
                    .ToList();
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} SearchByName failed: {ex.Message}"); return new List<PlayerIdentityRecord>(); }
        }

        public static List<PlayerIdentityRecord> GetBySteamId(string steamId)
        {
            string normalized = PlayerIdentity.NormalizeSteamId(steamId);
            if (string.IsNullOrWhiteSpace(normalized)) return new List<PlayerIdentityRecord>();
            if (!Ready("GetBySteamId")) return new List<PlayerIdentityRecord>();

            try
            {
                using var db = VaultDatabase.Open();
                return db.GetCollection<PlayerIdentityRecord>(VaultDatabase.PlayerIdentityCollection, BsonAutoId.ObjectId)
                    .Find(x => x.SteamId == normalized)
                    .OrderByDescending(x => x.LastConnectionUtc)
                    .ToList();
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetBySteamId failed: {ex.Message}"); return new List<PlayerIdentityRecord>(); }
        }

        public static List<PlayerIdentityGroupedDto> GetGrouped(int limit)
        {
            if (!Ready("GetGrouped")) return new List<PlayerIdentityGroupedDto>();
            try
            {
                using var db = VaultDatabase.Open();
                return db.GetCollection<PlayerIdentityRecord>(VaultDatabase.PlayerIdentityCollection, BsonAutoId.ObjectId)
                    .FindAll()
                    .Where(x => !string.IsNullOrWhiteSpace(x.SteamId))
                    .GroupBy(x => x.SteamId, StringComparer.OrdinalIgnoreCase)
                    .Select(g => new PlayerIdentityGroupedDto
                    {
                        SteamId = g.Key,
                        Profiles = g.OrderByDescending(x => x.LastConnectionUtc)
                            .Select(x => new PlayerIdentityProfileDto
                            {
                                ConnectionUid = x.ConnectionUid,
                                PlayerName = x.PlayerName ?? string.Empty,
                                LastConnectionUtc = x.LastConnectionUtc,
                                LastIp = x.LastIp ?? string.Empty,
                                IsAdmin = x.IsAdmin
                            })
                            .ToList()
                    })
                    .OrderByDescending(x => x.Profiles.Count > 0 ? x.Profiles[0].LastConnectionUtc : DateTime.MinValue)
                    .Take(Math.Max(1, limit))
                    .ToList();
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetGrouped failed: {ex.Message}"); return new List<PlayerIdentityGroupedDto>(); }
        }

        public static PlayerIdentityStatsDto GetStats()
        {
            if (!Ready("GetStats")) return new PlayerIdentityStatsDto { GeneratedAtUtc = DateTime.UtcNow };
            try
            {
                using var db = VaultDatabase.Open();
                List<PlayerIdentityRecord> records = db
                    .GetCollection<PlayerIdentityRecord>(VaultDatabase.PlayerIdentityCollection, BsonAutoId.ObjectId)
                    .FindAll().ToList();
                return new PlayerIdentityStatsDto
                {
                    TotalRecords = records.Count,
                    DistinctSteamIds = records.Where(x => !string.IsNullOrWhiteSpace(x.SteamId))
                        .Select(x => x.SteamId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    AdminProfiles = records.Count(x => x.IsAdmin),
                    GeneratedAtUtc = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} GetStats failed: {ex.Message}");
                return new PlayerIdentityStatsDto { GeneratedAtUtc = DateTime.UtcNow };
            }
        }

        private static PlayerIdentityRecord FindBestMatch(ILiteCollection<PlayerIdentityRecord> collection, PlayerIdentityRecord incoming)
        {
            if (!string.IsNullOrWhiteSpace(incoming.SteamId) && incoming.ConnectionUid != 0L)
            {
                PlayerIdentityRecord bySteamAndUid = collection.FindOne(x => x.SteamId == incoming.SteamId && x.ConnectionUid == incoming.ConnectionUid);
                if (bySteamAndUid != null) return bySteamAndUid;
            }

            if (!string.IsNullOrWhiteSpace(incoming.SteamId) && !string.IsNullOrWhiteSpace(incoming.PlayerName))
            {
                PlayerIdentityRecord bySteamAndName = collection.FindAll()
                    .FirstOrDefault(x => x.SteamId == incoming.SteamId
                                         && string.Equals(x.PlayerName ?? string.Empty, incoming.PlayerName ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                if (bySteamAndName != null) return bySteamAndName;
            }

            if (incoming.ConnectionUid != 0L)
            {
                PlayerIdentityRecord byUid = collection.FindOne(x => x.ConnectionUid == incoming.ConnectionUid);
                if (byUid != null) return byUid;
            }

            return null;
        }

        // Guard mirroring how the other repos tolerate failure: if the shared vault was never configured
        // by a host mod, no-op + warn rather than letting VaultDatabase.Open() throw.
        private static bool Ready(string op)
        {
            if (VaultDatabase.IsConfigured) return true;
            FiresLogger.LogWarning($"{LogPrefix} {op} skipped: VaultDatabase is not configured.");
            return false;
        }

        private static string FirstNonEmpty(string preferred, string fallback)
            => !string.IsNullOrWhiteSpace(preferred) ? preferred : (fallback ?? string.Empty);
    }
}
