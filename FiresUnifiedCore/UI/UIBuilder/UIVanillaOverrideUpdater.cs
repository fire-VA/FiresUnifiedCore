using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Per-frame driver for vanilla UI overrides during play: drains the chunked layout send queue, applies overrides
    /// whose targets have since appeared, and syncs injected elements' visibility with their vanilla roots. All
    /// overrides are cleaned up on logout so the menu holds no stale references.
    /// </summary>
    public class UIVanillaOverrideUpdater : MonoBehaviour
    {
        private float _pendingCheckInterval = 2f;
        private float _pendingCheckTimer;

        /// <summary>Tracks whether we were in-game last frame so we can detect logout transitions.</summary>
        private bool _wasInGame;

        private void Update()
        {
            // Drain the RPC send queue every frame (sends up to N chunks per tick
            // to avoid flooding Steam's send buffer with k_EResultLimitExceeded)
            UILayoutSyncRPC.DrainSendQueue();

            bool inGame = Player.m_localPlayer != null;

            // Detect logout transition: we were in-game but now we're not.
            // Clean up all active overrides — the vanilla GOs they reference are being destroyed
            // as the game scene unloads, so holding onto them would cause null-ref spam.
            if (_wasInGame && !inGame)
            {
                UIVanillaOverrideManager.OnSessionEnd();
                _pendingCheckTimer = 0f;
            }
            _wasInGame = inGame;

            // Only process pending overrides during active gameplay.
            // Vanilla HUD/UI GameObjects don't exist on the menu screen.
            if (!inGame) return;

            // Sync injected mod element visibility with their vanilla roots every frame.
            // Injected elements are new GameObjects added to the vanilla hierarchy —
            // they don't inherit the game's show/hide logic (InventoryGui.Show/Hide etc.),
            // so we must manually match their active state to the vanilla root's state.
            UIVanillaOverrideManager.SyncInjectedVisibility();

            _pendingCheckTimer += Time.unscaledDeltaTime;
            if (_pendingCheckTimer >= _pendingCheckInterval)
            {
                _pendingCheckTimer = 0f;
                UIVanillaOverrideManager.ProcessPendingOverrides();
            }
        }

        private void OnDestroy()
        {
            // When the mod/game is shutting down, clear runtime references but do NOT
            // remove entries from the active overrides dictionary or re-persist state.
            // DisableAll() was previously called here, but it removes entries and then
            // calls PersistOverrides() which writes an empty file — wiping the user's
            // persisted override state on every game exit. The vanilla GOs are being
            // destroyed by Unity anyway, so there's nothing to "restore".
            // OnSessionEnd() safely nulls out GO references without touching persistence.
            UIVanillaOverrideManager.OnSessionEnd();
        }
    }
}
