using FiresCore.Npc.Archetypes;

namespace FiresCore.Classes
{
    /// <summary>
    /// One ability an archetype can own, for players and companions alike. Definition data only -
    /// nothing here knows who is casting. Plan: Docs/PLAN_ClassesFoundation.md
    /// </summary>
    public class ClassAbility
    {
        public string Id;
        public string DisplayName;
        public string Description;
        public int UnlockLevel;
        public ArchetypeClass RequiredArchetype;
        public AbilityCategory Category;
        public string IconColor;
        public bool IsPassive;

        public ClassAbility(string id, string name, string desc, int level,
            ArchetypeClass archetype, AbilityCategory category, string color = "#FFFFFF", bool passive = false)
        {
            Id = id;
            DisplayName = name;
            Description = desc;
            UnlockLevel = level;
            RequiredArchetype = archetype;
            Category = category;
            IconColor = color;
            IsPassive = passive;
        }
    }

    /// <summary>How far into an archetype an ability sits. Drives the default tree rings.</summary>
    public enum AbilityCategory
    {
        Starter,
        Basic,
        Advanced,
        Expert,
        Master,
        Hybrid,
        Ultimate
    }
}
