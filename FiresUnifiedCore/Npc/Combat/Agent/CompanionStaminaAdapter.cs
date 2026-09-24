namespace FiresCore.Npc.Combat.Agent
{
    /// <summary>ICombatStamina over a companion's CompanionStats. Every call forwards unchanged.</summary>
    public class CompanionStaminaAdapter : ICombatStamina
    {
        private readonly CompanionStats _stats;

        public CompanionStaminaAdapter(CompanionStats stats)
        {
            _stats = stats;
        }

        public bool Wraps(CompanionStats stats) => ReferenceEquals(_stats, stats);

        public float CurrentStamina => _stats.CurrentStamina;

        public bool HasStamina(float amount) => _stats.HasStamina(amount);

        public bool UseStamina(float amount) => _stats.UseStamina(amount);

        public float ModifyBlockStaminaCost(float cost) => _stats.ModifyBlockStaminaCost(cost);

        public float ModifyDodgeStaminaCost(float cost) => _stats.ModifyDodgeStaminaCost(cost);

        public float GetAttackStaminaCost(float baseCost, Skills.SkillType skillType) =>
            _stats.GetAttackStaminaCost(baseCost, skillType);
    }
}
