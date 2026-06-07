using UnityEngine;
using System;

namespace FiresCore.Npc.Archetypes.StatusEffects
{
    /// <summary>
    /// Base class for all companion-related status effects.
    /// Provides common functionality like icons, logging, and duration management.
    /// 
    /// HUD DISPLAY:
    /// Status effects from companions will display in the player's HUD with:
    /// - A colored icon (fallback colored square if no sprite provided)
    /// - Duration countdown
    /// - Tooltip showing effect name and description
    /// 
    /// ORGANIZATION:
    /// Status effects are organized by archetype in subfolders:
    /// - Tank/        - Taunt, Fortify, etc.
    /// - Berserker/   - Berserk, Immunity, etc.
    /// - Rogue/       - Caltrops (slowdown), Stealth, etc.
    /// - Healer/      - Purify, Regen, etc.
    /// - Common/      - Shared effects used by multiple archetypes
    /// </summary>
    public abstract class CompanionStatusEffectBase : StatusEffect
    {
        /// <summary>Duration of the effect in seconds.</summary>
        public float Duration { get; set; } = 10f;
        
        /// <summary>Icon to display in HUD.</summary>
        public Sprite EffectIcon { get; set; }
        
        /// <summary>Verbose logging for debugging.</summary>
        public static bool VerboseLogging = false;
        
        /// <summary>The character who applied this effect (if applicable).</summary>
        public Character SourceCharacter { get; set; }
        
        /// <summary>
        /// Friendly display name for the tooltip. Override in derived classes.
        /// </summary>
        public virtual string DisplayName => GetDisplayNameFromEffectName(name);
        
        /// <summary>
        /// Description shown in tooltip. Override in derived classes.
        /// </summary>
        public virtual string Description => "Companion ability effect.";
        
        public override void Setup(Character character)
        {
            base.Setup(character);
            m_ttl = Duration;
            m_icon = EffectIcon;
            
            // Set tooltip info for HUD display
            m_name = DisplayName;
            m_tooltip = Description;
            
            OnEffectApplied();
            
            if (VerboseLogging && m_character != null)
            {
                Debug.Log($"[{GetType().Name}] Applied to {m_character.m_name}, Duration: {Duration}s");
            }
        }
        
        public override void Stop()
        {
            base.Stop();
            
            OnEffectRemoved();
            
            if (VerboseLogging && m_character != null)
            {
                Debug.Log($"[{GetType().Name}] Removed from {m_character.m_name}");
            }
        }
        
        /// <summary>
        /// Called when the effect is first applied.
        /// Override in derived classes for custom setup logic.
        /// </summary>
        protected virtual void OnEffectApplied() { }
        
        /// <summary>
        /// Called when the effect is removed.
        /// Override in derived classes for custom cleanup logic.
        /// </summary>
        protected virtual void OnEffectRemoved() { }
        
        /// <summary>
        /// Creates a simple colored icon texture as a fallback.
        /// Creates a rounded rectangle with a border for better visibility.
        /// </summary>
        public static Sprite CreateFallbackIcon(Color fillColor)
        {
            int size = 64;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            texture.filterMode = FilterMode.Bilinear;
            
            Color borderColor = new Color(0.1f, 0.1f, 0.1f, 1f); // Dark border
            Color innerBorderColor = new Color(
                Mathf.Min(fillColor.r + 0.3f, 1f),
                Mathf.Min(fillColor.g + 0.3f, 1f),
                Mathf.Min(fillColor.b + 0.3f, 1f),
                1f
            ); // Lighter inner border
            
            int borderWidth = 3;
            int innerBorderWidth = 2;
            int cornerRadius = 8;
            
            Color[] pixels = new Color[size * size];
            
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int idx = y * size + x;
                    
                    // Calculate distance from corners for rounded effect
                    float distFromEdge = GetDistanceFromRoundedEdge(x, y, size, cornerRadius);
                    
                    if (distFromEdge < 0)
                    {
                        // Outside rounded corners - transparent
                        pixels[idx] = Color.clear;
                    }
                    else if (distFromEdge < borderWidth)
                    {
                        // Outer border
                        pixels[idx] = borderColor;
                    }
                    else if (distFromEdge < borderWidth + innerBorderWidth)
                    {
                        // Inner border (highlight)
                        pixels[idx] = innerBorderColor;
                    }
                    else
                    {
                        // Fill
                        pixels[idx] = fillColor;
                    }
                }
            }
            
            texture.SetPixels(pixels);
            texture.Apply();
            
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }
        
        /// <summary>
        /// Helper for rounded rectangle calculation.
        /// </summary>
        private static float GetDistanceFromRoundedEdge(int x, int y, int size, int cornerRadius)
        {
            int right = size - 1 - x;
            int top = size - 1 - y;
            
            // Check if in corner region
            bool inLeftCorner = x < cornerRadius;
            bool inRightCorner = right < cornerRadius;
            bool inBottomCorner = y < cornerRadius;
            bool inTopCorner = top < cornerRadius;
            
            // Calculate distance from edge
            float distFromEdge;
            
            if ((inLeftCorner && inBottomCorner))
            {
                // Bottom-left corner
                float dx = cornerRadius - x;
                float dy = cornerRadius - y;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                distFromEdge = cornerRadius - dist;
            }
            else if ((inRightCorner && inBottomCorner))
            {
                // Bottom-right corner
                float dx = cornerRadius - right;
                float dy = cornerRadius - y;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                distFromEdge = cornerRadius - dist;
            }
            else if ((inLeftCorner && inTopCorner))
            {
                // Top-left corner
                float dx = cornerRadius - x;
                float dy = cornerRadius - top;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                distFromEdge = cornerRadius - dist;
            }
            else if ((inRightCorner && inTopCorner))
            {
                // Top-right corner
                float dx = cornerRadius - right;
                float dy = cornerRadius - top;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                distFromEdge = cornerRadius - dist;
            }
            else
            {
                // Not in corner - use minimum distance from any edge
                distFromEdge = Mathf.Min(Mathf.Min(x, right), Mathf.Min(y, top));
            }
            
            return distFromEdge;
        }
        
        /// <summary>
        /// Converts an effect name like "CompanionBerserkRage" to "Berserk Rage".
        /// </summary>
        protected static string GetDisplayNameFromEffectName(string effectName)
        {
            if (string.IsNullOrEmpty(effectName)) return "Unknown Effect";
            
            // Remove "Companion" prefix
            if (effectName.StartsWith("Companion"))
            {
                effectName = effectName.Substring(9);
            }
            
            // Add spaces before capital letters
            var result = new System.Text.StringBuilder();
            foreach (char c in effectName)
            {
                if (char.IsUpper(c) && result.Length > 0)
                {
                    result.Append(' ');
                }
                result.Append(c);
            }
            
            return result.ToString();
        }
    }
}
