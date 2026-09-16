using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI
{
    /// <summary>
    /// Runtime twin of the editor-side FiresUiSkin: generates one rounded-rect 9-slice sprite in code and
    /// caches it, so every code-built (codegen) button / panel / input field can share the same soft corners
    /// the baked prefabs use. Tinted by the caller's Image.color, so it keeps each control's palette and only
    /// softens the edges. 9-slice border == corner radius, so corners stay crisp at any control size.
    /// </summary>
    public static class FiresRoundedSprite
    {
        private const int Tex = 48;
        private const int Radius = 12;
        private static Sprite _sprite;

        /// <summary>The shared rounded-rect sprite (generated on first use, then cached for the session).</summary>
        public static Sprite Get()
        {
            if (_sprite != null) return _sprite;

            var tex = new Texture2D(Tex, Tex, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            float half = Tex * 0.5f;
            float innerHalf = half - Radius;
            var pixels = new Color[Tex * Tex];
            for (int y = 0; y < Tex; y++)
            for (int x = 0; x < Tex; x++)
            {
                float px = x + 0.5f - half, py = y + 0.5f - half;
                float qx = Mathf.Abs(px) - innerHalf, qy = Mathf.Abs(py) - innerHalf;
                float outside = Mathf.Sqrt(Mathf.Max(qx, 0f) * Mathf.Max(qx, 0f) + Mathf.Max(qy, 0f) * Mathf.Max(qy, 0f));
                float inside = Mathf.Min(Mathf.Max(qx, qy), 0f);
                float dist = outside + inside - Radius;
                float alpha = Mathf.Clamp01(0.5f - dist);
                pixels[y * Tex + x] = new Color(1f, 1f, 1f, alpha);
            }
            tex.SetPixels(pixels);
            tex.Apply(false, false);

            _sprite = Sprite.Create(tex, new Rect(0, 0, Tex, Tex), new Vector2(0.5f, 0.5f), 100f, 0,
                                    SpriteMeshType.FullRect, new Vector4(Radius, Radius, Radius, Radius));
            _sprite.name = "FiresRoundedRect";
            return _sprite;
        }

        /// <summary>Softens an existing Image's corners in place, preserving its color as the tint.</summary>
        public static void Apply(Image img)
        {
            if (img == null) return;
            img.sprite = Get();
            img.type = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 1f;
        }
    }
}
