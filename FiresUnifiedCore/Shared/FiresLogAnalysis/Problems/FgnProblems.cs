using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FiresLogAnalysis.Problems;

/// <summary>
/// FiresGhettoNetworking (FGN) and FiresSteamworksPatcher: version, setup, the dedicated server's frame rate and world clock,
/// clients' clock corrections and FGN's known errors, read from the lines FGN and vanilla write (FGN 1.4.x wording).
/// </summary>
internal sealed class FgnProblems : IProblemDetector
{
    internal const string PluginName = "FiresGhettoNetworkMod";
    private const string PatcherName = "FiresSteamworksPatcher";
    private const string Source = "FiresGhettoNetworkMod";

    private const double OverloadedFramesPerSecond = 10.0;
    private const double BadlyOverloadedFramesPerSecond = 5.0;
    private const double SlowClockSpeed = 0.9;
    private const double BadlySlowClockSpeed = 0.5;
    private const double MinClockWindowSeconds = 60.0;
    private const double LargeCorrectionMs = 1000.0;
    private const double LinksReportMinutes = 5.0;
    private const int MaxFrameRateDetails = 16;
    private const double PercentScale = 100.0;
    private const double SecondsPerMinute = 60.0;

    private const string UpdateFgnAdvice = "Update FGN on the server and on every player to the same, latest version.";
    private const string PatcherAdvice = "Put FiresSteamworksPatcher.dll in BepInEx\\patchers on the server and on every player. Without it FGN "
        + "can't raise Steam's receive buffer and message size, so large updates (big bases, joining) arrive slower.";
    private const string OverloadAdvice = "Server-Side Simulation makes the server build every object around every player, so its cost grows with "
        + "how many players there are and how far apart. At this frame rate creatures, ships and carts reach players only as often as the "
        + "server finishes a frame. Turn 'Enable Server-Side Simulation' off, or at least set 'Extended Zone Radius' to 0 and "
        + "'Enable Predictive Zone Streaming' off.";
    private const string SlowServerAdvice = "The server's frames are slow even without Server-Side Simulation. The other problems and the FDT "
        + "stall reports usually name the cause.";
    private const string SlowClockAdvice = "Unity advances game time by at most 0.2 s per frame, so a server under 5 frames a second lets the "
        + "world clock fall behind real time. Every 2 s the server re-syncs each player's clock, pulling it back: waves, ships, carts, the "
        + "day and every timer stutter. Fix the frame rate; FGN 1.4.68 and later also keep the world clock at real time.";
    private const string ClientClockAdvice = "This player's clock was corrected by a second or more. 'Ahead' means the server's world clock fell "
        + "behind (a slow server); 'behind' means this game froze. The server's log shows which.";
    // FGN 1.4.68+ prints [WorldClock] lines only when it eased the correction in, so the clock never jumped back.
    private const string WorldClockPrefix = "[WorldClock]";
    private const string EasedClockAdvice = "FGN eased these in, so the world clock never jumped back: waves and boats sped up or slowed "
        + "for a moment instead. A second or two right after joining is normal; several seconds all session means the server's frames "
        + "are slow (its [ServerHealth] line shows it).";
    private const string VcpAdvice = "ValheimCommunityPatch replaces the server's object create/destroy pass, so FGN turned Server-Side "
        + "Simulation off for this session. Remove one of them from the server.";
    private const string BroadOwnershipAdvice = "Broad ownership hands ships to the server even with Server-Side Ship Simulation off. Use the "
        + "Selective ownership toggle instead if players sail.";
    private const string BulkGateAdvice = "Harmless: that mod's config sync keeps its own 30 s limit. The HarmonyX error next to it is the "
        + "same event.";
    private const string WeaponTrailAdvice = "A visual effect running on the server's copies of players, a side effect of Server-Side "
        + "Simulation. Harmless; FGN 1.4.68 skips it on dedicated servers.";
    private const string HiddenReportsAdvice = "Set '01 - General / Log Level' to Info in BepInEx\\config\\com.Fire.FiresGhettoNetworkMod.cfg on "
        + "the server to log FGN's 5-minute [Links] report: each player's round trip and sends, and the slowest server frames.";
    private const string ConflictAdvice = "Both mods change how the server sends or simulates the world. Run only one of them.";

