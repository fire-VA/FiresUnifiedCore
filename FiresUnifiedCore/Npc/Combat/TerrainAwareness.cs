using UnityEngine;
using System.Collections.Generic;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Keeps combat positioning out of hazards (water, fire, cliffs and drops, Plains tar, Ashlands lava and the scalding
    /// Ashlands sea, ground AOEs) by reporting safe directions and adjusting retreat movement.
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
        private const float LavaDangerRange = 5f;
        private const float MinLavaDepth = 0.3f;
        private const float LavaDirectionMaxScore = 0.1f;
        private const float AoeCheckRangeMultiplier = 1.5f;
        private const float DefaultAoeRadius = 3f;
        private const float InWaterDepth = 0.5f;
        private const float SampleSpacing = 2f;
        private const float HazardObjectRadius = 2f;
        private const float TarProbeLift = 0.1f;
        private const int GroundProbeMargin = 2;
        private const int MaxCachedColliders = 20000;

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

        // Ground height (NaN when no ground was found) and water depth per (direction, distance) sample, filled once per
        // scan and read by every check that looks at those points.
        private float[] _sampleGround;
        private float[] _sampleWater;
        private bool[] _lavaDirections;
        private int _samplesPerDirection;
        private bool _inAshlands;
        private float _ownWaterDepth;

        // Cached environmental state
        private bool _isInWater;
        private bool _isNearCliff;
        private bool _isNearFire;
        private bool _isNearLava;
        private bool _isNearAoe;

        // AOE hazard tracking
        private readonly List<ActiveAoeHazard> _activeAoeHazards = new List<ActiveAoeHazard>();
        private readonly List<ActiveAoeHazard> _hazardMapAoe = new List<ActiveAoeHazard>();
        private float _lastAoeScanTime;
        private const float AoeScanInterval = 0.15f; // Scan AOEs more frequently

        public static bool VerboseLogging = false;

        // Every companion scans on the main thread, so one overlap buffer serves them all. It grows when a query fills it,
        // so no collider in range is ever dropped.
        private static Collider[] s_colliders = new Collider[256];
        private static readonly Collider[] s_waterHit = new Collider[1];
        private static readonly List<MonoBehaviour> s_behaviours = new List<MonoBehaviour>();

        // What a collider's name and components say about it never changes, so it is worked out once per collider
        // instead of lower-casing names and listing components on every scan.
        private static readonly Dictionary<int, ColliderInfo> s_colliderInfo = new Dictionary<int, ColliderInfo>();

        // Resolved on first use from a scan. Unity rejects NameToLayer while it constructs a MonoBehaviour, so a static
        // initializer here (it runs inside AddComponent<TerrainAwareness>) killed the type for the whole session on the
        // development player. None of these masks is empty, so 0 means not resolved yet.
        private static int s_fireMask, s_coverMask, s_obstacleMask, s_waterMask;
        private static int FireMask => s_fireMask != 0 ? s_fireMask : (s_fireMask = LayerMask.GetMask("piece", "Default"));
        private static int CoverMask => s_coverMask != 0 ? s_coverMask : (s_coverMask = LayerMask.GetMask("piece", "static_solid"));
        private static int ObstacleMask => s_obstacleMask != 0 ? s_obstacleMask : (s_obstacleMask = LayerMask.GetMask("piece", "static_solid", "terrain"));
        private static int WaterMask => s_waterMask != 0 ? s_waterMask : (s_waterMask = LayerMask.GetMask("Water"));

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

        private struct ColliderInfo
        {
            public HazardType Named;
            public bool FireSource;
            public Fireplace Fireplace;
            public Aoe Aoe;
            public HazardType AoeKind;
            public string AoeName;
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
            if (GroundHeightAt(position, out float groundHeight) && groundHeight > myHeight + 1f)
            {
                score.HasHighGround = true;
                score.Score += highGroundBonus;
            }

            // Check for cover (obstacles between us and enemy)
            if (enemy != null)
            {
                Vector3 toEnemy = enemy.transform.position - position;
                if (Physics.Raycast(position + Vector3.up, toEnemy.normalized, toEnemy.magnitude * 0.5f, CoverMask))
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
                    if (GroundHeightAt(samplePos, out float groundHeight))
                    {
                        samplePos.y = groundHeight;
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
            _lavaDirections = new bool[directionSamples];

            for (int i = 0; i < directionSamples; i++)
            {
                float angle = (360f / directionSamples) * i * Mathf.Deg2Rad;
                _sampleDirections[i] = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                _directionScores[i] = 1f;
            }
        }

        private void ScanEnvironment()
        {
            _hazardMapAoe.Clear();
            _currentHazards = new HazardMap
            {
                DistanceToWater = float.MaxValue,
                DistanceToCliff = float.MaxValue,
                DistanceToFire = float.MaxValue,
                DistanceToLava = float.MaxValue,
                DistanceToAoe = float.MaxValue,
                ActiveAoeHazards = _hazardMapAoe,
            };

            Vector3 myPos = transform.position;
            _inAshlands = WorldGenerator.instance != null && WorldGenerator.instance.GetBiome(myPos) == Heightmap.Biome.AshLands;

            // Check water at current position
            _ownWaterDepth = WaterDepthAt(myPos);
            _isInWater = _ownWaterDepth > InWaterDepth;

            // Check for AOE hazards (more frequently than other checks)
            if (Time.time - _lastAoeScanTime >= AoeScanInterval)
            {
                _lastAoeScanTime = Time.time;
                CheckForAoeHazards(myPos);
            }

            SampleSurroundings(myPos);

            // Lava before the direction scores, so the safest direction never points into it.
            CheckForLava(myPos);

            float bestScore = 0f;
            int blockedCount = 0;

            for (int i = 0; i < directionSamples; i++)
            {
                _directionScores[i] = ScoreDirection(i, myPos);

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
            CheckForWater();
            CheckForCliffs(myPos);
            CheckForFire(myPos);

            _isNearCliff = _currentHazards.CliffNearby;
            _isNearFire = _currentHazards.FireNearby;
            _isNearLava = _currentHazards.LavaNearby;
            _isNearAoe = _currentHazards.AoeHazardNearby;
        }

        // Ground and water at every (direction, distance) sample, once per scan.
        private void SampleSurroundings(Vector3 myPos)
        {
            int perDirection = Mathf.Max(0, Mathf.FloorToInt(hazardCheckDistance / SampleSpacing + 0.001f));
            int count = directionSamples * perDirection;
            if (_sampleGround == null || _sampleGround.Length != count)
            {
                _sampleGround = new float[count];
                _sampleWater = new float[count];
            }
            if (_lavaDirections == null || _lavaDirections.Length != directionSamples)
                _lavaDirections = new bool[directionSamples];
            _samplesPerDirection = perDirection;

            for (int i = 0; i < directionSamples; i++)
            {
                for (int k = 0; k < perDirection; k++)
                {
                    Vector3 checkPos = myPos + _sampleDirections[i] * SampleDistance(k);
                    int s = i * perDirection + k;
                    bool found = GroundHeightAt(checkPos, out float groundHeight);
                    _sampleGround[s] = found ? groundHeight : float.NaN;
                    _sampleWater[s] = WaterDepthAt(checkPos, found ? groundHeight : checkPos.y);
                }
            }
        }

        private static float SampleDistance(int k) => SampleSpacing * (k + 1);

        private float ScoreDirection(int i, Vector3 fromPos)
        {
            Vector3 direction = _sampleDirections[i];
            float score = 1f;

            // Obstacle check (can't move there): the nearest hit blocks every sample at or beyond it.
            float obstacleDistance = Physics.Raycast(fromPos + Vector3.up, direction, out RaycastHit hit, hazardCheckDistance, ObstacleMask)
                ? hit.distance
                : float.MaxValue;

            for (int k = 0; k < _samplesPerDirection; k++)
            {
                float dist = SampleDistance(k);
                float falloff = Mathf.InverseLerp(hazardCheckDistance, SampleSpacing, dist);
                int s = i * _samplesPerDirection + k;

                // Ground height check (cliff detection)
                float groundHeight = _sampleGround[s];
                if (!float.IsNaN(groundHeight) && fromPos.y - groundHeight > cliffThreshold)
                {
                    score -= falloff * 0.5f;
                }

                // Water check
                if (_sampleWater[s] > dangerousWaterDepth)
                {
                    score -= falloff * WaterDirectionPenalty;
                }

                if (obstacleDistance <= dist)
                {
                    score -= falloff * ObstacleDirectionPenalty;
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

            score = Mathf.Clamp01(score);
            return _lavaDirections[i] ? Mathf.Min(score, LavaDirectionMaxScore) : score;
        }

        private void CheckForWater()
        {
            // Check in a circle for water
            for (int i = 0; i < directionSamples; i++)
            {
                for (int k = 0; k < _samplesPerDirection; k++)
                {
                    if (_sampleWater[i * _samplesPerDirection + k] > dangerousWaterDepth)
                    {
                        float dist = SampleDistance(k);
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
            for (int i = 0; i < directionSamples; i++)
            {
                for (int k = 0; k < _samplesPerDirection; k++)
                {
                    float groundHeight = _sampleGround[i * _samplesPerDirection + k];
                    if (!float.IsNaN(groundHeight) && pos.y - groundHeight > cliffThreshold)
                    {
                        float dist = SampleDistance(k);
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

        private void CheckForFire(Vector3 pos)
        {
            // Check for fire sources
            int count = Overlap(pos, hazardCheckDistance, FireMask);
            for (int c = 0; c < count; c++)
            {
                Collider collider = s_colliders[c];
                if (collider == null) continue;

                ColliderInfo info = Describe(collider);
                bool isFire = info.FireSource || (info.Fireplace != null && info.Fireplace.IsBurning());
                if (!isFire) continue;

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

        /// <summary>
        /// Ashlands lava and the scalding Ashlands sea. Lava is INSTANT DEATH - companions must avoid it at all costs.
        /// Lava is the terrain's lava paint (vanilla's own test, the one CompanionAI's teleport check uses too); many
        /// Ashlands rocks, lanterns and pieces have "lava" in their names and are harmless.
        /// </summary>
        private void CheckForLava(Vector3 pos)
        {
            for (int i = 0; i < directionSamples; i++)
                _lavaDirections[i] = false;
            if (!_inAshlands || ZoneSystem.instance == null) return;

            if (ZoneSystem.instance.IsLava(pos))
            {
                _currentHazards.LavaNearby = true;
                _currentHazards.DistanceToLava = 0f;
            }

            // In the Ashlands sea, the water itself burns (vanilla heats characters in Ashlands water).
            bool inScaldingSea = _ownWaterDepth > InWaterDepth;

            for (int i = 0; i < directionSamples; i++)
            {
                for (int k = 0; k < _samplesPerDirection; k++)
                {
                    float dist = SampleDistance(k);
                    bool sea = inScaldingSea && _sampleWater[i * _samplesPerDirection + k] > MinLavaDepth;
                    bool lava = !sea && ZoneSystem.instance.IsLava(pos + _sampleDirections[i] * dist);
                    if (!sea && !lava) continue;

                    _lavaDirections[i] = true;
                    if (sea || dist < LavaDangerRange)
                    {
                        _currentHazards.LavaNearby = true;
                        if (dist < _currentHazards.DistanceToLava)
                        {
                            _currentHazards.DistanceToLava = dist;
                            _currentHazards.NearestLavaDirection = _sampleDirections[i];
                        }
                    }

                    if (VerboseLogging)
                    {
                        Debug.Log($"[TerrainAwareness] {(sea ? "Scalding sea" : "Lava")} {dist:F0}m toward {_sampleDirections[i]}");
                    }
                    break;
                }
            }
        }

        // The solid surface under a point, found from just above it, so voxel terrain, floors, docks and ground under an
        // overhang read right. ZoneSystem.GetGroundHeight casts from the sky at the terrain layer only: it takes the top
        // of a skyland or a cave roof for the ground and cannot see pieces. It stays as the fallback, for points whose
        // surface is higher than the probe's start.
        private static bool GroundHeightAt(Vector3 point, out float height)
        {
            ZoneSystem zoneSystem = ZoneSystem.instance;
            if (zoneSystem == null)
            {
                height = point.y;
                return false;
            }
            return zoneSystem.GetSolidHeight(point, out height, GroundProbeMargin) || zoneSystem.GetGroundHeight(point, out height);
        }

        private float WaterDepthAt(Vector3 pos)
        {
            return WaterDepthAt(pos, GroundHeightAt(pos, out float groundHeight) ? groundHeight : pos.y);
        }

        private float WaterDepthAt(Vector3 pos, float groundHeight)
        {
            try
            {
                WaterVolume waterVolume = null;
                return Mathf.Max(0f, Floating.GetWaterLevel(pos, ref waterVolume) - groundHeight);
            }
            catch
            {
                // Fallback: check if there's water nearby via physics
                return Physics.OverlapSphereNonAlloc(pos, 1f, s_waterHit, WaterMask) > 0 ? dangerousWaterDepth : 0f;
            }
        }

        private static bool TarAt(Vector3 pos, float groundHeight)
        {
            var probe = new Vector3(pos.x, groundHeight + TarProbeLift, pos.z);
            return Floating.GetLiquidLevel(probe, 1f, LiquidType.Tar) > groundHeight;
        }

        private HazardType CheckHazardAtPosition(Vector3 pos)
        {
            bool grounded = GroundHeightAt(pos, out float groundHeight);
            if (!grounded) groundHeight = pos.y;

            // Water (but in Ashlands, water IS lava)
            if (WorldGenerator.instance != null && WorldGenerator.instance.GetBiome(pos) == Heightmap.Biome.AshLands)
            {
                if (ZoneSystem.instance != null && ZoneSystem.instance.IsLava(pos))
                    return HazardType.Lava;
                if (WaterDepthAt(pos, groundHeight) > MinLavaDepth)
                    return HazardType.Lava;
            }
            else if (WaterDepthAt(pos, groundHeight) > dangerousWaterDepth)
            {
                return HazardType.Water;
            }

            // Check for hazard objects
            int count = Overlap(pos, HazardObjectRadius, Physics.AllLayers);
            for (int c = 0; c < count; c++)
            {
                Collider collider = s_colliders[c];
                if (collider == null) continue;
                HazardType named = Describe(collider).Named;
                if (named != HazardType.None)
                    return named;
            }

            if (grounded && TarAt(pos, groundHeight))
                return HazardType.Tar;

            // Cliff check
            if (grounded && transform.position.y - groundHeight > cliffThreshold)
                return HazardType.Cliff;

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

        private static int Overlap(Vector3 pos, float radius, int layerMask)
        {
            int count;
            while ((count = Physics.OverlapSphereNonAlloc(pos, radius, s_colliders, layerMask)) == s_colliders.Length)
                s_colliders = new Collider[s_colliders.Length * 2];
            return count;
        }

        private static ColliderInfo Describe(Collider collider)
        {
            int id = collider.GetInstanceID();
            if (s_colliderInfo.TryGetValue(id, out ColliderInfo info))
                return info;
            if (s_colliderInfo.Count >= MaxCachedColliders)
                s_colliderInfo.Clear();

            string name = collider.gameObject.name.ToLowerInvariant();
            info.FireSource = name.Contains("fire") || name.Contains("torch");
            if (name.Contains("fire"))
                info.Named = HazardType.Fire;
            else if (name.Contains("poison"))
                info.Named = HazardType.Poison;
            info.Fireplace = collider.GetComponent<Fireplace>();
            info.Aoe = collider.GetComponent<Aoe>();
            if (info.Aoe != null)
            {
                info.AoeName = collider.gameObject.name;
            }
            else
            {
                info.AoeKind = ClassifyAoeName(name);
                if (info.AoeKind == HazardType.None && HasEffectComponent(collider))
                    info.AoeKind = ClassifyParticleEffect(name);
                if (info.AoeKind != HazardType.None)
                    info.AoeName = name;
            }

            s_colliderInfo[id] = info;
            return info;
        }

        private static bool HasEffectComponent(Collider collider)
        {
            collider.GetComponents(s_behaviours);
            bool found = false;
            foreach (var behaviour in s_behaviours)
            {
                if (behaviour == null) continue;
                string compName = behaviour.GetType().Name.ToLowerInvariant();
                if (compName.Contains("damage") || compName.Contains("aoe") ||
                    compName.Contains("status") || compName.Contains("effect"))
                {
                    found = true;
                    break;
                }
            }
            s_behaviours.Clear();
            return found;
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
            int count = Overlap(pos, aoeCheckRange, Physics.AllLayers);
            for (int c = 0; c < count; c++)
            {
                Collider collider = s_colliders[c];
                if (collider == null) continue;

                ColliderInfo info = Describe(collider);

                // Valheim's built-in AOE system
                if (info.Aoe != null)
                {
                    ProcessAoeComponent(info.Aoe, info.AoeName, collider.transform.position);
                    continue;
                }

                if (info.AoeKind != HazardType.None)
                    AddAoeHazard(collider.transform.position, GetAoeRadius(collider), info.AoeKind, info.AoeName);
            }

            // Update hazard map
            if (_activeAoeHazards.Count > 0)
            {
                _currentHazards.AoeHazardNearby = true;
                _hazardMapAoe.AddRange(_activeAoeHazards);

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
        private void ProcessAoeComponent(Aoe aoe, string sourceName, Vector3 position)
        {
            HazardType hazardType = ClassifyDamageType(aoe.m_damage);
            AddAoeHazard(position, aoe.m_radius, hazardType, sourceName, aoe.m_ttl);

            if (VerboseLogging)
            {
                Debug.Log($"[TerrainAwareness] Detected Aoe: {sourceName}, radius={aoe.m_radius:F1}, type={hazardType}");
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
        private static HazardType ClassifyDamageType(HitData.DamageTypes damage)
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
        private static HazardType ClassifyParticleEffect(string objName)
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

        private static HazardType ClassifyAoeName(string name)
        {
            if (IsFireAoeName(name)) return HazardType.AoeFire;
            if (IsPoisonAoeName(name)) return HazardType.AoePoison;
            if (IsFrostAoeName(name)) return HazardType.AoeFrost;
            if (IsLightningAoeName(name)) return HazardType.AoeLightning;
            if (IsGenericAoeName(name)) return HazardType.AoeEffect;
            return HazardType.None;
        }

        // Name pattern checks
        private static bool IsFireAoeName(string name)
        {
            return name.Contains("fire_aoe") || name.Contains("flame_aoe") || name.Contains("firepool") ||
                   name.Contains("fire_pool") || name.Contains("burning_ground") || name.Contains("lava_burst") ||
                   name.Contains("meteor") || name.Contains("firebomb") || name.Contains("molten");
        }

        private static bool IsPoisonAoeName(string name)
        {
            return name.Contains("poison_aoe") || name.Contains("poison_pool") || name.Contains("toxic") ||
                   name.Contains("gas_cloud") || name.Contains("miasma") || name.Contains("blight") ||
                   name.Contains("ooze") || name.Contains("blob_attack");
        }

        private static bool IsFrostAoeName(string name)
        {
            return name.Contains("frost_aoe") || name.Contains("ice_aoe") || name.Contains("freeze_zone") ||
                   name.Contains("cold_aoe") || name.Contains("blizzard") || name.Contains("ice_shard");
        }

        private static bool IsLightningAoeName(string name)
        {
            return name.Contains("lightning_aoe") || name.Contains("electric") || name.Contains("storm_aoe") ||
                   name.Contains("thunder") || name.Contains("spark_aoe") || name.Contains("shock_zone");
        }

        private static bool IsGenericAoeName(string name)
        {
            return name.Contains("aoe") || name.Contains("area_effect") || name.Contains("ground_effect") ||
                   name.Contains("damage_zone") || name.Contains("hazard_zone") || name.Contains("attack_area");
        }

        #endregion
    }
}
