using System;
using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using FiresCore.Logging;

namespace FiresCore.Config
{
    /// <summary>
    /// Renames / regroups must migrate values, or players silently lose settings. Call it right
    /// AFTER binding the new entry: when the .cfg still holds the old section/key (an unbound "orphan"), its value moves into the new entry
    /// once (logged at Debug), and the orphan is removed so it stops showing. Without an old value nothing changes. Safe to call every start.
    /// <code>var e = Config.Bind("Keybinds", "Slot 1 key", "Alt+1", "...");
    /// FiresCore.Config.ConfigMigrate.Move(Config, "ToolHotbar", "KeybindEx1", e);</code>
    /// </summary>
    public static class ConfigMigrate
    {
        private static readonly PropertyInfo OrphansProperty = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        /// <summary>Moves [oldSection] oldKey's stored value into <paramref name="newEntry"/>. True when a value moved.</summary>
        public static bool Move(ConfigFile config, string oldSection, string oldKey, ConfigEntryBase newEntry)
        {
            if (config == null || newEntry == null || string.IsNullOrEmpty(oldSection) || string.IsNullOrEmpty(oldKey)) return false;
            var oldDef = new ConfigDefinition(oldSection, oldKey);
            if (oldDef.Equals(newEntry.Definition)) return false;
            IDictionary orphans;
            try { orphans = OrphansProperty?.GetValue(config, null) as IDictionary; }
            catch { return false; }
            if (orphans == null || !orphans.Contains(oldDef)) return false;
            string text = orphans[oldDef] as string;
            orphans.Remove(oldDef);
            string file = Path.GetFileName(config.ConfigFilePath);
            try
            {
                if (text != null) newEntry.SetSerializedValue(text);
                config.Save();
                FiresLogger.LogDebug($"[Config] {file}: moved [{oldSection}] {oldKey} = {text} -> [{newEntry.Definition.Section}] {newEntry.Definition.Key} (old key removed)");
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[Config] {file}: moving [{oldSection}] {oldKey} -> [{newEntry.Definition.Section}] {newEntry.Definition.Key} failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>The same by name: the new entry must already be bound in <paramref name="config"/>.</summary>
        public static bool Move(ConfigFile config, string oldSection, string oldKey, string newSection, string newKey)
        {
            if (config == null) return false;
            var def = new ConfigDefinition(newSection, newKey);
            return config.ContainsKey(def) && Move(config, oldSection, oldKey, config[def]);
        }
    }
}
