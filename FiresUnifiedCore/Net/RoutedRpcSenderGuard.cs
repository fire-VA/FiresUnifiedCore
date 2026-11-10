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

        private const string MarkerSlot = "Fires.RoutedSenderGuard";
        private const string Me = "com.Fire.FiresUnifiedCore";
        private const float LogEverySeconds = 10f;
        private static readonly Dictionary<long, float> s_lastLog = new Dictionary<long, float>();

        private static void Note(long peer, string line)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (s_lastLog.TryGetValue(peer, out float last) && now - last < LogEverySeconds) return;
            s_lastLog[peer] = now;
            FiresLogger.LogDebug("[RoutedRpcGuard] " + line);
        }

        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ZRpc rpc, ZPackage pkg)
        {
            ZNet net = ZNet.instance;
            if (net == null || !net.IsServer() || rpc == null || pkg == null) return true;
            if (!ReferenceEquals(net, _session)) { _session = net; Reported.Clear(); Drops.Clear(); }

            // The AppDomain slot FGN's router checks: with Core here FGN's own copy stands down. If FGN claimed it first, this guard still runs.
            if (AppDomain.CurrentDomain.GetData(MarkerSlot) == null) AppDomain.CurrentDomain.SetData(MarkerSlot, Me);

            ZNetPeer peer = net.GetPeer(rpc);
            if (peer == null) { Note(0L, "dropped a routed RPC from a connection with no peer"); return false; }
            if (peer.m_uid == 0L) return true;

            // 0.2.1176: the layout is read in place, field by field as RoutedRPCData.Deserialize does (no copy of the packet), and the read
            // position put back. A packet too short, or whose parameters length runs past its end, is dropped: vanilla's
            // ReadBytes would read short without complaint and hand a handler a cut parameters package.
            long sender;
            int pos = pkg.GetPos();
            try
            {
                pkg.ReadLong();           // m_msgID
                sender = pkg.ReadLong();  // m_senderPeerID
                pkg.ReadLong();           // m_targetPeerID
                pkg.ReadZDOID();          // m_targetZDO
                pkg.ReadInt();            // m_methodHash
                int len = pkg.ReadInt();  // m_parameters length, as ReadPackage reads it
                if (len < 0 || len > pkg.Size() - pkg.GetPos())
                {
                    Note(peer.m_uid, $"dropped a malformed routed RPC from {peer.m_playerName}: parameters length {len}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Note(peer.m_uid, $"dropped an unreadable routed RPC from {peer.m_playerName}: {ex.GetType().Name}");
                return false;
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
