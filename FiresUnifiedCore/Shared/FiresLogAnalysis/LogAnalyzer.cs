using System;
using System.IO;
using System.Text;
using FiresLogAnalysis.Problems;

namespace FiresLogAnalysis;

public static class LogAnalyzer
{
    private const string UnityExplorerSource = "UnityExplorer";
    private const string UnityExplorerMirrorPrefix = "[Unity] ";

    public static LogAnalysis Analyze(Stream logContent)
    {
        using (var reader = new StreamReader(logContent, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            return Analyze(reader);
    }

    public static LogAnalysis Analyze(TextReader reader)
    {
        var environment = new LogEnvironment();
        var environmentReader = new EnvironmentReader(environment);
        var pluginReader = new PluginListReader();
        var performanceReader = new RunPerformanceReader();
        var problems = new ProblemCollector();
        var context = new AnalysisContext(environment, problems);
        var fgnProblems = new FgnProblems();
        var detectors = CreateDetectors(fgnProblems);
        var messageGrouper = new MessageGrouper();
        var harmlessCounter = new HarmlessMessageCounter();
        var excerpts = new ExcerptRecorder();
        var timeline = new TimelineBuilder();
        var containsErrorWithStackTrace = false;
        LogEntry firstError = null;

        foreach (var entry in LogEntryReader.Read(reader))
        {
            excerpts.Begin(entry);
            var isHit = Read(entry);
            timeline.Observe(entry, isHit);
            excerpts.End(entry);
            if (isHit && entry.IsError && firstError == null)
                firstError = entry;
        }

        // Whether the entry is a real error or warning: one the TIMELINE shows - grouped below, or explained
        // by a problem detector.
        bool Read(LogEntry entry)
        {
            containsErrorWithStackTrace |= IsErrorWithStackTrace(entry);
            environmentReader.Read(entry);
            // Before the problem detectors, because FDT's per-second readings are measurements and
            // not findings: left in the noise lists they would bury a real warning under hundreds of
            // identical timing rows.
            if (performanceReader.TryRead(entry))
                return false;
            if (pluginReader.TryRead(entry) || IsUnityExplorerMirror(entry))
                return false;
            if (IsExplainedByProblem(entry, context, detectors))
                return entry.IsWarningOrError;
            if (!entry.IsWarningOrError)
                return false;
            if (harmlessCounter.TryCount(entry, environment))
            {
                timeline.CountHarmless();
                return false;
            }
            messageGrouper.Add(entry, excerpts);
            return true;
        }

        var plugins = pluginReader.ToPluginList();
        fgnProblems.Finish(context, plugins);

        return new LogAnalysis(
            environment,
            plugins,
            performanceReader.ToRunPerformance(),
            problems.ToSortedList(),
            messageGrouper.ToSortedList(),
            harmlessCounter.ToSortedList(),
            containsErrorWithStackTrace,
            timeline.Finish(),
            firstError,
            messageGrouper.ToFamilies());
    }

    private static bool IsErrorWithStackTrace(LogEntry entry) => entry.IsError && entry.ContinuationLines.Count > 0;

    private static IProblemDetector[] CreateDetectors(FgnProblems fgnProblems) => new IProblemDetector[]
    {
        fgnProblems,
        new DisconnectProblems(),
        new BepInExLoadProblems(),
        new ServerSyncProblems(),
        new VersionHandshakeProblems(),
        new JotunnProblems(),
        new GameConnectionProblems(),
        new WorldDataProblems(),
        new OutdatedModProblems(),
        new AssetBundleProblems(),
        new ZoneResetProblems(),
    };

    /// <summary>UnityExplorer repeats every Unity log message under its own source with a "[Unity] " prefix.</summary>
    private static bool IsUnityExplorerMirror(LogEntry entry) =>
        entry.Source == UnityExplorerSource && entry.Message.StartsWith(UnityExplorerMirrorPrefix, StringComparison.Ordinal);

    private static bool IsExplainedByProblem(LogEntry entry, AnalysisContext context, IProblemDetector[] detectors)
    {
        foreach (var detector in detectors)
        {
            if (detector.TryExplain(entry, context))
                return true;
        }
        return false;
    }
}
