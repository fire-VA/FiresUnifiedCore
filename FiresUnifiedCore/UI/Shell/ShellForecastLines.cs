// The forecast pill's TEXT
// comes from a small data table, one line per (context, weather now -> weather next). It is written once to
// <BepInEx>\cache\FiresUnifiedCore\forecast_lines.txt (players can edit it; Core re-reads it at start) and a line is picked per
// weather period by a seeded random, so it never flickers. Latin-1 only. Tokens: {next} / {now} = the pretty env names.
using System;
using System.Collections.Generic;
using System.IO;

namespace FiresCore.UI.Shell
{
    internal static class ForecastLines
    {
        // context|now|next|line    (context: normal, cave, raid, boss; now/next: a category or *)
        // categories: clear, mist, rain, storm, snow, snowstorm, ash, gloom, other
        private const string Defaults = @"# Fires forecast lines: context|now|next|line   (* = any; categories: clear mist rain storm snow snowstorm ash gloom other)
# contexts: normal, cave (inside a dungeon / cave), raid (an event is on), boss (a boss is near). Tokens: {now} {next}. Latin-1 only.
normal|clear|clear|Clear skies ahead
normal|clear|clear|Not a cloud in sight
normal|clear|clear|Good day to chop some trees
normal|clear|clear|Sunshine, for now
normal|*|mist|Could get misty
normal|*|mist|Fog rolling in
normal|*|mist|Hard to see soon
normal|mist|mist|Still foggy out
normal|mist|clear|The fog is lifting
normal|*|rain|Looks like rain
normal|*|rain|Bring a cloak
normal|*|rain|Rain on the way
normal|rain|rain|More rain. Of course
normal|rain|clear|Clearing up
normal|rain|clear|The rain is letting up
normal|storm|clear|The storm is passing
normal|*|storm|Chance of thunderstorms
normal|*|storm|Thunder brewing
normal|*|storm|Stay off the hilltops
normal|storm|storm|Thor is still angry
normal|*|snow|Winter is coming
normal|*|snow|Snow on the way
normal|snow|snow|Still snowing
normal|snow|clear|The snow is letting up
normal|*|snowstorm|Blizzard warning
normal|*|snowstorm|Wrap up warm
normal|*|ash|Ash on the wind
normal|*|ash|Smells like burning
normal|*|gloom|It gets gloomy
normal|*|other|Change in the air
normal|*|*|Hard to say
cave|*|*|It's dark in here
cave|*|*|Spooky
cave|*|*|No weather down here
cave|*|*|Was that a noise?
raid|*|*|Not the time to sightsee
raid|*|*|Weather is the least of your worries
raid|*|*|Busy day at the base
boss|*|*|Not the time to sightsee
boss|*|*|Forecast: pain
boss|*|*|Chance of death: high
";

        private struct Line { public string Ctx, Now, Next, Text; }
        private static List<Line> s_lines;

        internal static string Category(string env)
        {
            if (string.IsNullOrEmpty(env)) return "other";
            string e = env.ToLowerInvariant();
            if (e.Contains("snowstorm")) return "snowstorm";
            if (e.Contains("thunder") || e.Contains("storm")) return "storm";
            if (e.Contains("snow")) return "snow";
            if (e.Contains("rain") && e.Contains("ash")) return "ash";
            if (e.Contains("ash")) return "ash";
            if (e.Contains("rain")) return "rain";
            if (e.Contains("mist") || e.Contains("fog")) return "mist";
            if (e.Contains("dark") || e.Contains("gloom")) return "gloom";
            if (e.Contains("clear") || e == "twilight_clear") return "clear";
            return "other";
        }

        private static void Load()
        {
            if (s_lines != null) return;
            s_lines = new List<Line>();
            string path = Path.Combine(BepInEx.Paths.CachePath, "FiresUnifiedCore", "forecast_lines.txt");
            string text = Defaults;
            try
            {
                if (File.Exists(path)) text = File.ReadAllText(path);
                else { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, Defaults); }
            }
            catch (Exception e) { ShellLog.Warn("plate pills: forecast lines file (" + e.Message + "), using the built-in lines"); text = Defaults; }
            foreach (var raw in text.Split('\n'))
            {
                var l = raw.Trim();
                if (l.Length == 0 || l[0] == '#') continue;
                var f = l.Split(new[] { '|' }, 4);
                if (f.Length < 4) continue;
                s_lines.Add(new Line { Ctx = f[0].Trim().ToLowerInvariant(), Now = f[1].Trim().ToLowerInvariant(), Next = f[2].Trim().ToLowerInvariant(), Text = Latin1(f[3].Trim()) });
            }
            if (s_lines.Count == 0) { s_lines = null; text = Defaults; s_lines = new List<Line>(); }
            ShellLog.Info($"plate pills: {s_lines.Count} forecast lines from {(File.Exists(path) ? path : "the built-in table")}");
        }

        private static string Latin1(string s)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s) sb.Append(ch <= 'ÿ' ? ch : '?');
            return sb.ToString();
        }

        /// <summary>A line for (context, now -> next), the same all period long (seeded by the period).</summary>
        internal static string Pick(string context, string nowEnv, string nextEnv, long period)
        {
            Load();
            string cn = Category(nowEnv), cx = Category(nextEnv);
            var pool = new List<string>();
            // most specific first: exact now+next, then *+next, then now+*, then *+*
            foreach (var pass in new[] { 0, 1, 2, 3 })
            {
                foreach (var l in s_lines)
                {
                    if (l.Ctx != context) continue;
                    bool nowOk = pass == 0 || pass == 2 ? l.Now == cn : l.Now == "*";
                    bool nextOk = pass == 0 || pass == 1 ? l.Next == cx : l.Next == "*";
                    if (nowOk && nextOk) pool.Add(l.Text);
                }
                if (pool.Count > 0) break;
            }
            if (pool.Count == 0) return null;
            int idx = (int)((uint)(period * 2654435761L ^ (context.GetHashCode() * 31)) % (uint)pool.Count);
            return pool[idx].Replace("{next}", PlatePills.Pretty(nextEnv)).Replace("{now}", PlatePills.Pretty(nowEnv));
        }
    }
}
