using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// Stops the per-frame NRE spam from a destroyed Character left in Character.s_characters. Vanilla's
    /// OnDestroy removes the instance only after m_seman.OnDestroy, so a throw there (a null SEMan on a baked
    /// NPC) strands it for AnimalAI and EnemyHud to trip over. A finalizer on Character.OnDestroy always
    /// removes the instance and logs the culprit once, a MonoUpdaters.FixedUpdate prefix sweeps entries that
    /// already leaked, and client-only EnemyHud.TestShow guards skip destroyed characters. Attached explicitly
    /// from Setup with a read-back count, because attribute discovery silently failed to attach in 0.1.60.
    /// </summary>
    internal static class CharacterListLeakGuard
    {
        private const int SweepLogIntervalFrames = 600;

        private static readonly FieldInfo _f_sCharacters = AccessTools.Field(typeof(Character), "s_characters");
        private static List<Character> _sCharacters;
        private static readonly HashSet<string> _loggedThrowers = new HashSet<string>();
        private static int _lastSweepLogFrame = -100000;

        internal static void Register(Harmony harmony, bool clientSide)
        {
            TryPatch(harmony, typeof(Character), "OnDestroy", null,
                finalizer: nameof(OnDestroy_Finalizer));
            TryPatch(harmony, typeof(MonoUpdaters), "FixedUpdate", null,
                prefix: nameof(Sweep_Prefix));
            if (clientSide)
            {
                TryPatch(harmony, typeof(EnemyHud), "TestShow", new[] { typeof(Character), typeof(bool) },
                    prefix: nameof(TestShow_NullGuard),
                    finalizer: nameof(TestShow_Finalizer));
            }
        }

        private static void TryPatch(Harmony harmony, Type target, string method, Type[] args,
            string prefix = null, string finalizer = null)
        {
            try
            {
                var original = args != null ? AccessTools.Method(target, method, args) : AccessTools.Method(target, method);
                if (original == null)
                {
                    Debug.LogError($"[CharacterLeakGuard] {target.Name}.{method} NOT FOUND — guard not attached.");
                    return;
                }
                harmony.Patch(original,
                    prefix: prefix != null ? new HarmonyMethod(AccessTools.Method(typeof(CharacterListLeakGuard), prefix)) { priority = Priority.First } : null,
                    finalizer: finalizer != null ? new HarmonyMethod(AccessTools.Method(typeof(CharacterListLeakGuard), finalizer)) : null);

                var info = Harmony.GetPatchInfo(original);
                int mine = 0;
                if (info != null)
                {
                    foreach (var patch in info.Prefixes) if (patch.PatchMethod.DeclaringType == typeof(CharacterListLeakGuard)) mine++;
                    foreach (var patch in info.Finalizers) if (patch.PatchMethod.DeclaringType == typeof(CharacterListLeakGuard)) mine++;
                }
                Debug.Log($"[CharacterLeakGuard] attached {mine} patch(es) to {target.Name}.{method} (verified by read-back).");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CharacterLeakGuard] FAILED to attach {target.Name}.{method}: {ex.Message}");
            }
        }

        internal static List<Character> Chars()
            => _sCharacters ??= _f_sCharacters?.GetValue(null) as List<Character>;

        internal static string SafeName(Character character)
        {
            try { return character != null ? character.name : "<null>"; }
            catch { return "<destroyed>"; }
        }

        // 1) Root fix + diagnostic. Guarantees the Remove and names the culprit.
        private static Exception OnDestroy_Finalizer(Character __instance, Exception __exception)
        {
            if (__exception == null) return null;          // body completed — it ran its own Remove
            if (__instance != null)
            {
                Chars()?.Remove(__instance);               // salvage the removal the throw skipped
                string name = SafeName(__instance);
                if (_loggedThrowers.Add(name))
                    Debug.LogError(
                        $"[CharacterLeakGuard] Character.OnDestroy threw for '{name}' — forced its removal from " +
                        $"s_characters so it can't NRE-spam AnimalAI / EnemyHud. Fix the root: " +
                        $"{__exception.GetType().Name}: {__exception.Message}");
            }
            return null;                                    // swallow: cleanup salvaged, root logged
        }

        // 2) Sweep any already-leaked / OnDestroy-never-ran dead entries before the AI pass reads
        //    the list. RemoveAll is in-place + allocation-free; Unity's == null catches destroyed.
        private static void Sweep_Prefix()
        {
            var list = Chars();
            if (list == null || list.Count == 0) return;
            int before = list.Count;
            list.RemoveAll(c => c == null);
            int removed = before - list.Count;
            if (removed > 0 && Time.frameCount - _lastSweepLogFrame > SweepLogIntervalFrames)
            {
                _lastSweepLogFrame = Time.frameCount;
                Debug.LogWarning(
                    $"[CharacterLeakGuard] Swept {removed} destroyed Character(s) from s_characters " +
                    $"(residual leak — OnDestroy likely never ran for them).");
            }
        }

        // 3a) Destroyed Character short-circuits to "don't show" instead of dereferencing its dead
        //     transform (Unity == null is true for destroyed objects).
        private static bool TestShow_NullGuard(Character c, ref bool __result)
        {
            if (c == null) { __result = false; return false; }
            return true;
        }

        // 3b) Backstop: whatever TestShow throws for a single character (dead transform mid-frame,
        //     missing component), swallow it and hide that hud instead of killing the whole
        //     EnemyHud.LateUpdate pass. Logged once per exception type so the cause stays visible.
        private static Exception TestShow_Finalizer(Exception __exception, ref bool __result)
        {
            if (__exception == null) return null;
            __result = false;
            if (_loggedThrowers.Add("TestShow|" + __exception.GetType().Name))
                Debug.LogWarning(
                    $"[CharacterLeakGuard] EnemyHud.TestShow threw {__exception.GetType().Name} ('{__exception.Message}') — " +
                    $"swallowed; hud hidden for that character this frame.");
            return null;
        }
    }
}
