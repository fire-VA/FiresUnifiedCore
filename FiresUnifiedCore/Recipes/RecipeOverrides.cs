using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Storage;
using FiresCore.Sync;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace FiresCore.Recipes
{
    /// <summary>
    /// Server-authoritative crafting-recipe overrides for the Fires family. The server holds the only file
    /// (<see cref="FiresConfigPaths.Recipes"/>/recipe_overrides.json) and re-checks admin on every change; each client
    /// pulls the set once its player spawns and receives it again after every change, then applies it to its own
    /// ObjectDB — crafting is client-side, so a change only counts once every client holds it. Hand edits to the file
    /// apply live. Editors (FATI) call <see cref="Submit"/> / <see cref="Remove"/> and listen to <see cref="Changed"/>.
    /// </summary>
    public static class RecipeOverrides
    {
        public const string CustomNamePrefix = "FiresRecipe_";

        private const string FileName = "recipe_overrides.json";
        private const int MaxResourcesPerRecipe = 32;
        private const int MaxAmount = 9999;
        private const int MaxStationLevel = 20;

        private static readonly ServerOverrideStore<RecipeOverride> Store = new ServerOverrideStore<RecipeOverride>(
            "RecipeOverrides", FileName, () => FiresConfigPaths.Recipes, entry => entry.Name, TrySanitize,
            entries => JsonConvert.SerializeObject(new RecipeOverrideDocument { Recipes = entries }, Formatting.Indented),
            json => JsonConvert.DeserializeObject<RecipeOverrideDocument>(json)?.Recipes ?? new List<RecipeOverride>(),
            entry => entry.Clone(),
            entry => IsCustomName(entry.Name) ? $"Deleted '{entry.Name}'." : $"'{entry.Name}' is back to its original.",
            ApplyToLocalGame);

        public static event Action Changed
        {
            add => Store.Changed += value;
            remove => Store.Changed -= value;
        }

        public static event Action<bool, string> SubmitResult
        {
            add => Store.SubmitResult += value;
            remove => Store.SubmitResult -= value;
        }

        public static IReadOnlyList<RecipeOverride> All => Store.All;

        public static bool TryGet(string recipeName, out RecipeOverride entry) => Store.TryGet(recipeName, out entry);

        public static bool IsCustomName(string recipeName) =>
            recipeName != null && recipeName.StartsWith(CustomNamePrefix, StringComparison.Ordinal);

        public static RecipeOverride Describe(Recipe recipe) => RecipeOverrideApplier.Describe(recipe);

        public static RecipeOverride DescribeOriginal(Recipe recipe) => RecipeOverrideApplier.DescribeOriginal(recipe);

        public static string NewCustomName(string itemPrefab)
        {
            string stem = CustomNamePrefix + itemPrefab + "_";
            int index = 1;
            while (Store.Contains(stem + index) || RecipeExists(stem + index))
                index++;
            return stem + index;
        }

        /// <summary>Creates or replaces one override for everyone. On a client this asks the server, which re-checks admin.</summary>
        public static void Submit(RecipeOverride entry) => Store.Submit(entry);

        /// <summary>Drops an override (the recipe returns to its original) or deletes a custom recipe, for everyone.</summary>
        public static void Remove(string recipeName) => Store.Remove(recipeName);

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class RegisterOnSessionStart
        {
            [HarmonyPostfix]
            private static void Postfix(ZNet __instance)
            {
                RecipeOverrideApplier.ResetSession();
                Store.OnSessionStart(__instance);
            }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class PullOnLocalSpawn
        {
            [HarmonyPostfix]
            private static void Postfix(Player __instance)
            {
                if (__instance == Player.m_localPlayer)
                    Store.OnLocalPlayerSpawned();
            }
        }

        private static void ApplyToLocalGame()
        {
            if (Player.m_localPlayer != null)
                RecipeOverrideApplier.Apply(Store.Entries);
        }

        private static bool TrySanitize(RecipeOverride entry, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(entry.Name))
                error = "The recipe has no name.";
            else if (entry.IsCustom != IsCustomName(entry.Name))
                error = $"Custom recipes must be named '{CustomNamePrefix}…', and only custom recipes may use that prefix.";
            else if (entry.IsCustom && string.IsNullOrWhiteSpace(entry.Item))
                error = "A custom recipe needs a result item.";
            else if (entry.Resources != null && entry.Resources.Count > MaxResourcesPerRecipe)
                error = $"A recipe can list at most {MaxResourcesPerRecipe} ingredients.";
            if (error != null)
                return false;

            entry.Item = entry.Item ?? "";
            entry.CraftingStation = entry.CraftingStation ?? "";
            entry.RepairStation = entry.RepairStation ?? "";
            entry.Amount = Mathf.Clamp(entry.Amount, 1, MaxAmount);
            entry.MinStationLevel = Mathf.Clamp(entry.MinStationLevel, 1, MaxStationLevel);
            entry.Resources = (entry.Resources ?? new List<RecipeOverrideResource>())
                .Where(resource => resource != null && !string.IsNullOrWhiteSpace(resource.Item))
                .ToList();
            foreach (RecipeOverrideResource resource in entry.Resources)
            {
                resource.Amount = Mathf.Clamp(resource.Amount, 0, MaxAmount);
                resource.AmountPerLevel = Mathf.Clamp(resource.AmountPerLevel, 0, MaxAmount);
            }
            return true;
        }

        private static bool RecipeExists(string name) =>
            ObjectDB.instance != null && ObjectDB.instance.m_recipes.Any(recipe => recipe != null && recipe.name == name);
    }
}
