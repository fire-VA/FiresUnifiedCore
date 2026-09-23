using System;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// The one floating overlay the config window puts above its rows (dropdown lists, the colour picker).
    /// It is a separate IMGUI window brought to the front rather than a box drawn inside the scroll view, so
    /// clicks land on the list instead of falling through to whatever row was drawn underneath it.
    /// </summary>
    internal static class ConfigPopup
    {
        private const int WindowId = 0xF1C1;
        private const float ScreenMarginBase = 8f;
        private const float AnchorGapBase = 2f;

        private static string _ownerId;
        private static Action<Rect> _body;
        private static Vector2 _size;
        private static Rect _anchorScreen;
        private static bool _anchorKnown;

        public static Vector2 Scroll;

        public static bool IsOpen => _ownerId != null;

        public static bool IsOpenFor(string id) => _ownerId != null && _ownerId == id;

        public static void Open(string id, Vector2 size, Action<Rect> body)
        {
            _ownerId = id;
            _size = size;
            _body = body;
            _anchorKnown = false;
            Scroll = Vector2.zero;
        }

        public static void Toggle(string id, Vector2 size, Action<Rect> body)
        {
            if (IsOpenFor(id)) Close();
            else Open(id, size, body);
        }

        public static void Close()
        {
            _ownerId = null;
            _body = null;
            _anchorKnown = false;
        }

        /// <summary>
        /// Called during the owning row's Repaint: records where the control sits on screen, which is the only
        /// pass where <see cref="GUILayoutUtility.GetLastRect"/> is meaningful.
        /// </summary>
        public static void AnchorToLastRect(string id)
        {
            if (!IsOpenFor(id) || Event.current.type != EventType.Repaint) return;
            _anchorScreen = ConfigWindowScale.GuiToScreenRect(GUILayoutUtility.GetLastRect());
            _anchorKnown = true;
        }

        /// <summary>Draw from OnGUI AFTER the main window, so the overlay owns the events over its own rect.</summary>
        public static void Draw()
        {
            if (_ownerId == null || !_anchorKnown) return;

            var rect = PlaceOnScreen();
            var current = Event.current;
            if (current.type == EventType.MouseDown && !rect.Contains(current.mousePosition)) { Close(); return; }
            if (current.type == EventType.KeyDown && current.keyCode == KeyCode.Escape) { Close(); current.Use(); return; }

            var body = _body;
            GUI.Window(WindowId, rect, _ => body(new Rect(0f, 0f, rect.width, rect.height)), GUIContent.none, ConfigSkin.Window);
            GUI.BringWindowToFront(WindowId);
        }

        // The anchor is already in screen pixels and the windows draw at identity, so it is used as-is. It
        // opens below the control and flips above when there is no room.
        private static Rect PlaceOnScreen()
        {
            float margin = ConfigWindowScale.Px(ScreenMarginBase);
            float gap = ConfigWindowScale.Px(AnchorGapBase);

            float width = Mathf.Min(_size.x, Screen.width - margin * 2f);
            float height = Mathf.Min(_size.y, Screen.height - margin * 2f);
            float x = Mathf.Clamp(_anchorScreen.xMin, margin, Screen.width - width - margin);
            float below = _anchorScreen.yMax + gap;
            float y = below + height <= Screen.height - margin
                ? below
                : Mathf.Max(margin, _anchorScreen.yMin - gap - height);
            return new Rect(x, y, width, height);
        }
    }
}
