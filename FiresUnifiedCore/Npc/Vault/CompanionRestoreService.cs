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
            foreach (var prefabName in _companionPrefabNames)
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
                if (!entry.IsRecallReady(nowTicks)) continue;             // deadline not elapsed yet

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
                    return true;
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

        // Companion prefab names this service scans. Mirrors the list
        // CompanionPatches uses; kept local so we don't take a hard
        // dependency on internal helpers.
        private static readonly string[] _companionPrefabNames =
        {
            "CompanionNpc",
            "CompanionNpc_Wild",
            "BaseNpc",
        };

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
    }
}