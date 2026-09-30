using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine.UI;

namespace FiresCore.Pieces
{
    // Whole top tabs of the 1.0 build menu, for piece tables a mod owns.
    //
    // BuildUi's top tabs map by position to its m_pieceLists: by usage, by material, recent, favorites. While a table with
    // a page source is open, each page the source labels shows the source's own rows under the source's label, and
    // vanilla's list and label come back as soon as any other table opens. Favorites is never replaced: it is the player's
    // own stars and categories, saved with the character. Tables that keep vanilla's pages add rows through BuildMenuTabs.
    // Added 2026-09-27 so FAT's Chaos Hammer reads like the hammer (Categories / Biomes / Blueprints / Favorites).
    public static class BuildMenuPages
    {
        public const int Categories = 0;
        public const int Materials = 1;
        public const int Recent = 2;

        // Favorites is the fourth page and stays vanilla's.
        private const int ReplaceablePages = 3;
        private const int AllTags = -1;
        private const int NoSeparator = -1;

        public interface ISource
        {
            bool Owns(PieceTable table);

            // The label of a top tab this source fills, or null to leave vanilla's page there.
            string PageLabel(int page);

            // The page's rows, in display order. Keys are the source's own and must be unique within the page.
            void CollectTabs(PieceTable table, int page, List<BuildMenuTabs.Tab> into);

            // The pieces of one row. Core keeps only the table's available pieces, drops repeats, and adds the repair
            // and remove tools itself.
            void Fill(PieceTable table, int page, int key, IList<Piece> into);
        }

        private static readonly List<ISource> s_sources = new List<ISource>();
        private static readonly PageList[] s_pages = { new PageList(Categories), new PageList(Materials), new PageList(Recent) };
        private static readonly string[] s_labels = new string[ReplaceablePages];

        private static BuildUi s_capturedFor;
        private static readonly IPieceList[] s_vanillaLists = new IPieceList[ReplaceablePages];
        private static readonly TMP_Text[][] s_tabTexts = new TMP_Text[ReplaceablePages][];
        private static readonly string[][] s_vanillaLabels = new string[ReplaceablePages][];

        private static readonly AccessTools.FieldRef<BuildUi, List<IPieceList>> s_pieceLists = Resolve<List<IPieceList>>("m_pieceLists");
        private static readonly AccessTools.FieldRef<BuildUi, List<Button>> s_tabButtons = Resolve<List<Button>>("m_tabButtons");

        public static void Register(ISource source)
        {
            if (source != null && !s_sources.Contains(source)) s_sources.Add(source);
        }

        private static AccessTools.FieldRef<BuildUi, T> Resolve<T>(string field)
        {
            try { return AccessTools.FieldRefAccess<BuildUi, T>(field); }
            catch (Exception ex)
            {
                Logging.FiresLogger.LogWarning($"[BuildMenuPages] BuildUi.{field} did not resolve ({ex.GetType().Name}): "
                    + "tables with their own build-menu pages show vanilla's pages instead.");
                return null;
            }
        }

        // Before OpenBuildMenu selects a page, so the page it selects is already the right list.
        private static void ApplyLists(BuildUi ui)
        {
            if (ui == null || s_pieceLists == null) return;
            var lists = s_pieceLists(ui);
            if (lists == null || lists.Count <= ReplaceablePages) return;
            if (!ReferenceEquals(s_capturedFor, ui)) Capture(ui, lists);
            var table = Player.m_localPlayer != null ? Player.m_localPlayer.GetBuildTool() : null;
            var source = SourceFor(table);
            for (int page = 0; page < ReplaceablePages; page++)
            {
                s_labels[page] = source != null ? LabelOf(source, page) : null;
                if (s_labels[page] != null) s_pages[page].Bind(source);
                lists[page] = s_labels[page] != null ? s_pages[page] : s_vanillaLists[page];
            }
        }

        // After OpenBuildMenu shows the menu, so nothing that localises it on enable writes over the labels.
        private static void ApplyLabels()
        {
            for (int page = 0; page < ReplaceablePages; page++)
            {
                var texts = s_tabTexts[page];
                if (texts == null) continue;
                string label = s_labels[page] != null ? BuildMenuTabs.Localize(s_labels[page]) : null;
                for (int i = 0; i < texts.Length; i++)
                    if (texts[i] != null) texts[i].text = label ?? s_vanillaLabels[page][i];
            }
        }

