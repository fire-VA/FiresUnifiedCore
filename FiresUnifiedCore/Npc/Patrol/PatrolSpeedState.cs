using System.Runtime.CompilerServices;
using UnityEngine;

namespace FiresCore.Npc.Patrol
{
    /// <summary>
    /// One-frame side-channel from <c>PatrolBehavior</c> to <c>CompanionSpeedRamp</c>: the DBSM speed
    /// multiplier (multiple of base walk) and tier (walk/run) the patrol wants applied THIS frame.
    /// PatrolBehavior publishes it each frame it issues a move; CompanionSpeedRamp reads it inside its
    /// <c>Character.UpdateWalking</c> prefix and eases the active tier's speed scalar toward base×mul
    /// (restoring in its finalizer). Never touches SetMoveDir — the movement single-writer rule holds.
    ///
    /// Entries carry a timestamp and auto-expire after <see cref="MaxAge"/>: if patrol stops publishing
    /// (combat, cancel, disable, behavior swap), the ramp releases the override within a few frames with
    /// no clean-shutdown hook required. PatrolBehavior also <see cref="Clear"/>s explicitly on its hold
    /// branches for immediacy.
    /// </summary>
    public static class PatrolSpeedState
    {
        private sealed class State { public float SpeedMul; public bool Run; public float Stamp; }

        // Auto-released when the Character is collected (no manual cleanup / leak).
        private static readonly ConditionalWeakTable<Character, State> _states =
            new ConditionalWeakTable<Character, State>();

        // A handful of frames at 60 fps: long enough that a hitch won't drop a live patrol, short enough
        // that a stopped patrol releases its speed override almost immediately.
        private const float MaxAge = 0.35f;

        public static void Set(Character ch, float speedMul, bool run)
        {
            if (ch == null) return;
            var state = _states.GetOrCreateValue(ch);
            state.SpeedMul = speedMul;
            state.Run = run;
            state.Stamp = Time.time;
        }

        public static bool TryGet(Character ch, out float speedMul, out bool run)
        {
            speedMul = 1f; run = false;
            if (ch == null) return false;
            if (!_states.TryGetValue(ch, out var state)) return false;
            if (Time.time - state.Stamp > MaxAge) return false; // stale → patrol isn't driving this frame
            speedMul = state.SpeedMul;
            run = state.Run;
            return true;
        }

        public static void Clear(Character ch)
        {
            if (ch != null) _states.Remove(ch);
        }
    }
}
