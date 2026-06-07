using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using HarmonyLib;

namespace FiresCore.UI
{
    /// <summary>
    /// Detects when a dragged item is released outside any valid slot area and
    /// drops it on the ground. Attach to the full-screen background panel behind
    /// the inventory UI, or to any large panel that should act as a "drop zone".
    ///
    /// When a pointer-up event occurs while InventoryGui has an active drag item,
    /// this handler checks whether the cursor is over any registered slot element.
    /// If not, the item is dropped via <c>Player.DropItem()</c>.
    ///
    /// This also handles the case where the user drags an item off the edge of
    /// the inventory window entirely.
    /// </summary>
    public class UIOverrideDropZoneHandler : MonoBehaviour, IPointerUpHandler
    {
        /// <summary>
        /// If true, dropping on this zone drops the item to the ground.
        /// If false, dropping on this zone cancels the drag (returns item to original slot).
        /// </summary>
        public bool DropToGround = true;

        private static readonly FieldInfo fi_dragItem =
            AccessTools.Field(typeof(InventoryGui), "m_dragItem");
        private static readonly FieldInfo fi_dragGo =
            AccessTools.Field(typeof(InventoryGui), "m_dragGo");
        private static readonly FieldInfo fi_dragAmount =
            AccessTools.Field(typeof(InventoryGui), "m_dragAmount");
        private static readonly FieldInfo fi_dragInventory =
            AccessTools.Field(typeof(InventoryGui), "m_dragInventory");

        public void OnPointerUp(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            if (InventoryGui.instance == null) return;

            var dragItem = fi_dragItem?.GetValue(InventoryGui.instance) as ItemDrop.ItemData;
            if (dragItem == null) return;

            var dragInventory = fi_dragInventory?.GetValue(InventoryGui.instance) as Inventory;

            if (DropToGround)
            {
                // Drop the item on the ground
                var player = Player.m_localPlayer;
                if (player != null && dragInventory != null)
                {
                    int amount = 1;
                    try
                    {
                        var amountObj = fi_dragAmount?.GetValue(InventoryGui.instance);
                        if (amountObj != null) amount = (int)amountObj;
                    }
                    catch { }

                    try
                    {
                        player.DropItem(dragInventory, dragItem, amount);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[UIOverrideDropZoneHandler] DropItem failed: {ex.Message}");
                    }
                }
            }

            // Clear the drag state
            UIOverrideDragGhostManager.ClearDrag();
            eventData.Use();
        }
    }
}
