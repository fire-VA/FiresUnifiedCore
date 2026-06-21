using UnityEngine;
using FiresCore.Npc.Patrol;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Walks an NPC along an assigned <see cref="PatrolRoute"/>. An OPEN route is walked there-and-back
    /// (to the end, wait, reverse, wait, repeat); a LOOP (first≈last point) is walked continuously in one
    /// direction. Uses the framework's vanilla pathfinding (TryMoveToPosition) and validates each waypoint
    /// with IsReachable so an unreachable point doesn't trigger BaseAI's false "arrived" and skip the route.
    /// A small per-NPC deviation (re-rolled on arrival) keeps multiple NPCs on one route from stacking;
    /// the physics personal-space enforcer handles the rest.
    /// </summary>
    public class PatrolBehavior : IdleSubBehavior
    {
        public const float ReachDistance = 2.0f;
        public const float WaitSeconds = 4.0f;
        public const float DeviationRadius = 1.2f;

        private PatrolRoute _route;
        private int _index;
        private int _direction = 1;        // +1 forward, -1 reversed (open routes)
        private bool _waiting;
        private float _waitUntil;
        private Vector3 _deviation;

        public override string BehaviorName => "Patrol";
        public override bool AvailableForIdleRotation => false; // force-started by route assignment, not random rotation

        // Pause-during-combat support: when the NPC is attacked, CompanionCombatMovement fires
        // OnCombatStarted → the framework interrupts this behavior (IsActive=false) instead of cancelling it,
        // lets CompanionAI fight, then calls ResumeAfterCombat (IsActive=true) when combat ends. Our route
        // cursor (_index/_direction/_waiting) lives on this persistent instance, so the NPC resumes exactly
        // where it left off — no SaveState/RestoreState needed.
        public override bool SupportsResumption => true;

        private PatrolAssignment Assignment => Companion != null ? Companion.GetComponent<PatrolAssignment>() : null;

        public override bool CanStart()
        {
            var a = Assignment;
            if (a == null || string.IsNullOrEmpty(a.RouteName)) return false;
            var r = PatrolRouteManager.GetRoute(a.RouteName);
            return r != null && r.Points.Count >= 2;
        }

        public override void Start()
        {
            base.Start();
            MaxDuration = float.MaxValue;   // patrol runs continuously until cancelled
            _route = PatrolRouteManager.GetRoute(Assignment?.RouteName);
            _direction = 1;
            _waiting = false;
            _index = NearestPointIndex();
            RerollDeviation();
        }

        public override bool Update()
        {
            // Interrupted for combat (IsActive=false): hold position and stay the active behavior so the
            // framework can resume us afterward — don't move or complete.
            if (!IsActive) return false;

            if (_route == null || _route.Points.Count < 2) return true; // route lost → end (re-evaluated next tick)

            if (_waiting)
            {
                StopMovement();
                if (Time.time < _waitUntil) return false;
                _waiting = false;
                _index = Mathf.Clamp(_index + _direction, 0, _route.Points.Count - 1);
                RerollDeviation();
            }

            Vector3 target = _route.Points[_index] + _deviation;
            if (!IsReachable(target)) target = _route.Points[_index];   // deviation pushed off-mesh → use base point
            if (!IsReachable(target)) { AdvanceIndex(); return false; } // base point unreachable → skip waypoint

            TryMoveToPosition(target, walk: true);

            if (Utils.DistanceXZ(Transform.position, target) <= ReachDistance)
                OnArrived();

            return false;   // never completes on its own
        }

        private void OnArrived()
        {
            RerollDeviation();
            int last = _route.Points.Count - 1;

            if (_route.IsLoop)
            {
                _index = (_index + 1) % _route.Points.Count;
                return;
            }

            // Open route: at an endpoint, wait then reverse (the wait branch steps off the endpoint).
            if ((_direction == 1 && _index >= last) || (_direction == -1 && _index <= 0))
            {
                _direction = -_direction;
                _waiting = true;
                _waitUntil = Time.time + WaitSeconds;
                return;
            }
            _index = Mathf.Clamp(_index + _direction, 0, last);
        }

        private void AdvanceIndex()
        {
            int last = _route.Points.Count - 1;
            if (_route.IsLoop) { _index = (_index + 1) % _route.Points.Count; return; }
            if ((_direction == 1 && _index >= last) || (_direction == -1 && _index <= 0))
                _direction = -_direction;
            _index = Mathf.Clamp(_index + _direction, 0, last);
        }

        private int NearestPointIndex()
        {
            if (_route == null || Transform == null) return 0;
            int best = 0;
            float bestSq = float.MaxValue;
            for (int i = 0; i < _route.Points.Count; i++)
            {
                float d = (Transform.position - _route.Points[i]).sqrMagnitude;
                if (d < bestSq) { bestSq = d; best = i; }
            }
            return best;
        }

        private void RerollDeviation()
        {
            var c = Random.insideUnitCircle * DeviationRadius;
            _deviation = new Vector3(c.x, 0f, c.y);
        }

        public override string GetStatusDescription()
        {
            var name = Assignment?.RouteName;
            return string.IsNullOrEmpty(name) ? "Patrolling" : $"Patrolling '{name}'";
        }
    }
}
