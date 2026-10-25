using System;
using System.Collections.Generic;
using FiresCore.Logging;
using HarmonyLib;

namespace FiresCore.Net
{
    // ── The server drops routed RPCs whose sender id is forged ──
    // Vanilla ZRoutedRpc.RPC_RoutedRPC trusts the m_senderPeerID written inside the packet. Every handler then gets that
    // id as "who sent this", so a modified client can send any routed RPC as another player (VAngarde's VA_vgr: get
    // someone else kicked) and the server relays it to everyone under the victim's name.
    //
    // On a server, a client connection only ever carries that client's own packets: vanilla InvokeRoutedRPC always writes
    // the caller's m_id (ZDOMan session id), which is the uid the client sent in PeerInfo (ZNet.GetUID), and vanilla
    // refuses a second connection with a uid already in use. So m_senderPeerID != the connection's m_uid only happens on
    // a forged packet. It is dropped before vanilla (or FGN's router, which stands down on !__runOriginal) sees it.
    // Clients are untouched: the server relays other players' packets to them with the original sender, as it should.
    [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
    internal static class RoutedRpcSenderGuard
    {
        private static readonly HashSet<long> Reported = new HashSet<long>();
        private static readonly Dictionary<long, int> Drops = new Dictionary<long, int>();
        private static ZNet _session; // the counts belong to one server session; a new ZNet starts them over

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ZRpc rpc, ZPackage pkg)
        {
            ZNet net = ZNet.instance;
            if (net == null || !net.IsServer() || rpc == null || pkg == null) return true;
            if (!ReferenceEquals(net, _session)) { _session = net; Reported.Clear(); Drops.Clear(); }

            ZNetPeer peer = null;
            foreach (ZNetPeer p in net.GetPeers())
                if (p != null && p.m_rpc == rpc) { peer = p; break; }
            if (peer == null || peer.m_uid == 0L) return true;

            // RoutedRPCData.Deserialize reads m_msgID, then m_senderPeerID: peek both and put the read position back.
            long sender;
            int pos = pkg.GetPos();
            try
            {
                pkg.ReadLong();
                sender = pkg.ReadLong();
            }
            catch (Exception)
            {
                return true; // too short to be a routed RPC; vanilla's own Deserialize deals with it as before
            }
            finally
            {
                pkg.SetPos(pos);
            }
            if (sender == peer.m_uid) return true;

            Drops.TryGetValue(peer.m_uid, out int n);
            Drops[peer.m_uid] = ++n;
            if (Reported.Add(peer.m_uid))
                FiresLogger.LogWarning($"[RoutedRpcGuard] dropped a routed RPC from {peer.m_playerName} (uid {peer.m_uid}) that claimed to come from uid {sender}; later drops from this peer are counted at Debug.");
            else if (n == 10 || n == 100 || n == 1000 || n % 10000 == 0)
                FiresLogger.LogDebug($"[RoutedRpcGuard] {n} forged routed RPC(s) dropped from {peer.m_playerName} (uid {peer.m_uid}); last claimed uid {sender}.");
            return false;
        }
    }
}
