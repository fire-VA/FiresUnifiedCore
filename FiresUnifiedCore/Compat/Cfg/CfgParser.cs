using System;
using System.Collections.Generic;
using System.IO;

namespace FiresCore.Compat.Cfg
{
    // Shared skeleton parser for Marketplace's bespoke .cfg family (the Phase-2 drop-in foundation).
    //
    // There is NO single Marketplace serializer: every module's _Main_Server hand-parses its own
    // data-line grammar. What they ALL share is the skeleton transcribed here from
    // Trader_Main_Server.ProcessTraderProfiles (kg.Marketplace v9.8.1):
    //
    //   key = "default"; flag = false;
    //   foreach line:
    //     if (IsNullOrWhiteSpace || StartsWith("#")) skip;          // comments incl. "##"
    //     else if (StartsWith("[")):                                // section header
    //        key = line.Replace("[","").Replace("]","").Replace(" ","").ToLower();
    //        if (key.Split('=').Length == 2) { key = parts[0]; flag = ToBoolean(parts[1]); } else flag = false;
    //     else: data line -> module grammar, appended to current section.
    //   Folder read: Directory.GetFiles(folder,"*.cfg",AllDirectories) + File.ReadAllLines, merged.
    //
    // This type does ONLY the shared part. Per-module data-line grammar layers on CfgSection.Lines.
    // Faithful to MP's tolerances: brackets are STRIPPED, never regex-matched (~1.5% of real headers
    // are missing the closing ']'); parsing never throws (importers decide log-and-skip vs break).

    // One raw config line with provenance, for Marketplace-style "in {file}, line: {n}" diagnostics.
    public readonly struct CfgLine
    {
        public readonly string Text;
        public readonly string File;
        public readonly int Number;   // 1-based, within its source file

        public CfgLine(string text, string file, int number) { Text = text; File = file; Number = number; }
        public override string ToString() => Text;
    }

    // A [section] header and the data lines beneath it, in file order. RawHeader is bracket-stripped
    // (strip-if-present) with spaces/case preserved; per-module key/flag interpretation is the importer's.
    public sealed class CfgSection
    {
        public string RawHeader;
        public CfgLine HeaderLine;
        public readonly List<CfgLine> Lines = new List<CfgLine>();

        public CfgSection(string rawHeader, CfgLine headerLine) { RawHeader = rawHeader; HeaderLine = headerLine; }

        // Marketplace's common section key: spaces stripped, lowercased, part before the first '='
        // (only when the header splits into exactly 2 on '=', matching MP). Most modules key this way;
        // use RawHeader for the exceptions (Territory '@', PlayersTag ':', Banker/Teleporter no-split).
        public string Key { get { string k, f; CfgHeader.SplitEq(RawHeader, out k, out f); return k; } }

        // Raw token after the first '=' (Trader NeedToKnow bool / Gambler MAXROLLS int), or null.
        public string EqFlag { get { string k, f; CfgHeader.SplitEq(RawHeader, out k, out f); return f; } }
        // Raw token after the first '@' (Territory priority), or null.
        public string AtFlag => CfgHeader.AfterFirst(RawHeader, '@');
        // Raw token after the first ':' (PlayersTag), or null.
        public string ColonFlag => CfgHeader.AfterFirst(RawHeader, ':');
    }

    // The parsed skeleton of a Marketplace config (one file, or a merged folder).
    public sealed class CfgDocument
    {
        public readonly List<CfgSection> Sections = new List<CfgSection>();
        public readonly List<string> Warnings = new List<string>();

        // First section whose common Key equals key (Marketplace-normalized).
        public CfgSection Find(string key)
        {
            string k = Normalize(key);
            foreach (var s in Sections) if (s.Key == k) return s;
            return null;
        }

        // All sections sharing the common key (Marketplace merges same-key sections across files).
        public List<CfgSection> FindAll(string key)
        {
            string k = Normalize(key);
            var result = new List<CfgSection>();
            foreach (var s in Sections) if (s.Key == k) result.Add(s);
            return result;
        }

        private static string Normalize(string key) => (key ?? "").Replace(" ", "").ToLowerInvariant();
    }

