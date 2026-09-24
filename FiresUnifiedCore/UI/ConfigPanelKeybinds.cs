using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using FiresCore.Input;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// The config window's keybind-conflict surface: the "Conflicts" view, the "All bound keys" keymap, and the
    /// warning key capture shows before it commits a combination somebody else is already listening for.
    /// Plan: Docs/PLAN_KeybindConflicts.md
    /// </summary>
    public partial class ConfigPanel
    {
        private const float ConflictActionWidthBase = 62f;
        private const float ConflictIgnoreWidthBase = 74f;
        private const float ConflictChipWidthBase = 118f;
        private const float ConflictContextWidthBase = 132f;
        private const float ConflictModWidthBase = 210f;
        private const float ConflictKeyColumnWidthBase = 120f;
        private const float ConfirmActionWidthBase = 86f;
        private const string ConflictColor = "#FF9C6E";
        private const string HandledColor = "#8FD98F";
        private const string InfoColor = "#9AA0A6";

        // The markup a row and a group header draw. Built once per scan instead of per IMGUI event: the text
        // reads from the entry, and both KeybindRegistry.Unbind and .Apply call Invalidate(), so a rebind
        // raises ScanVersion and the next draw rebuilds. An ignored conflict keeps both header variants, so
        // toggling Ignore needs no invalidation at all.
        private sealed class ConflictRowMarkup
        {
            public string Detail;
            public string Hint;
            public string Combination;
            public string ReadOnlyHint;
        }

        private sealed class ConflictHeaderMarkup
        {
            public string Active;
            public string Ignored;
        }

        private readonly Dictionary<string, ConflictRowMarkup> _rowMarkup =
            new Dictionary<string, ConflictRowMarkup>(StringComparer.Ordinal);
        private readonly Dictionary<string, ConflictHeaderMarkup> _headerMarkup =
            new Dictionary<string, ConflictHeaderMarkup>(StringComparer.Ordinal);
        private int _markupScanVersion = -1;

        private void EnsureMarkupForCurrentScan()
        {
            if (_markupScanVersion == KeybindRegistry.ScanVersion) return;
            _markupScanVersion = KeybindRegistry.ScanVersion;
            _rowMarkup.Clear();
            _headerMarkup.Clear();
        }

        private ConflictRowMarkup MarkupOf(KeybindEntry member)
        {
            EnsureMarkupForCurrentScan();
            if (_rowMarkup.TryGetValue(member.Id, out var markup)) return markup;
            markup = new ConflictRowMarkup
            {
                Detail = $"<color={InfoColor}>{MemberDetail(member)}</color>",
                Hint = member.Section + " / " + member.Name,
                Combination = $"<b>{member.Combination}</b>",
                ReadOnlyHint = $"<color={InfoColor}>{MemberReadOnlyHint(member)}</color>",
            };
            _rowMarkup[member.Id] = markup;
            return markup;
        }

        private ConflictHeaderMarkup MarkupOf(KeybindConflict conflict)
        {
            EnsureMarkupForCurrentScan();
            if (_headerMarkup.TryGetValue(conflict.Signature, out var markup)) return markup;
            markup = new ConflictHeaderMarkup
            {
                Active = $"<b><color={ConflictColor}>{conflict.Combination}</color></b>   <color={InfoColor}>{conflict.KindLabel}</color>",
                Ignored = $"<b>{conflict.Combination}</b>   <color={InfoColor}>{conflict.KindLabel} (ignored)</color>",
            };
            _headerMarkup[conflict.Signature] = markup;
            return markup;
        }

        private bool _conflictsOnly;
        private bool _showIgnored;
        private bool _allBoundKeys;

        private readonly List<KeybindConflict> _conflicts = new List<KeybindConflict>();
        private int _conflictsScanVersion = -1;

        private readonly List<KeybindEntry> _keymap = new List<KeybindEntry>();
        private readonly HashSet<string> _keymapHandedOff = new HashSet<string>(StringComparer.Ordinal);
        private int _keymapScanVersion = -1;

        private KeybindEntry _captureEntry;

        private string _confirmId;
        private ConfigEntryBase _confirmTarget;
        private KeybindEntry _confirmEntry;
        private KeyCode _confirmKey;
        private readonly List<KeyCode> _confirmModifierKeys = new List<KeyCode>();
        private KeyModifiers _confirmModifiers;
        private bool _confirmWantsShortcut;
        private string _confirmUsers;

        private bool _vanillaSettingsWasOpen;
        private bool _conflictSummaryLogged;

        // ---------------------------------------------------------------- lifecycle hooks
        private void SetConflictsOnly(bool on)
        {
            _conflictsOnly = on;
            _allBoundKeys = false;
            ConfigPopup.Close();
            SettingEditWindow.Close();
            CancelCapture();
            ClearCaptureConfirm();
            _bodyScroll = Vector2.zero;
            if (on) KeybindRegistry.Invalidate();
        }

        /// <summary>Opens the Conflicts view from the popup's "Review all".</summary>
        public void ShowConflicts()
        {
            _search = "";
            GUIUtility.keyboardControl = 0;
            SetConflictsOnly(true);
            _showIgnored = false;
        }

        // Valheim's Controls screen rebinds through ZInput, so the vanilla half of the scan goes stale the
        // moment that screen closes. Watching Settings.instance fall back to null costs one null check a frame
        // and needs no Harmony patch - which matters, because a patch on a client-only type would have to be
        // listed in Core's dedicated-server skip set, in a file this work does not own.
        private void WatchVanillaControlsMenu()
        {
            bool open = Settings.instance != null;
            if (open == _vanillaSettingsWasOpen) return;
            _vanillaSettingsWasOpen = open;
            if (!open) KeybindRegistry.Invalidate();
        }

        // One line per session once a world is up, so headless servers (which never draw the window) and the
        // log-report bot see the same conflicts the popup would have listed.
        private void LogKeybindConflictsOnce()
        {
            if (_conflictSummaryLogged || ZNet.instance == null) return;
            _conflictSummaryLogged = true;
            try
            {
                var unresolved = UnresolvedConflicts();
                int ignored = Conflicts().Count - unresolved.Count;
                string suffix = ignored > 0 ? $" ({ignored} ignored)" : "";
                FiresConfigUI.Log.LogInfo("[Keybinds] " + KeybindConflictDetector.Summarize(unresolved) + suffix);
            }
            catch (Exception ex) { FiresConfigUI.Log.LogWarning("keybinds: conflict summary failed: " + ex.Message); }
        }

        private void OnWindowOpenedCheckKeybinds()
        {
            KeybindRegistry.Invalidate();
            OpenKeybindPopupIfWarranted();
        }

        // ---------------------------------------------------------------- conflict list cache
        private IReadOnlyList<KeybindConflict> Conflicts()
        {
            KeybindRegistry.EnsureScanned();
            if (_conflictsScanVersion == KeybindRegistry.ScanVersion) return _conflicts;
            _conflictsScanVersion = KeybindRegistry.ScanVersion;
            _conflicts.Clear();
            _conflicts.AddRange(KeybindConflictDetector.Detect());
            return _conflicts;
        }

        private List<KeybindConflict> UnresolvedConflicts() => KeybindConflictStore.UnresolvedOf(Conflicts());

        // ---------------------------------------------------------------- the Conflicts view
        private void DrawConflictsBody(float height)
        {
            DrawConflictChips();
            _bodyScroll = GUILayout.BeginScrollView(_bodyScroll, GUILayout.Height(height - Px(28f)));
            if (_allBoundKeys) DrawKeymap();
            else DrawConflictGroups();
            GUILayout.EndScrollView();
        }

        private void DrawConflictChips()
        {
            GUILayout.BeginHorizontal();
            if (Checkbox(_showIgnored, "Show ignored", ConflictChipWidthBase,
                    "Also list the conflicts you chose to keep, so you can un-ignore one."))
                _showIgnored = !_showIgnored;

            if (Checkbox(_allBoundKeys, "All bound keys", ConflictChipWidthBase,
                    "Every bound key across every mod and Valheim, conflicting or not, sorted by key."))
                _allBoundKeys = !_allBoundKeys;

            if (GUILayout.Button("Rescan", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(60f))))
                KeybindRegistry.Invalidate();
            FiresRoundedSkin.MarkHint("Re-read every mod's keys and Valheim's.");

            GUILayout.FlexibleSpace();
            GUILayout.Label($"<color={InfoColor}>{KeybindRegistry.Entries.Count} bindings, {KeybindConflictStore.IgnoredCount} ignored</color>",
                ConfigSkin.Hint);
            GUILayout.EndHorizontal();
        }

        private void DrawConflictGroups()
        {
            var conflicts = _showIgnored ? Conflicts() : (IReadOnlyList<KeybindConflict>)UnresolvedConflicts();
            int shown = 0;
            foreach (var conflict in conflicts)
            {
                if (!ConflictMatchesSearch(conflict)) continue;
                DrawConflictGroup(conflict);
                shown++;
            }
            if (shown == 0)
                GUILayout.Label(conflicts.Count == 0
                    ? "No keybind conflicts."
                    : "No conflicts match the search.", ConfigSkin.Label);
        }

        // One character is a legitimate search here, unlike the settings list: most keys ARE one character.
        private bool IsSearchingKeys => !string.IsNullOrEmpty(_search);

        private bool ConflictMatchesSearch(KeybindConflict conflict)
        {
            if (!IsSearchingKeys) return true;
            string query = _search;
            if (Contains(conflict.Combination, query) || Contains(conflict.KindLabel, query)) return true;
            foreach (var member in conflict.Members)
                if (Contains(member.ModName, query) || Contains(member.Label, query) || Contains(member.Name, query)) return true;
            return false;
        }

        private void DrawConflictGroup(KeybindConflict conflict)
        {
            bool ignored = KeybindConflictStore.IsIgnored(conflict);

            var headerMarkup = MarkupOf(conflict);

            GUILayout.Space(3f);
            GUILayout.BeginHorizontal();
            GUILayout.Label(ignored ? headerMarkup.Ignored : headerMarkup.Active, ConfigSkin.SectionBar);
            GUILayout.EndHorizontal();

            foreach (var member in conflict.Members) DrawConflictMemberRow(member);

            GUILayout.BeginHorizontal();
            GUILayout.Space(Px(ConflictKeyColumnWidthBase));
            if (ignored)
            {
                if (GUILayout.Button("Un-ignore", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(ConflictIgnoreWidthBase))))
                    KeybindConflictStore.Unignore(conflict.Signature);
                FiresRoundedSkin.MarkHint("Warn about this conflict again.");
            }
            else
            {
                if (GUILayout.Button("Ignore", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(ConflictIgnoreWidthBase))))
                    KeybindConflictStore.Ignore(conflict);
                FiresRoundedSkin.MarkHint("Keep this conflict and stop warning about it. Rebind any of these keys and it is a new situation.");
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void DrawConflictMemberRow(KeybindEntry member)
        {
            if (DrawCaptureConfirm(member)) return;

            var markup = MarkupOf(member);

            GUILayout.BeginHorizontal();
            GUILayout.Space(Px(12f));
            GUILayout.Label(MemberLabel(member), ConfigSkin.Label, ScaledLayout.Width(Px(ConflictModWidthBase)));
            FiresRoundedSkin.MarkHint(markup.Hint);

            GUILayout.Label(markup.Detail, ConfigSkin.Label, ScaledLayout.Width(Px(ConflictContextWidthBase)));

            bool capturingThis = _capturing && _captureEntry != null && _captureEntry.Id == member.Id;
            if (capturingThis)
            {
                if (GUILayout.Button(CaptureLabel(), ConfigSkin.Button, ScaledLayout.Width(KeyBindWidth)))
                    CommitEntryCapture();
                if (Event.current.type == EventType.Repaint) RecordCaptureButtonRect();
                if (GUILayout.Button("Apply", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(ConflictActionWidthBase))))
                    CommitEntryCapture();
                if (GUILayout.Button("X", ConfigSkin.ButtonSmall, ScaledLayout.Width(ClearButtonWidth)))
                    CancelCapture();
            }
            else
            {
                GUILayout.Label(markup.Combination, ConfigSkin.Field, ScaledLayout.Width(KeyBindWidth));
                if (member.CanChange)
                {
                    if (GUILayout.Button("Change", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(ConflictActionWidthBase))))
                        BeginEntryCapture(member);
                    if (GUILayout.Button("Unbind", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(ConflictActionWidthBase))))
                        UnbindMember(member);
                }
                else
                {
                    GUILayout.Label(markup.ReadOnlyHint, ConfigSkin.Hint);
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private static string MemberLabel(KeybindEntry member) => member.SourceLabel;

        private static string MemberDetail(KeybindEntry member)
            => BindingContexts.Describe(member.Context) + ", " + (member.Match == BindingMatch.Strict ? "strict" : "loose");

        private static string MemberReadOnlyHint(KeybindEntry member)
        {
            if (member.IsLocked) return "locked by the server";
            return string.IsNullOrEmpty(member.ChangeHint) ? "not changeable here" : member.ChangeHint;
        }

        private void UnbindMember(KeybindEntry member)
        {
            if (KeybindRegistry.Unbind(member)) return;
            FiresConfigUI.Log.LogWarning($"keybinds: '{member.Id}' could not be unbound.");
        }

        // ---------------------------------------------------------------- the full keymap
        // Sorting and the pairwise hand-off sweep are cached against the scan version: OnGUI runs several times
        // a frame, and this list is every bound key in the whole install.
        private void RefreshKeymapCache()
        {
            KeybindRegistry.EnsureScanned();
            if (_keymapScanVersion == KeybindRegistry.ScanVersion) return;
            _keymapScanVersion = KeybindRegistry.ScanVersion;
            _keymap.Clear();
            _keymap.AddRange(KeybindRegistry.BoundEntriesSortedByKey());
            _keymapHandedOff.Clear();
            CollectHandedOffIds(_keymap, _keymapHandedOff);
        }

        private void DrawKeymap()
        {
            RefreshKeymapCache();
            var entries = _keymap;
            var handled = _keymapHandedOff;
            string lastKey = null;
            int shown = 0;

            foreach (var entry in entries)
            {
                if (IsSearchingKeys && !Contains(entry.Combination, _search) && !Contains(entry.ModName, _search)
                    && !Contains(entry.Label, _search) && !Contains(entry.Name, _search)) continue;

                if (entry.Combination != lastKey)
                {
                    lastKey = entry.Combination;
                    GUILayout.Space(2f);
                    GUILayout.Label("<b>" + lastKey + "</b>", ConfigSkin.SectionBar);
                }

                GUILayout.BeginHorizontal();
                GUILayout.Space(Px(12f));
                GUILayout.Label(MemberLabel(entry), ConfigSkin.Label, ScaledLayout.Width(Px(ConflictModWidthBase)));
                GUILayout.Label($"<color={InfoColor}>{MemberDetail(entry)}</color>", ConfigSkin.Label,
                    ScaledLayout.Width(Px(ConflictContextWidthBase)));
                if (handled.Contains(entry.Id)) GUILayout.Label($"<color={HandledColor}>handled</color>", ConfigSkin.Hint);
                else if (entry.IsChord) GUILayout.Label($"<color={InfoColor}>chord - not checked</color>", ConfigSkin.Hint);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                shown++;
            }
            if (shown == 0) GUILayout.Label("No bound keys match the search.", ConfigSkin.Label);
        }

        private static void CollectHandedOffIds(List<KeybindEntry> entries, HashSet<string> into)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                for (int j = i + 1; j < entries.Count; j++)
                {
                    if (entries[i].Key != entries[j].Key) continue;
                    if (!KeybindConflictDetector.IsHandedOff(entries[i], entries[j])) continue;
                    into.Add(entries[i].Id);
                    into.Add(entries[j].Id);
                }
            }
        }

        // ---------------------------------------------------------------- registry-level key capture
        private void BeginEntryCapture(KeybindEntry member)
        {
            ClearCaptureConfirm();
            BeginCapture(member.Entry);
            _captureEntry = member;
        }

        private void CommitEntryCapture()
        {
            var member = _captureEntry;
            KeyCode main = _capMain;
            var modifierKeys = new List<KeyCode>(_capMods);
            CancelCapture();

            if (member == null) return;
            if (main == KeyCode.None && modifierKeys.Count == 0) return;
            if (main == KeyCode.None) { main = modifierKeys[0]; modifierKeys.RemoveAt(0); }

            var modifiers = KeyModifiers.None;
            foreach (var key in modifierKeys) modifiers |= KeyCombination.ModifierOf(key);

            var users = KeybindRegistry.UsersOf(main, modifiers, member.Context, member);
            if (users.Count > 0)
            {
                _confirmId = member.Id;
                _confirmEntry = member;
                _confirmTarget = member.Entry;
                _confirmKey = main;
                _confirmModifiers = modifiers;
                _confirmModifierKeys.Clear();
                _confirmModifierKeys.AddRange(modifierKeys);
                _confirmWantsShortcut = false;
                _confirmUsers = KeybindRegistry.DescribeUsers(users);
                return;
            }
            KeybindRegistry.Apply(member, main, modifiers);
        }

        // ---------------------------------------------------------------- "who else uses this" confirmation
        private bool OfferCaptureConfirm(CfgDescriptor descriptor, ConfigEntryBase target, KeyCode main,
            List<KeyCode> modifierKeys, bool wantsShortcut)
        {
            if (target == null || main == KeyCode.None) return false;

            var modifiers = KeyModifiers.None;
            foreach (var key in modifierKeys) modifiers |= KeyCombination.ModifierOf(key);

            var self = KeybindRegistry.FindByConfigEntry(target);
            var context = self?.Context ?? BindingContexts.Default;
            var users = KeybindRegistry.UsersOf(main, modifiers, context, self);
            if (users.Count == 0) return false;

            _confirmId = IdOf(descriptor);
            _confirmEntry = null;
            _confirmTarget = target;
            _confirmKey = main;
            _confirmModifiers = modifiers;
            _confirmModifierKeys.Clear();
            _confirmModifierKeys.AddRange(modifierKeys);
            _confirmWantsShortcut = wantsShortcut;
            _confirmUsers = KeybindRegistry.DescribeUsers(users);
            return true;
        }

        private bool DrawCaptureConfirm(CfgDescriptor descriptor)
            => _confirmId != null && _confirmId == IdOf(descriptor) && DrawConfirmRow();

        private bool DrawCaptureConfirm(KeybindEntry member)
            => _confirmId != null && _confirmId == member.Id && DrawConfirmRow();

        private bool DrawConfirmRow()
        {
            string combination = KeyCombination.Describe(_confirmKey, _confirmModifiers);
            GUILayout.BeginVertical();
            GUILayout.Label($"<color={CaptureColor}><b>{combination}</b></color> is already used by {_confirmUsers}.", ConfigSkin.Desc);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Use anyway", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(ConfirmActionWidthBase))))
                ApplyPendingConfirm();
            if (GUILayout.Button("Try another", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(ConfirmActionWidthBase))))
                RetryPendingConfirm();
            if (GUILayout.Button("Cancel", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(ConfirmActionWidthBase))))
                ClearCaptureConfirm();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            return true;
        }

        private void ApplyPendingConfirm()
        {
            if (_confirmEntry != null) KeybindRegistry.Apply(_confirmEntry, _confirmKey, _confirmModifiers);
            else WriteCapture(_confirmTarget, _confirmKey, _confirmModifierKeys, _confirmWantsShortcut);
            ClearCaptureConfirm();
        }

        private void RetryPendingConfirm()
        {
            var member = _confirmEntry;
            var target = _confirmTarget;
            ClearCaptureConfirm();
            if (member != null) BeginEntryCapture(member);
            else if (target != null) BeginCapture(target);
        }

        private void ClearCaptureConfirm()
        {
            _confirmId = null;
            _confirmEntry = null;
            _confirmTarget = null;
            _confirmKey = KeyCode.None;
            _confirmModifiers = KeyModifiers.None;
            _confirmModifierKeys.Clear();
            _confirmWantsShortcut = false;
            _confirmUsers = null;
        }
    }
}
