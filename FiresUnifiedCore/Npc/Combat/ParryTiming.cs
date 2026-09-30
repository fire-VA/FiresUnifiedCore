using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// When an attacker's swing lands, learned from the swings themselves (COMBAT_TACTICS §1; Fire, R72: "time his blocks to
    /// stagger sometimes so he can capitalize"). Vanilla parries a hit that lands less than 0.25 s after the block was RAISED
    /// (Humanoid.BlockAttack: m_blockTimer &lt; 0.25 with a blocker whose timedBlockBonus &gt; 1), and staggers the attacker. So the
    /// block has to go up just before the hit, not be held. A swing starts when the attacker's animator enters an attack state
    /// (Humanoid.InAttack, synced to every peer); it ends when a hit from it arrives on this peer (Core's RPC_Damage prefix). The
    /// delay per (creature, attack state) is a running mean; <see cref="Predict"/> answers when the open swing will land.
    /// Counted per body (TacticStats): parries, the staggers they caused, blocks, and hits landed on a staggering foe.
    /// </summary>
    public static class ParryTiming
    {
        /// <summary>Raise the block this long before the predicted hit (inside vanilla's 0.25 s window, with margin both ways).</summary>
        public const float Lead = 0.12f;
        /// <summary>Samples needed before a prediction is trusted (before that: hold-block as before).</summary>
        public const int MinSamples = 2;
        private const float MaxDelay = 3f, MinDelay = 0.1f, TrackRange = 12f;
        private const int MeanCap = 20;

        private sealed class Swing
        {
            public float Start;
            public string Key;
            public bool Landed;
        }

        private static readonly Dictionary<Character, Swing> s_swings = new Dictionary<Character, Swing>();
        private static readonly Dictionary<string, (float mean, int n)> s_delays = new Dictionary<string, (float, int)>();
        private static readonly List<Character> s_gone = new List<Character>();

        /// <summary>
        /// When <paramref name="attacker"/>'s open swing is expected to land (Time.time), with how many swings of that kind taught it.
        /// False: not swinging, or not enough samples yet.
        /// </summary>
        public static bool Predict(Character attacker, out float hitAt, out string key, out int samples)
        {
            hitAt = 0f;
            key = null;
            samples = 0;
            if (attacker == null || !s_swings.TryGetValue(attacker, out Swing swing) || swing.Landed) return false;
            key = swing.Key;
            if (!s_delays.TryGetValue(swing.Key, out var d)) return false;
            samples = d.n;
            hitAt = swing.Start + d.mean;
            return d.n >= MinSamples;
        }

        /// <summary>The learned delay for every kind of swing so far: "Skeleton:12345 0.52 s (n 6)", …</summary>
        public static string Table()
        {
            var parts = new List<string>();
            foreach (var kv in s_delays) parts.Add($"{kv.Key} {kv.Value.mean:0.00} s (n {kv.Value.n})");
            return string.Join("; ", parts);
        }

        // The attack state an attacker is in, as a key: its prefab + the animator state's hash.
        private static string KeyOf(Character c)
        {
            string prefab = Utils.GetPrefabName(c.gameObject);
            Animator animator = c.m_animator;
            if (animator == null) return prefab;
            var next = animator.GetNextAnimatorStateInfo(0);
            int state = next.fullPathHash != 0 ? next.fullPathHash : animator.GetCurrentAnimatorStateInfo(0).fullPathHash;
            return $"{prefab}:{state}";
        }

        // Every frame: swings starting and ending round the local player (the body this peer drives).
        private static void Track()
        {
            Player me = Player.m_localPlayer;
            if (me == null) return;
            Vector3 at = me.transform.position;
            float now = Time.time;
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == null || c == me || c.IsDead()) continue;
                if ((c.transform.position - at).sqrMagnitude > TrackRange * TrackRange) continue;
                bool swinging = c.InAttack();
                s_swings.TryGetValue(c, out Swing swing);
                if (swinging && swing == null) s_swings[c] = new Swing { Start = now, Key = KeyOf(c) };
                else if (!swinging && swing != null) s_swings.Remove(c);
            }
            s_gone.Clear();
            foreach (var kv in s_swings) if (kv.Key == null || now - kv.Value.Start > MaxDelay) s_gone.Add(kv.Key);
            foreach (var c in s_gone) s_swings.Remove(c);
        }

        /// <summary>The victim's owner, from Core's RPC_Damage prefix: a hit arrived; if its attacker's swing is open, learn the delay.</summary>
        internal static void OnVictimHit(Character victim, HitData hit)
        {
            if (hit == null || victim == null) return;
            Character attacker = hit.GetAttacker();
            if (attacker == null || !s_swings.TryGetValue(attacker, out Swing swing) || swing.Landed) return;
            swing.Landed = true;
            float delay = Time.time - swing.Start;
            if (delay < MinDelay || delay > MaxDelay) return;
            s_delays.TryGetValue(swing.Key, out var d);
            int n = Mathf.Min(d.n + 1, MeanCap);
            float mean = d.n == 0 ? delay : d.mean + (delay - d.mean) / n;
            s_delays[swing.Key] = (mean, d.n + 1);
            if (d.n + 1 <= 3 || (d.n + 1) % 10 == 0)
                Debug.Log($"[ParryTiming] {swing.Key}: hit landed {delay:0.00} s after the wind-up (mean {mean:0.00} s, n {d.n + 1})");
            // A hit that lands on a body whose parry was planned but whose block wasn't up: the raise came late.
            if (s_planned.TryGetValue(victim, out float raiseAt) && Mathf.Abs(Time.time - raiseAt) < 0.6f && !victim.IsBlocking())
                Debug.Log($"[ParryTiming] {victim.m_name}: block: {Utils.GetPrefabName(attacker.gameObject)} hit landed unblocked (no parry: late; raise was planned {Time.time - raiseAt:+0.00;-0.00} s ago)");
        }

        private static readonly Dictionary<Character, float> s_planned = new Dictionary<Character, float>();

        /// <summary>CombatAdvisor: a parry raise was ordered for <paramref name="body"/> at <paramref name="raiseAt"/> (for the "late" line).</summary>
        internal static void Planned(Character body, float raiseAt)
        {
            if (body != null) s_planned[body] = raiseAt;
        }

        /// <summary>Attacker side, from Core's Character.Damage prefix: a hit leaving on a staggering foe counts as a stagger hit.</summary>
        internal static void OnAttackerHit(Character victim, HitData hit)
        {
            if (victim == null || hit == null || !victim.IsStaggering()) return;
            Character attacker = hit.GetAttacker();
            if (attacker == null || attacker != Player.m_localPlayer) return;
            TacticStats.Counters c = TacticStats.For(attacker);
            if (c != null) c.StaggerHits++;
        }

        // Vanilla's parry test, read before BlockAttack runs (it resets nothing, but the outcome needs the before state).
        [HarmonyPatch(typeof(Humanoid), "BlockAttack")]
        private static class Humanoid_BlockAttack
        {
            private static void Prefix(Humanoid __instance, out (bool timed, bool bonus, float since) __state)
            {
                ItemDrop.ItemData blocker = __instance.GetCurrentBlocker();
                bool bonus = blocker?.m_shared != null && blocker.m_shared.m_timedBlockBonus > 1f;
                float since = __instance.m_blockTimer;
                __state = (bonus && since >= 0f && since < 0.25f, bonus, since);
            }

            private static void Postfix(Humanoid __instance, Character attacker, bool __result, (bool timed, bool bonus, float since) __state)
            {
                if (!__result || __instance != Player.m_localPlayer) return;
                TacticStats.Counters c = TacticStats.For(__instance);
                if (c == null) return;
                c.Blocks++;
                string foe = attacker != null ? Utils.GetPrefabName(attacker.gameObject) : "?";
                if (__state.timed)
                {
                    c.Parries++;
                    bool staggers = attacker != null && attacker.m_staggerWhenBlocked;
                    if (staggers) c.Staggers++;
                    Debug.Log($"[ParryTiming] {__instance.m_name}: parry: {foe} (block raised {__state.since:0.00} s before the hit){(staggers ? " -> stagger" : "; it doesn't stagger")}");
                }
                else
                {
                    string why = !__state.bonus ? "no bonus (this blocker can't parry)" : __state.since < 0f ? "held (no raise)" : "early";
                    Debug.Log($"[ParryTiming] {__instance.m_name}: block: {foe} held (no parry: {why}; raised {__state.since:0.00} s before)");
                }
            }
        }

        [HarmonyPatch(typeof(Player), "Update")]
        private static class Player_Update_Track
        {
            private static void Postfix(Player __instance)
            {
                if (__instance == Player.m_localPlayer) Track();
            }
        }
    }
}
