using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.World.Halo
{
    // Core port of FDT 1.1.1021 HaloCreatures ([visual], 10-24; Tools\PLAN_halo_motor_to_core_20261024.md). The creature rows of the ring
    // frame. All of it only acts inside a GravityUp field; off the ring vanilla runs untouched.
    //   #1 eye: m_eye is written from the LOCAL look (AI SetLookDir is localized; Player's own yaw/pitch is local), so GetLookDir /
    //      GetAimDir (= m_eye.forward) aimed every attack and projectile in the wrong frame. The eye becomes Frame * local; the player's
    //      m_lookDir stays local (vanilla's motor reads it inside the frame), so it is put back after SetMouseLook.
    //   #4 pushback: the hit direction is world; it is turned into the frame before vanilla flattens y and stores m_pushForce (local).
    //   #7 Jump: also runs inside the frame (called from BaseAI.MoveTowards outside it), see RingPatcher.Apply.
    //   stagger: RPC_Stagger's LookRotation(-dir) about world up = the creature lies on the wall; re-stood on the ring's up after.
    //   ground tilt: vanilla tilts m_visual from world up (quadrupeds up to 50 deg); skipped in the field (upright on the ring).
    //   GravityUp.BodyOverride(go) = a body driven by hand (climbing, seated, carried): no frame, no -up gravity.
    //   Loose bodies (drops, logs, ragdolls) are GravityBody's, never the motor's.
    internal static class RingCreatures
    {
        private static readonly AccessTools.FieldRef<Character, Vector3> LookDirRef = AccessTools.FieldRefAccess<Character, Vector3>("m_lookDir");

        internal static bool BodyOverride(GameObject go) => go != null && GravityUp.BodyOverride(go);

        private static bool Field(Character c, out Quaternion q)
        {
            q = Quaternion.identity;
            if (!RingMotor.Enabled || c == null || !GravityUp.Active) return false;
            if (!GravityUp.InField(c.transform.position, out Vector3 up)) return false;
            q = RingMotor.Active && RingMotor.CurrentFrame != Quaternion.identity ? RingMotor.CurrentFrame : Quaternion.FromToRotation(Vector3.up, up);
            return true;
        }

        // #1 (Character.UpdateEyeRotation and Player's override): postfix
        internal static void EyePostfix(Character __instance)
        {
            if (__instance.m_eye == null || !Field(__instance, out Quaternion q)) return;
            __instance.m_eye.rotation = q * __instance.m_eye.rotation;
        }

        // Player.SetMouseLook ends with m_lookDir = m_eye.forward (now world): back to local for the motor.
        internal static void MouseLookPostfix(Player __instance)
        {
            if (!Field(__instance, out Quaternion q)) return;
            LookDirRef(__instance) = Quaternion.Inverse(q) * LookDirRef(__instance);
        }

        // #4 Character.ApplyPushback(Vector3 dir, float pushForce): prefix
        internal static void PushbackPrefix(Character __instance, ref Vector3 dir)
        {
            if (dir == Vector3.zero || !Field(__instance, out Quaternion q)) return;
            dir = Quaternion.Inverse(q) * dir;
        }

        // stagger: Character.RPC_Stagger postfix
        internal static void StaggerPostfix(Character __instance)
        {
            if (!GravityUp.Active || !GravityUp.InField(__instance.transform.position, out Vector3 up)) return;
            Vector3 f = Vector3.ProjectOnPlane(__instance.transform.forward, up);
            if (f.sqrMagnitude < 1e-4f) f = Vector3.ProjectOnPlane(__instance.transform.up, up);
            if (f.sqrMagnitude < 1e-4f) return;
            __instance.transform.rotation = Quaternion.LookRotation(f.normalized, up);
        }

        // ground tilt: Character.UpdateGroundTilt prefix (skip in the field)
        internal static bool GroundTiltPrefix(Character __instance) => !(RingMotor.Enabled && GravityUp.Active && GravityUp.InField(__instance.transform.position, out _));

        // ── the ring camera's far clip (lead 10-24: the camera owns it on the ring; [worldgen] squeezes the far ring to 0.92 x it) ──
        internal const float RingFarClip = 10000f;
        private static Camera s_cam;
        private static float s_savedFar = -1f;

        internal static void FarClip(GameCamera gc, bool onRing)
        {
            if (s_cam == null && gc != null) s_cam = gc.GetComponent<Camera>() ?? Camera.main;
            if (s_cam == null) return;
            if (onRing)
            {
                if (s_savedFar < 0f) s_savedFar = s_cam.farClipPlane;
                if (s_cam.farClipPlane < RingFarClip)
                {
                    FiresCore.Logging.FiresLogger.LogDebug($"[Halo] ring camera far clip {s_cam.farClipPlane:0} -> {RingFarClip:0} m (restored to {s_savedFar:0} m off the ring)");
                    s_cam.farClipPlane = RingFarClip;
                }
            }
            else if (s_savedFar >= 0f)
            {
                if (Mathf.Approximately(s_cam.farClipPlane, RingFarClip)) s_cam.farClipPlane = s_savedFar;   // only undo our own value
                FiresCore.Logging.FiresLogger.LogDebug($"[Halo] off the ring: camera far clip back to {s_cam.farClipPlane:0} m");
                s_savedFar = -1f;
            }
        }
    }
}
