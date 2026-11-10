using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Compat
{
    /// <summary>
    /// 0.2.1217 ([generator], Fire 11-08: EBM relies on no other mod; Tools\PLAN_CoreToEbmApi_20261108.md section A). EBM 1.2.208 dropped
    /// its CoreLink lookups of Core, so Core pushes what EBM used to fetch INTO FiresEasyBakeMeshes.EbmApi, and pulls what EBM used to
    /// register. Found by type name only (no reference either way). Run once from CoreLateOwners.RunOnce: after every plugin's Awake and
    /// before any world bakes (first FejdStartup.Awake, or ZNet.Awake on a dedicated server). Each member binds on its own; a missing one
    /// (an older EBM) is skipped quietly, with one Debug line listing what was skipped.
    /// Pushes: RegisterBakeExclusion (+ RaiseBakeExclusionsChanged on MeshBakeBridge.ExclusionsChanged), RegisterFrameTimer, RegisterLineFit,
    /// RegisterDebugSwitch, RegisterGravityUp, RegisterSnowBase.
    /// Pulls: StatusLine() into StatusBanner, Config into ConfigHotReload.Watch + FiresConfigVisibility.MarkAdvanced(IsAdvancedKey), and
    /// HelpSections() into HelpRegistry.
    /// </summary>
    internal static class EbmHooksBridge
    {
        private const string EbmApiType = "FiresEasyBakeMeshes.EbmApi";
        private const string EbmModId = "FiresEasyBakeMeshes";
        private static bool s_ran;

        internal static void RunOnce()
        {
            if (s_ran) return;
            s_ran = true;
            Type api = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { api = asm.GetType(EbmApiType, false); } catch { continue; }
                if (api != null) break;
            }
            if (api == null) { FiresLogger.LogDebug("[EbmHooks] EBM not installed: nothing to push"); return; }

            var wired = new List<string>(); var skipped = new List<string>();
            void Do(string name, Func<bool> act)
            {
                bool ok;
                try { ok = act(); } catch (Exception e) { ok = false; FiresLogger.LogWarning($"[EbmHooks] {name} threw: {(e.InnerException ?? e).Message}"); }
                (ok ? wired : skipped).Add(name);
            }

            // ── pushes ──
            Do("bake exclusions", () =>
            {
                if (!Invoke(api, "RegisterBakeExclusion", new[] { typeof(Func<GameObject, bool>) }, (Func<GameObject, bool>)FiresCore.Bridge.MeshBakeBridge.IsExcludedFromBaking)) return false;
                var raise = Method(api, "RaiseBakeExclusionsChanged", Type.EmptyTypes);
                if (raise != null) FiresCore.Bridge.MeshBakeBridge.ExclusionsChanged += () => { try { raise.Invoke(null, null); } catch (Exception e) { FiresLogger.LogDebug("[EbmHooks] RaiseBakeExclusionsChanged threw: " + (e.InnerException ?? e).Message); } };
                return true;
            });
            Do("frame timer", () => Invoke(api, "RegisterFrameTimer", new[] { typeof(Action<string, long>), typeof(Func<bool>) },
                (Action<string, long>)FiresCore.Diagnostics.FrameTimer.Record, (Func<bool>)(() => FiresCore.Diagnostics.FrameTimer.Enabled)));
            Do("line fit", () => Invoke(api, "RegisterLineFit", new[] { typeof(Func<string, int, string>), typeof(Func<int>), typeof(Func<string, int>) },
                (Func<string, int, string>)ConsoleWrap.Fit, (Func<int>)(() => ConsoleWrap.ConsoleColumns), (Func<string, int>)ConsoleWrap.PrefixColumnsFor));
            Do("debug switch", () => Invoke(api, "RegisterDebugSwitch", new[] { typeof(Func<bool>) }, (Func<bool>)(() => FiresLogger.DebugEnabled)));
            Do("gravity up", () => Invoke(api, "RegisterGravityUp", new[] { typeof(Func<Vector3, Vector3>) }, (Func<Vector3, Vector3>)FiresCore.World.GravityUp.At));
            Do("snow base", () => Invoke(api, "RegisterSnowBase", new[] { typeof(Func<Vector3, float>) }, (Func<Vector3, float>)FiresCore.Snow.SnowField.BaseAt));
            // 0.2.1219 ([ghost], EBM 1.2.210: EBM no longer reads SnowField by name): Core is a piece-snow source for EBM's baked pieces,
            // priority 10, plus SnowField.ZoneAreaChanged forwarded so only the touched groups re-split.
            Do("piece snow source", () =>
            {
                var reg = Method(api, "RegisterPieceSnowSource", new[] { typeof(string), typeof(int), typeof(Func<bool>), typeof(Func<Vector3, float>), typeof(Func<Vector3, float>), typeof(Func<bool>), typeof(Func<GameObject, string, int>) });
                if (reg == null) return false;
                reg.Invoke(null, new object[] { "Core", 10, (Func<bool>)(() => FiresCore.Snow.SnowField.Enabled), (Func<Vector3, float>)FiresCore.Snow.SnowField.CoverageAt, null, null, null });
                var area = Method(api, "RaisePieceSnowAreaChanged", new[] { typeof(Vector2s), typeof(Rect), typeof(bool) });
                if (area != null)
                {
                    var raise = (Action<Vector2s, Rect, bool>)Delegate.CreateDelegate(typeof(Action<Vector2s, Rect, bool>), area);
                    FiresCore.Snow.SnowField.ZoneAreaChanged += (zone, rect, whole) => { try { raise(zone, rect, whole); } catch (Exception e) { FiresLogger.LogDebug("[EbmHooks] RaisePieceSnowAreaChanged threw: " + e.Message); } };
                }
                return true;
            });

            // ── pulls ──
            Do("status line", () =>
            {
                var m = Method(api, "StatusLine", Type.EmptyTypes);
                if (m == null || m.ReturnType != typeof(string)) return false;
                var line = (Func<string>)Delegate.CreateDelegate(typeof(Func<string>), m);
                StatusBanner.Register(EbmModId, line);
                return true;
            });
            Do("config hot reload + visibility", () =>
            {
                var p = api.GetProperty("Config", BindingFlags.Public | BindingFlags.Static);
                if (!(p?.GetValue(null) is ConfigFile cfg)) return false;
                FiresCore.Config.ConfigHotReload.Watch(cfg);
                var adv = Method(api, "IsAdvancedKey", new[] { typeof(ConfigDefinition) });
                if (adv != null && adv.ReturnType == typeof(bool))
                    FiresCore.Config.FiresConfigVisibility.MarkAdvanced(cfg, (Func<ConfigDefinition, bool>)Delegate.CreateDelegate(typeof(Func<ConfigDefinition, bool>), adv));
                return true;
            });
            Do("help sections", () =>
            {
                var m = Method(api, "HelpSections", Type.EmptyTypes);
                if (m == null || !(m.Invoke(null, null) is List<(string group, string title, int order, bool adminOnly, string text)> sections)) return false;
                foreach (var s in sections)
                {
                    var sec = s;
                    FiresCore.Help.HelpRegistry.RegisterSection(EbmModId, sec.group, sec.title, sec.order, w =>
                    {
                        foreach (var para in (sec.text ?? "").Split(new[] { "\n" }, StringSplitOptions.None))
                            if (!string.IsNullOrWhiteSpace(para)) w.Paragraph(para.TrimEnd('\r'));
                    }, sec.adminOnly);
                }
                return true;
            });

            FiresLogger.LogInfo($"[EbmHooks] wired into EbmApi: {string.Join(", ", wired.ToArray())}");
            if (skipped.Count > 0) FiresLogger.LogDebug($"[EbmHooks] not in this EBM (older build): {string.Join(", ", skipped.ToArray())}");
        }

        private static MethodInfo Method(Type t, string name, Type[] args) => t.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, args, null);

        private static bool Invoke(Type t, string name, Type[] args, params object[] values)
        {
            var m = Method(t, name, args);
            if (m == null) return false;
            m.Invoke(null, values);
            return true;
        }
    }
}
