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

        private static void EmitTopBorder(string title, string tag)
        {
            string safeTitle = title ?? string.Empty;
            int titleSlotWidth = BoxInnerWidth - (TitleSidePadding * 2);
            int decorationWidth = Math.Max(0, titleSlotWidth - safeTitle.Length);
            int leftDecoration = decorationWidth / 2;
            int rightDecoration = decorationWidth - leftDecoration;

            string top =
                TopLeftCorner
                + new string(HorizontalRule, leftDecoration + TitleSidePadding)
                + SpaceChar + safeTitle + SpaceChar
                + new string(HorizontalRule, rightDecoration + TitleSidePadding)
                + TopRightCorner;
            Debug.Log($"{tag} {top}");
        }

        private static void EmitBodyLines(string[] lines, string tag)
        {
            foreach (var raw in lines)
            {
                string content = raw ?? string.Empty;
                if (content.Length > BoxInnerWidth)
                    content = content.Substring(0, BoxInnerWidth);
                int pad = BoxInnerWidth - content.Length;
                Debug.Log($"{tag} {VerticalRule} {content}{new string(SpaceChar, pad)} {VerticalRule}");
            }
        }

        private static void EmitBottomBorder(string tag)
        {
            string bottom =
                BottomLeftCorner
                + new string(HorizontalRule, BoxInnerWidth + (TitleSidePadding * 2))
                + BottomRightCorner;
            Debug.Log($"{tag} {bottom}");
        }
    }
}
