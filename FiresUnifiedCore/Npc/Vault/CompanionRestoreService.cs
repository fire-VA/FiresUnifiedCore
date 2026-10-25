using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using FiresCore.Lifecycle;
using FiresCore.Bridge;

namespace FiresCore.Npc.Vault
{
    /// <summary>
    /// Restores a player's companions on the server when they connect. Once CompanionVaultBridge raises
    /// PlayerAnnouncedToServer and config sync has finished for that peer, it walks the player's saved
    /// companions, adopts any that already exist in ZDOMan and spawns the rest from their saved data. It only
    /// writes ZDO state, follow intent included, so it behaves the same on a dedicated server, where remote
    /// players have no Player object, as on a listen host.
    /// </summary>
    [HarmonyPatch]
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
    public static class CompanionRestoreService
    {
        private const string LogPrefix = "[CompanionRestoreService]";

        // Per-player session bookkeeping. Keyed on the network UID, which
        // equals Player.GetPlayerID() for the connecting peer.
        private static readonly HashSet<long> _restoredPlayerIds = new HashSet<long>();
        private static readonly HashSet<long> _pendingPlayerIds  = new HashSet<long>();


        // Subscribe (server-side, once) to the Core player-announced hook. A provider (the host's
        // Marketplace vault in integrated mode, or the standalone kennel) raises it after a player
        // announces; we run the server-side companion restore off it, with no Marketplace dependency.
        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        public static void ZNet_Start_SubscribePlayerAnnounced()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            FiresCore.Bridge.CompanionVaultBridge.PlayerAnnouncedToServer -= OnPlayerAnnounced;
            FiresCore.Bridge.CompanionVaultBridge.PlayerAnnouncedToServer += OnPlayerAnnounced;
        }

        private static void OnPlayerAnnounced(long playerId)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (playerId == 0L) return;

            if (_pendingPlayerIds.Contains(playerId))
            {
                Debug.Log($"{LogPrefix} Restore already pending for {playerId}; ignoring duplicate announce.");
                return;
            }
            if (_restoredPlayerIds.Contains(playerId))
            {
                Debug.Log($"{LogPrefix} Already restored {playerId} this session; skipping.");
                return;
            }

