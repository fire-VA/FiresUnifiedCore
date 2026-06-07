using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Async
{
    public enum WorkPriority
    {
        Critical = 0,
        High = 1,
        Normal = 2,
        Low = 3,
    }

    // Per-frame time-budget scheduler. All heavy main-thread mod work routes
    // through Schedule/ScheduleCoroutine so the mod never exceeds its frame
    // budget regardless of how many systems are active. Four priority lanes
    // drained Critical -> High -> Normal -> Low; at least one item runs
    // every frame so a single oversized item can't starve forward progress.
    public class FrameBudgetScheduler : MonoBehaviour
    {
        private const int LaneCount = 4;
        private const float DefaultMaxBudgetMs = 5f;
        private const string SchedulerHostName = "FiresUnifiedCore_FrameBudgetScheduler";
        private const string LogPrefix = "[FrameBudgetScheduler]";

        public static float MaxBudgetMs = DefaultMaxBudgetMs;

        private static FrameBudgetScheduler _instance;
        private static readonly object _queueLock = new object();

        private static readonly Queue<WorkItem>[] s_incomingQueues = BuildIncomingQueues();

        private static float _lastFrameMs;
        private static bool _budgetExceeded;
        private static int _pendingCount;

        private readonly List<WorkItem>[] _localQueues = BuildLocalQueues();
        private readonly int[] _localIndex = new int[LaneCount];
        private readonly Stopwatch _frameStopwatch = new Stopwatch();

        public static int PendingCount => _pendingCount;
        public static float LastFrameMs => _lastFrameMs;
        public static bool BudgetExceededLastFrame => _budgetExceeded;

        public static void Schedule(WorkPriority priority, Action action)
        {
            if (action == null) return;

            int lane = ClampLane(priority);
            lock (_queueLock)
            {
                s_incomingQueues[lane].Enqueue(new WorkItem { Action = action });
            }
            EnsureInstance();
        }

        public static void ScheduleSequence(WorkPriority priority, Action[] steps)
        {
            if (steps == null || steps.Length == 0) return;
            foreach (var step in steps)
            {
                if (step != null) Schedule(priority, step);
            }
        }

        public static void ScheduleCoroutine(WorkPriority priority, IEnumerator steps, Action onComplete = null)
        {
            if (steps == null) return;

            int lane = ClampLane(priority);
            lock (_queueLock)
            {
                s_incomingQueues[lane].Enqueue(new WorkItem
                {
                    Coroutine = steps,
                    OnComplete = onComplete,
                });
            }
            EnsureInstance();
        }

        private void Update()
        {
            DrainIncomingQueues();

            int pending = CountPending();
            if (pending == 0)
            {
                ResetLocalQueues();
                _pendingCount = 0;
                _lastFrameMs = 0f;
                _budgetExceeded = false;
                return;
            }

            RunWithinBudget();
            _pendingCount = CountPending();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void DrainIncomingQueues()
        {
            lock (_queueLock)
            {
                for (int lane = 0; lane < LaneCount; lane++)
                {
                    var incoming = s_incomingQueues[lane];
                    while (incoming.Count > 0)
                        _localQueues[lane].Add(incoming.Dequeue());
                }
            }
        }

        private int CountPending()
        {
            int total = 0;
            for (int lane = 0; lane < LaneCount; lane++)
                total += _localQueues[lane].Count - _localIndex[lane];
            return total;
        }

        private void ResetLocalQueues()
        {
            for (int lane = 0; lane < LaneCount; lane++)
            {
                _localQueues[lane].Clear();
                _localIndex[lane] = 0;
            }
        }

        private void RunWithinBudget()
        {
            _frameStopwatch.Reset();
            _frameStopwatch.Start();

            float budgetMs = MaxBudgetMs;
            bool processedFirst = false;
            bool budgetExhausted = false;

            for (int lane = 0; lane < LaneCount && !budgetExhausted; lane++)
                budgetExhausted = RunLane(lane, ref processedFirst, budgetMs);

            _frameStopwatch.Stop();
            _lastFrameMs = (float)_frameStopwatch.Elapsed.TotalMilliseconds;
            _budgetExceeded = budgetExhausted;
        }

        private bool RunLane(int lane, ref bool processedFirst, float budgetMs)
        {
            var list = _localQueues[lane];
            ref int idx = ref _localIndex[lane];
            bool budgetExhausted = false;

            while (idx < list.Count)
            {
                if (processedFirst && _frameStopwatch.Elapsed.TotalMilliseconds >= budgetMs)
                {
                    budgetExhausted = true;
                    break;
                }

                var item = list[idx];
                processedFirst = true;

                if (item.Coroutine != null)
                {
                    if (AdvanceCoroutineOneStep(ref idx, item))
                        break;
                    continue;
                }

                if (item.Action != null)
                {
                    idx++;
                    InvokeAction(item.Action);
                    continue;
                }

                idx++;
            }

            if (idx >= list.Count)
            {
                list.Clear();
                idx = 0;
            }

            return budgetExhausted;
        }

        // Returns true when the coroutine yielded but has more work — caller
        // breaks out so we respect the yield boundary (one step per frame).
        // Returns false when the coroutine completed or threw — caller
        // advances and runs the next item.
        private static bool AdvanceCoroutineOneStep(ref int idx, WorkItem item)
        {
            bool hasMore = false;
            try { hasMore = item.Coroutine.MoveNext(); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} Coroutine step failed: {ex.Message}");
            }

            if (hasMore) return true;

            idx++;
            InvokeOnComplete(item.OnComplete);
            return false;
        }

        private static void InvokeAction(Action action)
        {
            try { action.Invoke(); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} Action failed: {ex.Message}");
            }
        }

        private static void InvokeOnComplete(Action onComplete)
        {
            if (onComplete == null) return;
            try { onComplete.Invoke(); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} OnComplete failed: {ex.Message}");
            }
        }

        private static int ClampLane(WorkPriority priority)
        {
            int lane = (int)priority;
            if (lane < 0 || lane >= LaneCount) return (int)WorkPriority.Normal;
            return lane;
        }

        private static void EnsureInstance()
        {
            if (_instance != null) return;

            var go = new GameObject(SchedulerHostName);
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<FrameBudgetScheduler>();
        }

        private static Queue<WorkItem>[] BuildIncomingQueues()
        {
            var queues = new Queue<WorkItem>[LaneCount];
            for (int i = 0; i < LaneCount; i++) queues[i] = new Queue<WorkItem>();
            return queues;
        }

        private static List<WorkItem>[] BuildLocalQueues()
        {
            var queues = new List<WorkItem>[LaneCount];
            for (int i = 0; i < LaneCount; i++) queues[i] = new List<WorkItem>();
            return queues;
        }

        private struct WorkItem
        {
            public Action Action;
            public IEnumerator Coroutine;
            public Action OnComplete;
        }
    }
}
