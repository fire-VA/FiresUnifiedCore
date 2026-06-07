using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.Help
{
    // The shared help panel: a standalone fullscreen overlay with a grouped sidebar and a scrolling content
    // area. The renderer (canvas, title bar, sidebar, content, the "? Help" HUD button, ESC handling) was
    // lifted from FiresRPGmaker's VerdantAscentHelpPanel; content now comes entirely from HelpRegistry, so
    // any mod contributes sections without touching this code.
    //
    // Consuming mods configure: Title, FontProvider (a TMP font), AdminCheck (gates admin-only sections),
    // and InputBlocker (optional input-suppression hook). All have safe fallbacks so the panel works
    // unconfigured.
    public class HelpPanel : MonoBehaviour
    {
        private const float SidebarWidth = 200f;
        private const int OverlaySortingOrder = 300;
        private const float NavButtonHeight = 28f;
        private const float GroupHeaderHeight = 22f;

        public static string Title = "Help & Reference";
        public static Func<TMPro.TMP_FontAsset> FontProvider;
        public static Func<bool> AdminCheck;

        // Lets a consuming mod route input-blocking through its own system (e.g. to also suppress player
        // movement/camera while the panel is open). When unset, the panel manages the cursor itself so it
        // still works standalone.
        public static Action<bool> InputBlocker;

        private static bool _savedCursorVisible;
        private static CursorLockMode _savedCursorLock;

        private static HelpPanel _instance;
        public static HelpPanel Instance => _instance;

        private GameObject _root;
        private GameObject _helpButton;
        private RectTransform _contentArea;
        private ScrollRect _contentScroll;
        private Transform _sideContent;
        private readonly List<NavEntry> _navEntries = new List<NavEntry>();
        private HelpSection _current;

        private struct NavEntry
        {
            public GameObject Go;
            public HelpSection Section;
        }

        public bool IsVisible => _root != null && _root.activeSelf;

        public static void Initialize()
        {
            if (_instance != null) return;
            if (Hud.instance == null) return;

            var go = new GameObject("FiresCoreHelpPanel", typeof(RectTransform));
            go.transform.SetParent(Hud.instance.transform, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            _instance = go.AddComponent<HelpPanel>();
            _instance.CreateHelpButton();
        }

        public static void Cleanup()
        {
            if (_instance == null) return;
            if (_instance._root != null) Destroy(_instance._root);
            Destroy(_instance.gameObject);
            _instance = null;
        }

        internal static void ApplyFont(TMP_Text tmp)
        {
            try
            {
                var font = FontProvider?.Invoke();
                if (font != null) tmp.font = font;
            }
            catch { }
        }

        private static bool IsAdmin()
        {
            try
            {
                if (AdminCheck != null) return AdminCheck();
                return ZNet.instance != null && ZNet.instance.IsServer() && !ZNet.instance.IsDedicated();
            }
            catch { return false; }
        }

        public void Show()
        {
            if (_root == null) CreatePanel();
            RebuildSidebar();
            _root.SetActive(true);
            _root.transform.SetAsLastSibling();
            SetInputBlocked(true);

            if (Hud.instance != null) Hud.instance.m_rootObject.SetActive(false);
            if (Menu.instance != null) Menu.instance.m_root.gameObject.SetActive(false);

            if (_navEntries.Count > 0)
                NavigateTo(_current ?? _navEntries[0].Section);
        }

        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
            SetInputBlocked(false);
            if (Hud.instance != null) Hud.instance.m_rootObject.SetActive(true);
            if (Menu.instance != null) Menu.instance.m_root.gameObject.SetActive(true);
        }

        private static void SetInputBlocked(bool blocked)
        {
            if (InputBlocker != null)
            {
                try { InputBlocker(blocked); return; }
                catch { }
            }

            if (blocked)
            {
                _savedCursorVisible = Cursor.visible;
                _savedCursorLock = Cursor.lockState;
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
            else
            {
                Cursor.visible = _savedCursorVisible;
                Cursor.lockState = _savedCursorLock;
            }
        }

        public void Toggle()
        {
            if (IsVisible) Hide();
            else Show();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;
        }

        private void OnDestroy()
        {
            if (_root != null) Destroy(_root);
            if (_helpButton != null) Destroy(_helpButton);
            if (_instance == this) _instance = null;
        }

        private void Update()
        {
            bool menuOpen = Menu.IsVisible();
            if (_helpButton != null) _helpButton.SetActive(menuOpen && !IsVisible);

            if (IsVisible && UnityEngine.Input.GetKeyDown(KeyCode.Escape)) Hide();
        }

        private void CreateHelpButton()
        {
            _helpButton = new GameObject("HelpHudButton", typeof(RectTransform));
            _helpButton.transform.SetParent(Hud.instance.transform, false);

            var btnRect = _helpButton.GetComponent<RectTransform>();
            btnRect.anchorMin = new Vector2(0, 1);
            btnRect.anchorMax = new Vector2(0, 1);
            btnRect.pivot = new Vector2(0, 1);
            btnRect.anchoredPosition = new Vector2(12, -12);
            btnRect.sizeDelta = new Vector2(70, 30);

            var btnImg = _helpButton.AddComponent<Image>();
            btnImg.color = new Color(0.12f, 0.10f, 0.08f, 0.85f);

            var btn = _helpButton.AddComponent<Button>();
            btn.targetGraphic = btnImg;
            var colors = btn.colors;
            colors.normalColor = new Color(0.12f, 0.10f, 0.08f, 0.85f);
            colors.highlightedColor = new Color(0.22f, 0.18f, 0.12f, 0.95f);
            colors.pressedColor = new Color(0.35f, 0.25f, 0.15f, 1f);
            btn.colors = colors;
            btn.onClick.AddListener(Show);

            var labelGo = new GameObject("Label", typeof(RectTransform));
            labelGo.transform.SetParent(_helpButton.transform, false);
            var labelRect = labelGo.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            var labelTmp = labelGo.AddComponent<TextMeshProUGUI>();
            labelTmp.text = "? Help";
            labelTmp.fontSize = 13f;
            labelTmp.fontStyle = FontStyles.Bold;
            labelTmp.color = HelpTheme.TextGold;
            labelTmp.alignment = TextAlignmentOptions.Center;
            labelTmp.raycastTarget = false;
            ApplyFont(labelTmp);

            _helpButton.SetActive(false);
        }

        private void CreatePanel()
        {
            _root = new GameObject("HelpPanelRoot", typeof(RectTransform));
            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = OverlaySortingOrder;

            var scaler = _root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            _root.AddComponent<GraphicRaycaster>();

            var rootRect = _root.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            var backdrop = _root.AddComponent<Image>();
            backdrop.color = HelpTheme.Backdrop;
            var backdropBtn = _root.AddComponent<Button>();
            backdropBtn.targetGraphic = backdrop;
            var bc = backdropBtn.colors;
            bc.normalColor = bc.highlightedColor = bc.pressedColor = HelpTheme.Backdrop;
            backdropBtn.colors = bc;
            backdropBtn.onClick.AddListener(Hide);

            var dialogGo = new GameObject("Dialog", typeof(RectTransform));
            dialogGo.transform.SetParent(_root.transform, false);
            var dialogRect = dialogGo.GetComponent<RectTransform>();
            dialogRect.anchorMin = new Vector2(0.08f, 0.06f);
            dialogRect.anchorMax = new Vector2(0.92f, 0.94f);
            dialogRect.offsetMin = Vector2.zero;
            dialogRect.offsetMax = Vector2.zero;
            var dialogBg = dialogGo.AddComponent<Image>();
            dialogBg.color = HelpTheme.PanelBg;
            dialogGo.AddComponent<Button>().targetGraphic = dialogBg;

            CreateTitleBar(dialogGo.transform);
            CreateSidebarContainer(dialogGo.transform);
            CreateContentArea(dialogGo.transform);
        }

        private void CreateTitleBar(Transform dialog)
        {
            var titleGo = new GameObject("TitleBar");
            titleGo.transform.SetParent(dialog, false);
            var titleRect = titleGo.AddComponent<RectTransform>();
            titleRect.anchorMin = new Vector2(0, 1);
            titleRect.anchorMax = new Vector2(1, 1);
            titleRect.pivot = new Vector2(0.5f, 1);
            titleRect.anchoredPosition = Vector2.zero;
            titleRect.sizeDelta = new Vector2(0, 36);
            titleGo.AddComponent<Image>().color = HelpTheme.SidebarBg;

            var titleTextGo = new GameObject("Text");
            titleTextGo.transform.SetParent(titleGo.transform, false);
            var ttRect = titleTextGo.AddComponent<RectTransform>();
            ttRect.anchorMin = Vector2.zero;
            ttRect.anchorMax = Vector2.one;
            ttRect.offsetMin = new Vector2(16, 0);
            ttRect.offsetMax = new Vector2(-40, 0);
            var titleTmp = titleTextGo.AddComponent<TextMeshProUGUI>();
            titleTmp.text = Title;
            titleTmp.fontSize = 14f;
            titleTmp.fontStyle = FontStyles.Bold;
            titleTmp.color = HelpTheme.HeaderColor;
            titleTmp.alignment = TextAlignmentOptions.MidlineLeft;
            titleTmp.raycastTarget = false;
            ApplyFont(titleTmp);

            var closeBtnGo = new GameObject("Close");
            closeBtnGo.transform.SetParent(titleGo.transform, false);
            var closeRect = closeBtnGo.AddComponent<RectTransform>();
            closeRect.anchorMin = new Vector2(1, 0);
            closeRect.anchorMax = new Vector2(1, 1);
            closeRect.pivot = new Vector2(1, 0.5f);
            closeRect.anchoredPosition = new Vector2(-4, 0);
            closeRect.sizeDelta = new Vector2(30, 0);
            var closeImg = closeBtnGo.AddComponent<Image>();
            closeImg.color = new Color(0.5f, 0.15f, 0.1f, 0.8f);
            var closeBtn = closeBtnGo.AddComponent<Button>();
            closeBtn.targetGraphic = closeImg;
            closeBtn.onClick.AddListener(Hide);

            var closeTxt = new GameObject("X");
            closeTxt.transform.SetParent(closeBtnGo.transform, false);
            var cxr = closeTxt.AddComponent<RectTransform>();
            cxr.anchorMin = Vector2.zero;
            cxr.anchorMax = Vector2.one;
            cxr.offsetMin = cxr.offsetMax = Vector2.zero;
            var cx = closeTxt.AddComponent<TextMeshProUGUI>();
            cx.text = "X";
            cx.fontSize = 12f;
            cx.fontStyle = FontStyles.Bold;
            cx.color = HelpTheme.TextGold;
            cx.alignment = TextAlignmentOptions.Center;
            cx.raycastTarget = false;
            ApplyFont(cx);
        }

        private void CreateSidebarContainer(Transform dialog)
        {
            var sidebarGo = new GameObject("Sidebar");
            sidebarGo.transform.SetParent(dialog, false);
            var sidebarRect = sidebarGo.AddComponent<RectTransform>();
            sidebarRect.anchorMin = new Vector2(0, 0);
            sidebarRect.anchorMax = new Vector2(0, 1);
            sidebarRect.pivot = new Vector2(0, 0.5f);
            sidebarRect.sizeDelta = new Vector2(SidebarWidth, 0);
            sidebarRect.offsetMin = new Vector2(0, 0);
            sidebarRect.offsetMax = new Vector2(SidebarWidth, -36);
            sidebarGo.AddComponent<Image>().color = HelpTheme.SidebarBg;

            var sideScrollGo = new GameObject("SideScroll");
            sideScrollGo.transform.SetParent(sidebarGo.transform, false);
            var ssr = sideScrollGo.AddComponent<RectTransform>();
            ssr.anchorMin = Vector2.zero;
            ssr.anchorMax = Vector2.one;
            ssr.offsetMin = ssr.offsetMax = Vector2.zero;

            var sideVp = new GameObject("Viewport");
            sideVp.transform.SetParent(sideScrollGo.transform, false);
            var svr = sideVp.AddComponent<RectTransform>();
            svr.anchorMin = Vector2.zero;
            svr.anchorMax = Vector2.one;
            svr.offsetMin = svr.offsetMax = Vector2.zero;
            sideVp.AddComponent<RectMask2D>();
            sideVp.AddComponent<Image>().color = Color.clear;

            var sideContentGo = new GameObject("Content");
            sideContentGo.transform.SetParent(sideVp.transform, false);
            var scr = sideContentGo.AddComponent<RectTransform>();
            scr.anchorMin = new Vector2(0, 1);
            scr.anchorMax = new Vector2(1, 1);
            scr.pivot = new Vector2(0, 1);
            scr.anchoredPosition = Vector2.zero;
            scr.sizeDelta = Vector2.zero;
            var svlg = sideContentGo.AddComponent<VerticalLayoutGroup>();
            svlg.padding = new RectOffset(4, 4, 8, 8);
            svlg.spacing = 2;
            svlg.childControlWidth = true;
            svlg.childControlHeight = true;
            svlg.childForceExpandWidth = true;
            svlg.childForceExpandHeight = false;
            sideContentGo.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _sideContent = sideContentGo.transform;

            var sideScroll = sideScrollGo.AddComponent<ScrollRect>();
            sideScroll.horizontal = false;
            sideScroll.vertical = true;
            sideScroll.viewport = svr;
            sideScroll.content = scr;
            sideScroll.movementType = ScrollRect.MovementType.Clamped;
            sideScroll.scrollSensitivity = 80f;
        }

        private void RebuildSidebar()
        {
            if (_sideContent == null) return;
            for (int i = _sideContent.childCount - 1; i >= 0; i--)
                Destroy(_sideContent.GetChild(i).gameObject);
            _navEntries.Clear();

            bool isAdmin = IsAdmin();
            bool showModHeaders = HelpRegistry.ModCount > 1;
            var sections = HelpRegistry.GetVisibleSections(isAdmin);

            string lastMod = null;
            string lastGroup = null;
            foreach (var section in sections)
            {
                if (showModHeaders && section.ModId != lastMod)
                {
                    AddSidebarHeader(section.ModId);
                    lastMod = section.ModId;
                    lastGroup = null;
                }
                if (!string.IsNullOrEmpty(section.Group) && section.Group != lastGroup)
                {
                    AddSidebarHeader("── " + section.Group + " ──");
                    lastGroup = section.Group;
                }
                AddSidebarNavButton(section);
            }

            if (_current != null && !sections.Contains(_current))
                _current = sections.Count > 0 ? sections[0] : null;
        }

        private void AddSidebarHeader(string text)
        {
            var go = new GameObject("NavHeader");
            go.transform.SetParent(_sideContent, false);
            go.AddComponent<LayoutElement>().minHeight = GroupHeaderHeight;
            go.AddComponent<Image>().color = Color.clear;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var tr = textGo.AddComponent<RectTransform>();
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(10, 0);
            tr.offsetMax = new Vector2(-4, 0);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = 9.5f;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = HelpTheme.TextMuted;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.raycastTarget = false;
            ApplyFont(tmp);
        }

        private void AddSidebarNavButton(HelpSection section)
        {
            var go = new GameObject("Nav");
            go.transform.SetParent(_sideContent, false);
            go.AddComponent<LayoutElement>().minHeight = NavButtonHeight;
            var bg = go.AddComponent<Image>();
            bg.color = HelpTheme.NavNormal;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var tr = textGo.AddComponent<RectTransform>();
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = new Vector2(10, 0);
            tr.offsetMax = new Vector2(-4, 0);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = section.Title;
            tmp.fontSize = 11f;
            tmp.color = HelpTheme.TextLight;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.raycastTarget = false;
            ApplyFont(tmp);

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = bg;
            var nc = btn.colors;
            nc.normalColor = HelpTheme.NavNormal;
            nc.highlightedColor = HelpTheme.NavHover;
            nc.pressedColor = HelpTheme.NavActive;
            nc.selectedColor = HelpTheme.NavNormal;
            btn.colors = nc;
            var captured = section;
            btn.onClick.AddListener(() => NavigateTo(captured));

            _navEntries.Add(new NavEntry { Go = go, Section = section });
        }

        private void NavigateTo(HelpSection section)
        {
            _current = section;
            UpdateNavHighlight();

            for (int i = _contentArea.childCount - 1; i >= 0; i--)
                Destroy(_contentArea.GetChild(i).gameObject);

            try { section.Build(new HelpContentWriter(_contentArea)); }
            catch (Exception ex) { Logging.FiresLogger.LogWarning($"[HelpPanel] section '{section.Title}' build threw: {ex.Message}"); }

            if (_contentScroll != null) _contentScroll.verticalNormalizedPosition = 1f;
        }

        private void UpdateNavHighlight()
        {
            foreach (var entry in _navEntries)
            {
                bool active = entry.Section == _current;
                var bg = entry.Go.GetComponent<Image>();
                if (bg != null) bg.color = active ? HelpTheme.NavActive : HelpTheme.NavNormal;
                var tmp = entry.Go.GetComponentInChildren<TextMeshProUGUI>();
                if (tmp != null) tmp.color = active ? HelpTheme.TextGold : HelpTheme.TextLight;
            }
        }

        private void CreateContentArea(Transform dialog)
        {
            var contentGo = new GameObject("ContentArea");
            contentGo.transform.SetParent(dialog, false);
            var car = contentGo.AddComponent<RectTransform>();
            car.anchorMin = Vector2.zero;
            car.anchorMax = Vector2.one;
            car.offsetMin = new Vector2(SidebarWidth, 0);
            car.offsetMax = new Vector2(0, -36);
            contentGo.AddComponent<Image>().color = HelpTheme.ContentBg;

            var scrollGo = new GameObject("Scroll");
            scrollGo.transform.SetParent(contentGo.transform, false);
            var sgr = scrollGo.AddComponent<RectTransform>();
            sgr.anchorMin = Vector2.zero;
            sgr.anchorMax = Vector2.one;
            sgr.offsetMin = new Vector2(8, 8);
            sgr.offsetMax = new Vector2(-8, -8);

            var vpGo = new GameObject("Viewport");
            vpGo.transform.SetParent(scrollGo.transform, false);
            var vpr = vpGo.AddComponent<RectTransform>();
            vpr.anchorMin = Vector2.zero;
            vpr.anchorMax = Vector2.one;
            vpr.offsetMin = Vector2.zero;
            vpr.offsetMax = new Vector2(-10, 0);
            vpGo.AddComponent<RectMask2D>();
            vpGo.AddComponent<Image>().color = Color.clear;

            var contentInner = new GameObject("Content");
            contentInner.transform.SetParent(vpGo.transform, false);
            _contentArea = contentInner.AddComponent<RectTransform>();
            _contentArea.anchorMin = new Vector2(0, 1);
            _contentArea.anchorMax = new Vector2(1, 1);
            _contentArea.pivot = new Vector2(0, 1);
            _contentArea.anchoredPosition = Vector2.zero;
            _contentArea.sizeDelta = Vector2.zero;
            var vlg = contentInner.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(8, 8, 8, 16);
            vlg.spacing = 6;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            contentInner.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _contentScroll = scrollGo.AddComponent<ScrollRect>();
            _contentScroll.horizontal = false;
            _contentScroll.vertical = true;
            _contentScroll.viewport = vpr;
            _contentScroll.content = _contentArea;
            _contentScroll.movementType = ScrollRect.MovementType.Clamped;
            _contentScroll.scrollSensitivity = 120f;

            var sbGo = new GameObject("Scrollbar");
            sbGo.transform.SetParent(scrollGo.transform, false);
            var sbr = sbGo.AddComponent<RectTransform>();
            sbr.anchorMin = new Vector2(1, 0);
            sbr.anchorMax = new Vector2(1, 1);
            sbr.pivot = new Vector2(1, 0.5f);
            sbr.anchoredPosition = Vector2.zero;
            sbr.sizeDelta = new Vector2(8, 0);
            sbGo.AddComponent<Image>().color = HelpTheme.ScrollbarTrack;

            var handleGo = new GameObject("Handle");
            handleGo.transform.SetParent(sbGo.transform, false);
            var hr = handleGo.AddComponent<RectTransform>();
            hr.anchorMin = Vector2.zero;
            hr.anchorMax = Vector2.one;
            hr.offsetMin = new Vector2(1, 0);
            hr.offsetMax = new Vector2(-1, 0);
            var handleImg = handleGo.AddComponent<Image>();
            handleImg.color = HelpTheme.ScrollbarHandle;

            var sb = sbGo.AddComponent<Scrollbar>();
            sb.direction = Scrollbar.Direction.BottomToTop;
            sb.handleRect = hr;
            sb.targetGraphic = handleImg;
            _contentScroll.verticalScrollbar = sb;
            _contentScroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        }
    }
}
