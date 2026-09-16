using UnityEngine;
using System;

namespace FiresCore.Npc
{
    /// <summary>
    /// Manages companion stat pools: Health, Stamina, and Eitr.
    /// Works similarly to player stats with regeneration and consumption.
    /// Integrates with CompanionProgression for attribute bonuses.
    /// </summary>
    public class CompanionStats : MonoBehaviour
    {
        private const float FoodLoadSettleDelay = 0.75f;
        private const float DefaultHealthBonusPerPoint = 5f;
        private const float HealthValidationTolerance = 5f;
        private const float NearFullRatio = 0.95f;
        private const float MinScaleHealthMultiplier = 0.7f;
        private const float MaxScaleHealthMultiplier = 1.5f;
        private const float StaminaSkillMaxReduction = 0.33f;
        private const float EitrSkillMaxReduction = 0.25f;

        #region Settings
        
        [Header("Base Stats")]
        [Tooltip("Base maximum health before attribute bonuses")]
        public float baseMaxHealth = 100f;
        [Tooltip("Base maximum stamina before attribute bonuses")]
        public float baseMaxStamina = 100f;
        [Tooltip("Base maximum eitr before attribute bonuses")]
        public float baseMaxEitr = 100f;
        
        [Header("Regeneration Rates")]
        [Tooltip("Health regeneration per second (out of combat)")]
        public float healthRegenPerSecond = 2f;
        [Tooltip("Stamina regeneration per second")]
        public float staminaRegenPerSecond = 10f;
        [Tooltip("Eitr regeneration per second")]
        public float eitrRegenPerSecond = 5f;
        
        [Header("Combat Regen Modifiers")]
        [Tooltip("Health regen multiplier during combat")]
        public float combatHealthRegenMultiplier = 0.25f;
        [Tooltip("Stamina regen multiplier during combat")]
        public float combatStaminaRegenMultiplier = 0.5f;
        [Tooltip("Eitr regen multiplier during combat")]
        public float combatEitrRegenMultiplier = 0.5f;
        
        [Header("Regen Delays")]
        [Tooltip("Seconds after stamina use before regen starts")]
        public float staminaRegenDelay = 1f;
        [Tooltip("Seconds after eitr use before regen starts")]
        public float eitrRegenDelay = 2f;
        
        #endregion
        
        #region State
        
        private CompanionController _companion;
        private CompanionProgression _progression;
        private CompanionCombatMovement _combatMovement;
        private CompanionConsumables _consumables;
        private Character _character;
        private ZNetView _nview;
        
        // Current stat values - WE TRACK THESE OURSELVES
        // Character.GetHealth() caps at its internal max which doesn't include food bonuses
        // So we maintain our own values and sync them via ZDO
        private float _currentHealth;
        private float _currentStamina;
        private float _currentEitr;
        
        // Cached max values (recalculated when attributes change)
        private float _maxHealth;
        private float _maxStamina;
        private float _maxEitr;
        
        // Archetype stamina multiplier (set by ArchetypeController)
        private float _archetypeStaminaMultiplier = 1.0f;

        // Archetype health + move-speed multipliers (set by ArchetypeController, mirror stamina).
        private float _archetypeHealthMultiplier = 1.0f;
        private float _archetypeSpeedMultiplier = 1.0f;
        // Cached base walk/run speed so the speed multiplier is applied to the base, not compounded.
        private float _baseWalkSpeed = -1f;
        private float _baseRunSpeed = -1f;

        // Track when we last synced to ZDO
        private float _lastZdoSyncTime;
        
        // Regen timing
        private float _lastStaminaUseTime;
        private float _lastEitrUseTime;
        
        // Death tracking
        private int _deathCount;
        private float _lastDeathTime;
        
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region Properties
        
        /// <summary>Current health - uses our tracked value which properly reflects food bonuses</summary>
        public float CurrentHealth => _currentHealth;
        
        /// <summary>Maximum health with attribute bonuses</summary>
        public float MaxHealth => _maxHealth;
        
        /// <summary>Health as percentage (0-1)</summary>
        public float HealthPercentage => _maxHealth > 0 ? CurrentHealth / _maxHealth : 0f;
        
        /// <summary>Current stamina</summary>
        public float CurrentStamina => _currentStamina;
        
        /// <summary>Maximum stamina with attribute bonuses</summary>
        public float MaxStamina => _maxStamina;
        
        /// <summary>Stamina as percentage (0-1)</summary>
        public float StaminaPercentage => _maxStamina > 0 ? _currentStamina / _maxStamina : 0f;
        
        /// <summary>Current eitr</summary>
        public float CurrentEitr => _currentEitr;
        
        /// <summary>Maximum eitr with attribute bonuses</summary>
        public float MaxEitr => _maxEitr;
        
        /// <summary>Eitr as percentage (0-1)</summary>
        public float EitrPercentage => _maxEitr > 0 ? _currentEitr / _maxEitr : 0f;
        
        /// <summary>Total number of times this companion has died</summary>
        public int DeathCount => _deathCount;
        
        /// <summary>Whether currently in combat</summary>
        private bool IsInCombat => _combatMovement != null && _combatMovement.IsInCombat;
        
        #endregion
        
        #region Unity Lifecycle
        
        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _progression = GetComponent<CompanionProgression>();
            _combatMovement = GetComponent<CompanionCombatMovement>();
            _consumables = GetComponent<CompanionConsumables>();
            _character = GetComponent<Character>();
            _nview = GetComponent<ZNetView>();
        }
        
        /// <summary>
        /// Ensures all component references are valid, re-fetching if needed.
        /// Some components may be added dynamically after Awake().
        /// </summary>
        private void EnsureComponentReferences()
        {
            if (_consumables == null)
            {
                _consumables = GetComponent<CompanionConsumables>();
            }
            if (_progression == null)
            {
                _progression = GetComponent<CompanionProgression>();
            }
            if (_combatMovement == null)
            {
                _combatMovement = GetComponent<CompanionCombatMovement>();
            }
        }
        
