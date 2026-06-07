using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using HarmonyLib;

namespace FiresCore.UI
{
    /// <summary>
    /// Wires an override crafting panel to the vanilla InventoryGui crafting system.
    /// Reads the available recipe list from <c>InventoryGui.instance</c> and builds
    /// a visual recipe list inside the tagged container. The craft button forwards
    /// to <c>InventoryGui.OnCraftPressed()</c>.
    ///
    /// Child elements are discovered by tag:
    ///   - <c>override_recipe_list</c>   ? ScrollView content for recipe entries
    ///   - <c>override_craft_button</c>  ? Button to trigger crafting
    ///   - <c>override_craft_amount</c>  ? TMP_Text showing craft amount
    ///
    /// Each recipe entry is a runtime-created row with icon, name, and craftability
    /// indicator, styled to match vanilla patterns.
    /// </summary>
    public class UIOverrideCraftingWiring : MonoBehaviour
    {
        public float RecipeRowHeight = 30f;

        private Transform _recipeListRoot;
        private Button _craftButton;
        private TMP_Text _craftAmountText;

        private readonly List<GameObject> _recipeRows = new List<GameObject>();
        private int _lastRecipeCount = -1;
        private float _recheckTimer;

        private static readonly FieldInfo fi_availableRecipes =
            AccessTools.Field(typeof(InventoryGui), "m_availableRecipes");
        private static readonly MethodInfo mi_onCraftPressed =
            AccessTools.Method(typeof(InventoryGui), "OnCraftPressed");
        private static readonly MethodInfo mi_onSelectedRecipe =
            AccessTools.Method(typeof(InventoryGui), "OnSelectedRecipe");

        private void Start()
        {
            DiscoverChildren();
            WireButtons();
        }

        private void Update()
        {
            _recheckTimer += Time.unscaledDeltaTime;
            if (_recheckTimer < 0.5f) return;
            _recheckTimer = 0f;

            if (InventoryGui.instance == null) return;

            var recipes = GetAvailableRecipes();
            if (recipes == null)
            {
                if (_lastRecipeCount != 0)
                {
                    ClearRecipeList();
                    _lastRecipeCount = 0;
                }
                return;
            }

            if (recipes.Count != _lastRecipeCount)
            {
                RebuildRecipeList(recipes);
                _lastRecipeCount = recipes.Count;
            }
        }

        // ???????????????????????????????????????
        //  Child discovery
        // ???????????????????????????????????????

        private void DiscoverChildren()
        {
            foreach (Transform child in transform)
            {
                var tag = child.GetComponent<UIBuilderElementTag>();
                if (tag == null) continue;

                string t = tag.Tag;
                if (string.IsNullOrEmpty(t)) continue;

                if (string.Equals(t, UIOverrideElementTags.RecipeList, StringComparison.OrdinalIgnoreCase))
                    _recipeListRoot = child;
                else if (string.Equals(t, UIOverrideElementTags.CraftButton, StringComparison.OrdinalIgnoreCase))
                    _craftButton = child.GetComponent<Button>();
                else if (string.Equals(t, UIOverrideElementTags.CraftAmount, StringComparison.OrdinalIgnoreCase))
                    _craftAmountText = child.GetComponent<TMP_Text>();
            }

            if (_recipeListRoot == null)
                _recipeListRoot = transform;
        }

        private void WireButtons()
        {
            if (_craftButton != null)
            {
                _craftButton.onClick.RemoveAllListeners();
                _craftButton.onClick.AddListener(OnCraftPressed);
            }
        }

        private void OnCraftPressed()
        {
            if (InventoryGui.instance == null) return;
            try
            {
                mi_onCraftPressed?.Invoke(InventoryGui.instance, null);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIOverrideCraftingWiring] OnCraftPressed failed: {ex.Message}");
            }
        }

        // ???????????????????????????????????????
        //  Recipe list
        // ???????????????????????????????????????

        private System.Collections.IList GetAvailableRecipes()
        {
            if (InventoryGui.instance == null) return null;
            try
            {
                return fi_availableRecipes?.GetValue(InventoryGui.instance) as System.Collections.IList;
            }
            catch
            {
                return null;
            }
        }

