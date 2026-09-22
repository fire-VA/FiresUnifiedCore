using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BepInEx.Logging;

namespace FiresCore.Diagnostics
{
    // Per-second profiler for Harmony-probed methods plus a frame-time histogram, main thread only. Probes call
    // Record(label, elapsedTicks) from a prefix/postfix pair, and a once-a-second coroutine calls DumpAndReset to
    // log the top methods by time spent in the last window.
    public static class FrameTimer
    {
        private const int MaxLabelDisplayWidth = 50;
        private const float FrameWarningMs = 16f;
        private const float FrameSevereMs = 33f;

        // Master gate. When false, probes short-circuit in their Prefix
        // (returning 0 from GetTimestamp() helpers) and Postfix bails
        // before recording — total per-probe overhead ~5ns. Toggled live
        // via consumer mod's config so probes can stay loaded with zero
        // observable cost during normal play.
        public static bool Enabled = true;

        private static readonly Dictionary<string, MethodAccumulator> Stats
            = new Dictionary<string, MethodAccumulator>(64);

        private static readonly List<float> FrameDeltas = new List<float>(256);

        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

        private sealed class MethodAccumulator
        {
            public long TotalTicks;
            public int  CallCount;
            public long MaxTicksOneCall;
        }

        // ── Non-destructive snapshot of the last completed window ────────────
        // DumpAndReset / Rotate clear Stats every window. Before clearing they
        // copy the sorted results here so a live reader (the on-screen overlay)
        // can show a stable ranking that refreshes once per window, instead of
        // reading the mid-window accumulator and sawtoothing to zero on each
        // clear. The whole list reference is swapped atomically, so a consumer
        // iterating LastWindow always sees a consistent immutable snapshot.
        public readonly struct LabelTiming
        {
            public readonly string Label;
            public readonly double TotalMs;   // ms spent under this label in the window
            public readonly int    Calls;
            public readonly double AvgMs;
            public readonly double MaxMs;     // worst single call in the window

            public LabelTiming(string label, double totalMs, int calls, double avgMs, double maxMs)
            {
                Label = label; TotalMs = totalMs; Calls = calls; AvgMs = avgMs; MaxMs = maxMs;
            }
        }

        private static List<LabelTiming> _lastWindow = new List<LabelTiming>();

        // Sorted (descending by TotalMs) snapshot of the most recently completed
        // window. Refreshed by DumpAndReset/Rotate; never cleared mid-read.
        public static IReadOnlyList<LabelTiming> LastWindow => _lastWindow;

        // Duration (seconds) the LastWindow snapshot covers — divide a row's
        // TotalMs by this to get ms/sec.
        public static double LastWindowSeconds { get; private set; } = 1.0;

        public static void Record(string label, long elapsedTicks)
        {
            if (!Stats.TryGetValue(label, out var acc))
            {
                acc = new MethodAccumulator();
                Stats[label] = acc;
            }
            acc.TotalTicks += elapsedTicks;
            acc.CallCount++;
            if (elapsedTicks > acc.MaxTicksOneCall) acc.MaxTicksOneCall = elapsedTicks;
        }

        public static void RecordFrame(float deltaTime)
        {
            if (!Enabled) return;
            FrameDeltas.Add(deltaTime);
        }

        // Emits the report and clears accumulators. Safe to call with no
        // recorded samples — emits an "idle" line so silence in the log
        // unambiguously means the dumper itself stopped.
        public static void DumpAndReset(ManualLogSource log, int topN, double windowSeconds)
        {
            int frames = FrameDeltas.Count;
            log.LogMessage($"=== Frame & method timings (last {windowSeconds:F2}s) ===");

            if (frames == 0 && Stats.Count == 0)
            {
                if (!Enabled)
                    log.LogMessage("  (probes disabled via 'Enable Probes' — toggle on to resume capture)");
                else
                    log.LogMessage("  (no samples in window — game paused, in menu, or probes not loaded)");
                RefreshLastWindow(windowSeconds);   // publish an empty snapshot to live readers
                return;
            }

            if (frames > 0) EmitFrameStats(log, frames);

            RefreshLastWindow(windowSeconds);
            if (_lastWindow.Count > 0) EmitMethodStats(log, topN);

            Stats.Clear();
            FrameDeltas.Clear();
        }

        // Publish the snapshot + clear WITHOUT logging. Lets the consumer mod
        // keep the live overlay ranking fresh on its dump cadence even when log
        // dumping is turned off.
        public static void Rotate(double windowSeconds)
        {
            RefreshLastWindow(windowSeconds);
            Stats.Clear();
            FrameDeltas.Clear();
        }

