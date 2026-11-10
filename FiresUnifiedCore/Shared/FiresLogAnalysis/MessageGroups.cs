using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FiresLogAnalysis;

/// <summary>Repeats of one error or warning. Messages that differ only in numbers count as the same message.</summary>
public sealed class MessageGroup
{
    internal readonly List<int> lineNumbers = new List<int>();
    internal readonly List<int> sameTraceLines = new List<int>();
    internal readonly List<KeyValuePair<string, int>> variants = new List<KeyValuePair<string, int>>();
    internal string TraceKey;

    /// <summary>Whether the first occurrence carries stack frames (otherwise its extra lines are just wrapped text).</summary>
    public bool HasStackTrace { get; internal set; }

    // The message with any wrapped (non-trace) lines joined on, for name families: a log that wraps the same warning at
    // different words still folds into one family.
    internal string FullText;

    internal MessageGroup(LogEntry firstEntry, IReadOnlyList<string> context, Excerpt excerpt)
    {
        IsError = firstEntry.IsError;
        Source = firstEntry.Source;
        Message = firstEntry.Message;
        Context = context;
        FirstLineNumber = firstEntry.LineNumber;
        Excerpt = excerpt;
    }

    // A copy for a family, so the plain group list keeps its own objects.
    internal MessageGroup(MessageGroup other)
    {
        IsError = other.IsError;
        Source = other.Source;
        Message = other.Message;
        Context = other.Context;
        FirstLineNumber = other.FirstLineNumber;
        Excerpt = other.Excerpt;
        Occurrences = other.Occurrences;
        TraceKey = other.TraceKey;
        HasStackTrace = other.HasStackTrace;
        FullText = other.FullText;
        lineNumbers.AddRange(other.lineNumbers);
        sameTraceLines.AddRange(other.sameTraceLines);
    }

    public bool IsError { get; }
    public string Source { get; }

    /// <summary>The first occurrence's message; for a family, its template with the varying name as "&lt;name&gt;".</summary>
    public string Message { get; internal set; }
    public IReadOnlyList<string> Context { get; }
    public int FirstLineNumber { get; }
    public int Occurrences { get; internal set; }

    /// <summary>Every line this message is on, in log order.</summary>
    public IReadOnlyList<int> LineNumbers => lineNumbers;

    /// <summary>The first occurrence as it stands in the log: the entries around it and its stack trace, numbered.</summary>
    public Excerpt Excerpt { get; }

    /// <summary>Later occurrences whose stack trace is the same as the excerpt's.</summary>
    public IReadOnlyList<int> SameTraceLines => sameTraceLines;

    /// <summary>For a family (messages that differ only in a name): each name and how often it came, most first. Empty otherwise.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> Variants => variants;
}

public sealed class HarmlessGroup
{
    internal HarmlessGroup(string reason, string exampleMessage)
    {
        Reason = reason;
        ExampleMessage = exampleMessage;
    }

    public string Reason { get; }
    public string ExampleMessage { get; }
    public int Occurrences { get; internal set; }
}

internal sealed class MessageGrouper
{
    private const int MaxContextLines = 4;
    private const string NumberPlaceholder = "#";

    private static readonly Regex Numbers = new Regex(@"\d+", RegexOptions.Compiled);

    private readonly Dictionary<string, MessageGroup> groupsByKey = new Dictionary<string, MessageGroup>(StringComparer.Ordinal);
    private readonly List<MessageGroup> groupsInOrder = new List<MessageGroup>();

