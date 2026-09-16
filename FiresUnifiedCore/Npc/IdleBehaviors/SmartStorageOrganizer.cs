using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Picks which chest an item belongs in by what it is and which crafting stations are nearby, so fuel lands
    /// by kilns and fireplaces, ore by smelters, bars by forges, food by cooking stations and cauldrons, building
    /// materials by workbenches, and trophies and valuables together.
    /// </summary>
    public static class SmartStorageOrganizer
    {
        #region Item Categories
        
        /// <summary>
        /// Categories of items for smart sorting.
        /// </summary>
        public enum ItemCategory
        {
            Unknown,
            
            // Fuel types
            WoodFuel,           // Wood, FineWood, CoreWood, etc.
            CoalFuel,           // Coal for smelters/forges
            
            // Raw materials for processing
            Ore,                // CopperOre, TinOre, IronScrap, etc.
            MetalBar,           // Processed metal ingots/bars
            
            // Building materials
            Stone,              // Stone, various stone types
            BuildingWood,       // Wood for building (same as fuel but different purpose)
            ProcessedBuilding,  // Cut stone, tar, etc.
            
            // Food and cooking
            RawMeat,            // Raw meat for cooking
            RawFish,            // Fish for cooking
            Vegetables,         // Turnips, carrots, onions, etc.
            Berries,            // Berries, honey, etc.
            CookedFood,         // Finished food items
            MeadBase,           // Mead bases for fermenter
            FinishedMead,       // Finished meads/potions
            
            // Crafting materials
            Leather,            // Deer hide, leather, etc.
            Cloth,              // Linen thread, etc.
            Chain,              // Chain for armor
            Feathers,           // For arrows
            Resin,              // For various crafting
            
            // Valuables
            Coins,              // Gold coins
            Gems,               // Rubies, amber, etc.
            Trophy,             // Boss trophies and creature trophies
            
            // Ammunition
            Arrows,             // All arrow types

            // Equipment stored in chests
            Weapon,             // Melee weapons, bows (items in chests — companions never deposit equipped weapons)
            Armor,              // Helmets, chest, legs, shoulder, shields, utility

            // Special
            DragonTears,        // For artisan table
            BlackCore,          // For various high-tier

            // Misc materials
            MiscMaterial        // Everything else
        }
        
        /// <summary>
        /// Station types that items can be associated with.
        /// </summary>
        public enum StationType
        {
            None,
            Smelter,
            BlastFurnace,
            Kiln,
            Forge,
            Workbench,
            CookingStation,
            Cauldron,
            Fermenter,
            ArtisanTable,
            Stonecutter,
            SpinningWheel,
            Windmill,
            FireSource      // Campfires, hearths, etc.
        }
        
        #endregion
        
        #region Item Classification
        
        /// <summary>
        /// Item prefab name patterns mapped to categories.
        /// </summary>
        private static readonly Dictionary<string, ItemCategory> ItemPrefabCategories = new Dictionary<string, ItemCategory>(System.StringComparer.OrdinalIgnoreCase)
        {
            // Fuels
            { "Wood", ItemCategory.WoodFuel },
            { "FineWood", ItemCategory.WoodFuel },
            { "RoundLog", ItemCategory.WoodFuel },
            { "CoreWood", ItemCategory.WoodFuel },
            { "ElderBark", ItemCategory.WoodFuel },
            { "YggdrasilWood", ItemCategory.WoodFuel },
            { "Resin", ItemCategory.WoodFuel },
            { "Coal", ItemCategory.CoalFuel },
            
            // Ores
            { "CopperOre", ItemCategory.Ore },
            { "TinOre", ItemCategory.Ore },
            { "IronOre", ItemCategory.Ore },
            { "IronScrap", ItemCategory.Ore },
            { "SilverOre", ItemCategory.Ore },
            { "BlackMetalScrap", ItemCategory.Ore },
            { "FlametalOre", ItemCategory.Ore },
            { "FlametalOreNew", ItemCategory.Ore },
            
            // Metal bars
            { "Copper", ItemCategory.MetalBar },
            { "CopperBar", ItemCategory.MetalBar },
            { "Tin", ItemCategory.MetalBar },
            { "TinBar", ItemCategory.MetalBar },
            { "Bronze", ItemCategory.MetalBar },
            { "BronzeBar", ItemCategory.MetalBar },
            { "Iron", ItemCategory.MetalBar },
            { "IronBar", ItemCategory.MetalBar },
            { "Silver", ItemCategory.MetalBar },
            { "SilverBar", ItemCategory.MetalBar },
            { "BlackMetal", ItemCategory.MetalBar },
            { "BlackMetalBar", ItemCategory.MetalBar },
            { "FlametalNew", ItemCategory.MetalBar },
            { "Flametal", ItemCategory.MetalBar },
            
            // Stone and building
            { "Stone", ItemCategory.Stone },
            { "Obsidian", ItemCategory.Stone },
            { "Flint", ItemCategory.Stone },
            { "SharpeningStone", ItemCategory.ProcessedBuilding },
            { "BlackMarble", ItemCategory.ProcessedBuilding },
            { "Tar", ItemCategory.ProcessedBuilding },
            { "Crystal", ItemCategory.ProcessedBuilding },
            
            // Leather and cloth
            { "DeerHide", ItemCategory.Leather },
            { "LeatherScraps", ItemCategory.Leather },
            { "TrollHide", ItemCategory.Leather },
            { "WolfPelt", ItemCategory.Leather },
            { "LoxPelt", ItemCategory.Leather },
            { "ScaleHide", ItemCategory.Leather },
            { "LinenThread", ItemCategory.Cloth },
            { "JuteRed", ItemCategory.Cloth },
            { "Chain", ItemCategory.Chain },
            
            // Food - Raw meats
            { "RawMeat", ItemCategory.RawMeat },
            { "DeerMeat", ItemCategory.RawMeat },
            { "WolfMeat", ItemCategory.RawMeat },
            { "LoxMeat", ItemCategory.RawMeat },
            { "ChickenMeat", ItemCategory.RawMeat },
            { "HareMeat", ItemCategory.RawMeat },
            { "BugMeat", ItemCategory.RawMeat },
            { "SerpentMeat", ItemCategory.RawMeat },
            { "NeckTail", ItemCategory.RawMeat },
            
            // Food - Fish
            { "Fish1", ItemCategory.RawFish },
            { "Fish2", ItemCategory.RawFish },
            { "Fish3", ItemCategory.RawFish },
            { "Fish4_cave", ItemCategory.RawFish },
            { "Fish5", ItemCategory.RawFish },
            { "Fish6", ItemCategory.RawFish },
            { "Fish7", ItemCategory.RawFish },
            { "Fish8", ItemCategory.RawFish },
            { "Fish9", ItemCategory.RawFish },
            { "FishRaw", ItemCategory.RawFish },
            
            // Food - Vegetables
            { "Turnip", ItemCategory.Vegetables },
            { "Carrot", ItemCategory.Vegetables },
            { "Onion", ItemCategory.Vegetables },
            { "Barley", ItemCategory.Vegetables },
            { "Flax", ItemCategory.Vegetables },
            { "Mushroom", ItemCategory.Vegetables },
            { "MushroomBlue", ItemCategory.Vegetables },
            { "MushroomYellow", ItemCategory.Vegetables },
            { "Thistle", ItemCategory.Vegetables },
            { "Bloodbag", ItemCategory.Vegetables },
            { "Entrails", ItemCategory.Vegetables },
            
            // Food - Berries and sweets
            { "Raspberry", ItemCategory.Berries },
            { "Blueberries", ItemCategory.Berries },
            { "Cloudberry", ItemCategory.Berries },
            { "Honey", ItemCategory.Berries },
            { "QueenBee", ItemCategory.Berries },
            
            // Mead bases
            { "MeadBaseTasty", ItemCategory.MeadBase },
            { "MeadBaseMinorHealing", ItemCategory.MeadBase },
            { "MeadBaseMediumHealing", ItemCategory.MeadBase },
            { "MeadBaseMinorStamina", ItemCategory.MeadBase },
            { "MeadBaseMediumStamina", ItemCategory.MeadBase },
            { "MeadBasePoisonResist", ItemCategory.MeadBase },
            { "MeadBaseFrostResist", ItemCategory.MeadBase },
            { "MeadBaseFireResist", ItemCategory.MeadBase },
            { "BarleyWine", ItemCategory.MeadBase },
            
            // Finished meads
            { "MeadTasty", ItemCategory.FinishedMead },
            { "MeadHealthMinor", ItemCategory.FinishedMead },
            { "MeadHealthMedium", ItemCategory.FinishedMead },
            { "MeadStaminaMinor", ItemCategory.FinishedMead },
            { "MeadStaminaMedium", ItemCategory.FinishedMead },
            { "MeadPoisonResist", ItemCategory.FinishedMead },
            { "MeadFrostResist", ItemCategory.FinishedMead },
            { "MeadFireResist", ItemCategory.FinishedMead },
            
            // Valuables
            { "Coins", ItemCategory.Coins },
            { "Ruby", ItemCategory.Gems },
            { "Amber", ItemCategory.Gems },
            { "AmberPearl", ItemCategory.Gems },
            
            // Arrows
            { "ArrowWood", ItemCategory.Arrows },
            { "ArrowFlint", ItemCategory.Arrows },
            { "ArrowBronze", ItemCategory.Arrows },
            { "ArrowIron", ItemCategory.Arrows },
            { "ArrowSilver", ItemCategory.Arrows },
            { "ArrowObsidian", ItemCategory.Arrows },
            { "ArrowPoison", ItemCategory.Arrows },
            { "ArrowFrost", ItemCategory.Arrows },
            { "ArrowFire", ItemCategory.Arrows },
            { "ArrowNeedle", ItemCategory.Arrows },
            { "ArrowCarapace", ItemCategory.Arrows },
            
            // Feathers and misc crafting
            { "Feathers", ItemCategory.Feathers },
            { "Guck", ItemCategory.MiscMaterial },
            { "Ooze", ItemCategory.MiscMaterial },
            { "FreezeGland", ItemCategory.MiscMaterial },
            { "Needle", ItemCategory.MiscMaterial },
            { "Chitin", ItemCategory.MiscMaterial },
            
            // Special items
            { "DragonTear", ItemCategory.DragonTears },
            { "SurtlingCore", ItemCategory.BlackCore },
            { "BlackCore", ItemCategory.BlackCore },
        };
        
        /// <summary>
        /// Maps item categories to their preferred station types.
        /// </summary>
        private static readonly Dictionary<ItemCategory, StationType[]> CategoryToStations = new Dictionary<ItemCategory, StationType[]>
        {
            { ItemCategory.WoodFuel, new[] { StationType.Kiln, StationType.FireSource, StationType.Smelter } },
            { ItemCategory.CoalFuel, new[] { StationType.Smelter, StationType.BlastFurnace, StationType.Forge } },
            { ItemCategory.Ore, new[] { StationType.Smelter, StationType.BlastFurnace } },
            { ItemCategory.MetalBar, new[] { StationType.Forge, StationType.ArtisanTable } },
            { ItemCategory.Stone, new[] { StationType.Stonecutter, StationType.Workbench } },
            { ItemCategory.BuildingWood, new[] { StationType.Workbench } },
            { ItemCategory.ProcessedBuilding, new[] { StationType.Workbench, StationType.Stonecutter } },
            { ItemCategory.RawMeat, new[] { StationType.CookingStation } },
            { ItemCategory.RawFish, new[] { StationType.CookingStation } },
            { ItemCategory.Vegetables, new[] { StationType.Cauldron, StationType.CookingStation } },
            { ItemCategory.Berries, new[] { StationType.Cauldron } },
            { ItemCategory.MeadBase, new[] { StationType.Fermenter } },
            { ItemCategory.FinishedMead, new[] { StationType.None } }, // Store away from stations
            { ItemCategory.CookedFood, new[] { StationType.None } },
            { ItemCategory.Leather, new[] { StationType.Workbench, StationType.Forge } },
            { ItemCategory.Cloth, new[] { StationType.SpinningWheel, StationType.Workbench } },
            { ItemCategory.Chain, new[] { StationType.Forge } },
            { ItemCategory.Feathers, new[] { StationType.Workbench } },
            { ItemCategory.DragonTears, new[] { StationType.ArtisanTable } },
            { ItemCategory.BlackCore, new[] { StationType.Smelter, StationType.Forge } },
            { ItemCategory.Arrows, new[] { StationType.Workbench } },
            { ItemCategory.Weapon, new[] { StationType.Forge } },        // Weapons crafted at forge
            { ItemCategory.Armor, new[] { StationType.Forge } },         // Armor crafted at forge
            { ItemCategory.Coins, new[] { StationType.None } }, // Store away
            { ItemCategory.Gems, new[] { StationType.None } },
            { ItemCategory.Trophy, new[] { StationType.None } }, // Trophy storage
            { ItemCategory.MiscMaterial, new[] { StationType.Workbench } },
        };
        
        /// <summary>
        /// Gets the category of an item based on its prefab name.
        /// </summary>
        public static ItemCategory GetItemCategory(ItemDrop.ItemData item)
        {
            if (item == null) return ItemCategory.Unknown;
            
            string prefabName = item.m_dropPrefab?.name ?? "";
            
            // Check direct mapping first
            if (ItemPrefabCategories.TryGetValue(prefabName, out var category))
            {
                return category;
            }
            
            // Check partial matches for common patterns
            string lowerName = prefabName.ToLowerInvariant();
            
            // Trophies
            if (lowerName.Contains("trophy"))
                return ItemCategory.Trophy;
            
            // Cooked food patterns
            if (lowerName.Contains("cooked") || lowerName.Contains("grilled") || 
                lowerName.Contains("sausage") || lowerName.Contains("bread") ||
                lowerName.Contains("pie") || lowerName.Contains("stew") ||
                lowerName.Contains("soup") || lowerName.Contains("pudding"))
                return ItemCategory.CookedFood;
            
            // Mead patterns
            if (lowerName.Contains("meadbase"))
                return ItemCategory.MeadBase;
            if (lowerName.Contains("mead") && !lowerName.Contains("base"))
                return ItemCategory.FinishedMead;
            
            // Arrow patterns
            if (lowerName.Contains("arrow"))
                return ItemCategory.Arrows;
            
            // Ore patterns
            if (lowerName.Contains("ore") || lowerName.Contains("scrap"))
                return ItemCategory.Ore;
            
            // Bar/ingot patterns
            if (lowerName.Contains("bar") || lowerName.Contains("ingot"))
                return ItemCategory.MetalBar;
            
            // Wood patterns
            if (lowerName.Contains("wood") || lowerName.Contains("log"))
                return ItemCategory.WoodFuel;
            
            // Hide/leather patterns
            if (lowerName.Contains("hide") || lowerName.Contains("pelt") || lowerName.Contains("leather"))
                return ItemCategory.Leather;
            
            // Fish patterns
            if (lowerName.StartsWith("fish"))
                return ItemCategory.RawFish;
            
            // Meat patterns
            if (lowerName.Contains("meat") || lowerName.Contains("tail"))
                return ItemCategory.RawMeat;

            // Weapon/armor detection by ItemType — catches all modded content too
            if (item.m_shared != null)
            {
                switch (item.m_shared.m_itemType)
                {
                    case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                    case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                    case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                    case ItemDrop.ItemData.ItemType.Bow:
                    case ItemDrop.ItemData.ItemType.Torch:
                        return ItemCategory.Weapon;

                    case ItemDrop.ItemData.ItemType.Helmet:
                    case ItemDrop.ItemData.ItemType.Chest:
                    case ItemDrop.ItemData.ItemType.Legs:
                    case ItemDrop.ItemData.ItemType.Shoulder:
                    case ItemDrop.ItemData.ItemType.Shield:
                    case ItemDrop.ItemData.ItemType.Utility:
                        return ItemCategory.Armor;
                }
            }

            return ItemCategory.MiscMaterial;
        }
        
        /// <summary>
        /// Gets the preferred station types for storing an item category.
        /// </summary>
        public static StationType[] GetPreferredStations(ItemCategory category)
        {
            if (CategoryToStations.TryGetValue(category, out var stations))
            {
                return stations;
            }
            return new[] { StationType.Workbench }; // Default to workbench
        }
        
        #endregion
        
        #region Station Detection
        
        /// <summary>
        /// Cached station info for a location.
        /// </summary>
        public class StationInfo
        {
            public StationType Type;
            public GameObject Object;
            public Vector3 Position;
            public float Distance;
        }
        
        /// <summary>
        /// Finds all crafting stations within a radius.
        /// </summary>
        public static List<StationInfo> FindNearbyStations(Vector3 position, float radius)
        {
            var stations = new List<StationInfo>();
            var colliders = Physics.OverlapSphere(position, radius);
            var processed = new HashSet<GameObject>();
            
            foreach (var collider in colliders)
            {
                if (collider == null) continue;
                
                GameObject obj = collider.gameObject;
                if (processed.Contains(obj)) continue;
                
                // Check for various station types
                StationType stationType = StationType.None;
                
                // Smelter
                var smelter = obj.GetComponent<Smelter>() ?? obj.GetComponentInParent<Smelter>();
                if (smelter != null)
                {
                    // Distinguish between smelter types
                    string prefabName = GetPrefabName(obj);
                    if (prefabName.Contains("blast") || prefabName.Contains("furnace"))
                        stationType = StationType.BlastFurnace;
                    else if (prefabName.Contains("charcoal") || prefabName.Contains("kiln"))
                        stationType = StationType.Kiln;
                    else
                        stationType = StationType.Smelter;
                    
                    obj = smelter.gameObject;
                }
                
                // Crafting Station (Workbench, Forge, etc.)
                if (stationType == StationType.None)
                {
                    var craftingStation = obj.GetComponent<CraftingStation>() ?? obj.GetComponentInParent<CraftingStation>();
                    if (craftingStation != null)
                    {
                        string prefabName = GetPrefabName(obj);
                        if (prefabName.Contains("forge"))
                            stationType = StationType.Forge;
                        else if (prefabName.Contains("artisan"))
                            stationType = StationType.ArtisanTable;
                        else if (prefabName.Contains("stonecutter"))
                            stationType = StationType.Stonecutter;
                        else
                            stationType = StationType.Workbench;
                        
                        obj = craftingStation.gameObject;
                    }
                }
                
                // Cooking Station
                if (stationType == StationType.None)
                {
                    var cookingStation = obj.GetComponent<CookingStation>() ?? obj.GetComponentInParent<CookingStation>();
                    if (cookingStation != null)
                    {
                        stationType = StationType.CookingStation;
                        obj = cookingStation.gameObject;
                    }
                }
                
                // Fermenter
                if (stationType == StationType.None)
                {
                    var fermenter = obj.GetComponent<Fermenter>() ?? obj.GetComponentInParent<Fermenter>();
                    if (fermenter != null)
                    {
                        stationType = StationType.Fermenter;
                        obj = fermenter.gameObject;
                    }
                }
                
                // Fireplace (for fuel)
                if (stationType == StationType.None)
                {
                    var fireplace = obj.GetComponent<Fireplace>() ?? obj.GetComponentInParent<Fireplace>();
                    if (fireplace != null)
                    {
                        stationType = StationType.FireSource;
                        obj = fireplace.gameObject;
                    }
                }
                
                // Spinning Wheel
                if (stationType == StationType.None)
                {
                    string prefabName = GetPrefabName(obj).ToLowerInvariant();
                    if (prefabName.Contains("spinning"))
                    {
                        stationType = StationType.SpinningWheel;
                    }
                    else if (prefabName.Contains("windmill"))
                    {
                        stationType = StationType.Windmill;
                    }
                    else if (prefabName.Contains("cauldron"))
                    {
                        stationType = StationType.Cauldron;
                    }
                }
                
                if (stationType != StationType.None)
                {
                    processed.Add(obj);
                    stations.Add(new StationInfo
                    {
                        Type = stationType,
                        Object = obj,
                        Position = obj.transform.position,
                        Distance = Vector3.Distance(position, obj.transform.position)
                    });
                }
            }
            
            return stations;
        }
        
        private static string GetPrefabName(GameObject obj)
        {
            if (obj == null) return "";
            
            var nview = obj.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
            {
                int prefabHash = nview.GetZDO()?.GetPrefab() ?? 0;
                if (prefabHash != 0)
                {
                    var prefab = ZNetScene.instance?.GetPrefab(prefabHash);
                    if (prefab != null)
                        return prefab.name;
                }
            }
            
            return obj.name.Replace("(Clone)", "").Trim();
        }
        
        #endregion
        
        #region Smart Chest Selection
        
        /// <summary>
        /// Result of finding the best chest for an item.
        /// </summary>
        public class ChestRecommendation
        {
            public Container Chest;
            public float Score;
            public string Reason;
            public bool HasMatchingItems;
            public StationInfo NearestRelevantStation;
        }
        
        /// <summary>
        /// Finds the best chest to store an item in, considering nearby stations.
        /// </summary>
        public static ChestRecommendation FindBestChestForItem(
            ItemDrop.ItemData item, 
            List<Container> chests, 
            Vector3 searchCenter,
            float stationSearchRadius = 20f)
        {
            if (item == null || chests == null || chests.Count == 0)
                return null;
            
            var category = GetItemCategory(item);
            var preferredStations = GetPreferredStations(category);
            
            // Find relevant stations
            var nearbyStations = FindNearbyStations(searchCenter, stationSearchRadius);
            var relevantStations = nearbyStations
                .Where(s => preferredStations.Contains(s.Type))
                .OrderBy(s => s.Distance)
                .ToList();
            
            ChestRecommendation best = null;
            float bestScore = float.MinValue;
            
            foreach (var chest in chests)
            {
                if (chest == null) continue;
                
                var chestInv = chest.GetInventory();
                if (chestInv == null) continue;
                
                // Check if chest has room
                bool hasRoom = chestInv.GetEmptySlots() > 0;
                bool hasMatchingStack = HasMatchingStackRoom(chestInv, item);
                
                if (!hasRoom && !hasMatchingStack) continue;
                
                float score = 0f;
                string reason = "";
                StationInfo nearestStation = null;
                
                // SCORE 1: Matching items in chest (for stacking) - HIGHEST PRIORITY
                if (hasMatchingStack)
                {
                    score += 1000f;
                    reason = "Stacking with existing items";
                }
                
                // SCORE 2: Chest already contains same category items
                int sameCategoryCount = CountItemsOfCategory(chestInv, category);
                if (sameCategoryCount > 0)
                {
                    score += 500f + sameCategoryCount * 10f;
                    if (string.IsNullOrEmpty(reason))
                        reason = $"Contains similar items ({sameCategoryCount})";
                }
                
                // SCORE 3: Proximity to relevant station
                if (relevantStations.Count > 0 && preferredStations[0] != StationType.None)
                {
                    float chestToStationDist = float.MaxValue;
                    StationInfo closestStation = null;
                    
                    foreach (var station in relevantStations)
                    {
                        float dist = Vector3.Distance(chest.transform.position, station.Position);
                        if (dist < chestToStationDist)
                        {
                            chestToStationDist = dist;
                            closestStation = station;
                        }
                    }
                    
                    if (closestStation != null && chestToStationDist < stationSearchRadius)
                    {
                        // Higher score for chests closer to relevant stations
                        float proximityBonus = 300f * (1f - chestToStationDist / stationSearchRadius);
                        score += proximityBonus;
                        nearestStation = closestStation;
                        
                        if (string.IsNullOrEmpty(reason))
                            reason = $"Near {closestStation.Type} ({chestToStationDist:F0}m)";
                    }
                }
                
                // SCORE 4: Trophy chests should only contain trophies
                if (category == ItemCategory.Trophy)
                {
                    int trophyCount = CountItemsOfCategory(chestInv, ItemCategory.Trophy);
                    int otherCount = chestInv.GetAllItems().Count - trophyCount;
                    
                    if (trophyCount > 0 && otherCount == 0)
                    {
                        score += 800f; // Prefer dedicated trophy chests
                        reason = "Dedicated trophy storage";
                    }
                }
                
                // SCORE 5: Valuables should be stored together
                if (category == ItemCategory.Coins || category == ItemCategory.Gems)
                {
                    int coinCount = CountItemsOfCategory(chestInv, ItemCategory.Coins);
                    int gemCount = CountItemsOfCategory(chestInv, ItemCategory.Gems);

                    if (coinCount > 0 || gemCount > 0)
                    {
                        score += 700f;
                        reason = "Valuables storage";
                    }
                }

                // SCORE 5b: Weapons/armor should be grouped in armory chests
                if (category == ItemCategory.Weapon || category == ItemCategory.Armor)
                {
                    int weaponCount = CountItemsOfCategory(chestInv, ItemCategory.Weapon);
                    int armorCount  = CountItemsOfCategory(chestInv, ItemCategory.Armor);

                    if (weaponCount > 0 || armorCount > 0)
                    {
                        // Strongly prefer a chest that already has weapons/armor
                        score += 750f + (weaponCount + armorCount) * 10f;
                        if (string.IsNullOrEmpty(reason))
                            reason = "Armory storage";
                    }
                }

                // SCORE 6: Empty chests get a small bonus (for new categories)
                if (chestInv.GetAllItems().Count == 0)
                {
                    score += 50f;
                }
                
                // SCORE 7: Penalize overly full chests
                int emptySlots = chestInv.GetEmptySlots();
                if (emptySlots < 3)
                {
                    score -= 100f;
                }
                
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new ChestRecommendation
                    {
                        Chest = chest,
                        Score = score,
                        Reason = reason,
                        HasMatchingItems = hasMatchingStack || sameCategoryCount > 0,
                        NearestRelevantStation = nearestStation
                    };
                }
            }
            
            return best;
        }
        
        /// <summary>
        /// Checks if chest has room to stack more of this item.
        /// </summary>
        private static bool HasMatchingStackRoom(Inventory inv, ItemDrop.ItemData item)
        {
            if (inv == null || item == null) return false;
            
            string itemName = item.m_shared?.m_name ?? "";
            
            foreach (var existingItem in inv.GetAllItems())
            {
                if (existingItem == null) continue;
                
                string existingName = existingItem.m_shared?.m_name ?? "";
                
                if (itemName.Equals(existingName, System.StringComparison.OrdinalIgnoreCase))
                {
                    if (existingItem.m_stack < existingItem.m_shared.m_maxStackSize)
                    {
                        return true;
                    }
                }
            }
            
            return false;
        }
        
        /// <summary>
        /// Counts items of a specific category in an inventory.
        /// </summary>
        private static int CountItemsOfCategory(Inventory inv, ItemCategory category)
        {
            if (inv == null) return 0;
            
            int count = 0;
            foreach (var item in inv.GetAllItems())
            {
                if (item == null) continue;
                if (GetItemCategory(item) == category)
                {
                    count++;
                }
            }
            return count;
        }
        
        #endregion
        
        #region Organization Operations
        
        /// <summary>
        /// Result of an organization operation.
        /// </summary>
        public class OrganizationResult
        {
            public int ItemsMoved;
            public int StacksConsolidated;
            public int ChestsAffected;
            public List<string> Actions = new List<string>();
        }
        
        /// <summary>
        /// Organizes items across multiple chests based on station proximity.
        /// Moves items to be closer to the stations where they'll be used.
        /// </summary>
        public static OrganizationResult OrganizeChestCluster(
            List<Container> chests, 
            Vector3 clusterCenter,
            float stationSearchRadius = 25f)
        {
            var result = new OrganizationResult();
            
            if (chests == null || chests.Count < 2)
                return result;
            
            // Find all stations near this cluster
            var nearbyStations = FindNearbyStations(clusterCenter, stationSearchRadius);
            
            if (CompanionIdleBehavior.VerboseLogging)
                Debug.Log($"[SmartStorage] Found {nearbyStations.Count} stations near chest cluster");
            
            // Build a map of all items across all chests
            var allItems = new List<(Container chest, ItemDrop.ItemData item, int index)>();
            
            foreach (var chest in chests)
            {
                if (chest == null) continue;
                var inv = chest.GetInventory();
                if (inv == null) continue;
                
                int index = 0;
                foreach (var item in inv.GetAllItems())
                {
                    if (item == null) continue;
                    allItems.Add((chest, item, index++));
                }
            }
            
            // For each item, check if it should be moved to a better chest
            var itemsToMove = new List<(Container source, Container dest, ItemDrop.ItemData item, string reason)>();
            
            foreach (var (sourceChest, item, _) in allItems)
            {
                var recommendation = FindBestChestForItem(item, chests, clusterCenter, stationSearchRadius);
                
                if (recommendation == null) continue;
                if (recommendation.Chest == sourceChest) continue;
                if (recommendation.Score <= 0) continue;
                
                // Only move if the recommended chest is significantly better
                // (avoid unnecessary shuffling)
                var currentScore = ScoreChestForItem(sourceChest, item, nearbyStations);
                if (recommendation.Score > currentScore + 200f)
                {
                    itemsToMove.Add((sourceChest, recommendation.Chest, item, recommendation.Reason));
                }
            }
            
            // Execute moves, tracking which containers were dirtied
            var dirtiedChests = new HashSet<Container>();
            foreach (var (source, dest, item, reason) in itemsToMove)
            {
                var sourceInv = source.GetInventory();
                var destInv = dest.GetInventory();

                if (sourceInv == null || destInv == null) continue;
                if (!destInv.CanAddItem(item)) continue;

                var clone = item.Clone();
                if (destInv.AddItem(clone))
                {
                    sourceInv.RemoveItem(item);
                    result.ItemsMoved++;
                    result.Actions.Add($"Moved {item.m_shared?.m_name}: {reason}");
                    dirtiedChests.Add(source);
                    dirtiedChests.Add(dest);

                    if (CompanionIdleBehavior.VerboseLogging)
                        Debug.Log($"[SmartStorage] Moved {item.m_shared?.m_name}: {reason}");
                }
            }

            // Consolidate partial stacks (returns the set of chests it modified)
            result.StacksConsolidated = ConsolidateStacks(chests, dirtiedChests);

            // Persist every chest that was touched
            foreach (var chest in dirtiedChests)
                SaveContainer(chest);

            result.ChestsAffected = dirtiedChests.Count;
            return result;
        }
        
        /// <summary>
        /// Scores a chest for a specific item (used to compare current vs recommended).
        /// </summary>
        private static float ScoreChestForItem(Container chest, ItemDrop.ItemData item, List<StationInfo> stations)
        {
            if (chest == null || item == null) return 0;
            
            var inv = chest.GetInventory();
            if (inv == null) return 0;
            
            var category = GetItemCategory(item);
            var preferredStations = GetPreferredStations(category);
            
            float score = 0;
            
            // Check for stacking potential
            if (HasMatchingStackRoom(inv, item))
                score += 1000f;
            
            // Check category match
            int sameCategoryCount = CountItemsOfCategory(inv, category);
            if (sameCategoryCount > 0)
                score += 500f + sameCategoryCount * 10f;
            
            // Check station proximity
            if (preferredStations[0] != StationType.None)
            {
                foreach (var station in stations)
                {
                    if (!preferredStations.Contains(station.Type)) continue;
                    
                    float dist = Vector3.Distance(chest.transform.position, station.Position);
                    if (dist < 20f)
                    {
                        score += 300f * (1f - dist / 20f);
                        break;
                    }
                }
            }
            
            return score;
        }
        
        /// <summary>
        /// Consolidates partial stacks of the same item across chests so that
        /// a given item type fills one chest before spreading to the next.
        /// Uses proper AddItem / RemoveItem so that Valheim's inventory
        /// management layer stays consistent. Modified containers are added to
        /// <paramref name="dirtied"/> so the caller can persist them.
        /// </summary>
        public static int ConsolidateStacks(List<Container> chests, HashSet<Container> dirtied = null)
        {
            if (chests == null || chests.Count < 2) return 0;

            int totalConsolidated = 0;

            // Build map of item shared-name -> all (chest, item) pairs across chests
            var itemLocations = new Dictionary<string, List<(Container chest, ItemDrop.ItemData item)>>(System.StringComparer.OrdinalIgnoreCase);

            foreach (var chest in chests)
            {
                if (chest == null) continue;
                var inv = chest.GetInventory();
                if (inv == null) continue;

                foreach (var item in inv.GetAllItems())
                {
                    if (item == null) continue;
                    string key = item.m_shared?.m_name ?? "";
                    if (string.IsNullOrEmpty(key)) continue;

                    if (!itemLocations.ContainsKey(key))
                        itemLocations[key] = new List<(Container, ItemDrop.ItemData)>();
                    itemLocations[key].Add((chest, item));
                }
            }

            foreach (var kvp in itemLocations)
            {
                var locations = kvp.Value;
                if (locations.Count < 2) continue;

                // Only act when there are at least two partial stacks in different chests
                var partialStacks = locations
                    .Where(l => l.item != null && l.item.m_stack < l.item.m_shared.m_maxStackSize)
                    .OrderByDescending(l => l.item.m_stack) // fill the biggest partial first
                    .ToList();

                if (partialStacks.Count < 2) continue;

                // Drain smaller stacks into larger ones
                for (int i = 0; i < partialStacks.Count - 1; i++)
                {
                    var (targetChest, targetItem) = partialStacks[i];
                    if (targetItem == null) continue;

                    int maxStack = targetItem.m_shared.m_maxStackSize;

                    for (int j = partialStacks.Count - 1; j > i; j--)
                    {
                        var (sourceChest, sourceItem) = partialStacks[j];
                        if (sourceItem == null) continue;
                        if (sourceChest == targetChest) continue;

                        int canAdd = maxStack - targetItem.m_stack;
                        if (canAdd <= 0) break; // target is full - move to next target

                        int toMove = Mathf.Min(canAdd, sourceItem.m_stack);
                        if (toMove <= 0) continue;

                        // Transfer using inventory API
                        targetItem.m_stack += toMove;
                        sourceItem.m_stack -= toMove;
                        totalConsolidated += toMove;

                        dirtied?.Add(targetChest);
                        dirtied?.Add(sourceChest);

                        if (sourceItem.m_stack <= 0)
                        {
                            sourceChest.GetInventory()?.RemoveItem(sourceItem);
                            partialStacks[j] = (sourceChest, null);
                        }
                    }
                }
            }

            return totalConsolidated;
        }

        /// <summary>
        /// Persists a container's inventory to its ZDO so all clients see the changes.
        /// </summary>
        public static void SaveContainer(Container container)
        {
            if (container == null) return;
            try { container.Save(); }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[SmartStorage] SaveContainer failed for {container.name}: {ex.Message}");
            }
        }

        #endregion

        #region Public Utilities
        
        /// <summary>
        /// Gets a human-readable description of where an item should be stored.
        /// </summary>
        public static string GetStorageRecommendation(ItemDrop.ItemData item)
        {
            if (item == null) return "Unknown";
            
            var category = GetItemCategory(item);
            var stations = GetPreferredStations(category);
            
            if (stations.Length == 0 || stations[0] == StationType.None)
            {
                return category switch
                {
                    ItemCategory.Trophy => "Trophy storage",
                    ItemCategory.Coins => "Valuables chest",
                    ItemCategory.Gems => "Valuables chest",
                    ItemCategory.CookedFood => "Food storage",
                    ItemCategory.FinishedMead => "Mead/potion storage",
                    _ => "General storage"
                };
            }
            
            return $"Near {stations[0]}";
        }
        
        /// <summary>
        /// Checks if an item category should be stored near a specific station type.
        /// </summary>
        public static bool ShouldStoreNear(ItemCategory category, StationType station)
        {
            var preferred = GetPreferredStations(category);
            return preferred.Contains(station);
        }
        
        #endregion
    }
}
