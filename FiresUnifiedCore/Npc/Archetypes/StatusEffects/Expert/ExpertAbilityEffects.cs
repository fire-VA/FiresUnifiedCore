using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.Archetypes.StatusEffects.Expert
{
    /// <summary>
    /// Expert Ability Effects - Level 35-50 abilities for all archetypes.
    /// These are mid-to-late game abilities that enhance the archetype's capabilities.
    /// </summary>
    
    #region Tank - Iron Wall (L40)
    
    /// <summary>
    /// Iron Wall Effect (Tank L40) - Enhanced blocking passive.
    /// Increases block damage reduction from 40% to 60%.
    /// This is a PASSIVE effect - always active.
    /// </summary>
    public class IronWallEffect : CompanionStatusEffectBase
    {
        public float EnhancedBlockReduction { get; set; } = 0.4f; // Take only 40% damage when blocking (60% reduction)
        
        public override string Description => 
            $"<color=#AAAAFF>Iron Wall</color>\n" +
            $"Blocking reduces damage by {(1f - EnhancedBlockReduction) * 100:F0}%";
        
        public IronWallEffect()
        {
            m_name = "Iron Wall";
            m_tooltip = "Enhanced blocking technique";
            Duration = 0f; // Permanent passive
        }
        
        // Note: This needs to hook into the blocking system
        // For now, it marks the character as having enhanced block
        public bool HasEnhancedBlock => true;
        public float BlockDamageModifier => EnhancedBlockReduction;
    }
    
    #endregion
    
    #region Paladin - Consecration (L35)
    
    /// <summary>
    /// Consecration Effect (Paladin L35) - Ground AoE heal/damage zone.
    /// Heals allies and damages undead enemies in the area.
    /// </summary>
    public class ConsecrationEffect : CompanionStatusEffectBase
    {
        private const float DurationSeconds = 10f;

        public Vector3 GroundPosition { get; set; }
        public float Radius { get; set; } = 5f;
        public float HealPerTick { get; set; } = 8f;
        public float DamagePerTick { get; set; } = 15f; // Extra damage to undead
        public float TickInterval { get; set; } = 1f;
        
        private float _lastTickTime;
        
        public override string Description => 
            $"Consecrated ground\n" +
            $"Heals allies: {HealPerTick:F0}/s\n" +
            $"Damages undead: {DamagePerTick:F0}/s";
        
        public ConsecrationEffect()
        {
            m_name = "Consecration";
            m_tooltip = "Holy ground that heals allies and burns undead";
            Duration = DurationSeconds;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character != null)
            {
                GroundPosition = m_character.transform.position;
                _lastTickTime = Time.time;
                
                // Ground VFX
                SpawnVFX("vfx_spiritbolt_explosion", GroundPosition);
                
                m_character.Message(MessageHud.MessageType.TopLeft, "Consecrated Ground!");
            }
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (Time.time - _lastTickTime >= TickInterval)
            {
                _lastTickTime = Time.time;
                ApplyAreaEffects();
            }
        }
        
        private void ApplyAreaEffects()
        {
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                
                float dist = Vector3.Distance(GroundPosition, character.transform.position);
                if (dist > Radius) continue;
                
                if (character.IsTamed() || character.IsPlayer())
                {
                    // Heal allies
                    if (character.GetHealthPercentage() < 1f)
                    {
                        character.Heal(HealPerTick, true);
                    }
                }
                else
                {
                    // Damage enemies (especially undead)
                    var hitData = new HitData();
                    hitData.m_point = character.transform.position;
                    hitData.m_dir = Vector3.up;
                    hitData.m_attacker = m_character?.GetZDOID() ?? ZDOID.None;
                    hitData.m_damage.m_spirit = DamagePerTick;
                    
                    character.Damage(hitData);
                }
            }
            
            // Ground glow VFX
            SpawnVFX("vfx_spiritbolt_explosion", GroundPosition);
        }
        
        private void SpawnVFX(string prefabName, Vector3 position)
        {
            try
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                if (prefab != null)
                {
                    Object.Instantiate(prefab, position, Quaternion.identity);
                }
            }
            catch { }
        }
    }
    
    #endregion
    
    #region Paladin - Divine Shield (L50)
    
    /// <summary>
    /// Divine Shield Effect (Paladin L50) - Brief immunity.
    /// Cannot take damage but also cannot attack.
    /// </summary>
    public class DivineShieldEffect : CompanionStatusEffectBase
    {
        private const float AuraVfxChance = 0.3f;
        private const float DurationSeconds = 3f;

        public override string Description =>
            $"<color=gold>Divine Shield</color>\n" +
            $"Immune to all damage\n" +
            $"Cannot attack";
        
        /// <summary>Flag to prevent attacks.</summary>
        public bool PreventsAttack => true;
        
        public DivineShieldEffect()
        {
            m_name = "Divine Shield";
            m_tooltip = "Protected by divine light, but cannot attack";
            Duration = DurationSeconds;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character != null)
            {
                SpawnVFX("fx_shield_start", m_character.transform.position);
                SpawnVFX("vfx_spiritbolt_explosion", m_character.transform.position);
                m_character.Message(MessageHud.MessageType.Center, "<color=gold>Divine Shield!</color>");
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
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            // Shield aura
            if (m_character != null && Random.value < AuraVfxChance)
            {
                SpawnVFX("fx_shield_start", m_character.transform.position);
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Divine Shield fades");
            }
        }
        
        private void SpawnVFX(string prefabName, Vector3 position)
        {
            try
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                if (prefab != null)
                {
                    Object.Instantiate(prefab, position, Quaternion.identity);
                }
            }
            catch { }
        }
    }
    
    #endregion
    
    #region Berserker - Execute (L35)
    
    /// <summary>
    /// Execute Effect (Berserker L35) - Bonus damage to low HP enemies.
    /// Passive: +100% damage to enemies below 20% HP.
    /// </summary>
    public class ExecuteEffect : CompanionStatusEffectBase
    {
        public float HealthThreshold { get; set; } = 0.2f; // 20% HP
        public float DamageBonus { get; set; } = 2.0f; // Double damage
        
        public override string Description => 
            $"<color=#880000>Execute</color>\n" +
            $"+{(DamageBonus - 1f) * 100:F0}% damage to enemies below {HealthThreshold * 100:F0}% HP";
        
        public ExecuteEffect()
        {
            m_name = "Execute";
            m_tooltip = "Devastating damage to weakened foes";
            Duration = 0f; // Permanent passive
        }
        
        // This needs to be checked during combat - mark the character
        public bool HasExecute => true;
        public float ExecuteThreshold => HealthThreshold;
        public float ExecuteDamageBonus => DamageBonus;
    }
    
    #endregion
    
    #region Berserker - Rampage (L50)
    
    /// <summary>
    /// Rampage Effect (Berserker L50) - Kill stacking damage buff.
    /// Each kill increases damage by 10%, up to 5 stacks.
    /// </summary>
    public class RampageEffect : CompanionStatusEffectBase
    {
        public int CurrentStacks { get; private set; } = 0;
        public const int MAX_STACKS = 5;
        public const float DAMAGE_PER_STACK = 0.10f; // +10% per stack
        public const float STACK_DURATION = 10f; // Stacks fall off after 10s without a kill
        
        private float _lastKillTime;
        
        public override string Description => 
            $"<color=#FF4400>Rampage</color>\n" +
            $"Stacks: {CurrentStacks}/{MAX_STACKS}\n" +
            $"+{CurrentStacks * DAMAGE_PER_STACK * 100:F0}% damage";
        
        public RampageEffect()
        {
            m_name = "Rampage";
            m_tooltip = "Each kill increases damage";
            Duration = 0f; // Permanent passive, but stacks decay
        }
        
        /// <summary>
        /// Called when a kill is made.
        /// </summary>
        public void OnKill()
        {
            if (CurrentStacks < MAX_STACKS)
            {
                CurrentStacks++;
                _lastKillTime = Time.time;
                
                if (m_character != null)
                {
                    m_character.Message(MessageHud.MessageType.TopLeft, $"<color=#FF4400>Rampage x{CurrentStacks}!</color>");
                    SpawnVFX("vfx_MeadBzerker", m_character.transform.position);
                }
                
                Debug.Log($"[RampageEffect] {m_character?.m_name} Rampage stack: {CurrentStacks}");
            }
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            // Decay stacks if no kill recently
            if (CurrentStacks > 0 && Time.time - _lastKillTime > STACK_DURATION)
            {
                CurrentStacks = 0;
                if (m_character != null)
                {
                    m_character.Message(MessageHud.MessageType.TopLeft, "Rampage faded");
                }
            }
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            if (CurrentStacks > 0)
            {
                float bonus = 1f + (CurrentStacks * DAMAGE_PER_STACK);
                hitData.m_damage.Modify(bonus);
            }
        }
        
        private void SpawnVFX(string prefabName, Vector3 position)
        {
            try
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                if (prefab != null)
                {
                    Object.Instantiate(prefab, position, Quaternion.identity);
                }
            }
            catch { }
        }
    }
    
    #endregion
    
    #region Rogue - Evasion (L50)
    
    /// <summary>
    /// Evasion Effect (Rogue L50) - High dodge chance.
    /// 50% chance to completely avoid attacks.
    /// </summary>
    public class EvasionEffect : CompanionStatusEffectBase
    {
        private const float DurationSeconds = 8f;

        public float DodgeChance { get; set; } = 0.5f; // 50% dodge
        
        public override string Description => 
            $"<color=#AAAAAA>Evasion</color>\n" +
            $"{DodgeChance * 100:F0}% chance to dodge attacks";
        
        public EvasionEffect()
        {
            m_name = "Evasion";
            m_tooltip = "High chance to dodge incoming attacks";
            Duration = DurationSeconds;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character != null)
            {
                SpawnVFX("vfx_ghost_death", m_character.transform.position);
                m_character.Message(MessageHud.MessageType.TopLeft, "Evasion active!");
            }
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            // Roll for dodge
            if (Random.value < DodgeChance)
            {
                // Dodged! Negate all damage
                hit.m_damage.Modify(0f);
                
                if (m_character != null)
                {
                    SpawnVFX("vfx_ghost_death", m_character.transform.position);
                }
                
                if (VerboseLogging)
                {
                    Debug.Log($"[EvasionEffect] {m_character?.m_name} dodged attack!");
                }
            }
        }
        
        private void SpawnVFX(string prefabName, Vector3 position)
        {
            try
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                if (prefab != null)
                {
                    Object.Instantiate(prefab, position, Quaternion.identity);
                }
            }
            catch { }
        }
    }
    
    #endregion
    
    #region Ranger - Multishot (L35)
    
    /// <summary>
    /// Multishot Effect (Ranger L35) - Fire multiple arrows.
    /// This is a trigger effect - applies once then expires.
    /// </summary>
    public class MultishotEffect : CompanionStatusEffectBase
    {
        private const float ExpireAfterUseDuration = 0.1f;
        private const float DurationSeconds = 15f;

        public int ArrowCount { get; set; } = 3;
        public float DamagePerArrow { get; set; } = 0.6f; // 60% damage each
        public float SpreadAngle { get; set; } = 15f;
        
        private bool _hasUsed = false;
        
        public override string Description => 
            $"<color=#00FF88>Multishot</color>\n" +
            $"Fire {ArrowCount} arrows in a spread\n" +
            $"Each deals {DamagePerArrow * 100:F0}% damage";
        
        public MultishotEffect()
        {
            m_name = "Multishot";
            m_tooltip = "Next shot fires multiple arrows";
            Duration = DurationSeconds;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Multishot ready!");
            }
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            if (_hasUsed) return;
            
            if (skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows)
            {
                _hasUsed = true;
                
                // Modify this arrow's damage
                hitData.m_damage.Modify(DamagePerArrow);
                
                // Spawn additional projectiles would require projectile system integration
                // For now, we simulate with bonus damage for the "extra arrows"
                float bonusDamage = hitData.m_damage.GetTotalDamage() * (ArrowCount - 1);
                hitData.m_damage.m_pierce += bonusDamage;
                
                if (m_character != null)
                {
                    m_character.Message(MessageHud.MessageType.TopLeft, "Multishot!");
                    SpawnVFX("fx_Lightning", m_character.transform.position);
                }
                
                // Remove effect after use
                Duration = ExpireAfterUseDuration;
            }
        }
        
        private void SpawnVFX(string prefabName, Vector3 position)
        {
            try
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                if (prefab != null)
                {
                    Object.Instantiate(prefab, position, Quaternion.identity);
                }
            }
            catch { }
        }
    }
    
    #endregion
    
    #region Mage - Overcharge (L35)
    
    /// <summary>
    /// Overcharge Effect (Mage L35) - Trade eitr for damage.
    /// +50% eitr cost, +75% damage.
    /// </summary>
    public class OverchargeEffect : CompanionStatusEffectBase
    {
        private const float DurationSeconds = 20f;

        public float EitrCostMultiplier { get; set; } = 1.5f; // +50% cost
        public float DamageMultiplier { get; set; } = 1.75f; // +75% damage
        
        public override string Description => 
            $"<color=#FF00FF>Overcharge</color>\n" +
            $"+{(DamageMultiplier - 1f) * 100:F0}% magic damage\n" +
            $"+{(EitrCostMultiplier - 1f) * 100:F0}% eitr cost";
        
        public OverchargeEffect()
        {
            m_name = "Overcharge";
            m_tooltip = "Increased magic power at higher eitr cost";
            Duration = DurationSeconds;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character != null)
            {
                SpawnVFX("vfx_fireball_explosion", m_character.transform.position);
                m_character.Message(MessageHud.MessageType.TopLeft, "<color=#FF00FF>Overcharged!</color>");
            }
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            if (skill == Skills.SkillType.ElementalMagic || skill == Skills.SkillType.BloodMagic)
            {
                hitData.m_damage.Modify(DamageMultiplier);
            }
        }
        
        // Note: Eitr cost modification needs integration with casting system
        public float GetEitrCostMultiplier() => EitrCostMultiplier;
        
        private void SpawnVFX(string prefabName, Vector3 position)
        {
            try
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                if (prefab != null)
                {
                    Object.Instantiate(prefab, position, Quaternion.identity);
                }
            }
            catch { }
        }
    }
    
    #endregion
    
    #region Mage - Chain Casting (L50)
    
    /// <summary>
    /// Chain Casting Effect (Mage L50) - Chance for free casts.
    /// 30% chance for spells to not consume eitr.
    /// </summary>
    public class ChainCastingEffect : CompanionStatusEffectBase
    {
        public float FreecastChance { get; set; } = 0.3f; // 30% chance
        
        public override string Description => 
            $"<color=#00FFFF>Chain Casting</color>\n" +
            $"{FreecastChance * 100:F0}% chance for free spells";
        
        public ChainCastingEffect()
        {
            m_name = "Chain Casting";
            m_tooltip = "Spells have a chance to cost no eitr";
            Duration = 0f; // Permanent passive
        }
        
        /// <summary>
        /// Checks if this cast should be free.
        /// </summary>
        public bool TryFreeCast()
        {
            if (Random.value < FreecastChance)
            {
                if (m_character != null)
                {
                    m_character.Message(MessageHud.MessageType.TopLeft, "<color=#00FFFF>Chain Cast!</color>");
                }
                return true;
            }
            return false;
        }
    }
    
    #endregion
    
    #region Healer - Resurrection (L50)
    
    /// <summary>
    /// Resurrection Effect (Healer L50) - Revive defeated companion.
    /// Very long cooldown (5 minutes).
    /// This is applied to the healer as a marker that they can resurrect.
    /// </summary>
    public class ResurrectionEffect : CompanionStatusEffectBase
    {
        public string TargetCompanionId { get; set; }
        public float ReviveHealthPercent { get; set; } = 0.5f; // 50% HP
        
        private float _lastResurrectTime = -1000f;
        private const float ResurrectCooldown = 300f; // 5 minutes
        
        public override string Description
        {
            get
            {
                float cooldownRemaining = GetCooldownRemaining();
                if (cooldownRemaining > 0)
                    return $"Resurrection on cooldown ({cooldownRemaining:F0}s)";
                return "Can resurrect a defeated companion";
            }
        }
        
        public ResurrectionEffect()
        {
            m_name = "Resurrection";
            m_tooltip = "Can bring defeated companions back to life";
            Duration = 0f; // Permanent ability
        }
        
        public float GetCooldownRemaining()
        {
            return Mathf.Max(0, ResurrectCooldown - (Time.time - _lastResurrectTime));
        }
        
        public bool CanResurrect => GetCooldownRemaining() <= 0;
        
        /// <summary>
        /// Attempts to resurrect the target companion.
        /// </summary>
        public bool TryResurrect(CompanionController target)
        {
            if (!CanResurrect)
            {
                m_character?.Message(MessageHud.MessageType.TopLeft, "Resurrection on cooldown!");
                return false;
            }
            
            if (target == null || !target.isDefeated)
            {
                return false;
            }
            
            _lastResurrectTime = Time.time;
            
            // Trigger immediate respawn via CompanionRespawnManager
            // This uses the same system as glitch recovery - instant respawn near the healer
            var healerPos = m_character?.transform.position ?? target.transform.position;
            CompanionRespawnManager.RequestImmediateRespawn(target.companionId, healerPos);
            
            // VFX
            if (m_character != null)
            {
                SpawnVFX("fx_DvergerMage_Support_start", m_character.transform.position);
                SpawnVFX("vfx_spiritbolt_explosion", m_character.transform.position);
                m_character.Message(MessageHud.MessageType.Center, "<color=white>RESURRECTION!</color>");
            }
            
            Debug.Log($"[ResurrectionEffect] {m_character?.m_name} resurrected {target.companionName}!");
            
            return true;
        }
        
        private void SpawnVFX(string prefabName, Vector3 position)
        {
            try
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                if (prefab != null)
                {
                    Object.Instantiate(prefab, position, Quaternion.identity);
                }
            }
            catch { }
        }
    }
    
    #endregion
    
    #region Monk - Flurry of Blows (L35)
    
    /// <summary>
    /// Flurry of Blows Effect (Monk L35) - Rapid attacks.
    /// Grants bonus attacks for a duration.
    /// </summary>
    public class FlurryOfBlowsEffect : CompanionStatusEffectBase
    {
        private const float DurationSeconds = 5f;

        public int BonusAttacks { get; set; } = 4; // 5 total attacks
        public float DamagePerHit { get; set; } = 0.4f; // 40% damage each
        
        private int _attacksRemaining;
        
        public override string Description => 
            $"<color=#FF8800>Flurry of Blows</color>\n" +
            $"{_attacksRemaining} bonus attacks remaining\n" +
            $"Each deals {DamagePerHit * 100:F0}% damage";
        
        public FlurryOfBlowsEffect()
        {
            m_name = "Flurry of Blows";
            m_tooltip = "Unleashing a rapid series of strikes";
            Duration = DurationSeconds;
        }
        
        protected override void OnEffectApplied()
        {
            _attacksRemaining = BonusAttacks;
            
            if (m_character != null)
            {
                SpawnVFX("fx_fenring_frost", m_character.transform.position);
                m_character.Message(MessageHud.MessageType.TopLeft, "Flurry of Blows!");
            }
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            if (_attacksRemaining > 0)
            {
                // Each hit in the flurry deals reduced damage
                hitData.m_damage.Modify(DamagePerHit);
                _attacksRemaining--;
                
                // VFX per hit
                if (m_character != null)
                {
                    SpawnVFX("fx_fenring_frost", m_character.transform.position);
                }
                
                // Simulate rapid hits by adding damage for remaining attacks
                if (_attacksRemaining > 0)
                {
                    float bonusDamage = hitData.m_damage.GetTotalDamage() * _attacksRemaining;
                    hitData.m_damage.m_blunt += bonusDamage;
                    _attacksRemaining = 0;
                }
            }
        }
        
        private void SpawnVFX(string prefabName, Vector3 position)
        {
            try
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                if (prefab != null)
                {
                    Object.Instantiate(prefab, position, Quaternion.identity);
                }
            }
            catch { }
        }
    }
    
    #endregion
    
    #region Monk - Iron Body (L50)
    
    /// <summary>
    /// Iron Body Effect (Monk L50) - Damage reduction.
    /// Reduces all damage taken by 30%.
    /// </summary>
    public class IronBodyEffect : CompanionStatusEffectBase
    {
        private const float AuraVfxChance = 0.1f;
        private const float DurationSeconds = 10f;

        public float DamageReduction { get; set; } = 0.7f; // Take 70% damage (30% reduction)
        
        public override string Description => 
            $"<color=#AAAAAA>Iron Body</color>\n" +
            $"-{(1f - DamageReduction) * 100:F0}% damage taken";
        
        public IronBodyEffect()
        {
            m_name = "Iron Body";
            m_tooltip = "Body hardened against damage";
            Duration = DurationSeconds;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character != null)
            {
                SpawnVFX("fx_shaman_protect", m_character.transform.position);
                m_character.Message(MessageHud.MessageType.TopLeft, "Iron Body!");
            }
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            hit.m_damage.Modify(DamageReduction);
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            // Iron aura
            if (m_character != null && Random.value < AuraVfxChance)
            {
                SpawnVFX("fx_shaman_protect", m_character.transform.position);
            }
        }
        
        private void SpawnVFX(string prefabName, Vector3 position)
        {
            try
            {
                var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                if (prefab != null)
                {
                    Object.Instantiate(prefab, position, Quaternion.identity);
                }
            }
            catch { }
        }
    }
    
    #endregion
}
