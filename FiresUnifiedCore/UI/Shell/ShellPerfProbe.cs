// Fires UI Shell perf probe: measurement only.
// While the hub is open, every 5 s at Info: the frame median / p95, Unity's canvas rebuild (Canvas.SendWillRenderCanvases runs the
// layout, graphic and TMP rebuilds of every dirty canvas) avg / max, the hub tick, the minimap tick, the config driver, the number of
// graphics under the hub canvas and the active tab. Nothing here changes behaviour.
using System;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI.Shell
{
    internal static class ShellPerfProbe
    {
        internal enum Slot { CanvasRebuild = 0, HubTick = 1, MinimapTick = 2, ConfigDriver = 3, HubLateTick = 4, ChatTick = 5 }
        private const int Slots = 6;

        internal static bool Active;   // true only while the hub is open
        private static readonly long[] s_ticks = new long[Slots], s_max = new long[Slots];
        private static readonly int[] s_calls = new int[Slots];
        private static readonly float[] s_frames = new float[1024];
        private static int s_frameCount;
        private static float s_windowStart = -1f;

        internal static long Now() => Stopwatch.GetTimestamp();

        internal static void Add(Slot s, long start)
        {
            if (!Active) return;
            long d = Stopwatch.GetTimestamp() - start;
            int i = (int)s;
            s_ticks[i] += d; s_calls[i]++;
            if (d > s_max[i]) s_max[i] = d;
        }

        // A hub-CLOSED baseline, to tell shell costs from the game's own frame time. The last ~5 s
        // before the hub opens and the first 5 s after it closes are logged the same way, plus the GPU frame time when Unity's
        // FrameTimingManager has it, and TestSkyAB() pauses the sky backdrop render for one window (A/B of its GPU cost).
        private static readonly float[] s_closed = new float[600];
        private static int s_closedCount, s_closedHead;
        private static float s_afterCloseUntil = -1f;
        private static readonly System.Collections.Generic.List<float> s_after = new System.Collections.Generic.List<float>(600);
        private static readonly FrameTiming[] s_ft = new FrameTiming[1];
        private static double s_gpuSum; private static int s_gpuN;
        internal static bool SkyPaused;
        private static bool s_skyAbPending;

        /// <summary>Test hook: the next 5 s window renders the hub WITHOUT the sky backdrop camera (A/B), logged "sky paused".</summary>
        public static void TestSkyAB() { s_skyAbPending = true; ShellLog.Info("perf: sky A/B requested (next window without the sky render)"); }

        private static void SampleGpu()
        {
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, s_ft) > 0 && s_ft[0].gpuFrameTime > 0) { s_gpuSum += s_ft[0].gpuFrameTime; s_gpuN++; }
            }
            catch { }
        }

        private static string Gpu() => s_gpuN > 0 ? (s_gpuSum / s_gpuN).ToString("0.0") + " ms" : "n/a (frame timing stats off)";

        private static (float med, float p95, int n) Stats(float[] a, int n)
        {
            if (n <= 0) return (0f, 0f, 0);
            var s = new float[n]; Array.Copy(a, s, n); Array.Sort(s);
            return (s[n / 2], s[Mathf.Min(n - 1, (int)(n * 0.95f))], n);
        }

        // Called once per frame from ShellRuntime.LateUpdate (hub open or not; the closed path only fills a small ring).
        internal static void Frame(ShellHub hub)
        {
            bool open = hub != null && hub.IsOpen;
            float ms = Time.unscaledDeltaTime * 1000f;
            if (!open)
            {
                if (Active)
                {
                    Reset(); Active = false; s_windowStart = -1f; SkyPaused = false;
                    s_afterCloseUntil = Time.unscaledTime + 5f; s_after.Clear(); s_gpuSum = 0; s_gpuN = 0;
                }
                s_closed[s_closedHead] = ms; s_closedHead = (s_closedHead + 1) % s_closed.Length; if (s_closedCount < s_closed.Length) s_closedCount++;
                if (s_afterCloseUntil > 0f)
                {
                    s_after.Add(ms); SampleGpu();
                    if (Time.unscaledTime >= s_afterCloseUntil)
                    {
                        var arr = s_after.ToArray(); var st = Stats(arr, arr.Length);
                        ShellLog.Info($"perf: AFTER hub close (baseline), {st.n} frames, frame median {st.med:0.0} ms / p95 {st.p95:0.0} ms, GPU {Gpu()}");
                        s_afterCloseUntil = -1f; s_gpuSum = 0; s_gpuN = 0;
                    }
                }
                return;
            }
            if (!Active)
            {
                // the ~5 s just before it opened (hub closed)
                int take = 0; float acc = 0f; var before = new System.Collections.Generic.List<float>();
                for (int i = 1; i <= s_closedCount && acc < 5000f; i++) { float f = s_closed[(s_closedHead - i + s_closed.Length) % s_closed.Length]; before.Add(f); acc += f; take++; }
                var b = before.ToArray(); var bs = Stats(b, b.Length);
                if (bs.n > 10) ShellLog.Info($"perf: BEFORE hub open (baseline), {bs.n} frames, frame median {bs.med:0.0} ms / p95 {bs.p95:0.0} ms");
                s_gpuSum = 0; s_gpuN = 0;
            }
            Active = true;
            SampleGpu();
            float now = Time.unscaledTime;
            if (s_windowStart < 0f) { Reset(); s_windowStart = now; SkyPaused = s_skyAbPending; s_skyAbPending = false; return; }
            if (s_frameCount < s_frames.Length) s_frames[s_frameCount++] = ms;
            if (now - s_windowStart < 5f) return;

            int n = s_frameCount;
            float med = 0f, p95 = 0f;
            if (n > 0)
            {
                var sorted = new float[n];
                Array.Copy(s_frames, sorted, n);
                Array.Sort(sorted);
                med = sorted[n / 2];
                p95 = sorted[Mathf.Min(n - 1, (int)(n * 0.95f))];
            }
            int graphics = 0;
            var canvas = hub.HubCanvas;
            if (canvas != null) graphics = canvas.GetComponentsInChildren<Graphic>(false).Length;
            ShellLog.Info($"perf: tab '{hub.ActiveModuleId}'{(SkyPaused ? " (SKY PAUSED)" : "")}, {n} frames, frame median {med:0.0} ms / p95 {p95:0.0} ms, GPU {Gpu()}; "
                + $"canvas rebuild {Avg(Slot.CanvasRebuild)} (all canvases); hub tick {Avg(Slot.HubTick)}; hub late {Avg(Slot.HubLateTick)}; "
                + $"minimap {Avg(Slot.MinimapTick)}; chat {Avg(Slot.ChatTick)}; config driver {Avg(Slot.ConfigDriver)}; "
                + $"{graphics} active graphics under the hub canvas");
            Reset();
            s_windowStart = now;
            s_gpuSum = 0; s_gpuN = 0;
            SkyPaused = s_skyAbPending; s_skyAbPending = false;   // an A/B request applies to the next whole window
        }

        private static string Avg(Slot s)
        {
            int i = (int)s;
            if (s_calls[i] == 0) return "-";
            double ms = 1000.0 / Stopwatch.Frequency;
            return $"avg {s_ticks[i] * ms / s_calls[i]:0.00} / max {s_max[i] * ms:0.0} ms x{s_calls[i]}";
        }

        private static void Reset()
        {
            Array.Clear(s_ticks, 0, Slots); Array.Clear(s_max, 0, Slots); Array.Clear(s_calls, 0, Slots);
            s_frameCount = 0;
        }
    }

    // Unity's per-frame canvas update (layout + graphic + TMP rebuilds of every dirty canvas). Measurement only, and only while the
    // hub is open: the prefix is a bool check otherwise.
    [HarmonyPatch(typeof(Canvas), "SendWillRenderCanvases")]
    internal static class ShellPerfCanvasPatch
    {
        private static long s_start;
        [HarmonyPrefix] private static void Prefix() { if (ShellPerfProbe.Active) s_start = ShellPerfProbe.Now(); }
        [HarmonyPostfix] private static void Postfix() { if (ShellPerfProbe.Active && s_start != 0) { ShellPerfProbe.Add(ShellPerfProbe.Slot.CanvasRebuild, s_start); s_start = 0; } }
    }
}
