using System.Collections.Generic;

namespace FiresLogAnalysis;

public sealed class PluginInfo
{
    public PluginInfo(string name, string version)
    {
        Name = name;
        Version = version;
    }

    public PluginInfo(string name, string version, string guid)
        : this(name, version)
    {
        Guid = string.IsNullOrEmpty(guid) ? null : guid;
    }

    public string Name { get; }
    public string Version { get; }

    /// <summary>The BepInEx plugin GUID, when the log names it (BepInEx 5.4.23 and later); null otherwise.</summary>
    public string Guid { get; }
    public bool FailedToLoad { get; internal set; }

    public override string ToString() => Name + " " + Version;
}

public sealed class PluginList
{
    public PluginList(IReadOnlyList<PluginInfo> plugins, IReadOnlyList<PluginInfo> patchers, int? announcedPluginCount, bool loadingCompleted)
    {
        Plugins = plugins;
        Patchers = patchers;
        AnnouncedPluginCount = announcedPluginCount;
        LoadingCompleted = loadingCompleted;
    }

    public IReadOnlyList<PluginInfo> Plugins { get; }
    public IReadOnlyList<PluginInfo> Patchers { get; }
    public int? AnnouncedPluginCount { get; }
    public bool LoadingCompleted { get; }

    public int FailedCount
    {
        get
        {
            var failed = 0;
            foreach (var plugin in Plugins)
            {
                if (plugin.FailedToLoad)
                    failed++;
            }
            return failed;
        }
    }
}
