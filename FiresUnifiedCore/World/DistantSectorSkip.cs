using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;

namespace FiresCore.World
{
    /// <summary>
    /// Deciding what to draw around a player walks every object in the fifty-odd sectors ringing them, looking for the
    /// handful flagged as visible from far away - birds, ships, the occasional tall structure. In a settlement that is
    /// tens of thousands of objects examined and about ninety-nine in a hundred thrown away, thirty times a second.
    ///
    /// SectorChanges already counts how many far-away objects each sector holds, so a sector holding none can be passed
    /// over without looking. It is allowed to do that only because that count checks itself against a real count and has
    /// never yet disagreed; a count that was wrong the wrong way would make distant things quietly stop appearing.
    ///
    /// Servers are left alone: their equivalent pass belongs to the networking mod, which answers this question its own
    /// way, and two answers to one question is how a silent disagreement starts.
    /// </summary>
    [HarmonyPatch]
    internal static class DistantSectorSkip
    {
        private const string Section = "Performance";
        private const string Key = "Skip Empty Distant Sectors";
        private const string Description =
            "ON by default. Choosing what to draw around you examines every object in the ring of world sectors " +
            "surrounding you to find the few that are visible from a distance, and throws away almost all of them. " +
            "With this on, a sector known to hold none of them is passed over instead. The count it relies on checks " +
            "itself continuously and says so in the log. Turn it OFF if far-away objects stop appearing. Clients only; " +
            "this machine only; read live.";

        private static ConfigEntry<bool> s_enabled;

        internal static long Skipped;
        internal static long Walked;

        internal static void Initialize(ConfigFile config)
        {
            s_enabled = config.Bind(Section, Key, true, Description);
        }

        [HarmonyPatch(typeof(ZDOMan), "FindDistantObjects"), HarmonyPrefix]
        static bool FindDistantObjects_Prefix(Vector2s sector, HashSet<ZoneSystem.SectorIndex> visitedSectorIndices)
        {
            if (s_enabled == null || !s_enabled.Value || !SectorChanges.Ready) return true;
            if (ZNet.instance == null || ZNet.instance.IsServer()) return true;

            var index = ZoneSystem.SectorToIndex(sector);
            // Vanilla leaves immediately for a sector it has already been to, so this has to as well.
            if (visitedSectorIndices.Contains(index)) return false;

            // Anything other than a confident zero - including "cannot say" - gets walked.
            if (!SectorChanges.HasNoDistantObjects(index.Sector))
            {
                Walked++;
                return true;
            }

            // Vanilla marks a sector it has looked at, and this has looked at it, by knowing there was nothing in it.
            visitedSectorIndices.Add(index);
            Skipped++;
            return false;
        }

        /// <summary>The tally since the last caller asked, or null when there is nothing to say.</summary>
        internal static string TakeReportLine()
        {
            long total = Skipped + Walked;
            if (total == 0) return null;
            string line = $"[Sectors] distant-object search: {Walked:N0} sectors examined, {Skipped:N0} passed over "
                + $"({Skipped * 100 / total}%).";
            Skipped = 0;
            Walked = 0;
            return line;
        }
    }
}
