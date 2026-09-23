using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// A scaled copy of <see cref="FiresRoundedSkin"/> for the config windows.
    ///
    /// The shared skin is deliberately NOT scaled in place — FiresDebugginTools' inspector draws from the
    /// same style objects and would resize with us. Scaling here also means text is RENDERED at the larger
    /// size instead of being magnified by a GUI matrix, which is the whole point: a matrix stretches glyphs
    /// the font atlas already rasterised, so they come out soft.
    ///
    /// 9-slice borders stay unscaled on purpose: <see cref="GUIStyle.border"/> counts pixels of the SOURCE
    /// texture, so scaling it would sample past the rounded corner and smear it.
    /// </summary>
    internal static class ConfigSkin
    {
        private const int AlphaKeySpread = 1000;

        public static GUIStyle Window, SectionBar, Button, ButtonSmall, Field, Label, Title, Hint;
        public static GUIStyle Slider, SliderThumb;
        public static GUIStyle NavItem, NavSel, NavSub, NavSubOn, TextInput, Desc, Swatch, Tip;

        private static int _builtKey = -1;

        public static void Refresh(float windowAlpha, float scale)
        {
            FiresRoundedSkin.Ensure(windowAlpha);

            int key = Mathf.RoundToInt(windowAlpha * 100f) * AlphaKeySpread + Mathf.RoundToInt(scale * 100f);
            if (key == _builtKey) return;
            _builtKey = key;

            Window = Scale(FiresRoundedSkin.Window, scale);
            SectionBar = Scale(FiresRoundedSkin.SectionBar, scale);
            Button = Scale(FiresRoundedSkin.Button, scale);
            ButtonSmall = Scale(FiresRoundedSkin.ButtonSmall, scale);
            Field = Scale(FiresRoundedSkin.Field, scale);
            Label = Scale(FiresRoundedSkin.Label, scale);
            Title = Scale(FiresRoundedSkin.Title, scale);
            Hint = Scale(FiresRoundedSkin.Hint, scale);
            Slider = Scale(FiresRoundedSkin.Slider, scale);
            SliderThumb = Scale(FiresRoundedSkin.SliderThumb, scale);
            NavItem = Scale(FiresRoundedSkin.NavItem, scale);
            NavSel = Scale(FiresRoundedSkin.NavSel, scale);
            NavSub = Scale(FiresRoundedSkin.NavSub, scale);
            NavSubOn = Scale(FiresRoundedSkin.NavSubOn, scale);
            TextInput = Scale(FiresRoundedSkin.TextInput, scale);
            Desc = Scale(FiresRoundedSkin.Desc, scale);
            Swatch = Scale(FiresRoundedSkin.Swatch, scale);
            Tip = Scale(FiresRoundedSkin.Tip, scale);
        }

        private static GUIStyle Scale(GUIStyle source, float scale)
        {
            var style = new GUIStyle(source);
            if (source.fontSize > 0) style.fontSize = Mathf.Max(1, Mathf.RoundToInt(source.fontSize * scale));
            style.padding = Scale(source.padding, scale);
            style.margin = Scale(source.margin, scale);
            if (source.fixedWidth > 0f) style.fixedWidth = source.fixedWidth * scale;
            if (source.fixedHeight > 0f) style.fixedHeight = source.fixedHeight * scale;
            return style;
        }

        private static RectOffset Scale(RectOffset source, float scale)
            => new RectOffset(Mathf.RoundToInt(source.left * scale), Mathf.RoundToInt(source.right * scale),
                              Mathf.RoundToInt(source.top * scale), Mathf.RoundToInt(source.bottom * scale));
    }
}
