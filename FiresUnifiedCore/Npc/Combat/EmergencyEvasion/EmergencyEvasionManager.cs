using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using FiresCore.Npc.Archetypes;
using FiresCore.Npc.Utilities;

namespace FiresCore.Npc.Combat.EmergencyEvasion
{
    /// <summary>
    /// Each archetype's emergency escape (Arcane Blink, Sanctuary Fade, Savage Leap, Shadow Escape, Disengage,
    /// Shield Charge, Divine Retreat, Wind Step, and the Pyromancer's Infernal Blink), triggered when swarmed, at
    /// low health, or through the API.
    /// </summary>
    public class EmergencyEvasionManager : MonoBehaviour
    {
        #region Constants
        
        // Trigger conditions
        private const int SwarmEnemyCount = 3;
        private const float SwarmDetectionRange = 4f;
        private const float LowHealthThreshold = 0.25f;
        
        // Base cooldowns (can be modified by archetype)
        private const float BaseCooldown = 45f;
        private const float CheckInterval = 0.5f;
        
        // Teleport settings
        private const float BlinkDistance = 5f;
        private const float LeapDistance = 6f;
        private const float BackflipDistance = 4f;
        private const float ChargeDistance = 5f;
        
        // Damage/healing values (scaled by level)
        private const float BaseAoeDamage = 30f;
        private const float BaseHealing = 25f;
        private const float FireEvasionGroundSeconds = 3f;
        
        #endregion
        
        #region State
        
        private CompanionController _companion;
        private CompanionStats _stats;
        private CompanionCombat _combat;
        private ArchetypeController _archetypeController;
        private Character _character;
        private ZNetView _nview;
        
        private float _lastEvasionTime = -1000f;
        private float _lastCheckTime;
        private float _cooldown = BaseCooldown;
        private bool _isEvading;
        
        // Archetype-specific evasion
        private EvasionType _evasionType = EvasionType.None;
        private float _resourceCost;
        private bool _usesEitr;
        
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region Enums
        
        public enum EvasionType
        {
            None,
            ArcaneBlink,        // Mage - teleport + AoE damage at origin
            SanctuaryFade,      // Healer - teleport toward allies + heal at destination
            SavageLeap,         // Berserker - jump + AoE slam
            ShadowEscape,       // Rogue - vanish + smoke bomb
            Disengage,          // Ranger - backflip + arrow volley
            ShieldCharge,       // Tank - charge through + knockback
            DivineRetreat,      // Paladin - blind + reposition
            WindStep,           // Monk - rapid dodge + afterimages
            InfernalBlink       // Pyromancer hybrid - teleport + Fader Fire AOE
        }
        
        #endregion
        
        #region Properties
        
        public bool IsEvading => _isEvading;
        public float CooldownRemaining => Mathf.Max(0, _cooldown - (Time.time - _lastEvasionTime));
        public bool IsReady => Time.time - _lastEvasionTime >= _cooldown && !_isEvading;
        public EvasionType CurrentEvasionType => _evasionType;
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _stats = GetComponent<CompanionStats>();
            _combat = GetComponent<CompanionCombat>();
            _archetypeController = GetComponent<ArchetypeController>();
            _character = GetComponent<Character>();
            _nview = GetComponent<ZNetView>();
        }
        
        private void Start()
        {
            // Determine evasion type from archetype
            StartCoroutine(DetermineEvasionTypeDelayed());
        }
        
        private IEnumerator DetermineEvasionTypeDelayed()
        {
            // Wait for archetype assignment
            yield return new WaitForSeconds(1f);
            DetermineEvasionType();
        }
        
        private void Update()
        {
            if (_companion == null || _companion.isDefeated) return;
            if (_nview == null || !_nview.IsOwner()) return;
            if (_isEvading) return;
            if (_evasionType == EvasionType.None) return;
            
            // LEVEL GATE: Emergency evasion requires level 20+
            var progression = GetComponent<CompanionProgression>();
            int level = progression?.Level ?? 1;
            if (level < AbilityUnlockSystem.EVASION_UNLOCK_LEVEL)
            {
                return;
            }
            
            // Check interval
            if (Time.time - _lastCheckTime < CheckInterval) return;
            _lastCheckTime = Time.time;
            
            // Check if evasion should trigger
            if (ShouldTriggerEvasion())
            {
                TriggerEvasion();
            }
        }
        
        #endregion
        
        #region Evasion Type Determination
        
