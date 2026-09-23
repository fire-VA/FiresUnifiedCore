using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// How big the config windows draw: a user factor times, optionally, Valheim's own GUI scale
    /// (Accessibility → Scale GUI), matching how ConfigurationManager sizes itself.
    ///
    /// The factor is applied as real numbers — scaled font sizes via <see cref="ConfigSkin"/> and scaled
    /// layout sizes via <see cref="Px"/> — NOT as a <c>GUI.matrix</c>. A matrix would scale the whole window
    /// in one line, but IMGUI renders text into a font atlas at the style's own size and the matrix only
    /// stretches that result, so every glyph comes out soft. Everything stays in real screen pixels this way,
    /// which also keeps mouse coordinates honest.
    /// </summary>
    internal static class ConfigWindowScale
    {
        /// <summary>Range of the user's own scale setting.</summary>
        public const float MinFactor = 0.5f;
        public const float MaxFactor = 2.5f;

        // The user setting is multiplied by the game's GUI scale, so the product needs more headroom than
        // the setting itself or raising the setting would stop doing anything on a large-GUI setup.
        private const float MinEffectiveFactor = 0.4f;
        private const float MaxEffectiveFactor = 4f;

        public static float Factor { get; private set; } = 1f;

        public static void Refresh()
        {
            float requested = FiresConfigUI.WindowScale * (FiresConfigUI.UseGameGuiScale ? GameGuiFactor() : 1f);
            Factor = Mathf.Clamp(requested, MinEffectiveFactor, MaxEffectiveFactor);
        }

        /// <summary>A size written at design scale, in the pixels it should actually occupy.</summary>
        public static float Px(float designUnits) => designUnits * Factor;

        public static Vector2 Px(Vector2 designUnits) => designUnits * Factor;

        /// <summary>
        /// A GUI-space rect (inside a window and its scroll view) as screen pixels, for input that is polled
        /// in Update rather than read from an event. Both corners are mapped so the size converts too.
        /// </summary>
        public static Rect GuiToScreenRect(Rect guiRect)
        {
            var topLeft = GUIUtility.GUIToScreenPoint(new Vector2(guiRect.xMin, guiRect.yMin));
            var bottomRight = GUIUtility.GUIToScreenPoint(new Vector2(guiRect.xMax, guiRect.yMax));
            return new Rect(topLeft.x, topLeft.y, bottomRight.x - topLeft.x, bottomRight.y - topLeft.y);
        }

        private static float GameGuiFactor()
        {
            try
            {
                if (GuiScaler.m_minWidth <= 0 || GuiScaler.m_minHeight <= 0) return 1f;
                return Mathf.Min((float)Screen.width / GuiScaler.m_minWidth, (float)Screen.height / GuiScaler.m_minHeight)
                       * GuiScaler.m_largeGuiScale;
            }
            catch { return 1f; }
        }
    }
}
