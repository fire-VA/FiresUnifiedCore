using UnityEngine;
using System;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Drives hit detection for a companion's vanilla Attack. Attack.OnAttackTrigger normally fires from
    /// animation events; as a fallback this triggers it once the animator's normalized time passes the hit
    /// point. Attack.Update must still run every frame (WeaponBehavior.UpdateNativeAttack does it) for
    /// projectile bursts, hit freeze and stopping at the end of the animation.
    /// </summary>
    public class CompanionAttackBridge : MonoBehaviour
    {
        private CompanionCombat _combat;
        private Character _character;
        private Humanoid _humanoid;
        private Animator _animator;
        private ZSyncAnimation _zanim;
        private CharacterAnimEvent _animEvent;
    
        // The currently active Attack instance
        private Attack _activeAttack;
        private bool _attackTriggered;
        private float _attackStartTime;
        
        // Animation-based timing
        private bool _monitoringAnimation;
        private float _hitNormalizedTime;
        private int _attackAnimationHash;
        private string _attackAnimationName;
        
        // Fallback timer (only used if animator monitoring fails)
        private float _fallbackHitTime;
        private bool _useFallbackTimer;
        
        // Track if vanilla system already triggered the hit
        private bool _vanillaTriggered;
        
        // Callbacks
        public event Action OnAttackTriggered;
        
        private void Awake()
        {
            _combat = GetComponent<CompanionCombat>();
            _character = GetComponent<Character>();
            _humanoid = GetComponent<Humanoid>();
            _animator = GetComponentInChildren<Animator>(true);
            _zanim = GetComponent<ZSyncAnimation>();
            _animEvent = GetComponentInChildren<CharacterAnimEvent>(true);
            
            // Hook into CharacterAnimEvent if available
            if (_animEvent != null)
            {
                // CharacterAnimEvent calls m_character.OnAttackTrigger() which we can't easily intercept
                // But we can check if the attack was already triggered by checking its state
            }
        }
        
        /// <summary>
        /// Sets the active Attack instance and begins monitoring animation progress.
        /// </summary>
        /// <param name="attack">The Attack instance to trigger</param>
        /// <param name="animationName">The animation trigger name (e.g., "swing_longsword0")</param>
        /// <param name="hitNormalizedTime">When in the animation to trigger (0-1)</param>
        /// <param name="fallbackDelay">Fallback delay in seconds if animation monitoring fails</param>
        public void SetActiveAttack(Attack attack, string animationName = null, float hitNormalizedTime = 0.25f, float fallbackDelay = 0.2f)
        {
            _activeAttack = attack;
            _attackTriggered = false;
            _vanillaTriggered = false;
            _attackStartTime = Time.time;
            _monitoringAnimation = false;
            _useFallbackTimer = false;
            
            // Set up animation monitoring
            _hitNormalizedTime = hitNormalizedTime;
            _attackAnimationName = animationName ?? "";
            _attackAnimationHash = !string.IsNullOrEmpty(animationName) ? Animator.StringToHash(animationName) : 0;
            _fallbackHitTime = Time.time + fallbackDelay;
            
            if (_animator != null && !string.IsNullOrEmpty(animationName))
            {
                _monitoringAnimation = true;
                
                if (CompanionCombat.VerboseLogging)
                {
                    Debug.Log($"[CompanionAttackBridge] Monitoring animation '{animationName}' for hit at {hitNormalizedTime:P0} progress");
                }
            }
            else
            {
                // No animator or no animation name - use fallback timer
                _useFallbackTimer = true;
                
                if (CompanionCombat.VerboseLogging)
                {
                    Debug.Log($"[CompanionAttackBridge] Using fallback timer: {fallbackDelay:F3}s");
                }
            }
        }
        
        /// <summary>
        /// Legacy overload for compatibility.
        /// </summary>
        public void SetActiveAttack(Attack attack)
        {
            SetActiveAttack(attack, null, 0.25f, 0.2f);
        }
        
        /// <summary>
        /// Clears the active Attack instance.
        /// </summary>
        public void ClearActiveAttack()
        {
            _activeAttack = null;
            _attackTriggered = false;
            _vanillaTriggered = false;
            _monitoringAnimation = false;
            _useFallbackTimer = false;
        }
        
        /// <summary>
        /// Returns true if the attack trigger has been fired (by us OR by vanilla).
        /// </summary>
        public bool WasAttackTriggered => _attackTriggered || _vanillaTriggered;
        
        /// <summary>
        /// Returns true if we have an active attack that hasn't triggered yet.
        /// </summary>
        public bool HasPendingAttack => _activeAttack != null && !_attackTriggered && !_vanillaTriggered;
        
        private void Update()
        {
            if (_activeAttack == null)
                return;
            
            // Already triggered by us
            if (_attackTriggered)
                return;
            
            // Check if vanilla system already triggered the attack
            // We can detect this by checking if the attack has transitioned past the trigger point
            // Attack.m_wasInAttack is set after the first frame in InAttack state
            // and OnAttackTrigger is called when animation event fires
            if (_activeAttack.IsDone())
            {
                // Attack completed - vanilla handled it
                _vanillaTriggered = true;
                if (CompanionCombat.VerboseLogging)
                    Debug.Log("[CompanionAttackBridge] Attack completed via vanilla system");
                return;
            }
            
            // Priority 1: Monitor animation progress for precise timing
            if (_monitoringAnimation && _animator != null)
            {
                if (CheckAnimationProgress())
                {
                    TriggerAttackHit();
                    return;
                }
            }
            
            // Priority 2: Fallback timer
            if (_useFallbackTimer && Time.time >= _fallbackHitTime)
            {
                if (CompanionCombat.VerboseLogging)
                    Debug.Log("[CompanionAttackBridge] Fallback timer triggered");
                TriggerAttackHit();
            }
        }
        
        /// <summary>
        /// Checks if the animator has progressed past the hit point.
        /// Returns true if we should trigger the hit now.
        /// </summary>
        private bool CheckAnimationProgress()
        {
            if (_animator == null) return false;
            
            // Get the current animator state
            var stateInfo = _animator.GetCurrentAnimatorStateInfo(0);
            
            // Check if we're in an attack animation
            bool isInAttackState = false;
            
            // Method 1: Check by hash if we have one
            if (_attackAnimationHash != 0 && stateInfo.shortNameHash == _attackAnimationHash)
            {
                isInAttackState = true;
            }
            // Method 2: Check if state name contains attack-related keywords
            else if (stateInfo.IsTag("attack") || stateInfo.IsTag("Attack"))
            {
                isInAttackState = true;
            }
            // Method 3: Check animation clip names
            else if (_animator.GetCurrentAnimatorClipInfo(0).Length > 0)
            {
                var clipInfo = _animator.GetCurrentAnimatorClipInfo(0);
                foreach (var clip in clipInfo)
                {
                    string clipName = clip.clip?.name?.ToLower() ?? "";
                    if (clipName.Contains("attack") || clipName.Contains("swing") || 
                        clipName.Contains("slash") || clipName.Contains("stab") ||
                        (!string.IsNullOrEmpty(_attackAnimationName) && clipName.Contains(_attackAnimationName.ToLower())))
                    {
                        isInAttackState = true;
                        break;
                    }
                }
            }
            
            // If we're in an attack state and past the hit point, trigger!
            if (isInAttackState)
            {
                float normalizedTime = stateInfo.normalizedTime % 1f; // Handle looping
                
                if (normalizedTime >= _hitNormalizedTime)
                {
                    if (CompanionCombat.VerboseLogging)
                    {
                        Debug.Log($"[CompanionAttackBridge] Animation hit point reached! " +
                            $"Progress: {normalizedTime:P0} >= Target: {_hitNormalizedTime:P0}");
                    }
                    return true;
                }
            }
            else
            {
                // Not in attack state yet - might be transitioning
                // After a short grace period, fall back to timer
                if (Time.time - _attackStartTime > 0.3f && !_useFallbackTimer)
                {
                    if (CompanionCombat.VerboseLogging)
                    {
                        Debug.Log("[CompanionAttackBridge] Animation state not detected, enabling fallback timer");
                    }
                    _useFallbackTimer = true;
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Triggers the attack's hit detection using VANILLA LOGIC.
        /// Attack.OnAttackTrigger() does physics-based hit detection.
        /// </summary>
        private void TriggerAttackHit()
        {
            if (_activeAttack == null || _attackTriggered || _vanillaTriggered)
                return;
            
            _attackTriggered = true;
            _monitoringAnimation = false;
            _useFallbackTimer = false;
            
            try
            {
                // Verify the attack has all required references before triggering
                // Attack.OnAttackTrigger() can throw NRE if character/humanoid refs are null
                if (_character == null || _humanoid == null)
                {
                    if (CompanionCombat.VerboseLogging)
                        Debug.LogWarning($"[CompanionAttackBridge] Skipping OnAttackTrigger - missing character references");
                    OnAttackTriggered?.Invoke();
                    return;
                }
                
                // THIS IS VANILLA HIT DETECTION
                // Attack.OnAttackTrigger() does proper physics raycast/sphere checks
                _activeAttack.OnAttackTrigger();
                OnAttackTriggered?.Invoke();
                
                if (CompanionCombat.VerboseLogging)
                {
                    float elapsed = Time.time - _attackStartTime;
                    Debug.Log($"[CompanionAttackBridge] Attack.OnAttackTrigger() called at {elapsed:F3}s - VANILLA HIT DETECTION");
                }
            }
            catch (NullReferenceException)
            {
                // Silently handle NRE - this can happen for wild companions with incomplete setup
                // The attack will still complete via fallback hit detection in WeaponBehavior
                if (CompanionCombat.VerboseLogging)
                    Debug.LogWarning($"[CompanionAttackBridge] Attack.OnAttackTrigger() NRE - using fallback");
                OnAttackTriggered?.Invoke();
            }
            catch (Exception ex)
            {
                // Only log non-NRE exceptions
                if (CompanionCombat.VerboseLogging)
                    Debug.LogWarning($"[CompanionAttackBridge] Attack.OnAttackTrigger() error: {ex.Message}");
                OnAttackTriggered?.Invoke();
            }
        }
        
        /// <summary>
        /// Manually triggers the attack (used as fallback by WeaponBehavior).
        /// </summary>
        public void ManualTriggerAttack()
        {
            TriggerAttackHit();
        }
        
        /// <summary>
        /// Called when animation event fires (if it does).
        /// This can be hooked up to CharacterAnimEvent if needed.
        /// </summary>
        public void OnAnimationAttackTrigger()
        {
            if (_activeAttack == null)
                return;
            
            // Mark that vanilla triggered it so we don't double-trigger
            _vanillaTriggered = true;
            
            if (CompanionCombat.VerboseLogging)
                Debug.Log("[CompanionAttackBridge] Native animation event received!");
        }
        
        /// <summary>
        /// Gets the time since the current attack started.
        /// </summary>
        public float GetTimeSinceAttackStart()
        {
            if (_activeAttack == null) return 0f;
            return Time.time - _attackStartTime;
        }
        
        /// <summary>
        /// Gets the recommended normalized hit time for a weapon animation state.
        /// These are tuned to match vanilla Valheim animation timing.
        /// </summary>
        public static float GetHitNormalizedTime(ItemDrop.ItemData.AnimationState animState)
        {
            return animState switch
            {
                ItemDrop.ItemData.AnimationState.Unarmed => 0.20f,
                ItemDrop.ItemData.AnimationState.Knives => 0.18f,       // Knives are quick
                ItemDrop.ItemData.AnimationState.OneHanded => 0.25f,    // Swords/axes
                ItemDrop.ItemData.AnimationState.TwoHandedClub => 0.40f, // Sledge big windup
                ItemDrop.ItemData.AnimationState.TwoHandedAxe => 0.35f,
                ItemDrop.ItemData.AnimationState.Greatsword => 0.30f,
                ItemDrop.ItemData.AnimationState.Atgeir => 0.28f,
                ItemDrop.ItemData.AnimationState.DualAxes => 0.25f,
                ItemDrop.ItemData.AnimationState.Scythe => 0.32f,
                ItemDrop.ItemData.AnimationState.Bow => 0.05f,          // Instant release
                ItemDrop.ItemData.AnimationState.Crossbow => 0.10f,
                ItemDrop.ItemData.AnimationState.Staves => 0.25f,
                ItemDrop.ItemData.AnimationState.MagicItem => 0.20f,
                ItemDrop.ItemData.AnimationState.Torch => 0.22f,
                _ => 0.25f
            };
        }
    }
}
