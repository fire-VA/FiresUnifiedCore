using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace FiresCore.Terrain
{
    /// <summary>
    /// Harmony transpiler patches that replace the ±8 hard-coded height clamps
    /// inside <see cref="TerrainComp"/> with configurable values from
    /// <see cref="HeightmapOverrideConfig"/>.
    ///
    /// Mirrors the logic of the original HeightmapUnlimited mod but with no
    /// Jotunn dependency.
    ///
    /// Patched methods:
    ///   - TerrainComp.LevelTerrain       — replaces -8f ? Min(),  +8f ? Max()
    ///   - TerrainComp.RaiseTerrain       — replaces -8f ? Min(),  +8f ? Max()
    ///   - TerrainComp.ApplyToHeightmap   — replaces -8f ? Min(),
    ///                                               first +8f ? MinAbs(),
    ///                                               subsequent +8f ? Max()
    ///
    /// All patches are no-ops when <see cref="HeightmapOverrideConfig.Enabled"/> is false.
    /// </summary>
    [HarmonyPatch(typeof(TerrainComp))]
    public static class HeightmapOverridePatches
    {
        // ?????? LevelTerrain ??????

        [HarmonyTranspiler]
        [HarmonyPatch("LevelTerrain")]
        public static IEnumerable<CodeInstruction> LevelTerrain_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            if (HeightmapOverrideConfig.Enabled == null || !HeightmapOverrideConfig.Enabled.Value)
            {
                foreach (var instr in instructions) yield return instr;
                yield break;
            }

            foreach (var instr in instructions)
            {
                if (instr.Is(OpCodes.Ldc_R4, (object)(-8f)))
                    yield return new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(HeightmapOverrideConfig), nameof(HeightmapOverrideConfig.Min)));
                else if (instr.Is(OpCodes.Ldc_R4, (object)(8f)))
                    yield return new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(HeightmapOverrideConfig), nameof(HeightmapOverrideConfig.Max)));
                else
                    yield return instr;
            }
        }

        // ?????? RaiseTerrain ??????

        [HarmonyTranspiler]
        [HarmonyPatch("RaiseTerrain")]
        public static IEnumerable<CodeInstruction> RaiseTerrain_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            if (HeightmapOverrideConfig.Enabled == null || !HeightmapOverrideConfig.Enabled.Value)
            {
                foreach (var instr in instructions) yield return instr;
                yield break;
            }

            foreach (var instr in instructions)
            {
                if (instr.Is(OpCodes.Ldc_R4, (object)(-8f)))
                    yield return new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(HeightmapOverrideConfig), nameof(HeightmapOverrideConfig.Min)));
                else if (instr.Is(OpCodes.Ldc_R4, (object)(8f)))
                    yield return new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(HeightmapOverrideConfig), nameof(HeightmapOverrideConfig.Max)));
                else
                    yield return instr;
            }
        }

        // ?????? ApplyToHeightmap ??????

        [HarmonyTranspiler]
        [HarmonyPatch("ApplyToHeightmap")]
        public static IEnumerable<CodeInstruction> ApplyToHeightmap_Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            if (HeightmapOverrideConfig.Enabled == null || !HeightmapOverrideConfig.Enabled.Value)
            {
                foreach (var instr in instructions) yield return instr;
                yield break;
            }

            // The first occurrence of +8f in ApplyToHeightmap must resolve to MinAbs()
            // (the absolute value of the minimum) rather than Max(), matching the
            // original HeightmapUnlimited behaviour exactly.
            int positiveCount = 0;

            foreach (var instr in instructions)
            {
                if (instr.Is(OpCodes.Ldc_R4, (object)(-8f)))
                {
                    yield return new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(HeightmapOverrideConfig), nameof(HeightmapOverrideConfig.Min)));
                }
                else if (instr.Is(OpCodes.Ldc_R4, (object)(8f)))
                {
                    if (positiveCount == 0)
                        yield return new CodeInstruction(OpCodes.Call,
                            AccessTools.Method(typeof(HeightmapOverrideConfig), nameof(HeightmapOverrideConfig.MinAbs)));
                    else
                        yield return new CodeInstruction(OpCodes.Call,
                            AccessTools.Method(typeof(HeightmapOverrideConfig), nameof(HeightmapOverrideConfig.Max)));

                    positiveCount++;
                }
                else
                {
                    yield return instr;
                }
            }
        }
    }
}
