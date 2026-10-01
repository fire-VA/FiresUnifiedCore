using HarmonyLib;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>
    /// Core's own record of a player body's death (0.2.221; R90 run 4: the bot died to a Skeleton ★2 and nothing learned from it,
    /// since only FDT's drill path reported deaths). On the machine that owns the player (where Player.OnDeath does its work), the
    /// killer from the last hit goes to EnemyMemory.Died: the foe's spot and World.Danger, which ThreatLevel then weighs on sight.
    /// A death to falling, drowning or the world (no attacker) records nothing. The health before the last hit is taken in a
    /// Character.ApplyDamage prefix, since vanilla keeps the hit but not what it took.
    /// </summary>
    [HarmonyPatch]
    internal static class DeathRecorder
    {
        private static float s_healthBefore, s_maxBefore, s_damage;
        private static Character s_damaged;

        [HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
        [HarmonyPrefix]
        private static void ApplyDamage_Prefix(Character __instance, HitData hit)
        {
            if (hit == null) return;
            // 0.2.247: who hit which body when (vanilla's m_lastHit has no time), for FireScare's "the torch isn't keeping them off".
            FiresCore.Npc.Combat.FireScare.NoteHit(__instance, hit.GetAttacker());
            if (!(__instance is Player)) return;
            s_damaged = __instance;
            s_healthBefore = __instance.GetHealth();
            s_maxBefore = __instance.GetMaxHealth();
            s_damage = hit.GetTotalDamage();
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnDeath))]
        [HarmonyPostfix]
        private static void OnDeath_Postfix(Player __instance)
        {
            try
            {
                if (__instance == null || __instance.m_nview == null || !__instance.m_nview.IsOwner()) return;
                Character killer = __instance.m_lastHit?.GetAttacker();
                if (killer == null || killer == __instance || killer is Player) return;
                bool known = s_damaged == __instance;
                float damage = known ? s_damage : __instance.m_lastHit.GetTotalDamage();
                float max = known ? s_maxBefore : __instance.GetMaxHealth();
                float before = known ? s_healthBefore : damage;
                EnemyMemory.Died(__instance, killer, damage, max, before);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[EnemyMemory] couldn't record the death: {ex.Message}");
            }
        }
    }
}
