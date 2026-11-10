using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// 0.2.92x ([ghost], Fire 10-27, movement plan - PATHING set, Tools\COMPANION_MOVEMENT_PLAN_20261027.md):
    ///  - pace: walk / jog / sprint chosen by circumstance ("alternate between walking and jogging to destinations while they are working"):
    ///    walk for short hops (< 10 m), the last 3 m, a heavy bag or low stamina; jog for medium trips or an urgent need (a high homestead
    ///    need for the chore, an order); sprint only on a long (> 30 m) open stretch when urgent; per-companion +-15 % variety.
    ///    "[Move] <name> walk->jog: target 35 m, need high" on a change only.
    ///  - unstuck ladder ("never be getting stuck basically anywhere"; pathing moves only, no slide / combat moves): no progress toward the
    ///    goal (0.5 m in 3 s) -> jump -> double jump -> vault -> climb -> step back + re-path -> sprint around (open ground) -> teleport to the
    ///    leader / home (last). "[Unstuck] <name> at (x, y, z): tried <move> -> ok | fail".
    /// Both inside CompanionAI.RequestPathfindingMovement; [Companions] Dynamic pace / Unstuck moves, OFF until their tests pass (Fire).
    /// </summary>
    public partial class CompanionAI
    {
        // ---- config
        // 1086 ([generator], Fire 11-01: configs live in their own mods): bound by FiresCompanions; Core reads the owner's value (old default without it).
        private static readonly FiresCore.Config.OwnedSetting<bool> s_paceOn = new FiresCore.Config.OwnedSetting<bool>("Companions", "Dynamic pace", false),
            s_unstuckOn = new FiresCore.Config.OwnedSetting<bool>("Companions", "Unstuck moves", false);
        internal static bool s_pathingTestOn;   // core_companion_unstuck_test only; never saved to the cfg
        internal static void BindPathingConfig(BepInEx.Configuration.ConfigFile cfg) { }   // 1086 ([generator], Fire 11-01: configs live in their own mods): nothing to bind
        // 0.2.982 ([lead]/Fire 10-29): a fight is not dropped for an owner 12-35 m away who is walking off; only the far recall applies
        private static readonly FiresCore.Config.OwnedSetting<bool> s_combatNoNearRecall = new FiresCore.Config.OwnedSetting<bool>("Companions", "Combat ignores the 12 m moving-owner recall", true);   // 1086 ([generator], Fire 11-01: configs live in their own mods):
        private float _keptLogAt;
        internal static bool CombatIgnoresNearRecall => s_combatNoNearRecall.Value;
        internal static void BindRecallConfig(BepInEx.Configuration.ConfigFile cfg) { }   // 1086 ([generator], Fire 11-01: configs live in their own mods): nothing to bind
        internal static bool PaceOn => s_pathingTestOn || s_paceOn.Value;
        internal static bool UnstuckOn => s_pathingTestOn || s_unstuckOn.Value;

        // ---- pace
        private enum Pace { Walk, Jog, Sprint }
        private Pace _pace = Pace.Walk;
        private Vector3 _paceGoal = new Vector3(float.NaN, 0f, 0f);
        private float _paceAt = -10f;
        private float _paceVariety = -1f;

        // ---- 0.2.923 ([ghost], round #33 [WalkDiag]: "stand target (295.1, 54.5, 933.5): stopped at (295.9, 50.4, 932.9), 4.2 m left, moving 0.00
        // m/s" with a full 21.6 m path up the stairs): vanilla BaseAI.MoveTo calls a goal "reached" by FLAT (XZ) distance, so a companion right
        // under an upstairs goal (1.0 m flat, 4.1 m below) stopped there, while RequestPathfindingMovement's 3D check said "not arrived" - a 58 s
        // stall every trip. A goal on another floor (over 1.5 m up / down) keeps vanilla walking the path to its end (reach 0.25 m flat); the 3D
        // arrival check above still decides when it is there. Always on (a fix, not new behaviour; nothing changes on one floor).
        private const float OtherFloor = 1.5f, OtherFloorReach = 0.25f;
        /// <summary>The corners vanilla is walking now (after the corner easing) and the clearance it kept; for [WalkDiag].</summary>
        internal string WalkedPathNote
        {
            get
            {
                int n = m_path != null ? m_path.Count : 0;
                if (n == 0) return "no path points (vanilla has none)";
                return $"{n} point(s), clearance kept {PathWalker.LastClearance:0.0} m: " + string.Join(" ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Take(m_path, 12), p => $"({p.x:0.0},{p.y:0.0},{p.z:0.0})"));
            }
        }
        private Vector3 _floorLoggedFor = new Vector3(float.NaN, 0f, 0f);
        private float FloorReach(Vector3 destination, float reach)
        {
            float dy = destination.y - transform.position.y;
            if (Mathf.Abs(dy) <= OtherFloor || reach <= OtherFloorReach) return reach;
            if (Utils.DistanceXZ(transform.position, destination) < reach && (destination - _floorLoggedFor).sqrMagnitude > 1f)
            {
                _floorLoggedFor = destination;
                FiresCore.Logging.FiresLogger.LogDebug($"[CompanionAI] {m_character?.m_name}: goal {destination:F1} is {dy:+0.0;-0.0} m away on another floor, {Utils.DistanceXZ(transform.position, destination):0.0} m flat: walking the path on (vanilla would stop here)");
            }
            return OtherFloorReach;
        }

        private bool ApplyPace(Vector3 destination, string owner, Core.UnifiedMovementAuthority.MovementSource source, bool run)
        {
            if (!PaceOn || m_character == null) return run;
            if ((destination - _paceGoal).sqrMagnitude < 1f && Time.time - _paceAt < 2f) { m_character.SetWalk(_pace == Pace.Walk); return _pace == Pace.Sprint; }
            _paceGoal = destination; _paceAt = Time.time;
            if (_paceVariety < 0f) { var nv = m_character.GetComponent<ZNetView>(); int seed = nv != null && nv.IsValid() ? nv.GetZDO().m_uid.GetHashCode() : GetInstanceID(); _paceVariety = 0.85f + 0.3f * ((seed & 0xFFFF) / 65535f); }
            float v = _paceVariety;
            float dist = Vector3.Distance(transform.position, destination);
            float stamina = FiresCore.Movement.Climbing.StaminaOf(m_character) / 100f;
            var bag = _companion != null ? _companion.GetInventory()?.GetStorageInventory() : null;
            bool heavy = bag != null && bag.GetTotalWeight() > 240f;
            int need = owner != null ? FiresCore.Npc.JobBoard.HomesteadNeeds.NeedFor(owner, out _) : 0;
            bool order = source == Core.UnifiedMovementAuthority.MovementSource.PlayerCommand;
            bool urgent = need >= 8 || order;
            bool open = Mathf.Abs(destination.y - transform.position.y) < 2f && Core.CompanionReach.CanSee(transform, destination + Vector3.up, out _);
            Pace p; string why;
            if (dist < 3f) { p = Pace.Walk; why = "at the target"; }
            else if (stamina < 0.3f) { p = Pace.Walk; why = $"stamina {stamina * 100f:0} %"; }
            else if (heavy) { p = Pace.Walk; why = $"heavy load {bag.GetTotalWeight():0}"; }
            else if (dist > 30f * v && urgent && open) { p = Pace.Sprint; why = order ? "order, open ground" : "need high, open ground"; }
            else if (dist >= 10f * v && (urgent || dist > 20f * v)) { p = Pace.Jog; why = urgent ? (order ? "order" : "need high") : "medium trip"; }
            else { p = Pace.Walk; why = "short hop"; }
            if (p != _pace) FiresCore.Logging.FiresLogger.LogDebug($"[Move] {m_character.m_name} {_pace.ToString().ToLower()}->{p.ToString().ToLower()}: target {dist:0} m, {why}");
            _pace = p;
            m_character.SetWalk(p == Pace.Walk);
            return p == Pace.Sprint;
        }

        // ---- unstuck ladder
        private static readonly string[] Rungs = { "jump", "double jump", "vault", "climb", "step back + re-path", "sprint around", "teleport" };
        private const float UnstuckAfter = 3f, UnstuckProgress = 0.5f, RungSeconds = 1.5f;
        private Vector3 _usGoal = new Vector3(float.NaN, 0f, 0f);
        private float _usBest, _usBestAt;
        private int _usRung = -1;
        private float _usRungAt;
        private Vector3 _usDir;
        private bool _usDouble;
        internal static int UnstuckOk, UnstuckFail, UnstuckTeleports;

        /// <summary>True while a ladder rung steers the body (the caller skips its own move this frame).</summary>
        private float _usCapUntil, _usFalseLogAt; private bool _usFalseSaid;
        private readonly System.Collections.Generic.List<float> _usLadders = new System.Collections.Generic.List<float>();
        internal static int UnstuckFalse;
        private static int s_probeMask = -1;

        // what the companion is doing and what is in front of it, and whether something solid blocks the way to the next path point
        private string Diagnose(Vector3 destination, string owner, float reach, float dist, out bool blocked)
        {
            if (s_probeMask == -1) s_probeMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid", "terrain", "vehicle");
            Vector3 pos = transform.position;
            Vector3 next = m_path != null && m_path.Count > 0 ? m_path[0] : destination;
            Vector3 dir = next - pos; dir.y = 0f; if (dir.sqrMagnitude < 0.01f) { dir = destination - pos; dir.y = 0f; }
            if (dir.sqrMagnitude < 0.01f) dir = transform.forward;
            dir.Normalize();
            float r = 0.3f;
            blocked = false; string ahead = "nothing within 1 m";
            if (Physics.CapsuleCast(pos + Vector3.up * (r + 0.3f), pos + Vector3.up * 1.4f, r, dir, out RaycastHit hit, 1f, s_probeMask, QueryTriggerInteraction.Ignore)
                && hit.collider.GetComponentInParent<Character>() == null)
            {
                blocked = true;
                ahead = $"{hit.collider.name} (layer {LayerMask.LayerToName(hit.collider.gameObject.layer)}, root {Utils.GetPrefabName(hit.collider.transform.root.gameObject)}) at {hit.distance:0.00} m";
            }
            Vector3 v = m_character.GetVelocity(); v.y = 0f;
            int pts = m_path != null ? m_path.Count : 0;
            float snow = FiresCore.Snow.SnowField.Enabled ? FiresCore.Snow.SnowField.DepthAt(pos) * FiresCore.Snow.SnowField.MaxDepthMetres : 0f;
            return $"chore {owner ?? "?"}, goal {RouteMemory.At(destination)} {dist:0.0} m (reach {reach:0.0}, {destination.y - pos.y:+0.0;-0.0} m up), path {pts} pt(s){(pts > 0 ? $" next {Vector3.Distance(pos, next):0.0} m" : "")}, speed {v.magnitude:0.00} m/s (walk {(m_character.m_walk ? "on" : "off")}, run {m_character.IsRunning()}), on ground {m_character.IsOnGround()}, ahead: {ahead}, snow {snow:0.00} m";
        }

        private bool UnstuckTick(Vector3 destination, string owner, float reach)
        {
            if (!UnstuckOn || m_character == null) return false;
            var nv = m_character.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return false;
            float dist = Vector3.Distance(transform.position, destination);
            if ((destination - _usGoal).sqrMagnitude > 1f) { _usGoal = destination; _usBest = dist; _usBestAt = Time.time; _usRung = -1; return false; }
            bool climbing = FiresCore.Movement.Climb.HasRun(m_character);
            if (dist < _usBest - UnstuckProgress || climbing)
            {
                if (_usRung >= 0) { UnstuckOk++; FiresCore.Logging.FiresLogger.LogDebug($"[Unstuck] {m_character.m_name} at {RouteMemory.At(transform.position)}: tried {Rungs[_usRung]} -> ok{(climbing ? " (climbing)" : "")}"); }
                _usBest = Mathf.Min(_usBest, dist); _usBestAt = Time.time; _usRung = -1;
                return false;
            }
            if (_usRung < 0)
            {
                if (Time.time - _usBestAt < UnstuckAfter) return false;
                // 0.2.932 (Fire 10-27: "walking across flat ground and jumping ... something else is causing them to think they're stuck";
                // 1480 [Unstuck] lines on flat y 50): ONE diagnosis line per trigger (the chore, the goal, the path, velocity vs commanded move,
                // what is in front, snow), then the gates - a goal within 2 m, nothing solid ahead, or over the per-companion cap is a FALSE
                // trigger: no move, the watch restarts
                string why = Diagnose(destination, owner, reach, dist, out bool blocked);
                bool near = dist <= Mathf.Max(2f, reach + 0.5f);
                bool capped = Time.time < _usCapUntil;
                if (near || !blocked || capped)
                {
                    UnstuckFalse++;
                    if (!_usFalseSaid || Time.time >= _usFalseLogAt) { bool firstFalse = !_usFalseSaid; _usFalseSaid = true; _usFalseLogAt = Time.time + 10f; if (firstFalse) FiresCore.Logging.FiresLogger.LogDebug($"[Unstuck] {m_character.m_name}: first false trigger this session - no move (proof the 0.2.932 gate works)"); FiresCore.Logging.FiresLogger.LogDebug($"[Unstuck] {m_character.m_name}: not stuck ({(near ? "goal within 2 m / reach" : capped ? "per-companion cap" : "nothing blocks the way ahead")}) - {why}"); }
                    _usBest = dist; _usBestAt = Time.time;
                    return false;
                }
                FiresCore.Logging.FiresLogger.LogDebug($"[Unstuck] {m_character.m_name}: STUCK - {why}");
                _usLadders.RemoveAll(t => Time.time - t > 60f); _usLadders.Add(Time.time);
                if (_usLadders.Count >= 3) _usCapUntil = Time.time + 60f;   // at most 3 ladders a minute per companion
                StartRung(0, destination);
            }
            else if (Time.time - _usRungAt > RungSeconds + (_usRung == 4 ? 1f : 0f))
            {
                UnstuckFail++;
                FiresCore.Logging.FiresLogger.LogDebug($"[Unstuck] {m_character.m_name} at {RouteMemory.At(transform.position)}: tried {Rungs[_usRung]} -> fail");
                if (_usRung + 1 >= Rungs.Length) { _usRung = -1; _usBestAt = Time.time; return false; }
                StartRung(_usRung + 1, destination);
            }
            return SteerRung();
        }

        private void StartRung(int rung, Vector3 destination)
        {
            _usRung = rung; _usRungAt = Time.time; _usDouble = false;
            Vector3 to = destination - transform.position; to.y = 0f;
            _usDir = to.sqrMagnitude > 0.01f ? to.normalized : transform.forward;
            switch (rung)
            {
                case 0: if (m_character.IsOnGround()) m_character.Jump(); break;
                case 1: if (m_character.IsOnGround()) m_character.Jump(); break;   // the second push comes in SteerRung, in the air
                case 2:
                case 3:
                    bool planned = FiresCore.Movement.Climbing.PlanClimb(m_character, transform.position, destination, out var plan, out string why);
                    if (!planned && rung == 3) planned = FiresCore.Movement.Climbing.PlanNearestWall(m_character, transform.position, destination, out plan, out why);
                    if (planned && (rung == 3 || plan.Height <= FiresCore.Movement.Climbing.MantleMaxHeight)) FiresCore.Movement.Climb.Plan(m_character, plan, "unstuck " + Rungs[rung], 6f);
                    else _usRungAt = Time.time - RungSeconds;   // nothing to vault / climb here: fail fast
                    break;
                case 4: if (m_path != null) m_path.Clear(); break;
                case 5:
                    bool clear = !Physics.CheckSphere(transform.position + Vector3.up, 2.5f, LayerMask.GetMask("piece"), QueryTriggerInteraction.Ignore);
                    if (!clear) _usRungAt = Time.time - RungSeconds;   // "sprint (on long open stretches only)": not among pieces
                    else _usDir = Quaternion.Euler(0f, (GetInstanceID() & 1) == 0 ? 70f : -70f, 0f) * _usDir;
                    break;
                case 6:
                    UnstuckTeleports++;
                    StopMoving();
                    if (_followTarget != null) _companion?.TeleportToOwner();
                    else if (_idleBehavior != null && _idleBehavior.HasHomePosition) _companion?.TeleportToDestination(_idleBehavior.HomePosition);
                    else _companion?.TeleportToOwner();
                    FiresCore.Logging.FiresLogger.LogDebug($"[Unstuck] {m_character.m_name} at {RouteMemory.At(transform.position)}: tried teleport -> ok (every move failed; to {(_followTarget != null ? "the leader" : "home")})");
                    _usRung = -1; _usBestAt = Time.time; _usGoal = new Vector3(float.NaN, 0f, 0f);
                    break;
            }
        }

        private bool SteerRung()
        {
            if (_usRung < 0) return false;
            float t = Time.time - _usRungAt;
            switch (_usRung)
            {
                case 1:
                    // companion-side double jump (Core's player double jump is local-player only): one more upward push near the apex
                    if (!_usDouble && t > 0.3f && !m_character.IsOnGround() && m_character.m_body != null)
                    {
                        _usDouble = true;
                        var vel = m_character.m_body.velocity; vel.y = Mathf.Max(vel.y, m_character.m_jumpForce * 0.8f);
                        m_character.m_body.velocity = vel + _usDir * 1.5f;
                    }
                    MoveTowards(_usDir, false);
                    return true;
                case 0: MoveTowards(_usDir, false); return true;
                case 4: if (t < 1f) { MoveTowards(-_usDir, false); return true; } return false;   // then the fresh path (m_path cleared)
                case 5: MoveTowards(_usDir, true); return true;
                default: return false;   // vault / climb: the climb engine moves the body
            }
        }

        private static float s_unstuckSummaryAt;
        private static void UnstuckSummary()
        {
            if (Time.time < s_unstuckSummaryAt) return;
            bool first = s_unstuckSummaryAt == 0f; s_unstuckSummaryAt = Time.time + 600f;
            if (first || UnstuckOk + UnstuckFail + UnstuckTeleports + UnstuckFalse == 0) return;
            FiresCore.Logging.FiresLogger.LogDebug($"[Unstuck] summary 10 min: ok {UnstuckOk}, failed rungs {UnstuckFail}, teleports {UnstuckTeleports}, false triggers (not stuck) {UnstuckFalse}");
            UnstuckOk = UnstuckFail = UnstuckTeleports = UnstuckFalse = 0;
        }
    }
}
