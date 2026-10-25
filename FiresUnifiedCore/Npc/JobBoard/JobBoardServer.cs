using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Npc.JobBoard
{
    /// <summary>
    /// Daily jobs while the owner is offline (Fire 10-24 keep-alive follow-up: "daily jobs run while the owner is offline"). The client
    /// pushes its DAILY jobs (with its base center) to the server on load and every save. The server checks them - the sender is that
    /// player, at most 32 jobs, known kinds, times 0-23:59, trips with radius 30-100 m and a center within [Trips] Max range of the base -
    /// and keeps its own copy per owner (BepInEx\cache\FiresUnifiedCore\JobBoardServer\&lt;world&gt;.txt). While that owner is offline, the
    /// server runs due daily jobs on the owner's companions it simulates itself (FGN keep-alive) at that base, through the same
    /// JobBoard.StartOn; its last-run days go back to the owner on the next push, so a job never runs twice in a day.
    /// </summary>
    internal static class JobBoardServer
    {
        private const string PushRpc = "FiresCore_JobBoardPush", RunsRpc = "FiresCore_JobBoardRuns";
        private const int MaxJobs = 32;
        private sealed class OwnerBoard { public long Owner; public string Name; public Vector3 Base; public bool Verified; public List<JobBoard.Job> Jobs = new List<JobBoard.Job>(); }

        // ---- the base must be real ([lead] 10-24): within 64 m of a ward the player built or a bed they own -----------------------
        // The server indexes wards (creator) and beds (owner) from the ZDOs in the background (vanilla's frame-sliced iterator), so a
        // client can't name any spot as "base" and anchor the trip range there. An unverified board is kept but never run.
        private const float BaseReach = 64f;
        private static readonly string[] AnchorPrefabs = { "guard_stone", "bed", "piece_bed02" };
        private static readonly Dictionary<long, List<Vector3>> s_anchors = new Dictionary<long, List<Vector3>>();
        private static readonly Dictionary<long, List<Vector3>> s_building = new Dictionary<long, List<Vector3>>();
        private static readonly List<ZDO> s_scanBuf = new List<ZDO>();
        private static int s_scanPrefab, s_scanIndex;
        private static bool s_scanDone;

        private static void ScanStep()
        {
            if (ZDOMan.instance == null) return;
            string prefab = AnchorPrefabs[s_scanPrefab];
            s_scanBuf.Clear();
            bool finished = ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefab, s_scanBuf, ref s_scanIndex);
            foreach (var z in s_scanBuf)
            {
                long who = prefab == "guard_stone" ? z.GetLong(ZDOVars.s_creator, 0L) : z.GetLong(ZDOVars.s_owner, 0L);
                if (who == 0L) continue;
                if (!s_building.TryGetValue(who, out var list)) s_building[who] = list = new List<Vector3>();
                list.Add(z.GetPosition());
            }
            if (!finished) return;
            s_scanIndex = 0;
            if (++s_scanPrefab < AnchorPrefabs.Length) return;
            s_scanPrefab = 0;
            s_anchors.Clear();
            foreach (var kv in s_building) s_anchors[kv.Key] = kv.Value;
            s_building.Clear();
            if (!s_scanDone) FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] server: base index ready ({s_anchors.Count} player(s) with a ward or bed)");
            s_scanDone = true;
            bool changed = false;
            foreach (var b in s_boards.Values) { bool v = IsRealBase(b.Owner, b.Base); if (v != b.Verified) { b.Verified = v; changed = true; LogVerify(b); } }
            if (changed) Save();
        }

        private static bool IsRealBase(long owner, Vector3 at) => s_anchors.TryGetValue(owner, out var list) && list.Any(p => Vector3.Distance(p, at) <= BaseReach);

        private static void LogVerify(OwnerBoard b) => FiresCore.Logging.FiresLogger.LogDebug(b.Verified
            ? $"[JobBoard] server: {b.Name}'s base ({b.Base.x:0},{b.Base.z:0}) verified (a ward or bed of theirs within {BaseReach:0} m)"
            : $"[JobBoard] server: {b.Name}'s base ({b.Base.x:0},{b.Base.z:0}) refused: no ward or bed of theirs within {BaseReach:0} m; their daily jobs won't run offline");
        private static readonly Dictionary<long, OwnerBoard> s_boards = new Dictionary<long, OwnerBoard>();
        private static string s_file;

        // ---- client -> server ----------------------------------------------------------------------------------------------
        internal static void Push()
        {
            var p = Player.m_localPlayer;
            if (p == null || ZRoutedRpc.instance == null || ZNet.instance == null) return;
            var daily = JobBoard.Jobs.Where(j => j.Repeat == JobBoard.Repeat.Daily).Take(MaxJobs).ToList();
            var pkg = new ZPackage();
            pkg.Write(JobBoard.BaseCenter());
            pkg.Write(daily.Count);
            foreach (var j in daily) pkg.Write(JobBoard.ToLine(j));
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), PushRpc, pkg);
        }

        private static void RPC_Push(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || pkg == null) return;
            try
            {
                long owner = PlayerIdOf(sender, out string name);
                if (owner == 0L) return;
                Vector3 baseAt = pkg.ReadVector3();
                int n = pkg.ReadInt();
                if (n < 0 || n > MaxJobs) { FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] server: push from {name} refused ({n} jobs, max {MaxJobs})"); return; }
                Load();
                s_boards.TryGetValue(owner, out var old);
                var board = new OwnerBoard { Owner = owner, Name = name, Base = baseAt, Verified = s_scanDone && IsRealBase(owner, baseAt) };
                for (int i = 0; i < n; i++)
                {
                    var j = JobBoard.FromLine(pkg.ReadString());
                    if (j == null || j.Repeat != JobBoard.Repeat.Daily || j.Minute < 0 || j.Minute >= 1440) continue;
                    if (j.IsTrip)
                    {
                        float d = Vector3.Distance(new Vector3(j.Center.x, 0f, j.Center.z), new Vector3(baseAt.x, 0f, baseAt.z));
                        if (j.Radius < 30f || j.Radius > 100f || d > TripBehavior.MaxRange) continue;
                        j.Count = Mathf.Clamp(j.Count, 1, TripBehavior.MaxParty);
                    }
                    var was = old?.Jobs.FirstOrDefault(o => o.Id == j.Id);
                    if (was != null) j.LastRunDay = Mathf.Max(j.LastRunDay, was.LastRunDay);   // the server's runs count too
                    board.Jobs.Add(j);
                }
                s_boards[owner] = board;
                if (s_scanDone) LogVerify(board); else FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] server: {name}'s base waits for the base index");
                Save();
                FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] server: {name} has {board.Jobs.Count} daily job(s) (base {baseAt.x:0},{baseAt.z:0})");
                // tell the owner what the server ran
                var back = new ZPackage();
                back.Write(board.Jobs.Count);
                foreach (var j in board.Jobs) { back.Write(j.Id); back.Write(j.LastRunDay); }
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, RunsRpc, back);
            }
            catch (Exception e) { Debug.LogWarning("[JobBoard] server push: " + e.Message); }
        }

        private static void RPC_Runs(long sender, ZPackage pkg)
        {
            try
            {
                int n = pkg.ReadInt();
                bool changed = false;
                for (int i = 0; i < n && i < MaxJobs; i++)
                {
                    int id = pkg.ReadInt(), day = pkg.ReadInt();
                    var j = JobBoard.Jobs.FirstOrDefault(x => x.Id == id);
                    if (j != null && day > j.LastRunDay) { j.LastRunDay = day; changed = true; }
                }
                if (changed) FiresCore.Logging.FiresLogger.LogDebug("[JobBoard] took the server's last-run days for jobs it ran while you were away");
            }
            catch { }
        }

        private static long PlayerIdOf(long sender, out string name)
        {
            name = sender.ToString();
            if (sender == ZDOMan.GetSessionID() && Player.m_localPlayer != null) { name = Player.m_localPlayer.GetPlayerName(); return Player.m_localPlayer.GetPlayerID(); }
            var peer = ZNet.instance.GetPeer(sender);
            if (peer == null) return 0L;
            name = peer.m_playerName;
            var pz = ZDOMan.instance.GetZDO(peer.m_characterID);
            return pz != null ? pz.GetLong(ZDOVars.s_playerID, 0L) : 0L;
        }

        // ---- the server's own runs (owner offline) ---------------------------------------------------------------------------
        private static void ServerTick()
        {
            if (EnvMan.instance == null) return;
            Load();
            if (s_boards.Count == 0) return;
            var online = new HashSet<long>();
            foreach (var peer in ZNet.instance.GetPeers()) { var pz = ZDOMan.instance.GetZDO(peer.m_characterID); if (pz != null) online.Add(pz.GetLong(ZDOVars.s_playerID, 0L)); }
            if (Player.m_localPlayer != null) online.Add(Player.m_localPlayer.GetPlayerID());
            int today = EnvMan.instance.GetDay();
            int now = Mathf.FloorToInt(EnvMan.instance.GetDayFraction() * 1440f) % 1440;
            bool changed = false;
            foreach (var b in s_boards.Values)
            {
                if (online.Contains(b.Owner) || !b.Verified) continue;   // the owner's own client runs its board; an unverified base never runs
                foreach (var j in b.Jobs)
                {
                    if (j.LastRunDay == today || now < j.Minute) continue;
                    // the owner's companions this server simulates, at that base, free
                    var pool = CompanionController.AllCompanions.Where(c => c != null && c.ownerPlayerId == b.Owner && c.isTamed
                        && c.GetComponent<ZNetView>() is ZNetView nv && nv.IsValid() && nv.IsOwner()
                        && Vector3.Distance(c.transform.position, b.Base) <= 60f
                        && !(c.GetComponent<CompanionIdleBehavior>()?.ActiveSubBehavior is TripBehavior)).ToList();
                    if (j.Assigned.Count > 0) pool = pool.Where(c => j.Assigned.Contains(c.companionId)).ToList();
                    var skill = JobBoard.SkillOf(j.Kind);
                    pool = pool.OrderByDescending(c => skill == Skills.SkillType.None ? 0f : c.GetSkills()?.GetSkillLevel(skill) ?? 0f).ToList();
                    int want = j.IsTrip ? j.Count : 1, started = 0;
                    foreach (var c in pool)
                    {
                        if (started >= want) break;
                        if (JobBoard.StartOn(j, c, out string why)) { started++; FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] server: {JobBoard.Label(j)} for {b.Name} (offline) started on {c.GetDisplayName()}"); }
                        else FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] server: {JobBoard.Label(j)} for {b.Name}: {c.GetDisplayName()} can't ({why})");
                    }
                    if (started > 0 || pool.Count == 0)
                    {
                        j.LastRunDay = today; changed = true;
                        if (pool.Count == 0) FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] server: {JobBoard.Label(j)} for {b.Name} (offline) skipped today: no companion of theirs simulated here at the base");
                    }
                }
            }
            if (changed) Save();
        }

        // ---- storage ---------------------------------------------------------------------------------------------------------
        private static string FileFor()
        {
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
            if (string.IsNullOrEmpty(world)) return null;
            foreach (char ch in Path.GetInvalidFileNameChars()) world = world.Replace(ch, '_');
            return Path.Combine(BepInEx.Paths.BepInExRootPath, "cache", "FiresUnifiedCore", "JobBoardServer", world + ".txt");
        }

        private static void Load()
        {
            string f = FileFor();
            if (f == null || f == s_file) return;
            s_file = f; s_boards.Clear();
            if (!File.Exists(f)) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            OwnerBoard cur = null;
            foreach (string line in File.ReadAllLines(f))
            {
                if (line.StartsWith("#owner\t"))
                {
                    var p = line.Split('\t');
                    if (p.Length < 6) { cur = null; continue; }
                    try { cur = new OwnerBoard { Owner = long.Parse(p[1], inv), Name = p[2], Base = new Vector3(float.Parse(p[3], inv), float.Parse(p[4], inv), float.Parse(p[5], inv)) }; s_boards[cur.Owner] = cur; }
                    catch { cur = null; }
                    continue;
                }
                var j = cur != null ? JobBoard.FromLine(line) : null;
                if (j != null && cur.Jobs.Count < MaxJobs) cur.Jobs.Add(j);
            }
        }

        private static void Save()
        {
            string f = FileFor();
            if (f == null) return;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(f));
                var lines = new List<string>();
                foreach (var b in s_boards.Values)
                {
                    lines.Add(string.Join("\t", "#owner", b.Owner.ToString(inv), (b.Name ?? "").Replace("\t", " "), b.Base.x.ToString("0.#", inv), b.Base.y.ToString("0.#", inv), b.Base.z.ToString("0.#", inv)));
                    lines.AddRange(b.Jobs.Select(JobBoard.ToLine));
                }
                File.WriteAllLines(f, lines.ToArray());
            }
            catch (Exception e) { Debug.LogWarning("[JobBoard] server save: " + e.Message); }
        }

        private sealed class Pump : MonoBehaviour
        {
            private float _next;
            private void Update()
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
                if (Time.time < _next) { if (!s_scanDone) try { ScanStep(); } catch { } return; }
                _next = Time.time + 10f;
                try { for (int i = 0; i < 4; i++) ScanStep(); } catch (Exception e) { Debug.LogWarning("[JobBoard] base index: " + e.Message); }
                try { ServerTick(); } catch (Exception e) { Debug.LogWarning("[JobBoard] server tick: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_JobBoardServer
        {
            private static void Postfix()
            {
                s_file = null; s_boards.Clear(); s_anchors.Clear(); s_building.Clear(); s_scanDone = false; s_scanPrefab = 0; s_scanIndex = 0;
                if (ZRoutedRpc.instance != null) { ZRoutedRpc.instance.Register<ZPackage>(PushRpc, RPC_Push); ZRoutedRpc.instance.Register<ZPackage>(RunsRpc, RPC_Runs); }
                if (UnityEngine.Object.FindObjectOfType<Pump>() == null) { var go = new GameObject("FiresJobBoardServer"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<Pump>(); }
            }
        }
    }
}
