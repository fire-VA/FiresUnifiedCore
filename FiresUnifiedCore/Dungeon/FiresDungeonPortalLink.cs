using UnityEngine;

namespace FiresCore.Dungeon
{
    /// <summary>
    /// Generic safety-net teleport linker, attached to a dungeon's Portal at ZNetScene registration time (every
    /// peer, server + client). Teleport.m_targetPoint is a plain MonoBehaviour field, NOT ZDO-synced, so it must
    /// be valid LOCALLY on every peer or Teleport.Interact returns false ("nothing happens / blocked").
    ///
    /// The bake already cross-links gateway&lt;-&gt;exit within the single Portal subtree, so this is belt-and-
    /// suspenders: on Start it re-pairs any Teleport whose m_targetPoint went null. It first pairs the two
    /// teleports inside its OWN Portal (the common case); if one is somehow missing it falls back to a
    /// deterministic world search (the interior exit sits ~ (0, InteriorYOffset, 0) above the surface gateway).
    ///
    /// Lifted from FiresMausoleum.Dungeon.FiresMausoleumPortalLink. The gateway/exit name prefixes and the interior
    /// Y offset come from the owning mod via <see cref="Configure"/> (serialized so they survive the prefab clone).
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
            foreach (var t in teleports)
            {
                if (t == null) continue;
                string n = t.gameObject.name;
                if (n.StartsWith(_gatewayNamePrefix)) gateway = t;
                else if (n.StartsWith(_exitNamePrefix)) exit = t;
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
            Vector3 mp = mine.transform.position;
            // a gateway's partner is +offset in Y; an exit's partner is -offset in Y.
            Vector3 want = mp + new Vector3(0f, mineIsGateway ? _interiorYOffset : -_interiorYOffset, 0f);

            Teleport best = null;
            float bestSqr = float.MaxValue;
            foreach (var t in Object.FindObjectsByType<Teleport>(FindObjectsSortMode.None))
            {
                if (t == null || t == mine) continue;
                Vector3 d = t.transform.position - want;
                if (Mathf.Abs(d.x) > MatchXZTolerance || Mathf.Abs(d.z) > MatchXZTolerance || Mathf.Abs(d.y) > MatchYTolerance)
                    continue;
                float sqr = d.sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; best = t; }
            }
            return best;
        }
    }
}
