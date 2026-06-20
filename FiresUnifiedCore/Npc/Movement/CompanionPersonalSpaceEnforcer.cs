using UnityEngine;
using FiresCore.Npc.Formation;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// Runtime physics component that keeps a tamed companion out of personal-space bubbles around
    /// the local player and sibling companions, so the player can move through their squad without
    /// the companions hard-blocking them.
    ///
    /// Implemented as a soft acceleration field in FixedUpdate using <see cref="ForceMode.Acceleration"/>,
    /// which is mass-independent — the companion is pushed away from the player regardless of how heavy
    /// it is. We deliberately do NOT mutate the rigidbody mass: previous revisions forced it to 0.5 kg
    /// to make the player "win" collisions, but the side effect was that any monster hit
    /// (<c>HitData.m_pushForce</c>) launched the companion across the map. Keeping mass at the prefab
    /// baseline (~50 kg, set by <c>CompanionPrefabManager</c> / scale-multiplied at spawn) lets enemy
    /// knockback and stagger feel right; this field is what handles the player-yields-aside behaviour.
    ///
    [RequireComponent(typeof(Rigidbody))]
    public class CompanionPersonalSpaceEnforcer : MonoBehaviour
    {
        // Tunables
        private const float PLAYER_RADIUS        = 1.6f;   // companion will not enter within this distance of any player
        private const float PLAYER_PUSH_STRENGTH = 28f;    // accel m/s^2 at zero distance, scales linearly down to PLAYER_RADIUS
        private const float COMPANION_RADIUS     = 1.4f;   // personal space between sibling companions
        private const float COMPANION_PUSH_STRENGTH = 18f;
        private const float MAX_ACCEL            = 40f;    // safety clamp so we never launch a companion

        private CompanionController _companion;
        private Rigidbody _rigidbody;
        private Character _character;

        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _rigidbody = GetComponent<Rigidbody>();
            _character = GetComponent<Character>();
        }

        private void FixedUpdate()
        {
            if (_rigidbody == null || _rigidbody.isKinematic) return;
            if (_companion == null || !_companion.isTamed) return;
            if (_character == null || _character.IsDead()) return;

            Vector3 myPos = transform.position;
            Vector3 push = Vector3.zero;

            // Squared radii ï¿½ lets us skip sqrt for everyone outside the bubble,
            // which is the common case once a few companions are loaded.
            const float playerRadiusSq    = PLAYER_RADIUS * PLAYER_RADIUS;
            const float companionRadiusSq = COMPANION_RADIUS * COMPANION_RADIUS;

            // ?? Players ????????????????????????????????????????????????????
            var players = Player.GetAllPlayers();
            if (players != null)
            {
                int pcount = players.Count;
                for (int i = 0; i < pcount; i++)
                {
                    var p = players[i];
                    if (p == null || p.IsDead()) continue;

                    Vector3 d = myPos - p.transform.position;
                    d.y = 0f;
                    float sq = d.sqrMagnitude;
                    if (sq < 0.0001f || sq >= playerRadiusSq) continue;

                    float dist = Mathf.Sqrt(sq);
                    float t = 1f - (dist / PLAYER_RADIUS);
                    push += (d / dist) * (t * PLAYER_PUSH_STRENGTH);
                }
            }

            // ?? Sibling companions ????????????????????????????????????????
            var all = CompanionController.AllCompanions;
            int ccount = all.Count;
            for (int i = 0; i < ccount; i++)
            {
                var other = all[i];
                if (other == null || other == _companion) continue;
                if (!other.isTamed) continue;

                Vector3 d = myPos - other.transform.position;
                d.y = 0f;
                float sq = d.sqrMagnitude;
                if (sq < 0.0001f || sq >= companionRadiusSq) continue;

                float dist = Mathf.Sqrt(sq);
                float t = 1f - (dist / COMPANION_RADIUS);
                push += (d / dist) * (t * COMPANION_PUSH_STRENGTH);
            }

            if (push.sqrMagnitude > 0.0001f)
            {
                if (push.sqrMagnitude > MAX_ACCEL * MAX_ACCEL)
                    push = push.normalized * MAX_ACCEL;
                _rigidbody.AddForce(push, ForceMode.Acceleration);
            }
        }

            }
        }

