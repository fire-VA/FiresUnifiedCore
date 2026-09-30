using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Input
{
    /// <summary>
    /// Where every Fires key read goes (KeyBinding, Chord, and the mods' own direct reads), so a test can press a key through
    /// the same code a player's key goes through (Fire, popup 2026-09-28: the UI-driven class test). UnityEngine.Input is an
    /// engine InternalCall that can't be Harmony-patched reliably, so the seam is here instead. Outside a test this is exactly
    /// UnityEngine.Input: the injection sets are empty unless a test owner holds them.
    /// </summary>
    public static class FiresKeys
    {
        private static readonly HashSet<KeyCode> s_pressed = new HashSet<KeyCode>();
        // A press's modifiers are HELD that frame, never pressed: a binding on a bare modifier must not fire on "Alt+3".
        private static readonly HashSet<KeyCode> s_pressedMods = new HashSet<KeyCode>();
        private static readonly HashSet<KeyCode> s_held = new HashSet<KeyCode>();
        private static int s_pressedFrame = -1;
        private static object s_owner;

        public static bool GetKeyDown(KeyCode key) =>
            UnityEngine.Input.GetKeyDown(key) || (s_owner != null && Time.frameCount == s_pressedFrame && s_pressed.Contains(key));

        public static bool GetKey(KeyCode key) =>
            UnityEngine.Input.GetKey(key) || (s_owner != null && (s_held.Contains(key)
                || (Time.frameCount == s_pressedFrame && (s_pressed.Contains(key) || s_pressedMods.Contains(key)))));

        /// <summary>A test takes the seam; only one owner at a time. False when someone else holds it.</summary>
        public static bool Acquire(object owner)
        {
            if (owner == null || (s_owner != null && s_owner != owner)) return false;
            s_owner = owner;
            return true;
        }

        /// <summary>The test lets go: every injected key is released.</summary>
        public static void Release(object owner)
        {
            if (owner == null || s_owner != owner) return;
            s_owner = null;
            s_pressed.Clear();
            s_pressedMods.Clear();
            s_held.Clear();
            s_pressedFrame = -1;
        }

        /// <summary>
        /// Presses a key on the NEXT frame (GetKeyDown true for that one frame, GetKey too), with the given modifiers held for
        /// that frame: a player tapping Alt+3. Callers then wait a frame before checking the result.
        /// </summary>
        public static bool Press(object owner, KeyCode key, params KeyCode[] modifiers)
        {
            if (owner == null || s_owner != owner || key == KeyCode.None) return false;
            s_pressed.Clear();
            s_pressedMods.Clear();
            s_pressed.Add(key);
            if (modifiers != null)
                foreach (KeyCode modifier in modifiers) s_pressedMods.Add(modifier);
            s_pressedFrame = Time.frameCount + 1;
            return true;
        }

        /// <summary>Holds a key down until <see cref="Up"/> (GetKey true, GetKeyDown not).</summary>
        public static bool Down(object owner, KeyCode key) => owner != null && s_owner == owner && s_held.Add(key);

        public static bool Up(object owner, KeyCode key) => owner != null && s_owner == owner && s_held.Remove(key);
    }
}
