using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Base
{
    /// <summary>
    /// Base class for healing effects that restore health over time.
    /// Used for regeneration, healing auras, etc.
    /// </summary>
    public class HealingEffect : CompanionStatusEffectBase
    {
        /// <summary>Health restored per tick.</summary>
        public float HealPerTick { get; set; } = 10f;
        
        /// <summary>Time between heal ticks in seconds.</summary>
        public float TickInterval { get; set; } = 2f;
        
        /// <summary>Whether to show healing text above character.</summary>
        public bool ShowHealText { get; set; } = true;
        
        /// <summary>Whether this is an area heal affecting nearby allies.</summary>
        public bool IsAreaHeal { get; set; } = false;
        
        /// <summary>Range for area healing.</summary>
        public float AreaHealRange { get; set; } = 10f;
        
        /// <summary>Percentage of heal applied to allies (if area heal).</summary>
        public float AllyHealPercent { get; set; } = 0.5f;
        
        /// <summary>Initial burst heal on application.</summary>
        public float InitialHeal { get; set; } = 0f;
        
        private float _lastTickTime;
        private float _totalHealed;
        
        public HealingEffect()
        {
            m_name = "Healing";
            m_tooltip = "Restoring health over time";
            Duration = 10f;
        }
        
        protected override void OnEffectApplied()
        {
            _lastTickTime = Time.time;
            _totalHealed = 0f;
            
            // Every client applies the effect from the routed RPC; only the target's owner heals, or it heals once per client.
            if (InitialHeal > 0f && m_character != null && m_character.IsOwner())
            {
                AbilityHeals.Apply(Healer, m_character, InitialHeal, ShowHealText);
                _totalHealed += InitialHeal;
                
                if (VerboseLogging)
                {
                    Debug.Log($"[HealingEffect] {m_character.m_name} initial heal: {InitialHeal} HP");
                }
            }
            
            if (m_character != null && VerboseLogging)
            {
                Debug.Log($"[HealingEffect] {m_character.m_name} healing {HealPerTick} HP every {TickInterval}s for {Duration}s");
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
                ApplyHealTick();
            }
        }
        
        /// <summary>Who this heal is credited to. An unattributed effect heals on its own behalf.</summary>
        protected Character Healer => SourceCharacter ?? m_character;

        /// <summary>
        /// Applies a single tick of healing.
        /// </summary>
        protected virtual void ApplyHealTick()
        {
            if (m_character == null || m_character.IsDead()) return;
            
            // Heal the primary target
            AbilityHeals.Apply(Healer, m_character, HealPerTick, ShowHealText);
            _totalHealed += HealPerTick;
            
            // If area heal, also heal nearby allies
            if (IsAreaHeal)
            {
                HealNearbyAllies();
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[HealingEffect] {m_character.m_name} healed for {HealPerTick} HP (total: {_totalHealed})");
            }
        }
        
        /// <summary>
        /// Heals nearby allies (for area healing effects).
        /// </summary>
        protected virtual void HealNearbyAllies()
        {
            if (m_character == null) return;
            
            float allyHeal = HealPerTick * AllyHealPercent;
            if (allyHeal <= 0f) return;
            
            Vector3 center = m_character.transform.position;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                
                // Only heal allies (same faction or tamed by same owner)
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float distance = Vector3.Distance(center, character.transform.position);
                if (distance <= AreaHealRange)
                {
                    AbilityHeals.Apply(Healer, character, allyHeal, ShowHealText);
                    
                    if (VerboseLogging)
                    {
                        Debug.Log($"[HealingEffect] Ally {character.m_name} healed for {allyHeal} HP");
                    }
                }
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null && VerboseLogging)
            {
                Debug.Log($"[HealingEffect] {m_character.m_name} healing ended - total healed: {_totalHealed} HP");
            }
        }
        
        public new HealingEffect Clone()
        {
            var clone = (HealingEffect)base.Clone();
            if (clone != null)
            {
                clone.Duration = Duration;
                clone.HealPerTick = HealPerTick;
                clone.TickInterval = TickInterval;
                clone.ShowHealText = ShowHealText;
                clone.IsAreaHeal = IsAreaHeal;
                clone.AreaHealRange = AreaHealRange;
                clone.AllyHealPercent = AllyHealPercent;
                clone.InitialHeal = InitialHeal;
                clone.EffectIcon = EffectIcon;
                clone.SourceCharacter = SourceCharacter;
            }
            return clone;
        }
    }
}
