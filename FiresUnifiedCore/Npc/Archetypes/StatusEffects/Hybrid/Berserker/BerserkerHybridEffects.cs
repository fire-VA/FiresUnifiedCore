using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Berserker
{
    /// <summary>
    /// Berserker + Tank = Ravager - Bloodthirst
    /// Attacks heal for a portion of damage dealt while active.
    /// 
    /// THEME: A rampaging warrior who becomes harder to kill as they fight.
    /// The Ravager sustains themselves through sheer violence.
    /// </summary>
    public class RavagerEffect : HybridAbilityEffect
    {
        public float LifestealPercent { get; set; } = 0.25f;
        public float DamageBonus { get; set; } = 1.15f;
        public float DamageReduction { get; set; } = 0.92f;
        
        public override string Description => 
            $"+{(DamageBonus - 1f) * 100:F0}% damage\n" +
            $"{LifestealPercent * 100:F0}% lifesteal\n" +
            $"-{(1f - DamageReduction) * 100:F0}% damage taken";
        
        public RavagerEffect()
        {
            m_name = "Bloodthirst";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Berserker;
            SubArchetype = ArchetypeClass.Tank;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.Center, "BLOODTHIRST!");
            AbilityFXManager.SpawnEffect("vfx_MeadBzerker", m_character?.transform.position ?? Vector3.zero, null, 1f);
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
            
            // Lifesteal
            float totalDamage = hitData.GetTotalDamage();
            float healAmount = totalDamage * LifestealPercent;
            m_character?.Heal(healAmount, true);
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<RavagerEffect>();
            effect.name = "CompanionBloodthirst";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Berserker + Paladin = Avenger - Wrathful Smite
    /// Deal massive damage increased by how much health you're missing.
    /// 
    /// THEME: A holy warrior fueled by righteous anger.
    /// The Avenger grows stronger as they suffer, channeling pain into divine wrath.
    /// </summary>
    public class AvengerEffect : HybridAbilityEffect
    {
        public float BaseDamageBonus { get; set; } = 1.2f;
        public float MaxMissingHealthBonus { get; set; } = 1.5f;
        public float SpiritDamageBonus { get; set; } = 20f;
        
        public override string Description
        {
            get
            {
                float missingHealthPercent = m_character != null ? 1f - m_character.GetHealthPercentage() : 0f;
                float currentBonus = BaseDamageBonus + (MaxMissingHealthBonus - 1f) * missingHealthPercent;
                return $"Wrathful Smite active\n" +
                       $"+{(currentBonus - 1f) * 100:F0}% damage (scales with missing HP)\n" +
                       $"+{SpiritDamageBonus:F0} spirit damage";
            }
        }
        
        public AvengerEffect()
        {
            m_name = "Wrathful Smite";
            Duration = 12f;
            MainArchetype = ArchetypeClass.Berserker;
            SubArchetype = ArchetypeClass.Paladin;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.Center, "WRATHFUL SMITE!");
            AbilityFXManager.SpawnEffect("vfx_MeadBzerker", m_character?.transform.position ?? Vector3.zero, null, 0.8f);
            AbilityFXManager.SpawnEffect("vfx_spiritbolt_explosion", m_character?.transform.position ?? Vector3.zero, null, 1f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            // Calculate bonus based on missing health
            float missingHealthPercent = m_character != null ? 1f - m_character.GetHealthPercentage() : 0f;
            float damageMultiplier = BaseDamageBonus + (MaxMissingHealthBonus - 1f) * missingHealthPercent;
            
            hitData.m_damage.Modify(damageMultiplier);
            hitData.m_damage.m_spirit += SpiritDamageBonus;
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<AvengerEffect>();
            effect.name = "CompanionWrathfulSmite";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Berserker + Rogue = Reaper - Frenzy Strike
    /// Attack speed increases with each hit, critical hits restore stamina.
    /// 
    /// THEME: A frenzied killer who strikes vital points in their rage.
    /// The Reaper combines berserker fury with assassin precision.
    /// </summary>
    public class ReaperEffect : HybridAbilityEffect
    {
        public float BaseAttackSpeedBonus { get; set; } = 1.1f;
        public float AttackSpeedPerStack { get; set; } = 0.05f;
        public float StaminaRestoreOnCrit { get; set; } = 15f;
        public float CritChanceBonus { get; set; } = 0.15f;
        public int MaxStacks { get; set; } = 10;
        
        private int _currentStacks = 0;
        
        public override string Description => 
            $"Attack speed: +{(BaseAttackSpeedBonus - 1f + _currentStacks * AttackSpeedPerStack) * 100:F0}%\n" +
            $"+{CritChanceBonus * 100:F0}% crit chance\n" +
            $"Crits restore {StaminaRestoreOnCrit:F0} stamina\n" +
            $"Stacks: {_currentStacks}/{MaxStacks}";
        
        public ReaperEffect()
        {
            m_name = "Frenzy Strike";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Berserker;
            SubArchetype = ArchetypeClass.Rogue;
        }
        
        protected override void OnEffectApplied()
        {
            _currentStacks = 0;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Frenzy building...");
            AbilityFXManager.SpawnEffect("vfx_MeadBzerker", m_character?.transform.position ?? Vector3.zero, null, 0.7f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            // Increase stack on hit
            if (_currentStacks < MaxStacks)
            {
                _currentStacks++;
            }
            
            // Check for crit (simplified - actual crit is handled elsewhere)
            if (UnityEngine.Random.value < CritChanceBonus)
            {
                m_character?.AddStamina(StaminaRestoreOnCrit);
                AbilityFXManager.SpawnEffect("fx_crit", m_character?.transform.position ?? Vector3.zero, null, 0.8f);
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<ReaperEffect>();
            effect.name = "CompanionFrenzyStrike";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Berserker + Ranger = Beast Hunter - Predator's Mark
    /// Mark a target; deal increased damage and gain speed when attacking it.
    /// 
    /// THEME: A savage hunter who excels at taking down large prey.
    /// The Beast Hunter combines ranged marking with berserker aggression.
    /// </summary>
    public class BeastHunterEffect : HybridAbilityEffect
    {
        public float DamageBonus { get; set; } = 1.3f;
        public float SpeedBonus { get; set; } = 1.15f;
        
        public override string Description => 
            $"Predator's Mark active\n" +
            $"+{(DamageBonus - 1f) * 100:F0}% damage to marked target\n" +
            $"+{(SpeedBonus - 1f) * 100:F0}% movement speed";
        
        public BeastHunterEffect()
        {
            m_name = "Predator's Mark";
            Duration = 20f;
            MainArchetype = ArchetypeClass.Berserker;
            SubArchetype = ArchetypeClass.Ranger;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Prey marked!");
            AbilityFXManager.SpawnEffect("vfx_MeadBzerker", m_character?.transform.position ?? Vector3.zero, null, 0.6f);
            AbilityFXManager.SpawnEffect("vfx_spawn", m_character?.transform.position ?? Vector3.zero, null, 0.6f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.Modify(DamageBonus);
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<BeastHunterEffect>();
            effect.name = "CompanionPredatorsMark";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Berserker + Mage = Blood Mage - Blood Sacrifice
    /// Consume health to unleash a devastating magical attack.
    /// 
    /// THEME: A dangerous practitioner who fuels magic with their own life force.
    /// The Blood Mage trades health for immense magical power.
    /// </summary>
    public class BloodMageEffect : HybridAbilityEffect
    {
        public float HealthCostPercent { get; set; } = 0.2f;
        public float DamageMultiplier { get; set; } = 3.0f;
        public float AoERange { get; set; } = 8f;
        
        public override string Description => 
            $"Sacrifice {HealthCostPercent * 100:F0}% HP\n" +
            $"Deal {DamageMultiplier:F1}x damage in {AoERange:F0}m AoE";
        
        public BloodMageEffect()
        {
            m_name = "Blood Sacrifice";
            Duration = 1f;
            MainArchetype = ArchetypeClass.Berserker;
            SubArchetype = ArchetypeClass.Mage;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            // Sacrifice health
            float healthCost = m_character.GetMaxHealth() * HealthCostPercent;
            var damage = new HitData { m_damage = { m_damage = healthCost } };
            m_character.ApplyDamage(damage, true, false, HitData.DamageModifier.Normal);
            
            // Calculate AoE damage based on sacrificed health
            float aoeDamage = healthCost * DamageMultiplier;
            
            // Deal AoE damage
            DealAoEDamage(aoeDamage);
            
            m_character.Message(MessageHud.MessageType.Center, "BLOOD SACRIFICE!");
        }
        
        private void DealAoEDamage(float damage)
        {
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (!BaseAI.IsEnemy(m_character, character)) continue;
                if (character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= AoERange)
                {
                    var hit = new HitData
                    {
                        m_damage = { m_fire = damage * 0.5f, m_spirit = damage * 0.5f },
                        m_attacker = m_character.GetZDOID(),
                        m_point = character.transform.position
                    };
                    character.ApplyDamage(hit, true, true, HitData.DamageModifier.Normal);
                }
            }
            
            AbilityFXManager.SpawnEffect("vfx_fireball_explosion", m_character.transform.position, null, AoERange / 8f);
            AbilityFXManager.SpawnEffect("vfx_spiritbolt_explosion", m_character.transform.position, null, AoERange / 10f);
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<BloodMageEffect>();
            effect.name = "CompanionBloodSacrifice";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Berserker + Healer = Blood Knight - Sanguine Fury
    /// Enter a state where damage dealt heals you and nearby allies.
    /// 
    /// THEME: A warrior who heals through violence.
    /// The Blood Knight sustains their party through aggressive combat.
    /// </summary>
    public class BloodKnightEffect : HybridAbilityEffect
    {
        public float LifestealPercent { get; set; } = 0.3f;
        public float AllyHealPercent { get; set; } = 0.15f;
        public float DamageBonus { get; set; } = 1.2f;
        public float HealRange { get; set; } = 12f;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"+{(DamageBonus - 1f) * 100:F0}% damage\n" +
            $"{LifestealPercent * 100:F0}% lifesteal\n" +
            $"Allies heal for {AllyHealPercent * 100:F0}% of damage";
        
        public BloodKnightEffect()
        {
            m_name = "Sanguine Fury";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Berserker;
            SubArchetype = ArchetypeClass.Healer;
            GroupRange = 12f;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Blood empowers you!");
            AbilityFXManager.SpawnEffect("vfx_MeadBzerker", m_character?.transform.position ?? Vector3.zero, null, 1f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.Modify(DamageBonus);
            
            float totalDamage = hitData.GetTotalDamage();
            
            // Self heal
            m_character?.Heal(totalDamage * LifestealPercent, true);
            
            // Heal nearby allies
            if (m_character != null)
            {
                float allyHeal = totalDamage * AllyHealPercent;
                var characters = Character.GetAllCharacters();
                foreach (var character in characters)
                {
                    if (character == null || character.IsDead()) continue;
                    if (character == m_character) continue;
                    if (BaseAI.IsEnemy(m_character, character)) continue;
                    
                    float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                    if (dist <= HealRange)
                    {
                        character.Heal(allyHeal, true);
                    }
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<BloodKnightEffect>();
            effect.name = "CompanionSanguineFury";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Berserker + Monk = Battle Rager - Focused Fury
    /// Enter a controlled rage with faster attacks and no stamina cost.
    /// 
    /// THEME: A disciplined berserker who channels rage into precise strikes.
    /// The Battle Rager maintains control while unleashing devastating combos.
    /// </summary>
    public class BattleRagerEffect : HybridAbilityEffect
    {
        public float DamageBonus { get; set; } = 1.15f;
        public float AttackSpeedBonus { get; set; } = 1.25f;
        public float StaminaCostReduction { get; set; } = 0.5f;
        
        public override string Description => 
            $"Focused Fury active\n" +
            $"+{(DamageBonus - 1f) * 100:F0}% damage\n" +
            $"+{(AttackSpeedBonus - 1f) * 100:F0}% attack speed\n" +
            $"-{StaminaCostReduction * 100:F0}% stamina cost";
        
        public BattleRagerEffect()
        {
            m_name = "Focused Fury";
            Duration = 12f;
            MainArchetype = ArchetypeClass.Berserker;
            SubArchetype = ArchetypeClass.Monk;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.Center, "FOCUSED FURY!");
            AbilityFXManager.SpawnEffect("vfx_MeadBzerker", m_character?.transform.position ?? Vector3.zero, null, 0.8f);
            AbilityFXManager.SpawnEffect("vfx_Cold", m_character?.transform.position ?? Vector3.zero, null, 0.4f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.Modify(DamageBonus);
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<BattleRagerEffect>();
            effect.name = "CompanionFocusedFury";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
}
