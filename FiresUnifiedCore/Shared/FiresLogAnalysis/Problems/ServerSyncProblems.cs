namespace FiresLogAnalysis.Problems;

/// <summary>Version checks from blaxxun's ServerSync (ConfigSync.cs VersionCheck), bundled into many mods.</summary>
internal sealed class ServerSyncProblems : IProblemDetector
{
    private const string MatchServerVersionAdvice = "Install the same version of this mod that the server runs.";
    private const string MissingOnServerAdvice = "The server doesn't have this mod. Remove it from this game, or ask the server admin to install it.";
    private const string KickedPlayersAdvice = "Players need this mod installed at the version the server requires.";
    private const string ConfigMismatchAdvice = "Client and server most likely run different versions of this mod.";

    private static readonly MessageRule[] Rules =
    {
        new MessageRule(
            " is not installed on the server.",
            @"^(?<mod>.+) is not installed on the server\.$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Critical, "Mod missing on the server: " + match.Groups["mod"].Value, MissingOnServerAdvice, entry)),
        new MessageRule(
            " may not be higher than version ",
            @"^(?<mod>.+) may not be higher than version (?<server>\S+)\. You have version (?<client>\S+)\.$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Critical, "Mod version mismatch: " + match.Groups["mod"].Value, MatchServerVersionAdvice, entry)
                .AddDetail("Server has " + match.Groups["server"].Value + ", this game has " + match.Groups["client"].Value)),
        new MessageRule(
            " needs to be at least version ",
            @"^(?<mod>.+) needs to be at least version (?<minimum>\S+)\. You have version (?<client>\S+)\.$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Critical, "Mod version mismatch: " + match.Groups["mod"].Value, MatchServerVersionAdvice, entry)
                .AddDetail("Server needs at least " + match.Groups["minimum"].Value + ", this game has " + match.Groups["client"].Value)),
        new MessageRule(
            "Disconnect: The client (",
            @"^Disconnect: The client \((?<client>.+)\) doesn't have the correct (?<mod>.+) version (?<minimum>\S+)$",
            (match, entry, context) =>
            {
                context.DisconnectedPeers.Add(match.Groups["client"].Value);
                context.Problems
                    .Report(ProblemSeverity.Critical, "Players kicked for mod version: " + match.Groups["mod"].Value, KickedPlayersAdvice, entry)
                    .AddDetail("Server requires " + match.Groups["mod"].Value + " " + match.Groups["minimum"].Value);
            }),
        new MessageRule(
            "This may happen if client and server versions of the mod do not match.",
            @"^Received unknown config entry (?<entry>.+) in section (?<section>.+) for mod (?<mod>.+)\. This may happen if client and server versions of the mod do not match\.$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Warning, "Synced config doesn't match: " + match.Groups["mod"].Value, ConfigMismatchAdvice, entry)
                .AddDetail("Unknown setting \"" + match.Groups["entry"].Value + "\" in [" + match.Groups["section"].Value + "]")),
        new MessageRule(
            "Received server version is not equal:",
            @"^Received server version is not equal: server version = (?<server>.+); local version = (?<local>.+)$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Warning, "A synced mod runs a different version on the server", MatchServerVersionAdvice, entry)
                .AddDetail("Server has " + match.Groups["server"].Value + ", this game has " + match.Groups["local"].Value)),
    };

    public bool TryExplain(LogEntry entry, AnalysisContext context) =>
        entry.IsWarningOrError && MessageRule.TryExplainWithAny(Rules, entry, context);
}
