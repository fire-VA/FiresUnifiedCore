using System.Collections.Generic;
using UnityEngine;
using FiresCore.Npc.Utilities;

namespace FiresCore.Npc.Archetypes.StatusEffects.Rogue
{
    /// <summary>
    /// Stealth effect - makes the rogue harder to detect and increases backstab damage.
    /// Used by: Rogue archetype (self buff ability)
    /// 
    /// DESIGN: The rogue enters stealth, becoming harder to detect by enemies.
    /// First attack from stealth deals massive bonus damage. Stealth breaks on attack
    /// or when taking damage.
    /// 
    /// VALHEIM-SPECIFIC: Uses CharacterVisibilityController's ghost material swap
    /// which actually works with Valheim's custom shaders (unlike standard alpha blending).
    /// </summary>
    public class StealthEffect : CompanionStatusEffectBase
    {
        /// <summary>Backstab damage multiplier while stealthed.</summary>
        public float StealthBackstabMultiplier { get; set; } = 3.0f;
        
        /// <summary>Detection range reduction (0.3 = enemies detect at 30% normal range).</summary>
        public float DetectionRangeMultiplier { get; set; } = 0.3f;
        
        /// <summary>Speed multiplier while stealthed (usually slower for sneaking).</summary>
        public float StealthSpeedMultiplier { get; set; } = 0.8f;
        
        /// <summary>Whether stealth breaks on taking damage.</summary>
        public bool BreakOnDamage { get; set; } = true;
        
        /// <summary>Whether stealth breaks on attacking.</summary>
        public bool BreakOnAttack { get; set; } = true;
        
        /// <summary>Brief immunity when entering stealth (vanish).</summary>
        public float VanishImmunityDuration { get; set; } = 0.5f;
        
        /// <summary>Whether to make the model semi-transparent during stealth.</summary>
        public bool UseTransparencyEffect { get; set; } = true;
        
        /// <summary>Transparency alpha value (0 = invisible, 1 = opaque). 0.15 = very ghostly.</summary>
        public float StealthAlpha { get; set; } = 0.15f;
        
        /// <summary>Whether the first attack from stealth has been used.</summary>
        public bool FirstAttackUsed { get; private set; } = false;
        
        /// <summary>Whether to spawn ghost particle effects during stealth.</summary>
        public bool UseParticleEffects { get; set; } = true;
        
        private Rigidbody _rb;
        private bool _isStealthActive = true;
        private CharacterVisibilityController _visibilityController;
        private bool _pendingStealthBreak = false;
        
        // Particle effect instances
        private GameObject _ghostParticles;
        private GameObject _smokeParticles;
        
        /// <summary>
        /// Description shown in tooltip with actual stat values.
        /// </summary>
        public override string Description => 
            $"{StealthBackstabMultiplier:F1}x backstab damage from stealth\n" +
            (FirstAttackUsed ? "Stealth attack used" : "Next attack empowered");
        
        public StealthEffect()
        {
            m_name = "Stealth";
            Duration = 15f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character != null)
            {
                _rb = m_character.GetComponent<Rigidbody>();
                _isStealthActive = true;
                FirstAttackUsed = false;
                
                // Create visibility controller for ghost material swap
                _visibilityController = new CharacterVisibilityController(m_character);
                
                // Apply ghost mode for actual transparency in Valheim
                if (UseTransparencyEffect)
                {
                    _visibilityController.SetGhostMode(true, StealthAlpha);
                }
                
                // VFX for vanish (one-shot poof effect)
                SpawnVFX("vfx_ghost_death", m_character.transform.position);
                
                // Start continuous ghost particle effects
                if (UseParticleEffects)
                {
                    StartGhostParticles();
                }
                
                m_character.Message(MessageHud.MessageType.TopLeft, "Vanished!");
                
                // Apply brief immunity on entry
                if (VanishImmunityDuration > 0f)
                {
                    ApplyVanishImmunity();
                }
                
                Debug.Log($"[StealthEffect] {m_character.m_name} entered stealth - ghost material + particles, alpha={StealthAlpha}, {StealthBackstabMultiplier}x backstab damage");
            }
        }
        
