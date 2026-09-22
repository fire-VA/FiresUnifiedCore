using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// The only place that attaches slot, slot-interaction and hotbar mirrors to injected override elements, driven
    /// by their <see cref="UIOverrideElementTags"/> and the editor metadata slot_id (or grid_x and grid_y),
    /// hotbar_index and interactive. Called by <see cref="UIVanillaOverrideManager"/> once the override layout is
    /// instantiated.
    /// </summary>
    public static class UIOverrideInventoryWiring
    {
        /// <summary>
        /// Scans the given injected GameObjects for inventory-related tags and
        /// attaches the appropriate mirror/interaction components.
        /// Returns the count of successfully wired elements.
        /// </summary>
        public static int WireInventoryElements(List<GameObject> injectedObjects)
        {
            if (injectedObjects == null || injectedObjects.Count == 0) return 0;

            int wired = 0;

            foreach (var go in injectedObjects)
            {
                if (go == null) continue;

                // Check for the UIBuilderElementTag component (set by the editor/capture)
                var tagComp = go.GetComponent<UIBuilderElementTag>();
                if (tagComp == null) continue;

                string tag = tagComp.Tag;
                if (string.IsNullOrEmpty(tag)) continue;

                if (TryWireEquipmentSlot(go, tag, tagComp))
                {
                    wired++;
                    continue;
                }

                if (TryWireQuickSlot(go, tag, tagComp))
                {
                    wired++;
                    continue;
                }

                if (TryWireToolHotbarSlot(go, tag, tagComp))
                {
                    wired++;
                    continue;
                }

                // InventoryGrid, EquipmentPanel, and ToolHotbarBar tags no longer
                // attach runtime grid builders. The editor layout defines all slot
                // positions — no dynamic grid creation at runtime.

                if (TryWirePlayerPreview(go, tag))
                {
                    wired++;
                    continue;
                }

                if (TryWireContainerPanel(go, tag))
                {
                    wired++;
                    continue;
                }

                if (TryWireDropZone(go, tag))
                {
                    wired++;
                    continue;
                }

                // EquipmentAutoLayout tag no longer attaches a runtime auto-layout
                // component. Equipment slots are placed in the editor.

                if (TryWireStatusEffects(go, tag))
                {
                    wired++;
                    continue;
                }

                if (TryWireCraftingPanel(go, tag))
                {
                    wired++;
                    continue;
                }

                if (TryWireSkillsPanel(go, tag))
                {
                    wired++;
                    continue;
                }

                if (TryWireMapDisplay(go, tag))
                {
                    wired++;
                    continue;
                }
            }

            // Ensure drag ghost manager exists when we have interactive slots
            if (wired > 0)
                UIOverrideDragGhostManager.EnsureInstance();

            if (wired > 0)
                Debug.Log($"[UIOverrideInventoryWiring] Wired {wired} inventory element(s)");

            return wired;
        }

        //  Equipment slot wiring

        private static bool TryWireEquipmentSlot(GameObject go, string tag, UIBuilderElementTag tagComp)
        {
            if (!string.Equals(tag, UIOverrideElementTags.EquipmentSlot, StringComparison.OrdinalIgnoreCase))
                return false;

            // Read metadata for slot binding
            string slotId = GetMeta(go, "slot_id");
            int gridX = GetMetaInt(go, "grid_x", -1);
            int gridY = GetMetaInt(go, "grid_y", -1);
            bool interactive = GetMetaBool(go, "interactive", false);

            if (string.IsNullOrEmpty(slotId) && gridX < 0 && gridY < 0)
            {
                Debug.LogWarning($"[UIOverrideInventoryWiring] Equipment slot '{go.name}' " +
                    "has no slot_id or grid position metadata — skipping");
                return false;
            }

            // Attach visual mirror
            var mirror = go.GetComponent<UIOverrideSlotMirror>();
            if (mirror == null) mirror = go.AddComponent<UIOverrideSlotMirror>();
            mirror.SlotID = slotId ?? "";
            mirror.GridPosition = new Vector2Int(gridX, gridY);
            mirror.Interactive = interactive;

            // Attach tooltip handler
            var tooltip = go.GetComponent<UIOverrideSlotTooltipHandler>();
            if (tooltip == null) tooltip = go.AddComponent<UIOverrideSlotTooltipHandler>();
            tooltip.SlotID = slotId ?? "";
            tooltip.GridPosition = new Vector2Int(gridX, gridY);

            // Attach interaction handler if interactive
            if (interactive)
            {
                var interaction = go.GetComponent<UIOverrideSlotInteraction>();
                if (interaction == null) interaction = go.AddComponent<UIOverrideSlotInteraction>();
                interaction.SlotID = slotId ?? "";
                interaction.GridPosition = new Vector2Int(gridX, gridY);
            }

            Debug.Log($"[UIOverrideInventoryWiring] Wired equipment slot '{go.name}' -> " +
                $"SlotID='{slotId}' Grid=({gridX},{gridY}) Interactive={interactive}");
            return true;
        }

        //  Quick / hotbar slot wiring

        private static bool TryWireQuickSlot(GameObject go, string tag, UIBuilderElementTag tagComp)
        {
            if (!string.Equals(tag, UIOverrideElementTags.QuickSlot, StringComparison.OrdinalIgnoreCase))
                return false;

            // Hotbar slots use hotbar_index metadata
            int hotbarIndex = GetMetaInt(go, "hotbar_index", -1);
            bool interactive = GetMetaBool(go, "interactive", true); // Hotbar defaults to interactive

            if (hotbarIndex < 0)
            {
                // Try to infer from slot_id (e.g., "ToolHotbar1" - index 0)
                string slotId = GetMeta(go, "slot_id");
                if (!string.IsNullOrEmpty(slotId) && slotId.StartsWith("ToolHotbar", StringComparison.OrdinalIgnoreCase))
                {
                    string numPart = slotId.Substring("ToolHotbar".Length);
                    if (int.TryParse(numPart, out int parsed))
                        hotbarIndex = parsed - 1; // 1-based ID -> 0-based index
                }
            }

            if (hotbarIndex < 0)
            {
                // Fallback: if it has slot_id and grid position, wire as equipment slot instead
                string slotId = GetMeta(go, "slot_id");
                if (!string.IsNullOrEmpty(slotId))
                    return TryWireEquipmentSlot(go, UIOverrideElementTags.EquipmentSlot, tagComp);

                Debug.LogWarning($"[UIOverrideInventoryWiring] Quick slot '{go.name}' " +
                    "has no hotbar_index or slot_id metadata — skipping");
                return false;
            }

            // Attach hotbar mirror
            var mirror = go.GetComponent<UIOverrideHotbarSlotMirror>();
            if (mirror == null) mirror = go.AddComponent<UIOverrideHotbarSlotMirror>();
            mirror.HotbarIndex = hotbarIndex;
            mirror.Interactive = interactive;

            Debug.Log($"[UIOverrideInventoryWiring] Wired hotbar slot '{go.name}' -> " +
                $"Index={hotbarIndex} Interactive={interactive}");
            return true;
        }

        //  Tool hotbar slot wiring

        private static bool TryWireToolHotbarSlot(GameObject go, string tag, UIBuilderElementTag tagComp)
        {
            if (!string.Equals(tag, UIOverrideElementTags.ToolHotbarSlot, StringComparison.OrdinalIgnoreCase))
                return false;

            int hotbarIndex = GetMetaInt(go, "hotbar_index", -1);
            bool interactive = GetMetaBool(go, "interactive", true);

            if (hotbarIndex < 0)
            {
                string slotId = GetMeta(go, "slot_id");
                if (!string.IsNullOrEmpty(slotId) && slotId.StartsWith("ToolHotbar", StringComparison.OrdinalIgnoreCase))
                {
                    string numPart = slotId.Substring("ToolHotbar".Length);
                    if (int.TryParse(numPart, out int parsed))
                        hotbarIndex = parsed - 1;
                }
            }

            if (hotbarIndex < 0)
            {
                Debug.LogWarning($"[UIOverrideInventoryWiring] Tool hotbar slot '{go.name}' " +
                    "has no hotbar_index or slot_id metadata — skipping");
                return false;
            }

            var mirror = go.GetComponent<UIOverrideHotbarSlotMirror>();
            if (mirror == null) mirror = go.AddComponent<UIOverrideHotbarSlotMirror>();
            mirror.HotbarIndex = hotbarIndex;
            mirror.Interactive = interactive;

            Debug.Log($"[UIOverrideInventoryWiring] Wired tool hotbar slot '{go.name}' -> " +
                $"Index={hotbarIndex} Interactive={interactive}");
            return true;
        }

        private static bool TryWirePlayerPreview(GameObject go, string tag)
        {
            if (!string.Equals(tag, UIOverrideElementTags.PlayerPreview, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(tag, UIOverrideElementTags.CameraDisplay, StringComparison.OrdinalIgnoreCase))
                return false;

            var preview = go.GetComponent<UIOverridePlayerPreview>();
            if (preview == null) preview = go.AddComponent<UIOverridePlayerPreview>();

            Debug.Log($"[UIOverrideInventoryWiring] Wired player preview '{go.name}'");
            return true;
        }

        //  Container panel wiring

        private static bool TryWireContainerPanel(GameObject go, string tag)
        {
            if (!string.Equals(tag, UIOverrideElementTags.ContainerPanel, StringComparison.OrdinalIgnoreCase))
                return false;

            var wiring = go.GetComponent<UIOverrideContainerWiring>();
            if (wiring == null) wiring = go.AddComponent<UIOverrideContainerWiring>();
            wiring.Interactive = GetMetaBool(go, "interactive", true);

            Debug.Log($"[UIOverrideInventoryWiring] Wired container panel '{go.name}'");
            return true;
        }

        //  Drop zone wiring

        private static bool TryWireDropZone(GameObject go, string tag)
        {
            if (!string.Equals(tag, UIOverrideElementTags.DropZone, StringComparison.OrdinalIgnoreCase))
                return false;

            var handler = go.GetComponent<UIOverrideDropZoneHandler>();
            if (handler == null) handler = go.AddComponent<UIOverrideDropZoneHandler>();
            handler.DropToGround = GetMetaBool(go, "drop_to_ground", true);

            Debug.Log($"[UIOverrideInventoryWiring] Wired drop zone '{go.name}'");
            return true;
        }

        //  Status effects wiring

        private static bool TryWireStatusEffects(GameObject go, string tag)
        {
            if (!string.Equals(tag, UIOverrideElementTags.StatusEffectContainer, StringComparison.OrdinalIgnoreCase))
                return false;

            var sync = go.GetComponent<UIOverrideStatusEffectSync>();
            if (sync == null) sync = go.AddComponent<UIOverrideStatusEffectSync>();

            Debug.Log($"[UIOverrideInventoryWiring] Wired status effect container '{go.name}'");
            return true;
        }

        //  Crafting panel wiring

        private static bool TryWireCraftingPanel(GameObject go, string tag)
        {
            if (!string.Equals(tag, UIOverrideElementTags.CraftingPanel, StringComparison.OrdinalIgnoreCase))
                return false;

            var wiring = go.GetComponent<UIOverrideCraftingWiring>();
            if (wiring == null) wiring = go.AddComponent<UIOverrideCraftingWiring>();

            Debug.Log($"[UIOverrideInventoryWiring] Wired crafting panel '{go.name}'");
            return true;
        }

        //  Skills panel wiring

        private static bool TryWireSkillsPanel(GameObject go, string tag)
        {
            if (!string.Equals(tag, UIOverrideElementTags.SkillsPanel, StringComparison.OrdinalIgnoreCase))
                return false;

            var wiring = go.GetComponent<UIOverrideSkillsWiring>();
            if (wiring == null) wiring = go.AddComponent<UIOverrideSkillsWiring>();

            Debug.Log($"[UIOverrideInventoryWiring] Wired skills panel '{go.name}'");
            return true;
        }

        //  Map display wiring

        private static bool TryWireMapDisplay(GameObject go, string tag)
        {
            if (!string.Equals(tag, UIOverrideElementTags.MapDisplay, StringComparison.OrdinalIgnoreCase))
                return false;

            var wiring = go.GetComponent<UIOverrideMapWiring>();
            if (wiring == null) wiring = go.AddComponent<UIOverrideMapWiring>();

            Debug.Log($"[UIOverrideInventoryWiring] Wired map display '{go.name}'");
            return true;
        }

        //  Metadata helpers

        private static string GetMeta(GameObject go, string key)
        {
            var tagComp = go.GetComponent<UIBuilderElementTag>();
            if (tagComp == null || tagComp.Metadata == null) return null;

            for (int i = 0; i < tagComp.Metadata.Count; i++)
            {
                if (string.Equals(tagComp.Metadata[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    return tagComp.Metadata[i].Value;
            }
            return null;
        }

        private static int GetMetaInt(GameObject go, string key, int defaultValue)
        {
            string val = GetMeta(go, key);
            if (string.IsNullOrEmpty(val)) return defaultValue;
            if (int.TryParse(val, out int result)) return result;
            return defaultValue;
        }

        private static bool GetMetaBool(GameObject go, string key, bool defaultValue)
        {
            string val = GetMeta(go, key);
            if (string.IsNullOrEmpty(val)) return defaultValue;
            return string.Equals(val, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(val, "1", StringComparison.OrdinalIgnoreCase);
        }
    }
}
