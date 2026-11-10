using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// One achievement as a reader sees it, whatever system owns it. Progress is optional: a source that
    /// only knows earned-or-not leaves <see cref="Target"/> at zero and a reader shows no bar.
    /// </summary>
    public class AchievementRow
    {
        public string Id = "";
        public string Name = "";
        public string Description = "";
        public string Group = "";
        public int Progress;
        public int Target;
        public bool Earned;

        /// <summary>Hide the name and description until it is earned.</summary>
        public bool Secret;

        /// <summary>Tier or score, for ordering and for the reward a source attaches to it. Zero when unscored.</summary>
        public int Score;

        public Color Accent = DefaultAccent;
        public Sprite Icon;

        /// <summary>Set when the source knows this one can never be earned in the current session, and why.</summary>
        public string Blocked = "";

        /// <summary>Add-only: the source's kind of achievement (RPGmaker: its trigger type, e.g. "MonstersKilled") for a
        /// per-kind glyph or grouping. Null = none.</summary>
        public string Kind;

        public bool HasProgress => Target > 0;

        public static readonly Color DefaultAccent = new Color(190f / 255f, 190f / 255f, 190f / 255f, 1f);
    }

    /// <summary>
    /// The read seam over every achievement catalogue in the stack. A mod that OWNS achievements registers
    /// a catalogue; a mod that DISPLAYS them reads every registered one. Same optional-coupling model as
    /// <see cref="ProgressEvents"/> and <see cref="DiscordSink"/>: with no catalogue registered a reader
    /// sees an empty stack and shows nothing, so neither side needs a reference to the other.
    ///
    /// This is deliberately read-only and pull-based. Nothing here awards, unlocks or persists an
    /// achievement - the owning mod keeps that authority, and announces a crossing through
    /// <see cref="ProgressEvents.RaiseAchievementCompleted"/>.
    ///
    /// CLIENT-side. A catalogue reader may touch <c>Player.m_localPlayer</c> and vanilla GUI state, so
    /// registration is the owner's job to gate on headless.
    /// </summary>
    /// <summary>Add-only: one catalogue's totals for a stats header. Supplied by the owner through
    /// <see cref="AchievementBridge.SetCatalogueStats"/>, else counted from its rows (Score = Earned).</summary>
    public sealed class AchievementStats
    {
        public int Earned;
        public int Total;
        public int Score;
        public Dictionary<string, int> ByKind;
    }

    public static class AchievementBridge
    {
        private class Catalogue
        {
            public string Id = "";
            public string DisplayName = "";
            public Func<IReadOnlyList<AchievementRow>> Read;
            public int Order;
            public Func<AchievementStats> Stats;   // optional
        }

        private static readonly List<Catalogue> Catalogues = new List<Catalogue>();
        private static readonly IReadOnlyList<AchievementRow> Empty = new List<AchievementRow>();

        /// <summary>Raised when a catalogue registers or drops out, so an open achievements screen rebuilds.</summary>
        public static event Action CataloguesChanged;

        /// <summary>
        /// Offer a catalogue under a stable id. <paramref name="order"/> sorts the sections a reader shows,
        /// lowest first. Registering the same id twice replaces the reader rather than duplicating it.
        /// </summary>
        public static void RegisterCatalogue(string sourceId, string displayName,
            Func<IReadOnlyList<AchievementRow>> read, int order = 100)
        {
            if (string.IsNullOrEmpty(sourceId) || read == null) return;

            Catalogue existing = Find(sourceId);
            if (existing != null)
            {
                existing.DisplayName = displayName ?? sourceId;
                existing.Read = read;
                existing.Order = order;
            }
            else
            {
                Catalogues.Add(new Catalogue
                {
                    Id = sourceId,
                    DisplayName = displayName ?? sourceId,
                    Read = read,
                    Order = order
                });
                Catalogues.Sort((a, b) => a.Order.CompareTo(b.Order));
            }

            Raise();
        }

        public static void UnregisterCatalogue(string sourceId)
        {
            Catalogue existing = Find(sourceId);
            if (existing == null) return;
            Catalogues.Remove(existing);
            Raise();
        }

        public static bool HasCatalogue(string sourceId) => Find(sourceId) != null;

        public static int CatalogueCount => Catalogues.Count;

        /// <summary>Registered catalogue ids in display order.</summary>
        public static IEnumerable<string> SourceIds
        {
            get
            {
                foreach (Catalogue catalogue in Catalogues) yield return catalogue.Id;
            }
        }

        public static string DisplayNameOf(string sourceId) => Find(sourceId)?.DisplayName ?? sourceId;

        /// <summary>
        /// One catalogue's rows. A reader that throws costs its own section and one warning, never the
        /// screen - a display path must not be able to take the panel down.
        /// </summary>
        public static IReadOnlyList<AchievementRow> Read(string sourceId)
        {
            Catalogue catalogue = Find(sourceId);
            if (catalogue == null) return Empty;

            try
            {
                return catalogue.Read() ?? Empty;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AchievementBridge] {catalogue.Id} could not be read "
                    + $"({ex.GetType().Name}: {ex.Message}).");
                return Empty;
            }
        }

        /// <summary>Add-only: attach a stats provider to a registered catalogue (null clears it). Call after RegisterCatalogue.</summary>
        public static void SetCatalogueStats(string sourceId, Func<AchievementStats> stats)
        {
            Catalogue catalogue = Find(sourceId);
            if (catalogue == null) { Debug.LogWarning($"[AchievementBridge] SetCatalogueStats: no catalogue '{sourceId}' (register it first)"); return; }
            catalogue.Stats = stats;
            Debug.Log($"[AchievementBridge] stats provider {(stats != null ? "set" : "cleared")} for '{sourceId}'");
            Raise();
        }

        /// <summary>The catalogue's stats - its provider's when set (a throw falls back), else counted from the rows.</summary>
        public static AchievementStats Stats(string sourceId)
        {
            Catalogue catalogue = Find(sourceId);
            if (catalogue == null) return new AchievementStats();
            if (catalogue.Stats != null)
            {
                try { var s = catalogue.Stats(); if (s != null) return s; }
                catch (Exception ex) { Debug.LogWarning($"[AchievementBridge] {sourceId} stats provider threw ({ex.GetType().Name}: {ex.Message}); counting rows"); }
            }
            var rows = Read(sourceId);
            var st = new AchievementStats { Total = rows.Count };
            for (int i = 0; i < rows.Count; i++) if (rows[i] != null && rows[i].Earned) st.Earned++;
            st.Score = st.Earned;
            return st;
        }

        public static int EarnedCount(string sourceId)
        {
            int earned = 0;
            IReadOnlyList<AchievementRow> rows = Read(sourceId);
            for (int i = 0; i < rows.Count; i++)
                if (rows[i] != null && rows[i].Earned) earned++;
            return earned;
        }

        private static Catalogue Find(string sourceId)
        {
            if (string.IsNullOrEmpty(sourceId)) return null;
            foreach (Catalogue catalogue in Catalogues)
                if (catalogue.Id == sourceId) return catalogue;
            return null;
        }

        private static void Raise()
        {
            try { CataloguesChanged?.Invoke(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[AchievementBridge] CataloguesChanged subscriber threw: {ex.Message}");
            }
        }
    }
}
