using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Combat
{
    /// <summary>
    /// Lets melee swings aim up and down with the crosshair. Vanilla's Attack.GetMeleeAttackDir
    /// already pitches a swing toward the character's aim, but caps it at the attack's m_maxYAngle, which is small on most player swings,
    /// so they sweep flat. For the local player this redoes that one RotateTowards with max(m_maxYAngle, [Combat] Melee aim max pitch).
    /// The sweep arc, range, widths and origin stay vanilla's (FCC's size scaling of the cloned Attack is untouched).
    /// A player's aim is the CAMERA's look
    /// direction, but the swing starts at the hand + m_attackHeight, below and ahead of the camera, so the same pitch passes under the
    /// crosshair point. The direction is now from the swing's origin TO the point under the crosshair (the camera ray's hit, else 20 m
    /// out), on the body's heading, clamped to the max pitch. A Debug probe logs each swing's geometry and every collider on its
    /// centre ray with vanilla's filters.
    /// </summary>
    internal static class MeleeCrosshairAim
    {
        private static ConfigEntry<bool> s_enabled;
        private static ConfigEntry<float> s_maxPitch;
        private static readonly HashSet<string> s_logged = new HashSet<string>();
        private const float AimReach = 20f;

        public static void Bind(ConfigFile config)
        {
            s_enabled = config.Bind("Combat", "Melee aims with the crosshair", true,
                "Your melee swings tilt up or down toward the crosshair (a crab at your feet, a bird or a wall above), up to Melee aim max pitch. The swing's arc and reach stay the weapon's own. Off: vanilla (most swings stay level).");
            s_maxPitch = config.Bind("Combat", "Melee aim max pitch", 60f,
                new ConfigDescription("Degrees a swing may tilt up or down toward the crosshair. An attack whose own limit is higher keeps it.", new AcceptableValueRange<float>(0f, 89f)));
        }

        // Vanilla DoMeleeAttack's sweep origin (Attack.cs 1.0.15 line 946).
        private static Vector3 SwingOrigin(Attack a, Transform originJoint) =>
            originJoint.position + Vector3.up * a.m_attackHeight + a.m_character.transform.right * a.m_attackOffset;

        // The crosshair ray must match what the player sees even when another tool holds the camera elsewhere, so
        // the ray runs along the PLAYER's look direction (Player.GetLookDir: what the crosshair means), from the camera only while the
        // camera sits near the player's eye (normal 3rd/1st person), else from the eye itself.
        private const float CameraNearEye = 8f;

        // The world point under the crosshair: the look ray's first hit that is not the player, else AimReach m out.
        private static Vector3 CrosshairPoint(Player me, out string what)
        {
            Vector3 fwd = me.GetLookDir();
            Vector3 eye = me.m_eye != null ? me.m_eye.position : me.transform.position + Vector3.up * 1.6f;
            Camera cam = Utils.GetMainCamera();
            bool camNear = cam != null && Vector3.Distance(cam.transform.position, eye) <= CameraNearEye;
            Vector3 from = camNear ? cam.transform.position : eye;
            what = camNear ? "from camera" : (cam == null ? "no camera: from the eye" : $"camera {Vector3.Distance(cam.transform.position, eye):0.0} m from the eye: from the eye");
            var hits = Physics.RaycastAll(from, fwd, 60f, Attack.m_attackMask, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
            foreach (var h in hits)
            {
                if (h.collider == null || h.collider.transform.IsChildOf(me.transform)) continue;
                what += $", look ray hit {h.collider.name} at {h.distance:0.0} m";
                return h.point;
            }
            what += $", look ray hit nothing: {AimReach:0} m out";
            return from + fwd * AimReach;
        }

        [HarmonyPatch(typeof(Attack), "GetMeleeAttackDir")]
        private static class GetMeleeAttackDirPatch
        {
            private static void Postfix(Attack __instance, Transform originJoint, ref Vector3 attackDir)
            {
                try
                {
                    if (s_enabled == null || !s_enabled.Value) return;
                    Humanoid who = __instance.m_character;
                    if (who == null || who != Player.m_localPlayer || originJoint == null) return;
                    float cap = Mathf.Max(__instance.m_maxYAngle, s_maxPitch.Value);
                    if (cap <= __instance.m_maxYAngle + 0.01f) return;
                    Vector3 forward = who.transform.forward;
                    Vector3 origin = SwingOrigin(__instance, originJoint);
                    Vector3 aimPoint = CrosshairPoint((Player)who, out string aimWhat);
                    Vector3 toAim = aimPoint - origin;
                    float flat = new Vector2(toAim.x, toAim.z).magnitude;
                    // the pitch from the swing's own origin to the crosshair point, on the body's heading (vanilla keeps the heading too)
                    Vector3 aim = new Vector3(forward.x, 0f, forward.z).normalized * Mathf.Max(0.05f, flat) + Vector3.up * toAim.y;
                    aim.Normalize();
                    attackDir = Vector3.RotateTowards(forward, aim, Mathf.Deg2Rad * cap, 10f);
                    float pitch = -Mathf.Asin(Mathf.Clamp(attackDir.y, -1f, 1f)) * Mathf.Rad2Deg;
                    string key = $"{__instance.m_weapon?.m_shared?.m_name}/{__instance.m_attackAnimation}";
                    if (s_logged.Add(key))
                        FiresLogger.LogInfo($"[Combat] melee aim: {key} vanilla cap {__instance.m_maxYAngle:0}°, now {cap:0}° (crosshair)");
                    // How far the swing's centre
                    // line passes from the aim point, and the body heading vs the look heading (the swing keeps the BODY's heading)
                    float range = __instance.m_attackRange;
                    float along = Mathf.Clamp(Vector3.Dot(toAim, attackDir), 0f, range);
                    float passMiss = Vector3.Distance(origin + attackDir * along, aimPoint);
                    Vector3 lookFlat = who.GetLookDir(); lookFlat.y = 0f;
                    float yawApart = Vector3.Angle(new Vector3(forward.x, 0f, forward.z), lookFlat);
                    float aimYawApart = Vector3.Angle(new Vector3(forward.x, 0f, forward.z), new Vector3(toAim.x, 0f, toAim.z));
                    var near = new StringBuilder();
                    foreach (var col in Physics.OverlapSphere(aimPoint, 0.6f, ~0, QueryTriggerInteraction.Collide))
                    {
                        if (col == null || col.transform.IsChildOf(who.transform)) continue;
                        int layer = col.gameObject.layer;
                        near.Append($" {col.name}/{LayerMask.LayerToName(layer)}{(col.isTrigger ? "/trigger" : "")}{(((Attack.m_attackMask >> layer) & 1) == 1 ? "" : "/NOT-IN-ATTACK-MASK")};");
                        if (near.Length > 400) break;
                    }
                    FiresLogger.LogDebug($"[Combat] melee aim geometry {key}: centre line passes {passMiss:0.00} m from the aim point (closest at {along:0.00} m of range {range:0.00}, "
                        + $"ray width {__instance.m_attackRayWidth:0.00}); body heading vs look heading {yawApart:0}°, vs aim point {aimYawApart:0}°; needed pitch "
                        + $"{-Mathf.Atan2(toAim.y, Mathf.Max(0.01f, new Vector2(toAim.x, toAim.z).magnitude)) * Mathf.Rad2Deg:0}° (cap {cap:0}°); colliders within 0.6 m of the aim point:{(near.Length == 0 ? " none" : near.ToString())}");
                    FiresLogger.LogDebug($"[Combat] melee aim pitch {pitch:0}° (crosshair) {key}: origin ({origin.x:0.00}, {origin.y:0.00}, {origin.z:0.00}), "
                        + $"aim point ({aimPoint.x:0.00}, {aimPoint.y:0.00}, {aimPoint.z:0.00}) [{aimWhat}], {toAim.magnitude:0.00} m away; player look pitch {-Mathf.Asin(Mathf.Clamp(who.GetLookDir().y, -1f, 1f)) * Mathf.Rad2Deg:0}°, camera pitch {CameraPitch():0}°");
                }
                catch (Exception ex) { FiresLogger.LogDebug($"[Combat] melee aim skipped: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        private static float CameraPitch()
        {
            Camera cam = Utils.GetMainCamera();
            return cam != null ? -Mathf.Asin(Mathf.Clamp(cam.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg : 0f;
        }

        // Debug probe: the centre ray of the swing with vanilla's own numbers and
        // every collider it touches, with the filter that keeps or drops it. Debug only; local player; DoMeleeAttack itself is unchanged.
        [HarmonyPatch(typeof(Attack), "DoMeleeAttack")]
        private static class DoMeleeAttackProbe
        {
            private static void Prefix(Attack __instance)
            {
                try
                {
                    if (!FiresLogger.DebugEnabled || s_enabled == null || !s_enabled.Value) return;
                    Humanoid who = __instance.m_character;
                    if (who == null || who != Player.m_localPlayer) return;
                    __instance.GetMeleeAttackDir(out Transform joint, out Vector3 dir);
                    if (joint == null) return;
                    Vector3 origin = SwingOrigin(__instance, joint);
                    float range = __instance.m_attackRange, width = __instance.m_attackRayWidth;
                    int mask = __instance.m_hitTerrain ? Attack.m_attackMaskTerrain : Attack.m_attackMask;
                    var sb = new StringBuilder();
                    sb.Append($"[Combat] melee probe {__instance.m_weapon?.m_shared?.m_name}/{__instance.m_attackAnimation}: origin ({origin.x:0.00}, {origin.y:0.00}, {origin.z:0.00}) "
                        + $"(joint {joint.name} + up {__instance.m_attackHeight:0.00} + right {__instance.m_attackOffset:0.00}), dir ({dir.x:0.00}, {dir.y:0.00}, {dir.z:0.00}), "
                        + $"range {range:0.00}, ray width {width:0.00} (+{__instance.m_attackRayWidthCharExtra:0.00} chars at +{__instance.m_attackHeightChar1:0.00}/+{__instance.m_attackHeightChar2:0.00}), "
                        + $"{__instance.m_attackType} arc {__instance.m_attackAngle:0}°, hit terrain {__instance.m_hitTerrain}, through walls {__instance.m_hitThroughWalls}; centre ray:");
                    RaycastHit[] hits = width > 0f
                        ? Physics.SphereCastAll(origin, width, dir, Mathf.Max(0f, range - width), mask, QueryTriggerInteraction.Ignore)
                        : Physics.RaycastAll(origin, dir, range, mask, QueryTriggerInteraction.Ignore);
                    Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
                    if (hits.Length == 0) sb.Append(" nothing within range");
                    bool blocked = false;
                    foreach (var h in hits)
                    {
                        if (h.collider == null) continue;
                        string verdict;
                        GameObject go = Projectile.FindHitObject(h.collider);
                        Vector3 p = h.point == Vector3.zero ? h.collider.ClosestPoint(origin) : h.point;
                        if (h.collider.gameObject == who.gameObject || go == who.gameObject) verdict = "self";
                        else if (__instance.m_attackAngle < 180f && Vector3.Dot(p - origin, dir) <= 0f) verdict = "behind the swing (angle filter)";
                        else if (blocked) verdict = "behind an earlier hit (no hit-through-walls)";
                        else
                        {
                            Character c = go != null ? go.GetComponent<Character>() : null;
                            verdict = c != null ? (BaseAI.IsEnemy(who, c) || (c.GetBaseAI() != null && c.GetBaseAI().IsAggravatable()) ? "HIT (character)" : "character, not hostile (friendly filter)")
                                                : "HIT (" + (go != null && go.GetComponent<IDestructible>() != null ? "destructible" : "solid, no damage") + ")";
                            if (!__instance.m_hitThroughWalls) blocked = true;
                        }
                        sb.Append($" [{h.collider.name} layer {LayerMask.LayerToName(h.collider.gameObject.layer)} at {h.distance:0.00} m, y {p.y:0.00}: {verdict}]");
                    }
                    FiresLogger.LogDebug(sb.ToString());
                }
                catch (Exception ex) { FiresLogger.LogDebug($"[Combat] melee probe skipped: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
    }
}
