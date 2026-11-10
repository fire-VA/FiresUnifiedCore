using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;

namespace FiresCore.Activity
{
    /// <summary>
    /// CoreActivityApi on the wire.
    ///   "FUC_ActivityOn"  server -> clients: bool, a receiver (Studio Link) is listening. Sent on SetServerReceiver and to a joining peer
    ///                     that asks with "FUC_ActivityAsk" (rate-capped). Clients ignore any sender but the server.
    ///   "FUC_Activity"    client -> server: v1, count, then count x (kind, json). One package per flush (5 s or 32 rows), never one per row.
    /// Server checks, each failure dropped and counted (Debug summary): sender is a connected peer with a character, package <= 16 KB,
    /// <= 4 packages/s per peer, <= 64 rows, known kind, json one object <= 512 chars, nesting <= 4. Rows reach the receiver with the
    /// server's own peer uid + player name. On a listen host the local player's rows go straight to the receiver.
    /// </summary>
    internal static class ActivityRpc
    {
        private const string DataRpc = "FUC_Activity", OnRpc = "FUC_ActivityOn", AskRpc = "FUC_ActivityAsk", Tag = "[Activity] ";
        private const int QueueMax = 256, FlushRows = 32, MaxRowsPerPackage = 64, MaxPackageBytes = 16 * 1024;
        private const float FlushSeconds = 5f, SummarySeconds = 60f;

        internal static bool ServerListening;
        private static readonly Queue<KeyValuePair<string, string>> s_queue = new Queue<KeyValuePair<string, string>>();
        private static readonly Dictionary<string, int> s_queuedByKind = new Dictionary<string, int>();
        private static readonly Dictionary<long, (double windowStart, int count)> s_rate = new Dictionary<long, (double, int)>();
        private static readonly Dictionary<long, DateTime> s_lastAsk = new Dictionary<long, DateTime>();
        private static readonly HashSet<string> s_kinds = new HashSet<string>(CoreActivityApi.Kinds);
        private static float s_nextFlush, s_nextSummary;
        private static int s_dropped, s_rejected, s_rowsIn, s_flushes;
        private static double s_flushMsMax, s_recvMsMax;
        private static bool s_idleLogged, s_activeLogged;

        internal static void Enqueue(string kind, string json)
        {
            if (s_queue.Count == 0) s_nextFlush = UnityEngine.Time.unscaledTime + FlushSeconds;   // the first row starts the 5 s clock
            if (s_queue.Count >= QueueMax) { s_queue.Dequeue(); s_dropped++; }
            s_queue.Enqueue(new KeyValuePair<string, string>(kind, json));
            s_queuedByKind[kind] = s_queuedByKind.TryGetValue(kind, out int n) ? n + 1 : 1;
            if (s_queue.Count >= FlushRows) s_nextFlush = 0f;
        }

        // ---- client -> server ----

