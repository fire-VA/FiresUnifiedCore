using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace FiresCore.UI
{
    using Input = UnityEngine.Input;   // sibling FiresCore.Input namespace otherwise shadows UnityEngine.Input here

    /// <summary>
    /// Live camera fine-tuning for any Fires minigame that locks the camera through <see cref="MiniGameCamera"/>.
    /// Hold the tune combo (Ctrl+Alt by default) while a session owns the shot and the mouse moves the camera —
    /// left/right/up/down pan, wheel dollies in and out — writing a camera-local offset the driver applies on top of
    /// the session's pose. R re-centres. The offset persists per session key (<see cref="CameraTuneStore"/>) so each
    /// game keeps its framing. A small readout shows the live values so they can be reported / baked. The tuner never
    /// writes the cursor (single-authority rule — the session keeps it), and reads the raw mouse axes, which report
    /// device delta even while the cursor is locked.
    /// </summary>
    internal sealed class MiniGameCameraTuner : MonoBehaviour
    {
        private const float PanSpeed = 0.03f;    // metres per mouse-axis unit
        private const float ZoomSpeed = 1.5f;    // metres per wheel notch
        private const float Limit = 20f;         // clamp so a wild drag can't fling the camera away

        private static MiniGameCameraTuner _instance;
        private static GUIStyle _style;
        private static Texture2D _bg;

        public static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject("FiresMiniGameCameraTuner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<MiniGameCameraTuner>();
        }

        private void Update()
        {
            if (!MiniGameCamera.Active) { MiniGameCamera.IsTuning = false; return; }

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            bool combo = ctrl && alt;
            MiniGameCamera.IsTuning = combo;
            if (!combo) return;

            var offset = MiniGameCamera.TuneOffset;
            offset.x += Input.GetAxis("Mouse X") * PanSpeed;               // camera right
            offset.y += Input.GetAxis("Mouse Y") * PanSpeed;               // camera up
            offset.z += Input.GetAxis("Mouse ScrollWheel") * ZoomSpeed;   // dolly forward (zoom)
            offset.x = Mathf.Clamp(offset.x, -Limit, Limit);
            offset.y = Mathf.Clamp(offset.y, -Limit, Limit);
            offset.z = Mathf.Clamp(offset.z, -Limit, Limit);
            MiniGameCamera.TuneOffset = offset;

            if (Input.GetKeyDown(KeyCode.R)) MiniGameCamera.ResetTune();
        }

        private void OnGUI()
        {
            if (!MiniGameCamera.Active) return;
            EnsureStyle();

            var offset = MiniGameCamera.TuneOffset;
            string text = MiniGameCamera.IsTuning
                ? $"CAMERA TUNE   pan X {offset.x:0.00}  Y {offset.y:0.00}   zoom {offset.z:0.00}   —  mouse moves · wheel zooms · R re-centres"
                : "Esc: exit    ·    Hold Ctrl+Alt: move camera    ·    wheel: zoom";

            float width = 760f, height = 26f;
            var rect = new Rect((Screen.width - width) * 0.5f, Screen.height - 44f, width, height);
            GUI.color = new Color(1f, 1f, 1f, MiniGameCamera.IsTuning ? 0.95f : 0.5f);
            GUI.Label(rect, text, _style);
            GUI.color = Color.white;
        }

        private static void EnsureStyle()
        {
            if (_style != null) return;
            if (_bg == null)
            {
                _bg = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                _bg.SetPixel(0, 0, new Color(0.06f, 0.06f, 0.08f, 0.72f));
                _bg.Apply();
                _bg.hideFlags = HideFlags.HideAndDontSave;
            }
            _style = new GUIStyle
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 14,
                fontStyle = FontStyle.Bold
            };
            _style.normal.textColor = new Color(1f, 0.86f, 0.55f);
            _style.normal.background = _bg;
            _style.padding = new RectOffset(10, 10, 4, 4);
        }
    }

    /// <summary>Tiny flat-file store for per-game camera tune offsets — one line per key
    /// (<c>key|x|y|z</c>) in BepInEx/config, invariant-culture floats. Keeps each locked-camera minigame's
    /// framing across sessions and restarts without minting a config entry per game.</summary>
    internal static class CameraTuneStore
    {
        private static readonly string Path =
            System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "fires_camera_tune.cfg");

        private static Dictionary<string, Vector3> _cache;

        private static void EnsureLoaded()
        {
            if (_cache != null) return;
            _cache = new Dictionary<string, Vector3>();
            try
            {
                if (!File.Exists(Path)) return;
                foreach (var line in File.ReadAllLines(Path))
                {
                    var parts = line.Split('|');
                    if (parts.Length < 4) continue;
                    if (float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                        && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                        && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                        _cache[parts[0]] = new Vector3(x, y, z);
                }
            }
            catch { /* first run / unreadable — just start empty */ }
        }

        public static Vector3 Load(string key)
        {
            if (string.IsNullOrEmpty(key)) return Vector3.zero;
            EnsureLoaded();
            return _cache.TryGetValue(key, out var offset) ? offset : Vector3.zero;
        }

        public static void Save(string key, Vector3 offset)
        {
            if (string.IsNullOrEmpty(key)) return;
            EnsureLoaded();
            _cache[key] = offset;
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in _cache)
                    sb.Append(kv.Key).Append('|')
                      .Append(kv.Value.x.ToString(CultureInfo.InvariantCulture)).Append('|')
                      .Append(kv.Value.y.ToString(CultureInfo.InvariantCulture)).Append('|')
                      .Append(kv.Value.z.ToString(CultureInfo.InvariantCulture)).Append('\n');
                File.WriteAllText(Path, sb.ToString());
            }
            catch { /* read-only fs — tuning still works in-memory this session */ }
        }
    }
}
