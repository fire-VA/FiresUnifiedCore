using System;
using System.Collections.Generic;

namespace FiresCore.Terrain
{
    /// <summary>
    /// The height limits the patched TerrainComp methods read in place of Valheim's ±8 m literals.
    /// Evaluated on every call. Returns the vanilla limits while disabled or suppressed.
    /// </summary>
    public static class HeightmapOverrideLimits
    {
        public const float VanillaClamp = 8f;

        private static readonly List<Suppressor> s_suppressors = new List<Suppressor>();

        private readonly struct Suppressor
        {
            public readonly string Owner;
            public readonly Func<bool> IsSuppressing;

            public Suppressor(string owner, Func<bool> isSuppressing)
            {
                Owner = owner;
                IsSuppressing = isSuppressing;
            }
        }

        /// <summary>
        /// Registers a predicate that forces the vanilla limits while it returns true.
        /// The predicate must evaluate identically on every peer.
        /// </summary>
        public static void RegisterSuppressor(string owner, Func<bool> isSuppressing)
        {
            if (string.IsNullOrEmpty(owner)) throw new ArgumentException("Suppressor owner is required.", nameof(owner));
            if (isSuppressing == null) throw new ArgumentNullException(nameof(isSuppressing));
            s_suppressors.Add(new Suppressor(owner, isSuppressing));
        }

        public static float Max() => IsActive() ? HeightmapOverrideConfig.MaxHeight.Value : VanillaClamp;

        public static float Min() => IsActive() ? HeightmapOverrideConfig.MinHeight.Value : -VanillaClamp;

        public static float MinAbs() => Math.Abs(Min());

        public static bool IsActive() => IsEnabled() && ActiveSuppressor() == null;

        public static bool IsEnabled() =>
            HeightmapOverrideConfig.Enabled != null && HeightmapOverrideConfig.Enabled.Value;

        /// <summary>Owner of the first suppressor currently forcing vanilla limits, or null.</summary>
        public static string ActiveSuppressor()
        {
            foreach (var suppressor in s_suppressors)
                if (suppressor.IsSuppressing()) return suppressor.Owner;
            return null;
        }
    }
}
