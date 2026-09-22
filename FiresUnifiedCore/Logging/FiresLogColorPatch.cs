using System;
using System.Text.RegularExpressions;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace FiresCore.Logging
{
    // Harmony prefix on ConsoleLogListener.LogEvent that sends every console line through ConsoleOutput, so the
    // blocking console write happens off the main thread. Our mods' lines get per-class colours; everything else
    // keeps BepInEx's level colour, and BepInEx's own filters (displayed levels, Unity logs on or off) still apply.
    // Warnings, errors and fatal levels keep their vanilla colours so they stay visually distinct.
    //
    // Other Fires mods can ship a copy of this patch; the AppDomain-shared owner key makes exactly one of them write.
    // Core takes the key when it starts, so with Core installed its copy is the one that writes.
    [HarmonyPatch]
    internal static class FiresLogColorPatch
    {
        private const ConsoleColor DefaultColor = ConsoleColor.Magenta;
        private const string OwnerKey = "FiresColorPatch.Owner";
        private const string MyOwnerName = "FiresUnifiedCore";

        private static readonly (string keyword, ConsoleColor color)[] s_classColorRules =
        {
            ("Blueprint",      ConsoleColor.Cyan),
            ("Bake",           ConsoleColor.Cyan),
            ("Bundle",         ConsoleColor.Cyan),

            ("TileBrush",      ConsoleColor.Green),
            ("TileAnchor",     ConsoleColor.Green),
            ("TileMesh",       ConsoleColor.Green),
            ("TileHammer",     ConsoleColor.Green),
            ("Tile",           ConsoleColor.Green),
            ("Terrain",        ConsoleColor.Green),
            ("Heightmap",      ConsoleColor.Green),
            ("RegionScrubber", ConsoleColor.Green),

            ("WaterMesh",      ConsoleColor.DarkCyan),
            ("WaterDisk",      ConsoleColor.DarkCyan),
            ("WaterBrush",     ConsoleColor.DarkCyan),
            ("Water",          ConsoleColor.DarkCyan),
            ("Ocean",          ConsoleColor.DarkCyan),
            ("Liquid",         ConsoleColor.DarkCyan),
            ("Lava",           ConsoleColor.DarkCyan),
            ("Frozen",         ConsoleColor.DarkCyan),
            ("Snow",           ConsoleColor.DarkCyan),
            ("Ice",            ConsoleColor.DarkCyan),
            ("CustomRiver",    ConsoleColor.DarkCyan),
            ("CustomPond",     ConsoleColor.DarkCyan),

            ("Persistence",    ConsoleColor.DarkMagenta),
            ("Anchor",         ConsoleColor.DarkMagenta),
            ("ZDO",            ConsoleColor.DarkMagenta),
            ("Cache",          ConsoleColor.DarkMagenta),
            ("Codec",          ConsoleColor.DarkMagenta),
            ("Material",       ConsoleColor.DarkMagenta),

            ("RPC",            ConsoleColor.Blue),
            ("Network",        ConsoleColor.Blue),
            ("Ghetto",         ConsoleColor.Blue),
            ("Sync",           ConsoleColor.Blue),
            ("Server",         ConsoleColor.Blue),

            ("HammerScroll",   ConsoleColor.DarkYellow),
            ("HammerCategory", ConsoleColor.DarkYellow),
            ("HammerRight",    ConsoleColor.DarkYellow),
            ("Hammer",         ConsoleColor.DarkYellow),
            ("Tab",            ConsoleColor.DarkYellow),
            ("Category",       ConsoleColor.DarkYellow),
            ("VAPiece",        ConsoleColor.DarkYellow),
            ("Rebake",         ConsoleColor.DarkYellow),
            ("Icon",           ConsoleColor.DarkYellow),
            ("Piece",          ConsoleColor.DarkYellow),

            ("Spawn",          ConsoleColor.DarkGreen),
            ("Companion",      ConsoleColor.DarkGreen),
            ("NPC",            ConsoleColor.DarkGreen),
            ("Tameable",       ConsoleColor.DarkGreen),
            ("Fashion",        ConsoleColor.DarkGreen),
            ("Kennel",         ConsoleColor.DarkGreen),
            ("Mount",          ConsoleColor.DarkGreen),

            ("Guild",          ConsoleColor.DarkYellow),
            ("Wayshrine",      ConsoleColor.Cyan),
            ("Shrine",         ConsoleColor.Cyan),
            ("Portal",         ConsoleColor.Cyan),
            ("Mausoleum",      ConsoleColor.DarkMagenta),
            ("Memorial",       ConsoleColor.DarkMagenta),
            ("Ledger",         ConsoleColor.DarkMagenta),
            ("Dungeon",        ConsoleColor.DarkYellow),
            ("Lockpick",       ConsoleColor.DarkYellow),
            ("VAngarde",       ConsoleColor.DarkRed),
            ("Characters",     ConsoleColor.DarkRed),
            ("Discord",        ConsoleColor.Blue),
            ("Relay",          ConsoleColor.Blue),
            ("Heartbeat",      ConsoleColor.Blue),
            ("Shader",         ConsoleColor.DarkCyan),
            ("Aurora",         ConsoleColor.Cyan),
            ("Sky",            ConsoleColor.Cyan),
            ("Planet",         ConsoleColor.DarkMagenta),
            ("Moon",           ConsoleColor.Cyan),
            ("Galax",          ConsoleColor.DarkMagenta),
            ("Tide",           ConsoleColor.DarkCyan),
            ("Current",        ConsoleColor.DarkCyan),
            ("Diag",           ConsoleColor.Green),
            ("Probe",          ConsoleColor.Green),
            ("Corrupt",        ConsoleColor.Green),

            ("Inventory",      ConsoleColor.Yellow),
            ("Backpack",       ConsoleColor.Yellow),
            ("Container",      ConsoleColor.Yellow),

            ("Patch",          ConsoleColor.DarkGray),
            ("Compat",         ConsoleColor.DarkGray),
            ("Detector",       ConsoleColor.DarkGray),
            ("Distant",        ConsoleColor.DarkGray),
            ("StandIn",        ConsoleColor.DarkGray),
        };

        // Emoji-coded banner top-border rules. Banners flow as three log
        // emits per box (top / N body / bottom) all tagged [LoadSummary];
        // only the top border carries the emoji-prefixed title. The
        // category gets stashed in s_stickyBannerColor so subsequent body
        // + bottom lines inherit. Interleaved banners across mods would
        // mis-color the last few body lines — acceptable trade-off vs
        // threading explicit category through every EmitMiniBox call.
        private static readonly (string titleEmoji, ConsoleColor color)[] s_bannerTitleRules =
        {
            (StatusBanner.TitleEmoji, ConsoleColor.DarkCyan), // periodic status box
            ("📦", ConsoleColor.Cyan),         // bundles
            ("🧱", ConsoleColor.Cyan),         // baked
            ("📐", ConsoleColor.Cyan),         // blueprints
            ("🚀", ConsoleColor.Cyan),         // HD preloader / accelerated load
            ("🪨", ConsoleColor.Green),        // tile / terrain
            ("❄",  ConsoleColor.DarkCyan),     // snow / ice
            ("🌊", ConsoleColor.DarkCyan),     // water
            ("🔥", ConsoleColor.DarkRed),      // rebake / fire
            ("🔧", ConsoleColor.DarkGreen),    // commands / autotune
            ("⚙",  ConsoleColor.DarkGray),     // patches
            ("⚠",  ConsoleColor.DarkGray),     // vanilla noise
            ("🛑", ConsoleColor.DarkGray),     // missing scripts
            ("🎭", ConsoleColor.DarkGray),     // stand-ins
            ("📡", ConsoleColor.Blue),         // RPCs / RPC router
            ("🏛", ConsoleColor.Blue),         // server auth
            ("🔨", ConsoleColor.DarkYellow),   // hammer / placing
            ("🖼", ConsoleColor.DarkYellow),   // icons
            ("🎨", ConsoleColor.DarkMagenta),  // materials
            ("🐺", ConsoleColor.DarkGreen),    // companions
            ("🐴", ConsoleColor.DarkGreen),    // mounts
            ("⚔",  ConsoleColor.DarkYellow),   // guilds
            ("🏰", ConsoleColor.DarkYellow),   // dungeons (torchlit — off Red, which reads as a warning)
            ("🌀", ConsoleColor.Cyan),         // wayshrines / portals
            ("🌌", ConsoleColor.DarkMagenta),  // galaxies / celestial
            ("🛡",  ConsoleColor.DarkRed),      // vangarde / anti-cheat
            ("🎬", ConsoleColor.Gray),         // valcast / cameras
            ("♨",  ConsoleColor.Gray),         // steamy dumps
            ("⚰",  ConsoleColor.DarkMagenta),  // mausoleum
            ("📖", ConsoleColor.DarkYellow),   // quests / books / dialogue
            ("🐛", ConsoleColor.Green),        // debuggin tools
        };

        private const string LoadSummaryClassTag = "LoadSummary";
        private const double BannerStickySeconds = 2.0;

        private static ConsoleColor s_stickyBannerColor = DefaultColor;
        private static DateTime s_stickyBannerColorAt = DateTime.MinValue;

        private static readonly string[] s_ourModTags =
        {
            "[FiresUnifiedCore",
            "[FiresAdminPrefabs",
            "[FiresAdminTerrain",
            "[FiresGhetto",
            "[FiresNPCs",
            "[FiresRPGmaker",
            "[FiresValcast",
            "[FiresSteamyDumps",
            "[FiresEasyBakeMeshes",
            "[FiresDebugginTools",
            "[FiresDiscordIntegration",
            "[FiresCompanions",
            "[FiresGuilds",
            "[FiresMausoleum",
            "[FiresMounts",
            "[FiresDungeonMaster",
            "[FiresWayshrines",
            "[FiresVAngarde",
            "[FiresTossinShade",
            "[FiresValheimGalaxies",
            "[FiresHDPreloader",
            "[FiresHashFixer",
            "[FiresSteamworksPatcher",
            "[FiresWaterExtendedRing",
            "[FiresWater",
            "[FiresSkyboxAurora",
            "[FiresUnderwater",
            "[OceanZoneRing",
            "[VerdantsAscent",
            "[VAPiece",
            "[VAFAP",
            "[VAFAT",
            "[VAGhetto",
            "[Bake",
            "[Rebake",
            "[LoginFreeze",
        };

        private static readonly Regex s_classNameRx = new Regex(
            @"^\s*\[(?:Fires[A-Za-z0-9_:]*|VerdantsAscent[A-Za-z0-9_:]*|VA[A-Za-z0-9_:]*|OceanZoneRing|Bake|Rebake|LoginFreeze)\]\s*\[([^\]]+)\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Single-tag fallback when there's no `[Mod] [Class]` two-tag
        // shape — e.g. `[FiresWaterExtendedRing] Initial spawn: ...` or
        // `[OceanZoneRing] Cached N LOD water renderer(s)`. The single
        // bracketed tag IS the class, scanned against the keyword table.
        private static readonly Regex s_singleTagRx = new Regex(
            @"^\s*\[([^\]]+)\]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Func<ConsoleLogListener, bool> s_writeUnityLogs = BindWriteUnityLogs();
        private static readonly ConfigEntry<LogLevel> s_displayedLevels =
            AccessTools.Field(typeof(ConsoleLogListener), "ConfigConsoleDisplayedLevel")?.GetValue(null) as ConfigEntry<LogLevel>;

        // Called once from Core's setup: the writer starts, and Core's copy of this patch becomes the owner even if
        // another mod's copy claimed the key while it was loading first.
        internal static void TakeOwnership()
        {
            ConsoleOutput.Start();
            AppDomain.CurrentDomain.SetData(OwnerKey, MyOwnerName);
        }

        [HarmonyPatch(typeof(ConsoleLogListener), nameof(ConsoleLogListener.LogEvent))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool LogEvent_Prefix(ConsoleLogListener __instance, object sender, LogEventArgs eventArgs)
        {
            try
            {
                if (eventArgs == null || !ClaimOwnershipOrBail()) return true;
                if (!ConsoleOutput.Started || s_writeUnityLogs == null || s_displayedLevels == null) return true;
                if (!ConsoleShows(__instance, sender, eventArgs.Level)) return false;

                string message = eventArgs.Data?.ToString();
                ConsoleColor color = IsColouredFiresLine(eventArgs, message)
                    ? PickColorFromClassName(message)
                    : eventArgs.Level.GetConsoleColor();
                return !ConsoleOutput.Write(color, eventArgs.ToStringLine());
            }
            catch
            {
                return true;
            }
        }

        // The same filter BepInEx's ConsoleLogListener applies before it writes.
        private static bool ConsoleShows(ConsoleLogListener listener, object sender, LogLevel level)
        {
            if (sender is UnityLogSource && !s_writeUnityLogs(listener)) return false;
            return (level & s_displayedLevels.Value) != LogLevel.None;
        }

        private static bool IsColouredFiresLine(LogEventArgs eventArgs, string message)
        {
            if (string.IsNullOrEmpty(message) || ShouldKeepVanillaColor(eventArgs.Level)) return false;
            return IsOurModMessage(message) || IsOurModSource(eventArgs);
        }

        private static Func<ConsoleLogListener, bool> BindWriteUnityLogs()
        {
            var getter = AccessTools.PropertyGetter(typeof(ConsoleLogListener), "WriteUnityLogs");
            return getter != null ? AccessTools.MethodDelegate<Func<ConsoleLogListener, bool>>(getter) : null;
        }

        // Returns true when we are (or just became) the writer; false when
        // another Fires-* mod's copy of this patch already owns the line.
        private static bool ClaimOwnershipOrBail()
        {
            var owner = AppDomain.CurrentDomain.GetData(OwnerKey) as string;
            if (owner == null)
            {
                AppDomain.CurrentDomain.SetData(OwnerKey, MyOwnerName);
                return true;
            }
            return owner == MyOwnerName;
        }

        // internal so RateLimitedLogHandler shares the SAME tag set when deciding which Unity log lines are ours
        // (to write them straight to BepInEx instead of the native writer) — one source of truth for "is this a Fires mod line".
        internal static bool IsOurModMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            for (int i = 0; i < s_ourModTags.Length; i++)
            {
                if (message.IndexOf(s_ourModTags[i], StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        // A Fires mod emitting through its OWN BepInEx ManualLogSource (e.g. FAP's
        // summary banners) carries the mod name in the event SOURCE, not in the
        // message text — so colour those by source too. This is how a banner stays
        // coloured after being moved off Debug.Log (which avoids the stdout-echo
        // console duplicate).
        private static bool IsOurModSource(LogEventArgs eventArgs)
        {
            var name = eventArgs?.Source?.SourceName;
            return !string.IsNullOrEmpty(name) && name.StartsWith("Fires", StringComparison.Ordinal);
        }

        private static bool ShouldKeepVanillaColor(LogLevel level)
        {
            return level == LogLevel.Error
                || level == LogLevel.Fatal
                || level == LogLevel.Warning;
        }

        private static ConsoleColor PickColorFromClassName(string message)
        {
            var match = s_classNameRx.Match(message);
            string className;
            if (match.Success)
            {
                className = match.Groups[1].Value;
            }
            else
            {
                // Single-tag fallback. The first bracketed tag becomes
                // the class — covers `[FiresWaterExtendedRing] ...`,
                // `[OceanZoneRing] ...`, etc.
                var singleMatch = s_singleTagRx.Match(message);
                if (!singleMatch.Success) return DefaultColor;
                className = singleMatch.Groups[1].Value;
            }

            // Banners share the [LoadSummary] class tag — the category lives
            // in the title-bar emoji on the top-border line only. Detect on
            // the top border and stash; body/bottom lines inherit via the
            // sticky-color window.
            if (string.Equals(className, LoadSummaryClassTag, StringComparison.Ordinal))
                return PickBannerColor(message);

            for (int i = 0; i < s_classColorRules.Length; i++)
            {
                if (className.IndexOf(s_classColorRules[i].keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                    return s_classColorRules[i].color;
            }
            return DefaultColor;
        }

        private static ConsoleColor PickBannerColor(string message)
        {
            for (int i = 0; i < s_bannerTitleRules.Length; i++)
            {
                if (message.IndexOf(s_bannerTitleRules[i].titleEmoji, StringComparison.Ordinal) >= 0)
                {
                    var color = s_bannerTitleRules[i].color;
                    s_stickyBannerColor = color;
                    s_stickyBannerColorAt = DateTime.UtcNow;
                    return color;
                }
            }

            // No emoji on this line — likely a body / bottom-border line.
            // Reuse the most-recent banner's color if it's still fresh.
            if ((DateTime.UtcNow - s_stickyBannerColorAt).TotalSeconds < BannerStickySeconds)
                return s_stickyBannerColor;

            return DefaultColor;
        }

    }
}
