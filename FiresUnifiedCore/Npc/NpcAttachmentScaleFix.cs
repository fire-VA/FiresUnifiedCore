using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Sizes rigid attachments (hair, beards, helmets, weapons) on Fires NPC bodies: the attach child's authored world
    /// scale times the body's own scale, so a giant's sword and hair grow with the giant and a dwarf's shrink.
    /// VisEquipment.AttachItem keeps the item's world size and never touches scale, which pins items to player size on
    /// scaled bodies and made modded styles whose root scale is not 1 render a hundred times too large. Only bodies with
    /// a CompanionController are touched; skinned attachments follow the bones and are skipped.
    /// </summary>
    [HarmonyPatch(typeof(VisEquipment), "AttachItem")]
    internal static class NpcAttachmentScaleFix
    {
        private static void Postfix(VisEquipment __instance, int itemHash, Transform joint, bool backAttach, GameObject __result)
        {
            if (__result == null || joint == null || __instance == null) return;
            if (__instance.GetComponent<CompanionController>() == null) return;   // our bodies only

            // Skinned attaches ride the body's bones — leave them alone (also: __result is parented
            // to the body model there, not the joint we were handed).
            if (__result.transform.parent != joint) return;

            var itemPrefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(itemHash) : null;
            if (itemPrefab == null) return;

            // Re-derive the same attach child vanilla picked so we read ITS authored world scale.
            Transform original = null;
            int childCount = itemPrefab.transform.childCount;
            for (int i = 0; i < childCount; i++)
            {
                Transform child = itemPrefab.transform.GetChild(i);
                if (backAttach && child.gameObject.name == "attach_back") { original = child; break; }
                if (child.gameObject.name == "attach" || (!backAttach && child.gameObject.name == "attach_skin")) { original = child; break; }
            }
            if (original == null) return;

            Vector3 authored = original.lossyScale;
            Vector3 jointLossy = joint.lossyScale;
            if (Mathf.Approximately(jointLossy.x, 0f) || Mathf.Approximately(jointLossy.y, 0f) || Mathf.Approximately(jointLossy.z, 0f))
                return;

            float bodyScale = __instance.transform.lossyScale.y;
            Vector3 corrected = new Vector3(authored.x * bodyScale / jointLossy.x, authored.y * bodyScale / jointLossy.y, authored.z * bodyScale / jointLossy.z);

            // Only touch it when meaningfully wrong — vanilla-authored items already match, and a
            // no-op write every re-equip would be pointless churn.
            Vector3 current = __result.transform.localScale;
            if ((corrected - current).sqrMagnitude < 0.0001f) return;

            __result.transform.localScale = corrected;
        }
    }
}
