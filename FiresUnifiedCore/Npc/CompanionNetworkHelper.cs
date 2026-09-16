using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Spawning and destroying networked companion objects. Instantiate is fine because ZNetView.Awake registers
    /// with ZNetScene, but destroying must always go through ZNetScene.Destroy: Object.Destroy leaves orphaned ZDOs
    /// that cause engine NREs and duplicate companions.
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

            // Always destroy through ZNetScene.Destroy when there is a ZNetView, valid ZDO or not. A raw Object.Destroy
            // could leave a stale m_instances entry pointing at a destroyed component, which NREs the next
            // RemoveObjects pass; vanilla handles both cases.
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
