using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Analyzes a UI layout and determines which Runtime .cs files are needed
    /// for a standalone mod to run the UI. Generates a manifest and can copy
    /// the required files into an export folder.
    ///
    /// Use via the SDK or console command: <c>uibuilder_package [layout_uid] [output_path]</c>
    ///
    /// The analysis reads all element tags in the layout and maps each tag to
    /// the runtime class(es) that handle it. The output is a list of .cs files
    /// that the target mod needs, plus the base SDK files that are always required.
    /// </summary>
    public static class UIOverridePackagingHelper
    {
        //  Tag -> Required Files mapping

        /// <summary>
        /// Maps element tags to the runtime file(s) required to support them.
        /// Files are relative to the UIBuilder root (e.g., "Runtime/UIOverrideBarUpdater.cs").
        /// </summary>
        private static readonly Dictionary<string, string[]> TagToFiles =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            // HUD bars & text
            { UIOverrideElementTags.HealthBar, new[] { "Runtime/UIOverrideBarUpdater.cs" } },
            { UIOverrideElementTags.StaminaBar, new[] { "Runtime/UIOverrideBarUpdater.cs" } },
            { UIOverrideElementTags.EitrBar, new[] { "Runtime/UIOverrideBarUpdater.cs" } },
            { UIOverrideElementTags.FoodBar, new[] { "Runtime/UIOverrideBarUpdater.cs" } },
            { UIOverrideElementTags.GuardianBar, new[] { "Runtime/UIOverrideBarUpdater.cs" } },
            { UIOverrideElementTags.HealthText, new[] { "Runtime/UIOverrideTextUpdater.cs" } },
            { UIOverrideElementTags.StaminaText, new[] { "Runtime/UIOverrideTextUpdater.cs" } },
            { UIOverrideElementTags.EitrText, new[] { "Runtime/UIOverrideTextUpdater.cs" } },
            { UIOverrideElementTags.WeightText, new[] { "Runtime/UIOverrideTextUpdater.cs" } },
            { UIOverrideElementTags.DayTimeText, new[] { "Runtime/UIOverrideTextUpdater.cs" } },
            { UIOverrideElementTags.BiomeText, new[] { "Runtime/UIOverrideTextUpdater.cs" } },
            { UIOverrideElementTags.ComfortText, new[] { "Runtime/UIOverrideTextUpdater.cs" } },
            { UIOverrideElementTags.GuardianText, new[] { "Runtime/UIOverrideTextUpdater.cs" } },

            // Minimap & Map
            { UIOverrideElementTags.MinimapDisplay, new[] { "Runtime/UIOverrideMinimapWirer.cs" } },
            { UIOverrideElementTags.MapDisplay, new[] { "Runtime/UIOverrideMapWiring.cs" } },

            // Inventory system
            { UIOverrideElementTags.InventoryGrid, new[] {
                "Runtime/UIOverrideSlotSystem.cs",
                "Runtime/UIOverrideSlotMirror.cs",
                "Runtime/UIOverrideSlotInteraction.cs",
                "Runtime/UIOverrideSlotTooltipHandler.cs",
                "Runtime/UIOverrideEquipmentPanel.cs",
                "Runtime/UIOverrideInventoryPatches.cs",
                "Runtime/UIOverrideInventoryWiring.cs",
            } },
            { UIOverrideElementTags.EquipmentSlot, new[] {
                "Runtime/UIOverrideSlotSystem.cs",
                "Runtime/UIOverrideSlotMirror.cs",
                "Runtime/UIOverrideSlotInteraction.cs",
                "Runtime/UIOverrideSlotTooltipHandler.cs",
                "Runtime/UIOverrideEquipmentPanel.cs",
                "Runtime/UIOverrideInventoryPatches.cs",
            } },
            { UIOverrideElementTags.EquipmentPanel, new[] {
                "Runtime/UIOverrideSlotSystem.cs",
                "Runtime/UIOverrideSlotMirror.cs",
                "Runtime/UIOverrideSlotInteraction.cs",
                "Runtime/UIOverrideSlotTooltipHandler.cs",
                "Runtime/UIOverrideEquipmentPanel.cs",
                "Runtime/UIOverrideInventoryPatches.cs",
            } },
            { UIOverrideElementTags.ToolHotbarSlot, new[] {
                "Runtime/UIOverrideSlotSystem.cs",
                "Runtime/UIOverrideHotbarSlotMirror.cs",
                "Runtime/UIOverrideHotbarManager.cs",
                "Runtime/UIOverrideEquipmentPanel.cs",
            } },
            { UIOverrideElementTags.ToolHotbarBar, new[] {
                "Runtime/UIOverrideSlotSystem.cs",
                "Runtime/UIOverrideHotbarSlotMirror.cs",
                "Runtime/UIOverrideHotbarManager.cs",
                "Runtime/UIOverrideEquipmentPanel.cs",
            } },
            { UIOverrideElementTags.QuickSlot, new[] {
                "Runtime/UIOverrideSlotSystem.cs",
                "Runtime/UIOverrideHotbarSlotMirror.cs",
                "Runtime/UIOverrideHotbarManager.cs",
                "Runtime/UIOverrideEquipmentPanel.cs",
            } },

            // Container
            { UIOverrideElementTags.ContainerPanel, new[] {
                "Runtime/UIOverrideContainerWiring.cs",
                "Runtime/UIOverrideEquipmentPanel.cs",
            } },

            // Player preview
            { UIOverrideElementTags.PlayerPreview, new[] { "Runtime/UIOverridePlayerPreview.cs" } },
            { UIOverrideElementTags.CameraDisplay, new[] { "Runtime/UIOverridePlayerPreview.cs" } },

            // Status effects
            { UIOverrideElementTags.StatusEffectContainer, new[] { "Runtime/UIOverrideStatusEffectSync.cs" } },

            // Crafting
            { UIOverrideElementTags.CraftingPanel, new[] { "Runtime/UIOverrideCraftingWiring.cs" } },

            // Skills
            { UIOverrideElementTags.SkillsPanel, new[] { "Runtime/UIOverrideSkillsWiring.cs" } },

            // Drag & drop
            { UIOverrideElementTags.DropZone, new[] {
                "Runtime/UIOverrideDropZoneHandler.cs",
                "Runtime/UIOverrideDragGhostManager.cs",
                "Runtime/UIOverrideEquipmentPanel.cs",
            } },
        };

        /// <summary>
        /// Files that are ALWAYS required for any SDK-based mod.
        /// </summary>
        private static readonly string[] AlwaysRequired = new[]
        {
            "SDK/UIBuilderSDK.cs",
            "Data/UILayoutDefinition.cs",
            "Data/UILayoutSerializer.cs",
            "Runtime/UICanvasRenderer.cs",
            "Runtime/UIBuilderElementTag.cs",
            "Runtime/UIBuilderAssetCache.cs",
            "Runtime/UIOverrideElementTags.cs",
        };

        //  Analysis

        /// <summary>
        /// Analyzes a layout and returns the set of .cs files needed for a standalone mod.
        /// </summary>
        public static PackageManifest AnalyzeLayout(UILayoutDefinition layout)
        {
            var manifest = new PackageManifest();
            manifest.LayoutUID = layout?.UID ?? "unknown";

            // Always-required files
            foreach (var requiredFile in AlwaysRequired)
                manifest.RequiredFiles.Add(requiredFile);

            if (layout == null || layout.RootElement == null)
                return manifest;

            // Walk the element tree and collect tags
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectTags(layout.RootElement, tags);
            manifest.DetectedTags.AddRange(tags);

            // Map tags to files
            foreach (var tag in tags)
            {
                if (TagToFiles.TryGetValue(tag, out var files))
                {
                    foreach (var file in files)
                        manifest.RequiredFiles.Add(file);
                }
            }

            // If any inventory tag is present, also include the wiring orchestrator
            bool hasInventoryTag = tags.Any(t =>
                t.StartsWith("override_inventory", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("override_equipment", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("override_tool_hotbar", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("override_quick", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("override_container", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("override_drop_zone", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("override_player_preview", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("override_camera_display", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("override_status", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("override_crafting", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("override_skills", StringComparison.OrdinalIgnoreCase) ||
                t.StartsWith("override_map", StringComparison.OrdinalIgnoreCase));

            if (hasInventoryTag)
                manifest.RequiredFiles.Add("Runtime/UIOverrideInventoryWiring.cs");

            // If any drag-capable slots exist, include drag ghost
            bool hasDraggableSlots = tags.Any(t =>
                string.Equals(t, UIOverrideElementTags.InventoryGrid, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, UIOverrideElementTags.EquipmentPanel, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, UIOverrideElementTags.EquipmentSlot, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, UIOverrideElementTags.EquipmentAutoLayout, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, UIOverrideElementTags.ContainerPanel, StringComparison.OrdinalIgnoreCase));

            if (hasDraggableSlots)
            {
                manifest.RequiredFiles.Add("Runtime/UIOverrideDragGhostManager.cs");
                manifest.RequiredFiles.Add("Runtime/UIOverrideDropZoneHandler.cs");
            }

            return manifest;
        }

        /// <summary>
        /// Analyzes a layout by UID.
        /// </summary>
        public static PackageManifest AnalyzeLayout(string layoutUID)
        {
            var layout = UILayoutCodex.Get(layoutUID);
            return AnalyzeLayout(layout);
        }

        private static void CollectTags(UIElementNode node, HashSet<string> tags)
        {
            if (node == null) return;

            if (!string.IsNullOrEmpty(node.Tag))
                tags.Add(node.Tag);

            if (node.Children != null)
            {
                foreach (var child in node.Children)
                    CollectTags(child, tags);
            }
        }

        //  Manifest

        public class PackageManifest
        {
            public string LayoutUID;
            public readonly HashSet<string> RequiredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> DetectedTags = new List<string>();

            /// <summary>
            /// Returns a sorted list of all required files.
            /// </summary>
            public List<string> GetSortedFiles()
            {
                var list = new List<string>(RequiredFiles);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                return list;
            }

            /// <summary>
            /// Returns a human-readable summary of the manifest.
            /// </summary>
            public string FormatSummary()
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"=== Package Manifest for '{LayoutUID}' ===");
                sb.AppendLine();
                sb.AppendLine($"Detected tags ({DetectedTags.Count}):");
                foreach (var tag in DetectedTags)
                    sb.AppendLine($"  • {tag}");
                sb.AppendLine();
                var files = GetSortedFiles();
                sb.AppendLine($"Required files ({files.Count}):");
                foreach (var file in files)
                    sb.AppendLine($"  • {file}");
                return sb.ToString();
            }
        }

        //  Export

        /// <summary>
        /// Exports the required files for a layout to an output directory.
        /// The source directory should be the UIBuilder root folder.
        /// Returns the count of files copied.
        /// </summary>
        public static int ExportPackage(string layoutUID, string sourceDir, string outputDir)
        {
            var manifest = AnalyzeLayout(layoutUID);
            return ExportPackage(manifest, sourceDir, outputDir);
        }

        /// <summary>
        /// Exports the required files from a manifest.
        /// </summary>
        public static int ExportPackage(PackageManifest manifest, string sourceDir, string outputDir)
        {
            if (manifest == null || string.IsNullOrEmpty(sourceDir) || string.IsNullOrEmpty(outputDir))
                return 0;

            int copied = 0;
            var files = manifest.GetSortedFiles();

            foreach (var relPath in files)
            {
                string srcPath = Path.Combine(sourceDir, relPath);
                string dstPath = Path.Combine(outputDir, relPath);

                if (!File.Exists(srcPath))
                {
                    Debug.LogWarning($"[UIOverridePackagingHelper] Source file not found: {srcPath}");
                    continue;
                }

                string dstDir = Path.GetDirectoryName(dstPath);
                if (!string.IsNullOrEmpty(dstDir) && !Directory.Exists(dstDir))
                    Directory.CreateDirectory(dstDir);

                File.Copy(srcPath, dstPath, true);
                copied++;
            }

            // Also copy the layout JSON
            var layout = UILayoutCodex.Get(manifest.LayoutUID);
            if (layout != null)
            {
                string jsonDir = Path.Combine(outputDir, "Layouts");
                if (!Directory.Exists(jsonDir))
                    Directory.CreateDirectory(jsonDir);

                string json = UILayoutSerializer.Serialize(layout);
                string jsonPath = Path.Combine(jsonDir, manifest.LayoutUID + ".json");
                File.WriteAllText(jsonPath, json);
                copied++;
            }

            Debug.Log($"[UIOverridePackagingHelper] Exported {copied} files to {outputDir}");
            return copied;
        }
    }
}
