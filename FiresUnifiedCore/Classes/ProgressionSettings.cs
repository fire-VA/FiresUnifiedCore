using BepInEx.Configuration;
using FiresCore.Sync;

namespace FiresCore.Classes
{
    /// <summary>What happens to points already spent when a rate is lowered below them.</summary>
    public enum OverspentPointPolicy
    {
        Keep,
        Refund
    }

    /// <summary>
    /// A snapshot of the progression rates, so a calculation cannot see one setting change halfway
    /// through. Read it with <see cref="ProgressionSettings.CurrentRates"/>.
    /// </summary>
    public readonly struct ProgressionRates
    {
        public readonly float XpMultiplier;
        public readonly float KillXpMultiplier;
        public readonly float NonCombatXpMultiplier;
        public readonly float QuestXpMultiplier;
        public readonly int SkillPointsPerLevel;
        public readonly int ExtraSkillPointsPerTenthLevel;
        public readonly int SkillPointsPerValorLevel;
        public readonly float ValorLevelXpMultiplier;
        public readonly float AttributePointsPerLevel;
        public readonly float AttributePointsPerValorLevel;
        public readonly int AttributePointsMaximum;
        public readonly float ExpectedXpPerHour;
        public readonly OverspentPointPolicy WhenPointsDrop;

        public ProgressionRates(float xpMultiplier, float killXpMultiplier, float nonCombatXpMultiplier,
            float questXpMultiplier, int skillPointsPerLevel, int extraSkillPointsPerTenthLevel,
            int skillPointsPerValorLevel, float valorLevelXpMultiplier, float attributePointsPerLevel,
            float attributePointsPerValorLevel, int attributePointsMaximum, float expectedXpPerHour,
            OverspentPointPolicy whenPointsDrop)
        {
            XpMultiplier = xpMultiplier;
            KillXpMultiplier = killXpMultiplier;
            NonCombatXpMultiplier = nonCombatXpMultiplier;
            QuestXpMultiplier = questXpMultiplier;
            SkillPointsPerLevel = skillPointsPerLevel;
            ExtraSkillPointsPerTenthLevel = extraSkillPointsPerTenthLevel;
            SkillPointsPerValorLevel = skillPointsPerValorLevel;
            ValorLevelXpMultiplier = valorLevelXpMultiplier;
            AttributePointsPerLevel = attributePointsPerLevel;
            AttributePointsPerValorLevel = attributePointsPerValorLevel;
            AttributePointsMaximum = attributePointsMaximum;
            ExpectedXpPerHour = expectedXpPerHour;
            WhenPointsDrop = whenPointsDrop;
        }
    }

    /// <summary>
    /// Server-locked progression rates. Every one is synced and admin-only, so a change applies to
    /// every character at once - nothing earned is stored. Plan: Docs/PLAN_ClassesFoundation.md
    /// </summary>
    public static class ProgressionSettings
    {
        private const string Section = "FiresRPGClasses.Progression";

        private const float DefaultXpMultiplier = 1.0f;
        private const int DefaultSkillPointsPerLevel = 3;
        private const int DefaultExtraSkillPointsPerTenthLevel = 1;
        private const int DefaultSkillPointsPerValorLevel = 4;
        private const float DefaultValorLevelXpMultiplier = 1.8f;
        private const float DefaultAttributePointsPerLevel = 0.5f;
        private const int DefaultAttributePointsMaximum = 80;

        /// <summary>
        /// First-pass calibration: the XP a player is expected to earn per hour. The shipped curve is
        /// this times the hours each level is meant to take, so refitting the pace is one number until
        /// a playtest log replaces the whole table.
        /// </summary>
        private const float DefaultExpectedXpPerHour = 10000f;

        private const float MinMultiplier = 0.01f;
        private const float MaxMultiplier = 100f;
        private const int MaxSkillPointsPerLevel = 20;
        private const float MinExpectedXpPerHour = 100f;
        private const float MaxExpectedXpPerHour = 10000000f;

