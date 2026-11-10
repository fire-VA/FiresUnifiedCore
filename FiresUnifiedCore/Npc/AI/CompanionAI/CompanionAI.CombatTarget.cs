namespace FiresCore.Npc.AI
{
    // 0.2.92x ([ghost]): read-only view of the current fight target for the combat-move chooser (Npc\Combat\CompanionCombatMoves).
    public partial class CompanionAI
    {
        internal Character CombatTarget => _targetCreature;
    }
}
