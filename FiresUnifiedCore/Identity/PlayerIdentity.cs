using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Logging;
using HarmonyLib;

namespace FiresCore.Identity
{
    // Server-side player identity capture and lookup, ported from VikingLands.Core's PlayerIdentityService. On connect it
    // reads the peer's Steam id (from the socket host name), session uid, name, last IP and admin status and upserts a
    // record into the shared VaultDatabase. Reflection goes through AccessTools with soft fallbacks, so a renamed field
    // yields empty data rather than an exception; the admin list is read through SyncedList.GetList, since SyncedList
    // implements no list interface.
    public static class PlayerIdentity
    {
        private const int MinSteamIdDigits = 8;

        private static readonly HashSet<long> CapturedSessions = new HashSet<long>();
        private static readonly object Sync = new object();

        public static bool IsServerRuntime()
        {
            return ZNet.instance != null && ZNet.instance.IsServer();
        }

        public static void CapturePeer(ZNetPeer peer)
        {
            if (peer == null || !IsServerRuntime())
            {
                return;
            }

            long sessionUid = GetSessionUid(peer);
            if (sessionUid == 0L)
            {
                FiresLogger.LogWarning("[PlayerIdentity] Peer skipped: session uid not found.");
                return;
            }

            lock (Sync)
            {
                if (CapturedSessions.Contains(sessionUid))
                {
                    return;
                }
            }

            PlayerIdentityRecord record = BuildRecord(peer, sessionUid);
            if (record == null)
            {
                return;
            }

            PlayerIdentityRepository.UpsertSession(record);

            lock (Sync)
            {
                CapturedSessions.Add(sessionUid);
            }

            FiresLogger.LogInfo("[PlayerIdentity] Session recorded steamId='" + record.SteamId + "' uid=" + record.ConnectionUid + " name='" + record.PlayerName + "' admin=" + record.IsAdmin + ".");
        }

        public static void ForgetSession(long sessionUid)
        {
            if (sessionUid == 0L)
            {
                return;
            }

            lock (Sync)
            {
                CapturedSessions.Remove(sessionUid);
            }
        }

        public static void ClearRuntimeState()
        {
            lock (Sync)
            {
                CapturedSessions.Clear();
            }
        }

        public static List<PlayerIdentityRecord> GetRecent(int limit) => PlayerIdentityRepository.GetRecent(limit);
        public static List<PlayerIdentityRecord> GetAdmins(int limit) => PlayerIdentityRepository.GetAdmins(limit);
        public static List<PlayerIdentityRecord> SearchByName(string playerName, int limit) => PlayerIdentityRepository.SearchByName(playerName, limit);
        public static List<PlayerIdentityRecord> GetBySteamId(string steamId) => PlayerIdentityRepository.GetBySteamId(steamId);
        public static List<PlayerIdentityGroupedDto> GetGrouped(int limit) => PlayerIdentityRepository.GetGrouped(limit);
        public static PlayerIdentityStatsDto GetStats() => PlayerIdentityRepository.GetStats();

        private static PlayerIdentityRecord BuildRecord(ZNetPeer peer, long sessionUid)
        {
            string steamId = ResolveSteamId(peer);
            string playerName = GetPlayerName(peer);
            string lastIp = ResolveLastIp(peer);
            bool isAdmin = ResolveIsAdmin(peer, sessionUid, playerName, steamId);

            return new PlayerIdentityRecord
            {
                SteamId = steamId ?? string.Empty,
                ConnectionUid = sessionUid,
                PlayerName = playerName ?? string.Empty,
                LastConnectionUtc = DateTime.UtcNow,
                LastIp = lastIp ?? string.Empty,
                IsAdmin = isAdmin
            };
        }

