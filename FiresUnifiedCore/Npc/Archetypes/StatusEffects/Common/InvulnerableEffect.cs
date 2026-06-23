using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Common
{
    /// <summary>
    /// Invulnerable effect - grants complete immunity to all damage types.
    /// Used by: Tank (during taunt), Berserker (rage mode), Rogue (vanish immunity)
    /// 
    /// DESIGN: This makes the character immune to ALL damage while active.
    /// Use sparingly and with short durations to avoid making combat trivial.
    /// 
    /// CRITICAL: This effect has a TTL and WILL expire after Duration seconds.
    /// The base StatusEffect.UpdateStatusEffect handles TTL countdown.
    /// </summary>
    public class InvulnerableEffect : CompanionStatusEffectBase
    {
        /// <summary>
        /// Description shown in tooltip.
        /// </summary>
        public override string Description => "Immune to all damage types";
        
        public InvulnerableEffect()
        {
            m_name = "Invulnerable";
            m_tooltip = "Immune to all damage";
            Duration = 2f; // Default short duration
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character != null)
            {
                Debug.Log($"[InvulnerableEffect] {m_character.m_name} is now INVULNERABLE for {Duration}s (m_ttl={m_ttl})");
            }
        }
        
        /// <summary>
        /// Called every frame by the status effect manager.
        /// CRITICAL: Must call base.UpdateStatusEffect to decrement TTL!
        /// </summary>
        public override void UpdateStatusEffect(float dt)
        {
            // CRITICAL: This decrements m_ttl and handles expiration
            base.UpdateStatusEffect(dt);
            
            // Optional: Log remaining time every second for debugging
            if (VerboseLogging && m_character != null && Mathf.FloorToInt(m_ttl) != Mathf.FloorToInt(m_ttl + dt))
            {
                Debug.Log($"[InvulnerableEffect] {m_character.m_name} invulnerability remaining: {m_ttl:F1}s");
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null)
            {
                Debug.Log($"[InvulnerableEffect] {m_character.m_name} invulnerability ENDED");
            }
        }
        
        public override void ModifyDamageMods(ref HitData.DamageModifiers mods)
        {
            // IMMUNE TO EVERYTHING
            mods.m_blunt = HitData.DamageModifier.Immune;
            mods.m_slash = HitData.DamageModifier.Immune;
            mods.m_pierce = HitData.DamageModifier.Immune;
            mods.m_chop = HitData.DamageModifier.Immune;
            mods.m_pickaxe = HitData.DamageModifier.Immune;
            mods.m_fire = HitData.DamageModifier.Immune;
            mods.m_frost = HitData.DamageModifier.Immune;
            mods.m_lightning = HitData.DamageModifier.Immune;
            mods.m_poison = HitData.DamageModifier.Immune;
            mods.m_spirit = HitData.DamageModifier.Immune;
        }
        
        public new InvulnerableEffect Clone()
        {
            var clone = (InvulnerableEffect)base.Clone();
            if (clone != null)
            {
                clone.Duration = Duration;
                clone.EffectIcon = EffectIcon;
                clone.SourceCharacter = SourceCharacter;
            }
            return clone;
        }
    }
}
