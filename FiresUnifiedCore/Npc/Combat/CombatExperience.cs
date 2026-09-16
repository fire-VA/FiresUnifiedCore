using UnityEngine;
using System;
using System.Collections.Generic;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Level-based combat skill: each level adds a small bonus to attack prediction, block, parry and dodge
    /// timing and to retreating from dangerous enemies, compounding from barely functional at 0 to near-perfect
    /// at 100. Queried by BlockingBehavior, DodgeBehavior and EnemyAttackRecognition, and combined with
    /// CombatMemory so higher levels also avoid enemies that killed the companion before.
    /// </summary>
    public class CombatExperience : MonoBehaviour
    {
        private const float BaseParryWindowSeconds = 0.25f;
        private const float BaseParryCounterChance = 0.3f;
        private const int MaxKilledByStacks = 3;
        private const float BlockCautionDangerThreshold = 0.3f;
        private const float DangerBlockChanceScale = 0.1f;
        private const int ExperiencedEnemyMinEncounters = 5;
        private const float ParryExperienceBonusPerEncounter = 0.01f;
        private const float MaxParryExperienceBonus = 0.15f;
        private const int FamiliarEnemyMinEncounters = 3;
        private const float CounterFamiliarityBonusPerEncounter = 0.02f;
        private const float MaxCounterFamiliarityBonus = 0.1f;
        private const float RevengeCounterBonusPerKill = 0.05f;
        private const float MaxOneShotterDodgeBonus = 0.2f;
        private const float DangerDodgeChanceScale = 0.1f;
        private const float AnticipationBonusPerEncounter = 0.01f;
        private const float MaxAnticipationFamiliarityBonus = 0.1f;
        private const float MaxOneShotterRetreatThreshold = 0.6f;
        private const float MaxDangerousEnemyRetreatThreshold = 0.4f;

        #region Settings - Per-Level Bonuses (Small but Cumulative)
        
        [Header("Block Improvements Per Level")]
        [Tooltip("Block success chance bonus per level (0.008 = 0.8% per level, 80% at level 100)")]
        public float blockChancePerLevel = 0.008f;
        
        [Tooltip("Block anticipation window bonus per level in seconds (0.003 = 0.3s more anticipation at level 100)")]
        public float blockAnticipationPerLevel = 0.003f;
        
        [Header("Parry Improvements Per Level")]
        [Tooltip("Parry success chance bonus per level (0.005 = 0.5% per level, 50% at level 100)")]
        public float parryChancePerLevel = 0.005f;
        
        [Tooltip("Parry window expansion per level in seconds (0.001 = 0.1s wider window at level 100)")]
        public float parryWindowPerLevel = 0.001f;
        
        [Tooltip("Parry counter-attack chance bonus per level (0.005 = 0.5% per level, 50% extra at level 100)")]
        public float parryCounterChancePerLevel = 0.005f;
        
        [Header("Dodge Improvements Per Level")]
        [Tooltip("Dodge timing bonus per level (0.004 = better reaction timing)")]
        public float dodgeTimingPerLevel = 0.004f;
        
        [Tooltip("Dodge direction accuracy per level (0.006 = 0.6% better direction choice per level)")]
        public float dodgeDirectionPerLevel = 0.006f;
        
        [Header("Attack Recognition Per Level")]
        [Tooltip("Attack detection range bonus per level in meters (0.05 = 5m more range at level 100)")]
        public float attackDetectionRangePerLevel = 0.05f;
        
        [Tooltip("Attack prediction accuracy per level (0.007 = 0.7% better prediction per level)")]
        public float attackPredictionPerLevel = 0.007f;
        
        [Header("Survival Tactics Per Level")]
        [Tooltip("Retreat threshold increase per level (0.003 = retreat 30% earlier at level 100)")]
        public float retreatThresholdPerLevel = 0.003f;
        
        [Tooltip("Danger recognition per level (0.005 = 0.5% better danger assessment per level)")]
        public float dangerRecognitionPerLevel = 0.005f;
        
        [Header("Combat Memory Synergy")]
        [Tooltip("Bonus to defense against enemies that killed us, per level (0.002 = 20% extra defense at level 100)")]
        public float killerDefenseBonusPerLevel = 0.002f;
        
        [Tooltip("Learning rate multiplier - how fast we adapt to dangerous enemies")]
        public float learningRateMultiplier = 1.5f;
        
        [Header("Base Values (Level 0)")]
        [Tooltip("Base block success chance at level 0")]
        public float baseBlockChance = 0.2f;
        
        [Tooltip("Base parry success chance at level 0")]
        public float baseParryChance = 0.1f;
        
        [Tooltip("Base dodge success chance at level 0")]
        public float baseDodgeChance = 0.15f;
        
        [Tooltip("Base attack anticipation window in seconds")]
        public float baseAnticipationWindow = 0.15f;
        
        [Tooltip("Base retreat health threshold")]
        public float baseRetreatThreshold = 0.15f;
        
        #endregion
        
        #region Components
        
        private CompanionController _companion;
        private CompanionProgression _progression;
        private CombatMemory _combatMemory;
        private Character _character;
        
        #endregion
        
        #region State
        
        // Cached level for performance
        private int _cachedLevel;
        private float _lastLevelCheck;
        private const float LevelCheckInterval = 1f;
        
        // Combat statistics for adaptive learning
        private int _totalBlockAttempts;
        private int _successfulBlocks;
        private int _totalDodgeAttempts;
        private int _successfulDodges;
        private int _totalParryAttempts;
        private int _successfulParries;
        
        // Recent combat tracking
        private float _lastDamageTime;
        private float _lastBlockTime;
        private float _lastDodgeTime;
        private float _lastParryTime;
        
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region Properties - Calculated Bonuses
        
        /// <summary>Current companion level (0-100).</summary>
        public int Level
        {
            get
            {
                if (Time.time - _lastLevelCheck > LevelCheckInterval)
                {
                    _lastLevelCheck = Time.time;
                    _cachedLevel = _progression?.Level ?? 0;
                }
                return _cachedLevel;
            }
        }
        
        /// <summary>Block success chance modifier (0-1 scale bonus).</summary>
        public float BlockChanceBonus => Level * blockChancePerLevel;
        
        /// <summary>Total block chance including base.</summary>
        public float TotalBlockChance => Mathf.Clamp01(baseBlockChance + BlockChanceBonus);
        
        /// <summary>Parry success chance modifier.</summary>
        public float ParryChanceBonus => Level * parryChancePerLevel;
        
        /// <summary>Total parry chance including base.</summary>
        public float TotalParryChance => Mathf.Clamp01(baseParryChance + ParryChanceBonus);
        
        /// <summary>Dodge success chance modifier.</summary>
        public float DodgeChanceBonus => Level * dodgeTimingPerLevel;
        
        /// <summary>Total dodge chance including base.</summary>
        public float TotalDodgeChance => Mathf.Clamp01(baseDodgeChance + DodgeChanceBonus);
        
        /// <summary>Attack anticipation window in seconds.</summary>
        public float AnticipationWindow => baseAnticipationWindow + (Level * blockAnticipationPerLevel);
        
        /// <summary>Parry window in seconds (wider at higher levels).</summary>
        public float ParryWindow => BaseParryWindowSeconds + (Level * parryWindowPerLevel);
        
        /// <summary>Parry counter-attack chance bonus (0-0.5 at level 100).</summary>
        public float ParryCounterChanceBonus => Level * parryCounterChancePerLevel;
        
        /// <summary>Total parry counter-attack chance (0.3 base + level bonus).</summary>
        public float TotalParryCounterChance => Mathf.Clamp01(BaseParryCounterChance + ParryCounterChanceBonus);
        
        /// <summary>Retreat health threshold (0-1, retreats earlier at higher levels).</summary>
        public float RetreatThreshold => baseRetreatThreshold + (Level * retreatThresholdPerLevel);
        
        /// <summary>Attack detection range bonus in meters.</summary>
        public float AttackDetectionRangeBonus => Level * attackDetectionRangePerLevel;
        
        /// <summary>Danger recognition multiplier (1.0 at level 0, up to 1.7 at level 100).</summary>
        public float DangerRecognitionMultiplier => 1f + (Level * dangerRecognitionPerLevel);
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _progression = GetComponent<CompanionProgression>();
            _combatMemory = GetComponent<CombatMemory>();
            _character = GetComponent<Character>();
        }
        
        private void Start()
        {
            // Subscribe to progression changes
            if (_progression != null)
            {
                _progression.OnAttributeChanged += OnLevelChanged;
            }
            
            // Initialize cached level
            _cachedLevel = _progression?.Level ?? 0;
            
            if (VerboseLogging)
            {
                Debug.Log($"[CombatExperience] Initialized for {_companion?.companionName} at level {_cachedLevel}");
                LogCurrentBonuses();
            }
        }
        
        private void OnDestroy()
        {
            if (_progression != null)
            {
                _progression.OnAttributeChanged -= OnLevelChanged;
            }
        }
        
        private void OnLevelChanged()
        {
            int oldLevel = _cachedLevel;
            _cachedLevel = _progression?.Level ?? 0;
            
            if (_cachedLevel != oldLevel && VerboseLogging)
            {
                Debug.Log($"[CombatExperience] {_companion?.companionName} level changed: {oldLevel} -> {_cachedLevel}");
                LogCurrentBonuses();
            }
        }
        
        #endregion
        
        #region Combat Modifiers - Used by Other Combat Systems
        
        /// <summary>
        /// Gets the modified block chance against a specific enemy.
        /// Higher levels = better blocking. Known killers = extra caution bonus.
        /// </summary>
        public float GetBlockChanceAgainst(Character enemy)
        {
            float baseChance = TotalBlockChance;
            
            // Bonus against enemies that killed us before
            if (_combatMemory != null && enemy != null)
            {
                var memory = _combatMemory.GetMemory(enemy);
                if (memory != null && memory.TimesKilledBy > 0)
                {
                    // We learned from dying to this enemy - block more effectively
                    float killerBonus = Level * killerDefenseBonusPerLevel;
                    killerBonus *= Mathf.Min(memory.TimesKilledBy, MaxKilledByStacks); // Cap at 3x
                    baseChance += killerBonus;
                    
                    if (VerboseLogging)
                    {
                        Debug.Log($"[CombatExperience] Killer bonus vs {enemy.m_name}: +{killerBonus:P0} block chance");
                    }
                }
                
                // Also factor in overall danger level
                float dangerLevel = _combatMemory.GetEnemyDangerLevel(enemy);
                if (dangerLevel > BlockCautionDangerThreshold)
                {
                    // More cautious against known dangerous enemies
                    baseChance += dangerLevel * DangerBlockChanceScale * DangerRecognitionMultiplier;
                }
            }
            
            return Mathf.Clamp01(baseChance);
        }
        
        /// <summary>
        /// Gets the modified parry chance against a specific enemy.
        /// Parrying is harder but more rewarding - requires higher level to be reliable.
        /// </summary>
        public float GetParryChanceAgainst(Character enemy)
        {
            float baseChance = TotalParryChance;
            
            // Parry bonus against known enemies (we learn their attack patterns)
            if (_combatMemory != null && enemy != null)
            {
                var memory = _combatMemory.GetMemory(enemy);
                if (memory != null && memory.DamageInstanceCount > ExperiencedEnemyMinEncounters)
                {
                    // We've fought this enemy type many times - better at parrying
                    float experienceBonus = Mathf.Min(memory.DamageInstanceCount * ParryExperienceBonusPerEncounter, MaxParryExperienceBonus);
                    baseChance += experienceBonus * (Level / 100f); // Scales with level
                }
            }
            
            return Mathf.Clamp01(baseChance);
        }
        
        /// <summary>
        /// Gets the parry counter-attack chance against a specific enemy.
        /// Higher levels = more likely to capitalize on parries.
        /// Known enemies = even better counter timing.
        /// </summary>
        public float GetParryCounterChanceAgainst(Character enemy)
        {
            float baseChance = TotalParryCounterChance;
            
            // Bonus against familiar enemies (we know how to exploit their stagger)
            if (_combatMemory != null && enemy != null)
            {
                var memory = _combatMemory.GetMemory(enemy);
                if (memory != null && memory.DamageInstanceCount > FamiliarEnemyMinEncounters)
                {
                    // We've parried this enemy type before - better counter timing
                    float familiarityBonus = Mathf.Min(memory.DamageInstanceCount * CounterFamiliarityBonusPerEncounter, MaxCounterFamiliarityBonus);
                    baseChance += familiarityBonus;
                }
                
                // Extra counter chance against enemies that killed us (revenge!)
                if (memory != null && memory.TimesKilledBy > 0)
                {
                    baseChance += RevengeCounterBonusPerKill * Mathf.Min(memory.TimesKilledBy, MaxKilledByStacks);
                }
            }
            
            return Mathf.Clamp01(baseChance);
        }
        
        /// <summary>
        /// Gets the modified dodge chance against a specific enemy.
        /// </summary>
        public float GetDodgeChanceAgainst(Character enemy)
        {
            float baseChance = TotalDodgeChance;
            
            // Dodge bonus against one-shotters (we REALLY want to avoid those hits)
            if (_combatMemory != null && _combatMemory.IsKnownOneShotter(enemy))
            {
                // Extra dodge chance against enemies that can one-shot us
                float oneshotBonus = MaxOneShotterDodgeBonus * (Level / 100f);
                baseChance += oneshotBonus;
                
                if (VerboseLogging)
                {
                    Debug.Log($"[CombatExperience] One-shot dodge bonus vs {enemy?.m_name}: +{oneshotBonus:P0}");
                }
            }
            
            // General danger bonus
            if (_combatMemory != null && enemy != null)
            {
                float dangerLevel = _combatMemory.GetEnemyDangerLevel(enemy);
                baseChance += dangerLevel * DangerDodgeChanceScale;
            }
            
            return Mathf.Clamp01(baseChance);
        }
        
        /// <summary>
        /// Gets the anticipation window for blocking/parrying.
        /// Higher levels = earlier reaction to enemy attacks.
        /// </summary>
        public float GetAnticipationWindow(Character enemy)
        {
            float window = AnticipationWindow;
            
            // Longer anticipation against known enemies (we recognize their tells)
            if (_combatMemory != null && enemy != null)
            {
                var memory = _combatMemory.GetMemory(enemy);
                if (memory != null && memory.DamageInstanceCount > FamiliarEnemyMinEncounters)
                {
                    // We've seen this enemy attack before
                    float familiarityBonus = Mathf.Min(memory.DamageInstanceCount * AnticipationBonusPerEncounter, MaxAnticipationFamiliarityBonus);
                    window += familiarityBonus;
                }
            }
            
            return window;
        }
        
        /// <summary>
        /// Gets the retreat threshold for survival.
        /// Higher levels = smarter about when to retreat.
        /// </summary>
        public float GetRetreatThreshold(Character enemy)
        {
            float threshold = RetreatThreshold;
            
            // Earlier retreat against dangerous enemies
            if (_combatMemory != null && enemy != null)
            {
                if (_combatMemory.IsKnownOneShotter(enemy))
                {
                    // Retreat at 60% health against one-shotters (high level)
                    threshold = Mathf.Max(threshold, MaxOneShotterRetreatThreshold * (Level / 100f));
                }
                else if (_combatMemory.IsKnownDangerous(enemy))
                {
                    // Retreat earlier against known dangerous enemies
                    threshold = Mathf.Max(threshold, MaxDangerousEnemyRetreatThreshold * (Level / 100f));
                }
            }
            
            return Mathf.Clamp01(threshold);
        }
        
        /// <summary>
        /// Gets the optimal dodge direction based on experience.
        /// Higher levels = better at choosing the right direction.
        /// </summary>
        public Vector3 GetOptimalDodgeDirection(Character enemy, Vector3 defaultDirection)
        {
            if (enemy == null) return defaultDirection;
            
            // At low levels, just use the default direction
            float directionAccuracy = Level * dodgeDirectionPerLevel;
            if (UnityEngine.Random.value > directionAccuracy)
            {
                return defaultDirection; // Random direction
            }
            
            // At higher levels, choose smarter directions
            Vector3 toEnemy = (enemy.transform.position - transform.position).normalized;
            Vector3 enemyRight = Vector3.Cross(Vector3.up, toEnemy);
            
            // Prefer to dodge perpendicular to enemy facing
            Vector3 enemyForward = enemy.transform.forward;
            Vector3 perpendicular = Vector3.Cross(Vector3.up, enemyForward);
            
            // Choose the perpendicular direction that moves us away from enemy's weapon side
            // (Most enemies are right-handed, so dodge to their left)
            if (Vector3.Dot(perpendicular, enemyRight) > 0)
            {
                perpendicular = -perpendicular;
            }
            
            // Blend between random and optimal based on level
            return Vector3.Lerp(defaultDirection, perpendicular.normalized, directionAccuracy).normalized;
        }
        
        /// <summary>
        /// Gets a combined combat modifier for all defensive actions.
        /// Used for quick overall assessment.
        /// </summary>
        public CombatModifier GetCombatModifier(Character enemy)
        {
            return new CombatModifier
            {
                BlockChance = GetBlockChanceAgainst(enemy),
                ParryChance = GetParryChanceAgainst(enemy),
                DodgeChance = GetDodgeChanceAgainst(enemy),
                AnticipationWindow = GetAnticipationWindow(enemy),
                RetreatThreshold = GetRetreatThreshold(enemy),
                Level = Level
            };
        }
        
        #endregion
        
        #region Combat Event Tracking
        
        /// <summary>
        /// Called when a block is attempted.
        /// </summary>
        public void OnBlockAttempt(bool success)
        {
            _totalBlockAttempts++;
            if (success)
            {
                _successfulBlocks++;
                _lastBlockTime = Time.time;
            }
        }
        
        /// <summary>
        /// Called when a dodge is attempted.
        /// </summary>
        public void OnDodgeAttempt(bool success)
        {
            _totalDodgeAttempts++;
            if (success)
            {
                _successfulDodges++;
                _lastDodgeTime = Time.time;
            }
        }
        
        /// <summary>
        /// Called when a parry is attempted.
        /// </summary>
        public void OnParryAttempt(bool success)
        {
            _totalParryAttempts++;
            if (success)
            {
                _successfulParries++;
                _lastParryTime = Time.time;
            }
        }
        
        /// <summary>
        /// Called when damage is taken (for tracking).
        /// </summary>
        public void OnDamageTaken(float damage, Character attacker)
        {
            _lastDamageTime = Time.time;
        }
        
        #endregion
        
        #region Statistics
        
        /// <summary>Block success rate (0-1).</summary>
        public float BlockSuccessRate => _totalBlockAttempts > 0 
            ? (float)_successfulBlocks / _totalBlockAttempts 
            : 0f;
        
        /// <summary>Dodge success rate (0-1).</summary>
        public float DodgeSuccessRate => _totalDodgeAttempts > 0 
            ? (float)_successfulDodges / _totalDodgeAttempts 
            : 0f;
        
        /// <summary>Parry success rate (0-1).</summary>
        public float ParrySuccessRate => _totalParryAttempts > 0 
            ? (float)_successfulParries / _totalParryAttempts 
            : 0f;
        
        /// <summary>Gets a summary of combat experience stats.</summary>
        public string GetStatsSummary()
        {
            return $"Level {Level} Combat Experience:\n" +
                $"  Block: {TotalBlockChance:P0} ({_successfulBlocks}/{_totalBlockAttempts})\n" +
                $"  Parry: {TotalParryChance:P0} ({_successfulParries}/{_totalParryAttempts})\n" +
                $"  Dodge: {TotalDodgeChance:P0} ({_successfulDodges}/{_totalDodgeAttempts})\n" +
                $"  Anticipation: {AnticipationWindow:F2}s\n" +
                $"  Retreat at: {RetreatThreshold:P0} HP";
        }
        
        #endregion
        
        #region Debug
        
        private void LogCurrentBonuses()
        {
            Debug.Log($"[CombatExperience] Level {Level} bonuses:\n" +
                $"  Block: {baseBlockChance:P0} + {BlockChanceBonus:P0} = {TotalBlockChance:P0}\n" +
                $"  Parry: {baseParryChance:P0} + {ParryChanceBonus:P0} = {TotalParryChance:P0}\n" +
                $"  Dodge: {baseDodgeChance:P0} + {DodgeChanceBonus:P0} = {TotalDodgeChance:P0}\n" +
                $"  Anticipation: {AnticipationWindow:F2}s\n" +
                $"  Parry Window: {ParryWindow:F2}s\n" +
                $"  Retreat Threshold: {RetreatThreshold:P0}");
        }
        
        #endregion
        
        #region Data Structures
        
        /// <summary>
        /// Combined combat modifier data for quick access.
        /// </summary>
        public struct CombatModifier
        {
            public float BlockChance;
            public float ParryChance;
            public float DodgeChance;
            public float AnticipationWindow;
            public float RetreatThreshold;
            public int Level;
        }
        
        #endregion
    }
}
