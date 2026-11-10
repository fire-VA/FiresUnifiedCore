using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Profiling;

namespace FiresCore.Logging
{
    /// <summary>
    /// 0.2.870 (overnight 10-27, Fire: "our excessive logging alone is a perf hit" and "without using too much ram ... lower end pcs").
    /// Two log-only censuses, both OFF by default ([Diagnostics] Log census / RAM census); nothing here changes what any mod does or logs.
    ///   LOG: a passive BepInEx listener counts lines and characters per source and per leading "[Tag]" (no line is altered, held back or
    ///     written by the listener itself); every interval one line lists the top talkers: "[LogCensus] last 60 s: N lines (L/s, K chars/s);
    ///     top: Source [Tag] n (r/s, c ch/s), ...".
    ///   RAM: every interval (and on "fires_census ram"), once a world is loaded: the managed heap, Unity's own memory, the asset counts and
    ///     runtime sizes of textures / meshes / audio / materials, and per Fires plugin assembly its biggest static collections by element
    ///     count ("[RamCensus] FiresUnifiedCore: Type.field 1234, ..."). Reading a static field can run a type's static constructor that
    ///     nothing has run yet, so the walk waits for a loaded world and only covers Fires assemblies.
    /// "fires_census log|ram" prints either one now.
    /// </summary>
    public static class FiresCensus
    {
        private static ConfigEntry<bool> s_log, s_ram;
        private static ConfigEntry<int> s_logSeconds, s_ramMinutes, s_topN;
        private static Listener s_listener;
        private static Runner s_runner;
        private const string LogTag = "[LogCensus] ", RamTag = "[RamCensus] ";

        internal static void BindConfig(ConfigFile cfg)
        {
            s_log = cfg.Bind("Diagnostics", "Log census", false, "Count log lines and characters per mod and per [Tag] and print the top talkers every 'Log census seconds'. Log-only; no line is changed. For finding noisy logging.");
            s_logSeconds = cfg.Bind("Diagnostics", "Log census seconds", 60, new ConfigDescription("Seconds between log census lines.", new AcceptableValueRange<int>(10, 3600)));
            s_ram = cfg.Bind("Diagnostics", "RAM census", false, "Every 'RAM census minutes' (once a world is loaded): managed heap, Unity memory, asset counts and sizes, and each Fires mod's biggest static collections. Log-only.");
            s_ramMinutes = cfg.Bind("Diagnostics", "RAM census minutes", 10, new ConfigDescription("Minutes between RAM census reports.", new AcceptableValueRange<int>(1, 240)));
            s_topN = cfg.Bind("Diagnostics", "Census top N", 12, new ConfigDescription("Entries per census list.", new AcceptableValueRange<int>(3, 50)));
            s_log.SettingChanged += (_, __) => SyncListener();
            SyncListener();
            var go = new GameObject("FiresCensus");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            s_runner = go.AddComponent<Runner>();
        }

        private static void SyncListener()
        {
            if (s_log.Value && s_listener == null) { s_listener = new Listener(); BepInEx.Logging.Logger.Listeners.Add(s_listener); }
            else if (!s_log.Value && s_listener != null) { BepInEx.Logging.Logger.Listeners.Remove(s_listener); s_listener = null; }
        }

