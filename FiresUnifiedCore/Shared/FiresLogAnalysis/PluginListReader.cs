using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FiresLogAnalysis;

internal sealed class PluginListReader
{
    private const string BepInExSource = "BepInEx";
    private const string BepInExPreloaderName = "BepInEx.Preloader";
    private const string ChainloaderCompleteMessage = "Chainloader startup complete";

    private static readonly Regex AnnouncedPluginCount = new Regex(@"^(?<count>\d+) plugins? to load$", RegexOptions.Compiled);
    // The trailing "(guid)" is OPTIONAL and that optionality is the whole point. BepInEx 5.4.23 logs
    //     Loading [Conditional Config Sync 1.0.9] (_shudnal.ConditionalConfigSync)
    // while older builds log just
    //     Loading [PlantEverything 1.21.3]
    // This pattern was anchored with `$` straight after the bracket, so on any current BepInEx it
    // matched NOTHING and every report said "0 mods" while listing a plugin count of 22 two lines
    // earlier. Seen on a Linux dedicated server log.
    private static readonly Regex LoadingPlugin = new Regex(
        @"^Loading \[(?<name>.+) (?<version>\d+(?:\.\d+){1,3})\](?:\s+\((?<guid>[^)]+)\))?$", RegexOptions.Compiled);
    private static readonly Regex LoadedPatcher = new Regex(@"^Loaded \d+ patcher methods? from \[(?<name>.+) (?<version>\d+(?:\.\d+){1,3})\]$", RegexOptions.Compiled);
    // Same optional "(guid)" as LoadingPlugin. No log on hand contains this line, so the tolerance is
    // precautionary rather than verified - but a pattern that silently matches nothing is exactly the
    // failure being fixed here, and tolerating a suffix cannot make it match less.
    private static readonly Regex PluginLoadError = new Regex(
        @"^Error loading \[(?<name>.+) (?<version>\d+(?:\.\d+){1,3})\](?:\s+\([^)]+\))? ?: ", RegexOptions.Compiled);

    private readonly List<PluginInfo> plugins = new List<PluginInfo>();
    private readonly List<PluginInfo> patchers = new List<PluginInfo>();
    private int? announcedPluginCount;
    private bool loadingCompleted;

    /// <summary>Returns true for lines that only list mods. Load errors are recorded but left for the problem detectors.</summary>
    public bool TryRead(LogEntry entry)
    {
        if (entry.Source != BepInExSource)
            return false;

        Match match;
        if ((match = PluginLoadError.Match(entry.Message)).Success)
        {
            MarkFailed(match.Groups["name"].Value, match.Groups["version"].Value);
            return false;
        }
        if ((match = LoadingPlugin.Match(entry.Message)).Success)
        {
            plugins.Add(new PluginInfo(match.Groups["name"].Value, match.Groups["version"].Value, match.Groups["guid"].Value));
            return true;
        }
        if ((match = LoadedPatcher.Match(entry.Message)).Success)
        {
            if (match.Groups["name"].Value != BepInExPreloaderName)
                patchers.Add(new PluginInfo(match.Groups["name"].Value, match.Groups["version"].Value));
            return true;
        }
        if ((match = AnnouncedPluginCount.Match(entry.Message)).Success)
        {
            if (LoggedNumbers.TryParseInt(match.Groups["count"].Value, out var count))
                announcedPluginCount = count;
            return true;
        }
        if (entry.Message.StartsWith(ChainloaderCompleteMessage, System.StringComparison.Ordinal))
        {
            loadingCompleted = true;
            return true;
        }
        return false;
    }

    public PluginList ToPluginList() => new PluginList(plugins, patchers, announcedPluginCount, loadingCompleted);

    private void MarkFailed(string name, string version)
    {
        for (var index = plugins.Count - 1; index >= 0; index--)
        {
            if (plugins[index].Name == name && plugins[index].Version == version)
            {
                plugins[index].FailedToLoad = true;
                return;
            }
        }
    }
}
