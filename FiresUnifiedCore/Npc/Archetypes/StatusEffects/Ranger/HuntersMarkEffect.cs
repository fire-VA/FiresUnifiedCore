using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Ranger
{
    /// <summary>
    /// Hunter's Mark effect - marks a target for bonus damage from all allies.
    /// Used by: Ranger archetype (applied to enemies)
    /// 
    /// DESIGN: The ranger marks an enemy, causing all attacks against that
    /// enemy to deal bonus damage. Great for focus-firing bosses.
    /// </summary>
    public class HuntersMarkEffect : CompanionStatusEffectBase
    {
        /// <summary>Bonus damage multiplier from all sources.</summary>
        public float BonusDamageMultiplier { get; set; } = 1.25f;
        
        /// <summary>The ranger who applied the mark.</summary>
        public Character MarkedBy { get; set; }
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"Target takes +{(BonusDamageMultiplier - 1f) * 100:F0}% damage\n" +
            $"from all sources";
        
        public HuntersMarkEffect()
        {
            m_name = "Hunter's Mark";
            Duration = 20f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character != null && VerboseLogging)
            {
                Debug.Log($"[HuntersMarkEffect] {m_character.m_name} marked - takes {(BonusDamageMultiplier - 1f) * 100}% bonus damage");
            }
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            
            // Increase all incoming damage
            hit.m_damage.Modify(BonusDamageMultiplier);
            
            if (VerboseLogging && m_character != null)
            {
                Debug.Log($"[HuntersMarkEffect] {m_character.m_name} taking {BonusDamageMultiplier}x damage from mark");
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null && VerboseLogging)
            {
                Debug.Log($"[HuntersMarkEffect] Mark on {m_character.m_name} expired");
            }
        }
        
        /// <summary>
        /// Applies hunter's mark to an enemy.
        /// </summary>
        public static bool ApplyHuntersMark(Character target, Character ranger, float duration)
        {
            if (target == null || ranger == null) return false;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            var effect = ScriptableObject.CreateInstance<HuntersMarkEffect>();
            effect.name = StatusEffectManager.EFFECT_HUNTERS_MARK;
            effect.Duration = duration;
            effect.MarkedBy = ranger;
            effect.SourceCharacter = ranger;
            
            // CRITICAL: Set m_icon directly - clone copies m_icon but not custom properties
            var icon = StatusEffectManager.GetEffectIcon(StatusEffectManager.EFFECT_HUNTERS_MARK);
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            int existingHash = StatusEffectManager.EFFECT_HUNTERS_MARK.GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(effect, true);
            
            // Show visual feedback
            target.Message(MessageHud.MessageType.TopLeft, "MARKED!");
            
            Debug.Log($"[HuntersMarkEffect] Applied to {target.m_name}, hasIcon={icon != null}");
            
            return true;
        }
        
        public new HuntersMarkEffect Clone()
        {
            var clone = (HuntersMarkEffect)base.Clone();
            if (clone != null)
            {
                clone.BonusDamageMultiplier = BonusDamageMultiplier;
                clone.MarkedBy = MarkedBy;
            }
            return clone;
        }
    }
}
