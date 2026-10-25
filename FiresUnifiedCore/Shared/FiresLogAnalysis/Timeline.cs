using System;
using System.Collections.Generic;
using System.Text;

namespace FiresLogAnalysis;

/// <summary>
/// The TIMELINE section: every real error and warning in log order, each with its
/// original line number and the ContextBefore / ContextAfter entries around it. A run of the same message on adjacent
/// lines collapses to "----- Nx -----"; a stack trace already shown points back to its first appearance; known-harmless
/// lines are left out. Capped at MaxLines.
/// </summary>
public sealed class Timeline
{
    public const int MaxLines = 600;

    internal readonly List<string> lines = new List<string>();

    public IReadOnlyList<string> Lines => lines;

    /// <summary>Errors and warnings the cap left out, and the line the first of them is on (0 when none).</summary>
    public int HitsPastCap { get; internal set; }
    public int FirstLinePastCap { get; internal set; }
    public int HarmlessLeftOut { get; internal set; }
}

internal sealed class TimelineBuilder
{
    private readonly Timeline timeline = new Timeline();
    private readonly Queue<(LogEntry Entry, long Ordinal)> recent = new Queue<(LogEntry, long)>();
    private readonly Dictionary<string, int> traceFirstSeen = new Dictionary<string, int>(StringComparer.Ordinal);
    private long ordinal = -1;
    private long lastPrinted = -1;
    private int afterLeft;
    private LogEntry runFirst;
    private long runFirstOrdinal, runLastOrdinal;
    private int runCount;
    private bool full;

    public void CountHarmless() => timeline.HarmlessLeftOut++;

    public void Observe(LogEntry entry, bool isHit)
    {
        ordinal++;
        if (isHit)
            Hit(entry);
        else
        {
            FlushRun();
            if (afterLeft > 0)
            {
                Print(entry, ordinal, context: true);
                afterLeft--;
            }
        }
        recent.Enqueue((entry, ordinal));
        while (recent.Count > ExcerptLines.ContextBefore)
            recent.Dequeue();
    }

    public Timeline Finish()
    {
        FlushRun();
        return timeline;
    }

    private void Hit(LogEntry entry)
    {
        if (runFirst != null && runLastOrdinal == ordinal - 1 && runFirst.Level == entry.Level
            && runFirst.Source == entry.Source && runFirst.Message == entry.Message && SameContinuation(runFirst, entry))
        {
            runCount++;
            runLastOrdinal = ordinal;
            return;
        }
        FlushRun();
        foreach (var (before, beforeOrdinal) in recent)
            if (beforeOrdinal > lastPrinted)
                Print(before, beforeOrdinal, context: true);
        runFirst = entry;
        runFirstOrdinal = runLastOrdinal = ordinal;
        runCount = 1;
    }

    // A run is the same LINES: two errors with the same message but different stack traces are not a run.
    private static bool SameContinuation(LogEntry a, LogEntry b)
    {
        if (a.ContinuationLines.Count != b.ContinuationLines.Count)
            return false;
        for (var i = 0; i < a.ContinuationLines.Count; i++)
            if (a.ContinuationLines[i] != b.ContinuationLines[i])
                return false;
        return true;
    }

    private void FlushRun()
    {
        if (runFirst == null)
            return;
        if (runCount == 1)
            Print(runFirst, runFirstOrdinal, context: false);
        else
        {
            Add($"----- {runCount}x -----", gapFrom: runFirstOrdinal);
            lastPrinted = runFirstOrdinal - 1; // no gap between the header and its line
            Print(runFirst, runFirstOrdinal, context: false);
            Add("--------------", gapFrom: -1);
        }
        lastPrinted = runLastOrdinal;
        runFirst = null;
        afterLeft = ExcerptLines.ContextAfter;
    }

    private void Print(LogEntry entry, long entryOrdinal, bool context)
    {
        var text = entry.LineNumber + " | " + entry.RawLine;
        // A message wrapped over a few lines (no stack frames) is printed whole, each line numbered; a stack trace is a note.
        var wrapped = !context && entry.ContinuationLines.Count > 0 && !HasTrace(entry) && entry.ContinuationLines.Count <= MaxWrappedLines;
        if (!context && entry.ContinuationLines.Count > 0 && !wrapped)
            text += TraceNote(entry);
        if (full && !context)
        {
            if (timeline.HitsPastCap == 0)
                timeline.FirstLinePastCap = entry.LineNumber;
            timeline.HitsPastCap += runFirst == entry ? runCount : 1;
        }
        Add(text, gapFrom: entryOrdinal);
        if (wrapped)
            for (var i = 0; i < entry.ContinuationLines.Count; i++)
                Add(entry.ContinuationLineNumbers[i] + " |   " + entry.ContinuationLines[i].Trim(), gapFrom: -1);
        if (entryOrdinal > lastPrinted)
            lastPrinted = entryOrdinal;
    }

    private const int MaxWrappedLines = 6;

    private static bool HasTrace(LogEntry entry)
    {
        foreach (var line in entry.ContinuationLines)
            if (StackTraceLines.IsTraceLine(line))
                return true;
        return false;
    }

    // A stack trace is shown in full once, under ERRORS AND WARNINGS; here it is one note.
    private string TraceNote(LogEntry entry)
    {
        var key = entry.Message + "\n" + string.Join("\n", entry.ContinuationLines);
        var count = entry.ContinuationLines.Count;
        var what = HasTrace(entry) ? "stack trace" : "text";
        if (traceFirstSeen.TryGetValue(key, out var first))
            return $"   (same {count}-line {what} as line {first})";
        traceFirstSeen.Add(key, entry.LineNumber);
        return $"   (+{count}-line {what}, shown under ERRORS AND WARNINGS)";
    }

    private void Add(string text, long gapFrom)
    {
        if (full)
            return;
        if (gapFrom > lastPrinted + 1 && lastPrinted >= 0 && timeline.lines.Count > 0)
            timeline.lines.Add(string.Empty);
        timeline.lines.Add(text);
        if (timeline.lines.Count >= Timeline.MaxLines)
            full = true;
    }
}