        /// <summary>
        /// Determines the evasion type based on archetype and hybrid status.
        /// </summary>
        public void DetermineEvasionType()
        {
            if (_archetypeController == null)
            {
                _evasionType = EvasionType.None;
                return;
            }
            
            var mainArchetype = _archetypeController.CurrentArchetypeClass;
            var subArchetype = _archetypeController.SubArchetypeClass;
            
            // Check for Pyromancer hybrid (Mage + Berserker) - gets special Infernal Blink
            if ((mainArchetype == ArchetypeClass.Mage && 
                 subArchetype == ArchetypeClass.Berserker) ||
                (mainArchetype == ArchetypeClass.Berserker && 
                 subArchetype == ArchetypeClass.Mage))
            {
                _evasionType = EvasionType.InfernalBlink;
                _resourceCost = 35f;
                _usesEitr = true;
                _cooldown = 50f;
                
                if (VerboseLogging)
                    Debug.Log($"[EmergencyEvasion] {_companion?.companionName} assigned INFERNAL BLINK (Pyromancer hybrid)");
                return;
            }
            
            // Standard archetype evasions
            switch (mainArchetype)
            {
                case ArchetypeClass.Mage:
                    _evasionType = EvasionType.ArcaneBlink;
                    _resourceCost = 30f;
                    _usesEitr = true;
                    _cooldown = 40f;
                    break;
                    
                case ArchetypeClass.Healer:
                    _evasionType = EvasionType.SanctuaryFade;
                    _resourceCost = 25f;
                    _usesEitr = true;
                    _cooldown = 45f;
                    break;
                    
                case ArchetypeClass.Berserker:
                    _evasionType = EvasionType.SavageLeap;
                    _resourceCost = 40f;
                    _usesEitr = false;
                    _cooldown = 35f;
                    break;
                    
                case ArchetypeClass.Rogue:
                    _evasionType = EvasionType.ShadowEscape;
                    _resourceCost = 25f;
                    _usesEitr = false;
                    _cooldown = 30f;
                    break;
                    
                case ArchetypeClass.Ranger:
                    _evasionType = EvasionType.Disengage;
                    _resourceCost = 30f;
                    _usesEitr = false;
                    _cooldown = 35f;
                    break;
                    
                case ArchetypeClass.Tank:
                    _evasionType = EvasionType.ShieldCharge;
                    _resourceCost = 35f;
                    _usesEitr = false;
                    _cooldown = 40f;
                    break;
                    
                case ArchetypeClass.Paladin:
                    _evasionType = EvasionType.DivineRetreat;
                    _resourceCost = 30f;
                    _usesEitr = false;
                    _cooldown = 40f;
                    break;
                    
                case ArchetypeClass.Monk:
                    _evasionType = EvasionType.WindStep;
                    _resourceCost = 20f;
                    _usesEitr = false;
                    _cooldown = 25f;
                    break;
                    
                default:
                    _evasionType = EvasionType.None;
                    break;
            }
            
            if (VerboseLogging && _evasionType != EvasionType.None)
            {
                Debug.Log($"[EmergencyEvasion] {_companion?.companionName} assigned {_evasionType} " +
                    $"(cost: {_resourceCost} {(_usesEitr ? "Eitr" : "Stamina")}, cd: {_cooldown}s)");
            }
        }
        
        #endregion
        
        #region Trigger Logic
        
