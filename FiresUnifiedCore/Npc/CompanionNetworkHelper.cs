using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Centralized helpers for spawning and destroying ZNetView-bearing GameObjects.
    ///
    /// WHY THIS EXISTS:
    /// Valheim tracks every networked object through ZNetScene. Using raw
    /// Object.Destroy() bypasses that tracking, leaving orphaned ZDOs that cause
    /// NullReferenceExceptions deep inside engine code (GraphicsSettingsManager,
    /// PresentManager, etc.) and duplicate companions.
    ///
    /// RULES:
    /// - SPAWN:  Object.Instantiate() is fine — ZNetView.Awake() registers with
    ///           ZNetScene automatically. Spawn() is a thin wrapper for consistency.
    /// - DESTROY: ALWAYS use CompanionNetworkHelper.Destroy() which goes through
    ///            ZNetScene.instance.Destroy() to properly remove the ZDO. Never
    ///            fall back to Object.Destroy() on a ZNetView object.
    /// </summary>
    public static class CompanionNetworkHelper
    {
        /// <summary>
        /// Spawns a prefab at the given position. ZNetView.Awake() handles
        /// ZNetScene registration automatically. This is a thin wrapper for
        /// consistency and null-safety.
        /// </summary>
        public static GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation)
        {
            if (prefab == null)
            {
                Debug.LogError("[CompanionNetworkHelper] Spawn called with null prefab");
                return null;
            }

            return Object.Instantiate(prefab, position, rotation);
        }

        /// <summary>
        /// Destroys a networked GameObject through ZNetScene so the ZDO is
        /// properly removed and other clients are notified.
        ///
        /// This claims ownership first if needed, disables visual components
        /// that spam NREs during teardown, then routes through ZNetScene.Destroy().
        /// </summary>
        /// <param name="go">The GameObject to destroy.</param>
        /// <param name="disableFirst">If true, disables VisEquipment and
        /// CharacterAnimEvent before destruction to prevent NRE spam in their
        /// Update/LateUpdate loops during the teardown frame.</param>
        public static void Destroy(GameObject go, bool disableFirst = true)
        {
            if (go == null) return;

            if (disableFirst)
            {
                DisableVisualComponents(go);
            }

            var nview = go.GetComponent<ZNetView>();

            // No ZNetView — pure local object, plain Object.Destroy is fine.
            if (nview == null)
            {
                Object.Destroy(go);
                return;
            }

            // Claim ownership if we can. Some paths require ownership for the
            // network-side ZDO destroy to broadcast properly. We only attempt
            // this when the ZDO is still valid — otherwise ClaimOwnership has
            // nothing to claim.
            if (nview.IsValid() && !nview.IsOwner())
            {
                try { nview.ClaimOwnership(); } catch { /* ownership races are non-fatal */ }
            }

            // ALWAYS route through ZNetScene.Destroy when a ZNetView is present,
            // even if nview.IsValid() is false. Vanilla ZNetScene.Destroy is
            // safe to call in either case:
            //   • Valid ZDO  ? ResetZDO + m_instances.Remove(zdo) + Object.Destroy(go)
            //   • Null ZDO   ? just Object.Destroy(go)
            //
            // Skipping ZNetScene.Destroy and doing raw Object.Destroy on a
            // ZNetView whose ZDO has been reset (but whose entry might still
            // be in m_instances under a different code path) leaves a stale
            // dictionary entry pointing at a Unity-destroyed Component. The
            // next ZNetScene.RemoveObjects tick iterates m_instances.Values,
            // calls znetView.GetZDO() on the destroyed Component, and NREs.
            // That's the spam pattern we've been chasing. Letting vanilla
            // make the call removes the race entirely — the dict cleanup and
            // the Unity destroy are now atomic from our perspective.
            if (ZNetScene.instance != null)
            {
                ZNetScene.instance.Destroy(go);
            }
            else
            {
                // ZNetScene gone (shutting down). The ZDO will be cleaned on
                // next world load; nothing else to do here.
                Debug.LogWarning($"[CompanionNetworkHelper] ZNetScene not available for Destroy of {go.name}");
                Object.Destroy(go);
            }
        }

        /// <summary>
        /// Disables components that spam NullReferenceExceptions during the
        /// teardown frame between Destroy being called and the object actually
        /// being removed by Unity.
        /// </summary>
        private static void DisableVisualComponents(GameObject go)
        {
            var visEquip = go.GetComponent<VisEquipment>();
            if (visEquip != null) visEquip.enabled = false;

            var charAnimEvent = go.GetComponent<CharacterAnimEvent>();
            if (charAnimEvent != null) charAnimEvent.enabled = false;
        }
    }
}
