using BepInEx.Logging;
using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace FiresCore.Logging
{
    // Multi-color console banner writer. Consumers supply per-line Segment
    // arrays; Print emits them through BepInEx's console stream so each
    // segment renders in its declared color, and falls back to plain
    // Debug.Log when the reflection bind isn't available.
    public static class Banner
    {
        public readonly struct Segment
        {
            public readonly string Text;
            public readonly ConsoleColor Color;
            public Segment(string text, ConsoleColor color) { Text = text; Color = color; }
        }

        private const ConsoleColor ResetColor = ConsoleColor.Gray;
        private const string BepInExConsoleManagerTypeName = "BepInEx.ConsoleManager";
        private const string ConsoleStreamPropertyName = "ConsoleStream";
        private const string SetConsoleColorMethodName = "SetConsoleColor";
        private const int PlainFallbackBufferCapacity = 128;

        private static bool s_reflectionResolved;
        private static Func<object> s_consoleStreamGetter;
        private static Action<ConsoleColor> s_setConsoleColor;

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
            EnsureReflection();
            if (s_consoleStreamGetter == null || s_setConsoleColor == null) return false;

            var stream = s_consoleStreamGetter() as TextWriter;
            if (stream == null) return false;

            try
            {
                s_setConsoleColor(ResetColor);
                stream.WriteLine();

                foreach (var line in lines)
                {
                    if (line == null || line.Length == 0)
                    {
                        stream.WriteLine();
                        continue;
                    }
                    foreach (var seg in line)
                    {
                        if (string.IsNullOrEmpty(seg.Text)) continue;
                        s_setConsoleColor(seg.Color);
                        stream.Write(seg.Text);
                    }
                    stream.WriteLine();
                }
            }
            finally
            {
                ResetConsoleColorSafely();
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

        private static void EnsureReflection()
        {
            if (s_reflectionResolved) return;
            s_reflectionResolved = true;
            try
            {
                var asm = typeof(ConsoleLogListener).Assembly;
                var consoleManagerType = asm.GetType(BepInExConsoleManagerTypeName, throwOnError: false);
                if (consoleManagerType == null) return;

                BindConsoleStreamGetter(consoleManagerType);
                BindSetConsoleColor(consoleManagerType);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresUnifiedCore] Banner reflection bind failed (plain fallback will be used): {ex.Message}");
            }
        }

        private static void BindConsoleStreamGetter(Type consoleManagerType)
        {
            var streamProp = consoleManagerType.GetProperty(ConsoleStreamPropertyName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var getMethod = streamProp?.GetGetMethod(nonPublic: true);
            if (getMethod == null) return;

            s_consoleStreamGetter = (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), getMethod);
        }

        private static void BindSetConsoleColor(Type consoleManagerType)
        {
            var setColorMethod = consoleManagerType.GetMethod(SetConsoleColorMethodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(ConsoleColor) },
                modifiers: null);
            if (setColorMethod == null) return;

            s_setConsoleColor = (Action<ConsoleColor>)Delegate.CreateDelegate(typeof(Action<ConsoleColor>), setColorMethod);
        }

        private static void ResetConsoleColorSafely()
        {
            try { s_setConsoleColor?.Invoke(ResetColor); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresUnifiedCore] Banner color reset threw: {ex.Message}");
            }
        }
    }
}
