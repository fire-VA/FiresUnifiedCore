using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Reconcile-on-arrival companion teleport service.
    ///
    /// DESIGN â€” why this is reconcile-on-arrival, not dispatch-on-depart:
    ///
    /// Earlier iterations of this service dispatched a teleport RPC from
    /// <c>Player.TeleportTo</c> postfix â€” i.e. the moment the player
    /// initiates a wayshrine / portal / dungeon entry. That model has a
    /// fatal race on long jumps:
    ///   1. Player still in source zone when RPC fires.
    ///   2. Source zone may still be unloading.
    ///   3. Destination zone may not be loaded server-side yet.
    ///   4. Server writes companion ZDO position to a destination that
    ///      isn't loaded â†’ write races with zone-load sync â†’ companion
    ///      ends up half-teleported, follow flag thrashed by reconciliation
    ///      logic, group HUD empty when player arrives.
    ///
    /// This service uses the inverse model: do NOTHING at depart time
    /// (other than suppressing local CheckFollowTeleport via
    /// <c>SuppressCompanionTeleportsUntil</c>). Wait until the loading
    /// screen finishes and the character has control, THEN dispatch a
    /// single <see cref="RequestReconcileFollowers"/> RPC. The server
    /// scans <c>ZDOMan</c> for ZDOs where
    /// <c>companion_owner == playerId &amp;&amp;
    /// companion_wasfollowing == true</c> and rewrites the position of
    /// every one that's far from the player. Companions already near
    /// the player (small drift) are left in place.
    ///
    /// HOOK SET (covers all arrival transitions):
    /// - <see cref="Player.OnSpawned"/> postfix â€” fires for initial
    ///   login and death-respawn (the only paths that go through
    ///   Game.SpawnPlayer â†’ Player.AwakeAndSpawn).
    /// - <see cref="Player.TeleportTo"/> postfix â€” fires for wayshrine,
    ///   portal, and dungeon teleports. Player.TeleportTo reuses the
    ///   existing player object and never re-invokes OnSpawned, so a
    ///   second hook is required to cover these cases.
    /// Both hooks start ReconcileFollowersAfterArrival, which waits
    /// for IsTeleporting=false and CanMove=true before firing.
    ///
    /// FOURTH TRIGGER (in-session drift):
    /// - <see cref="CompanionController.CheckFollowTeleport"/> stranded
    ///   path â€” companion has drifted catastrophically far in-session;
    ///   the same reconcile call brings them back to the player.
    ///
    /// PROPERTIES:
    /// - Server-authoritative: server claims ZDO ownership before the
    ///   write (<c>zdo.SetOwner(ZDOMan.GetSessionID())</c>), so the
    ///   write isn't competing with closest-peer ownership flap.
    /// - <c>companion_wasfollowing</c> is treated as STICKY â€” the only
    ///   things that should ever clear it are explicit owner commands
    ///   (Stay via radial / shift+interact, Dismiss). Teleport, respawn,
    ///   restore, etc. must never touch it. The server uses this flag as
    ///   the source of truth for "should this companion follow my owner."
    /// - Idempotent: if reconcile fires twice in quick succession the
    ///   second one is mostly a no-op (companions now near the player
    ///   are skipped by the distance gate).
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
        // CompanionPatches and CompanionRestoreService â€” kept local so we
        // don't take a hard dependency on internal helpers.
        private static readonly string[] _companionPrefabNames =
        {
            "CompanionNpc",
            "CompanionNpc_Wild",
            "BaseNpc",
        };

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // RPC registration â€” non-generic Register(name, action) form
        // matches every working server-receiving RPC in the codebase
        // (VaultOfKnowledge, GuildSync, etc).
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        public static void ZNet_Start_RegisterRpc()
        {
            if (ZRoutedRpc.instance == null)
            {
                Debug.LogWarning($"{LogPrefix} ZNet.Start postfix: ZRoutedRpc.instance is null â€” RPC '{RPC_Reconcile}' NOT registered. Reconcile will silently no-op.");
                return;
            }
            ZRoutedRpc.instance.Register(
                RPC_Reconcile,
                new System.Action<long, ZPackage>(OnReconcileRequest));
            Debug.Log($"{LogPrefix} Registered routed RPC '{RPC_Reconcile}' (server={ZNet.instance?.IsServer() == true})");
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Public client entry point
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Client â†’ server: "I'm at this position; reconcile my followers."
        /// The server scans ZDOMan for every companion ZDO owned by
        /// <paramref name="owner"/> with the persistent follow flag set,
        /// and teleports any that are farther than the distance gate from
        /// the player to the player. Idempotent â€” calling repeatedly is
        /// harmless.
        ///
        /// Callsites:
        /// - <c>Player.OnSpawned</c> postfix â†’ ReconcileFollowersAfterArrival
        ///   coroutine. Covers initial login and death-respawn (the only
        ///   transitions where the engine actually re-invokes OnSpawned).
        /// - <c>Player.TeleportTo</c> postfix â†’ same coroutine. Covers
        ///   wayshrine, portal, and dungeon teleports â€” Player.TeleportTo
        ///   reuses the existing player object so OnSpawned never fires
        ///   for these, which is why a separate dispatch site is required.
        /// - <c>CompanionController.CheckFollowTeleport</c> stranded path
        ///   when a single companion has drifted catastrophically far.
        ///   The reconcile pattern handles this case too â€” server-side
        ///   distance gate skips companions that are already close, so
        ///   firing reconcile for "one stranded companion" only actually
        ///   moves that one.
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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Server-side handler
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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
                    Debug.Log($"[CompanionTeleportService][DIAG] OnReconcileRequest local self-echo received (sender={sender}) â€” bailing because !IsServer");
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
            foreach (var p in Player.GetAllPlayers())
            {
                if (p == null) continue;
                // Shared guard: skip dead / mid-teleport / in-bed owners so we never yank followers onto a
                // loading screen or a corpse (the "guard against player teleport/death" rule, server-side).
                if (!CompanionLeash.IsOwnerReelTarget(p)) continue;
                long id = p.GetPlayerID();
                if (id != 0L) ownerPositions[id] = p.transform.position;
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

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Diagnostics
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        public static bool Verbose = false;
    }
}
