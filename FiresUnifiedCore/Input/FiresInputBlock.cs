using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Input
{
    // Text-capture input gate for the Fires family: while a Fires text field is focused or a Fires modal claims
    // input, hotkey edges are swallowed so neither vanilla nor other mods act on typed keys. It blocks
    // Player.TakeInput, ZInput's Down/Up reads and BepInEx KeyboardShortcut triggers, but leaves the camera,
    // cursor, held-state reads and mouse deltas alone. Raw UnityEngine.Input polling cannot be patched and stays
    // live, which Fires UIs rely on for ESC. Callers Acquire and Release a token; ReleaseAll clears on teardown.
    // The heavier modal block that pins the camera is FiresCore.UI.InputBlock.
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

        private static bool Contains(HashSet<MethodBase> set, MethodBase candidate) => candidate != null && set.Contains(candidate);

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

        // 0.2.312: tokens whose owning window is gone (see FiresInputBlockDriver.LateUpdate).
        internal static void PruneStaleTokens()
        {
            if (_tokens.Count == 0) return;
            List<object> stale = null;
            foreach (object t in _tokens)
            {
                string why = null;
                if (t is UnityEngine.Object u)
                {
                    if (u == null) why = "destroyed";
                    else if (u is Behaviour b && !b.isActiveAndEnabled) why = "disabled";
                    else if (u is Component c && !c.gameObject.activeInHierarchy) why = "inactive";
                    else if (u is GameObject g && !g.activeInHierarchy) why = "inactive";
                }
                if (why == null) continue;
                (stale ?? (stale = new List<object>())).Add(t);
                try { FiresCore.Logging.FiresLogger.LogDebug($"[{LogTag}] text-capture gate: released a stale token from {t.GetType().FullName} ({why})"); } catch { }
            }
            if (stale == null) return;
            foreach (object t in stale) _tokens.Remove(t);
            Recompute();
        }

        internal static string TokenOwners()
        {
            var names = new List<string>();
            foreach (object t in _tokens) names.Add(t.GetType().FullName);
            return names.Count > 0 ? string.Join(", ", names.ToArray()) : "none";
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
        // 0.2.312 (Fire 10-06: "Up Arrow in chat still doesn't recall"): the keys a text field itself needs always pass the gate,
        // whatever is focused: vanilla's chat/console history (Terminal.UpdateInput reads ZInput.GetButtonDown("ChatUp"/"ChatDown")),
        // and close/submit. Read through __args, so no parameter name is assumed.
        private static readonly HashSet<string> s_passButtons = new HashSet<string>(StringComparer.Ordinal) { "ChatUp", "ChatDown", "JoyDPadUp", "JoyDPadDown" };
        private static readonly HashSet<KeyCode> s_passKeys = new HashSet<KeyCode> { KeyCode.Escape, KeyCode.Return, KeyCode.KeypadEnter };

        private static bool Passes(object[] args)
        {
            object first = args != null && args.Length > 0 ? args[0] : null;
            return (first is string button && s_passButtons.Contains(button)) || (first is KeyCode key && s_passKeys.Contains(key));
        }

        private static bool Gate(object[] __args, ref bool __result)
        {
            if (!FiresInputBlock.IsCapturing || Passes(__args)) return true;
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
        private static class GetButtonDownGate { private static bool Prefix(object[] __args, ref bool __result) => Gate(__args, ref __result); }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetButtonUp))]
        private static class GetButtonUpGate { private static bool Prefix(object[] __args, ref bool __result) => Gate(__args, ref __result); }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetKeyDown))]
        private static class GetKeyDownGate { private static bool Prefix(object[] __args, ref bool __result) => Gate(__args, ref __result); }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetKeyUp))]
        private static class GetKeyUpGate { private static bool Prefix(object[] __args, ref bool __result) => Gate(__args, ref __result); }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseButtonDown))]
        private static class GetMouseButtonDownGate { private static bool Prefix(object[] __args, ref bool __result) => Gate(__args, ref __result); }

        [HarmonyPatch(typeof(ZInput), nameof(ZInput.GetMouseButtonUp))]
        private static class GetMouseButtonUpGate { private static bool Prefix(object[] __args, ref bool __result) => Gate(__args, ref __result); }

        // BepInEx KeyboardShortcut hotkeys (the standard `ConfigEntry<KeyboardShortcut>` pattern most mods use,
        // e.g. another mod's [G] toggle firing while you type a 'g' into a Fires field). IsDown/IsUp/IsPressed
        // are MANAGED wrappers over UnityEngine.Input, so — unlike raw Input.GetKeyDown (extern, unpatchable) —
        // they CAN be gated. Suppressing them while a Fires text field is focused stops cross-mod hotkey leaks
        // without touching raw Input (so each Fires UI's own raw ESC-to-close stays live).
        private static bool _loggedKs;
        private static bool KsGate(ref bool __result)
        {
            if (!FiresInputBlock.IsCapturing) return true;
            if (!_loggedKs) { _loggedKs = true; try { FiresCore.Logging.FiresLogger.LogInfo($"[{FiresInputBlock.LogTag}] BepInEx KeyboardShortcut hotkey SUPPRESSED while typing (gate working)"); } catch { } }
            __result = false;
            return false;
        }

        [HarmonyPatch(typeof(BepInEx.Configuration.KeyboardShortcut), nameof(BepInEx.Configuration.KeyboardShortcut.IsDown))]
        private static class KeyboardShortcutIsDownGate { private static bool Prefix(ref bool __result) => KsGate(ref __result); }

        [HarmonyPatch(typeof(BepInEx.Configuration.KeyboardShortcut), nameof(BepInEx.Configuration.KeyboardShortcut.IsUp))]
        private static class KeyboardShortcutIsUpGate { private static bool Prefix(ref bool __result) => KsGate(ref __result); }

        [HarmonyPatch(typeof(BepInEx.Configuration.KeyboardShortcut), nameof(BepInEx.Configuration.KeyboardShortcut.IsPressed))]
        private static class KeyboardShortcutIsPressedGate { private static bool Prefix(ref bool __result) => KsGate(ref __result); }
    }

    // Client-side driver: engages the text-capture gate whenever a UI text field gains focus and releases it on
    // blur, so no Fires UI has to wire Acquire/Release per field — focusing ANY TMP_InputField / InputField (in
    // a Fires panel, the book, a search bar, etc.) automatically suppresses vanilla + mod hotkeys for the keys
    // you're typing. Spawned client-only from Core (never on a headless server, which has no EventSystem).
    internal sealed class FiresInputBlockDriver : MonoBehaviour
    {
        private static FiresInputBlockDriver _instance;
        private static readonly object AutoToken = new object();

        public static void Ensure()
        {
            if (_instance != null) return;
            var go = new GameObject("FiresInputBlockDriver") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            _instance = go.AddComponent<FiresInputBlockDriver>();
        }

        private void Update()
        {
            if (IsTextFieldFocused()) FiresInputBlock.Acquire(AutoToken);
            else FiresInputBlock.Release(AutoToken);
        }

        private static bool IsTextFieldFocused()
        {
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            var go = eventSystem != null ? eventSystem.currentSelectedGameObject : null;
            if (go == null) return false;

            // A field hidden with its panel (inactive, or the component disabled) can stay the selected object with isFocused
            // still true; it is not taking keys, so it must not hold the gate (the lead, 10-06: only this AutoToken; window-wide
            // tokens other UIs hold are theirs to release).
            if (!go.activeInHierarchy) return false;
            bool focused;
            var tmp = go.GetComponent<TMPro.TMP_InputField>();
            if (tmp != null) focused = tmp.isActiveAndEnabled && tmp.isFocused;
            else { var leg = go.GetComponent<UnityEngine.UI.InputField>(); focused = leg != null && leg.isActiveAndEnabled && leg.isFocused; }
            if (!focused) return false;

            // Only a FIRES UI field should drive the gate. Vanilla text inputs — the F5 console and chat (both
            // Terminals) and the sign/rename/password TextInput dialog — already block the game while open and
            // own their own keystrokes, so engaging the gate there just suppresses the keys you're typing into
            // them (the reported "typing in the vanilla console affects inputs" bug). Skip anything under them.
            if (go.GetComponentInParent<Terminal>() != null) return false;
            if (go.GetComponentInParent<TextInput>() != null) return false;
            // 0.2.312: and by identity. Chat's and the console's input field need not sit under their Terminal component.
            if (IsTerminalField(Chat.instance, go) || IsTerminalField(global::Console.instance, go)) return false;
            return true;
        }

        private static bool IsTerminalField(Terminal terminal, GameObject go)
            => terminal != null && terminal.m_input != null && terminal.m_input.gameObject == go;

        // ── 0.2.312: stale tokens ────────────────────────────────────────────────────────────────────────────────────────
        // A window that holds a token and is destroyed or switched off without releasing it would keep every hotkey dead.
        // Once a second: a token that is a UnityEngine.Object (most windows pass `this`) and is destroyed, disabled or inactive is
        // released, with a Debug line naming its type. A token that is a plain object (a static key) can't be judged and stays: a
        // window may hold the gate on purpose with no text field focused (the minigames, the lobby, the config windows), so there is
        // NO blind "no field focused" release; instead a gate held over 30 s with no field focused is named once at Debug.
        private float _nextPrune;
        private float _heldNoFieldSince = -1f;
        private bool _heldLogged;

        private void LateUpdate()
        {
            if (Time.unscaledTime < _nextPrune) return;
            _nextPrune = Time.unscaledTime + 1f;
            FiresInputBlock.PruneStaleTokens();
            if (!FiresInputBlock.IsCapturing || IsTextFieldFocused() || GUIUtility.keyboardControl != 0) { _heldNoFieldSince = -1f; _heldLogged = false; return; }
            if (_heldNoFieldSince < 0f) _heldNoFieldSince = Time.unscaledTime;
            if (_heldLogged || Time.unscaledTime - _heldNoFieldSince < 30f) return;
            _heldLogged = true;
            try { FiresCore.Logging.FiresLogger.LogDebug($"[{FiresInputBlock.LogTag}] text-capture gate held {Time.unscaledTime - _heldNoFieldSince:0} s with no text field focused, by: {FiresInputBlock.TokenOwners()}"); } catch { }
        }
    }
}