        /// <summary>
        /// Checks if evasion should automatically trigger.
        /// </summary>
        private bool ShouldTriggerEvasion()
        {
            if (!IsReady) return false;
            if (!HasResources()) return false;
            
            // Check for swarm condition
            int nearbyEnemies = CountNearbyEnemies(SwarmDetectionRange);
            if (nearbyEnemies >= SwarmEnemyCount)
            {
                if (VerboseLogging)
                    Debug.Log($"[EmergencyEvasion] {_companion?.companionName} SWARMED by {nearbyEnemies} enemies!");
                return true;
            }
            
            // Check for low health condition
            if (_character != null && _character.GetHealthPercentage() < LowHealthThreshold)
            {
                // Only trigger if enemies are close
                if (nearbyEnemies >= 1)
                {
                    if (VerboseLogging)
                        Debug.Log($"[EmergencyEvasion] {_companion?.companionName} LOW HEALTH with enemies nearby!");
                    return true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Counts enemies within range.
        /// </summary>
        private int CountNearbyEnemies(float range)
        {
            int count = 0;
            Vector3 myPos = transform.position;
            
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                if (_character != null && !BaseAI.IsEnemy(_character, character)) continue;
                
                float dist = Vector3.Distance(myPos, character.transform.position);
                if (dist <= range)
                {
                    count++;
                }
            }
            
            return count;
        }
        
        /// <summary>
        /// Checks if companion has enough resources for evasion.
        /// </summary>
        private bool HasResources()
        {
            if (_stats == null) return true;
            
            if (_usesEitr)
            {
                return _stats.HasEitr(_resourceCost);
            }
            else
            {
                return _stats.HasStamina(_resourceCost);
            }
        }
        
        /// <summary>
        /// Consumes resources for evasion.
        /// </summary>
        private void ConsumeResources()
        {
            if (_stats == null) return;
            
            if (_usesEitr)
            {
                _stats.UseEitr(_resourceCost);
            }
            else
            {
                _stats.UseStamina(_resourceCost);
            }
        }
        
        #endregion
        
        #region Evasion Execution
        
        /// <summary>
        /// Triggers the evasion ability.
        /// </summary>
        public void TriggerEvasion()
        {
            if (!IsReady || _isEvading) return;
            if (!HasResources()) return;
            
            ConsumeResources();
            _lastEvasionTime = Time.time;
            _isEvading = true;
            
            Debug.Log($"[EmergencyEvasion] {_companion?.companionName} using {_evasionType}!");
            
            StartCoroutine(ExecuteEvasion());
        }
        
        /// <summary>
        /// Forces an evasion regardless of conditions (for API use).
        /// </summary>
        public bool ForceEvasion()
        {
            if (_isEvading || _evasionType == EvasionType.None) return false;
            
            _lastEvasionTime = Time.time;
            _isEvading = true;
            
            Debug.Log($"[EmergencyEvasion] {_companion?.companionName} FORCED {_evasionType}!");
            
            StartCoroutine(ExecuteEvasion());
            return true;
        }
        
        /// <summary>
        /// Executes the evasion ability based on type.
        /// </summary>
        private IEnumerator ExecuteEvasion()
        {
            Vector3 originPos = transform.position;
            
            switch (_evasionType)
            {
                case EvasionType.ArcaneBlink:
                    yield return ExecuteArcaneBlink(originPos);
                    break;
                    
                case EvasionType.InfernalBlink:
                    yield return ExecuteInfernalBlink(originPos);
                    break;
                    
                case EvasionType.SanctuaryFade:
                    yield return ExecuteSanctuaryFade(originPos);
                    break;
                    
                case EvasionType.SavageLeap:
                    yield return ExecuteSavageLeap(originPos);
                    break;
                    
                case EvasionType.ShadowEscape:
                    yield return ExecuteShadowEscape(originPos);
                    break;
                    
                case EvasionType.Disengage:
                    yield return ExecuteDisengage(originPos);
                    break;
                    
                case EvasionType.ShieldCharge:
                    yield return ExecuteShieldCharge(originPos);
                    break;
                    
                case EvasionType.DivineRetreat:
                    yield return ExecuteDivineRetreat(originPos);
                    break;
                    
                case EvasionType.WindStep:
                    yield return ExecuteWindStep(originPos);
                    break;
            }
            
            _isEvading = false;
        }
        
        #endregion
        
        #region Evasion Implementations
        
        /// <summary>
        /// Arcane Blink - Mage evasion.
        /// Teleport 5m away, leave AoE damage at origin.
        /// </summary>
        private IEnumerator ExecuteArcaneBlink(Vector3 origin)
        {
            // Find safe position away from enemies
            Vector3 destination = FindSafePosition(origin, BlinkDistance);
            
            // VFX at origin (before teleport)
            SpawnVFX("vfx_ghost_death", origin);
            
            // Brief delay for visual
            yield return new WaitForSeconds(0.1f);
            
            // Teleport
            TeleportTo(destination);
            
            // VFX at destination
            SpawnVFX("vfx_ghost_hit", destination);
            
            // Deal AoE damage at origin (enemies only)
            DealAoEDamage(origin, 4f, GetScaledDamage(BaseAoeDamage), false);
            
            // Spawn damage VFX at origin
            SpawnVFX("fx_fireball_staff_explosion", origin);
            
            yield return new WaitForSeconds(0.3f);
        }
        
        /// <summary>
        /// Infernal Blink - Pyromancer hybrid evasion.
        /// Teleport 5m away, leave Fader Fire AOE at origin (devastating fire damage).
        /// </summary>
        private IEnumerator ExecuteInfernalBlink(Vector3 origin)
        {
            // Find safe position away from enemies
            Vector3 destination = FindSafePosition(origin, BlinkDistance);
            
            // VFX at origin (demonic teleport out)
            SpawnVFX("vfx_ghost_death", origin);
            SpawnVFX("vfx_FireballHit", origin);
            
            // Brief delay
            yield return new WaitForSeconds(0.1f);
            
            // Teleport
            TeleportTo(destination);
            
            // VFX at destination (fire arrival)
            SpawnVFX("vfx_MeadBzerker", destination);
            
            // Spawn the Fader Fire AOE at origin - this is the key differentiator
            // The Fader AOE does continuous fire damage
            SpawnFaderFireAOE(origin);
            
            // Also deal immediate burst damage
            DealAoEDamage(origin, 5f, GetScaledDamage(BaseAoeDamage * 1.5f), false, HitData.DamageType.Fire);
            
            yield return new WaitForSeconds(0.3f);
        }
        
        /// <summary>
        /// Sanctuary Fade - Healer evasion.
        /// Teleport toward allies, healing pulse at destination.
        /// </summary>
        private IEnumerator ExecuteSanctuaryFade(Vector3 origin)
        {
            // Find position toward allies (not just away from enemies)
            Vector3 destination = FindPositionTowardAllies(origin, BlinkDistance);
            
            // VFX at origin (gentle fade)
            SpawnVFX("fx_DvergerMage_Support_start", origin);
            
            yield return new WaitForSeconds(0.15f);
            
            // Teleport
            TeleportTo(destination);
            
            // VFX at destination (healing arrival)
            SpawnVFX("fx_creature_tamed", destination);
            SpawnVFX("fx_DvergerMage_Support_start", destination);
            
            // Heal allies at destination
            HealAlliesInRange(destination, 6f, GetScaledHealing(BaseHealing));
            
            yield return new WaitForSeconds(0.3f);
        }
        
        /// <summary>
        /// Savage Leap - Berserker evasion.
        /// Jump up and away (or into) combat with AoE slam on landing.
        /// IMPROVED: Uses Character.OnAutoJump for proper physics-based jumping.
        /// </summary>
        private IEnumerator ExecuteSavageLeap(Vector3 origin)
        {
            // Berserkers can leap INTO or OUT OF combat depending on health
            bool leapInto = _character != null && _character.GetHealthPercentage() > 0.5f;
            Vector3 destination;
            
            if (leapInto)
            {
                // Find position with most enemies (aggressive)
                destination = FindPositionWithMostEnemies(origin, LeapDistance);
            }
            else
            {
                // Find safe position (defensive)
                destination = FindSafePosition(origin, LeapDistance);
            }
            
            // Launch VFX
            SpawnVFX("vfx_MeadBzerker", origin);
            
            // Calculate jump direction
            Vector3 jumpDirection = (destination - origin).normalized;
            jumpDirection.y = 0;
            if (jumpDirection.magnitude < 0.1f)
            {
                jumpDirection = transform.forward;
            }
            
            // Use Character.OnAutoJump for proper physics-based jump
            // This is the same method used by AutoJumpLedge triggers in the game
            if (_character != null)
            {
                float upVelocity = 8f;  // Strong upward force
                float forwardVelocity = LeapDistance / 0.5f; // Reach destination in ~0.5s
                
                // Face the jump direction
                transform.rotation = Quaternion.LookRotation(jumpDirection);
                
                // Trigger the jump using the game's built-in auto-jump system
                _character.OnAutoJump(jumpDirection, upVelocity, forwardVelocity);
                
                if (VerboseLogging)
                {
                    Debug.Log($"[EmergencyEvasion] Savage Leap using OnAutoJump: dir={jumpDirection}, up={upVelocity}, fwd={forwardVelocity}");
                }
            }
            else
            {
                // Fallback: Direct rigidbody jump if Character not available
                var body = GetComponent<Rigidbody>();
                if (body != null && !body.isKinematic)
                {
                    Vector3 jumpForce = jumpDirection * 12f + Vector3.up * 10f;
                    body.AddForce(jumpForce, ForceMode.VelocityChange);
                }
            }
            
            // Wait for the jump to complete (physics-based, so we monitor)
            float jumpTimeout = 1.0f;
            float startTime = Time.time;
            bool hasLanded = false;
            
            // Small delay to let the jump start
            yield return new WaitForSeconds(0.1f);
            
            // Monitor for landing
            while (Time.time - startTime < jumpTimeout && !hasLanded)
            {
                // Check if we've landed (character is on ground and near destination Y)
                if (_character != null && _character.IsOnGround())
                {
                    float heightDiff = Mathf.Abs(transform.position.y - destination.y);
                    if (heightDiff < 1.5f || Time.time - startTime > 0.3f)
                    {
                        hasLanded = true;
                    }
                }
                
                yield return null;
            }
            
            // Slam VFX and damage on landing
            Vector3 landingPos = transform.position;
            SpawnVFX("vfx_sledge_hit", landingPos);
            SpawnVFX("fx_shaman_protect", landingPos);
            
            // AoE damage on landing
            DealAoEDamage(landingPos, 4f, GetScaledDamage(BaseAoeDamage * 1.2f), false);
            
            yield return new WaitForSeconds(0.2f);
        }
        
        /// <summary>
        /// Shadow Escape - Rogue evasion.
        /// Vanish and reposition, leave smoke bomb at origin.
        /// IMPROVED: Uses CharacterVisibilityController for proper transparency on body AND equipment.
        /// </summary>
        private IEnumerator ExecuteShadowEscape(Vector3 origin)
        {
            // Find flanking position
            Vector3 destination = FindFlankingPosition(origin, BlinkDistance);
            
            // Smoke bomb VFX at origin
            SpawnVFX("vfx_ghost_death", origin);
            SpawnVFX("fx_backstab", origin);
            
            // IMPROVED: Use CharacterVisibilityController for proper hide on ALL renderers (body + equipment)
            CharacterVisibilityController visibilityController = null;
            if (_character != null)
            {
                visibilityController = new CharacterVisibilityController(_character);
                visibilityController.SetHidden(true);
            }
            
            // Short delay while invisible
            yield return new WaitForSeconds(0.15f);
            
            // Teleport (while invisible)
            TeleportTo(destination);
            
            // Subtle arrival VFX
            SpawnVFX("vfx_ghost_death", destination);
            
            // Short delay before showing model again
            yield return new WaitForSeconds(0.1f);
            
            // Show the model again
            visibilityController?.Restore();
            
            // Apply real stealth effect (with transparency) for ongoing combat advantage
            ApplyStealthEffect(3f);
            
            // Smoke at origin slows/blinds enemies
            ApplySmokeBombEffect(origin, 3f);
            
            yield return new WaitForSeconds(0.1f);
        }
        
        /// <summary>
        /// Disengage - Ranger evasion.
        /// Backflip away while firing arrows.
        /// IMPROVED: Uses OnAutoJump for proper physics-based backflip.
        /// </summary>
        private IEnumerator ExecuteDisengage(Vector3 origin)
        {
            // Find position directly away from nearest enemy
            Character nearestEnemy = FindNearestEnemy();
            Vector3 awayDir = nearestEnemy != null 
                ? (origin - nearestEnemy.transform.position).normalized 
                : -transform.forward;
            
            awayDir.y = 0;
            awayDir.Normalize();
            
            Vector3 destination = FindValidPosition(origin + awayDir * BackflipDistance);
            
            // Backflip VFX
            SpawnVFX("fx_Lightning", origin);
            
            // Use OnAutoJump for proper backflip physics
            if (_character != null)
            {
                float upVelocity = 4f;  // Small upward hop
                float backwardVelocity = BackflipDistance / 0.4f; // Reach destination in ~0.4s
                
                // Face away from enemy (so "forward" is our retreat direction)
                transform.rotation = Quaternion.LookRotation(awayDir);
                
                // Trigger the jump
                _character.OnAutoJump(awayDir, upVelocity, backwardVelocity);
            }
            
            // Fire arrows during retreat
            int arrowCount = 3;
            float flipDuration = 0.4f;
            float arrowInterval = flipDuration / arrowCount;
            
            for (int i = 0; i < arrowCount; i++)
            {
                // Fire arrow at nearest enemy
                if (nearestEnemy != null && !nearestEnemy.IsDead())
                {
                    FireArrowAtTarget(nearestEnemy, GetScaledDamage(BaseAoeDamage * 0.4f));
                }
                
                yield return new WaitForSeconds(arrowInterval);
            }
            
            // Landing VFX
            SpawnVFX("fx_natureweapon_hit", transform.position);
            
            yield return new WaitForSeconds(0.1f);
        }
        
        /// <summary>
        /// Shield Charge - Tank evasion.
        /// Charge through enemies, knocking them back.
        /// </summary>
        private IEnumerator ExecuteShieldCharge(Vector3 origin)
        {
            // Find position through enemies (not away)
            Vector3 chargeDir = FindChargeDirection(origin);
            Vector3 destination = FindValidPosition(origin + chargeDir * ChargeDistance);
            
            // Charge start VFX
            SpawnVFX("fx_shield_start", origin);
            SpawnVFX("fx_shaman_protect", origin);
            
            // Charge movement
            float chargeDuration = 0.3f;
            float elapsed = 0f;
            
            while (elapsed < chargeDuration)
            {
                elapsed += Time.deltaTime;
                float progress = elapsed / chargeDuration;
                
                Vector3 currentPos = Vector3.Lerp(origin, destination, progress);
                transform.position = currentPos;
                
                // Knockback enemies we pass through
                KnockbackEnemiesInPath(currentPos, 2f, chargeDir);
                
                yield return null;
            }
            
            // Ensure at destination
            transform.position = destination;
            
            // Impact VFX
            SpawnVFX("vfx_sledge_hit", destination);
            
            yield return new WaitForSeconds(0.2f);
        }
        
        /// <summary>
        /// Divine Retreat - Paladin evasion.
        /// Blinding flash at origin, reposition.
        /// </summary>
        private IEnumerator ExecuteDivineRetreat(Vector3 origin)
        {
            // Blinding flash
            SpawnVFX("vfx_ghost_hit", origin);
            SpawnVFX("fx_shield_start", origin);
            
            // Apply blind/stagger to nearby enemies
            StaggerEnemiesInRange(origin, 4f);
            
            yield return new WaitForSeconds(0.15f);
            
            // Find safe position
            Vector3 destination = FindSafePosition(origin, BlinkDistance);
            
            // Quick reposition (not instant teleport - divine dash)
            float dashDuration = 0.2f;
            float elapsed = 0f;
            
            while (elapsed < dashDuration)
            {
                elapsed += Time.deltaTime;
                float progress = elapsed / dashDuration;
                transform.position = Vector3.Lerp(origin, destination, progress);
                yield return null;
            }
            
            transform.position = destination;
            
            // Arrival VFX
            SpawnVFX("vfx_ghost_hit", destination);
            
            yield return new WaitForSeconds(0.1f);
        }
        
        /// <summary>
        /// Wind Step - Monk evasion.
        /// Rapid dodge with damaging afterimages.
        /// </summary>
        private IEnumerator ExecuteWindStep(Vector3 origin)
        {
            // Multiple rapid short teleports with afterimages
            int steps = 3;
            float stepDistance = BlinkDistance / steps;
            Vector3 awayDir = FindSafeDirection(origin);
            
            Vector3 currentPos = origin;
            
            for (int i = 0; i < steps; i++)
            {
                Vector3 nextPos = FindValidPosition(currentPos + awayDir * stepDistance);
                
                // Leave afterimage (VFX + minor damage)
                SpawnVFX("fx_fenring_frost", currentPos);
                SpawnVFX("vfx_Cold", currentPos);
                
                // Small damage at each afterimage
                DealAoEDamage(currentPos, 2f, GetScaledDamage(BaseAoeDamage * 0.2f), false);
                
                // Quick step
                transform.position = nextPos;
                currentPos = nextPos;
                
                yield return new WaitForSeconds(0.08f);
            }
            
            // Final position VFX
            SpawnVFX("fx_fenring_frost", currentPos);
            
            yield return new WaitForSeconds(0.1f);
        }
        
        #endregion
        
        #region Helper Methods
        
        /// <summary>
        /// Finds a safe position away from enemies.
        /// </summary>
        private Vector3 FindSafePosition(Vector3 origin, float distance)
        {
            // Calculate direction away from enemy centroid
            Vector3 enemyCentroid = Vector3.zero;
            int enemyCount = 0;
            
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(origin, character.transform.position);
                if (dist < 10f)
                {
                    enemyCentroid += character.transform.position;
                    enemyCount++;
                }
            }
            
            Vector3 awayDir;
            if (enemyCount > 0)
            {
                enemyCentroid /= enemyCount;
                awayDir = (origin - enemyCentroid).normalized;
            }
            else
            {
                awayDir = -transform.forward;
            }
            
            // Ensure horizontal
            awayDir.y = 0;
            awayDir.Normalize();
            
            return FindValidPosition(origin + awayDir * distance);
        }
        
        /// <summary>
        /// Finds a position toward allies (for healer).
        /// </summary>
        private Vector3 FindPositionTowardAllies(Vector3 origin, float distance)
        {
            if (_companion == null) return FindSafePosition(origin, distance);
            
            // Find owner or other companions
            var owner = _companion.GetOwner();
            if (owner != null && !owner.IsDead())
            {
                Vector3 toOwner = (owner.transform.position - origin).normalized;
                toOwner.y = 0;
                return FindValidPosition(origin + toOwner * distance);
            }
            
            // Fallback to safe position
            return FindSafePosition(origin, distance);
        }
        
        /// <summary>
        /// Finds position with most enemies (for aggressive berserker leap).
        /// </summary>
        private Vector3 FindPositionWithMostEnemies(Vector3 origin, float distance)
        {
            Character bestTarget = null;
            int bestNearbyCount = 0;
            
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(origin, character.transform.position);
                if (dist < distance * 1.5f)
                {
                    int nearbyCount = CountEnemiesNearPosition(character.transform.position, 4f);
                    if (nearbyCount > bestNearbyCount)
                    {
                        bestNearbyCount = nearbyCount;
                        bestTarget = character;
                    }
                }
            }
            
            if (bestTarget != null)
            {
                Vector3 toTarget = (bestTarget.transform.position - origin).normalized;
                return FindValidPosition(origin + toTarget * distance);
            }
            
            return FindSafePosition(origin, distance);
        }
        
