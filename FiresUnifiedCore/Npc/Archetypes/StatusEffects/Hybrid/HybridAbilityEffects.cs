using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.Archetypes.StatusEffects.Hybrid
{
    /// <summary>
    /// Base class for hybrid archetype special abilities.
    /// Each hybrid combination gets a unique ability that combines aspects of both archetypes.
    /// 
    /// DESIGN PHILOSOPHY:
    /// - Hybrid abilities should feel like a natural blend of both archetypes
    /// - They should be more powerful than base abilities but have longer cooldowns
    /// - Visual effects combine elements from both archetype schools
    /// </summary>
    public abstract class HybridAbilityEffect : CompanionStatusEffectBase
    {
        /// <summary>The main archetype class.</summary>
        public ArchetypeClass MainArchetype { get; set; }
        
        /// <summary>The sub archetype class.</summary>
        public ArchetypeClass SubArchetype { get; set; }
        
        /// <summary>Whether this ability affects the group.</summary>
        public virtual bool IsGroupAbility => false;
        
        /// <summary>Range for group abilities.</summary>
        public float GroupRange { get; set; } = 12f;
        
        public HybridAbilityEffect()
        {
            Duration = 10f;
        }
    }
    
    #region Tank Hybrids
    
    /// <summary>
    /// Tank + Paladin = Crusader - Divine Bulwark
    /// Creates a holy shield that blocks all damage for 3s and heals nearby allies.
    /// </summary>
    public class DivineBulwarkEffect : HybridAbilityEffect
    {
        public float InvulnerabilityDuration { get; set; } = 3f;
        public float HealAmount { get; set; } = 50f;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Holy shield blocks all damage for {InvulnerabilityDuration:F0}s\n" +
            $"Heals nearby allies for {HealAmount:F0} HP";
        
        public DivineBulwarkEffect()
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
                    character.Heal(HealAmount, true);
                    
                    // Visual effect
                    AbilityFXManager.SpawnEffect("fx_creature_tamed", character.transform.position, null, 0.5f);
                }
            }
        }
        
        public static bool ApplyDivineBulwark(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<DivineBulwarkEffect>();
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
    
    /// <summary>
    /// Tank + Berserker = Juggernaut - Unstoppable Charge
    /// Charges forward, stunning enemies and gaining damage reduction.
    /// </summary>
    public class UnstoppableChargeEffect : HybridAbilityEffect
    {
        public float DamageReduction { get; set; } = 0.5f;
        public float StunDuration { get; set; } = 2f;
        public float ChargeRange { get; set; } = 8f;
        
        public override string Description => 
            $"Charge forward stunning enemies for {StunDuration:F0}s\n" +
            $"-{(1f - DamageReduction) * 100:F0}% damage taken while active";
        
        public UnstoppableChargeEffect()
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
                }
            }
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            hit.m_damage.Modify(DamageReduction);
        }
        
        public static bool ApplyUnstoppableCharge(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<UnstoppableChargeEffect>();
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
    
    /// <summary>
    /// Tank + Rogue = Shadow Guardian - Counter Shadow
    /// After blocking, teleport behind the attacker and strike.
    /// </summary>
    public class CounterShadowEffect : HybridAbilityEffect
    {
        public float CounterDamageMultiplier { get; set; } = 2.0f;
        public int CounterCharges { get; set; } = 3;
        private int _remainingCharges;
        
        public override string Description => 
            $"After blocking, teleport behind attacker\n" +
            $"Counter strikes deal {CounterDamageMultiplier:F1}x damage\n" +
            $"Charges: {_remainingCharges}";
        
        public CounterShadowEffect()
        {
            m_name = "Counter Shadow";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Tank;
            SubArchetype = ArchetypeClass.Rogue;
        }
        
        protected override void OnEffectApplied()
        {
            _remainingCharges = CounterCharges;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Counter Shadow ready...");
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            
            // Check if we blocked and have charges
            if (_remainingCharges > 0 && attacker != null && hit.m_blockable)
            {
                // Teleport behind attacker
                TeleportBehind(attacker);
                _remainingCharges--;
                
                if (_remainingCharges <= 0)
                {
                    // Remove effect when charges depleted
                    m_character?.GetSEMan()?.RemoveStatusEffect(name.GetStableHashCode(), true);
                }
            }
        }
        
        private void TeleportBehind(Character attacker)
        {
            if (m_character == null || attacker == null) return;
            
            // Calculate position behind attacker
            Vector3 behindPos = attacker.transform.position - attacker.transform.forward * 2f;
            
            // Spawn vanish effect at current position
            AbilityFXManager.SpawnEffect("vfx_odin_despawn", m_character.transform.position, null, 0.5f);
            
            // Move character
            m_character.transform.position = behindPos;
            m_character.transform.LookAt(attacker.transform);
            
            // Spawn appear effect
            AbilityFXManager.SpawnEffect("vfx_ghost_death", behindPos, null, 0.5f);
        }
        
        public static bool ApplyCounterShadow(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<CounterShadowEffect>();
            effect.name = "CompanionCounterShadow";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionCounterShadow");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    #endregion
    
    #region Paladin Hybrids
    
    /// <summary>
    /// Paladin + Berserker = Zealot - Holy Fury
    /// Enter a divine rage, dealing spirit damage with each hit.
    /// </summary>
    public class HolyFuryEffect : HybridAbilityEffect
    {
        public float SpiritDamageBonus { get; set; } = 30f;
        public float AttackSpeedBonus { get; set; } = 1.3f;
        
        public override string Description => 
            $"+{SpiritDamageBonus:F0} spirit damage per hit\n" +
            $"+{(AttackSpeedBonus - 1f) * 100:F0}% attack speed";
        
        public HolyFuryEffect()
        {
            m_name = "Holy Fury";
            Duration = 12f;
            MainArchetype = ArchetypeClass.Paladin;
            SubArchetype = ArchetypeClass.Berserker;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.Center, "HOLY FURY!");
            AbilityFXManager.SpawnEffect("vfx_spray_fire", m_character?.transform.position ?? Vector3.zero, null, 0.7f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.m_spirit += SpiritDamageBonus;
        }
        
        public static bool ApplyHolyFury(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<HolyFuryEffect>();
            effect.name = "CompanionHolyFury";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionHolyFury");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Paladin + Healer = High Priest - Mass Restoration
    /// Heal all nearby allies and grant temporary damage immunity.
    /// </summary>
    public class MassRestorationEffect : HybridAbilityEffect
    {
        private const float VfxReferenceRange = 15f;

        public float HealAmount { get; set; } = 100f;
        public float ImmunityDuration { get; set; } = 2f;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Heal all allies for {HealAmount:F0} HP\n" +
            $"Grant {ImmunityDuration:F0}s damage immunity";
        
        public MassRestorationEffect()
        {
            m_name = "Mass Restoration";
            Duration = 1f; // Instant effect
            MainArchetype = ArchetypeClass.Paladin;
            SubArchetype = ArchetypeClass.Healer;
            GroupRange = 15f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            int healed = 0;
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= GroupRange)
                {
                    character.Heal(HealAmount, true);
                    StatusEffectManager.ApplyInvulnerable(character, ImmunityDuration);
                    healed++;
                    
                    AbilityFXManager.SpawnEffect("fx_creature_tamed", character.transform.position, null, 0.6f);
                }
            }
            
            m_character.Message(MessageHud.MessageType.Center, $"Mass Restoration! ({healed} healed)");
            AbilityFXManager.SpawnEffect("fx_DvergerMage_Support_start", m_character.transform.position, null, GroupRange / VfxReferenceRange);
        }
        
        public static bool ApplyMassRestoration(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<MassRestorationEffect>();
            effect.name = "CompanionMassRestoration";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionMassRestoration");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    #endregion
    
    #region Berserker Hybrids
    
    /// <summary>
    /// Berserker + Rogue = Reaper - Frenzy Strike
    /// Attack speed increases with each hit, critical hits restore stamina.
    /// </summary>
    public class FrenzyStrikeEffect : HybridAbilityEffect
    {
        public float BaseAttackSpeedBonus { get; set; } = 1.1f;
        public float AttackSpeedPerStack { get; set; } = 0.05f;
        public float StaminaRestoreOnCrit { get; set; } = 15f;
        public int MaxStacks { get; set; } = 10;
        
        private int _currentStacks = 0;
        
        public override string Description => 
            $"Attack speed: +{(BaseAttackSpeedBonus - 1f + _currentStacks * AttackSpeedPerStack) * 100:F0}%\n" +
            $"Crits restore {StaminaRestoreOnCrit:F0} stamina\n" +
            $"Stacks: {_currentStacks}/{MaxStacks}";
        
        public FrenzyStrikeEffect()
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
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            // Increase stack on hit
            if (_currentStacks < MaxStacks)
            {
                _currentStacks++;
            }
        }
        
        public static bool ApplyFrenzyStrike(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<FrenzyStrikeEffect>();
            effect.name = "CompanionFrenzyStrike";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionFrenzyStrike");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Berserker + Mage = Blood Mage - Blood Sacrifice
    /// Consume health to unleash a devastating magical attack.
    /// </summary>
    public class BloodSacrificeEffect : HybridAbilityEffect
    {
        private const float VfxReferenceRange = 8f;

        public float HealthCostPercent { get; set; } = 0.2f;
        public float DamageMultiplier { get; set; } = 3.0f;
        public float AoERange { get; set; } = 8f;
        
        public override string Description => 
            $"Sacrifice {HealthCostPercent * 100:F0}% HP\n" +
            $"Deal {DamageMultiplier:F1}x damage in {AoERange:F0}m AoE";
        
        public BloodSacrificeEffect()
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
            
            // Calculate damage based on sacrificed health
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
            
            AbilityFXManager.SpawnEffect("vfx_fireball_explosion", m_character.transform.position, null, AoERange / VfxReferenceRange);
        }
        
        public static bool ApplyBloodSacrifice(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<BloodSacrificeEffect>();
            effect.name = "CompanionBloodSacrifice";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionBloodSacrifice");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Berserker + Healer = Blood Knight - Sanguine Fury
    /// Damage dealt heals you and nearby allies.
    /// </summary>
    public class SanguineFuryEffect : HybridAbilityEffect
    {
        public float LifestealPercent { get; set; } = 0.3f;
        public float AllyHealPercent { get; set; } = 0.15f;
        public float DamageBonus { get; set; } = 1.2f;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"+{(DamageBonus - 1f) * 100:F0}% damage\n" +
            $"{LifestealPercent * 100:F0}% lifesteal\n" +
            $"Allies heal for {AllyHealPercent * 100:F0}% of damage";
        
        public SanguineFuryEffect()
        {
            m_name = "Sanguine Fury";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Berserker;
            SubArchetype = ArchetypeClass.Healer;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Blood empowers you!");
            AbilityFXManager.SpawnEffect("vfx_MeadBzerker", m_character?.transform.position ?? Vector3.zero, null, 1.0f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.Modify(DamageBonus);
        }
        
        public static bool ApplySanguineFury(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<SanguineFuryEffect>();
            effect.name = "CompanionSanguineFury";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionSanguineFury");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    #endregion
    
    #region Rogue Hybrids
    
    /// <summary>
    /// Rogue + Mage = Spellthief - Mana Drain
    /// Strike an enemy to steal eitr and briefly silence them.
    /// </summary>
    public class ManaDrainEffect : HybridAbilityEffect
    {
        public float EitrStealAmount { get; set; } = 30f;
        public float SilenceDuration { get; set; } = 3f;
        public int DrainCharges { get; set; } = 3;
        
        private int _remainingCharges;
        
        public override string Description => 
            $"Steal {EitrStealAmount:F0} eitr on hit\n" +
            $"Silence enemies for {SilenceDuration:F0}s\n" +
            $"Charges: {_remainingCharges}";
        
        public ManaDrainEffect()
        {
            m_name = "Mana Drain";
            Duration = 20f;
            MainArchetype = ArchetypeClass.Rogue;
            SubArchetype = ArchetypeClass.Mage;
        }
        
        protected override void OnEffectApplied()
        {
            _remainingCharges = DrainCharges;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Mana Drain ready...");
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            if (_remainingCharges > 0)
            {
                // Add eitr to self
                if (m_character is Player player)
                {
                    player.AddEitr(EitrStealAmount);
                }
                
                _remainingCharges--;
                AbilityFXManager.SpawnEffect("vfx_Potion_eitr_minor", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
            }
        }
        
        public static bool ApplyManaDrain(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<ManaDrainEffect>();
            effect.name = "CompanionManaDrain";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionManaDrain");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Rogue + Monk = Shadow Monk/Ninja - Vanishing Strike
    /// Disappear and reappear behind target with a stunning blow.
    /// </summary>
    public class VanishingStrikeEffect : HybridAbilityEffect
    {
        public float StunDuration { get; set; } = 2f;
        public float BonusDamage { get; set; } = 50f;
        public int StrikeCharges { get; set; } = 2;
        
        private int _remainingCharges;
        
        public override string Description => 
            $"Teleport behind target\n" +
            $"+{BonusDamage:F0} bonus damage, {StunDuration:F0}s stun\n" +
            $"Charges: {_remainingCharges}";
        
        public VanishingStrikeEffect()
        {
            m_name = "Vanishing Strike";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Rogue;
            SubArchetype = ArchetypeClass.Monk;
        }
        
        protected override void OnEffectApplied()
        {
            _remainingCharges = StrikeCharges;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Vanishing Strike ready...");
        }
        
        public static bool ApplyVanishingStrike(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<VanishingStrikeEffect>();
            effect.name = "CompanionVanishingStrike";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionVanishingStrike");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    #endregion
    
    #region Ranger Hybrids
    
    /// <summary>
    /// Ranger + Mage = Arcane Archer - Elemental Arrow
    /// Fire an arrow that explodes with elemental damage on impact.
    /// </summary>
    public class ElementalArrowEffect : HybridAbilityEffect
    {
        private const int ElementCount = 3;

        public float ElementalDamage { get; set; } = 40f;
        public float ExplosionRadius { get; set; } = 4f;
        
        private int _currentElement = 0; // 0=fire, 1=frost, 2=lightning
        
        public override string Description
        {
            get
            {
                string element = _currentElement switch
                {
                    0 => "Fire",
                    1 => "Frost",
                    2 => "Lightning",
                    _ => "Unknown"
                };
                return $"Arrows deal +{ElementalDamage:F0} {element} damage\n" +
                       $"Explode in {ExplosionRadius:F0}m radius";
            }
        }
        
        public ElementalArrowEffect()
        {
            m_name = "Elemental Arrow";
            Duration = 20f;
            MainArchetype = ArchetypeClass.Ranger;
            SubArchetype = ArchetypeClass.Mage;
        }
        
        protected override void OnEffectApplied()
        {
            _currentElement = 0;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Arrows infused with fire!");
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            // Only for ranged attacks
            if (skill != Skills.SkillType.Bows && skill != Skills.SkillType.Crossbows) return;
            
            switch (_currentElement)
            {
                case 0:
                    hitData.m_damage.m_fire += ElementalDamage;
                    break;
                case 1:
                    hitData.m_damage.m_frost += ElementalDamage;
                    break;
                case 2:
                    hitData.m_damage.m_lightning += ElementalDamage;
                    break;
            }
            
            // Cycle element
            _currentElement = (_currentElement + 1) % ElementCount;
        }
        
        public static bool ApplyElementalArrow(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<ElementalArrowEffect>();
            effect.name = "CompanionElementalArrow";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionElementalArrow");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Ranger + Rogue = Sniper - Assassin's Shot
    /// A powerful shot from stealth that deals massive critical damage.
    /// </summary>
    public class AssassinsShotEffect : HybridAbilityEffect
    {
        public float CritMultiplier { get; set; } = 3.0f;
        public float BonusCritChance { get; set; } = 0.5f;
        
        public override string Description => 
            $"+{BonusCritChance * 100:F0}% critical chance\n" +
            $"Critical hits deal {CritMultiplier:F1}x damage";
        
        public AssassinsShotEffect()
        {
            m_name = "Assassin's Shot";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Ranger;
            SubArchetype = ArchetypeClass.Rogue;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Taking aim...");
            
            // Also apply stealth
            StatusEffectManager.ApplyStealth(m_character, Duration);
        }
        
        public static bool ApplyAssassinsShot(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<AssassinsShotEffect>();
            effect.name = "CompanionAssassinsShot";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionAssassinsShot");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    #endregion
    
    #region Mage Hybrids
    
    /// <summary>
    /// Mage + Berserker = Pyromancer - Inferno
    /// Surround yourself with flames, damaging nearby enemies.
    /// </summary>
    public class InfernoEffect : HybridAbilityEffect
    {
        private const float VfxReferenceRadius = 5f;

        public float DamagePerSecond { get; set; } = 20f;
        public float AuraRadius { get; set; } = 5f;
        
        private float _lastTickTime;
        private const float TickInterval = 0.5f;
        
        public override string Description => 
            $"Burn enemies for {DamagePerSecond:F0} fire damage/sec\n" +
            $"Radius: {AuraRadius:F0}m";
        
        public InfernoEffect()
        {
            m_name = "Inferno";
            Duration = 12f;
            MainArchetype = ArchetypeClass.Mage;
            SubArchetype = ArchetypeClass.Berserker;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.Center, "INFERNO!");
            _lastTickTime = Time.time;
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (Time.time - _lastTickTime >= TickInterval)
            {
                _lastTickTime = Time.time;
                DamageNearbyEnemies();
            }
        }
        
        private void DamageNearbyEnemies()
        {
            if (m_character == null) return;
            
            float damage = DamagePerSecond * TickInterval;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (!BaseAI.IsEnemy(m_character, character)) continue;
                if (character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= AuraRadius)
                {
                    var hit = new HitData
                    {
                        m_damage = { m_fire = damage },
                        m_attacker = m_character.GetZDOID(),
                        m_point = character.transform.position
                    };
                    character.ApplyDamage(hit, true, false, HitData.DamageModifier.Normal);
                }
            }
            
            // Visual effect
            AbilityFXManager.SpawnEffect("vfx_Burning", m_character.transform.position, null, AuraRadius / VfxReferenceRadius);
        }
        
        public static bool ApplyInferno(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<InfernoEffect>();
            effect.name = "CompanionInferno";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionInferno");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Mage + Ranger = Storm Caller - Chain Lightning
    /// Lightning arcs between enemies, dealing increasing damage.
    /// </summary>
    public class ChainLightningEffect : HybridAbilityEffect
    {
        public float BaseDamage { get; set; } = 25f;
        public float DamageIncreasePerJump { get; set; } = 1.2f;
        public int MaxJumps { get; set; } = 5;
        public float JumpRange { get; set; } = 8f;
        
        public override string Description => 
            $"Lightning chains to {MaxJumps} enemies\n" +
            $"Base: {BaseDamage:F0}, +{(DamageIncreasePerJump - 1f) * 100:F0}% per jump";
        
        public ChainLightningEffect()
        {
            m_name = "Chain Lightning";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Mage;
            SubArchetype = ArchetypeClass.Ranger;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Lightning courses through you!");
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.m_lightning += BaseDamage;
        }
        
        public static bool ApplyChainLightning(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<ChainLightningEffect>();
            effect.name = "CompanionChainLightning";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionChainLightning");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    #endregion
    
    #region Healer Hybrids
    
    /// <summary>
    /// Healer + Mage = Arcane Healer - Mana Transfusion
    /// Convert eitr into powerful heals and magical shields.
    /// </summary>
    public class ManaTransfusionEffect : HybridAbilityEffect
    {
        public float HealPerEitr { get; set; } = 2f;
        public float ShieldPerEitr { get; set; } = 1.5f;
        public float EitrCostPerTick { get; set; } = 5f;
        
        private float _lastTickTime;
        private const float TickInterval = 1f;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Convert eitr to healing and shields\n" +
            $"{HealPerEitr:F1} HP and {ShieldPerEitr:F1} shield per eitr";
        
        public ManaTransfusionEffect()
        {
            m_name = "Mana Transfusion";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Healer;
            SubArchetype = ArchetypeClass.Mage;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Channeling mana into healing...");
            _lastTickTime = Time.time;
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (Time.time - _lastTickTime >= TickInterval)
            {
                _lastTickTime = Time.time;
                ChannelHealing();
            }
        }
        
        private void ChannelHealing()
        {
            if (m_character == null) return;
            
            // Check eitr cost (only for players)
            if (m_character is Player player)
            {
                if (player.GetEitr() < EitrCostPerTick)
                {
                    return; // Not enough eitr
                }
                player.UseEitr(EitrCostPerTick);
            }
            
            float healAmount = EitrCostPerTick * HealPerEitr;
            
            // Heal nearby allies
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= GroupRange)
                {
                    character.Heal(healAmount, true);
                }
            }
        }
        
        public static bool ApplyManaTransfusion(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<ManaTransfusionEffect>();
            effect.name = "CompanionManaTransfusion";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionManaTransfusion");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    #endregion
    
    #region Monk Hybrids
    
    /// <summary>
    /// Monk + Berserker = Drunken Master - Drunken Frenzy
    /// Attack wildly with bonus damage and dodge chance.
    /// </summary>
    public class DrunkenFrenzyEffect : HybridAbilityEffect
    {
        public float DamageBonus { get; set; } = 1.25f;
        public float DodgeBonus { get; set; } = 0.3f;
        public float AttackSpeedBonus { get; set; } = 1.2f;
        
        public override string Description => 
            $"+{(DamageBonus - 1f) * 100:F0}% damage\n" +
            $"+{DodgeBonus * 100:F0}% dodge chance\n" +
            $"+{(AttackSpeedBonus - 1f) * 100:F0}% attack speed";
        
        public DrunkenFrenzyEffect()
        {
            m_name = "Drunken Frenzy";
            Duration = 12f;
            MainArchetype = ArchetypeClass.Monk;
            SubArchetype = ArchetypeClass.Berserker;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.Center, "Hic! *stumbles menacingly*");
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.Modify(DamageBonus);
        }
        
        public static bool ApplyDrunkenFrenzy(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<DrunkenFrenzyEffect>();
            effect.name = "CompanionDrunkenFrenzy";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionDrunkenFrenzy");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Monk + Mage = Elementalist - Elemental Fist
    /// Attacks cycle through fire, frost, and lightning damage.
    /// </summary>
    public class ElementalFistEffect : HybridAbilityEffect
    {
        private const int ElementCount = 3;

        public float ElementalDamage { get; set; } = 25f;
        
        private int _currentElement = 0;
        
        public override string Description
        {
            get
            {
                string element = _currentElement switch
                {
                    0 => "Fire",
                    1 => "Frost",
                    2 => "Lightning",
                    _ => "Unknown"
                };
                return $"Fists deal +{ElementalDamage:F0} {element} damage\n" +
                       "Elements cycle with each attack";
            }
        }
        
        public ElementalFistEffect()
        {
            m_name = "Elemental Fist";
            Duration = 20f;
            MainArchetype = ArchetypeClass.Monk;
            SubArchetype = ArchetypeClass.Mage;
        }
        
        protected override void OnEffectApplied()
        {
            _currentElement = 0;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Fists burn with elemental power!");
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            switch (_currentElement)
            {
                case 0:
                    hitData.m_damage.m_fire += ElementalDamage;
                    break;
                case 1:
                    hitData.m_damage.m_frost += ElementalDamage;
                    break;
                case 2:
                    hitData.m_damage.m_lightning += ElementalDamage;
                    break;
            }
            
            _currentElement = (_currentElement + 1) % ElementCount;
        }

        public static bool ApplyElementalFist(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<ElementalFistEffect>();
            effect.name = "CompanionElementalFist";
            effect.Duration = duration;
            
            var icon = StatusEffectManager.GetEffectIcon("CompanionElementalFist");
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    #endregion
}
