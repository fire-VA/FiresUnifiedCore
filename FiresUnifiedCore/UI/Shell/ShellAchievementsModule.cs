// Fires UI Shell (approved mock:
// Tools\golden\ui_overhaul\layout_20261015\achievements_unified_1920.png): ONE Achievements tab over every AchievementBridge
// catalogue, with a source selector Vanilla ("valheim", FiresRPGClasses) / Vanilla + RPGmaker / RPGmaker ("rpgmaker"), a stats
// header (Earned E / T, score), a progress bar, and source-tagged cards in 3 columns that scroll. The choice is remembered per
// character. Other UIs open it pre-selected: FiresShell.Open("fires.achievements", "valheim" | "rpgmaker" | "all").
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FiresCore.Bridge;
using FiresCore.UI.Shell.Kit;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI.Shell
{
    internal sealed class ShellAchievementsModule : FiresShellModuleBase
    {
        internal const string ModuleId = "fires.achievements";
        public override string Id => ModuleId;
        public override string Title => "Achievements";
        public override int Order => 800;
        // A Player pill, grouped with RPGmaker's Leaderboard ("Leaderboard | Achievements"); alone it reads "Achievements"
        public override string ParentTab => "player";
        // (One flat row Stats | Appearance | Classes | Leaderboard | Achievements, no inner pill row): its own pill
        public override int PillOrder => 40;

        private const string VanillaId = "valheim", RpgId = "rpgmaker";
        private static readonly string[] Modes = { "valheim", "all", "rpgmaker" };
        private static readonly string[] ModeLabels = { "Vanilla", "Vanilla + RPGmaker", "RPGmaker" };
        private static readonly Color RpgTag = new Color(0.47f, 0.70f, 1f, 1f);
        private const float Pad = 18f, Gap = 16f, CardH = 96f;

        private RectTransform _content, _top, _viewport, _list;
        private ScrollRect _scroll;
        private KitLabel _summary, _pct;
        private RectTransform _barFill;
        private int _mode = 1;
        private bool _built;

        public override void BuildTab(RectTransform content, IShellContext ctx)
        {
            _content = content;
            _mode = LoadMode();
            AchievementBridge.CataloguesChanged -= OnCataloguesChanged;
            AchievementBridge.CataloguesChanged += OnCataloguesChanged;

            _top = Widgets.Node(content, "Top");
            _top.anchorMin = new Vector2(0f, 1f); _top.anchorMax = new Vector2(1f, 1f); _top.pivot = new Vector2(0.5f, 1f);
            _top.anchoredPosition = Vector2.zero; _top.sizeDelta = new Vector2(0f, 74f);

            var vpGo = new GameObject("Viewport", typeof(RectTransform));
            _viewport = (RectTransform)vpGo.transform; _viewport.SetParent(content, false);
            _viewport.anchorMin = Vector2.zero; _viewport.anchorMax = Vector2.one;
            _viewport.offsetMin = Vector2.zero; _viewport.offsetMax = new Vector2(-(ShellConfigModule.BarWidth + 4f), -80f);
            vpGo.AddComponent<RectMask2D>();
            var hit = vpGo.AddComponent<Image>(); hit.color = new Color(0f, 0f, 0f, 0f);
            _list = Widgets.Node(_viewport, "Cards");
            _list.anchorMin = new Vector2(0f, 1f); _list.anchorMax = new Vector2(1f, 1f); _list.pivot = new Vector2(0.5f, 1f);
            _scroll = vpGo.AddComponent<ScrollRect>();
            _scroll.viewport = _viewport; _scroll.content = _list; _scroll.horizontal = false; _scroll.scrollSensitivity = 40f;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            ShellConfigModule.AddScrollbar(_scroll, _viewport, "AchievementsScrollbar");

            ctx.SetFooterHints(("Tab", "next tab"), ("Esc", "close"));
            _built = true;
            Rebuild();
        }

        public override void OnShow(string subView)
        {
            int i = Array.IndexOf(Modes, (subView ?? "").Trim().ToLowerInvariant());
            if (i >= 0 && i != _mode) { _mode = i; SaveMode(); ShellLog.Info("achievements: opened on '" + Modes[i] + "'"); }
            Rebuild();
        }

        private void OnCataloguesChanged() { if (_built) Rebuild(); }

        // ---- build ---------------------------------------------------------------------------------------------
        private void Rebuild()
        {
            if (_top == null || _list == null) return;
            // (Clicking "RPGmaker" lost the pills and the cards until reopen): Destroy is deferred, and the kit's
            // Widgets.Group finds its "Segmented" / "Card" groups BY NAME, so the rebuild re-used the dying groups and the new pills
            // and cards died with them at frame end. Detach first, then Destroy.
            DetachAndDestroyChildren(_top);
            DetachAndDestroyChildren(_list);

            var sources = new List<string>();
            if (_mode != 2 && AchievementBridge.HasCatalogue(VanillaId)) sources.Add(VanillaId);
            if (_mode != 0 && AchievementBridge.HasCatalogue(RpgId)) sources.Add(RpgId);
            if (_mode == 1) foreach (var id in AchievementBridge.SourceIds) if (id != VanillaId && id != RpgId) sources.Add(id);   // any other catalogue: shown under "both"

            // stats header
            int earned = 0, total = 0, score = 0;
            var rows = new List<(string src, AchievementRow row)>();
            foreach (var src in sources)
            {
                var st = AchievementBridge.Stats(src);
                earned += st.Earned; total += st.Total; score += st.Score;
                foreach (var r in AchievementBridge.Read(src)) if (r != null) rows.Add((src, r));
            }
            string player = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "";
            _summary = KitText.Create(_top, "Summary", "Earned " + earned + " / " + total + (score > earned ? " · Score " + score : "") + (player.Length > 0 ? " · " + player : ""),
                FontRole.SansRegular, 14f, FiresTokens.TextMuted, KitAlign.Left, 0f, false, false);
            Widgets.Place(_summary.Rect, Pad, 4f, 600f, 22f);

            // selector (centred)
            var seg = Widgets.Segmented(_top, "source", ModeLabels, _mode, 0f, 0f);
            float segW = seg.sizeDelta.x;
            seg.anchorMin = seg.anchorMax = new Vector2(0.5f, 1f); seg.pivot = new Vector2(0.5f, 1f); seg.anchoredPosition = new Vector2(0f, -2f);
            for (int i = 0; i < Modes.Length; i++)
            {
                var node = seg.Find("Seg/" + ModeLabels[i].ToLowerInvariant().Replace(" ", "_")) as RectTransform;
                int m = i;
                if (node != null) { var hi = node.gameObject.AddComponent<Image>(); hi.color = new Color(0f, 0f, 0f, 0f); ShellWidgets.Clickable(node, () => SetMode(m)); }
            }

            // progress bar
            var lbl = KitText.Create(_top, "ProgressLabel", "Progress", FontRole.SansRegular, 13f, FiresTokens.TextMuted, KitAlign.Left, 0f, false, false);
            Widgets.Place(lbl.Rect, Pad, 44f, 100f, 20f);
            var track = Widgets.Node(_top, "Bar");
            track.anchorMin = new Vector2(0f, 1f); track.anchorMax = new Vector2(1f, 1f); track.pivot = new Vector2(0f, 1f);
            track.offsetMin = new Vector2(Pad + 110f, -57f); track.offsetMax = new Vector2(-(Pad + 70f), -51f);   // 6 px tall, between the label and the %
            Widgets.Stretch(Widgets.Box(track, "Track", KitSprites.RoundedFill(3f), FiresTokens.Hairline, true).rectTransform, 0, 0, 0, 0);
            float frac = total > 0 ? Mathf.Clamp01(earned / (float)total) : 0f;
            var fill = Widgets.Box(track, "Fill", KitSprites.RoundedFill(3f), FiresTokens.Gold, true);
            _barFill = fill.rectTransform; _barFill.anchorMin = Vector2.zero; _barFill.anchorMax = new Vector2(frac, 1f); _barFill.offsetMin = _barFill.offsetMax = Vector2.zero;
            _pct = KitText.Create(_top, "Percent", Mathf.RoundToInt(frac * 100f) + "%", FontRole.SansBold, 14f, FiresTokens.Text, KitAlign.Right, 0f, false, false);
            Widgets.PlaceRight(_pct.Rect, Pad, 42f, 64f, 22f);

            // cards: earned first, then by progress, then name
            rows.Sort((a, b) =>
            {
                if (a.row.Earned != b.row.Earned) return a.row.Earned ? -1 : 1;
                float pa = a.row.HasProgress ? a.row.Progress / (float)a.row.Target : 0f, pb = b.row.HasProgress ? b.row.Progress / (float)b.row.Target : 0f;
                if (Mathf.Abs(pa - pb) > 0.0001f) return pb.CompareTo(pa);
                return string.Compare(a.row.Name, b.row.Name, StringComparison.OrdinalIgnoreCase);
            });
            float width = _viewport.rect.width > 10f ? _viewport.rect.width : 1300f;
            const int cols = 3;
            float cw = (width - 2f * Pad - (cols - 1) * Gap) / cols;
            for (int i = 0; i < rows.Count; i++)
            {
                int c = i % cols, r = i / cols;
                Card(rows[i].src, rows[i].row, Pad + c * (cw + Gap), 4f + r * (CardH + 12f), cw, i);
            }
            if (rows.Count == 0)
            {
                var none = KitText.Create(_list, "None", sources.Count == 0 ? "No achievement source is installed for this view." : "No achievements yet.", FontRole.SansRegular, 15f, FiresTokens.TextMuted, KitAlign.Center, 0f, false, false);
                Widgets.Place(none.Rect, 0f, 40f, width, 30f);
            }
            int rowsN = (rows.Count + cols - 1) / cols;
            _list.sizeDelta = new Vector2(0f, Mathf.Max(80f, 8f + rowsN * (CardH + 12f)));
            ShellLog.Info($"achievements: '{Modes[_mode]}' -> {rows.Count} shown from {sources.Count} source(s) ({string.Join(", ", sources.ToArray())}), earned {earned}/{total}, score {score}");
        }

        // Detach before Destroy so name lookups (Widgets.Group) never find a dying node.
        private static void DetachAndDestroyChildren(RectTransform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                var child = parent.GetChild(i);
                child.SetParent(null, false);
                UnityEngine.Object.Destroy(child.gameObject);
            }
        }

        private void Card(string src, AchievementRow row, float x, float y, float w, int n)
        {
            var root = Widgets.PathNode(_list, "Card", n.ToString(CultureInfo.InvariantCulture));
            Widgets.Place(root, x, y, w, CardH);
            bool earned = row.Earned;
            var fill = Widgets.Box(root, "Fill", KitSprites.RoundedFill(FiresTokens.Radius), earned ? FiresTokens.WithAlpha(FiresTokens.Selected, 0.92f) : new Color(0.086f, 0.067f, 0.047f, 0.82f), true);
            Widgets.Stretch(fill.rectTransform, 0, 0, 0, 0); fill.raycastTarget = false;
            var edge = Widgets.Box(root, "Edge", KitSprites.RoundedOutline(FiresTokens.Radius), earned ? FiresTokens.Edge : FiresTokens.Hairline, true);
            Widgets.Stretch(edge.rectTransform, 0, 0, 0, 0); edge.raycastTarget = false;

            // icon well
            var well = Widgets.Well(root, "IconWell", 12f, 14f, 66f, 66f);
            if (row.Icon != null && (!row.Secret || earned))
            {
                var ic = Widgets.Box(well, "Icon", row.Icon, earned ? Color.white : new Color(1f, 1f, 1f, 0.55f), false);
                Widgets.Stretch(ic.rectTransform, 6f, 6f, 6f, 6f); ic.preserveAspect = true; ic.raycastTarget = false;
            }

            bool hide = row.Secret && !earned;
            float tx = 90f, tw = w - tx - 12f;
            var name = KitText.Create(root, "Name", hide ? "???" : row.Name, FontRole.SerifBold, 17f, earned ? FiresTokens.Gold : FiresTokens.Text, KitAlign.Left, 0f, false, false);
            Widgets.Place(name.Rect, tx, 12f, tw - 100f, 24f);
            // source tag: Vanilla = muted outline, RPGmaker = blue
            bool rpg = src == RpgId;
            string tagText = src == VanillaId ? "Vanilla" : rpg ? "RPGmaker" : AchievementBridge.DisplayNameOf(src);
            var tag = Widgets.Node(root, "Tag");
            var tl = KitText.Create(tag, "L", tagText, FontRole.SansBold, 11f, rpg ? RpgTag : FiresTokens.Text, KitAlign.Center, 0f, false, false);
            float tagW = Mathf.Max(80f, Mathf.Ceil(tl.PreferredWidth) + 22f);
            Widgets.PlaceRight(tag, 12f, 12f, tagW, 20f);
            Widgets.Stretch(Widgets.Box(tag, "Outline", KitSprites.RoundPill(10f, true), rpg ? RpgTag : FiresTokens.TextMuted).rectTransform, 0, 0, 0, 0);
            Widgets.Stretch(tl.Rect, 0, 0, 0, 0);

            var desc = KitText.Create(root, "Desc", hide ? "Hidden until earned." : row.Description, FontRole.SansRegular, 13f, FiresTokens.TextMuted, KitAlign.Left, 0f, false, false);
            Widgets.Place(desc.Rect, tx, 38f, tw, 20f);
            string status; Color sc;
            if (earned) { status = "Earned"; sc = FiresTokens.Ok; }
            else if (!string.IsNullOrEmpty(row.Blocked)) { status = row.Blocked; sc = FiresTokens.Warn; }
            else if (row.HasProgress) { status = row.Progress + " / " + row.Target; sc = FiresTokens.Text; }
            else { status = "0 / 1"; sc = FiresTokens.Text; }
            var st = KitText.Create(root, "Status", status, FontRole.SansBold, 13f, sc, KitAlign.Left, 0f, false, false);
            Widgets.Place(st.Rect, tx, 62f, tw, 20f);
        }

        private void SetMode(int m)
        {
            if (m == _mode) return;
            _mode = m; SaveMode();
            ShellLog.Info("achievements: source -> '" + Modes[m] + "'");
            Rebuild();
        }

        // ---- per-character memory ---------------------------------------------------------------------------------
        private static string ModePath()
        {
            long id = 0;
            try { if (Game.instance != null && Game.instance.GetPlayerProfile() != null) id = Game.instance.GetPlayerProfile().GetPlayerID(); } catch { }
            return Path.Combine(BepInEx.Paths.CachePath, "FiresUnifiedCore", "achievements_source_" + id.ToString(CultureInfo.InvariantCulture) + ".txt");
        }

        private static int LoadMode()
        {
            try { var p = ModePath(); if (File.Exists(p)) { int i = Array.IndexOf(Modes, File.ReadAllText(p).Trim()); if (i >= 0) return i; } } catch { }
            return 1;   // default: Vanilla + RPGmaker
        }

        private void SaveMode()
        {
            try { var p = ModePath(); Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, Modes[_mode]); }
            catch (Exception e) { ShellLog.Warn("achievements: source choice not saved (" + e.Message + ")"); }
        }
    }
}
