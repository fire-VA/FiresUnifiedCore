using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using FiresCore.UI.ContextMenu;
using FiresCore.Utilities;

namespace FiresCore.UI
{
    /// <summary>
    /// EnvironmentBoxPanel — the admin config window for a placed Environment Box, opened by Shift+Alt right-click
    /// (EnvironmentBoxContextMenu's claim). The EntryDoorPanel pattern exactly: a standalone top-level
    /// ScreenSpaceOverlay parchment window with AUTO-POPULATED scrolling dropdowns for every field — Environment
    /// (the full EnvMan list), Biome, Skybox, Visibility, Wind, Time — plus Size X/Y/Z fields and a Lock toggle.
    /// Every change applies IMMEDIATELY through the controller's Set* methods (same apply + ZDO-save + message
    /// path as the original hover-key cycles). Modality mirrors EntryDoorPanel: InputBlock + freed cursor +
    /// SuppressDriver, Esc closes. Client-only.
    /// </summary>
    public static class EnvironmentBoxPanel
    {
        private static GameObject _canvasGo, _panel;
        private static RectTransform _content;
        private static EnvironmentBoxController _box;
        private static bool _blocked;
        private static TMP_InputField _sizeX, _sizeY, _sizeZ;

        private static bool IsClientOnly() => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
        public static bool IsOpen => _canvasGo != null;

        public static void Open(EnvironmentBoxController box)
        {
            if (IsClientOnly() || box == null) return;
            Close();
            _box = box;
            Build();
        }

        private static void Build()
        {
            _canvasGo = new GameObject("FiresEnvBoxPanel");
            UnityEngine.Object.DontDestroyOnLoad(_canvasGo);
            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 6400;
            var scaler = _canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _canvasGo.AddComponent<GraphicRaycaster>();

            _panel = new GameObject("Panel");
            _panel.transform.SetParent(_canvasGo.transform, false);
            var panelRect = _panel.AddComponent<RectTransform>();
            panelRect.sizeDelta = new Vector2(560f, 580f);
            panelRect.anchorMin = panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.anchoredPosition = Vector2.zero;
            _panel.AddComponent<Image>().color = UIFontConfig.Colors.ParchmentPanel;
            var outline = _panel.AddComponent<Outline>();
            outline.effectColor = UIFontConfig.Colors.ParchmentOutline;
            outline.effectDistance = new Vector2(2.5f, -2.5f);
            _panel.AddComponent<DragMove>().Target = panelRect;

            var title = MakeLabel(_panel.transform, "Title", "Configure Environment Box", 24f, UIFontConfig.Colors.ParchmentInk, TextAlignmentOptions.Center);
            var titleRect = title.rectTransform;
            titleRect.anchorMin = new Vector2(0f, 1f); titleRect.anchorMax = new Vector2(1f, 1f); titleRect.pivot = new Vector2(0.5f, 1f);
            titleRect.sizeDelta = new Vector2(-24f, 40f); titleRect.anchoredPosition = new Vector2(0f, -12f);

            var contentGo = new GameObject("Content", typeof(RectTransform));
            _content = contentGo.GetComponent<RectTransform>();
            _content.SetParent(_panel.transform, false);
            _content.anchorMin = new Vector2(0f, 0f); _content.anchorMax = new Vector2(1f, 1f);
            _content.offsetMin = new Vector2(0f, 64f); _content.offsetMax = new Vector2(0f, -60f);

            UIBuilderHelper.CreateParchmentButton(_panel.transform, "Close",
                new Vector2(0.30f, 0.03f), new Vector2(0.70f, 0.10f), Close);

            var averia = UIFontConfig.GetFont(UIFontConfig.FontStyle.AveriaLibre);
            if (averia != null)
                foreach (var tmp in _canvasGo.GetComponentsInChildren<TMP_Text>(true)) tmp.font = averia;

            FiresContextMenu.SuppressDriver = true;
            // Opened FROM the context-menu driver, which already acquired InputBlock and stands down (SuppressDriver),
            // handing us the release — so ALWAYS own it (mirrors EntryDoorPanel's leak fix).
            if (!InputBlock.IsBlocked) InputBlock.Block(true);
            _blocked = true;
            FiresCore.Input.FiresInputBlock.Acquire(PanelToken);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;

            Rebuild();
        }

