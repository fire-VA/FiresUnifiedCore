using System;
using System.Collections.Generic;
using FiresCore.Npc.Archetypes;
using Slot = FiresCore.Npc.CompanionInventory.EquipmentSlot;

namespace FiresCore.Npc.WildSpawn
{
    /// <summary>
    /// Hard-coded gear pools keyed by <c>(ArchetypeClass, tier)</c> for the
    /// wild-companion dresser. Tier indices follow the biome mapping:
    /// 0 Meadows, 1 BlackForest, 2 Swamp, 3 Mountain, 4 Plains, 5 Mistlands,
    /// 6 AshLands, 7 DeepNorth (reserved). Higher tiers populate as content ships.
    /// </summary>
    internal static class CompanionGearTables
    {
        public static Dictionary<Slot, string> Roll(ArchetypeClass archetype,
            int[] allowedTiers, Random rng)
        {
            var result = new Dictionary<Slot, string>();
            if (allowedTiers == null || allowedTiers.Length == 0) return result;

            int tier = allowedTiers[rng.Next(allowedTiers.Length)];

            if (!_tables.TryGetValue((archetype, tier), out var slotPools))
                return result;

            foreach (var kv in slotPools)
            {
                if (kv.Value == null || kv.Value.Length == 0) continue;
                string prefab = kv.Value[rng.Next(kv.Value.Length)];
                if (!string.IsNullOrEmpty(prefab))
                    result[kv.Key] = prefab;
            }
            return result;
        }

        private static readonly Dictionary<(ArchetypeClass, int), Dictionary<Slot, string[]>> _tables =
            new Dictionary<(ArchetypeClass, int), Dictionary<Slot, string[]>>
        {
            // Tier 0 : Meadows

            [(ArchetypeClass.Tank, 0)] = new Dictionary<Slot, string[]>
            {
                [Slot.RightHand] = new[] { "AxeFlint", "Club" },
                [Slot.LeftHand]  = new[] { "ShieldWood", "ShieldWoodTower" },
                [Slot.Chest]     = new[] { "ArmorLeatherChest", "ArmorRagsChest" },
                [Slot.Legs]      = new[] { "ArmorLeatherLegs",  "ArmorRagsLegs"  },
                [Slot.Helmet]    = new[] { "HelmetLeather" },
            },

            [(ArchetypeClass.Paladin, 0)] = new Dictionary<Slot, string[]>
            {
                [Slot.RightHand] = new[] { "MaceBronze" /* downgrade fallback */, "Club" },
                [Slot.LeftHand]  = new[] { "ShieldWood", "ShieldBoneTower" },
                [Slot.Chest]     = new[] { "ArmorLeatherChest" },
                [Slot.Legs]      = new[] { "ArmorLeatherLegs"  },
                [Slot.Helmet]    = new[] { "HelmetLeather" },
            },

            [(ArchetypeClass.Berserker, 0)] = new Dictionary<Slot, string[]>
            {
                [Slot.RightHand] = new[] { "AxeFlint", "Club" },
                [Slot.Chest]     = new[] { "ArmorLeatherChest", "ArmorRagsChest" },
                [Slot.Legs]      = new[] { "ArmorLeatherLegs" },
                [Slot.Helmet]    = new[] { "HelmetLeather" },
            },

            [(ArchetypeClass.Rogue, 0)] = new Dictionary<Slot, string[]>
            {
                [Slot.RightHand] = new[] { "KnifeFlint", "KnifeChitin" /* fallback */ },
                [Slot.Chest]     = new[] { "ArmorLeatherChest", "ArmorRagsChest" },
                [Slot.Legs]      = new[] { "ArmorLeatherLegs" },
                [Slot.Helmet]    = new[] { "HelmetLeather" },
            },

            [(ArchetypeClass.Monk, 0)] = new Dictionary<Slot, string[]>
            {
                [Slot.RightHand] = new[] { "Club" },
                [Slot.Chest]     = new[] { "ArmorRagsChest", "ArmorLeatherChest" },
                [Slot.Legs]      = new[] { "ArmorRagsLegs",  "ArmorLeatherLegs"  },
            },

            [(ArchetypeClass.Ranger, 0)] = new Dictionary<Slot, string[]>
            {
                [Slot.RightHand] = new[] { "Bow" },
                [Slot.Chest]     = new[] { "ArmorLeatherChest" },
                [Slot.Legs]      = new[] { "ArmorLeatherLegs"  },
                [Slot.Helmet]    = new[] { "HelmetLeather"     },
            },

            [(ArchetypeClass.Mage, 0)] = new Dictionary<Slot, string[]>
            {
                [Slot.Chest]     = new[] { "ArmorRagsChest" },
                [Slot.Legs]      = new[] { "ArmorRagsLegs"  },
            },

            [(ArchetypeClass.Healer, 0)] = new Dictionary<Slot, string[]>
            {
                [Slot.Chest]     = new[] { "ArmorRagsChest" },
                [Slot.Legs]      = new[] { "ArmorRagsLegs"  },
            },
        };

        /// <summary>Diagnostic: number of populated (archetype, tier) tables.</summary>
        public static int PopulatedTableCount => _tables.Count;
    }
}
