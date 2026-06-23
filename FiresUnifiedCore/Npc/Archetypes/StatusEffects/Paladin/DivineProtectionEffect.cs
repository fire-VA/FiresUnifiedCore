using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Paladin
{
    /// <summary>
    /// Divine Protection effect - reduces damage taken by allies in range.
    /// Used by: Paladin archetype (group buff ability)
    /// 
    /// DESIGN: The paladin channels divine power to protect nearby allies,
    /// reducing incoming damage. Can be maintained as an aura.
    /// </summary>
    public class DivineProtectionEffect : Base.BuffEffect
    {
        /// <summary>Range of the protection aura.</summary>
        public float AuraRange { get; set; } = 10f;
        
        /// <summary>Interval to reapply aura to allies.</summary>
        public float AuraTickInterval { get; set; } = 2f;
        
        private float _lastAuraTick;
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"-{(1f - DefenseMultiplier) * 100:F0}% damage taken";
        
        public DivineProtectionEffect()
        {
            m_name = "Divine Protection";
            Duration = 12f;
            DefenseMultiplier = 0.75f; // 25% damage reduction
            IsGroupBuff = true;
            GroupBuffRange = 10f;
        }
        
        protected override void OnEffectApplied()
        {
            base.OnEffectApplied();
            _lastAuraTick = Time.time;
            
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Divine Protection!");
                
                if (VerboseLogging)
                {
                    Debug.Log($"[DivineProtectionEffect] {m_character.m_name} protected - {(1f - DefenseMultiplier) * 100}% damage reduction");
                }
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Divine protection fades...");
            }
            
            base.OnEffectRemoved();
        }
        
        /// <summary>
        /// Applies divine protection to all allies within range.
        /// </summary>
        public static int ApplyDivineProtection(Character source, float range, float duration)
        {
            if (source == null) return 0;
            
            int protectedCount = 0;
            Vector3 center = source.transform.position;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (BaseAI.IsEnemy(source, character)) continue;
                
                float distance = Vector3.Distance(center, character.transform.position);
                if (distance <= range)
                {
                    ApplyToCharacter(character, source, duration);
                    protectedCount++;
                }
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[DivineProtectionEffect] {source.m_name} protecting {protectedCount} allies");
            }
            
            return protectedCount;
        }
        
        private static void ApplyToCharacter(Character target, Character source, float duration)
        {
            var seman = target.GetSEMan();
            if (seman == null) return;
            
            var effect = ScriptableObject.CreateInstance<DivineProtectionEffect>();
            effect.name = StatusEffectManager.EFFECT_DIVINE_PROTECTION;
            effect.Duration = duration;
            effect.SourceCharacter = source;
            
            // CRITICAL: Set m_icon directly - clone copies m_icon but not custom properties
            var icon = StatusEffectManager.GetEffectIcon(StatusEffectManager.EFFECT_DIVINE_PROTECTION);
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            int existingHash = StatusEffectManager.EFFECT_DIVINE_PROTECTION.GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(effect, true);
            
            Debug.Log($"[DivineProtectionEffect] Applied to {target.m_name}, hasIcon={icon != null}, isPlayer={target.IsPlayer()}");
        }
        
        public new DivineProtectionEffect Clone()
        {
            var clone = (DivineProtectionEffect)base.Clone();
            if (clone != null)
            {
                clone.AuraRange = AuraRange;
                clone.AuraTickInterval = AuraTickInterval;
            }
            return clone;
        }
    }
}
