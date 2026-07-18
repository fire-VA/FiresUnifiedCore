using FiresCore.UI.ContextMenu;
using UnityEngine;

namespace FiresCore.Utilities
{
    /// <summary>
    /// Shift+Alt right-click a placed Environment Box to open its config popup (<see cref="FiresCore.UI.EnvironmentBoxPanel"/>)
    /// — the same claim pattern as FDM's entry doors: the Core driver frees the cursor + raycasts on Shift+Alt
    /// right-click and hands each claim the ContextTarget; a box hit opens the panel and suppresses the shared list
    /// menu. Non-box hits return false so other claims run. Admin-gated (non-admins still claim so the box never
    /// shows a menu to them). Registered lazily from the controller's Awake. Client-only.
    /// </summary>
    internal static class EnvironmentBoxContextMenu
    {
        private static bool _registered;
        private static FiresContextMenu.TargetClaim _claim;

        public static void EnsureRegistered()
        {
            if (_registered) return;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return; // headless
            _registered = true;
            _claim = HandleClaim;
            FiresContextMenu.RegisterClaim(_claim);
            Debug.Log("[FiresCore] environment-box config claim registered (Shift+Alt right-click a box).");
        }

        private static bool HandleClaim(ContextTarget target, Vector2 screenPos)
        {
            var box = Resolve(target);
            if (box == null) return false;                                       // not a box → other claims / shared menu
            if (!EnvironmentBoxController.IsLocalPlayerAdmin()) return true;      // non-admins: claim silently
            FiresCore.UI.EnvironmentBoxPanel.Open(box);
            return true;
        }

        private static EnvironmentBoxController Resolve(ContextTarget target)
        {
            if (target == null) return null;
            var box = target.Find<EnvironmentBoxController>();
            if (box == null && target.Collider != null) box = target.Collider.GetComponentInParent<EnvironmentBoxController>();

            // The driver's raycast IGNORES triggers, and a box's only collider IS its trigger volume — so a click on
            // the box passes through to the terrain behind/inside it. If the hit is world background (no ZNetView —
            // never a companion/piece/door another claim owns), resolve the box CONTAINING the clicked point; failing
            // that, the box containing the PLAYER (flying 400m up inside a 500m box, aiming at sky/horizon: the point
            // may fall outside the volume but the admin plainly means "this box I'm in").
            if (box == null && target.NView == null)
            {
                box = EnvironmentBoxController.GetEnvBoxAtPosition(target.Point);
                if (box == null && Player.m_localPlayer != null)
                    box = EnvironmentBoxController.GetEnvBoxAtPosition(Player.m_localPlayer.transform.position);
            }
            return box;
        }
    }
}
