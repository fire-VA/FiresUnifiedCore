using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI
{
    /// <summary>
    /// Keeps an injected minimap RawImage synced with Minimap.instance's live
    /// render texture. Attached to injected minimap elements by the wiring system.
    /// Checks each frame (in LateUpdate so it runs after Minimap's own rendering)
    /// and updates the texture reference if it changes.
    /// </summary>
    public class UIOverrideMinimapWirer : MonoBehaviour
    {
        public RawImage MapImage;

        private static FieldInfo _smallMapField;
        private static bool _fieldLookedUp;

        private void LateUpdate()
        {
            if (Minimap.instance == null || MapImage == null) return;

            if (!_fieldLookedUp)
            {
                _fieldLookedUp = true;
                _smallMapField = typeof(Minimap).GetField("m_mapImageSmall",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_smallMapField == null) return;

            var liveMapImg = _smallMapField.GetValue(Minimap.instance) as RawImage;
            if (liveMapImg != null && liveMapImg.texture != null)
            {
                if (MapImage.texture != liveMapImg.texture)
                {
                    MapImage.texture = liveMapImg.texture;
                    MapImage.material = liveMapImg.material;
                }
            }
        }
    }
}
