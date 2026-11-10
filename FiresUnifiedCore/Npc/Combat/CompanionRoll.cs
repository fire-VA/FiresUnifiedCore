using System.Collections.Generic;
using FiresCore.Npc;
using HarmonyLib;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// A companion's roll counts as a dodge to vanilla, as a player's does: Character.AddRootMotion only takes the roll
    /// clip's motion during InDodge, and vanilla stops steering then. Character.InDodge is false for everything but
    /// Player (its override never reaches these patches), so non-players cost one int check while nobody rolls.
    /// </summary>
    internal static class CompanionRoll
    {
        // Fire 11-03: i-frames "just like players": from the start of the roll to the clip's DodgeMortal event, which vanilla only passes to a Player.
        internal const bool RollInvincible = true;

        // Until when, so a roll whose coroutine died with its object or a disable can never leave InDodge on.
        private const float RollMaxSeconds = 1.5f;
        private static readonly Dictionary<Character, float> s_rolling = new Dictionary<Character, float>();
        private static readonly HashSet<Character> s_invincible = new HashSet<Character>();

        internal static void Begin(Character c)
        {
            if (c == null) return;
            s_invincible.RemoveWhere(x => x == null);
            if (s_rolling.Count > 8) { foreach (var k in new List<Character>(s_rolling.Keys)) if (k == null) s_rolling.Remove(k); }
            s_rolling[c] = UnityEngine.Time.time + RollMaxSeconds;
            s_invincible.Add(c);
        }

        internal static void End(Character c)
        {
            if (c == null) return;
            s_rolling.Remove(c);
            s_invincible.Remove(c);
        }

        private static bool Live(Character c) => s_rolling.TryGetValue(c, out float until) && UnityEngine.Time.time < until;

        internal static bool IsRolling(Character c) => s_rolling.Count > 0 && Live(c);

        // [ghost] 11-03, LOG ONLY: a hit's size before armour (is it big when it arrives?), and who gives health back after a companion reads 0 (Verdandi did, repeatedly).
        private static readonly HashSet<Character> s_atZero = new HashSet<Character>();

        [HarmonyPatch(typeof(Character), nameof(Character.RPC_Damage))]
        private static class RawHitTrace
        {
            private static void Prefix(Character __instance, HitData hit)
            {
                if (hit == null || __instance is Player || __instance.GetComponent<CompanionController>() == null) return;
                float raw = hit.GetTotalDamage();
                if (raw > 40f) FiresCore.Logging.FiresLogger.LogDebug($"[CombatFight] {__instance.m_name}: raw hit {raw:0.0} before armour, attacker {hit.GetAttacker()?.m_name ?? "?"}, type {hit.m_hitType}, block ok {hit.m_blockable}, health {__instance.GetHealth():0}/{__instance.GetMaxHealth():0}");
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.SetHealth))]
        private static class ZeroHealthTrace
        {
            private static void Prefix(Character __instance, float health)
            {
                if (__instance is Player) return;
                if (health <= 0f) { if (__instance.GetComponent<CompanionController>() != null) s_atZero.Add(__instance); return; }
                if (s_atZero.Count > 0 && s_atZero.Remove(__instance))
                    FiresCore.Logging.FiresLogger.LogDebug($"[CombatFight] {__instance.m_name}: health from 0 to {health:0}, dead {__instance.IsDead()}, by {new System.Diagnostics.StackTrace(1, false)}".Replace("\r", "").Replace("\n", " < "));
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.InDodge))]
        private static class InDodgePatch
        {
            private static void Postfix(Character __instance, ref bool __result)
            {
                if (s_rolling.Count > 0 && Live(__instance)) __result = true;
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.IsDodgeInvincible))]
        private static class InvinciblePatch
        {
            private static void Postfix(Character __instance, ref bool __result)
            {
                if (RollInvincible && s_invincible.Count > 0 && s_invincible.Contains(__instance) && Live(__instance)) __result = true;
            }
        }

        [HarmonyPatch(typeof(CharacterAnimEvent), nameof(CharacterAnimEvent.DodgeMortal))]
        private static class DodgeMortalPatch
        {
            private static void Postfix(CharacterAnimEvent __instance)
            {
                if (s_invincible.Count == 0) return;
                var c = __instance.GetComponentInParent<Character>();
                if (c != null && s_invincible.Remove(c))
                    FiresCore.Logging.FiresLogger.LogDebug($"[CompanionDodge] {c.m_name}: roll i-frames end (clip's DodgeMortal)");
            }
        }
    }
}
