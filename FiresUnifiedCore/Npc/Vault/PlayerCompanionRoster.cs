using System;
using System.Collections.Generic;

namespace FiresCore.Npc.Vault
{
    /// <summary>
    /// High-level state of a companion entry in the player's roster.
    /// 
    /// This is the field that records the OWNER'S persistent intent for
    /// the companion. It is decoupled from runtime status (alive/dead,
    /// loaded/unloaded zone) which is tracked separately on
    /// <see cref="PlayerCompanionRosterEntry.IsPendingRespawn"/>.
    /// 
    /// SEMANTICS — read carefully, these distinguish three almost-identical
    /// commands the player can issue from the radial menu / roster UI:
    /// 
    ///   • <see cref="Following"/> / <see cref="Staying"/> / <see cref="Stationed"/>
    ///     all describe IN-WORLD companions. Their ZDO exists, their
    ///     GameObject loads when the player is in range. They differ only
    ///     in where they are and what they do when not in combat.
    /// 
    ///   • <see cref="Dismissed"/> is fundamentally different: the
    ///     companion is REMOVED FROM THE WORLD. Its live ZDO is destroyed.
    ///     The roster entry persists with a full <see cref="PlayerCompanionRosterEntry.Snapshot"/>
    ///     so the player can recall the companion later (re-spawning a new
    ///     ZDO and Apply'ing the snapshot — same code path as crash-recovery
    ///     respawn). Dismiss is the player's "I'm done with this companion
    ///     for now but don't lose it" command.
    /// 
    /// THE ROSTER ENTRY IS NEVER DELETED EXCEPT BY THE EXPLICIT
    /// "Remove" BUTTON ON THE ROSTER SCREEN. Dismiss does not delete.
    /// Death does not delete. Logout does not delete. The only path that
    /// calls <see cref="PlayerCompanionStorage.RemoveEntry"/> is the
    /// roster-screen Remove confirmation.
    /// </summary>
    public enum CompanionFollowState
    {
        /// <summary>
        /// Companion is in the world and follows the player.
        /// Default state after taming.
        /// </summary>
        Following = 0,

        /// <summary>
        /// Companion is in the world but parked at a specific location.
        /// HomePosition fields on the snapshot record where it should stand.
        /// </summary>
        Staying = 1,

        /// <summary>
        /// Companion is permanently stationed as a static NPC.
        /// Stationed position on the snapshot records the station location.
        /// </summary>
        Stationed = 2,

        /// <summary>
        /// Companion is OUT OF WORLD — its live ZDO has been destroyed —
        /// but the player still owns it and can recall it from the roster
        /// screen. The roster entry must carry a complete
        /// <see cref="PlayerCompanionRosterEntry.Snapshot"/> so the recall
        /// path can reconstitute identity, equipment, progression, etc.
        /// 
        /// AUTO-PROMOTE RULE: a Dismissed companion entering the world —
        /// regardless of which path produced the spawn (recall button,
        /// crash-recovery respawn, debug command) — is automatically flipped
        /// to <see cref="Following"/>. This rule lives in
        /// <c>CompanionRosterWriter.OnRespawned</c>; callers do not need to
        /// do anything special on recall, the post-spawn write handles it.
        /// (Dismissed is structurally impossible to die-from anyway, since
        /// the companion isn't in the world to take damage.)
        /// </summary>
        Dismissed = 3,
    }

