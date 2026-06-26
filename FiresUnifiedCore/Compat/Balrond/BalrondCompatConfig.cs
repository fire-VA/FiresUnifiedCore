using BepInEx.Configuration;
using FiresCore.Sync;

namespace FiresCore.Compat.Balrond
{
    /// <summary>
    /// Server-locked config gating Core's Balrond compatibility patches. Each entry corresponds to
    /// one specific Balrond behavior we want to neutralize; default is "patch active" so a fresh
    /// install benefits from the fixes without any tuning. Admins can flip individual gates off if
    /// they want vanilla-Balrond behavior back.
    /// </summary>
    public static class BalrondCompatConfig
    {
        /// <summary>
        /// When true, reverts BalrondAmazingNature's OR-addition of <c>DeepNorth</c> and <c>AshLands</c>
        /// biomes to vanilla <c>Mistlands_*</c> location prefabs. Those prefabs carry MistEmitter
        /// children — placing them outside Mistlands brings mistlands fog with them, the user-reported
        /// "mistlands mist in other biomes" issue. Source: BalrondNature.LocationBuilder.editLocation.
        /// </summary>
        public static ConfigEntry<bool> RestrictMistlandsLocations;

        public static void Initialize(ConfigFile config)
        {
            RestrictMistlandsLocations = config.Bind(
                "BalrondCompat", "RestrictMistlandsLocations", true,
                "When true, BalrondAmazingNature's added DeepNorth/AshLands biomes are stripped from " +
                "Mistlands_* location prefabs at ZoneSystem.SetupLocations postfix time. Prevents " +
                "the mistlands towers/viaducts/lighthouses/etc. — which carry MistEmitter children — " +
                "from spawning in DeepNorth or Ashlands and dragging mistlands fog with them. " +
                "[Synced with Server]");
        }

        public static void BindToSync(ConfigSync configSync)
        {
            if (configSync == null || RestrictMistlandsLocations == null) return;
            configSync.AddConfigEntry(RestrictMistlandsLocations);
        }
    }
}
