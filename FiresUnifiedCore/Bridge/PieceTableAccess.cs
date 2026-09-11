using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// The one place the family reads <see cref="PieceTable"/>'s per-category piece lists.
    ///
    /// Valheim 1.0 SPLIT the old field in two:
    ///   pre-1.0  <c>m_availablePieces</c>            : <c>List&lt;List&lt;Piece&gt;&gt;</c>  (per category)
    ///   1.0      <c>m_availablePieces</c>            : <c>HashSet&lt;Piece&gt;</c>            (FLAT, public readonly)
    ///   1.0      <c>m_availablePiecesByCategory</c>  : <c>List&lt;List&lt;Piece&gt;&gt;</c>  (per category, PRIVATE)
    ///
    /// So code that indexed <c>m_availablePieces[(int)category]</c> now wants
    /// <c>m_availablePiecesByCategory</c> — and that field is <b>private</b>. Reading it directly
    /// compiles fine against the publicized assembly and then throws
    /// <see cref="FieldAccessException"/> on first touch at runtime, which inside a try/catch is
    /// the silent-feature-death mode. Hence AccessTools.
    ///
    /// Five Fires mods index this per-category list (AdminPrefabs, DungeonMaster, RPGmaker,
    /// Mausoleum, VikingLands), which is why it lives in Core instead of being copied five times.
    /// </summary>
    public static class PieceTableAccess
    {
        private static AccessTools.FieldRef<PieceTable, List<List<Piece>>> _byCategory;
        private static bool _resolved;
        private static bool _warned;

        private static AccessTools.FieldRef<PieceTable, List<List<Piece>>> Ref()
        {
            if (_resolved) return _byCategory;
            _resolved = true;
            try
            {
                _byCategory = AccessTools.FieldRefAccess<PieceTable, List<List<Piece>>>("m_availablePiecesByCategory");
            }
            catch (Exception ex)
            {
                // Resolved lazily rather than in a static initializer on purpose: a throwing
                // static ctor becomes TypeInitializationException on every later call and buries
                // the real cause.
                _byCategory = null;
                if (!_warned)
                {
                    _warned = true;
                    Debug.LogWarning($"[PieceTableAccess] PieceTable.m_availablePiecesByCategory did not resolve — " +
                                     $"per-category piece lists are unavailable this session ({ex.GetType().Name}: {ex.Message}). " +
                                     $"A game patch has probably renamed it again.");
                }
            }
            return _byCategory;
        }

        /// <summary>
        /// The per-category piece lists, indexed by <c>(int)Piece.PieceCategory</c>.
        /// Null when <paramref name="table"/> is null or the field could not be resolved —
        /// callers already null-check this collection, so a rename degrades to "no pieces"
        /// rather than an exception storm.
        /// </summary>
        public static List<List<Piece>> ByCategory(PieceTable table)
        {
            if (table == null) return null;
            var fieldRef = Ref();
            return fieldRef == null ? null : fieldRef(table);
        }

        /// <summary>
        /// 1.0's flat set of every available piece regardless of category. Public in vanilla, so
        /// this needs no reflection; exposed here purely so both halves of the split have one
        /// obvious home.
        /// </summary>
        public static HashSet<Piece> Flat(PieceTable table) => table == null ? null : table.m_availablePieces;
    }
}
