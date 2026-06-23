using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Healer
{
    /// <summary>
    /// Purifying Circle effect - an area healing effect that cleanses and heals allies.
    /// Used by: Healer archetype (group heal ability)
    /// 
    /// DESIGN: Creates a healing circle that heals all allies within range
    /// and periodically cleanses negative effects.
    /// </summary>
    public class PurifyingCircleEffect : Base.HealingEffect
    {
        /// <summary>Whether to cleanse debuffs on each tick.</summary>
        public bool CleanseOnTick { get; set; } = true;
        
        /// <summary>How often to cleanse (in ticks, 1 = every tick).</summary>
        public int CleanseEveryNTicks { get; set; } = 3;
        
        private int _tickCount = 0;
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"+{HealPerTick:F0} HP every {TickInterval:F1}s\n" +
            $"Cleanses debuffs periodically";
        
        /// <summary>Names of debuffs to remove.</summary>
        private static readonly System.Collections.Generic.HashSet<string> DebuffNames = 
            new System.Collections.Generic.HashSet<string>
        {
            "Poison", "Burning", "Frost", "Wet", "Smoked", "Tared",
            "Cold", "Freezing", "Spirit", "Lightning", "Bleeding"
        };
        
        public PurifyingCircleEffect()
        {
            m_name = "Purifying Circle";
            Duration = 12f;
            HealPerTick = 8f;
            TickInterval = 1.5f;
            IsAreaHeal = true;
            AreaHealRange = 8f;
            AllyHealPercent = 1.0f; // Full healing to allies too
            InitialHeal = 15f; // Burst heal on application
        }
        
        protected override void OnEffectApplied()
        {
            base.OnEffectApplied();
            _tickCount = 0;
            
            // Initial cleanse
            if (CleanseOnTick && m_character != null)
            {
                CleanseDebuffs(m_character);
            }
            
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Purifying Circle!");
            }
        }
        
        protected override void ApplyHealTick()
        {
            base.ApplyHealTick();
            
            _tickCount++;
            
            // Cleanse on scheduled ticks
            if (CleanseOnTick && _tickCount % CleanseEveryNTicks == 0)
            {
                if (m_character != null)
                {
                    CleanseDebuffs(m_character);
                }
                
                // Also cleanse allies if area heal
                if (IsAreaHeal)
                {
                    CleanseNearbyAllies();
                }
            }
        }
        
        private void CleanseNearbyAllies()
        {
            if (m_character == null) return;
            
            Vector3 center = m_character.transform.position;
            var characters = Character.GetAllCharacters();
            
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float distance = Vector3.Distance(center, character.transform.position);
                if (distance <= AreaHealRange)
                {
                    CleanseDebuffs(character);
                }
            }
        }
        
        private void CleanseDebuffs(Character target)
        {
            var seman = target.GetSEMan();
            if (seman == null) return;
            
            var effects = new System.Collections.Generic.List<StatusEffect>(seman.GetStatusEffects());
            int removed = 0;
            
            foreach (var effect in effects)
            {
                if (effect == this) continue;
                
                if (DebuffNames.Contains(effect.name) || 
                    (!string.IsNullOrEmpty(effect.m_name) && DebuffNames.Contains(effect.m_name)))
                {
                    seman.RemoveStatusEffect(effect, true);
                    removed++;
                }
            }
            
            if (removed > 0 && VerboseLogging)
            {
                Debug.Log($"[PurifyingCircleEffect] Cleansed {removed} debuffs from {target.m_name}");
            }
        }
        
        /// <summary>
        /// Applies purifying circle to a character and nearby allies.
        /// </summary>
        public static int ApplyPurifyingCircle(Character source, float range, float duration)
        {
            if (source == null) return 0;
            
            int affectedCount = 0;
            Vector3 center = source.transform.position;
            
            // Apply to all allies in range
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (BaseAI.IsEnemy(source, character)) continue;
                
                float distance = Vector3.Distance(center, character.transform.position);
                if (distance <= range)
                {
                    ApplyToCharacter(character, source, duration);
                    affectedCount++;
                }
            }
            
            return affectedCount;
        }
        
        private static void ApplyToCharacter(Character target, Character source, float duration)
        {
            var seman = target.GetSEMan();
            if (seman == null) return;
            
            var effect = ScriptableObject.CreateInstance<PurifyingCircleEffect>();
            effect.name = StatusEffectManager.EFFECT_PURIFYING_CIRCLE;
            effect.Duration = duration;
            effect.SourceCharacter = source;
            
            // CRITICAL: Set m_icon directly - clone copies m_icon but not custom properties
            var icon = StatusEffectManager.GetEffectIcon(StatusEffectManager.EFFECT_PURIFYING_CIRCLE);
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            int existingHash = StatusEffectManager.EFFECT_PURIFYING_CIRCLE.GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(effect, true);
            
            Debug.Log($"[PurifyingCircleEffect] Applied to {target.m_name}, hasIcon={icon != null}, isPlayer={target.IsPlayer()}");
        }
        
        public new PurifyingCircleEffect Clone()
        {
            var clone = (PurifyingCircleEffect)base.Clone();
            if (clone != null)
            {
                clone.CleanseOnTick = CleanseOnTick;
                clone.CleanseEveryNTicks = CleanseEveryNTicks;
            }
            return clone;
        }
    }
}
