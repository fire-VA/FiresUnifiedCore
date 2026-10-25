using System.Collections.Generic;
using FiresLogAnalysis.Problems;

namespace FiresLogAnalysis;

public sealed class LogAnalysis
{
    internal LogAnalysis(
        LogEnvironment environment,
        PluginList plugins,
        RunPerformance performance,
        IReadOnlyList<Problem> problems,
        IReadOnlyList<MessageGroup> messageGroups,
        IReadOnlyList<HarmlessGroup> harmlessGroups,
        bool containsErrorWithStackTrace,
        Timeline timeline,
        LogEntry firstError,
        IReadOnlyList<MessageGroup> messageFamilies)
    {
        Timeline = timeline;
        FirstError = firstError;
        MessageFamilies = messageFamilies;
        Environment = environment;
        Plugins = plugins;
        Performance = performance;
        Problems = problems;
        MessageGroups = messageGroups;
        HarmlessGroups = harmlessGroups;
        LooksLikeLog = environment.HasBepInExLines || environment.FirstTimestamp.HasValue || containsErrorWithStackTrace;

        foreach (var group in messageGroups)
        {
            if (group.IsError)
                ErrorCount += group.Occurrences;
            else
                WarningCount += group.Occurrences;
        }
        foreach (var group in harmlessGroups)
            HarmlessCount += group.Occurrences;
    }

    public LogEnvironment Environment { get; }
    public PluginList Plugins { get; }

    /// <summary>How the run performed, read from the FiresDebugginTools diagnostics. Null when the log has none.</summary>
    public RunPerformance Performance { get; }
    public IReadOnlyList<Problem> Problems { get; }
    public IReadOnlyList<MessageGroup> MessageGroups { get; }
    public IReadOnlyList<HarmlessGroup> HarmlessGroups { get; }

    /// <summary>The real errors and warnings in log order, numbered, with their context (the report's TIMELINE).</summary>
    public Timeline Timeline { get; }

    /// <summary>MessageGroups with name families folded ("Failed to resolve prefab: &lt;name&gt;"), for the detailed report.</summary>
    public IReadOnlyList<MessageGroup> MessageFamilies { get; }

    /// <summary>The first real error in the log (not known-harmless), or null.</summary>
    public LogEntry FirstError { get; }
    public int ErrorCount { get; }
    public int WarningCount { get; }
    public int HarmlessCount { get; }

    /// <summary>True when any line is written like log output: a BepInEx "[Level:Source]" header, a leading timestamp, or an exception followed by its stack trace.</summary>
    public bool LooksLikeLog { get; }

    public ProblemSeverity? WorstSeverity => Problems.Count > 0 ? Problems[0].Severity : (ProblemSeverity?)null;

    /// <summary>The log starts at BepInEx's startup banner but ends before the chainloader finished loading mods.</summary>
    public bool EndsBeforeModsFinishedLoading => Environment.BepInExVersion != null && !Plugins.LoadingCompleted && Plugins.Plugins.Count > 0;
}
