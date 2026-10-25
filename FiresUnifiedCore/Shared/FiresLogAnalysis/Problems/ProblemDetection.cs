using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FiresLogAnalysis.Problems;

internal sealed class AnalysisContext
{
    public AnalysisContext(LogEnvironment environment, ProblemCollector problems)
    {
        Environment = environment;
        Problems = problems;
    }

    public LogEnvironment Environment { get; }
    public ProblemCollector Problems { get; }

    /// <summary>Peers (Steam ID or host name as logged) that the game or a mod already disconnected.</summary>
    public HashSet<string> DisconnectedPeers { get; } = new HashSet<string>(StringComparer.Ordinal);
}

internal interface IProblemDetector
{
    /// <summary>Records any problem the entry reveals. Returns true when the entry is fully explained by a problem.</summary>
    bool TryExplain(LogEntry entry, AnalysisContext context);
}

/// <summary>One exact log message shape. The fragment is a cheap substring gate checked before the regex.</summary>
internal sealed class MessageRule
{
    private readonly string requiredFragment;
    private readonly Regex pattern;
    private readonly Action<Match, LogEntry, AnalysisContext> explain;

    public MessageRule(string requiredFragment, string pattern, Action<Match, LogEntry, AnalysisContext> explain)
    {
        this.requiredFragment = requiredFragment;
        this.pattern = new Regex(pattern, RegexOptions.Compiled);
        this.explain = explain;
    }

    public bool TryExplain(LogEntry entry, AnalysisContext context)
    {
        if (entry.Message.IndexOf(requiredFragment, StringComparison.Ordinal) < 0)
            return false;

        var match = pattern.Match(entry.Message);
        if (!match.Success)
            return false;

        explain(match, entry, context);
        return true;
    }

    public static bool TryExplainWithAny(MessageRule[] rules, LogEntry entry, AnalysisContext context)
    {
        foreach (var rule in rules)
        {
            if (rule.TryExplain(entry, context))
                return true;
        }
        return false;
    }
}
