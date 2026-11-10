using System;
using System.Linq;

namespace FiresCore.Env
{
    /// <summary>
    /// World controls for the Studio (Fire 10-29, WP15: "Added to core and make sure that it properly skips if core is not used alongside it").
    /// Add-only contract, called by Studio Link through reflection on the SERVER. Inert unless Lock is called: no config, no patches act.
    ///   Lock merges into the owner's lock (sentinels instead of nullables, for reflection): dayFraction &lt; 0 = leave time as is,
    ///     env null / "" = leave weather as is, pause -1 = leave, 0 = off, 1 = on. Another owner's live lock refuses "locked by &lt;owner&gt;".
    ///   Lease: 120 s; the owner calls Renew every ~30 s. Expiry releases the lock on the server and every client (a crashed Studio
    ///     never leaves the world frozen).
    ///   Clients apply it through EnvLockRpc (server-sent only) to EnvMan's debug time / env; pause = a timescale freeze (EnvFreezePatch).
    ///     The world clock (ZNet time) keeps counting under a freeze ("freeze now, true stop later", Fire 10-29).
    /// </summary>
    public static class CoreEnvApi
    {
        public const int StudioApiVersion = 1;
        public const float LeaseSeconds = 120f;

        public static bool Lock(string owner, float dayFraction, string env, int pause, out string reason)
        {
            reason = "";
            if (!IsServer(out reason)) return false;
            if (string.IsNullOrEmpty(owner) || owner.Length > 128) { reason = "bad owner"; return false; }
            EnvLockRpc.Expire();
            var cur = EnvLockRpc.Current;
            if (cur.Active && cur.Owner != owner) { reason = "locked by " + cur.Owner; return false; }
            if (dayFraction >= 0f && (dayFraction > 1f || float.IsNaN(dayFraction))) { reason = "dayFraction out of 0..1"; return false; }
            if (!string.IsNullOrEmpty(env) && !EnvNames().Contains(env)) { reason = "unknown env " + env; return false; }
            if (pause < -1 || pause > 1) { reason = "pause must be -1, 0 or 1"; return false; }

            var next = cur.Active ? cur : new EnvLockRpc.LockState { Owner = owner };
            if (dayFraction >= 0f) { next.TimeLocked = true; next.DayFraction = dayFraction; }
            if (!string.IsNullOrEmpty(env)) { next.EnvLocked = true; next.Env = env; }
            if (pause >= 0) next.Paused = pause == 1;
            next.Active = next.TimeLocked || next.EnvLocked || next.Paused;
            next.LeaseEnd = DateTime.UtcNow.AddSeconds(LeaseSeconds);
            EnvLockRpc.SetAndBroadcast(next, "lock by " + owner);
            return true;
        }

        public static bool Renew(string owner, out string reason)
        {
            if (!IsServer(out reason)) return false;
            EnvLockRpc.Expire();
            var cur = EnvLockRpc.Current;
            if (!cur.Active) { reason = "no lock"; return false; }
            if (cur.Owner != owner) { reason = "locked by " + cur.Owner; return false; }
            cur.LeaseEnd = DateTime.UtcNow.AddSeconds(LeaseSeconds);
            EnvLockRpc.Current = cur;
            return true;
        }

        public static bool Release(string owner, out string reason)
        {
            if (!IsServer(out reason)) return false;
            var cur = EnvLockRpc.Current;
            if (!cur.Active) return true;   // nothing to release is not a failure
            if (cur.Owner != owner) { reason = "locked by " + cur.Owner; return false; }
            EnvLockRpc.SetAndBroadcast(new EnvLockRpc.LockState(), "released by " + owner);
            return true;
        }

        public static string[] EnvNames()
        {
            var em = EnvMan.instance;
            if (em == null || em.m_environments == null) return new string[0];
            return em.m_environments.Where(e => e != null && !string.IsNullOrEmpty(e.m_name)).Select(e => e.m_name).Distinct().ToArray();
        }

        /// <summary>JSON: {dayFraction, env, timeLocked, envLocked, paused, lockedBy, leaseLeftSec}. Unlocked fields report the live values.</summary>
        public static string State()
        {
            EnvLockRpc.Expire();
            var c = EnvLockRpc.Current; var em = EnvMan.instance;
            float day = c.TimeLocked ? c.DayFraction : (em != null ? em.GetDayFraction() : 0f);
            string env = c.EnvLocked ? c.Env : (em != null && em.GetCurrentEnvironment() != null ? em.GetCurrentEnvironment().m_name : "");
            double left = c.Active ? Math.Max(0, (c.LeaseEnd - DateTime.UtcNow).TotalSeconds) : 0;
            return "{\"dayFraction\":" + day.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)
                + ",\"env\":\"" + Esc(env) + "\",\"timeLocked\":" + B(c.TimeLocked) + ",\"envLocked\":" + B(c.EnvLocked)
                + ",\"paused\":" + B(c.Paused) + ",\"lockedBy\":" + (c.Active ? "\"" + Esc(c.Owner) + "\"" : "null")
                + ",\"leaseLeftSec\":" + ((int)left) + "}";
        }

        private static string B(bool b) => b ? "true" : "false";
        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        private static bool IsServer(out string reason)
        {
            reason = "";
            if (ZNet.instance == null || ZRoutedRpc.instance == null) { reason = "no world"; return false; }
            if (!ZNet.instance.IsServer()) { reason = "server only"; return false; }
            return true;
        }
    }
}
