using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI
{
    /// <summary>
    /// Shared bottom-right resize grip art for every Fires window (uGUI and IMGUI). Never a font glyph: the
    /// Valheim TMP/IMGUI fonts lack the arrow/triangle glyphs and draw a square instead. Resolution order:
    /// a vanilla sprite found by name, else a generated 16x16 dotted-diagonal texture (cached).
    /// </summary>
    public static class FiresResizeGrip
    {
        // Rounded resize corner (it otherwise rendered as a plain square).
        private static readonly string[] VanillaNames = { "resize_handle", "ResizeHandle", "resize_grip", "ResizeGrip", "grip", "corner_grip" };
        private static Sprite _sprite;
        private static bool _resolved;

        public static Sprite GetGripSprite()
        {
            if (_resolved && _sprite != null) return _sprite;
            _resolved = true;
            string source = null;
            try
            {
                foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                {
                    if (s == null) continue;
                    foreach (var n in VanillaNames)
                        if (string.Equals(s.name, n, System.StringComparison.OrdinalIgnoreCase)) { _sprite = s; source = s.name; break; }
                    if (_sprite != null) break;
                }
            }
            catch { _sprite = null; }
            if (_sprite == null) { _sprite = Generate(); source = "generated"; }
            FiresUnifiedCore.Log?.LogDebug($"[UI] resize grip sprite: {source}");
            return _sprite;
        }

        /// <summary>Adds (or reuses) an Image on the corner showing the grip sprite. Size/position/raycast are left as-is.</summary>
        public static Image AddGrip(RectTransform corner, Color tint)
        {
            if (corner == null) return null;
            var img = corner.GetComponent<Image>() ?? corner.gameObject.AddComponent<Image>();
            img.sprite = GetGripSprite();
            img.type = Image.Type.Simple;
            img.preserveAspect = true;
            img.color = tint;
            return img;
        }

        /// <summary>IMGUI: draws the grip in <paramref name="rect"/> (call from OnGUI in place of a text label).</summary>
        public static void DrawGrip(Rect rect, Color tint)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            var tex = GetGripSprite()?.texture;
            if (tex == null) return;
            var prev = GUI.color;
            GUI.color = tint;
            GUI.DrawTexture(rect, tex, ScaleMode.ScaleToFit, true);
            GUI.color = prev;
        }

        private static Sprite Generate()
        {
            const int N = 16;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "FiresResizeGrip" };
            var clear = new Color32(255, 255, 255, 0);
            var dot = new Color32(255, 255, 255, 255);
            var px = new Color32[N * N];
            for (int i = 0; i < px.Length; i++) px[i] = clear;
            // Three diagonals of 2x2 dots hugging the bottom-right corner (texture y=0 is the bottom row).
            for (int line = 0; line < 3; line++)
            {
                int d = 4 + line * 4;
                for (int k = 0; k + 1 <= d; k += 4)
                {
                    int x = N - 2 - k, y = d - 2 - k;
                    if (y < 0) continue;
                    for (int ox = 0; ox < 2; ox++)
                        for (int oy = 0; oy < 2; oy++)
                        {
                            int xx = x + ox, yy = y + oy;
                            if (xx >= 0 && xx < N && yy >= 0 && yy < N) px[yy * N + xx] = dot;
                        }
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            Object.DontDestroyOnLoad(tex);
            var s = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
            s.name = "FiresResizeGrip";
            Object.DontDestroyOnLoad(s);
            return s;
        }
    }
}
