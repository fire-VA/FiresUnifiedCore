using UnityEngine;

namespace FiresCore.Input
{
    // ONE gate every Fires world-editing tool asks before it acts.
    //
    // The bug it exists to kill: vanilla places pieces off ZInput's EDGE
    // ("button went down this frame"), so the click that picks a piece out of
    // the hammer menu can never also place it. Our brush/drag tools instead
    // read Input.GetMouseButton(0) — a LEVEL read, true for as long as the
    // button is held — so the very click that selected the tool and closed the
    // menu was still held on the next frame and immediately fired the tool.
    // Selecting Vfill placed a fill on the spot.
    //
    // The rule: after ANY selection UI is open (or has just closed), the mouse
    // must be RELEASED before a tool may act again. A tool selection is never
    // also a tool use.
    //
    // Tools call ReadyToAct() each frame and do nothing while it is false.
    // Anything that changes what the tool would do (a new tool, a new piece,
    // a mode switch) should also call RequireFreshPress().
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
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es != null && es.IsPointerOverGameObject()) return true;
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