    private static readonly Regex ServerAuthorityBox = new Regex(@"sim: (?<sim>\w+)\W+own: (?<own>.+?)\s*│", RegexOptions.Compiled);
    private static readonly Regex TrafficBox = new Regex(@"delta: (?<delta>\w+)\W+throttle: (?<throttle>\w+)", RegexOptions.Compiled);
    private static readonly Regex ClientTier = new Regex(@"^\[LoadSummary\] │ tier: (?<tier>\w+)", RegexOptions.Compiled);
    private static readonly Regex ServerTier = new Regex(@"SERVER auto-tune APPLIED: tier=(?<tier>\w+)", RegexOptions.Compiled);
    private static readonly Regex NetworkRates = new Regex(
        @"^Network rates applied: update rate (?<rate>\d+)%, send (?<min>\d+)-(?<max>\d+) KB/s, send buffer (?<buffer>\d+) KB", RegexOptions.Compiled);
    private static readonly Regex SendScheduler = new Regex(@"^\[SendScheduler\] every player is sent world updates at (?<hz>\d+) per second", RegexOptions.Compiled);
    private static readonly Regex ServerStatus = new Regex(
        @"^\[ServerStatus\] \+(?<window>\d+)s \| tick: \d+ \(\+(?<ticks>\d+)\) \| peers: (?<peers>\d+) .*\| CreateDestroyObjects: (?<passes>\d+) passes",
        RegexOptions.Compiled);
    private static readonly Regex ServerHealth = new Regex(
        @"^\[ServerHealth\] last \d+ min: up to (?<players>\d+) player\(s\), (?<fps>[\d.]+) frames a second", RegexOptions.Compiled);
    private static readonly Regex WorldTime = new Regex(@"^Considering autobackup for Character\. World time: (?<world>[\d.]+),", RegexOptions.Compiled);
    private static readonly Regex ClockCorrections = new Regex(
        @"^\[(?:WaveClock|WorldClock)\] Eased in (?<count>\d+) server clock correction\(s\) .* the largest (?<behind>\d+) ms with this client's clock behind the server's, (?<ahead>\d+) ms with it ahead",
        RegexOptions.Compiled);
    private static readonly Regex BulkGateFailure = new Regex(@"^Failed to patch bulk-transfer gate at (?<target>[^:]+):", RegexOptions.Compiled);
    private static readonly Regex OwnPluginVersion = new Regex(@"^FiresGhettoNetworkMod v(?<version>\d+(?:\.\d+){1,3}) ", RegexOptions.Compiled);

    private static readonly string[] ConflictingPlugins = { "Serverside Simulations", "NetworkPerformanceSystem", "Better Networking" };

    private readonly List<FrameRateSample> frameRates = new List<FrameRateSample>();
    private readonly List<ClockSample> clockSamples = new List<ClockSample>();
    private readonly List<string> setup = new List<string>();
    private LogEntry firstEntry;
    private LogEntry firstFrameRateEntry;
    private LogEntry firstClockEntry;
    private DateTime? lastTimestamp;
    private string loggedVersion;
    private bool? simulationOn;
    private bool linksReportSeen;
    private bool noSteam;

    private readonly struct FrameRateSample
    {
        public FrameRateSample(int players, double framesPerSecond)
        {
            Players = players;
            FramesPerSecond = framesPerSecond;
        }

        public int Players { get; }
        public double FramesPerSecond { get; }
    }

    private readonly struct ClockSample
    {
        public ClockSample(DateTime wall, double world)
        {
            Wall = wall;
            World = world;
        }

        public DateTime Wall { get; }
        public double World { get; }
    }

