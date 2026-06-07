using System;
using System.Collections.Generic;
using System.Threading;
using FiresCore.Logging;

namespace FiresCore.Async
{
    public delegate void AsyncJobCallback<TOutput>(TOutput output);

    public interface IAsyncWorker
    {
        void Shutdown();
        bool HasPendingJobs { get; }
        string Name { get; }
    }

    // Single-thread background worker that maps TInput -> TOutput. Process
    // function runs on a dedicated thread and MUST NOT touch Unity API;
    // completion callbacks dispatch through FrameBudgetScheduler so they
    // share the same per-frame budget as the rest of the mod's main-thread
    // work (avoids the dual-budget problem where independent dispatchers
    // each spent up to ~5ms/frame).
    public class BackgroundWorker<TInput, TOutput> : IAsyncWorker
        where TInput : class
        where TOutput : class, new()
    {
        private const string ThreadNamePrefix = "FUC_";
        private const int ShutdownJoinTimeoutMs = 1000;

        private readonly string _name;
        private readonly Action<TInput, TOutput> _processFunc;
        private readonly Queue<PendingJob> _pendingJobs = new Queue<PendingJob>();
        private readonly object _jobLock = new object();

        private Thread _workerThread;
        private volatile bool _shutdownRequested;
        private int _activeJobCount;

        public string Name => _name;
        public bool HasPendingJobs => _activeJobCount > 0;

        public BackgroundWorker(string name, Action<TInput, TOutput> processFunc)
        {
            _name = name ?? "BackgroundWorker";
            _processFunc = processFunc ?? throw new ArgumentNullException(nameof(processFunc));
            AsyncShutdownManager.Register(this);
        }

        public void QueueJob(TInput input, AsyncJobCallback<TOutput> callback)
        {
            if (input == null || callback == null) return;

            EnsureWorkerThread();

            var job = new PendingJob
            {
                Input = input,
                Output = new TOutput(),
                Callback = callback,
            };

            lock (_jobLock)
            {
                _pendingJobs.Enqueue(job);
                Interlocked.Increment(ref _activeJobCount);
                Monitor.Pulse(_jobLock);
            }
        }

        public void Shutdown()
        {
            _shutdownRequested = true;

            lock (_jobLock)
            {
                Monitor.PulseAll(_jobLock);
            }

            JoinWorkerThread();
            ClearStateAfterShutdown();
            AsyncShutdownManager.Unregister(this);
        }

        private void JoinWorkerThread()
        {
            if (_workerThread != null && _workerThread.IsAlive)
                _workerThread.Join(ShutdownJoinTimeoutMs);
        }

        private void ClearStateAfterShutdown()
        {
            _workerThread = null;
            _shutdownRequested = false;
            _activeJobCount = 0;

            lock (_jobLock)
            {
                _pendingJobs.Clear();
            }
        }

        private void EnsureWorkerThread()
        {
            if (_workerThread != null && _workerThread.IsAlive) return;

            _shutdownRequested = false;
            _workerThread = new Thread(WorkerLoop)
            {
                Name = ThreadNamePrefix + _name,
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
            };
            _workerThread.Start();
        }

        private void WorkerLoop()
        {
            while (!_shutdownRequested)
            {
                PendingJob job = WaitForNextJob();
                if (job == null) break;
                ProcessJobAndDispatchCallback(job);
            }
        }

        private PendingJob WaitForNextJob()
        {
            lock (_jobLock)
            {
                while (_pendingJobs.Count == 0 && !_shutdownRequested)
                    Monitor.Wait(_jobLock);

                if (_shutdownRequested) return null;
                return _pendingJobs.Count > 0 ? _pendingJobs.Dequeue() : null;
            }
        }

        private void ProcessJobAndDispatchCallback(PendingJob job)
        {
            try
            {
                _processFunc(job.Input, job.Output);
                ScheduleCallbackOnMainThread(job.Callback, job.Output);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[{_name}] Job failed: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                Interlocked.Decrement(ref _activeJobCount);
            }
        }

        private void ScheduleCallbackOnMainThread(AsyncJobCallback<TOutput> callback, TOutput output)
        {
            FrameBudgetScheduler.Schedule(WorkPriority.High, () =>
            {
                try { callback(output); }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"[{_name}] Callback failed: {ex.Message}");
                }
            });
        }

        private class PendingJob
        {
            public TInput Input;
            public TOutput Output;
            public AsyncJobCallback<TOutput> Callback;
        }
    }
}