        // Rebuild LastWindow from the current accumulators, sorted descending by
        // total time. Called immediately before a Clear so it captures the full
        // completed window. Swaps the whole list reference — never mutates the
        // list a reader may be holding.
        private static void RefreshLastWindow(double windowSeconds)
        {
            var snapshot = new List<LabelTiming>(Stats.Count);
            foreach (var kv in Stats)
            {
                double totalMs = kv.Value.TotalTicks      * TicksToMs;
                double maxMs   = kv.Value.MaxTicksOneCall * TicksToMs;
                double avgMs   = kv.Value.CallCount > 0 ? totalMs / kv.Value.CallCount : 0.0;
                snapshot.Add(new LabelTiming(kv.Key, totalMs, kv.Value.CallCount, avgMs, maxMs));
            }
            snapshot.Sort((a, b) => b.TotalMs.CompareTo(a.TotalMs));
            _lastWindow = snapshot;
            LastWindowSeconds = windowSeconds > 0.0 ? windowSeconds : 1.0;
        }

        // The live accumulators, ranked by WORST SINGLE CALL rather than total.
        // A stall is one long call, not a lot of short ones, so totals rank the
        // busy systems while MaxMs ranks the system that actually froze the
        // frame. Reads the current window without rotating it, because a stall
        // is reported mid-window and the evidence would otherwise be cleared
        // before anyone could ask for it. New method, not a changed signature —
        // sibling mods are compiled against the old Core.
        public static IReadOnlyList<LabelTiming> WorstSingleCalls(int topN)
        {
            var snapshot = new List<LabelTiming>(Stats.Count);
            foreach (var kv in Stats)
            {
                double totalMs = kv.Value.TotalTicks * TicksToMs;
                double maxMs = kv.Value.MaxTicksOneCall * TicksToMs;
                double avgMs = kv.Value.CallCount > 0 ? totalMs / kv.Value.CallCount : 0.0;
                snapshot.Add(new LabelTiming(kv.Key, totalMs, kv.Value.CallCount, avgMs, maxMs));
            }
            snapshot.Sort((a, b) => b.MaxMs.CompareTo(a.MaxMs));
            if (topN > 0 && snapshot.Count > topN) snapshot.RemoveRange(topN, snapshot.Count - topN);
            return snapshot;
        }

        public static void Clear()
        {
            Stats.Clear();
            FrameDeltas.Clear();
        }

        private static void EmitFrameStats(ManualLogSource log, int frames)
        {
            float total = 0f, worst = 0f, best = float.MaxValue;
            int overSevere = 0, overWarning = 0;

            for (int i = 0; i < frames; i++)
            {
                float delta = FrameDeltas[i];
                total += delta;
                if (delta > worst) worst = delta;
                if (delta < best)  best  = delta;
                if (delta * 1000f > FrameSevereMs) overSevere++;
                if (delta * 1000f > FrameWarningMs) overWarning++;
            }

            float avgMs = (total / frames) * 1000f;
            float fps   = frames / total;
            log.LogMessage(
                $"Frames: {fps,6:F1} fps  avg={avgMs,6:F1}ms  worst={worst*1000f,6:F1}ms  " +
                $"best={best*1000f,6:F1}ms  >{FrameSevereMs:F0}ms={overSevere}/{frames}  >{FrameWarningMs:F0}ms={overWarning}/{frames}");
        }

        // Logs the top-N rows of the freshly-built LastWindow snapshot. Format
        // is unchanged; the sort now lives in RefreshLastWindow so the log and
        // the live overlay read the identical ranking.
        private static void EmitMethodStats(ManualLogSource log, int topN)
        {
            log.LogMessage(
                $"  {"label",-50}  {"ms/sec",10}  {"calls",8}  {"avg(ms)",10}  {"max(ms)",10}");

            int count = topN < _lastWindow.Count ? topN : _lastWindow.Count;
            for (int i = 0; i < count; i++)
            {
                LabelTiming timing = _lastWindow[i];
                string label = timing.Label.Length > MaxLabelDisplayWidth
                    ? timing.Label.Substring(0, MaxLabelDisplayWidth)
                    : timing.Label;
                log.LogMessage(
                    $"  {label,-50}  {timing.TotalMs,10:F2}  {timing.Calls,8}  {timing.AvgMs,10:F3}  {timing.MaxMs,10:F2}");
            }
        }
    }
}
