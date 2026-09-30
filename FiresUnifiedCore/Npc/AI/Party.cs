using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// The co-op party between two bodies driven by the one brain (Core 0.2.219, COOP_BOTS §7.2 / LONGFORM_COOP_STREAM): the regroup
    /// before heading home (wait up to 60 s for the partner to come within 15 m, meeting halfway) and the leash on trips. Each body
    /// runs it for itself with the other as its partner; nothing is networked, both read the same positions.
    /// </summary>
    public static class Party
    {
        public enum RegroupState { Together, Walking, GaveUp }

        /// <summary>The partner counts as met within this distance (m).</summary>
        public const float MeetMetres = 15f;
        /// <summary>A regroup gives up after this long (s).</summary>
        public const float RegroupSeconds = 60f;
        /// <summary>The default leash on trips (m).</summary>
        public const float LeashMetres = 40f;

        private sealed class Wait { public float Since; public string Why; }
        private static readonly Dictionary<Character, Wait> s_waits = new Dictionary<Character, Wait>();

        /// <summary>
        /// One tick of regrouping <paramref name="self"/> with <paramref name="partner"/> before <paramref name="why"/> (e.g. "going home
        /// (bag: 84 %)"). Together: within <paramref name="meet"/> m (the first tick after a wait logs "regrouped with … in N s");
        /// Walking: walk to <paramref name="meetPoint"/> (halfway, on the navmesh); GaveUp: no partner, or <paramref name="maxWait"/> s passed.
        /// </summary>
        public static RegroupState Regroup(Character self, Character partner, string why, out Vector3 meetPoint,
            float meet = MeetMetres, float maxWait = RegroupSeconds)
        {
            meetPoint = self != null ? self.transform.position : Vector3.zero;
            if (self == null) return RegroupState.GaveUp;
            if (partner == null || partner.IsDead())
            {
                Finish(self, $"trip: {why}; no partner to regroup with ({(partner == null ? "none" : partner.m_name + " is dead")})");
                return RegroupState.GaveUp;
            }
            Vector3 a = self.transform.position, b = partner.transform.position;
            float gap = Flat(a, b);
            if (!s_waits.TryGetValue(self, out Wait wait))
            {
                if (gap <= meet) return RegroupState.Together;
                wait = new Wait { Since = Time.time, Why = why };
                s_waits[self] = wait;
                Debug.Log($"[Party] {Name(self)}: regrouping with {Name(partner)} before {why} ({gap:0} m apart)");
            }
            float waited = Time.time - wait.Since;
            if (gap <= meet)
            {
                Finish(self, $"trip: {why}; regrouped with {Name(partner)} in {waited:0} s");
                return RegroupState.Together;
            }
            if (waited > maxWait)
            {
                Finish(self, $"trip: {why}; regroup with {Name(partner)} gave up after {maxWait:0} s ({gap:0} m apart)");
                return RegroupState.GaveUp;
            }
            Vector3 mid = Vector3.Lerp(a, b, 0.5f);
            meetPoint = Pathfinding.instance != null && Pathfinding.instance.FindValidPoint(out Vector3 stand, mid, 8f, Pathfinding.AgentType.Humanoid) ? stand : mid;
            return RegroupState.Walking;
        }

        /// <summary>Forget a regroup in progress (the trip was cancelled).</summary>
        public static void CancelRegroup(Character self)
        {
            if (self != null) s_waits.Remove(self);
        }

        /// <summary>
        /// True when <paramref name="self"/> is more than <paramref name="leash"/> m from <paramref name="partner"/>: head back toward it.
        /// Logged once per stretch apart ("leash: … 52 m from …, heading back").
        /// </summary>
        public static bool Leash(Character self, Character partner, out float distance, float leash = LeashMetres)
        {
            distance = 0f;
            if (self == null || partner == null || partner.IsDead()) return false;
            distance = Flat(self.transform.position, partner.transform.position);
            bool over = distance > leash;
            bool was = s_leashed.Contains(self);
            if (over && !was)
            {
                s_leashed.Add(self);
                Debug.Log($"[Party] {Name(self)}: leash: {distance:0} m from {Name(partner)} (leash {leash:0} m), heading back");
            }
            else if (!over && was && distance < leash * 0.75f) s_leashed.Remove(self);
            return over;
        }

        private static readonly HashSet<Character> s_leashed = new HashSet<Character>();

        private static void Finish(Character self, string line)
        {
            s_waits.Remove(self);
            Debug.Log($"[Party] {Name(self)}: {line}");
        }

        private static string Name(Character c) => c is Player p ? p.GetPlayerName() : c != null ? c.m_name : "?";

        private static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }

    /// <summary>
    /// The party's home: one named base point per world (Core 0.2.219, LONGFORM_COOP_STREAM "RETURN to base"), persisted on each body's
    /// client like ResourceMemory (BepInEx\config\FiresCore\WorldMemory\&lt;world&gt;_&lt;seed&gt;.base.json), and the long walk home:
    /// <see cref="NextLeg"/> hands the walker legs of at most <see cref="LegMetres"/> m, so each navmesh ask stays inside tiles that are
    /// built around it (vanilla bakes 32 m tiles lazily round the start and the goal).
    /// </summary>
    public static class HomeBase
    {
        /// <summary>The longest leg handed to the walker on the way home (m).</summary>
        public const float LegMetres = 48f;
        /// <summary>Within this distance (m) of the base point the body is home.</summary>
        public const float HomeMetres = 6f;

        private sealed class FileData
        {
            public string World, Name;
            public int Seed;
            public float X, Y, Z;
            public bool Set;
        }

        private static FileData s_data;
        private static string s_path;

        /// <summary>Set (and save) the base point <paramref name="pos"/> named <paramref name="name"/> for this world.</summary>
        public static void Set(Vector3 pos, string name = "base")
        {
            if (!EnsureLoaded()) return;
            s_data.X = pos.x;
            s_data.Y = pos.y;
            s_data.Z = pos.z;
            s_data.Name = string.IsNullOrEmpty(name) ? "base" : name;
            s_data.Set = true;
            Save();
            Debug.Log($"[HomeBase] base '{s_data.Name}' set at ({pos.x:0}, {pos.y:0}, {pos.z:0})");
        }

        /// <summary>This world's base point, if one was set.</summary>
        public static bool TryGet(out Vector3 pos, out string name)
        {
            pos = Vector3.zero;
            name = null;
            if (!EnsureLoaded() || !s_data.Set) return false;
            pos = new Vector3(s_data.X, s_data.Y, s_data.Z);
            name = s_data.Name;
            return true;
        }

        /// <summary>True within <see cref="HomeMetres"/> of the base point.</summary>
        public static bool IsHome(Vector3 at) => TryGet(out Vector3 home, out _) && Flat(at, home) <= HomeMetres;

        /// <summary>
        /// The next point to walk to on the way home from <paramref name="at"/>: the base itself within <see cref="LegMetres"/>, else a
        /// point that far along the straight line, moved onto the navmesh nearby. False when no base is set. The walker (PathWalker)
        /// finds the way to each leg's end; call again when it arrives.
        /// </summary>
        public static bool NextLeg(Vector3 at, out Vector3 legEnd, out float remaining)
        {
            legEnd = at;
            remaining = 0f;
            if (!TryGet(out Vector3 home, out string name)) return false;
            remaining = Flat(at, home);
            if (remaining <= LegMetres) { legEnd = home; return true; }
            Vector3 dir = home - at;
            dir.y = 0f;
            Vector3 p = at + dir.normalized * LegMetres;
            p.y = at.y;
            if (Pathfinding.instance != null)
            {
                // Prefer a point on the navmesh near the line; widen the search, then take the line point as it is.
                foreach (float r in new[] { 6f, 12f, 20f })
                    if (Pathfinding.instance.FindValidPoint(out Vector3 stand, p, r, Pathfinding.AgentType.Humanoid)) { p = stand; break; }
            }
            legEnd = p;
            if (Time.time >= s_legLogAt)
            {
                s_legLogAt = Time.time + 10f;
                Debug.Log($"[HomeBase] going home to '{name}': {remaining:0} m left; next leg to ({p.x:0}, {p.z:0})");
            }
            return true;
        }

        private static float s_legLogAt;

        private static void Save()
        {
            if (s_data == null || string.IsNullOrEmpty(s_path)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(s_path));
                string tmp = s_path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(s_data, Formatting.Indented));
                if (File.Exists(s_path)) File.Delete(s_path);
                File.Move(tmp, s_path);
            }
            catch (Exception ex) { Debug.LogWarning($"[HomeBase] could not write {s_path}: {ex.Message}"); }
        }

        private static bool EnsureLoaded()
        {
            global::World world = WorldGenerator.instance != null ? WorldGenerator.instance.m_world : null;
            if (world == null) return false;
            string safe = world.m_name ?? "world";
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            string path = Path.Combine(BepInEx.Paths.ConfigPath, "FiresCore", "WorldMemory", $"{safe}_{world.m_seed}.base.json");
            if (s_data != null && path == s_path) return true;
            s_path = path;
            s_data = null;
            try
            {
                if (File.Exists(path)) s_data = JsonConvert.DeserializeObject<FileData>(File.ReadAllText(path));
            }
            catch (Exception ex) { Debug.LogWarning($"[HomeBase] could not read {path} ({ex.Message})"); }
            if (s_data == null) s_data = new FileData { World = world.m_name, Seed = world.m_seed };
            return true;
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }
    }
}
