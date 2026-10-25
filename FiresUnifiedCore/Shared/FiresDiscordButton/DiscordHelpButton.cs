// Shared source: the "Need help? Join our Discord" button under the vanilla pause menu's button list.
// Linked by path into Core (public, FIRES_DISCORD_PUBLIC defined, with the server-synced config) and into standalone Fires mods
// (internal, no config, always the built-in link). However many of our mods are loaded, ONE button appears: the first copy to
// Claim() owns it (Core claims in its Awake, so with Core installed it is always Core's); the others stay silent.
// The logo (va_logo.png, the Verdant's Ascent avatar) is an embedded resource of each assembly that links this file.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
#if FIRES_DISCORD_PUBLIC
using BepInEx.Configuration;
#endif

namespace FiresShared.DiscordHelp
{
#if FIRES_DISCORD_PUBLIC
    public
#else
    internal
#endif
    static class DiscordHelpButton
    {
        // RELEASE CHECKLIST: swap to the public invite before release. For now: the how-to forum.
        public const string BuiltInLink = "https://discord.com/channels/1375497033301102753/1557643894873919590";
        public const string Label = "Need help? Join our Discord";
        public const string PillText = "Verdant Ascent";
        private const string ObjectName = "FiresDiscordHelp";
        private const string OwnerKey = "FiresShared.DiscordHelp.Owner";
        private static readonly Color Blurple = new Color(0.345f, 0.396f, 0.949f); // Discord #5865F2

        private static string Me => typeof(DiscordHelpButton).Assembly.GetName().Name;

        /// <summary>First caller wins the button for its assembly. Core calls this in Awake; standalones on load.</summary>
        public static void Claim()
        {
            if (AppDomain.CurrentDomain.GetData(OwnerKey) is string) return;
            AppDomain.CurrentDomain.SetData(OwnerKey, Me);
        }

        private static bool IOwnIt() => (AppDomain.CurrentDomain.GetData(OwnerKey) as string) == Me;

#if FIRES_DISCORD_PUBLIC
        private static ConfigEntry<string> s_url;
        private static ConfigEntry<bool> s_show;

        /// <summary>Core only. "[Help] Discord invite URL" ships EMPTY; empty = the built-in link. Server-synced via BindToSync.</summary>
        public static void Initialize(ConfigFile config)
        {
            s_url = config.Bind("Help", "Discord invite URL", "",
                "The link the pause menu's \"Need help? Join our Discord\" button opens. Empty = the Verdant's Ascent help channel. "
                + "Server admins can point it at their own Discord; the server's value reaches its players.");
            s_show = config.Bind("Help", "Show Discord button", true, "Show the \"Need help? Join our Discord\" button in the pause menu.");
            Claim();
        }

        public static void BindToSync(FiresCore.Sync.ConfigSync configSync)
        {
            if (configSync == null || s_url == null) return;
            configSync.AddConfigEntry(s_url);
            configSync.AddConfigEntry(s_show);
        }

        private static string Link => string.IsNullOrWhiteSpace(s_url?.Value) ? BuiltInLink : s_url.Value.Trim();
        private static bool Shown => s_show == null || s_show.Value;
#else
        private static string Link => BuiltInLink;
        private static bool Shown => true;
#endif

