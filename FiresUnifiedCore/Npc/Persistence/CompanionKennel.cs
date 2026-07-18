using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using FiresCore.Bridge;

namespace FiresCore.Npc.Persistence
{
    /// <summary>
    /// The canonical dormant-NPC store: one server-owned, persistent data-ZDO per player per world.
    /// Holds companions that have no live creature in the world — logged-out followers,
    /// dead/pending-respawn, and dismissed.
    ///
    /// <para><b>Why this lives in Core.</b> Both companion frontends (FiresCompanions standalone and
    /// FiresRPGmaker integrated) need the same dormant persistence. Per the project rule — anything
    /// 2+ Fires mods depend on lives in <c>FiresUnifiedCore</c>, never forked per-mod — the store is
    /// shared infrastructure and belongs here, not in a frontend. It was prototyped in FiresCompanions
    /// during the clone; this is its destination. Core registers itself as the
    /// <see cref="NpcDormancyBridge"/> provider, so the store is ALWAYS available — no
    /// "vault present ⇒ disable kennel" coexistence gate.</para>
    ///
    /// <para><b>(character, world) scoping is structural.</b> The kennel ZDO physically lives in this
    /// world's <c>.db</c> keyed by <c>kennel_owner = playerId</c>. A character joining a different
    /// world finds no kennel for that (playerId, worldUid) tuple → nothing restores. No soft
    /// <c>==0</c> escape hatches.</para>
    ///
    /// <para><b>Storage shape.</b> <c>kennel_owner</c> (long) + <c>kennel_data</c> (byte[] ZPackage of
    /// <see cref="DormantNpcEntry"/> list). Server-authoritative: new ZDOs originate on the server.</para>
    /// </summary>
    [HarmonyPatch]
    public static class CompanionKennel
    {
        /// <summary>
        /// Prefab name tagging the kennel ZDO. Intentionally NOT registered in
        /// <c>ZNetScene.m_namedPrefabs</c> — kennels are pure data ZDOs that never instantiate
        /// (ZNetScene logs one benign "missing prefab" per kennel at load). Name kept identical to
        /// the FiresCompanions prototype so any world already carrying kennels reads them back.
        /// </summary>
        public const string PrefabName = "FiresCompanionsKennel";

        private static readonly int PrefabHash = PrefabName.GetStableHashCode();

        private const string KeyOwner = "kennel_owner";
        private const string KeyData  = "kennel_data";

        /// <summary>
        /// Routed RPC carrying a kennel write from a client to the server. The kennel ZDO can only
        /// be created/owned server-side, but the WRITE frequently originates on a client: the death
        /// handler runs on the companion's ZDO owner (usually the player standing next to it), and
        /// logout capture runs on the leaving client. Without this transport those writes were
        /// silently refused off-server ("GetOrCreate called off-server") and the death/logout
        /// snapshot was dropped — the respawn then had nothing to restore (nameless, gearless).
        /// Payload reuses <see cref="CompanionKennelSerializer"/> (a one-entry list).
        /// </summary>
        private const string RpcStoreName = "FiresCompanions_KennelStore";

        /// <summary>
        /// Routed RPC carrying a kennel REMOVE from a client to the server. Same transport reason as
        /// <see cref="RpcStoreName"/>: client command paths (e.g. CommandFollow clearing a stale
        /// Dismissed/DeadPendingRespawn entry) run on a machine that doesn't own the kennel ZDO, so a
        /// direct Remove was silently dropped — leaving a lingering DeadPendingRespawn that can drive a
        /// duplicate server-side respawn.
        /// </summary>
        private const string RpcRemoveName = "FiresCompanions_KennelRemove";

        /// <summary>Per-event verbose logging for kennel reads/writes.</summary>
        public static bool VerboseLogging = false;

        // ── Provider registration ─────────────────────────────────────────────────────

