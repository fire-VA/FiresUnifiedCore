using System;
using UnityEngine;

namespace FiresCore.Npc.Vault
{
    /// <summary>
    /// The one write path for the per-player companion roster. Each lifecycle event (tame, stay, follow,
    /// station, dismiss, recall, death, respawn, periodic refresh, logout) has a named method that captures a
    /// fresh snapshot, updates the entry and persists it through <see cref="PlayerCompanionStorage"/>, so rules
    /// like never dropping snapshot data, stamping the world UID on first ownership, and dismiss versus remove
    /// live in one place. Main thread only.
    /// </summary>
    public static class CompanionRosterWriter
    {
        private const string LogPrefix = "[CompanionRosterWriter]";

        /// <summary>
        /// Diagnostic flag, off by default. When on, logs every roster
        /// transition. The storage layer has its own VerboseLogging flag
        /// for raw read/write tracing — this one logs the high-level
        /// intent ("Dismissed companion X", "Recalled companion Y").
        /// </summary>
        public static bool VerboseLogging = false;

        // HOOK POINTS

        /// <summary>
        /// Called the first time a companion enters this player's
        /// ownership (taming completes, debug-spawn assigns owner, etc.).
        /// Stamps <see cref="PlayerCompanionRosterEntry.ServerWorldUid"/>
        /// from the current world; this stamp is never mutated again.
        /// </summary>
        public static bool OnFirstOwned(Player owner, CompanionController companion)
        {
            return Upsert(owner, companion, CompanionFollowState.Following, isPendingRespawn: false, deadlineTicks: 0L,
                          contextLabel: "OnFirstOwned");
        }

        /// <summary>
        /// Player commanded the companion to follow them. Refreshes the
        /// snapshot so any equipment / progression changes since the last
        /// write are captured.
        /// </summary>
        public static bool OnFollowCommand(Player owner, CompanionController companion)
        {
            return Upsert(owner, companion, CompanionFollowState.Following, isPendingRespawn: false, deadlineTicks: 0L,
                          contextLabel: "OnFollowCommand");
        }

        /// <summary>
        /// Player commanded the companion to stay at its current location.
        /// HomePosition fields on the captured snapshot record where.
        /// </summary>
        public static bool OnStayCommand(Player owner, CompanionController companion)
        {
            return Upsert(owner, companion, CompanionFollowState.Staying, isPendingRespawn: false, deadlineTicks: 0L,
                          contextLabel: "OnStayCommand");
        }

        /// <summary>
        /// Companion was permanently stationed as an NPC at a placed station.
        /// StationedPosition fields on the snapshot record where.
        /// </summary>
        public static bool OnStationedAsNpc(Player owner, CompanionController companion)
        {
            return Upsert(owner, companion, CompanionFollowState.Stationed, isPendingRespawn: false, deadlineTicks: 0L,
                          contextLabel: "OnStationedAsNpc");
        }

        /// <summary>
        /// Player dismissed the companion. The CALLER is responsible for
        /// destroying the live ZDO afterwards — this method ONLY captures
        /// the snapshot and updates roster state to <see cref="CompanionFollowState.Dismissed"/>.
        /// 
        /// Capture happens BEFORE the caller destroys the live companion;
        /// don't re-order, or the snapshot will be empty.
        /// </summary>
        public static bool OnDismissed(Player owner, CompanionController companion)
        {
            return Upsert(owner, companion, CompanionFollowState.Dismissed, isPendingRespawn: false, deadlineTicks: 0L,
                          contextLabel: "OnDismissed");
        }

        /// <summary>
        /// A dismissed companion was recalled and a live companion already exists: captures its snapshot and marks the
        /// entry Following. Recalls that go through CompanionRespawnManager.RequestImmediateRespawn don't need this,
        /// because <see cref="OnRespawned"/> already promotes Dismissed to Following.
        /// </summary>
        public static bool OnRecalled(Player owner, CompanionController companion)
        {
            return Upsert(owner, companion, CompanionFollowState.Following, isPendingRespawn: false, deadlineTicks: 0L,
                          contextLabel: "OnRecalled");
        }

