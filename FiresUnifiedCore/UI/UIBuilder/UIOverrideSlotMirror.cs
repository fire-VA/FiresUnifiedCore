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
    /// Wires injected inventory slot visuals created by the UI override system to live
    /// game inventory data. This class is a READ-ONLY visual mirror — it never modifies
    /// inventory state, grid positions, or item data.
    ///
    /// <b>Design principle:</b> The UIOverrideSlotSystem owns slot registration and lookup.
    /// This class only reads slot/item state and updates visual properties (icon sprite,
    /// amount text, overlay active states) on injected GameObjects that were instantiated
    /// from layout data by the override system.
    ///
    /// Attach to a GameObject tagged with <see cref="UIOverrideElementTags.EquipmentSlot"/>
    /// or <see cref="UIOverrideElementTags.EquipmentPanel"/>.
    ///
    /// <b>What this does:</b>
    /// - Finds the matching Slot from the live Slots system by grid position or slot ID
    /// - Per-frame: copies icon, amount, overlay states from the slot's item data
    /// - Per-frame: syncs overlay children (durability, equipped, quality, etc.)
    /// - Shows slot hint sprite/label when empty
    ///
    /// <b>What this does NOT do:</b>
    /// - Does NOT modify Inventory.m_height or InventoryGrid.m_elements
    /// - Does NOT set m_gridPos on items or call AddItem/RemoveItem
    /// - Does NOT create InventoryGrid.Element objects
    /// - Does NOT handle drag/drop or equip/unequip — those go through
    ///   UIOverrideSlotInteraction or InventoryGui methods
    /// </summary>
    public class UIOverrideSlotMirror : MonoBehaviour
    {
        // ???????????????????????????????????????
        //  Configuration — set by the wiring code
        // ???????????????????????????????????????

        /// <summary>
        /// The slot ID to mirror (e.g., "Helmet", "Food", "Ammo", "Misc_0_0").
        /// Takes priority over GridPosition for slot resolution.
        /// </summary>
        public string SlotID;

        /// <summary>
        /// The grid position to mirror. Used as fallback when SlotID is empty.
        /// </summary>
        public Vector2Int GridPosition = new Vector2Int(-1, -1);

        /// <summary>
        /// If true, this mirror also forwards pointer events (click, right-click, drag)
        /// to the InventoryGui system so the user can interact with the slot.
        /// If false, the element is purely visual (read-only display).
        /// </summary>
        public bool Interactive = false;

        // ???????????????????????????????????????
        //  Cached child references
        // ???????????????????????????????????????

        private Image _icon;
        private TMP_Text _amount;
        private Transform _durabilityOverlay;
        private Transform _equippedOverlay;
        private Transform _qualityOverlay;
        private Transform _foodIconOverlay;
        private Transform _noteleportOverlay;
        private Transform _selectedOverlay;
        private TMP_Text _bindingLabel;

        private bool _initialized;
        private int _lastUpdateFrame = -1;

        // ???????????????????????????????????????
        //  Init
        // ???????????????????????????????????????

        private void Start()
        {
            CacheChildReferences();
            _initialized = true;
        }

        private void CacheChildReferences()
        {
            var t = transform;

            // Icon — the main item display image
            var iconT = t.Find("icon");
            if (iconT != null) _icon = iconT.GetComponent<Image>();

            // Amount text
            var amountT = t.Find("amount");
            if (amountT != null) _amount = amountT.GetComponent<TMP_Text>();

            // Overlays
            _durabilityOverlay = t.Find("durability");
            _equippedOverlay = t.Find("equiped") ?? t.Find("equipped");
            _qualityOverlay = t.Find("quality");
            _foodIconOverlay = t.Find("foodicon");
            _noteleportOverlay = t.Find("noteleport");
            _selectedOverlay = t.Find("selected");

            // Binding / label
            var bindingT = t.Find("binding");
            if (bindingT != null) _bindingLabel = bindingT.GetComponent<TMP_Text>();
        }

        // ???????????????????????????????????????
        //  Per-frame visual sync
        // ???????????????????????????????????????

        private void LateUpdate()
        {
            if (!_initialized) return;
            if (Time.frameCount == _lastUpdateFrame) return;
            _lastUpdateFrame = Time.frameCount;

            if (Player.m_localPlayer == null) return;

            var slot = ResolveSlot();
            if (slot == null)
            {
                // No matching slot — clear visuals
                ClearVisuals();
                return;
            }

            var item = slot.Item;
            SyncIcon(item);
            SyncAmount(item);
            SyncOverlays(item);
            SyncBinding(slot, item);
        }

        // ???????????????????????????????????????
        //  Slot resolution
        // ???????????????????????????????????????

        private UIOverrideSlotSystem.Slot ResolveSlot()
        {
            // Priority 1: by slot ID
            if (!string.IsNullOrEmpty(SlotID))
            {
                var byId = UIOverrideSlotSystem.GetSlotByID(SlotID);
                if (byId != null && byId.IsActive) return byId;
            }

            // Priority 2: by grid position
            if (GridPosition.x >= 0 && GridPosition.y >= 0)
            {
                var pos = new Vector2i(GridPosition.x, GridPosition.y);
                return UIOverrideSlotSystem.GetSlotInGrid(pos);
            }

            return null;
        }

        // ???????????????????????????????????????
        //  Visual sync helpers
        // ???????????????????????????????????????

        private void SyncIcon(ItemDrop.ItemData item)
        {
            if (_icon == null) return;

            if (item != null && item.m_shared != null &&
                item.m_shared.m_icons != null && item.m_shared.m_icons.Length > 0)
            {
                _icon.sprite = item.m_shared.m_icons[0];
                _icon.color = Color.white;
                _icon.enabled = true;
            }
            else
            {
                _icon.sprite = null;
                _icon.color = Color.clear;
                _icon.enabled = true; // Keep enabled so layout doesn't shift
            }
        }

        private void SyncAmount(ItemDrop.ItemData item)
        {
            if (_amount == null) return;

            if (item != null && item.m_stack > 1)
            {
                _amount.text = item.m_stack.ToString();
                _amount.enabled = true;
            }
            else
            {
                _amount.text = "";
                _amount.enabled = false;
            }
        }

        private void SyncOverlays(ItemDrop.ItemData item)
        {
            var player = Player.m_localPlayer;

            // Durability
            if (_durabilityOverlay != null)
            {
                bool show = item != null && item.m_shared != null && item.m_shared.m_useDurability;
                _durabilityOverlay.gameObject.SetActive(show);
                if (show)
                {
                    var bar = _durabilityOverlay.GetComponent<GuiBar>();
                    if (bar != null && item.m_shared.m_maxDurability > 0)
                        bar.SetValue(item.m_durability / item.m_shared.m_maxDurability);
                }
            }

            // Equipped
            if (_equippedOverlay != null)
            {
                bool equipped = item != null && player != null && player.IsItemEquiped(item);
                _equippedOverlay.gameObject.SetActive(equipped);
            }

            // Quality
            if (_qualityOverlay != null)
            {
                bool show = item != null && item.m_quality > 1;
                _qualityOverlay.gameObject.SetActive(show);
                if (show)
                {
                    var qt = _qualityOverlay.GetComponent<TMP_Text>();
                    if (qt != null) qt.text = item.m_quality.ToString();
                }
            }

            // Food icon
            if (_foodIconOverlay != null)
            {
                bool show = item != null && item.m_shared != null && item.m_shared.m_food > 0;
                _foodIconOverlay.gameObject.SetActive(show);
            }

            // No teleport
            if (_noteleportOverlay != null)
            {
                bool show = item != null && item.m_shared != null && !item.m_shared.m_teleportable;
                _noteleportOverlay.gameObject.SetActive(show);
            }

            // Selected — never show (avoid stale highlight)
            if (_selectedOverlay != null)
                _selectedOverlay.gameObject.SetActive(false);
        }

        private void SyncBinding(UIOverrideSlotSystem.Slot slot, ItemDrop.ItemData item)
        {
            if (_bindingLabel == null) return;

            if (item == null && slot != null)
            {
                // Show slot name as hint for empty slots
                _bindingLabel.text = slot.Name;
                _bindingLabel.enabled = true;
                _bindingLabel.color = Color.white * 0.5f;
            }
            else
            {
                _bindingLabel.text = "";
                _bindingLabel.enabled = false;
            }
        }

        private void ClearVisuals()
        {
            if (_icon != null) { _icon.sprite = null; _icon.color = Color.clear; }
            if (_amount != null) { _amount.text = ""; _amount.enabled = false; }
            if (_durabilityOverlay != null) _durabilityOverlay.gameObject.SetActive(false);
            if (_equippedOverlay != null) _equippedOverlay.gameObject.SetActive(false);
            if (_qualityOverlay != null) _qualityOverlay.gameObject.SetActive(false);
            if (_foodIconOverlay != null) _foodIconOverlay.gameObject.SetActive(false);
            if (_noteleportOverlay != null) _noteleportOverlay.gameObject.SetActive(false);
            if (_selectedOverlay != null) _selectedOverlay.gameObject.SetActive(false);
        }
    }
}
