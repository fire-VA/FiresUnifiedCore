using System;
using System.Collections.Generic;

namespace FiresCore.UI
{
    /// <summary>
    /// Well-known element tag constants for the override wiring system.
    /// These tags are recognized by <see cref="UIVanillaOverrideManager"/> when wiring
    /// injected elements to live game systems. Tags can be set in the UI Builder editor
    /// via the Inspector's Tag field or by the capture system during vanilla UI capture.
    ///
    /// The override system checks tags FIRST (priority over name-guessing) to determine
    /// what an injected element is and how to wire it. This replaces the fragile name-based
    /// heuristic with explicit, user-controllable semantics.
    ///
    /// Tags are also used by the SDK so other mods can find and interact with override
    /// elements programmatically (e.g., reading the player's equipment slots, hooking
    /// into the quick-access bar, or toggling side panels).
    /// </summary>
    public static class UIOverrideElementTags
    {
        // ???????????????????????????????????????
        //  Inventory & Equipment
        // ???????????????????????????????????????

        /// <summary>The main player inventory grid container.</summary>
        public const string InventoryGrid = "override_inventory_grid";

        /// <summary>An extra inventory grid added by a mod (e.g., AzuEPI extra rows).</summary>
        public const string ExtraInventoryGrid = "override_extra_inventory_grid";

        /// <summary>An individual equipment slot (helmet, chest, legs, etc.).</summary>
        public const string EquipmentSlot = "override_equipment_slot";

        /// <summary>Container/background panel for equipment slots.</summary>
        public const string EquipmentPanel = "override_equipment_panel";

        /// <summary>A quick-access / hotkey bar slot.</summary>
        public const string QuickSlot = "override_quick_slot";

        /// <summary>Container for quick-access bar.</summary>
        public const string QuickSlotBar = "override_quick_slot_bar";

        /// <summary>A tool hotbar slot (mirrors Slots.toolHotbarSlots).</summary>
        public const string ToolHotbarSlot = "override_tool_hotbar_slot";

        /// <summary>Container for tool hotbar.</summary>
        public const string ToolHotbarBar = "override_tool_hotbar_bar";

        /// <summary>"Drop All" button in inventory.</summary>
        public const string DropAllButton = "override_drop_all_button";

        // ???????????????????????????????????????
        //  Equipment Slot Types
        // ???????????????????????????????????????

        /// <summary>Helmet equipment slot.</summary>
        public const string EquipHelmet = "override_equip_helmet";

        /// <summary>Chest armor equipment slot.</summary>
        public const string EquipChest = "override_equip_chest";

        /// <summary>Leg armor equipment slot.</summary>
        public const string EquipLegs = "override_equip_legs";

        /// <summary>Cape/shoulder equipment slot.</summary>
        public const string EquipShoulder = "override_equip_shoulder";

        /// <summary>Utility equipment slot (belt, wishbone, etc.).</summary>
        public const string EquipUtility = "override_equip_utility";

        /// <summary>Trinket equipment slot.</summary>
        public const string EquipTrinket = "override_equip_trinket";

        // ???????????????????????????????????????
        //  Food & Ammo Slots
        // ???????????????????????????????????????

        /// <summary>A food slot (one of 3 food slots in vanilla).</summary>
        public const string FoodSlot = "override_food_slot";

        /// <summary>An ammo/projectile slot.</summary>
        public const string AmmoSlot = "override_ammo_slot";

        // ???????????????????????????????????????
        //  Container (Chest)
        // ???????????????????????????????????????

        /// <summary>Root panel for the container UI.</summary>
        public const string ContainerPanel = "override_container_panel";

        /// <summary>Grid container for container inventory slots.</summary>
        public const string ContainerGrid = "override_container_grid";

        /// <summary>Text showing the container's name.</summary>
        public const string ContainerName = "override_container_name";

        /// <summary>Text showing the container's weight.</summary>
        public const string ContainerWeight = "override_container_weight";

        /// <summary>Button to take all items from the container.</summary>
        public const string ContainerTakeAll = "override_container_take_all";

        /// <summary>Button to stack matching items from the container.</summary>
        public const string ContainerStackAll = "override_container_stack_all";

        // ???????????????????????????????????????
        //  Side Panels & Toggle Buttons
        // ???????????????????????????????????????

        /// <summary>Player 3D preview camera panel.</summary>
        public const string PlayerPreview = "override_player_preview";

        /// <summary>Player stats panel (armor, weight, etc.).</summary>
        public const string StatsPanel = "override_stats_panel";

