using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Movement
{
    /// <summary>
    /// 0.2.967 first person mode (Fire 10-28: "make core handle proper first person mode just like the first person mods ... you allow
    /// scrolling in to scroll into a first person camera as the camera gets close enough and you hide the player's head to themselves").
    /// Local player only, written from our own camera code (CrawlCamera's head point / stand-downs, PossessionProxy's save and restore).
    ///  - Scroll in to the zoom-in threshold: GameCamera's minimum distance drops to 0 and the camera is vanilla's first person (m_eye),
    ///    moved EyeForward along the look and EyeRaise up. Scroll out: the player's minimum and a third-person distance come back.
    ///  - The head is hidden on your own screen: the head bone is shrunk and helmet / hair / beard are compensated and drawn shadows-only;
    ///    a shadows-only copy of the body keeps the head's own shadow (Fire: keep it). Vanilla's "camera within 2 m hides the player"
    ///    is held off so the body and its shadow stay.
    ///  - Seats, ships and mounts: the look yaw is limited to +-AttachedYawLimit off the body (Fire: never look into your own body).
    ///  - Bow aim (bows only, Fire): the held bow is drawn with cached ghost materials at GhostBowAlpha; one swap per aim start / stop.
    ///  - Stands down in the map, cutscenes and the intro, while dead (resumes on respawn), for FiresValcast camera modes, possession,
    ///    First Person POV and CrawlCamera's own crawl first person.
    /// Other mods: Enter(owner, reason, raiseM) / Exit(owner, reason) / IsActive (add-only API). Gameplay addition: default off.
    /// Debug lines: "[FirstPerson] enter", "exit", "head hidden" / "head restored", "ghost bow on" / "off", "yaw limited", "request".
    /// </summary>
    public static class FirstPerson
    {
        private static readonly FiresCore.Config.OwnedSetting<bool> s_enabled = new FiresCore.Config.OwnedSetting<bool>("Camera", "First person mode", false), s_hideHead = new FiresCore.Config.OwnedSetting<bool>("Camera", "Hide your head in first person", true);   // 1086 ([generator], Fire 11-01: configs live in their own mods): owner FAPM
        private static readonly FiresCore.Config.OwnedSetting<float> s_threshold = new FiresCore.Config.OwnedSetting<float>("Camera", "First person zoom-in threshold (m)", 1f), s_forward = new FiresCore.Config.OwnedSetting<float>("Camera", "First person camera ahead of the face (m)", 0.08f), s_raise = new FiresCore.Config.OwnedSetting<float>("Camera", "First person eye raise (m)", 0f), s_yawLimit = new FiresCore.Config.OwnedSetting<float>("Camera", "First person turn limit when seated (deg)", 100f), s_ghostAlpha = new FiresCore.Config.OwnedSetting<float>("Camera", "First person bow opacity while aiming", 0.2f);
        private static readonly FiresCore.Config.OwnedSetting<float> s_fov = new FiresCore.Config.OwnedSetting<float>("Camera", "First person FOV", 85f), s_pullBack = new FiresCore.Config.OwnedSetting<float>("Camera", "First person body pull-back looking down (m)", 0.25f), s_lean = new FiresCore.Config.OwnedSetting<float>("Camera", "First person body lean looking down (deg)", 15f);   // 0.2.973 / 0.2.974
        private static readonly FiresCore.Config.OwnedSetting<float> s_armReach = new FiresCore.Config.OwnedSetting<float>("Camera", "First person arm reach (deg)", 40f), s_armStretch = new FiresCore.Config.OwnedSetting<float>("Camera", "First person upper arm stretch", 0.15f);   // 0.2.985

        public static bool Enabled => AdvancedMovement.Installed && s_enabled != null && s_enabled.Value;
        /// <summary>True while the local camera is in Core's first person (scrolled in or requested).</summary>
        public static bool IsActive => s_in;

        internal static void Bind(ConfigFile config)
        {
            // 1086 ([generator], Fire 11-01: configs live in their own mods): [Camera] first-person keys are bound by Fires Advanced Player Movement (Crosshair red over an enemy: FiresRPGClasses); no Core binds
            Terminal.ConsoleCommand unused = new Terminal.ConsoleCommand("fires_fp_trace", "[frames] - first person: log the camera, head and pose per frame (Debug) for N frames (default 240)", args =>
            {
                s_trace = args.Length > 1 && int.TryParse(args[1], out int n) ? Mathf.Clamp(n, 1, 2000) : 240;
                args.Context?.AddString($"[FirstPerson] tracing {s_trace} frame(s) (Debug lines \"[FirstPerson] trace\")");
                FiresCore.Logging.FiresLogger.LogInfo($"[FirstPerson] trace start: {s_trace} frame(s), steady head {(s_steadyHead.Value ? "ON" : "OFF")}, lean {s_lean?.Value ?? 15f:0} deg, arm reach {s_armReach?.Value ?? 40f:0} deg, first person {(s_in ? "on" : "OFF - scroll in first")}");
            });
        }
        private static readonly FiresCore.Config.OwnedSetting<bool> s_steadyHead = new FiresCore.Config.OwnedSetting<bool>("Camera", "First person camera follows the animated head", true);   // 0.2.996
        private static readonly FiresCore.Config.OwnedSetting<float> s_attackForward = new FiresCore.Config.OwnedSetting<float>("Camera", "First person camera extra forward while attacking (m)", 0.10f);   // 0.2.1000
        private static float s_attackNow; private static float s_nextAttackWhy;
        private static readonly FiresCore.Config.OwnedSetting<float> s_backLift = new FiresCore.Config.OwnedSetting<float>("Camera", "First person camera lift looking behind (m)", 0.35f);   // 0.2.1000
        private static float s_backNow, s_nextBackWhy;
        private static int s_trace;                      // 0.2.996: frames left to trace
        private static bool s_traceFreshSpine;           // 0.2.996: did the animator write Spine1 since our last turn (read in the UpdateCamera prefix)
        // 0.2.999: first_person_test A/B - an in-memory override of the steady-head key (null = the cfg; never written to the cfg)
        internal static bool? SteadyOverride;
        internal static bool SteadyOn => SteadyOverride ?? (s_steadyHead == null || s_steadyHead.Value);
        internal static bool LastSpineFresh => s_traceFreshSpine;
        private static bool s_steadyLogged;

        // 0.2.996 (Fire 10-29, round R: "first person camera still flickers every frame"; 0.2.988 only stopped the turns STACKING):
        // the camera reads the head bone in GetCameraPosition, inside UpdateCamera, BEFORE the postfix re-applies this frame's turns.
        // Spine1 is above the head; on a frame the animator skipped (it runs on the physics step, the screen runs faster) Spine1 still
        // holds LAST frame's look-down lean, so the head - and the camera following it - sat leaned; on the next animator frame it did
        // not: the camera jumped back and forth every frame while looking down. Put every turned bone back to the animator's pose
        // before the camera reads the head (like UnshiftVisual does for the pull-back); the postfix turns them again for the render.
        private static void UnturnPose()
        {
            int back = 0;
            s_traceFreshSpine = s_spine != null && !(s_pose.TryGetValue(s_spine, out var sp) && s_spine.localRotation == sp.written);
            if (!SteadyOn) return;   // 0.2.999: the test override first
            foreach (var kv in s_pose)
                if (kv.Key != null && kv.Key.localRotation == kv.Value.written) { kv.Key.localRotation = kv.Value.basePose; back++; }
            if (!s_steadyLogged && back > 0)
            {
                s_steadyLogged = true;
                FiresCore.Logging.FiresLogger.LogInfo("[FirstPerson] steady head on: the camera follows the animated head (the look-down lean no longer moves it on frames the animator skipped)");
            }
        }

        // ── API (add-only) ──────────────────────────────────────────────────────────────────────────────────────────────────────
        private static readonly Dictionary<string, (string reason, float raise)> s_requests = new Dictionary<string, (string, float)>();

        /// <summary>Put the local camera in first person for <paramref name="owner"/> (works with "First person mode" off). The player can still
        /// scroll out; <see cref="Exit"/> puts back the distance from before the first request.</summary>
        public static void Enter(string owner, string reason = null, float raiseM = 0f)
        {
            if (string.IsNullOrEmpty(owner)) return;
            if (!OwnerMods.Movement) { OwnerMods.Skip("APM", "first person request from " + owner + " refused"); return; }   // 0.2.1139
            s_requests[owner] = (reason ?? owner, Mathf.Clamp(raiseM, 0f, 1f));
            FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] request from {owner}: enter ({reason ?? "no reason given"}, raise {raiseM:0.00} m)");
            var gc = GameCamera.instance;
            if (gc != null && !s_in) { s_requestDistWas = Distance(gc); Go(gc, true, $"requested by {owner}"); }
        }

        public static void Exit(string owner, string reason = null)
        {
            if (string.IsNullOrEmpty(owner) || !s_requests.Remove(owner)) return;
            FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] request from {owner}: exit ({reason ?? "no reason given"})");
            var gc = GameCamera.instance;
            if (s_requests.Count == 0 && gc != null && s_requestDistWas >= 0f)
            {
                if (s_in) Go(gc, false, $"{owner} released it");
                Distance(gc) = s_requestDistWas;
                FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] camera distance back to {s_requestDistWas:0.0} m (from before the request)");
                s_requestDistWas = -1f;
            }
        }

        // ── state ───────────────────────────────────────────────────────────────────────────────────────────────────────────────
        private static readonly AccessTools.FieldRef<GameCamera, float> Distance = AccessTools.FieldRefAccess<GameCamera, float>("m_distance");
        private static readonly AccessTools.FieldRef<Character, Quaternion> LookYaw = AccessTools.FieldRefAccess<Character, Quaternion>("m_lookYaw");
        private static readonly AccessTools.FieldRef<Character, Vector3> LookDir = AccessTools.FieldRefAccess<Character, Vector3>("m_lookDir");
        private static readonly AccessTools.FieldRef<Player, float> LookPitch = AccessTools.FieldRefAccess<Player, float>("m_lookPitch");
        private static readonly AccessTools.FieldRef<Character, Transform> HeadBone = AccessTools.FieldRefAccess<Character, Transform>("m_head");
        private static readonly AccessTools.FieldRef<VisEquipment, GameObject> LeftItem = AccessTools.FieldRefAccess<VisEquipment, GameObject>("m_leftItemInstance");
        private static readonly AccessTools.FieldRef<VisEquipment, GameObject> HelmetItem = AccessTools.FieldRefAccess<VisEquipment, GameObject>("m_helmetItemInstance");
        private static readonly AccessTools.FieldRef<VisEquipment, GameObject> HairItem = AccessTools.FieldRefAccess<VisEquipment, GameObject>("m_hairItemInstance");
        private static readonly AccessTools.FieldRef<VisEquipment, GameObject> BeardItem = AccessTools.FieldRefAccess<VisEquipment, GameObject>("m_beardItemInstance");

        // 0.2.968: what first_person_test reads (set at the same point as the Debug lines)
        internal static int EnterCount, ExitCount, YawClampCount, GhostOnCount, GhostOffCount; internal static string LastExitWhy, GhostShaderPath;
        private static readonly FiresCore.Config.OwnedSetting<float> s_shieldAlpha = new FiresCore.Config.OwnedSetting<float>("Camera", "First person shield opacity while blocking", 0.2f), s_aimLift = new FiresCore.Config.OwnedSetting<float>("Camera", "First person camera lift while aiming a bow (m)", 0.12f); private static readonly FiresCore.Config.OwnedSetting<bool> s_enemyRing = new FiresCore.Config.OwnedSetting<bool>("Camera", "Crosshair red over an enemy", false);   // 0.2.1009; 1086: owner FiresRPGClasses; no provider = off (0.2.1036 rule)
        private static float s_aimNow, s_nextAimWhy;
        internal static bool HeadHidden => s_head != null;
        internal static GameObject HeadShadowCopy => s_shadowCopy;
        internal static float YawLimit => s_yawLimit != null ? s_yawLimit.Value : 100f;
        internal static float GhostAlpha => s_ghostAlpha != null ? s_ghostAlpha.Value : 0.2f;
        private static bool s_in, s_suspended, s_resumeOnRespawn, s_yawLogged;
        private static float s_minWas = -1f, s_nearMaxWas = -1f, s_nearMinWas = -1f, s_requestDistWas = -1f, s_raiseNow;
        private static string s_suspendWhy;
        private const float FpNearClip = 0.03f;   // 0.2.986: one fixed near plane in first person

        private static void Go(GameCamera gc, bool on, string why)
        {
            if (on == s_in) return;
            s_in = on;
            if (on)
            {
                if (s_minWas < 0f) s_minWas = gc.m_minDistance;
                if (s_nearMaxWas < 0f) s_nearMaxWas = gc.m_nearClipPlaneMax;
                if (s_nearMinWas < 0f) s_nearMinWas = gc.m_nearClipPlaneMin;
                gc.m_minDistance = 0f; Distance(gc) = 0f;
                // 0.2.986 (Fire 10-29: "colliding with ourself causing the weapon and our armor to glitch in and out every frame"): vanilla
                // UpdateNearClipping picks m_nearClipPlaneMin (0.1) whenever a sphere at the camera touches the block mask, else Max - with
                // the camera on our own head that test flips every frame, so the near plane jumped 0.05 <-> 0.1 and sliced the weapon /
                // armour in and out. Both are pinned to one value in first person, so the plane never moves.
                gc.m_nearClipPlaneMin = gc.m_nearClipPlaneMax = FpNearClip;
                FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] near clip pinned to {FpNearClip:0.00} m (was min {s_nearMinWas:0.00} / max {s_nearMaxWas:0.00}, which flipped every frame against our own body)");
                s_raiseNow = Raise();
                EnterCount++;
                FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] enter ({why}): min distance {s_minWas:0.0} -> 0, eye forward {s_forward?.Value ?? 0.12f:0.00} m, raise {s_raiseNow:0.00} m, near clip {gc.m_nearClipPlaneMax:0.00}");
            }
            else
            {
                if (s_minWas >= 0f) { gc.m_minDistance = s_minWas; s_minWas = -1f; }
                if (s_nearMaxWas >= 0f) { gc.m_nearClipPlaneMax = s_nearMaxWas; s_nearMaxWas = -1f; }
                if (s_nearMinWas >= 0f) { gc.m_nearClipPlaneMin = s_nearMinWas; s_nearMinWas = -1f; }   // 0.2.986
                if (Distance(gc) < gc.m_minDistance + 0.5f) Distance(gc) = gc.m_minDistance + 0.5f;
                ExitCount++; LastExitWhy = why;
                // 0.2.973 (Fire 10-28: "transitioning OUT of first person does not restore our body"): every exit restores the body
                var lp = Player.m_localPlayer;
                int n = s_shadowOnly.Count + (s_head != null ? 1 : 0);
                RestoreFov(gc); UnshiftVisual();
                if (lp != null) Restore(lp);
                FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] body restored ({n} renderer(s) / head) on exit ({why})");
                FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] exit ({why}): min distance back to {gc.m_minDistance:0.0}, camera at {Distance(gc):0.0} m");
            }
        }

        private static float Raise()
        {
            float r = s_raise != null ? Mathf.Clamp(s_raise.Value, 0f, 1f) : 0f;
            foreach (var kv in s_requests) r = Mathf.Max(r, kv.Value.raise);
            return r;
        }

        private static string StandDown(Player p)
        {
            if (p.IsDead()) return "dead";
            if (p.InIntro()) return "intro";
            if (p.InCutscene()) return "cutscene";
            if (Minimap.IsOpen()) return "map open";
            if (CrawlCamera.Active) return "crawl first person";
            if (CrawlCamera.PovFirstPerson()) return "First Person POV is in first person";
            if (FiresPossession.PossessionEngine.IsPossessing) return "possession";
            return CrawlCamera.ValcastOwner();
        }

        // ── scroll in / out, stand-downs ────────────────────────────────────────────────────────────────────────────────────────
        [HarmonyPatch(typeof(GameCamera), "UpdateCamera")]
        private static class GameCamera_UpdateCamera_FirstPerson
        {
            private static void Prefix(GameCamera __instance)
            {
                UnshiftVisual();   // 0.2.974: the animated pose first, so the camera follows the head everyone else sees
                if (s_in) UnturnPose();   // 0.2.996: the turned bones too (the spine lean moved the head on frames the animator skipped)
                try
                {
                    var p = Player.m_localPlayer;
                    if (p == null) return;
                    bool wanted = Enabled || s_requests.Count > 0;
                    if (!wanted) { if (s_in) Go(__instance, false, "the setting was turned off"); Restore(p); return; }

                    string why = StandDown(p);
                    if (why != null)
                    {
                        if (s_in) { Go(__instance, false, why); s_resumeOnRespawn = why == "dead"; }
                        if (!s_suspended) { s_suspended = true; s_suspendWhy = why; }
                        Restore(p);
                        return;
                    }
                    if (s_suspended)
                    {
                        s_suspended = false;
                        if (s_resumeOnRespawn || s_requests.Count > 0) { s_resumeOnRespawn = false; Go(__instance, true, $"back after {s_suspendWhy}"); }
                    }

                    bool free = !(Chat.instance != null && Chat.instance.HasFocus()) && !global::Console.IsVisible() && !InventoryGui.IsVisible() && !Menu.IsVisible() && !p.InPlaceMode();
                    float wheel = free ? ZInput.GetMouseScrollWheel() : 0f;
                    float threshold = s_threshold != null ? s_threshold.Value : 1f;
                    if (!s_in && wheel > 0f && Distance(__instance) <= Mathf.Max(threshold, __instance.m_minDistance) + 0.01f) Go(__instance, true, $"scrolled in to {Distance(__instance):0.0} m");
                    else if (s_in && wheel < 0f) Go(__instance, false, "scrolled out");
                    else if (s_in && Distance(__instance) > 0.05f) Go(__instance, false, $"zoomed out to {Distance(__instance):0.0} m");
                }
                catch (Exception ex) { Debug.LogWarning($"[FirstPerson] {ex.Message}"); }
            }

            private static void Postfix(GameCamera __instance)
            {
                var p = Player.m_localPlayer;
                if (p == null || !s_in) return;
                try { HideHead(p, s_hideHead == null || s_hideHead.Value); GhostBow(p); GhostShield(p); ApplyFov(__instance); AntiClip(p, __instance.transform.position); HideBack(p, true); ShoulderGuard(p, __instance.m_camera, true); }
                catch (Exception ex) { Debug.LogWarning($"[FirstPerson] {ex.Message}"); }
            }
        }

        // 0.2.972 (Fire 10-28: "the camera sits too far back so we end up blocking our view, the camera needs to sit in front of the
        // players face a tiny bit"): vanilla's first person is m_eye, a fixed point on the body root, so the head leaning into a walk or
        // run moves in front of it. The camera now follows the head bone (eye height = head + EyeAboveHead) and sits the cfg distance
        // ahead along the flat look direction (+ the raise).
        [HarmonyPatch(typeof(GameCamera), "GetCameraPosition")]
        private static class GameCamera_GetCameraPosition_FirstPerson
        {
            private const float EyeAboveHead = 0.1f;   // the head bone sits at the skull base; the eyes are about this far above it
            private static float s_nextCamWhy;
            [HarmonyPriority(Priority.Low)]
            private static void Postfix(ref Vector3 pos)
            {
                var p = Player.m_localPlayer;
                if (!s_in || p == null || p.m_eye == null) return;
                var head = HeadBone(p);
                Vector3 eyeWas = pos;
                if (head != null)
                {
                    Vector3 look = p.GetLookDir(); Vector3 flat = new Vector3(look.x, 0f, look.z);
                    flat = flat.sqrMagnitude > 0.0001f ? flat.normalized : p.transform.forward;
                    // 0.2.1000: eased extra forward while attacking (0.15 s in / out)
                    float want = p.InAttack() ? (s_attackForward != null ? s_attackForward.Value : 0.10f) : 0f;
                    s_attackNow = Mathf.MoveTowards(s_attackNow, want, Time.deltaTime * Mathf.Max(0.01f, s_attackForward != null ? s_attackForward.Value : 0.10f) / 0.15f);
                    if (want > 0f && Time.unscaledTime >= s_nextAttackWhy) { s_nextAttackWhy = Time.unscaledTime + 10f; FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] attacking: camera {want:0.00} m further ahead of the face (eased) so the shoulder stays behind it"); }
                    // 0.2.1000 (Fire 10-29: "when we run backwards because the camera and the players head are independent we end up
                    // seeing our cape or clothing and blocking the camera, so when were turned all the way around the camera needs to
                    // come up a bit"): looking more than 90 deg away from the body's facing lifts the camera (and moves it a little
                    // along the look) so it looks over the shoulders / armour / cape; 0 at 90 deg, full at 180 deg, eased
                    Vector3 body = p.transform.forward; body.y = 0f;
                    float off = body.sqrMagnitude > 0.0001f ? Vector3.Angle(body.normalized, flat) : 0f;
                    float wantBack = Mathf.Clamp01((off - 90f) / 90f);
                    s_backNow = Mathf.MoveTowards(s_backNow, wantBack, Time.deltaTime / 0.25f);
                    float lift = s_backNow * (s_backLift != null ? s_backLift.Value : 0.35f), ahead = s_backNow * 0.12f;
                    if (wantBack > 0.5f && Time.unscaledTime >= s_nextBackWhy) { s_nextBackWhy = Time.unscaledTime + 10f; FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] looking {off:0} deg away from the body: camera lifted {lift:0.00} m and {ahead:0.00} m along the look, over the shoulders"); }
                    // 0.2.1009 (Fire 10-30, shot 367: drawing a bow, the shoulder / arm fill the lower view): eased lift while a bow is drawn
                    var aw = p.GetCurrentWeapon();
                    bool aiming = aw != null && aw.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow && p.GetAttackDrawPercentage() > 0f;
                    float aimMax = s_aimLift != null ? s_aimLift.Value : 0.12f;
                    s_aimNow = Mathf.MoveTowards(s_aimNow, aiming ? aimMax : 0f, Time.deltaTime * Mathf.Max(0.01f, aimMax) / 0.2f);
                    if (aiming && aimMax > 0f && Time.unscaledTime >= s_nextAimWhy) { s_nextAimWhy = Time.unscaledTime + 10f; FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] aiming a bow: camera lifted {aimMax:0.00} m (eased) so the shoulder stays below the sight line"); }
                    lift += s_aimNow;
                    pos = head.position + Vector3.up * EyeAboveHead + flat * ((s_forward != null ? s_forward.Value : 0.08f) + s_attackNow + ahead) + Vector3.up * (s_raiseNow + lift);
                }
                else pos += p.GetLookDir() * (s_forward != null ? s_forward.Value : 0.08f) + Vector3.up * s_raiseNow;
                if (s_trace > 0)   // 0.2.996: per-frame proof (fires_fp_trace): positions relative to the player root, in its own frame
                {
                    s_trace--;
                    var root = p.transform; Vector3 hl = head != null ? root.InverseTransformPoint(head.position) : Vector3.zero, cl = root.InverseTransformPoint(pos);
                    FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] trace f{Time.frameCount} fixed{(Time.inFixedTimeStep ? "*" : "")} dt {Time.unscaledDeltaTime * 1000f:0.0} ms: animator spine {(s_traceFreshSpine ? "NEW" : "kept")}, pitch {LookPitch(p):0.0}, head ({hl.x:0.000},{hl.y:0.000},{hl.z:0.000}) cam ({cl.x:0.000},{cl.y:0.000},{cl.z:0.000}) steady {(SteadyOn ? "on" : "off")}{(SteadyOverride.HasValue ? " (test)" : "")}" + (s_trace == 0 ? " - trace end" : ""));
                }
                float now = Time.unscaledTime;
                if (head != null && now >= s_nextCamWhy && p.GetVelocity().sqrMagnitude > 1f)
                {
                    s_nextCamWhy = now + 5f;
                    Vector3 f = p.transform.forward; f.y = 0f; f.Normalize();
                    FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] camera while moving ({p.GetVelocity().magnitude:0.0} m/s): head bone {Vector3.Dot(head.position - p.transform.position, f):0.00} m ahead of the feet, vanilla eye {Vector3.Dot(eyeWas - p.transform.position, f):0.00} m, camera {Vector3.Dot(pos - p.transform.position, f):0.00} m (camera - head {Vector3.Dot(pos - head.position, f):0.00} m forward, {pos.y - head.position.y:0.00} m up)");
                }
            }
        }

        // vanilla hides the player while the camera is within 2 m; first person keeps the body (and its shadow)
        [HarmonyPatch(typeof(Character), "SetVisible")]
        private static class Character_SetVisible_FirstPerson
        {
            private static void Prefix(Character __instance, ref bool visible)
            {
                if (!visible && s_in && __instance == Player.m_localPlayer) visible = true;
            }
        }

        // seats / ships / mounts: the look yaw stays within the limit of the body's facing
        [HarmonyPatch(typeof(Player), "SetMouseLook")]
        private static class Player_SetMouseLook_FirstPerson
        {
            private static void Postfix(Player __instance)
            {
                if (!s_in || __instance != Player.m_localPlayer) return;
                if (!__instance.IsAttached()) { s_yawLogged = false; return; }
                float limit = s_yawLimit != null ? s_yawLimit.Value : 100f;
                float body = __instance.transform.eulerAngles.y;
                float d = Mathf.DeltaAngle(body, LookYaw(__instance).eulerAngles.y);
                if (Mathf.Abs(d) <= limit) return;
                LookYaw(__instance) = Quaternion.Euler(0f, body + Mathf.Sign(d) * limit, 0f);
                __instance.m_eye.rotation = LookYaw(__instance) * Quaternion.Euler(LookPitch(__instance), 0f, 0f);
                LookDir(__instance) = __instance.m_eye.forward;
                YawClampCount++;
                if (!s_yawLogged) { s_yawLogged = true; FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] yaw limited to +-{limit:0} deg off the body while attached (seat / ship / mount)"); }
            }
        }

        [HarmonyPatch(typeof(Player), "OnDestroy")]
        private static class Player_OnDestroy_FirstPerson
        {
            private static void Prefix(Player __instance)
            {
                if (__instance != Player.m_localPlayer) return;
                Restore(__instance);
                if (s_in && GameCamera.instance != null) Go(GameCamera.instance, false, "the player left");
                s_in = false; s_suspended = false; s_resumeOnRespawn = s_resumeOnRespawn || s_requests.Count > 0;
            }
        }

        private static void Restore(Player p) { HideHead(p, false); s_pose.Clear(); HideBack(p, false); ShoulderGuard(p, null, false); UnstretchArms(); GhostShield(p, true); if (s_ghostBow != null || s_ghosted.Count > 0) GhostBow(p, true); }

        // ── head ────────────────────────────────────────────────────────────────────────────────────────────────────────────────
        private const float Shrink = 0.001f;
        private static Transform s_head; private static Vector3 s_headScale;
        private static readonly List<(Transform t, Vector3 scale)> s_grown = new List<(Transform, Vector3)>();
        private static readonly List<(Renderer r, ShadowCastingMode mode)> s_shadowOnly = new List<(Renderer, ShadowCastingMode)>();
        private static GameObject s_shadowCopy; private static Transform s_headProxy;
        private static GameObject s_lastHelmet, s_lastHair, s_lastBeard;

        private static void HideHead(Player p, bool hide)
        {
            if (!hide)
            {
                if (s_head == null && s_shadowCopy == null && s_shadowOnly.Count == 0) return;
                if (s_head != null) s_head.localScale = s_headScale;
                foreach (var (t, scale) in s_grown) if (t != null) t.localScale = scale;
                foreach (var (r, mode) in s_shadowOnly) if (r != null) r.shadowCastingMode = mode;
                s_grown.Clear(); s_shadowOnly.Clear(); s_head = null;
                if (s_shadowCopy != null) UnityEngine.Object.Destroy(s_shadowCopy);
                if (s_headProxy != null) UnityEngine.Object.Destroy(s_headProxy.gameObject);
                s_shadowCopy = null; s_headProxy = null; s_lastHelmet = s_lastHair = s_lastBeard = null;
                FiresCore.Logging.FiresLogger.LogDebug("[FirstPerson] head restored");
                return;
            }
            var vis = p.GetComponent<VisEquipment>();
            if (vis == null) return;
            GameObject helmet = HelmetItem(vis), hair = HairItem(vis), beard = BeardItem(vis);
            if (s_head != null && helmet == s_lastHelmet && hair == s_lastHair && beard == s_lastBeard)
            {
                s_head.localScale = s_headScale * Shrink;   // the animator may write scale: held every frame (no allocation)
                FollowHead();
                return;
            }
            if (s_head != null) HideHead(p, false);   // equipment changed: redo once
            var head = HeadBone(p);
            if (head == null) return;
            s_head = head; s_headScale = head.localScale; s_lastHelmet = helmet; s_lastHair = hair; s_lastBeard = beard;
            head.localScale = s_headScale * Shrink;
            // helmet / hair / beard sit under the head bone: grown back to full size and drawn as shadows only
            foreach (var go in new[] { helmet, hair, beard })
            {
                if (go == null || !go.transform.IsChildOf(head)) continue;
                s_grown.Add((go.transform, go.transform.localScale));
                go.transform.localScale = go.transform.localScale / Shrink;
                foreach (var r in go.GetComponentsInChildren<Renderer>())
                    if (r.shadowCastingMode != ShadowCastingMode.ShadowsOnly) { s_shadowOnly.Add((r, r.shadowCastingMode)); r.shadowCastingMode = ShadowCastingMode.ShadowsOnly; }
            }
            MakeShadowCopy(vis.m_bodyModel, head);
            FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] head hidden: head bone x{Shrink}, {s_grown.Count} head item(s) shadows-only ({s_shadowOnly.Count} renderer(s)), head shadow copy {(s_shadowCopy != null ? "made" : "not made (no body model)")}");
        }

        // the head's own shadow: a shadows-only copy of the body mesh whose head bone (and the bones under it) is a full-size proxy
        private static void MakeShadowCopy(SkinnedMeshRenderer body, Transform head)
        {
            if (body == null || body.sharedMesh == null) return;
            s_headProxy = new GameObject("fires_fp_head_proxy").transform;
            s_headProxy.SetParent(head.parent, false);
            FollowHead();
            var bones = body.bones;
            var mapped = new Transform[bones.Length];
            for (int i = 0; i < bones.Length; i++) mapped[i] = bones[i] != null && bones[i].IsChildOf(head) ? s_headProxy : bones[i];
            s_shadowCopy = new GameObject("fires_fp_head_shadow");
            s_shadowCopy.transform.SetParent(body.transform.parent, false);
            s_shadowCopy.transform.localPosition = body.transform.localPosition; s_shadowCopy.transform.localRotation = body.transform.localRotation; s_shadowCopy.transform.localScale = body.transform.localScale;
            s_shadowCopy.layer = body.gameObject.layer;
            var smr = s_shadowCopy.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = body.sharedMesh; smr.sharedMaterials = body.sharedMaterials; smr.rootBone = body.rootBone; smr.bones = mapped;
            smr.shadowCastingMode = ShadowCastingMode.ShadowsOnly; smr.receiveShadows = false; smr.updateWhenOffscreen = true;
        }

        private static void FollowHead()
        {
            if (s_headProxy == null || s_head == null) return;
            s_headProxy.localPosition = s_head.localPosition; s_headProxy.localRotation = s_head.localRotation; s_headProxy.localScale = s_headScale;
        }

        // ── world FOV + anti-clip (0.2.974) ─────────────────────────────────────────────────────────────────────────────────────
        // Fire 10-28 ("we should be able to look down and see our legs and chest ... the chest pulls back away from the camera"): one
        // body, drawn whole except the head. As the look pitches down, the player's visual is pushed back along the flat look and the
        // upper spine leans back, so the camera never ends up inside the chest. Local only: the visual offset is put back in the
        // UpdateCamera prefix (before the camera reads the head) and the spine lean is overwritten by the animator every frame.
        private static Transform s_visual, s_spine; private static Vector3 s_visualBase; private static bool s_visualShifted;
        private static float s_nextClipWhy;

        private static void ApplyFov(GameCamera gc)
        {
            if (gc.m_camera == null) return;
            gc.m_camera.fieldOfView = s_fov != null ? s_fov.Value : 85f;
            if (gc.m_skyCamera != null) gc.m_skyCamera.fieldOfView = gc.m_camera.fieldOfView;
        }

        private static void RestoreFov(GameCamera gc)
        {
            if (gc == null || gc.m_camera == null) return;
            gc.m_camera.fieldOfView = gc.m_fov; if (gc.m_skyCamera != null) gc.m_skyCamera.fieldOfView = gc.m_fov;
        }

        // the UpdateCamera prefix: back to the animated pose, so the head the camera follows is where everyone else sees it
        private static void UnshiftVisual()
        {
            UnstretchArms();   // 0.2.985
            if (!s_visualShifted) return;
            if (s_visual != null) s_visual.localPosition = s_visualBase;
            s_visualShifted = false;
        }

        private static void AntiClip(Player p, Vector3 camPos)
        {
            var visGo = p.m_visual;
            if (visGo == null) return;
            if (s_visual != visGo.transform)
            {
                s_visual = visGo.transform; s_visualBase = s_visual.localPosition; s_spine = null;
                foreach (var t in s_visual.GetComponentsInChildren<Transform>(true)) if (t.name == "Spine1") { s_spine = t; break; }
            }
            float pitch = LookPitch(p);   // + = looking down
            float k = Mathf.Clamp01(pitch / 60f);
            float pull = k * (s_pullBack != null ? s_pullBack.Value : 0.25f);
            float lean = k * (s_lean != null ? s_lean.Value : 15f);
            Vector3 look = p.GetLookDir(); Vector3 flat = new Vector3(look.x, 0f, look.z);
            flat = flat.sqrMagnitude > 0.0001f ? flat.normalized : p.transform.forward;
            if (pull > 0.001f)
            {
                Vector3 local = s_visual.parent != null ? s_visual.parent.InverseTransformVector(-flat * pull) : -flat * pull;
                s_visual.localPosition = s_visualBase + local; s_visualShifted = true;
            }
            ArmsForward(p, flat);   // 0.2.985: the hands and the held item in the lower view
            if (s_spine != null && lean > 0.01f)
            {
                Vector3 right = Vector3.Cross(Vector3.up, flat);
                PoseTurn(s_spine, Quaternion.AngleAxis(-lean, right));   // 0.2.988: on the animator's pose, never stacked
            }
            float now = Time.unscaledTime;
            if (now >= s_nextClipWhy && pitch > 20f)
            {
                s_nextClipWhy = now + 5f;
                float chest = s_spine != null ? Vector3.Dot(camPos - s_spine.position, flat) : float.NaN;
                FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] looking down {pitch:0} deg: body pulled back {pull:0.00} m, spine leaned {lean:0.0} deg; camera {chest:0.00} m ahead of the chest (Spine1 {(s_spine != null ? "found" : "NOT found")})");
            }
        }

        // ── arms in view + back items (0.2.985) ─────────────────────────────────────────────────────────────────────────────────
        // Fire 10-29: "first person should move our arms out a bit further so we can see then (i think AAA games stretch the upper arm?)
        // ... when running backwards our camera shows our backpack, it should hide it". After the animator, local only: both upper arms
        // turn forward by "First person arm reach (deg)" and stretch by "First person upper arm stretch" (scale put back before the
        // next animation, like the pull-back), so the hands and the held item sit in the lower view; the back items (both back slots:
        // a sheathed weapon, a shield on the back) and any backpack draw as shadows only on this screen, restored on exit.
        private static Transform s_lArm, s_rArm; private static Vector3 s_lArmScale, s_rArmScale; private static bool s_armsStretched;
        private static readonly List<(Renderer r, ShadowCastingMode mode)> s_backHidden = new List<(Renderer, ShadowCastingMode)>();
        private static float s_nextBackScan, s_nextArmWhy;

        // 0.2.988 (Fire 10-29 clip 04.12.45, 120 fps: the own arm / armour at the bottom of the view jumps between two poses frame to
        // frame): the player's animator does not evaluate on every rendered frame (it follows the physics step), but the arm reach and
        // the spine lean were ADDED to the bone each frame - on a frame without a new animator pose they stacked on last frame's turn
        // (double reach / lean), the next pose reset it: the flicker. Each turn is now applied to the animator's own pose: if the bone
        // still holds what we wrote last frame, the animator did not run, so we start again from the pose it gave us then.
        private static readonly Dictionary<Transform, (Quaternion written, Quaternion basePose)> s_pose = new Dictionary<Transform, (Quaternion, Quaternion)>();
        private static int s_poseReused, s_poseFresh; private static float s_nextPoseWhy;
        private static void PoseTurn(Transform t, Quaternion worldDelta)
        {
            if (t == null) return;
            if (s_pose.TryGetValue(t, out var p) && t.localRotation == p.written) { t.localRotation = p.basePose; s_poseReused++; }   // no new animator pose
            else s_poseFresh++;
            var basePose = t.localRotation;
            t.rotation = worldDelta * t.rotation;
            s_pose[t] = (t.localRotation, basePose);
            if (Time.unscaledTime >= s_nextPoseWhy)
            {
                s_nextPoseWhy = Time.unscaledTime + 10f;
                FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] pose turns last 10 s: {s_poseFresh} on a new animator pose, {s_poseReused} re-applied to the last pose (frames the animator skipped; these used to stack and flicker)");
                s_poseFresh = s_poseReused = 0;
            }
        }

        // Debug proof for Fire's 11-02 "aim about 1m low": one line per first-person shot, predicting the arrow at the crosshair's distance.
        [HarmonyPatch(typeof(Projectile), nameof(Projectile.Setup))]
        private static class Projectile_Setup_FirstPersonAimLog
        {
            private static readonly AccessTools.FieldRef<Humanoid, Attack> CurrentAttack = AccessTools.FieldRefAccess<Humanoid, Attack>("m_currentAttack");
            private static void Postfix(Projectile __instance, Character owner, Vector3 velocity)
            {
                try
                {
                    if (!s_in || owner == null || owner != Player.m_localPlayer || GameCamera.instance == null) return;
                    var cam = GameCamera.instance.transform;
                    var p = (Player)owner;
                    var atk = CurrentAttack(p);
                    string joint = atk != null ? atk.m_attackOriginJoint : "?";
                    Vector3 spawn = __instance.transform.position;
                    Vector3 local = cam.InverseTransformPoint(spawn);
                    string hit = "no hit within 200 m";
                    if (Physics.Raycast(cam.position, cam.forward, out var rh, 200f, Physics.DefaultRaycastLayers) && velocity.sqrMagnitude > 0.01f)
                    {
                        float flat = new Vector2(rh.point.x - spawn.x, rh.point.z - spawn.z).magnitude;
                        float vFlat = new Vector2(velocity.x, velocity.z).magnitude;
                        float t = vFlat > 0.01f ? flat / vFlat : 0f;
                        float y = spawn.y + velocity.y * t - 0.5f * __instance.m_gravity * t * t;
                        hit = $"crosshair hit {rh.distance:0.0} m '{rh.collider.name}'; predicted arrow {y - rh.point.y:+0.00;-0.00} m vs the crosshair point";
                    }
                    FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] bow shot: joint '{joint}' launchAngle {(atk != null ? atk.m_launchAngle : 0f):0.0}, spawn in camera space {local.ToString("F2")}, look pitch {LookPitch(p):0.0}, velocity {velocity.ToString("F1")} ({Vector3.Angle(cam.forward, velocity):0.0} deg from the camera), gravity {__instance.m_gravity:0.0}; {hit}");
                }
                catch (System.Exception ex) { FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] bow shot log failed: {ex.Message}"); }
            }
        }

        private static void UnstretchArms()
        {
            if (!s_armsStretched) return;
            if (s_lArm != null) s_lArm.localScale = s_lArmScale;
            if (s_rArm != null) s_rArm.localScale = s_rArmScale;
            s_armsStretched = false;
        }

        private static void ArmsForward(Player p, Vector3 flat)
        {
            if (s_visual == null) return;
            if (s_lArm == null || s_rArm == null || !s_lArm.IsChildOf(s_visual))
            {
                s_lArm = s_rArm = null;
                foreach (var t in s_visual.GetComponentsInChildren<Transform>(true)) { if (t.name == "LeftArm") s_lArm = t; else if (t.name == "RightArm") s_rArm = t; }
            }
            float reach = s_armReach != null ? s_armReach.Value : 40f, stretch = s_armStretch != null ? s_armStretch.Value : 0.15f;
            Vector3 right = Vector3.Cross(Vector3.up, flat);
            // Fire 11-02: "when we look down with the bow our left arm needs to go down too". One turn per bone (PoseTurn works on the
            // animator's pose). +angle about right = down; only while a bow is drawn, and only looking down.
            var aw = p.GetCurrentWeapon();
            bool bowDrawn = aw != null && aw.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow && p.GetAttackDrawPercentage() > 0f;
            float bowDown = bowDrawn ? Mathf.Max(0f, LookPitch(p)) : 0f;
            foreach (var arm in new[] { s_lArm, s_rArm })
            {
                if (arm == null) continue;
                float turn = -reach + (arm == s_lArm ? bowDown : 0f);
                if (Mathf.Abs(turn) > 0.01f) PoseTurn(arm, Quaternion.AngleAxis(turn, right));   // 0.2.988
            }
            if (stretch > 0.001f && !s_armsStretched)
            {
                if (s_lArm != null) { s_lArmScale = s_lArm.localScale; s_lArm.localScale = s_lArmScale * (1f + stretch); }
                if (s_rArm != null) { s_rArmScale = s_rArm.localScale; s_rArm.localScale = s_rArmScale * (1f + stretch); }
                s_armsStretched = true;
            }
            if (Time.unscaledTime >= s_nextArmWhy)
            {
                s_nextArmWhy = Time.unscaledTime + 10f;
                FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] arms in view: upper arms {reach:0} deg forward, bow left arm down {bowDown:0}, stretched x{1f + stretch:0.00} (LeftArm {(s_lArm != null ? "found" : "NOT found")}, RightArm {(s_rArm != null ? "found" : "NOT found")}); {s_backHidden.Count} back renderer(s) shadows-only");
            }
        }

        private static readonly AccessTools.FieldRef<VisEquipment, List<GameObject>> s_shoulderItems = AccessTools.FieldRefAccess<VisEquipment, List<GameObject>>("m_shoulderItemInstances");   // 0.2.1000
        // 0.2.1000 (Fire 10-29, round S: "when we do attacks or the character turns it can cause the shoulder to block the camera", then
        // "when were standing still the shoulder and arm look great so i dont want to completely just hide it, maybe only hide it when
        // it crosses the camera during animations?"): the shoulder-slot item (capes, fur collars) stays drawn; every frame, when either
        // shoulder bone comes inside the camera's near zone (in view, closer than ShoulderNear), the item goes shadows-only on this
        // screen, held ShoulderHold s after it leaves (no on / off flicker), then drawn again. A cheap toggle, no fade; others see it always.
        private const float ShoulderNear = 0.30f, ShoulderHold = 0.2f;
        // 0.2.1005: the mesh test (only while animating)
        private const float MeshNear = 0.25f;
        private static Mesh s_bake; private static readonly List<Vector3> s_bakeVerts = new List<Vector3>();
        private static float s_nextMeshWhy, s_nextChestWhy;
        private static readonly AccessTools.FieldRef<VisEquipment, List<GameObject>> s_chestItems = AccessTools.FieldRefAccess<VisEquipment, List<GameObject>>("m_chestItemInstances");
        // an attack, or any animator state that is not plain locomotion (dodge, emote, block, interact ...)
        private static bool Animating(Player p)
        {
            if (p.InAttack()) return true;
            var an = p.GetComponentInChildren<Animator>();
            if (an == null) return false;
            for (int l = 1; l < an.layerCount; l++) if (an.GetLayerWeight(l) > 0.1f && an.GetCurrentAnimatorClipInfoCount(l) > 0) return true;   // upper-body layers (attacks, blocks)
            return an.IsInTransition(0) && !p.IsOnGround();
        }
        private static string AnimName(Player p)
        {
            var an = p.GetComponentInChildren<Animator>();
            if (an == null) return "?";
            var names = new List<string>();
            for (int l = 0; l < an.layerCount; l++)
            {
                if (l > 0 && an.GetLayerWeight(l) <= 0.1f) continue;
                var info = an.GetCurrentAnimatorClipInfo(l);
                if (info.Length > 0 && info[0].clip != null) names.Add(info[0].clip.name);
            }
            return names.Count > 0 ? string.Join(" + ", names) : "?";
        }
        // the nearest baked vertex of the shoulder item that is in view (camera depth), or MaxValue
        private static float ShoulderMeshNear(Player p, Camera cam)
        {
            var vis = p.GetComponent<VisEquipment>();
            return ItemsMeshNear(vis != null ? s_shoulderItems(vis) : null, cam);
        }
        // the nearest baked vertex of these item instances that is in view (camera depth), or MaxValue
        private static float ItemsMeshNear(List<GameObject> items, Camera cam)
        {
            float best = float.MaxValue;
            if (items == null) return best;
            if (s_bake == null) s_bake = new Mesh();
            foreach (var go in items)
            {
                if (go == null) continue;
                foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>())
                {
                    if (smr == null || smr.sharedMesh == null) continue;
                    smr.BakeMesh(s_bake, true); s_bake.GetVertices(s_bakeVerts);
                    var m = smr.transform.localToWorldMatrix;
                    for (int i = 0; i < s_bakeVerts.Count; i += 2)   // every other vertex: plenty for cloth this close
                    {
                        Vector3 vp = cam.WorldToViewportPoint(m.MultiplyPoint3x4(s_bakeVerts[i]));
                        if (vp.z > 0f && vp.z < best && vp.x > -0.1f && vp.x < 1.1f && vp.y > -0.1f && vp.y < 1.1f) best = vp.z;
                    }
                }
            }
            return best;
        }
        private static readonly List<(Renderer r, ShadowCastingMode mode)> s_shoulderHidden = new List<(Renderer, ShadowCastingMode)>();
        private static Transform s_lShoulder, s_rShoulder; private static float s_shoulderUntil;
        private static void ShoulderGuard(Player p, Camera cam, bool active)
        {
            float near = float.MaxValue;
            if (active && cam != null && s_visual != null)
            {
                if (s_lShoulder == null || s_rShoulder == null || !s_lShoulder.IsChildOf(s_visual))
                {
                    s_lShoulder = s_rShoulder = null;
                    foreach (var t in s_visual.GetComponentsInChildren<Transform>(true)) { if (t.name == "LeftShoulder") s_lShoulder = t; else if (t.name == "RightShoulder") s_rShoulder = t; }
                }
                foreach (var b in new[] { s_lShoulder, s_rShoulder })
                {
                    if (b == null) continue;
                    Vector3 vp = cam.WorldToViewportPoint(b.position);
                    if (vp.z > 0f && vp.x > -0.15f && vp.x < 1.15f && vp.y > -0.15f && vp.y < 1.15f) near = Mathf.Min(near, vp.z);
                }
            }
            bool block = near < ShoulderNear;
            // 0.2.1005 (Fire 10-29 hand test on 1002: "we still end up blocking our view with our shoulder during attacks sometimes ...
            // its only the attacks and some animations now"; shot 361 = the fur shoulder item across the whole view mid-swing while the
            // log showed the bone test firing only on some swings): during an attack / any non-locomotion animation, the shoulder
            // item's own MESH is tested too (baked, every frame only while animating): any vertex in view closer than MeshNear blocks.
            // The bone sits at the joint, but the cape / collar cloth reaches far past it when the torso twists - that is what the
            // bone test missed. Standing / walking keeps the bone test only, so the shoulder stays drawn there (Fire: "looks great").
            if (!block && active && cam != null && p != null && Animating(p))
            {
                float mesh = ShoulderMeshNear(p, cam);
                // proof for the other suspect: the CHEST armour's own shoulders (one mesh with the arms, so it is never hidden): logged
                // once a second while animating when it is the thing in front of the camera
                if (mesh >= MeshNear && Time.unscaledTime >= s_nextChestWhy)
                {
                    s_nextChestWhy = Time.unscaledTime + 1f;
                    float chest = ItemsMeshNear(p.GetComponent<VisEquipment>() != null ? s_chestItems(p.GetComponent<VisEquipment>()) : null, cam);
                    if (chest < MeshNear) FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] chest armour mesh {chest:0.00} m in front of the camera (not hidden: it carries the arms), anim '{AnimName(p)}'{(p.InAttack() ? ", attacking" : "")}; shoulder item {(mesh < float.MaxValue ? mesh.ToString("0.00") + " m" : "none in view")}");
                }
                if (mesh < MeshNear)
                {
                    block = true;
                    if (Time.unscaledTime >= s_nextMeshWhy)
                    {
                        s_nextMeshWhy = Time.unscaledTime + 1f;
                        FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] shoulder item caught by its MESH (the bone test missed: shoulder bone {(near < float.MaxValue ? near.ToString("0.00") + " m" : "out of view")}): nearest vertex {mesh:0.00} m in front of the camera, anim '{AnimName(p)}'{(p.InAttack() ? ", attacking" : "")}");
                    }
                }
            }
            if (block) s_shoulderUntil = Time.unscaledTime + ShoulderHold;
            bool hide = active && Time.unscaledTime < s_shoulderUntil;
            if (hide && s_shoulderHidden.Count == 0)
            {
                var vis = p != null ? p.GetComponent<VisEquipment>() : null;
                var items = vis != null ? s_shoulderItems(vis) : null;
                if (items != null) foreach (var go in items) if (go != null)
                    foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                        if (!(r is ParticleSystemRenderer) && r.shadowCastingMode != ShadowCastingMode.ShadowsOnly) { s_shoulderHidden.Add((r, r.shadowCastingMode)); r.shadowCastingMode = ShadowCastingMode.ShadowsOnly; }
                if (s_shoulderHidden.Count > 0) FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] shoulder item hidden: {(near < ShoulderNear ? $"shoulder bone {near:0.00} m in front of the camera (under {ShoulderNear:0.00} m)" : "its mesh crossed the camera (bone test missed)")},{s_shoulderHidden.Count} renderer(s) shadows-only{(p != null && p.InAttack() ? ", attacking" : "")}");
            }
            else if (!hide && s_shoulderHidden.Count > 0)
            {
                foreach (var (r, mode) in s_shoulderHidden) if (r != null) r.shadowCastingMode = mode;
                FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] shoulder item shown again ({(active ? $"shoulder {(near < float.MaxValue ? near.ToString("0.00") + " m" : "out of view")}" : "left first person")})");
                s_shoulderHidden.Clear();
            }
        }
        private static void HideBack(Player p, bool hide)
        {
            if (!hide)
            {

                if (s_backHidden.Count == 0) return;
                foreach (var (r, mode) in s_backHidden) if (r != null) r.shadowCastingMode = mode;
                FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] back items restored ({s_backHidden.Count} renderer(s))");
                s_backHidden.Clear();
                return;
            }
            if (Time.unscaledTime < s_nextBackScan) return;   // re-checked every second (a bag or a sheathed weapon can change)
            s_nextBackScan = Time.unscaledTime + 1f;
            var vis = p.GetComponent<VisEquipment>();
            if (vis == null) return;
            var roots = new List<GameObject> { vis.m_leftBackItemInstance, vis.m_rightBackItemInstance };
            foreach (var t in p.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name;
                if (n.IndexOf("Backpack", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Bag_va", StringComparison.OrdinalIgnoreCase) >= 0 || n.EndsWith("Bag", StringComparison.OrdinalIgnoreCase)) roots.Add(t.gameObject);
            }
            int added = 0;
            foreach (var go in roots)
            {
                if (go == null) continue;
                if (vis.m_rightItemInstance != null && go.transform.IsChildOf(vis.m_rightItemInstance.transform)) continue;   // held items stay
                if (vis.m_leftItemInstance != null && go.transform.IsChildOf(vis.m_leftItemInstance.transform)) continue;
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                {
                    if (r is ParticleSystemRenderer || r.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;
                    s_backHidden.Add((r, r.shadowCastingMode)); r.shadowCastingMode = ShadowCastingMode.ShadowsOnly; added++;
                }
            }
            if (added > 0) FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] back items hidden on this screen: {added} renderer(s) (back slots + backpack) shadows-only");
        }

        // ── ghost bow ───────────────────────────────────────────────────────────────────────────────────────────────────────────
        private static readonly Dictionary<Material, Material> s_ghostOf = new Dictionary<Material, Material>();
        private static readonly List<(Renderer r, Material[] mats)> s_ghosted = new List<(Renderer, Material[])>();
        private static GameObject s_ghostBow; private static float s_ghostAlphaMade = -1f;

        private static void GhostBow(Player p, bool off = false)
        {
            var w = p.GetCurrentWeapon();
            var vis = p.GetComponent<VisEquipment>();
            GameObject held = vis != null ? LeftItem(vis) : null;
            bool bow = w != null && w.m_shared.m_skillType == Skills.SkillType.Bows && w.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow;   // bows only (Fire): crossbows stay solid
            bool aim = !off && s_in && bow && held != null && p.GetAttackDrawPercentage() > 0f;
            if (aim && s_ghostBow == held) return;
            if (s_ghostBow != null || s_ghosted.Count > 0)
            {
                foreach (var (r, mats) in s_ghosted) if (r != null) r.sharedMaterials = mats;
                string why = off ? (s_in ? "left first person" : "first person stood down") : s_ghostBow != held ? "the bow was unequipped" : "aim stopped";
                GhostOffCount++;
                FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] ghost bow off ({why}): {s_ghosted.Count} renderer(s) back to their own materials");
                s_ghosted.Clear(); s_ghostBow = null;
            }
            if (!aim) return;
            float alpha = s_ghostAlpha != null ? s_ghostAlpha.Value : 0.2f;
            if (Mathf.Abs(alpha - s_ghostAlphaMade) > 0.001f) { foreach (var m in s_ghostOf.Values) if (m != null) UnityEngine.Object.Destroy(m); s_ghostOf.Clear(); s_ghostAlphaMade = alpha; }
            int made = 0;
            foreach (var r in held.GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer) continue;
                var own = r.sharedMaterials;
                var ghost = new Material[own.Length];
                for (int i = 0; i < own.Length; i++)
                {
                    if (own[i] == null) continue;
                    if (!s_ghostOf.TryGetValue(own[i], out var g) || g == null) { g = MakeGhost(own[i], alpha); s_ghostOf[own[i]] = g; made++; }
                    ghost[i] = g;
                }
                s_ghosted.Add((r, own)); r.sharedMaterials = ghost;
            }
            s_ghostBow = held;
            GhostOnCount++;
            FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] ghost bow on (opacity {alpha:0.00}): {s_ghosted.Count} renderer(s), {made} ghost material(s) made, {s_ghostOf.Count} cached");
        }

        // ── see-through shield while blocking (0.2.1009) ───────────────────────────────────────────────────────────────────────────
        // Fire 10-30, shot 366: blocking with a tower shield in first person fills the whole view. While block is held the shield (the left
        // item when it is a Shield) gets the same ghost materials as the bow, at its own opacity; back to its own materials on release.
        private static readonly Dictionary<Material, Material> s_shieldGhostOf = new Dictionary<Material, Material>();
        private static readonly List<(Renderer r, Material[] mats)> s_shieldGhosted = new List<(Renderer, Material[])>();
        private static GameObject s_ghostShield; private static float s_shieldAlphaMade = -1f;
        private static void GhostShield(Player p, bool off = false)
        {
            if (p == null) return;
            var vis = p.GetComponent<VisEquipment>();
            GameObject held = vis != null ? LeftItem(vis) : null;
            var item = p.GetLeftItem();
            float alpha = s_shieldAlpha != null ? s_shieldAlpha.Value : 0.2f;
            bool want = !off && s_in && alpha < 0.999f && held != null && item != null && item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield && p.IsBlocking();
            if (want && s_ghostShield == held) return;
            if (s_ghostShield != null || s_shieldGhosted.Count > 0)
            {
                foreach (var (r, mats) in s_shieldGhosted) if (r != null) r.sharedMaterials = mats;
                FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] shield solid again ({(off ? "left first person" : s_ghostShield != held ? "the shield was unequipped" : "block released")}): {s_shieldGhosted.Count} renderer(s)");
                s_shieldGhosted.Clear(); s_ghostShield = null;
            }
            if (!want) return;
            if (Mathf.Abs(alpha - s_shieldAlphaMade) > 0.001f) { foreach (var m in s_shieldGhostOf.Values) if (m != null) UnityEngine.Object.Destroy(m); s_shieldGhostOf.Clear(); s_shieldAlphaMade = alpha; }
            foreach (var r in held.GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer) continue;
                var own = r.sharedMaterials; var ghost = new Material[own.Length];
                for (int i = 0; i < own.Length; i++)
                {
                    if (own[i] == null) continue;
                    if (!s_shieldGhostOf.TryGetValue(own[i], out var g) || g == null) { g = MakeGhost(own[i], alpha); s_shieldGhostOf[own[i]] = g; }
                    ghost[i] = g;
                }
                s_shieldGhosted.Add((r, own)); r.sharedMaterials = ghost;
            }
            s_ghostShield = held;
            FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] blocking: shield '{item.m_shared.m_name}' see-through (opacity {alpha:0.00}), {s_shieldGhosted.Count} renderer(s), shader {GhostShaderPath}");
        }

        // ── crosshair red over an enemy (0.2.1009) ─────────────────────────────────────────────────────────────────────────────
        // Fire 10-30, shot 368: vanilla Hud.UpdateCrosshair colours the crosshair yellow whenever the hover text is not empty (companions,
        // items, doors) and white otherwise. After it, a hovered creature that is hostile to us (BaseAI.IsEnemy, not tamed, not a player)
        // turns it red. Any view, not only first person: it is the same crosshair.
        private static readonly Color EnemyRed = new Color(1f, 0.25f, 0.2f, 1f);
        private static Character s_lastEnemy;
        [HarmonyPatch(typeof(Hud), "UpdateCrosshair")]
        private static class Hud_UpdateCrosshair_EnemyRing
        {
            // Fire 10-30: "the crosshair ring is vanillas bow crosshair" - Hud.m_crosshairBow, coloured Lerp(clear, yellow, draw) by vanilla.
            // While a bow is drawn the hover creature only reaches interact range, so the aim ray itself (camera forward, 100 m, stops
            // at terrain / pieces) picks the creature, and the ring takes Lerp(clear, red, draw) on an enemy.
            private static int s_aimMask = -1;
            private static Character AimedCreature(Player player)
            {
                var cam = GameCamera.instance != null ? GameCamera.instance.transform : null;
                if (cam == null) return null;
                if (s_aimMask == -1) s_aimMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "vehicle", "character", "character_net", "character_ghost", "hitbox", "character_noenv");
                var hits = Physics.RaycastAll(cam.position, cam.forward, 100f, s_aimMask, QueryTriggerInteraction.Ignore);
                System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                foreach (var h in hits)
                {
                    var ch = h.collider.GetComponentInParent<Character>();
                    if (ch == player) continue;   // our own body in first person
                    return ch;                    // the first thing that is not us: a creature, or null for a wall / the ground
                }
                return null;
            }
            private static bool Hostile(Player player, Character c) => c != null && !c.IsPlayer() && !c.IsTamed() && !c.IsDead() && BaseAI.IsEnemy(player, c);
            private static void Postfix(Hud __instance, Player player, float bowDrawPercentage)
            {
                // 0.2.1036 (plan step 1): the setting lives in FiresRPGClasses (CoreSettings); no provider = off
                if (!s_enemyRing.Value || player == null || __instance.m_crosshair == null) return;
                bool drawing = bowDrawPercentage > 0f && __instance.m_crosshairBow != null && __instance.m_crosshairBow.gameObject.activeSelf;
                var c = drawing ? AimedCreature(player) : player.GetHoverCreature();
                bool enemy = Hostile(player, c);
                if (enemy) __instance.m_crosshair.color = EnemyRed;
                if (enemy && drawing) __instance.m_crosshairBow.color = Color.Lerp(new Color(1f, 0.25f, 0.2f, 0f), EnemyRed, bowDrawPercentage);
                if (enemy && c != s_lastEnemy) FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] crosshair{(drawing ? " + bow ring" : "")} red: over '{c.m_name}' (hostile to you, faction {c.GetFaction()}{(drawing ? $", aim ray, draw {bowDrawPercentage:0.00}" : ", hover")})");
                s_lastEnemy = enemy ? c : null;
            }
        }

        private static Shader s_fade; private static bool s_fadeLooked;

        // one ghost per source material, made once: the bow's own shader if it blends (Standard's fade), else a legacy transparent shader
        private static Material MakeGhost(Material src, float alpha)
        {
            var g = new Material(src) { name = src.name + " (fires ghost)" };
            if (src.shader != null && src.shader.name == "Standard")
            {
                g.SetFloat("_Mode", 2f); g.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); g.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha); g.SetInt("_ZWrite", 0);
                g.DisableKeyword("_ALPHATEST_ON"); g.EnableKeyword("_ALPHABLEND_ON"); g.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                GhostShaderPath = "Standard fade";
            }
            else
            {
                if (!s_fadeLooked) { s_fadeLooked = true; s_fade = Shader.Find("Legacy Shaders/Transparent/Diffuse") ?? Shader.Find("Sprites/Default"); FiresCore.Logging.FiresLogger.LogDebug($"[FirstPerson] ghost bow shader: '{src.shader?.name}' has no fade mode, using '{s_fade?.name ?? "none found"}'"); }
                GhostShaderPath = s_fade != null ? "fallback " + s_fade.name : "none (still opaque)";
                if (s_fade != null)
                {
                    var tex = src.HasProperty("_MainTex") ? src.GetTexture("_MainTex") : null;
                    g.shader = s_fade;
                    if (tex != null && g.HasProperty("_MainTex")) g.SetTexture("_MainTex", tex);
                }
            }
            g.renderQueue = (int)RenderQueue.Transparent;
            // 0.2.1009 (Fire 10-30: "make the bow a bit more transparent"; his shot 367: the ghost bow glows, the fallback Sprites/Default
            // is unlit = full bright): a fallback ghost is also dimmed so it reads as see-through, not as a light
            if (g.HasProperty("_Color")) { var c = g.GetColor("_Color"); c.a = alpha; if (g.shader != src.shader) { c.r *= 0.55f; c.g *= 0.55f; c.b *= 0.55f; } g.SetColor("_Color", c); }
            return g;
        }
    }
}
