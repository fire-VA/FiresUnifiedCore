using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Combat
{
    /// <summary>
    /// The foe each player (the FDT bot included) last hit, so their companions join the fight the owner starts (the lead, R67 pve:
    /// the bot fought the pack and its companions engaged nothing; before this a companion only answered foes near the owner,
    /// after the owner or itself, or that had hit it). Recorded on the owner's machine as its hit leaves (Core's Character.Damage
    /// prefix), which is where its companions simulate. CompanionAI's target scan asks <see cref="Recent"/>.
    /// </summary>
    public static class OwnerAssist
    {
        private static readonly Dictionary<long, (Character foe, float at)> s_lastHit = new Dictionary<long, (Character, float)>();

        internal static void OnHit(Character victim, HitData hit)
        {
            if (victim == null || hit == null || victim.IsPlayer()) return;
            if (!(hit.GetAttacker() is Player owner) || victim.GetComponent<CompanionController>() != null) return;
            if (hit.GetTotalDamage() <= 0f) return;
            s_lastHit[owner.GetPlayerID()] = (victim, Time.time);
        }

        /// <summary>The foe <paramref name="owner"/> hit within <paramref name="seconds"/> s and still alive, else false.</summary>
        public static bool Recent(Player owner, float seconds, out Character foe)
        {
            foe = null;
            if (owner == null || !s_lastHit.TryGetValue(owner.GetPlayerID(), out var last)) return false;
            if (last.foe == null || last.foe.IsDead() || Time.time - last.at > seconds) return false;
            foe = last.foe;
            return true;
        }
    }
}
