// Fires UI kit, step 0. The single source of the Fires UI look (FIRES_UI_STYLE_GUIDE.md S3 tokens, S5 motion,
// FIRES_UI_SHELL.md S10 parallax). Every other kit file reads these fields; no hex value lives anywhere else.
// Depends only on UnityEngine. Byte-identical copy in ValheimRip1.0/Assets/FiresUIKit/Runtime.
// ASCII-only: non-ASCII characters are backslash-u escapes in strings and ASCII words in comments. Core UI/Shell/Kit and
// ValheimRip1.0/Assets/FiresUIKit/Runtime hold byte-identical copies (check with cmp).
using UnityEngine;

namespace FiresCore.UI.Shell.Kit
{
    public enum FontRole { SerifRegular, SerifBold, SansRegular, SansBold }

    public static class FiresTokens
    {
        // ---- S3.1 colour ------------------------------------------------------------------------------
        public static readonly Color Backdrop    = Hex("#0B0907", 0.80f); // full-screen scrim
        public static readonly Color Panel       = Hex("#16110C", 0.97f); // window body = ClassesStyle.Page; near-solid: the world never competes with text
        public static readonly Color PanelRaised = Hex("#241A11");         // = ClassesStyle.Panel; cards, selected tab body
        public static readonly Color Well        = Hex("#1B140D");         // = ClassesStyle.Well; inputs, slots, list wells
        public static readonly Color Selected    = Hex("#3A2A12");         // selected card / row / node background
        public static readonly Color SlotFace    = Hex("#3A2A12");         // inventory/equipment slot face (reads on Panel; PanelRaised was ~8 RGB off it and the squares vanished)
        public static readonly Color Hairline    = Hex("#4A3824");         // = ClassesStyle.Line; row separators, slot borders
        public static readonly Color Edge        = Hex("#8A6538");         // window frame, inactive underline
        public static readonly Color Gold        = Hex("#F0B44C");         // = ClassesStyle.Gold; titles, section headers, active tab
        public static readonly Color GoldBright  = Hex("#FFD48A");         // = ClassesStyle.GoldHover; hover, selected, focus ring, badges
        public static readonly Color GoldDim     = Hex("#9C7A45");         // divider ends, inner frame line
        public static readonly Color Text        = Hex("#EFE2C4");         // body, values
        public static readonly Color TextMuted   = Hex("#B3A181");         // = ClassesStyle.Muted; labels, captions
        public static readonly Color TextFaint   = Hex("#8A7A60");         // v1 baseline (was #6F6352) key hints, disabled
        public static readonly Color TextFaintV2 = Hex("#988869");         // variant "footer": footer hints >= 4.5:1 incl. a star behind the panel (Widgets.FooterHintV2)
        public static readonly Color Ok          = Hex("#7FB069");         // ON, success
        public static readonly Color Warn        = Hex("#E0A040");         // soft warning
        public static readonly Color Danger      = Hex("#D2563F");         // real problems, destructive

        // Scrim feathering (guide "feathered edges"; sketch fires.css .scrim): alpha at centre / 60 % / corners.
        public const float ScrimAlphaCentre = 0.84f;
        public const float ScrimAlphaMid    = 0.88f;
        public const float ScrimAlphaEdge   = 0.94f;
        public const float ScrimMidRadius   = 0.60f;

        // Derived alphas used with the colours above (sketch values, no new colours).
        public const float SelectedTintAlpha  = 0.12f; // gold wash behind a selected list item (fades right)
        public const float LearnedFillAlpha   = 0.25f; // gold fill inside a learned talent diamond
        public const float FrameEdgeAlpha     = 0.85f; // the single Edge frame line
        public const float FrameShadowAlpha   = 0.45f; // soft inner shadow just inside the line
        public const float FrameShadowWidth   = 10f;
        public const float PillWellAlpha      = 0.70f; // pill background (Well)
        public const float WorldSaturation    = 0.80f; // mockup only: world image desaturation stand-in

