using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Centralized font and style configuration for all UI elements.
    /// 
    /// HOW TO USE:
    /// 1. Find the UI section you want to change below
    /// 2. Modify the FontStyle, FontSize, or TextColor
    /// 3. Available FontStyle options:
    ///    - FontStyle.Valheim (default game font from HUD - Valheim-Prstartk)
    ///    - FontStyle.AveriaSerif   (Valheim's main UI font - Valheim-AveriaSerifLibre)
    ///    - FontStyle.Norse         (Viking runic style - Valheim-Norsebold)
    ///    - FontStyle.AveriaLibre   (Clean readable font - Valheim-AveriaSansLibre)
    ///    - FontStyle.Rune          (Runic symbols - Valheim-Rune)
    /// 
    /// FONT CATEGORIES (configurable via BepInEx config):
    /// - Primary: Headers, titles, main UI text (default: Valheim/Prstartk)
    /// - Decorative: Quest titles, Viking-themed headers (default: Norse)
    /// - Body: Descriptions, buttons, readable text (default: AveriaSerif)
    /// </summary>
    public static class UIFontConfig
    {
        //=============================================================================
        // FONT STYLE OPTIONS
        //=============================================================================
        public enum FontStyle
        {
            Valheim,        // Default Valheim game font (from Hud - Valheim-Prstartk)
            AveriaSerif,    // Valheim's main UI font - Valheim-AveriaSerifLibre (most UI text)
            Norse,          // Viking runic style - Valheim-Norsebold (decorative headers)
            AveriaLibre,    // Clean readable font - Valheim-AveriaSansLibre
            Rune            // Runic symbols font - Valheim-Rune
        }
        
        //=============================================================================
        // FONT CATEGORY ENUM - Maps to config entries
        //=============================================================================
        /// <summary>
        /// Font categories that can be configured via BepInEx config.
        /// Each category maps to a config dropdown allowing users to swap fonts.
        /// </summary>
        public enum FontCategory
        {
            /// <summary>Primary font for headers, titles, main UI text (default: Valheim)</summary>
            Primary,
            /// <summary>Decorative font for quest titles, Viking-themed headers (default: Norse)</summary>
            Decorative,
            /// <summary>Body font for descriptions, buttons, readable text (default: AveriaSerif)</summary>
            Body
        }
        
        //=============================================================================
        // CONFIGURABLE FONT OVERRIDES
        //=============================================================================
        
        /// <summary>
        /// Gets the configured font style for a given category.
        /// Reads the configured choice via UIBuilderHost.ConfiguredFontProvider.
        /// Falls back to defaults if config is not available.
        /// </summary>
        public static FontStyle GetConfiguredFont(FontCategory category)
        {
            try
            {
                var configured = UIBuilderHost.ConfiguredFontProvider?.Invoke(category);
                return configured ?? GetDefaultFont(category);
            }
            catch
            {
                return GetDefaultFont(category);
            }
        }
        
        /// <summary>
        /// Gets the default font for a category (used when config is not available).
        /// </summary>
        private static FontStyle GetDefaultFont(FontCategory category)
        {
            return category switch
            {
                FontCategory.Primary => FontStyle.AveriaLibre,
                FontCategory.Decorative => FontStyle.Norse,
                FontCategory.Body => FontStyle.AveriaSerif,
                _ => FontStyle.AveriaLibre
            };
        }
        
        /// <summary>
        /// Gets the effective font style, applying config overrides.
        /// Call this instead of directly using the Font property on UITextStyle.
        /// </summary>
        public static FontStyle GetEffectiveFont(FontStyle originalFont)
        {
            // Map original font to category, then get configured font for that category
            FontCategory category = originalFont switch
            {
                FontStyle.Valheim => FontCategory.Primary,
                FontStyle.Norse => FontCategory.Decorative,
                FontStyle.AveriaSerif => FontCategory.Body,
                FontStyle.AveriaLibre => FontCategory.Body,
                FontStyle.Rune => FontCategory.Decorative, // Rune is decorative
                _ => FontCategory.Primary
            };
            
            return GetConfiguredFont(category);
        }

        //=============================================================================
        // QUEST INTERACTION SCREEN - Main quest UI when talking to NPCs
        //=============================================================================
  
        /// <summary>NPC Name shown in the panel header (e.g., "Quest Giver")</summary>
  public static readonly UITextStyle NpcNameHeader = new UITextStyle
      {
            Font = FontStyle.Valheim,  // Retro 80's font
            Size = 26f,
            Color = new Color(1f, 0.9f, 0.6f),  // Bright gold
            Bold = true
        };

        /// <summary>"Available Quests" title above the quest list</summary>
        public static readonly UITextStyle AvailableQuestsTitle = new UITextStyle
   {
            Font = FontStyle.Norse,  // Keep Viking runic style
            Size = 20f,
            Color = new Color(0.9f, 0.75f, 0.45f),  // Warm gold
            Bold = true
        };

        /// <summary>Quest name when selected (shown above description)</summary>
      public static readonly UITextStyle SelectedQuestName = new UITextStyle
        {
             Font = FontStyle.Norse,   // Keep Viking runic style
             Size = 20f,
             Color = new Color(1f, 0.85f, 0.5f),  // Gold
             Bold = true
        };

     /// <summary>Quest names in the quest list</summary>
        public static readonly UITextStyle QuestListItem = new UITextStyle
   {
            Font = FontStyle.Valheim,
            Size = 12f,
            Color = new Color(1f, 0.85f, 0.5f),  // Gold
            Bold = false
     };

        /// <summary>Quest description text</summary>
    public static readonly UITextStyle QuestDescription = new UITextStyle
    {
            Font = FontStyle.Valheim,
            Size = 11f,
            Color = new Color(1f, 0.85f, 0.5f),  // Gold
            Bold = false
   };

        /// <summary>Status text ([Active], [Done], etc.)</summary>
        public static readonly UITextStyle QuestStatus = new UITextStyle
        {
          Font = FontStyle.Valheim,
          Size = 8f,
        Color = new Color(0.4f, 1f, 0.4f),  // Green
       Bold = false
        };

        /// <summary>Button text (Accept, Cancel)</summary>
        public static readonly UITextStyle ButtonText = new UITextStyle
        {
         Font = FontStyle.AveriaSerif,
         Size = 12f,
         Color = new Color(1f, 0.85f, 0.5f),  // Gold
         Bold = true
        };

        //=============================================================================
        // ADMIN PANEL - Shift+E configuration screen
        //=============================================================================

        /// <summary>Admin panel header (e.g., "Configure NPC: Name")</summary>
        public static readonly UITextStyle AdminPanelHeader = new UITextStyle
        {
           Font = FontStyle.Valheim,  // Retro 80's font
           Size = 22f,
           Color = new Color(1f, 0.9f, 0.6f),  // Bright gold
           Bold = true
   };

        /// <summary>Tab button text</summary>
public static readonly UITextStyle TabText = new UITextStyle
        {
         Font = FontStyle.Valheim,
         Size = 12f,
         Color = new Color(0.8f, 0.65f, 0.4f),  // Muted warm
         Bold = false
        };

  /// <summary>Tab button text when active</summary>
  public static readonly UITextStyle TabTextActive = new UITextStyle
        {
          Font = FontStyle.Valheim,
          Size = 12f,
          Color = new Color(1f, 0.9f, 0.6f),  // Bright gold
          Bold = true
        };

        //=============================================================================
        // INFO NPC SCREEN
        //=============================================================================

        /// <summary>Info NPC title/header</summary>
      public static readonly UITextStyle InfoNpcHeader = new UITextStyle
 {
            Font = FontStyle.Valheim,  // Retro 80's font
            Size = 20f,
            Color = new Color(1f, 0.85f, 0.5f),
            Bold = true
 };

        /// <summary>Info NPC body text</summary>
        public static readonly UITextStyle InfoNpcBody = new UITextStyle
        {
            Font = FontStyle.Valheim,
            Size = 14f,
            Color = new Color(0.9f, 0.85f, 0.7f),
            Bold = false
        };

        //=============================================================================
        // COMMON COLORS - Reusable color definitions
        //=============================================================================

     public static class Colors
     {
        // Gold/Orange theme
         public static readonly Color BrightGold = new Color(1f, 0.9f, 0.6f);
         public static readonly Color Gold = new Color(1f, 0.85f, 0.5f);
         public static readonly Color WarmGold = new Color(0.9f, 0.75f, 0.45f);
         public static readonly Color MutedGold = new Color(0.8f, 0.65f, 0.4f);
         public static readonly Color DarkGold = new Color(0.6f, 0.45f, 0.25f);
         public static readonly Color LabelGold = new Color(0.9f, 0.75f, 0.45f);

         // Status colors
         public static readonly Color Green = new Color(0.4f, 1f, 0.4f);
         public static readonly Color Yellow = new Color(1f, 0.9f, 0.4f);
         public static readonly Color Red = new Color(1f, 0.4f, 0.4f);
         public static readonly Color Blue = new Color(0.4f, 0.6f, 1f);
         public static readonly Color Gray = new Color(0.7f, 0.7f, 0.7f);
         public static readonly Color LightGray = new Color(0.81f, 0.81f, 0.81f); // #CFCFCF
         public static readonly Color CooldownBlue = new Color(0.67f, 0.67f, 1f); // #AAAAFF

         // Background colors
         public static readonly Color PanelBackground = new Color(0.04f, 0.04f, 0.06f, 0.85f);
         public static readonly Color PanelBackgroundLight = new Color(0.04f, 0.04f, 0.06f, 0.8f);
         public static readonly Color PanelBackgroundDim = new Color(0.04f, 0.04f, 0.06f, 0.6f);
      
         // Section backgrounds for improved readability (used for layout groups, form sections, etc.)
         public static readonly Color SectionBackground = new Color(0.02f, 0.02f, 0.04f, 0.7f);
         public static readonly Color SectionBackgroundLight = new Color(0.04f, 0.04f, 0.06f, 0.6f);
         public static readonly Color SectionBackgroundDark = new Color(0.01f, 0.01f, 0.02f, 0.8f);
         public static readonly Color HeaderBackground = new Color(0.03f, 0.03f, 0.05f, 0.75f);
         public static readonly Color TabBarBackground = new Color(0.03f, 0.03f, 0.05f, 0.65f);
         public static readonly Color FormSectionBackground = new Color(0.02f, 0.02f, 0.04f, 0.65f);
  
         // List item colors
         public static readonly Color ItemNormal = new Color(0.08f, 0.08f, 0.1f, 0.7f);
         public static readonly Color ItemSelected = new Color(0.4f, 0.3f, 0.15f, 0.9f);
         public static readonly Color ItemHover = new Color(0.15f, 0.12f, 0.08f, 0.8f);

         // Button colors
         public static readonly Color ButtonNormal = new Color(0.15f, 0.12f, 0.08f, 0.9f);
         public static readonly Color ButtonHover = new Color(0.4f, 0.3f, 0.15f, 0.95f);
         public static readonly Color ButtonPressed = new Color(0.6f, 0.4f, 0.2f, 1f);
         public static readonly Color ButtonDisabled = new Color(0.1f, 0.1f, 0.1f, 0.5f);

         // Scrollbar colors
         public static readonly Color ScrollbarHandle = new Color(0.6f, 0.45f, 0.25f, 0.9f);
         public static readonly Color ScrollbarHandleHover = new Color(0.7f, 0.55f, 0.35f, 1f);
         public static readonly Color ScrollbarHandlePressed = new Color(0.8f, 0.6f, 0.4f, 1f);
         public static readonly Color ScrollbarBackground = new Color(0.15f, 0.15f, 0.18f, 0.5f);

         // Placeholder text
         public static readonly Color PlaceholderText = new Color(0.4f, 0.35f, 0.25f, 0.6f);

         // Quest status colors (with alpha variants)
         public static readonly Color QuestActive = new Color(1f, 0.9f, 0.4f);
         public static readonly Color QuestCompleted = new Color(0.4f, 1f, 0.4f);
         public static readonly Color QuestCompletedFaded = new Color(0.4f, 1f, 0.4f, 0.7f);

   // Rich text hex color codes for use in TMP markup
        // UPDATED: Colors optimized for parchment/sepia background
        public static class Hex
     {
          public const string Gold = "#8B4513";        // Saddle brown - high contrast on parchment
          public const string BrightGold = "#704214";  // Dark brown - headers
          public const string Description = "#5C4033"; // Dark brownish - body text
          public const string Gray = "#4A4A4A";        // Dark gray - secondary text
          public const string Red = "#8B0000";         // Dark red - danger/health low
          public const string Yellow = "#B8860B";      // Dark goldenrod - warnings
          public const string Green = "#228B22";       // Forest green - positive
          public const string Blue = "#1E4D8C";        // Dark blue - info
          public const string Orange = "#D2691E";      // Chocolate - highlights
          public const string CooldownBlue = "#4169E1"; // Royal blue - cooldowns
          public const string Purple = "#6B238E";      // Purple for special abilities
          public const string LightText = "#3D2914";   // Light readable text on parchment
          public const string DarkText = "#2C1810";    // Very dark text for emphasis
        }
   }

      //=============================================================================
  // INTERNAL - Font application methods (don't modify unless you know what you're doing)
  //=============================================================================

        private static Dictionary<FontStyle, TMP_FontAsset> _fontCache = new Dictionary<FontStyle, TMP_FontAsset>();

        /// <summary>
        /// Applies a UITextStyle to a TMP_Text component.
        /// ALWAYS ensures a font is set to prevent TMP warnings.
        /// Uses configured font overrides from BepInEx config.
        /// </summary>
        public static void ApplyStyle(TMP_Text text, UITextStyle style)
        {
            if (text == null) return;

            // Get effective font (applies config overrides)
            FontStyle effectiveFont = GetEffectiveFont(style.Font);

            // CRITICAL: Always ensure a font is set to prevent TMP "Font Asset was not found" warnings
            // Try requested font first, then fallbacks
            var font = GetFont(effectiveFont);
            if (font != null)
            {
                text.font = font;
            }
            else
            {
                // Font not available - try ANY font to prevent warnings
                var fallbackFont = GetAnyAvailableFont();
                if (fallbackFont != null)
                {
                    text.font = fallbackFont;
                }
                // If still no font, TMP will show warnings - but we tried everything
            }

            // Apply size
            text.fontSize = style.Size;

            // Apply color
            text.color = style.Color;

            // Apply bold
            text.fontStyle = style.Bold ? FontStyles.Bold : FontStyles.Normal;
        }

        /// <summary>
        /// Gets the TMP_FontAsset for a given font style.
        /// </summary>
        public static TMP_FontAsset GetFont(FontStyle style)
        {
            // Check cache first
            if (_fontCache.TryGetValue(style, out var cached) && cached != null)
                return cached;

            TMP_FontAsset font = null;

            switch (style)
            {
                case FontStyle.Valheim:
                    font = GetValheimFont();
                    break;

                case FontStyle.AveriaSerif:
                    font = GetFontByName("Valheim-AveriaSerifLibre")
                        ?? GetFontByName("AveriaSerifLibre");
                    break;

                case FontStyle.Norse:
                    font = GetFontByName("Valheim-Norsebold")
                        ?? GetFontByName("Norsebold");
                    break;

                case FontStyle.AveriaLibre:
                    // AveriaSans has multiple possible names in different Valheim versions
                    font = GetFontByName("Valheim-AveriaSansLibre")
                        ?? GetFontByName("AveriaSansLibre-Bold SDF")
                        ?? GetFontByName("AveriaSansLibre")
                        ?? GetFontByName("Averia");
                    break;

                case FontStyle.Rune:
                    font = GetFontByName("Valheim-Rune")
                        ?? GetFontByName("Rune");
                    break;
            }

            // Fallback to Valheim font if not found
            if (font == null && style != FontStyle.Valheim)
            {
                Debug.LogWarning($"[UIFontConfig] Font '{style}' not found, falling back to Valheim font");
                font = GetValheimFont();
            }

            // Cache it
            if (font != null)
                _fontCache[style] = font;

            return font;
        }

        /// <summary>
        /// Gets the default Valheim font from the HUD.
        /// </summary>
        private static TMP_FontAsset GetValheimFont()
        {
            try
            {
                if (Hud.instance != null)
                {
                    var hudText = Hud.instance.GetComponentInChildren<TextMeshProUGUI>(true);
                    if (hudText?.font != null)
                        return hudText.font;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Searches for a font by name in loaded resources.
        /// </summary>
        private static TMP_FontAsset GetFontByName(string fontName)
        {
            if (string.IsNullOrEmpty(fontName)) return null;

            try
            {
                var allFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();

                // First pass: exact match
                foreach (var font in allFonts)
                {
                    if (font != null && font.name != null)
                    {
                        if (font.name.Equals(fontName, System.StringComparison.OrdinalIgnoreCase))
                        {
                            return font;
                        }
                    }
                }

                // Second pass: starts with match
                foreach (var font in allFonts)
                {
                    if (font != null && font.name != null)
                    {
                        if (font.name.StartsWith(fontName, System.StringComparison.OrdinalIgnoreCase))
                        {
                            return font;
                        }
                    }
                }
                
                // Third pass: contains match
                foreach (var font in allFonts)
                {
                    if (font != null && font.name != null)
                    {
                        if (font.name.IndexOf(fontName, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return font;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[UIFontConfig] Error finding font '{fontName}': {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Event raised when font configuration changes.
        /// UI screens can subscribe to this to refresh their fonts dynamically.
        /// </summary>
        public static event System.Action OnFontConfigChanged;
        
        /// <summary>
        /// Clears the font cache (useful if fonts are reloaded).
        /// Also clears any cached config values so they're re-read.
        /// Raises the OnFontConfigChanged event to notify UI screens.
        /// </summary>
        public static void ClearCache()
        {
            _fontCache.Clear();
            
            // Notify listeners that font config has changed
            try
            {
                OnFontConfigChanged?.Invoke();
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[UIFontConfig] Error notifying font config listeners: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Reapplies configured fonts to all TMP_Text components in a hierarchy.
        /// Call this when font configuration changes to update existing UI.
        /// Uses each text's current UITextStyle if tracked, otherwise applies Primary font.
        /// </summary>
        public static void RefreshFontsInHierarchy(GameObject root)
        {
            if (root == null) return;
            
            foreach (var tmp in root.GetComponentsInChildren<TMP_Text>(true))
            {
                if (tmp == null) continue;
                
                // Try to determine the appropriate font category based on current font
                // If the text has a Valheim/Norse font, keep it as Primary/Decorative
                // Otherwise default to Primary
                var category = FontCategory.Primary;
                if (tmp.font != null)
                {
                    var fontName = tmp.font.name ?? "";
                    if (fontName.Contains("Norse") || fontName.Contains("Rune"))
                        category = FontCategory.Decorative;
                    else if (fontName.Contains("AveriaSerif"))
                        category = FontCategory.Body;
                }
                
                var font = GetFontForCategory(category);
                if (font != null)
                {
                    tmp.font = font;
                }
            }
        }
        
        /// <summary>
        /// Gets the TMP_FontAsset for a font category using config overrides.
        /// Convenience method for getting fonts by category.
        /// </summary>
        public static TMP_FontAsset GetFontForCategory(FontCategory category)
        {
            FontStyle effectiveFont = GetConfiguredFont(category);
            return GetFont(effectiveFont);
        }
        
        /// <summary>
        /// Returns a description of current font configuration for debugging.
        /// </summary>
        public static string GetFontConfigSummary()
        {
            return $"Primary: {GetConfiguredFont(FontCategory.Primary)}, " +
                   $"Decorative: {GetConfiguredFont(FontCategory.Decorative)}, " +
                   $"Body: {GetConfiguredFont(FontCategory.Body)}";
        }

        /// <summary>
        /// Gets any available TMP font as a fallback when the preferred font isn't loaded yet.
        /// </summary>
        private static TMP_FontAsset GetAnyAvailableFont()
        {
            try
            {
                // Try to find any loaded TMP font
                var allFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
                foreach (var font in allFonts)
                {
                    if (font != null && !string.IsNullOrEmpty(font.name))
                    {
                        // Prefer Valheim fonts over fallback fonts
                        if (font.name.Contains("Valheim") || font.name.Contains("Averia") || font.name.Contains("Norse"))
                        {
                            return font;
                        }
                    }
                }
                
                // Return any font if no Valheim font found
                foreach (var font in allFonts)
                {
                    if (font != null)
                        return font;
                }
            }
            catch { }
            
            return null;
        }

        /// <summary>
        /// Logs all available TMP fonts for debugging.
        /// </summary>
        public static void LogAvailableFonts()
        {
            try
            {
              var allFonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
     Debug.Log($"[UIFontConfig] Available TMP Fonts ({allFonts.Length}):");
           foreach (var font in allFonts)
        {
     if (font != null)
             Debug.Log($"  - {font.name}");
             }
 }
          catch (System.Exception ex)
      {
     Debug.LogWarning($"[UIFontConfig] Error logging fonts: {ex.Message}");
   }
        }
    }

    /// <summary>
    /// Defines the style for a UI text element.
    /// </summary>
    public class UITextStyle
    {
     public UIFontConfig.FontStyle Font { get; set; } = UIFontConfig.FontStyle.Valheim;
        public float Size { get; set; } = 12f;
        public Color Color { get; set; } = Color.white;
        public bool Bold { get; set; } = false;
    }
}

/*
=============================================================================
AVAILABLE FONTS REFERENCE 
=============================================================================

VALHEIM CUSTOM FONTS (use these):
  - Valheim-Prstartk           <- Default HUD font (FontStyle.Valheim)
  - Valheim-AveriaSerifLibre   <- Main UI font, elegant (FontStyle.AveriaSerif)
  - Valheim-AveriaSansLibre    <- Sans-serif variant (FontStyle.AveriaLibre)
  - Valheim-Norsebold          <- Viking decorative (FontStyle.Norse)
  - Valheim-Norse              <- Lighter Norse variant
  - Valheim-Rune               <- Runic symbols (FontStyle.Rune)
  - Norsebold SDFYellow        <- Norse with yellow tint
  - AveriaSansLibre-Bold SDF   <- Another Averia variant

FALLBACK FONTS:
  - LiberationSans SDF
  - LiberationSans SDF - Fallback
  - Fallback-NotoSerifNormal
  - Fallback-NotoSansNormal
  - Fallback-NotoSansThin

INTERNATIONAL LANGUAGE SUPPORT     (Noto fonts):
  - NotoSansJP-Regular SDF         (Japanese)
  - NotoSansSC-Regular SDF         (Simplified Chinese)
  - NotoSansKR-Regular SDF         (Korean)
  - NotoSansThai-Regular SDF       (Thai)
  - NotoSansArabic-Regular SDF     (Arabic)
  - NotoSansHebrew-Regular SDF     (Hebrew)
  - NotoSansBengali-Regular SDF
  - NotoSansDevanagari-Regular SDF
  - NotoSansGeorgian-Regular SDF
  - NotoSansArmenian-Regular SDF
  - NotoSansMalayalam-Regular SDF
  - NotoSerifJP-Regular SDF
  - NotoSerifSC-Regular SDF
  - NotoSerifKR-Regular SDF
  - NotoSerifThai-Regular SDF
  - NotoSerifArmenian-Regular SDF
  - NotoSerifDevanagari-Regular SDF
  - NotoSerifGeorgian-Regular SDF
  - NotoSerifMalayalam-Regular SDF
  - NotoSerifBengali-Regular SDF
  - NotoEmoji-Regular SDF


=============================================================================
*/