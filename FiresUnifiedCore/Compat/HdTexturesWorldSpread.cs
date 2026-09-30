using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Compat
{
    // The second half of HDValheimTextures' freeze: its ZNetScene.Awake postfix walks every prefab once per process and
    // re-textures it, 15.3 s in one frame at world load (R35: part of a 26.9 s stall, ~20 s "Not Responding", as the
    // connection starts). Fire (15:47 popups, [perf] 16:1x): spread it too.
    //
    // HdTexturesLoadSpread's ZNetScene.Awake prefix calls Schedule, which sets their Mod.ZnetSceneTexturesReplaced, so their
    // postfix only clears its processed-material list and returns. From the next frame (every mod's ZNetScene.Awake postfix
    // has added its prefabs by then) this runs their body, prefab by prefab, through their own public ReplaceTextures /
    // ReplaceMaterialTextures, LoadingBudgetMs of work a frame until the player spawns and PlayingBudgetMs after.
    // HD sets textures on the prefabs' SHARED materials in place, so an object spawned before its prefab comes up still
    // turns HD when it does. The exceptions are MaterialOverrides that make new material instances or swap meshes, so
    // those prefabs go first, in OverrideBudgetMs slices while a ZNetScene.CreateDestroyObjects prefix holds off creating
    // any object (R36: all at once was 6.1 s). Leaving the world midway hands the pass back to HD for the next world.
    internal static class HdTexturesWorldSpread
    {
        private const string Tag = "[HD textures] ";
        private const double LoadingBudgetMs = 100.0;
        private const double PlayingBudgetMs = 20.0;
        // R36: the 152 override prefabs in one frame took 6.1 s, past Windows' 5 s "Not Responding". They now go in slices of
        // this size while ZNetScene holds off creating objects, so nothing can spawn with the old material or mesh meanwhile.
        private const double OverrideBudgetMs = 1000.0;
        private const float OverrideHoldCapSeconds = 30f;
        private const string ArmorWord = "armor";
        private const string ExcludedPrefix = "GB_";
        private const string ExcludedSuffix = "_TW";
        private const string YggdrasilName = "YggdrasilBranch";
        private const string MainTexture = "_MainTex";
        private const int SampleCount = 8;

        private static FieldInfo s_sceneReplaced;
        private static MethodInfo s_replaceTextures, s_replaceMaterialTextures;
        private static PropertyInfo s_missing, s_overrides;
        private static FieldInfo s_ovGameObject, s_ovEnabled, s_ovMakeNew, s_ovReplaceMesh;

        private static IEnumerator s_work;
        private static bool s_started;
        private static int s_prefabs;
        private static int s_total;
        private static int s_first;
        private static int s_frames;
        private static double s_longestMs;
        private static double s_slowestPrefabMs;
        private static string s_slowestPrefab;
        private static readonly Stopwatch s_clock = new Stopwatch();
        private static bool s_holding;
        private static readonly Stopwatch s_hold = new Stopwatch();

        private static double BudgetMs => Player.m_localPlayer == null ? LoadingBudgetMs : PlayingBudgetMs;

        // Every member the pass uses; the first one missing is returned (and the whole spread load stays off).
        internal static string Resolve(Type mod, Type replacer, Type materialOverride)
        {
            if (materialOverride == null) return "MaterialOverride";
            s_sceneReplaced = AccessTools.Field(mod, "ZnetSceneTexturesReplaced");
            s_replaceTextures = AccessTools.Method(replacer, "ReplaceTextures", new[] { typeof(GameObject) });
            s_replaceMaterialTextures = AccessTools.Method(replacer, "ReplaceMaterialTextures");
            s_missing = AccessTools.Property(replacer, "MissingTextures");
            s_overrides = AccessTools.Property(replacer, "Overrides");
            s_ovGameObject = AccessTools.Field(materialOverride, "GameObject");
            s_ovEnabled = AccessTools.Field(materialOverride, "Enabled");
            s_ovMakeNew = AccessTools.Field(materialOverride, "MakeNewMaterial");
            s_ovReplaceMesh = AccessTools.Field(materialOverride, "ReplaceMesh");
            var members = new List<(string name, object found)>
            {
                ("Mod.ZnetSceneTexturesReplaced", s_sceneReplaced), ("TextureReplacer.ReplaceTextures", s_replaceTextures),
                ("TextureReplacer.ReplaceMaterialTextures", s_replaceMaterialTextures), ("TextureReplacer.MissingTextures", s_missing),
                ("TextureReplacer.Overrides", s_overrides), ("MaterialOverride.GameObject", s_ovGameObject),
                ("MaterialOverride.Enabled", s_ovEnabled), ("MaterialOverride.MakeNewMaterial", s_ovMakeNew),
                ("MaterialOverride.ReplaceMesh", s_ovReplaceMesh),
            };
            return members.FirstOrDefault(m => m.found == null).name;
        }

        // From the ZNetScene.Awake prefix: take HD's one-time pass off that frame.
        internal static void Schedule(object mod)
        {
            if (mod == null || s_work != null || (bool)s_sceneReplaced.GetValue(mod)) return;
            s_sceneReplaced.SetValue(mod, true);
            // Held from this frame, not from the pass's first step: ZNetScene.Update may run before the runner in the next one
            // ([generator]'s review).
            s_holding = true;
            s_hold.Restart();
            s_started = true;
            s_prefabs = 0;
            s_total = 0;
            s_first = 0;
            s_frames = 0;
            s_longestMs = 0.0;
            s_slowestPrefabMs = 0.0;
            s_slowestPrefab = null;
            s_clock.Restart();
            s_work = Pass(mod);
            var runner = new GameObject(nameof(HdTexturesWorldSpread));
            UnityEngine.Object.DontDestroyOnLoad(runner);
            runner.AddComponent<Runner>();
        }

        private sealed class Runner : MonoBehaviour
        {
            private void Update()
            {
                if (s_work == null)
                {
                    Destroy(gameObject);
                    return;
                }
                if (ZNetScene.instance == null)
                {
                    Abandon();
                    return;
                }
                var step = Stopwatch.StartNew();
                bool more;
                try { more = s_work.MoveNext(); }
                catch (Exception ex)
                {
                    FiresLogger.LogError($"{Tag}world pass failed: {ex.GetBaseException().Message}");
                    more = false;
                }
                s_frames++;
                s_longestMs = Math.Max(s_longestMs, step.Elapsed.TotalMilliseconds);
                if (!more) Finish();
            }
        }

        // Left the world midway: HD's own postfix does the whole pass on the next world, as it always did.
        private static void Abandon()
        {
            object mod = HdTexturesLoadSpread.HdMod;
            if (mod != null) s_sceneReplaced.SetValue(mod, false);
            s_holding = false;
            FiresLogger.LogWarning($"{Tag}left the world with the world pass at {s_prefabs}/{s_total} prefabs; HD redoes it on the next world.");
            s_work = null;
        }

        private static void Finish()
        {
            s_work = null;
            s_holding = false;
            s_clock.Stop();
            FiresLogger.LogInfo($"{Tag}world pass: {s_prefabs} prefabs ({s_first} with instancing/mesh overrides first) in "
                                + $"{s_clock.Elapsed.TotalSeconds:0.0} s over {s_frames} frame(s), longest frame {s_longestMs:0} ms, "
                                + $"slowest prefab {s_slowestPrefab ?? "none"} ({s_slowestPrefabMs:0} ms of work).");
        }

        // Their ZNetScene_Awake postfix body, prefab by prefab.
        private static IEnumerator Pass(object mod)
        {
            yield return null;
            var scene = ZNetScene.instance;
            if (scene == null) yield break;
            object replacer = HdTexturesLoadSpread.Replacer(mod);
            var prefabs = scene.m_prefabs.Where(p => p != null).ToList();
            s_total = prefabs.Count;
            int missingAtStart = MissingCount(replacer);
            var first = FirstNames(replacer);
            var frame = Stopwatch.StartNew();
            foreach (var prefab in prefabs.Where(p => first.Contains(p.name)))
            {
                var steps = DoPrefab(replacer, prefab, frame, () => OverrideBudgetMs);
                while (steps.MoveNext()) yield return steps.Current;
                s_first++;
                if (frame.Elapsed.TotalMilliseconds < OverrideBudgetMs) continue;
                yield return null;
                frame.Restart();
            }
            s_holding = false;
            FiresLogger.LogInfo($"{Tag}world pass: held object creation {s_hold.Elapsed.TotalSeconds:0.0} s while {s_first} override prefab(s) were done.");
            frame.Restart();
            foreach (var prefab in prefabs.Where(p => !first.Contains(p.name)))
            {
                var steps = DoPrefab(replacer, prefab, frame, () => BudgetMs);
                while (steps.MoveNext()) yield return steps.Current;
                if (frame.Elapsed.TotalMilliseconds < BudgetMs) continue;
                yield return null;
                frame.Restart();
            }
            var branch = GameObject.Find(YggdrasilName);
            if (branch != null) Try(YggdrasilName, () => s_replaceTextures.Invoke(replacer, new object[] { branch }));
            int missing = MissingCount(replacer);
            FiresLogger.LogInfo($"{Tag}Processing textures resulted in {missing} missing textures.");
            // The list is shared: the menu steps and HD's own spawn-time hooks add to it too, so this pass's part is split out.
            FiresLogger.LogInfo($"{Tag}world pass: {missing - missingAtStart} of those {missing} added by the prefab pass itself "
                                + $"({missingAtStart} were already listed when it began).");
        }

        internal static int MissingCount(object replacer) => s_missing?.GetValue(replacer) is IList list ? list.Count : -1;

        // While the override prefabs are done, ZNetScene creates (and removes) nothing; capped, so a stuck pass can't hold
        // the world back for good.
        internal static bool CreateDestroyObjects_Prefix()
        {
            if (!s_holding) return true;
            if (s_hold.Elapsed.TotalSeconds < OverrideHoldCapSeconds) return false;
            s_holding = false;
            FiresLogger.LogWarning($"{Tag}world pass: stopped holding object creation after {OverrideHoldCapSeconds:0} s; "
                                   + $"{s_first} override prefab(s) done so far.");
            return true;
        }

        // Prefabs whose enabled overrides make new material instances or swap meshes: objects spawned before them would
        // keep the old ones, so they are done before anything else.
        private static HashSet<string> FirstNames(object replacer)
        {
            var names = new HashSet<string>();
            if (!(s_overrides.GetValue(replacer) is IEnumerable overrides)) return names;
            foreach (object o in overrides)
            {
                if (o == null || !(bool)s_ovEnabled.GetValue(o)) continue;
                bool instanced = (bool)s_ovMakeNew.GetValue(o);
                bool mesh = !string.IsNullOrEmpty((string)s_ovReplaceMesh.GetValue(o));
                if (instanced || mesh) names.Add((string)s_ovGameObject.GetValue(o));
            }
            return names;
        }

        // [perf] R39: one prefab took 1,024 ms in a single step. Its child renderers now split across frames when the budget
        // runs out, and the prefab whose work took longest (frames between its slices not counted) is named at the finish.
        private static IEnumerator DoPrefab(object replacer, GameObject prefab, Stopwatch frame, Func<double> budgetMs)
        {
            s_prefabs++;
            string name = prefab.name;
            if (name.StartsWith(ExcludedPrefix, StringComparison.Ordinal) || name.EndsWith(ExcludedSuffix, StringComparison.Ordinal)) yield break;
            var work = Stopwatch.StartNew();
            if (name.ToLowerInvariant().Contains(ArmorWord))
            {
                var material = prefab.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_armorMaterial;
                if (material != null) Try(name + " armor", () => s_replaceMaterialTextures.Invoke(replacer, new object[] { material, false }));
            }
            Try(name, () => s_replaceTextures.Invoke(replacer, new object[] { prefab }));
            foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>())
            {
                if (frame.Elapsed.TotalMilliseconds >= budgetMs())
                {
                    work.Stop();
                    yield return null;
                    frame.Restart();
                    work.Start();
                }
                Try(name, () => s_replaceTextures.Invoke(replacer, new object[] { renderer.gameObject }));
            }
            if (work.Elapsed.TotalMilliseconds <= s_slowestPrefabMs) yield break;
            s_slowestPrefabMs = work.Elapsed.TotalMilliseconds;
            s_slowestPrefab = name;
        }

        private static void Try(string what, Action action)
        {
            try { action(); }
            catch (Exception ex) { FiresLogger.LogWarning($"{Tag}world pass: {what} threw: {ex.GetBaseException().Message}"); }
        }

        internal static IEnumerable<string> Describe()
        {
            yield return $"{Tag}world pass: {(s_work != null ? "running" : s_started ? "done" : "not started")}; "
                         + $"{s_prefabs}/{s_total} prefabs, {s_frames} frame(s), longest {s_longestMs:0} ms.";
            var scene = ZNetScene.instance;
            if (scene == null) yield break;
            foreach (var prefab in scene.m_prefabs.Where(p => p != null && p.GetComponentInChildren<Renderer>() != null).Take(SampleCount))
            {
                var material = prefab.GetComponentInChildren<Renderer>().sharedMaterial;
                var texture = material != null && material.HasProperty(MainTexture) ? material.GetTexture(MainTexture) : null;
                yield return texture != null
                    ? $"{Tag}{prefab.name}: {texture.name} {texture.width}x{texture.height}"
                    : $"{Tag}{prefab.name}: no main texture";
            }
        }
    }
}