        [HarmonyLib.HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class Terminal_InitTerminal_Census
        {
            private static bool s_done;
            private static void Postfix() { if (s_done) return; s_done = true; RegisterCommand(); }
        }

        internal static void RegisterCommand()
        {
            new Terminal.ConsoleCommand("fires_census", "[FiresUnifiedCore] fires_census log|ram - print the log census (needs [Diagnostics] Log census on) or a RAM census now.",
                args =>
                {
                    string what = args.Args.Length > 1 ? args.Args[1].ToLowerInvariant() : "";
                    if (what == "log") { if (s_listener == null) args.Context?.AddString("log census is off ([Diagnostics] Log census)"); else ReportLog(true); }
                    else if (what == "ram") ReportRam();
                    else args.Context?.AddString("usage: fires_census log | ram");
                });
        }

        // ---- log census ---------------------------------------------------------------------------------------------------------
        private sealed class Count { public int Lines; public long Chars; }

        private sealed class Listener : ILogListener
        {
            internal readonly object Gate = new object();
            internal Dictionary<string, Count> Counts = new Dictionary<string, Count>(StringComparer.Ordinal);
            internal int Lines; internal long Chars; internal float Since = Time.realtimeSinceStartup;

            public void LogEvent(object sender, LogEventArgs e)
            {
                if (e == null) return;
                string msg = e.Data as string ?? e.Data?.ToString() ?? "";
                string key = (e.Source?.SourceName ?? "?") + " " + TagOf(msg);
                lock (Gate)
                {
                    Lines++; Chars += msg.Length;
                    if (!Counts.TryGetValue(key, out var c)) Counts[key] = c = new Count();
                    c.Lines++; c.Chars += msg.Length;
                }
            }

            public void Dispose() { }
        }

        // The leading "[Tag]" of a line (Fires lines start with one), else "-".
        private static string TagOf(string m)
        {
            int i = 0;
            while (i < m.Length && m[i] == ' ') i++;
            if (i >= m.Length || m[i] != '[') return "-";
            int end = m.IndexOf(']', i);
            if (end < 0 || end - i > 40) return "-";
            return m.Substring(i, end - i + 1);
        }

        private static void ReportLog(bool force)
        {
            var l = s_listener;
            if (l == null) return;
            Dictionary<string, Count> counts; int lines; long chars; float secs;
            lock (l.Gate)
            {
                secs = Mathf.Max(1f, Time.realtimeSinceStartup - l.Since);
                if (!force && secs < s_logSeconds.Value) return;
                counts = l.Counts; lines = l.Lines; chars = l.Chars;
                l.Counts = new Dictionary<string, Count>(StringComparer.Ordinal); l.Lines = 0; l.Chars = 0; l.Since = Time.realtimeSinceStartup;
            }
            var sb = new StringBuilder();
            sb.Append(LogTag).Append($"last {secs:0} s: {lines} lines ({lines / secs:0.0}/s, {chars / secs / 1024f:0.0} K chars/s); top: ");
            sb.Append(string.Join(", ", counts.OrderByDescending(kv => kv.Value.Chars).Take(s_topN.Value)
                .Select(kv => $"{kv.Key} {kv.Value.Lines} ({kv.Value.Lines / secs:0.0}/s, {kv.Value.Chars / secs:0} ch/s)")));
            FiresLogger.LogDebug(sb.ToString());
        }

        // ---- RAM census ---------------------------------------------------------------------------------------------------------
        // 0.2.955 ([ghost], spike report 10-28: the census made the 727 / 668 / 701 ms frames - the whole walk ran in one frame):
        // the periodic report runs as a coroutine, one section per frame (header, detail, then one Fires plugin per frame), and
        // logs each section's time at Debug. "fires_census ram" still prints it all at once.
        private static IEnumerator ReportRamSpread()
        {
            var sw = new System.Diagnostics.Stopwatch();
            var times = new List<string>();
            long mb = 1024L * 1024L;
            // 0.2.957 (lead 10-28, [classes-ui]: a 556 ms settled stall named FiresCensus -> FindObjectsOfTypeAll(GameObject) 24 ms): the
            // header's whole-heap walks no longer share one frame. The memory numbers first, then each asset kind in its own frame; the
            // GameObject count (every loaded GameObject, prefabs included) is left to "fires_census ram"
            sw.Restart();
            try { FiresLogger.LogDebug(RamTag + $"managed heap {GC.GetTotalMemory(false) / mb} MB (mono used {Profiler.GetMonoUsedSizeLong() / mb} / heap {Profiler.GetMonoHeapSizeLong() / mb} MB); unity allocated {Profiler.GetTotalAllocatedMemoryLong() / mb} MB / reserved {Profiler.GetTotalReservedMemoryLong() / mb} MB"); }
            catch (Exception e) { FiresLogger.LogWarning(RamTag + "failed: " + e.Message); yield break; }
            times.Add($"header {sw.Elapsed.TotalMilliseconds:0} ms");
            // 0.2.1182 (group F, [lead]): counts for the memory rows; plain .Count reads, one line per report, Debug.
            try
            {
                var zm = ZDOMan.instance;
                if (zm != null)
                {
                    long peerZdos = 0; foreach (var peer in zm.m_peers) peerZdos += peer.m_zdos.Count;
                    FiresLogger.LogDebug($"{RamTag}zdos: {zm.m_objectsByID.Count} live, {zm.m_deadZDOs.Count} dead table, {zm.m_destroySendList.Count} destroy list, {zm.m_clientChangeQueue.Count} client queue, {zm.m_peers.Count} peer(s) tracking {peerZdos}; pool {ZDOPool.GetPoolActive()} active, {ZDOPool.GetPoolSize()} free");
                }
            }
            catch (Exception e) { FiresLogger.LogDebug(RamTag + "zdo counts failed: " + e.Message); }
            var kinds = new (string label, Func<string> read)[]
            {
                ("textures", () => Assets<Texture2D>("textures")), ("render textures", () => Assets<RenderTexture>("render textures")),
                ("meshes", () => Assets<Mesh>("meshes")), ("audio", () => Assets<AudioClip>("audio")), ("materials", () => Assets<Material>("materials")),
            };
            var parts = new List<string>();
            foreach (var k in kinds)
            {
                yield return null;
                sw.Restart();
                try { parts.Add(k.read()); } catch (Exception e) { parts.Add($"{k.label} failed: {e.Message}"); }
                times.Add($"{k.label} {sw.Elapsed.TotalMilliseconds:0} ms");
            }
            FiresLogger.LogDebug(RamTag + "assets: " + string.Join(", ", parts));
            // 0.2.958 (gh_zb_core round, Core 957: "detail 440 ms" in one frame): the detail runs one step per frame too
            var detail = ReportRamDetailSteps(mb, times);
            while (true)
            {
                yield return null;
                bool more;
                try { more = detail.MoveNext(); } catch (Exception e) { FiresLogger.LogWarning(RamTag + "detail failed: " + e.Message); more = false; }
                if (!more) break;
            }
            foreach (var info in BepInEx.Bootstrap.Chainloader.PluginInfos.Values.ToList())
            {
                if (info?.Instance == null || info.Metadata == null || !info.Metadata.GUID.StartsWith("com.Fire.", StringComparison.Ordinal)) continue;
                yield return null;
                sw.Restart();
                try { ReportPluginStatics(info); } catch (Exception e) { FiresLogger.LogWarning(RamTag + info.Metadata.Name + " failed: " + e.Message); }
                double ms = sw.Elapsed.TotalMilliseconds;
                if (ms >= 5) times.Add($"{info.Metadata.Name} {ms:0} ms");
            }
            FiresLogger.LogDebug($"{RamTag}timing (one section per frame): {string.Join(", ", times)}");
        }

        private static void ReportPluginStatics(BepInEx.PluginInfo info)
        {
            var asm = info.Instance.GetType().Assembly;
            var top = StaticCollections(asm).OrderByDescending(t => t.count).Take(s_topN.Value).ToList();
            long total = top.Sum(t => (long)t.count);
            FiresLogger.LogDebug($"{RamTag}{info.Metadata.Name}: top static collections ({total} elements in the top {top.Count}): " +
                (top.Count == 0 ? "none" : string.Join(", ", top.Select(t => $"{t.name} {t.count}"))));
        }

        private static void ReportRam()
        {
            try
            {
                var sb = new StringBuilder();
                long mb = 1024L * 1024L;
                sb.Append(RamTag).Append($"managed heap {GC.GetTotalMemory(false) / mb} MB (mono used {Profiler.GetMonoUsedSizeLong() / mb} / heap {Profiler.GetMonoHeapSizeLong() / mb} MB); ")
                  .Append($"unity allocated {Profiler.GetTotalAllocatedMemoryLong() / mb} MB / reserved {Profiler.GetTotalReservedMemoryLong() / mb} MB; ")
                  .Append(Assets<Texture2D>("textures")).Append(", ").Append(Assets<RenderTexture>("render textures")).Append(", ")
                  .Append(Assets<Mesh>("meshes")).Append(", ").Append(Assets<AudioClip>("audio")).Append(", ").Append(Assets<Material>("materials"))
                  .Append($", {Resources.FindObjectsOfTypeAll<GameObject>().Length} GameObjects");
                FiresLogger.LogDebug(sb.ToString());
                ReportRamDetail(mb);
                foreach (var info in BepInEx.Bootstrap.Chainloader.PluginInfos.Values)
                {
                    if (info?.Instance == null || info.Metadata == null || !info.Metadata.GUID.StartsWith("com.Fire.", StringComparison.Ordinal)) continue;
                    var asm = info.Instance.GetType().Assembly;
                    var top = StaticCollections(asm).OrderByDescending(t => t.count).Take(s_topN.Value).ToList();
                    long total = top.Sum(t => (long)t.count);
                    FiresLogger.LogDebug($"{RamTag}{info.Metadata.Name}: top static collections ({total} elements in the top {top.Count}): " +
                        (top.Count == 0 ? "none" : string.Join(", ", top.Select(t => $"{t.name} {t.count}"))));
                }
            }
            catch (Exception e) { FiresLogger.LogWarning(RamTag + "failed: " + e.Message); }
        }

        // 0.2.877 ([wishbone], lead 10-27: Fire's client sits at 10.5 GB private with ~5-6 GB accounted for): where the rest is. Lines:
        //   "[RamCensus] process private P MB / working set W MB; unity reserved R (allocated A, unused reserved U), graphics driver G,
        //    temp allocator T; mono heap H (used M, reserved-unused H-M) MB; not seen by Unity or mono ~X MB (native plugins, driver, bundles' files)"
        //   "[RamCensus] asset bundles: N loaded, S MB (own runtime size); top: name S MB (n assets), ..."
        //   "[RamCensus] top textures / meshes / render textures: name size (dims/format; bundle or camera owner), ..." (top N each, 20 by default)
        // "fires_census ram": the whole detail at once
        private static void ReportRamDetail(long mb)
        {
            var steps = ReportRamDetailSteps(mb, null);
            while (steps.MoveNext()) { }
        }

        // The detail as steps (memory split, bundles, owners, then each top list); the periodic report runs one step per frame and
        // adds each step's time to times (null = untimed)
        private static IEnumerator ReportRamDetailSteps(long mb, List<string> times)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            void Done(string step) { if (times != null) times.Add($"{step} {sw.Elapsed.TotalMilliseconds:0} ms"); }
            try
            {
                long priv = 0, ws = 0;
                // 0.2.878: mono's Process returned 0 for both in round #5; psapi reads the real counters.
                try
                {
                    var pmc = new ProcessMemoryCounters { cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(ProcessMemoryCounters)) };
                    if (GetProcessMemoryInfo(GetCurrentProcess(), out pmc, pmc.cb)) { priv = (long)pmc.PrivateUsage; ws = (long)pmc.WorkingSetSize; }
                }
                catch { }
                if (priv == 0) try { using (var p = System.Diagnostics.Process.GetCurrentProcess()) { priv = p.PrivateMemorySize64; ws = p.WorkingSet64; } } catch { }
                long res = Profiler.GetTotalReservedMemoryLong(), alloc = Profiler.GetTotalAllocatedMemoryLong(), unused = Profiler.GetTotalUnusedReservedMemoryLong();
                long gfx = Profiler.GetAllocatedMemoryForGraphicsDriver(), temp = Profiler.GetTempAllocatorSize();
                long heap = Profiler.GetMonoHeapSizeLong(), used = Profiler.GetMonoUsedSizeLong();
                long seen = res + heap + gfx;
                FiresLogger.LogDebug($"{RamTag}process private {priv / mb} MB / working set {ws / mb} MB; unity reserved {res / mb} (allocated {alloc / mb}, unused reserved {unused / mb}), graphics driver {gfx / mb}, temp allocator {temp / mb}; mono heap {heap / mb} (used {used / mb}, reserved-unused {(heap - used) / mb}) MB; not seen by Unity or mono ~{Math.Max(0L, priv - seen) / mb} MB (native plugins, driver, bundle files)");
            }
            catch (Exception e) { FiresLogger.LogWarning(RamTag + "memory split failed: " + e.Message); }
            Done("memory split");
            yield return null;
            sw.Restart();

