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
        // The route is a GUIDE, not a rail: a generous reach distance lets the NPC cut corners / pass near
        // waypoints instead of pivoting onto each exact point (chokepoints are still traversed because the
        // NPC must path through them to reach the NEXT waypoint), and the deviation adds organic offset.
        public const float ReachDistance = 3.5f;
        public const float WaitSeconds = 4.0f;
        public const float DeviationRadius = 1.2f;
        public const float ChatPauseRange = 3.5f;  // stop and let a player interact when this close

        private PatrolRoute _route;
        private int _index;
        private int _direction = 1;        // +1 forward, -1 reversed (open routes)
        private bool _waiting;
        private float _waitUntil;
        private Vector3 _deviation;

        public const float TeleportTimeout = 60f;  // seconds spent trying to reach a waypoint before snapping to it
        private int _lastIndex = -1;
        private float _targetSince;

        // Stuck recovery: ground NPCs get no vanilla un-stick and the patrol move path has none of its own, so
        // when the NPC stops getting closer to its waypoint we escalate a recovery step (fresh path → lateral
        // nudge → skip the node) long before the teleport backstop kicks in.
        public const float NoProgressEpsilon = 0.5f;    // XZ gain toward the waypoint that counts as progress
        public const float NoProgressTimeout = 4f;      // no progress for this long → take the next recovery step
        public const float RecoveryStepCooldown = 6f;   // min seconds between recovery steps so each one can act
        private float _bestDistToTarget = float.MaxValue;
        private float _lastProgressTime;
        private float _lastRecoveryTime;
        private int _stuckEscalation;

        // TEMP diagnostic state — remove once patrol-on-relog is confirmed.
        private float _lastDiagTime;
        private Vector3 _lastDiagPos;

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
            _lastIndex = _index;
            _targetSince = Time.time;
            ResetProgress();
            RerollDeviation();
        }

        public override bool Update()
        {
            // Interrupted for combat (IsActive=false): hold position, stay the active behavior so the
            // framework can resume us afterward, and keep the join timer fresh so combat time doesn't count
            // toward a teleport.
            if (!IsActive) { _targetSince = Time.time; ResetProgress(); PatrolDiag("COMBAT-HOLD", 0f); return false; }

            if (_route == null || _route.Points.Count < 2) return true; // route lost → end (re-evaluated next tick)

            // Stop and let a nearby player interact/chat instead of pushing past them — and don't walk off
            // while the player is engaging the NPC (they stay within this range while its UI is open, since
            // the inventory pins them in place). Resumes from the same waypoint once they leave.
            if (Player.GetClosestPlayer(Transform.position, ChatPauseRange) != null)
            {
                StopMovement();
                _targetSince = Time.time;
                ResetProgress();
                PatrolDiag("PROXIMITY-PAUSE", Utils.DistanceXZ(Transform.position, _route.Points[_index]));
                return false;
            }

            if (_waiting)
            {
                StopMovement();
                PatrolDiag("WAITING", 0f);
                if (Time.time < _waitUntil) return false;
                _waiting = false;
                _index = Mathf.Clamp(_index + _direction, 0, _route.Points.Count - 1);
                RerollDeviation();
            }

            // Look-ahead: advance the cursor through every waypoint we're already within reach of BEFORE
            // issuing the move, so the move target is always far enough that vanilla MoveTo never hits its
            // stop-at-arrival branch (it stops ~2m out; the cursor advances at 3.5m). The NPC therefore flows
            // through nodes continuously instead of stopping at each one — and this is also the "route is a
            // guide" smoothing (near nodes get skipped). An open route's endpoint sets _waiting and breaks out.
            int guard = 0;
            while (!_waiting && guard++ < _route.Points.Count
                   && Utils.DistanceXZ(Transform.position, _route.Points[_index]) <= ReachDistance)
            {
                OnArrived();
            }
            if (_waiting) { StopMovement(); return false; }

            // Restart the per-waypoint join timer + stuck tracker whenever the target waypoint changes.
            if (_index != _lastIndex) { _lastIndex = _index; _targetSince = Time.time; ResetProgress(); }

            // Stuck recovery: if we stop getting closer to this waypoint, escalate a recovery step well before
            // the 60s teleport backstop. Any real progress re-baselines the clock, so a long straight leg or an
            // NPC still walking onto its route never trips it. Runs before the move so the step takes effect now.
            float distToWaypoint = Utils.DistanceXZ(Transform.position, _route.Points[_index]);
            if (distToWaypoint < _bestDistToTarget - NoProgressEpsilon)
            {
                _bestDistToTarget = distToWaypoint;
                _lastProgressTime = Time.time;
            }
            else if (Time.time - _lastProgressTime > NoProgressTimeout
                     && Time.time - _lastRecoveryTime > RecoveryStepCooldown)
            {
                StepStuckRecovery();
            }

            Vector3 target = _route.Points[_index] + _deviation;
            if (!IsReachable(target)) target = _route.Points[_index];   // deviation pushed off-mesh → use base point

            // Couldn't reach this waypoint within the timeout (unreachable, stuck, or just assigned the route
            // from far away) → snap onto it. This is how an NPC that isn't on its route gets there.
            if (Time.time - _targetSince > TeleportTimeout)
            {
                Debug.Log($"[PatrolDiag] {DiagId} TELEPORT-BACKSTOP idx={_index} to={_route.Points[_index]}");
                TeleportTo(_route.Points[_index]);
                OnArrived();
                return false;
            }

            PatrolDiag("MOVING", distToWaypoint);

            // Force vanilla WALK speed. The pathfinding chain only ever calls SetRun(false), leaving m_walk
            // false → the NPC would use the jog tier (m_speed=10, ~2× walk). Setting m_walk every patrol frame
            // selects m_walkSpeed; other behaviors (combat/follow) re-assert their own mode so this won't stick.
            Companion?.GetComponent<Character>()?.SetWalk(true);
            TryMoveToPosition(target, walk: true);

            return false;   // never completes on its own
        }

        // Snaps the NPC onto a route point (owner-side; syncs to others via ZSyncTransform). Used as the
        // fallback when walking can't get it onto the route.
        private void TeleportTo(Vector3 point)
        {
            if (Transform == null) return;
            Transform.position = point;
            var rb = Companion != null ? Companion.GetComponent<Rigidbody>() : null;
            if (rb != null) { rb.position = point; rb.linearVelocity = Vector3.zero; }
        }

        private void OnArrived()
        {
            WriteCheckpoint();
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

        // Re-baselines the no-progress clock. Called wherever the per-waypoint timer resets (start, combat hold,
        // player pause, waypoint change) so paused/interrupted time and fresh legs never read as "stuck".
        private void ResetProgress()
        {
            _bestDistToTarget = float.MaxValue;
            _lastProgressTime = Time.time;
            _lastRecoveryTime = Time.time;
            _stuckEscalation = 0;
        }

        // One rung of the stuck-recovery ladder, re-baselining the clock so the next rung waits a full cooldown.
        // If even skipping the node doesn't help, the existing 60s TeleportTimeout snap is the final backstop.
        private void StepStuckRecovery()
        {
            Debug.Log($"[PatrolDiag] {DiagId} STUCK-RECOVERY step={_stuckEscalation} idx={_index} pos={Transform.position}");
            _lastRecoveryTime = Time.time;
            _lastProgressTime = Time.time;
            _bestDistToTarget = float.MaxValue;
            switch (_stuckEscalation++)
            {
                case 0:
                    CompanionAI?.RequestPathRecalculation();   // fresh navmesh path (defeats the vanilla path cache)
                    break;
                case 1:
                    RerollDeviation();                          // nudge laterally off whatever we're wedged against
                    break;
                default:
                    AdvanceIndex();                             // give up on this node; move on (no checkpoint write)
                    _stuckEscalation = 0;
                    break;
            }
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

        // TEMP diagnostic — throttled per-NPC patrol state so we can see which branch a "standing" NPC is in
        // (proximity-paused vs physically stuck vs recovering). Remove once patrol-on-relog is confirmed.
        private string DiagId => $"{(Companion != null ? Companion.name : "?")}#{(Companion != null ? Companion.GetInstanceID() : 0)}";

        private void PatrolDiag(string branch, float distToWaypoint)
        {
            if (Time.time - _lastDiagTime < 2f) return;
            float moved2s = (Transform.position - _lastDiagPos).magnitude;
            _lastDiagPos = Transform.position;
            _lastDiagTime = Time.time;
            Debug.Log($"[PatrolDiag] {DiagId} {branch} idx={_index}/{(_route != null ? _route.Points.Count : 0)} " +
                      $"distWp={distToWaypoint:F1} best={_bestDistToTarget:F1} noProg={(Time.time - _lastProgressTime):F1}s " +
                      $"esc={_stuckEscalation} moved2s={moved2s:F2} pos={Transform.position}");
        }

        private void RerollDeviation()
        {
            var c = Random.insideUnitCircle * DeviationRadius;
            _deviation = new Vector3(c.x, 0f, c.y);
        }

        // Persists the last reached waypoint (base point, not the deviated target) to the NPC's ZDO so a
        // death-while-patrolling can respawn it here. Owner-only — the server owns these NPCs.
        private void WriteCheckpoint()
        {
            var nview = Companion != null ? Companion.GetComponent<ZNetView>() : null;
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;
            nview.GetZDO()?.Set("npc_patrol_checkpoint", _route.Points[_index]);
        }

        public override string GetStatusDescription()
        {
            var name = Assignment?.RouteName;
            return string.IsNullOrEmpty(name) ? "Patrolling" : $"Patrolling '{name}'";
        }
    }
}
