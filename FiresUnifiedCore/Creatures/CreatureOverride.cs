using System;
using System.Collections.Generic;

namespace FiresCore.Creatures
{
    /// <summary>One loot line of a <see cref="CreatureOverride"/>: CharacterDrop.Drop with the prefab named.</summary>
    [Serializable]
    public class CreatureDropOverride
    {
        public string Item;
        public int AmountMin = 1;
        public int AmountMax = 1;
        public float Chance = 1f;
        public bool LevelMultiplier = true;
        public bool OnePerPlayer;
        public bool DontScale;

        public CreatureDropOverride Clone() => (CreatureDropOverride)MemberwiseClone();
    }

    /// <summary>
    /// A server-authored rule set for one creature prefab (every instance of it). <see cref="Enabled"/> is the master
    /// switch: off hands the creature back to vanilla or its owning mod while keeping the values for later. Each
    /// section applies only when its own Override flag is on, so a creature can change stats without touching loot.
    /// Stars are levels minus one, the way players count them.
    /// </summary>
    [Serializable]
    public class CreatureOverride
    {
        public string Name;
        public bool Enabled = true;

        public bool OverrideDrops;
        public List<CreatureDropOverride> Drops = new List<CreatureDropOverride>();

        public bool OverrideStars;
        public int MinStars;
        public int MaxStars = 2;
        public float StarChance = 0.1f;

        public bool OverrideStats;

        /// <summary>
        /// Absolute base health for the UNSTARRED creature, replacing the prefab's own value. 0 means unset, in
        /// which case <see cref="HealthMultiplier"/> applies instead. World-level scaling still multiplies on
        /// top, exactly as vanilla does to the prefab value, and stars scale from here as usual.
        /// </summary>
        public float HealthValue;

        /// <summary>
        /// What one star multiplies health by; each further star adds the same again, so 2.0 reproduces
        /// vanilla (x1 at 0 stars, x2 at one, x3 at two). Formula: 1 + (level - 1) * (value - 1).
        /// </summary>
        public float HealthPerStar = 2f;

        /// <summary>
        /// What one star multiplies damage by, same formula. 1.5 reproduces vanilla's
        /// <c>Attack.GetLevelDamageFactor</c>: x1, x1.5, x2.
        /// </summary>
        public float DamagePerStar = 1.5f;

        /// <summary>Flat multipliers kept so older saved rules still mean what they meant. The editor sets the
        /// value and per-star fields above instead, and leaves these at 1.</summary>
        public float HealthMultiplier = 1f;

        public float DamageMultiplier = 1f;

        public CreatureOverride Clone()
        {
            var copy = (CreatureOverride)MemberwiseClone();
            copy.Drops = Drops.ConvertAll(drop => drop.Clone());
            return copy;
        }
    }

    [Serializable]
    public class CreatureOverrideDocument
    {
        public const int CurrentVersion = 1;

        public int Version = CurrentVersion;
        public List<CreatureOverride> Creatures = new List<CreatureOverride>();
    }
}
