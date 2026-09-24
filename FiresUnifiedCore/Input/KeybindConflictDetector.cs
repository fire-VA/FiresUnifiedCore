using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace FiresCore.Input
{
    public enum ConflictKind
    {
        SameKey,
        Overlap,
        ModifierInUse,
    }

    /// <summary>One detected clash: the combination it is about, its kind, and every binding involved.</summary>
    public sealed class KeybindConflict
    {
        public ConflictKind Kind;
        public KeyCode Key;
        public KeyModifiers Modifiers;
        public KeyModifiers ModifierInUse;
        public List<KeybindEntry> Members = new List<KeybindEntry>();

        public string Combination => Kind == ConflictKind.ModifierInUse
            ? KeyCombination.Describe(ModifierInUse)
            : KeyCombination.Describe(Key, Modifiers);

        public string KindLabel
        {
            get
            {
                switch (Kind)
                {
                    case ConflictKind.SameKey: return "Same key";
                    case ConflictKind.Overlap: return "Overlap";
                    case ConflictKind.ModifierInUse: return "Modifier in use";
                    default: return Kind.ToString();
                }
            }
        }

        /// <summary>
        /// Kind plus the sorted identity+combination of every member. Any member rebinding changes this, so an
        /// ignore can never silence a situation the player has not seen; rebinding back restores it.
        /// </summary>
        public string Signature
        {
            get
            {
                var parts = new List<string>(Members.Count);
                foreach (var member in Members) parts.Add(member.SignaturePart);
                parts.Sort(StringComparer.Ordinal);
                var text = new StringBuilder(Kind.ToString());
                if (Kind == ConflictKind.ModifierInUse) text.Append(':').Append(ModifierInUse);
                foreach (string part in parts) text.Append('|').Append(part);
                return text.ToString();
            }
        }

    }

    /// <summary>
    /// The three conflict rules from the design, and nothing else - no heuristics. Reads
    /// <see cref="KeybindRegistry.Entries"/> and returns the clashes in a stable order.
    /// </summary>
    public static class KeybindConflictDetector
    {
        public static List<KeybindConflict> Detect() => Detect(KeybindRegistry.Entries);

        public static List<KeybindConflict> Detect(IReadOnlyList<KeybindEntry> entries)
        {
            var candidates = new List<KeybindEntry>();
            foreach (var entry in entries)
                if (entry.IsBound && !entry.IsChord) candidates.Add(entry);

            var conflicts = new List<KeybindConflict>();
            DetectSameKey(candidates, conflicts);
            DetectOverlap(candidates, conflicts);
            DetectModifierInUse(candidates, conflicts);
            conflicts.Sort(Compare);
            return conflicts;
        }

        private static int Compare(KeybindConflict a, KeybindConflict b)
        {
            int byKind = a.Kind.CompareTo(b.Kind);
            if (byKind != 0) return byKind;
            return string.Compare(a.Combination, b.Combination, StringComparison.OrdinalIgnoreCase);
        }

        // Identical key AND identical modifiers. Two STRICT bindings on the same key with DIFFERENT modifiers
        // never reach here, which is the design's "not a conflict".
        private static void DetectSameKey(List<KeybindEntry> candidates, List<KeybindConflict> into)
        {
            var groups = new Dictionary<string, List<KeybindEntry>>(StringComparer.Ordinal);
            foreach (var entry in candidates)
            {
                string key = entry.Key + "/" + (int)entry.Modifiers;
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new List<KeybindEntry>();
                    groups[key] = group;
                }
                group.Add(entry);
            }

            foreach (var group in groups.Values)
            {
                if (group.Count < 2) continue;
                var members = MembersThatClashWithinGroup(group);
                if (members.Count < 2) continue;
                into.Add(new KeybindConflict
                {
                    Kind = ConflictKind.SameKey,
                    Key = members[0].Key,
                    Modifiers = members[0].Modifiers,
                    Members = members,
                });
            }
        }

        // A member whose context overlaps nothing else in its group is listed nowhere rather than reported
        // beside bindings it cannot actually collide with.
        private static List<KeybindEntry> MembersThatClashWithinGroup(List<KeybindEntry> group)
        {
            var members = new List<KeybindEntry>();
            for (int i = 0; i < group.Count; i++)
            {
                for (int j = 0; j < group.Count; j++)
                {
                    if (i == j) continue;
                    if (!Clashes(group[i], group[j])) continue;
                    members.Add(group[i]);
                    break;
                }
            }
            return members;
        }

        // Same key, different modifier sets, and the loose participant fires on every combination of that key.
        private static void DetectOverlap(List<KeybindEntry> candidates, List<KeybindConflict> into)
        {
            foreach (var loose in candidates)
            {
                if (loose.Match != BindingMatch.Loose || loose.Modifiers != KeyModifiers.None) continue;

                var members = new List<KeybindEntry>();
                foreach (var other in candidates)
                {
                    if (ReferenceEquals(other, loose)) continue;
                    if (other.Key != loose.Key) continue;
                    if (other.Modifiers == KeyModifiers.None) continue;
                    if (!Clashes(loose, other)) continue;
                    members.Add(other);
                }
                if (members.Count == 0) continue;

                members.Insert(0, loose);
                into.Add(new KeybindConflict
                {
                    Kind = ConflictKind.Overlap,
                    Key = loose.Key,
                    Modifiers = KeyModifiers.None,
                    Members = members,
                });
            }
        }

        // A binding's modifier key is itself bound, loosely, to something else - so every press of the binding
        // also fires that something.
        private static void DetectModifierInUse(List<KeybindEntry> candidates, List<KeybindConflict> into)
        {
            foreach (var entry in candidates)
            {
                if (entry.Modifiers == KeyModifiers.None) continue;

                foreach (var modifier in KeyCombination.Flags)
                {
                    if ((entry.Modifiers & modifier) == 0) continue;
                    var physicalKeys = KeyCombination.KeysOf(modifier);

                    var members = new List<KeybindEntry>();
                    foreach (var other in candidates)
                    {
                        if (ReferenceEquals(other, entry)) continue;
                        if (other.Match != BindingMatch.Loose || other.Modifiers != KeyModifiers.None) continue;
                        if (!ContainsKey(physicalKeys, other.Key)) continue;
                        if (!Clashes(entry, other)) continue;
                        members.Add(other);
                    }
                    if (members.Count == 0) continue;

                    members.Insert(0, entry);
                    into.Add(new KeybindConflict
                    {
                        Kind = ConflictKind.ModifierInUse,
                        Key = entry.Key,
                        Modifiers = entry.Modifiers,
                        ModifierInUse = modifier,
                        Members = members,
                    });
                }
            }
        }

        private static bool ContainsKey(IReadOnlyList<KeyCode> keys, KeyCode key)
        {
            for (int i = 0; i < keys.Count; i++)
                if (keys[i] == key) return true;
            return false;
        }

        private static bool Clashes(KeybindEntry a, KeybindEntry b)
            => BindingContexts.Overlap(a.Context, b.Context) && !IsHandedOff(a, b);

        /// <summary>
        /// True when either side declared that it replaces the other's vanilla button in an overlapping
        /// context. Core only agrees to stop calling it a conflict; actually suppressing the vanilla button is
        /// the declaring mod's job.
        /// </summary>
        public static bool IsHandedOff(KeybindEntry a, KeybindEntry b)
            => DeclaresReplacementOf(a, b) || DeclaresReplacementOf(b, a);

        private static bool DeclaresReplacementOf(KeybindEntry declarer, KeybindEntry target)
        {
            if (target.Origin != BindingOrigin.VanillaButton) return false;
            foreach (var replacement in declarer.DeclaredReplacements)
            {
                if (!string.Equals(replacement.ButtonName, target.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (BindingContexts.Overlap(replacement.Context, target.Context)) return true;
            }
            return false;
        }

        /// <summary>The one-line startup / log-bot summary: "3 conflicts: F (Emote Wheel, Valheim); ...".</summary>
        public static string Summarize(IReadOnlyList<KeybindConflict> conflicts)
        {
            if (conflicts.Count == 0) return "no conflicts";
            var text = new StringBuilder();
            text.Append(conflicts.Count).Append(conflicts.Count == 1 ? " conflict: " : " conflicts: ");
            for (int i = 0; i < conflicts.Count; i++)
            {
                if (i > 0) text.Append("; ");
                var conflict = conflicts[i];
                text.Append(conflict.Combination).Append(" (");
                for (int m = 0; m < conflict.Members.Count; m++)
                {
                    if (m > 0) text.Append(", ");
                    text.Append(conflict.Members[m].ModName);
                }
                text.Append(')');
            }
            return text.ToString();
        }
    }
}
