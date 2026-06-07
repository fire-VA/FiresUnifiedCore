using System;
using System.Collections.Generic;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Lifecycle
{
    // "Is this player safe to mutate right now?" gate plus a deferred-work
    // queue. Writes to Player.m_customData from inside Player.OnSpawned
    // (and similar lifecycle windows) hard-crash the game without a
    // managed exception even when every readiness check passes; the
    // engine hasn't finished post-spawn finalization at that point.
    //
    // Callers wrap their write in RunWhenReady; the work is queued onto a
    // hidden driver MonoBehaviour that polls each frame and fires the
    // moment the gate opens (or after a per-call timeout, in which case
    // the work is dropped with a warning — bounded so a never-ready
    // player can't grow the queue unbounded).
    //
    // All queue operations run on the Unity main thread. Not thread-safe.
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
        private static bool IsPlayerObjectAlive(Player p)
        {
            try { return (bool)p; }
            catch { return false; }
        }

        private static bool IsZNetViewLive(Player p)
        {
            try
            {
                if (p.m_nview == null) return false;
                return p.m_nview.IsValid();
            }
            catch { return false; }
        }

        private static bool IsNetworkReady()
        {
            if (ZNet.instance == null) return false;
            if (ZNet.instance.IsServer()) return true;
            return ZNet.GetConnectionStatus() == ZNet.ConnectionStatus.Connected;
        }

        private static bool IsPlayerTeleporting(Player p)
        {
            try { return p.IsTeleporting(); }
            catch { return true; }
        }

        // Diagnostic ENTER/EXIT bracket. A queued callback that ENTERs and
        // never EXITs is the login-freeze fingerprint — keep until the
        // freeze is fully characterized in the field.
        private static void InvokeWork(Player p, Action<Player> work)
        {
            string targetDesc = ResolvePlayerName(p);
            string workDesc = ResolveWorkDescription(work);

            FiresLogger.LogInfo($"{DiagnosticPrefix}.InvokeWork ENTER target={targetDesc} work={workDesc}");
            try { work(p); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{LogPrefix} deferred action threw: {ex.Message}");
            }
            FiresLogger.LogInfo($"{DiagnosticPrefix}.InvokeWork EXIT target={targetDesc} work={workDesc}");
        }

        private static string ResolvePlayerName(Player p)
        {
            if (p == null) return "<null player>";
            try { return p.GetPlayerName() ?? "<unnamed>"; }
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
                    var e = _queue[i];
                    Player target = e.UseLocal ? Player.m_localPlayer : e.Target;

                    if (IsReadyForCustomDataWrite(target))
                    {
                        _queue.RemoveAt(i);
                        InvokeWork(target, e.Work);
                        continue;
                    }

                    if (now >= e.DeadlineUnscaled)
                        DropTimedOutEntry(i, e, target);
                }
            }

            private void DropTimedOutEntry(int index, Entry e, Player target)
            {
                _queue.RemoveAt(index);
                if (!VerboseLogging) return;
                FiresLogger.LogWarning(
                    $"{LogPrefix} deferred action timed out before player became ready " +
                    $"(useLocal={e.UseLocal}, target={(target == null ? "null" : ResolvePlayerName(target))}) — dropping.");
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
