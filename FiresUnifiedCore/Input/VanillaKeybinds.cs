using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Input
{
    /// <summary>
    /// Reads Valheim's own keyboard bindings - including the player's rebinds - out of ZInput.
    ///
    /// Valheim 1.0 drives ZInput from Unity's InputSystem, so a binding is an action PATH
    /// ("&lt;Keyboard&gt;/F"), not a KeyCode. ZInput's own private KeyCodeToPath is reflected once and inverted
    /// into a path-to-KeyCode map rather than re-implementing its Digit / Apple / Windows / Command special
    /// cases here.
    /// </summary>
    public static class VanillaKeybinds
    {
        public const string PluginGuid = "valheim";
        public const string ModName = "Valheim";
        public const string SectionName = "Controls";
        public const string ChangeHint = "Change in Valheim's Controls settings.";

        private const string ButtonsFieldName = "m_buttons";
        private const string KeyCodeToPathMethodName = "KeyCodeToPath";
        private const string GamepadPathMarker = "Gamepad";
        private const string GamepadNamePrefix = "Joy";

        // Internal aliases that duplicate a raw mouse button or a modifier key, and the two keys our own UI
        // owns. Explicit opt-out: ZInput's Rebindable flag is NOT usable as the filter, because Hotbar1-8 are
        // not rebindable yet are exactly the bindings the class-slot hand-off is about.
        private static readonly HashSet<string> ExcludedButtons = new HashSet<string>(StringComparer.Ordinal)
        {
            "MouseLeft", "MouseRight", "MouseMiddle", "MouseForward", "MouseBack",
            "LShift", "Tab", "Escape",
        };

        private static readonly Dictionary<string, BindingContext> ContextByButton =
            new Dictionary<string, BindingContext>(StringComparer.Ordinal)
            {
                { "MapZoomIn", BindingContext.MapOpen },
                { "MapZoomOut", BindingContext.MapOpen },
                { "Remove", BindingContext.Building },
                { "AltPlace", BindingContext.Building },
                { "ScrollChatUp", BindingContext.Menu },
                { "ScrollChatDown", BindingContext.Menu },
                { "ChatUp", BindingContext.Menu },
                { "ChatDown", BindingContext.Menu },
                { "TabLeft", BindingContext.Menu },
                { "TabRight", BindingContext.Menu },
            };

        private static FieldInfo _buttonsField;
        private static MethodInfo _keyCodeToPath;
        private static Dictionary<string, KeyCode> _keyByPath;

        /// <summary>Appends every readable vanilla keyboard/mouse button to <paramref name="into"/>.</summary>
        public static void Collect(List<KeybindEntry> into)
        {
            var instance = ZInput.instance;
            if (instance == null) return;

            var buttons = ReadButtons(instance);
            if (buttons == null) return;

            var keyByPath = KeyByPath();
            foreach (var pair in buttons)
            {
                string name = pair.Key;
                var definition = pair.Value;
                if (definition == null || string.IsNullOrEmpty(name)) continue;
                if (ExcludedButtons.Contains(name)) continue;
                if (name.StartsWith(GamepadNamePrefix, StringComparison.Ordinal)) continue;

                string path = ActionPathOf(definition);
                if (string.IsNullOrEmpty(path) || path.IndexOf(GamepadPathMarker, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!keyByPath.TryGetValue(path, out var key) || key == KeyCode.None) continue;

                into.Add(new KeybindEntry
                {
                    Origin = BindingOrigin.VanillaButton,
                    PluginGuid = PluginGuid,
                    ModName = ModName,
                    Section = SectionName,
                    Name = name,
                    Label = definition.DisplayNameOverride ?? name,
                    Context = ContextByButton.TryGetValue(name, out var context) ? context : BindingContexts.Default,
                    Match = BindingMatch.Loose,
                    Key = key,
                    Modifiers = KeyModifiers.None,
                    CanChange = false,
                    ChangeHint = ChangeHint,
                });
            }
        }

        private static string ActionPathOf(ZInput.ButtonDef definition)
        {
            try { return definition.GetActionPath(); }
            catch (Exception ex)
            {
                UI.FiresConfigUI.Log.LogWarning($"keybinds: ZInput button '{definition.Name}' has no readable path: {ex.Message}");
                return null;
            }
        }

        private static IEnumerable<KeyValuePair<string, ZInput.ButtonDef>> ReadButtons(ZInput instance)
        {
            if (_buttonsField == null)
            {
                _buttonsField = AccessTools.Field(typeof(ZInput), ButtonsFieldName);
                if (_buttonsField == null)
                {
                    UI.FiresConfigUI.Log.LogError($"keybinds: ZInput.{ButtonsFieldName} not found - vanilla bindings cannot be scanned.");
                    return null;
                }
            }
            return _buttonsField.GetValue(instance) as IEnumerable<KeyValuePair<string, ZInput.ButtonDef>>;
        }

        // Built once per session: ~430 reflected calls, then a plain lookup. Paths compare case-insensitively
        // because ZInput writes "<Keyboard>/F" while an InputSystem rebind writes "<Keyboard>/f".
        private static Dictionary<string, KeyCode> KeyByPath()
        {
            if (_keyByPath != null) return _keyByPath;

            _keyByPath = new Dictionary<string, KeyCode>(StringComparer.OrdinalIgnoreCase);
            if (_keyCodeToPath == null)
                _keyCodeToPath = AccessTools.Method(typeof(ZInput), KeyCodeToPathMethodName, new[] { typeof(KeyCode), typeof(bool) });
            if (_keyCodeToPath == null)
            {
                UI.FiresConfigUI.Log.LogError($"keybinds: ZInput.{KeyCodeToPathMethodName} not found - vanilla bindings cannot be matched to keys.");
                return _keyByPath;
            }

            string nonePath = InvokePath(KeyCode.None);
            var arguments = new object[2];
            foreach (KeyCode key in Enum.GetValues(typeof(KeyCode)))
            {
                if (key == KeyCode.None) continue;
                arguments[0] = key;
                arguments[1] = false;
                string path = _keyCodeToPath.Invoke(null, arguments) as string;
                if (string.IsNullOrEmpty(path)) continue;
                if (nonePath != null && string.Equals(path, nonePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (path.IndexOf(GamepadPathMarker, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (!_keyByPath.ContainsKey(path)) _keyByPath[path] = key;
            }
            return _keyByPath;
        }

        private static string InvokePath(KeyCode key)
        {
            try { return _keyCodeToPath.Invoke(null, new object[] { key, false }) as string; }
            catch { return null; }
        }
    }
}
