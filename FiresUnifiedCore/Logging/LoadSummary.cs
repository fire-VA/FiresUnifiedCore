using System;
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
        {
            string emitTag = string.IsNullOrEmpty(tag) ? LogTag : tag;
            try
            {
                EmitTopBorder(title, emitTag);
                EmitBodyLines(lines ?? Array.Empty<string>(), emitTag);
                EmitBottomBorder(emitTag);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{emitTag} EmitMiniBox failed: {ex.Message}");
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

        // Console display width of a title. Grapheme clusters count one cell EXCEPT emoji
        // (astral-plane clusters like 📖/📦): the console font renders those TWO cells wide,
        // so counting them as one left the top border a dash longer than the bottom frame.
        private static int DisplayWidth(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            try
            {
                int width = 0;
                var elements = System.Globalization.StringInfo.GetTextElementEnumerator(s);
                while (elements.MoveNext())
                {
                    string element = (string)elements.Current;
                    width += char.IsSurrogatePair(element, 0) ? 2 : 1;
                }
                return width;
            }
            catch { return s.Length; }
        }

        private static void EmitTopBorder(string title, string tag)
        {
            string safeTitle = title ?? string.Empty;
            int titleSlotWidth = BoxInnerWidth - (TitleSidePadding * 2);
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

        private static void EmitBodyLines(string[] lines, string tag)
        {
            foreach (var raw in lines)
            {
                string content = raw ?? string.Empty;
                if (content.Length > BoxInnerWidth)
                    content = content.Substring(0, BoxInnerWidth);
                int pad = BoxInnerWidth - content.Length;
                EmitLine($"{tag} {VerticalRule} {content}{new string(SpaceChar, pad)} {VerticalRule}");
            }
        }

        // Body lines span 1 space + 28 content + 1 space between the verticals,
        // so the bottom rule is BoxInnerWidth + 2 — not +4, which overshot the
        // frame by two cells.
        private static void EmitBottomBorder(string tag)
        {
            string bottom =
                BottomLeftCorner
                + new string(HorizontalRule, BoxInnerWidth + TitleSidePadding)
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
