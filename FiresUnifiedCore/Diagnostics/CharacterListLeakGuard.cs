using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// Stops the per-frame NRE spam caused by a destroyed Character lingering in the static
    /// <c>Character.s_characters</c> list. That list is iterated (and <c>.transform</c> dereferenced)
    /// by <c>Character.IsCharacterInRange</c> (AnimalAI.UpdateAI — server + client) and by
    /// <c>EnemyHud.LateUpdate</c> (client) — a single dead entry throws every FixedUpdate/LateUpdate.
    ///
    /// Root cause: vanilla <c>Character.OnDestroy</c> runs <c>m_seman.OnDestroy()</c> BEFORE its own
    /// <c>s_characters.Remove(this)</c>; if the body throws first (e.g. a null SEMan on a baked NPC)
    /// the Remove is skipped and the dead Character is stuck in the list forever.
    ///
    /// Attachment is EXPLICIT via <see cref="Register"/> (called from FiresUnifiedCore.Setup), not
    /// attribute discovery: the 0.1.60 attribute-based version compiled into the DLL but never
    /// attached in the field (zero guard logs while the TestShow NRE persisted) — the same
    /// silent-miss symptom FGN's AILODPatches hit. Explicit Harmony.Patch with a read-back count
    /// makes attachment deterministic and self-proving in the boot log.
    ///
    /// Patches:
    ///   1. Finalizer on Character.OnDestroy (both sides) — always removes the instance from
    ///      s_characters even when the body threw, and logs the culprit prefab + exception ONCE.
    ///   2. Prefix sweep on MonoUpdaters.FixedUpdate (both sides) — clears already-leaked dead
    ///      entries before the AI pass iterates the list.
    ///   3. EnemyHud.TestShow prefix + finalizer (client only — patching the client-only EnemyHud
    ///      type on a headless server native-crashes the Mono IL rewriter): destroyed Character
    ///      short-circuits to "don't show"; the finalizer swallows any residual throw so one bad
    ///      entry can never kill the HUD pass.
    /// </summary>
    internal static class CharacterListLeakGuard
    {
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
                var mi = args != null ? AccessTools.Method(target, method, args) : AccessTools.Method(target, method);
                if (mi == null)
                {
                    Debug.LogError($"[CharacterLeakGuard] {target.Name}.{method} NOT FOUND — guard not attached.");
                    return;
                }
                harmony.Patch(mi,
                    prefix: prefix != null ? new HarmonyMethod(AccessTools.Method(typeof(CharacterListLeakGuard), prefix)) { priority = Priority.First } : null,
                    finalizer: finalizer != null ? new HarmonyMethod(AccessTools.Method(typeof(CharacterListLeakGuard), finalizer)) : null);

                var info = Harmony.GetPatchInfo(mi);
                int mine = 0;
                if (info != null)
                {
                    foreach (var p in info.Prefixes) if (p.PatchMethod.DeclaringType == typeof(CharacterListLeakGuard)) mine++;
                    foreach (var p in info.Finalizers) if (p.PatchMethod.DeclaringType == typeof(CharacterListLeakGuard)) mine++;
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

        internal static string SafeName(Character c)
        {
            try { return c != null ? c.name : "<null>"; }
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
            if (removed > 0 && Time.frameCount - _lastSweepLogFrame > 600)
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
