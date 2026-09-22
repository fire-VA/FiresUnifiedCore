using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Recipes
{
    /// <summary>
    /// Makes the live ObjectDB match the active override set. Every recipe it touches is snapshotted first, so each
    /// pass restores the originals before re-applying: dropping an override is simply applying the set without it.
    /// Vanilla recipes are shared ScriptableObject ASSETS that outlive the session, so snapshots are kept for the
    /// whole process and <see cref="ResetSession"/> restores them on every world join — otherwise one server's edits
    /// would follow the player onto the next server. Custom recipes are ScriptableObjects owned here, reused by name
    /// so an open crafting panel never holds a destroyed recipe, and destroyed once they leave the set.
    /// </summary>
    internal static class RecipeOverrideApplier
    {
        private sealed class RecipeSnapshot
        {
            public bool Enabled;
            public int Amount;
            public CraftingStation CraftingStation;
            public CraftingStation RepairStation;
            public int MinStationLevel;
            public Piece.Requirement[] Resources;
        }

        private static readonly Dictionary<Recipe, RecipeSnapshot> Originals = new Dictionary<Recipe, RecipeSnapshot>();
        private static readonly Dictionary<string, Recipe> CustomRecipes = new Dictionary<string, Recipe>();
        private static readonly HashSet<string> ReportedMissingPrefabs = new HashSet<string>();
        private static readonly MethodInfo UpdateCraftingPanelMethod = AccessTools.Method(typeof(InventoryGui), "UpdateCraftingPanel");

        public static bool CanApply => ObjectDB.instance != null && ObjectDB.instance.m_items.Count > 0 && ZNetScene.instance != null;

        public static void ResetSession()
        {
            RestoreOriginals();
            foreach (Recipe recipe in CustomRecipes.Values)
            {
                if (recipe == null)
                    continue;
                ObjectDB.instance?.m_recipes.Remove(recipe);
                Object.Destroy(recipe);
            }
            CustomRecipes.Clear();
        }

        public static void Apply(IEnumerable<RecipeOverride> overrides)
        {
            if (!CanApply)
                return;
            ObjectDB db = ObjectDB.instance;
            RestoreOriginals();
            var liveCustomNames = new HashSet<string>();
            int applied = 0;
            foreach (RecipeOverride entry in overrides)
            {
                bool ok = entry.IsCustom ? ApplyCustomRecipe(db, entry) : ApplyToExistingRecipe(db, entry);
                if (!ok)
                    continue;
                applied++;
                if (entry.IsCustom)
                    liveCustomNames.Add(entry.Name);
            }
            RemoveCustomRecipesNotIn(db, liveCustomNames);
            RefreshOpenCraftingPanel();
            FiresLogger.LogInfo($"[RecipeOverrides] applied {applied} override(s) to ObjectDB");
        }

        public static RecipeOverride Describe(Recipe recipe) => DescribeFields(recipe.name, recipe.m_item, recipe.m_enabled,
            recipe.m_amount, recipe.m_craftingStation, recipe.m_repairStation, recipe.m_minStationLevel, recipe.m_resources);

        public static RecipeOverride DescribeOriginal(Recipe recipe)
        {
            if (!Originals.TryGetValue(recipe, out RecipeSnapshot original))
                return Describe(recipe);
            return DescribeFields(recipe.name, recipe.m_item, original.Enabled, original.Amount, original.CraftingStation,
                original.RepairStation, original.MinStationLevel, original.Resources);
        }

        private static RecipeOverride DescribeFields(string name, ItemDrop item, bool enabled, int amount,
            CraftingStation craftingStation, CraftingStation repairStation, int minStationLevel, Piece.Requirement[] resources)
        {
            return new RecipeOverride
            {
                Name = name,
                Item = item != null ? item.name : "",
                IsCustom = RecipeOverrides.IsCustomName(name),
                Enabled = enabled,
                Amount = amount,
                CraftingStation = craftingStation != null ? craftingStation.name : "",
                RepairStation = repairStation != null ? repairStation.name : "",
                MinStationLevel = minStationLevel,
                Resources = (resources ?? new Piece.Requirement[0])
                    .Where(requirement => requirement.m_resItem != null)
                    .Select(requirement => new RecipeOverrideResource
                    {
                        Item = requirement.m_resItem.name,
                        Amount = requirement.m_amount,
                        AmountPerLevel = requirement.m_amountPerLevel,
                        UpgraderResource = requirement.m_upgraderResource,
                        Recover = requirement.m_recover,
                    })
                    .ToList(),
            };
        }

        private static void RestoreOriginals()
        {
            foreach (KeyValuePair<Recipe, RecipeSnapshot> pair in Originals)
            {
                Recipe recipe = pair.Key;
                if (recipe == null)
                    continue;
                RecipeSnapshot original = pair.Value;
                recipe.m_enabled = original.Enabled;
                recipe.m_amount = original.Amount;
                recipe.m_craftingStation = original.CraftingStation;
                recipe.m_repairStation = original.RepairStation;
                recipe.m_minStationLevel = original.MinStationLevel;
                recipe.m_resources = CopyRequirements(original.Resources);
            }
        }

        private static bool ApplyToExistingRecipe(ObjectDB db, RecipeOverride entry)
        {
            Recipe recipe = db.m_recipes.FirstOrDefault(candidate => candidate != null && candidate.name == entry.Name);
            if (recipe == null)
            {
                ReportMissingOnce("recipe", entry.Name);
                return false;
            }
            if (!Originals.ContainsKey(recipe))
                Originals[recipe] = Snapshot(recipe);
            WriteFields(recipe, entry);
            return true;
        }

        private static bool ApplyCustomRecipe(ObjectDB db, RecipeOverride entry)
        {
            ItemDrop result = FindItem(db, entry.Item);
            if (result == null)
                return false;
            if (!CustomRecipes.TryGetValue(entry.Name, out Recipe recipe) || recipe == null)
            {
                recipe = ScriptableObject.CreateInstance<Recipe>();
                recipe.name = entry.Name;
                CustomRecipes[entry.Name] = recipe;
            }
            recipe.m_item = result;
            WriteFields(recipe, entry);
            if (!db.m_recipes.Contains(recipe))
                db.m_recipes.Add(recipe);
            return true;
        }

        private static void WriteFields(Recipe recipe, RecipeOverride entry)
        {
            recipe.m_enabled = entry.Enabled;
            recipe.m_amount = entry.Amount;
            recipe.m_craftingStation = FindStation(entry.CraftingStation);
            recipe.m_repairStation = FindStation(entry.RepairStation);
            recipe.m_minStationLevel = entry.MinStationLevel;
            recipe.m_resources = BuildRequirements(ObjectDB.instance, entry.Resources);
        }

        private static Piece.Requirement[] BuildRequirements(ObjectDB db, List<RecipeOverrideResource> resources)
        {
            var requirements = new List<Piece.Requirement>();
            foreach (RecipeOverrideResource resource in resources)
            {
                ItemDrop item = FindItem(db, resource.Item);
                if (item == null)
                    continue;
                requirements.Add(new Piece.Requirement
                {
                    m_resItem = item,
                    m_amount = resource.Amount,
                    m_amountPerLevel = resource.AmountPerLevel,
                    m_upgraderResource = resource.UpgraderResource,
                    m_recover = resource.Recover,
                });
            }
            return requirements.ToArray();
        }

        private static void RemoveCustomRecipesNotIn(ObjectDB db, HashSet<string> liveCustomNames)
        {
            foreach (string name in CustomRecipes.Keys.Where(name => !liveCustomNames.Contains(name)).ToList())
            {
                Recipe recipe = CustomRecipes[name];
                CustomRecipes.Remove(name);
                if (recipe == null)
                    continue;
                db.m_recipes.Remove(recipe);
                Object.Destroy(recipe);
            }
        }

        private static RecipeSnapshot Snapshot(Recipe recipe) => new RecipeSnapshot
        {
            Enabled = recipe.m_enabled,
            Amount = recipe.m_amount,
            CraftingStation = recipe.m_craftingStation,
            RepairStation = recipe.m_repairStation,
            MinStationLevel = recipe.m_minStationLevel,
            Resources = CopyRequirements(recipe.m_resources),
        };

        private static Piece.Requirement[] CopyRequirements(Piece.Requirement[] source)
        {
            if (source == null)
                return new Piece.Requirement[0];
            return source.Select(requirement => new Piece.Requirement
            {
                m_resItem = requirement.m_resItem,
                m_amount = requirement.m_amount,
                m_extraAmountOnlyOneIngredient = requirement.m_extraAmountOnlyOneIngredient,
                m_amountPerLevel = requirement.m_amountPerLevel,
                m_upgraderResource = requirement.m_upgraderResource,
                m_recover = requirement.m_recover,
            }).ToArray();
        }

        private static ItemDrop FindItem(ObjectDB db, string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return null;
            ItemDrop item = db.GetItemPrefab(prefabName)?.GetComponent<ItemDrop>();
            if (item == null)
                ReportMissingOnce("item", prefabName);
            return item;
        }

        private static CraftingStation FindStation(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
                return null;
            CraftingStation station = ZNetScene.instance.GetPrefab(prefabName)?.GetComponent<CraftingStation>();
            if (station == null)
                ReportMissingOnce("crafting station", prefabName);
            return station;
        }

        private static void ReportMissingOnce(string kind, string prefabName)
        {
            if (ReportedMissingPrefabs.Add(kind + ":" + prefabName))
                FiresLogger.LogWarning($"[RecipeOverrides] skipped: {kind} '{prefabName}' is not loaded");
        }

        private static void RefreshOpenCraftingPanel()
        {
            if (InventoryGui.instance == null || !InventoryGui.IsVisible() || UpdateCraftingPanelMethod == null)
                return;
            UpdateCraftingPanelMethod.Invoke(InventoryGui.instance, new object[] { false });
        }
    }
}
