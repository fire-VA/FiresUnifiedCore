using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FiresCore.Input;
using FiresCore.UI.Shell.Kit;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FiresCore.UI.Kit
{
    /// <summary>
    /// A uGUI window in the rounded kit look, replacing IMGUI windows (IMGUI always draws over the cursor item and tooltips).
    /// Every KitWindow lives on one shared screen-space canvas (<see cref="SortOrder"/>), so windows order among themselves by
    /// sibling index; a click brings one to the front. The window has a title bar (drag), a close button, a resize grip and a
    /// <see cref="Body"/> column (VerticalLayoutGroup, 18 px sides) the caller fills with the <see cref="KitUI"/> builders.
    /// Esc closes the top window (the most recently shown or focused) without opening the vanilla menu.
    /// Sizes and positions are canvas units (reference 1920x1080, matched on height, like the docked panels).
    /// </summary>
    public sealed class KitWindow
    {
        /// <summary>The shared canvas' sorting order: above the inventory panels, the docked panels and Core's dock grips
        /// (30900-31002) and the shell devbar (32000-32010); below VAInventory's tooltip layer (32760) and drag layer (32767),
        /// so the vanilla tooltip and the cursor item always draw over a window.</summary>
        public const int SortOrder = 32600;

        /// <summary>The canvas scaler's reference resolution (as Core's docked panels).</summary>
        public static readonly Vector2 ReferenceResolution = new Vector2(1920f, 1080f);

        /// <summary>The canvas scaler's width/height match (1 = height, as Core's docked panels).</summary>
        public const float MatchWidthOrHeight = 1f;

        // Window geometry (the mockup's panel: 14 top, 18 sides, 12 bottom; title 20, subtitle 12)
        internal const float PadTop = 14f, PadSide = 18f, PadBottom = 12f;
        internal const float TitleHeight = 26f, TitleGap = 2f, SubtitleHeight = 16f, TitleBottomGap = 10f;
        internal const float CloseSize = 22f, CloseInset = 12f, GripSize = 16f;
        internal const float BodySpacing = 10f;
        private const float ClampMargin = 0f;

        private static readonly List<KitWindow> s_all = new List<KitWindow>();
        private static readonly List<KitWindow> s_stack = new List<KitWindow>();   // showing windows, top last
        private static readonly Dictionary<string, Rect> s_session = new Dictionary<string, Rect>();   // id -> (x, top, w, h) canvas units

        private readonly RectTransform m_root;
        private readonly RectTransform m_titleBar;
        private readonly TextMeshProUGUI m_title, m_subtitle;
        private readonly RectTransform m_body;
        private readonly Vector2 m_minSize;
        private Vector2 m_defaultSize;
        private bool m_placed;

        /// <summary>The window's id (the session-rect key).</summary>
        public string Id { get; }

        /// <summary>The content root under the title: a VerticalLayoutGroup column with the kit's padding (18 px sides).
        /// Add sections, rows, notes and buttons with <see cref="KitUI"/>.</summary>
        public RectTransform Body => m_body;

        /// <summary>The window's root (the plate). Callers may parent extra overlays here; never reparent it.</summary>
        public RectTransform Root => m_root;

        /// <summary>Raised after the window hides, for any reason (close button, Esc, <see cref="Hide"/>).</summary>
        public Action OnClosed;

        /// <summary>Where the window opens when it has no remembered rect: given the window size in screen pixels, return
        /// its rect in screen pixels (bottom-left origin, as Unity screen coordinates). Null = screen centre.</summary>
        public Func<Vector2, Rect> DefaultPlacement;

        /// <summary>Keep the window's rect per id, so it reopens where it was left: for the session, and across restarts in
        /// BepInEx\cache\FiresUnifiedCore\kitwindows.txt (written when a drag or resize ends). Default true.</summary>
        public bool RememberRect = true;

        /// <summary>True while the window is shown.</summary>
        public bool IsShowing => m_root != null && m_root.gameObject.activeSelf;

        /// <summary>The top showing window (the most recently shown or focused), or null. Esc closes this one.</summary>
        public static KitWindow Top
        {
            get
            {
                for (int i = s_stack.Count - 1; i >= 0; i--)
                    if (s_stack[i] != null && s_stack[i].IsShowing) return s_stack[i];
                return null;
            }
        }

        /// <summary>True while any KitWindow is shown.</summary>
        public static bool AnyShowing => Top != null;

        /// <summary>The window with this id, or null.</summary>
        public static KitWindow Find(string id)
        {
            for (int i = 0; i < s_all.Count; i++) if (s_all[i] != null && s_all[i].Id == id && s_all[i].m_root != null) return s_all[i];
            return null;
        }

        /// <summary>Creates a hidden window on the shared canvas. A second Create with a live id returns that window
        /// (with the new title and subtitle). Sizes are canvas units; call <see cref="Show"/> to open it.</summary>
        public static KitWindow Create(string id, string title, string subtitle, Vector2 defaultSize, Vector2 minSize)
            => Create(id, title, subtitle, defaultSize, minSize, KitWindowStyle.Normal);

        /// <summary>As <see cref="Create(string, string, string, Vector2, Vector2)"/>, with a style. A
        /// <see cref="KitWindowStyle.Popover"/> has no title bar, close button, resize grip or drag, uses tighter padding,
        /// keeps the Esc stack, and closes on a click outside it while <see cref="CloseOnOutsideClick"/> is true (its
        /// default). Title and subtitle are kept but not drawn. A second Create with a live id returns that window as it was built.</summary>
        public static KitWindow Create(string id, string title, string subtitle, Vector2 defaultSize, Vector2 minSize, KitWindowStyle style)
        {
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("KitWindow needs an id", nameof(id));
            LoadSavedOnce();
            var existing = Find(id);
            if (existing != null) { existing.SetTitle(title); existing.SetSubtitle(subtitle); return existing; }
            var w = new KitWindow(id, title, subtitle, defaultSize, minSize, style);
            s_all.Add(w);
            return w;
        }

        /// <summary>Popover padding (all sides), canvas units.</summary>
        internal const float PopoverPad = 8f;

        /// <summary>The style the window was built with.</summary>
        public KitWindowStyle Style { get; }

        /// <summary>Popover only: a left or right click outside the window hides it. Default true for a popover, false otherwise.</summary>
        public bool CloseOnOutsideClick;

        private KitWindow(string id, string title, string subtitle, Vector2 defaultSize, Vector2 minSize, KitWindowStyle style)
        {
            Id = id;
            Style = style;
            CloseOnOutsideClick = style == KitWindowStyle.Popover;
            m_minSize = new Vector2(Mathf.Max(120f, minSize.x), Mathf.Max(80f, minSize.y));
            m_defaultSize = new Vector2(Mathf.Max(m_minSize.x, defaultSize.x), Mathf.Max(m_minSize.y, defaultSize.y));
            var host = KitWindowHost.Ensure();

            m_root = KitUI.NewRect(host.transform, "KitWindow_" + id);
            m_root.anchorMin = m_root.anchorMax = Vector2.zero;
            m_root.pivot = new Vector2(0f, 1f);
            m_root.sizeDelta = m_defaultSize;
            var plate = m_root.gameObject.AddComponent<Image>();
            KitUI.SetPlate(plate, KitUI.Plate(FiresTokens.Radius, FiresTokens.Panel, FiresTokens.Edge));
            plate.raycastTarget = true;   // clicks on the plate never fall through to the world
            m_root.gameObject.AddComponent<KitWindowTag>().Window = this;

            // title bar: drag handle, title, subtitle, close
            m_titleBar = KitUI.NewRect(m_root, "TitleBar");
            m_titleBar.anchorMin = new Vector2(0f, 1f); m_titleBar.anchorMax = new Vector2(1f, 1f); m_titleBar.pivot = new Vector2(0.5f, 1f);
            var tbImg = m_titleBar.gameObject.AddComponent<Image>();
            tbImg.color = Color.clear; tbImg.raycastTarget = true;
            m_titleBar.gameObject.AddComponent<KitWindowDrag>().Window = this;

            m_title = KitUI.Label(m_titleBar, "Title", title ?? "", KitUI.TitleSize, FiresTokens.Gold, TextAlignmentOptions.Center);
            m_title.fontStyle = FontStyles.Bold;
            KitUI.MarkLeaf(m_title.rectTransform);
            m_subtitle = KitUI.Label(m_titleBar, "Subtitle", subtitle ?? "", KitUI.SmallSize, FiresTokens.TextMuted, TextAlignmentOptions.Center);
            KitUI.MarkLeaf(m_subtitle.rectTransform);

            var close = UIBuilderHelper.CreateButton(m_titleBar, FiresTokens.GlyphClose, Vector2.zero, Vector2.zero, Hide);
            close.name = "Close";
            KitUI.StyleButton(close, false);
            KitUI.StyleText(close.GetComponentInChildren<TMP_Text>(), KitUI.BodySize, FiresTokens.TextMuted, TextAlignmentOptions.Center);
            var cr = (RectTransform)close.transform;
            cr.anchorMin = cr.anchorMax = new Vector2(1f, 1f); cr.pivot = new Vector2(1f, 1f);
            cr.sizeDelta = new Vector2(CloseSize, CloseSize); cr.anchoredPosition = new Vector2(-CloseInset, -CloseInset);
            KitUI.MarkLeaf(cr);

            // body: the caller's column, clipped to the window
            m_body = KitUI.NewRect(m_root, "Body");
            m_body.anchorMin = Vector2.zero; m_body.anchorMax = Vector2.one; m_body.pivot = new Vector2(0.5f, 1f);
            m_body.gameObject.AddComponent<RectMask2D>();
            var vlg = m_body.gameObject.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset((int)PadSide, (int)PadSide, 0, (int)PadBottom);
            vlg.spacing = BodySpacing;
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlWidth = true; vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;

            if (style == KitWindowStyle.Popover)
            {
                // chromeless: no title bar (so no drag or close), no grip; the body fills the plate
                m_titleBar.gameObject.SetActive(false);
                int p = (int)PopoverPad;
                vlg.padding = new RectOffset(p, p, p, p);
                vlg.spacing = PopoverPad;
                LayoutTitle();
                m_root.gameObject.SetActive(false);
                return;
            }

            // resize grip (Core's grip art), bottom-right, always last
            var grip = KitUI.NewRect(m_root, "ResizeGrip");
            grip.anchorMin = grip.anchorMax = new Vector2(1f, 0f); grip.pivot = new Vector2(1f, 0f);
            grip.sizeDelta = new Vector2(GripSize, GripSize); grip.anchoredPosition = new Vector2(-3f, 3f);
            var gi = FiresResizeGrip.AddGrip(grip, FiresTokens.WithAlpha(FiresTokens.Edge, 0.9f));
            if (gi != null) gi.raycastTarget = true;
            grip.gameObject.AddComponent<KitWindowResize>().Window = this;

            LayoutTitle();
            m_root.gameObject.SetActive(false);
        }

        /// <summary>Sets the title text.</summary>
        public void SetTitle(string title) { if (m_title != null) m_title.text = title ?? ""; }

        /// <summary>Sets the subtitle text; empty hides the subtitle line and the title bar shrinks.</summary>
        public void SetSubtitle(string subtitle)
        {
            if (m_subtitle == null) return;
            m_subtitle.text = subtitle ?? "";
            LayoutTitle();
        }

        /// <summary>Changes the size used when the window opens without a remembered rect (canvas units).</summary>
        public void SetDefaultSize(Vector2 size) => m_defaultSize = new Vector2(Mathf.Max(m_minSize.x, size.x), Mathf.Max(m_minSize.y, size.y));

        private void LayoutTitle()
        {
            if (Style == KitWindowStyle.Popover) { m_body.offsetMin = Vector2.zero; m_body.offsetMax = Vector2.zero; return; }
            bool sub = m_subtitle != null && !string.IsNullOrEmpty(m_subtitle.text);
            m_subtitle.gameObject.SetActive(sub);
            float side = PadSide + CloseSize + 4f;   // the centred title keeps clear of the close button
            Place(m_title.rectTransform, side, PadTop, TitleHeight);
            float h = PadTop + TitleHeight;
            if (sub) { Place(m_subtitle.rectTransform, side, h + TitleGap, SubtitleHeight); h += TitleGap + SubtitleHeight; }
            h += TitleBottomGap;
            m_titleBar.sizeDelta = new Vector2(0f, h);
            m_titleBar.anchoredPosition = Vector2.zero;
            m_body.offsetMin = Vector2.zero; m_body.offsetMax = new Vector2(0f, -h);
        }

        private static void Place(RectTransform rt, float side, float top, float height)
        {
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(side, -top - height); rt.offsetMax = new Vector2(-side, -top);
        }

        /// <summary>Shows the window on top (placed by the remembered rect, else <see cref="DefaultPlacement"/>, else centred).</summary>
        public void Show()
        {
            if (m_root == null) return;
            if (!m_placed || !RememberRect || !s_session.ContainsKey(Id)) PlaceInitial();
            m_placed = true;
            m_root.gameObject.SetActive(true);
            BringToFront();
            Clamp();
        }

        /// <summary>Hides the window (keeps it for the next <see cref="Show"/>) and raises <see cref="OnClosed"/>.</summary>
        public void Hide()
        {
            if (m_root == null || !m_root.gameObject.activeSelf) return;
            SaveRect();
            m_root.gameObject.SetActive(false);
            s_stack.Remove(this);
            KitUI.HideOwnTip();
            try { OnClosed?.Invoke(); } catch (Exception e) { FiresCore.Logging.FiresLogger.LogWarning($"[KitWindow] '{Id}' OnClosed threw: {e.Message}"); }
        }

        /// <summary>Shows the window if hidden, hides it if shown.</summary>
        public void Toggle() { if (IsShowing) Hide(); else Show(); }

        /// <summary>Destroys the window. Its remembered rect stays for the session.</summary>
        public void Destroy()
        {
            if (m_root == null) return;
            Hide();
            UnityEngine.Object.Destroy(m_root.gameObject);
            s_all.Remove(this);
        }

        /// <summary>Moves the window to the front of the shared canvas and the top of the Esc stack.</summary>
        public void BringToFront()
        {
            if (m_root == null) return;
            m_root.SetAsLastSibling();
            KitWindowHost.KeepTipOnTop();
            s_stack.Remove(this);
            if (IsShowing) s_stack.Add(this);
        }

        /// <summary>The window's rect in canvas units (x, y = bottom-left).</summary>
        public Rect CanvasRect
        {
            get
            {
                if (m_root == null) return default;
                Vector2 p = m_root.anchoredPosition, s = m_root.sizeDelta;
                return new Rect(p.x, p.y - s.y, s.x, s.y);
            }
        }

        private void PlaceInitial()
        {
            if (RememberRect && (s_session.TryGetValue(Id, out Rect r) || s_session.TryGetValue(FileId(Id), out r)) && Good(r))
            {
                m_root.sizeDelta = new Vector2(Mathf.Max(m_minSize.x, r.width), Mathf.Max(m_minSize.y, r.height));
                m_root.anchoredPosition = new Vector2(r.x, r.y);
                if (!m_placed)
                    FiresCore.Logging.FiresLogger.LogDebug(string.Format(CultureInfo.InvariantCulture,
                        "[Kit] window '{0}' placed at its saved spot ({1:0}, {2:0}, {3:0} x {4:0})", Id, r.x, r.y, m_root.sizeDelta.x, m_root.sizeDelta.y));
                return;
            }
            if (!m_placed) FiresCore.Logging.FiresLogger.LogDebug($"[Kit] window '{Id}' placed at its default spot");
            Vector2 canvas = KitWindowHost.CanvasSize;
            float scale = KitWindowHost.ScaleFactor;
            Vector2 size = new Vector2(Mathf.Min(m_defaultSize.x, canvas.x), Mathf.Min(m_defaultSize.y, canvas.y));
            Rect screen;
            Vector2 sizePx = size * scale;
            try
            {
                screen = DefaultPlacement != null ? DefaultPlacement(sizePx)
                    : new Rect((Screen.width - sizePx.x) * 0.5f, (Screen.height - sizePx.y) * 0.5f, sizePx.x, sizePx.y);
            }
            catch (Exception e)
            {
                FiresCore.Logging.FiresLogger.LogWarning($"[KitWindow] '{Id}' DefaultPlacement threw ({e.Message}); centring");
                screen = new Rect((Screen.width - sizePx.x) * 0.5f, (Screen.height - sizePx.y) * 0.5f, sizePx.x, sizePx.y);
            }
            Vector2 sz = new Vector2(Mathf.Max(m_minSize.x, screen.width / scale), Mathf.Max(m_minSize.y, screen.height / scale));
            m_root.sizeDelta = sz;
            m_root.anchoredPosition = new Vector2(screen.x / scale, screen.y / scale + sz.y);
        }

        internal void SaveRect()
        {
            if (m_root == null || !RememberRect || !m_placed) return;
            s_session[Id] = new Rect(m_root.anchoredPosition.x, m_root.anchoredPosition.y, m_root.sizeDelta.x, m_root.sizeDelta.y);
        }

        // a drag or resize ended: keep the rect in memory and, when it changed, write the cache file once
        internal void EndMove()
        {
            SaveRect();
            if (!m_moved) return;
            m_moved = false;
            if (m_root == null || !RememberRect || !m_placed) return;
            if (WriteSaved()) FiresCore.Logging.FiresLogger.LogDebug($"[Kit] window '{Id}' spot saved");
        }

        // ---- rects kept across restarts: BepInEx\cache\FiresUnifiedCore\kitwindows.txt ----
        // one line per id: "id x y w h screenW screenH" (invariant culture; spaces in ids become '_'); x, y = top-left, canvas units

        private struct SavedRect { public Rect R; public int ScreenW, ScreenH; }
        private static readonly Dictionary<string, SavedRect> s_saved = new Dictionary<string, SavedRect>();
        private static bool s_savedLoaded;
        private bool m_moved;

        private static string SavedPath => Path.Combine(Path.Combine(BepInEx.Paths.CachePath, "FiresUnifiedCore"), "kitwindows.txt");

        private static string FileId(string id) => id.Replace(' ', '_').Replace('\t', '_');

        private static bool Good(Rect r) => !(float.IsNaN(r.x) || float.IsNaN(r.y) || float.IsNaN(r.width) || float.IsNaN(r.height)
            || float.IsInfinity(r.x) || float.IsInfinity(r.y) || float.IsInfinity(r.width) || float.IsInfinity(r.height)
            || r.width <= 0f || r.height <= 0f);

        // the canvas size a screen of this size has (the scaler's formula)
        private static Vector2 CanvasFor(int w, int h)
        {
            float lw = Mathf.Log(w / ReferenceResolution.x, 2f), lh = Mathf.Log(h / ReferenceResolution.y, 2f);
            return new Vector2(w, h) / Mathf.Pow(2f, Mathf.Lerp(lw, lh, MatchWidthOrHeight));
        }

        // once, lazily: reads the file into s_saved and seeds the in-memory rects (scaled to the current screen)
        private static void LoadSavedOnce()
        {
            if (s_savedLoaded) return;
            s_savedLoaded = true;
            string path;
            try { path = SavedPath; if (!File.Exists(path)) return; }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug($"[Kit] window spots not read: {e.Message}"); return; }
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug($"[Kit] window spots not read: {e.Message}"); return; }
            var ci = CultureInfo.InvariantCulture;
            foreach (var raw in lines)
            {
                var p = (raw ?? "").Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length != 7) continue;
                if (!float.TryParse(p[1], NumberStyles.Float, ci, out float x) || !float.TryParse(p[2], NumberStyles.Float, ci, out float y)
                    || !float.TryParse(p[3], NumberStyles.Float, ci, out float w) || !float.TryParse(p[4], NumberStyles.Float, ci, out float h)
                    || !int.TryParse(p[5], NumberStyles.Integer, ci, out int sw) || !int.TryParse(p[6], NumberStyles.Integer, ci, out int sh)) continue;
                var r = new Rect(x, y, w, h);
                if (!Good(r) || sw <= 0 || sh <= 0) continue;
                s_saved[p[0]] = new SavedRect { R = r, ScreenW = sw, ScreenH = sh };
                if (s_session.ContainsKey(p[0])) continue;   // this session already placed it
                if (Screen.width > 0 && Screen.height > 0 && (sw != Screen.width || sh != Screen.height))
                {
                    Vector2 from = CanvasFor(sw, sh), to = KitWindowHost.CanvasSize;
                    float kx = to.x / from.x, ky = to.y / from.y;
                    r = new Rect(r.x * kx, r.y * ky, r.width * kx, r.height * ky);
                    if (!Good(r)) continue;
                }
                s_session[p[0]] = r;
            }
        }

        // the whole file, written to a temp file and swapped in; false (one Debug line) on an IO error
        private bool WriteSaved()
        {
            LoadSavedOnce();
            if (s_session.TryGetValue(Id, out Rect r) && Good(r))
                s_saved[FileId(Id)] = new SavedRect { R = r, ScreenW = Screen.width, ScreenH = Screen.height };
            return WriteAll();
        }

        private static bool WriteAll()
        {
            try
            {
                string path = SavedPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var sb = new StringBuilder();
                var ci = CultureInfo.InvariantCulture;
                foreach (var kv in s_saved)
                {
                    Rect r = kv.Value.R;
                    sb.Append(kv.Key).Append(' ')
                      .Append(r.x.ToString("R", ci)).Append(' ').Append(r.y.ToString("R", ci)).Append(' ')
                      .Append(r.width.ToString("R", ci)).Append(' ').Append(r.height.ToString("R", ci)).Append(' ')
                      .Append(kv.Value.ScreenW.ToString(ci)).Append(' ').Append(kv.Value.ScreenH.ToString(ci)).Append('\n');
                }
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch (Exception e)
            {
                FiresCore.Logging.FiresLogger.LogDebug($"[Kit] window spots not saved: {e.Message}");
                return false;
            }
        }

        /// <summary>Forgets the window's remembered rect (this session and the cache file), so its next <see cref="Show"/>
        /// uses <see cref="DefaultPlacement"/>. A window showing now stays where it is until then.</summary>
        public static void ForgetSaved(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            LoadSavedOnce();
            s_session.Remove(id);
            if (s_saved.Remove(FileId(id))) WriteAll();
        }

        /// <summary>Forgets every window's remembered rect (this session and the cache file), as a layout reset.</summary>
        public static void ForgetAllSaved()
        {
            LoadSavedOnce();
            s_session.Clear();
            bool had = s_saved.Count > 0;
            s_saved.Clear();
            if (had) WriteAll();
        }

        /// <summary>Keeps the window on screen (whole window when it fits, else its top-left), shrinking it to the screen.</summary>
        public void Clamp()
        {
            if (m_root == null) return;
            Vector2 canvas = KitWindowHost.CanvasSize;
            if (canvas.x < 1f || canvas.y < 1f) return;
            Vector2 s = m_root.sizeDelta;
            s.x = Mathf.Clamp(s.x, Mathf.Min(m_minSize.x, canvas.x), canvas.x);
            s.y = Mathf.Clamp(s.y, Mathf.Min(m_minSize.y, canvas.y), canvas.y);
            Vector2 p = m_root.anchoredPosition;   // top-left
            p.x = Mathf.Clamp(p.x, ClampMargin, Mathf.Max(ClampMargin, canvas.x - s.x - ClampMargin));
            p.y = Mathf.Clamp(p.y, Mathf.Min(canvas.y, s.y + ClampMargin), canvas.y - ClampMargin);
            if (m_root.sizeDelta != s) m_root.sizeDelta = s;
            if (m_root.anchoredPosition != p) m_root.anchoredPosition = p;
        }

        internal void DragBy(Vector2 screenDelta)
        {
            m_root.anchoredPosition += screenDelta / KitWindowHost.ScaleFactor;
            Clamp();
        }

        internal void ResizeBy(Vector2 screenDelta)
        {
            Vector2 d = screenDelta / KitWindowHost.ScaleFactor;
            Vector2 canvas = KitWindowHost.CanvasSize;
            Vector2 p = m_root.anchoredPosition;
            Vector2 s = m_root.sizeDelta + new Vector2(d.x, -d.y);
            s.x = Mathf.Clamp(s.x, m_minSize.x, Mathf.Max(m_minSize.x, canvas.x - p.x));
            s.y = Mathf.Clamp(s.y, m_minSize.y, Mathf.Max(m_minSize.y, p.y));
            if (m_root.sizeDelta != s) m_moved = true;
            m_root.sizeDelta = s;
        }

        // a click landed on 'hit' (null = outside every window): popovers it missed close
        internal static void CloseOutsidePopovers(KitWindow hit)
        {
            for (int i = s_stack.Count - 1; i >= 0; i--)
            {
                if (i >= s_stack.Count) continue;
                var w = s_stack[i];
                if (w != null && w != hit && w.IsShowing && w.Style == KitWindowStyle.Popover && w.CloseOnOutsideClick) w.Hide();
            }
        }

        internal static void ClampAll()
        {
            for (int i = 0; i < s_all.Count; i++) if (s_all[i] != null && s_all[i].IsShowing) s_all[i].Clamp();
        }

        // the showing window under the screen point, front-most first
        internal static KitWindow At(Vector2 screen)
        {
            var host = KitWindowHost.Instance;
            if (host == null) return null;
            for (int i = host.transform.childCount - 1; i >= 0; i--)
            {
                var c = host.transform.GetChild(i);
                if (!c.gameObject.activeSelf) continue;
                var tag = c.GetComponent<KitWindowTag>();
                if (tag == null) continue;
                if (RectTransformUtility.RectangleContainsScreenPoint((RectTransform)c, screen, null)) return tag.Window;
            }
            return null;
        }

        /// <summary>A layout check over the window's visible elements, in screen pixels: elements that overlap another,
        /// lie outside the window, or hold single-line text wider than their box (TMP preferred width). Elements scrolled
        /// out of a <see cref="KitUI.ScrollView"/> are skipped. One line; the caller logs it.</summary>
        public string LayoutReport()
        {
            if (m_root == null) return $"[KitWindow] '{Id}': destroyed";
            if (!m_root.gameObject.activeInHierarchy) return $"[KitWindow] '{Id}': hidden";
            Canvas.ForceUpdateCanvases();
            Rect bg = KitUI.DrawnRect(m_root);
            var leaves = new List<(RectTransform t, Rect r)>();
            foreach (var mark in m_root.GetComponentsInChildren<KitLeafMark>(false))
            {
                var rt = (RectTransform)mark.transform;
                Rect r = KitUI.DrawnRect(rt);
                var vp = rt.GetComponentInParent<KitScrollViewport>();
                if (vp != null && vp.transform.IsChildOf(m_root))
                {
                    Rect v = KitUI.DrawnRect(vp.transform);
                    if (!r.Overlaps(v)) continue;   // scrolled out of view
                }
                leaves.Add((rt, r));
            }
            int overlaps = 0, outside = 0, clipped = 0;
            var bad = new List<string>();
            for (int i = 0; i < leaves.Count; i++)
            {
                Rect a = leaves[i].r;
                var vp = leaves[i].t.GetComponentInParent<KitScrollViewport>();
                Rect bound = vp != null ? KitUI.DrawnRect(vp.transform) : bg;
                bool partlyScrolled = vp != null && !(a.yMin >= bound.yMin - 1f && a.yMax <= bound.yMax + 1f);
                if (!partlyScrolled && !(a.xMin >= bg.xMin - 1f && a.xMax <= bg.xMax + 1f && a.yMin >= bg.yMin - 1f && a.yMax <= bg.yMax + 1f))
                { outside++; bad.Add(leaves[i].t.name + " outside"); }
                for (int j = i + 1; j < leaves.Count; j++)
                {
                    if (leaves[j].t.IsChildOf(leaves[i].t) || leaves[i].t.IsChildOf(leaves[j].t)) continue;
                    Rect b = leaves[j].r;
                    if (a.xMin < b.xMax - 1f && b.xMin < a.xMax - 1f && a.yMin < b.yMax - 1f && b.yMin < a.yMax - 1f)
                    { overlaps++; bad.Add(leaves[i].t.name + " x " + leaves[j].t.name); }
                }
                foreach (TMP_Text tx in leaves[i].t.GetComponentsInChildren<TMP_Text>(false))
                {
                    if (tx.textWrappingMode != TextWrappingModes.NoWrap || tx.overflowMode == TextOverflowModes.Ellipsis
                        || tx.GetComponentInParent<TMP_InputField>() != null) continue;
                    if (tx.preferredWidth > tx.rectTransform.rect.width + 0.5f) { clipped++; bad.Add(leaves[i].t.name + "/" + tx.name + " clipped"); }
                }
            }
            return $"[KitWindow] '{Id}' layout: window {bg.width:0}x{bg.height:0} at ({bg.xMin:0}, {bg.yMin:0}); {leaves.Count} element(s), "
                + $"{overlaps} overlap(s), {outside} outside, {clipped} clipped{(bad.Count > 0 ? ": " + string.Join("; ", bad.ToArray()) : "")}";
        }

        /// <summary>The frame (Time.frameCount) on which a KitWindow last consumed Esc; -1 = never.</summary>
        internal static int EscConsumedFrame = -1;
        internal static string EscConsumedWindowId;

        /// <summary>True when a KitWindow consumed Esc on this frame (closed a window or unfocused a kit field), so callers
        /// that read Esc themselves can ignore it.</summary>
        public static bool ConsumedEscThisFrame => EscConsumedFrame == Time.frameCount;

        // Esc: the top window closes; a focused kit field only loses focus
        internal static bool HandleEscape()
        {
            var top = Top;
            if (top == null) return false;
            bool kitField = KitFieldGuard.FocusedOrJustLeft;
            if (!kitField && FiresInputBlock.IsTyping()) return false;   // typing somewhere else: that field owns Esc
            FiresInputBlock.SwallowVanillaKey(KeyCode.Escape, 2);
            FiresInputBlock.ConsumeThisFrame("Menu");
            EscConsumedFrame = Time.frameCount;   // InventoryGui.Update reads Esc directly; KitInventoryHideGuard skips its Hide this frame
            EscConsumedWindowId = top.Id;
            if (kitField) { KitFieldGuard.Unfocus(); return true; }
            top.Hide();
            return true;
        }
    }

    /// <summary>The look of a <see cref="KitWindow"/>.</summary>
    public enum KitWindowStyle
    {
        /// <summary>Title bar (drag), close button, resize grip.</summary>
        Normal,
        /// <summary>Chromeless: no title bar, grip or drag; tighter padding; closes on an outside click (<see cref="KitWindow.CloseOnOutsideClick"/>).</summary>
        Popover,
    }

    /// <summary>Vanilla InventoryGui.Update reads Esc straight from ZInput and calls Hide on the frame a KitWindow
    /// consumed it, so closing a window over the inventory also closed the inventory. Skips Hide on that frame only.</summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
    internal static class KitInventoryHideGuard
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            if (KitWindow.EscConsumedFrame != Time.frameCount) return true;
            FiresCore.Logging.FiresLogger.LogDebug($"[Kit] inventory close skipped: Esc closed window '{KitWindow.EscConsumedWindowId}'");
            return false;
        }
    }

    // ---- components ---------------------------------------------------------------------------------------------

    internal sealed class KitWindowTag : MonoBehaviour { internal KitWindow Window; }

    /// <summary>Marks a kit element for <see cref="KitWindow.LayoutReport"/> (leaves only; containers would overlap their children).</summary>
    public sealed class KitLeafMark : MonoBehaviour { }

    /// <summary>Marks a <see cref="KitUI.ScrollView"/> viewport, so the layout report skips rows scrolled out of view.</summary>
    public sealed class KitScrollViewport : MonoBehaviour { }

    internal sealed class KitWindowDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        internal KitWindow Window;
        public void OnBeginDrag(PointerEventData e) { if (e.button == PointerEventData.InputButton.Left) Window?.BringToFront(); }
        public void OnDrag(PointerEventData e) { if (e.button == PointerEventData.InputButton.Left) Window?.DragBy(e.delta); }
        public void OnEndDrag(PointerEventData e) => Window?.EndMove();
    }

    internal sealed class KitWindowResize : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        internal KitWindow Window;
        public void OnBeginDrag(PointerEventData e) { if (e.button == PointerEventData.InputButton.Left) Window?.BringToFront(); }
        public void OnDrag(PointerEventData e) { if (e.button == PointerEventData.InputButton.Left) Window?.ResizeBy(e.delta); }
        public void OnEndDrag(PointerEventData e) => Window?.EndMove();
    }

    /// <summary>The one shared canvas holding every KitWindow (DontDestroyOnLoad). Runs early so Esc is swallowed before
    /// the vanilla menu reads it; watches the screen size and clicks (bring to front).</summary>
    [DefaultExecutionOrder(-1000)]
    internal sealed class KitWindowHost : MonoBehaviour
    {
        internal static KitWindowHost Instance { get; private set; }
        private int m_w, m_h, m_clampFrames;

        internal static KitWindowHost Ensure()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("FiresKitWindows", typeof(RectTransform));
            DontDestroyOnLoad(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = KitWindow.SortOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = KitWindow.ReferenceResolution;
            scaler.matchWidthOrHeight = KitWindow.MatchWidthOrHeight;
            go.AddComponent<GraphicRaycaster>();
            Instance = go.AddComponent<KitWindowHost>();
            Instance.m_w = Screen.width; Instance.m_h = Screen.height;
            return Instance;
        }

        internal static float ScaleFactor
        {
            get
            {
                // the scaler's own formula (log-lerp of the width and height ratios), so it is right before its first update too
                if (Screen.width <= 0 || Screen.height <= 0) return 1f;
                float lw = Mathf.Log(Screen.width / KitWindow.ReferenceResolution.x, 2f), lh = Mathf.Log(Screen.height / KitWindow.ReferenceResolution.y, 2f);
                return Mathf.Pow(2f, Mathf.Lerp(lw, lh, KitWindow.MatchWidthOrHeight));
            }
        }

        internal static Vector2 CanvasSize => new Vector2(Screen.width, Screen.height) / ScaleFactor;

        internal static void KeepTipOnTop() => KitUI.OwnTipToFront();

        private void Update()
        {
            if (Screen.width != m_w || Screen.height != m_h) { m_w = Screen.width; m_h = Screen.height; m_clampFrames = 3; }
            if (m_clampFrames > 0) { m_clampFrames--; KitWindow.ClampAll(); }

            if (KitWindow.Top == null) return;
            if (UnityEngine.Input.GetMouseButtonDown(0) || UnityEngine.Input.GetMouseButtonDown(1))
            {
                var w = KitWindow.At(UnityEngine.Input.mousePosition);
                KitWindow.CloseOutsidePopovers(w);
                if (w != null && w != KitWindow.Top && w.IsShowing) w.BringToFront();
            }
            if (UnityEngine.Input.GetKeyDown(KeyCode.Escape) && !OtherEscOwner()) KitWindow.HandleEscape();
        }

        // Esc belongs to the console, chat, a vanilla text prompt or the open menu first
        private static bool OtherEscOwner()
        {
            try
            {
                if (global::Console.IsVisible()) return true;
                if (Chat.instance != null && Chat.instance.HasFocus()) return true;
                if (TextInput.IsVisible()) return true;
                if (Menu.IsVisible()) return true;
            }
            catch { }
            return false;
        }

        private void OnDestroy() { if (Instance == this) Instance = null; }
    }
}
