using UnityEngine;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// "Is it getting anywhere?" - one implementation for the intent view's no-progress timer and the trip stuck failsafe: seconds since
    /// the distance to a goal last shrank by at least <see cref="Step"/> metres. A new goal (more than 1 m from the last) restarts it.
    /// </summary>
    public sealed class ProgressWatch
    {
        public readonly float Step;
        private Vector3 _goal = new Vector3(float.NaN, 0f, 0f);
        private float _best = float.MaxValue, _at;

        public ProgressWatch(float step) { Step = step; }

        /// <summary>Feeds the current position; returns the seconds without progress toward <paramref name="goal"/>.</summary>
        public float Tick(Vector3 position, Vector3 goal)
        {
            float d = Vector3.Distance(position, goal);
            if (float.IsNaN(_goal.x) || (goal - _goal).sqrMagnitude > 1f) { _goal = goal; _best = d; _at = Time.time; }
            else if (d < _best - Step) { _best = d; _at = Time.time; }
            return Time.time - _at;
        }

        public float Seconds => float.IsNaN(_goal.x) ? 0f : Time.time - _at;
        public void Reset() { _goal = new Vector3(float.NaN, 0f, 0f); _best = float.MaxValue; }
    }
}