        /// <summary>
        /// Register Core's kennel as the <see cref="NpcDormancyBridge"/> provider. Done at world
        /// start so it's wired before any death/restore. Idempotent (re-assigns the same delegates).
        /// Always reports available — the kennel is Core, always loaded.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        public static void ZNet_Start_RegisterProvider()
        {
            // Transport first, independent of which provider wins: the server must accept
            // client-originated kennel writes (death/logout snapshots captured on the ZDO owner).
            try
            {
                if (ZNet.instance != null && ZNet.instance.IsServer())
                {
                    if (ZRoutedRpc.instance != null)
                    {
                        ZRoutedRpc.instance.Register<long, ZPackage>(RpcStoreName, RPC_Store);
                        ZRoutedRpc.instance.Register<long, string>(RpcRemoveName, RPC_Remove);
                        Debug.Log($"[CompanionKennel] Registered server RPCs '{RpcStoreName}' / '{RpcRemoveName}' — accepting client-forwarded kennel writes/removes + death respawns.");
                    }
                    else
                    {
                        Debug.LogWarning($"[CompanionKennel] ZRoutedRpc.instance null at ZNet.Start — '{RpcStoreName}' NOT registered; client kennel writes will be dropped.");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionKennel] Failed to register {RpcStoreName}: {ex.Message}");
            }

            // Core's kennel is the DEFAULT dormant store. A frontend may register its own provider
            // first (e.g. during the transition, FiresCompanions' adapter, or later a vault
            // provider) — respect that and stand by rather than clobbering it. Once the frontend
            // adapter is removed, this default takes over automatically with no other change.
            if (NpcDormancyBridge.IsAvailableProvider != null)
            {
                Debug.Log("[CompanionKennel] A dormant-store provider is already registered; Core kennel standing by as default.");
                return;
            }
            NpcDormancyBridge.IsAvailableProvider = () => true;
            NpcDormancyBridge.StoreEntry  = Store;
            NpcDormancyBridge.GetEntry    = Get;
            NpcDormancyBridge.ListEntries = GetEntries;
            NpcDormancyBridge.RemoveEntry = Remove;
            Debug.Log("[CompanionKennel] Registered as NpcDormancyBridge provider (Core dormant store, always available).");
        }

        // ── Lookup ────────────────────────────────────────────────────────────────────

        /// <summary>Find this player's kennel ZDO, or <c>null</c> if none exists in this world.</summary>
        public static ZDO Find(long playerId)
        {
            if (playerId == 0L || ZDOMan.instance == null) return null;
            var all = ScanAllKennels();
            for (int i = 0; i < all.Count; i++)
            {
                var zdo = all[i];
                if (zdo == null || !zdo.IsValid()) continue;
                if (zdo.GetLong(KeyOwner, 0L) == playerId) return zdo;
            }
            return null;
        }

        /// <summary>True if this player has a kennel ZDO in this world.</summary>
        public static bool Exists(long playerId) => Find(playerId) != null;

        /// <summary>
        /// Find this player's kennel ZDO; create a new server-owned one if none exists.
        /// <b>Server-only</b> — returns <c>null</c> off-server because new ZDO ownership must
        /// originate from the server in a multiplayer world.
        /// </summary>
        public static ZDO GetOrCreate(long playerId)
        {
            if (playerId == 0L) return null;
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                Debug.LogWarning("[CompanionKennel] GetOrCreate called off-server — refused");
                return null;
            }
            var existing = Find(playerId);
            if (existing != null) return existing;

            try
            {
                var zdo = ZDOMan.instance.CreateNewZDO(Vector3.zero, PrefabHash);
                if (zdo == null) return null;
                zdo.SetPrefab(PrefabHash);   // CreateNewZDO doesn't set it; do it explicitly.
                zdo.Persistent = true;       // Save to disk with the world.
                zdo.Set(KeyOwner, playerId);
                zdo.Set(KeyData, Array.Empty<byte>());
                return zdo;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionKennel] GetOrCreate({playerId}) failed: {ex.Message}");
                return null;
            }
        }

        // ── Read ────────────────────────────────────────────────────────────────────

