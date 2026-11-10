using FiresCore.Config;

namespace FiresCore.Perf
{
    // 0.2.1182 small gains; 0.2.1217 ([generator], Fire 11-08: perf only in FGN / EBM / FiresFixes): the cooking slot keys and the
    // station range checks moved to FiresFixes 0.1.70 (its own keys) and are gone from Core. Only "One VisEquipment verdict per frame"
    // stays: it is Core's own companion code path (CompanionPatches), not a vanilla optimization, and FiresFixes does not port it.
    internal static class SmallGains
    {
        private const string Section = "Performance";
        private struct Cached { public int At; public bool On; public bool Valid; }
        private static Cached s_visVerdict;

        private static bool Read(ref Cached c, string key)
        {
            int now = System.Environment.TickCount;
            if (c.Valid && now - c.At < 1000) return c.On;
            c.On = CoreSettings.Get(Section, key, true); c.At = now; c.Valid = true;
            return c.On;
        }

        internal static bool VisVerdict => Read(ref s_visVerdict, "One VisEquipment verdict per frame");
    }
}
