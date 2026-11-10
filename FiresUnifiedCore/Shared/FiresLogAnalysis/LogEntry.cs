using System;
using System.Collections.Generic;

namespace FiresLogAnalysis;

public sealed class LogEntry
{
    private readonly List<string> continuationLines = new List<string>();
    private readonly List<int> continuationLineNumbers = new List<int>();

    public LogEntry(int lineNumber, LogEntryLevel level, string source, DateTime? timestamp, string message)
    {
        LineNumber = lineNumber;
        Level = level;
        Source = source;
        Timestamp = timestamp;
        Message = message;
    }

    public int LineNumber { get; }
    public LogEntryLevel Level { get; }
    public string Source { get; }
    public DateTime? Timestamp { get; }
    public string Message { get; }
    public IReadOnlyList<string> ContinuationLines => continuationLines;

    /// <summary>The log line each continuation line came from (blank lines are skipped, so they need not be consecutive).</summary>
    public IReadOnlyList<int> ContinuationLineNumbers => continuationLineNumbers;

    /// <summary>The entry's first line exactly as the log has it, for numbered excerpts.</summary>
    public string RawLine { get; internal set; } = string.Empty;

    public bool IsError => Level >= LogEntryLevel.Error;
    public bool IsWarningOrError => Level >= LogEntryLevel.Warning;
    public bool HasBepInExHeader => Source.Length > 0;

    internal void AddContinuationLine(string line, int lineNumber)
    {
        continuationLines.Add(line);
        continuationLineNumbers.Add(lineNumber);
    }
}
