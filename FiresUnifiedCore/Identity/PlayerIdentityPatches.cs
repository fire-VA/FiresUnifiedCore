using System.Collections;
using HarmonyLib;
using UnityEngine;

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
            PlayerIdentity.ClearRuntimeState();
        }

        [HarmonyPatch(typeof(Game), "Logout")]
        [HarmonyPrefix]
        private static void LogoutPrefix()
        {
            PlayerIdentity.ClearRuntimeState();
        }

        private static IEnumerator CaptureWhenReady(ZNetPeer peer)
        {
            float elapsed = 0f;
            const float timeout = 10f;

            while (elapsed < timeout)
            {
                if (peer == null || !PlayerIdentity.IsServerRuntime())
                {
                    yield break;
                }

                string playerName = Traverse.Create(peer).Field("m_playerName").GetValue<string>();
                long uid = Traverse.Create(peer).Field("m_uid").GetValue<long>();
                if (uid != 0L && !string.IsNullOrWhiteSpace(playerName))
                {
                    PlayerIdentity.CapturePeer(peer);
                    yield break;
                }

                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            if (peer != null && PlayerIdentity.IsServerRuntime())
            {
                PlayerIdentity.CapturePeer(peer);
            }
        }
    }
}
