using System.Collections.Generic;
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

        public string Section { get; }
        public string Name { get; }
        public ConfigFile Config { get; }

        /// <summary>When this binding is live. Drives whether the conflict scanner considers it against another.</summary>
        public BindingContext Context { get; private set; } = BindingContexts.Default;

        private readonly List<KeybindReplacement> _replacements = new List<KeybindReplacement>();

        public IReadOnlyList<KeybindReplacement> Replacements => _replacements;

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
            section = SafeConfigName(section, "section");
            name = SafeConfigName(name, "key");
            Config = config;
            Section = section;
            Name = name;
            Key   = config.Bind(section, name,                    defaultKey,   desc);
            Ctrl  = config.Bind(section, name + CtrlEntrySuffix,  defaultCtrl,  "Require Ctrl held with " + name + ".");
            Shift = config.Bind(section, name + ShiftEntrySuffix, defaultShift, "Require Shift held with " + name + ".");
            Alt   = config.Bind(section, name + AltEntrySuffix,   defaultAlt,   "Require Alt held with " + name + ".");
            KeybindRegistry.Register(this);
        }

        // BepInEx refuses these in a section or key name and throws from Bind (R77: a VikHavn key with an apostrophe aborted that mod's
        // setup on all three peers). A binding name comes from the calling mod, so Core cleans it instead of throwing (0.2.204).
        private static readonly char[] RefusedConfigChars = { '=', '\n', '\t', '\\', '"', '\'', '[', ']' };

        /// <summary>
        /// <paramref name="raw"/> with every character BepInEx refuses in a config section or key replaced by '_' (warned once per
        /// name); unchanged when clean.
        /// </summary>
        public static string SafeConfigName(string raw, string what)
        {
            if (string.IsNullOrEmpty(raw) || raw.IndexOfAny(RefusedConfigChars) < 0) return raw;
            var chars = raw.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (System.Array.IndexOf(RefusedConfigChars, chars[i]) >= 0) chars[i] = '_';
            string safe = new string(chars);
            UnityEngine.Debug.LogWarning($"[KeyBinding] config {what} name '{raw.Replace("\n", "\\n").Replace("\t", "\\t")}' has a character BepInEx refuses " +
                                         $"(= \\n \\t \\ \" ' [ ]); bound as '{safe}' instead");
            return safe;
        }

        // Overload, never an extra optional parameter: other Fires mods ship compiled against the 8-argument
        // constructor and optional arguments are baked into the caller's IL.
        public KeyBinding(
            ConfigFile config,
            string section,
            string name,
            KeyCode defaultKey,
            BindingContext context,
            bool defaultCtrl = false,
            bool defaultShift = false,
            bool defaultAlt = false,
            string description = null)
            : this(config, section, name, defaultKey, defaultCtrl, defaultShift, defaultAlt, description)
        {
            Context = context;
        }

        public KeyBinding WithContext(BindingContext context)
        {
            Context = context;
            return this;
        }

        /// <summary>Declares that this binding takes over a vanilla ZInput button in its own context.</summary>
        public KeyBinding Replaces(string vanillaButtonName) => Replaces(vanillaButtonName, Context);

        public KeyBinding Replaces(string vanillaButtonName, BindingContext context)
        {
            if (string.IsNullOrEmpty(vanillaButtonName)) return this;
            _replacements.Add(new KeybindReplacement(vanillaButtonName, context));
            return this;
        }

        public KeyModifiers Modifiers => KeyCombination.FromBooleans(Ctrl.Value, Shift.Value, Alt.Value);

        // Gated centrally: while a Fires text field is focused, no KeyBinding hotkey fires (the keys are being
        // typed). Raw UnityEngine.Input can't be Harmony-patched, so every KeyBinding-based hotkey checks here.
        public bool IsPressed()
            => !FiresInputBlock.IsCapturing && FiresKeys.GetKeyDown(Key.Value) && ModifiersMatch();

        public bool IsHeld()
            => !FiresInputBlock.IsCapturing && FiresKeys.GetKey(Key.Value) && ModifiersMatch();

        private bool ModifiersMatch()
        {
            bool ctrlHeld  = FiresKeys.GetKey(KeyCode.LeftControl) || FiresKeys.GetKey(KeyCode.RightControl);
            bool shiftHeld = FiresKeys.GetKey(KeyCode.LeftShift)   || FiresKeys.GetKey(KeyCode.RightShift);
            bool altHeld   = FiresKeys.GetKey(KeyCode.LeftAlt)     || FiresKeys.GetKey(KeyCode.RightAlt);
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
