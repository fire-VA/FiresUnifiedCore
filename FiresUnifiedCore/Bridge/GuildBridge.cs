using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Cross-mod query contract for the persistent guild layer. The guild mod (FiresGuilds) registers a single
    /// provider via <see cref="Register"/>; consumers (companion friendly-fire, territory/PvP gating, dialogue
    /// guild conditions, chat, the leaderboard) call the static facade. While no provider is registered every
    /// query is a null-safe no-op, keeping the dependency optional.
    ///
    /// Membership queries are REMOTE-aware: <see cref="GetGuildMemberIds"/>/<see cref="GetGuildNameForPlayer"/>
    /// resolve for any player id (not just the local player), because territory/PvP checks ask about others.
    /// Companion allegiance needs no wiring from a host: <see cref="NpcCompanionBridge.AreOwnersAllied"/>
    /// consults this bridge OR'd with <see cref="GroupBridge.AreGrouped"/> whenever no host has assigned
    /// <see cref="NpcCompanionBridge.OwnersAllied"/>. Player ids are <c>Player.GetPlayerID()</c> (long).
    /// </summary>
    public interface IGuildProvider
    {
        bool AreGuilded(long playerA, long playerB);
        string GetGuildNameForPlayer(long playerId);
        Color? GetGuildColorForPlayer(long playerId);
        IReadOnlyList<long> GetGuildMemberIds(long playerId);
        long GetGuildLeader(long playerId);
        string LocalGuildName();
        void SendGuildChat(string text);
    }

    public static class GuildBridge
    {
        private static IGuildProvider _impl;

        public static bool HasProvider => _impl != null;

        public static void Register(IGuildProvider impl) => _impl = impl;

        public static void Unregister(IGuildProvider impl)
        {
            if (_impl == impl) _impl = null;
        }

        public static bool AreGuilded(long playerA, long playerB)
        {
            if (playerA == 0L || playerB == 0L) return false;
            if (playerA == playerB) return true;
            try { return _impl?.AreGuilded(playerA, playerB) ?? false; } catch { return false; }
        }

        public static string GetGuildNameForPlayer(long playerId)
        {
            try { return _impl?.GetGuildNameForPlayer(playerId); } catch { return null; }
        }

        public static Color? GetGuildColorForPlayer(long playerId)
        {
            try { return _impl?.GetGuildColorForPlayer(playerId); } catch { return null; }
        }

        public static IReadOnlyList<long> GetGuildMemberIds(long playerId)
        {
            try { return _impl?.GetGuildMemberIds(playerId) ?? Array.Empty<long>(); } catch { return Array.Empty<long>(); }
        }

        public static long GetGuildLeader(long playerId)
        {
            try { return _impl?.GetGuildLeader(playerId) ?? 0L; } catch { return 0L; }
        }

        public static string LocalGuildName()
        {
            try { return _impl?.LocalGuildName(); } catch { return null; }
        }

        public static void SendGuildChat(string text)
        {
            try { _impl?.SendGuildChat(text); } catch { }
        }
    }
}