        private static void Rebuild()
        {
            if (_content == null || _box == null) return;
            for (int i = _content.childCount - 1; i >= 0; i--)
            {
                var child = _content.GetChild(i);
                child.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(child.gameObject);
            }

            int row = 0;

            // Environment — the FULL EnvMan list ("" shown as "Nothing").
            var envNames = EnvironmentBoxController.EnvironmentNames;
            var envOptions = new List<string>(envNames.Length);
            foreach (var envName in envNames) envOptions.Add(string.IsNullOrEmpty(envName) ? "Nothing" : envName);
            int envIndex = Array.IndexOf(envNames, _box.EnvironmentName);
            if (envIndex < 0) envIndex = 0;
            AddDropdownRow(row++, "Environment", envOptions, envIndex, i =>
            {
                if (i >= 0 && i < envNames.Length) _box.SetEnvironment(envNames[i]);
            });

            // Biome — the controller's own biome option list.
            var biomes = EnvironmentBoxController.BiomeOptions;
            var biomeOptions = new List<string>(biomes.Length);
            foreach (var biome in biomes) biomeOptions.Add(biome == Heightmap.Biome.None ? "None" : biome.ToString());
            int biomeIndex = Array.IndexOf(biomes, _box.ForcedBiome);
            if (biomeIndex < 0) biomeIndex = 0;
            AddDropdownRow(row++, "Biome", biomeOptions, biomeIndex, i =>
            {
                if (i >= 0 && i < biomes.Length) _box.SetBiome(biomes[i]);
            });

            // Skybox / Visibility — enum-populated.
            var skyboxes = (EnvironmentBoxController.SkyboxMode[])Enum.GetValues(typeof(EnvironmentBoxController.SkyboxMode));
            var skyboxOptions = new List<string>(skyboxes.Length);
            foreach (var skybox in skyboxes) skyboxOptions.Add(skybox.ToString());
            AddDropdownRow(row++, "Skybox", skyboxOptions, Array.IndexOf(skyboxes, _box.CurrentSkyboxMode), i =>
            {
                if (i >= 0 && i < skyboxes.Length) _box.SetSkyboxMode(skyboxes[i]);
            });

            // Visibility — EXPLICIT options, no enum derivation (the derived list rendered only two entries in the
            // field once; hardcoding all three guarantees Hidden is present). Logged so a short list is provable.
            var visibilities = new[]
            {
                EnvironmentBoxController.VisibilityMode.Full,
                EnvironmentBoxController.VisibilityMode.EdgesOnly,
                EnvironmentBoxController.VisibilityMode.Hidden,
            };
            var visibilityOptions = new List<string> { "Full", "EdgesOnly", "Hidden" };
            int visibilityIndex = Array.IndexOf(visibilities, _box.CurrentVisibility);
            if (visibilityIndex < 0) visibilityIndex = 0;
            Debug.Log($"[EnvBoxPanel] visibility dropdown built with: {string.Join(", ", visibilityOptions)} (current={_box.CurrentVisibility})");
            AddDropdownRow(row++, "Visibility", visibilityOptions, visibilityIndex, i =>
            {
                if (i >= 0 && i < visibilities.Length) _box.SetVisibility(visibilities[i]);
            });

            // Wind / Time — enum-populated with the controller's display names.
            var winds = (EnvironmentBoxController.WindIntensity[])Enum.GetValues(typeof(EnvironmentBoxController.WindIntensity));
            var windOptions = new List<string>(winds.Length);
            foreach (var wind in winds) windOptions.Add(EnvironmentBoxController.GetWindIntensityName(wind));
            AddDropdownRow(row++, "Wind", windOptions, Array.IndexOf(winds, _box.CurrentWindIntensity), i =>
            {
                if (i >= 0 && i < winds.Length) _box.SetWindIntensity(winds[i]);
            });

            var times = (EnvironmentBoxController.ForcedTimeOfDay[])Enum.GetValues(typeof(EnvironmentBoxController.ForcedTimeOfDay));
            var timeOptions = new List<string>(times.Length);
            foreach (var time in times) timeOptions.Add(EnvironmentBoxController.GetTimeOfDayName(time));
            AddDropdownRow(row++, "Time", timeOptions, Array.IndexOf(times, _box.CurrentTimeOfDay), i =>
            {
                if (i >= 0 && i < times.Length) _box.SetTimeOfDay(times[i]);
            });

            AddSizeRow(row++);
            AddScaleRow(row++);

            AddCycle(row++, "Lock", _box.Locked ? "Locked" : "Unlocked", () => { _box.ToggleLock(); Rebuild(); });

            var averia = UIFontConfig.GetFont(UIFontConfig.FontStyle.AveriaLibre);
            if (averia != null)
                foreach (var tmp in _content.GetComponentsInChildren<TMP_Text>(true)) tmp.font = averia;

            // FDT context-menu look (dark rounded shell, gold text) — replaces the parchment build
            // colors in place; runs after every rebuild since rows are recreated.
            FiresPopupTheme.Reskin(_panel);
        }

