// Fires UI kit, step 0. Procedural sprites and textures (FIRES_UI_STYLE_GUIDE.md S4, S11 "the sprites and
// 9-slices we will actually ship", S12 chamfer; FIRES_UI_SHELL.md S10 parallax textures). Built once, cached.
// No shipped image assets. Byte-identical copy in ValheimRip1.0/Assets/FiresUIKit/Runtime.
// ASCII-only (10-14): non-ASCII characters are backslash-u escapes in strings and ASCII words in comments. Core UI/Shell/Kit and
// ValheimRip1.0/Assets/FiresUIKit/Runtime hold byte-identical copies (check with cmp).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.UI.Shell.Kit
{
    public enum Corner { Square, Chamfer45, Rounded }   // Rounded appended (radius FiresTokens.Radius)
    public enum FadeMode { Center, FromLeft }

    public static class KitSprites
    {
        const int Supersample = 4;
        static readonly Dictionary<string, Sprite> Sprites = new Dictionary<string, Sprite>();
        static readonly Dictionary<string, Texture2D> Textures = new Dictionary<string, Texture2D>();

        /// <summary>Destroys every cached texture/sprite (mockup cleanup; Core calls it on shutdown).</summary>
        public static void ClearCache()
        {
            foreach (var s in Sprites.Values) if (s != null) UnityEngine.Object.DestroyImmediate(s);
            foreach (var t in Textures.Values) if (t != null) UnityEngine.Object.DestroyImmediate(t);
            Sprites.Clear();
            Textures.Clear();
        }

        // ---- shape helpers ---------------------------------------------------------------------------

        // Signed inside-distance of a point in a w*h rect with 45 deg corners cut by 'chamfer' (0 = square).
        static float FrameDistance(float x, float y, float w, float h, float chamfer)
        {
            float cx = Mathf.Min(x, w - x);
            float cy = Mathf.Min(y, h - y);
            float d = Mathf.Min(cx, cy);
            if (chamfer > 0f) d = Mathf.Min(d, (cx + cy - chamfer) * 0.70710678f);
            return d;
        }

        static float RoundedDistance(float x, float y, float w, float h, float r)
        {
            float cx = Mathf.Min(x, w - x);
            float cy = Mathf.Min(y, h - y);
            if (cx < r && cy < r)
            {
                float dx = r - cx, dy = r - cy;
                return r - Mathf.Sqrt(dx * dx + dy * dy);
            }
            return Mathf.Min(cx, cy);
        }

        static Texture2D NewTexture(string name, int w, int h, TextureWrapMode wrap = TextureWrapMode.Clamp)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = "FiresKit_" + name,
                wrapMode = wrap,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave
            };
            return t;
        }

        // Supersampled coverage fill: shade(x, y) returns a premultiplied-free colour for a sample point.
        static Texture2D Paint(string name, int w, int h, Func<float, float, Color> shade)
        {
            var t = NewTexture(name, w, h);
            var px = new Color[w * h];
            float inv = 1f / (Supersample * Supersample);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float r = 0, g = 0, b = 0, a = 0;
                    for (int sy = 0; sy < Supersample; sy++)
                        for (int sx = 0; sx < Supersample; sx++)
                        {
                            var c = shade(x + (sx + 0.5f) / Supersample, y + (sy + 0.5f) / Supersample);
                            r += c.r * c.a; g += c.g * c.a; b += c.b * c.a; a += c.a;
                        }
                    px[y * w + x] = a > 0f ? new Color(r / a, g / a, b / a, a * inv) : new Color(1f, 1f, 1f, 0f);
                }
            t.SetPixels(px);
            t.Apply(false, false);
            return t;
        }

        static Sprite Make(string key, Texture2D t, float border)
        {
            Textures[key] = t;
            var s = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 100f, 0, // 100 PPU = the canvas reference PPU (1 PPU made sliced borders 100x too wide)
                SpriteMeshType.FullRect, new Vector4(border, border, border, border));
            s.name = t.name;
            s.hideFlags = HideFlags.DontSave;
            Sprites[key] = s;
            return s;
        }

        // ---- window frame ----------------------------------------------------------------------------

        /// <summary>White window-body mask (tint with Panel), 9-sliced: square, chamfered, or Rounded (FiresTokens.Radius).</summary>
        public static Sprite FrameFill(Corner corner, float chamfer)
        {
            bool round = corner == Corner.Rounded;
            float c = corner == Corner.Chamfer45 ? Mathf.Max(1f, chamfer) : round ? FiresTokens.Radius : 0f;
            string key = "frameFill_" + (round ? "r" : "") + c;
            if (Sprites.TryGetValue(key, out var s)) return s;
            int border = Mathf.CeilToInt(c) + 6;
            int size = border * 2 + 4;
            var t = Paint(key, size, size, (x, y) =>
                (round ? RoundedDistance(x, y, size, size, c) : FrameDistance(x, y, size, size, c)) >= 0f ? Color.white : new Color(1, 1, 1, 0));
            return Make(key, t, border);
        }

        /// <summary>The frame: one 1 px Edge line (FrameEdgeAlpha) plus a soft inner shadow fading over FrameShadowWidth.
        /// Chamfer45 cuts the 4 corners; Rounded rounds them with FiresTokens.Radius. Colours baked from tokens.</summary>
        public static Sprite FrameLines(Corner corner, float chamfer)
        {
            bool round = corner == Corner.Rounded;
            float c = corner == Corner.Chamfer45 ? Mathf.Max(1f, chamfer) : round ? FiresTokens.Radius : 0f;
            string key = "frameLines_" + (round ? "r" : "") + c;
            if (Sprites.TryGetValue(key, out var s)) return s;
            float sw = FiresTokens.FrameShadowWidth;
            int border = Mathf.CeilToInt(c + sw) + 4;
            int size = border * 2 + 4;
            var edge = FiresTokens.WithAlpha(FiresTokens.Edge, FiresTokens.FrameEdgeAlpha);
            var clear = new Color(0, 0, 0, 0);
            var t = Paint(key, size, size, (x, y) =>
            {
                float d = round ? RoundedDistance(x, y, size, size, c) : FrameDistance(x, y, size, size, c);
                if (d < 0f) return clear;
                if (d < FiresTokens.FrameLine) return edge;
                float k = 1f - Mathf.Clamp01((d - FiresTokens.FrameLine) / sw);
                return new Color(0f, 0f, 0f, FiresTokens.FrameShadowAlpha * k * k);
            });
            return Make(key, t, border);
        }

        /// <summary>Rounded 9-slice FILL (white, tint it) with radius r: Radius for panels/popovers, RadiusSmall for rows/inputs/buttons.</summary>
        public static Sprite RoundedFill(float r) => RoundPill(r, false);

        /// <summary>Rounded 9-slice 1 px OUTLINE (white, tint it) with radius r; pairs with RoundedFill.</summary>
        public static Sprite RoundedOutline(float r) => RoundPill(r, true);

        /// <summary>Rounded pill fill (white) with radius r, 9-sliced (ClassesStyle.PaintPill look).</summary>
        public static Sprite RoundPill(float r, bool outline)
        {
            string key = "roundPill_" + r + "_" + outline;
            if (Sprites.TryGetValue(key, out var s)) return s;
            int ri = Mathf.CeilToInt(r);
            int size = ri * 2 + 4;
            var t = Paint(key, size, size, (x, y) =>
            {
                float d = RoundedDistance(x, y, size, size, r);
                bool on = outline ? d >= 0f && d < 1f : d >= 0f;
                return on ? Color.white : new Color(1, 1, 1, 0);
            });
            return Make(key, t, ri + 1);
        }

        /// <summary>A 1 px box outline (white), 9-sliced. Rotated 45 deg it is the outlined diamond.</summary>
        public static Sprite BoxOutline()
        {
            const string key = "boxOutline";
            if (Sprites.TryGetValue(key, out var s)) return s;
            const int size = 8;
            var t = Paint(key, size, size, (x, y) =>
                FrameDistance(x, y, size, size, 0f) < 1f ? Color.white : new Color(1, 1, 1, 0));
            t.filterMode = FilterMode.Point;
            return Make(key, t, 2);
        }

        /// <summary>Plain white (fills, hairlines, tracks).</summary>
        public static Sprite White()
        {
            const string key = "white";
            if (Sprites.TryGetValue(key, out var s)) return s;
            var t = NewTexture(key, 4, 4);
            var px = new Color[16];
            for (int i = 0; i < px.Length; i++) px[i] = Color.white;
            t.SetPixels(px); t.Apply(false, false);
            return Make(key, t, 1);
        }

        // ---- pills, diamond, knob --------------------------------------------------------------------

        /// <summary>Rounded RadiusSmall (6 px) rectangle fill (white), 9-sliced.</summary>
        public static Sprite Pill()
        {
            const string key = "pill";
            if (Sprites.TryGetValue(key, out var s)) return s;
            int r = Mathf.CeilToInt(FiresTokens.RadiusSmall);
            int size = r * 2 + 8;
            var t = Paint(key, size, size, (x, y) =>
                RoundedDistance(x, y, size, size, FiresTokens.RadiusSmall) >= 0f ? Color.white : new Color(1, 1, 1, 0));
            return Make(key, t, r + 1);
        }

        /// <summary>Rounded RadiusSmall (6 px) rectangle 1 px outline (white), 9-sliced.</summary>
        public static Sprite PillOutline()
        {
            const string key = "pillOutline";
            if (Sprites.TryGetValue(key, out var s)) return s;
            int r = Mathf.CeilToInt(FiresTokens.RadiusSmall);
            int size = r * 2 + 8;
            var t = Paint(key, size, size, (x, y) =>
            {
                float d = RoundedDistance(x, y, size, size, FiresTokens.RadiusSmall);
                return d >= 0f && d < 1f ? Color.white : new Color(1, 1, 1, 0);
            });
            return Make(key, t, r + 1);
        }

        /// <summary>Filled diamond (a 45 deg square, antialiased), white, 64 px. Use Simple, preserve aspect.</summary>
        public static Sprite Diamond()
        {
            const string key = "diamond";
            if (Sprites.TryGetValue(key, out var s)) return s;
            const int size = 64;
            const float h = size * 0.5f;
            var t = Paint(key, size, size, (x, y) =>
                Mathf.Abs(x - h) + Mathf.Abs(y - h) <= h - 1f ? Color.white : new Color(1, 1, 1, 0));
            return Make(key, t, 0);
        }

        /// <summary>Filled circle (badge dots, slider knob, status dots), white, 32 px.</summary>
        public static Sprite Circle()
        {
            const string key = "circle";
            if (Sprites.TryGetValue(key, out var s)) return s;
            const int size = 32;
            const float h = size * 0.5f;
            var t = Paint(key, size, size, (x, y) =>
            {
                float dx = x - h, dy = y - h;
                return dx * dx + dy * dy <= (h - 1f) * (h - 1f) ? Color.white : new Color(1, 1, 1, 0);
            });
            return Make(key, t, 0);
        }

        // ---- lines and washes ------------------------------------------------------------------------

        /// <summary>
        /// Horizontal fade line. Center: transparent -> end -> mid -> end -> transparent (the header divider).
        /// FromLeft: end at the left fading to transparent at 85 % (section underline, selected wash).
        /// </summary>
        public static Sprite FadeLine(Color end, Color mid, FadeMode mode)
        {
            string key = "fade_" + mode + "_" + ColorUtility.ToHtmlStringRGBA(end) + "_" + ColorUtility.ToHtmlStringRGBA(mid);
            if (Sprites.TryGetValue(key, out var s)) return s;
            const int w = 256;
            var t = NewTexture(key, w, 1);
            var px = new Color[w];
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w;
                Color c;
                if (mode == FadeMode.Center)
                {
                    float k = 1f - Mathf.Abs(u - 0.5f) * 2f;                 // 0 at ends, 1 at centre
                    if (k < 0.5f) { c = end; c.a *= Mathf.SmoothStep(0f, 1f, k / 0.5f); }
                    else c = Color.Lerp(end, mid, (k - 0.5f) / 0.5f);
                }
                else
                {
                    c = Color.Lerp(mid, end, Mathf.Clamp01(u / 0.4f));
                    c.a *= 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(u / 0.85f));
                }
                px[x] = c;
            }
            t.SetPixels(px); t.Apply(false, false);
            return Make(key, t, 0);
        }

        // ---- backdrop --------------------------------------------------------------------------------

        /// <summary>Radial scrim: the Backdrop colour, alpha ScrimAlphaCentre -> Mid -> Edge (feathered edges).</summary>
        public static Texture2D Vignette()
        {
            const string key = "vignette";
            if (Textures.TryGetValue(key, out var cached)) return cached;
            int n = FiresTokens.ProceduralTextureSize;
            var t = NewTexture(key, n, n);
            var px = new Color[n * n];
            var baseColor = FiresTokens.Backdrop;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy) / 1.41421356f;   // 0 centre, 1 corner
                    float a = r < FiresTokens.ScrimMidRadius
                        ? Mathf.Lerp(FiresTokens.ScrimAlphaCentre, FiresTokens.ScrimAlphaMid, r / FiresTokens.ScrimMidRadius)
                        : Mathf.Lerp(FiresTokens.ScrimAlphaMid, FiresTokens.ScrimAlphaEdge, (r - FiresTokens.ScrimMidRadius) / (1f - FiresTokens.ScrimMidRadius));
                    px[y * n + x] = FiresTokens.WithAlpha(baseColor, a);
                }
            t.SetPixels(px); t.Apply(false, false);
            Textures[key] = t;
            return t;
        }

        /// <summary>Soft tileable value-noise (white, alpha = noise), 256 px, for the parallax haze.</summary>
        public static Texture2D Noise()
        {
            const string key = "noise";
            if (Textures.TryGetValue(key, out var cached)) return cached;
            int n = FiresTokens.ProceduralTextureSize;
            var t = NewTexture(key, n, n, TextureWrapMode.Repeat);
            var px = new Color[n * n];
            var rng = new System.Random(1014);
            // three octaves of tileable lattice noise
            int[] cells = { 4, 8, 16 };
            float[] weights = { 0.55f, 0.30f, 0.15f };
            var grids = new float[cells.Length][];
            for (int o = 0; o < cells.Length; o++)
            {
                grids[o] = new float[cells[o] * cells[o]];
                for (int i = 0; i < grids[o].Length; i++) grids[o][i] = (float)rng.NextDouble();
            }
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float v = 0f;
                    for (int o = 0; o < cells.Length; o++)
                    {
                        int c = cells[o];
                        float fx = (float)x / n * c, fy = (float)y / n * c;
                        int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
                        float tx = Mathf.SmoothStep(0f, 1f, fx - x0), ty = Mathf.SmoothStep(0f, 1f, fy - y0);
                        float a = grids[o][(y0 % c) * c + (x0 % c)], b = grids[o][(y0 % c) * c + ((x0 + 1) % c)];
                        float d = grids[o][((y0 + 1) % c) * c + (x0 % c)], e = grids[o][((y0 + 1) % c) * c + ((x0 + 1) % c)];
                        v += Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(d, e, tx), ty) * weights[o];
                    }
                    px[y * n + x] = new Color(1f, 1f, 1f, Mathf.SmoothStep(0f, 1f, v));
                }
            t.SetPixels(px); t.Apply(false, false);
            Textures[key] = t;
            return t;
        }

        /// <summary>Sparse soft dust motes (white), tileable 256 px, for the parallax dust layer.</summary>
        public static Texture2D Dust()
        {
            const string key = "dust";
            if (Textures.TryGetValue(key, out var cached)) return cached;
            int n = FiresTokens.ProceduralTextureSize;
            var t = NewTexture(key, n, n, TextureWrapMode.Repeat);
            var px = new Color[n * n];
            for (int i = 0; i < px.Length; i++) px[i] = new Color(1f, 1f, 1f, 0f);
            var rng = new System.Random(4511);
            for (int m = 0; m < 22; m++)
            {
                float cx = (float)rng.NextDouble() * n, cy = (float)rng.NextDouble() * n;
                float radius = 0.8f + (float)rng.NextDouble() * 1.6f;
                float peak = 0.35f + (float)rng.NextDouble() * 0.65f;
                int r = Mathf.CeilToInt(radius * 2f);
                for (int oy = -r; oy <= r; oy++)
                    for (int ox = -r; ox <= r; ox++)
                    {
                        int x = ((Mathf.FloorToInt(cx) + ox) % n + n) % n, y = ((Mathf.FloorToInt(cy) + oy) % n + n) % n;
                        float dx = Mathf.FloorToInt(cx) + ox + 0.5f - cx, dy = Mathf.FloorToInt(cy) + oy + 0.5f - cy;
                        float a = peak * Mathf.Exp(-(dx * dx + dy * dy) / (radius * radius));
                        var p = px[y * n + x];
                        p.a = Mathf.Max(p.a, a);
                        px[y * n + x] = p;
                    }
            }
            t.SetPixels(px); t.Apply(false, false);
            Textures[key] = t;
            return t;
        }
    
        // ---- galaxy backdrop --------------------------------------------------------------------------

        static float TileNoise(int x, int y, int n, int[] cells, float[] weights, float[][] grids)
        {
            float v = 0f;
            for (int o = 0; o < cells.Length; o++)
            {
                int c = cells[o];
                float fx = (float)x / n * c, fy = (float)y / n * c;
                int x0 = Mathf.FloorToInt(fx), y0 = Mathf.FloorToInt(fy);
                float tx = Mathf.SmoothStep(0f, 1f, fx - x0), ty = Mathf.SmoothStep(0f, 1f, fy - y0);
                float a = grids[o][(y0 % c) * c + (x0 % c)], b = grids[o][(y0 % c) * c + ((x0 + 1) % c)];
                float d = grids[o][((y0 + 1) % c) * c + (x0 % c)], e = grids[o][((y0 + 1) % c) * c + ((x0 + 1) % c)];
                v += Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(d, e, tx), ty) * weights[o];
            }
            return v;
        }

        static float[][] Grids(int seed, int[] cells)
        {
            var rng = new System.Random(seed);
            var g = new float[cells.Length][];
            for (int o = 0; o < cells.Length; o++)
            {
                g[o] = new float[cells[o] * cells[o]];
                for (int i = 0; i < g[o].Length; i++) g[o][i] = (float)rng.NextDouble();
            }
            return g;
        }

        /// <summary>Opaque tileable nebula: fBm density (deep -> indigo) with warm dust lanes, low saturation, dark.</summary>
        public static Texture2D Nebula(bool rimColours = false)
        {
            string key = rimColours ? "nebulaRim" : "nebula";
            if (Textures.TryGetValue(key, out var cached)) return cached;
            int n = FiresTokens.GalaxyTextureSize;
            var t = NewTexture(key, n, n, TextureWrapMode.Repeat);
            var px = new Color[n * n];
            int[] cells = { 3, 6, 12, 24 };
            float[] w = { 0.5f, 0.27f, 0.15f, 0.08f };
            var g1 = Grids(1407, cells);
            var g2 = Grids(4511, cells);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float density = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((TileNoise(x, y, n, cells, w, g1) - 0.35f) / 0.45f));
                    float dust = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((TileNoise(x, y, n, cells, w, g2) - 0.45f) / 0.35f));
                    // rimColours (variant "margins"): brighter colours; the GalaxyShade layer darkens the centre back down
                    var c = Color.Lerp(FiresTokens.GalaxyDeep, rimColours ? FiresTokens.GalaxyIndigoRim : FiresTokens.GalaxyIndigo, density);
                    c = Color.Lerp(c, rimColours ? FiresTokens.GalaxyDustRim : FiresTokens.GalaxyDust, dust * (0.4f + 0.6f * density));
                    c.a = 1f;
                    px[y * n + x] = c;
                }
            t.SetPixels(px); t.Apply(false, false);
            Textures[key] = t;
            return t;
        }

        /// <summary>Radial GalaxyDeep shade: GalaxyShadeCentre alpha inside GalaxyShadeInner, fading to 0 at GalaxyShadeOuter.
        /// Screen-fixed (no drift), so the area behind the window stays dark and only the margins get colour.</summary>
        public static Texture2D GalaxyShade()
        {
            const string key = "galaxyShade";
            if (Textures.TryGetValue(key, out var cached)) return cached;
            int n = FiresTokens.ProceduralTextureSize;
            var t = NewTexture(key, n, n);
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                    float r = Mathf.Sqrt(dx * dx + dy * dy) / 1.41421356f;
                    float k = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(FiresTokens.GalaxyShadeInner, FiresTokens.GalaxyShadeOuter, r));
                    px[y * n + x] = FiresTokens.WithAlpha(FiresTokens.GalaxyDeep, FiresTokens.GalaxyShadeCentre * k);
                }
            t.SetPixels(px); t.Apply(false, false);
            Textures[key] = t;
            return t;
        }

        /// <summary>Sparse small soft stars (white, alpha = brightness), tileable.</summary>
        public static Texture2D Stars()
        {
            const string key = "stars";
            if (Textures.TryGetValue(key, out var cached)) return cached;
            int n = FiresTokens.GalaxyTextureSize;
            var t = NewTexture(key, n, n, TextureWrapMode.Repeat);
            var px = new Color[n * n];
            for (int i = 0; i < px.Length; i++) px[i] = new Color(1f, 1f, 1f, 0f);
            var rng = new System.Random(1014);
            for (int m = 0; m < 70; m++)
            {
                float cx = (float)rng.NextDouble() * n, cy = (float)rng.NextDouble() * n;
                float radius = 0.55f + (float)rng.NextDouble() * 0.6f;
                float peak = 0.25f + (float)rng.NextDouble() * (float)rng.NextDouble() * 0.75f;
                for (int oy = -3; oy <= 3; oy++)
                    for (int ox = -3; ox <= 3; ox++)
                    {
                        int ix = Mathf.FloorToInt(cx) + ox, iy = Mathf.FloorToInt(cy) + oy;
                        float dx = ix + 0.5f - cx, dy = iy + 0.5f - cy;
                        float a = peak * Mathf.Exp(-(dx * dx + dy * dy) / (radius * radius));
                        int k = ((iy % n + n) % n) * n + ((ix % n + n) % n);
                        var p = px[k]; p.a = Mathf.Max(p.a, a); px[k] = p;
                    }
            }
            t.SetPixels(px); t.Apply(false, false);
            Textures[key] = t;
            return t;
        }
    }
}
