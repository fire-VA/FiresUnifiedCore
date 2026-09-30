using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>How far and how wide a body senses (Fire, 2026-09-29: "bots need a proper vision cone like monster ai gets").</summary>
    public struct Senses
    {
        public float ViewRange;
        /// <summary>Degrees either side of forward, as BaseAI.m_viewAngle.</summary>
        public float ViewAngle;
        public float HearRange;
        /// <summary>Seconds a sensed character stays known after the last sight or sound ("spotted").</summary>
        public float Memory;
        public bool MistVision;

        /// <summary>A companion's own BaseAI senses (CompanionController: aggro range, 120 degrees, 1.5 x aggro range hearing).</summary>
        public static Senses Of(BaseAI ai) => new Senses
        {
            ViewRange = ai.m_viewRange,
            ViewAngle = ai.m_viewAngle,
            HearRange = ai.m_hearRange,
            Memory = 5f,
            MistVision = ai.m_mistVision,
        };

        /// <summary>A player body (the FDT bot): 40 m, 60 degrees either side of the look direction, 30 m hearing.</summary>
        public static readonly Senses Player = new Senses { ViewRange = 40f, ViewAngle = 60f, HearRange = 30f, Memory = 5f };
    }

    /// <summary>One solid thing near a body (<see cref="Perception.Obstacles"/>).</summary>
    public struct Obstacle
    {
        public Collider Collider;
        public Bounds Bounds;
        /// <summary>The obstacle's surface point nearest the body (knee height).</summary>
        public Vector3 Closest;
        /// <summary>Flat distance from the body's feet to <see cref="Closest"/>.</summary>
        public float Distance;
        /// <summary>Flat unit direction from the body to <see cref="Closest"/>.</summary>
        public Vector3 Direction;
        /// <summary>How high its top is above the body's feet.</summary>
        public float Height;
        /// <summary>Low enough to walk over (<see cref="Perception.StepHeight"/>).</summary>
        public bool Steppable;
        /// <summary>Low enough to jump onto (<see cref="Perception.JumpHeight"/>).</summary>
        public bool Jumpable;

        public override string ToString() =>
            $"{(Collider != null ? Collider.name : "?")} {Distance:0.0} m, {Height:0.0} m high{(Steppable ? ", step" : Jumpable ? ", jump" : "")}";
    }

    /// <summary>One character a body knows about.</summary>
    public sealed class Percept
    {
        public Character Target;
        public bool Seen;
        public bool Heard;
        public float Distance;
        /// <summary>Degrees off the body's forward when last sensed.</summary>
        public float Angle;
        public Vector3 LastPosition;
        public float LastSensed;

        /// <summary>Sensed at the latest look, not only remembered.</summary>
        public bool Fresh;
    }

    /// <summary>
    /// Sight and hearing for any body, the way monster AI senses (BaseAI.CanSeeTarget / CanHearTarget): a view range and cone off the
    /// body's forward (ignored while alerted), shrunk by the target's stealth, a line of sight through the same blocking layers, mist,
    /// and hearing a target whose own noise reaches the body (capped at 12 m indoors). What was sensed is remembered for
    /// <see cref="Senses.Memory"/> seconds. A body must not react to anything it has not perceived. <see cref="Changed"/> reports
    /// each "saw" / "heard" / "lost" so a trace can log it.
    /// </summary>
    public static class Perception
    {
        // BaseAI's view block layers.
        private static int s_viewBlockMask;

        private static int ViewBlockMask
        {
            get
            {
                if (s_viewBlockMask == 0)
                    s_viewBlockMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "viewblock", "vehicle");
                return s_viewBlockMask;
            }
        }

        private sealed class Mind
        {
            public readonly Dictionary<Character, Percept> Known = new Dictionary<Character, Percept>();
            public readonly List<Percept> Snapshot = new List<Percept>();
            public float LastScan = -100f;
        }

        private static readonly Dictionary<Character, Mind> s_minds = new Dictionary<Character, Mind>();
        private static readonly List<Character> s_nearby = new List<Character>();
        private static readonly List<Character> s_expired = new List<Character>();

        /// <summary>(body, percept, "saw" | "heard" | "lost") when a body first senses something, or forgets it.</summary>
        public static event Action<Character, Percept, string> Changed;

        /// <summary>
        /// Everything <paramref name="self"/> senses or still remembers, looking from <paramref name="eye"/> along
        /// <paramref name="forward"/>. Rescans at most every <paramref name="interval"/> seconds; in between the last list comes back.
        /// </summary>
        public static IReadOnlyList<Percept> Perceive(Character self, Vector3 eye, Vector3 forward, Senses senses, bool alerted, float interval = 0.25f)
        {
            Mind mind = MindOf(self);
            if (mind == null) return Array.Empty<Percept>();
            if (Time.time - mind.LastScan < interval) return mind.Snapshot;
            mind.LastScan = Time.time;

            foreach (var percept in mind.Known.Values) percept.Fresh = false;

            s_nearby.Clear();
            Character.GetCharactersInRange(self.transform.position, Mathf.Max(senses.ViewRange, senses.HearRange), s_nearby);
            foreach (Character other in s_nearby)
            {
                if (other == null || other == self || other.IsDead()) continue;
                Sense(self, mind, other, eye, forward, senses, alerted);
            }
            s_nearby.Clear();

            Expire(self, mind, senses);
            mind.Snapshot.Clear();
            mind.Snapshot.AddRange(mind.Known.Values);
            return mind.Snapshot;
        }

        /// <summary>
        /// Whether <paramref name="self"/> senses <paramref name="target"/> now, or sensed it within the memory window. Checks this one
        /// target (for a body that already has its candidates, e.g. a companion's target scan).
        /// </summary>
        public static bool Knows(Character self, Character target, Vector3 eye, Vector3 forward, Senses senses, bool alerted)
        {
            if (target == null || target == self) return false;
            Mind mind = MindOf(self);
            if (mind == null) return false;
            if (Sense(self, mind, target, eye, forward, senses, alerted)) return true;
            return mind.Known.TryGetValue(target, out Percept known) && Time.time - known.LastSensed <= senses.Memory;
        }

        /// <summary>The body's remembered percept of <paramref name="target"/>, or null.</summary>
        public static Percept Recall(Character self, Character target)
        {
            if (self == null || target == null) return null;
            return s_minds.TryGetValue(self, out Mind mind) && mind.Known.TryGetValue(target, out Percept percept) ? percept : null;
        }

        /// <summary>
        /// A look direction turned from <paramref name="current"/> toward <paramref name="desired"/> by at most
        /// <paramref name="degreesPerSecond"/> x <paramref name="dt"/>: a body turns its head (a bot its camera) smoothly, never snapping.
        /// </summary>
        public static Vector3 TurnToward(Vector3 current, Vector3 desired, float dt, float degreesPerSecond = 240f)
        {
            if (desired.sqrMagnitude < 0.0001f) return current;
            if (current.sqrMagnitude < 0.0001f) return desired.normalized;
            return Vector3.RotateTowards(current.normalized, desired.normalized, degreesPerSecond * Mathf.Deg2Rad * dt, 0f);
        }

        /// <summary>The perceived character <paramref name="self"/> should look at: the closest one seen or heard, else null.</summary>
        public static Percept Focus(Character self)
        {
            if (self == null || !s_minds.TryGetValue(self, out Mind mind)) return null;
            Percept best = null;
            foreach (Percept percept in mind.Known.Values)
            {
                if (percept.Target == null) continue;
                if (best == null || (percept.Fresh && !best.Fresh) || (percept.Fresh == best.Fresh && percept.Distance < best.Distance))
                    best = percept;
            }
            return best;
        }

        // ---- Obstacles (Fire, 2026-09-29: "they need to be able to register what is around them for objects in order to
        // properly avoid them"; R58 the bot stuck on a rock) ----

        /// <summary>A body steps over anything this high without jumping.</summary>
        public const float StepHeight = 0.45f;
        /// <summary>A body can jump onto anything up to this high.</summary>
        public const float JumpHeight = 1.1f;

        // BaseAI's solid layers, without terrain (the ground itself is not an obstacle; its slopes are the navigator's).
        private static int s_obstacleMask;
        internal static int ObstacleMask
        {
            get
            {
                if (s_obstacleMask == 0)
                    s_obstacleMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "vehicle");
                return s_obstacleMask;
            }
        }

        private static readonly Collider[] s_overlap = new Collider[64];
        private static readonly List<Obstacle> s_obstacles = new List<Obstacle>();

        /// <summary>
        /// The solid things within <paramref name="radius"/> of <paramref name="body"/>'s feet (rocks, stumps, trees, pieces, carts;
        /// never characters or the terrain), nearest first, each with its bounds, how high it rises above the body's feet and whether
        /// the body can step over it or jump onto it. The list is reused: copy it to keep it.
        /// </summary>
        public static IReadOnlyList<Obstacle> Obstacles(Character body, float radius = 4f)
        {
            s_obstacles.Clear();
            if (body == null) return s_obstacles;
            Vector3 feet = body.transform.position;
            int count = Physics.OverlapSphereNonAlloc(feet + Vector3.up * 0.5f, radius, s_overlap, ObstacleMask, QueryTriggerInteraction.Ignore);
            Collider own = body.GetCollider();
            for (int i = 0; i < count; i++)
            {
                Collider collider = s_overlap[i];
                if (collider == null || collider == own || collider.transform.IsChildOf(body.transform)) continue;
                if (collider.attachedRigidbody != null && collider.attachedRigidbody.GetComponent<Character>() != null) continue;
                Bounds bounds = collider.bounds;
                // ClosestPoint only on primitive / convex colliders: a concave mesh (voxel terrain, rocks, many pieces) answers with the
                // query point itself, which read every such obstacle as touching the body (R69, [visual]: the Unity warning spam).
                Vector3 probe = feet + Vector3.up * 0.5f;
                Vector3 closest = !(collider is MeshCollider mesh) || mesh.convex ? collider.ClosestPoint(probe) : bounds.ClosestPoint(probe);
                Vector3 flat = new Vector3(closest.x - feet.x, 0f, closest.z - feet.z);
                float height = bounds.max.y - feet.y;
                if (height <= 0.05f) continue;
                s_obstacles.Add(new Obstacle
                {
                    Collider = collider,
                    Bounds = bounds,
                    Closest = closest,
                    Distance = flat.magnitude,
                    Direction = flat.sqrMagnitude > 0.0001f ? flat.normalized : Vector3.zero,
                    Height = height,
                    Steppable = height <= StepHeight,
                    Jumpable = height <= JumpHeight,
                });
            }
            s_obstacles.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            return s_obstacles;
        }

        /// <summary>Drops a body's memory (it died, despawned or handed back).</summary>
        public static void Forget(Character self)
        {
            if (self != null) s_minds.Remove(self);
        }

        /// <summary>BaseAI's sight test from any eye point and forward.</summary>
        public static bool CanSee(Vector3 eye, Vector3 forward, Senses senses, bool alerted, Character target, out float distance, out float angle)
        {
            distance = 0f;
            angle = 0f;
            if (target == null) return false;
            if (target is Player player && (player.InDebugFlyMode() || player.InGhostMode())) return false;

            Vector3 to = target.transform.position - eye;
            distance = to.magnitude;
            if (distance > senses.ViewRange) return false;
            if (distance > senses.ViewRange * target.GetStealthFactor()) return false;

            Vector3 flatTo = new Vector3(to.x, 0f, to.z);
            Vector3 flatForward = new Vector3(forward.x, 0f, forward.z);
            angle = flatTo.sqrMagnitude > 0.0001f && flatForward.sqrMagnitude > 0.0001f ? Vector3.Angle(flatTo, flatForward) : 0f;
            if (!alerted && angle > senses.ViewAngle) return false;

            Vector3 aim = target.IsCrouching() || target.m_eye == null ? target.GetCenterPoint() : target.m_eye.position;
            Vector3 ray = aim - eye;
            if (Physics.Raycast(eye, ray.normalized, ray.magnitude, ViewBlockMask)) return false;
            if (!senses.MistVision && ParticleMist.IsMistBlocked(eye, aim)) return false;
            return true;
        }

        /// <summary>BaseAI's hearing test: the target's own noise reaches the body, within its hearing range (12 m indoors).</summary>
        public static bool CanHear(Character self, Senses senses, Character target, out float distance)
        {
            distance = 0f;
            if (self == null || target == null) return false;
            if (target is Player player && (player.InDebugFlyMode() || player.InGhostMode())) return false;

            distance = Vector3.Distance(target.transform.position, self.transform.position);
            float range = Character.InInterior(self.transform) ? Mathf.Min(12f, senses.HearRange) : senses.HearRange;
            return distance <= range && distance < target.GetNoiseRange();
        }

        private static Mind MindOf(Character self)
        {
            if (self == null) return null;
            if (!s_minds.TryGetValue(self, out Mind mind))
            {
                if (s_minds.Count > 64) Prune();
                s_minds[self] = mind = new Mind();
            }
            return mind;
        }

        private static bool Sense(Character self, Mind mind, Character target, Vector3 eye, Vector3 forward, Senses senses, bool alerted)
        {
            bool seen = CanSee(eye, forward, senses, alerted, target, out float distance, out float angle);
            bool heard = !seen && CanHear(self, senses, target, out distance);
            if (!seen && !heard) return false;

            bool isNew = !mind.Known.TryGetValue(target, out Percept percept);
            if (isNew) mind.Known[target] = percept = new Percept { Target = target };
            bool wasSeen = percept.Seen;
            percept.Seen = seen;
            percept.Heard = heard;
            percept.Distance = distance;
            percept.Angle = angle;
            percept.LastPosition = target.transform.position;
            percept.LastSensed = Time.time;
            percept.Fresh = true;
            if (isNew || (seen && !wasSeen)) Changed?.Invoke(self, percept, seen ? "saw" : "heard");
            return true;
        }

        private static void Expire(Character self, Mind mind, Senses senses)
        {
            s_expired.Clear();
            foreach (var pair in mind.Known)
            {
                if (pair.Key == null || pair.Key.IsDead() || Time.time - pair.Value.LastSensed > senses.Memory)
                    s_expired.Add(pair.Key);
            }
            foreach (Character gone in s_expired)
            {
                Percept percept = mind.Known[gone];
                mind.Known.Remove(gone);
                if (gone != null) Changed?.Invoke(self, percept, "lost");
            }
            s_expired.Clear();
        }

        // Bodies that are gone (Unity-null) leave their minds behind; clear them out when the table grows.
        private static void Prune()
        {
            s_expired.Clear();
            foreach (Character body in s_minds.Keys)
                if (body == null) s_expired.Add(body);
            foreach (Character body in s_expired) s_minds.Remove(body);
            s_expired.Clear();
        }
    }
}
