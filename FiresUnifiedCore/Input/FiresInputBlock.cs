using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace FiresCore.Input
{
    // Lightweight TEXT-CAPTURE input gate, shared across the Fires mod family.
    // While any Fires UI text field is focused (or a Fires modal claims input),
    // this swallows hotkey TRIGGERS so neither vanilla nor other mods act on the
    // keys you are typing into a Fires field.
    //
    // Distinct from FiresCore.UI.InputBlock (the heavy MODAL block that pins the
    // camera, frees the cursor and gates the minimap/inventory). This one does
    // NOT touch the camera or cursor — the build menu stays interactive behind a
    // focused search bar — it only short-circuits input READS:
    //   * Player.TakeInput                       -> false   (vanilla move/use/attack/hotbar/place)
    //   * ZInput Get{Button,Key,MouseButton}{Down,Up} -> false
    //       (other mods' hotkeys routed through Valheim's ZInput layer, plus
    //        vanilla chat/console open which polls ZInput directly)
    //
    // Coverage limit (by design): mods that poll UnityEngine.Input.GetKeyDown
    // directly — including BepInEx ConfigEntry<KeyboardShortcut> — cannot be
    // blocked. UnityEngine.Input.* is an extern native call Harmony cannot patch,
    // and that same raw-Input path is what every Fires UI uses for its own
    // ESC-to-close, so it must stay live. Held-state reads (GetButton/GetKey/
    // GetMouseButton) and the analog mouse-delta are intentionally NOT gated so
    // camera/analog UI don't freeze; only the press/release EDGES are suppressed.
    //
    // Usage (refcounted by an opaque token — a window instance or a stable field
    // key): Acquire(token) when a field gains focus / a modal opens, Release(token)
    // when it blurs / closes. ReleaseAll() force-clears on teardown.
    public static class FiresInputBlock
    {
        private static readonly HashSet<object> _tokens = new HashSet<object>();

        public static bool IsCapturing { get; private set; }

        // Per-consumer log tag seam (set once at the consuming mod's startup),
        // mirroring UIBuilderHost.ModName.
        public static string LogTag = "FiresCore";

        public static event Action<bool> CapturingChanged;

        // ── Diagnostics ───────────────────────────────────────────────────
        private static bool _diagLogged;
        private static bool _loggedTakeInput;
        private static bool _loggedZInput;

        // Logs (once) whether the Harmony patches actually ATTACHED. ZInput.* is
        // patched ONLY by this module, so its presence in the patched-method set
        // is a clean yes/no on whether our gate installed (Player.TakeInput is
        // ambiguous — FAP's config UIs patch it too).
        private static void LogPatchStatus()
        {
            if (_diagLogged) return;
            _diagLogged = true;
            try
            {
                var patched = new HashSet<MethodBase>(Harmony.GetAllPatchedMethods());
                bool playerCtl = Contains(patched, AccessTools.Method(typeof(PlayerController), "TakeInput", new[] { typeof(bool) }));
                bool takeInput = Contains(patched, AccessTools.Method(typeof(Player), "TakeInput"));
                bool btnDown   = Contains(patched, AccessTools.Method(typeof(ZInput), "GetButtonDown", new[] { typeof(string) }));
                FiresCore.Logging.FiresLogger.LogInfo(
                    $"[{LogTag}] INPUT-BLOCK PATCH STATUS — PlayerController.TakeInput(move/jump)={playerCtl} " +
                    $"Player.TakeInput(build/use)={takeInput} ZInput.GetButtonDown={btnDown} " +
                    $"(PlayerController.TakeInput False = movement/jump NOT blocked)");
            }
            catch (Exception ex)
            {
                FiresCore.Logging.FiresLogger.LogWarning($"[{LogTag}] input-block patch-status check failed: {ex.Message}");
            }
        }

        private static bool Contains(HashSet<MethodBase> set, MethodBase m) => m != null && set.Contains(m);

        internal static void LogZInputSuppressOnce()
        {
            if (_loggedZInput) return;
            _loggedZInput = true;
            try { FiresCore.Logging.FiresLogger.LogInfo($"[{LogTag}] ZInput hotkey read SUPPRESSED while capturing (gate working)"); } catch { }
        }

        public static void Acquire(object token)
        {
            if (token == null || !_tokens.Add(token)) return;
            Recompute();
        }

        public static void Release(object token)
        {
            if (token == null || !_tokens.Remove(token)) return;
            Recompute();
        }

        public static void ReleaseAll()
        {
            if (_tokens.Count == 0) return;
            _tokens.Clear();
            Recompute();
        }

        private static void Recompute()
        {
            bool now = _tokens.Count > 0;
            if (now == IsCapturing) return;
            IsCapturing = now;
            if (now) { _loggedTakeInput = false; _loggedZInput = false; LogPatchStatus(); }
            try { FiresCore.Logging.FiresLogger.LogInfo($"[{LogTag}] text-capture input gate {(now ? "ON" : "OFF")} (tokens={_tokens.Count})"); }
            catch { }
            try { CapturingChanged?.Invoke(now); }
            catch { }
        }

        // Vanilla input kill — server-safe (Player exists on a dedicated server),
        // so this patch is NOT in the dedi-skip set.
        [HarmonyPatch(typeof(Player), "TakeInput")]
        private static class TakeInput_CaptureGate
        {
            private static bool Prefix(ref bool __result)
            {
                if (!IsCapturing) return true;
                if (!_loggedTakeInput) { _loggedTakeInput = true; try { FiresCore.Logging.FiresLogger.LogInfo($"[{LogTag}] Player.TakeInput SUPPRESSED while capturing (gate working)"); } catch { } }
                __result = false;
                return false;
            }
        }
    }

    // CLIENT-ONLY input gates. This container's full name is listed in
    // FiresUnifiedCore.DedicatedServerSkipPatchTypes (the skip walks declaring-type
    // parents, so listing the container skips every nested gate) so a headless
    // PatchAll never imports ZInput / PlayerController's UI refs and native-crashes
    // Mono's IL rewriter.
    //
    // PlayerController.TakeInput is THE gate that zeroes MOVEMENT + JUMP
    // (PlayerController.FixedUpdate: `if (!TakeInput()) SetControls(zero,...)`). It
    // is a DIFFERENT method from Player.TakeInput (which only gates build/use/
    // hotbar) — patching Player.TakeInput alone is exactly why the character still
    // jumped while typing in a Fires field.
    public static class FiresInputBlockClientGates
    {
        private static bool Gate(ref bool __result)
        {
            if (!FiresInputBlock.IsCapturing) return true;
            FiresInputBlock.LogZInputSuppressOnce();
            __result = false;
            return false;
        }

        // The movement/jump/look gate.
        [HarmonyPatch(typeof(PlayerController), "TakeInput", new Type[] { typeof(bool) })]
        private static class PlayerControllerTakeInputGate
        {
            private static bool Prefix(ref bool __result)
            {
                if (!FiresInputBlock.IsCapturing) return true;
                __result = false;
                return false;
            }
        }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonDown))]
        private static class GetButtonDownGate { private static bool Prefix(ref bool __result) => Gate(ref __result); }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonUp))]
        private static class GetButtonUpGate { private static bool Prefix(ref bool __result) => Gate(ref __result); }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetKeyDown))]
        private static class GetKeyDownGate { private static bool Prefix(ref bool __result) => Gate(ref __result); }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetKeyUp))]
        private static class GetKeyUpGate { private static bool Prefix(ref bool __result) => Gate(ref __result); }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseButtonDown))]
        private static class GetMouseButtonDownGate { private static bool Prefix(ref bool __result) => Gate(ref __result); }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseButtonUp))]
        private static class GetMouseButtonUpGate { private static bool Prefix(ref bool __result) => Gate(ref __result); }
    }
}
