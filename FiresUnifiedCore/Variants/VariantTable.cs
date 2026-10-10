using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Variants
{
    /// <summary>
    /// One colour or material variant (Tools\FIRES_VARIANTS_PLAN.md). Generic: one variant works on every pet and mount.
    /// A colour variant shifts vanilla Custom/Creature's own knobs; a material variant replaces the body material.
    /// </summary>
    public sealed class VariantDef
    {
        public string Id;
        public string DisplayName;
        /// <summary>Multiplies the material's _Color (white = unchanged).</summary>
        public Color Tint = Color.white;
        /// <summary>Added to Custom/Creature's _Hue / _Saturation / _Value (the HSV shift vanilla uses for star levels).</summary>
        public float Hue, Saturation, Value;
        /// <summary>HDR emission; ignored when <see cref="HasEmission"/> is false.</summary>
        public Color Emission;
        public bool HasEmission;
        /// <summary>The colour attack and body FX are shifted toward (hue and saturation; each colour keeps its brightness).</summary>
        public Color FxColor;
        /// <summary>A vanilla material name that replaces the body's materials, or null for a colour variant.</summary>
        public string Material;
        /// <summary>FiresMounts: the horse coat this variant maps to, or null.</summary>
        public string HorseCoat;
        /// <summary>The essence item that applies it ("FiresEssence_Verdancy").</summary>
        public string EssenceItem;
        /// <summary>True for the reset essence: it removes any variant.</summary>
        public bool IsClear;
    }

    /// <summary>
    /// The variant table and the frozen key names (a contract between Core, FiresRPGmaker's pets and FiresMounts; never rename).
    /// Fire 10-08: seven variants plus a reset, all generic, unlocked by essences brewed at the Cauldron.
    /// </summary>
    public static class VariantTable
    {
        /// <summary>The variant id: in ItemData.m_customData (pet items, eggs) and in a mount's ZDO. Missing or "" = default look.</summary>
        public const string KeyVariant = "fires_variant";
        /// <summary>The player ZDO: the variant of the pet that player has out (next to "fires_pet").</summary>
        public const string KeyPetVariant = "fires_pet_variant";
        /// <summary>A spawned projectile / AoE ZDO: the FX colour as a Vector3 (HDR). Zero = none.</summary>
        public const string KeyFxTint = "fires_fx_tint";

        public static readonly int KeyVariantHash = KeyVariant.GetStableHashCode();
        public static readonly int KeyPetVariantHash = KeyPetVariant.GetStableHashCode();
        public static readonly int KeyFxTintHash = KeyFxTint.GetStableHashCode();

        public const string Clear = "clear";

        private static readonly Dictionary<string, VariantDef> ById = new Dictionary<string, VariantDef>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, VariantDef> ByEssence = new Dictionary<string, VariantDef>(StringComparer.OrdinalIgnoreCase);

        static VariantTable()
        {
            Add(new VariantDef { Id = Clear, DisplayName = "Clarity", IsClear = true, EssenceItem = "FiresEssence_Clarity",
                FxColor = new Color(1f, 1f, 1f) });
            Add(new VariantDef { Id = "verdant", DisplayName = "Verdant", EssenceItem = "FiresEssence_Verdancy",
                Tint = new Color(0.605f, 1.1f, 0.605f),             // mockup v2: tint (0.55, 1, 0.55) x value 1.10
                HasEmission = true, Emission = new Color(0.6f, 2.2f, 0.8f), FxColor = new Color(0.45f, 1f, 0.5f) });
            Add(new VariantDef { Id = "ember", DisplayName = "Ember", EssenceItem = "FiresEssence_Embers",
                Tint = new Color(0.12f, 0.042f, 0.036f),            // mockup v2: tint (1, 0.35, 0.3) x value 0.12 (black-red)
                HasEmission = true, Emission = new Color(3f, 0.35f, 0.1f), FxColor = new Color(1f, 0.2f, 0.05f) });
            Add(new VariantDef { Id = "frost", DisplayName = "Frost", EssenceItem = "FiresEssence_Frost",
                // Mockup v2: saturation x0.6 (an OFFSET in game: about -0.2, per [worldgen]), tint (0.85, 0.92, 1) x value 1.2.
                Saturation = -0.2f, Tint = new Color(1.02f, 1.1f, 1.2f),
                HasEmission = true, Emission = new Color(0.9f, 2f, 3.2f), FxColor = new Color(0.45f, 0.75f, 1f) });
            Add(new VariantDef { Id = "shadow", DisplayName = "Shadow", EssenceItem = "FiresEssence_Shadow",
                Tint = new Color(0.21f, 0.15f, 0.3f),               // mockup v2: tint (0.7, 0.5, 1) x value 0.3
                HasEmission = true, Emission = new Color(1.6f, 0.3f, 3f), FxColor = new Color(0.6f, 0.2f, 1f) });
            Add(new VariantDef { Id = "crystal", DisplayName = "Crystal", EssenceItem = "FiresEssence_Crystal",
                Material = "Aspect_mat", FxColor = new Color(0f, 0.89f, 1f) });
            Add(new VariantDef { Id = "spectre", DisplayName = "Spectre", EssenceItem = "FiresEssence_Spectre",
                Material = "Ghost_mat", FxColor = new Color(0.55f, 1f, 0.9f) });
            Add(new VariantDef { Id = "cinders", DisplayName = "Cinders", EssenceItem = "FiresEssence_Cinders",
                Material = "Charred_summoned_mat", FxColor = new Color(0.11f, 1f, 0.05f) });
        }

        private static void Add(VariantDef def)
        {
            ById[def.Id] = def;
            if (!string.IsNullOrEmpty(def.EssenceItem)) ByEssence[def.EssenceItem] = def;
        }

        public static IEnumerable<VariantDef> All => ById.Values;

        /// <summary>The variant with this id; false for null, "", the reset id and unknown ids.</summary>
        public static bool TryGet(string id, out VariantDef def)
        {
            def = null;
            return !string.IsNullOrEmpty(id) && ById.TryGetValue(id, out def) && !def.IsClear;
        }

        /// <summary>The variant an essence item applies (its prefab name), including the reset.</summary>
        public static bool TryGetByEssence(string itemPrefabName, out VariantDef def)
        {
            def = null;
            return !string.IsNullOrEmpty(itemPrefabName) && ByEssence.TryGetValue(itemPrefabName, out def);
        }

        /// <summary>The variant stored on an item ("" when none).</summary>
        public static string OfItem(ItemDrop.ItemData item) =>
            item?.m_customData != null && item.m_customData.TryGetValue(KeyVariant, out string id) ? id ?? "" : "";

        /// <summary>Stores a variant on an item; the reset (or "") removes it.</summary>
        public static void SetOnItem(ItemDrop.ItemData item, string id)
        {
            if (item == null) return;
            if (string.IsNullOrEmpty(id) || string.Equals(id, Clear, StringComparison.OrdinalIgnoreCase)) item.m_customData.Remove(KeyVariant);
            else item.m_customData[KeyVariant] = id;
        }
    }
}
