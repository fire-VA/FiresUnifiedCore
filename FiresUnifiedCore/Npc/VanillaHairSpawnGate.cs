using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Keeps vanilla from spawning hair and beards on Fires NPC rigs, where NpcFashionManager owns them. A stale hair or
    /// beard hash left in an NPC's ZDO by older builds made UpdateEquipmentVisuals attach a second, wildly mis-scaled copy.
    /// On any body with NpcVisEquipment the incoming hash is forced to 0 and the stale ZDO entry is healed; players and
    /// vanilla creatures are untouched.
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
