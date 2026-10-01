using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Where a swimming body gets out (Core 0.2.223; Fire 20:3x: "when bots are swimming across water or rivers they need to just
    /// commit and swim straight across, not turning back every few moments. this delay causes them to start drowning"). One exit is
    /// picked when the swim starts and held until the body has been out of the water for <see cref="DrySeconds"/> s. The pick is the
    /// ground nearest the walk goal plus half its ring radius, among the ground nearer the goal than the body (0.2.226), or simply
    /// the nearest ground when stamina runs low or there is no goal; with no such ground in reach, keep on toward the goal. The scoring is [visual]'s FDT 1.1.198 FindShore, which this replaces.
    /// One brain: the FDT bot bodies and the companions ask the same.
    /// </summary>
    public static class Swimming
    {
        /// <summary>How far out the rings look for ground (m).</summary>
        public const float MaxSearch = 80f;
        /// <summary>Under this stamina share the nearest ground wins whatever the goal.</summary>
        public const float LowStamina = 0.3f;
        /// <summary>A held exit is let go once the body has been out of the water this long (IsSwimming flickers at the edge).</summary>
        public const float DrySeconds = 1f;
        /// <summary>
        /// The exits a swim gave up stay barred until the body has been out of the water this long (s): a step onto a bank and back
        /// in is still the same swim (0.2.226; 0.2.223 forgot them with the held exit, after 1 s dry).
        /// </summary>
        public const float BarredDrySeconds = 5f;
        /// <summary>Ground counts as an exit this far above the liquid (m).</summary>
        private const float Lip = 0.3f;
        private const float RingStep = 4f;
        private const int Directions = 32;
        private const float RingWeight = 0.5f;
        private const float OnwardMetres = 10f;

        private sealed class Held
        {
            public Vector3 Exit;
            public string Why;
            public float DrySince = -1f;
            // No ground was in reach: the point is just "on toward the goal", re-picked every FallbackSeconds as the body swims on.
            public bool Fallback;
            public float PickedAt;
            // Progress toward it, and since when the body has been at it and still swimming (a bank it can't climb).
            public float BestDistance, BestAt, ReachedSince = -1f;
            // Picked as the nearest ground (low stamina or no goal): a stamina drop has nothing left to re-pick for.
            public bool Nearest;
        }

        private const float FallbackSeconds = 3f;
        /// <summary>At an exit (within this, m) and still swimming for <see cref="StuckAtExitSeconds"/>: it can't climb out there.</summary>
        private const float ReachMetres = 2.5f, StuckAtExitSeconds = 4f;
        /// <summary>No nearer to the exit by a metre in this long: it's not getting there (a current, an island in the way).</summary>
        private const float NoProgressSeconds = 12f;
        /// <summary>A barred exit keeps the next pick this far away (m).</summary>
        private const float BarRadius = 5f;
        /// <summary>Two metres out from an exit the water may be at most this deep (m), so the body can stand and walk out.</summary>
        private const float WadeDepth = 1.6f;

        private static readonly Dictionary<Character, Held> s_held = new Dictionary<Character, Held>();
        private static readonly HashSet<Character> s_warned = new HashSet<Character>();
        // Exits a body reached and couldn't climb out of, or never got nearer to, this swim (run 7: Coop2 held one 48 s and drowned).
        private static readonly Dictionary<Character, List<Vector3>> s_barred = new Dictionary<Character, List<Vector3>>();
        // Since when a body with barred exits has been out of the water (BarredDrySeconds).
        private static readonly Dictionary<Character, float> s_drySince = new Dictionary<Character, float>();

        /// <summary>
        /// The exit <paramref name="body"/> is committed to while it swims: picked once toward <paramref name="toward"/> (the walk goal;
        /// the body's own position for none) and held until it has been out of the water <see cref="DrySeconds"/> s. False when it isn't
        /// swimming and holds nothing.
        /// </summary>
        public static bool ExitFor(Character body, Vector3 toward, out Vector3 exit, out string why)
        {
            exit = Vector3.zero;
            why = "no body";
            if (body == null) return false;
            // 0.2.252: climbing out (Core's climb has the body): no exit to swim for.
            if (FiresCore.Movement.Climb.IsClimbing(body)) { s_held.Remove(body); why = "climbing out"; return false; }
            bool swimming = body.IsSwimming();
            Vector3 at = body.transform.position;
            if (swimming) s_drySince.Remove(body);
            else if (s_barred.ContainsKey(body))
            {
                if (!s_drySince.TryGetValue(body, out float drySince)) s_drySince[body] = drySince = Time.time;
                if (Time.time - drySince >= BarredDrySeconds) { s_barred.Remove(body); s_drySince.Remove(body); }
            }
            if (s_held.TryGetValue(body, out Held held))
            {
                if (swimming) held.DrySince = -1f;
                else if (held.DrySince < 0f) held.DrySince = Time.time;
                else if (Time.time - held.DrySince >= DrySeconds) { s_held.Remove(body); held = null; }
                if (held != null && held.Fallback && swimming && Time.time - held.PickedAt >= FallbackSeconds) { s_held.Remove(body); held = null; }
                // Stamina ran low on the way to a goal-scored exit that is still far: the nearest ground now (0.2.226; a goal-scored
                // pick may be most of MaxSearch away).
                if (held != null && swimming && !held.Nearest && Flat(at, held.Exit) > 2f * ReachMetres && StaminaShare(body) < LowStamina)
                {
                    Debug.Log($"[Swimming] {Name(body)}: stamina {StaminaShare(body) * 100f:0} % with the exit at ({held.Exit.x:0}, {held.Exit.z:0}) still {Flat(at, held.Exit):0} m off; the nearest ground instead");
                    s_held.Remove(body);
                    held = null;
                }
                if (held != null && !held.Fallback && swimming)
                {
                    float d = Flat(at, held.Exit);
                    if (d < held.BestDistance - 1f) { held.BestDistance = d; held.BestAt = Time.time; }
                    if (d <= ReachMetres) { if (held.ReachedSince < 0f) held.ReachedSince = Time.time; }
                    else held.ReachedSince = -1f;
                    string bar = held.ReachedSince >= 0f && Time.time - held.ReachedSince >= StuckAtExitSeconds
                        ? $"reached it and still swimming after {StuckAtExitSeconds:0} s (a bank it can't climb)"
                        : Time.time - held.BestAt >= NoProgressSeconds ? $"no nearer to it in {NoProgressSeconds:0} s" : null;
                    if (bar != null)
                    {
                        if (!s_barred.TryGetValue(body, out List<Vector3> list)) s_barred[body] = list = new List<Vector3>();
                        list.Add(held.Exit);
                        Debug.Log($"[Swimming] {Name(body)}: giving up the exit at ({held.Exit.x:0}, {held.Exit.z:0}): {bar}; picking the next");
                        s_held.Remove(body);
                        held = null;
                    }
                }
                if (held != null)
                {
                    exit = held.Exit;
                    why = held.Why;
                    return true;
                }
            }
            if (!swimming) { why = "not swimming"; return false; }

            float stamina = StaminaShare(body);
            float liquid = Mathf.Max(ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f, body.GetLiquidLevel());
            s_barred.TryGetValue(body, out List<Vector3> barred);
            float budget = SwimBudgetMetres(body);
            bool ground = Exit(at, toward, stamina, liquid, barred, budget, out exit, out why, body);
            // 0.2.252: a climb exit is a PLANNED climb: Core's climb starts once the swimmer reaches the bank's face.
            if (ground && s_pickClimb)
            {
                Vector3 inland = exit - at;
                inland.y = 0f;
                inland = inland.sqrMagnitude > 0.01f ? inland.normalized : body.transform.forward;
                Vector3 onward = toward - at;
                onward.y = 0f;
                // 0.2.258: past a shelf the face is where the probe found it, not at the exit.
                FiresCore.Movement.Climbing.BankFace face = s_pickFace;
                FiresCore.Movement.Climb.Plan(body, new FiresCore.Movement.Climbing.ClimbPlan
                {
                    Foot = face != null ? face.Foot : exit - inland * 1.5f, Top = (face != null ? face.Top : exit) + inland * 0.6f, Goal = onward.sqrMagnitude > 4f ? toward : exit,
                    Height = s_pickHeight, Angle = s_pickAngle, Stamina = FiresCore.Movement.Climbing.StaminaFor(body, s_pickHeight, s_pickAngle),
                    Surface = face != null ? face.Surface : "the bank",
                }, face != null ? $"climbing out of the water past a shelf up a {s_pickAngle:0}° {s_pickHeight:0.0} m face" : $"climbing out of the water up a {s_pickAngle:0}° bank", 60f);
            }
            Vector3 heading = toward - at;
            heading.y = 0f;
            s_held[body] = new Held
            {
                Exit = exit, Why = why, Fallback = !ground, PickedAt = Time.time, BestDistance = Flat(at, exit), BestAt = Time.time,
                Nearest = stamina < LowStamina || heading.sqrMagnitude <= 4f || GoalInDeepWater(toward, out _),
            };
            if (ground) Debug.Log($"[Swimming] {Name(body)}: committed to the exit at ({exit.x:0}, {exit.z:0}), {Flat(at, exit):0} m: {why}");
            // 0.2.250: the stamina didn't pay for the far side and the exit is the bank behind (no nearer the goal): remembered.
            if (ground && heading.sqrMagnitude > 4f && why.Contains("stamina for") && Flat(exit, toward) >= Flat(at, toward) - 1f)
                NoteTurnBack(body, toward, budget);
            else if (!s_warned.Contains(body)) { s_warned.Add(body); Debug.Log($"[Swimming] {Name(body)}: {why}"); }
            if (ground) s_warned.Remove(body);
            return true;
        }

        /// <summary>
        /// <see cref="Exit(Vector3, Vector3, float, float, out Vector3, out string)"/> at full stamina and the sea level: for a caller
        /// without a body. Prefer <see cref="ExitFor"/>, which holds the pick.
        /// </summary>
        public static bool Exit(Vector3 from, Vector3 toward, out Vector3 exit, out string why) =>
            Exit(from, toward, 1f, ZoneSystem.instance != null ? ZoneSystem.instance.m_waterLevel : 30f, out exit, out why);

        /// <summary>Forget <paramref name="body"/>'s held exit (the caller wants a fresh pick).</summary>
        public static void Release(Character body)
        {
            if (body != null) s_held.Remove(body);
        }

        /// <summary>
        /// The caller found <paramref name="exit"/> can't be climbed out of: it is barred for the rest of this swim (the next pick keeps
        /// <see cref="BarRadius"/> m off it) and the held exit is let go. <see cref="ExitFor"/> does this itself after 4 s at an exit
        /// still swimming; this is for a caller that knows sooner ([visual], FDT 1.1.199).
        /// </summary>
        public static void MarkUnclimbable(Character body, Vector3 exit)
        {
            if (body == null) return;
            if (!s_barred.TryGetValue(body, out List<Vector3> list)) s_barred[body] = list = new List<Vector3>();
            list.Add(exit);
            s_held.Remove(body);
        }

        /// <summary>
        /// The exit from <paramref name="from"/>: ground at least <see cref="Lip"/> above <paramref name="liquidLevel"/> and not itself
        /// under liquid, on rings every 4 m out to <see cref="MaxSearch"/>. Scored by its distance to <paramref name="toward"/> plus half
        /// its ring radius, and only ground nearer the goal than <paramref name="from"/> counts; the nearest when <paramref name="stamina"/>
        /// is under <see cref="LowStamina"/> or there's no goal. True with a ground point; false (exit = <see cref="OnwardMetres"/> on
        /// toward the goal) when no such ground lies within reach.
        /// </summary>
        public static bool Exit(Vector3 from, Vector3 toward, float stamina, float liquidLevel, out Vector3 exit, out string why) =>
            Exit(from, toward, stamina, liquidLevel, null, float.MaxValue, out exit, out why);

        // The pick, keeping BarRadius off the exits this swim already gave up (null: none), and wanting water shallow enough to stand
        // in WadeDepth 2 m out from the exit (a steep bank the body can't climb out of is no exit).
        // budget: the metres the body's stamina pays for (SwimBudgetMetres); a goal-scored exit further than that is not taken (0.2.233).
        private static bool Exit(Vector3 from, Vector3 toward, float stamina, float liquidLevel, List<Vector3> barred, float budget, out Vector3 exit, out string why,
            Character body = null)
        {
            exit = from;
            s_pickClimb = false;
            s_pickFace = null;
            bool bestClimb = false, nearClimb = false;
            float bestAngle = 0f, bestHeight = 0f, nearAngle = 0f, nearHeight = 0f;
            FiresCore.Movement.Climbing.BankFace bestFace = null, nearFace = null;
            Vector3 heading = toward - from;
            heading.y = 0f;
            bool hasGoal = heading.sqrMagnitude > 4f;
            // A goal no body can stand at (a floating or sunk item) is no goal (0.2.229, R90 run 10: Coop1 swam for "Wood at (211, 431)"
            // floating 6 m out, turned for the nearest ground at low stamina, walked back in, for 4+ min).
            string deep = null;
            if (hasGoal && GoalInDeepWater(toward, out float depth))
            {
                hasGoal = false;
                deep = $"the goal at ({toward.x:0}, {toward.z:0}) is in water {depth:0.0} m deep, no swim for it: ";
            }
            bool nearest = stamina < LowStamina || !hasGoal;
            float bestScore = float.MaxValue;
            bool found = false, behind = false, tooFar = false;
            float goalNow = Flat(from, toward);
            // The nearest ground too, for when every goal-scored exit costs more stamina than the body has (0.2.233).
            Vector3 nearExit = from;
            bool nearFound = false;
            if (ZoneSystem.instance != null)
                for (float r = RingStep; r <= MaxSearch; r += RingStep)
                {
                    // No later ring can beat what we have: its score is at least its radius (nearest) or half of it (goal-scored).
                    if (found && (nearest ? r : RingWeight * r) >= bestScore) break;
                    if (nearest && nearFound) break;
                    if (!nearest && nearFound && r > budget && !found) break;   // past the budget: only the nearest ground is left
                    for (int i = 0; i < Directions; i++)
                    {
                        float a = i * Mathf.PI * 2f / Directions;
                        Vector3 p = from + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * r;
                        // The bool form: an unloaded zone (no terrain hit) is skipped, not read as the probe's own height.
                        if (!ZoneSystem.instance.GetGroundHeight(p, out float ground) || ground < liquidLevel + Lip) continue;
                        p.y = ground;
                        if (FiresCore.World.Water.SurfaceOr(p.x, p.z, Floating.GetLiquidLevel(p + Vector3.up * 0.1f)) > ground + 0.1f) continue;   // ground under another water (FAT's too)
                        if (barred != null && barred.Exists(b => Flat(b, p) < BarRadius)) continue;     // given up this swim
                        // Shallow enough to stand in 2 m out toward the swimmer: a steep bank is no way out.
                        Vector3 back = from - p;
                        back.y = 0f;
                        // 0.2.252: a steep bank the body can climb (38°+, within its climb terms, the swim and the climb both paid for)
                        // is a climb exit (Fire, the overnight pond: climb out instead of turning back).
                        bool climbOut = false;
                        float climbAngle = 0f, climbHeight = 0f;
                        FiresCore.Movement.Climbing.BankFace climbFace = null;
                        if (back.sqrMagnitude > 0.01f && ZoneSystem.instance.GetGroundHeight(p + back.normalized * 2f, out float near)
                            && near < liquidLevel - WadeDepth)
                        {
                            if (body == null || !ClimbOut(body, p, back.normalized, liquidLevel, budget, r, out climbAngle, out climbHeight)) continue;
                            climbOut = true;
                        }
                        // 0.2.258 (R37, the CoopStream pond): wadeable water at the bank is no walk out when a face stands between the
                        // water and the exit (a shallow shelf up to a cliff: Coop1 waded into it overnight). That face is a climb exit if
                        // this body can climb it and pay for it, else no exit.
                        else if (back.sqrMagnitude > 0.01f && ShelfFace(p, back.normalized, liquidLevel, r, out climbFace))
                        {
                            if (body == null || !ClimbFace(body, climbFace, budget, r)) continue;
                            climbOut = true;
                            climbAngle = climbFace.Angle;
                            climbHeight = climbFace.Height;
                        }
                        if (!nearFound) { nearFound = true; nearExit = p; nearClimb = climbOut; nearAngle = climbAngle; nearHeight = climbHeight; nearFace = climbFace; }   // rings go outward: the first is the nearest
                        // Toward a goal, an exit must leave the body nearer the goal than it is now: the bank behind it is the way
                        // back, not across (0.2.226, R90 run 8: Coop2 at (49, 520) swimming for a Raspberry at (48, 528) committed
                        // to the bank it had just left at (48, 516), stepped out, walked back in, 7 times). None ahead: swim on.
                        if (!nearest && Flat(p, toward) >= goalNow) { behind = true; continue; }
                        // Further than the stamina pays for (0.2.233, R90 run 11: Coop1 committed to an exit 32 m off at 36 %, re-picked
                        // at 30 % with it 19 m off, and got out with 16 %).
                        if (!nearest && r > budget) { tooFar = true; continue; }
                        float score = nearest ? r : Flat(p, toward) + RingWeight * r;
                        if (score >= bestScore) continue;
                        bestScore = score;
                        exit = p;
                        found = true;
                        bestClimb = climbOut;
                        bestAngle = climbAngle;
                        bestHeight = climbHeight;
                        bestFace = climbFace;
                    }
                }
            if (found)
            {
                why = deep + (stamina < LowStamina ? $"stamina {stamina * 100f:0} %: the nearest ground" : hasGoal ? "the ground nearest the goal" : "the nearest ground");
                if (bestClimb)
                {
                    s_pickClimb = true; s_pickAngle = bestAngle; s_pickHeight = bestHeight; s_pickFace = bestFace;
                    why += bestFace != null ? $" (climb out past the shelf: {bestFace})" : $" (climb out: a {bestAngle:0}° bank, {bestHeight:0.0} m above the water)";
                }
                return true;
            }
            // No goal-scored exit the stamina pays for, or the goal itself further than that: the nearest ground (0.2.233).
            if (nearFound && !nearest && (tooFar || (behind && goalNow > budget)))
            {
                exit = nearExit;
                why = deep + $"stamina for {budget:0} m only (the goal {goalNow:0} m off): the nearest ground";
                if (nearClimb)
                {
                    s_pickClimb = true; s_pickAngle = nearAngle; s_pickHeight = nearHeight; s_pickFace = nearFace;
                    why += nearFace != null ? $" (climb out past the shelf: {nearFace})" : $" (climb out: a {nearAngle:0}° bank)";
                }
                return true;
            }
            exit = hasGoal ? from + heading.normalized * OnwardMetres : from;
            why = deep + (behind ? $"no ground nearer the goal ({goalNow:0} m) than here: swimming on toward it"
                : $"no ground within {MaxSearch:0} m{(hasGoal ? ": swimming on toward the goal" : "")}");
            return false;
        }

        // The last pick was a climb exit (Exit -> ExitFor, one thread); s_pickFace: the face past a shelf (0.2.258), null for a bank at the water.
        private static bool s_pickClimb;
        private static float s_pickAngle, s_pickHeight;
        private static FiresCore.Movement.Climbing.BankFace s_pickFace;

        /// <summary>The flat metres a shelf face is looked for between the swimmer and an exit (Climbing.BankReach).</summary>
        private const float ShelfReach = FiresCore.Movement.Climbing.BankReach;
        /// <summary>A face this high over the water (or more) on the way from the water to an exit is climbed, not walked (vanilla steps ~0.5 m).</summary>
        private const float ShelfFaceAbove = 1f;

        // 0.2.258: a face between the water and the exit p, looked for from the swimmer's side (up to ShelfReach m before p) toward p:
        // across the shelf to the waterline and the shore, a 38°+ rise whose top stands ShelfFaceAbove+ over the water and lies short of p
        // (+1 m). A gentle rise is a walk (false).
        private static bool ShelfFace(Vector3 p, Vector3 towardSwimmer, float liquidLevel, float swimMetres, out FiresCore.Movement.Climbing.BankFace face)
        {
            float reach = Mathf.Min(swimMetres, ShelfReach);
            Vector3 start = p + towardSwimmer * reach;
            if (!FiresCore.Movement.Climbing.ProbeBank(start, -towardSwimmer, liquidLevel, reach + 1f, ShelfFaceAbove, out face)) return false;
            return face.Angle >= FiresCore.Movement.Climbing.MinAngle && face.Distance <= reach + 1f;
        }

        // The shelf face climbable by body: within its climb terms, and the stamina after a swim of swimMetres (of the budget) paying for it.
        private static bool ClimbFace(Character body, FiresCore.Movement.Climbing.BankFace face, float budget, float swimMetres)
        {
            FiresCore.Movement.ClimbTerms terms = FiresCore.Movement.Climbing.TermsFor(body);
            if (!terms.Allowed || face.Angle > terms.MaxAngle + 0.5f || face.Above > FiresCore.Movement.Climbing.BankMaxAbove) return false;
            float max = FiresCore.Movement.Climbing.MaxStaminaOf(body), spare = FiresCore.Movement.Climbing.StaminaOf(body) - SwimMargin * max;
            if (spare <= 0f) return false;
            float left = spare * (1f - Mathf.Clamp01(swimMetres / Mathf.Max(1f, budget)));
            return FiresCore.Movement.Climbing.StaminaFor(body, face.Height, face.Angle) <= left;
        }

        // A steep bank at p (deep water 2 m out toward the swimmer) climbable by body: the face it climbs, from the waterline (where the
        // ground meets the surface between p and the swimmer) up to p, 38°+ and within the body's climb terms (0.2.254: from the waterline,
        // not the bed: a deep pond with a gentle bank read as steep), and the stamina after a swim of swimMetres (of the budget) paying
        // for the climb up out of the water.
        private static bool ClimbOut(Character body, Vector3 p, Vector3 towardSwimmer, float liquidLevel, float budget, float swimMetres, out float angle, out float height)
        {
            height = Mathf.Max(0.3f, p.y - liquidLevel);
            float run = 2f;
            for (float s = 0.25f; s <= 2f; s += 0.25f)
                if (ZoneSystem.instance.GetGroundHeight(p + towardSwimmer * s, out float g) && g < liquidLevel) { run = s; break; }
            angle = Mathf.Atan2(p.y - liquidLevel, Mathf.Max(0.05f, run)) * Mathf.Rad2Deg;
            FiresCore.Movement.ClimbTerms terms = FiresCore.Movement.Climbing.TermsFor(body);
            if (!terms.Allowed || angle < FiresCore.Movement.Climbing.MinAngle || angle > terms.MaxAngle + 0.5f) return false;
            float max = FiresCore.Movement.Climbing.MaxStaminaOf(body), spare = FiresCore.Movement.Climbing.StaminaOf(body) - SwimMargin * max;
            if (spare <= 0f) return false;
            float left = spare * (1f - Mathf.Clamp01(swimMetres / Mathf.Max(1f, budget)));
            return FiresCore.Movement.Climbing.StaminaFor(body, height, angle) <= left;
        }

        /// <summary>
        /// <paramref name="goal"/> lies in water deeper than <see cref="WadeDepth"/>: the ground under it is that far below the surface
        /// (a floating item, something sunk), so no body stands there. It never drives a committed swim (0.2.229), and a planner shouldn't
        /// send a body to it. False on dry or wadeable ground, or where the ground isn't loaded.
        /// </summary>
        public static bool GoalInDeepWater(Vector3 goal, out float depth)
        {
            depth = 0f;
            if (ZoneSystem.instance == null || !ZoneSystem.instance.GetGroundHeight(goal, out float ground)) return false;
            // 0.2.261 (R43: the climb's goal on a rock standing in the pond read "in water 2.0 m deep", so no swim went for it): a solid
            // floor near the goal's own height (a rock top, a dock) is where a body stands, whatever terrain lies under the water there.
            if (FiresCore.World.Surface.Height(goal, goal.y, out float floor) && floor > ground && Mathf.Abs(floor - goal.y) < 2f) ground = floor;
            // Floating.GetLiquidLevel only answers from inside a water volume: ask just above the bed.
            float surface = Mathf.Max(ZoneSystem.instance.m_waterLevel, Floating.GetLiquidLevel(new Vector3(goal.x, ground + 0.1f, goal.z)));
            // 0.2.256: FAT's lakes, rivers, pours and ponds too (R36: the CoopStream pond read dry).
            surface = FiresCore.World.Water.SurfaceOr(goal.x, goal.z, surface);
            depth = surface - ground;
            return depth > WadeDepth;
        }

        // ---- The stamina budget for a swim (0.2.233) ----

        /// <summary>The stamina share a swim keeps in hand at the far bank (vanilla drains 5/s at Swim 0 for 2 m/s: 75 stamina ≈ 30 m)…</summary>
        public const float SwimMargin = 0.15f;
        /// <summary>…and for a body that is unarmed or already under half its stamina (it lands with less to fight or flee on).</summary>
        public const float StrictSwimMargin = 0.3f;

        /// <summary>
        /// How far (m) <paramref name="body"/> can swim on its stamina and still land with <see cref="SwimMargin"/> of it
        /// (<see cref="StrictSwimMargin"/> unarmed or under half): vanilla's drain for a player (Player.OnSwimming: Lerp of
        /// m_swimStaminaDrainMinSkill → MaxSkill by the Swim skill, the gear's swim modifier, Game.m_moveStaminaRate) at its m_swimSpeed;
        /// a companion at the player's skill-0 drain. 0 when it is already at the margin.
        /// </summary>
        public static float SwimBudgetMetres(Character body)
        {
            if (body == null) return 0f;
            float speed = Mathf.Max(0.5f, body.m_swimSpeed);
            float drain = 5f, stamina, max;
            if (body is Player player)
            {
                drain = Mathf.Lerp(player.m_swimStaminaDrainMinSkill, player.m_swimStaminaDrainMaxSkill, player.GetSkillFactor(Skills.SkillType.Swim));
                drain += drain * player.GetEquipmentSwimStaminaModifier();
                drain *= Game.m_moveStaminaRate;
                stamina = player.GetStamina();
                max = Mathf.Max(1f, player.GetMaxStamina());
            }
            else
            {
                max = 100f;
                stamina = StaminaShare(body) * max;
            }
            Humanoid humanoid = body as Humanoid;
            ItemDrop.ItemData weapon = humanoid != null ? humanoid.GetCurrentWeapon() : null;
            bool unarmed = humanoid != null && (weapon == null || (humanoid.m_unarmedWeapon != null && weapon == humanoid.m_unarmedWeapon.m_itemData));
            float margin = (unarmed || stamina < max * 0.5f ? StrictSwimMargin : SwimMargin) * max;
            float spare = stamina - margin;
            return spare <= 0f ? 0f : spare / Mathf.Max(0.1f, drain) * speed;
        }

        /// <summary><see cref="SwimBudgetMetres(Character)"/> with full stamina: how far the body could swim once rested.</summary>
        public static float SwimBudgetRestedMetres(Character body)
        {
            if (body == null) return 0f;
            float speed = Mathf.Max(0.5f, body.m_swimSpeed), drain = 5f, max = 100f;
            if (body is Player player)
            {
                drain = Mathf.Lerp(player.m_swimStaminaDrainMinSkill, player.m_swimStaminaDrainMaxSkill, player.GetSkillFactor(Skills.SkillType.Swim));
                drain += drain * player.GetEquipmentSwimStaminaModifier();
                drain *= Game.m_moveStaminaRate;
                max = Mathf.Max(1f, player.GetMaxStamina());
            }
            float spare = max - SwimMargin * max;
            return spare <= 0f ? 0f : spare / Mathf.Max(0.1f, drain) * speed;
        }

        // ---- Swims on a route (0.2.250, Fire: "Our bot was stuck in a pond walking against a cliff for a long time") ----
        // Overnight 04:23-04:43 Coop1's walk to a Raspberry bush across an inland pond followed a navmesh path through the water (the
        // humanoid navmesh swims), Exit turned it back at the near bank ("stamina for 3 m only (the goal 50 m off): the nearest
        // ground"), and the walk sent it straight back in: 117 times in 20 min. Exit's call was right each time; nothing remembered it.

        public enum RouteSwim { Dry, Go, RestFirst, TooFar }

        /// <summary>
        /// The longest stretch of deep water (deeper than a body can stand in, as <see cref="GoalInDeepWater"/>) along the walk from
        /// <paramref name="from"/> through <paramref name="corners"/>, sampled every metre: where it starts and ends. Stamina comes back on
        /// land between stretches, so the longest one is what a crossing costs.
        /// </summary>
        public static float LongestSwim(Vector3 from, IList<Vector3> corners, out Vector3 start, out Vector3 end)
        {
            start = end = from;
            float best = 0f, run = 0f;
            Vector3 runStart = from, prev = from;
            if (corners == null) return 0f;
            foreach (Vector3 corner in corners)
            {
                float leg = Flat(prev, corner);
                int steps = Mathf.Max(1, Mathf.CeilToInt(leg));
                for (int i = 1; i <= steps; i++)
                {
                    Vector3 p = Vector3.Lerp(prev, corner, i / (float)steps);
                    if (GoalInDeepWater(p, out _))
                    {
                        if (run <= 0f) runStart = p;
                        run += leg / steps;
                        if (run > best) { best = run; start = runStart; end = p; }
                    }
                    else run = 0f;
                }
                prev = corner;
            }
            return best;
        }

        /// <summary>
        /// Whether <paramref name="body"/> should walk the route from <paramref name="from"/> through <paramref name="corners"/> as far as
        /// swimming goes: Dry (no deep water on it), Go (its longest swim fits the stamina now), RestFirst (it fits once rested: wait on
        /// the bank before going in), TooFar (not even rested: no swim route; walk round or pick another goal). A body this brain
        /// already turned back twice toward the route's end (<see cref="TurnedBackToward"/>) gets TooFar too. <paramref name="why"/> has
        /// the numbers and where the swim is.
        /// </summary>
        public static RouteSwim JudgeRoute(Character body, Vector3 from, IList<Vector3> corners, out string why)
        {
            why = "";
            if (body == null || corners == null || corners.Count == 0) { why = "no route"; return RouteSwim.Dry; }
            Vector3 goal = corners[corners.Count - 1];
            if (TurnedBackToward(body, goal, out string turned)) { why = turned; return RouteSwim.TooFar; }
            float swim = LongestSwim(from, corners, out Vector3 start, out Vector3 end);
            if (swim <= 0f) { why = "no deep water on the route"; return RouteSwim.Dry; }
            float now = SwimBudgetMetres(body), rested = SwimBudgetRestedMetres(body);
            string where = $"{swim:0} m of swimming from ({start.x:0}, {start.z:0}) to ({end.x:0}, {end.z:0})";
            if (swim <= now) { why = $"{where}; stamina pays for {now:0} m"; return RouteSwim.Go; }
            if (swim <= rested) { why = $"{where}; stamina pays for {now:0} m now, {rested:0} m rested: resting on the bank first"; return RouteSwim.RestFirst; }
            why = $"{where}; even rested the stamina pays for {rested:0} m: no swim route";
            return RouteSwim.TooFar;
        }

        // Turn-backs: Exit sent the body to the nearest ground, behind it, because the stamina didn't pay for the far side.
        private const float TurnBackRadius = 10f, TurnBackWindow = 300f, NoSwimSeconds = 600f;
        private const int TurnBacksToGiveUp = 2;
        private static readonly Dictionary<Character, List<(Vector3 goal, float at)>> s_turnBacks = new Dictionary<Character, List<(Vector3, float)>>();
        private static readonly Dictionary<Character, List<(Vector3 goal, float until, string why)>> s_noSwim = new Dictionary<Character, List<(Vector3, float, string)>>();

        private static void NoteTurnBack(Character body, Vector3 goal, float budget)
        {
            if (!s_turnBacks.TryGetValue(body, out var list)) s_turnBacks[body] = list = new List<(Vector3, float)>();
            list.RemoveAll(t => Time.time - t.at > TurnBackWindow);
            list.Add((goal, Time.time));
            int count = list.Count(t => Flat(t.goal, goal) < TurnBackRadius);
            Debug.Log($"[Swimming] {Name(body)}: turned back toward the bank behind it (stamina for {budget:0} m, the goal at ({goal.x:0}, {goal.z:0}) further): {count} time(s) in {TurnBackWindow / 60f:0} min");
            if (count < TurnBacksToGiveUp) return;
            if (!s_noSwim.TryGetValue(body, out var no)) s_noSwim[body] = no = new List<(Vector3, float, string)>();
            string why = $"turned back {count} times swimming toward ({goal.x:0}, {goal.z:0}) (stamina for {budget:0} m): no swim route there for {NoSwimSeconds / 60f:0} min";
            no.RemoveAll(n => Flat(n.goal, goal) < TurnBackRadius);
            no.Add((goal, Time.time + NoSwimSeconds, why));
            Debug.Log($"[Swimming] {Name(body)}: {why}");
        }

        /// <summary>
        /// This body was turned back twice in 5 min swimming toward somewhere within 10 m of <paramref name="goal"/>: for 10 min there is
        /// no swim route there for it (a planner skips the goal, a walker walks round or gives up). <paramref name="why"/> says so.
        /// </summary>
        public static bool TurnedBackToward(Character body, Vector3 goal, out string why)
        {
            why = "";
            if (body == null || !s_noSwim.TryGetValue(body, out var no)) return false;
            no.RemoveAll(n => Time.time >= n.until);
            foreach (var n in no)
                if (Flat(n.goal, goal) < TurnBackRadius) { why = n.why; return true; }
            return false;
        }

        /// <summary>
        /// Whether <paramref name="body"/> can swim <paramref name="metres"/> and land with its margin (<see cref="SwimBudgetMetres"/>): a
        /// planner asks before sending a body into water for a goal that can wait. <paramref name="why"/> gives both numbers.
        /// </summary>
        public static bool CanAffordSwim(Character body, float metres, out string why)
        {
            float budget = SwimBudgetMetres(body);
            why = $"stamina for {budget:0} m of swimming, {metres:0} m asked";
            return metres <= budget;
        }

        private static string Name(Character c) => c is Player p ? p.GetPlayerName() : c.m_name;

        // A player's own stamina; a companion's through its StaminaManager (CombatAdvisor's reading).
        private static float StaminaShare(Character body)
        {
            if (body is Player player)
            {
                float max = player.GetMaxStamina();
                return max > 0f ? player.GetStamina() / max : 1f;
            }
            var manager = body.GetComponent<Combat.StaminaManager>();
            return manager != null ? manager.GetStaminaPercent() : 1f;
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
