using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Audits Harmony patches and runtime MonoBehaviours on UI GameObjects at capture time.
    ///
    /// Records which mods modify which game methods and which components exist on the hierarchy.
    /// Data is stored two ways:
    ///   1. **Layout metadata** � compact pipe-delimited strings for programmatic dependency
    ///      checking at override-apply time (GetMissingDependencies).
    ///   2. **Markdown report files** � human-readable .md files organized per-mod, written to
    ///      Config/FiresRPGmaker/UIAudits/{layoutUID}/. Each mod gets its own file with
    ///      full IL disassembly of every patch method body, referenced fields/methods/types,
    ///      and C# code stubs. A summary index file links to all per-mod reports.
    ///
    /// Layout metadata keys:
    ///   "harmony_patches"    � pipe-delimited patch records
    ///   "mod_components"     � pipe-delimited component records
    ///   "dependency_mods"    � comma-separated mod assembly names
    ///   "dependency_plugins" � human-readable BepInEx plugin info
    /// </summary>
    public static class UIHarmonyPatchAuditor
    {
        // ???????????????????????????????????????
        //  Structured audit record types
        // ???????????????????????????????????????

        public class PatchRecord
        {
            public string TargetTypeName;
            public string TargetMethodName;
            public string TargetMethodSignature;
            public string[] TargetMethodParameters;
            public string TargetMethodReturnType;
            public bool TargetMethodIsStatic;
            public string PatchType;
            public string HarmonyOwner;
            public string ModAssemblyName;
            public string PatchDeclaringType;
            public string PatchMethodName;
            public string PatchMethodSignature;
            public int Priority;
            /// <summary>Live MethodInfo for the patch method � used for IL extraction during report generation.</summary>
            public MethodInfo PatchMethodInfo;
        }

        public class ComponentRecord
        {
            public string FullTypeName;
            public string AssemblyName;
            public string GameObjectPath;
            public bool Enabled;
        }

        public class PluginRecord
        {
            public string Name;
            public string Version;
            public string GUID;
            public string AssemblyName;
        }

        public class AuditResult
        {
            public List<PatchRecord> Patches = new List<PatchRecord>();
            public List<ComponentRecord> Components = new List<ComponentRecord>();
            public List<PluginRecord> Plugins = new List<PluginRecord>();
            public HashSet<string> ModAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        // ???????????????????????????????????????
        //  Known UI types to audit for patches
        // ???????????????????????????????????????

        private static readonly Type[] _auditTargetTypes = new Type[]
        {
            typeof(InventoryGui),
            typeof(InventoryGrid),
            typeof(Hud),
            typeof(Minimap),
            typeof(Menu),
            typeof(StoreGui),
            typeof(Player),
        };

        private static readonly HashSet<string> _uiRelevantMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Awake", "Start", "Show", "Hide", "Update", "LateUpdate",
            "UpdateGui", "UpdateInventory", "UpdateRecipe", "UpdateRecipeList",
            "UpdateRepair", "UpdateCraftingPanel", "SetupRequirement",
            "UpdateContainer", "OnOpenInventory", "OnClosedInventory",
            "OnSelectedItem", "OnRightClickItem",
            "SetupDragItem", "UpdateItemDrag",
            "UpdateInventoryWeight",
            "UpdateHealth", "UpdateStamina", "UpdateEitr", "UpdateFood",
            "UpdateStatusEffects", "UpdateGuardianPower",
            "SetMapMode", "UpdateMap", "UpdateBiome",
            "UpdateGui", "OnLeftClick", "OnRightClick",
            "GetElement", "CreateItemTooltip",
        };

        private static readonly HashSet<string> _skipAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UnityEngine", "UnityEngine.CoreModule", "UnityEngine.UI", "UnityEngine.UIModule",
            "UnityEngine.IMGUIModule", "UnityEngine.TextRenderingModule", "UnityEngine.InputLegacyModule",
            "Unity.TextMeshPro", "0Harmony", "BepInEx",
            "assembly_valheim", "assembly_utils", "assembly_guiutils",
            "assembly_steamworks", "assembly_googleanalytics",
            "mscorlib", "System", "System.Core",
        };

        private const string OurHarmonyId = "com.Fire.FiresRPGmaker";

        private static string AuditDir =>
            Path.Combine(Paths.ConfigPath, "FiresRPGmaker", "UIAudits");

        // ???????????????????????????????????????
        //  Public API
        // ???????????????????????????????????????

        /// <summary>
        /// Performs a full audit of the captured UI hierarchy, records results as layout
        /// metadata for programmatic use, AND writes a human-readable Markdown report
        /// to Config/FiresRPGmaker/UIAudits/{layoutUID}_audit.md.
        /// </summary>
        public static void AuditAndAnnotate(UILayoutDefinition layout, GameObject capturedRoot)
        {
            if (layout == null) return;

            try
            {
                var result = RunFullAudit(capturedRoot);

                // Store compact metadata on the layout for programmatic dependency checking
                StoreMetadata(layout, result);

                // Write the human-readable report file to disk
                ExportReportToDisk(layout, result);

                Debug.Log($"[UIHarmonyPatchAuditor] Audit complete: {result.Patches.Count} patched methods, " +
                    $"{result.Components.Count} mod components, {result.ModAssemblyNames.Count} mod(s)");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIHarmonyPatchAuditor] Audit failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Runs the full audit and returns the structured result without storing anything.
        /// Useful for on-demand inspection or re-export.
        /// </summary>
        public static AuditResult RunFullAudit(GameObject capturedRoot)
        {
            var result = new AuditResult();

            // Phase 1: Harmony patches
            ScanHarmonyPatches(result);

            // Phase 2: Runtime components
            if (capturedRoot != null)
                ScanRuntimeComponents(capturedRoot, result);

            // Phase 3: BepInEx plugins
            ScanBepInExPlugins(result);

            return result;
        }

        /// <summary>
        /// Writes Markdown audit reports to disk. Produces:
        ///   - A summary index file: UIAudits/{uid}_audit.md
        ///   - One per-mod file: UIAudits/{uid}/{ModName}.md
        /// Per-mod files contain full IL disassembly, referenced members, and code stubs.
        /// </summary>
        public static string ExportReportToDisk(UILayoutDefinition layout, AuditResult result = null)
        {
            if (layout == null) return null;

            try
            {
                string uid = layout.UID;
                if (string.IsNullOrEmpty(uid)) uid = "unknown";

                string safeUID = SanitizeFileName(uid);
                string layoutAuditDir = Path.Combine(AuditDir, safeUID);
                Directory.CreateDirectory(layoutAuditDir);

                string captureTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                int modFilesWritten = 0;

                if (result != null && result.Patches.Count > 0)
                {
                    // Group patches by mod
                    var byMod = new Dictionary<string, List<PatchRecord>>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < result.Patches.Count; i++)
                    {
                        string mod = result.Patches[i].ModAssemblyName ?? "Unknown";
                        if (!byMod.TryGetValue(mod, out var list))
                        {
                            list = new List<PatchRecord>();
                            byMod[mod] = list;
                        }
                        list.Add(result.Patches[i]);
                    }

                    // Find plugin record for each mod
                    var pluginByAsm = new Dictionary<string, PluginRecord>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < result.Plugins.Count; i++)
                        pluginByAsm[result.Plugins[i].AssemblyName] = result.Plugins[i];

                    // Find components for each mod
                    var compsByAsm = new Dictionary<string, List<ComponentRecord>>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < result.Components.Count; i++)
                    {
                        string asm = result.Components[i].AssemblyName;
                        if (!compsByAsm.TryGetValue(asm, out var cList))
                        {
                            cList = new List<ComponentRecord>();
                            compsByAsm[asm] = cList;
                        }
                        cList.Add(result.Components[i]);
                    }

                    // Write per-mod files
                    foreach (var kvp in byMod)
                    {
                        string modName = kvp.Key;
                        string safeModName = SanitizeFileName(modName);
                        string modFilePath = Path.Combine(layoutAuditDir, $"{safeModName}.md");

                        pluginByAsm.TryGetValue(modName, out var plugin);
                        compsByAsm.TryGetValue(modName, out var modComps);

                        string modMarkdown = BuildPerModReport(layout, modName, plugin, kvp.Value, modComps, captureTime);
                        File.WriteAllText(modFilePath, modMarkdown, Encoding.UTF8);
                        modFilesWritten++;
                    }

                    // Write files for mods that have components but no patches
                    foreach (var kvp in compsByAsm)
                    {
                        if (byMod.ContainsKey(kvp.Key)) continue; // Already written
                        string modName = kvp.Key;
                        string safeModName = SanitizeFileName(modName);
                        string modFilePath = Path.Combine(layoutAuditDir, $"{safeModName}.md");

                        pluginByAsm.TryGetValue(modName, out var plugin);

                        string modMarkdown = BuildPerModReport(layout, modName, plugin, null, kvp.Value, captureTime);
                        File.WriteAllText(modFilePath, modMarkdown, Encoding.UTF8);
                        modFilesWritten++;
                    }
                }

                // Write summary index
                string indexPath = Path.Combine(AuditDir, $"{safeUID}_audit.md");
                string indexMarkdown = result != null
                    ? BuildSummaryIndex(layout, result, safeUID, captureTime)
                    : BuildMarkdownFromMetadata(layout);
                File.WriteAllText(indexPath, indexMarkdown, Encoding.UTF8);

                Debug.Log($"[UIHarmonyPatchAuditor] Audit reports written: {indexPath} + {modFilesWritten} per-mod file(s)");
                return indexPath;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIHarmonyPatchAuditor] Failed to write audit reports: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns the file path where a layout's audit summary index would be written.
        /// </summary>
        public static string GetReportPath(string layoutUID)
        {
            if (string.IsNullOrEmpty(layoutUID)) return null;
            string safeUID = SanitizeFileName(layoutUID);
            return Path.Combine(AuditDir, $"{safeUID}_audit.md");
        }

        /// <summary>
        /// Returns the directory where per-mod audit files for a layout are stored.
        /// </summary>
        public static string GetReportDirectory(string layoutUID)
        {
            if (string.IsNullOrEmpty(layoutUID)) return null;
            string safeUID = SanitizeFileName(layoutUID);
            return Path.Combine(AuditDir, safeUID);
        }

        /// <summary>
        /// Checks if any dependency mods recorded in a layout are currently missing.
        /// </summary>
        public static List<string> GetMissingDependencies(UILayoutDefinition layout)
        {
            var missing = new List<string>();
            if (layout == null) return missing;

            string deps = layout.GetMeta("dependency_mods");
            if (string.IsNullOrEmpty(deps)) return missing;

            var loadedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { loadedAssemblies.Add(asm.GetName().Name); }
                catch { }
            }

            foreach (string mod in deps.Split(','))
            {
                string trimmed = mod.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                if (!loadedAssemblies.Contains(trimmed))
                    missing.Add(trimmed);
            }

            return missing;
        }

        /// <summary>
        /// Formats a human-readable summary for in-game UI display.
        /// For the full detailed report, see the .md file in UIAudits/.
        /// </summary>
        public static string FormatAuditSummary(UILayoutDefinition layout)
        {
            if (layout == null) return "No layout";

            var sb = new StringBuilder();

            string deps = layout.GetMeta("dependency_mods");
            if (!string.IsNullOrEmpty(deps))
            {
                sb.AppendLine("Dependency Mods:");
                foreach (string mod in deps.Split(','))
                {
                    string trimmed = mod.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        sb.AppendLine($"  � {trimmed}");
                }
            }

            var missing = GetMissingDependencies(layout);
            if (missing.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("? Missing Mods:");
                foreach (string mod in missing)
                    sb.AppendLine($"  ? {mod}");
            }

            string plugins = layout.GetMeta("dependency_plugins");
            if (!string.IsNullOrEmpty(plugins))
            {
                sb.AppendLine();
                sb.AppendLine("Plugin Details:");
                sb.AppendLine(plugins);
            }

            // Point to the full report files
            string reportPath = GetReportPath(layout.UID);
            string reportDir = GetReportDirectory(layout.UID);
            if (reportDir != null && Directory.Exists(reportDir))
            {
                sb.AppendLine();
                sb.AppendLine($"Per-mod reports: {reportDir}");
            }
            else if (reportPath != null && File.Exists(reportPath))
            {
                sb.AppendLine();
                sb.AppendLine($"Full report: {reportPath}");
            }

            if (sb.Length == 0)
                sb.Append("No mod dependencies detected.");

            return sb.ToString();
        }

        // ???????????????????????????????????????
        //  Phase 1: Harmony patch scanning
        // ???????????????????????????????????????

        private static void ScanHarmonyPatches(AuditResult result)
        {
            foreach (var targetType in _auditTargetTypes)
            {
                if (targetType == null) continue;

                try
                {
                    var methods = targetType.GetMethods(
                        BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.DeclaredOnly);

                    foreach (var method in methods)
                    {
                        if (_uiRelevantMethods.Count > 0 && !_uiRelevantMethods.Contains(method.Name))
                            continue;

                        try
                        {
                            var patchInfo = Harmony.GetPatchInfo(method);
                            if (patchInfo == null) continue;

                            var allPatches = new List<Patch>();
                            if (patchInfo.Prefixes != null) allPatches.AddRange(patchInfo.Prefixes);
                            if (patchInfo.Postfixes != null) allPatches.AddRange(patchInfo.Postfixes);
                            if (patchInfo.Transpilers != null) allPatches.AddRange(patchInfo.Transpilers);
                            if (patchInfo.Finalizers != null) allPatches.AddRange(patchInfo.Finalizers);

                            if (allPatches.Count == 0) continue;

                            var modPatches = allPatches.Where(p => !IsOurPatch(p)).ToList();
                            if (modPatches.Count == 0) continue;

                            // Build method signature once
                            string methodSig = FormatMethodSignature(method);
                            string returnType = FormatTypeName(method.ReturnType);
                            var paramInfos = method.GetParameters();
                            string[] paramNames = new string[paramInfos.Length];
                            for (int p = 0; p < paramInfos.Length; p++)
                                paramNames[p] = $"{FormatTypeName(paramInfos[p].ParameterType)} {paramInfos[p].Name}";

                            foreach (var patch in modPatches)
                            {
                                string patchType = GetPatchType(patch, patchInfo);
                                string ownerMod = ResolveModName(patch);

                                if (!string.IsNullOrEmpty(ownerMod))
                                    result.ModAssemblyNames.Add(ownerMod);

                                string patchMethodSig = "";
                                string patchDeclType = "";
                                string patchMethodName = "";
                                if (patch.PatchMethod != null)
                                {
                                    patchMethodSig = FormatMethodSignature(patch.PatchMethod);
                                    patchDeclType = patch.PatchMethod.DeclaringType?.FullName ?? patch.PatchMethod.DeclaringType?.Name ?? "?";
                                    patchMethodName = patch.PatchMethod.Name;
                                }

                                result.Patches.Add(new PatchRecord
                                {
                                    TargetTypeName = targetType.FullName ?? targetType.Name,
                                    TargetMethodName = method.Name,
                                    TargetMethodSignature = methodSig,
                                    TargetMethodParameters = paramNames,
                                    TargetMethodReturnType = returnType,
                                    TargetMethodIsStatic = method.IsStatic,
                                    PatchType = patchType,
                                    HarmonyOwner = patch.owner ?? "?",
                                    ModAssemblyName = ownerMod ?? "?",
                                    PatchDeclaringType = patchDeclType,
                                    PatchMethodName = patchMethodName,
                                    PatchMethodSignature = patchMethodSig,
                                    Priority = patch.priority,
                                    PatchMethodInfo = patch.PatchMethod
                                });
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[UIHarmonyPatchAuditor] Error scanning patches on {targetType.Name}: {ex.Message}");
                }
            }

            if (result.Patches.Count > 0)
                Debug.Log($"[UIHarmonyPatchAuditor] Found {result.Patches.Count} mod Harmony patch(es) on UI methods");
        }

        // ???????????????????????????????????????
        //  Phase 2: Runtime component scanning
        // ???????????????????????????????????????

        private static void ScanRuntimeComponents(GameObject root, AuditResult result)
        {
            var visited = new HashSet<string>();
            ScanComponentsRecursive(root.transform, result, visited, "");

            if (result.Components.Count > 0)
                Debug.Log($"[UIHarmonyPatchAuditor] Found {result.Components.Count} mod component type(s) on captured UI hierarchy");
        }

        private static void ScanComponentsRecursive(Transform current, AuditResult result,
            HashSet<string> visited, string path)
        {
            if (current == null) return;

            string currentPath = string.IsNullOrEmpty(path) ? current.name : path + "/" + current.name;

            var behaviours = current.GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] == null) continue;

                Type type = behaviours[i].GetType();
                string fullTypeName = type.FullName ?? type.Name;

                string ns = type.Namespace ?? "";
                if (ns.StartsWith("UnityEngine", StringComparison.Ordinal)) continue;
                if (ns.StartsWith("TMPro", StringComparison.Ordinal)) continue;
                if (ns.StartsWith("VerdantsAscent", StringComparison.Ordinal)) continue;

                string asmName = "";
                try { asmName = type.Assembly.GetName().Name ?? ""; }
                catch { }

                if (IsSkippedAssembly(asmName)) continue;
                if (asmName.IndexOf("valheim", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (asmName.IndexOf("assembly_", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                if (!string.IsNullOrEmpty(asmName))
                    result.ModAssemblyNames.Add(asmName);

                string typeKey = fullTypeName + "@" + asmName;
                if (visited.Contains(typeKey)) continue;
                visited.Add(typeKey);

                result.Components.Add(new ComponentRecord
                {
                    FullTypeName = fullTypeName,
                    AssemblyName = asmName,
                    GameObjectPath = currentPath,
                    Enabled = behaviours[i].enabled
                });
            }

            for (int c = 0; c < current.childCount; c++)
                ScanComponentsRecursive(current.GetChild(c), result, visited, currentPath);
        }

        // ???????????????????????????????????????
        //  Phase 3: BepInEx plugin metadata
        // ???????????????????????????????????????

        private static void ScanBepInExPlugins(AuditResult result)
        {
            if (result.ModAssemblyNames.Count == 0) return;

            try
            {
                var pluginType = typeof(BaseUnityPlugin);
                var allPlugins = UnityEngine.Object.FindObjectsByType(pluginType, UnityEngine.FindObjectsSortMode.None);

                foreach (var pluginObj in allPlugins)
                {
                    var plugin = pluginObj as BaseUnityPlugin;
                    if (plugin == null) continue;

                    try
                    {
                        string pluginAsm = plugin.GetType().Assembly.GetName().Name;
                        if (!result.ModAssemblyNames.Contains(pluginAsm)) continue;

                        var attr = plugin.GetType().GetCustomAttribute<BepInPlugin>();
                        if (attr == null) continue;

                        result.Plugins.Add(new PluginRecord
                        {
                            Name = attr.Name,
                            Version = attr.Version.ToString(),
                            GUID = attr.GUID,
                            AssemblyName = pluginAsm
                        });
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UIHarmonyPatchAuditor] Error scanning BepInEx plugins: {ex.Message}");
            }
        }

        // ???????????????????????????????????????
        //  Metadata storage (compact, for programmatic use)
        // ???????????????????????????????????????

        private static void StoreMetadata(UILayoutDefinition layout, AuditResult result)
        {
            // harmony_patches � pipe-delimited for backwards compat
            if (result.Patches.Count > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < result.Patches.Count; i++)
                {
                    var p = result.Patches[i];
                    if (sb.Length > 0) sb.Append("|");
                    sb.Append($"method:{p.TargetTypeName}.{p.TargetMethodName}");
                    sb.Append($",patch:{p.PatchType}");
                    sb.Append($",owner:{p.HarmonyOwner}");
                    sb.Append($",mod:{p.ModAssemblyName}");
                    if (!string.IsNullOrEmpty(p.PatchDeclaringType))
                        sb.Append($",patchMethod:{p.PatchDeclaringType}.{p.PatchMethodName}");
                    sb.Append($",priority:{p.Priority}");
                }
                layout.SetMeta("harmony_patches", sb.ToString());
            }

            // mod_components � pipe-delimited
            if (result.Components.Count > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < result.Components.Count; i++)
                {
                    var c = result.Components[i];
                    if (sb.Length > 0) sb.Append("|");
                    sb.Append($"type:{c.FullTypeName}");
                    sb.Append($",asm:{c.AssemblyName}");
                    sb.Append($",go:{c.GameObjectPath}");
                    sb.Append($",enabled:{c.Enabled}");
                }
                layout.SetMeta("mod_components", sb.ToString());
            }

            // dependency_mods
            if (result.ModAssemblyNames.Count > 0)
            {
                string[] modsArray = new string[result.ModAssemblyNames.Count];
                result.ModAssemblyNames.CopyTo(modsArray);
                Array.Sort(modsArray, StringComparer.OrdinalIgnoreCase);
                layout.SetMeta("dependency_mods", string.Join(",", modsArray));
                Debug.Log($"[UIHarmonyPatchAuditor] Detected {modsArray.Length} mod dependency/ies: {string.Join(", ", modsArray)}");
            }

            // dependency_plugins
            if (result.Plugins.Count > 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < result.Plugins.Count; i++)
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append($"  {result.Plugins[i].Name} v{result.Plugins[i].Version} ({result.Plugins[i].GUID})");
                }
                layout.SetMeta("dependency_plugins", sb.ToString());
            }
        }

        // ???????????????????????????????????????
        //  Summary index builder
        // ???????????????????????????????????????

        private static string BuildSummaryIndex(UILayoutDefinition layout, AuditResult result, string safeUID, string captureTime)
        {
            var sb = new StringBuilder(4096);
            string uid = layout.UID ?? "unknown";
            string displayName = layout.DisplayName ?? uid;

            sb.AppendLine($"# UI Audit Report: {displayName}");
            sb.AppendLine();
            sb.AppendLine($"- **Layout UID:** `{uid}`");
            sb.AppendLine($"- **Captured:** {captureTime}");
            string target = layout.GetMeta("VanillaOverrideTarget");
            if (!string.IsNullOrEmpty(target))
                sb.AppendLine($"- **Vanilla Target:** `{target}`");
            sb.AppendLine($"- **Mod Dependencies:** {result.ModAssemblyNames.Count}");
            sb.AppendLine($"- **Harmony Patches:** {result.Patches.Count}");
            sb.AppendLine($"- **Mod Components:** {result.Components.Count}");
            sb.AppendLine();
            sb.AppendLine("Per-mod detailed reports (with full IL disassembly and code stubs) are in the");
            sb.AppendLine($"`{safeUID}/` subdirectory. Each file below contains everything that mod does to the UI.");
            sb.AppendLine();

            // Plugin table
            if (result.Plugins.Count > 0)
            {
                sb.AppendLine("## Detected Plugins");
                sb.AppendLine();
                sb.AppendLine("| Plugin | Version | GUID | Assembly | Report |");
                sb.AppendLine("|--------|---------|------|----------|--------|");
                for (int i = 0; i < result.Plugins.Count; i++)
                {
                    var p = result.Plugins[i];
                    string safeMod = SanitizeFileName(p.AssemblyName);
                    sb.AppendLine($"| {Escape(p.Name)} | {Escape(p.Version)} | `{Escape(p.GUID)}` | `{Escape(p.AssemblyName)}` | [{safeMod}.md]({safeUID}/{safeMod}.md) |");
                }
                sb.AppendLine();
            }

            // Mod file listing
            var allMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < result.Patches.Count; i++)
                allMods.Add(result.Patches[i].ModAssemblyName ?? "Unknown");
            for (int i = 0; i < result.Components.Count; i++)
                allMods.Add(result.Components[i].AssemblyName);

            if (allMods.Count > 0)
            {
                sb.AppendLine("## Per-Mod Reports");
                sb.AppendLine();

                // Count patches/components per mod
                var patchCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var compCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < result.Patches.Count; i++)
                {
                    string m = result.Patches[i].ModAssemblyName ?? "Unknown";
                    patchCounts[m] = patchCounts.TryGetValue(m, out int c) ? c + 1 : 1;
                }
                for (int i = 0; i < result.Components.Count; i++)
                {
                    string m = result.Components[i].AssemblyName;
                    compCounts[m] = compCounts.TryGetValue(m, out int c) ? c + 1 : 1;
                }

                sb.AppendLine("| Mod | Patches | Components | Report File |");
                sb.AppendLine("|-----|---------|------------|-------------|");
                foreach (string mod in allMods.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
                {
                    string safeMod = SanitizeFileName(mod);
                    int pCount = patchCounts.TryGetValue(mod, out int pc) ? pc : 0;
                    int cCount = compCounts.TryGetValue(mod, out int cc) ? cc : 0;
                    sb.AppendLine($"| `{Escape(mod)}` | {pCount} | {cCount} | [{safeMod}.md]({safeUID}/{safeMod}.md) |");
                }
                sb.AppendLine();
            }

            // Missing deps
            var missing = GetMissingDependencies(layout);
            if (missing.Count > 0)
            {
                sb.AppendLine("## ? Missing Dependencies");
                sb.AppendLine();
                for (int i = 0; i < missing.Count; i++)
                    sb.AppendLine($"- **{missing[i]}** � NOT LOADED");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"*Generated by UIHarmonyPatchAuditor � {captureTime}*");
            return sb.ToString();
        }

        // ???????????????????????????????????????
        //  Per-mod report builder
        // ???????????????????????????????????????

        private static string BuildPerModReport(UILayoutDefinition layout, string modName,
            PluginRecord plugin, List<PatchRecord> patches, List<ComponentRecord> components, string captureTime)
        {
            var sb = new StringBuilder(16384);
            string uid = layout.UID ?? "unknown";

            sb.AppendLine($"# Mod Audit: {modName}");
            sb.AppendLine();
            sb.AppendLine($"- **Assembly:** `{modName}`");
            sb.AppendLine($"- **Layout:** `{uid}`");
            sb.AppendLine($"- **Captured:** {captureTime}");
            if (plugin != null)
            {
                sb.AppendLine($"- **Plugin Name:** {plugin.Name}");
                sb.AppendLine($"- **Version:** {plugin.Version}");
                sb.AppendLine($"- **GUID:** `{plugin.GUID}`");
            }
            int pCount = patches?.Count ?? 0;
            int cCount = components?.Count ?? 0;
            sb.AppendLine($"- **Harmony Patches:** {pCount}");
            sb.AppendLine($"- **Runtime Components:** {cCount}");
            sb.AppendLine();

            // ?? Harmony Patches ??
            if (patches != null && patches.Count > 0)
            {
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine("## Harmony Patches");
                sb.AppendLine();
                sb.AppendLine("Each entry includes the target method signature, the original patch method signature,");
                sb.AppendLine("a copy-pasteable Harmony stub, the full IL disassembly of the patch method body,");
                sb.AppendLine("and a list of all fields/methods/types the patch references.");
                sb.AppendLine();

                // Summary table
                sb.AppendLine("| # | Target Method | Type | Priority | Patch Method |");
                sb.AppendLine("|---|---------------|------|----------|-------------|");
                for (int i = 0; i < patches.Count; i++)
                {
                    var p = patches[i];
                    string shortTarget = $"{ShortTypeName(p.TargetTypeName)}.{p.TargetMethodName}";
                    string pm = !string.IsNullOrEmpty(p.PatchDeclaringType)
                        ? $"`{ShortTypeName(p.PatchDeclaringType)}.{p.PatchMethodName}`"
                        : "�";
                    sb.AppendLine($"| {i + 1} | `{shortTarget}` | {p.PatchType} | {p.Priority} | {pm} |");
                }
                sb.AppendLine();

                // Detailed per-patch entries
                for (int i = 0; i < patches.Count; i++)
                {
                    var p = patches[i];
                    string shortTarget = ShortTypeName(p.TargetTypeName);

                    sb.AppendLine($"### {i + 1}. `{shortTarget}.{p.TargetMethodName}` � {p.PatchType}");
                    sb.AppendLine();

                    sb.AppendLine("**Target method:**");
                    sb.AppendLine("```csharp");
                    sb.AppendLine(p.TargetMethodSignature);
                    sb.AppendLine("```");
                    sb.AppendLine();

                    if (!string.IsNullOrEmpty(p.PatchMethodSignature))
                    {
                        sb.AppendLine("**Patch method:**");
                        sb.AppendLine("```csharp");
                        sb.AppendLine($"// {p.PatchDeclaringType} (in {p.ModAssemblyName}.dll)");
                        sb.AppendLine(p.PatchMethodSignature);
                        sb.AppendLine("```");
                        sb.AppendLine();
                    }

                    sb.AppendLine($"**Harmony ID:** `{p.HarmonyOwner}` | **Priority:** {p.Priority}");
                    sb.AppendLine();

                    // Code stub
                    sb.AppendLine("<details><summary><b>Recreatable Harmony stub</b> (click to expand)</summary>");
                    sb.AppendLine();
                    sb.AppendLine("```csharp");
                    WriteCodeStub(sb, p);
                    sb.AppendLine("```");
                    sb.AppendLine("</details>");
                    sb.AppendLine();

                    // IL disassembly + referenced members
                    if (p.PatchMethodInfo != null)
                    {
                        string ilDis = DisassembleMethod(p.PatchMethodInfo);
                        var refs = ExtractReferencedMembers(p.PatchMethodInfo);

                        if (refs.Count > 0)
                        {
                            sb.AppendLine("**Referenced members** � fields, methods, and types this patch touches:");
                            sb.AppendLine();
                            for (int r = 0; r < refs.Count; r++)
                                sb.AppendLine($"- `{refs[r]}`");
                            sb.AppendLine();
                        }

                        if (!string.IsNullOrEmpty(ilDis))
                        {
                            sb.AppendLine("<details><summary><b>IL Disassembly</b> (click to expand)</summary>");
                            sb.AppendLine();
                            sb.AppendLine("```il");
                            sb.AppendLine(ilDis);
                            sb.AppendLine("```");
                            sb.AppendLine("</details>");
                            sb.AppendLine();
                        }
                    }
                }
            }

            // ?? Runtime Components ??
            if (components != null && components.Count > 0)
            {
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine("## Runtime MonoBehaviours");
                sb.AppendLine();
                sb.AppendLine("Components from this mod found on the captured UI hierarchy.");
                sb.AppendLine();
                sb.AppendLine("| Type | GameObject Path | Enabled |");
                sb.AppendLine("|------|----------------|---------|");
                for (int i = 0; i < components.Count; i++)
                {
                    var c = components[i];
                    sb.AppendLine($"| `{Escape(c.FullTypeName)}` | `{Escape(c.GameObjectPath)}` | {(c.Enabled ? "?" : "?")} |");
                }
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"*Generated by UIHarmonyPatchAuditor � {captureTime}*");
            return sb.ToString();
        }

        // ???????????????????????????????????????
        //  IL Disassembly Engine
        // ???????????????????????????????????????

        /// <summary>
        /// Disassembles a method body into human-readable IL text.
        /// Resolves metadata tokens to produce lines like:
        ///   IL_0000: ldarg.0
        ///   IL_0001: callvirt instance RectTransform UnityEngine.Component::get_transform()
        ///   IL_0006: ldfld Vector2 RectTransform::m_AnchoredPosition
        /// </summary>
        private static string DisassembleMethod(MethodInfo method)
        {
            if (method == null) return null;

            try
            {
                var body = method.GetMethodBody();
                if (body == null) return "// Method has no IL body (extern or abstract)";

                byte[] il = body.GetILAsByteArray();
                if (il == null || il.Length == 0) return "// Empty IL body";

                var module = method.Module;
                var sb = new StringBuilder(il.Length * 4);

                // Write locals
                var locals = body.LocalVariables;
                if (locals != null && locals.Count > 0)
                {
                    sb.Append(".locals (");
                    for (int i = 0; i < locals.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append($"[{i}] {FormatTypeName(locals[i].LocalType)}");
                    }
                    sb.AppendLine(")");
                    sb.AppendLine();
                }

                int pos = 0;
                while (pos < il.Length)
                {
                    int instrStart = pos;
                    sb.Append($"IL_{instrStart:X4}: ");

                    // Read opcode
                    OpCode opcode;
                    if (il[pos] == 0xFE)
                    {
                        pos++;
                        if (pos >= il.Length) break;
                        opcode = TwoByteOpCodes[il[pos]];
                    }
                    else
                    {
                        opcode = OneByteOpCodes[il[pos]];
                    }
                    pos++;

                    sb.Append(opcode.Name);

                    // Read operand based on operand type
                    switch (opcode.OperandType)
                    {
                        case OperandType.InlineNone:
                            break;

                        case OperandType.ShortInlineBrTarget:
                            if (pos < il.Length)
                            {
                                int offset = (sbyte)il[pos]; pos++;
                                sb.Append($" IL_{(pos + offset):X4}");
                            }
                            break;

                        case OperandType.InlineBrTarget:
                            if (pos + 3 < il.Length)
                            {
                                int offset = BitConverter.ToInt32(il, pos); pos += 4;
                                sb.Append($" IL_{(pos + offset):X4}");
                            }
                            else pos = il.Length;
                            break;

                        case OperandType.ShortInlineI:
                            if (pos < il.Length)
                            {
                                sb.Append($" {(sbyte)il[pos]}");
                                pos++;
                            }
                            break;

                        case OperandType.InlineI:
                            if (pos + 3 < il.Length)
                            {
                                sb.Append($" {BitConverter.ToInt32(il, pos)}");
                                pos += 4;
                            }
                            else pos = il.Length;
                            break;

                        case OperandType.InlineI8:
                            if (pos + 7 < il.Length)
                            {
                                sb.Append($" {BitConverter.ToInt64(il, pos)}");
                                pos += 8;
                            }
                            else pos = il.Length;
                            break;

                        case OperandType.ShortInlineR:
                            if (pos + 3 < il.Length)
                            {
                                sb.Append($" {BitConverter.ToSingle(il, pos):G}");
                                pos += 4;
                            }
                            else pos = il.Length;
                            break;

                        case OperandType.InlineR:
                            if (pos + 7 < il.Length)
                            {
                                sb.Append($" {BitConverter.ToDouble(il, pos):G}");
                                pos += 8;
                            }
                            else pos = il.Length;
                            break;

                        case OperandType.InlineString:
                            if (pos + 3 < il.Length)
                            {
                                int token = BitConverter.ToInt32(il, pos); pos += 4;
                                try
                                {
                                    string str = module.ResolveString(token);
                                    sb.Append($" \"{EscapeILString(str)}\"");
                                }
                                catch { sb.Append($" <string token 0x{token:X8}>"); }
                            }
                            else pos = il.Length;
                            break;

                        case OperandType.InlineMethod:
                            if (pos + 3 < il.Length)
                            {
                                int token = BitConverter.ToInt32(il, pos); pos += 4;
                                sb.Append(" ");
                                sb.Append(ResolveMethodToken(module, token, method));
                            }
                            else pos = il.Length;
                            break;

                        case OperandType.InlineField:
                            if (pos + 3 < il.Length)
                            {
                                int token = BitConverter.ToInt32(il, pos); pos += 4;
                                sb.Append(" ");
                                sb.Append(ResolveFieldToken(module, token, method));
                            }
                            else pos = il.Length;
                            break;

                        case OperandType.InlineType:
                            if (pos + 3 < il.Length)
                            {
                                int token = BitConverter.ToInt32(il, pos); pos += 4;
                                sb.Append(" ");
                                sb.Append(ResolveTypeToken(module, token, method));
                            }
                            else pos = il.Length;
                            break;

                        case OperandType.InlineTok:
                            if (pos + 3 < il.Length)
                            {
                                int token = BitConverter.ToInt32(il, pos); pos += 4;
                                sb.Append(" ");
                                sb.Append(ResolveMemberToken(module, token, method));
                            }
                            else pos = il.Length;
                            break;

                        case OperandType.InlineVar:
                            if (pos + 1 < il.Length)
                            {
                                sb.Append($" V_{BitConverter.ToInt16(il, pos)}");
                                pos += 2;
                            }
                            else pos = il.Length;
                            break;

                        case OperandType.ShortInlineVar:
                            if (pos < il.Length)
                            {
                                sb.Append($" V_{il[pos]}");
                                pos++;
                            }
                            break;

                        case OperandType.InlineSig:
                            if (pos + 3 < il.Length)
                            {
                                int token = BitConverter.ToInt32(il, pos); pos += 4;
                                sb.Append($" <sig 0x{token:X8}>");
                            }
                            else pos = il.Length;
                            break;

                        case OperandType.InlineSwitch:
                            if (pos + 3 < il.Length)
                            {
                                int count = BitConverter.ToInt32(il, pos); pos += 4;
                                int baseOffset = pos + count * 4;
                                sb.Append($" ({count} targets)");
                                for (int j = 0; j < count && pos + 3 < il.Length; j++)
                                {
                                    int target2 = BitConverter.ToInt32(il, pos); pos += 4;
                                    // Don't print all targets to keep output manageable
                                }
                            }
                            else pos = il.Length;
                            break;

                        default:
                            // Unknown operand type � skip 4 bytes as best guess
                            if (pos + 3 < il.Length) pos += 4;
                            else pos = il.Length;
                            break;
                    }

                    sb.AppendLine();
                }

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"// IL disassembly failed: {ex.Message}";
            }
        }

        /// <summary>
        /// Extracts all unique field accesses, method calls, and type references from IL.
        /// Returns human-readable strings like "InventoryGui.m_craftRecipe (field)",
        /// "RectTransform.set_anchoredPosition (call)", etc.
        /// </summary>
        private static List<string> ExtractReferencedMembers(MethodInfo method)
        {
            var refs = new List<string>();
            if (method == null) return refs;

            try
            {
                var body = method.GetMethodBody();
                if (body == null) return refs;

                byte[] il = body.GetILAsByteArray();
                if (il == null || il.Length == 0) return refs;

                var module = method.Module;
                var seen = new HashSet<string>(StringComparer.Ordinal);

                int pos = 0;
                while (pos < il.Length)
                {
                    OpCode opcode;
                    if (il[pos] == 0xFE)
                    {
                        pos++;
                        if (pos >= il.Length) break;
                        opcode = TwoByteOpCodes[il[pos]];
                    }
                    else
                    {
                        opcode = OneByteOpCodes[il[pos]];
                    }
                    pos++;

                    switch (opcode.OperandType)
                    {
                        case OperandType.InlineMethod:
                            if (pos + 3 < il.Length)
                            {
                                int token = BitConverter.ToInt32(il, pos); pos += 4;
                                try
                                {
                                    var resolved = module.ResolveMethod(token, SafeGenericTypeArgs(method), SafeGenericMethodArgs(method));
                                    if (resolved != null)
                                    {
                                        string entry = $"{FormatTypeName(resolved.DeclaringType)}.{resolved.Name}() � call";
                                        if (seen.Add(entry)) refs.Add(entry);
                                    }
                                }
                                catch { }
                            }
                            else pos = il.Length;
                            break;

                        case OperandType.InlineField:
                            if (pos + 3 < il.Length)
                            {
                                int token = BitConverter.ToInt32(il, pos); pos += 4;
                                try
                                {
                                    var resolved = module.ResolveField(token, SafeGenericTypeArgs(method), SafeGenericMethodArgs(method));
                                    if (resolved != null)
                                    {
                                        string entry = $"{FormatTypeName(resolved.DeclaringType)}.{resolved.Name} � {FormatTypeName(resolved.FieldType)} field";
                                        if (seen.Add(entry)) refs.Add(entry);
                                    }
                                }
                                catch { }
                            }
                            else pos = il.Length;
                            break;

                        case OperandType.InlineType:
                        case OperandType.InlineTok:
                            if (pos + 3 < il.Length)
                            {
                                int token = BitConverter.ToInt32(il, pos); pos += 4;
                                try
                                {
                                    var resolved = module.ResolveType(token, SafeGenericTypeArgs(method), SafeGenericMethodArgs(method));
                                    if (resolved != null)
                                    {
                                        string entry = $"{FormatTypeName(resolved)} � type ref";
                                        if (seen.Add(entry)) refs.Add(entry);
                                    }
                                }
                                catch { /* May be a method/field token for InlineTok, ignore */ }
                            }
                            else pos = il.Length;
                            break;

                        case OperandType.InlineNone:
                            break;
                        case OperandType.ShortInlineBrTarget:
                        case OperandType.ShortInlineI:
                        case OperandType.ShortInlineVar:
                            if (pos < il.Length) pos++;
                            break;
                        case OperandType.InlineBrTarget:
                        case OperandType.InlineI:
                        case OperandType.ShortInlineR:
                        case OperandType.InlineString:
                        case OperandType.InlineSig:
                            if (pos + 3 < il.Length) pos += 4;
                            else pos = il.Length;
                            break;
                        case OperandType.InlineVar:
                            if (pos + 1 < il.Length) pos += 2;
                            else pos = il.Length;
                            break;
                        case OperandType.InlineI8:
                        case OperandType.InlineR:
                            if (pos + 7 < il.Length) pos += 8;
                            else pos = il.Length;
                            break;
                        case OperandType.InlineSwitch:
                            if (pos + 3 < il.Length)
                            {
                                int count = BitConverter.ToInt32(il, pos); pos += 4;
                                pos += count * 4;
                                if (pos > il.Length) pos = il.Length;
                            }
                            else pos = il.Length;
                            break;
                        default:
                            if (pos + 3 < il.Length) pos += 4;
                            else pos = il.Length;
                            break;
                    }
                }
            }
            catch { }

            return refs;
        }

        // ???????????????????????????????????????
        //  IL token resolution helpers
        // ???????????????????????????????????????

        private static Type[] SafeGenericTypeArgs(MethodInfo method)
        {
            try
            {
                if (method.DeclaringType != null && method.DeclaringType.IsGenericType)
                    return method.DeclaringType.GetGenericArguments();
            }
            catch { }
            return null;
        }

        private static Type[] SafeGenericMethodArgs(MethodInfo method)
        {
            try
            {
                if (method.IsGenericMethod)
                    return method.GetGenericArguments();
            }
            catch { }
            return null;
        }

        private static string ResolveMethodToken(Module module, int token, MethodInfo context)
        {
            try
            {
                var m = module.ResolveMethod(token, SafeGenericTypeArgs(context), SafeGenericMethodArgs(context));
                if (m != null)
                    return $"{FormatTypeName(m.DeclaringType)}::{m.Name}({FormatParamTypes(m.GetParameters())})";
            }
            catch { }
            return $"<method 0x{token:X8}>";
        }

        private static string ResolveFieldToken(Module module, int token, MethodInfo context)
        {
            try
            {
                var f = module.ResolveField(token, SafeGenericTypeArgs(context), SafeGenericMethodArgs(context));
                if (f != null)
                    return $"{FormatTypeName(f.FieldType)} {FormatTypeName(f.DeclaringType)}::{f.Name}";
            }
            catch { }
            return $"<field 0x{token:X8}>";
        }

        private static string ResolveTypeToken(Module module, int token, MethodInfo context)
        {
            try
            {
                var t = module.ResolveType(token, SafeGenericTypeArgs(context), SafeGenericMethodArgs(context));
                if (t != null)
                    return FormatTypeName(t);
            }
            catch { }
            return $"<type 0x{token:X8}>";
        }

        private static string ResolveMemberToken(Module module, int token, MethodInfo context)
        {
            try
            {
                var member = module.ResolveMember(token, SafeGenericTypeArgs(context), SafeGenericMethodArgs(context));
                if (member is FieldInfo fi)
                    return $"{FormatTypeName(fi.FieldType)} {FormatTypeName(fi.DeclaringType)}::{fi.Name}";
                if (member is MethodInfo mi)
                    return $"{FormatTypeName(mi.DeclaringType)}::{mi.Name}()";
                if (member is Type tp)
                    return FormatTypeName(tp);
                if (member != null)
                    return member.ToString();
            }
            catch { }
            return $"<token 0x{token:X8}>";
        }

        private static string FormatParamTypes(ParameterInfo[] parms)
        {
            if (parms == null || parms.Length == 0) return "";
            var parts = new string[parms.Length];
            for (int i = 0; i < parms.Length; i++)
                parts[i] = FormatTypeName(parms[i].ParameterType);
            return string.Join(", ", parts);
        }

        private static string EscapeILString(string s)
        {
            if (s == null) return "";
            if (s.Length > 80) s = s.Substring(0, 77) + "...";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        // ???????????????????????????????????????
        //  OpCode lookup tables
        // ???????????????????????????????????????

        private static readonly OpCode[] OneByteOpCodes = new OpCode[256];
        private static readonly OpCode[] TwoByteOpCodes = new OpCode[256];

        static UIHarmonyPatchAuditor()
        {
            // Initialize with nop as default
            for (int i = 0; i < 256; i++)
            {
                OneByteOpCodes[i] = OpCodes.Nop;
                TwoByteOpCodes[i] = OpCodes.Nop;
            }

            // Populate from all OpCode fields
            foreach (var fi in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (fi.FieldType != typeof(OpCode)) continue;
                var op = (OpCode)fi.GetValue(null);
                ushort val = (ushort)op.Value;
                if (val < 0x100)
                    OneByteOpCodes[val] = op;
                else if ((val & 0xFF00) == 0xFE00)
                    TwoByteOpCodes[val & 0xFF] = op;
            }
        }

        /// <summary>
        /// Builds a Markdown report from stored metadata (when we don't have a live AuditResult).
        /// Less detailed than a fresh audit but still useful for re-export.
        /// </summary>
        private static string BuildMarkdownFromMetadata(UILayoutDefinition layout)
        {
            var sb = new StringBuilder(4096);
            string uid = layout.UID ?? "unknown";
            string displayName = layout.DisplayName ?? uid;

            sb.AppendLine($"# UI Audit Report: {displayName}");
            sb.AppendLine();
            sb.AppendLine($"- **Layout UID:** `{uid}`");
            sb.AppendLine($"- **Re-exported from metadata** (less detail than a live capture)");
            string target = layout.GetMeta("VanillaOverrideTarget");
            if (!string.IsNullOrEmpty(target))
                sb.AppendLine($"- **Vanilla Target:** `{target}`");
            sb.AppendLine();

            // Plugins
            string plugins = layout.GetMeta("dependency_plugins");
            if (!string.IsNullOrEmpty(plugins))
            {
                sb.AppendLine("## Detected Plugins");
                sb.AppendLine();
                sb.AppendLine("```");
                sb.AppendLine(plugins);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            // Harmony patches from metadata
            string patchData = layout.GetMeta("harmony_patches");
            if (!string.IsNullOrEmpty(patchData))
            {
                sb.AppendLine("## Harmony Patches (from stored metadata)");
                sb.AppendLine();
                sb.AppendLine("| Target Method | Patch Type | Owner | Mod | Patch Method | Priority |");
                sb.AppendLine("|---------------|-----------|-------|-----|-------------|----------|");

                string[] records = patchData.Split('|');
                for (int i = 0; i < records.Length; i++)
                {
                    var fields = ParsePipeRecord(records[i]);
                    string method = GetField(fields, "method");
                    string patchType = GetField(fields, "patch");
                    string owner = GetField(fields, "owner");
                    string mod = GetField(fields, "mod");
                    string patchMethod = GetField(fields, "patchMethod");
                    string priority = GetField(fields, "priority");
                    sb.AppendLine($"| `{Escape(method)}` | {Escape(patchType)} | `{Escape(owner)}` | `{Escape(mod)}` | `{Escape(patchMethod)}` | {Escape(priority)} |");
                }
                sb.AppendLine();
            }

            // Mod components from metadata
            string compData = layout.GetMeta("mod_components");
            if (!string.IsNullOrEmpty(compData))
            {
                sb.AppendLine("## Mod MonoBehaviours (from stored metadata)");
                sb.AppendLine();
                sb.AppendLine("| Type | Assembly | GameObject Path | Enabled |");
                sb.AppendLine("|------|----------|----------------|---------|");

                string[] records = compData.Split('|');
                for (int i = 0; i < records.Length; i++)
                {
                    var fields = ParsePipeRecord(records[i]);
                    string type = GetField(fields, "type");
                    string asm = GetField(fields, "asm");
                    string go = GetField(fields, "go");
                    string enabled = GetField(fields, "enabled");
                    sb.AppendLine($"| `{Escape(type)}` | `{Escape(asm)}` | `{Escape(go)}` | {Escape(enabled)} |");
                }
                sb.AppendLine();
            }

            // Missing deps
            var missing = GetMissingDependencies(layout);
            if (missing.Count > 0)
            {
                sb.AppendLine("## ? Missing Dependencies");
                sb.AppendLine();
                for (int i = 0; i < missing.Count; i++)
                    sb.AppendLine($"- **{missing[i]}** � NOT LOADED");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"*Re-exported from layout metadata by UIHarmonyPatchAuditor � {DateTime.Now:yyyy-MM-dd HH:mm:ss}*");

            return sb.ToString();
        }

        // ???????????????????????????????????????
        //  Code stub generator
        // ???????????????????????????????????????

        private static void WriteCodeStub(StringBuilder sb, PatchRecord p)
        {
            string shortTarget = ShortTypeName(p.TargetTypeName);
            string harmonyId = p.HarmonyOwner != "?" ? p.HarmonyOwner : "com.yourmod.id";
            string attrType = p.PatchType;

            sb.AppendLine($"[HarmonyPatch(typeof({shortTarget}), nameof({shortTarget}.{p.TargetMethodName}))]");
            sb.AppendLine($"public static class {shortTarget}_{p.TargetMethodName}_{attrType}");
            sb.AppendLine("{");

            // Build parameter list for the stub
            var stubParams = new List<string>();

            // For instance methods, add __instance
            if (!p.TargetMethodIsStatic)
                stubParams.Add($"{shortTarget} __instance");

            // For Postfix with non-void return, add __result
            if (attrType == "Postfix" && p.TargetMethodReturnType != "void")
                stubParams.Add($"{p.TargetMethodReturnType} __result");

            // For Prefix, add __result as ref if non-void (to allow skipping)
            if (attrType == "Prefix" && p.TargetMethodReturnType != "void")
                stubParams.Add($"ref {p.TargetMethodReturnType} __result");

            // Add original method parameters
            if (p.TargetMethodParameters != null)
            {
                for (int j = 0; j < p.TargetMethodParameters.Length; j++)
                    stubParams.Add(p.TargetMethodParameters[j]);
            }

            string paramList = string.Join(", ", stubParams.ToArray());
            string returnType = attrType == "Prefix" ? "bool" : "void";

            sb.AppendLine($"    [Harmony{attrType}]");
            sb.AppendLine($"    public static {returnType} {attrType}({paramList})");
            sb.AppendLine("    {");

            if (attrType == "Prefix")
            {
                sb.AppendLine($"        // Return false to skip the original method, true to let it run.");
                sb.AppendLine($"        // Original mod: {p.PatchDeclaringType}.{p.PatchMethodName} (priority {p.Priority})");
                sb.AppendLine($"        // TODO: Implement your replacement logic here");
                sb.AppendLine("        return true;");
            }
            else if (attrType == "Postfix")
            {
                sb.AppendLine($"        // Runs after {shortTarget}.{p.TargetMethodName}() completes.");
                sb.AppendLine($"        // Original mod: {p.PatchDeclaringType}.{p.PatchMethodName} (priority {p.Priority})");
                sb.AppendLine($"        // TODO: Implement your post-processing logic here");
            }
            else if (attrType == "Transpiler")
            {
                sb.AppendLine($"        // Transpilers modify IL instructions. This is an advanced technique.");
                sb.AppendLine($"        // Original mod: {p.PatchDeclaringType}.{p.PatchMethodName} (priority {p.Priority})");
                sb.AppendLine($"        // You'll need to use System.Reflection.Emit and HarmonyLib.CodeInstruction.");
                sb.AppendLine($"        // See: https://harmony.pardeike.net/articles/patching-transpiler.html");
            }
            else if (attrType == "Finalizer")
            {
                sb.AppendLine($"        // Finalizers run after the method even if it threw an exception.");
                sb.AppendLine($"        // Original mod: {p.PatchDeclaringType}.{p.PatchMethodName} (priority {p.Priority})");
                sb.AppendLine($"        // TODO: Implement your cleanup/error-handling logic here");
            }

            sb.AppendLine("    }");
            sb.AppendLine("}");
        }

        // ???????????????????????????????????????
        //  Reflection helpers
        // ???????????????????????????????????????

        private static string FormatMethodSignature(MethodInfo method)
        {
            if (method == null) return "?";

            try
            {
                var sb = new StringBuilder();
                string access = method.IsPublic ? "public" : method.IsPrivate ? "private" : "protected";
                if (method.IsStatic) access += " static";

                sb.Append(access);
                sb.Append(" ");
                sb.Append(FormatTypeName(method.ReturnType));
                sb.Append(" ");
                sb.Append(method.Name);
                sb.Append("(");

                var parms = method.GetParameters();
                for (int i = 0; i < parms.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    if (parms[i].IsOut)
                        sb.Append("out ");
                    else if (parms[i].ParameterType.IsByRef)
                        sb.Append("ref ");
                    sb.Append(FormatTypeName(parms[i].ParameterType));
                    sb.Append(" ");
                    sb.Append(parms[i].Name ?? $"arg{i}");
                }

                sb.Append(")");
                return sb.ToString();
            }
            catch
            {
                return $"{method.DeclaringType?.Name}.{method.Name}(?)";
            }
        }

        private static string FormatTypeName(Type type)
        {
            if (type == null) return "void";
            if (type == typeof(void)) return "void";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(int)) return "int";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(string)) return "string";
            if (type == typeof(long)) return "long";
            if (type == typeof(byte)) return "byte";
            if (type == typeof(char)) return "char";
            if (type == typeof(object)) return "object";
            if (type.IsByRef) return FormatTypeName(type.GetElementType());
            if (type.IsGenericType)
            {
                string baseName = type.Name;
                int tick = baseName.IndexOf('`');
                if (tick > 0) baseName = baseName.Substring(0, tick);
                var args = type.GetGenericArguments();
                string[] argNames = new string[args.Length];
                for (int i = 0; i < args.Length; i++)
                    argNames[i] = FormatTypeName(args[i]);
                return $"{baseName}<{string.Join(", ", argNames)}>";
            }
            if (type.IsArray)
                return FormatTypeName(type.GetElementType()) + "[]";
            return type.Name;
        }

        private static string ShortTypeName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return "?";
            int lastDot = fullName.LastIndexOf('.');
            if (lastDot >= 0 && lastDot < fullName.Length - 1)
                return fullName.Substring(lastDot + 1);
            return fullName;
        }

        // ???????????????????????????????????????
        //  Patch / metadata helpers
        // ???????????????????????????????????????

        private static bool IsOurPatch(Patch patch)
        {
            if (patch.owner == OurHarmonyId) return true;
            if (patch.PatchMethod != null)
            {
                string ns = patch.PatchMethod.DeclaringType?.Namespace ?? "";
                if (ns.StartsWith("VerdantsAscent", StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static string GetPatchType(Patch patch, HarmonyLib.Patches allPatches)
        {
            if (allPatches.Prefixes != null && allPatches.Prefixes.Contains(patch)) return "Prefix";
            if (allPatches.Postfixes != null && allPatches.Postfixes.Contains(patch)) return "Postfix";
            if (allPatches.Transpilers != null && allPatches.Transpilers.Contains(patch)) return "Transpiler";
            if (allPatches.Finalizers != null && allPatches.Finalizers.Contains(patch)) return "Finalizer";
            return "Unknown";
        }

        private static string ResolveModName(Patch patch)
        {
            if (patch.PatchMethod != null)
            {
                try
                {
                    string asmName = patch.PatchMethod.DeclaringType?.Assembly?.GetName()?.Name;
                    if (!string.IsNullOrEmpty(asmName) && !IsSkippedAssembly(asmName))
                        return asmName;
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(patch.owner) && patch.owner != OurHarmonyId)
            {
                string owner = patch.owner;
                int lastDot = owner.LastIndexOf('.');
                if (lastDot >= 0 && lastDot < owner.Length - 1)
                    return owner.Substring(lastDot + 1);
                return owner;
            }

            return null;
        }

        private static bool IsSkippedAssembly(string asmName)
        {
            if (string.IsNullOrEmpty(asmName)) return true;
            if (_skipAssemblies.Contains(asmName)) return true;
            if (asmName.StartsWith("UnityEngine.", StringComparison.Ordinal)) return true;
            if (asmName.StartsWith("Unity.", StringComparison.Ordinal)) return true;
            if (asmName.StartsWith("System.", StringComparison.Ordinal)) return true;
            if (asmName.StartsWith("Mono.", StringComparison.Ordinal)) return true;
            if (asmName.StartsWith("BepInEx.", StringComparison.Ordinal)) return true;
            return false;
        }

        // ???????????????????????????????????????
        //  String utilities
        // ???????????????????????????????????????

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("|", "\\|").Replace("\n", " ").Replace("\r", "");
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unknown";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        /// <summary>
        /// Parses a single pipe-record like "method:Hud.UpdateHealth,patch:Postfix,owner:..." into key-value pairs.
        /// </summary>
        private static Dictionary<string, string> ParsePipeRecord(string record)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(record)) return dict;

            string[] parts = record.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                int colon = parts[i].IndexOf(':');
                if (colon > 0 && colon < parts[i].Length - 1)
                {
                    string key = parts[i].Substring(0, colon).Trim();
                    string val = parts[i].Substring(colon + 1).Trim();
                    dict[key] = val;
                }
            }
            return dict;
        }

        private static string GetField(Dictionary<string, string> fields, string key)
        {
            if (fields.TryGetValue(key, out string val)) return val;
            return "";
        }
    }
}
