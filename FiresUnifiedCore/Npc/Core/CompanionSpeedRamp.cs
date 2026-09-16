using System.Runtime.CompilerServices;
using FiresCore.Npc.Patrol;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc.Core
{
    /// <summary>
    /// Eases companions from a walk up to a run, ported from FiresValcast's NaturalWalk. Companions move through
    /// Character.UpdateWalking's m_runSpeed branch with per-archetype top speeds, so the ramp runs between the
    /// companion's own walk and run speeds, triggers on its move and run intent, and writes m_runSpeed only for one
    /// UpdateWalking call, restored by a finalizer. It never touches m_moveDir, and each patch skips the other's
    /// subject.
    /// </summary>
    [HarmonyPatch]
    public static class CompanionSpeedRamp
    {
        private const float MinRampSeconds = 0.05f;

        // m_moveDir is protected and m_running is private on Character; read them with fast Harmony field refs.
        private static readonly AccessTools.FieldRef<Character, Vector3> MoveDir =
            AccessTools.FieldRefAccess<Character, Vector3>("m_moveDir");
        private static readonly AccessTools.FieldRef<Character, bool> Running =
            AccessTools.FieldRefAccess<Character, bool>("m_running");

        private sealed class Ramp { public float Current = -1f; }

        // Per-companion ramp state, auto-released when the Character is collected (no manual cleanup / leak).
        private static readonly ConditionalWeakTable<Character, Ramp> _ramps =
            new ConditionalWeakTable<Character, Ramp>();

        // DBSM patrol easing state kept separate from the follow-ramp so the two features never share a
        // running value (a static patrol NPC and a follower are different contexts).
        private static readonly ConditionalWeakTable<Character, Ramp> _patrolRamps =
            new ConditionalWeakTable<Character, Ramp>();

        private sealed class Mul { public float Value = 1f; }

        // Emergency follow catch-up multiplier, published by CompanionAI while a follower is in its
        // Sprinting tier (owner pulled far ahead). >1 lets the run ramp target ABOVE the archetype run
        // speed so the follower can actually CLOSE the gap against an owner moving at full run - without
        // it, Sprinting == Running (both cap at m_runSpeed) and a fast owner is uncatchable.
        private static readonly ConditionalWeakTable<Character, Mul> _catchUp =
            new ConditionalWeakTable<Character, Mul>();

        /// <summary>
        /// Set the emergency catch-up run multiplier for a follower (1.0 = normal run, &gt;1 = boosted).
        /// CompanionAI calls this each follow tick: the Sprinting tier passes the boost, every other tier
        /// passes 1.0 to clear it. Clamped to &gt;= 1 so it can only ever speed a companion up, never slow it.
        /// </summary>
        public static void SetCatchUpMultiplier(Character ch, float mul)
        {
            if (ch == null) return;
            _catchUp.GetOrCreateValue(ch).Value = Mathf.Max(1f, mul);
        }

        private static float GetCatchUp(Character ch) =>
            _catchUp.TryGetValue(ch, out var catchUp) ? catchUp.Value : 1f;

        // Only the SPRINTING follow tier snaps straight to full speed; RUNNING and below RAMP. The catch-up
        // table carries the tier's boost (CompanionAI SprintCatchUpBoost=1.5, RunCatchUpBoost=1.15), so a value
        // at/above this threshold means "sprint emergency, close the gap NOW" — snap; anything below eases. This
        // keeps genuine fall-behinds instant (no mosey) while restoring smooth accel/decel for normal keep-pace
        // following (the "always sprinting, never ramps" regression was this snap firing for the Running tier too).
        private const float SprintSnapMultiplier = 1.35f;

        // Which speed field(s) this frame's prefix overrode, and their real values for the finalizer to
        // restore. -1 = untouched. The companion branch uses Run only; DBSM patrol uses Walk OR Run
        // depending on the active tier. Struct so Harmony isolates it per call.
        private struct RampState { public float Walk; public float Run; }

        [HarmonyPatch(typeof(Character), "UpdateWalking")]
        [HarmonyPrefix]
        private static void UpdateWalking_Prefix(Character __instance, float dt, ref RampState __state)
        {
            __state.Walk = -1f;
            __state.Run = -1f;
            if (__instance == null) return;

            // DBSM patrol takes priority: while a fresh patrol multiplier is published it owns the active
            // tier's speed scalar. Independent of the cosmetic follow-ramp toggle (a slow/fast patrol is a
            // deliberate authored behaviour, not the natural-walk polish).
            if (PatrolSpeedState.TryGet(__instance, out float mul, out bool run))
            {
                ApplyPatrolRamp(__instance, dt, ref __state, mul, run);
                return;
            }

            if (CompanionPatches.GetCachedCompanion(__instance) == null) return; // companions only

            float catchUp = GetCatchUp(__instance);

            if (!MovementRampConfig.Enabled)
            {
                // Smoothing off: no ramp, but still honour an active catch-up boost directly so the
                // Sprinting tier can exceed the archetype run speed (else it equals Running and a fast
                // owner stays uncatchable). The finalizer restores the real m_runSpeed we stash here.
                if (catchUp > 1.001f)
                {
                    __state.Run = __instance.m_runSpeed;
                    __instance.m_runSpeed *= catchUp;
                }
                return;
            }

            float baseRun = __instance.m_runSpeed;         // real archetype run speed (finalizer restores THIS)
            float max = baseRun * catchUp;                 // ramp target - boosted while sprinting to catch up
            float min = Mathf.Min(__instance.m_walkSpeed, baseRun);
            if (max - min < 0.01f) return; // nothing to ramp (no run/walk gap)

            var ramp = _ramps.GetOrCreateValue(__instance);
            if (ramp.Current < 0f) ramp.Current = min;

            // Ramp UP toward full run speed only when the companion actually intends to run; otherwise ease
            // back down toward walk speed. m_running is last frame's value (set later in UpdateWalking) - a
            // one-frame lag that is invisible.
            bool runningIntent = Running(__instance) && MoveDir(__instance).sqrMagnitude > 0.0001f;
            float target = runningIntent ? max : min;
            float rate = (max - min) / Mathf.Max(MinRampSeconds, MovementRampConfig.Seconds);

            // FLOOR AT CURRENT PACE. A follower enters the run tier FROM A JOG (m_speed=4 > m_walkSpeed=2),
            // and follow-tier hysteresis bounces it run<->jog while catching up. Ramping run speed up from
            // WALK pace made starting to run an instant DECELERATION to walk pace, and the bounce meant the
            // ramp never topped out - companions shuffled after a jogging owner forever (the Jason-walk
            // regression). Flooring the ramp at the body's actual horizontal pace keeps run-entry seamless
            // from any gait, while a standing start (pace ~0) still gets the full smooth take-off.
            if (runningIntent)
            {
                var body = __instance.m_body;
                if (body != null)
                {
                    Vector3 vel = body.linearVelocity;
                    vel.y = 0f;
                    float pace = vel.magnitude;
                    if (pace > ramp.Current) ramp.Current = Mathf.Min(pace, max);
                }
            }

            // SPRINT SNAPS, RUN RAMPS. When the follow path signals a SPRINT-tier catch-up (owner pulled far
            // ahead, boost at/above SprintSnapMultiplier) we snap straight to full speed so a distant follower
            // closes instantly instead of moseying. The RUNNING tier and below ease over RampSeconds, so a
            // follower keeping pace accelerates/decelerates smoothly rather than blasting at max the whole time.
            if (catchUp >= SprintSnapMultiplier)
                ramp.Current = max;                                                 // sprint emergency → instant
            else
                ramp.Current = Mathf.MoveTowards(ramp.Current, target, rate * dt);  // run/jog → smooth ramp

            __state.Run = baseRun;                // restore the REAL base run speed (not the boosted target)
            __instance.m_runSpeed = ramp.Current; // ramped run speed for THIS UpdateWalking only
        }

        // Ease the ACTIVE tier's speed field toward base×mul and record the real value for restore. Base is
        // the archetype-scaled walk speed (m_walkSpeed on entry — the finalizer restores it every frame, so
        // it's never the ramped value); DBSM composes ON TOP of it, never clobbering the archetype cache.
        // Even in the run tier the target is a multiple of base WALK (design §1.4: the tier only selects the
        // animation + which field the motor reads), so a threshold-crossing pace ≈ 1.15× walk, not full run.
        private static void ApplyPatrolRamp(Character ch, float dt, ref RampState state, float mul, bool run)
        {
            float baseWalk = ch.m_walkSpeed;
            float targetSpeed = baseWalk * Mathf.Max(0.01f, mul);

            var ramp = _patrolRamps.GetOrCreateValue(ch);
            if (ramp.Current < 0f) ramp.Current = baseWalk;

            // Ease at "walk→run span per RampSeconds" so the feel matches the follow-ramp regardless of the
            // target; the section curve already changes u gradually, this just smooths tier/boundary steps.
            float refSpan = Mathf.Max(0.5f, ch.m_runSpeed - baseWalk);
            float rate = refSpan / Mathf.Max(MinRampSeconds, MovementRampConfig.Seconds);
            ramp.Current = Mathf.MoveTowards(ramp.Current, targetSpeed, rate * dt);

            if (run)
            {
                state.Run = ch.m_runSpeed;   // motor reads m_runSpeed when m_run is set (SetWalk(false) upstream)
                ch.m_runSpeed = ramp.Current;
            }
            else
            {
                state.Walk = ch.m_walkSpeed; // motor reads m_walkSpeed when m_walk is set (SetWalk(true) upstream)
                ch.m_walkSpeed = ramp.Current;
            }
        }

        [HarmonyPatch(typeof(Character), "UpdateWalking")]
        [HarmonyFinalizer]
        private static void UpdateWalking_Finalizer(Character __instance, RampState __state)
        {
            if (__instance == null) return;
            if (__state.Walk >= 0f) __instance.m_walkSpeed = __state.Walk; // always restore what we overrode
            if (__state.Run >= 0f) __instance.m_runSpeed = __state.Run;
        }
    }
}
