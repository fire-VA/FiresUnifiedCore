using System;
using System.Collections.Generic;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    /// <summary>
    /// Companions walking over and aggroing a creature mid-tame ruins the tame, so a creature
    /// a player is taming is left alone by companions. Being tamed = a wild Tameable within <see cref="PlayerRadius"/> of a player that
    /// has taming progress, or was fed in the last <see cref="FedMinutes"/>, or sits in a pit (most of the ground round it well above
    /// it). A companion never picks it as a target, drops it as one, and keeps <see cref="KeepAwayMetres"/> from it so it doesn't alert
    /// it. It still defends: a creature that hit the companion or its owner in the last <see cref="DefendSeconds"/> is fair game.
    /// </summary>
    public static class TamingGuard
    {
        public const float PlayerRadius = 30f, FedMinutes = 10f, KeepAwayMetres = 10f, DefendSeconds = 10f;
        private const float CacheSeconds = 2f, ScanSeconds = 1f, LogEvery = 10f;
        private const float PitRadius = 2.5f, PitRise = 1.2f;
        private const int PitSamples = 8, PitWalls = 6;

        private sealed class Read
        {
            public float Until;
            public bool Taming;
            public string By;
        }

        private static readonly Dictionary<Character, Read> s_reads = new Dictionary<Character, Read>();
        private static readonly Dictionary<string, float> s_logged = new Dictionary<string, float>();
        private static readonly Dictionary<Character, (float until, Character creature)> s_near = new Dictionary<Character, (float, Character)>();
        private static readonly List<Character> s_scan = new List<Character>();

        /// <summary>Whether <paramref name="creature"/> is being tamed, and by whom and why ("Vafire (fed 2 min ago)").</summary>
        public static bool BeingTamed(Character creature, out string by)
        {
            by = null;
            if (creature == null || creature.IsDead() || creature.IsPlayer() || creature.IsTamed()) return false;
            if (s_reads.TryGetValue(creature, out Read read) && Time.time < read.Until) { by = read.By; return read.Taming; }
            if (s_reads.Count > 256) Prune();
            read = read ?? new Read();
            read.Until = Time.time + CacheSeconds;
            read.Taming = false;
            read.By = null;
            s_reads[creature] = read;

            Tameable tame = creature.GetComponent<Tameable>();
            ZDO zdo = creature.m_nview != null && creature.m_nview.IsValid() ? creature.m_nview.GetZDO() : null;
            if (tame == null || zdo == null) return false;
            Player player = Player.GetClosestPlayer(creature.transform.position, PlayerRadius);
            if (player == null) return false;
            string why = null;
            float left = zdo.GetFloat(ZDOVars.s_tameTimeLeft, tame.m_tamingTime);
            if (left < tame.m_tamingTime - 0.5f) why = $"taming {(1f - Mathf.Clamp01(left / Mathf.Max(1f, tame.m_tamingTime))) * 100f:0} %";
            long fed = zdo.GetLong(ZDOVars.s_tameLastFeeding, 0L);
            if (why == null && fed > 0L && ZNet.instance != null)
            {
                double minutes = (ZNet.instance.GetTime() - new DateTime(fed)).TotalMinutes;
                if (minutes >= 0 && minutes < FedMinutes) why = $"fed {minutes:0} min ago";
            }
            if (why == null && InPit(creature)) why = "in a pit";
            if (why == null) return false;
            read.Taming = true;
            read.By = $"{player.GetPlayerName()} ({why})";
            by = read.By;
            return true;
        }

        /// <summary>
        /// Whether a companion leaves <paramref name="creature"/> alone: it is being tamed and hasn't hit the companion or its owner in the
        /// last DefendSeconds. <paramref name="why"/> is "being tamed by X (...)".
        /// </summary>
        public static bool LeaveAlone(Character companion, Character owner, Character creature, out string why)
        {
            why = null;
            if (!BeingTamed(creature, out string by)) return false;
            if (HurtParty(companion, owner, creature)) return false;
            why = $"being tamed by {by}";
            return true;
        }

        /// <summary>The Debug line, once a LogEvery per companion and creature.</summary>
        public static void Note(Character companion, string name, Character creature, string why)
        {
            if (companion == null || creature == null) return;
            string key = companion.GetInstanceID() + ":" + creature.GetInstanceID();
            if (s_logged.TryGetValue(key, out float next) && Time.time < next) return;
            if (s_logged.Count > 256) s_logged.Clear();
            s_logged[key] = Time.time + LogEvery;
            FiresLogger.LogDebug($"[Companion] {name}: leaving {creature.m_name} alone ({why})");
        }

        /// <summary>
        /// The companion move <paramref name="dir"/> bent so the body keeps KeepAwayMetres from a creature being tamed near it: no closer,
        /// and stepping away when inside (owner only; checked once a ScanSeconds).
        /// </summary>
        public static void KeepAway(Character body, Character owner, string name, ref Vector3 dir)
        {
            if (body == null || body.IsDead() || body.m_nview == null || !body.m_nview.IsValid() || !body.m_nview.IsOwner()) return;
            Character creature = NearTaming(body, owner);
            if (creature == null) return;
            Vector3 away = body.transform.position - creature.transform.position;
            away.y = 0f;
            float d = away.magnitude;
            if (d >= KeepAwayMetres || d < 0.01f) return;
            away /= d;
            Vector3 flat = new Vector3(dir.x, 0f, dir.z);
            float toward = Vector3.Dot(flat, -away);
            if (toward > 0f) flat += away * toward;                               // no closer
            float push = (KeepAwayMetres - d) / KeepAwayMetres;
            flat += away * Mathf.Clamp01(push * 2f);                              // and out to KeepAwayMetres
            if (flat.sqrMagnitude > 1f) flat.Normalize();
            dir = new Vector3(flat.x, dir.y, flat.z);
            if (LeaveAlone(body, owner, creature, out string why)) Note(body, name, creature, $"{why}; keeping {KeepAwayMetres:0} m off, {d:0.0} m now");
        }

        // The nearest creature being tamed within KeepAwayMetres + 2 that the party hasn't been hit by, rescanned once a ScanSeconds.
        private static Character NearTaming(Character body, Character owner)
        {
            if (s_near.TryGetValue(body, out var near) && Time.time < near.until) return near.creature != null && !near.creature.IsDead() ? near.creature : null;
            if (s_near.Count > 64) s_near.Clear();
            Character best = null;
            float bestD = float.MaxValue;
            s_scan.Clear();
            Character.GetCharactersInRange(body.transform.position, KeepAwayMetres + 2f, s_scan);
            foreach (Character c in s_scan)
            {
                if (c == null || c == body || !LeaveAlone(body, owner, c, out _)) continue;
                float d = Vector3.Distance(body.transform.position, c.transform.position);
                if (d < bestD) { bestD = d; best = c; }
            }
            s_scan.Clear();
            s_near[body] = (Time.time + ScanSeconds, best);
            return best;
        }

        // Hit the companion or its owner lately (FireScare's last hit per body).
        private static bool HurtParty(Character companion, Character owner, Character creature)
        {
            if (FiresCore.Npc.Combat.FireScare.HitLately(companion, DefendSeconds, out Character by, out _) && by == creature) return true;
            return owner != null && FiresCore.Npc.Combat.FireScare.HitLately(owner, DefendSeconds, out by, out _) && by == creature;
        }

        // A pit: PitWalls of PitSamples points PitRadius round the creature are PitRise or more above its feet.
        private static bool InPit(Character creature)
        {
            Vector3 at = creature.transform.position;
            int walls = 0;
            for (int i = 0; i < PitSamples; i++)
            {
                float a = i * Mathf.PI * 2f / PitSamples;
                Vector3 p = at + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * PitRadius;
                if (FiresCore.Movement.Climbing.TopSolid(p.x, p.z, out float h, out _) && h - at.y >= PitRise) walls++;
            }
            return walls >= PitWalls;
        }

        private static void Prune()
        {
            var gone = new List<Character>();
            foreach (var key in s_reads.Keys) if (key == null) gone.Add(key);
            foreach (var key in gone) s_reads.Remove(key);
            if (s_reads.Count > 256) s_reads.Clear();
        }
    }
}