        /// <summary>Vanity/transmog panel.</summary>
        public const string VanityPanel = "override_vanity_panel";

        /// <summary>Loadout/preset panel.</summary>
        public const string LoadoutPanel = "override_loadout_panel";

        /// <summary>Container holding toggle buttons for side panels.</summary>
        public const string ToggleButtonGroup = "override_toggle_group";

        /// <summary>Toggle button for the player preview panel.</summary>
        public const string TogglePreview = "override_toggle_preview";

        /// <summary>Toggle button for the stats panel.</summary>
        public const string ToggleStats = "override_toggle_stats";

        /// <summary>Toggle button for the vanity panel.</summary>
        public const string ToggleVanity = "override_toggle_vanity";

        /// <summary>Toggle button for the loadout panel.</summary>
        public const string ToggleLoadout = "override_toggle_loadout";

        // ???????????????????????????????????????
        //  Crafting
        // ???????????????????????????????????????

        /// <summary>The crafting panel root.</summary>
        public const string CraftingPanel = "override_crafting_panel";

        /// <summary>Recipe list / scroll view in crafting panel.</summary>
        public const string RecipeList = "override_recipe_list";

        /// <summary>Craft button.</summary>
        public const string CraftButton = "override_craft_button";

        /// <summary>Craft amount / multiplier text.</summary>
        public const string CraftAmount = "override_craft_amount";

        // ???????????????????????????????????????
        //  Map & Minimap
        // ???????????????????????????????????????

        /// <summary>Minimap display element (RawImage showing the render texture).</summary>
        public const string MinimapDisplay = "override_minimap";

        /// <summary>Minimap zoom-in button.</summary>
        public const string MinimapZoomIn = "override_minimap_zoom_in";

        /// <summary>Minimap zoom-out button.</summary>
        public const string MinimapZoomOut = "override_minimap_zoom_out";

        /// <summary>Large map display element.</summary>
        public const string MapDisplay = "override_map";

        // ???????????????????????????????????????
        //  HUD Bars
        // ???????????????????????????????????????

        /// <summary>Health bar fill image.</summary>
        public const string HealthBar = "override_health_bar";

        /// <summary>Stamina bar fill image.</summary>
        public const string StaminaBar = "override_stamina_bar";

        /// <summary>Eitr/magic bar fill image.</summary>
        public const string EitrBar = "override_eitr_bar";

        /// <summary>Food bar fill image.</summary>
        public const string FoodBar = "override_food_bar";

        /// <summary>Guardian power cooldown bar.</summary>
        public const string GuardianBar = "override_guardian_bar";

        // ???????????????????????????????????????
        //  HUD Text
        // ???????????????????????????????????????

        /// <summary>Health value text.</summary>
        public const string HealthText = "override_health_text";

        /// <summary>Stamina value text.</summary>
        public const string StaminaText = "override_stamina_text";

        /// <summary>Eitr value text.</summary>
        public const string EitrText = "override_eitr_text";

        /// <summary>Weight display text.</summary>
        public const string WeightText = "override_weight_text";

        /// <summary>Day/time display text.</summary>
        public const string DayTimeText = "override_daytime_text";

        /// <summary>Biome name text.</summary>
        public const string BiomeText = "override_biome_text";

        /// <summary>Comfort level text.</summary>
        public const string ComfortText = "override_comfort_text";

        /// <summary>Guardian power cooldown text.</summary>
        public const string GuardianText = "override_guardian_text";

        // ???????????????????????????????????????
        //  Status Effects & Skills
        // ???????????????????????????????????????

        /// <summary>Container for status effect icons.</summary>
        public const string StatusEffectContainer = "override_status_effects";

        /// <summary>Individual status effect icon.</summary>
        public const string StatusEffectIcon = "override_status_icon";

        /// <summary>Skills panel/dialog.</summary>
        public const string SkillsPanel = "override_skills_panel";

        // ???????????????????????????????????????
        //  Camera / RenderTexture
        // ???????????????????????????????????????

        /// <summary>A RawImage that should display a live camera render texture (e.g., player preview).</summary>
        public const string CameraDisplay = "override_camera_display";

        // ???????????????????????????????????????
        //  Drag & Drop
        // ???????????????????????????????????????

        /// <summary>
        /// A background panel that acts as a drop zone. When a dragged item is released
        /// over this element, the item is dropped on the ground (or the drag is cancelled).
        /// </summary>
        public const string DropZone = "override_drop_zone";

