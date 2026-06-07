using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using HarmonyLib;

namespace FiresCore.UI
{
    /// <summary>
    /// Wires an override large map display element to the vanilla Minimap system.
    /// When attached to a RawImage tagged <see cref="UIOverrideElementTags.MapDisplay"/>,
    /// this component copies the large map render texture from Minimap onto the
    /// override RawImage.
    ///
    /// Similar to <see cref="UIOverrideMinimapWirer"/> but for the full-screen map
    /// instead of the minimap. Handles the explore texture (fog of war).
    /// </summary>
    public class UIOverrideMapWiring : MonoBehaviour
    {
        private RawImage _rawImage;
        private bool _initialized;

        private static readonly FieldInfo fi_mapTexture =
            AccessTools.Field(typeof(Minimap), "m_mapTexture");
        private static readonly FieldInfo fi_fogTexture =
            AccessTools.Field(typeof(Minimap), "m_forestMaskTexture");

        private void Start()
        {
            _rawImage = GetComponent<RawImage>();
            if (_rawImage == null)
            {
                Debug.LogWarning("[UIOverrideMapWiring] No RawImage component found — cannot display map.");
                enabled = false;
                return;
            }
            _initialized = true;
        }

        private void LateUpdate()
        {
            if (!_initialized) return;
            if (Minimap.instance == null) return;

            try
            {
                var mapTex = fi_mapTexture?.GetValue(Minimap.instance) as Texture;
                if (mapTex != null && _rawImage.texture != mapTex)
                {
                    _rawImage.texture = mapTex;
                    _rawImage.color = Color.white;
                }
            }
            catch { }
        }
    }
}
