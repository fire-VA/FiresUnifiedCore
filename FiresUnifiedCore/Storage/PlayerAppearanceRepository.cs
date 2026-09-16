using System;
using FiresCore.Logging;
using LiteDB;

namespace FiresCore.Storage
{
    /// <summary>
    /// Appearance storage over the shared VaultDatabase <c>PlayerAppearance</c> collection. Mirrors
    /// <see cref="LeaderboardRepository"/> / PlayerIdentityRepository: static, open-per-operation
    /// (<c>using var db = VaultDatabase.Open()</c>), try/catch + <see cref="FiresLogger"/> warnings, and a
    /// guard on <see cref="VaultDatabase.IsConfigured"/> so a host that never configured the vault no-ops
    /// (and warns) instead of throwing. One row per player keyed by <see cref="PlayerAppearance.Owner"/>
    /// (the trimmed name, same key as the board). Latest-wins by <see cref="PlayerAppearance.UpdatedAtUtcTicks"/>.
    /// </summary>
    public static class PlayerAppearanceRepository
    {
        private const string LogPrefix = "[VaultAppearance]";

        public static void Upsert(PlayerAppearance incoming)
        {
            if (incoming == null || string.IsNullOrEmpty(incoming.Owner)) return;
            if (!Ready("Upsert")) return;

            try
            {
                using var db = VaultDatabase.Open();
                var collection = db.GetCollection<PlayerAppearance>(VaultDatabase.PlayerAppearanceCollection, BsonAutoId.ObjectId);
                collection.EnsureIndex(x => x.Owner);

                var existing = collection.FindOne(e => e.Owner == incoming.Owner);
                if (existing == null)
                {
                    collection.Insert(incoming);
                    return;
                }

                // Latest-wins: only overwrite when the incoming snapshot is at least as new.
                if (incoming.UpdatedAtUtcTicks < existing.UpdatedAtUtcTicks) return;
                collection.Update(incoming);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} Upsert failed: {ex.Message}"); }
        }

        public static PlayerAppearance Get(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return null;
            if (!Ready("Get")) return null;

            try
            {
                using var db = VaultDatabase.Open();
                return db.GetCollection<PlayerAppearance>(VaultDatabase.PlayerAppearanceCollection, BsonAutoId.ObjectId)
                    .FindOne(e => e.Owner == owner);
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} Get failed: {ex.Message}"); return null; }
        }

        /// <summary>Drop every stored mannequin appearance (a fresh-start / season-1 reset). SERVER-only.</summary>
        public static void WipeAll()
        {
            if (!Ready("WipeAll")) return;
            try
            {
                using var db = VaultDatabase.Open();
                db.DropCollection(VaultDatabase.PlayerAppearanceCollection);
                FiresLogger.LogInfo($"{LogPrefix} WipeAll — dropped all stored appearances.");
            }
            catch (Exception ex) { FiresLogger.LogWarning($"{LogPrefix} WipeAll failed: {ex.Message}"); }
        }

        // Guard mirroring the other repos: if the shared vault was never configured by a host mod,
        // no-op + warn rather than letting VaultDatabase.Open() throw.
        private static bool Ready(string op)
        {
            if (VaultDatabase.IsConfigured) return true;
            FiresLogger.LogWarning($"{LogPrefix} {op} skipped: VaultDatabase is not configured.");
            return false;
        }
    }
}
