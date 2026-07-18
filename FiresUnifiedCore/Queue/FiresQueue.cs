using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Queue
{
    /// <summary>One player in a queue roster (server truth + client mirror).</summary>
    public sealed class FiresQueueMember
    {
        public long Id;
        public string Name;
        public bool Ready;
    }

    /// <summary>
    /// A mod's queue configuration — registered once via <see cref="FiresQueueSystem.Register"/>. Everything the shared
    /// lobby needs to present and run one kind of queue: its id, the window/button copy, seat bounds, and the
    /// SERVER-side <see cref="OnReady"/> callback fired when a viable group has all readied (the mod starts its match
    /// there). Register the SAME def on client and server (clients need the labels; the server needs the callback).
    /// </summary>
    public sealed class FiresQueueDef
    {
        public string Id;
        public string Title = "Queue";
        public string Description = "";
        public int MinPlayers = 2;
        public int MaxPlayers = 8;
        public string JoinLabel = "Join Queue";
        public string ReadyLabel = "Ready Up";
        public string CancelReadyLabel = "Cancel Ready";
        public string LeaveLabel = "Leave Queue";

        /// <summary>SERVER-ONLY. Fired when the roster has ≥ <see cref="MinPlayers"/> and everyone is ready. Return
        /// true to CONSUME the group (they're cleared from the queue and the roster re-broadcast — do this once the
        /// mod has seated them into its match); false to keep them waiting.</summary>
        public Func<IReadOnlyList<FiresQueueMember>, bool> OnReady;
    }

    /// <summary>
    /// Shared, reusable matchmaking queue for the whole Fires family. Any mod registers a <see cref="FiresQueueDef"/>
    /// and opens the shared lobby (FiresQueueLobby); this class owns the server-authoritative roster and the network
    /// transport (ZPackage over ZRoutedRpc, wrapping the canonical m_playerID/name because peer UID diverges from it
    /// on dedicated servers — the BankerNetwork idiom). One protocol, many queues, keyed by def id. RPCs self-register
    /// on world load via a Game.Start postfix, so mods only ever call Register + the client API.
    /// </summary>
    public static class FiresQueueSystem
    {
        private const string RpcReq = "FiresCore_QueueReq";
        private const string RpcState = "FiresCore_QueueState";
        private enum Op : byte { Join = 1, Leave = 2, Ready = 3, Unready = 4 }

        private static readonly Dictionary<string, FiresQueueDef> _defs = new Dictionary<string, FiresQueueDef>();
        private static readonly Dictionary<string, List<FiresQueueMember>> _server = new Dictionary<string, List<FiresQueueMember>>();
        private static readonly Dictionary<string, List<FiresQueueMember>> _client = new Dictionary<string, List<FiresQueueMember>>();
        private static readonly List<FiresQueueMember> Empty = new List<FiresQueueMember>();

        /// <summary>Fires (client-side) when a queue's roster changes — arg is the queue id. The lobby redraws from it.</summary>
        public static event Action<string> StateChanged;

        private static bool _registered;
        private static ZRoutedRpc _registeredOn;
        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        // ── registration ────────────────────────────────────────────────────────────────────────────────────────
        public static void Register(FiresQueueDef def)
        {
            if (def == null || string.IsNullOrEmpty(def.Id)) return;
            _defs[def.Id] = def;
        }

        public static FiresQueueDef GetDef(string id) => id != null && _defs.TryGetValue(id, out var d) ? d : null;

        public static void Initialize()
        {
            var cur = ZRoutedRpc.instance;
            if (cur == null) return;
            if (_registered && _registeredOn == cur) return;
            cur.Register<ZPackage>(RpcReq, new Action<long, ZPackage>(RPC_Req));
            cur.Register<ZPackage>(RpcState, new Action<long, ZPackage>(RPC_State));
            _registered = true; _registeredOn = cur;
            _server.Clear(); _client.Clear();   // new world/server → fresh rosters
        }

        // ── client API ──────────────────────────────────────────────────────────────────────────────────────────
        public static IReadOnlyList<FiresQueueMember> Roster(string queueId) => _client.TryGetValue(queueId, out var l) ? l : Empty;
        public static void Join(string queueId) => Send(queueId, Op.Join);
        public static void Leave(string queueId) => Send(queueId, Op.Leave);
        public static void SetReady(string queueId, bool ready) => Send(queueId, ready ? Op.Ready : Op.Unready);

        public static bool LocalInQueue(string queueId)
        {
            long me = LocalId();
            if (me == 0L) return false;
            foreach (var m in Roster(queueId)) if (m.Id == me) return true;
            return false;
        }

        public static bool LocalReady(string queueId)
        {
            long me = LocalId();
            foreach (var m in Roster(queueId)) if (m.Id == me) return m.Ready;
            return false;
        }

        private static long LocalId() => Player.m_localPlayer != null ? Player.m_localPlayer.GetPlayerID() : 0L;

        private static void Send(string queueId, Op op)
        {
            if (ZRoutedRpc.instance == null || string.IsNullOrEmpty(queueId)) return;
            var local = Player.m_localPlayer;
            if (local == null) return;
            long id = local.GetPlayerID();
            if (id == 0L) return;
            var pkg = new ZPackage();
            pkg.Write(queueId);
            pkg.Write(id);
            pkg.Write(local.GetPlayerName() ?? "");
            pkg.Write((byte)op);
            ZRoutedRpc.instance.InvokeRoutedRPC(0L, RpcReq, pkg);
        }

        // ── server ──────────────────────────────────────────────────────────────────────────────────────────────
        private static void RPC_Req(long sender, ZPackage pkg)
        {
            if (!IsServer || pkg == null) return;
            try
            {
                string queueId = pkg.ReadString();
                long id = pkg.ReadLong();
                string name = pkg.ReadString();
                var op = (Op)pkg.ReadByte();

                var def = GetDef(queueId);
                if (def == null) return;
                if (!_server.TryGetValue(queueId, out var list)) { list = new List<FiresQueueMember>(); _server[queueId] = list; }
                var mem = list.Find(m => m.Id == id);

                switch (op)
                {
                    case Op.Join:
                        if (mem == null && list.Count < def.MaxPlayers) list.Add(new FiresQueueMember { Id = id, Name = name });
                        else if (mem != null) mem.Name = name;
                        break;
                    case Op.Leave: if (mem != null) list.Remove(mem); break;
                    case Op.Ready: if (mem != null) mem.Ready = true; break;
                    case Op.Unready: if (mem != null) mem.Ready = false; break;
                }

                Broadcast(queueId);
                TryReady(def, queueId, list);
            }
            catch (Exception ex) { FiresCore.Logging.FiresLogger.LogWarning($"[FiresQueue] req: {ex.Message}"); }
        }

        private static void TryReady(FiresQueueDef def, string queueId, List<FiresQueueMember> list)
        {
            if (def.OnReady == null || list.Count < def.MinPlayers) return;
            foreach (var m in list) if (!m.Ready) return;
            bool consume;
            try { consume = def.OnReady(new List<FiresQueueMember>(list)); }
            catch (Exception ex) { FiresCore.Logging.FiresLogger.LogWarning($"[FiresQueue] OnReady '{queueId}': {ex.Message}"); return; }
            if (consume) { list.Clear(); Broadcast(queueId); }
        }

        private static void Broadcast(string queueId)
        {
            var list = _server.TryGetValue(queueId, out var l) ? l : Empty;
            var pkg = new ZPackage();
            pkg.Write(queueId ?? "");
            pkg.Write(list.Count);
            foreach (var m in list)
            {
                pkg.Write(m.Id);
                pkg.Write(m.Name ?? "");
                pkg.Write((byte)(m.Ready ? 1 : 0));
            }
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcState, pkg);
        }

        // ── client ──────────────────────────────────────────────────────────────────────────────────────────────
        private static void RPC_State(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            try
            {
                string queueId = pkg.ReadString();
                int n = pkg.ReadInt();
                var list = new List<FiresQueueMember>(n);
                for (int i = 0; i < n; i++)
                    list.Add(new FiresQueueMember { Id = pkg.ReadLong(), Name = pkg.ReadString(), Ready = pkg.ReadByte() != 0 });
                _client[queueId] = list;
                StateChanged?.Invoke(queueId);
            }
            catch (Exception ex) { FiresCore.Logging.FiresLogger.LogWarning($"[FiresQueue] state: {ex.Message}"); }
        }

        // Self-register the routes on world load (client + server), so mods only touch Register + the client API.
        [HarmonyPatch(typeof(Game), "Start")]
        private static class Game_Start_Init
        {
            private static void Postfix() { try { Initialize(); } catch { } }
        }
    }
}
