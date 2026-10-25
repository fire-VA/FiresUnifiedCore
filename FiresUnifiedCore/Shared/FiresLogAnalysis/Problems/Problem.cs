using System.Collections.Generic;

namespace FiresLogAnalysis.Problems;

public enum ProblemSeverity
{
    Notice,
    Warning,
    Critical,
}

public sealed class ProblemDetail
{
    internal ProblemDetail(string text)
    {
        Text = text;
        Occurrences = 1;
    }

    public string Text { get; }
    public int Occurrences { get; internal set; }
}

public sealed class Problem
{
    private const int MaxDistinctDetails = 50;

    private readonly List<ProblemDetail> details = new List<ProblemDetail>();

    internal Problem(ProblemSeverity severity, string title, string advice, int firstLineNumber)
    {
        Severity = severity;
        Title = title;
        Advice = advice;
        FirstLineNumber = firstLineNumber;
    }

    public ProblemSeverity Severity { get; }
    public string Title { get; }
    public string Advice { get; }
    public int FirstLineNumber { get; }
    public int Occurrences { get; private set; }
    public IReadOnlyList<ProblemDetail> Details => details;
    public int DistinctDetailsNotListed { get; private set; }

    internal void CountOccurrence() => Occurrences++;

    internal void AddDetail(string text)
    {
        foreach (var detail in details)
        {
            if (detail.Text == text)
            {
                detail.Occurrences++;
                return;
            }
        }

        if (details.Count == MaxDistinctDetails)
        {
            DistinctDetailsNotListed++;
            return;
        }
        details.Add(new ProblemDetail(text));
    }
}
