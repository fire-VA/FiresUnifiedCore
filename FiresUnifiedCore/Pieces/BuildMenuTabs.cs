using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Pieces
{
    // Named tabs on the 1.0 build menu for a piece table.
    //
    // 1.0 dropped PieceCategory tabs: the menu's left-hand tag list is ByUsagePieceList, built only from the
    // Piece.m_usage flags of the available pieces, so a mod's own categories collapse into Show All. A new IPieceList
    // would need a tab button grafted into BuildUi's m_tabContainer, which maps tab index to list index by position,
    // so named rows are appended to the list vanilla already renders instead, after its usage tags.
    //
    // A source answers for the tables it owns and names their rows; the first registered source that owns the open
    // table is the one asked. Row tag ids are TagIdBase + the source's own key, a private id space that cannot collide
    // with vanilla's enum indices and stays stable while the menu is open. Moved here from FAP's
    // VABuildUiCategoryTags on 2026-09-27 so FAP's hammer and FAT's Chaos Hammer share one implementation.
    public static class BuildMenuTabs
    {
        public const int TagIdBase = 100000;

        // BuildUi's own starting value, which no real piece count matches.
        private const int UnseenPieceCount = -1;
        private const int NoSeparator = -1;

        public readonly struct Tab
        {
            public readonly string Label;
            public readonly int Key;

            public Tab(string label, int key)
            {
                Label = label;
                Key = key;
            }
        }

        public interface ISource
        {
            bool Owns(PieceTable table);

            // The table's rows, in display order. Keys are the source's own and must be unique within the table.
            void CollectTabs(PieceTable table, List<Tab> into);

            // The pieces of one row. Repair and remove tools are added by Core, never by the source.
            void Fill(PieceTable table, int key, IList<Piece> into);
        }

        private static readonly List<ISource> s_sources = new List<ISource>();
        private static readonly List<Tab> s_rows = new List<Tab>();
        private static readonly List<Tab> s_collect = new List<Tab>();
        private static ISource s_current;
        private static int s_vanillaCount;

        private static AccessTools.FieldRef<ByUsagePieceList, List<int>> s_availableTags;
        private static bool s_availableTagsResolved;
        private static AccessTools.FieldRef<BuildUi, int> s_availablePieceCount;
        private static bool s_availablePieceCountResolved;
        private static AccessTools.FieldRef<BuildUi, int> s_currentTagId;
        private static bool s_currentTagIdResolved;

        public static void Register(ISource source)
        {
            if (source != null && !s_sources.Contains(source)) s_sources.Add(source);
        }

        public static int TagIdFor(int key) => TagIdBase + key;

        public static int RowCount => s_rows.Count;

        // Vanilla's usage tags the menu lists for the open table, as displayed, so a source can leave out a row that
        // would only repeat one of them. Valid inside CollectTabs.
        public static IReadOnlyList<string> ListedVanillaTags => s_vanillaNames;

        private static readonly List<string> s_vanillaNames = new List<string>();

        // The key of the row the open build menu shows, when it is one of a source's rows.
        public static bool TryGetSelectedKey(out int key)
        {
            key = 0;
            var buildUi = Hud.instance != null ? Hud.instance.m_buildUi : null;
            if (buildUi == null) return false;
            if (!s_currentTagIdResolved)
            {
                s_currentTagIdResolved = true;
                s_currentTagId = Resolve<BuildUi, int>("m_currentTagId");
            }
            if (s_currentTagId == null) return false;
            int tagId = s_currentTagId(buildUi);
            if (tagId < TagIdBase) return false;
            key = tagId - TagIdBase;
            return true;
        }

        // BuildUi rebuilds its tags and piece buttons on the next Update whenever the available-piece count it last saw
        // differs from the table's. A rename or a move between rows leaves that count unchanged, so it is cleared.
        public static void RequestRefresh()
        {
            var buildUi = Hud.instance != null ? Hud.instance.m_buildUi : null;
            if (buildUi == null) return;
            if (!s_availablePieceCountResolved)
            {
                s_availablePieceCountResolved = true;
                s_availablePieceCount = Resolve<BuildUi, int>("m_availablePieceCount");
            }
            if (s_availablePieceCount != null) s_availablePieceCount(buildUi) = UnseenPieceCount;
        }

        private static AccessTools.FieldRef<TOwner, TField> Resolve<TOwner, TField>(string field)
        {
            try { return AccessTools.FieldRefAccess<TOwner, TField>(field); }
            catch (Exception ex)
            {
                Logging.FiresLogger.LogWarning($"[BuildMenuTabs] {typeof(TOwner).Name}.{field} did not resolve ({ex.GetType().Name}): "
                    + "named build-menu tabs may not refresh until the menu is reopened.");
                return null;
            }
        }

        // Runs right after vanilla rebuilds m_availableTags and right before BuildUi reads 0..TagCount by index, so the
        // vanilla count captured here is exactly the boundary the index lookups below need.
        private static void Refresh(ByUsagePieceList list, PieceTable table)
        {
            s_rows.Clear();
            s_current = null;
            s_vanillaCount = VanillaTagCount(list);
            if (s_vanillaCount < 0 || table == null)
            {
                s_vanillaCount = Mathf.Max(0, s_vanillaCount);
                return;
            }
            s_vanillaNames.Clear();
            for (int i = 0; i < s_vanillaCount; i++) s_vanillaNames.Add(Localize(list.GetTagDisplayName(i)));
            foreach (var source in s_sources)
            {
                bool owns;
                try { owns = source.Owns(table); }
                catch (Exception ex) { Warn(source, "Owns", ex); continue; }
                if (!owns) continue;
                s_current = source;
                s_collect.Clear();
                try { source.CollectTabs(table, s_collect); }
                catch (Exception ex) { Warn(source, "CollectTabs", ex); s_collect.Clear(); }
                s_rows.AddRange(s_collect);
                return;
            }
        }

        private static int VanillaTagCount(ByUsagePieceList list)
        {
            if (!s_availableTagsResolved)
            {
                s_availableTagsResolved = true;
                s_availableTags = Resolve<ByUsagePieceList, List<int>>("m_availableTags");
            }
            if (s_availableTags == null || list == null) return -1;
            var tags = s_availableTags(list);
            return tags != null ? tags.Count : -1;
        }

        public static string Localize(string text)
        {
            if (string.IsNullOrEmpty(text) || text[0] != '$' || Localization.instance == null) return text;
            return Localization.instance.Localize(text);
        }

        private static bool IsRow(int index) => index >= s_vanillaCount && index - s_vanillaCount < s_rows.Count;

        private static bool Fill(int tagId, PieceTable table, IList<Piece> into)
        {
            if (tagId < TagIdBase || s_current == null || table == null || into == null) return false;
            // Repair and remove are tool actions, not row members: vanilla emits them for every tag and BuildUi
            // re-parents them into its special-buttons container.
            foreach (var piece in table.m_availablePieces)
                if (piece != null && (piece.m_repairPiece || piece.m_removePiece)) into.Add(piece);
            try { s_current.Fill(table, tagId - TagIdBase, into); }
            catch (Exception ex) { Warn(s_current, "Fill", ex); }
            return true;
        }

        private static readonly HashSet<string> s_warned = new HashSet<string>();

        private static void Warn(ISource source, string call, Exception ex)
        {
            string key = source.GetType().FullName + "." + call;
            if (s_warned.Add(key))
                Logging.FiresLogger.LogWarning($"[BuildMenuTabs] {key} threw ({ex.GetType().Name}: {ex.Message}); its tabs are skipped.");
        }

        [HarmonyPatch(typeof(ByUsagePieceList), nameof(ByUsagePieceList.UpdateAvailableTags))]
        private static class ByUsagePieceList_UpdateAvailableTags_Patch
        {
            private static void Postfix(ByUsagePieceList __instance, PieceTable pieceTable) => Refresh(__instance, pieceTable);
        }

        // Getters are named as methods ("get_X") so Tools\Verify-HarmonyTargets.ps1 can resolve them statically.
        private const string GetterPrefix = "get_";

        [HarmonyPatch(typeof(ByUsagePieceList), GetterPrefix + nameof(ByUsagePieceList.TagCount))]
        private static class ByUsagePieceList_TagCount_Patch
        {
            private static void Postfix(ref int __result) => __result += s_rows.Count;
        }

        [HarmonyPatch(typeof(ByUsagePieceList), nameof(ByUsagePieceList.GetTagDisplayName))]
        private static class ByUsagePieceList_GetTagDisplayName_Patch
        {
            private static bool Prefix(int index, ref string __result)
            {
                if (!IsRow(index)) return true;
                __result = s_rows[index - s_vanillaCount].Label;
                return false;
            }
        }

        [HarmonyPatch(typeof(ByUsagePieceList), nameof(ByUsagePieceList.GetTagIdByIndex))]
        private static class ByUsagePieceList_GetTagIdByIndex_Patch
        {
            private static bool Prefix(int index, ref int __result)
            {
                if (!IsRow(index)) return true;
                __result = TagIdFor(s_rows[index - s_vanillaCount].Key);
                return false;
            }
        }

        [HarmonyPatch(typeof(ByUsagePieceList), nameof(ByUsagePieceList.GetAvailablePiecesWithTag))]
        private static class ByUsagePieceList_GetAvailablePiecesWithTag_Patch
        {
            private static bool Prefix(int tagId, PieceTable pieceTable, IList<Piece> resultOut) => !Fill(tagId, pieceTable, resultOut);
        }

        // ByUsagePieceList has no separator of its own. With named rows appended there is a real boundary worth
        // drawing: vanilla's usage tags above, the source's rows below.
        [HarmonyPatch(typeof(ByUsagePieceList), GetterPrefix + nameof(ByUsagePieceList.TagSeparatorIndex))]
        private static class ByUsagePieceList_TagSeparatorIndex_Patch
        {
            private static void Postfix(ref int __result)
            {
                if (__result == NoSeparator && s_rows.Count > 0) __result = s_vanillaCount;
            }
        }
    }
}
