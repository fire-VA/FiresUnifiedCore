using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Kills the "100x hair/beard" attachments on our NPC bodies (static NPCs, companions, wilds).
    ///
    /// Root cause: vanilla <c>VisEquipment.AttachItem</c> instantiates the item prefab's "attach"
    /// child and sets localPosition/localRotation — but NEVER localScale. The instance keeps the
    /// attach child's authored LOCAL scale while losing the prefab ROOT's scale factor and
    /// inheriting the target joint's scale instead. Vanilla items author everything at scale 1 so
    /// nobody notices; modded hair/beard prefabs (ObjectDB-sourced styles) frequently carry non-1
    /// root scales with compensating children — attach one of those to a head bone and it renders
    /// orders of magnitude wrong.
    ///
    /// Fix: after attach, force the instance's WORLD scale back to the attach child's authored
    /// world scale (root factor included): localScale = authoredLossy / joint.lossyScale. Scoped to
    /// our companion-stack bodies only (CompanionController present) so players/vanilla creatures
    /// keep exact vanilla behavior. attach_skin instances are skipped — skinned meshes follow the
    /// body's bones, transform scale is irrelevant there.
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

            Vector3 corrected = new Vector3(authored.x / jointLossy.x, authored.y / jointLossy.y, authored.z / jointLossy.z);

            // Only touch it when meaningfully wrong — vanilla-authored items already match, and a
            // no-op write every re-equip would be pointless churn.
            Vector3 current = __result.transform.localScale;
            if ((corrected - current).sqrMagnitude < 0.0001f) return;

            __result.transform.localScale = corrected;
        }
    }
}
