using System;
using System.Collections.Generic;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Lifecycle
{
    // Defers work on a player until it is safe: writing Player.m_customData inside OnSpawned hard-crashes the game even
    // when every readiness check passes. RunWhenReady queues the work on a hidden driver that runs it when the gate opens,
    // or drops it with a warning after its timeout. Main thread only.
    public static class PlayerSpawnGate
    {
        private const string LogPrefix = "[PlayerSpawnGate]";
        private const string DiagnosticPrefix = "[LoginFreeze][DIAG] PlayerSpawnGate";
        private const string DriverHostName = "FiresUnifiedCore_PlayerSpawnGateDriver";
        private const float DefaultCustomDataTimeoutSeconds = 30f;
        private const float MinTimeoutSeconds = 0.1f;
        private const int InitialQueueCapacity = 8;
        private const int MaxQueuedEntries = 256;

        public static bool VerboseLogging = false;

        public static bool IsReadyForCustomDataWrite(Player p)
        {
            if (p == null) return false;
            if (!IsPlayerObjectAlive(p)) return false;
            if (p.m_customData == null) return false;
            if (!IsZNetViewLive(p)) return false;
            if (!IsNetworkReady()) return false;
            if (IsPlayerTeleporting(p)) return false;
            return true;
        }

        public static void RunWhenReady(Player player, float timeoutSeconds, Action<Player> work)
        {
            if (work == null) return;
            if (player == null)
            {
                if (VerboseLogging)
                    FiresLogger.LogInfo($"{LogPrefix} RunWhenReady called with null player — dropping.");
                return;
            }
            Driver.GetOrCreate().Enqueue(player, timeoutSeconds, work);
        }

        public static void RunWhenLocalReady(float timeoutSeconds, Action<Player> work)
        {
            if (work == null) return;
            Driver.GetOrCreate().EnqueueLocal(timeoutSeconds, work);
        }

        public static void SafeSetCustomData(Player player, string key, string value,
                                              float timeoutSeconds = DefaultCustomDataTimeoutSeconds)
        {
            if (string.IsNullOrEmpty(key)) return;
            RunWhenReady(player, timeoutSeconds, p => p.m_customData[key] = value);
        }

        public static void SafeSetLocalCustomData(string key, string value,
                                                   float timeoutSeconds = DefaultCustomDataTimeoutSeconds)
        {
            if (string.IsNullOrEmpty(key)) return;
            RunWhenLocalReady(timeoutSeconds, p => p.m_customData[key] = value);
        }

        // The C# reference can survive a frame or two after Unity has torn
        // down the GameObject — `!p` catches that fake-null state.
        private static bool IsPlayerObjectAlive(Player player)
        {
            try { return (bool)player; }
            catch { return false; }
        }

        private static bool IsZNetViewLive(Player player)
        {
            try
            {
                if (player.m_nview == null) return false;
                return player.m_nview.IsValid();
            }
            catch { return false; }
        }

        private static bool IsNetworkReady()
        {
            if (ZNet.instance == null) return false;
            if (ZNet.instance.IsServer()) return true;
            return ZNet.GetConnectionStatus() == ZNet.ConnectionStatus.Connected;
        }

        private static bool IsPlayerTeleporting(Player player)
        {
            try { return player.IsTeleporting(); }
            catch { return true; }
        }

        // Verbose-only ENTER/EXIT bracket: a queued callback that ENTERs and never EXITs is the login-freeze
        // fingerprint. FDT's stall attribution times these callbacks now, so the bracket stays off unless verbose.
        private static void InvokeWork(Player player, Action<Player> work)
        {
            bool bracket = FiresLogger.VerboseEnabled;
            string targetDesc = bracket ? ResolvePlayerName(player) : null;
            string workDesc = bracket ? ResolveWorkDescription(work) : null;

            if (bracket) FiresLogger.LogInfo($"{DiagnosticPrefix}.InvokeWork ENTER target={targetDesc} work={workDesc}");
            try { work(player); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} deferred action threw: {ex.Message}");
            }
            if (bracket) FiresLogger.LogInfo($"{DiagnosticPrefix}.InvokeWork EXIT target={targetDesc} work={workDesc}");
        }

        private static string ResolvePlayerName(Player player)
        {
            if (player == null) return "<null player>";
            try { return player.GetPlayerName() ?? "<unnamed>"; }
            catch { return "<unnameable>"; }
        }

        private static string ResolveWorkDescription(Action<Player> work)
        {
            return (work?.Method?.DeclaringType?.FullName ?? "<unknown>")
                + "." + (work?.Method?.Name ?? "<unknown>");
        }

        private class Driver : MonoBehaviour
        {
            private static Driver _instance;

            private readonly List<Entry> _queue = new List<Entry>(InitialQueueCapacity);

            public static Driver GetOrCreate()
            {
                if (_instance != null) return _instance;

                var go = new GameObject(DriverHostName);
                DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                _instance = go.AddComponent<Driver>();
                return _instance;
            }

            public void Enqueue(Player target, float timeoutSeconds, Action<Player> work)
            {
                EnqueueEntry(target, useLocal: false, timeoutSeconds, work);
            }

            public void EnqueueLocal(float timeoutSeconds, Action<Player> work)
            {
                EnqueueEntry(target: null, useLocal: true, timeoutSeconds, work);
            }

            private void EnqueueEntry(Player target, bool useLocal, float timeoutSeconds, Action<Player> work)
            {
                if (_queue.Count >= MaxQueuedEntries)
                {
                    FiresLogger.LogWarning($"{LogPrefix} queue at MaxQueuedEntries ({MaxQueuedEntries}) — dropping enqueue.");
                    return;
                }

                _queue.Add(new Entry
                {
                    Target = target,
                    UseLocal = useLocal,
                    DeadlineUnscaled = Time.unscaledTime + Mathf.Max(MinTimeoutSeconds, timeoutSeconds),
                    Work = work,
                });
            }

            private void Update()
            {
                if (_queue.Count == 0) return;

                float now = Time.unscaledTime;

                // Walk back-to-front so RemoveAt doesn't break iteration.
                for (int i = _queue.Count - 1; i >= 0; i--)
                {
                    var pending = _queue[i];
                    Player target = pending.UseLocal ? Player.m_localPlayer : pending.Target;

                    if (IsReadyForCustomDataWrite(target))
                    {
                        _queue.RemoveAt(i);
                        InvokeWork(target, pending.Work);
                        continue;
                    }

                    if (now >= pending.DeadlineUnscaled)
                        DropTimedOutEntry(i, pending, target);
                }
            }

            private void DropTimedOutEntry(int index, Entry entry, Player target)
            {
                _queue.RemoveAt(index);
                if (!VerboseLogging) return;
                FiresLogger.LogWarning(
                    $"{LogPrefix} deferred action timed out before player became ready " +
                    $"(useLocal={entry.UseLocal}, target={(target == null ? "null" : ResolvePlayerName(target))}) — dropping.");
            }

            private struct Entry
            {
                public Player Target;
                public bool UseLocal;
                public float DeadlineUnscaled;
                public Action<Player> Work;
            }
        }
    }
}