        /// <summary>
        /// A companion died: captures the death snapshot, marks it pending respawn with a wall-clock deadline of now plus
        /// <paramref name="respawnDelaySeconds"/> so a crash resumes the timer, and keeps its follow state.
        /// </summary>
        public static bool OnDeath(Player owner, CompanionController companion, float respawnDelaySeconds)
        {
            var snapshot = CompanionZdoSnapshot.Capture(companion);
            if (snapshot == null)
            {
                Debug.LogWarning($"{LogPrefix} OnDeath: Capture returned null for {(companion == null ? "<null>" : companion.companionName)}; not upserting roster.");
                return false;
            }
            return OnDeathWithSnapshot(owner, snapshot, respawnDelaySeconds);
        }

        /// <summary>
        /// Death-flow overload for callers that have already built a
        /// snapshot AND mutated it (e.g. cleared equipment because the
        /// items were dropped to the tombstone). Skips the internal
        /// Capture so the caller's mutations are preserved.
        /// </summary>
        public static bool OnDeathWithSnapshot(Player owner, CompanionSaveData snapshot, float respawnDelaySeconds)
        {
            if (owner == null) return false;
            if (snapshot == null || string.IsNullOrEmpty(snapshot.CompanionId))
            {
                Debug.LogWarning($"{LogPrefix} OnDeathWithSnapshot: null or id-less snapshot; refusing.");
                return false;
            }

            // Preserve existing FollowState if any. Default to Following
            // for the never-before-seen edge case.
            var existing = PlayerCompanionStorage.GetEntry(owner, snapshot.CompanionId);
            var followState = existing?.FollowState ?? CompanionFollowState.Following;

            DateTime deadline = DateTime.UtcNow.AddSeconds(Mathf.Max(0f, respawnDelaySeconds));

            var entry = new PlayerCompanionRosterEntry
            {
                CompanionId             = snapshot.CompanionId,
                ServerWorldUid          = existing?.ServerWorldUid != 0 && existing != null
                                            ? existing.ServerWorldUid
                                            : PlayerCompanionStorage.GetCurrentServerWorldUid(),
                FollowState             = followState,
                IsPendingRespawn        = true,
                RespawnDeadlineUtcTicks = deadline.Ticks,
                Snapshot                = snapshot,
            };

            bool ok = PlayerCompanionStorage.UpsertEntry(owner, entry);
            if (VerboseLogging)
                Debug.Log($"{LogPrefix} OnDeathWithSnapshot {snapshot.CompanionName} ({snapshot.CompanionId}): pending respawn at UTC {deadline:O} (in {respawnDelaySeconds:F1}s) — ok={ok}");
            return ok;
        }

        /// <summary>
        /// A respawn produced a fresh companion: clears the pending-respawn flag and re-captures the snapshot.
        /// Follow state is kept, except that Dismissed becomes Following, since respawning a dismissed companion
        /// is a recall.
        /// </summary>
        public static bool OnRespawned(Player owner, CompanionController companion)
        {
            if (owner == null) return false;

            var snapshot = CompanionZdoSnapshot.Capture(companion);
            if (snapshot == null) return false;

            var existing = PlayerCompanionStorage.GetEntry(owner, snapshot.CompanionId);
            var followState = existing?.FollowState ?? CompanionFollowState.Following;

            // Auto-promote Dismissed -> Following. Dismissed entries can
            // only re-enter the world via recall, and recalled companions
            // belong with the player by default.
            if (followState == CompanionFollowState.Dismissed)
                followState = CompanionFollowState.Following;

            return Upsert(owner, companion, followState, isPendingRespawn: false, deadlineTicks: 0L,
                          contextLabel: "OnRespawned",
                          preCapturedSnapshot: snapshot);
        }