    /// <summary>
    /// One entry per companion the player owns. Lives inside
    /// <see cref="PlayerCompanionRoster"/>, which is JSON-serialised into a
    /// single key in <c>Player.m_customData</c>.
    /// 
    /// FIELD OWNERSHIP
    /// ---------------
    /// Read the tracker doc (Docs/COMPANION_SAVE_REFACTOR_TRACKER.md) for
    /// the full data-ownership matrix. In short:
    /// 
    ///   • <see cref="Snapshot"/> is the AUTHORITATIVE backup of the
    ///     companion's state. While the companion is alive in the world it
    ///     may be slightly stale (the live ZDO is the freshest copy); but
    ///     it MUST be re-captured on death and on logout so that respawn /
    ///     re-login can reconstitute the companion exactly. If <see cref="IsPendingRespawn"/>
    ///     is true and <see cref="Snapshot"/> is null, the companion is
    ///     UNRECOVERABLE — the entry should be removed rather than a default
    ///     companion be synthesised in its place. (This is the structural
    ///     fix for the "respawned with wrong name / wrong scale" bug.)
    /// 
    ///   • <see cref="ServerWorldUid"/> is stamped on creation and never
    ///     mutated. On restore, mismatching UIDs cause the entry to be
    ///     skipped silently — companions don't follow the player into a
    ///     different server.
    /// 
    ///   • <see cref="RespawnDeadlineUtcTicks"/> is wall-clock-absolute
    ///     instead of "seconds remaining" so a crash mid-timer doesn't
    ///     reset the timer to its full duration on next login.
    /// </summary>
    [Serializable]
    public class PlayerCompanionRosterEntry
    {
        /// <summary>
        /// Stable identifier for this companion across deaths. Matches
        /// <see cref="CompanionController.companionId"/> /
        /// <see cref="CompanionSaveData.CompanionId"/>.
        /// </summary>
        public string CompanionId;

        /// <summary>
        /// World UID (<c>ZNet.instance.GetWorldUID()</c>) of the server this
        /// companion was first owned on. Stamped once at creation and never
        /// mutated. Used to prevent companions from being restored on the
        /// wrong server when a character file is moved between worlds.
        /// </summary>
        public long ServerWorldUid;

        /// <summary>
        /// Persistent follow-state command (Following / Staying / Stationed).
        /// This is what the OWNER set, not a derived runtime flag — the
        /// runtime <see cref="CompanionAI"/> follow flag may temporarily be
        /// flipped by death/teleport flows but the persistent intent here
        /// survives those.
        /// </summary>
        public CompanionFollowState FollowState;

        /// <summary>
        /// True if the companion died and is on a respawn timer.
        /// While true, <see cref="Snapshot"/> holds the FULL state to
        /// reconstitute and <see cref="RespawnDeadlineUtcTicks"/> is the
        /// wall-clock deadline.
        /// </summary>
        public bool IsPendingRespawn;

        /// <summary>
        /// Wall-clock UTC tick count at which the respawn timer expires.
        /// 0 if not pending. Stored as ticks (not seconds remaining) so
        /// the timer survives crashes — on next login we recompute
        /// `remaining = max(0, deadline - now)` and resume.
        /// </summary>
        public long RespawnDeadlineUtcTicks;

        /// <summary>
        /// Authoritative snapshot of the companion's last known state.
        /// Re-uses the existing <see cref="CompanionSaveData"/> shape so
        /// migration from the JSON vault is a copy operation.
        /// 
        /// Refreshed:
        ///   • on death (full capture before the ZDO is destroyed),
        ///   • on logout (capture for live companions so cross-session works),
        ///   • on configurable periodic flush while alive (stale-by-design
        ///     so it stays a backup, not a duplicate authority).
        /// 
        /// This field is non-null whenever <see cref="IsPendingRespawn"/>
        /// is true — the contract is enforced by the storage layer in
        /// <see cref="PlayerCompanionStorage"/>.
        /// </summary>
        public CompanionSaveData Snapshot;

        /// <summary>
        /// UTC tick count at which this entry was last written. Useful for
        /// debug tooling and migration ordering decisions; not consumed by
        /// gameplay logic.
        /// </summary>
        public long LastUpdatedUtcTicks;
    }

    /// <summary>
    /// Top-level container serialised to a single key in
    /// <c>Player.m_customData</c>. One instance per character file.
    /// 
    /// Schema is versioned via <see cref="SchemaVersion"/>; the storage
    /// layer refuses to read entries written under a version it doesn't
    /// understand and falls back to fresh state, leaving the stored blob
    /// intact for forensic inspection.
    /// </summary>
    [Serializable]
    public class PlayerCompanionRoster
    {
        /// <summary>
        /// Currently-supported schema version. Bump on breaking changes;
        /// keep loaders for older versions if data needs to migrate.
        /// </summary>
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion = CurrentSchemaVersion;

        /// <summary>
        /// All companions this player owns. Order is not significant.
        /// Indexed by <see cref="PlayerCompanionRosterEntry.CompanionId"/>
        /// when needed; the storage layer provides indexed lookups.
        /// </summary>
        public List<PlayerCompanionRosterEntry> Entries = new List<PlayerCompanionRosterEntry>();
    }
}
