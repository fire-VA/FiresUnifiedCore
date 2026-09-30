using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.UI
{
    // Opening the 1.0 build menu froze the client for 1.8 s with FAP's full prefab list in the hammer (2026-09-27 run).
    // Three vanilla costs scale with the button count; each is cut here without changing what the menu shows.
    //
    // 1. BuildUiPieceButton.Setup appends RefreshSearchTerm to the static Localization.OnLanguageChange on every call and
    //    never removes it. Buttons are pooled and re-Setup on every open, tab switch and search keystroke, so the list
    //    grows all session and every append copies it. The append is removed; one handler refreshes live buttons instead.
    // 2. BuildUi.ConfigureButtonNavigation (GridNavigationUtility over every button) runs 2-3 times per open and on every
    //    search keystroke, and only a gamepad uses it. It is skipped while no gamepad is active and run once, the first
    //    frame one is, while the menu is open.
    // 3. Each button's Setup checks CanBuild, which scans for a crafting station in range per piece. Within one
    //    UpdatePieceButtons the player has not moved, so the answer is cached per station name for that call.
    internal static class BuildUiSpeedups
    {
        private static readonly FieldInfo LanguageChangeField = AccessTools.Field(typeof(Localization), nameof(Localization.OnLanguageChange));
        private static readonly MethodInfo ConfigureNavigation = AccessTools.Method(typeof(BuildUi), "ConfigureButtonNavigation");

        private static bool s_languageHandlerAdded;
        private static bool s_navigationPending;
        private static bool s_stationCacheActive;
        private static readonly Dictionary<string, CraftingStation> s_stationCache = new Dictionary<string, CraftingStation>();

        private static bool GamepadInUse() => ZInput.IsGamepadActive() || ZInput.IsGamepadMouseActive();

        // ── 1. Language-change leak ─────────────────────────────────────────────
        [HarmonyPatch(typeof(BuildUiPieceButton), nameof(BuildUiPieceButton.Setup))]
        private static class PieceButtonSetup
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var code = new List<CodeInstruction>(instructions);
                int start = code.FindIndex(c => c.opcode == OpCodes.Ldsfld && Equals(c.operand, LanguageChangeField));
                int end = start < 0 ? -1 : code.FindIndex(start, c => c.opcode == OpCodes.Stsfld && Equals(c.operand, LanguageChangeField));
                if (start < 0 || end < 0)
                {
                    FiresLogger.LogWarning("[BuildUiSpeedups] BuildUiPieceButton.Setup no longer appends to Localization.OnLanguageChange "
                        + "the way 1.0.15 did; left as it is.");
                    return code;
                }
                // Blank the whole "OnLanguageChange = Combine(OnLanguageChange, RefreshSearchTerm)" statement in place, so any
                // label on its first instruction still lands on a valid nop.
                for (int i = start; i <= end; i++)
                {
                    code[i].opcode = OpCodes.Nop;
                    code[i].operand = null;
                }
                return code;
            }

            private static void Postfix()
            {
                if (s_languageHandlerAdded) return;
                s_languageHandlerAdded = true;
                Localization.OnLanguageChange += RefreshLiveButtons;
            }
        }

        private static void RefreshLiveButtons()
        {
            foreach (var button in Resources.FindObjectsOfTypeAll<BuildUiPieceButton>())
            {
                if (button == null || button.Piece == null || !button.gameObject.scene.IsValid()) continue;
                try { button.RefreshSearchTerm(); }
                catch (Exception ex) { FiresLogger.LogWarning($"[BuildUiSpeedups] search term refresh failed: {ex.Message}"); }
            }
        }

        // ── 2. Gamepad-only navigation ──────────────────────────────────────────
        [HarmonyPatch(typeof(BuildUi), "ConfigureButtonNavigation")]
        private static class SkipNavigationWithoutGamepad
        {
            private static bool Prefix()
            {
                if (GamepadInUse())
                {
                    s_navigationPending = false;
                    return true;
                }
                s_navigationPending = true;
                return false;
            }
        }

        [HarmonyPatch(typeof(BuildUi), "Update")]
        private static class NavigationOnFirstGamepadFrame
        {
            private static void Postfix(BuildUi __instance)
            {
                if (!s_navigationPending || ConfigureNavigation == null || !GamepadInUse()) return;
                if (!__instance.gameObject.activeInHierarchy) return;
                ConfigureNavigation.Invoke(__instance, null);
            }
        }

        // ── 3. One station scan per station per rebuild ─────────────────────────
        [HarmonyPatch(typeof(BuildUi), "UpdatePieceButtons")]
        private static class StationCacheScope
        {
            private static void Prefix()
            {
                s_stationCache.Clear();
                s_stationCacheActive = true;
            }

            private static Exception Finalizer(Exception __exception)
            {
                s_stationCacheActive = false;
                s_stationCache.Clear();
                return __exception;
            }
        }

        [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.HaveBuildStationInRange))]
        private static class CachedStationInRange
        {
            private static bool Prefix(string name, ref CraftingStation __result)
            {
                if (!s_stationCacheActive || name == null || !s_stationCache.TryGetValue(name, out var cached)) return true;
                __result = cached;
                return false;
            }

            // Last, so the cached answer is the one other mods' postfixes settled on.
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(string name, CraftingStation __result)
            {
                if (s_stationCacheActive && name != null) s_stationCache[name] = __result;
            }
        }
    }
}
