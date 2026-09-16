using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.Archetypes.StatusEffects.Ultimate
{
    /// <summary>
    /// Ultimate Ability Effects - Level 100 capstone abilities for all archetypes.
    /// These are the most powerful abilities, representing mastery of the archetype.
    /// </summary>
    
    #region Tank - Immortal Stance
    
    /// <summary>
    /// Immortal Stance Effect (Tank L100) - Complete immunity + mass taunt.
    /// 5 seconds of invulnerability while forcing all enemies to attack you.
    /// </summary>
    public class ImmortalStanceEffect : CompanionStatusEffectBase
    {
        public float TauntRadius { get; set; } = 10f;
        
        public override string Description => 
            $"<color=gold>IMMORTAL STANCE</color>\n" +
            $"Immune to all damage\n" +
            $"All enemies taunted";
        
        public ImmortalStanceEffect()
        {
            m_name = "Immortal Stance";
            m_tooltip = "Completely immune to damage while all enemies are drawn to attack";
            Duration = 5f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            // Massive VFX - use AbilityFXManager for consistent scaling
            // The shaman protect bubble is the main visual, scaled appropriately for the companion
            AbilityFXManager.SpawnEffect("fx_shaman_protect", m_character.transform.position, null, 1.2f);
            AbilityFXManager.SpawnEffect("vfx_spiritbolt_explosion", m_character.transform.position, null, 1.0f);
            AbilityFXManager.SpawnEffect("fx_shield_start", m_character.transform.position, null, 1.0f);
            
            m_character.Message(MessageHud.MessageType.Center, "<color=gold>? IMMORTAL STANCE ?</color>");
            
            // Taunt all enemies in range
            TauntAllEnemies();
            
            Debug.Log($"[ImmortalStanceEffect] {m_character.m_name} activated IMMORTAL STANCE - {Duration}s immunity!");
        }
        
        private void TauntAllEnemies()
        {
            Vector3 pos = m_character.transform.position;
            int taunted = 0;
            
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(pos, character.transform.position);
                if (dist <= TauntRadius)
                {
                    // Use the same taunt system as Tank's regular taunt
                    // This damages the enemy slightly to trigger aggro via OnDamaged
                    var hitData = new HitData();
                    hitData.m_point = character.transform.position;
                    hitData.m_dir = (character.transform.position - pos).normalized;
                    hitData.m_attacker = m_character.GetZDOID();
                    hitData.m_damage.m_blunt = 0.1f; // Minimal damage just to trigger aggro
                    hitData.m_pushForce = 0f;
                    hitData.m_backstabBonus = 1f;
                    
                    character.Damage(hitData);
                    taunted++;
                }
            }
            
            Debug.Log($"[ImmortalStanceEffect] Taunted {taunted} enemies");
        }
        
        /// <summary>
        /// Makes the character immune to all damage.
        /// </summary>
        public override void ModifyDamageMods(ref HitData.DamageModifiers mods)
        {
            // COMPLETE IMMUNITY
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
            
            // Continuous aura VFX - use the same scaled shaman protect bubble
            // This prevents the jarring visual switch between big shaman shield and tiny shield_start
            if (m_character != null && Random.value < 0.15f) // Reduced frequency since effect is larger
            {
                // Use AbilityFXManager for consistent scaling with the initial effect
                AbilityFXManager.SpawnEffect("fx_shaman_protect", m_character.transform.position, null, 1.0f);
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Immortal Stance ended");
                SpawnVFX("vfx_spiritbolt_explosion", m_character.transform.position);
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
    
    #region Paladin - Avatar of Light
    
    /// <summary>
    /// Avatar of Light Effect (Paladin L100) - Divine transformation.
    /// All abilities enhanced, healing aura, damage aura to enemies.
    /// </summary>
    public class AvatarOfLightEffect : CompanionStatusEffectBase
    {
        public float DamageBonus { get; set; } = 1.5f; // +50% damage
        public float HealingBonus { get; set; } = 2.0f; // Double healing
        public float DamageReduction { get; set; } = 0.5f; // 50% less damage taken
        public float AuraRadius { get; set; } = 8f;
        public float AuraHealPerSecond { get; set; } = 5f;
        public float AuraDamagePerSecond { get; set; } = 10f;
        
        private float _lastAuraTick;
        
        public override string Description => 
            $"<color=white>? AVATAR OF LIGHT ?</color>\n" +
            $"+{(DamageBonus - 1f) * 100:F0}% damage\n" +
            $"2x healing power\n" +
            $"-{(1f - DamageReduction) * 100:F0}% damage taken\n" +
            $"Healing/damage aura";
        
        public AvatarOfLightEffect()
        {
            m_name = "Avatar of Light";
            m_tooltip = "Transformed into a divine avatar";
            Duration = 30f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            _lastAuraTick = Time.time;
            
            // Transformation VFX
            SpawnVFX("vfx_spiritbolt_explosion", m_character.transform.position);
            SpawnVFX("fx_shield_start", m_character.transform.position);
            SpawnVFX("fx_DvergerMage_Support_start", m_character.transform.position);
            
            m_character.Message(MessageHud.MessageType.Center, "<color=white>? AVATAR OF LIGHT ?</color>");
            
            Debug.Log($"[AvatarOfLightEffect] {m_character.m_name} transformed into AVATAR OF LIGHT!");
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            hitData.m_damage.Modify(DamageBonus);
            // Add spirit damage
            float totalDamage = hitData.m_damage.GetTotalDamage();
            hitData.m_damage.m_spirit += totalDamage * 0.2f;
        }
        
        public override void OnDamaged(HitData hit, Character attacker)
        {
            hit.m_damage.Modify(DamageReduction);
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (m_character == null) return;
            
            // Aura tick every 1 second
            if (Time.time - _lastAuraTick >= 1f)
            {
                _lastAuraTick = Time.time;
                ApplyAuraEffects();
            }
            
            // Continuous holy VFX
            if (Random.value < 0.2f)
            {
                SpawnVFX("vfx_spiritbolt_explosion", m_character.transform.position + Vector3.up);
            }
        }
        
        private void ApplyAuraEffects()
        {
            Vector3 pos = m_character.transform.position;
            
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                
                float dist = Vector3.Distance(pos, character.transform.position);
                if (dist > AuraRadius) continue;
                
                if (character.IsTamed() || character.IsPlayer())
                {
                    // Heal allies
                    if (character.GetHealthPercentage() < 1f)
                    {
                        character.Heal(AuraHealPerSecond, true);
                    }
                }
                else
                {
                    // Damage enemies (spirit damage)
                    var hitData = new HitData();
                    hitData.m_point = character.transform.position;
                    hitData.m_dir = (character.transform.position - pos).normalized;
                    hitData.m_attacker = m_character.GetZDOID();
                    hitData.m_damage.m_spirit = AuraDamagePerSecond;
                    
                    character.Damage(hitData);
                }
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Avatar of Light fades...");
                SpawnVFX("vfx_spiritbolt_explosion", m_character.transform.position);
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
    
    #region Berserker - Avatar of War
    
    /// <summary>
    /// Avatar of War Effect (Berserker L100) - Unstoppable warrior.
    /// Immune to stagger/knockback, massive damage increase.
    /// </summary>
    public class AvatarOfWarEffect : CompanionStatusEffectBase
    {
        public float DamageBonus { get; set; } = 1.75f; // +75% damage
        public float AttackSpeedBonus { get; set; } = 1.3f; // +30% attack speed
        
        public override string Description => 
            $"<color=red>? AVATAR OF WAR ?</color>\n" +
            $"+{(DamageBonus - 1f) * 100:F0}% damage\n" +
            $"+{(AttackSpeedBonus - 1f) * 100:F0}% attack speed\n" +
            $"Immune to stagger";
        
        public AvatarOfWarEffect()
        {
            m_name = "Avatar of War";
            m_tooltip = "An unstoppable force of destruction";
            Duration = 20f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            // Make immune to stagger
            m_character.m_staggerWhenBlocked = false;
            
            // Transformation VFX
            SpawnVFX("vfx_MeadBzerker", m_character.transform.position);
            SpawnVFX("vfx_spray_fire", m_character.transform.position);
            SpawnVFX("vfx_sledge_hit", m_character.transform.position);
            
            m_character.Message(MessageHud.MessageType.Center, "<color=red>? AVATAR OF WAR ?</color>");
            
            Debug.Log($"[AvatarOfWarEffect] {m_character.m_name} transformed into AVATAR OF WAR!");
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            hitData.m_damage.Modify(DamageBonus);
        }
        
        // Note: Attack speed bonus would need to be applied via animation speed modification
        // For now, we'll document this as needing additional integration
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (m_character == null) return;
            
            // Ensure stagger immunity persists
            m_character.m_staggerWhenBlocked = false;
            
            // War aura VFX
            if (Random.value < 0.15f)
            {
                SpawnVFX("vfx_MeadBzerker", m_character.transform.position);
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null)
            {
                // Restore stagger behavior
                m_character.m_staggerWhenBlocked = true;
                
                m_character.Message(MessageHud.MessageType.TopLeft, "Avatar of War fades...");
                SpawnVFX("vfx_MeadBzerker", m_character.transform.position);
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
    
    #region Rogue - Shadow Dance
    
    /// <summary>
    /// Shadow Dance Effect (Rogue L100) - Perfect evasion + double damage.
    /// Dodge all attacks while dealing devastating damage.
    /// 
    /// IMPROVED IMPLEMENTATION:
    /// - Actually teleports the rogue around during combat
    /// - Hides the model briefly during shadow steps
    /// - Creates afterimages at previous positions
    /// - Uses proper physics-based repositioning
    /// </summary>
    public class ShadowDanceEffect : CompanionStatusEffectBase
    {
        public float DamageBonus { get; set; } = 2.0f; // Double damage
        public float TeleportInterval { get; set; } = 2.0f; // Teleport every 2 seconds
        public float TeleportDistance { get; set; } = 3.0f; // Distance to teleport
        public float HideModelDuration { get; set; } = 0.15f; // Brief invisibility during teleport
        
        private float _lastTeleportTime;
        private float _modelHiddenUntil;
        private SkinnedMeshRenderer[] _renderers;
        private bool _renderersHidden;
        private Vector3 _lastAfterimagePos;
        
        public override string Description => 
            $"<color=purple>? SHADOW DANCE ?</color>\n" +
            $"Evade all attacks\n" +
            $"+{(DamageBonus - 1f) * 100:F0}% damage\n" +
            $"Shadow stepping through enemies";
        
        public ShadowDanceEffect()
        {
            m_name = "Shadow Dance";
            m_tooltip = "Dancing through shadows, untouchable";
            Duration = 10f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            // Cache renderers for hiding
            _renderers = m_character.GetComponentsInChildren<SkinnedMeshRenderer>();
            _lastTeleportTime = Time.time - TeleportInterval + 0.5f; // First teleport soon
            _lastAfterimagePos = m_character.transform.position;
            
            // Shadow VFX
            SpawnVFX("vfx_ghost_death", m_character.transform.position);
            SpawnVFX("fx_backstab", m_character.transform.position);
            
            m_character.Message(MessageHud.MessageType.Center, "<color=purple>? SHADOW DANCE ?</color>");
            
            Debug.Log($"[ShadowDanceEffect] {m_character.m_name} entered SHADOW DANCE!");
        }
        
        /// <summary>
        /// Evade all incoming attacks and counter-teleport.
        /// </summary>
        public override void OnDamaged(HitData hit, Character attacker)
        {
            // Negate ALL damage
            hit.m_damage.Modify(0f);
            
            // Dodge VFX at current position
            if (m_character != null)
            {
                SpawnVFX("vfx_ghost_death", m_character.transform.position);
                
                // Counter-teleport on hit (bonus dodge movement)
                if (attacker != null && Time.time - _lastTeleportTime > 0.5f)
                {
                    PerformShadowStep(attacker);
                }
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[ShadowDanceEffect] {m_character?.m_name} evaded attack from {attacker?.m_name}");
            }
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            hitData.m_damage.Modify(DamageBonus);
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            if (m_character == null) return;
            
            // Handle model visibility
            UpdateModelVisibility();
            
            // Periodic shadow teleports during dance
            if (Time.time - _lastTeleportTime >= TeleportInterval)
            {
                // Find a nearby enemy to dance around
                Character nearestEnemy = FindNearestEnemy();
                if (nearestEnemy != null)
                {
                    PerformShadowStep(nearestEnemy);
                }
                else
                {
                    // No enemy, just create afterimage effect
                    CreateAfterimage();
                }
            }
            
            // Continuous shadow VFX
            if (Random.value < 0.15f && !_renderersHidden)
            {
                Vector3 offset = Random.insideUnitSphere * 0.5f;
                offset.y = 0;
                SpawnVFX("vfx_ghost_death", m_character.transform.position + offset);
            }
        }
        
        /// <summary>
        /// Performs a shadow step - teleports to a flanking position around the target.
        /// </summary>
        private void PerformShadowStep(Character target)
        {
            if (m_character == null || target == null) return;
            
            _lastTeleportTime = Time.time;
            
            Vector3 currentPos = m_character.transform.position;
            Vector3 targetPos = target.transform.position;
            
            // Create afterimage at current position
            CreateAfterimage();
            
            // Hide model briefly
            HideModel();
            
            // Calculate flanking position (perpendicular to target)
            Vector3 toTarget = (targetPos - currentPos).normalized;
            Vector3 flankDir = Vector3.Cross(toTarget, Vector3.up);
            
            // Randomly pick left or right flank
            if (Random.value > 0.5f) flankDir = -flankDir;
            
            // Calculate destination - behind or beside the target
            Vector3 destination;
            if (Random.value > 0.3f)
            {
                // Flank position
                destination = targetPos + flankDir * TeleportDistance;
            }
            else
            {
                // Behind position (for backstab)
                Vector3 targetForward = target.transform.forward;
                destination = targetPos - targetForward * TeleportDistance;
            }
            
            // Validate and adjust destination to ground
            destination = ValidatePosition(destination, currentPos);
            
            // Perform the teleport
            TeleportTo(destination);
            
            // VFX at arrival
            SpawnVFX("vfx_ghost_death", destination);
            SpawnVFX("fx_backstab", destination);
            
            // Face the target
            Vector3 lookDir = (targetPos - destination).normalized;
            lookDir.y = 0;
            if (lookDir.magnitude > 0.1f)
            {
                m_character.transform.rotation = Quaternion.LookRotation(lookDir);
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[ShadowDanceEffect] Shadow stepped from {currentPos} to {destination}");
            }
        }
        
        /// <summary>
        /// Creates a shadow afterimage at the current position.
        /// </summary>
        private void CreateAfterimage()
        {
            if (m_character == null) return;
            
            Vector3 pos = m_character.transform.position;
            
            // Only create afterimage if moved enough
            if (Vector3.Distance(pos, _lastAfterimagePos) > 1f)
            {
                SpawnVFX("vfx_ghost_death", pos);
                SpawnVFX("fx_backstab", pos + Vector3.up * 0.5f);
                _lastAfterimagePos = pos;
            }
        }
        
        /// <summary>
        /// Hides the character model briefly for the teleport effect.
        /// </summary>
        private void HideModel()
        {
            if (_renderers == null) return;
            
            _modelHiddenUntil = Time.time + HideModelDuration;
            _renderersHidden = true;
            
            foreach (var renderer in _renderers)
            {
                if (renderer != null)
                {
                    renderer.enabled = false;
                }
            }
        }
        
        /// <summary>
        /// Shows the character model again.
        /// </summary>
        private void ShowModel()
        {
            if (_renderers == null) return;
            
            _renderersHidden = false;
            
            foreach (var renderer in _renderers)
            {
                if (renderer != null)
                {
                    renderer.enabled = true;
                }
            }
        }
        
        /// <summary>
        /// Updates model visibility state.
        /// </summary>
        private void UpdateModelVisibility()
        {
            if (_renderersHidden && Time.time >= _modelHiddenUntil)
            {
                ShowModel();
            }
        }
        
        /// <summary>
        /// Teleports the character to a destination using proper movement.
        /// </summary>
        private void TeleportTo(Vector3 destination)
        {
            if (m_character == null) return;
            
            // Use transform position directly - this works for NPCs
            // The physics will settle them naturally
            m_character.transform.position = destination;
            
            // Reset velocity to prevent weird movement
            var body = m_character.GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
            }
        }
        
        /// <summary>
        /// Validates a position and adjusts to ground level.
        /// </summary>
        private Vector3 ValidatePosition(Vector3 targetPos, Vector3 fallbackPos)
        {
            // Raycast down to find ground
            if (Physics.Raycast(targetPos + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 10f,
                LayerMask.GetMask("Default", "terrain", "static_solid")))
            {
                return hit.point + Vector3.up * 0.1f;
            }
            
            // Fallback to current Y
            return new Vector3(targetPos.x, fallbackPos.y, targetPos.z);
        }
        
        /// <summary>
        /// Finds the nearest enemy.
        /// </summary>
        private Character FindNearestEnemy()
        {
            if (m_character == null) return null;
            
            Character nearest = null;
            float nearestDist = float.MaxValue;
            
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                if (!BaseAI.IsEnemy(m_character, character)) continue;
                
                float dist = Vector3.Distance(m_character.transform.position, character.transform.position);
                if (dist < nearestDist && dist < 15f)
                {
                    nearestDist = dist;
                    nearest = character;
                }
            }
            
            return nearest;
        }
        
        protected override void OnEffectRemoved()
        {
            // Ensure model is visible
            ShowModel();
            
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Shadow Dance ends...");
                SpawnVFX("vfx_ghost_death", m_character.transform.position);
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
    
    #region Ranger - Perfect Shot
    
    /// <summary>
    /// Perfect Shot Effect (Ranger L100) - Guaranteed devastating crit.
    /// Next shot deals triple damage and always crits.
    /// </summary>
    public class PerfectShotEffect : CompanionStatusEffectBase
    {
        public float DamageMultiplier { get; set; } = 3.0f; // Triple damage
        private bool _hasUsedShot = false;
        
        public override string Description => 
            _hasUsedShot 
                ? "Perfect Shot used!"
                : $"<color=yellow>? PERFECT SHOT ?</color>\n" +
                  $"Next shot: {DamageMultiplier * 100:F0}% damage\n" +
                  $"Guaranteed critical hit";
        
        public PerfectShotEffect()
        {
            m_name = "Perfect Shot";
            m_tooltip = "The next shot will be perfect";
            Duration = 30f; // Long duration to wait for the shot
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            _hasUsedShot = false;
            
            // Focus VFX
            SpawnVFX("fx_Lightning", m_character.transform.position);
            
            m_character.Message(MessageHud.MessageType.Center, "<color=yellow>? PERFECT SHOT READY ?</color>");
            
            Debug.Log($"[PerfectShotEffect] {m_character.m_name} preparing PERFECT SHOT!");
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            if (_hasUsedShot) return;
            
            // Only apply to ranged attacks
            if (skill == Skills.SkillType.Bows || skill == Skills.SkillType.Crossbows)
            {
                _hasUsedShot = true;
                
                // Triple damage
                hitData.m_damage.Modify(DamageMultiplier);
                
                // Force backstab bonus (simulates crit)
                hitData.m_backstabBonus = 2.0f;
                
                if (m_character != null)
                {
                    m_character.Message(MessageHud.MessageType.Center, "<color=yellow>PERFECT SHOT!</color>");
                    SpawnVFX("fx_Lightning", m_character.transform.position);
                }
                
                Debug.Log($"[PerfectShotEffect] PERFECT SHOT fired! {DamageMultiplier * 100:F0}% damage!");
                
                // Remove effect after use
                Duration = 0.1f;
            }
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            // Aiming VFX
            if (!_hasUsedShot && m_character != null && Random.value < 0.1f)
            {
                SpawnVFX("fx_Lightning", m_character.transform.position + Vector3.up);
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
    
    #region Mage - Arcane Form
    
    /// <summary>
    /// Arcane Form Effect (Mage L100) - Pure magical energy.
    /// Spells cost no eitr and deal double damage.
    /// </summary>
    public class ArcaneFormEffect : CompanionStatusEffectBase
    {
        public float DamageBonus { get; set; } = 2.0f; // Double damage
        
        public override string Description => 
            $"<color=magenta>? ARCANE FORM ?</color>\n" +
            $"Spells cost no Eitr\n" +
            $"+{(DamageBonus - 1f) * 100:F0}% magic damage";
        
        /// <summary>Flag checked by eitr consumption systems.</summary>
        public bool NoEitrCost => true;
        
        public ArcaneFormEffect()
        {
            m_name = "Arcane Form";
            m_tooltip = "Transformed into pure magical energy";
            Duration = 15f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            // Arcane transformation VFX
            SpawnVFX("vfx_fireball_explosion", m_character.transform.position);
            SpawnVFX("vfx_StaffShield", m_character.transform.position);
            SpawnVFX("vfx_spiritbolt_explosion", m_character.transform.position);
            
            m_character.Message(MessageHud.MessageType.Center, "<color=magenta>? ARCANE FORM ?</color>");
            
            Debug.Log($"[ArcaneFormEffect] {m_character.m_name} transformed into ARCANE FORM!");
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            // Boost magic damage
            if (skill == Skills.SkillType.ElementalMagic || skill == Skills.SkillType.BloodMagic)
            {
                hitData.m_damage.Modify(DamageBonus);
            }
            
            // Also boost elemental damage types
            hitData.m_damage.m_fire *= DamageBonus;
            hitData.m_damage.m_frost *= DamageBonus;
            hitData.m_damage.m_lightning *= DamageBonus;
            hitData.m_damage.m_spirit *= DamageBonus;
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            // Arcane aura VFX
            if (m_character != null && Random.value < 0.2f)
            {
                Vector3 offset = Random.insideUnitSphere * 0.5f;
                SpawnVFX("vfx_StaffShield", m_character.transform.position + offset);
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Arcane Form dissipates...");
                SpawnVFX("vfx_fireball_explosion", m_character.transform.position);
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
    
    #region Healer - Avatar of Life
    
    /// <summary>
    /// Avatar of Life Effect (Healer L100) - Font of healing.
    /// Double healing output, cannot die.
    /// </summary>
    public class AvatarOfLifeEffect : CompanionStatusEffectBase
    {
        public float HealingBonus { get; set; } = 2.0f; // Double healing
        
        public override string Description => 
            $"<color=green>? AVATAR OF LIFE ?</color>\n" +
            $"2x healing power\n" +
            $"Cannot die";
        
        /// <summary>Flag to prevent death.</summary>
        public bool PreventsDeath => true;
        
        public AvatarOfLifeEffect()
        {
            m_name = "Avatar of Life";
            m_tooltip = "A conduit of pure life energy";
            Duration = 20f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            // Life transformation VFX
            SpawnVFX("fx_DvergerMage_Support_start", m_character.transform.position);
            SpawnVFX("fx_creature_tamed", m_character.transform.position);
            SpawnVFX("vfx_spiritbolt_explosion", m_character.transform.position);
            
            m_character.Message(MessageHud.MessageType.Center, "<color=green>? AVATAR OF LIFE ?</color>");
            
            Debug.Log($"[AvatarOfLifeEffect] {m_character.m_name} transformed into AVATAR OF LIFE!");
        }
        
        /// <summary>
        /// Prevents fatal damage while active.
        /// </summary>
        public override void OnDamaged(HitData hit, Character attacker)
        {
            if (m_character == null) return;
            
            float currentHealth = m_character.GetHealth();
            float incomingDamage = hit.GetTotalDamage();
            
            // If this would kill us, reduce damage to leave 1 HP
            if (incomingDamage >= currentHealth)
            {
                float damageToAllow = currentHealth - 1f;
                if (damageToAllow > 0)
                {
                    float reduction = damageToAllow / incomingDamage;
                    hit.m_damage.Modify(reduction);
                }
                else
                {
                    hit.m_damage.Modify(0f);
                }
                
                m_character.Message(MessageHud.MessageType.TopLeft, "<color=green>Life Sustained!</color>");
            }
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            // Life aura VFX
            if (m_character != null && Random.value < 0.15f)
            {
                SpawnVFX("fx_creature_tamed", m_character.transform.position);
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Avatar of Life fades...");
                SpawnVFX("fx_DvergerMage_Support_start", m_character.transform.position);
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
    
    #region Monk - Way of Perfection
    
    /// <summary>
    /// Way of Perfection Effect (Monk L100) - Martial mastery.
    /// Double attack speed, all attacks critically hit.
    /// </summary>
    public class WayOfPerfectionEffect : CompanionStatusEffectBase
    {
        public float AttackSpeedBonus { get; set; } = 2.0f; // Double attack speed
        public float CritBonus { get; set; } = 2.0f; // Double crit damage
        
        public override string Description => 
            $"<color=gold>? WAY OF PERFECTION ?</color>\n" +
            $"2x attack speed\n" +
            $"All attacks critical hit";
        
        public WayOfPerfectionEffect()
        {
            m_name = "Way of Perfection";
            m_tooltip = "Achieved martial perfection";
            Duration = 15f;
        }
        
        protected override void OnEffectApplied()
        {
            if (m_character == null) return;
            
            // Perfection VFX
            SpawnVFX("fx_fenring_frost", m_character.transform.position);
            SpawnVFX("vfx_Cold", m_character.transform.position);
            SpawnVFX("vfx_spiritbolt_explosion", m_character.transform.position);
            
            m_character.Message(MessageHud.MessageType.Center, "<color=gold>? WAY OF PERFECTION ?</color>");
            
            Debug.Log($"[WayOfPerfectionEffect] {m_character.m_name} achieved WAY OF PERFECTION!");
        }
        
        public override void ModifyAttack(Skills.SkillType skill, ref HitData hitData)
        {
            // All attacks crit
            hitData.m_backstabBonus = CritBonus;
            
            // Note: Attack speed would need animation system integration
            // For now, we simulate it with bonus damage
            hitData.m_damage.Modify(1.5f);
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            // Perfection aura
            if (m_character != null && Random.value < 0.2f)
            {
                SpawnVFX("fx_fenring_frost", m_character.transform.position);
            }
        }
        
        protected override void OnEffectRemoved()
        {
            if (m_character != null)
            {
                m_character.Message(MessageHud.MessageType.TopLeft, "Way of Perfection fades...");
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
