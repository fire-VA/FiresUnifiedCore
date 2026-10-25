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
    /// Ported from the original FiresCompanions companion HUD and generalized: the companion-specific
    /// scanning/respawn/stat plumbing now lives behind the provider contract, leaving this purely a
    /// renderer. Draggable in Esc-menu reposition mode; position persists per-client in PlayerPrefs.
    /// </summary>
    public sealed class GroupHudController : MonoBehaviour
    {
        #region Constants

        private const float PanelWidth = 180f;
        private const float MemberHeight = 50f;
        private const float NameRowHeight = 17f;
        private const float BarHeight = 6f;
        private const float BarSpacing = 2f;
        private const float StatusEffectRowHeight = 16f;
        private const float StatusEffectIconSize = 14f;
        private const int MaxEffectsPerRow = 5;
        private const float ColumnInnerWidth = PanelWidth - 8f;

        private const string PrefPosX = "GroupHud_PosX";
        private const string PrefPosY = "GroupHud_PosY";

        private static readonly Color HealthBarColor = new Color(0.8f, 0.2f, 0.2f, 1f);
        private static readonly Color StaminaBarColor = new Color(0.9f, 0.8f, 0.2f, 1f);
        private static readonly Color EitrBarColor = new Color(0.3f, 0.5f, 0.9f, 1f);
        private static readonly Color BarBackgroundColor = new Color(0.1f, 0.08f, 0.06f, 0.8f);
        private static readonly Color PanelBackgroundColor = new Color(0.13f, 0.093f, 0.058f, 0.9f);  // FUC brown, matches territory box / quest tracker
        private static readonly Color PanelGoldBorder = new Color(0.72f, 0.55f, 0.28f, 1f);
        private static readonly Color RowFill = new Color(0.09f, 0.065f, 0.04f, 0.55f);
        private static readonly Color RowBorder = new Color(0.4f, 0.3f, 0.16f, 0.6f);
        private static readonly Color NameColor = new Color(1f, 0.85f, 0.5f, 1f);
        private static readonly Color DistanceColor = new Color(0.7f, 0.7f, 0.7f, 1f);
        private static readonly Color DeadNameColor = new Color(0.5f, 0.4f, 0.35f, 1f);
        private static readonly Color DeadBarColor = new Color(0.2f, 0.2f, 0.2f, 0.5f);
        private static readonly Color StatusTextColor = new Color(1f, 0.5f, 0.3f, 1f);

        private const float UpdateInterval = 0.1f;

        private static int VisibleRows => Mathf.Clamp(GroupHudConfig.MaxRowsPerColumn?.Value ?? 8, 1, 20);
        private static int VisibleColumns => Mathf.Clamp(GroupHudConfig.Columns?.Value ?? 1, 1, 2);

        #endregion

        #region Fields

        private static GroupHudController _instance;
        public static GroupHudController Instance => _instance;

        private GameObject _root;
        private RectTransform _rootRect;
        // the UI shell's minimap column places this HUD under the minimap plates while the override is ON (restored on OFF).
        internal RectTransform RootRect => _rootRect;
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
            public RectTransform BarsRect;
            public int LaidOutIcons = -1;   // the icon count the card was last sized for // its bottom edge moves up over the status-icon rows
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
            var changed = e?.ChangedSetting;
            if (changed != GroupHudConfig.MaxRowsPerColumn && changed != GroupHudConfig.Columns
                && changed != GroupHudConfig.DefaultPosX && changed != GroupHudConfig.DefaultPosY) return;
            try
            {
                RebuildLayout();
                if (!DockOwned && _rootRect != null && !(PlayerPrefs.HasKey(PrefPosX) && PlayerPrefs.HasKey(PrefPosY)))
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
            if (Time.time - _lastUpdate >= UpdateInterval)
            {
                _lastUpdate = Time.time;
                RefreshMembers();
            }

            bool show = ShouldShowHud();
            if (!show) OnDragEnd();
            if (_root != null)
            {
                _root.SetActive(show);
                if (_backgroundImage != null) // tint the baked brown/gold sprite: warm highlight while dragging, plain otherwise
                    _backgroundImage.color = _isDragging ? new Color(1f, 0.92f, 0.78f) : Color.white;
            }
        }

        #endregion

        #region Visibility

        private bool ShouldShowHud()
        {
            if (GroupHudConfig.ShowGroupHud != null && !GroupHudConfig.ShowGroupHud.Value) return false;
            if (_localPlayer == null) return false;
            // Honour the vanilla "hide HUD" toggle (Ctrl+F3) — the same gate vanilla nameplates / health bars use.
            if (Hud.IsUserHidden()) return false;
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

            return _orderedIds.Count > 0;
        }

        #endregion

        #region Member refresh

        private void RefreshMembers()
        {
            // Pull + order: SortKey asc, then name.
            var members = GroupHudBridge.CollectMembers();
            members.Sort((a, b) =>
            {
                int comparison = a.SortKey.CompareTo(b.SortKey);
                return comparison != 0 ? comparison : string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.Ordinal);
            });

            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool orderChanged = members.Count != _orderedIds.Count;
            _orderedIds.Clear();

            for (int i = 0; i < members.Count; i++)
            {
                var member = members[i];
                seen.Add(member.Id);
                _orderedIds.Add(member.Id);

                if (!_rows.TryGetValue(member.Id, out var ui))
                {
                    ui = CreateMemberUI(member.Id);
                    _rows[member.Id] = ui;
                    orderChanged = true;
                }
                UpdateRow(member, ui);
            }

            // Drop rows whose members vanished.
            if (_rows.Count > seen.Count)
            {
                var stale = _rows.Keys.Where(k => !seen.Contains(k)).ToList();
                foreach (var key in stale)
                {
                    // Destroy only happens at the end of
                    // the frame, and RebuildLayout below measured the gone card(s) as still there. Off and out of the layout first.
                    if (_rows.TryGetValue(key, out var ui) && ui.Root != null) { ui.Root.SetActive(false); ui.Root.transform.SetParent(null, false); Destroy(ui.Root); Debug.Log($"[GroupHud] member {key} left the group: card removed"); }
                    _rows.Remove(key);
                    orderChanged = true;
                }
            }

            if (_iconLayoutDirty && !orderChanged) { _iconLayoutDirty = false; RebuildLayout(); }
            if (orderChanged)
            {
                _iconLayoutDirty = false;
                // every membership change, with who came and went
                var added = new List<string>(); foreach (var id in seen) if (!_lastMembers.Contains(id)) added.Add(id);
                var gone = new List<string>(); foreach (var id in _lastMembers) if (!seen.Contains(id)) gone.Add(id);
                _lastMembers.Clear(); foreach (var id in seen) _lastMembers.Add(id);
                Debug.Log($"[GroupHud] members changed: +{added.Count} [{string.Join(", ", added)}] / -{gone.Count} [{string.Join(", ", gone)}] -> {_orderedIds.Count} card(s)");
                RebuildLayout();
            }
        }

        private void UpdateRow(GroupHudMember member, MemberUI ui)
        {
            if (ui.Root == null) return;

            Color nameCol = member.NameColor.a > 0f ? member.NameColor : (member.IsDead ? DeadNameColor : NameColor);
            ui.NameText.text = ShownName(member);
            // on the dark shell plate a provider colour that is near-black or near-transparent would hide the name
            if (ShellWidth > 0f && (nameCol.a < 0.35f || nameCol.r * 0.3f + nameCol.g * 0.59f + nameCol.b * 0.11f < 0.22f)) nameCol = member.IsDead ? DeadNameColor : NameColor;
            ui.NameText.color = nameCol;
            NameDiag(member, ui);

            // Right-side text: explicit status overrides distance.
            if (!string.IsNullOrEmpty(member.StatusText))
            {
                ui.StatusText.text = member.StatusText;
                ui.StatusText.color = member.IsDead ? StatusTextColor : DistanceColor;
            }
            else if (member.Distance >= 0f)
            {
                ui.StatusText.text = $"({member.Distance:F0}m)";
                ui.StatusText.color = DistanceColor;
            }
            else
            {
                ui.StatusText.text = "";
            }

            if (member.IsDead)
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
                SetBarFill(ui.HealthBarFill, member.MaxHealth > 0f ? Mathf.Clamp01(member.Health / member.MaxHealth) : 0f);
                SetBarFill(ui.StaminaBarFill, member.MaxStamina > 0f ? Mathf.Clamp01(member.Stamina / member.MaxStamina) : 0f);

                if (member.MaxEitr > 0f)
                {
                    ui.EitrBarRoot.SetActive(true);
                    ui.EitrBarFill.color = EitrBarColor;
                    SetBarFill(ui.EitrBarFill, Mathf.Clamp01(member.Eitr / member.MaxEitr));
                }
                else ui.EitrBarRoot.SetActive(false);
            }

            UpdateStatusIcons(member, ui);
        }

        private void UpdateStatusIcons(GroupHudMember member, MemberUI ui)
        {
            var icons = member.StatusIcons;
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
            // the card's icon row was sized only
            // when the MEMBER LIST changed; icons that appeared later sat on the bars. A changed icon count re-lays the cards out.
            if (count != ui.LaidOutIcons) _iconLayoutDirty = true;
        }
        private bool _iconLayoutDirty;

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
            _rootRect.sizeDelta = new Vector2(PanelWidth, 30f);

            _backgroundImage = _root.AddComponent<Image>();
            _backgroundImage.sprite = FiresCore.UI.FiresRoundedSkin.RoundedSprite(13, PanelBackgroundColor, PanelGoldBorder, 2);
            _backgroundImage.type = Image.Type.Sliced;
            _backgroundImage.color = Color.white;
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
            rootLE.minWidth = PanelWidth;

            _root.AddComponent<GroupHudDragHandler>().Initialize(this);

            LoadPosition();
            _root.SetActive(false);
        }

        private GameObject CreateColumnContainer(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.AddComponent<RectTransform>().sizeDelta = new Vector2(ColumnInnerWidth, 0f);

            var layout = go.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 4f;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            var layoutElement = go.AddComponent<LayoutElement>();
            layoutElement.preferredWidth = ColumnInnerWidth;
            layoutElement.minWidth = ColumnInnerWidth;
            return go;
        }

        private MemberUI CreateMemberUI(string id)
        {
            var ui = new MemberUI();

            var memberGO = new GameObject($"Member_{id}");
            memberGO.transform.SetParent(_column1Container.transform, false);
            ui.Root = memberGO;
            memberGO.AddComponent<RectTransform>();

            var layoutElement = memberGO.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = MemberHeight + StatusEffectRowHeight * 2;
            layoutElement.minHeight = MemberHeight + StatusEffectRowHeight;

            var image = memberGO.AddComponent<Image>();
            image.sprite = FiresCore.UI.FiresRoundedSkin.RoundedSprite(8, RowFill, RowBorder, 1);
            image.type = Image.Type.Sliced;
            image.color = Color.white;
            image.raycastTarget = false;

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
            // a FIXED 17 px top row. It was 30 % of the card, i.e. 13 px on a
            // card with no status icons - too short for 11 pt text, so TextMeshPro drew 0 characters ("rendered 0 chars" in the log)
            rect.anchorMin = new Vector2(0, 1);
            rect.anchorMax = new Vector2(1, 1);
            rect.offsetMin = new Vector2(4, -(NameRowHeight + 2f));
            rect.offsetMax = new Vector2(-4, -2);

            var rowLayout = rowGO.AddComponent<HorizontalLayoutGroup>();
            rowLayout.spacing = 4f;
            rowLayout.childControlWidth = true;
            rowLayout.childControlHeight = true;
            rowLayout.childForceExpandWidth = false;
            rowLayout.childForceExpandHeight = true;

            ui.NameText = CreateText(rowGO.transform, "Name", "", 11f, NameColor, TextAlignmentOptions.MidlineLeft, true);
            ui.NameText.overflowMode = TextOverflowModes.Ellipsis;
            var nameLe = ui.NameText.gameObject.AddComponent<LayoutElement>();
            nameLe.flexibleWidth = 1f;
            nameLe.minWidth = 40f;   // never squeezed to nothing beside the status text

            ui.StatusText = CreateText(rowGO.transform, "Status", "", 9f, DistanceColor, TextAlignmentOptions.MidlineRight, false);
            ui.StatusText.gameObject.AddComponent<LayoutElement>().minWidth = 44f;
        }

        private void CreateStatBars(Transform parent, MemberUI ui)
        {
            var barsGO = new GameObject("StatBars");
            barsGO.transform.SetParent(parent, false);
            var rect = barsGO.AddComponent<RectTransform>();
            // the bars fill the card under the name row, down to the status-icon rows (SizeMemberRows moves the bottom edge)
            rect.anchorMin = new Vector2(0, 0);
            rect.anchorMax = new Vector2(1, 1);
            rect.offsetMin = new Vector2(4, 2);
            rect.offsetMax = new Vector2(-4, -(NameRowHeight + 4f));
            ui.BarsRect = rect;

            var barsLayout = barsGO.AddComponent<VerticalLayoutGroup>();
            barsLayout.spacing = BarSpacing;
            barsLayout.childControlWidth = true;
            barsLayout.childControlHeight = true;
            barsLayout.childForceExpandWidth = true;
            barsLayout.childForceExpandHeight = false;

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
            var layoutElement = barGO.AddComponent<LayoutElement>();
            layoutElement.preferredHeight = BarHeight;
            layoutElement.minHeight = BarHeight;

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
            rect.offsetMax = new Vector2(-4, 2 + StatusEffectRowHeight * 2);

            var grid = go.AddComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(StatusEffectIconSize + 16f, StatusEffectRowHeight);
            grid.spacing = new Vector2(2f, 2f);
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.childAlignment = TextAnchor.UpperLeft;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = MaxEffectsPerRow;

            ui.StatusEffectContainer = go.transform;
        }

        private StatusIconUI CreateStatusIcon(Transform parent)
        {
            var iconUI = new StatusIconUI { Root = new GameObject("EffectIcon") };
            iconUI.Root.transform.SetParent(parent, false);
            iconUI.Root.AddComponent<RectTransform>().sizeDelta = new Vector2(StatusEffectIconSize + 16f, StatusEffectRowHeight);

            var iconGO = new GameObject("Icon");
            iconGO.transform.SetParent(iconUI.Root.transform, false);
            var iconRect = iconGO.AddComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0, 0.5f);
            iconRect.anchorMax = new Vector2(0, 0.5f);
            iconRect.pivot = new Vector2(0, 0.5f);
            iconRect.sizeDelta = new Vector2(StatusEffectIconSize, StatusEffectIconSize);
            iconUI.Icon = iconGO.AddComponent<Image>();
            iconUI.Icon.raycastTarget = false;

            iconUI.TimerText = CreateText(iconUI.Root.transform, "Timer", "", 8f, Color.white, TextAlignmentOptions.MidlineLeft, false);
            var timerRect = iconUI.TimerText.rectTransform;
            timerRect.anchorMin = new Vector2(0, 0);
            timerRect.anchorMax = new Vector2(1, 1);
            timerRect.offsetMin = new Vector2(StatusEffectIconSize + 1, 0);
            timerRect.offsetMax = Vector2.zero;
            return iconUI;
        }

        private static TMP_Text CreateText(Transform parent, string name, string text, float size, Color color, TextAlignmentOptions align, bool bold)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = size;
            label.color = color;
            label.alignment = align;
            label.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.raycastTarget = false;
            UIFontConfig.ApplyGameFont(label);   // config-aware (defaults to Averia Sans)
            return label;
        }

        #endregion

        #region Layout

        private string _frameSig;

        // after every layout, per card with
        // icons, the icon grid's drawn top against the lowest bar's drawn bottom; one Debug line per change, a Warning on any overlap
        private readonly Dictionary<string, string> _iconCheck = new Dictionary<string, string>();
        internal static int IconOverlaps;   // for tests: cards whose icons overlap the bars at the last check
        private void CheckIconRows()
        {
            try
            {
                Canvas.ForceUpdateCanvases();
                var c = new Vector3[4]; int overlaps = 0;
                foreach (var kv in _rows)
                {
                    var ui = kv.Value;
                    if (ui?.Root == null || !ui.Root.activeInHierarchy || ui.BarsRect == null || !(ui.StatusEffectContainer is RectTransform grid)) continue;
                    int icons = ui.LaidOutIcons;
                    if (icons <= 0) continue;
                    // the lowest visible bar: eitr when shown, else stamina
                    var low = ui.EitrBarRoot != null && ui.EitrBarRoot.activeInHierarchy ? (RectTransform)ui.EitrBarRoot.transform : (ui.StaminaBarFill != null ? (RectTransform)ui.StaminaBarFill.transform.parent.parent : null);
                    if (low == null) continue;
                    low.GetWorldCorners(c); float barBottom = c[0].y;
                    float iconTop = float.MinValue;
                    foreach (Transform t in grid) if (t.gameObject.activeSelf && t is RectTransform rt) { rt.GetWorldCorners(c); iconTop = Mathf.Max(iconTop, c[1].y); }
                    if (iconTop == float.MinValue) continue;
                    float scale = ui.Root.transform.lossyScale.y > 0f ? ui.Root.transform.lossyScale.y : 1f;
                    float gap = (barBottom - iconTop) / scale;   // screen y up: positive = icons below the bar
                    bool ok = gap >= 2f - 0.5f;
                    if (!ok) overlaps++;
                    string sig = $"{icons}:{(ok ? "ok" : "overlap")}:{gap:0}";
                    if (_iconCheck.TryGetValue(kv.Key, out var was) && was == sig) continue;
                    _iconCheck[kv.Key] = sig;
                    string line = $"[GroupHud] card {kv.Key}: {icons} icon(s), icon row top vs lowest bar bottom: gap {gap:0.#} px - {(ok ? "OK" : "OVERLAP")}";
                    if (ok) FiresCore.Logging.FiresLogger.LogDebug(line); else FiresCore.Logging.FiresLogger.LogWarning(line);
                }
                IconOverlaps = overlaps;
            }
            catch { }
        }
        private readonly HashSet<string> _lastMembers = new HashSet<string>(StringComparer.Ordinal);
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

            float colW = ColumnInnerWidth;
            float totalW = needsCol2 ? colW * 2 + 4f + 8f : PanelWidth;
            float rowH = MemberHeight + StatusEffectRowHeight * 2 + 4f;
            int visibleRowsActual = Mathf.Clamp(total, 1, visRows);
            float viewportH = Mathf.Max(30f, visibleRowsActual * rowH + 8f);

            // in the UI shell's minimap column (override ON)
            // the panel takes the plate's width and rounded style, and its height is the content's real height (two status rows are no
            // longer reserved per member). Off the column: unchanged.
            // every member row reserved TWO status-icon rows (LayoutElement preferred =
            // MemberHeight + 2 rows), so the content's real height kept the empty space. In the column a row is sized from the icons it
            // actually has; off the column the original reservation comes back.
            // the panel is a
            // dock element, not in the minimap column, so it took the "off the column" branch: two icon rows reserved under every member and
            // the panel never shrank to its content. Rows and panel now fit their real content wherever the panel sits (one column).
            SizeMemberRows(!needsCol2);

            if (!needsCol2)
            {
                // its OWN width always;
                // the shell gives it only the rounded style (the plate grew with a long alt-biome title and stretched it)
                SetColumnWidth(_column1Container, colW);
                var content = _scrollRect != null ? _scrollRect.content : null;
                if (content != null)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(content);
                    float real = LayoutUtility.GetPreferredHeight(content);
                    if (real > 1f) viewportH = Mathf.Min(viewportH, real);
                }
            }
            else SetColumnWidth(_column1Container, ColumnInnerWidth);
            ApplyShellStyle();

            if (_rootRect != null) _rootRect.sizeDelta = new Vector2(totalW, viewportH);
            CheckIconRows();
            // the frame fits the shown cards exactly; say what it was sized for (Debug, on change)
            int shown = 0; foreach (var r in roots) if (r != null && r.activeSelf) shown++;
            string sig = $"{shown}/{total}:{totalW:0}x{viewportH:0}";
            if (sig != _frameSig)
            {
                _frameSig = sig;
                FiresCore.Logging.FiresLogger.LogDebug($"[GroupHud] frame sized for {shown} card(s): {totalW:0}x{viewportH:0}{(shown != total ? $" ({total - shown} card(s) hidden)" : "")}");
            }
            if (_scrollRect != null) _scrollRect.enabled = needsScroll;
        }

        // a provider may hand an empty Name; fall back to the player's own name by id,
        // and say once per member which name is shown and where it came from.
        private readonly Dictionary<string, string> _nameLogged = new Dictionary<string, string>();
        private string ShownName(GroupHudMember member)
        {
            string name = member.Name, src = "provider";
            if (string.IsNullOrEmpty(name)) { name = FallbackName(member.Id); src = string.IsNullOrEmpty(name) ? "none" : "player list"; }
            name = name ?? "";
            string key = member.Id ?? "";
            if (!_nameLogged.TryGetValue(key, out var was) || was != name)
            {
                _nameLogged[key] = name;
                Debug.Log($"[GroupHud] member {key}: name '{name}' (from {src})");
            }
            return name;
        }

        // once per member (after its first second on screen), what the name text really is on screen.
        private readonly HashSet<string> _nameDiag = new HashSet<string>();
        private readonly Dictionary<string, float> _nameSeen = new Dictionary<string, float>();
        private void NameDiag(GroupHudMember member, MemberUI ui)
        {
            string key = member.Id ?? "";
            if (_nameDiag.Contains(key)) return;
            if (!_nameSeen.TryGetValue(key, out float t0)) { _nameSeen[key] = Time.unscaledTime; return; }
            if (Time.unscaledTime - t0 < 1f) return;
            _nameDiag.Add(key);
            var t = ui.NameText; var rt = t.rectTransform;
            Debug.Log($"[GroupHud] member {key}: name text '{t.text}' drawn at {rt.rect.width:0}x{rt.rect.height:0} px, size {t.fontSize:0.#}, colour {t.color}, " +
                $"active {t.isActiveAndEnabled}, overflow {t.overflowMode}, rendered {t.textInfo?.characterCount ?? -1} chars");
        }
        private static string FallbackName(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var p in Player.GetAllPlayers())
            {
                if (p == null) continue;
                if (p.GetZDOID().ToString() == id || p.GetPlayerID().ToString() == id || p.GetPlayerName() == id) return p.GetPlayerName();
            }
            if (ZNet.instance != null)
                foreach (var info in ZNet.instance.GetPlayerList())
                    if (info.m_characterID.ToString() == id || info.m_name == id) return info.m_name;
            return null;
        }

        private void SizeMemberRows(bool fit)
        {
            foreach (var kv in _rows)
            {
                var ui = kv.Value;
                if (ui == null || ui.Root == null) continue;
                var le = ui.Root.GetComponent<LayoutElement>();
                if (le == null) continue;
                int rows = 2;
                if (fit)
                {
                    int icons = 0;
                    if (ui.StatusEffectContainer != null)
                        foreach (Transform c in ui.StatusEffectContainer) if (c.gameObject.activeSelf) icons++;
                    rows = icons == 0 ? 0 : Mathf.CeilToInt(icons / (float)Mathf.Max(1, MaxEffectsPerRow));
                }
                // the icon grid (anchored to the row's bottom, icons from its top) is as tall as the rows it shows, so icons sit in
                // the space the row now reserves and never ride up over the bars
                if (ui.StatusEffectContainer is RectTransform sc)
                {
                    float gridH = StatusEffectRowHeight * (fit ? Mathf.Max(rows, 1) : 2);
                    if (Mathf.Abs(sc.offsetMax.y - (2f + gridH)) > 0.5f) sc.offsetMax = new Vector2(sc.offsetMax.x, 2f + gridH);
                }
                // the icons get their own
                // rows UNDER the eitr bar (+2 px gap); the card grows by them only while a member has icons
                float iconsH = rows > 0 ? StatusEffectRowHeight * rows + 2f : 0f;
                { int n = 0; if (ui.StatusEffectContainer != null) foreach (Transform c in ui.StatusEffectContainer) if (c.gameObject.activeSelf) n++; ui.LaidOutIcons = n; }
                if (ui.BarsRect != null && Mathf.Abs(ui.BarsRect.offsetMin.y - (2f + iconsH)) > 0.5f) ui.BarsRect.offsetMin = new Vector2(ui.BarsRect.offsetMin.x, 2f + iconsH);
                float want = MemberHeight + iconsH;
                float min = fit ? want : MemberHeight + StatusEffectRowHeight;
                if (Mathf.Abs(le.preferredHeight - want) > 0.5f || Mathf.Abs(le.minHeight - min) > 0.5f) { le.preferredHeight = want; le.minHeight = min; }
            }
        }

        // ---- UI shell column ----
        internal static float ShellWidth;             // > 0 while the minimap column places this HUD (override ON)
        private bool _shellStyled;
        private static readonly Color ShellFill = new Color(0.086f, 0.067f, 0.047f, 0.92f);   // the minimap plate's fill
        private static readonly Color ShellEdge = new Color(0.541f, 0.396f, 0.220f, 1f);      // FiresTokens.Edge #8A6538

        private static void SetColumnWidth(GameObject col, float w)
        {
            if (col == null) return;
            var le = col.GetComponent<LayoutElement>();
            if (le != null && Mathf.Abs(le.preferredWidth - w) > 0.5f) { le.preferredWidth = w; le.minWidth = w; }
            var rt = col.transform as RectTransform;
            if (rt != null && Mathf.Abs(rt.sizeDelta.x - w) > 0.5f) rt.sizeDelta = new Vector2(w, rt.sizeDelta.y);
        }

        private void ApplyShellStyle()
        {
            bool want = ShellWidth > 0f;
            if (want == _shellStyled || _backgroundImage == null) return;
            _shellStyled = want;
            _backgroundImage.sprite = want
                ? FiresCore.UI.FiresRoundedSkin.RoundedSprite(10, ShellFill, ShellEdge, 1)
                : FiresCore.UI.FiresRoundedSkin.RoundedSprite(13, PanelBackgroundColor, PanelGoldBorder, 2);
        }

        /// <summary>the minimap column sets the plate width (0 = back to the HUD's own size and style).</summary>
        internal void SetShellWidth(float w)
        {
            if (Mathf.Abs(ShellWidth - w) < 0.5f) return;
            ShellWidth = w;
            RebuildLayout();
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

        // while Core's HUD dock owns the panel
        // (Unified ON, hud.grouphud) the dock is its ONLY writer: this older drag / PlayerPrefs save and the load / config position writes
        // stand down (they moved the same rect as the dock's drag, so a drop snapped back). Unified OFF: as before.
        internal static bool DockOwned;

        public void OnDragStart(Vector2 mousePosition)
        {
            if (DockOwned || !_isRepositionMode || _rootRect == null) return;
            _isDragging = true;
            _dragOffset = (Vector2)_rootRect.position - mousePosition;
        }

        public void OnDrag(Vector2 mousePosition)
        {
            if (DockOwned || !_isDragging || !_isRepositionMode || _rootRect == null) return;
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
            var position = _rootRect.anchoredPosition;
            if (float.IsNaN(position.x) || float.IsNaN(position.y) || float.IsInfinity(position.x) || float.IsInfinity(position.y)) return;
            PlayerPrefs.SetFloat(PrefPosX, position.x);
            PlayerPrefs.SetFloat(PrefPosY, position.y);
            PlayerPrefs.Save();
        }

        /// <summary>back to its own saved / default spot (Core's Unified UI turned off).</summary>
        internal void ReloadPosition() { LoadPosition(); if (_root != null && !_root.activeSelf && ShouldShowHud()) _root.SetActive(true); }

        private void LoadPosition()
        {
            if (_rootRect == null || DockOwned) return;
            Vector2 def = GroupHudConfig.DefaultPosition;
            float x = PlayerPrefs.GetFloat(PrefPosX, def.x);
            float y = PlayerPrefs.GetFloat(PrefPosY, def.y);
            if (float.IsNaN(x) || float.IsNaN(y) || float.IsInfinity(x) || float.IsInfinity(y)) { x = def.x; y = def.y; }
            _rootRect.anchoredPosition = new Vector2(x, y);
        }

        /// <summary>Clears the saved position and snaps back to the configured default.</summary>
        public void ResetPosition()
        {
            PlayerPrefs.DeleteKey(PrefPosX);
            PlayerPrefs.DeleteKey(PrefPosY);
            PlayerPrefs.Save();
            if (_rootRect != null) _rootRect.anchoredPosition = GroupHudConfig.DefaultPosition;
        }

        #endregion
    }
}
