using System;
using System.Collections.Generic;
using HarmonyLib;

namespace FiresCore.Env
{
    /// <summary>
    /// The CoreEnvApi lock on the wire. Server -> clients only: "FUC_EnvLock" carries the whole state (a clear = inactive state); a
    /// client ignores any sender but the server. Clients without Core never register it, so they simply don't get the lock.
    /// Late joiners: a client asks with "FUC_EnvAsk" when its player spawns; the server answers that peer only (rate-capped per peer).
    /// Apply: EnvMan m_debugTimeOfDay / m_debugTime / m_debugEnv, the client's own previous values kept and put back on clear.
    /// </summary>
    public static class EnvLockRpc
    {
        public struct LockState
        {
            public bool Active, TimeLocked, EnvLocked, Paused;
            public float DayFraction;
            public string Env, Owner;
            public DateTime LeaseEnd;
        }

        private const string LockRpc = "FUC_EnvLock", AskRpc = "FUC_EnvAsk", Tag = "[Env] ";
        internal static LockState Current;
        internal static bool Frozen;                       // read by EnvFreezePatch; true only while a pause lock is applied here
        private static bool s_saved, s_prevDebugTime, s_proofLogged;
        private static float s_prevTime;
        private static string s_prevEnv;
        private static readonly Dictionary<long, DateTime> s_lastAsk = new Dictionary<long, DateTime>();

        internal static void SetAndBroadcast(LockState next, string why)
        {
            Current = next;
            Apply(next);
            if (ZRoutedRpc.instance != null) ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.Everybody, LockRpc, Pack(next));
            Log(next, why);
        }

        /// <summary>Server: drop a lock whose lease ran out (called from every API call and once a second from ZNet.Update).</summary>
        internal static void Expire()
        {
            if (Current.Active && DateTime.UtcNow > Current.LeaseEnd) SetAndBroadcast(new LockState(), "lease expired for " + Current.Owner);
        }

        private static ZPackage Pack(LockState s)
        {
            var p = new ZPackage();
            p.Write(1);   // wire version
            p.Write(s.Active); p.Write(s.TimeLocked); p.Write(s.DayFraction); p.Write(s.EnvLocked); p.Write(s.Env ?? ""); p.Write(s.Paused);
            return p;
        }

        private static void RPC_Lock(long sender, ZPackage p)
        {
            try
            {
                if (ZNet.instance == null || ZNet.instance.IsServer()) return;                    // the server applied it already
                if (sender != ZRoutedRpc.instance.GetServerPeerID()) return;                     // server-sent only
                if (p.ReadInt() != 1) return;
                var s = new LockState { Active = p.ReadBool(), TimeLocked = p.ReadBool(), DayFraction = p.ReadSingle(), EnvLocked = p.ReadBool(), Env = p.ReadString(), Paused = p.ReadBool() };
                if (s.DayFraction < 0f || s.DayFraction > 1f || float.IsNaN(s.DayFraction) || (s.Env != null && s.Env.Length > 64)) return;
                Current = s; Apply(s); Log(s, "from the server");
            }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogDebug(Tag + "bad lock package: " + e.Message); }
        }

        private static void RPC_Ask(long sender, ZPackage p)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer() || !Current.Active) return;
            if (s_lastAsk.TryGetValue(sender, out var t) && (DateTime.UtcNow - t).TotalSeconds < 5) return;   // rate cap per peer
            s_lastAsk[sender] = DateTime.UtcNow;
            ZRoutedRpc.instance.InvokeRoutedRPC(sender, LockRpc, Pack(Current));
        }

        private static void Apply(LockState s)
        {
            var em = EnvMan.instance;
            if (em != null)
            {
                if (s.Active && !s_saved) { s_prevDebugTime = em.m_debugTimeOfDay; s_prevTime = em.m_debugTime; s_prevEnv = em.m_debugEnv; s_saved = true; }
                if (s.TimeLocked) { em.m_debugTimeOfDay = true; em.m_debugTime = s.DayFraction; }
                else if (s_saved) { em.m_debugTimeOfDay = s_prevDebugTime; em.m_debugTime = s_prevTime; }
                em.m_debugEnv = s.EnvLocked ? s.Env : (s_saved ? s_prevEnv : em.m_debugEnv);
                if (!s.Active) s_saved = false;
            }
            Frozen = s.Active && s.Paused;
        }

        private static void Log(LockState s, string why)
        {
            string what = s.Active ? $"time {(s.TimeLocked ? s.DayFraction.ToString("0.###") : "free")}, weather {(s.EnvLocked ? s.Env : "free")}, {(s.Paused ? "FROZEN" : "running")}" : "cleared";
            if (s.Active && !s_proofLogged) { s_proofLogged = true; FiresCore.Logging.FiresLogger.LogInfo(Tag + "world controls lock applied (Studio): " + what + " - " + why); }
            else FiresCore.Logging.FiresLogger.LogDebug(Tag + what + " - " + why);
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_EnvLock
        {
            private static void Postfix()
            {
                Current = new LockState(); Frozen = false; s_saved = false; s_lastAsk.Clear();
                if (ZRoutedRpc.instance == null) return;
                ZRoutedRpc.instance.Register<ZPackage>(LockRpc, RPC_Lock);
                ZRoutedRpc.instance.Register<ZPackage>(AskRpc, RPC_Ask);
            }
        }

        [HarmonyPatch(typeof(Player), "OnSpawned")]
        private static class Player_OnSpawned_EnvAsk
        {
            private static void Postfix(Player __instance)
            {
                if (__instance != Player.m_localPlayer || ZNet.instance == null || ZNet.instance.IsServer() || ZRoutedRpc.instance == null) return;
                ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), AskRpc, new ZPackage());
            }
        }

        [HarmonyPatch(typeof(ZNet), "Update")]
        private static class ZNet_Update_EnvLease
        {
            private static float s_next;
            private static void Postfix()
            {
                if (!Current.Active || UnityEngine.Time.unscaledTime < s_next) return;   // inert unless a lock is active
                s_next = UnityEngine.Time.unscaledTime + 1f;
                if (ZNet.instance != null && ZNet.instance.IsServer()) Expire();
            }
        }
    }
}
