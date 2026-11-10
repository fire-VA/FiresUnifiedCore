using System.Collections.Generic;
using System.Linq;
using FiresCore.UI.Kit;
using FiresCore.UI.Shell.Kit;
using TMPro;
using UnityEngine;

namespace FiresCore.Npc.JobBoard
{
    /// <summary>
    /// 0.2.854 ([ghost], Fire 10-27): the Job Board's Reports tab. Two parts, from <see cref="JobReport"/>:
    ///   DAYS - the last ShownDays in-game days, newest first; per companion: chores finished (by chore) and items collected / stored.
    ///   PRODUCTION - per item over those days: collected total, and stored by whom (a companion by name, "hopper", "player").
    /// Kit widgets only (KitUI sections, labels, notes), inside the Job Board KitWindow.
    /// </summary>
    internal static class JobReportView
    {
        private const int ShownDays = 3;

        internal static void Build(RectTransform into)
        {
            if (into == null) return;
            for (int i = into.childCount - 1; i >= 0; i--) Object.Destroy(into.GetChild(i).gameObject);
            var rows = JobReport.Rows;
            int today = JobReport.Today;
            var shown = rows.Where(r => r.Day > today - ShownDays).ToList();
            if (shown.Count == 0)
            {
                KitUI.Note(into, "Nothing recorded yet. Finished chores, items companions pick up, and everything put into chests (by a companion, a hopper chest or you) shows here per in-game day.");
                return;
            }

            // ---- DAYS ----
            foreach (int day in shown.Select(r => r.Day).Distinct().OrderByDescending(d => d))
            {
                var sec = KitUI.Section(into, day == today ? $"TODAY · DAY {day}" : $"DAY {day}");
                var dayRows = shown.Where(r => r.Day == day).ToList();
                foreach (string who in dayRows.Select(r => r.Who).Distinct().OrderBy(w => w == "player" ? 2 : w == "hopper" ? 1 : 0).ThenBy(w => w))
                {
                    var mine = dayRows.Where(r => r.Who == who).ToList();
                    var lines = new List<string>();
                    var chores = mine.Where(r => r.Verb == "chore").OrderByDescending(r => r.Count).ToList();
                    if (chores.Count > 0) lines.Add($"{chores.Sum(r => r.Count)} chore(s): " + string.Join(", ", chores.Select(r => $"{Nice(r.Item)} {r.Count}")));
                    var got = mine.Where(r => r.Verb == "collected").OrderByDescending(r => r.Count).ToList();
                    if (got.Count > 0) lines.Add("collected " + string.Join(", ", got.Take(8).Select(r => $"{Item(r.Item)} {r.Count}")) + (got.Count > 8 ? $" (+{got.Count - 8} more)" : ""));
                    var stored = mine.Where(r => r.Verb == "stored").OrderByDescending(r => r.Count).ToList();
                    if (stored.Count > 0) lines.Add("stored " + string.Join(", ", stored.Take(8).Select(r => $"{Item(r.Item)} {r.Count}")) + (stored.Count > 8 ? $" (+{stored.Count - 8} more)" : ""));
                    var head = KitUI.Label(sec, "Who", WhoName(who), KitUI.BodySize + 1f, FiresTokens.Text, TextAlignmentOptions.MidlineLeft);
                    head.fontStyle = FontStyles.Bold;
                    foreach (var l in lines)
                    {
                        var t = KitUI.Label(sec, "Line", "  " + l, KitUI.SmallSize, FiresTokens.TextMuted, TextAlignmentOptions.MidlineLeft);
                        t.textWrappingMode = TextWrappingModes.Normal;
                    }
                }
            }

            // ---- PRODUCTION ----
            var prod = KitUI.Section(into, $"PRODUCTION · LAST {ShownDays} DAYS");
            var items = shown.Where(r => r.Verb == "collected" || r.Verb == "stored").Select(r => r.Item).Distinct()
                .OrderByDescending(i => shown.Where(r => r.Item == i).Sum(r => r.Count)).ToList();
            if (items.Count == 0) KitUI.Note(prod, "No items collected or stored yet.");
            foreach (string item in items)
            {
                int collected = shown.Where(r => r.Item == item && r.Verb == "collected").Sum(r => r.Count);
                var by = shown.Where(r => r.Item == item && r.Verb == "stored").GroupBy(r => r.Who)
                    .Select(g => (who: g.Key, n: g.Sum(r => r.Count))).OrderByDescending(x => x.n).ToList();
                string storedText = by.Count == 0 ? "not stored yet" : $"stored {by.Sum(x => x.n)}: " + string.Join(", ", by.Select(x => $"{WhoName(x.who)} {x.n}"));
                var t = KitUI.Label(prod, "Item", $"{Item(item)} · collected {collected} · {storedText}", KitUI.SmallSize, FiresTokens.Text, TextAlignmentOptions.MidlineLeft);
                t.textWrappingMode = TextWrappingModes.Normal;
            }
        }

        private static string WhoName(string who) => who == "player" ? "You" : who == "hopper" ? "Hopper chests" : who;
        private static string Item(string token) => Localization.instance != null && !string.IsNullOrEmpty(token) ? Localization.instance.Localize(token) : token;
        private static string Nice(string chore) => System.Text.RegularExpressions.Regex.Replace(chore ?? "", "(?<=[a-z])(?=[A-Z])", " ");
    }
}
