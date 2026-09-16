using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;

namespace FiresCore.Terrain
{
    /// <summary>
    /// Replaces the hard-coded 8 m height clamps in TerrainComp.LevelTerrain, RaiseTerrain and ApplyToHeightmap with
    /// the limits from <see cref="HeightmapOverrideConfig"/>, like HeightmapUnlimited but without Jotunn. Inactive
    /// while <see cref="HeightmapOverrideConfig.Enabled"/> is false.
    /// </summary>
    [HarmonyPatch(typeof(TerrainComp))]
    public static class HeightmapOverridePatches
    {
        // LevelTerrain

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

        // RaiseTerrain

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

        // ApplyToHeightmap

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
