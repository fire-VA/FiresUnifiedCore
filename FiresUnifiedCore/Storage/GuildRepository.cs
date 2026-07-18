using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Logging;
using LiteDB;

namespace FiresCore.Storage
{
    /// <summary>
    /// Guild persistence over the shared VaultDatabase "Guild" collection. Mirrors
    /// <see cref="PlayerAppearanceRepository"/> / PlayerIdentityRepository / LeaderboardRepository: static,
    /// open-per-operation (<c>using var db = VaultDatabase.Open()</c>), try/catch + <see cref="FiresLogger"/>
    /// warnings, and a <see cref="VaultDatabase.IsConfigured"/> guard so a host that never configured the
    /// vault no-ops (and warns) rather than throwing. Per-world (the vault path is per-world). One
    /// <see cref="GuildRecord"/> per guild, keyed by <see cref="GuildRecord.GuildId"/>; the guild domain
    /// model is serialized into the record's Payload by the owning mod, so Core holds no guild logic.
    /// </summary>
    public static class GuildRepository
    {
        private const string LogPrefix = "[VaultGuild]";

        public static void Upsert(GuildRecord record)
        {
            if (record == null || string.IsNullOrEmpty(record.GuildId)) return;
            if (!Ready("Upsert")) return;

            try
            {
                using var db = VaultDatabase.Open();
                db.GetCollection<GuildRecord>(VaultDatabase.GuildCollection).Upsert(record);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} Upsert failed: {ex.Message}"); }
        }

        public static GuildRecord Get(string guildId)
        {
            if (string.IsNullOrEmpty(guildId) || !Ready("Get")) return null;

            try
            {
                using var db = VaultDatabase.Open();
                return db.GetCollection<GuildRecord>(VaultDatabase.GuildCollection).FindById(guildId);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} Get failed: {ex.Message}"); return null; }
        }

        public static List<GuildRecord> GetAll()
        {
            if (!Ready("GetAll")) return new List<GuildRecord>();

            try
            {
                using var db = VaultDatabase.Open();
                return db.GetCollection<GuildRecord>(VaultDatabase.GuildCollection).FindAll().ToList();
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} GetAll failed: {ex.Message}"); return new List<GuildRecord>(); }
        }

        public static void Delete(string guildId)
        {
            if (string.IsNullOrEmpty(guildId) || !Ready("Delete")) return;

            try
            {
                using var db = VaultDatabase.Open();
                db.GetCollection<GuildRecord>(VaultDatabase.GuildCollection).Delete(guildId);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} Delete failed: {ex.Message}"); }
        }

        // Guard mirroring the other vault repos: if no host mod configured the shared vault, no-op + warn
        // rather than letting VaultDatabase.Open() throw.
        private static bool Ready(string op)
        {
            if (VaultDatabase.IsConfigured) return true;
            FiresLogger.LogWarning($"{LogPrefix} {op} skipped: VaultDatabase is not configured.");
            return false;
        }
    }
}
