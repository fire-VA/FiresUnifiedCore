using System;
using System.Collections.Generic;
using FiresCore.Logging;

namespace FiresCore.Async
{
    // Central registry for IAsyncWorker instances. Workers self-register in
    // their constructor and ShutdownAll tears them down in one pass on world
    // unload or application quit. Consumers rarely interact with this
    // directly — BackgroundWorker handles registration internally.
    public static class AsyncShutdownManager
    {
        private const string LogPrefix = "[AsyncShutdownManager]";

        private static readonly List<IAsyncWorker> _workers = new List<IAsyncWorker>();
        private static readonly object _lock = new object();

        public static int WorkerCount
        {
            get
            {
                lock (_lock)
                {
                    return _workers.Count;
                }
            }
        }

        public static void Register(IAsyncWorker worker)
        {
            if (worker == null) return;
            lock (_lock)
            {
                if (!_workers.Contains(worker))
                    _workers.Add(worker);
            }
        }

        public static void Unregister(IAsyncWorker worker)
        {
            if (worker == null) return;
            lock (_lock)
            {
                _workers.Remove(worker);
            }
        }

        public static void ShutdownAll()
        {
            IAsyncWorker[] snapshot = SnapshotWorkers();
            int shutdownCount = ShutdownSnapshot(snapshot);
            ClearRegistry();

            if (shutdownCount > 0)
                FiresLogger.LogInfo($"{LogPrefix} Shut down {shutdownCount} background worker(s).");
        }

        private static IAsyncWorker[] SnapshotWorkers()
        {
            lock (_lock)
            {
                return _workers.ToArray();
            }
        }

        private static int ShutdownSnapshot(IAsyncWorker[] snapshot)
        {
            int count = 0;
            foreach (var worker in snapshot)
            {
                if (worker == null) continue;
                try
                {
                    worker.Shutdown();
                    count++;
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"{LogPrefix} Failed to shutdown worker '{worker.Name}': {ex.Message}");
                }
            }
            return count;
        }

        private static void ClearRegistry()
        {
            lock (_lock)
            {
                _workers.Clear();
            }
        }
    }
}