        /// <summary>
        /// Finds a flanking position (for rogue).
        /// </summary>
        private Vector3 FindFlankingPosition(Vector3 origin, float distance)
        {
            Character nearestEnemy = FindNearestEnemy();
            if (nearestEnemy == null) return FindSafePosition(origin, distance);
            
            // Get perpendicular direction for flank
            Vector3 toEnemy = (nearestEnemy.transform.position - origin).normalized;
            Vector3 flankDir = Vector3.Cross(toEnemy, Vector3.up);
            
            // Randomly pick left or right
            if (UnityEngine.Random.value > 0.5f)
                flankDir = -flankDir;
            
            return FindValidPosition(origin + flankDir * distance);
        }
        
        /// <summary>
        /// Finds the best charge direction (for tank).
        /// </summary>
        private Vector3 FindChargeDirection(Vector3 origin)
        {
            // Charge through enemies toward allies
            var owner = _companion?.GetOwner();
            if (owner != null)
            {
                Vector3 toOwner = (owner.transform.position - origin).normalized;
                toOwner.y = 0;
                return toOwner.normalized;
            }
            
            return transform.forward;
        }
        
        /// <summary>
        /// Finds direction away from enemies.
        /// </summary>
        private Vector3 FindSafeDirection(Vector3 origin)
        {
            Vector3 enemyCentroid = Vector3.zero;
            int count = 0;
            
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(origin, character.transform.position);
                if (dist < 8f)
                {
                    enemyCentroid += character.transform.position;
                    count++;
                }
            }
            
