using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// 0.2.310 (Fire 10:2x: a test clicks what a player clicks): where Core's IMGUI windows (the F8 config window) take a test's
    /// clicks and typing through the same IMGUI event path a player's mouse and keyboard go through. IMGUI reads the OS's events and
    /// an EventSystem raycast never reaches it, so the window records the screen rect of each named control on Repaint, and
    /// a test owner queues mouse-down / mouse-up / character events at those rects; the window replays one queued event per frame
    /// right after its own Repaint, against that frame's layout. Keys go through FiresKeys (the same seam KeyBinding and Chord read).
    /// Outside a test the queue is empty and nothing changes.
    /// </summary>
    public static class FiresGuiTest
    {
        private static object s_owner;
        private static readonly Queue<Event> s_events = new Queue<Event>();
        private static readonly Dictionary<string, Rect> s_rects = new Dictionary<string, Rect>();
        private static readonly Dictionary<string, int> s_rectFrame = new Dictionary<string, int>();

        /// <summary>A test takes the seam; one owner at a time. False when someone else holds it.</summary>
        public static bool Acquire(object owner)
        {
            if (owner == null || (s_owner != null && s_owner != owner)) return false;
            s_owner = owner;
            return true;
        }

        public static void Release(object owner)
        {
            if (owner == null || s_owner != owner) return;
            s_owner = null;
            s_events.Clear();
        }

        /// <summary>The control's last drawn rect in GUI screen space (top-left origin), if it was drawn in the last few frames.</summary>
        public static bool TryRect(string control, out Rect rect)
        {
            rect = default;
            return control != null && s_rects.TryGetValue(control, out rect) && s_rectFrame.TryGetValue(control, out int f) && Time.frameCount - f <= 5;
        }

        /// <summary>A left click on a named control: mouse down then mouse up at its centre. False when it isn't on screen.</summary>
        public static bool Click(object owner, string control)
        {
            if (owner == null || s_owner != owner || !TryRect(control, out Rect r)) return false;
            Vector2 at = r.center;
            s_events.Enqueue(new Event { type = EventType.MouseDown, mousePosition = at, button = 0, clickCount = 1 });
            s_events.Enqueue(new Event { type = EventType.MouseUp, mousePosition = at, button = 0, clickCount = 1 });
            return true;
        }

        /// <summary>Types text into the focused IMGUI text field, one character event per frame.</summary>
        public static bool Type(object owner, string text)
        {
            if (owner == null || s_owner != owner || string.IsNullOrEmpty(text)) return false;
            foreach (char c in text) s_events.Enqueue(new Event { type = EventType.KeyDown, character = c, keyCode = KeyCode.None });
            return true;
        }

        public static bool Busy => s_events.Count > 0;

        // ── the window's side ───────────────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Called by a window right after drawing a control it names: records the control's rect on Repaint.</summary>
        internal static void Record(string control)
        {
            if (s_owner == null || Event.current == null || Event.current.type != EventType.Repaint) return;
            Rect local = GUILayoutUtility.GetLastRect();
            Vector2 topLeft = GUIUtility.GUIToScreenPoint(local.position);
            s_rects[control] = new Rect(topLeft, local.size);
            s_rectFrame[control] = Time.frameCount;
        }

        /// <summary>Called by a window at the end of its OnGUI Repaint: hands back the next queued event to replay, if any.</summary>
        internal static bool TryNext(out Event e)
        {
            e = null;
            if (s_owner == null || s_events.Count == 0 || Event.current == null || Event.current.type != EventType.Repaint) return false;
            e = s_events.Dequeue();
            return true;
        }
    }
}
