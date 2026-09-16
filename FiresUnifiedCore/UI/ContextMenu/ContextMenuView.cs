using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace FiresCore.UI.ContextMenu
{
    /// <summary>
    /// The context-menu panel: a vertical stack of hoverable rows positioned at the cursor, styled like the
    /// FiresDebugginTools windows (FiresRoundedSkin brown/gold palette, rounded corners, draggable title bar).
    /// Input is gated while it's up (camera pinned, character frozen), so the driver drives hover + clicks
    /// manually via RectangleContainsScreenPoint rather than the EventSystem — the same approach the patrol
    /// popup uses over an open InventoryGui. Dragging the title bar is polled the same way (Tick).
    /// </summary>
    internal sealed class ContextMenuView
    {
        private const float Width = 250f;
        private const float RowPx = 32f;
        private const float HeaderPx = 30f;
        private const float SepPx = 9f;
        private const float Pad = 8f;

        // FiresRoundedSkin palette (FDT windows) — panel brown shell, bar-brown header, button browns.
        private static readonly Color PanelBg    = new Color(0.09f, 0.075f, 0.055f, 0.95f);
        private static readonly Color PanelEdge  = new Color(0.62f, 0.42f, 0.16f, 0.9f);
        private static readonly Color HeaderBg   = new Color(0.30f, 0.22f, 0.10f, 0.95f);
        private static readonly Color RowBg      = new Color(0.34f, 0.23f, 0.10f, 0.95f);
        private static readonly Color RowHover   = new Color(0.47f, 0.32f, 0.14f, 0.95f);
        private static readonly Color RowDisabled = new Color(0.24f, 0.16f, 0.07f, 0.95f);
        private static readonly Color TextGold   = new Color(1f, 0.85f, 0.5f, 1f);
        private static readonly Color TextLight  = new Color(0.88f, 0.79f, 0.6f, 1f);
        private static readonly Color TextDim    = new Color(0.8f, 0.72f, 0.56f, 0.7f);

        private GameObject _canvasGo;
        private RectTransform _canvasRect;
        private RectTransform _panel;
        private RectTransform _header;

        private bool _dragging;
        private Vector2 _dragOffset;   // panel anchoredPosition - cursor local point, held constant while dragging

        private struct Row { public RectTransform Rect; public Image Bg; public TextMeshProUGUI Label; public ContextMenuItem Item; }
        private readonly List<Row> _rows = new List<Row>();

        public bool IsOpen => _canvasGo != null;

        public void Show(string title, List<ContextMenuItem> items, Vector2 screenPos)
        {
            Close();
            BuildCanvas();

            float height = Pad * 2f + (string.IsNullOrEmpty(title) ? 0f : HeaderPx + 4f);
            foreach (var item in items) height += item.IsSeparator ? SepPx : RowPx;

            _panel = NewRect("Panel", _canvasRect);
            _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0.5f);
            _panel.pivot = new Vector2(0f, 1f); // top-left, refined in PositionAtCursor
            _panel.sizeDelta = new Vector2(Width, height);
            var image = _panel.gameObject.AddComponent<Image>();
            image.sprite = FiresRoundedSkin.RoundedSprite(10, PanelBg, PanelEdge, 1);
            image.type = Image.Type.Sliced;
            image.color = Color.white;

            float y = -Pad;

            if (!string.IsNullOrEmpty(title))
            {
                var header = UIBuilderHelper.CreateImage(_panel, "Header", Vector2.zero, Vector2.one, Color.white);
                header.sprite = FiresRoundedSkin.RoundedSprite(7, HeaderBg);
                header.type = Image.Type.Sliced;
                header.raycastTarget = false;
                PlaceTop(header.rectTransform, y, HeaderPx, sidePad: Pad);
                _header = header.rectTransform;
                var titleLabel = UIBuilderHelper.CreateLabel(_panel, "Title", title, 14,
                    TextGold, Vector2.zero, Vector2.one, TextAlignmentOptions.Center);
                titleLabel.fontStyle |= FontStyles.Bold;
                PlaceTop(titleLabel.rectTransform, y, HeaderPx, sidePad: Pad + 6f);
                y -= HeaderPx + 4f;
            }

            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    var line = UIBuilderHelper.CreateImage(_panel, "Sep", Vector2.zero, Vector2.one, PanelEdge);
                    line.raycastTarget = false;
                    PlaceTop(line.rectTransform, y - (SepPx - 2f) * 0.5f, 2f, sidePad: 12f);
                    y -= SepPx;
                    continue;
                }

                var rowRect = NewRect("Row", _panel);
                PlaceTop(rowRect, y, RowPx - 3f, sidePad: Pad);
                var img = rowRect.gameObject.AddComponent<Image>();
                img.sprite = FiresRoundedSkin.RoundedSprite(7, Color.white);
                img.type = Image.Type.Sliced;
                img.color = item.Enabled ? RowBg : RowDisabled;

                var lbl = UIBuilderHelper.CreateLabel(rowRect, "Label", item.Label, 13,
                    item.Enabled ? TextLight : TextDim, Vector2.zero, Vector2.one, TextAlignmentOptions.MidlineLeft);
                lbl.rectTransform.offsetMin = new Vector2(10f, 0f);
                lbl.rectTransform.offsetMax = new Vector2(-6f, 0f);

                if (item.Enabled)
                    _rows.Add(new Row { Rect = rowRect, Bg = img, Label = lbl, Item = item });
                y -= RowPx;
            }

            PositionAtCursor(screenPos);
        }

        /// <summary>Per-frame hover highlight + title-bar drag (called by the driver while input is gated —
        /// the EventSystem is not in play, so dragging is polled exactly like the row clicks).</summary>
        public void Tick(Vector2 mouse)
        {
            if (_dragging)
            {
                if (!UnityEngine.Input.GetMouseButton(0)) _dragging = false;
                else if (_panel != null &&
                         RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, mouse, null, out var local))
                    _panel.anchoredPosition = local + _dragOffset;
            }
            else if (_header != null && _panel != null && UnityEngine.Input.GetMouseButtonDown(0)
                     && RectTransformUtility.RectangleContainsScreenPoint(_header, mouse, null)
                     && RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, mouse, null, out var grab))
            {
                _dragging = true;
                _dragOffset = _panel.anchoredPosition - grab;
            }

            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row.Bg == null) continue;
                bool hot = RectTransformUtility.RectangleContainsScreenPoint(row.Rect, mouse, null);
                row.Bg.color = hot ? RowHover : RowBg;
                if (row.Label != null) row.Label.color = hot ? TextGold : TextLight;
            }
        }

        /// <summary>A left-click: pick the row under the cursor (close first, then act), or dismiss on click-away.</summary>
        public void HandleLeftClick(Vector2 mouse)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                if (row.Rect == null || row.Item == null) continue;
                if (RectTransformUtility.RectangleContainsScreenPoint(row.Rect, mouse, null))
                {
                    var act = row.Item.OnPick;
                    Close();
                    act?.Invoke();
                    return;
                }
            }
            // Outside the panel entirely → dismiss; inside but not on a row → keep open.
            if (_panel == null || !RectTransformUtility.RectangleContainsScreenPoint(_panel, mouse, null))
                Close();
        }

        public void Close()
        {
            _rows.Clear();
            if (_canvasGo != null) Object.Destroy(_canvasGo);
            _canvasGo = null;
            _canvasRect = null;
            _panel = null;
            _header = null;
            _dragging = false;
        }

        /// <summary>
        /// Makes THIS menu drive its own hover/click/dismiss input. The hold-Alt driver only pumps menus
        /// it opened itself — standalone popups (e.g. over the build HUD) attach this instead. Lives on
        /// the menu canvas, so closing the menu kills the pump with it.
        /// </summary>
        public void AttachSelfPump()
        {
            if (_canvasGo != null && _canvasGo.GetComponent<SelfPump>() == null)
                _canvasGo.AddComponent<SelfPump>();
        }

        private sealed class SelfPump : MonoBehaviour
        {
            private void Update()
            {
                var view = FiresContextMenu.View;
                if (view == null || !view.IsOpen) return;
                Vector2 mouse = UnityEngine.Input.mousePosition;
                view.Tick(mouse);
                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { view.Close(); return; }
                if (UnityEngine.Input.GetMouseButtonDown(0)) view.HandleLeftClick(mouse);
            }
        }

        // ── build helpers ────────────────────────────────────────────────────
        private void BuildCanvas()
        {
            _canvasGo = new GameObject("FiresContextMenuCanvas");
            Object.DontDestroyOnLoad(_canvasGo);
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 7000; // above the patrol popup (6000) / book (210)
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _canvasGo.AddComponent<GraphicRaycaster>();
            _canvasRect = (RectTransform)_canvasGo.transform;
        }

        private static RectTransform NewRect(string name, RectTransform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        // Stacks a child against the panel top: yTop is measured downward from the top edge (0 at top, negative
        // going down); height is the slot height.
        private static void PlaceTop(RectTransform rect, float yTop, float height, float sidePad)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(sidePad, yTop - height);
            rect.offsetMax = new Vector2(-sidePad, yTop);
        }

        private void PositionAtCursor(Vector2 screenPos)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screenPos, null, out var local);
            var size = _panel.sizeDelta;
            float halfW = _canvasRect.rect.width * 0.5f;
            float halfH = _canvasRect.rect.height * 0.5f;

            float pivotX = (local.x + size.x > halfW) ? 1f : 0f;  // overflow right → open left
            float pivotY = (local.y - size.y < -halfH) ? 0f : 1f; // overflow bottom → open up
            _panel.pivot = new Vector2(pivotX, pivotY);
            _panel.anchoredPosition = local;
        }
    }
}
