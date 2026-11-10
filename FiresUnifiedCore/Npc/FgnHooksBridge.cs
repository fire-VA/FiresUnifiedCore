using System;
using System.Collections.Generic;
using System.Reflection;
using FiresCore.Logging;
using FiresCore.UI.Shell;

namespace FiresCore.Npc
{
    /// <summary>
    /// 0.2.1215 ([generator], Fire 11-08 "fgn better not rely on core for anything"; FGN 1.6.63, FgnApi v8): FGN dropped its last
    /// three lookups of Core, and Core plugs into FGN's own API instead. Found by type name only, so neither mod references the other.
    ///  1. Stats card: Core registers FGN's section (FgnApi.StatsSectionId / Title / Order / RefreshSeconds) and maps
    ///     FgnApi.GetStatsRows() (label, value, state, tooltip) to StatRow. FGN used to call FiresShell.RegisterStatsSection itself.
    ///  2. Far-idle skip: FgnApi.RegisterFarIdleSkip(owner, c => c has a CompanionController), so FGN never slows a Core companion.
    ///     FGN used to look up CompanionController by name.
    ///  3. Kinematic sweep: Core provides FGN's "[Performance] Kinematic sync sweep" (FgnApi.KinematicSweepEnabled) to its own
    ///     CoreSettings at FGN's old priority (10), just before KinematicSync decides. FGN used to push it through CoreSettings.Provide.
    /// Runs once from CoreLateOwners.RunOnce (first FejdStartup.Awake / ZNet.Awake, after every plugin's Awake). The removal keep
    /// (v7) stays in FgnRemovalKeepBridge. Without FGN, or with FGN below v8: one Debug line; FGN's own defaults hold.
    /// </summary>
    internal static class FgnHooksBridge
    {
        private const string FgnApiType = "FiresGhettoNetworkMod.Api.FgnApi";
        private const int NeededVersion = 8;
        private const string Owner = "com.Fire.FiresUnifiedCore";

        private static Func<List<string[]>> s_getRows;
        private static readonly List<StatRow> s_rows = new List<StatRow>(16);
        private static bool s_ran;

        internal static void RunOnce()
        {
            if (s_ran) return;
            s_ran = true;
            Type api;
            try { api = FindType(FgnApiType); }
            catch (Exception e) { FiresLogger.LogDebug("[FgnHooks] FGN lookup failed: " + e.Message); return; }
            if (api == null) { FiresLogger.LogDebug("[FgnHooks] FGN not installed: no FGN stats card, far-idle skip or kinematic value to wire"); return; }
            int version = ConstInt(api, "Version");
            if (version < NeededVersion) { FiresLogger.LogDebug($"[FgnHooks] FGN API v{version} (needs v{NeededVersion}, FGN 1.6.63+): FGN wires these itself"); return; }

            bool stats = false, idle = false, kin = false;
            try { stats = RegisterStats(api); } catch (Exception e) { FiresLogger.LogWarning("[FgnHooks] FGN stats card: " + e.Message); }
            try { idle = RegisterFarIdle(api); } catch (Exception e) { FiresLogger.LogWarning("[FgnHooks] FGN far-idle skip: " + e.Message); }
            // 0.2.1217: FGN 1.6.64 runs the kinematic refresh from its own key; Core provides nothing (ProvideKinematic removed)
            FiresLogger.LogInfo($"[FgnHooks] wired into FGN API v{version}: stats card {(stats ? "on" : "off")}, companions never far-idled {(idle ? "on" : "off")}, kinematic sweep value {(kin ? "n/a" : "FGN's own (FGN runs it)")}");
        }

        // ── 1. stats card ──
        private static bool RegisterStats(Type api)
        {
            var m = api.GetMethod("GetStatsRows", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (m == null || m.ReturnType != typeof(List<string[]>)) return false;
            s_getRows = (Func<List<string[]>>)Delegate.CreateDelegate(typeof(Func<List<string[]>>), m);
            string id = ConstString(api, "StatsSectionId") ?? "fgn";
            string title = ConstString(api, "StatsSectionTitle") ?? "Networking (FGN)";
            int order = ConstInt(api, "StatsSectionOrder", 200);
            float refresh = ConstFloat(api, "StatsSectionRefreshSeconds", 1f);
            FiresShell.RegisterStatsSection(id, title, order, Rows, refresh);
            return true;
        }

        // Main thread, only while the Stats tab shows. One reused list; FGN's own list is read, never kept.
        private static IList<StatRow> Rows()
        {
            s_rows.Clear();
            List<string[]> rows = null;
            try { rows = s_getRows?.Invoke(); } catch { rows = null; }
            if (rows == null) return s_rows;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r == null || r.Length < 2) continue;
                s_rows.Add(new StatRow(r[0], r[1], r.Length > 2 ? StateOf(r[2]) : StatState.Normal, r.Length > 3 ? r[3] : null));
            }
            return s_rows;
        }

        private static StatState StateOf(string s)
        {
            switch (s)
            {
                case "good": return StatState.Good;
                case "warn": return StatState.Warn;
                case "bad": return StatState.Bad;
                case "muted": return StatState.Muted;
                default: return StatState.Normal;
            }
        }

        // ── 2. far-idle skip ──
        private static readonly Func<Character, bool> s_keepFullSpeed = c => c != null && c.GetComponent<CompanionController>() != null;

        private static bool RegisterFarIdle(Type api)
        {
            var m = api.GetMethod("RegisterFarIdleSkip", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(Func<Character, bool>) }, null);
            if (m == null) return false;
            return (bool)m.Invoke(null, new object[] { Owner, s_keepFullSpeed });
        }

        // ── helpers ──
        private static int ConstInt(Type t, string name, int fallback = 0)
        {
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Static);
            return f != null && f.FieldType == typeof(int) ? (int)f.GetValue(null) : fallback;
        }

        private static float ConstFloat(Type t, string name, float fallback)
        {
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Static);
            return f != null && f.FieldType == typeof(float) ? (float)f.GetValue(null) : fallback;
        }

        private static string ConstString(Type t, string name)
        {
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.Static);
            return f != null && f.FieldType == typeof(string) ? (string)f.GetValue(null) : null;
        }

        private static Type FindType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType(fullName, false); } catch { continue; }
                if (t != null) return t;
            }
            return null;
        }
    }
}
