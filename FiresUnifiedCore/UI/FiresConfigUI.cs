using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Settings and entry points for the Fires config window. Mods may call <see cref="Register"/> to supply a
    /// display name for their config; discovery finds them either way. The window itself is
    /// <see cref="ConfigPanel"/>.
    /// </summary>
    public static class FiresConfigUI
    {
        private const string Section = "00 - Config UI";
        private static readonly string[] RivalConfigManagerFiles =
        {
            "ConfigurationManager.dll",
            "ConfigurationManagerWrapper.dll",
        };

        internal static readonly BepInEx.Logging.ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("FiresConfigUI");

        /// <summary>Fallback hotkey used only before <see cref="BindAppearance"/> has run.</summary>
        public static KeyCode ToggleKey = KeyCode.F1;

        public static void Register(string modName, ConfigFile config)
        {
            if (config == null) return;
            CfgDiscovery.SetNameOverride(config, string.IsNullOrEmpty(modName) ? "Mod" : modName);
            EnsureHost();
        }

        public static void EnsureHost() => ConfigPanel.EnsureHost();

        public static void Toggle() => ConfigPanel.Toggle();

        public static void Open() => ConfigPanel.Open();

        internal static ConfigEntry<KeyboardShortcut> CfgHotkey;
        internal static ConfigEntry<bool> CfgFiresOnly;
        internal static ConfigEntry<bool> CfgShowAdvanced;
        internal static ConfigEntry<bool> CfgKeybindsOnly;
        internal static ConfigEntry<bool> CfgMenuButton;
        internal static ConfigEntry<float> CfgWindowScale;
        internal static ConfigEntry<bool> CfgUseGameGuiScale;
        internal static ConfigEntry<bool> CfgPauseGame;

        /// <summary>True when the window should show settings a mod tagged as advanced.</summary>
        public static bool ShowAdvanced => CfgShowAdvanced != null && CfgShowAdvanced.Value;

        /// <summary>True when the window is filtered down to keybind settings only.</summary>
        public static bool KeybindsOnly => CfgKeybindsOnly != null && CfgKeybindsOnly.Value;

        /// <summary>True when the pause/main menu should carry a "Mod Settings" entry.</summary>
        public static bool ShowMenuButton => CfgMenuButton == null || CfgMenuButton.Value;

        public static float WindowScale => CfgWindowScale?.Value ?? 1f;

        public static bool UseGameGuiScale => CfgUseGameGuiScale == null || CfgUseGameGuiScale.Value;

        public static bool PauseGame => CfgPauseGame != null && CfgPauseGame.Value;

        /// <summary>Bind the window's own settings and register that config so its section appears in the window.</summary>
        public static void BindAppearance(ConfigFile cfg)
        {
            if (cfg == null || CfgHotkey != null) return;

            var defaultKey = AnotherConfigManagerInstalled() ? KeyCode.F8 : KeyCode.F1;
            ToggleKey = defaultKey;

            CfgHotkey = cfg.Bind(Section, "00 Open Key", new KeyboardShortcut(defaultKey),
                "Key that opens the Fires config window. (Console command 'va_config' also toggles it.) "
                + "Defaults to F1, or F8 when another configuration manager is installed and would claim F1.");
            CfgFiresOnly = cfg.Bind(Section, "05 FiresModsOnly", false,
                "Limit the window to the Fires family instead of every loaded plugin. Toggleable in the window header.");
            CfgKeybindsOnly = cfg.Bind(Section, "06b KeybindsOnly", false,
                "Show only the keybind settings, across every mod — a controls screen rather than a config "
                + "screen. Unlike ConfigurationManager this covers KeyCode settings as well as KeyboardShortcut.");
            CfgShowAdvanced = cfg.Bind(Section, "06 ShowAdvanced", false,
                "Show the deep tuning settings that mods mark as advanced. OFF keeps the window to the "
                + "settings most players need. The same tag hides them in ConfigurationManager, so both windows agree.");
            CfgMenuButton = cfg.Bind(Section, "07 MenuButton", true,
                "Add a 'Mod Settings' entry to the main menu and the in-game pause menu.");
            CfgWindowScale = cfg.Bind(Section, "08 WindowScale", 1f,
                new ConfigDescription("Size of the config window and everything in it.",
                    new AcceptableValueRange<float>(ConfigWindowScale.MinFactor, ConfigWindowScale.MaxFactor)));
            CfgUseGameGuiScale = cfg.Bind(Section, "09 UseGameGuiScale", true,
                "Also scale the window by Valheim's own GUI scale (Settings - Accessibility - Scale GUI), so it "
                + "matches the rest of the game's UI. Turn this off to size the window purely by WindowScale.");
            CfgPauseGame = cfg.Bind(Section, "10 PauseGame", false,
                "Pause the game while the config window is open, when the game can be paused (single player).");

            CfgFiresOnly.SettingChanged += (sender, args) => CfgDiscovery.Rebuild();
            CfgMenuButton.SettingChanged += (sender, args) => ConfigMenuButton.Refresh();

            Register("FiresUnifiedCore", cfg);
        }

        // Decided from the installed files rather than the loaded plugin list: Core loads before most of the
        // pack, so Chainloader.PluginInfos is still filling in when the hotkey default has to be chosen.
        private static bool AnotherConfigManagerInstalled()
        {
            try
            {
                foreach (string fileName in RivalConfigManagerFiles)
                    if (Directory.GetFiles(Paths.PluginPath, fileName, SearchOption.AllDirectories).Length > 0) return true;
            }
            catch (Exception ex) { Log.LogWarning("config manager scan failed, defaulting the hotkey to F1: " + ex.Message); }
            return false;
        }
    }
}
