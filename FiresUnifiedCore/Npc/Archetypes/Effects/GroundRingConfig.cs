using BepInEx.Configuration;

namespace FiresCore.Npc.Archetypes.Effects
{
    /// <summary>
    /// Client-side presentation settings for the ground rings. Deliberately NOT server-synced: what a ring looks
    /// like is the viewer's choice, and the visibility table is evaluated on each client anyway.
    /// </summary>
    public static class GroundRingConfig
    {
        private const string Section = "GroundRings";

        private const float DefaultRingOpacity = 0.85f;
        private const float MinRingOpacity = 0.1f;
        private const float MaxRingOpacity = 1f;
        private const bool DefaultColorBlindPalette = false;
        private const bool DefaultShowOwnDamageRings = true;
        private const int DefaultMaxConcurrentRings = 12;
        private const int MinConcurrentRings = 1;
        private const int MaxConcurrentRingsLimit = 48;

        public static ConfigEntry<float> RingOpacity;
        public static ConfigEntry<bool> ColorBlindPalette;
        public static ConfigEntry<bool> ShowOwnDamageRings;
        public static ConfigEntry<int> MaxConcurrentRings;

        private static bool _initialized;

        public static bool Initialized => _initialized;

        public static void Initialize(ConfigFile config)
        {
            if (config == null || _initialized) return;

            RingOpacity = config.Bind(Section, "RingOpacity", DefaultRingOpacity,
                new ConfigDescription(
                    "Peak opacity of ability ground rings. Lower it if rings wash out the ground.",
                    new AcceptableValueRange<float>(MinRingOpacity, MaxRingOpacity)));

            ColorBlindPalette = config.Bind(Section, "ColorBlindPalette", DefaultColorBlindPalette,
                "Use the colour-blind ring palette: heal cyan, buff violet, damage orange. " +
                "Each ring also has its own motion pattern, so colour is never the only cue.");

            ShowOwnDamageRings = config.Bind(Section, "ShowOwnDamageRings", DefaultShowOwnDamageRings,
                "Draw the red damage ring for your own placed abilities. Off hides it once the cast commits; " +
                "the aiming preview is always drawn.");

            MaxConcurrentRings = config.Bind(Section, "MaxConcurrentRings", DefaultMaxConcurrentRings,
                new ConfigDescription(
                    "How many ground rings may draw at once. The oldest ring is recycled when the cap is reached.",
                    new AcceptableValueRange<int>(MinConcurrentRings, MaxConcurrentRingsLimit)));

            _initialized = true;
        }

        public static float Opacity => RingOpacity?.Value ?? DefaultRingOpacity;
        public static bool UseColorBlindPalette => ColorBlindPalette?.Value ?? DefaultColorBlindPalette;
        public static bool OwnDamageRingsVisible => ShowOwnDamageRings?.Value ?? DefaultShowOwnDamageRings;
        public static int ConcurrentRingCap => MaxConcurrentRings?.Value ?? DefaultMaxConcurrentRings;
    }
}