        /// <summary>Read all dormant entries for a player. Empty list if no kennel exists.</summary>
        public static List<DormantNpcEntry> GetEntries(long playerId)
        {
            var zdo = Find(playerId);
            if (zdo == null) return new List<DormantNpcEntry>();
            try { return CompanionKennelSerializer.Deserialize(zdo.GetByteArray(KeyData)); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionKennel] GetEntries({playerId}) failed: {ex.Message}");
                return new List<DormantNpcEntry>();
            }
        }

        /// <summary>Get a specific entry by NPC id. <c>null</c> if not found.</summary>
        public static DormantNpcEntry Get(long playerId, string npcId)
        {
            if (string.IsNullOrEmpty(npcId)) return null;
            var entries = GetEntries(playerId);
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e != null && string.Equals(e.NpcId, npcId, StringComparison.Ordinal)) return e;
            }
            return null;
        }

        // ── Write ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Insert (or replace by <see cref="DormantNpcEntry.NpcId"/>) an entry in this player's
        /// kennel. Creates the kennel ZDO on first write. Stamps <see cref="DormantNpcEntry.LastUpdatedUtcTicks"/>.
        /// </summary>
        public static void Store(long playerId, DormantNpcEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.NpcId)) return;

            // The kennel ZDO is server-owned, but the authoritative CAPTURE often happens on a
            // client (death runs on the companion's ZDO owner; logout runs on the leaving client).
            // Off-server, forward the entry to the server instead of dropping it.
            if (ZNet.instance != null && !ZNet.instance.IsServer())
            {
                ForwardStoreToServer(playerId, entry);
                return;
            }

            var zdo = GetOrCreate(playerId);
            if (zdo == null) return;
            try
            {
                var entries = CompanionKennelSerializer.Deserialize(zdo.GetByteArray(KeyData));
                int idx = entries.FindIndex(e => e != null
                    && string.Equals(e.NpcId, entry.NpcId, StringComparison.Ordinal));
                entry.LastUpdatedUtcTicks = DateTime.UtcNow.Ticks;
                if (idx >= 0) entries[idx] = entry;
                else entries.Add(entry);
                zdo.Set(KeyData, CompanionKennelSerializer.Serialize(entries));
                if (VerboseLogging)
                    Debug.Log($"[CompanionKennel] Stored {entry.NpcId} ({entry.Kind}) for player {playerId}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionKennel] Store({playerId}, {entry.NpcId}) failed: {ex.Message}");
            }
        }

        private static void ForwardStoreToServer(long playerId, DormantNpcEntry entry)
        {
            try
            {
                var rpc = ZRoutedRpc.instance;
                if (rpc == null)
                {
                    Debug.LogWarning($"[CompanionKennel] Cannot forward kennel write for {entry.NpcId} — routed RPC unavailable; entry dropped");
                    return;
                }
                entry.LastUpdatedUtcTicks = DateTime.UtcNow.Ticks;
                var payload = CompanionKennelSerializer.Serialize(new List<DormantNpcEntry> { entry });
                rpc.InvokeRoutedRPC(rpc.GetServerPeerID(), RpcStoreName, playerId, new ZPackage(payload));
                // Non-verbose on purpose: this is a per-death/per-tame event (low volume) and it is the
                // load-bearing step for companion persistence on a dedi — keep it visible in the log.
                Debug.Log($"[CompanionKennel] Forwarded {entry.NpcId} ({entry.Kind}) to server for player {playerId}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionKennel] ForwardStoreToServer({playerId}, {entry?.NpcId}) failed: {ex.Message}");
            }
        }

        private static void RPC_Store(long sender, long playerId, ZPackage pkg)
        {
            try
            {
                if (pkg == null || playerId == 0L) return;
                var entries = CompanionKennelSerializer.Deserialize(pkg.GetArray());
                if (entries == null || entries.Count == 0 || entries[0] == null) return;
                var entry = entries[0];
                Store(playerId, entry);
                Debug.Log($"[CompanionKennel] RPC_Store: stored {entry.NpcId} ({entry.Kind}) for player {playerId} from peer {sender}");

                // A client-forwarded death entry means the companion died on a machine that cannot reach
                // the server-owned kennel to run its own respawn — its local respawn loop would never find
                // this entry. Schedule the respawn HERE, on the server, where the kennel is readable. The
                // deadline is absolute UTC (crash-resumable); translate it to a seconds-from-now delay.
                if (entry.Kind == DormancyKind.DeadPendingRespawn && entry.Snapshot != null)
                {
                    float delay = (float)Math.Max(0.0,
                        (new DateTime(entry.RecallDeadlineUtcTicks, DateTimeKind.Utc) - DateTime.UtcNow).TotalSeconds);
                    FiresCore.Npc.CompanionRespawnManager.Instance?.ScheduleRespawn(
                        entry.NpcId, playerId, entry.Snapshot.PrefabName, delay, ZDOID.None);
                    Debug.Log($"[CompanionKennel] Scheduled server-side respawn for {entry.NpcId} in {delay:F0}s (client-forwarded death).");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionKennel] RPC_Store failed: {ex.Message}");
            }
        }

        private static void RPC_Remove(long sender, long playerId, string npcId)
        {
            try
            {
                if (playerId == 0L || string.IsNullOrEmpty(npcId)) return;
                if (Remove(playerId, npcId) && VerboseLogging)
                    Debug.Log($"[CompanionKennel] RPC_Remove: removed {npcId} for player {playerId} from peer {sender}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionKennel] RPC_Remove failed: {ex.Message}");
            }
        }

        /// <summary>Remove an entry by NPC id. Returns <c>true</c> if removed.</summary>
        public static bool Remove(long playerId, string npcId)
        {
            if (string.IsNullOrEmpty(npcId)) return false;

            // The kennel ZDO is server-owned; a client-side Remove can't touch it. Forward to the server
            // (mirrors Store). Returns false locally — the removal happens asynchronously server-side.
            if (ZNet.instance != null && !ZNet.instance.IsServer())
            {
                try
                {
                    var rpc = ZRoutedRpc.instance;
                    if (rpc != null)
                        rpc.InvokeRoutedRPC(rpc.GetServerPeerID(), RpcRemoveName, playerId, npcId);
                    else
                        Debug.LogWarning($"[CompanionKennel] Cannot forward kennel remove for {npcId} — routed RPC unavailable");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CompanionKennel] ForwardRemoveToServer({playerId}, {npcId}) failed: {ex.Message}");
                }
                return false;
            }

            var zdo = Find(playerId);
            if (zdo == null) return false;
            try
            {
                var entries = CompanionKennelSerializer.Deserialize(zdo.GetByteArray(KeyData));
                int removed = entries.RemoveAll(e => e != null
                    && string.Equals(e.NpcId, npcId, StringComparison.Ordinal));
                if (removed == 0) return false;
                zdo.Set(KeyData, CompanionKennelSerializer.Serialize(entries));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionKennel] Remove({playerId}, {npcId}) failed: {ex.Message}");
                return false;
            }
        }

        // ── Capture convenience (Capture → Store) ─────────────────────────────────────

        /// <summary>
        /// Capture a live companion's <see cref="NpcSaveState"/> and store it dormant in one call —
        /// the write path for dismiss / logout / death. <b>Server-only</b>; the caller destroys the
        /// live companion afterwards. <paramref name="recallDeadlineUtcTicks"/> is 0 for
        /// ready-now (logout/dismiss) or an absolute UTC deadline for a death-respawn timer.
        /// </summary>
        public static void CaptureAndStore(long playerId, CompanionController companion, DormancyKind kind, long recallDeadlineUtcTicks)
        {
            if (companion == null || playerId == 0L) return;
            try
            {
                var snapshot = companion.CaptureState();
                if (snapshot == null)
                {
                    Debug.LogWarning($"[CompanionKennel] CaptureState returned null for player {playerId} — not stored");
                    return;
                }
                Store(playerId, new DormantNpcEntry
                {
                    NpcId                  = snapshot.NpcId,
                    Kind                   = kind,
                    RecallDeadlineUtcTicks = recallDeadlineUtcTicks,
                    Snapshot               = snapshot,
                });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionKennel] CaptureAndStore({playerId}) failed: {ex.Message}");
            }
        }

        // ── Internals ─────────────────────────────────────────────────────────────────

        private static List<ZDO> ScanAllKennels()
        {
            var found = new List<ZDO>();
            try
            {
                int index = 0;
                while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(PrefabName, found, ref index)) { }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionKennel] ScanAllKennels failed: {ex.Message}");
            }
            return found;
        }
    }
}
