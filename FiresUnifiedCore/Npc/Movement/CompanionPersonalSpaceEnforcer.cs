using UnityEngine;
using FiresCore.Npc.Formation;

namespace FiresCore.Npc.Movement
{
    /// <summary>
    /// Runtime physics component that (a) keeps a tamed companion out of
    /// personal-space bubbles around the local player and sibling companions,
    /// and (b) makes tamed companions lightweight so the player can shove them
    /// but they cannot meaningfully shove back.
    ///
    /// SOFT PUSH (FixedUpdate):
    ///   Applies a soft acceleration away from any nearby player or peer
    ///   companion before a collision impulse can accumulate, so companions
    ///   don't stack or crowd the player during combat.
    ///
    /// ONE-WAY PUSH (mass):
    ///   Tamed companions are given a very low rigidbody mass (TAMED_COMPANION_MASS).
    ///   The local player character has ~60ï¿½ more mass, so:
    ///     - Player walks into companion ? companion gets knocked aside (player "pushes" it).
    ///     - Companion walks into player ? player barely moves.
    ///   Untamed (wild) companions are NOT affected ï¿½ they keep their default
    ///   prefab mass so combat feel (knockback, stance) is unchanged and they
    ///   can still physically interact with the player during a fight.
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

        // Tamed companions are given a very low rigidbody mass so the local
        // player (~60 kg default in Valheim) massively dominates any contact.
        // Result: player can walk into a companion and physically push it;
        // a companion walking into the player barely nudges them.
        // Untamed (wild) companions keep their default prefab mass so combat
        // feel is unaffected.
        private const float TAMED_COMPANION_MASS = 0.5f;

        private CompanionController _companion;
        private Rigidbody _rigidbody;
        private Character _character;

        private void Awake()
        {
            _companion = GetComponent<CompanionController>();
            _rigidbody = GetComponent<Rigidbody>();
            _character = GetComponent<Character>();
        }

        private void OnEnable()
        {
            // Apply low mass as soon as this companion is enabled so the
            // player always dominates in any physical contact.
            ApplyTamedMassIfNeeded();
        }

        /// <summary>
        /// Lowers the companion's rigidbody mass if it is tamed, so the
        /// local player can physically push it but it cannot meaningfully
        /// shove the player back. Called on enable and whenever taming state
        /// changes.
        /// </summary>
        public void ApplyTamedMassIfNeeded()
        {
            if (_companion == null || _rigidbody == null) return;
            if (_companion.isTamed)
                _rigidbody.mass = TAMED_COMPANION_MASS;
        }

        private void FixedUpdate()
        {
            if (_rigidbody == null || _rigidbody.isKinematic) return;
            if (_companion == null || !_companion.isTamed) return;
            if (_character == null || _character.IsDead()) return;

            // Self-healing mass cap. Several other systems (CompanionPrefabManager
            // setup, CompanionRandomLoadout, vault restore, respawn manager) write
            // to rigidbody.mass AFTER our OnEnable runs, leaving the companion at
            // 50+ kg instead of TAMED_COMPANION_MASS. At parity-with-player the
            // companion can shove the player around â€” the bug the user reports.
            // Re-asserting the cap every FixedUpdate is cheap and lets all the
            // other writers do whatever they want; we simply override.
            if (_rigidbody.mass > TAMED_COMPANION_MASS)
                _rigidbody.mass = TAMED_COMPANION_MASS;

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

