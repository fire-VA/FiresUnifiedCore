using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Vault
{
    /// <summary>
    /// One-shot migration from the legacy JSON vault into the per-player roster, run on a login where the
    /// roster is empty and the vault is not. Writing any entry makes the roster non-empty, so it never runs
    /// twice. Failures are logged, leave the vault untouched and are retried on the next login, with the
    /// resolver's vault fallback covering the gap.
    /// </summary>
    public static class PlayerCompanionMigrator
    {
        private const string LogPrefix = "[PlayerCompanionMigrator]";

        // Per-session de-dup so a single login doesn't fire the migration
        // twice if the restore coroutine is invoked from multiple paths.
        private static readonly HashSet<long> _migratedThisSession = new HashSet<long>();

        public static bool VerboseLogging = false;

        /// <summary>
        /// Run the vault - roster import for this player if their roster
        /// is currently empty. No-op when the roster already has entries
        /// or when the vault is unavailable / empty.
        ///
        /// Safe to call multiple times: a per-session set short-circuits
        /// repeats, and the "roster empty" check naturally gates
        /// cross-session repeats.
        /// </summary>
        public static void MigrateFromVaultIfNeeded(Player player)
        {
            if (player == null) return;

            long playerId;
            try { playerId = player.GetPlayerID(); }
            catch { return; }

            if (playerId == 0) return;
            if (_migratedThisSession.Contains(playerId)) return;
            _migratedThisSession.Add(playerId);

            try
            {
                var roster = PlayerCompanionStorage.GetRosterOrEmpty(player);
                if (roster?.Entries != null && roster.Entries.Count > 0)
                {
                    if (VerboseLogging)
                        Debug.Log($"{LogPrefix} Skipping migration for {playerId} — roster already has {roster.Entries.Count} entr{(roster.Entries.Count == 1 ? "y" : "ies")}.");
                    return;
                }

                List<CompanionSaveData> vaultCompanions = null;
                try
                {
                    vaultCompanions = FiresCore.Bridge.CompanionVaultBridge.GetCompanionsFor(playerId);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{LogPrefix} Vault read failed for {playerId}: {ex.Message}");
                    return;
                }

                if (vaultCompanions == null || vaultCompanions.Count == 0)
                {
                    if (VerboseLogging)
                        Debug.Log($"{LogPrefix} Nothing to migrate for {playerId} — vault empty.");
                    return;
                }

                long currentWorldUid = PlayerCompanionStorage.GetCurrentServerWorldUid();
                long nowTicks = DateTime.UtcNow.Ticks;

                if (roster == null) roster = new PlayerCompanionRoster();
                if (roster.Entries == null) roster.Entries = new List<PlayerCompanionRosterEntry>();

                int imported = 0;
                int skipped = 0;
                foreach (var saveData in vaultCompanions)
                {
                    if (saveData == null) { skipped++; continue; }
                    if (string.IsNullOrEmpty(saveData.CompanionId)) { skipped++; continue; }

                    var entry = BuildEntryFromVault(saveData, currentWorldUid, nowTicks);
                    if (entry == null) { skipped++; continue; }

                    roster.Entries.Add(entry);
                    imported++;
                }

                if (imported == 0)
                {
                    if (VerboseLogging)
                        Debug.Log($"{LogPrefix} No usable vault entries for {playerId} (skipped {skipped}).");
                    return;
                }

                bool saved = PlayerCompanionStorage.SaveRoster(player, roster);
                if (saved)
                {
                    Debug.Log($"{LogPrefix} Migrated {imported} companion{(imported == 1 ? "" : "s")} from vault ? roster for {playerId} (skipped {skipped}).");
                }
                else
                {
                    Debug.LogWarning($"{LogPrefix} Migration write FAILED for {playerId} ({imported} entries staged, {skipped} skipped) — vault remains the fallback source.");
                    // Allow retry next login.
                    _migratedThisSession.Remove(playerId);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix} Migration exception for {playerId}: {ex.Message}\n{ex.StackTrace}");
                _migratedThisSession.Remove(playerId);
            }
        }

        private static PlayerCompanionRosterEntry BuildEntryFromVault(CompanionSaveData saveData, long currentWorldUid, long nowTicks)
        {
            // Map the legacy boolean flags onto the new tri-state follow
            // intent. The legacy vault never carried a Dismissed concept,
            // so a non-stationed, non-following live companion maps to
            // Staying — the safest "in-world but parked" default.
            CompanionFollowState followState;
            if (saveData.IsStationedAsNpc)
                followState = CompanionFollowState.Stationed;
            else if (saveData.IsFollowing)
                followState = CompanionFollowState.Following;
            else
                followState = CompanionFollowState.Staying;

            long deadlineTicks = 0;
            if (saveData.IsPendingRespawn)
            {
                // Prefer the absolute death-timestamp + respawn-delay if we
                // have it (gives us a real wall-clock deadline that
                // survived the crash). Otherwise fall back to "remaining
                // seconds from now", which loses the crash-safety guarantee
                // for this one entry — a known limitation of the legacy
                // schema, accepted because we only see it during the
                // one-shot migration.
                try
                {
                    if (saveData.RespawnTimeRemaining > 0f)
                    {
                        deadlineTicks = nowTicks + TimeSpan.FromSeconds(saveData.RespawnTimeRemaining).Ticks;
                    }
                }
                catch
                {
                    deadlineTicks = 0;
                }
            }

            var entry = new PlayerCompanionRosterEntry
            {
                CompanionId = saveData.CompanionId,
                ServerWorldUid = currentWorldUid,
                FollowState = followState,
                IsPendingRespawn = saveData.IsPendingRespawn,
                RespawnDeadlineUtcTicks = deadlineTicks,
                Snapshot = saveData,
                LastUpdatedUtcTicks = nowTicks,
            };

            // Contract from PlayerCompanionStorage.UpsertEntry: a pending
            // entry MUST carry a snapshot. We always carry the source
            // CompanionSaveData as the snapshot, so this is satisfied by
            // construction; the assertion is just defence in depth.
            if (entry.IsPendingRespawn && entry.Snapshot == null)
                return null;

            return entry;
        }
    }
}
