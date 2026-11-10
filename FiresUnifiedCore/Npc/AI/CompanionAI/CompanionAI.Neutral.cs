using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.AI
{
    // 0.2.981 (Fire 10-29): neutral mobs (Dvergr and anything else that is not an enemy of the companion) are only fought when they
    // started it against our side or the owner really hit them. The owner-hit bookkeeping counts only hits that did damage after
    // vanilla's faction rules (ApplyDamage), from a player; a companion swing or a 0-damage hit never counts as "we attacked first".
    public partial class CompanionAI
    {
        private const float OwnerHitWindow = 10f;
        private static readonly Dictionary<ZDOID, (long player, float time)> s_playerHits = new Dictionary<ZDOID, (long, float)>();
        private float _nextNeutralLog; private Character _neutralLogged;

        [HarmonyPatch(typeof(Character), "ApplyDamage")]
        private static class Character_ApplyDamage_PlayerHit
        {
            private static void Postfix(Character __instance, HitData hit)
            {
                if (__instance == null || hit == null || __instance.IsPlayer() || hit.GetTotalDamage() <= 0f) return;
                var p = hit.GetAttacker() as Player;
                var nv = __instance.GetComponent<ZNetView>();
                if (p == null || nv == null || !nv.IsValid()) return;
                if (s_playerHits.Count > 256) s_playerHits.Clear();
                s_playerHits[nv.GetZDO().m_uid] = (p.GetPlayerID(), Time.time);
            }
        }

        private bool NeutralAccepted(Character foe)
        {
            string why = null;
            var ai = foe.GetBaseAI();
            var victim = (ai as MonsterAI)?.GetTargetCreature();
            if (ai != null && ai.IsAggravated()) why = "aggravated";
            if (why == null && victim != null && (victim == m_character || victim == OwnerBody() || IsOwnersCompanion(victim))) why = "targets us";
            if (why == null && _companion != null && _companion.ownerPlayerId != 0L)
            {
                var nv = foe.GetComponent<ZNetView>();
                if (nv != null && nv.IsValid() && s_playerHits.TryGetValue(nv.GetZDO().m_uid, out var h)
                    && h.player == _companion.ownerPlayerId && Time.time - h.time <= OwnerHitWindow)
                    why = "owner hit it";
            }
            if (foe != _neutralLogged || Time.time >= _nextNeutralLog)
            {
                _neutralLogged = foe; _nextNeutralLog = Time.time + 5f;
                if (why != null)
                    FiresCore.Logging.FiresLogger.LogDebug($"[CompanionAI] {m_character?.m_name} fights back against {foe.m_name} (neutral, accepted: {why})");
                else
                    FiresCore.Logging.FiresLogger.LogDebug($"[CompanionAI] {m_character?.m_name}: not fighting / dropped {foe.m_name} (neutral, faction {foe.m_faction}: its target is {(victim != null ? victim.m_name : "none")}, not aggravated, owner did not hit it)");
            }
            return why != null;
        }

        private bool IsOwnersCompanion(Character c)
        {
            if (c == null || _companion == null || _companion.ownerPlayerId == 0L) return false;
            var cc = c.GetComponent<CompanionController>();
            return cc != null && cc.isTamed && cc.ownerPlayerId == _companion.ownerPlayerId;
        }
    }
}
