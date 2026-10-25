using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Npc.JobBoard
{
    /// <summary>
    /// The Homestead Job Board's model and scheduler (Fire 10-24, mockup Tools\mockups\jobboard_20261024 approved). One implementation:
    /// every job runs an existing idle sub-behavior (CompanionIdleBehavior.SubBehaviors), and assigning a named companion writes the same
    /// per-companion chore toggle (CompanionBehaviorToggles) the companion radial writes, so the radial and the board always agree.
    ///   Daily jobs repeat at a time of day; One-off jobs run once at a time and then drop off; Now jobs run at once and drop off.
    ///   Trip jobs (hunt any / hunt a species / fish / mine) carry a center, a radius (30-100 m) and 1-3 companions (TripBehavior).
    ///   A job whose base requirements aren't met (a tool, a station, rod and bait) still shows, greyed, with what is missing.
    /// Jobs are this player's, saved per world under BepInEx\config\FiresUnifiedCore\JobBoard; they run on this client, where the
    /// player's companions are simulated.
    /// </summary>
    public static class JobBoard
    {
        public enum Kind { Farm, Wood, MineHome, Smelt, Cook, Fires, Organize, Repair, FishHome, Craft, HuntTrip, FishTrip, MineTrip }
        public enum Repeat { Daily, OneOff, Now }

        public sealed class Job
        {
            public int Id;
            public Kind Kind;
            public Repeat Repeat;
            public int Minute = 7 * 60;                 // time of day (in-game minutes after midnight)
            public List<string> Assigned = new List<string>();   // companion ids; empty = Anyone
            public int Count = 1;                       // trips: 1-3 companions
            public Vector3 Center;                      // trips
            public float Radius = 60f;                  // trips: 30-100 m
            public string Prey = "";                    // hunt trips: prefab, "" = any game
            public int LastRunDay = -1, DueDay = -1;
            public bool Started;
            public float DoneAt = -1f;
            public string LastResult = "";
            public readonly List<string> Running = new List<string>();   // companion ids started by the last run
            public bool IsTrip => Kind == Kind.HuntTrip || Kind == Kind.FishTrip || Kind == Kind.MineTrip;
        }

        public static readonly List<Job> Jobs = new List<Job>();
        public static event Action Changed;
        private static string s_file;
        private static int s_nextId = 1;
        private const float BaseRadius = 60f, ChestRadius = 30f, DoneLinger = 90f;

        // ---- labels / skills -----------------------------------------------------------------------------------------
        public static string Label(Job j)
        {
            switch (j.Kind)
            {
                case Kind.Farm: return "Tend the farm";
                case Kind.Wood: return "Cut wood";
                case Kind.MineHome: return "Mine nearby ore";
                case Kind.Smelt: return "Run the smelters";
                case Kind.Cook: return "Cook food";
                case Kind.Fires: return "Tend the fires";
                case Kind.Organize: return "Organise the chests";
                case Kind.Repair: return "Repair the base";
                case Kind.FishHome: return "Fish near home";
                case Kind.Craft: return "Upgrade gear";
                case Kind.HuntTrip: return "Hunting trip: " + (string.IsNullOrEmpty(j.Prey) ? "any game" : PreyName(j.Prey));
                case Kind.FishTrip: return "Fishing trip";
                case Kind.MineTrip: return "Mining trip";
            }
            return j.Kind.ToString();
        }

        public static string Detail(Job j) => j.IsTrip
            ? $"Center ({j.Center.x:0}, {j.Center.y:0}, {j.Center.z:0}) · {j.Radius:0} m · {j.Count} companion{(j.Count > 1 ? "s" : "")}"
            : ChoreDetail(j.Kind);

        private static string ChoreDetail(Kind k)
        {
            switch (k)
            {
                case Kind.Farm: return "Harvest, replant and tend the beds";
                case Kind.Wood: return "Fell trees near base, wood to the chests";
                case Kind.MineHome: return "Ore deposits near base";
                case Kind.Smelt: return "Fill smelters and kilns from the chests";
                case Kind.Cook: return "Cooking stations, raw food from the chests";
                case Kind.Fires: return "Keep fires fuelled";
                case Kind.Organize: return "Sort the chest cluster";
                case Kind.Repair: return "Damaged pieces at base";
                case Kind.FishHome: return "Water near home";
                case Kind.Craft: return "Upgrade gear at the workbench";
            }
            return "";
        }

        public static string PreyName(string prefab)
        {
            var go = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(prefab) : null;
            var c = go != null ? go.GetComponent<Character>() : null;
            return c != null && Localization.instance != null ? Localization.instance.Localize(c.m_name) : prefab;
        }

        /// <summary>The skill a job leans on (for "Anyone": the best at it goes first).</summary>
        public static Skills.SkillType SkillOf(Kind k)
        {
            switch (k)
            {
                case Kind.Farm: return Skills.SkillType.Farming;
                case Kind.Wood: return Skills.SkillType.WoodCutting;
                case Kind.MineHome: case Kind.MineTrip: return Skills.SkillType.Pickaxes;
                case Kind.FishHome: case Kind.FishTrip: return Skills.SkillType.Fishing;
                case Kind.HuntTrip: return Skills.SkillType.Bows;
                default: return Skills.SkillType.None;
            }
        }

        public static string Schedule(Job j) => j.Repeat == Repeat.Now ? "Now" : (j.Repeat == Repeat.Daily ? "Daily " : "One-off ") + Clock(j.Minute);
        public static string Clock(int minute) => $"{minute / 60 % 24:00}:{minute % 60:00}";

        // ---- base context --------------------------------------------------------------------------------------------
        public static Vector3 BaseCenter()
        {
            if (FiresCore.Npc.AI.HomeBase.TryGet(out var hb, out _)) return hb;
            return Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
        }

        public static List<CompanionController> CompanionsAtBase()
        {
            var p = Player.m_localPlayer;
            var b = BaseCenter();
            return CompanionController.AllCompanions.Where(c => c != null && p != null && c.isTamed && c.IsOwner(p)
                && Vector3.Distance(c.transform.position, b) <= BaseRadius).ToList();
        }

        public static string StateOf(CompanionController c)
        {
            var idle = c != null ? c.GetComponent<CompanionIdleBehavior>() : null;
            var sub = idle != null ? idle.ActiveSubBehavior : null;
            return sub == null ? "Idle" : sub.GetStatusDescription();
        }

        private static bool IsFree(CompanionController c)
        {
            var sub = c.GetComponent<CompanionIdleBehavior>()?.ActiveSubBehavior;
            return sub == null || (!sub.IsCommandInitiated && !(sub is TripBehavior));
        }

        // ---- requirements ("Needs: ...") -----------------------------------------------------------------------------
        /// <summary>What the base is missing for this job right now (empty = it can run).</summary>
        public static List<string> Missing(Job j)
        {
            var miss = new List<string>();
            var b = BaseCenter();
            var items = new List<ItemDrop.ItemData>();
            foreach (var c in CompanionsAtBase()) { var inv = c.GetInventory()?.GetStorageInventory(); if (inv != null) items.AddRange(inv.GetAllItems()); }
            foreach (var chest in ChestHelper.FindNearbyChests(b, ChestRadius)) { var inv = chest?.GetInventory(); if (inv != null) items.AddRange(inv.GetAllItems()); }
            bool Has(string prefab) => items.Any(i => i?.m_dropPrefab != null && i.m_dropPrefab.name == prefab);
            bool Tool(ResourceDataHelper.ToolType t) => ResourceDataHelper.BestToolTier(items, t) >= 0;
            bool Near<T>(float r) where T : Component => Physics.OverlapSphere(b, r).Any(col => col != null && col.GetComponentInParent<T>() != null);
            switch (j.Kind)
            {
                case Kind.Farm: if (!Has("Cultivator") && FiresCore.Npc.AI.ChoreBrain.CropsToHarvest(b, 30f, b, p => true).Count == 0) miss.Add("a Cultivator (or ripe crops to harvest)"); break;
                case Kind.Wood: if (!Tool(ResourceDataHelper.ToolType.Axe)) miss.Add("an axe in a chest or a companion's bag"); break;
                case Kind.MineHome: case Kind.MineTrip: if (!Tool(ResourceDataHelper.ToolType.Pickaxe)) miss.Add("a pickaxe in a chest or a companion's bag"); break;
                case Kind.Smelt: if (!Near<Smelter>(ChestRadius)) miss.Add("a smelter, kiln or furnace at base"); break;
                case Kind.Cook: if (!Near<CookingStation>(ChestRadius)) miss.Add("a cooking station at base"); break;
                case Kind.Fires: if (!Near<Fireplace>(ChestRadius)) miss.Add("a fire at base"); break;
                case Kind.Organize: if (ChestHelper.FindNearbyChests(b, ChestRadius).Count < 2) miss.Add("2 or more chests at base"); break;
                case Kind.Repair: if (!Has("Hammer")) miss.Add("a Hammer in a chest or a companion's bag"); break;
                case Kind.FishHome: case Kind.FishTrip:
                    if (!items.Any(i => i?.m_dropPrefab != null && i.m_dropPrefab.name.StartsWith("FishingRod"))) miss.Add("a fishing rod");
                    if (!items.Any(i => i?.m_dropPrefab != null && i.m_dropPrefab.name.StartsWith("FishingBait"))) miss.Add("fishing bait");
                    break;
                case Kind.Craft: if (FiresCore.Npc.AI.ChoreBrain.FindWorkstation(b, ChestRadius) == null) miss.Add("a workbench at base"); break;
            }
            if (j.IsTrip)
            {
                Vector3 c = j.Center; float d = Vector3.Distance(new Vector3(b.x, 0, b.z), new Vector3(c.x, 0, c.z));
                if (d > TripBehavior.MaxRange) miss.Add($"a center within {TripBehavior.MaxRange:0} m of home (it is {d:0} m)");
            }
            return miss;
        }

        // ---- editing -------------------------------------------------------------------------------------------------
        public static Job Add(Kind kind, Repeat repeat)
        {
            Load();
            var j = new Job { Id = s_nextId++, Kind = kind, Repeat = repeat };
            if (j.IsTrip) j.Center = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : BaseCenter();
            ArmDue(j);
            Jobs.Add(j);
            Save();
            FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] added {Label(j)} ({Schedule(j)})");
            return j;
        }

        /// <summary>Re-arms a job after its schedule changed (a one-off whose time already passed today runs tomorrow).</summary>
        public static void ArmDue(Job j)
        {
            j.Started = false; j.DoneAt = -1f; j.Running.Clear();
            int today = EnvMan.instance != null ? EnvMan.instance.GetDay() : 0;
            j.DueDay = j.Repeat == Repeat.OneOff && MinuteNow() > j.Minute ? today + 1 : today;
            if (j.Repeat == Repeat.Daily && MinuteNow() > j.Minute) j.LastRunDay = today;   // first run tomorrow, not straight away
        }

        public static void Remove(Job j) { Jobs.Remove(j); Save(); FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] removed {Label(j)}"); Changed?.Invoke(); }

        public static void Saved(Job j) { Save(); Changed?.Invoke(); }

        /// <summary>Assigns a companion (null = Anyone). The companion's chore toggle goes ON, the same switch the radial flips.</summary>
        public static void Assign(Job j, CompanionController c, bool add = false)
        {
            if (!add) j.Assigned.Clear();
            if (c != null && !string.IsNullOrEmpty(c.companionId) && !j.Assigned.Contains(c.companionId))
            {
                j.Assigned.Add(c.companionId);
                SetToggle(c, j.Kind, true);
            }
            Save(); Changed?.Invoke();
            FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] {Label(j)}: assigned {(c == null ? "Anyone" : c.GetDisplayName())}");
        }

        public static string AssignedText(Job j)
        {
            if (j.Assigned.Count == 0) return j.IsTrip && j.Count > 1 ? $"Anyone ×{j.Count}" : "Anyone";
            var names = j.Assigned.Select(id => CompanionController.AllCompanions.FirstOrDefault(c => c != null && c.companionId == id)?.GetDisplayName() ?? "(away)");
            return string.Join(", ", names);
        }

        private static void SetToggle(CompanionController c, Kind k, bool on)
        {
            switch (k)
            {
                case Kind.Farm: case Kind.Wood: case Kind.MineHome: CompanionBehaviorToggles.SetGatherEnabled(c, on); break;
                case Kind.Smelt: CompanionBehaviorToggles.SetSmelterEnabled(c, on); break;
                case Kind.Cook: CompanionBehaviorToggles.SetCookingEnabled(c, on); break;
                case Kind.Fires: CompanionBehaviorToggles.SetFiresEnabled(c, on); break;
                case Kind.Organize: CompanionBehaviorToggles.SetOrganizeEnabled(c, on); break;
                case Kind.Repair: CompanionBehaviorToggles.SetRepairEnabled(c, on); break;
                case Kind.FishHome: case Kind.FishTrip: CompanionBehaviorToggles.SetFishingEnabled(c, on); break;
                case Kind.Craft: CompanionBehaviorToggles.SetCraftingEnabled(c, on); break;
                case Kind.HuntTrip: CompanionBehaviorToggles.SetHuntingEnabled(c, on); break;
            }
        }

        // ---- running -------------------------------------------------------------------------------------------------
        private static int MinuteNow() => EnvMan.instance != null ? Mathf.FloorToInt(EnvMan.instance.GetDayFraction() * 24f * 60f) % (24 * 60) : 0;

        public static string LastNext(Job j)
        {
            if (j.Running.Count > 0 && j.Running.Any(IsRunning)) return "● running";
            if (j.DoneAt >= 0f) return "✔ done" + (string.IsNullOrEmpty(j.LastResult) ? "" : " · " + j.LastResult);
            int today = EnvMan.instance != null ? EnvMan.instance.GetDay() : 0;
            switch (j.Repeat)
            {
                case Repeat.Daily: return (j.LastRunDay >= 0 ? $"✔ day {j.LastRunDay}" : "not run yet") + " · next " + (j.LastRunDay == today ? "tomorrow " : "today ") + Clock(j.Minute);
                case Repeat.OneOff: return "queued · " + (j.DueDay > today ? "tomorrow " : "today ") + Clock(j.Minute);
                default: return "starting";
            }
        }

        private static bool IsRunning(string id)
        {
            var c = CompanionController.AllCompanions.FirstOrDefault(x => x != null && x.companionId == id);
            var sub = c != null ? c.GetComponent<CompanionIdleBehavior>()?.ActiveSubBehavior : null;
            return sub != null && sub.IsCommandInitiated;
        }

        internal static void Tick()
        {
            if (Player.m_localPlayer == null || EnvMan.instance == null) return;
            Load();
            int today = EnvMan.instance.GetDay(), now = MinuteNow();
            bool changed = false;
            foreach (var j in Jobs.ToList())
            {
                // finished runs
                if (j.Started && j.DoneAt < 0f && j.Running.Count > 0 && !j.Running.Any(IsRunning))
                {
                    j.DoneAt = Time.time;
                    if (j.Repeat == Repeat.Daily) { j.Started = false; j.Running.Clear(); j.DoneAt = -1f; }
                    changed = true;
                }
                if (j.Repeat != Repeat.Daily && j.DoneAt >= 0f && Time.time - j.DoneAt > DoneLinger) { Jobs.Remove(j); changed = true; FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] {Label(j)}: done, off the board"); continue; }
                bool due = j.Repeat == Repeat.Now ? !j.Started
                         : j.Repeat == Repeat.OneOff ? !j.Started && (today > j.DueDay || (today == j.DueDay && now >= j.Minute))
                         : j.LastRunDay != today && now >= j.Minute;
                if (!due) continue;
                var miss = Missing(j);
                if (miss.Count > 0)
                {
                    if (j.Repeat == Repeat.Daily) j.LastRunDay = today;   // skipped today
                    j.LastResult = "skipped: needs " + string.Join(", ", miss);
                    FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] {Label(j)}: skipped ({j.LastResult})");
                    changed = true;
                    if (j.Repeat != Repeat.Daily) { j.Started = true; j.DoneAt = Time.time; }
                    continue;
                }
                int started = Run(j);
                if (started == 0) { j.LastResult = "no free companion"; continue; }   // tries again next tick
                j.Started = true; j.LastRunDay = today; j.DoneAt = -1f; j.LastResult = "";
                changed = true;
            }
            if (changed) { Save(); Changed?.Invoke(); }
        }

        /// <summary>Starts the job on its companions (named first, else the free ones at base, best at the job first). Returns how many started.</summary>
        public static int Run(Job j)
        {
            var atBase = CompanionsAtBase();
            var skill = SkillOf(j.Kind);
            IEnumerable<CompanionController> pool = j.Assigned.Count > 0
                ? j.Assigned.Select(id => atBase.FirstOrDefault(c => c.companionId == id)).Where(c => c != null)
                : atBase.Where(IsFree).OrderByDescending(c => skill == Skills.SkillType.None ? 0f : c.GetSkills()?.GetSkillLevel(skill) ?? 0f);
            int want = j.IsTrip ? Mathf.Clamp(j.Count, 1, TripBehavior.MaxParty) : 1;
            j.Running.Clear();
            foreach (var c in pool)
            {
                if (j.Running.Count >= want) break;
                if (StartOn(j, c, out string why)) { j.Running.Add(c.companionId); FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] {Label(j)}: started on {c.GetDisplayName()}"); }
                else FiresCore.Logging.FiresLogger.LogDebug($"[JobBoard] {Label(j)}: {c.GetDisplayName()} can't ({why})");
            }
            return j.Running.Count;
        }

        internal static bool StartOn(Job j, CompanionController c, out string why)
        {
            why = "can't start now";
            var idle = c.GetComponent<CompanionIdleBehavior>();
            if (idle == null) return false;
            SetToggle(c, j.Kind, true);
            switch (j.Kind)
            {
                case Kind.Farm: return idle.TryStartSubBehavior<FarmingBehavior>();
                case Kind.Wood: return idle.TryStartSubBehavior<WoodGatheringBehavior>();
                case Kind.MineHome: return idle.TryStartSubBehavior<ResourceGatheringBehavior>();
                case Kind.Smelt: return idle.TryStartSubBehavior<SmelterOperatorBehavior>();
                case Kind.Cook: return idle.TryStartSubBehavior<CompanionCookingBehavior>();
                case Kind.Fires: return idle.TryStartSubBehavior<FireTendingBehaviorV2>();
                case Kind.Organize: return idle.TryStartSubBehavior<ChestDepositBehaviorV2>();
                case Kind.Repair: return idle.TryStartSubBehavior<BuildingRepairBehavior>();
                case Kind.FishHome: return idle.TryStartSubBehavior<FishingBehavior>();
                case Kind.Craft: return idle.TryStartSubBehavior<CraftingUpgradeBehaviorV2>();
                default:
                    var trip = idle.GetSubBehavior<TripBehavior>();
                    if (trip == null) return false;
                    var kind = j.Kind == Kind.FishTrip ? TripBehavior.TripKind.Fish : j.Kind == Kind.MineTrip ? TripBehavior.TripKind.Mine : TripBehavior.TripKind.Hunt;
                    if (!trip.Arm(kind, j.Center, j.Prey, j.Radius, out why)) return false;
                    return idle.TryStartSubBehavior<TripBehavior>();
            }
        }

        // ---- storage -------------------------------------------------------------------------------------------------
        private static string FileFor()
        {
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : null;
            var p = Player.m_localPlayer;
            if (string.IsNullOrEmpty(world) || p == null) return null;
            foreach (char ch in Path.GetInvalidFileNameChars()) world = world.Replace(ch, '_');
            string name = $"{world}_{p.GetPlayerID()}.txt";
            string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "cache", "FiresUnifiedCore", "JobBoard", name);
            // team convention: runtime data under BepInExche, configs config-only. One-time move of a 0.2.795 save.
            string old = Path.Combine(BepInEx.Paths.ConfigPath, "FiresUnifiedCore", "JobBoard", name);
            try
            {
                if (File.Exists(old) && !File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.Move(old, path);
                    FiresCore.Logging.FiresLogger.LogInfo($"[JobBoard] moved the saved jobs from BepInEx/config to {path}");
                }
            }
            catch (Exception e) { Debug.LogWarning("[JobBoard] move from config: " + e.Message); }
            return path;
        }

        /// <summary>One job as a line (the client file and the server sync share it).</summary>
        internal static string ToLine(Job j)
        {
            var inv = CultureInfo.InvariantCulture;
            return string.Join("|", j.Id.ToString(inv), j.Kind, j.Repeat, j.Minute.ToString(inv), string.Join(",", j.Assigned), j.Count.ToString(inv),
                j.Center.x.ToString("0.#", inv), j.Center.y.ToString("0.#", inv), j.Center.z.ToString("0.#", inv), j.Radius.ToString("0", inv),
                (j.Prey ?? "").Replace("|", ""), j.LastRunDay.ToString(inv), j.DueDay.ToString(inv));
        }

        internal static Job FromLine(string line)
        {
            var p = (line ?? "").Split('|');
            if (p.Length < 12) return null;
            var inv = CultureInfo.InvariantCulture;
            try
            {
                var j = new Job
                {
                    Id = int.Parse(p[0], inv), Kind = (Kind)Enum.Parse(typeof(Kind), p[1]), Repeat = (Repeat)Enum.Parse(typeof(Repeat), p[2]),
                    Minute = int.Parse(p[3], inv), Count = int.Parse(p[5], inv),
                    Center = new Vector3(float.Parse(p[6], inv), float.Parse(p[7], inv), float.Parse(p[8], inv)), Radius = float.Parse(p[9], inv),
                    Prey = p[10], LastRunDay = int.Parse(p[11], inv), DueDay = p.Length > 12 ? int.Parse(p[12], inv) : -1,
                };
                j.Assigned.AddRange(p[4].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
                return j;
            }
            catch { return null; }
        }

        public static void Load()
        {
            string f = FileFor();
            if (f == null || f == s_file) return;
            s_file = f; Jobs.Clear(); s_nextId = 1;
            if (!File.Exists(f)) { JobBoardServer.Push(); return; }
            foreach (string line in File.ReadAllLines(f))
            {
                var j = FromLine(line);
                if (j == null) continue;
                Jobs.Add(j);
                s_nextId = Mathf.Max(s_nextId, j.Id + 1);
            }
            JobBoardServer.Push();   // the server learns the daily jobs (and answers with its own last-run days)
        }

        private static void Save()
        {
            string f = FileFor();
            if (f == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(f));
                // a Now / one-off job that already ran isn't kept across a restart
                File.WriteAllLines(f, Jobs.Where(j => j.Repeat == Repeat.Daily || !j.Started).Select(ToLine).ToArray());
            }
            catch (Exception e) { Debug.LogWarning("[JobBoard] save: " + e.Message); }
            JobBoardServer.Push();
        }

        // ---- the scheduler tick ---------------------------------------------------------------------------------------
        private sealed class Runner : MonoBehaviour
        {
            private float _next;
            private void Update()
            {
                if (Time.time < _next) return;
                _next = Time.time + 2f;
                try { Tick(); } catch (Exception e) { Debug.LogWarning("[JobBoard] tick: " + e.Message); }
            }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_JobBoard
        {
            private static void Postfix()
            {
                s_file = null; Jobs.Clear();
                if (UnityEngine.Object.FindObjectOfType<Runner>() == null) { var go = new GameObject("FiresJobBoard"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<Runner>(); }
            }
        }
    }
}