        private static void Flush()
        {
            if (s_queue.Count == 0 || ZRoutedRpc.instance == null || ZNet.instance == null) return;
            var sw = Stopwatch.StartNew();
            if (ZNet.instance.IsServer())
            {
                // listen host: the local player's rows go straight to the receiver
                var r = CoreActivityApi.Receiver;
                string name = Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerName() : "";
                long uid = ZNet.GetUID();
                while (s_queue.Count > 0) { var kv = s_queue.Dequeue(); try { r?.Invoke(uid, name, kv.Key, kv.Value); } catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug(Tag + "receiver threw: " + e.Message); } }
            }
            else
            {
                while (s_queue.Count > 0)
                {
                    var p = new ZPackage();
                    p.Write(1);
                    int n = Math.Min(MaxRowsPerPackage, s_queue.Count);
                    p.Write(n);
                    for (int i = 0; i < n; i++) { var kv = s_queue.Dequeue(); p.Write(kv.Key); p.Write(kv.Value); }
                    ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), DataRpc, p);
                }
            }
            sw.Stop();
            s_flushes++;
            s_flushMsMax = Math.Max(s_flushMsMax, sw.Elapsed.TotalMilliseconds);
            FiresCore.Logging.FiresLogger.LogDebug(Tag + "flush " + Counts() + " in " + sw.Elapsed.TotalMilliseconds.ToString("0.000") + " ms");
            s_queuedByKind.Clear();
        }

        private static string Counts()
        {
            var parts = new List<string>();
            foreach (var kv in s_queuedByKind) parts.Add(kv.Key + "=" + kv.Value);
            return parts.Count == 0 ? "(none)" : string.Join(" ", parts.ToArray());
        }

        // ---- server ----

        private static void RPC_Data(long sender, ZPackage p)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
            var sw = Stopwatch.StartNew();
            try
            {
                var receiver = CoreActivityApi.Receiver;
                if (receiver == null) return;                                                       // nobody listening: ignore
                var peer = ZNet.instance.GetPeer(sender);
                if (peer == null || peer.m_characterID.IsNone()) { s_rejected++; return; }          // a connected peer with a character only
                if (p.Size() > MaxPackageBytes) { s_rejected++; return; }
                double now = UnityEngine.Time.unscaledTimeAsDouble;
                s_rate.TryGetValue(sender, out var w);
                if (now - w.windowStart >= 1.0) w = (now, 0);
                if (++w.count > 4) { s_rate[sender] = w; s_rejected++; return; }                    // 4 packages a second per peer
                s_rate[sender] = w;
                if (p.ReadInt() != 1) { s_rejected++; return; }
                int n = p.ReadInt();
                if (n < 0 || n > MaxRowsPerPackage) { s_rejected++; return; }
                for (int i = 0; i < n; i++)
                {
                    string kind = p.ReadString(), json = p.ReadString();
                    if (!s_kinds.Contains(kind) || !JsonShapeOk(json)) { s_rejected++; continue; }
                    s_rowsIn++;
                    receiver(sender, peer.m_playerName ?? "", kind, json);
                }
            }
            catch (Exception e) { s_rejected++; FiresCore.Logging.FiresLogger.LogDebug(Tag + "bad package from " + sender + ": " + e.Message); }
            finally { sw.Stop(); s_recvMsMax = Math.Max(s_recvMsMax, sw.Elapsed.TotalMilliseconds); }
        }

        // one JSON object, <= 512 chars, nesting <= 4 (strings skipped); the receiver parses it properly
        private static bool JsonShapeOk(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > CoreActivityApi.MaxJson || json[0] != '{' || json[json.Length - 1] != '}') return false;
            int depth = 0; bool str = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (str) { if (c == '\\') i++; else if (c == '"') str = false; continue; }
                if (c == '"') str = true;
                else if (c == '{' || c == '[') { if (++depth > 4) return false; }
                else if (c == '}' || c == ']') { if (--depth < 0) return false; }
            }
            return depth == 0 && !str;
        }

        internal static void Announce()
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || ZRoutedRpc.instance == null) return;
            ServerListening = CoreActivityApi.Receiver != null;                                      // the listen host's own player
            var p = new ZPackage(); p.Write(ServerListening);
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, OnRpc, p);
            LogState("receiver " + (ServerListening ? "set" : "cleared") + " on this server");
        }

        private static void RPC_On(long sender, ZPackage p)
        {
            if (ZNet.instance == null || ZNet.instance.IsServer() || sender != ZRoutedRpc.instance.GetServerPeerID()) return;
            try { ServerListening = p.ReadBool(); } catch { return; }
            if (!ServerListening) s_queue.Clear();
            LogState("from the server");
        }

        private static void RPC_Ask(long sender, ZPackage p)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || CoreActivityApi.Receiver == null) return;
            if (s_lastAsk.TryGetValue(sender, out var t) && (DateTime.UtcNow - t).TotalSeconds < 5) return;
            s_lastAsk[sender] = DateTime.UtcNow;
            var a = new ZPackage(); a.Write(true);
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, OnRpc, a);
        }

        private static void LogState(string why)
        {
            if (ServerListening && !s_activeLogged) { s_activeLogged = true; FiresCore.Logging.FiresLogger.LogInfo(Tag + "activity reporting active: the server's Studio Link is listening (" + why + ")"); }
            else if (!ServerListening && !s_idleLogged) { s_idleLogged = true; FiresCore.Logging.FiresLogger.LogDebug(Tag + "no Studio Link flag from server, activity reporting idle (" + why + ")"); }
            else FiresCore.Logging.FiresLogger.LogDebug(Tag + "listening=" + ServerListening + " (" + why + ")");
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_Activity
        {
            private static void Postfix()
            {
                ServerListening = false; s_queue.Clear(); s_queuedByKind.Clear(); s_rate.Clear(); s_lastAsk.Clear();
                if (ZRoutedRpc.instance == null) return;
                ZRoutedRpc.instance.Register<ZPackage>(DataRpc, RPC_Data);
                ZRoutedRpc.instance.Register<ZPackage>(OnRpc, RPC_On);
                ZRoutedRpc.instance.Register<ZPackage>(AskRpc, RPC_Ask);
                if (ZNet.instance != null && ZNet.instance.IsServer()) ServerListening = CoreActivityApi.Receiver != null;
                if (!ServerListening) LogState("no announce yet");
            }
        }

        [HarmonyPatch(typeof(Player), "OnSpawned")]
        private static class Player_OnSpawned_ActivityAsk
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || ZNet.instance == null || ZNet.instance.IsServer() || ZRoutedRpc.instance == null) return;
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), AskRpc, new ZPackage());
            }
        }

        [HarmonyPatch(typeof(ZNet), "Update")]
        private static class ZNet_Update_ActivityFlush
        {
            private static void Postfix()
            {
                float now = UnityEngine.Time.unscaledTime;
                if (now >= s_nextSummary)
                {
                    s_nextSummary = now + SummarySeconds;
                    if (s_flushes > 0 || s_rowsIn > 0 || s_dropped > 0 || s_rejected > 0)
                        FiresCore.Logging.FiresLogger.LogDebug(Tag + "60 s: flushes " + s_flushes + " (max " + s_flushMsMax.ToString("0.000") + " ms), rows in " + s_rowsIn
                            + " (max " + s_recvMsMax.ToString("0.000") + " ms a package), dropped " + s_dropped + ", rejected " + s_rejected);
                    s_flushes = s_rowsIn = s_dropped = s_rejected = 0; s_flushMsMax = s_recvMsMax = 0;
                    ActivityHooks.FlushSkills();
                }
                if (!ServerListening || s_queue.Count == 0 || now < s_nextFlush) return;   // inert unless rows wait
                s_nextFlush = now + FlushSeconds;
                Flush();
            }
        }
    }
}
