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
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Companions)]
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

        // Vanilla loads zones every 0.1 s, at most one new zone per reference point per pass; the companions' keep-alive ran every
        // frame instead (a join could cost tens of ms in one frame on a dedicated server). Every position is still visited each pass: CreateLocalZones also
        // pokes the existing zones, which is what keeps them from expiring.
        private const float KeepAliveInterval = 0.1f;
        private static float _keepAliveTimer;

        // Reusable lists to avoid GC churn — gathered fresh every tick.
        private static readonly List<Vector3> _localCompanionPositions = new List<Vector3>();
        private static readonly List<Vector3> _tempPositions = new List<Vector3>();
        /// <summary>Server-held bodies (the Studio god clone) whose zones stay loaded like a player's, ALSO with no player
        /// connected (no NoPlayersOnDedi gate). Set each frame by their owner code on the server; same keep-alive path.</summary>
        public static readonly List<Vector3> ServerHeldPositions = new List<Vector3>();
        private static readonly List<ZDO> _companionZDOs = new List<ZDO>();

        // Reflected ZoneSystem internals — same private methods Valcast reflects.
        // Cached once at ZNet.Start since they never change.
        // Hundreds of SpawnZone calls around parked companions with no peers grew memory by GBs: a
        // dedicated server with NO players connected keeps no zones loaded for parked (offline-owner) companions. A fix: always on.
        private static bool s_noPlayersLogged;
        private static bool NoPlayersOnDedi()
        {
            var zn = ZNet.instance;
            bool none = zn != null && zn.IsDedicated() && zn.GetPeers().Count == 0;
            if (none != s_noPlayersLogged)
            {
                s_noPlayersLogged = none;
                Debug.Log(none
                    ? $"[FiresUnifiedCore] [Companions] dedicated server with no players: parked companions' zones are not kept loaded ({_allKnownCompanionPositions.Count} position(s))"
                    : "[FiresUnifiedCore] [Companions] a player joined: parked companions' zones are kept loaded again");
            }
            return none;
        }

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

        private const int MaxReportedCompanions = 16;
        private const float MaxReportedDistance = 300f;   // metres from the reporting client's character
        private static void RPC_OnCompanionPositions(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            int count = pkg.ReadInt();
            if (count < 0 || count > MaxReportedCompanions) { RpcGuard.Refuse("CompanionPositions", sender, $"{count} positions (max {MaxReportedCompanions})"); return; }
            // A client reports the companions loaded around it, so every position must be near where the server sees that client
            Possession.PossessionServer.PlayerIdOf(sender, out Vector3 senderPos);
            if (!_peerCompanionPositions.TryGetValue(sender, out var positions))
            {
                positions = new List<Vector3>();
                _peerCompanionPositions[sender] = positions;
            }
            positions.Clear();

            for (int i = 0; i < count; i++)
            {
                Vector3 p = pkg.ReadVector3();
                if (senderPos == Vector3.zero || Vector3.Distance(p, senderPos) > MaxReportedDistance) continue;
                positions.Add(p);
            }
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

            // Client: throttled send to server. Sending while the local list is
            // empty is harmless but wasteful — skip when there's nothing to track.
            // 1076 ([ghost], Fire 10-31 event-driven / cache rule): the companion positions are gathered only when a send is due
            // (they were rebuilt every frame and read once a second); with none to send, the next look is 0.25 s later. The
            // positions sent are gathered the same frame they are sent, and a new companion is sent no later than before.
            _sendTimer += Time.deltaTime;
            if (_sendTimer >= SendInterval)
            {
                _localCompanionPositions.Clear();
                GatherLocalTamedCompanionPositions(_localCompanionPositions);
                if (_localCompanionPositions.Count > 0) { _sendTimer = 0f; SendCompanionPositions(); }
                else _sendTimer = SendInterval - 0.25f;
            }

            // Server only. Keeping zones alive on the client held companion GameObjects across long jumps after
            // ownership moved, and the stale ZDO position kept reverting their teleports. Listen hosts are
            // servers, so they still get coverage.
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                _keepAliveTimer += Time.deltaTime;
                bool keepAliveDue = _keepAliveTimer >= KeepAliveInterval;
                if (keepAliveDue) _keepAliveTimer = 0f;

                // Per-online-peer positions (always-on; offline players drop
                // out via ZNet.Disconnect cleanup).
                foreach (var kvp in _peerCompanionPositions)
                {
                    if (keepAliveDue && kvp.Value.Count > 0)
                        LoadZonesForPositions(kvp.Value);
                }
                if (keepAliveDue && ServerHeldPositions.Count > 0) LoadZonesForPositions(ServerHeldPositions);

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

                    if (keepAliveDue && _allKnownCompanionPositions.Count > 0 && !NoPlayersOnDedi())
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

        private static readonly HashSet<Vector2s> _zonesDone = new HashSet<Vector2s>();
        private static float _nextDedupeLog;

        [HarmonyPatch(typeof(ZNetScene), "RemoveObjects")]
        [HarmonyPrefix]
        private static void RemoveObjects_Prefix(
            List<ZDO> currentNearObjects,
            List<ZDO> currentDistantObjects)
        {
            _companionZDOs.Clear();
            if (CollectKept(_companionZDOs) > 0)
                currentNearObjects.AddRange(_companionZDOs);
        }

        /// <summary>
        /// 0.2.1213 ([generator], [lead]/[fgn] 11-08): the ONE place the companion keep is decided, used by the RemoveObjects prefix above
        /// and by <see cref="CompanionKeep"/> (FGN's sliced server removal). Appends to <paramref name="into"/> every ZDO vanilla's
        /// FindSectorObjects returns around each companion position (one call per distinct zone, the synced simulation distance), which is
        /// exactly the set the prefix has always added to vanilla's near list. Server only; returns how many were added.
        /// </summary>
        internal static int CollectKept(List<ZDO> into)
        {
            // Server only. Earmarking on the client kept every sector around a companion's last position in memory
            // after a long jump (gigabytes of trees and pieces) and fed the teleport revert loop; the server's
            // keep-alive covers companions on its own.
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null) return 0;
            int before = into.Count;

            _tempPositions.Clear();
            GatherLocalTamedCompanionPositions(_tempPositions);

            foreach (var kvp in _peerCompanionPositions)
                _tempPositions.AddRange(kvp.Value);
            _tempPositions.AddRange(ServerHeldPositions);

            bool keepOffline = FiresCore.Bridge.NpcConfigBridge.GetBool("KeepZonesLoadedForOfflinePlayers", true);
            if (keepOffline && _allKnownCompanionPositions.Count > 0 && !NoPlayersOnDedi())
                _tempPositions.AddRange(_allKnownCompanionPositions);

            if (_tempPositions.Count == 0) return 0;

            // Valheim 1.0 replaced the m_activeArea / m_activeDistantArea radii with the
            // server-synced SimulationDistance struct that FindSectorObjects now takes.
            SimulationDistance simDistance = ZNet.instance != null
                ? ZNet.instance.GetSyncedSimulationDistance()
                : new SimulationDistance(1, 1, true);

            // One FindSectorObjects per DISTINCT zone - every call uses the same simDistance, so
            // (zone, area) is unique per zone and the loaded set is identical
            _zonesDone.Clear();
            int calls = 0;
            for (int i = 0; i < _tempPositions.Count; i++)
            {
                Vector2s camZone = FiresCore.World.RingZones.ObjectSector(_tempPositions[i]);   // halo ring: the ring object-sector id
                if (!_zonesDone.Add(camZone)) continue;
                ZDOMan.instance.FindSectorObjects(camZone, simDistance, into);
                calls++;
            }
            if (Time.unscaledTime >= _nextDedupeLog)
            {
                _nextDedupeLog = Time.unscaledTime + 60f;
                if (FiresCore.Logging.FiresLogger.DebugEnabled)
                    FiresCore.Logging.FiresLogger.LogDebug($"[CompanionZoneLoader] {_tempPositions.Count} position(s) -> {calls} sector call(s) (distinct zones)");
            }
            return into.Count - before;
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

    /// <summary>
    /// 0.2.1213 ([generator], [lead]/[fgn] 11-08): the companion keep as data, for FGN's sliced server removal (FGN 1.6.60 fell back to the
    /// whole vanilla RemoveObjects every 2 s, a 17-20 ms pass, because the keep lived only in <c>TamedCompanionZoneLoader.RemoveObjects_Prefix</c>).
    /// The set is built by the SAME <c>TamedCompanionZoneLoader.CollectKept</c> the prefix uses (vanilla FindSectorObjects around each companion
    /// zone), so it holds exactly the ZDOs the prefix keeps, including vanilla's near / distant split. The prefix is unchanged for every other caller.
    /// Add-only, server only, main thread. Bind by reflection: type "FiresCore.Npc.CompanionKeep".
    ///   Refresh() at the start of each removal pass (it costs what one prefix call costs); IsKept(zdo) per instance; Active says whether to use it.
    /// </summary>
    public static class CompanionKeep
    {
        private static readonly List<ZDO> s_buf = new List<ZDO>();
        private static readonly HashSet<ZDO> s_kept = new HashSet<ZDO>();
        private static bool s_active, s_prefixLive, s_saidOnce;
        private static float s_prefixCheckAt = -1f;
        private static int s_version;

        /// <summary>True while this server's companion keep is on (the prefix is patched) and the last Refresh kept at least one ZDO.</summary>
        public static bool Active => s_active;

        /// <summary>Bumps each time a Refresh changes the kept count; cheap change hint for callers that cache derived data.</summary>
        public static int Version => s_version;

        /// <summary>Rebuilds the kept set; returns how many ZDOs it holds. Call once per removal pass, on the main thread.</summary>
        public static int Refresh()
        {
            int before = s_kept.Count;
            s_kept.Clear();
            s_buf.Clear();
            if (PrefixLive()) TamedCompanionZoneLoader.CollectKept(s_buf);
            for (int i = 0; i < s_buf.Count; i++) if (s_buf[i] != null) s_kept.Add(s_buf[i]);
            s_buf.Clear();
            bool was = s_active;
            s_active = s_prefixLive && s_kept.Count > 0;
            if (s_kept.Count != before) s_version++;
            if (!s_saidOnce && s_active) { s_saidOnce = true; FiresCore.Logging.FiresLogger.LogInfo($"[CompanionKeep] companion keep exposed as data: {s_kept.Count} ZDO(s) around tamed companions (same set as the RemoveObjects prefix)"); }
            else if (was != s_active && FiresCore.Logging.FiresLogger.DebugEnabled) FiresCore.Logging.FiresLogger.LogDebug($"[CompanionKeep] {(s_active ? "on" : "off")}: {s_kept.Count} ZDO(s), prefix {(s_prefixLive ? "patched" : "not patched")}");
            return s_kept.Count;
        }

        /// <summary>True if the last Refresh kept this ZDO (the prefix would add it to vanilla's near list).</summary>
        public static bool IsKept(ZDO zdo) => zdo != null && s_kept.Contains(zdo);

        // The Companions feature can leave the loader unpatched; then nothing is kept and Active stays false. Re-checked every 5 s, not per call.
        private static bool PrefixLive()
        {
            if (Time.unscaledTime < s_prefixCheckAt) return s_prefixLive;
            s_prefixCheckAt = Time.unscaledTime + 5f;
            s_prefixLive = false;
            try
            {
                var target = AccessTools.Method(typeof(ZNetScene), "RemoveObjects");
                var info = target != null ? Harmony.GetPatchInfo(target) : null;
                if (info != null)
                    foreach (var p in info.Prefixes)
                        if (p.PatchMethod != null && p.PatchMethod.DeclaringType == typeof(TamedCompanionZoneLoader)) { s_prefixLive = true; break; }
            }
            catch { s_prefixLive = false; }
            return s_prefixLive;
        }
    }
}
