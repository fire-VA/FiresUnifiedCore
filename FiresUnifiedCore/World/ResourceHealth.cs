using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>
    /// 0.2.902 ([ghost], Fire 10-27: "we will need to replace all of the infinite health mine rocks with ones that can be properly destroyed"
    /// + "Admin command"). Hammer placements keep Infinity Hammer's configured health (rig: "Overwrite health = 1E+30"), so a placed ore rock
    /// or tree can carry 1e30 health: a hit of tens of damage leaves the float unchanged and nothing ever breaks.
    /// 0.2.907 (round #28: the 60 m repair found "no infinite-health resources", the snapshot said 0, yet after the first hits the same
    /// iron / copper veins 13 m from home read 1E+30): Infinity Hammer keeps its health in the ZDO float s_health and puts it into the LIVE
    /// component's m_health; a MineRock5 seeds its areas from that field and writes them to the ZDO on its first hit, so a rock only
    /// "looked" infinite after it was hit. Now:
    ///  - <see cref="Infinite"/> also reads the float s_health on every kind and the live component (m_health, MineRock5 area health);
    ///  - the repair resets the float, the area string / "Health{i}" keys AND the live instance, and it runs on the peer that OWNS each ZDO
    ///    (a server-side write lost to the owner's next sync): "fap_fix_resource_health [radius 5-100, default 60]" -> the server checks the
    ///    admin and asks every peer; each repairs the ZDOs it owns (the server also the ownerless ones) and answers the caller with its
    ///    counts per prefab and the first positions + owners. Building pieces (WearNTear) are never touched. Not automatic.
    /// </summary>
    internal static class ResourceHealth
    {
        internal const float InfiniteAt = 1e20f;
        private const string Command = "fap_fix_resource_health", Rpc = "FiresCore_FixResourceHealth", ApplyRpc = "FiresCore_FixResourceHealthApply", ResultRpc = "FiresCore_FixResourceHealthResult";
        private const float MinRadius = 5f, MaxRadius = 100f, DefaultRadius = 60f;

        // ---- 0.2.916: the prefabs' own health, taken before anything can change it ---------------------------------------------
        // (round #31: rocks repaired in one session read "live health 1E+30" again the next, with IH's Overwrite health 0 on every peer; a
        // poisoned PREFAB m_health makes every loaded copy 1e30 and made the repair "reset" to 1e30): ZNetScene.Awake, first postfix, keeps
        // each harvestable prefab's m_health; repairs use it, and a prefab found at 1e20+ is put back (logged).
        private static readonly Dictionary<string, float> s_prefabHealth = new Dictionary<string, float>();

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class ResourceHealth_ZNetSceneAwake
        {
            [HarmonyPriority(Priority.First)]
            private static void Postfix(ZNetScene __instance)
            {
                try
                {
                    int n = 0;
                    foreach (var go in __instance.m_prefabs)
                    {
                        if (go == null || go.GetComponent<WearNTear>() != null) continue;
                        float h = BaseHealth(go);
                        if (h <= 0f || h >= InfiniteAt) continue;
                        if (!s_prefabHealth.ContainsKey(go.name)) { s_prefabHealth[go.name] = h; n++; }
                    }
                    FiresCore.Logging.FiresLogger.LogDebug($"[ResourceHealth] kept the own health of {n} harvestable prefab(s) at ZNetScene.Awake");
                }
                catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug("[ResourceHealth] prefab health snapshot: " + e.Message); }
            }
        }

        private static float BaseHealth(GameObject go)
        {
            var mr5 = go.GetComponent<MineRock5>(); if (mr5 != null) return mr5.m_health;
            var mr = go.GetComponent<MineRock>(); if (mr != null) return mr.m_health;
            var tb = go.GetComponent<TreeBase>(); if (tb != null) return tb.m_health;
            var tl = go.GetComponent<TreeLog>(); if (tl != null) return tl.m_health;
            var de = go.GetComponent<Destructible>(); if (de != null) return de.m_health;
            return -1f;
        }

        /// <summary>The prefab's own health: the ZNetScene.Awake snapshot, else the prefab's current value.</summary>
        private static float OwnHealth(GameObject prefab, float current)
            => prefab != null && s_prefabHealth.TryGetValue(prefab.name, out float h) ? h : current;

        /// <summary>A poisoned prefab (m_health 1e20+) is put back to its snapshot. Returns true when it fixed one.</summary>
        private static bool RestorePrefab(GameObject prefab)
        {
            if (prefab == null || !s_prefabHealth.TryGetValue(prefab.name, out float own)) return false;
            bool fixedIt = false;
            var mr5 = prefab.GetComponent<MineRock5>(); if (mr5 != null && mr5.m_health >= InfiniteAt) { mr5.m_health = own; fixedIt = true; }
            var mr = prefab.GetComponent<MineRock>(); if (mr != null && mr.m_health >= InfiniteAt) { mr.m_health = own; fixedIt = true; }
            var tb = prefab.GetComponent<TreeBase>(); if (tb != null && tb.m_health >= InfiniteAt) { tb.m_health = own; fixedIt = true; }
            var tl = prefab.GetComponent<TreeLog>(); if (tl != null && tl.m_health >= InfiniteAt) { tl.m_health = own; fixedIt = true; }
            var de = prefab.GetComponent<Destructible>(); if (de != null && de.m_health >= InfiniteAt) { de.m_health = own; fixedIt = true; }
            if (fixedIt) FiresCore.Logging.FiresLogger.LogInfo($"[ResourceHealth] prefab {prefab.name} itself had infinite health (every copy loaded with it): put back to its own {own:0}");
            return fixedIt;
        }

        private static float WorldScaled(float h)
            => h + (float)Game.m_worldLevel * h * (Game.instance != null ? Game.instance.m_worldLevelMineHPMultiplier : 0f);

        // ---- reading -----------------------------------------------------------------------------------------------------
        private static FieldInfo s_areasField, s_areaHealthField;
        private static IList AreasOf(MineRock5 rock)
        {
            if (rock == null) return null;
            if (s_areasField == null) s_areasField = AccessTools.Field(typeof(MineRock5), "m_hitAreas");
            return s_areasField?.GetValue(rock) as IList;
        }
        private static float AreaHealth(object area)
        {
            if (area == null) return -1f;
            if (s_areaHealthField == null) s_areaHealthField = AccessTools.Field(area.GetType(), "m_health");
            return s_areaHealthField != null ? (float)s_areaHealthField.GetValue(area) : -1f;
        }
        private static void SetAreaHealth(object area, float h)
        {
            if (area == null) return;
            if (s_areaHealthField == null) s_areaHealthField = AccessTools.Field(area.GetType(), "m_health");
            s_areaHealthField?.SetValue(area, h);
        }

        /// <summary>True when a harvestable is "infinite" in its ZDO (any of vanilla's or Infinity Hammer's keys) or in its live component.</summary>
        internal static bool Infinite(GameObject go, out string detail)
        {
            detail = null;
            if (go == null) return false;
            var nv = go.GetComponentInParent<ZNetView>();
            var zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
            var root = nv != null ? nv.gameObject : go;
            if (root.GetComponent<WearNTear>() != null) return false;
            float stored = zdo != null ? MaxStored(zdo, root.GetComponent<MineRock5>() != null, root.GetComponent<MineRock>() != null) : -1f;
            float live = LiveHealth(root);
            if (stored < InfiniteAt && live < InfiniteAt) return false;
            detail = stored >= InfiniteAt ? $"stored health {stored:0.##E+0}" : $"live health {live:0.##E+0} (the ZDO looks normal until a hit saves it)";
            return true;
        }

        private static float LiveHealth(GameObject root)
        {
            float best = -1f;
            var mr5 = root.GetComponent<MineRock5>();
            if (mr5 != null)
            {
                best = Mathf.Max(best, mr5.m_health);
                var areas = AreasOf(mr5);
                if (areas != null) foreach (var a in areas) best = Mathf.Max(best, AreaHealth(a));
            }
            var mr = root.GetComponent<MineRock>(); if (mr != null) best = Mathf.Max(best, mr.m_health);
            var tb = root.GetComponent<TreeBase>(); if (tb != null) best = Mathf.Max(best, tb.m_health);
            var tl = root.GetComponent<TreeLog>(); if (tl != null) best = Mathf.Max(best, tl.m_health);
            var de = root.GetComponent<Destructible>(); if (de != null) best = Mathf.Max(best, de.m_health);
            return best;
        }

        private static readonly HashSet<int> s_said = new HashSet<int>();

        /// <summary>For a chore's target search: true (skip it) when it has infinite health; says so once per object per session.</summary>
        internal static bool Skip(GameObject go, string chore)
        {
            if (!Infinite(go, out string detail)) return false;
            if (go != null && s_said.Add(go.GetInstanceID()))
            {
                var nv = go.GetComponentInParent<ZNetView>(); var zdo = nv != null && nv.IsValid() ? nv.GetZDO() : null;
                FiresCore.Logging.FiresLogger.LogInfo($"[Chores] {chore}: {Utils.GetPrefabName(go)} at ({go.transform.position.x:0}, {go.transform.position.z:0}) has infinite health ({detail}; owner {(zdo != null ? zdo.GetOwner().ToString() : "?")}; Infinity Hammer 'Overwrite health' at placement): skipped - an admin can reset it with {Command}");
            }
            return true;
        }

        // The highest health the ZDO stores for this kind, including Infinity Hammer's float s_health (-1 when it stores none)
        private static float MaxStored(ZDO zdo, bool mineRock5, bool mineRock)
        {
            float best = zdo.GetFloat(ZDOVars.s_health, -1f);
            if (mineRock5) foreach (float a in Areas(zdo)) best = Mathf.Max(best, a);
            if (mineRock) for (int i = 0; i < 128; i++) best = Mathf.Max(best, zdo.GetFloat("Health" + i, -1f));
            return best;
        }

        internal static List<float> Areas(ZDO zdo)
        {
            var r = new List<float>();
            string s = zdo.GetString(ZDOVars.s_health);
            if (string.IsNullOrEmpty(s)) return r;
            try
            {
                var pkg = new ZPackage(Convert.FromBase64String(s));
                int n = pkg.ReadInt();
                for (int i = 0; i < n && i < 4096; i++) r.Add(pkg.ReadSingle());
            }
            catch { }
            return r;
        }

        // ---- repairing ---------------------------------------------------------------------------------------------------
        /// <summary>Puts an "infinite" harvestable back to its prefab's own health: the ZDO keys and, when loaded here, the live instance.
        /// Returns the kind it fixed, or null.</summary>
        internal static string Repair(ZDO zdo)
        {
            var prefab = zdo != null && ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            if (prefab == null || prefab.GetComponent<WearNTear>() != null) return null;   // building pieces keep Infinity Hammer's health
            RestorePrefab(prefab);   // 0.2.916
            var live = ZNetScene.instance.FindInstance(zdo);
            string kind = null;
            float f = zdo.GetFloat(ZDOVars.s_health, -1f);

            var mr5 = prefab.GetComponent<MineRock5>();
            if (mr5 != null)
            {
                float def = WorldScaled(OwnHealth(prefab, mr5.m_health));
                if (f >= InfiniteAt) { zdo.Set(ZDOVars.s_health, def); kind = "MineRock5"; }   // Infinity Hammer's float
                var areas = Areas(zdo);
                if (areas.Count > 0 && areas.Max() >= InfiniteAt)
                {
                    var pkg = new ZPackage(); pkg.Write(areas.Count);
                    foreach (float a in areas) pkg.Write(a >= InfiniteAt ? def : a);   // a broken area (<= 0) stays broken
                    zdo.Set(ZDOVars.s_health, Convert.ToBase64String(pkg.GetArray()));
                    kind = "MineRock5";
                }
                var lr = live != null ? live.GetComponent<MineRock5>() : null;
                if (lr != null)
                {
                    bool fixedLive = false;
                    if (lr.m_health >= InfiniteAt) { lr.m_health = OwnHealth(prefab, mr5.m_health); fixedLive = true; }
                    var la = AreasOf(lr);
                    if (la != null) foreach (var a in la) if (AreaHealth(a) >= InfiniteAt) { SetAreaHealth(a, def); fixedLive = true; }
                    if (fixedLive) kind = "MineRock5";
                }
                return kind;
            }
            var mr = prefab.GetComponent<MineRock>();
            if (mr != null)
            {
                float def = WorldScaled(OwnHealth(prefab, mr.m_health));
                if (f >= InfiniteAt) { zdo.Set(ZDOVars.s_health, def); kind = "MineRock"; }
                for (int i = 0; i < 128; i++) if (zdo.GetFloat("Health" + i, -1f) >= InfiniteAt) { zdo.Set("Health" + i, def); kind = "MineRock"; }
                var lr = live != null ? live.GetComponent<MineRock>() : null;
                if (lr != null && lr.m_health >= InfiniteAt) { lr.m_health = OwnHealth(prefab, mr.m_health); kind = "MineRock"; }
                return kind;
            }
            var tb = prefab.GetComponent<TreeBase>(); var tl = prefab.GetComponent<TreeLog>(); var de = prefab.GetComponent<Destructible>();
            float baseH = tb != null ? tb.m_health : tl != null ? tl.m_health : de != null ? de.m_health : -1f; if (baseH >= 0f) baseH = OwnHealth(prefab, baseH);
            if (baseH < 0f) return null;
            string name = tb != null ? "TreeBase" : tl != null ? "TreeLog" : "Destructible";
            if (f >= InfiniteAt) { zdo.Set(ZDOVars.s_health, WorldScaled(baseH)); kind = name; }
            if (live != null)
            {
                var ltb = live.GetComponent<TreeBase>(); if (ltb != null && ltb.m_health >= InfiniteAt) { ltb.m_health = baseH; kind = name; }
                var ltl = live.GetComponent<TreeLog>(); if (ltl != null && ltl.m_health >= InfiniteAt) { ltl.m_health = baseH; kind = name; }
                var lde = live.GetComponent<Destructible>(); if (lde != null && lde.m_health >= InfiniteAt) { lde.m_health = baseH; kind = name; }
            }
            return kind;
        }

        /// <summary>0.2.911 (round #29: the repair reset rocks but no trees; a Beech then took "10 swings, broke 0"): the live instance's health
        /// is this peer's own memory (never synced), so EVERY peer that has the object loaded resets it, owner or not; only the owner writes the
        /// ZDO (<see cref="Repair"/>). Returns the kind it fixed here, or null.</summary>
        internal static string RepairLive(ZDO zdo)
        {
            var prefab = zdo != null && ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(zdo.GetPrefab()) : null;
            var live = prefab != null ? ZNetScene.instance.FindInstance(zdo) : null;
            if (live == null || prefab.GetComponent<WearNTear>() != null) return null;
            RestorePrefab(prefab);   // 0.2.916: a poisoned prefab first (every copy would load at 1e30)
            string kind = null;
            var mr5 = prefab.GetComponent<MineRock5>(); var lr5 = live.GetComponent<MineRock5>();
            if (mr5 != null && lr5 != null)
            {
                float def = WorldScaled(OwnHealth(prefab, mr5.m_health));
                if (lr5.m_health >= InfiniteAt) { lr5.m_health = OwnHealth(prefab, mr5.m_health); kind = "MineRock5"; }
                var la = AreasOf(lr5);
                if (la != null) foreach (var a in la) if (AreaHealth(a) >= InfiniteAt) { SetAreaHealth(a, def); kind = "MineRock5"; }
            }
            var mr = prefab.GetComponent<MineRock>(); var lr = live.GetComponent<MineRock>();
            if (mr != null && lr != null && lr.m_health >= InfiniteAt) { lr.m_health = OwnHealth(prefab, mr.m_health); kind = "MineRock"; }
            var tb = prefab.GetComponent<TreeBase>(); var ltb = live.GetComponent<TreeBase>();
            if (tb != null && ltb != null && ltb.m_health >= InfiniteAt) { ltb.m_health = OwnHealth(prefab, tb.m_health); kind = "TreeBase"; }
            var tl = prefab.GetComponent<TreeLog>(); var ltl = live.GetComponent<TreeLog>();
            if (tl != null && ltl != null && ltl.m_health >= InfiniteAt) { ltl.m_health = OwnHealth(prefab, tl.m_health); kind = "TreeLog"; }
            var de = prefab.GetComponent<Destructible>(); var lde = live.GetComponent<Destructible>();
            if (de != null && lde != null && lde.m_health >= InfiniteAt) { lde.m_health = OwnHealth(prefab, de.m_health); kind = kind ?? "Destructible"; }
            return kind;
        }

        // ---- the admin command ---------------------------------------------------------------------------------------------
        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class ResourceHealth_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[radius] [FiresUnifiedCore] (admin) reset placed ore rocks / trees / logs within the radius (5-100 m, default 60) whose health is infinite (Infinity Hammer 'Overwrite health') to the prefab's own health, so they can be mined and felled",
                    args =>
                    {
                        var p = Player.m_localPlayer;
                        if (p == null || ZRoutedRpc.instance == null) { args.Context?.AddString($"{Command}: no local player / network"); return; }
                        float r = DefaultRadius;
                        if (args.Length > 1 && !float.TryParse(args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out r)) { args.Context?.AddString($"{Command}: radius '{args[1]}' is not a number"); return; }
                        r = Mathf.Clamp(r, MinRadius, MaxRadius);
                        var pkg = new ZPackage(); pkg.Write(p.transform.position); pkg.Write(r);
                        Register();
                        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), Rpc, pkg);
                        args.Context?.AddString($"{Command}: asked the server to fix infinite-health resources within {r:0} m");
                        FiresCore.Logging.FiresLogger.LogInfo($"[ResourceHealth] {Command}: asked the server for ({p.transform.position.x:0}, {p.transform.position.z:0}) r={r:0} m");
                    }, isCheat: true);
            }
        }

        private static ZRoutedRpc s_registeredOn;
        internal static void Register()
        {
            var rr = ZRoutedRpc.instance;
            if (rr == null || s_registeredOn == rr) return;
            s_registeredOn = rr;
            rr.Register<ZPackage>(Rpc, OnFix);
            rr.Register<ZPackage>(ApplyRpc, OnApply);
            rr.Register<ZPackage>(ResultRpc, OnResult);
        }

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class ResourceHealth_ZNetAwake
        {
            private static void Postfix() { try { Register(); } catch (Exception e) { Debug.LogWarning("[ResourceHealth] register: " + e.Message); } }
        }

        // server: the sender must be an admin; the radius is capped; then every peer (the server too) repairs the ZDOs it owns
        private static void OnFix(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || pkg == null) return;
            try
            {
                if (!FiresCore.Lifecycle.AdminCheck.IsAdmin(sender)) { FiresCore.Logging.FiresLogger.LogInfo($"[ResourceHealth] {Command} from {sender} refused: not an admin"); Reply(sender, "server", "refused: you are not an admin on this server"); return; }
                Vector3 at = pkg.ReadVector3(); float r = Mathf.Clamp(pkg.ReadSingle(), MinRadius, MaxRadius);
                var apply = new ZPackage(); apply.Write(at); apply.Write(r); apply.Write(sender);
                FiresCore.Logging.FiresLogger.LogInfo($"[ResourceHealth] {Command} by {sender} at ({at.x:0}, {at.z:0}) r={r:0} m: asking every peer to repair the resources it owns");
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, ApplyRpc, apply);
            }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogInfo($"[ResourceHealth] {Command} failed: {e.Message}"); Reply(sender, "server", "failed: " + e.Message); }
        }

        // every peer: only the server may ask; repairs the ZDOs this peer owns (the server: also those nobody owns) and answers the caller
        private static void OnApply(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || ZRoutedRpc.instance == null || pkg == null) return;
            if (sender != ZRoutedRpc.instance.GetServerPeerID()) { FiresCore.Logging.FiresLogger.LogInfo($"[ResourceHealth] apply from {sender} ignored: not the server"); return; }
            string peer = ZNet.instance.IsServer() ? "server" : (Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "client");
            long caller = 0;
            string reply;
            try
            {
                Vector3 at = pkg.ReadVector3(); float r = Mathf.Clamp(pkg.ReadSingle(), MinRadius, MaxRadius); caller = pkg.ReadLong();
                bool server = ZNet.instance.IsServer();
                var near = new List<ZDO>(); var visited = new HashSet<ZoneSystem.SectorIndex>();
                var zone = FiresCore.World.RingZones.ObjectSector(at);   // halo ring: object sectors are ring-keyed (as RemoteStore)
                int span = Mathf.CeilToInt(r / ZoneSystem.c_ZoneSize) + 1;
                for (int dx = -span; dx <= span; dx++) for (int dz = -span; dz <= span; dz++)
                    ZDOMan.instance.FindObjects(new Vector2s((short)(zone.x + dx), (short)(zone.y + dz)), near, visited);
                var byPrefab = new Dictionary<string, int>(); var where = new List<string>(); int seen = 0, notMine = 0;
                foreach (var z in near)
                {
                    if (z == null) continue;
                    Vector3 zp = z.GetPosition(); zp.y = at.y;
                    if (Vector3.Distance(zp, at) > r) continue;
                    seen++;
                    bool mine = z.IsOwner() || (server && !z.HasOwner());
                    if (!mine) notMine++;
                    string kind = mine ? Repair(z) : RepairLive(z);   // 0.2.911: not the owner -> still reset the loaded instance here
                    if (kind == null) continue;
                    string name = ZNetScene.instance.GetPrefab(z.GetPrefab())?.name ?? z.GetPrefab().ToString();
                    byPrefab[name] = byPrefab.TryGetValue(name, out int n) ? n + 1 : 1;
                    if (where.Count < 8) where.Add($"{name} ({z.GetPosition().x:0}, {z.GetPosition().z:0})");
                }
                int total = byPrefab.Values.Sum();
                reply = total == 0 ? $"no infinite-health resources it owns within {r:0} m ({seen} objects in range, {notMine} owned by other peers - their loaded copies checked here too)"
                    : $"reset {total} to their prefab health within {r:0} m: {string.Join(", ", byPrefab.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}"))}; at {string.Join(", ", where)} ({notMine} owned by other peers)";
            }
            catch (Exception e) { reply = "failed: " + e.Message; }
            FiresCore.Logging.FiresLogger.LogInfo($"[ResourceHealth] {Command} on {peer}: {reply}");
            if (caller != 0) Reply(caller, peer, reply);
        }

        private static void Reply(long to, string peer, string text)
        {
            var back = new ZPackage(); back.Write(peer); back.Write(text.Length > 400 ? text.Substring(0, 400) : text);
            ZRoutedRpc.instance.InvokeRoutedRPC(to, ResultRpc, back);
        }

        private static void OnResult(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            string peer = pkg.ReadString(), text = pkg.ReadString();
            FiresCore.Logging.FiresLogger.LogInfo($"[ResourceHealth] {Command}: {peer}: {text}");
            Console.instance?.Print($"{Command}: {peer}: {text}");
            if (text.StartsWith("reset "))   // 0.2.905 (lead): the rocks can be mined now - drop the "took no damage" skips learned before the repair
            {
                int cleared = FiresCore.Npc.IdleBehaviors.ResourceGatheringBehavior.ClearNoProgressSkips();
                FiresCore.Logging.FiresLogger.LogInfo($"[ResourceHealth] {Command}: cleared {cleared} stale 'took no damage' mining skip(s)");
            }
        }
    }
}
