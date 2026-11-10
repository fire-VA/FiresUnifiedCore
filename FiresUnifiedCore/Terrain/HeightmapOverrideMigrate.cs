using System;
using System.IO;
using BepInEx.Configuration;
using FiresCore.Config;
using FiresCore.Logging;

namespace FiresCore.Terrain
{
    /// <summary>
    /// Core 0.2.1086 (Fire 11-02, Tools\CONFIG_PROPOSAL_20261102.md): the values FAT 0.2.1295-0.2.1334 saved under [HeightmapOverride] come
    /// back once: only into a key still at Core's default, one log line per key, once per install (marker file, as FAT's Ocean import did).
    /// FAT's file is only read. Call after HeightmapOverrideConfig.Initialize and before BindToSync.
    /// </summary>
    internal static class HeightmapOverrideMigrate
    {
        private const string FatCfg = "com.Fire.FiresAdminTerrain.cfg";
        private const string Marker = "com.Fire.FiresUnifiedCore.heightmap-imported";

        internal static void Run()
        {
            try
            {
                string marker = Path.Combine(BepInEx.Paths.ConfigPath, Marker);
                if (File.Exists(marker)) return;
                // Fire 11-02: "Heightmap defaults to on". FAT's old default wrote false into its file and Core's cfg can hold an unread leftover, so a saved false
                // isn't a real choice: once per install it's set ON. A player who turns it off later keeps that (the marker stops this running again).
                if (!HeightmapOverrideConfig.Enabled.Value) { HeightmapOverrideConfig.Enabled.Value = true; FiresLogger.LogInfo("[HeightmapOverride] Enabled set ON once (Fire 11-02 default)"); }
                Copy(HeightmapOverrideConfig.MaxHeight);
                Copy(HeightmapOverrideConfig.MinHeight);
                File.WriteAllText(marker, "[HeightmapOverride] checked against " + FatCfg + "\n");
            }
            catch (Exception e) { FiresLogger.LogWarning($"[HeightmapOverride] FAT's saved values not read ({e.GetType().Name}: {e.Message})"); }
        }

        private static void Copy(ConfigEntryBase entry)
        {
            if (!Equals(entry.BoxedValue, entry.DefaultValue)) return;
            if (!CoreSettings.TryReadSaved(FatCfg, entry.Definition.Section, entry.Definition.Key, out string old)) return;
            object value = TomlTypeConverter.ConvertToValue(old, entry.SettingType);
            if (Equals(value, entry.BoxedValue)) return;
            entry.BoxedValue = value;
            FiresLogger.LogInfo($"[HeightmapOverride] copied {entry.Definition.Key} = {old} from {FatCfg}");
        }
    }
}
