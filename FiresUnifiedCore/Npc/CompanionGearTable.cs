using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Npc.Archetypes;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Vanilla 1.0 gear by biome tier for companion and NPC loadouts: the curated list in
    /// Tools\VALHEIM_10_GEAR_BY_BIOME.md (wiki, every ID checked against the 1.0 ObjectDB). The wooden training weapons,
    /// the Fallen Warrior and Shadow Person copies, cheat items and monster attacks are simply not in it. Kits are built
    /// per archetype, so the gear matches the archetype ArchetypeController reads back from what is equipped.
    /// </summary>
    public static class CompanionGearTable
    {
        public const int MaxTier = 7;

        public enum ArmorClass { Light, Medium, Heavy, Mage }

        private const int LuckyTierUpPercent = 5;
        private const int OneTierDownPercent = 25;
        private const int ArmorTierFallback = 1;
        private const int MinAmmo = 20;
        private const int MaxAmmoExclusive = 51;
        private const int MinFoodStack = 1;
        private const int MaxFoodStackExclusive = 5;
        private const string CauldronPrefab = "piece_cauldron";
        private const string UncookedSuffix = "Uncooked";
        private const string MageCape = "CapeDeepNorthMage";

        private static readonly string[][] Weapons =
        {
            new[] { "Club", "AxeStone", "AxeFlint", "KnifeFlint", "SpearFlint", "KnifeButcher", "SledgeStagbreaker", "AxeEarly", "Bow" },
            new[] { "SwordBronze", "MaceBronze", "AxeBronze", "SpearBronze", "KnifeCopper", "AtgeirBronze", "FistBjornClaw", "BowFineWood" },
            new[] { "SwordIron", "MaceIron", "AxeIron", "SpearElderbark", "AtgeirIron", "SledgeIron", "Battleaxe", "BowHuntsman" },
            new[] { "SwordSilver", "MaceSilver", "KnifeSilver", "SpearWolfFang", "BattleaxeCrystal", "FistFenrirClaw", "BowDraugrFang", "KnifeChitin", "SpearChitin" },
            new[] { "SwordBlackmetal", "AxeBlackMetal", "KnifeBlackMetal", "MaceNeedle", "AtgeirBlackmetal", "BattleaxeBlackmetal", "KnifeSkollAndHati", "FistBjornUndeadClaw" },
            new[]
            {
                "SwordMistwalker", "AxeJotunBane", "SpearCarapace", "THSwordKrom", "AtgeirHimminAfl", "BattleaxeSkullSplittur", "SledgeDemolisher",
                "BowSpineSnap", "CrossbowArbalest", "StaffFireball", "StaffIceShards", "StaffShield", "StaffSkeleton",
            },
            new[]
            {
                "SwordNiedhogg", "SwordNiedhoggBlood", "SwordNiedhoggLightning", "SwordNiedhoggNature", "SwordDyrnwyn",
                "MaceEldner", "MaceEldnerBlood", "MaceEldnerLightning", "MaceEldnerNature",
                "SpearSplitner", "SpearSplitner_Blood", "SpearSplitner_Lightning", "SpearSplitner_Nature",
                "THSwordSlayer", "THSwordSlayerBlood", "THSwordSlayerLightning", "THSwordSlayerNature",
                "AxeBerzerkr", "AxeBerzerkrBlood", "AxeBerzerkrLightning", "AxeBerzerkrNature",
                "BowAshlands", "BowAshlandsBlood", "BowAshlandsStorm", "BowAshlandsRoot",
                "CrossbowRipper", "CrossbowRipperBlood", "CrossbowRipperLightning", "CrossbowRipperNature",
                "StaffClusterbomb", "StaffGreenRoots", "StaffLightning", "StaffRedTroll",
            },
            new[]
            {
                "SwordGold", "SwordGold_FrostFire", "SwordGold_BloodLightning", "AxeGold", "AxeGold_FrostFire", "AxeGold_BloodLightning",
                "MaceGold", "MaceGold_FrostFire", "MaceGold_BloodLightning", "SpearGold", "SpearGold_FrostFire", "SpearGold_BloodLightning",
                "KnifeGold", "KnifeGold_FrostFire", "KnifeGold_BloodLightning", "THSwordGold", "THSwordGold_FrostFire", "THSwordGold_BloodLightning",
                "BattleaxeGold", "BattleaxeGold_FrostFire", "BattleaxeGold_BloodLightning", "SledgeGold", "SledgeGold_FrostFire", "SledgeGold_BloodLightning",
                "AtgeirGold", "AtgeirGold_FrostFire", "AtgeirGold_BloodLightning", "FistGold", "FistGold_FrostFire", "FistGold_BloodLightning",
                "BowGold", "BowGold_FrostFire", "BowGold_BloodLightning", "CrossbowGold", "CrossbowGold_FrostFire", "CrossbowGold_BloodLightning",
                "StaffOrbofAhri", "StaffSpiritCaller", "StaffFrostOrbs", "StaffThunderBlood",
            },
        };

        private static readonly string[][] Shields =
        {
            new[] { "ShieldWood", "ShieldWoodTower" },
            new[] { "ShieldBronzeBuckler", "ShieldBoneTower" },
            new[] { "ShieldBanded", "ShieldIronBuckler", "ShieldIronTower", "ShieldRoots" },
            new[] { "ShieldSilver", "ShieldSerpentscale" },
            new[] { "ShieldBlackmetal", "ShieldBlackmetalTower" },
            new[] { "ShieldCarapace", "ShieldCarapaceBuckler" },
            new[] { "ShieldFlametal", "ShieldFlametalTower" },
            new[] { "ShieldGold", "ShieldGoldBuckler", "ShieldGoldTower" },
        };

        private static readonly string[][] Capes =
        {
            new[] { "CapeDeerHide" },
            new[] { "CapeTrollHide" },
            new string[0],
            new[] { "CapeWolf" },
            new[] { "CapeLinen", "CapeLox" },
            new[] { "CapeFeather" },
            new[] { "CapeAsh", "CapeAsksvin" },
            new[] { "CapeDeepNorth", MageCape },
        };

        private static readonly string[][] Arrows =
        {
            new[] { "ArrowWood", "ArrowFlint", "ArrowFire" },
            new[] { "ArrowBronze" },
            new[] { "ArrowIron" },
            new[] { "ArrowSilver", "ArrowObsidian", "ArrowFrost", "ArrowPoison" },
            new[] { "ArrowNeedle" },
            new[] { "ArrowCarapace" },
            new[] { "ArrowCharred" },
            new[] { "ArrowBloodGold" },
        };

        private static readonly string[][] Bolts =
        {
            new string[0], new string[0], new string[0], new string[0], new string[0],
            new[] { "BoltBone", "BoltIron", "BoltBlackmetal", "BoltCarapace" },
            new[] { "BoltCharred" },
            new[] { "BoltBloodGold" },
        };

        // Cooked at a cooking station, so no recipe to read a tier from. Cauldron dishes are tiered by station level.
        private static readonly string[][] StationFoods =
        {
            new[] { "CookedMeat", "CookedDeerMeat", "NeckTailGrilled", "Raspberry", "Honey", "Mushroom" },
            new[] { "Blueberries", "CookedBjornMeat" },
            new[] { "SerpentMeatCooked" },
            new[] { "CookedWolfMeat" },
            new[] { "CookedLoxMeat" },
            new[] { "CookedChickenMeat", "CookedHareMeat" },
            new[] { "CookedAsksvinMeat", "CookedVoltureMeat", "CookedBoneMawSerpentMeat" },
            new string[0],
        };

        private static readonly ArmorSet[] ArmorSets =
        {
            new ArmorSet(0, ArmorClass.Light, "HelmetLeather", "ArmorLeatherChest", "ArmorLeatherLegs"),
            new ArmorSet(0, ArmorClass.Light, null, "ArmorRagsChest", "ArmorRagsLegs"),
            new ArmorSet(1, ArmorClass.Heavy, "HelmetBronze", "ArmorBronzeChest", "ArmorBronzeLegs"),
            new ArmorSet(1, ArmorClass.Light, "HelmetTrollLeather", "ArmorTrollLeatherChest", "ArmorTrollLeatherLegs"),
            new ArmorSet(1, ArmorClass.Medium, "HelmetBerserkerHood", "ArmorBerserkerChest", "ArmorBerserkerLegs"),
            new ArmorSet(2, ArmorClass.Heavy, "HelmetIron", "ArmorIronChest", "ArmorIronLegs"),
            new ArmorSet(2, ArmorClass.Medium, "HelmetRoot", "ArmorRootChest", "ArmorRootLegs"),
            new ArmorSet(3, ArmorClass.Heavy, "HelmetDrake", "ArmorWolfChest", "ArmorWolfLegs"),
            new ArmorSet(3, ArmorClass.Light, "HelmetFenring", "ArmorFenringChest", "ArmorFenringLegs"),
            new ArmorSet(4, ArmorClass.Heavy, "HelmetPadded", "ArmorPaddedCuirass", "ArmorPaddedGreaves"),
            new ArmorSet(4, ArmorClass.Medium, "HelmetLox", "ArmorLoxChest", "ArmorLoxLegs"),
            new ArmorSet(4, ArmorClass.Medium, "HelmetBerserkerUndead", "ArmorBerserkerUndeadChest", "ArmorBerserkerUndeadLegs"),
            new ArmorSet(5, ArmorClass.Heavy, "HelmetCarapace", "ArmorCarapaceChest", "ArmorCarapaceLegs"),
            new ArmorSet(5, ArmorClass.Mage, "HelmetMage", "ArmorMageChest", "ArmorMageLegs"),
            new ArmorSet(6, ArmorClass.Heavy, "HelmetFlametal", "ArmorFlametalChest", "ArmorFlametalLegs"),
            new ArmorSet(6, ArmorClass.Medium, "HelmetAshlandsMediumHood", "ArmorAshlandsMediumChest", "ArmorAshlandsMediumlegs"),
            new ArmorSet(6, ArmorClass.Mage, "HelmetMage_Ashlands", "ArmorMageChest_Ashlands", "ArmorMageLegs_Ashlands"),
            new ArmorSet(7, ArmorClass.Heavy, "HelmetDNHeavy", "ArmorDeepNorthHeavyChest", "ArmorDeepNorthHeavylegs"),
            new ArmorSet(7, ArmorClass.Medium, "HelmetDNMediumHood", "ArmorDeepNorthMediumChest", "ArmorDeepNorthMediumlegs"),
            new ArmorSet(7, ArmorClass.Mage, "HelmetDNMage", "ArmorDeepNorthMageChest", "ArmorDeepNorthMagelegs"),
        };

        private static readonly ArchetypeClass[] KitArchetypes =
        {
            ArchetypeClass.Tank, ArchetypeClass.Paladin, ArchetypeClass.Berserker, ArchetypeClass.Rogue,
            ArchetypeClass.Monk, ArchetypeClass.Ranger, ArchetypeClass.Mage, ArchetypeClass.Healer,
        };

        private sealed class ArmorSet
        {
            public readonly int Tier;
            public readonly ArmorClass Class;
            public readonly string Helmet, Chest, Legs;

            public ArmorSet(int tier, ArmorClass armorClass, string helmet, string chest, string legs)
            {
                Tier = tier;
                Class = armorClass;
                Helmet = helmet;
                Chest = chest;
                Legs = legs;
            }
        }

        private static readonly Dictionary<string, int> TierByPrefab = new Dictionary<string, int>();
        private static readonly List<string>[] FoodsByTier = Enumerable.Range(0, MaxTier + 1).Select(_ => new List<string>()).ToArray();
        private static readonly HashSet<string> Missing = new HashSet<string>();
        private static bool _prepared;

        public static bool IsPrepared => _prepared;

        /// <summary>Checks the curated list against the live ObjectDB once the real item set is loaded.</summary>
        public static bool Prepare()
        {
            if (_prepared) return true;
            var objectDb = ObjectDB.instance;
            if (objectDb == null || objectDb.m_items == null || !IsWorldItemSet(objectDb)) return false;

            TierByPrefab.Clear();
            Missing.Clear();
            RegisterTiered(Weapons);
            RegisterTiered(Shields);
            RegisterTiered(Capes);
            RegisterTiered(Arrows);
            RegisterTiered(Bolts);
            foreach (var set in ArmorSets)
            {
                Register(set.Helmet, set.Tier);
                Register(set.Chest, set.Tier);
                Register(set.Legs, set.Tier);
            }
            BuildFoodTiers(objectDb);

            _prepared = true;
            Debug.Log($"[CompanionGearTable] {TierByPrefab.Count} gear items and {FoodsByTier.Sum(f => f.Count)} foods ready across tiers 0-{MaxTier}"
                + (Missing.Count > 0 ? $"; not in ObjectDB (renamed or removed?): {string.Join(", ", Missing)}" : ""));
            return true;
        }

        public static void Reset()
        {
            _prepared = false;
            TierByPrefab.Clear();
            foreach (var foods in FoodsByTier) foods.Clear();
        }

        public static int BiomeTier(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows: return 0;
                case Heightmap.Biome.BlackForest: return 1;
                case Heightmap.Biome.Swamp: return 2;
                case Heightmap.Biome.Mountain:
                case Heightmap.Biome.Ocean: return 3;
                case Heightmap.Biome.Plains: return 4;
                case Heightmap.Biome.Mistlands: return 5;
                case Heightmap.Biome.AshLands: return 6;
                case Heightmap.Biome.DeepNorth: return 7;
                default: return 2;
            }
        }

        public static bool TryGetTier(string prefabName, out int tier)
        {
            tier = 0;
            return !string.IsNullOrEmpty(prefabName) && TierByPrefab.TryGetValue(prefabName, out tier);
        }

        /// <summary>Every curated item of one ItemDrop type, all tiers.</summary>
        public static List<string> AllOfType(ItemDrop.ItemData.ItemType itemType)
        {
            return TierByPrefab.Keys.Where(name => Shared(name)?.m_itemType == itemType).ToList();
        }

        /// <summary>Mostly the biome's tier, sometimes one below, and now and then one above for a lucky companion.</summary>
        public static int RollKitTier(int biomeTier, System.Random rng)
        {
            int roll = rng.Next(100);
            if (roll < LuckyTierUpPercent) return Mathf.Min(biomeTier + 1, MaxTier);
            if (roll < LuckyTierUpPercent + OneTierDownPercent) return Mathf.Max(biomeTier - 1, 0);
            return biomeTier;
        }

        /// <summary>True when the archetype's defining weapon exists at or below the tier (staves only exist from Mistlands on).</summary>
        public static bool HasKitAtOrBelow(ArchetypeClass archetype, int tier)
        {
            for (int t = Mathf.Min(tier, MaxTier); t >= 0; t--)
                if (Weapons[t].Any(name => IsPrimaryFor(archetype, name))) return true;
            return false;
        }

        public static ArchetypeClass[] ArchetypesWithKitAtOrBelow(IEnumerable<ArchetypeClass> pool, int tier)
        {
            var available = pool.Where(a => HasKitAtOrBelow(a, tier)).ToArray();
            return available.Length > 0 ? available : new[] { ArchetypeClass.Berserker };
        }

        public static ArchetypeClass RollArchetype(int biomeTier, System.Random rng)
        {
            var pool = ArchetypesWithKitAtOrBelow(KitArchetypes, biomeTier);
            return pool[rng.Next(pool.Length)];
        }

        public static CompanionKit BuildKit(ArchetypeClass archetype, int kitTier, System.Random rng, CompanionKitChances chances)
        {
            var kit = new CompanionKit { Archetype = archetype, Tier = kitTier };

            string primary = PickPrimary(archetype, kitTier, rng);
            if (primary != null)
            {
                if (IsLeftHanded(primary)) kit.LeftHand = primary;
                else kit.RightHand = primary;
            }

            if ((archetype == ArchetypeClass.Tank || archetype == ArchetypeClass.Paladin) && primary != null)
                kit.LeftHand = PickNearest(Shields, kitTier, rng, _ => true);

            AddSidearm(kit, archetype, kitTier, rng, chances);
            AddAmmo(kit, rng);
            AddArmor(kit, archetype, kitTier, rng, chances);
            AddFood(kit, kitTier, rng, chances);
            return kit;
        }

        public static bool IsPrimaryFor(ArchetypeClass archetype, string prefabName)
        {
            var shared = Shared(prefabName);
            if (shared == null) return false;
            var skill = shared.m_skillType;
            bool oneHanded = shared.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon;
            bool twoHanded = shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon;
            switch (archetype)
            {
                case ArchetypeClass.Tank:
                    return oneHanded && (skill == Skills.SkillType.Swords || skill == Skills.SkillType.Axes || skill == Skills.SkillType.Spears);
                case ArchetypeClass.Paladin:
                    return oneHanded && skill == Skills.SkillType.Clubs;
                case ArchetypeClass.Berserker:
                    return twoHanded && (skill == Skills.SkillType.Swords || skill == Skills.SkillType.Axes || skill == Skills.SkillType.Polearms);
                case ArchetypeClass.Rogue:
                    return skill == Skills.SkillType.Knives;
                case ArchetypeClass.Monk:
                    return skill == Skills.SkillType.Unarmed || (oneHanded && skill == Skills.SkillType.Clubs);
                case ArchetypeClass.Ranger:
                    return shared.m_itemType == ItemDrop.ItemData.ItemType.Bow;
                case ArchetypeClass.Mage:
                    return skill == Skills.SkillType.ElementalMagic;
                case ArchetypeClass.Healer:
                    return skill == Skills.SkillType.BloodMagic;
                default:
                    return false;
            }
        }

        private static string PickPrimary(ArchetypeClass archetype, int kitTier, System.Random rng)
        {
            // A Monk fights with fists; a club is only the Meadows fallback before any fist weapon exists.
            if (archetype == ArchetypeClass.Monk)
                return PickNearest(Weapons, kitTier, rng, name => Shared(name)?.m_skillType == Skills.SkillType.Unarmed, maxTierAbove: 0)
                    ?? PickNearest(Weapons, kitTier, rng, name => IsPrimaryFor(ArchetypeClass.Monk, name));
            return PickNearest(Weapons, kitTier, rng, name => IsPrimaryFor(archetype, name));
        }

        private static void AddSidearm(CompanionKit kit, ArchetypeClass archetype, int kitTier, System.Random rng, CompanionKitChances chances)
        {
            switch (archetype)
            {
                case ArchetypeClass.Ranger:
                    if (rng.NextDouble() < chances.Secondary)
                        kit.RightBack = PickNearest(Weapons, kitTier, rng, name => IsOneHandedMelee(name));
                    break;
                case ArchetypeClass.Berserker:
                case ArchetypeClass.Mage:
                case ArchetypeClass.Healer:
                    if (rng.NextDouble() < chances.Secondary * 0.5)
                        kit.RightBack = PickNearest(Weapons, kitTier, rng, name => IsOneHandedMelee(name)
                            && (archetype == ArchetypeClass.Berserker || Shared(name)?.m_skillType == Skills.SkillType.Knives));
                    break;
                case ArchetypeClass.Rogue:
                    if (rng.NextDouble() < chances.Tertiary)
                        kit.LeftBack = PickNearest(Weapons, kitTier, rng, name => Shared(name)?.m_skillType == Skills.SkillType.Bows);
                    break;
            }
        }

        private static void AddAmmo(CompanionKit kit, System.Random rng)
        {
            string bow = IsBow(kit.LeftHand) ? kit.LeftHand : IsBow(kit.LeftBack) ? kit.LeftBack : null;
            if (bow == null) return;
            bool crossbow = Shared(bow).m_skillType == Skills.SkillType.Crossbows;
            TryGetTier(bow, out int bowTier);
            kit.Ammo = PickNearest(crossbow ? Bolts : Arrows, bowTier, rng, _ => true);
            kit.AmmoCount = rng.Next(MinAmmo, MaxAmmoExclusive);
        }

        private static void AddArmor(CompanionKit kit, ArchetypeClass archetype, int kitTier, System.Random rng, CompanionKitChances chances)
        {
            var set = PickArmorSet(archetype, kitTier, rng);
            if (set != null)
            {
                if (set.Helmet != null && rng.NextDouble() < chances.Helmet) kit.Helmet = Known(set.Helmet);
                if (rng.NextDouble() < chances.Chest) kit.Chest = Known(set.Chest);
                if (rng.NextDouble() < chances.Legs) kit.Legs = Known(set.Legs);
            }
            if (rng.NextDouble() < chances.Cape)
            {
                bool mage = set != null && set.Class == ArmorClass.Mage;
                kit.Shoulder = PickNearest(Capes, kitTier, rng, name => mage || name != MageCape);
            }
        }

        private static ArmorSet PickArmorSet(ArchetypeClass archetype, int kitTier, System.Random rng)
        {
            foreach (var armorClass in PreferredArmor(archetype))
            {
                for (int t = kitTier; t >= Mathf.Max(0, kitTier - ArmorTierFallback); t--)
                {
                    var sets = ArmorSets.Where(s => s.Tier == t && s.Class == armorClass && Known(s.Chest) != null).ToList();
                    if (sets.Count > 0) return sets[rng.Next(sets.Count)];
                }
            }
            var any = ArmorSets.Where(s => s.Tier == kitTier && Known(s.Chest) != null).ToList();
            return any.Count > 0 ? any[rng.Next(any.Count)] : null;
        }

        private static ArmorClass[] PreferredArmor(ArchetypeClass archetype)
        {
            switch (archetype)
            {
                case ArchetypeClass.Tank:
                case ArchetypeClass.Paladin:
                    return new[] { ArmorClass.Heavy, ArmorClass.Medium, ArmorClass.Light };
                case ArchetypeClass.Rogue:
                case ArchetypeClass.Monk:
                    return new[] { ArmorClass.Light, ArmorClass.Medium, ArmorClass.Heavy };
                case ArchetypeClass.Ranger:
                    return new[] { ArmorClass.Medium, ArmorClass.Light, ArmorClass.Heavy };
                case ArchetypeClass.Mage:
                case ArchetypeClass.Healer:
                    return new[] { ArmorClass.Mage, ArmorClass.Light, ArmorClass.Medium };
                default:
                    return new[] { ArmorClass.Medium, ArmorClass.Heavy, ArmorClass.Light };
            }
        }

        private static void AddFood(CompanionKit kit, int kitTier, System.Random rng, CompanionKitChances chances)
        {
            int count = rng.Next(chances.MinFood, chances.MaxFood + 1);
            for (int i = 0; i < count; i++)
            {
                var foods = FoodsByTier[kitTier].Count > 0 ? FoodsByTier[kitTier] : FoodsByTier[Mathf.Max(0, kitTier - 1)];
                if (foods.Count == 0) return;
                kit.Food.Add(new KeyValuePair<string, int>(foods[rng.Next(foods.Count)], rng.Next(MinFoodStack, MaxFoodStackExclusive)));
            }
        }

        /// <summary>
        /// An item at the tier, else the closest tier below, else the lowest tier above (up to maxTierAbove steps), so
        /// a Mistlands staff can still reach a Plains mage whose kit rolled one tier down.
        /// </summary>
        private static string PickNearest(string[][] table, int tier, System.Random rng, Func<string, bool> accept, int maxTierAbove = 1)
        {
            for (int t = Mathf.Min(tier, MaxTier); t >= 0; t--)
            {
                var hit = PickAt(table, t, rng, accept);
                if (hit != null) return hit;
            }
            for (int t = tier + 1; t <= Mathf.Min(tier + maxTierAbove, MaxTier); t++)
            {
                var hit = PickAt(table, t, rng, accept);
                if (hit != null) return hit;
            }
            return null;
        }

        private static string PickAt(string[][] table, int tier, System.Random rng, Func<string, bool> accept)
        {
            if (tier < 0 || tier >= table.Length) return null;
            var candidates = table[tier].Where(name => Known(name) != null && accept(name)).ToList();
            return candidates.Count > 0 ? candidates[rng.Next(candidates.Count)] : null;
        }

        private static bool IsOneHandedMelee(string prefabName)
        {
            var shared = Shared(prefabName);
            return shared != null && shared.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon
                && shared.m_skillType != Skills.SkillType.ElementalMagic && shared.m_skillType != Skills.SkillType.BloodMagic;
        }

        private static bool IsBow(string prefabName) => Shared(prefabName)?.m_itemType == ItemDrop.ItemData.ItemType.Bow;

        public static bool IsLeftHanded(string prefabName)
        {
            var type = Shared(prefabName)?.m_itemType;
            return type == ItemDrop.ItemData.ItemType.Bow || type == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft;
        }

        private static string Known(string prefabName) => prefabName != null && TierByPrefab.ContainsKey(prefabName) ? prefabName : null;

        private static ItemDrop.ItemData.SharedData Shared(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName) || ObjectDB.instance == null) return null;
            var prefab = ObjectDB.instance.GetItemPrefab(prefabName);
            return prefab != null && prefab.TryGetComponent(out ItemDrop drop) ? drop.m_itemData?.m_shared : null;
        }

        private static void RegisterTiered(string[][] table)
        {
            for (int tier = 0; tier < table.Length; tier++)
                foreach (var name in table[tier]) Register(name, tier);
        }

        private static void Register(string prefabName, int tier)
        {
            if (prefabName == null) return;
            if (ObjectDB.instance.GetItemPrefab(prefabName) == null) { Missing.Add(prefabName); return; }
            TierByPrefab[prefabName] = tier;
        }

        private static void BuildFoodTiers(ObjectDB objectDb)
        {
            foreach (var foods in FoodsByTier) foods.Clear();
            for (int tier = 0; tier < StationFoods.Length; tier++)
                foreach (var name in StationFoods[tier])
                    if (IsFood(Shared(name))) FoodsByTier[tier].Add(name);
                    else Missing.Add(name);

            foreach (var recipe in objectDb.m_recipes)
            {
                if (recipe == null || !recipe.m_enabled || recipe.m_item == null || recipe.m_craftingStation == null) continue;
                if (recipe.m_craftingStation.gameObject.name != CauldronPrefab) continue;
                string name = recipe.m_item.gameObject.name;
                if (name.EndsWith(UncookedSuffix, StringComparison.Ordinal) || !IsFood(recipe.m_item.m_itemData.m_shared)) continue;
                int tier = Mathf.Clamp(recipe.m_minStationLevel, 1, MaxTier);
                if (!FoodsByTier[tier].Contains(name)) FoodsByTier[tier].Add(name);
            }
        }

        private static bool IsFood(ItemDrop.ItemData.SharedData shared)
        {
            return shared != null && shared.m_itemType == ItemDrop.ItemData.ItemType.Consumable
                && (shared.m_food > 0f || shared.m_foodStamina > 0f || shared.m_foodEitr > 0f);
        }

        // ObjectDB.Awake also runs in the start scene with only a handful of menu items.
        private static bool IsWorldItemSet(ObjectDB objectDb) => objectDb.GetItemPrefab("SwordBronze") != null;
    }

    public sealed class CompanionKitChances
    {
        public float Helmet, Chest, Legs, Cape, Secondary, Tertiary;
        public int MinFood, MaxFood;
    }

    public sealed class CompanionKit
    {
        public ArchetypeClass Archetype;
        public int Tier;
        public string RightHand, LeftHand, RightBack, LeftBack, Helmet, Chest, Legs, Shoulder, Ammo;
        public int AmmoCount;
        public readonly List<KeyValuePair<string, int>> Food = new List<KeyValuePair<string, int>>();
    }
}
