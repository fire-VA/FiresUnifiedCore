using System;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Movement
{
    /// <summary>
    /// Low ceilings (Core 0.2.295, Fire 10-05: "when a character starts walking into a cave area that is too short for them to stand in,
    /// the camera will clip outside of the cave"). Two parts, local player only:
    /// - Headroom: a sphere cast up from the feet each physics step (and a step ahead in the walking direction). Under a ceiling too low
    ///   to stand, the player is held in vanilla's crouch (the same SetCrouch the crouch key uses; no sneak stamina drain or skill while
    ///   it is held), and the crouch can't be released until there is room again. That lowers the head and the eye, so the camera's pivot.
    /// - Camera: vanilla casts the camera back from a point ABOVE the head (the offset eye), so under a low roof that cast starts outside
    ///   the mesh and the camera ends up looking at the cave from outside. After vanilla's solve, the camera is cast again from the real
    ///   eye (inside the cave) with a sphere the size of the near clip, and pulled in to the first solid it meets.
    /// Debug lines: "[Headroom] ceiling 1.42 m over the feet -> crouch (needs 2.05 m to stand)", "-> stand", "[Headroom] camera pulled …".
    /// </summary>
    public static class Headroom
    {
        private static ConfigEntry<bool> s_enabled;
        private static int s_mask;
        private static bool s_forced;
        private static float s_roomSince = -1f;
        private static float s_nextCameraLog;

        /// <summary>Room above the eye (m) needed to stand: the head plus a margin.</summary>
        private const float StandMargin = 0.35f;
        /// <summary>Extra room (m) before a held crouch is released, held for ReleaseSeconds, so the pose doesn't flicker at an edge.</summary>
        private const float ReleaseExtra = 0.2f, ReleaseSeconds = 0.3f;
        /// <summary>How far ahead (m) in the walking direction the ceiling is also read, so the crouch starts before the head reaches it.</summary>
        private const float LookAhead = 0.6f;
        /// <summary>The camera's guard sphere: at least this radius (m), and the camera's near clip plus this.</summary>
        private const float CameraMinRadius = 0.15f, CameraClipMargin = 0.05f;

        private static int Mask => s_mask != 0 ? s_mask : (s_mask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "vehicle"));

        public static bool Enabled => s_enabled == null || s_enabled.Value;

        internal static void Bind(ConfigFile config)
        {
            s_enabled = config.Bind("Movement", "Low ceilings", true,
                "Under a ceiling too low to stand (caves, tunnels, low roofs) the player is held in a crouch, and the camera is kept inside the space instead of clipping out through the roof.");
            s_enabled.SettingChanged += (sender, args) => { if (!Enabled) Release(Player.m_localPlayer, "the setting is off"); };
        }

        /// <summary>The ceiling height over <paramref name="player"/>'s feet here and a step ahead (m), or +infinity with none in reach.</summary>
        public static float CeilingOver(Player player, float reach)
        {
            float best = float.PositiveInfinity;
            Vector3 feet = player.transform.position;
            Probe(feet, reach, ref best);
            Vector3 move = player.GetMoveDir();
            move.y = 0f;
            if (move.sqrMagnitude > 0.01f) Probe(feet + move.normalized * LookAhead, reach, ref best);
            return best;
        }

        private static void Probe(Vector3 feet, float reach, ref float best)
        {
            // 0.2.307: SphereCast ignores what the sphere starts inside; the old 0.3 m sphere at 0.5 m already reached 0.8 m, so a
            // 0.63 m roof read "no ceiling". A 0.25 m sphere at 0.3 m sees ceilings from 0.55 m up.
            const float radius = 0.25f, start = 0.3f;
            if (Physics.SphereCast(feet + Vector3.up * start, radius, Vector3.up, out RaycastHit hit, reach, Mask, QueryTriggerInteraction.Ignore)
                && hit.collider != null && !(hit.collider.attachedRigidbody != null && hit.collider.attachedRigidbody.GetComponent<Character>() != null))
                best = Mathf.Min(best, start + hit.distance + radius);
        }

        private static void Tick(Player player)
        {
            if (!Enabled || player.IsDead() || player.IsSwimming() || player.InBed() || player.IsAttached() || player.IsFlying() || Climb.IsClimbing(player))
            {
                Release(player, null);
                return;
            }
            float eyeHeight = player.m_eye != null ? player.m_eye.position.y - player.transform.position.y : 1.6f;
            if (s_forced) eyeHeight = Mathf.Max(eyeHeight, StandingEyeHeight);   // crouched now: judge against the standing eye
            else if (!player.IsCrouching() && !player.m_crouchToggled) StandingEyeHeight = Mathf.Max(1.2f, eyeHeight);
            float need = StandingEyeHeight + StandMargin;
            float ceiling = CeilingOver(player, need + ReleaseExtra + 0.5f);

            // Lower than a crouched body: crawl, when crawling is on (Crawl.Enabled); crouched room is the standing capsule x CrouchShare.
            float crawlBelow = Crawl.StandingCapsule(player) * Crawl.CrouchShare + 0.05f;
            if (ceiling < need && Crawl.Enabled)
            {
                if (ceiling < crawlBelow) { s_crawlRoomSince = -1f; Crawl.Begin(player, $"ceiling {ceiling:0.00} m (a crouch needs {crawlBelow:0.00} m)"); }
                else if (Crawl.Active)
                {
                    if (ceiling < crawlBelow + ReleaseExtra) s_crawlRoomSince = -1f;
                    else
                    {
                        if (s_crawlRoomSince < 0f) s_crawlRoomSince = Time.time;
                        if (Time.time - s_crawlRoomSince >= ReleaseSeconds) Crawl.End(player, $"ceiling {ceiling:0.00} m -> crouch");
                    }
                }
            }
            else if (Crawl.Active && ceiling >= need) Crawl.End(player, null);

            if (ceiling < need)
            {
                s_roomSince = -1f;
                if (!player.m_crouchToggled) player.SetCrouch(true);
                if (!s_forced)
                {
                    s_forced = true;
                    FiresCore.Logging.FiresLogger.LogDebug($"[Headroom] ceiling {ceiling:0.00} m over the feet -> crouch (needs {need:0.00} m to stand)");
                }
                return;
            }
            if (!s_forced) return;
            if (ceiling < need + ReleaseExtra) { s_roomSince = -1f; if (!player.m_crouchToggled) player.SetCrouch(true); return; }
            if (s_roomSince < 0f) s_roomSince = Time.time;
            if (Time.time - s_roomSince < ReleaseSeconds) { if (!player.m_crouchToggled) player.SetCrouch(true); return; }
            Release(player, float.IsPositiveInfinity(ceiling) ? "no ceiling in reach" : $"ceiling {ceiling:0.00} m");
        }

        private static float StandingEyeHeight = 1.6f;

        private static float s_crawlRoomSince = -1f;

        private static void Release(Player player, string why)
        {
            if (Crawl.Active) Crawl.End(player, null);
            if (!s_forced) return;
            s_forced = false;
            s_roomSince = -1f;
            if (player != null && player.m_crouchToggled) player.SetCrouch(false);
            if (why != null) FiresCore.Logging.FiresLogger.LogDebug($"[Headroom] {why} -> stand");
        }

        /// <summary>True while the low-ceiling crouch holds the local player.</summary>
        public static bool Holding => s_forced;

        [HarmonyPatch(typeof(Player), "FixedUpdate")]
        private static class Player_FixedUpdate_Headroom
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer) return;
                try { Tick(__instance); }
                catch (Exception ex) { Debug.LogWarning($"[Headroom] {ex.Message}"); }
            }
        }

        // A held crouch costs no sneak stamina and trains no Sneak: the player didn't choose to sneak.
        [HarmonyPatch(typeof(Player), "OnSneaking")]
        private static class Player_OnSneaking_Headroom
        {
            private static bool Prefix(Player __instance) => !(s_forced && __instance == Player.m_localPlayer);
        }

        // Core's own crawl lowers the camera pivot with the body (VikHavn's crawl does this itself when it is the owner).
        [HarmonyPatch(typeof(GameCamera), "GetCameraBaseOffset")]
        private static class GameCamera_GetCameraBaseOffset_Crawl
        {
            private static void Postfix(Player player, ref Vector3 __result)
            {
                if (Crawl.CoreCrawling && player == Player.m_localPlayer) __result.y *= Crawl.CoreHeightShare;
            }
        }

        [HarmonyPatch(typeof(GameCamera), "GetCameraPosition")]
        private static class GameCamera_GetCameraPosition_Headroom
        {
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(GameCamera __instance, ref Vector3 pos)
            {
                try
                {
                    if (!Enabled) return;
                    Player player = Player.m_localPlayer;
                    if (player == null || player.m_eye == null || player.InIntro()) return;
                    Vector3 eye = player.m_eye.position;
                    Vector3 d = pos - eye;
                    float dist = d.magnitude;
                    if (dist < 0.05f) return;
                    Camera camera = __instance.m_camera != null ? __instance.m_camera : Camera.main;
                    float radius = Mathf.Max(CameraMinRadius, (camera != null ? camera.nearClipPlane : 0.1f) + CameraClipMargin);
                    int mask = __instance.m_blockCameraMask.value != 0 ? __instance.m_blockCameraMask.value : Mask;
                    // The eye itself in a solid (a roof lower than the eye): nothing inside to cast from; let vanilla's answer stand.
                    if (Physics.CheckSphere(eye, radius * 0.5f, mask, QueryTriggerInteraction.Ignore)) return;
                    if (!Physics.SphereCast(eye, radius, d / dist, out RaycastHit hit, dist, mask, QueryTriggerInteraction.Ignore)) return;
                    if (hit.collider != null && hit.collider.attachedRigidbody != null && hit.collider.attachedRigidbody.GetComponent<Character>() != null) return;
                    float kept = Mathf.Max(0f, hit.distance - CameraClipMargin);
                    pos = eye + d / dist * kept;
                    if (Time.time >= s_nextCameraLog)
                    {
                        s_nextCameraLog = Time.time + 5f;
                        FiresCore.Logging.FiresLogger.LogDebug($"[Headroom] camera pulled {dist:0.00} -> {kept:0.00} m behind the eye by {hit.collider?.name ?? "?"} " +
                                                               $"({(s_forced ? "held crouch" : "standing")})");
                    }
                }
                catch (Exception ex) { Debug.LogWarning($"[Headroom] camera guard: {ex.Message}"); }
            }
        }
    }
}