        private static string ResolveSteamId(ZNetPeer peer)
        {
            if (peer == null)
            {
                return string.Empty;
            }

            try
            {
                object direct = AccessTools.Field(peer.GetType(), "m_socket")?.GetValue(peer);
                if (direct != null)
                {
                    string socketSteam = NormalizeSteamId(ResolveSocketIdentityValue(direct));
                    if (!string.IsNullOrWhiteSpace(socketSteam))
                    {
                        return socketSteam;
                    }
                }
            }
            catch
            {
            }

            try
            {
                object reflected = AccessTools.Field(peer.GetType(), "m_characterID")?.GetValue(peer)
                    ?? AccessTools.Property(peer.GetType(), "m_characterID")?.GetValue(peer, null)
                    ?? AccessTools.Field(peer.GetType(), "m_platformUserID")?.GetValue(peer)
                    ?? AccessTools.Property(peer.GetType(), "m_platformUserID")?.GetValue(peer, null)
                    ?? AccessTools.Property(peer.GetType(), "PlatformUserID")?.GetValue(peer, null);

                if (reflected != null)
                {
                    object userId = AccessTools.Property(reflected.GetType(), "UserID")?.GetValue(reflected, null)
                        ?? AccessTools.Method(reflected.GetType(), "get_UserID")?.Invoke(reflected, null)
                        ?? reflected;
                    string normalized = NormalizeSteamId(userId?.ToString());
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        return normalized;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ResolveSocketIdentityValue(object socket)
        {
            if (socket == null)
            {
                return string.Empty;
            }

            try
            {
                ISocket iSocket = socket as ISocket;
                if (iSocket != null)
                {
                    string hostName = iSocket.GetHostName();
                    if (!string.IsNullOrWhiteSpace(hostName))
                    {
                        return hostName;
                    }
                }
            }
            catch
            {
            }

            try
            {
                object hostField = AccessTools.Field(socket.GetType(), "m_hostName")?.GetValue(socket)
                    ?? AccessTools.Field(socket.GetType(), "<m_hostName>k__BackingField")?.GetValue(socket)
                    ?? AccessTools.Property(socket.GetType(), "HostName")?.GetValue(socket, null);
                if (hostField != null)
                {
                    return hostField.ToString();
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ResolveLastIp(ZNetPeer peer)
        {
            if (peer == null)
            {
                return string.Empty;
            }

            try
            {
                object socket = AccessTools.Field(peer.GetType(), "m_socket")?.GetValue(peer);
                if (socket == null)
                {
                    return string.Empty;
                }

                ISocket iSocket = socket as ISocket;
                if (iSocket != null)
                {
                    try
                    {
                        string endpoint = iSocket.GetEndPointString();
                        if (!string.IsNullOrWhiteSpace(endpoint))
                        {
                            return endpoint;
                        }
                    }
                    catch
                    {
                    }

                    try
                    {
                        string hostName = iSocket.GetHostName();
                        if (!string.IsNullOrWhiteSpace(hostName))
                        {
                            return hostName;
                        }
                    }
                    catch
                    {
                    }
                }

                object endpointField = AccessTools.Field(socket.GetType(), "m_endPoint")?.GetValue(socket)
                    ?? AccessTools.Property(socket.GetType(), "EndPoint")?.GetValue(socket, null)
                    ?? AccessTools.Property(socket.GetType(), "m_endPoint")?.GetValue(socket, null);
                if (endpointField != null)
                {
                    string value = endpointField.ToString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }

                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool ResolveIsAdmin(ZNetPeer peer, long sessionUid, string playerName, string steamId)
        {
            try
            {
                // ZNet.m_adminList is a SyncedList, which implements no list interface — `as IList`
                // always returned null, so ResolveIsAdmin was always false. Pull its backing List<string>.
                object syncedAdminList = AccessTools.Field(typeof(ZNet), "m_adminList")?.GetValue(ZNet.instance);
                IList adminList = syncedAdminList == null
                    ? null
                    : AccessTools.Method(syncedAdminList.GetType(), "GetList")?.Invoke(syncedAdminList, null) as IList;
                if (adminList == null)
                {
                    return false;
                }

                HashSet<string> keys = BuildIdentityKeys(steamId, sessionUid, playerName);
                foreach (object adminEntry in adminList)
                {
                    if (adminEntry == null)
                    {
                        continue;
                    }

                    string raw = adminEntry.ToString();
                    string normalizedSteam = NormalizeSteamId(raw);
                    if (!string.IsNullOrWhiteSpace(normalizedSteam) && keys.Contains(normalizedSteam))
                    {
                        return true;
                    }

                    string trimmed = (raw ?? string.Empty).Trim();
                    if (keys.Contains(trimmed))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static HashSet<string> BuildIdentityKeys(string steamId, long sessionUid, string playerName)
        {
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string normalizedSteam = NormalizeSteamId(steamId);
            if (!string.IsNullOrWhiteSpace(normalizedSteam))
            {
                keys.Add(normalizedSteam);
                if (normalizedSteam.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase))
                {
                    keys.Add(normalizedSteam.Substring("Steam_".Length));
                }
            }

            if (sessionUid != 0L)
            {
                keys.Add(sessionUid.ToString());
            }

            if (!string.IsNullOrWhiteSpace(playerName))
            {
                keys.Add(playerName.Trim());
            }

            return keys;
        }

        private static long GetSessionUid(ZNetPeer peer)
        {
            if (peer == null)
            {
                return 0L;
            }

            object value = AccessTools.Field(peer.GetType(), "m_uid")?.GetValue(peer)
                ?? AccessTools.Property(peer.GetType(), "m_uid")?.GetValue(peer, null);

            if (value is long longValue)
            {
                return longValue;
            }

            if (value is ulong ulongValue)
            {
                return unchecked((long)ulongValue);
            }

            return 0L;
        }

        private static string GetPlayerName(ZNetPeer peer)
        {
            if (peer == null)
            {
                return string.Empty;
            }

            return AccessTools.Field(peer.GetType(), "m_playerName")?.GetValue(peer) as string
                ?? AccessTools.Property(peer.GetType(), "m_playerName")?.GetValue(peer, null) as string
                ?? string.Empty;
        }

        public static string NormalizeSteamId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string trimmed = value.Trim();
            if (trimmed.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase))
            {
                string digits = new string(trimmed.Substring("Steam_".Length).Where(char.IsDigit).ToArray());
                return string.IsNullOrWhiteSpace(digits) ? string.Empty : "Steam_" + digits;
            }

            string digitsOnly = new string(trimmed.Where(char.IsDigit).ToArray());
            return !string.IsNullOrWhiteSpace(digitsOnly) && digitsOnly.Length >= MinSteamIdDigits
                ? "Steam_" + digitsOnly
                : string.Empty;
        }
    }
}
