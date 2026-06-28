using System;
using System.Collections.Generic;

namespace FiresCore.Bridge
{
    /// <summary>Kinds of domain events producers emit to Discord through <see cref="DiscordSink"/>; the hub renders each.</summary>
    public enum DiscordEventKind
    {
        LeaderboardTierUnlocked, LeaderboardRankChanged, LeaderboardSeasonReset,
        GuildCreated, GuildDisbanded, GuildRenamed,
        GuildMemberJoined, GuildMemberLeft, GuildMemberKicked,
        GuildMemberPromoted, GuildMemberDemoted, GuildOwnershipTransferred,
        GuildPortalLinked, GuildPortalUnlinked,
        GroupFormed, GroupDisbanded,
        PlayerDeath, RewardGranted, Milestone
    }

    /// <summary>A rolled reward line a producer hands to the hub to render (item / tier-reward announcement).</summary>
    public sealed class DiscordRewardItem
    {
        public string Prefab;
        public string DisplayName;
        public string Emoji;
        public int Amount;
        public int Quality;
        public string RarityLabel;
        public bool Unique;
    }

    /// <summary>A death event a producer hands to the hub. <see cref="Message"/> is already localized by the producer.</summary>
    public sealed class DiscordDeathInfo
    {
        public string PlayerName;
        public string PlatformId;
        public string Message;
        public string DeathType;
        public string EnemyName;
        public byte[] ScreenshotPng;
    }

    /// <summary>
    /// Additive domain-event contract layered beside <see cref="IDiscordSink"/>. The hub's sink implements this
    /// alongside <see cref="IDiscordSink"/>; the <see cref="DiscordSink"/> facade reaches it via cast so that
    /// adding events here never breaks an existing <see cref="IDiscordSink"/> implementer that predates them.
    /// </summary>
    public interface IDiscordEventSink
    {
        void PostEvent(DiscordEventKind kind, string title, string description,
            IReadOnlyList<KeyValuePair<string, string>> fields, string platformId = null, Action onComplete = null);

        void OnLeaderboardTierUnlocked(string playerName, string platformId, int tierValue, long currentScore,
            IReadOnlyList<DiscordRewardItem> awardedItems, string highestRarityLabel, bool isJackpot);

        void OnPlayerDeath(DiscordDeathInfo info, Action onComplete = null);

        void OnRewardGranted(string playerName, string platformId, string source, string label,
            IReadOnlyList<DiscordRewardItem> items);
    }

    /// <summary>
    /// Cross-mod contract for posting anti-cheat / character events to a Discord integration.
    /// The Discord integration mod registers a concrete implementation via <see cref="DiscordSink.Register"/>;
    /// producer mods (e.g. FiresVAngarde) call the static <see cref="DiscordSink"/> facade. When no sink
    /// is registered the facade no-ops — and crucially still completes any <c>onComplete</c> callback — so
    /// Discord stays an optional dependency and anti-cheat enforcement (the kick) never blocks on it.
    /// </summary>
    public interface IDiscordSink
    {
        bool IsActive();

        void OnAntiCheatViolation(string playerName, string platformId, string reason, int violationCount);

        void OnAntiCheatKick(string playerName, string platformId, string reason, Action onComplete = null);

        void OnAntiCheatKick(string playerName, string platformId, string reason,
            Dictionary<string, string> clientModList, byte[] clientBepInExLog, Action onComplete = null);

        void SendClientLogOnRequest(string playerName, string platformId, byte[] logBytes);

        /// <summary>Discord config bridge: whether per-login client-log artifacts should be captured.</summary>
        bool NotifyClientLoginArtifacts();

        /// <summary>Discord config bridge: comma-separated Discord user IDs allowed to request logs.</summary>
        string BotAdminDiscordIds();

        /// <summary>An admin/exempt player triggered a behavioral detection but was NOT kicked (monitoring mode).</summary>
        void OnAdminCheatDetected(string playerName, string platformId, string reason,
            int detectionCount, IReadOnlyList<string> activeAdminCommands);

