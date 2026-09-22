using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace FiresCore.Logging
{
    // The one writer for BepInEx's console window. Each console write is a blocking call on the console handle, and
    // one measured 878 ms on the main thread, so lines are queued here and written in order by a background thread.
    // BepInEx's own console writer is wrapped in TextWriter.Synchronized so other code writing to it directly stays
    // safe alongside the writer thread. When the game quits, whatever is still queued is written before exit.
    public static class ConsoleOutput
    {
        private const ConsoleColor ResetColor = ConsoleColor.Gray;
        private const string ConsoleManagerTypeName = "BepInEx.ConsoleManager";
        private const string WriterThreadName = "FiresConsoleWriter";

        // A console that stops accepting writes for long would otherwise grow the queue without limit. Past this many
        // waiting lines new ones skip the console; LogOutput.log keeps them.
        private const int MaxPendingLines = 50000;

        private struct PendingText
        {
            public ConsoleColor Color;
            public string Text;
        }

        private static readonly ConcurrentQueue<PendingText> Pending = new ConcurrentQueue<PendingText>();
        private static readonly AutoResetEvent WorkReady = new AutoResetEvent(false);
        private static readonly object WriteLock = new object();

        private static Func<object> _streamGetter;
        private static Action<ConsoleColor> _setColor;
        private static bool _started;
        private static int _pendingCount;
        private static int _skippedCount;

        public static bool Started => _started;

        // Binds BepInEx's console and starts the writer thread. Call once from the main thread; without a console
        // (disabled in BepInEx.cfg, or a build with no ConsoleManager) nothing starts and Write returns false.
        public static void Start()
        {
            if (_started) return;
            try
            {
                var consoleManager = typeof(BepInEx.Logging.ConsoleLogListener).Assembly.GetType(ConsoleManagerTypeName, throwOnError: false);
                if (consoleManager == null || !BindConsole(consoleManager)) return;
                if (!(_streamGetter() is TextWriter stream) || stream == TextWriter.Null) return;
                SynchronizeConsoleWriter(consoleManager);

                new Thread(WriterLoop) { IsBackground = true, Name = WriterThreadName }.Start();
                Application.quitting += WriteAllPending;
                _started = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresUnifiedCore] Console writer not started, console lines stay synchronous: {ex.Message}");
            }
        }

        // Queues text for the console in the given colour. False when the writer isn't running, so the caller writes
        // it the old way.
        public static bool Write(ConsoleColor color, string text)
        {
            if (!_started || text == null) return false;
            if (Interlocked.Increment(ref _pendingCount) > MaxPendingLines)
            {
                Interlocked.Decrement(ref _pendingCount);
                Interlocked.Increment(ref _skippedCount);
                return true;
            }
            Pending.Enqueue(new PendingText { Color = color, Text = text });
            WorkReady.Set();
            return true;
        }

        private static void WriterLoop()
        {
            while (true)
            {
                WorkReady.WaitOne();
                WriteAllPending();
            }
        }

        private static void WriteAllPending()
        {
            lock (WriteLock)
            {
                var stream = _streamGetter() as TextWriter;
                bool colourChanged = false;
                try
                {
                    ConsoleColor current = ResetColor;
                    while (Pending.TryDequeue(out var pending))
                    {
                        Interlocked.Decrement(ref _pendingCount);
                        if (stream == null) continue;
                        if (!colourChanged || pending.Color != current)
                        {
                            _setColor(pending.Color);
                            current = pending.Color;
                            colourChanged = true;
                        }
                        stream.Write(pending.Text);
                    }

                    int skipped = Interlocked.Exchange(ref _skippedCount, 0);
                    if (skipped > 0 && stream != null)
                    {
                        _setColor(ResetColor);
                        colourChanged = true;
                        stream.WriteLine($"[FiresUnifiedCore] {skipped} console line(s) skipped while the console was not accepting writes; LogOutput.log has them.");
                    }
                }
                catch
                {
                    // The console went away mid-write; the lines are still in LogOutput.log.
                }
                finally
                {
                    if (colourChanged)
                    {
                        try { _setColor(ResetColor); }
                        catch { }
                    }
                }
            }
        }

        private static bool BindConsole(Type consoleManager)
        {
            const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var streamGetter = consoleManager.GetProperty("ConsoleStream", AnyStatic)?.GetGetMethod(nonPublic: true);
            var setColor = consoleManager.GetMethod("SetConsoleColor", AnyStatic, null, new[] { typeof(ConsoleColor) }, null);
            if (streamGetter == null || setColor == null) return false;

            _streamGetter = (Func<object>)Delegate.CreateDelegate(typeof(Func<object>), streamGetter);
            _setColor = (Action<ConsoleColor>)Delegate.CreateDelegate(typeof(Action<ConsoleColor>), setColor);
            return true;
        }

        private static void SynchronizeConsoleWriter(Type consoleManager)
        {
            var driver = consoleManager.GetProperty("Driver", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            var consoleOut = driver?.GetType().GetProperty("ConsoleOut", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var setter = consoleOut?.GetSetMethod(nonPublic: true);
            if (!(consoleOut?.GetValue(driver) is TextWriter current) || setter == null) return;
            if (current == TextWriter.Null || current.GetType().Name == "SyncTextWriter") return;
            setter.Invoke(driver, new object[] { TextWriter.Synchronized(current) });
        }
    }
}
