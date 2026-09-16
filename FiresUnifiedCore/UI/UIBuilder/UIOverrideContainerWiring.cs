using System;
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
    /// Fills a tagged container panel while a chest or ship is open: the slot grid, name and weight, plus Take All
    /// and Stack All buttons, found by their override_container_* tags. Attached to a
    /// <see cref="UIOverrideElementTags.ContainerPanel"/> directly or through <see cref="UIOverrideInventoryWiring"/>.
    /// </summary>
    public class UIOverrideContainerWiring : MonoBehaviour
    {
        public float CellSize = 70f;
        public float Spacing = 2f;
        public bool Interactive = true;

        private Container _currentContainer;
        private Inventory _containerInventory;

        private Transform _gridRoot;
        private TMP_Text _nameText;
        private TMP_Text _weightText;
        private Button _takeAllBtn;
        private Button _stackAllBtn;

        private readonly List<GameObject> _slotGOs = new List<GameObject>();
        private int _lastWidth = -1;
        private int _lastHeight = -1;

        private static readonly FieldInfo fi_currentContainer =
            AccessTools.Field(typeof(InventoryGui), "m_currentContainer");
        private static readonly FieldInfo fi_onChanged =
            AccessTools.Field(typeof(Inventory), "m_onChanged");

        private void Start()
        {
            DiscoverChildren();
            WireButtons();
        }

        private void OnEnable()
        {
            RefreshContainer();
        }

        private void Update()
        {
            var container = GetActiveContainer();

            if (container != _currentContainer)
            {
                _currentContainer = container;
                _containerInventory = container != null ? container.GetInventory() : null;
                RebuildGrid();
            }

            bool hasContainer = _containerInventory != null;
            if (_gridRoot != null && _gridRoot.gameObject.activeSelf != hasContainer)
                _gridRoot.gameObject.SetActive(hasContainer);

            if (!hasContainer) return;

            SyncNameAndWeight();

            // Check for dimension changes
            int width = _containerInventory.GetWidth();
            int height = _containerInventory.GetHeight();
            if (width != _lastWidth || height != _lastHeight)
                RebuildGrid();
        }

        //  Child discovery

        private void DiscoverChildren()
        {
            foreach (Transform child in transform)
            {
                var tag = child.GetComponent<UIBuilderElementTag>();
                if (tag == null) continue;

                string tagName = tag.Tag;
                if (string.IsNullOrEmpty(tagName)) continue;

                if (string.Equals(tagName, UIOverrideElementTags.ContainerGrid, StringComparison.OrdinalIgnoreCase))
                    _gridRoot = child;
                else if (string.Equals(tagName, UIOverrideElementTags.ContainerName, StringComparison.OrdinalIgnoreCase))
                    _nameText = child.GetComponent<TMP_Text>();
                else if (string.Equals(tagName, UIOverrideElementTags.ContainerWeight, StringComparison.OrdinalIgnoreCase))
                    _weightText = child.GetComponent<TMP_Text>();
                else if (string.Equals(tagName, UIOverrideElementTags.ContainerTakeAll, StringComparison.OrdinalIgnoreCase))
                    _takeAllBtn = child.GetComponent<Button>();
                else if (string.Equals(tagName, UIOverrideElementTags.ContainerStackAll, StringComparison.OrdinalIgnoreCase))
                    _stackAllBtn = child.GetComponent<Button>();
            }

            // Fallback: use this transform as grid root if no child tagged
            if (_gridRoot == null)
                _gridRoot = transform;
        }

        //  Button wiring

        private void WireButtons()
        {
            if (_takeAllBtn != null)
            {
                _takeAllBtn.onClick.RemoveAllListeners();
                _takeAllBtn.onClick.AddListener(OnTakeAll);
            }
            if (_stackAllBtn != null)
            {
                _stackAllBtn.onClick.RemoveAllListeners();
                _stackAllBtn.onClick.AddListener(OnStackAll);
            }
        }

        private void OnTakeAll()
        {
            if (_containerInventory == null || Player.m_localPlayer == null) return;
            var playerInv = Player.m_localPlayer.GetInventory();
            if (playerInv == null) return;

            // Use the same approach as vanilla: move items one by one
            var items = new List<ItemDrop.ItemData>(_containerInventory.GetAllItems());
            foreach (var item in items)
            {
                if (playerInv.AddItem(item))
                    _containerInventory.RemoveItem(item);
            }
            UIOverrideEquipmentPanel.MarkDirty();
        }

        private void OnStackAll()
        {
            if (_containerInventory == null || Player.m_localPlayer == null) return;
            var playerInv = Player.m_localPlayer.GetInventory();
            if (playerInv == null) return;

            // Move items that can stack with existing player items
            var items = new List<ItemDrop.ItemData>(_containerInventory.GetAllItems());
            foreach (var item in items)
            {
                // Find matching item in player inventory
                ItemDrop.ItemData existing = null;
                foreach (var playerItem in playerInv.GetAllItems())
                {
                    if (playerItem.m_shared.m_name == item.m_shared.m_name &&
                        playerItem.m_quality == item.m_quality &&
                        playerItem.m_stack < playerItem.m_shared.m_maxStackSize)
                    {
                        existing = playerItem;
                        break;
                    }
                }

                if (existing != null)
                {
                    int spaceInStack = existing.m_shared.m_maxStackSize - existing.m_stack;
                    int toMove = Mathf.Min(item.m_stack, spaceInStack);
                    existing.m_stack += toMove;
                    if (toMove >= item.m_stack)
                        _containerInventory.RemoveItem(item);
                    else
                        item.m_stack -= toMove;
                }
            }

            // Fire changed events via reflection
            FireChanged(_containerInventory);
            FireChanged(playerInv);
            UIOverrideEquipmentPanel.MarkDirty();
        }

        private static void FireChanged(Inventory inv)
        {
            try
            {
                var onChanged = fi_onChanged?.GetValue(inv) as Action;
                onChanged?.Invoke();
            }
            catch { }
        }

        //  Container resolution

        private Container GetActiveContainer()
        {
            if (InventoryGui.instance == null) return null;
            try
            {
                return fi_currentContainer?.GetValue(InventoryGui.instance) as Container;
            }
            catch
            {
                return null;
            }
        }

        private void RefreshContainer()
        {
            _currentContainer = GetActiveContainer();
            _containerInventory = _currentContainer != null ? _currentContainer.GetInventory() : null;
            RebuildGrid();
        }

        //  Name and weight sync

        private void SyncNameAndWeight()
        {
            if (_containerInventory == null) return;

            if (_nameText != null && _currentContainer != null)
            {
                string name = _currentContainer.GetHoverName();
                if (_nameText.text != name)
                    _nameText.text = name;
            }

            if (_weightText != null)
            {
                string weight = $"{_containerInventory.GetTotalWeight():F1}";
                if (_weightText.text != weight)
                    _weightText.text = weight;
            }
        }

        //  Grid rebuild

        private void RebuildGrid()
        {
            foreach (var go in _slotGOs)
            {
                if (go != null) Destroy(go);
            }
            _slotGOs.Clear();

            if (_containerInventory == null)
            {
                _lastWidth = -1;
                _lastHeight = -1;
                return;
            }

            int width = _containerInventory.GetWidth();
            int height = _containerInventory.GetHeight();
            _lastWidth = width;
            _lastHeight = height;

            var gridLayout = _gridRoot.GetComponent<GridLayoutGroup>();
            if (gridLayout == null) gridLayout = _gridRoot.gameObject.AddComponent<GridLayoutGroup>();
            gridLayout.cellSize = new Vector2(CellSize, CellSize);
            gridLayout.spacing = new Vector2(Spacing, Spacing);
            gridLayout.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            gridLayout.constraintCount = width;
            gridLayout.startCorner = GridLayoutGroup.Corner.UpperLeft;
            gridLayout.startAxis = GridLayoutGroup.Axis.Horizontal;
            gridLayout.childAlignment = TextAnchor.UpperLeft;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    var slotGO = CreateContainerSlotElement($"CSlot_{x}_{y}", x, y);
                    _slotGOs.Add(slotGO);
                }
            }

            Debug.Log($"[UIOverrideContainerWiring] Built {width}x{height} container grid ({_slotGOs.Count} slots)");
        }

        private GameObject CreateContainerSlotElement(string name, int gridX, int gridY)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(_gridRoot, false);

            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(CellSize, CellSize);

            // Background
            var image = go.AddComponent<Image>();
            image.color = new Color(0f, 0f, 0f, 0.3f);
            image.raycastTarget = true;

            // Icon child
            var iconGO = new GameObject("icon", typeof(RectTransform));
            iconGO.transform.SetParent(go.transform, false);
            var iconRect = iconGO.GetComponent<RectTransform>();
            iconRect.anchorMin = Vector2.zero;
            iconRect.anchorMax = Vector2.one;
            iconRect.offsetMin = new Vector2(4, 4);
            iconRect.offsetMax = new Vector2(-4, -4);
            var iconImg = iconGO.AddComponent<Image>();
            iconImg.color = Color.clear;
            iconImg.raycastTarget = false;

            // Amount text child
            var amountGO = new GameObject("amount", typeof(RectTransform));
            amountGO.transform.SetParent(go.transform, false);
            var amountRect = amountGO.GetComponent<RectTransform>();
            amountRect.anchorMin = new Vector2(0.5f, 0f);
            amountRect.anchorMax = new Vector2(1f, 0.3f);
            amountRect.offsetMin = Vector2.zero;
            amountRect.offsetMax = Vector2.zero;
            var amountText = amountGO.AddComponent<TextMeshProUGUI>();
            amountText.fontSize = 12;
            amountText.alignment = TextAlignmentOptions.BottomRight;
            amountText.color = Color.white;
            amountText.raycastTarget = false;
            amountText.text = "";

            // Container slot mirror
            var mirror = go.AddComponent<UIOverrideContainerSlotMirror>();
            mirror.ContainerWiring = this;
            mirror.GridX = gridX;
            mirror.GridY = gridY;

            // Interaction
            if (Interactive)
            {
                var interaction = go.AddComponent<UIOverrideContainerSlotInteraction>();
                interaction.ContainerWiring = this;
                interaction.GridX = gridX;
                interaction.GridY = gridY;
            }

            return go;
        }

        /// <summary>
        /// Returns the current container inventory, or null if no container is open.
        /// </summary>
        public Inventory GetContainerInventory() => _containerInventory;

        /// <summary>
        /// Returns the vanilla container grid from InventoryGui via the public property.
        /// </summary>
        public InventoryGrid GetContainerGrid()
        {
            if (InventoryGui.instance == null) return null;
            return InventoryGui.instance.ContainerGrid;
        }

        private void OnDestroy()
        {
            foreach (var go in _slotGOs)
            {
                if (go != null) Destroy(go);
            }
            _slotGOs.Clear();
        }
    }

    /// <summary>
    /// Per-frame visual mirror for a single container slot. Reads item data from
    /// the container inventory and updates icon/amount visuals.
    /// </summary>
    public class UIOverrideContainerSlotMirror : MonoBehaviour
    {
        public UIOverrideContainerWiring ContainerWiring;
        public int GridX;
        public int GridY;

        private Image _icon;
        private TMP_Text _amount;
        private int _lastUpdateFrame = -1;

        private void Start()
        {
            var iconTransform = transform.Find("icon");
            if (iconTransform != null) _icon = iconTransform.GetComponent<Image>();
            var amountTransform = transform.Find("amount");
            if (amountTransform != null) _amount = amountTransform.GetComponent<TMP_Text>();
        }

        private void LateUpdate()
        {
            if (Time.frameCount == _lastUpdateFrame) return;
            _lastUpdateFrame = Time.frameCount;

            var inv = ContainerWiring != null ? ContainerWiring.GetContainerInventory() : null;
            if (inv == null)
            {
                ClearVisuals();
                return;
            }

            var item = inv.GetItemAt(GridX, GridY);
            SyncIcon(item);
            SyncAmount(item);
        }

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

        private void ClearVisuals()
        {
            if (_icon != null) { _icon.sprite = null; _icon.color = Color.clear; }
            if (_amount != null) { _amount.text = ""; _amount.enabled = false; }
        }
    }

    /// <summary>
    /// Handles click interactions on container slots by forwarding to
    /// InventoryGui's container grid via OnSelectedItem.
    /// </summary>
    public class UIOverrideContainerSlotInteraction : MonoBehaviour,
        IPointerClickHandler
    {
        public UIOverrideContainerWiring ContainerWiring;
        public int GridX;
        public int GridY;

        private static readonly MethodInfo mi_onSelectedItem =
            AccessTools.Method(typeof(InventoryGui), "OnSelectedItem");

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            if (InventoryGui.instance == null || ContainerWiring == null) return;

            var containerGrid = ContainerWiring.GetContainerGrid();
            if (containerGrid == null) return;

            var inv = ContainerWiring.GetContainerInventory();
            if (inv == null) return;

            var pos = new Vector2i(GridX, GridY);
            var item = inv.GetItemAt(GridX, GridY);

            InventoryGrid.Modifier modifier = InventoryGrid.Modifier.Select;
            if (ZInput.GetKey(KeyCode.LeftShift) || ZInput.GetKey(KeyCode.RightShift))
                modifier = InventoryGrid.Modifier.Split;
            else if (ZInput.GetKey(KeyCode.LeftControl) || ZInput.GetKey(KeyCode.RightControl))
                modifier = InventoryGrid.Modifier.Move;

            try
            {
                mi_onSelectedItem?.Invoke(InventoryGui.instance,
                    new object[] { containerGrid, item, pos, modifier });
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIOverrideContainerSlotInteraction] OnSelectedItem failed: {ex.Message}");
            }

            UIOverrideEquipmentPanel.MarkDirty();
        }
    }
}