            int topN = Math.Max(20, s_topN.Value);
            // Which bundle each asset came from (by name), for the owner column.
            var bundleOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // 0.2.959 (Core 958 round: "bundles 38 ms" in one frame): the bundles are walked a few per frame (yield past 4 ms)
            List<AssetBundle> bundles = null;
            try { bundles = AssetBundle.GetAllLoadedAssetBundles().Where(b => b != null).ToList(); }
            catch (Exception e) { FiresLogger.LogWarning(RamTag + "bundle list failed: " + e.Message); }
            if (bundles != null)
            {
                var rows = new List<(string name, long bytes, int assets)>();
                long total = 0, bundleTicks = 0;
                var slice = System.Diagnostics.Stopwatch.StartNew();
                foreach (var b in bundles)
                {
                    try
                    {
                        if (b == null) continue;
                        long bytes = Profiler.GetRuntimeMemorySizeLong(b);
                        total += bytes;
                        string[] names = Array.Empty<string>();
                        try { names = b.isStreamedSceneAssetBundle ? b.GetAllScenePaths() : b.GetAllAssetNames(); } catch { }
                        foreach (var n in names) { string k = System.IO.Path.GetFileNameWithoutExtension(n); if (!bundleOf.ContainsKey(k)) bundleOf[k] = b.name; }
                        rows.Add((b.name, bytes, names.Length));
                    }
                    catch { }
                    if (slice.Elapsed.TotalMilliseconds >= 4.0) { bundleTicks += sw.ElapsedTicks; yield return null; sw.Restart(); slice.Restart(); }
                }
                bundleTicks += sw.ElapsedTicks;
                try
                {
                    FiresLogger.LogDebug($"{RamTag}asset bundles: {bundles.Count} loaded, {total / mb} MB (own runtime size); top: " +
                        string.Join(", ", rows.OrderByDescending(r => r.bytes).Take(topN).Select(r => $"{r.name} {r.bytes / (double)mb:0.0} MB ({r.assets} assets)")));
                }
                catch (Exception e) { FiresLogger.LogWarning(RamTag + "bundle list failed: " + e.Message); }
                if (times != null) times.Add($"bundles {bundleTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms total over {bundles.Count} bundle(s), at most ~4 ms a frame");
            }
            else Done("bundles");
            yield return null;
            sw.Restart();

