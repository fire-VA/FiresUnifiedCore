using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FiresLogAnalysis;

/// <summary>
/// Reads FiresDebugginTools' own diagnostic output into a run summary.
///
/// The bot already explains what BROKE. This explains how the run PERFORMED, which is the question
/// actually being asked when a log gets dropped after a bad session - and the answer is spread over
/// hundreds of repeated lines that nobody reads by hand. Worst frame, the stalls, the methods that
/// cost the most, and the scene's size all come from lines FDT already emits.
///
/// Everything here is optional: a log from a server, or from a client with probes off, simply has
/// none of these lines and the section is omitted rather than reported as zeroes. A zero that means
/// "not measured" is indistinguishable from a zero that means "fast", and this project has been
/// bitten by that often enough to make it a rule.
/// </summary>
internal sealed class RunPerformanceReader
{
    private const string FdtSource = "FiresDebugginTools";

    // "Frames:    8.6 fps  avg= 116.7ms  worst=6105.7ms  best=  16.7ms  >33ms=10/64  >16ms=64/64"
    private static readonly Regex FrameSample = new Regex(
        @"^Frames:\s*(?<fps>[\d.]+) fps\s+avg=\s*(?<avg>[\d.]+)ms\s+worst=\s*(?<worst>[\d.]+)ms\s+best=\s*(?<best>[\d.]+)ms\s+>33ms=(?<over33>\d+)/(?<total>\d+)",
        RegexOptions.Compiled);

    // "=== Frame & method timings (last 30.01s) ===" - the window the rows below cover.
    private static readonly Regex TimingWindow = new Regex(
        @"^=== Frame & method timings \(last (?<seconds>[\d.]+)s\) ===",
        RegexOptions.Compiled);

    // "  ClutterSystem.GenerateVegPatch      60.56   79   0.767   3.93"  (label, "ms/sec", calls, avg, max).
    // FDT 1.1.64's "ms/sec" column holds the window's total ms (1540 calls x 7.170 ms = 11041.76 over 30 s), so the
    // cost per second is worked out from calls x avg over the window, which stays right if FDT fixes the column.
    private static readonly Regex TimingRow = new Regex(
        @"^\s{2,}(?<label>\S.*?)\s{2,}(?<msPerSec>\d+\.\d+)\s+(?<calls>\d+)\s+(?<avg>\d+\.\d+)\s+(?<max>\d+\.\d+)\s*$",
        RegexOptions.Compiled);

    // "[163.4fps 6.1ms | worst 0.0ms GC 0/5s | draws 569] RENDER TOP | scene"
    private static readonly Regex RenderTop = new Regex(
        @"^\[(?<fps>[\d.]+)fps\s+(?<frame>[\d.]+)ms\s*\|\s*worst\s+(?<worst>[\d.]+)ms\s+GC\s+(?<gc>\d+)/\d+s\s*\|\s*draws\s+(?<draws>\d+)\]",
        RegexOptions.Compiled);

    // "[LoginDiag] MAIN-THREAD STALL 1947 ms  at T+0.000s  during: post-OnSpawned (world settling)"
    private static readonly Regex Stall = new Regex(
        @"^\[LoginDiag\] MAIN-THREAD STALL (?<ms>\d+) ms\s+at T\+(?<at>[\d.]+)s\s+during:\s*(?<phase>.*)$",
        RegexOptions.Compiled);

    private static readonly string[] StallDetailPrefixes = { "[LoginDiag] stall breakdown", "[LoginDiag] stall trail" };

    // "[LoginDiag] 85 further stall frame(s) in the last 60s were not broken down; ..."
    private static readonly Regex FurtherStalls = new Regex(@"^\[LoginDiag\] \d+ further stall frame\(s\)", RegexOptions.Compiled);

    // "zdo 186,213   inst 29,767   no inst 156,446   zones 129" - the overlay's world line.
    private static readonly Regex WorldStats = new Regex(
        @"zdo\s+(?<zdo>[\d,]+)\s+inst\s+(?<inst>[\d,]+).*?zones\s+(?<zones>\d+)",
        RegexOptions.Compiled);

    private readonly List<FrameSampleInfo> frames = new List<FrameSampleInfo>();
    private readonly Dictionary<string, TimingAccumulator> timings = new Dictionary<string, TimingAccumulator>();
    private readonly List<StallInfo> stalls = new List<StallInfo>();
    private int peakDraws, peakZdo, peakInstances, peakZones;
    private double timingWindowSeconds;