        private void RebuildRecipeList(System.Collections.IList recipes)
        {
            ClearRecipeList();

            if (recipes == null || recipes.Count == 0) return;

            // Set up vertical layout
            var vlg = _recipeListRoot.GetComponent<VerticalLayoutGroup>();
            if (vlg == null) vlg = _recipeListRoot.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 2f;
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            // The RecipeDataPair is a private struct — we access its fields via reflection
            var recipeDataPairType = typeof(InventoryGui).GetNestedType("RecipeDataPair",
                BindingFlags.NonPublic | BindingFlags.Public);

            PropertyInfo pi_recipe = recipeDataPairType?.GetProperty("Recipe");
            PropertyInfo pi_canCraft = recipeDataPairType?.GetProperty("CanCraft");
            PropertyInfo pi_element = recipeDataPairType?.GetProperty("InterfaceElement");

            for (int i = 0; i < recipes.Count; i++)
            {
                var pair = recipes[i];
                if (pair == null) continue;

                Recipe recipe = null;
                bool canCraft = false;
                GameObject vanillaElement = null;

                try
                {
                    recipe = pi_recipe?.GetValue(pair) as Recipe;
                    var canCraftObj = pi_canCraft?.GetValue(pair);
                    if (canCraftObj != null) canCraft = (bool)canCraftObj;
                    vanillaElement = pi_element?.GetValue(pair) as GameObject;
                }
                catch { continue; }

                if (recipe == null || recipe.m_item == null) continue;

                var row = CreateRecipeRow(i, recipe, canCraft, vanillaElement);
                _recipeRows.Add(row);
            }
        }

        private GameObject CreateRecipeRow(int index, Recipe recipe, bool canCraft, GameObject vanillaElement)
        {
            var go = new GameObject($"Recipe_{index}", typeof(RectTransform));
            go.transform.SetParent(_recipeListRoot, false);

            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0, RecipeRowHeight);

            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = RecipeRowHeight;
            le.flexibleWidth = 1;

            // Background (semi-transparent, brighter if craftable)
            var bg = go.AddComponent<Image>();
            bg.color = canCraft ? new Color(0.15f, 0.15f, 0.15f, 0.6f) : new Color(0.1f, 0.1f, 0.1f, 0.3f);
            bg.raycastTarget = true;

            // HLG for icon + name
            var hlg = go.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 4f;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            hlg.childControlWidth = false;
            hlg.childControlHeight = false;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;
            hlg.padding = new RectOffset(4, 4, 2, 2);

            // Icon
            var iconGO = new GameObject("icon", typeof(RectTransform));
            iconGO.transform.SetParent(go.transform, false);
            var iconLE = iconGO.AddComponent<LayoutElement>();
            iconLE.preferredWidth = RecipeRowHeight - 4f;
            iconLE.preferredHeight = RecipeRowHeight - 4f;
            var iconImg = iconGO.AddComponent<Image>();
            iconImg.sprite = recipe.m_item.m_itemData.GetIcon();
            iconImg.color = canCraft ? Color.white : new Color(0.5f, 0.5f, 0.5f, 0.5f);
            iconImg.raycastTarget = false;

            // Name
            var nameGO = new GameObject("name", typeof(RectTransform));
            nameGO.transform.SetParent(go.transform, false);
            var nameLE = nameGO.AddComponent<LayoutElement>();
            nameLE.flexibleWidth = 1;
            nameLE.preferredHeight = RecipeRowHeight - 4f;
            var nameText = nameGO.AddComponent<TextMeshProUGUI>();
            string itemName = Localization.instance.Localize(recipe.m_item.m_itemData.m_shared.m_name);
            if (recipe.m_amount > 1)
                itemName += $" x{recipe.m_amount}";
            nameText.text = itemName;
            nameText.fontSize = 14;
            nameText.alignment = TextAlignmentOptions.MidlineLeft;
            nameText.color = canCraft ? Color.white : new Color(0.66f, 0.66f, 0.66f, 1f);
            nameText.raycastTarget = false;

            // Click handler — forward to vanilla OnSelectedRecipe
            var btn = go.AddComponent<Button>();
            var vanillaEl = vanillaElement; // capture for closure
            btn.onClick.AddListener(() =>
            {
                if (InventoryGui.instance == null || vanillaEl == null) return;
                try
                {
                    mi_onSelectedRecipe?.Invoke(InventoryGui.instance, new object[] { vanillaEl });
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UIOverrideCraftingWiring] OnSelectedRecipe failed: {ex.Message}");
                }
            });

            return go;
        }

        private void ClearRecipeList()
        {
            foreach (var go in _recipeRows)
            {
                if (go != null) Destroy(go);
            }
            _recipeRows.Clear();
        }

        private void OnDestroy()
        {
            ClearRecipeList();
        }
    }
}
