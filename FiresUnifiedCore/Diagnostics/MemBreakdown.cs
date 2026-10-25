using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// A Debug "[Mem] breakdown" so the memory is measured,
    /// not guessed. Logged once at the main menu, once after the local player spawns (on a dedi: once ZNet is up); only at Log Level = Debug (no periodic repeat).
    /// Process private / working set, Unity's allocator and Mono heap, graphics-driver memory, and per-type counts and bytes of loaded
    /// textures, render textures, meshes, materials and audio clips, with the 15 biggest textures and meshes by name.
    /// Read-only: nothing is unloaded or changed.
    /// </summary>
    internal static class MemBreakdown
    {
        //
        // no periodic run any more; only the menu / after-join / dedi-up snapshots, and those only at Log Level = Debug (checked in Log
        // BEFORE anything is measured).
        private const int Top = 15;

        [HarmonyPatch(typeof(FejdStartup), "Start")]
        private static class AtMenu
        {
            private static void Postfix() { Log("main menu"); }
        }

        [HarmonyPatch(typeof(Player), nameof(Player.OnSpawned))]
        private static class AfterJoin
        {
            private static bool _done;
            private static void Postfix(Player __instance)
            {
                if (_done || __instance != Player.m_localPlayer) return;
                _done = true;
                Log("after join");
            }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class DediUp
        {
            private static void Postfix()
            {
                if (ZNet.instance == null || !ZNet.instance.IsDedicated()) return;
                Log("dedi world up");
            }
        }

        private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0");

        // Mono's Process counters can be 0 in the
        // dedicated server; Windows' own GetProcessMemoryInfo gives the real private bytes and working set.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS_EX
        {
            public uint cb, PageFaultCount;
            public System.UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
        }

        [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(System.IntPtr process, out PROCESS_MEMORY_COUNTERS_EX counters, uint size);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern System.IntPtr GetCurrentProcess();

        private static void ProcessMemory(out long priv, out long ws, out string src)
        {
            priv = ws = 0; src = "Process";
            try
            {
                if (Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsServer || Application.platform == RuntimePlatform.WindowsEditor)
                {
                    var c = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS_EX)) };
                    if (GetProcessMemoryInfo(GetCurrentProcess(), out c, c.cb)) { priv = (long)c.PrivateUsage.ToUInt64(); ws = (long)c.WorkingSetSize.ToUInt64(); src = "psapi"; }
                }
            }
            catch { }
            if (priv == 0 || ws == 0)
            {
                try { using (var p = Process.GetCurrentProcess()) { if (priv == 0) priv = p.PrivateMemorySize64; if (ws == 0) ws = p.WorkingSet64; } src = src == "psapi" ? "psapi+Process" : "Process"; }
                catch { }
            }
        }

        internal static void Log(string when)
        {
            if (!FiresLogger.DebugEnabled) return;   // The level decides before the 126k-object scan, not at LogDebug
            try
            {
                var sw = Stopwatch.StartNew();
                var sb = new StringBuilder(4096);
                ProcessMemory(out long priv, out long ws, out string src);
                sb.Append($"[Mem] breakdown ({when}): process private {Mb(priv)} MB, working set {Mb(ws)} MB ({src})");
                sb.Append($"; Unity allocated {Mb(Profiler.GetTotalAllocatedMemoryLong())} MB, reserved {Mb(Profiler.GetTotalReservedMemoryLong())} MB");
                sb.Append($"; Mono used {Mb(Profiler.GetMonoUsedSizeLong())} MB, heap {Mb(Profiler.GetMonoHeapSizeLong())} MB");
                sb.Append($"; graphics driver {Mb(Profiler.GetAllocatedMemoryForGraphicsDriver())} MB");

                var textures = Sizes(Resources.FindObjectsOfTypeAll<Texture2D>());
                var meshes = Sizes(Resources.FindObjectsOfTypeAll<Mesh>());
                sb.Append($"\n  Texture2D {Summary(textures)}");
                sb.Append($"\n  RenderTexture {Summary(Sizes(Resources.FindObjectsOfTypeAll<RenderTexture>()))}");
                sb.Append($"\n  Mesh {Summary(meshes)}");
                sb.Append($"\n  Material {Summary(Sizes(Resources.FindObjectsOfTypeAll<Material>()))}");
                sb.Append($"\n  AudioClip {Summary(Sizes(Resources.FindObjectsOfTypeAll<AudioClip>()))}");
                sb.Append($"\n  top {Top} textures: {TopList(textures, t => t is Texture2D tex ? $" {tex.width}x{tex.height} {tex.format}" : "")}");
                sb.Append($"\n  top {Top} meshes: {TopList(meshes, m => m is Mesh mesh ? $" {mesh.vertexCount}v r{(mesh.isReadable ? 1 : 0)}" : "")}");
                sb.Append($"\n  managed: GC total {Mb(GC.GetTotalMemory(false))} MB, collections gen0/1/2 {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
                StaticHolders(sb);
                sb.Append($"\n  (measured in {sw.ElapsedMilliseconds} ms)");
                FiresLogger.LogDebug(sb.ToString());
            }
            catch (Exception ex)
            {
                FiresLogger.LogDebug($"[Mem] breakdown ({when}) failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // The
        // static collections of every plugin assembly (BepInEx's loaded plugins), by element count and a rough size (elements x 16 B,
        // or array length x element size, plus string lengths). Not exact, but it names which mod / field holds the most.
        private const int TopFields = 25;

        private static void StaticHolders(StringBuilder sb)
        {
            var perAssembly = new Dictionary<string, (long count, long bytes)>();
            var fields = new List<(string name, long count, long bytes)>();
            var skipped = new List<string>();
            var assemblies = BepInEx.Bootstrap.Chainloader.PluginInfos.Values
                .Where(p => p?.Instance != null).Select(p => p.Instance.GetType().Assembly).Distinct().ToList();
            const System.Reflection.BindingFlags Flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
            foreach (var asm in assemblies)
            {
                string asmName = asm.GetName().Name;
                Type[] types;
                try { types = asm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                foreach (var type in types)
                {
                    if (type.ContainsGenericParameters) continue;
                    System.Reflection.FieldInfo[] fs;
                    try { fs = type.GetFields(Flags); } catch { continue; }
                    foreach (var f in fs)
                    {
                        if (f.IsLiteral || f.FieldType.IsPrimitive || f.FieldType.IsEnum) continue;
                        object value;
                        try { value = f.GetValue(null); } catch { continue; }
                        if (!(value is System.Collections.ICollection col)) continue;
                        // Another mod's static collection being
                        // changed on a worker thread while it is sampled; skip that one field and name it instead of losing the whole breakdown
                        long count, bytes;
                        try { count = col.Count; bytes = EstimateBytes(col); }
                        catch (Exception ex) { skipped.Add($"{asmName}:{type.Name}.{f.Name} ({ex.GetType().Name})"); continue; }
                        if (count == 0) continue;
                        fields.Add(($"{asmName}:{type.Name}.{f.Name}", count, bytes));
                        perAssembly.TryGetValue(asmName, out var sum);
                        perAssembly[asmName] = (sum.count + count, sum.bytes + bytes);
                    }
                }
            }
            sb.Append($"\n  static collections by mod (elements, ~MB): " + string.Join("; ", perAssembly.OrderByDescending(kv => kv.Value.bytes)
                .Take(15).Select(kv => $"{kv.Key} {kv.Value.count} ~{Mb(kv.Value.bytes)}")));
            sb.Append($"\n  top {TopFields} static fields: " + string.Join("; ", fields.OrderByDescending(x => x.bytes).Take(TopFields)
                .Select(x => $"{x.name} {x.count} ~{Mb(x.bytes)} MB")));
            if (skipped.Count > 0) sb.Append($"\n  skipped (changed while read): {string.Join("; ", skipped.Take(10))}");
        }

        private static long EstimateBytes(System.Collections.ICollection col)
        {
            if (col is Array arr)
            {
                Type et = arr.GetType().GetElementType();
                int size = et != null && et.IsValueType ? (et.IsPrimitive ? System.Runtime.InteropServices.Marshal.SizeOf(et) : 16) : 8;
                return (long)arr.LongLength * size;
            }
            long bytes = (long)col.Count * 16;
            int sampled = 0;
            foreach (object o in col)   // sample the first 64 elements for strings / nested arrays and scale up
            {
                if (++sampled > 64) break;
                object v = o is System.Collections.DictionaryEntry de ? de.Value : o;
                var kv = v?.GetType();
                if (kv != null && kv.IsGenericType && kv.GetGenericTypeDefinition() == typeof(KeyValuePair<,>)) v = kv.GetProperty("Value")?.GetValue(v);
                if (v is string s) bytes += (long)s.Length * 2 * col.Count / 64;
                else if (v is Array a) bytes += a.LongLength * 4 * col.Count / 64;
                else if (v is System.Collections.ICollection c) bytes += (long)c.Count * 16 * col.Count / 64;
            }
            return bytes;
        }

        private static List<(UnityEngine.Object obj, long bytes)> Sizes<T>(T[] objects) where T : UnityEngine.Object =>
            objects.Where(o => o != null).Select(o => ((UnityEngine.Object)o, Profiler.GetRuntimeMemorySizeLong(o))).ToList();

        private static string Summary(List<(UnityEngine.Object obj, long bytes)> list) =>
            $"{list.Count} loaded, {Mb(list.Sum(x => x.bytes))} MB";

        private static string TopList(List<(UnityEngine.Object obj, long bytes)> list, Func<UnityEngine.Object, string> detail) =>
            string.Join("; ", list.OrderByDescending(x => x.bytes).Take(Top)
                .Select(x => $"{(string.IsNullOrEmpty(x.obj.name) ? "(unnamed)" : x.obj.name)} {Mb(x.bytes)} MB{detail(x.obj)}"));
    }
}
