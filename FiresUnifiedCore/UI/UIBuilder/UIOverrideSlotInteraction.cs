using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using HarmonyLib;

namespace FiresCore.UI
{
    /// <summary>
    /// Handles interactive pointer events (left-click, right-click, drag) on an
    /// override-injected inventory slot, forwarding them to the real InventoryGui
    /// system so the game processes them identically to vanilla slot interactions.
    ///
    /// <b>Design principle:</b> This class does NOT modify inventory state directly.
    /// All inventory mutations go through InventoryGui.OnSelectedItem, BeginDragItem,
    /// or Player.EquipItem/UnequipItem - the same code paths that vanilla uses.
    ///
    /// Attach alongside <see cref="UIOverrideSlotMirror"/> when Interactive=true.
    /// The slot mirror handles visuals; this class handles input forwarding.
    /// </summary>
    public class UIOverrideSlotInteraction : MonoBehaviour,
        IPointerClickHandler, IPointerDownHandler, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        //  Configuration - set by the wiring code

        /// <summary>Slot ID to interact with. Must match a registered UIOverrideSlotSystem.Slot.</summary>
        public string SlotID;

        /// <summary>Grid position fallback when SlotID is empty.</summary>
        public Vector2Int GridPosition = new Vector2Int(-1, -1);

        //  Cached reflection

        private static readonly FieldInfo fi_dragItem =
            AccessTools.Field(typeof(InventoryGui), "m_dragItem");
        private static readonly FieldInfo fi_dragGo =
            AccessTools.Field(typeof(InventoryGui), "m_dragGo");
        private static readonly FieldInfo fi_dragAmount =
            AccessTools.Field(typeof(InventoryGui), "m_dragAmount");
        private static readonly FieldInfo fi_dragInventory =
            AccessTools.Field(typeof(InventoryGui), "m_dragInventory");

        private static readonly MethodInfo mi_onSelectedItem =
            AccessTools.Method(typeof(InventoryGui), "OnSelectedItem");
        // Valheim 1.0 removed BeginDragItem/EndDragItem; the whole drag lifecycle now runs
        // through SetupDragItem(item, inventory, amount) — a real item begins a drag, and
        // (null, null, 1) ends one, which is exactly what vanilla does internally.
        private static readonly MethodInfo mi_setupDragItem =
            AccessTools.Method(typeof(InventoryGui), "SetupDragItem");

        //  Slot resolution (shared with SlotMirror)

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

        private Vector2i GetGridPosition()
        {
            var slot = ResolveSlot();
            if (slot != null) return slot.GridPosition;
            if (GridPosition.x >= 0 && GridPosition.y >= 0)
                return new Vector2i(GridPosition.x, GridPosition.y);
            return new Vector2i(-1, -1);
        }

        //  Left-click - pickup / place via OnSelectedItem

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button == PointerEventData.InputButton.Right)
            {
                HandleRightClick();
                return;
            }

            if (eventData.button != PointerEventData.InputButton.Left) return;
            if (InventoryGui.instance == null) return;

            var slot = ResolveSlot();
            if (slot == null || !slot.IsActive) return;

            var grid = InventoryGui.instance.m_playerGrid;
            if (grid == null) return;

            var pos = slot.GridPosition;
            var inv = UIOverrideSlotSystem.PlayerInventory;
            if (inv == null) return;

            var itemAt = inv.GetItemAt(pos.x, pos.y);

            // Check if there's an active drag item - if so, attempt placement
            var dragItem = fi_dragItem.GetValue(InventoryGui.instance) as ItemDrop.ItemData;
            if (dragItem != null)
            {
                // Validate placement
                if (!slot.IsActive || (!slot.IsFree && itemAt != dragItem))
                    return; // Can't place here - slot occupied by different item
                if (!slot.ItemFits(dragItem))
                    return; // Item doesn't fit this slot type
            }

            // Determine modifier
            InventoryGrid.Modifier modifier = InventoryGrid.Modifier.Select;
            if (ZInput.GetKey(KeyCode.LeftShift) || ZInput.GetKey(KeyCode.RightShift))
                modifier = InventoryGrid.Modifier.Split;
            else if (ZInput.GetKey(KeyCode.LeftControl) || ZInput.GetKey(KeyCode.RightControl))
                modifier = InventoryGrid.Modifier.Move;

            // Forward to vanilla OnSelectedItem
            try
            {
                mi_onSelectedItem?.Invoke(InventoryGui.instance,
                    new object[] { grid, itemAt, pos, modifier });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIOverrideSlotInteraction] OnSelectedItem failed: {ex.Message}");
            }

            UIOverrideEquipmentPanel.MarkDirty();
        }

        //  Right-click - equip/unequip or move to inventory

        private void HandleRightClick()
        {
            if (!InventoryGui.IsVisible()) return;

            var player = Player.m_localPlayer;
            if (player == null) return;

            var slot = ResolveSlot();
            if (slot == null || !slot.IsActive) return;

            var inv = UIOverrideSlotSystem.PlayerInventory;
            if (inv == null) return;

            var pos = slot.GridPosition;
            var item = inv.GetItemAt(pos.x, pos.y);
            if (item == null) return;

            // If equipped, unequip
            if (player.IsItemEquiped(item))
            {
                player.UnequipItem(item, false);
            }

            // Try to move to main inventory
            Vector2i freePos;
            if (UIOverrideSlotSystem.TryFindFreeInventorySlot(out freePos))
            {
                inv.RemoveItem(item);
                item.m_gridPos = freePos;
                inv.AddItem(item);
                inv.m_onChanged?.Invoke();
            }
            else
            {
                player.DropItem(inv, item, item.m_stack);
            }

            UIOverrideEquipmentPanel.MarkDirty();
        }

        //  Drag - begin/end via InventoryGui reflection

        public void OnPointerDown(PointerEventData eventData)
        {
            // Consumed by OnPointerClick; nothing special needed here
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (InventoryGui.instance == null) return;

            var slot = ResolveSlot();
            if (slot == null) return;

            var grid = InventoryGui.instance.m_playerGrid;
            if (grid == null) return;

            var pos = slot.GridPosition;
            var inv = UIOverrideSlotSystem.PlayerInventory;
            if (inv == null) return;

            var itemAt = inv.GetItemAt(pos.x, pos.y);
            if (itemAt == null) return;

            try
            {
                mi_setupDragItem?.Invoke(InventoryGui.instance,
                    new object[] { itemAt, inv, itemAt.m_stack });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIOverrideSlotInteraction] SetupDragItem (begin) failed: {ex.Message}");
            }
        }

        public void OnDrag(PointerEventData eventData)
        {
            // InventoryGui handles drag visual positioning via its own Update loop
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (InventoryGui.instance == null) return;

            try
            {
                mi_setupDragItem?.Invoke(InventoryGui.instance,
                    new object[] { null, null, 1 });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIOverrideSlotInteraction] SetupDragItem (end) failed: {ex.Message}");
            }

            UIOverrideEquipmentPanel.MarkDirty();
        }
    }
}
