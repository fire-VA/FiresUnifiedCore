using System;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// The focused single-setting window ConfigurationManager opens on a double-click: one setting with room
    /// for its full description, its type and default, and the same widget the row uses. Useful for the long
    /// multi-line descriptions that get truncated to one line in the list.
    /// </summary>
    internal static class SettingEditWindow
    {
        private const int WindowId = 0xF1C2;
        private const float TitleBarHeightBase = 26f;
        private const float DescriptionHeightBase = 120f;
        private const float OnScreenMarginBase = 60f;
        private static readonly Vector2 SizeBase = new Vector2(560f, 320f);

        private static float Px(float designUnits) => ConfigWindowScale.Px(designUnits);
        private static float TitleBarHeight => Px(TitleBarHeightBase);
        private static float DescriptionHeight => Px(DescriptionHeightBase);
        private static Vector2 Size => ConfigWindowScale.Px(SizeBase);

        private static CfgDescriptor _target;
        private static Rect _rect = new Rect(-1f, -1f, Size.x, Size.y);
        private static Vector2 _scroll;

        public static bool IsOpen => _target != null;

        public static void Open(CfgDescriptor descriptor)
        {
            _target = descriptor;
            _scroll = Vector2.zero;
        }

        public static void Close() => _target = null;

        /// <summary>Draw from OnGUI after the main window and BEFORE the popup, so a dropdown opened here lands on top.</summary>
        public static void Draw(Action<CfgDescriptor> drawWidget, Action<CfgDescriptor> resetToDefault)
        {
            if (_target == null) return;

            var size = Size;
            if (_rect.x < 0f)
                _rect = new Rect((Screen.width - size.x) / 2f, (Screen.height - size.y) / 2f, size.x, size.y);
            _rect.width = size.x;
            _rect.height = size.y;

            _rect = GUILayout.Window(WindowId, _rect, _ => DrawBody(drawWidget, resetToDefault), "", ConfigSkin.Window);
            float margin = Px(OnScreenMarginBase);
            _rect.x = Mathf.Clamp(_rect.x, 0f, Screen.width - margin);
            _rect.y = Mathf.Clamp(_rect.y, 0f, Screen.height - margin);
            GUI.BringWindowToFront(WindowId);
        }

        private static void DrawBody(Action<CfgDescriptor> drawWidget, Action<CfgDescriptor> resetToDefault)
        {
            var descriptor = _target;
            if (descriptor == null) return;

            GUILayout.Label("<b>" + descriptor.Label + "</b>", ConfigSkin.Title);
            GUILayout.Label(descriptor.ModName + "   ·   " + descriptor.SectionDisplay + "   ·   " + descriptor.Key,
                ConfigSkin.Hint);
            GUILayout.Space(4f);

            _scroll = GUILayout.BeginScrollView(_scroll, ScaledLayout.Height(DescriptionHeight));
            GUILayout.Label(string.IsNullOrEmpty(descriptor.Description) ? "No description." : descriptor.Description,
                ConfigSkin.Desc);
            GUILayout.EndScrollView();

            GUILayout.Space(4f);
            bool wasEnabled = GUI.enabled;
            if (descriptor.IsLocked) GUI.enabled = false;
            GUILayout.BeginHorizontal();
            try { drawWidget(descriptor); }
            catch (Exception ex) { FiresConfigUI.Log.LogWarning($"edit window widget for '{descriptor.Key}' failed: {ex.Message}"); }
            finally { GUILayout.EndHorizontal(); }
            GUI.enabled = wasEnabled;

            GUILayout.Space(4f);
            GUILayout.Label($"Type {descriptor.Type.Name}   ·   Default {descriptor.Default ?? "none"}", ConfigSkin.Hint);
            if (descriptor.IsLocked) GUILayout.Label("<color=#9AA0A6>Locked by the server.</color>", ConfigSkin.Hint);

            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            GUI.enabled = wasEnabled && !descriptor.IsLocked;
            if (GUILayout.Button("Reset to default", ConfigSkin.ButtonSmall)) resetToDefault(descriptor);
            GUI.enabled = wasEnabled;
            if (GUILayout.Button("Close", ConfigSkin.ButtonSmall)) Close();
            GUILayout.EndHorizontal();

            GUI.DragWindow(new Rect(0f, 0f, _rect.width, TitleBarHeight));
        }
    }
}
