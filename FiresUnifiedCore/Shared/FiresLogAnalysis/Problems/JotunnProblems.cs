using System.Text.RegularExpressions;

namespace FiresLogAnalysis.Problems;

/// <summary>Jotunn's network compatibility check (JotunnLib/Utils/ModCompatibility/ModCompatibility.cs).</summary>
internal sealed class JotunnProblems : IProblemDetector
{
    private const string ServerNeedsModsAdvice = "Install Jotunn and these mods on the server, or remove them from this game.";
    private const string ClientNeedsModsAdvice = "Players need Jotunn and these mods installed to join.";
    private const string MissingOnClientAdvice = "The server requires this mod. Install it on the client.";
    private const string ExtraOnClientAdvice = "The server doesn't allow this mod. Remove it from the client, or install it on the server.";
    private const string MatchVersionAdvice = "Client and server need the same version of this mod.";
    private const string MatchJotunnAdvice = "Client and server need the same Jotunn version.";

    private static readonly MessageRule[] Rules =
    {
        new MessageRule(
            "is not installed on the server. Client has mandatory mods",
            "^J\u00F6tunn is not installed on the server\\. Client has mandatory mods",
            (match, entry, context) => AddModList(
                context.Problems.Report(ProblemSeverity.Critical, "Server doesn't have Jotunn, but mods here require it", ServerNeedsModsAdvice, entry),
                entry,
                "Needed on the server: ")),
        new MessageRule(
            "is not installed on the client. Server has mandatory mods",
            "^J\u00F6tunn is not installed on the client\\. Server has mandatory mods",
            (match, entry, context) => AddModList(
                context.Problems.Report(ProblemSeverity.Critical, "A player without Jotunn was refused", ClientNeedsModsAdvice, entry),
                entry,
                "Needed on clients: ")),
        new MessageRule(
            "Missing mod on client: ",
            @"^Missing mod on client: (?<mod>.+)$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Critical, "Mod missing on the client: " + match.Groups["mod"].Value, MissingOnClientAdvice, entry)),
        new MessageRule(
            "Client loaded additional mod: ",
            @"^Client loaded additional mod: (?<mod>.+)$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Critical, "Client has a mod the server doesn't allow: " + match.Groups["mod"].Value, ExtraOnClientAdvice, entry)),
        new MessageRule(
            "Mod version mismatch ",
            @"^Mod version mismatch (?<mod>.+): Server (?<server>\S+), Client (?<client>\S+)$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Critical, "Mod version mismatch: " + match.Groups["mod"].Value, MatchVersionAdvice, entry)
                .AddDetail("Server has " + match.Groups["server"].Value + ", client has " + match.Groups["client"].Value)),
        new MessageRule(
            "Jotunn version on ",
            @"^Jotunn version on (?<newer>client|server) is higher than (?:server|client) version: (?<version>\S+)$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Critical, "Jotunn version mismatch", MatchJotunnAdvice, entry)
                .AddDetail("The " + match.Groups["newer"].Value + " has the newer Jotunn (this log's Jotunn is " + match.Groups["version"].Value + ")")),
        new MessageRule(
            "Disconnecting modded client with incompatible version message.",
            @"^(?:RPC_PeerInfo|RPC_Jotunn_ReceiveVersionData): Disconnecting modded client with incompatible version message\.",
            (match, entry, context) => { }),
    };

    public bool TryExplain(LogEntry entry, AnalysisContext context) =>
        entry.IsWarningOrError && MessageRule.TryExplainWithAny(Rules, entry, context);

    private static void AddModList(Problem problem, LogEntry entry, string prefix)
    {
        foreach (var modName in entry.ContinuationLines)
            problem.AddDetail(prefix + modName.Trim());
    }
}
