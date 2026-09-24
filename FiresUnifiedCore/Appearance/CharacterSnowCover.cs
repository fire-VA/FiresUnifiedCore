using HarmonyLib;
using UnityEngine;

namespace FiresCore.Appearance
{
    /// <summary>
    /// Snow on a character's gear, on the hook Valheim 1.0 shipped and never wired up.
    ///
    /// VisEquipment exposes a public SnowLevel setter that clamps 0..1 and pushes _SnowCover to every renderer under
    /// the character through MaterialMan (VisEquipment.cs:1134-1147 in the 1.0 decompile). Nothing in the assembly
    /// calls it - the property has no caller at all - so the shader work is present and inert. This drives it from the
    /// same conditions vanilla uses for building pieces, so a player crossing the Deep North whitens the way the
    /// structures around them do.
    ///
    /// The piece rules it mirrors, from WearNTear.UpdateWearNTear and CanHaveSnow:
    ///   * Deep North only. Accumulation sits inside `if (m_biome == DeepNorth)` (WearNTear.cs:407), so nowhere else
    ///     in the world grows snow, and Fire declined a Mountain equivalent.
    ///   * Roofed means none, matching CanHaveSnow's `!m_haveRoof`.
    ///   * The rate is the environment's own snowfall, EnvMan.GetSnowBuildup() times Game.m_snowBuildupSpeed, so
    ///     gear and buildings whiten at the same speed under the same weather and a clear Deep North night adds
    ///     nothing to either.
    ///
    /// Where it deliberately differs: pieces never shed (only a player brushing past removes it, and only in Deep
    /// North), but a character that walks indoors or leaves the biome must, or the snow would be permanent. Shedding
    /// uses the same rate so the two directions stay symmetric. It also skips vanilla's ShieldGenerator test, which
    /// CanHaveSnow applies to pieces - a shielded base already reads as roofed for anyone standing inside a building,
    /// and the per-character shield query is not worth its cost every tick.
    ///
    /// Visual only, local only, and entirely client-side: _SnowCover is a material property, nothing is written to a
    /// ZDO and nothing is sent to a peer. On a headless server VisEquipment has no renderers and MaterialMan has no
    /// instance, so the patch gates itself off there.
    /// </summary>
    [HarmonyPatch]
    public static class CharacterSnowCover
    {
        /// <summary>Set false to leave gear untouched; the level is driven back to zero once, then left alone.</summary>
        public static bool Enabled = true;

        private const float FullCover = 1f;
        private const float NoCover = 0f;

        [HarmonyPatch(typeof(Character), nameof(Character.CustomFixedUpdate))]
        [HarmonyPostfix]
        private static void Apply(Character __instance, float dt)
        {
            if (__instance == null || Application.isBatchMode) return;
            if (MaterialMan.instance == null) return;

            var visEquipment = __instance.GetComponent<VisEquipment>();
            if (visEquipment == null) return;

            float level = visEquipment.SnowLevel;
            if (!Enabled)
            {
                if (level > NoCover) visEquipment.SnowLevel = NoCover;
                return;
            }

            float target = Gathers(__instance) ? FullCover : NoCover;
            if (Mathf.Approximately(level, target)) return;

            float step = (target > level ? GatherRate() : ShedRate()) * dt;
            if (step <= NoCover) return;
            visEquipment.SnowLevel = Mathf.MoveTowards(level, target, step);
        }

        private static bool Gathers(Character character)
        {
            return character.m_lastBiome == Heightmap.Biome.DeepNorth && !character.IsUnderRoof();
        }

        // Gathering is the weather own snowfall, so gear whitens in step with the buildings around it and a clear
        // Deep North night adds nothing to either.
        private static float GatherRate()
        {
            var env = EnvMan.instance;
            return env == null ? NoCover : env.GetSnowBuildup() * BaseRate();
        }

        // Shedding must NOT read the snowfall, or a character who whitened in a blizzard and then walked somewhere
        // clear would keep the snow forever - GetSnowBuildup is zero in fair weather, so the step would be zero and
        // the level would never fall. It runs at the unscaled build-up speed, which is the gathering rate at full
        // snowfall, so the two directions stay comparable without shedding depending on the sky.
        private static float ShedRate() => BaseRate();

        private static float BaseRate()
        {
            var game = Game.instance;
            return game == null ? NoCover : game.m_snowBuildupSpeed;
        }
    }
}
