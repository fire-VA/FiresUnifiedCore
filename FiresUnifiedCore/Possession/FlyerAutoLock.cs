using System;
using BepInEx.Configuration;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Possession
{
    /// <summary>
    /// When a possessed FLYING creature attacks with nothing under
    /// the crosshair, the attack locks onto the nearest valid enemy ahead (within its attack range, in a 45 deg cone, any height up to the
    /// range); during the swing the body turns to face it and eases its height toward the target's centre. Uses the shared engine's
    /// add-only PossessionEngine.AutoTarget hook; the crosshair (and a test's AimOverride) still win. Config "Flyer attack auto-lock",
    /// default ON, flyers only.
    /// </summary>
    internal static class FlyerAutoLock
    {
        private const float ConeDegrees = 45f, RangePad = 1.0f, LockSeconds = 1.5f, HeightSpeed = 3f, TurnDegreesPerSecond = 540f;
        private static ConfigEntry<bool> s_on;
        private static Character s_target, s_attacker;
        private static float s_until;
        private static Runner s_runner;

        internal static void Bind(ConfigFile config)
        {
            s_on = config.Bind("Possession", "Flyer attack auto-lock", true,
                "CLIENT. A possessed flying creature's attack locks onto the nearest enemy ahead within its attack range (any height) when nothing is under the crosshair, and turns and eases its height toward it during the swing.");
            FiresPossession.PossessionEngine.AutoTarget = Pick;
        }

        private static readonly Collider[] s_hits = new Collider[64];
        private static readonly int s_mask = LayerMask.GetMask("character", "character_net", "character_noenv");

        private static Character Pick(Character attacker)
        {
            if (s_on == null || !s_on.Value || attacker == null || !attacker.IsFlying()) return null;
            var h = attacker as Humanoid;
            var weapon = h != null ? h.GetCurrentWeapon() : null;
            float range = (weapon != null ? Mathf.Max(weapon.m_shared.m_aiAttackRange, weapon.m_shared.m_attack != null ? weapon.m_shared.m_attack.m_attackRange : 0f) : 2f) + RangePad;
            Vector3 from = attacker.GetCenterPoint();
            Vector3 fwd = attacker.transform.forward; fwd.y = 0f; fwd = fwd.sqrMagnitude > 1e-4f ? fwd.normalized : Vector3.forward;
            Character best = null; float bestD = float.MaxValue;
            int n = Physics.OverlapSphereNonAlloc(from, range, s_hits, s_mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = s_hits[i] != null ? s_hits[i].GetComponentInParent<Character>() : null;
                if (c == null || c == attacker || c.IsDead() || c == Player.m_localPlayer) continue;
                if (!BaseAI.IsEnemy(attacker, c)) continue;
                Vector3 to = c.GetCenterPoint() - from;
                Vector3 flat = new Vector3(to.x, 0f, to.z);
                if (flat.sqrMagnitude > 1e-4f && Vector3.Angle(fwd, flat) > ConeDegrees) continue;
                float d = to.magnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best != null)
            {
                s_target = best; s_attacker = attacker; s_until = Time.time + LockSeconds;
                EnsureRunner();
                float dy = best.GetCenterPoint().y - from.y;
                FiresLogger.LogDebug($"[Possession] flyer auto-lock: {Utils.GetPrefabName(attacker.gameObject)} -> {Utils.GetPrefabName(best.gameObject)} at {bestD:0.0} m (height {dy:+0.0;-0.0} m, range {range:0.0} m)");
            }
            else FiresLogger.LogDebug($"[Possession] flyer auto-lock: {Utils.GetPrefabName(attacker.gameObject)} found no enemy within {range:0.0} m ahead");
            return best;
        }

        private static void EnsureRunner()
        {
            if (s_runner != null) return;
            var go = new GameObject("FiresFlyerAutoLock");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            s_runner = go.AddComponent<Runner>();
        }

        // During the swing: face the target and ease the body's height toward its centre (capped), then let go.
        private sealed class Runner : MonoBehaviour
        {
            private void FixedUpdate()
            {
                if (s_target == null || s_attacker == null) return;
                if (Time.time > s_until || s_target.IsDead() || s_attacker.IsDead() || FiresPossession.PossessionEngine.Creature != s_attacker)
                {
                    if (s_attacker != null && s_target != null)
                        FiresLogger.LogDebug($"[Possession] flyer auto-lock released: {Utils.GetPrefabName(s_target.gameObject)} health {s_target.GetHealth():0.0}/{s_target.GetMaxHealth():0}");
                    s_target = null; s_attacker = null;
                    return;
                }
                Vector3 to = s_target.GetCenterPoint() - s_attacker.GetCenterPoint();
                Vector3 flat = new Vector3(to.x, 0f, to.z);
                if (flat.sqrMagnitude > 1e-4f)
                {
                    float want = Quaternion.LookRotation(flat).eulerAngles.y;
                    FiresPossession.PossessionEngine.LookYaw = Mathf.MoveTowardsAngle(FiresPossession.PossessionEngine.LookYaw, want, TurnDegreesPerSecond * Time.fixedDeltaTime);
                }
                var rb = s_attacker.m_body;
                if (rb != null && Mathf.Abs(to.y) > 0.1f)
                    rb.MovePosition(rb.position + Vector3.up * Mathf.Clamp(to.y, -HeightSpeed * Time.fixedDeltaTime, HeightSpeed * Time.fixedDeltaTime));
            }
        }
    }
}