        /// <summary>
        /// Equipment panel that auto-creates the standard equipment slot layout
        /// (Helmet/Chest/Legs/Shoulder/Utility/Trinket + extras).
        /// </summary>
        public const string EquipmentAutoLayout = "override_equipment_auto_layout";

        // ???????????????????????????????????????
        //  Override Protection
        // ???????????????????????????????????????

        /// <summary>
        /// Tag value that marks a layout node (and its entire subtree) as override-protected.
        /// When the override system encounters a node with this tag, it skips that node and
        /// all of its descendants — no transform changes, no visual changes, no injection.
        /// This allows modded UI panels (e.g., VNEI crafting panel, EpicLoot enchanting UI)
        /// captured inside a vanilla hierarchy to remain completely untouched by the override,
        /// letting the original mod (or vanilla) handle that subtree instead.
        /// Set via the Inspector Tag field or the right-click context menu in the editor.
        /// </summary>
        public const string OverrideProtected = "override_protected";

        // ???????????????????????????????????????
        //  Metadata Keys
        // ???????????????????????????????????????

        /// <summary>
        /// Per-node metadata key for "reference text". When set on a Text or InputField node,
        /// this stores a plain-English description of what dynamic data should be wired into that
        /// field at runtime (e.g., "NPC's display name from dialogue system", "Current player health").
        /// The reference text is NOT displayed at runtime — the consuming mod reads it from metadata
        /// to understand what to wire in, then calls SetText() to replace the placeholder content.
        /// Persists in the layout JSON and travels with the layout when packaged for another mod.
        /// </summary>
        public const string ReferenceTextKey = "reference_text";

        // ???????????????????????????????????????
        //  Visibility management
        // ???????????????????????????????????????

        /// <summary>
        /// Tag prefix for elements whose visibility is managed by a compat class.
        /// The SyncInjectedVisibility system will NOT force these elements active/inactive —
        /// the compat MonoBehaviour has full control over their visibility.
        /// </summary>
        public const string CompatManaged = "compat_managed";

        // ???????????????????????????????????????
        //  Lookup helpers
        // ???????????????????????????????????????

        /// <summary>
        /// Checks whether a tag marks the element (and its subtree) as override-protected.
        /// Override-protected elements are completely skipped during override application.
        /// </summary>
        public static bool IsOverrideProtected(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            return string.Equals(tag, OverrideProtected, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks whether a tag indicates the element's visibility is managed by a compat class.
        /// Elements with this flag are excluded from the per-frame SyncInjectedVisibility pass.
        /// </summary>
        public static bool IsCompatManaged(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            return tag.IndexOf(CompatManaged, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Checks whether a node has reference text metadata set.
        /// </summary>
        public static bool HasReferenceText(UIElementNode node)
        {
            if (node == null) return false;
            string val = node.GetMeta(ReferenceTextKey);
            return !string.IsNullOrEmpty(val);
        }

        /// <summary>
        /// Gets the reference text from a node's metadata, or null if not set.
        /// </summary>
        public static string GetReferenceText(UIElementNode node)
        {
            return node?.GetMeta(ReferenceTextKey);
        }

        /// <summary>
        /// Sets or clears the reference text on a node's metadata.
        /// Pass null or empty string to clear.
        /// </summary>
        public static void SetReferenceText(UIElementNode node, string referenceText)
        {
            if (node == null) return;
            if (string.IsNullOrEmpty(referenceText))
            {
                // Clear by setting empty — GetMeta returns null for missing keys,
                // but we set empty to indicate explicitly cleared
                node.SetMeta(ReferenceTextKey, "");
            }
            else
            {
                node.SetMeta(ReferenceTextKey, referenceText);
            }
        }

        /// <summary>
        /// All AzuEPI-related element name prefixes and patterns. Used for fast detection of
        /// injected elements that belong to AzuExtendedPlayerInventory.
        /// </summary>
        public static readonly string[] AzuEPIPrefixes =
        {
            "AzuEPI_",
        };

        /// <summary>
        /// Checks if a GameObject name looks like an AzuEPI injected element.
        /// Only matches elements with the explicit "AzuEPI_" prefix.
        /// Does NOT match generic names like "InventoryElement(Clone)" or "Slot_XX"
        /// because those are also used by vanilla Valheim and other inventory mods.
        /// </summary>
        public static bool IsAzuEPIElement(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < AzuEPIPrefixes.Length; i++)
            {
                if (name.StartsWith(AzuEPIPrefixes[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
