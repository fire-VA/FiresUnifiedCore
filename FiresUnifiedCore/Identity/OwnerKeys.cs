using System;
using Splatform;

namespace FiresCore.Identity
{
    // kg-parity economy owner key. kg.Marketplace keys every DB.db row (Bank/Mails/Marketplace/Users)
    // by the PLATFORM user id (SteamID64 / PlayFab id — PlatformUserID.m_userID). Deriving the same key
    // SERVER-side from the sender's socket means: (1) a migrated kg database resolves with zero
    // remapping, (2) bank/mail follow the ACCOUNT across characters exactly like kg, and (3) the key
    // cannot be spoofed through the client payload the way a client-sent playerId could.
    public static class OwnerKeys
    {
        public static string ResolveEconomyOwner(long senderId, long fallbackPlayerId)
        {
            try
            {
                var peer = ZNet.instance != null ? ZNet.instance.GetPeer(senderId) : null;
                string host = peer != null && peer.m_socket != null ? peer.m_socket.GetHostName() : null;
                if (!string.IsNullOrEmpty(host) && host != "0") return host;
            }
            catch { }

            // No peer = the sender is THIS process (host-player / singleplayer): the local platform id,
            // kg's _localUserID equivalent.
            try
            {
                string local = PlatformManager.DistributionPlatform.LocalUser.PlatformUserID.m_userID;
                if (!string.IsNullOrEmpty(local)) return local;
            }
            catch { }

            return fallbackPlayerId.ToString();
        }
    }
}
