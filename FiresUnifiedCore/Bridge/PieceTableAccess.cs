using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Reads PieceTable's per-category piece lists. Valheim 1.0 turned m_availablePieces into a flat HashSet and
    /// moved the per-category lists into the private m_availablePiecesByCategory, which compiles against the
    /// publicized assembly but throws FieldAccessException at runtime, so it is read through AccessTools. Several
    /// Fires mods need it, hence Core.
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
