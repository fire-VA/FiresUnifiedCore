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
    /// Mirrors a tool hotbar slot onto an injected override element whether or not the inventory is open, showing the
    /// keybind label and forwarding hotkey activation to the existing ToolBar and HotbarManager. A parallel visual
    /// only; it never changes inventory state.
    /// </summary>
    public class UIOverrideHotbarSlotMirror : MonoBehaviour
    {
        //  Configuration

        /// <summary>
        /// Index into Slots.toolHotbarSlots (0-based).
        /// </summary>
        public int HotbarIndex = -1;

        /// <summary>
        /// If true, clicking this element activates the slot (equip/unequip).
        /// If false, purely visual.
        /// </summary>
        public bool Interactive = false;

        //  Cached child references

        private Image _icon;
        private Image _bkgImage;
        private TMP_Text _amount;
        private TMP_Text _bindingLabel;
        private Transform _equippedOverlay;
        private Transform _durabilityOverlay;

        private bool _initialized;
        private int _lastUpdateFrame = -1;

        //  Init

        private void Start()
        {
            CacheChildReferences();
            _initialized = true;

            if (Interactive)
                WireClickHandler();
        }

        private void CacheChildReferences()
        {
            var root = transform;
            var iconTransform = root.Find("icon");
            if (iconTransform != null) _icon = iconTransform.GetComponent<Image>();

            var backgroundTransform = root.Find("bkg") ?? root.Find("background") ?? root.Find("Bkg");
            if (backgroundTransform != null) _bkgImage = backgroundTransform.GetComponent<Image>();

            var amountTransform = root.Find("amount");
            if (amountTransform != null) _amount = amountTransform.GetComponent<TMP_Text>();

            var bindingT = root.Find("binding");
            if (bindingT != null) _bindingLabel = bindingT.GetComponent<TMP_Text>();

            _equippedOverlay = root.Find("equiped") ?? root.Find("equipped");
            _durabilityOverlay = root.Find("durability");
        }

        private void WireClickHandler()
        {
            var btn = GetComponent<Button>();
            if (btn == null) btn = gameObject.AddComponent<Button>();

            btn.onClick.RemoveAllListeners();
            int idx = HotbarIndex; // Capture for closure
            btn.onClick.AddListener(() =>
            {
                var manager = UIOverrideHotbarManager.Instance;
                if (manager == null)
                    manager = UIOverrideHotbarManager.EnsureInstance();
                if (manager == null) return;

                InventoryGrid.Modifier modifier = InventoryGrid.Modifier.Select;
                if (ZInput.GetKey(KeyCode.LeftShift) || ZInput.GetKey(KeyCode.RightShift))
                    modifier = InventoryGrid.Modifier.Split;
                else if (ZInput.GetKey(KeyCode.LeftControl) || ZInput.GetKey(KeyCode.RightControl))
                    modifier = InventoryGrid.Modifier.Move;

                manager.ActivateSlotByIndex(idx, modifier);
            });
        }

        //  Per-frame visual sync

        private void LateUpdate()
        {
            if (!_initialized) return;
            if (Time.frameCount == _lastUpdateFrame) return;
            _lastUpdateFrame = Time.frameCount;

            if (Player.m_localPlayer == null) return;

            var slot = ResolveSlot();
            if (slot == null)
            {
                ClearVisuals();
                return;
            }

            var item = slot.Item;
            SyncIcon(item);
            SyncAmount(item);
            SyncOverlays(item);
            SyncBinding(slot);
        }

        //  Slot resolution

        private UIOverrideSlotSystem.Slot ResolveSlot()
        {
            if (HotbarIndex < 0) return null;
            if (UIOverrideSlotSystem.toolHotbarSlots == null || HotbarIndex >= UIOverrideSlotSystem.toolHotbarSlots.Count)
                return null;
            return UIOverrideSlotSystem.toolHotbarSlots[HotbarIndex];
        }

        //  Visual sync helpers

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
                _icon.enabled = true;
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

            if (_equippedOverlay != null)
            {
                bool equipped = item != null && player != null && player.IsItemEquiped(item);
                _equippedOverlay.gameObject.SetActive(equipped);
            }

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
        }

        private void SyncBinding(UIOverrideSlotSystem.Slot slot)
        {
            if (_bindingLabel == null) return;

            // Show keybind text (e.g., "Alt+1")
            string shortcutText = slot.GetShortcutText();
            if (!string.IsNullOrEmpty(shortcutText))
            {
                // Strip "Alpha" prefix for cleaner display
                if (shortcutText.StartsWith("Alpha", StringComparison.OrdinalIgnoreCase))
                    shortcutText = shortcutText.Substring(5);

                _bindingLabel.text = shortcutText;
                _bindingLabel.enabled = true;
                _bindingLabel.color = Color.cyan;
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
            if (_equippedOverlay != null) _equippedOverlay.gameObject.SetActive(false);
            if (_durabilityOverlay != null) _durabilityOverlay.gameObject.SetActive(false);
            if (_bindingLabel != null) { _bindingLabel.text = ""; _bindingLabel.enabled = false; }
        }
    }
}
