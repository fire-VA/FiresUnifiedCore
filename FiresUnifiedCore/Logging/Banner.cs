using System;
using System.Text;
using UnityEngine;

namespace FiresCore.Logging
{
    // Multi-color console banner writer. Consumers supply per-line Segment
    // arrays; Print queues them on ConsoleOutput so each segment renders in
    // its declared color, and falls back to plain Debug.Log when there is no
    // console to write to.
    public static class Banner
    {
        public readonly struct Segment
        {
            public readonly string Text;
            public readonly ConsoleColor Color;
            public Segment(string text, ConsoleColor color) { Text = text; Color = color; }
        }

        private const ConsoleColor ResetColor = ConsoleColor.Gray;
        private const int PlainFallbackBufferCapacity = 128;

        public static void Print(Segment[][] lines)
        {
            if (lines == null || lines.Length == 0) return;
            try
            {
                if (!TryWriteSegmented(lines))
                    WritePlainFallback(lines);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresUnifiedCore] Banner.Print threw: {ex.Message}");
                try { WritePlainFallback(lines); }
                catch (Exception fallbackEx)
                {
                    Debug.LogWarning($"[FiresUnifiedCore] Banner plain fallback threw: {fallbackEx.Message}");
                }
            }
        }

        private static bool TryWriteSegmented(Segment[][] lines)
        {
            if (!ConsoleOutput.Write(ResetColor, Environment.NewLine)) return false;

            foreach (var line in lines)
            {
                if (line != null)
                {
                    foreach (var seg in line)
                    {
                        if (!string.IsNullOrEmpty(seg.Text)) ConsoleOutput.Write(seg.Color, seg.Text);
                    }
                }
                ConsoleOutput.Write(ResetColor, Environment.NewLine);
            }
            return true;
        }

        private static void WritePlainFallback(Segment[][] lines)
        {
            var sb = new StringBuilder(PlainFallbackBufferCapacity);
            foreach (var line in lines)
            {
                if (line == null) { Debug.Log(string.Empty); continue; }
                sb.Length = 0;
                foreach (var seg in line)
                {
                    if (!string.IsNullOrEmpty(seg.Text)) sb.Append(seg.Text);
                }
                Debug.Log(sb.ToString());
            }
        }
    }
}
