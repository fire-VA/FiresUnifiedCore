using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnityEngine;
using FiresCore.Lifecycle;

namespace FiresCore.Npc.Vault
{
    /// <summary>
    /// Read/write helpers for the per-player companion roster stored on
    /// <c>Player.m_customData</c>. This is the single point of contact
    /// for the new save layer; nothing outside this class should touch
    /// the underlying customData key directly.
    /// 
    /// THREADING
    /// ---------
    /// All methods run on the Unity main thread (where Player /
    /// m_customData live). No locking ï¿½ single-threaded callers.
    /// 
    /// PHASE 1 STATUS
    /// --------------
    /// This class is delivered without any production call sites. Tests
    /// and Phase 3+ glue code will add callers. Until then it is dead
    /// code that compiles cleanly and ships safely alongside the existing
    /// vault flow.
    /// 
    /// FAILURE MODE
    /// ------------
    /// Any I/O failure (corrupt JSON, schema version we don't recognise,
    /// missing player) is logged at Warning and degrades to "no roster
    /// stored". Callers that need to distinguish "no entry" from "couldn't
    /// read" should use <see cref="TryGetRoster"/> which returns false on
    /// the latter; the convenience methods auto-coerce to empty.
    /// </summary>
    public static class PlayerCompanionStorage
    {
        /// <summary>
        /// Single key inside <c>Player.m_customData</c> that holds the
        /// JSON-serialised <see cref="PlayerCompanionRoster"/>. Versioned
        /// suffix (<c>_v1</c>) lets us migrate to a new shape without
        /// destroying readable history.
        /// </summary>
        public const string CustomDataKey = "FiresRPGmaker_CompanionRoster_v1";

        private const string LogPrefix = "[PlayerCompanionStorage]";

        /// <summary>
        /// Diagnostic flag. Set to true at runtime via debug commands to
        /// see every read/write. Off by default; the production callers
        /// log their own higher-level messages.
        /// </summary>
        public static bool VerboseLogging = false;

        // ?????????????????????????????? READ ??????????????????????????????

