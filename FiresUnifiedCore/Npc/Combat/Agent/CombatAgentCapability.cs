using System;

namespace FiresCore.Npc.Combat.Agent
{
    /// <summary>
    /// What a body can physically do. A behaviour runs only when the capability is present, so a creature
    /// with no dodge animation sidesteps instead of rolling.
    /// </summary>
    [Flags]
    public enum CombatAgentCapability
    {
        None = 0,
        Melee = 1 << 0,
        Ranged = 1 << 1,
        Block = 1 << 2,
        Parry = 1 << 3,
        DodgeRoll = 1 << 4,
        Sidestep = 1 << 5,
        Cast = 1 << 6,
        Formation = 1 << 7,
        Consumables = 1 << 8,
        Crouch = 1 << 9
    }
}
