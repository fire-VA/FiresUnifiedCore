using System;
using System.Collections.Generic;

namespace FiresLogAnalysis.Problems;

internal sealed class ProblemCollector
{
    private readonly Dictionary<string, Problem> problemsByTitle = new Dictionary<string, Problem>(StringComparer.Ordinal);

    public Problem Report(ProblemSeverity severity, string title, string advice, LogEntry entry)
    {
        var problem = FindOrCreate(severity, title, advice, entry);
        problem.CountOccurrence();
        return problem;
    }

    /// <summary>For follow-up lines that add detail to a problem without being a new occurrence of it.</summary>
    public Problem FindOrCreate(ProblemSeverity severity, string title, string advice, LogEntry entry)
    {
        if (!problemsByTitle.TryGetValue(title, out var problem))
        {
            problem = new Problem(severity, title, advice, entry.LineNumber);
            problemsByTitle.Add(title, problem);
        }
        return problem;
    }

    public List<Problem> ToSortedList()
    {
        var problems = new List<Problem>(problemsByTitle.Values);
        problems.Sort(CompareMostSevereFirst);
        return problems;
    }

    private static int CompareMostSevereFirst(Problem left, Problem right)
    {
        var bySeverity = right.Severity.CompareTo(left.Severity);
        return bySeverity != 0 ? bySeverity : left.FirstLineNumber.CompareTo(right.FirstLineNumber);
    }
}
