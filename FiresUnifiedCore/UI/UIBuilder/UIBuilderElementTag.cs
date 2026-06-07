using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// MonoBehaviour tag placed on every live GameObject instantiated from a UIElementNode.
    /// Links the runtime object back to its data model node for the editor.
    /// </summary>
    public class UIBuilderElementTag : MonoBehaviour
    {
        public UIElementNode Node { get; set; }
        public UILayoutDefinition Layout { get; set; }
        public string ElementId { get; set; }
        public string ElementTag { get; set; }
        /// <summary>
        /// Set on RenderCamera elements. Runtime scripts can use this to configure the preview.
        /// </summary>
        public Camera PreviewCamera { get; set; }

        /// <summary>
        /// Convenience accessor: returns the override element tag from either the explicit
        /// <see cref="ElementTag"/> field or the backing <see cref="Node"/>.Tag.
        /// </summary>
        public string Tag
        {
            get
            {
                if (!string.IsNullOrEmpty(ElementTag)) return ElementTag;
                if (Node != null && !string.IsNullOrEmpty(Node.Tag)) return Node.Tag;
                return null;
            }
        }

        /// <summary>
        /// Convenience accessor: returns the metadata list from the backing node.
        /// Returns null if no node is linked or the node has no metadata.
        /// </summary>
        public List<MetadataEntry> Metadata
        {
            get { return Node?.Metadata; }
        }

        /// <summary>
        /// Reads a metadata value from the backing node by key (case-insensitive).
        /// Returns null if not found.
        /// </summary>
        public string GetMeta(string key)
        {
            return Node?.GetMeta(key);
        }
    }
}
