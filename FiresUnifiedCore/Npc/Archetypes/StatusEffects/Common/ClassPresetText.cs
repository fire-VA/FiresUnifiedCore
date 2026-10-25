using System.Collections.Generic;

namespace FiresCore.Npc.Archetypes.StatusEffects
{
    /// <summary>
    /// A proper name + tooltip for every custom status effect: the readable
    /// name and tooltip of the class presets and the effects whose names only split at capitals ("Class Slow30", "Assassins
    /// Shot", "Divine Protection W C", tooltip "Companion ability effect."). CompanionStatusEffectBase's default DisplayName /
    /// Description read this table first; effects that override DisplayName / Description keep their own. Numbers are the ones
    /// in ClassPresetEffects.cs. Add-only: a missing name keeps the old split-at-capitals text.
    /// </summary>
    public static class ClassPresetText
    {
        private static readonly Dictionary<string, (string name, string tip)> Table = new Dictionary<string, (string, string)>
        {
            // crowd control
            { "ClassStun", ("Stunned", "Can't move, attack or cast.") },
            { "ClassKnockdown", ("Knocked Down", "Knocked off your feet: can't act until you get up.") },
            { "ClassFlee", ("Feared", "Fleeing in terror; no control over where you run.") },
            { "ClassLoseTarget", ("Lost Track", "Enemies lose sight of the target for a moment.") },
            { "ClassStaggerImmune", ("Unshakable", "Can't be staggered.") },
            { "ClassSilence", ("Silenced", "No magic or skills.") },
            { "ClassInterrupt", ("Interrupted", "The cast was broken.") },
            { "ClassSlow20", ("Slowed (20%)", "Moves 20% slower.") },
            { "ClassSlow30", ("Slowed (30%)", "Moves 30% slower.") },
            { "ClassSlow60", ("Slowed (60%)", "Moves 60% slower.") },
            { "ClassWithered", ("Withered", "Takes 15% more damage from every source and moves 20% slower.") },
            { "ClassWounded", ("Wounded", "Receives 50% less healing from every source.") },
            // buffs
            { "ClassHaste15", ("Haste (15%)", "Moves 15% faster.") },
            { "ClassHaste20", ("Haste (20%)", "Moves 20% faster.") },
            { "ClassHaste25", ("Haste (25%)", "Moves 25% faster.") },
            { "ClassTrance10", ("Battle Trance (10%)", "Battle Trance: in the zone, fighting harder (rank 1).") },
            { "ClassTrance15", ("Battle Trance (15%)", "Battle Trance: in the zone, fighting harder (rank 2).") },
            { "ClassTrance20", ("Battle Trance (20%)", "Battle Trance: in the zone, fighting harder (rank 3).") },
            { "ClassShieldWall40", ("Shield Wall (40%)", "Takes 40% less damage from the front of the caster's shield wall.") },
            { "ClassShieldWall50", ("Shield Wall (50%)", "Takes 50% less damage from the front of the caster's shield wall.") },
            { "ClassShieldWall60", ("Shield Wall (60%)", "Takes 60% less damage from the front of the caster's shield wall.") },
            { "ClassOath", ("Guardian's Oath", "A guardian takes 30% of every hit on you.") },
            { "ClassLifeline20", ("Lifeline (20%)", "Healed once for 20% when health drops below 30%.") },
            { "ClassLifeline30", ("Lifeline (30%)", "Healed once for 30% when health drops below 30%.") },
            { "ClassLifeline40", ("Lifeline (40%)", "Healed once for 40% when health drops below 30%.") },
            { "ClassSpiritLink15", ("Spirit Link (15%)", "Damage is shared across the linked group, which regenerates together.") },
            { "ClassSpiritLink20", ("Spirit Link (20%)", "Damage is shared across the linked group, which regenerates together.") },
            { "ClassSpiritLink25", ("Spirit Link (25%)", "Damage is shared across the linked group, which regenerates together.") },
            { "ClassAegis", ("Aegis", "A shield absorbs incoming damage.") },
            { "ClassWard10", ("Ward (10%)", "Takes 10% less damage.") },
            { "ClassWard15", ("Ward (15%)", "Takes 15% less damage.") },
            { "ClassWard20", ("Ward (20%)", "Takes 20% less damage.") },
            { "ClassWard25", ("Ward (25%)", "Takes 25% less damage.") },
            { "ClassBanner10", ("Rallying Banner (10%)", "Takes 10% less damage and regains stamina 25% faster.") },
            { "ClassBanner15", ("Rallying Banner (15%)", "Takes 15% less damage and regains stamina 40% faster.") },
            { "ClassBanner20", ("Rallying Banner (20%)", "Takes 20% less damage and regains stamina 50% faster.") },
            { "ClassEitrWell100", ("Eitr Well (+100%)", "Regenerates eitr 100% faster.") },
            { "ClassEitrWell150", ("Eitr Well (+150%)", "Regenerates eitr 150% faster.") },
            { "ClassEitrWell200", ("Eitr Well (+200%)", "Regenerates eitr 200% faster.") },
            { "ClassDamage25", ("Horn of Valhalla", "Deals 25% more damage.") },
            // names that split badly
            { "CompanionHuntersMark", ("Hunter's Mark", null) },
            { "CompanionAssassinsShot", ("Assassin's Shot", null) },
            { "CompanionGuardiansVolley", ("Guardian's Volley", null) },
            { "CompanionPredatorsMark", ("Predator's Mark", null) },
            { "CompanionNaturesBlessing", ("Nature's Blessing", null) },
            { "CompanionDivineProtectionWC", ("Divine Protection", null) },
            { "CompanionLayOnHands", ("Lay on Hands", null) },
            { "CompanionRainOfArrows", ("Rain of Arrows", null) },
            { "CompanionRainOfFury", ("Rain of Fury", null) },
            { "CompanionFlurryOfBlows", ("Flurry of Blows", null) },
            { "CompanionAvatarOfLight", ("Avatar of Light", null) },
            { "CompanionAvatarOfWar", ("Avatar of War", null) },
            { "CompanionAvatarOfLife", ("Avatar of Life", null) },
            { "CompanionWayOfPerfection", ("Way of Perfection", null) },
            { "CompanionDanceOfDeath", ("Dance of Death", null) },
        };

        /// <summary>The readable name, or null to keep the default.</summary>
        public static string Name(string effectName) => effectName != null && Table.TryGetValue(effectName, out var t) ? t.name : null;

        /// <summary>The tooltip, or null to keep the default.</summary>
        public static string Tip(string effectName) => effectName != null && Table.TryGetValue(effectName, out var t) ? t.tip : null;
    }
}