    public void Add(LogEntry entry, ExcerptRecorder excerpts)
    {
        var context = ReadContext(entry);
        var key = (entry.IsError ? "E|" : "W|") + entry.Source + "|" + Numbers.Replace(entry.Message, NumberPlaceholder)
            + "|" + (context.Count > 0 ? context[0] : string.Empty);
        var traceKey = entry.ContinuationLines.Count > 0 ? string.Join("\n", entry.ContinuationLines) : null;

        if (!groupsByKey.TryGetValue(key, out var group))
        {
            var hasTrace = entry.ContinuationLines.Any(StackTraceLines.IsTraceLine);
            group = new MessageGroup(entry, context, excerpts.Capture(entry))
            {
                TraceKey = traceKey,
                HasStackTrace = hasTrace,
                FullText = hasTrace || entry.ContinuationLines.Count == 0 ? null
                    : entry.Message + " " + string.Join(" ", entry.ContinuationLines.Select(line => line.Trim())),
            };
            groupsByKey.Add(key, group);
            groupsInOrder.Add(group);
        }
        else if (traceKey != null && traceKey == group.TraceKey)
            group.sameTraceLines.Add(entry.LineNumber);
        group.lineNumbers.Add(entry.LineNumber);
        group.Occurrences++;
    }

    public List<MessageGroup> ToSortedList()
    {
        var groups = new List<MessageGroup>(groupsInOrder);
        groups.Sort(CompareErrorsFirstThenMostFrequent);
        return groups;
    }

    /// <summary>The same groups with name families folded (copies; ToSortedList's groups are left as they are).</summary>
    public List<MessageGroup> ToFamilies()
    {
        var groups = FoldFamilies(groupsInOrder);
        groups.Sort(CompareErrorsFirstThenMostFrequent);
        return groups;
    }

    // Messages like "Failed to resolve prefab: <name>" would otherwise make one group per name. Messages that
    // differ only in name-like words (holding a digit, '_' or '$') fold into one family: the first group's excerpt, every
    // line number, and the names with their counts.
    private static readonly Regex NameToken = new Regex(@"[^\s]*[\d_$][^\s]*", RegexOptions.Compiled);
    private static readonly char[] NameTrim = { '(', ')', '[', ']', ',', '.', '!', ':', ';', '"', '\'' };
    private const string PrefabSuffix = ".prefab";

    private static List<MessageGroup> FoldFamilies(List<MessageGroup> groups)
    {
        var byFamily = new Dictionary<string, List<MessageGroup>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var group in groups)
        {
            var key = (group.IsError ? "E|" : "W|") + group.Source + "|" + NameToken.Replace(group.FullText ?? group.Message, "\u0001");
            if (!byFamily.TryGetValue(key, out var members))
            {
                byFamily.Add(key, members = new List<MessageGroup>());
                order.Add(key);
            }
            members.Add(group);
        }

