using System;
using UnityEngine;

namespace FiresCore.Npc.Vault
{
    /// <summary>
    /// High-level writer API for the per-player companion roster. Every
    /// lifecycle event (tame, stay, follow, station, dismiss, recall,
    /// death, respawn, periodic refresh, logout) routes through one of the
    /// named methods on this class.
    /// 
    /// WHY A WRITER FACADE
    /// -------------------
    /// Without it, the policy of "always carry a fresh snapshot on roster
    /// upsert", "refuse to lose Snapshot data", "stamp ServerWorldUid on
    /// first ownership", etc. would have to be re-implemented at every
    /// call site — exactly the kind of distributed-state-machine sprawl
    /// that produced the original wrong-name / wrong-scale bug. With a
    /// facade:
    /// 
    ///   • Each call site reads as a one-line statement of intent
    ///     ("CompanionRosterWriter.OnDismissed(owner, companion)").
    ///   • Phase 6 can rewrite the underlying storage without touching
    ///     callers.
    ///   • The Dismiss/Remove distinction (preserve vs destroy roster
    ///     entry) is enforced HERE, not at the UI layer where it could
    ///     drift.
    /// 
    /// PHASE 3 USAGE PLAN
    /// ------------------
    /// Each method is a single-call entry point that does:
    ///   1. Capture a fresh <see cref="CompanionSaveData"/> from the live
    ///      companion (where applicable).
    ///   2. Look up or create the roster entry.
    ///   3. Set the appropriate flags / state.
    ///   4. Persist back to <see cref="Player.m_customData"/> via
    ///      <see cref="PlayerCompanionStorage"/>.
    /// 
    /// Side-by-side with the existing JSON vault: callers of these methods
    /// are EXPECTED to ALSO be calling <c>CompanionVault.SaveCompanion</c>
    /// in the same code path during Phase 3. Phase 4 flips reads over;
    /// Phase 6 retires the dual-write.
    /// 
    /// THREADING
    /// ---------
    /// All methods run on the Unity main thread. No locking; the storage
    /// layer is single-threaded.
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

        // ?????????????????????????????? HOOK POINTS ???????????????????????

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
        /// Player recalled a previously-dismissed companion from the roster
        /// screen. Caller has already spawned a new live companion; this
        /// method captures the post-spawn snapshot and stamps the entry
        /// back to Following.
        /// 
        /// Note: callers do NOT need to call this if the recall went
        /// through <see cref="CompanionRespawnManager.RequestImmediateRespawn"/>
        /// — the post-respawn <see cref="OnRespawned"/> auto-promotes
        /// Dismissed ? Following automatically. This method exists for
        /// the edge case where a recall happens with a live-existing
        /// CompanionController in hand (e.g. teleporting an alive Stay-mode
        /// companion that wasn't actually Dismissed).
        /// </summary>
        public static bool OnRecalled(Player owner, CompanionController companion)
        {
            return Upsert(owner, companion, CompanionFollowState.Following, isPendingRespawn: false, deadlineTicks: 0L,
                          contextLabel: "OnRecalled");
        }

        /// <summary>
        /// Companion died. Captures the death snapshot, marks
        /// <see cref="PlayerCompanionRosterEntry.IsPendingRespawn"/>=true,
        /// and stamps an absolute wall-clock deadline (so a crash mid-timer
        /// resumes correctly on next login).
        /// 
        /// <paramref name="respawnDelaySeconds"/> is the configured respawn
        /// delay; the deadline is computed as <c>UtcNow + delay</c>.
        /// 
        /// FollowState is preserved from any existing entry — a Staying
        /// companion that died is still Staying-and-pending-respawn, not
        /// reset to Following.
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
        /// Companion respawn fired and a fresh ZDO/companion exists.
        /// Clears <see cref="PlayerCompanionRosterEntry.IsPendingRespawn"/>
        /// and re-captures the snapshot from the new live companion (so
        /// any post-respawn state — restored equipment, etc. — is current).
        /// 
        /// FollowState is preserved from the prior entry, with ONE
        /// exception: a prior <see cref="CompanionFollowState.Dismissed"/>
        /// is auto-promoted to <see cref="CompanionFollowState.Following"/>.
        /// Dismissed means "out of world" by definition; if a respawn fired
        /// for a Dismissed entry, the player is recalling them, and they
        /// should land as Following. This makes the recall flow a single
        /// action (just call <c>RequestImmediateRespawn</c>) rather than
        /// requiring callers to flip state separately before spawning.
        /// 
        /// All other states preserve: Following ? Following, Staying ?
        /// Staying, Stationed ? Stationed.
        /// </summary>
        public static bool OnRespawned(Player owner, CompanionController companion)
        {
            if (owner == null) return false;

            var snapshot = CompanionZdoSnapshot.Capture(companion);
            if (snapshot == null) return false;

            var existing = PlayerCompanionStorage.GetEntry(owner, snapshot.CompanionId);
            var followState = existing?.FollowState ?? CompanionFollowState.Following;

            // Auto-promote Dismissed ? Following. Dismissed entries can
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

        // ?????????????????????????????? INTERNALS ?????????????????????????

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
