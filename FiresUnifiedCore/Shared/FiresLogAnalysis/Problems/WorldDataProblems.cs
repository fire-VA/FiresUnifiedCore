namespace FiresLogAnalysis.Problems;

/// <summary>World load and save messages from vanilla ZNet and ZDOMan (verified against the 1.0.12 assembly).</summary>
internal sealed class WorldDataProblems : IProblemDetector
{
    private const string UnknownPrefabsTitle = "World has objects from mods that aren't loaded";
    private const string RemovedObjectsTitle = "Objects were removed from the world while loading";

    private const string WorldLoadFailedAdvice = "Stop the server and restore the world from a backup before anyone plays on it.";
    private const string UnknownPrefabsAdvice = "A mod that placed these objects was removed or failed to load. Reinstall it, or clean the objects up with a world tool.";
    private const string RemovedObjectsAdvice = "The game dropped objects it can't load. If that was unexpected, back up the world before it saves again.";
    private const string OrphanChunksAdvice = "The world's chunk index didn't list these files. If areas are missing objects, restore a backup.";

    private static readonly MessageRule[] Rules =
    {
        new MessageRule(
            "World db couldn't load correctly, saving has been disabled",
            @"^World db couldn't load correctly, saving has been disabled",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Critical, "World failed to load, so saving is disabled", WorldLoadFailedAdvice, entry)),
        new MessageRule(
            " ZDOs with unknown prefabs. Will load anyway.",
            @"^Found (?<count>\d+) ZDOs with unknown prefabs\. Will load anyway\.$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Warning, UnknownPrefabsTitle, UnknownPrefabsAdvice, entry)
                .AddDetail(match.Groups["count"].Value + " objects with unknown prefabs")),
        new MessageRule(
            " appeared ",
            @"^\s*Hash (?<hash>-?\d+) appeared (?<count>\d+) times\.$",
            (match, entry, context) => context.Problems
                .FindOrCreate(ProblemSeverity.Warning, UnknownPrefabsTitle, UnknownPrefabsAdvice, entry)
                .AddDetail("Prefab hash " + match.Groups["hash"].Value + ": " + match.Groups["count"].Value + " objects")),
        new MessageRule(
            " ZDOs with prefabs not supported. Removing.",
            @"^Found (?<count>\d+) ZDOs with prefabs not supported\. Removing\. (?<before>\d+) => (?<after>\d+) ZDOs loaded\.$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Critical, RemovedObjectsTitle, RemovedObjectsAdvice, entry)
                .AddDetail(match.Groups["count"].Value + " objects removed (" + match.Groups["before"].Value + " before, " + match.Groups["after"].Value + " after)")),
        new MessageRule(
            " filtered out ",
            @"^\s*Hash (?<hash>-?\d+) filtered out (?<count>\d+) times\.$",
            (match, entry, context) => context.Problems
                .FindOrCreate(ProblemSeverity.Critical, RemovedObjectsTitle, RemovedObjectsAdvice, entry)
                .AddDetail("Prefab hash " + match.Groups["hash"].Value + ": " + match.Groups["count"].Value + " objects removed")),
        new MessageRule(
            "Removing orphan CHUNK file: ",
            @"^Removing orphan CHUNK file: (?<path>.+)$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Critical, "World chunk files were deleted as orphans", OrphanChunksAdvice, entry)
                .AddDetail(match.Groups["path"].Value)),
    };

    public bool TryExplain(LogEntry entry, AnalysisContext context) =>
        entry.IsWarningOrError && MessageRule.TryExplainWithAny(Rules, entry, context);
}
