using System;
using System.Text;

namespace FiresCore.Logging
{
    // The BepInEx console is 100 columns. Every line it prints already spends some of them on its own source prefix -
    // "[Info   : Unity Log] " for anything routed through Debug.Log, "[Message:FiresEasyBakeMeshes] " for a mod's own
    // ManualLogSource - before the mod writes a character. A summary line written to fit 100 columns therefore does
    // not, and the console folds it mid-word onto the next row.
    //
    // Fit() folds it here instead: at word boundaries, with the continuation rows indented so the wrap reads as one
    // entry. The first row keeps the mod's bracket tag, so console colouring, the suppression patches and any grep
    // still key off it. One string with embedded newlines is returned rather than several lines, because BepInEx
    // prints its prefix once per call - splitting into separate calls would spend the prefix again on every row.
    public static class ConsoleWrap
    {
        public const int ConsoleColumns = 100;

        // "[Info   : Unity Log] " - what BepInEx puts in front of every Debug.Log line.
        public const int UnityLogPrefixColumns = 21;

        private const string ContinuationIndent = "    ";

        // Never fold to less than this, so a caller passing a huge prefix gets an ugly line rather than one character
        // per row or an infinite loop.
        private const int MinRoom = 16;

        // prefixColumns is what the log sink prints before this text. For Debug.Log pass UnityLogPrefixColumns; for a
        // ManualLogSource pass PrefixColumnsFor(sourceName, level).
        public static string Fit(string text, int prefixColumns)
        {
            if (string.IsNullOrEmpty(text)) return text;

            int room = Math.Max(MinRoom, ConsoleColumns - prefixColumns);
            if (text.Length <= room && text.IndexOf('\n') < 0) return text;

            var built = new StringBuilder(text.Length + 16);
            foreach (string paragraph in text.Split('\n'))
            {
                if (built.Length > 0) built.Append('\n');
                AppendFolded(built, paragraph, room);
            }
            return built.ToString();
        }

        // Width of a BepInEx ManualLogSource prefix: "[Message:FiresEasyBakeMeshes] ". The level is padded to 7 so the
        // brackets line up, which is why this is not just the source name's length.
        public static int PrefixColumnsFor(string sourceName)
            => 1 + 7 + 1 + (sourceName?.Length ?? 0) + 2;

        private static void AppendFolded(StringBuilder into, string text, int room)
        {
            // A row that is already indented is part of a structure - FiresDebugginTools prints its stall breakdown as
            // an indented call tree - so its continuation rows keep that indentation and step in once more. Folding to
            // a flat margin would read as a new branch rather than the same one continuing.
            int leadingSpaces = 0;
            while (leadingSpaces < text.Length && text[leadingSpaces] == ' ') leadingSpaces++;
            string continuation = leadingSpaces > 0
                ? new string(' ', leadingSpaces) + ContinuationIndent
                : ContinuationIndent;

            string indent = string.Empty;
            while (text.Length > room)
            {
                int cut = text.LastIndexOf(' ', Math.Min(room, text.Length - 1));
                if (cut <= leadingSpaces) cut = room;
                into.Append(indent).Append(text, 0, cut).Append('\n');
                text = text.Substring(cut).TrimStart();
                indent = continuation;
                room = Math.Max(MinRoom, ConsoleColumns - continuation.Length);
            }
            into.Append(indent).Append(text);
        }
    }
}
