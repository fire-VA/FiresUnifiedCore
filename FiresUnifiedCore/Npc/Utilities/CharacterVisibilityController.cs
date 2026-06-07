using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Utilities
{
    /// <summary>
    /// Controls character visibility for stealth, vanish, and teleportation effects.
    /// 
    /// VALHEIM-SPECIFIC IMPLEMENTATION:
    /// Valheim uses custom shaders (Custom/Player, Custom/Creature) that don't support
    /// standard Unity transparency properties. Instead, we use Valheim's actual ghost
    /// material which is designed for transparent effects.
    /// 
    /// MODES:
    /// - Hidden: Completely invisible (renderer.enabled = false)
    /// - Ghost: Uses Valheim's ghost material for true transparency
    /// - Normal: Fully visible with original materials
    /// 
    /// USAGE:
    /// var visibility = new CharacterVisibilityController(character);
    /// visibility.SetHidden(true);  // Hide completely
    /// visibility.SetGhostMode(true);  // Apply ghost transparency
    /// visibility.Restore();  // Return to normal
    /// </summary>
    public class CharacterVisibilityController
    {
        #region State
        
        private readonly Character _character;
        private readonly List<RendererState> _rendererStates = new List<RendererState>();
        private bool _isInitialized = false;
        private bool _isHidden = false;
        private bool _isGhostMode = false;
        private float _currentAlpha = 1f;
        
        // Cached ghost material from Valheim
        private static Material _ghostMaterial;
        private static bool _ghostMaterialSearched = false;
        
        public static bool VerboseLogging = false;
        
        #endregion
        
        #region Renderer State Tracking
        
        /// <summary>
        /// Tracks original state of each renderer for restoration.
        /// </summary>
        private class RendererState
        {
            public Renderer Renderer;
            public bool WasEnabled;
            public Material[] OriginalMaterials;
            public Material[] OriginalSharedMaterials;
        }
        
        #endregion
        
        #region Constructor
        
        public CharacterVisibilityController(Character character)
        {
            _character = character;
            Initialize();
        }
        
        private void Initialize()
        {
            if (_character == null) return;
            if (_isInitialized) return;
            
            try
            {
                // Ensure ghost material is loaded
                EnsureGhostMaterial();
                
                // Get ALL renderers on the character and children
                var allRenderers = _character.GetComponentsInChildren<Renderer>(true);
                
                foreach (var renderer in allRenderers)
                {
                    if (renderer == null) continue;
                    
                    // Skip certain renderer types that shouldn't be affected by visibility changes
                    string typeName = renderer.GetType().Name;
                    if (typeName.Contains("Particle") || typeName.Contains("Trail") || typeName.Contains("Line"))
                    {
                        continue;
                    }
                    
                    var state = new RendererState
                    {
                        Renderer = renderer,
                        WasEnabled = renderer.enabled
                    };
                    
                    // Store original materials (both instanced and shared)
                    if (renderer.materials != null && renderer.materials.Length > 0)
                    {
                        // Clone the materials array to preserve originals
                        state.OriginalMaterials = new Material[renderer.materials.Length];
                        for (int i = 0; i < renderer.materials.Length; i++)
                        {
                            state.OriginalMaterials[i] = renderer.materials[i];
                        }
                    }
                    
                    if (renderer.sharedMaterials != null && renderer.sharedMaterials.Length > 0)
                    {
                        state.OriginalSharedMaterials = new Material[renderer.sharedMaterials.Length];
                        for (int i = 0; i < renderer.sharedMaterials.Length; i++)
                        {
                            state.OriginalSharedMaterials[i] = renderer.sharedMaterials[i];
                        }
                    }
                    
                    _rendererStates.Add(state);
                }
                
                _isInitialized = true;
                
                if (VerboseLogging)
                {
                    Debug.Log($"[CharacterVisibilityController] Initialized for {_character.m_name} with {_rendererStates.Count} renderers, ghostMaterial={_ghostMaterial != null}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CharacterVisibilityController] Init failed: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Finds and caches Valheim's ghost material from existing ghost prefabs.
        /// </summary>
        private static void EnsureGhostMaterial()
        {
            if (_ghostMaterialSearched) return;
            _ghostMaterialSearched = true;
            
            try
            {
                // Try to find a ghost prefab and extract its material
                string[] ghostPrefabNames = { "Ghost", "ghost", "Wraith", "wraith", "Ghost_ragdoll" };
                
                foreach (var prefabName in ghostPrefabNames)
                {
                    var prefab = ZNetScene.instance?.GetPrefab(prefabName);
                    if (prefab != null)
                    {
                        var renderer = prefab.GetComponentInChildren<Renderer>(true);
                        if (renderer != null && renderer.sharedMaterial != null)
                        {
                            // Clone the material so we can modify alpha
                            _ghostMaterial = new Material(renderer.sharedMaterial);
                            _ghostMaterial.name = "CompanionGhostMaterial";
                            Debug.Log($"[CharacterVisibilityController] Found ghost material from {prefabName}: {_ghostMaterial.shader.name}");
                            return;
                        }
                    }
                }
                
                // Fallback: Try to find the ghost shader directly and create a material
                var ghostShader = Shader.Find("Custom/Creature");
                if (ghostShader == null) ghostShader = Shader.Find("Lux Lit Particles/ Bumped");
                if (ghostShader == null) ghostShader = Shader.Find("Standard");
                
                if (ghostShader != null)
                {
                    _ghostMaterial = new Material(ghostShader);
                    _ghostMaterial.name = "CompanionGhostMaterialFallback";
                    
                    // Set up for transparency
                    if (_ghostMaterial.HasProperty("_Color"))
                    {
                        _ghostMaterial.SetColor("_Color", new Color(0.8f, 0.8f, 1f, 0.15f));
                    }
                    
                    // Enable transparency rendering
                    _ghostMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    _ghostMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    _ghostMaterial.SetInt("_ZWrite", 0);
                    _ghostMaterial.renderQueue = 3000;
                    
                    Debug.Log($"[CharacterVisibilityController] Created fallback ghost material with shader: {ghostShader.name}");
                }
                else
                {
                    Debug.LogWarning("[CharacterVisibilityController] Could not find any suitable shader for ghost material");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CharacterVisibilityController] Failed to load ghost material: {ex.Message}");
            }
        }
        
        #endregion
        
        #region Public API
        
        /// <summary>
        /// Gets current visibility state.
        /// </summary>
        public bool IsHidden => _isHidden;
        
        /// <summary>
        /// Gets current alpha level.
        /// </summary>
        public float CurrentAlpha => _currentAlpha;
        
        /// <summary>
        /// Completely hides the character (disables all renderers).
        /// Fast and efficient - good for teleport effects.
        /// </summary>
        public void SetHidden(bool hidden)
        {
            if (!_isInitialized) Initialize();
            
            _isHidden = hidden;
            
            foreach (var state in _rendererStates)
            {
                if (state.Renderer == null) continue;
                state.Renderer.enabled = hidden ? false : state.WasEnabled;
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[CharacterVisibilityController] {_character?.m_name} hidden: {hidden}");
            }
        }
        
        /// <summary>
        /// Sets transparency level for ghost/stealth effect.
        /// Uses Valheim's ghost material for proper transparency.
        /// 0 = invisible, 0.3 = ghostly, 1 = opaque
        /// </summary>
        public void SetTransparency(float alpha)
        {
            if (!_isInitialized) Initialize();
            
            alpha = Mathf.Clamp01(alpha);
            _currentAlpha = alpha;
            
            // If alpha is 0 or very low, just hide completely
            if (alpha <= 0.01f)
            {
                SetHidden(true);
                return;
            }
            
            // If alpha is 1, restore to normal
            if (alpha >= 0.99f)
            {
                Restore();
                return;
            }
            
            // Make sure renderers are enabled
            if (_isHidden)
            {
                SetHidden(false);
            }
            
            // Apply ghost mode with specified alpha
            ApplyGhostMode(alpha);
            
            Debug.Log($"[CharacterVisibilityController] {_character?.m_name} transparency set to {alpha:F2} (ghost mode)");
        }
        
        /// <summary>
        /// Enables or disables ghost mode (full transparency effect).
        /// </summary>
        public void SetGhostMode(bool enabled, float alpha = 0.15f)
        {
            if (!_isInitialized) Initialize();
            
            if (enabled)
            {
                ApplyGhostMode(alpha);
            }
            else
            {
                Restore();
            }
        }
        
        /// <summary>
        /// Restores character to full visibility with original materials.
        /// </summary>
        public void Restore()
        {
            if (!_isInitialized) return;
            
            _isHidden = false;
            _isGhostMode = false;
            _currentAlpha = 1f;
            
            foreach (var state in _rendererStates)
            {
                if (state.Renderer == null) continue;
                
                // Restore enabled state
                state.Renderer.enabled = state.WasEnabled;
                
                // Restore original materials
                if (state.OriginalSharedMaterials != null && state.OriginalSharedMaterials.Length > 0)
                {
                    state.Renderer.sharedMaterials = state.OriginalSharedMaterials;
                }
            }
            
            if (VerboseLogging)
            {
                Debug.Log($"[CharacterVisibilityController] {_character?.m_name} restored to normal");
            }
        }
        
        /// <summary>
        /// Re-scans for renderers (call after equipment change).
        /// </summary>
        public void RefreshRenderers()
        {
            // Save ghost state
            bool wasGhost = _isGhostMode;
            float alpha = _currentAlpha;
            
            // Re-initialize
            _isInitialized = false;
            _rendererStates.Clear();
            Initialize();
            
            // Re-apply ghost if was active
            if (wasGhost)
            {
                ApplyGhostMode(alpha);
            }
        }
        
        #endregion
        
        #region Ghost Mode Implementation
        
        /// <summary>
        /// Applies ghost transparency using Valheim's ghost material.
        /// This actually works because we're replacing the material entirely
        /// rather than trying to modify Valheim's custom shader properties.
        /// </summary>
        private void ApplyGhostMode(float alpha)
        {
            _isGhostMode = true;
            _currentAlpha = alpha;
            
            // Create a ghost material with the specified alpha if we have a base
            Material ghostMatWithAlpha = null;
            if (_ghostMaterial != null)
            {
                ghostMatWithAlpha = new Material(_ghostMaterial);
                if (ghostMatWithAlpha.HasProperty("_Color"))
                {
                    Color c = ghostMatWithAlpha.color;
                    c.a = alpha;
                    ghostMatWithAlpha.color = c;
                }
            }
            
            foreach (var state in _rendererStates)
            {
                if (state.Renderer == null) continue;
                
                // Method 1: Try to use ghost material replacement
                if (ghostMatWithAlpha != null)
                {
                    // Create array of ghost materials matching original count
                    int matCount = state.Renderer.sharedMaterials.Length;
                    var ghostMats = new Material[matCount];
                    for (int i = 0; i < matCount; i++)
                    {
                        ghostMats[i] = ghostMatWithAlpha;
                    }
                    state.Renderer.sharedMaterials = ghostMats;
                }
                else
                {
                    // Method 2: Fallback - directly modify material properties
                    // This may not work perfectly with Valheim shaders but is better than nothing
                    foreach (var mat in state.Renderer.materials)
                    {
                        if (mat == null) continue;
                        ApplyTransparencyToMaterial(mat, alpha);
                    }
                }
            }
            
            Debug.Log($"[CharacterVisibilityController] {_character?.m_name} ghost mode applied (alpha={alpha:F2}, usingGhostMat={ghostMatWithAlpha != null})");
        }
        
        /// <summary>
        /// Fallback: Tries to apply transparency to a material directly.
        /// May not work well with Valheim's custom shaders.
        /// </summary>
        private void ApplyTransparencyToMaterial(Material mat, float alpha)
        {
            // Try setting _Color alpha
            if (mat.HasProperty("_Color"))
            {
                Color color = mat.color;
                color.a = alpha;
                mat.color = color;
            }
            
            // Try setting transparency mode properties
            if (mat.HasProperty("_Mode"))
            {
                mat.SetFloat("_Mode", 3); // Transparent mode
            }
            
            // Set blend mode for transparency
            if (mat.HasProperty("_SrcBlend"))
            {
                mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            }
            if (mat.HasProperty("_DstBlend"))
            {
                mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            }
            if (mat.HasProperty("_ZWrite"))
            {
                mat.SetInt("_ZWrite", 0);
            }
            
            // Set render queue for transparency
            mat.renderQueue = 3000;
            
            // Enable transparency keywords
            mat.EnableKeyword("_ALPHABLEND_ON");
        }
        
        #endregion
        
        #region Static Helpers
        
        /// <summary>
        /// Quickly hides a character for teleportation.
        /// Returns an action to call to restore visibility.
        /// </summary>
        public static Action QuickHide(Character character, float duration = 0.15f)
        {
            if (character == null) return () => { };
            
            var controller = new CharacterVisibilityController(character);
            controller.SetHidden(true);
            
            return () => controller.Restore();
        }
        
        /// <summary>
        /// Creates a ghost effect on a character using Valheim's ghost material.
        /// Returns a controller to manage the effect.
        /// </summary>
        public static CharacterVisibilityController CreateGhostEffect(Character character, float alpha = 0.3f)
        {
            if (character == null) return null;
            
            var controller = new CharacterVisibilityController(character);
            controller.SetTransparency(alpha);
            return controller;
        }
        
        /// <summary>
        /// Forces reload of ghost material (call after ZNetScene changes).
        /// </summary>
        public static void ReloadGhostMaterial()
        {
            _ghostMaterialSearched = false;
            _ghostMaterial = null;
        }
        
        #endregion
    }
}
