using System;
using System.Collections.Generic;
using System.IO;
using FiresLogAnalysis;
using UnityEngine;

namespace FiresCore.ClientLogRelay
{
    /// <summary>
    /// Fan-out hub for client login artifacts. The wire transport builds a ClientLogArtifacts and calls
    /// ReportArtifacts; the relay parses errors and warnings into it once, then hands it to every registered
    /// consumer, each of which decides on its own whether to write it to disk, post it, or ignore it.
    /// Registration is process-global and idempotent, and every public method is locked.
    /// </summary>
    public static class ClientLogRelay
    {
        private static readonly object _lock = new object();
        private static readonly List<IClientLogConsumer> _consumers = new List<IClientLogConsumer>();

        /// <summary>
        /// True when at least one consumer is registered. Wire-transport layers should check
        /// this before doing the work of soliciting a log from the client - no consumers,
        /// no point.
        /// </summary>
        public static bool HasConsumers
        {
            get { lock (_lock) { return _consumers.Count > 0; } }
        }

        /// <summary>
        /// Registers a consumer. Returns true if added, false if a consumer with the same
        /// <see cref="IClientLogConsumer.ConsumerId"/> is already present.
        /// </summary>
        public static bool RegisterConsumer(IClientLogConsumer consumer)
        {
            if (consumer == null || string.IsNullOrEmpty(consumer.ConsumerId)) return false;
            lock (_lock)
            {
                for (int i = 0; i < _consumers.Count; i++)
                {
                    if (string.Equals(_consumers[i].ConsumerId, consumer.ConsumerId, StringComparison.Ordinal))
                        return false;
                }
                _consumers.Add(consumer);
                Debug.Log($"[ClientLogRelay] Registered consumer: {consumer.ConsumerId} (total: {_consumers.Count})");
                return true;
            }
        }

