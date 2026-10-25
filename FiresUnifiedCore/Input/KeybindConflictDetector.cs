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
            // A key that is ALSO a modifier of other
            // bindings, or a modifier combo beside its bare key, is not a clash in play. Only the identical full combination in
            // overlapping contexts (SameKey) counts. The other two rules still run, for the count in the log line only.
            var informational = new List<KeybindConflict>();
            DetectOverlap(candidates, informational);
            DetectModifierInUse(candidates, informational);
            LastIgnoredCount = informational.Count;
            conflicts.Sort(Compare);
            return conflicts;
        }

        /// <summary>How many modifier-only / different-combo overlaps the last Detect skipped (for the log line).</summary>
        public static int LastIgnoredCount { get; private set; }

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

        // Two keys of the SAME mod never clash (the mod decides which of its own modes is live; FiresValcast listed itself
        // ten times). Valheim's own buttons have no plugin GUID and still clash with each other as before.
        private static bool Clashes(KeybindEntry a, KeybindEntry b)
            => BindingContexts.Overlap(a.Context, b.Context) && !IsHandedOff(a, b) && !SameMod(a, b) && !DeclaredShared(a, b);

        // Mods that use one key with different gestures (a double-tap vs a hold vs a hold-while-aiming) and so never fire
        // together. Keyed by key + mod display name. A mod (or Core, for its own family) declares the group; then those mods' binds on
        // that key stop showing as conflicts. A user who rebinds one of them to another key simply leaves the group.
        private static readonly Dictionary<KeyCode, List<HashSet<string>>> s_shared = new Dictionary<KeyCode, List<HashSet<string>>>
        {
            // H: FiresMounts whistle = double-tap; FiresCompanions idle wheel = hold while aiming at your companion; FDT = test-only.
            [KeyCode.H] = new List<HashSet<string>> { new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FiresMounts", "FiresCompanions", "FiresDebugginTools" } },
            // R: FiresCompanions command wheel = hold while aiming at your companion; FiresAllTheItems = its own tap.
            [KeyCode.R] = new List<HashSet<string>> { new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FiresAllTheItems", "FiresCompanions" } },
        };

        /// <summary>Declares that these mods share <paramref name="key"/> by gesture, so their binds on it are not conflicts.</summary>
        public static void DeclareShared(KeyCode key, params string[] modNames)
        {
            if (modNames == null || modNames.Length < 2) return;
            if (!s_shared.TryGetValue(key, out var groups)) s_shared[key] = groups = new List<HashSet<string>>();
            groups.Add(new HashSet<string>(modNames, StringComparer.OrdinalIgnoreCase));
            KeybindRegistry.Invalidate();
        }

        private static bool DeclaredShared(KeybindEntry a, KeybindEntry b)
        {
            if (a.Key != b.Key || !s_shared.TryGetValue(a.Key, out var groups)) return false;
            foreach (var group in groups)
                if (group.Contains(a.ModName ?? "") && group.Contains(b.ModName ?? "")) return true;
            return false;
        }

        private static bool SameMod(KeybindEntry a, KeybindEntry b)
            => a.Origin != BindingOrigin.VanillaButton && b.Origin != BindingOrigin.VanillaButton
            && !string.IsNullOrEmpty(a.PluginGuid) && string.Equals(a.PluginGuid, b.PluginGuid, StringComparison.Ordinal);

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
