using System.Collections.Generic;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// How many hits and heals each class effect has landed on this peer since the last reset. class_test casts once and
    /// reads the count to prove an effect lands once per tick, not once per peer (the ownership rule in AbilityRPCManager).
    /// </summary>
    public static class ClassEffectLedger
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<string, int> _hits = new Dictionary<string, int>();
        private static readonly Dictionary<string, int> _heals = new Dictionary<string, int>();

        public static void Record(string effectName, bool heal)
        {
            if (string.IsNullOrEmpty(effectName)) return;
            lock (_lock)
            {
                Dictionary<string, int> counts = heal ? _heals : _hits;
                counts.TryGetValue(effectName, out int count);
                counts[effectName] = count + 1;
            }
        }

        public static int Hits(string effectName)
        {
            lock (_lock) return _hits.TryGetValue(effectName, out int count) ? count : 0;
        }

        public static int Heals(string effectName)
        {
            lock (_lock) return _heals.TryGetValue(effectName, out int count) ? count : 0;
        }

        public static void Reset()
        {
            lock (_lock)
            {
                _hits.Clear();
                _heals.Clear();
            }
        }
    }
}
