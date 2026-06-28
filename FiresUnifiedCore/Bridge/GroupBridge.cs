using System;
using System.Collections.Generic;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Cross-mod query contract for the transient party/"group" layer. The guild/group mod (FiresGuilds)
    /// registers a single provider via <see cref="Register"/>; consumers (companions, territory/PvP, portals,
    /// the leaderboard) call the static facade. While no provider is registered every query is a null-safe
    /// no-op (nobody is grouped), so the dependency stays optional.
    ///
    /// The FiresGuilds host is responsible for wiring <see cref="NpcCompanionBridge.OwnersAllied"/> to
    /// <see cref="AreGrouped"/> (OR'd with <see cref="GuildBridge.AreGuilded"/>) — drive that existing ally
    /// hook, do not introduce a parallel ally concept. Player ids are <c>Player.GetPlayerID()</c> (long).
    /// </summary>
    public interface IGroupProvider
    {
        bool AreGrouped(long playerA, long playerB);
        IReadOnlyList<long> GetGroupMembers(long playerId);
        long GetGroupLeader(long playerId);
        bool HasGroup(long playerId);
    }

    public static class GroupBridge
    {
        private static IGroupProvider _impl;

        public static bool HasProvider => _impl != null;

        public static void Register(IGroupProvider impl) => _impl = impl;

        public static void Unregister(IGroupProvider impl)
        {
            if (_impl == impl) _impl = null;
        }

        public static bool AreGrouped(long playerA, long playerB)
        {
            if (playerA == 0L || playerB == 0L) return false;
            if (playerA == playerB) return true;
            try { return _impl?.AreGrouped(playerA, playerB) ?? false; } catch { return false; }
        }

        public static IReadOnlyList<long> GetGroupMembers(long playerId)
        {
            try { return _impl?.GetGroupMembers(playerId) ?? Array.Empty<long>(); } catch { return Array.Empty<long>(); }
        }

        public static long GetGroupLeader(long playerId)
        {
            try { return _impl?.GetGroupLeader(playerId) ?? 0L; } catch { return 0L; }
        }

        public static bool HasGroup(long playerId)
        {
            try { return _impl?.HasGroup(playerId) ?? false; } catch { return false; }
        }
    }
}
