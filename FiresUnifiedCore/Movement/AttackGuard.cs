using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Movement
{
    /// <summary>
    /// 0.2.849 (Fire 10-26: stuck in a pickaxe swing, nothing worked, had to quit). Vanilla's InAttack() reads the animator: when something
    /// swaps the pose or the controller mid-swing (a forced crouch / crawl, a custom clip) the animator never returns from the attack state's
    /// end and the player stays "attacking" for good. Headroom / Crawl / ClipSlots no longer swap mid-swing; this is the safety net for
    /// anything else: on the local player, InAttack() true for over StuckSeconds with no swing in progress (no current attack, or one that
    /// is done) clears vanilla's current attack and returns the animator to Movement. A bow being drawn is never touched.
    /// Debug: "[AttackGuard] cleared stuck attack after N s (...)".
    /// </summary>
    [HarmonyPatch]
    internal static class AttackGuard
    {
        private const float StuckSeconds = 5f;
        private static readonly int MovementHash = Animator.StringToHash("Movement");
        private static readonly FieldInfo s_currentAttack = AccessTools.Field(typeof(Humanoid), "m_currentAttack");
        private static readonly FieldInfo s_attackDone = AccessTools.Field(typeof(Attack), "m_attackDone");
        private static readonly MethodInfo s_attackStop = AccessTools.Method(typeof(Attack), "Stop");
        private static float s_since = -1f;

        [HarmonyPatch(typeof(Player), "Update"), HarmonyPostfix]
        private static void Player_Update(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            try { Tick(__instance); }
            catch (Exception ex) { FiresCore.Logging.FiresLogger.LogWarning($"[AttackGuard] check failed: {ex.Message}"); s_since = -1f; }
        }

        private static void Tick(Player player)
        {
            if (!player.InAttack() || player.IsDead() || player.IsDrawingBow()) { s_since = -1f; return; }
            if (s_since < 0f) { s_since = Time.time; return; }
            float stuck = Time.time - s_since;
            if (stuck < StuckSeconds) return;

            Attack current = s_currentAttack != null ? s_currentAttack.GetValue(player) as Attack : null;
            bool done = current == null || (s_attackDone != null && (bool)s_attackDone.GetValue(current));
            if (!done) return;   // a swing really is running (vanilla ends it); only a finished or missing one is stuck

            string what = current == null ? "no current attack" : $"attack '{current.m_attackAnimation}' done";
            if (current != null)
            {
                try { s_attackStop?.Invoke(current, null); } catch { }
                s_currentAttack?.SetValue(player, null);
            }
            Animator animator = player.m_animator;
            string tag = "?";
            if (animator != null)
            {
                tag = animator.GetCurrentAnimatorStateInfo(0).IsTag("attack") ? "attack" : "other";
                if (animator.HasState(0, MovementHash)) animator.CrossFadeInFixedTime(MovementHash, 0.1f, 0);
            }
            FiresCore.Logging.FiresLogger.LogDebug($"[AttackGuard] cleared stuck attack after {stuck:0.0} s ({what}; animator tag {tag}; crouching {player.IsCrouching()})");
            s_since = -1f;
        }
    }
}
