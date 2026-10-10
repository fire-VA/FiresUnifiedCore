using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Extracts UI layout JSON embedded in the assembly into the UILayouts config folder on first run, so authored
    /// layouts ship with the mod. A resource named FiresRPGmaker.BundledLayouts.custom.screen.json extracts to
    /// UILayouts/custom/screen.json: the prefix is dropped and the remaining dots become folders.
    /// </summary>
    internal static class BundledLayoutExtractor
    {
        private const string BundledMarker = ".BundledLayouts.";

        /// <summary>
        /// Writes any missing bundled layouts to disk. Existing files are
        /// never overwritten so users keep their edits across mod updates.
        /// 0.2.330: every mod's layouts, not only the last mod to set UIBuilderHost (FiresCompanions loads after
        /// FiresRPGmaker and replaced its prefix, so RPGmaker's never extracted): each (assembly, prefix) a mod has set,
        /// plus any loaded assembly that references Core and embeds "*.BundledLayouts.*" resources. Only .json is a
        /// layout: Companions' prefix "FiresCompanions." also matched its embedded asset bundle, which was copied here
        /// (19.6 MB "firescompanions"); such copies are removed.
        /// </summary>
        public static void ExtractIfMissing()
        {
            try
            {
                string baseDir = FiresCore.Storage.FiresConfigPaths.UiLayouts;
                if (!Directory.Exists(baseDir))
                    Directory.CreateDirectory(baseDir);

                int extracted = 0, skipped = 0;
                foreach (var source in Sources())
                {
                    Assembly asm = source.Key;
                    string prefix = source.Value;
                    string[] names;
                    try { names = asm.GetManifestResourceNames(); }
                    catch { continue; }

                    foreach (var resName in names)
                    {
                        if (!resName.StartsWith(prefix, StringComparison.Ordinal) || resName.Length == prefix.Length) continue;
                        try
                        {
                            string relative = ResourceNameToRelativePath(resName, prefix);
                            string fullPath = Path.Combine(baseDir, relative);

                            if (!resName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                            {
                                RemoveWrongCopy(asm, resName, fullPath);
                                continue;
                            }

                            if (File.Exists(fullPath))
                            {
                                skipped++;
                                continue;
                            }

                            string dir = Path.GetDirectoryName(fullPath);
                            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                                Directory.CreateDirectory(dir);

                            using (var stream = asm.GetManifestResourceStream(resName))
                            {
                                if (stream == null)
                                {
                                    Debug.LogWarning($"[BundledLayoutExtractor] GetManifestResourceStream returned null for '{resName}'");
                                    continue;
                                }
                                using (var output = File.Create(fullPath))
                                {
                                    stream.CopyTo(output);
                                }
                            }

                            extracted++;
                            Debug.Log($"[BundledLayoutExtractor] Extracted bundled layout: {relative}");
                        }
                        catch (Exception inner)
                        {
                            Debug.LogWarning($"[BundledLayoutExtractor] Failed to extract '{resName}': {inner.Message}");
                        }
                    }
                }

                if (extracted > 0 || skipped > 0)
                    Debug.Log($"[BundledLayoutExtractor] Done. Extracted {extracted}, skipped {skipped} (already present).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BundledLayoutExtractor] ExtractIfMissing failed: {ex.Message}");
            }
        }

        /// <summary>Every (assembly, layout prefix) to extract from: the ones mods set on UIBuilderHost, then loaded
        /// assemblies that reference Core and embed "X.BundledLayouts.*" resources (prefix "X.BundledLayouts.").</summary>
        private static List<KeyValuePair<Assembly, string>> Sources()
        {
            var list = UIBuilderHost.LayoutSources();
            string core = typeof(BundledLayoutExtractor).Assembly.GetName().Name;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (asm.IsDynamic || !asm.GetReferencedAssemblies().Any(r => r.Name == core)) continue;
                    foreach (string name in asm.GetManifestResourceNames())
                    {
                        int at = name.IndexOf(BundledMarker, StringComparison.Ordinal);
                        if (at <= 0) continue;
                        var source = new KeyValuePair<Assembly, string>(asm, name.Substring(0, at + BundledMarker.Length));
                        if (!list.Contains(source)) list.Add(source);
                    }
                }
                catch { }
            }
            return list;
        }

        /// <summary>A non-layout resource (an asset bundle, an icon) copied here by an earlier Core under a too-wide prefix:
        /// removed when the file there has that resource's path and exact length (a non-.json file under a resource-derived
        /// name nobody else writes), so a user's own layout is never touched.</summary>
        private static void RemoveWrongCopy(Assembly asm, string resName, string fullPath)
        {
            if (!File.Exists(fullPath)) return;
            try
            {
                long length;
                using (var stream = asm.GetManifestResourceStream(resName))
                    length = stream != null ? stream.Length : -1;
                if (length < 0 || new FileInfo(fullPath).Length != length) return;
                File.Delete(fullPath);
                FiresCore.Logging.FiresLogger.LogDebug($"[BundledLayoutExtractor] removed '{fullPath}': a copy of embedded resource '{resName}' ({length} B), not a layout");
            }
            catch (Exception ex)
            {
                FiresCore.Logging.FiresLogger.LogDebug($"[BundledLayoutExtractor] could not remove '{fullPath}': {ex.Message}");
            }
        }

        /// <summary>
        /// Converts a resource name like
        /// <c>FiresRPGmaker.BundledLayouts.custom.companionsequipmentscreen.json</c>
        /// into a relative path <c>custom\companionsequipmentscreen.json</c>.
        /// The final ".json" extension is preserved; all earlier dots become
        /// directory separators.
        /// </summary>
        private static string ResourceNameToRelativePath(string resName, string prefix)
        {
            string trimmed = resName.Substring(prefix.Length);

            // Pull the extension off so we can dot-split the rest safely.
            string ext = string.Empty;
            int lastDot = trimmed.LastIndexOf('.');
            if (lastDot >= 0)
            {
                ext = trimmed.Substring(lastDot); // includes the dot
                trimmed = trimmed.Substring(0, lastDot);
            }

            // Replace remaining dots with the platform's directory separator.
            string pathPart = trimmed.Replace('.', Path.DirectorySeparatorChar);
            return pathPart + ext;
        }
    }
}
