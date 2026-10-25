// Fires UI kit, step 0. Code-built UGUI widgets (FIRES_UI_STYLE_GUIDE.md S4 components, S3.2 type, principle 8
// stable names; FIRES_UI_SHELL.md S6 kit, S7 hub layout, S10 parallax backdrop).
// Depends only on UnityEngine, UnityEngine.UI and TMPro. Byte-identical copy in ValheimRip1.0/Assets/FiresUIKit/Runtime.
// ASCII-only (10-14): non-ASCII characters are backslash-u escapes in strings and ASCII words in comments. Core UI/Shell/Kit and
// ValheimRip1.0/Assets/FiresUIKit/Runtime hold byte-identical copies (check with cmp).
//
// Coordinates: every Place()/builder position is in UI units from the parent's TOP-LEFT, y growing down.
// Stable names are real hierarchy paths, so Transform.Find("Window/fires.hub/Content/Section/performance/Row/fps/Value")
// works: a "Section" group node holds one child per section id, a "Row" group node one child per row id, and so on.
using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;

namespace FiresCore.UI.Shell.Kit
{
    public enum RowState { Normal, Warn, Danger }
    public enum ButtonKind { Primary, Destructive, Disabled }   // Disabled appended (rounded kit)
    public enum ToastKind { Info, Ok, Danger }
    public enum KitAlign { Left, Center, Right, TopLeft, TopCenter }
    public enum TextBackend { Tmp, Legacy }
    public enum NodeState { Learned, Available, Locked }

    // ---- text ------------------------------------------------------------------------------------------

    /// <summary>One label, TMP or legacy Text behind the same handle.</summary>
    public sealed class KitLabel
    {
        public TextMeshProUGUI Tmp;
        public Text Legacy;
        public float LegacyTrackingPx;   // extra px between glyphs (legacy only; TMP uses characterSpacing)
        public Graphic Graphic => Tmp != null ? (Graphic)Tmp : Legacy;
        public RectTransform Rect => Graphic.rectTransform;

        public string Value
        {
            get => Tmp != null ? Tmp.text : Legacy.text;
            set { if (Tmp != null) Tmp.text = value; else Legacy.text = value; }
        }

        public Color Color { get => Graphic.color; set => Graphic.color = value; }

        public float PreferredWidth
        {
            get
            {
                if (Tmp != null) return Tmp.GetPreferredValues(Tmp.text, 100000f, 1000f).x;
                float w = Legacy.cachedTextGeneratorForLayout.GetPreferredWidth(Legacy.text,
                    Legacy.GetGenerationSettings(new Vector2(100000f, 1000f))) / Legacy.pixelsPerUnit;
                if (LegacyTrackingPx > 0f) w += LegacyTrackingPx * Mathf.Max(0, KitTracking.GlyphCount(Legacy.text) - 1);
                return w;
            }
        }

        public float PreferredHeight(float width)
        {
            if (Tmp != null) return Tmp.GetPreferredValues(Tmp.text, width, 100000f).y;
            return Legacy.cachedTextGeneratorForLayout.GetPreferredHeight(Legacy.text,
                Legacy.GetGenerationSettings(new Vector2(width, 100000f))) / Legacy.pixelsPerUnit;
        }
    }

    public static class KitText
    {
        /// <summary>TMP is the target. Legacy exists only for hosts where the TMP SDF shader cannot render.</summary>
        public static TextBackend Backend = TextBackend.Tmp;
        public static IShellHost Host;
        // Host is set only by ShellRuntime (override ON);
        // OFF, every kit label got a null font and drew nothing. Fonts come from a game-font host of their own when the shell is not up.
        private static IShellHost s_fontFallback;
        private static IShellHost FontHost => Host ?? (s_fontFallback ?? (s_fontFallback = new FiresCore.UI.Shell.GameShellHost()));

        static readonly Dictionary<FontRole, TMP_FontAsset> TmpFonts = new Dictionary<FontRole, TMP_FontAsset>();
        static readonly HashSet<FontRole> TmpFailed = new HashSet<FontRole>();

        /// <summary>A dynamic SDF font asset made at runtime from the host's loaded Font (no bundled asset).</summary>
        public static TMP_FontAsset TmpFont(FontRole role)
        {
            if (TmpFonts.TryGetValue(role, out var cached) && cached != null) return cached;
            if (TmpFailed.Contains(role)) return null;
            var font = FontHost.GetFont(role);
            TMP_FontAsset asset = null;
            if (font != null)
            {
                try
                {
                    asset = TMP_FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024,
                        AtlasPopulationMode.Dynamic, true);
                }
                catch (Exception e) { Debug.LogWarning("[FiresKit] TMP font asset failed for " + role + ": " + e.Message); }
            }
            if (asset == null) { TmpFailed.Add(role); return null; }
            asset.name = "FiresKit " + font.name + " SDF";
            asset.hideFlags = HideFlags.DontSave;
            TmpFonts[role] = asset;
            return asset;
        }

        public static void ClearCache()
        {
            foreach (var a in TmpFonts.Values)
                if (a != null)
                {
                    if (a.atlasTexture != null) UnityEngine.Object.DestroyImmediate(a.atlasTexture);
                    if (a.material != null) UnityEngine.Object.DestroyImmediate(a.material);
                    UnityEngine.Object.DestroyImmediate(a);
                }
            TmpFonts.Clear();
            TmpFailed.Clear();
        }

        public static KitLabel Create(RectTransform parent, string name, string text, FontRole role, float size, Color color,
            KitAlign align, float tracking = 0f, bool autoSize = true, bool wrap = false)
        {
            var rt = Widgets.Node(parent, name);
            var label = new KitLabel();
            var tmpFont = Backend == TextBackend.Tmp ? TmpFont(role) : null;
            if (tmpFont != null)
            {
                var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
                t.font = tmpFont;
                t.fontSize = size;
                t.characterSpacing = tracking;
                t.color = color;
                t.richText = true;
                t.raycastTarget = false;
                t.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
                t.overflowMode = wrap ? TextOverflowModes.Overflow : TextOverflowModes.Ellipsis;
                t.enableAutoSizing = autoSize && !wrap;
                t.fontSizeMax = size;
                t.fontSizeMin = size * FiresTokens.AutoSizeMinFraction;
                t.alignment = ToTmp(align);
                t.margin = Vector4.zero;
                t.text = text;
                label.Tmp = t;
            }
            else
            {
                var t = rt.gameObject.AddComponent<Text>();
                t.font = FontHost.GetFont(role);
                t.fontSize = Mathf.RoundToInt(size);
                t.color = color;
                t.supportRichText = true;
                t.raycastTarget = false;
                t.alignment = ToAnchor(align);
                t.horizontalOverflow = wrap ? HorizontalWrapMode.Wrap : HorizontalWrapMode.Overflow;
                t.verticalOverflow = VerticalWrapMode.Overflow;
                t.resizeTextForBestFit = false; // best-fit needs the rect; legacy has no ellipsis (README gap)
                t.lineSpacing = 1f;
                t.text = text;
                label.Legacy = t;
                if (tracking > 0f && !wrap)
                {
                    var tr = rt.gameObject.AddComponent<KitTracking>();
                    tr.Px = tracking / 100f * size;
                    label.LegacyTrackingPx = tr.Px;
                }
            }
            return label;
        }

