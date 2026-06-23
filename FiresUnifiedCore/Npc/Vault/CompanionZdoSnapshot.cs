using System;
using UnityEngine;

namespace FiresCore.Npc.Vault
{
    /// <summary>
    /// Pure-function bridge between a live <see cref="CompanionController"/>
    /// (and the ZDO behind it) and an inert <see cref="CompanionSaveData"/>
    /// snapshot blob.
    /// 
    /// Two operations:
    ///   • <see cref="Capture"/>    — read everything we need from the live
    ///                                companion to be able to reconstitute it.
    ///   • <see cref="Apply"/>      — write a snapshot back onto a live
    ///                                companion (typically a fresh respawn).
    /// 
    /// PHASE 2 DESIGN
    /// --------------
    /// The field-by-field copy logic already exists in
    /// <see cref="CompanionVault.BuildSaveData"/> and
    /// <see cref="CompanionVault.RestoreCompanion"/>. Both are essentially
    /// pure functions today — they read/write the controller and the ZDO
    /// but do not perform any IO and do not consult
    /// <see cref="Modules.Vault.VaultOfKnowledge"/>.
    /// 
    /// This class is a thin facade on top of that pair. The reason for the
    /// facade rather than a direct call:
    ///   1. Phase 3+ callers depend on <c>CompanionZdoSnapshot.Capture</c> /
    ///      <c>Apply</c>. When Phase 6 rewrites the field-copy code in a
    ///      new home (or splits it differently), call sites don't change.
    ///   2. Consistent <c>Capture</c> / <c>Apply</c> naming reads cleaner
    ///      at the call site than <c>BuildSaveData</c> / <c>RestoreCompanion</c>.
    ///   3. We get one place to harden error semantics (null-safe, single
    ///      try/catch) without polluting the field-copy implementation.
    /// 
    /// PHASE 2 STATUS
    /// --------------
    /// Delivered without production call sites. Consumers wired up in
    /// Phase 3 (death flow) and Phase 4 (login/logout flow). Until then
    /// this is dead code that compiles and ships safely alongside the
    /// existing vault path.
    /// 
    /// PURITY CONTRACT
    /// ---------------
    /// Neither method touches the filesystem, the JSON vault, or
    /// <see cref="PlayerCompanionStorage"/>. They operate strictly on
    /// the live <see cref="CompanionController"/> ?
    /// <see cref="CompanionSaveData"/> bridge. Storage is the caller's
    /// responsibility — typically <c>roster.UpsertEntry(player, ...)</c>
    /// after a Capture.
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
        /// Reads the live state of <paramref name="companion"/> into an
        /// inert <see cref="CompanionSaveData"/> blob suitable for storage
        /// in <see cref="PlayerCompanionStorage"/> or any other persistent
        /// medium.
        /// 
        /// Returns null if:
        ///   • <paramref name="companion"/> is null,
        ///   • the companion lacks a <c>companionId</c> (not yet initialised),
        ///   • the underlying field-copy throws (extremely defensive — the
        ///     existing implementation has its own per-field try/catch).
        /// 
        /// On null return the caller must NOT persist anything. Persisting
        /// a null/incomplete snapshot for a pending-respawn entry is
        /// exactly the failure mode that produced the wrong-name /
        /// wrong-scale bug, so the storage layer also rejects null
        /// snapshots when <c>IsPendingRespawn=true</c> at upsert time
        /// (see <see cref="PlayerCompanionStorage.UpsertEntry"/>).
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
        /// Convenience helper used by the respawn flow: capture the
        /// current state of <paramref name="companion"/> and stamp it into
        /// the player's roster as a death-snapshot entry, including a
        /// wall-clock <paramref name="respawnDeadlineUtc"/> deadline.
        /// 
        /// Returns true if the snapshot was captured AND persisted to the
        /// roster. False on any failure (caller should fall back to the
        /// existing vault save path during the side-by-side phase).
        /// 
        /// This method exists in Phase 2 because it's a one-liner
        /// equivalent of "Capture ? mutate entry ? UpsertEntry" and Phase 3
        /// will call it three times. Defining it here keeps the storage /
        /// snapshot pieces colocated.
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
        /// clamped to ? 0. Returns 0 (i.e. "respawn now") when the deadline
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
