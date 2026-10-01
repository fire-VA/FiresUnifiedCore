using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// The one fishing brain for every body (Core 0.2.243; Fire: "they need to stand at the waters edge, equip bait, and cast into the
    /// water and actually fish, reeling in when they get a bite, aiming the cast towards fish to catch"). It decides where to stand,
    /// where to cast and how hard, which bait, and once the float is out what to do each tick; the game does the fishing: the rod
    /// throws its projectile, which drops a FishingFloat where it lands, fish swim to a float over 1 m+ of water within its range and
    /// nibble, holding block in the 0.5 s nibble window hooks, holding block reels, and a line of 0.5 m lands the catch.
    /// The FDT bot presses the keys (attack to draw and cast, block to reel); companions cast through <see cref="CastProjectile"/> and
    /// reel through <see cref="SetReeling"/>. The patches below let a companion own a float (vanilla resolves owners from the player
    /// list by user id, which for a companion finds its owner's player).
    /// </summary>
    public static class FishingBrain
    {
        public enum FishMove { Wait, Hook, Reel, Rest, Recast, Landed, Lost, Failed }

        /// <summary>Where to stand, where the float should land, and the cast for it (<see cref="Plan(Character, Inventory, ItemDrop.ItemData, Vector3, float, Func{Vector3, bool}, out FishPlan)"/>).</summary>
        public sealed class FishPlan
        {
            /// <summary>Dry ground (or a dock piece) at the water's edge, with a clear throw to <see cref="Aim"/>.</summary>
            public Vector3 Stand;
            /// <summary>The water surface point the float should land on: at least <see cref="MinCastDepth"/> deep.</summary>
            public Vector3 Aim;
            /// <summary>Horizontal unit vector from <see cref="Stand"/> toward <see cref="Aim"/>; the cast assumes a level look (pitch 0).</summary>
            public Vector3 LookDir;
            /// <summary>Rod draw 0..1 for a level look (vanilla GetAttackDrawPercentage), from the rod's own launch numbers.</summary>
            public float Draw;
            /// <summary>Horizontal metres from <see cref="Stand"/> to <see cref="Aim"/>.</summary>
            public float Distance;
            /// <summary>Water depth at <see cref="Aim"/>.</summary>
            public float Depth;
            public ItemDrop.ItemData Rod, Bait;
            /// <summary>The fish species the cast goes for (one that takes the bait), or null when none is seen.</summary>
            public string Fish;
            /// <summary>Fish within the float's range of <see cref="Aim"/> that take a carried bait.</summary>
            public int FishSeen;
            /// <summary>0.2.258: how the stand was chosen and its reach: "from where it stands (no walk)", "at the edge, path checked",
            /// "2.5 m back from the edge, path checked", or "… unchecked" (the caller gave no reach test).</summary>
            public string Reach = "";
            /// <summary>0.2.264: the fish the plan saw that take a carried bait ("fish seen: Fish1 x2 within 32 m of (48, 530)"), and the
            /// move when none were near the asked spot ("no fish in this pond (scanned 32 m); moving to (60, 512) where Fish1 x1").</summary>
            public string Seen = "";

            public override string ToString() =>
                $"stand ({Stand.x:0}, {Stand.z:0}) -> ({Aim.x:0}, {Aim.z:0}) {Distance:0.0} m cast, {Depth:0.0} m deep, draw {Draw:0.00}, " +
                $"{(Bait != null ? Utils.GetPrefabName(Bait.m_dropPrefab) : "no bait")}{(Fish != null ? $" toward {Fish} ({FishSeen} seen)" : "")}" +
                $"{(Reach.Length > 0 ? $"; stand {Reach}" : "")}{(Seen.Length > 0 ? $"; {Seen}" : "")}";
        }

        // "Fish1 x2, Fish2 x1" for a list of fish.
        private static string FishSummary(IEnumerable<Fish> fish) =>
            string.Join(", ", fish.GroupBy(f => Utils.GetPrefabName(f.gameObject)).Select(g => $"{g.Key} x{g.Count()}"));

        /// <summary>Fish only swim to a float over at least 1 m of water (Fish.m_minDepth, RandomizeWaypoint); a margin for waves.</summary>
        public const float MinCastDepth = 1.5f;
        public const float MinCast = 4f;
        /// <summary>The float's line breaks past 30 m (FishingFloat.m_maxDistance); casts stay well inside.</summary>
        public const float MaxCast = 16f;
        private const float GridStep = 2.5f;
        private const float MaxRadius = 48f;
        private const float NibbleWindow = 0.45f;   // FishingFloat.TryToHook: 0.5 s after the nibble
        private const float RecastAfter = 30f;
        private const float InAirFor = 2.5f;        // the rod's projectile flies before it drops the float
        private const float TensionRest = 2f;       // the line breaks at FishingFloat.m_breakDistance (4 m) of tension

        private sealed class CastState
        {
            public float Time;
            public Vector3 Origin;
            public float Expected;
            public float Draw;
            public bool Measured;
            public string Hooked;
            // 0.2.259: the stamina and line at the hook (to learn the reel's cost per metre), the last line seen, and since when the line is taut.
            public float HookStamina = -1f, HookLine, LastLine, TautSince = -1f;
            // 0.2.261: the last reel sample (every 2 s while hooked): when, the stamina and the line then.
            public float SampleAt = -1f, SampleStamina, SampleLine;
            // 0.2.263: since when the fish is running (an escape).
            public float RunningSince = -1f;
        }

        /// <summary>0.2.263: an escape held longer than this reels on slack anyway (a stale escape state; vanilla's escapes last 0.5-3 s + 1.5 s per quality).</summary>
        private const float EscapeHoldMax = 12f;

        // The fish's stamina use per second on the reel (vanilla: m_staminaUse, or m_escapeStaminaUse while running, times its quality).
        private static float FishUse(Fish fish, bool escaping)
        {
            if (fish == null) return 0f;
            ItemDrop drop = fish.GetComponent<ItemDrop>();
            return (escaping ? fish.m_escapeStaminaUse : fish.m_staminaUse) * (drop != null ? drop.m_itemData.m_quality : 1);
        }

        // 0.2.261: where a hooked reel's stamina goes, every 2 s: the stamina and line change per second, and every vanilla drain that could
        // be running at once (the reel = the rod held up / blocking, an attack draw held, running, swimming, encumbered), the escape as the
        // fish's owner says, the tension. Vanilla's numbers for that body and fish are on the "hooked" line.
        private static void SampleReel(Character body, CastState cast, FishingFloat line, Fish hooked, float tension, float now)
        {
            if (!(body is Player p)) return;
            if (cast.SampleAt < 0f) { cast.SampleAt = now; cast.SampleStamina = p.GetStamina(); cast.SampleLine = line.m_lineLength; return; }
            float dt = now - cast.SampleAt;
            if (dt < 2f) return;
            float staminaRate = (cast.SampleStamina - p.GetStamina()) / dt, lineRate = (cast.SampleLine - line.m_lineLength) / dt;
            bool drawing = p.m_attackHold && p.m_attackDrawTime >= 0f;
            Say(body, $"reel: stamina {p.GetStamina():0} (-{staminaRate:0.0}/s), line {line.m_lineLength:0.0} m (-{lineRate:0.00} m/s); " +
                      $"blocking {p.IsBlocking()}, drawing {drawing}, in attack {p.InAttack()}, running {p.IsRunning()}, speed {p.GetVelocity().magnitude:0.0} m/s, " +
                      $"swimming {p.IsSwimming()}, encumbered {p.IsEncumbered()}; escaping {EscapingNow(hooked)} (local {hooked.IsEscaping()}), tension {tension:0.0} m", force: true);
            cast.SampleAt = now; cast.SampleStamina = p.GetStamina(); cast.SampleLine = line.m_lineLength;
        }

        // The vanilla reel numbers for this float, fish and body (prefab values, not the class defaults), for the hooked line.
        private static string ReelNumbers(Character body, FishingFloat line, Fish fish)
        {
            ItemDrop drop = fish != null ? fish.GetComponent<ItemDrop>() : null;
            int quality = drop != null ? drop.m_itemData.m_quality : 1;
            float skill = body.GetSkillFactor(Skills.SkillType.Fishing);
            return $"vanilla: pull {line.m_pullStaminaUse:0.##}/s (x{line.m_pullStaminaUseMaxSkillMultiplier:0.##} at max skill), line {line.m_pullLineSpeed:0.##}-{line.m_pullLineSpeedMaxSkill:0.##} m/s, " +
                   $"hooked {line.m_hookedStaminaPerSec:0.##}-{line.m_hookedStaminaPerSecMaxSkill:0.##}/s, fish {fish?.m_staminaUse:0.##}/s ({fish?.m_escapeStaminaUse:0.##} escaping) x q{quality}, " +
                   $"Fishing skill {skill * 100f:0}, stamina rate x{Game.m_staminaRate:0.##}";
        }

        /// <summary>
        /// 0.2.259 (R41: 5 of 5 hooked fish lost at 0 stamina, one 0.3 m from landing): vanilla's reel (FishingFloat.FixedUpdate) spends
        /// m_pullStaminaUse + the fish's stamina use every tick the rod is held up, but shortens the line only while it has slack (the float
        /// within <see cref="SlackTension"/> of the line's length); a hooked fish drains m_hookedStaminaPerSec on top, which keeps resetting
        /// the stamina regen, so the stamina at the hook is all the reel gets; at 0 the fish is gone. So: reel only on slack, hold while the
        /// fish runs or the line is taut (the float's own pull takes it in), and cast no further than the stamina reels in.
        /// </summary>
        public const float SlackTension = 0.15f;
        /// <summary>
        /// 0.2.261 ([lead], R43 measured 19 stamina/m, which a human with 75 stamina would never land with): the cast cap and ReadyToCast stay
        /// OFF until the reel's real cost is understood (the per-2 s "reel:" lines and the "hooked" line's constants say where it goes).
        /// </summary>
        public const bool CastCapOn = false;
        /// <summary>A taut hold longer than this reels anyway (the float stuck on something).</summary>
        private const float TautHoldMax = 3f;
        /// <summary>The stamina kept back at the end of a reel.</summary>
        private const float ReelMargin = 8f;
        private static readonly Dictionary<int, float> s_reelCost = new Dictionary<int, float>();

        /// <summary>
        /// Stamina per metre of line reeled in by <paramref name="body"/>: learned from its last reels, else vanilla's numbers for its
        /// Fishing skill (pull + the fish's use + the hooked drain, at the pull speed). 0 for a non-player (its stamina never runs out).
        /// </summary>
        public static float ReelCostPerMetre(Character body, FishingFloat floatPrefab)
        {
            // 0.2.260: only what a real reel measured. Vanilla's numbers for the skill read ~0.9/m on the R42 bot against ~7-11/m
            // measured in R41, and a wrong model must not block a cast; 0 (no limit) until the first reel ends.
            if (!(body is Player) || floatPrefab == null) return 0f;
            return s_reelCost.TryGetValue(body.GetInstanceID(), out float learned) ? learned : 0f;
        }

        /// <summary>The metres of line <paramref name="body"/> can reel in on <paramref name="stamina"/> (no limit for a non-player).</summary>
        public static float ReelableMetres(Character body, FishingFloat floatPrefab, float stamina)
        {
            float cost = ReelCostPerMetre(body, floatPrefab);
            return cost <= 0f ? float.MaxValue : Mathf.Max(0f, stamina - ReelMargin) / cost;
        }

        /// <summary>
        /// 0.2.259: whether <paramref name="body"/> has the stamina to reel in a fish hooked at <paramref name="plan"/>'s distance (the
        /// line runs about the cast's length plus 1 m). False with why ("rest: …") when it should rest or eat first; always true for a
        /// non-player.
        /// </summary>
        public static bool ReadyToCast(Character body, FishPlan plan, out string why)
        {
            why = "";
            if (!CastCapOn || !(body is Player p) || plan == null) return true;
            FishingFloat floatPrefab = FloatPrefabOf(plan.Rod);
            float cost = ReelCostPerMetre(body, floatPrefab);
            if (cost <= 0f) return true;
            float need = (plan.Distance + 1f) * cost + ReelMargin;
            if (p.GetStamina() >= need) return true;
            why = $"rest: stamina {p.GetStamina():0}/{p.GetMaxStamina():0}, a {plan.Distance:0.0} m cast needs ~{need:0} to reel in ({cost:0.0} per metre)" +
                  (p.GetMaxStamina() < need ? " - more than its max: eat for stamina" : "");
            return false;
        }

        // Learn the reel's cost per metre from a reel that ended (landed at 0.5 m, or lost at the last line seen).
        private static void LearnReel(Character body, CastState cast, float endLine)
        {
            if (!(body is Player p) || cast == null || cast.HookStamina < 0f) return;
            float metres = cast.HookLine - endLine, used = cast.HookStamina - p.GetStamina();
            if (metres < 1f || used <= 0f) return;
            int id = body.GetInstanceID();
            float seen = used / metres;
            s_reelCost[id] = s_reelCost.TryGetValue(id, out float old) ? Mathf.Lerp(old, seen, 0.5f) : seen;
            Say(body, $"reel cost: {used:0} stamina for {metres:0.0} m ({seen:0.0}/m, now {s_reelCost[id]:0.0}/m)", force: true);
        }

        private static readonly Dictionary<int, CastState> s_casts = new Dictionary<int, CastState>();
        private static readonly Dictionary<int, float> s_gain = new Dictionary<int, float>();
        private static readonly Dictionary<int, (float time, string fish, int quality)> s_catches = new Dictionary<int, (float, string, int)>();
        private static readonly HashSet<int> s_reeling = new HashSet<int>();
        private static readonly Dictionary<int, (string line, float time)> s_lastLine = new Dictionary<int, (string, float)>();
        private static int s_solidMask;

        // ---- Items ----

        /// <summary>The float a rod's attack ends in (its projectile's spawn-on-hit, or the projectile itself), or null for no rod.</summary>
        public static FishingFloat FloatPrefabOf(ItemDrop.ItemData rod)
        {
            GameObject projectile = rod?.m_shared?.m_attack?.m_attackProjectile;
            if (projectile == null) return null;
            return projectile.GetComponent<FishingFloat>() ?? projectile.GetComponent<Projectile>()?.m_spawnOnHit?.GetComponent<FishingFloat>();
        }

        /// <summary>A fishing rod in <paramref name="bag"/> (any item whose attack ends in a FishingFloat), or null.</summary>
        public static ItemDrop.ItemData RodIn(Inventory bag) => bag?.GetAllItems().FirstOrDefault(i => FloatPrefabOf(i) != null);

        private static bool IsBaitFor(ItemDrop.ItemData item, ItemDrop.ItemData rod) =>
            item != null && rod != null && item != rod && !string.IsNullOrEmpty(rod.m_shared.m_ammoType) && item.m_shared.m_ammoType == rod.m_shared.m_ammoType;

        /// <summary>
        /// The best bait in <paramref name="bag"/> for <paramref name="target"/> (its highest Fish.m_baits chance carried), or with no
        /// target the bait most fish near <paramref name="near"/> take, or any bait the rod throws; null when none is carried.
        /// </summary>
        public static ItemDrop.ItemData BestBait(Inventory bag, Fish target = null, Vector3? near = null, ItemDrop.ItemData rod = null)
        {
            rod = rod ?? RodIn(bag);
            if (bag == null || rod == null) return null;
            List<ItemDrop.ItemData> baits = bag.GetAllItems().Where(i => IsBaitFor(i, rod)).ToList();
            if (baits.Count == 0) return null;
            IEnumerable<Fish> judges = target != null ? new[] { target } : near.HasValue ? FishNear(near.Value, MaxRadius) : Enumerable.Empty<Fish>();
            ItemDrop.ItemData best = null;
            float bestScore = 0f;
            foreach (ItemDrop.ItemData bait in baits)
            {
                string name = bait.m_dropPrefab != null ? bait.m_dropPrefab.name : null;
                float score = judges.Sum(f => f.m_baits.Where(b => b.m_bait != null && b.m_bait.name == name).Select(b => b.m_chance).DefaultIfEmpty(0f).Max());
                if (score > bestScore) { bestScore = score; best = bait; }
            }
            return best ?? baits[0];
        }

        /// <summary>The fish swimming within <paramref name="radius"/> of <paramref name="center"/> (not hooked).</summary>
        public static List<Fish> FishNear(Vector3 center, float radius)
        {
            float r2 = radius * radius;
            return Fish.Instances.OfType<Fish>().Where(f => f != null && !f.IsHooked() && (f.transform.position - center).sqrMagnitude <= r2).ToList();
        }

        private static float BaitChance(Fish fish, ItemDrop.ItemData bait)
        {
            string name = bait?.m_dropPrefab != null ? bait.m_dropPrefab.name : null;
            return name == null ? 0f : fish.m_baits.Where(b => b.m_bait != null && b.m_bait.name == name).Select(b => b.m_chance).DefaultIfEmpty(0f).Max();
        }

        // ---- Water ----

        /// <summary>
        /// Water at (x, z): its surface (the WaterVolume the fish use, sea or inland) and its depth over the solid ground (terrain or
        /// pieces, so a dock is not water). False where there is no water above the ground.
        /// </summary>
        public static bool WaterAt(Vector3 p, out float surface, out float depth)
        {
            surface = depth = 0f;
            ZoneSystem zones = ZoneSystem.instance;
            if (zones == null || !zones.GetSolidHeight(new Vector3(p.x, 0f, p.z), out float ground)) return false;
            surface = LiquidAt(p.x, p.z, ground, zones.m_waterLevel);
            if (surface < -1000f) return false;
            depth = surface - ground;
            return depth > 0f;
        }

        // The water surface over (x, z), probed just above the ground (a lake bed) and just below the sea level (the zone's sea volume).
        // 0.2.256: and FAT's lakes, rivers, pours and ponds (World.Water, FAT 0.2.673's one water query).
        private static float LiquidAt(float x, float z, float ground, float seaLevel) =>
            FiresCore.World.Water.SurfaceOr(x, z,
                Mathf.Max(Floating.GetLiquidLevel(new Vector3(x, ground + 0.3f, z), 1f, LiquidType.Water),
                          Floating.GetLiquidLevel(new Vector3(x, seaLevel - 0.3f, z), 1f, LiquidType.Water)));

        // Dry footing at (x, z): the solid height (ground or a piece), with no water more than 0.2 m over it.
        private static bool DryAt(Vector3 p, out Vector3 stand)
        {
            stand = p;
            ZoneSystem zones = ZoneSystem.instance;
            if (zones == null || !zones.GetSolidHeight(new Vector3(p.x, 0f, p.z), out float ground)) return false;
            if (LiquidAt(p.x, p.z, ground, zones.m_waterLevel) > ground + 0.2f) return false;
            stand = new Vector3(p.x, ground, p.z);
            return true;
        }

        // ---- Planning ----

        /// <summary><see cref="Plan(Character, Inventory, ItemDrop.ItemData, Vector3, float, Func{Vector3, bool}, out FishPlan)"/> for a body whose own bag holds the rod and the bait (the bot).</summary>
        public static string Plan(Humanoid body, Vector3 near, float radius, Func<Vector3, bool> standOk, out FishPlan plan) =>
            Plan(body, body != null ? body.GetInventory() : null, null, near, radius, standOk, out plan);

        /// <summary>
        /// Where <paramref name="body"/> fishes near <paramref name="near"/>: water at least <see cref="MinCastDepth"/> deep within
        /// <paramref name="radius"/> (preferring water near fish that take a carried bait), a dry stand at its edge with a clear throw
        /// <see cref="MinCast"/>..<see cref="MaxCast"/> m away that <paramref name="standOk"/> accepts (null = all), the bait, and the draw.
        /// <paramref name="rod"/> null = the rod in <paramref name="bag"/>. "" with <paramref name="plan"/>, or a coded reason: "norod: …",
        /// "nobait: …", "nowater: …", "noshore: …".
        /// </summary>
        public static string Plan(Character body, Inventory bag, ItemDrop.ItemData rod, Vector3 near, float radius, Func<Vector3, bool> standOk, out FishPlan plan) =>
            PlanCore(body, bag, rod, near, radius, standOk, false, out plan);

        /// <summary>
        /// 0.2.258 (R37: the bot stalled three times walking to the water's edge at a shelved pond with 3 m deep water 15 m off): a cast
        /// from where <paramref name="body"/> stands now, no walk: water at least <see cref="MinCastDepth"/> deep <see cref="MinCast"/>..
        /// <see cref="MaxCast"/> m away with a clear throw, from dry ground. For a caller whose walk to <see cref="FishPlan.Stand"/> stalled.
        /// "" with <paramref name="plan"/>, or a coded reason as <see cref="Plan(Character, Inventory, ItemDrop.ItemData, Vector3, float, Func{Vector3, bool}, out FishPlan)"/>.
        /// </summary>
        public static string PlanHere(Character body, Inventory bag, ItemDrop.ItemData rod, out FishPlan plan) =>
            PlanCore(body, bag, rod, body != null ? body.transform.position : Vector3.zero, MaxCast + GridStep, null, true, out plan);

        /// <summary>The rank a cast from where the body stands gets over a walk to the edge (rank: 10 per fish score, 0.1 per metre walked).</summary>
        private const float HereBonus = 2f;
        /// <summary>Stands tried back from the edge along each way (0.2.258: a step or a shelf at the edge can make the edge itself unreachable).</summary>
        private static readonly float[] StandBacks = { 0.75f, 2.5f, 4.5f };

        private static string PlanCore(Character body, Inventory bag, ItemDrop.ItemData rod, Vector3 near, float radius, Func<Vector3, bool> standOk, bool hereOnly, out FishPlan plan)
        {
            plan = null;
            if (body == null || bag == null) return "missing: no body or bag";
            rod = rod ?? RodIn(bag);
            FishingFloat floatPrefab = FloatPrefabOf(rod);
            if (floatPrefab == null) return "norod: no fishing rod";
            radius = Mathf.Clamp(radius, GridStep, MaxRadius);
            float range = Mathf.Max(2f, floatPrefab.m_range - 1f);
            // 0.2.259: no further than a player's full stamina reels in (the line runs about the cast plus 1 m); see ReadyToCast.
            float castMax = MaxCast;
            if (CastCapOn && body is Player bp) castMax = Mathf.Clamp(ReelableMetres(body, floatPrefab, bp.GetMaxStamina()) - 1f, MinCast, MaxCast);
            List<Fish> fish = FishNear(near, radius + MaxCast);
            ItemDrop.ItemData anyBait = BestBait(bag, null, near, rod);
            if (anyBait == null)
            {
                string want = string.Join(", ", fish.SelectMany(f => f.m_baits).Where(b => b.m_bait != null).Select(b => b.m_bait.name).Distinct());
                return $"nobait: no bait for the rod in the bag{(want.Length > 0 ? $" (fish here take {want})" : "")}";
            }
            List<ItemDrop.ItemData> baits = bag.GetAllItems().Where(i => IsBaitFor(i, rod)).ToList();

            // 0.2.264 (R45: 13 casts at empty water, "nothing bit in 30 s" each; Fire: "the bot needs to be casting towards the fish"):
            // cast only toward a fish that is there and takes a carried bait. None near the asked spot: the nearest such fish within
            // MaxRadius + MaxCast, and the plan moves there; none at all: "nofish:", no blind cast.
            bool Takes(Fish f) => baits.Any(b => BaitChance(f, b) > 0f);
            List<Fish> takers = fish.Where(Takes).ToList();
            string seen;
            if (takers.Count > 0) seen = $"fish seen: {FishSummary(takers)} within {radius + MaxCast:0} m of ({near.x:0}, {near.z:0})";
            else
            {
                float wide = MaxRadius + MaxCast;
                List<Fish> farther = FishNear(near, wide).Where(Takes).ToList();
                if (farther.Count == 0)
                {
                    List<Fish> any = FishNear(near, wide);
                    return $"nofish: no fish that takes {string.Join("/", baits.Select(b => Utils.GetPrefabName(b.m_dropPrefab)).Distinct())} seen within {wide:0} m of ({near.x:0}, {near.z:0})" +
                           (any.Count > 0 ? $" ({FishSummary(any)} there, none take it)" : $" ({Fish.Instances.Count} fish loaded in all)");
                }
                Vector3 from0 = near;
                Fish nearest = farther.OrderBy(f => (f.transform.position - from0).sqrMagnitude).First();
                near = new Vector3(nearest.transform.position.x, near.y, nearest.transform.position.z);
                fish = FishNear(near, radius + MaxCast);
                takers = fish.Where(Takes).ToList();
                seen = $"no fish in this pond (scanned {radius + MaxCast:0} m of ({from0.x:0}, {from0.z:0})); moving to ({near.x:0}, {near.z:0}) where {FishSummary(takers)}";
                if (hereOnly) return $"nofish: {seen.Replace("moving to", "the nearest is at")}";
            }

            // 1. One grid scan: water cells deep enough (scored by the fish in the float's range that take a carried bait) and dry cells.
            var cells = new List<(Vector3 p, float depth, float score, Fish target)>();
            var dry = new List<Vector3>();
            int blind = 0;
            int n = Mathf.CeilToInt(radius / GridStep);
            for (int i = -n; i <= n; i++)
                for (int j = -n; j <= n; j++)
                {
                    Vector3 p = near + new Vector3(i * GridStep, 0f, j * GridStep);
                    if ((p - near).sqrMagnitude > radius * radius) continue;
                    bool wet = WaterAt(p, out float surface, out float depth);
                    if (!wet || depth <= 0.2f) { dry.Add(p); continue; }
                    if (depth < MinCastDepth) continue;
                    p.y = surface;
                    float score = 0f;
                    Fish target = null;
                    float targetScore = 0f;
                    foreach (Fish f in fish)
                    {
                        float d = Vector3.Distance(f.transform.position, p);
                        if (d > range) continue;
                        float chance = baits.Max(b => BaitChance(f, b));
                        float s = chance * (1f - d / (range + 1f));
                        score += s;
                        if (s > targetScore) { targetScore = s; target = f; }
                    }
                    if (score <= 0f) { blind++; continue; }   // 0.2.264: only water a seen fish is within the float's range of
                    cells.Add((p, depth, score, target));
                }
            if (cells.Count == 0)
                return blind > 0
                    ? $"nofish: {seen}, but none within the float's {range:0} m of water >= {MinCastDepth:0.0} m deep ({blind} cell(s) without a fish near)"
                    : $"nowater: no water >= {MinCastDepth:0.0} m deep within {radius:0} m of ({near.x:0}, {near.z:0})";

            // 2. A dry stand at the edge for the best cells: toward the nearest dry cells a cast's length away, the exact edge found in
            //    0.5 m steps, then the footing just past it.
            if (s_solidMask == 0) s_solidMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain");
            Vector3 from = body.transform.position;
            cells.Sort((a, b) => b.score != a.score ? b.score.CompareTo(a.score)
                : (Mathf.Abs(a.depth - 3f) + Vector3.Distance(a.p, from) * 0.05f).CompareTo(Mathf.Abs(b.depth - 3f) + Vector3.Distance(b.p, from) * 0.05f));
            var candidates = new List<(float rank, Vector3 stand, Vector3 aim, float depth, Fish target, string how)>();
            int tried = 0, found = 0;
            string lastRefusal = dry.Count == 0 ? "no dry ground in the scan" : $"no dry ground {MinCast:0}-{castMax:0} m from deep water";

            // 0.2.258 (R37): where the body stands now, when deep water lies within a cast of it (a shelf or a step can block the walk to
            // the edge; this one needs no walk and no reach test).
            if (DryAt(from, out Vector3 here))
            {
                bool any = false;
                (float rank, Vector3 aim, float depth, Fish target) pick = default;
                foreach (var cell in cells)
                {
                    float d = new Vector2(cell.p.x - here.x, cell.p.z - here.z).magnitude;
                    if (d < MinCast || d > castMax) continue;
                    if (Physics.Linecast(here + Vector3.up * 1.8f, cell.p + Vector3.up * 1f, s_solidMask)) continue;
                    float rank = cell.score * 10f - Mathf.Abs(cell.depth - 3f) + HereBonus;
                    if (any && rank <= pick.rank) continue;
                    any = true;
                    pick = (rank, cell.p, cell.depth, cell.target);
                }
                if (any) candidates.Add((pick.rank, here, pick.aim, pick.depth, pick.target, "from where it stands (no walk)"));
                else if (hereOnly) lastRefusal = $"no water {MinCastDepth:0.0}+ m deep {MinCast:0}-{castMax:0} m from where it stands with a clear throw";
            }
            else if (hereOnly) lastRefusal = "it is not on dry ground";

            foreach (var cell in cells)
            {
                if (hereOnly || ++tried > 30 || found >= 24) break;
                var shores = dry.Select(d => (d, dist: new Vector2(d.x - cell.p.x, d.z - cell.p.z).magnitude))
                    .Where(s => s.dist >= MinCast - GridStep && s.dist <= castMax + GridStep).OrderBy(s => s.dist).ToList();
                var dirs = new List<Vector3>();
                foreach (var (d, dist) in shores)
                {
                    Vector3 dir = new Vector3(d.x - cell.p.x, 0f, d.z - cell.p.z).normalized;
                    if (dirs.Any(o => Vector3.Dot(o, dir) > 0.87f)) continue;   // 30 degrees apart
                    dirs.Add(dir);
                    if (dirs.Count > 4) break;
                    float edge = -1f;
                    for (float t = Mathf.Max(1f, dist - GridStep * 1.5f); t <= dist + GridStep; t += 0.5f)
                        if (!WaterAt(cell.p + dir * t, out _, out float dq) || dq <= 0.2f) { edge = t; break; }
                    if (edge < 0f) continue;
                    if (edge < MinCast || edge > castMax) { lastRefusal = edge > castMax && castMax < MaxCast ? $"deep water is {edge:0.0} m out, more than the {castMax:0.0} m its stamina reels in" : "the shore is too close to deep water for a cast"; continue; }
                    // 0.2.258: the edge itself, and stands further back along the same way while the cast still reaches.
                    foreach (float back in StandBacks)
                    {
                        if (edge + back > castMax) break;
                        if (!DryAt(cell.p + dir * (edge + back), out Vector3 stand)) continue;
                        if (stand.y - cell.p.y > 6f) { lastRefusal = "the shore is a cliff over the water"; continue; }
                        if (Physics.Linecast(stand + Vector3.up * 1.8f, cell.p + Vector3.up * 1f, s_solidMask)) { lastRefusal = "the throw is blocked"; continue; }
                        candidates.Add((cell.score * 10f - Vector3.Distance(from, stand) * 0.1f - Mathf.Abs(cell.depth - 3f) - back * 0.2f, stand, cell.p, cell.depth, cell.target,
                            back < 1f ? "at the edge" : $"{back:0.0} m back from the edge"));
                        found++;
                    }
                }
            }
            // The caller's reach test (a path query) only for the best few, best first; a cast from where it stands needs none.
            candidates.Sort((a, b) => b.rank.CompareTo(a.rank));
            (float rank, Vector3 stand, Vector3 aim, float depth, Fish target, string how) best = default;
            bool have = false;
            int refusedReach = 0;
            foreach (var c in candidates.Take(12))
            {
                bool noWalk = c.how.StartsWith("from where");
                if (!noWalk && standOk != null && !standOk(c.stand)) { refusedReach++; lastRefusal = "no reachable shore"; continue; }
                best = c;
                if (!noWalk) best.how += standOk != null ? $", path checked{(refusedReach > 0 ? $" ({refusedReach} nearer-ranked stand(s) unreachable)" : "")}" : ", unchecked";
                have = true;
                break;
            }
            if (!have) return $"noshore: {lastRefusal} (water cells {cells.Count}, candidates {candidates.Count}, near ({near.x:0}, {near.z:0}))";

            ItemDrop.ItemData bait = best.target != null ? BestBait(bag, best.target, null, rod) : anyBait;
            Vector3 flat = best.aim - best.stand;
            flat.y = 0f;
            plan = new FishPlan
            {
                Stand = best.stand,
                Aim = best.aim,
                LookDir = flat.normalized,
                Distance = flat.magnitude,
                Depth = best.depth,
                Rod = rod,
                Bait = bait,
                Fish = best.target != null ? Utils.GetPrefabName(best.target.gameObject) : null,
                FishSeen = fish.Count(f => Vector3.Distance(f.transform.position, best.aim) <= range && BaitChance(f, bait) > 0f),
                Reach = best.how,
                Seen = seen,
            };
            plan.Draw = DrawFor(body, plan);
            return "";
        }

        // ---- The cast ----

        private static (float gravity, float drag) Flight(ItemDrop.ItemData rod)
        {
            Projectile projectile = rod?.m_shared?.m_attack?.m_attackProjectile?.GetComponent<Projectile>();
            return projectile != null ? (projectile.m_gravity, projectile.m_drag) : (Mathf.Abs(Physics.gravity.y), 0f);
        }

        // The vanilla projectile step (Projectile.FixedUpdate: gravity, then drag on the speed squared) until it drops below the surface;
        // the horizontal metres from measureFrom where it lands.
        private static float Landing(Vector3 origin, Vector3 velocity, float surface, Vector3 measureFrom, float gravity, float drag)
        {
            float dt = Time.fixedDeltaTime > 0f ? Time.fixedDeltaTime : 0.02f;
            Vector3 p = origin, v = velocity;
            for (int i = 0; i < 1500; i++)
            {
                v += Vector3.down * (gravity * dt);
                v += v.sqrMagnitude * drag * dt * -v.normalized;
                p += v * dt;
                if (p.y < surface && v.y < 0f) break;
            }
            return new Vector2(p.x - measureFrom.x, p.z - measureFrom.z).magnitude;
        }

        // Where the rod's projectile leaves a body standing at stand facing dir (Attack.GetProjectileSpawnPoint), and its launch direction
        // for a level look (Attack.FireProjectileBurst's launch angle).
        private static void LaunchFrom(Character body, ItemDrop.ItemData rod, Vector3 stand, Vector3 dir, out Vector3 origin, out Vector3 launch)
        {
            Attack attack = rod.m_shared.m_attack;
            Vector3 joint = Vector3.zero;
            if (!string.IsNullOrEmpty(attack.m_attackOriginJoint) && body.GetVisual() != null)
            {
                Transform t = Utils.FindChild(body.GetVisual().transform, attack.m_attackOriginJoint);
                if (t != null) joint = body.transform.InverseTransformPoint(t.position);
            }
            if (joint == Vector3.zero && attack.m_attackHeight < 0.5f) joint = new Vector3(0f, 1.4f, 0f);
            Quaternion facing = Quaternion.LookRotation(dir);
            origin = stand + facing * joint + Vector3.up * attack.m_attackHeight + dir * attack.m_attackRange + (facing * Vector3.right) * attack.m_attackOffset;
            launch = attack.m_launchAngle != 0f ? Quaternion.AngleAxis(attack.m_launchAngle, Vector3.Cross(Vector3.up, dir)) * dir : dir;
        }

        private static float SpeedForDraw(Attack attack, float draw) =>
            attack.m_bowDraw ? Mathf.Lerp(attack.m_projectileVelMin, attack.m_projectileVel, attack.m_drawVelocityCurve.Evaluate(draw)) : attack.m_projectileVel;

        private static float Gain(Character body) => body != null && s_gain.TryGetValue(body.GetInstanceID(), out float g) ? g : 1f;

        // The draw that lands the projectile plan.Distance (times the body's landing correction) from the stand.
        private static float DrawFor(Character body, FishPlan plan)
        {
            Attack attack = plan.Rod.m_shared.m_attack;
            if (!attack.m_bowDraw) return 1f;
            var (gravity, drag) = Flight(plan.Rod);
            LaunchFrom(body, plan.Rod, plan.Stand, plan.LookDir, out Vector3 origin, out Vector3 launch);
            float want = plan.Distance * Gain(body);
            float Land(float draw) => Landing(origin, launch * SpeedForDraw(attack, draw), plan.Aim.y, plan.Stand, gravity, drag);
            if (Land(1f) <= want) return 1f;
            if (Land(0f) >= want) return 0f;
            float lo = 0f, hi = 1f;
            for (int i = 0; i < 14; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Land(mid) < want) lo = mid; else hi = mid;
            }
            return (lo + hi) * 0.5f;
        }

        /// <summary>
        /// Tell the brain a cast was just released by <paramref name="body"/> for <paramref name="plan"/> (the bot's FishVerb after it lets
        /// go of attack; <see cref="CastProjectile"/> calls it for companions): <see cref="Advise"/> times the cast from here and measures
        /// where the float lands to correct the next draw.
        /// </summary>
        public static void NoteCast(Character body, FishPlan plan)
        {
            if (body == null || plan == null) return;
            s_casts[body.GetInstanceID()] = new CastState { Time = Time.time, Origin = body.transform.position, Expected = plan.Distance, Draw = plan.Draw };
            Say(body, $"cast at ({plan.Aim.x:0}, {plan.Aim.z:0}) {plan.Distance:0.0} m draw {plan.Draw:0.00}" +
                      $"{(plan.Fish != null ? $" toward {plan.Fish}" : "")} with {(plan.Bait?.m_dropPrefab != null ? plan.Bait.m_dropPrefab.name : "no bait")}", force: true);
        }

        /// <summary>
        /// A companion's cast (it has no attack input; the caller faces it along the plan first): takes one bait from
        /// <paramref name="baitFrom"/> and throws the rod's own projectile at the speed that lands it on <see cref="FishPlan.Aim"/>, with
        /// the companion as its owner; the game drops the float where it lands. "" when thrown, or the reason.
        /// </summary>
        public static string CastProjectile(Character body, FishPlan plan, Inventory baitFrom)
        {
            if (body == null || plan?.Rod == null) return "missing: no body or plan";
            Attack attack = plan.Rod.m_shared.m_attack;
            GameObject prefab = attack.m_attackProjectile;
            if (prefab == null || prefab.GetComponent<IProjectile>() == null) return "norod: the rod throws nothing";
            if (plan.Bait == null || baitFrom == null || !baitFrom.ContainsItem(plan.Bait)) return "nobait: the planned bait is not in the bag";
            Vector3 stand = body.transform.position;
            Vector3 flat = plan.Aim - stand;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1f) return "missing: standing on the cast point";
            Vector3 dir = flat.normalized;
            var (gravity, drag) = Flight(plan.Rod);
            LaunchFrom(body, plan.Rod, stand, dir, out Vector3 origin, out Vector3 launch);
            float want = flat.magnitude * Gain(body);
            float lo = Mathf.Max(1f, attack.m_projectileVelMin), hi = Mathf.Max(lo + 1f, attack.m_projectileVel);
            for (int i = 0; i < 16; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (Landing(origin, launch * mid, plan.Aim.y, stand, gravity, drag) < want) lo = mid; else hi = mid;
            }
            Vector3 velocity = launch * ((lo + hi) * 0.5f);
            ItemDrop.ItemData bait = plan.Bait;
            GameObject thrown = UnityEngine.Object.Instantiate(prefab, origin, Quaternion.LookRotation(velocity));
            HitData hit = new HitData();
            hit.SetAttacker(body);
            thrown.GetComponent<IProjectile>().Setup(body, velocity, attack.m_attackHitNoise, hit, plan.Rod, bait);
            baitFrom.RemoveOneItem(bait);
            plan.Distance = flat.magnitude;
            NoteCast(body, plan);
            return "";
        }

        // ---- Each tick once cast ----

        /// <summary>The float <paramref name="body"/> has out, or null.</summary>
        public static FishingFloat FloatOf(Character body) =>
            body == null ? null : FishingFloat.GetAllInstances().FirstOrDefault(f => f != null && f.GetOwner() == body);

        /// <summary>
        /// What <paramref name="body"/> does now with its line (<see cref="FishMove"/>): Wait (block released), Hook (a nibble: hold
        /// block now, the hook window is 0.5 s), Reel (hold block), Rest (release block: the fish is running and the line is near
        /// breaking), Recast (nothing bit in 30 s, the float is on land, or it came back empty: plan and cast again), Landed (the catch
        /// is in the bag), Lost (hooked, then gone), Failed. <paramref name="why"/> says why; the brain logs the evidence lines.
        /// </summary>
        public static FishMove Advise(Character body, out string why)
        {
            why = "";
            if (body == null) { why = "no body"; return FishMove.Failed; }
            int id = body.GetInstanceID();
            float now = Time.time;
            s_casts.TryGetValue(id, out CastState cast);
            if (cast != null && s_catches.TryGetValue(id, out var caught) && caught.time >= cast.Time)
            {
                LearnReel(body, cast, 0.5f);
                s_casts.Remove(id);
                why = $"landed {caught.fish}{(caught.quality > 1 ? $" (q{caught.quality})" : "")}";
                Say(body, why, force: true);
                return FishMove.Landed;
            }
            FishingFloat line = FloatOf(body);
            if (line == null)
            {
                if (cast == null) { why = "no float out"; return FishMove.Recast; }
                if (cast.Hooked == null && now - cast.Time < InAirFor) { why = "the cast is in the air"; return FishMove.Wait; }
                s_casts.Remove(id);
                if (cast.Hooked != null) { LearnReel(body, cast, cast.LastLine); why = $"lost {cast.Hooked} (the line broke or it got away)"; Say(body, why, force: true); return FishMove.Lost; }
                why = "the float came back empty";
                Say(body, why);
                return FishMove.Recast;
            }
            if (cast == null) { cast = new CastState { Time = now, Origin = body.transform.position }; s_casts[id] = cast; }
            if (!cast.Measured && line.IsInWater())
            {
                cast.Measured = true;
                float landed = new Vector2(line.transform.position.x - cast.Origin.x, line.transform.position.z - cast.Origin.z).magnitude;
                if (cast.Expected > 1f && landed > 1f && cast.Draw < 0.99f)
                {
                    float gain = Mathf.Clamp(Gain(body) * Mathf.Lerp(1f, cast.Expected / landed, 0.6f), 0.5f, 2f);
                    s_gain[id] = gain;
                    Say(body, $"cast landed {landed:0.0} m (aimed {cast.Expected:0.0}), gain {gain:0.00}", force: true);
                }
                else Say(body, $"cast landed {landed:0.0} m (aimed {cast.Expected:0.0})", force: true);
            }
            Fish hooked = line.GetCatch();
            if (hooked == null)
            {
                if (cast.Hooked != null)
                {
                    LearnReel(body, cast, cast.LastLine);
                    Say(body, $"lost {cast.Hooked} (it got away{(body is Player lp && !lp.HaveStamina() ? ": out of stamina" : "")})", force: true);
                    cast.Hooked = null; cast.HookStamina = -1f; cast.TautSince = -1f; cast.RunningSince = -1f; cast.Time = now;
                }
                if (line.m_nibbler != null && now - line.m_nibbleTime < NibbleWindow)
                {
                    why = $"bite: {Utils.GetPrefabName(line.m_nibbler.gameObject)} -> hook";
                    Say(body, why);
                    return FishMove.Hook;
                }
                if (!line.IsInWater() && now - cast.Time > InAirFor + 3f) { why = "the float is not in water"; Say(body, why); return FishMove.Recast; }
                if (now - cast.Time > RecastAfter) { why = $"nothing bit in {RecastAfter:0} s"; Say(body, why); return FishMove.Recast; }
                why = "waiting for a bite";
                return FishMove.Wait;
            }
            string fish = Utils.GetPrefabName(hooked.gameObject);
            if (cast.Hooked == null)
            {
                cast.Hooked = fish;
                cast.HookLine = line.m_lineLength;
                cast.HookStamina = body is Player hp ? hp.GetStamina() : -1f;
                cast.TautSince = -1f;
                float reelCost = ReelCostPerMetre(body, line);
                Say(body, $"hooked {fish}, line {line.m_lineLength:0.0} m{(body is Player sp ? $", stamina {sp.GetStamina():0}" + (reelCost > 0f ? $" (reels ~{ReelableMetres(body, line, sp.GetStamina()):0.0} m at {reelCost:0.0}/m)" : " (reel cost not learned yet)") : "")}; {FishOwner(hooked)}; {ReelNumbers(body, line, hooked)}", force: true);
            }
            cast.LastLine = line.m_lineLength;
            Transform top = RodTopOf(body, false);
            float tension = top != null ? Vector3.Distance(top.position, line.transform.position) - line.m_lineLength : 0f;
            SampleReel(body, cast, line, hooked, tension, now);
            // 0.2.263 (R44, the prefab's real numbers on the "hooked" line: pull 0/s, line 2 m/s, hooked 1/s, Fish1 3/s or 10/s escaping x
            // quality): a calm fish reels at (3 x q + 1) / 2 m/s, ~3.5 stamina/m at q2; a running one at (10 x q + 1) / 1 m/s, ~21/m. Holding
            // costs only the hooked 1/s. So never reel while the fish runs (as its owner says: EscapingNow), the human's pattern; R44 spent
            // 40 of 74 stamina on 2 m of a running fish through the 3 s taut fallback. Only an escape longer than EscapeHoldMax (a stale
            // escape state) reels on slack anyway.
            if (EscapingNow(hooked))
            {
                if (cast.RunningSince < 0f) cast.RunningSince = now;
                if (now - cast.RunningSince < EscapeHoldMax || tension >= SlackTension)
                {
                    cast.TautSince = -1f;
                    why = $"{fish} is running: holding (reeling a running fish costs ~{FishUse(hooked, true):0}/s for half the line)";
                    Say(body, why);
                    return FishMove.Rest;
                }
            }
            else cast.RunningSince = -1f;
            // Calm: reel whenever the line has slack (vanilla shortens it only then), hold while it is taut (the stamina is spent but the
            // line doesn't shorten; the float's pull takes it in), reeling anyway after TautHoldMax (the float stuck on something).
            if (tension >= SlackTension)
            {
                if (cast.TautSince < 0f) cast.TautSince = now;
                if (now - cast.TautSince < TautHoldMax)
                {
                    why = EscapingNow(hooked) ? $"{fish} is running, line taut: holding" : "line taut: holding, reeling only spends stamina";
                    Say(body, why);
                    return FishMove.Rest;
                }
            }
            else cast.TautSince = -1f;
            why = $"reeling {fish}, line {line.m_lineLength:0.0} m{(body is Player p ? $", stamina {p.GetStamina():0}" : "")}";
            Say(body, why, every: 2f);
            return FishMove.Reel;
        }

        /// <summary>
        /// Takes <paramref name="body"/>'s line in at once (a companion recasting or stopping): a hooked fish let go, an unused bait
        /// returned (a player's bag, a companion's Humanoid bag), the float gone.
        /// </summary>
        public static void Withdraw(Character body)
        {
            if (body == null) return;
            s_casts.Remove(body.GetInstanceID());
            SetReeling(body, false);
            FishingFloat line = FloatOf(body);
            if (line == null || line.m_nview == null || !line.m_nview.IsValid()) return;
            Fish hooked = line.GetCatch();
            if (hooked != null) hooked.OnHooked(null);
            line.ReturnBait();
            if (line.m_nview.IsOwner()) line.m_nview.Destroy();
            else if (ZNetScene.instance != null) ZNetScene.instance.Destroy(line.gameObject);
        }

        /// <summary>A companion holds its line in (vanilla reels while the owner blocks; a companion's block is not vanilla's).</summary>
        public static void SetReeling(Character body, bool reel)
        {
            if (body == null) return;
            if (reel) s_reeling.Add(body.GetInstanceID()); else s_reeling.Remove(body.GetInstanceID());
        }

        public static bool IsReeling(Character body) => body != null && s_reeling.Contains(body.GetInstanceID());

        // FishingFloat.FixedUpdate's "owner.IsBlocking()": a player's block, or a companion told to reel.
        public static bool OwnerReels(Character owner) => owner != null && (owner.IsBlocking() || (!(owner is Player) && IsReeling(owner)));

        /// <summary>The rod tip of <paramref name="body"/>: the rod model's "_RodTop", or for a companion whose model isn't built yet a stand-in at the rod's reach.</summary>
        public static Transform RodTopOf(Character body, bool make = true)
        {
            if (body == null) return null;
            Transform top = Utils.FindChild(body.transform, "_RodTop");
            if (top != null || !make || body is Player) return top;
            var stand = new GameObject("_RodTop");
            stand.transform.SetParent(body.transform, false);
            stand.transform.localPosition = new Vector3(0.35f, 2.0f, 1.3f);
            return stand.transform;
        }

        // ---- Lines ----

        private static string BodyName(Character body) => body is Player p ? p.GetPlayerName() : body.GetHoverName();

        private static void Say(Character body, string line, bool force = false, float every = 5f)
        {
            int id = body.GetInstanceID();
            if (!force && s_lastLine.TryGetValue(id, out var last) && last.line == line && Time.time - last.time < every) return;
            if (!force && s_lastLine.TryGetValue(id, out last) && line.StartsWith("reeling") && last.line.StartsWith("reeling") && Time.time - last.time < every) return;
            if (s_lastLine.Count > 64) s_lastLine.Clear();
            s_lastLine[id] = (line, Time.time);
            Debug.Log($"[FishingBrain] {BodyName(body)}: {line}");
        }

        private static void NoteCatch(Character owner, Fish fish)
        {
            if (owner == null || fish == null) return;
            ItemDrop drop = fish.GetComponent<ItemDrop>();
            if (s_catches.Count > 64) s_catches.Clear();
            s_catches[owner.GetInstanceID()] = (Time.time, Utils.GetPrefabName(fish.gameObject), drop != null ? drop.m_itemData.m_quality : 1);
        }

        // ---- A companion owning a float (vanilla: owners are players, found by user id) ----

        private static readonly KeyValuePair<int, int> s_ownerKey = ZDO.GetHashZDOID("fires_rodOwner");

        [HarmonyPatch(typeof(FishingFloat), nameof(FishingFloat.Setup))]
        private static class FishingFloat_Setup_Owner
        {
            // A non-player caster: its own ZDOID is the owner (its user id is its owner's player's), and the vanilla id is cleared
            // so nothing resolves the player instead.
            private static void Postfix(FishingFloat __instance, Character owner)
            {
                if (owner == null || owner is Player || __instance.m_nview == null || !__instance.m_nview.IsValid()) return;
                ZDO zdo = __instance.m_nview.GetZDO();
                zdo.Set(s_ownerKey, owner.GetZDOID());
                zdo.Set(ZDOVars.s_rodOwner, 0L);
            }
        }

        [HarmonyPatch(typeof(FishingFloat), "GetOwner")]
        private static class FishingFloat_GetOwner_Character
        {
            private static bool Prefix(FishingFloat __instance, ref Character __result)
            {
                if (__instance.m_nview == null || !__instance.m_nview.IsValid()) return true;
                ZDOID id = __instance.m_nview.GetZDO().GetZDOID(s_ownerKey);
                if (id.IsNone()) return true;
                GameObject go = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(id) : null;
                __result = go != null ? go.GetComponent<Character>() : null;
                return false;
            }
        }

        [HarmonyPatch(typeof(FishingFloat), "GetRodTop")]
        private static class FishingFloat_GetRodTop_Companion
        {
            private static bool Prefix(Character owner, ref Transform __result)
            {
                if (owner == null || owner is Player) return true;
                __result = RodTopOf(owner);
                return false;
            }
        }

        [HarmonyPatch(typeof(FishingFloat), "FixedUpdate")]
        private static class FishingFloat_FixedUpdate_Reel
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                MethodInfo isBlocking = AccessTools.Method(typeof(Character), nameof(Character.IsBlocking));
                MethodInfo reels = AccessTools.Method(typeof(FishingBrain), nameof(OwnerReels));
                int swapped = 0;
                var result = new List<CodeInstruction>();
                foreach (CodeInstruction code in instructions)
                {
                    if ((code.opcode == OpCodes.Callvirt || code.opcode == OpCodes.Call) && code.operand is MethodInfo m && m == isBlocking)
                    {
                        code.opcode = OpCodes.Call;
                        code.operand = reels;
                        swapped++;
                    }
                    result.Add(code);
                }
                if (swapped != 1) Debug.LogWarning($"[FishingBrain] FishingFloat.FixedUpdate: {swapped} IsBlocking call(s) swapped (expected 1); companions may not reel");
                return result;
            }

            // 0.2.261 (R42/R43): the hooked fish's escape runs on the fish's ZDO owner (Fish.FixedUpdate counts m_escapeTime down there and
            // writes ZDOVars.s_escape; Fish.OnHooked claims it for the hooker, but on this rig it ends up owned elsewhere). On the float's
            // owner, a fish it doesn't own keeps the m_escapeTime OnHooked set, so vanilla's reel here read it as escaping for good: half the
            // line speed and twice the fish's stamina use for the whole fight (measured 19 stamina/m). Mirror the owner's escape state from
            // the ZDO before the reel runs, for players and companions alike.
            private static void Prefix(FishingFloat __instance)
            {
                if (__instance.m_nview == null || !__instance.m_nview.IsValid() || !__instance.m_nview.IsOwner()) return;
                Fish fish = __instance.GetCatch();
                if (fish == null || fish.m_nview == null || !fish.m_nview.IsValid() || fish.m_nview.IsOwner()) return;
                fish.m_escapeTime = fish.m_nview.GetZDO().GetFloat(ZDOVars.s_escape);
            }
        }

        /// <summary>0.2.261: whether the hooked fish is in an escape now, as its ZDO owner says (Fish.IsEscaping reads a local timer only its owner runs).</summary>
        public static bool EscapingNow(Fish fish) =>
            fish != null && fish.m_nview != null && fish.m_nview.IsValid() && fish.IsHooked()
            && (fish.m_nview.IsOwner() ? fish.IsEscaping() : fish.m_nview.GetZDO().GetFloat(ZDOVars.s_escape) > 0f);

        // Who owns the hooked fish (its escape and pull run there), for the hooked line.
        private static string FishOwner(Fish fish)
        {
            if (fish == null || fish.m_nview == null || !fish.m_nview.IsValid()) return "fish: no ZDO";
            if (fish.m_nview.IsOwner()) return "fish owned here";
            long owner = fish.m_nview.GetZDO().GetOwner();
            return owner == 0L ? "fish owned by nobody" : ZNet.instance != null && ZNet.instance.IsServer() ? $"fish owned by peer {owner}" : $"fish owned by peer {owner} (not this one: escape mirrored from its ZDO)";
        }

        [HarmonyPatch(typeof(FishingFloat), "ReturnBait")]
        private static class FishingFloat_ReturnBait_Companion
        {
            // Vanilla gives an unused bait back to players only; a companion gets it in its Humanoid bag (its behaviour moves it on).
            private static bool Prefix(FishingFloat __instance)
            {
                if (__instance.m_baitConsumed) return true;
                if (!(__instance.GetOwner() is Humanoid owner) || owner is Player) return true;
                GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(__instance.GetBait()) : null;
                if (prefab != null) owner.GetInventory().AddItem(prefab, 1);
                return false;
            }
        }

        [HarmonyPatch(typeof(FishingFloat), nameof(FishingFloat.Catch))]
        private static class FishingFloat_Catch_Note
        {
            // Every catch is noted for Advise's Landed. A companion's catch goes into its Humanoid bag the vanilla way (ItemDrop.Pickup,
            // which handles a fish another peer owns) without counting toward the local player's catch stats.
            private static bool Prefix(Fish fish, Character owner, ref string __result)
            {
                NoteCatch(owner, fish);
                if (fish == null || !(owner is Humanoid body) || owner is Player) return true;
                ItemDrop drop = fish.GetComponent<ItemDrop>();
                if (drop != null) drop.Pickup(body); else fish.Pickup(body);
                string text = "$msg_fishing_catched " + fish.GetHoverName();
                if (!fish.m_extraDrops.IsEmpty())
                    foreach (ItemDrop.ItemData extra in fish.m_extraDrops.GetDropListItems())
                    {
                        text = $"{text} & {extra.m_shared.m_name}";
                        if (body.GetInventory().CanAddItem(extra.m_dropPrefab, extra.m_stack)) body.GetInventory().AddItem(extra.m_dropPrefab, extra.m_stack);
                        else UnityEngine.Object.Instantiate(extra.m_dropPrefab, fish.transform.position, Quaternion.identity).GetComponent<ItemDrop>()?.SetStack(extra.m_stack);
                    }
                __result = text;
                return false;
            }
        }
    }
}
