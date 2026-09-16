using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Tells a rejected client why. A Fires server mod calls <see cref="Send"/> with a readable reason before kicking a
    /// peer; the client caches it and <see cref="FiresConnectReasonPanel"/> shows it in the connection-failed dialog
    /// instead of the generic message. Vanilla clients ignore the RPC, and pure transport failures still show vanilla's
    /// "Failed to connect" since no reason can reach them.
    /// </summary>
    [HarmonyPatch]
    public static class FiresConnectReason
    {
        private const string RpcName = "FVA_ConnectReason";
        private const float NoReasonTime = -999f;

        // A reason is valid for display for a window long enough that a mid-session kick survives the
        // in-world -> main-menu teardown + reload (which can run tens of seconds on a large world) before
        // ShowConnectError paints it. The per-attempt clear below (OnNewConnection) is what actually
        // prevents stale cross-attempt paint; the TTL is just a final backstop.
        private const float ReasonTtlSeconds = 120f;

        private static string _reasonText;
        private static float _reasonTime = NoReasonTime;

        /// <summary>Client side only: register the receiver on the server peer so the server can push a reason.</summary>
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        [HarmonyPostfix]
        private static void ZNet_OnNewConnection_Postfix(ZNet __instance, ZNetPeer peer)
        {
            if (__instance == null || __instance.IsServer()) return; // only a client needs to receive
            // Clear any stale reason from a prior attempt so it can't paint onto THIS connection's error
            // dialog; if the server kicks, it re-sends a fresh reason later in the handshake.
            _reasonText = null;
            _reasonTime = NoReasonTime;
            peer?.m_rpc?.Register<ZPackage>(RpcName, RPC_ReceiveReason);
        }

        private static void RPC_ReceiveReason(ZRpc rpc, ZPackage pkg)
        {
            try
            {
                _reasonText = pkg.ReadString();
                _reasonTime = Time.realtimeSinceStartup;
            }
            catch { /* best-effort; a malformed reason just leaves the vanilla error in place */ }
        }

        /// <summary>
        /// Server side: push a rejection reason to <paramref name="rpc"/>'s peer. Call this IMMEDIATELY
        /// before the kick so it flushes ahead of the disconnect. <paramref name="title"/> is the red
        /// headline; <paramref name="lines"/> are detail rows. Null-safe (no-op on a null rpc).
        /// </summary>
        public static void Send(ZRpc rpc, string title, IEnumerable<string> lines = null)
        {
            if (rpc == null) return;
            try
            {
                var pkg = new ZPackage();
                pkg.Write(BuildText(title, lines));
                rpc.Invoke(RpcName, pkg);
            }
            catch (Exception ex) { Debug.LogWarning($"[FiresConnectReason] Send failed: {ex.Message}"); }
        }

        /// <summary>
        /// Client side, client-initiated refusal: cache a reason WITHOUT an RPC, so a client that
        /// rejects its own connection (e.g. the Characters anti-import self-reject — refusing to join
        /// with a character already played elsewhere) populates the same slot the server RPC would.
        /// <see cref="FiresConnectReasonPanel"/> paints it on the next <c>ShowConnectError</c>.
        /// </summary>
        public static void SetLocalReason(string title, IEnumerable<string> lines = null)
        {
            _reasonText = BuildText(title, lines);
            _reasonTime = Time.realtimeSinceStartup;
        }

        /// <summary>Client side: take the cached reason if one arrived recently, then clear it.</summary>
        public static bool TryConsumeReason(out string text)
        {
            text = null;
            if (string.IsNullOrEmpty(_reasonText)) return false;
            if (Time.realtimeSinceStartup - _reasonTime > ReasonTtlSeconds) { _reasonText = null; return false; }
            text = _reasonText;
            _reasonText = null;
            return true;
        }

        private static string BuildText(string title, IEnumerable<string> lines)
        {
            var sb = new StringBuilder();
            sb.Append("<color=#E0503C>")
              .Append(string.IsNullOrEmpty(title) ? "Connection refused by server" : title)
              .Append("</color>");
            if (lines != null)
            {
                foreach (var line in lines)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    sb.Append('\n').Append(line);
                }
            }
            return sb.ToString();
        }
    }
}
