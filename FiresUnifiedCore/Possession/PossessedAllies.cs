using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Possession
{
    /// <summary>
    /// Tames and party treat a possessed form as an ally (a tamed Wolf used to bite a player's Boar form to
    /// death). A creature a player possesses carries that player's id on its ZDO (PossessionRules.PossessedKey, synced), so every peer knows:
    /// - tamed creatures (and companions, which are tamed) are never its enemy, nor it theirs (BaseAI.IsEnemy, wherever the tame's AI runs);
    /// - other players' hits on it are dropped unless PvP is on for both, the same rule as hitting the possessor himself.
    /// The possessed form still fights wild creatures (the shared engine's own IsEnemy rule).
    /// </summary>
    internal static class PossessedAllies
    {
        internal static long PossessorOf(Character c)
        {
            if (c == null || c is Player) return 0L;
            ZNetView view = c.GetComponent<ZNetView>();
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            return zdo != null ? zdo.GetLong(PossessionRules.PossessedKey, 0L) : 0L;
        }

        [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.IsEnemy), typeof(Character), typeof(Character))]
        private static class BaseAI_IsEnemy_PossessedAllies
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Character a, Character b, ref bool __result)
            {
                if (!__result || a == null || b == null) return;
                if ((PossessorOf(a) != 0L && b.IsTamed()) || (PossessorOf(b) != 0L && a.IsTamed())) __result = false;
            }
        }

        private static float s_nextLog;

        [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
        private static class Character_Damage_PossessedAllies
        {
            private static bool Prefix(Character __instance, HitData hit)
            {
                if (hit == null) return true;
                long possessor = PossessorOf(__instance);
                if (possessor == 0L) return true;
                if (!(hit.GetAttacker() is Player attacker) || attacker.GetPlayerID() == possessor) return true;
                Player owner = Player.GetPlayer(possessor);
                if (attacker.IsPVPEnabled() && owner != null && owner.IsPVPEnabled()) return true;
                if (Time.time >= s_nextLog)
                {
                    s_nextLog = Time.time + 2f;
                    FiresLogger.LogDebug($"[Possession] {attacker.GetPlayerName()}'s hit on {__instance.m_name} dropped: it is {(owner != null ? owner.GetPlayerName() : possessor.ToString())}'s possessed form (PvP off)");
                }
                return false;
            }
        }
    }
}
