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
        private const float PaddingPx = 8f;
        private KitLabel _title;
        private RectTransform _box;
        private float _next;
        private bool _logged;

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
            bool show = !FiresShell.OverrideOn && EnvMan.instance != null && ZNet.instance != null;
            for (int i = 0; i < _box.childCount; i++) { var c = _box.GetChild(i).gameObject; if (c.activeSelf != show) c.SetActive(show); }
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
