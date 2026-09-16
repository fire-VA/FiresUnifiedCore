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
        private bool _menuWasOpen;   // was the vanilla pause menu open when Help was shown? only then restore it
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
            // Remember whether the pause menu was open BEFORE we hide it, so Hide() only restores it when Help
            // was actually launched from the menu (Help ↔ menu toggle). Otherwise Hide() would spuriously OPEN
            // the pause menu.
            _menuWasOpen = Menu.instance != null && Menu.instance.m_root != null && Menu.instance.m_root.gameObject.activeSelf;
            _root.SetActive(true);
            _root.transform.SetAsLastSibling();
            SetInputBlocked(true);

            if (Hud.instance != null) Hud.instance.m_rootObject.SetActive(false);
            if (Menu.instance != null && Menu.instance.m_root != null) Menu.instance.m_root.gameObject.SetActive(false);

            if (_navEntries.Count > 0)
                NavigateTo(_current ?? _navEntries[0].Section);
        }

        public void Hide()
        {
            // GUARD: do NOTHING if Help isn't actually open. GUIManager.CloseAllPanels() calls Hide() on EVERY
            // panel close (including closing a dialogue via the Escape hook). Without this guard the
            // unconditional Menu.m_root.SetActive(true) below OPENED the vanilla pause menu on every dialogue
            // close — bypassing Menu.Show, so the Escape hook could never catch it. THIS was the "Escape opens
            // the esc menu" bug.
            if (_root == null || !_root.activeSelf) return;

            _root.SetActive(false);
            SetInputBlocked(false);
            if (Hud.instance != null) Hud.instance.m_rootObject.SetActive(true);
            // Restore the pause menu ONLY if it was open when Help was shown (Help was opened from it).
            if (_menuWasOpen && Menu.instance != null && Menu.instance.m_root != null)
                Menu.instance.m_root.gameObject.SetActive(true);
            _menuWasOpen = false;
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
            var backdropColors = backdropBtn.colors;
            backdropColors.normalColor = backdropColors.highlightedColor = backdropColors.pressedColor = HelpTheme.Backdrop;
            backdropBtn.colors = backdropColors;
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
            var closeTextRect = closeTxt.AddComponent<RectTransform>();
            closeTextRect.anchorMin = Vector2.zero;
            closeTextRect.anchorMax = Vector2.one;
            closeTextRect.offsetMin = closeTextRect.offsetMax = Vector2.zero;
            var label = closeTxt.AddComponent<TextMeshProUGUI>();
            label.text = "X";
            label.fontSize = 12f;
            label.fontStyle = FontStyles.Bold;
            label.color = HelpTheme.TextGold;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
            ApplyFont(label);
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
            var sideScrollRect = sideScrollGo.AddComponent<RectTransform>();
            sideScrollRect.anchorMin = Vector2.zero;
            sideScrollRect.anchorMax = Vector2.one;
            sideScrollRect.offsetMin = sideScrollRect.offsetMax = Vector2.zero;

            var sideVp = new GameObject("Viewport");
            sideVp.transform.SetParent(sideScrollGo.transform, false);
            var sideViewportRect = sideVp.AddComponent<RectTransform>();
            sideViewportRect.anchorMin = Vector2.zero;
            sideViewportRect.anchorMax = Vector2.one;
            sideViewportRect.offsetMin = sideViewportRect.offsetMax = Vector2.zero;
            sideVp.AddComponent<RectMask2D>();
            sideVp.AddComponent<Image>().color = Color.clear;

            var sideContentGo = new GameObject("Content");
            sideContentGo.transform.SetParent(sideVp.transform, false);
            var sideContentRect = sideContentGo.AddComponent<RectTransform>();
            sideContentRect.anchorMin = new Vector2(0, 1);
            sideContentRect.anchorMax = new Vector2(1, 1);
            sideContentRect.pivot = new Vector2(0, 1);
            sideContentRect.anchoredPosition = Vector2.zero;
            sideContentRect.sizeDelta = Vector2.zero;
            var sidebarLayout = sideContentGo.AddComponent<VerticalLayoutGroup>();
            sidebarLayout.padding = new RectOffset(4, 4, 8, 8);
            sidebarLayout.spacing = 2;
            sidebarLayout.childControlWidth = true;
            sidebarLayout.childControlHeight = true;
            sidebarLayout.childForceExpandWidth = true;
            sidebarLayout.childForceExpandHeight = false;
            sideContentGo.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _sideContent = sideContentGo.transform;

            var sideScroll = sideScrollGo.AddComponent<ScrollRect>();
            sideScroll.horizontal = false;
            sideScroll.vertical = true;
            sideScroll.viewport = sideViewportRect;
            sideScroll.content = sideContentRect;
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
            var rect = textGo.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(10, 0);
            rect.offsetMax = new Vector2(-4, 0);
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
            var image = go.AddComponent<Image>();
            image.color = HelpTheme.NavNormal;

            var textGo = new GameObject("Text");
            textGo.transform.SetParent(go.transform, false);
            var rect = textGo.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(10, 0);
            rect.offsetMax = new Vector2(-4, 0);
            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = section.Title;
            tmp.fontSize = 11f;
            tmp.color = HelpTheme.TextLight;
            tmp.alignment = TextAlignmentOptions.MidlineLeft;
            tmp.raycastTarget = false;
            ApplyFont(tmp);

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = image;
            var colors = btn.colors;
            colors.normalColor = HelpTheme.NavNormal;
            colors.highlightedColor = HelpTheme.NavHover;
            colors.pressedColor = HelpTheme.NavActive;
            colors.selectedColor = HelpTheme.NavNormal;
            btn.colors = colors;
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

            try { section.Build(new HelpContentWriter(_contentArea, section.ModId + "|" + section.Title)); }
            catch (Exception ex) { Logging.FiresLogger.LogWarning($"[HelpPanel] section '{section.Title}' build threw: {ex.Message}"); }

            if (_contentScroll != null) _contentScroll.verticalNormalizedPosition = 1f;
        }

        private void UpdateNavHighlight()
        {
            foreach (var entry in _navEntries)
            {
                bool active = entry.Section == _current;
                var image = entry.Go.GetComponent<Image>();
                if (image != null) image.color = active ? HelpTheme.NavActive : HelpTheme.NavNormal;
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
            var scrollAreaRect = scrollGo.AddComponent<RectTransform>();
            scrollAreaRect.anchorMin = Vector2.zero;
            scrollAreaRect.anchorMax = Vector2.one;
            scrollAreaRect.offsetMin = new Vector2(8, 8);
            scrollAreaRect.offsetMax = new Vector2(-8, -8);

            var viewportGo = new GameObject("Viewport");
            viewportGo.transform.SetParent(scrollGo.transform, false);
            var viewportRect = viewportGo.AddComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = Vector2.zero;
            viewportRect.offsetMax = new Vector2(-10, 0);
            viewportGo.AddComponent<RectMask2D>();
            viewportGo.AddComponent<Image>().color = Color.clear;

            var contentInner = new GameObject("Content");
            contentInner.transform.SetParent(viewportGo.transform, false);
            _contentArea = contentInner.AddComponent<RectTransform>();
            _contentArea.anchorMin = new Vector2(0, 1);
            _contentArea.anchorMax = new Vector2(1, 1);
            _contentArea.pivot = new Vector2(0, 1);
            _contentArea.anchoredPosition = Vector2.zero;
            _contentArea.sizeDelta = Vector2.zero;
            var verticalLayout = contentInner.AddComponent<VerticalLayoutGroup>();
            verticalLayout.padding = new RectOffset(8, 8, 8, 16);
            verticalLayout.spacing = 6;
            verticalLayout.childControlWidth = true;
            verticalLayout.childControlHeight = true;
            verticalLayout.childForceExpandWidth = true;
            verticalLayout.childForceExpandHeight = false;
            contentInner.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _contentScroll = scrollGo.AddComponent<ScrollRect>();
            _contentScroll.horizontal = false;
            _contentScroll.vertical = true;
            _contentScroll.viewport = viewportRect;
            _contentScroll.content = _contentArea;
            _contentScroll.movementType = ScrollRect.MovementType.Clamped;
            _contentScroll.scrollSensitivity = 120f;

            var scrollbarGo = new GameObject("Scrollbar");
            scrollbarGo.transform.SetParent(scrollGo.transform, false);
            var scrollbarRect = scrollbarGo.AddComponent<RectTransform>();
            scrollbarRect.anchorMin = new Vector2(1, 0);
            scrollbarRect.anchorMax = new Vector2(1, 1);
            scrollbarRect.pivot = new Vector2(1, 0.5f);
            scrollbarRect.anchoredPosition = Vector2.zero;
            scrollbarRect.sizeDelta = new Vector2(8, 0);
            scrollbarGo.AddComponent<Image>().color = HelpTheme.ScrollbarTrack;

            var handleGo = new GameObject("Handle");
            handleGo.transform.SetParent(scrollbarGo.transform, false);
            var rect = handleGo.AddComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(1, 0);
            rect.offsetMax = new Vector2(-1, 0);
            var handleImg = handleGo.AddComponent<Image>();
            handleImg.color = HelpTheme.ScrollbarHandle;

            var scrollbar = scrollbarGo.AddComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;
            scrollbar.handleRect = rect;
            scrollbar.targetGraphic = handleImg;
            _contentScroll.verticalScrollbar = scrollbar;
            _contentScroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
        }
    }
}