        private const float RowH = 40f, RowGap = 6f;

        private static RectTransform NewRow(int index)
        {
            var go = new GameObject("Row" + index, typeof(RectTransform));
            var rect = go.GetComponent<RectTransform>();
            rect.SetParent(_content, false);
            rect.anchorMin = new Vector2(0.05f, 1f); rect.anchorMax = new Vector2(0.95f, 1f); rect.pivot = new Vector2(0.5f, 1f);
            rect.sizeDelta = new Vector2(0f, RowH);
            rect.anchoredPosition = new Vector2(0f, -index * (RowH + RowGap));
            return rect;
        }

        private static void AddCycle(int index, string label, string value, UnityEngine.Events.UnityAction onClick)
        {
            var row = NewRow(index);
            UIBuilderHelper.CreateParchmentButton(row, $"{label}:   {value}",
                new Vector2(0f, 0f), new Vector2(1f, 1f), onClick);
        }

        // Size X/Y/Z input fields; each commits on end-edit through SetSize (which clamps + rebuilds trigger/mesh/
        // enclosure + persists), then the fields re-show the clamped values.
        private static void AddSizeRow(int index)
        {
            var row = NewRow(index);
            var lbl = MakeLabel(row, "Label", "Size (X·Y·Z)", 15f, UIFontConfig.Colors.ParchmentInk, TextAlignmentOptions.MidlineLeft);
            var labelRect = lbl.rectTransform;
            labelRect.anchorMin = new Vector2(0f, 0f); labelRect.anchorMax = new Vector2(0.34f, 1f); labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;

            _sizeX = SizeField(row, "X", new Vector2(0.36f, 0.05f), new Vector2(0.56f, 0.95f), _box.BoxSize.x);
            _sizeY = SizeField(row, "Y", new Vector2(0.58f, 0.05f), new Vector2(0.78f, 0.95f), _box.BoxSize.y);
            _sizeZ = SizeField(row, "Z", new Vector2(0.80f, 0.05f), new Vector2(1f, 0.95f), _box.BoxSize.z);
        }

        private static TMP_InputField SizeField(RectTransform row, string name, Vector2 aMin, Vector2 aMax, float value)
        {
            var field = UIBuilderHelper.CreateInputField(row, name, name, aMin, aMax, 15);
            field.contentType = TMP_InputField.ContentType.DecimalNumber;
            field.text = value.ToString("0.#");
            field.onEndEdit.AddListener(_ => ApplySize());
            return field;
        }

        private static void ApplySize()
        {
            if (_box == null) return;
            Vector3 size = _box.BoxSize;
            if (float.TryParse(_sizeX != null ? _sizeX.text : "", out float x)) size.x = x;
            if (float.TryParse(_sizeY != null ? _sizeY.text : "", out float y)) size.y = y;
            if (float.TryParse(_sizeZ != null ? _sizeZ.text : "", out float z)) size.z = z;
            _box.SetSize(size);
            RefreshSizeFields();
        }

        private static void RefreshSizeFields()
        {
            // Re-show the clamped result so the fields never lie about the applied size.
            if (_sizeX != null) _sizeX.SetTextWithoutNotify(_box.BoxSize.x.ToString("0.#"));
            if (_sizeY != null) _sizeY.SetTextWithoutNotify(_box.BoxSize.y.ToString("0.#"));
            if (_sizeZ != null) _sizeZ.SetTextWithoutNotify(_box.BoxSize.z.ToString("0.#"));
        }

        // 100% on the scale slider = the VANILLA interior env box (Location.Awake's 64 x 500 x 64) — the placed
        // piece's default size.
        private static readonly Vector3 ScaleBase = new Vector3(64f, 500f, 64f);

