using FiresCore.Bridge;

namespace FiresCore.Npc.Archetypes
{
    /// <summary>
    /// Who a class ability may touch (Fire, 2026-09-28): heals and buffs only the caster's party, damage and debuffs
    /// only enemies. The party is the caster, their companions, and every player allied with them by group or guild
    /// (NpcCompanionBridge.AreOwnersAllied, the rule companion allegiance already uses) with those players' companions.
    /// An enemy is a hostile creature, or another player when PvP is on for both. Every class effect asks here.
    /// </summary>
    public static class ClassTargeting
    {
        // CompanionController.LoadFromZDO's key: every peer reads the same owner from the ZDO, not from local state.
        private const string CompanionOwnerKey = "companion_owner";

        /// <summary>The player a character answers to: itself for a player, its owner for a companion, else 0.</summary>
        public static long PartyOwner(Character character)
        {
            if (character == null) return 0L;
            if (character is Player player) return player.GetPlayerID();
            CompanionController companion = character.GetComponent<CompanionController>();
            if (companion == null) return 0L;
            ZDO zdo = character.GetComponent<ZNetView>()?.GetZDO();
            long owner = zdo != null ? zdo.GetLong(CompanionOwnerKey, 0L) : 0L;
            return owner != 0L ? owner : companion.ownerPlayerId;
        }

        /// <summary>Same party as the caster (the caster is its own party). Heals and buffs only land here.</summary>
        public static bool IsPartyMember(Character caster, Character target)
        {
            if (caster == null || target == null) return false;
            if (caster == target) return true;
            long casterOwner = PartyOwner(caster);
            long targetOwner = PartyOwner(target);
            return casterOwner != 0L && targetOwner != 0L && NpcCompanionBridge.AreOwnersAllied(casterOwner, targetOwner);
        }

        /// <summary>
        /// A target damage and debuffs may land on: never the party; a player only when both sides have PvP on (vanilla
        /// needs both too: Attack checks the attacker's IsPVPEnabled, Character.RPC_Damage the target's); anything else
        /// when vanilla calls it an enemy of the caster (BaseAI.IsEnemy, which keeps tames on the players' side).
        /// </summary>
        public static bool IsEnemyTarget(Character caster, Character target)
        {
            if (caster == null || target == null || caster == target || target.IsDead()) return false;
            if (IsPartyMember(caster, target)) return false;
            if (target.IsPlayer())
            {
                Player attacker = caster as Player ?? OwnerPlayer(caster);
                return attacker != null && attacker.IsPVPEnabled() && target.IsPVPEnabled();
            }
            return BaseAI.IsEnemy(caster, target);
        }

        // A companion fighting a player follows its owner's PvP switch.
        private static Player OwnerPlayer(Character character)
        {
            long owner = PartyOwner(character);
            return owner != 0L ? Player.GetPlayer(owner) : null;
        }

        /// <summary>
        /// A Fires companion and another party's player or companion are enemies when both owners have PvP on and aren't
        /// allied. Vanilla never calls a tame an enemy of a player or of another tame, and its hit checks (Attack's melee and
        /// area hits, Projectile.IsValidTarget) drop every non-player hit on a non-enemy, so the PvP targets the companion AI
        /// picks (IsHostilePvpTarget) were never hit (R44 companion_test: cross hits 0/0).
        /// </summary>
        public static bool IsCompanionPvpEnemy(Character a, Character b)
        {
            bool aCompanion = CompanionController.TryGet(a, out _);
            bool bCompanion = CompanionController.TryGet(b, out _);
            if (!aCompanion && !bCompanion) return false;
            if ((!aCompanion && !a.IsPlayer()) || (!bCompanion && !b.IsPlayer())) return false;
            long ownerA = PartyOwner(a), ownerB = PartyOwner(b);
            if (ownerA == 0L || ownerB == 0L || NpcCompanionBridge.AreOwnersAllied(ownerA, ownerB)) return false;
            Player playerA = Player.GetPlayer(ownerA), playerB = Player.GetPlayer(ownerB);
            return playerA != null && playerB != null && playerA.IsPVPEnabled() && playerB.IsPVPEnabled();
        }

        [HarmonyLib.HarmonyPatch(typeof(BaseAI), nameof(BaseAI.IsEnemy), new[] { typeof(Character), typeof(Character) })]
        private static class CompanionPvpEnemies
        {
            private static void Postfix(Character a, Character b, ref bool __result)
            {
                // Creatures and companions don't see a stealthed character (Fire, 2026-09-28). First, before the early
                // return; a set lookup, because this runs for every AI against every candidate ([generator]).
                if (a != null && !a.IsPlayer() && b != null && StatusEffects.Rogue.StealthSync.IsStealthed(b))
                {
                    __result = false;
                    return;
                }
                if (__result || a == null || b == null || a == b) return;
                if (!a.IsTamed() && !b.IsTamed()) return;
                __result = IsCompanionPvpEnemy(a, b);
            }
        }
    }
}
