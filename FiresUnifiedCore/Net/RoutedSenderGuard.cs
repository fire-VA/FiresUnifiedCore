using System;
using System.Collections.Generic;
using FiresCore.Logging;
using HarmonyLib;

namespace FiresCore.Net
{
    /// <summary>
    /// Server side: a routed RPC names its sender inside the packet, and vanilla trusts that id. Every packet that arrives on a
    /// connection gets the connection's own peer id written over the claimed one, so every sender check downstream (server-only,
    /// admin, owner) sees the real peer. A packet from an unknown connection is dropped. The same rule runs in FGN's router; one
    /// owner per process (the AppDomain slot below) so the two never rewrite twice.
    /// </summary>
    [HarmonyPatch(typeof(ZRoutedRpc), "RPC_RoutedRPC")]
    internal static class RoutedSenderGuard
    {
        private const string MarkerSlot = "Fires.RoutedSenderGuard";
        private const string Me = "com.Fire.FiresUnifiedCore";
        private const float LogEverySeconds = 10f;
        private static readonly Dictionary<long, float> s_lastLog = new Dictionary<long, float>();

        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(ZRpc rpc, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || rpc == null || pkg == null) return true;
            object owner = AppDomain.CurrentDomain.GetData(MarkerSlot);
            if (owner == null) { AppDomain.CurrentDomain.SetData(MarkerSlot, Me); owner = Me; }
            if (!Me.Equals(owner)) return true;   // FGN's router guards this process

            ZNetPeer peer = ZNet.instance.GetPeer(rpc);
            if (peer == null) { Note(0L, "dropped a routed RPC from a connection with no peer"); return false; }

            var data = new ZRoutedRpc.RoutedRPCData();
            try
            {
                int pos = pkg.GetPos();
                data.Deserialize(new ZPackage(pkg.GetArray()));
                pkg.SetPos(pos);
            }
            catch (Exception ex) { Note(peer.m_uid, $"dropped an unreadable routed RPC from {peer.m_playerName}: {ex.GetType().Name}"); return false; }

            if (data.m_senderPeerID == peer.m_uid) return true;
            long claimed = data.m_senderPeerID;
            data.m_senderPeerID = peer.m_uid;
            pkg.Clear();
            data.Serialize(pkg);
            pkg.SetPos(0);
            Note(peer.m_uid, $"[RpcGuard] corrected sender {claimed} -> {peer.m_uid} for method {data.m_methodHash} from {peer.m_playerName}");
            return true;
        }

        private static void Note(long peer, string line)
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (s_lastLog.TryGetValue(peer, out float last) && now - last < LogEverySeconds) return;
            s_lastLog[peer] = now;
            FiresLogger.LogDebug(line);
        }
    }
}
