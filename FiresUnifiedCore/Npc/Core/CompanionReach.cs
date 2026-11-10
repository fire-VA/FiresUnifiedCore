using UnityEngine;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// A companion reaches a chest / station the way a player does: nothing solid (a wall, floor, roof, terrain) between its chest
    /// height and the target's nearest point. Distance alone let companions open chests from outside the base, through the wall
    /// (Fire 10-23). One rule for every companion chore that touches a container or a station.
    /// </summary>
    public static class CompanionReach
    {
        private const float ChestHeight = 1.2f;
        private static int s_mask = -1;
        private static int Mask => s_mask != -1 ? s_mask : (s_mask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "terrain", "vehicle"));
        private static readonly RaycastHit[] s_hits = new RaycastHit[16];

        /// <summary>True when nothing solid stands between <paramref name="me"/> (at chest height) and <paramref name="target"/>'s nearest
        /// collider point; <paramref name="blockedBy"/> names the first blocker otherwise. Hits on the target itself never block.</summary>
        /// <summary>[ghost] 10-26 (Fire: "if were standing under a chest and look up we get the interaction"): a player's interact reach.
        /// True when the target's nearest collider point is within <see cref="InteractReach"/> of <paramref name="me"/>'s eye height and
        /// CanTouch passes, so an overhead or wall chest counts as reached from underneath it.</summary>
        // 10-26 (Fire's 829 log: the overhead chests sit 5.3 m above the floor, out of a 3.5 m reach): vanilla's own
        // Player.m_maxInteractDistance (5 m from the eye).
        public const float InteractReach = 5f;
        public const float EyeHeight = 1.6f;
        public static bool InInteractReach(Transform me, Component target, out float dist)
        {
            dist = float.MaxValue;
            if (me == null || target == null) return false;
            Vector3 eye = me.position + Vector3.up * EyeHeight;
            dist = Vector3.Distance(eye, NearestPoint(target, eye));
            if (dist <= InteractReach && CanTouch(me, target, out _)) return true;
            // [ghost] 10-26 (Fire: storage overhaul = no unreachable chests): a chest is also in reach while a storage terminal
            // serving it (linked terminals count) is.
            if (target is Container || target.GetComponent<Container>() != null)
                foreach (Component t in FiresCore.Storage.StorageNetwork.TerminalsServing(target.transform.position, me.position))
                {
                    if (t == null) continue;
                    float td = Vector3.Distance(eye, NearestPoint(t, eye));
                    if (td > InteractReach) break;   // nearest first: the rest are farther
                    if (CanTouch(me, t, out _)) { dist = td; return true; }
                }
            return false;
        }

        public static bool CanTouch(Transform me, Component target, out string blockedBy)
        {
            blockedBy = null;
            if (me == null || target == null) return false;
            Transform root = target.transform.root;
            Vector3 from = me.position + Vector3.up * ChestHeight;
            Vector3 to = NearestPoint(target, from);
            Vector3 d = to - from;
            float len = d.magnitude;
            if (len < 0.05f) return true;
            int n = Physics.RaycastNonAlloc(from, d / len, s_hits, len, Mask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; Collider hitC = null;
            for (int i = 0; i < n; i++)
            {
                var c = s_hits[i].collider;
                if (c == null || c.transform.IsChildOf(root) || c.transform.IsChildOf(me.root) || IsDoor(c)) continue;   // the target itself, the companion, or a door (open door leaves and frames never block)
                if (s_hits[i].distance < best) { best = s_hits[i].distance; hitC = c; }
            }
            if (hitC == null) return true;
            blockedBy = $"'{hitC.transform.root.name}' ({LayerMask.LayerToName(hitC.gameObject.layer)}, {best:0.0} m on)";
            return false;
        }

        /// <summary>A clear line from <paramref name="me"/>'s chest height to a goal point (chest height there too). Doorways / walls block.</summary>
        public static bool CanSee(Transform me, Vector3 goal, out string blockedBy)
        {
            blockedBy = null;
            if (me == null) return false;
            Vector3 from = me.position + Vector3.up * ChestHeight, to = goal + Vector3.up * ChestHeight;
            Vector3 d = to - from; float len = d.magnitude;
            if (len < 0.05f) return true;
            int n = Physics.RaycastNonAlloc(from, d / len, s_hits, len, Mask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; Collider hitC = null;
            for (int i = 0; i < n; i++) { var c = s_hits[i].collider; if (c == null || c.transform.IsChildOf(me.root) || IsDoor(c)) continue; if (s_hits[i].distance < best) { best = s_hits[i].distance; hitC = c; } }
            if (hitC == null) return true;
            blockedBy = $"'{hitC.transform.root.name}' ({LayerMask.LayerToName(hitC.gameObject.layer)}, {best:0.0} m on)";
            return false;
        }

        // A doorway's door (its leaf, open or closed) is passable by path, so it never counts as the wall in the way.
        private static bool IsDoor(Collider c) => c.GetComponentInParent<Door>() != null;

        private static Vector3 NearestPoint(Component target, Vector3 from)
        {
            Vector3 best = target.transform.position + Vector3.up * 0.5f; float bestD = float.MaxValue;
            foreach (var c in target.GetComponentsInChildren<Collider>())
            {
                if (c == null || !c.enabled || c.isTrigger) continue;
                Vector3 p;
                try { p = c.ClosestPoint(from); } catch { p = c.bounds.ClosestPoint(from); }
                float dd = (p - from).sqrMagnitude;
                if (dd < bestD) { bestD = dd; best = p; }
            }
            return best;
        }
    }
}
