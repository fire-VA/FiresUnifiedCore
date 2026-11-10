using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using FiresCore.Npc.AI;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// 0.2.92x ([ghost], Fire 10-27: "Companions will also need to rebuild any pieces that were completely destroyed by a raid" + "must never
    /// rebuild something that the player actually destroyed themselves ... only ... damaged by other players or monsters or things like fire
    /// spread or rain damage"; Tools\HOMESTEAD_DEFENCE_PLAN_20261027.md section 6).
    ///  - RECORD (the piece's ZDO owner, in WearNTear.Destroy): the cause comes from the destroying path -
    ///      a hammer / tool removal (WearNTear.Remove / RPC_Remove) = NEVER queued;
    ///      the last hit (RPC_Damage, under 3 s old) by a creature or another player = "monster X" / "player X"; fire damage = "fire";
    ///      inside UpdateWear with no fresh hit: within 10 s / 20 m of raid damage = "collapse after the raid", support under the minimum =
    ///      plain support loss (not queued), else "weather wear".
    ///    The owner sends FiresCore_RaidDamage to the server (prefab, position, rotation, creator, chest tags, cause); the server checks the
    ///    prefab is a known piece, the spot is within 60 m of a staying companion's home, and a rate cap (30 per 10 s per sender).
    ///  - STORE: the server's per-world file BepInEx\cache\FiresUnifiedCore\RaidRebuild\&lt;world&gt;.txt (like the Job Board's), cap 200.
    ///    Chests come back EMPTY with their old ChestTags (Fire); doors closed.
    ///  - REBUILD (the server, while HomesteadDefence.QuietForRepairs - no threat left + 30 s quiet; a new alert pauses it): lowest first;
    ///    a staying companion it owns walks to the chests (storage terminal path) for the real build cost, then to the spot, and places the
    ///    piece; not affordable = skipped, retried every 10 min; a blocked spot = skipped, dropped after 3 tries.
    /// Lines: "[Rebuild] queued &lt;prefab&gt; at (x, y, z) destroyed by &lt;cause&gt;", "[Rebuild] not queued &lt;prefab&gt;: &lt;why&gt;",
    ///        "[Rebuild] &lt;name&gt; rebuilt &lt;prefab&gt; (cost ...) at (x, y, z)", "[Rebuild] skipped &lt;prefab&gt;: no materials (needs ...) | blocked by ...".
    /// Under [Homestead Defence] Back each other up.
    /// </summary>
    internal static class RaidRebuild
    {
        private const string Rpc = "FiresCore_RaidDamage";
        private const int MaxEntries = 200, RateCap = 30;
        private const float FreshHit = 3f, RaidLink = 10f, RaidLinkRange = 20f, NoMaterialsRetry = 600f, BlockedRetry = 60f, HomeRange = 60f;

        internal sealed class Entry
        {
            public string Prefab, Tags = "", Cause;
            public Vector3 Pos; public Quaternion Rot; public long Creator;
            public int Tries; public float RetryAt;
        }

        private static readonly List<Entry> s_queue = new List<Entry>();
        private static string s_file;
        internal static int Queued => s_queue.Count;
        internal static IEnumerable<Entry> Queue => s_queue;

        // ---- cause tracking (every peer; acted on by the piece's owner)
        private static readonly HashSet<int> s_removing = new HashSet<int>();
        private static readonly Dictionary<int, (string who, bool fire, float at)> s_lastHit = new Dictionary<int, (string, bool, float)>();
        private static readonly List<(Vector3 at, float t)> s_raidHits = new List<(Vector3, float)>();
        private static int s_inWear;
        private static readonly System.Reflection.MethodInfo s_getSupport = AccessTools.Method(typeof(WearNTear), "GetSupport");

        [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Remove))]
        private static class RaidRebuild_Remove { private static void Prefix(WearNTear __instance) { if (__instance != null) s_removing.Add(__instance.GetInstanceID()); } }

        [HarmonyPatch(typeof(WearNTear), "RPC_Remove")]
        private static class RaidRebuild_RpcRemove { private static void Prefix(WearNTear __instance) { if (__instance != null) s_removing.Add(__instance.GetInstanceID()); } }

        [HarmonyPatch(typeof(WearNTear), "RPC_Damage")]
        private static class RaidRebuild_Hit
        {
            private static void Prefix(WearNTear __instance, HitData hit)
            {
                if (!HomesteadDefence.On || __instance == null || hit == null) return;
                var att = hit.GetAttacker();
                bool fire = hit.m_damage.m_fire > 0f;
                string who = null;
                if (att is Player p) { if (Player.m_localPlayer == null || p.GetPlayerID() != __instance.GetComponent<Piece>()?.GetCreator()) who = "player " + p.GetPlayerName(); }
                else if (att != null && !att.IsTamed()) who = "monster " + (Localization.instance != null ? Localization.instance.Localize(att.m_name) : att.m_name);
                if (who == null && !fire) return;
                s_lastHit[__instance.GetInstanceID()] = (who ?? "fire", fire, Time.time);
                if (who != null) { s_raidHits.Add((__instance.transform.position, Time.time)); if (s_raidHits.Count > 64) s_raidHits.RemoveAt(0); }
            }
        }

        [HarmonyPatch(typeof(WearNTear), "UpdateWear")]
        private static class RaidRebuild_Wear
        {
            private static void Prefix() => s_inWear++;
            private static void Finalizer() => s_inWear = Math.Max(0, s_inWear - 1);
        }

        [HarmonyPatch(typeof(WearNTear), "Destroy")]
        private static class RaidRebuild_Destroy
        {
            private static void Prefix(WearNTear __instance)
            {
                if (!HomesteadDefence.On || __instance == null) return;
                try { Destroyed(__instance); } catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug("[Rebuild] record failed: " + e.Message); }
            }
        }

        private static void Destroyed(WearNTear w)
        {
            int id = w.GetInstanceID();
            var nv = w.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return;
            string prefab = Utils.GetPrefabName(w.gameObject);
            Vector3 pos = w.transform.position;
            if (s_removing.Remove(id)) { FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] not queued {prefab} at {RouteMemory.At(pos)}: removed with the hammer / a tool"); return; }
            string cause = null;
            if (s_lastHit.TryGetValue(id, out var h) && Time.time - h.at < FreshHit) cause = h.fire && h.who == "fire" ? "fire" : h.who;
            else if (s_inWear > 0)
            {
                bool raid = s_raidHits.Any(r => Time.time - r.t < RaidLink && Vector3.Distance(r.at, pos) < RaidLinkRange) || (HomesteadDefence.AnyAlert || Time.time - HomesteadDefence.LastResolvedAt < RaidLink);
                float support = s_getSupport != null ? (float)s_getSupport.Invoke(w, null) : 1f;
                float min = MinSupport(w);
                if (raid) cause = "collapse after the raid";
                else if (support < min) { FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] not queued {prefab} at {RouteMemory.At(pos)}: support loss ({support:0.0} < {min:0.0}), not raid damage"); return; }
                else cause = "weather wear";
            }
            s_lastHit.Remove(id);
            if (cause == null) { FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] not queued {prefab} at {RouteMemory.At(pos)}: no damage cause (not a raid, fire or wear)"); return; }
            var z = nv.GetZDO();
            var pkg = new ZPackage();
            pkg.Write(prefab); pkg.Write(pos); pkg.Write(w.transform.rotation); pkg.Write(z.GetLong(ZDOVars.s_creator, 0L));
            pkg.Write(w.GetComponent<Container>() != null ? z.GetString(FiresCore.Storage.ChestTags.ZdoKey, "") : "");
            pkg.Write(cause);
            pkg.Write(z.m_uid);   // the server checks the piece by its ZDO and takes every stored field from it ([wishbone] review)
            Register();
            if (ZNet.instance != null && ZNet.instance.IsServer()) { pkg.SetPos(0); OnRecord(ZDOMan.GetSessionID(), pkg); }
            else ZRoutedRpc.instance?.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), Rpc, pkg);
        }

        private static float MinSupport(WearNTear w)
        {
            try
            {
                var m = AccessTools.Method(typeof(WearNTear), "GetMaterialProperties");
                if (m == null) return 0f;
                var args = new object[] { 0f, 0f, 0f, 0f };
                m.Invoke(w, args);
                return (float)args[1];   // (maxSupport, minSupport, horizontalLoss, verticalLoss)
            }
            catch { return 0f; }
        }

        // ---- server: the queue
        private static ZRoutedRpc s_registeredOn;
        internal static void Register()
        {
            var rr = ZRoutedRpc.instance;
            if (rr == null || s_registeredOn == rr) return;
            s_registeredOn = rr;
            rr.Register<ZPackage>(Rpc, OnRecord);
        }

        [HarmonyPatch(typeof(ZNet), "Awake")]
        private static class RaidRebuild_ZNetAwake { private static void Postfix() { try { Register(); } catch (Exception e) { Debug.LogWarning("[Rebuild] register: " + e.Message); } } }

        private static readonly Dictionary<long, List<float>> s_rate = new Dictionary<long, List<float>>();

        private const float SenderReach = 128f;
        private static readonly HashSet<ZDOID> s_accepted = new HashSet<ZDOID>();   // replay guard (per server session)

        // no line breaks / tabs (the queue file is one entry per line, tab-separated), capped
        private static string Clean(string s, int max)
        {
            if (s == null) return null;
            s = s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            return s.Length > max ? s.Substring(0, max) : s;
        }

        private static bool AllowedCause(string c)
            => c.StartsWith("monster ") || c.StartsWith("player ") || c == "fire" || c == "weather wear" || c == "collapse after the raid";

        private static void OnRecord(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || pkg == null) return;
            try
            {
                if (!s_rate.TryGetValue(sender, out var times)) s_rate[sender] = times = new List<float>();
                times.RemoveAll(t => Time.time - t > 10f);
                if (times.Count >= RateCap) { FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] record from {sender} refused: over {RateCap} in 10 s"); return; }
                times.Add(Time.time);
                string sentPrefab = pkg.ReadString(); Vector3 sentPos = pkg.ReadVector3();
                pkg.ReadQuaternion(); pkg.ReadLong(); pkg.ReadString();   // the client's rotation / creator / tags: not trusted (the ZDO's are used)
                string cause = Clean(pkg.ReadString(), 80);
                // [wishbone] security review 10-27: the piece must still exist on the server as the sent prefab at the sent spot, and every
                // stored field comes from ITS ZDO; a ZDOID is accepted once; the sender must be near it.
                if (pkg.GetPos() >= pkg.Size()) { FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] record from {sender} refused: no ZDOID (an old client)"); return; }
                ZDOID id = pkg.ReadZDOID();
                if (sentPrefab == null || sentPrefab.Length > 64 || cause == null || !AllowedCause(cause)) { FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] record from {sender} refused: bad prefab / cause"); return; }
                ZDO zdo = ZDOMan.instance != null ? ZDOMan.instance.GetZDO(id) : null;
                if (zdo == null) { FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] record from {sender} refused: zdo {id} gone (the destroy synced first; not rebuilt)"); return; }
                if (zdo.GetPrefab() != sentPrefab.GetStableHashCode()) { FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] record from {sender} refused: prefab mismatch for zdo {id} (sent '{sentPrefab}')"); return; }
                if ((zdo.GetPosition() - sentPos).sqrMagnitude > 0.25f) { FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] record from {sender} refused: position mismatch for zdo {id} ({Vector3.Distance(zdo.GetPosition(), sentPos):0.0} m)"); return; }
                if (s_accepted.Contains(id)) { FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] record from {sender} refused: replay of zdo {id}"); return; }
                Vector3 zpos = zdo.GetPosition();
                ZNetPeer peer = ZNet.instance.GetPeer(sender);
                if (sender != ZDOMan.GetSessionID() && (peer == null || Vector3.Distance(peer.m_refPos, zpos) > SenderReach)) { FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] record from {sender} refused: the sender is {(peer != null ? $"{Vector3.Distance(peer.m_refPos, zpos):0} m" : "not a peer, so")} from the piece"); return; }
                var go = ZNetScene.instance?.GetPrefab(zdo.GetPrefab());
                if (go == null || go.GetComponent<Piece>() == null || go.GetComponent<WearNTear>() == null) { FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] record refused: zdo {id} is not a building piece"); return; }
                s_accepted.Add(id);
                var e = new Entry { Prefab = go.name, Pos = zpos, Rot = zdo.GetRotation(), Creator = zdo.GetLong(ZDOVars.s_creator, 0L), Tags = go.GetComponent<Container>() != null ? Clean(zdo.GetString(FiresCore.Storage.ChestTags.ZdoKey, ""), 512) : "", Cause = cause };
                if (!NearHome(e.Pos)) { FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] not queued {e.Prefab} at {RouteMemory.At(e.Pos)}: not within {HomeRange:0} m of a homestead"); return; }
                Load();
                if (s_queue.Any(q => q.Prefab == e.Prefab && Vector3.Distance(q.Pos, e.Pos) < 0.2f)) return;
                if (s_queue.Count >= MaxEntries) s_queue.RemoveAt(0);
                s_queue.Add(e);
                Save();
                FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] queued {e.Prefab} at {RouteMemory.At(e.Pos)} destroyed by {e.Cause}{(e.Tags.Length > 0 ? " (chest: rebuilt empty, tags kept)" : go.GetComponent<Container>() != null ? " (chest: rebuilt empty)" : "")}");
                EnsureRunner();
            }
            catch (Exception ex) { FiresCore.Logging.FiresLogger.LogDebug("[Rebuild] record failed: " + ex.Message); }
        }

        private static bool NearHome(Vector3 at)
        {
            foreach (var c in CompanionController.AllCompanions)
            {
                if (c == null || !c.isTamed || c.ShouldBeFollowing) continue;
                var idle = c.GetComponent<FiresCore.Npc.CompanionIdleBehavior>();
                if (idle != null && idle.HasHomePosition && Vector3.Distance(idle.HomePosition, at) <= HomeRange) return true;
            }
            return false;
        }

        private static string FileFor()
        {
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
            if (string.IsNullOrEmpty(world)) return null;
            foreach (char ch in Path.GetInvalidFileNameChars()) world = world.Replace(ch, '_');
            return Path.Combine(BepInEx.Paths.BepInExRootPath, "cache", "FiresUnifiedCore", "RaidRebuild", world + ".txt");
        }

        private static void Load()
        {
            string f = FileFor();
            if (f == null || f == s_file) return;
            s_file = f; s_queue.Clear();
            if (!File.Exists(f)) return;
            var inv = CultureInfo.InvariantCulture;
            foreach (var line in File.ReadAllLines(f))
            {
                var p = line.Split('\t');
                if (p.Length < 11) continue;
                try
                {
                    s_queue.Add(new Entry
                    {
                        Prefab = p[0], Pos = new Vector3(float.Parse(p[1], inv), float.Parse(p[2], inv), float.Parse(p[3], inv)),
                        Rot = new Quaternion(float.Parse(p[4], inv), float.Parse(p[5], inv), float.Parse(p[6], inv), float.Parse(p[7], inv)),
                        Creator = long.Parse(p[8], inv), Tags = p[9], Cause = p[10]
                    });
                }
                catch { }
            }
        }

        private static void Save()
        {
            string f = FileFor();
            if (f == null) return;
            var inv = CultureInfo.InvariantCulture;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(f));
                File.WriteAllLines(f, s_queue.Select(e => string.Join("\t", e.Prefab, e.Pos.x.ToString(inv), e.Pos.y.ToString(inv), e.Pos.z.ToString(inv),
                    e.Rot.x.ToString(inv), e.Rot.y.ToString(inv), e.Rot.z.ToString(inv), e.Rot.w.ToString(inv), e.Creator.ToString(inv), (e.Tags ?? "").Replace('\t', ' '), (e.Cause ?? "").Replace('\t', ' '))));
            }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug("[Rebuild] save failed: " + e.Message); }
        }

        internal static void ClearForTest() { s_queue.Clear(); Save(); s_job = null; }

        // ---- server: the rebuild, one piece at a time
        private enum Step { ToChest, Pull, ToSpot, Place }
        private sealed class Job
        {
            public Entry E; public CompanionController C; public Step Step;
            public List<(string prefab, string shared, int amount)> Cost; public int CostIdx; public Container Chest; public float StepAt;
        }
        private static Job s_job;
        private static Runner s_runner;

        private static void EnsureRunner()
        {
            if (s_runner != null) return;
            var go = new GameObject("FiresRaidRebuild");
            UnityEngine.Object.DontDestroyOnLoad(go);
            s_runner = go.AddComponent<Runner>();
        }

        private sealed class Runner : MonoBehaviour
        {
            private float m_tickAt;
            private void Update() { long pf = FiresCore.Diagnostics.CompanionSystemsProfile.Begin(); try { Tick0(); } finally { FiresCore.Diagnostics.CompanionSystemsProfile.End("rebuild runner", pf); } }
            private void Tick0()
            {
                if (s_job != null) { Drive(); return; }
                if (Time.time < m_tickAt) return;
                m_tickAt = Time.time + 2f;
                if (ZNet.instance == null || !ZNet.instance.IsServer() || !HomesteadDefence.On) return;
                Load();
                if (s_queue.Count == 0 || !HomesteadDefence.QuietForRepairs) return;
                Next();
            }
        }

        private static readonly int s_blockMask = LayerMask.GetMask("piece", "piece_nonsolid", "static_solid", "Default", "Default_small", "vehicle");

        private static void Next()
        {
            foreach (var e in s_queue.OrderBy(q => q.Pos.y))
            {
                if (Time.time < e.RetryAt) continue;
                var prefab = ZNetScene.instance?.GetPrefab(e.Prefab);
                if (prefab == null) continue;
                // blocked: anything solid at the spot now (Fire: "never rebuilding where something now blocks the spot")
                var hit = Physics.OverlapSphere(e.Pos + e.Rot * Vector3.up * 0.5f, 0.35f, s_blockMask, QueryTriggerInteraction.Ignore).FirstOrDefault(col => col != null && col.GetComponentInParent<Character>() == null);
                if (hit != null)
                {
                    e.Tries++; e.RetryAt = Time.time + BlockedRetry;
                    FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] skipped {e.Prefab} at {RouteMemory.At(e.Pos)}: blocked by {Utils.GetPrefabName(hit.transform.root.gameObject)}{(e.Tries >= 3 ? " - dropped after 3 tries" : $" (try {e.Tries}/3)")}");
                    if (e.Tries >= 3) { s_queue.Remove(e); Save(); }
                    return;
                }
                var cost = new List<(string, string, int)>();
                foreach (var r in prefab.GetComponent<Piece>().m_resources)
                    if (r?.m_resItem != null && r.m_amount > 0) cost.Add((r.m_resItem.gameObject.name, r.m_resItem.m_itemData.m_shared.m_name, r.m_amount));
                var c = Worker(e.Pos);
                if (c == null) return;   // nobody free here: try later
                var missing = cost.Where(x => FiresCore.Npc.Core.ContainerRegistry.GetTotalItemCount(e.Pos, x.Item1, HomeRange) + InBag(c, x.Item1) < x.Item3).ToList();
                if (missing.Count > 0)
                {
                    e.RetryAt = Time.time + NoMaterialsRetry;
                    FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] skipped {e.Prefab} at {RouteMemory.At(e.Pos)}: no materials (needs {string.Join(", ", missing.Select(x => $"{x.Item1} x{x.Item3}"))}); retry in {NoMaterialsRetry / 60f:0} min");
                    return;
                }
                s_job = new Job { E = e, C = c, Cost = cost, Step = Step.ToChest, StepAt = Time.time };
                FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] {c.companionName} starts {e.Prefab} at {RouteMemory.At(e.Pos)} (cost {string.Join(", ", cost.Select(x => $"{x.Item1} x{x.Item3}"))})");
                return;
            }
        }

        private static int InBag(CompanionController c, string prefab)
        {
            var bag = c?.GetInventory()?.GetStorageInventory();
            return bag == null ? 0 : bag.GetAllItems().Where(i => i?.m_dropPrefab != null && i.m_dropPrefab.name == prefab).Sum(i => i.m_stack);
        }

        private static CompanionController Worker(Vector3 at)
            => CompanionController.AllCompanions.Where(c => c != null && c.isTamed && !c.ShouldBeFollowing && c.GetCompanionAI() != null && !c.GetCompanionAI().IsInCombat
                    && c.GetComponent<ZNetView>() is ZNetView nv && nv.IsValid() && nv.IsOwner() && Vector3.Distance(c.transform.position, at) < HomeRange)
                .OrderBy(c => Vector3.Distance(c.transform.position, at)).FirstOrDefault();

        private static void Abort(string why)
        {
            if (s_job == null) return;
            try { s_job.C?.GetMovementAuthority()?.ReleaseAuthority("RaidRebuild"); } catch { }
            FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] {s_job.C?.companionName ?? "?"} stopped {s_job.E.Prefab} at {RouteMemory.At(s_job.E.Pos)}: {why}");
            s_job.E.RetryAt = Time.time + BlockedRetry;
            s_job = null;
        }

        private static void Drive()
        {
            var j = s_job; var c = j.C; var ai = c != null ? c.GetCompanionAI() : null;
            if (ai == null) { s_job = null; return; }
            if (HomesteadDefence.AnyAlert) { Abort("a new alert (paused until it is over)"); return; }
            if (Time.time - j.StepAt > 120f) { Abort($"{j.Step} took over 120 s"); return; }
            var src = FiresCore.Npc.Core.UnifiedMovementAuthority.MovementSource.SubBehavior;
            switch (j.Step)
            {
                case Step.ToChest:
                    while (j.CostIdx < j.Cost.Count && InBag(c, j.Cost[j.CostIdx].prefab) >= j.Cost[j.CostIdx].amount) j.CostIdx++;
                    if (j.CostIdx >= j.Cost.Count) { j.Step = Step.ToSpot; j.StepAt = Time.time; return; }
                    if (j.Chest == null) j.Chest = FiresCore.Npc.Core.ContainerRegistry.GetClosestWithItem(c.transform.position, j.Cost[j.CostIdx].prefab, HomeRange);
                    if (j.Chest == null) { Abort($"no chest holds {j.Cost[j.CostIdx].prefab} any more"); return; }
                    Vector3 stand = FiresCore.Npc.Core.InteractionPointHelper.GetContainerInteractionPoint(j.Chest, c.transform.position);
                    bool there = ai.RequestPathfindingMovement(stand, false, 1.5f, src, "RaidRebuild") || FiresCore.Npc.Core.CompanionReach.InInteractReach(c.transform, j.Chest, out _);
                    if (there) { j.Step = Step.Pull; j.StepAt = Time.time; }
                    return;
                case Step.Pull:
                    var need = j.Cost[j.CostIdx];
                    var bag = c.GetInventory()?.GetStorageInventory();
                    if (bag == null) { Abort("no bag"); return; }
                    var res = FiresCore.Npc.Core.InventoryTransferService.PullItem(j.Chest, bag, need.prefab, need.amount - InBag(c, need.prefab));
                    if (res.Success) c.GetInventory()?.SaveToZDO();
                    var term = FiresCore.Npc.Core.InteractionPointHelper.TerminalUsedFor(j.Chest);
                    if (term != null && res.Success) FiresCore.Logging.FiresLogger.LogDebug($"[Chore] {c.companionName} used storage terminal at ({term.transform.position.x:0}, {term.transform.position.z:0}): took {need.prefab} x{res.AmountTransferred} (from chest {RouteMemory.At(j.Chest.transform.position)})");
                    j.Chest = null; j.Step = Step.ToChest; j.StepAt = Time.time;
                    return;
                case Step.ToSpot:
                    Vector3 spot = j.E.Pos;
                    if (ai.RequestPathfindingMovement(spot, false, 3f, src, "RaidRebuild") || Vector3.Distance(c.transform.position, spot) < 4f) { j.Step = Step.Place; j.StepAt = Time.time; }
                    return;
                case Step.Place:
                    Place(j);
                    return;
            }
        }

        private static void Place(Job j)
        {
            var c = j.C; var e = j.E;
            var prefab = ZNetScene.instance?.GetPrefab(e.Prefab);
            var bag = c.GetInventory()?.GetStorageInventory();
            if (prefab == null || bag == null) { Abort("prefab / bag gone"); return; }
            foreach (var x in j.Cost) if (InBag(c, x.prefab) < x.amount) { Abort($"short of {x.prefab}"); return; }
            var go = UnityEngine.Object.Instantiate(prefab, e.Pos, e.Rot);
            var piece = go.GetComponent<Piece>();
            var pnv = go.GetComponent<ZNetView>(); if (piece != null && e.Creator != 0L && pnv != null && pnv.IsValid()) { pnv.GetZDO().Set(ZDOVars.s_creator, e.Creator); piece.m_creator = e.Creator; }   // the original creator kept (wards, ownership)
            if (!string.IsNullOrEmpty(e.Tags) && go.GetComponent<Container>() != null) { var nv = go.GetComponent<ZNetView>(); if (nv != null && nv.IsValid()) nv.GetZDO().Set(FiresCore.Storage.ChestTags.ZdoKey, e.Tags); }
            foreach (var x in j.Cost) bag.RemoveItem(x.shared, x.amount);
            c.GetInventory()?.SaveToZDO();
            try { c.GetMovementAuthority()?.ReleaseAuthority("RaidRebuild"); } catch { }
            RouteMemory.BuildChanged(e.Pos, e.Prefab + " rebuilt");
            s_queue.Remove(e); Save();
            FiresCore.Logging.FiresLogger.LogDebug($"[Rebuild] {c.companionName} rebuilt {e.Prefab} (cost {string.Join(", ", j.Cost.Select(x => $"{x.prefab} x{x.amount}"))}) at {RouteMemory.At(e.Pos)}{(e.Tags.Length > 0 ? ", chest tags restored" : "")}; {s_queue.Count} left");
            s_job = null;
        }
    }
}
