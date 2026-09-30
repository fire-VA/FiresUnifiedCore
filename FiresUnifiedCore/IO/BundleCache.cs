using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.IO
{
    /// <summary>
    /// Asset bundles without the resident archive (MEMORY_LEADS #1, lever A). Every FAP and embedded bundle we ship is one LZMA
    /// block, and Unity keeps a loaded LZMA bundle's whole archive in memory (re-packed) until it is unloaded. R63's bundletest
    /// measured 127 such archives = 3.24 GB of Unity memory per process, and freeing them late did not lower commit. A chunked
    /// LZ4 copy loaded from a file is read from disk on demand instead. BundleCache keeps such a copy per source under
    /// BepInEx\cache\FiresBundles: the first launch loads the source as before and writes the copy in the background (one at a
    /// time), and later launches load the copy. A new build of the owning mod (or a changed file) makes a new copy and prunes
    /// the old one. '-firesnobundlecache' turns it off.
    /// </summary>
    public static class BundleCache
    {
        private const string Tag = "[BundleCache] ";
        private const string OffArgument = "-firesnobundlecache";
        // Smaller bundles aren't worth a copy on disk.
        private const long MinBytes = 1L << 20;

        private static readonly Queue<(string source, string target, bool deleteSource)> s_queue = new Queue<(string, string, bool)>();
        private static readonly HashSet<string> s_queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool s_running;
        private static int s_hits, s_copies;
        private static bool? s_enabled;

        public static bool Enabled
        {
            get
            {
                if (s_enabled == null)
                    s_enabled = !Environment.GetCommandLineArgs().Any(a => string.Equals(a, OffArgument, StringComparison.OrdinalIgnoreCase));
                return s_enabled.Value;
            }
        }

        private static string Root => Path.Combine(BepInEx.Paths.CachePath, "FiresBundles");

        /// <summary>
        /// A bundle embedded as a manifest resource of <paramref name="assembly"/> (the Ascend pattern): the cached LZ4 copy
        /// when there is one, else the resource from memory exactly as before, while a copy is written for the next launch.
        /// <paramref name="resourceName"/> is the full manifest resource name. Null when the resource is missing.
        /// </summary>
        public static AssetBundle LoadEmbedded(Assembly assembly, string resourceName)
        {
            if (assembly == null || string.IsNullOrEmpty(resourceName)) return null;
            string cached = null;
            if (Enabled)
            {
                cached = CopyPath(assembly.GetName().Name, resourceName, assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 12));
                AssetBundle hit = TryCopy(cached);
                if (hit != null) return hit;
            }
            byte[] bytes;
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) return null;
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    bytes = ms.ToArray();
                }
            }
            AssetBundle bundle = AssetBundle.LoadFromMemory(bytes);
            if (bundle != null && cached != null && bytes.LongLength >= MinBytes)
            {
                try
                {
                    Directory.CreateDirectory(Root);
                    string source = cached + ".src";
                    File.WriteAllBytes(source, bytes);
                    Enqueue(source, cached, true);
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"{Tag}couldn't stage a copy of {resourceName}: {ex.Message}");
                }
            }
            return bundle;
        }

        /// <summary>
        /// The file to load for the bundle at <paramref name="path"/>: the cached LZ4 copy when there is one, else
        /// <paramref name="path"/> itself, and a copy is queued for the next launch. For loaders that open the file themselves
        /// (LoadFromFile / LoadFromFileAsync).
        /// </summary>
        public static string Resolve(string path)
        {
            if (!Enabled || string.IsNullOrEmpty(path)) return path;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < MinBytes) return path;
                string cached = CopyPath("files", info.Name, $"{info.Length:x}{info.LastWriteTimeUtc.Ticks:x}");
                if (File.Exists(cached))
                {
                    s_hits++;
                    return cached;
                }
                Directory.CreateDirectory(Root);
                Enqueue(path, cached, false);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{Tag}couldn't check the copy of {path}: {ex.Message}");
            }
            return path;
        }

        /// <summary>Loads the bundle at <paramref name="path"/> through <see cref="Resolve"/>; a broken copy is deleted and the source loaded.</summary>
        public static AssetBundle LoadFile(string path)
        {
            string file = Resolve(path);
            AssetBundle bundle = AssetBundle.LoadFromFile(file);
            if (bundle == null && !string.Equals(file, path, StringComparison.OrdinalIgnoreCase))
            {
                Discard(file);
                bundle = AssetBundle.LoadFromFile(path);
            }
            return bundle;
        }

        /// <summary>A copy that failed to load: deleted, so the next launch makes a new one.</summary>
        public static void Discard(string cachedFile)
        {
            try
            {
                if (cachedFile != null && cachedFile.StartsWith(Root, StringComparison.OrdinalIgnoreCase) && File.Exists(cachedFile))
                {
                    File.Delete(cachedFile);
                    FiresLogger.LogWarning($"{Tag}deleted the unloadable copy {Path.GetFileName(cachedFile)}; the source is used.");
                }
            }
            catch { }
        }

        public static string Status() =>
            $"{(Enabled ? "on" : "off (" + OffArgument + ")")}: {s_hits} copy(ies) loaded, {s_copies} written this session, {s_queue.Count} queued{(s_running ? ", one being written" : "")}";

        private static AssetBundle TryCopy(string cached)
        {
            if (!File.Exists(cached)) return null;
            AssetBundle bundle = AssetBundle.LoadFromFile(cached);
            if (bundle == null)
            {
                Discard(cached);
                return null;
            }
            s_hits++;
            return bundle;
        }

        // <owner>__<name>__<version>.bundle; other versions of the same owner and name are pruned once a new copy is written.
        private static string CopyPath(string owner, string name, string version) =>
            Path.Combine(Root, $"{Safe(owner)}__{Safe(name)}__{version}.bundle");

        private static string Safe(string text)
        {
            char[] bad = Path.GetInvalidFileNameChars();
            return new string(text.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c).ToArray());
        }

        private static void Enqueue(string source, string target, bool deleteSource)
        {
            if (!s_queued.Add(target)) return;
            s_queue.Enqueue((source, target, deleteSource));
            Next();
        }

        // One copy at a time: a recompress reads the whole LZMA stream, and the rig runs at ~90 % commit.
        private static void Next()
        {
            if (s_running || s_queue.Count == 0) return;
            var job = s_queue.Dequeue();
            string partial = job.target + ".part";
            AssetBundleRecompressOperation op;
            try
            {
                op = AssetBundle.RecompressAssetBundleAsync(job.source, partial, BuildCompression.LZ4Runtime);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"{Tag}couldn't start the copy of {Path.GetFileName(job.source)}: {ex.Message}");
                Cleanup(job.source, job.deleteSource, partial);
                Next();
                return;
            }
            s_running = true;
            float started = Time.realtimeSinceStartup;
            op.completed += _ =>
            {
                s_running = false;
                try
                {
                    if (op.success && File.Exists(partial))
                    {
                        if (File.Exists(job.target)) File.Delete(job.target);
                        File.Move(partial, job.target);
                        s_copies++;
                        Prune(job.target);
                        FiresLogger.LogInfo($"{Tag}wrote {Path.GetFileName(job.target)} ({new FileInfo(job.target).Length / 1048576.0:0.0} MB, "
                            + $"{Time.realtimeSinceStartup - started:0.0} s); it loads from disk from the next launch.");
                    }
                    else
                    {
                        FiresLogger.LogWarning($"{Tag}copy of {Path.GetFileName(job.source)} failed: {op.humanReadableResult}");
                    }
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"{Tag}couldn't finish the copy of {Path.GetFileName(job.source)}: {ex.Message}");
                }
                Cleanup(job.source, job.deleteSource, partial);
                Next();
            };
        }

        private static void Cleanup(string source, bool deleteSource, string partial)
        {
            try { if (deleteSource && File.Exists(source)) File.Delete(source); } catch { }
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
        }

        private static void Prune(string kept)
        {
            string name = Path.GetFileName(kept);
            int cut = name.LastIndexOf("__", StringComparison.Ordinal);
            if (cut <= 0) return;
            string prefix = name.Substring(0, cut + 2);
            foreach (string old in Directory.GetFiles(Root, prefix + "*.bundle"))
            {
                if (string.Equals(old, kept, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(old); } catch { }
            }
        }
    }
}
