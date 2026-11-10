using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using FiresCore.Logging;

namespace FiresCore.Config
{
    /// <summary>
    /// 1086 ([generator], Fire 11-01: configs live in their own mods): Core no longer binds another mod's settings, so BepInEx keeps the
    /// old values in Core's cfg as orphans (ConfigFile.OrphanedEntries) and writes them back on every Save. Once every plugin has loaded
    /// (CoreEvents.AllPluginsLoaded), each orphan whose (section, key) an owner provides (CoreSettings.IsProvided) leaves Core's cfg; an
    /// "[Auto defaults] &lt;Section&gt; - &lt;Key&gt;" marker goes with its key. An orphan with no provider (owner not installed) stays, so
    /// the player's saved value is there for that owner's later migration (CoreSettings.TryReadCoreSaved). A provider that arrives later
    /// removes its key then. One Info line; failures are a Warning.
    /// </summary>
    internal static class CoreCfgCleanup
    {
        private const string AutoSection = "Auto defaults";
        private static bool s_hooked;

        internal static void Hook()
        {
            if (s_hooked) return;
            s_hooked = true;
            FiresCore.Events.CoreEvents.WhenAllPluginsLoaded(Sweep);
        }

        private static IDictionary Orphans(ConfigFile file)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            return (typeof(ConfigFile).GetProperty("OrphanedEntries", flags)?.GetValue(file, null)
                    ?? typeof(ConfigFile).GetField("<OrphanedEntries>k__BackingField", flags)?.GetValue(file)) as IDictionary;
        }

        private static bool s_deadOrphan(ConfigDefinition d) => d.Section == "GroupHud" || d.Section == "Storage"
            || (d.Section == "Camera" && (d.Key == "Ghost bow opacity while aiming" || d.Key == "Hide your body in first person" || d.Key == "First person eye forward (m)"))
            || (d.Section == "Snow" && d.Key == "Winter snow depth (m)") || (d.Section == "MonsterArmorFit" && d.Key == "Name-match unmapped neck")
            || (d.Section == "Performance" && d.Key == "Reuse Ground Checks");

        private static bool Provided(ConfigDefinition d)
        {
            if (d == null) return false;
            if (CoreSettings.IsProvided(d.Section, d.Key)) return true;
            if (s_deadOrphan(d)) return true;   // 0.2.1086: entries no code reads any more (proposal 11-02, D)
            if (d.Section == AutoSection) return true;   // hidden markers carry no player value (proposal 11-02, D)
            if (d.Section == "Climbing and vaulting" && (d.Key == "Split migration done" || d.Key == "Split migration owner check")) return true;
            return false;
        }

        private static void Sweep()
        {
            try
            {
                ConfigFile file = FiresCore.FiresUnifiedCore.Instance?.Config;
                IDictionary orphans = file != null ? Orphans(file) : null;
                if (orphans == null) { FiresLogger.LogWarning("[CoreSettings] stale-entry cleanup: Core's cfg orphan list not found; nothing removed"); return; }
                var remove = new List<object>();
                int kept = 0;
                foreach (object k in orphans.Keys)
                {
                    if (Provided(k as ConfigDefinition)) remove.Add(k); else kept++;
                }
                foreach (object k in remove) orphans.Remove(k);
                if (remove.Count > 0) file.Save();
                FiresLogger.LogInfo($"[CoreSettings] removed {remove.Count} moved setting(s) from Core's cfg; {kept} kept (owner not installed)");
                CoreSettings.Provided += (section, key, owner) => Late(file, section, key);
            }
            catch (Exception e) { FiresLogger.LogWarning($"[CoreSettings] stale-entry cleanup failed: {e.GetType().Name}: {e.Message}"); }
        }

        private static void Late(ConfigFile file, string section, string key)
        {
            try
            {
                IDictionary orphans = Orphans(file);
                if (orphans == null) return;
                var remove = new List<object>();
                foreach (object k in orphans.Keys)
                {
                    if (!(k is ConfigDefinition d)) continue;
                    if ((d.Section == section && d.Key == key) || (d.Section == AutoSection && d.Key == section + " - " + key)) remove.Add(k);
                }
                if (remove.Count == 0) return;
                foreach (object k in remove) orphans.Remove(k);
                file.Save();
                FiresLogger.LogDebug($"[CoreSettings] {section}/{key}: provided late by {Owner(section, key)}; removed from Core's cfg");
            }
            catch (Exception e) { FiresLogger.LogWarning($"[CoreSettings] stale-entry cleanup ({section}/{key}) failed: {e.Message}"); }
        }

        private static string Owner(string section, string key) => CoreSettings.ProviderOf(section, key) ?? "?";
    }
}