        // ---- S3.2 type (sizes at 1080p, UI units) -------------------------------------------------------
        public const float TitleSize   = 52f;  public const float TitleTracking   = 14f;  // TMP characterSpacing = em/100
        public const float ContextSize = 15f;
        public const float SectionSize = 14f;  public const float SectionTracking = 10f;
        public const float TabSize     = 14f;  public const float TabTracking     = 14f; // tab rail in Section style
        public const float BodySize    = 18f;
        public const float LabelSize   = 18f;
        public const float ValueSize   = 17f;
        public const float HintSize    = 14f;
        public const float DescSize    = 14f;  // settings-row description, tooltip meta (sketch hub_settings)
        public const float PillTextSize = 13f;
        public const float SubTabSize  = 16f;
        public const float CloseSize   = 26f;
        public const float AutoSizeMinFraction = 0.80f; // auto-size min = 80 % of the size, then ellipsis

        // Font lookup names (the game's loaded Font objects, FIRES_UI_SHELL.md S6).
        public const string SerifRegularName = "AveriaSerifLibre-Regular";
        public const string SerifBoldName    = "AveriaSerifLibre-Bold";
        public const string SansRegularName  = "AveriaSansLibre-Regular";
        public const string SansBoldName     = "AveriaSansLibre-Bold";

        // Latin-1 glyphs (memory rule: no dingbat cross, star, arrow or check glyphs). Diamonds are sprites, never glyphs.
        public const string GlyphClose = "\u00D7";     // x
        public const string GlyphGo    = "\u00BB";     // >>
        public const string GlyphReset = "\u00AB";     // <<
        public const string GlyphSep   = " \u00B7 ";   // .
        public const string GlyphDot   = "\u00B7";

        // ---- S3.3 spacing and shape --------------------------------------------------------------------
        public static readonly float[] SpacingScale = { 4f, 8f, 12f, 16f, 24f, 32f, 48f };
        public const float S4 = 4f, S8 = 8f, S12 = 12f, S16 = 16f, S24 = 24f, S32 = 32f, S48 = 48f;
        public const float GroupGap = 36f;
        public const float RowGap = 8f;
        public const float WindowPadding = 32f;
        public const float Radius = 10f;              // panels, windows, popovers, modals (rounded kit; was 3 = buttons)
        public const float RadiusSmall = 6f;          // rows, section bars, inputs (Well), buttons, dropdowns, segments (= TooltipRadius family)
        public const float PillRadius = 15f;          // sub-view pills (ClassesStyle.PaintPill look)
        public const float FrameLine = 1f;            // outer Edge line
        public const float HeaderHeight = 150f;      // context strip + title + divider
        public const float ChamferSize = 12f;         // S12 optional 45 deg corner
        public const float RowHeight = 32f;
        public const float SettingsRowHeight = 36f;   // control row
        public const float SettingsRowTallHeight = 64f; // name + description + "Default: X" (sketch hub_settings)
        public static readonly Vector2 MinWindowSize = new Vector2(720f, 480f);
        public const float ContextStripHeight = 30f;
        public const float DividerWidth = 560f;
        public const float DiamondSize = 6f;          // ornament diamond
        public const float DividerDiamondSize = 8f;
        public const float ToggleDiamondSize = 12f;
        public const float SwitchWidth = 36f;         // pill switch track (DiamondToggle, rounded kit)
        public const float SwitchHeight = 20f;
        public const float SwitchKnob = 14f;
        public const float SegmentHeight = 30f;       // segmented control outer pill height
        public const float ItemCellIconFraction = 0.72f; // ItemCell icon size as a fraction of the cell
        // One hotbar look (Tools\golden\ui_overhaul\hotbars\PLAN.md). Add-only.
        public const float HotbarSlotSize = 64f;        // the vanilla element size (HUD scale / pitch match today)
        public const float HotbarSlotGap = 6f;          // pitch 70 = vanilla m_elementSpace
        public const float HotbarIconFraction = 0.72f;  // = ItemCellIconFraction
        public const float HotbarWellAlpha = 0.78f;     // the fill078 variant (the plan's baseline was 0.50)
        public const float HotbarKeySize = 12f;
        public const float HotbarCountSize = 13f;
        public const float HotbarCooldownSize = 20f;
        public const float HotbarPipSize = 7f;
        public const float BehindScale = 0.72f;         // the back bar: scaled about its bottom-centre
        public const float BehindAlpha = 0.70f;         // slightly more opaque than the mock's 0.55
        public const float BehindBottom = 74.1f;        // 64 x 0.72 + 10 + 18 above the container bottom
        public static readonly Color HotbarCooldownShade = WithAlpha(Hex("#0B0907"), 0.62f);
        public static readonly Color CooldownBlue = Hex("#AAAAFF");        public const float TabGap = 44f;
        public const float TabUnderline = 2f;
        public const float BadgeSize = 6f;
        public const float SubTabGap = 8f;
        public const float SliderTrack = 6f;          // pill track height, fully rounded ends (was 2 px hairline)
        public const float SliderKnob = 14f;          // round knob diameter (was 10 + 2)
        public const float TooltipPadding = 8f;
        public const float TooltipMaxWidth = 360f;
        public const float TooltipRadius = 6f;         // rounded tooltip corners (panels keep their own corner choice)
        public const float NodeIconSize = 44f;         // superseded by NodeLayout.Ring (kept: add-only)
        public const float NodeBlockPadding = 10f;     // superseded by NodeLayout.PadBottom (kept: add-only)
        public const float ToastWidth = 420f;
        public const float ToastEdge = 3f;
        public const float SelectedBar = 2f;          // left bar on a selected list item
        public const float FooterBottom = 20f;
        public const float CloseInset = 16f;

