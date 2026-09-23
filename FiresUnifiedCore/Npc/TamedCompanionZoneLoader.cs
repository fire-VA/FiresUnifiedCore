using HarmonyLib;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Keeps zones loaded around each player's tamed companions so they don't unload, drop through terrain or
    /// miss teleports while no player is nearby. Clients report their tamed companions' positions to the
    /// server on an interval; the server creates local and ghost zones around them and adds their ZDOs to
    /// ZNetScene's keep list, forgetting a peer's positions when it disconnects. Uses the same hooks as
    /// FiresValcast's CameraZoneLoader. Only companions with isTamed and an owner qualify.
    /// </summary>
    [HarmonyPatch]
    public static class TamedCompanionZoneLoader
    {
        // RPC name — namespaced so it can't collide with Valcast's analogous RPC.
        private const string RPC_TamedCompanionPositions = "FiresRPGmaker_TamedCompanionPositions";

        // Client send cadence. 1Hz matches Valcast and is plenty for zone loading
        // (zones are 64×64m, a companion would have to teleport faster than that
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
        // those belonging to offline players. Refreshed periodically from the
        // companion census (no client cooperation required). When merged with
        // _peerCompanionPositions in the keep-alive loop, this is what makes
        // companions persistent participants in the world even when their
        // owner is logged out — the companion's zone stays loaded server-side
        // so the GameObject continues ticking (combat, idle behaviors,
        // wandering). Empty when KeepZonesLoadedForOfflinePlayers is false.
        private static readonly List<Vector3> _allKnownCompanionPositions = new List<Vector3>();
        private static readonly List<ZDO> _allKnownScanBuf = new List<ZDO>();
        private static float _allKnownScanTimer;

        // Cadence of the offline-companion refresh from the census.
        private const float AllKnownScanInterval = 5f;

        // Reusable lists to avoid GC churn — gathered fresh every tick.
        private static readonly List<Vector3> _localCompanionPositions = new List<Vector3>();
        private static readonly List<Vector3> _tempPositions = new List<Vector3>();
        private static readonly List<ZDO> _companionZDOs = new List<ZDO>();

        // Reflected ZoneSystem internals — same private methods Valcast reflects.
        // Cached once at ZNet.Start since they never change.
        private static MethodInfo _createLocalZones;
        private static MethodInfo _createGhostZones;

        // ────────────────────────────────────────────────────────────────────
        //  Position gathering
        // ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Walks <see cref="CompanionController.AllCompanions"/> (loaded on this
        /// client only) and appends positions of tamed companions belonging to
        /// the local player. Only loaded companions can be gathered locally,
        /// which is fine — the WHOLE POINT of this loader is to make sure
        /// loaded companions stay loaded going forward, so once a companion
        /// has streamed in once it never falls off the tracked list.
        /// </summary>
        private static void GatherLocalTamedCompanionPositions(List<Vector3> target)
        {
            var localPlayer = Player.m_localPlayer;
            if (localPlayer == null) return;
            long localPlayerId = localPlayer.GetPlayerID();
            if (localPlayerId == 0) return;

            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null) continue;
                if (!companion.isTamed) continue;
                if (companion.ownerPlayerId != localPlayerId) continue;

                target.Add(companion.transform.position);
            }
        }

        // ────────────────────────────────────────────────────────────────────
        //  RPC registration + reflection cache
        // ────────────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(ZNet), "Start")]
        [HarmonyPostfix]
        private static void ZNet_Start_Postfix()
        {
            if (ZRoutedRpc.instance == null) return;

            ZRoutedRpc.instance.Register<ZPackage>(
                RPC_TamedCompanionPositions,
                new System.Action<long, ZPackage>(RPC_OnCompanionPositions));

            Archetypes.AbilityRPCManager.Initialize();

            CacheReflection();
        }

        private static void CacheReflection()
        {
            const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;
            _createLocalZones = typeof(ZoneSystem).GetMethod("CreateLocalZones", instanceFlags);
            _createGhostZones = typeof(ZoneSystem).GetMethod("CreateGhostZones", instanceFlags);
        }

        // ────────────────────────────────────────────────────────────────────
        //  Server: receive positions from clients
        // ────────────────────────────────────────────────────────────────────

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

        // ────────────────────────────────────────────────────────────────────
        //  Per-tick: client sends, both sides load zones
        //
        //  Postfix on ZoneSystem.Update so we ride the same 0.1s cadence the
        //  vanilla zone loader uses.
        // ────────────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(ZoneSystem), "Update")]
        [HarmonyPostfix]
        private static void ZoneSystem_Update_Postfix()
        {
            if (ZNet.GetConnectionStatus() != ZNet.ConnectionStatus.Connected) return;

            _localCompanionPositions.Clear();
            GatherLocalTamedCompanionPositions(_localCompanionPositions);

            // Client: throttled send to server. Sending while the local list is
            // empty is harmless but wasteful — skip when there's nothing to track.
            if (_localCompanionPositions.Count > 0)
            {
                _sendTimer += Time.deltaTime;
                if (_sendTimer >= SendInterval)
                {
                    _sendTimer = 0f;
                    SendCompanionPositions();
                }
            }

            // Server only. Keeping zones alive on the client held companion GameObjects across long jumps after
            // ownership moved, and the stale ZDO position kept reverting their teleports. Listen hosts are
            // servers, so they still get coverage.
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
        /// Server-only. Stores the position of every companion ZDO in the census that's tamed and has a real
        /// owner-player ID in <see cref="_allKnownCompanionPositions"/>. Untamed wild companion variants are excluded.
        /// </summary>
        private static void RefreshAllKnownCompanionPositions()
        {
            _allKnownCompanionPositions.Clear();
            Core.CompanionZdoCensus.Collect(_allKnownScanBuf);

            for (int i = 0; i < _allKnownScanBuf.Count; i++)
            {
                var zdo = _allKnownScanBuf[i];

                // Tamed filter — wild creatures sharing the prefab don't
                // qualify. Both flags must be set or we'd be keeping zones
                // alive for half-tamed wild spawns mid-conversion.
                if (!zdo.GetBool(ZDOVars.s_tamed, false)) continue;
                long owner = zdo.GetLong("companion_owner", 0);
                if (owner == 0) continue;

                _allKnownCompanionPositions.Add(zdo.GetPosition());
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

        // ────────────────────────────────────────────────────────────────────
        //  Zone loading — terrain heightmaps + ghost ZDOs
        // ────────────────────────────────────────────────────────────────────

        // Reused arg buffer to avoid allocating an object[] every invoke.
        private static readonly object[] _invokeArgs = new object[1];

        private static void LoadZonesForPositions(List<Vector3> positions)
        {
            if (ZoneSystem.instance == null) return;
            if (_createLocalZones == null || _createGhostZones == null) return;

            var zoneSystem = ZoneSystem.instance;

            for (int i = 0; i < positions.Count; i++)
            {
                _invokeArgs[0] = positions[i];

                // Heightmaps + collisions — needed on every peer that has the
                // companion in scope so AI / ground-snap / pathing all work.
                _createLocalZones.Invoke(zoneSystem, _invokeArgs);

                // Ghost ZDOs — server-only. This is what makes the companion's
                // ZDO survive if the player wanders away: vanilla normally
                // releases ZDOs whose zone has nobody nearby, but the ghost
                // pass keeps a server-side "interest" in this zone open.
                if (ZNet.instance != null && ZNet.instance.IsServer())
                    _createGhostZones.Invoke(zoneSystem, _invokeArgs);
            }
        }

        // ────────────────────────────────────────────────────────────────────
        //  Prevent companion ZDO despawn
        //
        //  ZNetScene.RemoveObjects walks ZDOMan and destroys ZDOs that aren't
        //  in the near or distant earmark lists. We prefix it to add every
        //  ZDO in a tamed-companion's sector to the near list so vanilla
        //  earmarks them and lets them survive the sweep.
        // ────────────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(ZNetScene), "RemoveObjects")]
        [HarmonyPrefix]
        private static void RemoveObjects_Prefix(
            List<ZDO> currentNearObjects,
            List<ZDO> currentDistantObjects)
        {
            // Server only. Earmarking on the client kept every sector around a companion's last position in memory
            // after a long jump (gigabytes of trees and pieces) and fed the teleport revert loop; the server's
            // keep-alive covers companions on its own.
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
            // Valheim 1.0 replaced the m_activeArea / m_activeDistantArea radii with the
            // server-synced SimulationDistance struct that FindSectorObjects now takes.
            SimulationDistance simDistance = ZNet.instance != null
                ? ZNet.instance.GetSyncedSimulationDistance()
                : new SimulationDistance(1, 1, true);

            for (int i = 0; i < _tempPositions.Count; i++)
            {
                Vector2s camZone = ZoneSystem.GetZone(_tempPositions[i]);
                ZDOMan.instance.FindSectorObjects(camZone, simDistance, _companionZDOs);
            }

            if (_companionZDOs.Count > 0)
                currentNearObjects.AddRange(_companionZDOs);
        }

        // ────────────────────────────────────────────────────────────────────
        //  Cleanup
        // ────────────────────────────────────────────────────────────────────

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
