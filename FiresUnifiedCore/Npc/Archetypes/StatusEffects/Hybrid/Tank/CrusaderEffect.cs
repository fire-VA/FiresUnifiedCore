using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Tank
{
    /// <summary>
    /// Tank + Paladin = Crusader - Divine Bulwark
    /// Creates a holy shield that blocks all damage for 3s and heals nearby allies.
    /// 
    /// THEME: Holy warrior combining unwavering defense with divine power.
    /// The Crusader stands as a beacon of light, protecting allies with both
    /// physical shields and divine grace.
    /// </summary>
    public class CrusaderEffect : HybridAbilityEffect
    {
        public float InvulnerabilityDuration { get; set; } = 3f;
        public float HealAmount { get; set; } = 50f;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Holy shield blocks all damage for {InvulnerabilityDuration:F0}s\n" +
            $"Heals nearby allies for {HealAmount:F0} HP";
        
        public CrusaderEffect()
        {
            m_name = "Divine Bulwark";
            Duration = 8f;
            MainArchetype = ArchetypeClass.Tank;
            SubArchetype = ArchetypeClass.Paladin;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            // Grant invulnerability
            StatusEffectManager.ApplyInvulnerable(m_character, InvulnerabilityDuration);
            
            // Heal nearby allies
            HealNearbyAllies();
            
            m_character.Message(MessageHud.MessageType.Center, "Divine Bulwark!");
            
            // VFX
            AbilityFXManager.SpawnEffect("fx_shield_start", m_character.transform.position, null, 2f);
            AbilityFXManager.SpawnEffect("vfx_ghost_hit", m_character.transform.position, null, 1.5f);
        }
        
        private void HealNearbyAllies()
        {
            if (m_character == null) return;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= GroupRange)
                {
                    AbilityHeals.Apply(SourceCharacter ?? m_character, character, HealAmount, true);
                    AbilityFXManager.SpawnEffect("fx_creature_tamed", character.transform.position, null, 0.5f);
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<CrusaderEffect>();
            effect.name = "CompanionDivineBulwark";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionDivineBulwark");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
}
