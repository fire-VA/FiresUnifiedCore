using UnityEngine;
using FiresCore.Npc.Formation;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// Keeps a tamed companion out of the personal space of players and sibling companions with a mass-independent
    /// acceleration field in FixedUpdate, so the player can walk through the squad. Mass is left at the prefab baseline:
    /// lowering it to let players win collisions made every monster hit launch the companion.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class CompanionPersonalSpaceEnforcer : MonoBehaviour
    {
        // Tunables
        private const float PlayerRadius        = 1.6f;   // companion will not enter within this distance of any player
        private const float PlayerPushStrength = 28f;    // accel m/s^2 at zero distance, scales linearly down to PlayerRadius
        private const float CompanionRadius     = 1.4f;   // personal space between sibling companions
        private const float CompanionPushStrength = 18f;
        private const float MaxAccel            = 40f;    // safety clamp so we never launch a companion

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

            // Squared radii - lets us skip sqrt for everyone outside the bubble,
            // which is the common case once a few companions are loaded.
            const float playerRadiusSq    = PlayerRadius * PlayerRadius;
            const float companionRadiusSq = CompanionRadius * CompanionRadius;

            // Players
            var players = Player.GetAllPlayers();
            if (players != null)
            {
                int pcount = players.Count;
                for (int i = 0; i < pcount; i++)
                {
                    var player = players[i];
                    if (player == null || player.IsDead()) continue;

                    Vector3 offset = myPos - player.transform.position;
                    offset.y = 0f;
                    float sqrDistance = offset.sqrMagnitude;
                    if (sqrDistance < 0.0001f || sqrDistance >= playerRadiusSq) continue;

                    float dist = Mathf.Sqrt(sqrDistance);
                    float overlap = 1f - (dist / PlayerRadius);
                    push += (offset / dist) * (overlap * PlayerPushStrength);
                }
            }

            // Sibling companions
            var all = CompanionController.AllCompanions;
            int ccount = all.Count;
            for (int i = 0; i < ccount; i++)
            {
                var other = all[i];
                if (other == null || other == _companion) continue;
                if (!other.isTamed) continue;

                Vector3 offset = myPos - other.transform.position;
                offset.y = 0f;
                float sqrDistance = offset.sqrMagnitude;
                if (sqrDistance < 0.0001f || sqrDistance >= companionRadiusSq) continue;

                float dist = Mathf.Sqrt(sqrDistance);
                float overlap = 1f - (dist / CompanionRadius);
                push += (offset / dist) * (overlap * CompanionPushStrength);
            }

            if (push.sqrMagnitude > 0.0001f)
            {
                if (push.sqrMagnitude > MaxAccel * MaxAccel)
                    push = push.normalized * MaxAccel;
                _rigidbody.AddForce(push, ForceMode.Acceleration);
            }
        }

            }
        }

