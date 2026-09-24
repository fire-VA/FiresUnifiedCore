using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>
    /// Two facts about every sector in the world, kept current as the world changes: the last frame anything in it
    /// changed, and how many of the objects filed there are the far-away kind that distant players are sent.
    ///
    /// Several mods want these and each one that answers them for itself ends up hooking the same six vanilla methods
    /// to learn the same thing. Worse, each gets to be subtly wrong on its own: a count that misses one way makes a
    /// mod skip work it needed to do, and the objects concerned simply stop appearing, with nothing in a log to say so.
    /// This publishes the answers once, for everyone, and checks its own work against a real count so that silence can
    /// be trusted rather than assumed.
    ///
    /// Nothing here changes behaviour. It only records.
    /// </summary>
    [HarmonyPatch]
    public static class SectorChanges
    {
        // ZNet builds ZDOMan with width 512, so m_objectsBySector is exactly this long.
        private const int SectorCount = 512 * 512;
        private const int Unknown = -1;
        private const double AuditSeconds = 5.0;
        private const int AuditSectorsPerPass = 64;
        private const double ReportSeconds = 300.0;

        private static readonly int[] s_changedFrame = new int[SectorCount];
        private static readonly int[] s_distant = new int[SectorCount];

        private static readonly AccessTools.FieldRef<ZDOMan, List<ZDO>[]> s_objectsBySector
            = AccessTools.FieldRefAccess<ZDOMan, List<ZDO>[]>("m_objectsBySector");

        private static double s_nextAudit;
        private static double s_nextReport;
        private static int s_auditCursor;

        /// <summary>Counts up once per world update, so a caller can remember "as of frame N" and ask again later.</summary>
        public static int Frame { get; private set; } = 1;

        /// <summary>True once a world is loaded and the counts have been built.</summary>
        public static bool Ready { get; private set; }

        /// <summary>Sectors audited against a real count, and how many disagreed. A caller trusting this should look.</summary>
        public static long Audited { get; private set; }
        public static long Wrong { get; private set; }

        /// <summary>The frame something in this sector last changed. An unknown sector answers "just now", so a caller
        /// that compares against its own last-seen frame does the work rather than skipping it.</summary>
        public static int ChangedFrame(uint sector) => sector < SectorCount ? s_changedFrame[sector] : Frame;

        /// <summary>True when anything in any of these sectors has changed since the given frame.</summary>
        public static bool AnyChangedSince(IList<uint> sectors, int since)
        {
            if (!Ready || sectors == null) return true;
            for (int i = 0; i < sectors.Count; i++)
                if (ChangedFrame(sectors[i]) >= since) return true;
            return false;
        }

        /// <summary>How many objects filed in this sector are flagged distant, or Unknown when it cannot be trusted.
        /// Only a confident zero means a caller may skip the sector.</summary>
        public static int DistantCount(uint sector)
        {
            if (!Ready || sector >= SectorCount) return Unknown;
            return s_distant[sector];
        }

        public static bool HasNoDistantObjects(uint sector) => DistantCount(sector) == 0;

        [HarmonyPatch(typeof(ZNet), "Update"), HarmonyPrefix, HarmonyPriority(Priority.First)]
        static void NextFrame() => Frame++;

        [HarmonyPatch(typeof(ZNet), "Start"), HarmonyPostfix]
        static void OnStart() => Rebuild();

        [HarmonyPatch(typeof(ZNet), "Shutdown"), HarmonyPostfix]
        static void OnShutdown()
        {
            Ready = false;
            Array.Clear(s_changedFrame, 0, SectorCount);
            Array.Clear(s_distant, 0, SectorCount);
        }

        // Loading a world files objects into their sectors without going through AddToSector, so the counts have to be
        // taken afresh afterwards rather than accumulated.
        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.LoadChunks)), HarmonyPostfix]
        static void OnLoadChunks() => Rebuild();

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.AddToSector)), HarmonyPostfix]
        static void OnAddToSector(ZDO zdo, ZoneSystem.SectorIndex sectorIndex)
        {
            Mark(sectorIndex.Sector);
            if (zdo != null && zdo.Distant) Add(sectorIndex.Sector, 1);
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.RemoveFromSector)), HarmonyPostfix]
        static void OnRemoveFromSector(ZDO zdo, ZoneSystem.SectorIndex sectorIndex)
        {
            Mark(sectorIndex.Sector);
            if (zdo != null && zdo.Distant) Add(sectorIndex.Sector, -1);
        }

        // The flag is set from ZNetView.Awake, AFTER the object has already been filed into its sector, so a count
        // built only from AddToSector would be wrong for every object that has just been created.
        // Read before the write lands: the property still holds the old answer here, and value carries the new one.
        [HarmonyPatch(typeof(ZDO), "set_Distant"), HarmonyPrefix]
        static void DistantSetter_Prefix(ZDO __instance, bool value)
        {
            if (!Ready || __instance == null || value == __instance.Distant) return;
            Add(SectorOf(__instance), value ? 1 : -1);
        }

        [HarmonyPatch(typeof(ZDO), "IncreaseDataRevision"), HarmonyPostfix]
        static void OnDataRevision(ZDO __instance) => Mark(SectorOf(__instance));

        [HarmonyPatch(typeof(ZDO), "IncreaseOwnerRevision"), HarmonyPostfix]
        static void OnOwnerRevision(ZDO __instance) => Mark(SectorOf(__instance));

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Deserialize)), HarmonyPostfix]
        static void OnDeserialize(ZDO __instance) => Mark(SectorOf(__instance));

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwnerInternal)), HarmonyPostfix]
        static void OnSetOwnerInternal(ZDO __instance) => Mark(SectorOf(__instance));

        // Checks a slice of the world against a real count every few seconds. The counts above are maintained by
        // arithmetic, and arithmetic that drifts one way makes objects disappear without complaining, so this is the
        // only thing that lets a caller treat a zero as meaning zero.
        [HarmonyPatch(typeof(ZDOMan), "Update"), HarmonyPostfix]
        static void Audit(ZDOMan __instance)
        {
            if (!Ready) return;
            double now = Time.realtimeSinceStartupAsDouble;
            ReportIfDue(now);
            if (now < s_nextAudit) return;
            s_nextAudit = now + AuditSeconds;

            var bySector = s_objectsBySector(__instance);
            if (bySector == null) return;

            for (int i = 0; i < AuditSectorsPerPass; i++)
            {
                int sector = s_auditCursor;
                s_auditCursor = s_auditCursor + 1 >= SectorCount ? 0 : s_auditCursor + 1;
                if (sector >= bySector.Length) continue;

                int held = s_distant[sector];
                if (held == Unknown) continue;

                int real = 0;
                var objects = bySector[sector];
                if (objects != null)
                    for (int j = 0; j < objects.Count; j++)
                        if (objects[j].Distant) real++;

                Audited++;
                if (real == held) continue;
                Wrong++;
                s_distant[sector] = real;
            }
        }

        /// <summary>Takes the audit tally since the last caller asked, or null when there is nothing to say.</summary>
        public static string TakeAuditLine()
        {
            if (Audited == 0) return null;
            string line = $"[Sectors] distant counts checked against a real count {Audited:N0} times, "
                + $"{Wrong:N0} disagreed.";
            Audited = 0;
            Wrong = 0;
            return line;
        }

        // Reported on its own timer rather than through another mod, so the index does not need anyone to ask.
        private static void ReportIfDue(double now)
        {
            if (now < s_nextReport) return;
            bool first = s_nextReport == 0.0;
            s_nextReport = now + ReportSeconds;
            if (first) return;
            string line = TakeAuditLine();
            if (line != null) FiresUnifiedCore.Log.LogInfo(line);
            string skip = DistantSectorSkip.TakeReportLine();
            if (skip != null) FiresUnifiedCore.Log.LogInfo(skip);
        }

        private static void Rebuild()
        {
            Array.Clear(s_changedFrame, 0, SectorCount);
            Array.Clear(s_distant, 0, SectorCount);
            s_auditCursor = 0;
            s_nextAudit = 0.0;
            s_nextReport = 0.0;

            var zdoMan = ZDOMan.instance;
            var bySector = zdoMan == null ? null : s_objectsBySector(zdoMan);
            if (bySector != null)
            {
                int width = Math.Min(bySector.Length, SectorCount);
                for (int sector = 0; sector < width; sector++)
                {
                    var objects = bySector[sector];
                    if (objects == null) continue;
                    int count = 0;
                    for (int j = 0; j < objects.Count; j++)
                        if (objects[j].Distant) count++;
                    s_distant[sector] = count;
                }
            }
            Ready = bySector != null;
        }

        private static void Mark(uint sector)
        {
            if (sector < SectorCount) s_changedFrame[sector] = Frame;
        }

        private static void Add(uint sector, int delta)
        {
            if (sector >= SectorCount) return;
            int held = s_distant[sector];
            if (held == Unknown) return;
            held += delta;
            // Arithmetic that has gone below zero has lost track of something; saying so is better than guessing.
            s_distant[sector] = held < 0 ? Unknown : held;
        }

        // A ZDO whose position falls outside the grid is filed in sector zero, and so is one parked outside the zones
        // entirely, which is the same answer vanilla's own SectorToIndex gives.
        private static uint SectorOf(ZDO zdo)
            => zdo == null ? uint.MaxValue : ZoneSystem.GetSectorIndex(zdo.GetPosition()).Sector;
    }
}
