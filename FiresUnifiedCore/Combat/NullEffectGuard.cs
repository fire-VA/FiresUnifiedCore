using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Combat
{
    /// <summary>
    /// "ArgumentException: The Object you want to instantiate is null" from Attack.Update -> EffectList.Create
    /// (258x in one session). An EffectList entry with a null m_prefab makes vanilla Instantiate throw every time the
    /// attack plays. This drops the null entries from the list once, and names the attacker (prefab + attack animation)
    /// in one Debug line per list so the culprit can be fixed at the source.
    /// </summary>
    [HarmonyPatch]
    public static class NullEffectGuard
    {
        // The attack whose Update is running (set by the Attack.Update prefix, cleared by its finalizer).
        private static Attack s_currentAttack;
        private static readonly HashSet<EffectList> s_reported = new HashSet<EffectList>();

        [HarmonyPatch(typeof(Attack), "Update")]
        [HarmonyPrefix]
        private static void Attack_Update_Prefix(Attack __instance) => s_currentAttack = __instance;

        [HarmonyPatch(typeof(Attack), "Update")]
        [HarmonyFinalizer]
        private static void Attack_Update_Finalizer() => s_currentAttack = null;

        [HarmonyPatch(typeof(EffectList), nameof(EffectList.Create))]
        [HarmonyPrefix]
        private static void EffectList_Create_Prefix(EffectList __instance)
        {
            var list = __instance?.m_effectPrefabs;
            if (list == null || list.Length == 0) return;

            int nulls = 0;
            for (int i = 0; i < list.Length; i++)
                if (list[i] == null || list[i].m_prefab == null) nulls++;
            if (nulls == 0) return;

            var kept = new List<EffectList.EffectData>(list.Length - nulls);
            foreach (var e in list)
                if (e != null && e.m_prefab != null) kept.Add(e);
            __instance.m_effectPrefabs = kept.ToArray();

            if (s_reported.Count < 256 && s_reported.Add(__instance))
            {
                string who = "not inside an attack";
                var atk = s_currentAttack;
                if (atk != null)
                {
                    Character c = atk.m_character;
                    string prefab = c != null ? Utils.GetPrefabName(c.gameObject) : "?";
                    bool companion = c != null && c.GetComponent<FiresCore.Npc.CompanionController>() != null;
                    string weapon = atk.m_weapon?.m_shared?.m_name ?? "?";
                    who = $"attacker {prefab}{(companion ? " (companion)" : "")}, animation '{atk.m_attackAnimation}', weapon {weapon}";
                }
                FiresCore.Logging.FiresLogger.LogDebug($"[NullEffect] skipped {nulls} null effect prefab(s) in an EffectList ({who}); later plays no longer throw");
            }
        }
    }
}
