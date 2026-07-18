using System;
using HarmonyLib;

namespace FiresCore.Dungeon
{
    /// <summary>
    /// Suppresses the PROXIMITY auto-teleport on a dungeon's interior exit when its owning spec sets
    /// <see cref="DungeonSpec.RequireInteractToExit"/>. Vanilla <c>Teleport.OnTriggerEnter</c> calls
    /// <c>Interact</c> the instant the local player's collider enters the exit's trigger volume, so a player who
    /// walks near the exit (e.g. to read a headstone beside the mausoleum door) is ejected. This prefix no-ops
    /// OnTriggerEnter for exit teleports belonging to an opted-in spec (matched by <see cref="DungeonSpec.ExitNamePrefix"/>),
    /// leaving the [Use]/Interact path fully intact so pressing Use still exits.
    ///
    /// Client-only in effect: OnTriggerEnter early-returns unless the entering collider is the LOCAL player, so on a
    /// dedicated server it never fires — the patch is inert there but harmless (no client-only refs).
    /// </summary>
    [HarmonyPatch(typeof(Teleport), "OnTriggerEnter")]
    internal static class DungeonExitInteractPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(Teleport __instance)
        {
            if (__instance == null) return true;
            string name = __instance.gameObject.name;
            if (string.IsNullOrEmpty(name)) return true;

            foreach (var spec in FiresDungeonRegistry.All)
            {
                if (spec == null || !spec.RequireInteractToExit) continue;
                if (string.IsNullOrEmpty(spec.ExitNamePrefix)) continue;
                if (name.StartsWith(spec.ExitNamePrefix, StringComparison.Ordinal))
                    return false; // opted-in exit → skip the proximity auto-teleport; [Use] still works
            }
            return true;
        }
    }
}
