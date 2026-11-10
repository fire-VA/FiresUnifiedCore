using FiresCore.UI.Shell.Kit;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.UI.Shell
{
    /// <summary>
    /// The day / time box for the NON-unified UI (Fires UI override OFF): Core's minimap plate only exists with the override ON.
    /// Fire 10-23: "set under the square mini map with a bit of padding"; Fire 10-24: "the day/time should be the same box we use on
    /// the unified one same style and everything, the only difference being the square mini map over the round one". So it is the
    /// unified plate's own box and title (ShellMinimap.PlateBox / PlateTitleText), centred under Minimap.m_smallRoot, shown only
    /// while the override is OFF.
    /// </summary>
    internal sealed class VanillaDayClock : MonoBehaviour
    {
        private static bool s_said;   // 0.2.1224: one Info line saying whether the box is on and why
        private const float PaddingPx = 8f;
        private KitLabel _title;
        private RectTransform _box;
        private float _next;
        private bool _logged;
        private float _nextColumn; private bool _columnFailLogged;   // 0.2.828

        [HarmonyPatch(typeof(Minimap), "Start")]
        private static class MinimapStart
        {
            private static void Postfix(Minimap __instance)
            {
                if (__instance == null || __instance.m_smallRoot == null) return;
                if (__instance.m_smallRoot.GetComponentInChildren<VanillaDayClock>(true) != null) return;
                var go = new GameObject("FiresVanillaDayClock", typeof(RectTransform));
                go.transform.SetParent(__instance.m_smallRoot.transform, false);
                go.AddComponent<VanillaDayClock>().Init();
            }
        }

        private void Init()
        {
            _box = (RectTransform)transform;
            _box.anchorMin = _box.anchorMax = new Vector2(0.5f, 0f);   // centred, hanging below the square map's bottom edge
            _box.pivot = new Vector2(0.5f, 1f);
            _box.anchoredPosition = new Vector2(0f, -PaddingPx);
            _title = ShellMinimap.PlateBox(_box, false);
        }

        private void Update()
        {
            bool show = !ShellMinimap.RoundOn && FiresShell.DayTimeBoxOn && EnvMan.instance != null && ZNet.instance != null;   // 0.2.824: [UI] Show day and time box
            if (!s_said && EnvMan.instance != null && ZNet.instance != null) { s_said = true; ShellLog.Info($"day / time box under the minimap: {(FiresShell.DayTimeBoxOn ? "ON (a mod provides [UI] Show day and time box and has it on)" : "OFF (no installed mod provides [UI] Show day and time box, or it is off there)")} (0.2.1224)"); }
            for (int i = 0; i < _box.childCount; i++) { var c = _box.GetChild(i).gameObject; if (c.activeSelf != show) c.SetActive(show); }
            // 0.2.828 (Fire 10-26, shot 449): the minimap column stacks under this box in the non-unified UI (under the square map when
            // the box is off), 4x a second
            if (!ShellMinimap.RoundOn && Time.unscaledTime >= _nextColumn)
            {
                _nextColumn = Time.unscaledTime + 0.25f;
                try { ShellMinimap.LayoutColumnBelow(show ? _box : _box.parent as RectTransform); } catch (System.Exception e) { if (!_columnFailLogged) { _columnFailLogged = true; FiresCore.Logging.FiresLogger.LogWarning("[Shell] non-unified minimap column failed: " + e.Message); } }
            }
            if (!show || _title == null || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 1f;
            _title.Value = ShellMinimap.PlateTitleText();
            var parent = _box.parent as RectTransform;
            _box.sizeDelta = ShellMinimap.PlateTitleBoxSize(_title, parent != null ? parent.rect.width : 0f);
            if (!_logged)
            {
                _logged = true;
                FiresCore.Logging.FiresLogger.LogDebug($"[Shell] non-unified day / time box (the unified plate's box) under the square minimap ({PaddingPx:0} px padding): '{_title.Value}', {_box.sizeDelta.x:0}x{_box.sizeDelta.y:0}");
            }
        }
    }
}
