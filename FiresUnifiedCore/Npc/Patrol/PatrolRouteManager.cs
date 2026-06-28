using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace FiresCore.Npc.Patrol
{
    /// <summary>
    /// Loads/saves NPC patrol routes to BepInEx/config/FiresNPCs_PatrolRoutes/&lt;name&gt;.cfg — one
    /// "x,y,z" per line under a [name] header. Routes are keyed by name (case-insensitive) so multiple
    /// NPCs can share one route. Mirrors the per-profile cfg manager pattern (TraderProfileManager).
    /// </summary>
    public static class PatrolRouteManager
    {
        private static readonly Dictionary<string, PatrolRoute> _routes =
            new Dictionary<string, PatrolRoute>(StringComparer.OrdinalIgnoreCase);
        private static bool _initialized;

        private static string RoutesFolder =>
            Path.Combine(BepInEx.Paths.ConfigPath, "FiresNPCs_PatrolRoutes");

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            EnsureFolderExists();
            LoadAll();
        }

        public static void Reset() { _routes.Clear(); _initialized = false; }

        public static PatrolRoute GetRoute(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (!_initialized) Initialize();
            return _routes.TryGetValue(name, out var r) ? r : null;
        }

        public static IEnumerable<string> GetAllRouteNames()
        {
            if (!_initialized) Initialize();
            return _routes.Keys.ToList();
        }

        /// <summary>Writes a route to disk and reloads the cache from disk (so the round-trip is the source of truth).</summary>
        public static void SaveRoute(string name, IList<Vector3> points)
        {
            if (string.IsNullOrEmpty(name) || points == null || points.Count < 2) return;
            EnsureFolderExists();

            var sb = new StringBuilder();
            sb.Append('[').Append(name).Append("]\n");
            foreach (var p in points)
                sb.Append(p.x.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.y.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                  .Append(p.z.ToString("F2", CultureInfo.InvariantCulture)).Append('\n');

            try { File.WriteAllText(Path.Combine(RoutesFolder, MakeSafeFileName(name) + ".cfg"), sb.ToString()); }
            catch (Exception ex) { Debug.LogWarning($"[PatrolRoute] save '{name}' failed: {ex.Message}"); return; }

            Reset();
            Initialize();
            Debug.Log($"[PatrolRoute] saved route '{name}' ({points.Count} points)");
        }

        /// <summary>Deletes a route's cfg from disk and drops it from the cache. Returns true if a file was removed.</summary>
        public static bool DeleteRoute(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (!_initialized) Initialize();
            try
            {
                var path = Path.Combine(RoutesFolder, MakeSafeFileName(name) + ".cfg");
                bool existed = File.Exists(path);
                if (existed) File.Delete(path);
                _routes.Remove(name);
                if (existed) Debug.Log($"[PatrolRoute] deleted route '{name}'");
                return existed;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PatrolRoute] delete '{name}' failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Writes the raw cfg text directly (used by the server RPC handler when a client sends a recording).</summary>
        public static void WriteRawAndReload(string name, string cfgContents)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(cfgContents)) return;
            EnsureFolderExists();
            try { File.WriteAllText(Path.Combine(RoutesFolder, MakeSafeFileName(name) + ".cfg"), cfgContents); }
            catch (Exception ex) { Debug.LogWarning($"[PatrolRoute] write raw '{name}' failed: {ex.Message}"); return; }
            Reset();
            Initialize();
        }

        private static void LoadAll()
        {
            try
            {
                foreach (var file in Directory.GetFiles(RoutesFolder, "*.cfg"))
                    ParseRouteFile(File.ReadAllLines(file), Path.GetFileName(file));
            }
            catch (Exception ex) { Debug.LogWarning($"[PatrolRoute] load failed: {ex.Message}"); }
        }

        private static void ParseRouteFile(string[] lines, string fileName)
        {
            var route = new PatrolRoute();
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == '[' && line.EndsWith("]")) { route.Name = line.Substring(1, line.Length - 2); continue; }
                var c = line.Split(',');
                if (c.Length < 3) continue;
                if (float.TryParse(c[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                    float.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                    float.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                    route.Points.Add(new Vector3(x, y, z));
            }
            if (string.IsNullOrEmpty(route.Name)) route.Name = Path.GetFileNameWithoutExtension(fileName);
            if (route.Points.Count >= 2) { route.DetectLoop(); _routes[route.Name] = route; }
        }

        private static void EnsureFolderExists()
        {
            try { if (!Directory.Exists(RoutesFolder)) Directory.CreateDirectory(RoutesFolder); }
            catch (Exception ex) { Debug.LogWarning($"[PatrolRoute] create folder failed: {ex.Message}"); }
        }

        private static string MakeSafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var ch in name.Trim()) sb.Append(invalid.Contains(ch) ? '_' : ch);
            return sb.Length > 0 ? sb.ToString() : "Route";
        }
    }
}
