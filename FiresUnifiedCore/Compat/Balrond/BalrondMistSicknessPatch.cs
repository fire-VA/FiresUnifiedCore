using System;
using HarmonyLib;

namespace FiresCore.Compat.Balrond
{
    /// <summary>
    /// Negates BalrondAmazingNature's <c>SE_MistSickness</c> debuff. Balrond's
    /// <c>MistSicknessPatches</c> postfixes <c>Player.UpdateEnvStatusEffects</c> and calls
    /// <c>SEMan.AddStatusEffect(hash, true)</c> every frame the player stands in mist, applying an
    /// <c>SE_Stats</c> with −15% move speed and −10% stamina regen.
    ///
    /// Rather than fight Balrond's per-frame add/remove, we block the status effect at the only place
    /// it can enter the player: <see cref="SEMan.AddStatusEffect(int, bool, int, float)"/>. The hash
    /// is the vanilla stable hash of the SE name — the same value SEMan registers the effect under, so
    /// it necessarily matches whatever Balrond passes. A second prefix covers the (unused-by-Balrond,
    /// but possible) StatusEffect-object overload for completeness.
    /// </summary>
    internal static class BalrondMistSicknessPatch
    {
        private const string MistSicknessName = "SE_MistSickness";
        private static readonly int MistSicknessHash = MistSicknessName.GetStableHashCode();

        private static bool ShouldBlock => BalrondCompatConfig.NegateMistSicknessDebuff != null
                                           && BalrondCompatConfig.NegateMistSicknessDebuff.Value;

        [HarmonyPatch(typeof(SEMan), nameof(SEMan.AddStatusEffect),
            new Type[] { typeof(int), typeof(bool), typeof(int), typeof(float), typeof(short) })]
        private static class AddByHash
        {
            private static bool Prefix(int nameHash, ref StatusEffect __result)
            {
                if (ShouldBlock && nameHash == MistSicknessHash)
                {
                    __result = null;
                    return false; // skip the original add
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(SEMan), nameof(SEMan.AddStatusEffect),
            new Type[] { typeof(StatusEffect), typeof(bool), typeof(int), typeof(float), typeof(short) })]
        private static class AddByInstance
        {
            private static bool Prefix(StatusEffect statusEffect, ref StatusEffect __result)
            {
                if (ShouldBlock && statusEffect != null
                    && string.Equals(statusEffect.name, MistSicknessName, StringComparison.Ordinal))
                {
                    __result = null;
                    return false;
                }
                return true;
            }
        }
    }
}
