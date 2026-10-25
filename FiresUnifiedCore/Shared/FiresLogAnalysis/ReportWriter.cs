using System.Collections.Generic;
using System.Text;
using FiresLogAnalysis.Problems;

namespace FiresLogAnalysis;

/// <summary>The full plain-text report attached to the Discord reply. Everything in it is redacted.</summary>
public static class ReportWriter
{
    private const string Indent = "    ";
    private const string LabelColumnFormat = "{0,-10}{1}";

    // detailed (the Discord bot only): every line number, a numbered excerpt per group, name families and the
    // TIMELINE. Off, the report is exactly what it was before, which is what Core's in-game reports use.
    public static string Write(LogAnalysis analysis, string logName, bool detailed = false)
    {
        var report = new StringBuilder();
        WriteHeading(report, "Valheim log report: " + logName, '=');
        WriteEnvironment(report, analysis);
        WriteProblems(report, analysis.Problems);
        WriteRunPerformance(report, analysis.Performance);
        if (detailed)
        {
            WriteMessageGroups(report, analysis);
            WriteTimeline(report, analysis.Timeline);
        }
        else
            WritePlainMessageGroups(report, analysis);
        WriteHarmlessGroups(report, analysis.HarmlessGroups);
        WritePlugins(report, analysis.Plugins);
        report.AppendLine("Steam IDs, IP addresses and user folder names in this report are masked.");
        return LogRedactor.Redact(report.ToString());
    }

    private static void WriteEnvironment(StringBuilder report, LogAnalysis analysis)
    {
        var environment = analysis.Environment;
        AppendLabeled(report, "Log", AnalysisText.LogKind(environment));
        AppendLabeled(report, "Valheim", AnalysisText.GameVersion(environment));
        AppendLabeled(report, "BepInEx", AnalysisText.BepInExVersion(environment));
        if (environment.UnityVersion != null)
            AppendLabeled(report, "Unity", environment.UnityVersion);
        AppendLabeled(report, "Session", AnalysisText.Session(environment));
        AppendLabeled(report, "Loaded", AnalysisText.ModCount(analysis.Plugins));
        foreach (var fact in AnalysisText.ServerFacts(environment))
            AppendLabeled(report, "Server", fact);
        if (!environment.IsRecognizedValheimLog)
            AppendLabeled(report, "Note", AnalysisText.PartialLogNote);
        if (analysis.EndsBeforeModsFinishedLoading)
            AppendLabeled(report, "Note", "The log ends before BepInEx finished loading mods (cut off, or the game crashed during startup).");
        report.AppendLine();
    }

    private static void WriteProblems(StringBuilder report, IReadOnlyList<Problem> problems)
    {
        WriteHeading(report, "PROBLEMS (" + problems.Count + ")", '-');
        if (problems.Count == 0)
            report.AppendLine("No known problems found.");

        foreach (var problem in problems)
        {
            report.Append('[').Append(AnalysisText.SeverityLabel(problem.Severity)).Append("] ")
                .Append(problem.Title).Append(AnalysisText.Times(problem.Occurrences))
                .Append("   (first at line ").Append(problem.FirstLineNumber).AppendLine(")");
            foreach (var detail in problem.Details)
                report.Append(Indent).Append(detail.Text).AppendLine(AnalysisText.Times(detail.Occurrences));
            if (problem.DistinctDetailsNotListed > 0)
                report.Append(Indent).Append("...and ").Append(problem.DistinctDetailsNotListed).AppendLine(" more details not listed");
            if (problem.Advice != null)
                report.Append(Indent).Append("What to do: ").AppendLine(problem.Advice);
            report.AppendLine();
        }
        report.AppendLine();
    }

