using System.Collections.Generic;

namespace FiresLogAnalysis;

/// <summary>How the run PERFORMED, read from FiresDebugginTools' own diagnostics. Null when nothing was measured.</summary>
public sealed class RunPerformance
{
    internal RunPerformance(
        List<FrameSampleInfo> frames,
        Dictionary<string, RunPerformanceReader.TimingAccumulator> timings,
        List<StallInfo> stalls,
        int peakDraws,
        int peakZdos,
        int peakInstances,
        int peakZones)
    {
        PeakDraws = peakDraws;
        PeakZdos = peakZdos;
        PeakInstances = peakInstances;
        PeakZones = peakZones;
        SampleCount = frames.Count;
        StallCount = stalls.Count;

        if (frames.Count > 0)
        {
            double fpsSum = 0d;
            WorstFrameMs = 0d;
            SlowestSampleFps = double.MaxValue;
            foreach (var f in frames)
            {
                fpsSum += f.Fps;
                if (f.WorstMs > WorstFrameMs) WorstFrameMs = f.WorstMs;
                if (f.Fps < SlowestSampleFps) SlowestSampleFps = f.Fps;
                if (f.Fps > FastestSampleFps) FastestSampleFps = f.Fps;
                FramesMeasured += f.TotalFrames;
                FramesOver33Ms += f.FramesOver33Ms;
            }
            AverageFps = fpsSum / frames.Count;
        }
        else
        {
            SlowestSampleFps = 0d;
        }

        var ordered = new List<KeyValuePair<string, RunPerformanceReader.TimingAccumulator>>(timings);
        ordered.Sort((a, b) => b.Value.Average.CompareTo(a.Value.Average));
        var top = new List<TimingSummary>();
        for (var i = 0; i < ordered.Count && i < 12; i++)
            top.Add(new TimingSummary(ordered[i].Key, ordered[i].Value.Average, ordered[i].Value.Worst, ordered[i].Value.Samples));
        TopCosts = top;

        stalls.Sort((a, b) => b.Milliseconds.CompareTo(a.Milliseconds));
        var worstStalls = new List<StallSummary>();
        for (var i = 0; i < stalls.Count && i < 5; i++)
            worstStalls.Add(new StallSummary(stalls[i].Milliseconds, stalls[i].AtSeconds, stalls[i].Phase));
        WorstStalls = worstStalls;
    }

    public double AverageFps { get; }
    public double SlowestSampleFps { get; }
    public double FastestSampleFps { get; }
    public double WorstFrameMs { get; }
    public int FramesMeasured { get; }
    public int FramesOver33Ms { get; }
    public int SampleCount { get; }
    public int StallCount { get; }
    public int PeakDraws { get; }
    public int PeakZdos { get; }
    public int PeakInstances { get; }
    public int PeakZones { get; }
    public IReadOnlyList<TimingSummary> TopCosts { get; }
    public IReadOnlyList<StallSummary> WorstStalls { get; }

    /// <summary>
    /// True when the frame samples are worth reporting. A handful of samples taken during loading
    /// describes the loading screen, not the run, and reporting those as "average fps" is how a
    /// measurement taken too early turns into a conclusion.
    /// </summary>
    public bool HasUsableFrameData => SampleCount >= 3;
}

public readonly struct TimingSummary
{
    internal TimingSummary(string label, double averageMsPerSecond, double worstMsPerSecond, int samples)
    {
        Label = label; AverageMsPerSecond = averageMsPerSecond; WorstMsPerSecond = worstMsPerSecond; Samples = samples;
    }

    public string Label { get; }
    public double AverageMsPerSecond { get; }
    public double WorstMsPerSecond { get; }
    public int Samples { get; }
}

public readonly struct StallSummary
{
    internal StallSummary(int milliseconds, string atSeconds, string phase)
    {
        Milliseconds = milliseconds; AtSeconds = atSeconds; Phase = phase;
    }

    public int Milliseconds { get; }
    public string AtSeconds { get; }
    public string Phase { get; }
}
