using UnityEngine;

namespace FiresCore.Dungeon
{
    /// <summary>
    /// Safety net that re-pairs a dungeon portal's teleports on every peer. Teleport.m_targetPoint isn't synced, so a
    /// null target makes Interact fail; on Start any unpaired teleport is linked within its own portal, or by a
    /// deterministic search along the interior offset. Names and offset come from <see cref="Configure"/>. Lifted from
    /// FiresMausoleum.
    /// </summary>
    public sealed class FiresDungeonPortalLink : MonoBehaviour
    {
        [SerializeField] private string _gatewayNamePrefix = "Gateway";
        [SerializeField] private string _exitNamePrefix = "Exit";
        [SerializeField] private float _interiorYOffset = 5000f;

        private const float MatchXZTolerance = 6f;   // gateway/exit share XZ within the prefab footprint
        private const float MatchYTolerance = 50f;   // generous: exit local y is ~offset+1, gateway ~0..3

        /// <summary>Set the name prefixes + interior offset from a spec (call right after AddComponent on the prefab).</summary>
        public void Configure(string gatewayNamePrefix, string exitNamePrefix, float interiorYOffset)
        {
            if (!string.IsNullOrEmpty(gatewayNamePrefix)) _gatewayNamePrefix = gatewayNamePrefix;
            if (!string.IsNullOrEmpty(exitNamePrefix)) _exitNamePrefix = exitNamePrefix;
            if (interiorYOffset > 0f) _interiorYOffset = interiorYOffset;
        }

        private void Start()
        {
            try { LinkLocalPair(); } catch { /* never throw from a peer's Start */ }
        }

        private void LinkLocalPair()
        {
            var teleports = GetComponentsInChildren<Teleport>(true);
            Teleport gateway = null, exit = null;
            foreach (var teleport in teleports)
            {
                if (teleport == null) continue;
                string objectName = teleport.gameObject.name;
                if (objectName.StartsWith(_gatewayNamePrefix)) gateway = teleport;
                else if (objectName.StartsWith(_exitNamePrefix)) exit = teleport;
            }

            if (gateway != null && exit != null)
            {
                if (gateway.m_targetPoint == null) gateway.m_targetPoint = exit;
                if (exit.m_targetPoint == null) exit.m_targetPoint = gateway;
                return;
            }

            // Fallback: a teleport in this Portal lost its partner (separate-clone edge case). Find the partner
            // among all loaded Teleports by the deterministic exit = gateway + (0, offset, 0) relationship.
            Teleport mine = gateway ?? exit;
            if (mine == null || mine.m_targetPoint != null) return;
            Teleport partner = FindPartnerByPosition(mine, mine == gateway);
            if (partner == null) return;
            mine.m_targetPoint = partner;
            if (partner.m_targetPoint == null) partner.m_targetPoint = mine;
        }

        private Teleport FindPartnerByPosition(Teleport mine, bool mineIsGateway)
        {
            Vector3 ownPosition = mine.transform.position;
            // a gateway's partner is +offset in Y; an exit's partner is -offset in Y.
            Vector3 want = ownPosition + new Vector3(0f, mineIsGateway ? _interiorYOffset : -_interiorYOffset, 0f);

            Teleport best = null;
            float bestSqr = float.MaxValue;
            foreach (var candidate in Object.FindObjectsByType<Teleport>(FindObjectsSortMode.None))
            {
                if (candidate == null || candidate == mine) continue;
                Vector3 offset = candidate.transform.position - want;
                if (Mathf.Abs(offset.x) > MatchXZTolerance || Mathf.Abs(offset.z) > MatchXZTolerance || Mathf.Abs(offset.y) > MatchYTolerance)
                    continue;
                float sqr = offset.sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; best = candidate; }
            }
            return best;
        }
    }
}
