using System;
using System.Collections.Concurrent;
using System.Threading;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Storage
{
    // Vault writes that need not hold the main thread: a join's identity upsert opened the shared LiteDB file, ensured four
    // indexes and searched it on the server's main thread, 141 ms of R20's join frame. One background thread runs the
    // writes in the order they were queued (LiteDB's Shared connection serializes them against the main thread's own vault
    // work). Drained before the vault is pointed at another world's file, when a world shuts down, and at quit, so a write
    // never lands in the wrong world or gets lost.
    public static class VaultWriter
    {
        private const int DrainTimeoutMs = 10000;
        private const string ThreadName = "FiresCore.VaultWriter";

        private static readonly BlockingCollection<Action> s_jobs = new BlockingCollection<Action>();
        private static readonly object s_startLock = new object();
        private static Thread s_thread;

        public static void Enqueue(Action write)
        {
            if (write == null) return;
            EnsureThread();
            try { s_jobs.Add(write); }
            catch (InvalidOperationException) { Run(write); }
        }

        // From one of the writer's own jobs a drain would wait on itself, so it returns at once there.
        public static void Drain()
        {
            if (s_thread == null || Thread.CurrentThread == s_thread) return;
            var done = new ManualResetEventSlim(false);
            try { s_jobs.Add(() => done.Set()); }
            catch (InvalidOperationException) { return; }
            if (!done.Wait(DrainTimeoutMs))
                FiresLogger.LogWarning($"[VaultWriter] vault writes still running after {DrainTimeoutMs / 1000} s.");
            FiresCore.Logging.RateLimitedLogHandler.FlushOffThreadLines();
        }

        // Called on the main thread (the first write comes from a main-thread hook), so the quit hook is registered there.
        private static void EnsureThread()
        {
            lock (s_startLock)
            {
                if (s_thread != null) return;
                s_thread = new Thread(Loop) { IsBackground = true, Name = ThreadName };
                s_thread.Start();
            }
            Application.quitting += Drain;
        }

        private static void Loop()
        {
            foreach (var job in s_jobs.GetConsumingEnumerable()) Run(job);
        }

        private static void Run(Action job)
        {
            try { job(); }
            catch (Exception ex) { FiresLogger.LogWarning($"[VaultWriter] vault write failed: {ex.Message}"); }
        }
    }
}