            var rtOwner = new Dictionary<RenderTexture, string>();
            try
            {
                foreach (var cam in Resources.FindObjectsOfTypeAll<Camera>())
                    if (cam != null && cam.targetTexture != null && !rtOwner.ContainsKey(cam.targetTexture)) rtOwner[cam.targetTexture] = "camera " + cam.name + OwnerAsm(cam.gameObject);
            }
            catch { }
            var meshUsers = MeshUsers();
            string Owner(UnityEngine.Object o)
            {
                if (o is RenderTexture rt && rtOwner.TryGetValue(rt, out string c)) return c;
                string b = bundleOf.TryGetValue(o.name ?? "", out string bn) ? "bundle " + bn : "";
                if (o is Mesh m) { string u = MeshUserText(meshUsers.TryGetValue(m, out Component mu) ? mu : null); return b.Length > 0 ? b + "; " + u : u; }
                return b;
            }
            Done("owners");
            yield return null;
            sw.Restart();
            TopObjects<Texture2D>("textures", topN, mb, t => $"{t.width}x{t.height} {t.format}{(t.mipmapCount > 1 ? " mips" : "")}", Owner);
            Done("top textures");
            yield return null;
            sw.Restart();
            TopObjects<Mesh>("meshes", topN, mb, m => $"{m.vertexCount} verts{(m.isReadable ? " readable" : "")}", Owner);
            Done("top meshes");
            yield return null;
            sw.Restart();
            TopObjects<RenderTexture>("render textures", topN, mb, r => $"{r.width}x{r.height} {r.format} / {r.graphicsFormat}, MSAA x{r.antiAliasing}, depth {r.depth} bits ({r.depthStencilFormat}), {(UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(r) / (double)Mathf.Max(1, r.width * r.height)):0.0} B/px{(r.useMipMap ? ", mips" : "")}"   /* 0.2.1017 ([fgn] / lead 10-30: does MSAA or the format drive the camera buffers?) */, Owner);
            Done("top render textures");
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }
        [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, uint size);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        // 0.2.878 (lead: give the big meshes an owner): who uses a mesh - the first MeshFilter / SkinnedMeshRenderer / MeshCollider holding
        // it: "used by Root/.../Child in scene S (assembly)", or "no user (orphan)" when nothing in the loaded scenes / DontDestroyOnLoad holds it.
        // 0.2.959 (gh_zb_core round, Core 958: "owners 362 ms" in one frame): the walk keeps only the first user component per mesh; the
        // "used by ..." text (path, scene, owning assembly up the parents) is built by MeshUserText for the top meshes the report prints
        private static Dictionary<Mesh, Component> MeshUsers()
        {
            var d = new Dictionary<Mesh, Component>();
            void Add(Mesh m, Component c) { if (m != null && c != null && !d.ContainsKey(m)) d[m] = c; }
            try
            {
                foreach (var f in Resources.FindObjectsOfTypeAll<MeshFilter>()) Add(f != null ? f.sharedMesh : null, f);
                foreach (var s in Resources.FindObjectsOfTypeAll<SkinnedMeshRenderer>()) Add(s != null ? s.sharedMesh : null, s);
                foreach (var c in Resources.FindObjectsOfTypeAll<MeshCollider>()) Add(c != null ? c.sharedMesh : null, c);
            }
            catch { }
            return d;
        }

