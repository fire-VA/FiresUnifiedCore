using BepInEx.Configuration;
using UnityEngine;

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
        // Advanced settings for the colour, intensity and opacity of the effect rings.
        public static ConfigEntry<Color> DamageColor, HealColor, BuffColor, InvalidColor;
        public static ConfigEntry<float> RingIntensity, RingFillOpacity;
        private static ConfigDescription Adv(string text, AcceptableValueBase range = null) =>
            new ConfigDescription(text, range, new FiresCore.Sync.ConfigurationManagerAttributes { IsAdvanced = true });

        private static bool _initialized;

        public static bool Initialized => _initialized;

        public static void Initialize(ConfigFile config)
        {
            if (config == null || _initialized) return;

            RingOpacity = config.Bind(Section, "RingOpacity", DefaultRingOpacity,
                Adv("How see-through the ability circles on the ground are overall (1 = fully visible).",
                    new AcceptableValueRange<float>(MinRingOpacity, MaxRingOpacity)));
            RingFillOpacity = config.Bind(Section, "Ring Fill Opacity", 0.38f,
                Adv("How solid the inside of the circle is. The edge stays solid.", new AcceptableValueRange<float>(0f, 1f)));
            RingIntensity = config.Bind(Section, "Ring Intensity", 1f,
                Adv("How bright the circles are. Above 1 they glow.", new AcceptableValueRange<float>(0.25f, 3f)));
            DamageColor = config.Bind(Section, "Damage Ring Color", GroundRingPalette.ColorFor(GroundRingKind.Damage, false),
                Adv("Colour of damage circles (enemy attacks, your damage skills)."));
            HealColor = config.Bind(Section, "Heal Ring Color", GroundRingPalette.ColorFor(GroundRingKind.Heal, false),
                Adv("Colour of healing circles."));
            BuffColor = config.Bind(Section, "Buff Ring Color", GroundRingPalette.ColorFor(GroundRingKind.Buff, false),
                Adv("Colour of buff circles."));
            InvalidColor = config.Bind(Section, "Invalid Ring Color", GroundRingPalette.ColorFor(GroundRingKind.Invalid, false),
                Adv("Colour of the aiming circle where a skill cannot be placed."));

            ColorBlindPalette = config.Bind(Section, "ColorBlindPalette", DefaultColorBlindPalette,
                "Use the colour-blind palette for the ground circles: heal cyan, buff violet, damage orange (overrides the colours below).");

            ShowOwnDamageRings = config.Bind(Section, "ShowOwnDamageRings", DefaultShowOwnDamageRings,
                "Draw the red damage ring for your own placed abilities. Off hides it once the cast commits; " +
                "the aiming preview is always drawn.");

            MaxConcurrentRings = config.Bind(Section, "MaxConcurrentRings", DefaultMaxConcurrentRings,
                new ConfigDescription(
                    "How many ground rings may draw at once. The oldest ring is recycled when the cap is reached.",
                    new AcceptableValueRange<int>(MinConcurrentRings, MaxConcurrentRingsLimit)));

            // Companion abilities draw these rings; without a companion host the keys leave the config.
            FiresCore.Config.OwnerGatedConfig.Gate(FiresCore.Config.OwnerGatedConfig.Owners.GROUNDRINGS, false,
                RingOpacity, ColorBlindPalette, ShowOwnDamageRings, MaxConcurrentRings,
                RingFillOpacity, RingIntensity, DamageColor, HealColor, BuffColor, InvalidColor);

            _initialized = true;
        }

        public static float Opacity => RingOpacity?.Value ?? DefaultRingOpacity;
        public static bool UseColorBlindPalette => ColorBlindPalette?.Value ?? DefaultColorBlindPalette;
        public static bool OwnDamageRingsVisible => ShowOwnDamageRings?.Value ?? DefaultShowOwnDamageRings;
        public static int ConcurrentRingCap => MaxConcurrentRings?.Value ?? DefaultMaxConcurrentRings;
        public static float FillOpacity => RingFillOpacity?.Value ?? 0.38f;
        public static float Intensity => RingIntensity?.Value ?? 1f;

        /// <summary>The colour a ring of this kind draws in: the colour-blind palette when that is on, else the player's colour.</summary>
        public static Color ColorFor(GroundRingKind kind)
        {
            if (UseColorBlindPalette) return GroundRingPalette.ColorFor(kind, true);
            ConfigEntry<Color> e = kind == GroundRingKind.Damage ? DamageColor : kind == GroundRingKind.Heal ? HealColor
                : kind == GroundRingKind.Buff ? BuffColor : kind == GroundRingKind.Invalid ? InvalidColor : null;
            return e != null ? e.Value : GroundRingPalette.ColorFor(kind, false);
        }
    }
}
