using System;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.Lifecycle
{
    /// <summary>
    /// Unity builds a Collision object for every contact it reports unless collision callbacks are told to reuse one.
    /// Valheim ships with reuse off (DynamicsManager m_ReuseCollisionCallbacks: 0), and a 15-minute client profile
    /// measured 476,000 of them, about 95 MB of garbage. Every collision handler in the game and in this family reads
    /// what it needs inside the callback, so reuse is safe here. A handler that kept the Collision past its own callback
    /// would see it change under it, which is why this can be turned off per machine.
    /// </summary>
    internal static class CollisionCallbackReuse
    {
        private const string Section = "Performance";
        private const string Key = "Reuse Collision Callbacks";
        private const string Description =
            "ON by default. Tells Unity to hand every collision callback the same Collision object instead of building a " +
            "new one per contact report, which a 15-minute profile measured at 476,000 objects. Vanilla's own handlers and " +
            "every Fires handler read what they need inside the callback, so this changes nothing they see. Turn it OFF if " +
            "another mod keeps a Collision past its callback and starts misbehaving. This machine only; read live.";

        internal static void Initialize(ConfigFile config)
        {
            try
            {
                ConfigEntry<bool> reuse = config.Bind(Section, Key, true, Description);
                Apply(reuse.Value);
                reuse.SettingChanged += (_, __) => Apply(reuse.Value);
            }
            catch (Exception ex)
            {
                FiresUnifiedCore.Log.LogWarning($"[Physics] collision callback reuse left as the game set it: {ex.Message}");
            }
        }

        private static void Apply(bool reuse)
        {
            if (Physics.reuseCollisionCallbacks == reuse) return;
            Physics.reuseCollisionCallbacks = reuse;
            FiresUnifiedCore.Log.LogInfo(reuse
                ? "[Physics] collision callbacks now reuse one object instead of allocating one per contact report."
                : "[Physics] collision callbacks allocate one object per contact report, as the game ships.");
        }
    }
}
