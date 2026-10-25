namespace FiresLogAnalysis.Problems;

/// <summary>
/// Valheim 1.0's chunked save never rewrites a save chunk whose objects were all deleted (ZDOMan.AddObjectsPerChunk
/// skips chunks counting 0), so a full zone reset reloads the deleted objects after a restart and the ungenerated
/// zone generates again on top of them.
/// </summary>
internal sealed class ZoneResetProblems : IProblemDetector
{
    private const string CronJobSource = "Cron Job";
    private const int FirstChunkedSaveMajorVersion = 1;

    private const string StackingAdvice = "On Valheim 1.0, areas emptied by a reset keep their old save files. The deleted objects come back after a restart and the zone generates again on top of them, stacking duplicate objects, spawners and raids. Avoid full zone resets until this is fixed.";

    private static readonly MessageRule[] Rules =
    {
        new MessageRule(
            "Executing: zones_reset",
            @"^Executing: (?<command>zones_reset\b.*)$",
            (match, entry, context) =>
            {
                if (context.Environment.GameMajorVersion >= FirstChunkedSaveMajorVersion)
                {
                    context.Problems
                        .Report(ProblemSeverity.Warning, "Zones were reset on Valheim 1.0, which can stack duplicates", StackingAdvice, entry)
                        .AddDetail("Cron Job ran: " + match.Groups["command"].Value);
                }
            }),
    };

    public bool TryExplain(LogEntry entry, AnalysisContext context) =>
        entry.Source == CronJobSource && MessageRule.TryExplainWithAny(Rules, entry, context);
}
