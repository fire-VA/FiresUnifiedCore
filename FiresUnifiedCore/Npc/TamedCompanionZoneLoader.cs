using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Tamed Companion Zone Loader â€” keeps terrain, objects, and ZDOs loaded
    /// around tamed companions so they don't fall into the void / despawn /
    /// drift out of sync when no player is in their zone.
    ///
    /// Why this exists
    /// ---------------
    /// Vanilla zone-stream rules: a ZDO's zone is loaded by the closest peer.
    /// If every peer is far away, the zone unloads, the ZDO sits dormant in
    /// ZDOMan, and any logic that needs the live MonoBehaviour (cross-peer
    /// teleport requests, idle-behaviour updates, ownership transfers, â€¦)
    /// silently no-ops until somebody walks back into range.
    ///
    /// For tamed companions that's the wrong behaviour. The player's pets
    /// shouldn't pop out of existence the moment they wander 64m off, and
    /// long-jump teleports (portals / wayshrines / dungeon entries) should
    /// be able to bring them along â€” both of which require an authoritative
    /// peer to be holding the ZDO loaded.
    ///
    /// Pattern
    /// -------
    /// Mirrors <c>VerdantsAscent.Modules.CameraZoneLoader</c> from FiresValcast,
    /// which keeps zones loaded around placed PiP cameras using exactly the
    /// same hooks. Same shape here, different position source:
    ///
    ///   Client: gather positions of every TAMED companion belonging to the
    ///   local player, send to server via routed RPC every <see cref="SendInterval"/>
    ///   seconds. Also load zones locally so the player's own client doesn't
    ///   despawn its companions while the player is in a different chunk.
    ///
    ///   Server: receives camera positions per peer, calls
    ///   <c>ZoneSystem.CreateLocalZones</c> + <c>CreateGhostZones</c> around
    ///   each tracked position so terrain heightmaps + ghost ZDOs are
    ///   generated. <c>ZNetScene.RemoveObjects</c> prefix appends
    ///   companion-area ZDOs to the near list so vanilla earmarks them and
    ///   doesn't despawn them on the next sweep.
    ///
    ///   Cleanup on <c>ZNet.Disconnect</c> drops the disconnecting peer's
    ///   tracked positions immediately so we don't keep idle zones loaded
    ///   for an offline player's dead pets forever.
    ///
    /// Filter
    /// ------
    /// "Tamed" specifically â€” wild creatures and untamed neutrals don't qualify.
    /// Both <c>isTamed</c> AND <c>ownerPlayerId != 0</c> on
    /// <see cref="CompanionController"/>; both flags must be set or we'd be
    /// keeping zones alive for half-tamed wild spawns mid-conversion.
    ///
    /// Cost
    /// ----
    /// Each kept zone is a heightmap + ZDO sector. For a player with N tamed
    /// companions scattered across the world, that's N active areas above the
    /// vanilla baseline. In practice the count is small (typical play has
    /// 1â€“3 active companions), but if server CPU climbs after long sessions
    /// this is the candidate to throttle â€” e.g. only follow-state companions
    /// or only the closest M per player.
    /// </summary>
    [HarmonyPatch]
    public static class TamedCompanionZoneLoader
    {
        // RPC name â€” namespaced so it can't collide with Valcast's analogous RPC.
        private const string RPC_TamedCompanionPositions = "FiresRPGmaker_TamedCompanionPositions";

        // Client send cadence. 1Hz matches Valcast and is plenty for zone loading
        // (zones are 64Ã—64m, a companion would have to teleport faster than that
        // for the gap to matter).
        private const float SendInterval = 1f;
        private static float _sendTimer;

        // Server-side: companion positions per peer UID, refreshed on every RPC.
        // Cleared per-peer on disconnect so an offline player's old positions
        // don't keep zones loaded forever via THIS source. Offline coverage is
        // provided by _allKnownCompanionPositions (below) when enabled.
        private static readonly Dictionary<long, List<Vector3>> _peerCompanionPositions
            = new Dictionary<long, List<Vector3>>();

        // Server-side: positions of EVERY tamed companion in the world, including
        // those belonging to offline players. Refreshed periodically via a
        // ZDOMan scan (no client cooperation required). When merged with
        // _peerCompanionPositions in the keep-alive loop, this is what makes
        // companions persistent participants in the world even when their
        // owner is logged out â€” the companion's zone stays loaded server-side
        // so the GameObject continues ticking (combat, idle behaviors,
        // wandering). Empty when KeepZonesLoadedForOfflinePlayers is false.
        private static readonly List<Vector3> _allKnownCompanionPositions = new List<Vector3>();
        private static readonly List<ZDO> _allKnownScanBuf = new List<ZDO>();
        private static float _allKnownScanTimer;

        // Cadence of the offline-companion ZDO scan. Heavier than the per-peer
        // RPC (it walks ZDOMan over multiple prefab names) but only runs every
        // 5 s, so amortised cost is small even on servers with hundreds of
        // tamed companions.
        private const float AllKnownScanInterval = 5f;

        // Companion prefab names this loader cares about. Mirrors the list in
        // CompanionPatches; kept local so this module doesn't take a hard
        // dependency on internal helpers.
        private static readonly string[] _companionPrefabNamesForScan =
        {
            "CompanionNpc",
            "CompanionNpc_Wild",
            "BaseNpc",
        };

        // Reusable lists to avoid GC churn â€” gathered fresh every tick.
        private static readonly List<Vector3> _localCompanionPositions = new List<Vector3>();
        private static readonly List<Vector3> _tempPositions = new List<Vector3>();
        private static readonly List<ZDO> _companionZDOs = new List<ZDO>();

        // Reflected ZoneSystem internals â€” same private methods Valcast reflects.
        // Cached once at ZNet.Start since they never change.
        private static MethodInfo _createLocalZones;
        private static MethodInfo _createGhostZones;

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //  Position gathering
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        /// <summary>
        /// Walks <see cref="CompanionController.AllCompanions"/> (loaded on this
        /// client only) and appends positions of tamed companions belonging to
        /// the local player. Only loaded companions can be gathered locally,
        /// which is fine â€” the WHOLE POINT of this loader is to make sure
        /// loaded companions stay loaded going forward, so once a companion
        /// has streamed in once it never falls off the tracked list.
        /// </summary>
        private static void GatherLocalTamedCompanionPositions(List<Vector3> target)
        {
            var lp = Player.m_localPlayer;
            if (lp == null) return;
            long localPlayerId = lp.GetPlayerID();
            if (localPlayerId == 0) return;

            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null) continue;
                if (!companion.isTamed) continue;
                if (companion.ownerPlayerId != localPlayerId) continue;

                target.Add(companion.transform.position);
            }
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //  RPC registration + reflection cache
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        private static void ZNet_Start_Postfix()
        {
            if (ZRoutedRpc.instance == null) return;

            ZRoutedRpc.instance.Register<ZPackage>(
                RPC_TamedCompanionPositions,
                new System.Action<long, ZPackage>(RPC_OnCompanionPositions));

            CacheReflection();
        }

        private static void CacheReflection()
        {
            const BindingFlags bf = BindingFlags.Instance | BindingFlags.NonPublic;
            _createLocalZones = typeof(ZoneSystem).GetMethod("CreateLocalZones", bf);
            _createGhostZones = typeof(ZoneSystem).GetMethod("CreateGhostZones", bf);
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //  Server: receive positions from clients
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        private static void RPC_OnCompanionPositions(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            int count = pkg.ReadInt();
            if (!_peerCompanionPositions.TryGetValue(sender, out var positions))
            {
                positions = new List<Vector3>();
                _peerCompanionPositions[sender] = positions;
            }
            positions.Clear();

            for (int i = 0; i < count; i++)
                positions.Add(pkg.ReadVector3());
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //  Per-tick: client sends, both sides load zones
        //
        //  Postfix on ZoneSystem.Update so we ride the same 0.1s cadence the
        //  vanilla zone loader uses.
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        [HarmonyPatch(typeof(ZoneSystem), "Update")]
        [HarmonyPostfix]
        private static void ZoneSystem_Update_Postfix()
        {
            if (ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected) return;

            _localCompanionPositions.Clear();
            GatherLocalTamedCompanionPositions(_localCompanionPositions);

            // Client: throttled send to server. Sending while the local list is
            // empty is harmless but wasteful â€” skip when there's nothing to track.
            if (_localCompanionPositions.Count > 0)
            {
                _sendTimer += Time.deltaTime;
                if (_sendTimer >= SendInterval)
                {
                    _sendTimer = 0f;
                    SendCompanionPositions();
                }
            }

            // SERVER-ONLY zone keep-alive.
            //
            // Earlier this block also called LoadZonesForPositions on the
            // CLIENT side, which seemed reasonable ("keep my own companions
            // loaded") but caused a vicious teleport loop on long-jumps:
            //
            //   1. Player long-jumps. Loading screen tears zones down.
            //   2. Client-side keep-alive prevents the companion's source
            //      zone from fully unloading on the client.
            //   3. ZDO ownership transfers (no peer is near the companion's
            //      sector anymore), but the ZNetView GameObject is still
            //      live on the client.
            //   4. Character.UpdateOwner.SyncFromZDO runs every frame, sees
            //      the stale ZDO position broadcast from the new owner,
            //      reverts transform.position to the source location.
            //   5. CompanionController.CheckFollowTeleport on the client
            //      sees companion ~5km from player â†’ fires TeleportToOwner
            //      â†’ SetPosition silently rejected because client isn't the
            //      ZDO owner anymore â†’ next frame transform reverts again.
            //
            // The fix is to NOT keep zones loaded client-side. Companion
            // GameObjects unload naturally when the player teleports far,
            // CheckFollowTeleport stops firing on the client (no MonoBehaviour
            // to host it), and the routed-RPC sweep in CompanionPatches
            // reaches the server (which IS the authoritative owner via
            // CreateGhostZones below) to do the teleport.
            //
            // Singleplayer / listen-host still get coverage: those topologies
            // run as IsServer() == true, the local client's positions are
            // sent to-self via the routed RPC, and the server-side block
            // below picks them up. So we don't lose anything; we just stop
            // double-loading on dedicated clients.
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                // Per-online-peer positions (always-on; offline players drop
                // out via ZNet.Disconnect cleanup).
                foreach (var kvp in _peerCompanionPositions)
                {
                    if (kvp.Value.Count > 0)
                        LoadZonesForPositions(kvp.Value);
                }

                // Offline-player coverage. Refresh the cached snapshot
                // periodically by walking ZDOMan (no client cooperation
                // required) and feed those positions into the same
                // keep-alive path. Gated by config so low-spec servers
                // can opt out.
                bool keepOffline = FiresCore.Bridge.NpcConfigBridge.GetBool("KeepZonesLoadedForOfflinePlayers", true);
                if (keepOffline)
                {
                    _allKnownScanTimer += Time.deltaTime;
                    if (_allKnownScanTimer >= AllKnownScanInterval)
                    {
                        _allKnownScanTimer = 0f;
                        RefreshAllKnownCompanionPositions();
                    }

                    if (_allKnownCompanionPositions.Count > 0)
                        LoadZonesForPositions(_allKnownCompanionPositions);
                }
                else if (_allKnownCompanionPositions.Count > 0)
                {
                    // Config flipped from on -> off mid-session; drop the
                    // cached snapshot so the keep-alive loop stops referencing
                    // offline-player companions on the next tick.
                    _allKnownCompanionPositions.Clear();
                    _allKnownScanTimer = 0f;
                }
            }
        }

        /// <summary>
        /// Server-only. Walks ZDOMan for every companion ZDO that's tamed
        /// and has a real owner-player ID, and stores each one's position
        /// in <see cref="_allKnownCompanionPositions"/>. Untamed wild
        /// companion variants are excluded. Skips invalid ZDOs.
        /// </summary>
        private static void RefreshAllKnownCompanionPositions()
        {
            _allKnownCompanionPositions.Clear();
            if (ZDOMan.instance == null) return;

            for (int p = 0; p < _companionPrefabNamesForScan.Length; p++)
            {
                _allKnownScanBuf.Clear();
                int idx = 0;
                while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(_companionPrefabNamesForScan[p], _allKnownScanBuf, ref idx)) { }

                for (int i = 0; i < _allKnownScanBuf.Count; i++)
                {
                    var zdo = _allKnownScanBuf[i];
                    if (zdo == null || !zdo.IsValid()) continue;

                    // Tamed filter â€” wild creatures sharing the prefab don't
                    // qualify. Both flags must be set or we'd be keeping zones
                    // alive for half-tamed wild spawns mid-conversion.
                    if (!zdo.GetBool(ZDOVars.s_tamed, false)) continue;
                    long owner = zdo.GetLong("companion_owner", 0);
                    if (owner == 0) continue;

                    _allKnownCompanionPositions.Add(zdo.GetPosition());
                }
            }
        }

        private static void SendCompanionPositions()
        {
            if (ZRoutedRpc.instance == null) return;

            var pkg = new ZPackage();
            pkg.Write(_localCompanionPositions.Count);
            for (int i = 0; i < _localCompanionPositions.Count; i++)
                pkg.Write(_localCompanionPositions[i]);

            ZRoutedRpc.instance.InvokeRoutedRPC(
                ZRoutedRpc.Everybody, RPC_TamedCompanionPositions, pkg);
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //  Zone loading â€” terrain heightmaps + ghost ZDOs
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        // Reused arg buffer to avoid allocating an object[] every invoke.
        private static readonly object[] _invokeArgs = new object[1];

        private static void LoadZonesForPositions(List<Vector3> positions)
        {
            if (ZoneSystem.instance == null) return;
            if (_createLocalZones == null || _createGhostZones == null) return;

            var zs = ZoneSystem.instance;

            for (int i = 0; i < positions.Count; i++)
            {
                _invokeArgs[0] = positions[i];

                // Heightmaps + collisions â€” needed on every peer that has the
                // companion in scope so AI / ground-snap / pathing all work.
                _createLocalZones.Invoke(zs, _invokeArgs);

                // Ghost ZDOs â€” server-only. This is what makes the companion's
                // ZDO survive if the player wanders away: vanilla normally
                // releases ZDOs whose zone has nobody nearby, but the ghost
                // pass keeps a server-side "interest" in this zone open.
                if (ZNet.instance != null && ZNet.instance.IsServer())
                    _createGhostZones.Invoke(zs, _invokeArgs);
            }
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //  Prevent companion ZDO despawn
        //
        //  ZNetScene.RemoveObjects walks ZDOMan and destroys ZDOs that aren't
        //  in the near or distant earmark lists. We prefix it to add every
        //  ZDO in a tamed-companion's sector to the near list so vanilla
        //  earmarks them and lets them survive the sweep.
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        [HarmonyPatch(typeof(ZNetScene), "RemoveObjects")]
        [HarmonyPrefix]
        private static void RemoveObjects_Prefix(
            List<ZDO> currentNearObjects,
            List<ZDO> currentDistantObjects)
        {
            // SERVER-ONLY earmark. Doing this on the client keeps companion
            // ZDOs (and every other ZDO in their sectors) "near" so
            // ZNetScene.RemoveObjects skips them â€” which sounds reasonable
            // ("don't despawn my pets") but with persistent mode in place
            // the companion's last position can be ANYWHERE in the world,
            // and earmarking those distant sectors holds tens of thousands
            // of trees / structures / mobs in memory on the client after a
            // long-jump. That's a multi-GB FPS-killer.
            //
            // It also feeds the CompanionController teleport-revert loop:
            // earmark keeps the companion's GameObject alive client-side
            // â†’ CheckFollowTeleport fires locally â†’ tries to teleport but
            // client isn't ZDO owner â†’ ZDO sync reverts the position â†’
            // loop. The Phase 2 teleport service makes ownership clean,
            // but the right answer here is to never earmark client-side
            // in the first place.
            //
            // The server's keep-alive (LoadZonesForPositions in
            // ZoneSystem_Update_Postfix above + this earmark when
            // IsServer()) is sufficient on its own â€” companions stay
            // loaded server-side regardless of whether any client
            // earmarks them.
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            _tempPositions.Clear();
            GatherLocalTamedCompanionPositions(_tempPositions);

            foreach (var kvp in _peerCompanionPositions)
                _tempPositions.AddRange(kvp.Value);

            bool keepOffline = FiresCore.Bridge.NpcConfigBridge.GetBool("KeepZonesLoadedForOfflinePlayers", true);
            if (keepOffline && _allKnownCompanionPositions.Count > 0)
                _tempPositions.AddRange(_allKnownCompanionPositions);

            if (_tempPositions.Count == 0) return;

            _companionZDOs.Clear();
            int activeArea = ZoneSystem.instance != null
                ? ZoneSystem.instance.m_activeArea : 1;
            int distantArea = ZoneSystem.instance != null
                ? ZoneSystem.instance.m_activeDistantArea : 1;

            for (int i = 0; i < _tempPositions.Count; i++)
            {
                // Public test renamed zone coordinates from Vector2i to Vector2s
                // on ZoneSystem.GetZone and ZDOMan.FindSectorObjects in unison.
                // Branch by build flag and pass the matching type to vanilla.
#if PUBLIC_TEST
                Vector2s camZone = ZoneSystem.GetZone(_tempPositions[i]);
#else
                Vector2i camZone = ZoneSystem.GetZone(_tempPositions[i]);
#endif
                ZDOMan.instance.FindSectorObjects(
                    camZone, activeArea, distantArea, _companionZDOs);
            }

            if (_companionZDOs.Count > 0)
                currentNearObjects.AddRange(_companionZDOs);
        }

        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        //  Cleanup
        // â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        [HarmonyPostfix]
        private static void ZNet_OnDestroy_Postfix()
        {
            _peerCompanionPositions.Clear();
            _localCompanionPositions.Clear();
            _tempPositions.Clear();
            _companionZDOs.Clear();
            _allKnownCompanionPositions.Clear();
            _allKnownScanBuf.Clear();
            _allKnownScanTimer = 0f;
            _sendTimer = 0f;
        }

        [HarmonyPatch(typeof(ZNet), "Disconnect")]
        [HarmonyPostfix]
        private static void ZNet_Disconnect_Postfix(ZNetPeer peer)
        {
            // Drop the disconnecting peer's positions immediately so we stop
            // keeping zones loaded around their (now-untended) companions.
            // The companions' ZDOs themselves persist in ZDOMan; if/when the
            // player reconnects they'll be re-tracked via the next RPC tick.
            if (peer != null)
                _peerCompanionPositions.Remove(peer.m_uid);
        }
    }
}
