using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI
{
    /// <summary>
    /// Describes a UI canvas discovered at runtime via scene scanning.
    /// May be from our mod, vanilla Valheim, or a third-party mod.
    /// </summary>
    public class DiscoveredUITarget
    {
        public string DisplayName;
        public string RootGameObjectPath;
        public string SourceCategory;       // "Ours", "Vanilla", "Modded"
        public string SourceAssembly;       // e.g. "EpicLoot.dll"
        public string SourceMod;            // BepInPlugin GUID if detectable
        public bool IsVisible;
        public int ChildCount;
        public int TotalNodeEstimate;
        public Canvas Canvas;
        public GameObject Root;
        public string CanvasRenderMode;
        public int SortingOrder;
    }

    /// <summary>
    /// Scans the scene for all active Canvas components and classifies them as
    /// Ours (VerdantsAscent), Vanilla (Valheim), or Modded (other BepInEx plugins).
    /// This allows the UI Builder to capture any UI on screen, not just hardcoded targets.
    /// </summary>
    public static class UIBuilderAutoDiscovery
    {
        private static List<DiscoveredUITarget> _lastDiscovery;

        // Names of our editor canvases — always excluded from discovery
        private static readonly HashSet<string> EditorCanvasNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UIBuilder_Workspace",
            "UIBuilder_Overlay",
            "UIBuilder_Panels"
        };

        // Known Valheim root GameObject names
        private static readonly HashSet<string> VanillaRootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IngameGui", "GuiRoot", "LoadingGUI", "Minimap", "HudMessage",
            "Menu", "Inventory_screen", "Store_Screen", "TextInput",
            "console", "ConnectPanel", "Hud"
        };

        // Known Valheim assembly names
        private static readonly HashSet<string> VanillaAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "assembly_valheim",
            "assembly_guiutils",
            "assembly_utils",
            "assembly_steamworks",
            "assembly_googleanalytics"
        };

        // Unity engine assembly prefixes — always ignored for source detection
        private static readonly string[] UnityAssemblyPrefixes =
        {
            "UnityEngine",
            "Unity.",
            "mscorlib",
            "System",
            "netstandard",
            "Mono.",
            "TMPro"
        };

        /// <summary>
        /// Returns the most recent discovery results, or performs a new scan if none exist.
        /// </summary>
        public static List<DiscoveredUITarget> GetDiscoveredTargets()
        {
            if (_lastDiscovery == null)
                RefreshDiscovery();
            return _lastDiscovery;
        }

        /// <summary>
        /// Performs a full scene scan for all Canvas components and classifies each one.
        /// </summary>
        public static void RefreshDiscovery()
        {
            _lastDiscovery = DiscoverAllCanvases();
        }

        /// <summary>
        /// Scans all loaded Canvas objects and returns a list of capturable UI targets.
        /// Filters out our own editor canvases, WorldSpace canvases, and empty canvases.
        /// </summary>
        public static List<DiscoveredUITarget> DiscoverAllCanvases()
        {
            var results = new List<DiscoveredUITarget>();
            var seenRoots = new HashSet<int>(); // instance IDs to avoid duplicates

            Canvas[] allCanvases;
            try
            {
                allCanvases = Resources.FindObjectsOfTypeAll<Canvas>();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIBuilderAutoDiscovery] Failed to scan canvases: {ex.Message}");
                return results;
            }

            for (int i = 0; i < allCanvases.Length; i++)
            {
                Canvas canvas = allCanvases[i];
                if (canvas == null) continue;

                try
                {
                    var go = canvas.gameObject;
                    if (go == null) continue;

                    // Skip duplicates (same root scanned via nested canvases)
                    int rootId = GetRootCanvasInstanceId(canvas);
                    if (!seenRoots.Add(rootId)) continue;

                    // Skip our editor canvases
                    if (EditorCanvasNames.Contains(go.name)) continue;

                    // Skip WorldSpace canvases (not capturable as 2D UI)
                    if (canvas.renderMode == RenderMode.WorldSpace) continue;

                    // Skip canvases with no RectTransform children
                    int childCount = CountRectTransformChildren(go.transform);
                    if (childCount == 0) continue;

                    // Classify the canvas
                    string sourceCategory;
                    string sourceAssembly;
                    string sourceMod;
                    ClassifyCanvas(go, out sourceCategory, out sourceAssembly, out sourceMod);

                    string renderMode;
                    switch (canvas.renderMode)
                    {
                        case RenderMode.ScreenSpaceOverlay: renderMode = "ScreenSpaceOverlay"; break;
                        case RenderMode.ScreenSpaceCamera: renderMode = "ScreenSpaceCamera"; break;
                        default: renderMode = canvas.renderMode.ToString(); break;
                    }

                    var target = new DiscoveredUITarget
                    {
                        DisplayName = BuildDisplayName(go, sourceCategory, sourceAssembly),
                        RootGameObjectPath = GetGameObjectPath(go),
                        SourceCategory = sourceCategory,
                        SourceAssembly = sourceAssembly,
                        SourceMod = sourceMod,
                        IsVisible = go.activeInHierarchy && canvas.enabled,
                        ChildCount = childCount,
                        TotalNodeEstimate = EstimateTotalNodes(go.transform, 4),
                        Canvas = canvas,
                        Root = go,
                        CanvasRenderMode = renderMode,
                        SortingOrder = canvas.sortingOrder
                    };

                    results.Add(target);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UIBuilderAutoDiscovery] Error processing canvas: {ex.Message}");
                }
            }

            // Sort: Ours first, then Vanilla, then Modded. Within each group, sort by name.
            results.Sort((a, b) =>
            {
                int catA = CategoryOrder(a.SourceCategory);
                int catB = CategoryOrder(b.SourceCategory);
                if (catA != catB) return catA.CompareTo(catB);
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
            });

            return results;
        }

        /// <summary>
        /// Classifies a canvas root as "Ours", "Vanilla", or "Modded" by examining
        /// the MonoBehaviour components on the root and its first few levels of children.
        /// </summary>
        public static void ClassifyCanvas(GameObject root, out string category, out string assembly, out string modGuid)
        {
            category = "Modded";
            assembly = "";
            modGuid = "";

            if (root == null) return;

            // Check the root and up to 3 levels of children for identifying components
            var components = root.GetComponentsInChildren<MonoBehaviour>(true);
            bool foundOurs = false;
            bool foundVanilla = false;
            string firstModAssembly = null;
            string firstModGuid = null;

            for (int i = 0; i < components.Length; i++)
            {
                var comp = components[i];
                if (comp == null) continue;

                Type compType = comp.GetType();
                string ns = compType.Namespace ?? "";
                string asmName = compType.Assembly?.GetName()?.Name ?? "";

                // Skip Unity engine types
                if (IsUnityAssembly(asmName)) continue;

                // Check for our namespace
                if (ns.StartsWith("VerdantsAscent", StringComparison.OrdinalIgnoreCase))
                {
                    foundOurs = true;
                    break; // Our mod takes priority
                }

                // Check for vanilla Valheim assemblies
                if (VanillaAssemblies.Contains(asmName))
                {
                    foundVanilla = true;
                    continue;
                }

                // Must be a modded component
                if (firstModAssembly == null && !string.IsNullOrEmpty(asmName))
                {
                    firstModAssembly = asmName + ".dll";

                    // Try to find the BepInPlugin attribute on the assembly for mod GUID
                    try
                    {
                        var pluginAttrs = compType.Assembly.GetCustomAttributes(
                            typeof(BepInEx.BepInPlugin), false);
                        if (pluginAttrs.Length > 0)
                        {
                            var attr = pluginAttrs[0] as BepInEx.BepInPlugin;
                            if (attr != null)
                                firstModGuid = attr.GUID;
                        }
                    }
                    catch { /* attribute lookup may fail */ }
                }
            }

            if (foundOurs)
            {
                category = "Ours";
                assembly = UIBuilderHost.ModAssemblyName;
            }
            else if (foundVanilla && firstModAssembly == null)
            {
                category = "Vanilla";
                assembly = "assembly_valheim.dll";
            }
            else if (firstModAssembly != null)
            {
                category = "Modded";
                assembly = firstModAssembly;
                modGuid = firstModGuid ?? "";
            }
            else
            {
                // No MonoBehaviours found — check GO name against known vanilla names
                if (VanillaRootNames.Contains(root.name))
                {
                    category = "Vanilla";
                    assembly = "assembly_valheim.dll";
                }
                else
                {
                    category = "Unknown";
                }
            }
        }

        /// <summary>
        /// Checks if a sprite should be cached to disk during capture.
        /// Used to decide whether to clone the sprite's pixels to a PNG file.
        ///
        /// Strategy: Cache ALL sprites except Unity built-in ones that are guaranteed
        /// to always be available at runtime. This is intentionally aggressive because:
        /// - Mods like Azumatt's Minimal UI replace vanilla sprites with custom ones
        /// - We cannot reliably distinguish vanilla sprites from modded replacements
        /// - At render time, the original mod may not be loaded, so FindLoadedSprite fails
        /// - Caching a few extra PNGs is harmless; missing a sprite breaks the layout
        ///
        /// The only sprites we skip are Unity's built-in UI sprites (UISprite, Background,
        /// Checkmark, etc.) which are always present in every Unity application.
        /// </summary>
        public static bool IsSpriteFromMod(Sprite sprite)
        {
            if (sprite == null) return false;

            string name = sprite.name;
            if (string.IsNullOrEmpty(name)) return false;

            // Known Unity built-in sprite names — always available in every Unity app, never need caching
            if (name == "UISprite" || name == "Background" || name == "InputFieldBackground" ||
                name == "Checkmark" || name == "Knob" || name == "UIMask" ||
                name == "UISpriteLegacy" || name == "DropdownArrow" || name == "UnitySplash")
                return false;

            // Check if the sprite is in our own asset bundle or external Sprites folder.
            // These are always available at runtime regardless of other mods.
            var ours = VAMiscAssetManager.GetSprite(name);
            if (ours != null && ours == sprite)
                return false;

            // Cache everything else — vanilla Valheim sprites, modded sprites, all of it.
            // Mods can replace vanilla sprites at runtime (e.g., Minimal UI replacing HUD icons),
            // and we cannot tell the difference between the original and the replacement.
            // At render/preview time the mod may not be loaded, so we must have the pixels on disk.
            return true;
        }

        /// <summary>
        /// Checks if a texture name matches known Valheim game texture naming patterns.
        /// </summary>
        public static bool IsKnownValheimTextureName(string texName)
        {
            if (string.IsNullOrEmpty(texName)) return false;

            string lower = texName.ToLowerInvariant();

            // Direct known Valheim UI texture names (atlas textures, icon sheets, etc.)
            if (lower == "water" || lower == "mapbackground" || lower == "nomap" ||
                lower == "cloudsoverlayer" || lower == "fogofwar" ||
                lower == "crosshair" || lower == "dot" || lower == "circle" ||
                lower == "roundmask" || lower == "selection" || lower == "skull" ||
                lower == "softglow" || lower == "square" || lower == "health_color" ||
                lower == "hammergizmo" || lower == "dragobj" || lower == "woodpanel_trophies")
                return true;

            // Valheim GUI texture prefixes
            if (lower.StartsWith("inventory_") || lower.StartsWith("hud_") ||
                lower.StartsWith("menu_") || lower.StartsWith("store_") ||
                lower.StartsWith("map_") || lower.StartsWith("crafting_") ||
                lower.StartsWith("skill_") || lower.StartsWith("piece_") ||
                lower.StartsWith("guardianpower_") || lower.StartsWith("demister_"))
                return true;

            return false;
        }

        // ???????????????????????????????????????
        //  Helpers
        // ???????????????????????????????????????

        private static bool IsUnityAssembly(string assemblyName)
        {
            if (string.IsNullOrEmpty(assemblyName)) return true;
            for (int i = 0; i < UnityAssemblyPrefixes.Length; i++)
            {
                if (assemblyName.StartsWith(UnityAssemblyPrefixes[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static int GetRootCanvasInstanceId(Canvas canvas)
        {
            var root = canvas.rootCanvas;
            return root != null ? root.gameObject.GetInstanceID() : canvas.gameObject.GetInstanceID();
        }

        private static int CountRectTransformChildren(Transform t)
        {
            int count = 0;
            for (int i = 0; i < t.childCount; i++)
            {
                if (t.GetChild(i).GetComponent<RectTransform>() != null)
                    count++;
            }
            return count;
        }

        private static int EstimateTotalNodes(Transform t, int maxDepth)
        {
            if (maxDepth <= 0) return 1;
            int count = 1;
            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                if (child.GetComponent<RectTransform>() != null)
                    count += EstimateTotalNodes(child, maxDepth - 1);
            }
            return count;
        }

        private static string GetGameObjectPath(GameObject go)
        {
            if (go == null) return "";
            string path = go.name;
            var t = go.transform.parent;
            int depth = 0;
            while (t != null && depth < 5)
            {
                path = t.name + "/" + path;
                t = t.parent;
                depth++;
            }
            return path;
        }

        private static string BuildDisplayName(GameObject go, string category, string assembly)
        {
            string name = go.name;
            // Clean up common suffixes
            if (name.EndsWith("(Clone)"))
                name = name.Substring(0, name.Length - 7).Trim();

            return name;
        }

        private static int CategoryOrder(string category)
        {
            switch (category)
            {
                case "Ours": return 0;
                case "Vanilla": return 1;
                case "Modded": return 2;
                default: return 3;
            }
        }
    }
}
