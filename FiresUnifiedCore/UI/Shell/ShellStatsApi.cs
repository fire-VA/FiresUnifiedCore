// Fires UI Shell: stats sections from any mod. Contract: ADD-ONLY, never rename.
// A mod registers a section once at load; the hub's Stats tab lays every section out as a rounded card and calls its rows
// delegate on the main thread, only while the tab shows, every refreshSeconds. Override OFF: kept, never shown, never called.
using System;
using System.Collections.Generic;

namespace FiresCore.UI.Shell
{
    /// <summary>Value colour of a stats row. Add-only.</summary>
    public enum StatState { Normal = 0, Good = 1, Warn = 2, Bad = 3, Muted = 4 }

    /// <summary>One stats row: muted label left, value right coloured by State, optional hover tooltip.</summary>
    public struct StatRow
    {
        public string Label;
        public string Value;
        public StatState State;
        public string Tooltip;

        public StatRow(string label, string value, StatState state = StatState.Normal, string tooltip = null)
        {
            Label = label; Value = value; State = state; Tooltip = tooltip;
        }
    }

    internal static class StatsRegistry
    {
        internal sealed class Section
        {
            public string Id, Title;
            public int Order;
            public Func<IList<StatRow>> Rows;
            public float Refresh;
            public float NextAt;
            public bool Failed, Shown;
            public int Seq;   // registration order breaks ties
        }

        private static readonly Dictionary<string, Section> s_byId = new Dictionary<string, Section>(StringComparer.Ordinal);
        private static int s_seq;
        internal static int Version { get; private set; }   // bumps on every register / unregister (the tab rebuilds its cards)

        internal static void Register(string id, string title, int order, Func<IList<StatRow>> rows, float refreshSeconds)
        {
            if (string.IsNullOrEmpty(id) || rows == null) { ShellLog.Warn("stats: RegisterStatsSection ignored (empty id or null rows)"); return; }
            bool replace = s_byId.ContainsKey(id);
            s_byId[id] = new Section
            {
                Id = id, Title = string.IsNullOrEmpty(title) ? id : title, Order = order, Rows = rows,
                Refresh = UnityEngine.Mathf.Clamp(refreshSeconds, 0.25f, 60f), Seq = s_seq++
            };
            Version++;
            ShellLog.Info($"stats: section '{id}' {(replace ? "replaced" : "registered")} (\"{title}\", order {order}, every {UnityEngine.Mathf.Clamp(refreshSeconds, 0.25f, 60f):0.##} s)");
        }

        internal static void Unregister(string id)
        {
            if (id != null && s_byId.Remove(id)) { Version++; ShellLog.Info($"stats: section '{id}' unregistered"); }
        }

        internal static List<Section> Ordered()
        {
            var list = new List<Section>(s_byId.Values);
            list.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Seq.CompareTo(b.Seq));
            return list;
        }
    }
}
