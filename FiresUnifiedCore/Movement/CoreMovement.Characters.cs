using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Movement
{
    /// <summary>
    /// 0.2.92x ([ghost], Fire 10-27 "companions are meant to be able to use every single movement option that we have"): add-only Character
    /// cores of Core's double jump and run slide for companions. The player's path (Player_Update -> TryDoubleJump / TrySlide) is unchanged.
    /// Same features (Double jump / Run slide switches, the stand-down), same stamina settings, same force / speed / seconds, same clip slots;
    /// owner-only; one per body at a time. "[Movement] double jump | run slide by <name> (companion): ..." / "refused (<why>)" at Debug.
    /// </summary>
    public static partial class CoreMovement
    {
        private static readonly Dictionary<Character, (Vector3 dir, float until, float speed)> s_bodySlides = new Dictionary<Character, (Vector3, float, float)>();
        private static readonly HashSet<Character> s_bodyAirJumped = new HashSet<Character>();

        private static string BodyRefusal(Character who)
        {
            if (who == null) return "no body";
            if (who is Player) return "a player (uses the player path)";
            var nv = who.GetComponent<ZNetView>();
            if (nv == null || !nv.IsValid() || !nv.IsOwner()) return "not the owner";
            if (who.IsDead() || who.InAttack() || who.InDodge() || who.IsSwimming() || who.IsAttached()) return "busy (dead / attacking / dodging / swimming / attached)";
            if (FiresCore.Movement.Climb.HasRun(who)) return "climbing";
            return null;
        }

        private static float BodyScale(Character who) => Mathf.Clamp(who.transform.localScale.y, MinSizeScale, MaxSizeScale);

        /// <summary>The second jump for a companion (in the air, once until it lands).</summary>
        public static bool TryDoubleJumpFor(Character who, out string why)
        {
            why = BodyRefusal(who);
            if (why == null && !DoubleJumpFeature.IsActive) why = "Double jump is off";
            if (why == null && StoodDown) why = "movement stood down";
            if (why == null && who.IsOnGround()) { s_bodyAirJumped.Remove(who); why = "on the ground (jump first)"; }
            if (why == null && s_bodyAirJumped.Contains(who)) why = "already used in this jump";
            float cost = s_jumpStamina != null ? s_jumpStamina.Value : 10f;
            if (why == null && !who.HaveStamina(cost)) why = $"stamina < {cost:0}";
            if (why != null) { FiresCore.Logging.FiresLogger.LogDebug($"[Movement] double jump by {who?.m_name} (companion): refused ({why})"); return false; }
            s_bodyAirJumped.Add(who);
            who.UseStamina(cost);
            var rb = who.m_body;
            if (rb == null) { why = "no rigidbody"; return false; }
            Vector3 v = rb.velocity;
            v.y = Mathf.Max(v.y, 0f) + who.m_jumpForce * BodyScale(who) * DoubleJumpForceShare;
            rb.velocity = v;
            bool clip = PlayClip(who, "core.double_jump");
            if (!clip) who.GetComponent<ZSyncAnimation>()?.SetTrigger("jump");
            FiresCore.Logging.FiresLogger.LogDebug($"[Movement] double jump by {who.m_name} (companion): up {v.y:0.0}, {cost:0} stamina, anim {(clip ? "clip slot" : "vanilla jump")}");
            return true;
        }

        /// <summary>The run slide for a companion along <paramref name="dir"/> (flat; on the ground, running).</summary>
        public static bool TrySlideFor(Character who, Vector3 dir, out string why)
        {
            why = BodyRefusal(who);
            if (why == null && !RunSlideFeature.IsActive) why = "Run slide is off";
            if (why == null && StoodDown) why = "movement stood down";
            if (why == null && !who.IsOnGround()) why = "in the air";
            if (why == null && s_bodySlides.TryGetValue(who, out var cur) && Time.time < cur.until) why = "already sliding";
            float cost = s_slideStamina != null ? s_slideStamina.Value : 8f;
            if (why == null && !who.HaveStamina(cost)) why = $"stamina < {cost:0}";
            if (why != null) { FiresCore.Logging.FiresLogger.LogDebug($"[Movement] run slide by {who?.m_name} (companion): refused ({why})"); return false; }
            who.UseStamina(cost);
            dir.y = 0f; if (dir.sqrMagnitude < 0.01f) dir = who.transform.forward; dir.y = 0f; dir.Normalize();
            float secs = s_slideSeconds != null ? s_slideSeconds.Value : 1f, speed = who.m_runSpeed * (s_slideSpeed != null ? s_slideSpeed.Value : 1.25f) * BodyScale(who);
            s_bodySlides[who] = (dir, Time.time + secs, speed);
            bool clip = PlayClip(who, "core.run_slide");
            if (!clip) who.GetComponent<ZSyncAnimation>()?.SetBool("crouching", true);
            if (who.GetComponent<BodySlide>() == null) who.gameObject.AddComponent<BodySlide>();
            FiresCore.Logging.FiresLogger.LogDebug($"[Movement] run slide by {who.m_name} (companion): {secs:0.0} s at {speed:0.0} m/s, {cost:0} stamina, anim {(clip ? "clip slot" : "vanilla crouch")}");
            return true;
        }

        internal static bool IsSliding(Character who) => who != null && s_bodySlides.TryGetValue(who, out var s) && Time.time < s.until;

        // the companion slide's velocity each physics step (the player's slide runs in Player_Update's TickSlide)
        private sealed class BodySlide : MonoBehaviour
        {
            private Character m_who;
            private void Awake() => m_who = GetComponent<Character>();
            private void FixedUpdate()
            {
                if (m_who == null || !s_bodySlides.TryGetValue(m_who, out var s)) return;
                if (Time.time >= s.until || m_who.IsDead() || !m_who.IsOnGround())
                {
                    s_bodySlides.Remove(m_who);
                    m_who.GetComponent<ZSyncAnimation>()?.SetBool("crouching", false);
                    FiresCore.Logging.FiresLogger.LogDebug($"[Movement] run slide by {m_who.m_name} (companion): ended");
                    return;
                }
                var rb = m_who.m_body;
                if (rb == null) return;
                float secs = Mathf.Max(MinSlideSeconds, s_slideSeconds != null ? s_slideSeconds.Value : 1f);
                float left = Mathf.Clamp01((s.until - Time.time) / secs);
                Vector3 v = s.dir * Mathf.Lerp(m_who.m_walkSpeed, s.speed, left);
                v.y = rb.velocity.y;
                rb.velocity = v;
            }
        }
    }
}
