using System.Linq;
using FiresCore.Npc.AI;
using FiresCore.Npc.IdleBehaviors;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Keeps a companion's haul going (added by <see cref="CompanionController.StartHaul"/>): starts HaulBehavior as a
    /// command, starts it again once a fight has been over for <see cref="CalmSeconds"/>, stops the haul when a player order takes
    /// over, and when the haul ends puts a companion that was following back on follow. Runs on the companion's ZDO owner only.
    /// </summary>
    internal sealed class CompanionHaulRunner : MonoBehaviour
    {
        private const float CalmSeconds = 2f;
        private const float RetrySeconds = 2f;

        internal HaulJob Job;
        internal bool WasFollowing;

        private CompanionController _companion;
        private ZNetView _nview;
        private CompanionIdleBehavior _idle;
        private float _calmSince = -1f;
        private float _nextTry;

        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _nview = GetComponent<ZNetView>();
            _idle = GetComponent<CompanionIdleBehavior>();
        }

        private void Update()
        {
            if (Job == null || _companion == null) { Destroy(this); return; }
            if (_nview == null || !_nview.IsValid() || !_nview.IsOwner()) return;

            HaulBehavior haul = _idle != null ? _idle.GetSubBehavior<HaulBehavior>() : null;
            if (haul == null) { Job.Stop("this companion has no haul behaviour"); End(); return; }
            if (!Job.Active) { End(); return; }

            if (_idle.ActiveSubBehavior == haul && haul.IsActive) { _calmSince = -1f; return; }
            if (haul.CancelledFromOutside) { Job.Stop("another order took over"); End(); return; }
            // Log why the runner is waiting (e.g. a fight), so a stalled haul is never silent
            if (_companion.IsInCombat) { _calmSince = -1f; Wait("in combat" + (_companion.GetCharacter() is Character ch && ch.GetComponent<MonsterAI>()?.GetTargetCreature() is Character tc ? " with " + tc.m_name : "")); return; }

            if (_calmSince < 0f) _calmSince = Time.time;
            if (Time.time - _calmSince < CalmSeconds) { Wait($"calm for {Time.time - _calmSince:0.0}/{CalmSeconds:0} s"); return; }
            if (Time.time < _nextTry) { Wait($"retry in {_nextTry - Time.time:0} s"); return; }
            _nextTry = Time.time + RetrySeconds;
            haul.Job = Job;
            haul.StartedByRunner = true;
            bool started = _idle.TryStartSubBehavior<HaulBehavior>();
            haul.StartedByRunner = false;
            if (!started)
                Debug.LogWarning($"[Haul] {_companion.companionName} couldn't start the haul behaviour; trying again in {RetrySeconds:0} s");
            else FiresCore.Logging.FiresLogger.LogDebug($"[Haul] {_companion.companionName} haul behaviour started");
        }

        // Why the haul isn't running yet - Debug, once per reason change and at most every 10 s
        private string _waitWhy; private float _waitAt = -100f;
        private void Wait(string why)
        {
            string kind = why.Split('(', ' ')[0] + (why.StartsWith("in combat") ? why : "");
            if (kind == _waitWhy && Time.time - _waitAt < 10f) return;
            _waitWhy = kind; _waitAt = Time.time;
            FiresCore.Logging.FiresLogger.LogDebug($"[Haul] {_companion.companionName} waiting: {why}");
        }

        private void End()
        {
            if (WasFollowing && _companion != null)
            {
                Player owner = Player.GetAllPlayers().FirstOrDefault(p => p != null && p.GetPlayerID() == _companion.ownerPlayerId);
                if (owner != null) _companion.HaulRuntimeFollow(owner);   // Runtime only, membership untouched
            }
            Destroy(this);
        }
    }
}
