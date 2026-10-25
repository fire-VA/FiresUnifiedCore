using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace FiresLogAnalysis;

/// <summary>
/// Splits a BepInEx LogOutput.log into entries: one per "[Level:Source]" header, with every following
/// line attached until the next header. Logs without BepInEx headers become one entry per line.
/// </summary>
public static class LogEntryReader
{
    private const string TimestampFormat = "MM/dd/yyyy HH:mm:ss";

    private static readonly Regex BepInExHeader = new Regex(
        @"^\[(?<level>Fatal|Error|Warning|Message|Info|Debug)\s*:(?<source>[^\]]*)\] ?(?<message>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex LeadingTimestamp = new Regex(
        @"^\[?(?<timestamp>\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2})\]?:? ",
        RegexOptions.Compiled);

    private static readonly Regex ExceptionHeader = new Regex(
        @"^(?:[A-Za-z_][\w`]*\.)*[A-Za-z_]\w*Exception\b",
        RegexOptions.Compiled);

    public static IEnumerable<LogEntry> Read(TextReader reader)
    {
        LogEntry current = null;
        var currentHasBepInExHeader = false;
        var lineNumber = 0;
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var headerEntry = TryReadBepInExHeader(line, lineNumber);
            if (headerEntry != null)
            {
                if (current != null)
                    yield return current;
                current = headerEntry;
                currentHasBepInExHeader = true;
                continue;
            }

            if (current != null && ((currentHasBepInExHeader && !IsHeaderlessExceptionAfterInfoLine(current, line))
                || (current.IsError && StackTraceLines.IsTraceLine(line))))
            {
                current.AddContinuationLine(line, lineNumber);
                continue;
            }

            if (current != null)
                yield return current;
            current = ReadPlainLine(line, lineNumber);
            currentHasBepInExHeader = false;
        }

        if (current != null)
            yield return current;
    }

    /// <summary>
    /// A server's console log mixes BepInEx lines with Unity's own output, which has no header. Unity's exceptions would
    /// otherwise become continuation lines of the info line before them and never count as errors.
    /// </summary>
    private static bool IsHeaderlessExceptionAfterInfoLine(LogEntry current, string line) =>
        !current.IsWarningOrError && ExceptionHeader.IsMatch(line);

    private static LogEntry TryReadBepInExHeader(string line, int lineNumber)
    {
        if (line[0] != '[')
            return null;
        var match = BepInExHeader.Match(line);
        if (!match.Success)
            return null;

        var level = (LogEntryLevel)Enum.Parse(typeof(LogEntryLevel), match.Groups["level"].Value);
        var message = SplitLeadingTimestamp(match.Groups["message"].Value, out var timestamp);
        return new LogEntry(lineNumber, level, match.Groups["source"].Value.Trim(), timestamp, message) { RawLine = line };
    }

    private static LogEntry ReadPlainLine(string line, int lineNumber)
    {
        var message = SplitLeadingTimestamp(line, out var timestamp);
        var level = ExceptionHeader.IsMatch(message) ? LogEntryLevel.Error : LogEntryLevel.Info;
        return new LogEntry(lineNumber, level, string.Empty, timestamp, message) { RawLine = line };
    }

    private static string SplitLeadingTimestamp(string message, out DateTime? timestamp)
    {
        timestamp = null;
        var match = LeadingTimestamp.Match(message);
        if (!match.Success)
            return message;

        if (DateTime.TryParseExact(match.Groups["timestamp"].Value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            timestamp = parsed;
        return message.Substring(match.Length);
    }
}