        private static string MeshUserText(Component c)
        {
            if (c == null) return "no user (orphan)";
            var go = c.gameObject;
            string scene = go.scene.IsValid() ? go.scene.name : "asset/prefab";
            string asm = OwnerAsm(go);
            for (var t = go.transform.parent; asm.Length == 0 && t != null; t = t.parent) asm = OwnerAsm(t.gameObject);
            return $"used by {PathOf(go.transform)} in {scene}{asm}";
        }

        private static string PathOf(Transform t)
        {
            var parts = new List<string>();
            for (int i = 0; t != null && i < 6; t = t.parent, i++) parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string OwnerAsm(GameObject go)
        {
            foreach (var mb in go.GetComponents<MonoBehaviour>())
            {
                if (mb == null) continue;
                string a = mb.GetType().Assembly.GetName().Name;
                if (a != "assembly_valheim" && !a.StartsWith("Unity", StringComparison.Ordinal)) return " (" + a + ")";
            }
            return "";
        }

        private static void TopObjects<T>(string label, int topN, long mb, Func<T, string> what, Func<UnityEngine.Object, string> owner) where T : UnityEngine.Object
        {
            try
            {
                var top = Resources.FindObjectsOfTypeAll<T>().Where(o => o != null).Select(o => (o, bytes: Profiler.GetRuntimeMemorySizeLong(o)))
                    .OrderByDescending(x => x.bytes).Take(topN).ToList();
                FiresLogger.LogDebug($"{RamTag}top {label}: " + string.Join(", ", top.Select(x =>
                {
                    string own = owner(x.o);
                    return $"{(string.IsNullOrEmpty(x.o.name) ? "(unnamed)" : x.o.name)} {x.bytes / (double)mb:0.0} MB ({what(x.o)}{(own.Length > 0 ? "; " + own : "")})";
                })));
            }
            catch (Exception e) { FiresLogger.LogWarning($"{RamTag}top {label} failed: {e.Message}"); }
        }

        private static string Assets<T>(string label) where T : UnityEngine.Object
        {
            var all = Resources.FindObjectsOfTypeAll<T>();
            long bytes = 0;
            foreach (var o in all) bytes += Profiler.GetRuntimeMemorySizeLong(o);
            return $"{all.Length} {label} {bytes / (1024L * 1024L)} MB";
        }

        private static IEnumerable<(string name, int count)> StaticCollections(Assembly asm)
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
            const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var t in types)
            {
                if (t.ContainsGenericParameters) continue;
                FieldInfo[] fields;
                try { fields = t.GetFields(F); } catch { continue; }
                foreach (var f in fields)
                {
                    if (f.IsLiteral || !(typeof(ICollection).IsAssignableFrom(f.FieldType) || f.FieldType.IsArray || IsGenericCollection(f.FieldType))) continue;
                    int n;
                    try { n = CountOf(f.GetValue(null)); } catch { continue; }
                    if (n > 0) yield return ($"{t.Name}.{f.Name}", n);
                }
            }
        }

        private static bool IsGenericCollection(Type t) => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICollection<>));

        private static int CountOf(object v)
        {
            if (v == null) return 0;
            if (v is ICollection c) return c.Count;
            var p = v.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            return p != null && p.PropertyType == typeof(int) ? (int)p.GetValue(v) : 0;
        }

        private sealed class Runner : MonoBehaviour
        {
            private float _nextRam = -1f;
            private void Update()
            {
                if (s_listener != null) ReportLog(false);
                if (s_ram == null || !s_ram.Value || ZNet.instance == null) { _nextRam = -1f; return; }
                if (_nextRam < 0f) { _nextRam = Time.realtimeSinceStartup + 60f; return; }   // the first report a minute after the world loads
                if (Time.realtimeSinceStartup < _nextRam) return;
                _nextRam = Time.realtimeSinceStartup + s_ramMinutes.Value * 60f;
                StartCoroutine(ReportRamSpread());
            }
        }
    }
}
