using System;
using System.Collections.Generic;

namespace FiresCore.Npc.Vault
{
    /// <summary>
    /// The owner's persistent intent for a companion, separate from runtime status such as death or zone
    /// loading. Following, Staying and Stationed are all in the world and differ only in position and idle
    /// behavior. Dismissed removes the companion from the world while the entry keeps its snapshot for a later
    /// recall. An entry is only ever deleted by the roster screen's Remove button, never by dismiss, death or
    /// logout.
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
        /// Out of the world with its ZDO destroyed, but still owned and recallable from the roster, so the entry keeps
        /// a complete snapshot. Any spawn of a dismissed companion turns it back to <see cref="Following"/>
        /// (CompanionRosterWriter.OnRespawned).
        /// </summary>
        Dismissed = 3,
    }

    /// <summary>
    /// One owned companion, stored in the roster JSON under Player.m_customData. <see cref="Snapshot"/> is the
    /// authoritative backup and is re-captured on death and logout; an entry pending respawn with no snapshot
    /// is unrecoverable and is removed rather than replaced by a default companion. <see cref="ServerWorldUid"/>
    /// is fixed at creation so companions never follow a character to another world, and
    /// <see cref="RespawnDeadlineUtcTicks"/> is wall-clock so a crash doesn't restart the timer.
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
        /// The companion's last captured state, refreshed on death, on logout and periodically while alive, so it
        /// remains a backup rather than a second authority. Never null while <see cref="IsPendingRespawn"/> is true.
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
