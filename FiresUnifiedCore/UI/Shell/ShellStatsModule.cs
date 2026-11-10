// Fires UI Shell: the built-in "Stats" tab, able to show stats from the installed mods:
// every section is a rounded kit card, Core's own four (Performance, Network, Objects, Players) plus any mod's
// FiresShell.RegisterStatsSection cards, ordered by their order band, packed into 2 or 3 columns by width (each card into the
// shortest column), inside a clipped scroll view with the kit scrollbar. Each card's rows are read at its own rate, only while the
// tab shows. With FGN loaded the Network card reads FGN's real state through FgnApi v2 (by reflection; Core never references FGN).
// Registered by the hub only while [UI] Fires UI override is ON.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using FiresCore.UI.Shell.Kit;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FiresCore.UI.Shell
{
    internal sealed class ShellStatsModule : FiresShellModuleBase
    {
        internal const string ModuleId = "fires.stats";
        public override string Id => ModuleId;
        public override string Title => "Info";   // the perf / network / objects / players / terrain screen
        public override int Order => 900;

        private const int MaxPlayerRows = 8;
        private const float Pad = 18f, ColGap = 24f, CardGap = 18f, CardPad = 16f;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private sealed class Card
        {
            public StatsRegistry.Section Src;
            public RectTransform Root;
            public KitSection Section;
            public readonly List<KitRow> Rows = new List<KitRow>();
            public readonly List<RowTip> Tips = new List<RowTip>();
            public float Height;
            public bool ErrorShown;
        }

        private RectTransform _content, _viewport, _list;
        private ScrollRect _scroll;
        private readonly List<Card> _cards = new List<Card>();
        private int _builtVersion = -1, _builtCols;
        private float _builtWidth;
        private IShellContext _ctx;

        public override void BuildTab(RectTransform content, IShellContext ctx)
        {
            _ctx = ctx;
            _content = content;
            _cards.Clear(); _builtVersion = -1;
            EnsureCoreSections();

            var vpGo = new GameObject("Viewport", typeof(RectTransform));
            _viewport = (RectTransform)vpGo.transform; _viewport.SetParent(content, false);
            _viewport.anchorMin = Vector2.zero; _viewport.anchorMax = Vector2.one;
            _viewport.offsetMin = Vector2.zero; _viewport.offsetMax = new Vector2(-(ShellConfigModule.BarWidth + 4f), 0f);
            vpGo.AddComponent<RectMask2D>();
            var hit = vpGo.AddComponent<Image>(); hit.color = new Color(0f, 0f, 0f, 0f);
            _list = Widgets.Node(_viewport, "Cards");
            _list.anchorMin = new Vector2(0f, 1f); _list.anchorMax = new Vector2(1f, 1f); _list.pivot = new Vector2(0.5f, 1f);
            _list.anchoredPosition = Vector2.zero; _list.sizeDelta = Vector2.zero;
            _scroll = vpGo.AddComponent<ScrollRect>();
            _scroll.viewport = _viewport; _scroll.content = _list; _scroll.horizontal = false; _scroll.scrollSensitivity = 40f;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            ShellConfigModule.AddScrollbar(_scroll, _viewport, "StatsScrollbar");

            ctx.SetFooterHints(("Tab", "next tab"), ("F1", "close"), ("Esc", "close"));
            Refresh();
        }

        public override void OnShow(string subView)
        {
            foreach (var c in _cards) if (c.Src != null) c.Src.NextAt = 0f;   // fresh numbers the moment the tab shows
            Refresh();
        }

        /// <summary>Called by the hub about twice a second while this tab is visible.</summary>
        internal void Refresh()
        {
            if (_list == null) return;
            float width = _viewport.rect.width > 10f ? _viewport.rect.width : 1340f;
            int cols = width >= 1260f ? 3 : 2;
            if (StatsRegistry.Version != _builtVersion) RebuildCards(width, cols);

            float now = Time.unscaledTime;
            bool relayout = cols != _builtCols || Mathf.Abs(width - _builtWidth) > 1f;
            foreach (var c in _cards)
            {
                if (now < c.Src.NextAt) continue;
                c.Src.NextAt = now + c.Src.Refresh;
                if (Fill(c, (width - 2f * Pad - (cols - 1) * ColGap) / cols)) relayout = true;
            }
            if (relayout) Layout(width, cols);
        }

        // ---- cards ------------------------------------------------------------------------------------------
        private void RebuildCards(float width, int cols)
        {
            foreach (var c in _cards) if (c.Root != null) UnityEngine.Object.Destroy(c.Root.gameObject);
            _cards.Clear();
            foreach (var s in StatsRegistry.Ordered())
                _cards.Add(new Card { Src = s });
            _builtVersion = StatsRegistry.Version;
            float cardW = (width - 2f * Pad - (cols - 1) * ColGap) / cols;
            foreach (var c in _cards) { c.Src.NextAt = 0f; BuildShell(c, cardW); }
            _builtCols = -1;   // force a layout
        }

        private void BuildShell(Card c, float cardW)
        {
            if (c.Root != null) UnityEngine.Object.Destroy(c.Root.gameObject);
            c.Rows.Clear(); c.Tips.Clear();
            c.Root = Widgets.PathNode(_list, "Card", SafeId(c.Src.Id));
            c.Root.anchorMin = c.Root.anchorMax = c.Root.pivot = new Vector2(0f, 1f);
            var fill = Widgets.Box(c.Root, "Fill", KitSprites.RoundedFill(FiresTokens.Radius), new Color(0.086f, 0.067f, 0.047f, 0.72f), true);
            Widgets.Stretch(fill.rectTransform, 0, 0, 0, 0); fill.raycastTarget = false;
            var edge = Widgets.Box(c.Root, "Edge", KitSprites.RoundedOutline(FiresTokens.Radius), FiresTokens.Edge, true);
            Widgets.Stretch(edge.rectTransform, 0, 0, 0, 0); edge.raycastTarget = false;
            c.Section = Widgets.Section(c.Root, "s", c.Src.Title, CardPad, CardPad, cardW - 2f * CardPad);
            c.Height = c.Section.Y + 2f * CardPad;
            c.Root.sizeDelta = new Vector2(cardW, c.Height);
        }

        // Reads the card's rows; returns true when its height changed (the columns re-pack).
        private bool Fill(Card c, float cardW)
        {
            IList<StatRow> rows;
            try { rows = c.Src.Rows(); }
            catch (Exception e)
            {
                if (!c.Src.Failed) { c.Src.Failed = true; ShellLog.Warn($"stats: section '{c.Src.Id}' rows threw {e.GetType().Name}: {e.Message} (card shows 'error - see log')"); }
                rows = new[] { new StatRow("error - see log", "", StatState.Bad) };
            }
            if (rows == null) rows = Array.Empty<StatRow>();
            bool grew = false;
            if (rows.Count != c.Rows.Count || Mathf.Abs(c.Section.Width - (cardW - 2f * CardPad)) > 1f)
            {
                BuildShell(c, cardW);
                for (int i = 0; i < rows.Count; i++)
                {
                    var kr = Widgets.Row(c.Section, "r" + i, "", "");
                    c.Rows.Add(kr);
                    var tip = kr.Root.gameObject.AddComponent<RowTip>();
                    tip.Host = _list;
                    c.Tips.Add(tip);
                }
                c.Height = c.Section.Y + 2f * CardPad;
                c.Root.sizeDelta = new Vector2(cardW, c.Height);
                grew = true;
            }
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                c.Rows[i].Label.Value = r.Label ?? "";
                c.Rows[i].Value.Value = r.Value ?? "";
                c.Rows[i].Value.Color = ColorOf(r.State);
                c.Tips[i].Title = r.Label; c.Tips[i].Body = r.Tooltip;
            }
            if (!c.Src.Shown)
            {
                c.Src.Shown = true;
                ShellLog.Info($"stats: section '{c.Src.Id}' shown ({rows.Count} rows, every {c.Src.Refresh:0.##} s)");
            }
            return grew;
        }

        // Each card into the shortest column, in order.
        private void Layout(float width, int cols)
        {
            float cardW = (width - 2f * Pad - (cols - 1) * ColGap) / cols;
            var colY = new float[cols];
            for (int i = 0; i < cols; i++) colY[i] = Pad * 0.5f;
            foreach (var c in _cards)
            {
                if (c.Root == null) continue;
                if (Mathf.Abs(c.Root.sizeDelta.x - cardW) > 1f) { c.Src.NextAt = 0f; Fill(c, cardW); }
                int best = 0;
                for (int i = 1; i < cols; i++) if (colY[i] < colY[best] - 0.5f) best = i;
                c.Root.anchoredPosition = new Vector2(Pad + best * (cardW + ColGap), -colY[best]);
                colY[best] += c.Height + CardGap;
            }
            float h = 0f; foreach (var y in colY) h = Mathf.Max(h, y);
            _list.sizeDelta = new Vector2(0f, h + Pad);
            _builtCols = cols; _builtWidth = width;
        }

        private static Color ColorOf(StatState s)
        {
            switch (s)
            {
                case StatState.Good: return FiresTokens.Ok;
                case StatState.Warn: return FiresTokens.Warn;
                case StatState.Bad: return FiresTokens.Danger;
                case StatState.Muted: return FiresTokens.TextFaint;
                default: return FiresTokens.Text;
            }
        }

        private static string SafeId(string id)
        {
            var sb = new System.Text.StringBuilder(id.Length);
            foreach (char ch in id) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '.' ? ch : '_');
            return sb.ToString();
        }

        // ---- hover tooltip ------------------------------------------------------------------------------------
        private sealed class RowTip : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            internal RectTransform Host;
            internal string Title, Body;
            private RectTransform _tip;
            public void OnPointerEnter(PointerEventData e)
            {
                if (string.IsNullOrEmpty(Body) || Host == null) return;
                var rt = (RectTransform)transform;
                Vector3 local = Host.InverseTransformPoint(rt.TransformPoint(new Vector3(rt.rect.xMin, rt.rect.yMin, 0f)));
                _tip = Widgets.Tooltip(Host, Title ?? "", Body, null, local.x, -local.y + 4f, FiresTokens.TooltipMaxWidth);
            }
            public void OnPointerExit(PointerEventData e) { Hide(); }
            private void OnDisable() { Hide(); }
            private void Hide() { if (_tip != null) { Destroy(_tip.gameObject); _tip = null; } }
        }

        // ---- Core's own sections (order 0-99) -------------------------------------------------------------------
        private static bool s_coreRegistered;
        private static float s_fpsSmoothed = -1f;
        private static FieldInfo s_objectsById;
        private static readonly List<StatRow> s_perf = new List<StatRow>(), s_net = new List<StatRow>(), s_obj = new List<StatRow>(), s_players = new List<StatRow>();

        private static void EnsureCoreSections()
        {
            if (s_coreRegistered) return;
            s_coreRegistered = true;
            StatsRegistry.Register("core.performance", "Performance", 0, PerfRows, 0.5f);
            StatsRegistry.Register("core.network", "Network", 10, NetRows, 1f);
            StatsRegistry.Register("core.objects", "Objects", 20, ObjRows, 1f);
            StatsRegistry.Register("core.players", "Players", 30, PlayerRows, 1f);
        }

        private static IList<StatRow> PerfRows()
        {
            s_perf.Clear();
            float fps = 1f / Mathf.Max(0.0001f, Time.unscaledDeltaTime);
            s_fpsSmoothed = s_fpsSmoothed < 0f ? fps : Mathf.Lerp(s_fpsSmoothed, fps, 0.3f);
            var st = s_fpsSmoothed < 30f ? StatState.Bad : s_fpsSmoothed < 50f ? StatState.Warn : StatState.Normal;
            s_perf.Add(new StatRow("Frames per second", s_fpsSmoothed.ToString("0.0", Inv), st));
            s_perf.Add(new StatRow("Frame time", (1000f / s_fpsSmoothed).ToString("0.0", Inv) + " ms", st));
            s_perf.Add(new StatRow("Managed heap", (GC.GetTotalMemory(false) / (1024 * 1024)).ToString(Inv) + " MB", StatState.Normal,
                "Mono keeps its peak: this rarely goes down, even after a collection."));
            return s_perf;
        }

        private static IList<StatRow> NetRows()
        {
            s_net.Clear();
            if (FgnNet(s_net)) return s_net;
            var zn = ZNet.instance;
            if (zn != null && !zn.IsServer())
            {
                try
                {
                    zn.GetNetStats(out float lq, out float rq, out int ping, out float outB, out float inB);
                    s_net.Add(new StatRow("Ping", ping.ToString(Inv) + " ms", ping > 200 ? StatState.Bad : ping > 100 ? StatState.Warn : StatState.Normal));
                    s_net.Add(new StatRow("Quality (local / remote)", (lq * 100f).ToString("0", Inv) + "% / " + (rq * 100f).ToString("0", Inv) + "%"));
                    s_net.Add(new StatRow("Data sent", (outB / 1024f).ToString("0.0", Inv) + " kB/s"));
                    s_net.Add(new StatRow("Data received", (inB / 1024f).ToString("0.0", Inv) + " kB/s"));
                }
                catch { s_net.Add(new StatRow("Network", "n/a", StatState.Muted)); }
            }
            else s_net.Add(new StatRow("Role", zn == null ? "-" : "host (vanilla)", StatState.Muted));
            return s_net;
        }

        // FGN's FgnApi v2 (a contract): GetRole(), GetPeerCount(), IsSimulationActive(), GetTraffic(out outKBps, out inKBps, out sendQueueBytes, out worstPingMs).
        private static bool s_fgnLooked;
        private static MethodInfo s_fgnRole, s_fgnPeers, s_fgnSim, s_fgnTraffic;
        private static bool FgnNet(List<StatRow> rows)
        {
            if (!s_fgnLooked)
            {
                s_fgnLooked = true;
                try
                {
                    var t = Type.GetType("FiresGhettoNetworkMod.Api.FgnApi, VAGhettoNetworking");
                    var ver = t?.GetProperty("Version", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    var verF = t?.GetField("Version", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    int v = Convert.ToInt32(ver ?? verF ?? 0, Inv);
                    if (t != null && v >= 2)
                    {
                        const BindingFlags B = BindingFlags.Public | BindingFlags.Static;
                        s_fgnRole = t.GetMethod("GetRole", B, null, Type.EmptyTypes, null);
                        s_fgnPeers = t.GetMethod("GetPeerCount", B, null, Type.EmptyTypes, null);
                        s_fgnSim = t.GetMethod("IsSimulationActive", B, null, Type.EmptyTypes, null);
                        s_fgnTraffic = t.GetMethod("GetTraffic", B);
                    }
                    ShellLog.Info("stats: Network card source " + (s_fgnRole != null ? "FGN (FgnApi v" + v + ")" : "vanilla" + (t != null ? " (FgnApi v" + v + " < 2)" : "")));
                }
                catch (Exception e) { ShellLog.Info("stats: Network card source vanilla (FgnApi lookup: " + e.Message + ")"); s_fgnRole = null; }
            }
            if (s_fgnRole == null) return false;
            try
            {
                rows.Add(new StatRow("Role", Convert.ToString(s_fgnRole.Invoke(null, null), Inv)));
                if (s_fgnPeers != null) rows.Add(new StatRow("Peers", Convert.ToString(s_fgnPeers.Invoke(null, null), Inv)));
                if (s_fgnSim != null) { bool sim = (bool)s_fgnSim.Invoke(null, null); rows.Add(new StatRow("Simulation", sim ? "active" : "idle", sim ? StatState.Good : StatState.Muted)); }
                if (s_fgnTraffic != null)
                {
                    var args = new object[4];
                    s_fgnTraffic.Invoke(null, args);
                    float o = Convert.ToSingle(args[0], Inv), i = Convert.ToSingle(args[1], Inv);
                    long q = Convert.ToInt64(args[2], Inv); int ping = Convert.ToInt32(args[3], Inv);
                    rows.Add(new StatRow("Out / in", o.ToString("0.0", Inv) + " / " + i.ToString("0.0", Inv) + " kB/s"));
                    rows.Add(new StatRow("Send queue", (q / 1024f).ToString("0.0", Inv) + " kB", q > 256 * 1024 ? StatState.Bad : q > 64 * 1024 ? StatState.Warn : StatState.Normal));
                    rows.Add(new StatRow("Worst ping", ping.ToString(Inv) + " ms", ping > 200 ? StatState.Bad : ping > 100 ? StatState.Warn : StatState.Normal));
                }
                return true;
            }
            catch (Exception e)
            {
                rows.Clear();
                ShellLog.Warn("stats: FgnApi read failed (" + (e.InnerException ?? e).Message + "), Network card back to vanilla");
                s_fgnRole = null;
                return false;
            }
        }

        private static IList<StatRow> ObjRows()
        {
            s_obj.Clear();
            s_obj.Add(new StatRow("ZDO active", ZdoCount()));
            var list = ZNet.instance != null ? ZNet.instance.GetPlayerList() : null;
            s_obj.Add(new StatRow("Players online", list != null ? list.Count.ToString(Inv) : "-"));
            return s_obj;
        }

        private static IList<StatRow> PlayerRows()
        {
            s_players.Clear();
            var list = ZNet.instance != null ? ZNet.instance.GetPlayerList() : null;
            if (list == null || list.Count == 0) { s_players.Add(new StatRow("No players", "", StatState.Muted)); return s_players; }
            for (int i = 0; i < list.Count && i < MaxPlayerRows; i++) s_players.Add(new StatRow(list[i].m_name, ""));
            if (list.Count > MaxPlayerRows) s_players.Add(new StatRow("+" + (list.Count - MaxPlayerRows) + " more", "", StatState.Muted));
            return s_players;
        }

        // ZDOMan.m_objectsByID is private: reflection, the same way World\ZdoOrphanService reads it.
        private static string ZdoCount()
        {
            var zm = ZDOMan.instance;
            if (zm == null) return "-";
            try
            {
                if (s_objectsById == null)
                    s_objectsById = typeof(ZDOMan).GetField("m_objectsByID", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var dict = s_objectsById?.GetValue(zm) as System.Collections.ICollection;
                return dict != null ? dict.Count.ToString(Inv) : "n/a";
            }
            catch { return "n/a"; }
        }
    }
}
