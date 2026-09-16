using BepInEx.Configuration;
using FiresCore.Sync;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Opt-in hard enforcement of the movement single-writer rule. Off by default, the SetMoveDir prefix only blocks
    /// kinematic bodies and frozen states; on, it also drops any companion SetMoveDir that didn't come from
    /// <see cref="UnifiedMovementAuthority"/>, with vanilla MoveTo pathfinding let through. Verify in game before
    /// relying on it; turning it off reverts instantly.
    /// </summary>
    public static class MovementGateConfig
    {
        public static ConfigEntry<bool> EnforceSingleWriterMovement;

        public static void Initialize(ConfigFile config)
        {
            EnforceSingleWriterMovement = config.Bind(
                "Companions", "EnforceSingleWriterMovement", false,
                "ADVANCED. Hard-enforce the companion movement single-writer rule: any Character.SetMoveDir " +
                "for a companion that does not come from the movement authority (or the vanilla-pathfinding " +
                "escape hatch) is dropped. Default false (cooperative — today's behavior). Only enable after " +
                "verifying follow / patrol / work-behavior movement still works in-game; set back to false to " +
                "revert instantly. [Synced with Server]");
        }

        public static void BindToSync(ConfigSync configSync)
        {
            if (configSync != null && EnforceSingleWriterMovement != null)
                configSync.AddConfigEntry(EnforceSingleWriterMovement);
        }

        /// <summary>Cheap hot-path read (ConfigEntry.Value is a backing field). False until bound.</summary>
        public static bool Enabled => EnforceSingleWriterMovement != null && EnforceSingleWriterMovement.Value;
    }
}