    /// <summary>
    /// How the run performed. Omitted entirely when the log has no FiresDebugginTools diagnostics,
    /// rather than printed as zeroes - "not measured" and "fast" must not look the same.
    /// </summary>
    private static void WriteRunPerformance(StringBuilder report, RunPerformance run)
    {
        if (run == null)
            return;

        WriteHeading(report, "RUN BREAKDOWN", '-');

        if (run.HasUsableFrameData)
        {
            report.Append("Frame rate   ").Append(run.AverageFps.ToString("0.0")).Append(" fps average over ")
                  .Append(run.SampleCount.ToString("N0")).Append(" samples, ranging ")
                  .Append(run.SlowestSampleFps.ToString("0.0")).Append(" to ").Append(run.FastestSampleFps.ToString("0.0"))
                  .AppendLine();
            report.Append("Worst frame  ").Append(run.WorstFrameMs.ToString("N0")).AppendLine(" ms");
            if (run.FramesMeasured > 0)
            {
                var share = 100d * run.FramesOver33Ms / run.FramesMeasured;
                report.Append("Stutter      ").Append(run.FramesOver33Ms.ToString("N0")).Append(" of ")
                      .Append(run.FramesMeasured.ToString("N0")).Append(" frames over 33 ms (")
                      .Append(share.ToString("0.0")).AppendLine("%)");
            }
        }
        else if (run.SampleCount > 0)
        {
            report.Append("Frame rate   only ").Append(run.SampleCount)
                  .AppendLine(" sample(s) - too few to describe the run, so no average is given");
        }

        if (run.PeakZdos > 0 || run.PeakDraws > 0)
        {
            report.Append("Scene peak   ");
            if (run.PeakZdos > 0)
                report.Append(run.PeakZdos.ToString("N0")).Append(" ZDOs, ")
                      .Append(run.PeakInstances.ToString("N0")).Append(" instanced, ")
                      .Append(run.PeakZones.ToString("N0")).Append(" zones");
            if (run.PeakDraws > 0)
                report.Append(run.PeakZdos > 0 ? ", " : string.Empty).Append(run.PeakDraws.ToString("N0")).Append(" draw calls");
            report.AppendLine();
        }

        if (run.WorstStalls.Count > 0)
        {
            report.Append("Stalls       ").Append(run.StallCount.ToString("N0")).AppendLine(" main-thread stalls, worst first:");
            foreach (var stall in run.WorstStalls)
                report.Append(Indent).Append(stall.Milliseconds.ToString("N0")).Append(" ms at T+")
                      .Append(stall.AtSeconds).Append("s during ").AppendLine(stall.Phase);
        }

        if (run.TopCosts.Count > 0)
        {
            report.AppendLine("Top costs    average ms per second of wall clock (nested brackets overlap - do not add them up):");
            foreach (var cost in run.TopCosts)
                report.Append(Indent).Append(cost.AverageMsPerSecond.ToString("0.0").PadLeft(7)).Append("  ")
                      .Append(cost.Label)
                      .Append("   (worst ").Append(cost.WorstMsPerSecond.ToString("0.0"))
                      .Append(", ").Append(cost.Samples).AppendLine(" samples)");
        }

        report.AppendLine();
    }

    private static void WriteMessageGroups(StringBuilder report, LogAnalysis analysis)
    {
        WriteHeading(report, "OTHER ERRORS AND WARNINGS (" + AnalysisText.Count(analysis.ErrorCount, "error") + ", " + AnalysisText.Count(analysis.WarningCount, "warning") + ")", '-');
        if (analysis.MessageGroups.Count == 0)
            report.AppendLine("None.");

        var warningExcerpts = 0;
        foreach (var group in analysis.MessageFamilies)
        {
            report.Append(group.IsError ? "[ERROR] " : "[WARN]  ");
            if (group.Source.Length > 0)
                report.Append('[').Append(group.Source).Append("] ");
            report.Append(group.Message).Append("   (");
            if (group.Variants.Count > 0)
                report.Append(group.Variants.Count).Append(" variants, ");
            report.Append(group.Occurrences).Append(group.Occurrences == 1 ? "x: line " : "x: lines ")
                .Append(LineList(group.LineNumbers)).AppendLine(")");
            if (group.Variants.Count > 0)
                report.Append(Indent).AppendLine(VariantList(group.Variants));

            // The first occurrence as it stands in the log. Every error gets one;
            // warnings, the first MaxWarningExcerpts groups (most frequent first).
            if (group.Excerpt != null && (group.IsError || warningExcerpts++ < MaxWarningExcerpts))
                WriteExcerpt(report, group);
            else
                foreach (var line in group.Context)
                    report.Append(Indent).AppendLine(line);
            if (group.SameTraceLines.Count > 0)
                report.Append(Indent).Append(group.HasStackTrace ? "Same stack trace at " : "Same text at ").Append(LineList(group.SameTraceLines)).AppendLine(".");
            report.AppendLine();
        }
        report.AppendLine();
    }

    private const int MaxWarningExcerpts = 15;
    private const int MaxListedLines = 12;
    private const int MaxListedVariants = 20;

    private static void WriteExcerpt(StringBuilder report, MessageGroup group)
    {
        var width = 0;
        foreach (var line in group.Excerpt.Lines)
            width = System.Math.Max(width, line.Number.ToString().Length);
        foreach (var line in group.Excerpt.Lines)
            report.Append(line.IsHit ? "  > " : "    ").Append(line.Number.ToString().PadLeft(width)).Append(" | ").AppendLine(line.Text);
        if (group.Excerpt.OmittedTraceLines > 0)
            report.Append(Indent).Append("... ").Append(group.Excerpt.OmittedTraceLines).AppendLine(" more stack trace lines");
    }

