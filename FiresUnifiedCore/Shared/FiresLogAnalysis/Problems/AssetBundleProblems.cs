namespace FiresLogAnalysis.Problems;

internal sealed class AssetBundleProblems : IProblemDetector
{
    private const string DuplicateBundleAdvice = "Usually the same mod is installed twice. Check BepInEx/plugins for duplicate copies.";
    private const string StreamBundleAdvice = "Two mods ship the same bundle inside their DLLs, or one mod loads it twice. The mod that logs an error right after this line is usually the one that tried.";

    /// <summary>The name Unity gives a bundle a mod loaded from a stream (usually a resource inside its DLL) rather than a file.</summary>
    private const string StreamBundleName = "IO.Stream";

    private static readonly MessageRule[] Rules =
    {
        new MessageRule(
            "can't be loaded because another AssetBundle with the same files is already loaded.",
            @"^The AssetBundle '(?<bundle>.*)' can't be loaded because another AssetBundle with the same files is already loaded\.$",
            (match, entry, context) =>
            {
                var bundle = match.Groups["bundle"].Value;
                if (bundle == StreamBundleName)
                    context.Problems
                        .Report(ProblemSeverity.Warning, "A mod loaded an asset bundle that was already loaded", StreamBundleAdvice, entry)
                        .AddDetail("Loaded from inside a mod's DLL, so the log has no file name for it");
                else
                    context.Problems
                        .Report(ProblemSeverity.Warning, "An asset bundle was loaded twice", DuplicateBundleAdvice, entry)
                        .AddDetail("Bundle '" + bundle + "'");
            }),
    };

    public bool TryExplain(LogEntry entry, AnalysisContext context) =>
        entry.IsError && MessageRule.TryExplainWithAny(Rules, entry, context);
}
