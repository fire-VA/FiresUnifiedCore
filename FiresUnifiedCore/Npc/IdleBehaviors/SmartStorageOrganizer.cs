using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.IdleBehaviors
{
    /// <summary>
    /// Decides which chest an item belongs in from the game's own data. Station conversions, fuel, recipes and building
    /// pieces say where each item is used, so ore and coal go by the smelter, bars by the forge, wood and hides by the
    /// workbench, raw food by the cooking stations, mead bases by the fermenter and seeds by the fields. Items nothing
    /// consumes group by kind: armour and clothes in a wardrobe or by the beds, food and meads by the kitchen or beds,
    /// trophies and valuables in chests of their own. Every chest is kept to one kind of item.
    /// Chests are only written while this client owns them and nobody has them open; callers claim them first
    /// (<see cref="ChestHelper"/>), and vanilla Container saves every Add/RemoveItem made by the owner.
    /// </summary>
    public static class SmartStorageOrganizer
    {
        public enum ItemCategory
        {
            Unknown,
            WoodFuel,
            CoalFuel,
            Ore,
            MetalBar,
            BuildingMaterial,
            RawMeat,
            RawFish,
            Vegetables,
            Berries,
            CookedFood,
            MeadBase,
            FinishedMead,
            Seeds,
            Coins,
            Gems,
            Trophy,
            Arrows,
            Weapon,
            Armor,
            MiscMaterial
        }

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
            FireSource,
            BlackForge,
            GaldrTable,
            PrepTable,
            MeadCauldron,
            EitrRefinery,
            UpgradeStation,
            Farm,
            Bed,
            Other
        }

        public class StationInfo
        {
            public StationType Type;
            public string Key;
            public GameObject Object;
            public Vector3 Position;
            public float Distance;
        }

        public class ChestRecommendation
        {
            public Container Chest;
            public float Score;
            public string Reason;
            public bool HasMatchingItems;
            public StationInfo NearestRelevantStation;
        }

        public class OrganizationResult
        {
            public int ItemsMoved;
            public int StacksConsolidated;
            public int ChestsAffected;
            public List<string> Actions = new List<string>();
        }

        private const string FarmKey = "farm";
        private const string BedKey = "bed";
        private const string KilnKey = "$piece_charcoalkiln";
        private const string CoinsPrefab = "Coins";
        private const string WardrobeName = "$piece_chestwarderobe";
        private static readonly HashSet<string> OreStationKeys = new HashSet<string> { "$piece_smelter", "$piece_blastfurnace" };

        private static readonly Dictionary<string, StationType> KnownStations = new Dictionary<string, StationType>
        {
            { "$piece_smelter", StationType.Smelter },
            { "$piece_blastfurnace", StationType.BlastFurnace },
            { KilnKey, StationType.Kiln },
            { "$piece_forge", StationType.Forge },
            { "$piece_workbench", StationType.Workbench },
            { "$piece_stonecutter", StationType.Stonecutter },
            { "$piece_artisanstation", StationType.ArtisanTable },
            { "$piece_cauldron", StationType.Cauldron },
            { "$piece_meadcauldron", StationType.MeadCauldron },
            { "$piece_blackforge", StationType.BlackForge },
            { "$piece_magetable", StationType.GaldrTable },
            { "$piece_preptable", StationType.PrepTable },
            { "$piece_upgradestation", StationType.UpgradeStation },
            { "$piece_windmill", StationType.Windmill },
            { "$piece_spinningwheel", StationType.SpinningWheel },
            { "$piece_eitrrefinery", StationType.EitrRefinery },
            { "$piece_fermenter", StationType.Fermenter },
        };

        private const float ConversionWeight = 10f;
        private const float SmelterFuelWeight = 8f;
        private const float MinorFuelWeight = 4f;
        private const float RecipeWeightPerUse = 2f;
        private const float PieceWeightPerUse = 1f;
        private const float MaxUseWeight = 10f;

        private const float StackBonus = 1000f;
        private const float CohesionBonus = 500f;
        private const float MixPenalty = 300f;
        private const float EmptyChestBonus = 50f;
        private const float StationBonus = 400f;
        private const float WardrobeBonus = 600f;
        private const float WardrobeMisusePenalty = 300f;
        private const float HouseBonus = 300f;
        private const float GearHouseBonus = 150f;
        private const float KitchenBonus = 300f;
        private const float DedicatedBonus = 400f;
        private const float LowSpacePenalty = 100f;
        private const int LowSpaceSlots = 3;
        private const float MoveThreshold = 200f;
        private const float StationReach = 10f;
        private const float StationCacheSeconds = 5f;
        private const float StationCacheMaxDrift = 2f;
        private const int InitialColliderBuffer = 1024;

        #region Item profiles

        private sealed class ItemProfile
        {
            public ItemCategory Category;
            public readonly Dictionary<string, float> Uses = new Dictionary<string, float>();
            public string HomeKey;
            public string Group;
        }

        /// <summary>Everything the game data says about items, rebuilt when a new ObjectDB loads.</summary>
        private sealed class GameData
        {
            public readonly Dictionary<string, ItemProfile> Profiles = new Dictionary<string, ItemProfile>();
            public readonly Dictionary<string, string> PrefabByToken = new Dictionary<string, string>();
            public readonly HashSet<string> KitchenKeys = new HashSet<string>();
            public readonly HashSet<string> CropPickables = new HashSet<string>();
            public readonly Dictionary<string, Dictionary<string, float>> Uses = new Dictionary<string, Dictionary<string, float>>();
            public readonly Dictionary<string, Dictionary<string, int>> RecipeUses = new Dictionary<string, Dictionary<string, int>>();
            public readonly Dictionary<string, Dictionary<string, int>> PieceUses = new Dictionary<string, Dictionary<string, int>>();
            public readonly HashSet<string> FermenterInputs = new HashSet<string>();
            public readonly HashSet<string> FermenterOutputs = new HashSet<string>();
            public readonly HashSet<string> CookingInputs = new HashSet<string>();
            public readonly HashSet<string> FoodOutputs = new HashSet<string>();
            public readonly HashSet<string> Crops = new HashSet<string>();
            public readonly HashSet<string> Seeds = new HashSet<string>();
            public readonly HashSet<string> OreInputs = new HashSet<string>();
            public readonly HashSet<string> OreOutputs = new HashSet<string>();
            public readonly HashSet<string> SmelterFuels = new HashSet<string>();
            public readonly HashSet<string> KilnInputs = new HashSet<string>();
            public readonly HashSet<string> Resources = new HashSet<string>();
        }

        private static GameData _data;
        private static ObjectDB _dataBuiltFor;

        private static GameData Data
        {
            get
            {
                var db = ObjectDB.instance;
                if (_data != null && _dataBuiltFor == db) return _data;
                if (db == null || ZNetScene.instance == null) return _data ?? new GameData();
                _data = Build(db, ZNetScene.instance);
                _dataBuiltFor = db;
                return _data;
            }
        }

        private static GameData Build(ObjectDB db, ZNetScene scene)
        {
            var data = new GameData();
            foreach (var itemPrefab in db.m_items)
            {
                var drop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
                if (drop != null) data.PrefabByToken[drop.m_itemData.m_shared.m_name] = itemPrefab.name;
            }

            foreach (var prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;
                ScanSmelter(data, prefab.GetComponent<Smelter>());
                ScanCookingStation(data, prefab.GetComponent<CookingStation>());
                ScanFermenter(data, prefab.GetComponent<Fermenter>());
                var fireplace = prefab.GetComponent<Fireplace>();
                if (fireplace != null && fireplace.m_fuelItem != null)
                    AddUse(data, fireplace.m_fuelItem.name, fireplace.m_name, MinorFuelWeight);
                var beehive = prefab.GetComponent<Beehive>();
                if (beehive != null && beehive.m_honeyItem != null) data.Resources.Add(beehive.m_honeyItem.name);

                var piece = prefab.GetComponent<Piece>();
                var plant = prefab.GetComponent<Plant>();
                if (plant != null && piece != null)
                {
                    foreach (var req in piece.m_resources)
                    {
                        if (req.m_resItem == null) continue;
                        data.Seeds.Add(req.m_resItem.name);
                        AddUse(data, req.m_resItem.name, FarmKey, ConversionWeight);
                    }
                    foreach (var grown in plant.m_grownPrefabs)
                    {
                        var pickable = grown != null ? grown.GetComponent<Pickable>() : null;
                        if (pickable == null || pickable.m_itemPrefab == null) continue;
                        data.CropPickables.Add(grown.name);
                        data.Crops.Add(pickable.m_itemPrefab.name);
                    }
                }
                else if (piece != null && piece.m_craftingStation != null)
                {
                    foreach (var req in piece.m_resources)
                        if (req.m_resItem != null) Count(data.PieceUses, req.m_resItem.name, piece.m_craftingStation.m_name);
                }
            }

            foreach (var recipe in db.m_recipes)
            {
                if (recipe == null || !recipe.m_enabled || recipe.m_item == null || recipe.m_craftingStation == null) continue;
                string key = recipe.m_craftingStation.m_name;
                foreach (var req in recipe.m_resources)
                {
                    if (req.m_resItem == null) continue;
                    Count(data.RecipeUses, req.m_resItem.name, key);
                    data.Resources.Add(req.m_resItem.name);
                }
                if (IsEdible(recipe.m_item.m_itemData.m_shared))
                {
                    data.FoodOutputs.Add(recipe.m_item.name);
                    data.KitchenKeys.Add(key);
                }
            }

            FoldCounts(data, data.RecipeUses, RecipeWeightPerUse);
            FoldCounts(data, data.PieceUses, PieceWeightPerUse);

            foreach (var itemPrefab in db.m_items)
            {
                var drop = itemPrefab != null ? itemPrefab.GetComponent<ItemDrop>() : null;
                if (drop != null) data.Profiles[itemPrefab.name] = Profile(data, itemPrefab.name, drop.m_itemData.m_shared);
            }
            return data;
        }

        private static void ScanSmelter(GameData data, Smelter smelter)
        {
            if (smelter == null) return;
            string key = smelter.m_name;
            if (smelter.m_fuelItem != null)
            {
                data.SmelterFuels.Add(smelter.m_fuelItem.name);
                AddUse(data, smelter.m_fuelItem.name, key, SmelterFuelWeight);
            }
            foreach (var conversion in smelter.m_conversion)
            {
                if (conversion.m_from != null)
                {
                    AddUse(data, conversion.m_from.name, key, ConversionWeight);
                    data.Resources.Add(conversion.m_from.name);
                    if (OreStationKeys.Contains(key)) data.OreInputs.Add(conversion.m_from.name);
                    if (key == KilnKey) data.KilnInputs.Add(conversion.m_from.name);
                }
                if (conversion.m_to == null) continue;
                if (OreStationKeys.Contains(key)) data.OreOutputs.Add(conversion.m_to.name);
                if (key == KilnKey) data.SmelterFuels.Add(conversion.m_to.name);
            }
        }

        private static void ScanCookingStation(GameData data, CookingStation station)
        {
            if (station == null) return;
            string key = station.m_name;
            if (station.m_useFuel && station.m_fuelItem != null)
                AddUse(data, station.m_fuelItem.name, key, MinorFuelWeight);
            foreach (var conversion in station.m_conversion)
            {
                if (conversion.m_from == null || conversion.m_to == null) continue;
                AddUse(data, conversion.m_from.name, key, ConversionWeight);
                data.Resources.Add(conversion.m_from.name);
                if (!IsEdible(conversion.m_to.m_itemData.m_shared)) continue;
                data.CookingInputs.Add(conversion.m_from.name);
                data.FoodOutputs.Add(conversion.m_to.name);
                data.KitchenKeys.Add(key);
            }
        }

        private static void ScanFermenter(GameData data, Fermenter fermenter)
        {
            if (fermenter == null) return;
            foreach (var conversion in fermenter.m_conversion)
            {
                if (conversion.m_from == null) continue;
                AddUse(data, conversion.m_from.name, fermenter.m_name, ConversionWeight);
                data.Resources.Add(conversion.m_from.name);
                data.FermenterInputs.Add(conversion.m_from.name);
                if (conversion.m_to != null) data.FermenterOutputs.Add(conversion.m_to.name);
            }
            data.KitchenKeys.Add(fermenter.m_name);
        }

        private static void AddUse(GameData data, string item, string key, float weight)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!data.Uses.TryGetValue(item, out var uses)) data.Uses[item] = uses = new Dictionary<string, float>();
            uses.TryGetValue(key, out float current);
            uses[key] = Mathf.Min(MaxUseWeight, current + weight);
        }

        private static void Count(Dictionary<string, Dictionary<string, int>> counts, string item, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!counts.TryGetValue(item, out var perStation)) counts[item] = perStation = new Dictionary<string, int>();
            perStation.TryGetValue(key, out int n);
            perStation[key] = n + 1;
        }

        private static void FoldCounts(GameData data, Dictionary<string, Dictionary<string, int>> counts, float weightPerUse)
        {
            foreach (var item in counts)
                foreach (var station in item.Value)
                    AddUse(data, item.Key, station.Key, station.Value * weightPerUse);
        }

        private static bool IsEdible(ItemDrop.ItemData.SharedData shared)
            => shared.m_food > 0f || shared.m_foodStamina > 0f || shared.m_foodEitr > 0f;

        private static ItemProfile Profile(GameData data, string prefab, ItemDrop.ItemData.SharedData shared)
        {
            var profile = new ItemProfile();
            if (data.Uses.TryGetValue(prefab, out var uses))
            {
                float best = 0f;
                foreach (var use in uses)
                {
                    profile.Uses[use.Key] = use.Value;
                    if (use.Value > best) { best = use.Value; profile.HomeKey = use.Key; }
                }
            }
            profile.Category = Classify(data, prefab, shared);
            profile.Group = GroupOf(profile);
            return profile;
        }

        private static ItemCategory Classify(GameData data, string prefab, ItemDrop.ItemData.SharedData shared)
        {
            switch (shared.m_itemType)
            {
                case ItemDrop.ItemData.ItemType.Trophy:
                    return ItemCategory.Trophy;
                case ItemDrop.ItemData.ItemType.Ammo:
                case ItemDrop.ItemData.ItemType.AmmoNonEquipable:
                    return ItemCategory.Arrows;
                case ItemDrop.ItemData.ItemType.Helmet:
                case ItemDrop.ItemData.ItemType.Chest:
                case ItemDrop.ItemData.ItemType.Legs:
                case ItemDrop.ItemData.ItemType.Hands:
                case ItemDrop.ItemData.ItemType.Shoulder:
                case ItemDrop.ItemData.ItemType.Shield:
                case ItemDrop.ItemData.ItemType.Utility:
                case ItemDrop.ItemData.ItemType.Trinket:
                case ItemDrop.ItemData.ItemType.Customization:
                    return ItemCategory.Armor;
                case ItemDrop.ItemData.ItemType.OneHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeapon:
                case ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft:
                case ItemDrop.ItemData.ItemType.Bow:
                case ItemDrop.ItemData.ItemType.Tool:
                case ItemDrop.ItemData.ItemType.Torch:
                case ItemDrop.ItemData.ItemType.Attach_Atgeir:
                    return ItemCategory.Weapon;
                case ItemDrop.ItemData.ItemType.Fish:
                    return ItemCategory.RawFish;
            }

            if (prefab == CoinsPrefab) return ItemCategory.Coins;
            if (shared.m_value > 0) return ItemCategory.Gems;
            if (data.FermenterInputs.Contains(prefab)) return ItemCategory.MeadBase;
            if (data.FermenterOutputs.Contains(prefab)) return ItemCategory.FinishedMead;
            if (data.CookingInputs.Contains(prefab)) return ItemCategory.RawMeat;
            if (IsEdible(shared))
            {
                if (data.FoodOutputs.Contains(prefab)) return ItemCategory.CookedFood;
                if (data.Crops.Contains(prefab)) return ItemCategory.Vegetables;
                return data.Resources.Contains(prefab) ? ItemCategory.Berries : ItemCategory.CookedFood;
            }
            if (shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable) return ItemCategory.FinishedMead;
            if (data.Seeds.Contains(prefab)) return ItemCategory.Seeds;
            if (data.OreInputs.Contains(prefab)) return ItemCategory.Ore;
            if (data.OreOutputs.Contains(prefab)) return ItemCategory.MetalBar;
            if (data.SmelterFuels.Contains(prefab)) return ItemCategory.CoalFuel;
            if (data.KilnInputs.Contains(prefab)) return ItemCategory.WoodFuel;
            if (MostlyBuilding(data, prefab)) return ItemCategory.BuildingMaterial;
            return ItemCategory.MiscMaterial;
        }

        private static bool MostlyBuilding(GameData data, string prefab)
        {
            if (!data.PieceUses.TryGetValue(prefab, out var pieces)) return false;
            int pieceCount = 0, recipeCount = 0;
            foreach (var n in pieces.Values) pieceCount += n;
            if (data.RecipeUses.TryGetValue(prefab, out var recipes))
                foreach (var n in recipes.Values) recipeCount += n;
            return pieceCount >= recipeCount;
        }

        /// <summary>Items sharing a group belong in the same chest. Generic materials group by the station that uses
        /// them most, everything else by kind.</summary>
        private static string GroupOf(ItemProfile profile)
        {
            switch (profile.Category)
            {
                case ItemCategory.Coins:
                case ItemCategory.Gems:
                    return "valuables";
                case ItemCategory.Weapon:
                case ItemCategory.Arrows:
                    return "gear";
                case ItemCategory.CookedFood:
                case ItemCategory.FinishedMead:
                    return "provisions";
                case ItemCategory.MiscMaterial:
                    return profile.HomeKey != null ? "use:" + profile.HomeKey : "misc";
                default:
                    return profile.Category.ToString();
            }
        }

        private static ItemProfile ProfileOf(ItemDrop.ItemData item)
        {
            var data = Data;
            string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name
                : data.PrefabByToken.TryGetValue(item.m_shared.m_name, out var byToken) ? byToken : item.m_shared.m_name;
            if (data.Profiles.TryGetValue(prefab, out var profile)) return profile;
            profile = Profile(data, prefab, item.m_shared);
            data.Profiles[prefab] = profile;
            return profile;
        }

        public static ItemCategory GetItemCategory(ItemDrop.ItemData item)
            => item == null || item.m_shared == null ? ItemCategory.Unknown : ProfileOf(item).Category;

        #endregion

        #region Station detection

        private static Collider[] _colliders = new Collider[InitialColliderBuffer];
        private static List<StationInfo> _cachedStations;
        private static Vector3 _cachedCenter;
        private static float _cachedRadius;
        private static float _cachedTime = float.NegativeInfinity;

        /// <summary>Every station, bed and crop within <paramref name="radius"/>; reused for a few seconds per spot
        /// because deposit loops ask once per item.</summary>
        public static List<StationInfo> FindNearbyStations(Vector3 position, float radius)
        {
            if (_cachedStations != null && Time.time - _cachedTime < StationCacheSeconds
                && Mathf.Approximately(radius, _cachedRadius)
                && (position - _cachedCenter).sqrMagnitude < StationCacheMaxDrift * StationCacheMaxDrift)
                return Refreshed(_cachedStations, position);

            var data = Data;
            int count;
            while ((count = Physics.OverlapSphereNonAlloc(position, radius, _colliders)) == _colliders.Length)
                _colliders = new Collider[_colliders.Length * 2];

            var stations = new List<StationInfo>();
            var seen = new HashSet<ZNetView>();
            for (int i = 0; i < count; i++)
            {
                var nview = _colliders[i] != null ? _colliders[i].GetComponentInParent<ZNetView>() : null;
                if (nview == null || !seen.Add(nview)) continue;
                if (!Describe(data, nview.gameObject, out string key, out StationType type)) continue;
                var pos = nview.transform.position;
                stations.Add(new StationInfo { Type = type, Key = key, Object = nview.gameObject, Position = pos, Distance = Vector3.Distance(position, pos) });
            }
            System.Array.Clear(_colliders, 0, count);

            _cachedStations = stations;
            _cachedCenter = position;
            _cachedRadius = radius;
            _cachedTime = Time.time;
            return stations;
        }

        private static List<StationInfo> Refreshed(List<StationInfo> cached, Vector3 position)
        {
            var stations = new List<StationInfo>(cached.Count);
            foreach (var s in cached)
                if (s.Object != null)
                    stations.Add(new StationInfo { Type = s.Type, Key = s.Key, Object = s.Object, Position = s.Position, Distance = Vector3.Distance(position, s.Position) });
            return stations;
        }

        private static bool Describe(GameData data, GameObject root, out string key, out StationType type)
        {
            var smelter = root.GetComponent<Smelter>();
            if (smelter != null) return Station(smelter.m_name, StationType.Smelter, out key, out type);
            var crafting = root.GetComponent<CraftingStation>();
            if (crafting != null) return Station(crafting.m_name, StationType.Other, out key, out type);
            var cooking = root.GetComponent<CookingStation>();
            if (cooking != null) return Station(cooking.m_name, StationType.CookingStation, out key, out type);
            var fermenter = root.GetComponent<Fermenter>();
            if (fermenter != null) return Station(fermenter.m_name, StationType.Fermenter, out key, out type);
            var fireplace = root.GetComponent<Fireplace>();
            if (fireplace != null) return Station(fireplace.m_name, StationType.FireSource, out key, out type);
            if (root.GetComponent<Bed>() != null) return Station(BedKey, StationType.Bed, out key, out type);
            if (root.GetComponent<Plant>() != null || data.CropPickables.Contains(Utils.GetPrefabName(root)))
                return Station(FarmKey, StationType.Farm, out key, out type);
            key = null;
            type = StationType.None;
            return false;
        }

        private static bool Station(string name, StationType fallback, out string key, out StationType type)
        {
            key = name;
            type = KnownStations.TryGetValue(name, out var known) ? known : fallback;
            return true;
        }

        #endregion

        #region Scoring

        private sealed class ChestContext
        {
            public Container Chest;
            public Inventory Inventory;
            public bool IsWardrobe;
            public float Kitchen;
            public float House;
            public int Total;
            public readonly Dictionary<string, int> Groups = new Dictionary<string, int>();
            public readonly Dictionary<string, int> OpenStacks = new Dictionary<string, int>();
            public readonly Dictionary<string, float> Proximity = new Dictionary<string, float>();
            public readonly Dictionary<string, StationInfo> StationByKey = new Dictionary<string, StationInfo>();
        }

        private struct Placement
        {
            public float Score;
            public string Reason;
            public StationInfo Station;
            public bool Matches;
        }

        private static ChestContext Context(Container chest, List<StationInfo> stations, GameData data)
        {
            var ctx = new ChestContext { Chest = chest, Inventory = chest.GetInventory(), IsWardrobe = chest.m_name == WardrobeName };
            foreach (var item in ctx.Inventory.GetAllItems())
            {
                ctx.Total++;
                Increment(ctx.Groups, ProfileOf(item).Group);
                if (IsPartial(item)) Increment(ctx.OpenStacks, StackKey(item));
            }
            var chestPos = chest.transform.position;
            foreach (var station in stations)
            {
                float distance = Vector3.Distance(chestPos, station.Position);
                if (distance > StationReach) continue;
                float closeness = 1f - distance / StationReach;
                if (!ctx.Proximity.TryGetValue(station.Key, out float old) || closeness > old)
                {
                    ctx.Proximity[station.Key] = closeness;
                    ctx.StationByKey[station.Key] = station;
                }
                if (data.KitchenKeys.Contains(station.Key)) ctx.Kitchen = Mathf.Max(ctx.Kitchen, closeness);
                if (station.Key == BedKey) ctx.House = Mathf.Max(ctx.House, closeness);
            }
            return ctx;
        }

        private static List<ChestContext> Contexts(List<Container> chests, List<StationInfo> stations, bool writableOnly)
        {
            var data = Data;
            var contexts = new List<ChestContext>();
            foreach (var chest in chests)
            {
                if (chest == null || chest.GetInventory() == null || InUse(chest)) continue;
                if (writableOnly && !IsWritable(chest)) continue;
                contexts.Add(Context(chest, stations, data));
            }
            return contexts;
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }

        private static bool IsPartial(ItemDrop.ItemData item) => item.m_stack < item.m_shared.m_maxStackSize;

        private static string StackKey(ItemDrop.ItemData item) => item.m_shared.m_name + "|" + item.m_quality;

        /// <summary>How well <paramref name="item"/> fits a chest. For the chest it already sits in
        /// (<paramref name="isCurrentChest"/>) the item does not count toward its own chest.</summary>
        private static Placement Score(ItemProfile profile, ItemDrop.ItemData item, ChestContext ctx, bool isCurrentChest)
        {
            var placement = new Placement();
            ctx.Groups.TryGetValue(profile.Group, out int same);
            ctx.OpenStacks.TryGetValue(StackKey(item), out int openStacks);
            int total = ctx.Total;
            if (isCurrentChest)
            {
                total--;
                same--;
                if (IsPartial(item)) openStacks--;
            }
            int other = total - same;
            bool stackRoom = openStacks > 0;

            if (stackRoom) Add(ref placement, StackBonus, "stacks with the same item");
            if (total == 0) placement.Score += EmptyChestBonus;
            else
            {
                if (same > 0) Add(ref placement, CohesionBonus * same / total, "holds the same kind of item");
                placement.Score -= MixPenalty * other / total;
            }
            placement.Matches = stackRoom || same > 0;

            float bestUse = 0f;
            foreach (var use in profile.Uses)
            {
                if (!ctx.Proximity.TryGetValue(use.Key, out float closeness)) continue;
                float fit = use.Value / MaxUseWeight * closeness;
                if (fit <= bestUse) continue;
                bestUse = fit;
                placement.Station = ctx.StationByKey[use.Key];
            }
            if (bestUse > 0f) Add(ref placement, StationBonus * bestUse, "next to " + Localize(placement.Station.Key));

            switch (profile.Category)
            {
                case ItemCategory.Armor:
                    if (ctx.IsWardrobe) Add(ref placement, WardrobeBonus, "wardrobe");
                    else if (ctx.House > 0f) Add(ref placement, HouseBonus * ctx.House, "by the beds");
                    break;
                case ItemCategory.Weapon:
                case ItemCategory.Arrows:
                    if (ctx.House > 0f) Add(ref placement, GearHouseBonus * ctx.House, "by the beds");
                    break;
                case ItemCategory.CookedFood:
                case ItemCategory.FinishedMead:
                    float provisions = Mathf.Max(ctx.Kitchen, ctx.House);
                    if (provisions > 0f) Add(ref placement, KitchenBonus * provisions, "kitchen and beds");
                    break;
                case ItemCategory.Trophy:
                case ItemCategory.Coins:
                case ItemCategory.Gems:
                    if (same > 0 && other == 0) Add(ref placement, DedicatedBonus, "its own chest");
                    break;
            }
            if (ctx.IsWardrobe && profile.Category != ItemCategory.Armor) placement.Score -= WardrobeMisusePenalty;
            if (ctx.Inventory.GetEmptySlots() < LowSpaceSlots) placement.Score -= LowSpacePenalty;
            return placement;
        }

        private static void Add(ref Placement placement, float amount, string reason)
        {
            placement.Score += amount;
            if (placement.Reason == null) placement.Reason = reason;
        }

        private static string Localize(string key) => Localization.instance != null ? Localization.instance.Localize(key) : key;

        private static bool HasRoom(Inventory inv, ItemDrop.ItemData item)
            => inv.HaveEmptySlot() || inv.FindFreeStackSpace(item.m_shared.m_name, item.m_worldLevel) > 0;

        /// <summary>Picks the chest an item should go into. <paramref name="chests"/> must already be ones the companion
        /// may use (ChestHelper checks privacy and wards); chests someone has open are skipped here.</summary>
        public static ChestRecommendation FindBestChestForItem(
            ItemDrop.ItemData item,
            List<Container> chests,
            Vector3 searchCenter,
            float stationSearchRadius = 20f)
        {
            if (item == null || item.m_shared == null || chests == null || chests.Count == 0) return null;
            var profile = ProfileOf(item);
            var contexts = Contexts(chests, FindNearbyStations(searchCenter, stationSearchRadius), writableOnly: false);

            ChestRecommendation best = null;
            foreach (var ctx in contexts)
            {
                if (!HasRoom(ctx.Inventory, item)) continue;
                var placement = Score(profile, item, ctx, false);
                if (best != null && placement.Score <= best.Score) continue;
                best = new ChestRecommendation
                {
                    Chest = ctx.Chest,
                    Score = placement.Score,
                    Reason = placement.Reason ?? "free space",
                    HasMatchingItems = placement.Matches,
                    NearestRelevantStation = placement.Station
                };
            }
            return best;
        }

        #endregion

        #region Organizing

        /// <summary>
        /// Moves items between chests of a cluster until each sits in the chest that fits it best, then merges partial
        /// stacks. Only chests this client owns and nobody has open are touched.
        /// </summary>
        public static OrganizationResult OrganizeChestCluster(
            List<Container> chests,
            Vector3 clusterCenter,
            float stationSearchRadius = 25f)
        {
            var result = new OrganizationResult();
            if (chests == null || chests.Count < 2) return result;

            var contexts = Contexts(chests, FindNearbyStations(clusterCenter, stationSearchRadius), writableOnly: true);
            if (contexts.Count < 2) return result;

            var moves = new List<(ChestContext from, ItemDrop.ItemData item, ChestContext to, string reason)>();
            foreach (var from in contexts)
            {
                foreach (var item in from.Inventory.GetAllItems())
                {
                    var profile = ProfileOf(item);
                    float current = Score(profile, item, from, true).Score;
                    ChestContext target = null;
                    Placement best = default;
                    foreach (var to in contexts)
                    {
                        if (to == from || !to.Inventory.CanAddItem(item)) continue;
                        var placement = Score(profile, item, to, false);
                        if (target != null && placement.Score <= best.Score) continue;
                        target = to;
                        best = placement;
                    }
                    if (target != null && best.Score > current + MoveThreshold)
                        moves.Add((from, item, target, best.Reason));
                }
            }

            var dirtied = new HashSet<Container>();
            foreach (var (from, item, to, reason) in moves)
            {
                if (!from.Inventory.ContainsItem(item)) continue;
                string name = item.m_shared.m_name;
                if (ChestHelper.MoveItem(from.Inventory, to.Inventory, item, item.m_stack) <= 0) continue;
                result.ItemsMoved++;
                result.Actions.Add($"Moved {name}: {reason}");
                dirtied.Add(from.Chest);
                dirtied.Add(to.Chest);
                if (CompanionIdleBehavior.VerboseLogging)
                    Debug.Log($"[SmartStorage] Moved {name}: {reason}");
            }

            result.StacksConsolidated = ConsolidateStacks(contexts, dirtied);
            result.ChestsAffected = dirtied.Count;
            return result;
        }

        /// <summary>Drains partial stacks of the same item into the chest holding the biggest one.</summary>
        private static int ConsolidateStacks(List<ChestContext> contexts, HashSet<Container> dirtied)
        {
            var partials = new Dictionary<string, List<(ChestContext ctx, ItemDrop.ItemData item)>>();
            foreach (var ctx in contexts)
            {
                foreach (var item in ctx.Inventory.GetAllItems())
                {
                    if (item.m_shared.m_maxStackSize <= 1 || item.m_stack >= item.m_shared.m_maxStackSize) continue;
                    string key = $"{item.m_shared.m_name}|{item.m_quality}|{item.m_worldLevel}";
                    if (!partials.TryGetValue(key, out var list)) partials[key] = list = new List<(ChestContext, ItemDrop.ItemData)>();
                    list.Add((ctx, item));
                }
            }

            int consolidated = 0;
            foreach (var list in partials.Values)
            {
                if (list.Count < 2) continue;
                list.Sort((a, b) => b.item.m_stack.CompareTo(a.item.m_stack));
                var target = list[0].ctx;
                for (int i = list.Count - 1; i > 0; i--)
                {
                    var (ctx, item) = list[i];
                    if (ctx == target) continue;
                    int space = target.Inventory.FindFreeStackSpace(item.m_shared.m_name, item.m_worldLevel);
                    int moved = ChestHelper.MoveItem(ctx.Inventory, target.Inventory, item, Mathf.Min(space, item.m_stack));
                    if (moved <= 0) continue;
                    consolidated += moved;
                    dirtied.Add(ctx.Chest);
                    dirtied.Add(target.Chest);
                }
            }
            return consolidated;
        }

        private static bool InUse(Container chest)
        {
            var zdo = chest.m_nview != null ? chest.m_nview.GetZDO() : null;
            return zdo == null || zdo.GetInt(ZDOVars.s_inUse) == 1;
        }

        private static bool IsWritable(Container chest)
            => chest.m_nview != null && chest.m_nview.IsValid() && chest.m_nview.IsOwner();

        #endregion
    }
}
