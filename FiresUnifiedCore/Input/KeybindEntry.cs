using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.Input
{
    /// <summary>When a binding is live. Undeclared bindings are <see cref="Gameplay"/>.</summary>
    public enum BindingContext
    {
        Always,
        Gameplay,
        WeaponDrawn,
        WeaponSheathed,
        Building,
        InventoryOpen,
        MapOpen,
        Menu,
    }

    /// <summary>
    /// Strict fires only on its exact modifier set; Loose fires whatever else is held, so it collides with
    /// every combination of its key.
    /// </summary>
    public enum BindingMatch
    {
        Strict,
        Loose,
    }

    public enum BindingOrigin
    {
        CoreKeyBinding,
        PluginShortcut,
        PluginKeyCode,
        VanillaButton,
    }

    /// <summary>Modifier keys normalised so a KeyBinding's three bools and a KeyboardShortcut's concrete left/right keys compare directly.</summary>
    [Flags]
    public enum KeyModifiers
    {
        None = 0,
        Ctrl = 1,
        Shift = 2,
        Alt = 4,
        Meta = 8,
    }

    /// <summary>A declared hand-off: this binding takes over a named vanilla ZInput button in a context.</summary>
    public struct KeybindReplacement
    {
        public string ButtonName;
        public BindingContext Context;

        public KeybindReplacement(string buttonName, BindingContext context)
        {
            ButtonName = buttonName;
            Context = context;
        }
    }

    /// <summary>
    /// One binding as the registry sees it, whatever its source. Immutable for a scan: the registry rebuilds
    /// the list rather than mutating entries, so a view can hold one for a frame without it changing underneath.
    /// </summary>
    public sealed class KeybindEntry
    {
        public BindingOrigin Origin;
        public string PluginGuid;
        public string ModName;
        public string Section;
        public string Name;
        public string Label;
        public BindingContext Context;
        public BindingMatch Match;
        public KeyCode Key;
        public KeyModifiers Modifiers;
        public bool IsChord;
        public bool CanChange;
        public string ChangeHint;
        public bool IsLocked;
        public KeyBinding CoreBinding;
        public ConfigEntryBase Entry;
        public IReadOnlyList<KeybindReplacement> Replacements;

        private static readonly KeybindReplacement[] NoReplacements = new KeybindReplacement[0];

        public bool IsBound => Key != KeyCode.None;

        public IReadOnlyList<KeybindReplacement> DeclaredReplacements => Replacements ?? NoReplacements;

        /// <summary>Stable identity of the binding itself, independent of what it is currently bound to.</summary>
        public string Id => PluginGuid + "|" + Section + "|" + Name;

        public string Combination => KeyCombination.Describe(Key, Modifiers);

        /// <summary>Identity plus the current combination - the unit an ignore signature is built from.</summary>
        public string SignaturePart => Id + "@" + Combination;

        public string SourceLabel => ModName + " - " + Label;
    }

    /// <summary>Formatting and modifier maths shared by the registry, the detector and the views.</summary>
    public static class KeyCombination
    {
        public const string UnboundLabel = "None";

        private static readonly KeyModifiers[] AllModifiers =
            { KeyModifiers.Ctrl, KeyModifiers.Shift, KeyModifiers.Alt, KeyModifiers.Meta };

        private static readonly KeyCode[] CtrlKeys = { KeyCode.LeftControl, KeyCode.RightControl };
        private static readonly KeyCode[] ShiftKeys = { KeyCode.LeftShift, KeyCode.RightShift };
        private static readonly KeyCode[] AltKeys = { KeyCode.LeftAlt, KeyCode.RightAlt, KeyCode.AltGr };
        private static readonly KeyCode[] MetaKeys = { KeyCode.LeftCommand, KeyCode.RightCommand, KeyCode.LeftWindows, KeyCode.RightWindows };
        private static readonly KeyCode[] NoKeys = new KeyCode[0];

        public static IReadOnlyList<KeyModifiers> Flags => AllModifiers;

        public static string Describe(KeyCode key, KeyModifiers modifiers)
        {
            if (key == KeyCode.None && modifiers == KeyModifiers.None) return UnboundLabel;
            var text = new StringBuilder();
            if ((modifiers & KeyModifiers.Ctrl) != 0) text.Append("Ctrl+");
            if ((modifiers & KeyModifiers.Shift) != 0) text.Append("Shift+");
            if ((modifiers & KeyModifiers.Alt) != 0) text.Append("Alt+");
            if ((modifiers & KeyModifiers.Meta) != 0) text.Append("Meta+");
            text.Append(key == KeyCode.None ? UnboundLabel : key.ToString());
            return text.ToString();
        }

        public static string Describe(KeyModifiers modifier) => modifier.ToString();

        /// <summary>The physical keys a normalised modifier flag stands for.</summary>
        public static IReadOnlyList<KeyCode> KeysOf(KeyModifiers modifier)
        {
            switch (modifier)
            {
                case KeyModifiers.Ctrl: return CtrlKeys;
                case KeyModifiers.Shift: return ShiftKeys;
                case KeyModifiers.Alt: return AltKeys;
                case KeyModifiers.Meta: return MetaKeys;
                default: return NoKeys;
            }
        }

        public static KeyModifiers ModifierOf(KeyCode key)
        {
            foreach (var modifier in AllModifiers)
            {
                var keys = KeysOf(modifier);
                for (int i = 0; i < keys.Count; i++)
                    if (keys[i] == key) return modifier;
            }
            return KeyModifiers.None;
        }

        public static KeyModifiers FromBooleans(bool ctrl, bool shift, bool alt)
        {
            var modifiers = KeyModifiers.None;
            if (ctrl) modifiers |= KeyModifiers.Ctrl;
            if (shift) modifiers |= KeyModifiers.Shift;
            if (alt) modifiers |= KeyModifiers.Alt;
            return modifiers;
        }

        /// <summary>
        /// Splits a KeyboardShortcut's extra keys into normalised modifier flags; any extra key that is not a
        /// modifier makes it a multi-key chord, which v1 does not reason about.
        /// </summary>
        public static KeyModifiers FromShortcut(KeyboardShortcut shortcut, out bool isChord)
        {
            var modifiers = KeyModifiers.None;
            isChord = false;
            foreach (var key in shortcut.Modifiers)
            {
                var modifier = ModifierOf(key);
                if (modifier == KeyModifiers.None) isChord = true;
                else modifiers |= modifier;
            }
            return modifiers;
        }

        public static KeyboardShortcut ToShortcut(KeyCode key, KeyModifiers modifiers)
        {
            if (key == KeyCode.None) return KeyboardShortcut.Empty;
            var keys = new List<KeyCode>();
            if ((modifiers & KeyModifiers.Ctrl) != 0) keys.Add(KeyCode.LeftControl);
            if ((modifiers & KeyModifiers.Shift) != 0) keys.Add(KeyCode.LeftShift);
            if ((modifiers & KeyModifiers.Alt) != 0) keys.Add(KeyCode.LeftAlt);
            if ((modifiers & KeyModifiers.Meta) != 0) keys.Add(KeyCode.LeftCommand);
            return new KeyboardShortcut(key, keys.ToArray());
        }
    }

    /// <summary>The design's context overlap table, collapsed to the one rule it reduces to.</summary>
    public static class BindingContexts
    {
        public const BindingContext Default = BindingContext.Gameplay;

        public static bool Overlap(BindingContext a, BindingContext b)
            => a == BindingContext.Always
            || b == BindingContext.Always
            || a == b
            || a == BindingContext.Gameplay
            || b == BindingContext.Gameplay;

        public static string Describe(BindingContext context)
        {
            switch (context)
            {
                case BindingContext.Always: return "always";
                case BindingContext.Gameplay: return "gameplay";
                case BindingContext.WeaponDrawn: return "weapon drawn";
                case BindingContext.WeaponSheathed: return "weapon sheathed";
                case BindingContext.Building: return "building";
                case BindingContext.InventoryOpen: return "inventory open";
                case BindingContext.MapOpen: return "map open";
                case BindingContext.Menu: return "menu";
                default: return context.ToString();
            }
        }
    }
}
