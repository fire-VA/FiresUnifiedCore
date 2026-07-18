using HarmonyLib;
using UnityEngine;

namespace FiresCore.UI
{
    // Single shared input-block for any Fires modal UI. Lifted from FiresNPCs' GUIManager.BlockInput +
    // PlayerInputPatches so there is ONE implementation (Shared -> Core). Consumers call Block(true) on
    // open and Block(false) on close; FiresNPCs' GUIManager.BlockInput now delegates here.
    //
    // - Player.TakeInput -> false: stops movement / attack / use / hotbar.
    // - GameCamera.LateUpdate is PINNED, not skipped. A prefix that skips GameCamera.UpdateCamera stops
    //   vanilla repositioning, but a camera mod applying a RELATIVE offset in GameCamera.LateUpdate
    //   (FiresValcast) then accumulates it every frame and flies the camera away. Instead we let the
    //   camera update and force its transform back, in a lowest-priority postfix that runs last.
    //   The cursor is held free by MouseCaptureGate (UpdateMouseCapture prefix), NOT here — a second per-frame
    //   cursor writer strobed the pointer (see CompanionRadialMenu / FiresLeaderboardInputPatches).
    // - Minimap / InventoryGui Update gates live in InputBlockClientGates (client-only types; that class
    //   is listed in FiresUnifiedCore.DedicatedServerSkipPatchTypes so PatchAll skips it headless).
    public static class InputBlock
    {
        private static bool _blocked;
        private static bool _prevCursorVisible;
        private static CursorLockMode _prevLockState;

        public static bool IsBlocked => _blocked;

        public static void Block(bool block)
        {
            if (block == _blocked) return;
            _blocked = block;

            if (block)
            {
                _prevCursorVisible = Cursor.visible;
                _prevLockState = Cursor.lockState;
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
            else
            {
                Cursor.visible = _prevCursorVisible;
                Cursor.lockState = _prevLockState;
            }
        }

        [HarmonyPatch(typeof(Player), "TakeInput")]
        private static class TakeInputPatch
        {
            private static bool Prefix(ref bool __result)
            {
                if (!_blocked) return true;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(GameCamera), "LateUpdate")]
        private static class CameraPinPatch
        {
            private static bool _pinned;
            private static Vector3 _pos;
            private static Quaternion _rot;

            [HarmonyPostfix]
            [HarmonyPriority(int.MinValue)]
            private static void Postfix(GameCamera __instance)
            {
                // A Fires minigame that owns the camera (MiniGameCamera) drives the transform itself — yield to it
                // so the pin never fights the shot (the reason the arcade/DOOM camera wouldn't reposition before).
                if (MiniGameCamera.Active) { _pinned = false; return; }
                if (!_blocked) { _pinned = false; return; }

                var t = __instance.transform;
                if (_pinned) { t.position = _pos; t.rotation = _rot; }
                else { _pos = t.position; _rot = t.rotation; _pinned = true; }

                // Cursor is held free SOLELY by MouseCaptureGate (the UpdateMouseCapture prefix). Re-asserting it
                // here too made a second per-frame cursor writer that STROBED the pointer for the world-context
                // menu (documented in CompanionRadialMenu / FiresLeaderboardInputPatches). Pin the camera only.
            }
        }
    }

    // Client-only gates: these vanilla types don't exist on a dedicated server, so this whole class is named in
    // FiresUnifiedCore.DedicatedServerSkipPatchTypes and skipped during headless PatchAll.
    public static class InputBlockClientGates
    {
        [HarmonyPatch(typeof(Minimap), "Update")]
        private static class MinimapGate
        {
            private static bool Prefix() => !InputBlock.IsBlocked;
        }

        [HarmonyPatch(typeof(InventoryGui), "Update")]
        private static class InventoryGate
        {
            private static bool Prefix() => !InputBlock.IsBlocked || (InventoryGui.instance != null && InventoryGui.IsVisible());
        }

        // The flash fix — same mechanism FiresRPGmaker's leaderboard uses (FiresLeaderboardInputPatches).
        // Refusing PlayerController.TakeInput stops the camera reading mouse-look while a modal is up; without it
        // the camera rotates every frame and the camera-pin postfix snaps it back = the per-frame HUD jitter.
        // Forcing the cursor free at GameCamera.UpdateMouseCapture (an empty per-frame call site) keeps the
        // pointer visible without relying on the LateUpdate-postfix re-lock dance.
        [HarmonyPatch(typeof(PlayerController), "TakeInput")]
        private static class PlayerControllerTakeInputGate
        {
            private static bool Prefix() => !InputBlock.IsBlocked;
        }

        [HarmonyPatch(typeof(GameCamera), "UpdateMouseCapture")]
        private static class MouseCaptureGate
        {
            private static bool Prefix()
            {
                if (!InputBlock.IsBlocked) return true;
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                return false;
            }
        }
    }
}