    /// <summary>True when the line was a pure diagnostic reading, so the caller can keep it out of the noise lists.</summary>
    public bool TryRead(LogEntry entry)
    {
        var message = entry.Message;

        var stall = Stall.Match(message);
        if (stall.Success && LoggedNumbers.TryParseInt(stall.Groups["ms"].Value, out var stallMs))
        {
            var phase = stall.Groups["phase"].Value.Trim();
            if (phase.Length == 0 && entry.ContinuationLines.Count > 0)
                phase = entry.ContinuationLines[0].Trim();   // FDT 1.1.6x prints "during:" and the phase on the next line
            stalls.Add(new StallInfo(stallMs, stall.Groups["at"].Value, phase.Length == 0 ? "(no phase recorded)" : phase));
            return false;   // a stall is a finding; leave it visible to the other readers too
        }

        if (entry.Source != FdtSource)
            return false;

        // The detail FDT prints under a stall (which calls, which log lines, how many more) is already summed up by the stall
        // itself; left in the warning list it buried a real warning under dozens of them in one of our own perf logs.
        foreach (var prefix in StallDetailPrefixes)
        {
            if (message.StartsWith(prefix, System.StringComparison.Ordinal))
                return true;
        }
        if (FurtherStalls.IsMatch(message))
            return true;

        var frame = FrameSample.Match(message);
        if (frame.Success)
        {
            frames.Add(new FrameSampleInfo(
                ParseDouble(frame.Groups["fps"].Value),
                ParseDouble(frame.Groups["avg"].Value),
                ParseDouble(frame.Groups["worst"].Value),
                ToInt(frame.Groups["over33"].Value),
                ToInt(frame.Groups["total"].Value)));
            return true;
        }

        var render = RenderTop.Match(message);
        if (render.Success)
        {
            var draws = ToInt(render.Groups["draws"].Value);
            if (draws > peakDraws) peakDraws = draws;
            return true;
        }

        var world = WorldStats.Match(message);
        if (world.Success)
        {
            Keep(ref peakZdo, world.Groups["zdo"].Value);
            Keep(ref peakInstances, world.Groups["inst"].Value);
            Keep(ref peakZones, world.Groups["zones"].Value);
            return true;
        }

        var window = TimingWindow.Match(message);
        if (window.Success)
        {
            timingWindowSeconds = ParseDouble(window.Groups["seconds"].Value);
            return true;
        }

        var row = TimingRow.Match(message);
        if (row.Success)
        {
            var label = row.Groups["label"].Value.Trim();
            // The header row of the table matches the same shape as a reading; skip it by name.
            if (label != "label")
            {
                if (!timings.TryGetValue(label, out var acc))
                    timings[label] = acc = new TimingAccumulator();
                acc.Add(timingWindowSeconds > 0d
                    ? ToInt(row.Groups["calls"].Value) * ParseDouble(row.Groups["avg"].Value) / timingWindowSeconds
                    : ParseDouble(row.Groups["msPerSec"].Value));
            }
            return true;
        }

        return false;
    }

    public RunPerformance ToRunPerformance()
    {
        if (frames.Count == 0 && timings.Count == 0 && stalls.Count == 0)
            return null;   // nothing measured; the report omits the section entirely

        return new RunPerformance(frames, timings, stalls, peakDraws, peakZdo, peakInstances, peakZones);
    }

    private static void Keep(ref int best, string raw)
    {
        if (LoggedNumbers.TryParseInt(raw.Replace(",", string.Empty), out var value) && value > best)
            best = value;
    }

    private static int ToInt(string raw) => LoggedNumbers.TryParseInt(raw.Replace(",", string.Empty), out var v) ? v : 0;

    private static double ParseDouble(string raw) =>
        double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0d;

    internal sealed class TimingAccumulator
    {
        public double TotalMsPerSecond;
        public int Samples;
        public double Worst;

        public void Add(double msPerSecond)
        {
            TotalMsPerSecond += msPerSecond;
            Samples++;
            if (msPerSecond > Worst) Worst = msPerSecond;
        }

        public double Average => Samples == 0 ? 0d : TotalMsPerSecond / Samples;
    }
}

internal readonly struct FrameSampleInfo
{
    public FrameSampleInfo(double fps, double averageMs, double worstMs, int over33, int total)
    {
        Fps = fps; AverageMs = averageMs; WorstMs = worstMs; FramesOver33Ms = over33; TotalFrames = total;
    }

    public double Fps { get; }
    public double AverageMs { get; }
    public double WorstMs { get; }
    public int FramesOver33Ms { get; }
    public int TotalFrames { get; }
}

internal readonly struct StallInfo
{
    public StallInfo(int milliseconds, string atSeconds, string phase)
    {
        Milliseconds = milliseconds; AtSeconds = atSeconds; Phase = phase;
    }

    public int Milliseconds { get; }
    public string AtSeconds { get; }
    public string Phase { get; }
}
