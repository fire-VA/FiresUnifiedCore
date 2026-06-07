using System;
using System.Collections.Generic;
using FiresCore.Npc;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Companion-roster persistence seam. A provider registers the delegates; while none is present
    /// every accessor is a null-safe no-op (returns false/null), which is the standalone-greenfield
    /// path — the cluster's legacy vault calls short-circuit and the kennel persists companions via
    /// the Core <c>CaptureState/ApplyState(NpcSaveState)</c> contract instead. In integrated mode the
    /// host registers these against its Marketplace vault. Player-keyed by design; world/character
    /// scoping is consumer policy (filter the returned roster).
    /// </summary>
    public static class CompanionVaultBridge
    {
        // ── Data provider ──
        public static Func<bool> IsAvailable;
        public static Func<long, List<CompanionSaveData>> GetCompanions;
        public static Action<long, CompanionSaveData> SaveCompanion;
        public static Action<long, string> RemoveCompanion;

        // ── Player-announced hook (replaces the Harmony patch on VaultOfKnowledge.RPC_AnnouncePlayerInfo) ──
        /// <summary>Raised server-side when a player announces to the server; carries the playerId.</summary>
        public static event Action<long> PlayerAnnouncedToServer;

        // ── Restore-timing gate ──
        // Integrated mode gates server-side companion restore until the host finishes streaming its
        // config files to the joining peer (host provides via its config-file watcher). Standalone has
        // no such streaming, so an unprovided gate reports "ready" and restore proceeds on announce.
        public static Func<long, bool> RestoreReadyForPeer;
        public static Action<long> ClearRestoreGate;

        public static bool IsVaultAvailable()
        {
            try { return IsAvailable != null && IsAvailable(); } catch { return false; }
        }

        public static List<CompanionSaveData> GetCompanionsFor(long playerId)
        {
            try { return GetCompanions?.Invoke(playerId); } catch { return null; }
        }

        public static void Save(long playerId, CompanionSaveData data)
        {
            try { SaveCompanion?.Invoke(playerId, data); } catch { }
        }

        public static void Remove(long playerId, string companionId)
        {
            try { RemoveCompanion?.Invoke(playerId, companionId); } catch { }
        }

        public static void RaisePlayerAnnounced(long playerId)
        {
            try { PlayerAnnouncedToServer?.Invoke(playerId); } catch { }
        }

        /// <summary>True when server-side restore may proceed for this peer (default: ready when no provider).</summary>
        public static bool IsRestoreReadyForPeer(long peer)
        {
            try { return RestoreReadyForPeer == null || RestoreReadyForPeer(peer); } catch { return true; }
        }

        public static void ClearRestoreGateFor(long peer)
        {
            try { ClearRestoreGate?.Invoke(peer); } catch { }
        }
    }
}
