using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace FiresCore.UI
{
    /// <summary>
    /// Valheim's crosshair "HoverName" text is a raycastTarget parked at screen-center; it sits on top of a
    /// custom ScreenSpaceOverlay UI in that band and eats clicks (center buttons/inputs/scroll go dead while
    /// edge controls work). A center-covering UI calls <see cref="Suppress"/> on open, <see cref="Reassert"/>
    /// each frame while open (the Hud re-enables it), and <see cref="Restore"/> on close. The caller owns the
    /// store list. See Tools/UI_CROSSHAIR_RAYCAST_BLOCKER.md.
    /// </summary>
    public static class CrosshairRaycast
    {
        public static void Suppress(List<Graphic> store)
        {
            if (store == null) return;
            store.Clear();
            var hud = Hud.instance;
            var cross = hud != null ? FindByName(hud.transform, "crosshair") : null;
            if (cross == null) return;
            foreach (var g in cross.GetComponentsInChildren<Graphic>(true))
                if (g != null && g.raycastTarget) { g.raycastTarget = false; store.Add(g); }
        }

        public static void Reassert(List<Graphic> store)
        {
            if (store == null) return;
            for (int i = 0; i < store.Count; i++)
                if (store[i] != null) store[i].raycastTarget = false;
        }

        public static void Restore(List<Graphic> store)
        {
            if (store == null) return;
            foreach (var g in store) if (g != null) g.raycastTarget = true;
            store.Clear();
        }

        private static Transform FindByName(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var r = FindByName(root.GetChild(i), name);
                if (r != null) return r;
            }
            return null;
        }
    }

    /// <summary>
  /// Centralized UI element builder helper for creating common Unity UI components.
    /// This reduces code duplication across screen controllers and ensures consistent styling.
    /// </summary>
    public static class UIBuilderHelper
    {
        #region Panel Creation

/// <summary>
        /// Creates a panel with a background image.
        /// </summary>
        /// <param name="parent">Parent transform</param>
  /// <param name="name">GameObject name</param>
        /// <param name="anchorMin">Anchor min (bottom-left)</param>
        /// <param name="anchorMax">Anchor max (top-right)</param>
        /// <param name="backgroundColor">Background color (uses default if null)</param>
        /// <returns>The created panel GameObject</returns>
        public static GameObject CreatePanel(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Color? backgroundColor = null)
        {
          var panelGO = new GameObject(name);
     panelGO.transform.SetParent(parent, false);

      var rect = panelGO.AddComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
          rect.offsetMin = Vector2.zero;
    rect.offsetMax = Vector2.zero;

        var bg = panelGO.AddComponent<Image>();
        bg.color = backgroundColor ?? UIFontConfig.Colors.PanelBackground;
        FiresRoundedSprite.Apply(bg);   // soft corners on every codegen panel

       return panelGO;
        }

     /// <summary>
        /// Creates a panel that fills its parent completely.
        /// </summary>
        public static GameObject CreateFullPanel(Transform parent, string name, Color? backgroundColor = null)
        {
            return CreatePanel(parent, name, Vector2.zero, Vector2.one, backgroundColor);
        }

        #endregion

        #region Button Creation

        /// <summary>
        /// Creates a styled button with text.
      /// </summary>
 /// <param name="parent">Parent transform</param>
        /// <param name="text">Button label text</param>
        /// <param name="anchorMin">Anchor min position</param>
    /// <param name="anchorMax">Anchor max position</param>
        /// <param name="onClick">Optional click handler</param>
        /// <returns>The created Button component</returns>
        public static Button CreateButton(Transform parent, string text, Vector2 anchorMin, Vector2 anchorMax, UnityEngine.Events.UnityAction onClick = null)
        {
            var buttonGO = new GameObject($"Btn_{text}");
            buttonGO.transform.SetParent(parent, false);

            var buttonRect = buttonGO.AddComponent<RectTransform>();
            buttonRect.anchorMin = anchorMin;
    buttonRect.anchorMax = anchorMax;
            buttonRect.offsetMin = Vector2.zero;
       buttonRect.offsetMax = Vector2.zero;

   var buttonImg = buttonGO.AddComponent<Image>();
         buttonImg.color = UIFontConfig.Colors.ButtonNormal;
         buttonImg.raycastTarget = true;
         FiresRoundedSprite.Apply(buttonImg);   // soft corners on every codegen button

         var button = buttonGO.AddComponent<Button>();
      button.targetGraphic = buttonImg;

   var colors = button.colors;
       colors.normalColor = UIFontConfig.Colors.ButtonNormal;
       colors.highlightedColor = UIFontConfig.Colors.ButtonHover;
  colors.pressedColor = UIFontConfig.Colors.ButtonPressed;
            colors.selectedColor = UIFontConfig.Colors.ButtonNormal;
        colors.disabledColor = UIFontConfig.Colors.ButtonDisabled;
   colors.fadeDuration = 0.1f;
   button.colors = colors;

  // Create text child
        var textGO = new GameObject("Text");
            textGO.transform.SetParent(buttonGO.transform, false);

       var textRect = textGO.AddComponent<RectTransform>();
   textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
 textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

     var buttonText = textGO.AddComponent<TextMeshProUGUI>();
            buttonText.text = text;
            buttonText.alignment = TextAlignmentOptions.Center;
            buttonText.fontStyle = FontStyles.Bold;
UIFontConfig.ApplyStyle(buttonText, UIFontConfig.ButtonText);

 if (onClick != null)
     button.onClick.AddListener(onClick);

   return button;
    }

        /// <summary>
        /// Creates a button with custom colors.
  /// </summary>
 public static Button CreateButton(Transform parent, string text, Vector2 anchorMin, Vector2 anchorMax,
         Color normalColor, Color hoverColor, Color pressedColor, UnityEngine.Events.UnityAction onClick = null)
        {
     var button = CreateButton(parent, text, anchorMin, anchorMax, onClick);

            var colors = button.colors;
            colors.normalColor = normalColor;
       colors.highlightedColor = hoverColor;
            colors.pressedColor = pressedColor;
  colors.selectedColor = normalColor;
            button.colors = colors;

            button.GetComponent<Image>().color = normalColor;

        return button;
        }

        /// <summary>
        /// Creates a button styled to match the baked parchment "book" UI (warm-brown face, cream ink,
        /// dark outline) instead of the dark default theme. Use this for any runtime widget that sits
        /// alongside the NPC book / dressing room. When the control lives over an open InventoryGui the
        /// onClick will be swallowed — pass onClick=null and drive it via a manual RectangleContainsScreenPoint
        /// hit-test; otherwise pass onClick normally.
        /// </summary>
        public static Button CreateParchmentButton(Transform parent, string text, Vector2 anchorMin, Vector2 anchorMax,
            UnityEngine.Events.UnityAction onClick = null)
        {
            var button = CreateButton(parent, text, anchorMin, anchorMax,
                UIFontConfig.Colors.ParchmentButton, UIFontConfig.Colors.ParchmentButtonHover,
                UIFontConfig.Colors.ParchmentButtonPressed, onClick);

            var outline = button.gameObject.AddComponent<Outline>();
            outline.effectColor = UIFontConfig.Colors.ParchmentEdge;
            outline.effectDistance = new Vector2(1.5f, -1.5f);

            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                label.color = UIFontConfig.Colors.ParchmentButtonInk;
                label.enableAutoSizing = true;
                label.fontSizeMin = 9f;
                label.fontSizeMax = 13f;
            }

            return button;
        }

        /// <summary>
        /// Creates a single-line TMP_InputField with the parchment field styling. Builds the
        /// Viewport/Text/Placeholder hierarchy TMP_InputField requires. Caller focuses it (ActivateInputField).
        /// </summary>
        public static TMP_InputField CreateInputField(Transform parent, string name, string placeholderText,
            Vector2 anchorMin, Vector2 anchorMax, int fontSize = 13)
        {
            var inputGO = new GameObject(name);
            inputGO.transform.SetParent(parent, false);
            var inputRect = inputGO.AddComponent<RectTransform>();
            inputRect.anchorMin = anchorMin;
            inputRect.anchorMax = anchorMax;
            inputRect.offsetMin = Vector2.zero;
            inputRect.offsetMax = Vector2.zero;
            var img = inputGO.AddComponent<Image>();
            img.color = UIFontConfig.Colors.ParchmentField;
            img.raycastTarget = true;
            FiresRoundedSprite.Apply(img);   // soft corners on every codegen input field

            var viewportGO = new GameObject("Viewport");
            viewportGO.transform.SetParent(inputGO.transform, false);
            var viewportRect = viewportGO.AddComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = new Vector2(8, 2);
            viewportRect.offsetMax = new Vector2(-8, -2);
            viewportGO.AddComponent<RectMask2D>();

            var textGO = new GameObject("Text");
            textGO.transform.SetParent(viewportGO.transform, false);
            var textRect = textGO.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            var text = textGO.AddComponent<TextMeshProUGUI>();
            text.fontSize = fontSize;
            text.color = UIFontConfig.Colors.ParchmentInk;
            text.alignment = TextAlignmentOptions.Left;
            text.raycastTarget = false;
            ApplyValheimFont(text);

            var placeholderGO = new GameObject("Placeholder");
            placeholderGO.transform.SetParent(viewportGO.transform, false);
            var placeholderRect = placeholderGO.AddComponent<RectTransform>();
            placeholderRect.anchorMin = Vector2.zero;
            placeholderRect.anchorMax = Vector2.one;
            placeholderRect.offsetMin = Vector2.zero;
            placeholderRect.offsetMax = Vector2.zero;
            var placeholder = placeholderGO.AddComponent<TextMeshProUGUI>();
            placeholder.text = placeholderText ?? "";
            placeholder.fontSize = fontSize;
            placeholder.color = new Color(UIFontConfig.Colors.ParchmentLabel.r, UIFontConfig.Colors.ParchmentLabel.g, UIFontConfig.Colors.ParchmentLabel.b, 0.6f);
            placeholder.alignment = TextAlignmentOptions.Left;
            placeholder.fontStyle = FontStyles.Italic;
            placeholder.raycastTarget = false;
            ApplyValheimFont(placeholder);

            var input = inputGO.AddComponent<TMP_InputField>();
            input.textViewport = viewportRect;
            input.textComponent = text;
            input.placeholder = placeholder;
            input.fontAsset = text.font;
            input.pointSize = fontSize;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.contentType = TMP_InputField.ContentType.Standard;
            input.caretColor = UIFontConfig.Colors.ParchmentInk;
            input.selectionColor = UIFontConfig.Colors.ParchmentItemSelected;
            input.interactable = true;

            return input;
        }

        #endregion

        #region Scrollable Area Creation

        /// <summary>
        /// Result object containing references to created scroll components.
  /// </summary>
        public class ScrollAreaResult
      {
   public GameObject Root { get; set; }
         public ScrollRect ScrollRect { get; set; }
     public RectTransform Viewport { get; set; }
     public RectTransform Content { get; set; }
            public Scrollbar Scrollbar { get; set; }
        }

        /// <summary>
        /// Creates a scrollable area with viewport, content, and scrollbar.
     /// </summary>
        /// <param name="parent">Parent transform</param>
     /// <param name="name">Base name for the scroll area</param>
        /// <param name="anchorMin">Anchor min position</param>
        /// <param name="anchorMax">Anchor max position</param>
        /// <param name="backgroundColor">Background color (optional)</param>
     /// <param name="scrollbarWidth">Width of the scrollbar (default 12)</param>
        /// <returns>ScrollAreaResult with references to all created components</returns>
        public static ScrollAreaResult CreateScrollArea(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax,
    Color? backgroundColor = null, float scrollbarWidth = 12f)
        {
            var result = new ScrollAreaResult();

            // Root container
 var scrollGO = new GameObject(name);
    scrollGO.transform.SetParent(parent, false);
      result.Root = scrollGO;

     var scrollRectTransform = scrollGO.AddComponent<RectTransform>();
        scrollRectTransform.anchorMin = anchorMin;
            scrollRectTransform.anchorMax = anchorMax;
            scrollRectTransform.offsetMin = Vector2.zero;
            scrollRectTransform.offsetMax = Vector2.zero;

 if (backgroundColor.HasValue)
    {
        var scrollBg = scrollGO.AddComponent<Image>();
     scrollBg.color = backgroundColor.Value;
            }

            // Viewport with mask
            var viewportGO = new GameObject("Viewport");
viewportGO.transform.SetParent(scrollGO.transform, false);

      var viewportRect = viewportGO.AddComponent<RectTransform>();
       viewportRect.anchorMin = Vector2.zero;
        viewportRect.anchorMax = Vector2.one;
      viewportRect.offsetMin = new Vector2(5, 5);
          viewportRect.offsetMax = new Vector2(-scrollbarWidth - 6, -5);
      viewportGO.AddComponent<RectMask2D>();
     viewportGO.AddComponent<Image>().color = Color.clear; // For raycasting
            result.Viewport = viewportRect;

      // Content container
  var contentGO = new GameObject("Content");
            contentGO.transform.SetParent(viewportGO.transform, false);

    var contentRect = contentGO.AddComponent<RectTransform>();
  contentRect.anchorMin = new Vector2(0, 1);
 contentRect.anchorMax = new Vector2(1, 1);
       contentRect.pivot = new Vector2(0, 1);
            contentRect.anchoredPosition = Vector2.zero;
        contentRect.sizeDelta = Vector2.zero;
     result.Content = contentRect;

       // ScrollRect component
 var scrollRect = scrollGO.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
  scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 120f;  // Higher value = faster scrolling
 scrollRect.inertia = false;
            scrollRect.viewport = viewportRect;
            scrollRect.content = contentRect;
    result.ScrollRect = scrollRect;

   // Scrollbar
            result.Scrollbar = CreateVerticalScrollbar(scrollGO.transform, scrollbarWidth);
scrollRect.verticalScrollbar = result.Scrollbar;
     scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;

            return result;
        }

        /// <summary>