        private static Sprite s_logo;
        private static Sprite Logo()
        {
            if (s_logo != null) return s_logo;
            try
            {
                Assembly asm = typeof(DiscordHelpButton).Assembly;
                string res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("va_logo.png", StringComparison.OrdinalIgnoreCase));
                if (res == null) return null;
                using (Stream s = asm.GetManifestResourceStream(res))
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                    if (!LoadImageReflect(tex, ms.ToArray())) return null;
                    s_logo = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                }
            }
            catch { s_logo = null; }
            return s_logo;
        }

        // ImageConversion.LoadImage by reflection: referencing UnityEngine.ImageConversionModule pulls netstandard 2.1 into the build.
        private static MethodInfo s_loadImage;
        private static bool LoadImageReflect(Texture2D tex, byte[] data)
        {
            if (s_loadImage == null)
            {
                Type t = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
                if (t == null) foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { t = a.GetType("UnityEngine.ImageConversion"); if (t != null) break; }
                s_loadImage = t?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) })
                              ?? t?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]) });
                if (s_loadImage == null) return false;
            }
            object r = s_loadImage.Invoke(null, s_loadImage.GetParameters().Length == 3 ? new object[] { tex, data, false } : new object[] { tex, data });
            return !(r is bool ok) || ok;
        }

        // The menu's background panel grows to hold the label + pill (same bottom padding as above) and goes back when hidden.
        private static RectTransform s_grownPanel;
        private static float s_grownBy;

        private static void RestorePanel()
        {
            if (s_grownPanel != null && s_grownBy != 0f) s_grownPanel.sizeDelta -= new Vector2(0f, s_grownBy);
            s_grownPanel = null; s_grownBy = 0f;
        }

        private static void Build(Menu menu)
        {
            if (menu == null || menu.m_settingsButton == null || !IOwnIt()) return;
            Transform list = menu.m_settingsButton.transform.parent;
            if (list == null) return;
            Transform existing = list.Find(ObjectName);
            if (!Shown)
            {
                if (existing != null) { UnityEngine.Object.Destroy(existing.gameObject); RestorePanel(); }
                return;
            }
            if (existing != null) return;   // fixed size: nothing to redo on later opens

            // ONE blue pill holds everything. Rounded #5865F2 with a thin gold edge: the round logo (42 px, white ring) on
            // the left, then "Need help?" (small, white) over "Join our Discord" (gold, larger). To always come out the same size and
            // shape, every size is a FIXED constant. Nothing is measured at runtime and there
            // are no layout groups inside the pill: each part sits in a fixed anchored rect, so the result can't depend on when or where
            // the menu opens (after a mod window, after login, after a resolution change). Text that doesn't fit shrinks inside its box
            // (a fixed auto-size range), never the box.
            TMP_Text vanillaText = menu.m_settingsButton.GetComponentInChildren<TMP_Text>(true);
            var root = new GameObject(ObjectName, typeof(RectTransform), typeof(LayoutElement));
            root.transform.SetParent(list, false);
            root.transform.SetAsLastSibling();
            var rootRt = (RectTransform)root.transform;
            // The fixed-size pill is
            // scaled ONCE by the vanilla menu button's height / PillH (measured from the Quit button), so it keeps its exact proportions
            // and lines up with the other options; the layout row shrinks with it
            float k = 1f;
            var refBtn = menu.m_quitButton != null ? menu.m_quitButton.transform as RectTransform : null;
            if (refBtn != null && refBtn.rect.height > 4f) k = Mathf.Clamp(refBtn.rect.height / PillH, 0.4f, 1f);
            rootRt.sizeDelta = new Vector2(RootW * k, RootH * k);
            var le = root.GetComponent<LayoutElement>();
            le.minWidth = RootW * k; le.preferredWidth = RootW * k; le.flexibleWidth = 0f;
            le.minHeight = RootH * k; le.preferredHeight = RootH * k; le.flexibleHeight = 0f;

            var pill = new GameObject("Pill", typeof(RectTransform), typeof(Image), typeof(Button), typeof(Outline));
            pill.transform.SetParent(root.transform, false);
            var pillRt = (RectTransform)pill.transform;
            pillRt.anchorMin = pillRt.anchorMax = new Vector2(0.5f, 1f); pillRt.pivot = new Vector2(0.5f, 1f);
            pillRt.sizeDelta = new Vector2(PillW, PillH);
            pillRt.localScale = new Vector3(k, k, 1f);   // the whole pill (logo + both lines) at the menu buttons' height
            pillRt.anchoredPosition = new Vector2(0f, -PillTop * k);
            var bg = pill.GetComponent<Image>();
            bg.sprite = Rounded(); bg.type = Image.Type.Sliced; bg.color = Blurple;
            var edge = pill.GetComponent<Outline>(); edge.effectColor = new Color(0.91f, 0.72f, 0.29f, 0.9f); edge.effectDistance = new Vector2(1.5f, -1.5f);
            var button = pill.GetComponent<Button>();
            button.targetGraphic = bg;
            var colors = button.colors; colors.highlightedColor = new Color(1.08f, 1.08f, 1.08f); colors.pressedColor = new Color(0.85f, 0.85f, 0.85f); button.colors = colors;
            button.onClick.AddListener(() => Application.OpenURL(Link));

            // The round logo with a white ring: a white circle (the ring) holding the logo masked to a slightly smaller circle.
            var ring = new GameObject("LogoRing", typeof(RectTransform), typeof(Image));
            ring.transform.SetParent(pill.transform, false);
            var ringRt = (RectTransform)ring.transform;
            ringRt.anchorMin = ringRt.anchorMax = new Vector2(0f, 0.5f); ringRt.pivot = new Vector2(0f, 0.5f);
            ringRt.sizeDelta = new Vector2(LogoSize, LogoSize); ringRt.anchoredPosition = new Vector2(PadLeft, 0f);
            var ringImg = ring.GetComponent<Image>(); ringImg.sprite = Circle(); ringImg.color = Color.white; ringImg.raycastTarget = false;
            var maskGo = new GameObject("LogoMask", typeof(RectTransform), typeof(Image), typeof(Mask));
            maskGo.transform.SetParent(ring.transform, false);
            var maskRt = (RectTransform)maskGo.transform; maskRt.anchorMin = Vector2.zero; maskRt.anchorMax = Vector2.one;
            maskRt.offsetMin = new Vector2(2.5f, 2.5f); maskRt.offsetMax = new Vector2(-2.5f, -2.5f);
            maskGo.GetComponent<Image>().sprite = Circle(); maskGo.GetComponent<Mask>().showMaskGraphic = false;
            Sprite logo = Logo();
            if (logo != null)
            {
                var icon = new GameObject("Logo", typeof(RectTransform), typeof(Image));
                icon.transform.SetParent(maskGo.transform, false);
                var rt = (RectTransform)icon.transform; rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                var img = icon.GetComponent<Image>(); img.sprite = logo; img.preserveAspect = true; img.raycastTarget = false;
            }

            // Two text lines, each in a fixed box to the right of the logo: offsets from the pill's top-left.
            float textLeft = PadLeft + LogoSize + Gap, textWidth = PillW - textLeft - PadRight;
            void Line(string name, string text, float top, float height, float size, float minSize, Color color, FontStyles style)
            {
                var go = new GameObject(name, typeof(RectTransform));
                // Without care TMP warns that no font asset is assigned to the labels:
                // TMP looks up its default font in OnEnable, i.e. inside AddComponent, before the font below was set, and
                // Valheim ships no LiberationSans. The label is built INACTIVE, gets the game's font, then turns on.
                go.SetActive(false);
                go.transform.SetParent(pill.transform, false);
                var rt = (RectTransform)go.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
                rt.sizeDelta = new Vector2(textWidth, height); rt.anchoredPosition = new Vector2(textLeft, -top);
                var t = go.AddComponent<TextMeshProUGUI>();
                t.font = GameFont(vanillaText);
                t.text = text; t.color = color; t.fontStyle = style; t.raycastTarget = false;
                t.enableWordWrapping = false; t.alignment = TextAlignmentOptions.MidlineLeft;
                t.enableAutoSizing = true; t.fontSizeMax = size; t.fontSizeMin = minSize; t.fontSize = size;
                t.overflowMode = TextOverflowModes.Ellipsis;
                go.SetActive(true);
            }
            Line("NeedHelp", "Need help?", SmallTop, SmallH, 18f, 12f, Color.white, FontStyles.Normal);
            Line("JoinDiscord", "Join our Discord", BigTop, BigH, 25f, 16f, new Color(0.91f, 0.72f, 0.29f), FontStyles.Bold);

            if (list.GetComponent<LayoutGroup>() == null && menu.m_quitButton != null)
            {
                var exit = (RectTransform)menu.m_quitButton.transform;
                rootRt.anchorMin = exit.anchorMin; rootRt.anchorMax = exit.anchorMax; rootRt.pivot = exit.pivot;
                rootRt.anchoredPosition = exit.anchoredPosition - new Vector2(0f, exit.rect.height * (1f - exit.pivot.y) + RootH * k * rootRt.pivot.y + 6f);
            }
            // 1.0's pause menu has no background panel behind the entries; the entries' layout grows with this child, so nothing
            // else is resized (s_grownBy stays 0).
            LayoutRebuilder.MarkLayoutForRebuild((RectTransform)list);
            menu.StartCoroutine(LogPlacementNextFrame(menu, list, rootRt));
        }

        // The fixed geometry, picked at the 1.0 menu scale. Pill 300 x 62; logo 42 at 14 px in;
        // texts start 12 px right of the logo and end 20 px before the pill's right edge.
        private const float PillW = 300f, PillH = 62f, PillTop = 14f, RootW = 320f, RootH = PillTop + PillH + 4f;
        private const float LogoSize = 42f, PadLeft = 14f, PadRight = 20f, Gap = 12f;
        private const float SmallTop = 6f, SmallH = 22f, BigTop = 27f, BigH = 30f;

        // A white rounded-rect sprite (9-sliced) and a white circle, made once at runtime; tinted by the Image colour.
        private static Sprite s_rounded, s_circle;
        // The menu button's own TMP font, else any font the game has loaded (its menus are TMP), else TMP's default.
        private static TMP_FontAsset GameFont(TMP_Text vanillaText)
        {
            if (vanillaText != null && vanillaText.font != null) return vanillaText.font;
            foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>()) if (f != null) return f;
            return TMP_Settings.defaultFontAsset;
        }

        private static Sprite Rounded()
        {
            if (s_rounded != null) return s_rounded;
            const int size = 64, r = 28;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Max(0f, Mathf.Max(r - x - 0.5f, x + 0.5f - (size - r))), dy = Mathf.Max(0f, Mathf.Max(r - y - 0.5f, y + 0.5f - (size - r)));
                    float a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply();
            s_rounded = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(r, r, r, r));
            return s_rounded;
        }

        private static Sprite Circle()
        {
            if (s_circle != null) return s_circle;
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float c = (size - 1) / 2f, rad = size / 2f - 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(rad - d + 0.5f)));
                }
            tex.Apply();
            s_circle = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            return s_circle;
        }

        // The placement line, written after the layout group has rebuilt (one frame later), so the rect is the real one. Logged once per session:
        // the pill is fixed, so later opens add nothing new.
        private static bool s_placementLogged;
        private static System.Collections.IEnumerator LogPlacementNextFrame(Menu menu, Transform list, RectTransform rootRt)
        {
            yield return null;
            if (rootRt == null) yield break;
            if (s_placementLogged) yield break;
            s_placementLogged = true;
            LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)list);
            Canvas.ForceUpdateCanvases();
            var panel = menu.m_menuDialog as RectTransform;
            Debug.Log($"[DiscordHelp] fixed pill {PillW:0}x{PillH:0} (logo {LogoSize:0}), added under '{(menu.m_quitButton != null ? menu.m_quitButton.name : "?")}' in '{list.name}' "
                + $"(layout {(list.GetComponent<LayoutGroup>() != null ? "group" : "manual")}): rect {rootRt.rect}, world {rootRt.position}; "
                + $"panel {(panel != null ? panel.rect.ToString() : "?")} grown by {s_grownBy:0}; link {Link}");
        }

        [HarmonyPatch(typeof(Menu), nameof(Menu.Show))]
        private static class MenuShow
        {
            private static void Postfix(Menu __instance)
            {
                try { Build(__instance); }
                catch (Exception e) { Debug.LogWarning("[DiscordHelp] could not add the pause-menu button: " + e.Message); }
            }
        }
    }
}