        // CRITICAL: The canonical base health for all companions
        // This should match CompanionPrefabManager.ConfigureHumanoid() which sets m_health = 200f
        // Do NOT read from Character.GetMaxHealth() because CompanionRandomLoadout may have already
        // modified it with scale/biome multipliers, causing health to balloon on reload.
        private const float CanonicalBaseHealth = 200f;
        
        private void Start()
        {
            // CRITICAL FIX: Always use the canonical base health value
            // CompanionRandomLoadout.ApplyScale() and ScaleHealthForBiome() modify Character.m_health directly
            // If we read from Character.GetMaxHealth() after those run, we get an already-scaled value
            // and then apply bonuses on top, causing health to balloon (the "super high health" bug).
            // 
            // Instead, we always use 200 as the base, and let CompanionRandomLoadout handle
            // scale/biome multipliers separately which we'll read from the ZDO.
            baseMaxHealth = CanonicalBaseHealth;
            
            // Initial calculation with base values
            RecalculateMaxStats();
            
            // Initialize all stats to full before loading from ZDO
            _currentHealth = _maxHealth;
            _currentStamina = _maxStamina;
            _currentEitr = _maxEitr;
            
            // Load from ZDO (will override with saved values if they exist)
            LoadFromZDO();
            
            // Sync our health with Character's current health if it's valid
            if (_character != null)
            {
                float charHealth = _character.GetHealth();
                // Only use Character health if we didn't load a value from ZDO
                // and the character health seems valid
                if (_currentHealth <= 0 && charHealth > 0)
                {
                    _currentHealth = charHealth;
                }
            }
            
            // Subscribe to death event
            if (_character != null)
            {
                _character.m_onDeath += OnDeath;
            }
            
            // Subscribe to progression changes if available
            if (_progression != null)
            {
                _progression.OnAttributeChanged += OnProgressionChanged;
            }
            
            // Defer recalculation to ensure progression has loaded its data
            // This runs after all Start() methods have completed
            StartCoroutine(DeferredStatsRecalculation());
        }
        