        var result = new List<MessageGroup>();
        foreach (var key in order)
        {
            var members = byFamily[key];
            if (members.Count == 1)
            {
                result.Add(members[0]);
                continue;
            }
            result.Add(Fold(members));
        }
        return result;
    }

    private static MessageGroup Fold(List<MessageGroup> members)
    {
        var first = members[0];
        var family = new MessageGroup(first);
        var tokens = (first.FullText ?? first.Message).Split(' ');
        var varying = new bool[tokens.Length];
        foreach (var member in members)
        {
            var other = (member.FullText ?? member.Message).Split(' ');
            for (var i = 0; i < tokens.Length && i < other.Length; i++)
                varying[i] |= other[i] != tokens[i];
        }
        var template = new string[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
            template[i] = varying[i] ? Placeholder(tokens[i]) : tokens[i];

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var member in members)
        {
            var name = VariantName((member.FullText ?? member.Message).Split(' '), varying);
            if (!counts.ContainsKey(name))
            {
                counts.Add(name, 0);
                names.Add(name);
            }
            counts[name] += member.Occurrences;
            if (member == first)
                continue;
            family.Occurrences += member.Occurrences;
            family.lineNumbers.AddRange(member.lineNumbers);
        }
        family.lineNumbers.Sort();
        family.Message = string.Join(" ", template);
        names.Sort((a, b) => counts[b] != counts[a] ? counts[b].CompareTo(counts[a]) : string.CompareOrdinal(a, b));
        foreach (var name in names)
            family.variants.Add(new KeyValuePair<string, int>(name, counts[name]));
        return family;
    }

    private static string VariantName(string[] tokens, bool[] varying)
    {
        for (var i = 0; i < tokens.Length && i < varying.Length; i++)
        {
            if (!varying[i] || !ContainsLetter(tokens[i]))
                continue;
            var name = tokens[i].Trim(NameTrim);
            if (name.EndsWith(PrefabSuffix, StringComparison.Ordinal))
                name = name.Substring(0, name.Length - PrefabSuffix.Length);
            return name;
        }
        // No word with a letter varies (a zone "(1,-2):", a count): name the variant by its first varying value.
        for (var i = 0; i < tokens.Length && i < varying.Length; i++)
            if (varying[i])
                return tokens[i].TrimEnd(':', ',', ';', '.', '!');
        return "?";
    }

    private static readonly Regex Hex = new Regex(@"^[0-9a-fA-F]{12,}$", RegexOptions.Compiled);

    // The varying word with its punctuation kept: "WoodVillage2.prefab" -> "<name>.prefab", "(808fa...)!" -> "(<hash>)!".
    private static string Placeholder(string token)
    {
        var start = 0;
        var end = token.Length;
        while (start < end && Array.IndexOf(NameTrim, token[start]) >= 0)
            start++;
        while (end > start && Array.IndexOf(NameTrim, token[end - 1]) >= 0)
            end--;
        var core = token.Substring(start, end - start);
        var suffix = string.Empty;
        if (core.EndsWith(PrefabSuffix, StringComparison.Ordinal))
        {
            suffix = PrefabSuffix;
            core = core.Substring(0, core.Length - PrefabSuffix.Length);
        }
        var placeholder = Hex.IsMatch(core) ? "<hash>" : ContainsLetter(core) ? "<name>" : "#";
        return token.Substring(0, start) + placeholder + suffix + token.Substring(end);
    }

    private static bool ContainsLetter(string token)
    {
        foreach (var c in token)
            if (char.IsLetter(c))
                return true;
        return false;
    }

    private static IReadOnlyList<string> ReadContext(LogEntry entry)
    {
        var frames = StackTraceLines.ReadModFrames(entry.ContinuationLines, MaxContextLines);
        if (frames.Count > 0)
            return frames;

        var patched = StackTraceLines.ReadPatchedMethod(entry.ContinuationLines);
        var roomForText = patched == null ? MaxContextLines : MaxContextLines - 1;
        var lines = new List<string>();
        foreach (var line in entry.ContinuationLines)
        {
            if (lines.Count == roomForText)
                break;
            if (!StackTraceLines.IsTraceLine(line))
                lines.Add(line.Trim());
        }
        if (patched != null)
            lines.Add("Patched method: " + patched + " (the mod that patched it is not in the stack)");
        return lines;
    }

    private static int CompareErrorsFirstThenMostFrequent(MessageGroup left, MessageGroup right)
    {
        if (left.IsError != right.IsError)
            return left.IsError ? -1 : 1;
        var byOccurrences = right.Occurrences.CompareTo(left.Occurrences);
        return byOccurrences != 0 ? byOccurrences : left.FirstLineNumber.CompareTo(right.FirstLineNumber);
    }
}

internal sealed class HarmlessMessageCounter
{
    private readonly Dictionary<string, HarmlessGroup> groupsByReason = new Dictionary<string, HarmlessGroup>(StringComparer.Ordinal);

    public bool TryCount(LogEntry entry, LogEnvironment environment)
    {
        foreach (var harmless in KnownHarmlessMessages.All)
        {
            if (!harmless.Matches(entry, environment))
                continue;

            if (!groupsByReason.TryGetValue(harmless.Reason, out var group))
            {
                group = new HarmlessGroup(harmless.Reason, entry.Message);
                groupsByReason.Add(harmless.Reason, group);
            }
            group.Occurrences++;
            return true;
        }
        return false;
    }

    public List<HarmlessGroup> ToSortedList()
    {
        var groups = new List<HarmlessGroup>(groupsByReason.Values);
        groups.Sort((left, right) => right.Occurrences.CompareTo(left.Occurrences));
        return groups;
    }
}
