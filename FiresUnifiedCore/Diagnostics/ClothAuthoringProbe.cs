using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// Names the prefab behind vanilla's "PlayerClothWindShelter: MagicaCloth component not found" error, which only
    /// reports the local object name ("default" on every cape). Logs the root prefab and child path once per
    /// prefab. EBM's prewarm instantiates every prefab, which is why the error appears at load on rigs running it.
    /// </summary>
    [HarmonyPatch(typeof(PlayerClothWindShelter), "Awake")]
    internal static class ClothAuthoringProbe
    {
        // Vanilla 1.0 prefabs already known to carry an orphaned PlayerClothWindShelter. These are
        // Iron Gate's authoring slips, not ours, and they are harmless: VisEquipment only reaches
        // SetPlayer via the MagicaCloth itself (componentsInChild.TryGetComponent<...>), so a
        // shelter with no MagicaCloth sibling never gets a player, and its Update early-returns
        // before touching m_cloth. Staying quiet about them keeps the probe meaningful — anything
        // it DOES print is either new in a game patch or ours.
        private static readonly HashSet<string> KnownVanilla = new HashSet<string>
        {
            "HelmetRootCrown",
        };

        private static readonly HashSet<string> _reported = new HashSet<string>();

        private static void Postfix(PlayerClothWindShelter __instance)
        {
            if (__instance == null) return;
            if (__instance.GetComponent<MagicaCloth2.MagicaCloth>() != null) return;

            Transform root = __instance.transform.root;
            string rootName = root != null ? root.name : "<no root>";
            // Instantiated copies arrive as "Name(Clone)"; match on the prefab name.
            string prefabName = rootName.EndsWith("(Clone)")
                ? rootName.Substring(0, rootName.Length - "(Clone)".Length)
                : rootName;
            if (KnownVanilla.Contains(prefabName)) return;
            if (!_reported.Add(prefabName)) return;

            var path = new StringBuilder(__instance.gameObject.name);
            for (Transform ancestor = __instance.transform.parent; ancestor != null; ancestor = ancestor.parent)
                path.Insert(0, ancestor.name + "/");

            Debug.LogWarning(
                $"[ClothAuthoringProbe] PlayerClothWindShelter without MagicaCloth on prefab '{prefabName}' " +
                $"(path: {path}). This is the prefab behind vanilla's \"check prefab authoring!\" error. " +
                $"If it is one of ours, the cape/armor needs a MagicaCloth component authored alongside it.");
        }
    }
}
