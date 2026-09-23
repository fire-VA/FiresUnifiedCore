using UnityEngine;
using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using FiresCore.Npc.Archetypes.StatusEffects;
using FiresCore.Npc.Archetypes.StatusEffects.Common;
using FiresCore.Lifecycle;
using FiresCore.Npc.Animation;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// Tank taunt: enemies are held on the taunter for the duration through an enemy-to-taunter registry and a
    /// MonsterAI.UpdateTarget postfix that restores the target after the AI's own selection. A newer taunt replaces
    /// an older one, and the taunt ends early if the taunter dies or leaves range.
    /// </summary>
    public class CompanionTauntEffect : StatusEffect
    {
        /// <summary>The character who applied the taunt.</summary>
        public Character Taunter { get; set; }
        
        /// <summary>Duration of the taunt in seconds.</summary>
        public float Duration { get; set; } = 10f;
        
        /// <summary>Range at which taunt breaks if taunter is too far.</summary>
        public float BreakRange { get; set; } = 30f;
        
        /// <summary>Icon to display (optional).</summary>
        public Sprite TauntIcon { get; set; }
        
        // Track the AI we're manipulating
        private BaseAI _targetAI;
        private float _checkInterval = 0.1f; // Check every 100ms for better responsiveness
        private float _lastCheck;
        
        // Reflection cache for setting target directly
        private static FieldInfo _monsterAI_targetCreatureField;
        private static bool _reflectionInitialized = false;
        
        public static bool VerboseLogging = false;
        
        /// <summary>
        /// Static registry of all active taunts: enemy character -> taunter character.
        /// Used by the Harmony patch to enforce targeting.
        /// </summary>
        public static Dictionary<Character, Character> ActiveTaunts { get; } = new Dictionary<Character, Character>();
        
        private static void InitializeReflection()
        {
            if (_reflectionInitialized) return;
            _reflectionInitialized = true;
            
            // MonsterAI has its own m_targetCreature field (not inherited from BaseAI)
            _monsterAI_targetCreatureField = typeof(MonsterAI).GetField("m_targetCreature",
                BindingFlags.NonPublic | BindingFlags.Instance);
                
            if (_monsterAI_targetCreatureField == null)
            {
                Debug.LogWarning("[CompanionTauntEffect] Could not find MonsterAI.m_targetCreature field via reflection");
            }
        }
        
        public override void Setup(Character character)
        {
            base.Setup(character);
            
            InitializeReflection();
            
            m_ttl = Duration;
            m_name = "Taunted";
            m_icon = TauntIcon;
            
            if (m_character != null && Taunter != null)
            {
                // Get the AI component
                _targetAI = m_character.GetComponent<BaseAI>();
                
                // CRITICAL: Register this taunt in the static registry
                // This allows the Harmony patch to enforce targeting
                lock (ActiveTaunts)
                {
                    ActiveTaunts[m_character] = Taunter;
                }
                
                // Force immediate target switch
                ForceTargetToTaunter();
                
                if (VerboseLogging)
                {
                    m_character.Message(MessageHud.MessageType.TopLeft, "TAUNTED!");
                    Debug.Log($"[CompanionTauntEffect] Setup on {m_character.m_name}, " +
                        $"Taunter: {Taunter.m_name}, Duration: {Duration}s, Registered in ActiveTaunts");
                }
            }
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            // Frequent checks to maintain taunt
            if (Time.time - _lastCheck < _checkInterval) return;
            _lastCheck = Time.time;
            
            // Check if taunt should break
            if (ShouldBreakTaunt())
            {
                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionTauntEffect] Taunt breaking early on {m_character?.m_name}");
                }
                m_ttl = 0; // End the effect
                return;
            }
            
            // Ensure the registry is up to date
            if (m_character != null && Taunter != null && !Taunter.IsDead())
            {
                lock (ActiveTaunts)
                {
                    ActiveTaunts[m_character] = Taunter;
                }
                
                // Also force target directly every update (belt and suspenders)
                ForceTargetToTaunter();
            }
        }
        
        public override void Stop()
        {
            // CRITICAL: Remove from the static registry when effect ends
            if (m_character != null)
            {
                lock (ActiveTaunts)
                {
                    ActiveTaunts.Remove(m_character);
                }
                
                if (VerboseLogging)
                {
                    m_character.Message(MessageHud.MessageType.TopLeft, "Taunt ended.");
                    Debug.Log($"[CompanionTauntEffect] Stopped on {m_character.m_name}, removed from ActiveTaunts");
                }
            }
            
            base.Stop();
        }
        
        /// <summary>
        /// Forces the affected enemy to target the taunter using direct field manipulation.
        /// Called frequently to override the AI's natural target selection.
        /// </summary>
        private void ForceTargetToTaunter()
        {
            if (_targetAI == null || Taunter == null || Taunter.IsDead()) return;
            
            // Use CompanionAI if available (our custom AI)
            var companionAI = _targetAI as FiresCore.Npc.AI.CompanionAI;
            if (companionAI != null)
            {
                companionAI.ForceTarget(Taunter);
                return;
            }
            
            // For MonsterAI, use direct field manipulation
            var monsterAI = _targetAI as MonsterAI;
            if (monsterAI != null && _monsterAI_targetCreatureField != null)
            {
                try
                {
                    var currentTarget = _monsterAI_targetCreatureField.GetValue(monsterAI) as Character;
                    if (currentTarget != Taunter)
                    {
                        _monsterAI_targetCreatureField.SetValue(monsterAI, Taunter);
                        
                        if (VerboseLogging)
                        {
                            Debug.Log($"[CompanionTauntEffect] Forced {m_character?.m_name} target from {currentTarget?.m_name ?? "null"} to {Taunter.m_name}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (VerboseLogging)
                    {
                        Debug.LogWarning($"[CompanionTauntEffect] Failed to force target: {ex.Message}");
                    }
                }
            }
        }
        
        /// <summary>
        /// Checks if the taunt should break early.
        /// </summary>
        private bool ShouldBreakTaunt()
        {
            // Taunter died
            if (Taunter == null || Taunter.IsDead())
            {
                return true;
            }
            
            // Taunter too far away
            if (m_character != null)
            {
                float distance = Vector3.Distance(m_character.transform.position, Taunter.transform.position);
                if (distance > BreakRange)
                {
                    return true;
                }
            }
            
            // Target died
            if (m_character == null || m_character.IsDead())
            {
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Creates a clone of this effect (required by Valheim's status effect system).
        /// </summary>
        public new CompanionTauntEffect Clone()
        {
            var clone = (CompanionTauntEffect)base.Clone();
            if (clone != null)
            {
                clone.Taunter = Taunter;
                clone.Duration = Duration;
                clone.BreakRange = BreakRange;
                clone.TauntIcon = TauntIcon;
            }
            return clone;
        }
        
        /// <summary>
        /// Static helper to check if a character is currently taunted.
        /// </summary>
        public static bool IsTaunted(Character character)
        {
            if (character == null) return false;
            lock (ActiveTaunts)
            {
                return ActiveTaunts.ContainsKey(character);
            }
        }
        
        /// <summary>
        /// Static helper to get the taunter for a taunted character.
        /// </summary>
        public static Character GetTaunter(Character tauntedCharacter)
        {
            if (tauntedCharacter == null) return null;
            lock (ActiveTaunts)
            {
                return ActiveTaunts.TryGetValue(tauntedCharacter, out var taunter) ? taunter : null;
            }
        }
    }
    
    /// <summary>
    /// Harmony patches to enforce taunt targeting in Valheim's AI system.
    /// These patches intercept the AI's target selection and override it when taunted.
    /// 
    /// CRITICAL: MonsterAI overrides BaseAI.GetTargetCreature(), so we must patch BOTH classes.
    /// Patching only BaseAI won't affect MonsterAI instances due to virtual dispatch.
    /// </summary>
    [HarmonyPatch]
    public static class TauntAIPatches
    {
        private static FieldInfo _monsterAI_targetCreatureField;
        private static FieldInfo _baseAI_targetCreatureField;
        private static bool _initialized = false;
        
        private static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            
            // MonsterAI has its own m_targetCreature field (not inherited)
            _monsterAI_targetCreatureField = typeof(MonsterAI).GetField("m_targetCreature",
                BindingFlags.NonPublic | BindingFlags.Instance);
                
            // BaseAI may also have one for other AI types
            _baseAI_targetCreatureField = typeof(BaseAI).GetField("m_targetCreature",
                BindingFlags.NonPublic | BindingFlags.Instance);
                
            if (_monsterAI_targetCreatureField == null)
            {
                Debug.LogWarning("[TauntAIPatches] Could not find MonsterAI.m_targetCreature field!");
            }
        }
        
        /// <summary>
        /// Postfix on MonsterAI.UpdateTarget - override target selection if taunted.
        /// This runs AFTER the AI picks a target, allowing us to override it.
        /// </summary>
        [HarmonyPatch(typeof(MonsterAI), "UpdateTarget")]
        [HarmonyPostfix]
        public static void MonsterAI_UpdateTarget_Postfix(MonsterAI __instance)
        {
            Initialize();
            
            var character = __instance.GetComponent<Character>();
            if (character == null) return;
            
            // Check if this monster is taunted
            Character taunter = CompanionTauntEffect.GetTaunter(character);
            if (taunter == null) return;
            
            // Taunter is dead or invalid - let the effect handle cleanup
            if (taunter.IsDead()) return;
            
            // Force target to the taunter using MonsterAI's m_targetCreature field
            if (_monsterAI_targetCreatureField != null)
            {
                try
                {
                    var currentTarget = _monsterAI_targetCreatureField.GetValue(__instance) as Character;
                    if (currentTarget != taunter)
                    {
                        _monsterAI_targetCreatureField.SetValue(__instance, taunter);
                        
                        if (CompanionTauntEffect.VerboseLogging)
                        {
                            Debug.Log($"[TauntAIPatches] UpdateTarget: Forced {character.m_name} target to {taunter.m_name} (was {currentTarget?.m_name ?? "null"})");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (CompanionTauntEffect.VerboseLogging)
                    {
                        Debug.LogWarning($"[TauntAIPatches] UpdateTarget error: {ex.Message}");
                    }
                }
            }
        }
        
        /// <summary>
        /// Postfix on MonsterAI.GetTargetCreature - return taunter if taunted.
        /// CRITICAL: MonsterAI OVERRIDES BaseAI.GetTargetCreature, so we MUST patch MonsterAI specifically!
        /// </summary>
        [HarmonyPatch(typeof(MonsterAI), "GetTargetCreature")]
        [HarmonyPostfix]
        public static void MonsterAI_GetTargetCreature_Postfix(MonsterAI __instance, ref Character __result)
        {
            var character = __instance.GetComponent<Character>();
            if (character == null) return;
            
            // Check if taunted - if so, return the taunter
            Character taunter = CompanionTauntEffect.GetTaunter(character);
            if (taunter != null && !taunter.IsDead())
            {
                __result = taunter;
                
                if (CompanionTauntEffect.VerboseLogging)
                {
                    Debug.Log($"[TauntAIPatches] MonsterAI.GetTargetCreature override: {character.m_name} -> {taunter.m_name}");
                }
            }
        }
        
        /// <summary>
        /// Postfix on BaseAI.FindEnemy - override enemy selection if taunted.
        /// This is called when the AI is looking for a new target.
        /// </summary>
        [HarmonyPatch(typeof(BaseAI), "FindEnemy")]
        [HarmonyPostfix]
        public static void BaseAI_FindEnemy_Postfix(BaseAI __instance, ref Character __result)
        {
            var character = __instance.GetComponent<Character>();
            if (character == null) return;
            
            // Check if this character is taunted
            Character taunter = CompanionTauntEffect.GetTaunter(character);
            if (taunter != null && !taunter.IsDead())
            {
                // Override the found enemy with our taunter
                __result = taunter;
                
                if (CompanionTauntEffect.VerboseLogging)
                {
                    Debug.Log($"[TauntAIPatches] FindEnemy override: {character.m_name} -> {taunter.m_name}");
                }
            }
        }
        
        /// <summary>
        /// Postfix on BaseAI.HaveTarget - ensure we always have a target when taunted.
        /// </summary>
        [HarmonyPatch(typeof(BaseAI), "HaveTarget")]
        [HarmonyPostfix]
        public static void BaseAI_HaveTarget_Postfix(BaseAI __instance, ref bool __result)
        {
            // If already has target, don't interfere
            if (__result) return;
            
            var character = __instance.GetComponent<Character>();
            if (character == null) return;
            
            // If taunted, we should always "have a target"
            Character taunter = CompanionTauntEffect.GetTaunter(character);
            if (taunter != null && !taunter.IsDead())
            {
                __result = true;
            }
        }
        
        /// <summary>
        /// Postfix on BaseAI.GetTargetCreature - return taunter if taunted.
        /// This handles non-MonsterAI types that don't override GetTargetCreature.
        /// </summary>
        [HarmonyPatch(typeof(BaseAI), "GetTargetCreature")]
        [HarmonyPostfix]
        public static void BaseAI_GetTargetCreature_Postfix(BaseAI __instance, ref Character __result)
        {
            // Skip if this is a MonsterAI - it has its own patch
            if (__instance is MonsterAI) return;
            
            var character = __instance.GetComponent<Character>();
            if (character == null) return;
            
            // Check if taunted - if so, return the taunter
            Character taunter = CompanionTauntEffect.GetTaunter(character);
            if (taunter != null && !taunter.IsDead())
            {
                __result = taunter;
            }
        }
        
        /// <summary>
        /// Postfix on MonsterAI.SetTarget - prevent overriding taunt target when damaged.
        /// When a monster is damaged, SetTarget is called with the attacker.
        /// We need to intercept this and keep our taunter as the target.
        /// </summary>
        [HarmonyPatch(typeof(MonsterAI), "SetTarget")]
        [HarmonyPostfix]
        public static void MonsterAI_SetTarget_Postfix(MonsterAI __instance, Character attacker)
        {
            Initialize();
            
            var character = __instance.GetComponent<Character>();
            if (character == null) return;
            
            // Check if this monster is taunted
            Character taunter = CompanionTauntEffect.GetTaunter(character);
            if (taunter == null || taunter.IsDead()) return;
            
            // Force target back to taunter (even if SetTarget tried to change it)
            if (_monsterAI_targetCreatureField != null)
            {
                try
                {
                    _monsterAI_targetCreatureField.SetValue(__instance, taunter);
                    
                    if (CompanionTauntEffect.VerboseLogging && attacker != null && attacker != taunter)
                    {
                        Debug.Log($"[TauntAIPatches] SetTarget: Prevented {character.m_name} from switching to {attacker.m_name}, keeping {taunter.m_name}");
                    }
                }
                catch { }
            }
        }
    }
    
    /// <summary>
    /// Status effect shown on the TAUNTER (the companion doing the taunting).
    /// Shows them that they're currently taunting enemies.
    /// 
    /// CONTINUOUS TAUNT: While this effect is active, the tank will continuously
    /// re-apply taunt to ALL enemies within range, including newly spawned enemies
    /// or enemies that enter the area after the initial taunt.
    /// </summary>
    public class CompanionTauntingEffect : StatusEffect
    {
        /// <summary>Duration of the taunt ability.</summary>
        public float Duration { get; set; } = 10f;
        
        /// <summary>Range within which enemies will be taunted.</summary>
        public float TauntRange { get; set; } = 5f;
        
        /// <summary>Icon to display on the taunter's buff bar.</summary>
        public Sprite TauntingIcon { get; set; }
        
        /// <summary>List of enemies currently taunted.</summary>
        public List<Character> TauntedEnemies { get; } = new List<Character>();
        
        // How often to re-apply taunt to enemies (seconds)
        private const float TauntReapplyInterval = 1.0f;
        private const float MinimalAggroDamage = 0.01f;
        private float _lastTauntReapplyTime;
        
        public static bool VerboseLogging = false;
        
        public override void Setup(Character character)
        {
            base.Setup(character);
            
            m_ttl = Duration;
            m_name = "Taunting";
            m_tooltip = "Drawing enemy attention";
            m_icon = TauntingIcon;
            _lastTauntReapplyTime = Time.time;
            
            if (VerboseLogging && m_character != null)
            {
                Debug.Log($"[CompanionTauntingEffect] Setup on {m_character.m_name}, Duration: {Duration}s, Range: {TauntRange}m");
            }
        }
        
        public override void UpdateStatusEffect(float dt)
        {
            base.UpdateStatusEffect(dt);
            
            // Continuously re-apply taunt to all enemies in range
            if (Time.time - _lastTauntReapplyTime >= TauntReapplyInterval)
            {
                _lastTauntReapplyTime = Time.time;
                ReapplyTauntToNearbyEnemies();
            }
        }
        
        /// <summary>
        /// Re-applies taunt to all enemies within range, including new ones.
        /// </summary>
        private void ReapplyTauntToNearbyEnemies()
        {
            if (m_character == null || m_character.IsDead()) return;
            
            Vector3 taunterPos = m_character.transform.position;
            float remainingDuration = m_ttl; // Use remaining time for new taunts
            
            // Clean up dead enemies from our list
            TauntedEnemies.RemoveAll(e => e == null || e.IsDead());
            
            int newTauntsApplied = 0;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                // Skip invalid targets
                if (character == null || character.IsDead()) continue;
                if (character == m_character) continue;
                if (character.IsPlayer()) continue;
                
                // Only hit enemies
                if (!BaseAI.IsEnemy(m_character, character)) continue;
                
                // Skip friendly tamed creatures
                if (character.IsTamed())
                {
                    var companionController = character.GetComponent<CompanionController>();
                    if (companionController == null || !BaseAI.IsEnemy(m_character, character))
                    {
                        continue;
                    }
                }
                
                // Check range
                float distance = Vector3.Distance(taunterPos, character.transform.position);
                if (distance > TauntRange) continue;
                
                // Apply or refresh taunt on this enemy
                bool isNewTarget = !TauntedEnemies.Contains(character);
                if (ApplyTauntToEnemy(character, remainingDuration))
                {
                    if (isNewTarget)
                    {
                        TauntedEnemies.Add(character);
                        newTauntsApplied++;
                        
                        // Apply minimal damage to draw aggro for new targets
                        ApplyAggroDamage(character);
                    }
                }
            }
            
            if (VerboseLogging && newTauntsApplied > 0)
            {
                Debug.Log($"[CompanionTauntingEffect] {m_character.m_name} taunted {newTauntsApplied} NEW enemies (total: {TauntedEnemies.Count})");
            }
        }
        
        /// <summary>
        /// Applies taunt status effect to an enemy.
        /// </summary>
        private bool ApplyTauntToEnemy(Character enemy, float duration)
        {
            if (enemy == null) return false;
            
            var seman = enemy.GetSEMan();
            if (seman == null) return false;
            
            // Check if already taunted by us - refresh the duration
            int existingHash = "CompanionTaunted".GetStableHashCode();
            var existingEffect = seman.GetStatusEffect(existingHash) as CompanionTauntEffect;
            
            if (existingEffect != null && existingEffect.Taunter == m_character)
            {
                // Already taunted by us - just refresh the target lock
                // The existing effect will handle keeping the AI focused
                return true;
            }
            
            // Apply new taunt
            var tauntEffect = ScriptableObject.CreateInstance<CompanionTauntEffect>();
            tauntEffect.name = "CompanionTaunted";
            tauntEffect.Taunter = m_character;
            tauntEffect.Duration = duration;
            
            // Remove any existing taunt (from us or others)
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(tauntEffect, true);
            Combat.VikHavnBridge.Taunt(m_character, enemy, duration);
            return true;
        }

        /// <summary>
        /// Applies minimal damage to draw initial aggro from a new target.
        /// </summary>
        private void ApplyAggroDamage(Character enemy)
        {
            if (enemy == null || m_character == null) return;
            
            ZDOID taunterZDOID = m_character.GetComponent<ZNetView>()?.GetZDO()?.m_uid ?? ZDOID.None;
            Vector3 taunterPos = m_character.transform.position;
            
            HitData hitData = new HitData();
            hitData.m_damage.m_blunt = MinimalAggroDamage; // Minimal damage to draw aggro
            hitData.m_point = enemy.transform.position;
            hitData.m_dir = (enemy.transform.position - taunterPos).normalized;
            hitData.m_pushForce = 0f;
            hitData.m_backstabBonus = 1f;
            hitData.m_staggerMultiplier = 0f;
            hitData.m_dodgeable = false;
            hitData.m_blockable = false;
            hitData.m_attacker = taunterZDOID;
            
            enemy.Damage(hitData);
        }
        
        public override void Stop()
        {
            base.Stop();
            
            TauntedEnemies.Clear();
            
            if (VerboseLogging && m_character != null)
            {
                Debug.Log($"[CompanionTauntingEffect] Stopped on {m_character.m_name}");
            }
        }
        
        /// <summary>
        /// Adds an enemy to the list of taunted targets.
        /// </summary>
        public void AddTauntedEnemy(Character enemy)
        {
            if (enemy != null && !TauntedEnemies.Contains(enemy))
            {
                TauntedEnemies.Add(enemy);
            }
        }
        
        public new CompanionTauntingEffect Clone()
        {
            var clone = (CompanionTauntingEffect)base.Clone();
            if (clone != null)
            {
                clone.Duration = Duration;
                clone.TauntRange = TauntRange;
                clone.TauntingIcon = TauntingIcon;
            }
            return clone;
        }
    }
    
    /// <summary>
    /// Manager for taunt-related operations.
    /// Handles applying taunt to enemies and tracking active taunts.
    /// </summary>
    public static class TauntManager
    {
        private const int FallbackIconSize = 64;

        private static bool _effectsRegistered = false;
        private static CompanionTauntEffect _tauntEffectPrefab;
        private static CompanionTauntingEffect _tauntingEffectPrefab;
        
        public static bool VerboseLogging = false;
        
        /// <summary>
        /// Registers the taunt status effects with ObjectDB.
        /// Call this during initialization.
        /// </summary>
        public static void RegisterEffects()
        {
            if (_effectsRegistered) return;
            if (ObjectDB.instance == null) return;
            
            // Create and register the Taunted effect (applied to enemies)
            _tauntEffectPrefab = ScriptableObject.CreateInstance<CompanionTauntEffect>();
            _tauntEffectPrefab.name = "CompanionTaunted";
            ObjectDB.instance.m_StatusEffects.Add(_tauntEffectPrefab);
            
            // Create and register the Taunting effect (applied to the taunter)
            _tauntingEffectPrefab = ScriptableObject.CreateInstance<CompanionTauntingEffect>();
            _tauntingEffectPrefab.name = "CompanionTaunting";
            _tauntingEffectPrefab.TauntingIcon = CreateFallbackIcon(Color.yellow);
            ObjectDB.instance.m_StatusEffects.Add(_tauntingEffectPrefab);
            
            _effectsRegistered = true;
            
            if (VerboseLogging)
            {
                Debug.Log("[TauntManager] Registered taunt status effects");
            }
        }
        
        /// <summary>
        /// Applies taunt from a companion to all nearby enemies.
        /// </summary>
        /// <param name="taunter">The companion doing the taunting.</param>
        /// <param name="range">Range of the taunt.</param>
        /// <param name="duration">How long the taunt lasts.</param>
        /// <returns>Number of enemies taunted.</returns>
        public static int ApplyTaunt(Character taunter, float range, float duration)
        {
            if (taunter == null) return 0;
            
            // Make sure effects are registered
            RegisterEffects();
            
            int tauntedCount = 0;
            Vector3 taunterPos = taunter.transform.position;
            
            // Find all enemies in range
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == taunter) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                
                // Check if enemy
                if (!BaseAI.IsEnemy(taunter, character)) continue;
                
                // Check range
                float distance = Vector3.Distance(taunterPos, character.transform.position);
                if (distance > range) continue;
                
                // Apply taunt to this enemy
                if (ApplyTauntToEnemy(taunter, character, duration))
                {
                    tauntedCount++;
                }
            }
            
            // Apply "Taunting" buff to the taunter (always apply so we can catch new enemies)
            ApplyTauntingBuff(taunter, duration, range);
            
            if (VerboseLogging)
            {
                Debug.Log($"[TauntManager] {taunter.m_name} taunted {tauntedCount} enemies (will continuously taunt for {duration}s in {range}m range)");
            }
            
            return tauntedCount;
        }
        
        /// <summary>
        /// Applies taunt to a single enemy.
        /// </summary>
        private static bool ApplyTauntToEnemy(Character taunter, Character enemy, float duration)
        {
            if (enemy == null) return false;
            
            var seman = enemy.GetSEMan();
            if (seman == null) return false;
            
            // Create a new taunt effect instance
            var tauntEffect = ScriptableObject.CreateInstance<CompanionTauntEffect>();
            tauntEffect.name = "CompanionTaunted";
            tauntEffect.Taunter = taunter;
            tauntEffect.Duration = duration;
            
            // Remove existing taunt if any (newer taunt replaces)
            int existingHash = "CompanionTaunted".GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            // Apply the taunt
            seman.AddStatusEffect(tauntEffect, true);
            Combat.VikHavnBridge.Taunt(taunter, enemy, duration);

            if (VerboseLogging)
            {
                Debug.Log($"[TauntManager] Applied taunt to {enemy.m_name} from {taunter.m_name}");
            }
            
            return true;
        }
        
        /// <summary>
        /// Applies the "Taunting" buff to the taunter.
        /// </summary>
        private static void ApplyTauntingBuff(Character taunter, float duration, float range = 5f)
        {
            var seman = taunter.GetSEMan();
            if (seman == null) return;
            
            // Create effect instance
            var tauntingEffect = ScriptableObject.CreateInstance<CompanionTauntingEffect>();
            tauntingEffect.name = "CompanionTaunting";
            tauntingEffect.Duration = duration;
            tauntingEffect.TauntRange = range;
            tauntingEffect.TauntingIcon = _tauntingEffectPrefab?.TauntingIcon;
            
            // Remove existing if any
            int existingHash = "CompanionTaunting".GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            // Apply
            seman.AddStatusEffect(tauntingEffect, true);
        }
        
        /// <summary>
        /// Creates a simple fallback icon texture.
        /// </summary>
        public static Sprite CreateFallbackIcon(Color color)
        {
            Texture2D texture = new Texture2D(FallbackIconSize, FallbackIconSize);
            Color[] pixels = new Color[FallbackIconSize * FallbackIconSize];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }
            texture.SetPixels(pixels);
            texture.Apply();
            return Sprite.Create(texture, new Rect(0, 0, FallbackIconSize, FallbackIconSize), new Vector2(0.5f, 0.5f));
        }
        
        /// <summary>
        /// Applies taunt with a shockwave effect that plays an emote and creates a hammer-like AoE.
        /// The shockwave deals minimal damage (0.01) to only hit monsters and apply the taunt effect.
        /// </summary>
        /// <param name="taunter">The companion doing the taunting.</param>
        /// <param name="range">Range of the taunt shockwave (default 5m).</param>
        /// <param name="duration">How long the taunt lasts (default 30 seconds).</param>
        /// <returns>Number of enemies taunted.</returns>
        public static int ApplyTauntWithShockwave(Character taunter, float range = 5f, float duration = 30f)
        {
            if (taunter == null) return 0;
            
            // Make sure effects are registered
            RegisterEffects();
            
            // Start the taunt shockwave coroutine via a helper MonoBehaviour
            var helper = taunter.gameObject.GetComponent<TauntShockwaveHelper>();
            if (helper == null)
            {
                helper = taunter.gameObject.AddComponent<TauntShockwaveHelper>();
            }
            
            helper.ExecuteTauntShockwave(taunter, range, duration);
            
            // Return immediately - actual count will be determined after emote plays
            // For now, return an estimate based on nearby enemies
            return CountNearbyEnemies(taunter, range);
        }
        
        /// <summary>
        /// Counts nearby enemies without applying effects (for estimation).
        /// </summary>
        private static int CountNearbyEnemies(Character taunter, float range)
        {
            int count = 0;
            Vector3 taunterPos = taunter.transform.position;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (character == taunter) continue;
                if (character.IsTamed() || character.IsPlayer()) continue;
                if (!BaseAI.IsEnemy(taunter, character)) continue;
                
                float distance = Vector3.Distance(taunterPos, character.transform.position);
                if (distance <= range)
                {
                    count++;
                }
            }
            
            return count;
        }
    }
    
    /// <summary>
    /// Runs the taunt shockwave: the companion plays a taunting emote (movement frozen, optionally invulnerable), then
    /// fires the sledgehammer secondary's effects with negligible damage to draw aggro, and applies the taunt effect to
    /// nearby enemies.
    /// </summary>
    public class TauntShockwaveHelper : MonoBehaviour
    {
        private Character _character;
        private Humanoid _humanoid;
        private Animator _animator;
        private bool _isExecutingTaunt = false;
        
        // Cached prefab names for sledge effects
        // NOTE: vfx_sledge_hit is the VISIBLE ground slam, fx_sledge_hit is small/invisible
        private const string SledgeHitEffect = "vfx_sledge_hit";
        private const string SledgeHitEffectFallback = "vfx_sledge_hit";
        private const string SledgePrefab = "SledgeStagbreaker";
        
        // Taunt animation duration
        private const float TauntEmoteDuration = 1.5f;
        private const float TauntImpactTime = 0.5f;
        private const string RoarEmote = "roar";
        private const string ComeHereEmote = "comehere";
        private static readonly string[] TauntEmotes = { "flex", "challenge", RoarEmote };
        private const float ComeHereMaxMove = 0.1f;
        private const float EffectGroundOffset = 0.1f;
        private const float AllyShockwaveScale = 0.7f;
        private const float MinimalAggroDamage = 0.01f;
        private const float ShockwaveScaleReferenceRange = 5f;
        private const float MinShockwaveVisualScale = 0.8f;
        private const float MaxShockwaveVisualScale = 2.0f;
        private const float TauntedIndicatorScale = 0.8f;
        private const float DefaultTauntingRange = 5f;

        /// <summary>Whether to grant invulnerability during the taunt animation.</summary>
        public static bool GrantImmunityDuringTaunt = true;
        
        /// <summary>Duration of immunity during taunt (seconds).</summary>
        public static float TauntImmunityDuration = 1.5f;
        
        public static bool VerboseLogging = false;
        
        private void Awake()
        {
            _character = GetComponent<Character>();
            _humanoid = GetComponent<Humanoid>();
            _animator = GetComponentInChildren<Animator>(true);
        }
        
        /// <summary>
        /// Executes the taunt shockwave sequence: emote -> sledge attack VFX -> taunt application.
        /// </summary>
        public void ExecuteTauntShockwave(Character taunter, float range, float duration)
        {
            if (_isExecutingTaunt) return;
            StartCoroutine(TauntShockwaveCoroutine(taunter, range, duration));
        }
        
        private IEnumerator TauntShockwaveCoroutine(Character taunter, float range, float duration)
        {
            _isExecutingTaunt = true;
            
            // Get the state controller for movement freezing
            var stateController = GetComponent<FiresCore.Npc.Movement.CompanionStateController>();
            
            try
            {
                // 1. Enter emote state to freeze movement (follows emote rules)
                if (stateController != null)
                {
                    stateController.TryEnterState(
                        FiresCore.Npc.Movement.CompanionStateController.CompanionState.Emote, 
                        TauntEmoteDuration, 
                        "TauntShockwave");
                    
                    if (VerboseLogging)
                    {
                        Debug.Log($"[TauntShockwave] {taunter.m_name} entered emote state, movement frozen");
                    }
                }
                
                // 2. Apply invulnerability if enabled
                if (GrantImmunityDuringTaunt)
                {
                    ApplyTauntImmunity(taunter, TauntImmunityDuration);
                }
                
                // 3. Play taunting emote (flex or challenge)
                PlayTauntEmote();
                
                // 4. Wait for emote to reach the "impact" moment
                yield return new WaitForSeconds(TauntImpactTime);
                
                // 5. Trigger the sledge hammer shockwave effect at tank's position
                TriggerSledgeShockwave(taunter, range);
                
                // 5b. EXTENDED TAUNT: Spawn shockwaves at all allied companion positions
                // This extends the tank's taunt reach to protect allies who may have enemies near them
                SpawnShockwavesAtAllyPositions(taunter, range, duration);
                
                // 6. Apply taunt to all valid targets in range (monsters only, not buildings/trees/players)
                int tauntedCount = ApplyTauntToNearbyEnemies(taunter, range, duration);
                
                // 6b. Also taunt enemies near allies (using the extended range from shockwaves)
                tauntedCount += TauntEnemiesNearAllies(taunter, range, duration);
                
                // Grant skill XP for each enemy taunted
                var skillSystem = taunter.GetComponent<ArchetypeSkillSystem>();
                if (skillSystem != null && tauntedCount > 0)
                {
                    skillSystem.OnAbilityUsed("taunt");
                    for (int i = 0; i < tauntedCount; i++)
                    {
                        skillSystem.OnAbilityHitEnemy("taunt", null, false);
                    }
                }
                
                // Always log taunt results and show message to player
                Debug.Log($"[TauntShockwave] {taunter.m_name} taunted {tauntedCount} enemies with sledge shockwave (range={range}m, duration={duration}s)");
                
                // Show message to player so they know taunt worked
                if (tauntedCount > 0)
                {
                    var tankController = taunter.GetComponent<CompanionController>();
                    string tankName = tankController?.GetDisplayName() ?? taunter.m_name;
                    MessageHud.instance?.ShowMessage(MessageHud.MessageType.TopLeft, 
                        $"<color=#FFD966>{tankName}</color> TAUNTED <color=#FF6666>{tauntedCount}</color> enemies!");
                }
                
                // 7. Apply the "Taunting" buff to the taunter (with range for continuous re-application)
                if (tauntedCount > 0)
                {
                    ApplyTauntingBuffToTaunter(taunter, duration, range);
                }
                else
                {
                    // Even if no enemies initially, apply the buff so we can catch new ones
                    ApplyTauntingBuffToTaunter(taunter, duration, range);
                }
                
                // 8. Wait for animation to complete
                yield return new WaitForSeconds(TauntEmoteDuration - TauntImpactTime);
            }
            finally
            {
                _isExecutingTaunt = false;
                
                // Exit emote state to restore movement
                if (stateController != null && 
                    stateController.CurrentState == FiresCore.Npc.Movement.CompanionStateController.CompanionState.Emote)
                {
                    stateController.ExitState(FiresCore.Npc.Movement.CompanionStateController.CompanionState.Idle);
                    
                    if (VerboseLogging)
                    {
                        Debug.Log($"[TauntShockwave] {_character?.m_name} exited emote state, movement restored");
                    }
                }
            }
        }
        
        /// <summary>
        /// Spawns shockwave VFX at all allied companion positions within reasonable range.
        /// This creates a visual effect showing the tank's protection extending to allies.
        /// </summary>
        private void SpawnShockwavesAtAllyPositions(Character taunter, float tauntRange, float duration)
        {
            if (taunter == null)
            {
                Debug.LogWarning("[TauntShockwave] SpawnShockwavesAtAllyPositions: taunter is null!");
                return;
            }
            
            var taunterController = taunter.GetComponent<CompanionController>();
            if (taunterController == null)
            {
                Debug.LogWarning($"[TauntShockwave] SpawnShockwavesAtAllyPositions: {taunter.m_name} has no CompanionController!");
                return;
            }
            
            long ownerId = taunterController.ownerPlayerId;
            Vector3 taunterPos = taunter.transform.position;
            const float MaxAllyRange = 20f; // Only spawn at allies within 20m
            
            int allyCount = 0;
            int shockwaveCount = 0;
            
            // Check how many companions are in AllCompanions
            Debug.Log($"[TauntShockwave] Checking {CompanionController.AllCompanions.Count} companions for ally shockwaves (owner={ownerId})");
            
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null || companion.isDefeated) continue;
                if (companion.ownerPlayerId != ownerId) continue;
                if (companion == taunterController) continue; // Skip self
                
                allyCount++;
                
                var companionChar = companion.GetCharacter();
                if (companionChar == null || companionChar.IsDead())
                {
                    Debug.Log($"[TauntShockwave] Ally {companion.companionName} has no character or is dead");
                    continue;
                }
                
                float distToTaunter = Vector3.Distance(taunterPos, companionChar.transform.position);
                if (distToTaunter > MaxAllyRange)
                {
                    Debug.Log($"[TauntShockwave] Ally {companion.companionName} too far ({distToTaunter:F1}m > {MaxAllyRange}m)");
                    continue;
                }
                
                // Spawn shockwave VFX at ally position
                Vector3 allyPos = companionChar.transform.position;
                
                // Get ground position
                if (ZoneSystem.instance != null)
                {
                    float groundHeight;
                    if (ZoneSystem.instance.GetGroundHeight(allyPos, out groundHeight))
                    {
                        allyPos.y = groundHeight + EffectGroundOffset;
                    }
                }
                
                // Spawn the sledge effect at ally position (scaled down slightly)
                // Try multiple prefab names as fallbacks - vfx_sledge_hit is the visible one
                string[] effectNames = { SledgeHitEffect, "fx_eikthyr_stomp", SledgeHitEffectFallback, "fx_shaman_protect" };
                GameObject effectPrefab = null;
                string usedEffectName = null;
                
                foreach (var effectName in effectNames)
                {
                    effectPrefab = ZNetScene.instance?.GetPrefab(effectName);
                    if (effectPrefab != null)
                    {
                        usedEffectName = effectName;
                        break;
                    }
                }
                
                if (effectPrefab != null)
                {
                    var fx = UnityEngine.Object.Instantiate(effectPrefab, allyPos, Quaternion.identity);
                    
                    // Scale down the ally shockwaves slightly (they're secondary effects)
                    fx.transform.localScale = Vector3.one * AllyShockwaveScale;
                    
                    shockwaveCount++;
                    Debug.Log($"[TauntShockwave] Spawned ally shockwave #{shockwaveCount} at {companion.companionName}'s position using {usedEffectName}");
                }
                else
                {
                    // Last resort: Use AbilityFXManager which has its own prefab lookup logic
                    AbilityFXManager.SpawnEffect("vfx_sledge_hit", allyPos, Quaternion.identity, AllyShockwaveScale);
                    shockwaveCount++;
                    Debug.Log($"[TauntShockwave] Spawned ally shockwave #{shockwaveCount} via AbilityFXManager at {companion.companionName}'s position");
                }
            }
            
            Debug.Log($"[TauntShockwave] Ally shockwave summary: {allyCount} allies found, {shockwaveCount} shockwaves spawned");
        }
        
        /// <summary>
        /// Taunts enemies that are near allied companions (within 5m of any ally).
        /// This extends the tank's taunt reach to protect allies who may have enemies near them.
        /// </summary>
        private int TauntEnemiesNearAllies(Character taunter, float range, float duration)
        {
            if (taunter == null) return 0;
            
            var taunterController = taunter.GetComponent<CompanionController>();
            if (taunterController == null) return 0;
            
            long ownerId = taunterController.ownerPlayerId;
            Vector3 taunterPos = taunter.transform.position;
            const float MaxAllyRange = 20f;
            const float AllyTauntRange = 5f; // Taunt enemies within 5m of allies
            
            int tauntedCount = 0;
            ZDOID taunterZDOID = taunter.GetComponent<ZNetView>()?.GetZDO()?.m_uid ?? ZDOID.None;
            HashSet<Character> alreadyTaunted = new HashSet<Character>();
            
            // First, collect all already-taunted enemies to avoid double-counting
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                if (character == null || character.IsDead()) continue;
                if (CompanionTauntEffect.IsTaunted(character))
                {
                    alreadyTaunted.Add(character);
                }
            }
            
            // Now check each ally's surroundings
            foreach (var companion in CompanionController.AllCompanions)
            {
                if (companion == null || companion.isDefeated) continue;
                if (companion.ownerPlayerId != ownerId) continue;
                if (companion == taunterController) continue;
                
                var companionChar = companion.GetCharacter();
                if (companionChar == null || companionChar.IsDead()) continue;
                
                float distToTaunter = Vector3.Distance(taunterPos, companionChar.transform.position);
                if (distToTaunter > MaxAllyRange) continue;
                
                Vector3 allyPos = companionChar.transform.position;
                
                // Find enemies near this ally
                foreach (var character in characters)
                {
                    if (character == null || character.IsDead()) continue;
                    if (character == taunter) continue;
                    if (character.IsPlayer()) continue;
                    if (!BaseAI.IsEnemy(taunter, character)) continue;
                    if (alreadyTaunted.Contains(character)) continue;
                    
                    // Skip friendly tamed creatures
                    if (character.IsTamed())
                    {
                        var ctrl = character.GetComponent<CompanionController>();
                        if (ctrl == null || !BaseAI.IsEnemy(taunter, character)) continue;
                    }
                    
                    float distToAlly = Vector3.Distance(allyPos, character.transform.position);
                    if (distToAlly > AllyTauntRange) continue;
                    
                    // Apply minimal damage to draw aggro
                    HitData hitData = new HitData();
                    hitData.m_damage.m_blunt = MinimalAggroDamage;
                    hitData.m_point = character.transform.position;
                    hitData.m_dir = (character.transform.position - taunterPos).normalized;
                    hitData.m_pushForce = 0f;
                    hitData.m_backstabBonus = 1f;
                    hitData.m_staggerMultiplier = 0f;
                    hitData.m_dodgeable = false;
                    hitData.m_blockable = false;
                    hitData.m_attacker = taunterZDOID;
                    
                    character.Damage(hitData);
                    
                    // Apply taunt status effect
                    if (ApplyTauntToEnemy(taunter, character, duration))
                    {
                        tauntedCount++;
                        alreadyTaunted.Add(character);
                        
                        if (VerboseLogging)
                        {
                            Debug.Log($"[TauntShockwave] Extended taunt to {character.m_name} near ally {companion.companionName}");
                        }
                    }
                }
            }
            
            return tauntedCount;
        }
        
        /// <summary>
        /// Applies brief invulnerability to the taunter during the taunt animation.
        /// This protects them while they're locked in the emote.
        /// </summary>
        private void ApplyTauntImmunity(Character taunter, float immunityDuration)
        {
            if (taunter == null) return;
            
            var seman = taunter.GetSEMan();
            if (seman == null) return;
            
            // Create and apply invulnerability effect
            var invulnEffect = ScriptableObject.CreateInstance<StatusEffects.Common.InvulnerableEffect>();
            invulnEffect.name = "TauntImmunity";
            invulnEffect.Duration = immunityDuration;
            invulnEffect.EffectIcon = TauntManager.CreateFallbackIcon(new Color(0.8f, 0.6f, 0.2f)); // Golden color
            
            // Remove existing if any
            int existingHash = "TauntImmunity".GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(invulnEffect, true);
            
            if (VerboseLogging)
            {
                Debug.Log($"[TauntShockwave] {taunter.m_name} granted {immunityDuration}s immunity during taunt");
            }
        }
        
        /// <summary>
        /// Plays a taunting emote animation (flex, challenge, or roar).
        /// </summary>
        private void PlayTauntEmote()
        {
            var zanim = GetComponent<ZSyncAnimation>();
            if (Combat.VikHavnBridge.Installed)
            {
                var roar = PlayerAnimationCatalog.Find(RoarEmote);
                if (PlayerAnimationCatalog.Play(zanim, _animator, roar))
                    StartCoroutine(ComeHereAfterRoar(roar.Seconds));
                return;
            }
            PlayerAnimationCatalog.Play(zanim, _animator, PlayerAnimationCatalog.Find(TauntEmotes[UnityEngine.Random.Range(0, TauntEmotes.Length)]));
        }

        private IEnumerator ComeHereAfterRoar(float roarSeconds)
        {
            yield return new WaitForSeconds(roarSeconds);
            if (_character == null || _character.IsDead() || _character.GetMoveDir().magnitude >= ComeHereMaxMove) yield break;
            PlayerAnimationCatalog.Play(GetComponent<ZSyncAnimation>(), _animator, PlayerAnimationCatalog.Find(ComeHereEmote));
        }
        
        /// <summary>
        /// Triggers the sledge hammer shockwave effect using Valheim's native VFX.
        /// This spawns the visual effect without the full attack damage.
        /// </summary>
        private void TriggerSledgeShockwave(Character taunter, float range)
        {
            Vector3 position = taunter.transform.position;
            
            // Get ground position for the effect
            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (ZoneSystem.instance.GetGroundHeight(position, out groundHeight))
                {
                    position.y = groundHeight + EffectGroundOffset;
                }
            }
            
            // Try to spawn the sledge hit effect (this is what creates the shockwave visual)
            // Try multiple names in order of preference
            string[] effectNames = { SledgeHitEffect, "fx_eikthyr_stomp", SledgeHitEffectFallback, "fx_shaman_protect" };
            GameObject effectPrefab = null;
            string usedEffectName = null;
            
            foreach (var effectName in effectNames)
            {
                effectPrefab = ZNetScene.instance?.GetPrefab(effectName);
                if (effectPrefab != null)
                {
                    usedEffectName = effectName;
                    break;
                }
            }
            
            if (effectPrefab != null)
            {
                // Spawn the effect - this handles VFX, SFX, and the visual shockwave ring
                var fx = UnityEngine.Object.Instantiate(effectPrefab, position, Quaternion.identity);
                
                // Scale based on range
                float scale = Mathf.Clamp(range / ShockwaveScaleReferenceRange, MinShockwaveVisualScale, MaxShockwaveVisualScale);
                fx.transform.localScale = Vector3.one * scale;
                
                Debug.Log($"[TauntShockwave] Spawned {usedEffectName} at {position} (scale={scale:F1})");
            }
            else
            {
                Debug.LogWarning($"[TauntShockwave] Could not find any shockwave effect prefab!");
            }
        }
        
        /// <summary>
        /// Applies taunt and minimal damage to all valid enemies in range.
        /// Only affects monsters and enemy companions - NOT buildings, trees, players, or friendly tamed creatures.
        /// </summary>
        private int ApplyTauntToNearbyEnemies(Character taunter, float range, float duration)
        {
            int tauntedCount = 0;
            Vector3 taunterPos = taunter.transform.position;
            ZDOID taunterZDOID = taunter.GetComponent<ZNetView>()?.GetZDO()?.m_uid ?? ZDOID.None;
            
            var characters = Character.GetAllCharacters();
            foreach (var character in characters)
            {
                // Skip invalid targets
                if (character == null || character.IsDead()) continue;
                if (character == taunter) continue;
                if (character.IsPlayer()) continue; // Never hit players
                
                // Only hit enemies (hostile monsters or enemy companions)
                if (!BaseAI.IsEnemy(taunter, character)) continue;
                
                // Skip friendly tamed creatures (but allow enemy companions in PvP)
                if (character.IsTamed())
                {
                    var companionController = character.GetComponent<CompanionController>();
                    if (companionController == null || !BaseAI.IsEnemy(taunter, character))
                    {
                        continue;
                    }
                }
                
                // Check range
                float distance = Vector3.Distance(taunterPos, character.transform.position);
                if (distance > range) continue;
                
                // Apply minimal damage (0.01) to register as a hit and draw aggro
                // This damage is so low it won't actually harm anything
                HitData hitData = new HitData();
                hitData.m_damage.m_blunt = MinimalAggroDamage; // Minimal blunt damage like a hammer tap
                hitData.m_point = character.transform.position;
                hitData.m_dir = (character.transform.position - taunterPos).normalized;
                hitData.m_pushForce = 0f; // No knockback
                hitData.m_backstabBonus = 1f;
                hitData.m_staggerMultiplier = 0f; // No stagger
                hitData.m_dodgeable = false; // Can't dodge taunt
                hitData.m_blockable = false; // Can't block taunt
                hitData.m_attacker = taunterZDOID;
                
                // Apply the hit to draw aggro
                character.Damage(hitData);
                
                // Apply taunt status effect
                if (ApplyTauntToEnemy(taunter, character, duration))
                {
                    tauntedCount++;
                    
                    // VISUAL FEEDBACK: Spawn a marker on the taunted enemy so player can see it worked
                    SpawnTauntedIndicator(character);
                }
            }
            
            return tauntedCount;
        }
        
        /// <summary>
        /// Spawns a visual indicator above a taunted enemy.
        /// </summary>
        private void SpawnTauntedIndicator(Character enemy)
        {
            if (enemy == null) return;

            Vector3 headPos = enemy.transform.position + Vector3.up * 2f;

            // Try to spawn a visible indicator
            string[] indicatorEffects = { "fx_crit", "vfx_blocked" };
            foreach (var effectName in indicatorEffects)
            {
                var prefab = ZNetScene.instance?.GetPrefab(effectName);
                if (prefab != null)
                {
                    var fx = UnityEngine.Object.Instantiate(prefab, headPos, Quaternion.identity);
                    fx.transform.localScale = Vector3.one * TauntedIndicatorScale;
                    // The vanilla prefabs here carry a ZNetView, so a raw Object.Destroy
                    // would leave a stale entry in ZNetScene.m_instances and trip
                    // ZNetSceneStaleInstanceDiagnostic. Route through SafeDestroy after
                    // the 2s display window.
                    StartCoroutine(SafeDestroyAfter(fx, 2f));
                    return;
                }
            }
        }

        private static IEnumerator SafeDestroyAfter(GameObject go, float seconds)
        {
            yield return new WaitForSeconds(seconds);
            FiresCore.Net.NetworkObjectHelper.SafeDestroy(go);
        }
        
        /// <summary>
        /// Applies taunt status effect to a single enemy.
        /// </summary>
        private bool ApplyTauntToEnemy(Character taunter, Character enemy, float duration)
        {
            if (enemy == null) return false;
            
            var seman = enemy.GetSEMan();
            if (seman == null) return false;
            
            var tauntEffect = ScriptableObject.CreateInstance<CompanionTauntEffect>();
            tauntEffect.name = "CompanionTaunted";
            tauntEffect.Taunter = taunter;
            tauntEffect.Duration = duration;
            
            // Remove existing taunt if any (newer taunt replaces)
            int existingHash = "CompanionTaunted".GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(tauntEffect, true);
            Combat.VikHavnBridge.Taunt(taunter, enemy, duration);
            return true;
        }

        /// <summary>
        /// Applies the "Taunting" buff to show the taunter they're drawing aggro.
        /// </summary>
        private void ApplyTauntingBuffToTaunter(Character taunter, float duration)
        {
            ApplyTauntingBuffToTaunter(taunter, duration, DefaultTauntingRange); // Default range
        }
        
        /// <summary>
        /// Applies the "Taunting" buff to show the taunter they're drawing aggro.
        /// </summary>
        private void ApplyTauntingBuffToTaunter(Character taunter, float duration, float range)
        {
            var seman = taunter.GetSEMan();
            if (seman == null) return;
            
            var tauntingEffect = ScriptableObject.CreateInstance<CompanionTauntingEffect>();
            tauntingEffect.name = "CompanionTaunting";
            tauntingEffect.Duration = duration;
            tauntingEffect.TauntRange = range;
            
            int existingHash = "CompanionTaunting".GetStableHashCode();
            if (seman.HaveStatusEffect(existingHash))
            {
                seman.RemoveStatusEffect(existingHash, true);
            }
            
            seman.AddStatusEffect(tauntingEffect, true);
        }
    }
}
