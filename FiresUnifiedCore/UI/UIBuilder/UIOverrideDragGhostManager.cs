using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using HarmonyLib;

namespace FiresCore.UI
{
    /// <summary>
    /// Standalone drag ghost visual that works independently of InventoryGui.
    /// When an item is being dragged (from our override slots or from vanilla),
    /// this component renders a ghost icon following the cursor.
    ///
    /// If InventoryGui already has its own drag ghost active (m_dragGo),
    /// this component defers to it. It only creates its own ghost when
    /// the vanilla drag visual is missing (e.g., inventory GUI not fully open
    /// but hotbar drag is active).
    ///
    /// This is a singleton - use <see cref="EnsureInstance"/> to create.
    /// </summary>
    public class UIOverrideDragGhostManager : MonoBehaviour
    {
        public static UIOverrideDragGhostManager Instance { get; private set; }

        private GameObject _ghostGO;
        private Image _ghostIcon;
        private Canvas _canvas;
        private RectTransform _canvasRect;

        private static readonly FieldInfo fi_dragItem =
            AccessTools.Field(typeof(InventoryGui), "m_dragItem");
        private static readonly FieldInfo fi_dragGo =
            AccessTools.Field(typeof(InventoryGui), "m_dragGo");
        private static readonly FieldInfo fi_dragAmount =
            AccessTools.Field(typeof(InventoryGui), "m_dragAmount");
        private static readonly FieldInfo fi_dragInventory =
            AccessTools.Field(typeof(InventoryGui), "m_dragInventory");

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            DestroyGhost();
        }

        private void LateUpdate()
        {
            if (InventoryGui.instance == null)
            {
                HideGhost();
                return;
            }

            // Read drag state from InventoryGui
            var dragItem = fi_dragItem?.GetValue(InventoryGui.instance) as ItemDrop.ItemData;
            var dragGo = fi_dragGo?.GetValue(InventoryGui.instance) as GameObject;

            if (dragItem == null)
            {
                HideGhost();
                return;
            }

            // If vanilla already has a visible drag GO, let it handle rendering
            if (dragGo != null && dragGo.activeInHierarchy)
            {
                HideGhost();
                return;
            }

            // Vanilla drag item exists but no visible ghost - we provide one
            EnsureGhost();
            UpdateGhostVisual(dragItem);
            UpdateGhostPosition();
        }

        //  Ghost lifecycle

        private void EnsureGhost()
        {
            if (_ghostGO != null) return;

            EnsureCanvas();

            _ghostGO = new GameObject("UIOverride_DragGhost", typeof(RectTransform));
            _ghostGO.transform.SetParent(_canvas.transform, false);

            var rect = _ghostGO.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(50, 50);
            rect.pivot = new Vector2(0.5f, 0.5f);

            _ghostIcon = _ghostGO.AddComponent<Image>();
            _ghostIcon.raycastTarget = false;
            _ghostIcon.color = new Color(1f, 1f, 1f, 0.8f);

            // Ensure ghost renders on top
            var canvasOverride = _ghostGO.AddComponent<Canvas>();
            canvasOverride.overrideSorting = true;
            canvasOverride.sortingOrder = 9999;
        }

        private void HideGhost()
        {
            if (_ghostGO != null)
                _ghostGO.SetActive(false);
        }

        private void DestroyGhost()
        {
            if (_ghostGO != null)
            {
                Destroy(_ghostGO);
                _ghostGO = null;
                _ghostIcon = null;
            }
        }

        private void EnsureCanvas()
        {
            if (_canvas != null) return;

            // Try to find the main UI canvas
            var allCanvases = FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            foreach (var canvas in allCanvases)
            {
                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay && canvas.sortingOrder >= 0)
                {
                    _canvas = canvas;
                    _canvasRect = canvas.GetComponent<RectTransform>();
                    return;
                }
            }

            // Fallback: create our own overlay canvas
            var canvasGO = new GameObject("UIOverride_DragGhostCanvas");
            DontDestroyOnLoad(canvasGO);
            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 9999;
            canvasGO.AddComponent<GraphicRaycaster>();
            _canvasRect = canvasGO.GetComponent<RectTransform>();
        }

        //  Visual update

        private void UpdateGhostVisual(ItemDrop.ItemData item)
        {
            if (_ghostIcon == null || item == null) return;

            _ghostGO.SetActive(true);

            if (item.m_shared != null && item.m_shared.m_icons != null && item.m_shared.m_icons.Length > 0)
            {
                _ghostIcon.sprite = item.m_shared.m_icons[0];
                _ghostIcon.color = new Color(1f, 1f, 1f, 0.8f);
            }
            else
            {
                _ghostIcon.sprite = null;
                _ghostIcon.color = Color.clear;
            }
        }

        private void UpdateGhostPosition()
        {
            if (_ghostGO == null || _canvasRect == null) return;

            var rect = _ghostGO.GetComponent<RectTransform>();
            Vector2 mousePos = ZInput.pointerPosition;

            // Convert screen position to canvas local position
            Vector2 localPos;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _canvasRect, mousePos, null, out localPos))
            {
                rect.anchoredPosition = localPos;
            }
        }

        //  Public API

        /// <summary>
        /// Returns the item currently being dragged, or null.
        /// </summary>
        public static ItemDrop.ItemData GetDragItem()
        {
            if (InventoryGui.instance == null) return null;
            return fi_dragItem?.GetValue(InventoryGui.instance) as ItemDrop.ItemData;
        }

        /// <summary>
        /// Returns the inventory the drag item came from, or null.
        /// </summary>
        public static Inventory GetDragInventory()
        {
            if (InventoryGui.instance == null) return null;
            return fi_dragInventory?.GetValue(InventoryGui.instance) as Inventory;
        }

        /// <summary>
        /// Clears the current drag state in InventoryGui.
        /// </summary>
        public static void ClearDrag()
        {
            if (InventoryGui.instance == null) return;
            try
            {
                fi_dragItem?.SetValue(InventoryGui.instance, null);
                fi_dragAmount?.SetValue(InventoryGui.instance, 0);

                var dragGo = fi_dragGo?.GetValue(InventoryGui.instance) as GameObject;
                if (dragGo != null)
                {
                    dragGo.SetActive(false);
                    Destroy(dragGo);
                    fi_dragGo?.SetValue(InventoryGui.instance, null);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIOverrideDragGhostManager] ClearDrag failed: {ex.Message}");
            }

            if (Instance != null)
                Instance.HideGhost();

            UIOverrideEquipmentPanel.MarkDirty();
        }

        /// <summary>
        /// Ensures a singleton instance exists.
        /// </summary>
        public static UIOverrideDragGhostManager EnsureInstance()
        {
            if (Instance != null) return Instance;

            var go = new GameObject("UIOverrideDragGhostManager");
            DontDestroyOnLoad(go);
            return go.AddComponent<UIOverrideDragGhostManager>();
        }
    }
}
