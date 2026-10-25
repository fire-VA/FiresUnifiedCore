using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FiresCore.Logging;
using FiresCore.UI.Minigames;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Variants
{
    /// <summary>
    /// The essences: one item per variant plus a reset, brewed at the Cauldron. Each is a clone of vanilla's
    /// BlobVial (a Material-type item, so it can't be thrown or eaten) with its glass tinted to the essence colour, a soft glow
    /// and its own icon. Core only makes the items; using one is the caller's: FiresRPGmaker applies it to the pet you have
    /// out, FiresMounts to a mount or an egg. Recipes: Tools\FIRES_VARIANTS_PLAN.md section 6.
    /// </summary>
    public static class VariantEssences
    {
        private const string Template = "BlobVial";
        private const string Station = "piece_cauldron";

        private sealed class Spec
        {
            public string Item, Name, Description;
            public int Level;
            public (string prefab, int amount)[] Recipe;
            public Color Colour;
        }

        private static readonly Spec[] Specs =
        {
            new Spec { Item = "FiresEssence_Clarity", Name = "Essence of Clarity", Level = 1, Colour = new Color(0.95f, 0.95f, 1f),
                Description = "Use it with a pet out, or on a mount: washes any essence away and brings back its own colours.",
                Recipe = new[] { ("Resin", 5), ("Dandelion", 3) } },
            new Spec { Item = "FiresEssence_Verdancy", Name = "Essence of Verdancy", Level = 1, Colour = new Color(0.45f, 1f, 0.5f),
                Description = "Use it with a pet out, or on a mount: green and white, with a pale green glow.",
                Recipe = new[] { ("Guck", 3), ("Thistle", 5), ("Dandelion", 5) } },
            new Spec { Item = "FiresEssence_Embers", Name = "Essence of Embers", Level = 1, Colour = new Color(1f, 0.25f, 0.08f),
                Description = "Use it with a pet out, or on a mount: black and red, with an ember glow.",
                Recipe = new[] { ("SurtlingCore", 2), ("Coal", 10), ("Resin", 5) } },
            new Spec { Item = "FiresEssence_Frost", Name = "Essence of Frost", Level = 2, Colour = new Color(0.55f, 0.8f, 1f),
                Description = "Use it with a pet out, or on a mount: pale and icy, with a blue glow.",
                Recipe = new[] { ("FreezeGland", 3), ("Crystal", 2) } },
            new Spec { Item = "FiresEssence_Shadow", Name = "Essence of Shadow", Level = 3, Colour = new Color(0.6f, 0.25f, 1f),
                Description = "Use it with a pet out, or on a mount: near black, with a violet glow.",
                Recipe = new[] { ("Ectoplasm", 5), ("BlackCore", 1) } },
            new Spec { Item = "FiresEssence_Spectre", Name = "Essence of the Spectre", Level = 3, Colour = new Color(0.6f, 1f, 0.92f),
                Description = "Use it with a pet out, or on a mount: a pale, see-through spirit.",
                Recipe = new[] { ("Ectoplasm", 10), ("Wisp", 3) } },
            new Spec { Item = "FiresEssence_Crystal", Name = "Essence of Crystal", Level = 3, Colour = new Color(0.2f, 0.9f, 1f),
                Description = "Use it with a pet out, or on a mount: clear, glowing ice, like the Frozen King's aspects.",
                Recipe = new[] { ("Crystal", 5), ("FreezeGland", 3), ("DragonTear", 1) } },
            new Spec { Item = "FiresEssence_Cinders", Name = "Essence of Cinders", Level = 4, Colour = new Color(0.3f, 1f, 0.2f),
                Description = "Use it with a pet out, or on a mount: the Ashlands' summoned charred, with its green glow.",
                Recipe = new[] { ("Charredskull", 1), ("SulfurStone", 5), ("MoltenCore", 1) } },
        };

        private static GameObject _holder;
        private static readonly Dictionary<string, GameObject> Items = new Dictionary<string, GameObject>(StringComparer.Ordinal);

        /// <summary>The variant an essence applies; false for anything that isn't an essence. Clarity returns the reset.</summary>
        public static bool IsEssence(ItemDrop.ItemData item, out VariantDef def)
        {
            def = null;
            string prefab = item?.m_dropPrefab != null ? item.m_dropPrefab.name : null;
            return prefab != null && prefab.StartsWith("FiresEssence_", StringComparison.Ordinal) && VariantTable.TryGetByEssence(prefab, out def);
        }

        // The essences and
        // their Cauldron recipes exist only when a mod that uses them is loaded. Without one, nothing is added to ObjectDB or ZNetScene.
        private static readonly string[] OwnerGuids = { "com.Fire.FiresRPGmaker", "com.Fire.FiresMounts" };
        private static bool s_skipLogged;

        private static bool OwnerLoaded()
        {
            foreach (string guid in OwnerGuids)
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(guid)) return true;
            if (!s_skipLogged)
            {
                s_skipLogged = true;
                FiresLogger.LogDebug("[Variants] essences not registered: no owner loaded (FiresRPGmaker, FiresMounts); no items, recipes or prefabs added.");
            }
            return false;
        }

        private static void EnsureRegistered()
        {
            if (!OwnerLoaded()) return;
            ObjectDB db = ObjectDB.instance;
            if (db == null || db.m_items == null || db.m_items.Count == 0) return;
            GameObject template = db.GetItemPrefab(Template);
            if (template == null)
            {
                FiresLogger.LogDebug($"[Variants] essences not made: no '{Template}' in ObjectDB.");
                return;
            }
            if (_holder == null)
            {
                _holder = new GameObject("FiresEssences");
                _holder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_holder);
            }
            int made = 0, recipes = 0;
            foreach (Spec spec in Specs)
            {
                if (!Items.TryGetValue(spec.Item, out GameObject item) || item == null)
                {
                    item = Build(spec, template);
                    Items[spec.Item] = item;
                    made++;
                }
                Register(db, item);
                if (AddRecipe(db, spec, item)) recipes++;
            }
            if (made > 0 || recipes > 0)
                FiresLogger.LogDebug($"[Variants] {Items.Count} essence(s) from {Template} ({made} built, {recipes} recipe(s) added at {Station}).");
        }

        private static GameObject Build(Spec spec, GameObject template)
        {
            GameObject item = UnityEngine.Object.Instantiate(template, _holder.transform, false);
            item.name = spec.Item;
            ItemDrop drop = item.GetComponent<ItemDrop>();
            // Instantiate copies the serialized item data, so this SharedData is the clone's own (BlobVial keeps its own).
            ItemDrop.ItemData.SharedData shared = drop.m_itemData.m_shared;
            shared.m_name = spec.Name;
            shared.m_description = spec.Description;
            shared.m_itemType = ItemDrop.ItemData.ItemType.Material;
            shared.m_maxStackSize = 10;
            shared.m_teleportable = true;
            drop.m_itemData.m_dropPrefab = item;

            Sprite icon = FiresMinigameSprites.Load(Assembly.GetExecutingAssembly(), "Essences." + spec.Item + ".png");
            if (icon != null) shared.m_icons = new[] { icon };

            // The glass: an own tinted copy of each material (an item template can't carry a property block), with a glow.
            foreach (Renderer r in item.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                Material[] mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null || mats[i].name.IndexOf("leather", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    var tinted = new Material(mats[i]) { name = mats[i].name + "_" + spec.Item };
                    if (tinted.HasProperty("_Color")) tinted.color = Color.Lerp(Color.white, spec.Colour, 0.85f);
                    if (tinted.HasProperty("_EmissionColor"))
                    {
                        tinted.EnableKeyword("_EMISSION");
                        tinted.SetColor("_EmissionColor", spec.Colour * 0.6f);
                    }
                    mats[i] = tinted;
                }
                r.sharedMaterials = mats;
            }
            var glow = new GameObject("EssenceGlow");
            glow.transform.SetParent(item.transform, false);
            glow.transform.localPosition = new Vector3(0f, 0.1f, 0f);
            Light light = glow.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = spec.Colour;
            light.range = 1.2f;
            light.intensity = 0.8f;
            light.shadows = LightShadows.None;
            return item;
        }

        private static void Register(ObjectDB db, GameObject item)
        {
            if (!db.m_items.Contains(item)) db.m_items.Add(item);
            int hash = item.name.GetStableHashCode();
            db.m_itemByHash[hash] = item;
            ZNetScene scene = ZNetScene.instance;
            if (scene != null && !scene.m_namedPrefabs.ContainsKey(hash))
            {
                scene.m_prefabs.Add(item);
                scene.m_namedPrefabs[hash] = item;
            }
        }

        private static bool AddRecipe(ObjectDB db, Spec spec, GameObject item)
        {
            string recipeName = "Recipe_" + spec.Item;
            if (db.m_recipes.Any(r => r != null && r.name == recipeName)) return false;
            // The station must resolve first. ObjectDB.Awake can run before ZNetScene.Awake;
            // a recipe added then had no crafting station (craftable by hand anywhere), and the later ZNetScene.Awake pass saw it
            // already there and never fixed it. Without the cauldron, add nothing now; the ZNetScene.Awake postfix adds it.
            CraftingStation station = ZNetScene.instance?.GetPrefab(Station)?.GetComponent<CraftingStation>();
            if (station == null) return false;
            var resources = new List<Piece.Requirement>();
            foreach ((string prefab, int amount) in spec.Recipe)
            {
                ItemDrop part = db.GetItemPrefab(prefab)?.GetComponent<ItemDrop>();
                if (part == null)
                {
                    FiresLogger.LogDebug($"[Variants] {spec.Item}: ingredient '{prefab}' not in ObjectDB; recipe not added.");
                    return false;
                }
                resources.Add(new Piece.Requirement { m_resItem = part, m_amount = amount, m_recover = true });
            }
            var recipe = ScriptableObject.CreateInstance<Recipe>();
            recipe.name = recipeName;
            recipe.m_item = item.GetComponent<ItemDrop>();
            recipe.m_amount = 1;
            recipe.m_craftingStation = station;
            recipe.m_minStationLevel = spec.Level;
            recipe.m_resources = resources.ToArray();
            recipe.m_enabled = true;
            db.m_recipes.Add(recipe);
            return true;
        }

        [HarmonyPatch(typeof(ObjectDB), "Awake")]
        private static class ObjectDBAwake
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix() => EnsureRegistered();
        }

        [HarmonyPatch(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB))]
        private static class ObjectDBCopy
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix() => EnsureRegistered();
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class ZNetSceneAwake
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix() => EnsureRegistered();
        }
    }
}