            _pendingPlayerIds.Add(playerId);
            // peerUid == playerId for the connecting peer (see session-bookkeeping note above); the
            // restore is gated on config-sync completion via CompanionVaultBridge.IsRestoreReadyForPeer.
            FiresCore.FiresUnifiedCore.Instance?.StartCoroutine(WaitForConfigSyncThenRestore(playerId, playerId));
        }

        /// <summary>
        /// Polls <see cref="ServerConfigFileWatcher.HasCompletedConfigSync"/> until
        /// it returns true (or the fallback timeout elapses), then runs
        /// <see cref="RestoreForPlayerServerSide"/>. Bails if the peer disconnects.
        ///
        /// 150s ceiling is intentionally generous — config sync on a slow client
        /// can take 60+ seconds, and missing the signal is far less harmful than
        /// firing restore mid-config-push.
        /// </summary>
        private static System.Collections.IEnumerator WaitForConfigSyncThenRestore(long peerUid, long playerId)
        {
            const float pollSec = 0.5f;
            const float maxWaitSec = 150f;
            float waited = 0f;

            Debug.Log($"{LogPrefix} Waiting for config sync to complete on peer {peerUid} before restoring player {playerId} (fallback {maxWaitSec:0}s)…");

            while (waited < maxWaitSec)
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer())
                {
                    Debug.Log($"{LogPrefix} Server gone while waiting for config-sync on peer {peerUid}; skipping restore.");
                    _pendingPlayerIds.Remove(playerId);
                    yield break;
                }
                if (peerUid != 0L && ZNet.instance.GetPeer(peerUid) == null)
                {
                    Debug.Log($"{LogPrefix} Peer {peerUid} disconnected while waiting for config-sync; skipping restore for player {playerId}.");
                    _pendingPlayerIds.Remove(playerId);
                    yield break;
                }
                if (FiresCore.Bridge.CompanionVaultBridge.IsRestoreReadyForPeer(peerUid))
                {
                    Debug.Log($"{LogPrefix} Config sync complete on peer {peerUid} after {waited:0.0}s; running RestoreForPlayerServerSide for player {playerId}.");
                    break;
                }
                yield return new WaitForSeconds(pollSec);
                waited += pollSec;
            }

            if (waited >= maxWaitSec)
            {
                Debug.LogWarning($"{LogPrefix} Config-sync signal never arrived for peer {peerUid} within {maxWaitSec:0}s — running RestoreForPlayerServerSide anyway for player {playerId} (companions may briefly race ongoing config traffic).");
            }

            try
            {
                RestoreForPlayerServerSide(playerId);
                _restoredPlayerIds.Add(playerId);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} RestoreForPlayerServerSide threw for {playerId}: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                _pendingPlayerIds.Remove(playerId);
            }
        }

        /// <summary>
        /// Drop session bookkeeping for a peer when they disconnect, so a
        /// genuine reconnect within the same server lifetime triggers a
        /// fresh restore on the next AnnouncePlayerInfo.
        /// </summary>
        [HarmonyPatch(typeof(ZNet), "Disconnect")]
        [HarmonyPostfix]
        public static void ZNet_Disconnect_Postfix(ZNetPeer peer)
        {
            if (peer == null) return;
            long playerId = peer.m_uid;
            if (playerId == 0L) return;
            _pendingPlayerIds.Remove(playerId);
            _restoredPlayerIds.Remove(playerId);
            // Drop the config-sync-complete flag too so a reconnecting peer's
            // next companion-restore waits for the fresh sync to complete.
            FiresCore.Bridge.CompanionVaultBridge.ClearRestoreGateFor(peer.m_uid);
        }

        // ──────────────────────────────────────────────────────────────────
        // Server-side restore — pure ZDO + vault, no Player MonoBehaviour
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Server-side restore for <paramref name="playerId"/>: finds the player's character position and calls
        /// CompanionPatches.RestoreCompanionsFromVault with no Player, which uses the saved follow flag, writes
        /// companion_wasfollowing directly and spawns near that position.
        /// </summary>
        public static void RestoreForPlayerServerSide(long playerId)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (playerId == 0L) return;

            Vector3 ownerPos = ResolvePeerCharacterPosition(playerId);
            if (ownerPos == Vector3.zero)
            {
                Debug.LogWarning($"{LogPrefix} Could not resolve character position for player {playerId} — using world origin (followers won't be teleported to player; vault-spawn fallback may also place them at the wrong height)");
            }

            Debug.Log($"{LogPrefix} Restoring companions for player {playerId} (server-authoritative, ownerPos={ownerPos})");

            try
            {
                CompanionPatches.DedupeCompanionZdosInWorld();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} DedupeCompanionZdosInWorld threw: {ex.Message}");
            }

            // ── Persistent-mode adopt pass ────────────────────────────────
            // Walk ZDOMan for every companion ZDO whose companion_owner
            // matches this player and claim ownership server-side. For
            // followers, rewrite the ZDO position to the player so they
            // re-materialise next to them when zones load. This pass is
            // independent of the vault — companions persist in the world
            // as ZDOs (Bug 1 architecture), so the world IS the canonical
            // source of truth. The vault below is the migration fallback
            // for players whose last logout was under legacy mode (no
            // companion ZDOs in the world, only vault entries) or for
            // dismissed-recall / expired-respawn cases.
            int adoptedCount = AdoptOwnedCompanionZdos(playerId, ownerPos);

            // ── Dormancy-pass: THE single login-restore path for despawned companions ──
            // Spawns recall-ready dormant companions from the dormant-store seam (kennel in
            // standalone / vault in integrated, once it registers a provider). When a dormant
            // store is present it OWNS restore and the legacy vault/roster-spawn pass is skipped,
            // so the two can never double-spawn. When NO dormant store is registered we fall back
            // to the legacy pass unchanged — which is the current dual-mod profile (kennel disabled
            // by the vault), so this whole branch is a no-op there: zero behaviour change until the
            // kennel becomes the active store.
            int dormantSpawned = 0;
            if (NpcDormancyBridge.IsAvailable)
            {
                dormantSpawned = RestoreDormantViaSeam(playerId, ownerPos);
            }
            else
            {
                try
                {
                    CompanionPatches.RestoreCompanionsFromVault(player: null, playerId: playerId, ownerPos: ownerPos);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{LogPrefix} RestoreCompanionsFromVault threw for {playerId}: {ex.Message}\n{ex.StackTrace}");
                }
            }

            Debug.Log($"{LogPrefix} Restore complete for player {playerId}: adopted {adoptedCount} world ZDO(s), dormant-spawned {dormantSpawned}");
        }

        /// <summary>
        /// Scans <c>ZDOMan</c> for every companion ZDO owned by the given
        /// player and adopts it server-side. "Adopt" = claim ZDO ownership
        /// (so subsequent writes are authoritative) + for followers,
        /// rewrite the position to <paramref name="ownerPos"/> so they
        /// re-materialise next to the player when zones load. Stay-mode
        /// companions are left at their saved position.
        /// </summary>
        private static int AdoptOwnedCompanionZdos(long playerId, Vector3 ownerPos)
        {
            if (ZDOMan.instance == null) return 0;

            int adopted = 0;
            int followersTeleported = 0;
            var temp = new List<ZDO>();
            foreach (var prefabName in Core.CompanionZdoCensus.PrefabNames)
            {
                temp.Clear();
                int idx = 0;
                while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefabName, temp, ref idx)) { }

                foreach (var zdo in temp)
                {
                    if (zdo == null || !zdo.IsValid()) continue;
                    long zdoOwner = zdo.GetLong("companion_owner", 0L);
                    if (zdoOwner != playerId) continue;

                    // Skip stationed NPCs — they live in the world for
                    // everyone and shouldn't be teleported on player login.
                    if (zdo.GetBool("npc_stationed", false)) continue;

                    bool wasFollowing = zdo.GetBool("companion_wasfollowing", false);

                    // Stay-mode companions: do NOT touch the ZDO. No SetOwner,
                    // no SetPosition. Their ZDO state is already correct in the
                    // world; touching it forces a sync to the client during the
                    // already-saturated login window which has been observed
                    // to lock the client up (heavy-state old characters fail
                    // to spawn, fresh characters at the same coords spawn fine).
                    // We still count it as "adopted" for telemetry — the
                    // companion is logically restored, just nothing to write.
                    if (!wasFollowing)
                    {
                        adopted++;
                        continue;
                    }

                    // Follower: server claims ownership BEFORE the position
                    // write so the write isn't competing with closest-peer
                    // ownership flap mid-teleport.
                    zdo.SetOwner(ZDOMan.GetSessionID());

                    if (ownerPos != Vector3.zero)
                    {
                        zdo.SetPosition(ownerPos);
                        zdo.DataRevision++;
                        ZDOMan.instance.ForceSendZDO(zdo.m_uid);
                        followersTeleported++;
                    }
                    adopted++;
                }
            }

            if (adopted > 0)
                Debug.Log($"{LogPrefix} Adopted {adopted} existing companion ZDO(s) for player {playerId} (followers teleported to ownerPos: {followersTeleported})");
            return adopted;
        }

        // ──────────────────────────────────────────────────────────────────────────
        // Dormancy-pass — spawn recall-ready dormant companions from the seam
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Spawns this player's recall-ready dormant companions from <see cref="NpcDormancyBridge"/>. Dismissed entries wait
        /// for an explicit recall and unexpired respawns stay dormant. A companion already live in the world is skipped and
        /// its stale dormant copy cleared, and a failed spawn leaves the entry for the next login rather than dropping it.
        /// </summary>
        private static int RestoreDormantViaSeam(long playerId, Vector3 ownerPos)
        {
            var entries = NpcDormancyBridge.List(playerId);
            if (entries == null || entries.Count == 0)
            {
                // Load-bearing diagnostic: an empty store here is the difference between "the save
                // never happened" and "the restore didn't fire" — the two failure modes look identical
                // in-game (companion simply absent), so keep this visible without verbose.
                Debug.Log($"{LogPrefix} Dormant restore for player {playerId}: NO entries in the dormant store — nothing to restore.");
                return 0;
            }
            Debug.Log($"{LogPrefix} Dormant restore for player {playerId}: {entries.Count} dormant entr{(entries.Count == 1 ? "y" : "ies")} found.");

            long nowTicks = DateTime.UtcNow.Ticks;
            int spawned = 0;

            foreach (var entry in entries)
            {
                if (entry == null || entry.Snapshot == null) continue;
                if (entry.Kind == DormancyKind.Dismissed) continue;       // explicit Recall only
                if (!entry.IsRecallReady(nowTicks))                      // deadline not elapsed yet
                {
                    // The respawn timer must survive a restart / relog mid-timer (otherwise the companion stays
                    // dormant until the next login): re-arm it from the kennel's absolute deadline
                    if (entry.Kind == DormancyKind.DeadPendingRespawn)
                    {
                        float delay = (float)Math.Max(0.0, (new DateTime(entry.RecallDeadlineUtcTicks, DateTimeKind.Utc) - DateTime.UtcNow).TotalSeconds);
                        try { FiresCore.Npc.CompanionRespawnManager.Instance?.ScheduleRespawn(entry.NpcId, playerId, entry.Snapshot.PrefabName, delay, ZDOID.None); Debug.Log($"{LogPrefix} re-armed the respawn of {entry.NpcId} in {delay:F0}s (kennel deadline)"); }
                        catch (Exception ex) { Debug.LogWarning($"{LogPrefix} respawn re-arm for {entry.NpcId} failed: {ex.Message}"); }
                    }
                    continue;
                }

                // I1: never spawn a second instance of a companion that's already live.
                if (IsCompanionLiveInWorld(entry.NpcId))
                {
                    NpcDormancyBridge.Remove(playerId, entry.NpcId);
                    continue;
                }

                if (TrySpawnDormant(entry, ownerPos, out _))
                {
                    NpcDormancyBridge.Remove(playerId, entry.NpcId);
                    spawned++;
                }
                // else: leave the entry for a retry on the next announce (I3).
            }

            Debug.Log($"{LogPrefix} Dormant restore for player {playerId}: spawned {spawned} of {entries.Count}.");
            return spawned;
        }

        /// <summary>I1 guard: is a companion with this id currently instantiated in the world?</summary>
        private static bool IsCompanionLiveInWorld(string npcId)
        {
            if (string.IsNullOrEmpty(npcId)) return false;
            var all = CompanionController.AllCompanions;
            if (all == null) return false;
            foreach (var companion in all)
            {
                if (companion != null && string.Equals(companion.companionId, npcId, StringComparison.Ordinal))
                {
                    // A corpse must not count as live, or the respawn removes the kennel entry and spawns nothing
                    bool dead = companion.isDefeated;
                    try { var ch = companion.GetComponent<Character>(); if (ch != null && (ch.IsDead() || ch.GetHealth() <= 0f)) dead = true; } catch { }
                    if (dead) continue;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Instantiate the snapshot's prefab at <paramref name="ownerPos"/> and apply the dormant
        /// state. Returns true on success (entry may be consumed). Failure is non-fatal — the caller
        /// keeps the dormant entry for a retry.
        /// </summary>
        private static bool TrySpawnDormant(DormantNpcEntry entry, Vector3 ownerPos, out CompanionController controller)
        {
            controller = null;
            GameObject go = null;
            try
            {
                var snap = entry.Snapshot;
                if (string.IsNullOrEmpty(snap.PrefabName)) return false;

                var prefab = ZNetScene.instance?.GetPrefab(snap.PrefabName);
                if (prefab == null)
                {
                    Debug.LogWarning($"{LogPrefix} Dormant prefab '{snap.PrefabName}' not registered yet for {entry.NpcId} — retry next announce");
                    return false;
                }

                go = UnityEngine.Object.Instantiate(prefab, ownerPos, Quaternion.identity);
                if (go == null) return false;

                controller = go.GetComponent<CompanionController>();
                if (controller == null)
                {
                    Debug.LogWarning($"{LogPrefix} Dormant prefab '{snap.PrefabName}' has no CompanionController — entry consumed");
                    DestroySpawn(go);
                    return true;
                }

                controller.ApplyState(snap);
                Debug.Log($"{LogPrefix} Dormancy-spawned {snap.DisplayName ?? snap.NpcId} ({entry.Kind}) at {ownerPos}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} TrySpawnDormant({entry?.NpcId}) failed: {ex.Message}");
                // Never leave a half-applied instance alive — it would be a nameless, unowned blank
                // in the world while the dormant entry is retried (duplicating on success).
                DestroySpawn(go);
                controller = null;
                return false;
            }
        }

        private static void DestroySpawn(GameObject go)
        {
            if (go == null) return;
            try
            {
                if (ZNetScene.instance != null) ZNetScene.instance.Destroy(go);
                else UnityEngine.Object.Destroy(go);
            }
            catch { }
        }

        /// <summary>
        /// Player-initiated Recall of one dormant companion (the roster screen's Recall button).
        /// Spawns the dormant snapshot near the owner, forces it back to Following, and clears the
        /// dormant entry. <b>Server-only</b> (single-player / listen-host); a dedicated client would
        /// need a server RPC — refused off-server rather than orphaning a ZDO. Returns false when
        /// there's no dormant entry or the spawn fails. Replaces the old FiresCompanions
        /// <c>KennelRestore.RecallNow</c> now that the store lives in Core.
        /// </summary>
        public static bool RecallDormant(long playerId, string npcId, Vector3 spawnPos, Player owner)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                Debug.LogWarning($"{LogPrefix} RecallDormant needs server authority (single-player / listen-host); dedicated-client recall needs a server RPC — not wired.");
                return false;
            }
            if (playerId == 0L || string.IsNullOrEmpty(npcId)) return false;

            // I1: if it's somehow already live, just clear the stale dormant copy.
            if (IsCompanionLiveInWorld(npcId))
            {
                NpcDormancyBridge.Remove(playerId, npcId);
                return true;
            }

            var entry = NpcDormancyBridge.Get(playerId, npcId);
            if (entry == null)
            {
                Debug.LogWarning($"{LogPrefix} RecallDormant: no dormant entry for {playerId}/{npcId}");
                return false;
            }

            if (!TrySpawnDormant(entry, spawnPos, out var controller))
                return false;

            // Recall always means "come back and follow me" — override the dormant snapshot's
            // follow intent (a dismissed companion may have been Stay / not-following).
            try
            {
                if (controller != null && owner != null)
                    controller.CommandFollow(owner);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} RecallDormant CommandFollow failed for {npcId}: {ex.Message}");
            }

            NpcDormancyBridge.Remove(playerId, npcId);
            return true;
        }

        /// <summary>
        /// Spawn ONE dormant companion by id, restoring its snapshot state AS-IS (follow / stay /
        /// stationed preserved — NOT forced to follow, unlike <see cref="RecallDormant"/>). The
        /// kennel-backed path for the intra-session death-respawn timer: used when the legacy
        /// m_customData snapshot is absent (post-redesign, or a kennel-only death). Server-only.
        /// Returns true if spawned (or already live). Honors I1 (won't double a live instance).
        /// </summary>
        public static bool TrySpawnDormantById(long playerId, string npcId, Vector3 spawnPos)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return false;
            if (playerId == 0L || string.IsNullOrEmpty(npcId)) return false;

            if (IsCompanionLiveInWorld(npcId))
            {
                NpcDormancyBridge.Remove(playerId, npcId);
                return true;
            }

            var entry = NpcDormancyBridge.Get(playerId, npcId);
            if (entry == null) return false;

            if (!TrySpawnDormant(entry, spawnPos, out _)) return false;

            NpcDormancyBridge.Remove(playerId, npcId);
            return true;
        }

        /// <summary>
        /// Finds the server-side position of a player's character by matching the playerID stored in each
        /// connected peer's character ZDO. A peer's m_uid is its network id, not the player id, so matching on it
        /// always failed. Returns Vector3.zero when nothing matches.
        /// </summary>
        private static Vector3 ResolvePeerCharacterPosition(long playerId)
        {
            try
            {
                if (ZNet.instance == null || ZDOMan.instance == null) return Vector3.zero;

                var peers = ZNet.instance.GetPeers();
                if (peers == null) return Vector3.zero;

                foreach (var peer in peers)
                {
                    if (peer == null) continue;
                    if (peer.m_characterID == ZDOID.None) continue;

                    ZDO charZdo = ZDOMan.instance.GetZDO(peer.m_characterID);
                    if (charZdo == null || !charZdo.IsValid()) continue;

                    long pid = charZdo.GetLong("playerID", 0L);
                    if (pid == playerId)
                    {
                        return charZdo.GetPosition();
                    }
                }

                // Listen-host / single-player: the local host player is NOT in GetPeers() (those are
                // remote peers), so the scan above misses them. Fall back to the live local Player —
                // without this, the local player's restore resolves position 0 and followers spawn at
                // world origin instead of next to the player.
                var localPlayer = Player.m_localPlayer;
                if (localPlayer != null && localPlayer.GetPlayerID() == playerId)
                    return localPlayer.transform.position;

                return Vector3.zero;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} ResolvePeerCharacterPosition({playerId}) threw: {ex.Message}");
                return Vector3.zero;
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Public: manual force-restore (debug command)
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Force a companion restore for the local player NOW. Only works
        /// on a listen-host or single-player (<c>IsServer()</c> on the
        /// local client). On a dedicated client this is a no-op with a
        /// warning. Replaces the old
        /// <c>CompanionPatches.ForceRestoreCompanions</c>.
        /// </summary>
        public static void ForceRestoreForLocalPlayer()
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                Debug.LogWarning($"{LogPrefix} ForceRestoreForLocalPlayer: no local player");
                return;
            }
            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                Debug.LogWarning($"{LogPrefix} ForceRestoreForLocalPlayer: only the server can force a companion restore (use a listen-host or single-player)");
                return;
            }
            if (!PlayerSpawnGate.IsReadyForCustomDataWrite(player))
            {
                Debug.LogWarning($"{LogPrefix} ForceRestoreForLocalPlayer: spawn gate not open — try again after the loading screen clears");
                return;
            }

            long playerId = player.GetPlayerID();
            _restoredPlayerIds.Remove(playerId);
            _pendingPlayerIds.Remove(playerId);

            try { CompanionPatches.DedupeCompanionZdosInWorld(); }
            catch (Exception ex) { Debug.LogWarning($"{LogPrefix} ForceRestoreForLocalPlayer dedupe threw: {ex.Message}"); }

            try
            {
                // Listen-host has the local Player available — pass it so
                // the restore takes the Player-aware code paths
                // (CommandFollow, m_customData writes). On dedicated this
                // path is unreachable because of the IsServer gate above.
                CompanionPatches.RestoreCompanionsFromVault(player, playerId, player.transform.position);
                _restoredPlayerIds.Add(playerId);
                Debug.Log($"{LogPrefix} Force-restored companions for local player {playerId}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} ForceRestoreForLocalPlayer threw: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Clears the per-player session tracking. Called on main-menu
        /// return so a new world / save can re-restore from scratch.
        /// </summary>
        public static void ResetSessionTracking()
        {
            _restoredPlayerIds.Clear();
            _pendingPlayerIds.Clear();
        }

        public static bool HasBeenRestoredThisSession(long playerId)
        {
            return _restoredPlayerIds.Contains(playerId);
        }

        // ──────────────────────────────────────────────────────────────────
        // Diagnostics
        // ──────────────────────────────────────────────────────────────────

        public static bool Verbose = false;

        // ──────────────────────────────────────────────────────────────────
        // Recall on a dedicated server must work for clients (charge only after success):
        // client → server recall + owned-companion listing. Add-only; RecallDormant is unchanged. Routed RPCs registered
        // per ZRoutedRpc instance (every ZNet.Awake makes a new one).
        // ──────────────────────────────────────────────────────────────────

        public const string RecallRpc = "FiresCore_CompanionRecall", RecallResultRpc = "FiresCore_CompanionRecallResult";
        public const string ListRpc = "FiresCore_CompanionList", ListResultRpc = "FiresCore_CompanionListResult";
        private const float RequestTimeout = 10f;

        /// <summary>One companion ZDO the player owns, wherever it is (loaded or not). Add-only.</summary>
        public struct OwnedCompanionInfo { public string CompanionId; public string Name; public ZDOID Zdo; public Vector3 Position; }

        private static ZRoutedRpc s_rpcOn;
        private static long s_nextRequest = 1;
        private static readonly Dictionary<long, (Action<bool, string> done, float deadline)> s_pendingRecall = new Dictionary<long, (Action<bool, string>, float)>();
        private static readonly Dictionary<long, (Action<List<OwnedCompanionInfo>> done, float deadline)> s_pendingList = new Dictionary<long, (Action<List<OwnedCompanionInfo>>, float)>();

        [HarmonyPatch(typeof(ZNet), "Awake")]
        [HarmonyPostfix]
        public static void ZNet_Awake_RegisterRecallRpcs() => EnsureRecallRpcs();

        private static void EnsureRecallRpcs()
        {
            var cur = ZRoutedRpc.instance;
            if (cur == null || ReferenceEquals(s_rpcOn, cur)) return;
            try
            {
                cur.Register<long, string, Vector3, long>(RecallRpc, RPC_Recall);
                cur.Register<long, bool, string>(RecallResultRpc, RPC_RecallResult);
                cur.Register<long, long>(ListRpc, RPC_List);
                cur.Register<long, ZPackage>(ListResultRpc, RPC_ListResult);
                cur.Register<long, long>(KennelRpc, RPC_Kennel);
                cur.Register<long, ZPackage>(KennelResultRpc, RPC_KennelResult);
                s_rpcOn = cur;
                s_pendingRecall.Clear(); s_pendingList.Clear();
            }
            catch (Exception ex) { Debug.LogWarning($"{LogPrefix} recall RPCs not registered: {ex.Message}"); }
        }

        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        /// <summary>
        /// Recall one companion for its owner from anywhere: a live / ZDO-only copy (any zone, loaded or not) is moved next to
        /// <paramref name="pos"/> and set to follow; otherwise the dormant (kennel) copy is spawned there. Never two copies.
        /// On the server / host it runs directly; on a client it asks the server and calls <paramref name="done"/>(ok, reason)
        /// when the answer arrives, or (false, "timeout") after 10 s. Callers charge only when ok is true. Add-only.
        /// </summary>
        public static void RequestRecall(long playerId, string companionId, Vector3 pos, Action<bool, string> done)
        {
            if (IsServer)
            {
                bool ok = RecallOnServer(playerId, companionId, pos, Player.m_localPlayer != null && Player.m_localPlayer.GetPlayerID() == playerId ? Player.m_localPlayer : null, out string why);
                done?.Invoke(ok, why);
                return;
            }
            EnsureRecallRpcs();
            if (ZRoutedRpc.instance == null || ZNet.instance == null) { done?.Invoke(false, "not connected"); return; }
            long id = s_nextRequest++;
            s_pendingRecall[id] = (done, Time.realtimeSinceStartup + RequestTimeout);
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerPeer(), RecallRpc, playerId, companionId ?? "", pos, id);
            TimeoutTicker.Ensure();
        }

        /// <summary>
        /// The companion ZDOs <paramref name="playerId"/> owns, loaded or not (so the roster can list companions in unloaded
        /// zones). On the server / host it answers directly; on a client it asks the server (empty list on timeout). Add-only.
        /// </summary>
        public static void RequestOwnedCompanions(long playerId, Action<List<OwnedCompanionInfo>> done)
        {
            if (IsServer) { done?.Invoke(ListOwnedCompanionZdos(playerId)); return; }
            EnsureRecallRpcs();
            if (ZRoutedRpc.instance == null || ZNet.instance == null) { done?.Invoke(new List<OwnedCompanionInfo>()); return; }
            long id = s_nextRequest++;
            s_pendingList[id] = (done, Time.realtimeSinceStartup + RequestTimeout);
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerPeer(), ListRpc, playerId, id);
            TimeoutTicker.Ensure();
        }

        /// <summary>Server / host only: every companion ZDO owned by <paramref name="playerId"/>. Add-only.</summary>
        public static List<OwnedCompanionInfo> ListOwnedCompanionZdos(long playerId)
        {
            var list = new List<OwnedCompanionInfo>();
            foreach (var zdo in AllZdos())
            {
                if (zdo == null || zdo.GetLong("companion_owner", 0L) != playerId) continue;
                string cid = zdo.GetString("companion_id", "");
                if (string.IsNullOrEmpty(cid)) continue;
                list.Add(new OwnedCompanionInfo { CompanionId = cid, Name = zdo.GetString("companion_name", ""), Zdo = zdo.m_uid, Position = zdo.GetPosition() });
            }
            return list;
        }

        private static long ServerPeer()
        {
            try { var sp = ZNet.instance.GetServerPeer(); if (sp != null) return sp.m_uid; } catch { }
            return ZRoutedRpc.instance.GetServerPeerID();
        }

        private static IEnumerable<ZDO> AllZdos()
        {
            if (ZDOMan.instance == null) yield break;
            var dict = AccessTools.Field(typeof(ZDOMan), "m_objectsByID")?.GetValue(ZDOMan.instance) as System.Collections.IDictionary;
            if (dict == null) yield break;
            var copy = new List<ZDO>();
            foreach (var v in dict.Values) if (v is ZDO z) copy.Add(z);
            foreach (var z in copy) yield return z;
        }

        // server: the recall itself (owner checked by the caller)
        private static bool RecallOnServer(long playerId, string companionId, Vector3 pos, Player owner, out string why)
        {
            why = "";
            if (playerId == 0L || string.IsNullOrEmpty(companionId)) { why = "bad request"; return false; }
            // 1) a live or ZDO-only copy anywhere: move it to the owner and make it follow (dedupe: the kennel copy is dropped)
            ZDO found = null; int copies = 0;
            foreach (var zdo in AllZdos())
                if (zdo != null && string.Equals(zdo.GetString("companion_id", ""), companionId, StringComparison.Ordinal) && zdo.GetLong("companion_owner", 0L) == playerId
                    && !zdo.GetBool("companion_defeated", false) && zdo.GetFloat(ZDOVars.s_health, 1f) > 0f)   // a corpse is not a live copy
                { copies++; if (found == null) found = zdo; }
            if (found != null)
            {
                if (copies > 1) Debug.LogWarning($"{LogPrefix} recall {companionId}: {copies} world copies; moving the first ({found.m_uid})");
                var inst = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(found) : null;
                if (inst != null)
                {
                    inst.transform.position = pos;
                    var cc = inst.GetComponent<CompanionController>();
                    if (cc != null && owner != null) { try { cc.CommandFollow(owner); } catch (Exception ex) { Debug.LogWarning($"{LogPrefix} recall CommandFollow: {ex.Message}"); } }
                }
                try
                {
                    if (!found.IsOwner()) found.SetOwner(ZDOMan.GetSessionID());   // the server takes the ZDO to move it; a nearby client takes it back as usual
                    found.SetPosition(pos);
                    found.Set("companion_wasfollowing", true);   // Recall is the owner's own command: "come back and follow me"
                }
                catch (Exception ex) { why = "move failed: " + ex.Message; return false; }
                NpcDormancyBridge.Remove(playerId, companionId);
                Debug.Log($"{LogPrefix} recall {companionId} for {playerId}: moved its world copy {found.m_uid} to {pos}{(inst != null ? " (loaded)" : " (unloaded zone)")}");
                why = "moved";
                return true;
            }
            // 2) the dormant (kennel) copy
            if (NpcDormancyBridge.Get(playerId, companionId) == null) { why = "no such companion"; return false; }
            var entry = NpcDormancyBridge.Get(playerId, companionId);
            if (!TrySpawnDormant(entry, pos, out var controller)) { why = "spawn failed"; return false; }
            try
            {
                if (controller != null && owner != null) controller.CommandFollow(owner);
                else controller?.GetComponent<ZNetView>()?.GetZDO()?.Set("companion_wasfollowing", true);   // dedicated server: no Player object for the owner
            }
            catch (Exception ex) { Debug.LogWarning($"{LogPrefix} recall follow for {companionId}: {ex.Message}"); }
            NpcDormancyBridge.Remove(playerId, companionId);
            Debug.Log($"{LogPrefix} recall {companionId} for {playerId}: spawned from the kennel at {pos}");
            why = "spawned";
            return true;
        }

        // The RPC sender is the
        // peer / session uid, the player id is the profile id. Map the sender peer to its character ZDO and read that ZDO's player id.
        private static bool SenderIsPlayer(long sender, long playerId)
        {
            if (sender == playerId) return true;
            try
            {
                if (ZNet.instance != null && sender == ZNet.GetUID())
                    return Player.m_localPlayer != null && Player.m_localPlayer.GetPlayerID() == playerId;   // host acting for itself
                var peer = ZNet.instance?.GetPeer(sender);
                if (peer == null || peer.m_characterID.IsNone()) return false;
                var zdo = ZDOMan.instance?.GetZDO(peer.m_characterID);
                return zdo != null && zdo.GetLong(ZDOVars.s_playerID) == playerId;
            }
            catch (Exception ex) { Debug.LogWarning($"{LogPrefix} sender check: {ex.Message}"); return false; }
        }

        private static void RPC_Recall(long sender, long playerId, string companionId, Vector3 pos, long requestId)
        {
            if (!IsServer) return;
            bool ok; string why;
            if (!SenderIsPlayer(sender, playerId)) { ok = false; why = "not the owner"; Debug.LogWarning($"{LogPrefix} recall {companionId} refused: sender {sender} is not player {playerId}"); }
            else ok = RecallOnServer(playerId, companionId, pos, null, out why);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, RecallResultRpc, requestId, ok, why ?? "");
        }

        private static void RPC_RecallResult(long sender, long requestId, bool ok, string why)
        {
            if (!RpcGuard.SenderIsServer(sender)) { RpcGuard.Refuse("RPC_RecallResult", sender, "only the server answers"); return; }
            if (!s_pendingRecall.TryGetValue(requestId, out var p)) return;   // late / duplicate reply: ignored (charge-on-success can't run twice)
            s_pendingRecall.Remove(requestId);
            try { p.done?.Invoke(ok, why); } catch (Exception ex) { Debug.LogWarning($"{LogPrefix} recall callback: {ex.Message}"); }
        }

        private static void RPC_List(long sender, long playerId, long requestId)
        {
            if (!IsServer) return;
            var pkg = new ZPackage();
            var list = SenderIsPlayer(sender, playerId) ? ListOwnedCompanionZdos(playerId) : new List<OwnedCompanionInfo>();
            pkg.Write(list.Count);
            foreach (var c in list) { pkg.Write(c.CompanionId); pkg.Write(c.Name ?? ""); pkg.Write(c.Zdo); pkg.Write(c.Position); }
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, ListResultRpc, requestId, pkg);
        }

        // The kennel ZDO sits at the world origin, so a dedi CLIENT only has
        // it loaded near (0,0) and every stored / dismissed / respawning companion vanished from the roster elsewhere. The server reads its
        // kennel and answers. Add-only.
        public const string KennelRpc = "FiresCore_KennelList", KennelResultRpc = "FiresCore_KennelListResult";
        private static readonly Dictionary<long, (Action<List<DormantNpcEntry>> done, float deadline)> s_pendingKennel = new Dictionary<long, (Action<List<DormantNpcEntry>>, float)>();

        /// <summary>the kennel (dormant / stored / dead-pending) entries of <paramref name="playerId"/>. On the server / host it answers
        /// at once from its kennel; on a client it asks the server and calls <paramref name="done"/> when the reply lands (no call on a
        /// timeout or a lost reply: keep the local kennel as the fallback). Add-only.</summary>
        public static void RequestKennelEntries(long playerId, Action<List<DormantNpcEntry>> done)
        {
            if (IsServer) { done?.Invoke(FiresCore.Npc.Persistence.CompanionKennel.GetEntries(playerId) ?? new List<DormantNpcEntry>()); return; }
            EnsureRecallRpcs();
            if (ZRoutedRpc.instance == null || ZNet.instance == null) return;
            float now = Time.realtimeSinceStartup;
            var stale = new List<long>(); foreach (var kv in s_pendingKennel) if (kv.Value.deadline < now) stale.Add(kv.Key);
            foreach (var k in stale) s_pendingKennel.Remove(k);
            long id = s_nextRequest++;
            s_pendingKennel[id] = (done, now + RequestTimeout);
            ZRoutedRpc.instance.InvokeRoutedRPC(ServerPeer(), KennelRpc, playerId, id);
        }

        private static void RPC_Kennel(long sender, long playerId, long requestId)
        {
            if (!IsServer) return;
            var entries = SenderIsPlayer(sender, playerId) ? (FiresCore.Npc.Persistence.CompanionKennel.GetEntries(playerId) ?? new List<DormantNpcEntry>()) : new List<DormantNpcEntry>();   // only your own kennel
            var pkg = new ZPackage();
            byte[] bytes = FiresCore.Npc.Persistence.CompanionKennelSerializer.Serialize(entries) ?? new byte[0];
            pkg.Write(bytes);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, KennelResultRpc, requestId, pkg);
        }

        private static void RPC_KennelResult(long sender, long requestId, ZPackage pkg)
        {
            if (!RpcGuard.SenderIsServer(sender)) { RpcGuard.Refuse("RPC_KennelResult", sender, "only the server answers"); return; }
            if (!s_pendingKennel.TryGetValue(requestId, out var p)) return;
            s_pendingKennel.Remove(requestId);
            List<DormantNpcEntry> list = null;
            try { list = FiresCore.Npc.Persistence.CompanionKennelSerializer.Deserialize(pkg.ReadByteArray()); } catch (Exception ex) { Debug.LogWarning($"{LogPrefix} kennel reply unreadable: {ex.Message}"); }
            try { p.done?.Invoke(list ?? new List<DormantNpcEntry>()); } catch (Exception ex) { Debug.LogWarning($"{LogPrefix} kennel callback: {ex.Message}"); }
        }

        private const int MaxListedCompanions = 256;
        private static void RPC_ListResult(long sender, long requestId, ZPackage pkg)
        {
            if (!RpcGuard.SenderIsServer(sender)) { RpcGuard.Refuse("RPC_ListResult", sender, "only the server answers"); return; }
            if (!s_pendingList.TryGetValue(requestId, out var p)) return;
            s_pendingList.Remove(requestId);
            var list = new List<OwnedCompanionInfo>();
            try { int n = pkg.ReadInt(); if (n < 0 || n > MaxListedCompanions) return; for (int i = 0; i < n; i++) list.Add(new OwnedCompanionInfo { CompanionId = pkg.ReadString(), Name = pkg.ReadString(), Zdo = pkg.ReadZDOID(), Position = pkg.ReadVector3() }); }
            catch (Exception ex) { Debug.LogWarning($"{LogPrefix} list reply unreadable: {ex.Message}"); }
            try { p.done?.Invoke(list); } catch (Exception ex) { Debug.LogWarning($"{LogPrefix} list callback: {ex.Message}"); }
        }

        // times out pending requests (10 s) so a lost reply never leaves the roster waiting; charges never happen on a timeout
        private sealed class TimeoutTicker : MonoBehaviour
        {
            private static TimeoutTicker s_one;
            internal static void Ensure() { if (s_one != null) return; var go = new GameObject("FiresCompanionRecallTimeouts"); DontDestroyOnLoad(go); s_one = go.AddComponent<TimeoutTicker>(); }
            private void Update()
            {
                if (s_pendingRecall.Count == 0 && s_pendingList.Count == 0) return;
                float now = Time.realtimeSinceStartup;
                foreach (var k in s_pendingRecall.Where(kv => kv.Value.deadline < now).Select(kv => kv.Key).ToList())
                { var p = s_pendingRecall[k]; s_pendingRecall.Remove(k); try { p.done?.Invoke(false, "timeout"); } catch { } }
                foreach (var k in s_pendingList.Where(kv => kv.Value.deadline < now).Select(kv => kv.Key).ToList())
                { var p = s_pendingList[k]; s_pendingList.Remove(k); try { p.done?.Invoke(new List<OwnedCompanionInfo>()); } catch { } }
            }
        }
    }
}