        /// <summary>An admin toggled a cheat/debug state (devcommands, debugmode, god, ghost, fly, nocost) on or off.</summary>
        void OnAdminCommandSnapshot(string playerName, string platformId,
            string changedCommand, bool enabled, IReadOnlyList<string> activeAdminCommands);
    }

    /// <summary>
    /// Process-global facade over the registered <see cref="IDiscordSink"/>. Safe to call whether or not
    /// the Discord integration mod is installed.
    /// </summary>
    public static class DiscordSink
    {
        private static IDiscordSink _impl;

        public static bool HasSink => _impl != null;

        public static void Register(IDiscordSink impl) => _impl = impl;

        public static void Unregister(IDiscordSink impl)
        {
            if (_impl == impl) _impl = null;
        }

        public static bool IsActive() => _impl?.IsActive() ?? false;

        public static void OnAntiCheatViolation(string playerName, string platformId, string reason, int violationCount)
            => _impl?.OnAntiCheatViolation(playerName, platformId, reason, violationCount);

        public static void OnAntiCheatKick(string playerName, string platformId, string reason, Action onComplete = null)
        {
            if (_impl != null) _impl.OnAntiCheatKick(playerName, platformId, reason, onComplete);
            else onComplete?.Invoke();
        }

        public static void OnAntiCheatKick(string playerName, string platformId, string reason,
            Dictionary<string, string> clientModList, byte[] clientBepInExLog, Action onComplete = null)
        {
            if (_impl != null) _impl.OnAntiCheatKick(playerName, platformId, reason, clientModList, clientBepInExLog, onComplete);
            else onComplete?.Invoke();
        }

        public static void SendClientLogOnRequest(string playerName, string platformId, byte[] logBytes)
            => _impl?.SendClientLogOnRequest(playerName, platformId, logBytes);

        public static bool NotifyClientLoginArtifacts() => _impl?.NotifyClientLoginArtifacts() ?? false;

        public static string BotAdminDiscordIds() => _impl?.BotAdminDiscordIds() ?? string.Empty;

        public static void OnAdminCheatDetected(string playerName, string platformId, string reason,
            int detectionCount, IReadOnlyList<string> activeAdminCommands)
            => _impl?.OnAdminCheatDetected(playerName, platformId, reason, detectionCount, activeAdminCommands);

        public static void OnAdminCommandSnapshot(string playerName, string platformId,
            string changedCommand, bool enabled, IReadOnlyList<string> activeAdminCommands)
            => _impl?.OnAdminCommandSnapshot(playerName, platformId, changedCommand, enabled, activeAdminCommands);

        // ── Domain events (leaderboard / guild / group / death). Routed to the sink only if it also implements
        //    IDiscordEventSink, so these are no-ops against an older anti-cheat-only sink. All server-emitted. ──

        public static void PostEvent(DiscordEventKind kind, string title, string description,
            IReadOnlyList<KeyValuePair<string, string>> fields, string platformId = null, Action onComplete = null)
        {
            if (_impl is IDiscordEventSink s) s.PostEvent(kind, title, description, fields, platformId, onComplete);
            else onComplete?.Invoke();
        }

        public static void OnLeaderboardTierUnlocked(string playerName, string platformId, int tierValue, long currentScore,
            IReadOnlyList<DiscordRewardItem> awardedItems, string highestRarityLabel, bool isJackpot)
            => (_impl as IDiscordEventSink)?.OnLeaderboardTierUnlocked(
                playerName, platformId, tierValue, currentScore, awardedItems, highestRarityLabel, isJackpot);

        public static void OnPlayerDeath(DiscordDeathInfo info, Action onComplete = null)
        {
            if (_impl is IDiscordEventSink s) s.OnPlayerDeath(info, onComplete);
            else onComplete?.Invoke();
        }

        public static void OnRewardGranted(string playerName, string platformId, string source, string label,
            IReadOnlyList<DiscordRewardItem> items)
            => (_impl as IDiscordEventSink)?.OnRewardGranted(playerName, platformId, source, label, items);
    }
}
