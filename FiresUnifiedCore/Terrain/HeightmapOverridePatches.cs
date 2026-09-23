using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace FiresCore.Terrain
{
    /// <summary>
    /// Replaces Valheim's ±8 m terrain height literals in TerrainComp.LevelTerrain, RaiseTerrain and
    /// ApplyToHeightmap with calls to <see cref="HeightmapOverrideLimits"/>. A method whose bounds are not
    /// found in pairs is left vanilla and reported by <see cref="HeightmapOverrideStatus"/>.
    /// </summary>
    public static class HeightmapOverridePatches
    {
        private static readonly MethodInfo s_min = AccessTools.Method(typeof(HeightmapOverrideLimits), nameof(HeightmapOverrideLimits.Min));
        private static readonly MethodInfo s_minAbs = AccessTools.Method(typeof(HeightmapOverrideLimits), nameof(HeightmapOverrideLimits.MinAbs));
        private static readonly MethodInfo s_max = AccessTools.Method(typeof(HeightmapOverrideLimits), nameof(HeightmapOverrideLimits.Max));

        [HarmonyPatch(typeof(TerrainComp), "LevelTerrain")]
        public static class LevelTerrainPatch
        {
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
                => ReplaceHeightBounds(instructions, "LevelTerrain");
        }

        [HarmonyPatch(typeof(TerrainComp), "RaiseTerrain")]
        public static class RaiseTerrainPatch
        {
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
                => ReplaceHeightBounds(instructions, "RaiseTerrain");
        }

        [HarmonyPatch(typeof(TerrainComp), "ApplyToHeightmap")]
        public static class ApplyToHeightmapPatch
        {
            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
                => ReplaceHeightBounds(instructions, "ApplyToHeightmap");
        }

        private static IEnumerable<CodeInstruction> ReplaceHeightBounds(IEnumerable<CodeInstruction> instructions, string method)
        {
            var codes = new List<CodeInstruction>(instructions);
            var targets = FindBoundTargets(codes, out int lower, out int upper);

            HeightmapOverrideStatus.Record(method, lower, upper);
            if (!HeightmapOverrideStatus.IsPairedClamp(lower, upper)) return codes;

            foreach (var target in targets)
                RetargetToCall(codes[target.Key], target.Value);
            return codes;
        }

        private static Dictionary<int, MethodInfo> FindBoundTargets(List<CodeInstruction> codes, out int lower, out int upper)
        {
            var targets = new Dictionary<int, MethodInfo>();
            lower = 0;
            upper = 0;

            for (int i = 0; i < codes.Count; i++)
            {
                if (codes[i].Is(OpCodes.Ldc_R4, -HeightmapOverrideLimits.VanillaClamp))
                {
                    targets[i] = s_min;
                    lower++;
                }
                else if (codes[i].Is(OpCodes.Ldc_R4, HeightmapOverrideLimits.VanillaClamp))
                {
                    if (IsNegatedByNext(codes, i))
                    {
                        targets[i] = s_minAbs;
                        lower++;
                    }
                    else
                    {
                        targets[i] = s_max;
                        upper++;
                    }
                }
            }
            return targets;
        }

        private static bool IsNegatedByNext(List<CodeInstruction> codes, int index)
        {
            if (index + 1 >= codes.Count) return false;
            var next = codes[index + 1].opcode;
            return next == OpCodes.Sub || next == OpCodes.Neg;
        }

        private static void RetargetToCall(CodeInstruction code, MethodInfo target)
        {
            code.opcode = OpCodes.Call;
            code.operand = target;
        }
    }
}
