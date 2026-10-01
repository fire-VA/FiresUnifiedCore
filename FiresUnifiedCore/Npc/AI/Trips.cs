using System;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Trips of the one brain's bodies (Core 0.2.220, [seasons]' autoplay planner and the haul job): a trip is one outing from home
    /// and back (leave, turn home, deposit). Whoever runs the loop raises Started when the body leaves and Ended when it is done, and
    /// anything that cares (the stream, the drill judges, the party regroup) listens instead of guessing from positions. Per client,
    /// main thread only; nothing is networked.
    /// </summary>
    public static class Trips
    {
        /// <summary>One trip as raised. Immutable, so a listener can keep it.</summary>
        public sealed class TripInfo
        {
            /// <summary>The run it belongs to (a planner session, a haul job).</summary>
            public readonly string Session;
            /// <summary>1 for the session's first trip.</summary>
            public readonly int Number;
            /// <summary>The body's name (the bot, the tester, a companion).</summary>
            public readonly string Body;
            /// <summary>Why it started or ended ("haul", "bag 82 %", "quota met", "night", …).</summary>
            public readonly string Trigger;
            /// <summary>ZNet time (s) when it was raised.</summary>
            public readonly double NetTime;
            /// <summary>The body itself when the raiser passed it (a companion, the bot), else null.</summary>
            public readonly Character Character;
            /// <summary>Where <see cref="Character"/> stood when it was raised (zero without one): a Started and its Ended give from and to.</summary>
            public readonly Vector3 Position;

            public TripInfo(string session, int number, string body, string trigger, double netTime, Character character = null)
            {
                Session = session ?? "";
                Number = number;
                Body = body ?? "";
                Trigger = trigger ?? "";
                NetTime = netTime;
                Character = character;
                Position = character != null ? character.transform.position : Vector3.zero;
            }

            public override string ToString() => $"trip {Number} for {Body} ({Session}): {Trigger}";
        }

        public static event Action<TripInfo> Started;
        public static event Action<TripInfo> Ended;

        /// <summary>The trip under way on this client, or null between trips.</summary>
        public static TripInfo Current { get; private set; }

        /// <summary>A trip began: sets <see cref="Current"/>, then tells every <see cref="Started"/> listener.</summary>
        public static void RaiseStarted(TripInfo trip)
        {
            if (trip == null) return;
            Current = trip;
            Delegate[] listeners = Started?.GetInvocationList() ?? new Delegate[0];
            Debug.Log($"[Trips] trip {trip.Number} started for {trip.Body} ({trip.Session}): {listeners.Length} listener(s)");
            Tell(listeners, trip, "trip-started");
        }

        /// <summary>A trip finished: clears <see cref="Current"/> when it is this trip, then tells every <see cref="Ended"/> listener.</summary>
        public static void RaiseEnded(TripInfo trip)
        {
            if (trip == null) return;
            if (Current != null && Current.Session == trip.Session && Current.Number == trip.Number) Current = null;
            Delegate[] listeners = Ended?.GetInvocationList() ?? new Delegate[0];
            Debug.Log($"[Trips] trip {trip.Number} ended for {trip.Body}: {trip.Trigger}; {listeners.Length} listener(s)");
            Tell(listeners, trip, "trip-ended");
        }

        // One listener's exception must not stop the others or the loop that raised the trip.
        private static void Tell(Delegate[] listeners, TripInfo trip, string what)
        {
            foreach (Delegate d in listeners)
            {
                try { ((Action<TripInfo>)d)(trip); }
                catch (Exception ex) { Debug.LogWarning($"[Trips] a {what} listener threw ({d.Method.DeclaringType?.Name}.{d.Method.Name}): {ex}"); }
            }
        }
    }
}
