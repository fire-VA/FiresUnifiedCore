using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using FiresCore.Logging;

namespace FiresCore.UI
{
    /// <summary>
    /// Settings left in a .cfg that the mod no longer binds ("orphans":
    /// renamed or removed keys) keep showing in the file and confuse players. BepInEx keeps them in ConfigFile.OrphanedEntries and writes
    /// them back on every Save.
    /// - Core prunes its OWN known-dead keys once all plugins have loaded (only orphans in the listed sections; live keys are never touched).
    /// - The config window offers a per-mod "Remove N old settings" button (two clicks). Some mods bind keys late, so that one is never
    ///   automatic; it keeps a copy of the file first (&lt;cfg&gt;.bak-prune-&lt;time&gt;).
    /// </summary>
    internal static class StaleEntries
    {
        // Core's own: the old config-UI numbering, the pre-rename progression block, and the old barber scale keys.
        private static readonly string[] CoreDeadSections = { "00 - Config UI", "FiresClasses.Progression" };
        private static readonly string[] CoreDeadKeys = { "Barber|Scale minimum", "Barber|Scale maximum" };

        private static readonly PropertyInfo OrphansProperty = typeof(ConfigFile).GetProperty("OrphanedEntries", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private static readonly FieldInfo OrphansField = typeof(ConfigFile).GetField("<OrphanedEntries>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

        private static IDictionary Orphans(ConfigFile file)
        {
            if (file == null) return null;
            try { return (OrphansProperty?.GetValue(file, null) ?? OrphansField?.GetValue(file)) as IDictionary; }
            catch { return null; }
        }

        internal static int Count(ConfigFile file) => Orphans(file)?.Count ?? 0;

        internal static List<ConfigDefinition> List(ConfigFile file)
        {
            var orphans = Orphans(file);
            return orphans == null ? new List<ConfigDefinition>() : orphans.Keys.OfType<ConfigDefinition>().ToList();
        }

        /// <summary>Removes the orphans `match` accepts (all when null), saves, and logs them. Returns how many went.</summary>
        internal static int Prune(ConfigFile file, Func<ConfigDefinition, bool> match, string why, bool backup)
        {
            var orphans = Orphans(file);
            if (orphans == null || orphans.Count == 0) return 0;
            var gone = orphans.Keys.OfType<ConfigDefinition>().Where(d => match == null || match(d)).ToList();
            if (gone.Count == 0) return 0;
            string backupPath = null;
            if (backup)
            {
                try
                {
                    backupPath = file.ConfigFilePath + ".bak-prune-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    if (File.Exists(file.ConfigFilePath)) File.Copy(file.ConfigFilePath, backupPath, false);
                }
                catch (Exception ex) { FiresLogger.LogWarning($"[Config] prune: no backup of {Path.GetFileName(file.ConfigFilePath)} ({ex.Message}); nothing removed"); return 0; }
            }
            foreach (var d in gone) orphans.Remove(d);
            try { file.Save(); }
            catch (Exception ex) { FiresLogger.LogWarning($"[Config] prune: saving {Path.GetFileName(file.ConfigFilePath)} failed: {ex.Message}"); }
            FiresLogger.LogDebug($"[Config] removed {gone.Count} old setting(s) from {Path.GetFileName(file.ConfigFilePath)} ({why}){(backupPath != null ? "; copy kept at " + Path.GetFileName(backupPath) : "")}: "
                + string.Join(", ", gone.Select(d => $"[{d.Section}] {d.Key}")));
            CfgDiscovery.Invalidate();
            return gone.Count;
        }

        /// <summary>Core's own dead keys, once every plugin has bound its config (TitleScene / WorldStart).</summary>
        internal static void PruneCoreKnown(ConfigFile coreConfig)
        {
            Prune(coreConfig, d => CoreDeadSections.Contains(d.Section) || CoreDeadKeys.Contains(d.Section + "|" + d.Key),
                "Core's own retired keys", backup: false);
        }
    }
}
