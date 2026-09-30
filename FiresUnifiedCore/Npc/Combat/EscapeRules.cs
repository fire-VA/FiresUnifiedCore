using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Never cornered, and fighting a group one at a time (COMBAT_TACTICS §2-§3; Fire, R72 Crypt4: "Retreat, run past them, split
    /// them up to handle one at a time if he can"). The body keeps a breadcrumb of where it came from (a point every
    /// <see cref="CrumbStep"/> m, the last <see cref="CrumbCount"/>). From that:
    /// - <see cref="RunPastGap"/>: cornered, the widest gap between the foes round it that is free of walls, to sprint through;
    /// - <see cref="PullPoint"/>: three or more coming, a crumb back along the way in to back off toward until they string out;
    /// - <see cref="Chokepoint"/>: three or more indoors (a dungeon), a crumb within 15 m behind where the way is at most
    ///   <see cref="ChokeWidth"/> m wide, to hold so only one or two can reach.
    /// Crumbs are kept for the local player (the body this peer drives); companions get them from their own movement later.
    /// </summary>
    public static class EscapeRules
    {
        public const float CrumbStep = 2f, ChokeWidth = 3f, ChokeSearch = 15f, PullBack = 8f, GapMinDegrees = 70f;
        public const int CrumbCount = 20;
        // Dungeons are built far above the world (y ~5000).
        private const float DungeonHeight = 3000f;

        private static readonly List<Vector3> s_crumbs = new List<Vector3>();
        private static int s_mask;
        private static int Mask => s_mask != 0 ? s_mask : (s_mask = AI.Perception.ObstacleMask | LayerMask.GetMask("terrain"));

        /// <summary>The local player's way in, oldest first (read only).</summary>
        public static IReadOnlyList<Vector3> Crumbs => s_crumbs;

        /// <summary>Inside a dungeon (Valheim builds them far above the world).</summary>
        public static bool Indoors(Character body) => body != null && body.transform.position.y > DungeonHeight;

        private static void Record(Player me)
        {
            Vector3 at = me.transform.position;
            if (s_crumbs.Count > 0)
            {
                Vector3 last = s_crumbs[s_crumbs.Count - 1];
                // A teleport (a dungeon door, a portal): a new trail.
                if ((at - last).sqrMagnitude > 50f * 50f) s_crumbs.Clear();
                else if ((at - last).sqrMagnitude < CrumbStep * CrumbStep) return;
            }
            s_crumbs.Add(at);
            if (s_crumbs.Count > CrumbCount) s_crumbs.RemoveAt(0);
        }

        /// <summary>
        /// Cornered with foes round it: the flat direction through the widest gap between their bearings (at least
        /// <see cref="GapMinDegrees"/>°) that has no wall within 2.5 m. False: no gap to run through.
        /// </summary>
        public static bool RunPastGap(Character body, IList<Character> foes, out Vector3 dir, out string why)
        {
            dir = Vector3.zero;
            why = null;
            if (body == null || foes == null || foes.Count == 0) return false;
            Vector3 at = body.transform.position;
            s_bearings.Clear();
            foreach (var foe in foes)
            {
                if (foe == null) continue;
                Vector3 to = foe.transform.position - at;
                to.y = 0f;
                if (to.sqrMagnitude < 0.0001f) continue;
                s_bearings.Add(Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg);
            }
            if (s_bearings.Count == 0) return false;
            s_bearings.Sort();
            float bestGap = 0f, bestMid = 0f;
            for (int i = 0; i < s_bearings.Count; i++)
            {
                float a = s_bearings[i];
                float b = i + 1 < s_bearings.Count ? s_bearings[i + 1] : s_bearings[0] + 360f;
                float gap = b - a;
                if (gap <= bestGap) continue;
                float mid = a + gap / 2f;
                Vector3 d = new Vector3(Mathf.Sin(mid * Mathf.Deg2Rad), 0f, Mathf.Cos(mid * Mathf.Deg2Rad));
                if (Blocked(body, d, 2.5f)) continue;
                bestGap = gap;
                bestMid = mid;
            }
            if (bestGap < GapMinDegrees) { why = $"widest free gap {bestGap:0}° < {GapMinDegrees:0}°"; return false; }
            dir = new Vector3(Mathf.Sin(bestMid * Mathf.Deg2Rad), 0f, Mathf.Cos(bestMid * Mathf.Deg2Rad));
            why = $"a {bestGap:0}° gap between {s_bearings.Count} foe(s)";
            return true;
        }

        /// <summary>
        /// A crumb about <see cref="PullBack"/> m back along the way in, away from the foes (its direction from the body must be
        /// more than 90° off the nearest foe's). False: no such trail.
        /// </summary>
        public static bool PullPoint(Character body, Character nearest, out Vector3 point)
        {
            point = Vector3.zero;
            if (body == null || s_crumbs.Count < 2) return false;
            Vector3 at = body.transform.position;
            Vector3 toFoe = nearest != null ? nearest.transform.position - at : Vector3.zero;
            toFoe.y = 0f;
            for (int i = s_crumbs.Count - 1; i >= 0; i--)
            {
                Vector3 c = s_crumbs[i];
                Vector3 to = c - at;
                to.y = 0f;
                if (to.magnitude < PullBack) continue;
                if (toFoe.sqrMagnitude > 0.0001f && Vector3.Angle(to, toFoe) < 90f) continue;
                point = c;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Indoors, a crumb within <see cref="ChokeSearch"/> m behind where the way is at most <see cref="ChokeWidth"/> m wide
        /// (measured across the trail), nearest first. False: none.
        /// </summary>
        public static bool Chokepoint(Character body, out Vector3 point, out float width)
        {
            point = Vector3.zero;
            width = 0f;
            if (!Indoors(body) || s_crumbs.Count < 2) return false;
            Vector3 at = body.transform.position;
            for (int i = s_crumbs.Count - 1; i >= 1; i--)
            {
                Vector3 c = s_crumbs[i];
                if (Vector3.Distance(c, at) > ChokeSearch) break;
                Vector3 along = c - s_crumbs[i - 1];
                along.y = 0f;
                if (along.sqrMagnitude < 0.0001f) continue;
                Vector3 side = Vector3.Cross(Vector3.up, along.normalized);
                Vector3 chest = c + Vector3.up * 1f;
                float w = Free(chest, side, 4f) + Free(chest, -side, 4f);
                if (w > ChokeWidth) continue;
                point = c;
                width = w;
                return true;
            }
            return false;
        }

        private static bool Blocked(Character body, Vector3 dir, float distance) =>
            Physics.SphereCast(body.GetCenterPoint(), Mathf.Max(0.2f, body.GetRadius() * 0.8f), dir, out RaycastHit hit, distance, Mask,
                QueryTriggerInteraction.Ignore) && hit.normal.y < 0.5f;

        private static float Free(Vector3 from, Vector3 dir, float distance) =>
            Physics.Raycast(from, dir, out RaycastHit hit, distance, Mask, QueryTriggerInteraction.Ignore) ? hit.distance : distance;

        private static readonly List<float> s_bearings = new List<float>();

        [HarmonyPatch(typeof(Player), "Update")]
        private static class Player_Update_Crumbs
        {
            private static void Postfix(Player __instance)
            {
                if (__instance == Player.m_localPlayer && !__instance.IsDead()) Record(__instance);
            }
        }
    }
}
