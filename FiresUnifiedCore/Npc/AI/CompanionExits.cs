using System.Collections.Generic;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Companions out of water and pits the way the bot gets out (0.2.283, the lead: Fire's rule "climbing or jumping out" holds for a
    /// companion stuck in the pond too; one brain, two bodies). Every companion move ends in Character.SetMoveDir (the movement
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

        private sealed class State
        {
            public Vector3 From;
            public float Since = -1f, NextPit, NextLog;
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

            // Dry ground: a pit or a ledge it walks into without getting anywhere.
            Vector3 way = dir;
            way.y = 0f;
            if (way.sqrMagnitude < 0.01f || !body.IsOnGround() || body.InAttack() || body.IsAttached()) { st.Since = -1f; return; }
            if (st.Since < 0f || Flat(at, st.From) > StuckMetres) { st.From = at; st.Since = Time.time; return; }
            if (Time.time - st.Since < StuckSeconds || Time.time < st.NextPit) return;
            st.NextPit = Time.time + PitRetrySeconds;
            Vector3 probe = at + way.normalized * PitProbeMetres;
            if (!FiresCore.Movement.Climbing.TopSolid(probe.x, probe.z, out float top, out _)) return;
            Vector3 goal = new Vector3(probe.x, top, probe.z);
            string reason = $"stuck {Time.time - st.Since:0} s below a {top - at.y:0.0} m rise";
            if (FiresCore.Movement.Climbing.PlanClimb(body, at, goal, out FiresCore.Movement.Climbing.ClimbPlan pit, out string refusal))
            {
                FiresCore.Movement.Climb.Plan(body, pit, $"companion {reason}");
                FiresLogger.LogDebug($"[CompanionExits] {name}: climb {pit.Height:0.0} m at {pit.Angle:0}° up {pit.Surface} toward ({goal.x:0}, {goal.y:0}, {goal.z:0}): {reason}");
                st.Since = -1f;
            }
            else if (Time.time >= st.NextLog)
            {
                st.NextLog = Time.time + LogEvery;
                FiresLogger.LogDebug($"[CompanionExits] {name}: {reason}, no climb: {refusal}");
            }
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