        /// <summary>
        /// Starts continuous ghost/wraith particle effects attached to the character.
        /// Uses simple prefab instantiation instead of manipulating particle systems directly.
        /// </summary>
        private void StartGhostParticles()
        {
            if (m_character == null) return;
            
            try
            {
                // Try to find ghost/wraith effects from Valheim prefabs
                // We'll clone the entire effect prefab and parent it to the character
                string[] particlePrefabs = new[]
                {
                    "vfx_ghost_death",     // Ghost death/spawn effect
                    "vfx_Wraith_hit",      // Wraith hit effect
                    "vfx_ice_destroyed",   // Ice particles (ghostly looking)
                };
                
                foreach (var prefabName in particlePrefabs)
                {
                    var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                    if (prefab == null) continue;
                    
                    // Clone the VFX prefab and parent to character
                    var effectGO = Object.Instantiate(prefab, m_character.transform);
                    effectGO.name = "StealthGhostParticles";
                    effectGO.transform.localPosition = Vector3.up * 1f;
                    effectGO.transform.localRotation = Quaternion.identity;
                    
                    // Keep this as our particle reference
                    _ghostParticles = effectGO;
                    
                    Debug.Log($"[StealthEffect] Started ghost particles from {prefabName}");
                    return;
                }
                
                // No particles found - that's ok, we still have the ghost material swap
                Debug.Log("[StealthEffect] No suitable ghost particles found - using material swap only");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[StealthEffect] Failed to create ghost particles: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Creates a simple fallback particle effect if we can't find ghost particles.
        /// </summary>
        private void CreateFallbackGhostParticles()
        {
            // No-op - we'll just use the material swap as the visual
        }
        
        /// <summary>
        /// Stops the ghost particle effects.
        /// </summary>
        private void StopGhostParticles()
        {
            if (_ghostParticles != null)
            {
                // Destroy after short delay to let particles fade
                Object.Destroy(_ghostParticles, 0.5f);
                _ghostParticles = null;
            }
            
            if (_smokeParticles != null)
            {
                Object.Destroy(_smokeParticles, 0.5f);
                _smokeParticles = null;
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
        
        private void ApplyVanishImmunity()
        {
            var seman = m_character.GetSEMan();
            if (seman == null) return;
            
            var immunity = ScriptableObject.CreateInstance<Common.InvulnerableEffect>();
            immunity.name = "VanishImmunity";
            immunity.Duration = VanishImmunityDuration;
            
            seman.AddStatusEffect(immunity, true);
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (m_character == null || m_character.IsDead()) return;
            
            // Handle pending stealth break (delayed to ensure backstab damage applies)
            if (_pendingStealthBreak && _isStealthActive)
            {
                _pendingStealthBreak = false;
                BreakStealth("backstab attack landed");
            }
            
            // Apply speed modifier for sneaking
            if (_isStealthActive && _rb != null && !_rb.isKinematic && StealthSpeedMultiplier != 1f)
            {
                Vector3 velocity = _rb.linearVelocity;
                float horizontalMag = new Vector3(velocity.x, 0, velocity.z).magnitude;
                
                if (horizontalMag > 0.1f)
                {
                    Vector3 horizontalDir = new Vector3(velocity.x, 0, velocity.z).normalized;
                    _rb.linearVelocity = horizontalDir * horizontalMag * StealthSpeedMultiplier + Vector3.up * velocity.y;
                }
            }
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            base.ModifyAttack(skill, ref hitData);
            
            // First attack from stealth gets massive backstab bonus
            if (_isStealthActive && !FirstAttackUsed)
            {
                FirstAttackUsed = true;
                hitData.m_backstabBonus *= StealthBackstabMultiplier;
                
                // ALWAYS log stealth attacks - this is important feedback
                if (m_character != null)
                {
                    Debug.Log($"[StealthEffect] {m_character.m_name} BACKSTAB from stealth! {StealthBackstabMultiplier}x damage bonus applied");
                }
                
                // CRITICAL: Delay stealth break slightly to ensure the damage is applied first
                if (BreakOnAttack)
                {
                    _pendingStealthBreak = true;
                }
            }
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            base.OnDamaged(hit, attacker);
            
            // Break stealth on damage if configured
            if (_isStealthActive && BreakOnDamage)
            {
                BreakStealth("took damage");
            }
        }
        
        /// <summary>
        /// Breaks stealth immediately.
        /// </summary>
        public void BreakStealth(string reason = "")
        {
            if (!_isStealthActive) return;
            
            _isStealthActive = false;
            
            // Restore visuals - this restores original materials
            _visibilityController?.Restore();
            
            // Stop ghost particles
            StopGhostParticles();
            
            // VFX for appearing
            if (m_character != null)
            {
                SpawnVFX("vfx_ghost_death", m_character.transform.position);
            }
            
            m_ttl = 0.1f; // End soon
            
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Revealed!");
                Debug.Log($"[StealthEffect] {m_character.m_name} stealth broken - {reason}");
            }
        }
        
        /// <summary>
        /// Returns whether stealth is currently active.
        /// </summary>
        public bool IsStealthActive => _isStealthActive;
        
        protected override void OnEffectRemoved()
        {
            // Restore normal visuals
            _visibilityController?.Restore();
            _visibilityController = null;
            
            // Stop ghost particles
            StopGhostParticles();
            
            // VFX for appearing
            if (m_character != null)
            {
                SpawnVFX("vfx_ghost_death", m_character.transform.position);
            }
            
            _rb = null;
            _isStealthActive = false;
            
            base.OnEffectRemoved();
        }
        
        /// <summary>
        /// Applies stealth to a character.
        /// </summary>
        public static bool ApplyStealth(Character target, float duration)
        {
            if (target == null) return false;
            
            var seman = target.GetSEMan();
            if (seman == null) return false;
            
            var effect = ScriptableObject.CreateInstance<StealthEffect>();
            effect.name = StatusEffectManager.EFFECT_STEALTH;
            effect.Duration = duration;
            effect.SourceCharacter = target;
            
            // CRITICAL: Set m_icon directly
            var icon = StatusEffectManager.GetEffectIcon(StatusEffectManager.EFFECT_STEALTH);
            effect.m_icon = icon;
            effect.EffectIcon = icon;
            
            int existingHash = StatusEffectManager.EFFECT_STEALTH.GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(effect, true);
            
            Debug.Log($"[StealthEffect] Applied to {target.m_name}, hasIcon={icon != null}");
            
            return true;
        }
        
        public new StealthEffect Clone()
        {
            var clone = (StealthEffect)base.Clone();
            if (clone != null)
            {
                clone.StealthBackstabMultiplier = StealthBackstabMultiplier;
                clone.DetectionRangeMultiplier = DetectionRangeMultiplier;
                clone.StealthSpeedMultiplier = StealthSpeedMultiplier;
                clone.BreakOnDamage = BreakOnDamage;
                clone.BreakOnAttack = BreakOnAttack;
                clone.VanishImmunityDuration = VanishImmunityDuration;
                clone.UseTransparencyEffect = UseTransparencyEffect;
                clone.StealthAlpha = StealthAlpha;
            }
            return clone;
        }
    }
}
