using System;
using System.Collections.Generic;
using System.Globalization;
using FiresLogAnalysis.Problems;

namespace FiresLogAnalysis;

/// <summary>Wording shared by the text report and the Discord summary so both describe a log the same way.</summary>
public static class AnalysisText
{
    public const string PartialLogNote = "Only part of a log, so versions and some mods may be missing. Please send the full LogOutput.log from the BepInEx folder.";

    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";
    private const string NotLogged = "not logged";
    private const string VersionNotIncluded = "version not included";
    private const string LoadedButVersionNotIncluded = "loaded, version not included";

    public static string LogKind(LogEnvironment environment)
    {
        var kind = environment.IsDedicatedServer ? "Dedicated server" : environment.ProcessName == null ? "Unknown program" : "Game client";
        return environment.OperatingSystem == null ? kind : kind + " on " + environment.OperatingSystem;
    }

    public static string GameVersion(LogEnvironment environment)
    {
        if (environment.GameVersion == null)
            return environment.IsRecognizedValheimLog ? NotLogged : VersionNotIncluded;
        return environment.NetworkVersion == null
            ? environment.GameVersion
            : environment.GameVersion + " (network " + environment.NetworkVersion + ")";
    }

    public static string BepInExVersion(LogEnvironment environment)
    {
        if (environment.BepInExVersion == null)
            return environment.HasBepInExLines ? LoadedButVersionNotIncluded : NotLogged;
        return environment.BepInExPackVersion == null
            ? environment.BepInExVersion
            : environment.BepInExVersion + " (BepInExPack " + environment.BepInExPackVersion + ")";
    }

    public static string Duration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return (int)duration.TotalHours + "h " + duration.Minutes + "m";
        if (duration.TotalMinutes >= 1)
            return duration.Minutes + "m " + duration.Seconds + "s";
        return duration.Seconds + "s";
    }

    public static string Session(LogEnvironment environment)
    {
        if (!environment.FirstTimestamp.HasValue || !environment.LastTimestamp.HasValue)
            return "no timestamps";
        var range = environment.FirstTimestamp.Value.ToString(TimestampFormat, CultureInfo.InvariantCulture)
            + " to " + environment.LastTimestamp.Value.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        return environment.SessionLength.HasValue ? range + " (" + Duration(environment.SessionLength.Value) + ")" : range;
    }

    public static string ModCount(PluginList plugins)
    {
        var text = Count(plugins.Plugins.Count, "mod");
        if (plugins.FailedCount > 0)
            text += " (" + plugins.FailedCount + " failed to load)";
        if (plugins.Patchers.Count > 0)
            text += ", " + Count(plugins.Patchers.Count, "preloader patcher");
        return text;
    }

    public static string SeverityLabel(ProblemSeverity severity)
    {
        switch (severity)
        {
            case ProblemSeverity.Critical:
                return "CRITICAL";
            case ProblemSeverity.Warning:
                return "WARNING";
            default:
                return "NOTICE";
        }
    }

    public static string Times(int occurrences) => occurrences > 1 ? " (" + occurrences.ToString("N0", CultureInfo.InvariantCulture) + "x)" : string.Empty;

    public static string Count(int count, string singular) =>
        count.ToString("N0", CultureInfo.InvariantCulture) + " " + (count == 1 ? singular : singular + "s");

    public static IEnumerable<string> ServerFacts(LogEnvironment environment)
    {
        if (environment.WorldName != null)
            yield return "World " + environment.WorldName + ", save " + environment.WorldSaveNumber;
        if (environment.ObjectsAtWorldLoad != null)
            yield return environment.ObjectsAtWorldLoad + " objects at load";
        if (environment.ObjectsAtLastCount.HasValue)
            yield return environment.ObjectsAtLastCount.Value.ToString("N0", CultureInfo.InvariantCulture) + " objects at last count";
        if (environment.PeakConnections.HasValue)
            yield return "peak of " + Count(environment.PeakConnections.Value, "connected player");
    }
}
