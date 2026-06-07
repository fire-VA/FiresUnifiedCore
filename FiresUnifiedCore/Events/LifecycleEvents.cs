using System;
using FiresCore.Logging;

namespace FiresCore.Events
{
    // Cross-mod lifecycle signals. Publishers (typically the FiresMod<T> base via Core) call the Raise*
    // methods; any mod subscribes to react. Subscribers are invoked defensively — one throwing subscriber
    // is logged and skipped so it can't break the others or the publisher.
    public static class LifecycleEvents
    {
        public static event Action WorldStart;
        public static event Action WorldSave;
        public static event Action<long> PlayerConnect;
        public static event Action<long> PlayerDisconnect;
        public static event Action<long, Player> PlayerSpawned;
        public static event Action Shutdown;

        public static void RaiseWorldStart() => SafeInvoke(WorldStart, nameof(WorldStart));
        public static void RaiseWorldSave() => SafeInvoke(WorldSave, nameof(WorldSave));
        public static void RaisePlayerConnect(long playerId) => SafeInvoke(PlayerConnect, playerId, nameof(PlayerConnect));
        public static void RaisePlayerDisconnect(long playerId) => SafeInvoke(PlayerDisconnect, playerId, nameof(PlayerDisconnect));
        public static void RaisePlayerSpawned(long playerId, Player player) => SafeInvoke(PlayerSpawned, playerId, player, nameof(PlayerSpawned));
        public static void RaiseShutdown() => SafeInvoke(Shutdown, nameof(Shutdown));

        private static void SafeInvoke(Action handlers, string eventName)
        {
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                try { handler(); }
                catch (Exception ex) { LogSubscriberFault(eventName, ex); }
            }
        }

        private static void SafeInvoke<T>(Action<T> handlers, T arg, string eventName)
        {
            if (handlers == null) return;
            foreach (Action<T> handler in handlers.GetInvocationList())
            {
                try { handler(arg); }
                catch (Exception ex) { LogSubscriberFault(eventName, ex); }
            }
        }

        private static void SafeInvoke<T1, T2>(Action<T1, T2> handlers, T1 arg1, T2 arg2, string eventName)
        {
            if (handlers == null) return;
            foreach (Action<T1, T2> handler in handlers.GetInvocationList())
            {
                try { handler(arg1, arg2); }
                catch (Exception ex) { LogSubscriberFault(eventName, ex); }
            }
        }

        private static void LogSubscriberFault(string eventName, Exception ex) =>
            FiresLogger.LogWarning($"[LifecycleEvents] subscriber to {eventName} threw: {ex.Message}");
    }
}