        /// <summary>
        /// Unregisters a consumer by id. Returns true if removed.
        /// </summary>
        public static bool UnregisterConsumer(string consumerId)
        {
            if (string.IsNullOrEmpty(consumerId)) return false;
            lock (_lock)
            {
                for (int i = 0; i < _consumers.Count; i++)
                {
                    if (string.Equals(_consumers[i].ConsumerId, consumerId, StringComparison.Ordinal))
                    {
                        _consumers.RemoveAt(i);
                        Debug.Log($"[ClientLogRelay] Unregistered consumer: {consumerId} (total: {_consumers.Count})");
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Called by the wire-transport layer once the client's log + mod list bytes have
        /// been received, verified, and decoded. The relay:
        ///  1. Analyzes the log once with <see cref="AnalyzeLog"/>.
        ///  2. Diffs the mod lists once with <see cref="ComputeModDiff"/>.
        ///  3. Fans out to every registered consumer.
        ///
        /// Consumer exceptions are caught and logged; one failing consumer does not affect
        /// the others.
        /// </summary>
        public static void ReportArtifacts(ClientLogArtifacts artifacts)
        {
            if (artifacts == null) return;

            AnalyzeLog(artifacts);
            ComputeModDiff(artifacts);

            // Snapshot consumers inside the lock, invoke outside so a slow consumer
            // cannot stall other ReportArtifacts calls.
            IClientLogConsumer[] snapshot;
            lock (_lock)
            {
                if (_consumers.Count == 0)
                {
                    Debug.Log($"[ClientLogRelay] No consumers; dropping artifacts for {artifacts.PlatformId}");
                    return;
                }
                snapshot = _consumers.ToArray();
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                var consumer = snapshot[i];
                try
                {
                    consumer.OnClientArtifacts(artifacts);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[ClientLogRelay] Consumer '{consumer.ConsumerId}' threw: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Runs FiresLogAnalysis over the client log and renders its report. A log the analyzer
        /// throws on leaves <see cref="ClientLogArtifacts.LogAnalysis"/> null and the report says why.
        /// </summary>
        public static void AnalyzeLog(ClientLogArtifacts artifacts)
        {
            try
            {
                var analysis = LogAnalyzer.Analyze(new MemoryStream(artifacts.LogBytes, writable: false));
                string logOwner = $"{artifacts.PlayerName} ({artifacts.PlatformId})";
                artifacts.ErrorsWarningsReport = ReportWriter.Write(analysis, logOwner);
                artifacts.LogAnalysis = analysis;
            }
            catch (Exception ex)
            {
                string failure = $"Log analysis failed for {artifacts.PlatformId}: {ex.GetType().Name}: {ex.Message}";
                Debug.LogWarning($"[ClientLogRelay] {failure}");
                artifacts.ErrorsWarningsReport = failure;
            }
        }

        /// <summary>
        /// Diffs the client mod list against the server's into <see cref="ClientLogArtifacts.ModDiff"/>.
        /// Does nothing when the transport did not provide the server's mod list.
        /// </summary>
        public static void ComputeModDiff(ClientLogArtifacts artifacts)
        {
            if (artifacts.ServerMods == null || artifacts.ServerMods.Count == 0) return;
            try
            {
                artifacts.ModDiff = ModListDiff.Compute(
                    artifacts.ModList, artifacts.ServerMods,
                    artifacts.PlayerName, artifacts.PlatformId,
                    artifacts.BrandLabel, artifacts.CapturedUtc);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ClientLogRelay] ModListDiff failed for {artifacts.PlatformId}: {ex.Message}");
            }
        }

        // Cross-mod login-snapshot ownership. Several mods dropping in this folder may each want to post a Discord
        // embed per login, so ownership is coordinated through an AppDomain slot keyed by a non-namespaced string:
        // two mods built against renamed copies still see the same state. Each claims with
        // TryClaimLoginSnapshotOwnership("MyMod.Login", priority) - highest priority wins, ties go to the first
        // caller - and each consumer no-ops when IsLoginSnapshotOwner says otherwise. With no claim at all every
        // consumer stays enabled.

        /// <summary>
        /// Fully-qualified string literal; intentionally NOT derived from a namespace so
        /// that two mods holding independent copies of this module still share state.
        /// </summary>
        private const string LoginOwnerKey         = "FiresMods.ClientLogRelay.LoginSnapshotOwner";
        private const string LoginOwnerPriorityKey = "FiresMods.ClientLogRelay.LoginSnapshotOwnerPriority";

        /// <summary>
        /// Tries to take (or keep) ownership of the "post Discord snapshot on login"
        /// channel.
        /// </summary>
        /// <param name="ownerId">Caller's stable id (e.g. <c>"FiresGhettoNetworkMod.Login"</c>).
        /// Used only for diagnostics and self-identification.</param>
        /// <param name="priority">Higher values win. Default 0. Sibling mods that should
        /// take precedence over a baseline mod should claim with a higher number.</param>
        /// <returns>true if <paramref name="ownerId"/> is (now) the owner, false if some
        /// other caller holds a higher-priority claim.</returns>
        public static bool TryClaimLoginSnapshotOwnership(string ownerId, int priority = 0)
        {
            if (string.IsNullOrEmpty(ownerId)) return false;
            lock (_lock)
            {
                string currentOwner     = AppDomain.CurrentDomain.GetData(LoginOwnerKey) as string;
                object currentPrioObj   = AppDomain.CurrentDomain.GetData(LoginOwnerPriorityKey);
                int    currentPriority  = currentPrioObj is int claimedPriority ? claimedPriority : int.MinValue;

                if (string.IsNullOrEmpty(currentOwner))
                {
                    AppDomain.CurrentDomain.SetData(LoginOwnerKey, ownerId);
                    AppDomain.CurrentDomain.SetData(LoginOwnerPriorityKey, priority);
                    Debug.Log($"[ClientLogRelay] Login snapshot owner: '{ownerId}' (priority {priority})");
                    return true;
                }

                if (string.Equals(currentOwner, ownerId, StringComparison.Ordinal))
                {
                    // Same owner re-claiming; bump priority if higher.
                    if (priority > currentPriority)
                        AppDomain.CurrentDomain.SetData(LoginOwnerPriorityKey, priority);
                    return true;
                }

                if (priority > currentPriority)
                {
                    Debug.Log($"[ClientLogRelay] Login snapshot owner changed: " +
                              $"'{currentOwner}' (priority {currentPriority}) -> '{ownerId}' (priority {priority})");
                    AppDomain.CurrentDomain.SetData(LoginOwnerKey, ownerId);
                    AppDomain.CurrentDomain.SetData(LoginOwnerPriorityKey, priority);
                    return true;
                }

                // Another claim wins.
                Debug.Log($"[ClientLogRelay] Login snapshot claim declined for '{ownerId}' " +
                          $"(priority {priority}); current owner '{currentOwner}' (priority {currentPriority})");
                return false;
            }
        }

        /// <summary>
        /// True if <paramref name="ownerId"/> is the current owner, OR if no mod has ever
        /// claimed ownership (so we default to "allowed" when nobody bothered to opt in).
        /// This is the call consumers make inside their <c>EnabledGate</c>.
        /// </summary>
        public static bool IsLoginSnapshotOwner(string ownerId)
        {
            if (string.IsNullOrEmpty(ownerId)) return false;
            string currentOwner = AppDomain.CurrentDomain.GetData(LoginOwnerKey) as string;
            if (string.IsNullOrEmpty(currentOwner)) return true; // nobody claimed, everyone allowed
            return string.Equals(currentOwner, ownerId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Returns the current login-snapshot owner id, or null if none has been claimed.
        /// Exposed for diagnostics / console commands.
        /// </summary>
        public static string GetLoginSnapshotOwner()
        {
            return AppDomain.CurrentDomain.GetData(LoginOwnerKey) as string;
        }
    }
}
