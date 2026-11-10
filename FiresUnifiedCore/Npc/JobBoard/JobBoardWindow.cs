using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using FiresCore.UI.Kit;
using KitWindow = FiresCore.UI.Kit.KitWindow;
using KitWindowStyle = FiresCore.UI.Kit.KitWindowStyle;
using FiresCore.UI.Shell.Kit;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Npc.JobBoard
{
    /// <summary>
    /// The Homestead Job Board window (Fire 10-24, approved mockup Tools\mockups\jobboard_20261024): a kit window with three columns -
    /// DAILY WORK (recurring jobs, then the add/edit panel), CURRENT NEEDED JOBS (one-off / now jobs) and COMPANIONS AT BASE (state and
    /// job skills). A job is assigned with its "Assigned" button (Anyone or a named companion) or by dragging a companion card onto it.
    /// A job the base can't run right now is greyed with a tooltip listing what is missing. Open: [Job Board] key (default Ctrl+J) or
    /// the console command jobboard. Logic lives in <see cref="JobBoard"/>.
    /// </summary>
    internal static class JobBoardWindow
    {
        private const string Id = "FiresCore.JobBoard";
        private static KitWindow s_win;
        private static RectTransform s_daily, s_needed, s_comps, s_editor;
        private static JobBoard.Job s_sel;
        private static bool s_dirty, s_dragging;
        private static float s_nextRefresh;
        // 1086 ([generator], Fire 11-01: configs live in their own mods): bound by FiresCompanions; Core reads the owner's value (old default without it).
        private static readonly FiresCore.Config.OwnedSetting<KeyboardShortcut> s_key = new FiresCore.Config.OwnedSetting<KeyboardShortcut>("Job Board", "Open key", new KeyboardShortcut(KeyCode.J, KeyCode.LeftControl));
        private static readonly Color Grey = new Color(1f, 1f, 1f, 0.45f);
        private static readonly string[] Prey = { "Deer", "Boar", "Neck", "Hare", "Lox", "Chicken", "Asksvin", "Volture" };

        internal static void BindConfig(ConfigFile cfg) { }

        internal static bool IsShowing => s_win != null && s_win.IsShowing;   // add-only, for JobBoardApi (the Job Board piece)

        // 0.2.858 (Fire 10-27 shot 556: "choosing the map should close, or minimize the job board ui and dock it to the side"): the
        // homestead map hides the full board while it shows (HomesteadMap's docked panel stands in) and puts it back when the map closes.
        private static bool s_hiddenForMap;
        internal static void HideForMap() { if (s_win != null && s_win.IsShowing) { s_win.Hide(); s_hiddenForMap = true; } }
        internal static void RestoreAfterMap(bool show)
        {
            bool was = s_hiddenForMap; s_hiddenForMap = false;
            if (show && (was || s_win == null || !s_win.IsShowing)) { if (s_win == null || !s_win.IsShowing) Toggle(); }
        }

        // 0.2.831 (Fire 10-26: "the job board itself doesnt block camera movement or game inputs"): while the window shows it holds
        // the game's input like Core's other windows: FiresInputBlock (owned by the window object, so it frees itself if the window
        // is ever deactivated without OnClosed), the older InputBlock gate, and a free cursor. Esc closes it (KitWindow's Esc stack).
        private static readonly object s_inputToken = new object();
        private static bool s_inputHeld, s_blockedByUs;

        private static void CaptureInput()
        {
            if (s_inputHeld || s_win?.Root == null) return;
            s_inputHeld = true;
            FiresCore.Input.FiresInputBlock.Acquire(s_inputToken, s_win.Root.gameObject);
            if (!FiresCore.UI.InputBlock.IsBlocked) { FiresCore.UI.InputBlock.Block(true); s_blockedByUs = true; }
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            FiresCore.Logging.FiresLogger.LogDebug("[JobBoard] input captured (camera, movement and attacks held while the board is open)");
        }

        private static void ReleaseInput()
        {
            if (!s_inputHeld) return;
            s_inputHeld = false;
            FiresCore.Input.FiresInputBlock.Release(s_inputToken);
            if (s_blockedByUs) { FiresCore.UI.InputBlock.Block(false); s_blockedByUs = false; }
            FiresCore.Logging.FiresLogger.LogDebug("[JobBoard] input released");
        }

        // ---- open / close --------------------------------------------------------------------------------------------
        internal static void Toggle()
        {
            if (s_win != null && s_win.IsShowing) { s_win.Hide(); return; }
            if (s_win == null || s_win.Root == null)
            {
                s_win = KitWindow.Create(Id, "Homestead Job Board", "", new Vector2(1500f, 780f), new Vector2(1100f, 560f));
                BuildFrame(s_win.Body);
                JobBoard.Changed += () => s_dirty = true;
                s_win.OnClosed += ReleaseInput;
                // a registered mod screen: the HUD hides under it and other mods see a Fires window is up
                FiresCore.Bridge.ModUiRegistry.Register("FiresJobBoard", () => IsShowing, () => s_win?.Hide(), () => s_win?.Root != null ? s_win.Root.gameObject : null);
            }
            s_buttonsMeasured = false;   // 0.2.863: measure the add buttons again on this opening
            JobBoard.Load();
            s_win.Show();
            CaptureInput();
            Refresh();
            FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] opened: {JobBoard.Jobs.Count} job(s), {JobBoard.CompanionsAtBase().Count} companion(s) at base");
        }

        private static void BuildFrame(RectTransform body)
        {
            // 0.2.854 (Fire 10-27: "job boards should get a reports section"): a Board / Reports switch above the columns
            // 0.2.868 (Fire 10-27 shots 580/581: "map board and reports have to take up the whole top half of the ui tho instead of being
            // proper header tabs" / "reports shows nothing because these fucking buttons are so goddamn big" / "theres no scroll area"):
            // the window body is a VerticalLayoutGroup, so the old anchors were ignored and the tab row (plain buttons, nothing fixing its
            // height) took the spare height. Now: the kit's segmented tabs (the same control as the board's own editor and the other
            // windows) in a FIXED strip (min = preferred = 32 px, flexibleHeight 0, left-aligned, sized to its labels), and each page
            // (Board columns / Reports) with flexibleHeight 1 fills the rest. Reports' kit ScrollView fills its page (scrollbar + wheel).
            // Line after layout on each opening: "[JobBoardUI] tabs: N, height H px, body fills yes/no (page WxH of body WxH)".
            // 0.2.887 (Fire, shot 47713484: "BoaRepoMap" - the segmented control sized its segments from GetPreferredValues at build time =
            // 0 width, all three labels on one point; Fire: "Make each button wide enough for the text to fit and give them padding in between
            // them. Spread them out"): [classes-ui]'s Classes header tabs - NO layout group, each tab anchored by hand (anchorMin (0,0),
            // anchorMax (0,1), pivot (0,0.5), explicit width and x) on a strip with a 1 px bottom rule; a clear hit image, the label stretched
            // over it, a gold underline when active (caption Gold + Bold active, Muted otherwise). Width = label + 2 x TabPad, min TabMin, TabGap
            // between, from the content margin; PlaceTabs re-measures the labels after the canvas laid them out (every opening), so nothing can
            // stay at zero width.
            var tabs = KitUI.NewRect(body, "Tabs");
            var tle = tabs.gameObject.AddComponent<LayoutElement>(); tle.minHeight = TabStripH; tle.preferredHeight = TabStripH; tle.flexibleHeight = 0f;
            var rule = new GameObject("Rule", typeof(RectTransform)); var rrt = (RectTransform)rule.transform; rrt.SetParent(tabs, false);
            rrt.anchorMin = Vector2.zero; rrt.anchorMax = new Vector2(1f, 0f); rrt.pivot = new Vector2(0.5f, 0f); rrt.sizeDelta = new Vector2(0f, 1f);
            var ri = rule.AddComponent<Image>(); ri.color = FiresTokens.Hairline; ri.raycastTarget = false;
            s_tabButtons.Clear();
            string[] names = { "Board", "Reports", "Map" };
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                var go = new GameObject("Tab_" + names[i], typeof(RectTransform));
                var trt = (RectTransform)go.transform; trt.SetParent(tabs, false);
                trt.anchorMin = new Vector2(0f, 0f); trt.anchorMax = new Vector2(0f, 1f); trt.pivot = new Vector2(0f, 0.5f);
                var hit = go.AddComponent<Image>(); hit.color = new Color(0f, 0f, 0f, 0.001f);
                var btn = go.AddComponent<Button>(); btn.targetGraphic = hit; btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(() => OnTab(idx));
                var lab = KitUI.Label(trt, "Label", names[i], KitUI.BodySize + 2f, FiresTokens.TextMuted, TextAlignmentOptions.Center);
                lab.textWrappingMode = TextWrappingModes.NoWrap; lab.raycastTarget = false;
                var lrt = lab.rectTransform; lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = lrt.offsetMax = Vector2.zero;
                var ul = new GameObject("Underline", typeof(RectTransform)); var urt = (RectTransform)ul.transform; urt.SetParent(trt, false);
                urt.anchorMin = new Vector2(0.12f, 0f); urt.anchorMax = new Vector2(0.88f, 0f); urt.pivot = new Vector2(0.5f, 0f); urt.sizeDelta = new Vector2(0f, 3f);
                var ui = ul.AddComponent<Image>(); ui.color = FiresTokens.Gold; ui.raycastTarget = false; ui.enabled = false;
                s_tabButtons.Add((trt, lab, ui));
            }
            s_tabStrip = tabs;
            PlaceTabs();
            PaintTabs(0);
            var row = KitUI.NewRect(body, "Columns");
            var rle = row.gameObject.AddComponent<LayoutElement>(); rle.flexibleHeight = 1f; rle.minHeight = 200f;
            s_boardRoot = row;
            var rep = KitUI.NewRect(body, "Reports");
            var ple = rep.gameObject.AddComponent<LayoutElement>(); ple.flexibleHeight = 1f; ple.minHeight = 200f;
            var repImg = rep.gameObject.AddComponent<Image>();
            KitUI.SetPlate(repImg, KitUI.Plate(FiresTokens.Radius, FiresTokens.PanelRaised, FiresTokens.Hairline));
            s_reportsRoot = rep;
            s_reports = KitUI.ScrollView(rep, 200f);
            rep.gameObject.SetActive(false);
            JobReport.Changed += () => { if (s_reportsRoot != null && s_reportsRoot.gameObject.activeSelf) s_reportsDirty = true; };
            var hl = row.gameObject.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = 14f; hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false; hl.childForceExpandHeight = true;
            s_daily = Column(row, "DAILY WORK", 1f, "+ Add daily job", () => AddMenu(JobBoard.Repeat.Daily), out _);
            s_needed = Column(row, "CURRENT NEEDED JOBS", 1f, "+ Add one-off job", () => AddMenu(JobBoard.Repeat.OneOff), out _);
            s_comps = Column(row, "COMPANIONS AT BASE", 0.62f, null, null, out _);
        }

        private static RectTransform s_boardRoot, s_reportsRoot, s_reports, s_tabStrip;
        // 0.2.887: the header tabs ([classes-ui]'s anchored tab pattern)
        private const float TabStripH = 40f, TabPad = 24f, TabMin = 140f, TabGap = 12f, TabMargin = 14f;
        private static readonly List<(RectTransform rt, TextMeshProUGUI label, Image underline)> s_tabButtons = new List<(RectTransform, TextMeshProUGUI, Image)>();
        private static int s_tabActive;

        private static void OnTab(int i)
        {
            if (i == 2) { HomesteadMap.Open(); return; }   // 0.2.856: the homestead map (the board docks; the active page stays)
            ShowReports(i == 1);
            PaintTabs(i);
        }

        private static void PaintTabs(int active)
        {
            s_tabActive = active;
            for (int i = 0; i < s_tabButtons.Count; i++)
            {
                var (rt, lab, ul) = s_tabButtons[i];
                if (lab == null) continue;
                bool on = i == active;
                lab.color = on ? FiresTokens.Gold : FiresTokens.TextMuted;
                lab.fontStyle = on ? FontStyles.Bold : FontStyles.Normal;
                if (ul != null) ul.enabled = on;
            }
        }

        /// <summary>Lays the tabs out left to right from the content margin: width = the label's measured width + 2 x TabPad (min TabMin), TabGap
        /// between. Run at build and again after the canvas has laid the labels out (each opening), so a zero build-time measure can't stick.</summary>
        private static void PlaceTabs()
        {
            float x = TabMargin;
            foreach (var (rt, lab, _) in s_tabButtons)
            {
                if (rt == null || lab == null) continue;
                float w = Mathf.Max(TabMin, Mathf.Ceil(lab.GetPreferredValues(lab.text).x) + TabPad * 2f);
                rt.sizeDelta = new Vector2(w, 0f);
                rt.anchoredPosition = new Vector2(x, 0f);
                x += w + TabGap;
            }
        }

        // 0.2.868: proof of the tab strip and page layout, once per opening (with the add-button lines)
        // 0.2.875: test hooks (homemap_test) - switch the page the way the tabs do, and read the strip / page layout
        internal static void TestShowTab(int i) { ShowReports(i == 1); PaintTabs(i); }
        internal static string TestLayout(out bool ok)
        {
            ok = false;
            if (s_tabStrip == null || s_win == null || s_win.Body == null || !s_win.IsShowing) return "board not showing";
            Canvas.ForceUpdateCanvases();
            var page = s_reportsRoot != null && s_reportsRoot.gameObject.activeSelf ? s_reportsRoot : s_boardRoot;
            var body = s_win.Body;
            PlaceTabs();
            string tabs = TabReport(out bool tabsOk);   // 0.2.887: each tab's own rect - the old check passed while the tabs sat on one point
            ok = tabsOk && s_tabStrip.rect.height <= 44f && page != null && page.rect.height > 0.6f * body.rect.height;
            return $"tab strip {s_tabStrip.rect.height:0} px; {tabs}; {page?.name} {page?.rect.width:0}x{page?.rect.height:0} of body {body.rect.width:0}x{body.rect.height:0}";
        }

        private static void MeasureTabs()
        {
            if (s_tabStrip == null || s_win == null || s_win.Body == null) return;
            var page = s_reportsRoot != null && s_reportsRoot.gameObject.activeSelf ? s_reportsRoot : s_boardRoot;
            if (page == null) return;
            var body = s_win.Body;
            float spare = body.rect.height - s_tabStrip.rect.height - page.rect.height;
            bool fills = page.rect.height > 0.6f * body.rect.height && page.rect.width > 0.9f * body.rect.width;
            FiresCore.Logging.FiresLogger.LogDebug($"[JobBoardUI] tabs: 3, height {s_tabStrip.rect.height:0} px, body fills {(fills ? "yes" : "NO")} ({page.name} {page.rect.width:0}x{page.rect.height:0} of body {body.rect.width:0}x{body.rect.height:0}, {spare:0} px left for spacing/padding)");
            PlaceTabs();   // 0.2.887: re-measured now the canvas has laid the labels out
            FiresCore.Logging.FiresLogger.LogDebug("[JobBoardUI] tabs: " + TabReport(out _));
        }

        /// <summary>0.2.887: "Board x=..,w=.. | Reports x=..,w=.. | Map x=..,w=.., gaps a, b, labels fit yes/NO"; ok = every tab wider than its
        /// label (+ padding) and no two tabs overlapping.</summary>
        internal static string TabReport(out bool ok)
        {
            ok = s_tabButtons.Count == 3;
            var parts = new List<string>(); var gaps = new List<string>();
            float prevEnd = float.NaN; bool fit = true;
            foreach (var (rt, lab, _) in s_tabButtons)
            {
                if (rt == null || lab == null) { ok = false; continue; }
                float x = rt.anchoredPosition.x, w = rt.rect.width, need = lab.GetPreferredValues(lab.text).x;
                parts.Add($"{lab.text} x={x:0},w={w:0}");
                if (w < need + 8f || w < 1f) fit = false;
                if (!float.IsNaN(prevEnd)) { gaps.Add($"{x - prevEnd:0}"); if (x - prevEnd < 4f) ok = false; }
                prevEnd = x + w;
            }
            ok = ok && fit;
            return $"{string.Join(" | ", parts)}, gaps {string.Join(", ", gaps)}, labels fit {(fit ? "yes" : "NO")}";
        }
        private static bool s_reportsDirty, s_layoutSaid;
        private static float s_reportsNext;

        private static void ShowReports(bool on)
        {
            if (s_boardRoot == null || s_reportsRoot == null) return;
            s_boardRoot.gameObject.SetActive(!on);
            s_reportsRoot.gameObject.SetActive(on);
            if (on) { JobReportView.Build(s_reports); s_reportsDirty = false; s_reportsNext = Time.unscaledTime + 2f; }
            FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] {(on ? "Reports" : "Board")} tab shown");
        }

        // the Reports tab rebuilds at most every 2 s while it shows and something was recorded
        internal static void TickReports()
        {
            if (!s_reportsDirty || s_reportsRoot == null || !s_reportsRoot.gameObject.activeSelf || Time.unscaledTime < s_reportsNext) return;
            s_reportsDirty = false; s_reportsNext = Time.unscaledTime + 2f;
            JobReportView.Build(s_reports);
        }

        // 0.2.863: the header add buttons, measured after layout on the first refresh of each opening:
        // "[JobBoardUI] button '<label>' text WxH, button WxH, inside header: yes/no, clipped: yes/no"
        private static readonly List<(RectTransform btn, TextMeshProUGUI lab, RectTransform head)> s_addButtons = new List<(RectTransform, TextMeshProUGUI, RectTransform)>();
        private static bool s_buttonsMeasured;
        private static void MeasureAddButtons()
        {
            if (s_buttonsMeasured) return;
            s_buttonsMeasured = true;
            Canvas.ForceUpdateCanvases();
            MeasureTabs();   // 0.2.868
            foreach (var (btn, lab, head) in s_addButtons)
            {
                if (btn == null || lab == null || head == null) continue;
                Rect b = Screenish(btn), h = Screenish(head), l = Screenish(lab.rectTransform);
                bool inside = b.xMin >= h.xMin - 0.5f && b.xMax <= h.xMax + 0.5f && b.yMin >= h.yMin - 0.5f && b.yMax <= h.yMax + 0.5f;
                Vector2 need = lab.GetPreferredValues(lab.text);
                bool clipped = need.x > lab.rectTransform.rect.width + 0.5f || l.xMin < b.xMin - 0.5f || l.xMax > b.xMax + 0.5f || lab.isTextOverflowing;
                FiresCore.Logging.FiresLogger.LogDebug($"[JobBoardUI] button '{lab.text}' text {need.x:0}x{need.y:0}, button {btn.rect.width:0}x{btn.rect.height:0}, inside header: {(inside ? "yes" : "NO")}, clipped: {(clipped ? "YES" : "no")}");
            }
        }
        private static Rect Screenish(RectTransform rt)
        {
            var c = new Vector3[4]; rt.GetWorldCorners(c);
            return Rect.MinMaxRect(c[0].x, c[0].y, c[2].x, c[2].y);
        }

        private static RectTransform Column(RectTransform parent, string title, float weight, string addText, Action add, out RectTransform root)
        {
            root = KitUI.NewRect(parent, "Col_" + title);
            var img = root.gameObject.AddComponent<Image>();
            KitUI.SetPlate(img, KitUI.Plate(FiresTokens.Radius, FiresTokens.PanelRaised, FiresTokens.Hairline));
            var le = root.gameObject.AddComponent<LayoutElement>(); le.flexibleWidth = weight; le.minWidth = 300f;
            var vl = root.gameObject.AddComponent<VerticalLayoutGroup>();
            vl.padding = new RectOffset(10, 10, 8, 10); vl.spacing = 6f;
            vl.childControlWidth = true; vl.childControlHeight = true; vl.childForceExpandWidth = true; vl.childForceExpandHeight = false;
            // 0.2.854 (Fire 10-27, shot 542: "im not sure why the daily work, current needed jobs, and companions at base headers are half
            // the screen, and the add job buttons text is not fitting the button, (this should be a MUCH smaller button)"): the header is a
            // fixed HeadHeight bar (min = preferred, flexibleHeight 0: it no longer shares the column's spare height with the table); the title
            // sits left, a small "+ Add" pill (ButtonH tall, as wide as its text + 2 x 8 px) right on the same row; the table starts below.
            const float HeadHeight = 34f, ButtonH = 24f, ButtonPad = 8f;
            var head = KitUI.NewRect(root, "Head");
            var hh = head.gameObject.AddComponent<HorizontalLayoutGroup>();
            hh.childAlignment = TextAnchor.MiddleLeft; hh.spacing = 8f;
            hh.childControlWidth = true; hh.childControlHeight = true; hh.childForceExpandWidth = false; hh.childForceExpandHeight = false;
            var hle = head.gameObject.AddComponent<LayoutElement>();
            hle.minHeight = HeadHeight; hle.preferredHeight = HeadHeight; hle.flexibleHeight = 0f;
            var t = KitUI.Label(head, "Title", title, KitUI.HeaderSize + 2f, FiresTokens.Gold, TextAlignmentOptions.MidlineLeft);
            t.fontStyle = FontStyles.Bold; t.characterSpacing = FiresTokens.SectionTracking;
            var tle = t.gameObject.AddComponent<LayoutElement>(); tle.flexibleWidth = 1f; tle.flexibleHeight = 0f;
            if (addText != null)
            {
                // 0.2.863 (Fire 10-27 shot 572, third ask: "just make a fucking button that is the correct size for hte text"): the old
                // width came from GetPreferredValues at build time (font not laid out yet = a ~30 px box with the label spilling left out of
                // it). Now one plain button: background + Button, its label a CHILD, and the button's own HorizontalLayoutGroup (8 px sides,
                // 3 px top/bottom) makes its preferred size = label preferred size + padding; the header row sizes it from that.
                var bgo = new GameObject("Add", typeof(RectTransform));
                var brt = (RectTransform)bgo.transform; brt.SetParent(head, false);
                var bimg = bgo.AddComponent<Image>();
                var btn = bgo.AddComponent<Button>(); btn.targetGraphic = bimg; btn.onClick.AddListener(() => add());
                KitUI.StyleButton(btn, false);
                var bl = bgo.AddComponent<HorizontalLayoutGroup>();
                bl.padding = new RectOffset((int)ButtonPad, (int)ButtonPad, 3, 3); bl.childAlignment = TextAnchor.MiddleCenter;
                bl.childControlWidth = true; bl.childControlHeight = true; bl.childForceExpandWidth = false; bl.childForceExpandHeight = false;
                var ble = bgo.AddComponent<LayoutElement>(); ble.flexibleWidth = 0f; ble.flexibleHeight = 0f; ble.minHeight = ButtonH;
                var lab = KitUI.Label(brt, "Label", addText, KitUI.SmallSize, FiresTokens.Text, TextAlignmentOptions.Center);
                lab.textWrappingMode = TextWrappingModes.NoWrap; lab.raycastTarget = false;
                s_addButtons.Add((brt, lab, head));
            }
            return KitUI.ScrollView(root, 120f);
        }

        // ---- refresh -------------------------------------------------------------------------------------------------
        private static void Refresh()
        {
            if (s_win == null || !s_win.IsShowing) return;
            s_dirty = false;
            MeasureAddButtons();   // 0.2.863
            Clear(s_daily); Clear(s_needed); Clear(s_comps);
            var daily = JobBoard.Jobs.Where(j => j.Repeat == JobBoard.Repeat.Daily).ToList();
            var needed = JobBoard.Jobs.Where(j => j.Repeat != JobBoard.Repeat.Daily).ToList();
            HeadRow(s_daily, "Last / next");
            foreach (var j in daily) JobRow(s_daily, j);
            if (daily.Count == 0) KitUI.Note(s_daily, "No daily work yet. \"+ Add daily job\" picks a chore or a trip and the time it runs each day.");
            s_editor = KitUI.Section(s_daily, s_sel != null ? "EDIT JOB · " + JobBoard.Label(s_sel).ToUpperInvariant() : "EDIT JOB");
            if (s_sel != null && JobBoard.Jobs.Contains(s_sel)) Editor(s_editor, s_sel);
            else KitUI.Note(s_editor, "Click a job to edit its schedule, companions and (for trips) its center and radius.");
            HeadRow(s_needed, "Status");
            foreach (var j in needed) JobRow(s_needed, j);
            if (needed.Count == 0) KitUI.Note(s_needed, "Nothing queued. One-off jobs run once at their time and drop off the board; a right-click quick trip lands here.");
            var comps = JobBoard.CompanionsAtBase();
            foreach (var c in comps) CompanionCard(s_comps, c);
            if (comps.Count == 0) KitUI.Note(s_comps, "No companions at base (within 60 m of home).");
            KitUI.Note(s_comps, "Drag a companion onto a job to assign it, or use the job's Assigned button (Anyone / a name).");
            Subtitle(comps.Count);
            s_compSig = CompSig(comps); s_nextFull = Time.unscaledTime + FullRefreshSeconds;
        }

        private const float FullRefreshSeconds = 15f;
        private static float s_nextFull; private static int s_compSig;

        private static int CompSig(List<CompanionController> comps)
        {
            int h = comps.Count;
            foreach (var c in comps) h = h * 31 + (c != null ? c.GetInstanceID() : 0);
            return h;
        }

        private static void Subtitle(int compsAtBase)
        {
            s_win.SetSubtitle($"{JobBoard.Jobs.Count} job(s) · {compsAtBase} companion(s) at base · " + (EnvMan.instance != null ? $"day {EnvMan.instance.GetDay()}, {JobBoard.Clock(Mathf.FloorToInt(EnvMan.instance.GetDayFraction() * 1440f))}" : ""));
        }

        private static void Clear(RectTransform rt) { if (rt == null) return; for (int i = rt.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(rt.GetChild(i).gameObject); }

        private static RectTransform Cells(Transform parent, string name, float height)
        {
            var r = KitUI.NewRect(parent, name);
            var h = r.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 8f; h.padding = new RectOffset(10, 10, 4, 4); h.childAlignment = TextAnchor.MiddleCenter;
            h.childControlWidth = true; h.childControlHeight = true; h.childForceExpandWidth = false; h.childForceExpandHeight = false;
            r.gameObject.AddComponent<LayoutElement>().minHeight = height;
            return r;
        }

        private static TextMeshProUGUI Cell(Transform parent, string text, float flex, Color color, float size, TextAlignmentOptions align)
        {
            var t = KitUI.Label(parent, "Cell", text, size, color, align);
            t.textWrappingMode = TextWrappingModes.Normal;
            var le = t.gameObject.AddComponent<LayoutElement>(); le.flexibleWidth = flex; le.minWidth = 40f; le.preferredWidth = 10f;
            return t;
        }

        private static void HeadRow(RectTransform col, string last)
        {
            var r = Cells(col, "HeadRow", 22f);
            Cell(r, "JOB", 1.6f, FiresTokens.TextFaint, KitUI.NoteSize, TextAlignmentOptions.MidlineLeft);
            Cell(r, "SCHEDULE", 1f, FiresTokens.TextFaint, KitUI.NoteSize, TextAlignmentOptions.Center);
            Cell(r, "ASSIGNED", 1.1f, FiresTokens.TextFaint, KitUI.NoteSize, TextAlignmentOptions.Center);
            Cell(r, last.ToUpperInvariant(), 1f, FiresTokens.TextFaint, KitUI.NoteSize, TextAlignmentOptions.Center);
        }

        private static void JobRow(RectTransform col, JobBoard.Job j)
        {
            var miss = JobBoard.Missing(j);
            var r = Cells(col, "Job_" + j.Id, 52f);
            var img = r.gameObject.AddComponent<Image>();
            bool sel = s_sel == j;
            KitUI.SetPlate(img, KitUI.Plate(FiresTokens.RadiusSmall, sel ? FiresTokens.Selected : FiresTokens.Well, sel ? FiresTokens.GoldBright : FiresTokens.Hairline));
            var name = KitUI.NewRect(r, "Name");
            var nv = name.gameObject.AddComponent<VerticalLayoutGroup>(); nv.childControlWidth = true; nv.childControlHeight = true; nv.childForceExpandHeight = false;
            var nle = name.gameObject.AddComponent<LayoutElement>(); nle.flexibleWidth = 1.6f; nle.minWidth = 120f; nle.preferredWidth = 10f;
            var t1 = KitUI.Label(name, "Label", JobBoard.Label(j), KitUI.BodySize + 1f, FiresTokens.Text, TextAlignmentOptions.MidlineLeft); t1.fontStyle = FontStyles.Bold;
            var t2 = KitUI.Label(name, "Detail", JobBoard.Detail(j), KitUI.SmallSize, FiresTokens.TextMuted, TextAlignmentOptions.MidlineLeft); t2.textWrappingMode = TextWrappingModes.Normal;
            Cell(r, JobBoard.Schedule(j), 1f, j.Repeat == JobBoard.Repeat.Daily ? FiresTokens.GoldBright : j.Repeat == JobBoard.Repeat.Now ? FiresTokens.Ok : FiresTokens.Text, KitUI.SmallSize, TextAlignmentOptions.Center);
            var assign = KitUI.Button(r, JobBoard.AssignedText(j), false, () => AssignMenu(j));
            var ale = assign.Rect.GetComponent<LayoutElement>(); if (ale != null) { ale.flexibleWidth = 1.1f; ale.minWidth = 80f; ale.preferredWidth = 10f; }
            string status = miss.Count > 0 ? "unavailable · hover for why" : JobBoard.LastNext(j);
            Cell(r, status, 1f, status.StartsWith("●") ? FiresTokens.Warn : status.StartsWith("✔") ? FiresTokens.Ok : FiresTokens.TextMuted, KitUI.SmallSize, TextAlignmentOptions.Center);
            if (status.StartsWith("●")) { var job = j; KitUI.Button(r, "Show on map", false, () => { s_win?.Hide(); TripMapView.Show(job); }); }
            if (miss.Count > 0)
            {
                var cg = r.gameObject.AddComponent<CanvasGroup>(); cg.alpha = 0.45f;
                KitUI.AttachTooltip(r.gameObject, $"{JobBoard.Label(j)}: can't run yet\n" + string.Join("\n", miss.Select(m => "✖ Needs " + m)) + "\nRechecked from the stations and chests around base");
            }
            var click = r.gameObject.AddComponent<JobDrop>(); click.Job = j;
        }

        // ---- the add/edit panel ---------------------------------------------------------------------------------------
        private static void Editor(RectTransform sec, JobBoard.Job j)
        {
            var rep = KitUI.Row(sec, "Repeat");
            KitUI.Segmented(rep.Control, new[] { "Daily", "One-off", "Now" }, (int)j.Repeat, i => { j.Repeat = (JobBoard.Repeat)i; JobBoard.ArmDue(j); JobBoard.Saved(j); });
            if (j.Repeat != JobBoard.Repeat.Now)
            {
                var tod = KitUI.Row(sec, "Time of day", "In-game clock; daily jobs run each day at this time, one-off jobs once.");
                KitStepper st = null;
                st = KitUI.Stepper(tod.Control, JobBoard.Clock(j.Minute), () => { j.Minute = (j.Minute + 1410) % 1440; st.SetValue(JobBoard.Clock(j.Minute)); JobBoard.ArmDue(j); JobBoard.Saved(j); },
                                                                       () => { j.Minute = (j.Minute + 30) % 1440; st.SetValue(JobBoard.Clock(j.Minute)); JobBoard.ArmDue(j); JobBoard.Saved(j); });
            }
            if (j.IsTrip)
            {
                var type = KitUI.Row(sec, "Trip type");
                int ti = j.Kind == JobBoard.Kind.FishTrip ? 2 : j.Kind == JobBoard.Kind.MineTrip ? 3 : string.IsNullOrEmpty(j.Prey) ? 0 : 1;
                KitUI.Segmented(type.Control, new[] { "Hunt any game", "Hunt species", "Fish", "Mine" }, ti, i =>
                {
                    j.Kind = i == 2 ? JobBoard.Kind.FishTrip : i == 3 ? JobBoard.Kind.MineTrip : JobBoard.Kind.HuntTrip;
                    j.Prey = i == 1 ? (string.IsNullOrEmpty(j.Prey) ? FirstPrey() : j.Prey) : "";
                    JobBoard.Saved(j);
                });
                if (j.Kind == JobBoard.Kind.HuntTrip && !string.IsNullOrEmpty(j.Prey))
                {
                    var sp = KitUI.Row(sec, "Species");
                    KitStepper ss = null;
                    ss = KitUI.Stepper(sp.Control, JobBoard.PreyName(j.Prey), () => { j.Prey = NextPrey(j.Prey, -1); JobBoard.Saved(j); }, () => { j.Prey = NextPrey(j.Prey, 1); JobBoard.Saved(j); });
                }
                var cnt = KitUI.Row(sec, "Companions", "How many go (a party is up to 3). Assigned names go first, else the best free companions at base.");
                KitUI.Segmented(cnt.Control, new[] { "1", "2", "3" }, Mathf.Clamp(j.Count, 1, 3) - 1, i => { j.Count = i + 1; JobBoard.Saved(j); });
                KitUI.Button(cnt.Control, JobBoard.AssignedText(j), false, () => AssignMenu(j));
                var ctr = KitUI.Row(sec, "Center", "X / Y / Z of the trip's center. Enter applies a typed value.");
                Coord(ctr.Control, "X", j.Center.x, v => { j.Center.x = v; JobBoard.Saved(j); });
                Coord(ctr.Control, "Y", j.Center.y, v => { j.Center.y = v; JobBoard.Saved(j); });
                Coord(ctr.Control, "Z", j.Center.z, v => { j.Center.z = v; JobBoard.Saved(j); });
                var ctr2 = KitUI.Row(sec, "");
                KitUI.Button(ctr2.Control, "Use my position", false, () => { if (Player.m_localPlayer != null) { j.Center = Player.m_localPlayer.transform.position; JobBoard.Saved(j); } });
                KitUI.Button(ctr2.Control, "Map pin…", false, () => PinMenu(j));
                var rad = KitUI.Row(sec, "Radius", "The area the companions work: 30-100 m around the center.");
                KitUI.Slider(rad.Control, 30f, 100f, 5f, Mathf.Clamp(j.Radius, 30f, 100f), "m", v => { j.Radius = v; JobBoard.Saved(j); });
            }
            else
            {
                var who = KitUI.Row(sec, "Companion");
                KitUI.Button(who.Control, JobBoard.AssignedText(j), false, () => AssignMenu(j));
            }
            var miss = JobBoard.Missing(j);
            KitUI.Note(sec, miss.Count == 0 ? "✔ Ready" + (j.IsTrip ? $" · {Vector3.Distance(Flat(j.Center), Flat(JobBoard.BaseCenter())):0} m from home (limit {TripBehavior.MaxRange:0} m) · stops on bag full, nightfall, low health, 5 min with nothing to do" : "")
                                         : "✖ Needs " + string.Join(", ", miss), miss.Count > 0);
            var btn = KitUI.Strip(sec);
            KitUI.Button(btn, "Run now", true, () => { int n = JobBoard.Run(j); FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] {JobBoard.Label(j)}: run now, {n} started"); if (n > 0) { j.Started = true; j.LastRunDay = EnvMan.instance != null ? EnvMan.instance.GetDay() : j.LastRunDay; } JobBoard.Saved(j); });
            KitUI.Button(btn, "Remove", false, () => { s_sel = null; JobBoard.Remove(j); });
        }

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        private static void Coord(RectTransform parent, string axis, float value, Action<float> set)
        {
            KitUI.Text(parent, axis, FiresTokens.TextMuted);
            var f = KitUI.TextField(parent, axis, s => { if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) set(v); });
            f.SetText(value.ToString("0", CultureInfo.InvariantCulture));
            var le = f.Input.GetComponent<LayoutElement>() ?? f.Input.gameObject.AddComponent<LayoutElement>(); le.preferredWidth = 70f; le.minWidth = 60f;
        }

        private static string FirstPrey() => Prey.FirstOrDefault(p => ZNetScene.instance != null && ZNetScene.instance.GetPrefab(p) != null) ?? "Deer";
        private static string NextPrey(string cur, int dir)
        {
            var list = Prey.Where(p => ZNetScene.instance != null && ZNetScene.instance.GetPrefab(p) != null).ToList();
            if (list.Count == 0) return cur;
            int i = Mathf.Max(0, list.IndexOf(cur));
            return list[(i + dir + list.Count) % list.Count];
        }

        // ---- popover menus --------------------------------------------------------------------------------------------
        private static KitWindow s_menu;
        private static void Menu(string title, List<(string label, Action act)> items)
        {
            if (s_menu != null) s_menu.Destroy();
            s_menu = KitWindow.Create(Id + ".Menu", title, "", new Vector2(320f, Mathf.Min(520f, 70f + items.Count * 36f)), new Vector2(240f, 120f), KitWindowStyle.Popover);
            s_menu.RememberRect = false;
            Vector2 m = UnityEngine.Input.mousePosition;
            s_menu.DefaultPlacement = px => new Rect(Mathf.Clamp(m.x, 0f, Screen.width - px.x), Mathf.Clamp(m.y - px.y, 0f, Screen.height - px.y), px.x, px.y);
            var scroll = KitUI.ScrollView(s_menu.Body, 60f);
            foreach (var it in items)
            {
                var a = it.act;
                var b = KitUI.Button(scroll, it.label, false, () => { s_menu?.Hide(); a(); });
                var le = b.Rect.GetComponent<LayoutElement>(); if (le != null) le.flexibleWidth = 1f;
            }
            s_menu.Show();
        }

        private static void AddMenu(JobBoard.Repeat repeat)
        {
            var items = new List<(string, Action)>();
            foreach (JobBoard.Kind k in Enum.GetValues(typeof(JobBoard.Kind)))
            {
                var kind = k;
                var probe = new JobBoard.Job { Kind = kind, Center = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero };
                var miss = JobBoard.Missing(probe);
                items.Add((JobBoard.Label(probe) + (miss.Count > 0 ? "  (needs " + string.Join(", ", miss) + ")" : ""), () => { s_sel = JobBoard.Add(kind, repeat); s_dirty = true; }));
            }
            Menu(repeat == JobBoard.Repeat.Daily ? "Add daily job" : "Add one-off job", items);
        }

        private static void AssignMenu(JobBoard.Job j)
        {
            var items = new List<(string, Action)> { ("Anyone (first free companion)", () => JobBoard.Assign(j, null)) };
            var skill = JobBoard.SkillOf(j.Kind);
            foreach (var c in JobBoard.CompanionsAtBase())
            {
                var comp = c;
                string s = skill == Skills.SkillType.None ? "" : $" · {skill} {c.GetSkills()?.GetSkillLevel(skill) ?? 0f:0}";
                items.Add(($"{c.GetDisplayName()} ({JobBoard.StateOf(c)}{s})", () => JobBoard.Assign(j, comp, j.IsTrip && j.Assigned.Count < j.Count)));
            }
            Menu("Assign: " + JobBoard.Label(j), items);
        }

        private static void PinMenu(JobBoard.Job j)
        {
            var items = new List<(string, Action)>();
            var pins = Minimap.instance != null ? Traverse.Create(Minimap.instance).Field("m_pins").GetValue<List<Minimap.PinData>>() : null;
            if (pins != null)
                foreach (var p in pins.Where(p => p != null && p.m_save && p.m_ownerID == 0L))
                {
                    var pos = p.m_pos;
                    string n = string.IsNullOrEmpty(p.m_name) ? p.m_type.ToString() : p.m_name;
                    items.Add(($"{n} ({pos.x:0}, {pos.z:0})", () => { j.Center = new Vector3(pos.x, ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(pos, out float h) ? h : pos.y, pos.z); JobBoard.Saved(j); }));
                }
            if (items.Count == 0) items.Add(("No map pins of yours yet", () => { }));
            Menu("Center on a map pin", items);
        }

        // ---- companions -----------------------------------------------------------------------------------------------
        private static readonly (string label, Skills.SkillType type)[] CardSkills =
        {
            ("Farming", Skills.SkillType.Farming), ("Hunting", Skills.SkillType.Bows), ("Fishing", Skills.SkillType.Fishing),
            ("Woodcutting", Skills.SkillType.WoodCutting), ("Mining", Skills.SkillType.Pickaxes),
        };

        private static void CompanionCard(RectTransform col, CompanionController c)
        {
            var card = KitUI.NewRect(col, "Card_" + c.companionId);
            var img = card.gameObject.AddComponent<Image>();
            KitUI.SetPlate(img, KitUI.Plate(FiresTokens.RadiusSmall, FiresTokens.Well, FiresTokens.Hairline));
            var v = card.gameObject.AddComponent<VerticalLayoutGroup>(); v.padding = new RectOffset(10, 10, 8, 8); v.spacing = 4f;
            v.childControlWidth = true; v.childControlHeight = true; v.childForceExpandHeight = false;
            var top = Cells(card, "Top", 22f); top.GetComponent<HorizontalLayoutGroup>().padding = new RectOffset(0, 0, 0, 0);
            var n = Cell(top, c.GetDisplayName(), 1f, FiresTokens.Text, KitUI.BodySize + 1f, TextAlignmentOptions.MidlineLeft); n.fontStyle = FontStyles.Bold;
            string state = JobBoard.StateOf(c);
            Cell(top, state, 1f, state == "Idle" ? FiresTokens.Ok : FiresTokens.Warn, KitUI.SmallSize, TextAlignmentOptions.MidlineRight);
            var grid = KitUI.NewRect(card, "Skills");
            var g = grid.gameObject.AddComponent<GridLayoutGroup>(); g.cellSize = new Vector2(118f, 26f); g.spacing = new Vector2(10f, 2f);
            var skills = c.GetSkills();
            var shown = CardSkills.ToList();
            var best = skills?.GetAllSkills()?.Where(kv => !CardSkills.Any(s => s.type == kv.Key) && kv.Key != Skills.SkillType.Run && kv.Key != Skills.SkillType.Jump && kv.Key != Skills.SkillType.Swim && kv.Key != Skills.SkillType.Sneak)
                .OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (best != null && best.Value.Value > 0f) shown.Add((best.Value.Key.ToString(), best.Value.Key));
            foreach (var s in shown)
            {
                float lv = skills?.GetSkillLevel(s.type) ?? 0f;
                var cell = KitUI.NewRect(grid, s.label);
                var cl = cell.gameObject.AddComponent<VerticalLayoutGroup>(); cl.childControlWidth = true; cl.childControlHeight = true; cl.childForceExpandHeight = false; cl.spacing = 2f;
                var lab = KitUI.Label(cell, "L", $"{s.label}  <color=#EFE2C4>{lv:0}</color>", KitUI.NoteSize, FiresTokens.TextMuted, TextAlignmentOptions.MidlineLeft);
                lab.gameObject.AddComponent<LayoutElement>().preferredHeight = 14f;
                var bar = KitUI.NewRect(cell, "Bar"); bar.gameObject.AddComponent<LayoutElement>().preferredHeight = 6f;
                var bi = bar.gameObject.AddComponent<Image>(); bi.color = new Color(0.07f, 0.05f, 0.03f, 1f);
                var fill = KitUI.NewRect(bar, "Fill"); fill.anchorMin = Vector2.zero; fill.anchorMax = new Vector2(Mathf.Clamp01(lv / 100f), 1f); fill.offsetMin = fill.offsetMax = Vector2.zero;
                fill.gameObject.AddComponent<Image>().color = FiresTokens.Gold;
            }
            int rows = Mathf.CeilToInt(shown.Count / 2f);
            grid.gameObject.AddComponent<LayoutElement>().preferredHeight = rows * 28f;
            var drag = card.gameObject.AddComponent<CompanionDrag>(); drag.Companion = c;
        }

        // ---- drag a companion onto a job --------------------------------------------------------------------------------
        private sealed class CompanionDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
        {
            public CompanionController Companion;
            private RectTransform _ghost;
            public void OnBeginDrag(PointerEventData e)
            {
                s_dragging = true;
                _ghost = (RectTransform)Instantiate(gameObject, s_win.Root, true).transform;
                Destroy(_ghost.GetComponent<CompanionDrag>());
                foreach (var g in _ghost.GetComponentsInChildren<Graphic>()) g.raycastTarget = false;
                var cg = _ghost.gameObject.AddComponent<CanvasGroup>(); cg.alpha = 0.85f; cg.blocksRaycasts = false;
                _ghost.localRotation = Quaternion.Euler(0f, 0f, -1.2f);
            }
            public void OnDrag(PointerEventData e) { if (_ghost != null) _ghost.position = e.position; }
            public void OnEndDrag(PointerEventData e)
            {
                if (_ghost != null) Destroy(_ghost.gameObject);
                s_dragging = false;
                var hit = e.pointerCurrentRaycast.gameObject;
                var drop = hit != null ? hit.GetComponentInParent<JobDrop>() : null;
                if (drop != null && drop.Job != null) JobBoard.Assign(drop.Job, Companion, drop.Job.IsTrip && drop.Job.Assigned.Count < drop.Job.Count);
            }
        }

        /// <summary>A job row: click selects it for the editor; it glows while a companion is dragged over it.</summary>
        private sealed class JobDrop : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
        {
            public JobBoard.Job Job;
            private Outline _glow;
            public void OnPointerClick(PointerEventData e) { if (e.button == PointerEventData.InputButton.Left) { s_sel = Job; s_dirty = true; } }
            public void OnPointerEnter(PointerEventData e) { if (!s_dragging) return; _glow = _glow ?? gameObject.AddComponent<Outline>(); _glow.effectColor = FiresTokens.GoldBright; _glow.effectDistance = new Vector2(2f, -2f); _glow.enabled = true; }
            public void OnPointerExit(PointerEventData e) { if (_glow != null) _glow.enabled = false; }
        }

        // ---- key, console, periodic refresh -----------------------------------------------------------------------------
        private sealed class Pump : MonoBehaviour
        {
            private void Update()
            {
                try
                {
                    if (Player.m_localPlayer != null && !FiresCore.Input.FiresInputBlock.IsTyping() && FiresCore.Input.Chord.IsDown(s_key.Value)
                        && (s_win == null || !s_win.IsShowing ? !Console.IsVisible() && !global::Menu.IsVisible() && !InventoryGui.IsVisible() : true))
                        Toggle();
                    TickReports();   // 0.2.854
                    if (s_win != null && s_win.IsShowing && !s_dragging && (s_dirty || Time.unscaledTime >= s_nextRefresh))
                    {
                        s_nextRefresh = Time.unscaledTime + 3f;
                        // 0.2.825 (UI efficiency audit C14): the full row rebuild (destroy + recreate every row, 2 LINQ lists) runs on a board
                        // change, a change in who is at base, or every FullRefreshSeconds (the rows' last / next times); the 3 s tick between
                        // only updates the subtitle clock
                        var comps = s_dirty ? null : JobBoard.CompanionsAtBase();
                        if (s_dirty || CompSig(comps) != s_compSig || Time.unscaledTime >= s_nextFull) Refresh();
                        else Subtitle(comps.Count);
                    }
                }
                catch (Exception e) { Debug.LogWarning("[JobBoard] window: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_JobBoardWindow
        {
            private static void Postfix()
            {
                if (UnityEngine.Object.FindObjectOfType<Pump>() == null) { var go = new GameObject("FiresJobBoardWindow"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<Pump>(); }
            }
        }

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class JobBoard_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand("jobboard", "[FiresUnifiedCore] jobboard - opens the Homestead Job Board.", args => Toggle());
            }
        }
    }
}
