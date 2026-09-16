using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Wrapper for Valheim's native VisEquipment system on NPCs.
    /// Instead of reimplementing armor attachment, we configure and use the real VisEquipment component.
    /// This ensures attach_skin, bone binding, cloth physics, etc. all work exactly like players.
    /// 
    /// KEY INSIGHT: The body model MUST use Valheim's "Custom/Player" shader for armor to work.
    /// This shader has special properties like _ChestTex, _LegsTex, _SkinColor, _SkinBumpMap
    /// that VisEquipment uses to overlay armor textures on the body.
    /// 
    /// The prefab should be set up in Unity with the correct shader - no runtime swapping needed.
    /// </summary>
    public class NpcVisEquipment : MonoBehaviour
    {
        private VisEquipment _visEquipment;
        private ZNetView _nview;
        private bool _initialized;

        // When true, a model override is pending from the ZDO/SavedNpcManager.
        // DelayedInitialize will wait for the override to be applied before enabling VisEquipment.
        private bool _hasPendingModelOverride;

        // Cached player models from player prefab
        private static VisEquipment.PlayerModel[] _cachedPlayerModels;
        private static bool _playerModelsCached;

        // Cache equipment state to detect changes
        private Dictionary<CompanionInventory.EquipmentSlot, string> _lastEquipment = new();

        // Every NpcVisEquipment registers its underlying VisEquipment here so the
        // VisEquipmentBackWeaponRefreshPatch (below) can recognize our NPCs and drive their
        // back-weapon attach every frame. See that patch for why this is necessary.
        private static readonly Dictionary<VisEquipment, NpcVisEquipment> _managedVisEquipments
            = new Dictionary<VisEquipment, NpcVisEquipment>();

        internal static bool TryGetManaged(VisEquipment vis, out NpcVisEquipment npc)
            => _managedVisEquipments.TryGetValue(vis, out npc);

        /// <summary>
        /// Reads the desired back-weapon hashes from this NPC's synced ZDO (the same source
        /// vanilla UpdateEquipmentVisuals reads at the player-gated path). Returns false when
        /// there is no ZDO yet (hammer ghosts / pre-init), in which case no back refresh is done.
        /// </summary>
        internal bool TryGetDesiredBackHashes(out int leftBack, out int rightBack, out int leftBackVariant)
        {
            leftBack = 0;
            rightBack = 0;
            leftBackVariant = 0;
            var zdo = _nview != null ? _nview.GetZDO() : null;
            if (zdo == null) return false;
            leftBack = zdo.GetInt(ZDOVars.s_leftBackItem);
            rightBack = zdo.GetInt(ZDOVars.s_rightBackItem);
            leftBackVariant = zdo.GetInt(ZDOVars.s_leftBackItemVariant);
            return true;
        }

        /// <summary>
        /// Gets the underlying VisEquipment component.
        /// </summary>
        public VisEquipment VisEquipment => _visEquipment;
        
        /// <summary>
        /// Whether this NPC is initialized for player-like visuals (model switching, colors, etc.)
        /// </summary>
        public bool IsPlayerMode => _visEquipment?.m_isPlayer ?? false;

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
            NpcBodyMeshGuard.EnsureInstalled();
            
            // VisEquipment runs every frame and NREs before the skeleton and model table exist. Until DelayedInitialize
            // it is disabled, given an empty model table, has its equipment hashes cleared and its ZDO override removed,
            // so nothing attaches early.
            _visEquipment = GetComponent<VisEquipment>();
            if (_visEquipment != null)
            {
                // Prefab-default body mesh, captured before any swap can run — the last-resort
                // replacement when every model-table candidate has been rejected at skin time.
                if (_nativeBodyMesh == null && _visEquipment.m_bodyModel != null)
                    _nativeBodyMesh = _visEquipment.m_bodyModel.sharedMesh;

                // CRITICAL: Disable the component to prevent MonoUpdaters from calling
                // UpdateEquipmentVisuals() and GetModelIndex() before we're ready.
                // This is the ONLY reliable way to prevent NullReferenceExceptions when m_models is null.
                _visEquipment.enabled = false;

                // Check if the NpcController already knows about a pending model override.
                // If so, mark ourselves so DelayedInitialize knows to stay disabled until
                // the model override is applied (prevents the humanoid flash + shader errors).
                if (FiresCore.Bridge.NpcInteractionBridge.HasPendingModelOverride(gameObject))
                {
                    _hasPendingModelOverride = true;
                }

                // CRITICAL FIX: Initialize m_models to a single-element array IMMEDIATELY
                // CharacterAnimEvent.CustomLateUpdate() calls GetModelIndex() every frame regardless of
                // whether VisEquipment is enabled. GetModelIndex() accesses m_models.Length which throws
                // NullReferenceException if m_models is null. UpdateColors() then does m_models[index]
                // which throws IndexOutOfRangeException on an empty array.
                // A single dummy element prevents both NRE and IndexOutOfRange until SetupPlayerModels runs.
                if (_visEquipment.m_models == null || _visEquipment.m_models.Length < 2)
                {
                    // Seed with 2 null-mesh entries so ModelIndex=0 (male) and
                    // ModelIndex=1 (female) both stay in-bounds before real models
                    // are copied in from the player prefab by SetupPlayerModels().
                    // IsVisEquipmentReady() rejects null-mesh entries so UpdateColors
                    // is blocked until SetupPlayerModels() replaces these with real data.
                    _visEquipment.m_models = new VisEquipment.PlayerModel[2]
                    {
                        new VisEquipment.PlayerModel { m_mesh = null },
                        new VisEquipment.PlayerModel { m_mesh = null }
                    };
                }
                
                // Block ZDO reads FIRST (synchronous) — these gates are
                // what makes deferring ClearAllEquipmentHashes safe. With
                // m_nview = null + m_nViewOverride = null + enabled = false,
                // no UpdateEquipmentVisuals path can read ZDO or fire from
                // MonoUpdater. So clearing the hashes themselves can wait
                // one frame, getting that work off the spawn frame entirely.
                _originalNviewOverride = _visEquipment.m_nViewOverride;
                _visEquipment.m_nViewOverride = null;
                
                // Also set the internal m_nview to null via reflection if it exists
                // This prevents VisEquipment from reading equipment state from ZDO
                try
                {
                    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    var nviewField = typeof(VisEquipment).GetField("m_nview", flags);
                    if (nviewField != null)
                    {
                        _originalNview = nviewField.GetValue(_visEquipment) as ZNetView;
                        nviewField.SetValue(_visEquipment, null);
                    }
                }
                catch { }

                // Defer the hash-clearing pass to next frame. Even after
                // the #14 reflection caching, walking 28 FieldInfo.SetValue
                // calls per NPC × 4-12 NPCs per stream-in batch contributes
                // 5-10 ms to the spawn frame. With the four ZDO/MonoUpdater
                // gates above already in place (enabled=false, m_models
                // seeded, m_nView/m_nViewOverride nulled), there's no
                // consumer that can read the hash fields between now and
                // next frame's Invoke firing — DelayedInitialize is on a
                // 2-second Invoke timer, and all public mutators
                // (ApplyEquipment / SetModel / SetAppearance) check
                // _initialized first which only becomes true in
                // DelayedInitialize.
                Invoke(nameof(ClearAllEquipmentHashes), 0f);
            }
        }
        
        // Store original nview references to restore after initialization
        private ZNetView _originalNviewOverride;
        private ZNetView _originalNview;
        
        // VisEquipment hash and variant FieldInfos, resolved once for all instances instead of on every Awake.
        // A null entry means this game build doesn't have that field.
        private static System.Reflection.FieldInfo[] _cachedHashFields;
        private static System.Reflection.FieldInfo[] _cachedVariantFields;

        // Field names — never change between builds; static readonly so the
        // arrays don't allocate per ClearAllEquipmentHashes call.
        private static readonly string[] _hashFieldNames =
        {
            "m_leftItemHash", "m_rightItemHash", "m_chestItemHash",
            "m_legItemHash", "m_helmetItemHash", "m_shoulderItemHash",
            "m_utilityItemHash", "m_leftBackItemHash", "m_rightBackItemHash",
            "m_beardItemHash", "m_hairItemHash",
            "m_currentLeftItemHash", "m_currentRightItemHash",
            "m_currentChestItemHash", "m_currentLegItemHash",
            "m_currentHelmetItemHash", "m_currentShoulderItemHash",
            "m_currentUtilityItemHash", "m_currentLeftBackItemHash",
            "m_currentRightBackItemHash", "m_currentBeardItemHash",
            "m_currentHairItemHash",
        };
        private static readonly string[] _variantFieldNames =
        {
            "m_leftItemVariant", "m_leftBackItemVariant", "m_shoulderItemVariant",
            "m_currentLeftItemVariant", "m_currentLeftBackItemVariant",
            "m_currentShoulderItemVariant",
        };

        private static void EnsureFieldCachesPopulated()
        {
            if (_cachedHashFields != null && _cachedVariantFields != null) return;
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var visType = typeof(VisEquipment);

            if (_cachedHashFields == null)
            {
                _cachedHashFields = new System.Reflection.FieldInfo[_hashFieldNames.Length];
                for (int i = 0; i < _hashFieldNames.Length; i++)
                {
                    var field = visType.GetField(_hashFieldNames[i], flags);
                    _cachedHashFields[i] = (field != null && field.FieldType == typeof(int)) ? field : null;
                }
            }
            if (_cachedVariantFields == null)
            {
                _cachedVariantFields = new System.Reflection.FieldInfo[_variantFieldNames.Length];
                for (int i = 0; i < _variantFieldNames.Length; i++)
                {
                    var field = visType.GetField(_variantFieldNames[i], flags);
                    _cachedVariantFields[i] = (field != null && field.FieldType == typeof(int)) ? field : null;
                }
            }
        }

        /// <summary>Clears both the target and current equipment hashes so nothing attaches before the joints exist.</summary>
        private void ClearAllEquipmentHashes()
        {
            if (_visEquipment == null) return;

            try
            {
                EnsureFieldCachesPopulated();

                // Walk cached handles instead of doing 28 GetField lookups
                // per NPC spawn. See _cachedHashFields comment above for
                // why this matters at scale.
                for (int i = 0; i < _cachedHashFields.Length; i++)
                {
                    _cachedHashFields[i]?.SetValue(_visEquipment, 0);
                }
                for (int i = 0; i < _cachedVariantFields.Length; i++)
                {
                    _cachedVariantFields[i]?.SetValue(_visEquipment, 0);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcVisEquipment] Error clearing equipment hashes: {ex.Message}");
            }
        }

        private void Start()
        {
            // Delay initialization to ensure model is set up and server is fully loaded
            // Use a longer delay (2 seconds) to allow skeleton/joints to be fully ready
            Invoke(nameof(DelayedInitialize), 2.0f);
        }

        private void DelayedInitialize()
        {
            if (_initialized) return;

            // If a model override is pending, do NOT initialize yet.
            // The default humanoid VisEquipment would be enabled, run UpdateColors/UpdateVisuals
            // against the humanoid body, then immediately get torn down when the model override
            // replaces the Visual hierarchy. This causes the shader property errors and
            // IndexOutOfRangeException. Instead, ForceReinitialize() will be called after
            // ApplyModelOverride() finishes swapping the model.
            if (_hasPendingModelOverride)
            {
                _delayedInitRetries++;
                if (_delayedInitRetries >= MaxDelayedInitRetries)
                {
                    Debug.LogWarning($"[NpcVisEquipment] Model override was pending but never applied for {gameObject.name} after {MaxDelayedInitRetries} retries. Initializing anyway.");
                    _hasPendingModelOverride = false;
                    // Fall through to normal init
                }
                else
                {
                    if (VerboseLogging)
                        Debug.Log($"[NpcVisEquipment] Deferring init for {gameObject.name} - model override pending (attempt {_delayedInitRetries}/{MaxDelayedInitRetries})");
                    Invoke(nameof(DelayedInitialize), 1.0f);
                    return;
                }
            }

            // Verify the skeleton is ready before initializing
            if (!IsSkeletonReady())
            {
                _delayedInitRetries++;
                if (_delayedInitRetries >= MaxDelayedInitRetries)
                {
                    Debug.LogWarning($"[NpcVisEquipment] Max init retries ({MaxDelayedInitRetries}) reached for {gameObject.name}, skeleton never became ready. Component will not function properly.");
                    return;
                }
                
                if (VerboseLogging)
                    Debug.Log($"[NpcVisEquipment] Skeleton not ready for {gameObject.name}, retrying... (attempt {_delayedInitRetries}/{MaxDelayedInitRetries})");
                Invoke(nameof(DelayedInitialize), 1.0f);
                return;
            }
            
            Initialize();
        }
        
        // Track delayed init retries
        private int _delayedInitRetries = 0;
        private const int MaxDelayedInitRetries = 10;
        
        /// <summary>
        /// Checks if the skeleton/joints are ready for equipment attachment.
        /// Returns false if the model hierarchy isn't fully loaded yet.
        /// NOTE: This is a best-effort check - if we can't find expected transforms,
        /// we still return true after the component is initialized to prevent infinite loops.
        /// </summary>
        private bool IsSkeletonReady()
        {
            // If already initialized, skeleton was ready at some point - trust that
            if (_initialized) return true;
            
            // Find the Visual transform (or use root if not found)
            Transform visual = transform.Find("Visual") ?? transform;
            
            // Check for body model - this is the minimum requirement
            var bodyModel = visual.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (bodyModel == null) 
            {
                // No body model at all - definitely not ready
                return false;
            }
            
            // If body has bones, we're likely ready
            if (bodyModel.bones != null && bodyModel.bones.Length > 0) 
            {
                return true;
            }
            
            // Check for hand attachment points as fallback
            // Some models may not have bones array populated but still have transforms
            var rightHand = FindTransformRecursive(visual, "RightHand_Attach") 
                ?? FindTransformRecursive(visual, "RightHand")
                ?? FindTransformRecursive(visual, "Hand.R");
            var leftHand = FindTransformRecursive(visual, "LeftHand_Attach") 
                ?? FindTransformRecursive(visual, "LeftHand")
                ?? FindTransformRecursive(visual, "Hand.L");
                
            // If we found at least one hand, we're probably ready
            if (rightHand != null || leftHand != null) 
            {
                return true;
            }
            
            // Last resort: if there's any transform hierarchy under Visual, assume ready
            // This prevents infinite loops when the model structure is different than expected
            if (visual.childCount > 0)
            {
                return true;
            }
            
            return false;
        }

        /// <summary>
        /// Forces reinitialization of the visual equipment system.
        /// Call this after the NPC's Visual hierarchy has been recreated.
        /// </summary>
        public void ForceReinitialize()
        {
            _initialized = false;
            _lastEquipment.Clear();

            // Clear the pending model override flag - the model has been applied,
            // so Initialize() should now evaluate the new model's shader/body.
            _hasPendingModelOverride = false;

            // CRITICAL: Disable VisEquipment during reinitialization to prevent NullReferenceExceptions
            if (_visEquipment != null)
            {
                _visEquipment.enabled = false;
            }
            
            // Reset eye overlay state
            if (_eyeOverlayObject != null)
            {
                UnityEngine.Object.Destroy(_eyeOverlayObject);
                _eyeOverlayObject = null;
            }
            
            if (_eyeMaterialInstance != null)
            {
                UnityEngine.Object.Destroy(_eyeMaterialInstance);
                _eyeMaterialInstance = null;
            }
            
            _lastBodyMesh = null;
            _visEquipmentStable = false;
            _visEquipmentStableTimer = 0f;
            // Keep _eyeColorPending if it was set, so the color will be applied after stability
            
            // CRITICAL: Clear VisEquipment's internal equipment instance references
            // These may point to destroyed GameObjects after a model change, causing NREs
            // when trying to remove them from LODGroups
            ClearVisEquipmentInstances();

            // CRITICAL: Null out all attachment point references. After a model override,
            // these point to destroyed transforms. ConfigureVisEquipment() only sets them
            // if they're currently null, so we must clear them here.
            if (_visEquipment != null)
            {
                _visEquipment.m_bodyModel = null;
                _visEquipment.m_leftHand = null;
                _visEquipment.m_rightHand = null;
                _visEquipment.m_helmet = null;
                _visEquipment.m_backShield = null;
                _visEquipment.m_backMelee = null;
                _visEquipment.m_backTwohandedMelee = null;
                _visEquipment.m_backBow = null;
                _visEquipment.m_backTool = null;
                _visEquipment.m_backAtgeir = null;
                _visEquipment.m_clothColliders = null;
            }

            Initialize();
            Debug.Log($"[NpcVisEquipment] Force reinitialized for {gameObject.name}");
        }
        
        /// <summary>
        /// Clears VisEquipment's internal equipment instance references.
        /// This prevents NullReferenceExceptions when equipping new items after a model change,
        /// as the old instance references may point to destroyed GameObjects.
        /// </summary>
        private void ClearVisEquipmentInstances()
        {
            if (_visEquipment == null) return;
            
            try
            {
                // Use reflection to clear internal instance fields
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var visType = typeof(VisEquipment);
                
                // Clear all equipment instance fields
                string[] instanceFields = {
                    "m_leftItemInstance",
                    "m_rightItemInstance", 
                    "m_helmetItemInstance",
                    "m_leftBackItemInstance",
                    "m_rightBackItemInstance",
                    "m_chestItemInstances",  // This is a List<GameObject>
                    "m_legItemInstances",    // This is a List<GameObject>
                    "m_shoulderItemInstances", // This is a List<GameObject>
                    "m_utilityItemInstances",  // This is a List<GameObject>
                    "m_beardItemInstance",
                    "m_hairItemInstance"
                };
                
                foreach (var fieldName in instanceFields)
                {
                    var field = visType.GetField(fieldName, flags);
                    if (field != null)
                    {
                        var currentValue = field.GetValue(_visEquipment);
                        
                        // Destroy any existing GameObjects
                        if (currentValue is GameObject go && go != null)
                        {
                            UnityEngine.Object.Destroy(go);
                        }
                        else if (currentValue is List<GameObject> goList)
                        {
                            foreach (var item in goList)
                            {
                                if (item != null)
                                {
                                    UnityEngine.Object.Destroy(item);
                                }
                            }
                            goList.Clear();
                        }
                        
                        // Set to null for single GameObjects
                        if (field.FieldType == typeof(GameObject))
                        {
                            field.SetValue(_visEquipment, null);
                        }
                    }
                }
                
                // Also clear the hash values so VisEquipment knows to recreate visuals
                string[] hashFields = {
                    "m_currentLeftItemHash",
                    "m_currentRightItemHash",
                    "m_currentChestItemHash",
                    "m_currentLegItemHash",
                    "m_currentHelmetItemHash",
                    "m_currentShoulderItemHash",
                    "m_currentUtilityItemHash",
                    "m_currentLeftBackItemHash",
                    "m_currentRightBackItemHash",
                    "m_currentBeardItemHash",
                    "m_currentHairItemHash"
                };
                
                foreach (var fieldName in hashFields)
                {
                    var field = visType.GetField(fieldName, flags);
                    if (field != null && field.FieldType == typeof(int))
                    {
                        field.SetValue(_visEquipment, 0);
                    }
                }
                
                if (VerboseLogging)
                    Debug.Log($"[NpcVisEquipment] Cleared VisEquipment internal instances for {gameObject.name}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcVisEquipment] Error clearing VisEquipment instances: {ex.Message}");
            }
        }

        /// <summary>
        /// Initializes the visual equipment system by finding or creating a VisEquipment component.
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;

            try
            {
                // Try to find existing VisEquipment
                _visEquipment = GetComponent<VisEquipment>();

                if (_visEquipment == null)
                {
                    // Need to add VisEquipment - but we need to configure it properly first
                    _visEquipment = gameObject.AddComponent<VisEquipment>();
                    // Keep it disabled until we're done configuring
                    _visEquipment.enabled = false;
                }

                // Configure VisEquipment for NPC use (with player features enabled)
                ConfigureVisEquipment();

                // CRITICAL: Restore ZDO access now that we're fully initialized
                // We blocked ZDO access in Awake() to prevent premature equipment attachment
                RestoreZdoAccess();

                // CRITICAL: Only re-enable VisEquipment if m_bodyModel is valid with a sharedMesh
                // AND the body uses the Custom/Player shader. Non-humanoid model overrides (e.g. Boar)
                // use Standard shader which doesn't have the texture properties (_SkinBumpMap, _ChestTex,
                // _LegsTex) that UpdateColors() tries to set, causing errors every frame.
                bool bodyModelValid = _visEquipment.m_bodyModel != null 
                    && _visEquipment.m_bodyModel.sharedMesh != null;
                bool shaderCompatible = bodyModelValid
                    && _visEquipment.m_bodyModel.sharedMaterial != null
                    && _visEquipment.m_bodyModel.sharedMaterial.shader != null
                    && IsPlayerCompatibleShader(_visEquipment.m_bodyModel.sharedMaterial.shader.name);
                _visEquipment.enabled = bodyModelValid && shaderCompatible;

                _initialized = true;
                if (_visEquipment != null)
                    _managedVisEquipments[_visEquipment] = this;
                if (VerboseLogging)
                    Debug.Log($"[NpcVisEquipment] Initialized VisEquipment for {gameObject.name}, m_isPlayer={_visEquipment.m_isPlayer}, bodyValid={bodyModelValid}, shaderOK={shaderCompatible}");
                if (!bodyModelValid)
                    Debug.Log($"[NpcVisEquipment] VisEquipment kept disabled for {gameObject.name} \u2014 no valid body model (model override may be non-humanoid)");
                else if (!shaderCompatible)
                    Debug.Log($"[NpcVisEquipment] VisEquipment kept disabled for {gameObject.name} \u2014 body shader is not Custom/Player (model override uses incompatible shader)");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NpcVisEquipment] Failed to initialize: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        /// <summary>
        /// Restores ZDO access to VisEquipment after initialization is complete.
        /// This was blocked in Awake() to prevent premature equipment attachment.
        /// </summary>
        private void RestoreZdoAccess()
        {
            if (_visEquipment == null) return;
            
            // Restore m_nViewOverride
            if (_originalNviewOverride != null || _nview != null)
            {
                _visEquipment.m_nViewOverride = _originalNviewOverride ?? _nview;
            }
            
            // Restore m_nview via reflection
            if (_originalNview != null)
            {
                try
                {
                    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    var nviewField = typeof(VisEquipment).GetField("m_nview", flags);
                    if (nviewField != null)
                    {
                        nviewField.SetValue(_visEquipment, _originalNview);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Configures the VisEquipment component for NPC use.
        /// Sets up required references that VisEquipment needs.
        /// IMPORTANT: We set m_isPlayer = true to enable model switching and color support.
        /// </summary>
        private void ConfigureVisEquipment()
        {
            if (_visEquipment == null) return;

            // Find the Visual transform
            Transform visual = transform.Find("Visual") ?? transform;

            // Find body model (SkinnedMeshRenderer named "body")
            if (_visEquipment.m_bodyModel == null)
            {
                var bodyTransform = FindTransformRecursive(visual, "body");
                if (bodyTransform != null)
                {
                    _visEquipment.m_bodyModel = bodyTransform.GetComponent<SkinnedMeshRenderer>();
                }

                // Fallback to any SMR on an active child (skip hidden original visuals)
                if (_visEquipment.m_bodyModel == null)
                {
                    foreach (var skinnedRenderer in visual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    {
                        if (skinnedRenderer == null) continue;
                        // Skip renderers under hidden original visuals from model override
                        bool isUnderHidden = false;
                        Transform check = skinnedRenderer.transform;
                        while (check != null && check != visual)
                        {
                            if (!check.gameObject.activeSelf && check.name.StartsWith("__OriginalVisual_"))
                            {
                                isUnderHidden = true;
                                break;
                            }
                            check = check.parent;
                        }
                        if (!isUnderHidden)
                        {
                            _visEquipment.m_bodyModel = skinnedRenderer;
                            break;
                        }
                    }
                }
            }

            // Set up attachment points if not already set
            if (_visEquipment.m_leftHand == null)
                _visEquipment.m_leftHand = FindTransformRecursive(visual, "LeftHand_Attach") 
                    ?? FindTransformRecursive(visual, "LeftHand");

            if (_visEquipment.m_rightHand == null)
                _visEquipment.m_rightHand = FindTransformRecursive(visual, "RightHand_Attach") 
                    ?? FindTransformRecursive(visual, "RightHand");

            if (_visEquipment.m_helmet == null)
                _visEquipment.m_helmet = FindTransformRecursive(visual, "Helmet_attach") 
                    ?? FindTransformRecursive(visual, "Head");

            if (_visEquipment.m_backShield == null)
                _visEquipment.m_backShield = FindTransformRecursive(visual, "BackShield_attach");

            if (_visEquipment.m_backMelee == null)
                _visEquipment.m_backMelee = FindTransformRecursive(visual, "BackMelee_attach");

            if (_visEquipment.m_backTwohandedMelee == null)
                _visEquipment.m_backTwohandedMelee = FindTransformRecursive(visual, "BackTwohandedMelee_attach") 
                    ?? FindTransformRecursive(visual, "BackTwoHanded_attach");

            if (_visEquipment.m_backBow == null)
                _visEquipment.m_backBow = FindTransformRecursive(visual, "BackBow_attach");

            if (_visEquipment.m_backTool == null)
                _visEquipment.m_backTool = FindTransformRecursive(visual, "BackTool_attach");

            if (_visEquipment.m_backAtgeir == null)
                _visEquipment.m_backAtgeir = FindTransformRecursive(visual, "BackAtgeir_attach");

            // Find cloth colliders
            if (_visEquipment.m_clothColliders == null || _visEquipment.m_clothColliders.Count == 0)
            {
                var clothColliderTransform = FindTransformRecursive(visual, "ClothCollider");
                if (clothColliderTransform != null)
                {
                    _visEquipment.m_clothColliders = new List<MagicaCloth2.ColliderComponent>(
                        clothColliderTransform.GetComponentsInChildren<MagicaCloth2.ColliderComponent>(true));
                }
                else
                {
                    _visEquipment.m_clothColliders = new List<MagicaCloth2.ColliderComponent>();
                }
            }

            // Armor overlays need the body on Custom/Player. Static NPC prefabs skip CompanionPrefabManager's load-time
            // fix, so the Player prefab's material is copied here. Headless servers skip it: no shaders load there, and
            // each client repairs its own body material from the synced appearance.
            if (_visEquipment.m_bodyModel != null
                && SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                string currentShader = _visEquipment.m_bodyModel.sharedMaterial?.shader?.name ?? "null";
                if (!IsPlayerCompatibleShader(currentShader))
                {
                    TryFixBodyShader(_visEquipment.m_bodyModel);
                    // Re-check after fix attempt
                    currentShader = _visEquipment.m_bodyModel.sharedMaterial?.shader?.name ?? "null";
                    if (!IsPlayerCompatibleShader(currentShader))
                    {
                        Debug.LogWarning($"[NpcVisEquipment] Body shader fix failed for {gameObject.name}. Current: {currentShader} (need Custom/Player or Custom/FiresPlayer)");
                    }
                    else
                    {
                        Debug.Log($"[NpcVisEquipment] Fixed body shader to {currentShader} for {gameObject.name}");
                    }
                }
                else if (VerboseLogging)
                {
                    Debug.Log($"[NpcVisEquipment] Body shader verified: {currentShader}");
                }
            }
            
            // Copy player models from the player prefab
            // This is required for model switching (male/female) and hair/beard to work
            SetupPlayerModels();
            
            // Keep m_isPlayer = false by default to prevent interference with armor textures.
            // We temporarily enable it only when setting model/hair/beard via SetAppearance().
            _visEquipment.m_isPlayer = false;

            // Set ZNetView override if needed (allows VisEquipment to work without its own ZNetView)
            if (_visEquipment.m_nViewOverride == null && _nview != null)
            {
                _visEquipment.m_nViewOverride = _nview;
            }

            if (VerboseLogging)
                Debug.Log($"[NpcVisEquipment] Configured VisEquipment - bodyModel: {_visEquipment.m_bodyModel?.name ?? "null"}, " +
                $"rightHand: {_visEquipment.m_rightHand?.name ?? "null"}, " +
                $"leftHand: {_visEquipment.m_leftHand?.name ?? "null"}, " +
                $"helmet: {_visEquipment.m_helmet?.name ?? "null"}, " +
                $"clothColliders: {_visEquipment.m_clothColliders?.Count ?? 0}, " +
                $"models: {_visEquipment.m_models?.Length ?? 0}, " +
                $"m_isPlayer: {_visEquipment.m_isPlayer}, " +
                $"bodyMaterials: {_visEquipment.m_bodyModel?.materials?.Length ?? 0}");
        }
        
        /// <summary>
        /// Sets up the player models array by copying from the player prefab.
        /// This is required for model switching (male/female) to work.
        /// </summary>
        private void SetupPlayerModels()
        {
            if (_visEquipment == null) return;
            
            // If already has models, don't override
            if (_visEquipment.m_models != null && _visEquipment.m_models.Length >= 2)
            {
                if (VerboseLogging)
                    Debug.Log($"[NpcVisEquipment] Already has {_visEquipment.m_models.Length} models configured");
                return;
            }
            
            // Try to get cached player models
            if (!_playerModelsCached)
            {
                CachePlayerModels();
            }
            
            if (_cachedPlayerModels != null && _cachedPlayerModels.Length >= 2)
            {
                _visEquipment.m_models = _cachedPlayerModels;
                if (VerboseLogging)
                    Debug.Log($"[NpcVisEquipment] Applied {_cachedPlayerModels.Length} cached player models");
            }
            else
            {
                // Fallback: Create models from the current body mesh
                CreateModelsFromCurrentBody();
            }
        }
        
        /// <summary>
        /// Caches player models from the player prefab for reuse.
        /// </summary>
        private static void CachePlayerModels()
        {
            if (_playerModelsCached) return;
            
            try
            {
                // Try to find the player prefab
                var playerPrefab = ZNetScene.instance?.GetPrefab("Player");
                if (playerPrefab == null)
                {
                    // Try from local player
                    if (Player.m_localPlayer != null)
                    {
                        var playerVisEquip = Player.m_localPlayer.GetComponent<VisEquipment>();
                        if (playerVisEquip != null && playerVisEquip.m_models != null && playerVisEquip.m_models.Length >= 2)
                        {
                            _cachedPlayerModels = playerVisEquip.m_models;
                            _playerModelsCached = true;
                            if (VerboseLogging)
                                Debug.Log($"[NpcVisEquipment] Cached {_cachedPlayerModels.Length} player models from local player");
                            return;
                        }
                    }
                    
                    Debug.LogWarning("[NpcVisEquipment] Could not find player prefab for model caching");
                    return;
                }
                
                var visEquip = playerPrefab.GetComponent<VisEquipment>();
                if (visEquip != null && visEquip.m_models != null && visEquip.m_models.Length >= 2)
                {
                    _cachedPlayerModels = visEquip.m_models;
                    _playerModelsCached = true;
                    if (VerboseLogging)
                        Debug.Log($"[NpcVisEquipment] Cached {_cachedPlayerModels.Length} player models from prefab");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcVisEquipment] Failed to cache player models: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Creates a minimal models array from the current body mesh.
        /// This is a fallback when player models aren't available.
        /// </summary>
        private void CreateModelsFromCurrentBody()
        {
            if (_visEquipment?.m_bodyModel == null) return;
            
            var bodyMesh = _visEquipment.m_bodyModel.sharedMesh;
            var bodyMaterial = _visEquipment.m_bodyModel.sharedMaterial;
            
            if (bodyMesh == null || bodyMaterial == null) return;
            
            // Create a single model entry (male only fallback)
            _visEquipment.m_models = new VisEquipment.PlayerModel[]
            {
                new VisEquipment.PlayerModel
                {
                    m_mesh = bodyMesh,
                    m_baseMaterial = bodyMaterial
                }
            };
            
            Debug.Log("[NpcVisEquipment] Created fallback models array from current body");
        }

        /// <summary>
        /// Applies equipment visuals to the NPC using Valheim's native VisEquipment system.
        /// </summary>
        // Verbose logging flag - set to true for debugging equipment issues
        public static bool VerboseLogging = false;

        /// <summary>
        /// True for shaders the player equipment pipeline can drive, meaning they expose the textures
        /// VisEquipment.UpdateColors writes: vanilla Custom/Player and FiresTossinShade's Custom/FiresPlayer. Leaving
        /// FiresPlayer out kept VisEquipment disabled on swapped bodies, so companions appeared unequipped.
        /// </summary>
        internal static bool IsPlayerCompatibleShader(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return false;
            return shaderName == "Custom/Player"
                || shaderName == "Custom/FiresPlayer";
        }
        
        // Retry tracking to prevent infinite retry loops
        private int _retryCount = 0;
        private const int MaxRetries = 5;
        
        public void ApplyEquipment(Dictionary<CompanionInventory.EquipmentSlot, string> equipment)
        {
            // If not initialized yet, try to initialize first
            if (!_initialized)
            {
                if (VerboseLogging)
                    Debug.Log($"[NpcVisEquipment] ApplyEquipment called before initialization, initializing now for {gameObject.name}");
                Initialize();
            }

            // After initialization attempt, check if VisEquipment is available
            if (_visEquipment == null)
            {
                Debug.LogWarning($"[NpcVisEquipment] VisEquipment not available for {gameObject.name}");
                return;
            }
            
            // Check if skeleton is ready before applying equipment
            // This prevents NullReferenceException in VisEquipment.AttachItem when joints don't exist yet
            if (!IsSkeletonReady())
            {
                // Check retry limit to prevent infinite loops
                if (_retryCount >= MaxRetries)
                {
                    Debug.LogWarning($"[NpcVisEquipment] Max retries ({MaxRetries}) reached for {gameObject.name}, skeleton never became ready. Proceeding anyway.");
                    // Don't return - proceed with equipment application even if skeleton check fails
                    // The underlying VisEquipment may still work
                    _pendingEquipment = null;
                    _retryCount = 0;
                }
                else
                {
                    if (VerboseLogging)
                        Debug.Log($"[NpcVisEquipment] Skeleton not ready for equipment on {gameObject.name}, deferring... (retry {_retryCount + 1}/{MaxRetries})");
                        
                    // Store equipment and retry later
                    _pendingEquipment = equipment;
                    if (!IsInvoking(nameof(RetryApplyEquipment)))
                    {
                        Invoke(nameof(RetryApplyEquipment), 1.0f);
                    }
                    return;
                }
            }
            
            // Reset retry count on success
            _retryCount = 0;
            
            // Clear pending equipment since we're applying now
            _pendingEquipment = null;
            
            // OPTIMIZATION: Only update slots that have changed
            // This prevents flickering and unnecessary visual recreation
            bool anyChanges = false;
            int changedCount = 0;
            
            // Check for changes and only update what's different
            var allSlots = (CompanionInventory.EquipmentSlot[])
                Enum.GetValues(typeof(CompanionInventory.EquipmentSlot));
            
            foreach (var slot in allSlots)
            {
                string newValue = "";
                equipment.TryGetValue(slot, out newValue);
                newValue = newValue ?? "";
                
                string oldValue = "";
                _lastEquipment.TryGetValue(slot, out oldValue);
                oldValue = oldValue ?? "";
                
                if (newValue != oldValue)
                {
                    anyChanges = true;
                    changedCount++;
                    
                    // Only log changes when verbose logging is enabled
                    if (VerboseLogging && !string.IsNullOrEmpty(newValue))
                    {
                        Debug.Log($"[NpcVisEquipment] Slot changed: {slot} '{oldValue}' -> '{newValue}'");
                    }
                    
                    // Update only the changed slot
                    SetEquipmentSlot(slot, newValue);
                }
            }
            
            // If this is the first time applying equipment (empty _lastEquipment), force update all
            if (_lastEquipment.Count == 0 && equipment.Count > 0)
            {
                anyChanges = true;
                
                // First-time application - need to clear hashes to force visual creation
                ClearVisEquipmentCurrentHashes();
                
                // Apply all equipment
                foreach (var kvp in equipment)
                {
                    SetEquipmentSlot(kvp.Key, kvp.Value);
                }
                
                if (VerboseLogging)
                {
                    Debug.Log($"[NpcVisEquipment] First-time equipment application for {gameObject.name}, applied {equipment.Count} slots");
                }
            }
            
            // Update the cached state
            _lastEquipment = new Dictionary<CompanionInventory.EquipmentSlot, string>(equipment);
            
            // Only log if there were actual changes and verbose logging is enabled
            if (anyChanges && VerboseLogging)
            {
                Debug.Log($"[NpcVisEquipment] Applied {changedCount} equipment changes for {gameObject.name}");
            }
        }
        
        // Store pending equipment for deferred application
        private Dictionary<CompanionInventory.EquipmentSlot, string> _pendingEquipment;
        
        /// <summary>
        /// Retries applying pending equipment after skeleton becomes ready.
        /// Increments retry counter to prevent infinite loops.
        /// </summary>
        private void RetryApplyEquipment()
        {
            if (_pendingEquipment != null)
            {
                _retryCount++;
                
                // Only log if verbose logging is enabled
                if (VerboseLogging)
                    Debug.Log($"[NpcVisEquipment] Retrying equipment application for {gameObject.name} (attempt {_retryCount}/{MaxRetries})");
                    
                ApplyEquipment(_pendingEquipment);
            }
        }
        
        /// <summary>
        /// Clears VisEquipment's internal "current" hash values.
        /// This forces the next UpdateEquipmentVisuals() call to recreate all visual GameObjects,
        /// even if the "desired" hashes match what was loaded from ZDO.
        /// 
        /// This is the key fix for equipment not showing on world load/restart.
        /// </summary>
        private void ClearVisEquipmentCurrentHashes()
        {
            if (_visEquipment == null) return;
            
            try
            {
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var visType = typeof(VisEquipment);
                
                // Clear only the "current" hash fields - these track what visuals are currently displayed
                // The "m_xxxItemHash" fields (without "current") track what SHOULD be displayed
                string[] currentHashFields = {
                    "m_currentLeftItemHash",
                    "m_currentRightItemHash",
                    "m_currentChestItemHash",
                    "m_currentLegItemHash",
                    "m_currentHelmetItemHash",
                    "m_currentShoulderItemHash",
                    "m_currentUtilityItemHash",
                    "m_currentLeftBackItemHash",
                    "m_currentRightBackItemHash"
                };
                
                int clearedCount = 0;
                foreach (var fieldName in currentHashFields)
                {
                    var field = visType.GetField(fieldName, flags);
                    if (field != null && field.FieldType == typeof(int))
                    {
                        field.SetValue(_visEquipment, -1); // Use -1 to force mismatch
                        clearedCount++;
                    }
                }
                
                if (clearedCount > 0 && VerboseLogging)
                {
                    Debug.Log($"[NpcVisEquipment] Cleared {clearedCount} current hash fields to force visual recreation for {gameObject.name}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcVisEquipment] Failed to clear current hashes: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets a specific equipment slot using VisEquipment's native methods.
        /// </summary>
        private void SetEquipmentSlot(CompanionInventory.EquipmentSlot slot, string prefabName)
        {
            if (_visEquipment == null) return;

            string name = prefabName ?? "";
            
            // Only log non-empty slots when verbose logging is enabled
            if (!string.IsNullOrEmpty(name) && VerboseLogging)
            {
                Debug.Log($"[NpcVisEquipment] SetEquipmentSlot: {slot} = '{name}' on {gameObject.name}");
            }

            switch (slot)
            {
                case CompanionInventory.EquipmentSlot.Helmet:
                    VisEquipmentCompat.SetHelmetItem(_visEquipment, name);
                    break;

                case CompanionInventory.EquipmentSlot.Chest:
                    VisEquipmentCompat.SetChestItem(_visEquipment, name);
                    break;

                case CompanionInventory.EquipmentSlot.Legs:
                    VisEquipmentCompat.SetLegItem(_visEquipment, name);
                    break;

                case CompanionInventory.EquipmentSlot.Shoulder:
                    VisEquipmentCompat.SetShoulderItem(_visEquipment, name, 0);
                    break;

                case CompanionInventory.EquipmentSlot.Utility:
                    VisEquipmentCompat.SetUtilityItem(_visEquipment, name);
                    break;

                case CompanionInventory.EquipmentSlot.RightHand:
                    VisEquipmentCompat.SetRightItem(_visEquipment, name);
                    break;

                case CompanionInventory.EquipmentSlot.LeftHand:
                    VisEquipmentCompat.SetLeftItem(_visEquipment, name, 0);
                    break;

                case CompanionInventory.EquipmentSlot.RightBack:
                    VisEquipmentCompat.SetRightBackItem(_visEquipment, name);
                    break;

                case CompanionInventory.EquipmentSlot.LeftBack:
                    VisEquipmentCompat.SetLeftBackItem(_visEquipment, name, 0);
                    break;

                default:
                    Debug.LogWarning($"[NpcVisEquipment] Unknown equipment slot: {slot}");
                    break;
            }
        }

        /// <summary>
        /// Clears a specific equipment slot.
        /// </summary>
        public void ClearSlot(CompanionInventory.EquipmentSlot slot)
        {
            SetEquipmentSlot(slot, "");
            _lastEquipment.Remove(slot);
        }

        /// <summary>
        /// Clears all equipment from the NPC.
        /// </summary>
        public void ClearAllEquipment()
        {
            if (_visEquipment == null) return;

            VisEquipmentCompat.SetHelmetItem(_visEquipment, "");
            VisEquipmentCompat.SetChestItem(_visEquipment, "");
            VisEquipmentCompat.SetLegItem(_visEquipment, "");
            VisEquipmentCompat.SetShoulderItem(_visEquipment, "", 0);
            VisEquipmentCompat.SetUtilityItem(_visEquipment, "");
            VisEquipmentCompat.SetRightItem(_visEquipment, "");
            VisEquipmentCompat.SetLeftItem(_visEquipment, "", 0);
            VisEquipmentCompat.SetRightBackItem(_visEquipment, "");
            VisEquipmentCompat.SetLeftBackItem(_visEquipment, "", 0);

            _lastEquipment.Clear();
            Debug.Log("[NpcVisEquipment] Cleared all equipment");
        }

        /// <summary>
        /// Forces a visual update on the equipment.
        /// </summary>
        public void ForceVisualUpdate()
        {
            // VisEquipment updates automatically via IMonoUpdater
            // But we can trigger an immediate update by reapplying current equipment
            if (_lastEquipment.Count > 0)
            {
                ApplyEquipment(_lastEquipment);
            }
        }
        
        #region Model and Color Methods
        
        // Eye material system - uses custom eye materials that overlay on the body mesh
        // These are full player body materials with everything but the eyes made transparent
        // FiresEyes = male model, FiresEyesFem = female model
        private const string EyeMaterialMale = "FiresEyes";
        private const string EyeMaterialFemale = "FiresEyesFem";
        private static Material _cachedEyeMaterialMale;
        private static Material _cachedEyeMaterialFemale;
        private static bool _eyeMaterialsCached;
        private static bool _eyeShaderWarningLogged; // Only log shader warning once per session
        private Material _eyeMaterialInstance;
        private GameObject _eyeOverlayObject;
        private Color _eyeColor = new Color(0.4f, 0.3f, 0.2f); // Default brown
        private bool _eyeColorPending; // True if eye color was set before we could attach overlay
        private Mesh _lastBodyMesh; // Track body mesh to detect model changes
        private bool _visEquipmentStable; // True after VisEquipment has completed first update
        private float _visEquipmentStableTimer; // Timer to wait for stability
        
        /// <summary>
        /// Called every frame to monitor VisEquipment state and update eye overlay if needed.
        /// </summary>
        private void LateUpdate()
        {
            // Wait for VisEquipment to stabilize before allowing eye overlay
            if (!_visEquipmentStable && _initialized && _visEquipment != null)
            {
                _visEquipmentStableTimer += Time.deltaTime;
                // Wait 1 second for VisEquipment to complete its initial updates
                if (_visEquipmentStableTimer >= 1.0f)
                {
                    _visEquipmentStable = true;
                    
                    // If eye color was set while waiting, apply it now
                    if (_eyeColorPending)
                    {
                        _eyeColorPending = false;
                        AttachEyeOverlayDelayed();
                    }
                }
            }
            
            // Monitor for body mesh changes (model switching)
            if (_visEquipmentStable && _eyeOverlayObject != null && _visEquipment?.m_bodyModel != null)
            {
                var currentMesh = _visEquipment.m_bodyModel.sharedMesh;
                if (currentMesh != _lastBodyMesh && currentMesh != null)
                {
                    // Body mesh changed - need to update eye overlay
                    _lastBodyMesh = currentMesh;
                    UpdateEyeOverlayMesh();
                }
            }
        }
        
        /// <summary>
        /// Updates the eye overlay mesh to match the current body mesh.
        /// Called when the body mesh changes (e.g., gender switch).
        /// </summary>
        private void UpdateEyeOverlayMesh()
        {
            if (_eyeOverlayObject == null) return;
            if (_visEquipment?.m_bodyModel == null) return;
            
            var overlayRenderer = _eyeOverlayObject.GetComponent<SkinnedMeshRenderer>();
            if (overlayRenderer == null) return;
            
            try
            {
                overlayRenderer.sharedMesh = _visEquipment.m_bodyModel.sharedMesh;
                overlayRenderer.bones = _visEquipment.m_bodyModel.bones;
                overlayRenderer.rootBone = _visEquipment.m_bodyModel.rootBone;
                overlayRenderer.localBounds = _visEquipment.m_bodyModel.localBounds;
                
                // Reapply color in case material was affected
                ApplyEyeColorToMaterial();
                
                Debug.Log($"[NpcVisEquipment] Updated eye overlay mesh for {gameObject.name}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcVisEquipment] Failed to update eye overlay mesh: {ex.Message}");
            }
        }
        
        // The model index last passed to VisEquipment.SetModel, so the repeated identical calls during a spawn skip the
        // surrounding color and hash work. -1 means never set.
        private int _lastSetModelIndex = -1;

        /// <summary>Sets the body model (0 male, 1 female) by swapping the body mesh directly.</summary>
        public void SetModel(int modelIndex)
        {
            // CRITICAL: Don't run on hammer ghosts - they don't have valid ZDOs
            // This prevents NullReferenceException in VisEquipment.SetModel()
            if (_nview == null || !_nview.IsValid() || _nview.GetZDO() == null) return;

            if (!_initialized) Initialize();
            if (_visEquipment == null) return;

            // Ensure we have models to switch between
            if (_visEquipment.m_models == null || _visEquipment.m_models.Length < 2)
            {
                Debug.LogWarning($"[NpcVisEquipment] Cannot set model - only {_visEquipment.m_models?.Length ?? 0} models available. Trying to set up models...");
                SetupPlayerModels();

                if (_visEquipment.m_models == null || _visEquipment.m_models.Length < 2)
                {
                    Debug.LogWarning($"[NpcVisEquipment] Still cannot set model after setup attempt");
                    return;
                }
            }

            // Clamp early so the dedup check below sees the resolved value.
            modelIndex = Mathf.Clamp(modelIndex, 0, _visEquipment.m_models.Length - 1);

            // Skip redundant calls. The spawn pipeline can fire SetModel
            // 3-4 times per NPC with the same index (default, loadout
            // restore, ZDO restore, randomiser). Subsequent calls were
            // ticking UpdateColors + body-mesh reassignment for no visual
            // change — pure waste. Mesh is already correct after the first
            // call so this is safe to short-circuit.
            if (modelIndex == _lastSetModelIndex) return;

            // Apply the model switch directly — do NOT set m_isPlayer=true.
            // Setting m_isPlayer=true causes UpdateBaseModel() to replace the body material with
            // model.m_baseMaterial (which may be Standard shader) and UpdateColors() to crash on
            // player-only arrays that are never populated for our NPCs (VisEquipment.Start() blocked).
            // UpdateEquipmentVisuals() (hair/beard) does NOT require m_isPlayer=true.
            _visEquipment.SetModel(modelIndex); // stores index in ZDO
            var model = _visEquipment.m_models[modelIndex];
            if (model != null && model.m_mesh != null)
                TryAssignBodyMesh(model.m_mesh, modelIndex);
            _lastSetModelIndex = modelIndex;
            if (VerboseLogging)
                Debug.Log($"[NpcVisEquipment] Set model index to {modelIndex}");
        }
        
        private static readonly HashSet<string> _meshAssignLogged = new HashSet<string>();
        private Mesh _nativeBodyMesh;

        /// <summary>
        /// Swaps the body mesh only onto a rig that can skin it, and never onto a pairing Unity already rejected this
        /// session. The prefab's own model-table mesh is tried first because it is the only one guaranteed to match a
        /// baked rig; the live Player mesh is a legacy fallback. Returns true when a mesh was assigned.
        /// </summary>
        private bool TryAssignBodyMesh(Mesh mesh, int modelIndex = -1)
        {
            var bodyRenderer = _visEquipment != null ? _visEquipment.m_bodyModel : null;
            if (bodyRenderer == null) return false;

            Mesh live = null;
            if (modelIndex >= 0)
            {
                try
                {
                    var playerPrefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Player") : null;
                    var playerVis = playerPrefab != null ? playerPrefab.GetComponent<VisEquipment>() : null;
                    if (playerVis != null && playerVis.m_models != null && modelIndex < playerVis.m_models.Length)
                        live = playerVis.m_models[modelIndex].m_mesh;
                }
                catch { }
            }

            Mesh chosen = null;
            string source = "none";
            if (NpcBodyMeshGuard.IsAssignable(mesh, bodyRenderer)) { chosen = mesh; source = ReferenceEquals(mesh, live) ? "table(live)" : "table"; }
            else if (live != null && !ReferenceEquals(live, mesh) && NpcBodyMeshGuard.IsAssignable(live, bodyRenderer)) { chosen = live; source = "vanilla-player"; }

            var probe = chosen ?? mesh;
            if (probe != null && _meshAssignLogged.Add($"{gameObject.name}|{probe.name}|{modelIndex}"))
            {
                int boneCount = bodyRenderer.bones != null ? bodyRenderer.bones.Length : 0;
                var bindposes = probe.bindposes;
                Debug.Log($"[NpcVisEquipment] body mesh '{probe.name}' (idx={modelIndex}, src={source}) on {gameObject.name}: " +
                          $"smrBones={boneCount} bindposes={(bindposes != null ? bindposes.Length : 0)} verts={probe.vertexCount} → {(chosen != null ? "assign" : "SKIP (no skinnable candidate)")}");
            }

            if (chosen == null) return false;
            if (!ReferenceEquals(bodyRenderer.sharedMesh, chosen)) bodyRenderer.sharedMesh = chosen;
            return true;
        }

        /// <summary>
        /// Called by NpcBodyMeshGuard when Unity reports the skin-time rejection for a mesh named
        /// <paramref name="meshName"/> on a renderer GameObject named <paramref name="goName"/>. If this
        /// NPC's body renderer is the one holding that mesh, blacklist the pairing and swap to the best
        /// remaining candidate (own model-table mesh → live Player mesh → the prefab's original body
        /// mesh) so the body renders again instead of staying invisible. Also works on undressed
        /// ghost/preview clones — no ZDO or Initialize() required. Returns true when this instance was
        /// healed.
        /// </summary>
        internal bool HealRejectedBodyMesh(string meshName, string goName)
        {
            try
            {
                var visEquipment = _visEquipment != null ? _visEquipment : GetComponent<VisEquipment>();
                var bodyRenderer = visEquipment != null ? visEquipment.m_bodyModel : null;
                if (bodyRenderer == null) bodyRenderer = GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (bodyRenderer == null || bodyRenderer.sharedMesh == null) return false;
                if (bodyRenderer.gameObject.name != goName || bodyRenderer.sharedMesh.name != meshName) return false;

                var bad = bodyRenderer.sharedMesh;
                NpcBodyMeshGuard.MarkRejected(bad, bodyRenderer);

                // The table/live entries the rejected mesh was standing in for, matched by name so
                // ghost clones (no ZDO model index) resolve too.
                Mesh table = FindModelMeshByName(visEquipment != null ? visEquipment.m_models : null, meshName);
                Mesh live = null;
                try
                {
                    var playerPrefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Player") : null;
                    var playerVis = playerPrefab != null ? playerPrefab.GetComponent<VisEquipment>() : null;
                    live = FindModelMeshByName(playerVis != null ? playerVis.m_models : null, meshName);
                }
                catch { }

                Mesh replacement = null;
                string source = null;
                if (table != null && !ReferenceEquals(table, bad) && NpcBodyMeshGuard.IsAssignable(table, bodyRenderer)) { replacement = table; source = "table"; }
                else if (live != null && !ReferenceEquals(live, bad) && NpcBodyMeshGuard.IsAssignable(live, bodyRenderer)) { replacement = live; source = "vanilla-player"; }
                else if (_nativeBodyMesh != null && !ReferenceEquals(_nativeBodyMesh, bad) && NpcBodyMeshGuard.IsAssignable(_nativeBodyMesh, bodyRenderer)) { replacement = _nativeBodyMesh; source = "native-default"; }

                if (replacement == null) return false;
                bodyRenderer.sharedMesh = replacement;
                NpcBodyMeshGuard.LogOnce(
                    $"heal|{GetInstanceID()}|{meshName}",
                    $"[NpcBodyMeshGuard] '{meshName}' rejected by Unity at skin time on {gameObject.name} — swapped body to '{replacement.name}' ({source}); pairing blacklisted for this session.");
                return true;
            }
            catch { return false; }
        }

        private static Mesh FindModelMeshByName(VisEquipment.PlayerModel[] models, string meshName)
        {
            if (models == null) return null;
            foreach (var model in models)
            {
                if (model != null && model.m_mesh != null && model.m_mesh.name == meshName) return model.m_mesh;
            }
            return null;
        }

        private void ReattachEyeOverlay()
        {
            // Destroy old overlay and material instance
            if (_eyeOverlayObject != null)
            {
                UnityEngine.Object.Destroy(_eyeOverlayObject);
                _eyeOverlayObject = null;
            }
            
            if (_eyeMaterialInstance != null)
            {
                UnityEngine.Object.Destroy(_eyeMaterialInstance);
                _eyeMaterialInstance = null;
            }
            
            _lastBodyMesh = null;
            
            // Reattach with current color (will pick correct gendered material)
            // Only if VisEquipment is stable
            if (_visEquipmentStable)
            {
                AttachEyeOverlayDelayed();
            }
            else
            {
                _eyeColorPending = true;
            }
        }
        
        /// <summary>
        /// Sets the skin color for this NPC.
        /// 
        /// We apply the skin color directly to the body material's _SkinColor property
        /// rather than using VisEquipment.SetSkinColor() which triggers UpdateColors()
        /// and can interfere with armor textures.
        /// </summary>
        public void SetSkinColor(Vector3 color)
        {
            if (!_initialized) Initialize();
            if (_visEquipment == null) return;
            
            // Store for our own reference
            _skinColor = new Color(color.x, color.y, color.z);
            _skinColorSet = true;
            
            // Apply directly to the body material
            ApplySkinColorToMaterial();
        }
        
        /// <summary>
        /// Sets the skin color for this NPC using a Color struct.
        /// </summary>
        public void SetSkinColor(Color color)
        {
            SetSkinColor(new Vector3(color.r, color.g, color.b));
        }
        
        // Stored colors
        private Color _skinColor = new Color(1f, 0.87f, 0.77f); // Default fair skin
        private bool _skinColorSet = false;
        private Color _hairColor = new Color(0.3f, 0.2f, 0.15f); // Default brown hair
        private bool _hairColorSet = false;
        
        /// <summary>
        /// Sets the hair color for this NPC.
        /// This also affects beard color.
        /// 
        /// We apply the hair color directly to the body material's _HairColor property
        /// and also to hair/beard mesh materials.
        /// </summary>
        public void SetHairColor(Vector3 color)
        {
            if (!_initialized) Initialize();
            if (_visEquipment == null) return;
            
            // Store for our own reference
            _hairColor = new Color(color.x, color.y, color.z);
            _hairColorSet = true;
            
            // Apply directly to the body and hair materials
            ApplyHairColorToMaterials();
        }
        
        /// <summary>
        /// Sets the hair color for this NPC using a Color struct.
        /// </summary>
        public void SetHairColor(Color color)
        {
            SetHairColor(new Vector3(color.r, color.g, color.b));
        }
        
        /// <summary>
        /// Applies the stored skin color directly to the body material.
        /// This avoids VisEquipment.UpdateColors() which can interfere with armor.
        /// </summary>
        private void ApplySkinColorToMaterial()
        {
            if (_visEquipment?.m_bodyModel == null) return;
            
            try
            {
                // Get the body material (use .material to get instance, not shared)
                var bodyMaterial = _visEquipment.m_bodyModel.material;
                if (bodyMaterial == null) return;
                
                // Apply skin color to the Custom/Player shader property
                if (bodyMaterial.HasProperty("_SkinColor"))
                {
                    bodyMaterial.SetColor("_SkinColor", _skinColor);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcVisEquipment] Failed to apply skin color: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Applies the stored hair color to hair/beard meshes and body material.
        /// </summary>
        private void ApplyHairColorToMaterials()
        {
            if (_visEquipment == null) return;

            try
            {
                // Mirror Valheim's VisEquipment.UpdateColors():
                // set _HairColor on every hair/beard SkinnedMeshRenderer found under Visual,
                // then also set it on the body material.
                Transform visual = transform.Find("Visual") ?? transform;

                foreach (var renderer in visual.GetComponentsInChildren<Renderer>(true))
                {
                    string rendererName = renderer.gameObject.name.ToLowerInvariant();
                    bool isHairOrBeard = rendererName.StartsWith("hair") || rendererName.StartsWith("beard")
                                     || rendererName.StartsWith("npc_hair_") || rendererName.StartsWith("npc_beard_");
                    if (!isHairOrBeard) continue;

                    // _HairColor is not in the shader's Properties block so HasProperty returns false -
                    // call SetColor unconditionally, exactly as VisEquipment.UpdateColors() does.
                    var mats = renderer.materials;
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] == null) continue;
                        mats[i].SetColor("_HairColor", _hairColor);
                    }
                    renderer.materials = mats;
                }

                // Body material - same as UpdateColors() setting _HairColor on m_baseMaterial
                if (_visEquipment.m_bodyModel != null)
                {
                    var bodyMat = _visEquipment.m_bodyModel.material;
                    if (bodyMat != null)
                        bodyMat.SetColor("_HairColor", _hairColor);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcVisEquipment] Failed to apply hair color: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Re-applies stored colors after equipment changes or model updates.
        /// Call this after hair/beard items are changed to ensure colors persist.
        /// </summary>
        public void ReapplyColors()
        {
            if (_skinColorSet)
            {
                ApplySkinColorToMaterial();
            }
            
            if (_hairColorSet)
            {
                // Delay slightly to allow hair/beard meshes to be created
                Invoke(nameof(ApplyHairColorToMaterials), 0.1f);
            }
        }
        
        /// <summary>
        /// Sets the eye color for this NPC.
        /// Uses the emission texture captured from the prefab before shader swap.
        /// Creates an eye overlay mesh that renders on top of the body with the eye texture tinted.
        /// </summary>
        public void SetEyeColor(Color color)
        {
            if (!_initialized) Initialize();
            
            _eyeColor = color;
            
            // Don't attach overlay until VisEquipment is stable
            if (!_visEquipmentStable)
            {
                _eyeColorPending = true;
                return;
            }
            
            // Attach eye overlay if not already attached
            if (_eyeOverlayObject == null)
            {
                AttachEyeOverlayDelayed();
            }
            else
            {
                // Apply the color to the existing eye material
                ApplyEyeColorToMaterial();
            }
        }
        
        /// <summary>
        /// Sets the eye color for this NPC using a Vector3.
        /// </summary>
        public void SetEyeColor(Vector3 color)
        {
            SetEyeColor(new Color(color.x, color.y, color.z));
        }
        
        /// <summary>
        /// Gets the current eye color.
        /// </summary>
        public Color GetEyeColor()
        {
            return _eyeColor;
        }
        
        /// <summary>
        /// Attaches the eye overlay after a short delay to ensure VisEquipment is stable.
        /// </summary>
        private void AttachEyeOverlayDelayed()
        {
            // Additional safety: verify VisEquipment and body model are ready
            if (_visEquipment?.m_bodyModel == null)
            {
                Debug.LogWarning($"[NpcVisEquipment] Cannot attach eye overlay - body model not ready");
                return;
            }
            
            if (_visEquipment.m_bodyModel.sharedMesh == null)
            {
                Debug.LogWarning($"[NpcVisEquipment] Cannot attach eye overlay - body mesh not ready");
                return;
            }
            
            if (_visEquipment.m_bodyModel.bones == null || _visEquipment.m_bodyModel.bones.Length == 0)
            {
                Debug.LogWarning($"[NpcVisEquipment] Cannot attach eye overlay - body bones not ready");
                return;
            }
            
            AttachEyeOverlay();
            ApplyEyeColorToMaterial();
        }
        
        /// <summary>
        /// Attaches the eye overlay to the body mesh.
        /// Creates a duplicate of the body mesh with an emission-based eye material.
        /// Uses the eye emission texture captured from the prefab before shader swap.
        /// </summary>
        private void AttachEyeOverlay()
        {
            // Eye overlay is a client-only visual effect: a graphics-less (dedicated) server never renders it
            // and never captures the emission texture, so attempting it there only produces broken-shader noise.
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;

            // Comprehensive null checks
            if (_visEquipment == null) return;
            if (_visEquipment.m_bodyModel == null) return;
            if (_visEquipment.m_bodyModel.sharedMesh == null) return;
            if (_visEquipment.m_bodyModel.bones == null || _visEquipment.m_bodyModel.bones.Length == 0) return;
            if (_visEquipment.m_bodyModel.transform.parent == null) return;
            
            // Get the eye emission texture from CompanionPrefabManager
            var eyeEmissionTexture = FiresCore.Bridge.NpcCompanionBridge.GetEyeEmissionTexture();
            
            if (eyeEmissionTexture == null)
            {
                if (VerboseLogging)
                    Debug.Log($"[NpcVisEquipment] No eye emission texture available - eye color will not be visible");
                return;
            }
            
            try
            {
                // Destroy any existing overlay first
                if (_eyeOverlayObject != null)
                {
                    UnityEngine.Object.Destroy(_eyeOverlayObject);
                    _eyeOverlayObject = null;
                }
                
                if (_eyeMaterialInstance != null)
                {
                    UnityEngine.Object.Destroy(_eyeMaterialInstance);
                    _eyeMaterialInstance = null;
                }
                
                // Create a new material using the player shader for proper eye rendering.
                // The player shader has _SkinColor which we use for eye tinting.
                // Try Custom/Player first; fall back to Custom/FiresPlayer (FiresTossinShade swap);
                // then to Standard as a last resort.
                var playerShader = Shader.Find("Custom/Player")
                    ?? Shader.Find("Custom/FiresPlayer")
                    ?? Shader.Find("Standard");
                
                if (playerShader == null)
                {
                    if (!_eyeShaderWarningLogged)
                    {
                        _eyeShaderWarningLogged = true;
                        Debug.LogWarning($"[NpcVisEquipment] Could not find Custom/Player or Standard shader for eye overlay");
                    }
                    return;
                }
                
                _eyeMaterialInstance = new Material(playerShader);
                _eyeMaterialInstance.name = "EyeOverlayMaterial";
                
                // Set the emission texture as the emission map
                _eyeMaterialInstance.SetTexture("_EmissionMap", eyeEmissionTexture);
                _eyeMaterialInstance.EnableKeyword("_EMISSION");
                
                // Configure for transparency - make everything except eyes transparent
                // The emission texture should have alpha = 0 for non-eye areas
                if (_eyeMaterialInstance.HasProperty("_Color"))
                {
                    _eyeMaterialInstance.SetColor("_Color", new Color(1, 1, 1, 0)); // Transparent base
                }
                
                // Set emission color to the eye color
                if (_eyeMaterialInstance.HasProperty("_EmissionColor"))
                {
                    _eyeMaterialInstance.SetColor("_EmissionColor", _eyeColor);
                }
                
                // Enable transparency
                _eyeMaterialInstance.SetFloat("_Mode", 2); // Fade mode
                _eyeMaterialInstance.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                _eyeMaterialInstance.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                _eyeMaterialInstance.SetInt("_ZWrite", 0);
                _eyeMaterialInstance.DisableKeyword("_ALPHATEST_ON");
                _eyeMaterialInstance.EnableKeyword("_ALPHABLEND_ON");
                _eyeMaterialInstance.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                _eyeMaterialInstance.renderQueue = 3000; // Render after opaque
                
                // Create a new GameObject for the eye overlay
                _eyeOverlayObject = new GameObject("EyeOverlay");
                _eyeOverlayObject.transform.SetParent(_visEquipment.m_bodyModel.transform.parent, false);
                _eyeOverlayObject.transform.localPosition = Vector3.zero;
                _eyeOverlayObject.transform.localRotation = Quaternion.identity;
                _eyeOverlayObject.transform.localScale = Vector3.one;
                
                // Add a SkinnedMeshRenderer that shares the same mesh and bones as the body
                var overlayRenderer = _eyeOverlayObject.AddComponent<SkinnedMeshRenderer>();
                overlayRenderer.sharedMesh = _visEquipment.m_bodyModel.sharedMesh;
                overlayRenderer.bones = _visEquipment.m_bodyModel.bones;
                overlayRenderer.rootBone = _visEquipment.m_bodyModel.rootBone;
                overlayRenderer.material = _eyeMaterialInstance;
                
                // Set rendering order to render on top of body
                overlayRenderer.sortingOrder = 1;
                
                // Match the bounds
                overlayRenderer.localBounds = _visEquipment.m_bodyModel.localBounds;
                
                // Track the current body mesh for change detection
                _lastBodyMesh = _visEquipment.m_bodyModel.sharedMesh;
                
                Debug.Log($"[NpcVisEquipment] Attached eye overlay using emission texture to {gameObject.name}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcVisEquipment] Failed to attach eye overlay: {ex.Message}");
                
                if (_eyeOverlayObject != null)
                {
                    UnityEngine.Object.Destroy(_eyeOverlayObject);
                    _eyeOverlayObject = null;
                }
                
                if (_eyeMaterialInstance != null)
                {
                    UnityEngine.Object.Destroy(_eyeMaterialInstance);
                    _eyeMaterialInstance = null;
                }
            }
        }
        
        /// <summary>
        /// Gets the appropriate eye material based on gender.
        /// </summary>
        /// <param name="isFemale">True for female model, false for male</param>
        private static Material GetEyeMaterial(bool isFemale)
        {
            // Cache both materials on first access
            if (!_eyeMaterialsCached)
            {
                CacheEyeMaterials();
            }
            
            return isFemale ? _cachedEyeMaterialFemale : _cachedEyeMaterialMale;
        }
        
        /// <summary>
        /// Caches both male and female eye materials from the asset manager.
        /// </summary>
        private static void CacheEyeMaterials()
        {
            if (_eyeMaterialsCached) return;
            _eyeMaterialsCached = true;
            
            try
            {
                // Try to get materials from VAMiscAssetManager
                var miscAssetManagerType = Type.GetType("FiresCore.UI.VAMiscAssetManager, FiresRPGmaker");
                if (miscAssetManagerType != null)
                {
                    // Try GetMaterial method
                    var getMethod = miscAssetManagerType.GetMethod("GetMaterial", 
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
                    
                    if (getMethod != null)
                    {
                        _cachedEyeMaterialMale = getMethod.Invoke(null, new object[] { EyeMaterialMale }) as Material;
                        _cachedEyeMaterialFemale = getMethod.Invoke(null, new object[] { EyeMaterialFemale }) as Material;
                        
                        if (_cachedEyeMaterialMale != null)
                            Debug.Log($"[NpcVisEquipment] Loaded eye material '{EyeMaterialMale}' from MiscAssetManager");
                        if (_cachedEyeMaterialFemale != null)
                            Debug.Log($"[NpcVisEquipment] Loaded eye material '{EyeMaterialFemale}' from MiscAssetManager");
                        
                        return;
                    }
                }
                
                // Fallback: Try to load from asset bundle directly
                var assetBundle = GetCompanionAssetBundle();
                if (assetBundle != null)
                {
                    _cachedEyeMaterialMale = assetBundle.LoadAsset<Material>(EyeMaterialMale);
                    _cachedEyeMaterialFemale = assetBundle.LoadAsset<Material>(EyeMaterialFemale);
                    
                    if (_cachedEyeMaterialMale != null)
                        Debug.Log($"[NpcVisEquipment] Loaded eye material '{EyeMaterialMale}' from asset bundle");
                    if (_cachedEyeMaterialFemale != null)
                        Debug.Log($"[NpcVisEquipment] Loaded eye material '{EyeMaterialFemale}' from asset bundle");
                    
                    return;
                }
                
                if (_cachedEyeMaterialMale == null && _cachedEyeMaterialFemale == null)
                {
                    Debug.Log($"[NpcVisEquipment] Eye materials not found - eye color will not be applied visually");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcVisEquipment] Error loading eye materials: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Gets the companion asset bundle.
        /// </summary>
        private static AssetBundle GetCompanionAssetBundle()
        {
            try
            {
                // Check if there's a static reference to the bundle in Ascend class
                var ascendType = Type.GetType("VerdantsAscent.Ascend, FiresRPGmaker");
                if (ascendType != null)
                {
                    var bundleField = ascendType.GetField("companionBundle", 
                        System.Reflection.BindingFlags.Static | 
                        System.Reflection.BindingFlags.Public | 
                        System.Reflection.BindingFlags.NonPublic);
                    
                    if (bundleField != null)
                    {
                        return bundleField.GetValue(null) as AssetBundle;
                    }
                }
            }
            catch
            {
                // Ignore reflection errors
            }
            
            return null;
        }
        
        /// <summary>
        /// Applies the current eye color to the eye material instance.
        /// The emission texture acts as a mask - only the eye area is visible.
        /// We tint it with the desired eye color.
        /// </summary>
        private void ApplyEyeColorToMaterial()
        {
            if (_eyeMaterialInstance == null) return;
            
            try
            {
                // Apply tint color to the material
                if (_eyeMaterialInstance.HasProperty("_Color"))
                {
                    // Use the eye color with some alpha for blending
                    Color tintColor = new Color(_eyeColor.r, _eyeColor.g, _eyeColor.b, 1f);
                    _eyeMaterialInstance.SetColor("_Color", tintColor);
                }
                
                if (_eyeMaterialInstance.HasProperty("_TintColor"))
                {
                    _eyeMaterialInstance.SetColor("_TintColor", _eyeColor);
                }
                
                // For particles/unlit shader, also set _BaseColor if available
                if (_eyeMaterialInstance.HasProperty("_BaseColor"))
                {
                    _eyeMaterialInstance.SetColor("_BaseColor", _eyeColor);
                }
                
                // Add emission for a subtle glow effect on bright eye colors
                if (_eyeMaterialInstance.HasProperty("_EmissionColor"))
                {
                    // Calculate luminance to determine if eyes should glow
                    float luminance = _eyeColor.r * 0.299f + _eyeColor.g * 0.587f + _eyeColor.b * 0.114f;
                    
                    // Only add emission for bright/fantasy colors
                    if (luminance > 0.6f)
                    {
                        Color emissionColor = _eyeColor * 0.3f;
                        _eyeMaterialInstance.SetColor("_EmissionColor", emissionColor);
                        _eyeMaterialInstance.EnableKeyword("_EMISSION");
                    }
                    else
                    {
                        _eyeMaterialInstance.SetColor("_EmissionColor", Color.black);
                    }
                }
                
                Debug.Log($"[NpcVisEquipment] Applied eye color {_eyeColor} to {gameObject.name}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcVisEquipment] Failed to apply eye color: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Called when the NPC is destroyed - cleanup eye overlay.
        /// </summary>
        private void OnDestroy()
        {
            if (_visEquipment != null)
                _managedVisEquipments.Remove(_visEquipment);

            // Cancel any pending invokes
            CancelInvoke();
            
            if (_eyeOverlayObject != null)
            {
                UnityEngine.Object.Destroy(_eyeOverlayObject);
                _eyeOverlayObject = null;
            }
            
            if (_eyeMaterialInstance != null)
            {
                UnityEngine.Object.Destroy(_eyeMaterialInstance);
                _eyeMaterialInstance = null;
            }
            
            _lastBodyMesh = null;
            _eyeColorPending = false;
        }
        
        /// <summary>
        /// Sets the hair item for this NPC (e.g., "Hair1", "Hair2", etc.)
        /// VisEquipment.UpdateEquipmentVisuals() spawns the hair prefab without needing m_isPlayer=true.
        /// </summary>
        public void SetHairItem(string hairName)
        {
            // CRITICAL: Don't run on hammer ghosts - they don't have valid ZDOs
            if (_nview == null || !_nview.IsValid() || _nview.GetZDO() == null) return;
            
            if (!_initialized) Initialize();
            if (_visEquipment == null) return;
            
            // Store hair item - UpdateEquipmentVisuals() will spawn it; m_isPlayer=true is NOT needed.
            VisEquipmentCompat.SetHairItem(_visEquipment, hairName ?? "");

            // Reapply hair color after hair mesh is created
            if (_hairColorSet)
            {
                Invoke(nameof(ApplyHairColorToMaterials), 0.5f);
            }
        }
        
        /// <summary>
        /// Sets the beard item for this NPC (e.g., "Beard1", "Beard2", etc.)
        /// VisEquipment.UpdateEquipmentVisuals() spawns the beard prefab without needing m_isPlayer=true.
        /// </summary>
        public void SetBeardItem(string beardName)
        {
            // CRITICAL: Don't run on hammer ghosts - they don't have valid ZDOs
            if (_nview == null || !_nview.IsValid() || _nview.GetZDO() == null) return;
            
            if (!_initialized) Initialize();
            if (_visEquipment == null) return;
            
            // Store beard item - UpdateEquipmentVisuals() will spawn it; m_isPlayer=true is NOT needed.
            VisEquipmentCompat.SetBeardItem(_visEquipment, beardName ?? "");

            // Reapply hair color after beard mesh is created (beards use hair color)
            if (_hairColorSet)
            {
                Invoke(nameof(ApplyHairColorToMaterials), 0.5f);
            }
        }
        
        /// <summary>
        /// Sets model, hair, and beard in a single batched operation.
        /// Switches the body mesh directly and stores hair/beard hashes in ZDO;
        /// VisEquipment.UpdateEquipmentVisuals() spawns them in the update loop.
        /// </summary>
        /// <param name="modelIndex">0 = male, 1 = female. Use -1 to skip model change.</param>
        /// <param name="hairItem">Hair style name (e.g., "Hair1"). Use null to skip.</param>
        /// <param name="beardItem">Beard style name (e.g., "Beard1"). Use null to skip.</param>
        public void SetAppearance(int modelIndex, string hairItem, string beardItem)
        {
            // CRITICAL: Don't run on hammer ghosts - they don't have valid ZDOs
            // This prevents NullReferenceException in VisEquipment methods
            if (_nview == null || !_nview.IsValid() || _nview.GetZDO() == null) return;
            
            if (!_initialized) Initialize();
            if (_visEquipment == null) return;
            
            // Ensure we have player models if we need to switch models
            if (modelIndex >= 0 && (_visEquipment.m_models == null || _visEquipment.m_models.Length < 2))
            {
                SetupPlayerModels();
            }
            
            // Set model if requested - switch mesh directly (no m_isPlayer=true needed/wanted)
            if (modelIndex >= 0 && _visEquipment.m_models != null && _visEquipment.m_models.Length > modelIndex)
            {
                _visEquipment.SetModel(modelIndex); // stores index in ZDO
                int clampedIndex = Mathf.Clamp(modelIndex, 0, _visEquipment.m_models.Length - 1);
                var model = _visEquipment.m_models[clampedIndex];
                if (model != null && model.m_mesh != null)
                    TryAssignBodyMesh(model.m_mesh, clampedIndex);
                // Switching the body model resets m_bodyModel.material from the model's base material, which wipes
                // the armor body-underlay textures vanilla wrote onto it (_ChestTex/_LegsTex — the chest/legs
                // "skin" the user saw missing on static NPCs). Clear vanilla's APPLIED hashes (not the desired
                // m_xxxItemHash) so the next UpdateEquipmentVisuals re-writes chest/legs/etc. onto the fresh
                // material in the same pass that reset it.
                ClearVisEquipmentCurrentHashes();
                if (VerboseLogging)
                    Debug.Log($"[NpcVisEquipment] SetAppearance - Model: {modelIndex} (cleared current hashes to re-apply armor underlay)");
            }

            // Set hair if requested - UpdateEquipmentVisuals() will spawn it without m_isPlayer=true
            if (hairItem != null)
            {
                VisEquipmentCompat.SetHairItem(_visEquipment, hairItem);
                if (VerboseLogging)
                    Debug.Log($"[NpcVisEquipment] SetAppearance - Hair: {hairItem}");
            }

            // Set beard if requested
            if (beardItem != null)
            {
                VisEquipmentCompat.SetBeardItem(_visEquipment, beardItem);
                if (VerboseLogging)
                    Debug.Log($"[NpcVisEquipment] SetAppearance - Beard: {beardItem}");
            }

            // Reapply hair color after meshes are created (give extra time for visual update)
            if (_hairColorSet)
            {
                Invoke(nameof(ApplyHairColorToMaterials), 0.5f);
            }
        }
        
        /// <summary>
        /// Gets the current model index (0 = male, 1 = female).
        /// </summary>
        public int GetModelIndex()
        {
            if (_visEquipment == null) return 0;
            return _visEquipment.GetModelIndex();
        }
        
        /// <summary>
        /// Clears the cached eye materials (call if the asset bundle is reloaded).
        /// </summary>
        public static void ClearEyeMaterialCache()
        {
            _cachedEyeMaterialMale = null;
            _cachedEyeMaterialFemale = null;
            _eyeMaterialsCached = false;
        }
        
        #endregion

        /// <summary>
        /// Finds a player-compatible body material. The Player prefab's own model table is checked first because
        /// prefab-side materials are immune to live shader swaps, which had poisoned every other donor and left static
        /// NPC bodies invisible or grey.
        /// </summary>
        internal static Material FindPlayerBodyDonor()
        {
            var playerPrefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Player") : null;
            var playerVis = playerPrefab != null ? playerPrefab.GetComponent<VisEquipment>() : null;

            if (playerVis != null && playerVis.m_models != null)
            {
                for (int i = 0; i < playerVis.m_models.Length; i++)
                {
                    var mat = playerVis.m_models[i] != null ? playerVis.m_models[i].m_baseMaterial : null;
                    if (mat != null && mat.shader != null && IsPlayerCompatibleShader(mat.shader.name))
                        return mat;
                }
            }
            if (playerVis != null && playerVis.m_bodyModel != null)
            {
                var mat = playerVis.m_bodyModel.sharedMaterial;
                if (mat != null && mat.shader != null && IsPlayerCompatibleShader(mat.shader.name))
                    return mat;
            }
            if (Player.m_localPlayer != null)
            {
                var liveVis = Player.m_localPlayer.GetComponent<VisEquipment>();
                var mat = liveVis != null && liveVis.m_bodyModel != null ? liveVis.m_bodyModel.sharedMaterial : null;
                if (mat != null && mat.shader != null && IsPlayerCompatibleShader(mat.shader.name))
                    return mat;
            }
            return null;
        }

        /// <summary>
        /// PREFAB-level body-material repair (client-side): points the prefab's body renderer AND its
        /// VisEquipment model table at a clone of the Player donor material when they carry a broken
        /// shader. Run once at load — placement GHOSTS clone the prefab directly, so without this the
        /// hammer ghost rendered the broken bundle Standard material (invisible NPC ghost) even though
        /// live instances got runtime-fixed.
        /// </summary>
        public static bool FixBodyMaterialOnPrefab(GameObject prefab)
        {
            if (prefab == null || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                return false;
            try
            {
                var vis = prefab.GetComponent<VisEquipment>();
                var body = vis != null ? vis.m_bodyModel : null;
                if (body == null)
                {
                    foreach (var skinnedRenderer in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        if (skinnedRenderer.name == "body") { body = skinnedRenderer; break; }
                }
                if (body == null) return false;

                string current = body.sharedMaterial?.shader?.name ?? "null";
                bool bodyBroken = !IsPlayerCompatibleShader(current);
                bool modelsBroken = false;
                if (vis != null && vis.m_models != null)
                    foreach (var playerModel in vis.m_models)
                        if (playerModel != null && (playerModel.m_baseMaterial == null || playerModel.m_baseMaterial.shader == null
                            || !IsPlayerCompatibleShader(playerModel.m_baseMaterial.shader.name)))
                        { modelsBroken = true; break; }
                if (!bodyBroken && !modelsBroken) return true;

                var donor = FindPlayerBodyDonor();
                if (donor == null)
                {
                    Debug.LogWarning($"[NpcVisEquipment] prefab body fix: no player-compatible donor material found for {prefab.name}");
                    return false;
                }

                if (bodyBroken) body.sharedMaterial = new Material(donor);
                if (vis != null && vis.m_models != null)
                {
                    for (int i = 0; i < vis.m_models.Length; i++)
                    {
                        var model = vis.m_models[i];
                        if (model == null) continue;
                        if (model.m_baseMaterial == null || model.m_baseMaterial.shader == null
                            || !IsPlayerCompatibleShader(model.m_baseMaterial.shader.name))
                            model.m_baseMaterial = new Material(donor);
                    }
                }
                Debug.Log($"[NpcVisEquipment] prefab body material repaired for {prefab.name} (donor '{donor.shader.name}')");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcVisEquipment] FixBodyMaterialOnPrefab failed for {prefab?.name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Gives the body the working Custom/Player material copied from the Player prefab, which armor overlays need.</summary>
        private void TryFixBodyShader(SkinnedMeshRenderer bodyRenderer)
        {
            if (bodyRenderer == null) return;

            try
            {
                // Find the Player prefab's body material — prefab model table FIRST (poison-proof),
                // then the legacy fallbacks.
                Material playerBodyMaterial = FindPlayerBodyDonor();

                // Legacy fallback chain (kept for odd deployments where the donor helper found nothing).
                var playerPrefab = ZNetScene.instance?.GetPrefab("Player");
                if (playerBodyMaterial == null && playerPrefab != null)
                {
                    var playerVisEquip = playerPrefab.GetComponent<VisEquipment>();
                    if (playerVisEquip?.m_bodyModel != null)
                    {
                        playerBodyMaterial = playerVisEquip.m_bodyModel.sharedMaterial;
                    }

                    // Fallback: search for body renderer on player prefab
                    if (playerBodyMaterial == null)
                    {
                        var bodyTransform = FindTransformRecursive(playerPrefab.transform, "body");
                        if (bodyTransform != null)
                        {
                            var skinnedRenderer = bodyTransform.GetComponent<SkinnedMeshRenderer>();
                            if (skinnedRenderer != null)
                                playerBodyMaterial = skinnedRenderer.sharedMaterial;
                        }
                    }
                }

                // Try from local player as last resort
                if (playerBodyMaterial == null && Player.m_localPlayer != null)
                {
                    var playerVisEquip = Player.m_localPlayer.GetComponent<VisEquipment>();
                    if (playerVisEquip?.m_bodyModel != null)
                    {
                        playerBodyMaterial = playerVisEquip.m_bodyModel.sharedMaterial;
                    }
                }

                // Accept either Custom/Player OR Custom/FiresPlayer as the source. FiresTossinShade
                // swaps the live Player.body material to Custom/FiresPlayer at runtime — without
                // accepting it here, TryFixBodyShader sees "shader != Custom/Player" on every fix
                // attempt, returns, and leaves StaticNpc (and any other prefab whose body lands on
                // Standard) stuck with the wrong shader → VisEquipment kept disabled → NPC stays
                // nude. Both shaders expose the same _SkinBumpMap / _ChestTex / _LegsTex / _SkinColor
                // properties VisEquipment needs.
                if (playerBodyMaterial == null
                    || playerBodyMaterial.shader == null
                    || !IsPlayerCompatibleShader(playerBodyMaterial.shader.name))
                {
                    Debug.LogWarning($"[NpcVisEquipment] Could not find Player body material with a player-compatible shader (looked for Custom/Player or Custom/FiresPlayer; got '{playerBodyMaterial?.shader?.name ?? "<null>"}')");
                    return;
                }

                // Create a new material based on the Player's material (gets us the real shader)
                var currentMaterial = bodyRenderer.sharedMaterial;
                var newMaterial = new Material(playerBodyMaterial);

                // Copy textures from the NPC's original material
                if (currentMaterial != null)
                {
                    if (currentMaterial.HasProperty("_MainTex"))
                    {
                        var tex = currentMaterial.GetTexture("_MainTex");
                        if (tex != null)
                            newMaterial.SetTexture("_MainTex", tex);
                    }

                    if (currentMaterial.HasProperty("_BumpMap"))
                    {
                        var bump = currentMaterial.GetTexture("_BumpMap");
                        if (bump != null && newMaterial.HasProperty("_SkinBumpMap"))
                            newMaterial.SetTexture("_SkinBumpMap", bump);
                    }
                }

                // Set a default skin color
                if (newMaterial.HasProperty("_SkinColor"))
                {
                    newMaterial.SetColor("_SkinColor", new Color(1f, 0.87f, 0.77f, 1f));
                }

                bodyRenderer.sharedMaterial = newMaterial;

                // ALSO repair the VisEquipment model table. Vanilla SetModel/UpdateVisuals re-applies
                // m_models[index].m_baseMaterial whenever the visual path re-runs (model index sync,
                // wander-start re-enables, gear changes) — fixing only the live renderer meant the NPC
                // looked right at spawn and went flat-gray (broken Standard material) the moment the
                // companion visual path re-ran. Point every broken base material at the repaired one.
                if (_visEquipment != null && _visEquipment.m_models != null)
                {
                    for (int i = 0; i < _visEquipment.m_models.Length; i++)
                    {
                        var model = _visEquipment.m_models[i];
                        if (model == null) continue;
                        var baseMat = model.m_baseMaterial;
                        if (baseMat == null || baseMat.shader == null || !IsPlayerCompatibleShader(baseMat.shader.name))
                        {
                            var repaired = new Material(newMaterial);
                            if (baseMat != null && baseMat.HasProperty("_MainTex"))
                            {
                                var tex = baseMat.GetTexture("_MainTex");
                                if (tex != null) repaired.SetTexture("_MainTex", tex);
                            }
                            model.m_baseMaterial = repaired;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcVisEquipment] TryFixBodyShader failed: {ex.Message}");
            }
        }

        private Transform FindTransformRecursive(Transform parent, string name)
        {
            if (parent == null) return null;

            // Skip hidden original visuals from model override
            if (!parent.gameObject.activeSelf && parent.name.StartsWith("__OriginalVisual_"))
                return null;

            if (parent.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return parent;

            foreach (Transform child in parent)
            {
                var found = FindTransformRecursive(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }

    /// <summary>
    /// Refreshes back-slot weapon visuals for Fires NPCs every frame. Vanilla only updates back weapons for players,
    /// and NPCs keep m_isPlayer false so the armor body material isn't overwritten, so drawn weapons stayed on the
    /// back. This postfix calls vanilla's SetBackEquipped with the synced ZDO hashes, which is a no-op when nothing
    /// changed, on every client.
    /// </summary>
    [HarmonyPatch(typeof(VisEquipment), "UpdateEquipmentVisuals")]
    internal static class VisEquipmentBackWeaponRefreshPatch
    {
        private static Func<VisEquipment, int, int, int, bool> _setBackEquipped;
        private static Action<VisEquipment> _updateLodgroup;
        private static bool _resolved;
        private static bool _usable;

        private static void Resolve()
        {
            _resolved = true;
            try
            {
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                var setBack = typeof(VisEquipment).GetMethod("SetBackEquipped", flags,
                    null, new[] { typeof(int), typeof(int), typeof(int) }, null);
                if (setBack == null) return;
                _setBackEquipped = (Func<VisEquipment, int, int, int, bool>)Delegate.CreateDelegate(
                    typeof(Func<VisEquipment, int, int, int, bool>), setBack);

                var lod = typeof(VisEquipment).GetMethod("UpdateLodgroup", flags,
                    null, Type.EmptyTypes, null);
                if (lod != null)
                    _updateLodgroup = (Action<VisEquipment>)Delegate.CreateDelegate(
                        typeof(Action<VisEquipment>), lod);

                _usable = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcVisEquipment] Back-weapon refresh patch could not bind VisEquipment internals: {ex.Message}");
            }
        }

        private static void Postfix(VisEquipment __instance)
        {
            if (__instance == null) return;
            if (!NpcVisEquipment.TryGetManaged(__instance, out var npc) || npc == null) return;
            if (!npc.TryGetDesiredBackHashes(out int leftBack, out int rightBack, out int leftBackVariant))
                return;

            if (!_resolved) Resolve();
            if (!_usable) return;

            bool changed = _setBackEquipped(__instance, leftBack, rightBack, leftBackVariant);
            if (changed)
                _updateLodgroup?.Invoke(__instance);
        }
    }
}
