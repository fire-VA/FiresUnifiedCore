using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Tank
{
    /// <summary>
    /// Tank + Berserker = Juggernaut - Unstoppable Charge
    /// Charges forward, stunning enemies and gaining damage reduction.
    /// 
    /// THEME: An unstoppable force that grows stronger as battle rages.
    /// The Juggernaut combines defensive prowess with berserker aggression,
    /// becoming an immovable object that also hits like a freight train.
    /// </summary>
    public class JuggernautEffect : HybridAbilityEffect
    {
        public float DamageReduction { get; set; } = 0.5f;
        public float StunDuration { get; set; } = 2f;
        public float ChargeRange { get; set; } = 8f;
        public float DamageBonus { get; set; } = 1.15f;
        
        public override string Description => 
            $"Charge forward stunning enemies for {StunDuration:F0}s\n" +
            $"-{(1f - DamageReduction) * 100:F0}% damage taken\n" +
            $"+{(DamageBonus - 1f) * 100:F0}% damage dealt";
        
        public JuggernautEffect()
        {
            m_name = "Unstoppable Charge";
            Duration = 6f;
            MainArchetype = ArchetypeClass.Tank;
            SubArchetype = ArchetypeClass.Berserker;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            // Stun nearby enemies
            StunNearbyEnemies();
            
            m_character.Message(MessageHud.MessageType.Center, "UNSTOPPABLE!");
            
            // VFX
            AbilityFXManager.SpawnEffect("vfx_sledge_hit", m_character.transform.position, null, 1.5f);
            AbilityFXManager.SpawnEffect("vfx_MeadBzerker", m_character.transform.position, null, 0.8f);
        }
        
        private void StunNearbyEnemies()
        {
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (!BaseAI.IsEnemy(m_character, character)) continue;
                if (character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= ChargeRange)
                {
                    // Apply stagger/stun
                    character.Stagger(m_character.transform.position - character.transform.position);
                    StatusEffectManager.ApplyRoot(character, StunDuration, m_character);
                    
                    // VFX on stunned enemy
                    AbilityFXManager.SpawnEffect("vfx_troll_groundslam", character.transform.position, null, 0.5f);
                }
            }
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            hit.m_damage.Modify(DamageReduction);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.Modify(DamageBonus);
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<JuggernautEffect>();
            effect.name = "CompanionUnstoppableCharge";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionUnstoppableCharge");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
}
