using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Async
{
    // Dispatches actions from background threads onto the Unity main thread.
    // Update() pulls from the thread-safe queue and runs actions until a
    // per-frame time budget elapses; overflow carries into the next frame
    // so a slow action can't stall ZNet heartbeats and cause disconnects.
    // StartRoutine bypasses the budget for callers that need true
    // frame-by-frame yield (e.g. chunked network sends).
    public class MainThreadDispatcher : MonoBehaviour
    {
        private const string DispatcherHostName = "FiresUnifiedCore_MainThreadDispatcher";
        private const string LogPrefix = "[MainThreadDispatcher]";
        private const float DefaultMaxBudgetMs = 4f;

        public static float MaxBudgetMs = DefaultMaxBudgetMs;

        private static MainThreadDispatcher _instance;
        private static readonly Queue<Action> _actionQueue = new Queue<Action>();
        private static readonly object _queueLock = new object();

        private readonly List<Action> _processingList = new List<Action>();
        private readonly Stopwatch _frameStopwatch = new Stopwatch();
        private int _processingIndex;

        public static int PendingCount
        {
            get
            {
                lock (_queueLock)
                {
                    int queued = _actionQueue.Count;
                    if (_instance != null)
                        queued += _instance._processingList.Count - _instance._processingIndex;
                    return queued;
                }
            }
        }

        public static void Enqueue(Action action)
        {
            if (action == null) return;

            lock (_queueLock)
            {
                _actionQueue.Enqueue(action);
            }
            EnsureInstance();
        }

        public static Coroutine StartRoutine(IEnumerator routine)
        {
            if (routine == null) return null;
            EnsureInstance();
            return _instance.StartCoroutine(routine);
        }

        private void Update()
        {
            DrainQueueIntoProcessingList();

            if (_processingIndex >= _processingList.Count)
            {
                ResetProcessingState();
                return;
            }

            RunActionsUntilBudgetExhausted();

            if (_processingIndex >= _processingList.Count)
                ResetProcessingState();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void DrainQueueIntoProcessingList()
        {
            lock (_queueLock)
            {
                while (_actionQueue.Count > 0)
                    _processingList.Add(_actionQueue.Dequeue());
            }
        }

        // Always runs at least one action per frame so a single slow action
        // can't permanently starve forward progress.
        private void RunActionsUntilBudgetExhausted()
        {
            _frameStopwatch.Reset();
            _frameStopwatch.Start();

            float budgetMs = MaxBudgetMs;
            bool processedFirst = false;

            while (_processingIndex < _processingList.Count)
            {
                if (processedFirst && _frameStopwatch.Elapsed.TotalMilliseconds >= budgetMs)
                    break;

                var action = _processingList[_processingIndex++];
                processedFirst = true;
                InvokeAction(action);
            }

            _frameStopwatch.Stop();
        }

        private static void InvokeAction(Action action)
        {
            try { action?.Invoke(); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} Action failed: {ex.Message}");
            }
        }

        private void ResetProcessingState()
        {
            _processingList.Clear();
            _processingIndex = 0;
        }

        private static void EnsureInstance()
        {
            if (_instance != null) return;

            var go = new GameObject(DispatcherHostName);
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<MainThreadDispatcher>();
        }
    }
}
