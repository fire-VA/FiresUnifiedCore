using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using FiresCore.Lifecycle;
using FiresCore.Npc.AI;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// Spawns and cleans up the visual and sound effects for companion abilities using Valheim's own effect
    /// prefabs. Every spawned effect is tracked and destroyed when its duration ends, with a fallback timeout
    /// so nothing outlives its ability.
    /// </summary>
    public static class AbilityFXManager
    {
        public static bool VerboseLogging = false;
        
        // Cache for prefab lookups
        private static Dictionary<string, GameObject> _prefabCache = new Dictionary<string, GameObject>();
        
        // Track active effects for cleanup
        private static List<TrackedEffect> _activeEffects = new List<TrackedEffect>();
        
        // Fallback cleanup duration if effect doesn't self-destruct
        private const float FallbackCleanupDuration = 10f;

        private static int _localCopyDepth;

        /// <summary>
        /// Every client runs routed-RPC ability code and spawns its own copy of the effect, so inside this scope a
        /// spawn skips its ZNetView (vanilla's trick for visual copies) and only happens near this machine's player.
        /// Outside it, the companion's owner spawns once, the copy is networked, and it needs any player nearby.
        /// </summary>
        public static LocalCopyScope LocalCopies()
        {
            _localCopyDepth++;
            return new LocalCopyScope();
        }

        public struct LocalCopyScope : System.IDisposable
        {
            public void Dispose() => _localCopyDepth--;
        }

        private static bool ShouldSpawnAt(Vector3 position) =>
            _localCopyDepth > 0 ? Core.NpcFxRange.NearLocalPlayer(position) : Core.NpcFxRange.NearAnyPlayer(position);

        private static GameObject CreateInstance(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            if (_localCopyDepth == 0) return Object.Instantiate(prefab, position, rotation);
            bool previous = ZNetView.m_forceDisableInit;
            ZNetView.m_forceDisableInit = true;
            try { return Object.Instantiate(prefab, position, rotation); }
            finally { ZNetView.m_forceDisableInit = previous; }
        }

        /// <summary>
        /// Tracks a spawned effect for cleanup.
        /// </summary>
        private class TrackedEffect
        {
            public GameObject Instance;
            public float SpawnTime;
            public float Duration;
            
            public bool IsExpired => Instance == null || Time.time > SpawnTime + Duration;
        }
        
        #region Effect Prefab Names - VERIFIED FROM FX DUMP
        
        // ========================
        // TANK/DEFENSIVE EFFECTS
        // ========================
        
        /// <summary>Shaman protection bubble - green shield dome [Duration:8.0s]</summary>
        public const string FX_TAUNT_SHOCKWAVE = "vfx_sledge_hit";
        /// <summary>Shaman protection bubble for Fortify [Duration:8.0s]</summary>
        public const string FX_FORTIFY = "fx_shaman_protect";
        /// <summary>Backup: Goblin shield glow [Continuous]</summary>
        public const string FX_FORTIFY_BACKUP = "vfx_GoblinShield";
        /// <summary>Shield startup effect for Divine Protection [Duration:8.0s]</summary>
        public const string FX_DIVINE_PROTECTION = "fx_shield_start";
        /// <summary>Shield dome for group protection</summary>
        public const string FX_SHIELD_DOME = "fx_shieldgenerator_domehit";
        
        // ========================
        // HEALER/SUPPORT EFFECTS  
        // ========================
        
        /// <summary>Dverger support buff - scaled down for less overwhelming visuals [Duration:28.0s]</summary>
        public const string FX_SANCTUARY = "fx_DvergerMage_Support_start";
        /// <summary>Alternative sanctuary effect - shield start for cleaner look [Duration:8.0s]</summary>
        public const string FX_SANCTUARY_ALT = "fx_shield_start";
        /// <summary>Dverger support hit for heal pulses [Duration:15.0s]</summary>
        public const string FX_HEAL_PULSE = "fx_DvergerMage_Support_hit";
        /// <summary>Offering altar glow for Purify [Duration:10.0s]</summary>
        public const string FX_PURIFY = "vfx_offering";
        /// <summary>Health upgrade particles for Purifying Circle [Duration:15.0s]</summary>
        public const string FX_PURIFYING_CIRCLE = "vfx_HealthUpgrade";
        /// <summary>Creature tamed hearts effect [Duration:5.0s]</summary>
        public const string FX_HEAL_HEARTS = "fx_creature_tamed";
        /// <summary>Shield start pulse - nice expanding ring for heal pulse [Duration:8.0s]</summary>
        public const string FX_HEAL_SHIELD_PULSE = "fx_shield_start";
        /// <summary>Spawn effect for resurrect/summon [Duration:10.0s]</summary>
        public const string FX_SPAWN_GLOW = "vfx_spawn";
        
        // ========================
        // BERSERKER EFFECTS
        // ========================
        
        /// <summary>Berserker mead visual for rage state [Continuous]</summary>
        public const string FX_BERSERK_RAGE = "vfx_MeadBzerker";
        /// <summary>Fire spray for berserker burst [Duration:5.0s]</summary>
        public const string FX_BERSERK_FIRE = "vfx_FireballHit";
        /// <summary>Eikthyr stomp for Warcry [Duration:10.0s]</summary>
        public const string FX_WARCRY = "fx_eikthyr_stomp";
        /// <summary>Troll ground slam for heavy attack [Duration:5.0s]</summary>
        public const string FX_GROUND_SLAM = "vfx_troll_groundslam";
        /// <summary>Burning aura for low health rage [Continuous]</summary>
        public const string FX_BURNING_AURA = "vfx_Burning";
        
        // ========================
        // ROGUE EFFECTS
        // ========================
        
        /// <summary>Ghost death fade for stealth [Duration:10.0s]</summary>
        public const string FX_STEALTH = "vfx_ghost_death";
        /// <summary>Wraith fade as backup stealth [Duration:10.0s]</summary>
        public const string FX_STEALTH_BACKUP = "vfx_wraith_death";
        /// <summary>Odin despawn for vanish [Duration:5.0s]</summary>
        public const string FX_VANISH = "vfx_odin_despawn";
        /// <summary>Poison aura [Continuous]</summary>
        public const string FX_POISON_APPLY = "vfx_Poison";
        /// <summary>Poison spray attack [Duration:5.0s]</summary>
        public const string FX_POISON_SPRAY = "vfx_BombBlob_explode_poison";
        /// <summary>Poison explosion for backstab [Duration:8.0s]</summary>
        public const string FX_POISON_BURST = "vfx_poisonarrow_hit";
        /// <summary>Blob attack for caltrops [Duration:5.0s]</summary>
        public const string FX_CALTROPS = "vfx_blob_attack";
        
        // ========================
        // RANGER EFFECTS
        // ========================
        
        /// <summary>Spawn glow for Hunter's Mark [Duration:10.0s]</summary>
        public const string FX_HUNTERS_MARK = "vfx_spawn";
        /// <summary>Lightning bolt for Eagle Eye activation [Instant]</summary>
        public const string FX_EAGLE_EYE = "fx_Lightning";
        /// <summary>Chain lightning spread for marked targets [Duration:8.0s]</summary>
        public const string FX_CHAIN_MARK = "fx_chainlightning_spread";
        /// <summary>Nature weapon hit for nature arrows [Duration:8.0s]</summary>
        public const string FX_NATURE_HIT = "fx_natureweapon_hit";
        
        // ========================
        // MAGE EFFECTS
        // ========================
        
        /// <summary>Haste mead visual for elemental infusion [Continuous]</summary>
        public const string FX_ELEMENTAL_INFUSION = "vfx_MeadHasty";
        /// <summary>Staff shield for Arcane Shield [Continuous]</summary>
        public const string FX_ARCANE_SHIELD = "vfx_StaffShield";
        /// <summary>Fireball explosion [Duration:8.0s]</summary>
        public const string FX_FIRE_BURST = "fx_fireball_staff_explosion";
        /// <summary>Staff fireball explosion [Duration:8.0s]</summary>
        public const string FX_FIRE_STAFF = "fx_fireball_staff_explosion";
        /// <summary>Frostbolt explosion [Duration:8.0s]</summary>
        public const string FX_ICE_BURST = "fx_DvergerMage_Ice_hit";
        /// <summary>Fenring ice nova [Duration:6.0s]</summary>
        public const string FX_ICE_NOVA = "fx_fenring_icenova";
        /// <summary>Thunder explosion [Duration:8.0s]</summary>
        public const string FX_LIGHTNING_BURST = "fx_JotunWitch_LightningBolt_Explosion";
        /// <summary>Chain lightning for AoE [Duration:3.0s]</summary>
        public const string FX_CHAIN_LIGHTNING = "fx_chainlightning_hit";
        /// <summary>Fire skeleton nova for fire mage [Duration:6.0s]</summary>
        public const string FX_FIRE_NOVA = "fx_fireskeleton_nova";
        
        // ========================
        // PALADIN EFFECTS
        // ========================
        
        /// <summary>Eikthyr forward shockwave for Holy Smite [Duration:10.0s]</summary>
        public const string FX_HOLY_SMITE = "fx_eikthyr_forwardshockwave";
        /// <summary>Spirit bolt for holy damage [Duration:8.0s]</summary>
        public const string FX_HOLY_BOLT = "vfx_ghost_hit";
        /// <summary>Spirit spray for consecration [Duration:5.0s]</summary>
        public const string FX_CONSECRATE = "fx_DvergerMage_Support_hit";
        /// <summary>Himminafl AoE for large consecration [Duration:10.0s]</summary>
        public const string FX_CONSECRATE_LARGE = "fx_himminafl_aoe";
        /// <summary>Goblin king nova as backup [Duration:10.0s]</summary>
        public const string FX_HOLY_NOVA = "fx_goblinking_nova";
        
        // ========================
        // MONK EFFECTS
        // ========================
        
        /// <summary>Fenring frost for Chi Strike [Duration:5.0s]</summary>
        public const string FX_CHI_STRIKE = "fx_fenring_frost";
        /// <summary>Cold aura for monk meditation [Continuous]</summary>
        public const string FX_CHI_AURA = "vfx_Cold";
        /// <summary>Tamed hearts for Inner Peace [Duration:5.0s]</summary>
        public const string FX_INNER_PEACE = "fx_creature_tamed";
        /// <summary>Stamina upgrade for chi regeneration [Duration:15.0s]</summary>
        public const string FX_CHI_REGEN = "vfx_StaminaUpgrade";
        /// <summary>Eitr potion for monk abilities [Continuous]</summary>
        public const string FX_EITR_FLOW = "vfx_Potion_eitr_minor";
        
        // ========================
        // UTILITY/GENERIC EFFECTS
        // ========================
        
        /// <summary>Perfect block flash [Duration:2.0s]</summary>
        public const string FX_PERFECT_BLOCK = "vfx_perfectblock";
        /// <summary>Blocked attack sparks [Duration:2.0s]</summary>
        public const string FX_BLOCKED = "vfx_blocked";
        /// <summary>Critical hit indicator [Duration:2.0s]</summary>
        public const string FX_CRIT = "fx_crit";
        /// <summary>Backstab indicator [Duration:4.0s]</summary>
        public const string FX_BACKSTAB = "fx_backstab";
        /// <summary>Adrenaline burst [Duration:4.0s]</summary>
        public const string FX_ADRENALINE = "fx_Adrenaline1";
        /// <summary>Taunted visual indicator [Duration:3.0s]</summary>
        public const string FX_TAUNTED = "fx_crit";
        
        #endregion
        
        #region Sound Effect Prefab Names - VERIFIED FROM SFX DUMP
        
        // ========================
        // TANK/DEFENSIVE SOUNDS
        // ========================
        
        /// <summary>Troll roar for taunt [Duration:8.0s]</summary>
        public const string SFX_TAUNT = "sfx_trollfire_roar";
        /// <summary>Shield blocked sound for fortify activation</summary>
        public const string SFX_FORTIFY = "sfx_metal_blocked";
        /// <summary>Perfect block for parry</summary>
        public const string SFX_PARRY = "sfx_perfectblock";
        
        // ========================
        // HEALER/SUPPORT SOUNDS
        // ========================
        
        /// <summary>Dverger heal finish for sanctuary [Duration:6.0s]</summary>
        public const string SFX_SANCTUARY = "sfx_dverger_heal_finish";
        /// <summary>Dverger heal start for healing abilities</summary>
        public const string SFX_HEAL_START = "sfx_dverger_heal_start";
        /// <summary>Health potion for heal pulse</summary>
        public const string SFX_HEAL = "sfx_Potion_health_medium";
        /// <summary>Offering sound for purify</summary>
        public const string SFX_PURIFY = "sfx_offering";
        
        // ========================
        // BERSERKER SOUNDS
        // ========================
        
        /// <summary>Bear roar for berserk rage</summary>
        public const string SFX_BERSERK = "sfx_trollfire_roar";
        /// <summary>Eikthyr stomp for warcry</summary>
        public const string SFX_WARCRY = "sfx_gdking_stomp";
        /// <summary>Sledge hit for ground slam</summary>
        public const string SFX_GROUND_SLAM = "sfx_sledge_hit";
        
        // ========================
        // ROGUE SOUNDS
        // ========================
        
        /// <summary>Ghost alert for stealth</summary>
        public const string SFX_STEALTH = "sfx_ghost_alert";
        /// <summary>Odin despawn for vanish</summary>
        public const string SFX_VANISH = "sfx_spawn";
        /// <summary>Poison start for poison application</summary>
        public const string SFX_POISON = "sfx_Poison_Start";
        /// <summary>Backstab hit indicator</summary>
        public const string SFX_BACKSTAB = "sfx_knife_swing";
        
        // ========================
        // RANGER SOUNDS
        // ========================
        
        /// <summary>Bow draw for hunter's mark</summary>
        public const string SFX_HUNTERS_MARK = "sfx_bow_draw";
        /// <summary>Lightning for eagle eye</summary>
        public const string SFX_EAGLE_EYE = "sfx_StaffLightning_fire";
        /// <summary>Arrow hit for marked target</summary>
        public const string SFX_ARROW_HIT = "sfx_arrow_hit";
        
        // ========================
        // MAGE SOUNDS
        // ========================
        
        /// <summary>Staff charge for elemental infusion</summary>
        public const string SFX_ELEMENTAL_INFUSION = "sfx_staff_lightning_charge";
        /// <summary>Shield generator startup for arcane shield</summary>
        public const string SFX_ARCANE_SHIELD = "sfx_shieldgenerator_startup";
        /// <summary>Fireball launch for fire burst</summary>
        public const string SFX_FIRE_BURST = "sfx_firestaff_launch";
        /// <summary>Ice staff start for ice burst</summary>
        public const string SFX_ICE_BURST = "sfx_icestaff_start";
        /// <summary>Lightning fire for lightning burst</summary>
        public const string SFX_LIGHTNING_BURST = "sfx_StaffLightning_fire";
        
        // ========================
        // PALADIN SOUNDS
        // ========================
        
        /// <summary>Shield start for divine protection</summary>
        public const string SFX_DIVINE_PROTECTION = "sfx_shieldgenerator_startup";
        /// <summary>Spirit bolt for holy smite</summary>
        public const string SFX_HOLY_SMITE = "sfx_dverger_heavyattack_launch";
        /// <summary>Offering for consecrate</summary>
        public const string SFX_CONSECRATE = "sfx_offering";
        
        // ========================
        // MONK SOUNDS
        // ========================
        
        /// <summary>Unarmed hit for chi strike</summary>
        public const string SFX_CHI_STRIKE = "sfx_unarmed_hit";
        /// <summary>Creature consume for inner peace</summary>
        public const string SFX_INNER_PEACE = "sfx_creature_consume";
        /// <summary>Stamina potion for chi flow</summary>
        public const string SFX_CHI_FLOW = "sfx_Potion_stamina_Start";
        
        // ========================
        // UTILITY SOUNDS
        // ========================
        
        /// <summary>Metal blocked for regular block</summary>
        public const string SFX_BLOCK = "sfx_metal_blocked";
        /// <summary>Crit hit indicator</summary>
        public const string SFX_CRIT = "sfx_sword_hit";
        /// <summary>Dodge sound</summary>
        public const string SFX_DODGE = "sfx_dodge";
        
        #endregion
        
        #region Spawn Effects

        /// <summary>
        /// Called periodically to prune the tracking list and force-clean any
        /// continuous effects (no TimedDestruction) whose duration has elapsed.
        /// Effects that already have TimedDestruction are left alone - their
        /// OnDestroy path through ZNetScene.Destroy is correct and sufficient.
        /// </summary>
        public static void CleanupExpiredEffects()
        {
            for (int i = _activeEffects.Count - 1; i >= 0; i--)
            {
                var tracked = _activeEffects[i];
                if (!tracked.IsExpired) continue;

                // If the instance is still alive it means there was no TimedDestruction
                // (continuous aura / missing timeout). Route through NetworkObjectHelper so
                // the ZNetScene entry is properly removed - never use Object.Destroy here.
                if (tracked.Instance != null)
                    FiresCore.Net.NetworkObjectHelper.SafeDestroy(tracked.Instance);

                _activeEffects.RemoveAt(i);
            }
        }

        /// <summary>
        /// Clears the tracking list, routing any still-live instances through
        /// NetworkObjectHelper so ZNetScene.m_instances stays consistent.
        /// </summary>
        public static void CleanupAllEffects()
        {
            foreach (var tracked in _activeEffects)
            {
                if (tracked.Instance != null)
                    FiresCore.Net.NetworkObjectHelper.SafeDestroy(tracked.Instance);
            }
            _activeEffects.Clear();

            if (VerboseLogging)
                Debug.Log("[AbilityFXManager] Cleared active-effect tracking list");
        }

        /// <summary>
        /// Spawns a sound effect at a position.
        /// Uses Object.Instantiate (same as EffectList.Create) so ZNetView.Awake
        /// registers the instance correctly. TimedDestruction on the prefab will
        /// call ZNetScene.Destroy when it expires - no manual cleanup needed.
        /// </summary>
        public static GameObject SpawnSound(string sfxName, Vector3 position)
        {
            var prefab = GetCachedPrefab(sfxName);
            if (prefab == null)
            {
                if (VerboseLogging)
                    Debug.LogWarning($"[AbilityFXManager] SFX prefab not found: {sfxName}");
                return null;
            }

            if (!ShouldSpawnAt(position)) return null;

            var instance = CreateInstance(prefab, position, Quaternion.identity);
            ForceSpatial3D(instance);

            // Track so CleanupExpiredEffects can catch any edge-case continuous SFX.
            float dur = EnsureSelfDestruct(instance, FallbackCleanupDuration);
            _activeEffects.Add(new TrackedEffect { Instance = instance, SpawnTime = Time.time, Duration = dur });

            if (VerboseLogging)
                Debug.Log($"[AbilityFXManager] Spawned SFX: {sfxName} at {position}");

            return instance;
        }

        /// <summary>
        /// Guarantees that <paramref name="instance"/> will self-destruct.
        /// If the prefab already has a TimedDestruction component, returns its
        /// configured timeout. Otherwise adds one (with the caller-supplied
        /// fallback duration) and triggers it so the FX cleans itself up via
        /// the vanilla ZNetScene.Destroy path. Without this, FX prefabs that
        /// ship without TimedDestruction (e.g. fx_Lightning used by the Ranger
        /// Eagle Eye ability) would persist indefinitely with their AudioSource
        /// looping until the zone unloads.
        /// </summary>
        private static float EnsureSelfDestruct(GameObject instance, float fallbackDuration)
        {
            if (instance == null) return fallbackDuration;

            var timedDestruction = instance.GetComponent<TimedDestruction>();
            if (timedDestruction != null && timedDestruction.m_timeout > 0f)
                return timedDestruction.m_timeout;

            if (timedDestruction == null)
                timedDestruction = instance.AddComponent<TimedDestruction>();

            timedDestruction.m_timeout = fallbackDuration;
            timedDestruction.Trigger();
            return fallbackDuration;
        }

        /// <summary>
        /// Forces every AudioSource on the spawned hierarchy to 3D positional
        /// audio with a sane rolloff. Many vanilla Valheim FX prefabs ship with
        /// AudioSources set to spatialBlend = 0 (pure 2D) — those play at full
        /// volume regardless of where the prefab is instantiated, so a companion
        /// casting an ability across the map sounds like it's right next to you.
        /// This is the structural fix for the "always the same sound, heard from
        /// anywhere" bug. Vanilla player-side sounds aren't routed through here.
        /// </summary>
        private static void ForceSpatial3D(GameObject instance)
        {
            if (instance == null) return;
            var sources = instance.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
            {
                var src = sources[i];
                if (src == null) continue;
                src.spatialBlend = 1f;                       // 0 = 2D, 1 = 3D
                src.rolloffMode  = AudioRolloffMode.Linear;
                src.minDistance  = 2f;
                src.maxDistance  = 32f;                      // audible only within ~32m
                src.dopplerLevel = 0f;
            }
        }

        /// <summary>
        /// Spawns a visual effect at a position.
        /// Object.Instantiate is the correct vanilla path (same as EffectList.Create).
        /// ZNetView.Awake registers the instance; TimedDestruction calls ZNetScene.Destroy
        /// when it expires. For effects without TimedDestruction, CleanupExpiredEffects
        /// will route through NetworkObjectHelper after FallbackCleanupDuration.
        /// </summary>
        public static GameObject SpawnEffect(string effectName, Vector3 position, Quaternion? rotation = null, float scale = 1f)
        {
            var prefab = GetCachedPrefab(effectName);
            if (prefab == null)
            {
                if (VerboseLogging)
                    Debug.LogWarning($"[AbilityFXManager] Effect prefab not found: {effectName}");
                return null;
            }

            if (!ShouldSpawnAt(position)) return null;

            var instance = CreateInstance(prefab, position, rotation ?? Quaternion.identity);
            ForceSpatial3D(instance);

            if (scale != 1f)
                instance.transform.localScale *= scale;

            float dur = EnsureSelfDestruct(instance, FallbackCleanupDuration);
            _activeEffects.Add(new TrackedEffect { Instance = instance, SpawnTime = Time.time, Duration = dur });

            if (VerboseLogging)
                Debug.Log($"[AbilityFXManager] Spawned effect: {effectName} at {position}");

            return instance;
        }

        /// <summary>
        /// Spawns the effect at the character's current position, deliberately unparented: parented effects were
        /// cascade-destroyed with a dying character, bypassing ZNetScene.Destroy and leaving orphaned instances.
        /// </summary>
        public static GameObject SpawnEffectOnCharacter(string effectName, Character character, float duration = 5f, float scale = 1f)
        {
            if (character == null) return null;
            var prefab = GetCachedPrefab(effectName);
            if (prefab == null)
            {
                if (VerboseLogging)
                    Debug.LogWarning($"[AbilityFXManager] Effect prefab not found: {effectName}");
                return null;
            }

            if (!ShouldSpawnAt(character.transform.position)) return null;

            var instance = CreateInstance(prefab, character.transform.position, Quaternion.identity);
            // SetParent intentionally removed — see method docstring.
            ForceSpatial3D(instance);

            if (scale != 1f)
                instance.transform.localScale *= scale;

            // Prefer the prefab's own TimedDestruction duration; fall back to the
            // caller-supplied duration so continuous auras still get cleaned up.
            float dur = EnsureSelfDestruct(instance, duration);
            _activeEffects.Add(new TrackedEffect { Instance = instance, SpawnTime = Time.time, Duration = dur });

            if (VerboseLogging)
                Debug.Log($"[AbilityFXManager] Spawned effect: {effectName} on {character.m_name}");

            return instance;
        }
        
        /// <summary>
        /// Spawns an AoE effect that radiates outward.
        /// </summary>
        public static void SpawnAoEEffect(string effectName, Vector3 center, float radius, int count = 8)
        {
            // Spawn center effect
            SpawnEffect(effectName, center);
            
            // Spawn effects in a circle
            for (int i = 0; i < count; i++)
            {
                float angle = (i / (float)count) * 360f * Mathf.Deg2Rad;
                Vector3 offset = new Vector3(Mathf.Cos(angle) * radius * 0.7f, 0, Mathf.Sin(angle) * radius * 0.7f);
                SpawnEffect(effectName, center + offset, Quaternion.identity, 0.5f);
            }
        }
        
        #endregion
        
        #region Ability-Specific Effects
        
        // ========================
        // TANK ABILITIES
        // ========================
        
        /// <summary>
        /// Plays the Tank taunt shockwave effect - sledge hammer ground slam with sound.
        /// </summary>
        public static void PlayTauntEffect(Character taunter, float range)
        {
            if (taunter == null) return;
            
            Vector3 groundPos = GetGroundPosition(taunter.transform.position);
            float scale = Mathf.Clamp(range / 8f, 0.5f, 2.5f);
            
            // Sound effect
            SpawnSound(SFX_TAUNT, groundPos);
            SpawnSound(SFX_GROUND_SLAM, groundPos);
            
            // Main sledge slam effect
            SpawnEffect(FX_TAUNT_SHOCKWAVE, groundPos, Quaternion.identity, scale);
            
            // Optional: Add troll groundslam for larger range
            if (range >= 10f)
            {
                SpawnEffect(FX_GROUND_SLAM, groundPos, Quaternion.identity, scale * 0.7f);
            }
        }
        
        /// <summary>
        /// Plays the Fortify defensive buff effect - shaman protection bubble with sound.
        /// </summary>
        public static void PlayFortifyEffect(Character target, float duration)
        {
            if (target == null) return;
            
            // Sound effect
            SpawnSound(SFX_FORTIFY, target.transform.position);
            
            // Shaman protect gives nice green bubble
            SpawnEffectOnCharacter(FX_FORTIFY, target, duration, 1.0f);
            
            // Also spawn shield glow as backup visual
            SpawnEffectOnCharacter(FX_FORTIFY_BACKUP, target, duration, 0.8f);
        }
        
        // ========================
        // HEALER ABILITIES
        // ========================
        
        /// <summary>
        /// Plays the Sanctuary healing aura effect - scaled down dverger support magic with sound.
        /// </summary>
        public static void PlaySanctuaryEffect(Character healer, float range, float duration)
        {
            if (healer == null) return;
            
            // Scale down the effect significantly to avoid overwhelming visuals
            float scale = Mathf.Clamp(range / 15f, 0.3f, 1.0f);
            
            // Sound effect
            SpawnSound(SFX_SANCTUARY, healer.transform.position);
            
            // Main aura - use scaled down version
            SpawnEffectOnCharacter(FX_SANCTUARY, healer, duration, scale * 0.4f);
            
            // Spawn healing hearts at edges
            SpawnAoEEffect(FX_HEAL_HEARTS, healer.transform.position, range * 0.8f, 4);
        }
        
        /// <summary>
        /// Plays the Purify cleanse effect - offering altar glow with sound.
        /// </summary>
        public static void PlayPurifyEffect(Character target)
        {
            if (target == null) return;
            
            // Sound effect
            SpawnSound(SFX_PURIFY, target.transform.position);
            
            // Offering effect for cleansing visual
            SpawnEffectOnCharacter(FX_PURIFY, target, 3f, 0.8f);
            
            // Heal hearts rising
            SpawnEffect(FX_HEAL_HEARTS, target.transform.position + Vector3.up, Quaternion.identity, 0.6f);
        }
        
        /// <summary>
        /// Plays the Purifying Circle AoE effect - health upgrade particles with sound.
        /// </summary>
        public static void PlayPurifyingCircleEffect(Character healer, float range)
        {
            if (healer == null) return;
            
            // Sound effect
            SpawnSound(SFX_HEAL_START, healer.transform.position);
            
            // Central health upgrade particles
            SpawnEffect(FX_PURIFYING_CIRCLE, healer.transform.position + Vector3.up, Quaternion.identity, 1.2f);
            
            // Healing pulses at edges
            SpawnAoEEffect(FX_HEAL_PULSE, healer.transform.position, range * 0.7f, 6);
        }
        
        /// <summary>
        /// Plays a simple heal pulse effect on a target with sound.
        /// Uses shield start for a nice expanding pulse ring plus hearts.
        /// </summary>
        public static void PlayHealEffect(Character target)
        {
            if (target == null) return;
            
            // Sound effect
            SpawnSound(SFX_HEAL, target.transform.position);
            
            // Shield start gives a nice pulse ring effect
            SpawnEffect(FX_HEAL_SHIELD_PULSE, target.transform.position + Vector3.up * 0.5f, Quaternion.identity, 0.6f);
            
            // Hearts rising for visual feedback
            SpawnEffect(FX_HEAL_HEARTS, target.transform.position + Vector3.up, Quaternion.identity, 0.4f);
        }
        
        // ========================
        // BERSERKER ABILITIES
        // ========================
        
        /// <summary>
        /// Plays the Berserk Rage activation effect - berserker mead + fire spray with sound.
        /// </summary>
        public static void PlayBerserkRageEffect(Character berserker, float duration)
        {
            if (berserker == null) return;
            
            // Sound effect
            SpawnSound(SFX_BERSERK, berserker.transform.position);
            
            // Berserker mead visual (red glow)
            SpawnEffectOnCharacter(FX_BERSERK_RAGE, berserker, duration, 1f);
            
            // Initial fire burst
            SpawnEffect(FX_BERSERK_FIRE, berserker.transform.position + Vector3.up, Quaternion.identity, 0.7f);
            
            // Burning aura while raging
            SpawnEffectOnCharacter(FX_BURNING_AURA, berserker, duration, 0.5f);
        }
        
        /// <summary>
        /// Plays the Warcry group buff effect - eikthyr stomp shockwave with sound.
        /// </summary>
        public static void PlayWarcryEffect(Character berserker, float range)
        {
            if (berserker == null) return;

            float scale = Mathf.Clamp(range / 8f, 0.8f, 2f);
            Vector3 groundPos = GetGroundPosition(berserker.transform.position);

            // SpawnSound + SpawnEffect are now ForceSpatial3D'd — the previously
            // global eikthyr-stomp SFX is now localized to the berserker's
            // position with ~32m falloff, so it's only audible nearby.
            SpawnSound(SFX_WARCRY, groundPos);
            SpawnEffect(FX_WARCRY, groundPos, Quaternion.identity, scale);
            SpawnEffect(FX_ADRENALINE, berserker.transform.position + Vector3.up, Quaternion.identity, 1f);
        }
        
        // ========================
        // ROGUE ABILITIES
        // ========================
        
        /// <summary>
        /// Plays the Stealth activation effect - ghost fade with sound.
        /// </summary>
        public static void PlayStealthEffect(Character rogue)
        {
            if (rogue == null) return;
            
            // Sound effect
            SpawnSound(SFX_STEALTH, rogue.transform.position);
            
            // Ghost death fade effect
            SpawnEffect(FX_STEALTH, rogue.transform.position, Quaternion.identity, 0.8f);
            
            // Alternative: Odin vanish
            SpawnEffect(FX_VANISH, rogue.transform.position + Vector3.up * 0.5f, Quaternion.identity, 0.5f);
        }
        
        /// <summary>
        /// Plays the Poison application effect - poison aura on target with sound.
        /// </summary>
        public static void PlayPoisonEffect(Character target)
        {
            if (target == null) return;
            
            // Sound effect
            SpawnSound(SFX_POISON, target.transform.position);
            
            // Poison aura (continuous)
            SpawnEffectOnCharacter(FX_POISON_APPLY, target, 5f, 0.4f);
            
            // Initial poison spray
            SpawnEffect(FX_POISON_SPRAY, target.transform.position + Vector3.up, Quaternion.identity, 0.5f);
        }
        
        /// <summary>
        /// Plays the Caltrops drop effect - blob acid with sound.
        /// </summary>
        public static void PlayCaltropsEffect(Vector3 position)
        {
            // Sound effect
            SpawnSound(SFX_POISON, position);
            
            SpawnEffect(FX_CALTROPS, GetGroundPosition(position), Quaternion.identity, 0.8f);
        }
        
        /// <summary>
        /// Plays the backstab critical hit effect with sound.
        /// </summary>
        public static void PlayBackstabEffect(Character target)
        {
            if (target == null) return;
            
            // Sound effect
            SpawnSound(SFX_BACKSTAB, target.transform.position);
            
            SpawnEffect(FX_BACKSTAB, target.transform.position + Vector3.up, Quaternion.identity, 1f);
            SpawnEffect(FX_POISON_BURST, target.transform.position + Vector3.up, Quaternion.identity, 0.5f);
        }
        
        // ========================
        // RANGER ABILITIES
        // ========================
        
        /// <summary>
        /// Plays the Hunter's Mark effect - spawn glow on target with sound.
        /// </summary>
        public static void PlayHuntersMarkEffect(Character target, float duration)
        {
            if (target == null) return;
            
            // Sound effect
            SpawnSound(SFX_HUNTERS_MARK, target.transform.position);
            
            // Spawn glow marks the target
            SpawnEffectOnCharacter(FX_HUNTERS_MARK, target, duration, 0.6f);
            
            // Chain mark effect for visibility
            SpawnEffect(FX_CHAIN_MARK, target.transform.position + Vector3.up * 2f, Quaternion.identity, 0.3f);
        }
        
        /// <summary>
        /// Plays the Eagle Eye activation effect - lightning bolt overhead with sound.
        /// </summary>
        public static void PlayEagleEyeEffect(Character ranger)
        {
            if (ranger == null) return;
            
            // Sound effect
            SpawnSound(SFX_EAGLE_EYE, ranger.transform.position);
            
            // Lightning bolt above head
            SpawnEffect(FX_EAGLE_EYE, ranger.transform.position + Vector3.up * 2.5f, Quaternion.identity, 0.4f);
        }
        
        /// <summary>
        /// Plays the nature arrow hit effect with sound.
        /// </summary>
        public static void PlayNatureArrowEffect(Vector3 hitPosition)
        {
            // Sound effect
            SpawnSound(SFX_ARROW_HIT, hitPosition);
            
            SpawnEffect(FX_NATURE_HIT, hitPosition, Quaternion.identity, 0.7f);
        }
        
        // ========================
        // MAGE ABILITIES
        // ========================
        
        /// <summary>
        /// Plays the Elemental Infusion effect - haste mead glow with sound.
        /// </summary>
        public static void PlayElementalInfusionEffect(Character mage, float duration)
        {
            if (mage == null) return;
            
            // Sound effect
            SpawnSound(SFX_ELEMENTAL_INFUSION, mage.transform.position);
            
            // Haste mead visual (magical glow)
            SpawnEffectOnCharacter(FX_ELEMENTAL_INFUSION, mage, duration, 0.8f);
            
            // Eitr flow effect
            SpawnEffectOnCharacter(FX_EITR_FLOW, mage, duration, 0.5f);
        }
        
        /// <summary>
        /// Plays the Arcane Shield effect - staff shield bubble with sound.
        /// </summary>
        public static void PlayArcaneShieldEffect(Character target, float duration)
        {
            if (target == null) return;
            
            // Sound effect
            SpawnSound(SFX_ARCANE_SHIELD, target.transform.position);
            
            // Staff shield - scale down to avoid giant swirling effect
            SpawnEffectOnCharacter(FX_ARCANE_SHIELD, target, duration, 0.6f);
        }
        
        /// <summary>
        /// Plays the Fire Mage burst effect - fireball explosion with sound.
        /// </summary>
        public static void PlayFireBurstEffect(Vector3 position, float scale = 1f)
        {
            // Sound effect
            SpawnSound(SFX_FIRE_BURST, position);
            
            SpawnEffect(FX_FIRE_BURST, position, Quaternion.identity, scale);
        }
        
        /// <summary>
        /// Plays the Fire Mage nova effect - fire skeleton nova with sound.
        /// </summary>
        public static void PlayFireNovaEffect(Character mage, float range)
        {
            if (mage == null) return;
            float scale = Mathf.Clamp(range / 8f, 0.5f, 2f);
            
            // Sound effect
            SpawnSound(SFX_FIRE_BURST, mage.transform.position);
            
            SpawnEffect(FX_FIRE_NOVA, mage.transform.position, Quaternion.identity, scale);
        }
        
        /// <summary>
        /// Plays the Ice Mage burst effect - frostbolt explosion with sound.
        /// </summary>
        public static void PlayIceBurstEffect(Vector3 position, float scale = 1f)
        {
            // Sound effect
            SpawnSound(SFX_ICE_BURST, position);
            
            SpawnEffect(FX_ICE_BURST, position, Quaternion.identity, scale);
        }
        
        /// <summary>
        /// Plays the Ice Mage nova effect - fenring ice nova with sound.
        /// </summary>
        public static void PlayIceNovaEffect(Character mage, float range)
        {
            if (mage == null) return;
            float scale = Mathf.Clamp(range / 6f, 0.5f, 2f);
            
            // Sound effect
            SpawnSound(SFX_ICE_BURST, mage.transform.position);
            
            SpawnEffect(FX_ICE_NOVA, mage.transform.position, Quaternion.identity, scale);
        }
        
        /// <summary>
        /// Plays the Lightning Mage burst effect - thunderbolt explosion with sound.
        /// </summary>
        public static void PlayLightningBurstEffect(Vector3 position, float scale = 1f)
        {
            // Sound effect
            SpawnSound(SFX_LIGHTNING_BURST, position);
            
            SpawnEffect(FX_LIGHTNING_BURST, position, Quaternion.identity, scale);
        }
        
        /// <summary>
        /// Plays the Lightning Mage chain effect - chain lightning with sound.
        /// </summary>
        public static void PlayChainLightningEffect(Vector3 position)
        {
            // Sound effect
            SpawnSound(SFX_LIGHTNING_BURST, position);
            
            SpawnEffect(FX_CHAIN_LIGHTNING, position, Quaternion.identity, 1f);
        }
        
        // ========================
        // PALADIN ABILITIES
        // ========================
        
        /// <summary>
        /// Plays the Divine Protection aura effect - shield startup with sound.
        /// </summary>
        public static void PlayDivineProtectionEffect(Character paladin, float range, float duration)
        {
            if (paladin == null) return;
            
            float scale = Mathf.Clamp(range / 8f, 0.5f, 2f);
            
            // Sound effect
            SpawnSound(SFX_DIVINE_PROTECTION, paladin.transform.position);
            
            // Shield start effect
            SpawnEffectOnCharacter(FX_DIVINE_PROTECTION, paladin, duration, scale);
            
            // Shield dome hit for visual shield
            SpawnEffect(FX_SHIELD_DOME, paladin.transform.position + Vector3.up, Quaternion.identity, scale * 0.8f);
        }
        
        /// <summary>
        /// Plays the Holy Smite attack effect - eikthyr forward shockwave with sound.
        /// </summary>
        public static void PlayHolySmiteEffect(Character paladin)
        {
            if (paladin == null) return;
            
            // Sound effect
            SpawnSound(SFX_HOLY_SMITE, paladin.transform.position);
            
            // Eikthyr forward shockwave for directional holy attack
            Quaternion forward = Quaternion.LookRotation(paladin.transform.forward);
            SpawnEffect(FX_HOLY_SMITE, paladin.transform.position + paladin.transform.forward * 2f, forward, 0.6f);
            
            // Spirit bolt for holy damage
            SpawnEffect(FX_HOLY_BOLT, paladin.transform.position + Vector3.up * 1.5f, Quaternion.identity, 0.5f);
        }
        
        /// <summary>
        /// Plays the Consecrate ground effect - spirit spray AoE with sound.
        /// </summary>
        public static void PlayConsecrateEffect(Character paladin, float range)
        {
            if (paladin == null) return;
            
            float scale = Mathf.Clamp(range / 10f, 0.5f, 2f);
            
            // Sound effect
            SpawnSound(SFX_CONSECRATE, paladin.transform.position);
            
            // Spirit spray for consecration
            SpawnEffect(FX_CONSECRATE, paladin.transform.position, Quaternion.identity, scale);
            
            // For larger consecrations, use himminafl AoE
            if (range >= 10f)
            {
                SpawnEffect(FX_CONSECRATE_LARGE, paladin.transform.position, Quaternion.identity, scale * 0.8f);
            }
        }
        
        // ========================
        // MONK ABILITIES
        // ========================
        
        /// <summary>
        /// Plays the Chi Strike effect — fenring frost burst with sound.
        /// SFX + FX prefabs are now routed through ForceSpatial3D so the previously
        /// global freeze SFX attenuates with distance (~32m falloff) instead of
        /// playing across the map.
        /// </summary>
        public static void PlayChiStrikeEffect(Character monk)
        {
            if (monk == null) return;

            SpawnSound(SFX_CHI_STRIKE, monk.transform.position);
            SpawnEffect(FX_CHI_STRIKE, monk.transform.position + monk.transform.forward + Vector3.up,
                Quaternion.LookRotation(monk.transform.forward), 0.6f);
        }
        
        /// <summary>
        /// Plays the Inner Peace meditation effect - hearts + chi regen with sound.
        /// </summary>
        public static void PlayInnerPeaceEffect(Character monk, float range, float duration)
        {
            if (monk == null) return;
            
            float scale = Mathf.Clamp(range / 10f, 0.5f, 1.5f);
            
            // Sound effect
            SpawnSound(SFX_INNER_PEACE, monk.transform.position);
            
            // Tamed hearts for peaceful aura
            SpawnEffectOnCharacter(FX_INNER_PEACE, monk, duration, scale);
            
            // Stamina upgrade for chi regeneration visual
            SpawnEffectOnCharacter(FX_CHI_REGEN, monk, duration, scale * 0.8f);
            
            // Cold aura for meditation focus
            SpawnEffectOnCharacter(FX_CHI_AURA, monk, duration, 0.3f);
        }
        
        /// <summary>
        /// Plays the Chi Flow regeneration effect with sound.
        /// </summary>
        public static void PlayChiFlowEffect(Character monk)
        {
            if (monk == null) return;
            
            // Sound effect
            SpawnSound(SFX_CHI_FLOW, monk.transform.position);
            
            SpawnEffectOnCharacter(FX_EITR_FLOW, monk, 3f, 0.5f);
        }
        
        // ========================
        // UTILITY EFFECTS
        // ========================
        
        /// <summary>
        /// Plays a perfect block effect with sound.
        /// </summary>
        public static void PlayPerfectBlockEffect(Character blocker)
        {
            if (blocker == null) return;
            
            // Sound effect
            SpawnSound(SFX_PARRY, blocker.transform.position);
            
            SpawnEffect(FX_PERFECT_BLOCK, blocker.transform.position + Vector3.up, Quaternion.identity, 1f);
        }
        
        /// <summary>
        /// Plays a regular block effect with sound.
        /// </summary>
        public static void PlayBlockEffect(Character blocker)
        {
            if (blocker == null) return;
            
            // Sound effect
            SpawnSound(SFX_BLOCK, blocker.transform.position);
            
            SpawnEffect(FX_BLOCKED, blocker.transform.position + Vector3.up, Quaternion.identity, 0.8f);
        }
        
        /// <summary>
        /// Plays a critical hit effect with sound.
        /// </summary>
        public static void PlayCritEffect(Vector3 position)
        {
            // Sound effect
            SpawnSound(SFX_CRIT, position);
            
            SpawnEffect(FX_CRIT, position, Quaternion.identity, 1f);
        }
        
        /// <summary>
        /// Plays the taunted indicator on an enemy.
        /// </summary>
        public static void PlayTauntedEffect(Character target, float duration)
        {
            if (target == null) return;
            SpawnEffectOnCharacter(FX_TAUNTED, target, duration, 0.8f);
        }
        
        /// <summary>
        /// Plays an adrenaline burst effect.
        /// </summary>
        public static void PlayAdrenalineEffect(Character target)
        {
            if (target == null) return;
            SpawnEffect(FX_ADRENALINE, target.transform.position + Vector3.up, Quaternion.identity, 0.8f);
        }
        
        /// <summary>
        /// Plays a dodge effect with sound.
        /// </summary>
        public static void PlayDodgeEffect(Character target)
        {
            if (target == null) return;
            
            // Sound effect
            SpawnSound(SFX_DODGE, target.transform.position);
        }
        
        #endregion
        
        #region Helpers
        
        /// <summary>
        /// Gets a cached prefab by name.
        /// </summary>
        private static GameObject GetCachedPrefab(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            
            if (_prefabCache.TryGetValue(prefabName, out var cached))
            {
                return cached;
            }
            
            var prefab = ZNetScene.instance?.GetPrefab(prefabName);
            if (prefab != null)
            {
                _prefabCache[prefabName] = prefab;
            }
            
            return prefab;
        }
        
        /// <summary>
        /// Gets the ground position at a world point.
        /// </summary>
        private static Vector3 GetGroundPosition(Vector3 position)
        {
            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (ZoneSystem.instance.GetGroundHeight(position, out groundHeight))
                {
                    position.y = groundHeight + 0.1f;
                }
            }
            return position;
        }
        
        /// <summary>
        /// Clears the prefab cache and all active effects (call on game unload).
        /// </summary>
        public static void ClearCache()
        {
            CleanupAllEffects();
            _prefabCache.Clear();
        }
        
        /// <summary>
        /// Gets the count of currently tracked active effects.
        /// </summary>
        public static int GetActiveEffectCount()
        {
            return _activeEffects.Count;
        }
        
        #endregion
    }
}
