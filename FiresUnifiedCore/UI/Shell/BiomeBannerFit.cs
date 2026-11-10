// Fire 11-02: a long location name wraps in vanilla's biome-found banner, and the prefab's two gold rules stay at their one-line spacing,
// so they cross the text. Each new banner opens its rules by the height of the extra lines. The rules are found by place (above or below
// the title), so the prefab's names are not assumed. The Debug lines say what moved and whether it held the next frame.
using System.Collections.Generic;
using FiresCore.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI.Shell
{
    [HarmonyPatch(typeof(MessageHud), "UpdateBiomeFound")]
    internal static class BiomeBannerFit
    {
        private const float CentredWithin = 1f;
        private static GameObject s_fitted;
        private static RectTransform s_probe;
        private static Vector2 s_probeAt;

        private static void Postfix(MessageHud __instance)
        {
            var banner = __instance.m_biomeMsgInstance;
            if (s_probe != null)
            {
                FiresLogger.LogDebug($"[BiomeBanner] next frame: rule '{s_probe.name}' {(s_probe.anchoredPosition == s_probeAt ? "held" : $"moved back to {s_probe.anchoredPosition} (the animator owns it)")}");
                s_probe = null;
            }
            if (banner == null || banner == s_fitted) return;
            s_fitted = banner;
            var titleNode = Utils.FindChild(banner.transform, "Title");
            var title = titleNode != null ? titleNode.GetComponent<TMP_Text>() : null;
            if (title == null) return;
            title.ForceMeshUpdate();
            int lines = title.textInfo.lineCount;
            if (lines <= 1) return;
            float half = (lines - 1) * title.textInfo.lineInfo[0].lineHeight * 0.5f;
            float centre = title.rectTransform.position.y;
            var moved = new List<Transform>();
            foreach (var graphic in banner.GetComponentsInChildren<Graphic>(true))
            {
                var node = graphic.transform;
                if (node == titleNode || node.IsChildOf(titleNode) || titleNode.IsChildOf(node)) continue;
                if (moved.Exists(node.IsChildOf)) continue;
                float away = node.position.y - centre;
                if (Mathf.Abs(away) < CentredWithin) continue;
                var rect = (RectTransform)node;
                rect.anchoredPosition += new Vector2(0f, away > 0f ? half : -half);
                moved.Add(node);
                s_probe = rect;
                s_probeAt = rect.anchoredPosition;
            }
            FiresLogger.LogDebug($"[BiomeBanner] '{title.text}' wraps to {lines} lines: opened {moved.Count} part(s) by {half:0.#} each way: {string.Join(", ", moved.ConvertAll(t => t.name))}; animator {(banner.GetComponentInChildren<Animator>() != null ? "present" : "none")}");
        }
    }
}
