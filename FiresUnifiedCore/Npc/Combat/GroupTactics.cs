using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// Where each companion stands round the foe it fights (Core 0.2.204; Fire, R77: "companions need to be aware of eachother and
    /// their owner and fight accordingly, not stacking on top of eachother fighting to get position on an enemy").
    ///
    /// Slots are angles round the foe, measured from the foe's side that faces the owner:
    /// - a tank takes the front (between the foe and the owner, just off the owner's line);
    /// - melee take the ring at <see cref="SlotStepDegrees"/> steps, never inside the owner's line (±<see cref="OwnerLineDegrees"/>),
    ///   nearest free slot to where they stand;
    /// - a slot is taken when ANY character (another owner's companion, a player, the bot) stands at it, so groups don't collide;
    /// - a slot is kept while it stays free (no re-shuffle every tick).
    /// The STACK watchdog logs two companions within <see cref="StackMetres"/> of each other on the same foe for
    /// <see cref="StackSeconds"/> s. One brain: the companions' director uses it; the bot's own fight is CombatAdvisor's.
    /// </summary>
    public static class GroupTactics
    {
        public const float OwnerLineDegrees = 25f, SlotStepDegrees = 30f, SlotTakenDegrees = 20f;
        public const float StackMetres = 1f, StackSeconds = 2f;
        private const float RingSlack = 2f;

        private sealed class Claim
        {
            public Character Foe;
            public float Offset;
            public string Logged;
        }

        private static readonly Dictionary<CompanionController, Claim> s_claims = new Dictionary<CompanionController, Claim>();
        private static readonly List<float> s_taken = new List<float>();
        private static readonly List<float> s_candidates = new List<float>();

        /// <summary>
        /// World yaws (degrees) round <paramref name="foe"/> for each of <paramref name="group"/> (the melee and tanks on it), written
        /// into <paramref name="yaws"/>. <paramref name="isTank"/> tells which are tanks.
        /// </summary>
        public static void AssignSlots(Character foe, Player owner, List<CompanionController> group, System.Func<CompanionController, bool> isTank,
            Dictionary<CompanionController, float> yaws)
        {
            if (foe == null || group == null || group.Count == 0) return;
            Vector3 foeAt = foe.transform.position;
            Vector3 toOwner = owner != null ? owner.transform.position - foeAt : Vector3.forward;
            toOwner.y = 0f;
            float baseYaw = toOwner.sqrMagnitude > 0.01f ? Mathf.Atan2(toOwner.x, toOwner.z) * Mathf.Rad2Deg : 0f;
            float ring = foe.GetRadius() + CompanionSettings.CombatCoordinationMeleeEngageDistance;

            // Taken by others: any character (not the foe, not this group) standing on the ring: another owner's companion, a player,
            // the foe's own pack.
            s_taken.Clear();
            foreach (Character other in Character.GetAllCharacters())
            {
                if (other == null || other == foe || other.IsDead()) continue;
                var cc = other.GetComponent<CompanionController>();
                if (cc != null && group.Contains(cc)) continue;
                if (owner != null && other == owner) continue;   // the owner is the reference, not a blocker
                Vector3 d = other.transform.position - foeAt;
                d.y = 0f;
                if (d.magnitude > ring + RingSlack) continue;
                s_taken.Add(Mathf.DeltaAngle(baseYaw, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg));
            }

            // Tanks first, then the others; each keeps its claim while free, else the nearest free slot to where it stands.
            var ordered = new List<CompanionController>(group);
            ordered.Sort((a, b) => (isTank(b) ? 1 : 0).CompareTo(isTank(a) ? 1 : 0));
            int index = 0;
            foreach (CompanionController c in ordered)
            {
                index++;
                Character body = c != null ? c.GetCharacter() : null;
                if (body == null) continue;
                bool tank = isTank(c);
                Vector3 me = body.transform.position - foeAt;
                me.y = 0f;
                float mine = me.sqrMagnitude > 0.01f ? Mathf.DeltaAngle(baseYaw, Mathf.Atan2(me.x, me.z) * Mathf.Rad2Deg) : 180f;

                BuildCandidates(tank);
                float chosen = float.NaN;
                if (s_claims.TryGetValue(c, out Claim claim) && claim.Foe == foe && s_candidates.Contains(claim.Offset) && Free(claim.Offset))
                    chosen = claim.Offset;
                if (float.IsNaN(chosen))
                {
                    float best = float.MaxValue;
                    foreach (float o in s_candidates)
                    {
                        if (!Free(o)) continue;
                        float travel = Mathf.Abs(Mathf.DeltaAngle(mine, o)) + (tank ? Mathf.Abs(o) * 0.5f : 0f);   // tanks lean to the front
                        if (travel < best) { best = travel; chosen = o; }
                    }
                }
                if (float.IsNaN(chosen)) chosen = mine;   // every slot taken: hold where it is (no pushing in)
                s_taken.Add(chosen);
                if (claim == null || claim.Foe != foe) s_claims[c] = claim = new Claim { Foe = foe };
                claim.Offset = chosen;
                yaws[c] = Mathf.Repeat(baseYaw + chosen, 360f);

                string line = $"slot {chosen:0}° on {foe.m_name} (role {(tank ? "tank" : "melee")}; {index} of {group.Count}, " +
                              $"{ring:0.0} m from it, owner line ±{OwnerLineDegrees:0}° {(Mathf.Abs(chosen) >= OwnerLineDegrees || tank ? "clear" : "IN THE LINE (no free slot)")})";
                if (claim.Logged != line)
                {
                    claim.Logged = line;
                    Debug.Log($"[GroupTactics] {c.companionName} {line}");
                }
            }
        }

        // The ring's slots for one companion: every SlotStepDegrees, the owner's line left out (a tank may take the front just off it).
        private static void BuildCandidates(bool tank)
        {
            s_candidates.Clear();
            if (tank) { s_candidates.Add(OwnerLineDegrees); s_candidates.Add(-OwnerLineDegrees); }
            for (float o = SlotStepDegrees; o <= 180f + 0.01f; o += SlotStepDegrees)
            {
                if (o >= OwnerLineDegrees) { s_candidates.Add(o); if (o < 179.9f) s_candidates.Add(-o); }
            }
        }

        private static bool Free(float offset)
        {
            foreach (float t in s_taken) if (Mathf.Abs(Mathf.DeltaAngle(t, offset)) < SlotTakenDegrees) return false;
            return true;
        }

        /// <summary>Forget a companion's slot (it died, left combat, or was dismissed).</summary>
        public static void Release(CompanionController companion)
        {
            if (companion != null) s_claims.Remove(companion);
        }

        // ---- STACK watchdog ----

        private static readonly Dictionary<long, float> s_closeSince = new Dictionary<long, float>();
        private static readonly HashSet<long> s_stackLogged = new HashSet<long>();

        /// <summary>
        /// Two companions on the same foe within <see cref="StackMetres"/> for <see cref="StackSeconds"/> s: one line per episode.
        /// <paramref name="targets"/>: each companion's foe.
        /// </summary>
        public static void Watch(List<CompanionController> companions, System.Func<CompanionController, Character> targetOf)
        {
            if (companions == null) return;
            float now = Time.time;
            for (int i = 0; i < companions.Count; i++)
            for (int j = i + 1; j < companions.Count; j++)
            {
                CompanionController a = companions[i], b = companions[j];
                Character ba = a != null ? a.GetCharacter() : null, bb = b != null ? b.GetCharacter() : null;
                if (ba == null || bb == null) continue;
                Character fa = targetOf(a), fb = targetOf(b);
                long key = ((long)a.GetInstanceID() << 32) ^ (uint)b.GetInstanceID();
                bool close = fa != null && fa == fb && Vector3.Distance(ba.transform.position, bb.transform.position) < StackMetres;
                if (!close) { s_closeSince.Remove(key); s_stackLogged.Remove(key); continue; }
                if (!s_closeSince.TryGetValue(key, out float since)) { s_closeSince[key] = now; continue; }
                if (now - since >= StackSeconds && s_stackLogged.Add(key))
                    Debug.Log($"[GroupTactics] STACK {a.companionName} + {b.companionName} within " +
                              $"{Vector3.Distance(ba.transform.position, bb.transform.position):0.0} m on {fa.m_name} for {now - since:0.0} s");
            }
            if (s_closeSince.Count > 256) { s_closeSince.Clear(); s_stackLogged.Clear(); }
        }
    }
}