        public static ConfigEntry<float> XpMultiplier;
        public static ConfigEntry<float> KillXpMultiplier;
        public static ConfigEntry<float> NonCombatXpMultiplier;
        public static ConfigEntry<float> QuestXpMultiplier;
        public static ConfigEntry<int> SkillPointsPerLevel;
        public static ConfigEntry<int> ExtraSkillPointsPerTenthLevel;
        public static ConfigEntry<int> SkillPointsPerValorLevel;
        public static ConfigEntry<float> ValorLevelXpMultiplier;
        public static ConfigEntry<float> AttributePointsPerLevel;
        public static ConfigEntry<float> AttributePointsPerValorLevel;
        public static ConfigEntry<int> AttributePointsMaximum;
        public static ConfigEntry<float> ExpectedXpPerHour;
        public static ConfigEntry<OverspentPointPolicy> WhenPointsDrop;

        public static bool IsInitialized => XpMultiplier != null;

        public static void Initialize(ConfigFile config)
        {
            XpMultiplier = config.Bind(
                Section, "XP multiplier", DefaultXpMultiplier,
                new ConfigDescription(
                    "Scales all XP gained. [Synced with Server]",
                    new AcceptableValueRange<float>(MinMultiplier, MaxMultiplier)));

            KillXpMultiplier = config.Bind(
                Section, "Kill XP multiplier", DefaultXpMultiplier,
                new ConfigDescription(
                    "Kills only. Multiplies with the overall XP multiplier. [Synced with Server]",
                    new AcceptableValueRange<float>(MinMultiplier, MaxMultiplier)));

            NonCombatXpMultiplier = config.Bind(
                Section, "Non-combat XP multiplier", DefaultXpMultiplier,
                new ConfigDescription(
                    "Gathering, crafting, building and exploring. [Synced with Server]",
                    new AcceptableValueRange<float>(MinMultiplier, MaxMultiplier)));

            QuestXpMultiplier = config.Bind(
                Section, "Quest XP multiplier", DefaultXpMultiplier,
                new ConfigDescription(
                    "Quest rewards, including EpicMMO_Exp grants. [Synced with Server]",
                    new AcceptableValueRange<float>(MinMultiplier, MaxMultiplier)));

            SkillPointsPerLevel = config.Bind(
                Section, "Skill points per level", DefaultSkillPointsPerLevel,
                new ConfigDescription(
                    "Skill points for each character level after the first. [Synced with Server]",
                    new AcceptableValueRange<int>(0, MaxSkillPointsPerLevel)));

            ExtraSkillPointsPerTenthLevel = config.Bind(
                Section, "Extra skill points every tenth level", DefaultExtraSkillPointsPerTenthLevel,
                new ConfigDescription(
                    "Added on levels 10, 20 and so on. [Synced with Server]",
                    new AcceptableValueRange<int>(0, MaxSkillPointsPerLevel)));

            SkillPointsPerValorLevel = config.Bind(
                Section, "Skill points per Valor level", DefaultSkillPointsPerValorLevel,
                new ConfigDescription(
                    "Skill points for each Valor level, earned past the character level cap. Lower fills "
                    + "every tree more slowly, higher more quickly. [Synced with Server]",
                    new AcceptableValueRange<int>(0, MaxSkillPointsPerLevel)));

            ValorLevelXpMultiplier = config.Bind(
                Section, "Valor level XP", DefaultValorLevelXpMultiplier,
                new ConfigDescription(
                    "XP for each Valor level, as a multiple of the last character level's. [Synced with Server]",
                    new AcceptableValueRange<float>(MinMultiplier, MaxMultiplier)));

            AttributePointsPerLevel = config.Bind(
                Section, "Attribute points per level", DefaultAttributePointsPerLevel,
                new ConfigDescription(
                    "Fractions accumulate, so 0.5 is one point every second level. [Synced with Server]",
                    new AcceptableValueRange<float>(0f, MaxSkillPointsPerLevel)));

            AttributePointsPerValorLevel = config.Bind(
                Section, "Attribute points per Valor level", DefaultAttributePointsPerLevel,
                new ConfigDescription(
                    "As above, for Valor levels. [Synced with Server]",
                    new AcceptableValueRange<float>(0f, MaxSkillPointsPerLevel)));

            AttributePointsMaximum = config.Bind(
                Section, "Attribute points maximum", DefaultAttributePointsMaximum,
                new ConfigDescription(
                    "Total attribute points a character can ever hold. [Synced with Server]",
                    new AcceptableValueRange<int>(0, 1000)));

            ExpectedXpPerHour = config.Bind(
                Section, "Expected XP per hour", DefaultExpectedXpPerHour,
                new ConfigDescription(
                    "Calibration for the shipped XP curve: the XP a player is expected to earn per hour. "
                    + "The curve is this times the hours each level is meant to take. [Synced with Server]",
                    new AcceptableValueRange<float>(MinExpectedXpPerHour, MaxExpectedXpPerHour)));

            WhenPointsDrop = config.Bind(
                Section, "When points drop", OverspentPointPolicy.Keep,
                "What happens when a rate is lowered below what a character has already spent. "
                + "Keep: nothing is unlearned, and they cannot spend again until they earn their way back. "
                + "Refund: every tree is refunded so they re-spend within the new budget. "
                + "Neither ever takes away a level. [Synced with Server]");
        }

