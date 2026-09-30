using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// The window's first open cost ~0.9 s at the end of that frame (R6, [perf], 2026-09-28: 949 ms in
    /// PostLateUpdate.FinishFrameRendering, main thread blocked 547 ms, IMGUI itself 79 ms). IMGUI draws with a dynamic font
    /// that rasterises each glyph at each size and style the first time it is drawn, growing and re-uploading its atlas as
    /// it goes; the window draws about 2,500 entries' text at a dozen sizes. So the window asks for every glyph it can
    /// show, at every size and style its skin uses, once per session, while Valheim's loading screen still covers the
    /// world join ([perf]: a 0.9 s frame after the fade can land while the player is already moving), falling back to
    /// 3 s after the first spawn if the screen was never seen up. The atlas then grows in one step and the first open
    /// only draws. Both this and each open log how many font atlas rebuilds they caused.
    /// </summary>
    public partial class ConfigPanel
    {
        private const float PrewarmAfterSpawnSeconds = 3f;
        private const float LoadingScreenOpaque = 0.9f;
        private const char FirstPrintable = ' ', LastPrintable = '~';
        private static readonly FontStyle[] s_prewarmStyles = { FontStyle.Normal, FontStyle.Bold };

        // R7 ([perf]): with the glyphs prewarmed the cold open was still ~500 ms after IMGUI, render-side and cold-only: the
        // first use of the window's and the conflicts popup's textures, materials and GUI shaders. So behind the loading
        // screen the whole window and the popup are also drawn for HiddenDrawRepaints frames at 1/255 opacity (IMGUI
        // windows keep the GUI.color they were called with), Layout and Repaint only, so nothing can be clicked.
        private const int HiddenDrawRepaints = 2;
        private const float HiddenAlpha = 1f / 255f;
        private int _hiddenDrawsLeft;
        private double _hiddenGuiMs;

        // Fire (R43/R44): the prewarm flashed the window over the loading screen. IMGUI draws after every uGUI canvas, so the
        // loading screen hides nothing, and the windows' contents run at the end of this OnGUI, after HiddenDraw has put
        // GUI.color back. So each window body applies the tint itself while this pass is hidden (OnGUI clears the flag).
        private bool _hiddenPass;

        private void ApplyHiddenTint()
        {
            if (_hiddenPass) GUI.color = new Color(1f, 1f, 1f, HiddenAlpha);
        }

        // The loading screen first comes up inside the main scene's activation frame (R39: 5.8 s of every object's Awake and
        // Start), so the prewarm waits a couple of covered frames instead of adding its ~0.4 s to that one.
        private const int CoveredFramesBeforePrewarm = 2;
        private int _coveredSinceFrame = -1;

        private float _prewarmAt = -1f;
        private bool _prewarmDone;
        private int _openFontRebuilds;
        private static int s_fontRebuilds;
        private static bool s_fontHooked;

        private static void HookFontRebuilds()
        {
            if (s_fontHooked) return;
            s_fontHooked = true;
            Font.textureRebuilt += _ => s_fontRebuilds++;
        }

        // From Update: the fallback, armed once per session a little after the local player first exists.
        private void ArmPrewarm()
        {
            if (_prewarmDone || _prewarmAt > 0f || Player.m_localPlayer == null) return;
            _prewarmAt = Time.realtimeSinceStartup + PrewarmAfterSpawnSeconds;
        }

        // In the game scene with the loading screen (nearly) opaque: the world join, a teleport or sleep.
        private static bool LoadingScreenUp()
        {
            var hud = Hud.instance;
            if (ZNet.instance == null || hud == null || hud.m_loadingScreen == null) return false;
            return hud.m_loadingScreen.gameObject.activeInHierarchy && hud.m_loadingScreen.alpha >= LoadingScreenOpaque;
        }

        // From OnGUI while the window is closed: IMGUI's skin, and so its font, is only reachable inside OnGUI.
        private void PrewarmFonts()
        {
            if (_prewarmDone) return;
            if (!LoadingScreenUp()) _coveredSinceFrame = -1;
            else if (_coveredSinceFrame < 0) _coveredSinceFrame = Time.frameCount;
            bool covered = _coveredSinceFrame >= 0 && Time.frameCount - _coveredSinceFrame >= CoveredFramesBeforePrewarm;
            if (!covered && (_prewarmAt < 0f || Time.realtimeSinceStartup < _prewarmAt)) return;
            _prewarmDone = true;
            if (EverOpened) return;
            string when = covered ? "behind the loading screen" : $"{PrewarmAfterSpawnSeconds:0} s after spawn (the loading screen was not up)";
            try
            {
                HookFontRebuilds();
                int rebuildsBefore = s_fontRebuilds;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                RefreshDiscovery();
                ConfigWindowScale.Refresh();
                ConfigSkin.Refresh(WindowAlpha, ConfigWindowScale.Factor);
                string glyphs = PanelGlyphs();

                var done = new HashSet<string>(StringComparer.Ordinal);
                var sizes = new List<int>();
                string fontName = null;
                foreach (var style in PanelStyles())
                {
                    if (style == null) continue;
                    var font = style.font != null ? style.font : GUI.skin.font;
                    if (font == null || !font.dynamic) continue;
                    int size = style.fontSize > 0 ? style.fontSize : font.fontSize;
                    if (!done.Add(font.GetInstanceID() + ":" + size)) continue;
                    foreach (var fontStyle in s_prewarmStyles) font.RequestCharactersInTexture(glyphs, size, fontStyle);
                    sizes.Add(size);
                    fontName = font.name;
                }
                sizes.Sort();
                var atlas = GUI.skin.font != null ? GUI.skin.font.material?.mainTexture : null;
                FiresConfigUI.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                    "ConfigPanel prewarm ({6}): {0} glyphs x sizes [{1}] x normal/bold of '{2}' in {3:0} ms, {4} font atlas rebuild(s){5}.",
                    glyphs.Length, string.Join(",", sizes), fontName ?? "no dynamic font", clock.Elapsed.TotalMilliseconds,
                    s_fontRebuilds - rebuildsBefore, atlas != null ? $", atlas {atlas.width}x{atlas.height}" : "", when));
                if (covered) _hiddenDrawsLeft = HiddenDrawRepaints;
            }
            catch (Exception ex) { FiresConfigUI.Log.LogWarning("ConfigPanel prewarm failed: " + ex.Message); }
        }

        // From OnGUI while the window is closed, after PrewarmFonts: the window and the popup, drawn but not seen. Never
        // once the loading screen is gone or the window has been opened for real.
        private void HiddenDraw()
        {
            if (_hiddenDrawsLeft <= 0) return;
            var type = Event.current != null ? Event.current.type : EventType.Ignore;
            if (type != EventType.Layout && type != EventType.Repaint) return;
            if (EverOpened || !LoadingScreenUp()) { _hiddenDrawsLeft = 0; return; }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var matrix = GUI.matrix;
            var color = GUI.color;
            bool popupWasOpen = _keybindPopupOpen;
            try
            {
                if (!popupWasOpen)
                {
                    _keybindPopupConflicts.Clear();
                    _keybindPopupConflicts.AddRange(UnresolvedConflicts());
                    _keybindPopupOpen = _keybindPopupConflicts.Count > 0;
                }
                ConfigWindowScale.Refresh();
                GUI.matrix = Matrix4x4.identity;
                GUI.color = new Color(1f, 1f, 1f, HiddenAlpha);
                _hiddenPass = true;
                DrawMainWindow();
                DrawKeybindPopup();
            }
            catch (Exception ex)
            {
                _hiddenDrawsLeft = 0;
                _hiddenPass = false;
                FiresConfigUI.Log.LogWarning("ConfigPanel hidden prewarm draw failed: " + ex.Message);
            }
            finally
            {
                GUI.matrix = matrix;
                GUI.color = color;
                if (!popupWasOpen)
                {
                    _keybindPopupOpen = false;
                    _keybindPopupConflicts.Clear();
                }
                _hiddenGuiMs += clock.Elapsed.TotalMilliseconds;
                if (type == EventType.Repaint && _hiddenDrawsLeft > 0 && --_hiddenDrawsLeft == 0)
                    FiresConfigUI.Log.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "ConfigPanel prewarm: window and conflicts popup drawn hidden behind the loading screen ({0} frames, IMGUI {1:0} ms), "
                        + "so their textures, materials and GUI shaders are first used here, not at the first open.",
                        HiddenDrawRepaints, _hiddenGuiMs));
            }
        }

        private static IEnumerable<GUIStyle> PanelStyles()
        {
            yield return ConfigSkin.Window; yield return ConfigSkin.SectionBar; yield return ConfigSkin.Button;
            yield return ConfigSkin.ButtonSmall; yield return ConfigSkin.Field; yield return ConfigSkin.Label;
            yield return ConfigSkin.Title; yield return ConfigSkin.Hint; yield return ConfigSkin.NavItem;
            yield return ConfigSkin.NavSel; yield return ConfigSkin.NavSub; yield return ConfigSkin.NavSubOn;
            yield return ConfigSkin.TextInput; yield return ConfigSkin.Desc; yield return ConfigSkin.Tip;
        }

        // Every character the window can draw: printable ASCII and whatever the discovered mods, sections, labels,
        // descriptions, options and values add.
        private static string PanelGlyphs()
        {
            var seen = new HashSet<char>();
            var glyphs = new StringBuilder();
            void Add(string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                foreach (char c in text)
                    if (!char.IsControl(c) && seen.Add(c)) glyphs.Append(c);
            }
            for (char c = FirstPrintable; c <= LastPrintable; c++) Add(c.ToString());
            foreach (var mod in CfgDiscovery.ModNames) Add(mod);
            foreach (var descriptor in CfgDiscovery.Descriptors)
            {
                Add(descriptor.SectionDisplay);
                Add(descriptor.Label);
                Add(descriptor.DescriptionLine);
                Add(descriptor.Description);
                if (descriptor.Options != null) foreach (var option in descriptor.Options) Add(option);
                try { Add(Convert.ToString(descriptor.BoxedValue, CultureInfo.InvariantCulture)); } catch { }
            }
            return glyphs.ToString();
        }
    }
}
