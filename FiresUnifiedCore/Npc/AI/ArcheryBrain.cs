using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Npc.IdleBehaviors;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// The one archery-practice brain for every body (Core 0.2.244; Fire: "we shouldnt skip bow training, its useful right?"). Picks
    /// a vanilla ArcheryTarget near base, a clear firing spot 6-15 m in front of it, the bow and the arrows (real ones: every shot
    /// uses one; the target hands back the ones that hit when interacted with, at its return point), how many shots, and when to stop
    /// (shots done, out of arrows, low stamina, the target gone). Skill comes from the game (ArcheryTarget.OnProjectileHit raises the
    /// shooter's Bows skill by accuracy). The FDT bot shoots with its real draw-and-release input; companions shoot through
    /// <see cref="Shoot"/>.
    /// </summary>
    public static class ArcheryBrain
    {
        /// <summary>A practice session (<see cref="Plan(Character, Inventory, ItemDrop.ItemData, Vector3, float, Func{Vector3, bool}, out Practice)"/>).</summary>
        public sealed class Practice
        {
            public ArcheryTarget Target;
            /// <summary>The bullseye (the target's m_center).</summary>
            public Vector3 Center;
            /// <summary>Where to stand: ground, dry, in front of the target with a clear line to <see cref="Center"/>.</summary>
            public Vector3 Spot;
            public float Distance;
            public ItemDrop.ItemData Bow, Arrows;
            /// <summary>Shots planned (never more than the arrows carried).</summary>
            public int Shots;
            /// <summary>Where <see cref="CollectArrows"/> drops the arrows that hit.</summary>
            public Vector3 ReturnPoint;
            public string TargetName;
            internal int HitsAtStart, PointsAtStart;

            public override string ToString() =>
                $"{TargetName} at ({Center.x:0}, {Center.z:0}) from ({Spot.x:0}, {Spot.z:0}) {Distance:0.0} m: {Shots} shot(s) with " +
                $"{(Arrows?.m_dropPrefab != null ? Arrows.m_dropPrefab.name : "?")}";
        }

        public const float MinDistance = 6f;
        public const float BestDistance = 10f;
        public const float MaxDistance = 15f;
        public const int MinShots = 5;
        public const int MaxShots = 10;
        /// <summary>A player rests below this share of its stamina (a shot's draw costs stamina).</summary>
        public const float RestStamina = 0.25f;
        /// <summary>A bullseye further than this under the terrain is buried (the target was not placed the way a player places it).</summary>
        public const float BuriedMetres = 0.25f;
        private static int s_losMask, s_terrainMask;

        // ---- Items ----

        /// <summary>A bow that trains the Bows skill (crossbows are ItemType Bow too, but train Crossbows).</summary>
        public static bool IsTrainingBow(ItemDrop.ItemData item) =>
            item?.m_shared != null && item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow && item.m_shared.m_skillType == Skills.SkillType.Bows;

        /// <summary>A training bow in <paramref name="bag"/>, or null.</summary>
        public static ItemDrop.ItemData BowIn(Inventory bag) => bag?.GetAllItems().FirstOrDefault(IsTrainingBow);

        /// <summary>
        /// The arrows to practice with from <paramref name="bag"/>: the bow's ammo type, preferring one <paramref name="target"/> hands
        /// back (its m_returnAmmo), then the biggest stack; null when none is carried.
        /// </summary>
        public static ItemDrop.ItemData ArrowsFor(Inventory bag, ItemDrop.ItemData bow, ArcheryTarget target = null)
        {
            if (bag == null || bow == null || string.IsNullOrEmpty(bow.m_shared.m_ammoType)) return null;
            var arrows = bag.GetAllItems().Where(i => i != bow && i.m_shared.m_ammoType == bow.m_shared.m_ammoType && i.m_dropPrefab != null).ToList();
            if (arrows.Count == 0) return null;
            HashSet<string> returned = target != null
                ? new HashSet<string>(target.m_returnAmmo.Where(r => r != null).Select(r => r.gameObject.name))
                : new HashSet<string>();
            return arrows.OrderByDescending(a => returned.Contains(a.m_dropPrefab.name)).ThenByDescending(a => bag.CountItems(a.m_shared.m_name)).First();
        }

        /// <summary>The vanilla archery targets within <paramref name="radius"/> of <paramref name="center"/>, nearest first.</summary>
        public static List<ArcheryTarget> TargetsNear(Vector3 center, float radius)
        {
            float r2 = radius * radius;
            return UnityEngine.Object.FindObjectsByType<ArcheryTarget>(FindObjectsSortMode.None)
                .Where(t => t != null && (CenterOf(t) - center).sqrMagnitude <= r2)
                .OrderBy(t => (CenterOf(t) - center).sqrMagnitude).ToList();
        }

        private static Vector3 CenterOf(ArcheryTarget target) => target.m_center != null ? target.m_center.transform.position : target.transform.position;

        // ---- Planning ----

        /// <summary><see cref="Plan(Character, Inventory, ItemDrop.ItemData, Vector3, float, Func{Vector3, bool}, out Practice)"/> for a body whose own bag holds the bow and arrows (the bot).</summary>
        public static string Plan(Humanoid body, Vector3 near, float radius, Func<Vector3, bool> spotOk, out Practice plan)
        {
            Inventory bag = body != null ? body.GetInventory() : null;
            return Plan(body, bag, BowIn(bag), near, radius, spotOk, out plan);
        }

        /// <summary>
        /// A practice session for <paramref name="body"/> at the nearest archery target within <paramref name="radius"/> of
        /// <paramref name="near"/> with a clear firing spot (<paramref name="spotOk"/> = the caller's reach test, null = all), the
        /// <paramref name="bow"/> and arrows from <paramref name="bag"/>. "" with <paramref name="plan"/>, or a coded reason:
        /// "nobow: …", "noarrows: …", "notarget: …", "nospot: …".
        /// </summary>
        public static string Plan(Character body, Inventory bag, ItemDrop.ItemData bow, Vector3 near, float radius, Func<Vector3, bool> spotOk, out Practice plan)
        {
            plan = null;
            if (body == null || bag == null) return "missing: no body or bag";
            if (!IsTrainingBow(bow)) return "nobow: no bow (Bows skill)";
            List<ArcheryTarget> targets = TargetsNear(near, radius);
            if (targets.Count == 0) return $"notarget: no archery target within {radius:0} m of ({near.x:0}, {near.z:0})";
            if (ArrowsFor(bag, bow) == null) return $"noarrows: no {bow.m_shared.m_ammoType} for {Utils.GetPrefabName(bow.m_dropPrefab)}";
            if (s_losMask == 0) s_losMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle");
            string refusal = "";
            foreach (ArcheryTarget target in targets.Take(4))
            {
                Vector3 center = CenterOf(target);
                // R33 (0.2.245): a target spawned with its pivot on the ground (a player's placement lifts it so its lowest collider
                // point sits there) stands half buried; its bullseye is in the dirt and every arrow aimed at it lands short.
                if (TerrainHeight(center, out float terrain) && terrain - center.y > BuriedMetres)
                {
                    refusal = $"the bullseye of the target at ({center.x:0}, {center.z:0}) is {terrain - center.y:0.0} m under the ground";
                    continue;
                }
                if (!FindSpot(target, center, spotOk, out Vector3 spot, out refusal)) continue;
                ItemDrop.ItemData arrows = ArrowsFor(bag, bow, target);
                int carried = bag.CountItems(arrows.m_shared.m_name);
                ZDO zdo = target.m_nview != null && target.m_nview.IsValid() ? target.m_nview.GetZDO() : null;
                plan = new Practice
                {
                    Target = target,
                    Center = center,
                    Spot = spot,
                    Distance = new Vector2(center.x - spot.x, center.z - spot.z).magnitude,
                    Bow = bow,
                    Arrows = arrows,
                    Shots = Mathf.Min(UnityEngine.Random.Range(MinShots, MaxShots + 1), carried),
                    ReturnPoint = target.m_returnPoint != null ? target.m_returnPoint.transform.position : target.transform.position,
                    TargetName = string.IsNullOrEmpty(target.m_name) ? Utils.GetPrefabName(target.transform.root.gameObject) : Localization.instance.Localize(target.m_name),
                    HitsAtStart = zdo != null ? zdo.GetInt(ZDOVars.s_hitPoint) : 0,
                    PointsAtStart = zdo != null ? zdo.GetInt(ZDOVars.s_dataCount) : 0,
                };
                Say(body, $"practice: {plan}");
                return "";
            }
            return $"nospot: {refusal} (nearest target at ({CenterOf(targets[0]).x:0}, {CenterOf(targets[0]).z:0}))";
        }

        // The terrain alone under (x, z), from above the point (the target's own board is a piece and must not count as ground).
        private static bool TerrainHeight(Vector3 at, out float height)
        {
            if (s_terrainMask == 0) s_terrainMask = LayerMask.GetMask("terrain");
            if (Physics.Raycast(new Vector3(at.x, at.y + 40f, at.z), Vector3.down, out RaycastHit hit, 120f, s_terrainMask, QueryTriggerInteraction.Ignore))
            {
                height = hit.point.y;
                return true;
            }
            height = 0f;
            return ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(at, out height);
        }

        // A firing spot in front of the target (the side its face looks to, as the companions shot from), 10 m first, then nearer and
        // farther and off to the sides: ground, dry, the bullseye in clear sight.
        private static bool FindSpot(ArcheryTarget target, Vector3 center, Func<Vector3, bool> spotOk, out Vector3 spot, out string refusal)
        {
            spot = default;
            refusal = "no clear firing spot 6-15 m in front of the target";
            Vector3 face = -target.transform.forward;
            face.y = 0f;
            if (face.sqrMagnitude < 0.01f) face = Vector3.forward;
            face.Normalize();
            float[] distances = { BestDistance, 8f, 12f, 14f, MinDistance + 1f };
            float[] angles = { 0f, 15f, -15f, 30f, -30f };
            int asked = 0;
            foreach (float d in distances)
                foreach (float a in angles)
                {
                    Vector3 p = center + Quaternion.Euler(0f, a, 0f) * face * d;
                    if (!FiresCore.World.Surface.GroundNear(p, out float ground)) continue;
                    p.y = ground;
                    if (FishingBrain.WaterAt(p, out _, out float depth) && depth > 0.3f) { refusal = "the spots in front of the target are in water"; continue; }
                    // The bullseye in clear sight: nothing in the way, or the first thing hit is this target's board within its scoring
                    // disc (an arrow scores only on the target's own collider, Projectile.OnHit; R33 aimed past the board's top edge).
                    Vector3 eye = p + Vector3.up * 1.5f;
                    if (Physics.Linecast(eye, center, out RaycastHit hit, s_losMask) && hit.distance < Vector3.Distance(eye, center) - 0.6f
                        && !(hit.collider.GetComponentInParent<ArcheryTarget>() == target && Vector3.Distance(hit.point, center) <= Mathf.Max(0.3f, target.m_targetSize)))
                    { refusal = "the line to the bullseye is blocked from every spot"; continue; }
                    if (spotOk != null)
                    {
                        if (++asked > 8) return false;
                        if (!spotOk(p)) { refusal = "no reachable firing spot"; continue; }
                    }
                    spot = p;
                    return true;
                }
            return false;
        }

        // ---- Shooting ----

        /// <summary>
        /// Why <paramref name="body"/> stops shooting now, or "": "done: N shots", "out of arrows", "low stamina", "the target is gone".
        /// Counted from the bag (<paramref name="bag"/> null = the body's own).
        /// </summary>
        public static string ShouldStop(Character body, Practice plan, Inventory bag, int fired)
        {
            if (plan == null || plan.Target == null) return "the target is gone";
            if (fired >= plan.Shots) return $"done: {fired} shot(s)";
            bag = bag ?? (body as Humanoid)?.GetInventory();
            if (bag == null || plan.Arrows == null || bag.CountItems(plan.Arrows.m_shared.m_name) <= 0) return "out of arrows";
            if (body is Player p && p.GetStamina() < p.GetMaxStamina() * RestStamina) return "low stamina";
            return "";
        }

        /// <summary>The bot's shot was released (its archery verb calls this after letting go of attack): the evidence line.</summary>
        public static void NoteShot(Character body, Practice plan, int shot)
        {
            if (body == null || plan == null) return;
            Say(body, $"shot {shot}/{plan.Shots} at {plan.TargetName} from {Vector3.Distance(body.transform.position, plan.Center):0.0} m");
        }

        /// <summary>
        /// A companion's shot (no attack input; the caller faces it at the target and plays the draw): one arrow from
        /// <paramref name="bag"/>, its projectile at the bow's full-draw speed on the arc that reaches the bullseye (the projectile's own
        /// gravity), the bow and arrow passed on so the target counts the arrow for its return and raises the skill. "" or the reason.
        /// </summary>
        public static string Shoot(Character body, Practice plan, Inventory bag, int shot)
        {
            if (body == null || plan?.Bow == null || plan.Arrows == null) return "missing: no plan";
            if (bag == null || !bag.ContainsItem(plan.Arrows))
            {
                ItemDrop.ItemData again = ArrowsFor(bag, plan.Bow, plan.Target);
                if (again == null) return "out of arrows";
                plan.Arrows = again;
            }
            GameObject prefab = plan.Arrows.m_shared.m_attack.m_attackProjectile ?? plan.Bow.m_shared.m_attack.m_attackProjectile;
            Projectile projectile = prefab != null ? prefab.GetComponent<Projectile>() : null;
            if (projectile == null) return "missing: the arrow has no projectile";
            Vector3 from = body.transform.position + Vector3.up * 1.5f + body.transform.forward * 0.3f;
            float speed = Mathf.Max(10f, plan.Bow.m_shared.m_attack.m_projectileVel + plan.Arrows.m_shared.m_attack.m_projectileVel);
            Vector3 velocity = AimVelocity(from, plan.Center, speed, projectile.m_gravity);
            GameObject thrown = UnityEngine.Object.Instantiate(prefab, from, Quaternion.LookRotation(velocity));
            Projectile arrow = thrown.GetComponent<Projectile>();
            HitData hit = new HitData();
            hit.m_damage.m_pierce = 1f;
            hit.m_skill = Skills.SkillType.Bows;
            hit.SetAttacker(body);
            arrow.m_skill = Skills.SkillType.Bows;
            arrow.m_raiseSkillAmount = 1f;
            arrow.Setup(body, velocity, 0f, hit, plan.Bow, plan.Arrows);
            bag.RemoveOneItem(plan.Arrows);
            NoteShot(body, plan, shot);
            return "";
        }

        /// <summary>
        /// The low arc from <paramref name="from"/> that reaches <paramref name="to"/> at <paramref name="speed"/> under
        /// <paramref name="gravity"/> (the vanilla projectile's own; drag ignored, small for arrows); 45 degrees when out of reach.
        /// </summary>
        public static Vector3 AimVelocity(Vector3 from, Vector3 to, float speed, float gravity)
        {
            Vector3 flat = to - from;
            float h = flat.y;
            flat.y = 0f;
            float d = flat.magnitude;
            Vector3 dir = d > 0.01f ? flat / d : Vector3.forward;
            if (gravity <= 0.001f || d < 0.01f) return (to - from).normalized * speed;
            float v2 = speed * speed;
            float disc = v2 * v2 - gravity * (gravity * d * d + 2f * h * v2);
            float angle = disc < 0f ? Mathf.PI / 4f : Mathf.Atan((v2 - Mathf.Sqrt(disc)) / (gravity * d));
            return (dir * Mathf.Cos(angle) + Vector3.up * Mathf.Sin(angle)) * speed;
        }

        // ---- After ----

        /// <summary>
        /// Interacts with the target the way a player does (ArcheryTarget.Interact): the arrows that hit drop at its return point for
        /// the body to pick up. Returns how many it holds; logs the session's hits and points (the target's own count).
        /// </summary>
        public static int CollectArrows(Humanoid body, Practice plan)
        {
            if (body == null || plan?.Target == null) return 0;
            ArcheryTarget target = plan.Target;
            ZDO zdo = target.m_nview != null && target.m_nview.IsValid() ? target.m_nview.GetZDO() : null;
            int held = 0;
            if (zdo != null)
                for (int i = 0; i < target.m_returnAmmo.Count; i++) held += zdo.GetInt(ZDOVars.s_ammoType + i);
            int hits = zdo != null ? zdo.GetInt(ZDOVars.s_hitPoint) - plan.HitsAtStart : 0;
            int points = zdo != null ? zdo.GetInt(ZDOVars.s_dataCount) - plan.PointsAtStart : 0;
            target.Interact(body, false, false);
            Say(body, $"collect: {plan.TargetName}: {hits} hit(s), {points} point(s) this session; {held} arrow(s) dropped at ({plan.ReturnPoint.x:0}, {plan.ReturnPoint.z:0})");
            ChoreBrain.ChoreDone(BodyName(body), "archery", $"{plan.TargetName}: {hits} hit(s), {points} point(s), {held} arrow(s) back");
            return held;
        }

        private static string BodyName(Character body) => body is Player p ? p.GetPlayerName() : body.GetHoverName();

        private static void Say(Character body, string line) => Debug.Log($"[ArcheryBrain] {BodyName(body)}: {line}");
    }
}
