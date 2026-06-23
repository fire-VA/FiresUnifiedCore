using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Paladin
{
    /// <summary>
    /// Paladin + Tank = Templar - Sacred Shield
    /// Grants a divine shield to all nearby allies.
    /// 
    /// THEME: A holy knight focused on protecting the faithful.
    /// The Templar excels at keeping their entire party alive through divine intervention.
    /// </summary>
    public class TemplarEffect : HybridAbilityEffect
    {
        public float ShieldAmount { get; set; } = 100f;
        public float DamageReduction { get; set; } = 0.88f;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Grant divine shield to all allies\n" +
            $"Absorbs {ShieldAmount:F0} damage\n" +
            $"-{(1f - DamageReduction) * 100:F0}% damage taken";
        
        public TemplarEffect()
        {
            m_name = "Sacred Shield";
            Duration = 12f;
            MainArchetype = ArchetypeClass.Paladin;
            SubArchetype = ArchetypeClass.Tank;
            GroupRange = 15f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            // Apply shield to all allies
            ApplyShieldToAllies();
            
            m_character.Message(MessageHud.MessageType.Center, "Sacred Shield!");
            AbilityFXManager.SpawnEffect("fx_shield_start", m_character.transform.position, null, GroupRange / 8f);
        }
        
        private void ApplyShieldToAllies()
        {
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= GroupRange)
                {
                    StatusEffectManager.ApplyArcaneShield(character, Duration, ShieldAmount);
                    AbilityFXManager.SpawnEffect("vfx_GoblinShield", character.transform.position, null, 0.8f);
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<TemplarEffect>();
            effect.name = "CompanionSacredShield";
            effect.Duration = duration;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Paladin + Berserker = Zealot - Holy Fury
    /// Enter a divine rage, dealing spirit damage with each hit.
    /// 
    /// THEME: A fanatic warrior empowered by righteous fury.
    /// The Zealot channels divine wrath into devastating attacks that purify the unholy.
    /// </summary>
    public class ZealotEffect : HybridAbilityEffect
    {
        public float SpiritDamageBonus { get; set; } = 30f;
        public float AttackSpeedBonus { get; set; } = 1.3f;
        public float HealthThreshold { get; set; } = 0.5f;
        public float LowHealthDamageBonus { get; set; } = 1.25f;
        
        public override string Description => 
            $"+{SpiritDamageBonus:F0} spirit damage per hit\n" +
            $"+{(AttackSpeedBonus - 1f) * 100:F0}% attack speed\n" +
            $"Below {HealthThreshold * 100:F0}% HP: +{(LowHealthDamageBonus - 1f) * 100:F0}% damage";
        
        public ZealotEffect()
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
            AbilityFXManager.SpawnEffect("vfx_spiritbolt_explosion", m_character?.transform.position ?? Vector3.zero, null, 1f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            hitData.m_damage.m_spirit += SpiritDamageBonus;
            
            // Bonus damage at low health
            if (m_character != null && m_character.GetHealthPercentage() < HealthThreshold)
            {
                hitData.m_damage.Modify(LowHealthDamageBonus);
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<ZealotEffect>();
            effect.name = "CompanionHolyFury";
            effect.Duration = duration;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Paladin + Rogue = Inquisitor - Divine Judgment
    /// Mark an enemy; your next attack deals massive spirit damage.
    /// 
    /// THEME: A holy hunter who strikes down evil from the shadows.
    /// The Inquisitor hunts heretics with divine precision, marking them for judgment.
    /// </summary>
    public class InquisitorEffect : HybridAbilityEffect
    {
        public float MarkDamageMultiplier { get; set; } = 2.5f;
        public float SpiritBonusDamage { get; set; } = 50f;
        public int JudgmentCharges { get; set; } = 3;
        
        private int _remainingCharges;
        
        public override string Description => 
            $"Divine Judgment ready\n" +
            $"Marked targets take {MarkDamageMultiplier:F1}x damage\n" +
            $"+{SpiritBonusDamage:F0} spirit damage\n" +
            $"Charges: {_remainingCharges}";
        
        public InquisitorEffect()
        {
            m_name = "Divine Judgment";
            Duration = 20f;
            MainArchetype = ArchetypeClass.Paladin;
            SubArchetype = ArchetypeClass.Rogue;
        }
        
        protected override void OnEffectApplied()
        {
            _remainingCharges = JudgmentCharges;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Divine Judgment ready...");
            
            AbilityFXManager.SpawnEffect("vfx_spiritbolt_explosion", m_character?.transform.position ?? Vector3.zero, null, 0.6f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            if (_remainingCharges > 0)
            {
                hitData.m_damage.m_spirit += SpiritBonusDamage;
                hitData.m_damage.Modify(MarkDamageMultiplier);
                _remainingCharges--;
                
                m_character?.Message(MessageHud.MessageType.TopLeft, $"Judgment! ({_remainingCharges} remaining)");
                
                if (_remainingCharges <= 0)
                {
                    m_character?.GetSEMan()?.RemoveStatusEffect(name.GetStableHashCode(), true);
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<InquisitorEffect>();
            effect.name = "CompanionDivineJudgment";
            effect.Duration = duration;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Paladin + Ranger = Holy Archer - Radiant Arrow
    /// Fire a holy arrow that damages enemies and heals allies it passes.
    /// 
    /// THEME: A blessed marksman whose arrows carry divine light.
    /// The Holy Archer's projectiles are infused with healing and destructive holy energy.
    /// </summary>
    public class HolyArcherEffect : HybridAbilityEffect
    {
        public float SpiritDamageBonus { get; set; } = 25f;
        public float HealOnHit { get; set; } = 15f;
        public float AccuracyBonus { get; set; } = 1.2f;
        
        public override string Description => 
            $"Arrows deal +{SpiritDamageBonus:F0} spirit damage\n" +
            $"Heal {HealOnHit:F0} HP on hit\n" +
            $"+{(AccuracyBonus - 1f) * 100:F0}% accuracy";
        
        public HolyArcherEffect()
        {
            m_name = "Radiant Arrow";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Paladin;
            SubArchetype = ArchetypeClass.Ranger;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Radiant Arrows blessed!");
            
            AbilityFXManager.SpawnEffect("vfx_spiritbolt_explosion", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
            AbilityFXManager.SpawnEffect("fx_Lightning", m_character?.transform.position ?? Vector3.zero, null, 0.4f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            if (skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows)
            {
                hitData.m_damage.m_spirit += SpiritDamageBonus;
                
                // Self-heal on ranged hit
                m_character?.Heal(HealOnHit, true);
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<HolyArcherEffect>();
            effect.name = "CompanionRadiantArrow";
            effect.Duration = duration;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Paladin + Mage = Battle Priest - Divine Nova
    /// Release a burst of holy energy that damages enemies and buffs allies.
    /// 
    /// THEME: A warrior-mage wielding both steel and holy magic.
    /// The Battle Priest channels divine magic through their weapon strikes.
    /// </summary>
    public class BattlePriestEffect : HybridAbilityEffect
    {
        public float NovaDamage { get; set; } = 80f;
        public float NovaRange { get; set; } = 8f;
        public float AllyDamageBonus { get; set; } = 1.15f;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Divine Nova: {NovaDamage:F0} spirit damage\n" +
            $"Range: {NovaRange:F0}m\n" +
            $"Allies gain +{(AllyDamageBonus - 1f) * 100:F0}% damage";
        
        public BattlePriestEffect()
        {
            m_name = "Divine Nova";
            Duration = 12f;
            MainArchetype = ArchetypeClass.Paladin;
            SubArchetype = ArchetypeClass.Mage;
            GroupRange = 8f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            // Deal AoE damage to enemies
            DealNovaDamage();
            
            // Buff allies
            BuffAllies();
            
            m_character.Message(MessageHud.MessageType.Center, "Divine Nova!");
            AbilityFXManager.SpawnEffect("fx_goblinking_nova", m_character.transform.position, null, NovaRange / 10f);
        }
        
        private void DealNovaDamage()
        {
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (!BaseAI.IsEnemy(m_character, character)) continue;
                if (character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= NovaRange)
                {
                    var hit = new HitData
                    {
                        m_damage = { m_spirit = NovaDamage },
                        m_attacker = m_character.GetZDOID(),
                        m_point = character.transform.position
                    };
                    character.ApplyDamage(hit, true, true, HitData.DamageModifier.Normal);
                }
            }
        }
        
        private void BuffAllies()
        {
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= GroupRange)
                {
                    StatusEffectManager.ApplyHolySmite(character, Duration);
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<BattlePriestEffect>();
            effect.name = "CompanionDivineNova";
            effect.Duration = duration;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Paladin + Healer = High Priest - Mass Restoration
    /// Heal all nearby allies and grant temporary damage immunity.
    /// 
    /// THEME: A master healer with powerful protective abilities.
    /// The High Priest is the ultimate holy support, capable of massive group healing.
    /// </summary>
    public class HighPriestEffect : HybridAbilityEffect
    {
        public float HealAmount { get; set; } = 100f;
        public float ImmunityDuration { get; set; } = 2f;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Heal all allies for {HealAmount:F0} HP\n" +
            $"Grant {ImmunityDuration:F0}s damage immunity";
        
        public HighPriestEffect()
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
            AbilityFXManager.SpawnEffect("fx_DvergerMage_Support_start", m_character.transform.position, null, GroupRange / 15f);
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<HighPriestEffect>();
            effect.name = "CompanionMassRestoration";
            effect.Duration = duration;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Paladin + Monk = Ascetic Knight - Enlightened Strike
    /// A powerful attack that restores stamina and eitr on hit.
    /// 
    /// THEME: A disciplined holy warrior who has mastered body and spirit.
    /// The Ascetic Knight combines martial perfection with divine grace.
    /// </summary>
    public class AsceticKnightEffect : HybridAbilityEffect
    {
        public float StaminaRestoreOnHit { get; set; } = 20f;
        public float EitrRestoreOnHit { get; set; } = 15f;
        public float DamageBonus { get; set; } = 1.15f;
        public float SpiritDamageBonus { get; set; } = 15f;
        
        public override string Description => 
            $"+{(DamageBonus - 1f) * 100:F0}% damage\n" +
            $"+{SpiritDamageBonus:F0} spirit damage\n" +
            $"Restore {StaminaRestoreOnHit:F0} stamina on hit";
        
        public AsceticKnightEffect()
        {
            m_name = "Enlightened Strike";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Paladin;
            SubArchetype = ArchetypeClass.Monk;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Enlightened Strike!");
            
            AbilityFXManager.SpawnEffect("vfx_spiritbolt_explosion", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
            AbilityFXManager.SpawnEffect("vfx_Cold", m_character?.transform.position ?? Vector3.zero, null, 0.3f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            hitData.m_damage.Modify(DamageBonus);
            hitData.m_damage.m_spirit += SpiritDamageBonus;
            
            // Restore stamina on hit
            m_character?.AddStamina(StaminaRestoreOnHit);
            
            // Restore eitr for players
            if (m_character is Player player)
            {
                player.AddEitr(EitrRestoreOnHit);
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            
            var effect = ScriptableObject.CreateInstance<AsceticKnightEffect>();
            effect.name = "CompanionEnlightenedStrike";
            effect.Duration = duration;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            seman.AddStatusEffect(effect, true);
            return true;
        }
    }
}
