using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace FiresCore.UI
{
    /// <summary>
    /// Shows vanilla Valheim tooltips when the pointer hovers over an override
    /// inventory slot. Reads the slot's item data and displays item name + tooltip
    /// text via <see cref="UITooltip"/>. When the slot is empty, shows a hint
    /// describing the slot type (e.g., "Helmet Slot — Equip a helmet here").
    ///
    /// Attach alongside <see cref="UIOverrideSlotMirror"/> on any interactive
    /// equipment/inventory slot element. The wiring system attaches this
    /// automatically when interactive=true.
    /// </summary>
    public class UIOverrideSlotTooltipHandler : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler
    {
        /// <summary>Slot ID to show tooltip for.</summary>
        public string SlotID;

        /// <summary>Grid position fallback.</summary>
        public Vector2Int GridPosition = new Vector2Int(-1, -1);

        private UITooltip _tooltip;
        private bool _hovering;

        private void Start()
        {
            _tooltip = GetComponent<UITooltip>();
            if (_tooltip == null)
                _tooltip = gameObject.AddComponent<UITooltip>();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hovering = true;

            if (InventoryGui.instance == null || InventoryGui.instance.m_playerGrid == null)
                return;

            var slot = ResolveSlot();
            if (slot == null) return;

            var anchor = InventoryGui.instance.m_playerGrid.m_tooltipAnchor;
            var item = slot.Item;

            if (item != null)
            {
                _tooltip.Set(item.m_shared.m_name, item.GetTooltip(), anchor);
            }
            else
            {
                // Show slot type hint for empty slots
                string topic = GetSlotHintTopic(slot);
                string desc = GetSlotHintDescription(slot);
                if (!string.IsNullOrEmpty(topic))
                    _tooltip.Set(topic, desc, anchor);
            }
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hovering = false;

            if (InventoryGui.instance != null && InventoryGui.instance.m_playerGrid != null)
                InventoryGui.instance.m_playerGrid.m_tooltipAnchor.gameObject.SetActive(false);
        }

        private UIOverrideSlotSystem.Slot ResolveSlot()
        {
            if (!string.IsNullOrEmpty(SlotID))
            {
                var byId = UIOverrideSlotSystem.GetSlotByID(SlotID);
                if (byId != null && byId.IsActive) return byId;
            }

            if (GridPosition.x >= 0 && GridPosition.y >= 0)
            {
                var pos = new Vector2i(GridPosition.x, GridPosition.y);
                return UIOverrideSlotSystem.GetSlotInGrid(pos);
            }

            return null;
        }

        private static string GetSlotHintTopic(UIOverrideSlotSystem.Slot slot)
        {
            if (slot == null || string.IsNullOrEmpty(slot.ID)) return null;

            switch (slot.ID)
            {
                case UIOverrideSlotSystem.HelmetSlotID: return "Helmet Slot";
                case UIOverrideSlotSystem.ChestSlotID: return "Chest Slot";
                case UIOverrideSlotSystem.LegsSlotID: return "Legs Slot";
                case UIOverrideSlotSystem.ShoulderSlotID: return "Shoulder Slot";
                case UIOverrideSlotSystem.UtilitySlotID: return "Utility Slot";
                case UIOverrideSlotSystem.TrinketSlotID: return "Trinket Slot";
                case UIOverrideSlotSystem.FoodSlotID: return "Food Slot";
                case UIOverrideSlotSystem.AmmoSlotID: return "Ammo Slot";
                default:
                    if (slot.IsMiscSlot) return "Misc Slot";
                    if (slot.IsQuickSlot) return "Quick Slot";
                    if (slot.IsHotbarSlot) return $"Tool Slot";
                    if (slot.IsEquipmentSlot) return "Equipment Slot";
                    return "Inventory Slot";
            }
        }

        private static string GetSlotHintDescription(UIOverrideSlotSystem.Slot slot)
        {
            if (slot == null || string.IsNullOrEmpty(slot.ID)) return "";

            switch (slot.ID)
            {
                case UIOverrideSlotSystem.HelmetSlotID: return "Equip a helmet here.";
                case UIOverrideSlotSystem.ChestSlotID: return "Equip chest armor here.";
                case UIOverrideSlotSystem.LegsSlotID: return "Equip leg armor here.";
                case UIOverrideSlotSystem.ShoulderSlotID: return "Equip a cape or shoulder armor here.";
                case UIOverrideSlotSystem.UtilitySlotID: return "Equip a utility item here (belt, wishbone, etc.).";
                case UIOverrideSlotSystem.TrinketSlotID: return "Equip a trinket here.";
                case UIOverrideSlotSystem.FoodSlotID: return "Place food here for quick access.";
                case UIOverrideSlotSystem.AmmoSlotID: return "Place ammunition here.";
                default:
                    if (slot.IsMiscSlot) return "Store any item here.";
                    if (slot.IsHotbarSlot) return $"Place a tool here. {slot.GetShortcutText()}";
                    return "An inventory slot.";
            }
        }
    }
}
