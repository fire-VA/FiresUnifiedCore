using System;
using UnityEngine;

namespace FiresCore.Npc.Vault
{
    /// <summary>
    /// Converts between a live companion (controller plus ZDO) and an inert <see cref="CompanionSaveData"/>:
    /// <see cref="Capture"/> reads everything needed to rebuild it, <see cref="Apply"/> writes a snapshot back
    /// onto a fresh companion. A facade over CompanionVault's field copy with no file, vault or roster I/O;
    /// persisting the snapshot is the caller's job.
    /// </summary>
    public static class CompanionZdoSnapshot
    {
        private const string LogPrefix = "[CompanionZdoSnapshot]";

        /// <summary>
        /// Diagnostic flag, off by default. When on, every Capture and Apply
        /// logs at Info level. Production callers do their own higher-level
        /// logging (death-flow log, respawn-flow log, etc.) so this is
        /// strictly for low-level inspection.
        /// </summary>
        public static bool VerboseLogging = false;

        /// <summary>
        /// Reads a live companion into an inert <see cref="CompanionSaveData"/>. Returns null when the companion
        /// is null, has no companionId yet, or the copy throws; callers must then persist nothing, since storing
        /// an incomplete snapshot for a pending respawn is what produced wrong-name and wrong-scale respawns.
        /// </summary>
        public static CompanionSaveData Capture(CompanionController companion)
        {
            if (companion == null)
            {
                if (VerboseLogging) Debug.LogWarning($"{LogPrefix} Capture: null companion.");
                return null;
            }

            if (string.IsNullOrEmpty(companion.companionId))
            {
                // Not an error condition — companions go through a brief
                // "exists but not yet initialised" window during spawn.
                // Quietly return null and let the caller decide whether
                // that's a problem.
                if (VerboseLogging) Debug.Log($"{LogPrefix} Capture: companion has no companionId yet, skipping.");
                return null;
            }

            try
            {
                var snapshot = CompanionVault.BuildSaveData(companion);
                if (snapshot == null)
                {
                    Debug.LogWarning($"{LogPrefix} Capture: BuildSaveData returned null for {companion.companionName} ({companion.companionId}).");
                    return null;
                }

                if (VerboseLogging)
                {
                    Debug.Log($"{LogPrefix} Captured snapshot for {companion.companionName} ({companion.companionId}): " +
                              $"scale={snapshot.Scale:F2}, hasEquip={snapshot.EquipmentPrefabs?.Count ?? 0}, " +
                              $"realm={snapshot.RealmId}.");
                }

                return snapshot;
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix} Capture threw for {companion.companionName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Writes the contents of <paramref name="snapshot"/> onto the live
        /// <paramref name="companion"/>. Used during respawn from roster /
        /// vault, and during cross-session restore on player login.
        /// 
        /// Returns true on success, false otherwise. A false return MUST be
        /// treated as fatal-for-this-companion — callers should NOT leave a
        /// half-restored companion in the world. Either re-attempt or
        /// destroy and reschedule. <see cref="CompanionRespawnManager"/>
        /// already encodes this convention; new callers should follow it.
        /// </summary>
        public static bool Apply(CompanionController companion, CompanionSaveData snapshot)
        {
            if (companion == null)
            {
                Debug.LogWarning($"{LogPrefix} Apply: null companion.");
                return false;
            }

            if (snapshot == null)
            {
                Debug.LogWarning($"{LogPrefix} Apply: null snapshot for {companion.companionName}; refusing to apply default state.");
                return false;
            }

            // Defensive — if the snapshot lacks an id, the companion will
            // come back with a default name later when something asks for
            // its identity. That's exactly the broken state we're refactoring
            // away. Refuse rather than silently completing with a synthetic
            // identity.
            if (string.IsNullOrEmpty(snapshot.CompanionId))
            {
                Debug.LogWarning($"{LogPrefix} Apply: snapshot has no CompanionId; refusing to apply (would produce nameless companion).");
                return false;
            }

            try
            {
                bool ok = CompanionVault.RestoreCompanion(companion, snapshot);
                if (!ok)
                {
                    Debug.LogWarning($"{LogPrefix} Apply: RestoreCompanion returned false for {snapshot.CompanionName} ({snapshot.CompanionId}).");
                    return false;
                }

                if (VerboseLogging)
                {
                    Debug.Log($"{LogPrefix} Applied snapshot to {snapshot.CompanionName} ({snapshot.CompanionId}): " +
                              $"scale={snapshot.Scale:F2}, hasEquip={snapshot.EquipmentPrefabs?.Count ?? 0}.");
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"{LogPrefix} Apply threw for {snapshot.CompanionName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Captures <paramref name="companion"/> and stores it in the owner's roster as a death snapshot with a
        /// wall-clock <paramref name="respawnDeadlineUtc"/>. Returns true only when the snapshot was both captured and
        /// persisted.
        /// </summary>
        public static bool StoreDeathSnapshotOnPlayer(
            Player owner,
            CompanionController companion,
            CompanionFollowState followState,
            DateTime respawnDeadlineUtc)
        {
            if (owner == null) return false;

            var snapshot = Capture(companion);
            if (snapshot == null) return false;

            var entry = new PlayerCompanionRosterEntry
            {
                CompanionId             = snapshot.CompanionId,
                ServerWorldUid          = PlayerCompanionStorage.GetCurrentServerWorldUid(),
                FollowState             = followState,
                IsPendingRespawn        = true,
                RespawnDeadlineUtcTicks = respawnDeadlineUtc.Ticks,
                Snapshot                = snapshot,
            };

            return PlayerCompanionStorage.UpsertEntry(owner, entry);
        }

        /// <summary>
        /// Computes the seconds remaining on a stored respawn deadline,
        /// clamped to - 0. Returns 0 (i.e. "respawn now") when the deadline
        /// has passed — that's how a crash recovery resumes a missed timer:
        /// next login finds <c>remaining = 0</c> and respawns immediately.
        /// </summary>
        public static float ComputeRemainingRespawnSeconds(long respawnDeadlineUtcTicks)
        {
            if (respawnDeadlineUtcTicks <= 0) return 0f;
            try
            {
                long now = DateTime.UtcNow.Ticks;
                long delta = respawnDeadlineUtcTicks - now;
                if (delta <= 0) return 0f;
                return (float)TimeSpan.FromTicks(delta).TotalSeconds;
            }
            catch
            {
                return 0f;
            }
        }
    }
}
