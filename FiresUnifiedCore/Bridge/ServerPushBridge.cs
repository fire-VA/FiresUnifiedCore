using System;
using System.Collections.Generic;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Cross-mod hook for "push my server-authoritative data up to the connected server".
    /// A Fires mod that authors files client-side which the server needs (e.g. FAP's
    /// chest/monster preset + EWP rule yaml) registers a pusher here; any command that
    /// pushes config to the server (FiresRPGmaker's <c>pushcfgs</c>) calls
    /// <see cref="InvokeAll"/> so one admin command propagates every family mod's data.
    ///
    /// The pusher receives an optional log sink so its progress surfaces in whatever
    /// console invoked the push. Pushers run client-side; each is responsible for its own
    /// admin / connected-to-server gating.
    /// </summary>
    public static class ServerPushBridge
    {
        private static readonly List<Action<Action<string>>> _pushers = new List<Action<Action<string>>>();

        /// <summary>Register a client-side pusher. The argument is a log sink (may be null).</summary>
        public static void RegisterPusher(Action<Action<string>> pusher)
        {
            if (pusher == null) return;
            if (_pushers.Contains(pusher)) return;
            _pushers.Add(pusher);
        }

        /// <summary>Fire every registered pusher. <paramref name="log"/> (optional) receives status lines.</summary>
        public static void InvokeAll(Action<string> log = null)
        {
            foreach (var pusher in _pushers)
            {
                try { pusher(log); }
                catch (Exception ex) { log?.Invoke($"[ServerPush] pusher threw: {ex.Message}"); }
            }
        }

        /// <summary>Whether any mod has registered a pusher (so callers can tailor their messaging).</summary>
        public static bool HasPushers => _pushers.Count > 0;
    }
}
