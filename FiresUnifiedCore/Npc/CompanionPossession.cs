namespace FiresCore.Npc
{
    /// <summary>
    /// A Core NPC body a player is DRIVING. FiresValcast's creature possession
    /// writes ZDO long "valcast_possessed" = the possessor's playerID on the creature while a player controls it, and 0 when they
    /// let go (FGN 1.5.37+ keeps the possessor as the ZDO owner meanwhile). While it's set, Core's own brain stands down for that
    /// body: no leash reel-in or follow teleport, no ownership claim-back, no per-frame movement, facing, combat, evasion or
    /// personal-space push, no speed ramp, no exit / taming-guard steering, and the AI isn't switched back on by a revive. It all
    /// resumes at 0. Valcast reads this type's presence to know a Core NPC body can be possessed (no reference either way).
    /// </summary>
    public static class CompanionPossession
    {
        public const string PossessedKey = "valcast_possessed";
        private static readonly int s_possessedHash = PossessedKey.GetStableHashCode();

        public static bool IsPossessed(ZDO zdo) => zdo != null && zdo.GetLong(s_possessedHash, 0L) != 0L;

        public static bool IsPossessed(ZNetView nview) => nview != null && nview.IsValid() && IsPossessed(nview.GetZDO());
    }
}
