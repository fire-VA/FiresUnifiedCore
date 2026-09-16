using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FiresCore.UI
{
    /// <summary>
    /// FDT-styled hover help for admin popup UIs — attach to any raycastable element and a shared
    /// tooltip (gold topic + light body, dark rounded shell) appears near the cursor after a short
    /// hover. <see cref="DynamicBody"/> re-resolves at show time, so a row whose meaning depends on
    /// a dropdown's current selection (command syntax etc.) always shows live help. The tooltip
    /// never raycasts, so it can't steal the hover that opened it.
    /// </summary>
    public sealed class FiresHoverTip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        /// <summary>Global master switch for admin hover-help — set from the mod's config toggle.
        /// When false, tips are attached but never shown, so flipping it back on needs no rebuild.</summary>
        public static bool Enabled = true;

        public string Topic;
        public string Body;
        public Func<string> DynamicBody;

        private const float HoverDelay = 0.3f;
        private float _hoverStart = -1f;
        private bool _shown;

        /// <summary>Attaches (or retargets) hover help. The element needs a raycastable Graphic —
        /// when it has none at all, an invisible raycast-catcher Image is added so plain layout rows
        /// can carry help too.</summary>
        public static FiresHoverTip Attach(GameObject target, string topic, string body, Func<string> dynamicBody = null)
        {
            if (target == null) return null;
            var graphic = target.GetComponent<Graphic>();
            if (graphic == null)
            {
                var catcher = target.AddComponent<Image>();
                catcher.color = new Color(0f, 0f, 0f, 0f);
                catcher.raycastTarget = true;
            }
            else if (!graphic.raycastTarget)
            {
                // Labels default to raycastTarget=false; hover needs the hit. Parent buttons still
                // receive clicks (uGUI walks up from the hit graphic to the nearest handler).
                graphic.raycastTarget = true;
            }
            var tip = target.GetComponent<FiresHoverTip>() ?? target.AddComponent<FiresHoverTip>();
            tip.Topic = topic;
            tip.Body = body;
            tip.DynamicBody = dynamicBody;
            return tip;
        }

        /// <summary>
        /// Manual show — for UIs whose EventSystem is swallowed (the NPC book runs under an open
        /// InventoryGui, so IPointerEnter never fires and the panel polls the mouse itself). Honors
        /// the same <see cref="Enabled"/> master switch.
        /// </summary>
        public static void ShowAt(string topic, string body, Vector2 screenPos)
        {
            if (!Enabled) return;
            TipView.Show(topic, body, screenPos);
        }

        /// <summary>Manual hide, pairs with <see cref="ShowAt"/>.</summary>
        public static void HideTip() => TipView.Hide();

        public void OnPointerEnter(PointerEventData eventData) => _hoverStart = Time.unscaledTime;

        public void OnPointerExit(PointerEventData eventData)
        {
            _hoverStart = -1f;
            if (_shown) { _shown = false; TipView.Hide(); }
        }

        private void Update()
        {
            if (!Enabled || _hoverStart < 0f || _shown) return;
            if (Time.unscaledTime - _hoverStart < HoverDelay) return;
            _shown = true;
            string body = DynamicBody != null ? (DynamicBody() ?? Body) : Body;
            TipView.Show(Topic, body, UnityEngine.Input.mousePosition);
        }

        private void OnDisable()
        {
            _hoverStart = -1f;
            if (_shown) { _shown = false; TipView.Hide(); }
        }

        // ── the shared tooltip view ──────────────────────────────────────────

        private static class TipView
        {
            private const float MaxWidth = 300f;

            private static GameObject _canvasGo;
            private static RectTransform _canvasRect;
            private static RectTransform _panel;
            private static TextMeshProUGUI _topic;
            private static TextMeshProUGUI _body;

            public static void Show(string topic, string body, Vector2 screenPos)
            {
                if (string.IsNullOrEmpty(topic) && string.IsNullOrEmpty(body)) return;
                EnsureBuilt();
                _canvasGo.SetActive(true);

                _topic.gameObject.SetActive(!string.IsNullOrEmpty(topic));
                _topic.text = topic ?? "";
                _body.gameObject.SetActive(!string.IsNullOrEmpty(body));
                _body.text = body ?? "";

                LayoutRebuilder.ForceRebuildLayoutImmediate(_panel);
                PositionAtCursor(screenPos);
            }

            public static void Hide()
            {
                if (_canvasGo != null) _canvasGo.SetActive(false);
            }

            private static void EnsureBuilt()
            {
                if (_canvasGo != null) return;

                _canvasGo = new GameObject("FiresHoverTipCanvas");
                UnityEngine.Object.DontDestroyOnLoad(_canvasGo);
                var canvas = _canvasGo.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 8500;   // above the context menu (7000) and every Fires popup
                var scaler = _canvasGo.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.matchWidthOrHeight = 0.5f;
                _canvasRect = (RectTransform)_canvasGo.transform;

                var panelGo = new GameObject("Tip", typeof(RectTransform));
                _panel = (RectTransform)panelGo.transform;
                _panel.SetParent(_canvasRect, false);
                _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0.5f);
                var image = panelGo.AddComponent<Image>();
                image.sprite = FiresRoundedSkin.RoundedSprite(8, FiresPopupTheme.PanelBg, FiresPopupTheme.PanelEdge, 1);
                image.type = Image.Type.Sliced;
                image.color = Color.white;
                image.raycastTarget = false;

                var layout = panelGo.AddComponent<VerticalLayoutGroup>();
                layout.padding = new RectOffset(10, 10, 7, 8);
                layout.spacing = 3f;
                layout.childControlWidth = true;
                layout.childControlHeight = true;
                layout.childForceExpandWidth = false;
                layout.childForceExpandHeight = false;
                var fitter = panelGo.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                _topic = MakeLabel(panelGo.transform, "Topic", 13f, FiresPopupTheme.TextGold, FontStyles.Bold);
                _body = MakeLabel(panelGo.transform, "Body", 12f, FiresPopupTheme.TextLight, FontStyles.Normal);

                _canvasGo.SetActive(false);
            }

            private static TextMeshProUGUI MakeLabel(Transform parent, string name, float size, Color color, FontStyles style)
            {
                var go = new GameObject(name, typeof(RectTransform));
                go.transform.SetParent(parent, false);
                var tmp = go.AddComponent<TextMeshProUGUI>();
                tmp.fontSize = size;
                tmp.color = color;
                tmp.fontStyle = style;
                tmp.alignment = TextAlignmentOptions.TopLeft;
                tmp.textWrappingMode = TextWrappingModes.Normal;
                tmp.raycastTarget = false;
                var layoutElement = go.AddComponent<LayoutElement>();
                layoutElement.preferredWidth = MaxWidth;
                try { UIBuilderHelper.ApplyBodyFont(tmp); } catch { }
                return tmp;
            }

            private static void PositionAtCursor(Vector2 screenPos)
            {
                RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRect, screenPos, null, out var local);
                var size = _panel.sizeDelta;
                float halfW = _canvasRect.rect.width * 0.5f;
                float halfH = _canvasRect.rect.height * 0.5f;
                local += new Vector2(14f, -18f);

                float pivotX = (local.x + size.x > halfW) ? 1f : 0f;
                float pivotY = (local.y - size.y < -halfH) ? 0f : 1f;
                _panel.pivot = new Vector2(pivotX, pivotY);
                _panel.anchoredPosition = local;
            }
        }
    }
}