        // Uniform scale slider, 0%..1000% of the default box. The label tracks the drag live; the size applies on
        // RELEASE (one SetSize — not a mesh rebuild + ZDO save per drag-frame). SetSize clamps, and the size fields
        // re-sync to whatever was actually applied.
        private static void AddScaleRow(int index)
        {
            var row = NewRow(index);
            float pct = Mathf.Clamp(_box.BoxSize.x / ScaleBase.x * 100f, 0f, 1000f);
            var lbl = MakeLabel(row, "Label", $"Scale: {pct:0}%", 15f, UIFontConfig.Colors.ParchmentInk, TextAlignmentOptions.MidlineLeft);
            var labelRect = lbl.rectTransform;
            labelRect.anchorMin = new Vector2(0f, 0f); labelRect.anchorMax = new Vector2(0.34f, 1f); labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;

            var sliderGo = new GameObject("ScaleSlider", typeof(RectTransform));
            sliderGo.transform.SetParent(row, false);
            var sliderRect = (RectTransform)sliderGo.transform;
            sliderRect.anchorMin = new Vector2(0.36f, 0.30f); sliderRect.anchorMax = new Vector2(1f, 0.70f); sliderRect.offsetMin = sliderRect.offsetMax = Vector2.zero;
            var image = sliderGo.AddComponent<Image>(); image.color = UIFontConfig.Colors.ParchmentField;

            var slider = sliderGo.AddComponent<Slider>();

            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(sliderGo.transform, false);
            var fillAreaRect = (RectTransform)fillArea.transform;
            fillAreaRect.anchorMin = Vector2.zero; fillAreaRect.anchorMax = Vector2.one; fillAreaRect.offsetMin = new Vector2(2f, 2f); fillAreaRect.offsetMax = new Vector2(-2f, -2f);
            var fillGo = new GameObject("Fill", typeof(RectTransform));
            fillGo.transform.SetParent(fillArea.transform, false);
            var fillRt = (RectTransform)fillGo.transform;
            fillRt.anchorMin = Vector2.zero; fillRt.anchorMax = Vector2.one; fillRt.offsetMin = fillRt.offsetMax = Vector2.zero;
            fillGo.AddComponent<Image>().color = UIFontConfig.Colors.ParchmentButton;

            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(sliderGo.transform, false);
            var handleAreaRect = (RectTransform)handleArea.transform;
            handleAreaRect.anchorMin = Vector2.zero; handleAreaRect.anchorMax = Vector2.one; handleAreaRect.offsetMin = new Vector2(8f, 0f); handleAreaRect.offsetMax = new Vector2(-8f, 0f);
            var handleGo = new GameObject("Handle", typeof(RectTransform));
            handleGo.transform.SetParent(handleArea.transform, false);
            var handleRect = (RectTransform)handleGo.transform;
            handleRect.sizeDelta = new Vector2(16f, 0f);
            var hImg = handleGo.AddComponent<Image>(); hImg.color = UIFontConfig.Colors.ParchmentButtonInk;

            slider.fillRect = fillRt;
            slider.handleRect = handleRect;
            slider.targetGraphic = hImg;
            slider.minValue = 0f; slider.maxValue = 1000f; slider.wholeNumbers = true;
            slider.SetValueWithoutNotify(pct);
            // REAL-TIME scaling: the box resizes live as the handle drags (SetSize clamps + rebuilds mesh/trigger/
            // enclosure each step). The size fields re-sync live too.
            slider.onValueChanged.AddListener(v =>
            {
                if (_box == null) return;
                _box.SetSize(ScaleBase * (v / 100f));
                RefreshSizeFields();
                float applied = Mathf.Clamp(_box.BoxSize.x / ScaleBase.x * 100f, 0f, 1000f);
                lbl.text = $"Scale: {applied:0}%";
            });
        }

        private static void AddDropdownRow(int index, string label, List<string> options, int value, UnityEngine.Events.UnityAction<int> onChange)
        {
            var row = NewRow(index);
            var lbl = MakeLabel(row, "Label", label, 15f, UIFontConfig.Colors.ParchmentInk, TextAlignmentOptions.MidlineLeft);
            var labelRect = lbl.rectTransform;
            labelRect.anchorMin = new Vector2(0f, 0f); labelRect.anchorMax = new Vector2(0.34f, 1f); labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;

            var holder = new GameObject("DDHolder", typeof(RectTransform));
            var hrt = holder.GetComponent<RectTransform>();
            hrt.SetParent(row, false);
            hrt.anchorMin = new Vector2(0.36f, 0.08f); hrt.anchorMax = new Vector2(1f, 0.92f); hrt.offsetMin = hrt.offsetMax = Vector2.zero;
            MakeDropdown(holder.transform, options, value, onChange);
        }

