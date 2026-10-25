using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Some mods keep a keybind in a
    /// string entry, "Alt+1" or "Ctrl+Shift+F": modifiers Alt / Ctrl / Shift, then one key (a KeyCode name, or a bare digit for the number
    /// row). An entry is treated as one when its key or description names a keybind / hotkey AND its value is empty or parses in that form,
    /// so ordinary text settings never get the recorder. The recorder's result is written back in the same text form.
    /// </summary>
    internal static class KeybindStrings
    {
        internal static bool IsKeybindEntry(string key, string description, string value)
        {
            string k = (key ?? "").ToLowerInvariant(), d = (description ?? "").ToLowerInvariant();
            bool named = k.Contains("keybind") || k.Contains("hotkey") || d.Contains("keybind") || d.Contains("hotkey");
            return named && (string.IsNullOrWhiteSpace(value) || TryParse(value, out _, out _));
        }

        internal static bool TryParse(string text, out KeyCode main, out List<KeyCode> modifiers)
        {
            main = KeyCode.None;
            modifiers = new List<KeyCode>();
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) return false;
                bool last = i == parts.Length - 1;
                string lower = p.ToLowerInvariant();
                if (!last)
                {
                    if (lower == "alt") modifiers.Add(KeyCode.LeftAlt);
                    else if (lower == "ctrl" || lower == "control") modifiers.Add(KeyCode.LeftControl);
                    else if (lower == "shift") modifiers.Add(KeyCode.LeftShift);
                    else return false;
                    continue;
                }
                if (p.Length == 1 && char.IsDigit(p[0])) { main = KeyCode.Alpha0 + (p[0] - '0'); return true; }
                if (Enum.TryParse(p, true, out KeyCode code) && code != KeyCode.None) { main = code; return true; }
                if (Enum.TryParse("Alpha" + p, true, out code)) { main = code; return true; }
                return false;
            }
            return false;
        }

        /// <summary>The recorder's chord as text: "Ctrl+Shift+F", "Alt+1" (number-row digits bare, modifiers by name, each once).</summary>
        internal static string Format(KeyCode main, IList<KeyCode> modifiers)
        {
            var parts = new List<string>();
            void Add(string m) { if (!parts.Contains(m)) parts.Add(m); }
            foreach (KeyCode m in modifiers ?? new List<KeyCode>())
            {
                if (m == KeyCode.LeftControl || m == KeyCode.RightControl) Add("Ctrl");
                else if (m == KeyCode.LeftShift || m == KeyCode.RightShift) Add("Shift");
                else if (m == KeyCode.LeftAlt || m == KeyCode.RightAlt || m == KeyCode.AltGr) Add("Alt");
            }
            string key = main >= KeyCode.Alpha0 && main <= KeyCode.Alpha9 ? ((int)(main - KeyCode.Alpha0)).ToString() : main.ToString();
            parts.Add(key);
            return string.Join("+", parts);
        }
    }
}
