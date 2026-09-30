using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Who did what in a fight, per character, for the drills' evidence (a boss or dungeon run: each body's share of the damage
    /// and the heals). Counted where the numbers are real: damage on the ATTACKER's machine as its hit leaves (Core's one
    /// Character.Damage prefix, after every scaling), heals where Core applies them (AbilityHeals). So a body's line is counted
    /// on the peer that drives it: the bot on its own client, a companion on its owner's. Read-only for callers; <see cref="Reset"/>
    /// between runs. Kills are the attacker's estimate (the hit takes the victim's remaining health), not the victim's word.
    /// </summary>
    public static class CombatStats
    {
        /// <summary>One character's totals since the last reset.</summary>
        public struct Totals
        {
            public float DamageDealt;
            public int Hits;
            public int Kills;
            public float HealingDone;
            public int HealsGiven;

            public override string ToString() =>
                $"damage {DamageDealt:0} in {Hits} hit(s), {Kills} kill(s), healed {HealingDone:0} in {HealsGiven} heal(s)";
        }

        private static readonly Dictionary<ZDOID, Totals> s_totals = new Dictionary<ZDOID, Totals>();
        private static bool s_healsHooked;

        /// <summary>The totals of <paramref name="who"/> counted on this peer (zero when none).</summary>
        public static Totals Of(Character who)
        {
            if (who == null) return default;
            return s_totals.TryGetValue(who.GetZDOID(), out Totals t) ? t : default;
        }

        /// <summary>Forget every total (call at the start of a run).</summary>
        public static void Reset() => s_totals.Clear();

        internal static void OnHit(Character victim, HitData hit)
        {
            if (victim == null || hit == null) return;
            HookHeals();
            Character attacker = hit.GetAttacker();
            if (attacker == null || attacker == victim) return;
            float damage = hit.GetTotalDamage();
            if (damage <= 0f) return;
            ZDOID id = attacker.GetZDOID();
            if (id.IsNone()) return;
            s_totals.TryGetValue(id, out Totals t);
            t.DamageDealt += damage;
            t.Hits++;
            if (!victim.IsDead() && victim.GetHealth() > 0f && damage >= victim.GetHealth()) t.Kills++;
            s_totals[id] = t;
        }

        private static void HookHeals()
        {
            if (s_healsHooked) return;
            s_healsHooked = true;
            Archetypes.AbilityHeals.OnHealApplied += (healer, target, amount) =>
            {
                if (healer == null || amount <= 0f) return;
                ZDOID id = healer.GetZDOID();
                if (id.IsNone()) return;
                s_totals.TryGetValue(id, out Totals t);
                t.HealingDone += amount;
                t.HealsGiven++;
                s_totals[id] = t;
            };
        }

        [HarmonyPatch(typeof(Game), "Start")]
        private static class Game_Start_Reset
        {
            private static void Postfix()
            {
                s_totals.Clear();
                HookHeals();
            }
        }
    }
}