/// Creates a vertical scrollbar positioned on the right side.
        /// </summary>
  public static Scrollbar CreateVerticalScrollbar(Transform parent, float width = 12f)
        {
            var scrollbarGO = new GameObject("Scrollbar");
            scrollbarGO.transform.SetParent(parent, false);

            var scrollbarRect = scrollbarGO.AddComponent<RectTransform>();
            scrollbarRect.anchorMin = new Vector2(1, 0);
        scrollbarRect.anchorMax = new Vector2(1, 1);
        scrollbarRect.pivot = new Vector2(1, 0.5f);
       scrollbarRect.anchoredPosition = new Vector2(-3, 0);
 scrollbarRect.sizeDelta = new Vector2(width, -10);

var scrollbarBg = scrollbarGO.AddComponent<Image>();
       scrollbarBg.color = UIFontConfig.Colors.ScrollbarBackground;

            var scrollbar = scrollbarGO.AddComponent<Scrollbar>();
        scrollbar.direction = Scrollbar.Direction.BottomToTop;

            // Sliding area
            var slidingAreaGO = new GameObject("SlidingArea");
         slidingAreaGO.transform.SetParent(scrollbarGO.transform, false);

            var slidingAreaRect = slidingAreaGO.AddComponent<RectTransform>();
       slidingAreaRect.anchorMin = Vector2.zero;
    slidingAreaRect.anchorMax = Vector2.one;
            slidingAreaRect.offsetMin = new Vector2(2, 2);
     slidingAreaRect.offsetMax = new Vector2(-2, -2);

     // Handle
  var handleGO = new GameObject("Handle");
            handleGO.transform.SetParent(slidingAreaGO.transform, false);

        var handleRect = handleGO.AddComponent<RectTransform>();
            handleRect.anchorMin = Vector2.zero;
handleRect.anchorMax = Vector2.one;
      handleRect.offsetMin = Vector2.zero;
    handleRect.offsetMax = Vector2.zero;

       var handleImg = handleGO.AddComponent<Image>();
            handleImg.color = UIFontConfig.Colors.ScrollbarHandle;
    handleImg.raycastTarget = true;

   scrollbar.handleRect = handleRect;
        scrollbar.targetGraphic = handleImg;

            // Button colors for visual feedback
            var scrollbarColors = scrollbar.colors;
            scrollbarColors.normalColor = UIFontConfig.Colors.ScrollbarHandle;
      scrollbarColors.highlightedColor = UIFontConfig.Colors.ScrollbarHandleHover;
     scrollbarColors.pressedColor = UIFontConfig.Colors.ScrollbarHandlePressed;
    scrollbarColors.selectedColor = UIFontConfig.Colors.ScrollbarHandle;
scrollbar.colors = scrollbarColors;

  return scrollbar;
        }

        #endregion

        #region Slider Creation

        /// <summary>
        /// Creates a horizontal uGUI Slider with the standard Background / Fill / Handle hierarchy,
        /// themed with the shared scrollbar/button colors. Returns the Slider so callers can read
        /// <c>value</c> or further tweak it. Pass <paramref name="onValueChanged"/> to react to drags.
        /// </summary>
        public static Slider CreateSlider(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax,
            float minValue, float maxValue, float value, UnityEngine.Events.UnityAction<float> onValueChanged = null)
        {
            var root = new GameObject(name, typeof(RectTransform));
            root.transform.SetParent(parent, false);
            var rootRect = root.GetComponent<RectTransform>();
            rootRect.anchorMin = anchorMin;
            rootRect.anchorMax = anchorMax;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;

            // Background track
            var bg = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bg.transform.SetParent(root.transform, false);
            var bgRect = bg.GetComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0f, 0.3f);
            bgRect.anchorMax = new Vector2(1f, 0.7f);
            bgRect.offsetMin = Vector2.zero;
            bgRect.offsetMax = Vector2.zero;
            bg.GetComponent<Image>().color = UIFontConfig.Colors.ScrollbarBackground;

            // Fill Area > Fill
            var fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(root.transform, false);
            var fillAreaRect = fillArea.GetComponent<RectTransform>();
            fillAreaRect.anchorMin = new Vector2(0f, 0.3f);
            fillAreaRect.anchorMax = new Vector2(1f, 0.7f);
            fillAreaRect.offsetMin = new Vector2(8f, 0f);
            fillAreaRect.offsetMax = new Vector2(-8f, 0f);
            var fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(fillArea.transform, false);
            var fillRect = fill.GetComponent<RectTransform>();
            fillRect.anchorMin = new Vector2(0f, 0f);
            fillRect.anchorMax = new Vector2(0f, 1f);
            fillRect.sizeDelta = new Vector2(10f, 0f);
            fill.GetComponent<Image>().color = UIFontConfig.Colors.ScrollbarHandle;

            // Handle Slide Area > Handle
            var handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(root.transform, false);
            var handleAreaRect = handleArea.GetComponent<RectTransform>();
            handleAreaRect.anchorMin = new Vector2(0f, 0f);
            handleAreaRect.anchorMax = new Vector2(1f, 1f);
            handleAreaRect.offsetMin = new Vector2(8f, 0f);
            handleAreaRect.offsetMax = new Vector2(-8f, 0f);
            var handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handle.transform.SetParent(handleArea.transform, false);
            var handleRect = handle.GetComponent<RectTransform>();
            handleRect.sizeDelta = new Vector2(16f, 0f);
            handle.GetComponent<Image>().color = UIFontConfig.Colors.ButtonNormal;

            var slider = root.AddComponent<Slider>();
            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.targetGraphic = handle.GetComponent<Image>();
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = minValue;
            slider.maxValue = maxValue;
            slider.wholeNumbers = false;
            slider.value = value;
            if (onValueChanged != null) slider.onValueChanged.AddListener(onValueChanged);
            return slider;
        }

        #endregion

    #region Text Creation

        /// <summary>
        /// Creates a TextMeshProUGUI component with the specified style.
        /// </summary>
    /// <param name="parent">Parent transform</param>
        /// <param name="name">GameObject name</param>
        /// <param name="text">Initial text content</param>
        /// <param name="style">UITextStyle to apply</param>
    /// <param name="anchorMin">Anchor min position</param>
     /// <param name="anchorMax">Anchor max position</param>
        /// <param name="alignment">Text alignment (default center)</param>
        /// <returns>The created TextMeshProUGUI component</returns>
   public static TextMeshProUGUI CreateText(Transform parent, string name, string text, UITextStyle style,
 Vector2 anchorMin, Vector2 anchorMax, TextAlignmentOptions alignment = TextAlignmentOptions.Center)
        {
            var textGO = new GameObject(name);
            textGO.transform.SetParent(parent, false);

            var textRect = textGO.AddComponent<RectTransform>();
      textRect.anchorMin = anchorMin;
      textRect.anchorMax = anchorMax;
   textRect.offsetMin = Vector2.zero;
       textRect.offsetMax = Vector2.zero;

        var tmp = textGO.AddComponent<TextMeshProUGUI>();
    tmp.text = text;
     tmp.alignment = alignment;
     UIFontConfig.ApplyStyle(tmp, style);

            return tmp;
   }

    /// <summary>
        /// Creates a text element that fills its parent.
        /// </summary>
     public static TextMeshProUGUI CreateFullText(Transform parent, string name, string text, UITextStyle style,
          TextAlignmentOptions alignment = TextAlignmentOptions.Center)
        {
 return CreateText(parent, name, text, style, Vector2.zero, Vector2.one, alignment);
      }

        /// <summary>
        /// Creates a simple text label with default styling.
        /// Uses the Primary font category by default.
        /// </summary>
        public static TextMeshProUGUI CreateLabel(Transform parent, string name, string text, float fontSize, Color color,
            Vector2 anchorMin, Vector2 anchorMax, TextAlignmentOptions alignment = TextAlignmentOptions.Center)
        {
            var textGO = new GameObject(name);
            textGO.transform.SetParent(parent, false);

            var textRect = textGO.AddComponent<RectTransform>();
            textRect.anchorMin = anchorMin;
            textRect.anchorMax = anchorMax;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            var tmp = textGO.AddComponent<TextMeshProUGUI>();
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.color = color;
            tmp.alignment = alignment;

            // Apply Primary font by default
            ApplyPrimaryFont(tmp);

            return tmp;
        }
        
        /// <summary>
        /// Creates a simple text label with a specific font category.
        /// </summary>
        public static TextMeshProUGUI CreateLabel(Transform parent, string name, string text, float fontSize, Color color,
            Vector2 anchorMin, Vector2 anchorMax, TextAlignmentOptions alignment, UIFontConfig.FontCategory fontCategory)
        {
            var tmp = CreateLabel(parent, name, text, fontSize, color, anchorMin, anchorMax, alignment);
            ApplyFontByCategory(tmp, fontCategory);
            return tmp;
        }

      #endregion

      #region Image/RawImage Creation

     /// <summary>
   /// Creates a RawImage for displaying textures (like quest preview images).
        /// </summary>
        /// <param name="parent">Parent transform</param>
      /// <param name="name">GameObject name</param>
        /// <param name="anchorMin">Anchor min position</param>
        /// <param name="anchorMax">Anchor max position</param>
      /// <returns>The created RawImage component</returns>
        public static RawImage CreateRawImage(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax)
  {
            var imageGO = new GameObject(name);
   imageGO.transform.SetParent(parent, false);

        var imageRect = imageGO.AddComponent<RectTransform>();
   imageRect.anchorMin = anchorMin;
imageRect.anchorMax = anchorMax;
            imageRect.offsetMin = Vector2.zero;
         imageRect.offsetMax = Vector2.zero;

    var rawImage = imageGO.AddComponent<RawImage>();
            rawImage.color = Color.white;
     rawImage.enabled = false;

            return rawImage;
        }

        /// <summary>
        /// Creates an Image component with specified color.
        /// </summary>
        public static Image CreateImage(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Color color)
  {
            var imageGO = new GameObject(name);
      imageGO.transform.SetParent(parent, false);

         var imageRect = imageGO.AddComponent<RectTransform>();
            imageRect.anchorMin = anchorMin;
            imageRect.anchorMax = anchorMax;
            imageRect.offsetMin = Vector2.zero;
            imageRect.offsetMax = Vector2.zero;

         var image = imageGO.AddComponent<Image>();
     image.color = color;

            return image;
        }

        #endregion

        #region List Item Creation

        /// <summary>
        /// Result object containing references to created list item components.
        /// </summary>
        public class ListItemResult
        {
public GameObject Root { get; set; }
       public Button Button { get; set; }
          public Image Background { get; set; }
   public TextMeshProUGUI Text { get; set; }
     public TextMeshProUGUI StatusText { get; set; }
        }

     /// <summary>
