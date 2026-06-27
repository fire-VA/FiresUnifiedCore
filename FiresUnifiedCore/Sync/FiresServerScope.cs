using System.IO;
using System.Text;
using UnityEngine;

namespace FiresCore.Sync
{
    /// <summary>
    /// Identifies the server a node is currently bound to, so per-server client
    /// caches never bleed across servers when one r2modman profile is reused.
    /// On a client the key is the connected server's host id; on a listen-server
    /// host it is the world UID (ZNet.m_world is host-only). Returns null when
    /// identity isn't resolved yet — callers must guard.
    ///
    /// Only valid after ZNet/ZRoutedRpc are alive (post WaitForZNetReady); never
    /// call from plugin Awake. Reads ZNet state, so call from the main thread.
    /// </summary>
    public static class FiresServerScope
    {
        public static string Key()
        {
            try
            {
                var net = ZNet.instance;
                if (net == null) return null;
                var sp = net.GetServerPeer();
                if (sp != null && sp.m_socket != null)
                {
                    string host = sp.m_socket.GetHostName();
                    if (!string.IsNullOrEmpty(host)) return "s" + Sanitize(host);
                }
                if (net.IsServer())
                {
                    long uid = net.GetWorldUID();
                    if (uid != 0L) return "h" + uid.ToString("X");
                }
            }
            catch { }
            return null;
        }

        public static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        /// <summary>baseDir/&lt;key&gt;, or baseDir/"unknown" when the server isn't resolved.</summary>
        public static string ScopedDir(string baseDir)
        {
            string key = Key();
            return Path.Combine(baseDir, string.IsNullOrEmpty(key) ? "unknown" : key);
        }

        /// <summary>Best-effort delete of legacy (profile-global) cache files. No-op once gone.</summary>
        public static void DeleteLegacy(params string[] paths)
        {
            if (paths == null) return;
            foreach (var p in paths)
            {
                try { if (!string.IsNullOrEmpty(p) && File.Exists(p)) File.Delete(p); }
                catch { }
            }
        }
    }
}
