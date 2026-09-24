using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Cross-mod query contract for the transient party/"group" layer. The party mod (FiresRPGmaker)
    /// registers a single provider via <see cref="Register"/>; consumers (companions, territory/PvP, portals,
    /// the leaderboard) call the static facade. While no provider is registered every query is a null-safe
    /// no-op (nobody is grouped), and the first query against an unregistered bridge logs one warning, so an
    /// unwired bridge shows up in the log instead of quietly answering "no" forever. <see cref="HasProvider"/>
    /// stays silent: it is the polite pre-check, and consumers that use it report their own degradation.
    ///
    /// Companion allegiance needs no wiring from a host: <see cref="NpcCompanionBridge.AreOwnersAllied"/>
    /// consults this bridge OR'd with <see cref="GuildBridge.AreGuilded"/> whenever no host has assigned
    /// <see cref="NpcCompanionBridge.OwnersAllied"/>. Player ids are <c>Player.GetPlayerID()</c> (long).
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
        private const string LogPrefix = "[GroupBridge]";

        private static IGroupProvider _impl;
        private static bool _reportedMissingProvider;

        public static bool HasProvider => _impl != null;

        public static void Register(IGroupProvider impl)
        {
            if (impl == null)
            {
                Debug.LogError($"{LogPrefix} Register(null) - the caller has no group provider to offer.");
                return;
            }
            if (_impl != null && !ReferenceEquals(_impl, impl))
            {
                Debug.LogWarning($"{LogPrefix} {impl.GetType().FullName} is replacing {_impl.GetType().FullName} " +
                                 "as the group provider - only one mod may own party membership.");
            }
            _impl = impl;
            _reportedMissingProvider = false;
            Debug.Log($"{LogPrefix} Group provider registered: {impl.GetType().FullName}");
        }

        public static void Unregister(IGroupProvider impl)
        {
            if (_impl == impl) _impl = null;
        }

        public static bool AreGrouped(long playerA, long playerB)
        {
            if (playerA == 0L || playerB == 0L) return false;
            if (playerA == playerB) return true;
            if (_impl == null) { ReportMissingProviderOnce(); return false; }
            try { return _impl.AreGrouped(playerA, playerB); } catch { return false; }
        }

        public static IReadOnlyList<long> GetGroupMembers(long playerId)
        {
            if (_impl == null) { ReportMissingProviderOnce(); return Array.Empty<long>(); }
            try { return _impl.GetGroupMembers(playerId) ?? Array.Empty<long>(); } catch { return Array.Empty<long>(); }
        }

        public static long GetGroupLeader(long playerId)
        {
            if (_impl == null) { ReportMissingProviderOnce(); return 0L; }
            try { return _impl.GetGroupLeader(playerId); } catch { return 0L; }
        }

        public static bool HasGroup(long playerId)
        {
            if (_impl == null) { ReportMissingProviderOnce(); return false; }
            try { return _impl.HasGroup(playerId); } catch { return false; }
        }

        private static void ReportMissingProviderOnce()
        {
            if (_reportedMissingProvider) return;
            _reportedMissingProvider = true;
            Debug.LogWarning($"{LogPrefix} No group provider is registered - every party query answers " +
                             "\"not grouped\" for this session. Ability ring visibility, companion allegiance " +
                             "and any other group-gated feature fall back to their no-party behaviour.");
        }
    }
}
