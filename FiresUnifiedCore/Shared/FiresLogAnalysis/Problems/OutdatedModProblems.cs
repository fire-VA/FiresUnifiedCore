using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FiresLogAnalysis.Problems;

/// <summary>Exceptions that mean a mod no longer fits the game or is missing a library. Checked in priority order per entry.</summary>
internal sealed class OutdatedModProblems : IProblemDetector
{
    private const string InnerExceptionMarker = " ---> ";

    private const string PatchFailedAdvice = "This mod's patch no longer fits the game. Update the mod, or remove it if there is no update.";
    private const string MissingLibraryAdvice = "Install the mod or library that provides it, or remove the mod that needs it.";
    private const string GameCodeChangedAdvice = "A mod uses game code that no longer exists in this version. Update the mod shown here.";
    private const string UnnamedModAdvice = "A mod uses game code that no longer exists in this version. The stack trace contains only game code, so it does not say which mod - update the mods that touch this area, or remove them one at a time to find it.";
    private const string PatchedSiteAdvice = "A mod has patched {0} and that patch calls game code this version removed. The stack trace contains only game code, so it does not name the mod - look for a mod that patches {0}.";
    private const string InvalidDllAdvice = "A DLL is damaged or isn't a .NET mod. Reinstall the mod it belongs to.";

    private static readonly Regex HarmonyPatchFailure = new Regex(
        @"HarmonyException: Patching exception in method (?<method>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex MissingAssembly = new Regex(
        @"Could not load file or assembly '(?<assembly>[^,']+)",
        RegexOptions.Compiled);

    private static readonly Regex MissingGameMember = new Regex(
        @"(?:^|[\s:.])(?<type>MissingMethodException|MissingFieldException|MissingMemberException|TypeLoadException|EntryPointNotFoundException): (?<message>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex InvalidDll = new Regex(
        @"BadImageFormatException: (?<message>.+)$",
        RegexOptions.Compiled);

    public bool TryExplain(LogEntry entry, AnalysisContext context)
    {
        if (!entry.IsError)
            return false;

        var lines = LinesOf(entry);
        return TryReportHarmonyPatchFailure(lines, entry, context)
            || TryReportMissingAssembly(lines, entry, context)
            || TryReportMissingGameMember(lines, entry, context)
            || TryReportInvalidDll(lines, entry, context);
    }

    private static bool TryReportHarmonyPatchFailure(List<string> lines, LogEntry entry, AnalysisContext context)
    {
        var match = FirstMatch(HarmonyPatchFailure, lines);
        if (match == null)
            return false;

        var patchedMethod = match.Groups["method"].Value;
        var innerExceptionStart = patchedMethod.IndexOf(InnerExceptionMarker, StringComparison.Ordinal);
        if (innerExceptionStart >= 0)
            patchedMethod = patchedMethod.Substring(0, innerExceptionStart);

        context.Problems
            .Report(ProblemSeverity.Critical, "A mod failed to patch the game", PatchFailedAdvice, entry)
            .AddDetail("Patch: " + patchedMethod.Trim());
        return true;
    }

    private static bool TryReportMissingAssembly(List<string> lines, LogEntry entry, AnalysisContext context)
    {
        var match = FirstMatch(MissingAssembly, lines);
        if (match == null)
            return false;

        context.Problems
            .Report(ProblemSeverity.Critical, "Missing library: " + match.Groups["assembly"].Value, MissingLibraryAdvice, entry)
            .AddDetail(WithFirstModFrame(entry.Message.Trim(), entry));
        return true;
    }

    private static bool TryReportMissingGameMember(List<string> lines, LogEntry entry, AnalysisContext context)
    {
        var match = FirstMatch(MissingGameMember, lines);
        if (match == null)
            return false;

        var detail = match.Groups["type"].Value + ": " + match.Groups["message"].Value.Trim();
        var modFrame = StackTraceLines.ReadOwningModFrame(entry.ContinuationLines);

        // When every frame is vanilla, no mod CAN be named - the trace simply does not contain one.
        // Saying "Update the mod shown here" while showing a vanilla type is worse than saying so:
        // it sends the reader looking for a mod that was never printed. The patch site is named
        // instead, because that is the one real lead a Harmony-patched vanilla stack carries.
        if (modFrame == null)
        {
            var patched = StackTraceLines.ReadPatchedMethod(entry.ContinuationLines);
            var problem = context.Problems.Report(
                ProblemSeverity.Critical,
                "A mod uses game code this version removed",
                patched == null ? UnnamedModAdvice : string.Format(PatchedSiteAdvice, patched),
                entry);
            problem.AddDetail(detail);
            if (patched != null)
                problem.AddDetail("Patched method: " + patched + " (the mod that patched it is not in the stack)");
            return true;
        }

        context.Problems
            .Report(ProblemSeverity.Critical, "Mod doesn't match this game version", GameCodeChangedAdvice, entry)
            .AddDetail(detail + " (in " + modFrame + ")");
        return true;
    }

    private static bool TryReportInvalidDll(List<string> lines, LogEntry entry, AnalysisContext context)
    {
        var match = FirstMatch(InvalidDll, lines);
        if (match == null)
            return false;

        context.Problems
            .Report(ProblemSeverity.Critical, "Invalid DLL", InvalidDllAdvice, entry)
            .AddDetail(match.Groups["message"].Value.Trim());
        return true;
    }

    private static List<string> LinesOf(LogEntry entry)
    {
        var lines = new List<string>(entry.ContinuationLines.Count + 1) { entry.Message };
        lines.AddRange(entry.ContinuationLines);
        return lines;
    }

    private static Match FirstMatch(Regex pattern, List<string> lines)
    {
        foreach (var line in lines)
        {
            var match = pattern.Match(line);
            if (match.Success)
                return match;
        }
        return null;
    }

    private static string WithFirstModFrame(string detail, LogEntry entry)
    {
        var owningFrame = StackTraceLines.ReadOwningModFrame(entry.ContinuationLines);
        return owningFrame == null ? detail : detail + " (in " + owningFrame + ")";
    }
}
