using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>
    /// 0.2.918 ([ghost], lead round #32: no "[ResourceHealth] prefab … itself had infinite health" line, yet every session's repair
    /// "reset 10 … MineRock_Iron x6, MineRock_Copper x4"): log-only proof of WHO puts 1e30 back.
    ///  - the first ZDO float s_health write >= 1e20 on a MineRock / MineRock5 ZDO (5 per session) with its stack trace;
    ///  - each such rock as it loads (8 per session): the stored ZDO value, owner, and the live component value, so a value that came from
    ///    the world save is told apart from one written this session.
    /// </summary>
    internal static class ResourceHealthTrace
    {
        private const int MaxWrites = 5, MaxLoads = 8;
        private static int s_writes, s_loads;
        private static readonly HashSet<int> s_rockPrefabs = new HashSet<int>();
        private static bool s_rockPrefabsBuilt;

        private static bool IsRock(int prefabHash)
        {
            if (!s_rockPrefabsBuilt && ZNetScene.instance != null)
            {
                foreach (var go in ZNetScene.instance.m_prefabs)
                    if (go != null && (go.GetComponent<MineRock>() != null || go.GetComponent<MineRock5>() != null)) s_rockPrefabs.Add(go.name.GetStableHashCode());
                s_rockPrefabsBuilt = s_rockPrefabs.Count > 0;
            }
            return s_rockPrefabs.Contains(prefabHash);
        }

        [HarmonyPatch(typeof(ZDO), nameof(ZDO.Set), new[] { typeof(int), typeof(float) })]
        private static class ResourceHealthTrace_ZdoSetFloat
        {
            private static void Prefix(ZDO __instance, int hash, float value)
            {
                if (s_writes >= MaxWrites || hash != ZDOVars.s_health || value < ResourceHealth.InfiniteAt || __instance == null) return;
                try
                {
                    if (!IsRock(__instance.GetPrefab())) return;
                    s_writes++;
                    var prefab = ZNetScene.instance?.GetPrefab(__instance.GetPrefab());
                    FiresCore.Logging.FiresLogger.LogDebug($"[ResourceHealth] TRACE {s_writes}/{MaxWrites}: s_health = {value:E1} written on {(prefab != null ? prefab.name : "?")} at {__instance.GetPosition():F1} (owner {__instance.GetOwner()}, this peer {ZDOMan.GetSessionID()}, server {ZNet.instance != null && ZNet.instance.IsServer()}); who:\n{Environment.StackTrace}");
                }
                catch (Exception e) { Debug.LogWarning("[ResourceHealth] trace: " + e.Message); }
            }
        }

        private static void Loaded(Component rock, string kind, float before)
        {
            if (rock == null) return;
            var nv = rock.GetComponent<ZNetView>();
            var zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
            if (zdo == null) return;
            float stored = zdo.GetFloat(ZDOVars.s_health, -1f);
            float live = rock is MineRock mr ? mr.m_health : rock is MineRock5 m5 ? m5.m_health : -1f;
            if (stored < ResourceHealth.InfiniteAt && live < ResourceHealth.InfiniteAt) return;
            if (s_loads < MaxLoads)
            {
                s_loads++;
                // 0.2.926 (round #34, Fire "Companions are still not mining the ore"; 918's lines showed ZDO s_health -1 and data rev 0 with live 1e30,
                // so the save / ZDO is not where it comes from): the value BEFORE this method ran (from the prefab / Instantiate, or set by a patch
                // running ahead of it) and every Harmony patch on it, so the mod that sets it is named
                FiresCore.Logging.FiresLogger.LogDebug($"[ResourceHealth] TRACE load {s_loads}/{MaxLoads}: {kind} {Utils.GetPrefabName(rock.gameObject)} at {rock.transform.position:F1} loaded with ZDO s_health {stored:E1}, live m_health {before:E1} before {(kind == "MineRock" ? "Start" : "Awake")} -> {live:E1} after (owner {zdo.GetOwner()}, this peer {ZDOMan.GetSessionID()}, ZDO data rev {zdo.DataRevision}); prefab m_health {PrefabHealth(rock):E1}; patches: {PatchOwners(kind)}");
            }
            // Never changed here (Fire 10-27: "some players may place them as decoration and purposely have them at infinite health"): an
            // intentional Infinity Hammer 1e30 placement looks the same (ZDO normal, live 1e30); only the admin command resets health.
        }

        private static float PrefabHealth(Component rock)
        {
            var p = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(Utils.GetPrefabName(rock.gameObject)) : null;
            if (p == null) return -1f;
            var mr = p.GetComponent<MineRock>(); if (mr != null) return mr.m_health;
            var m5 = p.GetComponent<MineRock5>(); return m5 != null ? m5.m_health : -1f;
        }

        private static string s_ownersMR, s_ownersMR5;
        private static string PatchOwners(string kind)
        {
            ref string cache = ref (kind == "MineRock" ? ref s_ownersMR : ref s_ownersMR5);
            if (cache != null) return cache;
            var list = new List<string>();
            foreach (var m in kind == "MineRock" ? new[] { AccessTools.Method(typeof(MineRock), "Start") } : new[] { AccessTools.Method(typeof(MineRock5), "Awake") })
            {
                if (m == null) continue;
                var info = Harmony.GetPatchInfo(m);
                if (info == null) continue;
                foreach (var p in info.Prefixes) list.Add($"{m.Name} prefix {p.owner}");
                foreach (var p in info.Postfixes) list.Add($"{m.Name} postfix {p.owner}");
                foreach (var p in info.Transpilers) list.Add($"{m.Name} transpiler {p.owner}");
            }
            cache = list.Count > 0 ? string.Join(", ", list) : "none";
            return cache;
        }

        private static readonly Dictionary<int, float> s_before = new Dictionary<int, float>();

        [HarmonyPatch(typeof(MineRock), "Start")]
        private static class ResourceHealthTrace_MineRockStart
        {
            [HarmonyPriority(Priority.First)] private static void Prefix(MineRock __instance) { if (__instance != null) s_before[__instance.GetInstanceID()] = __instance.m_health; }
            [HarmonyPriority(Priority.Last)] private static void Postfix(MineRock __instance) { try { if (__instance == null) return; s_before.TryGetValue(__instance.GetInstanceID(), out float b); s_before.Remove(__instance.GetInstanceID()); Loaded(__instance, "MineRock", b); } catch (Exception e) { Debug.LogWarning("[ResourceHealth] trace load: " + e.Message); } }
        }

        [HarmonyPatch(typeof(MineRock5), "Awake")]
        private static class ResourceHealthTrace_MineRock5Awake
        {
            [HarmonyPriority(Priority.First)] private static void Prefix(MineRock5 __instance) { if (__instance != null) s_before[__instance.GetInstanceID()] = __instance.m_health; }
            [HarmonyPriority(Priority.Last)] private static void Postfix(MineRock5 __instance) { try { if (__instance == null) return; s_before.TryGetValue(__instance.GetInstanceID(), out float b); s_before.Remove(__instance.GetInstanceID()); Loaded(__instance, "MineRock5", b); } catch (Exception e) { Debug.LogWarning("[ResourceHealth] trace load: " + e.Message); } }
        }
    }
}
