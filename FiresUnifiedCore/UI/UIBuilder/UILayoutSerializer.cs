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
            get { return Path.Combine(FiresCore.Storage.FiresConfigPaths.UiLayouts); }
        }

        //  Public API

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

        // --- Self-write suppression -----------------------------------------------------------------
        // A server-side file watcher (in consuming mods) reloads ALL layouts on any UILayouts change. The
        // local capture writes layouts itself AND updates the codex in-memory (UILayoutCodex.AddOrReplace),
        // so the watcher reloading on OUR OWN writes is pure overhead and causes a reload cascade. We record
        // every path we write so the watcher can skip it. Thread-safe (the watcher fires off-thread).
        private static readonly Dictionary<string, DateTime> _selfWrites =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _selfWriteLock = new object();
        private const double SelfWriteExpirySeconds = 8.0;

        private static string NormPath(string path)
        {
            try { return Path.GetFullPath(path); } catch { return path; }
        }

        /// <summary>Record that we just wrote this file, so a file watcher can ignore the change.</summary>
        public static void MarkSelfWritten(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return;
            lock (_selfWriteLock) { _selfWrites[NormPath(fullPath)] = DateTime.UtcNow; }
        }

        /// <summary>True if WE wrote this file within the last few seconds (so a watcher should skip it).</summary>
        public static bool WasRecentlySelfWritten(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            string key = NormPath(fullPath);
            lock (_selfWriteLock)
            {
                if (_selfWrites.TryGetValue(key, out var writtenAt))
                {
                    if ((DateTime.UtcNow - writtenAt).TotalSeconds < SelfWriteExpirySeconds) return true;
                    _selfWrites.Remove(key);
                }
                return false;
            }
        }

        public static void SaveToFile(UILayoutDefinition layout, string relativePath)
        {
            if (layout == null || string.IsNullOrEmpty(relativePath)) return;
            string fullPath = Path.Combine(LayoutsDir, relativePath);
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            MarkSelfWritten(fullPath);
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
            MarkSelfWritten(fullPath);
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

        //  JSON Writer

        private static void WriteLayout(StringBuilder sb, UILayoutDefinition layout, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteString(sb, i, "UID", layout.UID); sb.AppendLine(",");
            WriteString(sb, i, "DisplayName", layout.DisplayName); sb.AppendLine(",");
            WriteString(sb, i, "Category", layout.Category); sb.AppendLine(",");
            WriteString(sb, i, "Author", layout.Author); sb.AppendLine(",");
            WriteLong(sb, i, "CreatedTimestamp", layout.CreatedTimestamp); sb.AppendLine(",");
            WriteLong(sb, i, "ModifiedTimestamp", layout.ModifiedTimestamp); sb.AppendLine(",");
            WriteInt(sb, i, "Version", layout.Version); sb.AppendLine(",");
            WriteVec2(sb, i, "CanvasSize", layout.CanvasSize); sb.AppendLine(",");

            // Metadata
            Indent(sb, i); sb.Append("\"Metadata\": [");
            if (layout.Metadata != null && layout.Metadata.Count > 0)
            {
                sb.AppendLine();
                for (int metaIndex = 0; metaIndex < layout.Metadata.Count; metaIndex++)
                {
                    Indent(sb, i + 1);
                    sb.Append("{");
                    sb.Append($"\"Key\":{Esc(layout.Metadata[metaIndex].Key)},\"Value\":{Esc(layout.Metadata[metaIndex].Value)}");
                    sb.Append("}");
                    if (metaIndex < layout.Metadata.Count - 1) sb.Append(",");
                    sb.AppendLine();
                }
                Indent(sb, i);
            }
            sb.AppendLine("],");

            // Canvas scaler
            WriteOptional(sb, i, "CanvasScaler", layout.CanvasScaler, WriteCanvasScaler); sb.AppendLine(",");

            // Screen position and scale (preview layout mode)
            if (!float.IsNaN(layout.ScreenPositionX) && !float.IsNaN(layout.ScreenPositionY))
            {
                WriteFloat(sb, i, "ScreenPositionX", layout.ScreenPositionX); sb.AppendLine(",");
                WriteFloat(sb, i, "ScreenPositionY", layout.ScreenPositionY); sb.AppendLine(",");
            }
            if (!Mathf.Approximately(layout.ScreenScale, 1f))
            {
                WriteFloat(sb, i, "ScreenScale", layout.ScreenScale); sb.AppendLine(",");
            }

            // RuntimeRootTransform (preserved original root before editor normalization)
            if (layout.RuntimeRootTransform != null)
            {
                WriteOptional(sb, i, "RuntimeRootTransform", layout.RuntimeRootTransform, WriteRuntimeRootTransform); sb.AppendLine(",");
            }

            // Root element
            Indent(sb, i); sb.AppendLine("\"RootElement\":");
            if (layout.RootElement != null)
                WriteNode(sb, layout.RootElement, i);
            else
            { Indent(sb, i); sb.Append("null"); }

            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteNode(StringBuilder sb, UIElementNode node, int indent)
        {
            Indent(sb, indent); sb.AppendLine("{");
            int i = indent + 1;

            // Identity
            WriteString(sb, i, "Id", node.Id); sb.AppendLine(",");
            WriteString(sb, i, "Name", node.Name); sb.AppendLine(",");
            if (!string.IsNullOrEmpty(node.Tag)) { WriteString(sb, i, "Tag", node.Tag); sb.AppendLine(","); }
            WriteString(sb, i, "Type", node.Type.ToString()); sb.AppendLine(",");

            // Transform
            WriteVec2(sb, i, "AnchorMin", node.AnchorMin); sb.AppendLine(",");
            WriteVec2(sb, i, "AnchorMax", node.AnchorMax); sb.AppendLine(",");
            WriteVec2(sb, i, "Pivot", node.Pivot); sb.AppendLine(",");
            WriteVec2(sb, i, "AnchoredPosition", node.AnchoredPosition); sb.AppendLine(",");
            WriteVec2(sb, i, "SizeDelta", node.SizeDelta); sb.AppendLine(",");
            WriteVec2(sb, i, "OffsetMin", node.OffsetMin); sb.AppendLine(",");
            WriteVec2(sb, i, "OffsetMax", node.OffsetMax); sb.AppendLine(",");
            if (node.Rotation != 0) { WriteFloat(sb, i, "Rotation", node.Rotation); sb.AppendLine(","); }

            // State - only write non-defaults
            if (!node.Active) { WriteBool(sb, i, "Active", node.Active); sb.AppendLine(","); }
            if (!node.Interactable) { WriteBool(sb, i, "Interactable", node.Interactable); sb.AppendLine(","); }
            if (node.HasMask) { WriteBool(sb, i, "HasMask", node.HasMask); sb.AppendLine(","); }
            if (node.HasRectMask2D) { WriteBool(sb, i, "HasRectMask2D", node.HasRectMask2D); sb.AppendLine(","); }
            if (node.TransformLocked) { WriteBool(sb, i, "TransformLocked", node.TransformLocked); sb.AppendLine(","); }

            // Per-element metadata - only if non-empty
            if (node.Metadata != null && node.Metadata.Count > 0)
            {
                Indent(sb, i); sb.Append("\"Metadata\": [");
                sb.AppendLine();
                for (int metaIndex = 0; metaIndex < node.Metadata.Count; metaIndex++)
                {
                    Indent(sb, i + 1);
                    sb.Append("{");
                    sb.Append($"\"Key\":{Esc(node.Metadata[metaIndex].Key)},\"Value\":{Esc(node.Metadata[metaIndex].Value)}");
                    sb.Append("}");
                    if (metaIndex < node.Metadata.Count - 1) sb.Append(",");
                    sb.AppendLine();
                }
                Indent(sb, i);
                sb.AppendLine("],");
            }

            // Style
            if (node.Style != null)
            {
                Indent(sb, i); sb.Append("\"Style\": ");
                WriteStyle(sb, node.Style, i); sb.AppendLine(",");
            }

            // Optional components - SKIP null entries entirely.
            // The deserializer already handles missing keys via ContainsKey checks.
            if (node.LayoutGroup != null) { WriteOptional(sb, i, "LayoutGroup", node.LayoutGroup, WriteLayoutGroup); sb.AppendLine(","); }
            if (node.GridLayoutGroup != null) { WriteOptional(sb, i, "GridLayoutGroup", node.GridLayoutGroup, WriteGridLayoutGroup); sb.AppendLine(","); }
            if (node.LayoutElement != null) { WriteOptional(sb, i, "LayoutElement", node.LayoutElement, WriteLayoutElement); sb.AppendLine(","); }
            if (node.ContentFitter != null) { WriteOptional(sb, i, "ContentFitter", node.ContentFitter, WriteContentFitter); sb.AppendLine(","); }
            if (node.AspectRatioFitter != null) { WriteOptional(sb, i, "AspectRatioFitter", node.AspectRatioFitter, WriteAspectRatioFitter); sb.AppendLine(","); }
            if (node.TextData != null) { WriteOptional(sb, i, "TextData", node.TextData, WriteText); sb.AppendLine(","); }
            if (node.ImageData != null) { WriteOptional(sb, i, "ImageData", node.ImageData, WriteImage); sb.AppendLine(","); }
            if (node.InputFieldData != null) { WriteOptional(sb, i, "InputFieldData", node.InputFieldData, WriteInputField); sb.AppendLine(","); }
            if (node.ScrollViewData != null) { WriteOptional(sb, i, "ScrollViewData", node.ScrollViewData, WriteScrollView); sb.AppendLine(","); }
            if (node.DropdownData != null) { WriteOptional(sb, i, "DropdownData", node.DropdownData, WriteDropdown); sb.AppendLine(","); }
            if (node.ButtonData != null) { WriteOptional(sb, i, "ButtonData", node.ButtonData, WriteButton); sb.AppendLine(","); }
            if (node.ToggleData != null) { WriteOptional(sb, i, "ToggleData", node.ToggleData, WriteToggle); sb.AppendLine(","); }
            if (node.SliderData != null) { WriteOptional(sb, i, "SliderData", node.SliderData, WriteSlider); sb.AppendLine(","); }
            if (node.RenderCameraData != null) { WriteOptional(sb, i, "RenderCameraData", node.RenderCameraData, WriteRenderCamera); sb.AppendLine(","); }

            // Children - always last (no trailing comma issue)
            Indent(sb, i); sb.Append("\"Children\": ");
            if (node.Children != null && node.Children.Count > 0)
            {
                sb.AppendLine("[");
                for (int childIndex = 0; childIndex < node.Children.Count; childIndex++)
                {
                    WriteNode(sb, node.Children[childIndex], i + 1);
                    if (childIndex < node.Children.Count - 1) sb.AppendLine(",");
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

        private static void WriteStyle(StringBuilder sb, UIElementStyle style, int indent)
        {
            if (style == null) { sb.Append("null"); return; }
            sb.AppendLine("{");
            int i = indent + 1;
            WriteColor(sb, i, "BackgroundColor", style.BackgroundColor); sb.AppendLine(",");
            WriteColor(sb, i, "BorderColor", style.BorderColor); sb.AppendLine(",");
            WriteFloat(sb, i, "BorderWidth", style.BorderWidth); sb.AppendLine(",");
            WriteFloat(sb, i, "CornerRadius", style.CornerRadius); sb.AppendLine(",");
            WriteFloat(sb, i, "Opacity", style.Opacity); sb.AppendLine(",");
            WriteBool(sb, i, "RaycastTarget", style.RaycastTarget); sb.AppendLine(",");
            WriteString(sb, i, "Shape", style.Shape.ToString()); sb.AppendLine(",");
            WriteString(sb, i, "BackgroundSprite", style.BackgroundSprite); sb.AppendLine(",");
            WriteInt(sb, i, "ImageType", style.ImageType); sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteText(StringBuilder sb, UITextDef text, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteString(sb, i, "Text", text.Text); sb.AppendLine(",");
            WriteFloat(sb, i, "FontSize", text.FontSize); sb.AppendLine(",");
            WriteString(sb, i, "FontCategory", text.FontCategory); sb.AppendLine(",");
            WriteInt(sb, i, "FontStyle", text.FontStyle); sb.AppendLine(",");
            WriteColor(sb, i, "Color", text.Color); sb.AppendLine(",");
            WriteInt(sb, i, "Alignment", text.Alignment); sb.AppendLine(",");
            WriteBool(sb, i, "WordWrap", text.WordWrap); sb.AppendLine(",");
            WriteInt(sb, i, "OverflowMode", text.OverflowMode); sb.AppendLine(",");
            WriteFloat(sb, i, "LineSpacing", text.LineSpacing); sb.AppendLine(",");
            WriteRectOffset(sb, i, "TextPadding", text.TextPadding);
            if (!text.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", text.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteImage(StringBuilder sb, UIImageDef image, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteString(sb, i, "SpriteName", image.SpriteName); sb.AppendLine(",");
            WriteInt(sb, i, "ImageType", image.ImageType); sb.AppendLine(",");
            WriteBool(sb, i, "PreserveAspect", image.PreserveAspect); sb.AppendLine(",");
            WriteColor(sb, i, "Color", image.Color); sb.AppendLine(",");
            WriteBool(sb, i, "FillCenter", image.FillCenter); sb.AppendLine(",");
            WriteFloat(sb, i, "PixelsPerUnit", image.PixelsPerUnit);
            if (!image.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", image.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteInputField(StringBuilder sb, UIInputFieldDef inputField, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteString(sb, i, "PlaceholderText", inputField.PlaceholderText); sb.AppendLine(",");
            WriteFloat(sb, i, "FontSize", inputField.FontSize); sb.AppendLine(",");
            WriteBool(sb, i, "Multiline", inputField.Multiline); sb.AppendLine(",");
            WriteInt(sb, i, "CharacterLimit", inputField.CharacterLimit); sb.AppendLine(",");
            WriteColor(sb, i, "TextColor", inputField.TextColor); sb.AppendLine(",");
            WriteColor(sb, i, "PlaceholderColor", inputField.PlaceholderColor); sb.AppendLine(",");
            WriteColor(sb, i, "BackgroundColor", inputField.BackgroundColor); sb.AppendLine(",");
            WriteColor(sb, i, "CaretColor", inputField.CaretColor); sb.AppendLine(",");
            WriteColor(sb, i, "SelectionColor", inputField.SelectionColor); sb.AppendLine(",");
            WriteString(sb, i, "ContentType", inputField.ContentType); sb.AppendLine(",");
            WriteInt(sb, i, "TextAlignment", inputField.TextAlignment); sb.AppendLine(",");
            WriteRectOffset(sb, i, "TextPadding", inputField.TextPadding);
            if (!inputField.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", inputField.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteScrollView(StringBuilder sb, UIScrollViewDef scrollView, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteBool(sb, i, "Horizontal", scrollView.Horizontal); sb.AppendLine(",");
            WriteBool(sb, i, "Vertical", scrollView.Vertical); sb.AppendLine(",");
            WriteInt(sb, i, "MovementType", scrollView.MovementType); sb.AppendLine(",");
            WriteFloat(sb, i, "ScrollSensitivity", scrollView.ScrollSensitivity); sb.AppendLine(",");
            WriteFloat(sb, i, "Elasticity", scrollView.Elasticity); sb.AppendLine(",");
            WriteColor(sb, i, "ViewportColor", scrollView.ViewportColor); sb.AppendLine(",");
            WriteColor(sb, i, "ContentColor", scrollView.ContentColor);
            if (!scrollView.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", scrollView.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteDropdown(StringBuilder sb, UIDropdownDef dropdown, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteInt(sb, i, "DefaultValue", dropdown.DefaultValue); sb.AppendLine(",");
            WriteFloat(sb, i, "ItemHeight", dropdown.ItemHeight); sb.AppendLine(",");
            WriteFloat(sb, i, "TemplateHeight", dropdown.TemplateHeight); sb.AppendLine(",");
            WriteFloat(sb, i, "FontSize", dropdown.FontSize); sb.AppendLine(",");
            WriteColor(sb, i, "TextColor", dropdown.TextColor); sb.AppendLine(",");
            WriteColor(sb, i, "BackgroundColor", dropdown.BackgroundColor); sb.AppendLine(",");
            WriteColor(sb, i, "ItemColor", dropdown.ItemColor); sb.AppendLine(",");
            Indent(sb, i); sb.Append("\"Options\": [");
            if (dropdown.Options != null && dropdown.Options.Count > 0)
            {
                for (int optionIndex = 0; optionIndex < dropdown.Options.Count; optionIndex++)
                {
                    sb.Append(Esc(dropdown.Options[optionIndex]));
                    if (optionIndex < dropdown.Options.Count - 1) sb.Append(",");
                }
            }
            sb.Append("]");
            if (!dropdown.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", dropdown.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteButton(StringBuilder sb, UIButtonDef button, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteString(sb, i, "Label", button.Label); sb.AppendLine(",");
            WriteFloat(sb, i, "FontSize", button.FontSize); sb.AppendLine(",");
            WriteColor(sb, i, "LabelColor", button.LabelColor); sb.AppendLine(",");
            WriteColor(sb, i, "NormalColor", button.NormalColor); sb.AppendLine(",");
            WriteColor(sb, i, "HighlightedColor", button.HighlightedColor); sb.AppendLine(",");
            WriteColor(sb, i, "PressedColor", button.PressedColor); sb.AppendLine(",");
            WriteColor(sb, i, "SelectedColor", button.SelectedColor); sb.AppendLine(",");
            WriteColor(sb, i, "DisabledColor", button.DisabledColor); sb.AppendLine(",");
            WriteFloat(sb, i, "FadeDuration", button.FadeDuration); sb.AppendLine(",");
            WriteInt(sb, i, "LabelAlignment", button.LabelAlignment); sb.AppendLine(",");
            WriteRectOffset(sb, i, "LabelPadding", button.LabelPadding); sb.AppendLine(",");
            WriteString(sb, i, "ClickAction", button.ClickAction); sb.AppendLine(",");
            WriteString(sb, i, "ClickActionParam", button.ClickActionParam);
            if (button.ShowLabel) { sb.AppendLine(","); WriteBool(sb, i, "ShowLabel", button.ShowLabel); }
            if (!button.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", button.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteToggle(StringBuilder sb, UIToggleDef toggle, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteBool(sb, i, "DefaultValue", toggle.DefaultValue); sb.AppendLine(",");
            WriteColor(sb, i, "CheckmarkColor", toggle.CheckmarkColor); sb.AppendLine(",");
            WriteColor(sb, i, "BackgroundColor", toggle.BackgroundColor); sb.AppendLine(",");
            WriteString(sb, i, "Label", toggle.Label); sb.AppendLine(",");
            WriteFloat(sb, i, "FontSize", toggle.FontSize); sb.AppendLine(",");
            WriteColor(sb, i, "LabelColor", toggle.LabelColor);
            if (!toggle.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", toggle.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteSlider(StringBuilder sb, UISliderDef slider, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteFloat(sb, i, "MinValue", slider.MinValue); sb.AppendLine(",");
            WriteFloat(sb, i, "MaxValue", slider.MaxValue); sb.AppendLine(",");
            WriteFloat(sb, i, "DefaultValue", slider.DefaultValue); sb.AppendLine(",");
            WriteBool(sb, i, "WholeNumbers", slider.WholeNumbers); sb.AppendLine(",");
            WriteColor(sb, i, "BackgroundColor", slider.BackgroundColor); sb.AppendLine(",");
            WriteColor(sb, i, "FillColor", slider.FillColor); sb.AppendLine(",");
            WriteColor(sb, i, "HandleColor", slider.HandleColor);
            if (!slider.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", slider.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteLayoutGroup(StringBuilder sb, UILayoutGroupDef layoutGroup, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteBool(sb, i, "IsVertical", layoutGroup.IsVertical); sb.AppendLine(",");
            WriteFloat(sb, i, "Spacing", layoutGroup.Spacing); sb.AppendLine(",");
            WriteRectOffset(sb, i, "Padding", layoutGroup.Padding); sb.AppendLine(",");
            WriteInt(sb, i, "ChildAlignment", layoutGroup.ChildAlignment); sb.AppendLine(",");
            WriteBool(sb, i, "ChildControlWidth", layoutGroup.ChildControlWidth); sb.AppendLine(",");
            WriteBool(sb, i, "ChildControlHeight", layoutGroup.ChildControlHeight); sb.AppendLine(",");
            WriteBool(sb, i, "ChildForceExpandWidth", layoutGroup.ChildForceExpandWidth); sb.AppendLine(",");
            WriteBool(sb, i, "ChildForceExpandHeight", layoutGroup.ChildForceExpandHeight); sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteLayoutElement(StringBuilder sb, UILayoutElementDef layoutElement, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteFloat(sb, i, "MinWidth", layoutElement.MinWidth); sb.AppendLine(",");
            WriteFloat(sb, i, "MinHeight", layoutElement.MinHeight); sb.AppendLine(",");
            WriteFloat(sb, i, "PreferredWidth", layoutElement.PreferredWidth); sb.AppendLine(",");
            WriteFloat(sb, i, "PreferredHeight", layoutElement.PreferredHeight); sb.AppendLine(",");
            WriteFloat(sb, i, "FlexibleWidth", layoutElement.FlexibleWidth); sb.AppendLine(",");
            WriteFloat(sb, i, "FlexibleHeight", layoutElement.FlexibleHeight); sb.AppendLine(",");
            WriteBool(sb, i, "IgnoreLayout", layoutElement.IgnoreLayout); sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteContentFitter(StringBuilder sb, UIContentFitterDef contentFitter, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteInt(sb, i, "HorizontalFit", contentFitter.HorizontalFit); sb.AppendLine(",");
            WriteInt(sb, i, "VerticalFit", contentFitter.VerticalFit); sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static void WriteGridLayoutGroup(StringBuilder sb, UIGridLayoutGroupDef grid, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteVec2(sb, i, "CellSize", grid.CellSize); sb.AppendLine(",");
            WriteVec2(sb, i, "Spacing", grid.Spacing); sb.AppendLine(",");
            WriteInt(sb, i, "StartCorner", grid.StartCorner); sb.AppendLine(",");
            WriteInt(sb, i, "StartAxis", grid.StartAxis); sb.AppendLine(",");
            WriteInt(sb, i, "ChildAlignment", grid.ChildAlignment); sb.AppendLine(",");
            WriteInt(sb, i, "Constraint", grid.Constraint); sb.AppendLine(",");
            WriteInt(sb, i, "ConstraintCount", grid.ConstraintCount); sb.AppendLine(",");
            WriteRectOffset(sb, i, "Padding", grid.Padding); sb.AppendLine();
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

        //  Primitive writers

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

        private static void WriteVec2(StringBuilder sb, int indent, string key, Vector2Ser vector)
        {
            Indent(sb, indent);
            sb.Append($"\"{key}\": {{\"X\":{F(vector.X)},\"Y\":{F(vector.Y)}}}");
        }

        private static void WriteColor(StringBuilder sb, int indent, string key, ColorSer color)
        {
            Indent(sb, indent);
            sb.Append($"\"{key}\": {{\"R\":{F(color.R)},\"G\":{F(color.G)},\"B\":{F(color.B)},\"A\":{F(color.A)}}}");
        }

        private static void WriteRectOffset(StringBuilder sb, int indent, string key, RectOffsetSer offset)
        {
            Indent(sb, indent);
            sb.Append($"\"{key}\": {{\"Left\":{offset.Left},\"Right\":{offset.Right},\"Top\":{offset.Top},\"Bottom\":{offset.Bottom}}}");
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

        private static string Esc(string text)
        {
            if (text == null) return "\"\"";
            var sb = new StringBuilder(text.Length + 2);
            sb.Append('"');
            foreach (char c in text)
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

        //  JSON Parser (minimal, recursive-descent)

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
                    char escaped = json[pos++];
                    switch (escaped)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(escaped); break;
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

        //  Object readers (parsed dict - typed)

        private static UILayoutDefinition ReadLayout(Dictionary<string, object> data)
        {
            var layout = new UILayoutDefinition();
            layout.UID = GetStr(data, "UID");
            layout.DisplayName = GetStr(data, "DisplayName");
            layout.Category = GetStr(data, "Category", "Custom");
            layout.Author = GetStr(data, "Author");
            layout.CreatedTimestamp = GetLong(data, "CreatedTimestamp");
            layout.ModifiedTimestamp = GetLong(data, "ModifiedTimestamp");
            layout.Version = GetInt(data, "Version", 1);
            layout.CanvasSize = GetVec2(data, "CanvasSize", new Vector2Ser(1920, 1080));

            if (data.ContainsKey("Metadata") && data["Metadata"] is List<object> metaList)
            {
                layout.Metadata = new List<MetadataEntry>();
                foreach (var item in metaList)
                {
                    if (item is Dictionary<string, object> metaData)
                        layout.Metadata.Add(new MetadataEntry { Key = GetStr(metaData, "Key"), Value = GetStr(metaData, "Value") });
                }
            }

            // Canvas scaler
            if (data.ContainsKey("CanvasScaler") && data["CanvasScaler"] is Dictionary<string, object> canvasScalerData)
                layout.CanvasScaler = ReadCanvasScaler(canvasScalerData);

            // Screen position and scale (preview layout mode)
            if (data.ContainsKey("ScreenPositionX") && data.ContainsKey("ScreenPositionY"))
            {
                layout.ScreenPositionX = GetFloat(data, "ScreenPositionX", float.NaN);
                layout.ScreenPositionY = GetFloat(data, "ScreenPositionY", float.NaN);
            }
            if (data.ContainsKey("ScreenScale"))
            {
                layout.ScreenScale = GetFloat(data, "ScreenScale", 1f);
            }

            // RuntimeRootTransform (preserved original root before editor normalization)
            if (data.ContainsKey("RuntimeRootTransform") && data["RuntimeRootTransform"] is Dictionary<string, object> rootTransformData)
                layout.RuntimeRootTransform = ReadRuntimeRootTransform(rootTransformData);

            if (data.ContainsKey("RootElement") && data["RootElement"] is Dictionary<string, object> rootDict)
                layout.RootElement = ReadNode(rootDict);

            return layout;
        }

        private static UIElementNode ReadNode(Dictionary<string, object> data)
        {
            var node = new UIElementNode();
            node.Id = GetStr(data, "Id");
            node.Name = GetStr(data, "Name");
            node.Tag = GetStr(data, "Tag");

            string typeStr = GetStr(data, "Type", "Panel");
            if (Enum.TryParse(typeStr, true, out UIElementType elementType)) node.Type = elementType;

            node.AnchorMin = GetVec2(data, "AnchorMin");
            node.AnchorMax = GetVec2(data, "AnchorMax", new Vector2Ser(1, 1));
            node.Pivot = GetVec2(data, "Pivot", new Vector2Ser(0.5f, 0.5f));
            node.AnchoredPosition = GetVec2(data, "AnchoredPosition");
            node.SizeDelta = GetVec2(data, "SizeDelta");
            node.OffsetMin = GetVec2(data, "OffsetMin");
            node.OffsetMax = GetVec2(data, "OffsetMax");
            node.Rotation = GetFloat(data, "Rotation");
            node.Active = GetBool(data, "Active", true);
            node.Interactable = GetBool(data, "Interactable", true);
            node.HasMask = GetBool(data, "HasMask");
            node.HasRectMask2D = GetBool(data, "HasRectMask2D");
            node.TransformLocked = GetBool(data, "TransformLocked");

            // Per-element metadata
            if (data.ContainsKey("Metadata") && data["Metadata"] is List<object> nodeMetaList)
            {
                node.Metadata = new List<MetadataEntry>();
                foreach (var item in nodeMetaList)
                {
                    if (item is Dictionary<string, object> metaData)
                        node.Metadata.Add(new MetadataEntry { Key = GetStr(metaData, "Key"), Value = GetStr(metaData, "Value") });
                }
            }

            if (data.ContainsKey("Style") && data["Style"] is Dictionary<string, object> styleData)
                node.Style = ReadStyle(styleData);

            if (data.ContainsKey("LayoutGroup") && data["LayoutGroup"] is Dictionary<string, object> layoutGroupData)
                node.LayoutGroup = ReadLayoutGroup(layoutGroupData);
            if (data.ContainsKey("GridLayoutGroup") && data["GridLayoutGroup"] is Dictionary<string, object> gridLayoutData)
                node.GridLayoutGroup = ReadGridLayoutGroup(gridLayoutData);
            if (data.ContainsKey("LayoutElement") && data["LayoutElement"] is Dictionary<string, object> led)
                node.LayoutElement = ReadLayoutElement(led);
            if (data.ContainsKey("ContentFitter") && data["ContentFitter"] is Dictionary<string, object> contentFitterData)
                node.ContentFitter = ReadContentFitter(contentFitterData);
            if (data.ContainsKey("AspectRatioFitter") && data["AspectRatioFitter"] is Dictionary<string, object> aspectRatioData)
                node.AspectRatioFitter = ReadAspectRatioFitter(aspectRatioData);

            if (data.ContainsKey("TextData") && data["TextData"] is Dictionary<string, object> textData)
                node.TextData = ReadText(textData);
            if (data.ContainsKey("ImageData") && data["ImageData"] is Dictionary<string, object> imageData)
                node.ImageData = ReadImage(imageData);
            if (data.ContainsKey("InputFieldData") && data["InputFieldData"] is Dictionary<string, object> inputFieldData)
                node.InputFieldData = ReadInputField(inputFieldData);
            if (data.ContainsKey("ScrollViewData") && data["ScrollViewData"] is Dictionary<string, object> scrollViewData)
                node.ScrollViewData = ReadScrollView(scrollViewData);
            if (data.ContainsKey("DropdownData") && data["DropdownData"] is Dictionary<string, object> dropdownData)
                node.DropdownData = ReadDropdown(dropdownData);
            if (data.ContainsKey("ButtonData") && data["ButtonData"] is Dictionary<string, object> buttonData)
                node.ButtonData = ReadButton(buttonData);
            if (data.ContainsKey("ToggleData") && data["ToggleData"] is Dictionary<string, object> toggleData)
                node.ToggleData = ReadToggle(toggleData);
            if (data.ContainsKey("SliderData") && data["SliderData"] is Dictionary<string, object> sliderData)
                node.SliderData = ReadSlider(sliderData);

            if (data.ContainsKey("RenderCameraData") && data["RenderCameraData"] is Dictionary<string, object> renderCameraData)
                node.RenderCameraData = ReadRenderCamera(renderCameraData);

            if (data.ContainsKey("Children") && data["Children"] is List<object> children)
            {
                node.Children = new List<UIElementNode>();
                foreach (var child in children)
                    if (child is Dictionary<string, object> childData)
                        node.Children.Add(ReadNode(childData));
            }

            return node;
        }

        private static UIElementStyle ReadStyle(Dictionary<string, object> data)
        {
            var style = new UIElementStyle();
            style.BackgroundColor = GetColor(data, "BackgroundColor", style.BackgroundColor);
            style.BorderColor = GetColor(data, "BorderColor", style.BorderColor);
            style.BorderWidth = GetFloat(data, "BorderWidth");
            style.CornerRadius = GetFloat(data, "CornerRadius");
            style.Opacity = GetFloat(data, "Opacity", 1f);
            style.RaycastTarget = GetBool(data, "RaycastTarget", true);
            string shapeStr = GetStr(data, "Shape", "Rectangle");
            if (Enum.TryParse(shapeStr, true, out UIShapeType shape)) style.Shape = shape;
            style.BackgroundSprite = GetStr(data, "BackgroundSprite");
            style.ImageType = GetInt(data, "ImageType");
            return style;
        }

        private static UITextDef ReadText(Dictionary<string, object> data)
        {
            var text = new UITextDef();
            text.Text = GetStr(data, "Text");
            text.FontSize = GetFloat(data, "FontSize", 16f);
            text.FontCategory = GetStr(data, "FontCategory", "Body");
            text.FontStyle = GetInt(data, "FontStyle");
            text.Color = GetColor(data, "Color", text.Color);
            text.Alignment = GetInt(data, "Alignment", (int)TMPro.TextAlignmentOptions.MidlineLeft);
            text.WordWrap = GetBool(data, "WordWrap", true);
            text.OverflowMode = GetInt(data, "OverflowMode");
            text.LineSpacing = GetFloat(data, "LineSpacing");
            text.TextPadding = GetRectOffset(data, "TextPadding");
            text.ComponentEnabled = GetBool(data, "ComponentEnabled", true);
            return text;
        }

        private static UIImageDef ReadImage(Dictionary<string, object> data)
        {
            var img = new UIImageDef();
            img.SpriteName = GetStr(data, "SpriteName");
            img.ImageType = GetInt(data, "ImageType");
            img.PreserveAspect = GetBool(data, "PreserveAspect");
            img.Color = GetColor(data, "Color", ColorSer.White);
            img.FillCenter = GetBool(data, "FillCenter", true);
            img.PixelsPerUnit = GetFloat(data, "PixelsPerUnit", 100f);
            img.ComponentEnabled = GetBool(data, "ComponentEnabled", true);
            return img;
        }

        private static UIInputFieldDef ReadInputField(Dictionary<string, object> data)
        {
            var inf = new UIInputFieldDef();
            inf.PlaceholderText = GetStr(data, "PlaceholderText");
            inf.FontSize = GetFloat(data, "FontSize", 13f);
            inf.Multiline = GetBool(data, "Multiline");
            inf.CharacterLimit = GetInt(data, "CharacterLimit");
            inf.TextColor = GetColor(data, "TextColor", inf.TextColor);
            inf.PlaceholderColor = GetColor(data, "PlaceholderColor", inf.PlaceholderColor);
            inf.BackgroundColor = GetColor(data, "BackgroundColor", inf.BackgroundColor);
            inf.CaretColor = GetColor(data, "CaretColor", inf.CaretColor);
            inf.SelectionColor = GetColor(data, "SelectionColor", inf.SelectionColor);
            inf.ContentType = GetStr(data, "ContentType", "Standard");
            inf.TextAlignment = GetInt(data, "TextAlignment", (int)TMPro.TextAlignmentOptions.MidlineLeft);
            inf.TextPadding = GetRectOffset(data, "TextPadding");
            inf.ComponentEnabled = GetBool(data, "ComponentEnabled", true);
            return inf;
        }

        private static UIScrollViewDef ReadScrollView(Dictionary<string, object> data)
        {
            var scrollView = new UIScrollViewDef();
            scrollView.Horizontal = GetBool(data, "Horizontal");
            scrollView.Vertical = GetBool(data, "Vertical", true);
            scrollView.MovementType = GetInt(data, "MovementType", 2);
            scrollView.ScrollSensitivity = GetFloat(data, "ScrollSensitivity", 30f);
            scrollView.Elasticity = GetFloat(data, "Elasticity", 0.1f);
            scrollView.ViewportColor = GetColor(data, "ViewportColor", scrollView.ViewportColor);
            scrollView.ContentColor = GetColor(data, "ContentColor", scrollView.ContentColor);
            scrollView.ComponentEnabled = GetBool(data, "ComponentEnabled", true);
            return scrollView;
        }

        private static UIDropdownDef ReadDropdown(Dictionary<string, object> data)
        {
            var dropdown = new UIDropdownDef();
            dropdown.DefaultValue = GetInt(data, "DefaultValue");
            dropdown.ItemHeight = GetFloat(data, "ItemHeight", 24f);
            dropdown.TemplateHeight = GetFloat(data, "TemplateHeight", 160f);
            dropdown.FontSize = GetFloat(data, "FontSize", 13f);
            dropdown.TextColor = GetColor(data, "TextColor", dropdown.TextColor);
            dropdown.BackgroundColor = GetColor(data, "BackgroundColor", dropdown.BackgroundColor);
            dropdown.ItemColor = GetColor(data, "ItemColor", dropdown.ItemColor);
            if (data.ContainsKey("Options") && data["Options"] is List<object> opts)
            {
                dropdown.Options = new List<string>();
                foreach (var option in opts) dropdown.Options.Add(option?.ToString() ?? "");
            }
            dropdown.ComponentEnabled = GetBool(data, "ComponentEnabled", true);
            return dropdown;
        }

        private static UIButtonDef ReadButton(Dictionary<string, object> data)
        {
            var button = new UIButtonDef();
            button.Label = GetStr(data, "Label", "Button");
            button.FontSize = GetFloat(data, "FontSize", 13f);
            button.LabelColor = GetColor(data, "LabelColor", button.LabelColor);
            button.NormalColor = GetColor(data, "NormalColor", button.NormalColor);
            button.HighlightedColor = GetColor(data, "HighlightedColor", button.HighlightedColor);
            button.PressedColor = GetColor(data, "PressedColor", button.PressedColor);
            button.SelectedColor = GetColor(data, "SelectedColor", button.SelectedColor);
            button.DisabledColor = GetColor(data, "DisabledColor", button.DisabledColor);
            button.FadeDuration = GetFloat(data, "FadeDuration", 0.08f);
            button.LabelAlignment = GetInt(data, "LabelAlignment", (int)TMPro.TextAlignmentOptions.Center);
            button.LabelPadding = GetRectOffset(data, "LabelPadding");
            button.ClickAction = GetStr(data, "ClickAction");
            button.ClickActionParam = GetStr(data, "ClickActionParam");
            button.ShowLabel = GetBool(data, "ShowLabel");
            button.ComponentEnabled = GetBool(data, "ComponentEnabled", true);
            return button;
        }

        private static UIToggleDef ReadToggle(Dictionary<string, object> data)
        {
            var toggle = new UIToggleDef();
            toggle.DefaultValue = GetBool(data, "DefaultValue");
            toggle.CheckmarkColor = GetColor(data, "CheckmarkColor", toggle.CheckmarkColor);
            toggle.BackgroundColor = GetColor(data, "BackgroundColor", toggle.BackgroundColor);
            toggle.Label = GetStr(data, "Label");
            toggle.FontSize = GetFloat(data, "FontSize", 13f);
            toggle.LabelColor = GetColor(data, "LabelColor", toggle.LabelColor);
            toggle.ComponentEnabled = GetBool(data, "ComponentEnabled", true);
            return toggle;
        }

        private static UISliderDef ReadSlider(Dictionary<string, object> data)
        {
            var slider = new UISliderDef();
            slider.MinValue = GetFloat(data, "MinValue");
            slider.MaxValue = GetFloat(data, "MaxValue", 1f);
            slider.DefaultValue = GetFloat(data, "DefaultValue");
            slider.WholeNumbers = GetBool(data, "WholeNumbers");
            slider.BackgroundColor = GetColor(data, "BackgroundColor", slider.BackgroundColor);
            slider.FillColor = GetColor(data, "FillColor", slider.FillColor);
            slider.HandleColor = GetColor(data, "HandleColor", slider.HandleColor);
            slider.ComponentEnabled = GetBool(data, "ComponentEnabled", true);
            return slider;
        }

        private static void WriteRenderCamera(StringBuilder sb, UIRenderCameraDef renderCamera, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteString(sb, i, "Mode", renderCamera.Mode); sb.AppendLine(",");
            WriteString(sb, i, "PrefabName", renderCamera.PrefabName); sb.AppendLine(",");
            WriteFloat(sb, i, "FieldOfView", renderCamera.FieldOfView); sb.AppendLine(",");
            WriteColor(sb, i, "BackgroundColor", renderCamera.BackgroundColor); sb.AppendLine(",");
            WriteInt(sb, i, "TextureWidth", renderCamera.TextureWidth); sb.AppendLine(",");
            WriteInt(sb, i, "TextureHeight", renderCamera.TextureHeight); sb.AppendLine(",");
            WriteVec2(sb, i, "CameraOffset", renderCamera.CameraOffset); sb.AppendLine(",");
            WriteFloat(sb, i, "CameraDistance", renderCamera.CameraDistance); sb.AppendLine(",");
            WriteFloat(sb, i, "CameraRotationY", renderCamera.CameraRotationY); sb.AppendLine(",");
            WriteFloat(sb, i, "CameraRotationX", renderCamera.CameraRotationX); sb.AppendLine(",");
            WriteString(sb, i, "LayerName", renderCamera.LayerName);
            if (!renderCamera.ComponentEnabled) { sb.AppendLine(","); WriteBool(sb, i, "ComponentEnabled", renderCamera.ComponentEnabled); }
            sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static UIRenderCameraDef ReadRenderCamera(Dictionary<string, object> data)
        {
            var renderCamera = new UIRenderCameraDef();
            renderCamera.Mode = GetStr(data, "Mode", "player");
            renderCamera.PrefabName = GetStr(data, "PrefabName", "");
            renderCamera.FieldOfView = GetFloat(data, "FieldOfView", 30f);
            renderCamera.BackgroundColor = GetColor(data, "BackgroundColor", renderCamera.BackgroundColor);
            renderCamera.TextureWidth = GetInt(data, "TextureWidth", 256);
            renderCamera.TextureHeight = GetInt(data, "TextureHeight", 256);
            renderCamera.CameraOffset = GetVec2(data, "CameraOffset");
            renderCamera.CameraDistance = GetFloat(data, "CameraDistance", 3f);
            renderCamera.CameraRotationY = GetFloat(data, "CameraRotationY");
            renderCamera.CameraRotationX = GetFloat(data, "CameraRotationX", 10f);
            renderCamera.LayerName = GetStr(data, "LayerName", "");
            renderCamera.ComponentEnabled = GetBool(data, "ComponentEnabled", true);
            return renderCamera;
        }

        private static UILayoutGroupDef ReadLayoutGroup(Dictionary<string, object> data)
        {
            var layoutGroup = new UILayoutGroupDef();
            layoutGroup.IsVertical = GetBool(data, "IsVertical", true);
            layoutGroup.Spacing = GetFloat(data, "Spacing");
            layoutGroup.Padding = GetRectOffset(data, "Padding");
            layoutGroup.ChildAlignment = GetInt(data, "ChildAlignment");
            layoutGroup.ChildControlWidth = GetBool(data, "ChildControlWidth", true);
            layoutGroup.ChildControlHeight = GetBool(data, "ChildControlHeight");
            layoutGroup.ChildForceExpandWidth = GetBool(data, "ChildForceExpandWidth", true);
            layoutGroup.ChildForceExpandHeight = GetBool(data, "ChildForceExpandHeight");
            return layoutGroup;
        }

        private static UILayoutElementDef ReadLayoutElement(Dictionary<string, object> data)
        {
            var layoutElement = new UILayoutElementDef();
            layoutElement.MinWidth = GetFloat(data, "MinWidth", -1);
            layoutElement.MinHeight = GetFloat(data, "MinHeight", -1);
            layoutElement.PreferredWidth = GetFloat(data, "PreferredWidth", -1);
            layoutElement.PreferredHeight = GetFloat(data, "PreferredHeight", -1);
            layoutElement.FlexibleWidth = GetFloat(data, "FlexibleWidth", -1);
            layoutElement.FlexibleHeight = GetFloat(data, "FlexibleHeight", -1);
            layoutElement.IgnoreLayout = GetBool(data, "IgnoreLayout");
            return layoutElement;
        }

        private static UIContentFitterDef ReadContentFitter(Dictionary<string, object> data)
        {
            var contentFitter = new UIContentFitterDef();
            contentFitter.HorizontalFit = GetInt(data, "HorizontalFit");
            contentFitter.VerticalFit = GetInt(data, "VerticalFit");
            return contentFitter;
        }

        private static UIGridLayoutGroupDef ReadGridLayoutGroup(Dictionary<string, object> data)
        {
            var grid = new UIGridLayoutGroupDef();
            grid.CellSize = GetVec2(data, "CellSize", new Vector2Ser(64, 64));
            grid.Spacing = GetVec2(data, "Spacing", new Vector2Ser(0, 0));
            grid.StartCorner = GetInt(data, "StartCorner");
            grid.StartAxis = GetInt(data, "StartAxis");
            grid.ChildAlignment = GetInt(data, "ChildAlignment");
            grid.Constraint = GetInt(data, "Constraint");
            grid.ConstraintCount = GetInt(data, "ConstraintCount", 2);
            grid.Padding = GetRectOffset(data, "Padding");
            return grid;
        }

        private static void WriteCanvasScaler(StringBuilder sb, UICanvasScalerDef canvasScaler, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteInt(sb, i, "ScaleMode", canvasScaler.ScaleMode); sb.AppendLine(",");
            WriteVec2(sb, i, "ReferenceResolution", canvasScaler.ReferenceResolution); sb.AppendLine(",");
            WriteFloat(sb, i, "MatchWidthOrHeight", canvasScaler.MatchWidthOrHeight); sb.AppendLine(",");
            WriteFloat(sb, i, "ReferencePixelsPerUnit", canvasScaler.ReferencePixelsPerUnit); sb.AppendLine(",");
            WriteFloat(sb, i, "ScaleFactor", canvasScaler.ScaleFactor); sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static UICanvasScalerDef ReadCanvasScaler(Dictionary<string, object> data)
        {
            var canvasScaler = new UICanvasScalerDef();
            canvasScaler.ScaleMode = GetInt(data, "ScaleMode", 1);
            canvasScaler.ReferenceResolution = GetVec2(data, "ReferenceResolution", new Vector2Ser(1920, 1080));
            canvasScaler.MatchWidthOrHeight = GetFloat(data, "MatchWidthOrHeight", 0.5f);
            canvasScaler.ReferencePixelsPerUnit = GetFloat(data, "ReferencePixelsPerUnit", 100f);
            canvasScaler.ScaleFactor = GetFloat(data, "ScaleFactor", 1f);
            return canvasScaler;
        }

        private static void WriteAspectRatioFitter(StringBuilder sb, UIAspectRatioFitterDef fitter, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteInt(sb, i, "AspectMode", fitter.AspectMode); sb.AppendLine(",");
            WriteFloat(sb, i, "AspectRatio", fitter.AspectRatio); sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static UIAspectRatioFitterDef ReadAspectRatioFitter(Dictionary<string, object> data)
        {
            var aspectRatioFitter = new UIAspectRatioFitterDef();
            aspectRatioFitter.AspectMode = GetInt(data, "AspectMode", 1);
            aspectRatioFitter.AspectRatio = GetFloat(data, "AspectRatio", 1f);
            return aspectRatioFitter;
        }

        private static void WriteRuntimeRootTransform(StringBuilder sb, RuntimeRootTransformDef rootTransform, int indent)
        {
            sb.AppendLine("{");
            int i = indent + 1;
            WriteVec2(sb, i, "AnchorMin", rootTransform.AnchorMin); sb.AppendLine(",");
            WriteVec2(sb, i, "AnchorMax", rootTransform.AnchorMax); sb.AppendLine(",");
            WriteVec2(sb, i, "Pivot", rootTransform.Pivot); sb.AppendLine(",");
            WriteVec2(sb, i, "AnchoredPosition", rootTransform.AnchoredPosition); sb.AppendLine(",");
            WriteVec2(sb, i, "SizeDelta", rootTransform.SizeDelta); sb.AppendLine(",");
            WriteVec2(sb, i, "OffsetMin", rootTransform.OffsetMin); sb.AppendLine(",");
            WriteVec2(sb, i, "OffsetMax", rootTransform.OffsetMax); sb.AppendLine();
            Indent(sb, indent); sb.Append("}");
        }

        private static RuntimeRootTransformDef ReadRuntimeRootTransform(Dictionary<string, object> data)
        {
            var rootTransform = new RuntimeRootTransformDef();
            rootTransform.AnchorMin = GetVec2(data, "AnchorMin");
            rootTransform.AnchorMax = GetVec2(data, "AnchorMax", new Vector2Ser(1, 1));
            rootTransform.Pivot = GetVec2(data, "Pivot", new Vector2Ser(0.5f, 0.5f));
            rootTransform.AnchoredPosition = GetVec2(data, "AnchoredPosition");
            rootTransform.SizeDelta = GetVec2(data, "SizeDelta");
            rootTransform.OffsetMin = GetVec2(data, "OffsetMin");
            rootTransform.OffsetMax = GetVec2(data, "OffsetMax");
            return rootTransform;
        }

        //  Typed getters from parsed dict

        private static string GetStr(Dictionary<string, object> data, string key, string def = "")
        {
            if (data.TryGetValue(key, out object raw) && raw is string text) return text;
            return def;
        }

        private static int GetInt(Dictionary<string, object> data, string key, int def = 0)
        {
            if (data.TryGetValue(key, out object raw))
            {
                if (raw is double number) return (int)number;
                if (raw is int integer) return integer;
            }
            return def;
        }

        private static long GetLong(Dictionary<string, object> data, string key, long def = 0)
        {
            if (data.TryGetValue(key, out object raw))
            {
                if (raw is double number) return (long)number;
            }
            return def;
        }

        private static float GetFloat(Dictionary<string, object> data, string key, float def = 0)
        {
            if (data.TryGetValue(key, out object raw))
            {
                if (raw is double number) return (float)number;
            }
            return def;
        }

        private static bool GetBool(Dictionary<string, object> data, string key, bool def = false)
        {
            if (data.TryGetValue(key, out object raw))
            {
                if (raw is bool flag) return flag;
            }
            return def;
        }

        private static Vector2Ser GetVec2(Dictionary<string, object> data, string key, Vector2Ser def = default)
        {
            if (data.TryGetValue(key, out object raw) && raw is Dictionary<string, object> vectorData)
                return new Vector2Ser(GetFloat(vectorData, "X"), GetFloat(vectorData, "Y"));
            return def;
        }

        private static ColorSer GetColor(Dictionary<string, object> data, string key, ColorSer def = default)
        {
            if (data.TryGetValue(key, out object raw) && raw is Dictionary<string, object> colorData)
                return new ColorSer(GetFloat(colorData, "R"), GetFloat(colorData, "G"), GetFloat(colorData, "B"), GetFloat(colorData, "A", 1f));
            return def;
        }

        private static RectOffsetSer GetRectOffset(Dictionary<string, object> data, string key)
        {
            if (data.TryGetValue(key, out object raw) && raw is Dictionary<string, object> offsetData)
                return new RectOffsetSer(GetInt(offsetData, "Left"), GetInt(offsetData, "Right"), GetInt(offsetData, "Top"), GetInt(offsetData, "Bottom"));
            return new RectOffsetSer();
        }
    }
}
