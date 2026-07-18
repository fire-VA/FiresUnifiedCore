using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// Extracts UI layout JSON files that ship inside the assembly as
    /// <see cref="EmbeddedResource"/>s into the on-disk UILayouts folder on
    /// first run. This lets us bundle authored layouts (e.g. the companion
    /// equipment screen) with the mod itself instead of relying on every
    /// installer remembering to drop the JSON into BepInEx config manually.
    ///
    /// Resources must be embedded with a logical name of the form
    /// <c>FiresRPGmaker.BundledLayouts.&lt;subpath&gt;.&lt;file&gt;.json</c>.
    /// The <c>BundledLayouts.</c> prefix is stripped and the remaining dotted
    /// segments are converted to directory separators, so
    /// <c>FiresRPGmaker.BundledLayouts.custom.companionsequipmentscreen.json</c>
    /// extracts to <c>&lt;config&gt;/UILayouts/custom/companionsequipmentscreen.json</c>.
    /// </summary>
    internal static class BundledLayoutExtractor
    {
        private static string ResourcePrefix => UIBuilderHost.LayoutResourcePrefix;

        /// <summary>
        /// Writes any missing bundled layouts to disk. Existing files are
        /// never overwritten so users keep their edits across mod updates.
        /// </summary>
        public static void ExtractIfMissing()
        {
            try
            {
                var asm = UIBuilderHost.ResolveLayoutAssembly();
                var resources = asm.GetManifestResourceNames()
                    .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                    .ToArray();

                if (resources.Length == 0) return;

                string baseDir = Path.Combine(
                    FiresCore.Storage.FiresConfigPaths.UiLayouts);
                if (!Directory.Exists(baseDir))
                    Directory.CreateDirectory(baseDir);

                int extracted = 0, skipped = 0;
                foreach (var resName in resources)
                {
                    try
                    {
                        string relative = ResourceNameToRelativePath(resName);
                        string fullPath = Path.Combine(baseDir, relative);

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
                            using (var fs = File.Create(fullPath))
                            {
                                stream.CopyTo(fs);
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

                if (extracted > 0 || skipped > 0)
                    Debug.Log($"[BundledLayoutExtractor] Done. Extracted {extracted}, skipped {skipped} (already present).");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[BundledLayoutExtractor] ExtractIfMissing failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Converts a resource name like
        /// <c>FiresRPGmaker.BundledLayouts.custom.companionsequipmentscreen.json</c>
        /// into a relative path <c>custom\companionsequipmentscreen.json</c>.
        /// The final ".json" extension is preserved; all earlier dots become
        /// directory separators.
        /// </summary>
        private static string ResourceNameToRelativePath(string resName)
        {
            string trimmed = resName.Substring(ResourcePrefix.Length);

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
