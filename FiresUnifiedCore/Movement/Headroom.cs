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
    [FiresCore.Config.FiresOwnedFeature(FiresCore.Config.CoreFeatures.Movement, FiresCore.Config.CoreFeatures.Crawling)]
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
            string legacy = ReadLegacyCameraCollision(config);
            s_cameraGuard = config.Bind("Camera", "Camera collision", CameraCollisionMode.Auto,
                "Camera collision. On = the camera is pulled in when something blocks the view (vanilla) plus a smooth guard under roofs and in caves. " +
                "Off = the camera stays at the distance you set with the mouse wheel. Auto = On when Fires Admin Terrain is installed, Off otherwise.");
            // 0.2.369: 0.2.367 saved this key as true/false; keep the player's explicit choice (false -> Off, true -> Auto).
            if (legacy == "false") { s_cameraGuard.Value = CameraCollisionMode.Off; FiresCore.Logging.FiresLogger.LogDebug("[Camera] migrated Camera collision = false -> Off"); }
            else if (legacy == "true") { s_cameraGuard.Value = CameraCollisionMode.Auto; FiresCore.Logging.FiresLogger.LogDebug("[Camera] migrated Camera collision = true -> Auto"); }
            s_cameraGuard.SettingChanged += (sender, args) => { s_guardResolved = false; s_guardDist = -1f; s_guardOn = false; };
            FiresCore.Config.CoreFeatures.GateConfig(FiresCore.Config.CoreFeatures.Movement, false, s_enabled, s_cameraGuard);   // 0.2.387: FAT only
        }

        /// <summary>0.2.369: the raw value of a 0.2.367 bool "Camera collision" line in the cfg file ("true"/"false"), else null.</summary>
        private static string ReadLegacyCameraCollision(ConfigFile config)
        {
            try
            {
                if (config == null || string.IsNullOrEmpty(config.ConfigFilePath) || !System.IO.File.Exists(config.ConfigFilePath)) return null;
                bool inCamera = false;
                foreach (string raw in System.IO.File.ReadAllLines(config.ConfigFilePath))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[")) { inCamera = line == "[Camera]"; continue; }
                    if (!inCamera || !line.StartsWith("Camera collision")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) return null;
                    string v = line.Substring(eq + 1).Trim().ToLowerInvariant();
                    return v == "true" || v == "false" ? v : null;
                }
            }
            catch { }
            return null;
        }

        /// <summary>0.2.368 (Fire): the camera guard's default follows FAT; an explicit On / Off always wins.</summary>
        public enum CameraCollisionMode { Auto, On, Off }

        private const string FatGuid = "com.Fire.FiresAdminTerrain";
        private static ConfigEntry<CameraCollisionMode> s_cameraGuard;
        private static bool s_guardResolved, s_guardActive;

        /// <summary>Resolved on first use (after every plugin's Awake, so FAT is known) and again when the setting changes.</summary>
        public static bool CameraGuardEnabled
        {
            get
            {
                if (s_guardResolved) return s_guardActive;
                s_guardResolved = true;
                CameraCollisionMode mode = s_cameraGuard != null ? s_cameraGuard.Value : CameraCollisionMode.Auto;
                bool fat = BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(FatGuid);
                s_guardActive = mode == CameraCollisionMode.On || (mode == CameraCollisionMode.Auto && fat);
                FiresCore.Logging.FiresLogger.LogDebug($"[Camera] collision guard: {(s_guardActive ? "active" : "off")} (FAT present {fat}, setting {mode}{(mode == CameraCollisionMode.Auto ? ", follows FAT" : ", explicit")})");
                return s_guardActive;
            }
        }
        /// <summary>Camera guard state: eased distance behind the eye (m), -1 when idle.</summary>
        private static float s_guardDist = -1f, s_guardVel;
        private static bool s_guardOn;
        private static float s_nextGuardLog;
        private const float RoofReach = 2.5f, GuardSmooth = 0.15f;

        private static float s_nextPullLog;

        /// <summary>0.2.368: one rate-limited Debug line whenever a Core camera pull-in engages.</summary>
        internal static void LogPullIn(string source)
        {
            if (Time.time < s_nextPullLog) return;
            s_nextPullLog = Time.time + 2f;
            CameraCollisionMode mode = s_cameraGuard != null ? s_cameraGuard.Value : CameraCollisionMode.Auto;
            FiresCore.Logging.FiresLogger.LogDebug($"[Camera] pull-in by {source} (toggle {mode} -> {(CameraGuardEnabled ? "active" : "off")})");
        }

        private static void GuardLog(string line)
        {
            if (Time.time < s_nextGuardLog) return;
            s_nextGuardLog = Time.time + 2f;
            FiresCore.Logging.FiresLogger.LogDebug(line);
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
            // 0.2.369 (Fire: a tree forced a crouch): only a solid overhead surface counts. Every hit is read; trees, logs, tree
            // destructibles and vegetation are skipped, and the surface must face down (a roof / cave underside, normal.y < -0.5).
            RaycastHit[] hits = Physics.SphereCastAll(feet + Vector3.up * start, radius, Vector3.up, reach, Mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                RaycastHit hit = hits[i];
                if (!IsCeiling(hit)) continue;
                float h = start + hit.distance + radius;
                if (h < best) { best = h; s_lastCeiling = hit.collider; }
            }
        }

        private static Collider s_lastCeiling;
        private static int s_vegLayer = -2, s_treeLayer = -2;

        /// <summary>True for a solid, mostly horizontal surface above the head: terrain, a cave roof, a piece's roof / floor / beam underside.</summary>
        private static bool IsCeiling(RaycastHit hit)
        {
            Collider c = hit.collider;
            if (c == null || hit.distance <= 0f) return false;   // distance 0 = the sphere started inside it (a trunk beside the player)
            if (hit.normal.y >= -0.5f) return false;              // not facing down: a wall, a trunk's side, a branch's top
            if (c.attachedRigidbody != null && c.attachedRigidbody.GetComponent<Character>() != null) return false;
            if (s_vegLayer == -2) { s_vegLayer = LayerMask.NameToLayer("vegetation"); s_treeLayer = LayerMask.NameToLayer("tree"); }
            int layer = c.gameObject.layer;
            if ((s_vegLayer >= 0 && layer == s_vegLayer) || (s_treeLayer >= 0 && layer == s_treeLayer)) return false;
            if (c.GetComponentInParent<TreeBase>() != null || c.GetComponentInParent<TreeLog>() != null) return false;
            Destructible d = c.GetComponentInParent<Destructible>();
            if (d != null && d.m_destructibleType == DestructibleType.Tree) return false;
            return true;
        }

        private static string CeilingName()
        {
            Collider c = s_lastCeiling;
            if (c == null) return "?";
            return $"{c.name}/{LayerMask.LayerToName(c.gameObject.layer)}";
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
            // 0.2.387 (bug: caves forced a crouch without FAT): the low-ceiling crouch is FAT's (Movement). Without FAT this runs only
            // for a crawl (Crawling, live with RPGClasses), i.e. only where the ceiling is too low even to crouch.
            if (!FiresCore.Config.CoreFeatures.IsLive(FiresCore.Config.CoreFeatures.Movement)
                && !Crawl.Active && !(Crawl.Enabled && ceiling < crawlBelow))
            {
                Release(player, null);
                return;
            }
            if (ceiling < need && Crawl.Enabled)
            {
                if (ceiling < crawlBelow)
                {
                    s_crawlRoomSince = -1f;
                    if (!Crawl.Active) FiresCore.Logging.FiresLogger.LogDebug($"[Headroom] forced crouch by {CeilingName()} (toggle Low ceilings={Enabled}; crawl)");
                    Crawl.Begin(player, $"ceiling {ceiling:0.00} m (a crouch needs {crawlBelow:0.00} m)");
                }
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
                    FiresCore.Logging.FiresLogger.LogDebug($"[Headroom] forced crouch by {CeilingName()} (toggle Low ceilings={Enabled})");
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

        private static float s_nextVanillaLog;

        // 0.2.369 (Fire): Camera collision Off = the camera stays at the wheel-set distance, vanilla's occlusion pull-in included.
        // 1.0: private void GameCamera.CollideRay2(Vector3 eyePos, Vector3 offsetedEyePos, ref Vector3 end) shortens 'end' (the
        // m_distance point). Vanilla runs as usual; when the toggle resolves Off the postfix puts 'end' back to the un-collided point.
        // The player's own zoom (m_distance, its minimum and 0 = first person) is already in 'end', so it is kept.
        [HarmonyPatch(typeof(GameCamera), "CollideRay2")]
        private static class GameCamera_CollideRay2_Toggle
        {
            private static void Prefix(ref Vector3 end, out Vector3 __state) => __state = end;

            private static void Postfix(ref Vector3 end, Vector3 __state)
            {
                if ((end - __state).sqrMagnitude < 0.0001f) return;
                // 0.2.387 (bug): Auto without FAT resolved the guard off and that also suppressed vanilla's own pull-in. Only an
                // explicit Off suppresses vanilla; Auto never does.
                CameraCollisionMode mode = s_cameraGuard != null ? s_cameraGuard.Value : CameraCollisionMode.Auto;
                bool suppress = mode == CameraCollisionMode.Off;
                if (suppress) end = __state;
                if (Time.time < s_nextVanillaLog) return;
                s_nextVanillaLog = Time.time + 2f;
                FiresCore.Logging.FiresLogger.LogDebug(suppress ? "[Camera] vanilla pull-in suppressed (toggle Off)" : $"[Camera] pull-in by vanilla occlusion (toggle {mode})");
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
                    // 0.2.367: off = vanilla camera untouched; on = acts only under a roof, eased, never closer than vanilla's minimum.
                    if (!CameraGuardEnabled) { s_guardDist = -1f; s_guardOn = false; return; }
                    Player player = Player.m_localPlayer;
                    if (player == null || player.m_eye == null || player.InIntro()) { s_guardDist = -1f; return; }
                    Vector3 eye = player.m_eye.position;
                    Vector3 d = pos - eye;
                    float dist = d.magnitude;
                    if (dist < 0.05f) { s_guardDist = -1f; return; }
                    Vector3 dir = d / dist;
                    Camera camera = __instance.m_camera != null ? __instance.m_camera : Camera.main;
                    float radius = Mathf.Max(CameraMinRadius, (camera != null ? camera.nearClipPlane : 0.1f) + CameraClipMargin);
                    int mask = __instance.m_blockCameraMask.value != 0 ? __instance.m_blockCameraMask.value : Mask;

                    float roof = float.PositiveInfinity;
                    if (Physics.Raycast(eye, Vector3.up, out RaycastHit up, RoofReach, mask, QueryTriggerInteraction.Ignore)) roof = up.distance;
                    bool underRoof = s_forced || !float.IsPositiveInfinity(roof);

                    float target = dist;   // vanilla's answer unless the guard pulls in
                    if (underRoof && !Physics.CheckSphere(eye, radius * 0.5f, mask, QueryTriggerInteraction.Ignore)
                        && Physics.SphereCast(eye, radius, dir, out RaycastHit hit, dist, mask, QueryTriggerInteraction.Ignore)
                        && !(hit.collider != null && hit.collider.attachedRigidbody != null && hit.collider.attachedRigidbody.GetComponent<Character>() != null))
                    {
                        float floor = Mathf.Min(__instance.m_minDistance, dist);
                        target = Mathf.Max(floor, hit.distance - CameraClipMargin);
                    }

                    if (s_guardDist < 0f)
                    {
                        if (target >= dist - 0.01f) return;   // idle and nothing to do: vanilla stands
                        s_guardDist = dist; s_guardVel = 0f;
                    }
                    s_guardDist = Mathf.SmoothDamp(s_guardDist, target, ref s_guardVel, GuardSmooth);
                    if (s_guardDist > dist) s_guardDist = dist;   // never further out than vanilla
                    bool engaged = target < dist - 0.01f;
                    if (!engaged && s_guardDist >= dist - 0.01f)
                    {
                        s_guardDist = -1f;
                        if (s_guardOn) { s_guardOn = false; GuardLog("[Headroom] camera guard off"); }
                        return;
                    }
                    if (engaged && !s_guardOn) { s_guardOn = true; LogPullIn("Headroom roof guard");GuardLog($"[Headroom] camera guard on: roof at {(float.IsPositiveInfinity(roof) ? 0f : roof):0.00} m"); }
                    pos = eye + dir * s_guardDist;
                }
                catch (Exception ex) { Debug.LogWarning($"[Headroom] camera guard: {ex.Message}"); }
            }
        }
    }
}
