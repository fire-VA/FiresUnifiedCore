using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace FiresCore.World
{
    /// <summary>A remembered place where a kind of foe was met.</summary>
    public readonly struct EnemySpot
    {
        public readonly string Prefab;
        public readonly int Level;
        public readonly Vector3 At;
        public readonly Heightmap.Biome Biome;
        public readonly string Place;
        public readonly int Sightings, MaxGroup, Fights, Won, Fled, Died;
        public readonly bool SeenAtNight;
        public readonly double LastSeenGameTime;
        /// <summary>Flat distance from the search point (m).</summary>
        public readonly float Distance;

        internal EnemySpot(EnemyMemory.Entry e, float distance)
        {
            Prefab = e.Prefab;
            Level = e.Level;
            At = new Vector3(e.X, e.Y, e.Z);
            Biome = (Heightmap.Biome)e.Biome;
            Place = e.Place;
            Sightings = e.Sightings;
            MaxGroup = e.MaxGroup;
            Fights = e.Fights;
            Won = e.Won;
            Fled = e.Fled;
            Died = e.Died;
            SeenAtNight = e.Night > 0;
            LastSeenGameTime = e.LastSeen;
            Distance = distance;
        }
    }

    /// <summary>How a fight ended for the body that recorded it.</summary>
    public enum FightOutcome { Won, Fled, Died }

    /// <summary>
    /// Who is where (Core 0.2.219, AUTOPLAY_PROGRESSION §7.13, COOP_BOTS §8): every foe seen or fought, by prefab, star level and place
    /// (32 m spots), with the time of day, group size and fight outcomes. It is ResourceMemory's twin and persists the same way, per
    /// world on the recording body's own client: BepInEx\config\FiresCore\WorldMemory\&lt;world&gt;_&lt;seed&gt;.enemies.json. Fights also
    /// feed the shared <see cref="Danger"/> store.
    /// </summary>
    public static class EnemyMemory
    {
        /// <summary>Meetings of the same foe kind and level within this distance (m) are one spot.</summary>
        public const float MergeMetres = 32f;
        private const float FlushSeconds = 30f;
        private const int FileVersion = 1;

        internal sealed class Entry
        {
            public string Prefab, Place;
            public int Level, Biome, Sightings, MaxGroup, Fights, Won, Fled, Died, Night;
            public float X, Y, Z, DamageTakenPerSec, HitsToKill;
            public double LastSeen;
        }

        private sealed class FileData
        {
            public int Version = FileVersion;
            public string World;
            public int Seed;
            public List<Entry> Enemies = new List<Entry>();
            public List<Danger.Entry> Danger = new List<Danger.Entry>();
        }

        private static FileData s_data;
        private static string s_path;
        private static bool s_dirty;
        private static float s_nextFlush;

        /// <summary>
        /// A foe seen: <paramref name="count"/> of <paramref name="prefab"/> (★<paramref name="level"/>-1) at <paramref name="pos"/>, in
        /// <paramref name="place"/> (a location or dungeon room name, or null).
        /// </summary>
        public static void Sighted(string prefab, int level, Vector3 pos, int count = 1, string place = null)
        {
            if (string.IsNullOrEmpty(prefab) || !EnsureLoaded()) return;
            Entry e = Spot(prefab, level, pos, place);
            e.Sightings++;
            e.MaxGroup = Mathf.Max(e.MaxGroup, count);
            if (IsNight()) e.Night++;
            e.LastSeen = GameTime();
            if (e.Sightings == 1)
                Debug.Log($"[EnemyMemory] seen {prefab} {Stars(level)} x{count} at ({pos.x:0}, {pos.z:0}) ({(Heightmap.Biome)e.Biome}{(place != null ? ", " + place : "")}{(IsNight() ? ", night" : "")})");
            MarkDirty();
        }

        /// <summary>
        /// A fight with <paramref name="prefab"/> ended: its outcome, the damage per second taken, the hits the kill took (0 when not
        /// killed), and the lowest health share the body had. Also writes the <see cref="Danger"/> store.
        /// </summary>
        public static void Fought(string prefab, int level, Vector3 pos, FightOutcome outcome, float damageTakenPerSec = 0f, int hitsToKill = 0,
            float lowestHealthShare = 1f, string byWhom = null, string place = null)
        {
            if (string.IsNullOrEmpty(prefab) || !EnsureLoaded()) return;
            Entry e = Spot(prefab, level, pos, place);
            e.Fights++;
            if (outcome == FightOutcome.Won) e.Won++;
            else if (outcome == FightOutcome.Fled) e.Fled++;
            else e.Died++;
            e.DamageTakenPerSec = e.Fights == 1 ? damageTakenPerSec : Mathf.Lerp(e.DamageTakenPerSec, damageTakenPerSec, 0.3f);
            if (hitsToKill > 0) e.HitsToKill = e.HitsToKill <= 0f ? hitsToKill : Mathf.Lerp(e.HitsToKill, hitsToKill, 0.3f);
            e.LastSeen = GameTime();
            Debug.Log($"[EnemyMemory] foe: {prefab} {Stars(level)} at ({pos.x:0}, {pos.z:0}){(place != null ? " in " + place : "")}{(IsNight() ? " (night)" : "")}: " +
                      $"{outcome}, took {damageTakenPerSec:0} dmg/s{(hitsToKill > 0 ? $", {hitsToKill} hits/kill" : "")}{(byWhom != null ? $" ({byWhom})" : "")}");
            if (outcome == FightOutcome.Died) Danger.Record(prefab, level, DangerEvent.Died, 1f, pos, byWhom);
            else if (lowestHealthShare < Danger.NearDeathShare) Danger.Record(prefab, level, DangerEvent.NearDeath, 1f - lowestHealthShare, pos, byWhom);
            MarkDirty();
        }

        private static readonly Dictionary<string, float> s_lastDeath = new Dictionary<string, float>();

        /// <summary>
        /// A body killed by <paramref name="killer"/> (0.2.221, Core's own death record; R90 run 4: the bot's death to a Skeleton ★2
        /// was never written, so nothing learned from it): the foe's spot gains a fight lost to death, and World.Danger a death with
        /// the killing hit's share of <paramref name="maxHealth"/> (≥ 1 from full health is a one-shot). Once per killer and victim
        /// within a few seconds, whoever else reports the same death.
        /// </summary>
        public static void Died(Character victim, Character killer, float hitDamage, float maxHealth, float healthBefore)
        {
            if (victim == null || killer == null || !EnsureLoaded()) return;
            string prefab = Utils.GetPrefabName(killer.gameObject);
            int level = killer.GetLevel();
            string who = victim is Player p ? p.GetPlayerName() : victim.m_name;
            string key = prefab + ":" + level + ":" + who;
            if (s_lastDeath.TryGetValue(key, out float at) && Time.time - at < 5f) return;
            s_lastDeath[key] = Time.time;
            Vector3 pos = killer.transform.position;
            Entry e = Spot(prefab, level, pos, null);
            e.Fights++;
            e.Died++;
            e.LastSeen = GameTime();
            float share = maxHealth > 0f ? hitDamage / maxHealth : 1f;
            bool oneShot = maxHealth > 0f && healthBefore >= maxHealth * 0.99f && hitDamage >= healthBefore;
            Debug.Log($"[EnemyMemory] foe: {prefab} {Stars(level)} at ({pos.x:0}, {pos.z:0}){(IsNight() ? " (night)" : "")}: Died, " +
                      $"last hit {hitDamage:0} vs hp {healthBefore:0} of {maxHealth:0}{(oneShot ? " (one-shot)" : "")} ({who})");
            Danger.Record(prefab, level, DangerEvent.Died, oneShot ? 1f : Mathf.Min(0.99f, share), victim.transform.position, who);
            Danger.NoteHit(prefab, level, hitDamage);
            MarkDirty();
        }

        /// <summary>
        /// How dangerous the ground round <paramref name="at"/> is, 0..1 (0.2.221; for the grave walk, trip targets and the haul): the
        /// worst remembered foe spot within <paramref name="radius"/> m by World.Danger's level for its kind★, at least 0.6 where that
        /// kind killed a body. <paramref name="why"/> names it, and says when one of that kind is there right now.
        /// </summary>
        public static float DangerAt(Vector3 at, float radius, out string why) => DangerAt(at, radius, out why, out _);

        /// <summary>
        /// <see cref="DangerAt(Vector3, float, out string)"/> with <paramref name="liveNow"/>: how many of the worst spot's kind★ are within
        /// <paramref name="radius"/> + 10 m of <paramref name="at"/> right now (0.2.222, [seasons]: so callers don't parse <paramref name="why"/>).
        /// </summary>
        public static float DangerAt(Vector3 at, float radius, out string why, out int liveNow)
        {
            why = "nothing remembered";
            liveNow = 0;
            if (!EnsureLoaded()) return 0f;
            float worst = 0f;
            Entry worstSpot = null;
            foreach (Entry e in s_data.Enemies)
            {
                float d = Vector2.Distance(new Vector2(e.X, e.Z), new Vector2(at.x, at.z));
                if (d > radius) continue;
                float level = Danger.Level(e.Prefab, e.Level, out _);
                if (e.Died > 0) level = Mathf.Max(level, 0.6f);
                if (level > worst) { worst = level; worstSpot = e; }
            }
            if (worstSpot == null) return 0f;
            int live = 0;
            foreach (Character c in Character.GetAllCharacters())
                if (c != null && !c.IsDead() && c.GetLevel() == worstSpot.Level && Utils.GetPrefabName(c.gameObject) == worstSpot.Prefab
                    && Vector3.Distance(c.transform.position, at) <= radius + 10f) live++;
            why = $"{worstSpot.Prefab} {Stars(worstSpot.Level)} at ({worstSpot.X:0}, {worstSpot.Z:0}): {worstSpot.Died} death(s), {worstSpot.Fights} fight(s), danger {worst:0.00}" +
                  (live > 0 ? $"; {live} there now" : "; none in sight now");
            liveNow = live;
            return worst;
        }

        /// <summary>Known spots of <paramref name="prefab"/> (null: any foe) within <paramref name="leash"/> m of <paramref name="from"/>, nearest first.</summary>
        public static List<EnemySpot> Search(string prefab, Vector3 from, float leash = float.PositiveInfinity, int max = 20)
        {
            var found = new List<EnemySpot>();
            if (!EnsureLoaded()) return found;
            foreach (Entry e in s_data.Enemies)
            {
                if (prefab != null && e.Prefab != prefab) continue;
                float d = Flat(from, e.X, e.Z);
                if (d > leash) continue;
                found.Add(new EnemySpot(e, d));
            }
            found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            if (found.Count > max) found.RemoveRange(max, found.Count - max);
            return found;
        }

        /// <summary>A place's pattern for the log or a planner: "Crypt4: Skeleton x8 (★0-1), Ghost x2; night: Skeleton".</summary>
        public static string Pattern(Vector3 at, float radius = MergeMetres)
        {
            List<EnemySpot> near = Search(null, at, radius, 50);
            if (near.Count == 0) return "no foes remembered here";
            var parts = new List<string>();
            foreach (EnemySpot s in near)
                parts.Add($"{s.Prefab} x{s.MaxGroup} ({Stars(s.Level)}{(s.SeenAtNight ? ", night" : "")}; {s.Won}W {s.Fled}F {s.Died}D)");
            return string.Join(", ", parts);
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
            catch (Exception ex) { Debug.LogWarning($"[EnemyMemory] could not write {s_path}: {ex.Message}"); }
        }

        // The Danger store keeps its records in this file (one world memory file for who is where and how dangerous).
        internal static List<Danger.Entry> DangerRecords() => EnsureLoaded() ? s_data.Danger : null;

        internal static void MarkDirty()
        {
            s_dirty = true;
            if (Time.realtimeSinceStartup >= s_nextFlush)
            {
                s_nextFlush = Time.realtimeSinceStartup + FlushSeconds;
                Flush();
            }
        }

        private static Entry Spot(string prefab, int level, Vector3 pos, string place)
        {
            Entry best = null;
            float bestD = MergeMetres;
            foreach (Entry e in s_data.Enemies)
            {
                if (e.Prefab != prefab || e.Level != level) continue;
                float d = Flat(pos, e.X, e.Z);
                if (d <= bestD) { bestD = d; best = e; }
            }
            if (best == null)
            {
                best = new Entry
                {
                    Prefab = prefab, Level = level, X = pos.x, Y = pos.y, Z = pos.z, Place = place,
                    Biome = WorldGenerator.instance != null ? (int)WorldGenerator.instance.GetBiome(pos.x, pos.z) : 0,
                };
                s_data.Enemies.Add(best);
            }
            else if (place != null) best.Place = place;
            return best;
        }

        private static bool EnsureLoaded()
        {
            global::World world = WorldGenerator.instance != null ? WorldGenerator.instance.m_world : null;
            if (world == null) return false;
            string path = Path.Combine(BepInEx.Paths.ConfigPath, "FiresCore", "WorldMemory", $"{Safe(world.m_name)}_{world.m_seed}.enemies.json");
            if (s_data != null && path == s_path) return true;
            Flush();
            s_path = path;
            s_data = null;
            s_dirty = false;
            try
            {
                if (File.Exists(path)) s_data = JsonConvert.DeserializeObject<FileData>(File.ReadAllText(path));
            }
            catch (Exception ex) { Debug.LogWarning($"[EnemyMemory] could not read {path} ({ex.Message}); starting empty"); }
            if (s_data == null) s_data = new FileData { World = world.m_name, Seed = world.m_seed };
            if (s_data.Enemies == null) s_data.Enemies = new List<Entry>();
            if (s_data.Danger == null) s_data.Danger = new List<Danger.Entry>();
            Debug.Log($"[EnemyMemory] loaded {s_data.Enemies.Count} foe spot(s) and {s_data.Danger.Count} danger record(s) for {world.m_name} (seed {world.m_seed})");
            return true;
        }

        internal static string Stars(int level) => level <= 1 ? "★0" : $"★{level - 1}";

        private static bool IsNight() => EnvMan.instance != null && EnvMan.IsNight();

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
    }

    /// <summary>What a danger event was.</summary>
    public enum DangerEvent { Hit, NearDeath, Died }

    /// <summary>
    /// How dangerous each foe kind is, ONE store for the world (Core 0.2.219, AUTOPLAY_PROGRESSION §7.13 1b): deaths, near-deaths,
    /// one-shots and the biggest hit, per prefab and star level, written by every body (bot, tester, companions) and read the same way.
    /// Kept in the world's enemies file (<see cref="EnemyMemory"/>). Core owns the flee curve: <see cref="FleeBelow"/>.
    /// </summary>
    public static class Danger
    {
        /// <summary>A fight whose lowest health share fell under this is a near-death.</summary>
        public const float NearDeathShare = 0.2f;
        /// <summary>The flee curve: 25 % base, rising to 50 % at danger ≥ 0.6.</summary>
        public const float FleeBase = 0.25f, FleeTop = 0.5f, FleeTopDanger = 0.6f;

        public sealed class Entry
        {
            public string Prefab;
            public int Level, Deaths, NearDeaths, OneShots, Hits;
            public float BiggestHitShare;
            /// <summary>The biggest hit it landed, in health (0.2.221; 0 in older records): compared with a body's health today, so a
            /// foe that one-shot a 25 hp body isn't a one-hit threat forever once the body has 100.</summary>
            public float BiggestHit;
            public string LastBy;
        }

        /// <summary>
        /// A danger event: <paramref name="kind"/> from <paramref name="prefab"/> (★<paramref name="level"/>-1), with the share of the body's
        /// max health it took (<paramref name="damageShare"/>; ≥ 1 in one hit from full health is a one-shot), at <paramref name="pos"/>,
        /// suffered by <paramref name="byWhom"/>.
        /// </summary>
        public static void Record(string prefab, int level, DangerEvent kind, float damageShare, Vector3 pos, string byWhom = null)
        {
            if (string.IsNullOrEmpty(prefab)) return;
            List<Entry> records = EnemyMemory.DangerRecords();
            if (records == null) return;
            Entry r = Find(records, prefab, level);
            if (r == null) { r = new Entry { Prefab = prefab, Level = level }; records.Add(r); }
            switch (kind)
            {
                case DangerEvent.Died: r.Deaths++; break;
                case DangerEvent.NearDeath: r.NearDeaths++; break;
                default: r.Hits++; break;
            }
            if (kind == DangerEvent.Died && damageShare >= 1f) r.OneShots++;
            r.BiggestHitShare = Mathf.Max(r.BiggestHitShare, Mathf.Clamp01(damageShare));
            if (byWhom != null) r.LastBy = byWhom;
            if (kind != DangerEvent.Hit)
                Debug.Log($"[Danger] {prefab} {EnemyMemory.Stars(level)}: {kind}{(byWhom != null ? $" ({byWhom})" : "")} at ({pos.x:0}, {pos.z:0}) -> danger {Level(prefab, level, out _):0.00}");
            EnemyMemory.MarkDirty();
        }

        /// <summary>0..1: how dangerous <paramref name="prefab"/> at <paramref name="level"/> is in this world, with the why.</summary>
        public static float Level(string prefab, int level, out string why)
        {
            why = "no record";
            List<Entry> records = EnemyMemory.DangerRecords();
            if (records == null || string.IsNullOrEmpty(prefab)) return 0f;
            Entry r = Find(records, prefab, level);
            float scale = 1f;
            if (r == null)
            {
                // A missing star level borrows from its nearest neighbour, ×1.5 per star up (÷1.5 per star down).
                Entry near = null;
                int gap = int.MaxValue;
                foreach (Entry o in records)
                    if (o.Prefab == prefab && Mathf.Abs(o.Level - level) < gap) { gap = Mathf.Abs(o.Level - level); near = o; }
                if (near == null) return 0f;
                scale = Mathf.Pow(1.5f, level - near.Level);
                r = near;
            }
            float score = 0.35f * r.Deaths + 0.15f * r.NearDeaths + (r.OneShots > 0 ? 0.3f : 0f) + 0.3f * r.BiggestHitShare;
            score = Mathf.Clamp01(score * scale);
            why = $"{prefab}: {r.Deaths} death(s), {r.NearDeaths} near, one-shot {(r.OneShots > 0 ? "yes" : "no")}, biggest hit {r.BiggestHitShare:P0}" +
                  (scale != 1f ? $" (from {EnemyMemory.Stars(r.Level)} x{scale:0.##})" : "");
            return score;
        }

        /// <summary>
        /// The biggest hit <paramref name="prefab"/> ★<paramref name="level"/>-1 has landed in this world as a share of the body's max
        /// health: 1 when it has one-shot a body, 0 without a record (0.2.221; ThreatLevel weighs it as a learned one-hit threat).
        /// </summary>
        public static float BiggestHitShare(string prefab, int level)
        {
            List<Entry> records = EnemyMemory.DangerRecords();
            Entry r = records != null && !string.IsNullOrEmpty(prefab) ? Find(records, prefab, level) : null;
            if (r == null) return 0f;
            return r.OneShots > 0 ? 1f : r.BiggestHitShare;
        }

        /// <summary>The biggest hit (in health) <paramref name="prefab"/> ★<paramref name="level"/>-1 has landed in this world; 0 unknown.</summary>
        public static float BiggestHit(string prefab, int level)
        {
            List<Entry> records = EnemyMemory.DangerRecords();
            Entry r = records != null && !string.IsNullOrEmpty(prefab) ? Find(records, prefab, level) : null;
            return r != null ? r.BiggestHit : 0f;
        }

        // A landed hit's size (0.2.221): kept as the biggest for its kind★.
        internal static void NoteHit(string prefab, int level, float damage)
        {
            List<Entry> records = EnemyMemory.DangerRecords();
            if (records == null || string.IsNullOrEmpty(prefab) || damage <= 0f) return;
            Entry r = Find(records, prefab, level);
            if (r == null) { r = new Entry { Prefab = prefab, Level = level }; records.Add(r); }
            if (damage > r.BiggestHit) { r.BiggestHit = damage; EnemyMemory.MarkDirty(); }
        }

        /// <summary>The health share under which a body should flee <paramref name="prefab"/>: 25 %, rising to 50 % at danger ≥ 0.6.</summary>
        public static float FleeBelow(string prefab, int level)
        {
            float d = Level(prefab, level, out _);
            return Mathf.Lerp(FleeBase, FleeTop, Mathf.Clamp01(d / FleeTopDanger));
        }

        private static Entry Find(List<Entry> records, string prefab, int level)
        {
            foreach (Entry r in records)
                if (r.Prefab == prefab && r.Level == level) return r;
            return null;
        }
    }
}
