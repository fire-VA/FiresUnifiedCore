using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Bridge;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI.GroupHud
{
    /// <summary>
    /// Shared, mod-agnostic group HUD — the below-minimap panel listing tracked members with name,
    /// distance/status and health/stamina/eitr bars (plus optional status-effect pips). Rows come
    /// from <see cref="GroupHudBridge"/> providers, so any mod can populate it. Client-only:
    /// initialization waits for <see cref="Hud.instance"/>, which never exists on a dedicated server.
    ///
    /// Ported from the original FiresCompanions companion HUD and generalized: the companion-specific
    /// scanning/respawn/stat plumbing now lives behind the provider contract, leaving this purely a
    /// renderer. Draggable in Esc-menu reposition mode; position persists per-client in PlayerPrefs.
    /// </summary>
    public sealed class GroupHudController : MonoBehaviour
    {
        #region Constants

        private const float PANEL_WIDTH = 180f;
        private const float MEMBER_HEIGHT = 50f;
        private const float BAR_HEIGHT = 6f;
        private const float BAR_SPACING = 2f;
        private const float STATUS_EFFECT_ROW_HEIGHT = 16f;
        private const float STATUS_EFFECT_ICON_SIZE = 14f;
        private const int MAX_EFFECTS_PER_ROW = 5;
        private const float COLUMN_INNER_WIDTH = PANEL_WIDTH - 8f;

        private const string PREF_POS_X = "GroupHud_PosX";
        private const string PREF_POS_Y = "GroupHud_PosY";

        private static readonly Color HealthBarColor = new Color(0.8f, 0.2f, 0.2f, 1f);
        private static readonly Color StaminaBarColor = new Color(0.9f, 0.8f, 0.2f, 1f);
        private static readonly Color EitrBarColor = new Color(0.3f, 0.5f, 0.9f, 1f);
        private static readonly Color BarBackgroundColor = new Color(0.1f, 0.08f, 0.06f, 0.8f);
        private static readonly Color PanelBackgroundColor = new Color(0.02f, 0.02f, 0.04f, 0.6f);
        private static readonly Color PanelDraggingColor = new Color(0.1f, 0.1f, 0.15f, 0.85f);
        private static readonly Color NameColor = new Color(1f, 0.85f, 0.5f, 1f);
        private static readonly Color DistanceColor = new Color(0.7f, 0.7f, 0.7f, 1f);
        private static readonly Color DeadNameColor = new Color(0.5f, 0.4f, 0.35f, 1f);
        private static readonly Color DeadBarColor = new Color(0.2f, 0.2f, 0.2f, 0.5f);
        private static readonly Color StatusTextColor = new Color(1f, 0.5f, 0.3f, 1f);

        private const float UPDATE_INTERVAL = 0.1f;

        private static int VisibleRows => Mathf.Clamp(GroupHudConfig.MaxRowsPerColumn?.Value ?? 8, 1, 20);
        private static int VisibleColumns => Mathf.Clamp(GroupHudConfig.Columns?.Value ?? 1, 1, 2);

        #endregion

        #region Fields

        private static GroupHudController _instance;
        public static GroupHudController Instance => _instance;

        private GameObject _root;
        private RectTransform _rootRect;
        private ScrollRect _scrollRect;
        private GameObject _column1Container;
        private GameObject _column2Container;
        private Image _backgroundImage;

        // One row per member Id, plus the current display order.
        private readonly Dictionary<string, MemberUI> _rows = new Dictionary<string, MemberUI>(StringComparer.Ordinal);
        private readonly List<string> _orderedIds = new List<string>();

        private float _lastUpdate;
        private Player _localPlayer;

        private bool _isDragging;
        private Vector2 _dragOffset;
        private bool _isRepositionMode;

        #endregion

        #region Member UI

        private sealed class MemberUI
        {
            public GameObject Root;
            public TMP_Text NameText;
            public TMP_Text StatusText;   // right-aligned: distance or status (respawn/dead/offline)
            public Image HealthBarFill;
            public Image StaminaBarFill;
            public Image EitrBarFill;
            public GameObject EitrBarRoot;
            public Transform StatusEffectContainer;
            public readonly List<StatusIconUI> StatusIcons = new List<StatusIconUI>();
        }

        private sealed class StatusIconUI
        {
            public GameObject Root;
            public Image Icon;
            public TMP_Text TimerText;
        }

        #endregion

        #region Public API

        /// <summary>Creates the HUD once <see cref="Hud.instance"/> is ready. Safe to call repeatedly / early.</summary>
        public static void Initialize()
        {
            if (_instance != null) return;

            if (Hud.instance == null)
            {
                var go = new GameObject("GroupHudInitializer");
                go.AddComponent<DeferredInitializer>().StartCoroutine(DeferredInitializer.WaitForHudAndInit(go));
                return;
            }

            CreateInstance();
        }

        private static void CreateInstance()
        {
            if (_instance != null || Hud.instance == null) return;
            var go = new GameObject("GroupHudController");
            go.transform.SetParent(Hud.instance.transform, false);
            _instance = go.AddComponent<GroupHudController>();
            Debug.Log("[GroupHud] HUD created.");
        }

        public static void Cleanup()
        {
            if (_instance != null)
            {
                Destroy(_instance.gameObject);
                _instance = null;
            }
        }

        private sealed class DeferredInitializer : MonoBehaviour
        {
            public static IEnumerator WaitForHudAndInit(GameObject host)
            {
                float elapsed = 0f;
                while (Hud.instance == null && elapsed < 30f)
                {
                    yield return new WaitForSeconds(0.5f);
                    elapsed += 0.5f;
                }
                if (Hud.instance != null) CreateInstance();
                if (host != null) Destroy(host);
            }
        }

        #endregion

        #region Lifecycle

        private void Awake()
        {
            if (_instance != null && _instance != this) { Destroy(gameObject); return; }
            _instance = this;

            CreateUI();
            SubscribeConfig(true);
        }

        private void OnDestroy()
        {
            SubscribeConfig(false);
            if (_instance == this) _instance = null;
        }

        private void SubscribeConfig(bool subscribe)
        {
            // SettingChanged lives on ConfigFile (fires for every entry), so subscribe once and
            // filter to ours in the handler — a live edit via the config file / Configuration
            // Manager then re-applies layout + default position immediately.
            var file = GroupHudConfig.MaxRowsPerColumn?.ConfigFile;
            if (file == null) return;
            if (subscribe) file.SettingChanged += OnConfigChanged;
            else file.SettingChanged -= OnConfigChanged;
        }

        private void OnConfigChanged(object sender, BepInEx.Configuration.SettingChangedEventArgs e)
        {
            var c = e?.ChangedSetting;
            if (c != GroupHudConfig.MaxRowsPerColumn && c != GroupHudConfig.Columns
                && c != GroupHudConfig.DefaultPosX && c != GroupHudConfig.DefaultPosY) return;
            try
            {
                RebuildLayout();
                if (_rootRect != null && !(PlayerPrefs.HasKey(PREF_POS_X) && PlayerPrefs.HasKey(PREF_POS_Y)))
                    _rootRect.anchoredPosition = GroupHudConfig.DefaultPosition;
            }
            catch (Exception ex) { Debug.LogWarning($"[GroupHud] config refresh failed: {ex.Message}"); }
        }

        private void Update()
        {
            if (_localPlayer == null || !_localPlayer) _localPlayer = Player.m_localPlayer;

            _isRepositionMode = Menu.IsVisible();
            if (_backgroundImage != null && !_isDragging)
                _backgroundImage.raycastTarget = _isRepositionMode;

            // Refresh members ALWAYS (even while hidden). Visibility is gated on having members, and
            // members are only collected here — refreshing only when visible would mean the panel
            // could never appear in the first place.
            if (Time.time - _lastUpdate >= UPDATE_INTERVAL)
            {
                _lastUpdate = Time.time;
                RefreshMembers();
            }

            bool show = ShouldShowHud();
            if (_root != null)
            {
                _root.SetActive(show);
                if (_backgroundImage != null)
                    _backgroundImage.color = _isDragging ? PanelDraggingColor : PanelBackgroundColor;
            }
        }

        #endregion

        #region Visibility

        private bool ShouldShowHud()
        {
            if (GroupHudConfig.ShowGroupHud != null && !GroupHudConfig.ShowGroupHud.Value) return false;
            if (_localPlayer == null) return false;
            if (InventoryGui.IsVisible()) return false;
            if (Minimap.instance != null && Minimap.instance.m_mode == Minimap.MapMode.Large) return false;
            if (Console.IsVisible()) return false;
            if (TextInput.IsVisible()) return false;
            if (Chat.instance != null && Chat.instance.HasFocus()) return false;

            // Optional frontend gate (e.g. a mod's own full-screen panels).
            if (!_isRepositionMode && GroupHudBridge.IsBlockingUiOpen != null)
            {
                try { if (GroupHudBridge.IsBlockingUiOpen()) return false; } catch { }
            }

            // Nothing to show (and not parking the panel via the menu) → stay hidden.
            if (_orderedIds.Count == 0 && !_isRepositionMode) return false;
            return true;
        }

        #endregion

        #region Member refresh

        private void RefreshMembers()
        {
            // Pull + order: SortKey asc, then name.
            var members = GroupHudBridge.CollectMembers();
            members.Sort((a, b) =>
            {
                int c = a.SortKey.CompareTo(b.SortKey);
                return c != 0 ? c : string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.Ordinal);
            });

            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool orderChanged = members.Count != _orderedIds.Count;
            _orderedIds.Clear();

            for (int i = 0; i < members.Count; i++)
            {
                var m = members[i];
                seen.Add(m.Id);
                _orderedIds.Add(m.Id);

                if (!_rows.TryGetValue(m.Id, out var ui))
                {
                    ui = CreateMemberUI(m.Id);
                    _rows[m.Id] = ui;
                    orderChanged = true;
                }
                UpdateRow(m, ui);
            }

            // Drop rows whose members vanished.
            if (_rows.Count > seen.Count)
            {
                var stale = _rows.Keys.Where(k => !seen.Contains(k)).ToList();
                foreach (var key in stale)
                {
                    if (_rows.TryGetValue(key, out var ui) && ui.Root != null) Destroy(ui.Root);
                    _rows.Remove(key);
                    orderChanged = true;
                }
            }

            if (orderChanged)
            {
                Debug.Log($"[GroupHud] tracking {_orderedIds.Count} member(s).");
                RebuildLayout();
            }
        }

        private void UpdateRow(GroupHudMember m, MemberUI ui)
        {
            if (ui.Root == null) return;

            Color nameCol = m.NameColor.a > 0f ? m.NameColor : (m.IsDead ? DeadNameColor : NameColor);
            ui.NameText.text = m.Name ?? "";
            ui.NameText.color = nameCol;

            // Right-side text: explicit status overrides distance.
            if (!string.IsNullOrEmpty(m.StatusText))
            {
                ui.StatusText.text = m.StatusText;
                ui.StatusText.color = m.IsDead ? StatusTextColor : DistanceColor;
            }
            else if (m.Distance >= 0f)
            {
                ui.StatusText.text = $"({m.Distance:F0}m)";
                ui.StatusText.color = DistanceColor;
            }
            else
            {
                ui.StatusText.text = "";
            }

            if (m.IsDead)
            {
                SetBarFill(ui.HealthBarFill, 0f);
                SetBarFill(ui.StaminaBarFill, 0f);
                ui.HealthBarFill.color = DeadBarColor;
                ui.StaminaBarFill.color = DeadBarColor;
                ui.EitrBarRoot.SetActive(false);
            }
            else
            {
                ui.HealthBarFill.color = HealthBarColor;
                ui.StaminaBarFill.color = StaminaBarColor;
                SetBarFill(ui.HealthBarFill, m.MaxHealth > 0f ? Mathf.Clamp01(m.Health / m.MaxHealth) : 0f);
                SetBarFill(ui.StaminaBarFill, m.MaxStamina > 0f ? Mathf.Clamp01(m.Stamina / m.MaxStamina) : 0f);

                if (m.MaxEitr > 0f)
                {
                    ui.EitrBarRoot.SetActive(true);
                    ui.EitrBarFill.color = EitrBarColor;
                    SetBarFill(ui.EitrBarFill, Mathf.Clamp01(m.Eitr / m.MaxEitr));
                }
                else ui.EitrBarRoot.SetActive(false);
            }

            UpdateStatusIcons(m, ui);
        }

        private void UpdateStatusIcons(GroupHudMember m, MemberUI ui)
        {
            var icons = m.StatusIcons;
            int count = icons?.Count ?? 0;

            for (int i = 0; i < count; i++)
            {
                if (i >= ui.StatusIcons.Count) ui.StatusIcons.Add(CreateStatusIcon(ui.StatusEffectContainer));
                var iconUI = ui.StatusIcons[i];
                var data = icons[i];

                iconUI.Root.SetActive(true);
                if (data.Icon != null) { iconUI.Icon.sprite = data.Icon; iconUI.Icon.color = Color.white; }
                else { iconUI.Icon.sprite = null; iconUI.Icon.color = data.Color; }

                if (data.RemainingSeconds > 0f) { iconUI.TimerText.text = $"{data.RemainingSeconds:F0}"; iconUI.TimerText.gameObject.SetActive(true); }
                else iconUI.TimerText.gameObject.SetActive(false);
            }

            for (int i = count; i < ui.StatusIcons.Count; i++)
                if (ui.StatusIcons[i].Root != null) ui.StatusIcons[i].Root.SetActive(false);
        }

        #endregion

        #region UI creation

        private void CreateUI()
        {
            _root = new GameObject("GroupHudPanel");
            _root.transform.SetParent(transform, false);

            _rootRect = _root.AddComponent<RectTransform>();
            _rootRect.anchorMin = new Vector2(1f, 1f);
            _rootRect.anchorMax = new Vector2(1f, 1f);
            _rootRect.pivot = new Vector2(1f, 1f);
            _rootRect.sizeDelta = new Vector2(PANEL_WIDTH, 30f);

            _backgroundImage = _root.AddComponent<Image>();
            _backgroundImage.color = PanelBackgroundColor;
            _backgroundImage.raycastTarget = false;

            var viewportGO = new GameObject("Viewport");
            viewportGO.transform.SetParent(_root.transform, false);
            var viewportRect = viewportGO.AddComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = Vector2.zero;
            viewportRect.offsetMax = Vector2.zero;
            viewportGO.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.01f);
            viewportGO.AddComponent<Mask>().showMaskGraphic = false;

            var contentGO = new GameObject("Content");
            contentGO.transform.SetParent(viewportGO.transform, false);
            var contentRect = contentGO.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(0f, 1f);
            contentRect.pivot = new Vector2(0f, 1f);
            contentRect.anchoredPosition = Vector2.zero;

            var contentHL = contentGO.AddComponent<HorizontalLayoutGroup>();
            contentHL.spacing = 4f;
            contentHL.padding = new RectOffset(4, 4, 4, 4);
            contentHL.childAlignment = TextAnchor.UpperLeft;
            contentHL.childControlWidth = false;
            contentHL.childControlHeight = true;
            contentHL.childForceExpandWidth = false;
            contentHL.childForceExpandHeight = false;

            var contentFitter = contentGO.AddComponent<ContentSizeFitter>();
            contentFitter.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
            contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            _column1Container = CreateColumnContainer(contentGO.transform, "Column1");
            _column2Container = CreateColumnContainer(contentGO.transform, "Column2");
            _column2Container.SetActive(false);

            _scrollRect = _root.AddComponent<ScrollRect>();
            _scrollRect.viewport = viewportRect;
            _scrollRect.content = contentRect;
            _scrollRect.horizontal = false;
            _scrollRect.vertical = true;
            _scrollRect.scrollSensitivity = 400f;
            _scrollRect.movementType = ScrollRect.MovementType.Clamped;
            _scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
            _scrollRect.enabled = false;

            var rootLE = _root.AddComponent<LayoutElement>();
            rootLE.minHeight = 30f;
            rootLE.minWidth = PANEL_WIDTH;

            _root.AddComponent<GroupHudDragHandler>().Initialize(this);

            LoadPosition();
            _root.SetActive(false);
        }

        private GameObject CreateColumnContainer(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>().sizeDelta = new Vector2(COLUMN_INNER_WIDTH, 0f);

            var vl = go.AddComponent<VerticalLayoutGroup>();
            vl.spacing = 4f;
            vl.childControlWidth = true;
            vl.childControlHeight = true;
            vl.childForceExpandWidth = true;
            vl.childForceExpandHeight = false;

            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var le = go.AddComponent<LayoutElement>();
            le.preferredWidth = COLUMN_INNER_WIDTH;
            le.minWidth = COLUMN_INNER_WIDTH;
            return go;
        }

        private MemberUI CreateMemberUI(string id)
        {
            var ui = new MemberUI();

            var memberGO = new GameObject($"Member_{id}");
            memberGO.transform.SetParent(_column1Container.transform, false);
            ui.Root = memberGO;
            memberGO.AddComponent<RectTransform>();

            var le = memberGO.AddComponent<LayoutElement>();
            le.preferredHeight = MEMBER_HEIGHT + STATUS_EFFECT_ROW_HEIGHT * 2;
            le.minHeight = MEMBER_HEIGHT + STATUS_EFFECT_ROW_HEIGHT;

            var bg = memberGO.AddComponent<Image>();
            bg.color = new Color(0.05f, 0.05f, 0.08f, 0.5f);
            bg.raycastTarget = false;

            CreateNameRow(memberGO.transform, ui);
            CreateStatBars(memberGO.transform, ui);
            CreateStatusEffectRow(memberGO.transform, ui);
            return ui;
        }

        private void CreateNameRow(Transform parent, MemberUI ui)
        {
            var rowGO = new GameObject("NameRow");
            rowGO.transform.SetParent(parent, false);
            var rect = rowGO.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 0.70f);
            rect.anchorMax = new Vector2(1, 1);
            rect.offsetMin = new Vector2(4, 0);
            rect.offsetMax = new Vector2(-4, -2);

            var hl = rowGO.AddComponent<HorizontalLayoutGroup>();
            hl.spacing = 4f;
            hl.childControlWidth = true;
            hl.childControlHeight = true;
            hl.childForceExpandWidth = false;
            hl.childForceExpandHeight = true;

            ui.NameText = CreateText(rowGO.transform, "Name", "", 11f, NameColor, TextAlignmentOptions.MidlineLeft, true);
            ui.NameText.overflowMode = TextOverflowModes.Ellipsis;
            ui.NameText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

            ui.StatusText = CreateText(rowGO.transform, "Status", "", 9f, DistanceColor, TextAlignmentOptions.MidlineRight, false);
            ui.StatusText.gameObject.AddComponent<LayoutElement>().minWidth = 44f;
        }

        private void CreateStatBars(Transform parent, MemberUI ui)
        {
            var barsGO = new GameObject("StatBars");
            barsGO.transform.SetParent(parent, false);
            var rect = barsGO.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 0);
            rect.anchorMax = new Vector2(1, 0.68f);
            rect.offsetMin = new Vector2(4, 2);
            rect.offsetMax = new Vector2(-4, 0);

            var vl = barsGO.AddComponent<VerticalLayoutGroup>();
            vl.spacing = BAR_SPACING;
            vl.childControlWidth = true;
            vl.childControlHeight = true;
            vl.childForceExpandWidth = true;
            vl.childForceExpandHeight = false;

            ui.HealthBarFill = CreateStatBar(barsGO.transform, "Health", HealthBarColor);
            ui.StaminaBarFill = CreateStatBar(barsGO.transform, "Stamina", StaminaBarColor);
            ui.EitrBarFill = CreateStatBar(barsGO.transform, "Eitr", EitrBarColor);
            ui.EitrBarRoot = ui.EitrBarFill.transform.parent.parent.gameObject; // Fill → Inner → bar root
        }

        private Image CreateStatBar(Transform parent, string name, Color fillColor)
        {
            var barGO = new GameObject($"{name}Bar");
            barGO.transform.SetParent(parent, false);
            barGO.AddComponent<RectTransform>();
            var le = barGO.AddComponent<LayoutElement>();
            le.preferredHeight = BAR_HEIGHT;
            le.minHeight = BAR_HEIGHT;

            barGO.AddComponent<Image>().color = BarBackgroundColor;
            barGO.GetComponent<Image>().raycastTarget = false;

            var innerGO = new GameObject("Inner");
            innerGO.transform.SetParent(barGO.transform, false);
            var innerRect = innerGO.AddComponent<RectTransform>();
            innerRect.anchorMin = Vector2.zero;
            innerRect.anchorMax = Vector2.one;
            innerRect.offsetMin = new Vector2(1, 1);
            innerRect.offsetMax = new Vector2(-1, -1);
            innerRect.pivot = new Vector2(0, 0.5f);

            var fillGO = new GameObject("Fill");
            fillGO.transform.SetParent(innerGO.transform, false);
            var fillRect = fillGO.AddComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;
            fillRect.pivot = new Vector2(0, 0.5f);

            var fill = fillGO.AddComponent<Image>();
            fill.color = fillColor;
            fill.raycastTarget = false;
            return fill;
        }

        private void CreateStatusEffectRow(Transform parent, MemberUI ui)
        {
            var go = new GameObject("StatusEffects");
            go.transform.SetParent(parent, false);
            var rect = go.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0, 0);
            rect.anchorMax = new Vector2(1, 0);
            rect.pivot = new Vector2(0, 0);
            rect.offsetMin = new Vector2(4, 2);
            rect.offsetMax = new Vector2(-4, 2 + STATUS_EFFECT_ROW_HEIGHT * 2);

            var grid = go.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(STATUS_EFFECT_ICON_SIZE + 16f, STATUS_EFFECT_ROW_HEIGHT);
            grid.spacing = new Vector2(2f, 2f);
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.childAlignment = TextAnchor.UpperLeft;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = MAX_EFFECTS_PER_ROW;

            ui.StatusEffectContainer = go.transform;
        }

        private StatusIconUI CreateStatusIcon(Transform parent)
        {
            var iconUI = new StatusIconUI { Root = new GameObject("EffectIcon") };
            iconUI.Root.transform.SetParent(parent, false);
            iconUI.Root.AddComponent<RectTransform>().sizeDelta = new Vector2(STATUS_EFFECT_ICON_SIZE + 16f, STATUS_EFFECT_ROW_HEIGHT);

            var iconGO = new GameObject("Icon");
            iconGO.transform.SetParent(iconUI.Root.transform, false);
            var iconRect = iconGO.AddComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0, 0.5f);
            iconRect.anchorMax = new Vector2(0, 0.5f);
            iconRect.pivot = new Vector2(0, 0.5f);
            iconRect.sizeDelta = new Vector2(STATUS_EFFECT_ICON_SIZE, STATUS_EFFECT_ICON_SIZE);
            iconUI.Icon = iconGO.AddComponent<Image>();
            iconUI.Icon.raycastTarget = false;

            iconUI.TimerText = CreateText(iconUI.Root.transform, "Timer", "", 8f, Color.white, TextAlignmentOptions.MidlineLeft, false);
            var tRect = iconUI.TimerText.rectTransform;
            tRect.anchorMin = new Vector2(0, 0);
            tRect.anchorMax = new Vector2(1, 1);
            tRect.offsetMin = new Vector2(STATUS_EFFECT_ICON_SIZE + 1, 0);
            tRect.offsetMax = Vector2.zero;
            return iconUI;
        }

        private static TMP_Text CreateText(Transform parent, string name, string text, float size, Color color, TextAlignmentOptions align, bool bold)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = align;
            t.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.raycastTarget = false;
            UIFontConfig.ApplyGameFont(t);   // config-aware (defaults to Averia Sans)
            return t;
        }

        #endregion

        #region Layout

        private void RebuildLayout()
        {
            var roots = new List<GameObject>(_orderedIds.Count);
            foreach (var id in _orderedIds)
                if (_rows.TryGetValue(id, out var ui) && ui.Root != null) roots.Add(ui.Root);

            int total = roots.Count;
            int visRows = VisibleRows;
            bool needsCol2 = VisibleColumns >= 2;
            bool needsScroll = total > visRows * (needsCol2 ? 2 : 1);

            if (_column2Container != null) _column2Container.SetActive(needsCol2);
            int splitAt = needsCol2 ? visRows : int.MaxValue;

            for (int i = 0; i < roots.Count; i++)
            {
                bool inCol2 = needsCol2 && i >= splitAt;
                var targetCol = inCol2 ? _column2Container : _column1Container;
                if (targetCol == null) continue;
                if (roots[i].transform.parent != targetCol.transform)
                    roots[i].transform.SetParent(targetCol.transform, false);
                roots[i].transform.SetSiblingIndex(inCol2 ? i - splitAt : i);
            }

            float colW = COLUMN_INNER_WIDTH;
            float totalW = needsCol2 ? colW * 2 + 4f + 8f : PANEL_WIDTH;
            float rowH = MEMBER_HEIGHT + STATUS_EFFECT_ROW_HEIGHT * 2 + 4f;
            int visibleRowsActual = Mathf.Clamp(total, 1, visRows);
            float viewportH = Mathf.Max(30f, visibleRowsActual * rowH + 8f);

            if (_rootRect != null) _rootRect.sizeDelta = new Vector2(totalW, viewportH);
            if (_scrollRect != null) _scrollRect.enabled = needsScroll;
        }

        private static void SetBarFill(Image fill, float pct)
        {
            if (fill == null) return;
            var rect = fill.rectTransform;
            var max = rect.anchorMax;
            max.x = Mathf.Clamp01(pct);
            rect.anchorMax = max;
        }

        #endregion

        #region Drag + position

        public void OnDragStart(Vector2 mousePosition)
        {
            if (!_isRepositionMode || _rootRect == null) return;
            _isDragging = true;
            _dragOffset = (Vector2)_rootRect.position - mousePosition;
        }

        public void OnDrag(Vector2 mousePosition)
        {
            if (!_isDragging || !_isRepositionMode || _rootRect == null) return;
            _rootRect.position = mousePosition + _dragOffset;
        }

        public void OnDragEnd()
        {
            if (!_isDragging) return;
            _isDragging = false;
            SavePosition();
        }

        private void SavePosition()
        {
            if (_rootRect == null) return;
            var p = _rootRect.anchoredPosition;
            if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsInfinity(p.x) || float.IsInfinity(p.y)) return;
            PlayerPrefs.SetFloat(PREF_POS_X, p.x);
            PlayerPrefs.SetFloat(PREF_POS_Y, p.y);
            PlayerPrefs.Save();
        }

        private void LoadPosition()
        {
            if (_rootRect == null) return;
            Vector2 def = GroupHudConfig.DefaultPosition;
            float x = PlayerPrefs.GetFloat(PREF_POS_X, def.x);
            float y = PlayerPrefs.GetFloat(PREF_POS_Y, def.y);
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y)) { x = def.x; y = def.y; }
            _rootRect.anchoredPosition = new Vector2(x, y);
        }

        /// <summary>Clears the saved position and snaps back to the configured default.</summary>
        public void ResetPosition()
        {
            PlayerPrefs.DeleteKey(PREF_POS_X);
            PlayerPrefs.DeleteKey(PREF_POS_Y);
            PlayerPrefs.Save();
            if (_rootRect != null) _rootRect.anchoredPosition = GroupHudConfig.DefaultPosition;
        }

        #endregion
    }
}
