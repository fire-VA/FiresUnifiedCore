using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace FiresCore.UI.ContextMenu
{
    /// <summary>
    /// The parchment context-menu panel: a vertical stack of hoverable rows positioned at the cursor, styled to
    /// match the book/tooltip palette. Input is gated while it's up (camera pinned, character frozen), so the
    /// driver drives hover + clicks manually via RectangleContainsScreenPoint rather than the EventSystem —
    /// the same approach the patrol popup uses over an open InventoryGui.
    /// </summary>
    internal sealed class ContextMenuView
    {
        private const float Width = 250f;
        private const float RowPx = 34f;
        private const float HeaderPx = 30f;
        private const float SepPx = 9f;
        private const float Pad = 8f;

        private GameObject _canvasGo;
        private RectTransform _canvasRect;
        private RectTransform _panel;

        private struct Row { public RectTransform Rect; public Image Bg; public ContextMenuItem Item; }
        private readonly List<Row> _rows = new List<Row>();

        public bool IsOpen => _canvasGo != null;

        public void Show(string title, List<ContextMenuItem> items, Vector2 screenPos)
        {
            Close();
            BuildCanvas();

            float height = Pad * 2f + (string.IsNullOrEmpty(title) ? 0f : HeaderPx);
            foreach (var it in items) height += it.IsSeparator ? SepPx : RowPx;

            _panel = NewRect("Panel", _canvasRect);
            _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0.5f);
            _panel.pivot = new Vector2(0f, 1f); // top-left, refined in PositionAtCursor
            _panel.sizeDelta = new Vector2(Width, height);
            var bg = _panel.gameObject.AddComponent<Image>();
            bg.color = UIFontConfig.Colors.ParchmentPanel;
            var outline = _panel.gameObject.AddComponent<Outline>();
            outline.effectColor = UIFontConfig.Colors.ParchmentOutline;
            outline.effectDistance = new Vector2(2f, -2f);

            float y = -Pad;

            if (!string.IsNullOrEmpty(title))
            {
                var header = UIBuilderHelper.CreateImage(_panel, "Header", Vector2.zero, Vector2.one, UIFontConfig.Colors.ParchmentHeader);
                header.raycastTarget = false;
                PlaceTop(header.rectTransform, y, HeaderPx, sidePad: 0f);
                var titleLabel = UIBuilderHelper.CreateLabel(_panel, "Title", title, 14,
                    UIFontConfig.Colors.ParchmentButtonInk, Vector2.zero, Vector2.one, TextAlignmentOptions.Center);
                PlaceTop(titleLabel.rectTransform, y, HeaderPx, sidePad: 6f);
                y -= HeaderPx;
            }

            foreach (var it in items)
            {
                if (it.IsSeparator)
                {
                    var line = UIBuilderHelper.CreateImage(_panel, "Sep", Vector2.zero, Vector2.one, UIFontConfig.Colors.ParchmentEdge);
                    line.raycastTarget = false;
                    PlaceTop(line.rectTransform, y - (SepPx - 2f) * 0.5f, 2f, sidePad: 10f);
                    y -= SepPx;
                    continue;
                }

                var btn = UIBuilderHelper.CreateParchmentButton(_panel, it.Label, Vector2.zero, Vector2.one, null);
                var rt = btn.GetComponent<RectTransform>();
                PlaceTop(rt, y, RowPx, sidePad: Pad);
                var img = btn.GetComponent<Image>();

                if (!it.Enabled)
                {
                    img.color = UIFontConfig.Colors.ParchmentButtonPressed;
                    var lbl = btn.GetComponentInChildren<TextMeshProUGUI>();
                    if (lbl != null) lbl.color = UIFontConfig.Colors.ParchmentLabel;
                }
                else
                {
                    img.color = UIFontConfig.Colors.ParchmentButton;
                    _rows.Add(new Row { Rect = rt, Bg = img, Item = it });
                }
                y -= RowPx;
            }

            PositionAtCursor(screenPos);
        }

        /// <summary>Per-frame hover highlight (called by the driver while input is gated).</summary>
        public void Tick(Vector2 mouse)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                if (r.Bg == null) continue;
                bool hot = RectTransformUtility.RectangleContainsScreenPoint(r.Rect, mouse, null);
                r.Bg.color = hot ? UIFontConfig.Colors.ParchmentButtonHover : UIFontConfig.Colors.ParchmentButton;
            }
        }

        /// <summary>A left-click: pick the row under the cursor (close first, then act), or dismiss on click-away.</summary>
        public void HandleLeftClick(Vector2 mouse)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                if (r.Rect == null || r.Item == null) continue;
                if (RectTransformUtility.RectangleContainsScreenPoint(r.Rect, mouse, null))
                {
                    var act = r.Item.OnPick;
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
        private static void PlaceTop(RectTransform rt, float yTop, float height, float sidePad)
        {
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(sidePad, yTop - height);
            rt.offsetMax = new Vector2(-sidePad, yTop);
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
