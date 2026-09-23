using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// The server's list of companion ZDOs, gathered by one sweep of ZDOMan spread over frames (one
    /// GetAllZDOsWithPrefabIterative step per frame). The leash heartbeat, the arrival reconcile and the offline zone
    /// keeper read it instead of each sweeping the whole world in one frame, which cost about 200 ms apiece on a
    /// 3.75M-ZDO world. It holds ZDOIDs, not ZDOs, because ZDOMan recycles released ZDOs.
    /// </summary>
    public static class CompanionZdoCensus
    {
        internal static readonly string[] PrefabNames = { "CompanionNpc", "CompanionNpc_Wild", "BaseNpc" };

        private const float PassIntervalSeconds = 5f;

        private static readonly List<ZDOID> Snapshot = new List<ZDOID>();
        private static readonly HashSet<ZDOID> Gathering = new HashSet<ZDOID>();
        private static readonly List<ZDO> StepBuffer = new List<ZDO>();
        private static readonly List<Action> OnReady = new List<Action>();
        private static bool _sweeping;
        private static int _prefabIndex;
        private static int _sectorCursor;
        private static float _nextPassTime;

        /// <summary>True once a full sweep has finished in this world.</summary>
        public static bool Ready { get; private set; }

        /// <summary>Server, every frame.</summary>
        internal static void Tick()
        {
            if (ZDOMan.instance == null) return;
            if (!_sweeping)
            {
                if (Time.unscaledTime < _nextPassTime) return;
                _sweeping = true;
                _prefabIndex = 0;
                _sectorCursor = 0;
                Gathering.Clear();
            }

            StepBuffer.Clear();
            bool prefabDone = ZDOMan.instance.GetAllZDOsWithPrefabIterative(PrefabNames[_prefabIndex], StepBuffer, ref _sectorCursor);
            for (int i = 0; i < StepBuffer.Count; i++)
            {
                var zdo = StepBuffer[i];
                if (zdo != null && zdo.IsValid()) Gathering.Add(zdo.m_uid);
            }
            if (!prefabDone) return;

            _sectorCursor = 0;
            if (++_prefabIndex < PrefabNames.Length) return;

            Snapshot.Clear();
            Snapshot.AddRange(Gathering);
            _sweeping = false;
            _nextPassTime = Time.unscaledTime + PassIntervalSeconds;
            if (Ready) return;

            Ready = true;
            foreach (var action in OnReady)
            {
                try { action(); }
                catch (Exception ex) { Debug.LogWarning($"[CompanionZdoCensus] deferred work threw: {ex.Message}"); }
            }
            OnReady.Clear();
        }

        /// <summary>Runs <paramref name="action"/> now if a sweep has finished, otherwise when the first one does.</summary>
        internal static void WhenReady(Action action)
        {
            if (Ready) action();
            else OnReady.Add(action);
        }

        /// <summary>The companion ZDOs from the latest sweep that still exist.</summary>
        internal static void Collect(List<ZDO> results)
        {
            results.Clear();
            if (ZDOMan.instance == null) return;
            for (int i = 0; i < Snapshot.Count; i++)
            {
                var zdo = ZDOMan.instance.GetZDO(Snapshot[i]);
                if (zdo != null && zdo.IsValid()) results.Add(zdo);
            }
        }

        /// <summary>A new world: forget the previous one's ZDOs and any work waiting on them.</summary>
        internal static void Reset()
        {
            Snapshot.Clear();
            Gathering.Clear();
            OnReady.Clear();
            _sweeping = false;
            _nextPassTime = 0f;
            Ready = false;
        }
    }
}
