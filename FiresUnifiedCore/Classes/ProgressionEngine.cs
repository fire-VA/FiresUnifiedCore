using System;
using UnityEngine;

namespace FiresCore.Classes
{
    /// <summary>
    /// One character level to the cap, then Valor levels. Everything earned is COMPUTED from level,
    /// Valor and the current settings - never stored - so changing a server setting applies to every
    /// character at once. Plan: Docs/PLAN_ClassesFoundation.md
    /// </summary>
    public static class ProgressionEngine
    {
        public const int FirstLevel = 1;
        public const int MaxCharacterLevel = 100;

        /// <summary>Levels at which an extra skill point is awarded.</summary>
        public const int ExtraSkillPointLevelInterval = 10;

        private const float BaselineHoursConstant = 0.15f;
        private const float BaselineHoursCoefficient = 0.00260f;
        private const float BaselineHoursExponent = 1.5f;

        private static readonly int[] PassiveSlotCharacterLevels = { 5, 15, 30, 45, 60, 80, 100 };
        private static readonly int[] PassiveSlotValorLevels = { 15, 35, 55, 75 };

        private const int FirstPassiveSlotCount = 2;

        #region Skill points

        /// <summary>
        /// Skill points earned by a character. Levels past the first each pay
        /// SkillPointsPerLevel, with ExtraSkillPointsPerTenthLevel on every tenth level, then
        /// SkillPointsPerValorLevel for each Valor level.
        /// </summary>
        public static int EarnedSkillPoints(int level, int valorLevel, ProgressionRates rates)
        {
            int clampedLevel = Mathf.Clamp(level, FirstLevel, MaxCharacterLevel);
            int levelsGained = clampedLevel - FirstLevel;

            int points = levelsGained * rates.SkillPointsPerLevel;
            points += clampedLevel / ExtraSkillPointLevelInterval * rates.ExtraSkillPointsPerTenthLevel;
            points += Mathf.Max(0, valorLevel) * rates.SkillPointsPerValorLevel;
            return points;
        }

        /// <summary>
        /// Attribute points earned. Fractional rates accumulate, so 0.5 per level is one point every
        /// second level, and the total is capped by the server's maximum.
        /// </summary>
        public static int EarnedAttributePoints(int level, int valorLevel, ProgressionRates rates)
        {
            int clampedLevel = Mathf.Clamp(level, FirstLevel, MaxCharacterLevel);

            int fromLevels = Mathf.FloorToInt(clampedLevel * rates.AttributePointsPerLevel);
            int fromValor = Mathf.FloorToInt(Mathf.Max(0, valorLevel) * rates.AttributePointsPerValorLevel);
            return Mathf.Min(fromLevels + fromValor, rates.AttributePointsMaximum);
        }

        /// <summary>
        /// Points still available to spend. Never negative: lowering a rate below what a character has
        /// already spent leaves them at zero until they earn their way back, and never unlearns anything.
        /// </summary>
        public static int UnspentSkillPoints(int level, int valorLevel, int spent, ProgressionRates rates) =>
            Mathf.Max(0, EarnedSkillPoints(level, valorLevel, rates) - spent);

        public static int UnspentAttributePoints(int level, int valorLevel, int spent, ProgressionRates rates) =>
            Mathf.Max(0, EarnedAttributePoints(level, valorLevel, rates) - spent);

        /// <summary>True when a character has spent more than the current settings would earn them.</summary>
        public static bool IsOverspent(int level, int valorLevel, int spent, ProgressionRates rates) =>
            spent > EarnedSkillPoints(level, valorLevel, rates);

        #endregion

        #region Passive slots

        /// <summary>
        /// Always-on passive slots. Character levels open the first seven, Valor levels the rest.
        /// </summary>
        public static int PassiveSlots(int level, int valorLevel)
        {
            int slots = 0;
            foreach (int unlockLevel in PassiveSlotCharacterLevels)
                if (level >= unlockLevel) slots++;

            foreach (int unlockValor in PassiveSlotValorLevels)
                if (valorLevel >= unlockValor) slots++;

            return slots == 0 ? 0 : slots + FirstPassiveSlotCount - 1;
        }

        public static int MaxPassiveSlots => PassiveSlots(MaxCharacterLevel, PassiveSlotValorLevels[PassiveSlotValorLevels.Length - 1]);

        #endregion

        #region XP

        /// <summary>
        /// XP to go from this level to the next, from the shipped curve scaled by the settings.
        /// Above the cap every further level is a Valor level, priced as a multiple of the last one.
        /// </summary>
        public static long XpForNextLevel(int level, ProgressionRates rates)
        {
            if (level >= MaxCharacterLevel)
                return Mathf.RoundToInt(XpForLevelStep(MaxCharacterLevel, rates) * rates.ValorLevelXpMultiplier);

            return XpForLevelStep(level, rates);
        }