        // Parchment-styled scrolling TMP_Dropdown — the exact EntryDoorPanel builder.
        private static TMP_Dropdown MakeDropdown(Transform parent, List<string> options, int value, UnityEngine.Events.UnityAction<int> onChange)
        {
            var go = new GameObject("Dropdown", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
            var image = go.AddComponent<Image>(); image.color = UIFontConfig.Colors.ParchmentField; image.raycastTarget = true;
            var dropdown = go.AddComponent<TMP_Dropdown>();

            var cap = ChildText(go.transform, "Label", 14f, UIFontConfig.Colors.ParchmentInk, TextAlignmentOptions.MidlineLeft);
            var captionRect = cap.rectTransform; captionRect.anchorMin = Vector2.zero; captionRect.anchorMax = Vector2.one; captionRect.offsetMin = new Vector2(8, 2); captionRect.offsetMax = new Vector2(-20, -2);

            var arr = ChildText(go.transform, "Arrow", 10f, UIFontConfig.Colors.ParchmentInk, TextAlignmentOptions.Center);
            arr.text = "▼";
            var arrowRect = arr.rectTransform; arrowRect.anchorMin = new Vector2(1, 0.5f); arrowRect.anchorMax = new Vector2(1, 0.5f); arrowRect.pivot = new Vector2(1, 0.5f); arrowRect.anchoredPosition = new Vector2(-6, 0); arrowRect.sizeDelta = new Vector2(16, 16);

            // CANONICAL uGUI dropdown template — NO layout group / fitter on Content. TMP_Dropdown positions and
            // sizes the item clones ITSELF against this exact structure (Content = one item high; items anchored to
            // the vertical middle). The old hand-rolled VerticalLayoutGroup fought that placement and swallowed the
            // LAST option (the "Hidden is missing" bug — obvious on 3-entry lists, unnoticed on 50-entry ones).
            var template = new GameObject("Template", typeof(RectTransform)); template.transform.SetParent(go.transform, false);
            var templateRect = template.GetComponent<RectTransform>();
            templateRect.anchorMin = new Vector2(0, 0); templateRect.anchorMax = new Vector2(1, 0); templateRect.pivot = new Vector2(0.5f, 1); templateRect.anchoredPosition = new Vector2(0, 2); templateRect.sizeDelta = new Vector2(0, 168);
            template.AddComponent<Image>().color = UIFontConfig.Colors.ParchmentPanel;
            var tScroll = template.AddComponent<ScrollRect>();

            var viewport = new GameObject("Viewport", typeof(RectTransform)); viewport.transform.SetParent(template.transform, false);
            var viewportRect = viewport.GetComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero; viewportRect.anchorMax = Vector2.one; viewportRect.pivot = new Vector2(0f, 1f);
            viewportRect.offsetMin = new Vector2(2, 2); viewportRect.offsetMax = new Vector2(-2, -2);
            viewport.AddComponent<RectMask2D>();

            var content = new GameObject("Content", typeof(RectTransform)); content.transform.SetParent(viewport.transform, false);
            var contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f); contentRect.anchorMax = new Vector2(1f, 1f); contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.anchoredPosition = Vector2.zero; contentRect.sizeDelta = new Vector2(0, 28);   // ONE item high — the dropdown grows it per option

            tScroll.viewport = viewportRect; tScroll.content = contentRect; tScroll.horizontal = false; tScroll.vertical = true; tScroll.movementType = ScrollRect.MovementType.Clamped; tScroll.scrollSensitivity = 24f;

            var item = new GameObject("Item", typeof(RectTransform), typeof(Toggle));
            item.transform.SetParent(content.transform, false);
            var itemRect = (RectTransform)item.transform;
            itemRect.anchorMin = new Vector2(0f, 0.5f); itemRect.anchorMax = new Vector2(1f, 0.5f); itemRect.sizeDelta = new Vector2(0, 28);

            var itemBgGo = new GameObject("Item Background", typeof(RectTransform), typeof(Image));
            itemBgGo.transform.SetParent(item.transform, false);
            var itemBackgroundRect = (RectTransform)itemBgGo.transform; itemBackgroundRect.anchorMin = Vector2.zero; itemBackgroundRect.anchorMax = Vector2.one; itemBackgroundRect.offsetMin = itemBackgroundRect.offsetMax = Vector2.zero;
            var itemBg = itemBgGo.GetComponent<Image>(); itemBg.color = UIFontConfig.Colors.ParchmentButton;

            var checkGo = new GameObject("Item Checkmark", typeof(RectTransform), typeof(Image));
            checkGo.transform.SetParent(item.transform, false);
            var checkRect = (RectTransform)checkGo.transform;
            checkRect.anchorMin = new Vector2(0f, 0.5f); checkRect.anchorMax = new Vector2(0f, 0.5f); checkRect.pivot = new Vector2(0f, 0.5f);
            checkRect.anchoredPosition = new Vector2(6f, 0f); checkRect.sizeDelta = new Vector2(10f, 10f);
            var checkImg = checkGo.GetComponent<Image>(); checkImg.color = UIFontConfig.Colors.ParchmentButtonInk;

            var itemLbl = ChildText(item.transform, "Item Label", 14f, UIFontConfig.Colors.ParchmentButtonInk, TextAlignmentOptions.MidlineLeft);
            var itemLabelRect = itemLbl.rectTransform; itemLabelRect.anchorMin = Vector2.zero; itemLabelRect.anchorMax = Vector2.one; itemLabelRect.offsetMin = new Vector2(22, 0); itemLabelRect.offsetMax = new Vector2(-6, 0);

            var toggle = item.GetComponent<Toggle>();
            toggle.targetGraphic = itemBg;
            toggle.graphic = checkImg;

            dropdown.template = templateRect; dropdown.captionText = cap; dropdown.itemText = itemLbl;
            template.SetActive(false);

            dropdown.ClearOptions();
            dropdown.AddOptions(options ?? new List<string>());
            dropdown.value = Mathf.Clamp(value, 0, Mathf.Max(0, (options?.Count ?? 1) - 1));
            dropdown.RefreshShownValue();
            dropdown.onValueChanged.AddListener(onChange);
            return dropdown;
        }

