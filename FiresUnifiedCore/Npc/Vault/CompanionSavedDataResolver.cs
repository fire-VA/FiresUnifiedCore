using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Vault
{
    /// <summary>
    /// The single read path for companion save data. The owner's roster in Player.m_customData wins, filtered
    /// to the current server's world UID, with the legacy JSON vault as the fallback; callers get nothing
    /// rather than a fabricated default when neither has data. Roster respawn deadlines are wall-clock and
    /// are converted to seconds remaining, so a deadline that passed during a crash respawns immediately.
    /// Dismissed companions are left out of the full list but can still be resolved by id.
    /// </summary>
    public static class CompanionSavedDataResolver
    {
        private const string LogPrefix = "[CompanionSavedDataResolver]";

        /// <summary>
        /// Diagnostic flag; off by default. When on, logs every resolution
        /// with which source was used. Production callers should rely on
        /// their own higher-level log lines.
        /// </summary>
        public static bool VerboseLogging = false;

        /// <summary>
        /// Resolves all eligible companion snapshots for the owning player
        /// at login / first-restore time. Excludes Dismissed entries and
        /// entries from other servers. Falls back to vault if the roster
        /// is empty or the owner Player handle isn't available.
        /// </summary>
        public static List<CompanionSaveData> ResolveAllForPlayer(long ownerPlayerId)
        {
            // Try roster first.
            var owner = ResolvePlayer(ownerPlayerId);
            if (owner != null)
            {
                // Use TryGetRoster so we can distinguish:
                //   rosterKeyExists=true  - the key is in m_customData (even if Entries is empty)
                //   rosterKeyExists=false - key was never written (genuine first-login / pre-migration)
                // This matters for the vault-fallback decision below.
                bool rosterKeyExists = PlayerCompanionStorage.TryGetRoster(owner, out var roster);

                if (rosterKeyExists)
                {
                    var list = new List<CompanionSaveData>();
                    int skippedDismissed = 0;
                    int skippedOtherServer = 0;
                    int skippedNullSnap = 0;
                    bool needsRosterSave = false;

                    long currentWorldUid = PlayerCompanionStorage.GetCurrentServerWorldUid();

                    if (roster?.Entries != null)
                    {
                        foreach (var entry in roster.Entries)
                        {
                            if (entry == null) continue;

                            if (entry.Snapshot == null) { skippedNullSnap++; continue; }

                            if (entry.FollowState == CompanionFollowState.Dismissed)
                            {
                                skippedDismissed++;
                                continue;
                            }

                            // Self-healing migration
                            // ServerWorldUid == 0 means the entry was created before ZNet
                            // was ready (GetWorldUID returned 0 at tame-time). Now that we
                            // know the current world UID, stamp it so the entry is properly
                            // bound going forward. We still admit the entry this session
                            // (the companion belongs here — we just couldn't record it at
                            // the time). Future sessions on a different world will correctly
                            // filter it out via BelongsToCurrentServer.
                            if (entry.ServerWorldUid == 0 && currentWorldUid != 0)
                            {
                                Debug.Log($"{LogPrefix} Self-healing world UID: stamping {currentWorldUid} on legacy entry {entry.CompanionId} ({entry.Snapshot?.CompanionName})");
                                entry.ServerWorldUid = currentWorldUid;
                                needsRosterSave = true;
                            }

                            if (!PlayerCompanionStorage.BelongsToCurrentServer(entry))
                            {
                                skippedOtherServer++;
                                continue;
                            }

                            // Translate roster deadline - snapshot seconds-remaining
                            // so the downstream pipeline (which uses the snapshot
                            // fields) sees consistent values regardless of source.
                            StampPendingFields(entry);

                            list.Add(entry.Snapshot);
                        }
                    }

                    // Persist any UID migration stamps so they survive the next logout.
                    if (needsRosterSave)
                        PlayerCompanionStorage.SaveRoster(owner, roster);

                    if (VerboseLogging)
                        Debug.Log($"{LogPrefix} ResolveAllForPlayer({ownerPlayerId}) -> roster: {list.Count} eligible, " +
                                  $"skipped dismissed={skippedDismissed}, otherServer={skippedOtherServer}, nullSnapshot={skippedNullSnap}");

                    // CRITICAL: the roster key exists, so we own this player's companion
                    // list.  Do NOT fall back to the vault — the vault has no world-binding
                    // and would return companions from OTHER worlds if all roster entries
                    // were correctly filtered by ServerWorldUid above.
                    return list;
                }

                if (VerboseLogging)
                    Debug.Log($"{LogPrefix} ResolveAllForPlayer({ownerPlayerId}) -> roster key not found, trying vault");
            }

            // Roster key was never written (genuine first-login / pre-Phase-3 migration).
            // Fall back to vault as the only available source.
            try
            {
                var fromVault = FiresCore.Bridge.CompanionVaultBridge.GetCompanionsFor(ownerPlayerId);
                if (VerboseLogging)
                    Debug.Log($"{LogPrefix} ResolveAllForPlayer({ownerPlayerId}) -> vault: {fromVault?.Count ?? 0} entries");
                return fromVault;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} Vault read failed for player {ownerPlayerId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Resolves a single companion snapshot by id. Used by per-companion
        /// respawn lookups in <see cref="CompanionRespawnManager"/>.
        /// 
        /// Unlike <see cref="ResolveAllForPlayer"/>, this DOES return
        /// Dismissed-state entries (the recall flow needs them). It still
        /// respects the server-binding filter.
        /// </summary>
        public static CompanionSaveData ResolveByCompanionId(long ownerPlayerId, string companionId)
        {
            if (string.IsNullOrEmpty(companionId)) return null;

            // Roster first.
            var owner = ResolvePlayer(ownerPlayerId);
            if (owner != null)
            {
                var entry = PlayerCompanionStorage.GetEntry(owner, companionId);
                if (entry != null && entry.Snapshot != null && PlayerCompanionStorage.BelongsToCurrentServer(entry))
                {
                    StampPendingFields(entry);
                    if (VerboseLogging)
                        Debug.Log($"{LogPrefix} ResolveByCompanionId({companionId}) -> roster ({entry.FollowState}, pending={entry.IsPendingRespawn})");
                    return entry.Snapshot;
                }
            }

            // Vault fallback.
            try
            {
                var companions = FiresCore.Bridge.CompanionVaultBridge.GetCompanionsFor(ownerPlayerId);
                if (companions == null) return null;
                for (int i = 0; i < companions.Count; i++)
                {
                    var companion = companions[i];
                    if (companion != null && companion.CompanionId == companionId)
                    {
                        if (VerboseLogging)
                            Debug.Log($"{LogPrefix} ResolveByCompanionId({companionId}) -> vault");
                        return companion;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} Vault read failed for {companionId}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Copies the roster's wall-clock deadline / pending flag into the
        /// snapshot's seconds-remaining / pending fields, so downstream
        /// code (which only knows the snapshot shape) gets consistent
        /// values regardless of source. A deadline already in the past
        /// produces 0 (= "respawn immediately"), which is exactly what
        /// crash recovery wants.
        /// </summary>
        private static void StampPendingFields(PlayerCompanionRosterEntry entry)
        {
            if (entry?.Snapshot == null) return;
            entry.Snapshot.IsPendingRespawn = entry.IsPendingRespawn;
            entry.Snapshot.RespawnTimeRemaining = entry.IsPendingRespawn
                ? CompanionZdoSnapshot.ComputeRemainingRespawnSeconds(entry.RespawnDeadlineUtcTicks)
                : 0f;
            // CRITICAL: always stamp the roster entry's authoritative FollowState into
            // the snapshot before returning it. The snapshot's IsFollowing was written
            // by GetPersistentFollowIntent() at the moment the snapshot was captured,
            // which can be stale if the PlayerFollowingRegistry was wiped by a player
            // death/respawn, or if the follow command was issued just before the
            // companion died and hadn't fully propagated yet. The entry.FollowState
            // is updated synchronously on every CommandFollow / CommandStay, so it
            // is always authoritative. Without this stamp, RestoreCompanionFromVault
            // sees IsFollowing=false and respawns a following companion in Stay mode.
            entry.Snapshot.IsFollowing = entry.FollowState == CompanionFollowState.Following;
        }

        /// <summary>
        /// Resolves every roster entry for the owning player as
        /// <see cref="CompanionSaveData"/> snapshots, INCLUDING
        /// <see cref="CompanionFollowState.Dismissed"/> entries. Used by
        /// the roster screen UI which needs to display dismissed
        /// companions so the player can recall them. Falls back to the
        /// vault JSON if the roster is empty / unavailable.
        /// </summary>
        public static List<CompanionSaveData> ResolveAllForRosterUI(long ownerPlayerId)
        {
            var owner = ResolvePlayer(ownerPlayerId);
            if (owner != null)
            {
                // TryGetRoster returns true when the roster key exists in m_customData
                // (even if Entries is empty), and false only when the key has never
                // been written (first-time login / pre-migration). When the key exists
                // we trust the roster completely — a deliberately empty roster means
                // the player removed all companions and we must NOT fall back to the
                // vault, because the vault removal can be slightly delayed and would
                // cause removed companions to reappear.
                bool rosterExists = PlayerCompanionStorage.TryGetRoster(owner, out var roster);
                if (rosterExists)
                {
                    var list = new List<CompanionSaveData>();
                    if (roster?.Entries != null)
                    {
                        foreach (var entry in roster.Entries)
                        {
                            if (entry?.Snapshot == null) continue;
                            if (!PlayerCompanionStorage.BelongsToCurrentServer(entry)) continue;
                            StampPendingFields(entry);
                            list.Add(entry.Snapshot);
                        }
                    }
                    return list; // return even if empty — vault fallback would be wrong
                }
            }

            // Roster key doesn't exist - first-time login or pre-migration player.
            // Fall back to vault (debug mirror) to seed the initial display.
            try
            {
                var fromVault = FiresCore.Bridge.CompanionVaultBridge.GetCompanionsFor(ownerPlayerId);
                return fromVault ?? new List<CompanionSaveData>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} ResolveAllForRosterUI vault fallback failed for {ownerPlayerId}: {ex.Message}");
                return new List<CompanionSaveData>();
            }
        }

        /// <summary>
        /// Resolves a Player object for an owner id.
        /// <see cref="Player.m_localPlayer"/> first (single-player and
        /// the local-restore-on-login case), then walks
        /// <see cref="Player.GetAllPlayers"/> for the multiplayer host
        /// case. Returns null if no match — caller falls back to vault.
        /// </summary>
        private static Player ResolvePlayer(long ownerPlayerId)
        {
            try
            {
                var local = Player.m_localPlayer;
                if (local != null && local.GetPlayerID() == ownerPlayerId) return local;

                var all = Player.GetAllPlayers();
                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        var player = all[i];
                        if (player != null && player.GetPlayerID() == ownerPlayerId) return player;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} ResolvePlayer({ownerPlayerId}) threw: {ex.Message}");
            }
            return null;
        }
    }
}
