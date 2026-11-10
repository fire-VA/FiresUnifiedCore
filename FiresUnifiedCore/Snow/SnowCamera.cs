using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Snow
{
    /// <summary>
    /// 0.2.926 ([wishbone], Fire round 34: "We should not be forced in the first person camera when we are in deep snow").
    /// Cause from vanilla's code (GameCamera.RayTestPoint, 1.0): the camera clip rays start BELOW the eye by
    /// ZoneSystem.GetGroundOffset(eye) x 1.6 - the heightmap's snow offset - except when the heightmap biome is DeepNorth, where vanilla
    /// uses a fixed 0.4 m. Core's deep snow (SnowGroundAsDeepNorth / the heightmap inject) raises that offset to up to 2.1 m on ground
    /// whose heightmap biome is NOT DeepNorth (Mountain, painted / FAT ground), so the rays start up to ~3.4 m under the eye, inside the
    /// ground, hit "terrain" at ~0 m and the camera snaps to first person.
    ///  - Probe (always on, log only): one "[SnowCamera] snap ..." line per snap (camera pulled under 1 m of a wanted >= 1.5 m): the raw
    ///    offset, biome, eye / ray origin / ground y, the player's sink, and the hit collider + layer. Info, per event only.
    ///  - Fix ([Snow] Camera ignores deep snow, ships OFF): inside the camera's ray test only, a non-DeepNorth ground offset is capped at
    ///    0.25 (= vanilla's own Deep North 0.4 m drop). The player's zoom (m_distance) is never touched; real walls / hills still collide.
    /// 0.2.932 (Fire round 35 "the camera reads our head under snow and forces us in first person"): vanilla's DEEP NORTH snow avoidance -
    /// RayTestPoint samples ZoneSystem.GetGroundOffset(pos, out biome, out groundHeight) along the ray and pulls the camera to the first sample
    /// under the snow top (m_snowDistTarget). Vanilla's deepest snow is 1.3 m; Core's reaches 2.1 m, over the eye -> first person. The fix
    /// (same key) caps that snow top at 1.3 m inside the camera ray. The probe logs every snap (<= 1/s), WHICH path did it (vanilla deep-snow
    /// avoidance vs the clip ray), all three of vanilla's casts with their colliders (EBM stand-in prefab / position / bounds), the snow top
    /// and the wanted camera point vs its ground, plus the caller stack once.
    /// Client camera only: a local setting, not server-synced.
    /// </summary>
    internal static class SnowCamera
    {
        private const float CapOffset = 0.25f, SnapUnder = 1f, WantedOver = 1.5f;
        private static bool s_inRay, s_snapped;
        private static float s_rawOffset;
        private static Heightmap.Biome s_biome;
        private static int s_capped;

        private static bool FixOn => SnowConfig.P("Camera ignores deep snow", true);   // 1086 ([generator], Fire 11-01: configs live in their own mods): owner FiresAdminTerrain

        [HarmonyPatch(typeof(GameCamera), "RayTestPoint", new[] { typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(float), typeof(float) },
            new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out })]
        private static class GameCamera_RayTestPoint_Snow
        {
            private static void Prefix() { s_inRay = true; s_rawOffset = 0f; s_biome = Heightmap.Biome.None; s_capped = 0; s_snowCapped = 0; s_snowTopAtEye = -1f; }

            private static void Postfix(GameCamera __instance, Vector3 point, Vector3 dir, float maxDist, float distance)
            {
                s_inRay = false;
                try
                {
                    // 0.2.932: vanilla's Deep North snow avoidance (RayTestPoint tail) sets m_snowDistTarget when the ray is under the snow top;
                    // CollideRay2 then eases the camera to it - a snap the clip distance alone never shows
                    float snowTarget = s_snowDistTarget(__instance);
                    float eff = snowTarget > 0f ? Mathf.Min(distance, snowTarget) : distance;
                    if (maxDist < WantedOver || eff >= SnapUnder) { if (eff >= WantedOver) s_snapped = false; return; }
                    if (s_snapped && Time.unscaledTime < s_nextSnapLog) return;   // every snap logged, at most 1 per second while it lasts
                    s_snapped = true; s_nextSnapLog = Time.unscaledTime + 1f;
                    float drop = s_biome == Heightmap.Biome.DeepNorth ? 0.4f : s_rawOffset * 1.6f;
                    Vector3 origin = point - new Vector3(0f, drop, 0f);
                    string hit = "none";
                    if (Physics.SphereCast(origin, __instance.m_raycastWidth, dir, out RaycastHit h, maxDist, __instance.m_blockCameraMask))
                        hit = $"{h.collider.name} (layer {LayerMask.LayerToName(h.collider.gameObject.layer)}{StandIn(h.collider)}) at {h.distance:0.00} m, point {h.point:F2}";
                    // vanilla's other two casts too (from the eye, and the ray from the lowered origin), so a hit none above names the real one
                    if (Physics.SphereCast(point, __instance.m_raycastWidth, dir, out RaycastHit h2, maxDist, __instance.m_blockCameraMask))
                        hit += $"; eye cast {h2.collider.name} (layer {LayerMask.LayerToName(h2.collider.gameObject.layer)}{StandIn(h2.collider)}) at {h2.distance:0.00} m";
                    if (Physics.Raycast(origin, dir, out RaycastHit h3, maxDist, __instance.m_blockCameraMask))
                        hit += $"; low ray {h3.collider.name} (layer {LayerMask.LayerToName(h3.collider.gameObject.layer)}) at {h3.distance:0.00} m";
                    Vector3 cam = point + dir * maxDist;
                    float camGround = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(cam) : 0f;
                    hit += $"; wanted camera point y {cam.y:0.00} vs its ground y {camGround:0.00}";
                    float ground = ZoneSystem.instance != null ? ZoneSystem.instance.GetGroundHeight(point) : 0f;
                    var pl = Player.m_localPlayer;
                    float feet = pl != null ? pl.transform.position.y : 0f;
                    string by = snowTarget > 0f && snowTarget <= distance
                        ? $"VANILLA DEEP-SNOW AVOIDANCE (eye y {point.y:0.00} under the snow top y {s_snowTopAtEye:0.00}; snow target {snowTarget:0.00} m)"
                        : "VANILLA CLIP RAY";
                    FiresCore.Logging.FiresLogger.LogDebug($"[SnowCamera] snap to {eff:0.00} m (wanted {maxDist:0.00}) by {by}: ground offset {s_rawOffset:0.00} (biome {s_biome}), eye y {point.y:0.00}, snow top y {s_snowTopAtEye:0.00}, ray origin y {origin.y:0.00}, ground y {ground:0.00}, feet y {feet:0.00} (sink {ground - feet:0.00} m), snow depth {SnowField.DepthAt(point) * SnowField.MaxDepthMetres:0.00} m, clip hit {hit}; fix {(FixOn ? $"ON ({s_capped} offset(s) capped, {s_snowCapped} snow-top sample(s) capped at {VanillaDeepest:0.0} m)" : "off ([Snow] Camera ignores deep snow)")}");
                    if (!s_stackLogged) { s_stackLogged = true; FiresCore.Logging.FiresLogger.LogDebug("[SnowCamera] first snap caller stack: " + Environment.StackTrace.Replace("\r", "").Replace("\n", " | ")); }
                }
                catch (Exception e) { Debug.LogWarning("[SnowCamera] probe: " + e.Message); }
            }
        }

        private static readonly AccessTools.FieldRef<GameCamera, float> s_snowDistTarget = AccessTools.FieldRefAccess<GameCamera, float>("m_snowDistTarget");
        private const float VanillaDeepest = 1.3f;   // vanilla Deep North's deepest snow offset: Lerp(0.1, 2.1, G 0.6)
        private static float s_nextSnapLog, s_snowTopAtEye;
        private static bool s_stackLogged;
        private static int s_snowCapped;

        // the EBM stand-in behind a collider (by type name: no compile dependency on EBM): its prefab and position
        private static string StandIn(Collider c)
        {
            try
            {
                if (FiresCore.Compat.EbmStandIns.Supported)   // 0.2.1218: EbmApi.StandInPrefab, no reading EBM's private fields
                {
                    int ph = FiresCore.Compat.EbmStandIns.PrefabOf(c);
                    if (ph == 0) return $", bounds {c.bounds.center:F2} size {c.bounds.size:F2}";
                    string pn = ZNetScene.instance != null && ZNetScene.instance.GetPrefab(ph) != null ? ZNetScene.instance.GetPrefab(ph).name : ph.ToString();
                    return $", EBM stand-in of {pn}, bounds {c.bounds.center:F2} size {c.bounds.size:F2}";
                }
                foreach (var mb in c.GetComponentsInParent<MonoBehaviour>(true))
                {
                    if (mb == null || mb.GetType().Name != "EbmStandIn") continue;
                    var tr = Traverse.Create(mb);
                    object hash = tr.Field("PrefabHash").GetValue() ?? tr.Property("PrefabHash").GetValue();
                    object pos = tr.Field("Position").GetValue() ?? tr.Property("Position").GetValue();
                    string name = hash is int ih && ZNetScene.instance != null && ZNetScene.instance.GetPrefab(ih) != null ? ZNetScene.instance.GetPrefab(ih).name : hash?.ToString() ?? "?";
                    return $", EBM stand-in of {name} at {pos ?? "?"}, bounds {c.bounds.center:F2} size {c.bounds.size:F2}";
                }
                return $", bounds {c.bounds.center:F2} size {c.bounds.size:F2}";
            }
            catch (Exception e) { return ", stand-in read failed: " + e.Message; }
        }

        // 0.2.932: the Deep North under-snow test (RayTestPoint's tail calls GetGroundOffset(pos, out biome, out groundHeight) along the
        // ray): Core's deep snow reaches 2.1 m, over a sunk player's eye (~1.8 m), so every sample read "under the snow" -> first person.
        // With the fix on, inside the camera ray only, the snow top is capped at vanilla's own deepest (1.3 m).
        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.GetGroundOffset), new[] { typeof(Vector3), typeof(Heightmap.Biome), typeof(float) },
            new[] { ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out })]
        private static class ZoneSystem_GetGroundOffset3_Camera
        {
            private static bool s_first;
            private static void Prefix() { s_first = s_inRay && s_snowTopAtEye < 0f; }
            private static void Postfix(float groundHeight, ref float __result)
            {
                if (!s_inRay) return;
                if (s_first) { s_snowTopAtEye = groundHeight + __result; s_first = false; }
                if (__result > VanillaDeepest && FixOn) { __result = VanillaDeepest; s_snowCapped++; }
            }
        }
        [HarmonyPatch(typeof(ZoneSystem), nameof(ZoneSystem.GetGroundOffset), new[] { typeof(Vector3), typeof(Heightmap.Biome) },
            new[] { ArgumentType.Normal, ArgumentType.Out })]
        private static class ZoneSystem_GetGroundOffset_Camera
        {
            private static void Postfix(Heightmap.Biome biome, ref float __result)
            {
                if (!s_inRay) return;
                if (s_biome == Heightmap.Biome.None) { s_rawOffset = __result; s_biome = biome; }   // the eye's sample (the first call)
                if (biome != Heightmap.Biome.DeepNorth && __result > CapOffset && FixOn) { __result = CapOffset; s_capped++; }
            }
        }
    }
}
