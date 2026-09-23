using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Companions are Humanoids, and vanilla implements armor, armor resistances, the skill factor, gear movement speed
    /// and stamina/eitr restores only in Player. These hooks give companions the same at the same points in vanilla's
    /// code, so gear, status effects, skills, attributes and the archetype each apply once and in vanilla's order. All
    /// patch Character's own methods (or SEMan and HitData, gated to companions); Player's overrides never call them.
    /// </summary>
    internal static class CompanionStatHooks
    {
        // Vanilla Player.GetRunSpeedFactor: (1 + run skill x 0.25) x (1 + equipment movement modifier x 1.5).
        private const float RunSkillSpeedBonus = 0.25f;
        private const float RunMovementModifierScale = 1.5f;

        // The hit Character.RPC_Damage is processing for a companion: blocked just before vanilla's resistance pass,
        // armored just after it, which is where vanilla blocks and armors a player.
        private static HitData _incomingHit;
        private static Character _incomingTarget;

        internal static void BeginIncomingHit(Character target, HitData hit)
        {
            _incomingHit = hit;
            _incomingTarget = target;
            target.GetComponent<CompanionCombat>()?.RecordIncoming(hit);
        }

        private static void BlockIncoming(HitData hit)
        {
            var combat = _incomingTarget != null ? _incomingTarget.GetComponent<CompanionCombat>() : null;
            if (combat == null) return;
            if (!hit.m_blockable)
            {
                combat.OnDamageReceived();
                return;
            }
            float total = hit.GetTotalDamage();
            if (total <= 0f) return;
            float afterBlock = combat.ProcessIncomingDamage(hit);
            if (afterBlock < total) hit.ApplyModifier(Mathf.Max(0f, afterBlock) / total);
        }

        private static void ArmorIncoming(HitData hit)
        {
            if (!CompanionController.TryGet(_incomingTarget, out var companion) || companion.EquipmentData == null) return;
            float armor = companion.EquipmentData.GetEffectiveArmor();
            _incomingTarget.GetSEMan()?.ApplyArmorMods(ref armor);
            if (armor > 0f) hit.ApplyArmor(armor);
            companion.EquipmentData.DamageArmorDurability(hit);
        }

        [HarmonyPatch(typeof(HitData), nameof(HitData.ApplyResistance))]
        private static class IncomingHitMitigation
        {
            [HarmonyPrefix]
            private static void Block(HitData __instance)
            {
                if (ReferenceEquals(__instance, _incomingHit)) BlockIncoming(__instance);
            }

            [HarmonyPostfix]
            private static void Armor(HitData __instance)
            {
                if (!ReferenceEquals(__instance, _incomingHit)) return;
                ArmorIncoming(__instance);
                _incomingHit = null;
                _incomingTarget = null;
            }
        }

        [HarmonyPatch(typeof(Character), "ApplyArmorDamageMods")]
        private static class ArmorResistances
        {
            [HarmonyPostfix]
            private static void Postfix(Character __instance, ref HitData.DamageModifiers mods)
            {
                if (!CompanionController.TryGet(__instance, out var companion)) return;
                companion.EquipmentData?.ApplyArmorDamageMods(ref mods);
                // Fire immunity must beat gear: vanilla precedence lets a Resistant piece replace a base Immune.
                if (__instance.m_damageModifiers.m_fire == HitData.DamageModifier.Immune) mods.m_fire = HitData.DamageModifier.Immune;
            }
        }

        [HarmonyPatch(typeof(SEMan), nameof(SEMan.ModifyAttack))]
        private static class OutgoingModifiers
        {
            [HarmonyPostfix]
            private static void Postfix(Character ___m_character, Skills.SkillType skill, ref HitData hitData)
            {
                if (CompanionController.TryGet(___m_character, out var companion))
                    companion.EquipmentData?.ApplyOutgoingModifiers(skill, hitData);
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.GetRandomSkillFactor))]
        private static class DamageSkillFactor
        {
            [HarmonyPostfix]
            private static void Postfix(Character __instance, Skills.SkillType skill, ref float __result)
            {
                if (CompanionController.TryGet(__instance, out var companion) && companion.GetSkills() != null)
                    __result = companion.GetSkills().GetDamageSkillFactor(skill);
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.GetSkillFactor))]
        private static class SkillFactor
        {
            [HarmonyPostfix]
            private static void Postfix(Character __instance, Skills.SkillType skill, ref float __result)
            {
                if (CompanionController.TryGet(__instance, out var companion) && companion.GetSkills() != null)
                    __result = companion.GetSkills().GetSkillFactor(skill);
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.GetSkillLevel))]
        private static class SkillLevel
        {
            [HarmonyPostfix]
            private static void Postfix(Character __instance, Skills.SkillType skillType, ref float __result)
            {
                if (CompanionController.TryGet(__instance, out var companion) && companion.GetSkills() != null)
                    __result = companion.GetSkills().GetSkillLevel(skillType);
            }
        }

        private static float GearMovementModifier(CompanionController companion) =>
            companion.EquipmentData != null ? companion.EquipmentData.TotalMovementModifier : 0f;

        private static float SpeedAttributeMultiplier(CompanionController companion) =>
            companion.GetProgression() != null ? companion.GetProgression().GetSpeedMultiplier() : 1f;

        [HarmonyPatch(typeof(Character), nameof(Character.GetEquipmentMovementModifier))]
        private static class EquipmentMovementModifier
        {
            [HarmonyPostfix]
            private static void Postfix(Character __instance, ref float __result)
            {
                if (CompanionController.TryGet(__instance, out var companion)) __result = GearMovementModifier(companion);
            }
        }

        [HarmonyPatch(typeof(Character), "GetJogSpeedFactor")]
        private static class JogSpeedFactor
        {
            [HarmonyPostfix]
            private static void Postfix(Character __instance, ref float __result)
            {
                if (!CompanionController.TryGet(__instance, out var companion)) return;
                __result *= (1f + GearMovementModifier(companion)) * SpeedAttributeMultiplier(companion);
            }
        }

        [HarmonyPatch(typeof(Character), "GetRunSpeedFactor")]
        private static class RunSpeedFactor
        {
            [HarmonyPostfix]
            private static void Postfix(Character __instance, ref float __result)
            {
                if (!CompanionController.TryGet(__instance, out var companion)) return;
                float runSkill = companion.GetSkills() != null ? companion.GetSkills().GetSkillFactor(Skills.SkillType.Run) : 0f;
                __result *= (1f + runSkill * RunSkillSpeedBonus) * (1f + GearMovementModifier(companion) * RunMovementModifierScale)
                    * SpeedAttributeMultiplier(companion);
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.AddStamina))]
        private static class StaminaRestore
        {
            [HarmonyPostfix]
            private static void Postfix(Character __instance, float v)
            {
                if (CompanionController.TryGet(__instance, out var companion)) companion.GetStats()?.AddStamina(v);
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.AddEitr))]
        private static class EitrRestore
        {
            [HarmonyPostfix]
            private static void Postfix(Character __instance, float v)
            {
                if (CompanionController.TryGet(__instance, out var companion)) companion.GetStats()?.AddEitr(v);
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.GetMaxStamina))]
        private static class MaxStamina
        {
            [HarmonyPostfix]
            private static void Postfix(Character __instance, ref float __result)
            {
                if (CompanionController.TryGet(__instance, out var companion) && companion.GetStats() != null)
                    __result = companion.GetStats().MaxStamina;
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.GetMaxEitr))]
        private static class MaxEitr
        {
            [HarmonyPostfix]
            private static void Postfix(Character __instance, ref float __result)
            {
                if (CompanionController.TryGet(__instance, out var companion) && companion.GetStats() != null)
                    __result = companion.GetStats().MaxEitr;
            }
        }
    }
}