        public static void BindToSync(ConfigSync configSync)
        {
            configSync.AddConfigEntry(XpMultiplier);
            configSync.AddConfigEntry(KillXpMultiplier);
            configSync.AddConfigEntry(NonCombatXpMultiplier);
            configSync.AddConfigEntry(QuestXpMultiplier);
            configSync.AddConfigEntry(SkillPointsPerLevel);
            configSync.AddConfigEntry(ExtraSkillPointsPerTenthLevel);
            configSync.AddConfigEntry(SkillPointsPerValorLevel);
            configSync.AddConfigEntry(ValorLevelXpMultiplier);
            configSync.AddConfigEntry(AttributePointsPerLevel);
            configSync.AddConfigEntry(AttributePointsPerValorLevel);
            configSync.AddConfigEntry(AttributePointsMaximum);
            configSync.AddConfigEntry(ExpectedXpPerHour);
            configSync.AddConfigEntry(WhenPointsDrop);
        }

        /// <summary>
        /// The rates in force. Falls back to the shipped defaults before Initialize runs, so a caller
        /// during load gets the documented pace rather than zeroes.
        /// </summary>
        public static ProgressionRates CurrentRates => IsInitialized
            ? new ProgressionRates(
                XpMultiplier.Value,
                KillXpMultiplier.Value,
                NonCombatXpMultiplier.Value,
                QuestXpMultiplier.Value,
                SkillPointsPerLevel.Value,
                ExtraSkillPointsPerTenthLevel.Value,
                SkillPointsPerValorLevel.Value,
                ValorLevelXpMultiplier.Value,
                AttributePointsPerLevel.Value,
                AttributePointsPerValorLevel.Value,
                AttributePointsMaximum.Value,
                ExpectedXpPerHour.Value,
                WhenPointsDrop.Value)
            : DefaultRates;

        public static ProgressionRates DefaultRates => new ProgressionRates(
            DefaultXpMultiplier,
            DefaultXpMultiplier,
            DefaultXpMultiplier,
            DefaultXpMultiplier,
            DefaultSkillPointsPerLevel,
            DefaultExtraSkillPointsPerTenthLevel,
            DefaultSkillPointsPerValorLevel,
            DefaultValorLevelXpMultiplier,
            DefaultAttributePointsPerLevel,
            DefaultAttributePointsPerLevel,
            DefaultAttributePointsMaximum,
            DefaultExpectedXpPerHour,
            OverspentPointPolicy.Keep);
    }
}