        static TextAlignmentOptions ToTmp(KitAlign a)
        {
            switch (a)
            {
                case KitAlign.Center: return TextAlignmentOptions.Center;
                case KitAlign.Right: return TextAlignmentOptions.Right;
                case KitAlign.TopLeft: return TextAlignmentOptions.TopLeft;
                case KitAlign.TopCenter: return TextAlignmentOptions.Top;
                default: return TextAlignmentOptions.Left;
            }
        }

        static TextAnchor ToAnchor(KitAlign a)
        {
            switch (a)
            {
                case KitAlign.Center: return TextAnchor.MiddleCenter;
                case KitAlign.Right: return TextAnchor.MiddleRight;
                case KitAlign.TopLeft: return TextAnchor.UpperLeft;
                case KitAlign.TopCenter: return TextAnchor.UpperCenter;
                default: return TextAnchor.MiddleLeft;
            }
        }

        /// <summary>Rich-text colour span (hex computed from the token at runtime, never typed).</summary>
        public static string Tint(string s, Color c) => "<color=" + FiresTokens.RichHex(c) + ">" + s + "</color>";

        /// <summary>"a . b . c" with the separators in TextFaint.</summary>
        public static string Context(params string[] parts)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) sb.Append(Tint(FiresTokens.GlyphSep, FiresTokens.TextFaint));
                sb.Append(parts[i]);
            }
            return sb.ToString();
        }
    }

    // ---- handles ---------------------------------------------------------------------------------------

    public sealed class KitWindow { public RectTransform Root, Content; public Image Fill, Frame; public KitLabel Close; }
    public sealed class KitHeader { public RectTransform Root, Pills; public KitLabel Context, Title; public float Height; }

    public sealed class KitSection
    {
        public RectTransform Root, Rows;
        public float Width;
        public float Y;                    // next free y inside Root (top-left, down)
        public void Gap(float g) { Y += g; }
    }

    public sealed class KitRow
    {
        public RectTransform Root;
        public KitLabel Label, Value;
        public RowState State
        {
            set { Value.Color = value == RowState.Danger ? FiresTokens.Danger : value == RowState.Warn ? FiresTokens.Warn : FiresTokens.Text; }
        }
    }

    // ---- widgets ---------------------------------------------------------------------------------------

    public static partial class Widgets   // partial (the hotbar kit lives in Kit\HotbarKit.cs)
    {
        // ---- primitives ----------------------------------------------------------------------------

        public static RectTransform Node(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent != null ? parent.gameObject.layer : 5;
            var rt = (RectTransform)go.transform;
            if (parent != null) rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = Vector2.zero;
            return rt;
        }

        /// <summary>A group node (full-stretch, zero offsets) named 'group', reused if it exists.</summary>
        public static RectTransform Group(RectTransform parent, string group)
        {
            var existing = parent.Find(group) as RectTransform;
            if (existing != null) return existing;
            return Stretch(Node(parent, group), 0, 0, 0, 0);
        }

        /// <summary>Stable path node: parent/group/id (FIRES_UI_STYLE_GUIDE.md principle 8).</summary>
        public static RectTransform PathNode(RectTransform parent, string group, string id) => Node(Group(parent, group), id);

        public static RectTransform Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        /// <summary>Horizontally centred at the parent's middle, y from the top.</summary>
        public static RectTransform PlaceCentered(RectTransform rt, float y, float w, float h, float xOffset = 0f)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(xOffset, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        /// <summary>Anchored to the parent's top-right; x is the distance from the right edge.</summary>
        public static RectTransform PlaceRight(RectTransform rt, float xFromRight, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-xFromRight, -y);
            rt.sizeDelta = new Vector2(w, h);
            return rt;
        }

        public static RectTransform Stretch(RectTransform rt, float left, float top, float right, float bottom)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
            return rt;
        }

        public static Image Box(RectTransform parent, string name, Sprite sprite, Color color, bool sliced = true)
        {
            var rt = Node(parent, name);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.type = sliced && sprite != null && sprite.border != Vector4.zero ? Image.Type.Sliced : Image.Type.Simple;
            img.raycastTarget = false;
            return img;
        }

        public static Image Fill(RectTransform parent, string name, Color color) => Box(parent, name, KitSprites.White(), color, false);

        /// <summary>A 1 px horizontal hairline.</summary>
        public static Image Hairline(RectTransform parent, string name, float x, float y, float w, Color color)
        {
            var img = Fill(parent, name, color);
            Place(img.rectTransform, x, y, w, 1f);
            return img;
        }

        /// <summary>A drawn diamond centred on (cx, cy): Learned/filled, Available/outlined in GoldDim, Locked/outlined faint.</summary>
        public static RectTransform Diamond(RectTransform parent, string name, float cx, float cy, float size, Color outline,
            Color fill, bool filled)
        {
            var root = Node(parent, name);
            Place(root, cx - size * 0.5f, cy - size * 0.5f, size, size);
            // a square of side s rotated 45 deg spans s*sqrt2; size is the on-screen extent
            float side = size * 0.70710678f;
            if (filled && fill.a > 0f && outline == fill)
            {
                var d = Box(root, "Fill", KitSprites.Diamond(), fill, false);
                Stretch(d.rectTransform, 0, 0, 0, 0);
                root.gameObject.AddComponent<KitLeaf>();
                return root;
            }
            if (fill.a > 0f)
            {
                var f = Fill(root, "Fill", fill);
                CenterRotated(f.rectTransform, side);
            }
            var o = Box(root, "Outline", KitSprites.BoxOutline(), outline, true);
            CenterRotated(o.rectTransform, side);
            root.gameObject.AddComponent<KitLeaf>();
            return root;
        }

        static void CenterRotated(RectTransform rt, float side)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(side, side);
            rt.localRotation = Quaternion.Euler(0f, 0f, 45f);
        }

        static KitLabel Label(RectTransform parent, string name, string text, FontRole role, float size, Color color,
            KitAlign align, float tracking = 0f, bool wrap = false)
            => KitText.Create(parent, name, text, role, size, color, align, tracking, true, wrap);

        // ---- Window --------------------------------------------------------------------------------

        /// <summary>[Window/&lt;id&gt;] Panel fill + double hairline frame (Square or Chamfer45), centred in parent.</summary>
        public static KitWindow Window(RectTransform parent, string id, Vector2 size, Corner corner = Corner.Rounded, float chamfer = FiresTokens.ChamferSize)
        {
            size = Vector2.Max(size, FiresTokens.MinWindowSize);
            var root = PathNode(parent, "Window", id);
            root.anchorMin = root.anchorMax = root.pivot = new Vector2(0.5f, 0.5f);
            root.anchoredPosition = Vector2.zero;
            root.sizeDelta = size;

            var w = new KitWindow { Root = root };
            w.Fill = Box(root, "Fill", KitSprites.FrameFill(corner, chamfer), FiresTokens.Panel);
            Stretch(w.Fill.rectTransform, 0, 0, 0, 0);
            w.Frame = Box(root, "Frame", KitSprites.FrameLines(corner, chamfer), Color.white);
            Stretch(w.Frame.rectTransform, 0, 0, 0, 0);
            w.Content = Stretch(Node(root, "Content"), FiresTokens.WindowPadding + FiresTokens.S8, FiresTokens.S24,
                FiresTokens.WindowPadding + FiresTokens.S8, FiresTokens.S16);
            w.Close = Close(root);
            return w;
        }

        /// <summary>[Close] a plain x in TextMuted, top-right. Never a red square.</summary>
        public static KitLabel Close(RectTransform parent)
        {
            var l = Label(parent, "Close", FiresTokens.GlyphClose, FontRole.SansRegular, FiresTokens.CloseSize,
                FiresTokens.TextMuted, KitAlign.Center);
            PlaceRight(l.Rect, FiresTokens.CloseInset, FiresTokens.S8, 32f, 36f);
            return l;
        }

        // ---- Header --------------------------------------------------------------------------------

        /// <summary>[Header] context strip (pills left, context right), centred CAPS title, divider + diamond.</summary>
        public static KitHeader Header(RectTransform parent, string contextText, string title)
        {
            var root = Node(parent, "Header");
            root.anchorMin = new Vector2(0f, 1f); root.anchorMax = new Vector2(1f, 1f);
            root.pivot = new Vector2(0.5f, 1f);
            root.anchoredPosition = Vector2.zero;
            float height = FiresTokens.HeaderHeight;
            root.sizeDelta = new Vector2(0f, height);

            var h = new KitHeader { Root = root, Height = height };
            h.Pills = Node(root, "Pills");
            Place(h.Pills, 0, 0, 900f, FiresTokens.ContextStripHeight);

            h.Context = Label(root, "Context", contextText, FontRole.SerifBold, FiresTokens.ContextSize, FiresTokens.Gold, KitAlign.Right);
            PlaceRight(h.Context.Rect, FiresTokens.S32, 0, 700f, FiresTokens.ContextStripHeight);

            h.Title = Label(root, "Title", title.ToUpperInvariant(), FontRole.SerifRegular, FiresTokens.TitleSize,
                FiresTokens.Gold, KitAlign.Center, FiresTokens.TitleTracking);
            PlaceCentered(h.Title.Rect, FiresTokens.ContextStripHeight + 14f, 900f, 70f);

            Divider(root, FiresTokens.ContextStripHeight + 96f, FiresTokens.DividerWidth);
            return h;
        }

        /// <summary>[Divider] a 1 px line fading GoldDim -> Gold -> GoldDim to transparent, with a drawn diamond.</summary>
        public static RectTransform Divider(RectTransform parent, float y, float width)
        {
            var d = Node(parent, "Divider");
            PlaceCentered(d, y, width, 16f);
            var line = Box(d, "Line", KitSprites.FadeLine(FiresTokens.GoldDim, FiresTokens.Gold, FadeMode.Center), Color.white, false);
            Place(line.rectTransform, 0, 7.5f, width, 1f);
            Diamond(d, "Diamond", width * 0.5f, 8f, FiresTokens.DividerDiamondSize + 3f, FiresTokens.Gold, FiresTokens.Gold, true);
            return d;
        }

        // ---- Tab rail / sub-tabs --------------------------------------------------------------------

        /// <summary>[Tabs/&lt;id&gt;] centred CAPS tabs; active = Gold + 2 px underline; badge = GoldBright dot.</summary>
        public static RectTransform TabRail(RectTransform parent, float y, (string id, string title)[] tabs, string activeId,
            ICollection<string> badges = null)
        {
            var root = Group(parent, "Tabs");
            Place(root, 0, y, 0, 38f);
            root.anchorMin = new Vector2(0f, 1f); root.anchorMax = new Vector2(1f, 1f);
            root.sizeDelta = new Vector2(0f, 38f);
            var rule = Fill(root, "Rule", FiresTokens.Hairline);
            rule.rectTransform.anchorMin = new Vector2(0f, 0f); rule.rectTransform.anchorMax = new Vector2(1f, 0f);
            rule.rectTransform.pivot = new Vector2(0.5f, 0f);
            rule.rectTransform.anchoredPosition = Vector2.zero;
            rule.rectTransform.sizeDelta = new Vector2(0f, 1f);

            var labels = new List<KitLabel>();
            var widths = new List<float>();
            float total = 0f;
            foreach (var t in tabs)
            {
                var node = Node(root, t.id);
                bool on = t.id == activeId;
                var l = KitText.Create(node, "Label", t.title.ToUpperInvariant(), FontRole.SansBold, FiresTokens.TabSize,
                    on ? FiresTokens.Gold : FiresTokens.TextMuted, KitAlign.Center, FiresTokens.TabTracking, false);
                float w = Mathf.Ceil(l.PreferredWidth) + 4f;
                labels.Add(l); widths.Add(w);
                total += w;
            }
            total += FiresTokens.TabGap * (tabs.Length - 1);
            float x = -total * 0.5f;
            for (int i = 0; i < tabs.Length; i++)
            {
                var node = (RectTransform)labels[i].Rect.parent;
                PlaceCentered(node, 0, widths[i], 38f, x + widths[i] * 0.5f);
                Stretch(labels[i].Rect, 0, 0, 0, 2f);
                if (tabs[i].id == activeId)
                {
                    var u = Fill(node, "Underline", FiresTokens.Gold);
                    u.rectTransform.anchorMin = new Vector2(0f, 0f); u.rectTransform.anchorMax = new Vector2(1f, 0f);
                    u.rectTransform.pivot = new Vector2(0.5f, 0f);
                    u.rectTransform.anchoredPosition = Vector2.zero;
                    u.rectTransform.sizeDelta = new Vector2(0f, FiresTokens.TabUnderline);
                }
                if (badges != null && badges.Contains(tabs[i].id))
                {
                    var b = Box(node, "Badge", KitSprites.Circle(), FiresTokens.GoldBright, false);
                    PlaceRight(b.rectTransform, -FiresTokens.BadgeSize - 3f, 8f, FiresTokens.BadgeSize, FiresTokens.BadgeSize);
                }
                x += widths[i] + FiresTokens.TabGap;
            }
            return root;
        }

        /// <summary>[Pills/&lt;id&gt;] centred sub-view pills; active = PanelRaised fill + Gold text.</summary>
        public static RectTransform SubTabs(RectTransform parent, float y, (string id, string title)[] tabs, string activeId)
        {
            var root = Group(parent, "Pills");
            root.anchorMin = new Vector2(0f, 1f); root.anchorMax = new Vector2(1f, 1f);
            root.pivot = new Vector2(0.5f, 1f);
            root.anchoredPosition = new Vector2(0f, -y);
            root.sizeDelta = new Vector2(0f, 32f);
            var nodes = new List<RectTransform>();
            var widths = new List<float>();
            float total = 0f;
            foreach (var t in tabs)
            {
                var node = Node(root, t.id);
                bool on = t.id == activeId;
                if (on)
                {
                    var bg = Box(node, "Fill", KitSprites.RoundPill(FiresTokens.PillRadius, false), FiresTokens.Selected);
                    Stretch(bg.rectTransform, 0, 0, 0, 0);
                }
                var ol = Box(node, "Outline", KitSprites.RoundPill(FiresTokens.PillRadius, true), on ? FiresTokens.Gold : FiresTokens.Hairline);
                Stretch(ol.rectTransform, 0, 0, 0, 0);
                var l = KitText.Create(node, "Label", t.title, FontRole.SerifRegular, FiresTokens.SubTabSize,
                    on ? FiresTokens.Gold : FiresTokens.TextMuted, KitAlign.Center, 0f, false);
                Stretch(l.Rect, 0, 0, 0, 0);
                float w = Mathf.Ceil(l.PreferredWidth) + FiresTokens.S48;
                nodes.Add(node); widths.Add(w); total += w;
            }
            total += FiresTokens.SubTabGap * (tabs.Length - 1);
            float x = -total * 0.5f;
            for (int i = 0; i < nodes.Count; i++)
            {
                PlaceCentered(nodes[i], 0, widths[i], 32f, x + widths[i] * 0.5f);
                x += widths[i] + FiresTokens.SubTabGap;
            }
            return root;
        }

        // ---- Section / rows -------------------------------------------------------------------------

        /// <summary>[Section/&lt;id&gt;] CAPS gold header + fading hairline underline; rows stack below.</summary>
        public static KitSection Section(RectTransform parent, string id, string title, float x, float y, float width, string note = null)
        {
            var root = PathNode(parent, "Section", id);
            Place(root, x, y, width, 0f);
            var s = new KitSection { Root = root, Width = width };
            var t = KitText.Create(root, "Title", title.ToUpperInvariant(), FontRole.SansBold, FiresTokens.SectionSize,
                FiresTokens.Gold, KitAlign.Left, FiresTokens.SectionTracking, false);
            Place(t.Rect, 0, 0, width, 22f);
            if (!string.IsNullOrEmpty(note))
            {
                float tw = Mathf.Ceil(t.PreferredWidth);
                t.Rect.sizeDelta = new Vector2(tw + 2f, 20f);
                var n = KitText.Create(root, "Note", note, FontRole.SansRegular, FiresTokens.HintSize, FiresTokens.TextFaint, KitAlign.Left);
                Place(n.Rect, tw + FiresTokens.S12, 0, width - tw - FiresTokens.S12, 20f);
            }
            var u = Box(root, "Underline", KitSprites.FadeLine(FiresTokens.GoldDim, FiresTokens.GoldDim, FadeMode.FromLeft), Color.white, false);
            Place(u.rectTransform, 0, 28f, width, 1f);
            s.Rows = Group(root, "Row");
            s.Y = 38f;
            return s;
        }

        static void Grow(KitSection s, float h)
        {
            s.Y += h;
            s.Root.sizeDelta = new Vector2(s.Width, s.Y);
        }

        /// <summary>[Row/&lt;id&gt;/Label|Value] muted label left, bold value right; Warn/Danger only on a real threshold.</summary>
        public static KitRow Row(KitSection s, string id, string label, string value, RowState state = RowState.Normal)
        {
            var root = Node(s.Rows, id);
            Place(root, 0, s.Y, s.Width, FiresTokens.RowHeight);
            var r = new KitRow { Root = root };
            r.Label = Label(root, "Label", label, FontRole.SerifRegular, FiresTokens.BodySize, FiresTokens.TextMuted, KitAlign.Left);
            Place(r.Label.Rect, 0, 0, s.Width * 0.62f, FiresTokens.RowHeight);
            r.Value = Label(root, "Value", value, FontRole.SansBold, FiresTokens.ValueSize, FiresTokens.Text, KitAlign.Right);
            PlaceRight(r.Value.Rect, 0, 0, s.Width * 0.38f, FiresTokens.RowHeight);
            r.State = state;
            Grow(s, FiresTokens.RowHeight);
            return r;
        }

        /// <summary>
        /// [Row/&lt;id&gt;] settings row: name, 1-line description, "Default: X" (or the server-lock warning), the control
        /// on the right (built by 'control' into a 320-wide area), and a << reset only when not at default.
        /// </summary>
        public static RectTransform SettingsRow(KitSection s, string id, string name, string desc, string defaultText,
            Action<RectTransform> control, bool showReset = false, bool lockedByServer = false)
        {
            float h = FiresTokens.SettingsRowTallHeight;
            var root = Node(s.Rows, id);
            Place(root, 0, s.Y, s.Width, h);
            const float controlW = 320f, resetW = 30f;
            float textW = s.Width - controlW - resetW - FiresTokens.S16;
            var n = Label(root, "Name", name, FontRole.SerifRegular, FiresTokens.BodySize, FiresTokens.Text, KitAlign.Left);
            Place(n.Rect, 0, 6f, textW, 22f);
            var d = Label(root, "Desc", desc, FontRole.SerifRegular, FiresTokens.DescSize, FiresTokens.TextMuted, KitAlign.Left);
            Place(d.Rect, 0, 27f, textW, 18f);
            var df = Label(root, "Default", lockedByServer ? "Locked by the server" : "Default: " + defaultText,
                FontRole.SansRegular, FiresTokens.HintSize - 1f, lockedByServer ? FiresTokens.Warn : FiresTokens.TextFaint, KitAlign.Left);
            Place(df.Rect, 0, 44f, textW, 16f);

            var area = Node(root, "Control");
            PlaceRight(area, resetW, 0, controlW, h);
            control?.Invoke(area);

            if (showReset)
            {
                var rs = Label(root, "Reset", FiresTokens.GlyphReset, FontRole.SansRegular, 18f, FiresTokens.GoldDim, KitAlign.Center);
                PlaceRight(rs.Rect, 0, 0, resetW, h);
            }
            Hairline(root, "Rule", 0, h - 1f, s.Width, FiresTokens.Hairline);
            Grow(s, h);
            return root;
        }

        // ---- controls -------------------------------------------------------------------------------

        /// <summary>[Toggle/&lt;id&gt;/Switch|Label] rounded pill switch + CAPS label (rounded kit; the method keeps its name).
        /// ON = Gold-tinted track, Gold outline, Gold knob right, Text label; OFF = Well track, TextFaint outline, knob left, TextMuted label.
        /// Placed right-aligned inside 'parent' when rightAlign, else at (x, y). Returns the root.</summary>
        public static RectTransform DiamondToggle(RectTransform parent, string id, string label, bool on, float x, float y,
            float height, bool rightAlign = false)
        {
            var root = PathNode(parent, "Toggle", id);
            var l = Label(root, "Label", label.ToUpperInvariant(), FontRole.SansBold, 14f,
                on ? FiresTokens.Text : FiresTokens.TextMuted, KitAlign.Left, FiresTokens.SectionTracking);
            float lw = Mathf.Ceil(l.PreferredWidth) + 2f;
            float sw = FiresTokens.SwitchWidth, sh = FiresTokens.SwitchHeight, kd = FiresTokens.SwitchKnob;
            float w = sw + FiresTokens.S8 + lw;
            if (rightAlign) PlaceRight(root, x, y, w, height); else Place(root, x, y, w, height);
            var sn = Node(root, "Switch");
            Place(sn, 0, (height - sh) * 0.5f, sw, sh);
            Stretch(Box(sn, "Track", KitSprites.RoundedFill(sh * 0.5f), on ? FiresTokens.WithAlpha(FiresTokens.Gold, 0.25f) : FiresTokens.Well).rectTransform, 0, 0, 0, 0);
            Stretch(Box(sn, "Outline", KitSprites.RoundedOutline(sh * 0.5f), on ? FiresTokens.Gold : FiresTokens.TextFaint).rectTransform, 0, 0, 0, 0);
            float pad = (sh - kd) * 0.5f;
            Place(Box(sn, "Knob", KitSprites.Circle(), on ? FiresTokens.Gold : FiresTokens.TextFaint, false).rectTransform,
                on ? sw - pad - kd : pad, pad, kd, kd);
            sn.gameObject.AddComponent<KitLeaf>();
            Place(l.Rect, sw + FiresTokens.S8, 0, lw, height);
            return root;
        }

        /// <summary>[Segmented/&lt;id&gt;/Seg/&lt;option&gt;] rounded outer pill (Well + Hairline) holding rounded segments; the selected
        /// segment = Selected fill + Gold outline + Gold label. For enums and tri-states (Auto / On / Off). rightAlign: (x, y) is
        /// measured from parent's right edge. Returns the root; its width is sizeDelta.x.</summary>
        public static RectTransform Segmented(RectTransform parent, string id, string[] options, int selected, float x, float y,
            float height = FiresTokens.SegmentHeight, bool rightAlign = false)
        {
            var root = PathNode(parent, "Segmented", id);
            const float pad = 3f, gap = 2f;
            float sh = height - pad * 2f;
            var outerFill = Box(root, "Fill", KitSprites.RoundedFill(height * 0.5f), FiresTokens.Well);
            var outerLine = Box(root, "Outline", KitSprites.RoundedOutline(height * 0.5f), FiresTokens.Hairline);
            var nodes = new RectTransform[options.Length];
            var widths = new float[options.Length];
            float total = pad * 2f + gap * (options.Length - 1);
            for (int i = 0; i < options.Length; i++)
            {
                bool on = i == selected;
                nodes[i] = PathNode(root, "Seg", options[i].ToLowerInvariant().Replace(" ", "_"));
                if (on)
                {
                    Stretch(Box(nodes[i], "Fill", KitSprites.RoundedFill(sh * 0.5f), FiresTokens.Selected).rectTransform, 0, 0, 0, 0);
                    Stretch(Box(nodes[i], "Outline", KitSprites.RoundedOutline(sh * 0.5f), FiresTokens.Gold).rectTransform, 0, 0, 0, 0);
                }
                var l = Label(nodes[i], "Label", options[i], FontRole.SansBold, 14f, on ? FiresTokens.Gold : FiresTokens.TextMuted, KitAlign.Center);
                Stretch(l.Rect, 0, 0, 0, 0);
                widths[i] = Mathf.Max(52f, Mathf.Ceil(l.PreferredWidth) + 22f);
                total += widths[i];
            }
            if (rightAlign) PlaceRight(root, x, y, total, height); else Place(root, x, y, total, height);
            Stretch(outerFill.rectTransform, 0, 0, 0, 0);
            Stretch(outerLine.rectTransform, 0, 0, 0, 0);
            float sx = pad;
            for (int i = 0; i < options.Length; i++)
            {
                Place(nodes[i], sx, pad, widths[i], sh);
                nodes[i].gameObject.AddComponent<KitLeaf>();
                sx += widths[i] + gap;
            }
            return root;
        }

        /// <summary>[&lt;name&gt;/Fill|Frame] rounded input well (RadiusSmall): Well fill + 1 px outline (Hairline unless given).
        /// Inputs, dropdown boxes, keycaps, list wells. Popovers use radius = FiresTokens.Radius and outline Edge.</summary>
        public static RectTransform Well(RectTransform parent, string name, float x, float y, float w, float h,
            Color? outline = null, float radius = FiresTokens.RadiusSmall)
        {
            var n = Node(parent, name);
            Place(n, x, y, w, h);
            Stretch(Box(n, "Fill", KitSprites.RoundedFill(radius), FiresTokens.Well).rectTransform, 0, 0, 0, 0);
            Stretch(Box(n, "Frame", KitSprites.RoundedOutline(radius), outline ?? FiresTokens.Hairline).rectTransform, 0, 0, 0, 0);
            return n;
        }

        /// <summary>[ItemCell/&lt;id&gt;/Frame|Icon|Count] an inventory-style cell: rounded Well frame, the icon at ItemCellIconFraction
        /// of the cell centred at the top, the count on a small dark rounded strip in the bottom-right corner BELOW the icon rect
        /// (never over it). count of 1 or less draws no strip. KitLeaf on the cell.</summary>
        public static RectTransform ItemCell(RectTransform parent, string id, Sprite icon, int count, float x, float y, float size)
        {
            var root = PathNode(parent, "ItemCell", id);
            Place(root, x, y, size, size);
            var frame = Node(root, "Frame");
            Stretch(frame, 0, 0, 0, 0);
            Stretch(Box(frame, "Fill", KitSprites.RoundedFill(FiresTokens.RadiusSmall), FiresTokens.Well).rectTransform, 0, 0, 0, 0);
            Stretch(Box(frame, "Outline", KitSprites.RoundedOutline(FiresTokens.RadiusSmall), FiresTokens.Hairline).rectTransform, 0, 0, 0, 0);
            float isz = Mathf.Round(size * FiresTokens.ItemCellIconFraction);
            float top = Mathf.Round(size * 0.04f);
            var ic = Box(root, "Icon", icon, Color.white, false);
            ic.preserveAspect = true;
            Place(ic.rectTransform, (size - isz) * 0.5f, top, isz, isz);
            if (count > 1)
            {
                var cn = Node(root, "Count");
                float ch = Mathf.Max(10f, size - top - isz - 3f);   // the strip lives in the band under the icon
                var cl = Label(cn, "Label", count.ToString(), FontRole.SansBold, Mathf.Min(13f, ch - 1f), FiresTokens.Text, KitAlign.Center);
                float cw = Mathf.Ceil(cl.PreferredWidth) + 8f;
                PlaceRight(cn, 3f, top + isz + 1f, cw, ch);
                var strip = Box(cn, "Strip", KitSprites.RoundedFill(Mathf.Min(FiresTokens.RadiusSmall, ch * 0.5f)), FiresTokens.WithAlpha(FiresTokens.Backdrop, 0.9f));
                strip.transform.SetAsFirstSibling();
                Stretch(strip.rectTransform, 0, 0, 0, 0);
                Stretch(cl.Rect, 0, 0, 0, 0);
            }
            root.gameObject.AddComponent<KitLeaf>();
            return root;
        }

        /// <summary>[Pill/&lt;id&gt;] status capsule: dot (Ok when ON, TextFaint when OFF) + label. Returns width via sizeDelta.</summary>
        public static RectTransform Pill(RectTransform parent, string id, string label, bool on, float x, float y)
        {
            var root = PathNode(parent, "Pill", id);
            var bg = Box(root, "Fill", KitSprites.RoundPill(13f, false), FiresTokens.WithAlpha(FiresTokens.Well, on ? FiresTokens.PillWellAlpha : 0f));
            var ol = Box(root, "Outline", KitSprites.RoundPill(13f, true), on ? FiresTokens.Edge : FiresTokens.Hairline);
            var dot = Box(root, "Dot", KitSprites.Circle(), on ? FiresTokens.Ok : FiresTokens.TextFaint, false);
            var l = Label(root, "Label", label, FontRole.SansBold, FiresTokens.PillTextSize,
                on ? FiresTokens.Text : FiresTokens.TextMuted, KitAlign.Left, 4f);
            float lw = Mathf.Ceil(l.PreferredWidth) + 2f;
            float w = 24f + lw + 14f, h = 26f;
            Place(root, x, y, w, h);
            Stretch(bg.rectTransform, 0, 0, 0, 0);
            Stretch(ol.rectTransform, 0, 0, 0, 0);
            Place(dot.rectTransform, 11f, (h - 7f) * 0.5f, 7f, 7f);
            Place(l.Rect, 24f, 0, lw, h);
            root.gameObject.AddComponent<KitLeaf>();
            return root;
        }

        /// <summary>[Slider/&lt;id&gt;/Track|Fill|Knob|Value] pill track (SliderTrack high, fully rounded ends, Hairline), Gold filled part
        /// (also a pill), ROUND Text knob (SliderKnob), value right in Value style.</summary>
        public static RectTransform Slider(RectTransform parent, string id, float value01, string text, float x, float y, float width, float height)
        {
            var root = PathNode(parent, "Slider", id);
            Place(root, x, y, width, height);
            const float valueW = 52f;
            float trackW = width - valueW - FiresTokens.S12;
            float cy = height * 0.5f, th = FiresTokens.SliderTrack;
            float v = Mathf.Clamp01(value01);
            var pill = KitSprites.RoundedFill(th * 0.5f);
            Place(Box(root, "Track", pill, FiresTokens.Hairline).rectTransform, 0, cy - th * 0.5f, trackW, th);
            Place(Box(root, "Fill", pill, FiresTokens.Gold).rectTransform, 0, cy - th * 0.5f, Mathf.Max(th, trackW * v), th);
            float k = FiresTokens.SliderKnob;
            Place(Box(root, "Knob", KitSprites.Circle(), FiresTokens.Text, false).rectTransform,
                Mathf.Clamp(trackW * v - k * 0.5f, 0f, trackW - k), cy - k * 0.5f, k, k);
            var vl = Label(root, "Value", text, FontRole.SansBold, FiresTokens.ValueSize - 1f, FiresTokens.Text, KitAlign.Right);
            PlaceRight(vl.Rect, 0, 0, valueW, height);
            return root;
        }

        /// <summary>[Button/&lt;id&gt;] rounded (RadiusSmall). Primary = Gold on PanelRaised with an Edge frame; Destructive = Danger
        /// text + frame; Disabled = TextFaint label, Hairline frame, no gold, raycastTarget off on every graphic.</summary>
        public static RectTransform Button(RectTransform parent, string id, string label, ButtonKind kind, float x, float y,
            float minWidth = 0f, float height = 30f)
        {
            var root = PathNode(parent, "Button", id);
            bool off = kind == ButtonKind.Disabled;
            var bg = Box(root, "Fill", KitSprites.Pill(), off ? FiresTokens.Well : FiresTokens.PanelRaised);
            var ol = Box(root, "Outline", KitSprites.PillOutline(),
                kind == ButtonKind.Destructive ? FiresTokens.Danger : off ? FiresTokens.Hairline : FiresTokens.Edge);
            var l = Label(root, "Label", label, FontRole.SerifBold, 15f,
                kind == ButtonKind.Destructive ? FiresTokens.Danger : off ? FiresTokens.TextFaint : FiresTokens.Gold, KitAlign.Center);
            float w = Mathf.Max(minWidth, Mathf.Ceil(l.PreferredWidth) + FiresTokens.S24);
            Place(root, x, y, w, height);
            Stretch(bg.rectTransform, 0, 0, 0, 0);
            Stretch(ol.rectTransform, 0, 0, 0, 0);
            Stretch(l.Rect, 0, 0, 0, 0);
            if (off) foreach (var g in root.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            root.gameObject.AddComponent<KitLeaf>();
            return root;
        }

        // ---- talent node (KIT RULE: a node is a fixed vertical block; rows stack by block height, never overlap) -------

        /// <summary>
        /// [Node/&lt;id&gt;/Halo|Ring|Pip1..n|Name|Sub] a talent node as one fixed block laid out by NodeLayout:
        /// PadTop, Ring, RingToPips, the pip line (reserved even with no pips), PipsToName, Name, Sub, PadBottom.
        /// Placed at (0, y) in 'parent', 'width' wide, centred. Returns NodeLayout.Pitch; the caller adds it to y.
        /// Halo always exists (stable name): a soft Gold disc behind the ring, visible for Learned/Available only.
        /// </summary>
        public static float TalentNode(RectTransform parent, string id, float y, float width, string name, string level,
            NodeState state, int rank, int maxRank = 3)
        {
            var node = PathNode(parent, "Node", id);
            float ring = NodeLayout.Ring, cx = width * 0.5f;
            float ringCy = NodeLayout.PadTop + ring * 0.5f;
            Color outline = state == NodeState.Learned ? FiresTokens.Gold : state == NodeState.Available ? FiresTokens.GoldDim : FiresTokens.TextFaint;
            Color fill = state == NodeState.Learned ? FiresTokens.WithAlpha(FiresTokens.Gold, FiresTokens.LearnedFillAlpha) : FiresTokens.WithAlpha(FiresTokens.Well, 0f);

            float halo = NodeLayout.Halo;
            float haloA = state == NodeState.Learned ? 0.14f : state == NodeState.Available ? 0.06f : 0f;
            var hl = Box(node, "Halo", KitSprites.Circle(), FiresTokens.WithAlpha(FiresTokens.Gold, haloA), false);
            Place(hl.rectTransform, cx - halo * 0.5f, ringCy - halo * 0.5f, halo, halo);
            hl.gameObject.AddComponent<KitLeaf>();

            Diamond(node, "Ring", cx, ringCy, ring, outline, fill, false);

            float cy = NodeLayout.PadTop + ring + NodeLayout.RingToPips;
            if (rank > 0 && maxRank > 0)
            {
                float dot = NodeLayout.PipSize, gap = NodeLayout.PipGap;
                float total = maxRank * dot + (maxRank - 1) * gap;
                for (int r = 0; r < maxRank; r++)
                {
                    var pip = Box(node, "Pip" + (r + 1), KitSprites.Circle(), r < rank ? FiresTokens.Gold : FiresTokens.TextFaint, false);
                    Place(pip.rectTransform, cx - total * 0.5f + r * (dot + gap), cy, dot, dot);
                    pip.gameObject.AddComponent<KitLeaf>();
                }
            }
            cy += NodeLayout.PipSize + NodeLayout.PipsToName;   // the pip line is reserved: every block is the same height

            var n = Label(node, "Name", name, FontRole.SerifRegular, 15f, state == NodeState.Locked ? FiresTokens.TextFaint : FiresTokens.Text, KitAlign.Center);
            Place(n.Rect, 0, cy, width, NodeLayout.NameH); cy += NodeLayout.NameH;
            var l = Label(node, "Sub", level, FontRole.SansRegular, 12f, FiresTokens.TextFaint, KitAlign.Center);
            Place(l.Rect, 0, cy, width, NodeLayout.SubH);

            Place(node, 0, y, width, NodeLayout.Pitch);
            return NodeLayout.Pitch;
        }

        // ---- overlays -------------------------------------------------------------------------------

        /// <summary>[Tooltip] Well fill, Edge frame, title Gold, body Text, meta TextMuted; height fits the text.</summary>
        public static RectTransform Tooltip(RectTransform parent, string title, string body, string meta, float x, float y,
            float width = 300f)
        {
            width = Mathf.Min(width, FiresTokens.TooltipMaxWidth);
            var root = Node(parent, "Tooltip");
            var shadow = Box(root, "Shadow", KitSprites.Pill(), FiresTokens.WithAlpha(FiresTokens.Backdrop, 0.55f));
            var bg = Box(root, "Fill", KitSprites.RoundPill(FiresTokens.TooltipRadius, false), FiresTokens.Well);
            var ol = Box(root, "Frame", KitSprites.RoundPill(FiresTokens.TooltipRadius, true), FiresTokens.Edge);
            float pad = FiresTokens.TooltipPadding + 4f, inner = width - pad * 2f, cy = pad;
            var t = Label(root, "Title", title, FontRole.SerifBold, 16f, FiresTokens.Gold, KitAlign.TopLeft);
            Place(t.Rect, pad, cy, inner, 22f); cy += 24f;
            var b = KitText.Create(root, "Body", body, FontRole.SerifRegular, 15f, FiresTokens.Text, KitAlign.TopLeft, 0f, false, true);
            Place(b.Rect, pad, cy, inner, 0f);
            float bh = Mathf.Ceil(b.PreferredHeight(inner));
            b.Rect.sizeDelta = new Vector2(inner, bh); cy += bh + 6f;
            if (!string.IsNullOrEmpty(meta))
            {
                var m = KitText.Create(root, "Meta", meta, FontRole.SerifRegular, FiresTokens.DescSize, FiresTokens.TextMuted, KitAlign.TopLeft, 0f, false, true);
                Place(m.Rect, pad, cy, inner, 0f);
                float mh = Mathf.Ceil(m.PreferredHeight(inner));
                m.Rect.sizeDelta = new Vector2(inner, mh); cy += mh;
            }
            float h = cy + pad;
            Place(root, x, y, width, h);
            Stretch(bg.rectTransform, 0, 0, 0, 0);
            Stretch(ol.rectTransform, 0, 0, 0, 0);
            Stretch(shadow.rectTransform, -6f, 2f, -6f, -12f);
            return root;
        }

        /// <summary>[Toast] Text on Well with an Ok/Danger left edge; bottom-centre of 'parent', above its bottom by 'bottom'.</summary>
        public static RectTransform Toast(RectTransform parent, string text, ToastKind kind, float bottom)
        {
            var root = Node(parent, "Toast");
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 0f);
            root.pivot = new Vector2(0.5f, 0f);
            root.anchoredPosition = new Vector2(0f, bottom);
            root.sizeDelta = new Vector2(FiresTokens.ToastWidth, 40f);
            Stretch(Box(root, "Fill", KitSprites.RoundedFill(FiresTokens.RadiusSmall), FiresTokens.Well).rectTransform, 0, 0, 0, 0);
            Stretch(Box(root, "Frame", KitSprites.RoundedOutline(FiresTokens.RadiusSmall), FiresTokens.Hairline).rectTransform, 0, 0, 0, 0);
            var edge = Fill(root, "Edge", kind == ToastKind.Danger ? FiresTokens.Danger : kind == ToastKind.Ok ? FiresTokens.Ok : FiresTokens.Gold);
            Place(edge.rectTransform, 2f, 8f, FiresTokens.ToastEdge, 24f);   // inset so the square edge stays inside the rounded corners
            var l = Label(root, "Label", text, FontRole.SerifRegular, 15f, FiresTokens.Text, KitAlign.Left);
            Stretch(l.Rect, FiresTokens.S16, 0, FiresTokens.S12, 0);
            return root;
        }

        /// <summary>[Footer] centred key hints: keys in Text, actions in TextFaint; optional trailing note.</summary>
        /// <summary>Variant "footer": footer hint actions/note use TextFaintV2 instead of TextFaint. Off by default (v1).</summary>
        public static bool FooterHintV2 = false;

        public static KitLabel Footer(RectTransform parent, (string key, string action)[] hints, string note = null)
        {
            var faint = FooterHintV2 ? FiresTokens.TextFaintV2 : FiresTokens.TextFaint;
            var sb = new StringBuilder();
            const string gap = "      ";
            for (int i = 0; i < hints.Length; i++)
            {
                if (i > 0) sb.Append(gap);
                sb.Append(KitText.Tint(hints[i].key, FiresTokens.Text));
                sb.Append(KitText.Tint(FiresTokens.GlyphSep + hints[i].action, faint));
            }
            if (!string.IsNullOrEmpty(note)) sb.Append(gap).Append(KitText.Tint(note, faint));
            var l = Label(parent, "Footer", sb.ToString(), FontRole.SansRegular, FiresTokens.HintSize + 1f, faint, KitAlign.Center);
            var rt = l.Rect;
            rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, FiresTokens.FooterBottom - FiresTokens.S16);
            rt.sizeDelta = new Vector2(0f, 22f);
            return l;
        }

        // ---- backdrop -------------------------------------------------------------------------------

        /// <summary>[Backdrop] full-screen scrim (vignette) + haze + dust, offset by mouse and time (FIRES_UI_SHELL.md S10).</summary>
        /// <summary>galaxy = true: opaque procedural galaxy (Nebula + Stars + Dust motes, slow linear drift per layer)
        /// instead of the scrim over the live world.</summary>
        /// brightMargins (variant "margins", off = v1): rim-coloured nebula + a screen-fixed GalaxyShade that keeps the
        /// area behind the window dark.
        public static KitBackdrop Backdrop(RectTransform parent, IShellHost host, bool galaxy = false, bool brightMargins = false)
        {
            var root = Stretch(Node(parent, "Backdrop"), 0, 0, 0, 0);
            var b = root.gameObject.AddComponent<KitBackdrop>();
            b.Host = host;
            b.Galaxy = galaxy;
            if (galaxy)
            {
                b.Nebula = Raw(root, "Nebula", KitSprites.Nebula(brightMargins), Color.white);
                b.Stars = Raw(root, "Stars", KitSprites.Stars(), FiresTokens.WithAlpha(FiresTokens.Text, FiresTokens.StarAlpha));
                if (brightMargins) b.Shade = Raw(root, "Shade", KitSprites.GalaxyShade(), Color.white);   // over the stars: dims them behind the window too
                b.Dust = Raw(root, "Dust", KitSprites.Dust(), FiresTokens.WithAlpha(FiresTokens.GoldBright, FiresTokens.DustAlpha));
                b.Apply();
                return b;
            }
            b.Scrim = Raw(root, "Scrim", KitSprites.Vignette(), Color.white);
            b.Haze = Raw(root, "Haze", KitSprites.Noise(), FiresTokens.WithAlpha(FiresTokens.Gold, FiresTokens.HazeAlpha));
            b.Dust = Raw(root, "Dust", KitSprites.Dust(), FiresTokens.WithAlpha(FiresTokens.GoldBright, FiresTokens.DustAlpha));
            b.Apply();
            return b;
        }

        static RawImage Raw(RectTransform parent, string name, Texture tex, Color color)
        {
            var rt = Stretch(Node(parent, name), 0, 0, 0, 0);
            var r = rt.gameObject.AddComponent<RawImage>();
            r.texture = tex;
            r.color = color;
            r.raycastTarget = false;
            return r;
        }
    }

    /// <summary>Marks a widget root (diamond, pill, button, rank dot) as one unit for overlap checks; its children are part of it.</summary>
    public sealed class KitLeaf : MonoBehaviour { }

    /// <summary>Drives the 3 parallax layers: UV offsets only (no camera, no blur, no render texture).</summary>
    public sealed class KitBackdrop : MonoBehaviour
    {
        public IShellHost Host;
        public RawImage Scrim, Haze, Dust, Nebula, Stars, Shade;
        public bool Galaxy;
        /// <summary>Multiplies the galaxy drift speeds (1 = token speeds).</summary>
        public float DriftScale = 1f;
        public bool DustEnabled = true;
        public bool MotionEnabled = true;

        Vector2 _eased;
        bool _hasFixedTime;
        float _fixedTime;

        /// <summary>Freeze time (and snap the eased mouse) so renders repeat exactly.</summary>
        public void SetFixedTime(float t)
        {
            _hasFixedTime = true;
            _fixedTime = t;
            _eased = Host != null ? Host.MouseNormalized : Vector2.zero;
            Apply();
        }

        public void ClearFixedTime() { _hasFixedTime = false; }

        void LateUpdate()
        {
            if (!MotionEnabled || _hasFixedTime) return;
            var target = Host != null ? Host.MouseNormalized : Vector2.zero;
            float k = 1f - Mathf.Exp(-UnityEngine.Time.unscaledDeltaTime / Mathf.Max(0.0001f, FiresTokens.ParallaxEase));
            _eased = Vector2.Lerp(_eased, target, k);
            Apply();
        }

        public void Apply()
        {
            float t = _hasFixedTime ? _fixedTime : (Host != null ? Host.Time : 0f);
            float phase = t / FiresTokens.DriftPeriod * Mathf.PI * 2f;
            var drift = new Vector2(Mathf.Cos(phase), Mathf.Sin(phase)) * 0.5f;
            var rect = ((RectTransform)transform).rect;
            if (Galaxy)
            {
                // ReduceMotion = MotionEnabled false: the layers hold still.
                float tm = MotionEnabled ? t * DriftScale : 0f;
                var dir = new Vector2(1f, 0.35f);
                SetUv(Nebula, rect, FiresTokens.NebulaTile, dir * (tm * FiresTokens.NebulaDrift) + _eased * FiresTokens.HazeTravel);
                SetUv(Stars, rect, FiresTokens.StarTile, dir * (tm * FiresTokens.StarDrift) + _eased * FiresTokens.DustTravel * 0.5f);
                SetUv(Dust, rect, FiresTokens.DustTile, dir * (tm * FiresTokens.MoteDrift) + _eased * FiresTokens.DustTravel);
                if (Dust != null) Dust.enabled = DustEnabled;
                return;
            }
            SetUv(Haze, rect, FiresTokens.HazeTile, (_eased + drift) * FiresTokens.HazeTravel);
            SetUv(Dust, rect, FiresTokens.DustTile, (_eased + drift) * FiresTokens.DustTravel);
            if (Dust != null) Dust.enabled = DustEnabled;
        }

        static void SetUv(RawImage img, Rect rect, float tile, Vector2 offsetPx)
        {
            if (img == null) return;
            float w = Mathf.Max(1f, rect.width), h = Mathf.Max(1f, rect.height);
            img.uvRect = new Rect(offsetPx.x / tile, offsetPx.y / tile, w / tile, h / tile);
        }
    }

    /// <summary>
    /// Letter-spacing for legacy UnityEngine.UI.Text (TMP uses characterSpacing): shifts each glyph quad by Px,
    /// keeping the line's alignment. Single-line labels only (titles, section headers, tabs).
    /// </summary>
    public sealed class KitTracking : BaseMeshEffect
    {
        public float Px;

        public static int GlyphCount(string s)
        {
            int n = 0; bool tag = false;
            foreach (char ch in s)
            {
                if (ch == '<') { tag = true; continue; }
                if (tag) { if (ch == '>') tag = false; continue; }
                if (!char.IsWhiteSpace(ch)) n++;
            }
            return n;
        }

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive() || Px == 0f) return;
            int quads = vh.currentVertCount / 4;
            if (quads < 2) return;
            var text = GetComponent<Text>();
            float shift0;
            var a = text != null ? text.alignment : TextAnchor.MiddleLeft;
            if (a == TextAnchor.UpperCenter || a == TextAnchor.MiddleCenter || a == TextAnchor.LowerCenter) shift0 = -(quads - 1) * Px * 0.5f;
            else if (a == TextAnchor.UpperRight || a == TextAnchor.MiddleRight || a == TextAnchor.LowerRight) shift0 = -(quads - 1) * Px;
            else shift0 = 0f;
            var v = new UIVertex();
            for (int i = 0; i < vh.currentVertCount; i++)
            {
                vh.PopulateUIVertex(ref v, i);
                v.position.x += shift0 + (i / 4) * Px;
                vh.SetUIVertex(v, i);
            }
        }
    }
}
