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

        /// <summary>
        /// When true, restricts the <c>MistArea</c> / <c>MistArea_edge</c> mist-volume vegetation to the
        /// Mistlands biome only. The shipped Expand World vegetation config force-places these across
        /// Mountain / BlackForest / Swamp / Plains — the actual cause of "mistlands mist everywhere".
        /// </summary>
        public static ConfigEntry<bool> RestrictMistAreaVegetation;

        /// <summary>
        /// When true, disables the <c>vfx_swamp_mist</c> vegetation entry so the new swamp ground-fog
        /// never spawns.
        /// </summary>
        public static ConfigEntry<bool> RemoveSwampFog;

        /// <summary>
        /// When true, blocks <c>SE_MistSickness</c> (the BalrondAmazingNature mist debuff: −15% move
        /// speed / −10% stamina regen while standing in mist) from ever being applied. Source:
        /// BalrondNature.MistSicknessPatches + StatusEffectFactory.CreateMistSicknessStatusEffect.
        /// </summary>
        public static ConfigEntry<bool> NegateMistSicknessDebuff;

        /// <summary>
        /// Multiplier applied to the placement density (m_min / m_max) of every <c>PoisonGeyser*</c>
        /// vegetation entry. 0.5 = half as many geysers (the default), 1.0 = leave Balrond's values
        /// untouched. Covers both the geyser hazard and its blob-spawner.
        /// </summary>
        public static ConfigEntry<float> PoisonGeyserDensityScale;

        public static void Initialize(ConfigFile config)
        {
            RestrictMistlandsLocations = config.Bind(
                "BalrondCompat", "RestrictMistlandsLocations", true,
                "When true, BalrondAmazingNature's added DeepNorth/AshLands biomes are stripped from " +
                "Mistlands_* location prefabs at ZoneSystem.SetupLocations postfix time. Prevents " +
                "the mistlands towers/viaducts/lighthouses/etc. — which carry MistEmitter children — " +
                "from spawning in DeepNorth or Ashlands and dragging mistlands fog with them. " +
                "[Synced with Server]");

            RestrictMistAreaVegetation = config.Bind(
                "BalrondCompat", "RestrictMistAreaVegetation", true,
                "When true, the MistArea / MistArea_edge mist-volume vegetation is restricted to the " +
                "Mistlands biome only. The shipped Expand World vegetation config force-places these " +
                "mistlands mist volumes across Mountain, BlackForest, Swamp and Plains — this is what " +
                "puts mistlands mist all over the map. [Synced with Server]");

            RemoveSwampFog = config.Bind(
                "BalrondCompat", "RemoveSwampFog", true,
                "When true, the vfx_swamp_mist vegetation entry is disabled so BalrondAmazingNature's " +
                "added swamp ground-fog never spawns. [Synced with Server]");

            NegateMistSicknessDebuff = config.Bind(
                "BalrondCompat", "NegateMistSicknessDebuff", true,
                "When true, the SE_MistSickness status effect (−15% move speed / −10% stamina regen " +
                "while in mist) is blocked from being applied to players. [Synced with Server]");

            PoisonGeyserDensityScale = config.Bind(
                "BalrondCompat", "PoisonGeyserDensityScale", 0.5f,
                new ConfigDescription(
                    "Scales the placement density of every PoisonGeyser* vegetation entry. 0.5 halves " +
                    "the number of poison geysers (and their blob-spawners); 1.0 leaves Balrond's values " +
                    "untouched. Applied once per ZoneSystem at SetupLocations. [Synced with Server]",
                    new AcceptableValueRange<float>(0f, 1f)));
        }

        public static void BindToSync(ConfigSync configSync)
        {
            if (configSync == null) return;
            if (RestrictMistlandsLocations != null) configSync.AddConfigEntry(RestrictMistlandsLocations);
            if (RestrictMistAreaVegetation != null) configSync.AddConfigEntry(RestrictMistAreaVegetation);
            if (RemoveSwampFog != null) configSync.AddConfigEntry(RemoveSwampFog);
            if (NegateMistSicknessDebuff != null) configSync.AddConfigEntry(NegateMistSicknessDebuff);
            if (PoisonGeyserDensityScale != null) configSync.AddConfigEntry(PoisonGeyserDensityScale);
        }
    }
}
