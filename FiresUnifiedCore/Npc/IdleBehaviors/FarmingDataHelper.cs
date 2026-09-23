using UnityEngine;
using System.Collections.Generic;
using FiresCore.Npc;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Farming data read from the live game: the crops the Cultivator plants (sapling piece, the items it consumes,
    /// the pickables it grows into) and what every hive produces, plus crop/hive detection and planting checks.
    /// </summary>
    public static class FarmingDataHelper
    {
        /// <summary>A Cultivator piece that grows into a harvestable crop on open cultivated ground.</summary>
        public sealed class CropSapling
        {
            public readonly GameObject Prefab;
            public readonly Plant Plant;
            public readonly (string Item, int Amount)[] Seeds;
            
            public CropSapling(GameObject prefab, Plant plant, (string Item, int Amount)[] seeds)
            {
                Prefab = prefab;
                Plant = plant;
                Seeds = seeds;
            }
        }
            
        private const string CultivatorItemPrefab = "Cultivator";
        private static readonly IReadOnlyList<CropSapling> NoSaplings = new CropSapling[0];
            
        private static ObjectDB s_tablesSource;
        private static readonly Dictionary<string, List<CropSapling>> SaplingsBySeed = new Dictionary<string, List<CropSapling>>();
        private static readonly HashSet<string> CropPickablePrefabs = new HashSet<string>();
        private static readonly HashSet<string> FarmProducePrefabs = new HashSet<string>();

        #region Live Game Tables

        /// <summary>Builds the tables once per ObjectDB from the Cultivator's piece table and every Beehive prefab.</summary>
        private static bool EnsureTables()
        {
            var objectDB = ObjectDB.instance;
            if (objectDB == null || ZNetScene.instance == null) return false;
            if (s_tablesSource == objectDB) return true;

            var cultivatorPieces = objectDB.GetItemPrefab(CultivatorItemPrefab)?.GetComponent<ItemDrop>()?.m_itemData.m_shared.m_buildPieces;
            if (cultivatorPieces == null) return false;

            SaplingsBySeed.Clear();
            CropPickablePrefabs.Clear();
            FarmProducePrefabs.Clear();
            foreach (var piecePrefab in cultivatorPieces.m_pieces)
                AddCultivatorPiece(piecePrefab);
            foreach (var prefab in ZNetScene.instance.m_prefabs)
                AddHiveProduce(prefab);
            
            s_tablesSource = objectDB;
            return true;
        }
        
        private static void AddCultivatorPiece(GameObject piecePrefab)
        {
            var piece = piecePrefab != null ? piecePrefab.GetComponent<Piece>() : null;
            if (piece == null || !piece.m_enabled) return;
        
            var seeds = new List<(string Item, int Amount)>();
            foreach (var requirement in piece.m_resources)
            {
                if (requirement?.m_resItem == null) continue;
                seeds.Add((requirement.m_resItem.name, requirement.m_amount));
                FarmProducePrefabs.Add(requirement.m_resItem.name);
            }
        
            var plant = piecePrefab.GetComponent<Plant>();
            if (plant == null || !GrowsOnOpenCultivatedGround(plant) || seeds.Count == 0) return;
        
            var grownCrops = new List<Pickable>();
            foreach (var grownPrefab in plant.m_grownPrefabs)
            {
                var pickable = grownPrefab != null ? grownPrefab.GetComponent<Pickable>() : null;
                if (pickable != null) grownCrops.Add(pickable);
            }
            if (grownCrops.Count == 0) return;
        
            var crop = new CropSapling(piecePrefab, plant, seeds.ToArray());
            foreach (var seed in seeds)
            {
                if (!SaplingsBySeed.TryGetValue(seed.Item, out var saplings))
                    SaplingsBySeed[seed.Item] = saplings = new List<CropSapling>();
                saplings.Add(crop);
            }
            foreach (var pickable in grownCrops)
            {
                CropPickablePrefabs.Add(pickable.name);
                AddPickableDrops(pickable);
            }
        }
        
        /// <summary>Trees need no soil and vines (m_attachDistance) need a wall; only field crops qualify.</summary>
        private static bool GrowsOnOpenCultivatedGround(Plant plant)
            => plant.m_needCultivatedGround && plant.m_attachDistance <= 0f;
        
        private static void AddPickableDrops(Pickable pickable)
        {
            if (pickable.m_itemPrefab != null) FarmProducePrefabs.Add(pickable.m_itemPrefab.name);
            foreach (var drop in pickable.m_extraDrops.m_drops)
                if (drop.m_item != null) FarmProducePrefabs.Add(drop.m_item.name);
        }
        
        private static void AddHiveProduce(GameObject prefab)
        {
            var hive = prefab != null ? prefab.GetComponent<Beehive>() : null;
            if (hive != null && hive.m_honeyItem != null) FarmProducePrefabs.Add(hive.m_honeyItem.name);
        }
        
        #endregion
        
        #region Public API - Seeds
        
        /// <summary>The crop saplings this item plants (1.0 KaleSeeds plant either sapling_Kale or sapling_seedkale).</summary>
        public static IReadOnlyList<CropSapling> SaplingsForSeed(string seedPrefabName)
        {
            if (string.IsNullOrEmpty(seedPrefabName) || !EnsureTables()) return NoSaplings;
            return SaplingsBySeed.TryGetValue(seedPrefabName, out var saplings) ? saplings : NoSaplings;
        }
        
        /// <summary>
        /// Checks if an item can be planted.
        /// </summary>
        public static bool CanBePlanted(ItemDrop.ItemData item)
        {
            return SaplingsForSeed(item?.m_dropPrefab?.name).Count > 0;
        }
        
        /// <summary>The biome, heat and cold rules of vanilla Plant.UpdateHealth (Plant.cs:197-217).</summary>
        public static bool CanGrowAt(CropSapling crop, Vector3 position)
        {
            var biome = Heightmap.FindBiome(position);
            if ((biome & crop.Plant.m_biome) == Heightmap.Biome.None) return false;
            if (!crop.Plant.m_tolerateHeat && biome == Heightmap.Biome.AshLands && !ShieldGenerator.IsInsideShield(position))
                return false;
            bool coldBiome = biome == Heightmap.Biome.DeepNorth || biome == Heightmap.Biome.Mountain;
            return crop.Plant.m_tolerateCold || !coldBiome || ShieldGenerator.IsInsideShield(position);
        }
        
        /// <summary>Anything the Cultivator plants, a crop drops, or a hive produces.</summary>
        public static bool IsFarmProduce(string prefabName)
        {
            return !string.IsNullOrEmpty(prefabName) && EnsureTables() && FarmProducePrefabs.Contains(prefabName);
        }
        
        #endregion
        
        #region Public API - Pickables/Crops
        
        /// <summary>
        /// Checks if a Pickable component represents a harvestable crop.
        /// </summary>
        public static bool IsHarvestableCrop(Pickable pickable)
        {
            if (pickable == null || !EnsureTables()) return false;
            
            string prefabName = GetPrefabName(pickable.gameObject);
            return CropPickablePrefabs.Contains(prefabName);
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
        
        #endregion
        
        #region Public API - Beehives
        
        /// <summary>
        /// Checks if a hive (beehive, bird nest) has produce ready for harvest.
        /// </summary>
        public static bool HasProduceReady(Beehive beehive)
        {
            if (beehive == null) return false;
            return GetStoredProduce(beehive) > 0;
        }
        
        /// <summary>
        /// Gets how many items the hive holds (0-4).
        /// Uses ZDO since GetHoneyLevel() is private in Valheim.
        /// </summary>
        public static int GetStoredProduce(Beehive beehive)
        {
            if (beehive == null) return 0;
            
            var nview = beehive.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return 0;
            
            return nview.GetZDO().GetInt(ZDOVars.s_level, 0);
        }
        
        public static string GetProduceName(Beehive beehive)
        {
            return Localization.instance.Localize(beehive.m_honeyItem.m_itemData.m_shared.m_name);
        }
            
        public static string GetHiveName(Beehive beehive)
        {
            return Localization.instance.Localize(beehive.m_name);
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
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                var beehive = collider.GetComponent<Beehive>() ?? collider.GetComponentInParent<Beehive>();
                if (beehive != null && HasProduceReady(beehive))
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
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                var pickable = collider.GetComponent<Pickable>() ?? collider.GetComponentInParent<Pickable>();
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
            
            foreach (var collider in overlaps)
            {
                if (collider == null) continue;
                
                // Check for Plant component
                var plant = collider.GetComponent<Plant>() ?? collider.GetComponentInParent<Plant>();
                if (plant != null)
                    return true;
                
                // Check for Pickable (mature crops)
                var pickable = collider.GetComponent<Pickable>() ?? collider.GetComponentInParent<Pickable>();
                if (pickable != null && IsHarvestableCrop(pickable))
                    return true;
            }
            
            return false;
        }
        
        #endregion
        
        #region Public API - Planting
        
        /// <summary>
        /// Spawns the crop's sapling piece at the specified position.
        /// </summary>
        public static GameObject PlantSapling(CropSapling crop, Vector3 position)
        {
            // Spawn the plant via ZNetScene for proper network registration
            var spawned = CompanionNetworkHelper.Spawn(crop.Prefab, position, Quaternion.identity);
            
            Debug.Log($"[FarmingDataHelper] Planted {crop.Prefab.name} at {position}");
            
            return spawned;
        }
        
        /// <summary>
        /// Checks if a position is valid for planting.
        /// </summary>
        public static bool IsValidPlantingPosition(Vector3 position, CropSapling crop, float plantSpacing = 1f)
        {
            if (!CanGrowAt(crop, position))
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

        #endregion
    }
}
