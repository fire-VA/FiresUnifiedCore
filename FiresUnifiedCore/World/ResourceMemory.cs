using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>A remembered place where an item was gathered.</summary>
    public readonly struct ResourceSpot
    {
        public readonly Vector3 At;
        public readonly string Item;
        public readonly string Source;
        public readonly Heightmap.Biome Biome;
        public readonly int TimesFound;
        public readonly int LastCount;
        /// <summary>Game time (s) of the last find.</summary>
        public readonly double LastSeenGameTime;
        /// <summary>A regrowing source picked and not back yet.</summary>
        public readonly bool Regrowing;
        public readonly double ReadyAtGameTime;
        /// <summary>Flat distance from the search point (m).</summary>
        public readonly float Distance;

        internal ResourceSpot(ResourceMemory.Entry e, float distance, double now)
        {
            At = new Vector3(e.X, e.Y, e.Z);
            Item = e.Item;
            Source = e.Source;
            Biome = (Heightmap.Biome)e.Biome;
            TimesFound = e.Times;
            LastCount = e.LastCount;
            LastSeenGameTime = e.LastSeen;
            ReadyAtGameTime = e.ReadyAt;
            Regrowing = e.Regrows && now < e.ReadyAt;
            Distance = distance;
        }
    }

    /// <summary>
    /// Where each item was actually found (Core 0.2.203, AUTOPLAY_PROGRESSION §7.12/§7.14): recorded on every gather, searched first
    /// before the spawn knowledge and the explorer. Persisted per world on the gathering body's own client, under its BepInEx folder
    /// (a client has no world save; the bot's ProgramData writes vanish under Sandboxie):
    /// BepInEx\config\FiresCore\WorldMemory\&lt;world&gt;_&lt;seed&gt;.resources.json. The explorer's map is a separate file ([visual]).
    /// </summary>
    [HarmonyPatch]
    public static class ResourceMemory
    {
        /// <summary>Finds of the same item within this distance (m) are one spot.</summary>
        public const float MergeMetres = 8f;
        private const float FlushSeconds = 30f;
        private const int FileVersion = 1;

        internal sealed class Entry
        {
            public string Item, Source;
            public float X, Y, Z;
            public int Biome, Times, LastCount;
            public double LastSeen, ReadyAt;
            public bool Regrows;
        }

        private sealed class FileData
        {
            public int Version = FileVersion;
            public string World;
            public int Seed;
            public List<Entry> Resources = new List<Entry>();
        }

        private static FileData s_data;
        private static string s_path;
        private static bool s_dirty;
        private static float s_nextFlush;

        /// <summary>Remember a gather: <paramref name="count"/> of <paramref name="itemPrefab"/> from <paramref name="sourcePrefab"/> at <paramref name="pos"/>.</summary>
        public static void Record(string itemPrefab, int count, string sourcePrefab, Vector3 pos, bool regrows = false, float regrowSeconds = 0f)
        {
            if (string.IsNullOrEmpty(itemPrefab) || !EnsureLoaded()) return;
            double now = GameTime();
            Entry e = Nearest(itemPrefab, pos, MergeMetres);
            if (e == null)
            {
                e = new Entry { Item = itemPrefab, X = pos.x, Y = pos.y, Z = pos.z };
                s_data.Resources.Add(e);
            }
            e.Source = sourcePrefab ?? e.Source;
            e.Biome = WorldGenerator.instance != null ? (int)WorldGenerator.instance.GetBiome(pos.x, pos.z) : e.Biome;
            e.Times++;
            e.LastCount = count;
            e.LastSeen = now;
            e.Regrows = regrows;
            e.ReadyAt = regrows ? now + regrowSeconds : 0d;
            Debug.Log($"[ResourceMemory] found {itemPrefab} x{count} at ({pos.x:0}, {pos.z:0}) ({sourcePrefab ?? "?"}, {(Heightmap.Biome)e.Biome}; " +
                      $"{Ordinal(e.Times)} time here)");
            MarkDirty();
        }

        /// <summary>Known spots for <paramref name="itemPrefab"/> within <paramref name="leash"/> m of <paramref name="from"/>, nearest first.</summary>
        public static List<ResourceSpot> Search(string itemPrefab, Vector3 from, float leash = float.PositiveInfinity,
            Heightmap.Biome biome = Heightmap.Biome.None, bool includeRegrowing = false, int max = 10)
        {
            var found = new List<ResourceSpot>();
            if (string.IsNullOrEmpty(itemPrefab) || !EnsureLoaded()) return found;
            double now = GameTime();
            foreach (Entry e in s_data.Resources)
            {
                if (e.Item != itemPrefab) continue;
                if (biome != Heightmap.Biome.None && ((Heightmap.Biome)e.Biome & biome) == 0) continue;
                float d = Flat(from, e.X, e.Z);
                if (d > leash) continue;
                var spot = new ResourceSpot(e, d, now);
                if (spot.Regrowing && !includeRegrowing) continue;
                found.Add(spot);
            }
            found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            if (found.Count > max) found.RemoveRange(max, found.Count - max);
            return found;
        }

        /// <summary>Drop the spot of <paramref name="itemPrefab"/> near <paramref name="pos"/> (found empty: "picked clean").</summary>
        public static void Forget(string itemPrefab, Vector3 pos, string why)
        {
            if (string.IsNullOrEmpty(itemPrefab) || !EnsureLoaded()) return;
            Entry e = Nearest(itemPrefab, pos, MergeMetres);
            if (e == null) return;
            s_data.Resources.Remove(e);
            Debug.Log($"[ResourceMemory] forgot {itemPrefab} at ({e.X:0}, {e.Z:0}): {why}");
            MarkDirty();
        }

        /// <summary>Write now (also at a flush interval after changes, and at logout).</summary>
        public static void Flush()
        {
            if (!s_dirty || s_data == null || string.IsNullOrEmpty(s_path)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(s_path));
                string tmp = s_path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(s_data, Formatting.Indented));
                if (File.Exists(s_path)) File.Delete(s_path);
                File.Move(tmp, s_path);
                s_dirty = false;
            }
            catch (Exception ex) { Debug.LogWarning($"[ResourceMemory] could not write {s_path}: {ex.Message}"); }
        }

        private static void MarkDirty()
        {
            s_dirty = true;
            if (Time.realtimeSinceStartup >= s_nextFlush)
            {
                s_nextFlush = Time.realtimeSinceStartup + FlushSeconds;
                Flush();
            }
        }

        // The file for the world this client is in (its world generator's world: name + seed), loaded once per world.
        private static bool EnsureLoaded()
        {
            global::World world = WorldGenerator.instance != null ? WorldGenerator.instance.m_world : null;
            if (world == null) return false;
            string path = Path.Combine(BepInEx.Paths.ConfigPath, "FiresCore", "WorldMemory", $"{Safe(world.m_name)}_{world.m_seed}.resources.json");
            if (s_data != null && path == s_path) return true;
            Flush();
            s_path = path;
            s_data = null;
            s_dirty = false;
            try
            {
                if (File.Exists(path)) s_data = JsonConvert.DeserializeObject<FileData>(File.ReadAllText(path));
            }
            catch (Exception ex) { Debug.LogWarning($"[ResourceMemory] could not read {path} ({ex.Message}); starting empty"); }
            if (s_data == null || s_data.Resources == null) s_data = new FileData { World = world.m_name, Seed = world.m_seed };
            Debug.Log($"[ResourceMemory] loaded {s_data.Resources.Count} spot(s) for {world.m_name} (seed {world.m_seed})");
            return true;
        }

        private static Entry Nearest(string item, Vector3 pos, float within)
        {
            Entry best = null;
            float bestD = within;
            foreach (Entry e in s_data.Resources)
            {
                if (e.Item != item) continue;
                float d = Flat(pos, e.X, e.Z);
                if (d <= bestD) { bestD = d; best = e; }
            }
            return best;
        }

        private static double GameTime() => ZNet.instance != null ? ZNet.instance.GetTimeSeconds() : Time.time;

        private static float Flat(Vector3 a, float x, float z)
        {
            float dx = a.x - x, dz = a.z - z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static string Safe(string name)
        {
            if (string.IsNullOrEmpty(name)) return "world";
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        private static string Ordinal(int n) =>
            n % 100 >= 11 && n % 100 <= 13 ? $"{n}th" : (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" };

        // Plugins die before ZNet on quit: write at ZNet.Shutdown (logout and quit).
        [HarmonyPatch(typeof(ZNet), "Shutdown"), HarmonyPrefix]
        private static void FlushOnShutdown()
        {
            try { Flush(); } catch { }
            try { EnemyMemory.Flush(); } catch { }   // the enemies + danger file (0.2.219), same moment
        }
    }
}
