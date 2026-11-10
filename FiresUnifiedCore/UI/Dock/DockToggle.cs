// Docking ON/OFF toggle: one pill at the top centre,
// shown while any layout moving is possible (Esc menu direct edit, HUD / inventory / screen editors). Click flips
// DockService.DockingOn (default OFF, saved). Built from the layout editor's button look (DockLayoutOverlay.Button: kit well
// fill, gold outline, SansBold label); its own overlay canvas above the editor so it is always clickable.
using FiresCore.UI.Shell.Kit;
using UnityEngine;
using UnityEngine.UI;
using T = FiresCore.UI.Shell.Kit.FiresTokens;
using W = FiresCore.UI.Shell.Kit.Widgets;

namespace FiresCore.UI.Dock
{
    internal static class DockToggle
    {
        internal const string CanvasName = "FiresDockToggle";
        private static GameObject s_go;
        private static KitLabel s_label;
        private static Image s_fill;
        private static bool? s_shownState;

        internal static void Tick()
        {
            bool show = FiresCore.UI.Shell.FiresShell.OverrideOn && DockService.AnyMoveMode;
            if (!show) { if (s_go != null && s_go.activeSelf) s_go.SetActive(false); return; }
            Ensure();
            if (!s_go.activeSelf) s_go.SetActive(true);
            bool on = DockService.DockingOn;
            if (s_shownState != on)
            {
                s_shownState = on;
                s_label.Value = on ? "Docking: ON" : "Docking: OFF";
                s_fill.color = on ? T.WithAlpha(T.Gold, 0.35f) : T.WithAlpha(T.Well, 0.95f);
            }
        }

        private static void Ensure()
        {
            if (s_go != null) return;
            s_go = new GameObject(CanvasName, typeof(RectTransform));
            Object.DontDestroyOnLoad(s_go);
            s_go.layer = 5;
            var cv = s_go.AddComponent<Canvas>(); cv.renderMode = RenderMode.ScreenSpaceOverlay; cv.sortingOrder = 31002;
            var cs = s_go.AddComponent<CanvasScaler>(); cs.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            cs.referenceResolution = new Vector2(1920f, 1080f); cs.matchWidthOrHeight = 1f;
            s_go.AddComponent<GraphicRaycaster>();
            var b = W.Node(s_go.transform, "Docking");
            b.anchorMin = b.anchorMax = new Vector2(0.5f, 1f); b.pivot = new Vector2(0.5f, 1f);
            b.anchoredPosition = new Vector2(0f, -8f); b.sizeDelta = new Vector2(150f, 30f);
            s_fill = b.gameObject.AddComponent<Image>(); s_fill.sprite = KitSprites.RoundedFill(T.Radius); s_fill.type = Image.Type.Sliced; s_fill.raycastTarget = true;
            var ol = W.Box(b, "Outline", KitSprites.RoundedOutline(T.Radius), T.Gold); W.Stretch(ol.rectTransform, 0, 0, 0, 0);
            s_label = KitText.Create(b, "Label", "Docking: OFF", FontRole.SansBold, 14f, T.GoldBright, KitAlign.Center, 0f, false); W.Stretch(s_label.Rect, 0, 0, 0, 0);
            var btn = b.gameObject.AddComponent<Button>(); btn.targetGraphic = s_fill;
            btn.onClick.AddListener(() => { try { DockService.DockingOn = !DockService.DockingOn; } catch (System.Exception e) { DockService.Log("docking toggle threw " + e.Message); } });
            s_go.SetActive(false);
        }
    }
}