        /// <summary>
        /// Periodic refresh / logout flush — re-captures the live
        /// companion's state into the roster snapshot WITHOUT changing
        /// FollowState or IsPendingRespawn. Used by the periodic tick and
        /// by the Game.Logout prefix to keep the roster's snapshots fresh
        /// for crash-resilient restore.
        /// 
        /// If no roster entry exists yet (e.g. a companion that was loaded
        /// but never went through a state-changing command), this method
        /// auto-creates one with FollowState=Following so the roster
        /// becomes self-healing.
        /// </summary>
        public static bool RefreshLiveSnapshot(Player owner, CompanionController companion)
        {
            if (owner == null) return false;

            var snapshot = CompanionZdoSnapshot.Capture(companion);
            if (snapshot == null) return false;

            var existing = PlayerCompanionStorage.GetEntry(owner, snapshot.CompanionId);
            var followState        = existing?.FollowState ?? CompanionFollowState.Following;
            bool wasPendingRespawn = existing?.IsPendingRespawn ?? false;
            long deadline          = existing?.RespawnDeadlineUtcTicks ?? 0L;

            // Don't clobber a pending-respawn flag from a periodic refresh —
            // a live companion shouldn't be marked pending, but if existing
            // says it is, the caller is doing something unusual; preserve
            // the flag and let the death/respawn flow be the only writer
            // that touches it.
            return Upsert(owner, companion, followState, wasPendingRespawn, deadline,
                          contextLabel: "RefreshLiveSnapshot",
                          preCapturedSnapshot: snapshot);
        }

        /// <summary>
        /// Explicit Remove from the roster screen — the ONLY entry point
        /// that deletes a companion's data from the player's roster. The
        /// CALLER is responsible for destroying the live ZDO if one exists.
        /// </summary>
        public static bool OnRosterRemove(Player owner, string companionId)
        {
            if (owner == null || string.IsNullOrEmpty(companionId)) return false;
            bool ok = PlayerCompanionStorage.RemoveEntry(owner, companionId);
            if (VerboseLogging)
                Debug.Log($"{LogPrefix} OnRosterRemove {companionId}: ok={ok}");
            return ok;
        }

        // INTERNALS

        /// <summary>
        /// Shared upsert path. Captures a fresh snapshot from
        /// <paramref name="companion"/> unless one was already supplied as
        /// <paramref name="preCapturedSnapshot"/> (used by callers that
        /// already captured for their own logging / contract checks).
        /// </summary>
        private static bool Upsert(
            Player owner,
            CompanionController companion,
            CompanionFollowState followState,
            bool isPendingRespawn,
            long deadlineTicks,
            string contextLabel,
            CompanionSaveData preCapturedSnapshot = null)
        {
            if (owner == null)
            {
                if (VerboseLogging) Debug.LogWarning($"{LogPrefix} {contextLabel}: null owner.");
                return false;
            }

            var snapshot = preCapturedSnapshot ?? CompanionZdoSnapshot.Capture(companion);
            if (snapshot == null)
            {
                Debug.LogWarning($"{LogPrefix} {contextLabel}: Capture returned null for {(companion == null ? "<null>" : companion.companionName)}; not upserting roster.");
                return false;
            }

            var existing = PlayerCompanionStorage.GetEntry(owner, snapshot.CompanionId);

            var entry = new PlayerCompanionRosterEntry
            {
                CompanionId             = snapshot.CompanionId,
                ServerWorldUid          = existing?.ServerWorldUid != 0 && existing != null
                                            ? existing.ServerWorldUid
                                            : PlayerCompanionStorage.GetCurrentServerWorldUid(),
                FollowState             = followState,
                IsPendingRespawn        = isPendingRespawn,
                RespawnDeadlineUtcTicks = deadlineTicks,
                Snapshot                = snapshot,
            };

            if (entry.ServerWorldUid == 0)
                Debug.LogWarning($"{LogPrefix} {contextLabel}: stamping ServerWorldUid=0 for {snapshot.CompanionName} ({snapshot.CompanionId}) — ZNet.GetWorldUID() returned 0. Entry will be self-healed on next restore.");

            bool ok = PlayerCompanionStorage.UpsertEntry(owner, entry);
            if (VerboseLogging)
                Debug.Log($"{LogPrefix} {contextLabel} {snapshot.CompanionName} ({snapshot.CompanionId}): state={followState}, pending={isPendingRespawn}, ok={ok}");
            return ok;
        }
    }
}
