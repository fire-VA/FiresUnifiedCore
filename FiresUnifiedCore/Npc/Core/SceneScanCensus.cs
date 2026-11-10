using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// 0.2.1221 ([ghost], [lead] after the public HomesteadNeeds hitch report: measure Core's other whole-scene scanners). Per call site:
    /// calls, total and worst ms, items walked; one Debug line every 60 s naming only the sites that ran. "Scene scans from registries"
    /// ([Performance], default on) picks the registry path (HomesteadStations / vanilla instance lists); off = the old FindObjectsByType
    /// path, for an A/B in one session.
    /// </summary>
    internal static class SceneScanCensus
    {
        private sealed class Site { public int Calls, Items; public double Ms, Worst; }
        private static readonly Dictionary<string, Site> s_sites = new Dictionary<string, Site>();
        private static float s_sayAt;

        private static bool KeyOn => FiresCore.Config.CoreSettings.Get("Performance", "Scene scans from registries", true);

        // 0.2.1221 ([generator], lead): each registry is used only while the patch that FILLS it is active, so a site never silently finds
        // nothing; otherwise it falls back to the scene search. One Info line per registry when its path changes.
        //  - UseRegistries: beds / doors / smelters / cooking / plants (HomesteadStationsPatches, behind Core's NpcBrain feature);
        //  - UseFireRegistry: fires (HomesteadFirePatch, ungated: snow melt reads it too);
        //  - UseVanillaLists: vanilla's own instance lists (WearNTear.GetAllInstances, CraftingStation.m_allStations) and
        //    CompanionController.AllCompanions, always kept by their owners, so only the key decides.
        internal static bool UseRegistries => KeyOn && s_brain.Live();
        internal static bool UseFireRegistry => KeyOn && s_fire.Live();
        internal static bool UseVanillaLists => KeyOn;

        private sealed class PatchProbe
        {
            private readonly System.Type m_target, m_owner; private readonly string m_name;
            private bool m_live; private float m_checkAt = -1f; private string m_said;
            internal PatchProbe(System.Type target, System.Type owner, string name) { m_target = target; m_owner = owner; m_name = name; }
            internal bool Live()
            {
                if (m_live) return true;
                float now = Time.unscaledTime;
                if (now < m_checkAt) return false;
                m_checkAt = now + 10f;   // not live yet: look again in 10 s (a feature can be patched in after load)
                bool live = false;
                try
                {
                    var info = HarmonyLib.Harmony.GetPatchInfo(HarmonyLib.AccessTools.Method(m_target, "Awake"));
                    if (info != null) foreach (var p in info.Postfixes)
                            if (p?.PatchMethod?.DeclaringType?.DeclaringType == m_owner) { live = true; break; }
                }
                catch (System.Exception e) { FiresCore.Logging.FiresLogger.LogDebug($"[ScanCensus] {m_name} registry check failed: {e.Message}"); }
                m_live = live;
                string path = live ? "the registry (filled at each Awake)" : "a scene search (the registry patch is not active)";
                if (path != m_said) { m_said = path; FiresCore.Logging.FiresLogger.LogInfo($"[ScanCensus] {m_name} lookups use {path}"); }
                return live;
            }
        }
        private static readonly PatchProbe s_brain = new PatchProbe(typeof(Bed), typeof(FiresCore.Npc.JobBoard.HomesteadStationsPatches), "companion station (beds / doors / smelters)");
        private static readonly PatchProbe s_fire = new PatchProbe(typeof(Fireplace), typeof(FiresCore.Npc.JobBoard.HomesteadFirePatch), "fire");

        /// <summary>0.2.1221 [ghost]: the live, active companions from CompanionController's own Awake/OnDestroy list (a copy: callers destroy duplicates while walking it), or the old scene search with the key off.</summary>
        internal static CompanionController[] Companions(string site)
        {
            var sw = Start();
            CompanionController[] r;
            if (UseVanillaLists)
            {
                var l = new List<CompanionController>();
                foreach (var c in CompanionController.AllCompanions) if (c != null && c.gameObject.activeInHierarchy) l.Add(c);
                r = l.ToArray();
            }
            else r = Object.FindObjectsByType<CompanionController>(FindObjectsSortMode.None);
            Note(site, sw, r.Length);
            return r;
        }

        internal static Stopwatch Start() => Stopwatch.StartNew();

        internal static void Note(string site, Stopwatch sw, int items)
        {
            double ms = sw.Elapsed.TotalMilliseconds;
            if (!s_sites.TryGetValue(site, out var s)) { if (s_sites.Count >= 64) return; s_sites[site] = s = new Site(); }
            s.Calls++; s.Items += items; s.Ms += ms; if (ms > s.Worst) s.Worst = ms;
            float now = Time.unscaledTime;
            if (now < s_sayAt) return;
            s_sayAt = now + 60f;
            var sb = new StringBuilder($"[ScanCensus] last 60 s ({(KeyOn ? "registries where their patch is active" : "scene search, key off")}):");
            foreach (var kv in s_sites) if (kv.Value.Calls > 0) sb.Append($" {kv.Key} {kv.Value.Calls}x {kv.Value.Ms:0.0} ms (worst {kv.Value.Worst:0.00}, {kv.Value.Items} walked);");
            FiresCore.Logging.FiresLogger.LogDebug(sb.ToString());
            foreach (var v in s_sites.Values) { v.Calls = 0; v.Items = 0; v.Ms = 0; v.Worst = 0; }
        }
    }
}
