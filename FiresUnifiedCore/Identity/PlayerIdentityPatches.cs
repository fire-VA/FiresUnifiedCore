using System.Collections;
using HarmonyLib;

namespace FiresCore.Identity
{
    // Drives server-side identity capture off vanilla connection lifecycle events. Auto-patched by
    // FiresMod<TPlugin>.Awake's Harmony.PatchAll(assembly); no separate Harmony instance. Every body is
    // server-gated (PlayerIdentity.CapturePeer / IsServerRuntime self-gate too), and all references are to
    // server-safe types (ZNet, Game), so these need no dedicated-server skip. Ported from VikingLands.Core's
    // PlayerIdentityPatches.
    [HarmonyPatch]
    internal static class PlayerIdentityPatches
    {
        [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
        [HarmonyPostfix]
        private static void OnNewConnectionPostfix(ZNetPeer peer)
        {
            if (peer == null || !PlayerIdentity.IsServerRuntime() || FiresCore.FiresUnifiedCore.Instance == null)
            {
                return;
            }

            FiresCore.FiresUnifiedCore.Instance.StartCoroutine(CaptureWhenReady(peer));
        }

        [HarmonyPatch(typeof(ZNet), "Disconnect")]
        [HarmonyPostfix]
        private static void DisconnectPostfix(ZNetPeer peer)
        {
            // Forget ONLY the disconnecting peer's dedup entry. This previously called ClearRuntimeState(),
            // which wiped the recorded-session markers for EVERY connected player on any single disconnect
            // (so the next event for anyone re-recorded them). ForgetSession is the per-peer cleanup.
            if (peer == null)
            {
                return;
            }

            long uid = Traverse.Create(peer).Field("m_uid").GetValue<long>();
            PlayerIdentity.ForgetSession(uid);
        }

        [HarmonyPatch(typeof(ZNet), "Shutdown")]
        [HarmonyPrefix]
        private static void ShutdownPrefix()
        {
            FiresCore.Storage.VaultWriter.Drain();
            PlayerIdentity.ClearRuntimeState();
        }

        [HarmonyPatch(typeof(Game), "Logout")]
        [HarmonyPrefix]
        private static void LogoutPrefix()
        {
            PlayerIdentity.ClearRuntimeState();
        }

        // The uid and name arrive with the peer's RPC_PeerInfo, which a join gate can hold back for as long as it sends the
        // world's caches (R25: 26 s for a 359 MB terrain cache). A fixed 10 s wait gave up first and recorded nothing
        // ("Peer skipped: session uid not found"), so it waits as long as the connection lasts.
        private static IEnumerator CaptureWhenReady(ZNetPeer peer)
        {
            while (peer != null && PlayerIdentity.IsServerRuntime() && peer.m_socket != null && peer.m_socket.IsConnected())
            {
                if (peer.m_uid != 0L && !string.IsNullOrWhiteSpace(peer.m_playerName))
                {
                    PlayerIdentity.CapturePeer(peer, System.Diagnostics.Stopwatch.StartNew());
                    yield break;
                }

                yield return null;
            }
        }
    }
}
