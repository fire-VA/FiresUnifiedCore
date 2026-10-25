using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// One Debug line per companion fight, so the test rounds can
    /// put the companion's rhythm next to FiresTestBot's - "[CombatLoop] name: N attacks, max chain M, avg gap X s, dodges D, T s".
    /// A fight ends 5 s after its last swing.
    /// </summary>
    internal static class CombatLoopStats
    {
        private sealed class Fight { public string Name; public int Attacks, MaxChain, Dodges; public float First, Last, GapSum; public int Gaps; }
        private static readonly Dictionary<int, Fight> s_fights = new Dictionary<int, Fight>();
        private const float FightEndsAfter = 5f;

        private static Fight For(Character c, bool create)
        {
            if (c == null) return null;
            int id = c.GetInstanceID();
            if (s_fights.TryGetValue(id, out var f)) return f;
            if (!create) return null;
            f = new Fight { Name = c.m_name, First = Time.time, Last = Time.time };
            s_fights[id] = f;
            return f;
        }

        internal static void NoteAttack(Character c, int chainStep)
        {
            var f = For(c, true);
            if (f == null) return;
            if (f.Attacks > 0) { f.GapSum += Time.time - f.Last; f.Gaps++; }
            f.Attacks++;
            if (chainStep > f.MaxChain) f.MaxChain = chainStep;
            f.Last = Time.time;
        }

        internal static void NoteDodge(Character c)
        {
            var f = For(c, true);
            if (f == null) return;
            f.Dodges++;
            f.Last = Time.time;
        }

        internal static void Tick(Character c)
        {
            var f = For(c, false);
            if (f == null || Time.time - f.Last < FightEndsAfter) return;
            s_fights.Remove(c.GetInstanceID());
            if (f.Attacks == 0 && f.Dodges == 0) return;
            FiresCore.Logging.FiresLogger.LogDebug($"[CombatLoop] {f.Name}: {f.Attacks} attacks, max chain {f.MaxChain}, avg gap {(f.Gaps > 0 ? f.GapSum / f.Gaps : 0f):0.00} s, dodges {f.Dodges}, {f.Last - f.First:0.0} s");
        }
    }
}