using System;
using System.Collections.Generic;

namespace FiresCore.UI
{
    /// <summary>
    /// Built-in layout templates that replicate existing hard-coded UIs.
    /// Generated programmatically and saved to UILayouts/ on first run.
    /// Admins can duplicate and customize these as starting points.
    /// </summary>
    public static class UIBuilderTemplates
    {
        /// <summary>
        /// Generates the default dialogue panel template that matches the hard-coded DialogueUI layout.
        /// </summary>
        public static UILayoutDefinition CreateDialoguePanelDefault()
        {
            var layout = new UILayoutDefinition
            {
                UID = "dialogue_panel_default",
                DisplayName = "Dialogue Panel (Default)",
                Category = "Dialogue",
                Author = "System",
                CanvasSize = new Vector2Ser(1920, 1080),
                Version = 1
            };

            // Root panel � centered, 520x480
            var root = new UIElementNode
            {
                Id = "root",
                Name = "DialoguePanel",
                Tag = "",
                Type = UIElementType.Panel,
                AnchorMin = new Vector2Ser(0.5f, 0.5f),
                AnchorMax = new Vector2Ser(0.5f, 0.5f),
                Pivot = new Vector2Ser(0.5f, 0.5f),
                SizeDelta = new Vector2Ser(520, 480),
                Style = new UIElementStyle
                {
                    BackgroundColor = new ColorSer(0.08f, 0.08f, 0.1f, 0.95f),
                    Opacity = 1f,
                    RaycastTarget = true
                },
                LayoutGroup = new UILayoutGroupDef
                {
                    IsVertical = true,
                    Spacing = 8,
                    Padding = new RectOffsetSer(20, 20, 36, 16),
                    ChildControlWidth = true,
                    ChildControlHeight = false,
                    ChildForceExpandWidth = true,
                    ChildForceExpandHeight = false
                },
                Active = true
            };

            // Close button (top-right)
            var closeBtn = new UIElementNode
            {
                Id = "close_btn",
                Name = "Close Button",
                Tag = "close_button",
                Type = UIElementType.Button,
                AnchorMin = new Vector2Ser(1, 1),
                AnchorMax = new Vector2Ser(1, 1),
                Pivot = new Vector2Ser(1, 1),
                AnchoredPosition = new Vector2Ser(-4, -4),
                SizeDelta = new Vector2Ser(28, 28),
                ButtonData = new UIButtonDef
                {
                    Label = "X",
                    FontSize = 16,
                    LabelColor = new ColorSer(1f, 0.9f, 0.8f, 1f),
                    NormalColor = new ColorSer(0.6f, 0.15f, 0.1f, 0.9f),
                    HighlightedColor = new ColorSer(0.8f, 0.25f, 0.15f, 1f),
                    PressedColor = new ColorSer(0.9f, 0.3f, 0.2f, 1f),
                    ClickAction = "close_panel"
                },
                LayoutElement = new UILayoutElementDef { IgnoreLayout = true },
                Style = new UIElementStyle { RaycastTarget = true }
            };
            root.AddChild(closeBtn);

            // NPC Name
            var npcName = new UIElementNode
            {
                Id = "npc_name",
                Name = "NPC Name",
                Tag = "npc_name",
                Type = UIElementType.Text,
                SizeDelta = new Vector2Ser(0, 32),
                TextData = new UITextDef
                {
                    Text = "NPC Name",
                    FontSize = 22,
                    FontCategory = "Primary",
                    FontStyle = 1, // Bold
                    Color = new ColorSer(1f, 0.85f, 0.5f, 1f),
                    Alignment = (int)TMPro.TextAlignmentOptions.Center,
                    WordWrap = false
                },
                LayoutElement = new UILayoutElementDef { PreferredHeight = 32 },
                Style = new UIElementStyle { RaycastTarget = false }
            };
            root.AddChild(npcName);

            // Divider
            var divider = new UIElementNode
            {
                Id = "divider",
                Name = "Divider",
                Tag = "divider",
                Type = UIElementType.Divider,
                AnchorMin = new Vector2Ser(0, 0.5f),
                AnchorMax = new Vector2Ser(1, 0.5f),
                SizeDelta = new Vector2Ser(0, 1),
                Style = new UIElementStyle
                {
                    BackgroundColor = new ColorSer(1f, 0.85f, 0.5f, 0.3f),
                    RaycastTarget = false
                },
                LayoutElement = new UILayoutElementDef { PreferredHeight = 1 }
            };
            root.AddChild(divider);

            // Dialogue text
            var dialogueText = new UIElementNode
            {
                Id = "dialogue_text",
                Name = "Dialogue Text",
                Tag = "dialogue_text",
                Type = UIElementType.Text,
                SizeDelta = new Vector2Ser(0, 40),
                TextData = new UITextDef
                {
                    Text = "This is where the NPC dialogue text appears.",
                    FontSize = 16,
                    FontCategory = "Body",
                    Color = new ColorSer(0.9f, 0.88f, 0.82f, 1f),
                    Alignment = (int)TMPro.TextAlignmentOptions.TopLeft,
                    WordWrap = true
                },
                LayoutElement = new UILayoutElementDef { MinHeight = 40 },
                Style = new UIElementStyle { RaycastTarget = false }
            };
            root.AddChild(dialogueText);

            // Spacer
            var spacer = new UIElementNode
            {
                Id = "spacer",
                Name = "Spacer",
                Type = UIElementType.Spacer,
                SizeDelta = new Vector2Ser(0, 6),
                LayoutElement = new UILayoutElementDef { PreferredHeight = 6 }
            };
            root.AddChild(spacer);

            // Options container (ScrollView)
            var optionsContainer = new UIElementNode
            {
                Id = "options_scroll",
                Name = "Options Container",
                Tag = "options_container",
                Type = UIElementType.Panel,
                SizeDelta = new Vector2Ser(0, 250),
                Style = new UIElementStyle
                {
                    BackgroundColor = new ColorSer(0, 0, 0, 0),
                    RaycastTarget = false
                },
                LayoutGroup = new UILayoutGroupDef
                {
                    IsVertical = true,
                    Spacing = 4,
                    ChildControlWidth = true,
                    ChildControlHeight = false,
                    ChildForceExpandWidth = true,
                    ChildForceExpandHeight = false
                },
                LayoutElement = new UILayoutElementDef { FlexibleHeight = 1, MinHeight = 60, PreferredHeight = 250 }
            };
            root.AddChild(optionsContainer);

            layout.RootElement = root;
            return layout;
        }

