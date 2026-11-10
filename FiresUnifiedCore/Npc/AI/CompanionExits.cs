using System;
using HarmonyLib;
using System.Collections.Generic;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Companions out of water and pits the way the bot gets out (climbing or jumping out holds for a
    /// companion stuck in a pond too; one brain, two bodies). Every companion move ends in Character.SetMoveDir (the movement
    /// authority's apply and vanilla pathfinding alike), and CompanionPatches' prefix hands the direction here first, on the owner:
    /// <list type="bullet">
    /// <item>on a face (Core's climb has the body): into the face;</item>
    /// <item>a planned climb (a rim exit's mantle, a pit's climb): to its face, square to it (the climb engine starts it there);</item>
    /// <item>swimming: to <see cref="Swimming.ExitFor"/>'s exit (the nearest reachable rim first; it plans the mantle or climb);</item>
    /// <item>on dry ground and no nearer for <see cref="StuckSeconds"/> while it wants to move: a planned climb up the face along the
    /// way it wants to go (a pit, a ledge), where <see cref="FiresCore.Movement.Climbing.PlanClimb"/> finds one.</item>
    /// </list>
    /// </summary>
    internal static class CompanionExits
    {
        /// <summary>How far along its wanted way a swimmer's goal is taken to lie (m), when the mover gives only a direction.</summary>
        private const float TowardMetres = 20f;
        /// <summary>Wanting to move but not StuckMetres from where it was for this long (s) on dry ground: look for a climb.</summary>
        private const float StuckSeconds = 4f, StuckMetres = 0.5f;
        /// <summary>How far along the wanted way a stuck body looks for the top it should climb to (m).</summary>
        private const float PitProbeMetres = 4f;
        /// <summary>A refused pit climb is asked again no sooner than this (s).</summary>
        private const float PitRetrySeconds = 4f;
        private const float LogEvery = 5f;
        private const float ShoreHoldSeconds = 30f;   // 0.2.943

        private sealed class State
        {
            public Vector3 From;
            public float Since = -1f, NextPit, NextLog, NextRoofCheck, LastClimbAt, ShoreHoldUntil, NextShoreLog, NextCrossCheck; public string LastClimb;   // 0.2.943: + the shore hold
            public bool Swimming;
        }

        private static readonly Dictionary<Character, State> s_state = new Dictionary<Character, State>();

        /// <summary>The companion move <paramref name="dir"/> for this frame, bent toward a way out of water or a pit (owner only).</summary>
        internal static void Steer(Character body, string name, ref Vector3 dir)
        {
            if (body == null || body.IsDead() || body.m_nview == null || !body.m_nview.IsValid() || !body.m_nview.IsOwner()) return;
            Vector3 at = body.transform.position;
            if (!s_state.TryGetValue(body, out State st))
            {
                if (s_state.Count >= 64)
                {
                    var gone = new List<Character>();
                    foreach (var key in s_state.Keys) if (key == null) gone.Add(key);
                    foreach (var key in gone) s_state.Remove(key);
                }
                s_state[body] = st = new State();
            }

            if (RoofWatch(body, name, st, at)) return;

            if (FiresCore.Movement.Climb.HasRun(body))
            {
                Vector3 face = FiresCore.Movement.Climb.FaceDirection(body);
                if (face.sqrMagnitude > 0.0001f) dir = face;
                st.Since = -1f;
                return;
            }

            if (FiresCore.Movement.Climb.TicketFor(body, out FiresCore.Movement.Climbing.ClimbPlan plan))
            {
                Vector3 toFace = plan.Top - at;
                toFace.y = 0f;
                if (toFace.sqrMagnitude > 0.0001f)
                {
                    Vector3 move = toFace.normalized;
                    if (FiresCore.Movement.Climbing.FaceAhead(body, move, out RaycastHit faceHit, out _, out _))
                    {
                        Vector3 into = -faceHit.normal;
                        into.y = 0f;
                        if (into.sqrMagnitude > 0.0001f) move = into.normalized;
                    }
                    dir = move;
                }
                st.Since = -1f;
                return;
            }

            // 0.2.943 (Fire 10-27: "it could path around the water, it could swim through the water, it could jump the water"): a crossing
            // chosen on the bank is carried out to the far side, then forgotten
            if (WaterCrossing(body, name, st, at, ref dir)) return;

            if (body.IsSwimming())
            {
                Vector3 wanted = dir;
                wanted.y = 0f;
                Vector3 toward = wanted.sqrMagnitude > 0.01f ? at + wanted.normalized * TowardMetres : at;
                if (Swimming.ExitFor(body, toward, out Vector3 exit, out string why))
                {
                    Vector3 to = exit - at;
                    to.y = 0f;
                    if (!st.Swimming || Time.time >= st.NextLog)
                    {
                        st.NextLog = Time.time + LogEvery;
                        FiresLogger.LogDebug($"[CompanionExits] {name}: swimming to the exit at ({exit.x:0}, {exit.z:0}), {to.magnitude:0.0} m: {why}");
                    }
                    // 0.2.943: the goal is in water it may not swim for - once out, it holds at the shore instead of walking back in
                    if (why != null && why.Contains("no swim for it")) st.ShoreHoldUntil = Time.time + ShoreHoldSeconds;
                    // At the exit point: keep its own way in (the rim's planned mantle takes it from the face).
                    if (to.sqrMagnitude >= 0.25f) dir = to.normalized;
                }
                st.Swimming = true;
                st.Since = -1f;
                return;
            }
            if (st.Swimming)
            {
                st.Swimming = false;
                FiresLogger.LogDebug($"[CompanionExits] {name}: out of the water at ({at.x:0}, {at.y:0}, {at.z:0})");
            }

            // 0.2.943 (Fire 10-27: "companions are getting stuck when they come to the waters edge"; log 41444-41499: Knut "swimming to the
            // exit at (354, 909): the goal at (325, 901) is in water 2.2 m deep, no swim for it" -> "out of the water at (345, 47, 901)" ->
            // in again, over and over): after such an exit, for ShoreHoldSeconds a step into deep water (> 1 m) is not taken - it waits at
            // the edge (the goal may come out of the water, or the mover picks another way); no ping-pong, and the unstuck ladder sees
            // nothing solid ahead, so it does not jump
            if (Time.time < st.ShoreHoldUntil && dir.sqrMagnitude > 0.0001f)
            {
                Vector3 step = dir; step.y = 0f;
                if (step.sqrMagnitude > 0.0001f)
                {
                    Vector3 ahead = at + step.normalized * 1.5f;
                    float ground = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(ahead) : ahead.y;
                    float water = Floating.GetLiquidLevel(new Vector3(ahead.x, ground + 0.3f, ahead.z), 1f, LiquidType.Water);
                    if (water - ground > 1f)
                    {
                        if (Time.time >= st.NextShoreLog) { st.NextShoreLog = Time.time + LogEvery; FiresLogger.LogDebug($"[CompanionExits] {name}: holding at the shore ({at.x:0}, {at.z:0}): the way ahead is {water - ground:0.0} m deep and its goal is in water it may not swim for (for {st.ShoreHoldUntil - Time.time:0} s more)"); }
                        dir = Vector3.zero;
                        st.Since = -1f;
                        return;
                    }
                }
            }

            // Dry ground: a pit or a ledge it walks into without getting anywhere.
            Vector3 way = dir;
            way.y = 0f;
            if (way.sqrMagnitude < 0.01f || !body.IsOnGround() || body.InAttack() || body.IsAttached()) { st.Since = -1f; return; }
            if (st.Since < 0f || Flat(at, st.From) > StuckMetres) { st.From = at; st.Since = Time.time; return; }
            if (Time.time - st.Since < StuckSeconds || Time.time < st.NextPit) return;
            st.NextPit = Time.time + PitRetrySeconds;
            Vector3 probe = at + way.normalized * PitProbeMetres;
            if (!FiresCore.Movement.Climbing.TopSolid(probe.x, probe.z, out float top, out Collider topCol)) return;
            Vector3 goal = new Vector3(probe.x, top, probe.z);
            string reason = $"stuck {Time.time - st.Since:0} s below a {top - at.y:0.0} m rise";
            // [ghost] 0.2.868 (Fire 10-27 SFSC3 shot 578: "somehow a companion ended up on the roof?"; his log: "[CompanionExits] Thorsten:
            // climb 10.1 m at 86° up [EasyBake/Zone_5_15/standins] toward (287, 60, 939)" and Grimhild "topped out ... up [EasyBake/...
            // standins]" - the stuck-climb planned climbs up the BUILDING (its baked stand-in mesh) and a top-out puts the body on its top =
            // the roof): a rise whose top is a built piece or a baked building stand-in is never climbed; the companion paths round instead.
            if (IsBuilding(topCol) && top - at.y > 1.2f)
            {
                if (Time.time >= st.NextLog)
                {
                    st.NextLog = Time.time + LogEvery;
                    FiresLogger.LogDebug($"[CompanionExits] {name}: {reason}, climb refused: {(topCol != null && topCol.GetComponentInParent<Piece>() == null ? "EBM building stand-in" : "built piece (roof/wall)")} {Describe(topCol)}; repathing instead");
                }
                return;
            }
            if (FiresCore.Movement.Climbing.PlanClimb(body, at, goal, out FiresCore.Movement.Climbing.ClimbPlan pit, out string refusal))
            {
                FiresCore.Movement.Climb.Plan(body, pit, $"companion {reason}");
                st.LastClimb = $"{pit.Height:0.0} m up {pit.Surface}"; st.LastClimbAt = Time.time;
                FiresLogger.LogDebug($"[CompanionExits] {name}: climb {pit.Height:0.0} m at {pit.Angle:0}° up {pit.Surface} toward ({goal.x:0}, {goal.y:0}, {goal.z:0}): {reason}");
                st.Since = -1f;
            }
            else if (Time.time >= st.NextLog)
            {
                st.NextLog = Time.time + LogEvery;
                FiresLogger.LogDebug($"[CompanionExits] {name}: {reason}, no climb: {refusal}");
            }
        }

        // ---- 0.2.943: water crossing as a movement choice -------------------------------------------------------------------------
        // On the bank, with the way ahead in water deeper than DeepWater and dry ground again within MaxGap, the companion measures the
        // gap, its jump reach (run speed x air time; x DoubleReach when Core's double jump is on), a detour (a vanilla path to the far bank
        // that stays out of deep water) and the swim, and picks ONE: jump (gap <= 90 % of the reach), around (a dry path no longer than
        // 2x + 20 m), else swim (stamina >= 25 %). The choice holds until the far bank is reached or CrossSeconds pass - no ping-pong.
        // "[Water] <name>: gap X m, jump reach Y m, detour Z m, swim W m -> jump | around | swim | hold (why)" once per crossing (Debug).
        private const float DeepWater = 1f, MaxGap = 25f, DoubleReach = 1.6f, CrossSeconds = 25f;
        private sealed class Crossing { public string Mode; public Vector3 Far; public float Until; public List<Vector3> Path; public int Idx; public bool Jumped, Doubled; }
        private static readonly Dictionary<Character, Crossing> s_cross = new Dictionary<Character, Crossing>();
        private static readonly List<Vector3> s_crossPath = new List<Vector3>();

        private static float WaterDepth(Vector3 p)
        {
            float ground = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(p) : p.y;
            float water = Floating.GetLiquidLevel(new Vector3(p.x, ground + 0.3f, p.z), 1f, LiquidType.Water);
            return water - ground;
        }

        private static bool WaterCrossing(Character body, string name, State st, Vector3 at, ref Vector3 dir)
        {
            if (s_cross.TryGetValue(body, out var cr))
            {
                if (cr == null || Time.time > cr.Until || Flat(at, cr.Far) < 1.5f) { s_cross.Remove(body); if (cr != null) FiresLogger.LogDebug($"[CompanionExits] {name}: water crossing ({cr.Mode}) {(Flat(at, cr.Far) < 1.5f ? "done" : "timed out")} at ({at.x:0}, {at.z:0})"); return false; }
                Vector3 to = cr.Far - at;
                if (cr.Mode == "around" && cr.Path != null)
                {
                    while (cr.Idx < cr.Path.Count - 1 && Flat(at, cr.Path[cr.Idx]) < 1.2f) cr.Idx++;
                    to = cr.Path[cr.Idx] - at;
                }
                to.y = 0f;
                if (to.sqrMagnitude > 0.01f) dir = to.normalized;
                if (cr.Mode == "jump")
                {
                    if (!cr.Jumped && body.IsOnGround()) { body.Jump(); cr.Jumped = true; }
                    else if (cr.Jumped && !cr.Doubled && !body.IsOnGround() && body.m_body != null && body.m_body.velocity.y < 0.5f)
                    { cr.Doubled = true; FiresCore.Movement.CoreMovement.TryDoubleJumpFor(body, out _); }
                }
                return true;   // swim: the far bank, not the nearest rim; around: the dry path; jump: over
            }
            if (body.IsSwimming() || !body.IsOnGround() || Time.time < st.NextCrossCheck) return false;
            Vector3 way = dir; way.y = 0f;
            if (way.sqrMagnitude < 0.01f) return false;
            way.Normalize();
            if (WaterDepth(at + way * 1.5f) <= DeepWater) return false;
            st.NextCrossCheck = Time.time + 1f;   // at most one decision a second (it may run a path search)
            // the far bank along the way
            float gap = -1f; Vector3 far = at;
            for (float d = 1.5f; d <= MaxGap; d += 0.5f) { var p = at + way * d; if (WaterDepth(p) <= 0.3f) { gap = d; far = p; break; } }
            if (gap < 0f) return false;   // open water: the swim exit logic handles it
            if (ZoneSystem.instance != null) far.y = ZoneSystem.instance.GetGroundHeight(far);
            float g = Mathf.Abs(Physics.gravity.y) > 0.1f ? Mathf.Abs(Physics.gravity.y) : 9.81f;
            float air = 2f * body.m_jumpForce / g;
            bool dbl = FiresCore.Movement.CoreMovement.DoubleJumpFeature.IsActive;
            float reach = body.m_runSpeed * air * (dbl ? DoubleReach : 1f);
            float detour = -1f; List<Vector3> path = null;
            s_crossPath.Clear();
            if (Pathfinding.instance != null && Pathfinding.instance.GetPath(at, far, s_crossPath, Pathfinding.AgentType.Humanoid, requireFullPath: true) && s_crossPath.Count > 1)
            {
                float len = 0f; bool wet = false;
                for (int i = 1; i < s_crossPath.Count && !wet; i++)
                {
                    float seg = Vector3.Distance(s_crossPath[i - 1], s_crossPath[i]); len += seg;
                    for (float t = 0f; t <= seg; t += 1f) if (WaterDepth(Vector3.Lerp(s_crossPath[i - 1], s_crossPath[i], seg > 0f ? t / seg : 0f)) > DeepWater) { wet = true; break; }
                }
                if (!wet) { detour = len; path = new List<Vector3>(s_crossPath); }
            }
            float stamina = FiresCore.Movement.Climbing.StaminaOf(body) / 100f;
            string mode, why;
            if (gap <= reach * 0.9f) { mode = "jump"; why = $"the gap fits the jump{(dbl ? " (double jump)" : "")}"; }
            else if (detour > 0f && detour <= gap * 2f + 20f) { mode = "around"; why = "a dry path not much longer"; }
            else if (stamina >= 0.25f) { mode = "swim"; why = $"stamina {stamina * 100f:0} %"; }
            else { mode = "hold"; why = $"too far to jump, no short dry path, stamina {stamina * 100f:0} %"; }
            FiresLogger.LogDebug($"[Water] {name}: gap {gap:0.0} m, jump reach {reach:0.0} m, detour {(detour > 0f ? detour.ToString("0.0") + " m" : "none dry")}, swim {gap:0.0} m -> {mode} ({why})");
            if (mode == "hold") { st.ShoreHoldUntil = Time.time + ShoreHoldSeconds; return false; }
            s_cross[body] = new Crossing { Mode = mode, Far = far, Until = Time.time + CrossSeconds, Path = path };
            return WaterCrossing(body, name, st, at, ref dir);
        }

        // ---- 0.2.868: buildings + the roof watchdog -------------------------------------------------------------------------------
        internal static bool IsBuilding(Collider c)
        {
            if (c == null) return false;
            if (c.GetComponentInParent<Piece>() != null) return true;
            for (var t = c.transform; t != null; t = t.parent) if (t.name.IndexOf("standins", StringComparison.OrdinalIgnoreCase) >= 0) return true;   // EasyBake's baked building stand-ins
            return false;
        }
        private static System.Reflection.FieldInfo s_standInHash; private static System.Type s_standInType; private static bool s_standInLooked;
        internal static bool IsRoof(Collider c)
        {
            if (c == null) return false;
            var p = c.GetComponentInParent<Piece>();
            if (p != null) return Utils.GetPrefabName(p.gameObject).IndexOf("roof", StringComparison.OrdinalIgnoreCase) >= 0;
            if (FiresCore.Compat.EbmStandIns.Supported)   // 0.2.1218: EbmApi.StandInPrefab (no reading EBM's EbmStandIn.PrefabHash)
            {
                int ph = FiresCore.Compat.EbmStandIns.PrefabOf(c);
                var pf = ph != 0 && ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(ph) : null;
                return pf != null && pf.name.IndexOf("roof", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            if (!s_standInLooked)
            {
                s_standInLooked = true;
                s_standInType = FiresCore.Compat.OptionalTypes.Find("FiresEasyBakeMeshes.EbmStandIn");
                s_standInHash = s_standInType != null ? AccessTools.Field(s_standInType, "PrefabHash") : null;
            }
            if (s_standInType == null || s_standInHash == null) return false;
            var si = c.GetComponentInParent(s_standInType);
            if (si == null) return false;
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab((int)s_standInHash.GetValue(si)) : null;
            return prefab != null && prefab.name.IndexOf("roof", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Describe(Collider c)
        {
            if (c == null) return "?";
            var p = c.GetComponentInParent<Piece>();
            return p != null ? Utils.GetPrefabName(p.gameObject) : c.transform.root.name + "/" + c.name;
        }

        /// <summary>
        /// On a roof = standing on a built piece or a baked building stand-in, 2.5 m+ above the terrain there, with nothing solid within
        /// 3 m above its head (an upper floor has a ceiling; a roof has sky). Checked every 2 s while it moves. Recovery: vanilla's navmesh
        /// point (Pathfinding.FindValidPoint, humanoid) near the terrain under it, else beside the owner. "[Move] <name> on a roof piece
        /// <piece> at y Y: cause <the last climb / unknown>, recovered to (x, y, z)".
        /// </summary>
        private static bool RoofWatch(Character body, string name, State st, Vector3 at)
        {
            if (Time.time < st.NextRoofCheck || FiresCore.Movement.Climb.HasRun(body) || !body.IsOnGround()) return false;
            st.NextRoofCheck = Time.time + 2f;
            // 0.2.874 (soak #1: 57 recoveries, 22 of them a companion in an upstairs BED and others on an EBM floor stand-in 2.8 m up): never
            // while attached (bed, chair, ladder), and only on an actual ROOF - a piece whose prefab name has "roof", or an EBM stand-in
            // standing in for one (its EbmStandIn.PrefabHash)
            if (body.IsAttached() || body.InBed()) return false;
            int mask = FiresCore.World.Surface.Mask;
            if (!Physics.Raycast(at + Vector3.up * 0.5f, Vector3.down, out RaycastHit under, 1.5f, mask, QueryTriggerInteraction.Ignore) || !IsRoof(under.collider)) return false;
            float terrain = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(at) : at.y;
            if (at.y - terrain < 2.5f) return false;
            if (Physics.Raycast(at + Vector3.up * 1.9f, Vector3.up, 3f, mask, QueryTriggerInteraction.Ignore)) return false;   // a ceiling: an upper floor
            Vector3 to = new Vector3(at.x, terrain, at.z);
            bool nav = Pathfinding.instance != null && Pathfinding.instance.FindValidPoint(out to, to, 10f, Pathfinding.AgentType.Humanoid) && Mathf.Abs(to.y - terrain) < 2f;
            if (!nav)
            {
                var cc = body.GetComponent<CompanionController>(); var owner = cc != null ? Player.GetAllPlayers().Find(p => p != null && p.GetPlayerID() == cc.ownerPlayerId) : null;
                if (owner == null) { FiresLogger.LogDebug($"[Move] {name} on a roof piece {Describe(under.collider)} at y {at.y:0.0}: no ground spot near and no owner here; left in place"); return false; }
                to = owner.transform.position + owner.transform.right * 1.5f;
            }
            string cause = st.LastClimb != null && Time.time - st.LastClimbAt < 120f ? $"climb {Time.time - st.LastClimbAt:0} s ago ({st.LastClimb})" : "unknown (no companion climb in the last 2 min: a snap / teleport / spawn put it there)";
            body.transform.position = to;
            var rb = body.GetComponent<Rigidbody>();
            if (rb != null) { rb.position = to; rb.velocity = Vector3.zero; }
            st.Since = -1f;
            FiresLogger.LogDebug($"[Move] {name} on a roof piece {Describe(under.collider)} at y {at.y:0.0} ({at.y - terrain:0.0} m above the ground): cause {cause}, recovered to ({to.x:0}, {to.y:0.0}, {to.z:0}) ({(nav ? "navmesh by the building" : "beside the owner")})");
            return true;
        }

        /// <summary>Drop a body's state (it left the world).</summary>
        internal static void Forget(Character body)
        {
            if (body != null) s_state.Remove(body);
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
