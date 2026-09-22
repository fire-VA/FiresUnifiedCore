using System;
using System.Collections.Generic;

namespace FiresCore.Recipes
{
    /// <summary>One ingredient line of a <see cref="RecipeOverride"/>: Piece.Requirement with the item named by prefab.</summary>
    [Serializable]
    public class RecipeOverrideResource
    {
        public string Item;
        public int Amount = 1;
        public int AmountPerLevel = 1;
        public bool UpgraderResource;
        public bool Recover = true;

        public RecipeOverrideResource Clone() => (RecipeOverrideResource)MemberwiseClone();
    }

    /// <summary>
    /// A server-authored replacement for one crafting recipe, keyed by the Recipe asset name. An override of an existing
    /// recipe replaces every field here; a custom recipe (<see cref="IsCustom"/>) is created from scratch. Items and
    /// stations are prefab names, so the file survives mod reloads and ObjectDB rebuilds.
    /// </summary>
    [Serializable]
    public class RecipeOverride
    {
        public string Name;
        public string Item;
        public bool IsCustom;
        public bool Enabled = true;
        public int Amount = 1;
        public string CraftingStation = "";
        public string RepairStation = "";
        public int MinStationLevel = 1;
        public List<RecipeOverrideResource> Resources = new List<RecipeOverrideResource>();

        public RecipeOverride Clone()
        {
            var copy = (RecipeOverride)MemberwiseClone();
            copy.Resources = Resources.ConvertAll(resource => resource.Clone());
            return copy;
        }
    }

    [Serializable]
    public class RecipeOverrideDocument
    {
        public const int CurrentVersion = 1;

        public int Version = CurrentVersion;
        public List<RecipeOverride> Recipes = new List<RecipeOverride>();
    }
}
