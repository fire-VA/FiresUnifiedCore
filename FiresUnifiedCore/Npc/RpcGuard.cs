using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Who sent a routed or ZNetView RPC, from the routed sender only (never a packet field; on the server
    /// Net.RoutedSenderGuard has already replaced a spoofed sender). Any connected client can send any registered RPC
    /// to any peer, so every handler that changes a companion checks one of these first.
    /// </summary>
    internal static class RpcGuard
    {
        private const float RefusalLogEverySeconds = 10f;
        private static readonly Dictionary<string, float> s_lastRefusalLog = new Dictionary<string, float>();

        /// <summary>True when the RPC came from the server: the server peer on a client, this machine on the server.</summary>
        internal static bool SenderIsServer(long sender)
        {
            if (ZNet.instance == null) return false;
            if (ZNet.instance.IsServer()) return sender == 0L || sender == ZDOMan.GetSessionID();
            return ZRoutedRpc.instance != null && sender == ZRoutedRpc.instance.GetServerPeerID();
        }

        /// <summary>The player id of the sender's character, or 0 when it can't be told (not spawned, or out of reach).
        /// Works on the server (its peer list) and on clients (the synced player list: a character's ZDOID carries the
        /// peer uid that made it).</summary>
        internal static long SenderPlayerId(long sender)
        {
            if (ZNet.instance == null) return 0L;
            if (sender == ZDOMan.GetSessionID() && Player.m_localPlayer != null) return Player.m_localPlayer.GetPlayerID();
            if (ZNet.instance.IsServer())
                return Possession.PossessionServer.PlayerIdOf(sender, out _);
            foreach (ZNet.PlayerInfo info in ZNet.instance.GetPlayerList())
            {
                if (info.m_characterID.IsNone() || info.m_characterID.UserID != sender) continue;
                ZDO body = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(info.m_characterID) : null;
                return body != null ? body.GetLong(ZDOVars.s_playerID, 0L) : 0L;
            }
            return 0L;
        }

        /// <summary>True when the sender is the player who owns the companion.</summary>
        internal static bool SenderIsPlayer(long sender, long playerId)
            => playerId != 0L && SenderPlayerId(sender) == playerId;

        /// <summary>True when the sender is the peer that owns (simulates) this ZDO.</summary>
        internal static bool SenderOwnsZdo(long sender, ZDO zdo)
            => zdo != null && zdo.GetOwner() == sender;

        /// <summary>Logs a refused RPC once per RPC and sender every few seconds.</summary>
        internal static void Refuse(string rpc, long sender, string why)
        {
            string key = rpc + "|" + sender;
            if (s_lastRefusalLog.TryGetValue(key, out float last) && Time.time - last < RefusalLogEverySeconds) return;
            s_lastRefusalLog[key] = Time.time;
            Debug.LogWarning($"[RpcGuard] {rpc} refused from peer {sender}: {why}");
        }
    }
}
