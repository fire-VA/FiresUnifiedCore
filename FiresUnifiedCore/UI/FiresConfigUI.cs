using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Registration and settings for the Fires config window. Mods call <see cref="Register"/> to supply a
    /// display name for their config; the window itself is drawn by <see cref="ConfigPanel"/>.
    /// </summary>
    public static class FiresConfigUI
    {
        internal class ModEntry
        {
            public string Name;
            public ConfigFile Config;
        }

        private static readonly List<ModEntry> s_mods = new List<ModEntry>();
        internal static IReadOnlyList<ModEntry> Mods => s_mods;

        // Direct BepInEx log source - bypasses FUC's RateLimitedLogHandler so draw errors aren't swallowed.
        internal static readonly BepInEx.Logging.ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("FiresConfigUI");

        /// <summary>Fallback hotkey that opens the Fires window (the bindable CfgHotkey overrides it).
        /// Default F8 so we don't collide with shudnal's F1 - rebind it in the "00 - Config UI" section.</summary>
        public static KeyCode ToggleKey = KeyCode.F8;

        /// <summary>Title shown at the top of the window.</summary>
        public static string Title = "Fires Configuration";

        /// <summary>Register a mod's config so it appears in the Fires window. Idempotent (re-registering
        /// the same ConfigFile just refreshes its display name).</summary>
        public static void Register(string modName, ConfigFile config)
        {
            if (config == null) return;
            // Register is now an enrichment layer over auto-discovery: it supplies a curated display name
            // that wins over the plugin's metadata name (dedup is by ConfigFile reference in discovery).
            CfgDiscovery.SetNameOverride(config, Clean(modName));

            foreach (var mod in s_mods)
                if (ReferenceEquals(mod.Config, config)) { mod.Name = Clean(modName); return; }
            s_mods.Add(new ModEntry { Name = Clean(modName), Config = config });
            s_mods.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            EnsureHost();
            Debug.Log($"[FiresConfigUI] registered '{Clean(modName)}' ({config.Keys.Count} entries, {s_mods.Count} mod(s)).");
        }

        private static string Clean(string modName) => string.IsNullOrEmpty(modName) ? "Mod" : modName;

        /// <summary>Create the always-on UI host (safe to call repeatedly). The window is now a uGUI Canvas
        /// panel (<see cref="ConfigPanel"/>) - the old IMGUI host is retired.</summary>
        public static void EnsureHost() => ConfigPanel.EnsureHost();

        /// <summary>Open/close the window from code (used by the va_config console command).</summary>
        public static void Toggle() => ConfigPanel.Toggle();

        // Kept for API compatibility; the window no longer has a font setting.
        public enum UiFont { Default, Arial, Consolas, Serif }

        internal static ConfigEntry<KeyboardShortcut> CfgHotkey;
        internal static ConfigEntry<bool> CfgShowAllPlugins;
        internal static ConfigEntry<bool> CfgShowAdvanced;

        /// <summary>True when the window should show settings a mod tagged as advanced.</summary>
        public static bool ShowAdvanced => CfgShowAdvanced == null || CfgShowAdvanced.Value;

        /// <summary>Bind the window's own settings into <paramref name="cfg"/> and register that config so its
        /// "00 - Config UI" section appears in the window. Call once (Core does this in Setup).</summary>
        public static void BindAppearance(ConfigFile cfg)
        {
            if (cfg == null || CfgHotkey != null) return;
            CfgHotkey = cfg.Bind("00 - Config UI", "00 Hotkey", new KeyboardShortcut(KeyCode.F8),
                "Key that opens the Fires config window. (Console command 'va_config' also toggles it.)");
            CfgShowAllPlugins = cfg.Bind("00 - Config UI", "05 ShowAllPlugins", false,
                "Show EVERY loaded plugin's config in the window, not just Fires mods. Re-open the window to refresh.");
            CfgShowAdvanced = cfg.Bind("00 - Config UI", "06 ShowAdvanced", false,
                "Show the deep tuning sliders that mods mark as advanced. OFF keeps the window to the "
                + "toggles most players need. The same tag hides them in ConfigurationManager, so both "
                + "windows agree.");

            // Flipping ShowAll changes the discovery source, so rebuild descriptors + the cached nav lists.
            CfgShowAllPlugins.SettingChanged += (s, e) => CfgDiscovery.Rebuild();

            Register("FiresUnifiedCore", cfg);
        }
    }
}
