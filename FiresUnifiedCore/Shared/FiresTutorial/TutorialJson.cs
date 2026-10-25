using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FiresTutorial
{
    // The cross-mod form of a TutorialBook: plain JSON text plus callbacks keyed by "chapterId/stepId", so two compiled copies of
    // this source (whose internal types are different types) can hand chapters to each other with only System types.
    //   { "format": 1, "id", "title", "logTag", "resultsFile", "version",
    //     "chapters": [ { "id", "title", "intro", "steps": [ { "id", "title", "text", "watch", "hint" } ] } ] }
    // The live parts travel as callbacks: done(stepKey) -> bool, enter(stepKey), key(stepKey) -> the key text to show now.
    // enter("!start") / enter("!stop") run the owner's OnStart / OnStop when the host's manager starts or stops the book.
    internal static class TutorialJson
    {
        public const int Format = 1;

        public static string StepKey(TutorialChapter c, TutorialStep s) => c.Id + "/" + s.Id;

        public static string ToJson(TutorialBook b)
        {
            var sb = new StringBuilder();
            sb.Append("{\"format\":").Append(Format);
            Field(sb, "id", b.Id); Field(sb, "title", b.Title); Field(sb, "logTag", b.LogTag); Field(sb, "resultsFile", b.ResultsFile); Field(sb, "version", b.Version);
            sb.Append(",\"chapters\":[");
            for (int c = 0; c < b.Chapters.Count; c++)
            {
                var ch = b.Chapters[c];
                if (c > 0) sb.Append(',');
                sb.Append("{\"id\":").Append(Str(ch.Id)); Field(sb, "title", ch.Title); Field(sb, "intro", ch.Intro);
                sb.Append(",\"steps\":[");
                for (int i = 0; i < ch.Steps.Count; i++)
                {
                    var s = ch.Steps[i];
                    if (i > 0) sb.Append(',');
                    string hint = null; try { hint = s.Hint?.Invoke(); } catch { }
                    sb.Append("{\"id\":").Append(Str(s.Id)); Field(sb, "title", s.Title); Field(sb, "text", s.Text); Field(sb, "watch", s.Watch); Field(sb, "hint", hint ?? "");
                    sb.Append('}');
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // Null when the text isn't a book this copy understands.
        public static TutorialBook FromJson(string json, Func<string, bool> done, Action<string> enter, Func<string, string> key)
        {
            if (!(Parse(json) is Dictionary<string, object> root)) return null;
            if (!(root.TryGetValue("format", out var f) && f is double d && (int)d == Format)) return null;
            var b = new TutorialBook { Id = S(root, "id"), Title = S(root, "title"), LogTag = S(root, "logTag"), ResultsFile = S(root, "resultsFile"), Version = S(root, "version") };
            // The owner's own start / stop (turn modules on, restore the camera) cross as the reserved keys "!start" / "!stop".
            if (enter != null) { b.OnStart = () => enter(StartKey); b.OnStop = () => enter(StopKey); }
            if (b.Id.Length == 0 || !(root.TryGetValue("chapters", out var chs) && chs is List<object> chapters)) return null;
            foreach (var co in chapters)
            {
                if (!(co is Dictionary<string, object> cd)) continue;
                var ch = new TutorialChapter { Id = S(cd, "id"), Title = S(cd, "title"), Intro = S(cd, "intro") };
                if (cd.TryGetValue("steps", out var sts) && sts is List<object> steps)
                    foreach (var so in steps)
                    {
                        if (!(so is Dictionary<string, object> sd)) continue;
                        string stepKey = ch.Id + "/" + S(sd, "id"), hint = S(sd, "hint");
                        ch.Steps.Add(new TutorialStep
                        {
                            Id = S(sd, "id"), Title = S(sd, "title"), Text = S(sd, "text"), Watch = S(sd, "watch"),
                            Hint = hint.Length > 0 ? (Func<string>)(() => hint) : null,
                            Done = () => done != null && done(stepKey),
                            Enter = enter == null ? (Action)null : () => enter(stepKey),
                            Key = key == null ? (Func<string>)null : () => key(stepKey),
                        });
                    }
                if (ch.Steps.Count > 0) b.Chapters.Add(ch);
            }
            return b.Chapters.Count > 0 ? b : null;
        }

        // The owner's side of the callbacks: answers for its own book by "chapterId/stepId".
        public static bool Done(TutorialBook b, string stepKey) { var s = Find(b, stepKey); try { return s?.Done != null && s.Done(); } catch { return false; } }
        public const string StartKey = "!start", StopKey = "!stop";
        public static void Enter(TutorialBook b, string stepKey)
        {
            try
            {
                if (stepKey == StartKey) { b?.OnStart?.Invoke(); return; }
                if (stepKey == StopKey) { b?.OnStop?.Invoke(); return; }
                Find(b, stepKey)?.Enter?.Invoke();
            }
            catch { }
        }
        public static string Key(TutorialBook b, string stepKey) { var s = Find(b, stepKey); try { return s?.Key?.Invoke(); } catch { return null; } }

        private static TutorialStep Find(TutorialBook b, string stepKey)
        {
            if (b == null || stepKey == null) return null;
            int slash = stepKey.IndexOf('/');
            if (slash < 0) return null;
            string c = stepKey.Substring(0, slash), s = stepKey.Substring(slash + 1);
            foreach (var ch in b.Chapters) if (ch.Id == c) foreach (var st in ch.Steps) if (st.Id == s) return st;
            return null;
        }

        // ── tiny JSON (strings, numbers, bools, null, arrays, objects) ──────────────────────────────────────────────
        private static void Field(StringBuilder sb, string name, string value) => sb.Append(',').Append(Str(name)).Append(':').Append(Str(value ?? ""));
        private static string S(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v is string s ? s : "";

        private static string Str(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s ?? "")
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4")); else sb.Append(ch); break;
                }
            return sb.Append('"').ToString();
        }

        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = 0;
            try { var v = Value(json, ref i); return v; } catch { return null; }
        }

        private static void Ws(string j, ref int i) { while (i < j.Length && char.IsWhiteSpace(j[i])) i++; }

        private static object Value(string j, ref int i)
        {
            Ws(j, ref i);
            char c = j[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>(); i++; Ws(j, ref i);
                if (j[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(j, ref i); string k = (string)Value(j, ref i); Ws(j, ref i);
                    if (j[i++] != ':') throw new FormatException();
                    d[k] = Value(j, ref i); Ws(j, ref i);
                    if (j[i] == ',') { i++; continue; }
                    if (j[i++] == '}') return d;
                    throw new FormatException();
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++; Ws(j, ref i);
                if (j[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(j, ref i)); Ws(j, ref i);
                    if (j[i] == ',') { i++; continue; }
                    if (j[i++] == ']') return l;
                    throw new FormatException();
                }
            }
            if (c == '"')
            {
                var sb = new StringBuilder(); i++;
                while (j[i] != '"')
                {
                    if (j[i] == '\\')
                    {
                        i++;
                        char e = j[i++];
                        switch (e)
                        {
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'u': sb.Append((char)int.Parse(j.Substring(i, 4), NumberStyles.HexNumber)); i += 4; break;
                            default: sb.Append(e); break;
                        }
                    }
                    else sb.Append(j[i++]);
                }
                i++;
                return sb.ToString();
            }
            if (j.Length - i >= 4 && string.CompareOrdinal(j, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (j.Length - i >= 5 && string.CompareOrdinal(j, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (j.Length - i >= 4 && string.CompareOrdinal(j, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < j.Length && "+-.eE0123456789".IndexOf(j[i]) >= 0) i++;
            if (i == start) throw new FormatException();
            return double.Parse(j.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