        /// <summary>
        /// Generates a compact dialogue template for quick conversations.
        /// </summary>
        public static UILayoutDefinition CreateDialoguePanelCompact()
        {
            var layout = new UILayoutDefinition
            {
                UID = "dialogue_panel_compact",
                DisplayName = "Dialogue Panel (Compact)",
                Category = "Dialogue",
                Author = "System",
                CanvasSize = new Vector2Ser(1920, 1080),
                Version = 1
            };

            var root = new UIElementNode
            {
                Id = "root",
                Name = "CompactDialogue",
                Type = UIElementType.Panel,
                AnchorMin = new Vector2Ser(0.5f, 0.1f),
                AnchorMax = new Vector2Ser(0.5f, 0.1f),
                Pivot = new Vector2Ser(0.5f, 0),
                SizeDelta = new Vector2Ser(450, 300),
                Style = new UIElementStyle
                {
                    BackgroundColor = new ColorSer(0.06f, 0.06f, 0.08f, 0.92f),
                    Opacity = 1f,
                    RaycastTarget = true
                },
                LayoutGroup = new UILayoutGroupDef
                {
                    IsVertical = true,
                    Spacing = 6,
                    Padding = new RectOffsetSer(14, 14, 14, 14),
                    ChildControlWidth = true,
                    ChildControlHeight = false,
                    ChildForceExpandWidth = true
                }
            };

            var npcName = new UIElementNode
            {
                Id = "npc_name",
                Name = "NPC Name",
                Tag = "npc_name",
                Type = UIElementType.Text,
                TextData = new UITextDef
                {
                    Text = "NPC",
                    FontSize = 18,
                    FontCategory = "Primary",
                    FontStyle = 1,
                    Color = new ColorSer(1f, 0.85f, 0.5f, 1f),
                    Alignment = (int)TMPro.TextAlignmentOptions.MidlineLeft
                },
                LayoutElement = new UILayoutElementDef { PreferredHeight = 24 },
                Style = new UIElementStyle { RaycastTarget = false }
            };
            root.AddChild(npcName);

            var dialogueText = new UIElementNode
            {
                Id = "dialogue_text",
                Name = "Text",
                Tag = "dialogue_text",
                Type = UIElementType.Text,
                TextData = new UITextDef
                {
                    Text = "Dialogue text here...",
                    FontSize = 14,
                    Color = new ColorSer(0.85f, 0.83f, 0.78f, 1f),
                    Alignment = (int)TMPro.TextAlignmentOptions.TopLeft,
                    WordWrap = true
                },
                LayoutElement = new UILayoutElementDef { MinHeight = 30 },
                Style = new UIElementStyle { RaycastTarget = false }
            };
            root.AddChild(dialogueText);

            var options = new UIElementNode
            {
                Id = "options",
                Name = "Options",
                Tag = "options_container",
                Type = UIElementType.Panel,
                Style = new UIElementStyle { BackgroundColor = ColorSer.Clear, RaycastTarget = false },
                LayoutGroup = new UILayoutGroupDef
                {
                    IsVertical = true,
                    Spacing = 3,
                    ChildControlWidth = true,
                    ChildControlHeight = false,
                    ChildForceExpandWidth = true
                },
                LayoutElement = new UILayoutElementDef { FlexibleHeight = 1, MinHeight = 40 }
            };
            root.AddChild(options);

            layout.RootElement = root;
            return layout;
        }

