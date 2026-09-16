using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Npc;
using FiresCore.Npc.Archetypes.StatusEffects;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// Group-wide bonus effects when companions' abilities combine: sequential combos within a time window, passive
    /// synergies from the archetypes present, and chain reactions where one ability enhances another.
    /// </summary>
    public class GroupSynergyManager : MonoBehaviour
    {
        private static GroupSynergyManager _instance;
        public static GroupSynergyManager Instance => _instance;
        
        public static bool VerboseLogging = false;
        
        #region Combo Configuration
        
        /// <summary>Time window (seconds) for sequential combos.</summary>
        private const float ComboWindow = 5f;
        
        /// <summary>Minimum interval between the same combo triggering.</summary>
        private const float ComboCooldown = 30f;
        
        /// <summary>Range for synergy effects.</summary>
        private const float SynergyRange = 20f;

        private const int PresenceSynergyCheckFrameInterval = 60;
        private const int MaxRecentAbilitiesForCombo = 5;
        private const float CoordinatedAssaultDamageReduction = 0.85f;
        private const float HolyBastionInvulnerableSeconds = 3f;
        private const float HolyBastionHealAmount = 30f;
        private const float PrimalStormFireShare = 0.33f;
        private const float PrimalStormFrostShare = 0.33f;
        private const float PrimalStormLightningShare = 0.34f;
        private const float ArcaneConvergenceShieldHealth = 50f;
        private const float DivineHarmonyHealAmount = 75f;
        private const float ChiResonanceHealAmount = 40f;
        private const float ChiResonanceStaminaAmount = 30f;
        private const float ComboTextHeightOffset = 2.5f;
        private const float CoordinatedAssaultEffectDuration = 10f;
        private const float HolyBastionEffectDuration = 8f;
        private const float PrimalStormEffectDuration = 5f;
        private const float ShadowDanceEffectDuration = 8f;
        private const float NaturesFuryEffectDuration = 12f;
        private const float ArcaneConvergenceEffectDuration = 15f;
        private const float DivineHarmonyEffectDuration = 10f;
        private const float ChiResonanceEffectDuration = 10f;
        private const float BalancedPartyDamageBonus = 1.05f;
        private const float BalancedPartyDefenseBonus = 1.05f;
        private const float HolyVanguardDefenseBonus = 1.1f;
        private const float FuryUnleashedCritBonus = 0.1f;
        private const float ArcaneBrotherhoodEitrRegenBonus = 1.15f;
        private const float MartialMasteryAttackSpeedBonus = 1.1f;
        private const float DivineCrusadeHealingBonus = 1.2f;

        #endregion
        
        #region Tracking
        
        /// <summary>Tracks recent ability uses for combo detection.</summary>
        private class AbilityUse
        {
            public long OwnerPlayerId;
            public string CompanionId;
            public string CompanionName;
            public ArchetypeClass Archetype;
            public ArchetypeClass SubArchetype;
            public string AbilityName;
            public float Timestamp;
            public Character Character;
        }
        
        private List<AbilityUse> _recentAbilities = new List<AbilityUse>();
        private Dictionary<string, float> _comboCooldowns = new Dictionary<string, float>();
        
        /// <summary>Tracks active presence synergies per player.</summary>
        private Dictionary<long, HashSet<string>> _activeSynergies = new Dictionary<long, HashSet<string>>();
        
        #endregion
        
        #region Initialization
        
        public static void Initialize()
        {
            if (_instance != null) return;
            
            var go = new GameObject("GroupSynergyManager");
            _instance = go.AddComponent<GroupSynergyManager>();
            DontDestroyOnLoad(go);
            
            Debug.Log("[GroupSynergyManager] Initialized");
        }
        
        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
        }
        
        private void OnDestroy()
        {
            if (_instance == this)
                _instance = null;
        }
        
        #endregion
        
        #region Update Loop
        
        private void Update()
        {
            // Suppress combo / presence-synergy work during the local player's
            // respawn window — UpdatePresenceSynergies can trigger ability RPCs.
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;

            // Clean up old ability uses
            float cutoff = Time.time - ComboWindow;
            _recentAbilities.RemoveAll(a => a.Timestamp < cutoff);

            // Check presence synergies periodically
            if (Time.frameCount % PresenceSynergyCheckFrameInterval == 0) // Every ~1 second at 60fps
            {
                UpdatePresenceSynergies();
            }
        }
        
        #endregion
        
        #region Public API
        
        /// <summary>
        /// Called when a companion uses an ability. Checks for combo opportunities.
        /// </summary>
        /// <param name="companion">The companion using the ability.</param>
        /// <param name="abilityName">Name of the ability used.</param>
        public void OnAbilityUsed(CompanionController companion, string abilityName)
        {
            if (companion == null || string.IsNullOrEmpty(abilityName)) return;
            
            var archController = companion.GetArchetypeController();
            if (archController == null) return;
            
            // Record this ability use
            var use = new AbilityUse
            {
                OwnerPlayerId = companion.ownerPlayerId,
                CompanionId = companion.companionId,
                CompanionName = companion.companionName,
                Archetype = archController.CurrentArchetypeClass,
                SubArchetype = archController.SubArchetypeClass,
                AbilityName = abilityName,
                Timestamp = Time.time,
                Character = companion.GetCharacter()
            };
            
            _recentAbilities.Add(use);
            
            if (VerboseLogging)
            {
                Debug.Log($"[GroupSynergy] Ability recorded: {abilityName} by {companion.companionName} ({use.Archetype}/{use.SubArchetype})");
            }
            
            // Check for combos
            CheckForCombos(use);
        }
        
        /// <summary>
        /// Gets active synergy bonuses for a player's group.
        /// </summary>
        public List<SynergyBonus> GetActiveSynergies(long playerId)
        {
            var bonuses = new List<SynergyBonus>();
            
            if (_activeSynergies.TryGetValue(playerId, out var synergies))
            {
                foreach (var synergyName in synergies)
                {
                    var definition = GetSynergyDefinition(synergyName);
                    if (definition != null)
                    {
                        bonuses.Add(definition);
                    }
                }
            }
            
            return bonuses;
        }
        
        /// <summary>
        /// Checks if a companion should delay using an ability to avoid stacking
        /// identical buffs/debuffs with another companion who just used the same ability.
        /// Returns true if the ability was used by another group member within the delay window.
        /// </summary>
        /// <param name="companionId">The companion considering the ability.</param>
        /// <param name="abilityName">The ability name to check.</param>
        /// <param name="delayWindow">Time window in seconds (default 3s).</param>
        /// <returns>True if the ability should be delayed to avoid stacking.</returns>
        public bool ShouldDelayAbility(string companionId, string abilityName, float delayWindow = 3f)
        {
            if (string.IsNullOrEmpty(companionId) || string.IsNullOrEmpty(abilityName))
                return false;
            
            float cutoff = Time.time - delayWindow;
            
            foreach (var use in _recentAbilities)
            {
                if (use.Timestamp < cutoff) continue;
                if (use.CompanionId == companionId) continue; // Don't check against self
                if (use.AbilityName == abilityName)
                {
                    if (VerboseLogging)
                        Debug.Log($"[GroupSynergy] {companionId} should delay {abilityName} - {use.CompanionName} used it {Time.time - use.Timestamp:F1}s ago");
                    return true;
                }
            }
            
            return false;
        }
        
        #endregion
        
        #region Combo Detection
        
        private void CheckForCombos(AbilityUse newUse)
        {
            // Get recent abilities from the same player group
            var recentGroupAbilities = _recentAbilities
                .Where(a => a.OwnerPlayerId == newUse.OwnerPlayerId && a != newUse)
                .OrderByDescending(a => a.Timestamp)
                .Take(MaxRecentAbilitiesForCombo)
                .ToList();
            
            if (recentGroupAbilities.Count == 0) return;
            
            // Check each defined combo
            foreach (var combo in GetAllCombos())
            {
                if (IsComboOnCooldown(combo.Name)) continue;
                
                if (CheckComboCondition(combo, newUse, recentGroupAbilities))
                {
                    TriggerCombo(combo, newUse);
                }
            }
        }
        
        private bool CheckComboCondition(ComboDefinition combo, AbilityUse newUse, List<AbilityUse> recentUses)
        {
            // Check if the new ability matches one of the combo requirements
            bool matchesNew = false;
            foreach (var req in combo.RequiredAbilities)
            {
                if (MatchesRequirement(newUse, req))
                {
                    matchesNew = true;
                    break;
                }
            }
            
            if (!matchesNew) return false;
            
            // Check if all other requirements are met by recent uses
            int requiredCount = combo.RequiredAbilities.Count;
            int matchedCount = 1; // New use is already matched
            
            var matchedIds = new HashSet<string> { newUse.CompanionId };
            
            foreach (var recent in recentUses)
            {
                if (matchedIds.Contains(recent.CompanionId)) continue; // Each companion can only contribute once
                
                foreach (var req in combo.RequiredAbilities)
                {
                    if (MatchesRequirement(recent, req) && !IsRequirementFulfilledBy(combo, req, newUse))
                    {
                        matchedCount++;
                        matchedIds.Add(recent.CompanionId);
                        break;
                    }
                }
                
                if (matchedCount >= requiredCount) break;
            }
            
            return matchedCount >= requiredCount;
        }
        
        private bool MatchesRequirement(AbilityUse use, ComboRequirement req)
        {
            // Check archetype match
            if (req.RequiredArchetype != ArchetypeClass.None && use.Archetype != req.RequiredArchetype)
                return false;
            
            // Check sub-archetype match
            if (req.RequiredSubArchetype != ArchetypeClass.None && use.SubArchetype != req.RequiredSubArchetype)
                return false;
            
            // Check ability name match
            if (!string.IsNullOrEmpty(req.RequiredAbilityName) && use.AbilityName != req.RequiredAbilityName)
                return false;
            
            // Check ability type match
            if (req.RequiredAbilityType != AbilityType.Any)
            {
                var type = GetAbilityType(use.AbilityName);
                if (type != req.RequiredAbilityType)
                    return false;
            }
            
            return true;
        }
        
        private bool IsRequirementFulfilledBy(ComboDefinition combo, ComboRequirement req, AbilityUse use)
        {
            return MatchesRequirement(use, req);
        }
        
        private void TriggerCombo(ComboDefinition combo, AbilityUse triggerUse)
        {
            // Set cooldown
            _comboCooldowns[combo.Name] = Time.time + ComboCooldown;
            
            // Find all allies in range
            var allies = GetAlliesInRange(triggerUse.Character, SynergyRange);
            
            // Announce combo
            AnnounceCombo(combo, triggerUse);
            
            // Apply combo effect
            ApplyComboEffect(combo, triggerUse, allies);
            
            Debug.Log($"[GroupSynergy] COMBO TRIGGERED: {combo.Name} by {triggerUse.CompanionName}!");
        }
        
        private bool IsComboOnCooldown(string comboName)
        {
            if (_comboCooldowns.TryGetValue(comboName, out float cooldownEnd))
            {
                return Time.time < cooldownEnd;
            }
            return false;
        }
        
        #endregion
        
        #region Combo Effects
        
        private void ApplyComboEffect(ComboDefinition combo, AbilityUse trigger, List<Character> allies)
        {
            switch (combo.Name)
            {
                case "Coordinated Assault":
                    ApplyCoordinatedAssault(allies, combo.EffectDuration);
                    break;
                    
                case "Holy Bastion":
                    ApplyHolyBastion(allies, combo.EffectDuration);
                    break;
                    
                case "Primal Storm":
                    ApplyPrimalStorm(trigger.Character, combo.EffectDuration);
                    break;
                    
                case "Shadow Dance":
                    ApplyShadowDance(allies, combo.EffectDuration);
                    break;
                    
                case "Nature's Fury":
                    ApplyNaturesFury(trigger.Character, allies, combo.EffectDuration);
                    break;
                    
                case "Arcane Convergence":
                    ApplyArcaneConvergence(allies, combo.EffectDuration);
                    break;
                    
                case "Divine Harmony":
                    ApplyDivineHarmony(allies, combo.EffectDuration);
                    break;
                    
                case "Chi Resonance":
                    ApplyChiResonance(allies, combo.EffectDuration);
                    break;
                    
                default:
                    // Generic combo bonus
                    ApplyGenericComboBonus(allies, combo);
                    break;
            }
        }
        
        /// <summary>
        /// Tank + Berserker combo: All allies gain damage and damage reduction.
        /// </summary>
        private void ApplyCoordinatedAssault(List<Character> allies, float duration)
        {
            foreach (var ally in allies)
            {
                // Apply warcry + fortify combo
                StatusEffectManager.ApplyWarcry(ally, 0f, duration);
                StatusEffectManager.ApplyFortify(ally, duration / 2f, CoordinatedAssaultDamageReduction);
            }
            
            AbilityFXManager.SpawnEffect("fx_eikthyr_stomp", allies[0].transform.position, null, 2f);
        }
        
        /// <summary>
        /// Tank + Paladin combo: Group-wide damage immunity and healing.
        /// </summary>
        private void ApplyHolyBastion(List<Character> allies, float duration)
        {
            foreach (var ally in allies)
            {
                StatusEffectManager.ApplyInvulnerable(ally, HolyBastionInvulnerableSeconds);
                ally.Heal(HolyBastionHealAmount, true);
            }
            
            AbilityFXManager.SpawnEffect("fx_shield_start", allies[0].transform.position, null, 2.5f);
        }
        
        /// <summary>
        /// Berserker + Mage combo: AoE elemental explosion.
        /// </summary>
        private void ApplyPrimalStorm(Character source, float duration)
        {
            if (source == null) return;
            
            float damage = 100f;
            float range = 10f;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (!BaseAI.IsEnemy(source, character)) continue;
                if (character.IsPlayer()) continue;
                
                float dist = Vector3.Distance(source.transform.position, character.transform.position);
                if (dist <= range)
                {
                    var hit = new HitData
                    {
                        m_damage = { m_fire = damage * PrimalStormFireShare, m_frost = damage * PrimalStormFrostShare, m_lightning = damage * PrimalStormLightningShare },
                        m_attacker = source.GetZDOID(),
                        m_point = character.transform.position
                    };
                    character.ApplyDamage(hit, true, true, HitData.DamageModifier.Normal);
                }
            }
            
            AbilityFXManager.SpawnEffect("vfx_fireball_explosion", source.transform.position, null, 1.5f);
            AbilityFXManager.SpawnEffect("vfx_thunderbolt_explosion", source.transform.position, null, 1.2f);
        }
        
        /// <summary>
        /// Rogue + Monk combo: All allies gain stealth and attack speed.
        /// </summary>
        private void ApplyShadowDance(List<Character> allies, float duration)
        {
            foreach (var ally in allies)
            {
                StatusEffectManager.ApplyStealth(ally, duration / 2f);
                StatusEffectManager.ApplyChiStrike(ally, duration);
            }
            
            AbilityFXManager.SpawnEffect("vfx_odin_despawn", allies[0].transform.position, null, 1.5f);
        }
        
        /// <summary>
        /// Ranger + Healer combo: Healing over time and bonus damage.
        /// </summary>
        private void ApplyNaturesFury(Character source, List<Character> allies, float duration)
        {
            foreach (var ally in allies)
            {
                StatusEffectManager.ApplyPurify(ally, duration);
                StatusEffectManager.ApplyHuntersMark(ally, source, duration); // Temp bonus
            }
            
            AbilityFXManager.SpawnEffect("fx_natureweapon_hit", source.transform.position, null, 1.5f);
        }
        
        /// <summary>
        /// Mage + Mage (or Mage + Arcane hybrid) combo: Eitr regeneration and spell power.
        /// </summary>
        private void ApplyArcaneConvergence(List<Character> allies, float duration)
        {
            foreach (var ally in allies)
            {
                StatusEffectManager.ApplyElementalInfusion(ally, duration);
                StatusEffectManager.ApplyArcaneShield(ally, duration, ArcaneConvergenceShieldHealth);
            }
            
            AbilityFXManager.SpawnEffect("vfx_StaffShield", allies[0].transform.position, null, 2f);
        }
        
        /// <summary>
        /// Paladin + Healer combo: Massive group heal and protection.
        /// </summary>
        private void ApplyDivineHarmony(List<Character> allies, float duration)
        {
            foreach (var ally in allies)
            {
                ally.Heal(DivineHarmonyHealAmount, true);
                StatusEffectManager.ApplySanctuary(ally, 0f, duration);
                StatusEffectManager.ApplyDivineProtection(ally, 0f, duration);
            }
            
            AbilityFXManager.SpawnEffect("fx_DvergerMage_Support_start", allies[0].transform.position, null, 2f);
        }
        
        /// <summary>
        /// Monk + Healer combo: Stamina and health regeneration.
        /// </summary>
        private void ApplyChiResonance(List<Character> allies, float duration)
        {
            foreach (var ally in allies)
            {
                StatusEffectManager.ApplyInnerPeace(ally, duration);
                ally.Heal(ChiResonanceHealAmount, true);

                if (ally is Player player)
                {
                    player.AddStamina(ChiResonanceStaminaAmount);
                }
            }
            
            AbilityFXManager.SpawnEffect("fx_creature_tamed", allies[0].transform.position, null, 1.5f);
        }
        
        /// <summary>
        /// Generic combo bonus for undefined combos.
        /// </summary>
        private void ApplyGenericComboBonus(List<Character> allies, ComboDefinition combo)
        {
            foreach (var ally in allies)
            {
                StatusEffectManager.ApplyWarcry(ally, 0f, combo.EffectDuration);
            }
        }
        
        #endregion
        
        #region Presence Synergies
        
        private void UpdatePresenceSynergies()
        {
            // Group companions by owner
            var groups = CompanionController.AllCompanions
                .Where(c => c != null && !c.isDefeated && c.isTamed)
                .GroupBy(c => c.ownerPlayerId);
            
            foreach (var group in groups)
            {
                var playerId = group.Key;
                var archetypes = new HashSet<ArchetypeClass>();
                var subArchetypes = new HashSet<ArchetypeClass>();
                
                foreach (var companion in group)
                {
                    var arch = companion.GetArchetypeController();
                    if (arch != null)
                    {
                        archetypes.Add(arch.CurrentArchetypeClass);
                        if (arch.SubArchetypeClass != ArchetypeClass.None)
                        {
                            subArchetypes.Add(arch.SubArchetypeClass);
                        }
                    }
                }
                
                // Check presence synergies
                var activeSynergies = new HashSet<string>();
                
                foreach (var synergy in GetAllPresenceSynergies())
                {
                    if (CheckPresenceSynergy(synergy, archetypes, subArchetypes))
                    {
                        activeSynergies.Add(synergy.Name);
                    }
                }
                
                // Update active synergies and apply/remove as needed
                if (!_activeSynergies.TryGetValue(playerId, out var previousSynergies))
                {
                    previousSynergies = new HashSet<string>();
                }
                
                // Apply new synergies
                foreach (var synergy in activeSynergies.Except(previousSynergies))
                {
                    OnSynergyActivated(playerId, synergy, group.ToList());
                }
                
                // Remove old synergies
                foreach (var synergy in previousSynergies.Except(activeSynergies))
                {
                    OnSynergyDeactivated(playerId, synergy, group.ToList());
                }
                
                _activeSynergies[playerId] = activeSynergies;
            }
        }
        
        private bool CheckPresenceSynergy(SynergyBonus synergy, HashSet<ArchetypeClass> archetypes, HashSet<ArchetypeClass> subArchetypes)
        {
            // Check required archetypes
            foreach (var required in synergy.RequiredArchetypes)
            {
                if (!archetypes.Contains(required) && !subArchetypes.Contains(required))
                    return false;
            }
            
            return true;
        }
        
        private void OnSynergyActivated(long playerId, string synergyName, List<CompanionController> companions)
        {
            var synergy = GetSynergyDefinition(synergyName);
            if (synergy == null) return;
            
            if (VerboseLogging || true) // Always log synergy activation
            {
                Debug.Log($"[GroupSynergy] SYNERGY ACTIVATED: {synergyName} for player {playerId}");
            }
            
            // Could apply passive buffs here
        }
        
        private void OnSynergyDeactivated(long playerId, string synergyName, List<CompanionController> companions)
        {
            if (VerboseLogging)
            {
                Debug.Log($"[GroupSynergy] Synergy deactivated: {synergyName} for player {playerId}");
            }
        }
        
        #endregion
        
        #region Combo Announcements
        
        private void AnnounceCombo(ComboDefinition combo, AbilityUse trigger)
        {
            // Show floating text above the companion (like damage/heal numbers)
            // This is much less intrusive than center-screen text
            if (DamageText.instance != null && trigger.Character != null)
            {
                Vector3 textPos = trigger.Character.transform.position + Vector3.up * ComboTextHeightOffset;
                DamageText.instance.ShowText(
                    DamageText.TextType.Heal, // Use Heal type for gold-ish color
                    textPos,
                    combo.DisplayName,
                    true // Large text
                );
            }
            
            // Also show a subtle left-side message for more details
            MessageHud.instance?.ShowMessage(
                MessageHud.MessageType.TopLeft, 
                $"<color=#ffcc00>{combo.DisplayName}</color>: {combo.Description}"
            );
            
            // Chat bubble from companion
            ArchetypeChatManager.AnnounceCombo(trigger.CompanionName, combo.DisplayName);
        }
        
        #endregion
        
        #region Helper Methods
        
        private List<Character> GetAlliesInRange(Character source, float range)
        {
            var allies = new List<Character>();
            if (source == null) return allies;
            
            var sourcePos = source.transform.position;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (BaseAI.IsEnemy(source, character)) continue;
                
                float dist = Vector3.Distance(sourcePos, character.transform.position);
                if (dist <= range)
                {
                    allies.Add(character);
                }
            }
            
            return allies;
        }
        
        private AbilityType GetAbilityType(string abilityName)
        {
            // Map ability names to types
            if (abilityName.Contains("Fortify") || abilityName.Contains("Shield") || abilityName.Contains("Protection"))
                return AbilityType.Defensive;
            if (abilityName.Contains("Rage") || abilityName.Contains("Smite") || abilityName.Contains("Strike"))
                return AbilityType.Offensive;
            if (abilityName.Contains("Heal") || abilityName.Contains("Sanctuary") || abilityName.Contains("Purify"))
                return AbilityType.Support;
            if (abilityName.Contains("Taunt") || abilityName.Contains("Mark"))
                return AbilityType.Utility;
            
            return AbilityType.Any;
        }
        
        #endregion
        
        #region Data Definitions
        
        private static List<ComboDefinition> _allCombos;
        private static List<SynergyBonus> _allSynergies;
        
        private static List<ComboDefinition> GetAllCombos()
        {
            if (_allCombos != null) return _allCombos;
            
            _allCombos = new List<ComboDefinition>
            {
                // Tank + Berserker: Coordinated Assault
                new ComboDefinition
                {
                    Name = "Coordinated Assault",
                    DisplayName = "Coordinated Assault",
                    Description = "Tank and Berserker combine for damage and protection!",
                    RequiredAbilities = new List<ComboRequirement>
                    {
                        new ComboRequirement { RequiredArchetype = ArchetypeClass.Tank, RequiredAbilityType = AbilityType.Defensive },
                        new ComboRequirement { RequiredArchetype = ArchetypeClass.Berserker, RequiredAbilityType = AbilityType.Offensive }
                    },
                    EffectDuration = CoordinatedAssaultEffectDuration
                },

                // Tank + Paladin: Holy Bastion
                new ComboDefinition
                {
                    Name = "Holy Bastion",
                    DisplayName = "Holy Bastion",
                    Description = "Divine protection shields the entire group!",
                    RequiredAbilities = new List<ComboRequirement>
                    {
                        new ComboRequirement { RequiredArchetype = ArchetypeClass.Tank },
                        new ComboRequirement { RequiredArchetype = ArchetypeClass.Paladin }
                    },
                    EffectDuration = HolyBastionEffectDuration
                },

                // Berserker + Mage: Primal Storm
                new ComboDefinition
                {
                    Name = "Primal Storm",
                    DisplayName = "Primal Storm",
                    Description = "Rage and magic combine into elemental devastation!",
                    RequiredAbilities = new List<ComboRequirement>
                    {
                        new ComboRequirement { RequiredArchetype = ArchetypeClass.Berserker },
                        new ComboRequirement { RequiredArchetype = ArchetypeClass.Mage }
                    },
                    EffectDuration = PrimalStormEffectDuration
                },

                // Rogue + Monk: Shadow Dance
                new ComboDefinition
                {
                    Name = "Shadow Dance",
                    DisplayName = "Shadow Dance",
                    Description = "Stealth and chi merge into lethal speed!",
                    RequiredAbilities = new List<ComboRequirement>
                    {
                        new ComboRequirement { RequiredArchetype = ArchetypeClass.Rogue },
                        new ComboRequirement { RequiredArchetype = ArchetypeClass.Monk }
                    },
                    EffectDuration = ShadowDanceEffectDuration
                },

                // Ranger + Healer: Nature's Fury
                new ComboDefinition
                {
                    Name = "Nature's Fury",
                    DisplayName = "Nature's Fury",
                    Description = "Nature's power heals allies and empowers attacks!",
                    RequiredAbilities = new List<ComboRequirement>
                    {
                        new ComboRequirement { RequiredArchetype = ArchetypeClass.Ranger },
                        new ComboRequirement { RequiredArchetype = ArchetypeClass.Healer }
                    },
                    EffectDuration = NaturesFuryEffectDuration
                },

                // Mage + Mage (double mage): Arcane Convergence
                new ComboDefinition
                {
                    Name = "Arcane Convergence",
                    DisplayName = "Arcane Convergence",
                    Description = "Multiple mages amplify each other's power!",
                    RequiredAbilities = new List<ComboRequirement>
                    {
                        new ComboRequirement { RequiredArchetype = ArchetypeClass.Mage },
                        new ComboRequirement { RequiredArchetype = ArchetypeClass.Mage }
                    },
                    EffectDuration = ArcaneConvergenceEffectDuration
                },

                // Paladin + Healer: Divine Harmony
                new ComboDefinition
                {
                    Name = "Divine Harmony",
                    DisplayName = "Divine Harmony",
                    Description = "Holy power and healing magic restore all allies!",
                    RequiredAbilities = new List<ComboRequirement>
                    {
                        new ComboRequirement { RequiredArchetype = ArchetypeClass.Paladin },
                        new ComboRequirement { RequiredArchetype = ArchetypeClass.Healer }
                    },
                    EffectDuration = DivineHarmonyEffectDuration
                },

                // Monk + Healer: Chi Resonance
                new ComboDefinition
                {
                    Name = "Chi Resonance",
                    DisplayName = "Chi Resonance",
                    Description = "Chi and healing energy resonate, restoring body and spirit!",
                    RequiredAbilities = new List<ComboRequirement>
                    {
                        new ComboRequirement { RequiredArchetype = ArchetypeClass.Monk },
                        new ComboRequirement { RequiredArchetype = ArchetypeClass.Healer }
                    },
                    EffectDuration = ChiResonanceEffectDuration
                }
            };

            return _allCombos;
        }
        
        private static List<SynergyBonus> GetAllPresenceSynergies()
        {
            if (_allSynergies != null) return _allSynergies;
            
            _allSynergies = new List<SynergyBonus>
            {
                // Balanced Party: Tank + DPS + Support
                new SynergyBonus
                {
                    Name = "Balanced Party",
                    DisplayName = "Balanced Party",
                    Description = "Having Tank, DPS, and Support archetypes grants all companions +5% damage and defense.",
                    RequiredArchetypes = new List<ArchetypeClass> { ArchetypeClass.Tank, ArchetypeClass.Healer },
                    DamageBonus = BalancedPartyDamageBonus,
                    DefenseBonus = BalancedPartyDefenseBonus
                },
                
                // Holy Vanguard: Tank + Paladin
                new SynergyBonus
                {
                    Name = "Holy Vanguard",
                    DisplayName = "Holy Vanguard",
                    Description = "Tank and Paladin together grant +10% defense to the group.",
                    RequiredArchetypes = new List<ArchetypeClass> { ArchetypeClass.Tank, ArchetypeClass.Paladin },
                    DefenseBonus = HolyVanguardDefenseBonus
                },

                // Fury Unleashed: Berserker + Rogue
                new SynergyBonus
                {
                    Name = "Fury Unleashed",
                    DisplayName = "Fury Unleashed",
                    Description = "Berserker and Rogue together grant +10% critical chance.",
                    RequiredArchetypes = new List<ArchetypeClass> { ArchetypeClass.Berserker, ArchetypeClass.Rogue },
                    CritBonus = FuryUnleashedCritBonus
                },
                
                // Arcane Brotherhood: Mage + Healer
                new SynergyBonus
                {
                    Name = "Arcane Brotherhood",
                    DisplayName = "Arcane Brotherhood",
                    Description = "Mage and Healer together grant +15% eitr regeneration.",
                    RequiredArchetypes = new List<ArchetypeClass> { ArchetypeClass.Mage, ArchetypeClass.Healer },
                    EitrRegenBonus = ArcaneBrotherhoodEitrRegenBonus
                },
                
                // Martial Mastery: Monk + Ranger
                new SynergyBonus
                {
                    Name = "Martial Mastery",
                    DisplayName = "Martial Mastery",
                    Description = "Monk and Ranger together grant +10% attack speed.",
                    RequiredArchetypes = new List<ArchetypeClass> { ArchetypeClass.Monk, ArchetypeClass.Ranger },
                    AttackSpeedBonus = MartialMasteryAttackSpeedBonus
                },
                
                // Divine Crusade: Paladin + Healer
                new SynergyBonus
                {
                    Name = "Divine Crusade",
                    DisplayName = "Divine Crusade",
                    Description = "Paladin and Healer together grant +20% healing received.",
                    RequiredArchetypes = new List<ArchetypeClass> { ArchetypeClass.Paladin, ArchetypeClass.Healer },
                    HealingBonus = DivineCrusadeHealingBonus
                }
            };
            
            return _allSynergies;
        }
        
        private SynergyBonus GetSynergyDefinition(string name)
        {
            return GetAllPresenceSynergies().FirstOrDefault(s => s.Name == name);
        }
        
        #endregion
    }
    
    #region Data Classes
    
    public enum AbilityType
    {
        Any,
        Offensive,
        Defensive,
        Support,
        Utility
    }
    
    public class ComboRequirement
    {
        public ArchetypeClass RequiredArchetype = ArchetypeClass.None;
        public ArchetypeClass RequiredSubArchetype = ArchetypeClass.None;
        public string RequiredAbilityName = "";
        public AbilityType RequiredAbilityType = AbilityType.Any;
    }
    
    public class ComboDefinition
    {
        public string Name;
        public string DisplayName;
        public string Description;
        public List<ComboRequirement> RequiredAbilities = new List<ComboRequirement>();
        public float EffectDuration = 10f;
    }
    
    public class SynergyBonus
    {
        public string Name;
        public string DisplayName;
        public string Description;
        public List<ArchetypeClass> RequiredArchetypes = new List<ArchetypeClass>();
        
        // Stat bonuses (multipliers, 1.0 = no change)
        public float DamageBonus = 1f;
        public float DefenseBonus = 1f;
        public float AttackSpeedBonus = 1f;
        public float CritBonus = 0f;
        public float HealingBonus = 1f;
        public float EitrRegenBonus = 1f;
        public float StaminaRegenBonus = 1f;
    }
    
    #endregion
}
