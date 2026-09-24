using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.Archetypes.StatusEffects.Healer
{
    /// <summary>
    /// Purify effect - cleanses negative status effects and provides healing over time.
    /// Used by: Healer archetype
    /// 
    /// DESIGN: When applied, immediately removes common debuffs (poison, burning, frost, etc.)
    /// and then heals the target for a small amount over the duration.
    /// </summary>
    public class PurifyEffect : CompanionStatusEffectBase
    {
        /// <summary>Heal per tick.</summary>
        public float HealPerTick { get; set; } = 5f;
        
        /// <summary>Time between heal ticks.</summary>
        public float TickInterval { get; set; } = 2f;
        
        private float _lastTickTime;
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"Cleanses debuffs\n" +
            $"+{HealPerTick:F0} HP every {TickInterval:F1}s";
        
        /// <summary>Names of debuffs to remove.</summary>
        private static readonly HashSet<string> DebuffNames = new HashSet<string>
        {
            "Poison",
            "Burning",
            "Frost",
            "Wet",
            "Smoked",
            "Tared",
            "Cold",
            "Freezing",
            "Spirit",
            "Lightning"
        };
        
        public PurifyEffect()
        {
            m_name = "Purified";
            m_tooltip = "Cleansed and regenerating health";
            Duration = 10f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            // Remove all debuffs immediately
            RemoveDebuffs();
            
            // Show message
            m_character.Message(MessageHud.MessageType.TopLeft, "Purified!");
            
            _lastTickTime = Time.time;
            
            if (VerboseLogging)
            {
                Debug.Log($"[PurifyEffect] {m_character.m_name} purified - debuffs removed, healing for {Duration}s");
            }
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (m_character == null || m_character.IsDead()) return;
            
            // Heal on tick interval
            if (Time.time - _lastTickTime >= TickInterval)
            {
                _lastTickTime = Time.time;
                
                // Heal the character
                AbilityHeals.Apply(SourceCharacter ?? m_character, m_character, HealPerTick, true);
                
                if (VerboseLogging)
                {
                    Debug.Log($"[PurifyEffect] {m_character.m_name} healed for {HealPerTick} HP");
                }
            }
        }
        
        /// <summary>
        /// Removes all negative status effects from the character.
        /// </summary>
        private void RemoveDebuffs()
        {
            var seman = m_character.GetSEMan();
            if (seman == null) return;
            
            // Get a copy of the list to avoid modification during iteration
            var effects = new List<StatusEffect>(seman.GetStatusEffects());
            
            int removedCount = 0;
            foreach (var effect in effects)
            {
                if (effect == this) continue; // Don't remove ourselves
                
                // Check if this is a debuff we should remove
                if (IsDebuff(effect))
                {
                    seman.RemoveStatusEffect(effect, true);
                    removedCount++;
                    
                    if (VerboseLogging)
                    {
                        Debug.Log($"[PurifyEffect] Removed debuff: {effect.name}");
                    }
                }
            }
            
            if (removedCount > 0 && VerboseLogging)
            {
                Debug.Log($"[PurifyEffect] Removed {removedCount} debuffs from {m_character.m_name}");
            }
        }
        
        /// <summary>
        /// Checks if a status effect is a debuff that should be removed.
        /// </summary>
        private bool IsDebuff(StatusEffect effect)
        {
            if (effect == null) return false;
            
            // Check by name
            if (DebuffNames.Contains(effect.name))
                return true;
            
            // Check by m_name (display name)
            if (!string.IsNullOrEmpty(effect.m_name) && DebuffNames.Contains(effect.m_name))
                return true;
            
            return false;
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null && VerboseLogging)
            {
                Debug.Log($"[PurifyEffect] {m_character.m_name} purify effect ended");
            }
        }
        
        public new PurifyEffect Clone()
        {
            var clone = (PurifyEffect)base.Clone();
            if (clone != null)
            {
                clone.Duration = Duration;
                clone.HealPerTick = HealPerTick;
                clone.TickInterval = TickInterval;
                clone.EffectIcon = EffectIcon;
                clone.SourceCharacter = SourceCharacter;
            }
            return clone;
        }
    }
}
