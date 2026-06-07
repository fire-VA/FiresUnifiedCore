using UnityEngine;
using System;
using System.Collections.Generic;
using FiresCore.Npc.AI;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Tracks enemy attack patterns and provides dodge/block timing recommendations.
    /// Learns enemy "tells" - the animations that precede dangerous attacks.
    /// 
    /// DESIGN PRINCIPLE:
    /// Real players watch enemy animations to know when to dodge/block.
    /// This system observes enemy animators and detects wind-up animations.
    /// 
    /// KNOWN ENEMY PATTERNS:
    /// - Troll: "swing_longsword" wind-up before slam
    /// - Fuling: "attack" with spear thrust tell
    /// - Deathsquito: Direct charge (very short tell)
    /// - Greydwarf Brute: "attack" with club overhead
    /// 
    /// OUTPUT:
    /// - ThreatLevel: How dangerous is the current moment?
    /// - RecommendedAction: Dodge, Block, Parry, or Attack
    /// - TimeToImpact: Estimated time until attack lands
    /// </summary>
    public class EnemyAttackRecognition : MonoBehaviour
    {
        #region Settings

        [Header("Detection Settings")]
        [Tooltip("How far to scan for enemies")]
        public float detectionRange = 15f;
   
        [Tooltip("How often to scan for threats (seconds)")]
        public float scanInterval = 0.1f;
        
        [Tooltip("Time window where we consider an attack 'imminent'")]
        public float imminentAttackWindow = 0.5f;
     
        [Tooltip("Time window for perfect parry")]
  public float parryWindow = 0.25f;

        [Header("Threat Assessment")]
        [Tooltip("Distance where melee attacks become dangerous")]
        public float meleeDangerRange = 4f;
        
  [Tooltip("Multiplier for threat from enemies targeting our owner")]
        public float ownerThreatMultiplier = 1.5f;

        #endregion

        #region Components

        private CompanionController _companion;
        private CompanionCombatMovement _combatMovement;
        private Character _character;
        private CombatExperience _combatExperience;
        private StaminaManager _staminaManager;

        #endregion

        #region State

// Current threat assessment
        private ThreatAssessment _currentThreat;
        private float _lastScanTime;
  
        // Tracked enemies and their states
        private Dictionary<Character, EnemyState> _trackedEnemies = new Dictionary<Character, EnemyState>();
        
      // Known attack patterns (prefab name -> pattern data)
        private static Dictionary<string, AttackPattern[]> _knownPatterns;
        
        public static bool VerboseLogging = false;

        #endregion

        #region Enums & Data Structures

        public enum ThreatLevel
        {
  None,   // No threats nearby
    Low,            // Enemies present but not attacking
Medium,         // Enemy winding up attack, time to prepare
            High,           // Attack imminent, should dodge/block NOW
            Critical        // Multiple simultaneous threats
        }

    public enum RecommendedAction
        {
   None,           // Continue current action
            Attack,     // Safe to attack
            PrepareBlock,   // Raise shield, attack coming
            Parry,          // Perfect parry window
   Dodge,   // Dodge now!
     Retreat,        // Too many threats, back off
        Interrupt       // Attack to interrupt enemy wind-up
        }

 public struct ThreatAssessment
        {
          public ThreatLevel Level;
     public RecommendedAction Action;
 public Character PrimaryThreat;
            public float TimeToImpact;
     public Vector3 ThreatDirection;
 public bool IsProjectile;
     public int SimultaneousThreats;
      
         public static ThreatAssessment Safe => new ThreatAssessment
{
      Level = ThreatLevel.None,
       Action = RecommendedAction.Attack,
        TimeToImpact = float.MaxValue
};
        }

        private class EnemyState
        {
        public Character Enemy;
       public Animator Animator;
      public float LastAttackTime;
     public float AttackCooldown;
            public bool IsWindingUp;
        public float WindUpStartTime;
       public string CurrentAnimation;
      public Vector3 LastPosition;
            public float LastUpdateTime;
         public AttackPattern? CurrentPattern;
        }

        public struct AttackPattern
        {
     public string AnimationTrigger;     // Animation name/trigger
            public float WindUpDuration;        // Time from start to damage
       public float TotalDuration;         // Full attack duration
            public float DamageWindowStart;     // When damage starts
         public float DamageWindowEnd;       // When damage ends
            public float Range;         // Attack range
            public bool IsAOE;          // Area attack?
  public bool CanBeParried;       // Can we parry this?
    public bool CanBeInterrupted;       // Can we interrupt wind-up?
            public float StaggerOnParry;        // Stagger time if parried
        }

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _combatMovement = GetComponent<CompanionCombatMovement>();
            _character = GetComponent<Character>();
            _combatExperience = GetComponent<CombatExperience>();
            _staminaManager = GetComponent<StaminaManager>();
            InitializeKnownPatterns();
        }

        private void Update()
        {
            // Allow both tamed AND wild companions to recognize enemy attacks
            // Wild companions need this to defend themselves
            if (_companion == null) return;
            if (!_combatMovement?.IsInCombat ?? true) return;
      
         // OPTIMIZATION: Don't scan threats if we're just chasing a distant target
  // This prevents unnecessary threat calculations when approaching
    var companionAI = _companion?.GetCompanionAI();
   var currentTarget = companionAI?.GetTargetCreature();
         if (currentTarget != null)
     {
    float distToTarget = Vector3.Distance(transform.position, currentTarget.transform.position);
  // Only scan threats when we're close enough to actually need defensive actions
    if (distToTarget > meleeDangerRange * 2f)
             {
    // Far from target - set safe threat level and skip scanning
           _currentThreat = ThreatAssessment.Safe;
 return;
       }
            }
         
 // Throttled scanning
    if (Time.time - _lastScanTime >= scanInterval)
      {
            _lastScanTime = Time.time;
 ScanThreats();
     }
         
    // Continuous tracking of known enemies
            UpdateTrackedEnemies();
     }

        #endregion

        #region Public API

    /// <summary>
        /// Gets the current threat assessment.
    /// </summary>
        public ThreatAssessment GetCurrentThreat()
{
    return _currentThreat;
   }

        /// <summary>
        /// Checks if we should dodge RIGHT NOW.
        /// SURVIVAL INSTINCT: If stamina is critically low, DON'T DODGE - retreat instead.
        /// Dodging uses stamina we can't afford to spend.
        /// </summary>
        public bool ShouldDodgeNow()
        {
            // Check if StaminaManager says we should treat this as a survival emergency
            if (_staminaManager != null)
            {
                // SURVIVAL INSTINCT: Low stamina = don't dodge, just run
                // Dodging costs stamina we need to regenerate
                if (_staminaManager.ShouldTreatAsLowHealth())
                {
                    return false;  // Don't recommend ANY defensive action
                }
                
                // During critical recovery - NO dodging at all
                if (_staminaManager.IsInCriticalRecovery())
                {
                    return false;
                }
                
                // During normal recovery - NO dodging (conserve stamina)
                if (_staminaManager.IsRecovering())
                {
                    return false;  // Changed: was allowing some dodging, now none
                }
                
                // Not in recovery - still check if we can afford the dodge
                if (!_staminaManager.CanDodge())
                {
                    return false;
                }
            }
            
            return _currentThreat.Level >= ThreatLevel.High && 
                   _currentThreat.Action == RecommendedAction.Dodge &&
                   _currentThreat.TimeToImpact < imminentAttackWindow;
        }

        /// <summary>
        /// Checks if we should block/parry.
        /// SURVIVAL INSTINCT: If stamina is critically low, DON'T BLOCK - retreat instead.
        /// Blocking costs stamina and if block breaks we're defenseless.
        /// </summary>
        public bool ShouldBlockNow()
        {
            // Check if StaminaManager says we should treat this as a survival emergency
            if (_staminaManager != null)
            {
                // SURVIVAL INSTINCT: Low stamina = don't block, just run
                if (_staminaManager.ShouldTreatAsLowHealth())
                {
                    return false;  // Don't recommend ANY defensive action
                }
                
                // During critical recovery - NO blocking
                if (_staminaManager.IsInCriticalRecovery())
                {
                    return false;
                }
                
                // During normal recovery - NO blocking (conserve stamina)
                if (_staminaManager.IsRecovering())
                {
                    return false;  // Changed: was allowing some blocking, now none
                }
                
                // Not in recovery - still check if we can afford to block
                if (!_staminaManager.CanBlock())
                {
                    return false;
                }
            }
            
            return _currentThreat.Level >= ThreatLevel.Medium &&
                   (_currentThreat.Action == RecommendedAction.PrepareBlock ||
                    _currentThreat.Action == RecommendedAction.Parry);
        }

        /// <summary>
        /// Checks if it's a good time to attack.
        /// Respects StaminaManager recovery state.
        /// </summary>
        public bool IsSafeToAttack()
        {
            // Check StaminaManager first - if in recovery, don't recommend attacking
            if (_staminaManager != null)
            {
                if (_staminaManager.IsInCriticalRecovery())
                {
                    return false; // NEVER attack during critical recovery
                }
                
                if (_staminaManager.IsRecovering())
                {
                    return false; // Don't attack during normal recovery either
                }
                
                if (!_staminaManager.CanAttack())
                {
                    return false; // Stamina too low to attack
                }
            }
            
            return _currentThreat.Level <= ThreatLevel.Low ||
                   _currentThreat.Action == RecommendedAction.Attack ||
                   _currentThreat.Action == RecommendedAction.Interrupt;
        }

        /// <summary>
        /// Gets direction to dodge (perpendicular to threat).
        /// </summary>
        public Vector3 GetDodgeDirection()
     {
            if (_currentThreat.ThreatDirection == Vector3.zero)
       return transform.right; // Default to side dodge
            
    // Dodge perpendicular to threat direction
 Vector3 perpendicular = Vector3.Cross(Vector3.up, _currentThreat.ThreatDirection);
    
            // Randomly pick left or right
            if (UnityEngine.Random.value < 0.5f)
    perpendicular = -perpendicular;
  
            return perpendicular.normalized;
        }

     /// <summary>
    /// Checks if a specific enemy is currently attacking.
        /// </summary>
      public bool IsEnemyAttacking(Character enemy)
        {
     if (enemy == null) return false;
 if (_trackedEnemies.TryGetValue(enemy, out var state))
            {
       return state.IsWindingUp || Time.time - state.LastAttackTime < 0.5f;
     }
       return false;
        }

        /// <summary>
        /// Gets estimated time until enemy can attack again.
        /// </summary>
        public float GetEnemyAttackCooldown(Character enemy)
        {
            if (enemy == null) return 0f;
        if (_trackedEnemies.TryGetValue(enemy, out var state))
            {
 float timeSinceAttack = Time.time - state.LastAttackTime;
     return Mathf.Max(0f, state.AttackCooldown - timeSinceAttack);
   }
    return 0f;
        }

        #endregion

        #region Threat Scanning

     private void ScanThreats()
        {
            _currentThreat = ThreatAssessment.Safe;
            
     var enemies = FindNearbyEnemies();
    if (enemies.Count == 0) return;
            
 float highestThreat = 0f;
      Character primaryThreat = null;
    int simultaneousThreats = 0;
     
            foreach (var enemy in enemies)
            {
      float threatScore = EvaluateEnemyThreat(enemy);
      
         if (threatScore > 0.5f)
         simultaneousThreats++;
     
if (threatScore > highestThreat)
    {
    highestThreat = threatScore;
         primaryThreat = enemy;
                }
     }
            
         if (primaryThreat != null)
      {
        _currentThreat = BuildThreatAssessment(primaryThreat, highestThreat, simultaneousThreats);
          }
        }

        private List<Character> FindNearbyEnemies()
        {
        List<Character> enemies = new List<Character>();
    Vector3 myPos = transform.position;
    
    foreach (var character in Character.GetAllCharacters())
  {
     if (character == null || character.IsDead()) continue;
     if (character == _character) continue;
     if (character.IsTamed() || character.IsPlayer()) continue;
           
      float dist = Vector3.Distance(myPos, character.transform.position);
         if (dist <= detectionRange)
    {
enemies.Add(character);
    
       // Start tracking if not already
         if (!_trackedEnemies.ContainsKey(character))
          {
 StartTrackingEnemy(character);
         }
            }
          }
            
      // Clean up enemies that are gone
    var toRemove = new List<Character>();
  foreach (var tracked in _trackedEnemies.Keys)
            {
     if (tracked == null || tracked.IsDead() || !enemies.Contains(tracked))
  {
  toRemove.Add(tracked);
                }
            }
            foreach (var enemy in toRemove)
            {
     _trackedEnemies.Remove(enemy);
      }
        
 return enemies;
        }

        private void StartTrackingEnemy(Character enemy)
        {
       var state = new EnemyState
            {
          Enemy = enemy,
         Animator = enemy.GetComponentInChildren<Animator>(),
      LastPosition = enemy.transform.position,
         LastUpdateTime = Time.time,
         AttackCooldown = GetDefaultAttackCooldown(enemy)
      };
     
     _trackedEnemies[enemy] = state;
        }

        private float EvaluateEnemyThreat(Character enemy)
        {
            if (!_trackedEnemies.TryGetValue(enemy, out var state))
                return 0f;
      
            float threat = 0f;
            float dist = Vector3.Distance(transform.position, enemy.transform.position);
            
            // Get level-based danger recognition multiplier
            float dangerMultiplier = 1f;
            if (_combatExperience != null)
            {
                dangerMultiplier = _combatExperience.DangerRecognitionMultiplier;
            }
      
            // Base threat from distance - improved with level
            if (dist < meleeDangerRange)
            {
                threat += Mathf.InverseLerp(meleeDangerRange, 0f, dist) * 0.5f * dangerMultiplier;
            }
            
            // Check if enemy is targeting us or our owner
            var enemyAI = enemy.GetComponent<BaseAI>();
            if (enemyAI != null)
            {
                var target = enemyAI.GetTargetCreature();
                if (target == _character)
                {
                    threat += 0.3f * dangerMultiplier;
                }
                else if (_companion?.GetOwner() != null)
                {
                    var ownerChar = _companion.GetOwner().GetComponent<Character>();
                    if (target == ownerChar)
                    {
                        threat += 0.3f * ownerThreatMultiplier * dangerMultiplier;
                    }
                }
            }
            
            // Check if currently attacking - level helps predict attacks earlier
            if (state.IsWindingUp)
            {
                threat += 0.5f * dangerMultiplier;
   
                // Higher threat as attack gets closer to landing
                if (state.CurrentPattern.HasValue)
                {
                    float elapsed = Time.time - state.WindUpStartTime;
                    float progress = elapsed / state.CurrentPattern.Value.WindUpDuration;
                    threat += progress * 0.3f * dangerMultiplier;
                }
            }
      
            // Check movement toward us (charging) - better detection at higher levels
            Vector3 velocity = (enemy.transform.position - state.LastPosition) / Mathf.Max(0.01f, Time.time - state.LastUpdateTime);
            Vector3 toMe = (transform.position - enemy.transform.position).normalized;
            float approachSpeed = Vector3.Dot(velocity, toMe);
 
            if (approachSpeed > 2f) // Running toward us
            {
                threat += 0.2f * dangerMultiplier;
            }
          
            return Mathf.Clamp01(threat);
        }

        private ThreatAssessment BuildThreatAssessment(Character enemy, float threatScore, int simultaneousThreats)
        {
            var assessment = new ThreatAssessment();
            assessment.PrimaryThreat = enemy;
            assessment.SimultaneousThreats = simultaneousThreats;
            assessment.ThreatDirection = (transform.position - enemy.transform.position).normalized;
            
            // Get level-based parry window from CombatExperience
            float effectiveParryWindow = parryWindow;
            float effectiveImminentWindow = imminentAttackWindow;
            
            if (_combatExperience != null)
            {
                effectiveParryWindow = _combatExperience.ParryWindow;
                effectiveImminentWindow = _combatExperience.AnticipationWindow + imminentAttackWindow;
            }
     
            // Determine threat level
            if (simultaneousThreats >= 3)
            {
                assessment.Level = ThreatLevel.Critical;
            }
            else if (threatScore >= 0.8f)
            {
                assessment.Level = ThreatLevel.High;
            }
            else if (threatScore >= 0.5f)
            {
                assessment.Level = ThreatLevel.Medium;
            }
            else if (threatScore >= 0.2f)
            {
                assessment.Level = ThreatLevel.Low;
            }
            else
            {
                assessment.Level = ThreatLevel.None;
            }
    
            // Determine recommended action and time to impact
            if (_trackedEnemies.TryGetValue(enemy, out var state) && state.IsWindingUp && state.CurrentPattern.HasValue)
            {
                float elapsed = Time.time - state.WindUpStartTime;
                assessment.TimeToImpact = state.CurrentPattern.Value.DamageWindowStart - elapsed;
   
                // Use level-based parry window
                if (assessment.TimeToImpact <= effectiveParryWindow && state.CurrentPattern.Value.CanBeParried)
                {
                    assessment.Action = RecommendedAction.Parry;
                }
                else if (assessment.TimeToImpact <= effectiveImminentWindow)
                {
                    assessment.Action = RecommendedAction.Dodge;
                }
                else if (state.CurrentPattern.Value.CanBeInterrupted && assessment.TimeToImpact > 0.3f)
                {
                    assessment.Action = RecommendedAction.Interrupt;
                }
                else
                {
                    assessment.Action = RecommendedAction.PrepareBlock;
                }
            }
            else if (assessment.Level == ThreatLevel.Critical)
            {
                assessment.Action = RecommendedAction.Retreat;
                assessment.TimeToImpact = 1f;
            }
            else if (assessment.Level >= ThreatLevel.Medium)
            {
                assessment.Action = RecommendedAction.PrepareBlock;
                assessment.TimeToImpact = 1f;
            }
            else
            {
                assessment.Action = RecommendedAction.Attack;
                assessment.TimeToImpact = float.MaxValue;
            }
     
            return assessment;
        }

        #endregion

 #region Enemy State Tracking

    private void UpdateTrackedEnemies()
     {
         foreach (var kvp in _trackedEnemies)
      {
     var enemy = kvp.Key;
    var state = kvp.Value;
      
    if (enemy == null || enemy.IsDead()) continue;
              
   // Update position tracking
 state.LastPosition = enemy.transform.position;
           state.LastUpdateTime = Time.time;
     
      // Check animator for attack states
    if (state.Animator != null)
      {
 DetectAttackAnimation(state);
   }
            }
   }

        private void DetectAttackAnimation(EnemyState state)
    {
 if (state.Animator == null) return;
  
   var stateInfo = state.Animator.GetCurrentAnimatorStateInfo(0);
          string currentAnim = GetAnimationName(stateInfo);
     
   // Check if animation changed
            if (currentAnim != state.CurrentAnimation)
{
  state.CurrentAnimation = currentAnim;
             
     // Check if this is an attack animation
      var pattern = GetAttackPattern(state.Enemy, currentAnim);
  if (pattern.HasValue)
           {
             state.IsWindingUp = true;
    state.WindUpStartTime = Time.time;
          state.CurrentPattern = pattern;
    
if (VerboseLogging)
       {
  Debug.Log($"[EnemyAttackRecognition] Detected attack: {state.Enemy.m_name} - {currentAnim}");
   }
        }
     }
    
      // Check if attack completed
      if (state.IsWindingUp && state.CurrentPattern.HasValue)
 {
    float elapsed = Time.time - state.WindUpStartTime;
              if (elapsed >= state.CurrentPattern.Value.TotalDuration)
                {
         state.IsWindingUp = false;
                    state.LastAttackTime = Time.time;
   state.CurrentPattern = null;
            }
          }
        }

        private string GetAnimationName(AnimatorStateInfo stateInfo)
        {
        // Common attack animation names to check
 string[] attackNames = { "attack", "swing", "slam", "bite", "charge", "throw", "shoot" };
      
foreach (var name in attackNames)
            {
   if (stateInfo.IsName(name) || stateInfo.IsTag(name))
         {
         return name;
   }
         }
   
       return stateInfo.shortNameHash.ToString();
        }

        private AttackPattern? GetAttackPattern(Character enemy, string animationName)
        {
        if (enemy == null) return null;
   
       string prefabName = GetPrefabName(enemy);
     
// Check known patterns
    if (_knownPatterns != null && _knownPatterns.TryGetValue(prefabName, out var patterns))
          {
            foreach (var pattern in patterns)
      {
           if (animationName.Contains(pattern.AnimationTrigger.ToLower()))
            {
     return pattern;
       }
   }
            }
            
   // Generic attack detection
  if (animationName.Contains("attack") || animationName.Contains("swing") || 
     animationName.Contains("slam") || animationName.Contains("bite"))
            {
    return new AttackPattern
     {
 AnimationTrigger = animationName,
          WindUpDuration = 0.5f,
       TotalDuration = 1.5f,
        DamageWindowStart = 0.4f,
      DamageWindowEnd = 0.8f,
            Range = 3f,
  CanBeParried = true,
      CanBeInterrupted = false
          };
 }
       
            return null;
        }

     private string GetPrefabName(Character character)
    {
      if (character == null) return "";
      string name = character.gameObject.name;
          // Remove "(Clone)" suffix
    int cloneIndex = name.IndexOf("(Clone)");
     if (cloneIndex > 0)
         name = name.Substring(0, cloneIndex);
            return name.ToLower();
   }

     private float GetDefaultAttackCooldown(Character enemy)
     {
            // Estimate attack cooldown based on enemy type
            string prefabName = GetPrefabName(enemy);
       
        if (prefabName.Contains("troll")) return 3f;
            if (prefabName.Contains("deathsquito")) return 1f;
    if (prefabName.Contains("fuling")) return 1.5f;
            if (prefabName.Contains("draugr")) return 2f;
       if (prefabName.Contains("skeleton")) return 1.5f;
    if (prefabName.Contains("greydwarf")) return 2f;
     if (prefabName.Contains("wolf")) return 1.2f;
   if (prefabName.Contains("boar")) return 2f;
       
            return 2f; // Default
        }

  #endregion

 #region Known Patterns Initialization

        private static void InitializeKnownPatterns()
    {
     if (_knownPatterns != null) return;
   
 _knownPatterns = new Dictionary<string, AttackPattern[]>();
            
      // Troll
            _knownPatterns["troll"] = new AttackPattern[]
            {
    new AttackPattern
      {
         AnimationTrigger = "swing",
        WindUpDuration = 1.0f,
  TotalDuration = 2.5f,
           DamageWindowStart = 0.9f,
     DamageWindowEnd = 1.3f,
        Range = 5f,
      IsAOE = true,
     CanBeParried = false,
  CanBeInterrupted = false,
              StaggerOnParry = 0f
                },
                new AttackPattern
 {
     AnimationTrigger = "throw",
   WindUpDuration = 1.2f,
     TotalDuration = 2.0f,
     DamageWindowStart = 1.1f,
          DamageWindowEnd = 1.5f,
      Range = 20f,
     IsAOE = true,
     CanBeParried = false,
       CanBeInterrupted = false
    }
      };
  
     // Fuling
            _knownPatterns["goblin"] = new AttackPattern[]
          {
            new AttackPattern
           {
         AnimationTrigger = "attack",
        WindUpDuration = 0.4f,
TotalDuration = 1.2f,
     DamageWindowStart = 0.35f,
             DamageWindowEnd = 0.6f,
     Range = 2.5f,
     CanBeParried = true,
    CanBeInterrupted = true,
   StaggerOnParry = 1.5f
            }
        };
    
    // Draugr
            _knownPatterns["draugr"] = new AttackPattern[]
    {
             new AttackPattern
       {
    AnimationTrigger = "attack",
   WindUpDuration = 0.6f,
    TotalDuration = 1.5f,
    DamageWindowStart = 0.5f,
           DamageWindowEnd = 0.9f,
         Range = 2.5f,
   CanBeParried = true,
  CanBeInterrupted = false,
         StaggerOnParry = 2f
            }
   };
  
            // Skeleton
_knownPatterns["skeleton"] = new AttackPattern[]
{
   new AttackPattern
    {
        AnimationTrigger = "attack",
      WindUpDuration = 0.5f,
   TotalDuration = 1.3f,
          DamageWindowStart = 0.4f,
  DamageWindowEnd = 0.7f,
          Range = 2f,
          CanBeParried = true,
      CanBeInterrupted = true,
      StaggerOnParry = 1.5f
  }
        };
            
            // Greydwarf
    _knownPatterns["greydwarf"] = new AttackPattern[]
     {
  new AttackPattern
     {
      AnimationTrigger = "attack",
         WindUpDuration = 0.4f,
          TotalDuration = 1.2f,
           DamageWindowStart = 0.35f,
   DamageWindowEnd = 0.6f,
   Range = 2f,
           CanBeParried = true,
        CanBeInterrupted = true,
           StaggerOnParry = 1f
                }
            };

            // Deathsquito - very fast
            _knownPatterns["deathsquito"] = new AttackPattern[]
     {
          new AttackPattern
    {
                AnimationTrigger = "attack",
           WindUpDuration = 0.15f,
                    TotalDuration = 0.5f,
  DamageWindowStart = 0.1f,
            DamageWindowEnd = 0.2f,
          Range = 1.5f,
 CanBeParried = true,
        CanBeInterrupted = false,
      StaggerOnParry = 3f
    }
            };
        }

        #endregion
}
}
