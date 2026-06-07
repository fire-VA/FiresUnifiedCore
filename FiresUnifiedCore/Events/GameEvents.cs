using System;
using FiresCore.Logging;

namespace FiresCore.Events
{
    // Cross-mod gameplay signals. The kill event is the one the quest system and companion kill-credit
    // depend on: a combat mod raises it, quest trackers subscribe. Subscribers are invoked defensively.
    public static class GameEvents
    {
        public static event Action<KillEventArgs> EntityKilled;
        public static event Action<ItemPickupArgs> ItemPickedUp;
        public static event Action<CraftEventArgs> ItemCrafted;

        public static void RaiseEntityKilled(KillEventArgs args) => SafeInvoke(EntityKilled, args, nameof(EntityKilled));
        public static void RaiseItemPickedUp(ItemPickupArgs args) => SafeInvoke(ItemPickedUp, args, nameof(ItemPickedUp));
        public static void RaiseItemCrafted(CraftEventArgs args) => SafeInvoke(ItemCrafted, args, nameof(ItemCrafted));

        private static void SafeInvoke<T>(Action<T> handlers, T arg, string eventName)
        {
            if (handlers == null) return;
            foreach (Action<T> handler in handlers.GetInvocationList())
            {
                try { handler(arg); }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[GameEvents] subscriber to {eventName} threw: {ex.Message}");
                }
            }
        }
    }
}
