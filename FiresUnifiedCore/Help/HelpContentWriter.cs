using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.Help
{
    // The content-building API handed to each registered help section. A section appends elements to the
    // panel's scroll content by calling these methods; the renderer owns the parent transform. Lifted from
    // the original VerdantAscentHelpPanel Add* helpers so output is identical.
    //
    // COLLAPSIBLE GROUPS: SubHeader and AdminHeader are clickable — each starts a group that swallows every
    // element written after it (paragraphs, bullets, code, dividers) until the next header. Clicking the
    // header collapses/expands the group ([-] / [+] prefix; ASCII on purpose — Unicode arrows render as
    // tofu in the game fonts). Collapse state is remembered per section+header for the session so
    // reopening the panel keeps your layout. Content authors need no changes — grouping is inferred from
    // the existing Header/SubHeader call order.
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

        private const string ExpandedPrefix = "[-] ";
        private const string CollapsedPrefix = "[+] ";

        // Session-persistent collapse memory, keyed "<modId>|<sectionTitle>|<headerText>". Static so it
        // survives panel rebuilds (the content area is destroyed and rebuilt on every NavigateTo).
        private static readonly Dictionary<string, bool> s_collapsed = new Dictionary<string, bool>();

        private readonly Transform _content;
        private readonly string _stateKey;
        private Transform _group;   // active collapsible group container, or null when writing to the root

        public HelpContentWriter(Transform content, string stateKey = null)
        {
            _content = content;
            _stateKey = stateKey ?? "";
        }

        // Elements land in the open collapsible group when one is active, else at the section root.
        private Transform Target => _group != null ? _group : _content;

        public void Header(string text)
        {
            _group = null;   // top-level headers never collapse and always end any open group
            var tmp = NewText("Header", text, HeaderFontSize, HelpTheme.HeaderColor, TextAlignmentOptions.MidlineLeft, _content);
            tmp.fontStyle = FontStyles.Bold;
            tmp.gameObject.AddComponent<LayoutElement>().minHeight = HeaderMinHeight;
        }

        public void SubHeader(string text)
        {
            BeginCollapsibleGroup("SubHeader", text, SubHeaderFontSize, HelpTheme.TextGold, SubHeaderMinHeight, adminStyle: false);
        }

        public void AdminHeader(string text)
        {
            BeginCollapsibleGroup("AdminHeader", text, AdminHeaderFontSize, HelpTheme.AdminHeaderColor, AdminHeaderMinHeight, adminStyle: true);
        }

        public void Paragraph(string text) => WrappedText("Paragraph", text);

        public void Bullet(string text) => WrappedText("Bullet", "  • " + text);

        public void CodeBlock(string text)
        {
            var box = NewChild("Code", Target);
            var image = box.AddComponent<Image>();
            image.color = HelpTheme.CodeBg;
            image.raycastTarget = false;
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

        // ── collapsible group machinery ─────────────────────────────────────

        private void BeginCollapsibleGroup(string name, string text, float fontSize, Color textColor,
            float minHeight, bool adminStyle)
        {
            _group = null;   // consecutive headers never nest

            string stateId = _stateKey + "|" + text;
            bool collapsed = s_collapsed.TryGetValue(stateId, out var wasCollapsed) && wasCollapsed;

            // Header row — always visible at the section root, click to toggle.
            var row = NewChild(name, _content);
            row.AddComponent<LayoutElement>().minHeight = minHeight;

            Image target;
            if (adminStyle)
            {
                // Preserve the AdminHeader look: an inset tinted backplate behind the text.
                var backgroundGo = NewChild("AdminBg", row.transform);
                var bgRect = backgroundGo.AddComponent<RectTransform>();
                bgRect.anchorMin = Vector2.zero;
                bgRect.anchorMax = Vector2.one;
                bgRect.offsetMin = new Vector2(-4, -2);
                bgRect.offsetMax = new Vector2(4, 2);
                target = backgroundGo.AddComponent<Image>();
                target.color = HelpTheme.AdminBg;
            }
            else
            {
                // Invisible full-row hit target so the whole line is clickable and can tint on hover.
                target = row.AddComponent<Image>();
                target.color = new Color(1f, 1f, 1f, 0f);
            }
            target.raycastTarget = true;

            var textGo = NewChild("Text", row.transform);
            var rect = textGo.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = (collapsed ? CollapsedPrefix : ExpandedPrefix) + text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = textColor;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.raycastTarget = false;
            HelpPanel.ApplyFont(tmp);

            // Group container — everything until the next header parents here; SetActive drives collapse.
            var groupGo = NewChild(name + "Group", _content);
            var verticalLayout = groupGo.AddComponent<VerticalLayoutGroup>();
            verticalLayout.spacing = 6;
            verticalLayout.childControlWidth = true;
            verticalLayout.childControlHeight = true;
            verticalLayout.childForceExpandWidth = true;
            verticalLayout.childForceExpandHeight = false;
            groupGo.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            groupGo.SetActive(!collapsed);
            _group = groupGo.transform;

            var btn = row.AddComponent<Button>();
            btn.targetGraphic = target;
            var colors = btn.colors;
            if (adminStyle)
            {
                colors.normalColor = Color.white;
                colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f, 1f);
                colors.pressedColor = new Color(1.5f, 1.5f, 1.5f, 1f);
                colors.selectedColor = Color.white;
            }
            else
            {
                colors.normalColor = new Color(1f, 1f, 1f, 0f);
                colors.highlightedColor = new Color(1f, 1f, 1f, 0.06f);
                colors.pressedColor = new Color(1f, 1f, 1f, 0.12f);
                colors.selectedColor = new Color(1f, 1f, 1f, 0f);
            }
            btn.colors = colors;

            var contentRect = _content as RectTransform;
            btn.onClick.AddListener(() =>
            {
                bool nowCollapsed = groupGo.activeSelf;   // currently expanded → this click collapses
                groupGo.SetActive(!nowCollapsed);
                s_collapsed[stateId] = nowCollapsed;
                tmp.text = (nowCollapsed ? CollapsedPrefix : ExpandedPrefix) + text;
                if (contentRect != null) LayoutRebuilder.ForceRebuildLayoutImmediate(contentRect);
            });
        }

        // ── primitives ──────────────────────────────────────────────────────

        private TextMeshProUGUI NewText(string name, string text, float fontSize, Color color,
            TextAlignmentOptions align, Transform parent)
        {
            var go = NewChild(name, parent);
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
            var go = NewChild(name, Target);
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
            var go = NewChild(name, Target);
            var layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.minHeight = height;
            layoutElement.preferredHeight = height;
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
