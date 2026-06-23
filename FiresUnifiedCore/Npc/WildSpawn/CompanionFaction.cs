using UnityEngine;

namespace FiresCore.Npc.WildSpawn
{
    /// <summary>
    /// Logical faction for wild-spawned companions, mapped onto a vanilla
    /// <see cref="Character.Faction"/> at dresser time:
    ///   Neutral -> Dverger, Bandit -> ForestMonsters, Cultist -> Demon.
    /// </summary>
    public enum CompanionFaction
    {
        Neutral = 0,
        Bandit  = 1,
        Cultist = 2,
    }

    /// <summary>
    /// Conversion helpers between <see cref="CompanionFaction"/> and
    /// <see cref="Character.Faction"/>. Single point of truth for the mapping.
    /// </summary>
    public static class CompanionFactionExtensions
    {
        public static Character.Faction ToValheim(this CompanionFaction f)
        {
            switch (f)
            {
                case CompanionFaction.Neutral: return Character.Faction.Dverger;
                case CompanionFaction.Bandit:  return Character.Faction.ForestMonsters;
                case CompanionFaction.Cultist: return Character.Faction.Demon;
                default:                       return Character.Faction.Dverger;
            }
        }

        public static bool TryFromValheim(Character.Faction v, out CompanionFaction f)
        {
            switch (v)
            {
                case Character.Faction.Dverger:        f = CompanionFaction.Neutral; return true;
                case Character.Faction.ForestMonsters: f = CompanionFaction.Bandit;  return true;
                case Character.Faction.Demon:          f = CompanionFaction.Cultist; return true;
                default:                               f = CompanionFaction.Neutral; return false;
            }
        }

        /// <summary>True if the faction should reject the recruit dialogue outright.</summary>
        public static bool IsHostileByDefault(this CompanionFaction f) =>
            f == CompanionFaction.Bandit || f == CompanionFaction.Cultist;
    }
}
