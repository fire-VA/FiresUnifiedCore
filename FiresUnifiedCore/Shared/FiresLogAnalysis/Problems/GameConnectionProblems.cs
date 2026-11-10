using System.Text.RegularExpressions;

namespace FiresLogAnalysis.Problems;

/// <summary>Connection results logged by vanilla ZNet (strings verified against the 1.0.12 assembly_valheim.dll).</summary>
internal sealed class GameConnectionProblems : IProblemDetector
{
    private const string MatchGameVersionAdvice = "Everyone must run the same Valheim version as the server. Check for a pending game update or a beta branch.";
    private const string RejectedVersionAdvice = "The server refused this game's version or mods. The other problems listed usually say which one.";
    private const string SessionDataAdvice = "Usually the server runs a different game version.";
    private const string ConnectFailedAdvice = "The server may be offline, or the address, port or password is wrong.";

    private static readonly MessageRule[] Rules =
    {
        new MessageRule(
            " has incompatible version, mine:",
            @"^Peer (?<peer>.+) has incompatible version, mine:(?<mine>.+) \(network version (?<mineNetwork>\w+)\)\s+remote (?<remote>.+) \(network version (?<remoteNetwork>\w+)\)$",
            (match, entry, context) =>
            {
                context.DisconnectedPeers.Add(match.Groups["peer"].Value);
                context.Problems
                    .Report(ProblemSeverity.Critical, "Game version mismatch", MatchGameVersionAdvice, entry)
                    .AddDetail("This side runs " + match.Groups["mine"].Value + " (network " + match.Groups["mineNetwork"].Value
                        + "), the other side runs " + match.Groups["remote"].Value + " (network " + match.Groups["remoteNetwork"].Value + ")");
            }),
        new MessageRule(
            "Got connectoin error msg ",
            @"^Got connectoin error msg (?<status>\w+)$",
            (match, entry, context) => ReportConnectionError(match.Groups["status"].Value, entry, context)),
        new MessageRule(
            "because the found session has incompatible data!",
            @"^Failed to join server '(?<server>.*)' because the found session has incompatible data!$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Critical, "Couldn't join a server: incompatible session data", SessionDataAdvice, entry)
                .AddDetail("Server: " + match.Groups["server"].Value)),
        new MessageRule(
            "Failed to connect to server",
            @"^Failed to connect to server$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Warning, "Couldn't connect to the server", ConnectFailedAdvice, entry)),
        new MessageRule(
            " has wrong password",
            @"^Peer (?<peer>.+) has wrong password$",
            (match, entry, context) => RefuseConnection(match, entry, context, "Connections refused: wrong password")),
        new MessageRule(
            " disconnected due to server is full",
            @"^Peer (?<peer>.+) disconnected due to server is full$",
            (match, entry, context) => RefuseConnection(match, entry, context, "Connections refused: server full")),
        new MessageRule(
            " is blacklisted or not in whitelist.",
            @"^Player .+ : (?<peer>.+) is blacklisted or not in whitelist\.$",
            (match, entry, context) => RefuseConnection(match, entry, context, "Connections refused: banned or not on the permitted list")),
        new MessageRule(
            " disconnected because they were not authenticated!",
            @"^Peer (?<peer>.+) disconnected because they were not authenticated!$",
            (match, entry, context) => RefuseConnection(match, entry, context, "Connections dropped: not authenticated")),
        new MessageRule(
            " is blocked by the server owner (you), disconnecting them!",
            @"^The player with the ID (?<peer>.+) is blocked by the server owner \(you\), disconnecting them!$",
            (match, entry, context) => RefuseConnection(match, entry, context, "Connections refused: blocked player")),
        new MessageRule(
            "Peer diconnected due to server platform privileges disallowing crossplay.",
            @"^Peer diconnected due to server platform privileges disallowing crossplay\. Server platform: (?<server>.*?)\s+Peer platform: (?<peer>.*)$",
            (match, entry, context) => context.Problems
                .Report(ProblemSeverity.Notice, "Connections refused: crossplay not allowed", null, entry)
                .AddDetail("Server platform " + match.Groups["server"].Value + ", player platform " + match.Groups["peer"].Value)),
    };

    public bool TryExplain(LogEntry entry, AnalysisContext context) =>
        MessageRule.TryExplainWithAny(Rules, entry, context);

    private static void RefuseConnection(Match match, LogEntry entry, AnalysisContext context, string title)
    {
        context.DisconnectedPeers.Add(match.Groups["peer"].Value);
        context.Problems.Report(ProblemSeverity.Notice, title, null, entry);
    }

    private static void ReportConnectionError(string status, LogEntry entry, AnalysisContext context)
    {
        switch (status)
        {
            case "ErrorVersion":
                context.Problems.Report(ProblemSeverity.Critical, "The server rejected this game: version or mod mismatch", RejectedVersionAdvice, entry);
                break;
            case "ErrorPassword":
                context.Problems.Report(ProblemSeverity.Warning, "The server rejected the password", null, entry);
                break;
            case "ErrorBanned":
                context.Problems.Report(ProblemSeverity.Warning, "This player is banned from the server", null, entry);
                break;
            case "ErrorFull":
                context.Problems.Report(ProblemSeverity.Notice, "The server was full", null, entry);
                break;
            case "ErrorKicked":
                context.Problems.Report(ProblemSeverity.Warning, "Kicked from the server", null, entry);
                break;
            case "ErrorPlatformExcluded":
                context.Problems.Report(ProblemSeverity.Warning, "The server doesn't allow this platform", null, entry);
                break;
            case "ErrorCrossplayPrivilege":
                context.Problems.Report(ProblemSeverity.Warning, "Crossplay is blocked by this account's platform settings", null, entry);
                break;
            default:
                context.Problems.Report(ProblemSeverity.Warning, "The server ended the connection: " + status, null, entry);
                break;
        }
    }
}
