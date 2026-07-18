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

        /// <summary>Tally a kill credited to a weapon prefab; the "favorite weapon" is the highest-count entry.</summary>
        public static void RecordWeaponKill(string ownerKey, string playerName, string weaponPrefab, int amount = 1)
            => LeaderboardRepository.RecordWeaponKill(ownerKey, playerName, weaponPrefab, amount);

        /// <summary>Latest-wins skill level for a single skill (Skills.SkillType name → level 0..100).</summary>
        public static void SetSkillLevel(string ownerKey, string playerName, string skill, int level)
            => LeaderboardRepository.SetSkillLevel(ownerKey, playerName, skill, level);

        public static void RecordDeath(string ownerKey, string playerName)
            => LeaderboardRepository.RecordDeath(ownerKey, playerName);

        public static void SetMapExplored(string ownerKey, string playerName, float percent)
            => LeaderboardRepository.SetMapExplored(ownerKey, playerName, percent);

        /// <summary>Flag the first time a player enters a biome (Heightmap.Biome name) — drives biome milestones.</summary>
        public static void RecordBiomeReached(string ownerKey, string playerName, string biome)
            => LeaderboardRepository.RecordBiomeReached(ownerKey, playerName, biome);

        /// <summary>Increment a player's tame count — &gt;0 satisfies the "first tame" milestone.</summary>
        public static void RecordTamed(string ownerKey, string playerName, int amount = 1)
            => LeaderboardRepository.RecordTamed(ownerKey, playerName, amount);

        /// <summary>Tally hoe terrain ops (raise / level / path) — kept out of the build count.</summary>
        public static void RecordTerraformed(string ownerKey, string playerName, int amount = 1)
            => LeaderboardRepository.RecordTerraformed(ownerKey, playerName, amount);

        /// <summary>Tally cultivator ops (cultivate farmland + plant seeds).</summary>
        public static void RecordCultivated(string ownerKey, string playerName, int amount = 1)
            => LeaderboardRepository.RecordCultivated(ownerKey, playerName, amount);

        public static LeaderboardEntry GetForPlayer(string ownerKey)
            => LeaderboardRepository.GetForPlayer(ownerKey);

        // ── Seasons ──────────────────────────────────────────────────────────────────────────────
        /// <summary>The current (live) season number.</summary>
        public static int GetCurrentSeason() => LeaderboardRepository.GetCurrentSeason();

        /// <summary>Archive the live board under the current season, wipe it, advance the season. SERVER-only.
        /// Returns the season just archived, or -1 on failure.</summary>
        public static int RollSeason() => LeaderboardRepository.RollSeason();

        /// <summary>Hard-wipe ALL leaderboard data — live board, season archive, season counter (reset to 1) AND
        /// every stored mannequin appearance — for a fresh start at season 1. SERVER-only. Achievement DEFINITIONS
        /// (.cfg files) are untouched; per-player achievement progress is derived from the board and clears with
        /// it.</summary>
        public static void WipeAllData()
        {
            LeaderboardRepository.WipeAll();
            PlayerAppearanceRepository.WipeAll();
        }

        /// <summary>Archived season numbers (ascending), not including the current live season.</summary>
        public static System.Collections.Generic.List<int> GetArchivedSeasons() => LeaderboardRepository.GetArchivedSeasons();

        /// <summary>Owner-keyed rows for a season (live if current/newer, else the archive).</summary>
        public static System.Collections.Generic.Dictionary<string, LeaderboardEntry> GetSeasonEntries(int season)
            => LeaderboardRepository.GetSeasonEntries(season);

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
