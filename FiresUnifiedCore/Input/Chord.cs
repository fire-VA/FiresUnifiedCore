using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.Input
{
    // Lenient KeyboardShortcut check for GAMEPLAY hotkeys.
    //
    // BepInEx's KeyboardShortcut.IsDown() refuses to fire while ANY unrelated key is held —
    // correct for menu hotkeys, wrong in a game where W is held half the time (Journal on J
    // would never open while walking). This check:
    //   - main key just pressed,
    //   - every listed modifier held,
    //   - no UNLISTED modifier held (so a plain "L" bind stays distinct from "L + LeftAlt"),
    //   - and ignores every non-modifier key (movement, mouse, whatever).
    public static class Chord
    {
        private static readonly KeyCode[] s_modifiers =
        {
            KeyCode.LeftControl, KeyCode.RightControl, KeyCode.LeftShift, KeyCode.RightShift,
            KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.AltGr, KeyCode.LeftCommand, KeyCode.RightCommand,
        };

        public static bool IsDown(KeyboardShortcut shortcut)
        {
            var main = shortcut.MainKey;
            if (main == KeyCode.None) return false;
            if (!UnityEngine.Input.GetKeyDown(main)) return false;

            foreach (var mod in shortcut.Modifiers)
                if (!UnityEngine.Input.GetKey(mod)) return false;

            // Treat L/R variants of a listed modifier as satisfying the "unlisted" test only for
            // exact keys — a bind saved as "L + LeftAlt" should not fire on RightAlt+L, matching
            // how the capture UI records concrete keys.
            foreach (var mod in s_modifiers)
            {
                if (mod == main) continue;
                bool listed = false;
                foreach (var modifier in shortcut.Modifiers)
                    if (modifier == mod) { listed = true; break; }
                if (!listed && UnityEngine.Input.GetKey(mod)) return false;
            }
            return true;
        }
    }
}
