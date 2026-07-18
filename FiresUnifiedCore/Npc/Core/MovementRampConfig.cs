using BepInEx.Configuration;
using FiresCore.Sync;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Config for the companion locomotion speed-ramp (CompanionSpeedRamp), ported from FiresValcast's
    /// NaturalWalk. Eases a companion's run speed up from a slow start to full instead of snapping to it
    /// (and eases back down on stop), ramping between the companion's OWN walk and run speed - so it scales
    /// correctly now that archetypes push run speed high. Purely cosmetic locomotion polish.
    /// </summary>
    public static class MovementRampConfig
    {
        public static ConfigEntry<bool> Enable;
        public static ConfigEntry<float> RampSeconds;

        public static void Initialize(ConfigFile config)
        {
            Enable = config.Bind(
                "Companions", "SmoothSpeedRamp", true,
                "Ease companion run speed up to full instead of snapping to it (and ease back down when they " +
                "stop), ramping between their own walk and run speed. Cosmetic locomotion polish. [Synced with Server]");

            RampSeconds = config.Bind(
                "Companions", "SpeedRampSeconds", 1.0f,
                new ConfigDescription(
                    "Seconds for a companion to ramp from walk speed up to full run speed (and back). Lower = " +
                    "snappier, higher = more gradual. [Synced with Server]",
                    new AcceptableValueRange<float>(0.1f, 5f)));
        }

        public static void BindToSync(ConfigSync configSync)
        {
            if (configSync == null) return;
            if (Enable != null) configSync.AddConfigEntry(Enable);
            if (RampSeconds != null) configSync.AddConfigEntry(RampSeconds);
        }

        /// <summary>Cheap hot-path read (backing field). False until bound.</summary>
        public static bool Enabled => Enable != null && Enable.Value;
        public static float Seconds => RampSeconds != null ? RampSeconds.Value : 1.0f;
    }
}
