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
        // 1086 ([generator], Fire 11-01: configs live in their own mods): bound and synced by FiresCompanions; Core reads the owner's
        // value (old default without it). Same member names; .Value still reads (type is now OwnedSetting<T>).
        public static readonly FiresCore.Config.OwnedSetting<bool> Enable = new FiresCore.Config.OwnedSetting<bool>("Companions", "SmoothSpeedRamp", true);
        public static readonly FiresCore.Config.OwnedSetting<float> RampSeconds = new FiresCore.Config.OwnedSetting<float>("Companions", "SpeedRampSeconds", 1.0f);

        public static void Initialize(ConfigFile config) { }

        public static void BindToSync(ConfigSync configSync) { }

        /// <summary>Hot-path read (cached while the owner's key has a change event).</summary>
        public static bool Enabled => Enable.Value;
        public static float Seconds => RampSeconds.Value;
    }
}
