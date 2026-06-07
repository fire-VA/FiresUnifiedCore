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

        // Distance gate. Companions farther than this from the player at
        // reconcile time get teleported; closer ones are left alone. 30m
        // is comfortably outside the normal follow distance (12m default)
        // but tight enough that nothing teleports unnecessarily.
        private const float ReconcileDistanceThreshold = 30f;

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

                int teleported = 0;
                int alreadyClose = 0;
                int notFollowing = 0;
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

                        // Stationed NPCs are world fixtures â€” never teleport them.
                        if (zdo.GetBool("npc_stationed", false)) continue;

                        // Authoritative follow flag is on the companion ZDO
                        // (companion_wasfollowing). Stay-mode companions
                        // stay where they are even on owner login/teleport;
                        // only followers reel in.
                        if (!zdo.GetBool("companion_wasfollowing", false))
                        {
                            notFollowing++;
                            continue;
                        }

                        Vector3 zdoPos = zdo.GetPosition();
                        if (Vector3.Distance(zdoPos, ownerPos) < ReconcileDistanceThreshold)
                        {
                            alreadyClose++;
                            continue;
                        }

                        // Server claims ownership BEFORE the position write
                        // so the write doesn't lose to closest-peer
                        // ownership flap mid-teleport.
                        zdo.SetOwner(ZDOMan.GetSessionID());

                        zdo.SetPosition(ownerPos);
                        zdo.DataRevision++;
                        ZDOMan.instance.ForceSendZDO(zdo.m_uid);
                        teleported++;
                    }
                }

                Debug.Log($"{LogPrefix} Reconcile complete for player {playerId}: teleported={teleported}, alreadyClose={alreadyClose}, notFollowing={notFollowing}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} OnReconcileRequest threw: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        // Diagnostics
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        public static bool Verbose = false;
    }
}
