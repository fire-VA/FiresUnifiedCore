using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc;
using FiresCore.Npc.Archetypes.StatusEffects;
using FiresCore.Npc.Archetypes.StatusEffects.Expert;
using FiresCore.Npc.Archetypes.StatusEffects.Master;
using FiresCore.Npc.Archetypes.StatusEffects.Ultimate;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// Triggers archetype abilities for companions: each archetype has a self buff and a group or attack
    /// ability, unlocked by companion level (see AbilityUnlockSystem). Abilities sync through AbilityRPCManager
    /// and play their effects through AbilityFXManager so every client sees the same thing.
    /// </summary>
    public class ArchetypeAbilitySystem : MonoBehaviour
    {
        private const float CombatAssessmentRange = 20f;
        private const float AllySupportRange = 15f;
        private const int EnemyCrowdSize = 3;

        private const float ImmortalStanceHealthThreshold = 0.15f;
        private const float ImmortalStanceRange = 10f;
        private const float ImmortalStanceDuration = 5f;
        private const float FortifyDuration = 10f;

        private const float LayOnHandsHealthThreshold = 0.15f;
        private const float DivineShieldHealthThreshold = 0.2f;
        private const float DivineShieldDuration = 3f;
        private const float ConsecrationRange = 5f;
        private const float ConsecrationDuration = 10f;
        private const float HolySmiteHealthThreshold = 0.6f;
        private const float HolySmiteEngageRange = 5f;
        private const float HolySmiteDuration = 15f;
        private const float DivineProtectionRange = 12f;
        private const float DivineProtectionDuration = 12f;
        private const float AvatarOfLightDuration = 30f;

        private const float AvatarOfWarHealthThreshold = 0.3f;
        private const float AvatarOfWarRange = 10f;
        private const float AvatarOfWarDuration = 20f;
        private const float DeathWishHealthThreshold = 0.10f;
        private const float DeathWishDuration = 60f;
        private const float BerserkRageHealthThreshold = 0.35f;
        private const float BerserkRageDuration = 12f;
        private const float WarcryCombatStartWindow = 5f;
        private const float WarcryRange = 15f;
        private const float WarcryDuration = 15f;

        private const float ShadowDanceHealthThreshold = 0.25f;
        private const float ShadowDanceRange = 8f;
        private const float ShadowDanceDuration = 10f;
        private const float DeathMarkDuration = 30f;
        private const float EvasionDuration = 8f;
        private const float StealthDuration = 12f;
        private const float PoisonDuration = 8f;

        private const float WayOfPerfectionRange = 5f;
        private const float WayOfPerfectionDuration = 15f;
        private const float ChiExplosionRange = 6f;
        private const float IronBodyHealthThreshold = 0.4f;
        private const float IronBodyDuration = 10f;
        private const float MonkEngageRange = 3f;
        private const float FlurryOfBlowsDuration = 5f;
        private const float ChiStrikeDuration = 15f;
        private const float InnerPeaceDuration = 8f;

        private const float PerfectShotRange = 30f;
        private const float PerfectShotDuration = 15f;
        private const float RangerCloseQuartersRange = 5f;
        private const float RainOfArrowsRange = 15f;
        private const float RainOfArrowsDuration = 5f;
        private const float MultishotRange = 15f;
        private const float MultishotDuration = 15f;
        private const float EagleEyeRange = 20f;
        private const float EagleEyeDuration = 15f;
        private const float HuntersMarkDuration = 20f;

        private const float ArcaneFormRange = 15f;
        private const float ArcaneFormDuration = 15f;
        private const float MeteorRange = 10f;
        private const float MeteorDuration = 3f;
        private const float OverchargeRange = 15f;
        private const float OverchargeDuration = 20f;
        private const float ElementalInfusionDuration = 20f;
        private const float ArcaneShieldHealthThreshold = 0.6f;
        private const float ArcaneShieldDuration = 15f;

        private const int HealerStatusLogIntervalFrames = 300;
        private const float AvatarOfLifeHealthThreshold = 0.3f;
        private const float AvatarOfLifeDuration = 20f;
        private const float DivineHymnDuration = 8f;
        private const float ResurrectionDuration = 3f;
        private const float EmergencySanctuaryRange = 15f;
        private const float EmergencySanctuaryDuration = 12f;
        private const float TankSupportHealthThreshold = 0.70f;
        private const float PurifyHealthThreshold = 0.7f;
        private const float PurifyDuration = 10f;
        private const float SanctuaryCombatStartWindow = 10f;
        private const float SanctuaryRange = 10f;
        private const float SanctuaryDuration = 10f;
        private const float PurifyingCircleRange = 10f;
        private const float PurifyingCircleDuration = 12f;

        private const int BossMaxHealthThreshold = 5000;
        private const int HighThreatMaxHealthThreshold = 1000;
        private const int MediumThreatMaxHealthThreshold = 200;
        private const int TrivialThreatMaxHealthThreshold = 100;
        private const int ThreatEscalationEnemyCount = 3;
        private const float LongCooldownSeconds = 30f;
        private const float VeryLongCooldownSeconds = 60f;
        private const int LongCooldownMinEnemies = 4;
        private const int VeryLongCooldownMinEnemies = 5;

        [Header("Cooldowns (seconds)")]
        public float selfBuffCooldown = 20f; // Reduced from 30s for more active ability usage
        public float groupAbilityCooldown = 30f; // Reduced from 45s for more group support
        public float passiveAbilityCooldown = 8f; // Reduced from 10s for more marking/utility
        
        [Header("Expert/Master/Ultimate Cooldowns")]
        public float expertAbilityCooldown = 45f; // Level 35-50 abilities
        public float masterAbilityCooldown = 120f; // Level 75 abilities (2 min)
        public float ultimateAbilityCooldown = 300f; // Level 100 abilities (5 min)
        
        [Header("Trigger Thresholds")]
        public float lowHealthThreshold = 0.4f;
        public float allyLowHealthThreshold = 0.5f;
        public float combatStartDelay = 2f;
        
        // Components
        private CompanionController _companion;
        private ArchetypeController _archetypeController;
        private CompanionProgression _progression;
        private AI.CompanionAI _ai;
        private CompanionCombat _combat;
        private Character _character;
        private ArchetypeSkillSystem _skillSystem;
        private ZNetView _nview;

        // Cooldown tracking
        private float _lastSelfBuffTime = -100f;
        private float _lastGroupAbilityTime = -100f;
        private float _lastPassiveAbilityTime = -100f;
        private float _combatEntryTime;
        private bool _inCombat;
        
        // Expert/Master/Ultimate cooldown tracking (per archetype)
        private float _lastExpertAbilityTime = -1000f;
        private float _lastMasterAbilityTime = -1000f;
        private float _lastUltimateAbilityTime = -1000f;
        
        public static bool VerboseLogging = false;
        
        /// <summary>Gets the companion's current level for ability unlock checks.</summary>
        private int CompanionLevel => _progression?.Level ?? 1;
        
        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _archetypeController = GetComponent<ArchetypeController>();
            _progression = GetComponent<CompanionProgression>();
            _ai = GetComponent<AI.CompanionAI>();
            _combat = GetComponent<CompanionCombat>();
            _character = GetComponent<Character>();
            _skillSystem = GetComponent<ArchetypeSkillSystem>();
            _nview = GetComponent<ZNetView>();
        }
        
        private void Update()
        {
            if (_companion == null || !_companion.isTamed) return;
            if (_archetypeController == null) return;
            if (_character == null || _character.IsDead()) return;
            // Pause ability ticking during the local player's respawn / loading-screen
            // window. The RPC broadcasts these abilities trigger have been observed
            // to deadlock the zone stream (see CompanionPatches.cs). Cooldowns aren't
            // consumed while suppressed — abilities resume cleanly post-respawn.
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;
            // Only the owner decides: CompanionAI.ForceTarget can put a non-owner copy into combat.
            if (_nview == null || !_nview.IsValid() || !_nview.IsOwner()) return;

            var currentArchetype = _archetypeController.CurrentArchetypeClass;
            if (currentArchetype != _passiveArchetype)
                DropOtherArchetypePassives(currentArchetype);

            // Check if we're in combat
            bool wasInCombat = _inCombat;
            _inCombat = _ai != null && _ai.IsInCombat;
            
            // Track combat entry time
            if (_inCombat && !wasInCombat)
            {
                _combatEntryTime = Time.time;
                
                if (VerboseLogging)
                {
                    Debug.Log($"[ArchetypeAbility] {_companion.companionName} entered combat as {_archetypeController.CurrentArchetypeClass}");
                }
            }
            
            // Only use abilities in combat after initial delay
            if (!_inCombat) return;
            if (Time.time - _combatEntryTime < combatStartDelay) return;

            // CRITICAL ENEMY-PRESENCE GATE
            // ----------------------------
            // _inCombat reflects this companion's AI state machine, which can
            // get stuck on Combat after the actual enemies are dead/distant —
            // and per-ability triggers like UseElementalInfusion / UseWarcry
            // historically only checked cooldowns, not proximity. The result
            // was companions standing in the player's base spamming buff FX
            // because some sibling companion 100m away was still chasing a
            // hog. Require a live enemy within AbilityUseRange of THIS
            // companion before any ability can tick. Cheap, central, and
            // matches the spirit of every per-ability HasEnemyInRange check.
            if (!HasEnemyInRange(AbilityUseRange)) return;

            // Check and use abilities based on archetype
            UpdateAbilities();
        }

        // Single source of truth for "is this companion close enough to any
        // enemy to justify burning an ability cooldown?". 25 m is roughly the
        // longest engagement distance any ability uses (Ranger's Eagle Eye is
        // 20 m, Perfect Shot is 30 m but is also gated separately).
        private const float AbilityUseRange = 25f;
        
        /// <summary>
        /// Main ability update - checks conditions and triggers appropriate abilities.
        /// </summary>
        private void UpdateAbilities()
        {
            var archetype = _archetypeController.CurrentArchetypeClass;
            
            switch (archetype)
            {
                case ArchetypeClass.Tank:
                    UpdateTankAbilities();
                    break;
                case ArchetypeClass.Paladin:
                    UpdatePaladinAbilities();
                    break;
                case ArchetypeClass.Berserker:
                    UpdateBerserkerAbilities();
                    break;
                case ArchetypeClass.Rogue:
                    UpdateRogueAbilities();
                    break;
                case ArchetypeClass.Monk:
                    UpdateMonkAbilities();
                    break;
                case ArchetypeClass.Ranger:
                    UpdateRangerAbilities();
                    break;
                case ArchetypeClass.Mage:
                    UpdateMageAbilities();
                    break;
                case ArchetypeClass.Healer:
                    UpdateHealerAbilities();
                    break;
            }
        }
        
        #region Tank Abilities
        
        private void UpdateTankAbilities()
        {
            var archetype = ArchetypeClass.Tank;
            int level = CompanionLevel;
            float healthPercent = _character.GetHealthPercentage();
            
            // ULTIMATE (L100): Immortal Stance - when critically low and surrounded
            if (AbilityUnlockSystem.IsAbilityUnlocked("tank_immortal_stance", archetype, level))
            {
                if (CanUseUltimateAbility() && healthPercent < ImmortalStanceHealthThreshold && CountEnemiesInRange(ImmortalStanceRange) >= EnemyCrowdSize)
                {
                    UseImmortalStance();
                    return;
                }
            }
            
            if (AbilityUnlockSystem.IsAbilityUnlocked("tank_unyielding", archetype, level))
                EnsurePassive(StatusEffectManager.EFFECT_UNYIELDING, "unyielding");

            if (AbilityUnlockSystem.IsAbilityUnlocked("tank_iron_wall", archetype, level))
                EnsurePassive(StatusEffectManager.EFFECT_IRON_WALL, "ironwall");
            
            // Self buff: Fortify when taking heavy damage (unlocks at level 10)
            if (AbilityUnlockSystem.IsAbilityUnlocked("tank_fortify", archetype, level))
            {
                if (CanUseSelfBuff() && healthPercent < lowHealthThreshold)
                {
                    UseFortify();
                }
            }
            
            // Taunt is handled by ArchetypeController (unlocks at level 5)
        }
        
        private void UseFortify()
        {
            _lastSelfBuffTime = Time.time;
            
            // Use RPC manager for multiplayer sync
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_FORTIFY, FortifyDuration);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("fortify");
            
            // Announce via chat bubble
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "Fortify");
            
            // Notify synergy manager
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "Fortify");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} used FORTIFY!");
        }
        
        private void UseImmortalStance()
        {
            _lastUltimateAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_IMMORTAL_STANCE, ImmortalStanceDuration);

            // Grant skill XP + bonus for nearby enemies
            _skillSystem?.OnAbilityUsed("immortalstance");
            int nearbyEnemies = CountEnemiesInRange(ImmortalStanceRange);
            for (int i = 0; i < nearbyEnemies; i++)
            {
                _skillSystem?.OnAbilityHitEnemy("immortalstance", null, false);
            }
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "ImmortalStance");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "ImmortalStance");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} activated IMMORTAL STANCE!");
        }
        
        #endregion
        
        #region Paladin Abilities
        
        private void UpdatePaladinAbilities()
        {
            var archetype = ArchetypeClass.Paladin;
            int level = CompanionLevel;
            float healthPercent = _character.GetHealthPercentage();
            
            // SMART CHECK: Evaluate if abilities are worth using
            int activeEnemies = CountEnemiesInRange(CombatAssessmentRange);
            bool combatEffectivelyOver = activeEnemies == 0;
            var currentTarget = GetCurrentTarget();
            var threatLevel = currentTarget != null ? EvaluateThreatLevel(currentTarget) : ThreatLevel.Trivial;
            
            // ULTIMATE (L100): Avatar of Light - when multiple allies are hurt
            if (AbilityUnlockSystem.IsAbilityUnlocked("paladin_avatar", archetype, level))
            {
                if (CanUseUltimateAbility() && CountHurtAlliesNearby() >= 2)
                {
                    UseAvatarOfLight();
                    return;
                }
            }
            
            // MASTER (L75): Lay on Hands - emergency full heal for critically low ally
            if (AbilityUnlockSystem.IsAbilityUnlocked("paladin_lay_hands", archetype, level))
            {
                if (CanUseMasterAbility())
                {
                    var criticalAlly = FindCriticalAlly(LayOnHandsHealthThreshold);
                    if (criticalAlly != null)
                    {
                        UseLayOnHands(criticalAlly);
                        return;
                    }
                }
            }
            
            // EXPERT (L50): Divine Shield - emergency immunity when critically low
            if (AbilityUnlockSystem.IsAbilityUnlocked("paladin_divine_shield", archetype, level))
            {
                if (CanUseExpertAbility() && healthPercent < DivineShieldHealthThreshold)
                {
                    UseDivineShield();
                    return;
                }
            }
            
            // EXPERT (L35): Consecration - ground AoE when surrounded by SIGNIFICANT enemies
            if (AbilityUnlockSystem.IsAbilityUnlocked("paladin_consecration", archetype, level))
            {
                int nearbyEnemies = CountEnemiesInRange(ConsecrationRange);
                // Only use consecration if there are multiple enemies OR a significant threat
                if (CanUseExpertAbility() && (nearbyEnemies >= EnemyCrowdSize || (nearbyEnemies >= 2 && threatLevel >= ThreatLevel.Medium)))
                {
                    UseConsecration();
                    return;
                }
            }
            
            // Self buff: Holy Smite when engaging WORTHY targets (not trivial enemies)
            if (AbilityUnlockSystem.IsAbilityUnlocked("paladin_smite", archetype, level))
            {
                // Only use Holy Smite if: multiple enemies OR medium+ threat OR we're hurt
                bool worthUsingSmite = activeEnemies >= EnemyCrowdSize || threatLevel >= ThreatLevel.Medium || healthPercent < HolySmiteHealthThreshold;
                if (CanUseSelfBuff() && HasEnemyInRange(HolySmiteEngageRange) && !combatEffectivelyOver && worthUsingSmite)
                {
                    UseHolySmite();
                }
            }
            
            // Group ability: Divine Protection when MULTIPLE allies are hurt (not just one)
            if (AbilityUnlockSystem.IsAbilityUnlocked("paladin_protection", archetype, level))
            {
                int hurtAllies = CountHurtAlliesNearby();
                // Only use Divine Protection if 2+ allies hurt, or significant combat ongoing
                bool worthUsingProtection = hurtAllies >= 2 || (hurtAllies >= 1 && threatLevel >= ThreatLevel.High);
                if (CanUseGroupAbility() && worthUsingProtection)
                {
                    UseDivineProtection();
                }
            }
        }
        
        private void UseHolySmite()
        {
            _lastSelfBuffTime = Time.time;
            
            // Use RPC manager for multiplayer sync
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_HOLY_SMITE, HolySmiteDuration);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("holysmite");
            
            // Announce via chat bubble
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "HolySmite");
            
            // Notify synergy manager
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "HolySmite");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} used HOLY SMITE!");
        }
        
        private void UseDivineProtection()
        {
            _lastGroupAbilityTime = Time.time;
            
            // Use RPC manager for multiplayer sync - applies to all allies in range
            int count = AbilityRPCManager.ApplyGroupBuff(_character, StatusEffectManager.EFFECT_DIVINE_PROTECTION, DivineProtectionRange, DivineProtectionDuration);
            
            // Grant skill XP + bonus for each ally protected
            _skillSystem?.OnAbilityUsed("divineprotection");
            for (int i = 0; i < count; i++)
            {
                _skillSystem?.OnAbilityBuffedAlly("divineprotection", null);
            }
            
            // Announce via chat bubble
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "DivineProtection", count);
            
            // Notify synergy manager
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "DivineProtection");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} used DIVINE PROTECTION on {count} allies!");
        }
        
        private void UseAvatarOfLight()
        {
            _lastUltimateAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_AVATAR_OF_LIGHT, AvatarOfLightDuration);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("avataroflight");
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "AvatarOfLight");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "AvatarOfLight");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} became AVATAR OF LIGHT!");
        }
        
        private void UseLayOnHands(Character target)
        {
            _lastMasterAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySingleEffect(_character, target, StatusEffectManager.EFFECT_LAY_ON_HANDS, 1f);
            
            // Grant skill XP + bonus for saving ally
            _skillSystem?.OnAbilityUsed("layonhands");
            _skillSystem?.OnAbilityBuffedAlly("layonhands", target);
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "LayOnHands");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "LayOnHands");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} used LAY ON HANDS on {target.m_name}!");
        }
        
        private void UseDivineShield()
        {
            _lastExpertAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_DIVINE_SHIELD, DivineShieldDuration);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("divineshield");
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "DivineShield");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "DivineShield");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} activated DIVINE SHIELD!");
        }
        
        private void UseConsecration()
        {
            _lastExpertAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_CONSECRATION, ConsecrationDuration);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("consecration");
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "Consecration");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "Consecration");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} created CONSECRATION!");
        }
        
        #endregion
        
        #region Berserker Abilities
        
        private void UpdateBerserkerAbilities()
        {
            var archetype = ArchetypeClass.Berserker;
            int level = CompanionLevel;
            float healthPercent = _character.GetHealthPercentage();
            
            // ULTIMATE (L100): Avatar of War - when low health and in heavy combat
            if (AbilityUnlockSystem.IsAbilityUnlocked("berserker_avatar_war", archetype, level))
            {
                if (CanUseUltimateAbility() && healthPercent < 0.3f && CountEnemiesInRange(10f) >= 2)
                {
                    UseAvatarOfWar();
                    return;
                }
            }
            
            if (AbilityUnlockSystem.IsAbilityUnlocked("berserker_deathwish", archetype, level))
                EnsurePassive(StatusEffectManager.EFFECT_DEATH_WISH, "deathwish");

            if (AbilityUnlockSystem.IsAbilityUnlocked("berserker_rampage", archetype, level))
                EnsurePassive(StatusEffectManager.EFFECT_RAMPAGE, "rampage");

            if (AbilityUnlockSystem.IsAbilityUnlocked("berserker_execute", archetype, level))
                EnsurePassive(StatusEffectManager.EFFECT_EXECUTE, "execute");
            
            // Self buff: Berserk Rage when low health (unlocks at level 10)
            if (AbilityUnlockSystem.IsAbilityUnlocked("berserker_rage", archetype, level))
            {
                if (CanUseSelfBuff() && healthPercent < 0.35f)
                {
                    UseBerserkRage();
                }
            }
            
            // Group ability: Warcry at combat start or when allies need boost (unlocks at level 5)
            if (AbilityUnlockSystem.IsAbilityUnlocked("berserker_warcry", archetype, level))
            {
                if (CanUseGroupAbility() && (Time.time - _combatEntryTime < 5f || HasAlliesInCombat()))
                {
                    UseWarcry();
                }
            }
        }
        
        private void UseBerserkRage()
        {
            _lastSelfBuffTime = Time.time;
            
            // Use RPC manager for multiplayer sync
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_BERSERK_RAGE, 12f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("berserkrage");
            
            // Announce via chat bubble
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "BerserkRage");
            
            // Notify synergy manager
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "BerserkRage");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} entered BERSERK RAGE!");
        }
        
        private void UseWarcry()
        {
            _lastGroupAbilityTime = Time.time;
            
            // Use RPC manager for multiplayer sync - applies to all allies in range
            int count = AbilityRPCManager.ApplyGroupBuff(_character, StatusEffectManager.EFFECT_WARCRY, 15f, 15f);
            
            // Grant skill XP + bonus for each ally buffed
            _skillSystem?.OnAbilityUsed("warcry");
            for (int i = 0; i < count; i++)
            {
                _skillSystem?.OnAbilityBuffedAlly("warcry", null);
            }
            
            // Announce via chat bubble
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "Warcry", count);
            
            // Notify synergy manager
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "Warcry");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} used WARCRY buffing {count} allies!");
        }
        
        private void UseAvatarOfWar()
        {
            _lastUltimateAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_AVATAR_OF_WAR, 20f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("avatarofwar");
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "AvatarOfWar");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "AvatarOfWar");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} became AVATAR OF WAR!");
        }
        
        #endregion
        
        #region Rogue Abilities
        
        private void UpdateRogueAbilities()
        {
            var archetype = ArchetypeClass.Rogue;
            int level = CompanionLevel;
            float healthPercent = _character.GetHealthPercentage();
            
            // SMART CHECK: Don't use stealth if combat is ending (few/no enemies left)
            int activeEnemies = CountEnemiesInRange(20f);
            bool combatEffectivelyOver = activeEnemies == 0;
            
            // ULTIMATE (L100): Shadow Dance - when outnumbered or low health
            if (AbilityUnlockSystem.IsAbilityUnlocked("rogue_shadow_dance", archetype, level))
            {
                if (CanUseUltimateAbility() && !combatEffectivelyOver && (healthPercent < 0.25f || CountEnemiesInRange(8f) >= 3))
                {
                    UseShadowDance();
                    return;
                }
            }
            
            // MASTER (L75): Death Mark - mark a high-value target (not weak enemies)
            if (AbilityUnlockSystem.IsAbilityUnlocked("rogue_death_mark", archetype, level))
            {
                if (CanUseMasterAbility() && !combatEffectivelyOver)
                {
                    var target = GetCurrentTarget();
                    // Only mark targets worth marking (not greydwarfs etc)
                    if (target != null && !StatusEffectManager.HasEffect(target, StatusEffectManager.EFFECT_DEATH_MARK))
                    {
                        var threatLevel = EvaluateThreatLevel(target);
                        if (threatLevel >= ThreatLevel.Medium)
                        {
                            UseDeathMark(target);
                        }
                    }
                }
            }
            
            // EXPERT (L50): Evasion - when taking damage and being targeted
            if (AbilityUnlockSystem.IsAbilityUnlocked("rogue_evasion", archetype, level))
            {
                if (CanUseExpertAbility() && !combatEffectivelyOver && IsBeingTargeted() && healthPercent < 0.5f)
                {
                    UseEvasion();
                    return;
                }
            }
            
            // Self buff: Stealth when not directly engaged AND there are still enemies to fight
            // DON'T stealth if combat is effectively over!
            if (AbilityUnlockSystem.IsAbilityUnlocked("rogue_stealth", archetype, level))
            {
                if (CanUseSelfBuff() && !IsBeingTargeted() && !combatEffectivelyOver && activeEnemies >= 1)
                {
                    UseStealth();
                }
            }
            
            // Passive: Apply poison on attacks (unlocks at level 5, handled via combat hooks)
            // Caltrops (unlocks at level 15) could be triggered when surrounded
        }
        
        private void UseStealth()
        {
            _lastSelfBuffTime = Time.time;
            
            // Use RPC manager for multiplayer sync
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_STEALTH, 12f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("stealth");
            
            // Announce via chat bubble
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "Stealth");
            
            // Notify synergy manager
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "Stealth");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} entered STEALTH!");
        }
        
        /// <summary>
        /// Called when rogue lands a hit - applies poison.
        /// Poison unlocks at level 5.
        /// </summary>
        public void OnRogueHit(Character target)
        {
            if (_archetypeController?.CurrentArchetypeClass != ArchetypeClass.Rogue) return;
            if (target == null || target.IsDead()) return;
            
            // Check if poison is unlocked (level 5)
            if (!AbilityUnlockSystem.IsAbilityUnlocked("rogue_poison", ArchetypeClass.Rogue, CompanionLevel))
                return;
            
            // Apply poison with a chance
            if (Random.value < 0.5f) // 50% chance to poison
            {
                // Use RPC manager for multiplayer sync
                AbilityRPCManager.ApplySingleEffect(_character, target, StatusEffectManager.EFFECT_POISON, 8f);
                
                // Grant skill XP for poisoning enemy
                _skillSystem?.OnAbilityHitEnemy("poison", target, false);
                
                if (VerboseLogging)
                {
                    Debug.Log($"[ArchetypeAbility] {_companion.companionName} poisoned {target.m_name}!");
                }
            }
        }
        
        private void UseShadowDance()
        {
            _lastUltimateAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_SHADOW_DANCE, 10f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("shadowdance");
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "ShadowDance");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "ShadowDance");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} began SHADOW DANCE!");
        }
        
        private void UseDeathMark(Character target)
        {
            _lastMasterAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySingleEffect(_character, target, StatusEffectManager.EFFECT_DEATH_MARK, 30f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("deathmark");
            _skillSystem?.OnAbilityHitEnemy("deathmark", target, false);
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "DeathMark");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "DeathMark");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} placed DEATH MARK on {target.m_name}!");
        }
        
        private void UseEvasion()
        {
            _lastExpertAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_EVASION, 8f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("evasion");
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "Evasion");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "Evasion");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} activated EVASION!");
        }
        
        #endregion
        
        #region Monk Abilities
        
        private void UpdateMonkAbilities()
        {
            var archetype = ArchetypeClass.Monk;
            int level = CompanionLevel;
            float healthPercent = _character.GetHealthPercentage();
            
            // ULTIMATE (L100): Way of Perfection - when surrounded by enemies
            if (AbilityUnlockSystem.IsAbilityUnlocked("monk_perfection", archetype, level))
            {
                if (CanUseUltimateAbility() && CountEnemiesInRange(5f) >= 3)
                {
                    UseWayOfPerfection();
                    return;
                }
            }
            
            // MASTER (L75): Chi Explosion - when surrounded and ready to burst
            if (AbilityUnlockSystem.IsAbilityUnlocked("monk_chi_explosion", archetype, level))
            {
                if (CanUseMasterAbility() && CountEnemiesInRange(6f) >= 2)
                {
                    UseChiExplosion();
                    return;
                }
            }
            
            // EXPERT (L50): Iron Body - when taking heavy damage
            if (AbilityUnlockSystem.IsAbilityUnlocked("monk_iron_body", archetype, level))
            {
                if (CanUseExpertAbility() && healthPercent < 0.4f && IsBeingTargeted())
                {
                    UseIronBody();
                    return;
                }
            }
            
            // EXPERT (L35): Flurry of Blows - when engaging an enemy
            if (AbilityUnlockSystem.IsAbilityUnlocked("monk_flurry", archetype, level))
            {
                if (CanUseExpertAbility() && HasEnemyInRange(3f))
                {
                    UseFlurryOfBlows();
                    return;
                }
            }
            
            // Self buff: Chi Strike when engaging (unlocks at level 5)
            if (AbilityUnlockSystem.IsAbilityUnlocked("monk_chi_strike", archetype, level))
            {
                if (CanUseSelfBuff() && HasEnemyInRange(3f))
                {
                    UseChiStrike();
                }
            }
            
            // Group ability: Inner Peace when allies need healing (unlocks at level 10)
            if (AbilityUnlockSystem.IsAbilityUnlocked("monk_inner_peace", archetype, level))
            {
                if (CanUseGroupAbility() && HasHurtAlliesNearby() && !IsUnderHeavyAttack())
                {
                    UseInnerPeace();
                }
            }
        }
        
        private void UseChiStrike()
        {
            _lastSelfBuffTime = Time.time;
            
            // Use RPC manager for multiplayer sync
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_CHI_STRIKE, 15f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("chistrike");
            
            // Announce via chat bubble
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "ChiStrike");
            
            // Notify synergy manager
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "ChiStrike");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} activated CHI STRIKE!");
        }
        
        private void UseInnerPeace()
        {
            _lastGroupAbilityTime = Time.time;
            
            // Use RPC manager for multiplayer sync
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_INNER_PEACE, 8f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("innerpeace");
            
            // Announce via chat bubble
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "InnerPeace");
            
            // Notify synergy manager
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "InnerPeace");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} entered INNER PEACE meditation!");
        }
        
        private void UseWayOfPerfection()
        {
            _lastUltimateAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_WAY_OF_PERFECTION, 15f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("wayofperfection");
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "WayOfPerfection");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "WayOfPerfection");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} achieved WAY OF PERFECTION!");
        }
        
        private void UseChiExplosion()
        {
            _lastMasterAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_CHI_EXPLOSION, 1f);
            
            // Grant skill XP + bonus for nearby enemies
            _skillSystem?.OnAbilityUsed("chiexplosion");
            int nearbyEnemies = CountEnemiesInRange(6f);
            for (int i = 0; i < nearbyEnemies; i++)
            {
                _skillSystem?.OnAbilityHitEnemy("chiexplosion", null, false);
            }
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "ChiExplosion");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "ChiExplosion");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} released CHI EXPLOSION!");
        }
        
        private void UseIronBody()
        {
            _lastExpertAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_IRON_BODY, 10f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("ironbody");
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "IronBody");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "IronBody");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} activated IRON BODY!");
        }
        
        private void UseFlurryOfBlows()
        {
            _lastExpertAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_FLURRY_OF_BLOWS, 5f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("flurryofblows");
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "FlurryOfBlows");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "FlurryOfBlows");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} unleashed FLURRY OF BLOWS!");
        }
        
        #endregion
        
        #region Ranger Abilities
        
        private void UpdateRangerAbilities()
        {
            var archetype = ArchetypeClass.Ranger;
            int level = CompanionLevel;
            
            // ULTIMATE (L100): Perfect Shot - when engaging a high-value target
            if (AbilityUnlockSystem.IsAbilityUnlocked("ranger_perfect_shot", archetype, level))
            {
                if (CanUseUltimateAbility() && HasEnemyInRange(30f) && !HasEnemyInRange(5f))
                {
                    UsePerfectShot();
                    return;
                }
            }
            
            // MASTER (L75): Rain of Arrows - when multiple enemies are grouped
            if (AbilityUnlockSystem.IsAbilityUnlocked("ranger_rain_arrows", archetype, level))
            {
                if (CanUseMasterAbility() && CountEnemiesInRange(15f) >= 3)
                {
                    UseRainOfArrows();
                    return;
                }
            }
            
            // EXPERT (L35): Multishot - when engaging multiple enemies
            if (AbilityUnlockSystem.IsAbilityUnlocked("ranger_multishot", archetype, level))
            {
                if (CanUseExpertAbility() && CountEnemiesInRange(15f) >= 2 && !HasEnemyInRange(5f))
                {
                    UseMultishot();
                    return;
                }
            }
            
            // Self buff: Eagle Eye when engaging at range (unlocks at level 10)
            if (AbilityUnlockSystem.IsAbilityUnlocked("ranger_eagle_eye", archetype, level))
            {
                if (CanUseSelfBuff() && HasEnemyInRange(20f) && !HasEnemyInRange(5f))
                {
                    UseEagleEye();
                }
            }
            
            // Mark target: Apply Hunter's Mark to priority target (unlocks at level 5)
            if (AbilityUnlockSystem.IsAbilityUnlocked("ranger_mark", archetype, level))
            {
                if (CanUsePassiveAbility())
                {
                    var target = GetCurrentTarget();
                    if (target != null && !StatusEffectManager.HasEffect(target, StatusEffectManager.EFFECT_HUNTERS_MARK))
                    {
                        UseHuntersMark(target);
                    }
                }
            }
        }
        
        private void UseEagleEye()
        {
            _lastSelfBuffTime = Time.time;
            
            // Use RPC manager for multiplayer sync
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_EAGLE_EYE, 15f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("eagleeye");
            
            // Announce via chat bubble
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "EagleEye");
            
            // Notify synergy manager
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "EagleEye");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} activated EAGLE EYE!");
        }
        
        private void UseHuntersMark(Character target)
        {
            _lastPassiveAbilityTime = Time.time;
            
            // Use RPC manager for multiplayer sync
            AbilityRPCManager.ApplySingleEffect(_character, target, StatusEffectManager.EFFECT_HUNTERS_MARK, 20f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("huntersmark");
            _skillSystem?.OnAbilityHitEnemy("huntersmark", target, false);
            
            // Announce via chat bubble
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "HuntersMark");
            
            // Notify synergy manager
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "HuntersMark");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} marked {target.m_name} with HUNTER'S MARK!");
        }
        
        private void UsePerfectShot()
        {
            _lastUltimateAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_PERFECT_SHOT, 15f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("perfectshot");
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "PerfectShot");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "PerfectShot");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} prepared PERFECT SHOT!");
        }
        
        private void UseRainOfArrows()
        {
            _lastMasterAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_RAIN_OF_ARROWS, 5f);
            
            // Grant skill XP + bonus for enemies in area
            _skillSystem?.OnAbilityUsed("rainofarrows");
            int nearbyEnemies = CountEnemiesInRange(15f);
            for (int i = 0; i < nearbyEnemies; i++)
            {
                _skillSystem?.OnAbilityHitEnemy("rainofarrows", null, false);
            }
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "RainOfArrows");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "RainOfArrows");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} unleashed RAIN OF ARROWS!");
        }
        
        private void UseMultishot()
        {
            _lastExpertAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_MULTISHOT, 15f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("multishot");
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "Multishot");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "Multishot");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} readied MULTISHOT!");
        }
        
        #endregion
        
        #region Mage Abilities
        
        private void UpdateMageAbilities()
        {
            var archetype = ArchetypeClass.Mage;
            int level = CompanionLevel;
            float healthPercent = _character.GetHealthPercentage();
            
            // ULTIMATE (L100): Arcane Form - when in heavy combat
            if (AbilityUnlockSystem.IsAbilityUnlocked("mage_arcane_form", archetype, level))
            {
                if (CanUseUltimateAbility() && CountEnemiesInRange(15f) >= 3)
                {
                    UseArcaneForm();
                    return;
                }
            }
            
            // MASTER (L75): Meteor - when multiple enemies are grouped
            if (AbilityUnlockSystem.IsAbilityUnlocked("mage_meteor", archetype, level))
            {
                if (CanUseMasterAbility() && CountEnemiesInRange(10f) >= 2)
                {
                    UseMeteor();
                    return;
                }
            }
            
            if (AbilityUnlockSystem.IsAbilityUnlocked("mage_chain", archetype, level))
                EnsurePassive(StatusEffectManager.EFFECT_CHAIN_CASTING, "chaincasting");
            
            // EXPERT (L35): Overcharge - when engaging enemies
            if (AbilityUnlockSystem.IsAbilityUnlocked("mage_overcharge", archetype, level))
            {
                if (CanUseExpertAbility() && HasEnemyInRange(15f))
                {
                    UseOvercharge();
                    return;
                }
            }
            
            // Self buff: Elemental Infusion when casting (unlocks at level 5)
            if (AbilityUnlockSystem.IsAbilityUnlocked("mage_infusion", archetype, level))
            {
                if (CanUseSelfBuff())
                {
                    UseElementalInfusion();
                }
            }
            
            // Defensive: Arcane Shield when taking damage (unlocks at level 10)
            if (AbilityUnlockSystem.IsAbilityUnlocked("mage_shield", archetype, level))
            {
                if (CanUseGroupAbility() && healthPercent < 0.6f)
                {
                    UseArcaneShield();
                }
            }
        }
        
        private void UseElementalInfusion()
        {
            _lastSelfBuffTime = Time.time;
            
            // Use RPC manager for multiplayer sync
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_ELEMENTAL_INFUSION, 20f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("elementalinfusion");
            
            // Announce via chat bubble
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "ElementalInfusion");
            
            // Notify synergy manager
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "ElementalInfusion");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} activated ELEMENTAL INFUSION!");
        }
        
        private void UseArcaneShield()
        {
            _lastGroupAbilityTime = Time.time;
            
            // Use RPC manager for multiplayer sync
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_ARCANE_SHIELD, 15f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("arcaneshield");
            
            // Announce via chat bubble
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "ArcaneShield");
            
            // Notify synergy manager
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "ArcaneShield");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} created ARCANE SHIELD!");
        }
        
        private void UseArcaneForm()
        {
            _lastUltimateAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_ARCANE_FORM, 15f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("arcaneform");
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "ArcaneForm");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "ArcaneForm");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} transformed into ARCANE FORM!");
        }
        
        private void UseMeteor()
        {
            _lastMasterAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_METEOR, 3f);
            
            // Grant skill XP + bonus for enemies in area
            _skillSystem?.OnAbilityUsed("meteor");
            int nearbyEnemies = CountEnemiesInRange(10f);
            for (int i = 0; i < nearbyEnemies; i++)
            {
                _skillSystem?.OnAbilityHitEnemy("meteor", null, false);
            }
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "Meteor");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "Meteor");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} called down METEOR!");
        }
        
        private void UseOvercharge()
        {
            _lastExpertAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_OVERCHARGE, 20f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("overcharge");
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "Overcharge");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "Overcharge");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} activated OVERCHARGE!");
        }
        
        #endregion
        
        #region Healer Abilities
        
        // Emergency heal cooldown tracking
        private float _lastEmergencyHealTime = -100f;
        private const float EmergencyHealCooldown = 5f;
        private const float EmergencyHealthThreshold = 0.20f; // 20% health = emergency
        
        // Resurrection cooldown (very long - 5 minutes)
        private float _lastResurrectionTime = -1000f;
        private const float ResurrectionCooldown = 300f;
        
        private void UpdateHealerAbilities()
        {
            var archetype = ArchetypeClass.Healer;
            int level = CompanionLevel;
            
            // DEBUG: Always log when healer updates abilities
            if (VerboseLogging || Time.frameCount % 300 == 0) // Log every ~5 seconds
            {
                bool canGroup = CanUseGroupAbility();
                bool sanctuaryUnlocked = AbilityUnlockSystem.IsAbilityUnlocked("healer_sanctuary", archetype, level);
                Debug.Log($"[ArchetypeAbility] Healer {_companion.companionName} Update - Level:{level}, CanGroup:{canGroup}, SanctuaryUnlocked:{sanctuaryUnlocked}");
            }
            
            // ULTIMATE (L100): Avatar of Life - when multiple allies are critically low
            if (AbilityUnlockSystem.IsAbilityUnlocked("healer_avatar_life", archetype, level))
            {
                if (CanUseUltimateAbility() && CountCriticalAllies(0.3f) >= 2)
                {
                    UseAvatarOfLife();
                    return;
                }
            }
            
            // MASTER (L75): Divine Hymn - when multiple allies need healing
            if (AbilityUnlockSystem.IsAbilityUnlocked("healer_divine_hymn", archetype, level))
            {
                if (CanUseMasterAbility() && CountHurtAlliesNearby() >= 2)
                {
                    UseDivineHymn();
                    return;
                }
            }
            
            // EXPERT (L50): Resurrection - when a companion is defeated
            if (AbilityUnlockSystem.IsAbilityUnlocked("healer_resurrection", archetype, level))
            {
                if (Time.time - _lastResurrectionTime >= ResurrectionCooldown)
                {
                    var defeatedCompanion = FindDefeatedCompanion();
                    if (defeatedCompanion != null)
                    {
                        UseResurrection(defeatedCompanion);
                        return;
                    }
                }
            }
            
            // PRIORITY 1: Emergency heal - anyone below 20% health gets immediate attention (unlocks at level 35)
            if (AbilityUnlockSystem.IsAbilityUnlocked("healer_emergency", archetype, level))
            {
                if (Time.time - _lastEmergencyHealTime >= EmergencyHealCooldown)
                {
                    var emergencyTarget = FindEmergencyHealTarget();
                    if (emergencyTarget != null)
                    {
                        UseEmergencyHeal(emergencyTarget);
                        return;
                    }
                }
            }
            
            // PRIORITY 2: Keep tank alive - check ANY tank, not just taunting ones
            var tank = FindTankInCombat();
            if (tank != null && CanUseGroupAbility())
            {
                var tankChar = tank.GetCharacter();
                if (tankChar != null)
                {
                    float tankHealth = tankChar.GetHealthPercentage();
                    if (tankHealth < 0.70f)
                    {
                        if (AbilityUnlockSystem.IsAbilityUnlocked("healer_sanctuary", archetype, level))
                        {
                            Debug.Log($"[ArchetypeAbility] Healer {_companion.companionName} protecting tank at {tankHealth:P0} health");
                            UseSanctuary();
                            return;
                        }
                    }
                }
            }
            
            // Self buff: Purify when debuffed or hurt (unlocks at level 5)
            if (AbilityUnlockSystem.IsAbilityUnlocked("healer_purify", archetype, level))
            {
                if (CanUseSelfBuff() && (_character.GetHealthPercentage() < 0.7f || HasDebuffs()))
                {
                    UsePurify();
                    return;
                }
            }
            
            // CRITICAL: Healers should use Sanctuary PROACTIVELY at combat start!
            // Check if Sanctuary is unlocked (level 10)
            if (AbilityUnlockSystem.IsAbilityUnlocked("healer_sanctuary", archetype, level))
            {
                if (CanUseGroupAbility())
                {
                    bool combatJustStarted = Time.time - _combatEntryTime < 10f; // Extended window
                    bool alliesHurt = HasHurtAlliesNearby();
                    bool anyAllyTookDamage = HasAnyAllyBelowFullHealth();
                    
                    // Purifying Circle for debuffs (unlocks at level 25)
                    if (AlliesHaveDebuffs() && AbilityUnlockSystem.IsAbilityUnlocked("healer_purify_circle", archetype, level))
                    {
                        Debug.Log($"[ArchetypeAbility] Healer {_companion.companionName} using Purifying Circle for debuffs");
                        UsePurifyingCircle();
                        return;
                    }
                    
                    // Sanctuary when in combat - be AGGRESSIVE about healing!
                    if (combatJustStarted || alliesHurt || anyAllyTookDamage)
                    {
                        Debug.Log($"[ArchetypeAbility] Healer {_companion.companionName} using Sanctuary - combatStart={combatJustStarted}, hurt={alliesHurt}, anyDamage={anyAllyTookDamage}");
                        UseSanctuary();
                        return;
                    }
                }
                else
                {
                    // Cooldown active - log for debug
                    float remaining = groupAbilityCooldown - (Time.time - _lastGroupAbilityTime);
                    if (VerboseLogging)
                    {
                        Debug.Log($"[ArchetypeAbility] Healer {_companion.companionName} Sanctuary on cooldown ({remaining:F1}s remaining)");
                    }
                }
            }
            else
            {
                // Sanctuary not unlocked - log for debug
                if (VerboseLogging)
                {
                    Debug.Log($"[ArchetypeAbility] Healer {_companion.companionName} Sanctuary NOT unlocked (level {level}, needs 10)");
                }
            }
        }
        
        /// <summary>
        /// Finds ANY tank in combat, not just one that's actively taunting.
        /// Healers should protect tanks proactively.
        /// </summary>
        private CompanionController FindTankInCombat()
        {
            if (_companion == null) return null;
            
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null || companion.isDefeated) continue;
                if (companion.ownerPlayerId != _companion.ownerPlayerId) continue;
                
                var archController = companion.GetArchetypeController();
                if (archController != null && archController.IsTank)
                {
                    // Check if tank is in combat
                    var tankAI = companion.GetCompanionAI();
                    if (tankAI != null && tankAI.IsInCombat)
                    {
                        return companion;
                    }
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// Checks if ANY ally has taken damage (below full health).
        /// More sensitive than HasHurtAlliesNearby which uses a 50% threshold.
        /// </summary>
        private bool HasAnyAllyBelowFullHealth()
        {
            if (_companion == null) return false;
            
            float range = 15f;
            float threshold = 0.95f; // 95% health - very sensitive
            
            // Check owner
            var owner = _companion.GetOwner();
            if (owner != null && owner.GetHealthPercentage() < threshold)
            {
                return true;
            }
            
            // Check other companions
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null || companion.isDefeated) continue;
                if (companion.ownerPlayerId != _companion.ownerPlayerId) continue;
                if (companion == _companion) continue;
                
                var compChar = companion.GetCharacter();
                if (compChar != null && !compChar.IsDead())
                {
                    float dist = Vector3.Distance(transform.position, compChar.transform.position);
                    if (dist <= range && compChar.GetHealthPercentage() < threshold)
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Finds any ally below 20% health that needs emergency healing.
        /// Priority: Tank > Self > Other Companions > Owner
        /// </summary>
        private Character FindEmergencyHealTarget()
        {
            if (_companion == null) return null;
            
            Character emergencyTarget = null;
            float lowestHealth = EmergencyHealthThreshold;
            
            // Check self first
            if (_character.GetHealthPercentage() < lowestHealth)
            {
                emergencyTarget = _character;
                lowestHealth = _character.GetHealthPercentage();
            }
            
            // Check tank (highest priority if taunting)
            var tank = FindTauntingTank();
            if (tank != null)
            {
                var tankChar = tank.GetCharacter();
                if (tankChar != null && tankChar.GetHealthPercentage() < EmergencyHealthThreshold)
                {
                    // Tank in emergency - they get priority!
                    return tankChar;
                }
            }
            
            // Check other companions
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null || companion.isDefeated) continue;
                if (companion.ownerPlayerId != _companion.ownerPlayerId) continue;
                if (companion == _companion) continue;
                
                var compChar = companion.GetCharacter();
                if (compChar != null && !compChar.IsDead())
                {
                    float healthPercent = compChar.GetHealthPercentage();
                    if (healthPercent < lowestHealth)
                    {
                        emergencyTarget = compChar;
                        lowestHealth = healthPercent;
                    }
                }
            }
            
            // Check owner
            var owner = _companion.GetOwner();
            if (owner != null && owner.GetHealthPercentage() < lowestHealth)
            {
                emergencyTarget = owner;
            }
            
            return emergencyTarget;
        }
        
        /// <summary>
        /// Finds a tank companion that is currently taunting (has active taunt).
        /// Used for emergency heal prioritization - taunting tanks need immediate support.
        /// </summary>
        private CompanionController FindTauntingTank()
        {
            if (_companion == null) return null;
            
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null || companion.isDefeated) continue;
                if (companion.ownerPlayerId != _companion.ownerPlayerId) continue;
                
                var archController = companion.GetArchetypeController();
                if (archController != null && archController.IsTank)
                {
                    // Check if tank has the "Taunting" status effect active
                    var tankChar = companion.GetCharacter();
                    if (tankChar != null)
                    {
                        var seman = tankChar.GetSEMan();
                        if (seman != null && seman.HaveStatusEffect("CompanionTaunting".GetStableHashCode()))
                        {
                            return companion;
                        }
                    }
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// Finds a defeated companion that can be resurrected.
        /// </summary>
        private CompanionController FindDefeatedCompanion()
        {
            if (_companion == null) return null;
            
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null) continue;
                if (companion.ownerPlayerId != _companion.ownerPlayerId) continue;
                if (companion == _companion) continue;
                
                if (companion.isDefeated)
                {
                    return companion;
                }
            }
            
            return null;
        }
        
        /// <summary>
        /// Emergency heal for critically wounded allies.
        /// </summary>
        private void UseEmergencyHeal(Character target)
        {
            _lastEmergencyHealTime = Time.time;
            
            // Apply sanctuary to the target (it's a group buff that will help them)
            int count = AbilityRPCManager.ApplyGroupBuff(_character, StatusEffectManager.EFFECT_SANCTUARY, 15f, 12f);
            
            // Grant skill XP + bonus for emergency heal
            _skillSystem?.OnAbilityUsed("sanctuary");
            _skillSystem?.OnAbilityBuffedAlly("sanctuary", target);
            
            // Announce
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "EmergencyHeal");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} EMERGENCY HEAL on {target.m_name} ({target.GetHealthPercentage() * 100:F0}% HP)!");
        }
        
        private void UsePurify()
        {
            _lastSelfBuffTime = Time.time;
            
            // Use RPC manager for multiplayer sync
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_PURIFY, 10f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("purify");
            
            // Announce via chat bubble
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "Purify");
            
            // Notify synergy manager
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "Purify");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} used PURIFY!");
        }
        
        private void UseSanctuary()
        {
            _lastGroupAbilityTime = Time.time;
            
            // Use RPC manager for multiplayer sync - applies to all allies in range
            int count = AbilityRPCManager.ApplyGroupBuff(_character, StatusEffectManager.EFFECT_SANCTUARY, 10f, 10f);
            
            // Grant skill XP + bonus for each ally healed
            _skillSystem?.OnAbilityUsed("sanctuary");
            for (int i = 0; i < count; i++)
            {
                _skillSystem?.OnAbilityBuffedAlly("sanctuary", null);
            }
            
            // Announce via chat bubble
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "Sanctuary", count);
            
            // Notify synergy manager
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "Sanctuary");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} created SANCTUARY protecting {count} allies!");
        }
        
        private void UsePurifyingCircle()
        {
            _lastGroupAbilityTime = Time.time;
            
            // Use RPC manager for multiplayer sync - applies to all allies in range
            int count = AbilityRPCManager.ApplyGroupBuff(_character, StatusEffectManager.EFFECT_PURIFYING_CIRCLE, 10f, 12f);
            
            // Grant skill XP + bonus for each ally
            _skillSystem?.OnAbilityUsed("purifyingcircle");
            for (int i = 0; i < count; i++)
            {
                _skillSystem?.OnAbilityBuffedAlly("purifyingcircle", null);
            }
            
            // Announce via chat bubble
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "PurifyingCircle", count);
            
            // Notify synergy manager
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "PurifyingCircle");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} created PURIFYING CIRCLE for {count} allies!");
        }
        
        private void UseAvatarOfLife()
        {
            _lastUltimateAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_AVATAR_OF_LIFE, 20f);
            
            // Grant skill XP
            _skillSystem?.OnAbilityUsed("avataroflife");
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "AvatarOfLife");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "AvatarOfLife");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} became AVATAR OF LIFE!");
        }
        
        private void UseDivineHymn()
        {
            _lastMasterAbilityTime = Time.time;
            
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_DIVINE_HYMN, 8f);
            
            // Grant skill XP + bonus for allies in range
            _skillSystem?.OnAbilityUsed("divinehymn");
            int hurtAllies = CountHurtAlliesNearby();
            for (int i = 0; i < hurtAllies; i++)
            {
                _skillSystem?.OnAbilityBuffedAlly("divinehymn", null);
            }
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "DivineHymn");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "DivineHymn");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} began singing DIVINE HYMN!");
        }
        
        private void UseResurrection(CompanionController defeatedCompanion)
        {
            _lastResurrectionTime = Time.time;
            
            // Apply resurrection effect to self (triggers the respawn)
            AbilityRPCManager.ApplySelfBuff(_character, StatusEffectManager.EFFECT_RESURRECTION, 3f);
            
            // Grant skill XP - big XP for resurrection
            _skillSystem?.OnAbilityUsed("resurrection");
            _skillSystem?.OnAbilityBuffedAlly("resurrection", null); // Extra XP for saving an ally
            
            // Request immediate respawn of the defeated companion
            var healerPos = _character.transform.position;
            CompanionRespawnManager.RequestImmediateRespawn(defeatedCompanion.companionId, healerPos);
            
            ArchetypeChatManager.AnnounceAbilityUsed(_companion, "Resurrection");
            GroupSynergyManager.Instance?.OnAbilityUsed(_companion, "Resurrection");
            
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} used RESURRECTION on {defeatedCompanion.companionName}!");
        }
        
        #endregion
        
        #region Passives

        private const float PassiveRetrySeconds = 5f;

        private static readonly Dictionary<string, ArchetypeClass> PassiveOwners = new Dictionary<string, ArchetypeClass>
        {
            { StatusEffectManager.EFFECT_UNYIELDING, ArchetypeClass.Tank },
            { StatusEffectManager.EFFECT_IRON_WALL, ArchetypeClass.Tank },
            { StatusEffectManager.EFFECT_DEATH_WISH, ArchetypeClass.Berserker },
            { StatusEffectManager.EFFECT_RAMPAGE, ArchetypeClass.Berserker },
            { StatusEffectManager.EFFECT_EXECUTE, ArchetypeClass.Berserker },
            { StatusEffectManager.EFFECT_CHAIN_CASTING, ArchetypeClass.Mage },
        };

        private readonly Dictionary<string, float> _passiveRetryAt = new Dictionary<string, float>();
        private ArchetypeClass _passiveArchetype = ArchetypeClass.None;

        /// <summary>Applies a permanent passive once; a failed apply waits PassiveRetrySeconds instead of retrying every frame.</summary>
        private void EnsurePassive(string effectName, string skillId)
        {
            if (StatusEffectManager.HasEffect(_character, effectName)) return;
            if (_passiveRetryAt.TryGetValue(effectName, out float retryAt) && Time.time < retryAt) return;
            _passiveRetryAt[effectName] = Time.time + PassiveRetrySeconds;
            if (!AbilityRPCManager.ApplySelfBuff(_character, effectName, 0f)) return;

            _skillSystem?.OnAbilityUsed(skillId);
            Debug.Log($"[ArchetypeAbility] {_companion.companionName} gained passive {effectName}");
        }

        private void DropOtherArchetypePassives(ArchetypeClass archetype)
        {
            _passiveArchetype = archetype;
            foreach (var passive in PassiveOwners)
            {
                if (passive.Value != archetype && StatusEffectManager.HasEffect(_character, passive.Key))
                    AbilityRPCManager.RemoveSelfBuff(_character, passive.Key);
            }
        }

        #endregion

        #region Helper Methods

        private bool CanUseSelfBuff()
        {
            return Time.time - _lastSelfBuffTime >= selfBuffCooldown;
        }
        
        private bool CanUseGroupAbility()
        {
            return Time.time - _lastGroupAbilityTime >= groupAbilityCooldown;
        }
        
        private bool CanUsePassiveAbility()
        {
            return Time.time - _lastPassiveAbilityTime >= passiveAbilityCooldown;
        }
        
        private bool CanUseExpertAbility()
        {
            return Time.time - _lastExpertAbilityTime >= expertAbilityCooldown;
        }
        
        private bool CanUseMasterAbility()
        {
            return Time.time - _lastMasterAbilityTime >= masterAbilityCooldown;
        }
        
        private bool CanUseUltimateAbility()
        {
            return Time.time - _lastUltimateAbilityTime >= ultimateAbilityCooldown;
        }
        
        /// <summary>
        /// Counts enemies within the specified range.
        /// </summary>
        private int CountEnemiesInRange(float range)
        {
            if (_character == null) return 0;
            
            int count = 0;
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (!BaseAI.IsEnemy(_character, character)) continue;
                
                float dist = Vector3.Distance(transform.position, character.transform.position);
                if (dist <= range) count++;
            }
            return count;
        }
        
        /// <summary>
        /// Counts hurt allies (below threshold) in range.
        /// </summary>
        private int CountHurtAlliesNearby()
        {
            if (_companion == null) return 0;
            
            int count = 0;
            float range = 15f;
            
            // Check owner
            var owner = _companion.GetOwner();
            if (owner != null && owner.GetHealthPercentage() < allyLowHealthThreshold)
            {
                count++;
            }
            
            // Check other companions
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null || companion.isDefeated) continue;
                if (companion.ownerPlayerId != _companion.ownerPlayerId) continue;
                if (companion == _companion) continue;
                
                var compChar = companion.GetCharacter();
                if (compChar != null && !compChar.IsDead())
                {
                    float dist = Vector3.Distance(transform.position, compChar.transform.position);
                    if (dist <= range && compChar.GetHealthPercentage() < allyLowHealthThreshold)
                    {
                        count++;
                    }
                }
            }
            
            return count;
        }
        
        /// <summary>
        /// Counts critically low allies (below threshold) in range.
        /// </summary>
        private int CountCriticalAllies(float threshold)
        {
            if (_companion == null) return 0;
            
            int count = 0;
            float range = 15f;
            
            // Check self
            if (_character.GetHealthPercentage() < threshold)
            {
                count++;
            }
            
            // Check owner
            var owner = _companion.GetOwner();
            if (owner != null && owner.GetHealthPercentage() < threshold)
            {
                count++;
            }
            
            // Check other companions
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null || companion.isDefeated) continue;
                if (companion.ownerPlayerId != _companion.ownerPlayerId) continue;
                if (companion == _companion) continue;
                
                var compChar = companion.GetCharacter();
                if (compChar != null && !compChar.IsDead())
                {
                    float dist = Vector3.Distance(transform.position, compChar.transform.position);
                    if (dist <= range && compChar.GetHealthPercentage() < threshold)
                    {
                        count++;
                    }
                }
            }
            
            return count;
        }
        
        /// <summary>
        /// Finds an ally below the specified health threshold.
        /// </summary>
        private Character FindCriticalAlly(float threshold)
        {
            if (_companion == null) return null;
            
            Character criticalAlly = null;
            float lowestHealth = threshold;
            float range = 15f;
            
            // Check self
            if (_character.GetHealthPercentage() < lowestHealth)
            {
                criticalAlly = _character;
                lowestHealth = _character.GetHealthPercentage();
            }
            
            // Check owner
            var owner = _companion.GetOwner();
            if (owner != null && owner.GetHealthPercentage() < lowestHealth)
            {
                float dist = Vector3.Distance(transform.position, owner.transform.position);
                if (dist <= range)
                {
                    criticalAlly = owner;
                    lowestHealth = owner.GetHealthPercentage();
                }
            }
            
            // Check other companions
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null || companion.isDefeated) continue;
                if (companion.ownerPlayerId != _companion.ownerPlayerId) continue;
                if (companion == _companion) continue;
                
                var compChar = companion.GetCharacter();
                if (compChar != null && !compChar.IsDead())
                {
                    float dist = Vector3.Distance(transform.position, compChar.transform.position);
                    if (dist <= range && compChar.GetHealthPercentage() < lowestHealth)
                    {
                        criticalAlly = compChar;
                        lowestHealth = compChar.GetHealthPercentage();
                    }
                }
            }
            
            return criticalAlly;
        }
        
        private bool HasEnemyInRange(float range)
        {
            if (_character == null) return false;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (!BaseAI.IsEnemy(_character, character)) continue;
                
                float dist = Vector3.Distance(transform.position, character.transform.position);
                if (dist <= range) return true;
            }
            return false;
        }
        
        private bool HasHurtAlliesNearby()
        {
            if (_companion == null) return false;
            
            // Check owner
            var owner = _companion.GetOwner();
            if (owner != null && owner.GetHealthPercentage() < allyLowHealthThreshold)
            {
                return true;
            }
            
            // Check other companions
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                if (BaseAI.IsEnemy(_character, character)) continue;
                
                float dist = Vector3.Distance(transform.position, character.transform.position);
                if (dist <= 15f && character.GetHealthPercentage() < allyLowHealthThreshold)
                {
                    return true;
                }
            }
            return false;
        }
        
        private bool HasAlliesInCombat()
        {
            // Only count allies fighting nearby. Without the distance gate,
            // a companion 200 m away chasing a hog made every Berserker in
            // the player's base fire Warcry repeatedly — there's no point
            // buffing for a fight you're not part of.
            const float AllyCombatRangeSq = 25f * 25f;
            Vector3 myPos = transform.position;

            var allCompanions = UnityEngine.Object.FindObjectsByType<CompanionController>(UnityEngine.FindObjectsSortMode.None);
            foreach (var companion in allCompanions)
            {
                if (companion == null || companion == _companion) continue;
                if (companion.ownerPlayerId != _companion.ownerPlayerId) continue;

                var ai = companion.GetComponent<AI.CompanionAI>();
                if (ai == null || !ai.IsInCombat) continue;

                if ((companion.transform.position - myPos).sqrMagnitude <= AllyCombatRangeSq)
                    return true;
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
        
        private bool IsUnderHeavyAttack()
        {
            // Count enemies targeting us
            int targetingCount = 0;
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (!BaseAI.IsEnemy(_character, character)) continue;
                
                var ai = character.GetComponent<BaseAI>();
                if (ai != null && ai.GetTargetCreature() == _character)
                {
                    targetingCount++;
                }
            }
            return targetingCount >= 2;
        }
        
        private Character GetCurrentTarget()
        {
            if (_ai == null) return null;
            return _ai.GetTargetCreature();
        }
        
        private bool HasDebuffs()
        {
            var seman = _character?.GetSEMan();
            if (seman == null) return false;
            
            // Check for common debuffs
            string[] debuffs = { "Poison", "Burning", "Frost", "Wet", "Cold", "Freezing" };
            foreach (var debuff in debuffs)
            {
                if (seman.HaveStatusEffect(debuff.GetStableHashCode())) return true;
            }
            return false;
        }
        
        private bool AlliesHaveDebuffs()
        {
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == _character) continue;
                if (BaseAI.IsEnemy(_character, character)) continue;
                
                float dist = Vector3.Distance(transform.position, character.transform.position);
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
        
        #endregion
        
        #region Enemy Threat Evaluation
        
        /// <summary>
        /// Threat levels for enemies - used to decide if abilities are worth using.
        /// </summary>
        public enum ThreatLevel
        {
            /// <summary>Not worth using cooldowns on (greydwarfs, necks, etc)</summary>
            Trivial,
            /// <summary>Basic enemies (skeletons, draugr)</summary>
            Low,
            /// <summary>Moderate threat (trolls, elite draugr, fulings)</summary>
            Medium,
            /// <summary>Significant threat (bosses, mini-bosses, starred enemies)</summary>
            High,
            /// <summary>Major threat (world bosses, raid bosses)</summary>
            Boss
        }
        
        /// <summary>
        /// Evaluates the threat level of an enemy to determine if cooldowns are worth using.
        /// </summary>
        private ThreatLevel EvaluateThreatLevel(Character enemy)
        {
            if (enemy == null) return ThreatLevel.Trivial;
            
            string name = enemy.m_name?.ToLower() ?? "";
            float maxHealth = enemy.GetMaxHealth();
            
            // Check for starred enemies (1-star, 2-star) - these are always higher threat
            int level = enemy.GetLevel();
            if (level >= 3) return ThreatLevel.High; // 2-star
            if (level >= 2) return ThreatLevel.Medium; // 1-star
            
            // Boss check - high health or boss names
            if (maxHealth > 5000 || name.Contains("boss") || name.Contains("yagluth") || 
                name.Contains("bonemass") || name.Contains("moder") || name.Contains("eikthyr") ||
                name.Contains("elder") || name.Contains("queen"))
            {
                return ThreatLevel.Boss;
            }
            
            // High threat enemies
            if (maxHealth > 1000 || name.Contains("troll") || name.Contains("golem") || 
                name.Contains("abomination") || name.Contains("serpent") || name.Contains("lox") ||
                name.Contains("growth") || name.Contains("gjall") || name.Contains("seeker") ||
                name.Contains("dvergr"))
            {
                return ThreatLevel.High;
            }
            
            // Medium threat enemies
            if (maxHealth > 200 || name.Contains("draugr") || name.Contains("skeleton") || 
                name.Contains("fuling") || name.Contains("fenring") || name.Contains("wolf") ||
                name.Contains("wraith") || name.Contains("blob") || name.Contains("ooze"))
            {
                return ThreatLevel.Medium;
            }
            
            // Low threat - basic creatures
            if (name.Contains("boar") || name.Contains("deer") || name.Contains("leech"))
            {
                return ThreatLevel.Low;
            }
            
            // Trivial - greydwarfs, necks, etc (low health, common trash mobs)
            if (maxHealth < 100 || name.Contains("greydwarf") || name.Contains("neck") || 
                name.Contains("greyling"))
            {
                return ThreatLevel.Trivial;
            }
            
            // Default to low for unknown enemies
            return ThreatLevel.Low;
        }
        
        /// <summary>
        /// Gets the combined threat level of all enemies in range.
        /// </summary>
        private ThreatLevel GetCombinedThreatLevel(float range)
        {
            ThreatLevel highest = ThreatLevel.Trivial;
            int significantEnemyCount = 0;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (!BaseAI.IsEnemy(_character, character)) continue;
                
                float dist = Vector3.Distance(transform.position, character.transform.position);
                if (dist <= range)
                {
                    var threat = EvaluateThreatLevel(character);
                    if (threat > highest) highest = threat;
                    if (threat >= ThreatLevel.Medium) significantEnemyCount++;
                }
            }
            
            // Bump up threat level if there are many medium+ threats
            if (significantEnemyCount >= 3 && highest < ThreatLevel.High)
            {
                highest = ThreatLevel.High;
            }
            
            return highest;
        }
        
        /// <summary>
        /// Checks if the current combat situation justifies using a major cooldown.
        /// </summary>
        private bool IsWorthUsingCooldown(float cooldownDuration)
        {
            // Short cooldowns (< 30s) - use more freely
            if (cooldownDuration < 30f) return true;
            
            var threatLevel = GetCombinedThreatLevel(20f);
            int enemyCount = CountEnemiesInRange(20f);
            
            // Long cooldowns (30s+) - only use on medium+ threats or 4+ enemies
            if (cooldownDuration >= 30f)
            {
                return threatLevel >= ThreatLevel.Medium || enemyCount >= 4;
            }
            
            // Very long cooldowns (60s+) - only use on high+ threats or 5+ enemies
            if (cooldownDuration >= 60f)
            {
                return threatLevel >= ThreatLevel.High || enemyCount >= 5;
            }
            
            return true;
        }
        
        #endregion
    }
}
