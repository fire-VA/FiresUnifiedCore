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
        // The route is a GUIDE, not a rail: the per-route ArrivalRadius (the editor's "checkpoint precision")
        // lets the NPC cut corners / pass near waypoints instead of pivoting onto each exact point — small hugs
        // the line, large rounds corners. A must-hit node overrides it with a tight reach so a doorway/bridge is
        // actually entered. The look-ahead ("carrot") distance and curve smoothing are per-route too (see below).
        public const float WaitSeconds = 4.0f;
        public const float DeviationRadius = 1.2f;
        public const float ChatPauseRange = 3.5f;  // stop and let a player interact when this close
        public const float TeleportTimeout = 60f;  // seconds spent trying to reach a waypoint before snapping to it
        public const float MustHitReach = 0.9f;     // a must-hit node must be reached this tightly before the cursor advances past it
        private const float MinArrivalRadius = 0.4f;
        private const float MaxArrivalRadius = 12f;
        private const float MinLookAheadDistance = 0.5f;
        private const float MaxLookAheadDistance = 30f;
        private const float BuildPieceProbeHeight = 2f;
        private const float BuildPieceProbeDistance = 6f;
        private const float MinSectionSpeedMultiplier = 0.05f;

        private float ArrivalRadius => Mathf.Clamp(_route != null ? _route.ArrivalRadius : PatrolRoute.DefaultArrivalRadius, MinArrivalRadius, MaxArrivalRadius);
        private float LookAheadDistance => Mathf.Clamp(_route != null ? _route.LookAhead : PatrolRoute.DefaultLookAhead, MinLookAheadDistance, MaxLookAheadDistance);
        private float Smoothing => _route != null ? Mathf.Clamp01(_route.Smoothing) : 0f;
        // The reach for the CURRENT cursor node: tight if it's flagged must-hit, else the route's arrival radius.
        private float ReachFor(int index) => (_route != null && _route.MustHit.Contains(index)) ? Mathf.Min(ArrivalRadius, MustHitReach) : ArrivalRadius;

        // Player-built pieces (bridges/floors) live on these layers; a downward probe at the route surface that
        // hits one carrying a Piece component means it's NOT on the terrain navmesh, so we drive direct there.
        private static readonly int BuildPieceMask = UnityEngine.LayerMask.GetMask("piece", "static_solid", "Default");

        private PatrolRoute _route;
        private int _index;
        private int _direction = 1;        // +1 forward, -1 reversed (open routes)
        private bool _waiting;
        private float _waitUntil;
        private Vector3 _deviation;
        private int _lastIndex = -1;
        private float _targetSince;

        // DBSM speed model. _preset is null for the Default assignment (flat base walk, legacy behaviour);
        // _cum is the cumulative arc-length along the route so a section span maps to progress u∈[0,1].
        // _currentSpeedMul / _runTier are recomputed each frame and drive the tier + published speed.
        private FiresCore.Npc.Patrol.SpeedPreset _preset;
        private float[] _cum;
        private float _currentSpeedMul = 1f;
        private bool _runTier;

        // Stuck recovery — escalates re-path → skip-node before the long TeleportTimeout backstop. Movement
        // works now, so a stall is almost always a stale path or a waypoint behind geometry, both recoverable
        // here in seconds instead of waiting out the 60s teleport.
        private const float StuckRecoverDelay = 3.0f;   // seconds of no closing-progress before each escalation step
        private const float StuckMinProgress = 0.5f;    // metres of closing distance that counts as progress
        private const float MinStuckSpeedScale = 0.1f;
        private const float MaxStuckSpeedScale = 3f;
        private float _stuckSampleTime;
        private float _stuckBestDist = float.MaxValue;
        private bool _stuckRepathed;

        private CompanionCombatMovement _combatMovement;
        private CompanionCombatMovement CombatMovement =>
            _combatMovement != null ? _combatMovement
            : (_combatMovement = HostGameObject != null ? HostGameObject.GetComponent<CompanionCombatMovement>() : null);

        private FiresCore.Npc.AI.CompanionAI _ai;
        private FiresCore.Npc.AI.CompanionAI Ai =>
            _ai != null ? _ai
            : (_ai = HostGameObject != null ? HostGameObject.GetComponent<FiresCore.Npc.AI.CompanionAI>() : null);

        // True while the NPC is fighting (or in the post-combat hold buffer). CompanionAI's own combat STATE is
        // the authoritative signal — the stationed self-defense pass sets AIState.Combat and holds it through the
        // buffer — and CombatMovement.IsInCombat covers any other combat path.
        private bool InCombatNow =>
            (Ai != null && Ai.IsInCombat) || (CombatMovement != null && CombatMovement.IsInCombat);

        public override string BehaviorName => "Patrol";
        public override bool AvailableForIdleRotation => false; // force-started by route assignment, not random rotation

        // Pause-during-combat support: when the NPC is attacked, CompanionCombatMovement fires
        // OnCombatStarted → the framework interrupts this behavior (IsActive=false) instead of cancelling it,
        // lets CompanionAI fight, then calls ResumeAfterCombat (IsActive=true) when combat ends. Our route
        // cursor (_index/_direction/_waiting) lives on this persistent instance, so the NPC resumes exactly
        // where it left off — no SaveState/RestoreState needed.
        public override bool SupportsResumption => true;

        private PatrolAssignment Assignment => HostGameObject != null ? HostGameObject.GetComponent<PatrolAssignment>() : null;

        private Character _character;
        private Character Char =>
            _character != null ? _character
            : (_character = HostGameObject != null ? HostGameObject.GetComponent<Character>() : null);

        public override bool CanStart()
        {
            var assignment = Assignment;
            if (assignment == null || string.IsNullOrEmpty(assignment.RouteName)) return false;
            var route = PatrolRouteManager.GetRoute(assignment.RouteName);
            return route != null && route.Points.Count >= 2;
        }

        public override void Start()
        {
            base.Start();
            MaxDuration = float.MaxValue;   // patrol runs continuously until cancelled
            _route = PatrolRouteManager.GetRoute(Assignment?.RouteName);
            _preset = _route?.GetPreset(Assignment?.PresetName);   // null = Default (flat base walk)
            BuildArcLengths();
            Debug.Log($"[PatrolBehavior] started for {HostGameObject?.name} — route '{Assignment?.RouteName}' ({_route?.Points.Count ?? 0} pts)"
                      + (_preset != null ? $", preset '{_preset.Name}'" : ""));
            _direction = 1;
            _waiting = false;
            _index = NearestPointIndex();
            _lastIndex = _index;
            _targetSince = Time.time;
            ResetStuckTracking();
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
                // Do NOT StopMovement here — it calls SetMoveDir(zero) every frame and fights the combat mover
                // (that was the "slides instead of fighting" bug). During combat the sub-behavior is cancelled
                // upstream in CompanionIdleBehavior (stops ticking + releases its lease once), so this branch is
                // only a defensive no-op hold for any stray tick on the transition frame.
                _targetSince = Time.time;
                ResetStuckTracking();
                FiresCore.Npc.Patrol.PatrolSpeedState.Clear(Char); // let combat/other movers own the speed
                return false;
            }

            if (_route == null || _route.Points.Count < 2) return true; // route lost → end (re-evaluated next tick)

            // Stop and let a nearby player interact/chat instead of pushing past them — and don't walk off
            // while the player is engaging the NPC (they stay within this range while its UI is open, since
            // the inventory pins them in place). Resumes from the same waypoint once they leave.
            if (Player.GetClosestPlayer(Transform.position, ChatPauseRange) != null)
            {
                HoldStill();
                _targetSince = Time.time;
                ResetStuckTracking();
                FiresCore.Npc.Patrol.PatrolSpeedState.Clear(Char);
                return false;
            }

            if (_waiting)
            {
                HoldStill();
                FiresCore.Npc.Patrol.PatrolSpeedState.Clear(Char);
                if (Time.time < _waitUntil) return false;
                _waiting = false;
                AdvanceIndex();   // step off the endpoint / stop node, wrapping for loops
            }

            // Look-ahead: advance the cursor through every waypoint we're already within reach of BEFORE issuing
            // the move, so the target is always far enough that vanilla MoveTo never hits its stop-at-arrival
            // branch; the NPC flows through nodes instead of stopping at each. Distance is 3D (NOT XZ): routes
            // save full XYZ, so a marker up on a balcony/bridge must be reached at its real HEIGHT before we
            // advance — otherwise an elevated marker directly above the NPC reads as "reached" (XZ≈0) and gets
            // skipped, and it never climbs the stairs. Respecting Y is what keeps the NPC true to a multi-level
            // route instead of taking the ground path under it. Open-route endpoint sets _waiting and breaks out.
            int guard = 0;
            while (!_waiting && guard++ < _route.Points.Count
                   && Vector3.Distance(Transform.position, _route.Points[_index]) <= ReachFor(_index))
            {
                OnArrived();
            }
            if (_waiting) { HoldStill(); FiresCore.Npc.Patrol.PatrolSpeedState.Clear(Char); return false; }

            // Restart the per-waypoint join timer (and the stuck baseline) whenever the look-ahead loop advanced
            // us onto a genuinely new waypoint. A recovery skip claims its own index change (see
            // SkipCurrentWaypoint) so it does NOT reset the join timer — the TeleportTimeout backstop below must
            // keep counting across skips so a fully off-route NPC still snaps on.
            if (_index != _lastIndex) { _lastIndex = _index; _targetSince = Time.time; ResetStuckTracking(); }

            // DBSM: resolve the active section's speed multiplier + tier for THIS index (arc-length progress).
            // Sets _currentSpeedMul/_runTier; identity (1.0, walk) for the Default preset or outside all sections.
            ComputeSpeed();

            // Aim at a point well AHEAD along the route (not the current marker) so MoveTo never brakes for
            // arrival and the heading turns gradually — the NPC flows through the whole route as one path
            // instead of stuttering at each segment. The cursor above still advances as markers are passed.
            Vector3 carrot = CarrotAhead();
            // Is the route surface here a player-built piece (bridge/floor) the terrain navmesh excludes? Decided
            // from the BARE carrot — the anti-stacking deviation can sit in mid-air off a narrow bridge edge.
            bool onPiece = IsOnBuildPiece(carrot);
            // No deviation on a piece: keep the NPC centered on the authored route so it can't be nudged off a
            // narrow bridge. On terrain, apply it (anti-stacking) and drop it if it lands off-mesh.
            Vector3 target = onPiece ? carrot : carrot + _deviation;
            if (!onPiece && !IsReachable(target)) target = carrot;

            // Couldn't reach this waypoint within the timeout (unreachable, or just assigned the route from far
            // away) → snap onto it. This is how an NPC that isn't on its route gets there. Vanilla MoveTo
            // (FindPath/m_path) handles ordinary obstacle avoidance, so this is only the last-resort backstop.
            if (Time.time - _targetSince > TeleportTimeout)
            {
                TeleportTo(_route.Points[_index]);
                OnArrived();
                return false;
            }

            // Stuck recovery: if we stop closing on the current MARKER (not the always-ahead carrot, which we
            // never reach), re-path then skip the node — long before the 60s teleport. A skip changes the cursor.
            if (RecoverIfStuck(_route.Points[_index])) return false;

            // Tier + speed. Vanilla speed selection: m_walk true → m_walkSpeed; else m_run true (moving) →
            // m_runSpeed. The pathfinding chain only sets m_run (via the move's run flag), never m_walk, so we
            // own m_walk: walk tier forces it true (selects m_walkSpeed), run tier clears it (selects m_runSpeed
            // + run animation). The published multiplier is applied to the active field by CompanionSpeedRamp —
            // we never touch SetMoveDir. The Default preset keeps the exact legacy path (force walk, no publish).
            var character = Char;
            if (_preset == null)
            {
                character?.SetWalk(true);
                if (onPiece) TryMoveDirectToPosition(target, run: false);
                else         TryMoveToPosition(target, walk: true);
            }
            else
            {
                character?.SetWalk(!_runTier);
                FiresCore.Npc.Patrol.PatrolSpeedState.Set(character, _currentSpeedMul, _runTier);
                if (onPiece) TryMoveDirectToPosition(target, run: _runTier);
                else         TryMoveToPosition(target, walk: !_runTier, run: _runTier);
            }

            return false;   // never completes on its own
        }

        // Snaps the NPC onto a route point (owner-side; syncs to others via ZSyncTransform). Used as the
        // fallback when walking can't get it onto the route.
        private void TeleportTo(Vector3 point)
        {
            if (Transform == null) return;
            Transform.position = point;
            var body = HostGameObject != null ? HostGameObject.GetComponent<Rigidbody>() : null;
            if (body != null) { body.position = point; body.linearVelocity = Vector3.zero; }
        }

        // Stop AND clamp residual horizontal velocity. A bare StopMovement (SetMoveDir(0)) leaves a non-kinematic
        // NPC coasting under gravity, so it slides down slopes "like ice" while it waits/chats — the combat
        // mover's velocity-clamp is parked while patrol owns the body (IsInSubBehavior). Keep vel.y so gravity
        // still settles it onto the surface. Only runs on the idle frames patrol holds, never while walking.
        private void HoldStill()
        {
            StopMovement();
            var body = HostGameObject != null ? HostGameObject.GetComponent<Rigidbody>() : null;
            if (body != null && !body.isKinematic)
            {
                var velocity = body.linearVelocity;
                body.linearVelocity = new Vector3(0f, velocity.y, 0f);
            }
        }

        // A point LookAheadDistance ahead of the current cursor ALONG the route path (in _direction). A loop
        // wraps; an open route clamps at the far endpoint. Aiming here instead of at the discrete marker is what
        // keeps the NPC walking the route as one continuous path rather than braking/turning at every point.
        //
        // Two per-route shaping steps: (1) the carrot NEVER aims past a must-hit node — it stops exactly on it so
        // the NPC steers through the doorway/bridge instead of cutting the corner and skipping it; (2) with
        // Smoothing > 0 the landing point is bent from the straight chord onto the Catmull-Rom curve, so the NPC
        // follows a rounded path (matching the drawn line) instead of jerking point-to-point.
        private Vector3 CarrotAhead()
        {
            var points = _route.Points;
            int count = points.Count;
            float remain = LookAheadDistance;
            int current = _index;
            Vector3 here = points[current];
            int guard = 0;
            while (remain > 0f && guard++ < count + 2)
            {
                int next = _route.IsLoop ? ((current + _direction) % count + count) % count : current + _direction;
                if (!_route.IsLoop && (next < 0 || next >= count)) return here;   // open-route end → clamp the carrot
                Vector3 nextPoint = points[next];
                float len = Vector3.Distance(here, nextPoint);

                // Must-hit clamp: don't let the carrot cross a mandatory node — pin it at (or before) that node.
                if (_route.MustHit.Contains(next))
                    return len >= remain ? OnCurve(current, next, remain / len) : nextPoint;

                if (len < 0.001f) { current = next; here = nextPoint; continue; }
                if (len >= remain) return OnCurve(current, next, remain / len);
                remain -= len;
                current = next;
                here = nextPoint;
            }
            return here;
        }

        // The point a fraction f along the cur→nxt segment, bent onto the smoothed curve. At Smoothing 0 this is
        // the exact straight-chord point (unchanged legacy behaviour). PatrolSpline segments run in index order
        // (i → i+1), so travelling forward the segment is `cur` and travelling back it's `nxt` (this also handles
        // the loop wrap seam, where cur/nxt straddle 0 and Min() would pick the wrong segment).
        private Vector3 OnCurve(int current, int next, float fraction)
        {
            float smoothing = Smoothing;
            if (smoothing <= 0.0001f) return Vector3.Lerp(_route.Points[current], _route.Points[next], fraction);
            int seg = _direction > 0 ? current : next;
            float local = _direction > 0 ? fraction : 1f - fraction;
            return PatrolSpline.Point(_route.Points, _route.IsLoop, seg, local, smoothing);
        }

        // True when the route surface under <paramref name="point"/> is a player-built Piece (bridge, floor,
        // dock) rather than terrain — i.e. it isn't in Valheim's terrain navmesh, so the patrol must walk it
        // directly instead of via FindPath. A short downward probe + a Piece-component check is definitive.
        private static bool IsOnBuildPiece(Vector3 point)
        {
            if (Physics.Raycast(point + Vector3.up * BuildPieceProbeHeight, Vector3.down, out var hit, BuildPieceProbeDistance, BuildPieceMask, QueryTriggerInteraction.Ignore))
                return hit.collider != null && hit.collider.GetComponentInParent<Piece>() != null;
            return false;
        }

        private void OnArrived(bool writeCheckpoint = true)
        {
            if (writeCheckpoint) WriteCheckpoint();
            int last = _route.Points.Count - 1;

            // DBSM stop point flagged on this node → hold here for its duration, then resume. We keep _index
            // ON the stop node and set _waiting; the top-of-Update wait branch calls AdvanceIndex() on wake,
            // which steps off the node (so we don't re-arm the same stop until the route brings us back). The
            // section curve's Floor near the stop provides the ease-in; HoldStill zeroes velocity while paused.
            var stop = _preset?.StopAt(_index);
            if (stop != null)
            {
                _waiting = true;
                _waitUntil = Time.time + Mathf.Max(0f, stop.Duration);
                // If this stop is also an open-route endpoint, flip direction now so AdvanceIndex heads back.
                if (!_route.IsLoop && ((_direction == 1 && _index >= last) || (_direction == -1 && _index <= 0)))
                    _direction = -_direction;
                return;
            }

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

        // Advance the cursor one node in the travel direction, wrapping for loops and clamping for open routes.
        // Used on wake from any wait (endpoint or stop point). Matches the old open-route clamp exactly; the loop
        // wrap only matters for a stop flagged near the loop seam.
        private void AdvanceIndex()
        {
            int count = _route.Points.Count;
            if (count == 0) return;
            _index = _route.IsLoop ? (((_index + _direction) % count) + count) % count
                                   : Mathf.Clamp(_index + _direction, 0, count - 1);
        }

        // Escalating stuck recovery, sampled each frame we issue a move. _stuckBestDist is the closest we've
        // gotten to the current waypoint; while it keeps improving we're fine. If it stalls for StuckRecoverDelay
        // we first force a fresh path (a stale m_path is the usual cause now that the motor works); if that still
        // doesn't help we skip the node entirely (it's unreachable — behind geometry / off-mesh — so we route to
        // the next one instead of grinding until the 60s TeleportTimeout fires). Returns true only when a node
        // was skipped, so the caller re-evaluates next tick.
        private bool RecoverIfStuck(Vector3 target)
        {
            // Scale the stuck thresholds by the DBSM speed multiplier: in a slow (Floor) zone the NPC covers
            // less ground per second, so require proportionally less closing distance to count as progress and
            // grant proportionally more patience before escalating — otherwise a deliberately slow span reads as
            // "stuck" and false-skips its nodes. Fast zones tighten symmetrically (they close distance quickly).
            float mul = Mathf.Clamp(_currentSpeedMul, MinStuckSpeedScale, MaxStuckSpeedScale);
            float minProgress = StuckMinProgress * mul;
            float recoverDelay = StuckRecoverDelay / mul;

            float dist = Vector3.Distance(Transform.position, target);   // 3D: climbing toward an elevated marker counts as progress
            if (dist < _stuckBestDist - minProgress)
            {
                _stuckBestDist = dist;
                _stuckSampleTime = Time.time;
                _stuckRepathed = false;
                return false;
            }

            if (Time.time - _stuckSampleTime < recoverDelay) return false;
            _stuckSampleTime = Time.time;

            if (!_stuckRepathed)
            {
                _stuckRepathed = true;
                Ai?.RequestPathRecalculation();   // recompute the path, then fall through and re-issue the move
                return false;
            }

            // Re-path didn't free us. If the waypoint is genuinely OFF the navmesh — e.g. a marker on a
            // player-built BRIDGE (Valheim bakes its terrain navmesh WITHOUT runtime pieces, so pathing tries
            // to route UNDER the bridge and never reaches it) — snap across it now instead of grinding the full
            // 60s TeleportTimeout. A must-hit node is likewise snapped onto rather than skipped: it's flagged
            // mandatory (doorway/bridge) precisely because the drawn path is the only correct one there. A plain
            // reachable-but-stuck marker is just skipped, as before.
            if (!IsReachable(target) || _route.MustHit.Contains(_index))
            {
                TeleportTo(target);
                OnArrived();
                return true;
            }

            SkipCurrentWaypoint();
            return true;
        }

        // Abandon the current (unreachable) waypoint and advance the cursor without requiring arrival. We claim
        // the index change ourselves (set _lastIndex) so Update's arrival-reset doesn't restart TeleportTimeout —
        // that backstop must keep counting so a genuinely off-route NPC still snaps onto its route if even
        // skipping can't recover. No checkpoint is written for a node we never reached.
        private void SkipCurrentWaypoint()
        {
            OnArrived(writeCheckpoint: false);
            _lastIndex = _index;
            ResetStuckTracking();
        }

        private void ResetStuckTracking()
        {
            _stuckSampleTime = Time.time;
            _stuckBestDist = float.MaxValue;
            _stuckRepathed = false;
        }

        private int NearestPointIndex()
        {
            if (_route == null || Transform == null) return 0;
            int best = 0;
            float bestSq = float.MaxValue;
            for (int i = 0; i < _route.Points.Count; i++)
            {
                float sqrDistance = (Transform.position - _route.Points[i]).sqrMagnitude;
                if (sqrDistance < bestSq) { bestSq = sqrDistance; best = i; }
            }
            return best;
        }

        private void RerollDeviation()
        {
            var deviation = Random.insideUnitCircle * DeviationRadius;
            _deviation = new Vector3(deviation.x, 0f, deviation.y);
        }

        // Cumulative arc-length along the route polyline: _cum[i] = distance from Points[0] to Points[i]. Lets
        // a section span [Low..High] map the current node index to progress u∈[0,1] the same way outbound and
        // inbound (arc-length is direction-agnostic), so a section reads symmetrically on an open route's return.
        private void BuildArcLengths()
        {
            _cum = null;
            var points = _route?.Points;
            if (points == null || points.Count == 0) return;
            _cum = new float[points.Count];
            _cum[0] = 0f;
            for (int i = 1; i < points.Count; i++)
                _cum[i] = _cum[i - 1] + Vector3.Distance(points[i - 1], points[i]);
        }

        // Resolve the DBSM speed multiplier + tier for the current node index. Identity (1.0, walk tier) for the
        // Default preset or when outside every section; inside a section, u is arc-length progress across its span.
        private void ComputeSpeed()
        {
            _currentSpeedMul = 1f;
            _runTier = false;
            if (_preset == null || _cum == null) return;

            var sec = _preset.SectionAt(_index);
            if (sec != null)
            {
                int low = Mathf.Clamp(sec.Low, 0, _cum.Length - 1);
                int high = Mathf.Clamp(sec.High, 0, _cum.Length - 1);
                float len = _cum[high] - _cum[low];
                float here = _cum[Mathf.Clamp(_index, low, high)] - _cum[low];
                float sectionProgress = len > 1e-3f ? here / len : 0f;
                _currentSpeedMul = Mathf.Max(MinSectionSpeedMultiplier, sec.Sample(sectionProgress));
            }
            _runTier = _currentSpeedMul > _preset.RunThreshold;
        }

        public override void Cancel()
        {
            FiresCore.Npc.Patrol.PatrolSpeedState.Clear(Char); // drop the speed override when patrol is cancelled
            base.Cancel();
        }

        // Persists the last reached waypoint (base point, not the deviated target) to the NPC's ZDO so a
        // death-while-patrolling can respawn it here. Owner-only — the server owns these NPCs.
        private void WriteCheckpoint()
        {
            var nview = HostGameObject != null ? HostGameObject.GetComponent<ZNetView>() : null;
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
