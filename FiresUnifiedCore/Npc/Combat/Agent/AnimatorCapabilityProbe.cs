using UnityEngine;

namespace FiresCore.Npc.Combat.Agent
{
    /// <summary>
    /// Reads a body's real capabilities off its animator parameters and its equipment, so a behaviour never
    /// asks for an animation the creature does not have.
    /// </summary>
    public static class AnimatorCapabilityProbe
    {
        private const string DodgeParameter = "dodge";
        private const string BlockingParameter = "blocking";
        private const string CrouchParameter = "crouching";
        private const string AttackParameter = "attack";

        public static CombatAgentCapability Probe(Character body, Animator animator)
        {
            if (body == null) return CombatAgentCapability.None;

            CombatAgentCapability capabilities = CombatAgentCapability.Melee | CombatAgentCapability.Sidestep;

            if (HasParameter(animator, DodgeParameter)) capabilities |= CombatAgentCapability.DodgeRoll;
            if (HasParameter(animator, CrouchParameter)) capabilities |= CombatAgentCapability.Crouch;

            if (HasParameter(animator, BlockingParameter))
            {
                capabilities |= CombatAgentCapability.Block;
                if (HasParameter(animator, AttackParameter)) capabilities |= CombatAgentCapability.Parry;
            }

            var humanoid = body as Humanoid;
            if (humanoid != null && HasRangedAttack(humanoid)) capabilities |= CombatAgentCapability.Ranged;

            return capabilities;
        }

        public static bool HasParameter(Animator animator, string parameterName)
        {
            if (animator == null || string.IsNullOrEmpty(parameterName)) return false;

            var parameters = animator.parameters;
            for (int index = 0; index < parameters.Length; index++)
            {
                if (parameters[index].name == parameterName) return true;
            }
            return false;
        }

        private static bool HasRangedAttack(Humanoid humanoid)
        {
            if (humanoid.m_defaultItems != null)
            {
                for (int index = 0; index < humanoid.m_defaultItems.Length; index++)
                {
                    if (IsRangedItem(humanoid.m_defaultItems[index])) return true;
                }
            }

            if (humanoid.m_randomWeapon != null)
            {
                for (int index = 0; index < humanoid.m_randomWeapon.Length; index++)
                {
                    if (IsRangedItem(humanoid.m_randomWeapon[index])) return true;
                }
            }

            var equipped = humanoid.GetCurrentWeapon();
            return equipped != null && IsRangedShared(equipped.m_shared);
        }

        private static bool IsRangedItem(GameObject itemPrefab)
        {
            if (itemPrefab == null) return false;
            var drop = itemPrefab.GetComponent<ItemDrop>();
            return drop != null && drop.m_itemData != null && IsRangedShared(drop.m_itemData.m_shared);
        }

        private static bool IsRangedShared(ItemDrop.ItemData.SharedData shared)
        {
            if (shared == null) return false;
            if (shared.m_skillType == Skills.SkillType.Bows || shared.m_skillType == Skills.SkillType.Crossbows) return true;
            return shared.m_attack != null && shared.m_attack.m_attackProjectile != null;
        }
    }
}
