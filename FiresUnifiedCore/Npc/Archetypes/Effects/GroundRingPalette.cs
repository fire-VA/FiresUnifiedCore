using UnityEngine;

namespace FiresCore.Npc.Archetypes.Effects
{
    public enum GroundRingKind
    {
        Heal,
        Buff,
        Damage,
    }

    public enum GroundRingRipple
    {
        None,
        Inward,
        Outward,
    }

    /// <summary>Everything that makes one ring kind recognisable without relying on its colour.</summary>
    public struct GroundRingPattern
    {
        public int SegmentCount;
        public float TurnsPerSecond;
        public int DashStride;
        public GroundRingRipple Ripple;
        public float SawtoothFraction;
        public int CrossTickCount;
    }

    public static class GroundRingPalette
    {
        private const float HealStandardR = 0.247f, HealStandardG = 0.839f, HealStandardB = 0.498f;
        private const float BuffStandardR = 0.353f, BuffStandardG = 0.663f, BuffStandardB = 0.941f;
        private const float DamageStandardR = 0.878f, DamageStandardG = 0.314f, DamageStandardB = 0.227f;

        private const float HealBlindR = 0.310f, HealBlindG = 0.820f, HealBlindB = 0.878f;
        private const float BuffBlindR = 0.690f, BuffBlindG = 0.545f, BuffBlindB = 0.910f;
        private const float DamageBlindR = 0.941f, DamageBlindG = 0.541f, DamageBlindB = 0.165f;

        private const int HealSegments = 28;
        private const int BuffSegments = 32;
        private const int DamageSegments = 36;

        private const float HealTurnsPerSecond = 0.04f;
        private const float BuffTurnsPerSecond = 0.10f;
        private const float DamageTurnsPerSecond = -0.06f;

        private const int SolidDashStride = 1;
        private const int RunicDashStride = 3;

        private const float NoSawtooth = 0f;
        private const float DamageSawtoothFraction = 0.08f;

        private const int HealCrossTicks = 4;
        private const int NoCrossTicks = 0;

        public static Color ColorFor(GroundRingKind kind, bool colorBlindPalette)
        {
            switch (kind)
            {
                case GroundRingKind.Heal:
                    return colorBlindPalette
                        ? new Color(HealBlindR, HealBlindG, HealBlindB, 1f)
                        : new Color(HealStandardR, HealStandardG, HealStandardB, 1f);
                case GroundRingKind.Buff:
                    return colorBlindPalette
                        ? new Color(BuffBlindR, BuffBlindG, BuffBlindB, 1f)
                        : new Color(BuffStandardR, BuffStandardG, BuffStandardB, 1f);
                case GroundRingKind.Damage:
                    return colorBlindPalette
                        ? new Color(DamageBlindR, DamageBlindG, DamageBlindB, 1f)
                        : new Color(DamageStandardR, DamageStandardG, DamageStandardB, 1f);
            }
            Debug.LogError($"[GroundRingPalette] No colour defined for ring kind {kind}");
            return Color.white;
        }

        public static GroundRingPattern PatternFor(GroundRingKind kind)
        {
            switch (kind)
            {
                case GroundRingKind.Heal:
                    return new GroundRingPattern
                    {
                        SegmentCount = HealSegments,
                        TurnsPerSecond = HealTurnsPerSecond,
                        DashStride = SolidDashStride,
                        Ripple = GroundRingRipple.Inward,
                        SawtoothFraction = NoSawtooth,
                        CrossTickCount = HealCrossTicks,
                    };
                case GroundRingKind.Buff:
                    return new GroundRingPattern
                    {
                        SegmentCount = BuffSegments,
                        TurnsPerSecond = BuffTurnsPerSecond,
                        DashStride = RunicDashStride,
                        Ripple = GroundRingRipple.None,
                        SawtoothFraction = NoSawtooth,
                        CrossTickCount = NoCrossTicks,
                    };
                case GroundRingKind.Damage:
                    return new GroundRingPattern
                    {
                        SegmentCount = DamageSegments,
                        TurnsPerSecond = DamageTurnsPerSecond,
                        DashStride = SolidDashStride,
                        Ripple = GroundRingRipple.Outward,
                        SawtoothFraction = DamageSawtoothFraction,
                        CrossTickCount = NoCrossTicks,
                    };
            }
            Debug.LogError($"[GroundRingPalette] No pattern defined for ring kind {kind}");
            return new GroundRingPattern { SegmentCount = BuffSegments, DashStride = SolidDashStride };
        }
    }
}
