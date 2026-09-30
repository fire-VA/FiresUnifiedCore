using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using FiresCore.Lifecycle;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Compat
{
    // HDValheimTextures 1.26.0 (Badgers.HDValheimTextures) loads its ~3,000 replacement textures (Textures.dat 11.4 GB +
    // OverrideTextures.dat 7.3 GB) inside ONE coroutine step at the main menu: 52.9 s with the game "Not Responding"
    // (LoginDiag, 2026-09-28 M1; Tools\HDVALHEIMTEXTURES_LOAD.md). Fire (15:47): spread it, for every player.
    //
    // Their ObjectDB.CopyOtherDB postfix starts the load only while their Mod.TexturesLoaded is false, so a prefix on the
    // same vanilla method sets that flag first and runs their steps in their order from here: HUD on, GetCustomTextures,
    // their own per-texture LoadBundleTexture / LoadCustomTexture a few per frame (FrameBudgetMs of work, then the next
    // frame), then their armor and menu-scene replacement, HUD off. None of their methods is patched. If a world starts
    // before the load is done, a ZNetScene.Awake prefix finishes the rest at once, ahead of their ZNetScene pass, which is
    // what they do today. Arms only for exactly 1.26.0 with every member below found; anything else leaves the mod alone.
    // Nothing on a dedicated server, and one log line and nothing else when HDValheimTextures is not loaded (Fire, 16:02).
    // HdTexturesLoadScreen covers the menu while it runs (Fire, 16:00). HdTexturesWorldSpread does the same for HD's
    // one-time ZNetScene pass at the first world load. 'fires_hdtex' reports the state and samples materials, to prove
    // the textures landed.
    internal static class HdTexturesLoadSpread
    {
        private const string Tag = "[HD textures] ";
        private const string HdGuid = "Badgers.HDValheimTextures";
        private const string HdVersion = "1.26.0";
        private const string OffArgument = "-fireshdspread-off";
        private const string Ns = "NS_HDValheimTextures.";
        // R35 (25 ms): one HD texture often takes longer than the budget, so each paid a whole menu frame on top and the menu ran
        // at ~11 fps. Behind the loading screen a few frames a second are plenty; a larger slice cuts that per-frame cost.
        private const double FrameBudgetMs = 200.0;
        private const string MainBundle = "main";
        private const string OverrideBundle = "material override";
        private const string DdsType = "DDS";
        private const string CommandName = "fires_hdtex";
        private const string ArmorWord = "armor";
        private const int SampleCount = 8;
        private const string MainTexture = "_MainTex";
        private const double BaselineWaitCapSeconds = 300.0;

        private static Harmony s_harmony;
        private static bool s_checked;
        private static bool s_armed;
        private static bool s_started;
        private static IEnumerator s_work;
        private static int s_loaded;
        private static int s_frames;
        private static double s_longestMs;
        private static double s_longestTextureMs;
        private static string s_slowestTexture;
        private static int s_processed;
        private static int s_resized;
        private static bool s_resizeFailureLogged;
        private static readonly Stopwatch s_clock = new Stopwatch();
        private static readonly Stopwatch s_baselineClock = new Stopwatch();
        private static bool s_baselineCapped;

        private static FieldInfo s_modField, s_texturesLoaded, s_replacerField, s_hudField, s_gotBodyTexture;
        private static FieldInfo s_mainPath, s_overridePath, s_customTextures, s_textures;
        private static MethodInfo s_getCustomTextures, s_loadBundleTexture, s_loadCustomTexture, s_enableHud, s_disableHud;
        private static MethodInfo s_replaceArmor, s_replaceMenu, s_unload, s_readDirectory, s_hdLoadTextures;
        private static PropertyInfo s_isValid;
        private static Type s_bundleStreamType;
        private static object s_dds;

        // True while the spread load, or with '-fireshdspread-off' HD's own load, is still running (FDT's AutoJoin waits for
        // it, by name).
        public static bool Loading => s_work != null || BaselineLoading;

        private static bool BaselineLoading
        {
            get
            {
                if (!s_baselineClock.IsRunning || s_baselineCapped) return false;
                if (s_baselineClock.Elapsed.TotalSeconds < BaselineWaitCapSeconds) return true;
                s_baselineCapped = true;
                FiresLogger.LogWarning($"{Tag}baseline: HD's own load has not finished after {BaselineWaitCapSeconds:0} s; "
                                       + "no longer holding the join for it.");
                return false;
            }
        }

        // Progress for the loading screen: entries done so far, of how many, and for how long. Done counts every entry HD was
        // handed, loaded or not: HD skips textures whose sides are not powers of two (R35: 3,086 entries, 3,051 loaded).
        internal static int Done => s_processed;
        internal static int Total { get; private set; }
        internal static double ElapsedSeconds => s_clock.Elapsed.TotalSeconds;

        internal static void Register(Harmony harmony, ConfigFile config)
        {
            if (FiresMod.IsDedicatedServer) return;
            HdTexturesSize.Bind(config);
            s_harmony = harmony;
            try
            {
                harmony.Patch(AccessTools.Method(typeof(FejdStartup), "Awake"),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(HdTexturesLoadSpread), nameof(FejdStartup_Awake_Prefix))));
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{Tag}not attached: {ex.Message}");
            }
        }

        // The first menu comes after the chainloader has finished, so HD's types are all there; checked once per process.
        private static void FejdStartup_Awake_Prefix()
        {
            if (s_checked) return;
            s_checked = true;
            if (!Chainloader.PluginInfos.TryGetValue(HdGuid, out var info))
            {
                FiresLogger.LogInfo($"{Tag}HDValheimTextures not loaded; the spread-load stays off.");
                return;
            }
            RegisterCommand();
            string version = info.Metadata.Version.ToString();
            if (version != HdVersion)
            {
                FiresLogger.LogInfo($"{Tag}HDValheimTextures is {version}; the spread load is pinned to {HdVersion}, so the mod loads as it always has.");
                return;
            }
            string missing = Resolve(info.Instance.GetType().Assembly);
            if (missing != null)
            {
                FiresLogger.LogWarning($"{Tag}{missing} not found in HDValheimTextures {version}; the mod loads as it always has.");
                return;
            }
            if (Environment.GetCommandLineArgs().Any(a => string.Equals(a, OffArgument, StringComparison.OrdinalIgnoreCase)))
            {
                ArmBaseline(version);
                return;
            }
            try
            {
                s_harmony.Patch(AccessTools.Method(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB)),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(HdTexturesLoadSpread), nameof(ObjectDB_CopyOtherDB_Prefix))));
                s_harmony.Patch(AccessTools.Method(typeof(ZNetScene), "Awake"),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(HdTexturesLoadSpread), nameof(ZNetScene_Awake_Prefix))));
                s_harmony.Patch(AccessTools.Method(typeof(Player), "Awake"),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(HdTexturesLoadSpread), nameof(Player_Awake_Postfix))));
                s_harmony.Patch(AccessTools.Method(typeof(ZNetScene), "CreateDestroyObjects"),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(HdTexturesWorldSpread), nameof(HdTexturesWorldSpread.CreateDestroyObjects_Prefix))));
                s_armed = true;
                FiresLogger.LogInfo($"{Tag}armed for HDValheimTextures {version}: its textures load over several frames at the menu "
                                    + $"({FrameBudgetMs:0} ms of work per frame) instead of in one, at size {HdTexturesSize.Size?.Value}.");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{Tag}not armed: {ex.Message}; the mod loads as it always has.");
            }
        }

        // Every member used, by name; the first one missing is returned.
        private static string Resolve(Assembly hd)
        {
            Type patches = hd.GetType(Ns + "HarmonyPatches");
            Type mod = hd.GetType(Ns + "Mod");
            Type replacer = hd.GetType(Ns + "TextureReplacer");
            Type hud = hd.GetType(Ns + "HUD");
            s_bundleStreamType = hd.GetType(Ns + "BundleTools.BundleStream");
            Type copyOtherDb = patches != null ? AccessTools.Inner(patches, "ObjectDB_CopyOtherDB") : null;
            Type textureType = replacer != null ? AccessTools.Inner(replacer, "TextureType") : null;
            var need = new List<(string name, object found)>
            {
                ("HarmonyPatches", patches), ("Mod", mod), ("TextureReplacer", replacer), ("HUD", hud),
                ("BundleStream", s_bundleStreamType), ("ObjectDB_CopyOtherDB", copyOtherDb), ("TextureType", textureType),
            };
            string type = need.FirstOrDefault(n => n.found == null).name;
            if (type != null) return type;

            s_modField = AccessTools.Field(patches, "Mod");
            s_gotBodyTexture = AccessTools.Field(patches, "GotBodyTexture");
            s_unload = AccessTools.Method(patches, "Unload");
            s_texturesLoaded = AccessTools.Field(mod, "TexturesLoaded");
            s_replacerField = AccessTools.Field(mod, "TextureReplacer");
            s_hudField = AccessTools.Field(mod, "Hud");
            s_mainPath = AccessTools.Field(replacer, "m_MainBundlePath");
            s_overridePath = AccessTools.Field(replacer, "m_OverrideBundlePath");
            s_customTextures = AccessTools.Field(replacer, "m_CustomTextures");
            s_textures = AccessTools.Field(replacer, "m_Textures");
            s_getCustomTextures = AccessTools.Method(replacer, "GetCustomTextures");
            s_loadBundleTexture = AccessTools.Method(replacer, "LoadBundleTexture");
            s_loadCustomTexture = AccessTools.Method(replacer, "LoadCustomTexture");
            s_enableHud = AccessTools.Method(hud, "EnableHud");
            s_disableHud = AccessTools.Method(hud, "DisableHud");
            s_replaceArmor = AccessTools.Method(copyOtherDb, "ReplaceObjectDBArmorTextures");
            s_replaceMenu = AccessTools.Method(copyOtherDb, "ReplaceMenuSceneTextures");
            s_hdLoadTextures = AccessTools.Method(replacer, "LoadTextures");
            s_readDirectory = AccessTools.Method(s_bundleStreamType, "ReadDirectory");
            s_isValid = AccessTools.Property(s_bundleStreamType, "IsValid");
            s_dds = Enum.IsDefined(textureType, DdsType) ? Enum.Parse(textureType, DdsType) : null;
            var members = new List<(string name, object found)>
            {
                ("HarmonyPatches.Mod", s_modField), ("HarmonyPatches.GotBodyTexture", s_gotBodyTexture),
                ("HarmonyPatches.Unload", s_unload), ("Mod.TexturesLoaded", s_texturesLoaded),
                ("Mod.TextureReplacer", s_replacerField), ("Mod.Hud", s_hudField), ("m_MainBundlePath", s_mainPath),
                ("m_OverrideBundlePath", s_overridePath), ("m_CustomTextures", s_customTextures), ("m_Textures", s_textures),
                ("GetCustomTextures", s_getCustomTextures), ("LoadBundleTexture", s_loadBundleTexture),
                ("LoadCustomTexture", s_loadCustomTexture), ("EnableHud", s_enableHud), ("DisableHud", s_disableHud),
                ("ReplaceObjectDBArmorTextures", s_replaceArmor), ("ReplaceMenuSceneTextures", s_replaceMenu),
                ("TextureReplacer.LoadTextures", s_hdLoadTextures),
                ("BundleStream.ReadDirectory", s_readDirectory), ("BundleStream.IsValid", s_isValid), ("TextureType.DDS", s_dds),
            };
            return members.FirstOrDefault(m => m.found == null).name
                   ?? HdTexturesWorldSpread.Resolve(mod, replacer, hd.GetType(Ns + "MaterialOverride"))
                   ?? HdTexturesSize.Resolve(hd.GetType(Ns + "BundleTools.CBDirectoryItem"), s_bundleStreamType);
        }

        // '-fireshdspread-off' ([generator], R36: no baseline for the 2,560 missing): HD loads and replaces exactly as it
        // always has, and Core only logs HD's own missing count once its ZNetScene pass is over (HD's own line is below the
        // rig's log level). R37: AutoJoin joined before HD's own load had run, so the world had a part of its textures and the
        // count meant nothing ([generator]); Loading now also covers HD's own load, from CopyOtherDB to its LoadTextures' end.
        private static void ArmBaseline(string version)
        {
            try
            {
                var postfix = new HarmonyMethod(AccessTools.Method(typeof(HdTexturesLoadSpread), nameof(ZNetScene_Awake_Baseline))) { priority = Priority.Last };
                s_harmony.Patch(AccessTools.Method(typeof(ZNetScene), "Awake"), postfix: postfix);
                s_harmony.Patch(AccessTools.Method(typeof(ObjectDB), nameof(ObjectDB.CopyOtherDB)),
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(HdTexturesLoadSpread), nameof(ObjectDB_CopyOtherDB_Baseline))));
                s_harmony.Patch(s_hdLoadTextures,
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(HdTexturesLoadSpread), nameof(HdLoadTextures_Baseline_Finalizer))));
                FiresLogger.LogInfo($"{Tag}spread load OFF ({OffArgument}): HDValheimTextures {version} loads as it always has; its own counts follow.");
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{Tag}baseline logging not attached: {ex.Message}");
            }
        }

        private static void ObjectDB_CopyOtherDB_Baseline()
        {
            if (s_started) return;
            object mod = HdMod;
            if (mod == null || (bool)s_texturesLoaded.GetValue(mod)) return;
            s_started = true;
            s_baselineClock.Restart();
        }

        private static void HdLoadTextures_Baseline_Finalizer()
        {
            if (!s_baselineClock.IsRunning) return;
            s_baselineClock.Stop();
            object mod = HdMod;
            int count = mod != null && s_textures.GetValue(Replacer(mod)) is ICollection textures ? textures.Count : 0;
            FiresLogger.LogInfo($"{Tag}baseline (HD's own load): {count} replacement textures in "
                                + $"{s_baselineClock.Elapsed.TotalSeconds:0.0} s, from CopyOtherDB to the end of its LoadTextures"
                                + (s_baselineCapped ? " (after the join hold was released)." : "."));
        }

        private static void ZNetScene_Awake_Baseline()
        {
            object mod = HdMod;
            if (mod == null) return;
            FiresLogger.LogInfo($"{Tag}baseline (HD's own pass): Processing textures resulted in "
                                + $"{HdTexturesWorldSpread.MissingCount(Replacer(mod))} missing textures.");
        }

        // HD's Mod instance, and its TextureReplacer, for the world pass.
        internal static object HdMod => s_modField?.GetValue(null);

        internal static object Replacer(object mod) => s_replacerField.GetValue(mod);

        private static void ObjectDB_CopyOtherDB_Prefix()
        {
            if (s_started) return;
            object mod = s_modField.GetValue(null);
            if (mod == null || (bool)s_texturesLoaded.GetValue(mod)) return;
            s_texturesLoaded.SetValue(mod, true);
            s_started = true;
            s_loaded = 0;
            s_frames = 0;
            s_longestMs = 0.0;
            s_longestTextureMs = 0.0;
            s_slowestTexture = null;
            s_processed = 0;
            s_resized = 0;
            s_clock.Restart();
            s_work = Load(mod);
            Total = 0;
            var runner = new GameObject(nameof(HdTexturesLoadSpread));
            UnityEngine.Object.DontDestroyOnLoad(runner);
            runner.AddComponent<Runner>();
            runner.AddComponent<HdTexturesLoadScreen>();
        }

        // Their Player.Awake prefix clears GotBodyTexture so their VisEquipment.UpdateBaseModel postfix re-textures the new
        // player's body. For the menu's preview character built while the load runs, that would log textures that simply are
        // not loaded yet as missing ([generator]'s review), so it waits: their final ReplaceMenuSceneTextures does the preview
        // with the full set and sets the flag, exactly as when the preview came after their one-frame load.
        private static void Player_Awake_Postfix()
        {
            if (Loading) s_gotBodyTexture.SetValue(null, true);
        }

        // A world is starting. If the menu load isn't done, the rest now, ahead of their ZNetScene pass (their behaviour
        // today); then their one-time ZNetScene pass is taken off this frame and spread (HdTexturesWorldSpread).
        private static void ZNetScene_Awake_Prefix()
        {
            if (s_work != null)
            {
                var drain = Stopwatch.StartNew();
                int before = s_loaded;
                try { while (s_work.MoveNext()) { } }
                catch (Exception ex) { FiresLogger.LogError($"{Tag}load failed while finishing before the world: {ex.GetBaseException().Message}"); }
                FiresLogger.LogInfo($"{Tag}a world started before the load was done: finished the last {s_loaded - before} texture(s) "
                                    + $"first, in {drain.ElapsedMilliseconds} ms.");
                Finish();
            }
            HdTexturesWorldSpread.Schedule(HdMod);
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
                var step = Stopwatch.StartNew();
                bool more;
                try { more = s_work.MoveNext(); }
                catch (Exception ex)
                {
                    FiresLogger.LogError($"{Tag}load failed: {ex.GetBaseException().Message}");
                    more = false;
                }
                s_frames++;
                s_longestMs = Math.Max(s_longestMs, step.Elapsed.TotalMilliseconds);
                if (!more) Finish();
            }
        }

        private static void Finish()
        {
            s_work = null;
            s_clock.Stop();
            FiresLogger.LogInfo($"{Tag}loaded {s_loaded} replacement textures of {s_processed} entries ({s_processed - s_loaded} skipped by "
                                + $"HD itself; {s_resized} at size {HdTexturesSize.Size?.Value}) in {s_clock.Elapsed.TotalSeconds:0.0} s over {s_frames} "
                                + $"frame(s), longest frame {s_longestMs:0} ms, longest single texture {s_longestTextureMs:0} ms ({s_slowestTexture ?? "none"}).");
        }

        // Their ObjectDB_CopyOtherDB.LoadTextures coroutine and TextureReplacer.LoadTextures, step for step, yielding between
        // textures once a frame's budget is spent. One texture that throws is logged and skipped rather than ending the load.
        private static IEnumerator Load(object mod)
        {
            object replacer = s_replacerField.GetValue(mod);
            object hud = s_hudField.GetValue(mod);
            Try("EnableHud", () => s_enableHud.Invoke(hud, null));
            yield return null;
            s_getCustomTextures.Invoke(replacer, null);
            var custom = ((IDictionary)s_customTextures.GetValue(replacer)).Values.Cast<string>().ToList();
            Total = CountBundle((string)s_mainPath.GetValue(replacer)) + CountBundle((string)s_overridePath.GetValue(replacer)) + custom.Count;
            yield return null;

            var textures = (IDictionary)s_textures.GetValue(replacer);
            var frame = Stopwatch.StartNew();
            foreach (var (pathField, bundle) in new[] { (s_mainPath, MainBundle), (s_overridePath, OverrideBundle) })
            {
                string path = (string)pathField.GetValue(replacer);
                if (bundle == OverrideBundle && !File.Exists(path))
                {
                    FiresLogger.LogInfo($"{Tag}no override bundle; HD uses its loose override textures.");
                    continue;
                }
                var stream = (IDisposable)Activator.CreateInstance(s_bundleStreamType, path, FileAccess.Read);
                try
                {
                    if (!(bool)s_isValid.GetValue(stream))
                    {
                        FiresLogger.LogError($"{Tag}the {bundle} bundle has a bad header; HD unloads itself, as it does.");
                        s_unload.Invoke(null, null);
                        yield break;
                    }
                    foreach (object item in (IList)s_readDirectory.Invoke(stream, null))
                    {
                        var args = new[] { item, stream, s_loaded, s_dds, bundle };
                        bool ours = false;
                        bool done = LoadOne($"{bundle} bundle texture {HdTexturesSize.NameOf(item)}", () =>
                        {
                            // A resize that fails costs only the resize: HD's loader still gets the entry ([generator]'s review).
                            try { ours = HdTexturesSize.Drop > 0 && HdTexturesSize.TryLoad(item, stream, textures); }
                            catch (Exception ex)
                            {
                                ours = false;
                                if (!s_resizeFailureLogged)
                                    FiresLogger.LogWarning($"{Tag}resizing a {bundle} bundle texture failed ({ex.GetBaseException().Message}); "
                                                           + "HD loads it at full size, and any other failures go unlogged.");
                                s_resizeFailureLogged = true;
                            }
                            if (!ours) s_loadBundleTexture.Invoke(replacer, args);
                        });
                        if (done) s_loaded = ours ? s_loaded + 1 : (int)args[2];
                        if (ours) s_resized++;
                        if (frame.Elapsed.TotalMilliseconds < FrameBudgetMs) continue;
                        yield return null;
                        frame.Restart();
                    }
                }
                finally { stream?.Dispose(); }
            }

            foreach (string path in custom)
            {
                var args = new object[] { path, s_loaded };
                if (LoadOne("custom texture " + Path.GetFileName(path), () => s_loadCustomTexture.Invoke(replacer, args))) s_loaded = (int)args[1];
                if (frame.Elapsed.TotalMilliseconds < FrameBudgetMs) continue;
                yield return null;
                frame.Restart();
            }

            Try("ReplaceObjectDBArmorTextures", () => s_replaceArmor.Invoke(null, null));
            Try("ReplaceMenuSceneTextures", () => s_replaceMenu.Invoke(null, null));
            Try("DisableHud", () => s_disableHud.Invoke(hud, null));
        }

        // How many entries a bundle's directory lists (0 when it is missing or its header is bad); only for the progress bar.
        private static int CountBundle(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;
            try
            {
                using (var stream = (IDisposable)Activator.CreateInstance(s_bundleStreamType, path, FileAccess.Read))
                    return (bool)s_isValid.GetValue(stream) ? ((IList)s_readDirectory.Invoke(stream, null)).Count : 0;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{Tag}could not count {Path.GetFileName(path)}: {ex.GetBaseException().Message}");
                return 0;
            }
        }

        // One texture through HD's own loader, timed, so the finish line can say what one texture costs at most, and which.
        private static bool LoadOne(string what, Action action)
        {
            var one = Stopwatch.StartNew();
            bool loaded = Try(what, action);
            if (one.Elapsed.TotalMilliseconds > s_longestTextureMs)
            {
                s_longestTextureMs = one.Elapsed.TotalMilliseconds;
                s_slowestTexture = what;
            }
            s_processed++;
            return loaded;
        }

        private static bool Try(string what, Action action)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{Tag}{what} threw: {ex.GetBaseException().Message}");
                return false;
            }
        }

        // ── fires_hdtex: proof the textures landed ──────────────────────────────────────────────────────────────────────
        private static void RegisterCommand()
        {
            try
            {
                new Terminal.ConsoleCommand(CommandName,
                    "[FiresUnifiedCore] HDValheimTextures: the spread load's state, HD's loaded-texture count, and the main texture of a "
                    + "sample of armor items (name and size; HD's are larger than vanilla's).",
                    args => { foreach (string line in Describe()) args.Context?.AddString(line); });
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{Tag}{CommandName} not registered: {ex.Message}");
            }
        }

        private static IEnumerable<string> Describe()
        {
            yield return $"{Tag}spread load: {(!s_armed ? "not armed (HD loads as it always has)" : Loading ? "running" : s_started ? "done" : "armed, not started")}; "
                         + $"{s_loaded} loaded so far, {s_frames} frame(s), longest {s_longestMs:0} ms.";
            object mod = s_modField?.GetValue(null);
            object replacer = mod != null ? s_replacerField?.GetValue(mod) : null;
            if (replacer != null && s_textures?.GetValue(replacer) is IDictionary textures)
                yield return $"{Tag}HD holds {textures.Count} replacement texture(s) and lists {HdTexturesWorldSpread.MissingCount(replacer)} missing.";
            if (ObjectDB.instance != null)
                foreach (var item in ObjectDB.instance.m_items.Where(i => i != null && i.name.ToLowerInvariant().Contains(ArmorWord)).Take(SampleCount))
                {
                    var material = item.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_armorMaterial;
                    var texture = material != null && material.HasProperty(MainTexture) ? material.GetTexture(MainTexture) : null;
                    yield return texture != null
                        ? $"{Tag}{item.name}: {texture.name} {texture.width}x{texture.height}"
                        : $"{Tag}{item.name}: no armor texture";
                }
            if (!s_armed) yield break;
            foreach (string line in HdTexturesWorldSpread.Describe()) yield return line;
        }
    }
}
