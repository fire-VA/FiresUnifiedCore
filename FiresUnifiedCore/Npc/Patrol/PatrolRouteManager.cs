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
    /// Loads/saves NPC patrol routes to BepInEx/config/FiresRPGmaker/PatrolRoutes/&lt;name&gt;.cfg — one
    /// "x,y,z" per line under a [name] header. Routes are keyed by name (case-insensitive) so multiple
    /// NPCs can share one route. Mirrors the per-profile cfg manager pattern (TraderProfileManager).
    /// </summary>
    public static class PatrolRouteManager
    {
        private static readonly Dictionary<string, PatrolRoute> _routes =
            new Dictionary<string, PatrolRoute>(StringComparer.OrdinalIgnoreCase);
        private static bool _initialized;

        // Resolved through the shared family config resolver (FiresRPGmaker/NPCs/PatrolRoutes). Legacy locations
        // are folded in once by FiresConfigPaths.Migrate() at Core startup.
        private static string RoutesFolder => FiresCore.Storage.FiresConfigPaths.PatrolRoutes;

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
            return _routes.TryGetValue(name, out var route) ? route : null;
        }

        public static IEnumerable<string> GetAllRouteNames()
        {
            if (!_initialized) Initialize();
            return _routes.Keys.ToList();
        }

        /// <summary>
        /// Writes a route's geometry to disk and reloads the cache from disk (so the round-trip is the
        /// source of truth). Any existing DBSM presets on the route are PRESERVED — this overload only
        /// rewrites the point block, so node edits (add/move/delete via the context menu) never wipe the
        /// speed presets. Use <see cref="SaveRouteFull"/> to persist preset edits.
        /// </summary>
        public static void SaveRoute(string name, IList<Vector3> points)
        {
            if (string.IsNullOrEmpty(name) || points == null || points.Count < 2) return;
            var existing = GetRoute(name);
            WriteAndReload(name, points, existing?.Presets, existing);
        }

        /// <summary>Persists a route's full state (points + DBSM presets + follow tuning + must-hit). Used by the preset/section editor.</summary>
        public static void SaveRouteFull(PatrolRoute route)
        {
            if (route == null || route.Points.Count < 2) return;
            WriteAndReload(route.Name, route.Points, route.Presets, route);
        }

        /// <summary>Persists explicit points + presets together — used when a node edit must also fix-up preset indices. Preserves the route's follow tuning + must-hit from the current cache.</summary>
        public static void SaveRoute(string name, IList<Vector3> points, IList<SpeedPreset> presets)
        {
            if (string.IsNullOrEmpty(name) || points == null || points.Count < 2) return;
            WriteAndReload(name, points, presets, GetRoute(name));
        }

        // <paramref name="meta"/> carries the route-level follow tuning (ArrivalRadius / LookAhead / Smoothing /
        // MustHit) to serialize; null writes defaults. Points + presets are written explicitly so a node edit
        // that clones them still round-trips.
        private static void WriteAndReload(string name, IList<Vector3> points, IList<SpeedPreset> presets, PatrolRoute meta)
        {
            EnsureFolderExists();
            var text = SerializeRoute(name, points, presets, meta);
            try { File.WriteAllText(Path.Combine(RoutesFolder, MakeSafeFileName(name) + ".cfg"), text); }
            catch (Exception ex) { Debug.LogWarning($"[PatrolRoute] save '{name}' failed: {ex.Message}"); return; }

            Reset();
            Initialize();
            Debug.Log($"[PatrolRoute] saved route '{name}' ({points.Count} points, {presets?.Count ?? 0} presets)");
        }

        // XYZ point block FIRST (backward-compatible: the old parser and any tool reading only points still
        // works), then route-level "key=value" follow-tuning lines (points never contain '=', so they're
        // unambiguous), then a [preset:Name] block per DBSM speed preset. See ParseRouteFile for the round-trip.
        private static string SerializeRoute(string name, IList<Vector3> points, IList<SpeedPreset> presets, PatrolRoute meta)
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(name).Append("]\n");
            foreach (var point in points)
                sb.Append(F2(point.x)).Append(',').Append(F2(point.y)).Append(',').Append(F2(point.z)).Append('\n');

            if (meta != null)
            {
                sb.Append("arrivalRadius=").Append(N(meta.ArrivalRadius)).Append('\n');
                sb.Append("lookAhead=").Append(N(meta.LookAhead)).Append('\n');
                sb.Append("smoothing=").Append(N(meta.Smoothing)).Append('\n');
                if (meta.MustHit.Count > 0)
                {
                    var idx = new List<int>(meta.MustHit); idx.Sort();
                    sb.Append("mustHit=").Append(string.Join(",", idx.Select(i => i.ToString(CultureInfo.InvariantCulture)))).Append('\n');
                }
            }

            if (presets != null)
                foreach (var preset in presets)
                    AppendPreset(sb, preset);
            return sb.ToString();
        }

        private static void AppendPreset(StringBuilder sb, SpeedPreset preset)
        {
            if (preset == null || string.IsNullOrEmpty(preset.Name)) return;
            sb.Append("[preset:").Append(preset.Name).Append("]\n");
            sb.Append("runThreshold=").Append(N(preset.RunThreshold)).Append('\n');
            foreach (var section in preset.Sections)
            {
                sb.Append("section=").Append(section.Low).Append(',').Append(section.High)
                  .Append(",peak=").Append(N(section.Peak))
                  .Append(",floor=").Append(N(section.Floor))
                  .Append(",shape=").Append(section.Shape.ToString())
                  .Append(",k=").Append(N(section.ShapeK));
                if (section.Shape == SpeedShape.Custom && section.CustomCurve != null && section.CustomCurve.Count > 0)
                {
                    sb.Append(",curve=");
                    for (int i = 0; i < section.CustomCurve.Count; i++)
                    {
                        if (i > 0) sb.Append('|');
                        sb.Append(N(section.CustomCurve[i].x)).Append(':').Append(N(section.CustomCurve[i].y));
                    }
                }
                sb.Append('\n');
            }
            foreach (var stop in preset.StopPoints)
                sb.Append("stop=").Append(stop.Index).Append(",dur=").Append(N(stop.Duration)).Append('\n');
        }

        private static string F2(float v) => v.ToString("F2", CultureInfo.InvariantCulture);
        private static string N(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

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
            SpeedPreset currentPreset = null;   // non-null once we've entered a [preset:Name] block
            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;

                if (line[0] == '[' && line.EndsWith("]"))
                {
                    var inner = line.Substring(1, line.Length - 2);
                    if (inner.StartsWith("preset:", StringComparison.OrdinalIgnoreCase))
                    {
                        currentPreset = new SpeedPreset { Name = inner.Substring("preset:".Length).Trim() };
                        if (!string.IsNullOrEmpty(currentPreset.Name)) route.Presets.Add(currentPreset);
                    }
                    else { route.Name = inner; currentPreset = null; }
                    continue;
                }

                // Inside a preset block, every non-header line is preset body (section= / stop= / runThreshold=).
                if (currentPreset != null) { ParsePresetLine(currentPreset, line); continue; }

                // Before any preset block a "key=value" line is route-level follow tuning; points never contain '='.
                if (line.IndexOf('=') > 0) { ParseRouteMetaLine(route, line); continue; }

                // Otherwise it's an XYZ point line (the block that stays first — unchanged format).
                var parts = line.Split(',');
                if (parts.Length < 3) continue;
                if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                    route.Points.Add(new Vector3(x, y, z));
            }
            if (string.IsNullOrEmpty(route.Name)) route.Name = Path.GetFileNameWithoutExtension(fileName);
            if (route.Points.Count >= 2) { route.DetectLoop(); route.ClampMustHit(route.Points.Count); _routes[route.Name] = route; }
        }

        // Route-level follow tuning ("arrivalRadius=" / "lookAhead=" / "smoothing=" / "mustHit=2,5,7"). Values are
        // clamped to sane bounds so a hand-edited or corrupt cfg can't break the follower.
        private static void ParseRouteMetaLine(PatrolRoute route, string line)
        {
            int equalsIndex = line.IndexOf('=');
            string key = line.Substring(0, equalsIndex).Trim();
            string val = line.Substring(equalsIndex + 1).Trim();

            if (key.Equals("arrivalRadius", StringComparison.OrdinalIgnoreCase))
            {
                if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) route.ArrivalRadius = Mathf.Clamp(parsed, 0.4f, 12f);
            }
            else if (key.Equals("lookAhead", StringComparison.OrdinalIgnoreCase))
            {
                if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) route.LookAhead = Mathf.Clamp(parsed, 0.5f, 30f);
            }
            else if (key.Equals("smoothing", StringComparison.OrdinalIgnoreCase))
            {
                if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) route.Smoothing = Mathf.Clamp01(parsed);
            }
            else if (key.Equals("mustHit", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var tok in val.Split(','))
                    if (int.TryParse(tok.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0) route.MustHit.Add(i);
            }
        }

        private static void ParsePresetLine(SpeedPreset preset, string line)
        {
            int equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0) return;
            string key = line.Substring(0, equalsIndex).Trim();
            string val = line.Substring(equalsIndex + 1).Trim();

            if (key.Equals("runThreshold", StringComparison.OrdinalIgnoreCase))
            {
                if (float.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var runThreshold))
                    preset.RunThreshold = runThreshold;
            }
            else if (key.Equals("section", StringComparison.OrdinalIgnoreCase))
            {
                var section = ParseSection(val);
                if (section != null) preset.Sections.Add(section);
            }
            else if (key.Equals("stop", StringComparison.OrdinalIgnoreCase))
            {
                var stop = ParseStop(val);
                if (stop != null) preset.StopPoints.Add(stop);
            }
        }

        // "start,end,peak=..,floor=..,shape=..,k=..[,curve=u:sp|u:sp|..]" — first two tokens are the index span,
        // the rest are key=value. curve uses '|'/':' (no commas) so a plain comma-split is safe.
        private static Section ParseSection(string val)
        {
            var parts = val.Split(',');
            if (parts.Length < 2) return null;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var start)) return null;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var end)) return null;

            var section = new Section { StartIndex = start, EndIndex = end };
            for (int i = 2; i < parts.Length; i++)
            {
                var pair = parts[i].Split(new[] { '=' }, 2);
                if (pair.Length != 2) continue;
                string key = pair[0].Trim(), valueText = pair[1].Trim();
                switch (key.ToLowerInvariant())
                {
                    case "peak":  if (float.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var peak)) section.Peak = peak; break;
                    case "floor": if (float.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var floor)) section.Floor = floor; break;
                    case "shape": if (Enum.TryParse(valueText, true, out SpeedShape shape)) section.Shape = shape; break;
                    case "k":     if (float.TryParse(valueText, NumberStyles.Float, CultureInfo.InvariantCulture, out var shapeK)) section.ShapeK = shapeK; break;
                    case "curve": section.CustomCurve = ParseCurve(valueText); break;
                }
            }
            return section;
        }

        private static List<Vector2> ParseCurve(string val)
        {
            var list = new List<Vector2>();
            foreach (var seg in val.Split('|'))
            {
                var parts = seg.Split(':');
                if (parts.Length != 2) continue;
                if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var u) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                    list.Add(new Vector2(u, speed));
            }
            list.Sort((a, b) => a.x.CompareTo(b.x));
            return list.Count > 0 ? list : null;
        }

        // "index,dur=seconds"
        private static StopPoint ParseStop(string val)
        {
            var parts = val.Split(',');
            if (parts.Length < 1) return null;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)) return null;
            var stop = new StopPoint { Index = idx };
            for (int i = 1; i < parts.Length; i++)
            {
                var pair = parts[i].Split(new[] { '=' }, 2);
                if (pair.Length == 2 && pair[0].Trim().Equals("dur", StringComparison.OrdinalIgnoreCase)
                    && float.TryParse(pair[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration))
                    stop.Duration = duration;
            }
            return stop;
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
