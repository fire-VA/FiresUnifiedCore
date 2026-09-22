using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI
{
    /// <summary>
    /// Applies a layout's transform and visual edits directly onto the original vanilla UI GameObjects, so every
    /// component, reference and callback keeps working. Originals are backed up first and restored when the
    /// override is disabled, nothing is deactivated or destroyed, and captured sprites load from disk so a layout
    /// still works after its source mod is removed. Driven by the VanillaOverrideTarget and
    /// VanillaOverrideEnabled layout metadata keys.
    /// </summary>

    public static class UIVanillaOverrideManager
    {
        // ---------------------------------------
        //  Active override tracking
        // ---------------------------------------

        /// <summary>Backed-up RectTransform and visual state for a single vanilla element.</summary>
        public class TransformBackup
        {
            public string Path;
            public GameObject GO;
            public Vector2 AnchorMin;
            public Vector2 AnchorMax;
            public Vector2 Pivot;
            public Vector2 AnchoredPosition;
            public Vector2 SizeDelta;
            public Vector2 OffsetMin;
            public Vector2 OffsetMax;
            public Vector3 LocalScale;
            public bool WasActive;

            // Visual state backups (Image component)
            public bool HadImage;
            public Sprite OriginalSprite;
            public Color OriginalImageColor;
            public Image.Type OriginalImageType;
            public bool OriginalPreserveAspect;
            public float OriginalPixelsPerUnit;
            public bool OriginalFillCenter;

            // Visual state backups (RawImage component)
            public bool HadRawImage;
            public Texture OriginalRawTexture;
            public Color OriginalRawImageColor;
        }

        /// <summary>Tracks a single active override instance.</summary>
        public class ActiveOverride
        {
            public string LayoutUID;
            public string VanillaTargetName;
            public GameObject VanillaRoot;
            /// <summary>
            /// For the in-place approach, CustomRoot is the same as VanillaRoot.
            /// Kept for API compatibility with code that calls GetCustomRoot().
            /// </summary>
            public GameObject CustomRoot;
            public bool WasVanillaActive;
            /// <summary>Original transforms backed up before modification.</summary>
            public List<TransformBackup> Backups = new List<TransformBackup>();
            /// <summary>GameObjects instantiated for mod-injected nodes not present in vanilla.</summary>
            public List<GameObject> InjectedObjects = new List<GameObject>();
            /// <summary>Number of vanilla elements matched and modified.</summary>
            public int MatchedCount;
            /// <summary>Number of layout nodes that had no vanilla match.</summary>
            public int UnmatchedCount;
            /// <summary>Number of mod-injected nodes instantiated from layout data.</summary>
            public int InjectedCount;
            /// <summary>
            /// Injected GameObjects whose layout node had Active=false (user deactivated in editor).
            /// These are excluded from the per-frame SyncInjectedVisibility so they stay hidden
            /// as the user intended, rather than being forced visible when the vanilla root is shown.
            /// </summary>
            public HashSet<GameObject> UserDeactivatedObjects = new HashSet<GameObject>();
        }

        private static readonly Dictionary<string, ActiveOverride> _activeOverrides =
            new Dictionary<string, ActiveOverride>(StringComparer.OrdinalIgnoreCase);

        private static bool _initialized;

        // Config directory for override state persistence
        private static string OverrideConfigDir =>
            Path.Combine(FiresCore.Storage.FiresConfigPaths.UiOverrides);

        private static string OverrideConfigFile =>
            Path.Combine(OverrideConfigDir, "active_overrides.txt");

        // ---------------------------------------
        //  Metadata key constants
        // ---------------------------------------

        public const string MetaKeyTarget = "VanillaOverrideTarget";
        public const string MetaKeyEnabled = "VanillaOverrideEnabled";

        /// <summary>
        /// Tag value that marks a layout node (and its entire subtree) as override-protected.
        /// When the override system encounters a node with this tag, it skips that node and
        /// all of its descendants - no transform changes, no visual changes, no injection.
        /// This allows modded UI panels (e.g., VNEI, EpicLoot) captured inside a vanilla
        /// hierarchy to remain untouched by the override, preventing duplicate/broken UIs.
        /// Set via the Inspector Tag field or context menu in the editor.
        /// References the canonical constant in <see cref="UIOverrideElementTags"/>.
        /// </summary>
        public const string OverrideProtectedTag = UIOverrideElementTags.OverrideProtected;

        // ---------------------------------------
        //  Public API
        // ---------------------------------------

        /// <summary>
        /// Initializes the override system. Loads persisted override state and applies
        /// any overrides whose layouts exist in the codex.
        /// Safe to call multiple times - only loads once.
        /// </summary>
        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;
            LoadPersistedOverrides();
        }

        /// <summary>
        /// Early initialization hook called before game systems load (ObjectDB, ZNet, etc.).
        /// Detects if we have persisted overrides and pre-loads their required asset bundles
        /// so sprites and other assets are available during early registration.
        /// Call this from the mod's Awake() or a startup hook.
        /// </summary>
        public static void EarlyLoadRequiredBundles()
        {
            if (!File.Exists(OverrideConfigFile)) return;

            try
            {
                var lines = File.ReadAllLines(OverrideConfigFile);
                var allRequired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('|');
                    if (parts.Length < 2) continue;

                    string uid = parts[0].Trim();
                    var layout = UILayoutCodex.Get(uid);
                    if (layout == null) continue;

                    string requiredBundles = layout.GetMeta("required_bundles");
                    if (string.IsNullOrEmpty(requiredBundles)) continue;

                    foreach (string bundleName in requiredBundles.Split(','))
                    {
                        string trimmed = bundleName.Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                            allRequired.Add(trimmed);
                    }
                }

                if (allRequired.Count > 0)
                {
                    Debug.Log($"[UIVanillaOverride] Early loading {allRequired.Count} required bundle(s) for persisted overrides: " +
                        string.Join(", ", allRequired));
                    UIBuilderAssetCache.LoadRequiredBundles(allRequired);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIVanillaOverride] Failed to early-load required bundles: {ex.Message}");
            }
        }

        /// <summary>
        /// Enables a vanilla UI override for the given layout.
        /// The layout must have "VanillaOverrideTarget" metadata set.
        /// This modifies the vanilla GO's transforms in-place - all MonoBehaviours,
        /// component wiring, and game references remain intact.
        /// </summary>
        /// <returns>True if the override was successfully applied.</returns>
        public static bool EnableOverride(string layoutUID)
        {
            if (string.IsNullOrEmpty(layoutUID))
            {
                Debug.LogWarning("[UIVanillaOverride] Cannot enable override: no layout UID provided.");
                return false;
            }

            var layout = UILayoutCodex.Get(layoutUID);
            if (layout == null)
            {
                Debug.LogWarning($"[UIVanillaOverride] Layout '{layoutUID}' not found in codex.");
                return false;
            }

            string target = layout.GetMeta(MetaKeyTarget);
            if (string.IsNullOrEmpty(target))
            {
                Debug.LogWarning($"[UIVanillaOverride] Layout '{layoutUID}' has no VanillaOverrideTarget metadata.");
                return false;
            }

            // If already active, disable first to restore originals
            if (_activeOverrides.ContainsKey(layoutUID))
                DisableOverride(layoutUID);

            // Find the vanilla GameObject
            var vanillaGO = FindVanillaTarget(target);
            if (vanillaGO == null)
            {
                Debug.LogWarning($"[UIVanillaOverride] Vanilla target '{target}' not found in scene. " +
                    "Override will be applied when the target becomes available.");
                var pendingOverride = new ActiveOverride
                {
                    LayoutUID = layoutUID,
                    VanillaTargetName = target,
                    VanillaRoot = null,
                    CustomRoot = null,
                    WasVanillaActive = false
                };
                _activeOverrides[layoutUID] = pendingOverride;
                layout.SetMeta(MetaKeyEnabled, "true");
                PersistOverrides();
                Debug.Log($"[UIVanillaOverride] Override for '{target}' queued (vanilla GO not found yet).");
                return true;
            }

            return ApplyOverride(layout, vanillaGO);
        }

        /// <summary>
        /// Disables a vanilla UI override, restoring all original transforms.
        /// </summary>
        public static bool DisableOverride(string layoutUID)
        {
            if (string.IsNullOrEmpty(layoutUID)) return false;

            if (!_activeOverrides.TryGetValue(layoutUID, out var active))
            {
                Debug.Log($"[UIVanillaOverride] No active override for '{layoutUID}'.");
                return false;
            }

            // Destroy any mod-injected GameObjects we created during apply
            int destroyed = 0;
            if (active.InjectedObjects != null)
            {
                for (int i = 0; i < active.InjectedObjects.Count; i++)
                {
                    if (active.InjectedObjects[i] != null)
                    {
                        UnityEngine.Object.Destroy(active.InjectedObjects[i]);
                        destroyed++;
                    }
                }
                active.InjectedObjects.Clear();
            }
            active.UserDeactivatedObjects?.Clear();
            if (destroyed > 0)
                Debug.Log($"[UIVanillaOverride] Destroyed {destroyed} injected mod elements for '{active.VanillaTargetName}'.");

            // Remove mod-specific compat shims that were attached during apply
            if (active.VanillaRoot != null)
            {
                var azuCompat = active.VanillaRoot.GetComponent<UIOverrideAzuEPICompat>();
                if (azuCompat != null)
                    UnityEngine.Object.Destroy(azuCompat);
            }

            // Restore all backed-up transforms
            int restored = 0;
            if (active.Backups != null)
            {
                for (int i = 0; i < active.Backups.Count; i++)
                {
                    var backup = active.Backups[i];
                    if (backup.GO == null) continue;

                    var rect = backup.GO.GetComponent<RectTransform>();
                    if (rect == null) continue;

                    rect.anchorMin = backup.AnchorMin;
                    rect.anchorMax = backup.AnchorMax;
                    rect.pivot = backup.Pivot;
                    rect.localScale = backup.LocalScale;

                    bool isStretch = !Mathf.Approximately(backup.AnchorMin.x, backup.AnchorMax.x) ||
                                     !Mathf.Approximately(backup.AnchorMin.y, backup.AnchorMax.y);
                    if (isStretch)
                    {
                        rect.offsetMin = backup.OffsetMin;
                        rect.offsetMax = backup.OffsetMax;
                    }
                    else
                    {
                        rect.anchoredPosition = backup.AnchoredPosition;
                        rect.sizeDelta = backup.SizeDelta;
                    }

                    // Restore Image visual state
                    if (backup.HadImage)
                    {
                        var img = backup.GO.GetComponent<Image>();
                        if (img != null)
                        {
                            img.sprite = backup.OriginalSprite;
                            img.color = backup.OriginalImageColor;
                            img.type = backup.OriginalImageType;
                            img.preserveAspect = backup.OriginalPreserveAspect;
                            img.pixelsPerUnitMultiplier = backup.OriginalPixelsPerUnit;
                            img.fillCenter = backup.OriginalFillCenter;
                        }
                    }

                    // Restore RawImage visual state
                    if (backup.HadRawImage)
                    {
                        var rawImg = backup.GO.GetComponent<RawImage>();
                        if (rawImg != null)
                        {
                            rawImg.texture = backup.OriginalRawTexture;
                            rawImg.color = backup.OriginalRawImageColor;
                        }
                    }

                    // Restore active state if it was changed
                    if (backup.GO.activeSelf != backup.WasActive)
                        backup.GO.SetActive(backup.WasActive);

                    restored++;
                }
            }

            Debug.Log($"[UIVanillaOverride] Restored {restored} transforms for '{active.VanillaTargetName}'.");

            _activeOverrides.Remove(layoutUID);

            var layout = UILayoutCodex.Get(layoutUID);
            if (layout != null)
                layout.SetMeta(MetaKeyEnabled, "false");

            PersistOverrides();
            Debug.Log($"[UIVanillaOverride] Override disabled for '{layoutUID}'.");
            return true;
        }

        /// <summary>
        /// Disables all active overrides.
        /// </summary>
        public static void DisableAll()
        {
            var uids = _activeOverrides.Keys.ToList();
            foreach (var uid in uids)
                DisableOverride(uid);
        }

        /// <summary>
        /// Returns all currently active overrides.
        /// </summary>
        public static List<ActiveOverride> GetActiveOverrides()
        {
            return _activeOverrides.Values.ToList();
        }

        /// <summary>
        /// Returns true if the given layout has an active override.
        /// </summary>
        public static bool IsOverrideActive(string layoutUID)
        {
            return !string.IsNullOrEmpty(layoutUID) && _activeOverrides.ContainsKey(layoutUID);
        }

        /// <summary>
        /// Returns all layouts in the codex that have VanillaOverrideTarget metadata set.
        /// </summary>
        public static List<UILayoutDefinition> GetOverrideCapableLayouts()
        {
            var result = new List<UILayoutDefinition>();
            foreach (var layout in UILayoutCodex.GetAll())
            {
                string target = layout.GetMeta(MetaKeyTarget);
                if (!string.IsNullOrEmpty(target))
                    result.Add(layout);
            }
            return result;
        }

        /// <summary>
        /// Sets the vanilla override target on a layout. Called when capturing a vanilla UI
        /// so the layout "remembers" which vanilla GO it came from.
        /// </summary>
        public static void SetOverrideTarget(UILayoutDefinition layout, string vanillaGameObjectName)
        {
            if (layout == null || string.IsNullOrEmpty(vanillaGameObjectName)) return;
            layout.SetMeta(MetaKeyTarget, vanillaGameObjectName);
        }

        /// <summary>
        /// Checks if a layout has a vanilla override target set.
        /// </summary>
        public static bool HasOverrideTarget(UILayoutDefinition layout)
        {
            return layout != null && !string.IsNullOrEmpty(layout.GetMeta(MetaKeyTarget));
        }

        /// <summary>
        /// Gets the vanilla override target name from a layout.
        /// </summary>
        public static string GetOverrideTarget(UILayoutDefinition layout)
        {
            return layout?.GetMeta(MetaKeyTarget);
        }

        /// <summary>
        /// Refreshes an active override - re-applies the layout transforms in-place.
        /// Useful after editing and re-saving a layout.
        /// </summary>
        public static bool RefreshOverride(string layoutUID)
        {
            if (!_activeOverrides.TryGetValue(layoutUID, out var active)) return false;
            if (active.VanillaRoot == null) return false;

            // Restore old transforms first, then re-apply
            DisableOverride(layoutUID);
            return EnableOverride(layoutUID);
        }

        /// <summary>
        /// Called periodically to apply pending overrides whose vanilla targets weren't
        /// found at enable time. Only call this during active gameplay sessions - vanilla
        /// HUD/UI GameObjects don't exist on the menu screen.
        /// </summary>
        public static void ProcessPendingOverrides()
        {
            if (_activeOverrides.Count == 0) return;

            // Safety check: don't search for vanilla GOs when not in a game session.
            // The HUD, Minimap, etc. are destroyed when the player logs out.
            if (Player.m_localPlayer == null) return;

            foreach (var kvp in _activeOverrides.ToList())
            {
                var active = kvp.Value;
                if (active.VanillaRoot != null) continue; // Already applied

                // Use the quiet variant that doesn't log warnings on failure -
                // the target simply isn't loaded yet and that's expected.
                var vanillaGO = FindVanillaTargetQuiet(active.VanillaTargetName);
                if (vanillaGO == null) continue;

                var layout = UILayoutCodex.Get(active.LayoutUID);
                if (layout == null) continue;

                Debug.Log($"[UIVanillaOverride] Pending target '{active.VanillaTargetName}' found, applying override.");
                ApplyOverride(layout, vanillaGO);
            }
        }

        /// <summary>
        /// Called when the player logs out / leaves a game session.
        /// Clears all active override state without trying to restore transforms
        /// (the vanilla GOs are being destroyed anyway as the scene unloads).
        /// Pending overrides are preserved so they re-apply on the next session.
        /// </summary>
        public static void OnSessionEnd()
        {
            if (_activeOverrides.Count == 0) return;

            int cleared = 0;
            foreach (var kvp in _activeOverrides)
            {
                var active = kvp.Value;
                // Don't try to restore transforms - the GOs are being destroyed.
                // Just clear our references so we don't hold stale pointers.
                if (active.VanillaRoot != null)
                {
                    active.VanillaRoot = null;
                    active.CustomRoot = null;
                    active.Backups?.Clear();
                    active.InjectedObjects?.Clear();
                    active.UserDeactivatedObjects?.Clear();
                    active.MatchedCount = 0;
                    active.UnmatchedCount = 0;
                    active.InjectedCount = 0;
                    cleared++;
                }
            }

            if (cleared > 0)
                Debug.Log($"[UIVanillaOverride] Session ended - cleared {cleared} active override(s). " +
                    "Will re-apply when targets become available in next session.");
        }

        /// <summary>
        /// Each frame, matches the visibility of injected objects that sit directly under an override root to their
        /// vanilla root. Deeper elements inherit visibility from vanilla parents, and elements owned by a compat
        /// component (such as <see cref="UIOverrideAzuEPICompat"/>) are left to it, since syncing them here fought it.
        /// </summary>
        public static void SyncInjectedVisibility()
        {
            if (_activeOverrides.Count == 0) return;

            foreach (var kvp in _activeOverrides)
            {
                var active = kvp.Value;
                if (active.VanillaRoot == null) continue;
                if (active.InjectedObjects == null || active.InjectedObjects.Count == 0) continue;

                // Get the compat class's managed set (if any) so we can skip those GOs
                HashSet<GameObject> compatManaged = null;
                var azuCompat = active.VanillaRoot.GetComponent<UIOverrideAzuEPICompat>();
                if (azuCompat != null)
                    compatManaged = azuCompat.ManagedObjects;

                for (int i = 0; i < active.InjectedObjects.Count; i++)
                {
                    var go = active.InjectedObjects[i];
                    if (go == null) continue;

                    // Skip elements managed by a compat MonoBehaviour - it controls their visibility
                    if (compatManaged != null && compatManaged.Contains(go))
                        continue;

                    // For elements whose immediate parent is NOT the override root, skip sync.
                    // They inherit visibility from their intermediate vanilla parent naturally.
                    // Only elements directly under the override root need explicit sync.
                    var goParent = go.transform.parent;
                    if (goParent != null && goParent != active.VanillaRoot.transform)
                    {
                        // The element is parented deeper in the hierarchy (e.g., under Crafting,
                        // under RecipeList, etc.). Its parent's activeInHierarchy already
                        // controls whether it's visible. Don't override that.
                        continue;
                    }

                    // Skip elements the user explicitly deactivated in the editor.
                    // These should stay hidden regardless of the vanilla root's state.
                    if (active.UserDeactivatedObjects != null && active.UserDeactivatedObjects.Contains(go))
                        continue;

                    bool rootVisible = active.VanillaRoot.activeInHierarchy;
                    if (go.activeSelf != rootVisible)
                        go.SetActive(rootVisible);
                }
            }
        }

        /// <summary>
        /// Returns the modified vanilla root for a given layout UID, or null if not active.
        /// For in-place overrides, this is the original vanilla GO (still fully functional).
        /// </summary>
        public static GameObject GetCustomRoot(string layoutUID)
        {
            if (_activeOverrides.TryGetValue(layoutUID, out var active))
                return active.CustomRoot;
            return null;
        }

        /// <summary>
        /// Returns the original vanilla GameObject for a given layout UID, or null if not active.
        /// </summary>
        public static GameObject GetVanillaRoot(string layoutUID)
        {
            if (_activeOverrides.TryGetValue(layoutUID, out var active))
                return active.VanillaRoot;
            return null;
        }

        // ---------------------------------------
        //  Internal - In-place transform override
        // ---------------------------------------

        private static bool ApplyOverride(UILayoutDefinition layout, GameObject vanillaGO)
        {
            string layoutUID = layout.UID;
            string target = layout.GetMeta(MetaKeyTarget);
            bool wasActive = vanillaGO.activeSelf;

            Debug.Log($"[UIVanillaOverride] === Applying in-place override '{layoutUID}' to vanilla '{target}' ===");
            Debug.Log($"[UIVanillaOverride] Vanilla GO: name='{vanillaGO.name}', active={wasActive}, " +
                $"parent='{vanillaGO.transform.parent?.name}', childCount={vanillaGO.transform.childCount}");

            // Check for missing mod dependencies and warn (but don't block override application)
            var missingDeps = UIHarmonyPatchAuditor.GetMissingDependencies(layout);
            if (missingDeps.Count > 0)
            {
                Debug.LogWarning($"[UIVanillaOverride] Layout '{layoutUID}' was captured with {missingDeps.Count} mod(s) " +
                    $"that are no longer installed: {string.Join(", ", missingDeps.ToArray())}. " +
                    "Injected elements from those mods will be recreated from cached layout data, " +
                    "but runtime behaviours (Harmony patches, custom MonoBehaviours) cannot be replicated.");
            }

            // Load only the asset bundles required by this specific layout.
            // This replaces the blanket LoadCachedBundles() approach that tried to
            // load all 40+ cached bundles and produced dozens of errors.
            string requiredBundlesMeta = layout.GetMeta("required_bundles");
            if (!string.IsNullOrEmpty(requiredBundlesMeta))
            {
                var requiredBundles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string bundleName in requiredBundlesMeta.Split(','))
                {
                    string trimmed = bundleName.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        requiredBundles.Add(trimmed);
                }
                if (requiredBundles.Count > 0)
                    UIBuilderAssetCache.LoadRequiredBundles(requiredBundles);
            }
            else
            {
                // Legacy layout without required_bundles metadata - fall back to
                // scanning live mod bundles in memory (no disk loading = no errors)
                UIBuilderAssetCache.IndexAllLiveModBundles();
            }

            // Invalidate sprite cache so any newly loaded sprites are found
            UICanvasRenderer.InvalidateSpriteCache();

            // The layout was captured from this same vanilla hierarchy, then edited in the builder.
            // We need to match layout nodes to vanilla GOs and apply transform changes.
            // The editor normalizes the root for editing but stores the original in RuntimeRootTransform.

            var rootNode = layout.RootElement;
            if (rootNode == null)
            {
                Debug.LogError($"[UIVanillaOverride] Layout '{layoutUID}' has no RootElement.");
                return false;
            }

            // Log vanilla hierarchy summary
            int vanillaTotal = CountChildren(vanillaGO.transform);
            Debug.Log($"[UIVanillaOverride] Vanilla hierarchy: {vanillaTotal} total GameObjects");

            // Sanity check: if the layout has many nodes but the vanilla GO has very few children,
            // we probably matched the wrong GameObject (e.g., a generic "root" under TopLeftMessage
            // instead of the real inventory root). Abort to prevent injecting a full duplicate UI.
            int layoutNodeCount = rootNode.CountAll();
            if (layoutNodeCount > 20 && vanillaTotal < 5)
            {
                Debug.LogError($"[UIVanillaOverride] Aborting override '{layoutUID}': layout has {layoutNodeCount} nodes " +
                    $"but vanilla target '{vanillaGO.name}' (parent='{vanillaGO.transform.parent?.name}') only has {vanillaTotal} " +
                    $"GameObjects. This likely means FindVanillaTarget matched the wrong '{target}' in the scene. " +
                    $"Try using the VanillaTarget UID (e.g., 'vanilla_inventory_root') as the override target.");
                return false;
            }

            Debug.Log($"[UIVanillaOverride] Layout has {rootNode.CountAll()} nodes, " +
                $"HasRuntimeRootTransform={layout.HasRuntimeRootTransform()}, " +
                $"HasScreenPosition={layout.HasScreenPosition()}");

            // Build the override entry
            var overrideEntry = _activeOverrides.ContainsKey(layoutUID)
                ? _activeOverrides[layoutUID]
                : new ActiveOverride();

            overrideEntry.LayoutUID = layoutUID;
            overrideEntry.VanillaTargetName = target;
            overrideEntry.VanillaRoot = vanillaGO;
            overrideEntry.CustomRoot = vanillaGO; // Same GO - in-place modification
            overrideEntry.WasVanillaActive = wasActive;
            overrideEntry.Backups = new List<TransformBackup>();
            overrideEntry.InjectedObjects = new List<GameObject>();
            overrideEntry.UserDeactivatedObjects = new HashSet<GameObject>();
            overrideEntry.MatchedCount = 0;
            overrideEntry.UnmatchedCount = 0;
            overrideEntry.InjectedCount = 0;

            // Build a name->GO lookup of the vanilla hierarchy for fast matching
            var vanillaLookup = new Dictionary<string, List<GameObject>>(StringComparer.OrdinalIgnoreCase);
            BuildVanillaLookup(vanillaGO.transform, vanillaLookup, "");

            Debug.Log($"[UIVanillaOverride] Built vanilla lookup: {vanillaLookup.Count} unique names");

            // Compute which nodes have been modified from the capture baseline.
            // If a baseline exists, only modified/new nodes are overridden - unchanged
            // captured nodes are left untouched. If no baseline exists (legacy layout
            // or user-created from scratch), all nodes are overridden as before.
            var modifiedIds = UIOverrideDiffEngine.ComputeModifiedNodeIds(layout);
            if (modifiedIds != null)
            {
                var diffStats = UIOverrideDiffEngine.GetDiffStats(layout);
                Debug.Log($"[UIVanillaOverride] Diff baseline found: {diffStats}");
            }
            else
            {
                Debug.Log($"[UIVanillaOverride] No capture baseline - applying full override (legacy behavior)");
            }

            // Apply transforms recursively, matching by name/path
            ApplyNodeTransforms(rootNode, vanillaGO, vanillaLookup, overrideEntry, "", modifiedIds);

            // Wire injected elements to live vanilla game systems using name-based context clues
            if (overrideEntry.InjectedObjects.Count > 0)
            {
                int wired = WireInjectedElements(overrideEntry.InjectedObjects, vanillaGO);
                if (wired > 0)
                    Debug.Log($"[UIVanillaOverride] Wired {wired} injected elements to live game systems.");

                // Wire inventory slot/hotbar mirrors via our own slot system
                UIOverrideSlotSystem.DiscoverSlotsFromGameState();
                int inventoryWired = UIOverrideInventoryWiring.WireInventoryElements(overrideEntry.InjectedObjects);
                if (inventoryWired > 0)
                    Debug.Log($"[UIVanillaOverride] Wired {inventoryWired} inventory elements via UIOverrideInventoryWiring.");

                // NOTE: We do NOT force initial visibility here. The compat class
                // (UIOverrideAzuEPICompat) runs ApplyPanelStates() which sets the
                // correct initial visibility for each managed element. If we forced
                // all injected elements visible/hidden here, we'd override the compat
                // class's decisions. The per-frame SyncInjectedVisibility handles
                // non-compat-managed elements going forward.
            }

            // Force layout rebuild
            ForceLayoutRebuild(vanillaGO);

            // Apply screen position and scale to the vanilla root.
            // When the user has positioned the UI via Preview -> Layout mode, the saved
            // ScreenPosition/ScreenScale must be applied here so the override matches
            // what was shown in preview. This converts the vanilla root from its original
            // stretch-fill anchoring to center-anchored with explicit dimensions and an
            // offset, exactly as ApplyScreenPositionToPreview does in the preview canvas.
            // Without this, the vanilla root's original anchors would ignore the user's
            // positioning and the UI would snap back to the vanilla location.
            ApplyScreenPositionToRoot(vanillaGO, layout);

            _activeOverrides[layoutUID] = overrideEntry;

            // Attach mod-specific compatibility shims for mods that were present at
            // capture time but are no longer installed. These MonoBehaviours replicate
            // the UI-facing runtime behaviour (slot positioning, panel visibility, etc.)
            // that the mod's Harmony patches would normally handle.
            UIOverrideAzuEPICompat.TryAttach(layout, vanillaGO);

            layout.SetMeta(MetaKeyEnabled, "true");
            PersistOverrides();

            Debug.Log($"[UIVanillaOverride] === Override complete: '{layoutUID}' -> '{target}' ===");
            Debug.Log($"[UIVanillaOverride]   Matched: {overrideEntry.MatchedCount} elements modified");
            Debug.Log($"[UIVanillaOverride]   Injected: {overrideEntry.InjectedCount} mod elements instantiated from layout data");
            Debug.Log($"[UIVanillaOverride]   Unmatched: {overrideEntry.UnmatchedCount} layout nodes had no vanilla counterpart");
            Debug.Log($"[UIVanillaOverride]   Backups: {overrideEntry.Backups.Count} transforms saved for restore");

            return true;
        }

        /// <summary>
        /// Recursively walks the layout tree and applies transform changes to matching vanilla GOs.
        /// Matching strategy: walk both trees in parallel by child index and verify by name.
        /// Falls back to name-based lookup when parallel walk fails.
        /// </summary>
        private static void ApplyNodeTransforms(UIElementNode node, GameObject vanillaGO,
            Dictionary<string, List<GameObject>> vanillaLookup, ActiveOverride overrideEntry, string path,
            HashSet<string> modifiedIds = null)
        {
            if (node == null || vanillaGO == null) return;

            // Skip this node and its entire subtree if tagged as override-protected.
            // This allows modded UI panels captured inside a vanilla hierarchy
            // (e.g., VNEI inside inventory root) to remain completely untouched.
            if (IsOverrideProtected(node))
            {
                if (overrideEntry.UnmatchedCount < 20)
                    Debug.Log($"[UIVanillaOverride] Skipping override-protected subtree '{path}' (tag='{node.Tag}')");
                return;
            }

            // Check if this node was modified from the capture baseline.
            // If not, skip applying transforms/visuals but still recurse into children
            // (a child may be modified even if its parent is unchanged).
            bool shouldApply = UIOverrideDiffEngine.ShouldApplyNode(node, modifiedIds, path);

            var rect = vanillaGO.GetComponent<RectTransform>();
            if (rect != null && shouldApply)
            {
                // Back up the current transform and visual state before modifying
                var backup = new TransformBackup
                {
                    Path = path,
                    GO = vanillaGO,
                    AnchorMin = rect.anchorMin,
                    AnchorMax = rect.anchorMax,
                    Pivot = rect.pivot,
                    AnchoredPosition = rect.anchoredPosition,
                    SizeDelta = rect.sizeDelta,
                    OffsetMin = rect.offsetMin,
                    OffsetMax = rect.offsetMax,
                    LocalScale = rect.localScale,
                    WasActive = vanillaGO.activeSelf
                };

                // Back up Image visual state
                var img = vanillaGO.GetComponent<Image>();
                if (img != null)
                {
                    backup.HadImage = true;
                    backup.OriginalSprite = img.sprite;
                    backup.OriginalImageColor = img.color;
                    backup.OriginalImageType = img.type;
                    backup.OriginalPreserveAspect = img.preserveAspect;
                    backup.OriginalPixelsPerUnit = img.pixelsPerUnitMultiplier;
                    backup.OriginalFillCenter = img.fillCenter;
                }

                // Back up RawImage visual state
                var rawImg = vanillaGO.GetComponent<RawImage>();
                if (rawImg != null)
                {
                    backup.HadRawImage = true;
                    backup.OriginalRawTexture = rawImg.texture;
                    backup.OriginalRawImageColor = rawImg.color;
                }

                overrideEntry.Backups.Add(backup);

                // For the root node (first call, path is empty), do NOT apply the editor-normalized
                // transform from the layout data. The layout's root node stores the editor-workspace
                // transform (center-anchored fixed-size), not the runtime transform.
                // Instead, the root's position/anchoring is handled separately by
                // ApplyScreenPositionToRoot(), which applies the user's saved screen position
                // (from Preview -> Layout mode) or leaves vanilla anchoring intact if no
                // position was set. ScreenScale is also applied there.
                bool isRootNode = string.IsNullOrEmpty(path);
                if (!isRootNode)
                {
                    // Apply the layout node's transform to the vanilla GO
                    ApplyNodeToRect(rect, node);
                }

                // Apply visual properties (sprites, colors) from the layout node
                ApplyNodeVisuals(vanillaGO, node, img, rawImg);

                overrideEntry.MatchedCount++;
            }
            else if (rect != null && !shouldApply)
            {
                // Node exists but is unchanged from baseline - skip it.
                // We don't back up or modify this element at all.
            }

            // IMPORTANT: Do NOT force active state on vanilla-matched GameObjects.
            // The captured layout records Active=true because the UI was open at capture time.
            // If we force SetActive(true) here, UIs like the inventory screen become permanently
            // visible (bypassing InventoryGui.Show/Hide). The game's own code manages visibility
            // of its own hierarchy. We only modify transforms and visuals - never visibility of
            // existing vanilla elements.
            //
            // The only exception would be if the user explicitly deactivated a node in the editor
            // (Active=false when it was originally Active=true at capture). But since we don't
            // currently track "original captured active state" vs "editor-modified active state",
            // the safe default is to never touch active state on matched vanilla elements.

            // Recurse into children - match by parallel index + name verification
            if (node.Children == null || node.Children.Count == 0) return;

            // Track which vanilla child indices have already been claimed by a layout node.
            // This prevents two layout nodes with the same name (e.g., both named "icon"
            // inside different inventory slots) from matching the SAME vanilla child.
            // Without this, Strategy 2's name search always returns the first sibling
            // with a matching name, causing the second layout node to apply its transform
            // to the wrong GO (the first slot's icon instead of the second slot's icon).
            var claimedVanillaIndices = new HashSet<int>();

            for (int i = 0; i < node.Children.Count; i++)
            {
                var childNode = node.Children[i];
                if (childNode == null) continue;

                string childPath = string.IsNullOrEmpty(path)
                    ? childNode.Name
                    : path + "/" + childNode.Name;

                GameObject matchedChild = null;

                // Strategy 1: parallel index match - if the vanilla GO has a child at the same index
                // with the same name, that's our match (most common case for captured layouts)
                if (i < vanillaGO.transform.childCount && !claimedVanillaIndices.Contains(i))
                {
                    var candidate = vanillaGO.transform.GetChild(i).gameObject;
                    if (string.Equals(candidate.name, childNode.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedChild = candidate;
                        claimedVanillaIndices.Add(i);
                    }
                }

                // Strategy 2: search siblings by name, skipping already-claimed indices.
                // This handles cases where mod injection shifted child order (e.g., a mod
                // inserted an element at index 2, pushing vanilla children down by one).
                if (matchedChild == null)
                {
                    for (int childIndex = 0; childIndex < vanillaGO.transform.childCount; childIndex++)
                    {
                        if (claimedVanillaIndices.Contains(childIndex)) continue;
                        var candidate = vanillaGO.transform.GetChild(childIndex).gameObject;
                        if (string.Equals(candidate.name, childNode.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedChild = candidate;
                            claimedVanillaIndices.Add(childIndex);
                            break;
                        }
                    }
                }

                // Strategy 3: broader lookup by name (handles reparented elements).
                // Only used when no unclaimed sibling matched - avoids cross-parent mismatches.
                if (matchedChild == null && vanillaLookup.TryGetValue(childNode.Name, out var candidates))
                {
                    if (candidates.Count == 1)
                    {
                        matchedChild = candidates[0];
                    }
                    else if (candidates.Count > 1)
                    {
                        // Multiple matches - try to find an unclaimed one under the current parent
                        for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                        {
                            if (candidates[candidateIndex].transform.parent == vanillaGO.transform)
                            {
                                // Verify this specific GO wasn't already claimed
                                int sibIdx = candidates[candidateIndex].transform.GetSiblingIndex();
                                if (!claimedVanillaIndices.Contains(sibIdx))
                                {
                                    matchedChild = candidates[candidateIndex];
                                    claimedVanillaIndices.Add(sibIdx);
                                    break;
                                }
                            }
                        }
                        // Last resort: take the first candidate from any parent
                        if (matchedChild == null)
                            matchedChild = candidates[0];
                    }
                }

                if (matchedChild != null)
                {
                    ApplyNodeTransforms(childNode, matchedChild, vanillaLookup, overrideEntry, childPath, modifiedIds);
                }
                else
                {
                    // No vanilla match - this is a mod-injected element captured in the layout.
                    // Instantiate it from the layout data and parent it to the current vanilla GO
                    // so the captured mod UI survives even when the source mod is removed.
                    try
                    {
                        var injectedGO = UICanvasRenderer.Instantiate(
                            new UILayoutDefinition { RootElement = childNode },
                            vanillaGO.transform);
                        if (injectedGO != null)
                        {
                            overrideEntry.InjectedObjects.Add(injectedGO);
                            overrideEntry.InjectedCount++;

                            // Track elements the user explicitly deactivated in the editor.
                            // UICanvasRenderer.Instantiate respects node.Active, so the GO
                            // is already inactive. Record it so SyncInjectedVisibility
                            // doesn't force it visible when the vanilla root is shown.
                            if (!childNode.Active)
                            {
                                overrideEntry.UserDeactivatedObjects.Add(injectedGO);
                            }

                            if (overrideEntry.InjectedCount <= 20)
                            {
                                Debug.Log($"[UIVanillaOverride] Injected mod element '{childPath}' " +
                                    $"(type={childNode.Type}, active={childNode.Active}) into '{vanillaGO.name}'");
                            }
                        }
                        else
                        {
                            overrideEntry.UnmatchedCount++;
                            if (overrideEntry.UnmatchedCount <= 20)
                            {
                                Debug.LogWarning($"[UIVanillaOverride] Failed to inject mod element '{childPath}' " +
                                    $"(type={childNode.Type}, parent='{vanillaGO.name}')");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        overrideEntry.UnmatchedCount++;
                        if (overrideEntry.UnmatchedCount <= 20)
                        {
                            Debug.LogWarning($"[UIVanillaOverride] Error injecting mod element '{childPath}': {ex.Message}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Applies visual properties from a layout node to the vanilla GO's Image/RawImage components.
        /// This handles sprites (including cached modded sprites), colors, image type, etc.
        /// Only modifies visual properties that differ from the current state.
        /// </summary>
        private static void ApplyNodeVisuals(GameObject go, UIElementNode node, Image img, RawImage rawImg)
        {
            if (go == null || node == null) return;

            // Skip sprite/visual override if the GO has active mod MonoBehaviours
            // managing its visuals. The mod's own code handles sprites, colors, etc.
            // We still apply transform changes (handled in ApplyNodeToRect).
            if (HasActiveModBehaviours(go))
                return;

            // Skip visual override for game-managed dynamic content elements.
            // Inventory slot children (icon, amount, quality, equipped, etc.) have their
            // sprites and text set per-frame by InventoryGrid.UpdateGui(). Applying
            // captured visual data here would overwrite the game's dynamic state with
            // stale snapshot data (e.g., showing an item icon in an empty slot).
            if (IsDynamicContentNode(node))
                return;

            // Determine the sprite name to apply from the node's data.
            // ImageData is set for Image-type nodes; Style.BackgroundSprite is set for Panels/Buttons.
            string spriteName = null;
            Color? imageColor = null;
            int imageType = -1;
            bool? preserveAspect = null;
            float pixelsPerUnit = -1f;
            bool? fillCenter = null;

            if (node.ImageData != null)
            {
                spriteName = node.ImageData.SpriteName;
                imageColor = node.ImageData.Color.ToColor();
                imageType = node.ImageData.ImageType;
                preserveAspect = node.ImageData.PreserveAspect;
                pixelsPerUnit = node.ImageData.PixelsPerUnit;
                fillCenter = node.ImageData.FillCenter;
            }
            else if (node.Style != null && !string.IsNullOrEmpty(node.Style.BackgroundSprite))
            {
                spriteName = node.Style.BackgroundSprite;
                imageColor = node.Style.BackgroundColor.ToColor();
                imageType = node.Style.ImageType;
            }

            // Apply to Image component
            if (img != null && !string.IsNullOrEmpty(spriteName))
            {
                var sprite = ResolveSprite(spriteName);
                if (sprite != null)
                {
                    img.sprite = sprite;
                }
                else
                {
                    Debug.LogWarning($"[UIVanillaOverride] Failed to resolve sprite '{spriteName}' for Image on '{go.name}'");
                }
                if (imageColor.HasValue)
                    img.color = imageColor.Value;
                if (imageType >= 0)
                    img.type = (Image.Type)imageType;
                if (preserveAspect.HasValue)
                    img.preserveAspect = preserveAspect.Value;
                if (pixelsPerUnit > 0f)
                    img.pixelsPerUnitMultiplier = pixelsPerUnit;
                if (fillCenter.HasValue)
                    img.fillCenter = fillCenter.Value;
            }
            else if (img != null && imageColor.HasValue)
            {
                // No sprite name but we have color data (e.g., panel backgrounds)
                img.color = imageColor.Value;
                if (imageType >= 0)
                    img.type = (Image.Type)imageType;
            }

            // Apply Style color to Image only if neither ImageData nor sprite name provided
            // the color above. Avoids overwriting the precise ImageData.Color with
            // the less-accurate Style.BackgroundColor.
            if (img != null && string.IsNullOrEmpty(spriteName) && !imageColor.HasValue && node.Style != null)
            {
                img.color = node.Style.BackgroundColor.ToColor();
            }

            // Apply to RawImage component if no Image and we have image data
            if (rawImg != null && img == null && node.ImageData != null)
            {
                string rawTexName = node.ImageData.SpriteName;
                if (!string.IsNullOrEmpty(rawTexName))
                {
                    // RawImage uses Texture, not Sprite - resolve through the full
                    // sprite pipeline and extract the texture from the result
                    var resolvedSprite = ResolveSprite(rawTexName);
                    if (resolvedSprite != null && resolvedSprite.texture != null)
                    {
                        rawImg.texture = resolvedSprite.texture;
                    }
                    else
                    {
                        Debug.LogWarning($"[UIVanillaOverride] Failed to resolve RawImage texture '{rawTexName}' for '{go.name}'");
                    }
                }
                rawImg.color = node.ImageData.Color.ToColor();
            }
        }

        /// <summary>
        /// Resolves a sprite name from layout data to a live Sprite object.
        /// Resolution order:
        ///   1. Indexed bundle sprites (from live mod bundles + cached .bundle files)
        ///   2. All sprites loaded in memory (vanilla + active mod sprites)
        ///   3. Our asset bundle (VAMiscAssetManager)
        ///   4. Cached PNGs from disk (last resort - flat rasterized sprites)
        /// </summary>
        private static Sprite ResolveSprite(string spriteName)
        {
            if (string.IsNullOrEmpty(spriteName)) return null;

            // Strip "cached:" prefix early to get the real name for all lookups
            string lookupName = spriteName;
            if (UIBuilderAssetCache.IsCachedName(spriteName))
                lookupName = UIBuilderAssetCache.GetCacheId(spriteName);

            // Strip qualified "texture:sprite" format for plain lookups
            string plainName = lookupName;
            int colonIdx = lookupName.IndexOf(':');
            if (colonIdx > 0 && colonIdx < lookupName.Length - 1)
                plainName = lookupName.Substring(colonIdx + 1);

            // -- Priority 1: Indexed bundle sprites (real sprites with full metadata) --
            var bundleSprite = UIBuilderAssetCache.LoadSpriteFromCachedBundle(lookupName);
            if (bundleSprite != null)
                return bundleSprite;

            if (plainName != lookupName)
            {
                bundleSprite = UIBuilderAssetCache.LoadSpriteFromCachedBundle(plainName);
                if (bundleSprite != null)
                    return bundleSprite;
            }

            // -- Priority 2: All sprites loaded in memory (vanilla + active mod sprites) --
            var sprite = UICanvasRenderer.FindLoadedSpriteByName(lookupName);
            if (sprite != null)
                return sprite;

            if (plainName != lookupName)
            {
                sprite = UICanvasRenderer.FindLoadedSpriteByName(plainName);
                if (sprite != null)
                    return sprite;
            }

            // -- Priority 3: Our asset bundle + external files --
            sprite = VAMiscAssetManager.GetSprite(plainName);
            if (sprite != null)
                return sprite;

            if (plainName != lookupName)
            {
                sprite = VAMiscAssetManager.GetSprite(lookupName);
                if (sprite != null)
                    return sprite;
            }

            // -- Priority 4: Cached PNGs from disk (last resort) --
            if (UIBuilderAssetCache.IsCachedName(spriteName))
            {
                string cacheId = UIBuilderAssetCache.GetCacheId(spriteName);
                var cached = UIBuilderAssetCache.LoadCachedSprite(cacheId);
                if (cached != null)
                    return cached;
            }

            // Try by original name in cache manifest
            var cachedByName = UIBuilderAssetCache.LoadCachedSpriteByOriginalName(lookupName);
            if (cachedByName != null)
                return cachedByName;

            if (plainName != lookupName)
            {
                cachedByName = UIBuilderAssetCache.LoadCachedSpriteByOriginalName(plainName);
                if (cachedByName != null)
                    return cachedByName;
            }

            return null;
        }

        // ---------------------------------------
        //  Post-injection wiring - connect dead visual shells to live game systems
        // ---------------------------------------

        /// <summary>
        /// After injecting mod elements from layout data, this method walks all injected
        /// GameObjects and uses name-based context clues to connect them to live vanilla
        /// game systems (Minimap RenderTexture, Hud bars, status effect icons, etc.).
        /// Returns the number of elements successfully wired.
        /// </summary>
        private static int WireInjectedElements(List<GameObject> injectedObjects, GameObject vanillaRoot)
        {
            int wiredCount = 0;

            for (int i = 0; i < injectedObjects.Count; i++)
            {
                var go = injectedObjects[i];
                if (go == null) continue;

                wiredCount += WireElementRecursive(go, vanillaRoot);
            }

            return wiredCount;
        }

        private static int WireElementRecursive(GameObject go, GameObject vanillaRoot)
        {
            int count = 0;

            // Try to wire this specific element based on its name
            if (TryWireByName(go, vanillaRoot))
                count++;

            // Recurse into children
            for (int i = 0; i < go.transform.childCount; i++)
            {
                count += WireElementRecursive(go.transform.GetChild(i).gameObject, vanillaRoot);
            }

            return count;
        }

        /// <summary>
        /// Attempts to wire a single injected element to a live game system based on its name.
        /// Uses case-insensitive name matching with known patterns from common UI mods.
        /// Also checks element tags set in the UI Builder editor for explicit wiring semantics.
        /// </summary>
        private static bool TryWireByName(GameObject go, GameObject vanillaRoot)
        {
            if (go == null) return false;
            string name = go.name;
            string nameLower = name.ToLowerInvariant();

            // Check for a UIBuilderElementTag with an override wiring tag
            var elementTag = go.GetComponent<UIBuilderElementTag>();
            string tag = elementTag != null ? elementTag.ElementTag : null;

            try
            {
                // --- Tag-based wiring (highest priority) -------------
                if (!string.IsNullOrEmpty(tag))
                {
                    // Minimap display
                    if (string.Equals(tag, UIOverrideElementTags.MinimapDisplay, StringComparison.OrdinalIgnoreCase))
                    {
                        if (WireMinimapTexture(go)) return true;
                    }
                    // Minimap zoom
                    if (string.Equals(tag, UIOverrideElementTags.MinimapZoomIn, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(tag, UIOverrideElementTags.MinimapZoomOut, StringComparison.OrdinalIgnoreCase))
                    {
                        if (WireMinimapButton(go, tag.ToLowerInvariant())) return true;
                    }
                    // HUD bars
                    if (string.Equals(tag, UIOverrideElementTags.HealthBar, StringComparison.OrdinalIgnoreCase))
                        if (WireHudBarFill(go, nameLower, "health")) return true;
                    if (string.Equals(tag, UIOverrideElementTags.StaminaBar, StringComparison.OrdinalIgnoreCase))
                        if (WireHudBarFill(go, nameLower, "stamina")) return true;
                    if (string.Equals(tag, UIOverrideElementTags.EitrBar, StringComparison.OrdinalIgnoreCase))
                        if (WireHudBarFill(go, nameLower, "eitr")) return true;
                    if (string.Equals(tag, UIOverrideElementTags.FoodBar, StringComparison.OrdinalIgnoreCase))
                        if (WireHudBarFill(go, nameLower, "food")) return true;
                    if (string.Equals(tag, UIOverrideElementTags.GuardianBar, StringComparison.OrdinalIgnoreCase))
                        if (WireHudBarFill(go, nameLower, "guardian")) return true;
                }

                // --- AzuEPI elements - skip name-based wiring --------
                // These are handled entirely by UIOverrideAzuEPICompat.
                // We return false so they aren't double-wired by generic logic.
                if (UIOverrideElementTags.IsAzuEPIElement(name))
                    return false;

                // --- Player preview / camera display ------------------
                if (nameLower.Contains("playerpreview") || nameLower.Contains("player_preview") ||
                    nameLower.Contains("characterpreview") || nameLower.Contains("character_preview") ||
                    nameLower.Contains("paperdoll"))
                {
                    var rawImg = go.GetComponent<UnityEngine.UI.RawImage>();
                    if (rawImg != null)
                    {
                        var preview = go.GetComponent<UIOverridePlayerPreview>();
                        if (preview == null) preview = go.AddComponent<UIOverridePlayerPreview>();
                        Debug.Log($"[UIVanillaOverride] Wired player preview on '{go.name}'");
                        return true;
                    }
                }

                // --- Minimap wiring -----------------------------------
                // Modded minimap elements (e.g., MUIMap from Minimal UI)
                // need to display the actual minimap render texture
                if (nameLower.Contains("minimap") || nameLower.Contains("muimap") ||
                    nameLower.Contains("mapimage") || nameLower.Contains("map_image"))
                {
                    if (WireMinimapTexture(go))
                        return true;
                }

                // Minimap zoom/resize buttons
                if ((nameLower.Contains("map") || nameLower.Contains("minimap")) &&
                    (nameLower.Contains("zoom") || nameLower.Contains("resize") ||
                     nameLower.Contains("larger") || nameLower.Contains("smaller") ||
                     nameLower.Contains("plus") || nameLower.Contains("minus")))
                {
                    if (WireMinimapButton(go, nameLower))
                        return true;
                }

                // --- HUD bar wiring -----------------------------------
                // HP/Health bar elements
                if (nameLower.Contains("hp") || nameLower.Contains("health"))
                {
                    if (nameLower.Contains("bar") || nameLower.Contains("fill") || nameLower.Contains("gauge"))
                    {
                        if (WireHudBarFill(go, nameLower, "health"))
                            return true;
                    }
                }

                // Stamina bar elements
                if (nameLower.Contains("stamina"))
                {
                    if (nameLower.Contains("bar") || nameLower.Contains("fill") || nameLower.Contains("gauge"))
                    {
                        if (WireHudBarFill(go, nameLower, "stamina"))
                            return true;
                    }
                }

                // Eitr bar elements
                if (nameLower.Contains("eitr"))
                {
                    if (nameLower.Contains("bar") || nameLower.Contains("fill") || nameLower.Contains("gauge"))
                    {
                        if (WireHudBarFill(go, nameLower, "eitr"))
                            return true;
                    }
                }

                // --- Food bar wiring ----------------------------------
                if (nameLower.Contains("food") && nameLower.Contains("bar"))
                {
                    if (WireHudBarFill(go, nameLower, "food"))
                        return true;
                }

                // --- Guardian power bar wiring ------------------------
                if (nameLower.Contains("guardian") || nameLower.Contains("forsaken"))
                {
                    if (nameLower.Contains("bar") || nameLower.Contains("fill") || nameLower.Contains("cooldown"))
                    {
                        if (WireHudBarFill(go, nameLower, "guardian"))
                            return true;
                    }
                }

                // --- Weight icon wiring -------------------------------
                if (nameLower.Contains("weight") && (nameLower.Contains("icon") || nameLower.Contains("image")))
                {
                    return WireVanillaIcon(go, "weight");
                }

                // --- Text element wiring -----------------------------
                if (TryWireTextElement(go, nameLower))
                    return true;

                // --- Day/night cycle indicator ------------------------
                if (nameLower.Contains("daynight") || nameLower.Contains("day_night") ||
                    nameLower.Contains("timecycle") || nameLower.Contains("sunmoon"))
                {
                    return WireDayNightIndicator(go);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIVanillaOverride] Error wiring element '{name}': {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Wires a RawImage to Minimap.instance's render texture so the injected
        /// minimap element displays the actual live minimap.
        /// </summary>
        private static bool WireMinimapTexture(GameObject go)
        {
            if (Minimap.instance == null) return false;

            // Check for RawImage first (most minimap mods use RawImage to display the render texture)
            var rawImg = go.GetComponent<RawImage>();
            if (rawImg != null)
            {
                // The small minimap texture is available via the Minimap component
                // Try to get the render texture from Minimap's fields
                var mapTexField = typeof(Minimap).GetField("m_mapTexture",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (mapTexField != null)
                {
                    var tex = mapTexField.GetValue(Minimap.instance) as Texture;
                    if (tex != null)
                    {
                        rawImg.texture = tex;
                        AttachMinimapWirer(go, rawImg);
                        Debug.Log($"[UIVanillaOverride] Wired minimap RawImage '{go.name}' to Minimap.m_mapTexture");
                        return true;
                    }
                }

                // Fallback: try the small map render texture
                var smallMapField = typeof(Minimap).GetField("m_mapImageSmall",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (smallMapField != null)
                {
                    var smallMapImg = smallMapField.GetValue(Minimap.instance) as RawImage;
                    if (smallMapImg != null && smallMapImg.texture != null)
                    {
                        rawImg.texture = smallMapImg.texture;
                        rawImg.material = smallMapImg.material;
                        AttachMinimapWirer(go, rawImg);
                        Debug.Log($"[UIVanillaOverride] Wired minimap RawImage '{go.name}' to m_mapImageSmall.texture");
                        return true;
                    }
                }
            }

            // If it has children with RawImage, try wiring them
            var childRawImages = go.GetComponentsInChildren<RawImage>(true);
            for (int j = 0; j < childRawImages.Length; j++)
            {
                if (childRawImages[j].gameObject != go)
                {
                    if (WireMinimapTexture(childRawImages[j].gameObject))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Wires an injected minimap button to call Minimap zoom in/out functions.
        /// </summary>
        private static bool WireMinimapButton(GameObject go, string nameLower)
        {
            if (Minimap.instance == null) return false;

            var btn = go.GetComponent<Button>();
            if (btn == null) return false;

            // Clear any existing listeners from the builder instantiation
            btn.onClick.RemoveAllListeners();

            if (nameLower.Contains("larger") || nameLower.Contains("plus") || nameLower.Contains("zoomin") || nameLower.Contains("zoom_in"))
            {
                btn.onClick.AddListener(() =>
                {
                    if (Minimap.instance != null)
                    {
                        // Increase minimap zoom
                        var zoomField = typeof(Minimap).GetField("m_largeZoom",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (zoomField != null)
                        {
                            float current = (float)zoomField.GetValue(Minimap.instance);
                            zoomField.SetValue(Minimap.instance, Mathf.Max(0.01f, current * 0.75f));
                        }
                    }
                });
                Debug.Log($"[UIVanillaOverride] Wired minimap zoom-in button '{go.name}'");
                return true;
            }

            if (nameLower.Contains("smaller") || nameLower.Contains("minus") || nameLower.Contains("zoomout") || nameLower.Contains("zoom_out"))
            {
                btn.onClick.AddListener(() =>
                {
                    if (Minimap.instance != null)
                    {
                        var zoomField = typeof(Minimap).GetField("m_largeZoom",
                            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (zoomField != null)
                        {
                            float current = (float)zoomField.GetValue(Minimap.instance);
                            zoomField.SetValue(Minimap.instance, Mathf.Min(1f, current * 1.333f));
                        }
                    }
                });
                Debug.Log($"[UIVanillaOverride] Wired minimap zoom-out button '{go.name}'");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Wires an injected bar/fill element to show the corresponding HUD value.
        /// Attaches a UIOverrideBarUpdater MonoBehaviour for per-frame fill updates.
        /// Also applies appropriate colors and configures fill image type.
        /// </summary>
        private static bool WireHudBarFill(GameObject go, string nameLower, string barType)
        {
            var img = go.GetComponent<Image>();
            if (img == null) return false;

            // Determine bar type enum
            UIOverrideBarUpdater.BarType parsedBarType;
            switch (barType)
            {
                case "health":
                    parsedBarType = UIOverrideBarUpdater.BarType.Health;
                    img.color = new Color(0.8f, 0.2f, 0.2f, 1f);
                    break;
                case "stamina":
                    parsedBarType = UIOverrideBarUpdater.BarType.Stamina;
                    img.color = new Color(0.9f, 0.8f, 0.2f, 1f);
                    break;
                case "eitr":
                    parsedBarType = UIOverrideBarUpdater.BarType.Eitr;
                    img.color = new Color(0.4f, 0.6f, 0.9f, 1f);
                    break;
                case "food":
                    parsedBarType = UIOverrideBarUpdater.BarType.Food;
                    img.color = new Color(0.7f, 0.5f, 0.2f, 1f);
                    break;
                case "guardian":
                    parsedBarType = UIOverrideBarUpdater.BarType.GuardianPower;
                    img.color = new Color(0.6f, 0.4f, 0.9f, 1f);
                    break;
                default:
                    return false;
            }

            // Set as filled image if not already
            if (img.type != Image.Type.Filled && nameLower.Contains("fill"))
            {
                img.type = Image.Type.Filled;
                img.fillMethod = Image.FillMethod.Horizontal;
                img.fillAmount = 1f;
            }

            // Attach per-frame updater
            var updater = go.GetComponent<UIOverrideBarUpdater>();
            if (updater == null)
                updater = go.AddComponent<UIOverrideBarUpdater>();
            updater.Type = parsedBarType;
            updater.FillImage = img;

            // Try to find a sibling or child text element for value display
            var valueText = FindChildTextComponent(go);
            if (valueText == null && go.transform.parent != null)
                valueText = FindChildTextComponent(go.transform.parent.gameObject);
            updater.ValueText = valueText;

            // Try to find a slow-fill sibling (name containing "slow", "background", "bg")
            if (go.transform.parent != null)
            {
                for (int i = 0; i < go.transform.parent.childCount; i++)
                {
                    var sibling = go.transform.parent.GetChild(i).gameObject;
                    if (sibling == go) continue;
                    string sibName = sibling.name.ToLowerInvariant();
                    if (sibName.Contains("slow") || sibName.Contains("bg") || sibName.Contains("background"))
                    {
                        var slowImg = sibling.GetComponent<Image>();
                        if (slowImg != null && slowImg.type == Image.Type.Filled)
                        {
                            updater.SlowFillImage = slowImg;
                            break;
                        }
                    }
                }
            }

            Debug.Log($"[UIVanillaOverride] Wired {barType} bar updater on '{go.name}'");
            return true;
        }

        /// <summary>
        /// Wires a known vanilla icon by looking up the actual sprite from the game's loaded assets.
        /// </summary>
        private static bool WireVanillaIcon(GameObject go, string iconType)
        {
            var img = go.GetComponent<Image>();
            if (img == null) return false;

            Sprite foundSprite = null;
            switch (iconType)
            {
                case "weight":
                    foundSprite = UICanvasRenderer.FindLoadedSpriteByName("weight_icon")
                        ?? UICanvasRenderer.FindLoadedSpriteByName("mapicon_anchor")
                        ?? UICanvasRenderer.FindLoadedSpriteByName("inv_weight");
                    break;
            }

            if (foundSprite != null)
            {
                img.sprite = foundSprite;
                Debug.Log($"[UIVanillaOverride] Wired '{iconType}' icon on '{go.name}' to sprite '{foundSprite.name}'");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Placeholder for day/night cycle indicator wiring.
        /// </summary>
        private static bool WireDayNightIndicator(GameObject go)
        {
            // The day/night cycle visualization in Minimal UI uses Hud.instance
            // and EnvMan.instance to calculate sun position. This would need a
            // per-frame updater MonoBehaviour to animate properly.
            // For now, just ensure the element is visible and not a white box.
            var img = go.GetComponent<Image>();
            if (img != null && img.sprite == null)
            {
                // Apply a gradient or simple sun/moon color
                img.color = new Color(1f, 0.9f, 0.4f, 0.8f); // Warm sun color
                Debug.Log($"[UIVanillaOverride] Applied day/night indicator placeholder to '{go.name}'");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Attaches a UIOverrideMinimapWirer to keep the minimap texture synced per-frame.
        /// </summary>
        private static void AttachMinimapWirer(GameObject go, RawImage rawImg)
        {
            var wirer = go.GetComponent<UIOverrideMinimapWirer>();
            if (wirer == null)
                wirer = go.AddComponent<UIOverrideMinimapWirer>();
            wirer.MapImage = rawImg;
        }

        /// <summary>
        /// Attempts to wire a text element to a live game data source based on its name.
        /// Attaches a UIOverrideTextUpdater MonoBehaviour for per-frame text updates.
        /// </summary>
        private static bool TryWireTextElement(GameObject go, string nameLower)
        {
            var tmpText = go.GetComponent<TMP_Text>();
            var legacyText = tmpText == null ? go.GetComponent<UnityEngine.UI.Text>() : null;
            if (tmpText == null && legacyText == null) return false;

            UIOverrideTextUpdater.TextType? textType = null;

            // Health value text
            if ((nameLower.Contains("hp") || nameLower.Contains("health")) &&
                (nameLower.Contains("text") || nameLower.Contains("value") || nameLower.Contains("label") || nameLower.Contains("amount")))
            {
                textType = UIOverrideTextUpdater.TextType.HealthValue;
            }
            // Stamina value text
            else if (nameLower.Contains("stamina") &&
                (nameLower.Contains("text") || nameLower.Contains("value") || nameLower.Contains("label") || nameLower.Contains("amount")))
            {
                textType = UIOverrideTextUpdater.TextType.StaminaValue;
            }
            // Eitr value text
            else if (nameLower.Contains("eitr") &&
                (nameLower.Contains("text") || nameLower.Contains("value") || nameLower.Contains("label") || nameLower.Contains("amount")))
            {
                textType = UIOverrideTextUpdater.TextType.EitrValue;
            }
            // Weight text
            else if (nameLower.Contains("weight") &&
                (nameLower.Contains("text") || nameLower.Contains("value") || nameLower.Contains("label")))
            {
                textType = UIOverrideTextUpdater.TextType.Weight;
            }
            // Day/time text
            else if ((nameLower.Contains("day") || nameLower.Contains("time")) &&
                (nameLower.Contains("text") || nameLower.Contains("value") || nameLower.Contains("label") || nameLower.Contains("clock")))
            {
                textType = UIOverrideTextUpdater.TextType.DayTime;
            }
            // Biome text
            else if (nameLower.Contains("biome") &&
                (nameLower.Contains("text") || nameLower.Contains("value") || nameLower.Contains("label") || nameLower.Contains("name")))
            {
                textType = UIOverrideTextUpdater.TextType.Biome;
            }
            // Comfort text
            else if (nameLower.Contains("comfort") &&
                (nameLower.Contains("text") || nameLower.Contains("value") || nameLower.Contains("label")))
            {
                textType = UIOverrideTextUpdater.TextType.Comfort;
            }
            // Guardian/forsaken power text
            else if ((nameLower.Contains("guardian") || nameLower.Contains("forsaken")) &&
                (nameLower.Contains("text") || nameLower.Contains("value") || nameLower.Contains("label") || nameLower.Contains("cooldown")))
            {
                textType = UIOverrideTextUpdater.TextType.GuardianPower;
            }

            if (!textType.HasValue) return false;

            var updater = go.GetComponent<UIOverrideTextUpdater>();
            if (updater == null)
                updater = go.AddComponent<UIOverrideTextUpdater>();
            updater.Type = textType.Value;
            updater.Text = tmpText;
            updater.LegacyText = legacyText;

            Debug.Log($"[UIVanillaOverride] Wired text updater ({textType.Value}) on '{go.name}'");
            return true;
        }

        /// <summary>
        /// Searches a GameObject and its children for a TMP_Text component (for bar value display).
        /// </summary>
        private static TMP_Text FindChildTextComponent(GameObject go)
        {
            if (go == null) return null;

            var text = go.GetComponent<TMP_Text>();
            if (text != null) return text;

            for (int i = 0; i < go.transform.childCount; i++)
            {
                var childText = go.transform.GetChild(i).GetComponent<TMP_Text>();
                if (childText != null)
                {
                    string childName = go.transform.GetChild(i).name.ToLowerInvariant();
                    if (childName.Contains("text") || childName.Contains("value") ||
                        childName.Contains("label") || childName.Contains("amount"))
                        return childText;
                }
            }

            // Fallback: any TMP_Text in children
            return go.GetComponentInChildren<TMP_Text>(true);
        }

        /// <summary>
        /// Checks if a GameObject has active MonoBehaviours from a mod (not Unity, not Valheim, not ours).
        /// When true, we should skip visual overrides and let the mod manage its own sprites/colors.
        /// </summary>
        private static bool HasActiveModBehaviours(GameObject go)
        {
            if (go == null) return false;

            var behaviours = go.GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] == null || !behaviours[i].enabled) continue;

                Type type = behaviours[i].GetType();
                string typeNamespace = type.Namespace ?? "";
                string asmName = type.Assembly.GetName().Name ?? "";

                // Skip Unity engine types
                if (typeNamespace.StartsWith("UnityEngine", StringComparison.Ordinal)) continue;
                if (typeNamespace.StartsWith("TMPro", StringComparison.Ordinal)) continue;

                // Skip Valheim game types (assembly_valheim)
                if (asmName.IndexOf("valheim", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                // Skip our own types
                if (typeNamespace.StartsWith("VerdantsAscent", StringComparison.Ordinal)) continue;

                // This is a mod MonoBehaviour - the mod is managing this element
                return true;
            }

            return false;
        }

        /// <summary>
        /// Tags that identify game-managed dynamic content elements inside inventory grid slots.
        /// These elements have their sprites, text, and enabled state set per-frame by
        /// InventoryGrid.UpdateGui(). We must never overwrite their visuals during override.
        /// </summary>
        private static readonly HashSet<string> _dynamicContentTags =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "slot_icon",
                "slot_amount",
                "slot_quality",
                "slot_equipped",
                "slot_noteleport",
                "slot_food",
                "slot_binding",
                "slot_selected",
                "durability_bar",
                "selected_frame"
            };

        /// <summary>
        /// Checks if a layout node represents dynamic content that is managed per-frame
        /// by game code (e.g., inventory slot icons, amount text, quality indicators).
        /// When true, the override should apply transform changes but skip visual overrides
        /// to avoid overwriting the game's dynamic state with stale captured data.
        /// </summary>
        private static bool IsDynamicContentNode(UIElementNode node)
        {
            if (node == null) return false;

            // Check metadata flag set by NormalizeVanillaVisibility in the parser
            if (!string.IsNullOrEmpty(node.GetMeta("dynamic_content")))
                return true;

            // Check known dynamic tags directly (handles layouts that were captured
            // before the normalization pass was added)
            if (!string.IsNullOrEmpty(node.Tag) && _dynamicContentTags.Contains(node.Tag))
                return true;

            return false;
        }

        /// <summary>
        /// Checks if a layout node is tagged as override-protected.
        /// Override-protected nodes and their entire subtrees are skipped during override
        /// application - no transform changes, no visual changes, no injection.
        /// This allows users to tag modded UI panels (e.g., VNEI, EpicLoot panels) that
        /// were captured inside a vanilla hierarchy so the override doesn't duplicate or
        /// break them. The tag can be set via the Inspector's Tag field or context menu.
        /// </summary>
        private static bool IsOverrideProtected(UIElementNode node)
        {
            if (node == null) return false;

            // Check the Tag field directly
            if (!string.IsNullOrEmpty(node.Tag) &&
                string.Equals(node.Tag, OverrideProtectedTag, StringComparison.OrdinalIgnoreCase))
                return true;

            // Also check per-element metadata for a more granular flag
            if (!string.IsNullOrEmpty(node.GetMeta(OverrideProtectedTag)))
                return true;

            return false;
        }

        /// <summary>
        /// Applies a layout node's transform data to a live RectTransform.
        /// Uses the same stretch vs fixed-size logic as UICanvasRenderer.ApplyTransform.
        /// </summary>
        private static void ApplyNodeToRect(RectTransform rect, UIElementNode node)
        {
            rect.anchorMin = node.AnchorMin.ToVector2();
            rect.anchorMax = node.AnchorMax.ToVector2();
            rect.pivot = node.Pivot.ToVector2();

            bool isStretchX = !Mathf.Approximately(node.AnchorMin.X, node.AnchorMax.X);
            bool isStretchY = !Mathf.Approximately(node.AnchorMin.Y, node.AnchorMax.Y);

            if (isStretchX || isStretchY)
            {
                rect.offsetMin = node.OffsetMin.ToVector2();
                rect.offsetMax = node.OffsetMax.ToVector2();
            }
            else
            {
                rect.anchoredPosition = node.AnchoredPosition.ToVector2();
                rect.sizeDelta = node.SizeDelta.ToVector2();
            }

            if (node.Rotation != 0)
                rect.localRotation = Quaternion.Euler(0, 0, node.Rotation);
        }

        /// <summary>
        /// Applies the saved screen position and scale from the layout to the vanilla root GO.
        /// When a screen position is saved (from Preview -> Layout mode), the vanilla root is
        /// converted from its original anchoring to center-anchored with explicit canvas-size
        /// dimensions so it can be placed at the user-specified location. This is the runtime
        /// equivalent of ApplyScreenPositionToPreview in UIBuilderPreviewMode.
        /// When no screen position is saved, only ScreenScale is applied (if non-default).
        /// </summary>
        private static void ApplyScreenPositionToRoot(GameObject vanillaGO, UILayoutDefinition layout)
        {
            if (vanillaGO == null || layout == null) return;

            var rootRect = vanillaGO.GetComponent<RectTransform>();
            if (rootRect == null) return;

            float screenScale = Mathf.Clamp(layout.ScreenScale, 0.1f, 3f);

            if (layout.HasScreenPosition())
            {
                Vector2 canvasSize = GetEffectiveCanvasSize(layout);
                float canvasW = canvasSize.x;
                float canvasH = canvasSize.y;

                if (canvasW >= 1f && canvasH >= 1f)
                {
                    rootRect.anchorMin = new Vector2(0.5f, 0.5f);
                    rootRect.anchorMax = new Vector2(0.5f, 0.5f);
                    rootRect.pivot = new Vector2(0.5f, 0.5f);
                    rootRect.sizeDelta = canvasSize;

                    float localX = (layout.ScreenPositionX - 0.5f) * canvasW;
                    float localY = (layout.ScreenPositionY - 0.5f) * canvasH;
                    rootRect.anchoredPosition = new Vector2(localX, localY);
                    rootRect.localScale = new Vector3(screenScale, screenScale, 1f);

                    Debug.Log($"[UIVanillaOverride] Applied screen position ({layout.ScreenPositionX:F3}, {layout.ScreenPositionY:F3}) " +
                        $"scale={screenScale:F2} to root '{vanillaGO.name}'");
                }
            }
            else if (!Mathf.Approximately(screenScale, 1f))
            {
                // No saved position but non-default scale - apply scale only,
                // convert to center-anchored so children lay out correctly inside the scaled root.
                Vector2 canvasSize = GetEffectiveCanvasSize(layout);
                if (canvasSize.x >= 1f && canvasSize.y >= 1f)
                {
                    rootRect.anchorMin = new Vector2(0.5f, 0.5f);
                    rootRect.anchorMax = new Vector2(0.5f, 0.5f);
                    rootRect.pivot = new Vector2(0.5f, 0.5f);
                    rootRect.sizeDelta = canvasSize;
                    rootRect.anchoredPosition = Vector2.zero;
                }
                rootRect.localScale = new Vector3(screenScale, screenScale, 1f);
            }
        }

        /// <summary>
        /// Computes the effective canvas dimensions for the vanilla UI's parent canvas,
        /// using the same CanvasScaler math as UIBuilderPreviewMode.GetEffectiveCanvasSize.
        /// Uses the layout's CanvasSize as the reference resolution with a 0.5 matchWidthOrHeight.
        /// </summary>
        private static Vector2 GetEffectiveCanvasSize(UILayoutDefinition layout)
        {
            float screenW = Screen.width;
            float screenH = Screen.height;

            Vector2 refRes = layout != null ? layout.CanvasSize.ToVector2() : new Vector2(1920f, 1080f);
            if (refRes.x < 1f) refRes.x = 1920f;
            if (refRes.y < 1f) refRes.y = 1080f;

            float matchWidthOrHeight = 0.5f;
            float logW = Mathf.Log(screenW / refRes.x, 2f);
            float logH = Mathf.Log(screenH / refRes.y, 2f);
            float scaleFactor = Mathf.Pow(2f, Mathf.Lerp(logW, logH, matchWidthOrHeight));

            if (scaleFactor < 0.001f) scaleFactor = 1f;

            return new Vector2(screenW / scaleFactor, screenH / scaleFactor);
        }

        /// <summary>
        /// Builds a name->GameObjects lookup for all descendants of the given transform.
        /// Used for fallback matching when parallel tree walk fails.
        /// </summary>
        private static void BuildVanillaLookup(Transform root, Dictionary<string, List<GameObject>> lookup, string path)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                string childPath = string.IsNullOrEmpty(path) ? child.name : path + "/" + child.name;
                string name = child.name;

                if (!lookup.TryGetValue(name, out var list))
                {
                    list = new List<GameObject>();
                    lookup[name] = list;
                }
                list.Add(child.gameObject);

                BuildVanillaLookup(child, lookup, childPath);
            }
        }

        /// <summary>
        /// Counts all GameObjects in a hierarchy (including the root).
        /// </summary>
        private static int CountChildren(Transform root)
        {
            int count = 1;
            for (int i = 0; i < root.childCount; i++)
                count += CountChildren(root.GetChild(i));
            return count;
        }

        /// <summary>
        /// Forces Unity to rebuild the layout hierarchy so all RectTransform positions
        /// and layout groups recalculate.
        /// </summary>
        private static void ForceLayoutRebuild(GameObject root)
        {
            if (root == null) return;
            Canvas.ForceUpdateCanvases();
            var rects = root.GetComponentsInChildren<RectTransform>(true);
            for (int i = 0; i < rects.Length; i++)
                LayoutRebuilder.ForceRebuildLayoutImmediate(rects[i]);
        }

        /// <summary>
        /// Finds a vanilla UI GameObject by name/path.
        /// Tries multiple strategies: known VanillaTargets registry, direct name, path-based search.
        /// Logs a warning if the target is not found.
        /// </summary>
        private static GameObject FindVanillaTarget(string targetName)
        {
            var go = FindVanillaTargetQuiet(targetName);
            if (go == null && !string.IsNullOrEmpty(targetName))
                Debug.LogWarning($"[UIVanillaOverride] FindVanillaTarget: '{targetName}' not found in any strategy");
            return go;
        }

        /// <summary>
        /// Same as FindVanillaTarget but does not log warnings on failure.
        /// Used by ProcessPendingOverrides where failure is expected and normal
        /// (the target simply hasn't loaded yet).
        /// </summary>
        private static GameObject FindVanillaTargetQuiet(string targetName)
        {
            if (string.IsNullOrEmpty(targetName)) return null;

            // Track whether the targetName matched a known VanillaTarget/KnownTarget UID
            // OR a known GO name pattern. If so, we must NOT fall through to generic
            // GameObject.Find() - that would match the wrong "root" or other generic name.
            // Instead we return null and let ProcessPendingOverrides retry later when
            // the game singleton becomes available.
            bool isKnownTarget = false;

            // Strategy 1: Check known VanillaTargets registry by UID.
            // This is the most reliable method - uses the same FindRoot() delegate
            // that was used during capture, which accesses singleton references
            // (e.g., InventoryGui.instance.m_inventoryRoot) instead of generic
            // GameObject.Find() that can match the wrong "root" GO.
            for (int i = 0; i < UILayoutParser.VanillaTargets.Count; i++)
            {
                var target = UILayoutParser.VanillaTargets[i];
                if (string.Equals(target.UID, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    isKnownTarget = true;
                    try
                    {
                        var found = target.FindRoot();
                        if (found != null) return found;
                    }
                    catch { /* FindRoot may throw if game systems not ready */ }
                }
            }

            // Strategy 1b: Check VanillaTargets by the GO name their FindRoot() returns.
            // Handles legacy layouts that stored the raw GO name (e.g., "root") instead of
            // the VanillaTarget UID (e.g., "vanilla_inventory_root"). We call each target's
            // FindRoot() delegate and check if the returned GO's name matches targetName.
            // This uses the singleton-based lookup so we get the RIGHT "root", not a random one.
            if (!isKnownTarget)
            {
                for (int i = 0; i < UILayoutParser.VanillaTargets.Count; i++)
                {
                    var target = UILayoutParser.VanillaTargets[i];
                    try
                    {
                        var found = target.FindRoot();
                        if (found != null && string.Equals(found.name, targetName, StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.Log($"[UIVanillaOverride] Matched legacy target name '{targetName}' to VanillaTarget '{target.UID}' via FindRoot()");
                            return found;
                        }
                    }
                    catch { /* FindRoot returned null - singleton not ready. Mark as known so we don't
                               fall through to GameObject.Find which would match the wrong GO. */ }
                }

                // Even if all FindRoot() calls returned null, check if any VanillaTarget
                // is KNOWN to produce a GO with this name. If so, treat it as a known target
                // and don't fall through to the generic search strategies.
                // We do a lightweight check: try each FindRoot() and if it threw or returned null,
                // that's a signal the singleton isn't ready - but the target IS a known pattern.
                // We compare against the UID's leaf name and common GO name patterns.
                for (int i = 0; i < UILayoutParser.VanillaTargets.Count; i++)
                {
                    var target = UILayoutParser.VanillaTargets[i];
                    // Check if the VanillaTarget's UID ends with the target name
                    // (e.g., targetName="root" could belong to "vanilla_inventory_root")
                    // This is a heuristic - we only set the flag, not return a result.
                    // The actual GO will be found on a later retry when the singleton is ready.
                    string uidLower = target.UID.ToLowerInvariant();
                    string targetLower = targetName.ToLowerInvariant();
                    if (uidLower.EndsWith("_" + targetLower) || uidLower.EndsWith("/" + targetLower))
                    {
                        isKnownTarget = true;
                        break;
                    }
                }
            }

            // Strategy 2: Check known KnownTargets registry by UID.
            for (int i = 0; i < UILayoutParser.KnownTargets.Count; i++)
            {
                var target = UILayoutParser.KnownTargets[i];
                if (string.Equals(target.UID, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    isKnownTarget = true;
                    try
                    {
                        var found = target.FindRoot();
                        if (found != null) return found;
                    }
                    catch { }
                }
            }

            // Strategy 2b: Check KnownTargets by GO name (same legacy fallback as 1b).
            if (!isKnownTarget)
            {
                for (int i = 0; i < UILayoutParser.KnownTargets.Count; i++)
                {
                    var target = UILayoutParser.KnownTargets[i];
                    try
                    {
                        var found = target.FindRoot();
                        if (found != null && string.Equals(found.name, targetName, StringComparison.OrdinalIgnoreCase))
                        {
                            Debug.Log($"[UIVanillaOverride] Matched legacy target name '{targetName}' to KnownTarget '{target.UID}' via FindRoot()");
                            return found;
                        }
                    }
                    catch { }
                }
            }

            // If the target name matched a known VanillaTarget or KnownTarget (by UID or GO name
            // pattern) but FindRoot() returned null, the game singleton simply isn't ready yet.
            // Do NOT fall through to GameObject.Find - generic names like "root" will match the
            // wrong GO in the scene. Return null so ProcessPendingOverrides retries later.
            if (isKnownTarget)
                return null;

            // Strategy 3: Direct GameObject.Find (works for unique names and path-based names)
            // Only reached for targets that are NOT in the known VanillaTargets/KnownTargets
            // registries (e.g., discovered UIs, manually specified targets).
            var go = GameObject.Find(targetName);
            if (go != null) return go;

            // Strategy 4: Search by just the leaf name if targetName contains a path
            if (targetName.Contains("/"))
            {
                string leafName = targetName.Substring(targetName.LastIndexOf('/') + 1);
                go = GameObject.Find(leafName);
                if (go != null) return go;
            }

            // Strategy 5: Search all root GameObjects for a match (finds inactive GOs too)
            var rootObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            for (int i = 0; i < rootObjects.Length; i++)
            {
                var found = FindChildRecursive(rootObjects[i].transform, targetName);
                if (found != null) return found.gameObject;
            }

            return null;
        }

        private static Transform FindChildRecursive(Transform parent, string name)
        {
            if (parent.name == name) return parent;
            for (int i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                var found = FindChildShallow(child, name, 8);
                if (found != null) return found;
            }
            return null;
        }

        private static Transform FindChildShallow(Transform current, string name, int depth)
        {
            if (current.name == name) return current;
            if (depth <= 0) return null;
            for (int i = 0; i < current.childCount; i++)
            {
                var found = FindChildShallow(current.GetChild(i), name, depth - 1);
                if (found != null) return found;
            }
            return null;
        }

        // ---------------------------------------
        //  Persistence - survive session restart
        // ---------------------------------------

        private static void PersistOverrides()
        {
            try
            {
                Directory.CreateDirectory(OverrideConfigDir);
                var lines = new List<string>();
                foreach (var kvp in _activeOverrides)
                {
                    lines.Add($"{kvp.Key}|{kvp.Value.VanillaTargetName}");
                }
                File.WriteAllLines(OverrideConfigFile, lines.ToArray());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIVanillaOverride] Failed to persist overrides: {ex.Message}");
            }
        }

        private static void LoadPersistedOverrides()
        {
            if (!File.Exists(OverrideConfigFile)) return;

            try
            {
                var lines = File.ReadAllLines(OverrideConfigFile);
                int loaded = 0;
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = line.Split('|');
                    if (parts.Length < 2) continue;

                    string uid = parts[0].Trim();
                    string target = parts[1].Trim();

                    var layout = UILayoutCodex.Get(uid);
                    if (layout == null) continue;

                    string savedTarget = layout.GetMeta(MetaKeyTarget);
                    if (string.IsNullOrEmpty(savedTarget)) continue;

                    _activeOverrides[uid] = new ActiveOverride
                    {
                        LayoutUID = uid,
                        VanillaTargetName = target,
                        VanillaRoot = null,
                        CustomRoot = null,
                        WasVanillaActive = false
                    };
                    loaded++;
                }

                if (loaded > 0)
                    Debug.Log($"[UIVanillaOverride] Loaded {loaded} persisted override(s). Will apply when targets are available.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIVanillaOverride] Failed to load persisted overrides: {ex.Message}");
            }
        }
    }
}