        private System.Collections.IEnumerator DeferredStatsRecalculation()
        {
            // Wait several frames to ensure all components have initialized
            yield return null;
            yield return null;
            yield return null;
            
            // Wait a bit longer to ensure food effects are loaded from ZDO
            // CompanionConsumables.DeferredLoad() only waits 2 frames, so we wait longer
            yield return new WaitForSeconds(FoodLoadSettleDelay);
            
            // CRITICAL: Re-fetch component references in case they were added after Awake()
            EnsureComponentReferences();
            
            // Check if CompanionConsumables has already loaded food data
            // If it has, and our current health looks correct, don't override it
            float currentFoodBonus = _consumables?.GetFoodHealthBonus() ?? 0f;
            bool consumablesAlreadyLoaded = currentFoodBonus > 0 || (_consumables?.GetActiveFoodCount() ?? 0) > 0;
            
            // If consumables loaded food and our health already reflects it, skip recalculation
            // This prevents race conditions where DeferredStatsRecalculation overwrites correct values
            float attributeHealthBonus = _progression?.GetAttributeValue(CompanionProgression.AttributeType.Health) * (_progression?.healthBonusPerPoint ?? DefaultHealthBonusPerPoint) ?? 0f;
            float expectedMaxWithFood = ComputeMaxHealth(attributeHealthBonus, currentFoodBonus);
            
            bool healthAlreadyCorrect = Mathf.Abs(_maxHealth - expectedMaxWithFood) < 1f && _currentHealth > 0;
            
            if (consumablesAlreadyLoaded && healthAlreadyCorrect)
            {
                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionStats] DeferredStatsRecalculation: Skipping - health already correct " +
                        $"(current={_currentHealth:F0}/{_maxHealth:F0}, expected max={expectedMaxWithFood:F0}, foodBonus={currentFoodBonus:F0})");
                }
                yield break;
            }
            
            // Store old values to detect increases
            float oldMaxHealth = _maxHealth;
            float oldMaxStamina = _maxStamina;
            float oldMaxEitr = _maxEitr;
            float oldCurrentHealth = _currentHealth;
            float oldCurrentStamina = _currentStamina;
            float oldCurrentEitr = _currentEitr;
            
            // Now recalculate with full progression and food data
            RecalculateMaxStats();
            
            // CRITICAL: If max increased and current was at or near old max, update to new max
            bool healthWasFull = oldCurrentHealth >= oldMaxHealth - 1f || oldCurrentHealth <= 0 || oldMaxHealth <= 0;
            bool staminaWasFull = oldCurrentStamina >= oldMaxStamina - 1f || oldCurrentStamina <= 0;
            bool eitrWasFull = oldCurrentEitr >= oldMaxEitr - 1f || oldCurrentEitr <= 0;
            
            if (_maxHealth > oldMaxHealth && healthWasFull)
            {
                _currentHealth = _maxHealth;
                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionStats] DeferredStatsRecalculation: Health filled to max ({oldCurrentHealth:F0} -> {_currentHealth:F0})");
                }
            }
            else if (_maxHealth > oldMaxHealth)
            {
                // Max increased but wasn't full - add the difference
                float increase = _maxHealth - oldMaxHealth;
                _currentHealth = Mathf.Min(_currentHealth + increase, _maxHealth);
                if (VerboseLogging)
                {
                    Debug.Log($"[CompanionStats] DeferredStatsRecalculation: Health increased by {increase:F0} ({oldCurrentHealth:F0} -> {_currentHealth:F0})");
                }
            }
            
            if (_maxStamina > oldMaxStamina && staminaWasFull)
            {
                _currentStamina = _maxStamina;
            }
            
            if (_maxEitr > oldMaxEitr && eitrWasFull)
            {
                _currentEitr = _maxEitr;
            }
            
            // Clamp to max (in case we somehow exceeded it)
            _currentHealth = Mathf.Clamp(_currentHealth, 0, _maxHealth);
            _currentStamina = Mathf.Clamp(_currentStamina, 0, _maxStamina);
            _currentEitr = Mathf.Clamp(_currentEitr, 0, _maxEitr);
            
            // Save updated stats to ZDO
            SaveToZDO();
            
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionStats] DeferredStatsRecalculation complete for {_companion?.companionName}: " +
                    $"Health={_currentHealth:F0}/{_maxHealth:F0}, Stamina={_currentStamina:F0}/{_maxStamina:F0}");
            }
        }
        
        private void OnProgressionChanged()
        {
            RecalculateMaxStats();
        }
        
        private void OnDestroy()
        {
            if (_character != null)
            {
                _character.m_onDeath -= OnDeath;
            }
            
            // Unsubscribe from progression changes
            if (_progression != null)
            {
                _progression.OnAttributeChanged -= OnProgressionChanged;
            }
        }
        
        private void Update()
        {
            // Allow BOTH tamed and wild companions to regenerate stamina
            // Wild companions need stamina to attack too!
            if (_companion == null) return;
            
            UpdateRegeneration(Time.deltaTime);
            
            // Sync our health with Character's actual health (in case of damage)
            SyncHealthFromCharacter();
            
            // Periodically validate and fix health if it seems wrong
            // This catches race conditions where health got stuck at a wrong value
            ValidateHealthPeriodically();
            
            // Periodically save to ZDO for network sync
            if (Time.time - _lastZdoSyncTime > 1f)
            {
                _lastZdoSyncTime = Time.time;
                SaveToZDO();
            }
        }
        
        private float _lastHealthValidation;
        private const float HealthValidationInterval = 3f; // Check every 3 seconds instead of 5
        private int _healthValidationFailures = 0;
        
        /// <summary>
        /// Periodically validates that health values make sense.
        /// If health appears stuck at a value lower than it should be, forces a recalculation.
        /// </summary>
        private void ValidateHealthPeriodically()
        {
            if (Time.time - _lastHealthValidation < HealthValidationInterval) return;
            _lastHealthValidation = Time.time;
            
            // Validate for ALL following companions, not just active ones
            if (_companion == null || !_companion.isTamed) return;
            if (_character == null || _character.IsDead()) return;
            
            // Check if current health is suspiciously low compared to max
            // This catches cases where health got stuck at 100/230 despite having food
            float expectedMaxHealth = CalculateExpectedMaxHealth();
            
            // If our tracked max is significantly lower than expected, something went wrong
            if (expectedMaxHealth > _maxHealth + HealthValidationTolerance)
            {
                _healthValidationFailures++;
                
                if (_healthValidationFailures >= 1) // Act immediately on first failure
                {
                    Debug.LogWarning($"[CompanionStats] Health validation FAILED for {_companion?.companionName}: " +
                        $"current max={_maxHealth:F0}, expected={expectedMaxHealth:F0}, current health={_currentHealth:F0}. Forcing refresh.");
                    
                    ForceHealthRefresh();
                    _healthValidationFailures = 0;
                }
            }
            // ALSO check if current health is stuck below max for too long
            // This catches the 159/230 situation where max is correct but current isn't updating
            else if (_currentHealth < _maxHealth - HealthValidationTolerance && !IsInCombat)
            {
                // Health should be regenerating - if it's stuck, something is wrong
                // Note: we don't fail here, just log for debugging
                // The active health regen in UpdateRegeneration should fix this
            }
            else
            {
                _healthValidationFailures = 0;
            }
        }
        
        /// <summary>
        /// Calculates what the max health SHOULD be based on all bonuses.
        /// Used for validation.
        /// </summary>
        /// <summary>
        /// THE canonical max-health formula: multiplicative core (base x scale/biome x effective-level x
        /// archetype) + flat bonuses (attributes + food). RecalculateMaxStats AND the periodic validator
        /// both call this so they can never diverge - a divergence is exactly what made the validator
        /// false-fail into an endless "Forcing refresh" loop after the archetype/level terms were added.
        /// </summary>
        private float ComputeMaxHealth(float healthBonus, float foodHealthBonus)
        {
            float healthMultiplier = GetHealthMultiplierFromRandomLoadout();
            int effectiveLevel = _companion != null ? _companion.GetEffectiveLevel() : 1;
            return (baseMaxHealth * healthMultiplier * effectiveLevel * _archetypeHealthMultiplier) + healthBonus + foodHealthBonus;
        }

        private float CalculateExpectedMaxHealth()
        {
            EnsureComponentReferences();
            
            float healthBonus = 0f;
            if (_progression != null)
            {
                healthBonus = _progression.GetAttributeValue(CompanionProgression.AttributeType.Health) * _progression.healthBonusPerPoint;
            }
            
            float foodHealthBonus = _consumables?.GetFoodHealthBonus() ?? 0f;
            return ComputeMaxHealth(healthBonus, foodHealthBonus);
        }
        
        /// <summary>
        /// Forces a complete health refresh, recalculating max health and updating current health to match.
        /// Call this if health appears to be stuck at an incorrect value.
        /// </summary>
        public void ForceHealthRefresh()
        {
            EnsureComponentReferences();
            
            float oldMaxHealth = _maxHealth;
            float oldCurrentHealth = _currentHealth;
            
            // Force recalculate
            RecalculateMaxStats();
            
            // If max health increased, update current health proportionally
            if (_maxHealth > oldMaxHealth)
            {
                float increase = _maxHealth - oldMaxHealth;
                
                // If was at or near full, fill to new max
                if (oldCurrentHealth >= oldMaxHealth - 1f || oldMaxHealth <= 0)
                {
                    _currentHealth = _maxHealth;
                }
                else
                {
                    // Add the increase
                    _currentHealth = Mathf.Min(_currentHealth + increase, _maxHealth);
                }
            }
            
            // Update Character's health to match
            ApplyMaxHealthToCharacter();
            
            // Save to ZDO
            SaveToZDO();
            
            if (VerboseLogging || oldMaxHealth != _maxHealth)
            {
                Debug.Log($"[CompanionStats] ForceHealthRefresh for {_companion?.companionName}: " +
                    $"Max: {oldMaxHealth:F0} -> {_maxHealth:F0}, Current: {oldCurrentHealth:F0} -> {_currentHealth:F0}");
            }
        }
        
        /// <summary>
        /// Syncs our tracked health with the Character's health.
        /// We track both so we can display food bonus health properly.
        /// 
        /// CRITICAL: This method handles the bidirectional sync between our tracked _currentHealth
        /// and the Character component's health. The Character's health is used for damage calculations
        /// by Valheim, while our _currentHealth includes food bonuses and is displayed in UI.
        /// </summary>
        private void SyncHealthFromCharacter()
        {
            if (_character == null) return;
            
            float charHealth = _character.GetHealth();
            float charMaxHealth = _character.GetMaxHealth();
            
            // Skip sync if character health is invalid
            if (charHealth < 0 || float.IsNaN(charHealth) || float.IsInfinity(charHealth)) return;
            
            // CRITICAL FIX: Detect healing by tracking previous char health
            // If Character healed (health went up), we should heal too
            float prevCharHealth = _lastSyncedCharHealth;
            _lastSyncedCharHealth = charHealth;
            
            // If Character took damage (its health went down), reduce our tracked health
            if (charHealth < prevCharHealth && prevCharHealth > 0)
            {
                float damageTaken = prevCharHealth - charHealth;
                _currentHealth = Mathf.Max(0, _currentHealth - damageTaken);
            }
            // If Character healed (health went up), increase our tracked health
            else if (charHealth > prevCharHealth && prevCharHealth >= 0)
            {
                float healAmount = charHealth - prevCharHealth;
                _currentHealth = Mathf.Min(_maxHealth, _currentHealth + healAmount);
            }
            // If this is first sync or values diverged significantly, align with character
            else if (prevCharHealth < 0 || Mathf.Abs(_currentHealth - charHealth) > _maxHealth * 0.5f)
            {
                // First sync - use character health but scale to our max if needed
                if (charMaxHealth > 0 && _maxHealth > charMaxHealth)
                {
                    // Our max is higher (food bonus) - scale proportionally
                    float ratio = charHealth / charMaxHealth;
                    _currentHealth = ratio * _maxHealth;
                }
                else
                {
                    _currentHealth = charHealth;
                }
            }
            
            // Always clamp to valid range
            _currentHealth = Mathf.Clamp(_currentHealth, 0, _maxHealth);
        }
        
        // Track last synced character health for change detection
        private float _lastSyncedCharHealth = -1f;
        
        #endregion
        
        #region Stat Calculations
        
        /// <summary>
        /// Recalculates maximum stats based on attributes.
        /// Called when attributes change or on initialization.
        /// </summary>
        public void RecalculateMaxStats()
        {
            string companionName = _companion?.companionName ?? "Unknown";
            
            // CRITICAL: Ensure component references are valid before using them
            EnsureComponentReferences();
            
            float healthBonus = 0f;
            float staminaBonus = 0f;
            float eitrBonus = 0f;
            
            if (_progression != null)
            {
                // Health: uses healthBonusPerPoint (5 HP per point)
                healthBonus = _progression.GetAttributeValue(CompanionProgression.AttributeType.Health) * _progression.healthBonusPerPoint;
                
                // Endurance: uses staminaBonusPerPoint (3 stamina per point)
                staminaBonus = _progression.GetAttributeValue(CompanionProgression.AttributeType.Endurance) * _progression.staminaBonusPerPoint;
                
                // Intelligence: uses eitrBonusPerPoint (3 eitr per point)
                eitrBonus = _progression.GetAttributeValue(CompanionProgression.AttributeType.Intelligence) * _progression.eitrBonusPerPoint;
            }
            
            // Add food bonuses if consumables component exists
            float foodHealthBonus = 0f;
            float foodStaminaBonus = 0f;
            float foodEitrBonus = 0f;
            
            if (_consumables != null)
            {
                foodHealthBonus = _consumables.GetFoodHealthBonus();
                foodStaminaBonus = _consumables.GetFoodStaminaBonus();
                foodEitrBonus = _consumables.GetFoodEitrBonus();
            }
            
            float oldMaxHealth = _maxHealth;
            float oldMaxStamina = _maxStamina;
            float oldMaxEitr = _maxEitr;
            float oldCurrentStamina = _currentStamina;
            float oldCurrentEitr = _currentEitr;
            
            // Health = scale/biome x effective-level x archetype, + flat bonuses (attributes + food).
            // Centralised in ComputeMaxHealth so the periodic validator computes the SAME value and can never
            // false-fail into a "Forcing refresh" loop.
            _maxHealth = ComputeMaxHealth(healthBonus, foodHealthBonus);
            // Stamina keeps the original (base+bonuses)*archetype convention and is intentionally NOT
            // level-scaled (vanilla creatures do not grow stamina with level).
            _maxStamina = (baseMaxStamina + staminaBonus + foodStaminaBonus) * _archetypeStaminaMultiplier;
            _maxEitr = baseMaxEitr + eitrBonus + foodEitrBonus;
            
            // Apply max health to character (for damage calculations)
            ApplyMaxHealthToCharacter();
            
            // CRITICAL: Update current health when max increases from food
            if (_maxHealth > oldMaxHealth && oldMaxHealth > 0)
            {
                float healthRatio = _currentHealth / oldMaxHealth;
                if (healthRatio >= NearFullRatio) // Was at or near full health
                {
                    _currentHealth = _maxHealth;
                    if (VerboseLogging)
                        Debug.Log($"[CompanionStats] Health filled to max: {oldMaxHealth:F0} -> {_currentHealth:F0}/{_maxHealth:F0}");
                }
                else
                {
                    // Scale health proportionally
                    float increase = _maxHealth - oldMaxHealth;
                    _currentHealth = Mathf.Min(_currentHealth + increase, _maxHealth);
                    if (VerboseLogging)
                        Debug.Log($"[CompanionStats] Health increased proportionally to {_currentHealth:F0}/{_maxHealth:F0}");
                }
            }
            
            // CRITICAL: Update current stamina/eitr when max increases
            // Always update current values to match the increase in max values
            // This ensures that food bonuses properly fill the stat bars
            if (_maxStamina > oldMaxStamina)
            {
                float increase = _maxStamina - oldMaxStamina;
                
                // If old max was 0 or we were at/near full, fill to new max
                if (oldMaxStamina <= 0 || oldCurrentStamina >= oldMaxStamina - 1f)
                {
                    _currentStamina = _maxStamina;
                    if (VerboseLogging)
                        Debug.Log($"[CompanionStats] Stamina filled to max: {oldCurrentStamina:F0}/{oldMaxStamina:F0} -> {_currentStamina:F0}/{_maxStamina:F0}");
                }
                else
                {
                    // We had used some stamina - add the bonus amount proportionally
                    _currentStamina = Mathf.Min(_currentStamina + increase, _maxStamina);
                    if (VerboseLogging)
                        Debug.Log($"[CompanionStats] Stamina increased: {oldCurrentStamina:F0}/{oldMaxStamina:F0} + {increase:F0} -> {_currentStamina:F0}/{_maxStamina:F0}");
                }
            }
            
            if (_maxEitr > oldMaxEitr)
            {
                float increase = _maxEitr - oldMaxEitr;
                
                // If old max was 0 or we were at/near full, fill to new max
                if (oldMaxEitr <= 0 || oldCurrentEitr >= oldMaxEitr - 1f)
                {
                    _currentEitr = _maxEitr;
                    if (VerboseLogging)
                        Debug.Log($"[CompanionStats] Eitr filled to max: {oldCurrentEitr:F0}/{oldMaxEitr:F0} -> {_currentEitr:F0}/{_maxEitr:F0}");
                }
                else
                {
                    // We had used some eitr - add the bonus amount proportionally
                    _currentEitr = Mathf.Min(_currentEitr + increase, _maxEitr);
                    if (VerboseLogging)
                        Debug.Log($"[CompanionStats] Eitr increased: {oldCurrentEitr:F0}/{oldMaxEitr:F0} + {increase:F0} -> {_currentEitr:F0}/{_maxEitr:F0}");
                }
            }
            
            // Clamp current values to new max (handles case where max decreased)
            _currentStamina = Mathf.Min(_currentStamina, _maxStamina);
            _currentEitr = Mathf.Min(_currentEitr, _maxEitr);
            
            // Only log for companions that are actively following the player to reduce log spam
            bool shouldLog = VerboseLogging && _companion != null && _companion.ShouldBeFollowing;
            if (shouldLog)
            {
                Debug.Log($"[CompanionStats] Recalculated stats for {_companion?.companionName}: " +
                    $"MaxHP={_maxHealth} (base={baseMaxHealth}, attr={healthBonus}, food={foodHealthBonus}), " +
                    $"MaxStam={_maxStamina}, MaxEitr={_maxEitr}");
            }
        }
        
        /// <summary>
        /// Gets the health multiplier from CompanionRandomLoadout.
        /// This accounts for scale-based health (giants have more HP, dwarves less)
        /// and biome-based health (Ashlands companions are stronger).
        /// </summary>
        private float GetHealthMultiplierFromRandomLoadout()
        {
            var randomLoadout = GetComponent<CompanionRandomLoadout>();
            if (randomLoadout == null) return 1f;
            
            float scale = randomLoadout.GetScale();
            if (scale <= 0f) scale = 1f;
            
            // Scale health: giants get up to 1.5x health, dwarves get 0.7x minimum
            // This matches the logic in CompanionRandomLoadout.ApplyScale()
            float scaleMultiplier = Mathf.Lerp(MinScaleHealthMultiplier, MaxScaleHealthMultiplier, Mathf.InverseLerp(CompanionRandomLoadout.MIN_SCALE, CompanionRandomLoadout.MAX_SCALE, scale));
            
            // Also get biome multiplier from ZDO if available
            float biomeMultiplier = 1f;
            var nview = GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
            {
                var zdo = nview.GetZDO();
                if (zdo != null)
                {
                    int spawnBiome = zdo.GetInt("companion_spawn_biome", (int)Heightmap.Biome.None);
                    if (spawnBiome != (int)Heightmap.Biome.None)
                    {
                        biomeMultiplier = GetBiomeHealthMultiplier((Heightmap.Biome)spawnBiome);
                    }
                }
            }
            
            return scaleMultiplier * biomeMultiplier;
        }
        
        /// <summary>
        /// Gets the health multiplier for a given spawn biome.
        /// Matches CompanionRandomLoadout.ScaleHealthForBiome().
        /// </summary>
        private float GetBiomeHealthMultiplier(Heightmap.Biome biome)
        {
            return biome switch
            {
                Heightmap.Biome.Meadows => 0.5f,       // 100 HP base
                Heightmap.Biome.BlackForest => 0.75f,  // 150 HP base
                Heightmap.Biome.Swamp => 1.0f,         // 200 HP base
                Heightmap.Biome.Mountain => 1.5f,      // 300 HP base
                Heightmap.Biome.Plains => 2.0f,        // 400 HP base
                Heightmap.Biome.Mistlands => 2.5f,     // 500 HP base
                Heightmap.Biome.AshLands => 3.0f,      // 600 HP base
                Heightmap.Biome.DeepNorth => 3.0f,     // 600 HP base
                _ => 1.0f
            };
        }
        
        /// <summary>
        /// Applies the calculated max health to the Character component.
        /// This is mainly for damage calculations - we track current health ourselves.
        /// </summary>
        private void ApplyMaxHealthToCharacter()
        {
            if (_character == null) return;
            
            try
            {
                // Use Character.SetMaxHealth() - this is the proper Valheim method
                _character.SetMaxHealth(_maxHealth);
                
                // Also try to set the current health on Character to match our tracked value
                // This helps with damage calculation consistency
                SetCurrentHealthDirect(_currentHealth);
                
                if (VerboseLogging)
                {
                    float actualMax = _character.GetMaxHealth();
                    Debug.Log($"[CompanionStats] ApplyMaxHealthToCharacter: target={_maxHealth:F0}, actual={actualMax:F0}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionStats] Failed to set max health: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Directly sets the current health value via reflection.
        /// Used when we need to bypass normal health clamping.
        /// </summary>
        private void SetCurrentHealthDirect(float health)
        {
            if (_character == null) return;
            
            try
            {
                var healthField = typeof(Character).GetField("m_health",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    
                if (healthField != null)
                {
                    healthField.SetValue(_character, health);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionStats] Failed to set current health: {ex.Message}");
            }
        }
        
        #endregion
        
        #region Stamina Management
        
        /// <summary>
        /// Attempts to use stamina for an action.
        /// Returns true if stamina was successfully consumed.
        /// </summary>
        public bool UseStamina(float amount)
        {
            if (amount <= 0) return true;
            
            if (_currentStamina >= amount)
            {
                _currentStamina -= amount;
                _lastStaminaUseTime = Time.time;
                
                // Only log for following companions to reduce spam
                if (VerboseLogging && _companion != null && _companion.ShouldBeFollowing)
                {
                    Debug.Log($"[CompanionStats] {_companion?.companionName} used {amount:F1} stamina, " +
                        $"remaining: {_currentStamina:F1}/{_maxStamina:F1}");
                }
                
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if there's enough stamina for an action without consuming it.
        /// </summary>
        public bool HasStamina(float amount)
        {
            return _currentStamina >= amount;
        }
        
        /// <summary>
        /// Directly sets the current stamina value.
        /// Used for forced recovery during flee state.
        /// </summary>
        public void SetStamina(float amount)
        {
            _currentStamina = Mathf.Clamp(amount, 0, _maxStamina);
            _lastStaminaUseTime = 0f; // Allow immediate regen
            
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionStats] {_companion?.companionName} stamina set to {_currentStamina:F1}/{_maxStamina:F1}");
            }
        }
        
        /// <summary>
        /// Gets the stamina cost for an action, modified by skills.
        /// </summary>
        public float GetStaminaCost(float baseCost, Skills.SkillType skillType)
        {
            float skillReduction = 0f;
            
            var skills = GetComponent<CompanionSkills>();
            if (skills != null)
            {
                // Higher skill = lower stamina cost (up to 33% reduction at level 100)
                float skillFactor = skills.GetSkillFactor(skillType);
                skillReduction = baseCost * skillFactor * StaminaSkillMaxReduction;
            }
            
            return Mathf.Max(baseCost - skillReduction, baseCost * 0.5f);
        }
        
        #endregion
        
        #region Eitr Management
        
        /// <summary>
        /// Attempts to use eitr for magic.
        /// Returns true if eitr was successfully consumed.
        /// </summary>
        public bool UseEitr(float amount)
        {
            if (amount <= 0) return true;
            
            if (_currentEitr >= amount)
            {
                _currentEitr -= amount;
                _lastEitrUseTime = Time.time;
                
                // Only log for following companions to reduce spam
                if (VerboseLogging && _companion != null && _companion.ShouldBeFollowing)
                {
                    Debug.Log($"[CompanionStats] {_companion?.companionName} used {amount:F1} eitr, " +
                        $"remaining: {_currentEitr:F1}/{_maxEitr:F1}");
                }
                
                return true;
            }
            
            return false;
        }
        
        /// <summary>
        /// Checks if there's enough eitr for an action without consuming it.
        /// </summary>
        public bool HasEitr(float amount)
        {
            return _currentEitr >= amount;
        }
        
        /// <summary>
        /// Gets the eitr cost for magic, modified by skills.
        /// </summary>
        public float GetEitrCost(float baseCost, Skills.SkillType magicSkill)
        {
            float skillReduction = 0f;
            
            var skills = GetComponent<CompanionSkills>();
            if (skills != null)
            {
                // Higher magic skill = lower eitr cost (up to 25% reduction at level 100)
                float skillFactor = skills.GetSkillFactor(magicSkill);
                skillReduction = baseCost * skillFactor * EitrSkillMaxReduction;
            }
            
            return Mathf.Max(baseCost - skillReduction, baseCost * 0.5f);
        }
        
        #endregion
        
        #region Regeneration
        
        /// <summary>
        /// Bonus stamina regen per endurance point (0.1 = 10% faster regen per point)
        /// At 50 endurance, this provides 50% faster stamina regeneration.
        /// </summary>
        private const float StaminaRegenBonusPerEndurance = 0.01f;
        
        // Cache stamina manager reference
        private Combat.StaminaManager _staminaManager;
        
        private void UpdateRegeneration(float dt)
        {
            bool inCombat = IsInCombat;
            float combatMult = 1f;
            
            // Health regeneration - ACTIVE REGENERATION for companions
            // Companions regenerate health over time, faster out of combat
            // This ensures health eventually reaches max, even if sync was wrong
            if (_currentHealth < _maxHealth && _currentHealth > 0)
            {
                combatMult = inCombat ? combatHealthRegenMultiplier : 1f;
                
                // Base health regen + food regen bonus, scaled by world buffs/debuffs (Rested, etc.)
                // through the vanilla SEMan hook - the same modifier Player health regen uses.
                float foodRegen = _consumables?.GetFoodHealthRegen() ?? 0f;
                float semanRegenMult = 1f;
                _character?.GetSEMan()?.ModifyHealthRegen(ref semanRegenMult);
                float totalHealthRegen = (healthRegenPerSecond + foodRegen) * combatMult * semanRegenMult * dt;
                
                float oldHealth = _currentHealth;
                _currentHealth = Mathf.Min(_currentHealth + totalHealthRegen, _maxHealth);
                
                // Also heal the Character component so damage calculations work correctly
                if (_currentHealth > oldHealth && _character != null)
                {
                    SetCurrentHealthDirect(_currentHealth);
                }
            }
            
            // Stamina regeneration - ENHANCED BY ENDURANCE AND RETREAT STATE
            if (Time.time - _lastStaminaUseTime >= staminaRegenDelay)
            {
                combatMult = inCombat ? combatStaminaRegenMultiplier : 1f;
                
                // Calculate endurance bonus for stamina regen
                // Each point of endurance adds a small % to stamina regen speed
                float enduranceBonus = 1f;
                if (_progression != null)
                {
                    int endurance = _progression.GetAttributeValue(CompanionProgression.AttributeType.Endurance);
                    // Each point adds 1% faster regen
                    // At 50 endurance = 50% faster, at 100 endurance = 100% faster (double speed)
                    enduranceBonus = 1f + (endurance * StaminaRegenBonusPerEndurance);
                }
                
                // Get retreat regen boost from StaminaManager
                // When in critical stamina recovery and retreating, stamina regens faster
                float retreatBonus = 1f;
                if (_staminaManager == null)
                {
                    _staminaManager = GetComponent<Combat.StaminaManager>();
                }
                if (_staminaManager != null)
                {
                    retreatBonus = _staminaManager.GetStaminaRegenMultiplier();
                }
                
                float semanStaminaMult = 1f;
                _character?.GetSEMan()?.ModifyStaminaRegen(ref semanStaminaMult);
                float staminaRegen = staminaRegenPerSecond * combatMult * enduranceBonus * retreatBonus * semanStaminaMult * dt;
                _currentStamina = Mathf.Min(_currentStamina + staminaRegen, _maxStamina);
            }
            
            // Eitr regeneration
            if (Time.time - _lastEitrUseTime >= eitrRegenDelay)
            {
                combatMult = inCombat ? combatEitrRegenMultiplier : 1f;
                float eitrRegen = eitrRegenPerSecond * combatMult * dt;
                _currentEitr = Mathf.Min(_currentEitr + eitrRegen, _maxEitr);
            }
        }
        
        /// <summary>
        /// Restores all stats to full (used after respawn).
        /// </summary>
        public void RestoreAllStats()
        {
            _currentHealth = _maxHealth;
            _currentStamina = _maxStamina;
            _currentEitr = _maxEitr;
            
            // Also restore health via Character for consistency
            if (_character != null)
            {
                _character.SetMaxHealth(_maxHealth);
                _character.Heal(_maxHealth, true);
            }
            
            SaveToZDO();
        }
        
        #endregion
        
        #region Death Tracking
        
        private void OnDeath()
        {
            _deathCount++;
            _lastDeathTime = Time.time;
            
            SaveToZDO();
            
            // Only log deaths for tamed companions to reduce log spam
            if (_companion?.isTamed == true)
            {
                Debug.Log($"[CompanionStats] {_companion?.companionName} died (total deaths: {_deathCount})");
            }
        }
        
        /// <summary>
        /// Called when companion respawns.
        /// </summary>
        public void OnRespawn()
        {
            RestoreAllStats();
        }
        
        #endregion
        
        #region Persistence
        
        public void SaveToZDO()
        {
            if (CompanionPatches.AreCompanionTeleportsSuppressed()) return;
            var zdo = _nview?.GetZDO();
            if (zdo == null) return;

            // Save all current values
            zdo.Set("companion_health", _currentHealth);
            zdo.Set("companion_stamina", _currentStamina);
            zdo.Set("companion_eitr", _currentEitr);
            
            // Save max values so other clients know the caps
            zdo.Set("companion_max_health", _maxHealth);
            zdo.Set("companion_max_stamina", _maxStamina);
            zdo.Set("companion_max_eitr", _maxEitr);
            
            zdo.Set("companion_deaths", _deathCount);
        }
        
        public void LoadFromZDO()
        {
            var zdo = _nview?.GetZDO();
            if (zdo == null) return;
            
            // Load saved current values - use -1 as default to detect if not set
            float savedHealth = zdo.GetFloat("companion_health", -1f);
            float savedStamina = zdo.GetFloat("companion_stamina", -1f);
            float savedEitr = zdo.GetFloat("companion_eitr", -1f);
            
            // Load saved max values (for reference)
            float savedMaxHealth = zdo.GetFloat("companion_max_health", -1f);
            float savedMaxStamina = zdo.GetFloat("companion_max_stamina", -1f);
            float savedMaxEitr = zdo.GetFloat("companion_max_eitr", -1f);
            
            _deathCount = zdo.GetInt("companion_deaths", 0);
            
            // Load health
            if (savedHealth < 0)
            {
                _currentHealth = _maxHealth;
            }
            else if (savedMaxHealth > 0 && _maxHealth > savedMaxHealth)
            {
                // Max health increased since save (food bonuses?) - scale up proportionally
                float ratio = savedHealth / savedMaxHealth;
                _currentHealth = ratio >= NearFullRatio ? _maxHealth : Mathf.Min(savedHealth + (_maxHealth - savedMaxHealth), _maxHealth);
            }
            else
            {
                _currentHealth = Mathf.Clamp(savedHealth, 0, _maxHealth);
            }
            
            // Load stamina
            if (savedStamina < 0)
            {
                _currentStamina = _maxStamina;
            }
            else if (savedMaxStamina > 0 && _maxStamina > savedMaxStamina)
            {
                // Max increased since save - scale up
                float ratio = savedStamina / savedMaxStamina;
                _currentStamina = ratio >= NearFullRatio ? _maxStamina : Mathf.Min(savedStamina + (_maxStamina - savedMaxStamina), _maxStamina);
            }
            else
            {
                _currentStamina = Mathf.Clamp(savedStamina, 0, _maxStamina);
            }
            
            // Load eitr
            if (savedEitr < 0)
            {
                _currentEitr = _maxEitr;
            }
            else if (savedMaxEitr > 0 && _maxEitr > savedMaxEitr)
            {
                // Max increased since save - scale up
                float ratio = savedEitr / savedMaxEitr;
                _currentEitr = ratio >= NearFullRatio ? _maxEitr : Mathf.Min(savedEitr + (_maxEitr - savedMaxEitr), _maxEitr);
            }
            else
            {
                _currentEitr = Mathf.Clamp(savedEitr, 0, _maxEitr);
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[CompanionStats] Loaded from ZDO: Health={_currentHealth:F0}/{_maxHealth:F0}, Stamina={_currentStamina:F0}/{_maxStamina:F0}, Eitr={_currentEitr:F0}/{_maxEitr:F0}");
            }
        }
        
        /// <summary>
        /// Gets stats data for vault storage.
        /// Format: deathCount|lastDeathTime
        /// </summary>
        public string GetStatsDataForVault()
        {
            // Include more data in the format to ensure persistence
            return $"{_deathCount}|{_lastDeathTime:F0}";
        }
        
        /// <summary>
        /// Restores stats from vault data.
        /// </summary>
        public void RestoreStatsFromVault(string data)
        {
            if (string.IsNullOrEmpty(data)) return;
            
            // Parse format: deathCount|lastDeathTime (backward compatible with old format)
            var parts = data.Split('|');
            
            if (parts.Length >= 1 && int.TryParse(parts[0], out int deaths))
            {
                _deathCount = deaths;
                Debug.Log($"[CompanionStats] Restored death count {_deathCount} from vault for {_companion?.companionName}");
            }
            
            if (parts.Length >= 2 && float.TryParse(parts[1], out float lastDeath))
            {
                _lastDeathTime = lastDeath;
            }
            
            // CRITICAL: Also save to ZDO immediately to ensure persistence
            SaveToZDO();
        }
        
        /// <summary>
        /// Gets archetype statistics data for vault storage.
        /// Delegates to ArchetypeController if available.
        /// </summary>
        public string GetArchetypeStatisticsData()
        {
            var archetypeController = GetComponent<Archetypes.ArchetypeController>();
            return archetypeController?.GetStatisticsDataForVault() ?? "";
        }
        
        /// <summary>
        /// Sets the archetype stamina multiplier.
        /// Called by ArchetypeController when archetype is assigned.
        /// </summary>
        public void SetArchetypeStaminaMultiplier(float multiplier)
        {
            _archetypeStaminaMultiplier = multiplier;
            RecalculateMaxStats();

            if (VerboseLogging)
            {
                Debug.Log($"[CompanionStats] Archetype stamina multiplier set to {multiplier:F2}x, new max={_maxStamina:F0}");
            }
        }

        /// <summary>
        /// Sets the archetype max-health multiplier (mirrors SetArchetypeStaminaMultiplier).
        /// Stacks multiplicatively with effective-level scaling in RecalculateMaxStats.
        /// </summary>
        public void SetArchetypeHealthMultiplier(float multiplier)
        {
            _archetypeHealthMultiplier = multiplier <= 0f ? 1f : multiplier;
            RecalculateMaxStats();

            if (VerboseLogging)
                Debug.Log($"[CompanionStats] Archetype health multiplier set to {_archetypeHealthMultiplier:F2}x, new max={_maxHealth:F0}");
        }

        /// <summary>
        /// Sets the archetype move-speed multiplier and applies it to the Character's walk/run speed
        /// (scaled from the cached base so repeated calls don't compound).
        /// </summary>
        public void SetArchetypeSpeedMultiplier(float multiplier)
        {
            _archetypeSpeedMultiplier = multiplier <= 0f ? 1f : multiplier;
            ApplyArchetypeSpeedToCharacter();
        }

        private void ApplyArchetypeSpeedToCharacter()
        {
            var character = _companion != null ? _companion.GetComponent<Character>() : GetComponent<Character>();
            if (character == null) return;
            if (_baseWalkSpeed < 0f) { _baseWalkSpeed = character.m_walkSpeed; _baseRunSpeed = character.m_runSpeed; }
            character.m_walkSpeed = _baseWalkSpeed * _archetypeSpeedMultiplier;
            character.m_runSpeed = _baseRunSpeed * _archetypeSpeedMultiplier;
        }

        #endregion
    }
}
