using UnityEngine;

namespace FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Healer
{
    /// <summary>
    /// Healer + Tank = War Cleric - Divine Protection
    /// Grant damage immunity to yourself and nearby allies briefly.
    /// 
    /// THEME: A healer who protects allies with shield and faith.
    /// The War Cleric stands at the front lines, shielding allies with divine power.
    /// </summary>
    public class WarClericEffect : HybridAbilityEffect
    {
        public float ImmunityDuration { get; set; } = 2f;
        public float DamageReduction { get; set; } = 0.85f;
        public float HealPerSecond { get; set; } = 5f;
        
        private float _lastHealTick;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Divine Protection active\n" +
            $"-{(1f - DamageReduction) * 100:F0}% damage taken\n" +
            $"+{HealPerSecond:F0} HP/sec to allies";
        
        public WarClericEffect()
        {
            m_name = "Divine Protection";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Healer;
            SubArchetype = ArchetypeClass.Tank;
            GroupRange = 12f;
        }
        
        protected override void OnEffectApplied()
        {
            _lastHealTick = Time.time;
            
            // Grant initial immunity to nearby allies
            if (m_character != null)
            {
                var characters = Character.GetAllCharacters();
                foreach (var character in characters)
                {
                    if (character == null || character.IsDead()) continue;
                    if (BaseAI.IsEnemy(m_character, character)) continue;
                    
                    float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                    if (dist <= GroupRange)
                    {
                        StatusEffectManager.ApplyInvulnerable(character, ImmunityDuration);
                    }
                }
            }
            
            m_character?.Message(MessageHud.MessageType.Center, "Divine Protection!");
            AbilityFXManager.SpawnEffect("fx_shield_start", m_character?.transform.position ?? Vector3.zero, null, GroupRange / 8f);
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (Time.time - _lastHealTick >= 1f)
            {
                _lastHealTick = Time.time;
                HealNearbyAllies();
            }
        }
        
        private void HealNearbyAllies()
        {
            if (m_character == null) return;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= GroupRange)
                {
                    character.Heal(HealPerSecond, true);
                }
            }
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            hit.m_damage.Modify(DamageReduction);
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<WarClericEffect>();
            effect.name = "CompanionDivineProtectionWC";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Healer + Paladin = Oracle - Divine Foresight
    /// Predict incoming damage and pre-heal allies, granting brief immunity.
    /// 
    /// THEME: A divine healer with prophetic powers.
    /// The Oracle sees threats before they happen and prepares allies accordingly.
    /// </summary>
    public class OracleEffect : HybridAbilityEffect
    {
        public float PreHealAmount { get; set; } = 50f;
        public float ImmunityDuration { get; set; } = 1.5f;
        public float CooldownReduction { get; set; } = 0.8f;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Divine Foresight active\n" +
            $"Pre-heal: {PreHealAmount:F0} HP\n" +
            $"Immunity: {ImmunityDuration:F1}s on low health";
        
        public OracleEffect()
        {
            m_name = "Divine Foresight";
            Duration = 20f;
            MainArchetype = ArchetypeClass.Healer;
            SubArchetype = ArchetypeClass.Paladin;
            GroupRange = 15f;
        }
        
        protected override void OnEffectApplied()
        {
            // Pre-heal all allies
            if (m_character != null)
            {
                var characters = Character.GetAllCharacters();
                foreach (var character in characters)
                {
                    if (character == null || character.IsDead()) continue;
                    if (BaseAI.IsEnemy(m_character, character)) continue;
                    
                    float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                    if (dist <= GroupRange)
                    {
                        character.Heal(PreHealAmount, true);
                        AbilityFXManager.SpawnEffect("fx_creature_tamed", character.transform.position, null, 0.4f);
                    }
                }
            }
            
            m_character?.Message(MessageHud.MessageType.Center, "Divine Foresight!");
            AbilityFXManager.SpawnEffect("fx_DvergerMage_Support_start", m_character?.transform.position ?? Vector3.zero, null, GroupRange / 15f);
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<OracleEffect>();
            effect.name = "CompanionDivineForesight";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Healer + Berserker = Pain Shaman - Blood Pact
    /// Take damage to heal allies for double the amount.
    /// 
    /// THEME: A healer who channels pain into restoration.
    /// The Pain Shaman sacrifices themselves to empower their healing.
    /// </summary>
    public class PainShamanEffect : HybridAbilityEffect
    {
        public float SacrificePercent { get; set; } = 0.1f;
        public float HealMultiplier { get; set; } = 2.5f;
        public int SacrificeCharges { get; set; } = 5;
        
        private int _remainingCharges;
        private float _lastSacrificeTime;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Blood Pact active\n" +
            $"Sacrifice {SacrificePercent * 100:F0}% HP\n" +
            $"Heal allies for {HealMultiplier:F1}x amount\n" +
            $"Charges: {_remainingCharges}";
        
        public PainShamanEffect()
        {
            m_name = "Blood Pact";
            Duration = 30f;
            MainArchetype = ArchetypeClass.Healer;
            SubArchetype = ArchetypeClass.Berserker;
            GroupRange = 12f;
        }
        
        protected override void OnEffectApplied()
        {
            _remainingCharges = SacrificeCharges;
            _lastSacrificeTime = 0f;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Blood Pact ready...");
            AbilityFXManager.SpawnEffect("vfx_MeadBzerker", m_character?.transform.position ?? Vector3.zero, null, 0.6f);
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            // Auto-sacrifice when allies are low (every 3 seconds)
            if (_remainingCharges > 0 && Time.time - _lastSacrificeTime >= 3f)
            {
                if (HasLowHealthAlly())
                {
                    PerformSacrifice();
                }
            }
        }
        
        private bool HasLowHealthAlly()
        {
            if (m_character == null) return false;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= GroupRange && character.GetHealthPercentage() < 0.5f)
                {
                    return true;
                }
            }
            return false;
        }
        
