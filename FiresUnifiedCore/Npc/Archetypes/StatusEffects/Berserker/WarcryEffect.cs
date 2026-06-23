using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.Archetypes.StatusEffects.Berserker
{
    /// <summary>
    /// Warcry effect - a group buff that increases damage and provides brief fear immunity.
    /// Used by: Berserker archetype (group buff ability)
    /// 
    /// DESIGN: The berserker lets out a warcry that buffs nearby allies,
    /// increasing their damage output and making them immune to fear/stagger briefly.
    /// </summary>
    public class WarcryEffect : Base.BuffEffect
    {
        /// <summary>Fear/stagger immunity duration at start.</summary>
        public float FearImmunityDuration { get; set; } = 3f;
        
        /// <summary>Whether the warcry grants stagger immunity.</summary>
        public bool GrantStaggerImmunity { get; set; } = true;
        
        private bool _fearImmunityActive = false;
        private float _fearImmunityEndTime;
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"+{(DamageMultiplier - 1f) * 100:F0}% damage, +{(AttackSpeedMultiplier - 1f) * 100:F0}% attack speed\n" +
            $"{FearImmunityDuration:F0}s fear/stagger immunity";
        
        public WarcryEffect()
        {
            m_name = "Warcry";
            Duration = 15f;
            DamageMultiplier = 1.25f; // 25% damage boost
            AttackSpeedMultiplier = 1.1f; // 10% attack speed
            IsGroupBuff = true;
            GroupBuffRange = 15f;
        }
        
        protected override void OnEffectApplied()
        {
            base.OnEffectApplied();
            
            // Grant fear immunity for initial duration
            _fearImmunityActive = true;
            _fearImmunityEndTime = Time.time + FearImmunityDuration;
            
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "WARCRY!");
                
                if (VerboseLogging)
                {
                    Debug.Log($"[WarcryEffect] {m_character.m_name} empowered by warcry - {FearImmunityDuration}s fear immunity");
                }
            }
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            // Check if fear immunity has expired
            if (_fearImmunityActive && Time.time >= _fearImmunityEndTime)
            {
                _fearImmunityActive = false;
                
                if (VerboseLogging && m_character != null)
                {
                    Debug.Log($"[WarcryEffect] {m_character.m_name} fear immunity ended");
                }
            }
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            
            // Negate stagger during fear immunity period
            if (_fearImmunityActive && GrantStaggerImmunity)
            {
                hit.m_staggerMultiplier = 0f;
            }
        }
        
        protected override void OnEffectRemoved()
        {
            _fearImmunityActive = false;
            
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Warcry fades...");
            }
            
            base.OnEffectRemoved();
        }
        
        /// <summary>
        /// Applies the warcry effect to all nearby allies.
        /// Call this from the ability that triggers warcry.
        /// </summary>
        public static int ApplyWarcryToAllies(Character source, float range, float duration)
        {
            if (source == null) return 0;
            
            int buffedCount = 0;
            Vector3 center = source.transform.position;
            
            // Apply to self first
            ApplyWarcryToCharacter(source, source, duration);
            buffedCount++;
            
            // Find and buff nearby allies
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == source) continue;
                
                // Only buff allies
                if (BaseAI.IsEnemy(source, character)) continue;
                
                float distance = Vector3.Distance(center, character.transform.position);
                if (distance <= range)
                {
                    ApplyWarcryToCharacter(character, source, duration);
                    buffedCount++;
                }
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[WarcryEffect] {source.m_name} warcry buffed {buffedCount} allies");
            }
            
            return buffedCount;
        }
        
        private static void ApplyWarcryToCharacter(Character target, Character source, float duration)
        {
            var seman = target.GetSEMan();
            if (seman == null) return;
            
            var effect = ScriptableObject.CreateInstance<WarcryEffect>();
            effect.name = StatusEffectManager.EFFECT_WARCRY;
            effect.Duration = duration;
            effect.SourceCharacter = source;
            
            // CRITICAL: Set m_icon directly - clone copies m_icon but not custom properties
            var icon = StatusEffectManager.GetEffectIcon(StatusEffectManager.EFFECT_WARCRY);
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            // Remove existing warcry
            int existingHash = StatusEffectManager.EFFECT_WARCRY.GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(effect, true);
            
            Debug.Log($"[WarcryEffect] Applied to {target.m_name}, hasIcon={icon != null}, isPlayer={target.IsPlayer()}");
        }
        
        public new WarcryEffect Clone()
        {
            var clone = (WarcryEffect)base.Clone();
            if (clone != null)
            {
                clone.FearImmunityDuration = FearImmunityDuration;
                clone.GrantStaggerImmunity = GrantStaggerImmunity;
            }
            return clone;
        }
    }
}
