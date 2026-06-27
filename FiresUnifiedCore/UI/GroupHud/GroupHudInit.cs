using HarmonyLib;

namespace FiresCore.UI.GroupHud
{
    /// <summary>
    /// Creates the group HUD as soon as the player's HUD spawns. <c>Hud.Awake</c> only runs on a
    /// client (a dedicated server has no <see cref="Hud"/>), so this is the correct client-only,
    /// correctly-timed entry point — and it re-creates the HUD on every world (re)load since the
    /// controller is parented under the HUD and dies with it.
    /// </summary>
    [HarmonyPatch(typeof(Hud), "Awake")]
    internal static class GroupHudInit
    {
        private static void Postfix() => GroupHudController.Initialize();
    }
}