    public static class CfgParser
    {
        // Read + merge every *.cfg under folder (recursive). Each file is parsed independently (the
        // section cursor resets per file, as MP does), then all sections are concatenated in file order.
        // Missing folder -> empty document (no throw).
        public static CfgDocument ParseFolder(string folder)
        {
            var doc = new CfgDocument();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return doc;
            foreach (var file in Directory.GetFiles(folder, "*.cfg", SearchOption.AllDirectories))
            {
                var fileDoc = ParseFile(file);
                doc.Sections.AddRange(fileDoc.Sections);
                doc.Warnings.AddRange(fileDoc.Warnings);
            }
            return doc;
        }

        // Parse a single .cfg file. Unreadable file -> empty document with a warning (no throw).
        public static CfgDocument ParseFile(string path)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (Exception ex)
            {
                var d = new CfgDocument();
                d.Warnings.Add("Can't read " + path + ": " + ex.Message);
                return d;
            }
            return ParseLines(Enumerate(lines, path));
        }

        // Parse already-read text as if it were a file named 'file'.
        public static CfgDocument ParseText(string text, string file = "<text>")
        {
            var lines = (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
            return ParseLines(Enumerate(lines, file));
        }

        // Core skeleton loop. Skips null/whitespace and '#'-prefixed lines; '[' opens a section; anything
        // else is a data line under the current section. Pre-header data lines collect under an implicit
        // "default" section (matching MP's initial key="default").
        public static CfgDocument ParseLines(IEnumerable<CfgLine> lines)
        {
            var doc = new CfgDocument();
            CfgSection current = null;
            foreach (var line in lines)
            {
                var text = line.Text;
                if (string.IsNullOrWhiteSpace(text) || text.StartsWith("#")) continue;
                if (text.StartsWith("["))
                {
                    current = new CfgSection(CfgHeader.StripBrackets(text), line);
                    doc.Sections.Add(current);
                }
                else
                {
                    if (current == null)
                    {
                        current = new CfgSection("default", line);
                        doc.Sections.Add(current);
                    }
                    current.Lines.Add(line);
                }
            }
            return doc;
        }

        private static IEnumerable<CfgLine> Enumerate(string[] lines, string file)
        {
            for (int i = 0; i < lines.Length; i++) yield return new CfgLine(lines[i], file, i + 1);
        }
    }

    // Header-token helpers matching Marketplace's bracket/space/case handling.
    public static class CfgHeader
    {
        // Remove '[' and ']' anywhere (strip-if-present) — never a regex; real headers are frequently
        // missing the closing ']'.
        public static string StripBrackets(string s) => (s ?? "").Replace("[", "").Replace("]", "");

        // Marketplace's '=' header split: Replace(" ","").ToLower(), then split on '='. Only when it
        // splits into EXACTLY two does it become (key, flag); otherwise the whole string is the key and
        // there is no flag. Input is the bracket-stripped header.
        public static void SplitEq(string rawHeader, out string key, out string flag)
        {
            string s = (rawHeader ?? "").Replace(" ", "").ToLowerInvariant();
            string[] parts = s.Split('=');
            if (parts.Length == 2) { key = parts[0]; flag = parts[1]; }
            else { key = s; flag = null; }
        }

        // Substring after the first occurrence of delim, or null if absent.
        public static string AfterFirst(string rawHeader, char delim)
        {
            if (string.IsNullOrEmpty(rawHeader)) return null;
            int i = rawHeader.IndexOf(delim);
            return (i >= 0 && i + 1 <= rawHeader.Length) ? rawHeader.Substring(i + 1) : null;
        }
    }

    // Data-line tokenizers matching the dominant Marketplace patterns.
    public static class CfgTokens
    {
        // Marketplace's dominant data-line split: strip ALL spaces, then split on ','.
        public static string[] NoSpaceCsv(string line) => (line ?? "").Replace(" ", "").Split(',');

        // Strip ALL spaces, split on '=' (multi-item "need=result" trades, etc.).
        public static string[] NoSpaceEq(string line) => (line ?? "").Replace(" ", "").Split('=');

        // Split on ',' WITHOUT stripping spaces (Teleporter pin names, ServerInfo text).
        public static string[] Csv(string line) => (line ?? "").Split(',');
    }
}
