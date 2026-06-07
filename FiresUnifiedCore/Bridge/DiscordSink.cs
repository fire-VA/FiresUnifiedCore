using System;
using System.Collections.Generic;

namespace FiresCore.Bridge
{
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
    }
}
