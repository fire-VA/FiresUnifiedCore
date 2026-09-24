using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Monk
{
    /// <summary>
    /// Monk + Tank = Stone Fist - Stone Stance
    /// Become immovable, blocking all damage and countering attacks.
    /// 
    /// THEME: A martial artist with unbreakable defense.
    /// The Stone Fist is an immovable object, deflecting all who oppose them.
    /// </summary>
    public class StoneFistEffect : HybridAbilityEffect
    {
        public float DamageReduction { get; set; } = 0.6f;
        public float CounterDamage { get; set; } = 50f;
        public float StaggerResist { get; set; } = 1f;
        
        public override string Description => 
            $"Stone Stance active\n" +
            $"-{(1f - DamageReduction) * 100:F0}% damage taken\n" +
            $"Counter: {CounterDamage:F0} damage\n" +
            $"Immune to stagger";
        
        public StoneFistEffect()
        {
            m_name = "Stone Stance";
            Duration = 12f;
            MainArchetype = ArchetypeClass.Monk;
            SubArchetype = ArchetypeClass.Tank;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.Center, "STONE STANCE!");
            AbilityFXManager.SpawnEffect("fx_shaman_protect", m_character?.transform.position ?? Vector3.zero, null, 0.8f);
            AbilityFXManager.SpawnEffect("vfx_sledge_hit", m_character?.transform.position ?? Vector3.zero, null, 0.6f);
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            hit.m_damage.Modify(DamageReduction);
            hit.m_staggerMultiplier = 0f; // Immune to stagger
            
            // Counter attack
            if (attacker != null && !attacker.IsPlayer())
            {
                var counterHit = new HitData
                {
                    m_damage = { m_blunt = CounterDamage },
                    m_attacker = m_character.GetZDOID(),
                    m_point = attacker.transform.position
                };
                attacker.ApplyDamage(counterHit, true, true, HitData.DamageModifier.Normal);
                
                AbilityFXManager.SpawnEffect("fx_fenring_frost", attacker.transform.position, null, 0.5f);
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<StoneFistEffect>();
            effect.name = "CompanionStoneStance";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Monk + Paladin = Temple Guardian - Sacred Fist
    /// Attacks deal spirit damage and heal nearby allies.
    /// 
    /// THEME: A holy martial artist protecting sacred places.
    /// The Temple Guardian channels divine energy through their strikes.
    /// </summary>
    public class TempleGuardianEffect : HybridAbilityEffect
    {
        public float SpiritDamageBonus { get; set; } = 25f;
        public float HealOnHit { get; set; } = 10f;
        public float HealRange { get; set; } = 10f;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Sacred Fist active\n" +
            $"+{SpiritDamageBonus:F0} spirit damage\n" +
            $"Heal allies for {HealOnHit:F0} HP per hit";
        
        public TempleGuardianEffect()
        {
            m_name = "Sacred Fist";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Monk;
            SubArchetype = ArchetypeClass.Paladin;
            GroupRange = 10f;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.TopLeft, "Sacred Fist!");
            AbilityFXManager.SpawnEffect("vfx_ghost_hit", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
            AbilityFXManager.SpawnEffect("vfx_Cold", m_character?.transform.position ?? Vector3.zero, null, 0.3f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.m_spirit += SpiritDamageBonus;
            
            // Heal allies on hit
            if (m_character != null)
            {
                var characters = Character.GetAllCharacters();
                foreach (var character in characters)
                {
                    if (character == null || character.IsDead()) continue;
                    if (BaseAI.IsEnemy(m_character, character)) continue;
                    
                    float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                    if (dist <= HealRange)
                    {
                        AbilityHeals.Apply(SourceCharacter ?? m_character, character, HealOnHit, true);
                    }
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<TempleGuardianEffect>();
            effect.name = "CompanionSacredFist";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Monk + Berserker = Drunken Master - Drunken Frenzy
    /// Attack wildly with bonus damage and dodge chance.
    /// 
    /// THEME: An unpredictable fighter whose movements confuse foes.
    /// The Drunken Master's erratic style makes them impossible to predict.
    /// </summary>
    public class DrunkenMasterEffect : HybridAbilityEffect
    {
        public float DamageBonus { get; set; } = 1.25f;
        public float DodgeChance { get; set; } = 0.25f;
        public float AttackSpeedBonus { get; set; } = 1.2f;
        
        public override string Description => 
            $"Drunken Frenzy!\n" +
            $"+{(DamageBonus - 1f) * 100:F0}% damage\n" +
            $"+{DodgeChance * 100:F0}% dodge chance\n" +
            $"+{(AttackSpeedBonus - 1f) * 100:F0}% attack speed";
        
        public DrunkenMasterEffect()
        {
            m_name = "Drunken Frenzy";
            Duration = 12f;
            MainArchetype = ArchetypeClass.Monk;
            SubArchetype = ArchetypeClass.Berserker;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.Center, "DRUNKEN FRENZY!");
            AbilityFXManager.SpawnEffect("vfx_MeadBzerker", m_character?.transform.position ?? Vector3.zero, null, 0.7f);
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            
            // Chance to dodge
            if (UnityEngine.Random.value < DodgeChance)
            {
                hit.m_damage.Modify(0f);
                m_character?.Message(MessageHud.MessageType.TopLeft, "Stumbled away!");
                AbilityFXManager.SpawnEffect("vfx_odin_despawn", m_character?.transform.position ?? Vector3.zero, null, 0.3f);
            }
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.Modify(DamageBonus);
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<DrunkenMasterEffect>();
            effect.name = "CompanionDrunkenFrenzy";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Monk + Rogue = Ninja - Vanishing Strike
    /// Disappear and reappear behind target with a stunning blow.
    /// 
    /// THEME: A silent warrior combining martial arts with stealth.
    /// The Ninja strikes from shadows with lethal precision.
    /// </summary>
    public class NinjaMonkEffect : HybridAbilityEffect
    {
        public float DamageBonus { get; set; } = 1.3f;
        public float CritChanceBonus { get; set; } = 0.2f;
        public float StealthDuration { get; set; } = 5f;
        public int VanishCharges { get; set; } = 3;
        
        private int _remainingCharges;
        
        public override string Description => 
            $"Vanishing Strike ready\n" +
            $"+{(DamageBonus - 1f) * 100:F0}% damage\n" +
            $"+{CritChanceBonus * 100:F0}% crit\n" +
            $"Vanish charges: {_remainingCharges}";
        
        public NinjaMonkEffect()
        {
            m_name = "Vanishing Strike";
            Duration = 20f;
            MainArchetype = ArchetypeClass.Monk;
            SubArchetype = ArchetypeClass.Rogue;
        }
        
        protected override void OnEffectApplied()
        {
            _remainingCharges = VanishCharges;
            StatusEffectManager.ApplyStealth(m_character, StealthDuration);
            m_character?.Message(MessageHud.MessageType.TopLeft, "Vanishing Strike ready...");
            AbilityFXManager.SpawnEffect("vfx_odin_despawn", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.Modify(DamageBonus);
            
            if (_remainingCharges > 0)
            {
                hitData.m_staggerMultiplier *= 2f;
                _remainingCharges--;
                
                // Re-stealth after strike
                StatusEffectManager.ApplyStealth(m_character, 2f);
                AbilityFXManager.SpawnEffect("vfx_ghost_death", m_character?.transform.position ?? Vector3.zero, null, 0.4f);
                
                if (_remainingCharges <= 0)
                {
                    m_character?.GetSEMan()?.RemoveStatusEffect(name.GetStableHashCode(), true);
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<NinjaMonkEffect>();
            effect.name = "CompanionVanishingStrike";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Monk + Ranger = Wind Walker - Wind Step
    /// Gain massive movement speed and attack speed briefly.
    /// 
    /// THEME: A swift martial artist who moves like the wind.
    /// The Wind Walker achieves superhuman speed through chi mastery.
    /// </summary>
    public class WindWalkerEffect : HybridAbilityEffect
    {
        public float SpeedBonus { get; set; } = 1.4f;
        public float AttackSpeedBonus { get; set; } = 1.3f;
        public float DamageBonus { get; set; } = 1.1f;
        public float StaminaRegenBonus { get; set; } = 2f;
        
        public override string Description => 
            $"Wind Step active\n" +
            $"+{(SpeedBonus - 1f) * 100:F0}% movement speed\n" +
            $"+{(AttackSpeedBonus - 1f) * 100:F0}% attack speed\n" +
            $"+{StaminaRegenBonus:F0}x stamina regen";
        
        public WindWalkerEffect()
        {
            m_name = "Wind Step";
            Duration = 10f;
            MainArchetype = ArchetypeClass.Monk;
            SubArchetype = ArchetypeClass.Ranger;
        }
        
        protected override void OnEffectApplied()
        {
            m_character?.Message(MessageHud.MessageType.Center, "WIND STEP!");
            AbilityFXManager.SpawnEffect("vfx_Cold", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
            AbilityFXManager.SpawnEffect("fx_Lightning", m_character?.transform.position ?? Vector3.zero, null, 0.3f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.Modify(DamageBonus);
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<WindWalkerEffect>();
            effect.name = "CompanionWindStep";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Monk + Mage = Elementalist - Elemental Fist
    /// Attacks cycle through fire, frost, and lightning damage.
    /// 
    /// THEME: A martial artist who channels elemental chi.
    /// The Elementalist infuses their strikes with primal forces.
    /// </summary>
    public class ElementalistEffect : HybridAbilityEffect
    {
        public float FireDamage { get; set; } = 20f;
        public float FrostDamage { get; set; } = 20f;
        public float LightningDamage { get; set; } = 20f;
        public float DamageBonus { get; set; } = 1.15f;
        
        private int _elementCycle = 0;
        
        public override string Description
        {
            get
            {
                string currentElement = (_elementCycle % 3) switch
                {
                    0 => "Fire",
                    1 => "Frost",
                    2 => "Lightning",
                    _ => "Fire"
                };
                return $"Elemental Fist: {currentElement}\n" +
                       $"+{(DamageBonus - 1f) * 100:F0}% damage\n" +
                       $"+{FireDamage:F0}/{FrostDamage:F0}/{LightningDamage:F0} elemental";
            }
        }
        
        public ElementalistEffect()
        {
            m_name = "Elemental Fist";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Monk;
            SubArchetype = ArchetypeClass.Mage;
        }
        
        protected override void OnEffectApplied()
        {
            _elementCycle = 0;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Elemental Fist!");
            AbilityFXManager.SpawnEffect("vfx_MeadHasty", m_character?.transform.position ?? Vector3.zero, null, 0.6f);
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            hitData.m_damage.Modify(DamageBonus);
            
            // Cycle through elements
            switch (_elementCycle % 3)
            {
                case 0:
                    hitData.m_damage.m_fire += FireDamage;
                    AbilityFXManager.SpawnEffect("vfx_FireballHit", m_character?.transform.position ?? Vector3.zero, null, 0.3f);
                    break;
                case 1:
                    hitData.m_damage.m_frost += FrostDamage;
                    AbilityFXManager.SpawnEffect("fx_fenring_frost", m_character?.transform.position ?? Vector3.zero, null, 0.4f);
                    break;
                case 2:
                    hitData.m_damage.m_lightning += LightningDamage;
                    AbilityFXManager.SpawnEffect("fx_Lightning", m_character?.transform.position ?? Vector3.zero, null, 0.3f);
                    break;
            }
            _elementCycle++;
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<ElementalistEffect>();
            effect.name = "CompanionElementalFist";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Monk + Healer = Chi Healer - Healing Palm
    /// Touch an ally to instantly restore health and cure ailments.
    /// 
    /// THEME: A martial artist who heals through touch.
    /// The Chi Healer channels life energy directly into allies.
    /// </summary>
    public class ChiHealerEffect : HybridAbilityEffect
    {
        public float HealPerTouch { get; set; } = 50f;
        public float HealRange { get; set; } = 3f;
        public float StaminaRestore { get; set; } = 30f;
        public int HealingTouches { get; set; } = 5;
        
        private int _remainingTouches;
        private float _lastTouchTime;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Healing Palm ready\n" +
            $"Heal: {HealPerTouch:F0} HP per touch\n" +
            $"Restore: {StaminaRestore:F0} stamina\n" +
            $"Touches: {_remainingTouches}";
        
        public ChiHealerEffect()
        {
            m_name = "Healing Palm";
            Duration = 30f;
            MainArchetype = ArchetypeClass.Monk;
            SubArchetype = ArchetypeClass.Healer;
            GroupRange = 3f;
        }
        
        protected override void OnEffectApplied()
        {
            _remainingTouches = HealingTouches;
            _lastTouchTime = 0f;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Healing Palm ready...");
            AbilityFXManager.SpawnEffect("vfx_Cold", m_character?.transform.position ?? Vector3.zero, null, 0.3f);
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            // Auto-heal closest low-health ally (every 2 seconds)
            if (_remainingTouches > 0 && Time.time - _lastTouchTime >= 2f)
            {
                Character lowestAlly = FindLowestHealthAlly();
                if (lowestAlly != null)
                {
                    PerformHealingTouch(lowestAlly);
                }
            }
        }
        
        private Character FindLowestHealthAlly()
        {
            if (m_character == null) return null;
            
            Character lowest = null;
            float lowestPercent = 0.7f; // Only heal if below 70%
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= HealRange && character.GetHealthPercentage() < lowestPercent)
                {
                    lowestPercent = character.GetHealthPercentage();
                    lowest = character;
                }
            }
            
            return lowest;
        }
        
        private void PerformHealingTouch(Character target)
        {
            if (target == null || _remainingTouches <= 0) return;
            
            _lastTouchTime = Time.time;
            _remainingTouches--;
            
            AbilityHeals.Apply(SourceCharacter ?? m_character, target, HealPerTouch, true);
            target.AddStamina(StaminaRestore);
            
            // Cleanse debuffs
            var seman = target.GetSEMan();
            if (seman != null)
            {
                seman.RemoveStatusEffect("Poison".GetStableHashCode(), true);
                seman.RemoveStatusEffect("Burning".GetStableHashCode(), true);
                seman.RemoveStatusEffect("Frost".GetStableHashCode(), true);
            }
            
            m_character?.Message(MessageHud.MessageType.TopLeft, $"Healing Palm! ({_remainingTouches} remaining)");
            AbilityFXManager.SpawnEffect("fx_creature_tamed", target.transform.position, null, 0.5f);
            
            if (_remainingTouches <= 0)
            {
                m_character?.GetSEMan()?.RemoveStatusEffect(name.GetStableHashCode(), true);
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<ChiHealerEffect>();
            effect.name = "CompanionHealingPalm";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
}
