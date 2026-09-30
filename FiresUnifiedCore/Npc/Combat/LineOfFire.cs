using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>What <see cref="LineOfFire.Shot"/> found in a bow shot's lane.</summary>
    public struct LaneBlock
    {
        /// <summary>Something other than the target is in the way.</summary>
        public bool Blocked;
        /// <summary>It belongs to the shooter's side: its own piece, a companion, a player, a tame.</summary>
        public bool Friendly;
        /// <summary>For the log: "piece_workbench (own piece)", "Liv the Swift (companion)", "terrain" …</summary>
        public string Blocker;
        /// <summary>Where the lane meets it.</summary>
        public Vector3 Point;
    }

    /// <summary>
    /// Is a shot from a shooter to its target clear? (Fire 2026-09-29: the bot shot an enemy through its own workbench and
    /// damaged it, "they need to understand line of sight".) Blocked by any solid on the flight (a piece, own or not, terrain, a
    /// rock or tree: <see cref="ArcCheck.Mask"/>) and by any character that is not an enemy of the shooter (a companion, a
    /// player, a tame) in the lane. <see cref="Shot"/> traces the real arrow: vanilla's spawn point for the bow's attack, its
    /// full-draw speed with the ammo, the projectile's gravity, the low ballistic launch that hits the target's centre. One
    /// brain: CombatAdvisor holds a ranged body's release on it, and the FDT bot's bow and hunt ask it too.
    /// </summary>
    public static class LineOfFire
    {
        /// <summary>Half-width of the lane a projectile needs past other characters (m).</summary>
        public const float LaneRadius = 0.3f;

        private static int s_characterMask;
        private static readonly RaycastHit[] s_hits = new RaycastHit[16];

        private static int CharacterMask => s_characterMask != 0 ? s_characterMask
            : (s_characterMask = LayerMask.GetMask("character", "character_net", "character_ghost", "character_noenv"));

        /// <summary>
        /// The lane of a full-draw shot from <paramref name="self"/> with <paramref name="bow"/> (null = the weapon in hand) at
        /// <paramref name="target"/>'s centre. Solids along the ballistic arc, then characters along the straight line.
        /// </summary>
        public static LaneBlock Shot(Humanoid self, ItemDrop.ItemData bow, Character target)
        {
            var lane = new LaneBlock();
            if (self == null || target == null) return lane;
            bow = bow ?? self.GetCurrentWeapon();
            Vector3 spawn = SpawnPoint(self, bow);
            Vector3 aim = target.GetCenterPoint();
            Ballistics(self, bow, out float speed, out float gravity);
            Vector3 launch = LaunchDirection(spawn, aim, speed, gravity);

            if (speed > 0f)
            {
                ArcBlock arc = ArcCheck.Blocked(spawn, launch, speed, gravity, aim);
                if (arc.Blocked && arc.Blocker != null && arc.Blocker.GetComponentInParent<Character>() != target)
                    return Solid(self, arc.Blocker, arc.Point);
            }
            else if (Physics.Linecast(spawn, aim, out RaycastHit solid, ArcCheck.Mask, QueryTriggerInteraction.Ignore)
                     && solid.distance < Vector3.Distance(spawn, aim) - ArcCheck.TargetSlack
                     && solid.collider.GetComponentInParent<Character>() != target)
            {
                return Solid(self, solid.collider, solid.point);
            }
            return People(self, target, spawn, aim);
        }

        /// <summary>The same test from any point, straight line only (a spell, a thrown thing, a planner's "could I shoot from here").</summary>
        public static bool Clear(Character shooter, Vector3 from, Character target, out string blocker)
        {
            blocker = null;
            if (shooter == null || target == null) return true;
            Vector3 aim = target.GetCenterPoint();
            float length = Vector3.Distance(from, aim);
            if (length < 0.5f) return true;
            if (Physics.Linecast(from, aim, out RaycastHit solid, ArcCheck.Mask, QueryTriggerInteraction.Ignore)
                && solid.distance < length - ArcCheck.TargetSlack && solid.collider.GetComponentInParent<Character>() != target)
            {
                blocker = Solid(shooter, solid.collider, solid.point).Blocker;
                return false;
            }
            LaneBlock people = People(shooter, target, from, aim);
            blocker = people.Blocker;
            return !people.Blocked;
        }

        /// <summary>Where the bow's projectile leaves (vanilla: the attack origin joint plus the attack's height, range and offset).</summary>
        public static Vector3 SpawnPoint(Humanoid self, ItemDrop.ItemData bow)
        {
            Attack attack = bow?.m_shared?.m_attack;
            if (self == null) return Vector3.zero;
            if (attack == null) return self.GetCenterPoint();
            Transform body = self.transform;
            Transform origin = string.IsNullOrEmpty(attack.m_attackOriginJoint) ? body : FindChild(body, attack.m_attackOriginJoint) ?? body;
            return origin.position + body.up * attack.m_attackHeight + body.forward * attack.m_attackRange + body.right * attack.m_attackOffset;
        }

        /// <summary>A full-draw shot's speed (the bow's plus the ammo's) and its projectile's gravity.</summary>
        public static void Ballistics(Humanoid self, ItemDrop.ItemData bow, out float speed, out float gravity)
        {
            speed = 0f;
            gravity = 0f;
            Attack attack = bow?.m_shared?.m_attack;
            if (attack == null) return;
            speed = attack.m_projectileVel;
            GameObject projectile = attack.m_attackProjectile;
            ItemDrop.ItemData ammo = self != null ? self.GetAmmoItem() : null;
            Attack ammoAttack = ammo?.m_shared?.m_attack;
            if (ammoAttack != null && ammoAttack.m_attackProjectile != null)
            {
                speed += ammoAttack.m_projectileVel;
                projectile = ammoAttack.m_attackProjectile;
            }
            Projectile p = projectile != null ? projectile.GetComponent<Projectile>() : null;
            if (p != null) gravity = p.m_gravity;
        }

        // The low launch that reaches aim at this speed and gravity; straight at it when out of range or without drop.
        private static Vector3 LaunchDirection(Vector3 from, Vector3 aim, float speed, float gravity)
        {
            Vector3 to = aim - from;
            Vector3 flat = new Vector3(to.x, 0f, to.z);
            float x = flat.magnitude;
            if (speed <= 0f || gravity <= 0f || x < 0.01f) return to.normalized;
            float v2 = speed * speed;
            float root = v2 * v2 - gravity * (gravity * x * x + 2f * to.y * v2);
            if (root < 0f) return to.normalized;
            float angle = Mathf.Atan((v2 - Mathf.Sqrt(root)) / (gravity * x));
            return (flat / x * Mathf.Cos(angle) + Vector3.up * Mathf.Sin(angle)).normalized;
        }

        private static LaneBlock Solid(Character shooter, Collider c, Vector3 point)
        {
            var lane = new LaneBlock { Blocked = true, Point = point };
            Piece piece = c != null ? c.GetComponentInParent<Piece>() : null;
            if (piece != null)
            {
                bool own = shooter is Player player && piece.GetCreator() == player.GetPlayerID();
                lane.Friendly = own;
                lane.Blocker = $"{Utils.GetPrefabName(piece.gameObject)} ({(own ? "own piece" : "piece")})";
            }
            else if (c != null && (c.gameObject.layer == LayerMask.NameToLayer("terrain") || c.GetComponentInParent<Heightmap>() != null)) lane.Blocker = "terrain";
            else lane.Blocker = c != null ? Utils.GetPrefabName(c.transform.root.gameObject) : "something";
            return lane;
        }

        private static LaneBlock People(Character shooter, Character target, Vector3 from, Vector3 aim)
        {
            var lane = new LaneBlock();
            Vector3 to = aim - from;
            float length = to.magnitude;
            if (length < 0.6f) return lane;
            int n = Physics.SphereCastNonAlloc(from, LaneRadius, to / length, s_hits, length - 0.5f, CharacterMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                Character other = s_hits[i].collider != null ? s_hits[i].collider.GetComponentInParent<Character>() : null;
                if (other == null || other == shooter || other == target || other.IsDead()) continue;
                if (Archetypes.ClassTargeting.IsEnemyTarget(shooter, other)) continue;
                string kind = other.IsPlayer() ? "player" : other.GetComponent<CompanionController>() != null ? "companion" : other.IsTamed() ? "tame" : "friendly";
                lane.Blocked = true;
                lane.Friendly = true;
                lane.Blocker = $"{other.GetHoverName()} ({kind})";
                lane.Point = s_hits[i].point;
                return lane;
            }
            return lane;
        }

        private static Transform FindChild(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindChild(root.GetChild(i), name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