        private static TextMeshProUGUI ChildText(Transform parent, string name, float size, Color color, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<TextMeshProUGUI>();
            label.fontSize = size; label.color = color; label.alignment = align; label.raycastTarget = false; label.textWrappingMode = TextWrappingModes.NoWrap;
            return label;
        }

        private static TextMeshProUGUI MakeLabel(Transform parent, string name, string text, float size, Color color, TextAlignmentOptions align)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var label = go.AddComponent<TextMeshProUGUI>();
            label.text = text; label.fontSize = size; label.color = color; label.alignment = align; label.raycastTarget = false;
            return label;
        }

        // Drag-by-panel handler (ConfigPanel's private DragMove replicated — it isn't shared).
        private sealed class DragMove : MonoBehaviour, UnityEngine.EventSystems.IDragHandler
        {
            public RectTransform Target;
            public void OnDrag(UnityEngine.EventSystems.PointerEventData e)
            {
                if (Target == null) return;
                var canvas = GetComponentInParent<Canvas>();
                float scale = canvas != null && canvas.scaleFactor > 0f ? canvas.scaleFactor : 1f;
                Target.anchoredPosition += e.delta / scale;
            }
        }

        private static readonly object PanelToken = new object();

        public static void Close()
        {
            _box = null; _content = null; _panel = null;
            _sizeX = _sizeY = _sizeZ = null;
            FiresContextMenu.SuppressDriver = false;
            FiresCore.Input.FiresInputBlock.Release(PanelToken);
            if (_blocked) { InputBlock.Block(false); _blocked = false; }
            if (_canvasGo != null) { UnityEngine.Object.Destroy(_canvasGo); _canvasGo = null; }
        }

        // Keep the cursor freed while open and close on Esc (mirrors EntryDoorPanel). Client-only patch.
        [HarmonyPatch(typeof(Hud), "Update")]
        internal static class Hud_Update_Patch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (!IsOpen) return;
                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
                if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
                if (!Cursor.visible) Cursor.visible = true;
            }
        }

        // Force-close on logout so an open panel can't leak the static input blocks across relogin.
        [HarmonyPatch(typeof(ZNet), "Shutdown")]
        internal static class ZNet_Shutdown_PanelGuard
        {
            private static void Postfix()
            {
                if (IsClientOnly()) return;
                try { Close(); } catch { }
            }
        }
    }
}
