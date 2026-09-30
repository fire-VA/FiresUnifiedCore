using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Walking a body along the Humanoid navmesh the way vanilla monsters and our companions walk (CompanionAI goes through
    /// BaseAI.MoveTo), for ANY body, a Player included (one brain: the FDT bot, Fire 2026-09-29 "vanilla AI doesn't get stuck like
    /// this"). The rules (Tools\BOT_MOVEMENT_VS_VANILLA.md):
    /// - the path is asked for at most once a second, and kept for up to 5 s while the goal moves less than a metre;
    /// - the body heads straight for the NEXT corner only, which counts as passed within 0.5 m walking / 1 m running;
    /// - no steering on a path: the navmesh already goes round trees and rocks, and leaving the corner line walks into them;
    /// - turn first, then move: no moveDir until the body faces within <see cref="MoveMinAngle"/> of the way;
    /// - no path: <see cref="WalkState.NoPath"/> and no move, and the caller reports it unreachable. Only a goal within
    ///   <see cref="FeelRange"/> is felt for (three casts ahead, ±20° round what they hit, a 4 s back-out when it hasn't moved).
    /// One instance per body; call <see cref="Tick"/> every frame, turn toward lookDir and apply moveDir (a companion through its
    /// movement authority, a player body through its input). No allocations per tick.
    /// </summary>
    public sealed class PathWalker
    {
        /// <summary>
        /// Partial (appended, so older callers' values don't shift): the navmesh path ends short of the goal by more than the reach
        /// (R70: "arrived" 4.7 m short at a 1.05 m lip, then 90 s standing still). No move; the caller decides (wait and re-ask,
        /// jump, go round). A goal within <see cref="FeelRange"/> of the path's end is felt for instead.
        /// </summary>
        /// <remarks>Door (appended): a passable closed door blocks the way (<see cref="DoorRule.DoorOnPath"/>); moveDir heads for it
        /// (zero within <see cref="DoorRule.InteractDistance"/>), <see cref="Door"/> names it. The caller opens it
        /// (<see cref="DoorRule.OpenFor"/>) and may <see cref="DoorRule.CloseBehind"/>; once it is open the walker asks for a new path.</remarks>
        public enum WalkState { Arrived, Moving, Turning, NoPath, Pending, Partial, Door }

        /// <summary>The door this walk is waiting on (<see cref="WalkState.Door"/>), else null.</summary>
        public Door Door { get; private set; }

        /// <summary>
        /// This walker's steep-ground guard (every heading it gives goes through it). A caller walking straight at a goal outside the
        /// walker (FDT's last stretch) should put its heading through <see cref="SlopeGuide.Steer"/> too.
        /// </summary>
        public SlopeGuide Slope { get; } = new SlopeGuide();

        /// <summary>Within this of the goal (flat m) the slope guard stands down.</summary>
        public const float SlopeFreeNearGoal = 2.5f;

        private string _slopeLogged = "";

        public const float MoveMinAngle = 30f, FeelRange = 3f;
        private const float RepathSeconds = 1f, KeepPathSeconds = 5f, GoalMovedMeters = 1f;
        private const float CornerWalk = 0.5f, CornerRun = 1f;
        private const float StuckCheckSeconds = 1.5f, StuckMeters = 0.2f, BackOutSeconds = 4f, SideAngle = 20f;

        private static int s_blockMask;
        private static int BlockMask => s_blockMask != 0 ? s_blockMask
            : (s_blockMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle"));

        private readonly List<Vector3> _path = new List<Vector3>();
        private float _lastAsk = -999f;
        private Vector3 _lastGoal = new Vector3(float.NaN, 0f, 0f);
        private bool _lastFound;
        private int _pathCorners;
        private float _stuckClock;
        private Vector3 _stuckFrom;
        private float _backOutUntil;
        private float _backOutAngle;

        /// <summary>Short, printable: "arrived", "moving to corner k/n", "turning", "pending", "no path", "stuck: backing out", "feeling: …".</summary>
        public string State { get; private set; } = "";

        /// <summary>Paths asked since the last <see cref="Reset"/>.</summary>
        public int Repaths { get; private set; }

        /// <summary>This tick walks at a step the body can jump (0.2.202): the driver should jump now (Jumping's own timing still applies).</summary>
        public bool WantsJump { get; private set; }

        /// <summary>Corners passed since the last <see cref="Reset"/>.</summary>
        public int CornersPassed { get; private set; }

        /// <summary>Why the last walk ended without arriving ("no path", …), or null.</summary>
        public string GaveUp { get; private set; }

        /// <summary>For the drill evidence: "path: re-path N, corners N, gave up: &lt;reason|no&gt;".</summary>
        public string Summary => $"path: re-path {Repaths}, corners {CornersPassed}, gave up: {GaveUp ?? "no"}";

        /// <summary>Corners left on the current path (the next one first). Read only.</summary>
        public IReadOnlyList<Vector3> Corners => _path;

        /// <summary>Forget the path and the counters: the next tick asks again (call per new walk).</summary>
        public void Reset()
        {
            _path.Clear();
            _lastAsk = -999f;
            _lastGoal = new Vector3(float.NaN, 0f, 0f);
            _lastFound = false;
            _pathCorners = 0;
            _backOutUntil = 0f;
            _stuckClock = 0f;
            Repaths = 0;
            CornersPassed = 0;
            GaveUp = null;
            Door = null;
            Slope.Reset();
            _slopeLogged = "";
            _detour.Clear();
            _slideSince = -1f;
            _checkRidge = false;
            _overshootFrom = new Vector3(float.NaN, 0f, 0f);
            _contourSince = -1f;
            _terrainNext = 0f;
            State = "";
        }

        /// <summary>
        /// One frame of walking <paramref name="body"/> to <paramref name="goal"/>. <paramref name="facing"/> is where the body looks
        /// now (a player's look is driven apart from its move). lookDir: turn toward it; moveDir: walk it (zero while turning,
        /// arrived, pending or without a path). Pending = the navmesh isn't loaded there yet: try again.
        /// </summary>
        public WalkState Tick(Character body, Vector3 facing, Vector3 goal, float reach, bool run, out Vector3 moveDir, out Vector3 lookDir)
        {
            moveDir = Vector3.zero;
            lookDir = Vector3.zero;
            WantsJump = false;
            if (body == null) { State = "no body"; return WalkState.NoPath; }
            Vector3 at = body.transform.position;
            float cornerReach = run ? CornerRun : CornerWalk;
            float goalFlat = FlatDistance(at, goal);
            // Arrived = near AND no wall between (0.2.213, R84 corridor: the goal in the corridor's closed end was "reached" from outside
            // the end wall, 0 of 13 samples inside the mouth).
            if (goalFlat < Mathf.Max(reach, cornerReach) && (goalFlat < 0.5f || NoWallBefore(at, goal)))
            {
                _path.Clear();
                Door = null;
                _retryGoal = new Vector3(float.NaN, 0f, 0f);   // arrived: the next walk starts without failure memory (a rescue Reset keeps it)
                State = "arrived";
                return WalkState.Arrived;
            }

            // No progress (0.2.202, R77 u_trap: "circling: walked 15 m in 29 s without getting nearer than 5.2 m", a navmesh path
            // all along, so no terrain trigger fired): not nearer to the goal by NoProgressMetres in NoProgressSeconds -> a terrain
            // route (it sees built walls on its fine grid), whatever the navmesh says.
            if (float.IsNaN(_bestGoal.x) || FlatDistance(goal, _bestGoal) > 3f) { _bestGoal = goal; _bestFlat = goalFlat; _bestAt = Time.time; }
            else if (goalFlat < _bestFlat - NoProgressMetres) { _bestFlat = goalFlat; _bestAt = Time.time; }
            else if (Time.time - _bestAt > NoProgressSeconds && Door == null
                     && (_detour.Count == 0 || (_detour[0].y - at.y >= WaypointLevelMetres && FlatDistance(at, _detour[0]) < CornerRun + 0.5f)))   // or below a step waypoint it can't get up (0.2.209)
            {
                _bestAt = Time.time;
                // The door it's stalled at first (0.2.205, R78 nav_course doorway: stood 0.8 m from a closed gate, then went 26 m round).
                if (FindDoor(body, at, goal, StalledDoorMetres))
                {
                    Debug.Log($"[PathWalker] {body.m_name}: door: {Utils.GetPrefabName(Door.gameObject)} on the path " +
                              $"({FlatDistance(at, Door.transform.position):0.0} m) is cheaper than a detour; opening");
                    return ToDoor(body, at, out moveDir, out lookDir);
                }
                if (PlanRooms(body, at, goal, $"no progress: {NoProgressSeconds:0} s without getting nearer than {_bestFlat:0.0} m")
                    || PlanTerrain(body, at, goal, $"no progress: {NoProgressSeconds:0} s without getting nearer than {_bestFlat:0.0} m"))
                    return Steer(body, facing, _detour[0] - at, goalFlat, out moveDir, out lookDir);
                if (at.y > IndoorsHeight)
                {
                    // Vanilla-style indoors (0.2.209): nothing better planned -> a fresh navmesh path from here, not pressing on.
                    Debug.Log($"[PathWalker] {body.m_name}: stuck indoors ({NoProgressSeconds:0} s without getting nearer than {_bestFlat:0.0} m); asking the navmesh again");
                    _path.Clear();
                    _lastAsk = -999f;
                    _overshootFrom = new Vector3(float.NaN, 0f, 0f);
                }
            }

            // A door this walk is waiting on: still closed -> keep heading for it; opened (or gone) -> ask for a new path through it.
            if (Door != null)
            {
                if (Door && DoorRule.IsClosed(Door)) return ToDoor(body, at, out moveDir, out lookDir);
                Door = null;
                _lastAsk = -999f;
                _path.Clear();
            }

            // Going round a building's footprint (PlanRound): its corners first, then back to the navmesh.
            _goalNow = goal;
            if (_detour.Count > 0)
            {
                // A waypoint up a step is reached ON it, not beside it (0.2.209, R80 jump_steps: "corner 3/4" on the 1.0 m row counted
                // as reached from the ground 0.9 m away, and the walk went on along the rows' side into the 2 m face; Fire: "it needs to
                // recognize the only way up … is to take the incremental jumps").
                while (_detour.Count > 0 && FlatDistance(at, _detour[0]) < cornerReach + 0.5f && _detour[0].y - at.y < WaypointLevelMetres)
                { _detour.RemoveAt(0); _bestAt = Time.time; }
                if (_detour.Count > 0)
                {
                    State = $"round {_detourWhat}: corner {_detourTotal - _detour.Count + 1}/{_detourTotal}";
                    Vector3 toWaypoint = _detour[0] - at;
                    toWaypoint.y = 0f;
                    // The step up to it is jumped as it comes (the walker's own jump request, as on a navmesh path's SlideAlong).
                    if (_detour[0].y - at.y > Perception.StepHeight && toWaypoint.magnitude < StepJumpReach && toWaypoint.sqrMagnitude > 0.0001f)
                    {
                        JumpAdvice up = Jumping.Check(body, toWaypoint.normalized, false);
                        if (!up.Wall && (up.Jump || (up.LipHeight > Perception.StepHeight && up.LipHeight <= Jumping.MaxLip && up.LipDistance <= Jumping.LipReach + 0.3f)))
                        {
                            WantsJump = true;
                            State = $"round {_detourWhat}: corner {_detourTotal - _detour.Count + 1}/{_detourTotal}: step {up.LipHeight:0.0} m up -> jump";
                            if (State != _slideLogged) { _slideLogged = State; Debug.Log($"[PathWalker] {body.m_name}: {State}"); }
                        }
                    }
                    return Steer(body, facing, _detour[0] - at, goalFlat, out moveDir, out lookDir);
                }
                Debug.Log($"[PathWalker] {body.m_name}: round {_detourWhat}: past it ({goalFlat:0.0} m to go); asking a path again");
                _lastAsk = -999f;
                _path.Clear();
            }

            Vector3 want;
            bool found = Ask(at, goal, body.GetRadius(), out bool pending);
            if (_cornerNote != null)
            {
                Debug.Log($"[PathWalker] {body.m_name}: {_cornerNote}");
                _cornerNote = null;
            }
            if (found)
            {
                _radius = body.GetRadius();
                while (_path.Count > 0 && CornerDone(body, at, cornerReach))
                {
                    _path.RemoveAt(0);
                    CornersPassed++;
                    _overshootFrom = new Vector3(float.NaN, 0f, 0f);
                }
                bool overshooting = !float.IsNaN(_overshootFrom.x);
                bool atCorner = at.y <= IndoorsHeight && _path.Count > 1 && (overshooting || FlatDistance(at, _path[0]) < cornerReach || PassedCorner(at));
                // No overshoot without a way walked toward THIS corner (0.2.209, R80 l_corner: the path's first corner was where the bot
                // stood, so "the way we came" was a stale heading and it walked 3 m away from the goal): re-plan instead.
                if (atCorner && !overshooting && _blockedLeg == null && (_approach.sqrMagnitude < 0.0001f || FlatDistance(_approachCorner, _path[0]) > 1f))
                    _blockedLeg = $"corner {_pathCorners - _path.Count + 1}/{_pathCorners}: the next leg hits {LegBlocker(at, _path[1])} and no approach to carry on from";
                // A corner whose next leg stayed blocked (0.2.209): plan round on the terrain from here, the blocker kept off; only when
                // the terrain has no way round is the leg taken as before.
                if (_blockedLeg != null)
                {
                    string why = _blockedLeg;
                    _blockedLeg = null;
                    _overshootFrom = new Vector3(float.NaN, 0f, 0f);
                    if (_legDoor != null && _legDoor && DoorRule.IsClosed(_legDoor))
                    {
                        Door = _legDoor;
                        _legDoor = null;
                        _legAvoid.Clear();
                        Debug.Log($"[PathWalker] {body.m_name}: door: {Utils.GetPrefabName(Door.gameObject)} is what blocks the next leg; opening it, not planning round");
                        return ToDoor(body, at, out moveDir, out lookDir);
                    }
                    _legDoor = null;
                    if (PlanTerrain(body, at, goal, why, force: true)) return Steer(body, facing, _detour[0] - at, goalFlat, out moveDir, out lookDir);
                    Debug.Log($"[PathWalker] {body.m_name}: {why}: no terrain route; walking the leg");
                    _path.RemoveAt(0);
                    CornersPassed++;
                    atCorner = false;
                }
                // At a corner whose next leg isn't clear yet (the wall end is still in the way): keep walking the way we came, past
                // the end, until the capsule to the next corner is clear (Fire, R77: "tries to turn to round the corner too soon").
                // Once started, the overshoot carries on forward (0.2.205, R78: out of reach of the corner the walker aimed BACK at it, then
                // overshot again: "corner 10/11: walking past the wall end" with the heading flipping 164° / 357° and 5 dithers in a minute).
                if (atCorner && _approach.sqrMagnitude > 0.0001f)
                {
                    if (float.IsNaN(_overshootFrom.x))
                    {
                        _overshootFrom = at;
                        Debug.Log($"[PathWalker] {body.m_name}: corner {_pathCorners - _path.Count + 1}/{_pathCorners}: next leg blocked by the wall end; walking on past it");
                    }
                    State = $"corner {_pathCorners - _path.Count + 1}/{_pathCorners}: walking past the wall end";
                    return Steer(body, facing, _approach, goalFlat, out moveDir, out lookDir);
                }
                if (_path.Count > 0 && FlatDistance(at, _path[0]) >= cornerReach)
                {
                    Vector3 approach = _path[0] - at;
                    approach.y = 0f;
                    _approach = approach.normalized;
                    _approachCorner = _path[0];
                }
                if (_path.Count == 0)
                {
                    // The path is used up but the goal (checked above) is not reached: a partial navmesh path that ends short.
                    if (goalFlat > FeelRange)
                    {
                        if (FindDoor(body, at, goal)) return ToDoor(body, at, out moveDir, out lookDir);
                        NavmeshIsland(body, at, goal);
                        if (!PlanRooms(body, at, goal, $"the navmesh path ends {goalFlat:0.0} m short") && !PlanRound(body, at, goal, null)
                            && !PlanTerrain(body, at, goal, $"the navmesh path ends {goalFlat:0.0} m short"))
                        {
                            State = $"partial (end {goalFlat:0.0} m short)";
                            GaveUp = State;
                            return WalkState.Partial;
                        }
                        want = _detour[0] - at;
                    }
                    else if (WayOut(body, at, goal, goalFlat)) { if (Door != null) return ToDoor(body, at, out moveDir, out lookDir); want = _detour[0] - at; }
                    else want = Feel(body, goal);
                }
                else
                {
                    // A fresh navmesh route that climbs a face steeper than this body can (the navmesh agent climbs far more than a
                    // player's 38°: R76 "route none … path 0 m vs straight 17 m" straight up a ridge): plan round it on the terrain.
                    if (_checkRidge)
                    {
                        _checkRidge = false;
                        if (RidgeOnPath(body, at, out string ridge) && PlanTerrain(body, at, goal, ridge))
                            return Steer(body, facing, _detour[0] - at, goalFlat, out moveDir, out lookDir);
                        // A navmesh leg a body-wide capsule can't walk (0.2.209, R80 l_corner / u_trap on both bodies: "route: navmesh,
                        // 2 corner(s), 8 m vs straight 7 m" where the way round was 19 m): the tile was baked before the walls were built
                        // (vanilla re-bakes only when poked 5 s after its last bake), so its corners cut through them. The terrain
                        // planner sees built walls on its own grid.
                        bool staleLeg = StaleLeg(at, out string stale);
                        // A stale route isn't kept by the re-path hysteresis (0.2.218, R88 corridor: the tile older than the course
                        // routed round the outside to the cap; the re-baked, longer route through the mouth was never taken because a new
                        // route only replaces the walked one when it is shorter): for StaleRouteSeconds any fresh route replaces it.
                        if (staleLeg) _staleUntil = Time.time + StaleRouteSeconds;
                        if (staleLeg && PlanTerrain(body, at, goal, stale))
                            return Steer(body, facing, _detour[0] - at, goalFlat, out moveDir, out lookDir);
                        // A closed door on the straight way beats a long way round (0.2.213, R84 doorway, self body: "route: navmesh,
                        // 13 corner(s), 19 m vs straight 8 m" round the 14 m wall line, "door passes 0"): the navmesh routes round a shut
                        // door; the door rule opens it.
                        float roundLength = PathRemaining(at, _path);
                        if (roundLength > goalFlat * DoorDetourShare + DoorDetourMetres && FindDoor(body, at, goal))
                        {
                            Debug.Log($"[PathWalker] {body.m_name}: door: {Utils.GetPrefabName(Door.gameObject)} on the straight way ({goalFlat:0} m) beats the " +
                                      $"{roundLength:0} m way round; opening it");
                            return ToDoor(body, at, out moveDir, out lookDir);
                        }
                        // Which planner each walk uses, so a regression names itself (0.2.206, the lead: "route: navmesh" / "route: terrain (<why>)").
                        float pathLength = PathRemaining(at, _path);
                        if (Time.time >= _navLogAt)
                        {
                            _navLogAt = Time.time + 5f;
                        Debug.Log($"[PathWalker] {body.m_name}: route: navmesh, {_path.Count} corner(s), {pathLength:0} m vs straight {goalFlat:0} m" +
                                  (LastClearance >= 0f ? $", clearance kept {LastClearance:0.0} m (target {WantedCornerClearance:0.0})" : ", clear of walls"));
                        }
                    }
                    want = _path[0] - at;
                    State = $"moving to corner {_pathCorners - _path.Count + 1}/{_pathCorners}";
                    // One shoulder on a corner: a short sidestep off it, still facing the way on (0.2.209, Fire: "simply move a little
                    // bit more to the right or the left … it can strafe as well"), before any slide, re-plan or turn.
                    if (Strafe(body, at, want, out moveDir, out lookDir)) return WalkState.Moving;
                    want = SlideAlong(body, want);
                    if (_slideDoor != null)
                    {
                        Door = _slideDoor;
                        _slideDoor = null;
                        Debug.Log($"[PathWalker] {body.m_name}: door: {Utils.GetPrefabName(Door.gameObject)} in the way on the path; opening it, not sliding along it");
                        return ToDoor(body, at, out moveDir, out lookDir);
                    }
                }
            }
            else if (pending)
            {
                State = "pending";
                return WalkState.Pending;
            }
            else if (goalFlat <= FeelRange)
            {
                if (WayOut(body, at, goal, goalFlat)) { if (Door != null) return ToDoor(body, at, out moveDir, out lookDir); want = _detour[0] - at; }
                else want = Feel(body, goal);
            }
            else
            {
                if (FindDoor(body, at, goal)) return ToDoor(body, at, out moveDir, out lookDir);
                // No navmesh route and a building in the straight way (Fire, R74/R75: pinned at the mausoleum facade for
                // minutes): go round its footprint, corner by corner, the shorter way.
                if (!PlanRooms(body, at, goal, "no navmesh path") && !PlanRound(body, at, goal, null) && !PlanTerrain(body, at, goal, "no navmesh path"))
                {
                    State = "no path";
                    GaveUp = "no path";
                    return WalkState.NoPath;
                }
                want = _detour[0] - at;
            }

            return Steer(body, facing, want, goalFlat, out moveDir, out lookDir);
        }

        // The last step of every tick: the slope guard, turn-then-move.
        private WalkState Steer(Character body, Vector3 facing, Vector3 want, float goalFlat, out Vector3 moveDir, out Vector3 lookDir)
        {
            moveDir = Vector3.zero;
            lookDir = Vector3.zero;
            Vector3 flat = new Vector3(want.x, 0f, want.z);
            if (flat.sqrMagnitude < 0.0001f) return WalkState.Moving;
            // Ground ahead too steep for this body (it would slide): the nearest walkable heading along the contour; nothing
            // walkable round it = a partial way (Fire: the bot wandered into steep hills, on paths and on the loot walk). Not in
            // the last SlopeFreeNearGoal m: a drop lying on the slope is reached by sliding if need be.
            if (goalFlat > SlopeFreeNearGoal)
            {
                Vector3 steered = Slope.Steer(body, flat, out bool boxed);
                if (Slope.State != _slopeLogged)
                {
                    _slopeLogged = Slope.State;
                    if (Slope.State.Length > 0) Debug.Log($"[PathWalker] {body.m_name}: {Slope.State} ({goalFlat:0.0} m to go)");
                }
                // Following a contour for long means the slope ahead never clears (a ridge or a pocket in the way): plan round it.
                if (Slope.State.Length == 0) _contourSince = -1f;
                else if (_contourSince < 0f) _contourSince = Time.time;
                bool contourLong = _contourSince >= 0f && Time.time - _contourSince > ContourPlanSeconds && _detour.Count == 0;
                if (boxed || contourLong)
                {
                    string reason = boxed ? Slope.State : $"on a slope contour for {Time.time - _contourSince:0} s ({Slope.State})";
                    if (PlanTerrain(body, body.transform.position, _goalNow, reason))
                    {
                        _contourSince = -1f;
                        State = $"terrain route: waypoint 1/{_detourTotal}";
                        return WalkState.Moving;
                    }
                }
                if (boxed)
                {
                    State = Slope.State;
                    GaveUp = State;
                    return WalkState.Partial;
                }
                if (Slope.State.Length > 0)
                {
                    State = Slope.State;
                    flat = steered;
                    want = steered;
                }
            }
            // Never into a dungeon's exit teleport unless the goal is the exit (0.2.206, R78 Crypt4: the "walk on past the wall end" step
            // right after entering walked into the exit door's trigger at the spawn point and left the dungeon).
            if (KeepOffExit(body, flat, out Vector3 around))
            {
                flat = around;
                want = around * want.magnitude;
            }
            // A look-ahead (0.2.206, Fire: "i just dont understand how it is incapable of detecting objects its walking directly into"; every
            // stall line read "at 0.6 m"): a body-wide cast LookAheadMetres along the heading; something solid there (not a door it may
            // open, not a step it can jump) turns the heading the least that clears it, before contact.
            if (goalFlat > LookAheadMetres && flat.sqrMagnitude > 0.0001f && Time.time >= _calmUntil
                && LookAhead(body, flat.normalized, _goalNow, Mathf.Min(LookAheadMetres, want.magnitude), out Vector3 clearDir, out string blocker, out float at, out float turned))
            {
                flat = clearDir * flat.magnitude;
                want = clearDir * want.magnitude;
                if (Time.time - _lookLoggedAt > 3f || blocker != _lookLogged)
                {
                    _lookLoggedAt = Time.time;
                    _lookLogged = blocker;
                    Debug.Log($"[PathWalker] {body.m_name}: look-ahead: {blocker} {at:0.0} m ahead on the way; steering {turned:0}° wide");
                }
            }
            // A soft margin (0.2.206, Fire: "always hugging walls and edges"): a wall closer than SoftMargin on one side than the other eases
            // the heading toward the middle, so a corridor is walked down its middle and a wall is passed with room. Not in the last 2 m.
            if (goalFlat > 2f && flat.sqrMagnitude > 0.0001f)
            {
                Vector3 dirN = flat.normalized;
                Vector3 right = Vector3.Cross(Vector3.up, dirN);
                float l = SideRoom(body, -right), r = SideRoom(body, right);
                if (Mathf.Min(l, r) < SoftMargin && Mathf.Abs(l - r) > 0.15f)
                {
                    float lean = Mathf.Clamp((r - l) / SoftMargin, -1f, 1f) * SoftLean;
                    Vector3 eased = (dirN + right * lean).normalized;
                    flat = eased * flat.magnitude;
                    want = eased * want.magnitude;
                }
            }
            lookDir = flat.normalized;
            Dither(body, lookDir);
            Vector3 face = new Vector3(facing.x, 0f, facing.z);
            if (face.sqrMagnitude > 0.0001f && Vector3.Angle(face, lookDir) > MoveMinAngle)
            {
                State = "turning";
                return WalkState.Turning;
            }
            moveDir = want.normalized;
            return WalkState.Moving;
        }

        // ---- Keep-out round a dungeon's exit teleport ----

        /// <summary>Inside a dungeon, the walker keeps this far (m) from the interior's exit teleport unless the goal is that exit.</summary>
        public const float ExitKeepOut = 2f;
        private static Teleport[] s_teleports;
        private static float s_teleportsUntil;
        private float _exitLoggedAt = -999f;

        // Heading flat (unit or not) toward a point within ExitKeepOut of an interior exit teleport (the goal not being it): the tangent
        // round the disc on the goal's side instead. Indoors only: outside, a teleport is an entrance a walk may want.
        private bool KeepOffExit(Character body, Vector3 flat, out Vector3 around)
        {
            around = flat;
            Vector3 at = body.transform.position;
            if (at.y <= IndoorsHeight || flat.sqrMagnitude < 0.0001f) return false;
            if (s_teleports == null || Time.time >= s_teleportsUntil)
            {
                s_teleports = Object.FindObjectsByType<Teleport>(FindObjectsSortMode.None);
                s_teleportsUntil = Time.time + 5f;
            }
            Vector3 dir = flat.normalized;
            foreach (Teleport t in s_teleports)
            {
                if (t == null) continue;
                Vector3 tp = t.transform.position;
                if (Mathf.Abs(tp.y - at.y) > 4f) continue;
                if (FlatDistance(_goalNow, tp) <= ExitKeepOut + 1f) continue;   // the goal is the exit: go in
                // Where the next metre of this heading ends, and whether it is inside (or deeper into) the disc.
                Vector3 next = at + dir * 1f;
                float now = FlatDistance(at, tp), then = FlatDistance(next, tp);
                if (then > ExitKeepOut || then >= now) continue;
                Vector3 away = at - tp;
                away.y = 0f;
                if (away.sqrMagnitude < 0.0001f) away = -dir;
                away.Normalize();
                Vector3 tangent = Vector3.Cross(Vector3.up, away);
                Vector3 toGoal = _goalNow - at;
                toGoal.y = 0f;
                if (Vector3.Dot(tangent, toGoal) < 0f) tangent = -tangent;
                around = (tangent + (now < ExitKeepOut ? away : Vector3.zero)).normalized;
                if (Time.time - _exitLoggedAt > 5f)
                {
                    _exitLoggedAt = Time.time;
                    Debug.Log($"[PathWalker] {body.m_name}: keep-out: the exit teleport {Utils.GetPrefabName(t.gameObject)} at {now:0.0} m is in the way; going round it");
                }
                return true;
            }
            return false;
        }

        // The path to goal, asked at most once a second and kept while the goal stays put; false with no path.
        // Fire: "closed doors should not cut their route". The one door rule (DoorRule), for the bot like the companions.
        private bool FindDoor(Character body, Vector3 at, Vector3 goal, float radius = 4f)
        {
            Door = DoorRule.DoorOnPath(at, goal, radius, body as Humanoid);
            return Door != null;
        }

        /// <summary>A closed door of the room the body is in may be this far from it (m) when the goal is behind a wall.</summary>
        public const float EnclosureDoorRadius = 8f;
        /// <summary>A walk stalled within this far (m) of a closed door it may open (or with the door on its straight line) opens it.</summary>
        public const float StalledDoorMetres = 3f;
        private const float WayOutSeconds = 3f;
        private float _wayOutNext;

        // The goal is close (feel range) but a wall stands between (R77 [visual]: the bot inside the base's bed hut, the drill point
        // 2.7 m away just outside its wall; "route none" and 90 s against the wall; R76 the same hut): the room's closed door first
        // (Door set), else round the wall or a terrain route (Detour set). False: nothing better than feeling forward.
        private bool WayOut(Character body, Vector3 at, Vector3 goal, float goalFlat)
        {
            if (Time.time < _wayOutNext || InSight(at, goal)) return false;
            _wayOutNext = Time.time + WayOutSeconds;
            Vector3 from = at + Vector3.up * 1f, to = goal + Vector3.up * 1f;
            Physics.Linecast(from, to, out RaycastHit wall, SolidMask, QueryTriggerInteraction.Ignore);
            string what = wall.collider != null ? wall.collider.name : "a wall";
            if (FindDoor(body, at, goal, EnclosureDoorRadius))
            {
                Debug.Log($"[PathWalker] {body.m_name}: way out: {what} between us and the goal {goalFlat:0.0} m away; through {Utils.GetPrefabName(Door.gameObject)} " +
                          $"({FlatDistance(at, Door.transform.position):0.0} m)");
                return true;
            }
            if (PlanRooms(body, at, goal, $"{what} between us and the goal {goalFlat:0.0} m away")) return true;   // indoors: through the doorways (0.2.207)
            if (wall.collider != null && PlanRound(body, at, goal, wall.collider)) return true;
            return PlanTerrain(body, at, goal, $"{what} between us and the goal {goalFlat:0.0} m away (no door within {EnclosureDoorRadius:0} m)");
        }

        private WalkState ToDoor(Character body, Vector3 at, out Vector3 moveDir, out Vector3 lookDir)
        {
            Vector3 to = Door.transform.position - at;
            to.y = 0f;
            float toDoor = to.magnitude;
            // Come at it from the front, not along the wall (FrontApproach): the point before it first, then the door.
            Vector3 front = FrontApproach(Door.transform, at, DoorFrontStandoff);
            Vector3 toFront = front - at;
            toFront.y = 0f;
            bool viaFront = toDoor > DoorFrontStandoff + 0.5f && toFront.magnitude > 0.6f
                            && Vector3.Angle(to, Door.transform.forward) > 45f && Vector3.Angle(to, -Door.transform.forward) > 45f;
            Vector3 aim = viaFront ? toFront : to;
            lookDir = aim.sqrMagnitude > 0.0001f ? aim.normalized : Vector3.zero;
            moveDir = viaFront || toDoor > DoorRule.InteractDistance ? lookDir : Vector3.zero;
            // At the door: open it here (the door rule), close it behind (R76: the bot stood 74 s at its own hut's door after
            // respawning, "door: wood_door on the path (1.6 m)", and nothing opened it). The rule's toggle guard stops a second
            // opener (the driver) from shutting it again.
            if (!viaFront && toDoor <= DoorRule.InteractDistance && body is Humanoid opener)
            {
                if (DoorRule.OpenFor(opener, Door))
                {
                    DoorRule.CloseBehind(opener, Door);
                    // Opened: straight through it (0.2.209, R80 doorway, both bodies: the re-path right after the opening came back
                    // "14 corners" round the west wall end, 17.9 m vs straight 8.0, the tile still baked with the door shut and the
                    // clearance push off its jambs). Through the doorway on waypoints, then back to the navmesh on the far side.
                    Transform t = Door.transform;
                    Vector3 across = t.forward;
                    across.y = 0f;
                    across = across.sqrMagnitude > 0.0001f ? across.normalized : Vector3.forward;
                    if (Vector3.Dot(at - t.position, across) > 0f) across = -across;   // from our side to the other
                    _detour.Clear();
                    _detour.Add(t.position - across * 0.6f);
                    _detour.Add(t.position + across * DoorThroughMetres);
                    _detourTotal = _detour.Count;
                    _detourWhat = $"the doorway of {Utils.GetPrefabName(Door.gameObject)}";
                    _path.Clear();
                    Debug.Log($"[PathWalker] {body.m_name}: door: opened {Utils.GetPrefabName(Door.gameObject)} on the path (Core door rule); going through it " +
                              $"to ({_detour[1].x:0.0}, {_detour[1].z:0.0}), closing it behind");
                }
                else if (Time.time >= _doorWhyAt)
                {
                    _doorWhyAt = Time.time + 5f;
                    Debug.Log($"[PathWalker] {body.m_name}: door: {Utils.GetPrefabName(Door.gameObject)} won't open ({DoorRule.WhyNot(Door, opener)})");
                }
            }
            State = $"door: {Utils.GetPrefabName(Door.gameObject)} on the path ({toDoor:0.0} m{(viaFront ? ", to its front first" : "")})";
            return WalkState.Door;
        }

        /// <summary>How far in front of a door the walker lines up before going to it (m).</summary>
        public const float DoorFrontStandoff = 2f;
        /// <summary>An opened door is walked through to this far past it (m) before the navmesh takes over again.</summary>
        public const float DoorThroughMetres = 2f;

        private float _doorWhyAt;

        /// <summary>A slide side against one blocker holds until the body has gone this far along it (m).</summary>
        public const float SlideSideHoldMetres = 2.5f;
        private Collider _slideFace;
        private Vector3 _slideNormal;
        private Vector3 _sideFrom;

        private bool Ask(Vector3 at, Vector3 goal, float radius, out bool pending)
        {
            pending = false;
            float now = Time.time;
            float since = now - _lastAsk;
            bool goalMoved = float.IsNaN(_lastGoal.x) || Vector3.Distance(goal, _lastGoal) >= GoalMovedMeters;
            if (since < RepathSeconds || (!goalMoved && since < KeepPathSeconds)) return _lastFound;
            var finder = Pathfinding.instance;
            if (finder == null) { pending = true; return false; }
            _lastAsk = now;
            _lastGoal = goal;
            Repaths++;
            // Re-path hysteresis (R75 outdoors: 4 heading flips in 8 s): with the goal where it was and the current route still
            // being walked, a new route only replaces it when it is clearly shorter.
            float oldRemaining = _path.Count > 0 && _lastFound ? PathRemaining(at, _path) : float.MaxValue;
            bool found = finder.GetPath(at, goal, s_fresh, Pathfinding.AgentType.Humanoid);
            if (found && !goalMoved && oldRemaining < float.MaxValue && Time.time >= _staleUntil && PathRemaining(at, s_fresh) > oldRemaining * KeepRouteShare)
                return _lastFound;   // keep the route being walked
            _lastFound = found;
            _path.Clear();
            if (_lastFound)
            {
                _path.AddRange(s_fresh);
                KeepCornersClear(radius);
                _checkRidge = true;
                _overshootFrom = new Vector3(float.NaN, 0f, 0f);
            }
            _pathCorners = _path.Count;
            return _lastFound;
        }

        /// <summary>A fresh route replaces the one being walked only when it is shorter than this share of the rest of it.</summary>
        public const float KeepRouteShare = 0.85f;
        private static readonly List<Vector3> s_fresh = new List<Vector3>();

        private static float PathRemaining(Vector3 at, List<Vector3> path)
        {
            float length = 0f;
            Vector3 last = at;
            foreach (Vector3 c in path) { length += FlatDistance(last, c); last = c; }
            return length;
        }

        // A corner the body has already gone past along the path (it lies behind the body relative to the next leg): passed, even
        // if the body never came within the corner radius (turn-then-move otherwise orbits it: turn 180°, step back, overshoot).
        private bool PassedCorner(Vector3 at)
        {
            if (_path.Count < 2) return false;
            Vector3 corner = _path[0], next = _path[1];
            if (FlatDistance(at, corner) > PassedCornerRange) return false;
            Vector3 leg = next - corner, toCorner = corner - at;
            leg.y = 0f;
            toCorner.y = 0f;
            if (Vector3.Dot(toCorner, leg) >= 0f) return false;
            // Only passed when the next corner is in sight (0.2.202, R77 nav_course u_trap: inside a 4 m U of walls the corners
            // round the wall's end lay within 3 m THROUGH the wall and "behind" the next leg, so they were dropped and the walker
            // aimed at corner 4 straight through the back wall for 30 s).
            return InSight(at, next);
        }

        // ---- The next-corner rule (0.2.202, Fire R77: "make it fucking walk AROUND corners") ----

        /// <summary>A capsule this much wider than the body (m) must pass clear to the next corner before the walker turns to it.</summary>
        public const float NextCornerSlack = 0.2f;
        /// <summary>Walked this far past a corner without the next leg clearing: take the corner anyway (a probe that never clears).</summary>
        public const float OvershootMetres = 3f;
        private float _radius = 0.5f;
        private Vector3 _approach;
        private Vector3 _overshootFrom = new Vector3(float.NaN, 0f, 0f);
        private static int s_capsuleMask;
        private static int CapsuleMask => s_capsuleMask != 0 ? s_capsuleMask : (s_capsuleMask = LayerMask.GetMask("static_solid", "piece", "terrain"));

        // The current corner is done when the way on from it is clear: within reach of it (or past it along the path) AND a body-wide
        // capsule from here to the next corner (or the goal, for the last one) hits nothing. The last corner only needs reaching.
        private bool CornerDone(Character body, Vector3 at, float cornerReach)
        {
            Vector3 corner = _path[0];
            bool near = FlatDistance(at, corner) < cornerReach;
            if (_path.Count == 1) return near;
            // Indoors, corner to corner as vanilla monsters walk the baked navmesh: reached = done, no overshoot (0.2.209).
            if (at.y > IndoorsHeight) return near || PassedCorner(at);
            bool passed = near || PassedCorner(at) || !float.IsNaN(_overshootFrom.x);   // an overshoot in progress counts as passed
            if (!passed) return false;
            if (CapsuleClear(at, _path[1], _radius + NextCornerSlack)) return true;
            // Overshooting into something solid straight on: the corner can't be walked past, so take it now (0.2.205).
            // Taking a leg the capsule says is blocked walked both R80 bodies into the woodwall ("… taking it", then "slid along
            // woodwall … toward corner 2", Fire: "cutting the fucking corner"): a blocked leg is re-planned, not taken (0.2.209).
            if (!float.IsNaN(_overshootFrom.x) && Blocked(body, _approach))
            {
                _blockedLeg = $"corner {_pathCorners - _path.Count + 1}/{_pathCorners}: blocked straight on while walking past it, the next leg still hits {LegBlocker(at, _path[1])}";
                return false;
            }
            if (!float.IsNaN(_overshootFrom.x) && FlatDistance(at, _overshootFrom) > OvershootMetres)
            {
                _blockedLeg = $"corner {_pathCorners - _path.Count + 1}/{_pathCorners}: the next leg still hits {LegBlocker(at, _path[1])} {OvershootMetres:0} m past it";
                return false;
            }
            return false;
        }

        /// <summary>A detour waypoint this much (m) or more above the body isn't reached until the body is up there too.</summary>
        public const float WaypointLevelMetres = 0.6f;
        /// <summary>Within this far (m) of a waypoint up a step, the walker asks for the jump.</summary>
        public const float StepJumpReach = 2.5f;

        /// <summary>A navmesh route longer than this share of the straight way plus these metres loses to a door on the straight way.</summary>
        public const float DoorDetourShare = 1.5f, DoorDetourMetres = 4f;

        /// <summary>After a stale navmesh leg, fresh routes replace the walked one whatever their length for this long (s): a tile re-bakes 5 s after it's poked.</summary>
        public const float StaleRouteSeconds = 8f;
        private float _staleUntil = -1f;

        private string _blockedLeg;
        private Door _legDoor;
        private Vector3 _approachCorner;
        private readonly List<Vector3> _legAvoid = new List<Vector3>(1);

        // What a body-wide capsule from a to b hits first ("nothing" when clear).
        private string LegBlocker(Vector3 a, Vector3 b)
        {
            float r = _radius + NextCornerSlack;
            Vector3 low = a + Vector3.up * (r + 0.35f), high = a + Vector3.up * Mathf.Max(r + 0.4f, 1.6f);
            Vector3 dir = (b + Vector3.up * (r + 0.35f)) - low;
            float distance = dir.magnitude;
            if (distance < 0.05f) return "nothing";
            if (!FirstNonGround(low, high, r, dir / distance, Mathf.Max(0.05f, distance - r), out RaycastHit hit))
                return "nothing";
            // A door the body may open is the way through, not a wall to plan round (0.2.212, R83 doorway, both bodies: "the next leg hits
            // door … and no approach" -> "route: terrain … 25 m vs straight 8 m" round the wall line, "door passes 0").
            Door door = hit.collider.GetComponentInParent<Door>();
            if (door != null && DoorRule.IsPassable(door)) _legDoor = door;
            _legAvoid.Clear();
            _legAvoid.Add(hit.point);   // the terrain re-plan keeps off it (PlanTerrain)
            return $"{hit.collider.name} at ({hit.point.x:0.0}, {hit.point.z:0.0})";
        }

        // A fresh navmesh path whose legs (from here, corner to corner) a capsule of the body's own radius can't walk: built pieces
        // or rock the navmesh doesn't know about yet. Terrain is left out (a navmesh leg over a hump brushes it).
        private bool StaleLeg(Vector3 at, out string why)
        {
            why = null;
            if (at.y > IndoorsHeight) return false;   // a dungeon's navmesh is baked with its rooms: nothing is built after it
            Vector3 from = at;
            for (int i = 0; i < _path.Count; i++)
            {
                Vector3 to = _path[i];
                if (WallAcross(from, to, _radius, out RaycastHit hit))
                {
                    why = $"navmesh leg {i + 1}/{_path.Count} crosses {hit.collider.name} at ({hit.point.x:0.0}, {hit.point.z:0.0}) (a tile older than the piece)";
                    _legAvoid.Clear();
                    _legAvoid.Add(hit.point);
                    return true;
                }
                from = to;
            }
            return false;
        }
        private static int s_pieceMask;
        private static readonly RaycastHit[] s_wallHits = new RaycastHit[16];

        /// <summary>
        /// A wall a body of <paramref name="radius"/> can't get past on the straight way a→b (knee to head height): something built
        /// or rocky, not a character, not a door, not ground it stands on (a face tilted up) and not a step it can jump (its top
        /// within a jumpable lip of the lower end). The nearest such hit in <paramref name="hit"/>.
        /// </summary>
        public static bool WallAcross(Vector3 a, Vector3 b, float radius, out RaycastHit hit)
        {
            hit = default;
            if (s_pieceMask == 0) s_pieceMask = LayerMask.GetMask("static_solid", "piece");
            Vector3 low = a + Vector3.up * (radius + 0.35f), high = a + Vector3.up * Mathf.Max(radius + 0.4f, 1.6f);
            Vector3 dir = (b + Vector3.up * (radius + 0.35f)) - low;
            float distance = dir.magnitude;
            if (distance < 0.05f) return false;
            int n = Physics.CapsuleCastNonAlloc(low, high, radius, dir / distance, s_wallHits, Mathf.Max(0.05f, distance - radius), s_pieceMask, QueryTriggerInteraction.Ignore);   // a radius short of the end (0.2.212)
            float floor = Mathf.Min(a.y, b.y);
            bool found = false;
            for (int k = 0; k < n; k++)
            {
                RaycastHit h = s_wallHits[k];
                Collider c = h.collider;
                if (c == null || h.distance <= 0f) continue;   // 0: overlapping at the start (the ground under us, a wall we lean on)
                if (c.attachedRigidbody != null && c.attachedRigidbody.GetComponent<Character>() != null) continue;
                if (c.GetComponentInParent<Door>() != null) continue;
                if (h.normal.y > 0.7f || Grazes(h, dir)) continue;
                if (c.bounds.max.y - floor <= Jumping.MaxLip) continue;
                if (!found || h.distance < hit.distance) { hit = h; found = true; }
            }
            return found;
        }

        /// <summary>A capsule of <paramref name="radius"/> (knee to head height) moved from <paramref name="from"/> to <paramref name="to"/> hits nothing built, rocky or terrain.</summary>
        public static bool CapsuleClear(Vector3 from, Vector3 to, float radius)
        {
            Vector3 low = from + Vector3.up * (radius + 0.35f), high = from + Vector3.up * Mathf.Max(radius + 0.4f, 1.6f);
            Vector3 dir = (to + Vector3.up * (radius + 0.35f)) - low;
            float distance = dir.magnitude;
            if (distance < 0.05f) return true;
            // Stopping a radius short of the end (0.2.212, R83 corridor: the goal sat in a 1.8 m corridor's closed end and the sweep to it
            // touched the end walls, so the last leg read "blocked" and the walker went on past the mouth, "went round outside").
            return !FirstNonGround(low, high, radius, dir / distance, Mathf.Max(0.05f, distance - radius), out _);
        }

        private static readonly RaycastHit[] s_legHits = new RaycastHit[16];

        // The nearest hit of a capsule sweep that isn't ground to stand on (0.2.214, R85 corridor, self body: "the next leg hits
        // VoxelChunk_-27_1_-12 at (-838.0, -358.7)", a bump of voxel ground read as the wall that blocked the leg).
        // A wall running along the way (its face turned across the travel) is only brushed by the sweep's side, not in the way
        // (0.2.217, R87 corridor: a 1.7 m passage and a ~1.6 m wide sweep: "the next leg hits woodwall(Clone) at (-837.3, -352.7)" at the
        // mouth, the leg re-planned round the outside every time).
        private static bool Grazes(RaycastHit h, Vector3 travel)
        {
            Vector3 n = new Vector3(h.normal.x, 0f, h.normal.z), t = new Vector3(travel.x, 0f, travel.z);
            if (n.sqrMagnitude < 0.0001f || t.sqrMagnitude < 0.0001f) return false;
            return Mathf.Abs(Vector3.Dot(n.normalized, t.normalized)) < 0.35f;
        }

        private static bool FirstNonGround(Vector3 low, Vector3 high, float radius, Vector3 dir, float distance, out RaycastHit first)
        {
            first = default;
            int n = Physics.CapsuleCastNonAlloc(low, high, radius, dir, s_legHits, distance, CapsuleMask, QueryTriggerInteraction.Ignore);
            bool found = false;
            for (int k = 0; k < n; k++)
            {
                RaycastHit h = s_legHits[k];
                if (h.collider == null || h.distance <= 0f || h.normal.y > 0.7f || Grazes(h, dir)) continue;
                if (!found || h.distance < first.distance) { first = h; found = true; }
            }
            return found;
        }

        // A clear line at knee and chest height between two points (not ground, not characters).
        // No wall between here and the goal at waist height; a hit on the goal's own object (within 0.8 m of it: a chest, a stand,
        // a pickable on a table) doesn't count.
        private static bool NoWallBefore(Vector3 from, Vector3 goal)
        {
            if (!Physics.Linecast(from + Vector3.up * 1f, goal + Vector3.up * 1f, out RaycastHit hit, SolidMask, QueryTriggerInteraction.Ignore)) return true;
            if (hit.collider.attachedRigidbody != null && hit.collider.attachedRigidbody.GetComponent<Character>() != null) return true;
            return FlatDistance(hit.point, goal) < 0.8f;
        }

        private static bool InSight(Vector3 from, Vector3 to)
        {
            foreach (float up in SightHeights)
            {
                if (Physics.Linecast(from + Vector3.up * up, to + Vector3.up * up, out RaycastHit hit, SolidMask, QueryTriggerInteraction.Ignore)
                    && !(hit.collider.attachedRigidbody != null && hit.collider.attachedRigidbody.GetComponent<Character>() != null))
                    return false;
            }
            return true;
        }

        private static readonly float[] SightHeights = { 0.6f, 1.4f };

        private const float PassedCornerRange = 3f;

        // No path, goal within FeelRange: straight at it while nothing is in the way, else ±20° off the forward to the more open
        // side; no progress for a while -> back out at a slant for a few seconds.
        private Vector3 Feel(Character body, Vector3 goal)
        {
            Vector3 at = body.transform.position;
            Vector3 want = goal - at;
            want.y = 0f;
            want.Normalize();
            float now = Time.time;
            if (now < _backOutUntil)
            {
                State = "stuck: backing out";
                return Quaternion.Euler(0f, _backOutAngle, 0f) * -want;
            }
            _stuckClock += Time.deltaTime;
            if (_stuckClock > StuckCheckSeconds)
            {
                bool stuck = (at - _stuckFrom).sqrMagnitude < StuckMeters * StuckMeters;
                _stuckClock = 0f;
                _stuckFrom = at;
                if (stuck)
                {
                    _backOutUntil = now + BackOutSeconds;
                    _backOutAngle = Random.Range(-SideAngle, SideAngle);
                    State = "stuck: backing out";
                    return Quaternion.Euler(0f, _backOutAngle, 0f) * -want;
                }
            }

            float radius = body.GetRadius();
            float reach = radius + 1f;
            Vector3 centre = body.GetCenterPoint();
            Vector3 side = Vector3.Cross(Vector3.up, want) * Mathf.Max(0.05f, radius - 0.1f);
            if (Clear(centre, want, reach) && Clear(centre - side, want, reach) && Clear(centre + side, want, reach))
            {
                State = "feeling: straight at the goal";
                return want;
            }
            // A jumpable step in the way is jumped, not turned from (0.2.202, R77 jump_steps).
            JumpAdvice rise = Jumping.Check(body, want, false);
            if (!rise.Wall && (rise.Jump || (rise.LipHeight > Perception.StepHeight && rise.LipHeight <= Jumping.MaxLip && rise.LipDistance <= Jumping.LipReach + 0.3f)))
            {
                State = $"feeling: step {rise.LipHeight:0.0} m -> jump";
                WantsJump = true;
                return want;
            }
            Vector3 forward = body.transform.forward;
            forward.y = 0f;
            forward.Normalize();
            Vector3 offset = Vector3.Cross(Vector3.up, forward) * radius * 0.75f;
            float look = reach * 1.5f;
            float left = Free(centre - offset, forward, look), right = Free(centre + offset, forward, look);
            if (left >= look && right >= look)
            {
                State = "feeling: ahead is open";
                return forward;
            }
            bool goLeft = left > right;
            State = $"feeling: blocked, {SideAngle:0} deg {(goLeft ? "left" : "right")}";
            return Quaternion.Euler(0f, goLeft ? -SideAngle : SideAngle, 0f) * forward;
        }

        // ---- Corners (Fire, R73: "not recognizing collision objects when trying to round corners"; the bot stalled 20 s on the
        // mausoleum's stone corner at corner 4/9). The navmesh agent is thinner than a player body, so its corners hug walls. ----

        /// <summary>Extra room (m) kept between a corner and anything solid, past the body's radius.</summary>
        public const float CornerMargin = 0.5f;
        /// <summary>A corner at an obstacle edge is pushed out up to this far (m) where the room is there (wide arcs round corners).</summary>
        public const float WideCornerMetres = 3f;
        /// <summary>How far ahead (m) the walker looks for something solid on its heading, and how much wider than the body it looks.</summary>
        public const float LookAheadMetres = 3f, LookAheadMargin = 0.3f;
        private static readonly float[] LookTurns = { 15f, 30f, 45f, 60f };
        private float _lookLoggedAt = -999f;
        private string _lookLogged;

        // Something solid within LookAheadMetres along dir (body radius + LookAheadMargin wide) that isn't a door the body may open or a lip it
        // can jump: the least turn (goal side first) whose way is clear, out; false when the way is clear or nothing clears it.
        private static bool LookAhead(Character body, Vector3 dir, Vector3 goal, float reach, out Vector3 clearDir, out string blocker, out float at, out float turned)
        {
            clearDir = dir;
            blocker = null;
            at = 0f;
            turned = 0f;
            float radius = Mathf.Max(0.2f, body.GetRadius()) + LookAheadMargin;
            Vector3 centre = body.GetCenterPoint();
            if (reach < 0.5f || !Physics.SphereCast(centre, radius, dir, out RaycastHit hit, reach, SolidMask, QueryTriggerInteraction.Ignore)) return false;
            if (hit.normal.y >= 0.5f) return false;   // ground, not a wall
            if (hit.collider.attachedRigidbody != null && hit.collider.attachedRigidbody.GetComponent<Character>() != null) return false;
            Door door = hit.collider.GetComponentInParent<Door>();
            if (door != null && DoorRule.IsPassable(door, body as Humanoid)) return false;   // the door rule opens it
            foreach (Door d in DoorRule.Doors())   // a doorway's frame is the way through, not an obstacle
                if (d != null && FlatDistance(d.transform.position, hit.point) < 2f) return false;
            JumpAdvice rise = Jumping.Check(body, dir, false);
            if (!rise.Wall && (rise.Jump || rise.LipHeight <= Jumping.MaxLip && rise.LipHeight > Perception.StepHeight)) return false;   // a step: jump it
            blocker = hit.collider.name;
            at = hit.distance;
            Vector3 toGoal = goal - body.transform.position;
            toGoal.y = 0f;
            float goalSide = Vector3.Cross(dir, toGoal).y >= 0f ? -1f : 1f;
            foreach (float t in LookTurns)
            {
                foreach (float side in new[] { goalSide, -goalSide })
                {
                    Vector3 d = Quaternion.Euler(0f, t * side, 0f) * dir;
                    if (Physics.SphereCast(centre, radius, d, out RaycastHit h2, reach, SolidMask, QueryTriggerInteraction.Ignore)
                        && h2.normal.y < 0.5f && !(h2.collider.attachedRigidbody != null && h2.collider.attachedRigidbody.GetComponent<Character>() != null)) continue;
                    clearDir = d;
                    turned = t;
                    return true;
                }
            }
            return false;   // nothing clears it here: the slide and the re-plan take over at contact
        }

        /// <summary>A wall nearer than this (m) to one side eases the heading away from it (at most <see cref="SoftLean"/> of a right angle's tangent).</summary>
        public const float SoftMargin = 1.2f, SoftLean = 0.35f;

        // Free room (m, up to SoftMargin) from the body's chest toward dir: the first solid (not a character) a thin cast meets.
        private static float SideRoom(Character body, Vector3 dir)
        {
            if (Physics.SphereCast(body.GetCenterPoint(), 0.2f, dir, out RaycastHit hit, SoftMargin, SolidMask, QueryTriggerInteraction.Ignore)
                && !(hit.collider.attachedRigidbody != null && hit.collider.attachedRigidbody.GetComponent<Character>() != null))
                return hit.distance;
            return SoftMargin;
        }

        /// <summary>How far (m) a corner at an obstacle edge is kept off it where the room is there (Fire: walk AROUND, not along the edge).</summary>
        public const float WantedCornerClearance = 2f;

        // Flat distance from a point at chest height to the nearest point of a collider (0 without one).
        private static float WallDistance(Vector3 point, Collider wall)
        {
            if (wall == null || !wall) return 0f;
            Vector3 probe = point + Vector3.up * 1f;
            bool concave = wall is MeshCollider mesh && !mesh.convex;
            Vector3 closest = concave ? wall.bounds.ClosestPoint(probe) : wall.ClosestPoint(probe);
            closest.y = probe.y;
            return Vector3.Distance(probe, closest);
        }

        private static readonly Collider[] s_near = new Collider[16];
        private static int s_solidMask;
        private static int SolidMask => s_solidMask != 0 ? s_solidMask
            : (s_solidMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "vehicle"));

        private string _cornerNote;
        private string _slideLogged;

        // ---- Making up its mind (R75 Crypt4, Fire: "it turns one way and then turns the other" in an open hallway) ----

        /// <summary>How long a chosen slide side is kept unless that side is blocked (s).</summary>
        public const float SlideSideHoldSeconds = 3f;
        /// <summary>More than this many heading reversals (&gt; 120°) within <see cref="DitherWindow"/> s is a dither.</summary>
        public const int DitherReversals = 3;
        public const float DitherWindow = 5f, CalmSeconds = 5f;

        private int _slideSide;
        private float _slideSideUntil;
        private Door _slideDoor;
        private float _calmUntil;
        private Vector3 _lastLook;
        private readonly List<float> _reversals = new List<float>();

        // Counts heading reversals; on a dither, commits to the path as it is (no slides) for CalmSeconds; slopes keep their held contour.
        private void Dither(Character body, Vector3 look)
        {
            float now = Time.time;
            if (_lastLook.sqrMagnitude > 0.0001f && Vector3.Angle(_lastLook, look) > 120f) _reversals.Add(now);
            _lastLook = look;
            _reversals.RemoveAll(t => now - t > DitherWindow);
            if (_reversals.Count <= DitherReversals || now < _calmUntil) return;
            _calmUntil = now + CalmSeconds;
            Slope.HoldSide(CalmSeconds);
            Debug.Log($"[PathWalker] {body.m_name}: dither: {_reversals.Count} turn-arounds in {DitherWindow:0} s; committing to the path " +
                      $"(no slides for {CalmSeconds:0} s; a slope contour holds its side)");
            _reversals.Clear();
        }

        // Something solid within 1 m along dir (flat) in front of the body.
        private static bool Blocked(Character body, Vector3 dir)
        {
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return true;
            return Physics.SphereCast(body.GetCenterPoint(), Mathf.Max(0.2f, body.GetRadius() * 0.8f), dir.normalized, out RaycastHit hit, 1f,
                SolidMask, QueryTriggerInteraction.Ignore) && hit.normal.y < 0.5f;
        }

        // Each corner but the last is pushed out to radius + CornerMargin from the nearest solid (not terrain, not characters).
        private void KeepCornersClear(float radius)
        {
            string note = ClearCorners(_path, radius);
            if (note != null) _cornerNote = note;
        }

        /// <summary>
        /// The walkable point <paramref name="standoff"/> m in FRONT of a doorway (a door, a dungeon entrance, a house opening), on
        /// the side <paramref name="from"/> is on, snapped to the navmesh; walk there first, then straight through (Fire, R74: "if
        /// it's trying to enter an area, it should approach the doorway from the front" - the bot kept walking into the
        /// mausoleum's corner). The doorway's forward axis is its facing (Door, Teleport); either side counts as the front.
        /// </summary>
        public static Vector3 FrontApproach(Transform doorway, Vector3 from, float standoff = 3f)
        {
            if (doorway == null) return from;
            Vector3 forward = doorway.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 centre = doorway.position;
            Vector3 a = centre + forward * standoff, b = centre - forward * standoff;
            Vector3 point = (a - from).sqrMagnitude <= (b - from).sqrMagnitude ? a : b;
            if (Pathfinding.instance != null
                && Pathfinding.instance.FindValidPoint(out Vector3 stand, point, 1.5f, Pathfinding.AgentType.Humanoid))
                point = stand;
            return point;
        }

        /// <summary>
        /// Moves each point of <paramref name="path"/> but the last out to <see cref="WantedCornerClearance"/> from the nearest solid where
        /// there is room (at least <paramref name="radius"/> + <see cref="CornerMargin"/>; not terrain, not characters). The one rule for the bot's
        /// walker and the companions' BaseAI paths. Returns the "corner: kept …" note, or null when nothing moved.
        /// </summary>
        public static string ClearCorners(List<Vector3> path, float radius)
        {
            if (path == null) return null;
            // Clearance is a property of the whole path (0.2.206, Fire R79: "its just always hugging walls and edges … it gives itself no
            // room to go around shit"): every point but the last within WantedCornerClearance of a solid goes out along the way away from
            // it, as far as WantedCornerClearance where there is room (standable: a body-sized sphere free and on the navmesh; both legs
            // free of solid). Where there isn't, at least radius + CornerMargin (the old rule), else it stays.
            float minimum = radius + CornerMargin;
            float search = Mathf.Max(WantedCornerClearance, minimum);
            int moved = 0;
            string tightWhat = null;
            float tightest = -1f, tightRoom = 0f;
            string passageNote = null;
            // Indoors the navmesh is baked with the rooms, so it is followed as vanilla monsters follow it (0.2.209, Fire R80: "the
            // vanilla ai monsters navigate crypts no problem"): centred in a passage, never pushed off a wall ("kept 0.0 m off
            // stonewall (1) … room 0.2; 34 of 35 point(s) moved" in a crypt corridor).
            bool indoors = path.Count > 0 && path[0].y > IndoorsHeight;
            for (int i = 0; i < path.Count - 1; i++)
            {
                Vector3 corner = path[i];
                Vector3 probe = corner + Vector3.up * 1f;
                // In a passage narrower than the wanted clearance both sides (a corridor, a doorway, a gap between pieces): the middle
                // of it, never out of it (0.2.209, R80 corridor 1.8 m, Fire: "they chose to go around instead of down the hallway").
                if (Passage(path, i, radius, out Vector3 middle, out float width))
                {
                    if (FlatDistance(middle, corner) > 0.05f) { path[i] = middle; moved++; }
                    if (passageNote == null) passageNote = $"clearance: passage {width:0.0} m, centred (wanted {WantedCornerClearance:0.0})";
                    continue;
                }
                if (indoors) continue;
                int n = Physics.OverlapSphereNonAlloc(probe, search, s_near, SolidMask, QueryTriggerInteraction.Ignore);
                Vector3 away = Vector3.zero;
                float nearest = float.MaxValue;
                Collider pushedFrom = null;
                for (int k = 0; k < n; k++)
                {
                    Collider c = s_near[k];
                    if (c == null || (c.attachedRigidbody != null && c.attachedRigidbody.GetComponent<Character>() != null)) continue;
                    // A step the body can get up (its top within a jumpable lip of this floor) is the way on, not a wall to keep off
                    // (0.2.209, R80 jump_steps: "kept 1.0 m off stone_floor_2x2" pushed the path away from the stair it had to climb);
                    // an open or passable door is the way through (the doorway leg's "10 of 14 point(s) moved" off its jambs).
                    if (c.bounds.max.y - corner.y <= Jumping.MaxLip) continue;
                    Door door = c.GetComponentInParent<Door>();
                    if (door != null && (!DoorRule.IsClosed(door) || DoorRule.IsPassable(door))) continue;
                    // A concave mesh's ClosestPoint returns the query point: use its bounds instead.
                    bool concave = c is MeshCollider mesh && !mesh.convex;
                    Vector3 closest = concave ? c.bounds.ClosestPoint(probe) : c.ClosestPoint(probe);
                    Vector3 off = probe - closest;
                    off.y = 0f;
                    float d = off.magnitude;
                    if (d < 0.01f || d >= search) continue;
                    away += off / d * (search - d);
                    if (d < nearest) { nearest = d; pushedFrom = c; }
                }
                if (away.sqrMagnitude < 0.0001f || pushedFrom == null) continue;
                Vector3 outward = away.normalized;
                Vector3 best = corner;
                bool found = false;
                float room = 0f;
                for (float d = 0.25f; d <= WideCornerMetres + 0.01f; d += 0.25f)
                {
                    Vector3 cand = corner + outward * d;
                    if (Physics.CheckSphere(cand + Vector3.up * 1f, radius + 0.2f, SolidMask, QueryTriggerInteraction.Ignore)) break;   // out of room
                    room = d;
                    if (Pathfinding.instance != null)
                    {
                        if (!Pathfinding.instance.FindValidPoint(out Vector3 stand, cand, 1f, Pathfinding.AgentType.Humanoid)) continue;
                        cand = stand;
                    }
                    if (i > 0 && !InSight(path[i - 1], cand)) continue;
                    if (!InSight(cand, path[i + 1])) continue;
                    best = cand;
                    found = true;
                    if (WallDistance(best, pushedFrom) >= WantedCornerClearance) break;
                }
                if (!found)
                {
                    if (nearest >= minimum) continue;   // no room to widen, and already clear of the old minimum: leave it
                    best = corner + outward * (minimum - nearest);   // the old rule: at least radius + CornerMargin off
                    // Only where the pushed point stands free (0.2.217): 0.2.214 skipped every point with "room 0" (R85 workstation hut),
                    // but room is 0 whenever the first step still touches the wall being pushed from, so the U-trap's corners stayed on
                    // its walls (R87 u_trap FAIL on both bodies, "clear of walls" logged, passed in R85). A push into another solid is
                    // what the hut needed to avoid.
                    if (Physics.CheckSphere(best + Vector3.up * 1f, radius * 0.6f, SolidMask, QueryTriggerInteraction.Ignore)) continue;
                }
                float kept = WallDistance(best, pushedFrom);
                if (tightest < 0f || kept < tightest)
                {
                    tightest = kept;
                    tightWhat = pushedFrom.name;
                    tightRoom = WallDistance(corner + outward * room, pushedFrom);
                }
                path[i] = best;
                moved++;
            }
            LastClearance = tightest;
            string note = moved > 0 && tightWhat != null
                ? $"corner: kept {tightest:0.0} m off {tightWhat} (wanted {WantedCornerClearance:0.0}, room {tightRoom:0.0}; {moved} of {path.Count} point(s) moved)"
                : null;
            if (passageNote != null) note = note != null ? note + "; " + passageNote : passageNote;
            return note;
        }

        // Point i of the path sits in a passage when solids stand on both sides of the way through it (across the path's direction
        // there, knee to chest height) closer together than the wanted clearance each side plus the body: middle = halfway between.
        private static bool Passage(List<Vector3> path, int i, float radius, out Vector3 middle, out float width)
        {
            middle = path[i];
            width = 0f;
            Vector3 dir = path[i + 1] - (i > 0 ? path[i - 1] : path[i]);
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return false;
            dir.Normalize();
            Vector3 side = new Vector3(-dir.z, 0f, dir.x);
            float reach = WantedCornerClearance + radius;
            float limit = 2f * WantedCornerClearance + 2f * radius;
            foreach (float up in PassageHeights)
            {
                Vector3 o = path[i] + Vector3.up * up;
                if (!PassageSide(o, side, reach, out float right) || !PassageSide(o, -side, reach, out float left)) return false;
                float w = right + left;
                if (w >= limit || w < 2f * radius) return false;
                if (width == 0f || w < width) { width = w; middle = path[i] + side * ((right - left) * 0.5f); }
            }
            return width > 0f;
        }

        private static readonly float[] PassageHeights = { 0.9f, 1.5f };

        // The nearest solid along dir from o within reach (not a character, not a passable door, not a step).
        private static bool PassageSide(Vector3 o, Vector3 dir, float reach, out float distance)
        {
            distance = reach;
            int n = Physics.RaycastNonAlloc(o, dir, s_passageHits, reach, SolidMask, QueryTriggerInteraction.Ignore);
            bool hitAny = false;
            for (int k = 0; k < n; k++)
            {
                Collider c = s_passageHits[k].collider;
                if (c == null || (c.attachedRigidbody != null && c.attachedRigidbody.GetComponent<Character>() != null)) continue;
                Door door = c.GetComponentInParent<Door>();
                if (door != null && (!DoorRule.IsClosed(door) || DoorRule.IsPassable(door))) continue;
                if (s_passageHits[k].distance < distance) { distance = s_passageHits[k].distance; hitAny = true; }
            }
            return hitAny;
        }
        private static readonly RaycastHit[] s_passageHits = new RaycastHit[8];

        /// <summary>The least clearance the last <see cref="ClearCorners"/> kept (m), or -1 when nothing was near a solid.</summary>
        public static float LastClearance { get; private set; } = -1f;

        // On a path, something solid right ahead (a building corner the body clips): walk along its face toward the corner instead
        // of into it; head-on, the side nearer the corner after this one.
        /// <summary>A corner contact is first stepped off sideways this far (m).</summary>
        public const float StrafeMetres = 0.8f;
        private const float StrafeMaxSeconds = 1f, StrafeCooldownSeconds = 2f, StrafeFailedCooldownSeconds = 10f;
        private const float RoundRetrySeconds = 3f;
        private float _roundFailedUntil;
        private Vector3 _roundFailedGoal;
        private float _strafeUntil = -1f, _strafeNext;
        private Vector3 _strafeDir, _strafeFrom;
        private string _strafeWhat;

        // Only one shoulder touches (a corner or a wall end, not a wall head-on): a sidestep away from it, facing the way on. True
        // while stepping (moveDir sideways, lookDir ahead).
        private bool Strafe(Character body, Vector3 at, Vector3 want, out Vector3 moveDir, out Vector3 lookDir)
        {
            moveDir = Vector3.zero;
            lookDir = Vector3.zero;
            Vector3 ahead = new Vector3(want.x, 0f, want.z);
            if (ahead.sqrMagnitude < 0.0001f) return false;
            ahead.Normalize();
            if (_strafeUntil > 0f)
            {
                bool done = Time.time >= _strafeUntil || FlatDistance(at, _strafeFrom) >= StrafeMetres || Blocked(body, _strafeDir);
                if (!done)
                {
                    moveDir = _strafeDir;
                    lookDir = ahead;
                    State = $"unstick: strafing off {_strafeWhat}";
                    return true;
                }
                float moved = FlatDistance(at, _strafeFrom);
                Debug.Log($"[PathWalker] {body.m_name}: unstick: strafed {moved:0.0} m {(Vector3.Dot(_strafeDir, Vector3.Cross(Vector3.up, ahead)) > 0f ? "right" : "left")} off {_strafeWhat}, then on");
                // A sidestep that went nowhere isn't tried again soon (0.2.211, R82: "strafed 0.0 m" x13).
                if (moved < 0.2f) _strafeNext = Time.time + StrafeFailedCooldownSeconds;
                _strafeUntil = -1f;
                return false;
            }
            if (Time.time < _strafeNext) return false;
            float radius = body.GetRadius();
            Vector3 right = Vector3.Cross(Vector3.up, ahead);
            Vector3 centre = body.GetCenterPoint();
            float reach = radius + 0.35f;
            bool hitRight = Physics.Raycast(centre + right * radius * 0.9f, ahead, out RaycastHit r, reach, SolidMask, QueryTriggerInteraction.Ignore) && !IsStep(r.collider, at);
            bool hitLeft = Physics.Raycast(centre - right * radius * 0.9f, ahead, out RaycastHit l, reach, SolidMask, QueryTriggerInteraction.Ignore) && !IsStep(l.collider, at);
            bool hitMiddle = Physics.Raycast(centre, ahead, reach, SolidMask, QueryTriggerInteraction.Ignore);
            if (hitRight == hitLeft || hitMiddle) return false;   // clear, or a wall across the way: the slide / re-plan rules
            Vector3 side = hitRight ? -right : right;
            if (Blocked(body, side)) return false;
            _strafeDir = side;
            _strafeFrom = at;
            _strafeUntil = Time.time + StrafeMaxSeconds;
            _strafeNext = Time.time + StrafeCooldownSeconds;
            Collider c = hitRight ? r.collider : l.collider;
            _strafeWhat = c != null ? $"{c.name} corner" : "a corner";
            moveDir = side;
            lookDir = ahead;
            State = $"unstick: strafing off {_strafeWhat}";
            return true;
        }

        private static bool IsStep(Collider c, Vector3 at) => c != null && c.bounds.max.y - at.y <= Jumping.MaxLip;

        private Vector3 SlideAlong(Character body, Vector3 want)
        {
            Vector3 flat = new Vector3(want.x, 0f, want.z);
            float distance = flat.magnitude;
            if (distance < 0.05f) return want;
            // Calm (after a dither): follow the path corners as they are for a while.
            if (Time.time < _calmUntil) return want;
            // A step the body can jump is not a wall to slide along (0.2.202, R77 nav_course jump_steps: "slid along
            // stone_floor_2x2 (left) … (right)" at a 0.5 m riser for 9 s, never walking at it for the jump to fire).
            JumpAdvice rise = Jumping.Check(body, flat / distance, false);
            if (!rise.Wall && (rise.Jump || (rise.LipHeight > Perception.StepHeight && rise.LipHeight <= Jumping.MaxLip && rise.LipDistance <= Jumping.LipReach + 0.3f)))
            {
                string jumpNote = $"rise: step {rise.LipHeight:0.0} m at {rise.LipDistance:0.0} m -> jump (not a wall)";
                if (jumpNote != _slideLogged)
                {
                    _slideLogged = jumpNote;
                    Debug.Log($"[PathWalker] {body.m_name}: {jumpNote}");
                }
                State = jumpNote;
                WantsJump = true;
                return want;
            }
            Vector3? next = _path.Count > 1 ? _path[1] : (Vector3?)null;
            if (!Slide(body, flat / distance, Mathf.Min(0.9f, distance), next, out Vector3 along, out string what, out string side, out Collider face))
            {
                _slideLogged = null;
                _slideSince = -1f;
                return want;
            }
            // A door in the way is opened (the door rule), not slid along (R75 Crypt4: "slid along door (left) … (right) …").
            Door door = face != null ? face.GetComponentInParent<Door>() : null;
            if (door != null && DoorRule.IsPassable(door, body as Humanoid))
            {
                _slideDoor = door;
                return want;
            }
            // Keep the side a while: re-choosing it every frame flipped left/right in a corridor (R75).
            // R76 Crypt4 (an open hallway, right -> left -> left against the same stonewall): the side chosen against a blocker holds
            // while it's the same blocker and the body hasn't gone SlideSideHoldMetres along it, not only for a few seconds.
            int sign = side == "right" ? -1 : 1;
            Vector3 here = body.transform.position;
            // "The same blocker" includes the next piece of the same wall (0.2.202, R77 u_trap: a wall of 2 m woodwall pieces flipped
            // right/left at every seam for 30 s): a face whose normal is within 30° of the held one counts as the same wall.
            Vector3 faceNormal = _slideNormal;
            if (face != null && Physics.SphereCast(body.GetCenterPoint(), Mathf.Max(0.2f, body.GetRadius() * 0.9f), flat / distance, out RaycastHit faceHit,
                    Mathf.Min(0.9f, distance) + 0.1f, SolidMask, QueryTriggerInteraction.Ignore))
                faceNormal = new Vector3(faceHit.normal.x, 0f, faceHit.normal.z).normalized;
            bool sameWall = face != null && _slideFace != null && (face == _slideFace || Vector3.Dot(faceNormal, _slideNormal) > 0.87f);
            bool sameFace = sameWall && FlatDistance(here, _sideFrom) < SlideSideHoldMetres + (face != _slideFace ? 2f : 0f);
            if ((Time.time < _slideSideUntil || sameFace) && _slideSide != 0 && sign != _slideSide && !Blocked(body, -along))
            {
                along = -along;
                sign = _slideSide;
                side = sign < 0 ? "right" : "left";
            }
            else if (sign != _slideSide || (Time.time >= _slideSideUntil && !sameFace))
            {
                _slideSide = sign;
                _slideSideUntil = Time.time + SlideSideHoldSeconds;
                _slideFace = face;
                _slideNormal = faceNormal;
                _sideFrom = here;
            }
            // A slide that gets nowhere (R74/R75: "slid along Facade (right) toward corner 5" for minutes): go round the whole
            // thing's footprint instead of re-trying the same face.
            Vector3 at = body.transform.position;
            if (_slideSince < 0f) { _slideSince = Time.time; _slideFrom = at; }
            else if (Time.time - _slideSince > SlideGiveUpSeconds)
            {
                bool stuck = FlatDistance(at, _slideFrom) < 1f;
                _slideSince = -1f;
                if (stuck && PlanRound(body, at, _goalNow, face)) return _detour[0] - at;
            }
            string note = $"slid along {what} ({side}) toward corner {_pathCorners - _path.Count + 1}";
            if (note != _slideLogged)
            {
                _slideLogged = note;
                Debug.Log($"[PathWalker] {body.m_name}: {note}");
            }
            State = note;
            return along * distance;
        }

        /// <summary>
        /// Something solid (not ground, not a character) within <paramref name="reach"/> m along <paramref name="dir"/> (flat, unit)
        /// in front of <paramref name="body"/>: <paramref name="along"/> = the way along its face (head-on: the side toward
        /// <paramref name="next"/>, else the right). The one slide rule for the bot's walker and the companions' BaseAI moves.
        /// </summary>
        public static bool Slide(Character body, Vector3 dir, float reach, Vector3? next, out Vector3 along, out string what, out string side) =>
            Slide(body, dir, reach, next, out along, out what, out side, out _);

        /// <summary><see cref="Slide(Character, Vector3, float, Vector3?, out Vector3, out string, out string)"/> plus the face's collider.</summary>
        public static bool Slide(Character body, Vector3 dir, float reach, Vector3? next, out Vector3 along, out string what, out string side,
            out Collider face)
        {
            along = dir;
            what = null;
            side = null;
            face = null;
            if (body == null || reach <= 0.01f) return false;
            float radius = Mathf.Max(0.2f, body.GetRadius() * 0.9f);
            Vector3 centre = body.GetCenterPoint();
            if (!Physics.SphereCast(centre, radius, dir, out RaycastHit hit, reach, SolidMask, QueryTriggerInteraction.Ignore)
                || hit.normal.y >= 0.5f
                || (hit.collider.attachedRigidbody != null && hit.collider.attachedRigidbody.GetComponent<Character>() != null))
                return false;
            Vector3 normal = new Vector3(hit.normal.x, 0f, hit.normal.z);
            if (normal.sqrMagnitude < 0.0001f) return false;
            normal.Normalize();
            along = dir - normal * Vector3.Dot(dir, normal);
            if (along.sqrMagnitude < 0.04f)
            {
                Vector3 tangent = Vector3.Cross(Vector3.up, normal);
                Vector3 toward = next.HasValue ? next.Value - body.transform.position : dir;
                along = Vector3.Dot(tangent, toward) >= 0f ? tangent : -tangent;
            }
            along.Normalize();
            side = Vector3.Cross(dir, along).y < 0f ? "right" : "left";
            what = hit.collider.name;
            face = hit.collider;
            return true;
        }

        // ---- Round a building's footprint (Fire, R74/R75: "make the bot go around the corner") ----

        /// <summary>Seconds a slide along one face may make no progress (under 1 m) before the walker goes round the whole thing.</summary>
        public const float SlideGiveUpSeconds = 3f;
        /// <summary>A footprint larger than this (m, either side) is only its hit collider's own bounds (not a whole location).</summary>
        private const float MaxFootprint = 80f, MaxFootprintIndoors = 12f;
        // Dungeons are built far above the world (y ~5000).
        private const float IndoorsHeight = 3000f;

        private readonly List<Vector3> _detour = new List<Vector3>();
        private int _detourTotal;
        private string _detourWhat;
        private Vector3 _goalNow;
        private float _slideSince = -1f;
        private Vector3 _slideFrom;
        private static readonly Collider[] s_footprint = new Collider[64];

        /// <summary>The corners this walk is going round (empty when none), read only.</summary>
        public IReadOnlyList<Vector3> Detour => _detour;

        /// <summary>
        /// Plans a way round the solid in the straight way from <paramref name="at"/> to <paramref name="goal"/> (or round
        /// <paramref name="seed"/>): the footprint = the flat bounds of the solid colliders of that thing (its root, near the seed),
        /// grown by the body's radius + <see cref="CornerMargin"/> + 0.5 m; its corners are walked the shorter way round until the
        /// goal is in the clear. True with <see cref="Detour"/> filled.
        /// </summary>
        public bool PlanRound(Character body, Vector3 at, Vector3 goal, Collider seed)
        {
            _detour.Clear();
            if (body == null) return false;
            if (at.y > IndoorsHeight) return false;   // indoors: the navmesh and the room graph only (0.2.209, vanilla-style)
            // The same failed round isn't re-tried every frame (0.2.211, R82 u_trap: "round woodwall: a corner … is off the navmesh" x40 in a row).
            if (Time.time < _roundFailedUntil && FlatDistance(goal, _roundFailedGoal) < GoalMovedMeters) return false;
            if (seed == null)
            {
                Vector3 from = at + Vector3.up, to = goal + Vector3.up;
                if (!Physics.Linecast(from, to, out RaycastHit hit, SolidMask, QueryTriggerInteraction.Ignore)) return false;
                if (hit.collider.attachedRigidbody != null && hit.collider.attachedRigidbody.GetComponent<Character>() != null) return false;
                seed = hit.collider;
            }
            Bounds box = seed.bounds;
            Transform root = seed.transform.root;
            // Inside a dungeon (Valheim builds them far above the world) every wall shares one root, the whole dungeon: going
            // round "the thing" there means round the one piece that was hit (a pillar, a wall end), never a cluster of rooms
            // (R75 Crypt4: "stuck on stonewall", 12 wall stalls a minute on the way to a burial chamber).
            bool indoors = at.y > IndoorsHeight;
            float cap = indoors ? MaxFootprintIndoors : MaxFootprint;
            if (box.size.x > cap || box.size.z > cap)
            {
                Debug.Log($"[PathWalker] {body.m_name}: round {Utils.GetPrefabName(root.gameObject)}: the hit piece alone is {box.size.x:0} x {box.size.z:0} m (cap {cap:0} m); not going round it");
                return false;
            }
            if (!indoors)
            {
                int n = Physics.OverlapSphereNonAlloc(box.center, Mathf.Max(box.extents.magnitude, 20f), s_footprint, SolidMask, QueryTriggerInteraction.Ignore);
                // Built pieces each have their own root: a wall of pieces (R77 u_trap: a U of woodwalls) grows by the pieces that
                // touch it (0.2.202), a few passes so a chain of them joins up.
                bool seedPiece = seed.GetComponentInParent<Piece>() != null;
                for (int pass = 0; pass < (seedPiece ? 4 : 1); pass++)
                {
                    bool grew = false;
                    for (int i = 0; i < n; i++)
                    {
                        Collider c = s_footprint[i];
                        if (c == null || c == seed) continue;
                        bool member = c.transform.root == root;
                        // Standing pieces only (walls, posts, not floors: a floor would join up the whole base).
                        if (!member && seedPiece && c.bounds.size.y > 1f && c.GetComponentInParent<Piece>() != null)
                        {
                            Bounds touch = box;
                            touch.Expand(0.4f);
                            member = touch.Intersects(c.bounds);
                        }
                        if (!member) continue;
                        Bounds grown = box;
                        grown.Encapsulate(c.bounds);
                        if (grown.size.x > cap || grown.size.z > cap || grown == box) continue;
                        box = grown;
                        grew = true;
                    }
                    if (!grew) break;
                }
            }
            float clear = body.GetRadius() + CornerMargin + 0.5f;
            float minX = box.min.x - clear, maxX = box.max.x + clear, minZ = box.min.z - clear, maxZ = box.max.z + clear;
            // Corners in order round the box.
            var corners = new[]
            {
                new Vector3(minX, at.y, minZ), new Vector3(minX, at.y, maxZ), new Vector3(maxX, at.y, maxZ), new Vector3(maxX, at.y, minZ)
            };
            List<Vector3> best = null;
            float bestCost = float.MaxValue;
            string bestWay = null;
            foreach (int step in new[] { 1, -1 })
            {
                int start = 0;
                float near = float.MaxValue;
                for (int i = 0; i < 4; i++)
                {
                    float d = FlatDistance(at, corners[i]);
                    if (d < near) { near = d; start = i; }
                }
                var route = new List<Vector3>();
                float cost = 0f;
                Vector3 last = at;
                for (int k = 0; k < 4; k++)
                {
                    Vector3 c = corners[((start + step * k) % 4 + 4) % 4];
                    route.Add(c);
                    cost += FlatDistance(last, c);
                    last = c;
                    if (!CrossesBox(c, goal, minX + clear * 0.6f, maxX - clear * 0.6f, minZ + clear * 0.6f, maxZ - clear * 0.6f)) break;
                }
                cost += FlatDistance(last, goal);
                if (cost < bestCost) { bestCost = cost; best = route; bestWay = step > 0 ? "one way" : "the other way"; }
            }
            if (best == null || best.Count == 0) return false;
            // Every corner must be standable (on the navmesh within 2 m) and every leg clear at chest height; else no detour
            // (a corner inside rock or another room would only trade one wall for the next).
            Vector3 prev = at;
            foreach (Vector3 c in best)
            {
                Vector3 p = c;
                if (Pathfinding.instance != null)
                {
                    if (!Pathfinding.instance.FindValidPoint(out Vector3 stand, c, 2f, Pathfinding.AgentType.Humanoid))
                    {
                        _detour.Clear();
                        _roundFailedUntil = Time.time + RoundRetrySeconds;
                        _roundFailedGoal = goal;
                        Debug.Log($"[PathWalker] {body.m_name}: round {Utils.GetPrefabName(root.gameObject)}: a corner at ({c.x:0}, {c.z:0}) is off the navmesh; no clear way round");
                        return false;
                    }
                    p = stand;
                }
                if (Physics.Linecast(prev + Vector3.up, p + Vector3.up, out RaycastHit blocked, SolidMask, QueryTriggerInteraction.Ignore)
                    && blocked.collider != seed)
                {
                    _detour.Clear();
                    Debug.Log($"[PathWalker] {body.m_name}: round {Utils.GetPrefabName(root.gameObject)}: the way to a corner is blocked by {blocked.collider.name}; no clear way round");
                    return false;
                }
                _detour.Add(p);
                prev = p;
            }
            _detourTotal = _detour.Count;
            _detourWhat = Utils.GetPrefabName(root.gameObject);
            Debug.Log($"[PathWalker] {body.m_name}: round {_detourWhat}: no way through; going round its footprint " +
                      $"({box.size.x:0} x {box.size.z:0} m) {bestWay}, {_detour.Count} corner(s), {bestCost:0} m");
            return true;
        }

        // ---- Terrain route (0.2.201): round ridges, banks and pockets the navmesh and the slope guard can't see past ----

        /// <summary>A slope contour followed this long (s) without clearing asks the terrain planner for a way round.</summary>
        public const float ContourPlanSeconds = 15f;
        /// <summary>At most one terrain plan per walker this often (s); a failed plan for the same goal waits longer.</summary>
        public const float TerrainPlanSeconds = 5f, TerrainFailSeconds = 15f;
        // A leg of the navmesh route climbing steeper than the body's limit by this much (degrees) is a ridge.
        private const float RidgeMargin = 3f;

        /// <summary>A walk not nearer its goal by this much (m) in this long (s) asks the terrain planner.</summary>
        public const float NoProgressMetres = 0.5f, NoProgressSeconds = 6f;
        private Vector3 _bestGoal = new Vector3(float.NaN, 0f, 0f);
        private float _bestFlat, _bestAt;

        private bool _checkRidge;
        private float _navLogAt;
        private float _contourSince = -1f;
        private float _terrainNext;
        private Vector3 _terrainFailedGoal = new Vector3(float.NaN, 0f, 0f);
        private float _terrainFailedUntil;
        private static readonly List<Vector3> s_terrainRoute = new List<Vector3>();

        /// <summary>
        /// A terrain route from <paramref name="at"/> to <paramref name="goal"/> (TerrainPlanner) as this walk's detour. True with
        /// <see cref="Detour"/> filled. <paramref name="reason"/> is what made the walk ask (for the log).
        /// </summary>
        public bool PlanTerrain(Character body, Vector3 at, Vector3 goal, string reason, bool force = false)
        {
            // force (0.2.209): a blocked leg re-plans at once; the TerrainPlanSeconds spacing would have it walk the blocked leg meanwhile.
            if (body == null || (!force && Time.time < _terrainNext)) return false;
            if (!float.IsNaN(_terrainFailedGoal.x) && Time.time < _terrainFailedUntil && FlatDistance(goal, _terrainFailedGoal) < GoalMovedMeters) return false;
            _terrainNext = Time.time + TerrainPlanSeconds;

            // A retry must change something (0.2.206, Fire R79: "turns around to retry and makes the same fucking route"): a second plan
            // for the same goal marks where the last route stalled and weighs walls heavier each time; a route near-equal to the last
            // failed one is re-planned round its other side.
            if (float.IsNaN(_retryGoal.x) || FlatDistance(goal, _retryGoal) > 3f)
            {
                _retryGoal = goal;
                _retries = 0;
                _stalls.Clear();
                _lastRoute.Clear();
            }
            else if (_lastRoute.Count > 0)
            {
                _retries++;
                _stalls.Add(at);
            }
            float weight = TerrainPlanner.DefaultClearanceWeight * Mathf.Pow(2f, Mathf.Min(_retries, 3));
            string retryNote = _retries > 0
                ? $"retry {_retries}: the last route stalled at ({at.x:0}, {at.z:0}); avoiding {_stalls.Count} stall point(s) ({RetryAvoidMetres:0.#} m), wall weight x{weight:0}"
                : null;
            // The wall a blocked leg hit is kept off like a stall point (0.2.209).
            if (_legAvoid.Count > 0)
            {
                _stalls.AddRange(_legAvoid);
                _legAvoid.Clear();
            }
            bool planned = TerrainPlanner.Plan(body, at, goal, s_terrainRoute, out string why, _stalls.Count > 0 ? _stalls : null, RetryAvoidMetres, weight);
            if (planned && _retries > 0 && NearEqual(s_terrainRoute, _lastRoute))
            {
                // Same way round as the one that failed: its middle is off limits too, so the plan takes the other side.
                foreach (Vector3 p in _lastRoute) _stalls.Add(p);
                planned = TerrainPlanner.Plan(body, at, goal, s_terrainRoute, out why, _stalls, RetryAvoidMetres, weight);
                retryNote += "; the same route came back, so trying the other side";
            }
            if (retryNote != null) Debug.Log($"[PathWalker] {body.m_name}: {retryNote}");
            if (planned && s_terrainRoute.Count > 0)
            {
                _lastRoute.Clear();
                _lastRoute.AddRange(s_terrainRoute);
            }
            // A route through a wall the grid couldn't see (0.2.209, R80 Crypt4: "route: terrain (the navmesh path ends 8.0 m short) via
            // (-2369, -2624) -> (-2370, -2626): 8 m vs straight 8 m" straight through a 4.9 m crypt wall, then 7 s against it: cells
            // inside a thick mesh wall read as open floor) is no route: the walk reports it instead of jogging into the wall.
            if (planned && s_terrainRoute.Count > 0)
            {
                Vector3 legFrom = at;
                foreach (Vector3 p in s_terrainRoute)
                {
                    if (WallAcross(legFrom, p, body.GetRadius() * 0.8f, out RaycastHit wall))
                    {
                        planned = false;
                        why = $"its way crosses {wall.collider.name} at ({wall.point.x:0.0}, {wall.point.z:0.0}), a wall the grid didn't see; no walkable way";
                        break;
                    }
                    legFrom = p;
                }
            }
            if (!planned || s_terrainRoute.Count == 0)
            {
                _terrainFailedGoal = goal;
                _terrainFailedUntil = Time.time + TerrainFailSeconds;
                Debug.Log($"[PathWalker] {body.m_name}: route: terrain ({reason}): no way round to ({goal.x:0}, {goal.z:0}): {why}");
                return false;
            }
            _detour.Clear();
            _detour.AddRange(s_terrainRoute);
            _detourTotal = _detour.Count;
            _detourWhat = "the terrain";
            _path.Clear();
            Slope.Reset();
            Debug.Log($"[PathWalker] {body.m_name}: route: terrain ({reason}) via {Waypoints(_detour)}: {why}");
            return true;
        }

        // ---- Indoors: through the rooms' doorways (0.2.207, Fire R79 Crypt4: "it tries the shortest route rather than the actual open
        // hallways") ----

        private float _roomsNext;
        private float _islandLoggedAt = -999f;
        private static readonly List<Vector3> s_roomRoute = new List<Vector3>();

        /// <summary>
        /// Inside a dungeon, a route through its rooms' doorways (<see cref="RoomGraph"/>) as this walk's detour; true with
        /// <see cref="Detour"/> filled. Outdoors, or when the rooms don't join, false (the caller tries its next way).
        /// </summary>
        public bool PlanRooms(Character body, Vector3 at, Vector3 goal, string reason)
        {
            if (body == null || at.y <= IndoorsHeight || Time.time < _roomsNext) return false;
            _roomsNext = Time.time + 3f;
            if (!RoomGraph.Route(at, goal, s_roomRoute, out string why))
            {
                Debug.Log($"[PathWalker] {body.m_name}: route: rooms ({reason}): {why}");
                return false;
            }
            _detour.Clear();
            _detour.AddRange(s_roomRoute);
            _detourTotal = _detour.Count;
            _detourWhat = "the rooms";
            _path.Clear();
            Debug.Log($"[PathWalker] {body.m_name}: route: rooms ({reason}) {why}");
            return true;
        }

        // Why the navmesh inside ends short (R80 evidence): where the path ends, which room each end is in, and the doors near the end.
        private void NavmeshIsland(Character body, Vector3 at, Vector3 goal)
        {
            if (at.y <= IndoorsHeight || Time.time - _islandLoggedAt < 10f) return;
            _islandLoggedAt = Time.time;
            Vector3 end = s_fresh.Count > 0 ? s_fresh[s_fresh.Count - 1] : at;
            var doors = new List<string>();
            foreach (Door d in DoorRule.Doors())
            {
                if (d == null || FlatDistance(d.transform.position, end) > 6f || Mathf.Abs(d.transform.position.y - end.y) > 4f) continue;
                doors.Add($"{Utils.GetPrefabName(d.gameObject)} {(DoorRule.IsClosed(d) ? "closed" : "open")} {FlatDistance(d.transform.position, end):0.0} m");
            }
            Debug.Log($"[PathWalker] {body.m_name}: navmesh: the path ends at ({end.x:0}, {end.z:0}) in {RoomGraph.Name(RoomGraph.RoomAt(end))}; " +
                      $"target in {RoomGraph.Name(RoomGraph.RoomAt(goal))}; doors within 6 m of the end: {(doors.Count > 0 ? string.Join(", ", doors) : "none")}");
        }

        /// <summary>Round each point where an earlier route for the same goal stalled, the planner avoids this far (m).</summary>
        public const float RetryAvoidMetres = 2.5f;
        private Vector3 _retryGoal = new Vector3(float.NaN, 0f, 0f);
        private int _retries;
        private readonly List<Vector3> _stalls = new List<Vector3>();
        private readonly List<Vector3> _lastRoute = new List<Vector3>();

        // Every waypoint of a lies within 1.5 m of b's polyline and the lengths are within a fifth: the same way round.
        private static bool NearEqual(List<Vector3> a, List<Vector3> b)
        {
            if (a == null || b == null || a.Count == 0 || b.Count == 0) return false;
            foreach (Vector3 p in a)
            {
                float best = float.MaxValue;
                for (int i = 0; i < b.Count; i++)
                {
                    Vector3 s = i == 0 ? b[0] : b[i - 1], e = b[i];
                    Vector3 se = e - s; se.y = 0f;
                    Vector3 sp = p - s; sp.y = 0f;
                    float t = se.sqrMagnitude > 0.0001f ? Mathf.Clamp01(Vector3.Dot(sp, se) / se.sqrMagnitude) : 0f;
                    best = Mathf.Min(best, FlatDistance(p, s + se * t));
                }
                if (best > 1.5f) return false;
            }
            float la = 0f, lb = 0f;
            for (int i = 1; i < a.Count; i++) la += FlatDistance(a[i - 1], a[i]);
            for (int i = 1; i < b.Count; i++) lb += FlatDistance(b[i - 1], b[i]);
            return Mathf.Abs(la - lb) <= 0.2f * Mathf.Max(la, lb, 1f);
        }

        // The first leg of the route (body to corner, corner to corner) that climbs steeper than this body can, as text; false with none.
        private bool RidgeOnPath(Character body, Vector3 at, out string ridge)
        {
            ridge = null;
            if (body == null || at.y > IndoorsHeight || _path.Count == 0) return false;
            float limit = SlopeGuide.Limit(body);
            if (limit >= 89f) return false;
            Vector3 from = at;
            foreach (Vector3 corner in _path)
            {
                float climb = TerrainPlanner.SteepestClimb(from, corner, out Vector3 foot);
                if (climb > limit + RidgeMargin)
                {
                    ridge = $"ridge between ({from.x:0}, {from.z:0}) and ({corner.x:0}, {corner.z:0}) ({climb:0}° up from ({foot.x:0}, {foot.z:0}); limit {limit:0}°)";
                    return true;
                }
                from = corner;
            }
            return false;
        }

        private static string Waypoints(List<Vector3> points)
        {
            var text = new System.Text.StringBuilder();
            int shown = Mathf.Min(points.Count, 6);
            for (int i = 0; i < shown; i++)
            {
                if (i > 0) text.Append(" -> ");
                text.Append($"({points[i].x:0}, {points[i].z:0})");
            }
            if (points.Count > shown) text.Append($" … ({points.Count} in all)");
            return text.ToString();
        }

        // Does the flat segment a-b pass through the box's inside (Liang-Barsky)?
        private static bool CrossesBox(Vector3 a, Vector3 b, float minX, float maxX, float minZ, float maxZ)
        {
            if (minX >= maxX || minZ >= maxZ) return false;
            float t0 = 0f, t1 = 1f, dx = b.x - a.x, dz = b.z - a.z;
            float[] p = { -dx, dx, -dz, dz };
            float[] q = { a.x - minX, maxX - a.x, a.z - minZ, maxZ - a.z };
            for (int i = 0; i < 4; i++)
            {
                if (Mathf.Approximately(p[i], 0f)) { if (q[i] < 0f) return false; continue; }
                float r = q[i] / p[i];
                if (p[i] < 0f) { if (r > t1) return false; if (r > t0) t0 = r; }
                else { if (r < t0) return false; if (r < t1) t1 = r; }
            }
            return t0 < t1;
        }

        private static bool Clear(Vector3 from, Vector3 dir, float distance) => Free(from, dir, distance) >= distance;

        private static float Free(Vector3 from, Vector3 dir, float distance) =>
            Physics.SphereCast(from, 0.1f, dir, out RaycastHit hit, distance, BlockMask, QueryTriggerInteraction.Ignore) ? hit.distance : distance;

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x, z = a.z - b.z;
            return Mathf.Sqrt(x * x + z * z);
        }
    }
}