    public bool TryExplain(LogEntry entry, AnalysisContext context)
    {
        if (entry.Timestamp.HasValue)
            lastTimestamp = entry.Timestamp;

        if (entry.Source == Source)
            return ReadFgnLine(entry, context);

        Match match;
        if ((match = WorldTime.Match(entry.Message)).Success && lastTimestamp.HasValue
            && double.TryParse(match.Groups["world"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var world))
        {
            firstClockEntry ??= entry;
            clockSamples.Add(new ClockSample(lastTimestamp.Value, world));
            return false;
        }
        if (firstEntry != null && entry.IsError && entry.Source == "HarmonyX" && entry.Message.IndexOf("ServerSync.ConfigSync", StringComparison.Ordinal) >= 0
            && entry.Message.IndexOf("waitForQueue", StringComparison.Ordinal) >= 0)
            return true;
        if (entry.IsError && IsWeaponTrailError(entry) && context.Environment.IsDedicatedServer)
        {
            context.Problems.Report(ProblemSeverity.Notice, "Weapon-trail errors on the server (Server-Side Simulation)", WeaponTrailAdvice, entry);
            return true;
        }
        return false;
    }

    private bool ReadFgnLine(LogEntry entry, AnalysisContext context)
    {
        firstEntry ??= entry;
        string message = entry.Message;
        Match match;
        // FGN 1.5.51+ on the Microsoft Store / Game Pass build (no Steam networking): the Steamworks patcher has nothing to patch
        if (message.IndexOf("Steam networking not present", StringComparison.Ordinal) >= 0)
            noSteam = true;

        if ((match = OwnPluginVersion.Match(message)).Success)
        {
            loggedVersion = match.Groups["version"].Value;
            return false;
        }
        if ((match = ServerAuthorityBox.Match(message)).Success)
        {
            simulationOn = string.Equals(match.Groups["sim"].Value, "ON", StringComparison.OrdinalIgnoreCase);
            setup.Add("Server-Side Simulation " + (simulationOn.Value ? "on" : "off") + ", creature/ship ownership: " + match.Groups["own"].Value.Trim());
            return false;
        }
        if ((match = TrafficBox.Match(message)).Success)
        {
            setup.Add("ZDO delta compression " + match.Groups["delta"].Value + ", ZDO throttling " + match.Groups["throttle"].Value);
            return false;
        }
        if (message.IndexOf("server-side ship physics is ON", StringComparison.Ordinal) >= 0)
        {
            setup.Add("Server-Side Ship Simulation on (the server owns ships nobody is sailing)");
            return false;
        }
        if ((match = ClientTier.Match(message)).Success || (match = ServerTier.Match(message)).Success)
        {
            setup.Add("Auto-tune tier " + match.Groups["tier"].Value);
            return false;
        }
        if (message.IndexOf("implausibly large for a dedicated game-server allocation", StringComparison.Ordinal) >= 0)
        {
            setup.Add("Auto-tune saw the container host's cores and memory and capped itself at Medium");
            return true;
        }
        if ((match = NetworkRates.Match(message)).Success)
        {
            setup.Add($"Send rate {match.Groups["min"].Value}-{match.Groups["max"].Value} KB/s, send buffer {match.Groups["buffer"].Value} KB, "
                + $"update rate {match.Groups["rate"].Value}%");
            return false;
        }
        if ((match = SendScheduler.Match(message)).Success)
        {
            setup.Add("World updates to each player at " + match.Groups["hz"].Value + " a second");
            return false;
        }
        if ((match = ServerStatus.Match(message)).Success)
        {
            ReadFrameRate(entry, match.Groups["passes"].Value, match.Groups["window"].Value, match.Groups["peers"].Value);
            return false;
        }
        if ((match = ServerHealth.Match(message)).Success)
        {
            if (LoggedNumbers.TryParseInt(match.Groups["players"].Value, out var players)
                && double.TryParse(match.Groups["fps"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var healthFps))
            {
                firstFrameRateEntry ??= entry;
                frameRates.Add(new FrameRateSample(players, healthFps));
            }
            return false;
        }
        if ((match = ClockCorrections.Match(message)).Success)
        {
            ReadClockCorrections(entry, context, match);
            return false;
        }
        if (message.StartsWith("[Links] ", StringComparison.Ordinal))
        {
            linksReportSeen = true;
            return false;
        }
        if ((match = BulkGateFailure.Match(message)).Success)
        {
            context.Problems.Report(ProblemSeverity.Notice, "FGN couldn't widen a mod's config-sync gate", BulkGateAdvice, entry)
                .AddDetail(match.Groups["target"].Value.Trim());
            return true;
        }
        if (message.IndexOf("Server-Side Simulation is OFF for this session: ValheimCommunityPatch", StringComparison.Ordinal) >= 0)
        {
            context.Problems.Report(ProblemSeverity.Warning, "ValheimCommunityPatch turned FGN's Server-Side Simulation off", VcpAdvice, entry);
            return true;
        }
        if (message.StartsWith("V2 BROAD claims SHIPS as well", StringComparison.Ordinal))
        {
            context.Problems.Report(ProblemSeverity.Warning, "FGN broad ownership gives ships to the server", BroadOwnershipAdvice, entry);
            return true;
        }
        return false;
    }

    private void ReadFrameRate(LogEntry entry, string passesText, string windowText, string peersText)
    {
        if (!LoggedNumbers.TryParseInt(passesText, out var passes) || !LoggedNumbers.TryParseInt(windowText, out var window)
            || !LoggedNumbers.TryParseInt(peersText, out var peers) || window <= 0 || peers <= 0)
            return;
        firstFrameRateEntry ??= entry;
        frameRates.Add(new FrameRateSample(peers, (double)passes / window));
    }

    private static void ReadClockCorrections(LogEntry entry, AnalysisContext context, Match match)
    {
        if (!LoggedNumbers.TryParseInt(match.Groups["behind"].Value, out var behindMs) || !LoggedNumbers.TryParseInt(match.Groups["ahead"].Value, out var aheadMs))
            return;
        if (Math.Max(behindMs, aheadMs) < LargeCorrectionMs)
            return;
        var sides = new List<string>();
        if (aheadMs > 0)
            sides.Add(aheadMs.ToString("N0", CultureInfo.InvariantCulture) + " ms ahead of the server");
        if (behindMs > 0)
            sides.Add(behindMs.ToString("N0", CultureInfo.InvariantCulture) + " ms behind it");
        var detail = "largest " + string.Join(", ", sides);
        if (entry.Message.StartsWith(WorldClockPrefix, StringComparison.Ordinal))
            context.Problems.Report(ProblemSeverity.Notice, "Server clock corrections eased in on this player", EasedClockAdvice, entry).AddDetail(detail);
        else
            context.Problems.Report(ProblemSeverity.Warning, "Large server clock corrections on this player", ClientClockAdvice, entry).AddDetail(detail);
    }

    private static bool IsWeaponTrailError(LogEntry entry)
    {
        foreach (var line in entry.ContinuationLines)
        {
            if (line.IndexOf("MeleeWeaponTrail.CustomFixedUpdate", StringComparison.Ordinal) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>Findings that need the whole log: version, patcher, setup, frame rate, world clock and hidden reports.</summary>
    public void Finish(AnalysisContext context, PluginList plugins)
    {
        var fgn = FindPlugin(plugins.Plugins, PluginName);
        string version = fgn?.Version ?? loggedVersion;
        if (fgn == null && firstEntry == null)
            return;
        var anchor = firstEntry ?? firstFrameRateEntry ?? firstClockEntry;

        ReportVersion(context, version, anchor);
        if (FindPlugin(plugins.Patchers, PatcherName) == null && anchor != null && context.Environment.BepInExVersion != null)
        {
            if (noSteam)
                context.Problems.Report(ProblemSeverity.Notice, "FiresSteamworksPatcher is not installed (not needed on this game build)",
                    "This game build has no Steam networking (Microsoft Store / Game Pass), so the patcher has nothing to patch. No action needed.", anchor);
            else
                context.Problems.Report(ProblemSeverity.Warning, "FiresSteamworksPatcher is missing", PatcherAdvice, anchor);
        }
        ReportConflicts(context, plugins, anchor);
        ReportSetup(context, version, anchor);
        ReportFrameRate(context);
        ReportWorldClock(context);
        ReportHiddenReports(context, anchor);
    }

    private static void ReportVersion(AnalysisContext context, string version, LogEntry anchor)
    {
        string latest = FgnReleases.LatestReleasedVersion;
        if (anchor == null || version == null || latest == null || CompareVersions(version, latest) >= 0)
            return;
        context.Problems.Report(ProblemSeverity.Warning, "FGN is out of date", UpdateFgnAdvice, anchor)
            .AddDetail("This log runs FGN " + version + "; the latest release is " + latest + ".");
    }

    private static void ReportConflicts(AnalysisContext context, PluginList plugins, LogEntry anchor)
    {
        if (anchor == null)
            return;
        foreach (var plugin in plugins.Plugins)
        {
            foreach (var name in ConflictingPlugins)
            {
                if (string.Equals(plugin.Name, name, StringComparison.OrdinalIgnoreCase))
                    context.Problems.Report(ProblemSeverity.Warning, "A second networking mod runs alongside FGN", ConflictAdvice, anchor)
                        .AddDetail(plugin.ToString());
            }
        }
    }

    private void ReportSetup(AnalysisContext context, string version, LogEntry anchor)
    {
        if (anchor == null || (setup.Count == 0 && version == null))
            return;
        var problem = context.Problems.Report(ProblemSeverity.Notice, "FGN setup in this log", null, anchor);
        if (version != null)
            problem.AddDetail("FGN " + version);
        foreach (var line in setup)
            problem.AddDetail(line);
    }

    private void ReportFrameRate(AnalysisContext context)
    {
        if (frameRates.Count == 0 || firstFrameRateEntry == null)
            return;
        double worst = double.MaxValue;
        foreach (var sample in frameRates)
            worst = Math.Min(worst, sample.FramesPerSecond);
        if (worst >= OverloadedFramesPerSecond)
            return;

        bool simulation = simulationOn ?? true;
        var severity = worst < BadlyOverloadedFramesPerSecond ? ProblemSeverity.Critical : ProblemSeverity.Warning;
        string title = simulation ? "Server-Side Simulation is overloading the server" : "The server runs at a low frame rate";
        var problem = context.Problems.Report(severity, title, simulation ? OverloadAdvice : SlowServerAdvice, firstFrameRateEntry);
        problem.AddDetail("Frames a second by players online: " + DescribeFrameRatesByPlayers());
    }

    private string DescribeFrameRatesByPlayers()
    {
        var worstByPlayers = new SortedDictionary<int, double>();
        foreach (var sample in frameRates)
        {
            if (!worstByPlayers.TryGetValue(sample.Players, out var worst) || sample.FramesPerSecond < worst)
                worstByPlayers[sample.Players] = sample.FramesPerSecond;
        }
        var text = new StringBuilder();
        int listed = 0;
        foreach (var pair in worstByPlayers)
        {
            if (listed++ == MaxFrameRateDetails)
                break;
            if (text.Length > 0)
                text.Append(", ");
            text.Append(pair.Key.ToString(CultureInfo.InvariantCulture)).Append(" player").Append(pair.Key == 1 ? "" : "s").Append(": ")
                .Append(pair.Value.ToString("0.0", CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    private void ReportWorldClock(AnalysisContext context)
    {
        if (!context.Environment.IsDedicatedServer || clockSamples.Count < 2 || firstClockEntry == null)
            return;
        double slowest = double.MaxValue;
        var start = clockSamples[0];
        for (int i = 1; i < clockSamples.Count; i++)
        {
            double wallSeconds = (clockSamples[i].Wall - start.Wall).TotalSeconds;
            if (wallSeconds < MinClockWindowSeconds)
                continue;
            slowest = Math.Min(slowest, (clockSamples[i].World - start.World) / wallSeconds);
            start = clockSamples[i];
        }
        var first = clockSamples[0];
        var last = clockSamples[clockSamples.Count - 1];
        double totalWall = (last.Wall - first.Wall).TotalSeconds;
        if (slowest == double.MaxValue || slowest >= SlowClockSpeed || totalWall <= 0)
            return;
        double overall = (last.World - first.World) / totalWall;
        var severity = slowest < BadlySlowClockSpeed ? ProblemSeverity.Critical : ProblemSeverity.Warning;
        context.Problems.Report(severity, "The server's world clock fell behind real time", SlowClockAdvice, firstClockEntry)
            .AddDetail("Slowest stretch " + Percent(slowest) + " of real time; " + Percent(overall) + " over the whole log ("
                + (totalWall / SecondsPerMinute).ToString("0", CultureInfo.InvariantCulture) + " min)");
    }

    private void ReportHiddenReports(AnalysisContext context, LogEntry anchor)
    {
        var session = context.Environment.SessionLength;
        if (anchor == null || linksReportSeen || !context.Environment.IsDedicatedServer || frameRates.Count == 0
            || !session.HasValue || session.Value.TotalMinutes < LinksReportMinutes)
            return;
        context.Problems.Report(ProblemSeverity.Notice, "FGN's detailed network reports are switched off", HiddenReportsAdvice, anchor);
    }

    private static string Percent(double fraction) => (fraction * PercentScale).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static PluginInfo FindPlugin(IReadOnlyList<PluginInfo> plugins, string name)
    {
        foreach (var plugin in plugins)
        {
            if (string.Equals(plugin.Name, name, StringComparison.Ordinal))
                return plugin;
        }
        return null;
    }

    internal static int CompareVersions(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        int length = Math.Max(leftParts.Length, rightParts.Length);
        for (int i = 0; i < length; i++)
        {
            int leftPart = i < leftParts.Length && LoggedNumbers.TryParseInt(leftParts[i], out var l) ? l : 0;
            int rightPart = i < rightParts.Length && LoggedNumbers.TryParseInt(rightParts[i], out var r) ? r : 0;
            if (leftPart != rightPart)
                return leftPart.CompareTo(rightPart);
        }
        return 0;
    }
}
