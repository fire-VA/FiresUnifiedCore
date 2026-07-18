using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Vanilla <c>VisEquipment.UpdateEquipmentVisuals</c> reads the hair/beard item hashes straight out of
    /// the ZDO every frame and instantiates them itself (no m_isPlayer needed - VisEquipment is ENABLED on
    /// our NPC rigs for armor/body support). On our NPCs, hair and beard are OWNED by the NpcFashionManager
    /// bone-binding pipeline - vanilla's spawner is a SECOND writer. Any stale s_hairItem / s_beardItem hash
    /// persisted by older builds (pre-pipeline-unification static NPCs, old companions) made vanilla spawn a
    /// duplicate attach on the NPC rig, mis-scaled ~100x ("blocks out the sun"), but ONLY on bodies whose
    /// ZDO carried the stale hash - which is why the bug looked intermittent and survived the attach-side
    /// fixes. Single-writer, applied to hair: on any body that carries NpcVisEquipment, force the incoming
    /// hash to 0 (vanilla then destroys any existing instance and attaches nothing) and heal the stale ZDO
    /// entry so the data stops carrying it. Players and vanilla creatures have no NpcVisEquipment - vanilla
    /// behavior there is untouched.
    /// </summary>
    [HarmonyPatch]
    public static class VanillaHairSpawnGate
    {
        // One log per body per session - the suppression itself runs silently every frame.
        private static readonly HashSet<int> _logged = new HashSet<int>();

        [HarmonyPatch(typeof(VisEquipment), "SetHairEquipped")]
        [HarmonyPrefix]
        private static void SetHairEquipped_Prefix(VisEquipment __instance, ref int hash)
        {
            Gate(__instance, ref hash, ZDOVars.s_hairItem, "hair");
        }

        [HarmonyPatch(typeof(VisEquipment), "SetBeardEquipped")]
        [HarmonyPrefix]
        private static void SetBeardEquipped_Prefix(VisEquipment __instance, ref int hash)
        {
            Gate(__instance, ref hash, ZDOVars.s_beardItem, "beard");
        }

        // Deliberately NOT cached: NpcVisEquipment can be added at runtime (static-NPC component wiring runs
        // after the first VisEquipment ticks), so a cached negative would exempt those bodies forever.
        private static void Gate(VisEquipment vis, ref int hash, int zdoKey, string slot)
        {
            if (hash == 0 || vis == null) return;
            if (vis.GetComponent<NpcVisEquipment>() == null) return;

            if (_logged.Add(vis.GetInstanceID()))
                Debug.LogWarning($"[VanillaHairSpawnGate] Suppressed vanilla {slot} spawn (stale ZDO hash {hash}) on " +
                                 $"'{vis.gameObject.name}' - NPC hair/beard is owned by NpcFashionManager. Healing ZDO.");

            hash = 0;

            var nview = vis.m_nview;
            if (nview != null && nview.IsValid() && nview.IsOwner())
                nview.GetZDO().Set(zdoKey, 0);
        }
    }
}
