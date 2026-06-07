using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI
{
    // Flags for parser behaviour
    [Flags]
    public enum ParseFlags
    {
        None = 0,
        /// <summary>Walk ALL children recursively, even inside buttons/toggles/sliders/etc.</summary>
        DeepChildren = 1,
        /// <summary>Read legacy UnityEngine.UI.Text components and convert to UITextDef.</summary>
        LegacyText = 2,
        /// <summary>Capture inactive GameObjects as well.</summary>
        IncludeInactive = 4,
        /// <summary>Compute pixel rects from world corners (better for nested vanilla UIs).</summary>
        ResolvePixelRects = 8,
        /// <summary>Convenience combo for capturing vanilla Valheim UIs.</summary>
        Vanilla = DeepChildren | LegacyText | IncludeInactive | ResolvePixelRects
    }

    /// <summary>
    /// Reverse-engineers a live Unity UI GameObject hierarchy into a UILayoutDefinition.
    /// This is the inverse of UICanvasRenderer � it reads components from GameObjects
    /// and builds the data model tree.
    /// 
    /// Use cases:
    /// - Capture existing programmatic UIs (DialogueUI, InfoNpcPlayerPanel, etc.) into
    ///   editable layouts for the UI builder.
    /// - Save captured UIs as templates for admin customization.
    /// - "Import" any on-screen Unity UI into the builder system.
    /// 
    /// The parser walks the RectTransform hierarchy recursively, detecting component types
    /// and extracting all relevant properties into UIElementNode trees.
    /// </summary>
    public static class UILayoutParser
    {
        /// <summary>Default screen edge padding used when normalizing captured root transforms.</summary>
        private const float ScreenPadding = 15f;

        private static int _idCounter;
        private static ParseFlags _flags;
        private static bool _cacheAssets;

        /// <summary>
        /// Parses a live GameObject hierarchy into a UILayoutDefinition.
        /// The given root becomes the RootElement.
        /// </summary>
        /// <param name="root">The root GameObject to parse.</param>
        /// <param name="uid">UID for the resulting layout.</param>
        /// <param name="displayName">Human-readable name for the layout.</param>
        /// <param name="category">Category (e.g., "Dialogue", "Custom").</param>
        /// <param name="flags">Parse flags controlling behaviour (default: None).</param>
        /// <returns>A complete UILayoutDefinition, or null if root is null.</returns>
        public static UILayoutDefinition Parse(GameObject root, string uid, string displayName, string category = "Custom", ParseFlags flags = ParseFlags.None)
        {
            if (root == null) return null;

            _idCounter = 0;
            _flags = flags;

            // Always enable asset caching during capture so modded sprites on any UI
            // (including vanilla GOs modified by other mods) are saved to disk.
            // The IsSpriteFromMod() filter inside CacheSprite() decides what actually needs caching.
            if (!_cacheAssets)
                _cacheAssets = true;

            var layout = new UILayoutDefinition
            {
                UID = uid ?? "captured_" + DateTime.UtcNow.Ticks,
                DisplayName = displayName ?? root.name,
                Category = category ?? "Custom",
                Author = GetPlayerName(),
                CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ModifiedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                CanvasSize = new Vector2Ser(1920, 1080),
                Version = 1
            };

            layout.RootElement = ParseNode(root);
            layout.SetMeta("source", "captured");
            layout.SetMeta("source_object", root.name);
            if (flags != ParseFlags.None)
                layout.SetMeta("parse_flags", flags.ToString());

            _flags = ParseFlags.None;
            _cacheAssets = false;
            return layout;
        }

        /// <summary>
        /// Parses a vanilla Valheim UI (or any complex UI) using deep capture mode.
        /// Walks ALL children, handles legacy Text components, and captures inactive objects.
        /// </summary>
        public static UILayoutDefinition ParseVanilla(GameObject root, string uid, string displayName, string category = "Vanilla")
        {
            return Parse(root, uid, displayName, category, ParseFlags.Vanilla);
        }

        /// <summary>
        /// Parses a single GameObject (and all children) into a UIElementNode tree.
        /// Can be used to capture a sub-tree for templates or presets.
        /// </summary>
        public static UIElementNode ParseNode(GameObject go)
        {
            if (go == null) return null;

            var rect = go.GetComponent<RectTransform>();
            if (rect == null) return null;

            var node = new UIElementNode();
            node.Id = "cap_" + (_idCounter++);
            node.Name = go.name;
            node.Active = go.activeSelf;

            // Detect element type from components
            node.Type = DetectElementType(go, (_flags & ParseFlags.LegacyText) != 0);

            // Read RectTransform
            ReadTransform(rect, node);

            // Read components based on detected type.
            // Wrapped in try/catch because captured UIs may contain partially-initialised
            // TMP components (e.g. TMP_InputField.caretColor) that throw internally.
            try
            {
                ReadComponents(go, node);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutParser] ReadComponents failed on '{go.name}' ({node.Type}): {ex.Message}");
            }

            // Read layout components
            ReadLayoutGroup(go, node);
            ReadLayoutElement(go, node);
            ReadContentFitter(go, node);

            // Read masks
            node.HasMask = go.GetComponent<Mask>() != null;
            node.HasRectMask2D = go.GetComponent<RectMask2D>() != null;

            // Read interactable state
            var button = go.GetComponent<Button>();
            if (button != null) node.Interactable = button.interactable;
            var inputField = go.GetComponent<TMP_InputField>();
            if (inputField != null) node.Interactable = inputField.interactable;
            var toggle = go.GetComponent<Toggle>();
            if (toggle != null) node.Interactable = toggle.interactable;
            var slider = go.GetComponent<Slider>();
            if (slider != null) node.Interactable = slider.interactable;

            // Read style
            ReadStyle(go, node);

            // Read CanvasGroup opacity
            var canvasGroup = go.GetComponent<CanvasGroup>();
            if (canvasGroup != null && node.Style != null)
            {
                node.Style.Opacity = canvasGroup.alpha;
            }

            // Check for InventoryGrid component and synthesize grid layout
            ReadInventoryGrid(go, node);

            // Recurse into children
            bool deep = (_flags & ParseFlags.DeepChildren) != 0;
            if (node.Type == UIElementType.ScrollView)
            {
                ParseScrollViewChildren(go, node);
                // In deep mode, also capture non-content children (scrollbars, etc.)
                if (deep)
                    ParseChildrenExcluding(go, node, go.GetComponent<ScrollRect>());
            }
            else if (deep)
            {
                // Deep mode: walk ALL children even for buttons/toggles/sliders/etc.
                // For InventoryGrid containers, parse representative slots instead of all dynamic children
                if (ShouldParseInventoryGridChildren(go, node))
                    ParseInventoryGridChildren(go, node);
                else
                    ParseChildren(go, node);
            }
            else if (node.Type == UIElementType.Button)
            {
                // Button children (label text) are captured in ButtonData, skip them
            }
            else if (node.Type == UIElementType.InputField)
            {
                // InputField children are internal structure, skip them
            }
            else if (node.Type == UIElementType.Dropdown)
            {
                // Dropdown children are internal, skip them
            }
            else if (node.Type == UIElementType.Toggle)
            {
                // Toggle children are internal, skip them
            }
            else if (node.Type == UIElementType.Slider)
            {
                // Slider children are internal, skip them
            }
            else
            {
                ParseChildren(go, node);
            }

            return node;
        }

        // ???????????????????????????????????????
        //  Type detection
        // ???????????????????????????????????????

        private static UIElementType DetectElementType(GameObject go, bool legacyText = false)
        {
            // Check specific component types in order of specificity

            // TMP_InputField (check before TMP_Text, since InputField has child text)
            if (go.GetComponent<TMP_InputField>() != null)
                return UIElementType.InputField;

            // TMP_Dropdown (check before Button, since Dropdown has internal buttons)
            if (go.GetComponent<TMP_Dropdown>() != null)
                return UIElementType.Dropdown;

            // ScrollRect (ScrollView)
            if (go.GetComponent<ScrollRect>() != null)
                return UIElementType.ScrollView;

            // Toggle
            if (go.GetComponent<Toggle>() != null)
                return UIElementType.Toggle;

            // Slider
            if (go.GetComponent<Slider>() != null)
                return UIElementType.Slider;

            // Button (check before Image, since Button always has Image)
            if (go.GetComponent<Button>() != null)
                return UIElementType.Button;

            // Text (TMP_Text / TextMeshProUGUI � and NOT a child of something else)
            var tmpText = go.GetComponent<TMP_Text>();
            if (tmpText != null)
            {
                // Check if this is a standalone text element (not a label inside a button/etc.)
                var parentButton = go.transform.parent != null ? go.transform.parent.GetComponent<Button>() : null;
                var parentInput = go.transform.parent != null ? go.transform.parent.GetComponent<TMP_InputField>() : null;
                var parentDropdown = go.transform.parent != null ? go.transform.parent.GetComponent<TMP_Dropdown>() : null;
                if (parentButton == null && parentInput == null && parentDropdown == null)
                    return UIElementType.Text;
            }

            // Legacy UnityEngine.UI.Text (vanilla Valheim uses this in some UIs)
            if (legacyText)
            {
                var legacyTextComp = go.GetComponent<UnityEngine.UI.Text>();
                if (legacyTextComp != null)
                {
                    var parentBtn = go.transform.parent != null ? go.transform.parent.GetComponent<Button>() : null;
                    if (parentBtn == null)
                        return UIElementType.Text;
                }
            }

            // Scrollbar (vanilla Valheim uses standalone Scrollbar components)
            if (go.GetComponent<Scrollbar>() != null)
                return UIElementType.Panel; // Treat as panel container; children are the sliding area/handle

            // Image (without being a panel � a panel typically has children or layout groups)
            var image = go.GetComponent<Image>();
            if (image != null)
            {
                // Heuristic: if it has children, LayoutGroup, or is named like a panel ? Panel
                // If it's a thin element (h<=4 or w<=4) ? Divider
                var rect = go.GetComponent<RectTransform>();
                if (rect != null)
                {
                    var size = rect.sizeDelta;
                    bool isStretch = (rect.anchorMin != rect.anchorMax);
                    if (!isStretch && (size.y <= 4 || size.x <= 4))
                        return UIElementType.Divider;
                }

                bool hasChildren = go.transform.childCount > 0;
                bool hasLayoutGroup = go.GetComponent<HorizontalLayoutGroup>() != null ||
                                      go.GetComponent<VerticalLayoutGroup>() != null ||
                                      go.GetComponent<GridLayoutGroup>() != null;

                // Layout groups are always panels (they are containers)
                if (hasLayoutGroup)
                    return UIElementType.Panel;

                // Image with a sprite and children: treat as Panel so it gets
                // container semantics (children rendered inside it). The sprite data
                // is captured into both ImageData (via ReadImageComponent for all types)
                // and Style.BackgroundSprite (via ReadStyle). BuildPanel uses Style data
                // and also checks ImageData for detailed sprite properties.
                // Leaf images (no children) remain UIElementType.Image for proper rendering.
                if (image.sprite != null && hasChildren)
                    return UIElementType.Panel;

                // Leaf image with a sprite and no children � true Image element
                if (image.sprite != null)
                    return UIElementType.Image;

                // Has children but no sprite � container panel
                if (hasChildren)
                    return UIElementType.Panel;

                // Background-like image with no sprite � Panel
                return UIElementType.Panel;
            }

            // RawImage with a RenderTexture � camera preview element (e.g. AzuExtendedPlayerInventory character model)
            var rawImgCam = go.GetComponent<RawImage>();
            if (rawImgCam != null && rawImgCam.texture is RenderTexture)
                return UIElementType.RenderCamera;

            // RawImage � treat as Image type
            if (go.GetComponent<RawImage>() != null)
                return UIElementType.Image;

            // Empty LayoutElement with no visuals ? Spacer
            var layoutElem = go.GetComponent<LayoutElement>();
            if (layoutElem != null && go.GetComponent<Graphic>() == null && go.transform.childCount == 0)
                return UIElementType.Spacer;

            // Default: Panel (empty container)
            return UIElementType.Panel;
        }

        // ???????????????????????????????????????
        //  Transform reader
        // ???????????????????????????????????????

        private static void ReadTransform(RectTransform rect, UIElementNode node)
        {
            node.AnchorMin = Vector2Ser.From(rect.anchorMin);
            node.AnchorMax = Vector2Ser.From(rect.anchorMax);
            node.Pivot = Vector2Ser.From(rect.pivot);
            node.AnchoredPosition = Vector2Ser.From(rect.anchoredPosition);
            node.SizeDelta = Vector2Ser.From(rect.sizeDelta);
            node.OffsetMin = Vector2Ser.From(rect.offsetMin);
            node.OffsetMax = Vector2Ser.From(rect.offsetMax);

            var euler = rect.localRotation.eulerAngles;
            node.Rotation = euler.z;
        }

        // ???????????????????????????????????????
        //  Component readers
        // ???????????????????????????????????????

        private static void ReadComponents(GameObject go, UIElementNode node)
        {
            switch (node.Type)
            {
                case UIElementType.Panel:
                    // Panels that have an Image with a sprite need ImageData captured too.
                    // This happens when an Image with children is classified as Panel.
                    ReadImageComponent(go, node);
                    break;
                case UIElementType.Text:
                    ReadTextComponent(go, node);
                    break;
                case UIElementType.Image:
                    ReadImageComponent(go, node);
                    break;
                case UIElementType.RenderCamera:
                    ReadRenderCameraComponent(go, node);
                    break;
                case UIElementType.Button:
                    ReadButtonComponent(go, node);
                    break;
                case UIElementType.InputField:
                    ReadInputFieldComponent(go, node);
                    break;
                case UIElementType.ScrollView:
                    ReadScrollViewComponent(go, node);
                    break;
                case UIElementType.Dropdown:
                    ReadDropdownComponent(go, node);
                    break;
                case UIElementType.Toggle:
                    ReadToggleComponent(go, node);
                    break;
                case UIElementType.Slider:
                    ReadSliderComponent(go, node);
                    break;
            }

            // If TMP read failed, try legacy Text as fallback (vanilla Valheim)
            if ((_flags & ParseFlags.LegacyText) != 0 && node.TextData == null && node.Type == UIElementType.Text)
            {
                ReadLegacyTextComponent(go, node);
            }
        }

        private static void ReadTextComponent(GameObject go, UIElementNode node)
        {
            var tmp = go.GetComponent<TMP_Text>();
            if (tmp == null) return;

            node.TextData = new UITextDef
            {
                Text = tmp.text ?? "",
                FontSize = tmp.fontSize,
                FontCategory = DetectFontCategory(tmp),
                FontStyle = (int)tmp.fontStyle,
                Color = ColorSer.From(tmp.color),
                Alignment = (int)tmp.alignment,
                WordWrap = tmp.textWrappingMode != TextWrappingModes.NoWrap,
                OverflowMode = (int)tmp.overflowMode,
                LineSpacing = tmp.lineSpacing,
                TextPadding = new RectOffsetSer(
                    (int)tmp.margin.x, (int)tmp.margin.z,
                    (int)tmp.margin.w, (int)tmp.margin.y),
                ComponentEnabled = tmp.enabled
            };
        }

        /// <summary>
        /// Reads a legacy UnityEngine.UI.Text component and converts it to UITextDef.
        /// Used for vanilla Valheim UIs that don't use TextMeshPro.
        /// </summary>
        private static void ReadLegacyTextComponent(GameObject go, UIElementNode node)
        {
            var legacyText = go.GetComponent<UnityEngine.UI.Text>();
            if (legacyText == null) return;

            node.TextData = new UITextDef
            {
                Text = legacyText.text ?? "",
                FontSize = legacyText.fontSize,
                FontCategory = "Body",
                FontStyle = LegacyFontStyleToTMP(legacyText.fontStyle),
                Color = ColorSer.From(legacyText.color),
                Alignment = LegacyAlignmentToTMP(legacyText.alignment),
                WordWrap = legacyText.horizontalOverflow == HorizontalWrapMode.Wrap,
                OverflowMode = 0,
                LineSpacing = legacyText.lineSpacing,
                TextPadding = new RectOffsetSer(0, 0, 0, 0),
                ComponentEnabled = legacyText.enabled
            };
        }

        private static int LegacyFontStyleToTMP(FontStyle style)
        {
            switch (style)
            {
                case FontStyle.Bold: return (int)FontStyles.Bold;
                case FontStyle.Italic: return (int)FontStyles.Italic;
                case FontStyle.BoldAndItalic: return (int)(FontStyles.Bold | FontStyles.Italic);
                default: return (int)FontStyles.Normal;
            }
        }

        private static int LegacyAlignmentToTMP(TextAnchor anchor)
        {
            switch (anchor)
            {
                case TextAnchor.UpperLeft: return (int)TextAlignmentOptions.TopLeft;
                case TextAnchor.UpperCenter: return (int)TextAlignmentOptions.Top;
                case TextAnchor.UpperRight: return (int)TextAlignmentOptions.TopRight;
                case TextAnchor.MiddleLeft: return (int)TextAlignmentOptions.MidlineLeft;
                case TextAnchor.MiddleCenter: return (int)TextAlignmentOptions.Center;
                case TextAnchor.MiddleRight: return (int)TextAlignmentOptions.MidlineRight;
                case TextAnchor.LowerLeft: return (int)TextAlignmentOptions.BottomLeft;
                case TextAnchor.LowerCenter: return (int)TextAlignmentOptions.Bottom;
                case TextAnchor.LowerRight: return (int)TextAlignmentOptions.BottomRight;
                default: return (int)TextAlignmentOptions.MidlineLeft;
            }
        }

        private static void ReadImageComponent(GameObject go, UIElementNode node)
        {
            var img = go.GetComponent<Image>();
            if (img != null)
            {
                // For atlas sprites, try texture name + sprite name for uniqueness
                string spriteName = "";
                if (img.sprite != null)
                {
                    spriteName = img.sprite.name;
                    // If the sprite comes from an atlas and has a generic name, qualify it
                    if (img.sprite.texture != null && img.sprite.packed &&
                        !string.IsNullOrEmpty(img.sprite.texture.name) &&
                        img.sprite.texture.name != img.sprite.name)
                    {
                        // Store as "textureName:spriteName" for better lookup
                        spriteName = img.sprite.texture.name + ":" + img.sprite.name;
                    }

                    // Record which asset bundle this sprite came from for targeted loading.
                    // Do this FIRST so the bundle can be cached and the manifest created
                    // before we decide whether to cache the sprite as a PNG.
                    string srcBundle = null;
                    if (_cacheAssets)
                    {
                        srcBundle = UIBuilderAssetCache.FindBundleContainingSprite(img.sprite);
                        if (!string.IsNullOrEmpty(srcBundle))
                            node.SetMeta("source_bundle", srcBundle);
                    }

                    // Prioritize the bundle sprite path: if the sprite lives in a known
                    // asset bundle AND that bundle is already indexed or can be cached as
                    // raw data, we can reload the real sprite later with full metadata
                    // (9-slice borders, pivot, PPU, atlas rects). The PNG fallback loses
                    // all of that metadata and produces a flat rasterized sprite.
                    //
                    // Only fall back to CacheSprite (GPU screenshot) when:
                    //   - The sprite is NOT in any loaded asset bundle, OR
                    //   - The bundle cannot be cached (raw bytes not found on disk or in
                    //     embedded resources)
                    if (_cacheAssets)
                    {
                        bool hasBundlePath = !string.IsNullOrEmpty(srcBundle);
                        bool bundleAlreadyCached = false;

                        if (hasBundlePath)
                        {
                            // Check if the bundle manifest already exists (raw data is on disk)
                            bundleAlreadyCached = UIBundleManifestManager.GetManifest(srcBundle) != null;

                            // If not cached yet, the CacheRequiredBundlesToDisk call in
                            // AggregateRequiredBundles will handle it after capture completes.
                            // For now, just check if the sprite name can be resolved from
                            // the indexed bundle sprites.
                            if (!bundleAlreadyCached)
                            {
                                var bundleSprite = UIBuilderAssetCache.LoadSpriteFromCachedBundle(spriteName);
                                if (bundleSprite != null)
                                    bundleAlreadyCached = true;
                            }
                        }

                        if (!hasBundlePath || !bundleAlreadyCached)
                        {
                            // No bundle path or bundle not cached � use PNG fallback
                            spriteName = UIBuilderAssetCache.CacheSprite(img.sprite, spriteName);
                        }
                        // else: sprite is in a bundle that's cached or will be cached �
                        // keep the original sprite name for bundle-based resolution at load time
                    }
                }
                node.ImageData = new UIImageDef
                {
                    SpriteName = spriteName,
                    ImageType = (int)img.type,
                    PreserveAspect = img.preserveAspect,
                    Color = ColorSer.From(img.color),
                    FillCenter = img.fillCenter,
                    PixelsPerUnit = img.pixelsPerUnitMultiplier,
                    ComponentEnabled = img.enabled
                };
                return;
            }

            // RawImage fallback
            var rawImg = go.GetComponent<RawImage>();
            if (rawImg != null)
            {
                string rawTexName = rawImg.texture != null ? rawImg.texture.name : "";

                // Cache RawImage textures from mods so they survive mod removal
                if (_cacheAssets && rawImg.texture is Texture2D)
                {
                    rawTexName = UIBuilderAssetCache.CacheRawImageTexture(rawImg, rawTexName);
                }

                node.ImageData = new UIImageDef
                {
                    SpriteName = rawTexName,
                    Color = ColorSer.From(rawImg.color),
                    ComponentEnabled = rawImg.enabled
                };
            }
        }

        private static void ReadRenderCameraComponent(GameObject go, UIElementNode node)
        {
            node.RenderCameraData = new UIRenderCameraDef();

            var rawImg = go.GetComponent<RawImage>();
            if (rawImg != null)
            {
                var rt = rawImg.texture as RenderTexture;
                if (rt != null)
                {
                    node.RenderCameraData.TextureWidth = rt.width;
                    node.RenderCameraData.TextureHeight = rt.height;
                }
                node.RenderCameraData.BackgroundColor = ColorSer.From(rawImg.color);
            }

            // Try to detect mode from context and record render source metadata
            string goName = go.name.ToLowerInvariant();
            if (goName.Contains("player") || goName.Contains("character") || goName.Contains("model"))
            {
                node.RenderCameraData.Mode = "player";
                node.SetMeta("render_source", "player_model");
            }
            else if (goName.Contains("minimap") || goName.Contains("muimap") || goName.Contains("map"))
            {
                node.RenderCameraData.Mode = "world";
                node.SetMeta("render_source", "minimap");
            }
            else
            {
                node.RenderCameraData.Mode = "world";
            }
        }

        private static void ReadButtonComponent(GameObject go, UIElementNode node)
        {
            var btn = go.GetComponent<Button>();
            var img = go.GetComponent<Image>();
            if (btn == null) return;

            var colors = btn.colors;
            node.ButtonData = new UIButtonDef
            {
                NormalColor = ColorSer.From(colors.normalColor),
                HighlightedColor = ColorSer.From(colors.highlightedColor),
                PressedColor = ColorSer.From(colors.pressedColor),
                SelectedColor = ColorSer.From(colors.selectedColor),
                DisabledColor = ColorSer.From(colors.disabledColor),
                FadeDuration = colors.fadeDuration
            };

            // Capture the actual Image.color separately from Button.colors.
            // Unity multiplies Image.color � Button.colors.normalColor for the final visual.
            // If Image.color has alpha=0 the button is invisible, even if normalColor is opaque.
            // We also capture the Image sprite data into ImageData so it can be applied during rendering.
            if (img != null)
            {
                string spriteName = "";
                if (img.sprite != null)
                {
                    spriteName = img.sprite.name;
                    if (img.sprite.texture != null && img.sprite.packed &&
                        !string.IsNullOrEmpty(img.sprite.texture.name) &&
                        img.sprite.texture.name != img.sprite.name)
                    {
                        spriteName = img.sprite.texture.name + ":" + img.sprite.name;
                    }
                    if (_cacheAssets)
                    {
                        spriteName = UIBuilderAssetCache.CacheSprite(img.sprite, spriteName);
                        string srcBundle = UIBuilderAssetCache.FindBundleContainingSprite(img.sprite);
                        if (!string.IsNullOrEmpty(srcBundle))
                            node.SetMeta("source_bundle", srcBundle);
                    }
                }
                node.ImageData = new UIImageDef
                {
                    SpriteName = spriteName,
                    ImageType = (int)img.type,
                    PreserveAspect = img.preserveAspect,
                    Color = ColorSer.From(img.color),
                    FillCenter = img.fillCenter,
                    PixelsPerUnit = img.pixelsPerUnitMultiplier,
                    ComponentEnabled = img.enabled
                };
            }

            // Find label text in children (TMP first, then legacy)
            var labelTmp = FindChildText(go);
            if (labelTmp != null)
            {
                node.ButtonData.Label = labelTmp.text ?? "Button";
                node.ButtonData.FontSize = labelTmp.fontSize;
                node.ButtonData.LabelColor = ColorSer.From(labelTmp.color);
                node.ButtonData.LabelAlignment = (int)labelTmp.alignment;

                // Capture label padding from layout group, TMP margin, or RectTransform offsets
                var parentHlg = go.GetComponent<HorizontalLayoutGroup>();
                var parentVlg = go.GetComponent<VerticalLayoutGroup>();
                if (parentHlg != null)
                    node.ButtonData.LabelPadding = RectOffsetSer.From(parentHlg.padding);
                else if (parentVlg != null)
                    node.ButtonData.LabelPadding = RectOffsetSer.From(parentVlg.padding);
                else
                {
                    // Try to read from the label's RectTransform offsets (vanilla buttons use this)
                    var labelRect = labelTmp.GetComponent<RectTransform>();
                    if (labelRect != null &&
                        Mathf.Approximately(labelRect.anchorMin.x, 0) && Mathf.Approximately(labelRect.anchorMin.y, 0) &&
                        Mathf.Approximately(labelRect.anchorMax.x, 1) && Mathf.Approximately(labelRect.anchorMax.y, 1))
                    {
                        node.ButtonData.LabelPadding = new RectOffsetSer(
                            (int)labelRect.offsetMin.x, (int)(-labelRect.offsetMax.x),
                            (int)(-labelRect.offsetMax.y), (int)labelRect.offsetMin.y);
                    }
                    else
                    {
                        node.ButtonData.LabelPadding = new RectOffsetSer(
                            (int)labelTmp.margin.x, (int)labelTmp.margin.z,
                            (int)labelTmp.margin.w, (int)labelTmp.margin.y);
                    }
                }
            }
            else if ((_flags & ParseFlags.LegacyText) != 0)
            {
                // Try legacy UnityEngine.UI.Text for vanilla buttons
                var legacyLabel = FindChildLegacyText(go);
                if (legacyLabel != null)
                {
                    node.ButtonData.Label = legacyLabel.text ?? "Button";
                    node.ButtonData.FontSize = legacyLabel.fontSize;
                    node.ButtonData.LabelColor = ColorSer.From(legacyLabel.color);
                    node.ButtonData.LabelAlignment = LegacyAlignmentToTMP(legacyLabel.alignment);
                    node.ButtonData.LabelPadding = new RectOffsetSer(0, 0, 0, 0);
                }
            }

            // Try to read existing UIBuilderElementTag for action bindings
            var tag = go.GetComponent<UIBuilderElementTag>();
            if (tag != null && tag.Node != null && tag.Node.ButtonData != null)
            {
                node.ButtonData.ClickAction = tag.Node.ButtonData.ClickAction;
                node.ButtonData.ClickActionParam = tag.Node.ButtonData.ClickActionParam;
            }

            // If no action from tag, try to read persistent onClick listeners via reflection.
            // Unity's Button.onClick stores persistent calls (set in the inspector or AddListener)
            // as entries with a target object and method name. We capture these as metadata so
            // the builder user can see what the button was wired to do in the original UI.
            if (string.IsNullOrEmpty(node.ButtonData.ClickAction))
            {
                try
                {
                    var onClick = btn.onClick;
                    int persistentCount = onClick.GetPersistentEventCount();
                    if (persistentCount > 0)
                    {
                        var actionParts = new List<string>();
                        for (int pi = 0; pi < persistentCount; pi++)
                        {
                            var target = onClick.GetPersistentTarget(pi);
                            string methodName = onClick.GetPersistentMethodName(pi);
                            if (!string.IsNullOrEmpty(methodName))
                            {
                                string targetType = target != null ? target.GetType().Name : "?";
                                actionParts.Add($"{targetType}.{methodName}");
                            }
                        }
                        if (actionParts.Count > 0)
                        {
                            string captured = string.Join("; ", actionParts.ToArray());
                            node.SetMeta("captured_onClick", captured);
                            // Use the first listener's method name as a hint for the action
                            node.ButtonData.ClickAction = "captured:" + actionParts[0];
                            Debug.Log($"[UILayoutParser] Captured onClick for '{go.name}': {captured}");
                        }
                    }
                }
                catch { /* reflection may fail on some button types */ }
            }
        }

        private static void ReadInputFieldComponent(GameObject go, UIElementNode node)
        {
            var input = go.GetComponent<TMP_InputField>();
            if (input == null) return;

            node.InputFieldData = new UIInputFieldDef
            {
                FontSize = input.pointSize,
                Multiline = input.lineType != TMP_InputField.LineType.SingleLine,
                CharacterLimit = input.characterLimit,
                ContentType = ContentTypeToString(input.contentType)
            };

            // caretColor and selectionColor can throw NullReferenceException on
            // TMP_InputField instances whose internal graphic references haven't
            // been initialised (common with prefabs and captured inactive UIs).
            try { node.InputFieldData.CaretColor = ColorSer.From(input.caretColor); }
            catch { node.InputFieldData.CaretColor = new ColorSer(1f, 1f, 1f, 1f); }

            try { node.InputFieldData.SelectionColor = ColorSer.From(input.selectionColor); }
            catch { node.InputFieldData.SelectionColor = new ColorSer(0.6f, 0.4f, 0.2f, 0.4f); }

            // Read text color and alignment from text component
            if (input.textComponent != null)
            {
                node.InputFieldData.TextColor = ColorSer.From(input.textComponent.color);
                node.InputFieldData.TextAlignment = (int)input.textComponent.alignment;
            }

            // Read text padding from viewport offset
            if (input.textViewport != null)
            {
                var vpOffset = input.textViewport.offsetMin;
                var vpOffset2 = input.textViewport.offsetMax;
                node.InputFieldData.TextPadding = new RectOffsetSer(
                    (int)vpOffset.x, (int)Mathf.Abs(vpOffset2.x),
                    (int)Mathf.Abs(vpOffset2.y), (int)vpOffset.y);
            }

            // Read placeholder
            try
            {
                var placeholder = input.placeholder as TMP_Text;
                if (placeholder != null)
                {
                    node.InputFieldData.PlaceholderText = placeholder.text ?? "";
                    node.InputFieldData.PlaceholderColor = ColorSer.From(placeholder.color);
                }
            }
            catch { /* placeholder graphic may not be initialised */ }

            // Read background color
            var bgImg = go.GetComponent<Image>();
            if (bgImg != null)
                node.InputFieldData.BackgroundColor = ColorSer.From(bgImg.color);
        }

        private static void ReadScrollViewComponent(GameObject go, UIElementNode node)
        {
            var scroll = go.GetComponent<ScrollRect>();
            if (scroll == null) return;

            node.ScrollViewData = new UIScrollViewDef
            {
                Horizontal = scroll.horizontal,
                Vertical = scroll.vertical,
                MovementType = (int)scroll.movementType,
                ScrollSensitivity = scroll.scrollSensitivity,
                Elasticity = scroll.elasticity
            };

            // Read viewport color � viewport reference may be destroyed
            try
            {
                if (scroll.viewport != null)
                {
                    var vpImg = scroll.viewport.GetComponent<Image>();
                    if (vpImg != null)
                        node.ScrollViewData.ViewportColor = ColorSer.From(vpImg.color);
                }
            }
            catch { /* viewport may reference a destroyed object */ }
        }

        private static void ReadDropdownComponent(GameObject go, UIElementNode node)
        {
            var dropdown = go.GetComponent<TMP_Dropdown>();
            if (dropdown == null) return;

            node.DropdownData = new UIDropdownDef
            {
                DefaultValue = dropdown.value
            };

            // Read options
            try
            {
                if (dropdown.options != null)
                {
                    node.DropdownData.Options = new List<string>();
                    for (int i = 0; i < dropdown.options.Count; i++)
                        node.DropdownData.Options.Add(dropdown.options[i].text ?? "");
                }
            }
            catch { /* options list may not be initialised */ }

            // Read caption text
            try
            {
                if (dropdown.captionText != null)
                {
                    node.DropdownData.FontSize = dropdown.captionText.fontSize;
                    node.DropdownData.TextColor = ColorSer.From(dropdown.captionText.color);
                }
            }
            catch { /* captionText may reference a destroyed object */ }

            // Read background
            var bgImg = go.GetComponent<Image>();
            if (bgImg != null)
                node.DropdownData.BackgroundColor = ColorSer.From(bgImg.color);

            // Read template height
            try
            {
                if (dropdown.template != null)
                    node.DropdownData.TemplateHeight = dropdown.template.sizeDelta.y;
            }
            catch { /* template may reference a destroyed object */ }
        }

        private static void ReadToggleComponent(GameObject go, UIElementNode node)
        {
            var toggle = go.GetComponent<Toggle>();
            if (toggle == null) return;

            node.ToggleData = new UIToggleDef
            {
                DefaultValue = toggle.isOn
            };

            // Read background color
            var bgImg = go.GetComponent<Image>();
            if (bgImg != null)
                node.ToggleData.BackgroundColor = ColorSer.From(bgImg.color);

            // Read checkmark color � graphic reference may be destroyed/null internally
            try
            {
                if (toggle.graphic != null)
                {
                    var checkTmp = toggle.graphic as TMP_Text;
                    if (checkTmp != null)
                        node.ToggleData.CheckmarkColor = ColorSer.From(checkTmp.color);
                    else
                        node.ToggleData.CheckmarkColor = ColorSer.From(toggle.graphic.color);
                }
            }
            catch { /* graphic may reference a destroyed object */ }

            // Find label
            var labelTmp = FindLabelText(go);
            if (labelTmp != null)
            {
                node.ToggleData.Label = labelTmp.text ?? "";
                node.ToggleData.FontSize = labelTmp.fontSize;
                node.ToggleData.LabelColor = ColorSer.From(labelTmp.color);
            }
        }

        private static void ReadSliderComponent(GameObject go, UIElementNode node)
        {
            var slider = go.GetComponent<Slider>();
            if (slider == null) return;

            node.SliderData = new UISliderDef
            {
                MinValue = slider.minValue,
                MaxValue = slider.maxValue,
                DefaultValue = slider.value,
                WholeNumbers = slider.wholeNumbers
            };

            // Read colors from child elements � references may be destroyed
            try
            {
                if (slider.fillRect != null)
                {
                    var fillImg = slider.fillRect.GetComponent<Image>();
                    if (fillImg != null)
                        node.SliderData.FillColor = ColorSer.From(fillImg.color);
                }
            }
            catch { /* fillRect may reference a destroyed object */ }

            try
            {
                if (slider.handleRect != null)
                {
                    var handleImg = slider.handleRect.GetComponent<Image>();
                    if (handleImg != null)
                        node.SliderData.HandleColor = ColorSer.From(handleImg.color);
                }
            }
            catch { /* handleRect may reference a destroyed object */ }

            // Background from first child Image
            try
            {
                var bg = go.GetComponentInChildren<Image>();
                if (bg != null && bg.gameObject != go)
                    node.SliderData.BackgroundColor = ColorSer.From(bg.color);
            }
            catch { /* child image may have been destroyed */ }
        }

        // ???????????????????????????????????????
        //  Style reader
        // ???????????????????????????????????????

        private static void ReadStyle(GameObject go, UIElementNode node)
        {
            node.Style = new UIElementStyle
            {
                Opacity = 1f,
                RaycastTarget = true,
                Shape = UIShapeType.Rectangle
            };

            var img = go.GetComponent<Image>();
            if (img != null)
            {
                node.Style.BackgroundColor = ColorSer.From(img.color);
                node.Style.RaycastTarget = img.raycastTarget;
                node.Style.ImageType = (int)img.type;
                if (img.sprite != null)
                {
                    string spriteName = img.sprite.name;
                    // Qualify atlas sprites with texture name for better modded UI support
                    if (img.sprite.texture != null && img.sprite.packed &&
                        !string.IsNullOrEmpty(img.sprite.texture.name) &&
                        img.sprite.texture.name != img.sprite.name)
                    {
                        spriteName = img.sprite.texture.name + ":" + img.sprite.name;
                    }

                    if (_cacheAssets)
                    {
                        // Record which asset bundle this sprite came from for targeted loading
                        string srcBundle = UIBuilderAssetCache.FindBundleContainingSprite(img.sprite);
                        if (!string.IsNullOrEmpty(srcBundle))
                            node.SetMeta("source_bundle", srcBundle);

                        // Prioritize bundle sprite path over GPU screenshot fallback.
                        // If the sprite lives in a known bundle that's cached or indexed,
                        // keep the original sprite name for bundle-based resolution.
                        bool hasBundlePath = !string.IsNullOrEmpty(srcBundle);
                        bool bundleAvailable = false;

                        if (hasBundlePath)
                        {
                            bundleAvailable = UIBundleManifestManager.GetManifest(srcBundle) != null;
                            if (!bundleAvailable)
                            {
                                var bundleSprite = UIBuilderAssetCache.LoadSpriteFromCachedBundle(spriteName);
                                if (bundleSprite != null)
                                    bundleAvailable = true;
                            }
                        }

                        if (!hasBundlePath || !bundleAvailable)
                        {
                            // No bundle path or bundle not available � use PNG fallback
                            spriteName = UIBuilderAssetCache.CacheSprite(img.sprite, spriteName);
                        }
                    }
                    node.Style.BackgroundSprite = spriteName;
                }
            }
            else
            {
                // No Image � transparent background
                node.Style.BackgroundColor = ColorSer.Clear;
                // Check if any Graphic exists for raycast
                var graphic = go.GetComponent<Graphic>();
                node.Style.RaycastTarget = graphic != null ? graphic.raycastTarget : false;
            }
        }

        // ???????????????????????????????????????
        //  Layout component readers
        // ???????????????????????????????????????

        private static void ReadLayoutGroup(GameObject go, UIElementNode node)
        {
            var vlg = go.GetComponent<VerticalLayoutGroup>();
            if (vlg != null)
            {
                node.LayoutGroup = new UILayoutGroupDef
                {
                    IsVertical = true,
                    Spacing = vlg.spacing,
                    Padding = RectOffsetSer.From(vlg.padding),
                    ChildAlignment = (int)vlg.childAlignment,
                    ChildControlWidth = vlg.childControlWidth,
                    ChildControlHeight = vlg.childControlHeight,
                    ChildForceExpandWidth = vlg.childForceExpandWidth,
                    ChildForceExpandHeight = vlg.childForceExpandHeight
                };
                return;
            }

            var hlg = go.GetComponent<HorizontalLayoutGroup>();
            if (hlg != null)
            {
                node.LayoutGroup = new UILayoutGroupDef
                {
                    IsVertical = false,
                    Spacing = hlg.spacing,
                    Padding = RectOffsetSer.From(hlg.padding),
                    ChildAlignment = (int)hlg.childAlignment,
                    ChildControlWidth = hlg.childControlWidth,
                    ChildControlHeight = hlg.childControlHeight,
                    ChildForceExpandWidth = hlg.childForceExpandWidth,
                    ChildForceExpandHeight = hlg.childForceExpandHeight
                };
                return;
            }

            // GridLayoutGroup � used by inventory/crafting slot grids
            var glg = go.GetComponent<GridLayoutGroup>();
            if (glg != null)
            {
                node.GridLayoutGroup = new UIGridLayoutGroupDef
                {
                    CellSize = Vector2Ser.From(glg.cellSize),
                    Spacing = Vector2Ser.From(glg.spacing),
                    StartCorner = (int)glg.startCorner,
                    StartAxis = (int)glg.startAxis,
                    ChildAlignment = (int)glg.childAlignment,
                    Constraint = (int)glg.constraint,
                    ConstraintCount = glg.constraintCount,
                    Padding = RectOffsetSer.From(glg.padding)
                };
            }
        }

        private static void ReadLayoutElement(GameObject go, UIElementNode node)
        {
            var le = go.GetComponent<LayoutElement>();
            if (le == null) return;

            node.LayoutElement = new UILayoutElementDef
            {
                MinWidth = le.minWidth,
                MinHeight = le.minHeight,
                PreferredWidth = le.preferredWidth,
                PreferredHeight = le.preferredHeight,
                FlexibleWidth = le.flexibleWidth,
                FlexibleHeight = le.flexibleHeight,
                IgnoreLayout = le.ignoreLayout
            };
        }

        private static void ReadContentFitter(GameObject go, UIElementNode node)
        {
            var fitter = go.GetComponent<ContentSizeFitter>();
            if (fitter == null) return;

            node.ContentFitter = new UIContentFitterDef
            {
                HorizontalFit = (int)fitter.horizontalFit,
                VerticalFit = (int)fitter.verticalFit
            };
        }

        // ???????????????????????????????????????
        //  InventoryGrid capture
        // ???????????????????????????????????????

        /// <summary>
        /// Resolves the authoritative grid dimensions for an InventoryGrid.
        /// Checks multiple sources in priority order:
        ///   1. The InventoryGrid's bound m_inventory object (set by UpdateInventory)
        ///   2. The local player's live Inventory (for PlayerGrid when m_inventory is stale/null)
        ///   3. The InventoryGrid's cached m_width/m_height fields (set after UpdateGui)
        ///   4. Fallback defaults (8x4 for player grids, 4x4 for others)
        /// Returns (width, height).
        /// </summary>
        private static (int width, int height) ResolveInventoryGridDimensions(InventoryGrid invGrid)
        {
            int gridWidth = 0, gridHeight = 0;

            // Read cached m_width/m_height (set after UpdateGui, defaults to 4x4)
            var widthField = typeof(InventoryGrid).GetField("m_width", BindingFlags.NonPublic | BindingFlags.Instance);
            var heightField = typeof(InventoryGrid).GetField("m_height", BindingFlags.NonPublic | BindingFlags.Instance);
            int cachedW = 0, cachedH = 0;
            if (widthField != null) cachedW = (int)widthField.GetValue(invGrid);
            if (heightField != null) cachedH = (int)heightField.GetValue(invGrid);

            // Try the InventoryGrid's bound Inventory object
            var invField = typeof(InventoryGrid).GetField("m_inventory", BindingFlags.NonPublic | BindingFlags.Instance);
            Inventory boundInv = null;
            if (invField != null)
                boundInv = invField.GetValue(invGrid) as Inventory;

            if (boundInv != null)
            {
                int invW = boundInv.GetWidth();
                int invH = boundInv.GetHeight();
                if (invW > 0 && invH > 0)
                {
                    gridWidth = invW;
                    gridHeight = invH;
                }
            }

            // For the player inventory grid, the bound m_inventory may be null or stale
            // (e.g., InventoryGui hasn't been shown yet, or AzuEPI hasn't expanded it yet).
            // Try reading directly from the player's live Inventory which is always authoritative.
            if (Player.m_localPlayer != null)
            {
                bool isPlayerGrid = false;
                string goName = invGrid.gameObject.name.ToLowerInvariant();
                if (goName.Contains("player"))
                    isPlayerGrid = true;
                else if (InventoryGui.instance != null)
                {
                    // Check if this is the m_playerGrid reference
                    try { isPlayerGrid = (invGrid == InventoryGui.instance.m_playerGrid); } catch { }
                }

                if (isPlayerGrid)
                {
                    // Ensure slot discovery has run so InventoryHeight is accurate
                    UIOverrideSlotSystem.DiscoverSlotsFromGameState();

                    try
                    {
                        var playerInv = Player.m_localPlayer.GetInventory();
                        if (playerInv != null)
                        {
                            int pW = playerInv.GetWidth();
                            // GetHeight() returns the FULL extended height including hidden
                            // equipment rows. For capture purposes we only want the VISIBLE
                            // rows (vanilla 4 + extra rows from inventory mods). The
                            // UIOverrideSlotSystem reads the real visible height via reflection.
                            int pH = UIOverrideSlotSystem.InventoryHeight;
                            if (pH <= 0) pH = 4; // vanilla fallback
                            if (pW > 0 && pH > 0)
                            {
                                gridWidth = pW;
                                gridHeight = pH;
                            }
                        }
                    }
                    catch { }
                }
            }

            // Fall back to cached fields if still 0
            if (gridWidth <= 0 && cachedW > 0) gridWidth = cachedW;
            if (gridHeight <= 0 && cachedH > 0) gridHeight = cachedH;

            // Final fallback � standard Valheim player inventory is 8x4
            if (gridWidth <= 0) gridWidth = 8;
            if (gridHeight <= 0) gridHeight = 4;

            return (gridWidth, gridHeight);
        }

        /// <summary>
        /// For an InventoryGrid with 0 live children in its m_gridRoot, try to force-populate
        /// the grid by binding the player's inventory and calling UpdateInventory.
        /// This makes the slots exist so we can capture real slot GOs with actual item sprites
        /// instead of falling back to the empty prefab template.
        /// Returns true if the grid was force-populated.
        /// </summary>
        private static bool TryForcePopulateGrid(InventoryGrid invGrid)
        {
            if (invGrid == null || invGrid.m_gridRoot == null) return false;
            if (invGrid.m_gridRoot.childCount > 0) return false; // Already has children
            if (Player.m_localPlayer == null) return false;

            try
            {
                // Check if this is a player grid
                bool isPlayerGrid = false;
                string goName = invGrid.gameObject.name.ToLowerInvariant();
                if (goName.Contains("player"))
                    isPlayerGrid = true;
                else if (InventoryGui.instance != null)
                {
                    try { isPlayerGrid = (invGrid == InventoryGui.instance.m_playerGrid); } catch { }
                }

                if (!isPlayerGrid) return false;

                var playerInv = Player.m_localPlayer.GetInventory();
                if (playerInv == null) return false;

                // Call UpdateInventory to create the slot GameObjects
                invGrid.UpdateInventory(playerInv, Player.m_localPlayer, null);
                Debug.Log($"[UILayoutParser] Force-populated InventoryGrid '{invGrid.gameObject.name}' with {invGrid.m_gridRoot.childCount} slots via UpdateInventory");
                return invGrid.m_gridRoot.childCount > 0;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutParser] TryForcePopulateGrid failed on '{invGrid.gameObject.name}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Detects InventoryGrid components and synthesizes a UIGridLayoutGroupDef
        /// from the runtime grid properties. Valheim's InventoryGrid doesn't use a
        /// Unity GridLayoutGroup � it positions slot elements via code using m_elementSpace.
        /// This method reads those properties and creates an equivalent GridLayoutGroup
        /// definition so the UI builder can display and edit the grid structure.
        /// </summary>
        private static void ReadInventoryGrid(GameObject go, UIElementNode node)
        {
            var invGrid = go.GetComponent<InventoryGrid>();
            if (invGrid == null) return;

            try
            {
                // Read the grid root RectTransform � this is where slot elements live
                var gridRoot = invGrid.m_gridRoot;
                float elementSpace = invGrid.m_elementSpace;

                // Get authoritative dimensions (handles stale m_inventory, AzuEPI, etc.)
                var (gridWidth, gridHeight) = ResolveInventoryGridDimensions(invGrid);

                // If the grid root has 0 children, try to force-populate it
                // so ParseInventoryGridChildren captures real slots with item sprites.
                if (gridRoot != null && gridRoot.childCount == 0)
                    TryForcePopulateGrid(invGrid);

                // Tag the node as an inventory grid container
                if (string.IsNullOrEmpty(node.Tag))
                {
                    string goName = go.name.ToLowerInvariant();
                    if (goName.Contains("player") || goName.Contains("inventory"))
                        node.Tag = "player_inventory_grid";
                    else if (goName.Contains("container"))
                        node.Tag = "container_grid";
                    else
                        node.Tag = "inventory_grid";
                }

                // Store grid metadata
                node.SetMeta("inventory_grid", "true");
                node.SetMeta("grid_width", gridWidth.ToString());
                node.SetMeta("grid_height", gridHeight.ToString());
                node.SetMeta("element_space", elementSpace.ToString("F1"));

                // If the grid root child exists, synthesize a GridLayoutGroup on it
                if (gridRoot != null)
                {
                    // Find the grid root node in children after they're parsed
                    // We'll set a flag so ParseInventoryGridChildren picks it up
                    gridRoot.gameObject.name = gridRoot.gameObject.name; // ensure it exists
                }

                Debug.Log($"[UILayoutParser] Detected InventoryGrid on '{go.name}': {gridWidth}x{gridHeight}, spacing={elementSpace}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutParser] ReadInventoryGrid failed on '{go.name}': {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if a GameObject is a grid root inside an InventoryGrid that should
        /// have its children parsed specially (representative slots + synthesized GridLayoutGroup).
        /// </summary>
        private static bool ShouldParseInventoryGridChildren(GameObject go, UIElementNode node)
        {
            // Check if parent has an InventoryGrid whose m_gridRoot points to this GO
            if (go.transform.parent == null) return false;
            var parentInvGrid = go.transform.parent.GetComponent<InventoryGrid>();
            if (parentInvGrid != null && parentInvGrid.m_gridRoot == go.GetComponent<RectTransform>())
                return true;

            // Also check if this GO itself has InventoryGrid (the grid root is a child)
            var invGrid = go.GetComponent<InventoryGrid>();
            if (invGrid != null) return false; // The InventoryGrid GO itself isn't the grid root

            return false;
        }

        /// <summary>
        /// Parses children of an InventoryGrid's m_gridRoot. Instead of capturing all N�M
        /// dynamically-created slot elements (which would be hundreds of near-identical nodes),
        /// this captures a representative sample of slots and synthesizes a GridLayoutGroup
        /// so the builder can display the grid structure properly.
        ///
        /// KEY INSIGHT: InventoryGrid creates slot GameObjects dynamically in UpdateGui().
        /// If the inventory hasn't been opened yet (or the grid was just created), the grid
        /// root will have 0 children. In that case, we parse the m_elementPrefab as a
        /// template slot and duplicate it to fill the grid structure.
        /// </summary>
        private static void ParseInventoryGridChildren(GameObject go, UIElementNode node)
        {
            // Get the parent InventoryGrid component
            var parentInvGrid = go.transform.parent != null
                ? go.transform.parent.GetComponent<InventoryGrid>()
                : null;

            if (parentInvGrid == null)
            {
                // Fallback to normal parsing
                ParseChildren(go, node);
                return;
            }

            try
            {
                float elementSpace = parentInvGrid.m_elementSpace;

                // Get authoritative dimensions using the shared resolver
                var (gridWidth, gridHeight) = ResolveInventoryGridDimensions(parentInvGrid);

                // If grid root has 0 children, try to force-populate before falling back to prefab
                if (go.transform.childCount == 0)
                    TryForcePopulateGrid(parentInvGrid);

                // Synthesize a GridLayoutGroup definition for this grid root
                float cellSize = elementSpace;
                var elementPrefab = parentInvGrid.m_elementPrefab;
                if (elementPrefab != null)
                {
                    var prefabRect = elementPrefab.GetComponent<RectTransform>();
                    if (prefabRect != null)
                        cellSize = Mathf.Max(prefabRect.sizeDelta.x, prefabRect.sizeDelta.y);
                }

                float spacing = Mathf.Max(0, elementSpace - cellSize);

                node.GridLayoutGroup = new UIGridLayoutGroupDef
                {
                    CellSize = new Vector2Ser(cellSize, cellSize),
                    Spacing = new Vector2Ser(spacing, spacing),
                    StartCorner = 0,
                    StartAxis = 0,
                    ChildAlignment = 0,
                    Constraint = 1, // FixedColumnCount
                    ConstraintCount = gridWidth,
                    Padding = new RectOffsetSer(0, 0, 0, 0)
                };

                if (string.IsNullOrEmpty(node.Tag))
                    node.Tag = "grid_root";
                node.SetMeta("inventory_grid_root", "true");
                node.SetMeta("grid_width", gridWidth.ToString());
                node.SetMeta("grid_height", gridHeight.ToString());
                node.SetMeta("element_space", elementSpace.ToString("F1"));

                int totalSlots = gridWidth * gridHeight;
                int childCount = go.transform.childCount;
                node.SetMeta("slot_count", totalSlots.ToString());

                if (childCount > 0)
                {
                    // Determine the visible slot boundary. Slots beyond totalSlots
                    // (gridWidth * gridHeight where gridHeight = visible rows) are
                    // equipment/extra slots in hidden rows � capture them but mark as
                    // inactive so they aren't rendered as regular grid cells.
                    int visibleSlots = totalSlots;
                    int capturedSlots = 0;
                    int hiddenSlots = 0;
                    for (int i = 0; i < childCount; i++)
                    {
                        var child = go.transform.GetChild(i);
                        if (child.GetComponent<RectTransform>() == null) continue;

                        // Check if this child is beyond the visible grid boundary
                        bool isHiddenEquipmentSlot = (capturedSlots >= visibleSlots);

                        // Also detect slots positioned off-screen (VAInventory places
                        // equipment slot GOs at extreme negative positions)
                        var childRect = child.GetComponent<RectTransform>();
                        if (childRect != null)
                        {
                            var pos = childRect.anchoredPosition;
                            if (pos.x < -5000f || pos.y < -5000f || pos.x > 5000f || pos.y > 5000f)
                                isHiddenEquipmentSlot = true;
                        }

                        // Skip capturing hidden equipment slots entirely � they belong
                        // to the equipment panel system, not the inventory grid UI.
                        // The UIOverrideSlotSystem / UIOverrideEquipmentAutoLayout handles
                        // equipment slot rendering separately.
                        if (isHiddenEquipmentSlot)
                        {
                            hiddenSlots++;
                            capturedSlots++;
                            continue;
                        }

                        var childNode = ParseNode(child.gameObject);
                        if (childNode != null)
                        {
                            if (capturedSlots == 0)
                                childNode.SetMeta("slot_template", "true");
                            childNode.SetMeta("grid_pos", $"{capturedSlots % gridWidth},{capturedSlots / gridWidth}");
                            if (string.IsNullOrEmpty(childNode.Tag))
                                childNode.Tag = "inventory_slot";
                            node.AddChild(childNode);
                            capturedSlots++;
                        }
                        else
                        {
                            capturedSlots++;
                        }
                    }
                    if (hiddenSlots > 0)
                        Debug.Log($"[UILayoutParser] Skipped {hiddenSlots} hidden equipment slots beyond visible grid ({gridWidth}x{gridHeight})");
                    Debug.Log($"[UILayoutParser] Captured {capturedSlots - hiddenSlots}/{visibleSlots} live inventory grid slots from '{go.name}' ({gridWidth}x{gridHeight})");

                    // If fewer visible captured slots than totalSlots, clone the first
                    // captured slot to fill the remaining visible grid positions.
                    int visibleCaptured = node.Children != null ? node.Children.Count : 0;
                    if (visibleCaptured > 0 && visibleCaptured < totalSlots)
                    {
                        var templateSlot = node.Children[0];
                        string templateJson = null;
                        try
                        {
                            var tempLayout = new UILayoutDefinition { RootElement = templateSlot };
                            templateJson = UILayoutSerializer.Serialize(tempLayout);
                        }
                        catch { /* serialization failed � can't clone */ }

                        if (templateJson != null)
                        {
                            int generated = 0;
                            for (int s = visibleCaptured; s < totalSlots; s++)
                            {
                                try
                                {
                                    var cloned = UILayoutSerializer.Deserialize(templateJson)?.RootElement;
                                    if (cloned != null)
                                    {
                                        cloned.Id = "cap_" + (_idCounter++);
                                        cloned.Name = $"Slot_{s}";
                                        cloned.SetMeta("slot_template", null);
                                        cloned.SetMeta("grid_pos", $"{s % gridWidth},{s / gridWidth}");
                                        if (string.IsNullOrEmpty(cloned.Tag))
                                            cloned.Tag = "inventory_slot";
                                        node.AddChild(cloned);
                                        generated++;
                                    }
                                }
                                catch { /* clone failed � skip slot */ }
                            }
                            Debug.Log($"[UILayoutParser] Generated {generated} additional slots to fill {totalSlots} total for '{go.name}'");
                        }
                    }
                }
                else if (elementPrefab != null)
                {
                    // No live children � InventoryGrid.UpdateGui() hasn't run yet.
                    // Parse the element prefab as a template and duplicate it to fill the grid.
                    Debug.Log($"[UILayoutParser] Grid root '{go.name}' has 0 children � using m_elementPrefab as template");

                    var templateNode = ParseNode(elementPrefab);
                    if (templateNode != null)
                    {
                        templateNode.SetMeta("slot_template", "true");
                        templateNode.SetMeta("grid_pos", "0,0");
                        if (string.IsNullOrEmpty(templateNode.Tag))
                            templateNode.Tag = "inventory_slot";
                        templateNode.Name = "Slot_Template";
                        node.AddChild(templateNode);

                        // Create lightweight clones for remaining slots
                        // (serialize + deserialize for deep clone)
                        string templateJson = null;
                        int slotsToGenerate = totalSlots - 1;
                        if (slotsToGenerate > 0)
                        {
                            var tempLayout = new UILayoutDefinition { RootElement = templateNode };
                            templateJson = UILayoutSerializer.Serialize(tempLayout);
                        }

                        for (int s = 1; s <= slotsToGenerate; s++)
                        {
                            try
                            {
                                var cloned = UILayoutSerializer.Deserialize(templateJson)?.RootElement;
                                if (cloned != null)
                                {
                                    cloned.Id = "cap_" + (_idCounter++);
                                    cloned.Name = $"Slot_{s}";
                                    cloned.SetMeta("slot_template", null);
                                    cloned.SetMeta("grid_pos", $"{s % gridWidth},{s / gridWidth}");
                                    if (string.IsNullOrEmpty(cloned.Tag))
                                        cloned.Tag = "inventory_slot";
                                    node.AddChild(cloned);
                                }
                            }
                            catch { /* clone failed � skip slot */ }
                        }
                        int total = 1 + slotsToGenerate;
                        Debug.Log($"[UILayoutParser] Synthesized {total}/{totalSlots} inventory grid slots from prefab for '{go.name}' ({gridWidth}x{gridHeight})");
                    }
                    else
                    {
                        Debug.LogWarning($"[UILayoutParser] Failed to parse element prefab for '{go.name}'");
                    }
                }
                else
                {
                    Debug.LogWarning($"[UILayoutParser] Grid root '{go.name}' has 0 children and no element prefab");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutParser] ParseInventoryGridChildren failed on '{go.name}': {ex.Message}");
                ParseChildren(go, node);
            }
        }

        // ???????????????????????????????????????
        //  Child parsing
        // ???????????????????????????????????????

        private static void ParseChildren(GameObject go, UIElementNode node)
        {
            if (go.transform.childCount == 0) return;

            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i);
                var childRect = child.GetComponent<RectTransform>();
                if (childRect == null) continue;

                var childNode = ParseNode(child.gameObject);
                if (childNode != null)
                    node.AddChild(childNode);
            }
        }

        private static void ParseScrollViewChildren(GameObject go, UIElementNode node)
        {
            var scroll = go.GetComponent<ScrollRect>();
            if (scroll == null || scroll.content == null) return;

            // Parse children of the Content transform (skip Viewport/Content structure)
            var content = scroll.content;
            for (int i = 0; i < content.childCount; i++)
            {
                var child = content.GetChild(i);
                var childRect = child.GetComponent<RectTransform>();
                if (childRect == null) continue;

                var childNode = ParseNode(child.gameObject);
                if (childNode != null)
                    node.AddChild(childNode);
            }
        }

        /// <summary>
        /// Parses children of a ScrollView that are NOT part of the content area
        /// (e.g., scrollbars, decorative frames). Used in deep capture mode.
        /// </summary>
        private static void ParseChildrenExcluding(GameObject go, UIElementNode node, ScrollRect scroll)
        {
            if (scroll == null) return;
            var viewport = scroll.viewport != null ? scroll.viewport.transform : null;
            var content = scroll.content != null ? scroll.content.transform : null;

            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i);
                // Skip viewport and content � they're already handled
                if (child == viewport || child == content) continue;
                // Skip if this child is the viewport's parent
                if (viewport != null && child == viewport.parent && child != go.transform) continue;

                var childRect = child.GetComponent<RectTransform>();
                if (childRect == null) continue;

                var childNode = ParseNode(child.gameObject);
                if (childNode != null)
                    node.AddChild(childNode);
            }
        }

        // ???????????????????????????????????????
        //  Utility helpers
        // ???????????????????????????????????????

        /// <summary>
        /// Tries to detect the font category from a TMP_Text component.
        /// </summary>
        private static string DetectFontCategory(TMP_Text text)
        {
            if (text == null || text.font == null) return "Body";

            string fontName = text.font.name.ToLowerInvariant();

            if (fontName.Contains("norse") || fontName.Contains("norsebold"))
                return "Decorative";
            if (fontName.Contains("valheim") || fontName.Contains("averia"))
                return "Primary";

            return "Body";
        }

        /// <summary>
        /// Finds the first TMP_Text child of a GameObject (e.g., button label).
        /// Also searches grandchildren one level deep for vanilla UIs.
        /// </summary>
        private static TMP_Text FindChildText(GameObject go)
        {
            for (int i = 0; i < go.transform.childCount; i++)
            {
                var tmp = go.transform.GetChild(i).GetComponent<TMP_Text>();
                if (tmp != null) return tmp;
            }
            // Search one level deeper (vanilla buttons sometimes nest text in sub-containers)
            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i);
                for (int j = 0; j < child.childCount; j++)
                {
                    var tmp = child.GetChild(j).GetComponent<TMP_Text>();
                    if (tmp != null) return tmp;
                }
            }
            return null;
        }

        /// <summary>
        /// Finds the first legacy UnityEngine.UI.Text child of a GameObject.
        /// </summary>
        private static UnityEngine.UI.Text FindChildLegacyText(GameObject go)
        {
            for (int i = 0; i < go.transform.childCount; i++)
            {
                var txt = go.transform.GetChild(i).GetComponent<UnityEngine.UI.Text>();
                if (txt != null) return txt;
            }
            // Search one level deeper
            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i);
                for (int j = 0; j < child.childCount; j++)
                {
                    var txt = child.GetChild(j).GetComponent<UnityEngine.UI.Text>();
                    if (txt != null) return txt;
                }
            }
            return null;
        }

        /// <summary>
        /// Finds a label text in toggle/button children, skipping the first child (checkmark/icon).
        /// </summary>
        private static TMP_Text FindLabelText(GameObject go)
        {
            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i);
                if (child.name.IndexOf("label", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    child.name.IndexOf("text", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var tmp = child.GetComponent<TMP_Text>();
                    if (tmp != null) return tmp;
                }
            }

            // Fallback: find any text after the first child
            for (int i = 1; i < go.transform.childCount; i++)
            {
                var tmp = go.transform.GetChild(i).GetComponent<TMP_Text>();
                if (tmp != null) return tmp;
            }
            return null;
        }

        private static string GetPlayerName()
        {
            try
            {
                if (Player.m_localPlayer != null)
                    return Player.m_localPlayer.GetPlayerName();
            }
            catch { }
            return "Admin";
        }

        /// <summary>
        /// Converts TMP_InputField.ContentType enum to the string representation used in UIInputFieldDef.
        /// </summary>
        private static string ContentTypeToString(TMP_InputField.ContentType contentType)
        {
            switch (contentType)
            {
                case TMP_InputField.ContentType.Autocorrected: return "Autocorrected";
                case TMP_InputField.ContentType.IntegerNumber: return "IntegerNumber";
                case TMP_InputField.ContentType.DecimalNumber: return "DecimalNumber";
                case TMP_InputField.ContentType.Alphanumeric: return "Alphanumeric";
                case TMP_InputField.ContentType.Name: return "Name";
                case TMP_InputField.ContentType.EmailAddress: return "EmailAddress";
                case TMP_InputField.ContentType.Password: return "Password";
                case TMP_InputField.ContentType.Pin: return "Pin";
                case TMP_InputField.ContentType.Custom: return "Custom";
                default: return "Standard";
            }
        }

        // ???????????????????????????????????????
        //  Known UI capture helpers
        // ???????????????????????????????????????

        /// <summary>
        /// Registry of known programmatic UIs that can be captured.
        /// Each entry has a display name, a method to find the root GO, and suggested tags.
        /// </summary>
        public static readonly List<CaptureTarget> KnownTargets = new List<CaptureTarget>
        {
            new CaptureTarget
            {
                DisplayName = "Dialogue Panel",
                UID = "captured_dialogue_panel",
                Category = "Dialogue",
                FindRoot = () => GameObject.Find("DialoguePanel"),
                TagHints = new Dictionary<string, string>
                {
                    { "NpcName", "npc_name" },
                    { "DialogueText", "dialogue_text" },
                    { "Content", "options_container" },
                    { "CloseBtn", "close_button" },
                    { "Divider", "divider" },
                    { "OptionsScroll", "options_container" },
                    { "Option_", "option_button" }
                }
            },
            new CaptureTarget
            {
                DisplayName = "Info NPC Panel",
                UID = "captured_info_panel",
                Category = "Info",
                FindRoot = () => GameObject.Find("InfoNpcPlayerPanel"),
                TagHints = new Dictionary<string, string>
                {
                    { "Header", "header_text" },
                    { "Content", "content_text" },
                    { "ImageContainer", "image_container" },
                    { "Image", "background_image" },
                    { "CloseButton", "close_button" },
                    { "SizeButton", "size_button" },
                    { "HeaderBackground", "header_bg" },
                    { "ContentBackground", "content_bg" }
                }
            }
        };

        /// <summary>
        /// Registry of known vanilla Valheim UIs that can be deep-captured.
        /// These use ParseFlags.Vanilla for full-depth capture with legacy text support.
        /// </summary>
        public static readonly List<VanillaCaptureTarget> VanillaTargets = new List<VanillaCaptureTarget>
        {
            new VanillaCaptureTarget
            {
                DisplayName = "Inventory Root",
                UID = "vanilla_inventory_root",
                Category = "Vanilla",
                FindRoot = () =>
                {
                    var inst = InventoryGui.instance;
                    if (inst == null) return null;
                    // Capture the entire inventory root (player + crafting + container + info)
                    return inst.m_inventoryRoot != null ? inst.m_inventoryRoot.gameObject : null;
                },
                TagHints = new Dictionary<string, string>
                {
                    { "Armor", "armor_text" },
                    { "Weight", "weight_text" },
                    { "PlayerGrid", "player_grid" },
                    { "playerGrid", "player_grid" },
                    { "PlayerName", "player_name" },
                    { "ContainerGrid", "container_grid" },
                    { "containerGrid", "container_grid" },
                    { "ContainerName", "container_name" },
                    { "TakeAll", "take_all_button" },
                    { "StackAll", "stack_all_button" },
                    { "recipeName", "recipe_name" },
                    { "recipeIcon", "recipe_icon" },
                    { "recipeDecription", "recipe_description" },
                    { "CraftButton", "craft_button" },
                    { "RepairButton", "repair_button" },
                    { "TabCraft", "tab_craft" },
                    { "TabUpgrade", "tab_upgrade" },
                    { "recipeList", "recipe_list" },
                    { "Durability", "durability_bar" },
                    { "icon", "slot_icon" },
                    { "amount", "slot_amount" },
                    { "quality", "slot_quality" },
                    { "equiped", "slot_equipped" },
                    { "binding", "slot_binding" },
                    { "selected", "slot_selected" }
                }
            },
            new VanillaCaptureTarget
            {
                DisplayName = "Inventory",
                UID = "vanilla_inventory",
                Category = "Vanilla",
                FindRoot = () =>
                {
                    var inst = InventoryGui.instance;
                    if (inst == null) return null;
                    // Capture the player inventory panel
                    return inst.m_player != null ? inst.m_player.gameObject : null;
                },
                TagHints = new Dictionary<string, string>
                {
                    { "Armor", "armor_text" },
                    { "Weight", "weight_text" },
                    { "PlayerGrid", "player_grid" },
                    { "playerGrid", "player_grid" },
                    { "PlayerName", "player_name" },
                    { "icon", "slot_icon" },
                    { "amount", "slot_amount" },
                    { "quality", "slot_quality" },
                    { "equiped", "slot_equipped" },
                    { "binding", "slot_binding" },
                    { "selected", "slot_selected" },
                    { "Durability", "durability_bar" },
                    { "noteleport", "slot_noteleport" },
                    { "foodicon", "slot_food" }
                }
            },
            new VanillaCaptureTarget
            {
                DisplayName = "Crafting Panel",
                UID = "vanilla_crafting",
                Category = "Vanilla",
                FindRoot = () =>
                {
                    var inst = InventoryGui.instance;
                    if (inst == null) return null;
                    return inst.m_crafting != null ? inst.m_crafting.gameObject : null;
                },
                TagHints = new Dictionary<string, string>
                {
                    { "recipeName", "recipe_name" },
                    { "recipeIcon", "recipe_icon" },
                    { "recipeDecription", "recipe_description" },
                    { "CraftButton", "craft_button" },
                    { "CraftCancel", "craft_cancel_button" },
                    { "RepairButton", "repair_button" },
                    { "TabCraft", "tab_craft" },
                    { "TabUpgrade", "tab_upgrade" },
                    { "recipeList", "recipe_list" },
                    { "recipeElement", "recipe_element" },
                    { "CraftProgressPanel", "craft_progress" },
                    { "CraftProgressBar", "craft_progress_bar" },
                    { "QualityPanel", "quality_panel" },
                    { "MinStationLevel", "min_station_level" },
                    { "CraftingStationName", "station_name" },
                    { "CraftingStationIcon", "station_icon" },
                    { "VariantButton", "variant_button" },
                    { "upgradeItem", "upgrade_item" },
                    { "Requirement", "requirement" }
                }
            },
            new VanillaCaptureTarget
            {
                DisplayName = "Container Panel",
                UID = "vanilla_container",
                Category = "Vanilla",
                FindRoot = () =>
                {
                    var inst = InventoryGui.instance;
                    if (inst == null) return null;
                    return inst.m_container != null ? inst.m_container.gameObject : null;
                },
                TagHints = new Dictionary<string, string>
                {
                    { "ContainerName", "container_name" },
                    { "ContainerGrid", "container_grid" },
                    { "containerGrid", "container_grid" },
                    { "TakeAll", "take_all_button" },
                    { "StackAll", "stack_all_button" },
                    { "ContainerWeight", "container_weight" },
                    { "icon", "slot_icon" },
                    { "amount", "slot_amount" },
                    { "quality", "slot_quality" }
                }
            },
            new VanillaCaptureTarget
            {
                DisplayName = "Info Panel",
                UID = "vanilla_info_panel",
                Category = "Vanilla",
                FindRoot = () =>
                {
                    var inst = InventoryGui.instance;
                    if (inst == null) return null;
                    return inst.m_info != null ? inst.m_info.gameObject : null;
                },
                TagHints = new Dictionary<string, string>
                {
                    { "Weight", "weight_text" },
                    { "Armor", "armor_text" },
                    { "PlayerName", "player_name" },
                    { "pvp", "pvp_toggle" }
                }
            },
            new VanillaCaptureTarget
            {
                DisplayName = "Split Dialog",
                UID = "vanilla_split_dialog",
                Category = "Vanilla",
                FindRoot = () =>
                {
                    var inst = InventoryGui.instance;
                    if (inst == null) return null;
                    return inst.m_splitPanel != null ? inst.m_splitPanel.gameObject : null;
                },
                TagHints = new Dictionary<string, string>
                {
                    { "SplitAmount", "split_amount" },
                    { "SplitSlider", "split_slider" },
                    { "SplitOk", "ok_button" },
                    { "SplitCancel", "cancel_button" }
                }
            },
            new VanillaCaptureTarget
            {
                DisplayName = "Trophies Panel",
                UID = "vanilla_trophies",
                Category = "Vanilla",
                FindRoot = () =>
                {
                    var inst = InventoryGui.instance;
                    if (inst == null) return null;
                    return inst.m_trophiesPanel;
                },
                TagHints = new Dictionary<string, string>
                {
                    { "TrophieList", "trophy_list" }
                }
            },
            new VanillaCaptureTarget
            {
                DisplayName = "HUD",
                UID = "vanilla_hud",
                Category = "Vanilla",
                FindRoot = () =>
                {
                    var hud = Hud.instance;
                    if (hud == null) return null;
                    return hud.gameObject;
                },
                TagHints = new Dictionary<string, string>
                {
                    { "healthpanel", "health_panel" },
                    { "stamina", "stamina_bar" },
                    { "BuildHud", "build_hud" },
                    { "MiniMap", "minimap" },
                    { "StatusEffect", "status_effects" }
                }
            },
            new VanillaCaptureTarget
            {
                DisplayName = "Menu",
                UID = "vanilla_menu",
                Category = "Vanilla",
                FindRoot = () =>
                {
                    var menu = Menu.instance;
                    if (menu == null) return null;
                    return menu.gameObject;
                },
                TagHints = new Dictionary<string, string>
                {
                    { "MenuList", "menu_list" },
                    { "Continue", "continue_button" },
                    { "Logout", "logout_button" },
                    { "Exit", "exit_button" }
                }
            },
            new VanillaCaptureTarget
            {
                DisplayName = "Store Panel",
                UID = "vanilla_store",
                Category = "Vanilla",
                FindRoot = () =>
                {
                    var store = StoreGui.instance;
                    if (store == null) return null;
                    return store.gameObject;
                },
                TagHints = new Dictionary<string, string>
                {
                    { "StoreList", "store_list" },
                    { "BuyButton", "buy_button" },
                    { "SellButton", "sell_button" },
                    { "ItemName", "item_name" },
                    { "Coins", "coins_text" }
                }
            },
            new VanillaCaptureTarget
            {
                DisplayName = "Skills Dialog",
                UID = "vanilla_skills",
                Category = "Vanilla",
                FindRoot = () =>
                {
                    var inst = InventoryGui.instance;
                    if (inst == null || inst.m_skillsDialog == null) return null;
                    return inst.m_skillsDialog.gameObject;
                },
                TagHints = new Dictionary<string, string>
                {
                    { "SkillList", "skill_list" },
                    { "Close", "close_button" }
                }
            },
            new VanillaCaptureTarget
            {
                DisplayName = "Texts Dialog",
                UID = "vanilla_texts",
                Category = "Vanilla",
                FindRoot = () =>
                {
                    var inst = InventoryGui.instance;
                    if (inst == null || inst.m_textsDialog == null) return null;
                    return inst.m_textsDialog.gameObject;
                },
                TagHints = new Dictionary<string, string>
                {
                    { "TextList", "text_list" },
                    { "Close", "close_button" }
                }
            }
        };

        /// <summary>
        /// Captures a known UI target by index. Returns the parsed layout or null.
        /// Auto-applies tag hints based on GameObject names.
        /// </summary>
        public static UILayoutDefinition CaptureKnownTarget(int targetIndex)
        {
            if (targetIndex < 0 || targetIndex >= KnownTargets.Count) return null;
            return CaptureKnownTarget(KnownTargets[targetIndex]);
        }

        /// <summary>
        /// Captures a known UI target. Returns the parsed layout or null.
        /// </summary>
        public static UILayoutDefinition CaptureKnownTarget(CaptureTarget target)
        {
            if (target == null) return null;

            var root = target.FindRoot();
            if (root == null)
            {
                Debug.LogWarning($"[UILayoutParser] Cannot find root for '{target.DisplayName}' � is it currently visible?");
                return null;
            }

            var layout = Parse(root, target.UID, target.DisplayName, target.Category);
            if (layout == null) return null;

            // Apply tag hints
            if (target.TagHints != null && layout.RootElement != null)
            {
                ApplyTagHints(layout.RootElement, target.TagHints);
            }

            // Normalize inventory slot children to empty defaults
            NormalizeVanillaVisibility(layout);

            // Aggregate source_bundle metadata from all nodes into layout-level required_bundles
            AggregateRequiredBundles(layout);

            // Audit Harmony patches and runtime mod components on the captured hierarchy
            UIHarmonyPatchAuditor.AuditAndAnnotate(layout, root);

            // Normalize for editor display
            NormalizeForEditor(layout);

            // Store the capture baseline for diff-based overrides
            UIOverrideDiffEngine.StoreCaptureBaseline(layout);

            return layout;
        }

        /// <summary>
        /// Captures any visible UI root by GameObject name.
        /// </summary>
        public static UILayoutDefinition CaptureByName(string gameObjectName, string uid = null, string displayName = null, string category = "Custom")
        {
            var go = GameObject.Find(gameObjectName);
            if (go == null)
            {
                Debug.LogWarning($"[UILayoutParser] GameObject '{gameObjectName}' not found");
                return null;
            }
            var layout = Parse(go, uid ?? "captured_" + gameObjectName.ToLowerInvariant(), displayName ?? gameObjectName, category);
            NormalizeVanillaVisibility(layout);
            AggregateRequiredBundles(layout);
            UIHarmonyPatchAuditor.AuditAndAnnotate(layout, go);
            NormalizeForEditor(layout);

            // Store the capture baseline for diff-based overrides
            UIOverrideDiffEngine.StoreCaptureBaseline(layout);

            return layout;
        }

        /// <summary>
        /// Captures an auto-discovered UI target. Detects the appropriate parse flags
        /// based on the source category and enables asset caching for modded UIs.
        /// </summary>
        public static UILayoutDefinition CaptureDiscoveredTarget(DiscoveredUITarget target)
        {
            if (target == null || target.Root == null)
            {
                Debug.LogWarning("[UILayoutParser] CaptureDiscoveredTarget: target or root is null");
                return null;
            }

            // Build a UID from the target name
            string safeName = target.DisplayName.Replace(" ", "_").Replace("(", "").Replace(")", "").ToLowerInvariant();
            string uid = "discovered_" + safeName;
            string displayName = target.DisplayName;
            string category = target.SourceCategory;

            // Choose parse flags based on source
            ParseFlags flags;
            switch (target.SourceCategory)
            {
                case "Vanilla":
                    flags = ParseFlags.Vanilla;
                    break;
                case "Ours":
                    flags = ParseFlags.DeepChildren | ParseFlags.IncludeInactive;
                    break;
                default: // Modded, Unknown
                    flags = ParseFlags.DeepChildren | ParseFlags.IncludeInactive | ParseFlags.LegacyText;
                    break;
            }

            // Asset caching is now always enabled inside Parse() for all source categories.
            // Sprites from any source (Vanilla, Modded, Unknown) are evaluated by IsSpriteFromMod()
            // which decides whether to cache based on sprite origin, not UI classification.
            UILayoutDefinition layout = Parse(target.Root, uid, displayName, category, flags);

            if (layout == null) return null;

            // Store source metadata
            layout.SetMeta("source", "discovered");
            layout.SetMeta("source_category", target.SourceCategory);
            if (!string.IsNullOrEmpty(target.SourceAssembly))
                layout.SetMeta("source_assembly", target.SourceAssembly);
            if (!string.IsNullOrEmpty(target.SourceMod))
                layout.SetMeta("source_mod", target.SourceMod);
            layout.SetMeta("source_canvas_path", target.RootGameObjectPath);
            layout.SetMeta("canvas_sorting_order", target.SortingOrder.ToString());

            // Normalize dynamically-toggled inventory slot children to their empty default
            // state. This applies to any captured UI that contains inventory grids �
            // vanilla, modded (AzuEPI, etc.), or discovered. Without this, slot children
            // (icon, amount, equipped, quality, etc.) retain whatever live item data was
            // present at capture time instead of starting blank for the game to populate.
            NormalizeVanillaVisibility(layout);

            // Aggregate source_bundle metadata from all nodes into layout-level required_bundles
            AggregateRequiredBundles(layout);

            // Audit Harmony patches and runtime mod components on the captured hierarchy
            UIHarmonyPatchAuditor.AuditAndAnnotate(layout, target.Root);

            NormalizeForEditor(layout);

            // Store the capture baseline for diff-based overrides
            UIOverrideDiffEngine.StoreCaptureBaseline(layout);

            return layout;
        }

        /// <summary>
        /// Captures a vanilla Valheim UI target by index. Returns the parsed layout or null.
        /// Uses deep capture mode with legacy text support.
        /// </summary>
        public static UILayoutDefinition CaptureVanillaTarget(int targetIndex)
        {
            if (targetIndex < 0 || targetIndex >= VanillaTargets.Count) return null;
            return CaptureVanillaTarget(VanillaTargets[targetIndex]);
        }

        /// <summary>
        /// Captures a vanilla Valheim UI target. Returns the parsed layout or null.
        /// Uses deep capture mode with legacy text support.
        /// </summary>
        public static UILayoutDefinition CaptureVanillaTarget(VanillaCaptureTarget target)
        {
            if (target == null) return null;

            var root = target.FindRoot();
            if (root == null)
            {
                Debug.LogWarning($"[UILayoutParser] Cannot find root for vanilla '{target.DisplayName}' � is it currently visible?");
                return null;
            }

            var layout = ParseVanilla(root, target.UID, target.DisplayName, target.Category);
            if (layout == null) return null;

            // Apply tag hints
            if (target.TagHints != null && layout.RootElement != null)
                ApplyTagHints(layout.RootElement, target.TagHints);

            // Post-capture: synthesize placeholder recipe entries for crafting panels
            // that have empty recipe lists (no crafting station nearby at capture time)
            SynthesizeCraftingRecipeEntries(layout);

            // Normalize dynamically-toggled elements to their default inactive state.
            // When we capture, the UI is open and everything is active � slot indicators
            // (equipped, selected, queued, noteleport, food), panels (container, split,
            // trophies, skills, texts, variant dialog), and per-item overlays are all shown.
            // The game toggles these on/off at runtime. We reset them to their intended
            // default so the editor shows a clean layout and the override system doesn't
            // force-show elements that should be hidden.
            NormalizeVanillaVisibility(layout);

            // Aggregate source_bundle metadata from all nodes into layout-level required_bundles
            AggregateRequiredBundles(layout);

            // Audit Harmony patches and runtime mod components on the captured hierarchy
            UIHarmonyPatchAuditor.AuditAndAnnotate(layout, root);

            // Normalize for editor display
            NormalizeForEditor(layout);

            layout.SetMeta("source", "vanilla_capture");
            layout.SetMeta("vanilla_ui", target.DisplayName);

            // Store the VanillaTarget UID so the override system can use the FindRoot() delegate
            // to reliably locate the correct vanilla GO. Using root.name alone fails when the
            // name is generic (e.g., "root") and matches the wrong GO via GameObject.Find().
            UIVanillaOverrideManager.SetOverrideTarget(layout, target.UID);

            // Store the capture baseline for diff-based overrides
            UIOverrideDiffEngine.StoreCaptureBaseline(layout);

            return layout;
        }

        /// <summary>
        /// Detects empty recipe list containers in crafting panel captures and synthesizes
        /// placeholder recipe entries from the m_recipeElementPrefab. Valheim's crafting
        /// panel creates recipe elements dynamically in UpdateRecipeList(), so if no crafting
        /// station is nearby at capture time, the recipe list will be empty.
        /// </summary>
        private static void SynthesizeCraftingRecipeEntries(UILayoutDefinition layout)
        {
            if (layout == null || layout.RootElement == null) return;

            // Find the recipe list node (tagged "recipe_list" or named "RecipeList" / "recipeList")
            var recipeListNode = layout.RootElement.FindByTag("recipe_list");
            if (recipeListNode == null)
                recipeListNode = FindNodeByName(layout.RootElement, "RecipeList");
            if (recipeListNode == null)
                recipeListNode = FindNodeByName(layout.RootElement, "recipeList");
            if (recipeListNode == null) return;

            // If the recipe list already has children, it was populated at capture time
            if (recipeListNode.Children != null && recipeListNode.Children.Count > 0) return;

            // Try to get the recipe element prefab from InventoryGui
            try
            {
                var invGui = InventoryGui.instance;
                if (invGui == null) return;

                var prefabField = typeof(InventoryGui).GetField("m_recipeElementPrefab", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (prefabField == null) return;

                var prefab = prefabField.GetValue(invGui) as GameObject;
                if (prefab == null) return;

                // Parse the prefab to get a template recipe element
                var savedFlags = _flags;
                _flags = ParseFlags.Vanilla;
                var templateNode = ParseNode(prefab);
                _flags = savedFlags;

                if (templateNode == null) return;

                templateNode.Tag = "recipe_element";
                templateNode.Name = "RecipeElement_Template";
                templateNode.SetMeta("recipe_template", "true");

                // Synthesize placeholder recipe entries (6 gives a good visual representation)
                int recipeCount = 6;
                string[] sampleNames = { "Iron Sword", "Bronze Helmet", "Deer Stew", "Fire Arrow", "Wood Shield", "Leather Pants" };

                recipeListNode.AddChild(templateNode);

                string templateJson = null;
                try
                {
                    var tempLayout = new UILayoutDefinition { RootElement = templateNode };
                    templateJson = UILayoutSerializer.Serialize(tempLayout);
                }
                catch { /* serialization failed */ }

                if (templateJson != null)
                {
                    for (int i = 1; i < recipeCount; i++)
                    {
                        try
                        {
                            var cloned = UILayoutSerializer.Deserialize(templateJson)?.RootElement;
                            if (cloned != null)
                            {
                                cloned.Id = "cap_" + (_idCounter++);
                                cloned.Name = $"RecipeElement_{i}";
                                cloned.Tag = "recipe_element";
                                cloned.SetMeta("recipe_template", null);
                                recipeListNode.AddChild(cloned);
                            }
                        }
                        catch { /* clone failed */ }
                    }
                }

                // Ensure the recipe list has a vertical layout group if it doesn't already
                if (recipeListNode.LayoutGroup == null)
                {
                    recipeListNode.LayoutGroup = new UILayoutGroupDef
                    {
                        IsVertical = true,
                        Spacing = 1,
                        Padding = new RectOffsetSer(0, 0, 0, 0),
                        ChildAlignment = 0,
                        ChildControlWidth = true,
                        ChildControlHeight = false,
                        ChildForceExpandWidth = true,
                        ChildForceExpandHeight = false
                    };
                }

                recipeListNode.SetMeta("recipe_list_synthesized", "true");
                recipeListNode.SetMeta("recipe_count", recipeCount.ToString());
                Debug.Log($"[UILayoutParser] Synthesized {recipeCount} placeholder recipe entries for crafting recipe list");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutParser] SynthesizeCraftingRecipeEntries failed: {ex.Message}");
            }
        }

        // ???????????????????????????????????????
        //  Vanilla visibility normalization
        // ???????????????????????????????????????

        /// <summary>
        /// Names of elements inside inventory grid slots (InventoryGrid.Element children)
        /// that are toggled via GameObject.SetActive() by UpdateGui() and should default
        /// to inactive. Only includes elements where Valheim calls .gameObject.SetActive().
        ///
        /// From decompiled InventoryGrid.UpdateGui():
        ///   - selected: element.m_selected.SetActive(flag1 && ...) � uses SetActive
        ///   - durability: element.m_durability.gameObject.SetActive(flag2) � uses SetActive
        /// </summary>
        private static readonly HashSet<string> _slotDynamicChildren =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "selected",     // m_selected.SetActive() � only for gamepad-selected slot
                "durability",   // m_durability.gameObject.SetActive() � only when item has durability < max
            };

        /// <summary>
        /// Names of ALL child elements inside inventory grid slots that are managed
        /// per-frame by InventoryGrid.UpdateGui(). These children have their sprites,
        /// text, color, and Image/Text .enabled state driven by the game based on
        /// what item occupies the slot. At capture time, they reflect whatever live
        /// state happened to be present. We must reset them to a clean empty default
        /// so the override starts blank and lets the game populate them at runtime.
        /// </summary>
        private static readonly HashSet<string> _slotManagedChildren =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "icon",         // Item icon sprite � .sprite and .enabled set per-frame
                "amount",       // Stack count text � .text and .enabled set per-frame
                "quality",      // Item quality text � .text and .enabled set per-frame
                "equiped",      // Equipped indicator � .enabled set per-frame
                "queued",       // Queued indicator � .enabled set per-frame
                "noteleport",   // No-teleport indicator � .enabled set per-frame
                "foodicon",     // Food type color indicator � .enabled and .color set per-frame
                "selected",     // Gamepad selection highlight � SetActive() per-frame
                "durability",   // Durability bar � SetActive() per-frame
                "binding",      // Hotbar binding text � .text and .enabled set per-frame
            };

        /// <summary>
        /// Top-level panels under the inventory root that are hidden by default
        /// in InventoryGui.Awake(). These only become visible when the game
        /// explicitly shows them (e.g., opening a container, splitting a stack).
        /// </summary>
        private static readonly HashSet<string> _defaultInactivePanels =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Container",         // m_container � hidden until chest opened
                "SplitPanel",        // m_splitPanel � hidden until stack split
                "TrophiesPanel",     // m_trophiesPanel � hidden by default
                "VariantDialog",     // m_variantDialog � hidden by default
                "SkillsDialog",      // m_skillsDialog � hidden by default
                "TextsDialog",       // m_textsDialog � hidden by default
            };

        /// <summary>
        /// Additional element names that are dynamically toggled via SetActive() throughout
        /// the inventory UI and should default to inactive for a clean capture.
        /// Derived from decompiled InventoryGui: UpdateRecipe(), UpdateRepair(),
        /// AddRecipeToList(), and related methods.
        /// </summary>
        private static readonly HashSet<string> _defaultInactiveElements =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                // ?? Crafting panel (from UpdateRecipe) ??
                "CraftProgressPanel",       // m_craftProgressPanel.gameObject.SetActive � only during active crafting
                "CraftProgressBar",         // child of CraftProgressPanel � only during active crafting
                "itemCraftType",            // m_itemCraftType.gameObject.SetActive � only when upgrade item exists
                "VariantButton",            // m_variantButton.gameObject.SetActive � only when item has variants
                "MinStationLevel",          // m_minStationLevelIcon.gameObject.SetActive � only when station required
                "QualityPanel",             // m_qualityPanel.gameObject.SetActive � only when recipe selected with quality

                // ?? Crafting station display (from UpdateRecipe) ??
                "CraftingStationIcon",      // m_craftingStationIcon.gameObject.SetActive � only when station present
                "CraftingStationLevelRoot", // m_craftingStationLevelRoot.gameObject.SetActive � only when station present

                // ?? Repair panel (from UpdateRepair) ??
                "repairPanel",              // m_repairPanel.gameObject.SetActive � only when station present
                "repairPanelSelection",     // m_repairPanelSelection.gameObject.SetActive � only when station present
                "repairButtonGlow",         // m_repairButtonGlow.gameObject.SetActive � only when repairable items exist

                // ?? Recipe list elements (from AddRecipeToList) ??
                "QualityLevel",             // component4.gameObject.SetActive � only for items with quality > 1
                "Durability",               // component3.gameObject.SetActive � only for damaged items

                // ?? Recipe list sub-elements (from SetRecipe) ??
                "selected",                 // .Find("selected").gameObject.SetActive � only for currently selected recipe

                // ?? Upgrade display (from SetupUpgradeItem) ??
                "upgradeItem",              // shown only when viewing an upgrade
                "upgradeItemQualityArrow",  // m_upgradeItemQualityArrow � only during upgrades
            };

        /// <summary>
        /// Post-capture pass that resets dynamically-toggled vanilla UI elements to their
        /// intended default (inactive) state. When we capture while the UI is open,
        /// everything is active � equipped indicators, selection highlights, per-item
        /// overlays, and sub-panels. This method walks the captured tree and sets
        /// Active=false on elements that the game normally keeps hidden until triggered.
        ///
        /// This ensures:
        /// - The editor shows a clean "default" layout, not a snapshot of live state
        /// - The override system doesn't force-show elements that should be game-controlled
        /// - Users don't have to manually hide hundreds of dynamic elements
        /// </summary>
        private static void NormalizeVanillaVisibility(UILayoutDefinition layout)
        {
            if (layout == null || layout.RootElement == null) return;

            int normalized = 0;
            NormalizeVisibilityRecursive(layout.RootElement, null, ref normalized);

            if (normalized > 0)
            {
                layout.SetMeta("visibility_normalized", "true");
                layout.SetMeta("visibility_normalized_count", normalized.ToString());
                Debug.Log($"[UILayoutParser] Normalized {normalized} dynamically-toggled elements to default inactive state.");
            }
        }

        private static void NormalizeVisibilityRecursive(UIElementNode node, UIElementNode parent, ref int count)
        {
            if (node == null) return;

            string name = node.Name ?? "";
            bool shouldDeactivate = false;

            // Rule 1: Top-level panels that are hidden in InventoryGui.Awake()
            if (_defaultInactivePanels.Contains(name))
                shouldDeactivate = true;

            // Rule 2: Slot dynamic children � elements inside inventory_slot that use SetActive()
            if (!shouldDeactivate && parent != null)
            {
                string parentTag = parent.Tag ?? "";
                bool isSlotParent = parentTag.IndexOf("inventory_slot", StringComparison.OrdinalIgnoreCase) >= 0
                    || parentTag.IndexOf("slot", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isSlotParent && _slotDynamicChildren.Contains(name))
                    shouldDeactivate = true;
            }

            // Rule 3: Known dynamic elements anywhere in the tree
            if (!shouldDeactivate && _defaultInactiveElements.Contains(name))
                shouldDeactivate = true;

            // Rule 4: Elements tagged with known dynamic tag hints that indicate
            // game-managed visibility via SetActive()
            if (!shouldDeactivate && !string.IsNullOrEmpty(node.Tag))
            {
                string tag = node.Tag;
                if (tag == "slot_selected")
                    shouldDeactivate = true;
            }

            if (shouldDeactivate && node.Active)
            {
                node.Active = false;
                node.SetMeta("default_inactive", "true");
                count++;
            }

            // Rule 5: Reset ALL managed slot children to their empty/default state.
            // When we capture a live inventory, these children contain the sprites, text,
            // and enabled states of whatever items happen to be in the slots. The game's
            // InventoryGrid.UpdateGui() drives these per-frame, so we must reset them to
            // blank defaults. The override system skips applying visual data for nodes
            // tagged dynamic_content, letting the game populate them at runtime.
            if (parent != null)
            {
                string parentTag = parent.Tag ?? "";
                bool isSlotChild = parentTag.IndexOf("inventory_slot", StringComparison.OrdinalIgnoreCase) >= 0
                    || parentTag.IndexOf("slot", StringComparison.OrdinalIgnoreCase) >= 0;

                if (isSlotChild && _slotManagedChildren.Contains(name))
                {
                    node.SetMeta("dynamic_content", "true");

                    // Reset Image components to disabled/blank
                    if (node.ImageData != null)
                    {
                        node.ImageData.ComponentEnabled = false;
                        // Clear the captured item sprite � slot starts empty
                        if (name == "icon" || name == "foodicon")
                            node.ImageData.SpriteName = "";
                    }

                    // Reset Text components to disabled/blank
                    if (node.TextData != null)
                    {
                        node.TextData.ComponentEnabled = false;
                        // Clear captured item text (stack count, quality number, binding)
                        if (name == "amount" || name == "quality")
                            node.TextData.Text = "";
                        // Keep binding text for row 0 (hotbar keys 1-8) � the game
                        // enables binding.text for y==0 slots and disables for others.
                        // We leave binding text as-is since it's set once, not per-item.
                    }

                    // For children toggled via SetActive(), also deactivate
                    if (_slotDynamicChildren.Contains(name) && node.Active)
                    {
                        node.Active = false;
                        node.SetMeta("default_inactive", "true");
                    }

                    count++;
                }
            }

            // Recurse into children
            if (node.Children != null)
            {
                for (int i = 0; i < node.Children.Count; i++)
                    NormalizeVisibilityRecursive(node.Children[i], node, ref count);
            }
        }

        /// <summary>
        /// Recursively finds a node by name (case-insensitive).
        /// </summary>
        private static UIElementNode FindNodeByName(UIElementNode node, string name)
        {
            if (node == null) return null;
            if (!string.IsNullOrEmpty(node.Name) &&
                node.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                return node;
            if (node.Children != null)
            {
                for (int i = 0; i < node.Children.Count; i++)
                {
                    var found = FindNodeByName(node.Children[i], name);
                    if (found != null) return found;
                }
            }
            return null;
        }

        /// <summary>
        /// Walks all nodes in the layout tree and collects unique source_bundle metadata
        /// values, then stores them as a comma-separated "required_bundles" on the layout.
        /// This metadata tells the override system which asset bundles to load at apply time.
        /// </summary>
        private static void AggregateRequiredBundles(UILayoutDefinition layout)
        {
            if (layout == null || layout.RootElement == null) return;

            var bundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectSourceBundles(layout.RootElement, bundles);

            if (bundles.Count > 0)
            {
                string[] arr = new string[bundles.Count];
                bundles.CopyTo(arr);
                layout.SetMeta("required_bundles", string.Join(",", arr));
                Debug.Log($"[UILayoutParser] Aggregated {bundles.Count} required bundle(s): {string.Join(", ", arr)}");

                // Cache the raw .bundle files to disk so they can be loaded later
                // even when the source mod is no longer installed.
                UIBuilderAssetCache.CacheRequiredBundlesToDisk(bundles);
            }
        }

        private static void CollectSourceBundles(UIElementNode node, HashSet<string> bundles)
        {
            if (node == null) return;

            string srcBundle = node.GetMeta("source_bundle");
            if (!string.IsNullOrEmpty(srcBundle))
                bundles.Add(srcBundle);

            if (node.Children != null)
            {
                for (int i = 0; i < node.Children.Count; i++)
                    CollectSourceBundles(node.Children[i], bundles);
            }
        }

        /// <summary>
        /// Normalizes a captured layout so it renders well in the editor workspace.
        /// 
        /// Captured UIs were originally children of some parent in the game's canvas hierarchy.
        /// When we place them as direct children of the editor workspace canvas, any
        /// stretch-anchored root will fill the entire workspace instead of its original parent area.
        /// 
        /// This method detects stretch-anchored roots and converts them to centered fixed-size
        /// panels using the computed pixel dimensions from the original canvas. This makes them
        /// appear at the correct size and be freely movable/resizable in the editor.
        /// 
        /// Also handles fixed-size roots that were positioned relative to a corner of their
        /// original parent � these are re-centered so they appear in the middle of the workspace.
        /// 
        /// Call this after Parse() / ParseVanilla() when the layout will be opened in the editor.
        /// </summary>
        public static void NormalizeForEditor(UILayoutDefinition layout)
        {
            if (layout == null || layout.RootElement == null) return;

            var root = layout.RootElement;
            var canvasSize = layout.CanvasSize.ToVector2();

            // Preserve the original root transform before normalization so the runtime
            // override system and fullscreen preview can instantiate with correct anchoring.
            layout.RuntimeRootTransform = RuntimeRootTransformDef.FromNode(root);

            bool isStretchX = !Mathf.Approximately(root.AnchorMin.X, root.AnchorMax.X);
            bool isStretchY = !Mathf.Approximately(root.AnchorMin.Y, root.AnchorMax.Y);

            if (isStretchX || isStretchY)
            {
                // Root uses stretch anchors on at least one axis.
                // Compute the actual pixel rect it would occupy at the reference canvas size.
                float anchorLeft = root.AnchorMin.X * canvasSize.x;
                float anchorBottom = root.AnchorMin.Y * canvasSize.y;
                float anchorRight = root.AnchorMax.X * canvasSize.x;
                float anchorTop = root.AnchorMax.Y * canvasSize.y;

                // OffsetMin.X is inset from left anchor edge, OffsetMin.Y is inset from bottom
                // OffsetMax.X is inset from right anchor edge, OffsetMax.Y is inset from top
                float left = anchorLeft + root.OffsetMin.X;
                float bottom = anchorBottom + root.OffsetMin.Y;
                float right = anchorRight + root.OffsetMax.X;
                float top = anchorTop + root.OffsetMax.Y;

                float width = right - left;
                float height = top - bottom;

                // For partially-stretched axes (e.g., stretch X but fixed Y), use SizeDelta
                // for the fixed axis dimension
                if (!isStretchX)
                    width = Mathf.Abs(root.SizeDelta.X);
                if (!isStretchY)
                    height = Mathf.Abs(root.SizeDelta.Y);

                // Clamp to reasonable minimums
                if (width < 50f) width = canvasSize.x * 0.5f;
                if (height < 50f) height = canvasSize.y * 0.5f;

                // Cap to the workspace content area so the root fits within the editor
                // background. The workspace content is inset by ScreenPadding on
                // left/right/bottom and WorkspaceTopPadding (55) on top.
                float maxW = canvasSize.x - ScreenPadding * 2f;
                float maxH = canvasSize.y - ScreenPadding - 55f;
                width = Mathf.Min(width, maxW);
                height = Mathf.Min(height, maxH);

                // Convert to centered fixed-size
                root.AnchorMin = new Vector2Ser(0.5f, 0.5f);
                root.AnchorMax = new Vector2Ser(0.5f, 0.5f);
                root.Pivot = new Vector2Ser(0.5f, 0.5f);
                root.AnchoredPosition = new Vector2Ser(0, 0);
                root.SizeDelta = new Vector2Ser(width, height);
                root.OffsetMin = new Vector2Ser(0, 0);
                root.OffsetMax = new Vector2Ser(0, 0);
            }
            else
            {
                // Fixed-size root � re-center it so it appears in the middle of the workspace
                // instead of being positioned relative to its original parent's corner.
                // Preserve its original size but move it to center.
                float w = root.SizeDelta.X;
                float h = root.SizeDelta.Y;
                if (w < 50f) w = 300f;
                if (h < 50f) h = 200f;

                // Cap to the workspace content area
                float maxFixedW = canvasSize.x - ScreenPadding * 2f;
                float maxFixedH = canvasSize.y - ScreenPadding - 55f;
                w = Mathf.Min(w, maxFixedW);
                h = Mathf.Min(h, maxFixedH);

                root.AnchorMin = new Vector2Ser(0.5f, 0.5f);
                root.AnchorMax = new Vector2Ser(0.5f, 0.5f);
                root.Pivot = new Vector2Ser(0.5f, 0.5f);
                root.AnchoredPosition = new Vector2Ser(0, 0);
                root.SizeDelta = new Vector2Ser(w, h);
                root.OffsetMin = new Vector2Ser(0, 0);
                root.OffsetMax = new Vector2Ser(0, 0);
            }
        }

        /// <summary>
        /// Recursively applies tag hints by matching GameObject names to tags.
        /// </summary>
        private static void ApplyTagHints(UIElementNode node, Dictionary<string, string> hints)
        {
            if (node == null || hints == null) return;

            foreach (var hint in hints)
            {
                if (!string.IsNullOrEmpty(node.Name) &&
                    node.Name.IndexOf(hint.Key, StringComparison.OrdinalIgnoreCase) >= 0 &&
                    string.IsNullOrEmpty(node.Tag))
                {
                    node.Tag = hint.Value;
                }
            }

            if (node.Children != null)
            {
                for (int i = 0; i < node.Children.Count; i++)
                    ApplyTagHints(node.Children[i], hints);
            }
        }

        /// <summary>
        /// Describes a known capturable UI target.
        /// </summary>
        public class CaptureTarget
        {
            public string DisplayName;
            public string UID;
            public string Category;
            public Func<GameObject> FindRoot;
            public Dictionary<string, string> TagHints;
        }

        /// <summary>
        /// Describes a vanilla Valheim UI capture target.
        /// Uses ParseFlags.Vanilla for deep capture with legacy text support.
        /// </summary>
        public class VanillaCaptureTarget
        {
            public string DisplayName;
            public string UID;
            public string Category;
            public Func<GameObject> FindRoot;
            public Dictionary<string, string> TagHints;
        }
    }
}
