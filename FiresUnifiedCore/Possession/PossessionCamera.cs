using FiresCore.Logging;
using FiresCore.UI;
using UnityEngine;

namespace FiresCore.Possession
{
    /// <summary>
    /// The chase camera for a Frozen-body possession (the engine calls the host's CameraOn/CameraOff): behind the creature along
    /// the heading the mouse turns (PossessionEngine.LookYaw; the creature's own facing in AUTO), framed by its height, aimed at
    /// 75 % of it, never under the ground or the water. It drives the game camera through MiniGameCamera, Core's one camera
    /// arbiter, so nothing else fights for the transform. A Hidden body keeps the player's own camera (CameraOff at its start).
    /// </summary>
    internal static class PossessionCamera
    {
        private static Character s_target;
        private static bool s_on;
        private static WaterVolume s_water;
        // 0.2.327 ([ghost]'s drop 7, Fire): while the possessed creature flies (PossessionEngine.CameraFree), keys steer it and the
        // mouse orbits this camera (yaw + clamped pitch) around it; otherwise the camera trails the heading as before.
        private static bool s_wasFree;
        private static float s_orbitYaw, s_orbitPitch;
        private const float OrbitPitchStart = 15f, OrbitPitchMin = -30f, OrbitPitchMax = 70f;

        internal static void On(Character creature)
        {
            s_target = creature;
            if (creature == null || s_on) return;
            if (MiniGameCamera.Active) { FiresLogger.LogDebug(PossessionRules.Tag + "chase camera skipped: a minigame holds the camera"); return; }
            MiniGameCamera.Begin(ChasePose, 10f, null);
            s_on = true;
            FiresLogger.LogDebug(PossessionRules.Tag + $"chase camera on {creature.gameObject.name} (height {creature.GetHeight():0.0} m)");
        }

        internal static void Off()
        {
            s_target = null;
            if (!s_on) return;
            s_on = false;
            s_wasFree = false;
            MiniGameCamera.End();
            FiresLogger.LogDebug(PossessionRules.Tag + "chase camera off");
        }

        private static MiniGameCamera.Pose? ChasePose()
        {
            Character c = s_target;
            if (c == null) return null;
            float yaw = c.transform.eulerAngles.y;
#if FIRES_POSSESSION_ENGINE
            if (FiresPossession.PossessionEngine.Driven) yaw = FiresPossession.PossessionEngine.LookYaw;
#endif
            float h = Mathf.Clamp(c.GetHeight(), 0.5f, 12f);
            Vector3 aim = c.transform.position + Vector3.up * (h * 0.75f);
            float dist = Mathf.Clamp(h * 2f + 2f, 3.5f, 26f);
            Vector3 pos;
            bool free = false;
#if FIRES_POSSESSION_ENGINE
            free = FiresPossession.PossessionEngine.CameraFree;
#endif
            if (free)
            {
                if (!s_wasFree) { s_orbitYaw = yaw; s_orbitPitch = OrbitPitchStart; }   // start from the trailing view, no snap
                Vector2 mouse = ZInput.GetMouseDelta() * PlayerController.m_mouseSens;   // vanilla's own sensitivity and invert
                s_orbitYaw = Mathf.Repeat(s_orbitYaw + mouse.x, 360f);
                s_orbitPitch = Mathf.Clamp(s_orbitPitch + (PlayerController.m_invertMouse ? mouse.y : -mouse.y), OrbitPitchMin, OrbitPitchMax);
                yaw = s_orbitYaw;
                pos = aim + Quaternion.Euler(s_orbitPitch, s_orbitYaw, 0f) * Vector3.back * dist;
            }
            else
            {
                Vector3 back = Quaternion.Euler(0f, yaw, 0f) * Vector3.back;
                pos = aim + back * dist + Vector3.up * (h * 0.35f + 0.8f);
            }
            if (free != s_wasFree) FiresLogger.LogDebug(PossessionRules.Tag + "chase camera " + (free ? "orbits with the mouse (flying)" : "trails the heading"));
            s_wasFree = free;
            float floor = float.MinValue;
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(pos, out float ground)) floor = ground;
            floor = Mathf.Max(floor, Floating.GetWaterLevel(pos, ref s_water));
            if (pos.y < floor + 0.6f) pos.y = floor + 0.6f;
            Vector3 look = aim - pos;
            return new MiniGameCamera.Pose(pos, look.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(look) : Quaternion.Euler(0f, yaw, 0f), -1f);
        }
    }
}