        private static void Capture(BuildUi ui, List<IPieceList> lists)
        {
            s_capturedFor = ui;
            var buttons = s_tabButtons != null ? s_tabButtons(ui) : null;
            for (int page = 0; page < ReplaceablePages; page++)
            {
                s_vanillaLists[page] = lists[page];
                var button = buttons != null && page < buttons.Count ? buttons[page] : null;
                var texts = button != null ? button.GetComponentsInChildren<TMP_Text>(true) : null;
                s_tabTexts[page] = texts;
                s_vanillaLabels[page] = new string[texts?.Length ?? 0];
                for (int i = 0; i < s_vanillaLabels[page].Length; i++) s_vanillaLabels[page][i] = texts[i] != null ? texts[i].text : null;
            }
        }

        private static ISource SourceFor(PieceTable table)
        {
            if (table == null) return null;
            foreach (var source in s_sources)
            {
                bool owns;
                try { owns = source.Owns(table); }
                catch (Exception ex) { Warn(source, "Owns", ex); continue; }
                if (owns) return source;
            }
            return null;
        }

        private static string LabelOf(ISource source, int page)
        {
            try { return source.PageLabel(page); }
            catch (Exception ex) { Warn(source, "PageLabel", ex); return null; }
        }

        private static readonly HashSet<string> s_warned = new HashSet<string>();

        private static void Warn(ISource source, string call, Exception ex)
        {
            string key = source.GetType().FullName + "." + call;
            if (s_warned.Add(key))
                Logging.FiresLogger.LogWarning($"[BuildMenuPages] {key} threw ({ex.GetType().Name}: {ex.Message}); its page is skipped.");
        }

        private sealed class PageList : IPieceList
        {
            private readonly int m_page;
            private readonly List<BuildMenuTabs.Tab> m_rows = new List<BuildMenuTabs.Tab>();
            private readonly List<Piece> m_fill = new List<Piece>();
            private readonly HashSet<Piece> m_added = new HashSet<Piece>();
            private ISource m_source;

            internal PageList(int page) => m_page = page;

            internal void Bind(ISource source)
            {
                if (!ReferenceEquals(source, m_source)) m_rows.Clear();
                m_source = source;
            }

            public string DisplayName => m_source != null ? LabelOf(m_source, m_page) : null;
            public bool ShowTags => true;
            public bool CanCustomizeTags => false;
            public int TagCount => m_rows.Count;
            public int TagSeparatorIndex => NoSeparator;
            public string GetTagDisplayName(int index) => m_rows[index].Label;
            public int GetTagIdByIndex(int index) => BuildMenuTabs.TagIdFor(m_rows[index].Key);

            public void UpdateAvailableTags(PieceTable pieceTable)
            {
                m_rows.Clear();
                if (m_source == null || pieceTable == null) return;
                try { m_source.CollectTabs(pieceTable, m_page, m_rows); }
                catch (Exception ex) { Warn(m_source, "CollectTabs", ex); m_rows.Clear(); }
            }

            public void GetAvailablePiecesWithTag(int tagId, PieceTable pieceTable, IList<Piece> resultOut)
            {
                if (pieceTable == null || resultOut == null) return;
                m_added.Clear();
                foreach (var piece in pieceTable.m_availablePieces)
                    if (piece != null && (piece.m_repairPiece || piece.m_removePiece) && m_added.Add(piece)) resultOut.Add(piece);
                if (m_source == null) return;
                if (m_rows.Count == 0) UpdateAvailableTags(pieceTable);
                if (tagId == AllTags)
                    foreach (var row in m_rows) Add(pieceTable, row.Key, resultOut);
                else if (tagId >= BuildMenuTabs.TagIdBase)
                    Add(pieceTable, tagId - BuildMenuTabs.TagIdBase, resultOut);
                m_added.Clear();
            }

            private void Add(PieceTable table, int key, IList<Piece> into)
            {
                m_fill.Clear();
                try { m_source.Fill(table, m_page, key, m_fill); }
                catch (Exception ex) { Warn(m_source, "Fill", ex); }
                foreach (var piece in m_fill)
                    if (piece != null && table.m_availablePieces.Contains(piece) && m_added.Add(piece)) into.Add(piece);
                m_fill.Clear();
            }
        }

        [HarmonyPatch(typeof(BuildUi), nameof(BuildUi.OpenBuildMenu))]
        private static class BuildUi_OpenBuildMenu_Patch
        {
            private static void Prefix(BuildUi __instance) => ApplyLists(__instance);

            private static void Postfix() => ApplyLabels();
        }
    }
}
