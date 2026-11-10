using System;
using System.Text.RegularExpressions;

namespace FiresLogAnalysis.Problems;

/// <summary>
/// The VersionHandshake.cs from Azumatt's mod templates, copied into many mods. The log source is the mod name.
/// A version check line with two different versions is the mismatch; the disconnect lines that follow are explained by it.
/// </summary>
internal sealed class VersionHandshakeProblems : IProblemDetector
{
    private const string MatchVersionAdvice = "Everyone needs the same version of this mod as the server.";
    private const string NoVersionSentAdvice = "The player either doesn't have this mod installed, or left before the version check finished.";
    private const string VersionCheckPrefix = "Version check, local: ";
    private const string PeerPrefix = "Peer (";

    private static readonly Regex VersionCheck = new Regex(@"^Version check, local: (?<local>\S+),\s+remote: (?<remote>\S+)$", RegexOptions.Compiled);
    private static readonly Regex IncompatiblePeer = new Regex(@"^Peer \((?<peer>.+)\) has incompatible version, disconnecting\.\.\.$", RegexOptions.Compiled);
    private static readonly Regex PeerNeverSentVersion = new Regex(@"^Peer \((?<peer>.+)\) never sent version or couldn't due to previous disconnect, disconnecting$", RegexOptions.Compiled);

    public bool TryExplain(LogEntry entry, AnalysisContext context)
    {
        if (!entry.Message.StartsWith(VersionCheckPrefix, StringComparison.Ordinal) && !entry.Message.StartsWith(PeerPrefix, StringComparison.Ordinal))
            return false;

        var versionCheck = VersionCheck.Match(entry.Message);
        if (versionCheck.Success)
        {
            ReportMismatch(entry, context, versionCheck.Groups["local"].Value, versionCheck.Groups["remote"].Value);
            return true;
        }

        var incompatiblePeer = IncompatiblePeer.Match(entry.Message);
        if (incompatiblePeer.Success)
        {
            context.DisconnectedPeers.Add(incompatiblePeer.Groups["peer"].Value);
            return true;
        }

        var peerNeverSentVersion = PeerNeverSentVersion.Match(entry.Message);
        if (!peerNeverSentVersion.Success)
            return false;

        if (!context.DisconnectedPeers.Contains(peerNeverSentVersion.Groups["peer"].Value))
            context.Problems.Report(ProblemSeverity.Warning, "Player connected without sending a version for " + entry.Source, NoVersionSentAdvice, entry);
        return true;
    }

    private static void ReportMismatch(LogEntry entry, AnalysisContext context, string localVersion, string remoteVersion)
    {
        if (localVersion == remoteVersion)
            return;

        var detail = context.Environment.IsDedicatedServer
            ? "Server has " + localVersion + ", a player had " + remoteVersion
            : "This game has " + localVersion + ", the other side had " + remoteVersion;
        context.Problems
            .Report(ProblemSeverity.Critical, "Mod version mismatch: " + entry.Source, MatchVersionAdvice, entry)
            .AddDetail(detail);
    }
}