        /// <summary>
        /// Reads the roster from the player's customData.
        /// Returns true on success (including the "no roster yet" case,
        /// which yields an empty roster). Returns false on corrupt data.
        /// </summary>
        public static bool TryGetRoster(Player player, out PlayerCompanionRoster roster)
        {
            roster = null;
            if (player == null) return false;

            string raw;
            try
            {
                if (player.m_customData == null) { roster = NewEmpty(); return true; }
                if (!player.m_customData.TryGetValue(CustomDataKey, out raw) || string.IsNullOrEmpty(raw))
                {
                    roster = NewEmpty();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} m_customData access failed: {ex.Message}");
                return false;
            }

            try
            {
                var parsed = JsonConvert.DeserializeObject<PlayerCompanionRoster>(raw);
                if (parsed == null) { roster = NewEmpty(); return true; }

                // Schema check. We currently only know v1. Future versions
                // should add migration branches HERE rather than silently
                // overwriting unknown data.
                if (parsed.SchemaVersion <= 0 || parsed.SchemaVersion > PlayerCompanionRoster.CurrentSchemaVersion)
                {
                    Debug.LogWarning($"{LogPrefix} Unknown schema version {parsed.SchemaVersion}; refusing to read roster.");
                    return false;
                }

                if (parsed.Entries == null) parsed.Entries = new List<PlayerCompanionRosterEntry>();
                roster = parsed;

                if (VerboseLogging)
                    Debug.Log($"{LogPrefix} Read {parsed.Entries.Count} roster entr{(parsed.Entries.Count == 1 ? "y" : "ies")} for {SafePlayerName(player)}.");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} Failed to parse roster JSON for {SafePlayerName(player)}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Convenience read that returns an empty roster on any failure.
        /// Use when you don't care about distinguishing "no data" from
        /// "couldn't read data" ï¿½ most call sites just want a usable list.
        /// </summary>
        public static PlayerCompanionRoster GetRosterOrEmpty(Player player)
        {
            return TryGetRoster(player, out var r) ? r : NewEmpty();
        }

        /// <summary>
        /// Looks up a single entry by companion id. Returns null if absent
        /// or on read failure (read failure is treated as "no entry" from
        /// the caller's perspective; the underlying error has already
        /// been logged by <see cref="TryGetRoster"/>).
        /// </summary>
        public static PlayerCompanionRosterEntry GetEntry(Player player, string companionId)
        {
            if (string.IsNullOrEmpty(companionId)) return null;
            var roster = GetRosterOrEmpty(player);
            return FindEntry(roster, companionId);
        }

        /// <summary>
        /// Finds the entry inside an already-read roster. O(n) over the
        /// entries list, which is fine for typical sizes (? 50).
        /// </summary>
        public static PlayerCompanionRosterEntry FindEntry(PlayerCompanionRoster roster, string companionId)
        {
            if (roster == null || roster.Entries == null || string.IsNullOrEmpty(companionId)) return null;
            for (int i = 0; i < roster.Entries.Count; i++)
            {
                var e = roster.Entries[i];
                if (e != null && string.Equals(e.CompanionId, companionId, StringComparison.Ordinal))
                    return e;
            }
            return null;
        }

        // ?????????????????????????????? WRITE ?????????????????????????????

        /// <summary>
        /// Saves the roster back to the player's customData.
        /// Returns true on success. On failure (null player, serialisation
        /// error) the existing customData blob is left untouched.
        /// </summary>
        public static bool SaveRoster(Player player, PlayerCompanionRoster roster)
        {
            if (player == null || roster == null) return false;

            // Defence in depth ï¿½ never serialise a roster with an out-of-band
            // schema version. Always force-stamp the current version on write.
            roster.SchemaVersion = PlayerCompanionRoster.CurrentSchemaVersion;
            if (roster.Entries == null) roster.Entries = new List<PlayerCompanionRosterEntry>();

            string json;
            try
            {
                json = JsonConvert.SerializeObject(roster);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{LogPrefix} Serialise failed for {SafePlayerName(player)}: {ex.Message}");
                return false;
            }

            // Defer the write through the spawn gate so a roster save during the
            // player's respawn / teleport teardown doesn't race engine teardown.
            // Return value reflects serialise success only â€” the actual write may
            // run a frame later.
            string capturedJson = json;
            int entryCount = roster.Entries.Count;
            string playerName = SafePlayerName(player);
            PlayerSpawnGate.RunWhenReady(player, 30f, p =>
            {
                try
                {
                    if (p.m_customData == null) return;
                    p.m_customData[CustomDataKey] = capturedJson;
                    if (VerboseLogging)
                        Debug.Log($"{LogPrefix} Wrote {entryCount} entr{(entryCount == 1 ? "y" : "ies")} ({capturedJson.Length} bytes) for {playerName}.");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"{LogPrefix} m_customData write failed for {playerName}: {ex.Message}");
                }
            });
            return true;
        }

