using System;
using System.Collections.Generic;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Events
{
    /// <summary>
    /// 0.2.1074 (Fire 10-31: "anytime our checks can become event driven instead of per frame ... we should be doing that cleanly"; the
    /// merged list in Tools\EVENT_DRIVEN_generator_20261031.md section F). Add-only events a mod subscribes to instead of polling. Each one
    /// is raised from the vanilla method every path goes through, and the "ready" ones REPLAY: a subscriber that comes late is called at
    /// once with the current state (use the When* methods; the plain events are for reflection binders that check the Is* flags first).
    /// Every handler is called in try/catch on the main thread. Debug: "[CoreEvents] &lt;event&gt; raised (...) to N subscriber(s)".
    ///  - AllPluginsLoaded: the first FejdStartup.Awake (client) or ZNet.Awake (dedicated) - every plugin's Awake has run (CoreLateOwners).
    ///  - WorldReady(uid): the world id is known - ZNet.Awake on a server / host, ZNet.RPC_PeerInfo on a client. WorldClosed: ZNet.OnDestroy.
    ///  - RoutedRpcReady(instance): every new ZRoutedRpc (its ctor; a relog makes a new one), so RPCs register once per instance.
    ///  - LocalPlayerSpawned(player) / LocalPlayerDespawned: Player.OnSpawned / Player.OnDestroy of the local player (respawn included).
    ///  - PeerJoined(peer) / PeerLeft(peer) (server): ZNet.RPC_PeerInfo accepted / ZNet.Disconnect.
    ///  - PieceAppeared(piece) / PieceGone(piece): Piece.Awake / Piece.OnDestroy - every path: placed, built by a peer, loaded with its
    ///    zone, removed, destroyed, unloaded. EbmStandInsChanged(zone): EBM moved a zone's piece colliders to / from its stand-ins.
    ///  - LocalBiomeChanged(old, new): Player.UpdateBiome when the local player's biome changes.
    /// </summary>
    public static class CoreEvents
    {
        public static event Action AllPluginsLoaded;
        public static event Action<long> WorldReady;
        public static event Action WorldClosed;
        public static event Action<ZRoutedRpc> RoutedRpcReady;
        public static event Action<Player> LocalPlayerSpawned;
        public static event Action LocalPlayerDespawned;
        public static event Action<ZNetPeer> PeerJoined;
        public static event Action<ZNetPeer> PeerLeft;
        public static event Action<Piece> PieceAppeared;
        public static event Action<Piece> PieceGone;
        public static event Action<Vector2s> EbmStandInsChanged;
        public static event Action<Heightmap.Biome, Heightmap.Biome> LocalBiomeChanged;

        public static bool IsAllPluginsLoaded { get; private set; }
        public static bool IsWorldReady { get; private set; }
        public static long WorldUid { get; private set; }

        /// <summary>Calls <paramref name="a"/> once all plugins have loaded (at once if that already happened).</summary>
        public static void WhenAllPluginsLoaded(Action a)
        {
            if (a == null) return;
            if (IsAllPluginsLoaded) Call(a, nameof(AllPluginsLoaded)); else AllPluginsLoaded += a;
        }

        /// <summary>Subscribes to WorldReady and replays it at once when a world is already up.</summary>
        public static void WhenWorldReady(Action<long> a)
        {
            if (a == null) return;
            WorldReady += a;
            if (IsWorldReady) Call(() => a(WorldUid), nameof(WorldReady));
        }

        /// <summary>Subscribes to RoutedRpcReady and replays it at once with the current instance.</summary>
        public static void WhenRoutedRpcReady(Action<ZRoutedRpc> a)
        {
            if (a == null) return;
            RoutedRpcReady += a;
            if (ZRoutedRpc.instance != null) Call(() => a(ZRoutedRpc.instance), nameof(RoutedRpcReady));
        }

        /// <summary>Subscribes to LocalPlayerSpawned and replays it at once when the local player is already up.</summary>
        public static void WhenLocalPlayerSpawned(Action<Player> a)
        {
            if (a == null) return;
            LocalPlayerSpawned += a;
            if (Player.m_localPlayer != null) Call(() => a(Player.m_localPlayer), nameof(LocalPlayerSpawned));
        }

        internal static void RaiseAllPluginsLoaded()
        {
            if (IsAllPluginsLoaded) return;
            IsAllPluginsLoaded = true;
            var h = AllPluginsLoaded; AllPluginsLoaded = null;   // one-shot: later subscribers are called at once
            Raise(h, nameof(AllPluginsLoaded), d => ((Action)d)());
        }

        internal static void RaiseEbmStandInsChanged(Vector2s zone) => Raise(EbmStandInsChanged, nameof(EbmStandInsChanged), d => ((Action<Vector2s>)d)(zone), quiet: true);

        private static void Call(Action a, string name)
        {
            try { a(); } catch (Exception e) { FiresLogger.LogWarning($"[CoreEvents] a {name} subscriber threw: {e.Message}"); }
        }

        private static void Raise(Delegate h, string name, Action<Delegate> call, string detail = null, bool quiet = false)
        {
            int n = 0;
            if (h != null)
                foreach (var d in h.GetInvocationList())
                {
                    n++;
                    try { call(d); } catch (Exception e) { FiresLogger.LogWarning($"[CoreEvents] {name} subscriber {d.Method.DeclaringType?.FullName}.{d.Method.Name} threw: {e.Message}"); }
                }
            if (!quiet) FiresLogger.LogDebug($"[CoreEvents] {name} raised{(detail != null ? " (" + detail + ")" : "")} to {n} subscriber(s)");
        }

        private static void SetWorld(long uid, string where)
        {
            IsWorldReady = true; WorldUid = uid;
            Raise(WorldReady, nameof(WorldReady), d => ((Action<long>)d)(uid), $"uid {uid}, {where}");
        }

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class ZNet_Awake
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ZNet __instance)
            {
                try { if (__instance.IsServer() && ZNet.World != null) SetWorld(ZNet.World.m_uid, "server start"); } catch (Exception e) { FiresLogger.LogDebug($"[CoreEvents] world ready (server) skipped: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
        private static class ZNet_RPC_PeerInfo
        {
            private static void Prefix(ZNet __instance, ZRpc rpc, out bool __state)
            {
                __state = false;
                try { foreach (var p in __instance.GetPeers()) if (p.m_rpc == rpc) { __state = p.m_uid != 0L; break; } } catch { }
            }

            private static void Postfix(ZNet __instance, ZRpc rpc, bool __state)
            {
                try
                {
                    if (!__instance.IsServer())
                    {
                        if (!IsWorldReady && ZNet.World != null) SetWorld(ZNet.World.m_uid, "joined server");
                        return;
                    }
                    if (__state) return;   // that peer was already known
                    foreach (var p in __instance.GetPeers())
                        if (p.m_rpc == rpc && p.m_uid != 0L) { var peer = p; Raise(PeerJoined, nameof(PeerJoined), d => ((Action<ZNetPeer>)d)(peer), $"uid {p.m_uid}"); break; }
                }
                catch (Exception e) { FiresLogger.LogDebug($"[CoreEvents] peer info skipped: {e.Message}"); }
            }
        }

        [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
        private static class ZNet_Disconnect
        {
            private static void Prefix(ZNet __instance, ZNetPeer peer)
            {
                if (peer == null || !__instance.IsServer() || peer.m_uid == 0L) return;
                Raise(PeerLeft, nameof(PeerLeft), d => ((Action<ZNetPeer>)d)(peer), $"uid {peer.m_uid}");
            }
        }

        [HarmonyPatch(typeof(ZNet), "OnDestroy")]
        private static class ZNet_OnDestroy
        {
            private static void Prefix()
            {
                if (!IsWorldReady) return;
                IsWorldReady = false; WorldUid = 0L;
                Raise(WorldClosed, nameof(WorldClosed), d => ((Action)d)());
            }
        }

        [HarmonyPatch(typeof(ZRoutedRpc), MethodType.Constructor, new[] { typeof(bool) })]
        private static class ZRoutedRpc_Ctor
        {
            private static void Postfix(ZRoutedRpc __instance)
            {
                var inst = __instance;
                Raise(RoutedRpcReady, nameof(RoutedRpcReady), d => ((Action<ZRoutedRpc>)d)(inst));
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class Player_OnSpawned
        {
            private static void Postfix(Player __instance)
            {
                if (__instance == null || __instance != Player.m_localPlayer) return;
                var pl = __instance;
                Raise(LocalPlayerSpawned, nameof(LocalPlayerSpawned), d => ((Action<Player>)d)(pl));
            }
        }

        [HarmonyPatch(typeof(Player), "OnDestroy")]
        private static class Player_OnDestroy
        {
            private static void Prefix(Player __instance)
            {
                if (__instance == null || __instance != Player.m_localPlayer) return;
                Raise(LocalPlayerDespawned, nameof(LocalPlayerDespawned), d => ((Action)d)());
            }
        }

        [HarmonyPatch(typeof(Piece), "Awake")]
        private static class Piece_Awake
        {
            private static void Postfix(Piece __instance)
            {
                var h = PieceAppeared;
                if (h == null || __instance == null) return;
                var pc = __instance;
                Raise(h, nameof(PieceAppeared), d => ((Action<Piece>)d)(pc), quiet: true);
            }
        }

        [HarmonyPatch(typeof(Piece), "OnDestroy")]
        private static class Piece_OnDestroy
        {
            private static void Prefix(Piece __instance)
            {
                var h = PieceGone;
                if (h == null || __instance == null) return;
                var pc = __instance;
                Raise(h, nameof(PieceGone), d => ((Action<Piece>)d)(pc), quiet: true);
            }
        }

        private static readonly AccessTools.FieldRef<Player, Heightmap.Biome> s_biome = AccessTools.FieldRefAccess<Player, Heightmap.Biome>("m_currentBiome");

        [HarmonyPatch(typeof(Player), "UpdateBiome")]
        private static class Player_UpdateBiome
        {
            private static void Prefix(Player __instance, out Heightmap.Biome __state) => __state = __instance != null ? s_biome(__instance) : Heightmap.Biome.None;

            private static void Postfix(Player __instance, Heightmap.Biome __state)
            {
                if (__instance == null || __instance != Player.m_localPlayer) return;
                var now = s_biome(__instance);
                if (now == __state) return;
                Raise(LocalBiomeChanged, nameof(LocalBiomeChanged), d => ((Action<Heightmap.Biome, Heightmap.Biome>)d)(__state, now), $"{__state} -> {now}");
            }
        }
    }
}
