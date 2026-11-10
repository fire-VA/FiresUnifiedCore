using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// 0.2.916 (round #31: 16 smelting runs in 10 min, each "ore in 0, fuel in 0, bars out 0 ... source not reached: Coal chest at (293, 54,
    /// 935) not reached in 30 s" - the ONLY chest holding the base's ore and coal is upstairs, and the starved-smelter score kept sending the
    /// next companion to it): chests a companion could not walk to are shared for every companion for a while, so the station scoring stops
    /// counting their contents as "to hand" and the pull takes another chest (or says there is none).
    /// </summary>
    public static class ChestReach
    {
        private static readonly Dictionary<int, (float until, string why)> s_unreachable = new Dictionary<int, (float, string)>();

        public static void MarkUnreachable(Container chest, float seconds, string why)
        {
            if (chest == null) return;
            s_unreachable[chest.GetInstanceID()] = (Time.time + seconds, why);
        }

        public static bool IsUnreachable(Container chest)
        {
            if (chest == null) return false;
            if (!s_unreachable.TryGetValue(chest.GetInstanceID(), out var e)) return false;
            if (Time.time < e.until) return true;
            s_unreachable.Remove(chest.GetInstanceID());
            return false;
        }

        // 0.2.930 (round #34: 23 of 26 FireTending runs "MovingToFire timed out" on the same low fire, which kept its +20 need and won every pick,
        // so wood was never gathered): the same shared memory for any target (a fire), add-only
        public static void MarkUnreachable(Component target, float seconds, string why)
        {
            if (target == null) return;
            s_unreachable[target.GetInstanceID()] = (Time.time + seconds, why);
        }
        public static bool IsUnreachable(Component target)
        {
            if (target == null) return false;
            if (!s_unreachable.TryGetValue(target.GetInstanceID(), out var e)) return false;
            if (Time.time < e.until) return true;
            s_unreachable.Remove(target.GetInstanceID());
            return false;
        }

        public static string WhyUnreachable(Container chest)
            => chest != null && s_unreachable.TryGetValue(chest.GetInstanceID(), out var e) && Time.time < e.until ? e.why : null;
    }
}
