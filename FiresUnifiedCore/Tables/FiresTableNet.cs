using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Tables
{
    /// <summary>One seat at a live table — server truth. Bots hold a seat with Id = -(seat index + 1).</summary>
    public sealed class FiresTableSeat
    {
        public long Id;
        public string Name;
        public bool IsBot;
        public int Seat;
        public float LastSeen;    // server clock; pings + actions refresh it
        internal long Route;      // routed-rpc uid learned from the seat's first ping (0 = not attached yet)
    }

    /// <summary>
    /// A game mod's server-side table brain. Core calls these on the SERVER only. Implementations wrap the actual
    /// rules engine (a PokerGame, a chess board, …), validate every action against the acting player, and describe
    /// the world per viewer — <see cref="BuildState"/> redacts private info (hole cards) at the source, so a
    /// player's secrets never ride inside someone else's packet.
    /// </summary>
    public interface IFiresTableHost
    {
        /// <summary>A seated player sent a game action. Validate against <paramref name="playerId"/> — never trust the payload.</summary>
        void OnAction(long playerId, ZPackage payload);
        /// <summary>A human seat attached (first ping) or hot-joined (cash-table bot swap-in point).</summary>
        void OnJoin(FiresTableSeat seat);
        /// <summary>A human seat left or timed out. The host decides: bot-swap, fold-out, or end the game.</summary>
        void OnLeave(FiresTableSeat seat, bool timedOut);
        /// <summary>Server pump — drive AI turns, phase timers, auto-fold clocks. Call PushState when things change.</summary>
        void Tick(float dt);
        /// <summary>Public snapshot AS SEEN BY <paramref name="viewerId"/>. Redact everything the viewer may not know.</summary>
        ZPackage BuildState(long viewerId);
    }

    /// <summary>
    /// The shared server-authoritative TABLE layer for the whole Fires family — the sibling of
    /// <see cref="FiresCore.Queue.FiresQueueSystem"/> and the missing piece between "queue is ready" and "cards are
    /// dealt". Core owns sessions, seats, transport, per-viewer state fan-out with version stamping, ping/timeout
    /// liveness, and the server pump; game mods implement <see cref="IFiresTableHost"/> and never touch the wire.
    ///
    /// Transport is the BankerNetwork idiom (canonical m_playerID + name in requests — peer UID diverges from player
    /// id on dedicated servers) with one addition: the server can only PUSH to a seat after learning its route, so
    /// seating is a handshake. <see cref="SendInvites"/> broadcasts "you are seated at table X" to everybody; each
    /// invited client calls <see cref="Attach"/>, whose ping teaches the server its routed uid; state then fans out
    /// per seat. A listen host's own ping loops back through the same path — no special-casing.
    /// </summary>
    public static class FiresTableNet
    {
        private const string RpcAct = "FiresCore_TableAct";
        private const string RpcState = "FiresCore_TableState";
        private const string RpcEvt = "FiresCore_TableEvt";
        private const byte CodePing = 0, CodeAction = 1, CodeDetach = 2;

        /// <summary>Core event codes on the evt channel — game mods may extend from 100 up.</summary>
        public const int EvtClosed = 1, EvtInvite = 2, EvtUnseated = 3;

        public sealed class Session
        {
            public string Id;
            public string GameKey;
            public IFiresTableHost Host;
            public readonly List<FiresTableSeat> Seats = new List<FiresTableSeat>();
            public float TimeoutSeconds = 30f;
            public int Version;

            public FiresTableSeat Find(long playerId)
            {
                foreach (var seat in Seats) if (!seat.IsBot && seat.Id == playerId) return seat;
                return null;
            }
        }

        private static readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>();
        private static readonly Dictionary<string, int> _clientVersion = new Dictionary<string, int>();

        /// <summary>CLIENT: a per-viewer state snapshot arrived — (tableId, version, payload).</summary>
        public static event Action<string, int, ZPackage> StateReceived;
        /// <summary>CLIENT: a table event arrived — (tableId, evtCode, payload). Invites use <see cref="TryParseInvite"/>.</summary>
        public static event Action<string, int, ZPackage> EventReceived;

        private static bool _registered;
        private static ZRoutedRpc _registeredOn;
        private static GameObject _pump;
        private static bool IsServer => ZNet.instance != null && ZNet.instance.IsServer();

        public static void Initialize()
        {
            var routedRpc = ZRoutedRpc.instance;
            if (routedRpc == null) return;
            if (_registered && _registeredOn == routedRpc) return;
            routedRpc.Register<ZPackage>(RpcAct, new Action<long, ZPackage>(RPC_Act));
            routedRpc.Register<ZPackage>(RpcState, new Action<long, ZPackage>(RPC_State));
            routedRpc.Register<ZPackage>(RpcEvt, new Action<long, ZPackage>(RPC_Evt));
            _registered = true; _registeredOn = routedRpc;
            _sessions.Clear(); _clientVersion.Clear();
            if (IsServer && _pump == null)
            {
                _pump = new GameObject("FiresTableNetPump") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(_pump);
                _pump.AddComponent<Pump>();
            }
        }

        // ── server API ──────────────────────────────────────────────────────────────────────────────────────────
        public static Session Open(string tableId, string gameKey, IFiresTableHost host, float timeoutSeconds = 30f)
        {
            if (!IsServer || string.IsNullOrEmpty(tableId) || host == null) return null;
            var session = new Session { Id = tableId, GameKey = gameKey ?? "", Host = host, TimeoutSeconds = timeoutSeconds };
            _sessions[tableId] = session;
            return session;
        }

        public static Session Get(string tableId) => tableId != null && _sessions.TryGetValue(tableId, out var session) ? session : null;

        public static void Close(string tableId, string reason = "")
        {
            var session = Get(tableId);
            if (session == null) return;
            _sessions.Remove(tableId);
            var pkg = new ZPackage();
            pkg.Write(reason ?? "");
            foreach (var seat in session.Seats)
                if (!seat.IsBot) SendEventTo(session, seat, EvtClosed, pkg);
        }

        public static FiresTableSeat SeatHuman(Session s, long playerId, string name, int seatIndex)
        {
            if (s == null) return null;
            var seat = new FiresTableSeat { Id = playerId, Name = name ?? "", Seat = seatIndex, LastSeen = Time.realtimeSinceStartup };
            s.Seats.Add(seat);
            return seat;
        }

        public static FiresTableSeat SeatBot(Session s, string name, int seatIndex)
        {
            if (s == null) return null;
            var seat = new FiresTableSeat { Id = -(seatIndex + 1), Name = name ?? "", IsBot = true, Seat = seatIndex };
            s.Seats.Add(seat);
            return seat;
        }

        public static void Unseat(Session s, FiresTableSeat seat, bool timedOut)
        {
            if (s == null || seat == null || !s.Seats.Remove(seat)) return;
            if (!seat.IsBot) SendEventTo(s, seat, EvtUnseated, new ZPackage());
            try { s.Host.OnLeave(seat, timedOut); }
            catch (Exception ex) { FiresCore.Logging.FiresLogger.LogWarning($"[FiresTableNet] OnLeave '{s.Id}': {ex.Message}"); }
        }

        /// <summary>Broadcast "you are seated" to the invited players (public info — ids/names only). Each invited
        /// client answers with <see cref="Attach"/>, which teaches the server its route; pushes start then.</summary>
        public static void SendInvites(Session s)
        {
            if (s == null || ZRoutedRpc.instance == null) return;
            var inner = new ZPackage();
            inner.Write(s.GameKey ?? "");
            int humans = 0;
            foreach (var seat in s.Seats) if (!seat.IsBot) humans++;
            inner.Write(humans);
            foreach (var seat in s.Seats)
                if (!seat.IsBot) { inner.Write(seat.Id); inner.Write(seat.Name ?? ""); inner.Write(seat.Seat); }

            var pkg = new ZPackage();
            pkg.Write(s.Id);
            pkg.Write(EvtInvite);
            pkg.Write(inner.GetArray());
            ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, RpcEvt, pkg);   // delivers locally too (listen host)
        }

        /// <summary>Fan the current state out — one redacted snapshot per ATTACHED human seat, version-stamped.</summary>
        public static void PushState(Session s)
        {
            if (s == null || ZRoutedRpc.instance == null) return;
            s.Version++;
            foreach (var seat in s.Seats)
            {
                if (seat.IsBot || seat.Route == 0L) continue;
                ZPackage state;
                try { state = s.Host.BuildState(seat.Id); }
                catch (Exception ex) { FiresCore.Logging.FiresLogger.LogWarning($"[FiresTableNet] BuildState '{s.Id}': {ex.Message}"); continue; }
                if (state == null) continue;
                var pkg = new ZPackage();
                pkg.Write(s.Id);
                pkg.Write(s.Version);
                pkg.Write(state.GetArray());
                ZRoutedRpc.instance.InvokeRoutedRPC(seat.Route, RpcState, pkg);
            }
        }

        public static void SendEventTo(Session s, FiresTableSeat seat, int code, ZPackage payload)
        {
            if (s == null || seat == null || seat.IsBot || seat.Route == 0L || ZRoutedRpc.instance == null) return;
            var pkg = new ZPackage();
            pkg.Write(s.Id);
            pkg.Write(code);
            pkg.Write(payload != null ? payload.GetArray() : new byte[0]);
            ZRoutedRpc.instance.InvokeRoutedRPC(seat.Route, RpcEvt, pkg);
        }

        public static void SendEventAll(Session s, int code, ZPackage payload)
        {
            if (s == null) return;
            foreach (var seat in s.Seats) SendEventTo(s, seat, code, payload);
        }

        // ── client API ──────────────────────────────────────────────────────────────────────────────────────────
        /// <summary>Answer an invite / (re)connect to a table: the ping teaches the server this client's route.</summary>
        public static void Attach(string tableId) => SendWrapped(tableId, CodePing, null);
        /// <summary>Keep the seat alive while a table UI (or dock chip) is open — call every few seconds.</summary>
        public static void Ping(string tableId) => SendWrapped(tableId, CodePing, null);
        /// <summary>Leave the table deliberately (the host decides what that means mid-hand).</summary>
        public static void Detach(string tableId) => SendWrapped(tableId, CodeDetach, null);
        public static void SendAction(string tableId, ZPackage payload) => SendWrapped(tableId, CodeAction, payload);

        /// <summary>Parse an <see cref="EvtInvite"/> payload → (gameKey, [id, name, seatIndex]). True if well-formed.</summary>
        public static bool TryParseInvite(ZPackage payload, out string gameKey, out List<FiresTableSeat> seats)
        {
            gameKey = null; seats = null;
            try
            {
                gameKey = payload.ReadString();
                int count = payload.ReadInt();
                seats = new List<FiresTableSeat>(count);
                for (int i = 0; i < count; i++)
                    seats.Add(new FiresTableSeat { Id = payload.ReadLong(), Name = payload.ReadString(), Seat = payload.ReadInt() });
                return true;
            }
            catch { return false; }
        }

        private static void SendWrapped(string tableId, byte code, ZPackage payload)
        {
            if (ZRoutedRpc.instance == null || string.IsNullOrEmpty(tableId)) return;
            var local = Player.m_localPlayer;
            if (local == null) return;
            var pkg = new ZPackage();
            pkg.Write(tableId);
            pkg.Write(local.GetPlayerID());
            pkg.Write(local.GetPlayerName() ?? "");
            pkg.Write(code);
            pkg.Write(payload != null ? payload.GetArray() : new byte[0]);
            ZRoutedRpc.instance.InvokeRoutedRPC(0L, RpcAct, pkg);
        }

        // ── wire handlers ───────────────────────────────────────────────────────────────────────────────────────
        private static void RPC_Act(long sender, ZPackage pkg)
        {
            if (!IsServer || pkg == null) return;
            try
            {
                string tableId = pkg.ReadString();
                long playerId = pkg.ReadLong();
                pkg.ReadString();   // name — carried for idiom parity; seats already know it
                byte code = pkg.ReadByte();
                byte[] inner = pkg.ReadByteArray();

                var session = Get(tableId);
                var seat = session?.Find(playerId);
                if (session == null || seat == null) return;

                seat.LastSeen = Time.realtimeSinceStartup;
                bool firstAttach = seat.Route == 0L;
                seat.Route = sender;   // the routed uid we can push to — refreshed every message (reconnects)

                if (firstAttach)
                {
                    try { session.Host.OnJoin(seat); }
                    catch (Exception ex) { FiresCore.Logging.FiresLogger.LogWarning($"[FiresTableNet] OnJoin '{session.Id}': {ex.Message}"); }
                }

                if (code == CodeDetach) Unseat(session, seat, timedOut: false);
                else if (code == CodeAction) session.Host.OnAction(playerId, new ZPackage(inner));
            }
            catch (Exception ex) { FiresCore.Logging.FiresLogger.LogWarning($"[FiresTableNet] act: {ex.Message}"); }
        }

        private static void RPC_State(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            try
            {
                string tableId = pkg.ReadString();
                int version = pkg.ReadInt();
                byte[] inner = pkg.ReadByteArray();
                if (_clientVersion.TryGetValue(tableId, out int seen) && version <= seen) return;
                _clientVersion[tableId] = version;
                StateReceived?.Invoke(tableId, version, new ZPackage(inner));
            }
            catch (Exception ex) { FiresCore.Logging.FiresLogger.LogWarning($"[FiresTableNet] state: {ex.Message}"); }
        }

        private static void RPC_Evt(long sender, ZPackage pkg)
        {
            if (pkg == null) return;
            try
            {
                string tableId = pkg.ReadString();
                int code = pkg.ReadInt();
                byte[] inner = pkg.ReadByteArray();
                EventReceived?.Invoke(tableId, code, new ZPackage(inner));
            }
            catch (Exception ex) { FiresCore.Logging.FiresLogger.LogWarning($"[FiresTableNet] evt: {ex.Message}"); }
        }

        // ── server pump — tick hosts + retire silent seats ──────────────────────────────────────────────────────
        private sealed class Pump : MonoBehaviour
        {
            private readonly List<Session> _work = new List<Session>();
            private readonly List<FiresTableSeat> _stale = new List<FiresTableSeat>();

            private void Update()
            {
                if (_sessions.Count == 0) return;
                _work.Clear();
                _work.AddRange(_sessions.Values);
                float now = Time.realtimeSinceStartup;
                foreach (var session in _work)
                {
                    _stale.Clear();
                    foreach (var seat in session.Seats)
                        if (!seat.IsBot && seat.Route != 0L && now - seat.LastSeen > session.TimeoutSeconds) _stale.Add(seat);
                    foreach (var seat in _stale) Unseat(session, seat, timedOut: true);

                    try { session.Host.Tick(Time.deltaTime); }
                    catch (Exception ex) { FiresCore.Logging.FiresLogger.LogWarning($"[FiresTableNet] tick '{session.Id}': {ex.Message}"); }
                }
            }
        }

        // Self-register on world load (client + server) — the FiresQueueSystem idiom.
        [HarmonyPatch(typeof(Game), "Start")]
        private static class Game_Start_Init
        {
            private static void Postfix() { try { Initialize(); } catch { } }
        }
    }
}
