using UnityEngine;

namespace FiresCore.Ravens
{
    /// <summary>
    /// Plays a raven model's own animator (vanilla raven.controller) for Hugin and Munin. The controller has no looping flight
    /// (only one-shots), so while flying it plays its "Fly" state (HuginFlyAway, 7.8 s) and jumps back to the start of a slice
    /// whenever it passes the slice's end: a steady wing-beat (a looped slice of vanilla's FlyAway clip). On the
    /// ground or perched it plays "Idle"; <see cref="Talk"/> plays its croak.
    /// Nothing else writes to this animator: the bird's Character and ZSyncAnimation bind to the hidden Bat animator, so no
    /// parameter the engine writes is ever missing here.
    /// </summary>
    public sealed class RavenAnimDriver : MonoBehaviour
    {
        /// <summary>The looped slice of the fly clip, as normalized time (tunable from test shots).</summary>
        public static float SliceStart = 0.35f;
        public static float SliceEnd = 0.6f;

        /// <summary>Pets set this (they have no Character); null = judge from the height above the ground.</summary>
        public bool? FlyingOverride;

        // Vanilla raven.controller (1.0 rip) has no "Fly" state, so nothing played and the bird slid in its entry
        // pose. Its Base Layer states are Idle (a blend tree), Flyin, Fly away, away, talk, teleportin, Poff, poff. The fly
        // state is resolved once per animator from these, first found wins, and every probe is logged.
        private static readonly string[] FlyCandidates = { "Fly away", "Flyin", "away", "Fly" };
        private static readonly string[] ProbeNames = { "Idle", "idle", "Fly", "fly", "Fly away", "Flyin", "away", "talk", "teleportin", "Poff", "poff" };
        private static bool s_probeLogged;
        private int FlyState;        private static readonly int IdleState = Animator.StringToHash("Idle");
        private static readonly int TalkTrigger = Animator.StringToHash("talk");

        private Animator _animator;
        private bool _flying;
        private bool _started;

        public Animator Animator => _animator != null ? _animator : (_animator = GetComponent<Animator>());

        private void OnEnable()
        {
            _started = false;
            if (Animator != null) _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            ResolveStates();
        }

        private void ResolveStates()
        {
            Animator anim = Animator;
            if (anim == null || anim.runtimeAnimatorController == null) return;
            FlyState = 0;
            foreach (string n in FlyCandidates)
            {
                int h = Animator.StringToHash(n);
                if (anim.HasState(0, h)) { FlyState = h; break; }
            }
            if (s_probeLogged) return;
            s_probeLogged = true;
            var found = new System.Collections.Generic.List<string>();
            foreach (string n in ProbeNames) if (anim.HasState(0, Animator.StringToHash(n))) found.Add(n);
            string fly = "none";
            foreach (string n in FlyCandidates) if (Animator.StringToHash(n) == FlyState) { fly = n; break; }
            FiresCore.Logging.FiresLogger.LogDebug($"[Ravens] animator '{anim.runtimeAnimatorController.name}' states found: [{string.Join(", ", found.ToArray())}]; fly state '{fly}', idle {(anim.HasState(0, IdleState) ? "Idle" : "missing")}");
        }

        private void Update()
        {
            Animator anim = Animator;
            if (anim == null || !anim.isActiveAndEnabled || anim.runtimeAnimatorController == null) return;
            bool flying = FlyingOverride ?? Airborne();
            AnimatorStateInfo state = anim.GetCurrentAnimatorStateInfo(0);

            if (flying)
            {
                if (!_started || !_flying || FlyState == 0 || state.shortNameHash != FlyState)
                {
                    if (FlyState == 0) ResolveStates();
                    if (FlyState != 0) anim.CrossFade(FlyState, 0.15f, 0, SliceStart);
                }
                else if (state.normalizedTime >= SliceEnd)
                {
                    anim.Play(FlyState, 0, SliceStart);
                }
            }
            else if (!_started || _flying)
            {
                if (anim.HasState(0, IdleState)) anim.CrossFade(IdleState, 0.2f, 0, 0f);
            }
            _flying = flying;
            _started = true;
        }

        /// <summary>A croak: the raven controller's own "talk" trigger.</summary>
        public void Talk()
        {
            Animator anim = Animator;
            if (anim != null && anim.isActiveAndEnabled) anim.SetTrigger(TalkTrigger);
        }

        /// <summary>More than 0.4 m above the solid ground below (the transform is synced, so every peer agrees).</summary>
        private bool Airborne()
        {
            Vector3 p = transform.position;
            if (ZoneSystem.instance == null || !ZoneSystem.instance.GetSolidHeight(p, out float ground)) return true;
            return p.y - ground > 0.4f;
        }
    }
}
