namespace FiresCore.Npc.Combat.Agent
{
    /// <summary>ICombatSkills over a companion's CompanionSkills. Every call forwards unchanged.</summary>
    public class CompanionSkillsAdapter : ICombatSkills
    {
        private readonly CompanionSkills _skills;

        public CompanionSkillsAdapter(CompanionSkills skills)
        {
            _skills = skills;
        }

        public bool Wraps(CompanionSkills skills) => ReferenceEquals(_skills, skills);

        public float GetSkillFactor(Skills.SkillType skillType) => _skills.GetSkillFactor(skillType);

        public float GetSkillLevel(Skills.SkillType skillType) => _skills.GetSkillLevel(skillType);

        public void RaiseSkill(Skills.SkillType skillType, float amount) => _skills.RaiseSkill(skillType, amount);
    }
}