    private static string LineList(IReadOnlyList<int> lines)
    {
        var text = new StringBuilder();
        for (var i = 0; i < lines.Count && i < MaxListedLines; i++)
            text.Append(i == 0 ? string.Empty : ", ").Append(lines[i]);
        if (lines.Count > MaxListedLines)
            text.Append(" +").Append(lines.Count - MaxListedLines).Append(" more");
        return text.ToString();
    }

    private static string VariantList(IReadOnlyList<KeyValuePair<string, int>> variants)
    {
        var text = new StringBuilder("Variants: ");
        for (var i = 0; i < variants.Count && i < MaxListedVariants; i++)
        {
            text.Append(i == 0 ? string.Empty : ", ").Append(variants[i].Key);
            if (variants[i].Value > 1)
                text.Append(" x").Append(variants[i].Value);
        }
        if (variants.Count > MaxListedVariants)
            text.Append(", +").Append(variants.Count - MaxListedVariants).Append(" more");
        return text.ToString();
    }

    // The report as it was before the detailed one: one line per group with its first line number.
    private static void WritePlainMessageGroups(StringBuilder report, LogAnalysis analysis)
    {
        WriteHeading(report, "OTHER ERRORS AND WARNINGS (" + AnalysisText.Count(analysis.ErrorCount, "error") + ", " + AnalysisText.Count(analysis.WarningCount, "warning") + ")", '-');
        if (analysis.MessageGroups.Count == 0)
            report.AppendLine("None.");

        foreach (var group in analysis.MessageGroups)
        {
            report.Append(group.IsError ? "[ERROR] " : "[WARN]  ");
            if (group.Source.Length > 0)
                report.Append('[').Append(group.Source).Append("] ");
            report.Append(group.Message).Append(AnalysisText.Times(group.Occurrences))
                .Append("   (line ").Append(group.FirstLineNumber).AppendLine(")");
            foreach (var line in group.Context)
                report.Append(Indent).AppendLine(line);
        }
        report.AppendLine();
    }

    private static void WriteTimeline(StringBuilder report, Timeline timeline)
    {
        if (timeline == null || timeline.Lines.Count == 0)
            return;
        WriteHeading(report, "TIMELINE (the errors and warnings in log order, with " + ExcerptLines.ContextBefore + " lines before and "
            + ExcerptLines.ContextAfter + " after)", '-');
        foreach (var line in timeline.Lines)
            report.AppendLine(line);
        if (timeline.HitsPastCap > 0)
            report.Append("... ").Append(timeline.HitsPastCap).Append(" more errors/warnings past ").Append(Timeline.MaxLines)
                .Append(" lines: open the log at line ").Append(timeline.FirstLinePastCap).AppendLine(".");
        if (timeline.HarmlessLeftOut > 0)
            report.Append('(').Append(AnalysisText.Count(timeline.HarmlessLeftOut, "known-harmless line"))
                .AppendLine(" left out: see KNOWN HARMLESS.)");
        report.AppendLine();
    }

    private static void WriteHarmlessGroups(StringBuilder report, IReadOnlyList<HarmlessGroup> harmlessGroups)
    {
        if (harmlessGroups.Count == 0)
            return;

        WriteHeading(report, "KNOWN HARMLESS (hidden from the list above)", '-');
        foreach (var group in harmlessGroups)
        {
            report.Append(AnalysisText.Count(group.Occurrences, "line")).Append(": ").AppendLine(group.Reason);
            report.Append(Indent).Append("e.g. ").AppendLine(group.ExampleMessage);
        }
        report.AppendLine();
    }

    private static void WritePlugins(StringBuilder report, PluginList plugins)
    {
        WriteHeading(report, "MODS (" + plugins.Plugins.Count + ")", '-');
        foreach (var plugin in plugins.Plugins)
            report.Append(Indent).Append(plugin.ToString()).AppendLine(plugin.FailedToLoad ? "   (failed to load)" : string.Empty);
        if (plugins.Patchers.Count > 0)
        {
            report.AppendLine("Preloader patchers:");
            foreach (var patcher in plugins.Patchers)
                report.Append(Indent).AppendLine(patcher.ToString());
        }
        report.AppendLine();
    }

    private static void WriteHeading(StringBuilder report, string heading, char underline)
    {
        report.AppendLine(heading);
        report.AppendLine(new string(underline, heading.Length));
    }

    private static void AppendLabeled(StringBuilder report, string label, string value) =>
        report.AppendFormat(LabelColumnFormat, label, value).AppendLine();
}
