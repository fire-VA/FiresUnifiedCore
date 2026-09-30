using FiresCore.Npc.Archetypes.StatusEffects;

namespace FiresCore.Npc.Archetypes
{
    public enum CrowdControlResult
    {
        /// <summary>The effect is on its way to the target's owner.</summary>
        Applied,

        /// <summary>A boss: slowed instead (Fire's rule).</summary>
        Slowed,

        /// <summary>Not allowed (ClassTargeting: party, or a player without PvP on both sides), or nothing to apply to.</summary>
        Refused,
    }

    /// <summary>
    /// Crowd control for class skills (Fire, popup 2026-09-28 21:3x: "build them simply"). Every call goes through
    /// AbilityRPCManager.ApplySingleEffect, so ClassTargeting decides who may be hit and the effect lands on the target's
    /// owner, holding on every peer. Bosses (vanilla Character.IsBoss) are slowed instead of stunned, knocked down or sent
    /// fleeing. The effects are in StatusEffects\Common\CrowdControlEffects.cs.
    /// </summary>
    public static class ClassCrowdControl
    {
        /// <summary>A boss's slow in place of a stun, knockdown or flee, for the same time (at least this long).</summary>
        private const float BossSlowMinimumSeconds = 3f;

        public static CrowdControlResult Stun(Character target, float seconds, Character caster) =>
            Debuff(target, caster, StatusEffectManager.EFFECT_CLASS_STUN, seconds);

        /// <summary>
        /// <see cref="Stun(Character, float, Character)"/> with the slow a boss gets instead: a registered slow effect, e.g.
        /// <see cref="StatusEffectManager.EFFECT_CLASS_SLOW60"/> for mage_absolute_zero (the default is CompanionSlowed, 50%).
        /// </summary>
        public static CrowdControlResult Stun(Character target, float seconds, Character caster, string bossSlowEffect) =>
            Debuff(target, caster, StatusEffectManager.EFFECT_CLASS_STUN, seconds, bossSlowEffect);

        /// <summary>A stagger and vanilla's medium push away from the caster (KnockdownEffect.DefaultForce).</summary>
        public static CrowdControlResult Knockdown(Character target, Character caster) =>
            Debuff(target, caster, StatusEffectManager.EFFECT_CLASS_KNOCKDOWN, 1f);

        public static CrowdControlResult Flee(Character target, float seconds, Character caster) =>
            Debuff(target, caster, StatusEffectManager.EFFECT_CLASS_FLEE, seconds);

        /// <summary>The target forgets what it was after. Not a hard control, so bosses are not exempt.</summary>
        public static CrowdControlResult LoseTarget(Character target, Character caster)
        {
            if (target == null || caster == null) return CrowdControlResult.Refused;
            return AbilityRPCManager.ApplySingleEffect(caster, target, StatusEffectManager.EFFECT_CLASS_LOSE_TARGET, 0.5f)
                ? CrowdControlResult.Applied : CrowdControlResult.Refused;
        }

        /// <summary>Hits neither stagger nor push the target (a buff: the caster's party only).</summary>
        public static CrowdControlResult SetStaggerImmune(Character target, float seconds, Character caster)
        {
            if (target == null || caster == null) return CrowdControlResult.Refused;
            return AbilityRPCManager.ApplySingleEffect(caster, target, StatusEffectManager.EFFECT_CLASS_STAGGER_IMMUNE, seconds)
                ? CrowdControlResult.Applied : CrowdControlResult.Refused;
        }

        private static CrowdControlResult Debuff(Character target, Character caster, string effect, float seconds, string bossSlowEffect = null)
        {
            if (target == null || caster == null) return CrowdControlResult.Refused;
            if (target.IsBoss())
            {
                float slow = seconds > BossSlowMinimumSeconds ? seconds : BossSlowMinimumSeconds;
                return AbilityRPCManager.ApplySingleEffect(caster, target, bossSlowEffect ?? StatusEffectManager.EFFECT_SLOWDOWN, slow)
                    ? CrowdControlResult.Slowed : CrowdControlResult.Refused;
            }
            return AbilityRPCManager.ApplySingleEffect(caster, target, effect, seconds) ? CrowdControlResult.Applied : CrowdControlResult.Refused;
        }
    }
}
