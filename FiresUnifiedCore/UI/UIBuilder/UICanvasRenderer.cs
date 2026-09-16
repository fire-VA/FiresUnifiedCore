using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI
{
    /// <summary>
    /// Instantiates a UILayoutDefinition into live Unity GameObjects.
    /// Read-only renderer - creates the runtime hierarchy from the data model.
    /// Every instantiated element gets a UIBuilderElementTag linking it back to its node.
    /// </summary>
    public static class UICanvasRenderer
    {
        /// <summary>
        /// Instantiate a full layout under the given parent transform.
        /// Returns the root GameObject, or null if the layout is empty.
        /// </summary>
        public static GameObject Instantiate(UILayoutDefinition layout, Transform parent)
        {
            if (layout == null || layout.RootElement == null || parent == null) return null;
            var root = InstantiateNode(layout.RootElement, parent, layout, null);
            return root;
        }

        /// <summary>
        /// Instantiate with a callback invoked for every node, allowing the caller
        /// to populate a lookup cache as nodes are created.
        /// </summary>
        public static GameObject Instantiate(UILayoutDefinition layout, Transform parent,
            Action<string, GameObject> onNodeCreated)
        {
            if (layout == null || layout.RootElement == null || parent == null) return null;
            var root = InstantiateNode(layout.RootElement, parent, layout, onNodeCreated);
            return root;
        }

        /// <summary>
        /// Tier 2: Incremental instantiation coroutine that yields after every batchSize nodes.
        /// Each yield is a scheduling boundary for FrameBudgetScheduler.
        /// </summary>
        public static IEnumerator InstantiateIncremental(UILayoutDefinition layout, Transform parent,
            Action<string, GameObject> onNodeCreated, int batchSize = 20)
        {
            if (layout == null || layout.RootElement == null || parent == null) yield break;
            int counter = 0;
            var stack = new Stack<System.Tuple<UIElementNode, Transform>>();
            stack.Push(new System.Tuple<UIElementNode, Transform>(layout.RootElement, parent));

            while (stack.Count > 0)
            {
                var item = stack.Pop();
                var node = item.Item1;
                var nodeParent = item.Item2;
                if (node == null) continue;

                var go = InstantiateNode(node, nodeParent, layout, onNodeCreated, skipChildren: true);
                if (go == null) continue;

                // Queue children in reverse order so they get processed in forward order
                Transform childParent = go.transform;
                // For ScrollView, children go into Content
                if (node.Type == UIElementType.ScrollView)
                {
                    var scrollRect = go.GetComponent<UnityEngine.UI.ScrollRect>();
                    if (scrollRect != null && scrollRect.content != null)
                        childParent = scrollRect.content;
                }

                if (node.Children != null)
                {
                    for (int childIndex = node.Children.Count - 1; childIndex >= 0; childIndex--)
                        stack.Push(new System.Tuple<UIElementNode, Transform>(node.Children[childIndex], childParent));
                }

                counter++;
                if (counter >= batchSize)
                {
                    counter = 0;
                    yield return null; // yield to scheduler
                }
            }
        }

        /// <summary>
        /// Destroys a previously instantiated layout root.
        /// </summary>
        public static void Destroy(GameObject root)
        {
            if (root != null) UnityEngine.Object.Destroy(root);
        }

        /// <summary>
        /// Finds the first element GameObject with the given tag in the hierarchy.
        /// </summary>
        public static GameObject FindElementByTag(GameObject root, string tag)
        {
            if (root == null || string.IsNullOrEmpty(tag)) return null;
            var tags = root.GetComponentsInChildren<UIBuilderElementTag>(true);
            for (int i = 0; i < tags.Length; i++)
            {
                if (string.Equals(tags[i].ElementTag, tag, StringComparison.OrdinalIgnoreCase))
                    return tags[i].gameObject;
            }
            return null;
        }

        /// <summary>
        /// Finds all element GameObjects with the given tag.
        /// </summary>
        public static List<GameObject> FindAllByTag(GameObject root, string tag)
        {
            var result = new List<GameObject>();
            if (root == null || string.IsNullOrEmpty(tag)) return result;
            var tags = root.GetComponentsInChildren<UIBuilderElementTag>(true);
            for (int i = 0; i < tags.Length; i++)
            {
                if (string.Equals(tags[i].ElementTag, tag, StringComparison.OrdinalIgnoreCase))
                    result.Add(tags[i].gameObject);
            }
            return result;
        }

        /// <summary>
        /// Binds action callbacks to buttons in the hierarchy by their ClickAction string.
        /// </summary>
        public static void BindActions(GameObject root, Dictionary<string, Action> actionMap)
        {
            if (root == null || actionMap == null) return;
            var tags = root.GetComponentsInChildren<UIBuilderElementTag>(true);
            for (int i = 0; i < tags.Length; i++)
            {
                var node = tags[i].Node;
                if (node == null || node.ButtonData == null) continue;
                string action = node.ButtonData.ClickAction;
                if (string.IsNullOrEmpty(action)) continue;
                if (actionMap.TryGetValue(action, out Action callback))
                {
                    var btn = tags[i].GetComponent<Button>();
                    if (btn != null)
                    {
                        Action captured = callback;
                        btn.onClick.AddListener(() => captured());
                    }
                }
            }
        }

        //  Node instantiation (recursive)

        private static GameObject InstantiateNode(UIElementNode node, Transform parent,
            UILayoutDefinition layout, Action<string, GameObject> onNodeCreated = null,
            bool skipChildren = false)
        {
            if (node == null) return null;

            var go = new GameObject(string.IsNullOrEmpty(node.Name) ? node.Type.ToString() : node.Name);
            go.transform.SetParent(parent, false);

            // Tag
            var tag = go.AddComponent<UIBuilderElementTag>();
            tag.Node = node;
            tag.Layout = layout;
            tag.ElementId = node.Id;
            tag.ElementTag = node.Tag;

            // Notify caller for cache population
            onNodeCreated?.Invoke(node.Id, go);

            // RectTransform (already added by SetParent on UI hierarchy, but ensure it exists)
            var rect = go.GetComponent<RectTransform>();
            if (rect == null) rect = go.AddComponent<RectTransform>();
            ApplyTransform(rect, node);

            // Type-specific components
            switch (node.Type)
            {
                case UIElementType.Panel:
                    BuildPanel(go, node);
                    break;
                case UIElementType.Image:
                    BuildImage(go, node);
                    break;
                case UIElementType.Text:
                    BuildText(go, node);
                    break;
                case UIElementType.Button:
                    BuildButton(go, node);
                    break;
                case UIElementType.InputField:
                    BuildInputField(go, node);
                    break;
                case UIElementType.ScrollView:
                    BuildScrollView(go, node, layout, onNodeCreated, skipChildren);
                    break;
                case UIElementType.Dropdown:
                    BuildDropdown(go, node);
                    break;
                case UIElementType.Toggle:
                    BuildToggle(go, node);
                    break;
                case UIElementType.Slider:
                    BuildSlider(go, node);
                    break;
                case UIElementType.Divider:
                    BuildDivider(go, node);
                    break;
                case UIElementType.Spacer:
                    BuildSpacer(go, node);
                    break;
                case UIElementType.RenderCamera:
                    BuildRenderCamera(go, node);
                    break;
            }

            // Layout components
            ApplyLayoutGroup(go, node.LayoutGroup);
            ApplyGridLayoutGroup(go, node.GridLayoutGroup);
            ApplyLayoutElement(go, node.LayoutElement);
            ApplyContentFitter(go, node.ContentFitter);

            // Masks
            if (node.HasRectMask2D) go.AddComponent<RectMask2D>();
            else if (node.HasMask) go.AddComponent<Mask>();

            // Opacity via CanvasGroup
            if (node.Style != null && node.Style.Opacity < 1f)
            {
                var canvasGroup = go.AddComponent<CanvasGroup>();
                canvasGroup.alpha = node.Style.Opacity;
            }

            // Active state
            go.SetActive(node.Active);

            // Children (ScrollView handles its own children via Content)
            if (!skipChildren && node.Type != UIElementType.ScrollView && node.Children != null)
            {
                for (int i = 0; i < node.Children.Count; i++)
                    InstantiateNode(node.Children[i], go.transform, layout, onNodeCreated);
            }

            return go;
        }

        //  Transform

        private static void ApplyTransform(RectTransform rect, UIElementNode node)
        {
            rect.anchorMin = node.AnchorMin.ToVector2();
            rect.anchorMax = node.AnchorMax.ToVector2();
            rect.pivot = node.Pivot.ToVector2();

            // For stretch-anchored elements (anchors differ on either axis),
            // offsetMin/offsetMax are the authoritative positioning data.
            // Always apply them when anchors are stretched.
            bool isStretchX = !Mathf.Approximately(node.AnchorMin.X, node.AnchorMax.X);
            bool isStretchY = !Mathf.Approximately(node.AnchorMin.Y, node.AnchorMax.Y);

            if (isStretchX || isStretchY)
            {
                // Set offsets directly - they define the edges relative to anchored parent area
                rect.offsetMin = node.OffsetMin.ToVector2();
                rect.offsetMax = node.OffsetMax.ToVector2();
            }
            else
            {
                // Fixed-size element: use anchoredPosition + sizeDelta
                rect.anchoredPosition = node.AnchoredPosition.ToVector2();
                rect.sizeDelta = node.SizeDelta.ToVector2();
            }

            if (node.Rotation != 0)
                rect.localRotation = Quaternion.Euler(0, 0, node.Rotation);
        }

        //  Element builders

        private static void BuildPanel(GameObject go, UIElementNode node)
        {
            var style = node.Style;
            if (style == null) return;

            // Always add an Image so the panel has bounds for selection/raycasting.
            // Transparent panels (alpha 0) still need the Image component for
            // the editor to detect them during hit-testing.
            var img = go.AddComponent<Image>();

            // If the panel has detailed ImageData (e.g., it was an Image-with-children
            // reclassified as Panel during capture), use ImageData for more accurate
            // rendering - it preserves the actual sprite, image type, fill center,
            // pixels-per-unit, and the precise Image.color including alpha.
            if (node.ImageData != null && !string.IsNullOrEmpty(node.ImageData.SpriteName))
            {
                img.color = node.ImageData.Color.ToColor();
                img.type = (Image.Type)node.ImageData.ImageType;
                img.preserveAspect = node.ImageData.PreserveAspect;
                img.fillCenter = node.ImageData.FillCenter;
                img.pixelsPerUnitMultiplier = node.ImageData.PixelsPerUnit;
                img.raycastTarget = style.RaycastTarget;
                ApplySprite(img, node.ImageData.SpriteName);
                img.enabled = node.ImageData.ComponentEnabled;
            }
            else
            {
                var color = style.BackgroundColor.ToColor();
                img.color = color;
                img.raycastTarget = style.RaycastTarget;
                img.type = (Image.Type)style.ImageType;
                ApplySprite(img, style.BackgroundSprite);
            }
        }

        private static void BuildImage(GameObject go, UIElementNode node)
        {
            var img = go.AddComponent<Image>();
            if (node.ImageData != null)
            {
                img.color = node.ImageData.Color.ToColor();
                img.type = (Image.Type)node.ImageData.ImageType;
                img.preserveAspect = node.ImageData.PreserveAspect;
                img.fillCenter = node.ImageData.FillCenter;
                img.pixelsPerUnitMultiplier = node.ImageData.PixelsPerUnit;
                ApplySprite(img, node.ImageData.SpriteName);
                img.enabled = node.ImageData.ComponentEnabled;
            }
            else
            {
                img.color = node.Style?.BackgroundColor.ToColor() ?? Color.white;
            }
            img.raycastTarget = node.Style?.RaycastTarget ?? true;
        }

        private static void BuildText(GameObject go, UIElementNode node)
        {
            var tmp = go.AddComponent<TextMeshProUGUI>();
            var textData = node.TextData;
            if (textData != null)
            {
                tmp.text = textData.Text ?? "";
                tmp.fontSize = textData.FontSize;
                tmp.fontStyle = (FontStyles)textData.FontStyle;
                tmp.color = textData.Color.ToColor();
                tmp.alignment = (TextAlignmentOptions)textData.Alignment;
                tmp.textWrappingMode = textData.WordWrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
                tmp.overflowMode = (TextOverflowModes)textData.OverflowMode;
                tmp.lineSpacing = textData.LineSpacing;
                tmp.margin = new Vector4(textData.TextPadding.Left, textData.TextPadding.Top, textData.TextPadding.Right, textData.TextPadding.Bottom);
                ApplyFont(tmp, textData.FontCategory);
                tmp.enabled = textData.ComponentEnabled;
            }
            tmp.raycastTarget = node.Style?.RaycastTarget ?? false;
        }

        private static void BuildButton(GameObject go, UIElementNode node)
        {
            var buttonData = node.ButtonData ?? new UIButtonDef();

            // Background image - use the actual Image.color captured in ImageData
            // if available. This preserves transparency (e.g., dropButton with alpha=0).
            // Fall back to NormalColor only if ImageData wasn't captured.
            var img = go.AddComponent<Image>();
            if (node.ImageData != null)
            {
                img.color = node.ImageData.Color.ToColor();
                img.type = (Image.Type)node.ImageData.ImageType;
                img.preserveAspect = node.ImageData.PreserveAspect;
                img.fillCenter = node.ImageData.FillCenter;
                img.pixelsPerUnitMultiplier = node.ImageData.PixelsPerUnit;
                ApplySprite(img, node.ImageData.SpriteName);
            }
            else
            {
                img.color = buttonData.NormalColor.ToColor();
                if (node.Style != null)
                {
                    img.type = (Image.Type)node.Style.ImageType;
                    ApplySprite(img, node.Style.BackgroundSprite);
                }
            }
            img.raycastTarget = true;

            // Button component
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.interactable = node.Interactable;
            btn.enabled = buttonData.ComponentEnabled;

            var colors = btn.colors;
            colors.normalColor = buttonData.NormalColor.ToColor();
            colors.highlightedColor = buttonData.HighlightedColor.ToColor();
            colors.pressedColor = buttonData.PressedColor.ToColor();
            colors.selectedColor = buttonData.SelectedColor.ToColor();
            colors.disabledColor = buttonData.DisabledColor.ToColor();
            colors.fadeDuration = buttonData.FadeDuration;
            btn.colors = colors;

            // Label text child
            var textGo = new GameObject("Label");
            textGo.transform.SetParent(go.transform, false);
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(buttonData.LabelPadding.Left, buttonData.LabelPadding.Bottom);
            textRect.offsetMax = new Vector2(-buttonData.LabelPadding.Right, -buttonData.LabelPadding.Top);

            var tmp = textGo.AddComponent<TextMeshProUGUI>();
            tmp.text = buttonData.Label ?? "Button";
            tmp.fontSize = buttonData.FontSize;
            tmp.color = buttonData.LabelColor.ToColor();
            tmp.alignment = (TextAlignmentOptions)buttonData.LabelAlignment;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.raycastTarget = false;
            ApplyFont(tmp, "Body");

            // Hide label by default; only show when explicitly enabled
            if (!buttonData.ShowLabel)
                textGo.SetActive(false);
        }

        private static void BuildInputField(GameObject go, UIElementNode node)
        {
            var inputFieldData = node.InputFieldData ?? new UIInputFieldDef();

            // Background
            var background = go.AddComponent<Image>();
            background.color = inputFieldData.BackgroundColor.ToColor();
            background.raycastTarget = true;
            if (node.Style != null)
            {
                background.type = (Image.Type)node.Style.ImageType;
                ApplySprite(background, node.Style.BackgroundSprite);
            }

            // Viewport
            var viewport = CreateChild(go.transform, "Viewport");
            var vpRect = viewport.AddComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.offsetMin = new Vector2(inputFieldData.TextPadding.Left, inputFieldData.TextPadding.Bottom);
            vpRect.offsetMax = new Vector2(-inputFieldData.TextPadding.Right, -inputFieldData.TextPadding.Top);
            viewport.AddComponent<RectMask2D>();

            // Text
            var textGo = CreateChild(viewport.transform, "Text");
            var textRect = textGo.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            var txt = textGo.AddComponent<TextMeshProUGUI>();
            txt.fontSize = inputFieldData.FontSize;
            txt.color = inputFieldData.TextColor.ToColor();
            txt.alignment = (TextAlignmentOptions)inputFieldData.TextAlignment;
            txt.textWrappingMode = inputFieldData.Multiline ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            txt.raycastTarget = false;
            ApplyFont(txt, "Body");

            // Placeholder
            var placeholderGo = CreateChild(viewport.transform, "Placeholder");
            var phRect = placeholderGo.AddComponent<RectTransform>();
            phRect.anchorMin = Vector2.zero;
            phRect.anchorMax = Vector2.one;
            phRect.offsetMin = Vector2.zero;
            phRect.offsetMax = Vector2.zero;
            var label = placeholderGo.AddComponent<TextMeshProUGUI>();
            label.text = inputFieldData.PlaceholderText ?? "";
            label.fontSize = inputFieldData.FontSize;
            label.color = inputFieldData.PlaceholderColor.ToColor();
            label.fontStyle = FontStyles.Italic;
            label.alignment = (TextAlignmentOptions)inputFieldData.TextAlignment;
            label.raycastTarget = false;
            ApplyFont(label, "Body");

            // TMP_InputField
            var input = go.AddComponent<TMP_InputField>();
            input.textViewport = vpRect;
            input.textComponent = txt;
            input.placeholder = label;
            input.fontAsset = txt.font;
            input.pointSize = inputFieldData.FontSize;
            input.lineType = inputFieldData.Multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
            input.characterLimit = inputFieldData.CharacterLimit;
            input.contentType = StringToContentType(inputFieldData.ContentType);
            input.caretColor = inputFieldData.CaretColor.ToColor();
            input.caretWidth = 1;
            input.customCaretColor = true;
            input.selectionColor = inputFieldData.SelectionColor.ToColor();
            input.interactable = node.Interactable;
            input.enabled = inputFieldData.ComponentEnabled;
        }

        private static void BuildScrollView(GameObject go, UIElementNode node, UILayoutDefinition layout,
            Action<string, GameObject> onNodeCreated, bool skipChildren)
        {
            var scrollViewData = node.ScrollViewData ?? new UIScrollViewDef();

            // Background on the scroll root
            if (node.Style != null)
            {
                var image = go.AddComponent<Image>();
                image.color = node.Style.BackgroundColor.ToColor();
                image.raycastTarget = node.Style.RaycastTarget;
                image.type = (Image.Type)node.Style.ImageType;
                ApplySprite(image, node.Style.BackgroundSprite);
            }

            // Viewport
            var viewport = CreateChild(go.transform, "Viewport");
            var vpRect = viewport.AddComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.offsetMin = new Vector2(2, 2);
            vpRect.offsetMax = new Vector2(-2, -2);
            viewport.AddComponent<RectMask2D>();
            var viewportImage = viewport.AddComponent<Image>();
            viewportImage.color = scrollViewData.ViewportColor.ToColor();
            viewportImage.raycastTarget = true;

            // Content
            var content = CreateChild(viewport.transform, "Content");
            var contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0, 1);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = new Vector2(0, 0);

            // ScrollRect
            var scroll = go.AddComponent<ScrollRect>();
            scroll.horizontal = scrollViewData.Horizontal;
            scroll.vertical = scrollViewData.Vertical;
            scroll.viewport = vpRect;
            scroll.content = contentRect;
            scroll.movementType = (ScrollRect.MovementType)scrollViewData.MovementType;
            scroll.scrollSensitivity = scrollViewData.ScrollSensitivity;
            scroll.elasticity = scrollViewData.Elasticity;
            scroll.enabled = scrollViewData.ComponentEnabled;
            if (!skipChildren && node.Children != null)
            {
                for (int i = 0; i < node.Children.Count; i++)
                    InstantiateNode(node.Children[i], content.transform, layout, onNodeCreated);
            }
        }

        private static void BuildDropdown(GameObject go, UIElementNode node)
        {
            var dropdownData = node.DropdownData ?? new UIDropdownDef();

            // Background
            var background = go.AddComponent<Image>();
            background.color = dropdownData.BackgroundColor.ToColor();
            background.raycastTarget = true;
            if (node.Style != null)
            {
                background.type = (Image.Type)node.Style.ImageType;
                ApplySprite(background, node.Style.BackgroundSprite);
            }

            var dropdown = go.AddComponent<TMP_Dropdown>();

            // Caption label
            var labelGo = CreateChild(go.transform, "Label");
            var labelRect = labelGo.AddComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = new Vector2(6, 2);
            labelRect.offsetMax = new Vector2(-20, -2);
            var labelTmp = labelGo.AddComponent<TextMeshProUGUI>();
            labelTmp.fontSize = dropdownData.FontSize;
            labelTmp.color = dropdownData.TextColor.ToColor();
            labelTmp.alignment = TextAlignmentOptions.MidlineLeft;
            labelTmp.textWrappingMode = TextWrappingModes.NoWrap;
            labelTmp.raycastTarget = false;
            ApplyFont(labelTmp, "Body");
            dropdown.captionText = labelTmp;

            // Arrow
            var arrowGo = CreateChild(go.transform, "Arrow");
            var arrowRect = arrowGo.AddComponent<RectTransform>();
            arrowRect.anchorMin = new Vector2(1, 0.5f);
            arrowRect.anchorMax = new Vector2(1, 0.5f);
            arrowRect.pivot = new Vector2(1, 0.5f);
            arrowRect.anchoredPosition = new Vector2(-4, 0);
            arrowRect.sizeDelta = new Vector2(14, 14);
            var arrowTxt = arrowGo.AddComponent<TextMeshProUGUI>();
            arrowTxt.text = "\u25BC";
            arrowTxt.fontSize = 8;
            arrowTxt.color = dropdownData.TextColor.ToColor();
            arrowTxt.alignment = TextAlignmentOptions.Center;
            arrowTxt.raycastTarget = false;
            ApplyFont(arrowTxt, "Body");

            // Template
            var template = CreateChild(go.transform, "Template");
            var templateRect = template.AddComponent<RectTransform>();
            templateRect.anchorMin = new Vector2(0, 0);
            templateRect.anchorMax = new Vector2(1, 0);
            templateRect.pivot = new Vector2(0.5f, 1);
            templateRect.anchoredPosition = Vector2.zero;
            templateRect.sizeDelta = new Vector2(0, dropdownData.TemplateHeight);
            var templateBg = template.AddComponent<Image>();
            templateBg.color = new Color(0.08f, 0.08f, 0.1f, 0.98f);
            var templateScroll = template.AddComponent<ScrollRect>();

            var viewportGo = CreateChild(template.transform, "Viewport");
            var viewportRect = viewportGo.AddComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = new Vector2(2, 2);
            viewportRect.offsetMax = new Vector2(-2, -2);
            viewportGo.AddComponent<RectMask2D>();

            var contentGo = CreateChild(viewportGo.transform, "Content");
            var contentRect = contentGo.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0, 1);
            contentRect.anchorMax = new Vector2(1, 1);
            contentRect.pivot = new Vector2(0, 1);
            contentRect.anchoredPosition = Vector2.zero;
            contentRect.sizeDelta = Vector2.zero;
            var contentVlg = contentGo.AddComponent<VerticalLayoutGroup>();
            contentVlg.spacing = 1;
            contentVlg.childControlWidth = true;
            contentVlg.childControlHeight = false;
            contentVlg.childForceExpandWidth = true;
            contentVlg.childForceExpandHeight = false;
            var contentFitter = contentGo.AddComponent<ContentSizeFitter>();
            contentFitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            templateScroll.viewport = viewportRect;
            templateScroll.content = contentRect;
            templateScroll.horizontal = false;
            templateScroll.vertical = true;
            templateScroll.movementType = ScrollRect.MovementType.Clamped;

            // Item template
            var itemGo = CreateChild(contentGo.transform, "Item");
            itemGo.AddComponent<RectTransform>().sizeDelta = new Vector2(0, dropdownData.ItemHeight);
            var itemLe = itemGo.AddComponent<LayoutElement>();
            itemLe.preferredHeight = dropdownData.ItemHeight;
            itemLe.flexibleWidth = 1;
            var itemBg = itemGo.AddComponent<Image>();
            itemBg.color = dropdownData.ItemColor.ToColor();
            var itemToggle = itemGo.AddComponent<Toggle>();
            itemToggle.targetGraphic = itemBg;

            var itemLabelGo = CreateChild(itemGo.transform, "Item Label");
            var itemLabelRect = itemLabelGo.AddComponent<RectTransform>();
            itemLabelRect.anchorMin = Vector2.zero;
            itemLabelRect.anchorMax = Vector2.one;
            itemLabelRect.offsetMin = new Vector2(6, 0);
            itemLabelRect.offsetMax = new Vector2(-6, 0);
            var itemLabel = itemLabelGo.AddComponent<TextMeshProUGUI>();
            itemLabel.fontSize = dropdownData.FontSize;
            itemLabel.color = dropdownData.TextColor.ToColor();
            itemLabel.alignment = TextAlignmentOptions.MidlineLeft;
            itemLabel.textWrappingMode = TextWrappingModes.NoWrap;
            itemLabel.raycastTarget = false;
            ApplyFont(itemLabel, "Body");

            dropdown.template = templateRect;
            dropdown.itemText = itemLabel;
            template.SetActive(false);

            dropdown.ClearOptions();
            if (dropdownData.Options != null && dropdownData.Options.Count > 0)
                dropdown.AddOptions(dropdownData.Options);
            dropdown.value = dropdownData.DefaultValue;
            dropdown.enabled = dropdownData.ComponentEnabled;
        }

        private static void BuildToggle(GameObject go, UIElementNode node)
        {
            var toggleData = node.ToggleData ?? new UIToggleDef();

            // Background
            var image = go.AddComponent<Image>();
            image.color = toggleData.BackgroundColor.ToColor();
            image.raycastTarget = true;
            if (node.Style != null)
            {
                image.type = (Image.Type)node.Style.ImageType;
                ApplySprite(image, node.Style.BackgroundSprite);
            }

            // Checkmark
            var checkGo = CreateChild(go.transform, "Checkmark");
            var checkRect = checkGo.AddComponent<RectTransform>();
            checkRect.anchorMin = new Vector2(0, 0);
            checkRect.anchorMax = new Vector2(0, 1);
            checkRect.pivot = new Vector2(0, 0.5f);
            checkRect.anchoredPosition = new Vector2(2, 0);
            checkRect.sizeDelta = new Vector2(20, 0);
            var checkTxt = checkGo.AddComponent<TextMeshProUGUI>();
            checkTxt.text = "\u2713";
            checkTxt.fontSize = 16;
            checkTxt.color = toggleData.CheckmarkColor.ToColor();
            checkTxt.alignment = TextAlignmentOptions.Center;
            checkTxt.raycastTarget = false;
            ApplyFont(checkTxt, "Body");

            // Label
            if (!string.IsNullOrEmpty(toggleData.Label))
            {
                var labelGo = CreateChild(go.transform, "Label");
                var labelRect = labelGo.AddComponent<RectTransform>();
                labelRect.anchorMin = new Vector2(0, 0);
                labelRect.anchorMax = new Vector2(1, 1);
                labelRect.offsetMin = new Vector2(24, 0);
                labelRect.offsetMax = new Vector2(-2, 0);
                var labelTmp = labelGo.AddComponent<TextMeshProUGUI>();
                labelTmp.text = toggleData.Label;
                labelTmp.fontSize = toggleData.FontSize;
                labelTmp.color = toggleData.LabelColor.ToColor();
                labelTmp.alignment = TextAlignmentOptions.MidlineLeft;
                labelTmp.raycastTarget = false;
                ApplyFont(labelTmp, "Body");
            }

            var toggle = go.AddComponent<Toggle>();
            toggle.targetGraphic = image;
            toggle.graphic = checkTxt;
            toggle.isOn = toggleData.DefaultValue;
            toggle.interactable = node.Interactable;
            toggle.enabled = toggleData.ComponentEnabled;
        }

        private static void BuildSlider(GameObject go, UIElementNode node)
        {
            var sliderData = node.SliderData ?? new UISliderDef();

            // Background
            var backgroundGo = CreateChild(go.transform, "Background");
            var bgRect = backgroundGo.AddComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            var background = backgroundGo.AddComponent<Image>();
            background.color = sliderData.BackgroundColor.ToColor();
            background.raycastTarget = true;

            // Fill area
            var fillArea = CreateChild(go.transform, "FillArea");
            var fillAreaRect = fillArea.AddComponent<RectTransform>();
            fillAreaRect.anchorMin = new Vector2(0, 0.25f);
            fillAreaRect.anchorMax = new Vector2(1, 0.75f);
            fillAreaRect.offsetMin = new Vector2(5, 0);
            fillAreaRect.offsetMax = new Vector2(-5, 0);

            var fill = CreateChild(fillArea.transform, "Fill");
            var fillRect = fill.AddComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;
            var fillImg = fill.AddComponent<Image>();
            fillImg.color = sliderData.FillColor.ToColor();

            // Handle area
            var handleArea = CreateChild(go.transform, "HandleSlideArea");
            var handleAreaRect = handleArea.AddComponent<RectTransform>();
            handleAreaRect.anchorMin = Vector2.zero;
            handleAreaRect.anchorMax = Vector2.one;
            handleAreaRect.offsetMin = new Vector2(10, 0);
            handleAreaRect.offsetMax = new Vector2(-10, 0);

            var handle = CreateChild(handleArea.transform, "Handle");
            var handleRect = handle.AddComponent<RectTransform>();
            handleRect.sizeDelta = new Vector2(16, 0);
            var handleImg = handle.AddComponent<Image>();
            handleImg.color = sliderData.HandleColor.ToColor();
            handleImg.raycastTarget = true;

            var slider = go.AddComponent<Slider>();
            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.targetGraphic = handleImg;
            slider.minValue = sliderData.MinValue;
            slider.maxValue = sliderData.MaxValue;
            slider.value = sliderData.DefaultValue;
            slider.wholeNumbers = sliderData.WholeNumbers;
            slider.interactable = node.Interactable;
            slider.enabled = sliderData.ComponentEnabled;
        }

        private static void BuildDivider(GameObject go, UIElementNode node)
        {
            var img = go.AddComponent<Image>();
            img.color = node.Style?.BorderColor.ToColor() ?? new Color(0.4f, 0.3f, 0.15f, 0.6f);
            img.raycastTarget = false;
        }

        private static void BuildSpacer(GameObject go, UIElementNode node)
        {
            // Just an empty GameObject with a LayoutElement (applied separately)
        }

        private static void BuildRenderCamera(GameObject go, UIElementNode node)
        {
            var def = node.RenderCameraData ?? new UIRenderCameraDef();

            int texW = Mathf.Max(32, def.TextureWidth);
            int texH = Mathf.Max(32, def.TextureHeight);
            var renderTexture = new RenderTexture(texW, texH, 16, RenderTextureFormat.ARGB32);
            renderTexture.name = "UIBuilderPreviewRT";
            renderTexture.Create();

            var rawImg = go.AddComponent<RawImage>();
            rawImg.texture = renderTexture;
            rawImg.color = def.BackgroundColor.ToColor();
            rawImg.enabled = def.ComponentEnabled;

            // Create a child camera
            var camGo = new GameObject("PreviewCamera");
            camGo.transform.SetParent(go.transform, false);
            // Position camera off-screen so it only renders to the RT
            camGo.transform.localPosition = new Vector3(5000f, 5000f, 0f);

            var cam = camGo.AddComponent<Camera>();
            cam.targetTexture = renderTexture;
            cam.fieldOfView = def.FieldOfView;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = def.BackgroundColor.ToColor();
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            cam.enabled = false; // Disabled by default; activated by runtime binding

            // Tag the camera for runtime scripts to find and configure
            var tag = go.GetComponent<UIBuilderElementTag>();
            if (tag != null)
                tag.PreviewCamera = cam;
        }

        //  Layout component helpers

        private static void ApplyLayoutGroup(GameObject go, UILayoutGroupDef def)
        {
            if (def == null) return;
            if (def.IsVertical)
            {
                var verticalLayout = go.AddComponent<VerticalLayoutGroup>();
                verticalLayout.spacing = def.Spacing;
                verticalLayout.padding = def.Padding.ToRectOffset();
                verticalLayout.childAlignment = (TextAnchor)def.ChildAlignment;
                verticalLayout.childControlWidth = def.ChildControlWidth;
                verticalLayout.childControlHeight = def.ChildControlHeight;
                verticalLayout.childForceExpandWidth = def.ChildForceExpandWidth;
                verticalLayout.childForceExpandHeight = def.ChildForceExpandHeight;
            }
            else
            {
                var horizontalLayout = go.AddComponent<HorizontalLayoutGroup>();
                horizontalLayout.spacing = def.Spacing;
                horizontalLayout.padding = def.Padding.ToRectOffset();
                horizontalLayout.childAlignment = (TextAnchor)def.ChildAlignment;
                horizontalLayout.childControlWidth = def.ChildControlWidth;
                horizontalLayout.childControlHeight = def.ChildControlHeight;
                horizontalLayout.childForceExpandWidth = def.ChildForceExpandWidth;
                horizontalLayout.childForceExpandHeight = def.ChildForceExpandHeight;
            }
        }

        private static void ApplyLayoutElement(GameObject go, UILayoutElementDef def)
        {
            if (def == null) return;
            var layoutElement = go.AddComponent<LayoutElement>();
            if (def.MinWidth >= 0) layoutElement.minWidth = def.MinWidth;
            if (def.MinHeight >= 0) layoutElement.minHeight = def.MinHeight;
            if (def.PreferredWidth >= 0) layoutElement.preferredWidth = def.PreferredWidth;
            if (def.PreferredHeight >= 0) layoutElement.preferredHeight = def.PreferredHeight;
            if (def.FlexibleWidth >= 0) layoutElement.flexibleWidth = def.FlexibleWidth;
            if (def.FlexibleHeight >= 0) layoutElement.flexibleHeight = def.FlexibleHeight;
            layoutElement.ignoreLayout = def.IgnoreLayout;
        }

        private static void ApplyGridLayoutGroup(GameObject go, UIGridLayoutGroupDef def)
        {
            if (def == null) return;
            var gridLayout = go.AddComponent<GridLayoutGroup>();
            gridLayout.cellSize = def.CellSize.ToVector2();
            gridLayout.spacing = def.Spacing.ToVector2();
            gridLayout.startCorner = (GridLayoutGroup.Corner)def.StartCorner;
            gridLayout.startAxis = (GridLayoutGroup.Axis)def.StartAxis;
            gridLayout.childAlignment = (TextAnchor)def.ChildAlignment;
            gridLayout.constraint = (GridLayoutGroup.Constraint)def.Constraint;
            gridLayout.constraintCount = def.ConstraintCount;
            gridLayout.padding = def.Padding.ToRectOffset();
        }

        private static void ApplyContentFitter(GameObject go, UIContentFitterDef def)
        {
            if (def == null) return;
            var fitter = go.AddComponent<ContentSizeFitter>();
            fitter.horizontalFit = (ContentSizeFitter.FitMode)def.HorizontalFit;
            fitter.verticalFit = (ContentSizeFitter.FitMode)def.VerticalFit;
        }

        //  Helpers

        private static GameObject CreateChild(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go;
        }

        private static void ApplyFont(TMP_Text text, string category)
        {
            if (text == null) return;
            if (string.IsNullOrEmpty(category) || category.Equals("Body", StringComparison.OrdinalIgnoreCase))
                UIBuilderHelper.ApplyBodyFont(text);
            else if (category.Equals("Primary", StringComparison.OrdinalIgnoreCase))
                UIBuilderHelper.ApplyPrimaryFont(text);
            else if (category.Equals("Decorative", StringComparison.OrdinalIgnoreCase))
                UIBuilderHelper.ApplyDecorativeFont(text);
            else
                UIBuilderHelper.ApplyBodyFont(text);
        }

        private static TMP_InputField.ContentType StringToContentType(string contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return TMP_InputField.ContentType.Standard;
            switch (contentType)
            {
                case "Autocorrected": return TMP_InputField.ContentType.Autocorrected;
                case "IntegerNumber": return TMP_InputField.ContentType.IntegerNumber;
                case "DecimalNumber": return TMP_InputField.ContentType.DecimalNumber;
                case "Alphanumeric": return TMP_InputField.ContentType.Alphanumeric;
                case "Name": return TMP_InputField.ContentType.Name;
                case "EmailAddress": return TMP_InputField.ContentType.EmailAddress;
                case "Password": return TMP_InputField.ContentType.Password;
                case "Pin": return TMP_InputField.ContentType.Pin;
                case "Custom": return TMP_InputField.ContentType.Custom;
                default: return TMP_InputField.ContentType.Standard;
            }
        }

        private static void ApplySprite(Image img, string spriteName)
        {
            if (img == null || string.IsNullOrEmpty(spriteName)) return;

            // Ensure sprite cache is fresh for current frame
            if (_loadedSpriteCacheDirty)
                FindLoadedSprite("__warmup__");

            // Priority 1: Real sprites from cached asset bundles (full metadata intact)
            var bundleSprite = UIBuilderAssetCache.LoadSpriteFromCachedBundle(spriteName);
            if (bundleSprite != null)
            {
                img.sprite = bundleSprite;
                return;
            }

            // For cached: names, strip the prefix and try all live sources first
            // before falling back to the flat PNG raster. The PNG should be last resort.
            string resolvedName = spriteName;
            bool wasCached = false;
            if (UIBuilderAssetCache.IsCachedName(spriteName))
            {
                resolvedName = UIBuilderAssetCache.GetCacheId(spriteName);
                wasCached = true;

                // Try bundle lookup with stripped name
                bundleSprite = UIBuilderAssetCache.LoadSpriteFromCachedBundle(resolvedName);
                if (bundleSprite != null)
                {
                    img.sprite = bundleSprite;
                    return;
                }
            }

            // Sprite names may be stored in qualified "texture:sprite" format for atlas sprites.
            // Use resolvedName which has the cached: prefix stripped.
            string lookupName = resolvedName;
            string plainName = resolvedName;
            int colonIdx = resolvedName.IndexOf(':');
            if (colonIdx > 0 && colonIdx < resolvedName.Length - 1)
                plainName = resolvedName.Substring(colonIdx + 1);

            // Try bundle with plain name
            bundleSprite = UIBuilderAssetCache.LoadSpriteFromCachedBundle(plainName);
            if (bundleSprite != null)
            {
                img.sprite = bundleSprite;
                return;
            }

            // Priority 3+: UIPrefabFactory, VAMiscAssetManager, loaded sprites in memory

            try
            {
                // 1. Check the mod's known background sprite
                var sprite = UIBuilderHost.GetBackgroundSprite();
                if (sprite != null && sprite.name == plainName)
                {
                    img.sprite = sprite;
                    return;
                }

                // 2. Check VAMiscAssetManager (asset bundle + external Sprites folder)
                sprite = VAMiscAssetManager.GetSprite(plainName);
                if (sprite != null)
                {
                    img.sprite = sprite;
                    return;
                }

                // Also try full lookup name if different from plain
                if (lookupName != plainName)
                {
                    sprite = VAMiscAssetManager.GetSprite(lookupName);
                    if (sprite != null)
                    {
                        img.sprite = sprite;
                        return;
                    }
                }

                // 3. Search all sprites loaded in memory (covers Unity built-in and Valheim game sprites).
                // This is the key path for captured vanilla UIs - sprites like "UISprite",
                // "Background", "InputFieldBackground", "Checkmark", "UIMask", "Knob" etc.
                // are loaded in memory by Unity/Valheim but not in our asset bundles.
                sprite = FindLoadedSprite(lookupName);
                if (sprite != null)
                {
                    img.sprite = sprite;
                    return;
                }

                // Also try plain name in memory
                if (plainName != lookupName)
                {
                    sprite = FindLoadedSprite(plainName);
                    if (sprite != null)
                    {
                        img.sprite = sprite;
                        return;
                    }
                }
            }
            catch
            {
                // Sprite not found from live sources - fall through to cached PNG
            }

            // Last resort: cached PNG from disk (flat rasterized sprite without metadata).
            // This is the fallback for when the source mod is removed and the sprite
            // is no longer in memory or in any loaded bundle.
            if (wasCached)
            {
                var cachedPng = UIBuilderAssetCache.LoadCachedSprite(resolvedName);
                if (cachedPng != null)
                {
                    img.sprite = cachedPng;
                    return;
                }
            }

            // Also try cache manifest by original name
            var cachedByName = UIBuilderAssetCache.LoadCachedSpriteByOriginalName(plainName);
            if (cachedByName != null)
            {
                img.sprite = cachedByName;
                return;
            }
        }

        // Cache for sprites found via Resources scan to avoid repeated searches
        private static readonly Dictionary<string, Sprite> _loadedSpriteCache = new Dictionary<string, Sprite>();
        private static bool _loadedSpriteCacheDirty = true;

        /// <summary>
        /// Searches all Sprite objects currently loaded in memory for one matching the given name.
        /// Results are cached so subsequent lookups are fast.
        /// This finds Unity built-in sprites (UISprite, Background, Checkmark, Knob, etc.)
        /// and any game/mod sprites that happen to be loaded at runtime.
        /// Note: This is a fallback - captured sprites should use the "cached:" prefix path
        /// which loads from disk and doesn't depend on the source mod being loaded.
        /// </summary>
        private static Sprite FindLoadedSprite(string spriteName)
        {
            if (string.IsNullOrEmpty(spriteName)) return null;

            // Check cache first
            if (!_loadedSpriteCacheDirty && _loadedSpriteCache.TryGetValue(spriteName, out var cached))
                return cached;

            // Rebuild cache if dirty (first call or after invalidation)
            if (_loadedSpriteCacheDirty)
            {
                _loadedSpriteCache.Clear();
                var allSprites = Resources.FindObjectsOfTypeAll<Sprite>();
                for (int i = 0; i < allSprites.Length; i++)
                {
                    var sprite = allSprites[i];
                    if (sprite != null && !string.IsNullOrEmpty(sprite.name))
                    {
                        // Cache by plain sprite name (first wins)
                        if (!_loadedSpriteCache.ContainsKey(sprite.name))
                            _loadedSpriteCache[sprite.name] = sprite;

                        // Also cache by qualified "texture:sprite" key for atlas sprites
                        if (sprite.texture != null && !string.IsNullOrEmpty(sprite.texture.name))
                        {
                            string qualifiedKey = sprite.texture.name + ":" + sprite.name;
                            if (!_loadedSpriteCache.ContainsKey(qualifiedKey))
                                _loadedSpriteCache[qualifiedKey] = sprite;
                        }
                    }
                }
                _loadedSpriteCacheDirty = false;
            }

            // Try exact match (handles both plain and qualified names)
            if (_loadedSpriteCache.TryGetValue(spriteName, out var found))
                return found;

            // If qualified name was given but not found, try just the sprite part
            int colonIdx = spriteName.IndexOf(':');
            if (colonIdx > 0 && colonIdx < spriteName.Length - 1)
            {
                string justSprite = spriteName.Substring(colonIdx + 1);
                if (_loadedSpriteCache.TryGetValue(justSprite, out var fallback))
                    return fallback;
            }

            return null;
        }

        /// <summary>
        /// Invalidates the loaded sprite cache. Call this if new sprites may have been loaded
        /// (e.g., after scene changes or asset bundle loads).
        /// </summary>
        public static void InvalidateSpriteCache()
        {
            _loadedSpriteCacheDirty = true;
        }

        /// <summary>
        /// Public accessor for searching loaded sprites by name.
        /// Used by UIBuilderSpriteHelper and other editor utilities.
        /// </summary>
        public static Sprite FindLoadedSpriteByName(string spriteName)
        {
            return FindLoadedSprite(spriteName);
        }

        /// <summary>
        /// Applies a layout's saved screen position to the root element's RectTransform.
        /// Call this after Instantiate() when displaying the layout to the player.
        /// The position is stored in normalized screen coordinates (0..1) and converted
        /// to the parent canvas's local coordinates.
        /// </summary>
        public static void ApplyScreenPosition(UILayoutDefinition layout, GameObject root, RectTransform canvasRect)
        {
            if (layout == null || root == null || canvasRect == null) return;

            var rect = root.GetComponent<RectTransform>();
            if (rect == null) return;

            // Apply saved scale (always - default is 1.0 so no-op when unset)
            float scale = Mathf.Clamp(layout.ScreenScale, 0.1f, 3f);
            rect.localScale = new Vector3(scale, scale, 1f);

            if (!layout.HasScreenPosition()) return;

            float canvasW = canvasRect.rect.width;
            float canvasH = canvasRect.rect.height;
            if (canvasW < 1f || canvasH < 1f) return;

            // Switch to center-anchored free positioning
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            // Convert normalized position to canvas local coordinates
            float localX = (layout.ScreenPositionX - 0.5f) * canvasW;
            float localY = (layout.ScreenPositionY - 0.5f) * canvasH;
            rect.anchoredPosition = new Vector2(localX, localY);
        }
    }
}
