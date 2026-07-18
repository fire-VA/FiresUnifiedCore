using System;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// The single arbiter for any Fires minigame that takes over the game camera (air hockey, the arcade cabinet,
    /// chess played at the board, the DOOM TVs). ONE GameCamera.LateUpdate postfix drives the camera transform +
    /// FOV — the technique proven in FiresValcast's PodcastCameraLock/RigCam: set position, rotation, m_fov AND
    /// the live Camera.fieldOfView after vanilla's LateUpdate has run. InputBlock's camera pin yields to this (see
    /// InputBlock.CameraPinPatch), so nothing fights for the transform and the shot never jitters.
    ///
    /// A session calls <see cref="Begin"/> with a per-frame pose provider (world position, look rotation and FOV)
    /// and <see cref="End"/> on teardown. The first frame snaps; subsequent frames ease, so a changing target
    /// (zoom, a moving subject) glides. Returning null from the provider holds the last pose for that frame.
    /// </summary>
    public static class MiniGameCamera
    {
        public struct Pose
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public float Fov;        // <= 0 leaves the vanilla FOV untouched
            public Pose(Vector3 pos, Quaternion rot, float fov) { Position = pos; Rotation = rot; Fov = fov; }
        }

        private static Func<Pose?> _provider;
        private static bool _snap;
        private static float _ease = 12f;
        private static Vector3 _curPos;      // internally-lerped pose — NOT read back from the camera transform,
        private static Quaternion _curRot;   // which vanilla GameCamera.LateUpdate resets every frame (the bounce)

        // Live camera fine-tune (hold the tune combo, see MiniGameCameraTuner): a camera-LOCAL offset applied on
        // top of whatever pose the session hands us — x = right, y = up, z = forward (dolly/zoom). Persisted per
        // session key so each game remembers its framing.
        internal static Vector3 TuneOffset;
        private static string _tuneKey;

        /// <summary>True while the player is holding the camera-tune combo — sessions freeze their game logic so a
        /// tuning drag / scroll doesn't leak into play.</summary>
        public static bool IsTuning { get; internal set; }

        public static bool Active => _provider != null;

        /// <summary>Take over the camera. <paramref name="ease">ease</paramref> is the exponential follow rate
        /// (higher = snappier); the very first frame always snaps. <paramref name="tuneKey"/> names this shot so its
        /// live fine-tune offset persists across sessions (null = not tunable).</summary>
        public static void Begin(Func<Pose?> poseProvider, float ease = 12f) => Begin(poseProvider, ease, null);

        private static float _savedFov = -1f;   // GameCamera.m_fov before we hijacked it, so End can put it back

        public static void Begin(Func<Pose?> poseProvider, float ease, string tuneKey)
        {
            _provider = poseProvider;
            _ease = Mathf.Max(1f, ease);
            _snap = true;
            _tuneKey = tuneKey;
            TuneOffset = CameraTuneStore.Load(tuneKey);
            if (tuneKey != null) MiniGameCameraTuner.Ensure();

            // We overwrite GameCamera.m_fov every frame while active; remember the original so End restores it —
            // otherwise the world stays stuck at the screen-fill FOV after you step away (the "wonky camera").
            var gc = GameCamera.instance;
            if (gc != null && _savedFov <= 0f) _savedFov = gc.m_fov;
        }

        public static void End()
        {
            if (_tuneKey != null) CameraTuneStore.Save(_tuneKey, TuneOffset);
            _provider = null;
            _tuneKey = null;
            IsTuning = false;

            var gc = GameCamera.instance;
            if (gc != null && _savedFov > 0f)
            {
                gc.m_fov = _savedFov;
                var cam = gc.m_camera != null ? gc.m_camera : gc.GetComponent<Camera>();
                if (cam != null) cam.fieldOfView = _savedFov;
            }
            _savedFov = -1f;
        }

        /// <summary>Reset the current shot's live tune offset to zero.</summary>
        public static void ResetTune() { TuneOffset = Vector3.zero; }

        [HarmonyPatch(typeof(GameCamera), "LateUpdate")]
        private static class Driver
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(GameCamera __instance)
            {
                var provider = _provider;
                if (provider == null) return;

                Pose? p;
                try { p = provider(); }
                catch { p = null; }
                if (p == null) return;
                var pose = p.Value;

                // Ease an INTERNAL pose toward the target and write it absolutely. Lerping from the live transform
                // would fight vanilla's per-frame follow/collision reset and make the camera bounce in and out.
                if (_snap)
                {
                    _snap = false;
                    _curPos = pose.Position;
                    _curRot = pose.Rotation;
                }
                else
                {
                    float k = 1f - Mathf.Exp(-_ease * Time.deltaTime);
                    _curPos = Vector3.Lerp(_curPos, pose.Position, k);
                    _curRot = Quaternion.Slerp(_curRot, pose.Rotation, k);
                }

                var t = __instance.transform;
                // Add the live tune offset in the camera's own frame (x right, y up, z forward) so a drag moves the
                // shot the way it looks, not along world axes. Applied after the ease so tuning is instant.
                t.position = _curPos + _curRot * TuneOffset;
                t.rotation = _curRot;

                if (pose.Fov > 1f)
                {
                    __instance.m_fov = pose.Fov;
                    var cam = __instance.m_camera != null ? __instance.m_camera : __instance.GetComponent<Camera>();
                    if (cam != null) cam.fieldOfView = pose.Fov;
                }
            }
        }
    }
}