        /// <summary>
        /// Inserts the entry if no entry with the same id exists, or
        /// replaces the existing one if it does. Stamps
        /// <see cref="PlayerCompanionRosterEntry.LastUpdatedUtcTicks"/>
        /// to <c>DateTime.UtcNow</c>.
        /// 
        /// Reads + writes the full roster ï¿½ fine for ? 50 entries; bulk
        /// operations should call <see cref="SaveRoster"/> directly with a
        /// pre-mutated roster.
        /// </summary>
        public static bool UpsertEntry(Player player, PlayerCompanionRosterEntry entry)
        {
            if (player == null || entry == null || string.IsNullOrEmpty(entry.CompanionId)) return false;

            entry.LastUpdatedUtcTicks = DateTime.UtcNow.Ticks;

            // Contract: pending-respawn entries MUST carry a snapshot. If
            // we're asked to upsert one without a snapshot, that's a bug
            // in the caller ï¿½ log loudly and refuse rather than allowing
            // a structurally-invalid entry to be persisted.
            if (entry.IsPendingRespawn && entry.Snapshot == null)
            {
                Debug.LogError($"{LogPrefix} Refusing to upsert pending-respawn entry for {entry.CompanionId} with null snapshot. " +
                               $"Caller must populate Snapshot before marking IsPendingRespawn=true.");
                return false;
            }

            var roster = GetRosterOrEmpty(player);
            if (roster.Entries == null) roster.Entries = new List<PlayerCompanionRosterEntry>();

            int existingIndex = -1;
            for (int i = 0; i < roster.Entries.Count; i++)
            {
                var e = roster.Entries[i];
                if (e != null && string.Equals(e.CompanionId, entry.CompanionId, StringComparison.Ordinal))
                {
                    existingIndex = i;
                    break;
                }
            }

            if (existingIndex >= 0)
                roster.Entries[existingIndex] = entry;
            else
                roster.Entries.Add(entry);

            return SaveRoster(player, roster);
        }

        /// <summary>
        /// Removes the entry with the given id. No-op if absent.
        /// Returns true if an entry was actually removed.
        /// </summary>
        public static bool RemoveEntry(Player player, string companionId)
        {
            if (player == null || string.IsNullOrEmpty(companionId)) return false;

            var roster = GetRosterOrEmpty(player);
            if (roster.Entries == null) return false;

            int removed = 0;
            for (int i = roster.Entries.Count - 1; i >= 0; i--)
            {
                var e = roster.Entries[i];
                // Remove null tombstone entries OR entries whose ID matches.
                // Previously the condition was `e == null || string.Equals(...)`,
                // which is equivalent ï¿½ but keeping them separate makes intent clear
                // and avoids any future short-circuit confusion.
                bool isNull = e == null;
                bool idMatch = !isNull && string.Equals(e.CompanionId, companionId, StringComparison.Ordinal);
                if (isNull || idMatch)
                {
                    roster.Entries.RemoveAt(i);
                    removed++;
                }
            }

            if (removed == 0) return false;

            return SaveRoster(player, roster);
        }

        // ?????????????????????????????? HELPERS ???????????????????????????

        /// <summary>
        /// Resolves the world UID of the currently-loaded server, or 0 if
        /// ZNet isn't initialised yet. Use this to stamp
        /// <see cref="PlayerCompanionRosterEntry.ServerWorldUid"/> at
        /// creation time and to gate restore logic.
        /// </summary>
        public static long GetCurrentServerWorldUid()
        {
            try
            {
                var znet = ZNet.instance;
                if (znet == null) return 0;
                return znet.GetWorldUID();
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// True if this entry was created on the same world as we're
        /// currently in. Used by the restore path to skip companions that
        /// belong to a different server (cross-server character migration).
        /// </summary>
        public static bool BelongsToCurrentServer(PlayerCompanionRosterEntry entry)
        {
            if (entry == null) return false;
            long current = GetCurrentServerWorldUid();
            // 0 means "current server unknown" ï¿½ be permissive in that
            // case so single-player or pre-network-ready spawns aren't
            // accidentally denied. The wrong-server check kicks in only
            // when both sides are known.
            if (current == 0 || entry.ServerWorldUid == 0) return true;
            return entry.ServerWorldUid == current;
        }

        private static PlayerCompanionRoster NewEmpty()
        {
            return new PlayerCompanionRoster
            {
                SchemaVersion = PlayerCompanionRoster.CurrentSchemaVersion,
                Entries = new List<PlayerCompanionRosterEntry>(),
            };
        }

        private static string SafePlayerName(Player p)
        {
            try { return p != null ? (p.GetPlayerName() ?? "<null-name>") : "<null-player>"; }
            catch { return "<player-name-throw>"; }
        }
    }
}
