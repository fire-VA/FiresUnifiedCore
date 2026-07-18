using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.Input
{
    // Modifier-aware key binding. Wraps four ConfigEntry slots (Key, Ctrl,
    // Shift, Alt) so users can rebind via BepInEx ConfigurationManager's
    // native "click and set" UI for the key and tick the modifier boxes.
    //
    // Matching is STRICT: a binding of "Ctrl+F7" fires only when Ctrl is
    // held AND Shift+Alt are NOT held. A binding of "F7" with no modifiers
    // fires only when no modifier keys are held. This prevents the common
    // bug where "F7" and "Ctrl+F7" both fire on Ctrl+F7.
    //
    // Call IsPressed() inside a per-frame postfix and gate with the
    // standard input-blocker checks (Menu / Console / TextInput).
    public class KeyBinding
    {
        private const string CtrlEntrySuffix = "_Ctrl";
        private const string ShiftEntrySuffix = "_Shift";
        private const string AltEntrySuffix = "_Alt";

        public ConfigEntry<KeyCode> Key { get; }
        public ConfigEntry<bool> Ctrl { get; }
        public ConfigEntry<bool> Shift { get; }
        public ConfigEntry<bool> Alt { get; }

        public KeyBinding(
            ConfigFile config,
            string section,
            string name,
            KeyCode defaultKey,
            bool defaultCtrl = false,
            bool defaultShift = false,
            bool defaultAlt = false,
            string description = null)
        {
            string desc = string.IsNullOrEmpty(description) ? name : description;
            Key   = config.Bind(section, name,                    defaultKey,   desc);
            Ctrl  = config.Bind(section, name + CtrlEntrySuffix,  defaultCtrl,  "Require Ctrl held with " + name + ".");
            Shift = config.Bind(section, name + ShiftEntrySuffix, defaultShift, "Require Shift held with " + name + ".");
            Alt   = config.Bind(section, name + AltEntrySuffix,   defaultAlt,   "Require Alt held with " + name + ".");
        }

        // Gated centrally: while a Fires text field is focused, no KeyBinding hotkey fires (the keys are being
        // typed). Raw UnityEngine.Input can't be Harmony-patched, so every KeyBinding-based hotkey checks here.
        public bool IsPressed()
            => !FiresInputBlock.IsCapturing && UnityEngine.Input.GetKeyDown(Key.Value) && ModifiersMatch();

        public bool IsHeld()
            => !FiresInputBlock.IsCapturing && UnityEngine.Input.GetKey(Key.Value) && ModifiersMatch();

        private bool ModifiersMatch()
        {
            bool ctrlHeld  = UnityEngine.Input.GetKey(KeyCode.LeftControl) || UnityEngine.Input.GetKey(KeyCode.RightControl);
            bool shiftHeld = UnityEngine.Input.GetKey(KeyCode.LeftShift)   || UnityEngine.Input.GetKey(KeyCode.RightShift);
            bool altHeld   = UnityEngine.Input.GetKey(KeyCode.LeftAlt)     || UnityEngine.Input.GetKey(KeyCode.RightAlt);
            return ctrlHeld == Ctrl.Value && shiftHeld == Shift.Value && altHeld == Alt.Value;
        }

        public override string ToString()
        {
            string mods = string.Empty;
            if (Ctrl.Value)  mods += "Ctrl+";
            if (Shift.Value) mods += "Shift+";
            if (Alt.Value)   mods += "Alt+";
            return mods + Key.Value;
        }
    }
}
