using System;
using System.Collections.Generic;
using UnityEngine;
using FiresCore.Bridge;

namespace FiresCore.Npc.Persistence
{
    /// <summary>
    /// <see cref="byte"/>[] ⇄ <see cref="List{DormantNpcEntry}"/> via <c>ZPackage</c>, with the
    /// <see cref="NpcSaveState"/> snapshot stored as Core's JSON. JSON-for-snapshot is deliberate:
    /// it lets Core add/remove <see cref="NpcSaveState"/> fields without breaking on-disk kennels
    /// (missing JSON fields default; extra fields are ignored), while the envelope (id, kind,
    /// deadline, timestamp) stays packed for speed.
    ///
    /// <para>Moved from FiresCompanions into Core as part of the persistence redesign — the dormant
    /// store is shared infrastructure both companion frontends use, so it lives in Core. The
    /// envelope now carries <see cref="DormancyKind"/> directly (an int) rather than the old
    /// kennel's dismissed-bool, so the kind is explicit on disk.</para>
    /// </summary>
    internal static class CompanionKennelSerializer
    {
        /// <summary>Bump on any breaking envelope change. Old payloads are dropped silently with a warning.</summary>
        private const int SchemaVersion = 2;

        public static byte[] Serialize(List<DormantNpcEntry> entries)
        {
            var pkg = new ZPackage();
            pkg.Write(SchemaVersion);
            int count = entries?.Count ?? 0;
            pkg.Write(count);
            if (count == 0) return pkg.GetArray();

            for (int i = 0; i < count; i++)
            {
                var entry = entries[i];
                try
                {
                    pkg.Write(entry?.NpcId ?? string.Empty);
                    pkg.Write((int)(entry?.Kind ?? DormancyKind.LoggedOutFollower));
                    pkg.Write(entry?.RecallDeadlineUtcTicks ?? 0L);
                    pkg.Write(entry?.LastUpdatedUtcTicks ?? 0L);
                    pkg.Write(entry?.Snapshot?.ToJson() ?? string.Empty);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CompanionKennel] Serialize entry #{i} failed: {ex.Message}; writing empty slot");
                    // Keep stream count consistent so decode doesn't desync on one bad entry.
                    pkg.Write(string.Empty);
                    pkg.Write((int)DormancyKind.LoggedOutFollower);
                    pkg.Write(0L);
                    pkg.Write(0L);
                    pkg.Write(string.Empty);
                }
            }
            return pkg.GetArray();
        }

        public static List<DormantNpcEntry> Deserialize(byte[] data)
        {
            var entries = new List<DormantNpcEntry>();
            if (data == null || data.Length == 0) return entries;

            ZPackage pkg;
            try { pkg = new ZPackage(data); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionKennel] Decode: invalid ZPackage payload: {ex.Message}");
                return entries;
            }

            int version;
            int count;
            try
            {
                version = pkg.ReadInt();
                count = pkg.ReadInt();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CompanionKennel] Decode: header read failed: {ex.Message}");
                return entries;
            }
            if (version != SchemaVersion)
            {
                Debug.LogWarning($"[CompanionKennel] Decode: unknown schema version {version} (expected {SchemaVersion}); dropping payload");
                return entries;
            }

            for (int i = 0; i < count; i++)
            {
                try
                {
                    var entry = new DormantNpcEntry
                    {
                        NpcId                  = pkg.ReadString(),
                        Kind                   = (DormancyKind)pkg.ReadInt(),
                        RecallDeadlineUtcTicks = pkg.ReadLong(),
                        LastUpdatedUtcTicks    = pkg.ReadLong(),
                    };
                    var json = pkg.ReadString();
                    if (!string.IsNullOrEmpty(json))
                    {
                        try { entry.Snapshot = NpcSaveState.FromJson(json); }
                        catch (Exception jsonEx)
                        {
                            Debug.LogWarning($"[CompanionKennel] Decode entry #{i} ({entry.NpcId}): NpcSaveState JSON failed: {jsonEx.Message}");
                        }
                    }
                    if (!string.IsNullOrEmpty(entry.NpcId))
                        entries.Add(entry);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CompanionKennel] Decode entry #{i} stream-read failed: {ex.Message}; stopping at {entries.Count} entries");
                    break;
                }
            }
            return entries;
        }
    }
}
