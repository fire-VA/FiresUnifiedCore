using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Keeps a walking body off ground too steep for it (Fire, 2026-09-29: the bot "keeps wandering into steep hills", on paths and
    /// on the loot walk to a kill). Vanilla slides a body whose ground tilts past its slide angle (Character.GetSlideAngle: 38° for
    /// players, 90° for monsters, 45° with a rider), whatever way it walks, so the ground AHEAD is checked before stepping on it:
    /// the tilt of the surface <see cref="Near"/> and <see cref="Far"/> m along the wanted heading. Too steep: the nearest heading
    /// round it (±30° steps up to ±<see cref="MaxTurn"/>°) whose ground is walkable, keeping to one side for a while so it follows
    /// the contour instead of flicking left and right. Nothing walkable round it: boxed in (the caller treats it as a partial way).
    /// One instance per body (a PathWalker owns one; a caller walking straight at a goal can use the walker's). No allocations.
    /// </summary>
    public sealed class SlopeGuide
    {
        public const float Near = 1.2f, Far = 2.4f, MaxTurn = 120f, TurnStep = 30f;
        private const float CastAbove = 6f, KeepSideSeconds = 2f, CheckSeconds = 0.2f;

        /// <summary>Degrees a PLAYER body may climb past vanilla's 38° (FDT's slope allowance for the bot). Monsters are not limited.</summary>
        public static float PlayerAllowance;

        private static int s_mask;
        // Only ground a body can slide on: terrain and rock. Never building pieces, doors or dungeon walls (R75 Crypt4: the
        // hallway's walls and door read as 47-80° "slopes" and the bot turned back and forth in an open corridor).
        private static int Mask => s_mask != 0 ? s_mask : (s_mask = LayerMask.GetMask("terrain", "static_solid"));
        private static int s_terrainLayer = -1;
        private static int TerrainLayer => s_terrainLayer >= 0 ? s_terrainLayer : (s_terrainLayer = LayerMask.NameToLayer("terrain"));
        // A face steeper than this is a wall (Jumping's business), not a slope to go round.
        private const float WallDegrees = 70f;
        // Dungeons are built far above the world (y ~5000): no terrain slopes in there, only floors, walls and ceilings.
        private const float IndoorsHeight = 3000f;

        private float _nextCheck;
        private Vector3 _lastWant;
        private Vector3 _lastResult;
        private bool _lastBoxed;
        private float _side;
        private float _sideUntil;

        /// <summary>
        /// "" while the way ahead is walkable, else "slope: 44° too steep ahead (limit 38°): contour sidestep L 60°" or
        /// "slope: … boxed in (nothing walkable within ±120°)".
        /// </summary>
        public string State { get; private set; } = "";

        /// <summary>Times it turned a heading (per instance, for the drill evidence).</summary>
        public int Sidesteps { get; private set; }

        /// <summary>The steepest ground this body walks without sliding (degrees).</summary>
        public static float Limit(Character body) =>
            body == null ? 90f : body.IsPlayer() ? 38f + PlayerAllowance : (body.GetBaseAI() != null && body.GetBaseAI().HaveRider() ? 45f : 90f);

        /// <summary>
        /// The heading to walk instead of <paramref name="want"/> (flat), or <paramref name="want"/> itself when its ground is walkable.
        /// <paramref name="boxed"/>: nothing walkable round it (the result is then <paramref name="want"/>).
        /// </summary>
        public Vector3 Steer(Character body, Vector3 want, out bool boxed)
        {
            boxed = false;
            Vector3 flat = new Vector3(want.x, 0f, want.z);
            if (body == null || flat.sqrMagnitude < 0.0001f) return want;
            float limit = Limit(body);
            if (limit >= 89f || body.transform.position.y > IndoorsHeight) { State = ""; return want; }
            flat.Normalize();
            float now = Time.time;
            if (now < _nextCheck && Vector3.Angle(flat, _lastWant) < 10f)
            {
                boxed = _lastBoxed;
                return _lastResult;
            }
            _nextCheck = now + CheckSeconds;
            _lastWant = flat;

            Vector3 feet = body.transform.position;
            float ahead = Steepest(feet, flat);
            // A contour is held until the body has gone ContourCommitMetres along it AND the straight way is walkable (R75: a 90°
            // sidestep alternating with a re-path back up the slope flip-flopped the heading 5 times in 8 s).
            // 0.2.203 (R77 obstacle_field z: "contour: holding R until clear" dithered 5 s beside a stump): the straight way walkable
            // for ClearChecksToRelease checks in a row ends the hold early, unless the dither guard froze the side.
            _clearChecks = ahead <= limit ? _clearChecks + 1 : 0;
            bool holding = _side != 0f && (FlatDistance(feet, _contourFrom) < ContourCommitMetres || now < _freezeUntil)
                           && !(_clearChecks >= ClearChecksToRelease && now >= _freezeUntil);
            if (ahead <= limit && !holding)
            {
                _side = 0f;
                State = "";
                return Keep(want, false);
            }
            if (ahead <= limit)
            {
                Vector3 along = Quaternion.Euler(0f, _contourTurn * _side, 0f) * flat;
                if (Steepest(feet, along) <= limit)
                {
                    State = $"contour: holding {(_side < 0f ? "L" : "R")} until clear ({ContourCommitMetres - FlatDistance(feet, _contourFrom):0.0} m)";
                    return Keep(along * want.magnitude, false);
                }
                _side = 0f;
                State = "";
                return Keep(want, false);
            }

            // What rises ahead (Fire: "make sure that doesn't ruin its ability to jump up the exit stair"): a lip the body can jump
            // (a step, a ledge, a stair) is jumped, straight at it; a face Jumping calls a wall is not a slope to contour round
            // (the path, or going round the thing, handles it); only a continuous steep ground slope is contoured.
            JumpAdvice rise = Jumping.Check(body, flat, false);
            if (rise.Wall)
            {
                State = $"rise: wall {rise.LipHeight:0.0} m at {rise.LipDistance:0.0} m -> not a slope (path / round)";
                return Keep(want, false);
            }
            if (rise.LipHeight > Perception.StepHeight && rise.LipHeight <= Jumping.MaxLip && rise.LipDistance <= Jumping.LipReach + 0.01f)
            {
                State = $"rise: step {rise.LipHeight:0.0} m -> jump";
                return Keep(want, false);
            }

            // Too steep straight ahead: try the side we're on first (the contour), then the other one, widening the turn. While a
            // contour is held, only its own side (a re-path pointing back up the slope doesn't reverse it).
            if (now >= _sideUntil && !holding) _side = 0f;
            for (float turn = TurnStep; turn <= MaxTurn + 0.01f; turn += TurnStep)
            {
                for (int k = 0; k < 2; k++)
                {
                    if (k == 1 && holding) continue;
                    float side = k == 0 ? (_side != 0f ? _side : 1f) : (_side != 0f ? -_side : -1f);
                    Vector3 dir = Quaternion.Euler(0f, turn * side, 0f) * flat;
                    if (Steepest(feet, dir) > limit) continue;
                    if (_side != side)
                    {
                        Sidesteps++;
                        _contourFrom = feet;
                    }
                    else if (!holding) _contourFrom = feet;
                    _side = side;
                    _contourTurn = turn;
                    _sideUntil = now + KeepSideSeconds;
                    State = $"slope: {AheadText(ahead, limit)}: contour sidestep {(side < 0f ? "L" : "R")} {turn:0}°";
                    return Keep(dir * want.magnitude, false);
                }
            }
            State = $"slope: {AheadText(ahead, limit)}: boxed in (nothing walkable within ±{MaxTurn:0}°)";
            boxed = true;
            return Keep(want, true);
        }

        /// <summary>Forget the side and the cached answer (a new walk).</summary>
        /// <summary>
        /// Keep the contour side for <paramref name="seconds"/> whatever the distance walked (PathWalker's dither guard: R76, the
        /// side still flipped L/R after "committing to the path").
        /// </summary>
        public void HoldSide(float seconds)
        {
            if (_side != 0f) _freezeUntil = Time.time + seconds;
        }

        private float _freezeUntil;

        private static string AheadText(float ahead, float limit) =>
            ahead >= DropOff - 0.5f ? $"a drop-off over {FallSafe:0} m ahead" : $"{ahead:0}° too steep ahead (limit {limit:0}°)";

        public void Reset()
        {
            _freezeUntil = 0f;
            _nextCheck = 0f;
            _side = 0f;
            _sideUntil = 0f;
            State = "";
        }

        /// <summary>A contour is kept until the body has gone this far along it (m) and the straight way is walkable.</summary>
        public const float ContourCommitMetres = 3f;
        /// <summary>Checks in a row (<see cref="CheckSeconds"/> apart) with the straight way walkable that end a held contour early.</summary>
        public const int ClearChecksToRelease = 2;
        private int _clearChecks;
        /// <summary>A rock narrower than this (m, both ways) is an obstacle to go round, not a slope to contour.</summary>
        public const float NarrowSolid = 3f;
        private Vector3 _contourFrom;
        private float _contourTurn = TurnStep;

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            float x = a.x - b.x, z = a.z - b.z;
            return Mathf.Sqrt(x * x + z * z);
        }

        private Vector3 Keep(Vector3 result, bool boxed)
        {
            _lastResult = result;
            _lastBoxed = boxed;
            return result;
        }

        // The steeper surface tilt (degrees) of the ground Near and Far m along dir; 0 where no ground is found.
        private static float Steepest(Vector3 feet, Vector3 dir) =>
            Mathf.Max(Tilt(feet + dir * Near, feet.y), Tilt(feet + dir * Far, feet.y));

        /// <summary>A drop steeper than this below the feet (m) is a drop-off (Valheim's fall damage starts about here).</summary>
        public const float FallSafe = 4f;
        /// <summary>The tilt reported for a drop-off (no ground within <see cref="FallSafe"/> below): always too steep.</summary>
        private const float DropOff = 90f;

        private static float Tilt(Vector3 p, float feetY)
        {
            Vector3 from = new Vector3(p.x, feetY + CastAbove, p.z);
            // A drop-off: no ground at all within FallSafe below the feet.
            if (!Physics.Raycast(from, Vector3.down, out RaycastHit hit, CastAbove + FallSafe + 0.5f, Mask, QueryTriggerInteraction.Ignore))
                return DropOff;
            if (hit.collider.attachedRigidbody != null && hit.collider.attachedRigidbody.GetComponent<Character>() != null) return 0f;
            // A narrow rock or stump isn't a slope to contour along (0.2.203): the walker goes round it (slide, PlanRound, the planner).
            if (hit.collider.gameObject.layer != TerrainLayer
                && hit.collider.bounds.size.x < NarrowSolid && hit.collider.bounds.size.z < NarrowSolid) return 0f;
            // The first surface under the cast must be the ground at about the body's own level: a hit well above the feet is an
            // overhang or a ceiling, not the slope ahead.
            if (hit.point.y > feetY + 2.5f) return 0f;
            // Going DOWN (R76: boxed in on a hilltop, every heading down a 45-54° face refused, a rescue teleport): a player runs or
            // slides down a steep face; the slide angle only stops climbing. Downhill is walkable unless it is a drop-off.
            if (hit.point.y < feetY - FallSafe) return DropOff;
            if (hit.point.y < feetY - 0.2f) return 0f;
            float tilt = Mathf.Acos(Mathf.Clamp01(hit.normal.y)) * Mathf.Rad2Deg;
            return tilt > WallDegrees ? 0f : tilt;
        }
    }
}
