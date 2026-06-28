using System;
using System.Collections.Generic;
using FiresCore.Storage;

namespace FiresCore.Bridge
{
    /// <summary>
    /// The cross-mod surface over Core's authoritative <see cref="LeaderboardRepository"/> (the shared
    /// LiteDB vault). The leaderboard frontend (RPG Maker / FiresRPGmaker) records stats and reads rankings
    /// through here, so the data lives in Core, not the frontend. Owner key is <c>"{hostName}_{playerName}"</c>
    /// (Marketplace-compatible).
    ///
    /// Writes are SERVER-AUTHORITATIVE — call the <c>Record*</c>/<c>Set*</c> helpers only on the server.
    /// Clients learn of an update through <see cref="EntryPublished"/>: the frontend raises it (via
    /// <see cref="RaiseEntryPublished"/>) after a server→client snapshot RPC lands, so an open leaderboard UI
    /// refreshes without polling.
    /// </summary>
    public static class LeaderboardBridge
    {
        public static void RecordKilledCreature(string ownerKey, string playerName, string prefab, int amount = 1)
            => LeaderboardRepository.RecordKilledCreature(ownerKey, playerName, prefab, amount);

        public static void RecordBuilt(string ownerKey, string playerName, string prefab, int amount = 1)
            => LeaderboardRepository.RecordBuilt(ownerKey, playerName, prefab, amount);

        public static void RecordCrafted(string ownerKey, string playerName, string prefab, int amount = 1)
            => LeaderboardRepository.RecordCrafted(ownerKey, playerName, prefab, amount);

        public static void RecordKilledBy(string ownerKey, string playerName, string source, int amount = 1)
            => LeaderboardRepository.RecordKilledBy(ownerKey, playerName, source, amount);

        public static void RecordHarvested(string ownerKey, string playerName, string prefab, int amount = 1)
            => LeaderboardRepository.RecordHarvested(ownerKey, playerName, prefab, amount);

        public static void RecordDeath(string ownerKey, string playerName)
            => LeaderboardRepository.RecordDeath(ownerKey, playerName);

        public static void SetMapExplored(string ownerKey, string playerName, float percent)
            => LeaderboardRepository.SetMapExplored(ownerKey, playerName, percent);

        public static LeaderboardEntry GetForPlayer(string ownerKey)
            => LeaderboardRepository.GetForPlayer(ownerKey);

        /// <summary>Top entries by an arbitrary scored projection (e.g. total kills, deaths, map explored).</summary>
        public static List<LeaderboardEntry> GetTop(Func<LeaderboardEntry, long> selector, int count)
            => LeaderboardRepository.GetTop(selector, count);

        /// <summary>Raised on the client with the owner key after a server push, so a live leaderboard UI refreshes.</summary>
        public static event Action<string> EntryPublished;

        public static void RaiseEntryPublished(string ownerKey)
        {
            try { EntryPublished?.Invoke(ownerKey); } catch { }
        }
    }
}
