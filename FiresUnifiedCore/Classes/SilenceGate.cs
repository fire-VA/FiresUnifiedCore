using FiresCore.Npc.Archetypes;
using HarmonyLib;

namespace FiresCore.Classes
{
    /// <summary>
    /// 0.2.320 ([wishbone] 10-07): ClassSilence refuses attack starts. A player is refused staff attacks (the weapon's skill is
    /// elemental or blood magic); any other character is refused every attack, which interrupts a monster's cast. Class skills
    /// are RPGClasses' own path: it checks ClassCrowdControl.IsSilenced first.
    /// </summary>
    internal static class SilenceGate
    {
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.StartAttack))]
        private static class Humanoid_StartAttack_Silence
        {
            private static bool Prefix(Humanoid __instance)
            {
                if (__instance == null || !ClassCrowdControl.IsSilenced(__instance)) return true;
                if (!__instance.IsPlayer()) return false;
                ItemDrop.ItemData weapon = __instance.GetCurrentWeapon();
                Skills.SkillType skill = weapon != null ? weapon.m_shared.m_skillType : Skills.SkillType.None;
                return skill != Skills.SkillType.ElementalMagic && skill != Skills.SkillType.BloodMagic;
            }
        }
    }
}
