using System.Text.RegularExpressions;

namespace FiresLogAnalysis.Problems;

/// <summary>Mod loading failures reported by the BepInEx 5 chainloader (Chainloader.cs, v5-lts).</summary>
internal sealed class BepInExLoadProblems : IProblemDetector
{
    private const string BepInExSource = "BepInEx";

    private const string MissingDependencyAdvice = "Install the missing dependencies (listed by mod ID) or remove this mod.";
    private const string IncompatibleAdvice = "These mods are marked as incompatible with each other. Remove one of them.";
    private const string DependencyFailedAdvice = "A mod this one needs failed to load. Fix that one first.";
    private const string DuplicateInstallAdvice = "The same mod is installed twice. Delete the older copy from BepInEx/plugins.";
    private const string WrongBepInExAdvice = "Update the mod, or the BepInEx pack it was built for.";
    private const string LoadFailedAdvice = "The mod crashed while loading. Update it, or remove it if there is no update.";
    private const string GameStartFailedAdvice = "BepInEx itself failed to start. Reinstall BepInExPack Valheim; the error follows this line in the log.";

    private static readonly Regex PluginNameAndVersion = new Regex(@"^(?<name>.+) \d+(?:\.\d+){1,3}$", RegexOptions.Compiled);

    private static readonly MessageRule[] Rules =
    {
        new MessageRule(
            "because it has missing dependencies:",
            @"^Could not load \[(?<plugin>.+)\] because it has missing dependencies: (?<dependencies>.+)$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Critical, "Mod not loaded, missing dependency: " + match.Groups["plugin"].Value, MissingDependencyAdvice, entry)
                .AddDetail("Needs " + match.Groups["dependencies"].Value)),
        new MessageRule(
            "because it is incompatible with:",
            @"^Could not load \[(?<plugin>.+)\] because it is incompatible with: (?<plugins>.+)$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Critical, "Mod not loaded, incompatible mod installed: " + match.Groups["plugin"].Value, IncompatibleAdvice, entry)
                .AddDetail("Conflicts with " + match.Groups["plugins"].Value)),
        new MessageRule(
            "because it has a dependency that was not loaded",
            @"^Skipping \[(?<plugin>.+)\] because it has a dependency that was not loaded\.",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Critical, "Mod not loaded, its dependency failed: " + match.Groups["plugin"].Value, DependencyFailedAdvice, entry)),
        new MessageRule(
            "because a newer version exists",
            @"^Skipping \[(?<plugin>.+)\] because a newer version exists \((?<loaded>.+)\)$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Warning, "Mod installed twice: " + PluginName(match.Groups["plugin"].Value), DuplicateInstallAdvice, entry)
                .AddDetail("Skipped " + match.Groups["plugin"].Value + ", loaded " + match.Groups["loaded"].Value)),
        new MessageRule(
            "targets a wrong version of BepInEx",
            @"^Plugin \[(?<plugin>.+)\] targets a wrong version of BepInEx \((?<version>.+)\)",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Warning, "Mod built for a different BepInEx: " + match.Groups["plugin"].Value, WrongBepInExAdvice, entry)
                .AddDetail("Built for BepInEx " + match.Groups["version"].Value)),
        new MessageRule(
            "Error loading [",
            @"^Error loading \[(?<plugin>.+)\] : (?<message>.*)$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Critical, "Mod failed to load: " + match.Groups["plugin"].Value, LoadFailedAdvice, entry)
                .AddDetail(match.Groups["message"].Value)),
        new MessageRule(
            "Error occurred starting the game",
            @"^Error occurred starting the game$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Critical, "BepInEx failed to start the game", GameStartFailedAdvice, entry)),
    };

    public bool TryExplain(LogEntry entry, AnalysisContext context) =>
        entry.Source == BepInExSource && MessageRule.TryExplainWithAny(Rules, entry, context);

    private static string PluginName(string pluginWithVersion)
    {
        var match = PluginNameAndVersion.Match(pluginWithVersion);
        return match.Success ? match.Groups["name"].Value : pluginWithVersion;
    }
}
