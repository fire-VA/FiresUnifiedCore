using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.World.Halo
{
    // HALO RING SPIKE: run vanilla's character code UNCHANGED in a rotated local frame. While a character inside a GravityUp field
    // updates (CustomFixedUpdate, OnCollisionStay), every read/write it makes of its OWN body velocity, forces, rotation, facing and
    // contact normals goes through the helpers below, which rotate by Q = FromToRotation(world up, GravityUp(pos)). Vanilla then
    // "thinks" it stands on flat ground with y up, and the world sees the result tilted. Everything else (other objects, positions,
    // raycasts, ground-height lookups) is untouched, which is exactly what the spike measures. World gravity is switched off for that
    // body and -up * |g| applied instead. 'halo_frame off' = vanilla, for A/B.
    internal static class RingMotor
    {
        internal static bool Enabled = true;
        private static Character s_char;
        private static Transform s_tr;
        private static Rigidbody s_body;
        private static Quaternion s_q = Quaternion.identity, s_qi = Quaternion.identity;
        private static int s_depth;
        private static readonly FieldInfo BodyField = AccessTools.Field(typeof(Character), "m_body");

        internal static bool Active => s_depth > 0;
        // 1.1.1015: when -up gravity was last applied to the local player's body (the probe reports this instead of reading
        // Rigidbody.useGravity, which vanilla ZSyncTransform sets back to its cached world value between physics steps, ZSyncTransform.cs:188)
        internal static float LastLocalGravityTime = -1f;
        internal static Quaternion CurrentFrame => s_q;

        internal static bool Push(Character c)
        {
            if (s_depth > 0) { if (c == s_char) { s_depth++; return true; } return false; }   // never nest two characters
            if (!Enabled || c == null || !GravityUp.Active) return false;
            if (!GravityUp.InField(c.transform.position, out Vector3 up)) return false;
            if (RingCreatures.BodyOverride(c.gameObject)) return false;   // 1.1.1020: Core 0.2.817 says a hand-driven body (climbing, seated, carried): no frame, no -up gravity
            s_char = c; s_tr = c.transform; s_body = BodyField.GetValue(c) as Rigidbody;
            s_q = Quaternion.FromToRotation(Vector3.up, up); s_qi = Quaternion.Inverse(s_q);
            s_depth = 1;
            return true;
        }

        // applyGravity: only the physics frame (CustomFixedUpdate) applies gravity. 1.1.1015 (Fire's hand-over probe): OnCollisionStay
        // also closed the frame with gravity, and Unity calls it once per touching collider per step, so standing on the segment added
        // extra gravity per contact.
        internal static void Pop(Character c, bool pushed, bool applyGravity = true)
        {
            if (!pushed || s_depth == 0 || c != s_char) return;
            if (--s_depth > 0) return;
            // outermost physics frame: gravity along -up for this body (vanilla turned world gravity on for walking)
            if (applyGravity && s_body != null && !s_body.isKinematic)
            {
                AlignToUp();
                bool hold = c == Player.m_localPlayer && Safety();
                s_body.useGravity = false;
                if (!hold) s_body.AddForce(s_q * Physics.gravity, ForceMode.Acceleration);
                if (c == Player.m_localPlayer) LastLocalGravityTime = Time.time;
            }
            s_char = null; s_tr = null; s_body = null;
        }

        // 1.1.1016 (Fire's HaloFlat spawn: "body up off by 90 deg"): vanilla writes a character's rotation only while it walks, turns or
        // touches ground (Character.UpdateRotation), so a body falling with no input was never tilted onto the ring. Every physics frame
        // on the ring now sets the body's up to the ring's up, keeping its yaw in the local frame.
        private static void AlignToUp()
        {
            if (s_tr == null) return;
            Vector3 up = s_q * Vector3.up;
            if (Vector3.Angle(s_tr.up, up) < 0.5f) return;
            Vector3 f = s_qi * s_tr.forward;
            f.y = 0f;
            Quaternion local = f.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(f.normalized, Vector3.up) : Quaternion.identity;
            Quaternion world = s_q * local;
            s_tr.rotation = world;
            if (s_body != null) s_body.rotation = world;
        }

        // 1.1.1016 (Fire's HaloFlat spawn fell outward through a shell whose colliders did not exist yet): the local player is never
        // left falling into nothing on a ring. Nothing solid within 60 m along -up = held in place (no gravity, velocity zeroed) until
        // ground appears; a solid surface just ABOVE (toward the axis) within 12 m = he is outside the shell, so he is put back 6 m past it.
        // 1.1.1017: the probe reaches the floor from anywhere inside a ring (R 4,775 m), and debug flight is never held (Fire flies to the axis)
        private const float HoldProbe = 6000f, OutsideProbe = 12f, BackInside = 6f;
        private static int s_solidMask;
        private static bool s_holding;
        internal static int Holds, Rescues;

        private static bool Safety()
        {
            if (s_tr == null || s_body == null || s_char == null || s_char.IsOnGround() || s_char.IsDebugFlying()) { s_holding = false; return false; }
            if (s_solidMask == 0) s_solidMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "vehicle");
            Vector3 up = s_q * Vector3.up;
            Vector3 pos = s_tr.position;
            if (Physics.Raycast(pos + up * 0.5f, -up, HoldProbe, s_solidMask, QueryTriggerInteraction.Ignore)) { s_holding = false; return false; }
            if (Physics.Raycast(pos, up, out RaycastHit above, OutsideProbe, s_solidMask, QueryTriggerInteraction.Ignore))
            {
                Vector3 back = above.point + up * BackInside;
                s_tr.position = back; s_body.position = back; s_body.linearVelocity = Vector3.zero;
                Rescues++;
                FiresCore.Logging.FiresLogger.LogInfo($"[Halo] you were OUTSIDE the ring's floor (a surface {above.distance:0.0} m toward the axis): put back inside at {back:F1}");
                s_holding = false;
                return true;
            }
            s_body.linearVelocity = Vector3.zero;
            if (!s_holding) { s_holding = true; Holds++; FiresCore.Logging.FiresLogger.LogInfo($"[Halo] nothing solid within {HoldProbe:0} m below you on the ring at {pos:F1}: holding you in place until the ground exists"); }
            return true;
        }

        private static bool Mine(Rigidbody b) => s_depth > 0 && b == s_body;
        private static bool Mine(Transform t) => s_depth > 0 && t == s_tr;

        // ── replacements for the calls vanilla makes ──
        internal static Vector3 GetVel(Rigidbody b) => Mine(b) ? s_qi * b.linearVelocity : b.linearVelocity;
        internal static void SetVel(Rigidbody b, Vector3 v) => b.linearVelocity = Mine(b) ? s_q * v : v;
        internal static void AddForceMode(Rigidbody b, Vector3 f, ForceMode m) => b.AddForce(Mine(b) ? s_q * f : f, m);
        internal static void AddForce(Rigidbody b, Vector3 f) => b.AddForce(Mine(b) ? s_q * f : f);
        internal static Quaternion GetBodyRot(Rigidbody b) => Mine(b) ? s_qi * b.rotation : b.rotation;
        internal static void SetBodyRot(Rigidbody b, Quaternion r) => b.rotation = Mine(b) ? s_q * r : r;
        internal static Quaternion GetRot(Transform t) => Mine(t) ? s_qi * t.rotation : t.rotation;
        internal static void SetRot(Transform t, Quaternion r) => t.rotation = Mine(t) ? s_q * r : r;
        internal static Vector3 GetForward(Transform t) => Mine(t) ? s_qi * t.forward : t.forward;
        internal static Vector3 GetRight(Transform t) => Mine(t) ? s_qi * t.right : t.right;
        internal static Vector3 GetUp(Transform t) => Mine(t) ? s_qi * t.up : t.up;
        internal static Vector3 ContactNormal(ref ContactPoint c) => s_depth > 0 ? s_qi * c.normal : c.normal;
        internal static Vector3 ContactPointPos(ref ContactPoint c)
        {
            if (s_depth == 0 || s_tr == null) return c.point;
            Vector3 p = s_tr.position;
            return p + s_qi * (c.point - p);
        }

        // ── the transpiler: one map of vanilla member -> helper, applied to every Character / Humanoid / Player method using them ──
        private static Dictionary<MethodBase, MethodInfo> s_map;

        internal static Dictionary<MethodBase, MethodInfo> Map
        {
            get
            {
                if (s_map != null) return s_map;
                var h = typeof(RingMotor);
                s_map = new Dictionary<MethodBase, MethodInfo>
                {
                    [AccessTools.PropertyGetter(typeof(Rigidbody), nameof(Rigidbody.linearVelocity))] = AccessTools.Method(h, nameof(GetVel)),
                    [AccessTools.PropertySetter(typeof(Rigidbody), nameof(Rigidbody.linearVelocity))] = AccessTools.Method(h, nameof(SetVel)),
                    [AccessTools.Method(typeof(Rigidbody), nameof(Rigidbody.AddForce), new[] { typeof(Vector3), typeof(ForceMode) })] = AccessTools.Method(h, nameof(AddForceMode)),
                    [AccessTools.Method(typeof(Rigidbody), nameof(Rigidbody.AddForce), new[] { typeof(Vector3) })] = AccessTools.Method(h, nameof(AddForce)),
                    [AccessTools.PropertyGetter(typeof(Rigidbody), nameof(Rigidbody.rotation))] = AccessTools.Method(h, nameof(GetBodyRot)),
                    [AccessTools.PropertySetter(typeof(Rigidbody), nameof(Rigidbody.rotation))] = AccessTools.Method(h, nameof(SetBodyRot)),
                    [AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.rotation))] = AccessTools.Method(h, nameof(GetRot)),
                    [AccessTools.PropertySetter(typeof(Transform), nameof(Transform.rotation))] = AccessTools.Method(h, nameof(SetRot)),
                    [AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.forward))] = AccessTools.Method(h, nameof(GetForward)),
                    [AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.right))] = AccessTools.Method(h, nameof(GetRight)),
                    [AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.up))] = AccessTools.Method(h, nameof(GetUp)),
                    [AccessTools.PropertyGetter(typeof(ContactPoint), nameof(ContactPoint.normal))] = AccessTools.Method(h, nameof(ContactNormal)),
                    [AccessTools.PropertyGetter(typeof(ContactPoint), nameof(ContactPoint.point))] = AccessTools.Method(h, nameof(ContactPointPos)),
                };
                foreach (var kv in s_map.Where(kv => kv.Key == null || kv.Value == null).ToList())
                    FiresCore.Logging.FiresLogger.LogWarning($"[Halo] frame map: missing {(kv.Key == null ? "vanilla member" : "helper")} {kv.Key?.Name ?? "?"} - that access stays world-frame");
                foreach (var k in s_map.Keys.Where(k => k == null || s_map[k] == null).ToList()) s_map.Remove(k);
                return s_map;
            }
        }

        internal static int s_patchedMethods, s_replacedCalls;
    }

    // Applied ONLY on the first 'halo_spawn' (own Harmony id), so a session that never builds a segment runs with no halo patch at all.
    internal static class RingPatcher
    {
        private const string Id = "com.Fire.FiresUnifiedCore.RingMotor";
        internal static bool Applied;
        internal static string Problem;

        internal static void Apply()
        {
            if (Applied) return;
            Applied = true;
            var h = new Harmony(Id);
            int ok = 0, failed = 0;
            void Try(MethodBase target, HarmonyMethod prefix = null, HarmonyMethod postfix = null, HarmonyMethod transpiler = null, HarmonyMethod finalizer = null)
            {
                if (target == null) { failed++; return; }
                try { h.Patch(target, prefix: prefix, postfix: postfix, transpiler: transpiler, finalizer: finalizer, ilmanipulator: null); ok++; }
                catch (Exception ex) { failed++; FiresCore.Logging.FiresLogger.LogWarning($"[Halo] patch {target.DeclaringType?.Name}.{target.Name} failed: {ex.GetBaseException().Message}"); }
            }
            var tr = new HarmonyMethod(AccessTools.Method(typeof(RingMotorTranspiler), "Transpiler"));
            foreach (var m in RingMotorTranspiler.Targets()) Try(m, transpiler: tr);
            var push = new HarmonyMethod(AccessTools.Method(typeof(RingMotorPush), "Prefix")) { priority = Priority.First };
            var pop = new HarmonyMethod(AccessTools.Method(typeof(RingMotorPush), "Finalizer")) { priority = Priority.Last };
            foreach (var m in RingMotorPush.Targets()) Try(m, prefix: push, finalizer: pop);
            Try(RingMotorPush.ContactTarget, prefix: push, finalizer: new HarmonyMethod(AccessTools.Method(typeof(RingMotorPush), "ContactFinalizer")) { priority = Priority.Last });
            Try(AccessTools.Method(typeof(Character), nameof(Character.SetMoveDir)), prefix: new HarmonyMethod(AccessTools.Method(typeof(RingAiDirections), "MoveDir")));
            Try(AccessTools.Method(typeof(Character), nameof(Character.SetLookDir)), prefix: new HarmonyMethod(AccessTools.Method(typeof(RingAiDirections), "LookDir")));
            Try(AccessTools.Method(typeof(GameCamera), "GetCameraPosition"), prefix: new HarmonyMethod(AccessTools.Method(typeof(RingCamera), "Prefix")));   // 1.1.1017: a local-frame orbit replaces vanilla's camera inside a field
            Try(AccessTools.Method(typeof(Player), "UpdatePlacementGhost"), postfix: new HarmonyMethod(AccessTools.Method(typeof(RingPlacement), "Postfix")));
            // 1.1.1020 (HALO_UP_CREATURES #1 #4 #7, stagger, ground tilt)
            var eye = new HarmonyMethod(AccessTools.Method(typeof(RingCreatures), nameof(RingCreatures.EyePostfix)));
            Try(AccessTools.Method(typeof(Character), "UpdateEyeRotation"), postfix: eye);
            Try(AccessTools.Method(typeof(Player), "UpdateEyeRotation"), postfix: eye);
            Try(AccessTools.Method(typeof(Player), "SetMouseLook"), postfix: new HarmonyMethod(AccessTools.Method(typeof(RingCreatures), nameof(RingCreatures.MouseLookPostfix))));
            Try(AccessTools.Method(typeof(Character), nameof(Character.ApplyPushback), new[] { typeof(Vector3), typeof(float) }), prefix: new HarmonyMethod(AccessTools.Method(typeof(RingCreatures), nameof(RingCreatures.PushbackPrefix))));
            Try(AccessTools.Method(typeof(Character), nameof(Character.Jump)), prefix: push, finalizer: new HarmonyMethod(AccessTools.Method(typeof(RingMotorPush), "ContactFinalizer")) { priority = Priority.Last });
            Try(AccessTools.Method(typeof(Character), "RPC_Stagger"), postfix: new HarmonyMethod(AccessTools.Method(typeof(RingCreatures), nameof(RingCreatures.StaggerPostfix))));
            Try(AccessTools.Method(typeof(Character), "UpdateGroundTilt"), prefix: new HarmonyMethod(AccessTools.Method(typeof(RingCreatures), nameof(RingCreatures.GroundTiltPrefix))));
            Problem = failed > 0 ? $"{failed} patch(es) failed (see warnings)" : null;
            FiresCore.Logging.FiresLogger.LogInfo($"[Halo] spike patches applied: {ok} ok, {failed} failed; {RingMotor.s_patchedMethods} methods / {RingMotor.s_replacedCalls} calls retargeted");
        }
    }

    // Every method of Character / Humanoid / Player whose IL touches a mapped member gets those calls swapped for the helpers.
    internal static class RingMotorTranspiler
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            var map = RingMotor.Map;
            foreach (var type in new[] { typeof(Character), typeof(Humanoid), typeof(Player) })
                foreach (var m in AccessTools.GetDeclaredMethods(type))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                    bool uses = false;
                    try { uses = PatchProcessor.GetOriginalInstructions(m).Any(ci => ci.operand is MethodBase mb && map.ContainsKey(mb)); }
                    catch { }
                    if (uses) yield return m;
                }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var map = RingMotor.Map;
            bool any = false;
            foreach (var ci in instructions)
            {
                if ((ci.opcode == OpCodes.Call || ci.opcode == OpCodes.Callvirt) && ci.operand is MethodBase mb && map.TryGetValue(mb, out var helper))
                {
                    ci.opcode = OpCodes.Call;
                    ci.operand = helper;
                    RingMotor.s_replacedCalls++;
                    any = true;
                }
                yield return ci;
            }
            if (any) RingMotor.s_patchedMethods++;
        }
    }

    // The frame is open while a character runs its physics frame and while Unity reports its contacts.
    internal static class RingMotorPush
    {
        internal static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(Character), nameof(Character.CustomFixedUpdate));
            var h = AccessTools.DeclaredMethod(typeof(Humanoid), nameof(Humanoid.CustomFixedUpdate));
            if (h != null) yield return h;
        }

        internal static MethodBase ContactTarget => AccessTools.Method(typeof(Character), "OnCollisionStay");

        private static void Prefix(Character __instance, out bool __state) => __state = RingMotor.Push(__instance);

        private static void Finalizer(Character __instance, bool __state) => RingMotor.Pop(__instance, __state);

        // contacts: the frame only (normals / points local), never gravity
        private static void ContactFinalizer(Character __instance, bool __state) => RingMotor.Pop(__instance, __state, applyGravity: false);
    }

    // AI hands the body WORLD directions; inside a field they become local (projected onto the local ground plane).
    internal static class RingAiDirections
    {
        private static void MoveDir(Character __instance, ref Vector3 dir) => Localize(__instance, ref dir, true);

        private static void LookDir(Character __instance, ref Vector3 dir) => Localize(__instance, ref dir, false);

        private static void Localize(Character c, ref Vector3 dir, bool flat)
        {
            if (!RingMotor.Enabled || c == null || c.IsPlayer() || dir == Vector3.zero || !GravityUp.Active) return;
            if (!GravityUp.InField(c.transform.position, out Vector3 up)) return;
            float mag = dir.magnitude;
            Vector3 local = Quaternion.Inverse(Quaternion.FromToRotation(Vector3.up, up)) * dir;
            if (flat && !c.IsFlying()) { local.y = 0f;   // 1.1.1020 (#2): flyers keep their climb/dive
                 dir = local.sqrMagnitude > 1e-6f ? local.normalized * mag : Vector3.zero; }
            else dir = local;
        }
    }

    // The camera, 1.1.1017 (Fire on HaloFlat: "the camera is really bad, it should lock into a position relative to the character and then
    // it's always oriented correctly"). Why 1.1.1016 was wrong (GameCamera.cs:250-283): vanilla builds the camera from m_eye.position (on
    // the TILTED body) and m_eye.transform.forward (the eye's rotation is written from the LOCAL look yaw/pitch, so it is untilted), adds a
    // world-Y base offset, pulls in with world raycasts and a world water clamp; the old postfix then rotated that mixed result about the
    // feet, tilting the eye offset twice (camera low, character huge and off to one side). Now, inside a field, vanilla's
    // GetCameraPosition is replaced by one orbit computed entirely in the character's LOCAL frame and then turned into the world:
    //   look  = the player's local look (m_lookYaw * pitch m_lookPitch), never re-derived from world vectors
    //   pivot = the feet + the eye's height along up + vanilla's shoulder offset (m_3rdOffset / m_3rdCombatOffset) in the look frame
    //   pos   = pivot + look * back * vanilla's m_distance, pulled in by a sphere cast from the pivot (m_blockCameraMask, like vanilla)
    //   up    = GravityUp smoothed (0.15 s) so walking round the hoop never snaps the horizon
    // ApplyCameraTilt (world-up blend, ships) is skipped there. Off the field vanilla runs untouched. One Debug line a second.
    internal static class RingCamera
    {
        private const float UpSmoothSeconds = 0.15f;
        private static readonly AccessTools.FieldRef<Character, Quaternion> LookYaw = AccessTools.FieldRefAccess<Character, Quaternion>("m_lookYaw");
        private static readonly AccessTools.FieldRef<Player, float> LookPitch = AccessTools.FieldRefAccess<Player, float>("m_lookPitch");
        private static readonly AccessTools.FieldRef<GameCamera, float> Distance = AccessTools.FieldRefAccess<GameCamera, float>("m_distance");
        private static Vector3 s_smoothedUp = Vector3.up;
        private static bool s_hasUp;
        private static float s_nextLog;

        // GameCamera.GetCameraPosition(float dt, out Vector3 pos, out Quaternion rot)
        private static bool Prefix(GameCamera __instance, float dt, ref Vector3 pos, ref Quaternion rot)
        {
            var p = Player.m_localPlayer;
            if (!RingMotor.Enabled || p == null || !GravityUp.Active || p.IsAttached() || p.IsDead() || p.InIntro()) { s_hasUp = false; RingCreatures.FarClip(__instance, false); return true; }
            if (!GravityUp.InField(p.transform.position, out Vector3 up)) { s_hasUp = false; RingCreatures.FarClip(__instance, false); return true; }
            RingCreatures.FarClip(__instance, true);   // 1.1.1020: the ring camera sees the far ring (>= 10 km)

            s_smoothedUp = s_hasUp ? Vector3.Slerp(s_smoothedUp, up, 1f - Mathf.Exp(-Mathf.Max(dt, 0f) / UpSmoothSeconds)).normalized : up;
            s_hasUp = true;
            Quaternion q = Quaternion.FromToRotation(Vector3.up, s_smoothedUp);

            Quaternion look = LookYaw(p) * Quaternion.Euler(LookPitch(p), 0f, 0f);   // local
            Vector3 feet = p.transform.position;
            float eyeHeight = p.m_eye != null ? Vector3.Dot(p.m_eye.position - feet, s_smoothedUp) : 1.6f;   // the same axis as q ([wishbone])
            float dist = Distance(__instance);
            Vector3 shoulder = dist <= 0f ? __instance.m_fpsOffset : (p.UseMeleeCamera() ? __instance.m_3rdCombatOffset : __instance.m_3rdOffset);
            Vector3 pivotLocal = Vector3.up * eyeHeight + look * shoulder;
            Vector3 pivot = feet + q * pivotLocal;
            // a pivot that starts inside geometry (a low ceiling, a rim lip) can't be pulled in by the cast: drop the shoulder offset ([wishbone])
            if (Physics.CheckSphere(pivot, 0.2f, (int)__instance.m_blockCameraMask, QueryTriggerInteraction.Ignore)) pivot = feet + q * (Vector3.up * eyeHeight);
            Vector3 back = q * (look * Vector3.back);
            float reach = dist;
            if (dist > 0f && Physics.SphereCast(pivot, 0.2f, back, out RaycastHit hit, dist, (int)__instance.m_blockCameraMask, QueryTriggerInteraction.Ignore))
                reach = Mathf.Max(0.1f, hit.distance);
            pos = pivot + back * reach;
            rot = q * look;

            if (Time.time >= s_nextLog)
            {
                s_nextLog = Time.time + 1f;
                Vector3 e = look.eulerAngles;
                FiresCore.Logging.FiresLogger.LogDebug($"[Halo] camera: up {up:F2} (smoothed off by {Vector3.Angle(up, s_smoothedUp):0.0} deg), local pitch {Mathf.DeltaAngle(0f, e.x):0} yaw {e.y:0}, "
                    + $"eye height {eyeHeight:0.00}, distance {dist:0.0} -> {reach:0.0}{(reach < dist ? " (pulled in)" : "")}, camera {Vector3.Dot(pos - feet, up):0.00} m above the feet along up");
            }
            return false;
        }
    }

    // Building: the ghost (and so the placed piece) stands on the local up instead of world up. Support/stability is NOT retargeted
    // (WearNTear searches world down): what that does is part of the spike's answer.
    internal static class RingPlacement
    {
        private static readonly FieldInfo Ghost = AccessTools.Field(typeof(Player), "m_placementGhost");

        private static void Postfix(Player __instance)
        {
            if (!RingMotor.Enabled || !GravityUp.Active) return;
            var g = Ghost?.GetValue(__instance) as GameObject;
            if (g == null || !g.activeSelf) return;
            if (!GravityUp.InField(g.transform.position, out Vector3 up)) return;
            g.transform.rotation = Quaternion.FromToRotation(Vector3.up, up) * g.transform.rotation;
        }
    }

}
