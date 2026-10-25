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
            if (NeverShowConflictPopup) return;   // the player chose "Don't show again"

            // Never over an FDT test's shots.
            if (FdtTestRunning()) { FiresConfigUI.Log.LogDebug("[Keybinds] conflict popup skipped: an FDT test is running"); return; }

            var unresolved = UnresolvedConflicts();
            if (unresolved.Count == 0) return;

            s_keybindPopupShownThisSession = true;
            FiresConfigUI.Log.LogDebug($"[Keybinds] conflict popup shown: {unresolved.Count} real conflict(s), FDT test running {FdtTestRunning()} (runner flag {(s_fdtRunning != null ? "found" : "not found")})");
            _keybindPopupConflicts.Clear();
            _keybindPopupConflicts.AddRange(unresolved);
            _keybindPopupScroll = Vector2.zero;
            _keybindPopupOpen = true;
        }

        // FiresDebugginTools' test runner busy flag (FiresTests.Running, internal), by reflection; false without FDT.
        private static System.Reflection.PropertyInfo s_fdtRunning;
        private static bool s_fdtLooked;
        private static object s_fdtTests;   // FDT TestScreen.s_running (Dictionary<string, float>), read-only
        private static bool FdtTestRunning()
        {
            if (!s_fdtLooked)
            {
                s_fdtLooked = true;
                foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name != "FiresDebugginTools") continue;
                    var t = asm.GetType("FiresDebugginTools.Utilities.FiresTests");
                    s_fdtRunning = t?.GetProperty("Running", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                    var screen = asm.GetType("FiresDebugginTools.Utilities.TestScreen");
                    s_fdtTests = screen?.GetField("s_running", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)?.GetValue(null);
                    break;
                }
            }
            try { if (s_fdtRunning != null && s_fdtRunning.GetValue(null, null) is bool b && b) return true; } catch { }
            // The popup could open during a golden test: FiresTests.Running is only the fires_test_all runner;
            // single tests register in TestScreen.s_running. Any tag there = a test is running.
            try { return s_fdtTests is System.Collections.ICollection c && c.Count > 0; } catch { return false; }
        }

        private static Texture2D s_solid;
        private static Texture2D Solid { get { if (s_solid == null) { s_solid = new Texture2D(1, 1) { hideFlags = HideFlags.DontSave }; s_solid.SetPixel(0, 0, Color.white); s_solid.Apply(); } return s_solid; } }

        private const string NeverShowKey = "FiresConfig.ConflictPopup.NeverShow";
        private static bool NeverShowConflictPopup
        {
            get { try { return PlayerPrefs.GetInt(NeverShowKey, 0) == 1; } catch { return false; } }
            set { try { PlayerPrefs.SetInt(NeverShowKey, value ? 1 : 0); PlayerPrefs.Save(); } catch { } }
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

            // The popup was see-through over the mod rows: a full-screen modal window (IMGUI windows draw above
            // all non-window GUI, so a scrim outside it would sit UNDER the config window): dim scrim, opaque plate, then the body.
            _keybindPopupRect = rect;
            GUI.Window(KeybindPopupWindowId, new Rect(0f, 0f, Screen.width, Screen.height), DrawKeybindPopupModal, GUIContent.none, GUIStyle.none);
            GUI.BringWindowToFront(KeybindPopupWindowId);
        }

        private Rect _keybindPopupRect;

        private void DrawKeybindPopupModal(int id)
        {
            var prevColor = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Solid);      // scrim over everything, eats clicks
            GUI.color = new Color(0.075f, 0.065f, 0.06f, 1f);                              // opaque plate (the kit's Panel tone)
            GUI.DrawTexture(_keybindPopupRect, Solid);
            GUI.color = prevColor;
            GUI.Box(_keybindPopupRect, GUIContent.none, ConfigSkin.Window);               // the usual frame on top of the plate
            var inner = new Rect(_keybindPopupRect.x + Px(12f), _keybindPopupRect.y + Px(10f), _keybindPopupRect.width - Px(24f), _keybindPopupRect.height - Px(20f));
            GUILayout.BeginArea(inner);
            DrawKeybindPopupBody(id);
            GUILayout.EndArea();
        }

        private void DrawKeybindPopupBody(int id)
        {
            ApplyHiddenTint();
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

            // "Don't show again" for players who want to keep ignoring conflicts: per player (PlayerPrefs),
            // this popup never opens by itself again; the Conflicts view in the config window still lists them
            if (GUILayout.Button("Don't show again", ConfigSkin.ButtonSmall, ScaledLayout.Width(Px(KeybindPopupFooterButtonWidthBase + 20f))))
            {
                NeverShowConflictPopup = true;
                CloseKeybindPopup();
            }
            FiresRoundedSkin.MarkHint("Never open this popup by itself again. The Conflicts view still lists them.");

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
