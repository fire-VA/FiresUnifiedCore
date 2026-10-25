using FiresCore.UI.Shell.Kit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI.Shell
{
    /// <summary>
    /// The Esc menu could draw as bare floating text, and its panel was not adjustable in the layout editor.
    /// On each open of the real Esc dialog (Menu.m_menuDialog):
    /// (1) one Debug line lists the dialog's own background candidates (every Image: active, enabled, alpha, sprite, size, culled), so the
    ///     reason a backdrop is missing is on record;
    /// (2) when no visible Image covers at least half the dialog, a kit plate (Panel colour, rounded, KitLeaf) is put behind the entries,
    ///     so the dialog always reads as a panel; the vanilla one is never touched;
    /// (3) the dialog is a HUD layout element "hud.pausemenu" ("Pause menu"): listed in the HUD editor, movable, resizable, Hide / Show,
    ///     saved with the HUD layout like every other element.
    /// Client only: Menu exists only where there is a UI.
    /// </summary>
    [HarmonyPatch(typeof(Menu), "Update")]
    internal static class ShellPauseMenu
    {
        private const string PlateName = "FiresMenuPlate";
        private const string DockId = "hud.pausemenu";
        private static bool s_wasOpen, s_registered;
        private static RectTransform s_dialog;

        private static void Postfix(Menu __instance)
        {
            try
            {
                var dialog = __instance != null ? __instance.m_menuDialog as RectTransform : null;
                bool open = dialog != null && dialog.gameObject.activeInHierarchy;
                if (open && !s_wasOpen) OnOpened(dialog);
                s_wasOpen = open;
            }
            catch (System.Exception e) { ShellLog.Debug("pause menu: " + e.Message); }
        }

        private static void OnOpened(RectTransform dialog)
        {
            if (s_dialog != dialog) { s_dialog = dialog; s_registered = false; }
            var sb = new System.Text.StringBuilder();
            float dialogArea = Mathf.Max(1f, dialog.rect.width * dialog.rect.height);
            bool covered = false;
            foreach (var img in dialog.GetComponentsInChildren<Image>(true))
            {
                if (img == null || img.GetComponent<KitLeaf>() != null || img.GetComponentInParent<Button>() != null) continue;
                var r = img.rectTransform.rect;
                bool visible = img.isActiveAndEnabled && img.color.a > 0.05f && img.sprite != null && !img.canvasRenderer.cull;
                if (visible && r.width * r.height >= 0.5f * dialogArea) covered = true;
                if (sb.Length < 900)
                    sb.Append($" {img.name}[act {img.gameObject.activeInHierarchy} en {img.enabled} a {img.color.a:0.00} sprite {(img.sprite != null ? img.sprite.name : "none")} {r.width:0}x{r.height:0} cull {img.canvasRenderer.cull}]");
            }
            ShellLog.Debug($"pause menu opened: dialog {dialog.rect.width:0}x{dialog.rect.height:0}; background covered {covered}; images:{(sb.Length > 0 ? sb.ToString() : " none")}");

            var plate = dialog.Find(PlateName);
            if (!covered && plate == null)
            {
                var go = new GameObject(PlateName, typeof(RectTransform), typeof(Image), typeof(KitLeaf), typeof(LayoutElement));
                go.GetComponent<LayoutElement>().ignoreLayout = true;   // never a layout child (the entries stay where vanilla lays them out)
                var rt = (RectTransform)go.transform;
                rt.SetParent(dialog, false);
                rt.SetAsFirstSibling();   // behind every entry
                rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(-24f, -16f); rt.offsetMax = new Vector2(24f, 16f);
                var img = go.GetComponent<Image>();
                img.sprite = KitSprites.RoundedFill(FiresTokens.Radius); img.type = Image.Type.Sliced;
                img.color = FiresTokens.WithAlpha(FiresTokens.Panel, 0.92f); img.raycastTarget = false;
                ShellLog.Debug("pause menu: no visible backdrop covers the dialog; kit plate added behind the entries");
            }
            else if (covered && plate != null) plate.gameObject.SetActive(false);   // the dialog has its own again
            else if (!covered && plate != null && !plate.gameObject.activeSelf) plate.gameObject.SetActive(true);

            if (!s_registered)
            {
                s_registered = true;
                FiresShell.RegisterHudElement(DockId, dialog, new HudElementOptions { Label = "Pause menu", Owner = "Core", Resizable = true });
                ShellLog.Debug("pause menu: registered as HUD layout element " + DockId);
            }
        }
    }
}
