using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.WildSpawn
{
    /// <summary>
    /// Deterministic squad election for freshly-spawned wild companions:
    /// leader = nearby same-faction member with the lowest ZDO UID hash.
    /// Each member writes <c>companion_squad_id</c> /
    /// <c>companion_squad_role</c> to its own ZDO; sibling members converge
    /// on the same leader independently without coordination RPCs.
    /// </summary>
    internal static class WildCompanionSquad
    {
        private const float CohesionRadius = 15f;

        public const string ZDO_SQUAD_ID   = "companion_squad_id";
        public const string ZDO_SQUAD_ROLE = "companion_squad_role";

        public static void ElectAndAssign(WildCompanionDresser self, WildCompanionSeed seed)
        {
            if (self == null || seed == null) return;
            if (!seed.EnableGroupCohesion) return;

            var selfView = self.GetComponent<ZNetView>();
            if (selfView == null || !selfView.IsValid()) return;

            var selfZdo = selfView.GetZDO();
            if (selfZdo == null) return;

            int selfHash = selfZdo.m_uid.GetHashCode();

            var candidates = new List<(ZDO zdo, GameObject go, int hash)>(8);
            candidates.Add((selfZdo, self.gameObject, selfHash));

            Collider[] buffer = s_overlapBuffer ?? (s_overlapBuffer = new Collider[32]);
            int hits = Physics.OverlapSphereNonAlloc(self.transform.position, CohesionRadius, buffer);

            for (int i = 0; i < hits; i++)
            {
                var collider = buffer[i];
                if (collider == null) continue;

                var otherDresser = collider.GetComponentInParent<WildCompanionDresser>();
                if (otherDresser == null || otherDresser == self) continue;

                var otherSeed = otherDresser.GetComponent<WildCompanionSeed>();
                if (otherSeed == null || otherSeed.Faction != seed.Faction) continue;

                var otherView = otherDresser.GetComponent<ZNetView>();
                if (otherView == null || !otherView.IsValid()) continue;
                var otherZdo = otherView.GetZDO();
                if (otherZdo == null) continue;
                if (string.IsNullOrEmpty(otherZdo.GetString(WildCompanionDresser.ZDO_DRESSED_FLAG, string.Empty)))
                    continue;

                int otherHash = otherZdo.m_uid.GetHashCode();

                bool seen = false;
                for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                {
                    if (candidates[candidateIndex].hash == otherHash) { seen = true; break; }
                }
                if (seen) continue;

                candidates.Add((otherZdo, otherDresser.gameObject, otherHash));

                if (candidates.Count >= Math.Max(1, seed.HardMaxGroupSize)) break;
            }

            int leaderHash = selfHash;
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].hash < leaderHash) leaderHash = candidates[i].hash;
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                candidate.zdo.Set(ZDO_SQUAD_ID,   leaderHash);
                candidate.zdo.Set(ZDO_SQUAD_ROLE, candidate.hash == leaderHash ? 1 : 0);
            }

            if (candidates.Count > 1)
            {
                Debug.Log($"[WildCompanionSquad] {seed.Faction} squad formed: " +
                          $"{candidates.Count} members, leader hash 0x{leaderHash:X8}.");
            }
        }

        [ThreadStatic] private static Collider[] s_overlapBuffer;
    }

    /// <summary>
    /// Server-side ticker that leashes non-leader wild companions to their
    /// squad leader by re-anchoring <see cref="CompanionIdleBehavior"/>'s
    /// home position each tick. Reuses the existing idle pathfinder so squad
    /// follow inherits its obstacle / unstuck handling.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WildCompanionSquadFollower : MonoBehaviour
    {
        private const float UpdateInterval = 1.25f;

        private CompanionIdleBehavior _idle;
        private ZNetView _nview;
        private int _cachedSquadId;
        private GameObject _leaderGo;

        private void Start()
        {
            _nview = GetComponent<ZNetView>();
            _idle  = GetComponent<CompanionIdleBehavior>();

            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                enabled = false;
                return;
            }

            StartCoroutine(TickRoutine());
        }

        private IEnumerator TickRoutine()
        {
            yield return new WaitForSeconds(1.0f);

            while (this != null && gameObject != null)
            {
                try { TickSquadFollow(); }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[WildCompanionSquadFollower] Tick failed: {ex.Message}");
                }
                yield return new WaitForSeconds(UpdateInterval);
            }
        }

        private void TickSquadFollow()
        {
            if (_nview == null || !_nview.IsValid()) return;
            var zdo = _nview.GetZDO();
            if (zdo == null) return;

            int squadId = zdo.GetInt(WildCompanionSquad.ZDO_SQUAD_ID, 0);
            if (squadId == 0) return;
            int role = zdo.GetInt(WildCompanionSquad.ZDO_SQUAD_ROLE, 0);
            if (role == 1) return;

            if (_leaderGo == null || squadId != _cachedSquadId)
            {
                _leaderGo = FindLeaderByHash(squadId);
                _cachedSquadId = squadId;
            }

            if (_leaderGo == null) return;
            if (_idle == null) return;

            Vector3 leaderPos = _leaderGo.transform.position;
            _idle.SetHomePosition(leaderPos);
        }

        private static GameObject FindLeaderByHash(int squadLeaderHash)
        {
            if (ZNetScene.instance == null) return null;

            var dressers = UnityEngine.Object.FindObjectsByType<WildCompanionDresser>(UnityEngine.FindObjectsSortMode.None);
            for (int i = 0; i < dressers.Length; i++)
            {
                var dresser = dressers[i];
                if (dresser == null) continue;
                var view = dresser.GetComponent<ZNetView>();
                if (view == null || !view.IsValid()) continue;
                var zdo = view.GetZDO();
                if (zdo == null) continue;
                if (zdo.m_uid.GetHashCode() == squadLeaderHash)
                    return dresser.gameObject;
            }
            return null;
        }
    }
}
