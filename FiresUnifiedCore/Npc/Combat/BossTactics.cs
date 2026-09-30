using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// What to do when a boss starts one of its big attacks (Fire 2026-09-29: boss fights the bot and its companions survive;
    /// [seasons] Tools\BOSS_DRILL.md). Keyed by the boss's attack item prefab (Eikthyr_charge, gd_king_stomp …) read from the
    /// attack in progress; each maps to a way out: step sideways, leave the forward cone, leave the radius. CombatAdvisor turns
    /// the answer into a move (and a dodge roll where it fits) and logs "dodge: &lt;attack&gt; → &lt;response&gt;". The rows come from
    /// the ObjectDB export; the first boss runs print the attack names really seen, and new rows are one line each.
    /// </summary>
    public static class BossTactics
    {
        public enum Response { Sidestep, LeaveCone, LeaveRadius }

        public struct Rule
        {
            public Response Response;
            /// <summary>LeaveCone / LeaveRadius: how far the danger reaches (m).</summary>
            public float Range;
            /// <summary>LeaveCone: the cone's half-angle (degrees) off the boss's facing.</summary>
            public float HalfAngle;

            public override string ToString() =>
                Response == Response.Sidestep ? "sidestep"
                : Response == Response.LeaveCone ? $"leave the cone ({HalfAngle * 2f:0} deg, {Range:0} m)"
                : $"leave the radius ({Range:0} m)";
        }

        private static readonly Dictionary<string, Rule> s_rules = new Dictionary<string, Rule>
        {
            // Eikthyr (BOSS_DRILL §2): the charge runs straight at you, the antler lightning goes forward, the stomp is round him.
            ["Eikthyr_charge"] = new Rule { Response = Response.Sidestep },
            ["Eikthyr_antler"] = new Rule { Response = Response.LeaveCone, Range = 25f, HalfAngle = 30f },
            ["Eikthyr_stomp"] = new Rule { Response = Response.LeaveRadius, Range = 7f },
            // The Elder: roots come up under you (keep moving), the stomp and punch are close, the shot is a line.
            ["gd_king_rootspawn"] = new Rule { Response = Response.Sidestep },
            ["gd_king_stomp"] = new Rule { Response = Response.LeaveRadius, Range = 9f },
            ["gd_king_punch"] = new Rule { Response = Response.LeaveRadius, Range = 6f },
            ["gd_king_shoot"] = new Rule { Response = Response.Sidestep },
            ["gd_king_scream"] = new Rule { Response = Response.LeaveRadius, Range = 10f },
        };

        /// <summary>The rule for an attack prefab name, if the table has one.</summary>
        public static bool For(string attack, out Rule rule)
        {
            rule = default;
            return !string.IsNullOrEmpty(attack) && s_rules.TryGetValue(attack, out rule);
        }

        /// <summary>Adds or replaces a row (a drill correcting a name, a mod's boss).</summary>
        public static void Set(string attack, Rule rule)
        {
            if (!string.IsNullOrEmpty(attack)) s_rules[attack] = rule;
        }

        /// <summary>
        /// The attack item prefab <paramref name="boss"/> is attacking with right now, or null when it isn't attacking. On the boss's
        /// owner: its attack in progress; elsewhere: its (synced) attack animation plus the right-hand item its record carries.
        /// </summary>
        public static string CurrentAttack(Character boss)
        {
            if (!(boss is Humanoid humanoid) || !boss.InAttack()) return null;
            ItemDrop.ItemData weapon = humanoid.m_currentAttack?.m_weapon;
            if (weapon?.m_dropPrefab != null) return weapon.m_dropPrefab.name;
            ZDO zdo = boss.m_nview != null && boss.m_nview.IsValid() ? boss.m_nview.GetZDO() : null;
            int hash = zdo != null ? zdo.GetInt(ZDOVars.s_rightItem) : 0;
            GameObject prefab = hash != 0 && ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(hash) : null;
            return prefab != null ? prefab.name : null;
        }

        /// <summary>
        /// The way out of <paramref name="rule"/> for a body at <paramref name="self"/>, or zero when it is already safe.
        /// <paramref name="side"/> (+1/-1) picks the sidestep side.
        /// </summary>
        public static Vector3 Escape(Rule rule, Character boss, Vector3 self, float side)
        {
            Vector3 from = boss.transform.position;
            Vector3 away = self - from;
            away.y = 0f;
            float distance = away.magnitude;
            Vector3 out1 = distance > 0.01f ? away / distance : -boss.transform.forward;
            Vector3 across = Vector3.Cross(Vector3.up, out1) * side;
            switch (rule.Response)
            {
                case Response.Sidestep:
                    return across;
                case Response.LeaveRadius:
                    return distance < rule.Range ? out1 : Vector3.zero;
                case Response.LeaveCone:
                    Vector3 facing = boss.transform.forward;
                    facing.y = 0f;
                    if (distance >= rule.Range || Vector3.Angle(facing, out1) >= rule.HalfAngle) return Vector3.zero;
                    // Out of the cone the short way: across the boss's facing, toward the side we're already on.
                    float onRight = Vector3.Dot(Vector3.Cross(Vector3.up, facing), out1) >= 0f ? 1f : -1f;
                    return Vector3.Cross(Vector3.up, facing).normalized * onRight;
                default:
                    return Vector3.zero;
            }
        }
    }
}
