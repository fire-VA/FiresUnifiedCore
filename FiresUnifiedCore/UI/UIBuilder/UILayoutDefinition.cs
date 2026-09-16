using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace FiresCore.UI
{
    //  Enums

    public enum UIElementType
    {
        Panel,
        Image,
        Text,
        Button,
        InputField,
        ScrollView,
        Dropdown,
        Toggle,
        Slider,
        Divider,
        Spacer,
        RenderCamera,
        Custom
    }

    public enum UIShapeType
    {
        Rectangle,
        Circle,
        RoundedRect
    }

    //  Serialization helper structs

    [Serializable]
    public struct Vector2Ser
    {
        public float X;
        public float Y;

        public Vector2Ser(float x, float y) { X = x; Y = y; }

        public Vector2 ToVector2() { return new Vector2(X, Y); }

        public static Vector2Ser From(Vector2 v) { return new Vector2Ser(v.x, v.y); }

        public override string ToString() { return $"({X:F2},{Y:F2})"; }
    }

    [Serializable]
    public struct ColorSer
    {
        public float R;
        public float G;
        public float B;
        public float A;

        public ColorSer(float r, float g, float b, float a)
        {
            R = r; G = g; B = b; A = a;
        }

        public Color ToColor() { return new Color(R, G, B, A); }

        public static ColorSer From(Color c) { return new ColorSer(c.r, c.g, c.b, c.a); }

        public static ColorSer White { get { return new ColorSer(1, 1, 1, 1); } }
        public static ColorSer Black { get { return new ColorSer(0, 0, 0, 1); } }
        public static ColorSer Clear { get { return new ColorSer(0, 0, 0, 0); } }

        public override string ToString() { return $"({R:F2},{G:F2},{B:F2},{A:F2})"; }
    }

    [Serializable]
    public struct RectOffsetSer
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;

        public RectOffsetSer(int l, int r, int t, int b)
        {
            Left = l; Right = r; Top = t; Bottom = b;
        }

        public RectOffset ToRectOffset() { return new RectOffset(Left, Right, Top, Bottom); }

        public static RectOffsetSer From(RectOffset o)
        {
            return new RectOffsetSer(o.left, o.right, o.top, o.bottom);
        }
    }

    //  Top-level layout document

    [Serializable]
    public class UILayoutDefinition
    {
        public string UID = "";
        public string DisplayName = "";
        public string Category = "Custom";
        public string Author = "";
        public long CreatedTimestamp;
        public long ModifiedTimestamp;
        public int Version = 1;
        public Vector2Ser CanvasSize = new Vector2Ser(1920, 1080);
        public UIElementNode RootElement;
        public List<MetadataEntry> Metadata = new List<MetadataEntry>();

        /// <summary>
        /// CanvasScaler configuration for resolution-independent rendering.
        /// When null, defaults to ScaleWithScreenSize at 1920x1080 with 0.5 match.
        /// </summary>
        public UICanvasScalerDef CanvasScaler;

        /// <summary>
        /// Screen-space anchored position for the root element when shown to the player.
        /// Set via Preview Mode's Layout Positioning mode. Stored in normalized screen
        /// coordinates (0..1) so it adapts to different resolutions.
        /// When both are NaN, the layout uses its default anchors/position as designed.
        /// </summary>
        public float ScreenPositionX = float.NaN;
        public float ScreenPositionY = float.NaN;

        /// <summary>
        /// Uniform scale factor for the root element when displayed at runtime.
        /// Set via Preview Mode's Layout Positioning mode (scroll wheel to scale).
        /// Default is 1.0 (no scaling). Stored as a simple multiplier.
        /// </summary>
        public float ScreenScale = 1f;

        /// <summary>
        /// Stores the original root element RectTransform data captured before
        /// NormalizeForEditor converted it to center-anchored fixed-size for the editor.
        /// Used by the override system and fullscreen preview to instantiate the layout
        /// with the correct stretch-fill root so children are in the right coordinate space.
        /// Null if the layout was never normalized (e.g., freshly created layouts).
        /// </summary>
        public RuntimeRootTransformDef RuntimeRootTransform;

        /// <summary>
        /// Returns true if the layout has stored runtime root transform data
        /// (i.e., it was normalized for the editor and the original transform was preserved).
        /// </summary>
        public bool HasRuntimeRootTransform()
        {
            return RuntimeRootTransform != null;
        }

        /// <summary>
        /// Temporarily applies the stored runtime root transform onto the root element node,
        /// returns the saved editor values so they can be restored after instantiation.
        /// Returns null if no runtime root transform is stored.
        /// </summary>
        public RuntimeRootTransformDef SwapRootToRuntime()
        {
            if (RuntimeRootTransform == null || RootElement == null) return null;

            var saved = new RuntimeRootTransformDef
            {
                AnchorMin = RootElement.AnchorMin,
                AnchorMax = RootElement.AnchorMax,
                Pivot = RootElement.Pivot,
                AnchoredPosition = RootElement.AnchoredPosition,
                SizeDelta = RootElement.SizeDelta,
                OffsetMin = RootElement.OffsetMin,
                OffsetMax = RootElement.OffsetMax
            };

            RootElement.AnchorMin = RuntimeRootTransform.AnchorMin;
            RootElement.AnchorMax = RuntimeRootTransform.AnchorMax;
            RootElement.Pivot = RuntimeRootTransform.Pivot;
            RootElement.AnchoredPosition = RuntimeRootTransform.AnchoredPosition;
            RootElement.SizeDelta = RuntimeRootTransform.SizeDelta;
            RootElement.OffsetMin = RuntimeRootTransform.OffsetMin;
            RootElement.OffsetMax = RuntimeRootTransform.OffsetMax;

            return saved;
        }

        /// <summary>
        /// Restores the root element node's transform data from previously saved editor values.
        /// </summary>
        public void RestoreRootFromSaved(RuntimeRootTransformDef saved)
        {
            if (saved == null || RootElement == null) return;

            RootElement.AnchorMin = saved.AnchorMin;
            RootElement.AnchorMax = saved.AnchorMax;
            RootElement.Pivot = saved.Pivot;
            RootElement.AnchoredPosition = saved.AnchoredPosition;
            RootElement.SizeDelta = saved.SizeDelta;
            RootElement.OffsetMin = saved.OffsetMin;
            RootElement.OffsetMax = saved.OffsetMax;
        }

        public string GetMeta(string key)
        {
            if (Metadata == null) return null;
            for (int i = 0; i < Metadata.Count; i++)
                if (string.Equals(Metadata[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    return Metadata[i].Value;
            return null;
        }

        public void SetMeta(string key, string value)
        {
            if (Metadata == null) Metadata = new List<MetadataEntry>();
            for (int i = 0; i < Metadata.Count; i++)
            {
                if (string.Equals(Metadata[i].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    Metadata[i] = new MetadataEntry { Key = key, Value = value };
                    return;
                }
            }
            Metadata.Add(new MetadataEntry { Key = key, Value = value });
        }

        /// <summary>
        /// Returns true if the layout has a screen position override set via preview layout mode.
        /// </summary>
        public bool HasScreenPosition()
        {
            return !float.IsNaN(ScreenPositionX) && !float.IsNaN(ScreenPositionY);
        }

        /// <summary>
        /// Clears the screen position override so the layout uses its default positioning.
        /// </summary>
        public void ClearScreenPosition()
        {
            ScreenPositionX = float.NaN;
            ScreenPositionY = float.NaN;
            ScreenScale = 1f;
        }

        public UILayoutDefinition DeepClone()
        {
            string json = UILayoutSerializer.Serialize(this);
            return UILayoutSerializer.Deserialize(json);
        }
    }

    [Serializable]
    public struct MetadataEntry
    {
        public string Key;
        public string Value;
    }

    //  Element node (recursive tree)

    [Serializable]
    public class UIElementNode
    {
        // Identity
        public string Id = "";
        public string Name = "";
        public string Tag = "";

        // Type
        public UIElementType Type = UIElementType.Panel;

        // RectTransform
        public Vector2Ser AnchorMin = new Vector2Ser(0, 0);
        public Vector2Ser AnchorMax = new Vector2Ser(1, 1);
        public Vector2Ser Pivot = new Vector2Ser(0.5f, 0.5f);
        public Vector2Ser AnchoredPosition = new Vector2Ser(0, 0);
        public Vector2Ser SizeDelta = new Vector2Ser(0, 0);
        public Vector2Ser OffsetMin = new Vector2Ser(0, 0);
        public Vector2Ser OffsetMax = new Vector2Ser(0, 0);
        public float Rotation;

        // Visual Style
        public UIElementStyle Style;

        // Layout components (optional)
        public UILayoutGroupDef LayoutGroup;
        public UIGridLayoutGroupDef GridLayoutGroup;
        public UILayoutElementDef LayoutElement;
        public UIContentFitterDef ContentFitter;

        // Component-specific data (only one applies based on Type)
        public UITextDef TextData;
        public UIImageDef ImageData;
        public UIInputFieldDef InputFieldData;
        public UIScrollViewDef ScrollViewData;
        public UIDropdownDef DropdownData;
        public UIButtonDef ButtonData;
        public UIToggleDef ToggleData;
        public UISliderDef SliderData;
        public UIRenderCameraDef RenderCameraData;

        // Aspect Ratio Fitter (optional)
        public UIAspectRatioFitterDef AspectRatioFitter;

        // Masks
        public bool HasMask;
        public bool HasRectMask2D;

        // Children
        public List<UIElementNode> Children;

        // Per-element metadata (key-value pairs for parser hints, grid info, etc.)
        public List<MetadataEntry> Metadata;

        // State
        public bool Active = true;
        public bool Interactable = true;

        /// <summary>
        /// When true, the element cannot be moved or resized via drag/resize in the editor.
        /// Useful for locking finalized elements in place.
        /// </summary>
        public bool TransformLocked;

        /// <summary>
        /// When true, all direct children of this element are group-locked.
        /// Dragging any child in the workspace moves all siblings together as one unit.
        /// </summary>
        public bool GroupLocked;

        public UIElementNode() { }

        public UIElementNode(string id, string name, UIElementType type)
        {
            Id = id;
            Name = name;
            Type = type;
        }

        public void AddChild(UIElementNode child)
        {
            if (Children == null) Children = new List<UIElementNode>();
            Children.Add(child);
        }

        public string GetMeta(string key)
        {
            if (Metadata == null) return null;
            for (int i = 0; i < Metadata.Count; i++)
                if (string.Equals(Metadata[i].Key, key, StringComparison.OrdinalIgnoreCase))
                    return Metadata[i].Value;
            return null;
        }

        public void SetMeta(string key, string value)
        {
            if (Metadata == null) Metadata = new List<MetadataEntry>();
            for (int i = 0; i < Metadata.Count; i++)
            {
                if (string.Equals(Metadata[i].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    Metadata[i] = new MetadataEntry { Key = key, Value = value };
                    return;
                }
            }
            Metadata.Add(new MetadataEntry { Key = key, Value = value });
        }

        public UIElementNode FindByTag(string tag)
        {
            if (!string.IsNullOrEmpty(Tag) && string.Equals(Tag, tag, StringComparison.OrdinalIgnoreCase))
                return this;
            if (Children != null)
            {
                for (int i = 0; i < Children.Count; i++)
                {
                    var found = Children[i].FindByTag(tag);
                    if (found != null) return found;
                }
            }
            return null;
        }

        public UIElementNode FindById(string id)
        {
            if (string.Equals(Id, id, StringComparison.OrdinalIgnoreCase))
                return this;
            if (Children != null)
            {
                for (int i = 0; i < Children.Count; i++)
                {
                    var found = Children[i].FindById(id);
                    if (found != null) return found;
                }
            }
            return null;
        }

        public int CountAll()
        {
            int count = 1;
            if (Children != null)
                for (int i = 0; i < Children.Count; i++)
                    count += Children[i].CountAll();
            return count;
        }
    }

    //  Style & component definitions

    [Serializable]
    public class UIElementStyle
    {
        public ColorSer BackgroundColor = new ColorSer(0.1f, 0.1f, 0.12f, 0.9f);
        public ColorSer BorderColor = new ColorSer(0.4f, 0.3f, 0.15f, 0.8f);
        public float BorderWidth;
        public float CornerRadius;
        public float Opacity = 1f;
        public bool RaycastTarget = true;
        public UIShapeType Shape = UIShapeType.Rectangle;
        public string BackgroundSprite = "";
        public int ImageType;
    }

    [Serializable]
    public class UITextDef
    {
        public string Text = "";
        public float FontSize = 16f;
        public string FontCategory = "Body";
        public int FontStyle;
        public ColorSer Color = new ColorSer(1f, 0.85f, 0.5f, 1f);
        public int Alignment = (int)TMPro.TextAlignmentOptions.MidlineLeft;
        public bool WordWrap = true;
        public int OverflowMode;
        public float LineSpacing;
        public RectOffsetSer TextPadding = new RectOffsetSer(0, 0, 0, 0);
        /// <summary>
        /// Captured Text component .enabled state.
        /// When false, the component was disabled at capture time (e.g., inventory slot
        /// amount/quality text which Valheim toggles via .enabled).
        /// Default is true for backwards compatibility.
        /// </summary>
        public bool ComponentEnabled = true;
    }

    [Serializable]
    public class UIImageDef
    {
        public string SpriteName = "";
        public int ImageType;
        public bool PreserveAspect;
        public ColorSer Color = ColorSer.White;
        public bool FillCenter = true;
        public float PixelsPerUnit = 100f;
        /// <summary>
        /// Captured Image/RawImage component .enabled state.
        /// When false, the component was disabled at capture time (e.g., inventory slot
        /// indicators like equiped, queued, noteleport, foodicon, quality, amount, icon
        /// which Valheim toggles via .enabled rather than SetActive).
        /// Default is true for backwards compatibility with layouts captured before this field.
        /// </summary>
        public bool ComponentEnabled = true;
    }

    [Serializable]
    public class UIInputFieldDef
    {
        public string PlaceholderText = "";
        public float FontSize = 13f;
        /// <summary>
        /// Component enabled state. When false, the InputField is visually hidden.
        /// Default true.
        /// </summary>
        public bool ComponentEnabled = true;
        public bool Multiline;
        public int CharacterLimit;
        public int TextAlignment = (int)TMPro.TextAlignmentOptions.MidlineLeft;
        public RectOffsetSer TextPadding = new RectOffsetSer(4, 4, 2, 2);
        public ColorSer TextColor = new ColorSer(1f, 0.85f, 0.5f, 1f);
        public ColorSer PlaceholderColor = new ColorSer(0.5f, 0.4f, 0.25f, 0.6f);
        public ColorSer BackgroundColor = new ColorSer(0.06f, 0.06f, 0.08f, 0.65f);
        public ColorSer CaretColor = new ColorSer(1f, 0.9f, 0.6f, 1f);
        public ColorSer SelectionColor = new ColorSer(0.6f, 0.4f, 0.2f, 0.4f);
        public string ContentType = "Standard";
    }

    [Serializable]
    public class UIScrollViewDef
    {
        public bool Horizontal;
        public bool Vertical = true;
        /// <summary>
        /// Component enabled state. When false, the ScrollView is visually hidden.
        /// Default true.
        /// </summary>
        public bool ComponentEnabled = true;
        public int MovementType = 2;
        public float ScrollSensitivity = 30f;
        public float Elasticity = 0.1f;
        public ColorSer ViewportColor = new ColorSer(0, 0, 0, 0);
        public ColorSer ContentColor = new ColorSer(0, 0, 0, 0);
    }

    [Serializable]
    public class UIDropdownDef
    {
        public List<string> Options = new List<string>();
        public int DefaultValue;
        /// <summary>
        /// Component enabled state. When false, the Dropdown is visually hidden.
        /// Default true.
        /// </summary>
        public bool ComponentEnabled = true;
        public float ItemHeight = 24f;
        public float TemplateHeight = 160f;
        public float FontSize = 13f;
        public ColorSer TextColor = new ColorSer(1f, 0.85f, 0.5f, 1f);
        public ColorSer BackgroundColor = new ColorSer(0.06f, 0.06f, 0.08f, 0.65f);
        public ColorSer ItemColor = new ColorSer(0.12f, 0.11f, 0.1f, 0.95f);
    }

    [Serializable]
    public class UIButtonDef
    {
        public string Label = "Button";
        public bool ShowLabel;
        public float FontSize = 13f;
        /// <summary>
        /// Component enabled state. When false, the Button component is visually hidden
        /// in the override (the GO is deactivated). Default true.
        /// </summary>
        public bool ComponentEnabled = true;
        public ColorSer LabelColor = new ColorSer(1f, 0.85f, 0.5f, 1f);
        public int LabelAlignment = (int)TMPro.TextAlignmentOptions.Center;
        public RectOffsetSer LabelPadding = new RectOffsetSer(4, 4, 2, 2);
        public ColorSer NormalColor = new ColorSer(0.15f, 0.12f, 0.08f, 0.9f);
        public ColorSer HighlightedColor = new ColorSer(0.4f, 0.3f, 0.15f, 0.95f);
        public ColorSer PressedColor = new ColorSer(0.6f, 0.4f, 0.2f, 1f);
        public ColorSer SelectedColor = new ColorSer(0.15f, 0.12f, 0.08f, 0.9f);
        public ColorSer DisabledColor = new ColorSer(0.1f, 0.1f, 0.1f, 0.5f);
        public float FadeDuration = 0.08f;
        public string ClickAction = "";
        public string ClickActionParam = "";
    }

    [Serializable]
    public class UIToggleDef
    {
        public bool DefaultValue;
        /// <summary>
        /// Component enabled state. When false, the Toggle is visually hidden.
        /// Default true.
        /// </summary>
        public bool ComponentEnabled = true;
        public ColorSer CheckmarkColor = new ColorSer(1f, 0.85f, 0.5f, 1f);
        public ColorSer BackgroundColor = new ColorSer(0.06f, 0.06f, 0.08f, 0.65f);
        public string Label = "";
        public float FontSize = 13f;
        public ColorSer LabelColor = new ColorSer(1f, 0.85f, 0.5f, 1f);
    }

    [Serializable]
    public class UISliderDef
    {
        public float MinValue;
        public float MaxValue = 1f;
        /// <summary>
        /// Component enabled state. When false, the Slider is visually hidden.
        /// Default true.
        /// </summary>
        public bool ComponentEnabled = true;
        public float DefaultValue;
        public bool WholeNumbers;
        public ColorSer BackgroundColor = new ColorSer(0.1f, 0.1f, 0.12f, 0.9f);
        public ColorSer FillColor = new ColorSer(0.4f, 0.3f, 0.15f, 0.95f);
        public ColorSer HandleColor = new ColorSer(1f, 0.85f, 0.5f, 1f);
    }

    [Serializable]
    public class UILayoutGroupDef
    {
        public bool IsVertical = true;
        public float Spacing;
        public RectOffsetSer Padding = new RectOffsetSer(0, 0, 0, 0);
        public int ChildAlignment;
        public bool ChildControlWidth = true;
        public bool ChildControlHeight;
        public bool ChildForceExpandWidth = true;
        public bool ChildForceExpandHeight;
    }

    [Serializable]
    public class UILayoutElementDef
    {
        public float MinWidth = -1;
        public float MinHeight = -1;
        public float PreferredWidth = -1;
        public float PreferredHeight = -1;
        public float FlexibleWidth = -1;
        public float FlexibleHeight = -1;
        public bool IgnoreLayout;
    }

    [Serializable]
    public class UIContentFitterDef
    {
        public int HorizontalFit;
        public int VerticalFit;
    }

    /// <summary>
    /// Definition for a camera preview element.
    /// Renders a Camera's output onto a RawImage via RenderTexture.
    /// Can display the player model, a prefab preview, or a world view.
    /// </summary>
    [Serializable]
    public class UIRenderCameraDef
    {
        /// <summary>What the camera renders: "player", "prefab", "world".</summary>
        public string Mode = "player";
        /// <summary>
        /// Component enabled state. When false, the RenderCamera is visually hidden.
        /// Default true.
        /// </summary>
        public bool ComponentEnabled = true;
        /// <summary>Prefab name to spawn for preview when Mode is "prefab".</summary>
        public string PrefabName = "";
        /// <summary>Camera field of view.</summary>
        public float FieldOfView = 80f;
        /// <summary>Camera background color.</summary>
        public ColorSer BackgroundColor = new ColorSer(0, 0, 0, 0);
        /// <summary>Resolution of the RenderTexture (width and height).</summary>
        public int TextureWidth = 256;
        public int TextureHeight = 256;
        /// <summary>Camera offset from the target pivot.</summary>
        public Vector2Ser CameraOffset = new Vector2Ser(0, 0);
        /// <summary>Camera distance from target.</summary>
        public float CameraDistance = 3f;
        /// <summary>Camera rotation around the target (Euler Y).</summary>
        public float CameraRotationY = 0f;
        /// <summary>Camera rotation pitch (Euler X).</summary>
        public float CameraRotationX = 10f;
        /// <summary>Layer mask name for the camera. Uses a dedicated preview layer.</summary>
        public string LayerName = "";
    }

    /// <summary>
    /// GridLayoutGroup definition. Used by inventory/crafting grids in vanilla Valheim.
    /// </summary>
    [Serializable]
    public class UIGridLayoutGroupDef
    {
        public Vector2Ser CellSize = new Vector2Ser(64, 64);
        public Vector2Ser Spacing = new Vector2Ser(0, 0);
        public int StartCorner;   // GridLayoutGroup.Corner enum
        public int StartAxis;     // GridLayoutGroup.Axis enum
        public int ChildAlignment; // TextAnchor enum
        public int Constraint;    // GridLayoutGroup.Constraint enum
        public int ConstraintCount = 2;
        public RectOffsetSer Padding = new RectOffsetSer(0, 0, 0, 0);
    }

    /// <summary>
    /// CanvasScaler configuration for resolution-independent UI rendering.
    /// Mirrors Unity's CanvasScaler component. The key formula for ScaleWithScreenSize:
    /// scaleFactor = Pow(2, Lerp(Log2(screenW/refW), Log2(screenH/refH), match))
    /// </summary>
    [Serializable]
    public class UICanvasScalerDef
    {
        /// <summary>0=ConstantPixelSize, 1=ScaleWithScreenSize, 2=ConstantPhysicalSize</summary>
        public int ScaleMode = 1;
        public Vector2Ser ReferenceResolution = new Vector2Ser(1920, 1080);
        /// <summary>0=match width, 1=match height, 0.5=balanced</summary>
        public float MatchWidthOrHeight = 0.5f;
        public float ReferencePixelsPerUnit = 100f;
        /// <summary>Used only for ConstantPixelSize mode.</summary>
        public float ScaleFactor = 1f;
    }

    /// <summary>
    /// AspectRatioFitter definition. Maintains a specific aspect ratio on the element.
    /// Mirrors Unity's AspectRatioFitter component.
    /// </summary>
    [Serializable]
    public class UIAspectRatioFitterDef
    {
        /// <summary>0=None, 1=WidthControlsHeight, 2=HeightControlsWidth, 3=FitInParent, 4=EnvelopeParent</summary>
        public int AspectMode = 1;
        public float AspectRatio = 1f;
    }

    /// <summary>
    /// Stores the original root element RectTransform values captured before
    /// NormalizeForEditor converted them to center-anchored fixed-size.
    /// This preserves the stretch-fill (or other) anchoring so the runtime
    /// override system and fullscreen preview can instantiate with the correct
    /// root transform, making children position correctly at any resolution.
    /// </summary>
    [Serializable]
    public class RuntimeRootTransformDef
    {
        public Vector2Ser AnchorMin = new Vector2Ser(0, 0);
        public Vector2Ser AnchorMax = new Vector2Ser(1, 1);
        public Vector2Ser Pivot = new Vector2Ser(0.5f, 0.5f);
        public Vector2Ser AnchoredPosition = new Vector2Ser(0, 0);
        public Vector2Ser SizeDelta = new Vector2Ser(0, 0);
        public Vector2Ser OffsetMin = new Vector2Ser(0, 0);
        public Vector2Ser OffsetMax = new Vector2Ser(0, 0);

        public static RuntimeRootTransformDef FromNode(UIElementNode node)
        {
            if (node == null) return null;
            return new RuntimeRootTransformDef
            {
                AnchorMin = node.AnchorMin,
                AnchorMax = node.AnchorMax,
                Pivot = node.Pivot,
                AnchoredPosition = node.AnchoredPosition,
                SizeDelta = node.SizeDelta,
                OffsetMin = node.OffsetMin,
                OffsetMax = node.OffsetMax
            };
        }

        public void ApplyToRectTransform(RectTransform rect)
        {
            if (rect == null) return;
            rect.anchorMin = AnchorMin.ToVector2();
            rect.anchorMax = AnchorMax.ToVector2();
            rect.pivot = Pivot.ToVector2();
            rect.anchoredPosition = AnchoredPosition.ToVector2();
            rect.sizeDelta = SizeDelta.ToVector2();
            rect.offsetMin = OffsetMin.ToVector2();
            rect.offsetMax = OffsetMax.ToVector2();
        }
    }
}
