using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Decides which layout nodes an override touches by comparing them with the baseline captured under
    /// <see cref="BaselineMetaKey"/>: nodes marked override_modified, nodes with no baseline counterpart and
    /// nodes that differ are applied, identical ones are skipped. Editing a few nodes of a large captured UI
    /// therefore changes only those; marking a removed mod's captured elements as modified keeps them injected.
    /// </summary>
    public static class UIOverrideDiffEngine
    {
        /// <summary>
        /// Layout metadata key that stores the serialized baseline root element tree (JSON).
        /// Set at capture time by <see cref="StoreCaptureBaseline"/>.
        /// </summary>
        public const string BaselineMetaKey = "capture_baseline";

        /// <summary>
        /// Per-node metadata key that marks a node as explicitly modified.
        /// When present (any non-empty value), the override always applies this node
        /// regardless of whether it matches the baseline.
        /// </summary>
        public const string NodeModifiedKey = "override_modified";

        //  Baseline management

        /// <summary>
        /// Stores a deep-clone of the layout's current root element tree as the
        /// capture baseline. Call this immediately after a capture completes (before
        /// the user makes any edits).
        /// The baseline is stored as serialized JSON in layout metadata so it
        /// persists through save/load and packaging.
        /// </summary>
        public static void StoreCaptureBaseline(UILayoutDefinition layout)
        {
            if (layout?.RootElement == null) return;

            try
            {
                // Serialize just the root element tree (not the full layout)
                // by wrapping it in a temporary layout for the serializer.
                var tempLayout = new UILayoutDefinition
                {
                    UID = "__baseline__",
                    RootElement = layout.RootElement
                };
                string baselineJson = UILayoutSerializer.Serialize(tempLayout);
                layout.SetMeta(BaselineMetaKey, baselineJson);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIOverrideDiffEngine] Failed to store capture baseline: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns true if the layout has a stored capture baseline.
        /// </summary>
        public static bool HasBaseline(UILayoutDefinition layout)
        {
            return layout != null && !string.IsNullOrEmpty(layout.GetMeta(BaselineMetaKey));
        }

        /// <summary>
        /// Deserializes the stored baseline root element tree from layout metadata.
        /// Returns null if no baseline is stored or deserialization fails.
        /// </summary>
        public static UIElementNode GetBaselineRoot(UILayoutDefinition layout)
        {
            if (layout == null) return null;
            string json = layout.GetMeta(BaselineMetaKey);
            if (string.IsNullOrEmpty(json)) return null;

            try
            {
                var tempLayout = UILayoutSerializer.Deserialize(json);
                return tempLayout?.RootElement;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIOverrideDiffEngine] Failed to deserialize baseline: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Clears the capture baseline from a layout. After this, all nodes will
        /// be treated as modified (full override behavior, same as before this feature).
        /// </summary>
        public static void ClearBaseline(UILayoutDefinition layout)
        {
            if (layout == null) return;
            layout.SetMeta(BaselineMetaKey, null);
        }

        //  Diff computation

        /// <summary>
        /// Builds a set of node IDs that should be overridden (modified nodes).
        /// Returns null if there's no baseline (meaning: override everything, legacy behavior).
        /// When a baseline exists, only nodes that differ from the baseline are included.
        ///
        /// The override manager calls this once before walking the tree and checks each
        /// node's ID against the returned set.
        /// </summary>
        public static HashSet<string> ComputeModifiedNodeIds(UILayoutDefinition layout)
        {
            if (layout?.RootElement == null) return null;

            var baselineRoot = GetBaselineRoot(layout);
            if (baselineRoot == null) return null; // No baseline -> full override (legacy)

            var modifiedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Build a lookup of baseline nodes by ID for O(1) access
            var baselineLookup = new Dictionary<string, UIElementNode>(StringComparer.OrdinalIgnoreCase);
            BuildNodeLookup(baselineRoot, baselineLookup);

            // Walk the current tree and compare each node against its baseline counterpart
            CollectModifiedNodes(layout.RootElement, baselineLookup, modifiedIds);

            return modifiedIds;
        }

        /// <summary>
        /// Checks whether a specific node should have the override applied.
        /// Used during the recursive tree walk in ApplyNodeTransforms.
        ///
        /// Returns true if:
        ///   - modifiedIds is null (no baseline - full override, legacy behavior)
        ///   - The node's ID is in modifiedIds
        ///   - The node has the override_modified metadata flag
        ///   - The node is the root node (path is empty — root is always applied for
        ///     screen position/scale handling)
        /// </summary>
        public static bool ShouldApplyNode(UIElementNode node, HashSet<string> modifiedIds, string path)
        {
            // No baseline - apply everything (legacy behavior)
            if (modifiedIds == null) return true;

            // Root node is always "applied" (screen position logic needs it)
            if (string.IsNullOrEmpty(path)) return true;

            // Explicitly marked as modified by the user
            if (IsNodeMarkedModified(node)) return true;

            // Node ID is in the computed modified set
            if (!string.IsNullOrEmpty(node?.Id) && modifiedIds.Contains(node.Id))
                return true;

            return false;
        }

        /// <summary>
        /// Checks whether a node should have its children walked during override.
        /// Even if a node itself is not modified, its children might be — so we
        /// still need to recurse. Only returns false when we're certain no descendant
        /// is modified (which we can't cheaply determine), so this always returns true.
        /// The actual skip happens at the individual node level via ShouldApplyNode.
        /// </summary>
        public static bool ShouldRecurseIntoChildren(UIElementNode node, HashSet<string> modifiedIds)
        {
            // Always recurse — a child deep in the tree may be modified even if
            // its parent is not. The per-node check in ShouldApplyNode handles skipping.
            return true;
        }

        //  Per-node modified flag

        /// <summary>
        /// Returns true if the node has been explicitly marked as modified.
        /// </summary>
        public static bool IsNodeMarkedModified(UIElementNode node)
        {
            if (node == null) return false;
            string val = node.GetMeta(NodeModifiedKey);
            return !string.IsNullOrEmpty(val);
        }

        /// <summary>
        /// Marks a node as explicitly modified. The override will always apply this node.
        /// </summary>
        public static void MarkNodeModified(UIElementNode node)
        {
            if (node == null) return;
            node.SetMeta(NodeModifiedKey, "1");
        }

        /// <summary>
        /// Clears the modified flag from a node, returning it to baseline-comparison mode.
        /// </summary>
        public static void ClearNodeModified(UIElementNode node)
        {
            if (node == null) return;
            node.SetMeta(NodeModifiedKey, null);
        }

        /// <summary>
        /// Marks a node and all its descendants as modified.
        /// Useful when the user wants to force-include an entire subtree
        /// (e.g., a mod panel they want preserved after the mod is removed).
        /// </summary>
        public static void MarkSubtreeModified(UIElementNode node)
        {
            if (node == null) return;
            MarkNodeModified(node);
            if (node.Children != null)
            {
                for (int i = 0; i < node.Children.Count; i++)
                    MarkSubtreeModified(node.Children[i]);
            }
        }

        /// <summary>
        /// Clears the modified flag from a node and all its descendants.
        /// </summary>
        public static void ClearSubtreeModified(UIElementNode node)
        {
            if (node == null) return;
            ClearNodeModified(node);
            if (node.Children != null)
            {
                for (int i = 0; i < node.Children.Count; i++)
                    ClearSubtreeModified(node.Children[i]);
            }
        }

        //  Stats

        /// <summary>
        /// Returns a summary of modified vs total nodes for status display.
        /// </summary>
        public static DiffStats GetDiffStats(UILayoutDefinition layout)
        {
            var stats = new DiffStats();
            if (layout?.RootElement == null) return stats;

            CountNodes(layout.RootElement, ref stats.TotalNodes);

            var modifiedIds = ComputeModifiedNodeIds(layout);
            if (modifiedIds == null)
            {
                stats.HasBaseline = false;
                stats.ModifiedNodes = stats.TotalNodes;
            }
            else
            {
                stats.HasBaseline = true;
                stats.ModifiedNodes = modifiedIds.Count;
            }

            return stats;
        }

        public struct DiffStats
        {
            public int TotalNodes;
            public int ModifiedNodes;
            public bool HasBaseline;

            public override string ToString()
            {
                if (!HasBaseline) return $"{TotalNodes} nodes (no baseline — full override)";
                return $"{ModifiedNodes}/{TotalNodes} nodes modified";
            }
        }

        //  Internal comparison logic

        private static void CollectModifiedNodes(UIElementNode current, 
            Dictionary<string, UIElementNode> baselineLookup, HashSet<string> modifiedIds)
        {
            if (current == null) return;

            // Check if this node is explicitly marked modified
            if (IsNodeMarkedModified(current))
            {
                if (!string.IsNullOrEmpty(current.Id))
                    modifiedIds.Add(current.Id);
            }
            else if (!string.IsNullOrEmpty(current.Id))
            {
                // Try to find the baseline counterpart
                if (baselineLookup.TryGetValue(current.Id, out var baselineNode))
                {
                    // Compare the two nodes
                    if (!AreNodesEquivalent(current, baselineNode))
                        modifiedIds.Add(current.Id);
                }
                else
                {
                    // No baseline counterpart - new/injected node - always modified
                    modifiedIds.Add(current.Id);
                }
            }

            // Recurse into children
            if (current.Children != null)
            {
                for (int i = 0; i < current.Children.Count; i++)
                    CollectModifiedNodes(current.Children[i], baselineLookup, modifiedIds);
            }
        }

        /// <summary>
        /// Compares two nodes for equivalence. Only checks properties that the override
        /// system actually applies (transforms, visuals, active state). Ignores metadata,
        /// editor-only properties, and children (children are compared individually).
        /// </summary>
        private static bool AreNodesEquivalent(UIElementNode a, UIElementNode b)
        {
            if (a == null || b == null) return a == null && b == null;

            // Transform comparison
            if (!V2Equal(a.AnchorMin, b.AnchorMin)) return false;
            if (!V2Equal(a.AnchorMax, b.AnchorMax)) return false;
            if (!V2Equal(a.Pivot, b.Pivot)) return false;
            if (!V2Equal(a.AnchoredPosition, b.AnchoredPosition)) return false;
            if (!V2Equal(a.SizeDelta, b.SizeDelta)) return false;
            if (!V2Equal(a.OffsetMin, b.OffsetMin)) return false;
            if (!V2Equal(a.OffsetMax, b.OffsetMax)) return false;
            if (!FEqual(a.Rotation, b.Rotation)) return false;

            // Active state
            if (a.Active != b.Active) return false;

            // Style comparison (what ApplyNodeVisuals reads)
            if (!StylesEquivalent(a.Style, b.Style)) return false;

            // Image data comparison
            if (!ImageDefsEquivalent(a.ImageData, b.ImageData)) return false;

            // Text data comparison (text content, font size, color)
            if (!TextDefsEquivalent(a.TextData, b.TextData)) return false;

            // Child count change means structural modification
            int aChildCount = a.Children?.Count ?? 0;
            int bChildCount = b.Children?.Count ?? 0;
            if (aChildCount != bChildCount) return false;

            return true;
        }

        private static bool StylesEquivalent(UIElementStyle a, UIElementStyle b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;

            if (!CEqual(a.BackgroundColor, b.BackgroundColor)) return false;
            if (!FEqual(a.Opacity, b.Opacity)) return false;
            if (!string.Equals(a.BackgroundSprite ?? "", b.BackgroundSprite ?? "", StringComparison.Ordinal)) return false;
            if (a.ImageType != b.ImageType) return false;

            return true;
        }

        private static bool ImageDefsEquivalent(UIImageDef a, UIImageDef b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;

            if (!string.Equals(a.SpriteName ?? "", b.SpriteName ?? "", StringComparison.Ordinal)) return false;
            if (a.ImageType != b.ImageType) return false;
            if (a.PreserveAspect != b.PreserveAspect) return false;
            if (!CEqual(a.Color, b.Color)) return false;
            if (a.FillCenter != b.FillCenter) return false;
            if (!FEqual(a.PixelsPerUnit, b.PixelsPerUnit)) return false;

            return true;
        }

        private static bool TextDefsEquivalent(UITextDef a, UITextDef b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;

            if (!string.Equals(a.Text ?? "", b.Text ?? "", StringComparison.Ordinal)) return false;
            if (!FEqual(a.FontSize, b.FontSize)) return false;
            if (!CEqual(a.Color, b.Color)) return false;
            if (a.Alignment != b.Alignment) return false;
            if (a.FontStyle != b.FontStyle) return false;

            return true;
        }

        private static void BuildNodeLookup(UIElementNode node, Dictionary<string, UIElementNode> lookup)
        {
            if (node == null) return;
            if (!string.IsNullOrEmpty(node.Id))
                lookup[node.Id] = node;
            if (node.Children != null)
            {
                for (int i = 0; i < node.Children.Count; i++)
                    BuildNodeLookup(node.Children[i], lookup);
            }
        }

        private static void CountNodes(UIElementNode node, ref int count)
        {
            if (node == null) return;
            count++;
            if (node.Children != null)
            {
                for (int i = 0; i < node.Children.Count; i++)
                    CountNodes(node.Children[i], ref count);
            }
        }

        // Float/color/vector comparison helpers with tolerance
        private const float Epsilon = 0.001f;

        private static bool FEqual(float a, float b) => Mathf.Abs(a - b) < Epsilon;

        private static bool V2Equal(Vector2Ser a, Vector2Ser b) =>
            FEqual(a.X, b.X) && FEqual(a.Y, b.Y);

        private static bool CEqual(ColorSer a, ColorSer b) =>
            FEqual(a.R, b.R) && FEqual(a.G, b.G) && FEqual(a.B, b.B) && FEqual(a.A, b.A);
    }
}
