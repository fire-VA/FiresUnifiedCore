using System;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace FiresCore.Logging
{
    // Unicode mini-box renderer for end-of-init summaries. Consumers pass
    // a title and body lines; LoadSummary tags each emitted line so the
    // shared FiresLogColorPatch routes the box to its own console color.
    public static class LoadSummary
    {
        private const string LogTag = "[LoadSummary]";
        private const int BoxInnerWidth = 28;
        private const int TitleSidePadding = 2;
        private const char HorizontalRule = '─';
        private const char TopLeftCorner = '╭';
        private const char TopRightCorner = '╮';
        private const char BottomLeftCorner = '╰';
        private const char BottomRightCorner = '╯';
        private const char VerticalRule = '│';
        private const char SpaceChar = ' ';
        private const char EmojiPresentation = (char)0xFE0F;

        // BMP characters with Unicode Emoji_Presentation, drawn two cells wide without VS16 (inclusive pairs, ascending).
        private static readonly int[] WideBmpEmojiRanges =
        {
            0x231A, 0x231B, 0x23E9, 0x23EC, 0x23F0, 0x23F0, 0x23F3, 0x23F3, 0x25FD, 0x25FE, 0x2614, 0x2615,
            0x2648, 0x2653, 0x267F, 0x267F, 0x2693, 0x2693, 0x26A1, 0x26A1, 0x26AA, 0x26AB, 0x26BD, 0x26BE,
            0x26C4, 0x26C5, 0x26CE, 0x26CE, 0x26D4, 0x26D4, 0x26EA, 0x26EA, 0x26F2, 0x26F3, 0x26F5, 0x26F5,
            0x26FA, 0x26FA, 0x26FD, 0x26FD, 0x2705, 0x2705, 0x270A, 0x270B, 0x2728, 0x2728, 0x274C, 0x274C,
            0x274E, 0x274E, 0x2753, 0x2755, 0x2757, 0x2757, 0x2795, 0x2797, 0x27B0, 0x27B0, 0x27BF, 0x27BF,
            0x2B1B, 0x2B1C, 0x2B50, 0x2B50, 0x2B55, 0x2B55,
        };

        // How many console cells a title emoji takes. It cannot be measured at runtime - the terminal decides it from
        // whatever font it falls back to, long after the line has been written - so there is an override here for the
        // case where the rule below is wrong.
        //
        // The arithmetic a box depends on: a top border renders at innerWidth + 2 + (drawn - counted). It lines up
        // with the body only when the count matches what is drawn, so being wrong by one in either direction shows up
        // as a border that overhangs its corner or falls short of it.
        //
        // Default 0 = work it out per glyph, which is right on every console tested: a VS16 colour sequence or a BMP
        // Emoji_Presentation character advances two cells, a bare astral pictograph advances one. 1 or 2 forces every
        // emoji to that width for a terminal that does something else.
        private const int DefaultEmojiColumns = 0;
        private const string EmojiSection = "Logging";
        private const string EmojiColumnsKey = "BannerEmojiColumns";
        private const string EmojiColumnsDescription =
            "Console cells a banner title's emoji occupies. 0 works it out per glyph and is almost always right. "
            + "Set 1 or 2 to force it, if a box's top border does not line up with its sides: raise it when the "
            + "border falls short of the corner, lower it when it overhangs.";

        private static ConfigEntry<int> _emojiColumns;

        internal static int EmojiColumns => _emojiColumns != null ? _emojiColumns.Value : DefaultEmojiColumns;

        // Core binds this from its own config file at setup; nothing else should.
        internal static void BindConfig(ConfigFile config)
        {
            if (config == null) return;
            _emojiColumns = config.Bind(EmojiSection, EmojiColumnsKey, DefaultEmojiColumns,
                new ConfigDescription(EmojiColumnsDescription, new AcceptableValueRange<int>(0, 2)));
        }

        public static bool VerboseEnabled => FiresLogger.VerboseEnabled;

        // Generic emit — uses the bare [LoadSummary] tag. For per-mod
        // log coloring, prefer EmitMiniBox(title, lines, customTag) or
        // build a per-mod TaggedEmitter via For(modName).
        public static void EmitMiniBox(string title, string[] lines)
            => EmitMiniBox(title, lines, LogTag);

        // Tagged emit — consumer mods pass their own tag so FiresLogColorPatch
        // can route the box to a mod-specific console color. Typical tag:
        // "[FiresAdminTerrain] [LoadSummary]".
        public static void EmitMiniBox(string title, string[] lines, string tag)
            => EmitBox(title, lines, tag, BoxInnerWidth);

        // The same box at a caller-chosen inner width, for body lines longer than the mini box's columns.
        public static void EmitBox(string title, string[] lines, string tag, int innerWidth)
        {
            string emitTag = string.IsNullOrEmpty(tag) ? LogTag : tag;
            int width = Math.Max(innerWidth, BoxInnerWidth);
            try
            {
                EmitTopBorder(title, emitTag, width);
                EmitBodyLines(lines ?? Array.Empty<string>(), emitTag, width);
                EmitBottomBorder(emitTag, width);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{emitTag} EmitBox failed: {ex.Message}");
            }
        }

        // Per-mod emitter factory. Build once in the consumer's Setup,
        // call .EmitMiniBox(...) anywhere. Avoids re-formatting the tag
        // string on every emit call.
        public static TaggedEmitter For(string modName)
            => new TaggedEmitter(modName);

        public readonly struct TaggedEmitter
        {
            public string Tag { get; }

            public TaggedEmitter(string modName)
            {
                Tag = string.IsNullOrEmpty(modName)
                    ? LogTag
                    : $"[{modName}] {LogTag}";
            }

            public void EmitMiniBox(string title, string[] lines)
                => LoadSummary.EmitMiniBox(title, lines, Tag);
        }

        // Cells as the terminal draws them: controls and format characters none, an emoji EmojiColumns, anything else one.
        private static int DisplayWidth(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int width = 0;
            var elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext()) width += ElementWidth((string)elements.Current);
            return width;
        }

        private static int ElementWidth(string element)
        {
            char first = element[0];
            if (char.IsControl(first) || CharUnicodeInfo.GetUnicodeCategory(first) == UnicodeCategory.Format) return 0;
            // Two cells for an emoji the terminal is being asked to draw in colour - a VS16 sequence, or a BMP
            // character that carries Emoji_Presentation on its own. One for a bare astral pictograph, which advances
            // a single cell. WithEmojiPresentation puts VS16 on most box titles, so most land on the two-cell path.
            if (element.IndexOf(EmojiPresentation) >= 0 || IsWideBmpEmoji(first))
                return EmojiColumns > 0 ? EmojiColumns : 2;
            if (char.IsSurrogatePair(element, 0))
                return EmojiColumns > 0 ? EmojiColumns : 1;
            return 1;
        }

        private static bool IsWideBmpEmoji(char c)
        {
            for (int i = 0; i < WideBmpEmojiRanges.Length; i += 2)
            {
                if (c < WideBmpEmojiRanges[i]) return false;
                if (c <= WideBmpEmojiRanges[i + 1]) return true;
            }
            return false;
        }

        // Windows Terminal draws a text-style emoji one cell wide; VS16 asks for the two-cell colour glyph. Whether
        // the terminal honours that depends on the font it falls back to, which is why the width counted for one is a
        // setting rather than a constant - see EmojiColumns.
        private static string WithEmojiPresentation(string title)
        {
            if (title.Length < 2 || !char.IsSurrogatePair(title, 0)) return title;
            int codePoint = char.ConvertToUtf32(title, 0);
            if (codePoint < 0x1F300 || codePoint > 0x1FAFF) return title;
            if (title.Length > 2 && title[2] == EmojiPresentation) return title;
            return title.Insert(2, EmojiPresentation.ToString());
        }

        // The longest run of whole text elements that fits in maxWidth cells, and the cells it takes.
        private static string FitWidth(string text, int maxWidth, out int width)
        {
            width = 0;
            var elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
            {
                int next = ElementWidth((string)elements.Current);
                if (width + next > maxWidth) return text.Substring(0, elements.ElementIndex);
                width += next;
            }
            return text;
        }

        private static void EmitTopBorder(string title, string tag, int innerWidth)
        {
            string safeTitle = WithEmojiPresentation(title ?? string.Empty);
            int titleSlotWidth = innerWidth - (TitleSidePadding * 2);
            int decorationWidth = Math.Max(0, titleSlotWidth - DisplayWidth(safeTitle));
            int leftDecoration = decorationWidth / 2;
            int rightDecoration = decorationWidth - leftDecoration;

            string top =
                TopLeftCorner
                + new string(HorizontalRule, leftDecoration + TitleSidePadding)
                + SpaceChar + safeTitle + SpaceChar
                + new string(HorizontalRule, rightDecoration + TitleSidePadding)
                + TopRightCorner;

            EmitLine($"{tag} {top}");
        }

        private static void EmitBodyLines(string[] lines, string tag, int innerWidth)
        {
            foreach (var raw in lines)
            {
                string content = FitWidth(raw ?? string.Empty, innerWidth, out int width);
                EmitLine($"{tag} {VerticalRule} {content}{new string(SpaceChar, innerWidth - width)} {VerticalRule}");
            }
        }

        // Body lines span 1 space + the inner width + 1 space between the verticals,
        // so the bottom rule is innerWidth + 2 — not +4, which overshot the
        // frame by two cells.
        private static void EmitBottomBorder(string tag, int innerWidth)
        {
            string bottom =
                BottomLeftCorner
                + new string(HorizontalRule, innerWidth + TitleSidePadding)
                + BottomRightCorner;
            EmitLine($"{tag} {bottom}");
        }

        // Routes a summary line through Core's BepInEx log source instead of
        // Debug.Log, so the banner doesn't also stdout-echo a raw white duplicate
        // in the console. FiresLogColorPatch colours it by the Fires source name.
        // Falls back to Debug.Log before the source is wired (pre-Setup).
        private static void EmitLine(string fullLine)
        {
            if (FiresUnifiedCore.Log != null) FiresUnifiedCore.Log.LogInfo(fullLine);
            else Debug.Log(fullLine);
        }
    }
}