        // ---- S5 motion (seconds) -----------------------------------------------------------------------
        public const float OpenFade = 0.120f;
        public const float OpenScaleFrom = 0.98f;
        public const float CloseFade = 0.080f;
        public const float TabCrossFade = 0.090f;
        public const float HoverFade = 0.060f;
        public const float TooltipDelay = 0.350f;
        public const float ToastDuration = 3.0f;

        // ---- FIRES_UI_SHELL.md S10 parallax -----------------------------------------------------------
        public const int   ProceduralTextureSize = 256;
        // Galaxy backdrop (dark, slow, warm; procedural, generated once). Max luminance ~15-20 %.
        public static readonly Color GalaxyDeep   = Hex("#06050A");   // empty space
        public static readonly Color GalaxyIndigo = Hex("#15131D");   // cool nebula body (low saturation)
        public static readonly Color GalaxyIndigoRim = Hex("#272040"); // variant "margins" (BrightMargins): outer-margin nebula
        public static readonly Color GalaxyDustRim   = Hex("#46301B"); // outer-margin dust; max channel 27 %, rel. luminance < 4 %
        public static readonly Color GalaxyDust   = Hex("#2E2116");   // warm amber dust lanes
        public const int   GalaxyTextureSize = 512;
        public const float NebulaTile = 1600f;          // UI units one nebula tile covers (bilinear upscale = soft blur)
        public const float StarTile = 640f;
        public const float NebulaDrift = 0.5f;          // UI units per second (< 2 px/s at 1920)
        public const float StarDrift = 1.0f;
        public const float MoteDrift = 1.6f;
        public const float StarAlpha = 0.40f;           // (Text colour)
        public const float GalaxyShadeCentre = 0.80f;    // GalaxyDeep shade alpha behind the window (keeps it dark)
        public const float GalaxyShadeInner  = 0.42f;    // radius (0 centre, 1 corner) where the shade starts to fade
        public const float GalaxyShadeOuter  = 0.95f;    // radius where the margins show the full rim colour
        public const float HazeTravel = 4f;            // px at full mouse offset
        public const float DustTravel = 10f;
        public const float ParallaxEase = 0.25f;       // s
        public const float DriftPeriod = 40f;          // s per cycle
        public const float HazeAlpha = 0.03f;          // tint alpha over the scrim (Gold)
        public const float DustAlpha = 0.03f;          // (GoldBright)
        public const float HazeTile = 768f;            // UI units one noise tile covers
        public const float DustTile = 512f;
        public const float DustMinUiScale = 0.8f;
        public const float DustMinFps = 30f;

        // ---- helpers ------------------------------------------------------------------------------------
        public static Color WithAlpha(Color c, float a) { c.a = a; return c; }

        public static string RichHex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);

        static Color Hex(string hex, float alpha = 1f)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            c.a = alpha;
            return c;
        }
    }
}
