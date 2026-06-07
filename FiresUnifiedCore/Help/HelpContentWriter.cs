using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.Help
{
    // The content-building API handed to each registered help section. A section appends elements to the
    // panel's scroll content by calling these methods; the renderer owns the parent transform. Lifted from
    // the original VerdantAscentHelpPanel Add* helpers so output is identical.
    public sealed class HelpContentWriter
    {
        private const float HeaderFontSize = 16f;
        private const float SubHeaderFontSize = 13f;
        private const float BodyFontSize = 11f;
        private const float CodeFontSize = 10f;
        private const float AdminHeaderFontSize = 12f;
        private const float HeaderMinHeight = 32f;
        private const float SubHeaderMinHeight = 24f;
        private const float AdminHeaderMinHeight = 24f;
        private const float DividerHeight = 1f;
        private const float AdminDividerHeight = 2f;

        private readonly Transform _content;

        public HelpContentWriter(Transform content) => _content = content;

        public void Header(string text)
        {
            var tmp = NewText("Header", text, HeaderFontSize, HelpTheme.HeaderColor, TextAlignmentOptions.MidlineLeft);
            tmp.fontStyle = FontStyles.Bold;
            tmp.gameObject.AddComponent<LayoutElement>().minHeight = HeaderMinHeight;
        }

        public void SubHeader(string text)
        {
            var tmp = NewText("SubHeader", text, SubHeaderFontSize, HelpTheme.TextGold, TextAlignmentOptions.MidlineLeft);
            tmp.fontStyle = FontStyles.Bold;
            tmp.gameObject.AddComponent<LayoutElement>().minHeight = SubHeaderMinHeight;
        }

        public void Paragraph(string text) => WrappedText("Paragraph", text);

        public void Bullet(string text) => WrappedText("Bullet", "  • " + text);

        public void CodeBlock(string text)
        {
            var box = NewChild("Code", _content);
            var bg = box.AddComponent<Image>();
            bg.color = HelpTheme.CodeBg;
            bg.raycastTarget = false;
            var layout = box.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(10, 10, 6, 6);
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            box.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var inner = NewChild("Text", box.transform);
            var tmp = inner.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = CodeFontSize;
            tmp.color = HelpTheme.TextMuted;
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.raycastTarget = false;
            HelpPanel.ApplyFont(tmp);
            inner.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        public void Divider() => Bar("Divider", DividerHeight, HelpTheme.DividerColor);

        public void AdminDivider() => Bar("AdminDivider", AdminDividerHeight, HelpTheme.AdminDivider);

        public void AdminHeader(string text)
        {
            var box = NewChild("AdminHeader", _content);

            var bg = NewChild("AdminBg", box.transform);
            var bgRect = bg.AddComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = new Vector2(-4, -2);
            bgRect.offsetMax = new Vector2(4, 2);
            var bgImg = bg.AddComponent<Image>();
            bgImg.color = HelpTheme.AdminBg;
            bgImg.raycastTarget = false;

            box.AddComponent<LayoutElement>().minHeight = AdminHeaderMinHeight;
            var tmp = box.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = AdminHeaderFontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = HelpTheme.AdminHeaderColor;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.raycastTarget = false;
            HelpPanel.ApplyFont(tmp);
        }

        private TextMeshProUGUI NewText(string name, string text, float fontSize, Color color, TextAlignmentOptions align)
        {
            var go = NewChild(name, _content);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = align;
            tmp.raycastTarget = false;
            HelpPanel.ApplyFont(tmp);
            return tmp;
        }

        private void WrappedText(string name, string text)
        {
            var go = NewChild(name, _content);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = BodyFontSize;
            tmp.color = HelpTheme.TextLight;
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.textWrappingMode = TextWrappingModes.Normal;
            tmp.raycastTarget = false;
            HelpPanel.ApplyFont(tmp);
            go.AddComponent<LayoutElement>().flexibleWidth = 1;
            go.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        }

        private void Bar(string name, float height, Color color)
        {
            var go = NewChild(name, _content);
            var le = go.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
            var img = go.AddComponent<Image>();
            img.color = color;
            img.raycastTarget = false;
        }

        private static GameObject NewChild(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go;
        }
    }
}
