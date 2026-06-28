using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Cross-mod connection-rejection diagnostics. When a Fires server mod refuses a peer at (or near)
    /// connect time, it calls <see cref="Send"/> with a human-readable reason BEFORE invoking the vanilla
    /// kick (<c>rpc.Invoke("Error", ...)</c> / disconnect). The reason rides a dedicated per-peer RPC to
    /// the client, which caches it; the client-side <see cref="FiresConnectReasonPanel"/> then renders it
    /// into the vanilla connection-failed dialog instead of the generic "Failed to connect" /
    /// "Incompatible version". A vanilla (or non-Fires) client simply never registers the receiver, so the
    /// server's extra RPC is ignored and the player just sees the normal generic error - graceful.
    ///
    /// This carries SERVER-DECIDED reasons (version/mod gate, anti-cheat). It cannot explain a pure
    /// transport failure (Steam timeout / unreachable), because in that case nothing reaches the server
    /// and no reason is ever sent - the client shows the vanilla "Failed to connect", which is correct.
    /// </summary>
    [HarmonyPatch]
    public static class FiresConnectReason
    {
        private const string RpcName = "FVA_ConnectReason";

        // A reason is valid for display for a window long enough that a mid-session kick survives the
        // in-world -> main-menu teardown + reload (which can run tens of seconds on a large world) before
        // ShowConnectError paints it. The per-attempt clear below (OnNewConnection) is what actually
        // prevents stale cross-attempt paint; the TTL is just a final backstop.
        private const float ReasonTtlSeconds = 120f;

        private static string _reasonText;
        private static float _reasonTime = -999f;

        /// <summary>Client side only: register the receiver on the server peer so the server can push a reason.</summary>
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        [HarmonyPostfix]
        private static void ZNet_OnNewConnection_Postfix(ZNet __instance, ZNetPeer peer)
        {
            if (__instance == null || __instance.IsServer()) return; // only a client needs to receive
            // Clear any stale reason from a prior attempt so it can't paint onto THIS connection's error
            // dialog; if the server kicks, it re-sends a fresh reason later in the handshake.
            _reasonText = null;
            _reasonTime = -999f;
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
