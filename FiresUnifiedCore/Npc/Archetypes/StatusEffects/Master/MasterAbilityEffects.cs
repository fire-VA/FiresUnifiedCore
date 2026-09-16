using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.Archetypes.StatusEffects.Master
{
    /// <summary>
    /// Master Ability Effects - Level 75 abilities for all archetypes.
    /// These are powerful abilities with long cooldowns.
    /// </summary>
    
    #region Tank - Unyielding
    
    /// <summary>
    /// Unyielding Effect (Tank L75) - Prevents fatal damage once.
    /// When the tank would die, they instead survive with 1 HP.
    /// 5 minute cooldown after triggering.
    /// </summary>
    public class UnyieldingEffect : CompanionStatusEffectBase
    {
        private const float CooldownSeconds = 300f;

        private bool _hasTriggered = false;
        private float _triggerTime = 0f;
        
        public override string Description => 
            _hasTriggered 
                ? $"Unyielding on cooldown ({GetCooldownRemaining():F0}s)"
                : "Survives fatal damage with 1 HP (once)";
        
        public UnyieldingEffect()
        {
            m_name = "Unyielding";
            m_tooltip = "Will survive the next fatal blow with 1 HP";
            Duration = 0f; // Permanent passive until triggered
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character != null && VerboseLogging)
            {
                Debug.Log($"[UnyieldingEffect] {m_character.m_name} gained Unyielding - will survive fatal damage");
            }
        }
        
        /// <summary>
        /// Intercepts fatal damage and prevents death.
        /// </summary>
        public override void OnDamaged(HitData hit, Character attacker)
        {
            if (_hasTriggered) return;
            if (m_character == null) return;
            
            // Calculate if this damage would be fatal
            float currentHealth = m_character.GetHealth();
            float incomingDamage = hit.GetTotalDamage();
            
            if (incomingDamage >= currentHealth)
            {
                // This would kill us - trigger Unyielding!
                _hasTriggered = true;
                _triggerTime = Time.time;
                
                // Reduce damage to leave us at 1 HP
                float damageToAllow = currentHealth - 1f;
                if (damageToAllow > 0)
                {
                    float reduction = damageToAllow / incomingDamage;
                    hit.m_damage.Modify(reduction);
                }
                else
                {
                    // Already at 1 HP or less, negate all damage
                    hit.m_damage.Modify(0f);
                }
                
                // Visual feedback
                m_character.Message(MessageHud.MessageType.Center, "<color=yellow>UNYIELDING!</color>");
                
                // Spawn VFX
                SpawnVFX("fx_shield_start", m_character.transform.position);
                SpawnVFX("vfx_spiritbolt_explosion", m_character.transform.position);
                
                // Grant skill XP for surviving fatal damage
                var skillSystem = m_character.GetComponent<ArchetypeSkillSystem>();
                skillSystem?.OnAbilityHitEnemy("unyielding", attacker, false);
                
                Debug.Log($"[UnyieldingEffect] {m_character.m_name} SURVIVED FATAL DAMAGE! Unyielding triggered!");
                
                // Start cooldown timer - effect will be removed and reapplied after 5 minutes
                // For now, the effect stays but won't trigger again
            }
        }
        
        private float GetCooldownRemaining()
        {
            if (!_hasTriggered) return 0f;
            float elapsed = Time.time - _triggerTime;
            return Mathf.Max(0f, CooldownSeconds - elapsed); // 5 minute cooldown
        }
        
        /// <summary>
        /// Reset the effect after cooldown.
        /// </summary>
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (_hasTriggered && GetCooldownRemaining() <= 0f)
            {
                _hasTriggered = false;
                m_character?.Message(MessageHud.MessageType.TopLeft, "Unyielding ready again!");
                Debug.Log($"[UnyieldingEffect] {m_character?.m_name} Unyielding cooldown complete - ready again");
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
    
    #region Paladin - Lay on Hands
    
    /// <summary>
    /// Lay on Hands Effect (Paladin L75) - Full heal with long cooldown.
    /// This is applied briefly to trigger the heal, then removed.
    /// </summary>
    public class LayOnHandsEffect : CompanionStatusEffectBase
    {
        public Character HealTarget { get; set; }
        
        public override string Description => "Fully heals the target";
        
        public LayOnHandsEffect()
        {
            m_name = "Lay on Hands";
            m_tooltip = "Divine healing restores all health";
            Duration = 1f; // Brief duration - just long enough to apply heal
        }
        
        protected override void OnEffectApplied()
        {
            // Determine target (self if no target specified)
            var target = HealTarget ?? m_character;
            if (target == null) return;
            
            // Full heal!
            float maxHealth = target.GetMaxHealth();
            float currentHealth = target.GetHealth();
            float healAmount = maxHealth - currentHealth;
            
            if (healAmount > 0)
            {
                target.Heal(healAmount, true);
            }
            
            // Visual feedback
            target.Message(MessageHud.MessageType.Center, "<color=green>LAY ON HANDS!</color>");
            m_character?.Message(MessageHud.MessageType.TopLeft, $"Fully healed {target.m_name}!");
            
            // VFX
            SpawnVFX("fx_DvergerMage_Support_start", target.transform.position);
            SpawnVFX("fx_creature_tamed", target.transform.position);
            SpawnVFX("vfx_spiritbolt_explosion", target.transform.position);
            
            Debug.Log($"[LayOnHandsEffect] {m_character?.m_name} used Lay on Hands on {target.m_name} - healed {healAmount:F0} HP");
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
    
    #region Berserker - Death Wish
    
    /// <summary>
    /// Death Wish Effect (Berserker L75) - Massive damage boost when near death.
    /// +100% damage when below 10% HP, but cannot be healed.
    /// </summary>
    public class DeathWishEffect : CompanionStatusEffectBase
    {
        private const float HealthThreshold = 0.10f; // 10% HP
        private const float DamageBonus = 2.0f; // +100% damage
        
        private bool _isActive = false;
        
        public override string Description => 
            _isActive 
                ? "<color=red>DEATH WISH ACTIVE!</color>\n+100% damage, cannot heal"
                : $"Activates below {HealthThreshold * 100:F0}% HP";
        
        public DeathWishEffect()
        {
            m_name = "Death Wish";
            m_tooltip = "Deals double damage when near death, but cannot be healed";
            Duration = 0f; // Permanent passive
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (m_character == null) return;
            
            float healthPercent = m_character.GetHealthPercentage();
            bool shouldBeActive = healthPercent <= HealthThreshold;
            
            if (shouldBeActive && !_isActive)
            {
                // Activate Death Wish!
                _isActive = true;
                m_character.Message(MessageHud.MessageType.Center, "<color=red>DEATH WISH!</color>");
                SpawnVFX("vfx_MeadBzerker", m_character.transform.position);
                Debug.Log($"[DeathWishEffect] {m_character.m_name} DEATH WISH activated at {healthPercent * 100:F0}% HP!");
            }
            else if (!shouldBeActive && _isActive)
            {
                // Deactivate (healed above threshold somehow)
                _isActive = false;
                m_character.Message(MessageHud.MessageType.TopLeft, "Death Wish deactivated");
                Debug.Log($"[DeathWishEffect] {m_character.m_name} Death Wish deactivated");
            }
        }
        
        /// <summary>
        /// Modifies outgoing damage when Death Wish is active.
        /// </summary>
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            if (_isActive)
            {
                hitData.m_damage.Modify(DamageBonus);
                
                if (VerboseLogging)
                {
                    Debug.Log($"[DeathWishEffect] {m_character?.m_name} Death Wish doubled attack damage!");
                }
            }
        }
        
        /// <summary>
        /// Blocks healing when Death Wish is active.
        /// Note: This requires a patch to Character.Heal() to check for this effect.
        /// For now, we'll track it and log it.
        /// </summary>
        public bool BlocksHealing => _isActive;
        
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
    
    #region Rogue - Death Mark
    
    /// <summary>
    /// Death Mark Effect (Rogue L75) - Marks enemy for bonus damage from all sources.
    /// Applied to ENEMY targets, increases all damage they take by 30%.
    /// </summary>
    public class DeathMarkEffect : CompanionStatusEffectBase
    {
        public const float DAMAGE_INCREASE = 1.30f; // +30% damage taken
        
        public override string Description => 
            $"Marked for death - takes +{(DAMAGE_INCREASE - 1f) * 100:F0}% damage from all sources";
        
        public DeathMarkEffect()
        {
            m_name = "Death Mark";
            m_tooltip = "Marked for death - takes increased damage";
            Duration = 20f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character != null)
            {
                // VFX on the marked target
                SpawnVFX("vfx_ghost_death", m_character.transform.position);
                
                if (VerboseLogging)
                {
                    Debug.Log($"[DeathMarkEffect] {m_character.m_name} marked for death - +{(DAMAGE_INCREASE - 1f) * 100:F0}% damage taken");
                }
            }
        }
        
        /// <summary>
        /// Increases damage taken by the marked target.
        /// </summary>
        public override void OnDamaged(HitData hit, Character attacker)
        {
            // Increase damage taken
            hit.m_damage.Modify(DAMAGE_INCREASE);
            
            if (VerboseLogging && m_character != null)
            {
                Debug.Log($"[DeathMarkEffect] {m_character.m_name} (marked) took {(DAMAGE_INCREASE - 1f) * 100:F0}% extra damage");
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null && VerboseLogging)
            {
                Debug.Log($"[DeathMarkEffect] Death Mark expired on {m_character.m_name}");
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
    
    #region Ranger - Rain of Arrows
    
    /// <summary>
    /// Rain of Arrows Effect (Ranger L75) - Creates an AoE damage zone.
    /// Deals damage over time to enemies in the area.
    /// </summary>
    public class RainOfArrowsEffect : CompanionStatusEffectBase
    {
        private const float DefaultTargetDistance = 8f;
        private const float InitialVfxHeight = 10f;
        private const int ArrowImpactVfxPerTick = 3;

        public Vector3 TargetPosition { get; set; }
        public float Radius { get; set; } = 6f;
        public float DamagePerTick { get; set; } = 15f;
        public float TickInterval { get; set; } = 0.5f;
        
        private float _lastTickTime;
        private List<GameObject> _arrowVFX = new List<GameObject>();
        
        public override string Description => 
            $"Arrows rain on area for {Duration:F0}s\n" +
            $"{DamagePerTick:F0} damage every {TickInterval:F1}s";
        
        public RainOfArrowsEffect()
        {
            m_name = "Rain of Arrows";
            m_tooltip = "Arrows rain down from the sky";
            Duration = 5f;
        }
        
        protected override void OnEffectApplied()
        {
            _lastTickTime = Time.time;
            
            // Use caster position if no target specified
            if (TargetPosition == Vector3.zero && m_character != null)
            {
                TargetPosition = m_character.transform.position + m_character.transform.forward * DefaultTargetDistance;
            }
            
            // Initial VFX
            SpawnVFX("fx_Lightning", TargetPosition + Vector3.up * InitialVfxHeight);
            
            if (VerboseLogging)
            {
                Debug.Log($"[RainOfArrowsEffect] Started at {TargetPosition}, radius {Radius}m, {Duration}s duration");
            }
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            // Check if it's time for another tick
            if (Time.time - _lastTickTime >= TickInterval)
            {
                _lastTickTime = Time.time;
                DealDamageInArea();
                SpawnArrowVFX();
            }
        }
        
        private void DealDamageInArea()
        {
            if (m_character == null) return;
            
            // Get skill system for XP tracking
            var skillSystem = m_character.GetComponent<ArchetypeSkillSystem>();
            
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(TargetPosition, character.transform.position);
                if (dist <= Radius)
                {
                    // Deal pierce damage
                    var hitData = new HitData();
                    hitData.m_point = character.transform.position;
                    hitData.m_dir = Vector3.down;
                    hitData.m_attacker = m_character.GetZDOID();
                    hitData.m_damage.m_pierce = DamagePerTick;
                    
                    // Check if this will kill
                    bool willKill = character.GetHealth() <= hitData.GetTotalDamage();
                    
                    character.Damage(hitData);
                    
                    // Grant skill XP for hitting
                    skillSystem?.OnAbilityHitEnemy("rainofarrows", character, willKill || character.IsDead());
                    
                    // Arrow hit VFX
                    SpawnVFX("fx_arrow_hit", character.transform.position);
                }
            }
        }
        
        private void SpawnArrowVFX()
        {
            // Spawn multiple arrow impact effects in the area
            for (int i = 0; i < ArrowImpactVfxPerTick; i++)
            {
                Vector3 randomOffset = new Vector3(
                    Random.Range(-Radius, Radius),
                    0,
                    Random.Range(-Radius, Radius)
                );
                Vector3 impactPos = TargetPosition + randomOffset;
                
                SpawnVFX("fx_arrow_hit", impactPos);
            }
        }
        
        protected override void OnEffectRemoved()
        {
            // Cleanup
            foreach (var vfx in _arrowVFX)
            {
                if (vfx != null) Object.Destroy(vfx);
            }
            _arrowVFX.Clear();
            
            if (VerboseLogging)
            {
                Debug.Log($"[RainOfArrowsEffect] Ended");
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
    
    #region Mage - Meteor
    
    /// <summary>
    /// Meteor Effect (Mage L75) - Calls down a massive AoE attack.
    /// High damage, long cooldown, impressive VFX.
    /// </summary>
    public class MeteorEffect : CompanionStatusEffectBase
    {
        private const float DefaultTargetDistance = 10f;
        private const float BluntDamageFraction = 0.3f;

        public Vector3 TargetPosition { get; set; }
        public float Radius { get; set; } = 8f;
        public float Damage { get; set; } = 200f;
        public float ImpactDelay { get; set; } = 1.5f;
        
        private float _startTime;
        private bool _hasImpacted = false;
        
        public override string Description => 
            $"Meteor incoming!\n{Damage:F0} fire damage in {Radius:F0}m radius";
        
        public MeteorEffect()
        {
            m_name = "Meteor";
            m_tooltip = "A massive meteor is falling!";
            Duration = 3f; // Time for animation + lingering fire
        }
        
        protected override void OnEffectApplied()
        {
            _startTime = Time.time;
            _hasImpacted = false;
            
            // Use forward position if no target
            if (TargetPosition == Vector3.zero && m_character != null)
            {
                TargetPosition = m_character.transform.position + m_character.transform.forward * DefaultTargetDistance;
            }
            
            // Warning VFX at target location
            SpawnVFX("vfx_spray_fire", TargetPosition);
            
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.Center, "<color=orange>METEOR!</color>");
            }
            
            Debug.Log($"[MeteorEffect] Meteor called at {TargetPosition}, impact in {ImpactDelay}s");
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (!_hasImpacted && Time.time - _startTime >= ImpactDelay)
            {
                _hasImpacted = true;
                DoMeteorImpact();
            }
        }
        
        private void DoMeteorImpact()
        {
            // Massive VFX
            SpawnVFX("vfx_fireball_explosion", TargetPosition);
            SpawnVFX("vfx_spray_fire", TargetPosition);
            SpawnVFX("vfx_sledge_hit", TargetPosition);
            
            // Try to spawn actual fire AOE
            SpawnFireAOE(TargetPosition);
            
            // Get skill system for XP tracking
            var skillSystem = m_character?.GetComponent<ArchetypeSkillSystem>();
            
            // Deal damage to all enemies in radius
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (m_character != null && character == m_character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(TargetPosition, character.transform.position);
                if (dist <= Radius)
                {
                    // Damage falls off with distance
                    float falloff = 1f - (dist / Radius) * 0.5f;
                    float actualDamage = Damage * falloff;
                    
                    var hitData = new HitData();
                    hitData.m_point = character.transform.position;
                    hitData.m_dir = (character.transform.position - TargetPosition).normalized;
                    hitData.m_attacker = m_character?.GetZDOID() ?? ZDOID.None;
                    hitData.m_damage.m_fire = actualDamage;
                    hitData.m_damage.m_blunt = actualDamage * BluntDamageFraction; // Some impact damage
                    
                    // Check if this will kill the target
                    bool willKill = character.GetHealth() <= hitData.GetTotalDamage();
                    
                    character.Damage(hitData);
                    
                    // Stagger enemies
                    character.Stagger(hitData.m_dir);
                    
                    // Grant skill XP for hitting (and possibly killing)
                    skillSystem?.OnAbilityHitEnemy("meteor", character, willKill || character.IsDead());
                    
                    Debug.Log($"[MeteorEffect] Hit {character.m_name} for {actualDamage:F0} fire damage");
                }
            }
            
            Debug.Log($"[MeteorEffect] IMPACT! {Damage:F0} fire damage in {Radius}m radius");
        }
        
        private void SpawnFireAOE(Vector3 position)
        {
            try
            {
                // Try to use game's fire AOE
                string[] fireAoePrefabs = { "Fader_fire_aoe", "vfx_fire_aoe", "fx_Fader_fire" };
                foreach (var prefabName in fireAoePrefabs)
                {
                    var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                    if (prefab != null)
                    {
                        Object.Instantiate(prefab, position, Quaternion.identity);
                        return;
                    }
                }
            }
            catch { }
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
    
    #region Healer - Divine Hymn
    
    /// <summary>
    /// Divine Hymn Effect (Healer L75) - Channel powerful group heal.
    /// Heals all allies in range rapidly while channeling.
    /// </summary>
    public class DivineHymnEffect : CompanionStatusEffectBase
    {
        public float HealPerTick { get; set; } = 20f;
        public float Radius { get; set; } = 15f;
        public float TickInterval { get; set; } = 0.5f;
        
        private float _lastTickTime;
        private CompanionController _companionController;
        
        public override string Description => 
            $"Channeling Divine Hymn\n" +
            $"Heals {HealPerTick:F0} HP every {TickInterval:F1}s to allies in {Radius:F0}m";
        
        public DivineHymnEffect()
        {
            m_name = "Divine Hymn";
            m_tooltip = "Channeling a powerful healing song";
            Duration = 6f;
        }
        
        protected override void OnEffectApplied()
        {
            _lastTickTime = Time.time;
            _companionController = m_character?.GetComponent<CompanionController>();
            
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.Center, "<color=cyan>? Divine Hymn ?</color>");
                SpawnVFX("fx_DvergerMage_Support_start", m_character.transform.position);
            }
            
            Debug.Log($"[DivineHymnEffect] Started channeling - {HealPerTick} HP/tick, {Duration}s duration");
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (Time.time - _lastTickTime >= TickInterval)
            {
                _lastTickTime = Time.time;
                HealAllies();
            }
        }
        
        private void HealAllies()
        {
            if (m_character == null) return;
            
            Vector3 pos = m_character.transform.position;
            int healed = 0;
            
            // Get skill system for XP tracking
            var skillSystem = m_character.GetComponent<ArchetypeSkillSystem>();
            
            // Heal owner
            if (_companionController != null)
            {
                var owner = _companionController.GetOwner();
                if (owner != null && !owner.IsDead())
                {
                    float dist = Vector3.Distance(pos, owner.transform.position);
                    if (dist <= Radius && owner.GetHealthPercentage() < 1f)
                    {
                        owner.Heal(HealPerTick, true);
                        SpawnVFX("fx_creature_tamed", owner.transform.position);
                        
                        // Grant skill XP for healing ally
                        skillSystem?.OnAbilityBuffedAlly("divinehymn", owner);
                        healed++;
                    }
                }
            }
            
            // Heal all allies in range
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character.GetHealthPercentage() >= 1f) continue; // Already full
                if (BaseAI.IsEnemy(m_character, character)) continue;
                
                float dist = Vector3.Distance(pos, character.transform.position);
                if (dist <= Radius)
                {
                    character.Heal(HealPerTick, true);
                    SpawnVFX("fx_creature_tamed", character.transform.position);
                    
                    // Grant skill XP for healing ally
                    skillSystem?.OnAbilityBuffedAlly("divinehymn", character);
                    healed++;
                }
            }
            
            // Ongoing VFX on caster
            SpawnVFX("fx_DvergerMage_Support_start", pos);
            
            if (healed > 0 && VerboseLogging)
            {
                Debug.Log($"[DivineHymnEffect] Healed {healed} allies for {HealPerTick} HP");
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Divine Hymn ended");
            }
            Debug.Log($"[DivineHymnEffect] Channeling complete");
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
    
    #region Monk - Chi Explosion
    
    /// <summary>
    /// Chi Explosion Effect (Monk L75) - Releases all chi in a devastating AoE.
    /// Instant burst damage around the monk.
    /// </summary>
    public class ChiExplosionEffect : CompanionStatusEffectBase
    {
        private const float MaxDistanceFalloff = 0.3f;

        public float Damage { get; set; } = 150f;
        public float Radius { get; set; } = 6f;
        
        public override string Description => 
            $"Chi explodes outward!\n{Damage:F0} damage in {Radius:F0}m";
        
        public ChiExplosionEffect()
        {
            m_name = "Chi Explosion";
            m_tooltip = "Releasing accumulated chi energy";
            Duration = 0.5f; // Brief - just for the explosion
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            Vector3 pos = m_character.transform.position;
            
            // Dramatic VFX
            SpawnVFX("fx_fenring_frost", pos);
            SpawnVFX("vfx_Cold", pos);
            SpawnVFX("vfx_sledge_hit", pos);
            
            m_character.Message(MessageHud.MessageType.Center, "<color=cyan>CHI EXPLOSION!</color>");
            
            // Get skill system for XP tracking
            var skillSystem = m_character.GetComponent<ArchetypeSkillSystem>();
            
            // Deal damage to all enemies in radius
            int hitCount = 0;
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(pos, character.transform.position);
                if (dist <= Radius)
                {
                    // Damage falls off slightly with distance
                    float falloff = 1f - (dist / Radius) * MaxDistanceFalloff;
                    float actualDamage = Damage * falloff;
                    
                    var hitData = new HitData();
                    hitData.m_point = character.transform.position;
                    hitData.m_dir = (character.transform.position - pos).normalized;
                    hitData.m_attacker = m_character.GetZDOID();
                    hitData.m_damage.m_blunt = actualDamage * 0.5f;
                    hitData.m_damage.m_spirit = actualDamage * 0.5f;
                    
                    // Check if this will kill
                    bool willKill = character.GetHealth() <= hitData.GetTotalDamage();
                    
                    character.Damage(hitData);
                    character.Stagger(hitData.m_dir);
                    
                    // Grant skill XP for hitting
                    skillSystem?.OnAbilityHitEnemy("chiexplosion", character, willKill || character.IsDead());
                    
                    hitCount++;
                }
            }
            
            Debug.Log($"[ChiExplosionEffect] Hit {hitCount} enemies for up to {Damage:F0} damage");
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
