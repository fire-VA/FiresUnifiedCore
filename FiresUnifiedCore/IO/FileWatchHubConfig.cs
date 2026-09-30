using System;
using BepInEx.Configuration;
using FiresCore.Logging;
using HarmonyLib;

namespace FiresCore.IO
{
    // Binds [File Watching] from Core's own config (per machine, read live), installs the hub, and adds its STATUS line and
    // the fires_filewatch command.
    internal static class FileWatchHubConfig
    {
        private const string Section = "File Watching";
        private const string EnabledKey = "Enabled";
        private const string IntervalKey = "Check Interval Seconds";
        private const float IntervalDefault = 2f;
        private const float IntervalMin = 0.25f;
        private const float IntervalMax = 60f;
        private const string LogPrefix = "[FileWatch] ";
        private const string StatusSource = "Files";
        private const string CommandName = "fires_filewatch";
        private const string CommandDescription = "Lists every file watcher Core serves and the mod that created it.";
        private const string EnabledDescription =
            "Core serves every mod's file watchers (config hot reload and the like) from Windows change notifications instead of " +
            "Mono re-listing the watched folders every 750 ms. Off stops file watching entirely, so .cfg edits on disk (this file " +
            "included) no longer apply live; turn it back on from the config window. This machine only.";
        private const string IntervalDescription =
            "After the first change, changes are collected for this many seconds and then delivered to the mods together, so a " +
            "burst of saves reaches each mod once.";
        private const string FoldKey = "Fold Into Config Root";
        private const string FoldDescription =
            "Watches the whole config folder from ONE change-notification handle instead of one per watched subfolder, which is " +
            "the fewest the game can run on. Each handle costs a thread and a 64 KB buffer, so folding them saves both. While " +
            "nothing is changing this is free either way - a handle only does work when a file actually changes, so an idle 4 GB " +
            "config folder costs exactly what an idle small one does. The difference appears while something is WRITING a lot " +
            "inside config: every write anywhere under it then has to be examined and discarded, and a burst large enough to " +
            "overflow the one buffer makes every mod on that handle re-check its files rather than only the mods near the churn. " +
            "On this setup the folders that get written in bulk are asset caches that no mod watches, so the trade is usually " +
            "worth it - but if config hot reload starts firing for no reason during a bundle sync or a bake, turn this off. " +
            "Applies live, and the STATUS Files line shows the folder count so you can see what it did. This machine only.";

        public static void Initialize(Harmony harmony, ConfigFile config)
        {
            try
            {
                ConfigEntry<bool> enabled = config.Bind(Section, EnabledKey, true, EnabledDescription);
                ConfigEntry<float> interval = config.Bind(Section, IntervalKey, IntervalDefault,
                    new ConfigDescription(IntervalDescription, new AcceptableValueRange<float>(IntervalMin, IntervalMax)));
                ConfigEntry<bool> fold = config.Bind(Section, FoldKey, true, FoldDescription);
                FileWatchHub.Enabled = enabled.Value;
                FileWatchHub.IntervalSeconds = interval.Value;
                FileWatchHub.FoldIntoConfigRoot = fold.Value;
                enabled.SettingChanged += (_, __) => FileWatchHub.Enabled = enabled.Value;
                interval.SettingChanged += (_, __) => FileWatchHub.IntervalSeconds = interval.Value;
                fold.SettingChanged += (_, __) => FileWatchHub.FoldIntoConfigRoot = fold.Value;

                if (!FileWatchHub.Install(harmony, message => FiresUnifiedCore.Log.LogInfo(LogPrefix + message),
                                          message => FiresUnifiedCore.Log.LogWarning(LogPrefix + message)))
                    return;
            }
            catch (Exception ex)
            {
                FiresUnifiedCore.Log.LogWarning($"{LogPrefix}setup failed, file watching left to Mono: {ex}");
                return;
            }

            StatusBanner.Register(StatusSource, FileWatchHub.StatusLine);
            try
            {
                new Terminal.ConsoleCommand(CommandName, CommandDescription, args =>
                {
                    foreach (string line in FileWatchHub.Describe()) args.Context?.AddString(line);
                    args.Context?.AddString(FileWatchHub.StatusLine());
                });
            }
            catch (Exception ex)
            {
                FiresUnifiedCore.Log.LogWarning($"{LogPrefix}{CommandName} not registered: {ex.Message}");
            }
        }
    }
}
