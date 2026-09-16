using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Brings following companions to their owner after arrival rather than at departure; dispatching on
    /// departure raced zone loads on long jumps and left companions half-teleported. Once the player has
    /// control again (Player.OnSpawned for login and respawn, Player.TeleportTo for portals, wayshrines and
    /// dungeons, or a companion stranded in session) one reconcile RPC has the server take ownership of and
    /// move every far-off companion whose ZDO says it was following. companion_wasfollowing is only cleared
    /// by an explicit owner command, and nearby companions are skipped, so repeat calls are harmless.
    /// </summary>
    [HarmonyPatch]
    public static class CompanionTeleportService
    {
        private const string LogPrefix = "[CompanionTeleportService]";
        private const string RPC_Reconcile = "FiresRPGmaker_CompanionTeleport_v2";

        // All distance gates + the snap predicate live in the single source of truth CompanionLeash. Arrival
        // reconcile reels in past CompanionLeash.ArrivalReelInDistance; the heartbeat uses CompanionLeash.SnapDistance.

        // ── Server-authoritative "leash" heartbeat ───────────────────────────────────────────────
        // The client-side CompanionController.CheckFollowTeleport can only reel a companion in when the
        // peer running its AI can actually SEE the owner (owner's character loaded in that peer's scene).
        // Two cases break that: (a) on a DEDICATED SERVER the companion is frequently owned by the server
        // itself once the owner walks out of the companion's zone, and CheckFollowTeleport has NO local
        // player to resolve — it bails; (b) a bystander client owns the companion ZDO but doesn't have the
        // owner in its scene. In both, a stranded follower would sit forever. This heartbeat runs ON THE
        // SERVER, which keeps every connected player's zone loaded, so it can resolve each player's position
        // directly and reel in any of their FOLLOWING companions (dormant ZDO or live) that drifted too far.
        // It is the single authority that can always see everyone — the real leash.
        private const float HeartbeatIntervalSeconds = 5f;
        // The heartbeat reels in followers past CompanionLeash.SnapDistance (the SAME 80m ceiling the client-side
        // run-back snaps at) — the safety net for followers the client path can't manage (dormant / blind peer).
        private static float _lastHeartbeat;

        // Companion prefab names this service scans. Mirrors the list in
        // CompanionPatches and CompanionRestoreService — kept local so we
        // don't take a hard dependency on internal helpers.
        private static readonly string[] _companionPrefabNames =
        {
            "CompanionNpc",
            "CompanionNpc_Wild",
            "BaseNpc",
        };

        // ──────────────────────────────────────────────────────────────────
        // RPC registration — non-generic Register(name, action) form
        // matches every working server-receiving RPC in the codebase
        // (VaultOfKnowledge, GuildSync, etc).
        // ──────────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        public static void ZNet_Start_RegisterRpc()
        {
            if (ZRoutedRpc.instance == null)
            {
                Debug.LogWarning($"{LogPrefix} ZNet.Start postfix: ZRoutedRpc.instance is null — RPC '{RPC_Reconcile}' NOT registered. Reconcile will silently no-op.");
                return;
            }
            ZRoutedRpc.instance.Register(
                RPC_Reconcile,
                new System.Action<long, ZPackage>(OnReconcileRequest));
            Debug.Log($"{LogPrefix} Registered routed RPC '{RPC_Reconcile}' (server={ZNet.instance?.IsServer() == true})");
        }

        // ──────────────────────────────────────────────────────────────────
        // Public client entry point
        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Client to server: "I'm here; reconcile my followers." The server moves every companion ZDO owned by
        /// <paramref name="owner"/> with the follow flag set that is beyond the distance gate. Repeat calls are
        /// harmless, which is why a single stranded companion can use the same call.
        /// </summary>
        public static void RequestReconcileFollowers(Player owner)
        {
            if (owner == null) return;
            if (owner != Player.m_localPlayer) return;
            if (ZRoutedRpc.instance == null || ZNet.instance == null) return;

            long playerId = owner.GetPlayerID();
            if (playerId == 0L) return;

            Vector3 pos = owner.transform.position;

            var pkg = new ZPackage();
            pkg.Write(playerId);
            pkg.Write(pos);

            try
            {
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RPC_Reconcile, pkg);
                Debug.Log($"{LogPrefix} Sent reconcile request: playerId={playerId}, pos={pos}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} RequestReconcileFollowers threw for {playerId}: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────────────────
        // Server-side handler
        // ──────────────────────────────────────────────────────────────────

        private static void OnReconcileRequest(long sender, ZPackage pkg)
        {
            try
            {
                bool isServer = ZNet.instance != null && ZNet.instance.IsServer();
                if (!isServer)
                {
                    // DIAG (login-freeze): the Everybody broadcast self-echoes back to
                    // the client on the next frame after dispatch. Log this so we know
                    // whether the client got at least one frame of Update past
                    // "Sent reconcile request" before the freeze.
                    Debug.Log($"[CompanionTeleportService][DIAG] OnReconcileRequest local self-echo received (sender={sender}) — bailing because !IsServer");
                    return;
                }
                if (pkg == null) return;
                if (ZDOMan.instance == null) return;

                long playerId = pkg.ReadLong();
                Vector3 ownerPos = pkg.ReadVector3();
                if (playerId == 0L) return;

                Debug.Log($"{LogPrefix} Reconcile received: playerId={playerId}, ownerPos={ownerPos} (sender={sender})");

                int teleported = ReconcileFollowersFor(playerId, ownerPos, CompanionLeash.ArrivalReelInDistance);
                Debug.Log($"{LogPrefix} Reconcile complete for player {playerId}: teleported={teleported}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} OnReconcileRequest threw: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Server-side: reel in every FOLLOWING companion owned by <paramref name="playerId"/> that has drifted
        /// farther than <paramref name="threshold"/> from <paramref name="ownerPos"/>. Operates directly on ZDOs
        /// (works for dormant companions too). Returns how many were moved. MUST run on the server.
        /// </summary>
        private static int ReconcileFollowersFor(long playerId, Vector3 ownerPos, float threshold)
        {
            if (ZDOMan.instance == null || playerId == 0L) return 0;

            int teleported = 0;
            var temp = new List<ZDO>();
            foreach (var prefabName in _companionPrefabNames)
            {
                temp.Clear();
                int idx = 0;
                while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefabName, temp, ref idx)) { }

                foreach (var zdo in temp)
                {
                    if (zdo == null || !zdo.IsValid()) continue;
                    if (zdo.GetLong("companion_owner", 0L) != playerId) continue;
                    if (TryReelIn(zdo, ownerPos, threshold)) teleported++;
                }
            }
            return teleported;
        }

        /// <summary>
        /// Server-side single-companion reel-in with all the safety gates in ONE place:
        ///   • Stationed NPCs are world fixtures — never moved.
        ///   • Stay-mode companions (companion_wasfollowing=false) are left where they are — the follow flag is
        ///     owner-command-only and SACRED; this service never reels in a companion the owner told to stay.
        ///   • Already within <paramref name="threshold"/> — nothing to do.
        /// When it does move a follower it claims ZDO ownership first so the write can't lose to closest-peer
        /// ownership flap. Returns true if the companion was moved.
        /// </summary>
        private static bool TryReelIn(ZDO zdo, Vector3 ownerPos, float threshold)
        {
            if (zdo == null || !zdo.IsValid()) return false;
            if (zdo.GetBool("npc_stationed", false)) return false;
            if (!zdo.GetBool("companion_wasfollowing", false)) return false;
            if (Vector3.Distance(zdo.GetPosition(), ownerPos) < threshold) return false;

            zdo.SetOwner(ZDOMan.GetSessionID());
            zdo.SetPosition(ownerPos);
            zdo.DataRevision++;
            ZDOMan.instance.ForceSendZDO(zdo.m_uid);
            return true;
        }

        // ── Server leash heartbeat ───────────────────────────────────────────────────────────────
        // Runs off Game.Update (ticks on the dedicated server too). Throttled to HeartbeatIntervalSeconds.
        // The single authority that always sees every connected player, so it can reel in stranded followers
        // no matter which peer owns the companion ZDO or whether that peer can see the owner.
        [HarmonyPatch(typeof(Game), "Update")]
        [HarmonyPostfix]
        public static void Game_Update_Heartbeat()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            if (ZDOMan.instance == null) return;
            if (Time.unscaledTime - _lastHeartbeat < HeartbeatIntervalSeconds) return;
            _lastHeartbeat = Time.unscaledTime;

            try { ServerHeartbeat(); }
            catch (Exception ex) { Debug.LogWarning($"{LogPrefix} heartbeat threw: {ex.Message}"); }
        }

        private static void ServerHeartbeat()
        {
            // The server keeps every connected player's zone loaded, so their character is in GetAllPlayers()
            // here even though a remote client's scene would not contain a different player. Skip players who
            // are mid-teleport or dead so we don't reel a follower onto a loading screen or a corpse.
            var ownerPositions = new Dictionary<long, Vector3>();
            foreach (var player in Player.GetAllPlayers())
            {
                if (player == null) continue;
                // Shared guard: skip dead / mid-teleport / in-bed owners so we never yank followers onto a
                // loading screen or a corpse (the "guard against player teleport/death" rule, server-side).
                if (!CompanionLeash.IsOwnerReelTarget(player)) continue;
                long id = player.GetPlayerID();
                if (id != 0L) ownerPositions[id] = player.transform.position;
            }
            if (ownerPositions.Count == 0) return;

            // ONE ZDO scan for all players; look each companion's owner up in the map.
            int reeled = 0;
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
                    if (zdoOwner == 0L) continue;
                    if (!ownerPositions.TryGetValue(zdoOwner, out var pos)) continue; // owner not connected/loaded
                    if (TryReelIn(zdo, pos, CompanionLeash.SnapDistance)) reeled++;
                }
            }

            if (reeled > 0)
                Debug.Log($"{LogPrefix} leash heartbeat reeled in {reeled} stranded follower(s)");
        }

        // ──────────────────────────────────────────────────────────────────
        // Diagnostics
        // ──────────────────────────────────────────────────────────────────

        public static bool Verbose = false;
    }
}