/// Creates a selectable list item (e.g., for quest lists).
      /// </summary>
    /// <param name="parent">Parent transform (usually content of a scroll area)</param>
   /// <param name="name">GameObject name</param>
/// <param name="text">Display text</param>
        /// <param name="yPosition">Y position (typically negative, measured from top)</param>
     /// <param name="height">Item height</param>
      /// <param name="onClick">Click handler</param>
      /// <returns>ListItemResult with references to all created components</returns>
  public static ListItemResult CreateListItem(Transform parent, string name, string text, float yPosition, float height,
  UnityEngine.Events.UnityAction onClick = null)
   {
    var result = new ListItemResult();

    var go = new GameObject(name);
  go.transform.SetParent(parent, false);
            result.Root = go;

    var rect = go.AddComponent<RectTransform>();
  rect.anchorMin = new Vector2(0, 1);
 rect.anchorMax = new Vector2(1, 1);
      rect.pivot = new Vector2(0, 1);
  rect.anchoredPosition = new Vector2(2, yPosition);
 rect.sizeDelta = new Vector2(-4, height);

      // Background image
      var bg = go.AddComponent<Image>();
      bg.color = UIFontConfig.Colors.ItemNormal;
bg.raycastTarget = true;
            result.Background = bg;

    // Button component
  var button = go.AddComponent<Button>();
 button.targetGraphic = bg;

   var colors = button.colors;
colors.normalColor = UIFontConfig.Colors.ItemNormal;
   colors.highlightedColor = UIFontConfig.Colors.ItemHover;
   colors.pressedColor = UIFontConfig.Colors.ItemSelected;
    colors.selectedColor = UIFontConfig.Colors.ItemSelected;
      colors.colorMultiplier = 1f;
     colors.fadeDuration = 0.1f;
     button.colors = colors;
 button.transition = Selectable.Transition.ColorTint;
 result.Button = button;

  if (onClick != null)
    button.onClick.AddListener(onClick);

       // Text - fills full width, allows overlap with status below
   var textGO = new GameObject("Text");
            textGO.transform.SetParent(go.transform, false);

     var textRect = textGO.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
       textRect.offsetMin = new Vector2(8, 2);
            textRect.offsetMax = new Vector2(-8, -2);

   var tmp = textGO.AddComponent<TextMeshProUGUI>();
   tmp.text = text;
tmp.color = UIFontConfig.Colors.Gold;
 tmp.alignment = TextAlignmentOptions.MidlineLeft;
    tmp.textWrappingMode = TextWrappingModes.NoWrap;
    tmp.overflowMode = TextOverflowModes.Ellipsis;
   tmp.raycastTarget = false;
       UIFontConfig.ApplyStyle(tmp, UIFontConfig.QuestListItem);
  result.Text = tmp;

      // Status text (bottom right, overlaps with name area)
    var statusGO = new GameObject("Status");
    statusGO.transform.SetParent(go.transform, false);

    var statusRect = statusGO.AddComponent<RectTransform>();
          statusRect.anchorMin = new Vector2(1, 0);
   statusRect.anchorMax = new Vector2(1, 0.5f);
  statusRect.pivot = new Vector2(1, 0);
statusRect.anchoredPosition = new Vector2(-3, 2);
   statusRect.sizeDelta = new Vector2(70, 0);

  var statusText = statusGO.AddComponent<TextMeshProUGUI>();
 statusText.alignment = TextAlignmentOptions.BottomRight;
     statusText.raycastTarget = false;
   UIFontConfig.ApplyStyle(statusText, UIFontConfig.QuestStatus);
result.StatusText = statusText;

      return result;
   }

        #endregion

        #region Layout Helpers

        /// <summary>
        /// Adds a VerticalLayoutGroup to a GameObject with common settings.
        /// </summary>
 public static VerticalLayoutGroup AddVerticalLayout(GameObject go, RectOffset padding = null,
        float spacing = 0, bool childControlWidth = true, bool childControlHeight = true,
            bool childForceExpandWidth = true, bool childForceExpandHeight = false)
    {
            var layout = go.AddComponent<VerticalLayoutGroup>();
  layout.padding = padding ?? new RectOffset(0, 0, 0, 0);
         layout.spacing = spacing;
        layout.childControlWidth = childControlWidth;
   layout.childControlHeight = childControlHeight;
   layout.childForceExpandWidth = childForceExpandWidth;
    layout.childForceExpandHeight = childForceExpandHeight;
            return layout;
     }

        /// <summary>
        /// Adds a HorizontalLayoutGroup to a GameObject with common settings.
        /// </summary>
        public static HorizontalLayoutGroup AddHorizontalLayout(GameObject go, RectOffset padding = null,
     float spacing = 0, bool childControlWidth = true, bool childControlHeight = true,
            bool childForceExpandWidth = true, bool childForceExpandHeight = false)
        {
            var layout = go.AddComponent<HorizontalLayoutGroup>();
       layout.padding = padding ?? new RectOffset(0, 0, 0, 0);
      layout.spacing = spacing;
            layout.childControlWidth = childControlWidth;
       layout.childControlHeight = childControlHeight;
   layout.childForceExpandWidth = childForceExpandWidth;
      layout.childForceExpandHeight = childForceExpandHeight;
        return layout;
    }

      /// <summary>
        /// Adds a ContentSizeFitter to a GameObject.
        /// </summary>
     public static ContentSizeFitter AddContentSizeFitter(GameObject go,
  ContentSizeFitter.FitMode horizontalFit = ContentSizeFitter.FitMode.Unconstrained,
       ContentSizeFitter.FitMode verticalFit = ContentSizeFitter.FitMode.PreferredSize)
     {
          var fitter = go.AddComponent<ContentSizeFitter>();
    fitter.horizontalFit = horizontalFit;
            fitter.verticalFit = verticalFit;
         return fitter;
        }

        /// <summary>
        /// Adds a LayoutElement to a GameObject.
        /// </summary>
        public static LayoutElement AddLayoutElement(GameObject go, float minWidth = -1, float minHeight = -1,
   float preferredWidth = -1, float preferredHeight = -1, float flexibleWidth = -1, float flexibleHeight = -1)
        {
            var element = go.AddComponent<LayoutElement>();
            if (minWidth >= 0) element.minWidth = minWidth;
            if (minHeight >= 0) element.minHeight = minHeight;
     if (preferredWidth >= 0) element.preferredWidth = preferredWidth;
          if (preferredHeight >= 0) element.preferredHeight = preferredHeight;
            if (flexibleWidth >= 0) element.flexibleWidth = flexibleWidth;
            if (flexibleHeight >= 0) element.flexibleHeight = flexibleHeight;
        return element;
        }

        #endregion

        #region Font Helpers
        
        // Cached default font to reduce lookup overhead
        private static TMP_FontAsset _cachedDefaultFont;
        private static bool _fontCacheInitialized;

        /// <summary>
        /// Adds a TextMeshProUGUI component with a font already set to prevent TMP warnings.
        /// ALWAYS use this instead of AddComponent&lt;TextMeshProUGUI&gt;() to avoid font warnings.
        /// Uses the Primary font category by default.
        /// </summary>
        public static TextMeshProUGUI AddTextMeshProUGUI(GameObject go)
        {
            if (go == null) return null;
            
            // Ensure we have a cached font before adding the component
            EnsureFontCache();
            
            var tmp = go.AddComponent<TextMeshProUGUI>();
            
            // Apply Primary font by default to prevent "Font Asset was not found" warnings
            ApplyPrimaryFont(tmp);
            
            return tmp;
        }
        
        /// <summary>
        /// Adds a TextMeshProUGUI component with a font from the specified category.
        /// Use this when you know the text should use a specific font category.
        /// </summary>
        public static TextMeshProUGUI AddTextMeshProUGUI(GameObject go, UIFontConfig.FontCategory category)
        {
            if (go == null) return null;
            
            var tmp = go.AddComponent<TextMeshProUGUI>();
            ApplyFontByCategory(tmp, category);
            
            return tmp;
        }
        
        /// <summary>
        /// Ensures we have a cached default font for TMP components.
        /// Also sets TMP_Settings.defaultFontAsset to prevent "Font Asset was not found" warnings
        /// when AddComponent&lt;TextMeshProUGUI&gt;() is called anywhere.
        /// </summary>
        private static void EnsureFontCache()
        {
            if (_fontCacheInitialized && _cachedDefaultFont != null) return;
            
            _fontCacheInitialized = true;
            
            // Try Valheim fonts first
            _cachedDefaultFont = UIFontConfig.GetFont(UIFontConfig.FontStyle.Valheim);
            if (_cachedDefaultFont != null) { SetTMPDefault(_cachedDefaultFont); return; }
            
            // Try known Valheim font names
            var fallbackNames = new[] { "Valheim-AveriaSerifLibre", "Valheim-AveriaSansLibre", "AveriaSansLibre-Bold SDF" };
            foreach (var name in fallbackNames)
            {
                _cachedDefaultFont = GetFontByNameInternal(name);
                if (_cachedDefaultFont != null) { SetTMPDefault(_cachedDefaultFont); return; }
            }
            
            // Last resort: any TMP font
            var allFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            foreach (var f in allFonts)
            {
                if (f != null && !string.IsNullOrEmpty(f.name))
                {
                    _cachedDefaultFont = f;
                    SetTMPDefault(_cachedDefaultFont);
                    return;
                }
            }
        }
        
        /// <summary>
        /// Sets the TMP default font asset so new TextMeshProUGUI components
        /// don't log "LiberationSans SDF Font Asset was not found" warnings.
        /// </summary>
        private static void SetTMPDefault(TMP_FontAsset font)
        {
            if (font == null) return;
            try
            {
                var settings = TMP_Settings.instance;
                if (settings != null && TMP_Settings.defaultFontAsset == null)
                {
                    TMP_Settings.defaultFontAsset = font;
                }
            }
            catch { }
        }
        
        /// <summary>
        /// Clears the font cache (call if fonts are reloaded).
        /// </summary>
        public static void ClearFontCache()
        {
            _cachedDefaultFont = null;
            _fontCacheInitialized = false;
        }

        /// <summary>
        /// Initializes the font cache and sets the TMP default font early.
        /// Call this during mod initialization to prevent font warnings
        /// from any TextMeshProUGUI created before ApplyBodyFont/ApplyPrimaryFont is called.
        /// </summary>
        public static void InitializeFontCache()
        {
            EnsureFontCache();
        }

        /// <summary>
        /// Applies the Valheim font to a TMP_Text component.
        /// Uses the configured Primary font from BepInEx config.
        /// If the font isn't available yet, tries fallback fonts to prevent TMP warnings.
        /// </summary>
        public static void ApplyValheimFont(TMP_Text text)
        {
            if (text == null) return;
            
            // Get the configured primary font (defaults to Valheim if not set)
            var effectiveFont = UIFontConfig.GetConfiguredFont(UIFontConfig.FontCategory.Primary);
            var font = UIFontConfig.GetFont(effectiveFont);
            
            if (font != null)
            {
                text.font = font;
                return;
            }
            
            // If no configured font, try to get any available font to prevent TMP warnings
            if (text.font == null)
            {
                EnsureFontCache();
                if (_cachedDefaultFont != null)
                {
                    text.font = _cachedDefaultFont;
                    return;
                }
            }
        }
        
        /// <summary>
        /// Applies a font from a specific category to a TMP_Text component.
        /// Uses the configured font for that category from BepInEx config.
        /// </summary>
        /// <param name="text">The TMP_Text component to apply the font to</param>
        /// <param name="category">The font category (Primary, Decorative, or Body)</param>
        public static void ApplyFontByCategory(TMP_Text text, UIFontConfig.FontCategory category)
        {
            if (text == null) return;
            
            var effectiveFont = UIFontConfig.GetConfiguredFont(category);
            var font = UIFontConfig.GetFont(effectiveFont);
            
            if (font != null)
            {
                text.font = font;
                return;
            }
            
            // Fallback to any available font
            if (text.font == null)
            {
                EnsureFontCache();
                if (_cachedDefaultFont != null)
                {
                    text.font = _cachedDefaultFont;
                }
            }
        }
        
        /// <summary>
        /// Applies the Primary font (headers, titles, main UI text) to a TMP_Text component.
        /// Alias for ApplyValheimFont() for clarity.
        /// </summary>
        public static void ApplyPrimaryFont(TMP_Text text)
        {
            ApplyFontByCategory(text, UIFontConfig.FontCategory.Primary);
        }
        
        /// <summary>
        /// Applies the Decorative font (quest titles, Viking-themed headers) to a TMP_Text component.
        /// </summary>
        public static void ApplyDecorativeFont(TMP_Text text)
        {
            ApplyFontByCategory(text, UIFontConfig.FontCategory.Decorative);
        }
        
        /// <summary>
        /// Applies the Body font (descriptions, buttons, readable text) to a TMP_Text component.
        /// </summary>
        public static void ApplyBodyFont(TMP_Text text)
        {
            ApplyFontByCategory(text, UIFontConfig.FontCategory.Body);
        }
        
        /// <summary>
        /// Internal helper to find font by name without logging.
        /// </summary>
        private static TMP_FontAsset GetFontByNameInternal(string fontName)
        {
            if (string.IsNullOrEmpty(fontName)) return null;
            try
            {
                var allFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                foreach (var font in allFonts)
                {
                    if (font != null && font.name != null &&
                        font.name.IndexOf(fontName, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return font;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Applies the Valheim font to all TMP_Text components in a hierarchy.
        /// Uses the Primary font category for all text elements.
        /// For more control, use ApplyFontByCategory on individual elements.
        /// </summary>
        public static void ApplyValheimFontToAll(GameObject root)
        {
            if (root == null) return;
            var font = UIFontConfig.GetFontForCategory(UIFontConfig.FontCategory.Primary);
            if (font == null) return;

            foreach (var tmp in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (tmp.font == null || tmp.font.name.Contains("Liberation"))
                    tmp.font = font;
            }
        }
        
        /// <summary>
        /// Applies fonts from the specified category to all TMP_Text components in a hierarchy.
        /// </summary>
        public static void ApplyFontToAll(GameObject root, UIFontConfig.FontCategory category)
        {
            if (root == null) return;
            var font = UIFontConfig.GetFontForCategory(category);
            if (font == null) return;

            foreach (var tmp in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (tmp.font == null || tmp.font.name.Contains("Liberation"))
                    tmp.font = font;
            }
        }

      #endregion

     #region Transform Helpers

        /// <summary>
        /// Finds a child transform recursively by name.
        /// </summary>
        public static Transform FindChildRecursive(Transform parent, string name)
        {
   if (parent == null) return null;

 var direct = parent.Find(name);
  if (direct != null) return direct;

  for (int i = 0; i < parent.childCount; i++)
   {
       var found = FindChildRecursive(parent.GetChild(i), name);
     if (found != null) return found;
            }

   return null;
    }

  #endregion

     #region Section Background Helpers

     /// <summary>
        /// Adds a semi-transparent background behind an existing element for improved readability.
    /// The background is inserted as a sibling behind the target element.
        /// </summary>
        /// <param name="targetElement">The element to add a background behind</param>
        /// <param name="backgroundColor">Background color (uses PanelBackground if null)</param>
        /// <param name="padding">Padding around the element (default 5 on all sides)</param>
/// <returns>The created background Image component</returns>
        public static Image AddSectionBackground(Transform targetElement, Color? backgroundColor = null, RectOffset padding = null)
 {
     if (targetElement == null) return null;

            padding = padding ?? new RectOffset(5, 5, 5, 5);
            var bgColor = backgroundColor ?? UIFontConfig.Colors.SectionBackground;

  // Create background as a sibling, positioned behind the target
    var bgGO = new GameObject($"{targetElement.name}_Background");
        bgGO.transform.SetParent(targetElement.parent, false);

            // Position it right before the target element in hierarchy
            int targetIndex = targetElement.GetSiblingIndex();
          bgGO.transform.SetSiblingIndex(targetIndex);

      var bgRect = bgGO.AddComponent<RectTransform>();
      var targetRect = targetElement.GetComponent<RectTransform>();

        if (targetRect != null)
  {
                // Copy anchors and position from target
         bgRect.anchorMin = targetRect.anchorMin;
  bgRect.anchorMax = targetRect.anchorMax;
         bgRect.pivot = targetRect.pivot;
     bgRect.anchoredPosition = targetRect.anchoredPosition;
    bgRect.sizeDelta = targetRect.sizeDelta;

// Expand by padding
       bgRect.offsetMin = targetRect.offsetMin - new Vector2(padding.left, padding.bottom);
                bgRect.offsetMax = targetRect.offsetMax + new Vector2(padding.right, padding.top);
            }

  var bgImage = bgGO.AddComponent<Image>();
      bgImage.color = bgColor;
            bgImage.raycastTarget = false;

      return bgImage;
        }

 /// <summary>
        /// Adds a semi-transparent background as a child that fills the parent element.
        /// Useful for adding backgrounds to layout groups where you want the bg inside the element.
        /// </summary>
        /// <param name="parentElement">The element to add a background inside</param>
 /// <param name="backgroundColor">Background color (uses SectionBackground if null)</param>
        /// <param name="name">Name for the background GameObject</param>
  /// <returns>The created background Image component</returns>
     public static Image AddInnerBackground(Transform parentElement, Color? backgroundColor = null, string name = "SectionBackground")
    {
            if (parentElement == null) return null;

      var bgColor = backgroundColor ?? UIFontConfig.Colors.SectionBackground;

       var bgGO = new GameObject(name);
bgGO.transform.SetParent(parentElement, false);
  bgGO.transform.SetAsFirstSibling(); // Put behind other children

     var bgRect = bgGO.AddComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
      bgRect.offsetMin = Vector2.zero;
          bgRect.offsetMax = Vector2.zero;

         var bgImage = bgGO.AddComponent<Image>();
         bgImage.color = bgColor;
   bgImage.raycastTarget = false;

    return bgImage;
        }

        /// <summary>
        /// Wraps an existing element with a panel that has a semi-transparent background.
   /// Returns the wrapper panel so you can add more children to it.
     /// </summary>
        /// <param name="elementToWrap">The element to wrap</param>
    /// <param name="backgroundColor">Background color</param>
        /// <param name="padding">Padding inside the wrapper</param>
        /// <returns>The wrapper panel GameObject</returns>
  public static GameObject WrapWithBackground(Transform elementToWrap, Color? backgroundColor = null, RectOffset padding = null)
        {
  if (elementToWrap == null) return null;

      padding = padding ?? new RectOffset(8, 8, 8, 8);
         var bgColor = backgroundColor ?? UIFontConfig.Colors.SectionBackground;

    var parent = elementToWrap.parent;
    int siblingIndex = elementToWrap.GetSiblingIndex();

 // Create wrapper
    var wrapperGO = new GameObject($"{elementToWrap.name}_Wrapper");
            wrapperGO.transform.SetParent(parent, false);
            wrapperGO.transform.SetSiblingIndex(siblingIndex);

  var wrapperRect = wrapperGO.AddComponent<RectTransform>();
   var originalRect = elementToWrap.GetComponent<RectTransform>();

        if (originalRect != null)
       {
        // Copy positioning from original
    wrapperRect.anchorMin = originalRect.anchorMin;
         wrapperRect.anchorMax = originalRect.anchorMax;
         wrapperRect.pivot = originalRect.pivot;
        wrapperRect.anchoredPosition = originalRect.anchoredPosition;
                wrapperRect.sizeDelta = originalRect.sizeDelta;
   wrapperRect.offsetMin = originalRect.offsetMin;
        wrapperRect.offsetMax = originalRect.offsetMax;
            }

         // Add background
            var bgImage = wrapperGO.AddComponent<Image>();
          bgImage.color = bgColor;
   bgImage.raycastTarget = false;

          // Reparent the original element
elementToWrap.SetParent(wrapperGO.transform, false);

  // Reset the original to fill the wrapper with padding
     if (originalRect != null)
          {
        originalRect.anchorMin = Vector2.zero;
      originalRect.anchorMax = Vector2.one;
    originalRect.offsetMin = new Vector2(padding.left, padding.bottom);
             originalRect.offsetMax = new Vector2(-padding.right, -padding.top);
        }

      return wrapperGO;
        }

 #endregion
    }
}
