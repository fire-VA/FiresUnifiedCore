using System.Diagnostics;
using UnityEngine;

namespace FiresCore.UI.Shell
{
    /// <summary>
    /// 0.2.955 (Fire 10-28 perf: InventoryGui.Update with the inventory open). Times Core's own InventoryGui.Update postfixes
    /// (CraftingSkin pills / chest pieces, ChestTagsPanel) and counts how often each ran its work vs skipped it as unchanged.
    /// One Debug line every 30 s, only while the inventory was open in that window. Debug only.
    /// </summary>
    internal static class PerfProbe
    {
        internal const int Pills = 0, Tags = 1;
        private static readonly string[] Names = { "CraftingSkin pills+chest", "ChestTags button+popup" };
        private static readonly long[] s_ticks = new long[2];
        private static readonly int[] s_ran = new int[2], s_skipped = new int[2];
        private static float s_windowStart = -1f;

        internal static long Start() => Stopwatch.GetTimestamp();

        internal static void Stop(int which, long t0)
        {
            s_ticks[which] += Stopwatch.GetTimestamp() - t0;
            s_ran[which]++;
            Flush();
        }

        internal static void Skip(int which) { s_skipped[which]++; Flush(); }

        private static void Flush()
        {
            float now = Time.unscaledTime;
            if (s_windowStart < 0f) { s_windowStart = now; return; }
            float span = now - s_windowStart;
            if (span < 30f) return;
            var sb = new System.Text.StringBuilder("[Perf] Core InventoryGui postfixes (ms/s over ");
            sb.Append(span.ToString("0")).Append(" s, inventory open):");
            for (int i = 0; i < Names.Length; i++)
            {
                double ms = s_ticks[i] * 1000.0 / Stopwatch.Frequency;
                sb.Append(' ').Append(Names[i]).Append(' ').Append((ms / span).ToString("0.00"))
                  .Append(" (ran ").Append(s_ran[i]).Append(", skipped unchanged ").Append(s_skipped[i]).Append(')');
                if (i < Names.Length - 1) sb.Append(';');
                s_ticks[i] = 0; s_ran[i] = 0; s_skipped[i] = 0;
            }
            FiresCore.Logging.FiresLogger.LogDebug(sb.ToString());
            s_windowStart = now;
        }
    }
}
