using UnityEngine;
using System;
using System.Collections.Generic;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Keeps combat positioning out of hazards (water, fire, cliffs and drops, Mistlands poison, Plains tar,
    /// Ashlands lava) by reporting safe directions and adjusting retreat movement.
    /// </summary>
    public class TerrainAwareness : MonoBehaviour
    {
        private const float SafeDirectionScoreThreshold = 0.7f;
        private const float AoeEscapeRadiusMultiplier = 1.5f;
        private const float BasePositionScore = 50f;
        private const float CombatPositionSampleSpacing = 3f;
        private const float MaxEnemyDistanceScore = 10f;
        private const float BlockedDirectionScoreThreshold = 0.3f;
        private const float WaterDirectionPenalty = 0.4f;
        private const float ObstacleDirectionPenalty = 0.3f;
        private const float HazardProbeDistance = 3f;
        private const float LavaDirectionPenalty = 0.9f;
        private const float FireDangerRange = 3f;
        private const float LavaCheckRangeMultiplier = 1.5f;
        private const float MinLavaDepth = 0.3f;
        private const float LavaDirectionMaxScore = 0.1f;
        private const float AoeCheckRangeMultiplier = 1.5f;
        private const float DefaultAoeRadius = 3f;

        #region Settings

        [Header("Detection Settings")]
        [Tooltip("How far to check for hazards")]
   public float hazardCheckDistance = 8f;
        
        [Tooltip("How often to scan for hazards (seconds)")]
        public float scanInterval = 0.25f;
        
        [Tooltip("Number of directions to sample")]
        public int directionSamples = 8;
     
     [Tooltip("Height difference considered a cliff")]
        public float cliffThreshold = 4f;

        [Header("Water Detection")]
        [Tooltip("Water depth considered dangerous")]
        public float dangerousWaterDepth = 1.5f;
        
  [Tooltip("Distance to keep from water edge")]
        public float waterSafetyMargin = 2f;

        [Header("Position Scoring")]
        [Tooltip("Bonus for high ground")]
        public float highGroundBonus = 2f;
        
     [Tooltip("Penalty for being near hazards")]
        public float hazardPenalty = 5f;
        
        [Tooltip("Bonus for having cover nearby")]
        public float coverBonus = 1.5f;

   #endregion

  #region Components

        private CompanionController _companion;
     private CompanionCombatMovement _combatMovement;
        private Character _character;

        #endregion

        #region State

        // Cached hazard data
      private HazardMap _currentHazards;
        private float _lastScanTime;
        
        // Direction safety scores (0 = blocked, 1 = safe)
        private float[] _directionScores;
   private Vector3[] _sampleDirections;
        
        // Cached environmental state
        private bool _isInWater;
     private bool _isNearCliff;
        private bool _isNearFire;
        private bool _isNearLava;
        private bool _isNearAoe;
        
        // AOE hazard tracking
        private List<ActiveAoeHazard> _activeAoeHazards = new List<ActiveAoeHazard>();
        private float _lastAoeScanTime;
        private const float AoeScanInterval = 0.15f; // Scan AOEs more frequently
        
        public static bool VerboseLogging = false;

        #endregion

        #region Data Structures

        public struct HazardMap
        {
     public bool WaterNearby;
        public bool CliffNearby;
            public bool FireNearby;
            public bool PoisonNearby;
            public bool TarNearby;
            public bool LavaNearby;
            public bool AoeHazardNearby;
public Vector3 NearestWaterDirection;
  public Vector3 NearestCliffDirection;
   public Vector3 NearestFireDirection;
            public Vector3 NearestLavaDirection;
            public Vector3 NearestAoeDirection;
        public float DistanceToWater;
  public float DistanceToCliff;
            public float DistanceToFire;
            public float DistanceToLava;
            public float DistanceToAoe;
      public Vector3 SafestDirection;
      public Vector3 BestCombatPosition;
     public int BlockedDirections;
            public List<ActiveAoeHazard> ActiveAoeHazards;
        }
        
        /// <summary>
        /// Represents an active AOE hazard zone on the ground.
        /// </summary>
        public struct ActiveAoeHazard
        {
            public Vector3 Position;
            public float Radius;
            public HazardType Type;
            public float TimeRemaining;
            public string SourceName;
        }

        public struct PositionScore
    {
    public Vector3 Position;
public float Score;
            public bool IsSafe;
     public bool HasHighGround;
            public bool HasCover;
 public float DistanceToHazard;
        }

        public enum HazardType
  {
      None,
            Water,
   Cliff,
Fire,
            Poison,
            Tar,
       Lava,
            AoeEffect,
            AoeFire,
            AoePoison,
            AoeFrost,
            AoeLightning
        }

     #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _combatMovement = GetComponent<CompanionCombatMovement>();
          _character = GetComponent<Character>();
     
            InitializeDirectionSamples();
  }

        private void Update()
        {
            // Allow both tamed AND wild companions to use terrain awareness
            if (_companion == null) return;
    
   // Throttled scanning
            if (Time.time - _lastScanTime >= scanInterval)
            {
       _lastScanTime = Time.time;
    ScanEnvironment();
            }
      }

        #endregion

    #region Public API

   /// <summary>
   /// Gets the current hazard map.
        /// </summary>
   public HazardMap GetHazardMap()
    {
            return _currentHazards;
        }

        /// <summary>
        /// Checks if a direction is safe to move in.
        /// </summary>
  public bool IsDirectionSafe(Vector3 direction)
    {
   if (_directionScores == null) return true;
            
    // Find closest sample direction
          int closest = GetClosestDirectionIndex(direction);
         return _directionScores[closest] > 0.5f;
        }

        /// <summary>
        /// Gets the safety score for a direction (0-1).
        /// </summary>
   public float GetDirectionScore(Vector3 direction)
        {
  if (_directionScores == null) return 1f;
       
         int closest = GetClosestDirectionIndex(direction);
            return _directionScores[closest];
 }

        /// <summary>
        /// Adjusts a desired movement direction to avoid hazards.
        /// </summary>
        public Vector3 GetSafeMovementDirection(Vector3 desiredDirection)
 {
          if (_directionScores == null) return desiredDirection;
            
   // Check if desired direction is safe
 int closestIndex = GetClosestDirectionIndex(desiredDirection);
            if (_directionScores[closestIndex] > SafeDirectionScoreThreshold)
          {
     return desiredDirection; // Original direction is safe
            }
   
            // Find nearest safe direction
            Vector3 bestDirection = desiredDirection;
   float bestScore = 0f;
  float bestAlignment = -1f;
       
            for (int i = 0; i < directionSamples; i++)
       {
         if (_directionScores[i] < 0.5f) continue;
       
       float alignment = Vector3.Dot(desiredDirection.normalized, _sampleDirections[i]);
    float score = _directionScores[i] * (0.5f + 0.5f * alignment);
          
          if (score > bestScore || (score == bestScore && alignment > bestAlignment))
      {
     bestScore = score;
           bestAlignment = alignment;
         bestDirection = _sampleDirections[i];
        }
     }
    
     // Blend toward safe direction
            float dangerLevel = 1f - _directionScores[closestIndex];
      return Vector3.Lerp(desiredDirection, bestDirection, dangerLevel).normalized;
        }

        /// <summary>
  /// Gets the safest retreat direction.
     /// </summary>
        public Vector3 GetSafeRetreatDirection(Vector3 threatDirection)
      {
      Vector3 idealRetreat = -threatDirection.normalized;
   return GetSafeMovementDirection(idealRetreat);
        }

        /// <summary>
        /// Checks if current position is dangerous.
        /// </summary>
        public bool IsCurrentPositionDangerous()
   {
    return _isInWater || _isNearCliff || _isNearFire || _isNearLava || IsInAoeHazard();
  }

        /// <summary>
        /// Checks if we're near lava (especially in Ashlands).
        /// </summary>
        public bool IsNearLava()
        {
            return _isNearLava;
        }
        
        /// <summary>
        /// Checks if we're currently standing in an AOE hazard.
        /// </summary>
        public bool IsInAoeHazard()
        {
            Vector3 myPos = transform.position;
            foreach (var aoe in _activeAoeHazards)
            {
                float dist = Vector3.Distance(myPos, aoe.Position);
                if (dist < aoe.Radius)
                {
                    return true;
                }
            }
            return false;
        }
        
        /// <summary>
        /// Gets all currently active AOE hazards near the companion.
        /// </summary>
        public List<ActiveAoeHazard> GetActiveAoeHazards()
        {
            return _activeAoeHazards;
        }
        
        /// <summary>
        /// Gets the direction to move to escape the nearest AOE hazard.
        /// </summary>
        public Vector3 GetAoeEscapeDirection()
        {
            if (_activeAoeHazards.Count == 0) return Vector3.zero;
            
            Vector3 myPos = transform.position;
            Vector3 escapeDir = Vector3.zero;
            
            // Sum up escape vectors from all nearby AOEs
            foreach (var aoe in _activeAoeHazards)
            {
                float dist = Vector3.Distance(myPos, aoe.Position);
                if (dist < aoe.Radius * AoeEscapeRadiusMultiplier) // Also escape from nearby AOEs
                {
                    Vector3 awayFromAoe = (myPos - aoe.Position).normalized;
                    float urgency = 1f - (dist / (aoe.Radius * AoeEscapeRadiusMultiplier));
                    escapeDir += awayFromAoe * urgency;
                }
            }
            
            if (escapeDir.sqrMagnitude > 0.01f)
            {
                return GetSafeMovementDirection(escapeDir.normalized);
            }
            
            return _currentHazards.SafestDirection;
        }

        /// <summary>
        /// Scores a potential combat position.
        /// </summary>
  public PositionScore ScorePosition(Vector3 position, Character enemy = null)
        {
var score = new PositionScore
    {
     Position = position,
        Score = BasePositionScore, // Base score
       IsSafe = true
       };
     
       // Check for hazards at position
      var hazard = CheckHazardAtPosition(position);
    if (hazard != HazardType.None)
   {
        score.IsSafe = false;
    score.Score -= hazardPenalty * 2;
            }
     
            // Check ground height
       float myHeight = transform.position.y;
            if (ZoneSystem.instance != null)
  {
        float groundHeight;
      if (ZoneSystem.instance.GetGroundHeight(position, out groundHeight))
            {
      if (groundHeight > myHeight + 1f)
        {
   score.HasHighGround = true;
       score.Score += highGroundBonus;
         }
         }
            }
            
            // Check for cover (obstacles between us and enemy)
            if (enemy != null)
            {
  Vector3 toEnemy = enemy.transform.position - position;
  if (Physics.Raycast(position + Vector3.up, toEnemy.normalized, toEnemy.magnitude * 0.5f, LayerMask.GetMask("piece", "static_solid")))
              {
 score.HasCover = true;
 score.Score += coverBonus;
      }
            }

    // Distance to nearest hazard
     score.DistanceToHazard = GetDistanceToNearestHazard(position);
            if (score.DistanceToHazard < hazardCheckDistance)
            {
    float proximityPenalty = (1f - score.DistanceToHazard / hazardCheckDistance) * hazardPenalty;
    score.Score -= proximityPenalty;
 }
    
            return score;
        }

     /// <summary>
 /// Finds the best combat position within range.
     /// </summary>
 public Vector3 FindBestCombatPosition(Character enemy, float maxRange)
  {
       if (enemy == null) return transform.position;
  
    Vector3 bestPos = transform.position;
          float bestScore = float.MinValue;
            
         // Sample positions around current location
  for (int i = 0; i < directionSamples; i++)
      {
      for (float dist = CombatPositionSampleSpacing; dist <= maxRange; dist += CombatPositionSampleSpacing)
                {
          Vector3 samplePos = transform.position + _sampleDirections[i] * dist;
   
        // Get ground height
      if (ZoneSystem.instance != null)
 {
   float groundHeight;
    if (ZoneSystem.instance.GetGroundHeight(samplePos, out groundHeight))
             {
  samplePos.y = groundHeight;
   }
            }
   
             var posScore = ScorePosition(samplePos, enemy);
             if (!posScore.IsSafe) continue;
     
           // Factor in distance to enemy
          float distToEnemy = Vector3.Distance(samplePos, enemy.transform.position);
    float optimalDist = 3f; // Melee range
float distScore = MaxEnemyDistanceScore - Mathf.Abs(distToEnemy - optimalDist);
           
        float totalScore = posScore.Score + distScore;
if (totalScore > bestScore)
  {
           bestScore = totalScore;
             bestPos = samplePos;
                  }
       }
    }
    
      return bestPos;
        }

        /// <summary>
        /// Checks if we're in water.
        /// </summary>
        public bool IsInWater()
  {
 return _isInWater;
        }

        /// <summary>
        /// Checks if we're near a cliff edge.
        /// </summary>
      public bool IsNearCliff()
        {
       return _isNearCliff;
        }

        #endregion

        #region Environment Scanning

        private void InitializeDirectionSamples()
        {
       _directionScores = new float[directionSamples];
        _sampleDirections = new Vector3[directionSamples];
        
    for (int i = 0; i < directionSamples; i++)
     {
       float angle = (360f / directionSamples) * i * Mathf.Deg2Rad;
          _sampleDirections[i] = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
      _directionScores[i] = 1f;
}
 }

    private void ScanEnvironment()
        {
            _currentHazards = new HazardMap();
            _currentHazards.DistanceToWater = float.MaxValue;
          _currentHazards.DistanceToCliff = float.MaxValue;
   _currentHazards.DistanceToFire = float.MaxValue;
            _currentHazards.DistanceToLava = float.MaxValue;
            _currentHazards.DistanceToAoe = float.MaxValue;
            _currentHazards.ActiveAoeHazards = new List<ActiveAoeHazard>();
            
    Vector3 myPos = transform.position;
            
            // Check water at current position
      _isInWater = CheckWaterLevel(myPos) > 0.5f;
            
            // Check for AOE hazards (more frequently than other checks)
            if (Time.time - _lastAoeScanTime >= AoeScanInterval)
            {
                _lastAoeScanTime = Time.time;
                CheckForAoeHazards(myPos);
            }
            
     // Scan each direction
    float bestScore = 0f;
            int blockedCount = 0;
            
            for (int i = 0; i < directionSamples; i++)
            {
  _directionScores[i] = ScoreDirection(_sampleDirections[i], myPos);
           
 if (_directionScores[i] < BlockedDirectionScoreThreshold)
    blockedCount++;
    
       if (_directionScores[i] > bestScore)
     {
        bestScore = _directionScores[i];
       _currentHazards.SafestDirection = _sampleDirections[i];
         }
        }
 
  _currentHazards.BlockedDirections = blockedCount;
  
    // Check for specific hazards
            CheckForWater(myPos);
        CheckForCliffs(myPos);
      CheckForFire(myPos);
            CheckForLava(myPos);
  
  _isNearCliff = _currentHazards.CliffNearby;
            _isNearFire = _currentHazards.FireNearby;
            _isNearLava = _currentHazards.LavaNearby;
            _isNearAoe = _currentHazards.AoeHazardNearby;
        }

        private float ScoreDirection(Vector3 direction, Vector3 fromPos)
  {
            float score = 1f;
            
          // Check multiple distances
      for (float dist = 2f; dist <= hazardCheckDistance; dist += 2f)
       {
            Vector3 checkPos = fromPos + direction * dist;
      
                // Ground height check (cliff detection)
                if (ZoneSystem.instance != null)
             {
       float groundHeight;
    if (ZoneSystem.instance.GetGroundHeight(checkPos, out groundHeight))
        {
             float heightDiff = fromPos.y - groundHeight;
   if (heightDiff > cliffThreshold)
          {
        // Cliff detected
             float cliffPenalty = Mathf.InverseLerp(hazardCheckDistance, 2f, dist);
            score -= cliffPenalty * 0.5f;
               }
       }
          }
      
 // Water check
            float waterLevel = CheckWaterLevel(checkPos);
   if (waterLevel > dangerousWaterDepth)
                {
     float waterPenalty = Mathf.InverseLerp(hazardCheckDistance, 2f, dist);
         score -= waterPenalty * WaterDirectionPenalty;
             }
   
        // Obstacle check (can't move there)
      if (Physics.Raycast(fromPos + Vector3.up, direction, dist, LayerMask.GetMask("piece", "static_solid", "terrain")))
        {
          float obstaclePenalty = Mathf.InverseLerp(hazardCheckDistance, 2f, dist) * ObstacleDirectionPenalty;
       score -= obstaclePenalty;
          }
 }
       
            // Check for fire/hazard objects
            var hazard = CheckHazardAtPosition(fromPos + direction * HazardProbeDistance);
     if (hazard == HazardType.Fire || hazard == HazardType.Poison || hazard == HazardType.Tar)
       {
         score -= 0.5f;
            }
            if (hazard == HazardType.Lava)
            {
                // Lava is instant death - heavily penalize this direction
                score -= LavaDirectionPenalty;
            }
            
            // Check for AOE hazards in this direction
            float aoePenalty = GetAoePenaltyForDirection(fromPos, direction);
            score -= aoePenalty;
      
        return Mathf.Clamp01(score);
        }

        private void CheckForWater(Vector3 pos)
        {
    // Check in a circle for water
       for (int i = 0; i < directionSamples; i++)
            {
        for (float dist = 2f; dist <= hazardCheckDistance; dist += 2f)
    {
      Vector3 checkPos = pos + _sampleDirections[i] * dist;
    float waterLevel = CheckWaterLevel(checkPos);
        
              if (waterLevel > dangerousWaterDepth)
        {
  _currentHazards.WaterNearby = true;
   if (dist < _currentHazards.DistanceToWater)
         {
        _currentHazards.DistanceToWater = dist;
_currentHazards.NearestWaterDirection = _sampleDirections[i];
      }
         break;
           }
       }
       }
        }

        private void CheckForCliffs(Vector3 pos)
    {
            if (ZoneSystem.instance == null) return;
       
         for (int i = 0; i < directionSamples; i++)
         {
    for (float dist = 2f; dist <= hazardCheckDistance; dist += 2f)
      {
Vector3 checkPos = pos + _sampleDirections[i] * dist;
    
     float groundHeight;
          if (ZoneSystem.instance.GetGroundHeight(checkPos, out groundHeight))
          {
         float heightDiff = pos.y - groundHeight;
           if (heightDiff > cliffThreshold)
   {
            _currentHazards.CliffNearby = true;
     if (dist < _currentHazards.DistanceToCliff)
          {
               _currentHazards.DistanceToCliff = dist;
        _currentHazards.NearestCliffDirection = _sampleDirections[i];
   }
   break;
    }
    }
      }
            }
        }

        private void CheckForFire(Vector3 pos)
 {
            // Check for fire sources
            Collider[] colliders = Physics.OverlapSphere(pos, hazardCheckDistance, LayerMask.GetMask("piece", "Default"));
            
   foreach (var collider in colliders)
            {
                if (collider == null) continue;
            
           string objName = collider.gameObject.name.ToLower();
    bool isFire = objName.Contains("fire") || objName.Contains("campfire") || 
         objName.Contains("bonfire") || objName.Contains("torch");
           
     // Also check for Fireplace component
       var fireplace = collider.GetComponent<Fireplace>();
          if (fireplace != null && fireplace.IsBurning())
                {
  isFire = true;
                }
        
         if (isFire)
   {
               float dist = Vector3.Distance(pos, collider.transform.position);
   if (dist < FireDangerRange) // Fire danger range
       {
   _currentHazards.FireNearby = true;
        if (dist < _currentHazards.DistanceToFire)
       {
       _currentHazards.DistanceToFire = dist;
     _currentHazards.NearestFireDirection = (collider.transform.position - pos).normalized;
   }
              }
         }
            }
        }

        /// <summary>
        /// Checks for Ashlands lava pools and other lava hazards.
        /// Lava is INSTANT DEATH - companions must avoid it at all costs.
        /// </summary>
        private void CheckForLava(Vector3 pos)
        {
            // Check for lava in the area - use a larger range since lava is so dangerous
            float lavaCheckRange = hazardCheckDistance * LavaCheckRangeMultiplier;
            
            // Method 1: Check for lava by collider name/tag
            Collider[] colliders = Physics.OverlapSphere(pos, lavaCheckRange, LayerMask.GetMask("piece", "Default", "terrain"));
            
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                string objName = collider.gameObject.name.ToLower();
                
                // Ashlands lava detection - check for various lava object names
                bool isLava = objName.Contains("lava") || 
                              objName.Contains("magma") ||
                              objName.Contains("lavabomb") ||
                              objName.Contains("lava_pool") ||
                              objName.Contains("ashlands_lava");
                
                // Also check parent objects
                if (!isLava)
                {
                    Transform parent = collider.transform.parent;
                    if (parent != null)
                    {
                        string parentName = parent.name.ToLower();
                        isLava = parentName.Contains("lava") || parentName.Contains("magma");
                    }
                }
                
                // Check for LavaOcean or LavaDamage components (if they exist)
                if (!isLava)
                {
                    // Check if object or parent has a component with "lava" or "damage" in the name
                    var components = collider.GetComponents<MonoBehaviour>();
                    foreach (var behaviour in components)
                    {
                        if (behaviour == null) continue;
                        string compName = behaviour.GetType().Name.ToLower();
                        if (compName.Contains("lava") || compName.Contains("lavadamage"))
                        {
                            isLava = true;
                            break;
                        }
                    }
                }
                
                if (isLava)
                {
                    float dist = Vector3.Distance(pos, collider.transform.position);
                    
                    // Lava is dangerous from further away
                    float lavaDangerRange = 5f;
                    if (dist < lavaDangerRange)
                    {
                        _currentHazards.LavaNearby = true;
                        if (dist < _currentHazards.DistanceToLava)
                        {
                            _currentHazards.DistanceToLava = dist;
                            _currentHazards.NearestLavaDirection = (collider.transform.position - pos).normalized;
                        }
                        
                        if (VerboseLogging)
                        {
                            Debug.Log($"[TerrainAwareness] Lava detected: {collider.gameObject.name} at distance {dist:F1}m");
                        }
                    }
                }
            }
            
            // Method 2: Check biome and ground for Ashlands lava
            // In Ashlands, certain terrain is lava even without explicit colliders
            if (WorldGenerator.instance != null)
            {
                Heightmap.Biome biome = WorldGenerator.instance.GetBiome(pos);
                if (biome == Heightmap.Biome.AshLands)
                {
                    // In Ashlands, check if we're in lava ocean area
                    // Lava replaces water in Ashlands below certain heights
                    CheckAshlandsLavaOcean(pos);
                }
            }
        }
        
        /// <summary>
        /// Checks for Ashlands lava ocean (lava replaces water in Ashlands).
        /// </summary>
        private void CheckAshlandsLavaOcean(Vector3 pos)
        {
            // In Ashlands, the "ocean" is actually lava
            // Check water level - in Ashlands, water = lava
            try
            {
                WaterVolume waterVolume = null;
                float waterLevel = Floating.GetWaterLevel(pos, ref waterVolume);
                
                // Get ground height
                float groundHeight = pos.y;
                if (ZoneSystem.instance != null)
                {
                    ZoneSystem.instance.GetGroundHeight(pos, out groundHeight);
                }
                
                // If we're in or near "water" in Ashlands, it's actually lava
                float waterDepth = waterLevel - groundHeight;
                if (waterDepth > 0.5f)
                {
                    // Check distance to the lava edge
                    for (int i = 0; i < directionSamples; i++)
                    {
                        for (float dist = 2f; dist <= hazardCheckDistance; dist += 2f)
                        {
                            Vector3 checkPos = pos + _sampleDirections[i] * dist;
                            
                            // Check if this position would be in lava
                            float checkWaterLevel = Floating.GetWaterLevel(checkPos, ref waterVolume);
                            float checkGroundHeight = checkPos.y;
                            if (ZoneSystem.instance != null)
                            {
                                ZoneSystem.instance.GetGroundHeight(checkPos, out checkGroundHeight);
                            }
                            
                            if (checkWaterLevel > checkGroundHeight + MinLavaDepth)
                            {
                                // This direction leads to lava
                                _currentHazards.LavaNearby = true;
                                if (dist < _currentHazards.DistanceToLava)
                                {
                                    _currentHazards.DistanceToLava = dist;
                                    _currentHazards.NearestLavaDirection = _sampleDirections[i];
                                }
                                
                                // Heavily penalize this direction
                                _directionScores[i] = Mathf.Min(_directionScores[i], LavaDirectionMaxScore);
                                break;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Water level check failed - not a critical error
            }
        }

      private float CheckWaterLevel(Vector3 pos)
     {
       // Use Valheim's water level system with proper signature
  try
 {
      // Try to get water level - Valheim's API varies by version
     // Method 1: Try static GetWaterLevel with position only
  float waterLevel = 0f;
    
       // Check if position is in water using WaterVolume
       WaterVolume waterVolume = null;
  waterLevel = Floating.GetWaterLevel(pos, ref waterVolume);
         
      float groundLevel = pos.y;
      
     if (ZoneSystem.instance != null)
  {
        ZoneSystem.instance.GetGroundHeight(pos, out groundLevel);
  }
    
     return Mathf.Max(0, waterLevel - groundLevel);
 }
   catch
   {
     // Fallback: check if there's water nearby via physics
   Collider[] waterColliders = Physics.OverlapSphere(pos, 1f, LayerMask.GetMask("Water"));
       return waterColliders.Length > 0 ? dangerousWaterDepth : 0f;
  }
     }

      private HazardType CheckHazardAtPosition(Vector3 pos)
    {
            // Water (but in Ashlands, water IS lava)
            if (WorldGenerator.instance != null)
            {
                Heightmap.Biome biome = WorldGenerator.instance.GetBiome(pos);
                if (biome == Heightmap.Biome.AshLands)
                {
                    // Check for lava ocean in Ashlands
                    try
                    {
                        WaterVolume waterVolume = null;
                        float waterLevel = Floating.GetWaterLevel(pos, ref waterVolume);
                        float groundHeight = pos.y;
                        if (ZoneSystem.instance != null)
                        {
                            ZoneSystem.instance.GetGroundHeight(pos, out groundHeight);
                        }
                        if (waterLevel > groundHeight + MinLavaDepth)
                        {
                            return HazardType.Lava; // In Ashlands, water = lava
                        }
                    }
                    catch { }
                }
                else if (CheckWaterLevel(pos) > dangerousWaterDepth)
                {
                    return HazardType.Water;
                }
            }
            else if (CheckWaterLevel(pos) > dangerousWaterDepth)
            {
                return HazardType.Water;
            }
     
     // Check for hazard objects
            Collider[] colliders = Physics.OverlapSphere(pos, 2f);
            foreach (var collider in colliders)
        {
if (collider == null) continue;
      string objName = collider.gameObject.name.ToLower();
      
                // Check lava FIRST - it's the most dangerous
                if (objName.Contains("lava") || objName.Contains("magma"))
                    return HazardType.Lava;
                if (objName.Contains("fire") || objName.Contains("campfire"))
           return HazardType.Fire;
      if (objName.Contains("poison") || objName.Contains("mist"))
        return HazardType.Poison;
       if (objName.Contains("tar"))
              return HazardType.Tar;
     }
      
     // Cliff check
    if (ZoneSystem.instance != null)
   {
           float groundHeight;
    if (ZoneSystem.instance.GetGroundHeight(pos, out groundHeight))
                {
                    if (transform.position.y - groundHeight > cliffThreshold)
           return HazardType.Cliff;
      }
            }
          
    return HazardType.None;
        }

        private float GetDistanceToNearestHazard(Vector3 pos)
        {
     float nearest = float.MaxValue;
          
   if (_currentHazards.WaterNearby)
    nearest = Mathf.Min(nearest, _currentHazards.DistanceToWater);
     if (_currentHazards.CliffNearby)
                nearest = Mathf.Min(nearest, _currentHazards.DistanceToCliff);
 if (_currentHazards.FireNearby)
              nearest = Mathf.Min(nearest, _currentHazards.DistanceToFire);
            if (_currentHazards.LavaNearby)
                nearest = Mathf.Min(nearest, _currentHazards.DistanceToLava);
            if (_currentHazards.AoeHazardNearby)
                nearest = Mathf.Min(nearest, _currentHazards.DistanceToAoe);
            
 return nearest;
  }

        private int GetClosestDirectionIndex(Vector3 direction)
        {
        direction.y = 0;
        direction.Normalize();
   
            int closest = 0;
    float bestDot = float.MinValue;
      
            for (int i = 0; i < directionSamples; i++)
      {
                float dot = Vector3.Dot(direction, _sampleDirections[i]);
         if (dot > bestDot)
         {
  bestDot = dot;
     closest = i;
                }
 }
            
     return closest;
        }
        
        #endregion
        
        #region AOE Hazard Detection
        
        /// <summary>
        /// Scans for active AOE hazards in the area.
        /// Detects fire patches, poison clouds, frost zones, lightning, and other ground effects.
        /// </summary>
        private void CheckForAoeHazards(Vector3 pos)
        {
            _activeAoeHazards.Clear();
            
            float aoeCheckRange = hazardCheckDistance * AoeCheckRangeMultiplier;
            
            // Find all potential AOE objects in range
            Collider[] colliders = Physics.OverlapSphere(pos, aoeCheckRange);
            
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                // Check for Aoe component (Valheim's built-in AOE system)
                var aoe = collider.GetComponent<Aoe>();
                if (aoe != null)
                {
                    ProcessAoeComponent(aoe, collider.transform.position);
                    continue;
                }
                
                // Check by object name for common AOE patterns
                string objName = collider.gameObject.name.ToLower();
                
                // Fire AOEs
                if (IsFireAoeName(objName))
                {
                    AddAoeHazard(collider.transform.position, GetAoeRadius(collider), HazardType.AoeFire, objName);
                    continue;
                }
                
                // Poison AOEs
                if (IsPoisonAoeName(objName))
                {
                    AddAoeHazard(collider.transform.position, GetAoeRadius(collider), HazardType.AoePoison, objName);
                    continue;
                }
                
                // Frost AOEs
                if (IsFrostAoeName(objName))
                {
                    AddAoeHazard(collider.transform.position, GetAoeRadius(collider), HazardType.AoeFrost, objName);
                    continue;
                }
                
                // Lightning AOEs
                if (IsLightningAoeName(objName))
                {
                    AddAoeHazard(collider.transform.position, GetAoeRadius(collider), HazardType.AoeLightning, objName);
                    continue;
                }
                
                // Generic damage AOEs
                if (IsGenericAoeName(objName))
                {
                    AddAoeHazard(collider.transform.position, GetAoeRadius(collider), HazardType.AoeEffect, objName);
                    continue;
                }
                
                // Check for StatusEffect or damage-related components by name
                var components = collider.GetComponents<MonoBehaviour>();
                foreach (var behaviour in components)
                {
                    if (behaviour == null) continue;
                    string compName = behaviour.GetType().Name.ToLower();
                    if (compName.Contains("damage") || compName.Contains("aoe") || 
                        compName.Contains("status") || compName.Contains("effect"))
                    {
                        HazardType hazardType = ClassifyParticleEffect(objName);
                        AddAoeHazard(collider.transform.position, GetAoeRadius(collider), hazardType, objName);
                        break;
                    }
                }
            }
            
            // Update hazard map
            if (_activeAoeHazards.Count > 0)
            {
                _currentHazards.AoeHazardNearby = true;
                _currentHazards.ActiveAoeHazards = new List<ActiveAoeHazard>(_activeAoeHazards);
                
                // Find nearest AOE
                float nearestDist = float.MaxValue;
                foreach (var aoe in _activeAoeHazards)
                {
                    float dist = Vector3.Distance(pos, aoe.Position) - aoe.Radius;
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        _currentHazards.DistanceToAoe = Mathf.Max(0, dist);
                        _currentHazards.NearestAoeDirection = (aoe.Position - pos).normalized;
                    }
                }
            }
        }
        
        /// <summary>
        /// Processes a Valheim Aoe component.
        /// </summary>
        private void ProcessAoeComponent(Aoe aoe, Vector3 position)
        {
            if (aoe == null) return;
            
            try
            {
                // Get AOE radius
                float radius = DefaultAoeRadius; // Default radius
                var radiusField = typeof(Aoe).GetField("m_radius", 
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (radiusField != null)
                {
                    radius = (float)radiusField.GetValue(aoe);
                }
                
                // Determine hazard type from damage
                HazardType hazardType = HazardType.AoeEffect;
                var damageField = typeof(Aoe).GetField("m_damage",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (damageField != null)
                {
                    var damage = damageField.GetValue(aoe) as HitData.DamageTypes?;
                    if (damage.HasValue)
                    {
                        hazardType = ClassifyDamageType(damage.Value);
                    }
                }
                
                // Get TTL if available
                float ttl = 10f;
                var ttlField = typeof(Aoe).GetField("m_ttl",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (ttlField != null)
                {
                    ttl = (float)ttlField.GetValue(aoe);
                }
                
                AddAoeHazard(position, radius, hazardType, aoe.gameObject.name, ttl);
                
                if (VerboseLogging)
                {
                    Debug.Log($"[TerrainAwareness] Detected Aoe: {aoe.gameObject.name}, radius={radius:F1}, type={hazardType}");
                }
            }
            catch (Exception ex)
            {
                if (VerboseLogging)
                {
                    Debug.LogWarning($"[TerrainAwareness] Failed to process Aoe: {ex.Message}");
                }
            }
        }
        
        /// <summary>
        /// Adds an AOE hazard to the tracking list.
        /// </summary>
        private void AddAoeHazard(Vector3 position, float radius, HazardType type, string sourceName, float ttl = 10f)
        {
            // Don't add if already tracking this position
            foreach (var existing in _activeAoeHazards)
            {
                if (Vector3.Distance(existing.Position, position) < 1f)
                {
                    return; // Already tracking
                }
            }
            
            _activeAoeHazards.Add(new ActiveAoeHazard
            {
                Position = position,
                Radius = Mathf.Max(radius, 1f), // Minimum 1m radius
                Type = type,
                TimeRemaining = ttl,
                SourceName = sourceName
            });
        }
        
        /// <summary>
        /// Gets the radius of an AOE effect from its collider.
        /// </summary>
        private float GetAoeRadius(Collider collider)
        {
            if (collider == null) return DefaultAoeRadius;
            
            // Try sphere collider
            var sphere = collider as SphereCollider;
            if (sphere != null)
            {
                return sphere.radius * collider.transform.lossyScale.x;
            }
            
            // Try capsule collider
            var capsule = collider as CapsuleCollider;
            if (capsule != null)
            {
                return capsule.radius * collider.transform.lossyScale.x;
            }
            
            // Try box collider - use average of x and z
            var box = collider as BoxCollider;
            if (box != null)
            {
                Vector3 size = Vector3.Scale(box.size, collider.transform.lossyScale);
                return (size.x + size.z) / 4f;
            }
            
            // Default radius
            return DefaultAoeRadius;
        }
        
        /// <summary>
        /// Gets the penalty for a direction based on AOE hazards.
        /// </summary>
        private float GetAoePenaltyForDirection(Vector3 fromPos, Vector3 direction)
        {
            float maxPenalty = 0f;
            
            foreach (var aoe in _activeAoeHazards)
            {
                // Check if moving in this direction would enter the AOE
                for (float dist = 1f; dist <= hazardCheckDistance; dist += 1f)
                {
                    Vector3 checkPos = fromPos + direction * dist;
                    float distToAoe = Vector3.Distance(checkPos, aoe.Position);
                    
                    if (distToAoe < aoe.Radius)
                    {
                        // Would enter AOE - penalty based on how close and AOE type
                        float penalty = GetAoeTypePenalty(aoe.Type);
                        float proximityFactor = 1f - (dist / hazardCheckDistance);
                        maxPenalty = Mathf.Max(maxPenalty, penalty * proximityFactor);
                        break;
                    }
                }
            }
            
            return maxPenalty;
        }
        
        /// <summary>
        /// Gets the penalty for a specific AOE type.
        /// </summary>
        private float GetAoeTypePenalty(HazardType type)
        {
            switch (type)
            {
                case HazardType.AoeFire: return 0.7f;
                case HazardType.AoePoison: return 0.6f;
                case HazardType.AoeFrost: return 0.5f;
                case HazardType.AoeLightning: return 0.8f;
                case HazardType.AoeEffect: return 0.6f;
                default: return 0.5f;
            }
        }
        
        /// <summary>
        /// Classifies damage type to hazard type.
        /// </summary>
        private HazardType ClassifyDamageType(HitData.DamageTypes damage)
        {
            if (damage.m_fire > 0) return HazardType.AoeFire;
            if (damage.m_poison > 0) return HazardType.AoePoison;
            if (damage.m_frost > 0) return HazardType.AoeFrost;
            if (damage.m_lightning > 0) return HazardType.AoeLightning;
            return HazardType.AoeEffect;
        }
        
        /// <summary>
        /// Classifies particle effect by name.
        /// </summary>
        private HazardType ClassifyParticleEffect(string objName)
        {
            if (objName.Contains("fire") || objName.Contains("flame") || objName.Contains("burn"))
                return HazardType.AoeFire;
            if (objName.Contains("poison") || objName.Contains("toxic") || objName.Contains("gas"))
                return HazardType.AoePoison;
            if (objName.Contains("frost") || objName.Contains("ice") || objName.Contains("freeze") || objName.Contains("cold"))
                return HazardType.AoeFrost;
            if (objName.Contains("lightning") || objName.Contains("electric") || objName.Contains("shock") || objName.Contains("spark"))
                return HazardType.AoeLightning;
            return HazardType.AoeEffect;
        }
        
        // Name pattern checks
        private bool IsFireAoeName(string name)
        {
            return name.Contains("fire_aoe") || name.Contains("flame_aoe") || name.Contains("firepool") ||
                   name.Contains("fire_pool") || name.Contains("burning_ground") || name.Contains("lava_burst") ||
                   name.Contains("meteor") || name.Contains("firebomb") || name.Contains("molten");
        }
        
        private bool IsPoisonAoeName(string name)
        {
            return name.Contains("poison_aoe") || name.Contains("poison_pool") || name.Contains("toxic") ||
                   name.Contains("gas_cloud") || name.Contains("miasma") || name.Contains("blight") ||
                   name.Contains("ooze") || name.Contains("blob_attack");
        }
        
        private bool IsFrostAoeName(string name)
        {
            return name.Contains("frost_aoe") || name.Contains("ice_aoe") || name.Contains("freeze_zone") ||
                   name.Contains("cold_aoe") || name.Contains("blizzard") || name.Contains("ice_shard");
        }
        
        private bool IsLightningAoeName(string name)
        {
            return name.Contains("lightning_aoe") || name.Contains("electric") || name.Contains("storm_aoe") ||
                   name.Contains("thunder") || name.Contains("spark_aoe") || name.Contains("shock_zone");
        }
        
        private bool IsGenericAoeName(string name)
        {
            return name.Contains("aoe") || name.Contains("area_effect") || name.Contains("ground_effect") ||
                   name.Contains("damage_zone") || name.Contains("hazard_zone") || name.Contains("attack_area");
        }
        
        #endregion
    }
}
