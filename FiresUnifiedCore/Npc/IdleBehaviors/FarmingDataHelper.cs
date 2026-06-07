using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Helper class for farming-related data and detection.
    /// Provides seed-to-plant mappings, crop detection, and beehive utilities.
    /// </summary>
    public static class FarmingDataHelper
    {
        #region Seed to Plant Mappings
        
        /// <summary>
        /// Maps seed item prefab names to their corresponding plant prefab names.
        /// </summary>
        private static readonly Dictionary<string, string> SeedToPlantMap = new Dictionary<string, string>
        {
            // Vegetables
            { "CarrotSeeds", "sapling_carrot" },
            { "TurnipSeeds", "sapling_turnip" },
            { "OnionSeeds", "sapling_onion" },
            
            // Grains (require Plains biome)
            { "BarleySeeds", "sapling_barley" },
            { "FlaxSeeds", "sapling_flax" },
            
            // Jotun Puffs (Mistlands)
            { "JotunPuffs", "Pickable_JotunPuffs" },
            
            // Magecap (Mistlands)
            { "Magecap", "Pickable_Magecap" },
            
            // Smoky Puffs (Ashlands)
            { "SmokerPuff", "sapling_smokepuff" },
        };
        
        /// <summary>
        /// Maps plant prefab names to the seeds they drop when harvested.
        /// Used to determine what seeds to replant.
        /// </summary>
        private static readonly Dictionary<string, string> PlantToSeedMap = new Dictionary<string, string>
        {
            // Seed pickables drop seeds
            { "Pickable_SeedCarrot", "CarrotSeeds" },
            { "Pickable_SeedTurnip", "TurnipSeeds" },
            { "Pickable_SeedOnion", "OnionSeeds" },
            
            // Regular crops drop their respective items
            { "Pickable_Carrot", "Carrot" },
            { "Pickable_Turnip", "Turnip" },
            { "Pickable_Onion", "Onion" },
            { "Pickable_Barley", "Barley" },
            { "Pickable_Flax", "Flax" },
        };
        
        /// <summary>
        /// Crop items that can be used to grow seed variants.
        /// E.g., planting a Carrot grows Pickable_SeedCarrot which drops CarrotSeeds.
        /// </summary>
        private static readonly Dictionary<string, string> CropToSeedPlantMap = new Dictionary<string, string>
        {
            { "Carrot", "sapling_seedcarrot" },
            { "Turnip", "sapling_seedturnip" },
            { "Onion", "sapling_seedonion" },
        };
        
        #endregion
        
        #region Biome Requirements
        
        /// <summary>
        /// Seeds that require Plains biome to grow.
        /// </summary>
        private static readonly HashSet<string> PlainsOnlySeeds = new HashSet<string>
        {
            "BarleySeeds",
            "FlaxSeeds"
        };
        
        /// <summary>
        /// Seeds that require Mistlands biome to grow.
        /// </summary>
        private static readonly HashSet<string> MistlandsOnlySeeds = new HashSet<string>
        {
            "JotunPuffs",
            "Magecap"
        };
        
        /// <summary>
        /// Seeds that require Ashlands biome to grow.
        /// </summary>
        private static readonly HashSet<string> AshlandsOnlySeeds = new HashSet<string>
        {
            "SmokerPuff"
        };
        
        #endregion
        
        #region Crop Data
        
        /// <summary>
        /// All pickable prefab names that are harvestable crops.
        /// </summary>
        public static readonly HashSet<string> HarvestableCropPrefabs = new HashSet<string>
        {
            "Pickable_Carrot",
            "Pickable_Turnip",
            "Pickable_Onion",
            "Pickable_Barley",
            "Pickable_Flax",
            "Pickable_SeedCarrot",
            "Pickable_SeedTurnip",
            "Pickable_SeedOnion",
            "Pickable_JotunPuffs",
            "Pickable_Magecap",
            "Pickable_SmokePuff",
        };
        
        /// <summary>
        /// All seed item prefab names that can be planted.
        /// </summary>
        public static readonly HashSet<string> PlantableSeedPrefabs = new HashSet<string>
        {
            "CarrotSeeds",
            "TurnipSeeds",
            "OnionSeeds",
            "BarleySeeds",
            "FlaxSeeds",
            "JotunPuffs",
            "Magecap",
            "SmokerPuff",
            // Crops that grow into seed plants
            "Carrot",
            "Turnip",
            "Onion",
        };
        
        #endregion
        
        #region Public API - Seeds
        
        /// <summary>
        /// Gets the plant prefab name for a given seed item.
        /// </summary>
        public static string GetPlantPrefabForSeed(string seedPrefabName)
        {
            if (string.IsNullOrEmpty(seedPrefabName)) return null;
            
            if (SeedToPlantMap.TryGetValue(seedPrefabName, out string plantPrefab))
                return plantPrefab;
            
            // Check if it's a crop that grows seed plants
            if (CropToSeedPlantMap.TryGetValue(seedPrefabName, out plantPrefab))
                return plantPrefab;
            
            return null;
        }
        
        /// <summary>
        /// Checks if an item can be planted.
        /// </summary>
        public static bool CanBePlanted(ItemDrop.ItemData item)
        {
            if (item?.m_dropPrefab == null) return false;
            return PlantableSeedPrefabs.Contains(item.m_dropPrefab.name);
        }
        
        /// <summary>
        /// Checks if a seed can grow in the given biome.
        /// </summary>
        public static bool CanGrowInBiome(string seedPrefabName, Heightmap.Biome biome)
        {
            if (string.IsNullOrEmpty(seedPrefabName)) return false;
            
            // Plains-only crops
            if (PlainsOnlySeeds.Contains(seedPrefabName))
            {
                return biome == Heightmap.Biome.Plains;
            }
            
            // Mistlands-only crops
            if (MistlandsOnlySeeds.Contains(seedPrefabName))
            {
                return biome == Heightmap.Biome.Mistlands;
            }
            
            // Ashlands-only crops
            if (AshlandsOnlySeeds.Contains(seedPrefabName))
            {
                return biome == Heightmap.Biome.AshLands;
            }
            
            // Regular crops can grow in Meadows, Black Forest, or Swamp
            return biome == Heightmap.Biome.Meadows || 
                   biome == Heightmap.Biome.BlackForest || 
                   biome == Heightmap.Biome.Swamp ||
                   biome == Heightmap.Biome.Plains;
        }
        
        /// <summary>
        /// Gets the minimum tool tier required to plant a seed (always 0 for basic cultivator).
        /// </summary>
        public static int GetRequiredCultivatorTier(string seedPrefabName)
        {
            // Ashlands crops might need higher tier cultivator
            if (AshlandsOnlySeeds.Contains(seedPrefabName))
                return 2; // Assume black metal cultivator or similar
            
            return 0; // Basic cultivator works for most
        }
        
        #endregion
        
        #region Public API - Pickables/Crops
        
        /// <summary>
        /// Checks if a Pickable component represents a harvestable crop.
        /// </summary>
        public static bool IsHarvestableCrop(Pickable pickable)
        {
            if (pickable == null) return false;
            
            string prefabName = GetPrefabName(pickable.gameObject);
            return HarvestableCropPrefabs.Contains(prefabName);
        }
        
        /// <summary>
        /// Checks if a Pickable is ready to be harvested (not already picked).
        /// </summary>
        public static bool IsPickableReady(Pickable pickable)
        {
            if (pickable == null) return false;
            
            var nview = pickable.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;
            
            // Check if already picked
            return !nview.GetZDO().GetBool(ZDOVars.s_picked, false);
        }
        
        /// <summary>
        /// Gets the seed that corresponds to this crop (for replanting).
        /// Returns null if no seed mapping exists.
        /// </summary>
        public static string GetSeedForCrop(Pickable pickable)
        {
            if (pickable == null) return null;
            
            string prefabName = GetPrefabName(pickable.gameObject);
            
            if (PlantToSeedMap.TryGetValue(prefabName, out string seedName))
                return seedName;
            
            return null;
        }
        
        #endregion
        
        #region Public API - Beehives
        
        /// <summary>
        /// Checks if a beehive has honey ready for harvest.
        /// </summary>
        public static bool HasHoneyReady(Beehive beehive)
        {
            if (beehive == null) return false;
            return GetHoneyLevel(beehive) > 0;
        }
        
        /// <summary>
        /// Gets the honey level of a beehive (0-4).
        /// Uses ZDO since GetHoneyLevel() is private in Valheim.
        /// </summary>
        public static int GetHoneyLevel(Beehive beehive)
        {
            if (beehive == null) return 0;
            
            var nview = beehive.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return 0;
            
            return nview.GetZDO().GetInt(ZDOVars.s_level, 0);
        }
        
        /// <summary>
        /// Checks if a beehive is in a valid biome and location.
        /// </summary>
        public static bool IsBeehiveHealthy(Beehive beehive)
        {
            if (beehive == null) return false;
            
            // Check biome
            Heightmap.Biome biome = Heightmap.FindBiome(beehive.transform.position);
            if ((biome & beehive.m_biome) == 0)
                return false;
            
            // Beehives check for cover in their own update
            // We just verify the component is valid
            return true;
        }
        
        #endregion
        
        #region Public API - Detection
        
        /// <summary>
        /// Finds all harvestable beehives within radius.
        /// </summary>
        public static List<Beehive> FindNearbyHarvestableBeehives(Vector3 position, float radius)
        {
            var result = new List<Beehive>();
            
            var colliders = Physics.OverlapSphere(position, radius);
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                var beehive = col.GetComponent<Beehive>() ?? col.GetComponentInParent<Beehive>();
                if (beehive != null && HasHoneyReady(beehive))
                {
                    if (!result.Contains(beehive))
                        result.Add(beehive);
                }
            }
            
            return result;
        }
        
        /// <summary>
        /// Finds all harvestable crops (Pickables) within radius.
        /// </summary>
        public static List<Pickable> FindNearbyHarvestableCrops(Vector3 position, float radius)
        {
            var result = new List<Pickable>();
            
            var colliders = Physics.OverlapSphere(position, radius);
            foreach (var col in colliders)
            {
                if (col == null) continue;
                
                var pickable = col.GetComponent<Pickable>() ?? col.GetComponentInParent<Pickable>();
                if (pickable != null && IsHarvestableCrop(pickable) && IsPickableReady(pickable))
                {
                    if (!result.Contains(pickable))
                        result.Add(pickable);
                }
            }
            
            return result;
        }
        
        /// <summary>
        /// Finds cultivated ground positions suitable for planting within radius.
        /// Returns positions that are cultivated and have enough space for a plant.
        /// </summary>
        public static List<Vector3> FindPlantablePositions(Vector3 center, float radius, float plantSpacing = 1f)
        {
            var result = new List<Vector3>();
            
            // Sample positions in a grid pattern
            int samples = Mathf.CeilToInt(radius / plantSpacing);
            
            for (int x = -samples; x <= samples; x++)
            {
                for (int z = -samples; z <= samples; z++)
                {
                    Vector3 testPos = center + new Vector3(x * plantSpacing, 0, z * plantSpacing);
                    
                    // Check distance
                    if (Vector3.Distance(center, testPos) > radius)
                        continue;
                    
                    // Get ground height
                    if (ZoneSystem.instance != null)
                    {
                        float height;
                        if (ZoneSystem.instance.GetGroundHeight(testPos, out height))
                        {
                            testPos.y = height;
                        }
                    }
                    
                    // Check if cultivated
                    var heightmap = Heightmap.FindHeightmap(testPos);
                    if (heightmap == null || !heightmap.IsCultivated(testPos))
                        continue;
                    
                    // Check for existing plants (spacing)
                    if (HasExistingPlantNearby(testPos, plantSpacing * 0.8f))
                        continue;
                    
                    result.Add(testPos);
                }
            }
            
            return result;
        }
        
        /// <summary>
        /// Checks if there's already a plant too close to this position.
        /// </summary>
        public static bool HasExistingPlantNearby(Vector3 position, float spacing)
        {
            int spaceMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid");
            var overlaps = Physics.OverlapSphere(position, spacing, spaceMask);
            
            foreach (var col in overlaps)
            {
                if (col == null) continue;
                
                // Check for Plant component
                var plant = col.GetComponent<Plant>() ?? col.GetComponentInParent<Plant>();
                if (plant != null)
                    return true;
                
                // Check for Pickable (mature crops)
                var pickable = col.GetComponent<Pickable>() ?? col.GetComponentInParent<Pickable>();
                if (pickable != null && IsHarvestableCrop(pickable))
                    return true;
            }
            
            return false;
        }
        
        #endregion
        
        #region Public API - Planting
        
        /// <summary>
        /// Attempts to place a plant at the specified position.
        /// Returns the spawned GameObject if successful.
        /// </summary>
        public static GameObject TryPlantSeed(string seedPrefabName, Vector3 position)
        {
            string plantPrefab = GetPlantPrefabForSeed(seedPrefabName);
            if (string.IsNullOrEmpty(plantPrefab))
            {
                Debug.LogWarning($"[FarmingDataHelper] No plant prefab found for seed: {seedPrefabName}");
                return null;
            }
            
            // Get the plant prefab from ZNetScene
            var prefab = ZNetScene.instance?.GetPrefab(plantPrefab);
            if (prefab == null)
            {
                Debug.LogWarning($"[FarmingDataHelper] Plant prefab not found: {plantPrefab}");
                return null;
            }
            
            // Spawn the plant via ZNetScene for proper network registration
            var spawned = CompanionNetworkHelper.Spawn(prefab, position, Quaternion.identity);
            
            Debug.Log($"[FarmingDataHelper] Planted {plantPrefab} at {position}");
            
            return spawned;
        }
        
        /// <summary>
        /// Checks if a position is valid for planting.
        /// </summary>
        public static bool IsValidPlantingPosition(Vector3 position, string seedPrefabName, float plantSpacing = 1f)
        {
            // Check biome compatibility
            var biome = Heightmap.FindBiome(position);
            if (!CanGrowInBiome(seedPrefabName, biome))
                return false;
            
            // Check if cultivated
            var heightmap = Heightmap.FindHeightmap(position);
            if (heightmap == null || !heightmap.IsCultivated(position))
                return false;
            
            // Check spacing
            if (HasExistingPlantNearby(position, plantSpacing))
                return false;
            
            // Check for roof (plants need sunlight)
            int roofMask = LayerMask.GetMask("Default", "static_solid", "piece");
            if (Physics.Raycast(position, Vector3.up, 100f, roofMask))
                return false;
            
            return true;
        }
        
        #endregion
        
        #region Utilities
        
        /// <summary>
        /// Gets the prefab name from a GameObject, handling "(Clone)" suffix.
        /// </summary>
        private static string GetPrefabName(GameObject obj)
        {
            if (obj == null) return string.Empty;
            
            string name = obj.name;
            
            // Remove "(Clone)" suffix if present
            int cloneIndex = name.IndexOf("(Clone)");
            if (cloneIndex > 0)
                name = name.Substring(0, cloneIndex);
            
            return name.Trim();
        }
        
        /// <summary>
        /// Checks if private area access is granted at position.
        /// </summary>
        public static bool HasPrivateAreaAccess(Vector3 position)
        {
            return PrivateArea.CheckAccess(position, flash: false);
        }
        
        #endregion
    }
}
