using HarmonyLib;
using UnityEngine;

namespace FiresCore.World.Halo
{
    /// <summary>
    /// Core port of FDT 1.1.1021's HaloSpike switch-on ([visual], 10-24). The ring motor / camera / creature patches are applied ONCE, the
    /// first time a GravityUp field becomes active (FAT's ring via FatApi, or any registered provider), checked every 2 s. A normal world
    /// never patches anything (zero cost off the ring). FDT 1.1.1022+ looks for this type ("FiresCore.World.Halo.RingBoot") and stands its
    /// own spike down, so the two never both patch (a double frame = a double rotation).
    /// Proof lines: "[Halo] ring motor patches applied: N ok, N failed; N methods / N calls retargeted" (FDT's spike printed 65 / 168).
    /// </summary>
    [HarmonyPatch(typeof(Game), "Update")]
    public static class RingBoot
    {
        /// <summary>True once Core's ring motor has been applied in this process.</summary>
        public static bool Applied => RingPatcher.Applied;

        private static float s_next;

        [HarmonyPostfix]
        private static void Postfix()
        {
            if (RingPatcher.Applied || Time.unscaledTime < s_next) return;
            s_next = Time.unscaledTime + 2f;
            if (!GravityUp.Active) return;
            RingPatcher.Apply();
        }
    }
}
