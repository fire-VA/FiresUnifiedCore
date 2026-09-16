using System;
using System.Reflection;
using UnityEngine;
using HarmonyLib;

namespace FiresCore.UI
{
    /// <summary>
    /// Self-contained hotbar manager for the UI override system.
    /// Handles activating tool hotbar slots by index — equipping/unequipping
    /// items through the vanilla game API. Does NOT depend on any external
    /// inventory mod's HotbarManager or ToolBar classes.
    /// </summary>
    public class UIOverrideHotbarManager : MonoBehaviour
    {
        public static UIOverrideHotbarManager Instance { get; private set; }

        private static readonly MethodInfo mi_onSelectedItem =
            AccessTools.Method(typeof(InventoryGui), "OnSelectedItem");

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Activates a tool hotbar slot by index. This equips/unequips the item
        /// through the vanilla InventoryGui.OnSelectedItem pathway, the same as
        /// clicking a slot in the inventory grid.
        /// </summary>
        public void ActivateSlotByIndex(int index, InventoryGrid.Modifier modifier)
        {
            if (UIOverrideSlotSystem.toolHotbarSlots == null || index < 0 ||
                index >= UIOverrideSlotSystem.toolHotbarSlots.Count)
                return;

            var slot = UIOverrideSlotSystem.toolHotbarSlots[index];
            if (slot == null || !slot.IsActive) return;

            var player = Player.m_localPlayer;
            if (player == null) return;

            var inv = player.GetInventory();
            if (inv == null) return;

            var item = slot.Item;
            if (item == null) return;

            // If InventoryGui is available, use OnSelectedItem for full vanilla handling
            if (InventoryGui.instance != null && InventoryGui.instance.m_playerGrid != null)
            {
                try
                {
                    mi_onSelectedItem?.Invoke(InventoryGui.instance, new object[]
                    {
                        InventoryGui.instance.m_playerGrid,
                        item,
                        slot.GridPosition,
                        modifier
                    });
                    UIOverrideEquipmentPanel.MarkDirty();
                    return;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UIOverrideHotbarManager] OnSelectedItem failed: {ex.Message}");
                }
            }

            // Fallback: direct equip/unequip via Humanoid API
            try
            {
                if (player.IsItemEquiped(item))
                    player.UnequipItem(item, false);
                else
                    player.EquipItem(item, false);

                UIOverrideEquipmentPanel.MarkDirty();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIOverrideHotbarManager] Direct equip/unequip failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures a singleton instance exists. Called by the wiring system
        /// when hotbar elements are detected in the override layout.
        /// </summary>
        public static UIOverrideHotbarManager EnsureInstance()
        {
            if (Instance != null) return Instance;

            var go = new GameObject("UIOverrideHotbarManager");
            DontDestroyOnLoad(go);
            return go.AddComponent<UIOverrideHotbarManager>();
        }
    }
}
