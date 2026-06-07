using BepInEx;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Hand-rolled JSON serializer/deserializer for UILayoutDefinition.
    /// Uses a recursive approach to handle the nested UIElementNode tree.
    /// Avoids Unity's JsonUtility limitations with nested objects, nulls, and dictionaries.
    /// Targets .NET Framework 4.8.
    /// </summary>
    public static class UILayoutSerializer
    {
        private static string LayoutsDir
        {
            get { return Path.Combine(Paths.ConfigPath, "FiresRPGmaker", "UILayouts"); }
        }

        // ???????????????????????????????????????
        //  Public API
        // ???????????????????????????????????????

        public static string Serialize(UILayoutDefinition layout)
        {
            if (layout == null) return "{}";
            var sb = new StringBuilder(4096);
            WriteLayout(sb, layout, 0);
            return sb.ToString();
        }

        public static UILayoutDefinition Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                int pos = 0;
                var obj = ParseObject(json, ref pos);
                return ReadLayout(obj);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutSerializer] Deserialize error: {ex.Message}");
                return null;
            }
        }

        public static void SaveToFile(UILayoutDefinition layout, string relativePath)
        {
            if (layout == null || string.IsNullOrEmpty(relativePath)) return;
            string fullPath = Path.Combine(LayoutsDir, relativePath);
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(fullPath, Serialize(layout), Encoding.UTF8);
        }

        /// <summary>
        /// Writes pre-serialized JSON to a layout file. Used by the async save path
        /// where serialization happened on a background thread.
        /// </summary>
        public static void SaveJsonToFile(string json, string relativePath)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(relativePath)) return;
            string fullPath = Path.Combine(LayoutsDir, relativePath);
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(fullPath, json, Encoding.UTF8);
        }

        public static UILayoutDefinition LoadFromFile(string relativePath)
        {
            string fullPath = Path.Combine(LayoutsDir, relativePath);
            if (!File.Exists(fullPath)) return null;
            string json = File.ReadAllText(fullPath, Encoding.UTF8);
            return Deserialize(json);
        }

        public static Dictionary<string, UILayoutDefinition> LoadAll()
        {
            var result = new Dictionary<string, UILayoutDefinition>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(LayoutsDir)) return result;

            foreach (var file in Directory.GetFiles(LayoutsDir, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    string json = File.ReadAllText(file, Encoding.UTF8);
                    var layout = Deserialize(json);
                    if (layout != null && !string.IsNullOrEmpty(layout.UID))
                        result[layout.UID] = layout;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UILayoutSerializer] Failed to load {file}: {ex.Message}");
                }
            }
            return result;
        }

        /// <summary>
        /// Deletes a layout JSON file from disk by searching for a file whose UID matches.
        /// Returns true if a file was found and deleted.
        /// </summary>
        public static bool DeleteLayoutFile(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return false;
            if (!Directory.Exists(LayoutsDir)) return false;

            foreach (var file in Directory.GetFiles(LayoutsDir, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    string json = File.ReadAllText(file, Encoding.UTF8);
                    var layout = Deserialize(json);
                    if (layout != null && string.Equals(layout.UID, uid, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(file);
                        Debug.Log($"[UILayoutSerializer] Deleted layout file: {file}");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UILayoutSerializer] Error checking file {file} for deletion: {ex.Message}");
                }
            }
            return false;
        }

        /// <summary>
        /// Deletes ALL layout JSON files from disk. Returns the number of files deleted.
        /// </summary>
        public static int DeleteAllLayoutFiles()
        {
            if (!Directory.Exists(LayoutsDir)) return 0;
            int deleted = 0;
            foreach (var file in Directory.GetFiles(LayoutsDir, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UILayoutSerializer] Failed to delete {file}: {ex.Message}");
                }
            }
            Debug.Log($"[UILayoutSerializer] Deleted {deleted} layout file(s)");
            return deleted;
        }

        // ???????????????????????????????????????
        //  JSON Writer
        // ???????????????????????????????????????

        private static void WriteLayout(StringBuilder sb, UILayoutDefinition d, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteString(sb, i, "UID", d.UID); sb.AppendLine(",");
            WriteString(sb, i, "DisplayName", d.DisplayName); sb.AppendLine(",");
            WriteString(sb, i, "Category", d.Category); sb.AppendLine(",");
            WriteString(sb, i, "Author", d.Author); sb.AppendLine(",");
            WriteLong(sb, i, "CreatedTimestamp", d.CreatedTimestamp); sb.AppendLine(",");
            WriteLong(sb, i, "ModifiedTimestamp", d.ModifiedTimestamp); sb.AppendLine(",");
            WriteInt(sb, i, "Version", d.Version); sb.AppendLine(",");
            WriteVec2(sb, i, "CanvasSize", d.CanvasSize); sb.AppendLine(",");

            // Metadata
            Indent(sb, i); sb.Append("\"Metadata\": [");
            if (d.Metadata != null && d.Metadata.Count > 0)
            {
                sb.AppendLine();
                for (int m = 0; m < d.Metadata.Count; m++)
                {
                    Indent(sb, i + 1);
                    sb.Append("{");
                    sb.Append($"\"Key\":{Esc(d.Metadata[m].Key)},\"Value\":{Esc(d.Metadata[m].Value)}");
                    sb.Append("}");
                    if (m < d.Metadata.Count - 1) sb.Append(",");
                    sb.AppendLine();
                }
                Indent(sb, i);
            }
            sb.AppendLine("],");

            // Canvas scaler
            WriteOptional(sb, i, "CanvasScaler", d.CanvasScaler, WriteCanvasScaler); sb.AppendLine(",");

            // Screen position and scale (preview layout mode)
            if (!float.IsNaN(d.ScreenPositionX) && !float.IsNaN(d.ScreenPositionY))
            {
                WriteFloat(sb, i, "ScreenPositionX", d.ScreenPositionX); sb.AppendLine(",");
                WriteFloat(sb, i, "ScreenPositionY", d.ScreenPositionY); sb.AppendLine(",");
            }
            if (!Mathf.Approximately(d.ScreenScale, 1f))
            {
                WriteFloat(sb, i, "ScreenScale", d.ScreenScale); sb.AppendLine(",");
            }

            // RuntimeRootTransform (preserved original root before editor normalization)
            if (d.RuntimeRootTransform != null)
            {
                WriteOptional(sb, i, "RuntimeRootTransform", d.RuntimeRootTransform, WriteRuntimeRootTransform); sb.AppendLine(",");
            }

            // Root element
            Indent(sb, i); sb.AppendLine("\"RootElement\":");
            if (d.RootElement != null)
                WriteNode(sb, d.RootElement, i);
            else
            { Indent(sb, i); sb.Append("null"); }

            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteNode(StringBuilder sb, UIElementNode n, int indent)
        {
            Indent(sb, indent); sb.AppendLine("{");
            int i = indent + 1;

            // Identity
            WriteString(sb, i, "Id", n.Id); sb.AppendLine(",");
            WriteString(sb, i, "Name", n.Name); sb.AppendLine(",");
            if (!string.IsNullOrEmpty(n.Tag)) { WriteString(sb, i, "Tag", n.Tag); sb.AppendLine(","); }
            WriteString(sb, i, "Type", n.Type.ToString()); sb.AppendLine(",");

            // Transform
            WriteVec2(sb, i, "AnchorMin", n.AnchorMin); sb.AppendLine(",");
            WriteVec2(sb, i, "AnchorMax", n.AnchorMax); sb.AppendLine(",");
            WriteVec2(sb, i, "Pivot", n.Pivot); sb.AppendLine(",");
            WriteVec2(sb, i, "AnchoredPosition", n.AnchoredPosition); sb.AppendLine(",");
            WriteVec2(sb, i, "SizeDelta", n.SizeDelta); sb.AppendLine(",");
            WriteVec2(sb, i, "OffsetMin", n.OffsetMin); sb.AppendLine(",");
            WriteVec2(sb, i, "OffsetMax", n.OffsetMax); sb.AppendLine(",");
            if (n.Rotation != 0) { WriteFloat(sb, i, "Rotation", n.Rotation); sb.AppendLine(","); }

            // State � only write non-defaults
            if (!n.Active) { WriteBool(sb, i, "Active", n.Active); sb.AppendLine(","); }
            if (!n.Interactable) { WriteBool(sb, i, "Interactable", n.Interactable); sb.AppendLine(","); }
            if (n.HasMask) { WriteBool(sb, i, "HasMask", n.HasMask); sb.AppendLine(","); }
            if (n.HasRectMask2D) { WriteBool(sb, i, "HasRectMask2D", n.HasRectMask2D); sb.AppendLine(","); }
            if (n.TransformLocked) { WriteBool(sb, i, "TransformLocked", n.TransformLocked); sb.AppendLine(","); }

            // Per-element metadata � only if non-empty
            if (n.Metadata != null && n.Metadata.Count > 0)
            {
                Indent(sb, i); sb.Append("\"Metadata\": [");
                sb.AppendLine();
                for (int m = 0; m < n.Metadata.Count; m++)
                {
                    Indent(sb, i + 1);
                    sb.Append("{");
                    sb.Append($"\"Key\":{Esc(n.Metadata[m].Key)},\"Value\":{Esc(n.Metadata[m].Value)}");
                    sb.Append("}");
                    if (m < n.Metadata.Count - 1) sb.Append(",");
                    sb.AppendLine();
                }
                Indent(sb, i);
                sb.AppendLine("],");
            }

            // Style
            if (n.Style != null)
            {
                Indent(sb, i); sb.Append("\"Style\": ");
                WriteStyle(sb, n.Style, i); sb.AppendLine(",");
            }

            // Optional components � SKIP null entries entirely.
            // The deserializer already handles missing keys via ContainsKey checks.
            if (n.LayoutGroup != null) { WriteOptional(sb, i, "LayoutGroup", n.LayoutGroup, WriteLayoutGroup); sb.AppendLine(","); }
            if (n.GridLayoutGroup != null) { WriteOptional(sb, i, "GridLayoutGroup", n.GridLayoutGroup, WriteGridLayoutGroup); sb.AppendLine(","); }
            if (n.LayoutElement != null) { WriteOptional(sb, i, "LayoutElement", n.LayoutElement, WriteLayoutElement); sb.AppendLine(","); }
            if (n.ContentFitter != null) { WriteOptional(sb, i, "ContentFitter", n.ContentFitter, WriteContentFitter); sb.AppendLine(","); }
            if (n.AspectRatioFitter != null) { WriteOptional(sb, i, "AspectRatioFitter", n.AspectRatioFitter, WriteAspectRatioFitter); sb.AppendLine(","); }
            if (n.TextData != null) { WriteOptional(sb, i, "TextData", n.TextData, WriteText); sb.AppendLine(","); }
            if (n.ImageData != null) { WriteOptional(sb, i, "ImageData", n.ImageData, WriteImage); sb.AppendLine(","); }
            if (n.InputFieldData != null) { WriteOptional(sb, i, "InputFieldData", n.InputFieldData, WriteInputField); sb.AppendLine(","); }
            if (n.ScrollViewData != null) { WriteOptional(sb, i, "ScrollViewData", n.ScrollViewData, WriteScrollView); sb.AppendLine(","); }
            if (n.DropdownData != null) { WriteOptional(sb, i, "DropdownData", n.DropdownData, WriteDropdown); sb.AppendLine(","); }
            if (n.ButtonData != null) { WriteOptional(sb, i, "ButtonData", n.ButtonData, WriteButton); sb.AppendLine(","); }
            if (n.ToggleData != null) { WriteOptional(sb, i, "ToggleData", n.ToggleData, WriteToggle); sb.AppendLine(","); }
            if (n.SliderData != null) { WriteOptional(sb, i, "SliderData", n.SliderData, WriteSlider); sb.AppendLine(","); }
            if (n.RenderCameraData != null) { WriteOptional(sb, i, "RenderCameraData", n.RenderCameraData, WriteRenderCamera); sb.AppendLine(","); }

            // Children � always last (no trailing comma issue)
            Indent(sb, i); sb.Append("\"Children\": ");
            if (n.Children != null && n.Children.Count > 0)
            {
                sb.AppendLine("[");
                for (int c = 0; c < n.Children.Count; c++)
                {
                    WriteNode(sb, n.Children[c], i + 1);
                    if (c < n.Children.Count - 1) sb.AppendLine(",");
                    else sb.AppendLine();
                }
                Indent(sb, i); sb.Append("]");
            }
            else
            {
                sb.Append("[]");
            }
            sb.AppendLine();

            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteStyle(StringBuilder sb, UIElementStyle s, int indent)
        {
            if (s == null) { sb.Append("null"); return; }
            sb.AppendLine("{");
            int i = indent + 1;
            WriteColor(sb, i, "BackgroundColor", s.BackgroundColor); sb.AppendLine(",");
            WriteColor(sb, i, "BorderColor", s.BorderColor); sb.AppendLine(",");
            WriteFloat(sb, i, "BorderWidth", s.BorderWidth); sb.AppendLine(",");
            WriteFloat(sb, i, "CornerRadius", s.CornerRadius); sb.AppendLine(",");
            WriteFloat(sb, i, "Opacity", s.Opacity); sb.AppendLine(",");
            WriteBool(sb, i, "RaycastTarget", s.RaycastTarget); sb.AppendLine(",");
            WriteString(sb, i, "Shape", s.Shape.ToString()); sb.AppendLine(",");
            WriteString(sb, i, "BackgroundSprite", s.BackgroundSprite); sb.AppendLine(",");
            WriteInt(sb, i, "ImageType", s.ImageType); sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteText(StringBuilder sb, UITextDef t, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteString(sb, i, "Text", t.Text); sb.AppendLine(",");
            WriteFloat(sb, i, "FontSize", t.FontSize); sb.AppendLine(",");
            WriteString(sb, i, "FontCategory", t.FontCategory); sb.AppendLine(",");
            WriteInt(sb, i, "FontStyle", t.FontStyle); sb.AppendLine(",");
            WriteColor(sb, i, "Color", t.Color); sb.AppendLine(",");
            WriteInt(sb, i, "Alignment", t.Alignment); sb.AppendLine(",");
            WriteBool(sb, i, "WordWrap", t.WordWrap); sb.AppendLine(",");
            WriteInt(sb, i, "OverflowMode", t.OverflowMode); sb.AppendLine(",");
            WriteFloat(sb, i, "LineSpacing", t.LineSpacing); sb.AppendLine(",");
            WriteRectOffset(sb, i, "TextPadding", t.TextPadding);
            if (!t.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", t.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteImage(StringBuilder sb, UIImageDef d, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteString(sb, i, "SpriteName", d.SpriteName); sb.AppendLine(",");
            WriteInt(sb, i, "ImageType", d.ImageType); sb.AppendLine(",");
            WriteBool(sb, i, "PreserveAspect", d.PreserveAspect); sb.AppendLine(",");
            WriteColor(sb, i, "Color", d.Color); sb.AppendLine(",");
            WriteBool(sb, i, "FillCenter", d.FillCenter); sb.AppendLine(",");
            WriteFloat(sb, i, "PixelsPerUnit", d.PixelsPerUnit);
            if (!d.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", d.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteInputField(StringBuilder sb, UIInputFieldDef d, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteString(sb, i, "PlaceholderText", d.PlaceholderText); sb.AppendLine(",");
            WriteFloat(sb, i, "FontSize", d.FontSize); sb.AppendLine(",");
            WriteBool(sb, i, "Multiline", d.Multiline); sb.AppendLine(",");
            WriteInt(sb, i, "CharacterLimit", d.CharacterLimit); sb.AppendLine(",");
            WriteColor(sb, i, "TextColor", d.TextColor); sb.AppendLine(",");
            WriteColor(sb, i, "PlaceholderColor", d.PlaceholderColor); sb.AppendLine(",");
            WriteColor(sb, i, "BackgroundColor", d.BackgroundColor); sb.AppendLine(",");
            WriteColor(sb, i, "CaretColor", d.CaretColor); sb.AppendLine(",");
            WriteColor(sb, i, "SelectionColor", d.SelectionColor); sb.AppendLine(",");
            WriteString(sb, i, "ContentType", d.ContentType); sb.AppendLine(",");
            WriteInt(sb, i, "TextAlignment", d.TextAlignment); sb.AppendLine(",");
            WriteRectOffset(sb, i, "TextPadding", d.TextPadding);
            if (!d.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", d.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteScrollView(StringBuilder sb, UIScrollViewDef d, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteBool(sb, i, "Horizontal", d.Horizontal); sb.AppendLine(",");
            WriteBool(sb, i, "Vertical", d.Vertical); sb.AppendLine(",");
            WriteInt(sb, i, "MovementType", d.MovementType); sb.AppendLine(",");
            WriteFloat(sb, i, "ScrollSensitivity", d.ScrollSensitivity); sb.AppendLine(",");
            WriteFloat(sb, i, "Elasticity", d.Elasticity); sb.AppendLine(",");
            WriteColor(sb, i, "ViewportColor", d.ViewportColor); sb.AppendLine(",");
            WriteColor(sb, i, "ContentColor", d.ContentColor);
            if (!d.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", d.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteDropdown(StringBuilder sb, UIDropdownDef d, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteInt(sb, i, "DefaultValue", d.DefaultValue); sb.AppendLine(",");
            WriteFloat(sb, i, "ItemHeight", d.ItemHeight); sb.AppendLine(",");
            WriteFloat(sb, i, "TemplateHeight", d.TemplateHeight); sb.AppendLine(",");
            WriteFloat(sb, i, "FontSize", d.FontSize); sb.AppendLine(",");
            WriteColor(sb, i, "TextColor", d.TextColor); sb.AppendLine(",");
            WriteColor(sb, i, "BackgroundColor", d.BackgroundColor); sb.AppendLine(",");
            WriteColor(sb, i, "ItemColor", d.ItemColor); sb.AppendLine(",");
            Indent(sb, i); sb.Append("\"Options\": [");
            if (d.Options != null && d.Options.Count > 0)
            {
                for (int o = 0; o < d.Options.Count; o++)
                {
                    sb.Append(Esc(d.Options[o]));
                    if (o < d.Options.Count - 1) sb.Append(",");
                }
            }
            sb.Append("]");
            if (!d.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", d.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteButton(StringBuilder sb, UIButtonDef d, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteString(sb, i, "Label", d.Label); sb.AppendLine(",");
            WriteFloat(sb, i, "FontSize", d.FontSize); sb.AppendLine(",");
            WriteColor(sb, i, "LabelColor", d.LabelColor); sb.AppendLine(",");
            WriteColor(sb, i, "NormalColor", d.NormalColor); sb.AppendLine(",");
            WriteColor(sb, i, "HighlightedColor", d.HighlightedColor); sb.AppendLine(",");
            WriteColor(sb, i, "PressedColor", d.PressedColor); sb.AppendLine(",");
            WriteColor(sb, i, "SelectedColor", d.SelectedColor); sb.AppendLine(",");
            WriteColor(sb, i, "DisabledColor", d.DisabledColor); sb.AppendLine(",");
            WriteFloat(sb, i, "FadeDuration", d.FadeDuration); sb.AppendLine(",");
            WriteInt(sb, i, "LabelAlignment", d.LabelAlignment); sb.AppendLine(",");
            WriteRectOffset(sb, i, "LabelPadding", d.LabelPadding); sb.AppendLine(",");
            WriteString(sb, i, "ClickAction", d.ClickAction); sb.AppendLine(",");
            WriteString(sb, i, "ClickActionParam", d.ClickActionParam);
            if (d.ShowLabel) { sb.AppendLine(","); WriteBool(sb, i, "ShowLabel", d.ShowLabel); }
            if (!d.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", d.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteToggle(StringBuilder sb, UIToggleDef d, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteBool(sb, i, "DefaultValue", d.DefaultValue); sb.AppendLine(",");
            WriteColor(sb, i, "CheckmarkColor", d.CheckmarkColor); sb.AppendLine(",");
            WriteColor(sb, i, "BackgroundColor", d.BackgroundColor); sb.AppendLine(",");
            WriteString(sb, i, "Label", d.Label); sb.AppendLine(",");
            WriteFloat(sb, i, "FontSize", d.FontSize); sb.AppendLine(",");
            WriteColor(sb, i, "LabelColor", d.LabelColor);
            if (!d.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", d.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteSlider(StringBuilder sb, UISliderDef d, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteFloat(sb, i, "MinValue", d.MinValue); sb.AppendLine(",");
            WriteFloat(sb, i, "MaxValue", d.MaxValue); sb.AppendLine(",");
            WriteFloat(sb, i, "DefaultValue", d.DefaultValue); sb.AppendLine(",");
            WriteBool(sb, i, "WholeNumbers", d.WholeNumbers); sb.AppendLine(",");
            WriteColor(sb, i, "BackgroundColor", d.BackgroundColor); sb.AppendLine(",");
            WriteColor(sb, i, "FillColor", d.FillColor); sb.AppendLine(",");
            WriteColor(sb, i, "HandleColor", d.HandleColor);
            if (!d.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", d.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteLayoutGroup(StringBuilder sb, UILayoutGroupDef d, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteBool(sb, i, "IsVertical", d.IsVertical); sb.AppendLine(",");
            WriteFloat(sb, i, "Spacing", d.Spacing); sb.AppendLine(",");
            WriteRectOffset(sb, i, "Padding", d.Padding); sb.AppendLine(",");
            WriteInt(sb, i, "ChildAlignment", d.ChildAlignment); sb.AppendLine(",");
            WriteBool(sb, i, "ChildControlWidth", d.ChildControlWidth); sb.AppendLine(",");
            WriteBool(sb, i, "ChildControlHeight", d.ChildControlHeight); sb.AppendLine(",");
            WriteBool(sb, i, "ChildForceExpandWidth", d.ChildForceExpandWidth); sb.AppendLine(",");
            WriteBool(sb, i, "ChildForceExpandHeight", d.ChildForceExpandHeight); sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteLayoutElement(StringBuilder sb, UILayoutElementDef d, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteFloat(sb, i, "MinWidth", d.MinWidth); sb.AppendLine(",");
            WriteFloat(sb, i, "MinHeight", d.MinHeight); sb.AppendLine(",");
            WriteFloat(sb, i, "PreferredWidth", d.PreferredWidth); sb.AppendLine(",");
            WriteFloat(sb, i, "PreferredHeight", d.PreferredHeight); sb.AppendLine(",");
            WriteFloat(sb, i, "FlexibleWidth", d.FlexibleWidth); sb.AppendLine(",");
            WriteFloat(sb, i, "FlexibleHeight", d.FlexibleHeight); sb.AppendLine(",");
            WriteBool(sb, i, "IgnoreLayout", d.IgnoreLayout); sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteContentFitter(StringBuilder sb, UIContentFitterDef d, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteInt(sb, i, "HorizontalFit", d.HorizontalFit); sb.AppendLine(",");
            WriteInt(sb, i, "VerticalFit", d.VerticalFit); sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteGridLayoutGroup(StringBuilder sb, UIGridLayoutGroupDef d, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteVec2(sb, i, "CellSize", d.CellSize); sb.AppendLine(",");
            WriteVec2(sb, i, "Spacing", d.Spacing); sb.AppendLine(",");
            WriteInt(sb, i, "StartCorner", d.StartCorner); sb.AppendLine(",");
            WriteInt(sb, i, "StartAxis", d.StartAxis); sb.AppendLine(",");
            WriteInt(sb, i, "ChildAlignment", d.ChildAlignment); sb.AppendLine(",");
            WriteInt(sb, i, "Constraint", d.Constraint); sb.AppendLine(",");
            WriteInt(sb, i, "ConstraintCount", d.ConstraintCount); sb.AppendLine(",");
            WriteRectOffset(sb, i, "Padding", d.Padding); sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private delegate void WriteDelegate<T>(StringBuilder sb, T data, int indent);

        private static void WriteOptional<T>(StringBuilder sb, int indent, string key, T data, WriteDelegate<T> writer) where T : class
        {
            Indent(sb, indent);
            sb.Append($"\"{key}\": ");
            if (data == null) { sb.Append("null"); return; }
            writer(sb, data, indent);
        }

        // ???????????????????????????????????????
        //  Primitive writers
        // ???????????????????????????????????????

        private static void WriteString(StringBuilder sb, int indent, string key, string val)
        {
            Indent(sb, indent);
            sb.Append($"\"{key}\": {Esc(val ?? "")}");
        }

        private static void WriteInt(StringBuilder sb, int indent, string key, int val)
        {
            Indent(sb, indent);
            sb.Append($"\"{key}\": {val}");
        }

        private static void WriteLong(StringBuilder sb, int indent, string key, long val)
        {
            Indent(sb, indent);
            sb.Append($"\"{key}\": {val}");
        }

        private static void WriteFloat(StringBuilder sb, int indent, string key, float val)
        {
            Indent(sb, indent);
            sb.Append($"\"{key}\": {F(val)}");
        }

        private static void WriteBool(StringBuilder sb, int indent, string key, bool val)
        {
            Indent(sb, indent);
            sb.Append($"\"{key}\": {(val ? "true" : "false")}");
        }

        private static void WriteVec2(StringBuilder sb, int indent, string key, Vector2Ser v)
        {
            Indent(sb, indent);
            sb.Append($"\"{key}\": {{\"X\":{F(v.X)},\"Y\":{F(v.Y)}}}");
        }

        private static void WriteColor(StringBuilder sb, int indent, string key, ColorSer c)
        {
            Indent(sb, indent);
            sb.Append($"\"{key}\": {{\"R\":{F(c.R)},\"G\":{F(c.G)},\"B\":{F(c.B)},\"A\":{F(c.A)}}}");
        }

        private static void WriteRectOffset(StringBuilder sb, int indent, string key, RectOffsetSer o)
        {
            Indent(sb, indent);
            sb.Append($"\"{key}\": {{\"Left\":{o.Left},\"Right\":{o.Right},\"Top\":{o.Top},\"Bottom\":{o.Bottom}}}");
        }

        private static void Indent(StringBuilder sb, int level)
        {
            for (int i = 0; i < level; i++) sb.Append("  ");
        }

        private static string F(float val)
        {
            // JSON has no NaN/Infinity literal - emitting "NaN" produces invalid JSON that the reader
            // chokes on ("Expected '}' got 'N'"). Sanitize to 0 so every dump stays parseable.
            if (float.IsNaN(val) || float.IsInfinity(val)) return "0";
            return val.ToString("G9", CultureInfo.InvariantCulture);
        }

        private static string Esc(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // ???????????????????????????????????????
        //  JSON Parser (minimal, recursive-descent)
        // ???????????????????????????????????????

        private static Dictionary<string, object> ParseObject(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);
            Expect(json, ref pos, '{');
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            SkipWhitespace(json, ref pos);
            if (pos < json.Length && json[pos] == '}') { pos++; return dict; }
            while (pos < json.Length)
            {
                SkipWhitespace(json, ref pos);
                string key = ParseString(json, ref pos);
                SkipWhitespace(json, ref pos);
                Expect(json, ref pos, ':');
                SkipWhitespace(json, ref pos);
                object val = ParseValue(json, ref pos);
                dict[key] = val;
                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
                break;
            }
            SkipWhitespace(json, ref pos);
            Expect(json, ref pos, '}');
            return dict;
        }

        private static List<object> ParseArray(string json, ref int pos)
        {
            Expect(json, ref pos, '[');
            var list = new List<object>();
            SkipWhitespace(json, ref pos);
            if (pos < json.Length && json[pos] == ']') { pos++; return list; }
            while (pos < json.Length)
            {
                SkipWhitespace(json, ref pos);
                list.Add(ParseValue(json, ref pos));
                SkipWhitespace(json, ref pos);
                if (pos < json.Length && json[pos] == ',') { pos++; continue; }
                break;
            }
            SkipWhitespace(json, ref pos);
            Expect(json, ref pos, ']');
            return list;
        }

        private static object ParseValue(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);
            if (pos >= json.Length) return null;
            char c = json[pos];
            if (c == '{') return ParseObject(json, ref pos);
            if (c == '[') return ParseArray(json, ref pos);
            if (c == '"') return ParseString(json, ref pos);
            if (c == 'n') { pos += 4; return null; }
            if (c == 't') { pos += 4; return true; }
            if (c == 'f') { pos += 5; return false; }
            // Tolerate invalid NaN/Infinity tokens left by older dumps (consume them so we don't stall).
            if (c == 'N') { pos += 3; return 0d; }                                                   // NaN
            if (c == 'I') { pos += 8; return 0d; }                                                   // Infinity
            if (c == '-' && pos + 1 < json.Length && json[pos + 1] == 'I') { pos += 9; return 0d; }  // -Infinity
            return ParseNumber(json, ref pos);
        }

        private static string ParseString(string json, ref int pos)
        {
            Expect(json, ref pos, '"');
            var sb = new StringBuilder();
            while (pos < json.Length)
            {
                char c = json[pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\' && pos < json.Length)
                {
                    char n = json[pos++];
                    switch (n)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(n); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static double ParseNumber(string json, ref int pos)
        {
            int start = pos;
            while (pos < json.Length && "0123456789.eE+-".IndexOf(json[pos]) >= 0) pos++;
            if (pos == start) { pos++; return 0d; }   // safety: never stall on an unexpected char
            string num = json.Substring(start, pos - start);
            double.TryParse(num, NumberStyles.Any, CultureInfo.InvariantCulture, out double val);
            return val;
        }

        private static void SkipWhitespace(string json, ref int pos)
        {
            while (pos < json.Length && char.IsWhiteSpace(json[pos])) pos++;
        }

        private static void Expect(string json, ref int pos, char expected)
        {
            if (pos < json.Length && json[pos] == expected) { pos++; return; }
            throw new FormatException($"Expected '{expected}' at position {pos}, got '{(pos < json.Length ? json[pos] : '?')}'");
        }

        // ???????????????????????????????????????
        //  Object readers (parsed dict ? typed)
        // ???????????????????????????????????????

        private static UILayoutDefinition ReadLayout(Dictionary<string, object> d)
        {
            var layout = new UILayoutDefinition();
            layout.UID = GetStr(d, "UID");
            layout.DisplayName = GetStr(d, "DisplayName");
            layout.Category = GetStr(d, "Category", "Custom");
            layout.Author = GetStr(d, "Author");
            layout.CreatedTimestamp = GetLong(d, "CreatedTimestamp");
            layout.ModifiedTimestamp = GetLong(d, "ModifiedTimestamp");
            layout.Version = GetInt(d, "Version", 1);
            layout.CanvasSize = GetVec2(d, "CanvasSize", new Vector2Ser(1920, 1080));

            if (d.ContainsKey("Metadata") && d["Metadata"] is List<object> metaList)
            {
                layout.Metadata = new List<MetadataEntry>();
                foreach (var item in metaList)
                {
                    if (item is Dictionary<string, object> md)
                        layout.Metadata.Add(new MetadataEntry { Key = GetStr(md, "Key"), Value = GetStr(md, "Value") });
                }
            }

            // Canvas scaler
            if (d.ContainsKey("CanvasScaler") && d["CanvasScaler"] is Dictionary<string, object> csd)
                layout.CanvasScaler = ReadCanvasScaler(csd);

            // Screen position and scale (preview layout mode)
            if (d.ContainsKey("ScreenPositionX") && d.ContainsKey("ScreenPositionY"))
            {
                layout.ScreenPositionX = GetFloat(d, "ScreenPositionX", float.NaN);
                layout.ScreenPositionY = GetFloat(d, "ScreenPositionY", float.NaN);
            }
            if (d.ContainsKey("ScreenScale"))
            {
                layout.ScreenScale = GetFloat(d, "ScreenScale", 1f);
            }

            // RuntimeRootTransform (preserved original root before editor normalization)
            if (d.ContainsKey("RuntimeRootTransform") && d["RuntimeRootTransform"] is Dictionary<string, object> rrtd)
                layout.RuntimeRootTransform = ReadRuntimeRootTransform(rrtd);

            if (d.ContainsKey("RootElement") && d["RootElement"] is Dictionary<string, object> rootDict)
                layout.RootElement = ReadNode(rootDict);

            return layout;
        }

        private static UIElementNode ReadNode(Dictionary<string, object> d)
        {
            var n = new UIElementNode();
            n.Id = GetStr(d, "Id");
            n.Name = GetStr(d, "Name");
            n.Tag = GetStr(d, "Tag");

            string typeStr = GetStr(d, "Type", "Panel");
            if (Enum.TryParse(typeStr, true, out UIElementType et)) n.Type = et;

            n.AnchorMin = GetVec2(d, "AnchorMin");
            n.AnchorMax = GetVec2(d, "AnchorMax", new Vector2Ser(1, 1));
            n.Pivot = GetVec2(d, "Pivot", new Vector2Ser(0.5f, 0.5f));
            n.AnchoredPosition = GetVec2(d, "AnchoredPosition");
            n.SizeDelta = GetVec2(d, "SizeDelta");
            n.OffsetMin = GetVec2(d, "OffsetMin");
            n.OffsetMax = GetVec2(d, "OffsetMax");
            n.Rotation = GetFloat(d, "Rotation");
            n.Active = GetBool(d, "Active", true);
            n.Interactable = GetBool(d, "Interactable", true);
            n.HasMask = GetBool(d, "HasMask");
            n.HasRectMask2D = GetBool(d, "HasRectMask2D");
            n.TransformLocked = GetBool(d, "TransformLocked");

            // Per-element metadata
            if (d.ContainsKey("Metadata") && d["Metadata"] is List<object> nodeMetaList)
            {
                n.Metadata = new List<MetadataEntry>();
                foreach (var item in nodeMetaList)
                {
                    if (item is Dictionary<string, object> md)
                        n.Metadata.Add(new MetadataEntry { Key = GetStr(md, "Key"), Value = GetStr(md, "Value") });
                }
            }

            if (d.ContainsKey("Style") && d["Style"] is Dictionary<string, object> sd)
                n.Style = ReadStyle(sd);

            if (d.ContainsKey("LayoutGroup") && d["LayoutGroup"] is Dictionary<string, object> lgd)
                n.LayoutGroup = ReadLayoutGroup(lgd);
            if (d.ContainsKey("GridLayoutGroup") && d["GridLayoutGroup"] is Dictionary<string, object> glgd)
                n.GridLayoutGroup = ReadGridLayoutGroup(glgd);
            if (d.ContainsKey("LayoutElement") && d["LayoutElement"] is Dictionary<string, object> led)
                n.LayoutElement = ReadLayoutElement(led);
            if (d.ContainsKey("ContentFitter") && d["ContentFitter"] is Dictionary<string, object> cfd)
                n.ContentFitter = ReadContentFitter(cfd);
            if (d.ContainsKey("AspectRatioFitter") && d["AspectRatioFitter"] is Dictionary<string, object> arfd)
                n.AspectRatioFitter = ReadAspectRatioFitter(arfd);

            if (d.ContainsKey("TextData") && d["TextData"] is Dictionary<string, object> td)
                n.TextData = ReadText(td);
            if (d.ContainsKey("ImageData") && d["ImageData"] is Dictionary<string, object> id2)
                n.ImageData = ReadImage(id2);
            if (d.ContainsKey("InputFieldData") && d["InputFieldData"] is Dictionary<string, object> ifd)
                n.InputFieldData = ReadInputField(ifd);
            if (d.ContainsKey("ScrollViewData") && d["ScrollViewData"] is Dictionary<string, object> svd)
                n.ScrollViewData = ReadScrollView(svd);
            if (d.ContainsKey("DropdownData") && d["DropdownData"] is Dictionary<string, object> ddd)
                n.DropdownData = ReadDropdown(ddd);
            if (d.ContainsKey("ButtonData") && d["ButtonData"] is Dictionary<string, object> bd)
                n.ButtonData = ReadButton(bd);
            if (d.ContainsKey("ToggleData") && d["ToggleData"] is Dictionary<string, object> togd)
                n.ToggleData = ReadToggle(togd);
            if (d.ContainsKey("SliderData") && d["SliderData"] is Dictionary<string, object> sld)
                n.SliderData = ReadSlider(sld);

            if (d.ContainsKey("RenderCameraData") && d["RenderCameraData"] is Dictionary<string, object> rcd)
                n.RenderCameraData = ReadRenderCamera(rcd);

            if (d.ContainsKey("Children") && d["Children"] is List<object> children)
            {
                n.Children = new List<UIElementNode>();
                foreach (var child in children)
                    if (child is Dictionary<string, object> cd)
                        n.Children.Add(ReadNode(cd));
            }

            return n;
        }

        private static UIElementStyle ReadStyle(Dictionary<string, object> d)
        {
            var s = new UIElementStyle();
            s.BackgroundColor = GetColor(d, "BackgroundColor", s.BackgroundColor);
            s.BorderColor = GetColor(d, "BorderColor", s.BorderColor);
            s.BorderWidth = GetFloat(d, "BorderWidth");
            s.CornerRadius = GetFloat(d, "CornerRadius");
            s.Opacity = GetFloat(d, "Opacity", 1f);
            s.RaycastTarget = GetBool(d, "RaycastTarget", true);
            string shapeStr = GetStr(d, "Shape", "Rectangle");
            if (Enum.TryParse(shapeStr, true, out UIShapeType st)) s.Shape = st;
            s.BackgroundSprite = GetStr(d, "BackgroundSprite");
            s.ImageType = GetInt(d, "ImageType");
            return s;
        }

        private static UITextDef ReadText(Dictionary<string, object> d)
        {
            var t = new UITextDef();
            t.Text = GetStr(d, "Text");
            t.FontSize = GetFloat(d, "FontSize", 16f);
            t.FontCategory = GetStr(d, "FontCategory", "Body");
            t.FontStyle = GetInt(d, "FontStyle");
            t.Color = GetColor(d, "Color", t.Color);
            t.Alignment = GetInt(d, "Alignment", (int)TMPro.TextAlignmentOptions.MidlineLeft);
            t.WordWrap = GetBool(d, "WordWrap", true);
            t.OverflowMode = GetInt(d, "OverflowMode");
            t.LineSpacing = GetFloat(d, "LineSpacing");
            t.TextPadding = GetRectOffset(d, "TextPadding");
            t.ComponentEnabled = GetBool(d, "ComponentEnabled", true);
            return t;
        }

        private static UIImageDef ReadImage(Dictionary<string, object> d)
        {
            var img = new UIImageDef();
            img.SpriteName = GetStr(d, "SpriteName");
            img.ImageType = GetInt(d, "ImageType");
            img.PreserveAspect = GetBool(d, "PreserveAspect");
            img.Color = GetColor(d, "Color", ColorSer.White);
            img.FillCenter = GetBool(d, "FillCenter", true);
            img.PixelsPerUnit = GetFloat(d, "PixelsPerUnit", 100f);
            img.ComponentEnabled = GetBool(d, "ComponentEnabled", true);
            return img;
        }

        private static UIInputFieldDef ReadInputField(Dictionary<string, object> d)
        {
            var inf = new UIInputFieldDef();
            inf.PlaceholderText = GetStr(d, "PlaceholderText");
            inf.FontSize = GetFloat(d, "FontSize", 13f);
            inf.Multiline = GetBool(d, "Multiline");
            inf.CharacterLimit = GetInt(d, "CharacterLimit");
            inf.TextColor = GetColor(d, "TextColor", inf.TextColor);
            inf.PlaceholderColor = GetColor(d, "PlaceholderColor", inf.PlaceholderColor);
            inf.BackgroundColor = GetColor(d, "BackgroundColor", inf.BackgroundColor);
            inf.CaretColor = GetColor(d, "CaretColor", inf.CaretColor);
            inf.SelectionColor = GetColor(d, "SelectionColor", inf.SelectionColor);
            inf.ContentType = GetStr(d, "ContentType", "Standard");
            inf.TextAlignment = GetInt(d, "TextAlignment", (int)TMPro.TextAlignmentOptions.MidlineLeft);
            inf.TextPadding = GetRectOffset(d, "TextPadding");
            inf.ComponentEnabled = GetBool(d, "ComponentEnabled", true);
            return inf;
        }

        private static UIScrollViewDef ReadScrollView(Dictionary<string, object> d)
        {
            var sv = new UIScrollViewDef();
            sv.Horizontal = GetBool(d, "Horizontal");
            sv.Vertical = GetBool(d, "Vertical", true);
            sv.MovementType = GetInt(d, "MovementType", 2);
            sv.ScrollSensitivity = GetFloat(d, "ScrollSensitivity", 30f);
            sv.Elasticity = GetFloat(d, "Elasticity", 0.1f);
            sv.ViewportColor = GetColor(d, "ViewportColor", sv.ViewportColor);
            sv.ContentColor = GetColor(d, "ContentColor", sv.ContentColor);
            sv.ComponentEnabled = GetBool(d, "ComponentEnabled", true);
            return sv;
        }

        private static UIDropdownDef ReadDropdown(Dictionary<string, object> d)
        {
            var dd = new UIDropdownDef();
            dd.DefaultValue = GetInt(d, "DefaultValue");
            dd.ItemHeight = GetFloat(d, "ItemHeight", 24f);
            dd.TemplateHeight = GetFloat(d, "TemplateHeight", 160f);
            dd.FontSize = GetFloat(d, "FontSize", 13f);
            dd.TextColor = GetColor(d, "TextColor", dd.TextColor);
            dd.BackgroundColor = GetColor(d, "BackgroundColor", dd.BackgroundColor);
            dd.ItemColor = GetColor(d, "ItemColor", dd.ItemColor);
            if (d.ContainsKey("Options") && d["Options"] is List<object> opts)
            {
                dd.Options = new List<string>();
                foreach (var o in opts) dd.Options.Add(o?.ToString() ?? "");
            }
            dd.ComponentEnabled = GetBool(d, "ComponentEnabled", true);
            return dd;
        }

        private static UIButtonDef ReadButton(Dictionary<string, object> d)
        {
            var b = new UIButtonDef();
            b.Label = GetStr(d, "Label", "Button");
            b.FontSize = GetFloat(d, "FontSize", 13f);
            b.LabelColor = GetColor(d, "LabelColor", b.LabelColor);
            b.NormalColor = GetColor(d, "NormalColor", b.NormalColor);
            b.HighlightedColor = GetColor(d, "HighlightedColor", b.HighlightedColor);
            b.PressedColor = GetColor(d, "PressedColor", b.PressedColor);
            b.SelectedColor = GetColor(d, "SelectedColor", b.SelectedColor);
            b.DisabledColor = GetColor(d, "DisabledColor", b.DisabledColor);
            b.FadeDuration = GetFloat(d, "FadeDuration", 0.08f);
            b.LabelAlignment = GetInt(d, "LabelAlignment", (int)TMPro.TextAlignmentOptions.Center);
            b.LabelPadding = GetRectOffset(d, "LabelPadding");
            b.ClickAction = GetStr(d, "ClickAction");
            b.ClickActionParam = GetStr(d, "ClickActionParam");
            b.ShowLabel = GetBool(d, "ShowLabel");
            b.ComponentEnabled = GetBool(d, "ComponentEnabled", true);
            return b;
        }

        private static UIToggleDef ReadToggle(Dictionary<string, object> d)
        {
            var t = new UIToggleDef();
            t.DefaultValue = GetBool(d, "DefaultValue");
            t.CheckmarkColor = GetColor(d, "CheckmarkColor", t.CheckmarkColor);
            t.BackgroundColor = GetColor(d, "BackgroundColor", t.BackgroundColor);
            t.Label = GetStr(d, "Label");
            t.FontSize = GetFloat(d, "FontSize", 13f);
            t.LabelColor = GetColor(d, "LabelColor", t.LabelColor);
            t.ComponentEnabled = GetBool(d, "ComponentEnabled", true);
            return t;
        }

        private static UISliderDef ReadSlider(Dictionary<string, object> d)
        {
            var s = new UISliderDef();
            s.MinValue = GetFloat(d, "MinValue");
            s.MaxValue = GetFloat(d, "MaxValue", 1f);
            s.DefaultValue = GetFloat(d, "DefaultValue");
            s.WholeNumbers = GetBool(d, "WholeNumbers");
            s.BackgroundColor = GetColor(d, "BackgroundColor", s.BackgroundColor);
            s.FillColor = GetColor(d, "FillColor", s.FillColor);
            s.HandleColor = GetColor(d, "HandleColor", s.HandleColor);
            s.ComponentEnabled = GetBool(d, "ComponentEnabled", true);
            return s;
        }

        private static void WriteRenderCamera(StringBuilder sb, UIRenderCameraDef d, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteString(sb, i, "Mode", d.Mode); sb.AppendLine(",");
            WriteString(sb, i, "PrefabName", d.PrefabName); sb.AppendLine(",");
            WriteFloat(sb, i, "FieldOfView", d.FieldOfView); sb.AppendLine(",");
            WriteColor(sb, i, "BackgroundColor", d.BackgroundColor); sb.AppendLine(",");
            WriteInt(sb, i, "TextureWidth", d.TextureWidth); sb.AppendLine(",");
            WriteInt(sb, i, "TextureHeight", d.TextureHeight); sb.AppendLine(",");
            WriteVec2(sb, i, "CameraOffset", d.CameraOffset); sb.AppendLine(",");
            WriteFloat(sb, i, "CameraDistance", d.CameraDistance); sb.AppendLine(",");
            WriteFloat(sb, i, "CameraRotationY", d.CameraRotationY); sb.AppendLine(",");
            WriteFloat(sb, i, "CameraRotationX", d.CameraRotationX); sb.AppendLine(",");
            WriteString(sb, i, "LayerName", d.LayerName);
            if (!d.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", d.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static UIRenderCameraDef ReadRenderCamera(Dictionary<string, object> d)
        {
            var rc = new UIRenderCameraDef();
            rc.Mode = GetStr(d, "Mode", "player");
            rc.PrefabName = GetStr(d, "PrefabName", "");
            rc.FieldOfView = GetFloat(d, "FieldOfView", 30f);
            rc.BackgroundColor = GetColor(d, "BackgroundColor", rc.BackgroundColor);
            rc.TextureWidth = GetInt(d, "TextureWidth", 256);
            rc.TextureHeight = GetInt(d, "TextureHeight", 256);
            rc.CameraOffset = GetVec2(d, "CameraOffset");
            rc.CameraDistance = GetFloat(d, "CameraDistance", 3f);
            rc.CameraRotationY = GetFloat(d, "CameraRotationY");
            rc.CameraRotationX = GetFloat(d, "CameraRotationX", 10f);
            rc.LayerName = GetStr(d, "LayerName", "");
            rc.ComponentEnabled = GetBool(d, "ComponentEnabled", true);
            return rc;
        }

        private static UILayoutGroupDef ReadLayoutGroup(Dictionary<string, object> d)
        {
            var lg = new UILayoutGroupDef();
            lg.IsVertical = GetBool(d, "IsVertical", true);
            lg.Spacing = GetFloat(d, "Spacing");
            lg.Padding = GetRectOffset(d, "Padding");
            lg.ChildAlignment = GetInt(d, "ChildAlignment");
            lg.ChildControlWidth = GetBool(d, "ChildControlWidth", true);
            lg.ChildControlHeight = GetBool(d, "ChildControlHeight");
            lg.ChildForceExpandWidth = GetBool(d, "ChildForceExpandWidth", true);
            lg.ChildForceExpandHeight = GetBool(d, "ChildForceExpandHeight");
            return lg;
        }

        private static UILayoutElementDef ReadLayoutElement(Dictionary<string, object> d)
        {
            var le = new UILayoutElementDef();
            le.MinWidth = GetFloat(d, "MinWidth", -1);
            le.MinHeight = GetFloat(d, "MinHeight", -1);
            le.PreferredWidth = GetFloat(d, "PreferredWidth", -1);
            le.PreferredHeight = GetFloat(d, "PreferredHeight", -1);
            le.FlexibleWidth = GetFloat(d, "FlexibleWidth", -1);
            le.FlexibleHeight = GetFloat(d, "FlexibleHeight", -1);
            le.IgnoreLayout = GetBool(d, "IgnoreLayout");
            return le;
        }

        private static UIContentFitterDef ReadContentFitter(Dictionary<string, object> d)
        {
            var cf = new UIContentFitterDef();
            cf.HorizontalFit = GetInt(d, "HorizontalFit");
            cf.VerticalFit = GetInt(d, "VerticalFit");
            return cf;
        }

        private static UIGridLayoutGroupDef ReadGridLayoutGroup(Dictionary<string, object> d)
        {
            var g = new UIGridLayoutGroupDef();
            g.CellSize = GetVec2(d, "CellSize", new Vector2Ser(64, 64));
            g.Spacing = GetVec2(d, "Spacing", new Vector2Ser(0, 0));
            g.StartCorner = GetInt(d, "StartCorner");
            g.StartAxis = GetInt(d, "StartAxis");
            g.ChildAlignment = GetInt(d, "ChildAlignment");
            g.Constraint = GetInt(d, "Constraint");
            g.ConstraintCount = GetInt(d, "ConstraintCount", 2);
            g.Padding = GetRectOffset(d, "Padding");
            return g;
        }

        private static void WriteCanvasScaler(StringBuilder sb, UICanvasScalerDef d, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteInt(sb, i, "ScaleMode", d.ScaleMode); sb.AppendLine(",");
            WriteVec2(sb, i, "ReferenceResolution", d.ReferenceResolution); sb.AppendLine(",");
            WriteFloat(sb, i, "MatchWidthOrHeight", d.MatchWidthOrHeight); sb.AppendLine(",");
            WriteFloat(sb, i, "ReferencePixelsPerUnit", d.ReferencePixelsPerUnit); sb.AppendLine(",");
            WriteFloat(sb, i, "ScaleFactor", d.ScaleFactor); sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static UICanvasScalerDef ReadCanvasScaler(Dictionary<string, object> d)
        {
            var cs = new UICanvasScalerDef();
            cs.ScaleMode = GetInt(d, "ScaleMode", 1);
            cs.ReferenceResolution = GetVec2(d, "ReferenceResolution", new Vector2Ser(1920, 1080));
            cs.MatchWidthOrHeight = GetFloat(d, "MatchWidthOrHeight", 0.5f);
            cs.ReferencePixelsPerUnit = GetFloat(d, "ReferencePixelsPerUnit", 100f);
            cs.ScaleFactor = GetFloat(d, "ScaleFactor", 1f);
            return cs;
        }

        private static void WriteAspectRatioFitter(StringBuilder sb, UIAspectRatioFitterDef d, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteInt(sb, i, "AspectMode", d.AspectMode); sb.AppendLine(",");
            WriteFloat(sb, i, "AspectRatio", d.AspectRatio); sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static UIAspectRatioFitterDef ReadAspectRatioFitter(Dictionary<string, object> d)
        {
            var ar = new UIAspectRatioFitterDef();
            ar.AspectMode = GetInt(d, "AspectMode", 1);
            ar.AspectRatio = GetFloat(d, "AspectRatio", 1f);
            return ar;
        }

        private static void WriteRuntimeRootTransform(StringBuilder sb, RuntimeRootTransformDef d, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteVec2(sb, i, "AnchorMin", d.AnchorMin); sb.AppendLine(",");
            WriteVec2(sb, i, "AnchorMax", d.AnchorMax); sb.AppendLine(",");
            WriteVec2(sb, i, "Pivot", d.Pivot); sb.AppendLine(",");
            WriteVec2(sb, i, "AnchoredPosition", d.AnchoredPosition); sb.AppendLine(",");
            WriteVec2(sb, i, "SizeDelta", d.SizeDelta); sb.AppendLine(",");
            WriteVec2(sb, i, "OffsetMin", d.OffsetMin); sb.AppendLine(",");
            WriteVec2(sb, i, "OffsetMax", d.OffsetMax); sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static RuntimeRootTransformDef ReadRuntimeRootTransform(Dictionary<string, object> d)
        {
            var rrt = new RuntimeRootTransformDef();
            rrt.AnchorMin = GetVec2(d, "AnchorMin");
            rrt.AnchorMax = GetVec2(d, "AnchorMax", new Vector2Ser(1, 1));
            rrt.Pivot = GetVec2(d, "Pivot", new Vector2Ser(0.5f, 0.5f));
            rrt.AnchoredPosition = GetVec2(d, "AnchoredPosition");
            rrt.SizeDelta = GetVec2(d, "SizeDelta");
            rrt.OffsetMin = GetVec2(d, "OffsetMin");
            rrt.OffsetMax = GetVec2(d, "OffsetMax");
            return rrt;
        }

        // ???????????????????????????????????????
        //  Typed getters from parsed dict
        // ???????????????????????????????????????

        private static string GetStr(Dictionary<string, object> d, string key, string def = "")
        {
            if (d.TryGetValue(key, out object v) && v is string s) return s;
            return def;
        }

        private static int GetInt(Dictionary<string, object> d, string key, int def = 0)
        {
            if (d.TryGetValue(key, out object v))
            {
                if (v is double dv) return (int)dv;
                if (v is int iv) return iv;
            }
            return def;
        }

        private static long GetLong(Dictionary<string, object> d, string key, long def = 0)
        {
            if (d.TryGetValue(key, out object v))
            {
                if (v is double dv) return (long)dv;
            }
            return def;
        }

        private static float GetFloat(Dictionary<string, object> d, string key, float def = 0)
        {
            if (d.TryGetValue(key, out object v))
            {
                if (v is double dv) return (float)dv;
            }
            return def;
        }

        private static bool GetBool(Dictionary<string, object> d, string key, bool def = false)
        {
            if (d.TryGetValue(key, out object v))
            {
                if (v is bool bv) return bv;
            }
            return def;
        }

        private static Vector2Ser GetVec2(Dictionary<string, object> d, string key, Vector2Ser def = default)
        {
            if (d.TryGetValue(key, out object v) && v is Dictionary<string, object> vd)
                return new Vector2Ser(GetFloat(vd, "X"), GetFloat(vd, "Y"));
            return def;
        }

        private static ColorSer GetColor(Dictionary<string, object> d, string key, ColorSer def = default)
        {
            if (d.TryGetValue(key, out object v) && v is Dictionary<string, object> cd)
                return new ColorSer(GetFloat(cd, "R"), GetFloat(cd, "G"), GetFloat(cd, "B"), GetFloat(cd, "A", 1f));
            return def;
        }

        private static RectOffsetSer GetRectOffset(Dictionary<string, object> d, string key)
        {
            if (d.TryGetValue(key, out object v) && v is Dictionary<string, object> rd)
                return new RectOffsetSer(GetInt(rd, "Left"), GetInt(rd, "Right"), GetInt(rd, "Top"), GetInt(rd, "Bottom"));
            return new RectOffsetSer();
        }
    }
}
