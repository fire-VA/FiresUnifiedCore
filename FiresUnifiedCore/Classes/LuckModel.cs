using System;
using UnityEngine;

namespace FiresCore.Classes
{
    /// <summary>
    /// The one luck model for players and companions. 50 is plain Valheim; base luck stays 0-100 but
    /// effective luck is uncapped, and every 100 points above 50 is one more attempt at a roll.
    /// Plan: Docs/PLAN_ClassesFoundation.md
    /// </summary>
    public static class LuckModel
    {
        public const float VanillaLuck = 50f;
        public const int MinBaseLuck = 0;
        public const int MaxBaseLuck = 100;
        public const int DefaultMaxAttempts = 3;

        private const float LuckPointsPerAttempt = 100f;
        private const string PlayerZdoLuckKey = "fires_luck";

        /// <summary>Extra attempts a roll gets, as a fraction. 0 at vanilla luck, 1 at 150, -0.5 at 0.</summary>
        public static float ExtraAttempts(float effectiveLuck) => (effectiveLuck - VanillaLuck) / LuckPointsPerAttempt;

        /// <summary>
        /// The chance after luck, for a yes/no roll. Equivalent to keeping the best of
        /// 1 + ExtraAttempts tries, so it never reaches 1 and helps rare outcomes most.
        /// Luck below vanilla shrinks the chance instead.
        /// </summary>
        public static float ChanceWithLuck(float baseChance, float effectiveLuck, int maxAttempts = DefaultMaxAttempts)
        {
            if (baseChance <= 0f) return 0f;
            if (baseChance >= 1f) return 1f;

            float attempts = ClampAttempts(1f + ExtraAttempts(effectiveLuck), maxAttempts);
            return 1f - Mathf.Pow(1f - baseChance, attempts);
        }

        /// <summary>True when a yes/no roll at this base chance succeeds for this luck.</summary>
        public static bool RollChance(float baseChance, float effectiveLuck, int maxAttempts = DefaultMaxAttempts) =>
            UnityEngine.Random.value < ChanceWithLuck(baseChance, effectiveLuck, maxAttempts);

        /// <summary>
        /// Rolls an amount 1 + ExtraAttempts times, the fractional part being the chance of one more
        /// attempt, and keeps the highest result.
        /// </summary>
        public static int RollAmount(Func<int> rollOnce, float effectiveLuck, int maxAttempts = DefaultMaxAttempts)
        {
            if (rollOnce == null) throw new ArgumentNullException(nameof(rollOnce));

            float attempts = ClampAttempts(1f + ExtraAttempts(effectiveLuck), maxAttempts);
            int wholeAttempts = Mathf.FloorToInt(attempts);
            if (UnityEngine.Random.value < attempts - wholeAttempts) wholeAttempts++;
            if (wholeAttempts < 1) wholeAttempts = 1;

            int best = rollOnce();
            for (int attempt = 1; attempt < wholeAttempts; attempt++)
            {
                int rolled = rollOnce();
                if (rolled > best) best = rolled;
            }
            return best;
        }

        public static int ClampBase(int baseLuck) => Mathf.Clamp(baseLuck, MinBaseLuck, MaxBaseLuck);

        /// <summary>The companion roll: the average of three, so most land near the middle.</summary>
        public static int RollBaseLuck()
        {
            float averaged = (UnityEngine.Random.value + UnityEngine.Random.value + UnityEngine.Random.value) / 3f;
            return ClampBase(Mathf.RoundToInt(averaged * MaxBaseLuck));
        }

        public static string LuckTier(float effectiveLuck)
        {
            if (effectiveLuck >= 90f) return "Blessed";
            if (effectiveLuck >= 75f) return "Lucky";
            if (effectiveLuck >= 60f) return "Fortunate";
            if (effectiveLuck >= 40f) return "Average";
            if (effectiveLuck >= 25f) return "Unlucky";
            if (effectiveLuck >= 10f) return "Cursed";
            return "Doomed";
        }

        /// <summary>
        /// Publishes a player's effective luck on their own ZDO. Drops are generated on whichever peer
        /// processes the event, so that peer has to be able to read the luck of a player it does not own.
        /// </summary>
        public static void PublishPlayerLuck(Player player, float effectiveLuck)
        {
            var zdo = player?.m_nview?.GetZDO();
            if (zdo == null) return;
            zdo.Set(PlayerZdoLuckKey, effectiveLuck);
        }

        /// <summary>Effective luck for a player, from their ZDO. Vanilla luck when nothing is published.</summary>
        public static float ReadPlayerLuck(Player player) => ReadPlayerLuck(player?.m_nview?.GetZDO());

        public static float ReadPlayerLuck(ZDO playerZdo) =>
            playerZdo == null ? VanillaLuck : playerZdo.GetFloat(PlayerZdoLuckKey, VanillaLuck);

        private static float ClampAttempts(float attempts, int maxAttempts)
        {
            float ceiling = maxAttempts < 1 ? 1f : maxAttempts;
            return Mathf.Clamp(attempts, 0f, ceiling);
        }
    }
}
