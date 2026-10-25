using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Companions keep their own areas alive on the server (Fire 10-24: "if were running serverside simulations the companion can BE its
    /// own player"; "the server wont need to keep multiple companions from one area alive as long as one of them can keep the area
    /// loaded"). With FGN 1.5.75+ (FgnApi.Version >= 4, found by reflection; without it nothing changes):
    ///   SERVER, every 5 s: every tamed, owned companion ZDO (CompanionZdoCensus) gets a priority the server can VERIFY - on a trip the
    ///     server saw start (its own table, fed by the validated TripState RPC; the ZDO's trip line is never trusted for this) = 3, else 1.
    ///     Companions are grouped into AREAS: the best one becomes the anchor and every companion within 2 zones of it rides on it free.
    ///     FGN's cap (Max Companion Keep-Alive Areas) counts areas; each owner gets a fair share = ceil(cap / owners), at least 1.
    ///     Anchors are registered with FgnApi.AddKeepAlive("core.companion.&lt;zdoid&gt;", position) and removed when they drop out.
    ///   EVERY PEER, every 2 s: a companion whose ZDO this peer owns and whose ZDO carries a running trip line resumes that trip here;
    ///     one this peer no longer owns stops its trip quietly (the new owner resumes it). So a trip started by a player keeps going on
    ///     the server after they log off, and comes back to them when they're near again.
    /// </summary>
    public static class CompanionKeepAlive
    {
        private const string TripRpc = "FiresCore_CompanionTripState";
        private const float ZoneSize = 64f;

        // ---- the server's own trip table -------------------------------------------------------------------------------------
        private sealed class TripRow { public long Owner; public Vector3 Center; public float Since; public string Line; }
        private static readonly Dictionary<ZDOID, TripRow> s_trips = new Dictionary<ZDOID, TripRow>();
        private static readonly HashSet<ZDOID> s_noRowLogged = new HashSet<ZDOID>();

        /// <summary>Called by TripBehavior on whoever simulates the companion: start / phase (true) and end (false).</summary>
        public static void NotifyTrip(CompanionController c, bool running, string line)
        {
            var nv = c != null ? c.GetComponent<ZNetView>() : null;
            if (nv == null || !nv.IsValid() || ZRoutedRpc.instance == null) return;
            var pkg = new ZPackage(); pkg.Write(nv.GetZDO().m_uid); pkg.Write(running); pkg.Write(line ?? "");
            if (ZNet.instance != null && ZNet.instance.IsServer()) RPC_Trip(ZDOMan.GetSessionID(), pkg);
            else ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), TripRpc, pkg);
        }

        private static void RPC_Trip(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || pkg == null) return;
            try
            {
                ZDOID uid = pkg.ReadZDOID(); bool running = pkg.ReadBool(); string line = pkg.ReadString();
                ZDO zdo = ZDOMan.instance.GetZDO(uid);
                if (zdo == null) return;
                long owner = zdo.GetLong("companion_owner", 0L);
                // only the companion's simulator may say so (the ZDO's owner peer, or the server itself)
                if (sender != ZDOMan.GetSessionID() && zdo.GetOwner() != sender) { FiresCore.Logging.FiresLogger.LogDebug($"[KeepAlive] trip state from {sender} refused: not the simulator of {uid}"); return; }
                if (!running) { if (s_trips.Remove(uid)) FiresCore.Logging.FiresLogger.LogDebug($"[KeepAlive] trip ended for {uid}"); return; }
                // the server's own copy of the trip, checked: a known kind, radius 30-100 m, center within [Trips] Max range of its home
                if (!ValidLine(line, out Vector3 center, out Vector3 home, out string bad)) { FiresCore.Logging.FiresLogger.LogDebug($"[KeepAlive] trip state for {uid} refused: {bad}"); return; }
                if (owner == 0L || Vector3.Distance(zdo.GetPosition(), center) > TripBehavior.MaxRange + 200f) { FiresCore.Logging.FiresLogger.LogDebug($"[KeepAlive] trip state for {uid} refused (owner {owner}, center {Vector3.Distance(zdo.GetPosition(), center):0} m off)"); return; }
                if (!s_trips.ContainsKey(uid)) FiresCore.Logging.FiresLogger.LogDebug($"[KeepAlive] trip running for {uid} (owner {owner}) at ({center.x:0},{center.z:0})");
                s_trips[uid] = new TripRow { Owner = owner, Center = center, Line = line, Since = s_trips.TryGetValue(uid, out var old) ? old.Since : Time.time };
            }
            catch (Exception e) { Debug.LogWarning("[KeepAlive] trip: " + e.Message); }
        }

        private static bool ValidLine(string line, out Vector3 center, out Vector3 home, out string bad)
        {
            center = home = Vector3.zero; bad = null;
            var p = (line ?? "").Split('|');
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            if (p.Length < 11 || !Enum.TryParse(p[0], out TripBehavior.TripKind _)) { bad = "bad trip line"; return false; }
            try
            {
                center = new Vector3(float.Parse(p[2], ci), float.Parse(p[3], ci), float.Parse(p[4], ci));
                float area = float.Parse(p[5], ci);
                home = new Vector3(float.Parse(p[6], ci), float.Parse(p[7], ci), float.Parse(p[8], ci));
                if (area < 30f || area > 100f) { bad = $"radius {area:0} m"; return false; }
                float d = Vector3.Distance(new Vector3(center.x, 0f, center.z), new Vector3(home.x, 0f, home.z));
                if (d > TripBehavior.MaxRange) { bad = $"center {d:0} m from home (max {TripBehavior.MaxRange:0})"; return false; }
            }
            catch { bad = "unreadable trip line"; return false; }
            return true;
        }

        // ---- FgnApi (reflection, Version >= 4) -------------------------------------------------------------------------------
        private static bool s_apiLooked;
        private static MethodInfo s_add, s_remove, s_state, s_counts;
        private static bool Api()
        {
            if (s_apiLooked) return s_add != null;
            s_apiLooked = true;
            var t = AccessTools.TypeByName("FiresGhettoNetworkMod.Api.FgnApi");
            if (t == null) { FiresCore.Logging.FiresLogger.LogDebug("[KeepAlive] FGN not installed: companions keep areas only through their players (client reports)"); return false; }
            int ver = 0;
            try { var f = AccessTools.Field(t, "Version"); var p = AccessTools.Property(t, "Version"); ver = Convert.ToInt32(f != null ? f.GetValue(null) : p?.GetValue(null)); } catch { }
            if (ver < 4) { FiresCore.Logging.FiresLogger.LogDebug($"[KeepAlive] FgnApi.Version {ver} < 4: no keep-alive API"); return false; }
            s_add = AccessTools.Method(t, "AddKeepAlive", new[] { typeof(string), typeof(Func<Vector3?>) });
            s_remove = AccessTools.Method(t, "RemoveKeepAlive", new[] { typeof(string) });
            s_state = AccessTools.Method(t, "GetKeepAliveState", new[] { typeof(string) });
            s_counts = AccessTools.Method(t, "GetKeepAliveCounts");
            FiresCore.Logging.FiresLogger.LogInfo($"[KeepAlive] FgnApi.Version {ver}: companion keep-alive areas {(s_add != null ? "available" : "MISSING methods")}");
            return s_add != null;
        }

        private static int Cap(out string guard)
        {
            guard = "?";
            if (s_counts == null) return 0;
            var args = new object[] { 0, 0, 0, "" };
            try { s_counts.Invoke(null, args); guard = args[3] as string ?? "?"; return (int)args[2]; } catch { return 0; }
        }

        /// <summary>FGN's view of a companion's keep-alive (for the intent panel); "" without FGN.</summary>
        public static string StateOf(ZDOID uid) { try { return s_state != null ? s_state.Invoke(null, new object[] { Id(uid) }) as string : ""; } catch { return ""; } }

        private static string Id(ZDOID uid) => $"core.companion.{uid.UserID}:{uid.ID}";

        // ---- the intent panel's "Kept alive" line (asked from a client, answered by the server) -------------------------------
        private const string AskRpc = "FiresCore_KeepAliveAsk", TellRpc = "FiresCore_KeepAliveTell";
        private static readonly Dictionary<ZDOID, (string text, float at)> s_told = new Dictionary<ZDOID, (string, float)>();
        private static readonly Dictionary<ZDOID, float> s_asked = new Dictionary<ZDOID, float>();

        /// <summary>Client: the last answer for this companion ("" until one came), asking again every 3 s while it's shown.</summary>
        public static string KeptAliveText(ZDOID uid)
        {
            if (ZRoutedRpc.instance != null && (!s_asked.TryGetValue(uid, out float at) || Time.time - at > 3f))
            {
                s_asked[uid] = Time.time;
                var pkg = new ZPackage(); pkg.Write(uid);
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), AskRpc, pkg);
            }
            return s_told.TryGetValue(uid, out var t) && Time.time - t.at < 15f ? t.text : "";
        }

        private static void RPC_Ask(long sender, ZPackage pkg)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || pkg == null) return;
            try
            {
                ZDOID uid = pkg.ReadZDOID();
                string text;
                if (!Api()) text = "no (FGN keep-alive not available)";
                else
                {
                    string anchorId = Id(uid);
                    string state = StateOf(uid);
                    // a rider: kept by the anchor of its area
                    var rider = s_anchors.Values.FirstOrDefault(a => a.uid != uid && ZDOMan.instance.GetZDO(uid) is ZDO z && ZDOMan.instance.GetZDO(a.uid) is ZDO az
                        && FiresCore.World.RingZones.Within(FiresCore.World.RingZones.ObjectSector(az.GetPosition()), FiresCore.World.RingZones.ObjectSector(z.GetPosition()), 2));   // halo ring: seam-wrapped
                    int cap = Cap(out string guard);
                    var args = new object[] { 0, 0, 0, "" };
                    try { s_counts?.Invoke(null, args); } catch { }
                    string counts = $"{args[0]}/{cap} areas, guard {guard}";
                    if (s_anchors.ContainsKey(anchorId)) text = (state == "active" || state == "covered" ? "yes" : "no") + $" ({state}; anchor, {counts})";
                    else if (rider.name != null) text = $"yes (rides on {rider.name}'s area; {counts})";
                    else text = $"no (not picked: cap or fair share; {counts})";
                }
                var back = new ZPackage(); back.Write(uid); back.Write(text);
                ZRoutedRpc.instance.InvokeRoutedRPC(sender, TellRpc, back);
            }
            catch (Exception e) { Debug.LogWarning("[KeepAlive] ask: " + e.Message); }
        }

        private static void RPC_Tell(long sender, ZPackage pkg)
        {
            try { ZDOID uid = pkg.ReadZDOID(); string text = pkg.ReadString(); s_told[uid] = (text.Length > 160 ? text.Substring(0, 160) : text, Time.time); } catch { }
        }

        // ---- server: pick the areas ------------------------------------------------------------------------------------------
        private static readonly Dictionary<string, (ZDOID uid, string name, int riders)> s_anchors = new Dictionary<string, (ZDOID, string, int)>();
        private static readonly List<ZDO> s_buf = new List<ZDO>();

        private static void ServerTick()
        {
            if (!Api()) return;
            int cap = Cap(out string guard);
            s_buf.Clear();
            CompanionZdoCensus.Collect(s_buf);
            var list = new List<(ZDO zdo, long owner, int prio, float since)>();
            foreach (var z in s_buf)
            {
                if (z == null || !z.GetBool(ZDOVars.s_tamed, false)) continue;
                long owner = z.GetLong("companion_owner", 0L);
                if (owner == 0L) continue;
                bool trip = s_trips.TryGetValue(z.m_uid, out var row);
                list.Add((z, owner, trip ? 3 : 1, trip ? row.Since : float.MaxValue));
            }
            // drop table rows whose companion is gone
            foreach (var k in s_trips.Keys.Where(k => ZDOMan.instance.GetZDO(k) == null).ToList()) s_trips.Remove(k);

            int owners = Mathf.Max(1, list.Select(x => x.owner).Distinct().Count());
            int share = Mathf.Max(1, Mathf.CeilToInt(cap / (float)owners));
            var perOwner = new Dictionary<long, int>();
            var areas = new List<(ZDO anchor, Vector2s zone, int riders)>();
            foreach (var c in list.OrderByDescending(x => x.prio).ThenBy(x => x.since))
            {
                Vector2s zc = FiresCore.World.RingZones.ObjectSector(c.zdo.GetPosition());   // halo ring: object-sector ids (HaloZoneDelta works on these)
                int hit = areas.FindIndex(a => FiresCore.World.RingZones.Within(a.zone, zc, 2));   // halo ring: seam-wrapped (plain |dx|,|dy| <= 2 off the ring)
                if (hit >= 0) { var a = areas[hit]; areas[hit] = (a.anchor, a.zone, a.riders + 1); continue; }   // rides on that area
                if (areas.Count >= cap) continue;
                perOwner.TryGetValue(c.owner, out int used);
                if (used >= share) continue;
                perOwner[c.owner] = used + 1;
                areas.Add((c.zdo, zc, 0));
            }
            var want = new HashSet<string>();
            foreach (var a in areas)
            {
                string id = Id(a.anchor.m_uid); want.Add(id);
                ZDOID uid = a.anchor.m_uid;
                string name = a.anchor.GetString("companion_name", uid.ToString());
                bool changed = !s_anchors.TryGetValue(id, out var was) || was.riders != a.riders;
                if (!s_anchors.ContainsKey(id))
                {
                    Func<Vector3?> where = () => { var z = ZDOMan.instance?.GetZDO(uid); return z != null ? z.GetPosition() : (Vector3?)null; };
                    try { s_add.Invoke(null, new object[] { id, where }); } catch (Exception e) { Debug.LogWarning("[KeepAlive] add: " + e.Message); }
                }
                s_anchors[id] = (uid, name, a.riders);
                if (changed) { Vector3 p = a.anchor.GetPosition(); FiresCore.Logging.FiresLogger.LogDebug($"[KeepAlive] area at ({p.x:0},{p.z:0}): anchor {name}{(s_trips.ContainsKey(uid) ? " (trip)" : "")}, +{a.riders} companion(s) sharing; {areas.Count}/{cap} area(s), share {share} per owner, guard {guard}"); }
            }
            foreach (var id in s_anchors.Keys.Where(k => !want.Contains(k)).ToList())
            {
                try { s_remove.Invoke(null, new object[] { id }); } catch { }
                FiresCore.Logging.FiresLogger.LogDebug($"[KeepAlive] {s_anchors[id].name}: no longer an anchor (moved into another area, dropped out of the cap, or gone)");
                s_anchors.Remove(id);
            }
        }

        // ---- every peer: resume / yield trips by ZDO ownership ----------------------------------------------------------------
        private static void PeerTick()
        {
            foreach (var c in CompanionController.AllCompanions)
            {
                if (c == null) continue;
                var nv = c.GetComponent<ZNetView>();
                var idle = c.GetComponent<CompanionIdleBehavior>();
                if (nv == null || !nv.IsValid() || idle == null) continue;
                var trip = idle.GetSubBehavior<TripBehavior>();
                if (trip == null) continue;
                string saved = nv.GetZDO().GetString(TripBehavior.TripKey, "");
                bool mine = nv.IsOwner();
                bool running = idle.ActiveSubBehavior is TripBehavior;
                if (!mine && running)
                {
                    trip.Yielding = true;   // the new simulator resumes it from the ZDO
                    idle.StopActiveSubBehavior("simulated elsewhere now");
                    FiresCore.Logging.FiresLogger.LogDebug($"[KeepAlive] {c.companionName}: trip handed to its new simulator (owner {nv.GetZDO().GetOwner()})");
                }
                else if (mine && !running && !string.IsNullOrEmpty(saved))
                {
                    // [lead] 10-24: the ZDO line is the owner client's word. The SERVER resumes only from its own validated table.
                    if (ZNet.instance != null && ZNet.instance.IsServer() && !c.IsOwner(Player.m_localPlayer))   // the server simulating someone else's companion (a host's own companions read their ZDO)
                    {
                        if (!s_trips.TryGetValue(nv.GetZDO().m_uid, out var row) || string.IsNullOrEmpty(row.Line))
                        {
                            if (s_noRowLogged.Add(nv.GetZDO().m_uid)) FiresCore.Logging.FiresLogger.LogDebug($"[KeepAlive] {c.companionName}: a trip line on its ZDO but none in the server's table; not resumed here");
                            continue;
                        }
                        saved = row.Line;
                    }
                    if (trip.Resume(saved) && idle.TryStartSubBehavior<TripBehavior>()) { }
                    else FiresCore.Logging.FiresLogger.LogDebug($"[KeepAlive] {c.companionName}: saved trip could not resume yet");
                }
            }
        }

        private sealed class Pump : MonoBehaviour
        {
            private float _server, _peer;
            private void Update()
            {
                try
                {
                    if (ZNet.instance == null) return;
                    if (Time.time >= _peer) { _peer = Time.time + 2f; PeerTick(); }
                    if (ZNet.instance.IsServer() && Time.time >= _server) { _server = Time.time + 5f; ServerTick(); }
                }
                catch (Exception e) { Debug.LogWarning("[KeepAlive] " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_KeepAlive
        {
            private static void Postfix()
            {
                s_trips.Clear(); s_anchors.Clear(); s_apiLooked = false;
                if (ZRoutedRpc.instance != null) { ZRoutedRpc.instance.Register<ZPackage>(TripRpc, RPC_Trip); ZRoutedRpc.instance.Register<ZPackage>(AskRpc, RPC_Ask); ZRoutedRpc.instance.Register<ZPackage>(TellRpc, RPC_Tell); }
                if (UnityEngine.Object.FindObjectOfType<Pump>() == null) { var go = new GameObject("FiresCompanionKeepAlive"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<Pump>(); }
            }
        }
    }
}
