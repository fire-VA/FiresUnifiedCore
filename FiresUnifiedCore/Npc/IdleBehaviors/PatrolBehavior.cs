using UnityEngine;
using FiresCore.Npc.Patrol;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Walks an NPC along an assigned <see cref="PatrolRoute"/>. An OPEN route is walked there-and-back
    /// (to the end, wait, reverse, wait, repeat); a LOOP (first≈last point) is walked continuously in one
    /// direction. Uses the framework's vanilla pathfinding (TryMoveToPosition) and validates each waypoint
    /// with IsReachable so an unreachable point doesn't trigger BaseAI's false "arrived" and skip the route.
    /// A small per-NPC deviation (re-rolled on arrival) keeps multiple NPCs on one route from stacking.
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
        public const float TeleportTimeout = 60f;  // seconds spent trying to reach a waypoint before snapping to it

        private PatrolRoute _route;
        private int _index;
        private int _direction = 1;        // +1 forward, -1 reversed (open routes)
        private bool _waiting;
        private float _waitUntil;
        private Vector3 _deviation;
        private int _lastIndex = -1;
        private float _targetSince;

        private CompanionCombatMovement _combatMovement;
        private CompanionCombatMovement CombatMovement =>
            _combatMovement != null ? _combatMovement
            : (_combatMovement = Companion != null ? Companion.GetComponent<CompanionCombatMovement>() : null);

        private FiresCore.Npc.AI.CompanionAI _ai;
        private FiresCore.Npc.AI.CompanionAI Ai =>
            _ai != null ? _ai
            : (_ai = Companion != null ? Companion.GetComponent<FiresCore.Npc.AI.CompanionAI>() : null);

        // True while the NPC is fighting (or in the post-combat hold buffer). CompanionAI's own combat STATE is
        // the authoritative signal — the stationed self-defense pass sets AIState.Combat and holds it through the
        // buffer — and CombatMovement.IsInCombat covers any other combat path.
        private bool InCombatNow =>
            (Ai != null && Ai.IsInCombat) || (CombatMovement != null && CombatMovement.IsInCombat);

        // TEMP diagnostic — why patrol still moves during combat. Remove once confirmed.
        private float _lastCombatDiag;
        private void CombatDiag(string branch)
        {
            if (Time.time - _lastCombatDiag < 1f) return;
            _lastCombatDiag = Time.time;
            var t = Ai != null ? Ai.GetTargetCreature() : null;
            Debug.Log($"[PatrolCombatDiag] {(Companion != null ? Companion.name : "?")} {branch} inCombat={InCombatNow} " +
                      $"aiState={(Ai != null ? Ai.IsInCombat : false)} cm={(CombatMovement != null && CombatMovement.IsInCombat)} " +
                      $"target={(t != null ? t.m_name : "none")}");
        }

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
            RerollDeviation();
        }

        public override bool Update()
        {
            // DROP patrol entirely while in combat. Combat (the stationed self-defense / CompanionAI path) is the
            // single mover; patrol must issue NO moves AND release its movement-authority lease so the combat
            // mover can take it — otherwise the two fight every frame and the NPC just glitches in place. We key
            // off CompanionAI's combat STATE, which stays true through the post-combat hold buffer, so patrol
            // only resumes once combat (and that buffer) is fully over.
            if (!IsActive || InCombatNow)
            {
                CombatDiag("HELD");
                StopMovement();   // release the 'Patrol' authority lease so combat owns movement; issue nothing
                _targetSince = Time.time;
                return false;
            }

            if (_route == null || _route.Points.Count < 2) return true; // route lost → end (re-evaluated next tick)

            // Stop and let a nearby player interact/chat instead of pushing past them — and don't walk off
            // while the player is engaging the NPC (they stay within this range while its UI is open, since
            // the inventory pins them in place). Resumes from the same waypoint once they leave.
            if (Player.GetClosestPlayer(Transform.position, ChatPauseRange) != null)
            {
                StopMovement();
                _targetSince = Time.time;
                return false;
            }

            if (_waiting)
            {
                StopMovement();
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

            // Restart the per-waypoint join timer whenever the target waypoint changes (incl. after a skip).
            if (_index != _lastIndex) { _lastIndex = _index; _targetSince = Time.time; }

            Vector3 target = _route.Points[_index] + _deviation;
            if (!IsReachable(target)) target = _route.Points[_index];   // deviation pushed off-mesh → use base point

            // Couldn't reach this waypoint within the timeout (unreachable, or just assigned the route from far
            // away) → snap onto it. This is how an NPC that isn't on its route gets there. Vanilla MoveTo
            // (FindPath/m_path) handles ordinary obstacle avoidance, so this is only the last-resort backstop.
            if (Time.time - _targetSince > TeleportTimeout)
            {
                TeleportTo(_route.Points[_index]);
                OnArrived();
                return false;
            }

            // Force vanilla WALK speed. The pathfinding chain only ever calls SetRun(false), leaving m_walk
            // false → the NPC would use the jog tier (m_speed=10, ~2× walk). Setting m_walk every patrol frame
            // selects m_walkSpeed; other behaviors (combat/follow) re-assert their own mode so this won't stick.
            CombatDiag("MOVING");
            Companion?.GetComponent<Character>()?.SetWalk(true);
            TryMoveToPosition(target, walk: true);   // pathfinding lives in vanilla MoveTo here — keep it, do not swap for straight-line

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
