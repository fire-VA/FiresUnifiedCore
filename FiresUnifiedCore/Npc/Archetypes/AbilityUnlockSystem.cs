using System.Collections.Generic;
using FiresCore.Classes;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// The companion-facing view of the class roster. The ability data itself lives in
    /// <see cref="ClassAbilityRegistry"/>, shared with players.
    /// Plan: Docs/PLAN_ClassesFoundation.md
    /// </summary>
    public static class AbilityUnlockSystem
    {
        /// <summary>Level required to unlock sub-archetype (hybrid class)</summary>
        public const int HYBRID_UNLOCK_LEVEL = ClassAbilityRegistry.HybridUnlockLevel;

        /// <summary>Level required to use hybrid special ability</summary>
        public const int HYBRID_ABILITY_LEVEL = ClassAbilityRegistry.HybridAbilityLevel;

        /// <summary>Level required to use emergency evasion</summary>
        public const int EVASION_UNLOCK_LEVEL = ClassAbilityRegistry.EvasionUnlockLevel;

        public static bool HasUnlockedHybrid(int level) => ClassAbilityRegistry.HasUnlockedHybrid(level);

        public static bool HasUnlockedHybridAbility(int level) => ClassAbilityRegistry.HasUnlockedHybridAbility(level);

        public static bool HasUnlockedEvasion(int level) => ClassAbilityRegistry.HasUnlockedEvasion(level);

        public static bool IsAbilityUnlocked(string abilityId, ArchetypeClass archetype, int level) =>
            ClassAbilityRegistry.IsAbilityUnlocked(abilityId, archetype, level);

        public static List<ClassAbility> GetAbilitiesForArchetype(ArchetypeClass archetype) =>
            ClassAbilityRegistry.GetAbilitiesForArchetype(archetype);

        public static List<ClassAbility> GetUnlockedAbilities(ArchetypeClass archetype, int level) =>
            ClassAbilityRegistry.GetUnlockedAbilities(archetype, level);

        public static List<ClassAbility> GetLockedAbilities(ArchetypeClass archetype, int level) =>
            ClassAbilityRegistry.GetLockedAbilities(archetype, level);

        public static ClassAbility GetNextUnlock(ArchetypeClass archetype, int currentLevel) =>
            ClassAbilityRegistry.GetNextUnlock(archetype, currentLevel);

        public static ClassAbility GetAbilityDefinition(string abilityId, ArchetypeClass archetype) =>
            ClassAbilityRegistry.GetAbility(abilityId, archetype);

        public static int GetHybridUnlockLevel() => HYBRID_UNLOCK_LEVEL;

        /// <summary>Unlock progress for a companion's stats screen.</summary>
        public static string GetUnlockProgressSummary(ArchetypeClass archetype, int level)
        {
            var unlocked = GetUnlockedAbilities(archetype, level);
            var total = GetAbilitiesForArchetype(archetype);
            var next = GetNextUnlock(archetype, level);

            string summary = $"{unlocked.Count}/{total.Count} abilities unlocked";

            if (next != null)
                summary += $"\nNext: {next.DisplayName} at level {next.UnlockLevel}";

            if (!HasUnlockedHybrid(level))
                summary += $"\nHybrid Class unlocks at level {HYBRID_UNLOCK_LEVEL}";

            return summary;
        }
    }
}
