using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Repairs an NPC's attached equipment the moment vanilla attaches it. VisEquipment builds these instances on
    /// its own update, well after NpcController.ApplyVisualEquipment returns, so a sweep from there runs before the
    /// objects exist and only ever caught them on a later re-apply — which is why broken effects looked fixed after
    /// opening the dressing room but never on first load. Hooking the attach itself catches them on every path.
    /// Weapons and helmets come from AttachItem; chest, legs, shoulders (capes), utility and trinkets come from
    /// AttachArmor, which is also the only one that runs SetupCloth.
    /// </summary>
    [HarmonyPatch]
    internal static class AttachedItemVisuals
    {
        private static readonly HashSet<string> _reportedItems = new HashSet<string>();

        [HarmonyPatch(typeof(VisEquipment), "AttachItem")]
        [HarmonyPostfix]
        private static void AttachItemPostfix(VisEquipment __instance, int itemHash, GameObject __result)
        {
            if (__result == null) return;
            HandleAttached(__instance, itemHash, __result);
        }

        [HarmonyPatch(typeof(VisEquipment), "AttachArmor")]
        [HarmonyPostfix]
        private static void AttachArmorPostfix(VisEquipment __instance, int itemHash, List<GameObject> __result)
        {
            if (__result == null) return;
            foreach (var instance in __result)
            {
                if (instance != null) HandleAttached(__instance, itemHash, instance);
            }
        }

        private static void HandleAttached(VisEquipment visEquipment, int itemHash, GameObject instance)
        {
            if (visEquipment == null) return;
            if (visEquipment.GetComponent<NpcVisEquipment>() == null) return;

            GameObject itemPrefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(itemHash) : null;
            string itemName = itemPrefab != null ? itemPrefab.name : itemHash.ToString();

            FiresCore.Services.VanillaAssetResolver.RepairBrokenShaders(instance, $"attached '{itemName}'");
            ReportSkinnedState(itemPrefab, instance, itemName);
        }

        /// <summary>One line per item, naming what the SOURCE prefab holds against what the INSTANCE ended up with.
        /// A cape arriving with a null mesh or a legacy UnityEngine.Cloth is a pre-1.0 asset: vanilla 1.0 capes are
        /// MagicaCloth2, and Unity disables a Cloth it cannot skin.</summary>
        private static void ReportSkinnedState(GameObject itemPrefab, GameObject instance, string itemName)
        {
            if (!_reportedItems.Add(itemName)) return;

            string sourceState = DescribeSkinnedRenderers(itemPrefab);
            string instanceState = DescribeSkinnedRenderers(instance);
            if (sourceState == NoSkinnedRenderers && instanceState == NoSkinnedRenderers) return;

            Debug.Log($"[AttachedItemVisuals] '{itemName}' origin={DescribeOrigin(itemPrefab, itemName)}"
                + $" | source: {sourceState} | instance: {instanceState}");
        }

        private const string NoSkinnedRenderers = "no skinned renderers";

        private static string DescribeOrigin(GameObject itemPrefab, string itemName)
        {
            if (itemPrefab == null) return "unresolved";

            GameObject scenePrefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(itemName) : null;
            if (scenePrefab == null) return "ObjectDB only, absent from ZNetScene";
            return ReferenceEquals(scenePrefab, itemPrefab)
                ? "ZNetScene, same object"
                : "ZNetScene holds a DIFFERENT object of this name";
        }

        private static string DescribeSkinnedRenderers(GameObject root)
        {
            if (root == null) return "<null>";

            var skins = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (skins == null || skins.Length == 0) return NoSkinnedRenderers;

            var description = new System.Text.StringBuilder();
            foreach (var skin in skins)
            {
                if (skin == null) continue;
                if (description.Length > 0) description.Append(", ");

                Mesh mesh = skin.sharedMesh;
                description.Append($"{skin.name}[mesh={(mesh != null ? mesh.name : "NULL")}");
                description.Append($" bindposes={(mesh != null && mesh.bindposes != null ? mesh.bindposes.Length : 0)}");
                description.Append($" bones={(skin.bones != null ? skin.bones.Length : 0)}");
                description.Append($" cloth={DescribeClothComponents(skin.gameObject)}]");
            }
            return description.ToString();
        }

        private static string DescribeClothComponents(GameObject owner)
        {
            bool legacy = owner.GetComponent<Cloth>() != null;
            bool magica = owner.GetComponent<MagicaCloth2.MagicaCloth>() != null;

            if (legacy && magica) return "legacy+magica";
            if (legacy) return "legacy";
            if (magica) return "magica";
            return "none";
        }
    }
}
