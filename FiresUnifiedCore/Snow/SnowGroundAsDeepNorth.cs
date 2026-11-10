using System;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// 0.2.829 ([seasons], Fire 10-26: "both the decompiled winter mod and VANILLA DEEP NORTH allow for snow to pile up in thick
    /// layers"): in a deep-snow base (<see cref="SnowField.DeepGroundAt"/>: painted / overridden Deep North, Winter territories) the
    /// ground answers exactly as vanilla Deep North's. Vanilla reads the paint-mask G as snow depth only where Heightmap.GetBiome
    /// (the chunk's CORNER biomes, never our paint) says DeepNorth:
    ///  - Heightmap.GetHeightOffset: Lerp(Snow 0.1 m, SnowVeryDeep 2.1 m, G) (Heightmap.cs:981-1003; camera, logs, floating, sink);
    ///  - Heightmap.GetGroundMaterial: G > 0.6 SnowVeryDeep, > 0.12 SnowDeep (:1224-1242; footsteps, effects);
    ///  - Character.m_lastBiome (set in UpdateLava, :959): the deep-snow slow + walk trail (:1694, :2794).
    /// Each postfix only changes a result vanilla gave for a NON-Deep-North biome where DeepGroundAt is true; real Deep North and
    /// everything else stay vanilla. The depth G itself comes from <see cref="SnowHeightmapInject"/> (local runtime texture).
    /// The terrain LOOK: vanilla's Custom/Heightmap raises G only inside the Deep North world arc (SnowMeshRaise does it outside); the colour is FAT BiomePaint's ground
    /// recolour, not here.
    /// </summary>
    internal static class SnowGroundAsDeepNorth
    {
        private static readonly AccessTools.FieldRef<Character, Heightmap.Biome> s_lastBiome = AccessTools.FieldRefAccess<Character, Heightmap.Biome>("m_lastBiome");
        private static int s_offsets, s_materials, s_chars;
        private static float s_summaryAt;

        private static bool Applies(Heightmap hm, Vector3 p, out Heightmap.Biome vanilla)
        {
            vanilla = hm.GetBiome(p);
            if (SnowVedrGround.Owns) return false;   // 0.2.1197: Vedr answers the sink / footsteps / slow off the Deep North itself
            return vanilla != Heightmap.Biome.DeepNorth && vanilla != Heightmap.Biome.AshLands && (SnowField.DeepGroundAt(p) || SnowField.WinterGroundAt(p));   // 0.2.1052: + seasonal winter ground (Fire: "paint seasonal-winter ground as snow")
        }

        // 0.2.1060 (lead, Fire's RenderDoc: the terrain shader samples the paint mask as sRGB): where CORE owns the snow (not the real Deep
        // North; deep ground, seasonal winter ground or Mountain) Core writes the snow G encoded, so every CPU reader of it - vanilla's
        // deep-snow slow (Character.UpdateWalking, GetCultivationMask x 2 m), the sink (GetHeightOffset), footsteps and the Mountain trail
        // test below - gets the DECODED value here: the height you see is the height you sink in. One place, no per-caller decode.
        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.GetCultivationMask))]
        private static class CultivationMaskDecode
        {
            private static void Postfix(Vector3 worldPos, ref float __result)
            {
                try
                {
                    if (__result <= 0f || !SnowConfig.RealHeight || !SnowField.CoreGroundOn) return;
                    if (WorldGenerator.IsDeepnorth(worldPos.x, worldPos.z)) return;   // vanilla snow, vanilla raw G
                    if (!SnowField.DeepGroundAt(worldPos) && !SnowField.WinterGroundAt(worldPos) && SnowField.MountainWeight(worldPos) <= 0f) return;
                    __result = SnowHeightmapInject.Decode(__result);
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.GetHeightOffset))]
        private static class Offset
        {
            private static void Postfix(Heightmap __instance, Vector3 worldPos, ref float __result)
            {
                try
                {
                    if (__result != 0f || !Applies(__instance, worldPos, out _)) return;
                    __result = Mathf.Lerp(Heightmap.GetGroundMaterialOffset(FootStep.GroundMaterial.Snow), Heightmap.GetGroundMaterialOffset(FootStep.GroundMaterial.SnowVeryDeep), __instance.GetCultivationMask(worldPos));
                    s_offsets++; Summary();
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Heightmap), nameof(Heightmap.GetGroundMaterial))]
        private static class Material
        {
            private static void Postfix(Heightmap __instance, Vector3 groundNormal, Vector3 point, ref FootStep.GroundMaterial __result)
            {
                try
                {
                    if (!Applies(__instance, point, out var vb))
                    {
                        // seasonal snow ([seasons], Fire: "snow from the seasons needs to kick up the snow footstep fx properly"): outside a snow
                        // base the only cover is Vedr's winter cover (SnowField.CoverageNoMeltAt, shelter included); at SeasonFxCover or more a
                        // flat, uncleared step is Snow like Mountain / Deep North, and back to vanilla's own once it melts
                        if (vb == Heightmap.Biome.DeepNorth || vb == Heightmap.Biome.AshLands || vb == Heightmap.Biome.Mountain || !SnowField.CoreGroundOn) return;
                        float cov = SnowField.CoverageNoMeltAt(point);
                        if (cov < SeasonFxCover || Mathf.Acos(Mathf.Clamp01(groundNormal.y)) * Mathf.Rad2Deg >= 40f || __instance.IsCleared(point)) { NoteFx(point, __result, cov, 0f, "heightmap"); return; }
                        __result = FootStep.GroundMaterial.Snow;
                        s_materials++; NoteFx(point, __result, cov, 0f, "heightmap"); Summary();
                        return;
                    }
                    float g = __instance.GetCultivationMask(point);
                    if (g > 0.6f) __result = FootStep.GroundMaterial.SnowVeryDeep;
                    else if (g > 0.12f) __result = FootStep.GroundMaterial.SnowDeep;
                    else if (Mathf.Acos(Mathf.Clamp01(groundNormal.y)) * Mathf.Rad2Deg < 40f && !__instance.IsCleared(point)) __result = FootStep.GroundMaterial.Snow;
                    else return;
                    s_materials++; Summary();
                }
                catch { }
            }
        }

        // 0.2.854 ([seasons], Fire 10-27 shot 546 "not kicking up the deep snow footstep fx in our deep snow anymore"): vanilla's
        // FootStep.GetGroundMaterial (FootStep.cs:343) asks the ground collider's Heightmap; FAT voxel ground has none, so every step
        // there is Default (no snow FX) and the Heightmap postfix above never runs. On ground with no Heightmap that answers Default,
        // the deep-snow depth (SnowField.DeepSnowG, the same model the voxel raise draws) picks Snow / SnowDeep / SnowVeryDeep with
        // the Heightmap thresholds. FX only, for every character (no TerrainOps: those stay local-player-only via CharBiome).
        private const float DeepFxG = 0.12f, VeryDeepFxG = 0.6f;
        // [seasons]: seasonal (winter) snow at this cover or more steps as Snow
        internal const float SeasonFxCover = 0.3f;
        private static FootStep.GroundMaterial s_lastFx = (FootStep.GroundMaterial)(-1);
        internal static FootStep.GroundMaterial LastFx => s_lastFx;
        internal static float LastFxCover { get; private set; }
        // one Debug line per material CHANGE (any character): position, biome, cover, depth, the chosen material
        private static void NoteFx(Vector3 p, FootStep.GroundMaterial m, float cover, float g, string ground)
        {
            LastFxCover = cover;
            // 0.2.941 (Fire's rule: every fix proves itself in the log): the FIRST seasonal Snow footstep of the session at Info
            if (!s_seasonProved && m == FootStep.GroundMaterial.Snow && g <= 0f)
            {
                s_seasonProved = true;
                FiresLogger.LogDebug($"[DeepSnowFx] first seasonal snow footstep this session: Snow at {p.x:0.0},{p.z:0.0} ({ground}; biome {(WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(p.x, p.z) : Heightmap.Biome.None)}, winter snow cover {cover:0.00} >= {SeasonFxCover:0.00})");
            }
            if (m == s_lastFx) return;
            s_lastFx = m;
            FiresLogger.LogDebug($"[DeepSnowFx] footstep material -> {m} at {p.x:0.0},{p.z:0.0} ({ground}; biome {(WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(p.x, p.z) : Heightmap.Biome.None)}, snow cover {cover:0.00} (seasonal threshold {SeasonFxCover:0.00}), deep {g * SnowField.MaxDepthMetres:0.00} m)");
        }
        private static bool s_seasonProved;
        private static int s_voxelSteps;
        private static float s_fxLogAt;

        [HarmonyPatch(typeof(FootStep), "GetGroundMaterial")]
        private static class VoxelFootstep
        {
            private static void Postfix(Character character, Vector3 point, ref FootStep.GroundMaterial __result)
            {
                try
                {
                    if (__result != FootStep.GroundMaterial.Default || character == null || !SnowField.CoreGroundOn) return;
                    var col = character.GetLastGroundCollider();
                    if (col == null || col.GetComponent<Heightmap>() != null) return;
                    float g = SnowField.DeepSnowG(point);
                    if (g <= 0f)
                    {
                        // seasonal snow on voxel ground ([seasons]): no deep-snow base here, so Vedr's winter cover decides (see the Heightmap path)
                        float cov = SnowField.CoverageNoMeltAt(point);
                        if (cov < SeasonFxCover) { NoteFx(point, __result, cov, 0f, "voxel"); return; }
                        __result = FootStep.GroundMaterial.Snow;
                        s_voxelSteps++; NoteFx(point, __result, cov, 0f, "voxel");
                        return;
                    }
                    __result = g > VeryDeepFxG ? FootStep.GroundMaterial.SnowVeryDeep : g > DeepFxG ? FootStep.GroundMaterial.SnowDeep : FootStep.GroundMaterial.Snow;
                    NoteFx(point, __result, SnowField.CoverageNoMeltAt(point), g, "voxel");
                    s_voxelSteps++;
                    if (Time.unscaledTime >= s_fxLogAt)
                    {
                        s_fxLogAt = Time.unscaledTime + 10f;
                        FiresLogger.LogDebug($"[DeepSnowFx] footstep material at feet: {__result} (biome {(WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(point) : Heightmap.Biome.None)}, deep {g * SnowField.MaxDepthMetres:0.00} m, ground '{col.name}' has no Heightmap; {s_voxelSteps} voxel step(s) so far, latest {character.name.Replace("(Clone)", "")})");
                    }
                }
                catch { }
            }
        }

        [HarmonyPatch(typeof(Character), "UpdateLava")]
        private static class CharBiome
        {
            private static void Postfix(Character __instance)
            {
                try
                {
                    // 0.2.846 ([seasons], Fire 10-26 op storm at his hut, ~20 TerrainComp ops/s via RPC from the server): m_lastBiome =
                    // DeepNorth makes vanilla run its whole Deep North character code: m_deepSnowWalkObj TerrainOps every 1 m and on
                    // landing (Character.cs:1701-1705, :2794), for EVERY character, server-owned companions / NPCs included. Only the
                    // LOCAL player now gets the painted Deep North (its own sink, slow and trail); everyone else keeps vanilla's biome.
                    // 0.2.1191 ([generator], ptimer4a 1.3-1.9 us/call): a managed reference compare, not Unity's native-checked
                    // op_Inequality; the same answer for a live character (UpdateLava never runs on a destroyed one).
                    s_probeCalls++;
                    if (!ReferenceEquals(__instance, Player.m_localPlayer)) { Probe(); return; }
                    s_probeLocal++;
                    ref var b = ref s_lastBiome(__instance);
                    if (b == Heightmap.Biome.DeepNorth || b == Heightmap.Biome.AshLands || b == Heightmap.Biome.None || SnowVedrGround.Owns) { Probe(); return; }   // 0.2.1197: Vedr owns the deep-snow walk off the Deep North
                    Vector3 pos = __instance.transform.position;
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool snow = SnowField.DeepGroundAt(pos) || SnowField.WinterGroundAt(pos);   // 0.2.1052: + seasonal winter ground
                    s_probeEvalTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    s_probeEvals++;
                    if (pos == s_probeLastPos && snow == s_probeLastSnow) s_probeSame++;
                    s_probeLastPos = pos; s_probeLastSnow = snow;
                    Probe();
                    if (!snow) return;
                    b = Heightmap.Biome.DeepNorth;
                    s_chars++; Summary();
                }
                catch { }
            }
        }


        // 0.2.987 (Fire 10-29: "snow trails in the mountains still are not a thing"): vanilla's walk trail (m_deepSnowWalkObj every
        // m_deepSnowWalkObjDist, Character.UpdateWalking) only runs while m_lastBiome is DeepNorth, and the flip above is deep-snow
        // ground only (Mountain snow is its own class: no Deep North sink / slow). On Mountain snow the LOCAL player now drops the same
        // trail op itself (same distance and depth rule as vanilla), without the biome flip.
        private static readonly AccessTools.FieldRef<Character, Heightmap> s_lastHeightmap = AccessTools.FieldRefAccess<Character, Heightmap>("m_lastHeightmap");
        private static readonly AccessTools.FieldRef<Character, Vector3> s_lastWalkPos = AccessTools.FieldRefAccess<Character, Vector3>("m_lastDeepSnowWalkPos");
        private static int s_mountainTrails; private static float s_mountainLogAt; private static bool s_mountainWhy;

        [HarmonyPatch(typeof(Character), "UpdateWalking")]
        private static class MountainTrail
        {
            private static void Postfix(Character __instance)
            {
                try
                {
                    if (__instance != Player.m_localPlayer || __instance.m_deepSnowWalkObj == null || SnowVedrGround.Owns) return;   // 0.2.1197: Vedr's ground trail
                    if (s_lastBiome(__instance) == Heightmap.Biome.DeepNorth) return;   // vanilla's own trail runs
                    Vector3 pos = __instance.transform.position;
                    if (SnowField.MountainWeight(pos) < 0.5f || !__instance.IsOnGround() || !__instance.IsOnHeightmap()) return;
                    var hm = s_lastHeightmap(__instance);
                    if (hm == null) return;
                    float depth = hm.GetCultivationMask(pos) * __instance.m_deepSnowSlowMaxHeight;
                    if (depth <= __instance.m_deepSnowSlowStartHeight)
                    {
                        if (!s_mountainWhy) { s_mountainWhy = true; FiresLogger.LogDebug($"[Snow] mountain trail: snow here {depth:0.00} m, below the {__instance.m_deepSnowSlowStartHeight:0.00} m vanilla needs for a trail (no trench)"); }
                        return;
                    }
                    ref var last = ref s_lastWalkPos(__instance);
                    if (Vector3.Distance(last, pos) <= __instance.m_deepSnowWalkObjDist) return;
                    UnityEngine.Object.Instantiate(__instance.m_deepSnowWalkObj, pos, __instance.transform.rotation);
                    last = pos; s_mountainTrails++;
                    if (Time.unscaledTime >= s_mountainLogAt)
                    {
                        s_mountainLogAt = Time.unscaledTime + 30f;
                        FiresLogger.LogDebug($"[Snow] mountain trail: {s_mountainTrails} walk trail op(s) on Mountain snow (snow {depth:0.00} m at {pos.x:0},{pos.z:0}); vanilla only trails in Deep North");
                        s_mountainTrails = 0;
                    }
                }
                catch { }
            }
        }

        // 0.2.1191 ([generator], [lead]: log-prove before any cache): Debug-only, every 10 s: CharBiome calls, local-player calls,
        // snow-ground evaluations, how many repeated the last position with the same answer (what a position cache would save),
        // and the evaluation time. Counting costs a few increments; nothing is logged unless Debug is on for Core.
        private static int s_probeCalls, s_probeLocal, s_probeEvals, s_probeSame;
        private static long s_probeEvalTicks;
        private static Vector3 s_probeLastPos = new Vector3(float.NaN, 0f, 0f);
        private static bool s_probeLastSnow;
        private static float s_probeAt;

        private static void Probe()
        {
            float now = Time.unscaledTime;
            if (now < s_probeAt) return;
            if (s_probeAt > 0f)
            {
                double us = s_probeEvals > 0 ? s_probeEvalTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency / s_probeEvals : 0;
                FiresLogger.LogDebug($"[CharBiome] last 10 s: {s_probeCalls} call(s), {s_probeLocal} local player, {s_probeEvals} snow-ground eval(s) avg {us:0.00} us, {s_probeSame} same position + same answer (a position cache would skip these)");
            }
            s_probeAt = now + 10f;
            s_probeCalls = s_probeLocal = s_probeEvals = s_probeSame = 0; s_probeEvalTicks = 0;
        }

        private static void Summary()
        {
            if (Time.unscaledTime < s_summaryAt) return;
            bool first = s_summaryAt <= 0f;
            s_summaryAt = Time.unscaledTime + 30f;
            FiresLogger.LogDebug($"[Snow] deep ground as Deep North{(first ? " (first use)" : " last 30 s")}: {s_offsets} sink offset(s), {s_materials} ground material(s), {s_chars} character biome(s)");
            s_offsets = s_materials = s_chars = 0;
        }
    }
}
