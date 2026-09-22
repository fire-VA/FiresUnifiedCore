using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx.Configuration;
using FiresCore.Logging;
using FiresCore.Storage;

namespace FiresCore.Config
{
    /// <summary>
    /// Named config snapshots per mod, written in BepInEx's own .cfg format so a preset is shared by
    /// copying one file, and any mod's live .cfg can be dropped in as a preset unchanged.
    /// </summary>
    public static class FiresConfigPresets
    {
        public const string PresetExtension = ".cfg";

        private static readonly char[] SectionTrim = { '[', ']' };

        public static string PresetFolder(string modName) => FiresConfigPaths.PresetsFor(modName);

        public static string PresetPath(string modName, string presetName) =>
            Path.Combine(PresetFolder(modName), SanitizeFileName(presetName) + PresetExtension);

        public static IReadOnlyList<string> ListPresetNames(string modName)
        {
            try
            {
                return Directory.GetFiles(PresetFolder(modName), "*" + PresetExtension)
                    .Select(Path.GetFileNameWithoutExtension)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresConfigPresets] Listing presets for {modName} failed: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        public static bool SaveCurrentValuesAsPreset(string modName, ConfigFile config, string presetName)
        {
            if (config == null || string.IsNullOrWhiteSpace(presetName)) return false;

            try
            {
                File.WriteAllText(PresetPath(modName, presetName), BuildPresetText(modName, presetName, config),
                    new UTF8Encoding(false));
                FiresLogger.LogInfo($"[FiresConfigPresets] Saved preset '{presetName}' for {modName} ({config.Keys.Count} entries).");
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresConfigPresets] Saving preset '{presetName}' for {modName} failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Applies every entry the preset names, leaving entries it omits untouched so partial presets work.
        /// Returns how many live entries changed value. SaveOnConfigSet is held off across the whole apply so
        /// one file write lands at the end instead of one per entry.
        /// </summary>
        public static int ApplyPreset(string modName, ConfigFile config, string presetName)
        {
            if (config == null) return 0;

            string path = PresetPath(modName, presetName);
            if (!File.Exists(path))
            {
                FiresLogger.LogWarning($"[FiresConfigPresets] Preset '{presetName}' not found for {modName} at {path}.");
                return 0;
            }

            Dictionary<string, string> presetValues;
            try
            {
                presetValues = ParsePresetFile(path);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresConfigPresets] Reading preset '{presetName}' for {modName} failed: {ex.Message}");
                return 0;
            }

            bool saveOnSet = config.SaveOnConfigSet;
            config.SaveOnConfigSet = false;
            int changed = 0;
            int unmatched = 0;

            try
            {
                foreach (var definition in config.Keys.ToList())
                {
                    if (!presetValues.TryGetValue(EntryKey(definition.Section, definition.Key), out string serialized))
                    {
                        unmatched++;
                        continue;
                    }

                    var entry = config[definition];
                    if (entry.GetSerializedValue() == serialized) continue;

                    try
                    {
                        entry.SetSerializedValue(serialized);
                        changed++;
                    }
                    catch (Exception ex)
                    {
                        FiresLogger.LogWarning(
                            $"[FiresConfigPresets] '{definition.Section}/{definition.Key}' rejected \"{serialized}\": {ex.Message}");
                    }
                }
            }
            finally
            {
                config.SaveOnConfigSet = saveOnSet;
            }

            try { config.Save(); }
            catch (Exception ex) { FiresLogger.LogWarning($"[FiresConfigPresets] Saving {modName} after preset apply failed: {ex.Message}"); }

            FiresLogger.LogInfo(
                $"[FiresConfigPresets] Applied '{presetName}' to {modName}: {changed} changed, {unmatched} not in preset.");
            return changed;
        }

        public static bool DeletePreset(string modName, string presetName)
        {
            try
            {
                string path = PresetPath(modName, presetName);
                if (!File.Exists(path)) return false;
                File.Delete(path);
                FiresLogger.LogInfo($"[FiresConfigPresets] Deleted preset '{presetName}' for {modName}.");
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[FiresConfigPresets] Deleting preset '{presetName}' for {modName} failed: {ex.Message}");
                return false;
            }
        }

        private static string BuildPresetText(string modName, string presetName, ConfigFile config)
        {
            var text = new StringBuilder();
            text.AppendLine($"## Fires config preset '{presetName}' for {modName}");
            text.AppendLine($"## Saved {DateTime.Now:yyyy-MM-dd HH:mm}");
            text.AppendLine();

            foreach (var section in config.Keys.GroupBy(definition => definition.Section)
                                               .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
            {
                text.AppendLine($"[{section.Key}]");
                text.AppendLine();
                foreach (var definition in section.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
                    text.AppendLine($"{definition.Key} = {config[definition].GetSerializedValue()}");
                text.AppendLine();
            }

            return text.ToString();
        }

        private static Dictionary<string, string> ParsePresetFile(string path)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string section = string.Empty;

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;

                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Trim(SectionTrim).Trim();
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator <= 0) continue;

                values[EntryKey(section, line.Substring(0, separator).Trim())] = line.Substring(separator + 1).Trim();
            }

            return values;
        }

        private static string EntryKey(string section, string key) => section + "␟" + key;

        private static string SanitizeFileName(string name)
        {
            var cleaned = new StringBuilder(name.Length);
            foreach (char character in name.Trim())
                cleaned.Append(Path.GetInvalidFileNameChars().Contains(character) ? '_' : character);
            return cleaned.Length == 0 ? "preset" : cleaned.ToString();
        }
    }
}
