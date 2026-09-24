using System.Collections.Generic;
using UnityEngine;
using FiresCore.Npc.Archetypes;

namespace FiresCore.Classes
{
    /// <summary>
    /// A character's stored progression: one level then Valor, what has been spent, and which talent
    /// ranks and passives they own. Earned points are never stored - they are computed from this plus
    /// the server's rates. Plan: Docs/PLAN_ClassesFoundation.md
    /// </summary>
    public class ClassProgressState
    {
        public const int CurrentSchema = 3;

        public int Schema = CurrentSchema;

        public int Level = ProgressionEngine.FirstLevel;
        public long Xp;
        public int ValorLevel;
        public long ValorXp;

        /// <summary>Talent ranks owned, per archetype: node id -> rank.</summary>
        public readonly Dictionary<ArchetypeClass, Dictionary<string, int>> Talents =
            new Dictionary<ArchetypeClass, Dictionary<string, int>>();

        /// <summary>Equipped always-on passives, by node id. Bounded by the current passive slot count.</summary>
        public readonly List<string> PassiveLoadout = new List<string>();

        public readonly Dictionary<AttributeKind, int> Attributes = new Dictionary<AttributeKind, int>();

        /// <summary>Total skill points spent across every tree.</summary>
        public int SpentSkillPoints
        {
            get
            {
                int spent = 0;
                foreach (var tree in Talents.Values)
                    foreach (int rank in tree.Values)
                        spent += rank;
                return spent;
            }
        }

        public int SpentAttributePoints
        {
            get
            {
                int spent = 0;
                foreach (int points in Attributes.Values)
                    spent += points;
                return spent;
            }
        }

        public int EarnedSkillPoints(ProgressionRates rates) =>
            ProgressionEngine.EarnedSkillPoints(Level, ValorLevel, rates);

        public int UnspentSkillPoints(ProgressionRates rates) =>
            ProgressionEngine.UnspentSkillPoints(Level, ValorLevel, SpentSkillPoints, rates);

        public int UnspentAttributePoints(ProgressionRates rates) =>
            ProgressionEngine.UnspentAttributePoints(Level, ValorLevel, SpentAttributePoints, rates);

        public int PassiveSlots => ProgressionEngine.PassiveSlots(Level, ValorLevel);

        public ProgressionGain AddXp(long amount, ProgressionRates rates) =>
            ProgressionEngine.AddXp(ref Level, ref Xp, ref ValorLevel, ref ValorXp, amount, rates);

        /// <summary>
        /// Applies the server's policy for a character who has spent more than the current rates would
        /// earn. Keep leaves everything learned and simply blocks further spending until they catch up,
        /// so only Refund changes stored data. Returns the points refunded.
        /// </summary>
        public int ReconcileOverspend(ProgressionRates rates)
        {
            if (!ProgressionEngine.IsOverspent(Level, ValorLevel, SpentSkillPoints, rates)) return 0;
            if (rates.WhenPointsDrop != OverspentPointPolicy.Refund) return 0;

            int refunded = SpentSkillPoints;
            Talents.Clear();
            PassiveLoadout.Clear();
            return refunded;
        }

        /// <summary>Refunds one archetype's tree, and drops any passive that came from it.</summary>
        public int RespecArchetype(ArchetypeClass archetype, IEnumerable<string> passiveNodeIdsInTree)
        {
            if (!Talents.TryGetValue(archetype, out var tree)) return 0;

            int refunded = 0;
            foreach (int rank in tree.Values) refunded += rank;
            Talents.Remove(archetype);

            foreach (string nodeId in passiveNodeIdsInTree)
                PassiveLoadout.Remove(nodeId);

            return refunded;
        }

        public int TalentRank(ArchetypeClass archetype, string nodeId) =>
            Talents.TryGetValue(archetype, out var tree) && tree.TryGetValue(nodeId, out int rank) ? rank : 0;

        /// <summary>
        /// Buys one rank of a node. Returns false, changing nothing, when there are no points left or
        /// the node is already at its maximum rank.
        /// </summary>
        public bool TryBuyTalentRank(ArchetypeClass archetype, string nodeId, int maxRank, ProgressionRates rates)
        {
            if (maxRank < 1) return false;
            if (TalentRank(archetype, nodeId) >= maxRank) return false;
            if (UnspentSkillPoints(rates) < 1) return false;

            if (!Talents.TryGetValue(archetype, out var tree))
            {
                tree = new Dictionary<string, int>();
                Talents[archetype] = tree;
            }

            tree.TryGetValue(nodeId, out int rank);
            tree[nodeId] = rank + 1;
            return true;
        }

        /// <summary>Equips a learned passive if a slot is free. Returns false when the grid is full.</summary>
        public bool TryEquipPassive(string nodeId)
        {
            if (PassiveLoadout.Contains(nodeId)) return false;
            if (PassiveLoadout.Count >= PassiveSlots) return false;

            PassiveLoadout.Add(nodeId);
            return true;
        }

        public bool UnequipPassive(string nodeId) => PassiveLoadout.Remove(nodeId);

        /// <summary>
        /// Drops passives beyond the current slot count, keeping the earliest equipped. Needed when a
        /// slot-count setting is lowered.
        /// </summary>
        public int TrimPassivesToSlots()
        {
            int slots = PassiveSlots;
            int removed = 0;
            while (PassiveLoadout.Count > slots)
            {
                PassiveLoadout.RemoveAt(PassiveLoadout.Count - 1);
                removed++;
            }
            return removed;
        }

        public int Attribute(AttributeKind kind) => Attributes.TryGetValue(kind, out int points) ? points : 0;

        public bool TryBuyAttributePoint(AttributeKind kind, ProgressionRates rates)
        {
            if (UnspentAttributePoints(rates) < 1) return false;

            Attributes.TryGetValue(kind, out int points);
            Attributes[kind] = points + 1;
            return true;
        }

        /// <summary>
        /// Migrates a schema 2 character, whose levels were per archetype: the character level becomes
        /// the highest archetype level, and every talent point is refunded to re-spend.
        /// </summary>
        public static ClassProgressState FromSchema2(int highestArchetypeLevel, long carriedXp)
        {
            return new ClassProgressState
            {
                Schema = CurrentSchema,
                Level = Mathf.Clamp(highestArchetypeLevel, ProgressionEngine.FirstLevel, ProgressionEngine.MaxCharacterLevel),
                Xp = carriedXp < 0 ? 0 : carriedXp,
            };
        }
    }

    public enum AttributeKind
    {
        Strength,
        Agility,
        Intellect,
        Endurance,
        Vigour
    }
}