        /// <summary>XP for one Valor level: the cap's last step times the Valor multiplier.</summary>
        public static long XpForValorLevel(ProgressionRates rates) =>
            Mathf.RoundToInt(XpForLevelStep(MaxCharacterLevel, rates) * rates.ValorLevelXpMultiplier);

        /// <summary>
        /// The shipped first-pass curve: the hours a level is meant to take, times the XP per hour
        /// expected at that level. Refit both sides from a playtest log before release.
        /// </summary>
        private static long XpForLevelStep(int level, ProgressionRates rates)
        {
            int nextLevel = Mathf.Clamp(level + 1, FirstLevel + 1, MaxCharacterLevel);
            return (long)Math.Round(BaselineHoursForLevel(nextLevel) * rates.ExpectedXpPerHour);
        }

        /// <summary>Hours one level is meant to take at the baseline pace, from doc 19's curve.</summary>
        public static float BaselineHoursForLevel(int level) =>
            BaselineHoursConstant + BaselineHoursCoefficient * Mathf.Pow(level, BaselineHoursExponent);

        /// <summary>Hours to reach a level from the start, at the baseline pace.</summary>
        public static float BaselineHoursToLevel(int level)
        {
            float hours = 0f;
            for (int step = FirstLevel + 1; step <= Mathf.Min(level, MaxCharacterLevel); step++)
                hours += BaselineHoursForLevel(step);
            return hours;
        }

        /// <summary>
        /// Applies XP to a character's level and Valor, spilling past the cap into Valor levels.
        /// Returns how many levels and Valor levels were gained.
        /// </summary>
        public static ProgressionGain AddXp(ref int level, ref long xp, ref int valorLevel, ref long valorXp,
            long amount, ProgressionRates rates)
        {
            if (amount <= 0) return default;

            int levelsGained = 0;
            int valorGained = 0;

            while (level < MaxCharacterLevel && amount > 0)
            {
                long needed = XpForNextLevel(level, rates) - xp;
                if (amount < needed)
                {
                    xp += amount;
                    return new ProgressionGain(levelsGained, valorGained);
                }

                amount -= needed;
                xp = 0;
                level++;
                levelsGained++;
            }

            long perValorLevel = XpForValorLevel(rates);
            if (perValorLevel <= 0) return new ProgressionGain(levelsGained, valorGained);

            valorXp += amount;
            while (valorXp >= perValorLevel)
            {
                valorXp -= perValorLevel;
                valorLevel++;
                valorGained++;
            }

            return new ProgressionGain(levelsGained, valorGained);
        }

        #endregion

        #region Pace preview

        /// <summary>
        /// What the current settings add up to, for the config manager's live preview. Hours scale
        /// from the baseline curve, so they are only as good as the XP table's calibration.
        /// </summary>
        public static PacePreview Preview(ProgressionRates rates, int totalTreeCost)
        {
            int pointsAtCap = EarnedSkillPoints(MaxCharacterLevel, 0, rates);
            float xpScale = rates.XpMultiplier <= 0f ? 1f : rates.XpMultiplier;
            float hoursToCap = BaselineHoursToLevel(MaxCharacterLevel) / xpScale;

            int remaining = Mathf.Max(0, totalTreeCost - pointsAtCap);
            int valorToFill = rates.SkillPointsPerValorLevel <= 0
                ? 0
                : Mathf.CeilToInt(remaining / (float)rates.SkillPointsPerValorLevel);

            float hoursPerValorLevel = BaselineHoursForLevel(MaxCharacterLevel) * rates.ValorLevelXpMultiplier / xpScale;

            return new PacePreview(
                pointsAtCap,
                totalTreeCost <= 0 ? 0f : pointsAtCap / (float)totalTreeCost,
                valorToFill,
                hoursToCap,
                hoursToCap + valorToFill * hoursPerValorLevel);
        }

        #endregion
    }

    public readonly struct ProgressionGain
    {
        public readonly int LevelsGained;
        public readonly int ValorLevelsGained;

        public ProgressionGain(int levelsGained, int valorLevelsGained)
        {
            LevelsGained = levelsGained;
            ValorLevelsGained = valorLevelsGained;
        }

        public bool Any => LevelsGained > 0 || ValorLevelsGained > 0;
    }

    public readonly struct PacePreview
    {
        public readonly int SkillPointsAtCap;
        public readonly float ShareOfAllPoints;
        public readonly int ValorLevelsToFillEveryTree;
        public readonly float HoursToCap;
        public readonly float HoursToFillEveryTree;

        public PacePreview(int skillPointsAtCap, float shareOfAllPoints, int valorLevelsToFillEveryTree,
            float hoursToCap, float hoursToFillEveryTree)
        {
            SkillPointsAtCap = skillPointsAtCap;
            ShareOfAllPoints = shareOfAllPoints;
            ValorLevelsToFillEveryTree = valorLevelsToFillEveryTree;
            HoursToCap = hoursToCap;
            HoursToFillEveryTree = hoursToFillEveryTree;
        }
    }
}
