using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FiresLogAnalysis;

/// <summary>One line of a numbered excerpt: the log line number, the text, and whether it is the error/warning itself.</summary>
public readonly struct ExcerptLine
{
    public ExcerptLine(int number, string text, bool isHit)
    {
        Number = number;
        Text = text;
        IsHit = isHit;
    }

    public int Number { get; }
    public string Text { get; }
    public bool IsHit { get; }
}

/// <summary>
/// An error or warning as it stands in the log: the entries before it, the entry
/// with its stack trace, and the entries after it, each line with its own log line number.
/// </summary>
public sealed class Excerpt
{
    internal readonly List<ExcerptLine> lines = new List<ExcerptLine>();
    internal int AfterLeft;

    public IReadOnlyList<ExcerptLine> Lines => lines;

    /// <summary>Trace lines left out past ExcerptLines.MaxTraceLines.</summary>
    public int OmittedTraceLines { get; internal set; }
}

/// <summary>Shared rules for excerpt text.</summary>
public static class ExcerptLines
{
    public const int ContextBefore = 2;
    public const int ContextAfter = 2;
    public const int MaxTraceLines = 40;

    // " (at <030acc1fa99247f68cf8ec1aed3604d6>:0)" and " [0x00000] in <hash>:0": no information for a player.
    private static readonly Regex FrameSuffix = new Regex(@"\s*(?:\(at <[^>]*>:\d+\)|\[0x[0-9a-fA-F]+\] in <[^>]*>:\d+)\s*$", RegexOptions.Compiled);

    public static string TrimFrame(string line) => FrameSuffix.Replace(line, string.Empty);

    /// <summary>The entry's first line, then its continuation lines (trimmed, at most MaxTraceLines).</summary>
    internal static void AppendEntry(List<ExcerptLine> into, LogEntry entry, bool isHit, bool withContinuation, out int omitted)
    {
        omitted = 0;
        into.Add(new ExcerptLine(entry.LineNumber, entry.RawLine, isHit));
        if (!withContinuation)
            return;
        var count = entry.ContinuationLines.Count;
        for (var i = 0; i < count; i++)
        {
            if (i == MaxTraceLines)
            {
                omitted = count - i;
                break;
            }
            into.Add(new ExcerptLine(entry.ContinuationLineNumbers[i], "  " + TrimFrame(entry.ContinuationLines[i].Trim()), false));
        }
    }
}

/// <summary>
/// Sees every entry in order, keeps the last ContextBefore of them, and fills each captured excerpt's after-context from
/// the entries that follow. Call Begin before handling an entry and End after it.
/// </summary>
internal sealed class ExcerptRecorder
{
    private readonly Queue<LogEntry> recent = new Queue<LogEntry>();
    private readonly List<Excerpt> waiting = new List<Excerpt>();

    public void Begin(LogEntry entry)
    {
        for (var i = waiting.Count - 1; i >= 0; i--)
        {
            var excerpt = waiting[i];
            ExcerptLines.AppendEntry(excerpt.lines, entry, isHit: false, withContinuation: false, out _);
            if (--excerpt.AfterLeft <= 0)
                waiting.RemoveAt(i);
        }
    }

    public Excerpt Capture(LogEntry entry)
    {
        var excerpt = new Excerpt { AfterLeft = ExcerptLines.ContextAfter };
        foreach (var before in recent)
            ExcerptLines.AppendEntry(excerpt.lines, before, isHit: false, withContinuation: false, out _);
        ExcerptLines.AppendEntry(excerpt.lines, entry, isHit: true, withContinuation: true, out var omitted);
        excerpt.OmittedTraceLines = omitted;
        waiting.Add(excerpt);
        return excerpt;
    }

    public void End(LogEntry entry)
    {
        recent.Enqueue(entry);
        while (recent.Count > ExcerptLines.ContextBefore)
            recent.Dequeue();
    }
}
