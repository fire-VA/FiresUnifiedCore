namespace FiresCore.Npc.Combat.Agent
{
    /// <summary>
    /// A body's stamina, for behaviours that pay for blocks and shots. Null on a body with no stamina system,
    /// which is how a vanilla creature reads - the behaviour then spends nothing, as it does today.
    /// </summary>
    public interface ICombatStamina
    {
        float CurrentStamina { get; }
        bool HasStamina(float amount);
        bool UseStamina(float amount);
        float ModifyBlockStaminaCost(float cost);
        float ModifyDodgeStaminaCost(float cost);
        float GetAttackStaminaCost(float baseCost, Skills.SkillType skillType);
    }

    /// <summary>
    /// A body's weapon skills. Null on a body that keeps none, which is how a vanilla creature reads.
    /// </summary>
    public interface ICombatSkills
    {
        float GetSkillFactor(Skills.SkillType skillType);
        float GetSkillLevel(Skills.SkillType skillType);
        void RaiseSkill(Skills.SkillType skillType, float amount);
    }

    /// <summary>
    /// Which band a behaviour asks for facing in. Maps onto the movement-authority ladder, so an attack or
    /// dodge animation outranks a behaviour's general enemy-facing.
    /// </summary>
    public enum CombatFacingPriority
    {
        Behavior,
        Animation
    }
}
