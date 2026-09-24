using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Classes
{
    /// <summary>What produced an XP award. Decides which multiplier and which cap applies.</summary>
    public enum XpSourceKind
    {
        Kill,
        Discovery,
        RepeatableWork,
        SkillLevelUp,
        Quest,
        Consumable
    }

    /// <summary>The one-time discoveries, each worth a share of the current level rather than a flat amount.</summary>
    public enum DiscoveryKind
    {
        Biome,
        Boss,
        LocationType,
        CreatureType,
        ItemType,
        RecipeOrPiece
    }

    /// <summary>
    /// How much XP each source pays. Kill XP comes from the creature's own health so modded creatures
    /// are handled without a table; discoveries pay a share of the current level so they are worth the
    /// same early and late. Plan: Docs/PLAN_ClassesFoundation.md
    /// </summary>
    public static class XpSources
    {
        public const float DefaultKillXpPerHealth = 0.1f;
        public const float DefaultBossKillMultiplier = 5f;
        public const float DefaultKillRangeMetres = 50f;
        public const float DefaultGroupShare = 1.0f;
        public const int MinimumKillHealth = 10;

        /// <summary>Summoned adds pay a fraction, so a fight cannot be stretched to farm them.</summary>
        public const float SummonedCreatureShare = 0.1f;

        public const float DefaultSkillLevelUpShareOfLevel = 0.02f;
        public const float DefaultRepeatableHourlyCapShareOfLevel = 0.20f;
        public const float DefaultRestedBonus = 0.10f;
        public const float DefaultXpStoneShareOfLevel = 1f / 3f;

        /// <summary>A player counts as active in a fight for this long after damaging or being damaged.</summary>
        public const float ActiveInFightSeconds = 30f;

        private static readonly Dictionary<DiscoveryKind, float> DiscoveryShareOfLevel =
            new Dictionary<DiscoveryKind, float>
            {
                { DiscoveryKind.Biome, 0.25f },
                { DiscoveryKind.Boss, 0.50f },
                { DiscoveryKind.LocationType, 0.10f },
                { DiscoveryKind.CreatureType, 0.03f },
                { DiscoveryKind.ItemType, 0.01f },
                { DiscoveryKind.RecipeOrPiece, 0.01f },
            };

        /// <summary>
        /// XP for a kill, before any multiplier or group share: the creature's max health times the
        /// per-health rate, multiplied by its star level, and again for a boss. Anything under
        /// MinimumKillHealth pays nothing.
        /// </summary>
        public static long KillXp(float maxHealth, int starLevel, bool isBoss, bool wasSummoned,
            float killXpPerHealth = DefaultKillXpPerHealth, float bossMultiplier = DefaultBossKillMultiplier)
        {
            if (maxHealth < MinimumKillHealth) return 0;

            float xp = maxHealth * killXpPerHealth * (starLevel + 1);
            if (isBoss) xp *= bossMultiplier;
            if (wasSummoned) xp *= SummonedCreatureShare;
            return (long)Math.Floor(xp);
        }

        /// <summary>A one-time discovery's XP: a share of what the character's current level costs.</summary>
        public static long DiscoveryXp(DiscoveryKind kind, long xpForCurrentLevel) =>
            (long)Math.Floor(xpForCurrentLevel * ShareOfLevel(kind));

        public static float ShareOfLevel(DiscoveryKind kind) =>
            DiscoveryShareOfLevel.TryGetValue(kind, out float share) ? share : 0f;

        /// <summary>A vanilla skill level-up's XP, as a share of the current level.</summary>
        public static long SkillLevelUpXp(long xpForCurrentLevel,
            float shareOfLevel = DefaultSkillLevelUpShareOfLevel) =>
            (long)Math.Floor(xpForCurrentLevel * shareOfLevel);

        /// <summary>An XP stone's value: a share of a level at the middle of its biome band.</summary>
        public static long XpStoneValue(long xpForBandMiddleLevel,
            float shareOfLevel = DefaultXpStoneShareOfLevel) =>
            (long)Math.Floor(xpForBandMiddleLevel * shareOfLevel);

        /// <summary>
        /// The multiplier a source actually gets. Boosts and Rested lift kills, repeatable work and
        /// skill level-ups only - never discoveries, quests or stones, which are fixed by design.
        /// </summary>
        public static float MultiplierFor(XpSourceKind kind, ProgressionRates rates, float boostFraction, bool isRested)
        {
            float multiplier = rates.XpMultiplier;

            switch (kind)
            {
                case XpSourceKind.Kill:
                    multiplier *= rates.KillXpMultiplier * (1f + boostFraction + RestedFraction(isRested));
                    break;
                case XpSourceKind.RepeatableWork:
                    multiplier *= rates.NonCombatXpMultiplier * (1f + boostFraction + RestedFraction(isRested));
                    break;
                case XpSourceKind.SkillLevelUp:
                    multiplier *= rates.NonCombatXpMultiplier * (1f + boostFraction + RestedFraction(isRested));
                    break;
                case XpSourceKind.Discovery:
                    multiplier *= rates.NonCombatXpMultiplier;
                    break;
                case XpSourceKind.Quest:
                    multiplier *= rates.QuestXpMultiplier;
                    break;
                case XpSourceKind.Consumable:
                    break;
            }

            return multiplier;
        }

        private static float RestedFraction(bool isRested) => isRested ? DefaultRestedBonus : 0f;

        /// <summary>True for the sources the hourly repeatable-work cap applies to.</summary>
        public static bool IsCapped(XpSourceKind kind) => kind == XpSourceKind.RepeatableWork;

        /// <summary>
        /// Whether a player is eligible for a kill's XP: close enough, and active in the fight recently.
        /// Standing nearby doing nothing pays nothing.
        /// </summary>
        public static bool IsEligibleForKill(float distanceToDeath, float secondsSinceActive,
            float killRange = DefaultKillRangeMetres) =>
            distanceToDeath <= killRange && secondsSinceActive <= ActiveInFightSeconds;
    }

    /// <summary>
    /// The rolling hourly allowance for repeatable work. It refills continuously rather than resetting
    /// on the hour, so it never pays to wait for a tick.
    /// </summary>
    public class RepeatableWorkBudget
    {
        private const float SecondsPerHour = 3600f;

        private double _spent;
        private double _lastRefillTime;

        /// <summary>XP still available this hour, given the cap for the character's current level.</summary>
        public long Remaining(long hourlyCap, double now)
        {
            Refill(hourlyCap, now);
            return (long)Math.Max(0, hourlyCap - _spent);
        }

        /// <summary>
        /// Takes as much of an award as the allowance still permits, and returns what was actually
        /// granted. Beyond the cap this returns zero.
        /// </summary>
        public long Take(long requested, long hourlyCap, double now)
        {
            if (requested <= 0) return 0;

            Refill(hourlyCap, now);
            long remaining = (long)Math.Max(0, hourlyCap - _spent);
            long granted = Math.Min(requested, remaining);
            _spent += granted;
            return granted;
        }

        /// <summary>The cap for a level: a share of what that level costs.</summary>
        public static long HourlyCap(long xpForCurrentLevel,
            float shareOfLevel = XpSources.DefaultRepeatableHourlyCapShareOfLevel) =>
            (long)Math.Floor(xpForCurrentLevel * shareOfLevel);

        private void Refill(long hourlyCap, double now)
        {
            if (_lastRefillTime <= 0d)
            {
                _lastRefillTime = now;
                return;
            }

            double elapsed = now - _lastRefillTime;
            if (elapsed <= 0d) return;

            _lastRefillTime = now;
            _spent = Math.Max(0d, _spent - hourlyCap * (elapsed / SecondsPerHour));
        }
    }
}
