using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// Names the prefab behind vanilla's
    /// <c>"PlayerClothWindShelter: MagicaCloth component not found on gameobject {name}"</c> error.
    ///
    /// Valheim 1.0 replaced Unity Cloth with MagicaCloth2 and added
    /// <see cref="PlayerClothWindShelter"/>, whose Awake hard-errors when the GameObject it sits on
    /// has no <c>MagicaCloth</c> sibling. Vanilla's message reports only the local GameObject name,
    /// which on every cape is the FBX-import mesh name <c>"default"</c> — identical across all of
    /// them, so the log alone cannot tell you which prefab is at fault.
    ///
    /// EBM's prewarm instantiates every registered prefab, so it surfaces this at load instead of
    /// whenever a player first equips the offending cape. That is why it shows up on a rig running
    /// EBM and not on a plain client.
    ///
    /// This logs the ROOT object name (the prefab) plus the child path, once per distinct root, so
    /// the offender is identified without spamming. It costs nothing unless the fault is present —
    /// the patch body only runs inside the Awake of a component that is already erroring.
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
            for (Transform t = __instance.transform.parent; t != null; t = t.parent)
                path.Insert(0, t.name + "/");

            Debug.LogWarning(
                $"[ClothAuthoringProbe] PlayerClothWindShelter without MagicaCloth on prefab '{prefabName}' " +
                $"(path: {path}). This is the prefab behind vanilla's \"check prefab authoring!\" error. " +
                $"If it is one of ours, the cape/armor needs a MagicaCloth component authored alongside it.");
        }
    }
}
