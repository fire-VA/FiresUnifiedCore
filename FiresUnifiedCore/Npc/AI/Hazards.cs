using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Ground that hurts to stand on (Core 0.2.220; R90: the bot's terrain route ran through the base campfire and it burned from
    /// 25 to 16 HP, "hit by EnemyHit for 10.0 (fire)"). A hazard is a static Aoe that deals damage: a campfire's or hearth's flames,
    /// cinder fire, a trap. Those are what hurt; torches and heat areas don't, so they don't count (a wall torch by a door must not
    /// close the doorway). The walkers keep <see cref="Margin"/> clear: TerrainPlanner refuses cells inside one, PathWalker drops a
    /// navmesh path through one, and a goal inside one moves to its edge (<see cref="SafeGoal"/>).
    /// </summary>
    public static class Hazards
    {
        /// <summary>Room kept between a hazard's damage area and a body's centre (m).</summary>
        public const float Margin = 0.8f;
        /// <summary>The list is rebuilt at most this often (s).</summary>
        public const float RefreshSeconds = 3f;
        private const float MaxRadius = 3f;
        private const float Height = 2.5f;
        private const float Sample = 0.5f;

        private struct Spot
        {
            public Vector3 Pos;
            public float Radius;
            public string What;
        }

        private static readonly List<Spot> s_spots = new List<Spot>();
        private static float s_refreshed = -999f;

        /// <summary>Whether <paramref name="p"/> is inside a hazard's damage area plus <see cref="Margin"/>; <paramref name="what"/> names it.</summary>
        public static bool Inside(Vector3 p, out string what)
        {
            Refresh();
            foreach (Spot s in s_spots)
                if (Mathf.Abs(p.y - s.Pos.y) <= Height && Flat(p, s.Pos) <= s.Radius + Margin) { what = s.What; return true; }
            what = null;
            return false;
        }

        /// <summary>The first point of the flat line a→b (every 0.5 m) inside a hazard, if any.</summary>
        public static bool Crosses(Vector3 a, Vector3 b, out Vector3 at, out string what)
        {
            Refresh();
            at = a;
            what = null;
            if (s_spots.Count == 0) return false;
            float length = Flat(a, b);
            int steps = Mathf.Max(1, Mathf.CeilToInt(length / Sample));
            for (int i = 0; i <= steps; i++)
            {
                Vector3 p = Vector3.Lerp(a, b, i / (float)steps);
                if (Inside(p, out what)) { at = p; return true; }
            }
            return false;
        }

        /// <summary>
        /// <paramref name="goal"/> moved to the edge of any hazard it lies in, on the side of <paramref name="from"/> (unchanged when
        /// clear; <paramref name="what"/> names the last hazard it was moved out of).
        /// </summary>
        public static Vector3 SafeGoal(Vector3 goal, Vector3 from, out string what)
        {
            Refresh();
            what = null;
            for (int pass = 0; pass < 3; pass++)   // between two fires: out of each in turn
            {
                bool moved = false;
                foreach (Spot s in s_spots)
                {
                    float edge = s.Radius + Margin + 0.2f;
                    if (Mathf.Abs(goal.y - s.Pos.y) > Height || Flat(goal, s.Pos) >= edge - 0.1f) continue;
                    Vector3 away = from - s.Pos;
                    away.y = 0f;
                    if (away.sqrMagnitude < 0.01f) { away = goal - s.Pos; away.y = 0f; }
                    if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
                    Vector3 out_ = s.Pos + away.normalized * edge;
                    out_.y = goal.y;
                    goal = out_;
                    what = s.What;
                    moved = true;
                }
                if (!moved) break;
            }
            return goal;
        }

        private static void Refresh()
        {
            if (Time.time >= s_refreshed && Time.time - s_refreshed < RefreshSeconds) return;
            s_refreshed = Time.time;
            s_spots.Clear();
            foreach (Aoe aoe in Object.FindObjectsByType<Aoe>(FindObjectsSortMode.None))
            {
                if (aoe == null || !aoe.isActiveAndEnabled || aoe.m_damage.GetTotalDamage() <= 0f) continue;
                // A creature's attack or a projectile's blast moves with it: combat's business, not the route's.
                if (aoe.GetComponentInParent<Character>() != null || aoe.GetComponentInParent<Projectile>() != null) continue;
                Vector3 pos = aoe.transform.position;
                float radius = aoe.m_radius;
                // Where Aoe.CheckHits / OnTriggerStay look: its trigger colliders, else its box, else its sphere.
                Collider trigger = aoe.m_useTriggers ? aoe.GetComponent<Collider>() : null;
                if (trigger != null)
                {
                    pos = trigger.bounds.center;
                    radius = Mathf.Max(trigger.bounds.extents.x, trigger.bounds.extents.z);
                }
                else if (aoe.m_useCollider != null)
                {
                    pos = aoe.transform.TransformPoint(aoe.m_useCollider.center);
                    Vector3 size = Vector3.Scale(aoe.m_useCollider.size, aoe.transform.lossyScale);
                    radius = Mathf.Max(Mathf.Abs(size.x), Mathf.Abs(size.z)) * 0.5f;
                }
                ZNetView view = aoe.GetComponentInParent<ZNetView>();
                string name = view != null ? Utils.GetPrefabName(view.gameObject) : Utils.GetPrefabName(aoe.gameObject);
                s_spots.Add(new Spot { Pos = pos, Radius = Mathf.Clamp(radius, 0.3f, MaxRadius), What = $"{name}'s damage at ({pos.x:0}, {pos.z:0})" });
            }
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
