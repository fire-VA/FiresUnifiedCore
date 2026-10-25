using System.Text.RegularExpressions;

namespace FiresLogAnalysis.Problems;

/// <summary>Why players dropped: vanilla ZRpc timeouts and Steam connection problems, FGN's join grace, and AzuAntiCheat kicks
/// (strings from the 1.0 assembly_valheim, FGN 1.4.68 and AzuAntiCheat 5.1).</summary>
internal sealed class DisconnectProblems : IProblemDetector
{
    private const string TimeoutAdvice = "The other side sent nothing for 30 s. On a server this is usually a player whose game froze while "
        + "loading the world; FGN 1.4.68 and later wait up to 'Join Grace Seconds' (5 min) for a joining player's first spawn.";
    private const string SteamDropAdvice = "Steam lost the connection (network trouble on either end, or the game stalled long enough for Steam "
        + "to give up).";
    private const string JoinGraceAdvice = "FGN waited for players whose game went silent while loading, instead of dropping them after 30 s.";
    private const string AntiCheatAdvice = "AzuAntiCheat removed these players. Each detail is its reason.";
    private const string MidSessionAssemblyAdvice = "A mod loads this assembly after the player joins, and AzuAntiCheat treats that as "
        + "injection. Allow the assembly in AzuAntiCheat's settings, or update or remove the mod that ships it.";

    private static readonly MessageRule[] Rules =
    {
        new MessageRule(
            "ZRpc timeout detected",
            @"^ZRpc timeout detected$",
            (match, entry, context) => context.Problems.Report(ProblemSeverity.Warning,
                context.Environment.IsDedicatedServer ? "A player timed out" : "The connection to the server timed out", TimeoutAdvice, entry)),
        new MessageRule(
            "Got problem ",
            @"^Got problem (?<code>\d+):(?<text>.+)$",
            (match, entry, context) => context.Problems.Report(ProblemSeverity.Notice, "Steam connection problems", SteamDropAdvice, entry)
                .AddDetail(match.Groups["code"].Value + ": " + match.Groups["text"].Value.Trim())),
        new MessageRule(
            "[JoinGrace] ",
            @"^\[JoinGrace\] (?<text>.+)$",
            (match, entry, context) => context.Problems.Report(ProblemSeverity.Notice, "Joining players took long to load", JoinGraceAdvice, entry)
                .AddDetail(match.Groups["text"].Value)),
        new MessageRule(
            "Kicking ",
            @"^Kicking (?<peer>\d+) \((?<category>\w+)\): (?<reason>.+?)\s*(?:Character will (?:not be saved|be saved first)\.)?$",
            (match, entry, context) => ReportAntiCheatKick(match.Groups["peer"].Value, match.Groups["category"].Value, match.Groups["reason"].Value, entry, context)),
        new MessageRule(
            "Kicked by server: ",
            @"^Kicked by server: (?<reason>.+)$",
            (match, entry, context) => ReportAntiCheatKick(null, null, match.Groups["reason"].Value, entry, context)),
    };

    private static readonly Regex MidSessionAssembly = new Regex(@"Unknown assembly loaded mid-session: (?<assembly>\S+)", RegexOptions.Compiled);

    public bool TryExplain(LogEntry entry, AnalysisContext context) =>
        MessageRule.TryExplainWithAny(Rules, entry, context);

    private static void ReportAntiCheatKick(string peer, string category, string reason, LogEntry entry, AnalysisContext context)
    {
        if (entry.Source != "AzuAntiCheat")
            return;
        if (peer != null)
            context.DisconnectedPeers.Add(peer);
        var assembly = MidSessionAssembly.Match(reason);
        if (assembly.Success)
        {
            context.Problems.Report(ProblemSeverity.Warning, "AzuAntiCheat kicks players for an assembly a mod loads after joining",
                    MidSessionAssemblyAdvice, entry)
                .AddDetail(assembly.Groups["assembly"].Value);
            return;
        }
        context.Problems.Report(ProblemSeverity.Warning, "AzuAntiCheat kicked players", AntiCheatAdvice, entry)
            .AddDetail(category != null ? category + ": " + reason : reason);
    }
}