        /// <summary>
        /// Ensures all built-in templates are saved to disk if not already present.
        /// Called during codex initialization.
        /// </summary>
        public static void EnsureDefaultTemplates()
        {
            try
            {
                SaveIfNotExists(CreateDialoguePanelDefault);
                SaveIfNotExists(CreateDialoguePanelCompact);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[UIBuilderTemplates] Error ensuring templates: {ex.Message}");
            }
        }

        /// <summary>
        /// Captures all currently visible known UIs and saves them as templates.
        /// Useful for auto-generating templates from programmatic UIs that are on screen.
        /// </summary>
        public static int CaptureAndSaveVisibleUIs()
        {
            int saved = 0;
            for (int i = 0; i < UILayoutParser.KnownTargets.Count; i++)
            {
                try
                {
                    var target = UILayoutParser.KnownTargets[i];
                    var root = target.FindRoot();
                    if (root == null) continue;

                    string uid = target.UID;
                    string category = string.IsNullOrEmpty(target.Category) ? "custom" : target.Category.ToLowerInvariant();
                    string fileName = $"{category}/{uid}.json";
                    string fullPath = System.IO.Path.Combine(
                        FiresCore.Storage.FiresConfigPaths.UiLayouts, fileName);

                    // Only capture if template doesn't already exist
                    if (System.IO.File.Exists(fullPath)) continue;

                    var layout = UILayoutParser.CaptureKnownTarget(target);
                    if (layout == null) continue;

                    layout.CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    layout.ModifiedTimestamp = layout.CreatedTimestamp;
                    layout.SetMeta("auto_captured", "true");

                    UILayoutSerializer.SaveToFile(layout, fileName);
                    UILayoutCodex.AddOrReplace(layout);
                    saved++;

                    UnityEngine.Debug.Log($"[UIBuilderTemplates] Auto-captured template: {target.DisplayName} ? {fileName}");
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogWarning($"[UIBuilderTemplates] Error capturing target {i}: {ex.Message}");
                }
            }
            return saved;
        }

        /// <summary>
        /// Captures a single live UI by name and saves it as a template.
        /// Returns the layout, or null on failure.
        /// </summary>
        public static UILayoutDefinition CaptureAndSave(string gameObjectName, string uid = null, string displayName = null, string category = "Custom")
        {
            var layout = UILayoutParser.CaptureByName(gameObjectName, uid, displayName, category);
            if (layout == null) return null;

            layout.CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            layout.ModifiedTimestamp = layout.CreatedTimestamp;
            layout.SetMeta("auto_captured", "true");

            string cat = string.IsNullOrEmpty(layout.Category) ? "custom" : layout.Category.ToLowerInvariant();
            string fileName = $"{cat}/{layout.UID}.json";
            UILayoutSerializer.SaveToFile(layout, fileName);
            UILayoutCodex.AddOrReplace(layout);

            UnityEngine.Debug.Log($"[UIBuilderTemplates] Saved captured template: {layout.DisplayName} ? {fileName}");
            return layout;
        }

        private static void SaveIfNotExists(Func<UILayoutDefinition> generator)
        {
            var layout = generator();
            if (layout == null || string.IsNullOrEmpty(layout.UID)) return;

            // Check if file already exists
            string category = string.IsNullOrEmpty(layout.Category) ? "custom" : layout.Category.ToLowerInvariant();
            string fileName = $"{category}/{layout.UID}.json";
            string fullPath = System.IO.Path.Combine(
                FiresCore.Storage.FiresConfigPaths.UiLayouts, fileName);

            if (System.IO.File.Exists(fullPath)) return;

            layout.CreatedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            layout.ModifiedTimestamp = layout.CreatedTimestamp;

            UILayoutSerializer.SaveToFile(layout, fileName);
            UnityEngine.Debug.Log($"[UIBuilderTemplates] Created default template: {fileName}");
        }
    }
}
