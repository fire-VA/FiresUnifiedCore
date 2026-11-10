// ONE hotbar look for every bar (Tools\golden\ui_overhaul\hotbars\PLAN.md; mockups approved).
// Add-only kit contract: KitHotbarSlot (per-frame Set* handles, no allocations, text rebuilt only when the
// shown value changes), KitHotbarRow (Normal / Behind), Widgets.HotbarSlot / Widgets.HotbarRow. Ports: vanilla 1-8 (Core),
// RPGClasses bars, VAInventory tool/food/ammo, possession chips. Shapes from KitSprites only (no textures).
using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI.Shell.Kit
{
    public enum HotbarStyle { Normal, Behind }
    public enum HotbarSlotState { Normal, Empty, Disabled, Selected }

    public sealed class KitHotbarSlot
    {
        public RectTransform Root;
        public Image Frame, Outline, Glow, Icon, Sweep, Durability, DurabilityTrack, KeyChip, CountStrip, Reason;
        public KitLabel Key, Count, Seconds;
        internal RectTransform PipRoot;
        internal Image[] Pips = new Image[0];
        internal float Size;

        private Sprite _icon; private Color _tint = Color.white;
        private float _cd = -1f; private string _secText = "";
        private int _charges = int.MinValue, _maxCharges = int.MinValue;
        private int _count = int.MinValue; private float _dur = float.NaN;
        private string _key; private HotbarSlotState _state = (HotbarSlotState)(-1);
        private bool _keyHidden;
        internal bool Ultimate;

        public void SetIcon(Sprite icon, Color tint)
        {
            if (icon != _icon) { _icon = icon; Icon.sprite = icon; Icon.enabled = icon != null; }
            if (tint != _tint) { _tint = tint; ApplyIconColor(); }
        }

        /// <summary>cooldown01 &lt;= 0 hides the sweep; the seconds text is only rebuilt when the shown value changes.</summary>
        public void SetCooldown(float cooldown01, float secondsLeft)
        {
            bool on = cooldown01 > 0f;
            if (Mathf.Abs(cooldown01 - _cd) > 0.001f)
            {
                _cd = cooldown01;
                Sweep.enabled = on;
                if (on) Sweep.fillAmount = Mathf.Clamp01(cooldown01);
                ApplyIconColor();
            }
            string s = on ? FormatSeconds(secondsLeft) : "";
            if (s != _secText) { _secText = s; Seconds.Value = s; Seconds.Graphic.enabled = s.Length > 0; }
        }

        private static string FormatSeconds(float t)
        {
            if (t <= 0f) return "";
            if (t < 1f) return t.ToString("0.0", CultureInfo.InvariantCulture);
            int s = Mathf.CeilToInt(t);
            return s >= 60 ? (s / 60) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture) : s.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>charges &lt; 0 or maxCharges &lt;= 1 hides the pips.</summary>
        public void SetCharges(int charges, int maxCharges)
        {
            if (charges == _charges && maxCharges == _maxCharges) return;
            _charges = charges; _maxCharges = maxCharges;
            bool show = charges >= 0 && maxCharges > 1;
            PipRoot.gameObject.SetActive(show);
            if (!show) return;
            if (Pips.Length != maxCharges) BuildPips(maxCharges);
            for (int i = 0; i < Pips.Length; i++)
            {
                bool full = i < charges;
                Pips[i].sprite = full ? KitSprites.Circle() : KitSprites.RoundedOutline(FiresTokens.HotbarPipSize * 0.5f);
                Pips[i].color = full ? FiresTokens.GoldBright : FiresTokens.Hairline;
            }
        }

        private void BuildPips(int n)
        {
            foreach (var p in Pips) if (p != null) Object.Destroy(p.gameObject);
            Pips = new Image[n];
            float d = FiresTokens.HotbarPipSize;
            for (int i = 0; i < n; i++)
            {
                Pips[i] = Widgets.Box(PipRoot, "Pip" + i, KitSprites.Circle(), FiresTokens.GoldBright, false);
                Widgets.Place(Pips[i].rectTransform, i * (d + 3f), 0f, d, d);
                Pips[i].raycastTarget = false;
            }
        }

        /// <summary>count &lt;= 1 hides the strip.</summary>
        public void SetCount(int count)
        {
            if (count == _count) return;
            _count = count;
            bool show = count > 1;
            CountStrip.gameObject.SetActive(show);
            if (show) { Count.Value = count.ToString(CultureInfo.InvariantCulture); LayoutCount(); }
        }

        /// <summary>durability01 &lt; 0 hides the bar.</summary>
        public void SetDurability(float durability01)
        {
            if (Mathf.Abs(durability01 - _dur) < 0.002f) return;
            bool wasShown = !float.IsNaN(_dur) && _dur >= 0f;
            _dur = durability01;
            bool show = durability01 >= 0f;
            DurabilityTrack.gameObject.SetActive(show);
            if (show)
            {
                float d = Mathf.Clamp01(durability01);
                var rt = Durability.rectTransform; rt.anchorMax = new Vector2(d, 1f);
                Durability.color = d > 0.5f ? FiresTokens.Ok : d > 0.25f ? FiresTokens.Warn : FiresTokens.Danger;
            }
            if (show != wasShown) LayoutCount();
        }

        public void SetKey(string keyLabel)
        {
            if (keyLabel == _key) return;
            _key = keyLabel;
            bool show = !string.IsNullOrEmpty(keyLabel) && !_keyHidden;
            KeyChip.gameObject.SetActive(show);
            if (!show) return;
            Key.Value = keyLabel;
            float w = Mathf.Ceil(Key.PreferredWidth) + 6f;
            // (The hint inside the top edge crowds the square): the key chip sits centred just BELOW the slot
            var kr = KeyChip.rectTransform; kr.anchorMin = kr.anchorMax = new Vector2(0.5f, 0f); kr.pivot = new Vector2(0.5f, 1f);
            kr.anchoredPosition = new Vector2(0f, -2f); kr.sizeDelta = new Vector2(w, 15f);
        }

        internal void HideKey(bool hidden) { _keyHidden = hidden; var k = _key; _key = null; SetKey(k); }

        public void SetState(HotbarSlotState s)
        {
            if (s == _state) return;
            _state = s;
            bool empty = s == HotbarSlotState.Empty, sel = s == HotbarSlotState.Selected, dis = s == HotbarSlotState.Disabled;
            Outline.color = sel ? FiresTokens.GoldBright : empty ? FiresTokens.WithAlpha(FiresTokens.Hairline, 0.6f) : dis ? FiresTokens.Hairline : (_icon != null ? FiresTokens.Edge : FiresTokens.Hairline);
            Glow.enabled = sel;
            Key.Color = empty || dis ? FiresTokens.TextFaint : FiresTokens.Text;
            if (empty) { Icon.enabled = false; CountStrip.gameObject.SetActive(false); DurabilityTrack.gameObject.SetActive(false); }
            else Icon.enabled = _icon != null;
            ApplyIconColor();
        }

        /// <summary>Optional reason glyph top-right (Latin-1, e.g. "×" in Danger); null hides it.</summary>
        public void SetReason(string glyph, Color color)
        {
            bool show = !string.IsNullOrEmpty(glyph);
            Reason.gameObject.SetActive(show);
        }

        private void ApplyIconColor()
        {
            Color c = _tint;
            if (_state == HotbarSlotState.Disabled) c = new Color(0.541f, 0.478f, 0.376f, 0.45f);
            else if (_cd > 0f) c = new Color(c.r * 0.70f, c.g * 0.75f, c.b, c.a);
            Icon.color = c;
        }

        private void LayoutCount()
        {
            if (!CountStrip.gameObject.activeSelf) return;
            float w = Mathf.Ceil(Count.PreferredWidth) + 8f, h = 16f;
            bool durShown = DurabilityTrack.gameObject.activeSelf;
            var rt = CountStrip.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 0f);
            rt.anchoredPosition = new Vector2(-3f, 3f + (durShown ? 4f : 0f));
            rt.sizeDelta = new Vector2(w, h);
        }
    }

    public sealed class KitHotbarRow
    {
        public RectTransform Root;
        public KitHotbarSlot[] Slots;
        public float Width;
        public HotbarStyle Style { get; private set; }
        internal CanvasGroup Group;
        internal Vector2 NormalPos;

        /// <summary>Normal or Behind (scaled 0.72 about its bottom-centre, CanvasGroup alpha BehindAlpha, no raycasts, drawn first,
        /// key chips hidden). The swap never moves the row's slots; it only restyles.</summary>
        public void SetStyle(HotbarStyle style)
        {
            if (style == Style && Group != null) return;
            Style = style;
            if (Group == null) Group = Root.gameObject.GetComponent<CanvasGroup>() ?? Root.gameObject.AddComponent<CanvasGroup>();
            bool behind = style == HotbarStyle.Behind;
            Root.pivot = new Vector2(0.5f, 0f);
            Root.localScale = behind ? new Vector3(FiresTokens.BehindScale, FiresTokens.BehindScale, 1f) : Vector3.one;
            Group.alpha = behind ? FiresTokens.BehindAlpha : 1f;
            Group.blocksRaycasts = !behind;   // the caller turns it on during item drags
            if (behind) Root.SetAsFirstSibling();
            foreach (var s in Slots) s.HideKey(behind);
        }
    }

    public static partial class Widgets
    {
        /// <summary>[HotbarSlot/&lt;id&gt;] one hotbar slot (PLAN.md anatomy): rounded Well frame (HotbarWellAlpha) + outline, icon (0.72),
        /// key chip top-left, count strip bottom-right, durability bar, radial cooldown sweep + seconds, charge pips, states.</summary>
        public static KitHotbarSlot HotbarSlot(RectTransform parent, string id, Sprite icon, string keyLabel,
            float cooldown01, int charges, int count, float durability01, float x, float y, float size = FiresTokens.HotbarSlotSize)
            => HotbarSlot(parent, id, icon, keyLabel, cooldown01, charges, -1, count, durability01, x, y, size);

        /// <summary>HotbarSlot with max charges (pips shown when maxCharges &gt; 1).</summary>
        public static KitHotbarSlot HotbarSlot(RectTransform parent, string id, Sprite icon, string keyLabel,
            float cooldown01, int charges, int maxCharges, int count, float durability01, float x, float y, float size = FiresTokens.HotbarSlotSize)
        {
            var root = PathNode(parent, "HotbarSlot", id);
            Place(root, x, y, size, size);
            var s = new KitHotbarSlot { Root = root, Size = size };
            float r = FiresTokens.RadiusSmall;

            // frame + clip (the sweep is masked by the rounded frame)
            s.Frame = Box(root, "Frame", KitSprites.RoundedFill(r), FiresTokens.WithAlpha(FiresTokens.Well, FiresTokens.HotbarWellAlpha));
            Stretch(s.Frame.rectTransform, 0, 0, 0, 0);
            var mask = s.Frame.gameObject.AddComponent<Mask>(); mask.showMaskGraphic = true;
            s.Glow = Box(root, "Glow", KitSprites.RoundedOutline(r), FiresTokens.WithAlpha(FiresTokens.Gold, 0.35f));
            Stretch(s.Glow.rectTransform, 2, 2, 2, 2); s.Glow.enabled = false;
            s.Outline = Box(root, "Outline", KitSprites.RoundedOutline(r), FiresTokens.Hairline);
            Stretch(s.Outline.rectTransform, 0, 0, 0, 0);

            float isz = Mathf.Round(size * FiresTokens.HotbarIconFraction);
            s.Icon = Box(s.Frame.rectTransform, "Icon", icon, Color.white, false);
            s.Icon.preserveAspect = true;
            Place(s.Icon.rectTransform, (size - isz) * 0.5f, Mathf.Round(size * 0.10f), isz, isz);

            s.Sweep = Box(s.Frame.rectTransform, "Sweep", KitSprites.White(), FiresTokens.HotbarCooldownShade, false);
            Stretch(s.Sweep.rectTransform, 0, 0, 0, 0);
            s.Sweep.type = Image.Type.Filled; s.Sweep.fillMethod = Image.FillMethod.Radial360; s.Sweep.fillOrigin = (int)Image.Origin360.Top; s.Sweep.fillClockwise = true;
            s.Sweep.enabled = false;
            s.Seconds = KitText.Create(root, "Seconds", "", FontRole.SerifBold, FiresTokens.HotbarCooldownSize, FiresTokens.Text, KitAlign.Center, 0f, false, false);
            Stretch(s.Seconds.Rect, 0, 0, 0, 0);
            var shadow = s.Seconds.Graphic.gameObject.AddComponent<Shadow>(); shadow.effectColor = FiresTokens.Backdrop; shadow.effectDistance = new Vector2(1f, -1f);
            s.Seconds.Graphic.enabled = false;

            // durability bar
            s.DurabilityTrack = Box(root, "DurabilityTrack", KitSprites.RoundedFill(1.5f), FiresTokens.WithAlpha(FiresTokens.Backdrop, 0.7f));
            var dt = s.DurabilityTrack.rectTransform; dt.anchorMin = new Vector2(0f, 0f); dt.anchorMax = new Vector2(1f, 0f); dt.pivot = new Vector2(0.5f, 0f);
            dt.offsetMin = new Vector2(6f, 3f); dt.offsetMax = new Vector2(-6f, 6f);
            s.Durability = Box(dt, "Fill", KitSprites.RoundedFill(1.5f), FiresTokens.Ok);
            var df = s.Durability.rectTransform; df.anchorMin = Vector2.zero; df.anchorMax = Vector2.one; df.offsetMin = df.offsetMax = Vector2.zero;
            s.DurabilityTrack.gameObject.SetActive(false);

            // count strip
            s.CountStrip = Box(root, "CountStrip", KitSprites.RoundedFill(Mathf.Min(r, 8f)), FiresTokens.WithAlpha(FiresTokens.Backdrop, 0.9f));
            s.Count = KitText.Create(s.CountStrip.rectTransform, "Count", "", FontRole.SansBold, FiresTokens.HotbarCountSize, FiresTokens.Text, KitAlign.Center, 0f, false, false);
            Stretch(s.Count.Rect, 0, 0, 0, 0);
            s.CountStrip.gameObject.SetActive(false);

            // charge pips
            s.PipRoot = Node(root, "Pips");
            s.PipRoot.anchorMin = s.PipRoot.anchorMax = s.PipRoot.pivot = new Vector2(0f, 0f);
            s.PipRoot.anchoredPosition = new Vector2(5f, 5f); s.PipRoot.sizeDelta = new Vector2(size - 10f, FiresTokens.HotbarPipSize);
            s.PipRoot.gameObject.SetActive(false);

            // key chip
            s.KeyChip = Box(root, "KeyChip", KitSprites.RoundedFill(6f), FiresTokens.WithAlpha(FiresTokens.Backdrop, 0.85f));
            s.KeyChip.rectTransform.anchorMin = s.KeyChip.rectTransform.anchorMax = new Vector2(0.5f, 0f); s.KeyChip.rectTransform.pivot = new Vector2(0.5f, 1f);   // below the slot
            s.Key = KitText.Create(s.KeyChip.rectTransform, "Key", "", FontRole.SansBold, FiresTokens.HotbarKeySize, FiresTokens.Text, KitAlign.Center, 0f, false, false);
            Stretch(s.Key.Rect, 0, 0, 0, 0);
            s.KeyChip.gameObject.SetActive(false);

            // reason glyph
            s.Reason = Box(root, "Reason", KitSprites.Circle(), FiresTokens.WithAlpha(FiresTokens.Backdrop, 0.85f), false);
            s.Reason.rectTransform.anchorMin = s.Reason.rectTransform.anchorMax = s.Reason.rectTransform.pivot = new Vector2(1f, 1f);
            s.Reason.rectTransform.anchoredPosition = new Vector2(-3f, -3f); s.Reason.rectTransform.sizeDelta = new Vector2(14f, 14f);
            s.Reason.gameObject.SetActive(false);

            foreach (var g in root.GetComponentsInChildren<Graphic>(true)) g.raycastTarget = false;
            root.gameObject.AddComponent<KitLeaf>();

            s.SetIcon(icon, Color.white);
            s.SetKey(keyLabel);
            s.SetCount(count);
            s.SetDurability(durability01);
            s.SetCharges(charges, maxCharges);
            s.SetCooldown(cooldown01, 0f);
            s.SetState(icon == null ? HotbarSlotState.Empty : HotbarSlotState.Normal);
            return s;
        }

        /// <summary>[HotbarRow/&lt;id&gt;] a row of <paramref name="slots"/> HotbarSlots (ids "0".."n-1"), pitch size + gap, in the given style.</summary>
        public static KitHotbarRow HotbarRow(RectTransform parent, string id, int slots, float x, float y,
            HotbarStyle style = HotbarStyle.Normal, float size = FiresTokens.HotbarSlotSize, float gap = FiresTokens.HotbarSlotGap)
        {
            var root = PathNode(parent, "HotbarRow", id);
            float w = slots * size + Mathf.Max(0, slots - 1) * gap;
            Place(root, x, y, w, size);
            if (style == HotbarStyle.Behind) root.name = "DimmedBehind";
            var row = new KitHotbarRow { Root = root, Slots = new KitHotbarSlot[slots], Width = w };
            for (int i = 0; i < slots; i++)
                row.Slots[i] = HotbarSlot(root, i.ToString(CultureInfo.InvariantCulture), null, null, 0f, -1, 0, -1f, i * (size + gap), 0f, size);
            row.SetStyle(style);
            return row;
        }
    }
}