            if (count > 0)
            {
                enemyCentroid /= count;
                Vector3 awayDir = (origin - enemyCentroid).normalized;
                awayDir.y = 0;
                return awayDir.normalized;
            }
            
            return -transform.forward;
        }
        
        /// <summary>
        /// Finds nearest enemy.
        /// </summary>
        private Character FindNearestEnemy()
        {
            Character nearest = null;
            float nearestDist = float.MaxValue;
            
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(transform.position, character.transform.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = character;
                }
            }
            
            return nearest;
        }
        
        /// <summary>
        /// Counts enemies near a position.
        /// </summary>
        private int CountEnemiesNearPosition(Vector3 pos, float range)
        {
            int count = 0;
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(pos, character.transform.position);
                if (dist <= range)
                    count++;
            }
            return count;
        }
        
        /// <summary>
        /// Finds a valid walkable position.
        /// </summary>
        private Vector3 FindValidPosition(Vector3 targetPos)
        {
            // Raycast down to find ground
            if (Physics.Raycast(targetPos + Vector3.up * 5f, Vector3.down, out RaycastHit hit, 10f, 
                LayerMask.GetMask("Default", "terrain", "static_solid")))
            {
                return hit.point + Vector3.up * 0.1f;
            }
            
            // Fallback to target with current Y
            return new Vector3(targetPos.x, transform.position.y, targetPos.z);
        }
        
        /// <summary>
        /// Teleports to destination with proper physics state reset.
        /// IMPROVED: Resets rigidbody velocity and ensures clean state transition.
        /// </summary>
        private void TeleportTo(Vector3 destination)
        {
            // Store current position for logging
            Vector3 oldPos = transform.position;
            
            // Set the new position
            transform.position = destination;
            
            // Reset physics state to prevent momentum carrying over
            if (_character != null)
            {
                var body = _character.GetComponent<Rigidbody>();
                if (body != null && !body.isKinematic)
                {
                    body.linearVelocity = Vector3.zero;
                    body.angularVelocity = Vector3.zero;
                }
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[EmergencyEvasion] Teleported from {oldPos} to {destination} (dist: {Vector3.Distance(oldPos, destination):F1}m)");
            }
        }
        
        /// <summary>
        /// Spawns VFX at position.
        /// </summary>
        private void SpawnVFX(string prefabName, Vector3 position) => AbilityFXManager.SpawnEffect(prefabName, position);
        
        /// <summary>
        /// Burning ground left behind by the fire evasion. The damage runs here on the owner: vanilla's fire Aoe prefabs
        /// (Fader_DroppedFire_AOE) are trigger AoEs that damage on every client and have no owner on remote ones, so
        /// they would burn other players.
        /// </summary>
        private void SpawnFaderFireAOE(Vector3 position)
        {
            SpawnVFX("fx_fireball_staff_explosion", position);
            StartCoroutine(SimulateFaderFireAOE(position, FireEvasionGroundSeconds));
        }

        private IEnumerator SimulateFaderFireAOE(Vector3 position, float duration)
        {
            float elapsed = 0f;
            float tickInterval = 0.5f;
            float damagePerTick = GetScaledDamage(BaseAoeDamage * 0.3f);
            
            while (elapsed < duration)
            {
                // Deal fire damage to enemies in range
                DealAoEDamage(position, 4f, damagePerTick, false, HitData.DamageType.Fire);
                
                // Fire VFX
                SpawnVFX("vfx_FireballHit", position + UnityEngine.Random.insideUnitSphere * 1.5f);
                
                elapsed += tickInterval;
                yield return new WaitForSeconds(tickInterval);
            }
        }
        
        /// <summary>
        /// Deals AoE damage to enemies (not friendlies).
        /// </summary>
        private void DealAoEDamage(Vector3 position, float radius, float damage, bool hitFriendlies, 
            HitData.DamageType damageType = HitData.DamageType.Blunt)
        {
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                
                // Skip friendlies unless specified
                if (!hitFriendlies)
                {
                    if (character.IsTamed() || character.IsPlayer()) continue;
                    
                    // Check if same owner
                    var otherCompanion = character.GetComponent<CompanionController>();
                    if (otherCompanion != null && _companion != null && 
                        otherCompanion.ownerPlayerId == _companion.ownerPlayerId)
                        continue;
                }
                
                float dist = Vector3.Distance(position, character.transform.position);
                if (dist > radius) continue;
                
                // Create hit data
                var hitData = new HitData();
                hitData.m_point = character.transform.position;
                hitData.m_dir = (character.transform.position - position).normalized;
                hitData.m_attacker = _character?.GetZDOID() ?? ZDOID.None;
                
                // Set damage type
                switch (damageType)
                {
                    case HitData.DamageType.Fire:
                        hitData.m_damage.m_fire = damage;
                        break;
                    case HitData.DamageType.Frost:
                        hitData.m_damage.m_frost = damage;
                        break;
                    case HitData.DamageType.Lightning:
                        hitData.m_damage.m_lightning = damage;
                        break;
                    default:
                        hitData.m_damage.m_blunt = damage;
                        break;
                }
                
                character.Damage(hitData);
            }
        }
        
        /// <summary>
        /// Heals allies in range.
        /// </summary>
        private void HealAlliesInRange(Vector3 position, float radius, float healing)
        {
            // Heal owner
            var owner = _companion?.GetOwner();
            if (owner != null && !owner.IsDead())
            {
                float dist = Vector3.Distance(position, owner.transform.position);
                if (dist <= radius)
                {
                    owner.Heal(healing, true);
                    SpawnVFX("fx_creature_tamed", owner.transform.position);
                }
            }
            
            // Heal self
            if (_character != null && !_character.IsDead())
            {
                _character.Heal(healing, true);
            }
            
            // Heal other companions
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                if (character.IsPlayer()) continue;
                
                var otherCompanion = character.GetComponent<CompanionController>();
                if (otherCompanion != null && _companion != null && 
                    otherCompanion.ownerPlayerId == _companion.ownerPlayerId)
                {
                    float dist = Vector3.Distance(position, character.transform.position);
                    if (dist <= radius)
                    {
                        character.Heal(healing, true);
                        SpawnVFX("fx_creature_tamed", character.transform.position);
                    }
                }
            }
        }
        
        /// <summary>
        /// Applies stealth effect to the companion using the proper status effect system.
        /// IMPROVED: Actually applies the StealthEffect from the StatusEffectManager.
        /// </summary>
        private void ApplyStealthEffect(float duration)
        {
            if (_character == null) return;
            
            try
            {
                // Use the StatusEffectManager's stealth application method
                FiresCore.Npc.Archetypes.StatusEffects.Rogue.StealthEffect.ApplyStealth(_character, duration);
                
                if (VerboseLogging)
                {
                    Debug.Log($"[EmergencyEvasion] Applied stealth effect to {_character.m_name} for {duration}s");
                }
            }
            catch (System.Exception ex)
            {
                if (VerboseLogging)
                {
                    Debug.LogWarning($"[EmergencyEvasion] Failed to apply stealth: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Applies smoke bomb effect (slow enemies).
        /// </summary>
        private void ApplySmokeBombEffect(Vector3 position, float radius)
        {
            // Apply slowdown/stagger to enemies in smoke
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(position, character.transform.position);
                if (dist <= radius)
                {
                    // Stagger enemies in smoke
                    Vector3 staggerDir = (character.transform.position - position).normalized;
                    character.Stagger(staggerDir);
                }
            }
        }
        
        /// <summary>
        /// Fires an arrow at target.
        /// </summary>
        private void FireArrowAtTarget(Character target, float damage)
        {
            if (target == null) return;
            
            // Spawn arrow projectile or just deal damage
            var hitData = new HitData();
            hitData.m_point = target.transform.position;
            hitData.m_dir = (target.transform.position - transform.position).normalized;
            hitData.m_attacker = _character?.GetZDOID() ?? ZDOID.None;
            hitData.m_damage.m_pierce = damage;
            
            target.Damage(hitData);
            
            // Arrow hit VFX
            SpawnVFX("vfx_arrowhit", target.transform.position);
        }
        
        /// <summary>
        /// Knocks back enemies in path.
        /// </summary>
        private void KnockbackEnemiesInPath(Vector3 position, float radius, Vector3 direction)
        {
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(position, character.transform.position);
                if (dist <= radius)
                {
                    // Apply knockback force
                    var body = character.GetComponent<Rigidbody>();
                    if (body != null)
                    {
                        Vector3 knockDir = (character.transform.position - position).normalized;
                        knockDir.y = 0.3f; // Slight upward
                        body.AddForce(knockDir * 500f, ForceMode.Impulse);
                    }
                    
                    // Also stagger
                    character.Stagger(direction);
                }
            }
        }
        
        /// <summary>
        /// Staggers enemies in range (for paladin blind).
        /// </summary>
        private void StaggerEnemiesInRange(Vector3 position, float radius)
        {
            foreach (var character in Character.GetAllCharacters())
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(position, character.transform.position);
                if (dist <= radius)
                {
                    Vector3 staggerDir = (character.transform.position - position).normalized;
                    character.Stagger(staggerDir);
                }
            }
        }
        
        /// <summary>
        /// Gets damage scaled by companion level and luck.
        /// Uses the centralized CompanionLuck system for consistent scaling across all abilities.
        /// </summary>
        private float GetScaledDamage(float baseDamage)
        {
            return baseDamage * GetLevelScaling();
        }
        
        /// <summary>
        /// Gets healing scaled by companion level and luck.
        /// Uses the centralized CompanionLuck system for consistent scaling across all abilities.
        /// </summary>
        private float GetScaledHealing(float baseHealing)
        {
            return baseHealing * GetLevelScaling();
        }
        
        // Cache CompanionLuck reference
        private CompanionLuck _companionLuck;
        
        /// <summary>
        /// Gets level-based scaling multiplier using the CompanionLuck system.
        /// This ensures consistent scaling across all companion abilities based on their luck stat.
        /// 
        /// Scaling range (at level 100):
        /// - 0 Luck: +24.75% (worst case)
        /// - 50 Luck: +37.125% (average)
        /// - 100 Luck: +49.5% (best case)
        /// </summary>
        private float GetLevelScaling()
        {
            // Get or cache the CompanionLuck component
            if (_companionLuck == null)
            {
                _companionLuck = GetComponent<CompanionLuck>();
            }
            
            // If we have CompanionLuck, use its scaling system
            if (_companionLuck != null)
            {
                return _companionLuck.GetLevelScalingMultiplier();
            }
            
            // Fallback: use average scaling if CompanionLuck not available
            var progression = GetComponent<CompanionProgression>();
            if (progression != null)
            {
                int level = progression.Level;
                if (level <= 1) return 1f;
                
                // Use middle scaling value (0.375% per level) as fallback
                return 1f + (level - 1) * 0.00375f;
            }
            
            return 1f;
        }
        
        #endregion
        
        #region Public API
        
        /// <summary>
        /// Called when archetype changes to update evasion type.
        /// </summary>
        public void OnArchetypeChanged()
        {
            DetermineEvasionType();
        }
        
        /// <summary>
        /// Gets the display name for the current evasion type.
        /// </summary>
        public string GetEvasionName()
        {
            switch (_evasionType)
            {
                case EvasionType.ArcaneBlink: return "Arcane Blink";
                case EvasionType.InfernalBlink: return "Infernal Blink";
                case EvasionType.SanctuaryFade: return "Sanctuary Fade";
                case EvasionType.SavageLeap: return "Savage Leap";
                case EvasionType.ShadowEscape: return "Shadow Escape";
                case EvasionType.Disengage: return "Disengage";
                case EvasionType.ShieldCharge: return "Shield Charge";
                case EvasionType.DivineRetreat: return "Divine Retreat";
                case EvasionType.WindStep: return "Wind Step";
                default: return "None";
            }
        }
        
        #endregion
    }
}
