using System;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Net
{
    // Safe-destroy helper for GameObjects that may carry a ZNetView.
    //
    // Raw Object.Destroy on a ZNetView'd GameObject tears down the Unity
    // side without removing the matching entry from ZNetScene.m_instances.
    // The next ZNetScene.RemoveObjects tick iterates that dictionary, hits
    // the destroyed reference, and NREs. Routing through ZNetScene.Destroy
    // keeps both sides consistent.
    //
    // SafeDestroyImmediate is for callers that need the GameObject gone
    // before the next statement (same-frame replacement, icon-stage
    // proxies, hammer-overlay UI swaps).
    public static class NetworkObjectHelper
    {
        public static void SafeDestroy(GameObject go)
        {
            if (go == null) return;

            ZNetView nview = TryGetZNetView(go);
            if (nview == null)
            {
                UnityEngine.Object.Destroy(go);
                return;
            }

            TryClaimOwnership(nview);
            RouteDestroyThroughZNetScene(go);
        }

        public static void SafeDestroyImmediate(GameObject go)
        {
            if (go == null) return;

            ZNetView nview = TryGetZNetView(go);
            if (nview != null && nview.IsValid())
                TearDownZdoBeforeImmediate(nview);

            try { UnityEngine.Object.DestroyImmediate(go); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"NetworkObjectHelper.SafeDestroyImmediate threw on '{go.name}': {ex.Message}");
            }
        }

        private static ZNetView TryGetZNetView(GameObject go)
        {
            try { return go.GetComponent<ZNetView>(); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"NetworkObjectHelper GetComponent threw on '{go.name}': {ex.Message}");
                return null;
            }
        }

        private static void TryClaimOwnership(ZNetView nview)
        {
            if (!nview.IsValid() || nview.IsOwner()) return;
            try { nview.ClaimOwnership(); }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"NetworkObjectHelper ClaimOwnership threw: {ex.Message}");
            }
        }

        private static void RouteDestroyThroughZNetScene(GameObject go)
        {
            if (ZNetScene.instance != null)
            {
                ZNetScene.instance.Destroy(go);
                return;
            }
            UnityEngine.Object.Destroy(go);
        }

        // Replicates ZNetScene.Destroy's ZDO bookkeeping (ownership claim,
        // ResetZDO, owner-side DestroyZDO broadcast) without deferring the
        // GameObject destruction. The DestroyImmediate that follows in the
        // caller actually tears the Unity object down synchronously.
        private static void TearDownZdoBeforeImmediate(ZNetView nview)
        {
            try
            {
                if (!nview.IsOwner())
                {
                    try { nview.ClaimOwnership(); }
                    catch (Exception ex)
                    {
                        FiresLogger.LogWarning($"NetworkObjectHelper.SafeDestroyImmediate ClaimOwnership threw: {ex.Message}");
                    }
                }
                var zdo = nview.GetZDO();
                if (zdo == null) return;

                nview.ResetZDO();
                if (zdo.IsOwner() && ZDOMan.instance != null)
                    ZDOMan.instance.DestroyZDO(zdo);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"NetworkObjectHelper.SafeDestroyImmediate ZDO teardown threw: {ex.Message}");
            }
        }
    }
}
