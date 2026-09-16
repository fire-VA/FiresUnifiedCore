using UnityEngine;

namespace FiresCore.Input
{
    // The gate every Fires world-editing tool checks before acting. Vanilla places on a button-down edge, but the
    // brush and drag tools read the held button, so the click that chose a tool from the menu fired it on the next
    // frame. After any selection UI opens or closes the mouse must be released first. Tools call ReadyToAct each
    // frame, and anything that changes what a tool would do calls RequireFreshPress.
    public static class FiresPlaceGate
    {
        private static bool s_needRelease;
        private static bool s_uiWasOpen;
        private static int s_tickedFrame = -1;

        // Arm the gate: nothing may act until the mouse comes up again.
        public static void RequireFreshPress() => s_needRelease = true;

        // True when a tool is allowed to act this frame. Self-ticking so callers
        // don't have to be wired into an update order.
        public static bool ReadyToAct()
        {
            Tick();
            return !s_needRelease;
        }

        private static void Tick()
        {
            int frame = Time.frameCount;
            if (frame == s_tickedFrame) return;
            s_tickedFrame = frame;

            bool uiOpen = IsSelectionUiOpen();
            // Open, or closed THIS frame: either way the in-flight click belongs
            // to the UI, not to the world.
            if (uiOpen || s_uiWasOpen) s_needRelease = true;
            s_uiWasOpen = uiOpen;

            // Only an actual release clears it — never a timer, so a held button
            // can't leak through on a slow frame.
            if (s_needRelease
                && !UnityEngine.Input.GetMouseButton(0)
                && !UnityEngine.Input.GetMouseButton(1))
                s_needRelease = false;
        }

        // Any UI that can hand the world a stray click: the hammer piece menu,
        // inventory/crafting, the pause menu, a text field, or the pointer
        // sitting over any uGUI element (our own panels included).
        private static bool IsSelectionUiOpen()
        {
            try
            {
                if (FiresInputBlock.IsCapturing) return true;
                if (Hud.IsPieceSelectionVisible()) return true;
                if (InventoryGui.IsVisible()) return true;
                if (Menu.IsVisible()) return true;
                if (TextInput.IsVisible()) return true;
                var eventSystem = UnityEngine.EventSystems.EventSystem.current;
                if (eventSystem != null && eventSystem.IsPointerOverGameObject()) return true;
            }
            catch
            {
                // A UI singleton torn down mid-frame must not strand the gate
                // closed — treat an unreadable state as "no UI".
            }
            return false;
        }
    }
}
