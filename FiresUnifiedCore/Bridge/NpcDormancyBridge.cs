using System;
using System.Collections.Generic;
using FiresCore.Npc;

namespace FiresCore.Bridge
{
    /// <summary>
    /// How a dormant NPC re-enters the world.
    /// </summary>
    public enum DormancyKind
    {
        /// <summary>A following companion that went dormant on owner logout. Auto-recalls
        /// (spawns next to the owner) on the owner's next login.</summary>
        LoggedOutFollower = 0,

        /// <summary>Explicitly dismissed by the owner. Stays dormant until an explicit Recall
        /// command from the roster UI — never auto-spawns on login.</summary>
        Dismissed = 1,

        /// <summary>Died and is waiting out its respawn timer. Auto-recalls once
        /// <see cref="DormantNpcEntry.RecallDeadlineUtcTicks"/> has elapsed.</summary>
        DeadPendingRespawn = 2,

        /// <summary>Currently ALIVE in the world (owned + spawned). Not dormant — this entry exists
        /// purely so the kennel is the authoritative persistent store for the companion's identity
        /// (name / loadout / appearance / stats) exactly like the old vault was, so a live companion
        /// whose world ZDO reloads incomplete can restore itself. Never auto-spawned by the restore
        /// engine (the live world ZDO owns the runtime instance); a server-side reconciler keeps it
        /// current, and death/logout/dismiss flip it to the matching dormant kind.</summary>
        Alive = 3,
    }

    /// <summary>
    /// One dormant NPC: a Core <see cref="NpcSaveState"/> snapshot plus the small amount of
    /// recall POLICY the dormancy layer owns. Mirrors the kennel's <c>KennelEntry</c> but is the
    /// Core-level, store-agnostic shape the restore/respawn engine works in.
    /// </summary>
    public class DormantNpcEntry
    {
        /// <summary>Stable NPC id (== <see cref="NpcSaveState.NpcId"/>) — the entry's primary key.</summary>
        public string NpcId;

        /// <summary>Why this NPC is dormant + how it should come back.</summary>
        public DormancyKind Kind;

        /// <summary>Absolute UTC ticks at which recall becomes ready. 0 = ready immediately
        /// (logout/dismiss). &gt;0 = a death-respawn deadline. Absolute wall-clock so a process
        /// restart resumes the timer with no bookkeeping (no "seconds remaining" drift).</summary>
        public long RecallDeadlineUtcTicks;

        /// <summary>The Core-owned intrinsic NPC state, round-tripped via
        /// <c>CompanionController.CaptureState/ApplyState</c>.</summary>
        public NpcSaveState Snapshot;

        /// <summary>Last write timestamp (DateTime.UtcNow.Ticks). Diagnostic / freshness signal;
        /// stamped by the store on every upsert.</summary>
        public long LastUpdatedUtcTicks;

        /// <summary>True when this entry is eligible to auto-spawn now: a dormant kind (never
        /// <see cref="DormancyKind.Alive"/> — those already have a live world instance — and never
        /// <see cref="DormancyKind.Dismissed"/>) whose deadline (if any) has elapsed.</summary>
        public bool IsRecallReady(long nowUtcTicks)
            => Kind != DormancyKind.Dismissed
               && Kind != DormancyKind.Alive
               && nowUtcTicks >= RecallDeadlineUtcTicks;
    }

    /// <summary>
    /// The single seam between Core's death/respawn/restore engine and whatever actually stores
    /// dormant NPCs for a given deployment:
    ///
    /// <list type="bullet">
    ///   <item><description><b>Standalone (FiresCompanions)</b> registers the per-(player,world)
    ///     <c>CompanionKennel</c> ZDO store.</description></item>
    ///   <item><description><b>Integrated (RPGMaker + Marketplace)</b> registers its vault store.</description></item>
    /// </list>
    ///
    /// <para>Core's engine NEVER references a concrete store — it only talks to this bridge. That is
    /// the "Shared → Core, never fork per-mod" contract from the persistence redesign spec
    /// (<c>COMPANION_PERSISTENCE_REDESIGN.md §5/§7</c>): one restore engine, one dormant-store
    /// interface, store implementation supplied by the frontend.</para>
    ///
    /// <para>While no provider is registered every accessor is a null-safe no-op
    /// (<see cref="IsAvailable"/> false, reads return empty/null, writes drop). That keeps a
    /// Core-only deployment from throwing; it just has no dormant persistence until a frontend
    /// plugs one in.</para>
    ///
    /// <para>Player-keyed by design. World/character scoping is the provider's concern — the kennel
    /// makes it structural (the ZDO lives in this world's <c>.db</c>); a vault provider would scope
    /// however it chooses. Threading: all calls are on the Unity main thread (server-side).</para>
    /// </summary>
    public static class NpcDormancyBridge
    {
        // ── Provider delegates (set by the frontend at init) ──
        /// <summary>Insert or replace (by <see cref="DormantNpcEntry.NpcId"/>) a dormant entry for a player.</summary>
        public static Action<long, DormantNpcEntry> StoreEntry;
        /// <summary>Fetch one dormant entry by id, or null.</summary>
        public static Func<long, string, DormantNpcEntry> GetEntry;
        /// <summary>List all dormant entries for a player (empty list, never null, when none).</summary>
        public static Func<long, List<DormantNpcEntry>> ListEntries;
        /// <summary>Remove a dormant entry by id. Returns true if one was removed.</summary>
        public static Func<long, string, bool> RemoveEntry;
        /// <summary>True when a real dormant store is wired up.</summary>
        public static Func<bool> IsAvailableProvider;

        /// <summary>True when a frontend has registered a dormant store.</summary>
        public static bool IsAvailable
        {
            get { try { return IsAvailableProvider != null && IsAvailableProvider(); } catch { return false; } }
        }

        public static void Store(long playerId, DormantNpcEntry entry)
        {
            if (playerId == 0L || entry == null || string.IsNullOrEmpty(entry.NpcId)) return;
            try { StoreEntry?.Invoke(playerId, entry); } catch { }
        }

        public static DormantNpcEntry Get(long playerId, string npcId)
        {
            if (playerId == 0L || string.IsNullOrEmpty(npcId)) return null;
            try { return GetEntry?.Invoke(playerId, npcId); } catch { return null; }
        }

        public static List<DormantNpcEntry> List(long playerId)
        {
            if (playerId == 0L) return new List<DormantNpcEntry>();
            try { return ListEntries?.Invoke(playerId) ?? new List<DormantNpcEntry>(); }
            catch { return new List<DormantNpcEntry>(); }
        }

        public static bool Remove(long playerId, string npcId)
        {
            if (playerId == 0L || string.IsNullOrEmpty(npcId)) return false;
            try { return RemoveEntry?.Invoke(playerId, npcId) ?? false; } catch { return false; }
        }
    }
}
