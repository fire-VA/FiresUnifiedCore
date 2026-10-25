using System;
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
        // 4: talent ranks cost 1/2/3 points (RankCosts), no longer 1 each.
        // 5: Paragon - ParagonRanks added; talent ranks past 3 are paid in Paragon points, not skill points.
        public const int CurrentSchema = 5;

        /// <summary>Talent ranks from this one up are paid in Paragon points (1 each), not skill points.</summary>
        public const int ParagonTalentFromRank = 4;

        public int Schema = CurrentSchema;

        /// <summary>
        /// What each rank of a talent costs, rank 1 first (the Talents board's 1/2/3). Ranks past the table cost its
        /// last entry. Core owns the table; FiresRPGClasses' board reads it.
        /// </summary>
        public static readonly int[] RankCosts = { 1, 2, 3 };

        /// <summary>The points buying <paramref name="rank"/> (1-based) costs.</summary>
        public static int RankCost(int rank)
        {
            if (rank < 1) return 0;
            return RankCosts[Mathf.Min(rank, RankCosts.Length) - 1];
        }

        /// <summary>The points ranks 1..<paramref name="rank"/> cost together.</summary>
        public static int CumulativeRankCost(int rank)
        {
            int total = 0;
            for (int r = 1; r <= rank; r++) total += RankCost(r);
            return total;
        }

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
                        spent += CumulativeRankCost(Mathf.Min(rank, ParagonTalentFromRank - 1));   // Ranks 4+ are Paragon
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

        // Lore books can grant attribute points. A plain stored count, added on top of the earned points. No schema
        // bump: FiresRPGClasses' Newtonsoft store reads a missing field as 0 and an older loader ignores the extra one.
        public int BonusAttributePoints;

        /// <summary>Most bonus attribute points one record may hold (a guard against a runaway grant).</summary>
        public const int MaxBonusAttributePoints = 100;

        public int UnspentAttributePoints(ProgressionRates rates) =>
            Mathf.Max(0, ProgressionEngine.EarnedAttributePoints(Level, ValorLevel, rates) + Mathf.Clamp(BonusAttributePoints, 0, MaxBonusAttributePoints) - SpentAttributePoints);

        /// <summary>Adds <paramref name="n"/> bonus attribute points (lore books). Returns the points actually added (0 when n &lt;= 0 or at the cap).</summary>
        public int AddBonusAttributePoints(int n)
        {
            if (n <= 0) return 0;
            int before = Mathf.Clamp(BonusAttributePoints, 0, MaxBonusAttributePoints);
            BonusAttributePoints = Mathf.Min(MaxBonusAttributePoints, before + n);
            int added = BonusAttributePoints - before;
            FiresCore.Logging.FiresLogger.LogDebug($"[Classes] bonus attribute points +{added} (asked {n}), now {BonusAttributePoints}");
            return added;
        }

        // ── Paragon (ESO Champion Points style) ──────────────────────────────────────────────────────────
        // Earned points are COMPUTED from ValorLevel (like skill points); only the spend is stored. Ranks have no maximum; what a rank
        // gives is FiresRPGClasses' (passive definitions on the shared SoftCap curve). Respec works like talents.

        /// <summary>Paragon passive ranks bought: passive id -> rank (no maximum).</summary>
        public readonly Dictionary<string, int> ParagonRanks = new Dictionary<string, int>();

        public int EarnedParagonPoints() => ProgressionEngine.EarnedParagonPoints(ValorLevel);

        /// <summary>Paragon points spent: every Paragon passive rank (1 each) plus every talent rank from <see cref="ParagonTalentFromRank"/> up.</summary>
        public int SpentParagonPoints
        {
            get
            {
                int spent = 0;
                foreach (int rank in ParagonRanks.Values) spent += Mathf.Max(0, rank);
                foreach (var tree in Talents.Values)
                    foreach (int rank in tree.Values) spent += Mathf.Max(0, rank - (ParagonTalentFromRank - 1));
                return spent;
            }
        }

        public int UnspentParagonPoints() => Mathf.Max(0, EarnedParagonPoints() - SpentParagonPoints);

        public int ParagonRank(string id) => id != null && ParagonRanks.TryGetValue(id, out int r) ? r : 0;

        /// <summary>Buys one rank of a Paragon passive for 1 Paragon point. False, changing nothing, when none is left.</summary>
        public bool TryBuyParagonRank(string id)
        {
            if (string.IsNullOrEmpty(id) || UnspentParagonPoints() < 1) return false;
            ParagonRanks[id] = ParagonRank(id) + 1;
            return true;
        }

        /// <summary>As <see cref="TryBuyParagonRank(string)"/>, refusing past <paramref name="maxRank"/> (0 or less = no maximum).</summary>
        public bool TryBuyParagonRank(string id, int maxRank)
        {
            if (maxRank > 0 && ParagonRank(id) >= maxRank) return false;
            return TryBuyParagonRank(id);
        }

        /// <summary>Refunds every Paragon passive rank and empties every slot (talent ranks past 3 come back with their tree's respec). Returns the points refunded.</summary>
        public int RespecParagon()
        {
            int refunded = 0;
            foreach (int rank in ParagonRanks.Values) refunded += Mathf.Max(0, rank);
            ParagonRanks.Clear();
            ParagonSlotted.Clear();
            return refunded;
        }

        // ── Paragon slots (ESO Champion-Point style: slottable stars per tree). Core stores and validates; the combat lock and what a
        // slotted star does are FiresRPGClasses'. ──────────────────────────────────────────────────────────────────────────────────────

        /// <summary>Slots a Paragon tree holds.</summary>
        public const int ParagonSlotsPerTree = 4;

        /// <summary>Slotted stars per tree id: always <see cref="ParagonSlotsPerTree"/> entries, null = an empty slot.</summary>
        public readonly Dictionary<string, List<string>> ParagonSlotted = new Dictionary<string, List<string>>();

        private List<string> SlotsOf(string tree)
        {
            if (!ParagonSlotted.TryGetValue(tree, out var slots) || slots == null) ParagonSlotted[tree] = slots = new List<string>();
            while (slots.Count < ParagonSlotsPerTree) slots.Add(null);
            return slots;
        }

        /// <summary>The star in <paramref name="tree"/>'s slot <paramref name="slot"/> (0-based), or null.</summary>
        public string ParagonSlot(string tree, int slot) =>
            tree != null && slot >= 0 && slot < ParagonSlotsPerTree && ParagonSlotted.TryGetValue(tree, out var s) && s != null && slot < s.Count ? s[slot] : null;

        /// <summary>Puts an owned star (rank 1+) in a slot of its tree; it leaves any other slot of that tree. False, changing nothing, otherwise.</summary>
        public bool TrySlotParagon(string tree, int slot, string starId)
        {
            if (string.IsNullOrEmpty(tree) || string.IsNullOrEmpty(starId) || slot < 0 || slot >= ParagonSlotsPerTree || ParagonRank(starId) < 1) return false;
            var slots = SlotsOf(tree);
            for (int i = 0; i < slots.Count; i++) if (slots[i] == starId) slots[i] = null;
            slots[slot] = starId;
            return true;
        }

        /// <summary>Empties a slot. False when it was already empty.</summary>
        public bool UnslotParagon(string tree, int slot)
        {
            if (string.IsNullOrEmpty(tree) || slot < 0 || slot >= ParagonSlotsPerTree) return false;
            var slots = SlotsOf(tree);
            if (slots[slot] == null) return false;
            slots[slot] = null;
            return true;
        }

        /// <summary>Paragon ranks spent in one tree (for tier gates); <paramref name="treeOf"/> maps a star id to its tree id.</summary>
        public int ParagonSpentInTree(string tree, Func<string, string> treeOf)
        {
            if (tree == null || treeOf == null) return 0;
            int spent = 0;
            foreach (var kv in ParagonRanks) if (kv.Value > 0 && treeOf(kv.Key) == tree) spent += kv.Value;
            return spent;
        }

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
            foreach (int rank in tree.Values) refunded += CumulativeRankCost(Mathf.Min(rank, ParagonTalentFromRank - 1));
            Talents.Remove(archetype);

            foreach (string nodeId in passiveNodeIdsInTree)
                PassiveLoadout.Remove(nodeId);

            return refunded;
        }

        public int TalentRank(ArchetypeClass archetype, string nodeId) =>
            Talents.TryGetValue(archetype, out var tree) && tree.TryGetValue(nodeId, out int rank) ? rank : 0;

        /// <summary>
        /// Buys one rank of a node for <see cref="RankCost"/> of that rank. Returns false, changing nothing, when there are not
        /// enough points left or the node is already at its maximum rank.
        /// </summary>
        public bool TryBuyTalentRank(ArchetypeClass archetype, string nodeId, int maxRank, ProgressionRates rates)
        {
            if (maxRank < 1) return false;
            int current = TalentRank(archetype, nodeId);
            if (current >= maxRank) return false;
            // Ranks past 3 are paid in Paragon points.
            if (current + 1 >= ParagonTalentFromRank) { if (UnspentParagonPoints() < 1) return false; }
            else if (UnspentSkillPoints(rates) < RankCost(current + 1)) return false;

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
        /// Brings a loaded record to <see cref="CurrentSchema"/>; call it at load, before any reconcile. A schema 3 record bought
        /// every rank for 1 point: if its ranks no longer fit the points it has earned at 1/2/3 pricing, every talent and passive is
        /// refunded once to re-spend (as the schema 2 migration did), instead of a Refund policy wiping the trees at every load.
        /// Returns the points refunded (0 when the record still fits).
        /// </summary>
        public int UpgradeSchema(ProgressionRates rates)
        {
            if (Schema >= CurrentSchema) return 0;
            int refunded = 0;
            if (Schema == 3 && ProgressionEngine.IsOverspent(Level, ValorLevel, SpentSkillPoints, rates))
            {
                refunded = SpentSkillPoints;
                Talents.Clear();
                PassiveLoadout.Clear();
            }
            Schema = CurrentSchema;
            return refunded;
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
                Level = Mathf.Clamp(highestArchetypeLevel, ProgressionEngine.FirstLevel, ProgressionEngine.MaxLevel),
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
