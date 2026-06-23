using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc;
using FiresCore.Npc.Archetypes.StatusEffects;
using FiresCore.Npc.Archetypes.StatusEffects.Hybrid;
using FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Tank;
using FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Paladin;
using FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Berserker;
using FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Rogue;
using FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Ranger;
using FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Mage;
using FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Healer;
using FiresCore.Npc.Archetypes.StatusEffects.Hybrid.Monk;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// Manages special abilities for hybrid archetypes.
    /// Each hybrid combination has a unique ability that combines aspects of both archetypes.
    /// 
    /// INTEGRATION:
    /// - Works alongside ArchetypeAbilitySystem
    /// - Triggered when companion has both main and sub archetype
    /// - Uses longer cooldowns than base abilities (special = more powerful, less frequent)
    /// 
    /// ABILITY REGISTRATION:
    /// Abilities must be registered in StatusEffectManager with icons for HUD display.
    /// </summary>
    public class HybridAbilityManager : MonoBehaviour
    {
        [Header("Cooldowns")]
        public float hybridAbilityCooldown = 60f; // Base cooldown - modified by hybrid definition
        
        [Header("Trigger Thresholds")]
        public float emergencyThreshold = 0.25f;
        public float groupSupportThreshold = 0.5f;
        
        // Components
        private CompanionController _companion;
        private ArchetypeController _archetypeController;
        private AI.CompanionAI _ai;
        private Character _character;
        
        // State
        private float _lastHybridAbilityTime = -100f;
        private bool _hasHybrid;
        private HybridArchetypeDefinitions.HybridDefinition _hybridDef;
        
        public static bool VerboseLogging = false;
        
        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _archetypeController = GetComponent<ArchetypeController>();
            _ai = GetComponent<AI.CompanionAI>();
            _character = GetComponent<Character>();
        }
        
        private void Start()
        {
            // Check if we have a valid hybrid combination
            RefreshHybridDefinition();
        }
        
        /// <summary>
        /// Refreshes the hybrid definition based on current archetypes.
        /// Call this when archetype changes.
        /// </summary>
        public void RefreshHybridDefinition()
        {
            if (_archetypeController == null)
            {
                _hasHybrid = false;
                _hybridDef = null;
                return;
            }
            
            var main = _archetypeController.CurrentArchetypeClass;
            var sub = _archetypeController.SubArchetypeClass;
            
            if (main == ArchetypeClass.None || sub == ArchetypeClass.None || main == sub)
            {
                _hasHybrid = false;
                _hybridDef = null;
                return;
            }
            
            _hybridDef = HybridArchetypeDefinitions.GetHybrid(main, sub);
            _hasHybrid = _hybridDef != null;
            
            if (_hasHybrid)
            {
                hybridAbilityCooldown = _hybridDef.SpecialAbilityCooldown;
                
                if (VerboseLogging)
                {
                    Debug.Log($"[HybridAbility] {_companion?.companionName} has hybrid: {_hybridDef.DisplayName} ({main}/{sub})");
                }
            }
        }
        
        private void Update()
        {
            if (!_hasHybrid || _hybridDef == null) return;
            if (_companion == null || !_companion.isTamed) return;
            if (_character == null || _character.IsDead()) return;
            if (_ai == null || !_ai.IsInCombat) return;
            // Suppress during local player respawn / loading screen.
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;
            
            // LEVEL GATE: Hybrid abilities require level 30+
            var progression = GetComponent<CompanionProgression>();
            int level = progression?.Level ?? 1;
            if (level < AbilityUnlockSystem.HYBRID_ABILITY_LEVEL)
            {
                return;
            }
            
            // Check if we can use hybrid ability
            if (!CanUseHybridAbility()) return;
            
            // Check trigger conditions based on hybrid type
            if (ShouldUseHybridAbility())
            {
                UseHybridAbility();
            }
        }
        
        private bool CanUseHybridAbility()
        {
            return Time.time - _lastHybridAbilityTime >= hybridAbilityCooldown;
        }
        
        private bool ShouldUseHybridAbility()
        {
            if (_hybridDef == null) return false;
            
            var main = _archetypeController.CurrentArchetypeClass;
            var sub = _archetypeController.SubArchetypeClass;
            
            // Different triggers based on hybrid role
            switch (main)
            {
                case ArchetypeClass.Tank:
                    // Tank hybrids trigger on low health or multiple enemies
                    return _character.GetHealthPercentage() < 0.4f || CountNearbyEnemies(8f) >= 3;
                    
                case ArchetypeClass.Paladin:
                    // Paladin hybrids trigger when allies are hurt
                    return HasHurtAlliesNearby() || _character.GetHealthPercentage() < 0.5f;
                    
                case ArchetypeClass.Berserker:
                    // Berserker hybrids trigger at low health or high damage opportunity
                    return _character.GetHealthPercentage() < 0.35f || CountNearbyEnemies(6f) >= 2;
                    
                case ArchetypeClass.Rogue:
                    // Rogue hybrids trigger when not being targeted
                    return !IsBeingTargeted() || _character.GetHealthPercentage() < 0.3f;
                    
                case ArchetypeClass.Ranger:
                    // Ranger hybrids trigger at range or when marking is beneficial
                    return HasEnemyInRange(15f) && !HasEnemyInRange(5f);
                    
                case ArchetypeClass.Mage:
                    // Mage hybrids trigger when multiple enemies are in range
                    return CountNearbyEnemies(10f) >= 2;
                    
                case ArchetypeClass.Healer:
                    // Healer hybrids trigger on critical ally health
                    return HasCriticalAlly() || AlliesHaveDebuffs();
                    
                case ArchetypeClass.Monk:
                    // Monk hybrids trigger in melee with good stamina
                    return HasEnemyInRange(4f) && GetStaminaPercentage() > 0.5f;
            }
            
            return false;
        }
        
        private void UseHybridAbility()
        {
            _lastHybridAbilityTime = Time.time;
            
            var main = _archetypeController.CurrentArchetypeClass;
            var sub = _archetypeController.SubArchetypeClass;
            
            bool success = ApplyHybridEffect(main, sub);
            
            if (success)
            {
                // Announce
                ArchetypeChatManager.AnnounceAbilityUsed(_companion, _hybridDef.SpecialAbilityName);
                
                // Notify synergy manager
                GroupSynergyManager.Instance?.OnAbilityUsed(_companion, _hybridDef.SpecialAbilityName);
                
                Debug.Log($"[HybridAbility] {_companion.companionName} used {_hybridDef.SpecialAbilityName}!");
            }
        }
        
        private bool ApplyHybridEffect(ArchetypeClass main, ArchetypeClass sub)
        {
            float duration = 12f; // Default duration
            
            // Apply the specific hybrid ability
            switch (main)
            {
                case ArchetypeClass.Tank:
                    return ApplyTankHybridAbility(sub, duration);
                    
                case ArchetypeClass.Paladin:
                    return ApplyPaladinHybridAbility(sub, duration);
                    
                case ArchetypeClass.Berserker:
                    return ApplyBerserkerHybridAbility(sub, duration);
                    
                case ArchetypeClass.Rogue:
                    return ApplyRogueHybridAbility(sub, duration);
                    
                case ArchetypeClass.Ranger:
                    return ApplyRangerHybridAbility(sub, duration);
                    
                case ArchetypeClass.Mage:
                    return ApplyMageHybridAbility(sub, duration);
                    
                case ArchetypeClass.Healer:
                    return ApplyHealerHybridAbility(sub, duration);
                    
                case ArchetypeClass.Monk:
                    return ApplyMonkHybridAbility(sub, duration);
            }
            
            return false;
        }
        
        #region Tank Hybrid Abilities
        
        private bool ApplyTankHybridAbility(ArchetypeClass sub, float duration)
        {
            switch (sub)
            {
                case ArchetypeClass.Paladin: // Crusader - Divine Bulwark
                    return CrusaderEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Berserker: // Juggernaut - Unstoppable Charge
                    return JuggernautEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Rogue: // Shadow Guardian - Counter Shadow
                    return ShadowGuardianEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Ranger: // Warden - Guardian's Volley
                    return WardenEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Mage: // Spellbreaker - Arcane Fortress
                    return SpellbreakerEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Healer: // Bastion - Protective Aura
                    return BastionEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Monk: // Iron Monk - Iron Stance
                    return IronMonkEffect.Apply(_character, duration);
            }
            
            return false;
        }
        
        #endregion
        
        #region Paladin Hybrid Abilities
        
        private bool ApplyPaladinHybridAbility(ArchetypeClass sub, float duration)
        {
            switch (sub)
            {
                case ArchetypeClass.Tank: // Templar - Sacred Shield
                    return TemplarEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Berserker: // Zealot - Holy Fury
                    return ZealotEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Rogue: // Inquisitor - Divine Judgment
                    return InquisitorEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Ranger: // Holy Archer - Radiant Arrow
                    return HolyArcherEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Mage: // Battle Priest - Divine Nova
                    return BattlePriestEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Healer: // High Priest - Mass Restoration
                    return HighPriestEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Monk: // Ascetic Knight - Enlightened Strike
                    return AsceticKnightEffect.Apply(_character, duration);
            }
            
            return false;
        }
        
        #endregion
        
        #region Berserker Hybrid Abilities
        
        private bool ApplyBerserkerHybridAbility(ArchetypeClass sub, float duration)
        {
            switch (sub)
            {
                case ArchetypeClass.Tank: // Ravager - Bloodthirst
                    return RavagerEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Paladin: // Avenger - Wrathful Smite
                    return AvengerEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Rogue: // Reaper - Frenzy Strike
                    return ReaperEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Ranger: // Beast Hunter - Predator's Mark
                    return BeastHunterEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Mage: // Blood Mage - Blood Sacrifice
                    return BloodMageEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Healer: // Blood Knight - Sanguine Fury
                    return BloodKnightEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Monk: // Battle Rager - Focused Fury
                    return BattleRagerEffect.Apply(_character, duration);
            }
            
            return false;
        }
        
        #endregion
        
        #region Rogue Hybrid Abilities
        
        private bool ApplyRogueHybridAbility(ArchetypeClass sub, float duration)
        {
            switch (sub)
            {
                case ArchetypeClass.Tank: // Duelist - Perfect Riposte
                    return DuelistEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Paladin: // Shadow Priest - Holy Assassination
                    return ShadowPriestEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Berserker: // Blade Dancer - Dance of Death
                    return BladeDancerEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Ranger: // Scout - Ambush
                    return ScoutEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Mage: // Spellthief - Mana Drain
                    return SpellthiefEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Healer: // Medicine Man - Life Steal
                    return MedicineManEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Monk: // Shadow Monk/Ninja - Shadow Step
                    return NinjaEffect.Apply(_character, duration);
            }
            
            return false;
        }
        
        #endregion
        
        #region Ranger Hybrid Abilities
        
        private bool ApplyRangerHybridAbility(ArchetypeClass sub, float duration)
        {
            switch (sub)
            {
                case ArchetypeClass.Tank: // Sentinel - Defensive Volley
                    return SentinelEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Paladin: // Divine Hunter - Blessed Arrows
                    return DivineHunterEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Berserker: // Wild Hunter - Rain of Fury
                    return WildHunterEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Rogue: // Sniper - Assassin's Shot
                    return SniperEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Mage: // Arcane Archer - Elemental Arrow
                    return ArcaneArcherEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Healer: // Forest Keeper - Nature's Blessing
                    return ForestKeeperEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Monk: // Zen Archer - Perfect Shot
                    return ZenArcherEffect.Apply(_character, duration);
            }
            
            return false;
        }
        
        #endregion
        
        #region Mage Hybrid Abilities
        
        private bool ApplyMageHybridAbility(ArchetypeClass sub, float duration)
        {
            switch (sub)
            {
                case ArchetypeClass.Tank: // Battlemage - Arcane Armor
                    return BattlemageEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Paladin: // Hierophant - Divine Wrath
                    return HierophantEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Berserker: // Pyromancer - Inferno
                    return PyromancerEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Rogue: // Shadowcaster - Shadow Clone
                    return ShadowcasterEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Ranger: // Storm Caller - Chain Lightning
                    return StormCallerEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Healer: // Sage - Balance
                    return SageEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Monk: // Mystic - Arcane Meditation
                    return MysticEffect.Apply(_character, duration);
            }
            
            return false;
        }
        
        #endregion
        
        #region Healer Hybrid Abilities
        
        private bool ApplyHealerHybridAbility(ArchetypeClass sub, float duration)
        {
            switch (sub)
            {
                case ArchetypeClass.Tank: // War Cleric - Divine Protection
                    return WarClericEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Paladin: // Oracle - Divine Foresight
                    return OracleEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Berserker: // Pain Shaman - Blood Pact
                    return PainShamanEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Rogue: // Shadow Healer - Phantom Touch
                    return ShadowHealerEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Ranger: // Nature Priest - Regrowth
                    return NaturePriestEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Mage: // Arcane Healer - Mana Transfusion
                    return ArcaneHealerEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Monk: // Chi Master - Chi Restoration
                    return ChiMasterEffect.Apply(_character, duration);
            }
            
            return false;
        }
        
        #endregion
        
        #region Monk Hybrid Abilities
        
        private bool ApplyMonkHybridAbility(ArchetypeClass sub, float duration)
        {
            switch (sub)
            {
                case ArchetypeClass.Tank: // Stone Fist - Stone Stance
                    return StoneFistEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Paladin: // Temple Guardian - Sacred Fist
                    return TempleGuardianEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Berserker: // Drunken Master - Drunken Frenzy
                    return DrunkenMasterEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Rogue: // Ninja - Vanishing Strike
                    return NinjaMonkEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Ranger: // Wind Walker - Wind Step
                    return WindWalkerEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Mage: // Elementalist - Elemental Fist
                    return ElementalistEffect.Apply(_character, duration);
                    
                case ArchetypeClass.Healer: // Chi Healer - Healing Palm
                    return ChiHealerEffect.Apply(_character, duration);
            }
            
            return false;
        }
        
        #endregion
        
        #region Helper Methods
        
        private int CountNearbyEnemies(float range)
        {
            int count = 0;
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (!BaseAI.IsEnemy(_character, character)) continue;
                
                float dist = Vector3.Distance(_character.transform.position, character.transform.position);
                if (dist <= range) count++;
            }
            return count;
        }
        
        private bool HasEnemyInRange(float range)
        {
            return CountNearbyEnemies(range) > 0;
        }
        
        private bool HasHurtAlliesNearby()
        {
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                if (BaseAI.IsEnemy(_character, character)) continue;
                
                float dist = Vector3.Distance(_character.transform.position, character.transform.position);
                if (dist <= 15f && character.GetHealthPercentage() < groupSupportThreshold)
                {
                    return true;
                }
            }
            return false;
        }
        
        private bool HasCriticalAlly()
        {
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (BaseAI.IsEnemy(_character, character)) continue;
                
                float dist = Vector3.Distance(_character.transform.position, character.transform.position);
                if (dist <= 15f && character.GetHealthPercentage() < emergencyThreshold)
                {
                    return true;
                }
            }
            return false;
        }
        
        private bool IsBeingTargeted()
        {
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (!BaseAI.IsEnemy(_character, character)) continue;
                
                var ai = character.GetComponent<BaseAI>();
                if (ai != null && ai.GetTargetCreature() == _character)
                {
                    return true;
                }
            }
            return false;
        }
        
        private Character GetCurrentTarget()
        {
            return _ai?.GetTargetCreature();
        }
        
        private float GetStaminaPercentage()
        {
            if (_character == null) return 0f;
            float max = _character.GetMaxStamina();
            if (max <= 0) return 0f;
            // For companions (non-players), we can check if they have stamina via HaveStamina()
            // Since we can't directly access m_stamina on non-player characters,
            // we assume good stamina if they can perform actions
            if (_character is Player player)
            {
                // Players have direct stamina access via reflection or we estimate
                return player.HaveStamina() ? 0.7f : 0.2f;
            }
            // For NPCs/companions, use HaveStamina as a binary check
            return _character.HaveStamina() ? 0.8f : 0.3f;
        }
        
        private bool AlliesHaveDebuffs()
        {
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                if (BaseAI.IsEnemy(_character, character)) continue;
                
                float dist = Vector3.Distance(_character.transform.position, character.transform.position);
                if (dist <= 15f)
                {
                    var seman = character.GetSEMan();
                    if (seman != null)
                    {
                        string[] debuffs = { "Poison", "Burning", "Frost", "Wet", "Cold", "Freezing" };
                        foreach (var debuff in debuffs)
                        {
                            if (seman.HaveStatusEffect(debuff.GetStableHashCode())) return true;
                        }
                    }
                }
            }
            return false;
        }
        
        private void HealAlliesInRange(float amount, float range)
        {
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (BaseAI.IsEnemy(_character, character)) continue;
                
                float dist = Vector3.Distance(_character.transform.position, character.transform.position);
                if (dist <= range)
                {
                    character.Heal(amount, true);
                    AbilityFXManager.SpawnEffect("fx_creature_tamed", character.transform.position, null, 0.4f);
                }
            }
        }
        
        #endregion
    }
}
