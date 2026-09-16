namespace FiresCore.UI
{
    /// <summary>
    /// Quick-add element presets for common UI patterns.
    /// Used by context menus to offer pre-styled elements.
    /// </summary>
    public static class UIBuilderPresets
    {
        private static string GenerateId()
        {
            return System.Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        //  Button presets

        public static UIElementNode DefaultButton()
        {
            return new UIElementNode
            {
                Id = GenerateId(),
                Name = "Button",
                Type = UIElementType.Button,
                SizeDelta = new Vector2Ser(160, 36),
                Style = new UIElementStyle { RaycastTarget = true },
                ButtonData = new UIButtonDef
                {
                    Label = "Button",
                    FontSize = 13,
                    LabelColor = new ColorSer(1f, 0.85f, 0.5f, 1f),
                    NormalColor = new ColorSer(0.15f, 0.12f, 0.08f, 0.9f),
                    HighlightedColor = new ColorSer(0.4f, 0.3f, 0.15f, 0.95f),
                    PressedColor = new ColorSer(0.6f, 0.4f, 0.2f, 1f)
                }
            };
        }

        public static UIElementNode DangerButton()
        {
            return new UIElementNode
            {
                Id = GenerateId(),
                Name = "Danger Button",
                Type = UIElementType.Button,
                SizeDelta = new Vector2Ser(160, 36),
                Style = new UIElementStyle { RaycastTarget = true },
                ButtonData = new UIButtonDef
                {
                    Label = "Danger",
                    FontSize = 13,
                    LabelColor = new ColorSer(1f, 0.9f, 0.8f, 1f),
                    NormalColor = new ColorSer(0.5f, 0.12f, 0.08f, 0.9f),
                    HighlightedColor = new ColorSer(0.7f, 0.2f, 0.12f, 0.95f),
                    PressedColor = new ColorSer(0.85f, 0.25f, 0.15f, 1f)
                }
            };
        }

        public static UIElementNode GhostButton()
        {
            return new UIElementNode
            {
                Id = GenerateId(),
                Name = "Ghost Button",
                Type = UIElementType.Button,
                SizeDelta = new Vector2Ser(160, 36),
                Style = new UIElementStyle
                {
                    BackgroundColor = ColorSer.Clear,
                    RaycastTarget = true
                },
                ButtonData = new UIButtonDef
                {
                    Label = "Ghost",
                    FontSize = 13,
                    LabelColor = new ColorSer(1f, 0.85f, 0.5f, 0.8f),
                    NormalColor = new ColorSer(0, 0, 0, 0),
                    HighlightedColor = new ColorSer(0.3f, 0.25f, 0.12f, 0.3f),
                    PressedColor = new ColorSer(0.4f, 0.3f, 0.15f, 0.5f)
                }
            };
        }

        //  Panel presets

        public static UIElementNode TransparentPanel()
        {
            return new UIElementNode
            {
                Id = GenerateId(),
                Name = "Transparent Panel",
                Type = UIElementType.Panel,
                SizeDelta = new Vector2Ser(300, 200),
                Style = new UIElementStyle
                {
                    BackgroundColor = ColorSer.Clear,
                    RaycastTarget = false
                }
            };
        }

        public static UIElementNode DarkPanel()
        {
            return new UIElementNode
            {
                Id = GenerateId(),
                Name = "Dark Panel",
                Type = UIElementType.Panel,
                SizeDelta = new Vector2Ser(300, 200),
                Style = new UIElementStyle
                {
                    BackgroundColor = new ColorSer(0.04f, 0.04f, 0.06f, 0.9f),
                    RaycastTarget = true
                }
            };
        }

        public static UIElementNode CardPanel()
        {
            return new UIElementNode
            {
                Id = GenerateId(),
                Name = "Card Panel",
                Type = UIElementType.Panel,
                SizeDelta = new Vector2Ser(300, 200),
                Style = new UIElementStyle
                {
                    BackgroundColor = new ColorSer(0.08f, 0.08f, 0.1f, 0.92f),
                    BorderColor = new ColorSer(0.4f, 0.3f, 0.15f, 0.6f),
                    BorderWidth = 1,
                    RaycastTarget = true
                }
            };
        }

        //  Text presets

        public static UIElementNode TitleText()
        {
            return new UIElementNode
            {
                Id = GenerateId(),
                Name = "Title Text",
                Type = UIElementType.Text,
                SizeDelta = new Vector2Ser(300, 32),
                TextData = new UITextDef
                {
                    Text = "Title",
                    FontSize = 22,
                    FontCategory = "Primary",
                    FontStyle = 1, // Bold
                    Color = new ColorSer(1f, 0.85f, 0.5f, 1f),
                    Alignment = (int)TMPro.TextAlignmentOptions.Center
                },
                Style = new UIElementStyle { RaycastTarget = false }
            };
        }

        public static UIElementNode BodyText()
        {
            return new UIElementNode
            {
                Id = GenerateId(),
                Name = "Body Text",
                Type = UIElementType.Text,
                SizeDelta = new Vector2Ser(300, 40),
                TextData = new UITextDef
                {
                    Text = "Body text content goes here.",
                    FontSize = 14,
                    FontCategory = "Body",
                    Color = new ColorSer(0.9f, 0.88f, 0.82f, 1f),
                    Alignment = (int)TMPro.TextAlignmentOptions.TopLeft,
                    WordWrap = true
                },
                Style = new UIElementStyle { RaycastTarget = false }
            };
        }

        public static UIElementNode LabelText()
        {
            return new UIElementNode
            {
                Id = GenerateId(),
                Name = "Label Text",
                Type = UIElementType.Text,
                SizeDelta = new Vector2Ser(100, 20),
                TextData = new UITextDef
                {
                    Text = "Label",
                    FontSize = 11,
                    FontCategory = "Body",
                    Color = new ColorSer(0.6f, 0.5f, 0.35f, 0.8f),
                    Alignment = (int)TMPro.TextAlignmentOptions.MidlineLeft,
                    WordWrap = false
                },
                Style = new UIElementStyle { RaycastTarget = false }
            };
        }

        //  Game UI presets

        /// <summary>
        /// Inventory-style slot: 64x64 panel with icon image and amount text overlay.
        /// Matches Valheim's InventoryGrid slot structure.
        /// </summary>
        public static UIElementNode InventorySlot()
        {
            var slot = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Inventory Slot",
                Type = UIElementType.Panel,
                SizeDelta = new Vector2Ser(64, 64),
                Style = new UIElementStyle
                {
                    BackgroundColor = new ColorSer(0.06f, 0.06f, 0.08f, 0.8f),
                    BorderColor = new ColorSer(0.4f, 0.3f, 0.15f, 0.6f),
                    BorderWidth = 1,
                    RaycastTarget = true
                }
            };

            var icon = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Icon",
                Tag = "slot_icon",
                Type = UIElementType.Image,
                AnchorMin = new Vector2Ser(0, 0),
                AnchorMax = new Vector2Ser(1, 1),
                OffsetMin = new Vector2Ser(4, 4),
                OffsetMax = new Vector2Ser(-4, -4),
                SizeDelta = new Vector2Ser(0, 0),
                ImageData = new UIImageDef
                {
                    PreserveAspect = true,
                    Color = ColorSer.White
                },
                Style = new UIElementStyle { RaycastTarget = false }
            };
            slot.AddChild(icon);

            var amount = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Amount",
                Tag = "slot_amount",
                Type = UIElementType.Text,
                AnchorMin = new Vector2Ser(0, 0),
                AnchorMax = new Vector2Ser(1, 0),
                Pivot = new Vector2Ser(1, 0),
                AnchoredPosition = new Vector2Ser(-2, 2),
                SizeDelta = new Vector2Ser(0, 18),
                TextData = new UITextDef
                {
                    Text = "",
                    FontSize = 11,
                    FontCategory = "Body",
                    FontStyle = 1,
                    Color = ColorSer.White,
                    Alignment = (int)TMPro.TextAlignmentOptions.BottomRight,
                    WordWrap = false
                },
                Style = new UIElementStyle { RaycastTarget = false }
            };
            slot.AddChild(amount);

            return slot;
        }

        /// <summary>
        /// Horizontal stat bar with background, fill, and label.
        /// Common for HP, stamina, eitr, XP bars.
        /// </summary>
        public static UIElementNode StatBar()
        {
            var bar = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Stat Bar",
                Type = UIElementType.Panel,
                SizeDelta = new Vector2Ser(200, 24),
                Style = new UIElementStyle
                {
                    BackgroundColor = new ColorSer(0.08f, 0.08f, 0.1f, 0.85f),
                    RaycastTarget = false
                }
            };

            var fill = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Fill",
                Tag = "bar_fill",
                Type = UIElementType.Image,
                AnchorMin = new Vector2Ser(0, 0),
                AnchorMax = new Vector2Ser(0.75f, 1),
                SizeDelta = new Vector2Ser(0, 0),
                OffsetMin = new Vector2Ser(1, 1),
                OffsetMax = new Vector2Ser(-1, -1),
                ImageData = new UIImageDef
                {
                    Color = new ColorSer(0.4f, 0.7f, 0.3f, 0.9f)
                },
                Style = new UIElementStyle { RaycastTarget = false }
            };
            bar.AddChild(fill);

            var label = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Label",
                Tag = "bar_label",
                Type = UIElementType.Text,
                AnchorMin = new Vector2Ser(0, 0),
                AnchorMax = new Vector2Ser(1, 1),
                SizeDelta = new Vector2Ser(0, 0),
                TextData = new UITextDef
                {
                    Text = "75 / 100",
                    FontSize = 11,
                    FontCategory = "Body",
                    Color = ColorSer.White,
                    Alignment = (int)TMPro.TextAlignmentOptions.Center,
                    WordWrap = false
                },
                Style = new UIElementStyle { RaycastTarget = false }
            };
            bar.AddChild(label);

            return bar;
        }

        /// <summary>
        /// Tab-style button for tabbed interfaces.
        /// </summary>
        public static UIElementNode TabButton()
        {
            return new UIElementNode
            {
                Id = GenerateId(),
                Name = "Tab Button",
                Type = UIElementType.Button,
                SizeDelta = new Vector2Ser(100, 30),
                Style = new UIElementStyle { RaycastTarget = true },
                ButtonData = new UIButtonDef
                {
                    Label = "Tab",
                    FontSize = 12,
                    LabelColor = new ColorSer(0.8f, 0.7f, 0.5f, 1f),
                    NormalColor = new ColorSer(0.08f, 0.08f, 0.1f, 0.7f),
                    HighlightedColor = new ColorSer(0.15f, 0.12f, 0.08f, 0.9f),
                    PressedColor = new ColorSer(0.2f, 0.15f, 0.1f, 1f),
                    SelectedColor = new ColorSer(0.15f, 0.12f, 0.08f, 1f),
                    FadeDuration = 0.06f
                }
            };
        }

        /// <summary>
        /// Auto-sizing tooltip panel with title and body text.
        /// Uses ContentSizeFitter for dynamic height.
        /// </summary>
        public static UIElementNode TooltipPanel()
        {
            var panel = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Tooltip Panel",
                Type = UIElementType.Panel,
                AnchorMin = new Vector2Ser(0.5f, 0.5f),
                AnchorMax = new Vector2Ser(0.5f, 0.5f),
                SizeDelta = new Vector2Ser(240, 0),
                Style = new UIElementStyle
                {
                    BackgroundColor = new ColorSer(0.04f, 0.04f, 0.06f, 0.95f),
                    BorderColor = new ColorSer(0.4f, 0.3f, 0.15f, 0.7f),
                    BorderWidth = 1,
                    RaycastTarget = false
                },
                LayoutGroup = new UILayoutGroupDef
                {
                    IsVertical = true,
                    Spacing = 4,
                    Padding = new RectOffsetSer(10, 10, 8, 8),
                    ChildControlWidth = true,
                    ChildControlHeight = true,
                    ChildForceExpandWidth = true,
                    ChildForceExpandHeight = false
                },
                ContentFitter = new UIContentFitterDef
                {
                    HorizontalFit = 0,
                    VerticalFit = 2
                }
            };

            var title = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Title",
                Tag = "tooltip_title",
                Type = UIElementType.Text,
                TextData = new UITextDef
                {
                    Text = "Tooltip Title",
                    FontSize = 14,
                    FontCategory = "Primary",
                    FontStyle = 1,
                    Color = new ColorSer(1f, 0.85f, 0.5f, 1f),
                    Alignment = (int)TMPro.TextAlignmentOptions.TopLeft,
                    WordWrap = true
                },
                Style = new UIElementStyle { RaycastTarget = false }
            };
            panel.AddChild(title);

            var body = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Body",
                Tag = "tooltip_body",
                Type = UIElementType.Text,
                TextData = new UITextDef
                {
                    Text = "Tooltip description text goes here.",
                    FontSize = 12,
                    FontCategory = "Body",
                    Color = new ColorSer(0.85f, 0.83f, 0.78f, 1f),
                    Alignment = (int)TMPro.TextAlignmentOptions.TopLeft,
                    WordWrap = true
                },
                Style = new UIElementStyle { RaycastTarget = false }
            };
            panel.AddChild(body);

            return panel;
        }

        /// <summary>
        /// Full-width header bar with title text and close button.
        /// Anchored to stretch horizontally.
        /// </summary>
        public static UIElementNode HeaderBar()
        {
            var header = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Header Bar",
                Type = UIElementType.Panel,
                AnchorMin = new Vector2Ser(0, 1),
                AnchorMax = new Vector2Ser(1, 1),
                Pivot = new Vector2Ser(0.5f, 1),
                SizeDelta = new Vector2Ser(0, 36),
                Style = new UIElementStyle
                {
                    BackgroundColor = new ColorSer(0.06f, 0.06f, 0.08f, 0.95f),
                    RaycastTarget = true
                }
            };

            var title = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Title",
                Tag = "header_text",
                Type = UIElementType.Text,
                AnchorMin = new Vector2Ser(0, 0),
                AnchorMax = new Vector2Ser(1, 1),
                SizeDelta = new Vector2Ser(0, 0),
                OffsetMin = new Vector2Ser(12, 0),
                OffsetMax = new Vector2Ser(-40, 0),
                TextData = new UITextDef
                {
                    Text = "Panel Title",
                    FontSize = 16,
                    FontCategory = "Primary",
                    FontStyle = 1,
                    Color = new ColorSer(1f, 0.85f, 0.5f, 1f),
                    Alignment = (int)TMPro.TextAlignmentOptions.MidlineLeft,
                    WordWrap = false
                },
                Style = new UIElementStyle { RaycastTarget = false }
            };
            header.AddChild(title);

            var closeBtn = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Close",
                Tag = "close_button",
                Type = UIElementType.Button,
                AnchorMin = new Vector2Ser(1, 0.5f),
                AnchorMax = new Vector2Ser(1, 0.5f),
                Pivot = new Vector2Ser(1, 0.5f),
                AnchoredPosition = new Vector2Ser(-4, 0),
                SizeDelta = new Vector2Ser(28, 28),
                ButtonData = new UIButtonDef
                {
                    Label = "X",
                    FontSize = 14,
                    LabelColor = new ColorSer(1f, 0.9f, 0.8f, 1f),
                    NormalColor = new ColorSer(0.5f, 0.12f, 0.08f, 0.8f),
                    HighlightedColor = new ColorSer(0.7f, 0.2f, 0.12f, 0.95f),
                    PressedColor = new ColorSer(0.85f, 0.25f, 0.15f, 1f),
                    ClickAction = "close_panel"
                },
                LayoutElement = new UILayoutElementDef { IgnoreLayout = true },
                Style = new UIElementStyle { RaycastTarget = true }
            };
            header.AddChild(closeBtn);

            return header;
        }

        /// <summary>
        /// Two-column layout: horizontal layout group with two equal vertical panels.
        /// </summary>
        public static UIElementNode TwoColumnLayout()
        {
            var container = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Two Columns",
                Type = UIElementType.Panel,
                SizeDelta = new Vector2Ser(500, 300),
                Style = new UIElementStyle
                {
                    BackgroundColor = ColorSer.Clear,
                    RaycastTarget = false
                },
                LayoutGroup = new UILayoutGroupDef
                {
                    IsVertical = false,
                    Spacing = 8,
                    Padding = new RectOffsetSer(0, 0, 0, 0),
                    ChildControlWidth = true,
                    ChildControlHeight = true,
                    ChildForceExpandWidth = true,
                    ChildForceExpandHeight = true
                }
            };

            var leftCol = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Left Column",
                Type = UIElementType.Panel,
                Style = new UIElementStyle
                {
                    BackgroundColor = new ColorSer(0.08f, 0.08f, 0.1f, 0.5f),
                    RaycastTarget = false
                },
                LayoutGroup = new UILayoutGroupDef
                {
                    IsVertical = true,
                    Spacing = 4,
                    Padding = new RectOffsetSer(8, 8, 8, 8),
                    ChildControlWidth = true,
                    ChildControlHeight = false,
                    ChildForceExpandWidth = true
                },
                LayoutElement = new UILayoutElementDef { FlexibleWidth = 1 }
            };
            container.AddChild(leftCol);

            var rightCol = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Right Column",
                Type = UIElementType.Panel,
                Style = new UIElementStyle
                {
                    BackgroundColor = new ColorSer(0.08f, 0.08f, 0.1f, 0.5f),
                    RaycastTarget = false
                },
                LayoutGroup = new UILayoutGroupDef
                {
                    IsVertical = true,
                    Spacing = 4,
                    Padding = new RectOffsetSer(8, 8, 8, 8),
                    ChildControlWidth = true,
                    ChildControlHeight = false,
                    ChildForceExpandWidth = true
                },
                LayoutElement = new UILayoutElementDef { FlexibleWidth = 1 }
            };
            container.AddChild(rightCol);

            return container;
        }

        /// <summary>
        /// Card grid: GridLayoutGroup container with sample card cells.
        /// Matches inventory/crafting grid patterns from Valheim's InventoryGrid.
        /// </summary>
        public static UIElementNode CardGrid()
        {
            var grid = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Card Grid",
                Type = UIElementType.Panel,
                SizeDelta = new Vector2Ser(400, 300),
                Style = new UIElementStyle
                {
                    BackgroundColor = ColorSer.Clear,
                    RaycastTarget = false
                },
                GridLayoutGroup = new UIGridLayoutGroupDef
                {
                    CellSize = new Vector2Ser(80, 100),
                    Spacing = new Vector2Ser(4, 4),
                    StartCorner = 0,
                    StartAxis = 0,
                    ChildAlignment = 0,
                    Constraint = 0,
                    ConstraintCount = 4,
                    Padding = new RectOffsetSer(4, 4, 4, 4)
                }
            };

            for (int i = 0; i < 4; i++)
            {
                var card = new UIElementNode
                {
                    Id = GenerateId(),
                    Name = $"Card {i + 1}",
                    Type = UIElementType.Panel,
                    Style = new UIElementStyle
                    {
                        BackgroundColor = new ColorSer(0.08f, 0.08f, 0.1f, 0.85f),
                        BorderColor = new ColorSer(0.3f, 0.25f, 0.15f, 0.5f),
                        BorderWidth = 1,
                        RaycastTarget = true
                    }
                };
                grid.AddChild(card);
            }

            return grid;
        }

        /// <summary>
        /// Icon button with image and optional label below.
        /// Common for toolbar/action bar buttons.
        /// </summary>
        public static UIElementNode IconButton()
        {
            var btn = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Icon Button",
                Type = UIElementType.Button,
                SizeDelta = new Vector2Ser(48, 48),
                Style = new UIElementStyle { RaycastTarget = true },
                ButtonData = new UIButtonDef
                {
                    Label = "",
                    NormalColor = new ColorSer(0.1f, 0.1f, 0.12f, 0.8f),
                    HighlightedColor = new ColorSer(0.25f, 0.2f, 0.12f, 0.9f),
                    PressedColor = new ColorSer(0.4f, 0.3f, 0.15f, 1f),
                    FadeDuration = 0.06f
                }
            };

            var icon = new UIElementNode
            {
                Id = GenerateId(),
                Name = "Icon",
                Tag = "button_icon",
                Type = UIElementType.Image,
                AnchorMin = new Vector2Ser(0, 0),
                AnchorMax = new Vector2Ser(1, 1),
                SizeDelta = new Vector2Ser(0, 0),
                OffsetMin = new Vector2Ser(6, 6),
                OffsetMax = new Vector2Ser(-6, -6),
                ImageData = new UIImageDef
                {
                    PreserveAspect = true,
                    Color = new ColorSer(1f, 0.85f, 0.5f, 1f)
                },
                Style = new UIElementStyle { RaycastTarget = false }
            };
            btn.AddChild(icon);

            return btn;
        }
    }
}
