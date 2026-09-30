using System;
using FiresCore.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.Compat
{
    // Fire (16:00): "a custom loading screen up while the HD textures are loading". Full screen over the menu while
    // HdTexturesLoadSpread runs: our own branding (text only; no IronGate art), a progress bar, "Loading HD textures: N / T",
    // the time so far and an estimate of what is left. It takes no clicks. It lives on the load's DontDestroyOnLoad runner,
    // so it stays up through an early join's finish-before-the-world and goes when the load ends.
    internal sealed class HdTexturesLoadScreen : MonoBehaviour
    {
        private const int SortingOrder = 32000;
        private const float ReferenceWidth = 1920f;
        private const float ReferenceHeight = 1080f;
        private const float MatchWidthOrHeight = 0.5f;
        private const float TitleSize = 72f;
        private const float SubtitleSize = 28f;
        private const float StatusSize = 26f;
        private const float TextWidth = 1400f;
        private const float TextHeight = 90f;
        private const float TitleY = 150f;
        private const float SubtitleY = 70f;
        private const float BarY = -20f;
        private const float StatusY = -80f;
        private const float BarWidth = 900f;
        private const float BarHeight = 16f;
        private const float RefreshSeconds = 0.25f;
        private const string Title = "Verdant's Ascent";
        private const string Subtitle = "Fire is preparing the HD textures";

        private static readonly Color Backdrop = new Color(0.03f, 0.05f, 0.04f, 0.97f);
        private static readonly Color TitleColour = new Color(0.86f, 0.93f, 0.80f, 1f);
        private static readonly Color TextColour = new Color(0.72f, 0.78f, 0.70f, 1f);
        private static readonly Color BarBack = new Color(1f, 1f, 1f, 0.12f);
        private static readonly Color BarFill = new Color(0.40f, 0.68f, 0.33f, 1f);

        private RectTransform _fill;
        private TextMeshProUGUI _status;
        private float _nextRefresh;

        private void Awake()
        {
            var root = new GameObject(nameof(HdTexturesLoadScreen), typeof(RectTransform));
            root.transform.SetParent(transform, false);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(ReferenceWidth, ReferenceHeight);
            scaler.matchWidthOrHeight = MatchWidthOrHeight;

            var backdrop = Panel(root.transform, "Backdrop", Backdrop);
            Stretch(backdrop.rectTransform);

            UIBuilderHelper.ApplyDecorativeFont(Label(root.transform, Title, TitleSize, TitleColour, TitleY));
            UIBuilderHelper.ApplyBodyFont(Label(root.transform, Subtitle, SubtitleSize, TextColour, SubtitleY));

            var bar = Panel(root.transform, "Bar", BarBack);
            bar.rectTransform.sizeDelta = new Vector2(BarWidth, BarHeight);
            bar.rectTransform.anchoredPosition = new Vector2(0f, BarY);
            var fill = Panel(bar.transform, "Fill", BarFill);
            _fill = fill.rectTransform;
            _fill.anchorMin = new Vector2(0f, 0f);
            _fill.anchorMax = new Vector2(0f, 1f);
            _fill.pivot = new Vector2(0f, 0.5f);
            _fill.anchoredPosition = Vector2.zero;
            _fill.sizeDelta = Vector2.zero;

            _status = Label(root.transform, "", StatusSize, TextColour, StatusY);
            UIBuilderHelper.ApplyBodyFont(_status);
            Refresh();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + RefreshSeconds;
            Refresh();
        }

        private void Refresh()
        {
            int done = HdTexturesLoadSpread.Done;
            int total = HdTexturesLoadSpread.Total;
            double elapsed = HdTexturesLoadSpread.ElapsedSeconds;
            float share = total > 0 ? Mathf.Clamp01((float)done / total) : 0f;
            _fill.sizeDelta = new Vector2(BarWidth * share, 0f);
            string left = done > 0 && total > done ? $", about {Clock(elapsed / done * (total - done))} left" : "";
            _status.text = total > 0
                ? $"Loading HD textures: {done:N0} / {total:N0}   ({Clock(elapsed)}{left})"
                : $"Getting the HD textures ready...   ({Clock(elapsed)})";
        }

        private static string Clock(double seconds)
        {
            var time = TimeSpan.FromSeconds(Math.Max(0.0, seconds));
            return $"{(int)time.TotalMinutes}:{time.Seconds:00}";
        }

        private static Image Panel(Transform parent, string name, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>();
            image.color = colour;
            image.raycastTarget = false;
            return image;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static TextMeshProUGUI Label(Transform parent, string text, float size, Color colour, float y)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = size;
            label.color = colour;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
            var rect = label.rectTransform;
            rect.sizeDelta = new Vector2(TextWidth, TextHeight);
            rect.anchoredPosition = new Vector2(0f, y);
            return label;
        }
    }
}
