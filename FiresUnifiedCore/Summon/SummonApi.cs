using System;
using System.Collections.Generic;

namespace FiresCore.Summon
{
    public enum SummonMode { Follow, Guard, Attack }

    /// <summary>What a summon is asked for (RPGClasses supplies the class numbers).</summary>
    public sealed class SummonOptions
    {
        public int Level = 1;
        public float Scale = 1f;
        /// <summary>Seconds; 0 = until dismissed or the owner dies.</summary>
        public float Duration;
        public SummonMode Mode = SummonMode.Follow;
        /// <summary>Summons sharing a group share the cap (one shared cap for the class summons).</summary>
        public string CapGroup = "rpg-summons";
        /// <summary>The cap for this owner and group (default 2, +1 per 15 levels, max 6). The server clamps it to 1..6.</summary>
        public int Cap = 2;
        public string SourceTag = "";
    }

    /// <summary>One of the local player's summons, for UI.</summary>
    public sealed class SummonInfo
    {
        public ZDOID Id;
        public string Prefab;
        public string DisplayName;
        public float Health, HealthMax;
        /// <summary>Seconds left, or -1 when there is no timer.</summary>
        public float Remaining;
        public SummonMode Mode;
        public string SourceTag;
    }

    /// <summary>
    /// Core's summons (Tools\CLASSES_DRUID_NECROMANCER.md section 6 item 8). A summon is a vanilla creature spawned
    /// tamed next to its owner (the way the Dead Raiser does it), marked with its owner's player id, a timer, a mode and a
    /// cap group. The server enforces the cap (the oldest goes first) and the timer. Summons follow their owner through
    /// portals and persist across logout (saved with the character, re-spawned at login); they despawn on the owner's death
    /// or when the timer ends. Their hits on players count as the owner's for PvP. Every event is static and safe to
    /// subscribe at any time.
    /// </summary>
    public static class SummonApi
    {
        public static event Action Changed;
        public static event Action<SummonInfo> Summoned;
        public static event Action<ZDOID> Despawned;

        /// <summary>Spawns a summon next to the local player. Returns ZDOID.None (and why) when it couldn't.</summary>
        public static ZDOID Summon(string prefab, SummonOptions options, out string reason)
            => SummonClient.Spawn(prefab, options ?? new SummonOptions(), out reason);

        public static void Dismiss(ZDOID id) => SummonClient.Dismiss(id);

        public static void DismissAll() => SummonClient.DismissAll(save: false);

        public static void SetMode(ZDOID id, SummonMode mode) => SummonClient.SetMode(id, mode);

        /// <summary>The local player's summons that are loaded near them.</summary>
        public static IReadOnlyList<SummonInfo> MySummons() => SummonClient.Mine;

        public static int MyCount => SummonClient.Mine.Count;

        /// <summary>The cap last asked for by the local player (default 2).</summary>
        public static int MyCap => SummonClient.LastCap;

        internal static void RaiseChanged() => Safe(() => Changed?.Invoke());
        internal static void RaiseSummoned(SummonInfo i) => Safe(() => Summoned?.Invoke(i));
        internal static void RaiseDespawned(ZDOID id) => Safe(() => Despawned?.Invoke(id));

        private static void Safe(Action a)
        {
            try { a(); }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogWarning("[Summon] a subscriber threw: " + e.Message); }
        }
    }

    internal static class SummonKeys
    {
        internal const string Tag = "[Summon] ";
        internal static readonly int Owner = "fires_summon_owner".GetStableHashCode();
        internal static readonly int Expiry = "fires_summon_expiry".GetStableHashCode();   // ZNet time in ms; 0 = none
        internal static readonly int Made = "fires_summon_made".GetStableHashCode();       // ZNet time in ms
        internal static readonly int Mode = "fires_summon_mode".GetStableHashCode();
        internal static readonly int Group = "fires_summon_group".GetStableHashCode();
        internal static readonly int Source = "fires_summon_tag".GetStableHashCode();
        internal static readonly int Level = "fires_summon_level".GetStableHashCode();
        internal static readonly int Scale = "fires_summon_scale".GetStableHashCode();

        internal static long NowMs => ZNet.instance != null ? (long)(ZNet.instance.GetTimeSeconds() * 1000.0) : 0L;
    }
}