        private void PerformSacrifice()
        {
            if (m_character == null || _remainingCharges <= 0) return;
            
            _lastSacrificeTime = Time.time;
            _remainingCharges--;
            
            // Self damage
            float sacrifice = m_character.GetMaxHealth() * SacrificePercent;
            var damage = new HitData { m_damage = { m_damage = sacrifice } };
            m_character.ApplyDamage(damage, true, false, HitData.DamageModifier.Normal);
            
            // Heal allies
            float healAmount = sacrifice * HealMultiplier;
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= GroupRange)
                {
                    character.Heal(healAmount, true);
                    AbilityFXManager.SpawnEffect("fx_creature_tamed", character.transform.position, null, 0.4f);
                }
            }
            
            m_character.Message(MessageHud.MessageType.TopLeft, $"Blood Pact! ({_remainingCharges} remaining)");
            AbilityFXManager.SpawnEffect("vfx_FireballHit", m_character.transform.position, null, 0.5f);
            
            if (_remainingCharges <= 0)
            {
                m_character.GetSEMan()?.RemoveStatusEffect(name.GetStableHashCode(), true);
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<PainShamanEffect>();
            effect.name = "CompanionBloodPact";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Healer + Rogue = Shadow Healer - Phantom Touch
    /// Become invisible while healing, cannot be targeted by enemies.
    /// 
    /// THEME: A healer who works from the shadows, unseen.
    /// The Shadow Healer supports allies while remaining undetected.
    /// </summary>
    public class ShadowHealerEffect : HybridAbilityEffect
    {
        public float HealPerSecond { get; set; } = 8f;
        public float StealthDuration { get; set; } = 10f;
        
        private float _lastHealTick;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Phantom Touch active\n" +
            $"Invisible while healing\n" +
            $"+{HealPerSecond:F0} HP/sec to nearby allies";
        
        public ShadowHealerEffect()
        {
            m_name = "Phantom Touch";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Healer;
            SubArchetype = ArchetypeClass.Rogue;
            GroupRange = 12f;
        }
        
        protected override void OnEffectApplied()
        {
            _lastHealTick = Time.time;
            
            // Grant stealth
            StatusEffectManager.ApplyStealth(m_character, StealthDuration);
            
            m_character?.Message(MessageHud.MessageType.TopLeft, "Phantom Touch...");
            AbilityFXManager.SpawnEffect("vfx_ghost_death", m_character?.transform.position ?? Vector3.zero, null, 0.6f);
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (Time.time - _lastHealTick >= 1f)
            {
                _lastHealTick = Time.time;
                HealNearbyAllies();
            }
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
                    character.Heal(HealPerSecond, true);
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<ShadowHealerEffect>();
            effect.name = "CompanionPhantomTouch";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Healer + Ranger = Nature Priest - Regrowth
    /// Create a healing zone that grows stronger over time.
    /// 
    /// THEME: A healer drawing power from nature itself.
    /// The Nature Priest channels the forest's regenerative power.
    /// </summary>
    public class NaturePriestEffect : HybridAbilityEffect
    {
        public float BaseHealPerSecond { get; set; } = 5f;
        public float HealGrowthPerSecond { get; set; } = 0.5f;
        public float MaxHealPerSecond { get; set; } = 15f;
        
        private float _lastHealTick;
        private float _elapsedTime;
        
        public override bool IsGroupAbility => true;
        
        public override string Description
        {
            get
            {
                float currentHeal = Mathf.Min(BaseHealPerSecond + _elapsedTime * HealGrowthPerSecond, MaxHealPerSecond);
                return $"Regrowth active\n" +
                       $"Healing: {currentHeal:F0} HP/sec (growing)\n" +
                       $"Max: {MaxHealPerSecond:F0} HP/sec";
            }
        }
        
        public NaturePriestEffect()
        {
            m_name = "Regrowth";
            Duration = 20f;
            MainArchetype = ArchetypeClass.Healer;
            SubArchetype = ArchetypeClass.Ranger;
            GroupRange = 10f;
        }
        
        protected override void OnEffectApplied()
        {
            _lastHealTick = Time.time;
            _elapsedTime = 0f;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Regrowth!");
            AbilityFXManager.SpawnEffect("fx_natureweapon_hit", m_character?.transform.position ?? Vector3.zero, null, 0.8f);
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            _elapsedTime += dt;
            
            if (Time.time - _lastHealTick >= 1f)
            {
                _lastHealTick = Time.time;
                HealNearbyAllies();
            }
        }
        
        private void HealNearbyAllies()
        {
            if (m_character == null) return;
            
            float currentHeal = Mathf.Min(BaseHealPerSecond + _elapsedTime * HealGrowthPerSecond, MaxHealPerSecond);
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= GroupRange)
                {
                    character.Heal(currentHeal, true);
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<NaturePriestEffect>();
            effect.name = "CompanionRegrowth";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Healer + Mage = Arcane Healer - Mana Transfusion
    /// Convert eitr into powerful heals and magical shields.
    /// 
    /// THEME: A healer who uses arcane magic to restore and protect.
    /// The Arcane Healer blends magical energy with restorative powers.
    /// </summary>
    public class ArcaneHealerEffect : HybridAbilityEffect
    {
        public float EitrToHealRatio { get; set; } = 2f;
        public float ShieldAmount { get; set; } = 60f;
        public float HealBurstAmount { get; set; } = 80f;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Mana Transfusion active\n" +
            $"Burst heal: {HealBurstAmount:F0} HP\n" +
            $"Grant {ShieldAmount:F0} arcane shield";
        
        public ArcaneHealerEffect()
        {
            m_name = "Mana Transfusion";
            Duration = 1f; // Instant burst
            MainArchetype = ArchetypeClass.Healer;
            SubArchetype = ArchetypeClass.Mage;
            GroupRange = 12f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= GroupRange)
                {
                    character.Heal(HealBurstAmount, true);
                    StatusEffectManager.ApplyArcaneShield(character, 10f, ShieldAmount);
                    AbilityFXManager.SpawnEffect("vfx_Potion_eitr_minor", character.transform.position, null, 0.5f);
                }
            }
            
            m_character.Message(MessageHud.MessageType.Center, "Mana Transfusion!");
            AbilityFXManager.SpawnEffect("fx_DvergerMage_Support_start", m_character.transform.position, null, GroupRange / 15f);
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<ArcaneHealerEffect>();
            effect.name = "CompanionManaTransfusion";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
    
    /// <summary>
    /// Healer + Monk = Chi Master - Chi Restoration
    /// Channel chi to heal all allies and restore their stamina.
    /// 
    /// THEME: A healer who channels life energy through meditation.
    /// The Chi Master restores both body and spirit.
    /// </summary>
    public class ChiMasterEffect : HybridAbilityEffect
    {
        public float HealPerSecond { get; set; } = 8f;
        public float StaminaRestorePerSecond { get; set; } = 10f;
        public float EitrRestorePerSecond { get; set; } = 5f;
        
        private float _lastTick;
        
        public override bool IsGroupAbility => true;
        
        public override string Description => 
            $"Chi Restoration active\n" +
            $"+{HealPerSecond:F0} HP/sec\n" +
            $"+{StaminaRestorePerSecond:F0} stamina/sec\n" +
            $"+{EitrRestorePerSecond:F0} eitr/sec";
        
        public ChiMasterEffect()
        {
            m_name = "Chi Restoration";
            Duration = 15f;
            MainArchetype = ArchetypeClass.Healer;
            SubArchetype = ArchetypeClass.Monk;
            GroupRange = 12f;
        }
        
        protected override void OnEffectApplied()
        {
            _lastTick = Time.time;
            m_character?.Message(MessageHud.MessageType.TopLeft, "Chi flows...");
            AbilityFXManager.SpawnEffect("vfx_Cold", m_character?.transform.position ?? Vector3.zero, null, 0.4f);
            AbilityFXManager.SpawnEffect("fx_creature_tamed", m_character?.transform.position ?? Vector3.zero, null, 0.5f);
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (Time.time - _lastTick >= 1f)
            {
                _lastTick = Time.time;
                RestoreAllies();
            }
        }
        
        private void RestoreAllies()
        {
            if (m_character == null) return;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist <= GroupRange)
                {
                    character.Heal(HealPerSecond, true);
                    character.AddStamina(StaminaRestorePerSecond);
                    
                    if (character is Player player)
                    {
                        player.AddEitr(EitrRestorePerSecond);
                    }
                }
            }
        }
        
        public static bool Apply(Character target, float duration)
        {
            if (target == null) return false;
            var effect = ScriptableObject.CreateInstance<ChiMasterEffect>();
            effect.name = "CompanionChiRestoration";
            effect.Duration = duration;
            target.GetSEMan()?.AddStatusEffect(effect, true);
            return true;
        }
    }
}
