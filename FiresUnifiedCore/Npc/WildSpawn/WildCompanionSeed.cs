using System;
using UnityEngine;
using FiresCore.Npc.Archetypes;

namespace FiresCore.Npc.WildSpawn
{
    /// <summary>
    /// Per-prefab configuration for wild-companion rolls (faction, archetype
    /// mask, star weights, squad cohesion). Read-only data block consumed by
    /// <see cref="WildCompanionDresser"/>; never mutated at runtime. Gear comes
    /// from <see cref="CompanionGearTable"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WildCompanionSeed : MonoBehaviour
    {
        public CompanionFaction Faction = CompanionFaction.Neutral;

        /// <summary>Bitmask over <see cref="ArchetypeClass"/> values; ~0 = all allowed.</summary>
        public int AllowedArchetypesMask = ~0;

        /// <summary>Weights for 0/1/2 stars (sum need not be 100).</summary>
        public int[] StarWeights = new[] { 85, 12, 3 };

        public bool EnableGroupCohesion = true;

        /// <summary>Project-wide squad-size ceiling; defends against mistuned EW YAML.</summary>
        public int HardMaxGroupSize = 10;

        private void Awake() { }

        public bool IsArchetypeAllowed(ArchetypeClass a) =>
            (AllowedArchetypesMask & (1 << (int)a)) != 0;
    }
}
