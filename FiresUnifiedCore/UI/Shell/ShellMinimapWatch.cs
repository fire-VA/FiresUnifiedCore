// Fires UI Shell, Core the round minimap as a POCKET WATCH. Glass over the map (the HUD globes' own
// Custom/FiresGlobeGlass when RPGmaker has loaded it, else a Core highlight), a brass case, the sun and moon riding the outer ring
// as a clock (Galaxies' moon phase through Core's MoonPhase when Galaxies drives it), night clearly darker than day, a wind sock
// pill behind the map's top-right, the whole column above the Esc menu's dim layer (so +/- take clicks there) and draggable as one
// unit by the brass case while the cursor is free, its offset saved per resolution. Everything here is override-ON only and is
// put back by RestoreWatch.
using System;
using FiresCore.UI.Shell.Kit;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FiresCore.UI.Shell
{
    internal static partial class ShellMinimap
    {
        internal static readonly Color Brass = new Color(0.78f, 0.60f, 0.32f, 1f);
        internal static readonly Color BrassDark = new Color(0.34f, 0.24f, 0.11f, 1f);

        // ---- glass ------------------------------------------------------------------------------------------
        private static Image s_glass;
        private static Material s_glassMat;
        private static bool s_glassIsGlobe;
        private static float s_nextGlassTry;

        private static void BuildGlass(RectTransform art)
        {
            s_glass = Widgets.Box(art, "Glass", KitSprites.White(), Color.white, false);
            Widgets.Stretch(s_glass.rectTransform, 0, 0, 0, 0);
            s_glass.raycastTarget = false;
            if (!TryGlobeGlass()) UseCoreGlass();
        }

        // The globes' glass: the same asset values GlobeView uses, a square rect, no glint.
        private static bool TryGlobeGlass()
        {
            var sh = Shader.Find("Custom/FiresGlobeGlass");
            if (sh == null || !sh.isSupported || s_glass == null) return false;
            s_glassMat = new Material(sh) { name = "FiresMinimapGlass", hideFlags = HideFlags.DontSave };
            void F(string p, float v) { if (s_glassMat.HasProperty(p)) s_glassMat.SetFloat(p, v); }
            void C(string p, Color v) { if (s_glassMat.HasProperty(p)) s_glassMat.SetColor(p, v); }
            if (s_glassMat.HasProperty("_VesselCenter")) s_glassMat.SetVector("_VesselCenter", new Vector4(0.5f, 0.5f, 0f, 0f));
            F("_VesselRadius", 0.49f); F("_Aspect", 1f); F("_Mode", 0f); F("_Glint", 0f);
            C("_BodyTint", new Color(0.08f, 0.10f, 0.13f, 0.06f)); F("_EdgeDarkness", 0.4f);
            C("_RimColor", new Color(0.82f, 0.90f, 1.0f, 1f)); F("_RimPower", 3.2f); F("_RimStrength", 0.55f);
            // a small tight highlight like the globes' spot, little sheen or
            // reflection over the map, so the map stays readable
            F("_ReflectStrength", 0.12f); F("_SpecPower", 420f); F("_SpecStrength", 0.55f); F("_SheenStrength", 0.03f);
            F("_KickerStrength", 0.05f); F("_TransmitStrength", 0.6f); F("_ArtBlend", 0.35f);
            s_glass.sprite = KitSprites.White();
            s_glass.material = s_glassMat;
            s_glass.color = Color.white;
            s_glassIsGlobe = true;
            ShellLog.Info("minimap glass: the HUD globes' Custom/FiresGlobeGlass over the map (same asset values as the globes)");
            return true;
        }

        private static void UseCoreGlass()
        {
            s_glass.material = null;
            s_glass.sprite = GlassSprite();
            s_glass.color = Color.white;
            s_glassIsGlobe = false;
            ShellLog.Info("minimap glass: Core highlight (the globes' glass shader is not loaded: RPGmaker's globes off, or not yet)");
        }

        // A domed-glass look without the shader: a soft top-left highlight, a thin bright rim, the edge slightly darker.
        private static Sprite s_glassSprite;
        private static Sprite GlassSprite()
        {
            if (s_glassSprite != null) return s_glassSprite;
            const int N = 256;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave, name = "FiresMinimapGlass" };
            var px = new Color32[N * N];
            float c = (N - 1) * 0.5f;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float dx = (x - c) / c, dy = (y - c) / c, r = Mathf.Sqrt(dx * dx + dy * dy);
                    if (r > 1f) { px[y * N + x] = new Color32(0, 0, 0, 0); continue; }
                    float edge = Mathf.Clamp01((1f - r) * N * 0.5f);                                  // anti-aliased disc
                    float hx = dx + 0.48f, hy = dy - 0.50f;                                          // up and left, out of the way
                    float spec = Mathf.Exp(-(hx * hx * 40f + hy * hy * 55f)) * 0.42f;
                    float rim = Mathf.Pow(r, 12f) * 0.25f;                                           // bright rim
                    float darken = Mathf.Pow(r, 3f) * 0.10f;                                         // the curve darkens the edge
                    float a = Mathf.Clamp01(spec + rim + darken) * edge;
                    float l = Mathf.Clamp01((spec + rim) / Mathf.Max(0.001f, spec + rim + darken));  // highlight white, edge dark
                    byte v = (byte)(255f * Mathf.Lerp(0.05f, 1f, l));
                    px[y * N + x] = new Color32(v, v, (byte)Mathf.Min(255, v + 8), (byte)(a * 255f));
                }
            tex.SetPixels32(px); tex.Apply(false);
            s_glassSprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
            s_glassSprite.hideFlags = HideFlags.DontSave;
            return s_glassSprite;
        }

        // ---- the sun / moon clock on the ring ---------------------------------------------------------------
        private static RectTransform s_sun, s_moon;
        private static Image s_moonImg;
        private static float s_ringR;
        private static int s_moonBucket = -1;
        private static string s_clockSig;

        private static void BuildClock(RectTransform art, float r)
        {
            s_ringR = r;
            float size = Mathf.Max(14f, r * 2f * 0.09f);
            var sun = Widgets.Box(art, "Sun", SunSprite(), Color.white, false);
            s_sun = sun.rectTransform; s_sun.anchorMin = s_sun.anchorMax = s_sun.pivot = new Vector2(0.5f, 0.5f); s_sun.sizeDelta = new Vector2(size, size);
            sun.raycastTarget = false;
            s_moonImg = Widgets.Box(art, "Moon", KitSprites.Circle(), Color.white, false);
            s_moon = s_moonImg.rectTransform; s_moon.anchorMin = s_moon.anchorMax = s_moon.pivot = new Vector2(0.5f, 0.5f); s_moon.sizeDelta = new Vector2(size, size);
            s_moonImg.raycastTarget = false;
            s_moon.SetAsLastSibling(); s_sun.SetAsLastSibling();
        }

        private static void UpdateClock()
        {
            if (s_sun == null || EnvMan.instance == null) return;
            // (R-E12 logged sun 269 then 90): before the world clock syncs, the day fraction is a placeholder; wait for it
            if (ZNet.instance == null || ZNet.instance.GetTimeSeconds() < 1.0) return;
            float f = EnvMan.instance.GetDayFraction();
            // noon = top, sunrise (0.25) = east (right), sunset (0.75) = west (left); the moon is opposite
            float sunDeg = 90f + (f - 0.5f) * 360f, moonDeg = sunDeg + 180f;
            s_sun.anchoredPosition = Dir(sunDeg) * s_ringR;
            s_moon.anchoredPosition = Dir(moonDeg) * s_ringR;
            float phase = FiresCore.Services.MoonPhase.Compute01();
            int bucket = Mathf.RoundToInt(phase * 31f);
            if (bucket != s_moonBucket) { s_moonBucket = bucket; s_moonImg.sprite = MoonSprite(bucket / 31f); }
            bool galaxies = FiresCore.Services.MoonPhase.Override >= 0f;
            string sig = Mathf.RoundToInt(sunDeg / 15f) + "/" + bucket;
            if (sig != s_clockSig)
            {
                s_clockSig = sig;
                ShellLog.Info($"minimap clock: sun {Mathf.Repeat(sunDeg, 360f):0} deg, moon {Mathf.Repeat(moonDeg, 360f):0} deg, phase {phase:0.00} (source {(galaxies ? "Galaxies" : "Core")})");
            }
        }

        private static Vector2 Dir(float deg) => new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad));

        private static Sprite s_sunSprite;
        private static Sprite SunSprite()
        {
            if (s_sunSprite != null) return s_sunSprite;
            const int N = 64; float c = (N - 1) * 0.5f;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave, name = "FiresMinimapSun" };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                float dx = x - c, dy = y - c, r = Mathf.Sqrt(dx * dx + dy * dy) / c;
                float ang = Mathf.Atan2(dy, dx);
                float rays = Mathf.Clamp01((0.95f - r) * 6f) * Mathf.Pow(Mathf.Abs(Mathf.Cos(ang * 4f)), 6f) * (r > 0.55f ? 1f : 0f);
                float disc = Mathf.Clamp01((0.5f - r) * c * 0.5f);
                float a = Mathf.Max(disc, rays * 0.9f);
                px[y * N + x] = new Color32(255, (byte)(200 + 40 * disc), (byte)(80 + 60 * disc), (byte)(a * 255f));
            }
            tex.SetPixels32(px); tex.Apply(false);
            s_sunSprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
            return s_sunSprite;
        }

        // The moon at a phase (0 = new, 0.5 = full): the lit part of a disc, terminator as an ellipse.
        private static readonly System.Collections.Generic.Dictionary<int, Sprite> s_moonSprites = new System.Collections.Generic.Dictionary<int, Sprite>();
        private static Sprite MoonSprite(float phase)
        {
            int key = Mathf.RoundToInt(phase * 31f);
            if (s_moonSprites.TryGetValue(key, out var cached)) return cached;
            const int N = 64; float c = (N - 1) * 0.5f;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave, name = "FiresMinimapMoon" };
            var px = new Color32[N * N];
            Color[] gal = GalaxiesMoonPixels(N);
            float k = Mathf.Cos(phase * 2f * Mathf.PI);   // 1 new .. -1 full
            bool waxing = phase < 0.5f;
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                float dx = (x - c) / c, dy = (y - c) / c, r = Mathf.Sqrt(dx * dx + dy * dy);
                float edge = Mathf.Clamp01((1f - r) * c);
                if (edge <= 0f) { px[y * N + x] = new Color32(0, 0, 0, 0); continue; }
                float half = Mathf.Sqrt(Mathf.Max(0f, 1f - dy * dy));
                float term = k * half;                          // terminator x
                bool lit = waxing ? dx > term : dx < -term;
                if (gal != null)
                {
                    // Galaxies' own moon texture (craters), lit side full, dark side faint
                    Color g = gal[y * N + x];
                    float m = lit ? 1f : 0.16f;
                    px[y * N + x] = new Color32((byte)(255f * g.r * m), (byte)(255f * g.g * m), (byte)(255f * g.b * m), (byte)(edge * (lit ? 255f : 150f)));
                    continue;
                }
                byte v = lit ? (byte)235 : (byte)70;
                px[y * N + x] = new Color32(v, v, (byte)Mathf.Min(255, v + 12), (byte)(edge * (lit ? 255f : 150f)));
            }
            tex.SetPixels32(px); tex.Apply(false);
            var sp = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
            s_moonSprites[key] = sp;
            return sp;
        }

        // a mini Galaxies moon. Galaxies.GalaxiesApi.GetMoonTexture (an add-only addition to the GalaxiesApi moon
        // contract) is read once by name, blitted down to N px (cheap, readable copy), and shaded per phase above. Absent = today's disc.
        private static Color[] s_galPixels; private static bool s_galTried;
        private static Color[] GalaxiesMoonPixels(int n)
        {
            if (s_galTried) return s_galPixels;
            s_galTried = true;
            try
            {
                Type api = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { api = asm.GetType("Galaxies.GalaxiesApi", false); if (api != null) break; }
                var m = api?.GetMethod("GetMoonTexture", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, Type.EmptyTypes, null);
                var src = m != null ? m.Invoke(null, null) as Texture : null;
                if (src == null) { ShellLog.Info("minimap moon: Core disc (" + (api == null ? "Galaxies not loaded" : m == null ? "GalaxiesApi has no GetMoonTexture" : "GetMoonTexture returned null") + ")"); return null; }
                var rt = RenderTexture.GetTemporary(n, n, 0, RenderTextureFormat.ARGB32);
                var prev = RenderTexture.active;
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var read = new Texture2D(n, n, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave };
                read.ReadPixels(new Rect(0, 0, n, n), 0, 0); read.Apply(false);
                RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
                s_galPixels = read.GetPixels();
                UnityEngine.Object.Destroy(read);
                ShellLog.Info($"minimap moon: Galaxies moon texture '{src.name}' ({src.width}x{src.height}) downsampled to {n}x{n}");
            }
            catch (Exception e) { ShellLog.Info("minimap moon: Core disc (Galaxies moon texture failed: " + e.Message + ")"); s_galPixels = null; }
            return s_galPixels;
        }

        // ---- night darker than day (the map image's tint) ------------------------------------------------
        private static Color s_mapColorWas = Color.white;
        private static bool s_tinted;
        private static void UpdateNightTint(Minimap mm)
        {
            var img = mm.m_mapImageSmall;
            if (img == null || EnvMan.instance == null) return;
            if (!s_tinted) { s_mapColorWas = img.color; s_tinted = true; }
            float f = EnvMan.instance.GetDayFraction();
            float daylight = Mathf.Clamp01(Mathf.Sin((f - 0.25f) * 2f * Mathf.PI) * 1.6f + 0.5f);   // ~1 by day, 0 deep night
            img.color = Color.Lerp(new Color(0.40f, 0.45f, 0.62f, s_mapColorWas.a), s_mapColorWas, daylight);
        }

        // ---- wind sock pill (behind the map's top-right) --------------------------------------------------
        private static RectTransform s_windPill, s_sock;
        private static KitLabel s_windText;
        private static string s_windSig;

        private static void BuildWindPill(float dia)
        {
            s_windPill = Widgets.Node(s_rect, "FiresMinimapWindPill");
            s_windPill.SetSiblingIndex(s_mask.GetSiblingIndex());   // behind the map
            s_windPill.anchorMin = s_windPill.anchorMax = new Vector2(0.5f, 0.5f); s_windPill.pivot = new Vector2(0f, 1f);
            var fill = Widgets.Box(s_windPill, "Fill", KitSprites.RoundedFill(FiresTokens.Radius), new Color(0.086f, 0.067f, 0.047f, 0.92f), true);
            Widgets.Stretch(fill.rectTransform, 0, 0, 0, 0); fill.raycastTarget = false;
            var edge = Widgets.Box(s_windPill, "Edge", KitSprites.RoundedOutline(FiresTokens.Radius), FiresTokens.Edge, true);
            Widgets.Stretch(edge.rectTransform, 0, 0, 0, 0); edge.raycastTarget = false;
            var sock = Widgets.Box(s_windPill, "Sock", SockSprite(), Color.white, false);
            s_sock = sock.rectTransform; sock.raycastTarget = false;
            s_windText = KitText.Create(s_windPill, "Text", "", FontRole.SansBold, 11f, FiresTokens.Text, KitAlign.Left, 0f, false, false);
            s_windDia = dia;
            LayoutWindPill(false);
        }

        // a compact tile just outside the rim at 45 deg (upper-right); when that leaves the screen, at 135 deg (upper-left).
        // Re-checked as the column moves.
        private static float s_windDia;
        private static bool s_windLeft;
        private static void LayoutWindPill(bool left)
        {
            s_windLeft = left;
            // a slim tile TUCKED against and slightly behind the copper rim, the label BESIDE the sock.
            // The tile sits behind the map (sibling order), so its inner end hides under the rim.
            const float w = 74f, h = 24f, sockW = 22f, sockH = 13f, tuck = 12f;
            float r = s_windDia * 0.5f;
            s_windPill.pivot = new Vector2(left ? 1f : 0f, 0.5f);
            s_windPill.sizeDelta = new Vector2(w, h);
            float inner = left ? 0f : tuck;   // the end under the rim carries no content
            s_sock.anchorMin = s_sock.anchorMax = new Vector2(0f, 0.5f); s_sock.pivot = new Vector2(0.5f, 0.5f);
            s_sock.anchoredPosition = new Vector2(inner + 4f + sockW * 0.5f, 0f); s_sock.sizeDelta = new Vector2(sockW, sockH);
            var tr = s_windText.Rect; tr.anchorMin = new Vector2(0f, 0f); tr.anchorMax = new Vector2(0f, 1f); tr.pivot = new Vector2(0f, 0.5f);
            tr.anchoredPosition = new Vector2(inner + 8f + sockW, 0f); tr.sizeDelta = new Vector2(w - tuck - sockW - 10f, 0f);
           Vector3 mapCentre = s_rect.InverseTransformPoint(s_mask.TransformPoint(s_mask.rect.center));
            Vector2 d = Dir(left ? 135f : 45f);
            float reach = r - tuck + 4f;      // the tile's rim-side end starts just inside the rim's outer edge
            s_windPill.localPosition = mapCentre + new Vector3(d.x * reach, d.y * reach, 0f);
        }

        private static readonly Vector3[] s_corners = new Vector3[4];
        private static void KeepWindPillOnScreen()
        {
            if (s_windPill == null) return;
            bool was = s_windLeft;   // compare with the side from the LAST check
            if (s_windLeft) LayoutWindPill(false);   // try the default side first each check
            s_windPill.GetWorldCorners(s_corners);
            var cam = s_windPill.GetComponentInParent<Canvas>()?.rootCanvas;
            bool overlay = cam == null || cam.renderMode == RenderMode.ScreenSpaceOverlay;
            float right = overlay ? s_corners[2].x : RectTransformUtility.WorldToScreenPoint(cam.worldCamera, s_corners[2]).x;
            bool wantLeft = right > Screen.width - 2f;
            if (wantLeft) LayoutWindPill(true);
            if (wantLeft != was) ShellLog.Debug("minimap wind: pill on the map's " + (wantLeft ? "upper-left (no room on the right)" : "upper-right"));
        }

        private static bool s_windRingLogged;

        private static void UpdateWind()
        {
            if (s_windPill == null || EnvMan.instance == null) return;
            Vector3 w = EnvMan.instance.GetWindDir();
            float strength = Mathf.Clamp01(EnvMan.instance.GetWindIntensity());
            float deg = Mathf.Atan2(w.z, w.x) * Mathf.Rad2Deg;      // map is north-up: +z up, +x right
            // Halo ring ([classes-ui] for [generator], lead 10-24): the map is FAT's unrolled strip there, so "north-up" means nothing;
            // the wind is projected through the map's own maths (WorldToMapPoint, which FAT remaps): its direction ON the strip.
            var ringPlayer = Player.m_localPlayer;
            if (ringPlayer != null && Minimap.instance != null && FiresCore.World.GravityUp.Active && FiresCore.World.GravityUp.InField(ringPlayer.transform.position))
            {
                Vector3 at = ringPlayer.transform.position;
                Minimap.instance.WorldToMapPoint(at, out float ax, out float ay);
                Minimap.instance.WorldToMapPoint(at + w.normalized * 50f, out float bx, out float by);
                if ((bx - ax) * (bx - ax) + (by - ay) * (by - ay) > 1e-10f)
                {
                    deg = Mathf.Atan2(by - ay, bx - ax) * Mathf.Rad2Deg;
                    if (!s_windRingLogged) { s_windRingLogged = true; ShellLog.Debug($"[Halo] minimap wind: projected onto the ring strip, {Mathf.Repeat(deg, 360f):0} deg on the map"); }
                }
            }
            float flap = 1f + 0.18f * strength * Mathf.Sin(Time.unscaledTime * (3f + 9f * strength));
            s_sock.localRotation = Quaternion.Euler(0f, 0f, deg + 180f);   // the sock's POINT faces the direction listed ("from")
            s_sock.localScale = new Vector3(0.65f + 0.35f * strength, flap, 1f);   // limp when calm, straight and flapping when strong
            int n = Mathf.RoundToInt(strength * 10f);
            string from = Compass(deg + 180f);                       // where it blows FROM, as weather reports say
            s_windText.Value = from + " " + n;
            string sig = from + n;
            if (sig != s_windSig) { s_windSig = sig; ShellLog.Debug($"minimap wind: dir {Mathf.Repeat(deg, 360f):0} deg (from {from}), strength {strength:0.00}"); }
        }

        private static string Compass(float mathDeg)
        {
            // math degrees (0 = east, 90 = north) to an 8-point compass
            string[] names = { "E", "NE", "N", "NW", "W", "SW", "S", "SE" };
            int i = Mathf.RoundToInt(Mathf.Repeat(mathDeg, 360f) / 45f) % 8;
            return names[i];
        }

        private static Sprite s_sockSprite;
        private static Sprite SockSprite()
        {
            if (s_sockSprite != null) return s_sockSprite;
            const int W = 64, H = 40;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave, name = "FiresMinimapSock" };
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                float t = x / (float)(W - 1);                         // 0 = mast end (wide) .. 1 = tail (narrow); points +x
                float halfH = Mathf.Lerp(0.46f, 0.16f, t) * H;
                float dy = Mathf.Abs(y - (H - 1) * 0.5f);
                bool inside = dy <= halfH && x > 2;
                bool mast = x <= 3;
                bool red = ((int)(t * 5f)) % 2 == 0;
                if (mast) px[y * W + x] = new Color32(200, 170, 110, 255);
                else if (inside) px[y * W + x] = red ? new Color32(220, 70, 50, 255) : new Color32(240, 236, 226, 255);
                else px[y * W + x] = new Color32(0, 0, 0, 0);
            }
            tex.SetPixels32(px); tex.Apply(false);
            s_sockSprite = Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 100f);
            return s_sockSprite;
        }

        private static float s_nextWindClamp;

        // ---- copper case -------------------------
        // The bronze armour's own albedo (vanilla's copper-coloured armour, ArmorBronzeChest) fills the ring through a mask, and a
        // light layer turns with the scene's sun as the camera sees it: bright on the side facing the light, a narrow specular band,
        // shadow opposite, dimmer at night. UI canvases have no real lighting, so this is the shading done by hand, not a lit mesh.
        private static RawImage s_copperTex;
        private static Image s_copperShade;
        private static bool s_copperTried;
        private static Texture s_copperCache;
        private static string s_copperSource = "";

        internal static void CopperRim(Image rim)
        {
            if (rim == null) return;
            var mask = rim.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = true;   // the brass colour stays as the base if the texture is missing
            Texture tex = CopperTexture();
            if (tex != null)
            {
                s_copperTex = Widgets.Node(rim.rectTransform, "Copper").gameObject.AddComponent<RawImage>();
                Widgets.Stretch(s_copperTex.rectTransform, 0, 0, 0, 0);
                s_copperTex.texture = tex;
                s_copperTex.uvRect = new Rect(0.1f, 0.1f, 0.35f, 0.35f);   // a patch of plates, not the whole atlas
                s_copperTex.color = CopperTint;
                s_copperTex.raycastTarget = false;
            }
            s_copperShade = Widgets.Box(rim.rectTransform, "Shade", ShadeSprite(), Color.white, false);
            Widgets.Stretch(s_copperShade.rectTransform, 0, 0, 0, 0);
            s_copperShade.raycastTarget = false;
            ShellLog.Info("minimap case: copper " + (tex != null ? "texture '" + tex.name + "' (" + tex.width + "x" + tex.height + ", from " + s_copperSource + ")" : "colour only (no armour texture found)") + " + sun-facing shading");
        }

        private static Texture CopperTexture()
        {
            if (s_copperCache != null || (s_copperTried && ObjectDB.instance == null)) return s_copperCache;
            s_copperTried = true;
            // (R-E13: 'starspark' 64x64 came from the item's particle renderer): the armour's own body texture first, i.e. the
            // m_armorMaterial vanilla paints onto the player (_ChestTex / _LegsTex), then only mesh renderers, never particles or sparks.
            foreach (var name in new[] { "ArmorBronzeChest", "ArmorBronzeLegs" })
            {
                var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(name) : null;
                var mat = prefab != null ? prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_armorMaterial : null;
                if (mat == null) continue;
                foreach (var prop in new[] { "_ChestTex", "_LegsTex", "_MainTex" })
                {
                    var t = mat.HasProperty(prop) ? mat.GetTexture(prop) : null;
                    if (t != null && t.width >= 256) { s_copperSource = name + " m_armorMaterial." + prop; return s_copperCache = t; }
                }
            }
            foreach (var name in new[] { "ArmorBronzeChest", "HelmetBronze", "ArmorBronzeLegs" })
            {
                var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(name) : null;
                if (prefab == null) continue;
                foreach (var rend in prefab.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(rend is MeshRenderer) && !(rend is SkinnedMeshRenderer)) continue;
                    var m = rend.sharedMaterial;
                    var t = m != null ? m.mainTexture : null;
                    if (t == null || t.width < 256 || t.name.IndexOf("spark", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    s_copperSource = name + " " + rend.GetType().Name + " '" + rend.name + "' mainTexture";
                    return s_copperCache = t;
                }
            }
            ShellLog.Warn("minimap case: NO bronze armour texture found (ArmorBronzeChest/Legs m_armorMaterial, mesh renderers) - the ring stays plain brass colour");
            return null;
        }

        private static float s_nextShadeLog;
        internal static readonly Color CopperTint = new Color(0xE3 / 255f, 0x91 / 255f, 0x52 / 255f, 1f);   // the v2b mock's bronze-armour tint #E39152
        private static void UpdateCopper()
        {
            if (s_copperShade == null) return;
            var sun = RenderSettings.sun;
            var cam = Camera.main;
            float daylight = 0.35f, deg = 135f;
            if (sun != null && cam != null)
            {
                Vector3 toSun = -sun.transform.forward;
                deg = Mathf.Atan2(Vector3.Dot(toSun, cam.transform.up), Vector3.Dot(toSun, cam.transform.right)) * Mathf.Rad2Deg;
                daylight = Mathf.Clamp01(sun.intensity) * Mathf.Clamp01(toSun.y * 3f + 0.4f);
            }
            s_copperShade.rectTransform.localRotation = Quaternion.Euler(0f, 0f, deg);   // the sprite's light side points +x
            s_copperShade.color = new Color(1f, 1f, 1f, Mathf.Lerp(0.55f, 1f, daylight));
            if (s_copperTex != null)
            {
                float v = Mathf.Lerp(0.55f, 1f, daylight);
                s_copperTex.color = new Color(CopperTint.r * v, CopperTint.g * v, CopperTint.b * v, 1f);
            }
            if (Time.unscaledTime >= s_nextShadeLog)
            {
                s_nextShadeLog = Time.unscaledTime + 30f;
                ShellLog.Info($"minimap case: light from {Mathf.Repeat(deg, 360f):0} deg on screen, daylight {daylight:0.00}");
            }
        }

        // light side at +x: a broad soft lit half, a narrow bright specular band, shadow on the far side
        private static Sprite s_shadeSprite;
        private static Sprite ShadeSprite()
        {
            if (s_shadeSprite != null) return s_shadeSprite;
            const int N = 128; float c = (N - 1) * 0.5f;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { hideFlags = HideFlags.DontSave, name = "FiresMinimapCaseShade", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                float dx = (x - c) / c, dy = (y - c) / c;
                float ang = Mathf.Atan2(dy, dx), facing = Mathf.Cos(ang);             // 1 toward the light, -1 away
                float spec = Mathf.Pow(Mathf.Max(0f, facing), 24f) * 0.75f;
                float lit = Mathf.Max(0f, facing) * 0.22f;
                float shade = Mathf.Max(0f, -facing) * 0.45f;
                if (spec + lit >= shade) { byte a = (byte)(255f * Mathf.Clamp01(spec + lit)); px[y * N + x] = new Color32(255, 236, 205, a); }
                else px[y * N + x] = new Color32(20, 10, 4, (byte)(255f * shade));
            }
            tex.SetPixels32(px); tex.Apply(false);
            s_shadeSprite = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
            return s_shadeSprite;
        }

        // ---- above the Esc menu --------------------------------------------------------------------------
        private static Canvas s_topCanvas, s_hudCanvas;
        private static bool s_aboveMenu;
        private static bool s_plateMovedHooked;

        // while the inventory is open its panels draw IN FRONT of every HUD element (minimap
        // column, plates, territory ...): a sorting override on InventoryGui.m_inventoryRoot above the HUD's top layer, removed on close.
        private static Canvas s_invCanvas; private static bool s_invTop;
        private static readonly System.Collections.Generic.Dictionary<Canvas, int> s_childOrig = new System.Collections.Generic.Dictionary<Canvas, int>();
        private static void UpdateInventoryLayer()
        {
            var ig = InventoryGui.instance;
            bool want = ig != null && InventoryGui.IsVisible() && ig.m_inventoryRoot != null;
            if (want == s_invTop && (s_invCanvas != null || !want)) return;
            s_invTop = want;
            if (ig == null || ig.m_inventoryRoot == null) return;
            var go = ig.m_inventoryRoot.gameObject;
            if (s_invCanvas == null)
            {
                s_invCanvas = go.GetComponent<Canvas>() ?? go.AddComponent<Canvas>();
                if (go.GetComponent<GraphicRaycaster>() == null) go.AddComponent<GraphicRaycaster>();   // a nested canvas needs its own raycaster
            }
            s_invCanvas.overrideSorting = want;
            if (want) s_invCanvas.sortingOrder = 30100;
            // a CHILD canvas with its own sorting (VAInventory panels, tooltips ...) would now sort
            // below the inventory root and be covered / un-clickable; each is lifted by the same 30100 while the override is on, then restored.
            foreach (var cc in go.GetComponentsInChildren<Canvas>(true))
            {
                if (cc == s_invCanvas || !cc.overrideSorting) continue;
                if (want) { if (!s_childOrig.ContainsKey(cc)) { s_childOrig[cc] = cc.sortingOrder; cc.sortingOrder += 30100; } }
            }
            if (!want)
            {
                foreach (var kv in s_childOrig) if (kv.Key != null) kv.Key.sortingOrder = kv.Value;
                s_childOrig.Clear();
            }
            ShellLog.Debug("inventory layer: " + (want ? "in front of the HUD (sorting 30100)" : "back to its normal order"));
        }

        private static void UpdateEscLayer()
        {
            // only the REAL Esc dialog (Menu.IsVisible is also true for any popup, the NPC book ...)
            bool menu = Menu.instance != null && Menu.instance.m_menuDialog != null && Menu.instance.m_menuDialog.gameObject.activeInHierarchy;
            // the HUD canvas has no raycaster, so the buttons only took clicks through the one
            // added here for the Esc menu. The minimap's own raycaster is now on whenever the CURSOR is free
            // (inventory, map, Esc), off while playing (no per-frame cost then); the sorting lift stays Esc-only.
            bool free = menu || Cursor.visible;
            if (menu == s_aboveMenu && free == s_clickable && (s_topCanvas != null || !free || s_rect == null)) return;   // + no minimap root = nothing to lift (was re-run every frame)
            s_aboveMenu = menu; s_clickable = free;
            // (public report: FPS 144 -> 40 after one Esc open/close, until relog; normal while the HUD is hidden / paused): the
            // Canvas + GraphicRaycaster added here on the first Esc stayed for the rest of the session (only their sorting was turned off),
            // splitting the HUD canvas and adding a per-frame raycaster over the map. What Core ADDED is now removed again on close.
            if (free)
            {
                if (s_topCanvas == null && s_rect != null)
                {
                    var go = s_rect.gameObject;
                    s_topCanvas = go.GetComponent<Canvas>();
                    if (s_topCanvas == null) { s_topCanvas = go.AddComponent<Canvas>(); s_topAdded = true; }
                    s_topRc = go.GetComponent<GraphicRaycaster>();
                    if (s_topRc == null) { s_topRc = go.AddComponent<GraphicRaycaster>(); s_topRcAdded = true; }
                }
                var gh = FiresCore.UI.GroupHud.GroupHudController.Instance;
                var ghRt = gh != null ? gh.RootRect : null;
                if (ghRt != null && s_hudCanvas == null)
                {
                    s_hudCanvas = ghRt.gameObject.GetComponent<Canvas>();
                    if (s_hudCanvas == null) { s_hudCanvas = ghRt.gameObject.AddComponent<Canvas>(); s_hudAdded = true; }
                }
                Apply(s_topCanvas, menu); Apply(s_hudCanvas, menu);
            }
            else
            {
                Apply(s_topCanvas, false); Apply(s_hudCanvas, false);
                // Destroy is
                // deferred, so the raycaster still existed when the Canvas went and the Canvas stayed (a split HUD canvas). The raycaster
                // goes first, at once; a Canvas that still carries someone else's raycaster is left in place with its sorting off.
                if (s_topRcAdded && s_topRc != null) UnityEngine.Object.DestroyImmediate(s_topRc);
                if (s_topAdded && s_topCanvas != null && s_topCanvas.GetComponent<GraphicRaycaster>() == null) UnityEngine.Object.Destroy(s_topCanvas);
                if (s_hudAdded && s_hudCanvas != null && s_hudCanvas.GetComponent<GraphicRaycaster>() == null) UnityEngine.Object.Destroy(s_hudCanvas);
                s_topCanvas = null; s_hudCanvas = null; s_topRc = null; s_topAdded = s_topRcAdded = s_hudAdded = false;
            }
            ShellLog.Info("minimap column: " + (menu ? "above the Esc menu (+/- clickable)" : free ? "clickable (cursor free: +/- zoom)" : "back in the HUD layer (click-only canvases removed)"));
        }
        private static GraphicRaycaster s_topRc; private static bool s_topAdded, s_topRcAdded, s_hudAdded, s_clickable;   // s_clickable

        private static void Apply(Canvas c, bool top)
        {
            if (c == null) return;
            c.overrideSorting = top;
            if (top) c.sortingOrder = 30000;
        }

        // ---- drag the column by the brass case (cursor free), saved per resolution ---------------------------
        private static Vector2 s_origAnchored;
        private static bool s_origSaved;
        private static string DragKey => "minimap@" + Screen.width + "x" + Screen.height;

        private static void AttachDrag(RectTransform handle)
        {
            if (!s_origSaved && s_rect != null) { s_origAnchored = s_rect.anchoredPosition; s_origSaved = true; }
            // (HUD layout part 2): the minimap column is a FiresDock HUD element (drag / scale / dock in layout mode, saved per
            // character + resolution); the brass-case drag is retired so there is ONE mover. The column children follow the map.
            if (s_rect != null)
                FiresShell.RegisterHudElement("hud.minimap", s_rect, new HudElementOptions { Label = "Minimap", Owner = "Core", Resizable = true });
        }

        // Core's other HUD elements (the vanilla status-effect area, the vanilla hotbar following the inventory grid's 1-8 row),
        // registered once their objects exist; positions + scales put back exactly on override OFF.
        private static readonly System.Collections.Generic.Dictionary<string, (RectTransform rt, Vector2 pos, Vector3 scale)> s_hudOrig =
            new System.Collections.Generic.Dictionary<string, (RectTransform, Vector2, Vector3)>();

        internal static bool s_groupHudDocked;
        private static Vector3 s_ghOrigWorld, s_ghOrigScale; private static RectTransform s_ghRt;   // the Group HUD left the column and is the "hud.grouphud" dock element

        private static void EnsureHudElements()
        {
            var hud = Hud.instance;
            if (hud == null) return;
            // register only in the world with the HUD shown. Registered while loading or
            // hidden (root offscreen), an element took that spot as its original and was clamped onto the screen.
            if (!FiresCore.UI.Dock.DockService.InWorld) return;
            // (FAD direct hop): forget registrations whose UI was destroyed, so they register again in the new world
            if (s_hudOrig.Count > 0) { var dead = new System.Collections.Generic.List<string>(); foreach (var kv in s_hudOrig) if (kv.Value.rt == null) dead.Add(kv.Key); foreach (var k in dead) s_hudOrig.Remove(k); }
            if (s_groupHudDocked && s_ghRt == null) s_groupHudDocked = false;
            ShellVanillaHud.Ensure();   // the vanilla HUD pieces (chat, build info, messages, ship ...)
            ShellHudProxies.Ensure();   // spawned pieces (boss bars, unlock toasts, biome banner) through slots
            // the time / territory plate is its own element, following the minimap (dragging it sets a new offset)
            if (!s_hudOrig.ContainsKey("hud.plates") && s_plate != null)
            {
                s_hudOrig["hud.plates"] = (s_plate, s_plate.anchoredPosition, s_plate.localScale);
                FiresCore.UI.Dock.DockService.Register(new FiresCore.UI.Dock.DockElementOptions
                {
                    Id = "hud.plates", Owner = "Core", Layer = "hud", Rect = s_plate, Label = "Time & territory", ShowGrip = true, FollowId = "hud.minimap",
                });
                // the minimap places the capsule (its default under the map + the
                // player's saved offset); the dock only drags it and reports the drop
                FiresCore.UI.Dock.DockService.MarkOwnerPlaced("hud.plates");
                if (!s_plateMovedHooked) { s_plateMovedHooked = true; FiresCore.UI.Dock.DockService.Moved += (id, r) => { if (id == "hud.plates") OnPlateMoved(); }; }
            }
            // the Group HUD becomes its own element once the column has
            // placed it: taken out of the column at that spot, it FOLLOWS hud.minimap (moves with the column, still draggable on its own).
            var gh = FiresCore.UI.GroupHud.GroupHudController.Instance;
            var ghRt = gh != null ? gh.RootRect : null;
            if (!s_groupHudDocked && ghRt != null && s_column.Exists(b => b.Id == "core.grouphud" && b.Placed))
            {
                var tl = FiresCore.UI.Dock.DockService.TopLeft(ghRt);
                var b0 = s_column.Find(b => b.Id == "core.grouphud");
                s_ghOrigWorld = b0.OrigPos; s_ghRt = ghRt; s_ghOrigScale = ghRt.localScale;   // its pre-column spot (world), put back on OFF
                s_groupHudDocked = true;
                // A territory HUD element sits between the daytime box and the Group HUD, so the Group HUD slides with it:
                // the Group HUD STAYS a column member (order 30,
                // last), so by default it sits centred under whatever the column shows and slides with the territory box. Like every column
                // member, a player's drag detaches it (DockService.DetachFromColumn, saved) and from then on its own spot is kept; a spot the
                // player had already saved before this version detaches it at once (DetachFromColumnIfSaved).
                FiresCore.UI.Dock.DockService.Register(new FiresCore.UI.Dock.DockElementOptions
                {
                    Id = "hud.grouphud", Owner = "Core", Layer = "hud", Rect = ghRt, Label = "Group", ShowGrip = true,   // no FollowId (a plain dock element: default once, the player's drag sticks)
                });
                FiresCore.UI.Dock.DockService.DetachFromColumnIfSaved("hud.grouphud");
                FiresCore.UI.GroupHud.GroupHudController.DockOwned = true;   // the dock is its only writer
                ShellLog.Info($"minimap column: the Group HUD is now the dock element hud.grouphud at {tl.x:0},{tl.y:0} (the dock is its only placer)");
            }
            if (!s_hudOrig.ContainsKey("hud.status") && hud.m_statusEffectListRoot is RectTransform st)
            {
                s_hudOrig["hud.status"] = (st, st.anchoredPosition, st.localScale);
                FiresShell.RegisterHudElement("hud.status", st, new HudElementOptions { Label = "Status effects", Owner = "Core", Resizable = true });
            }
            // The vanilla hotbar may be moved / resized in the layout tools, but nothing may force-hide it or
            // push it off screen: a plain player-movable dock element. No FollowId (it no longer chases vainv.grid.row1), no ShowInLayout,
            // never hidden; its spot is vanilla's default until the player saves one.
            if (!s_hudOrig.ContainsKey("hud.hotbar.vanilla"))
            {
                var bar = hud.GetComponentInChildren<HotkeyBar>(true);
                var rt = bar != null ? bar.transform as RectTransform : null;
                if (rt != null)
                {
                    s_hudOrig["hud.hotbar.vanilla"] = (rt, rt.anchoredPosition, rt.localScale);
                    FiresCore.UI.Dock.DockService.Register(new FiresCore.UI.Dock.DockElementOptions
                    {
                        Id = "hud.hotbar.vanilla", Owner = "Core", Layer = "hud", Rect = rt, Label = "Hotbar", ShowGrip = true,
                    });
                }
            }
        }

        private static void RestoreHudElements()
        {
            ShellVanillaHud.Restore();
            ShellHudProxies.Restore();
            FiresShell.UnregisterHudElement("hud.minimap");
            foreach (var kv in s_hudOrig)
            {
                FiresShell.UnregisterHudElement(kv.Key);
                if (kv.Value.rt != null) { kv.Value.rt.localScale = kv.Value.scale; kv.Value.rt.anchoredPosition = kv.Value.pos; }
            }
            s_hudOrig.Clear();
            if (s_groupHudDocked) { FiresShell.UnregisterHudElement("hud.grouphud"); FiresCore.UI.GroupHud.GroupHudController.DockOwned = false; if (s_ghRt != null) { s_ghRt.localScale = s_ghOrigScale; FiresCore.UI.GroupHud.GroupHudController.Instance?.ReloadPosition(); } }   // its own saved / default spot, not the world spot captured during loading
            s_groupHudDocked = false; s_ghRt = null;
        }

        internal static void SaveDrag()
        {
            if (s_rect == null) return;
            Vector2 off = s_rect.anchoredPosition - s_origAnchored;
            ShellWindows.Save(DragKey, new ShellWindows.WindowState { X = off.x, Y = off.y, W = 0f, H = 0f, Scale = 1f });
            ShellLog.Info($"minimap column: moved to {off.x:0},{off.y:0} (saved, res {Screen.width}x{Screen.height})");
        }

        // ---- per frame + restore ----------------------------------------------------------------------------
        private static void UpdateWatch(Minimap mm)
        {
            if (s_glass != null && !s_glassIsGlobe && Time.unscaledTime >= s_nextGlassTry) { s_nextGlassTry = Time.unscaledTime + 2f; TryGlobeGlass(); }
            UpdateClock();
            UpdateNightTint(mm);
            UpdateWind();
            UpdateEscLayer();
            UpdateInventoryLayer();
            UpdateCopper();
            if (Time.unscaledTime >= s_nextWindClamp) { s_nextWindClamp = Time.unscaledTime + 0.5f; KeepWindPillOnScreen(); SyncColumnBoxes(Menu.IsVisible()); EnsureHudElements(); }
            UpdateBlueprintPopup();
            UpdatePicker();
        }

        private static void RestoreWatch()
        {
            var mm = Minimap.instance;
            RestoreHudElements();   // unregister from the dock and put the vanilla HUD pieces back first
            if (s_tinted && mm != null && mm.m_mapImageSmall != null) mm.m_mapImageSmall.color = s_mapColorWas;
            s_tinted = false;
            // (for , round_20261022-070750:20416 "Can't remove Canvas because GraphicRaycaster
            // (Script) depends on it" right after "runtime destroyed (master OFF)"): this master-OFF path still had the pre-
            // order (a deferred Destroy of the raycaster, then the Canvas at once, refused) and also removed a Canvas it never added.
            // Same rule as the Unified OFF path above: our raycaster goes first, immediately; only a Canvas we added is destroyed,
            // and only when no other raycaster still needs it; otherwise its sorting is switched off.
            if (s_topCanvas != null || s_hudCanvas != null) { Apply(s_topCanvas, false); Apply(s_hudCanvas, false); }
            if (s_topRcAdded && s_topRc != null) UnityEngine.Object.DestroyImmediate(s_topRc);
            if (s_topAdded && s_topCanvas != null && s_topCanvas.GetComponent<GraphicRaycaster>() == null) UnityEngine.Object.Destroy(s_topCanvas);
            if (s_hudAdded && s_hudCanvas != null && s_hudCanvas.GetComponent<GraphicRaycaster>() == null) UnityEngine.Object.Destroy(s_hudCanvas);
            s_topCanvas = null; s_hudCanvas = null; s_topRc = null; s_topAdded = s_topRcAdded = s_hudAdded = false;
            s_aboveMenu = false;
            if (s_origSaved && s_rect != null) s_rect.anchoredPosition = s_origAnchored;
            s_origSaved = false;
            if (s_windPill != null) { UnityEngine.Object.Destroy(s_windPill.gameObject); s_windPill = null; s_sock = null; s_windText = null; }
            if (s_glassMat != null) { UnityEngine.Object.Destroy(s_glassMat); s_glassMat = null; }
            s_copperTex = null; s_copperShade = null; s_windLeft = false;   // destroyed with the art
            RestoreClicks();
            s_glass = null; s_glassIsGlobe = false; s_sun = null; s_moon = null; s_moonImg = null; s_moonBucket = -1; s_clockSig = null; s_windSig = null;
        }
    }

    // The brass case as a drag handle: only while the cursor is free (Esc menu, hub), moves the whole minimap root, so every plate and
    // registered column box follows; saved on release.
    internal sealed class MinimapColumnDrag : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler
    {
        internal RectTransform Target;
        private bool _dragging;
        public void OnBeginDrag(PointerEventData e) { _dragging = Cursor.visible && Target != null; }
        public void OnDrag(PointerEventData e)
        {
            if (!_dragging || Target == null) return;
            var canvas = Target.GetComponentInParent<Canvas>();
            float s = canvas != null && canvas.rootCanvas != null && canvas.rootCanvas.scaleFactor > 0f ? canvas.rootCanvas.scaleFactor : 1f;
            Target.anchoredPosition += e.delta / s;
            // keep it on screen
            Vector3[] c = new Vector3[4]; Target.GetWorldCorners(c);
            Vector3 fix = Vector3.zero;
            if (c[2].x > Screen.width) fix.x = Screen.width - c[2].x; if (c[0].x < 0f) fix.x = -c[0].x;
            if (c[1].y > Screen.height) fix.y = Screen.height - c[1].y; if (c[0].y < 0f) fix.y = -c[0].y;
            if (fix != Vector3.zero) Target.position += fix;
        }
        public void OnEndDrag(PointerEventData e) { if (_dragging) ShellMinimap.SaveDrag(); _dragging = false; }
    }

    // the data pill's click: left = "x,z,y", right = goto's "x z" (only reachable while the cursor is free)
    internal sealed class CoordsClick : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public void OnPointerEnter(PointerEventData e) => ShellMinimap.SetCopyIconHover(true);    // the copy icon shows on hover only
        public void OnPointerExit(PointerEventData e) => ShellMinimap.SetCopyIconHover(false);
        public void OnPointerClick(PointerEventData e)
        {
            ShellMinimap.DataPillClick(e);   // per line (coords / zone / biome)
        }
    }
}