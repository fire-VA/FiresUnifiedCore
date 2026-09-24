using System.Collections.Generic;
using FiresCore.Input;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// The one-per-session keybind-conflict popup: shown the first time the config window opens while
    /// unresolved conflicts exist. Drawn after the main window like <see cref="ConfigPopup"/>, so clicks land on
    /// it rather than falling through to the rows behind it.
    /// Plan: Docs/PLAN_KeybindConflicts.md
    /// </summary>
    public partial class ConfigPanel
    {
        private const int KeybindPopupWindowId = 0xF1C2;
        private const float KeybindPopupWidthBase = 720f;
        private const float KeybindPopupHeightBase = 440f;
        private const float KeybindPopupScreenShare = 0.8f;
        private const float KeybindPopupFooterHeightBase = 34f;
        private const float KeybindPopupUnbindWidthBase = 68f;
        private const float KeybindPopupIgnoreWidthBase = 68f;
        private const float KeybindPopupFooterButtonWidthBase = 96f;

        private static bool s_keybindPopupShownThisSession;

        private bool _keybindPopupOpen;
        private Vector2 _keybindPopupScroll;
        private readonly List<KeybindConflict> _keybindPopupConflicts = new List<KeybindConflict>();

        private bool KeybindPopupOpen => _keybindPopupOpen;

        private void OpenKeybindPopupIfWarranted()
        {
            if (s_keybindPopupShownThisSession) return;
            if (!FiresConfigUI.WarnKeybindConflicts) return;
            if (KeybindConflictStore.RemindLaterThisSession) return;

            var unresolved = UnresolvedConflicts();
            if (unresolved.Count == 0) return;

            s_keybindPopupShownThisSession = true;
            _keybindPopupConflicts.Clear();
            _keybindPopupConflicts.AddRange(unresolved);
            _keybindPopupScroll = Vector2.zero;
            _keybindPopupOpen = true;
        }

        private void CloseKeybindPopup()
        {
            _keybindPopupOpen = false;
            _keybindPopupConflicts.Clear();
        }

        private void DrawKeybindPopup()
        {
            if (!_keybindPopupOpen) return;

            float width = Mathf.Min(Px(KeybindPopupWidthBase), Screen.width * KeybindPopupScreenShare);
            float height = Mathf.Min(Px(KeybindPopupHeightBase), Screen.height * KeybindPopupScreenShare);
            var rect = new Rect((Screen.width - width) / 2f, (Screen.height - height) / 2f, width, height);

            GUI.Window(KeybindPopupWindowId, rect, DrawKeybindPopupBody, GUIContent.none, ConfigSkin.Window);
            GUI.BringWindowToFront(KeybindPopupWindowId);
        }

        private void DrawKeybindPopupBody(int id)
        {
            int count = _keybindPopupConflicts.Count;
            GUILayout.Label(count == 1 ? "1 keybind conflict" : count + " keybind conflicts", ConfigSkin.Title);
            GUILayout.Label("More than one thing is listening for the same key. Fix one now, or keep it and never be asked again.",
                ConfigSkin.Desc);

            float bodyHeight = Mathf.Max(Px(60f), Px(KeybindPopupHeightBase) - Px(KeybindPopupFooterHeightBase) - Px(70f));
            _keybindPopupScroll = GUILayout.BeginScrollView(_keybindPopupScroll, GUILayout.Height(bodyHeight));
            for (int i = _keybindPopupConflicts.Count - 1; i >= 0; i--)
            {
                var conflict = _keybindPopupConflicts[i];
                if (KeybindConflictStore.IsIgnored(conflict)) { _keybindPopupConflicts.RemoveAt(i); continue; }
                DrawKeybindPopupConflict(conflict);
            }
            if (_keybindPopupConflicts.Count == 0) GUILayout.Label("All resolved.", ConfigSkin.Label);
            GUILayout.EndScrollView();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Review all", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(KeybindPopupFooterButtonWidthBase))))
            {
                CloseKeybindPopup();
                ShowConflicts();
            }
            FiresRoundedSkin.MarkHint("Open the Conflicts view, where every binding can be changed.");

            if (GUILayout.Button("Remind me later", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(KeybindPopupFooterButtonWidthBase + 34f))))
            {
                KeybindConflictStore.RemindLater();
                CloseKeybindPopup();
            }
            FiresRoundedSkin.MarkHint("Say nothing more this session. Nothing is remembered.");

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Close", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(60f)))) CloseKeybindPopup();
            GUILayout.EndHorizontal();
        }

        private void DrawKeybindPopupConflict(KeybindConflict conflict)
        {
            GUILayout.Space(3f);
            GUILayout.Label($"<b><color={ConflictColor}>{conflict.Combination}</color></b>   <color={InfoColor}>{conflict.KindLabel}</color>",
                ConfigSkin.SectionBar);

            foreach (var member in conflict.Members)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(Px(12f));
                GUILayout.Label(MemberLabel(member), ConfigSkin.Label, ScaledLayout.Width(Px(ConflictModWidthBase)));
                GUILayout.Label($"<color={InfoColor}>{MemberDetail(member)}</color>", ConfigSkin.Label,
                    ScaledLayout.Width(Px(ConflictContextWidthBase)));
                if (member.CanChange)
                {
                    if (GUILayout.Button("Unbind", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(KeybindPopupUnbindWidthBase))))
                        UnbindMember(member);
                }
                else GUILayout.Label($"<color={InfoColor}>{MemberReadOnlyHint(member)}</color>", ConfigSkin.Hint);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }

            GUILayout.BeginHorizontal();
            GUILayout.Space(Px(12f));
            if (GUILayout.Button("Change", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(KeybindPopupIgnoreWidthBase))))
            {
                CloseKeybindPopup();
                ShowConflicts();
            }
            FiresRoundedSkin.MarkHint("Open the Conflicts view to pick a new key for any of these.");

            if (GUILayout.Button("Ignore", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(KeybindPopupIgnoreWidthBase))))
                KeybindConflictStore.Ignore(conflict);
            FiresRoundedSkin.MarkHint("Keep this conflict. Rebinding any of these keys makes it a new situation.");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
    }
}
