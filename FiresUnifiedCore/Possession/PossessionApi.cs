using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Possession
{
    /// <summary>Why a possession ended (the engine's reason strings, mapped).</summary>
    public enum PossessEndReason { Released, Death, Range, Duration, Logout, Gone, Revoked, Portal, Error }

    /// <summary>How the player's own body is kept while possessing.</summary>
    public enum PossessBody
    {
        /// <summary>The body stays where it was (admin, Necromancer). Usually with PlayerInvulnerable + ChannelLock.</summary>
        Frozen,
        /// <summary>The body is hidden and carried inside the form (Druid Wildshape). Humanoid-rig creatures; else Frozen.</summary>
        Hidden
    }

    /// <summary>One of the possessed creature's attacks, for the bar.</summary>
    public struct PossessAttackInfo
    {
        public string Name;
        /// <summary>"main" (LMB), "special" (MMB) or "other" (Alt + mouse wheel picks it).</summary>
        public string Kind;
        public bool Ranged;
    }

    /// <summary>
    /// What a caller asks for. Core admin possession uses Body Frozen + PlayerInvulnerable + ChannelLock; RPGClasses fills
    /// in its own (Tools\CLASSES_DRUID_NECROMANCER.md section 6).
    /// </summary>
    public sealed class PossessOptions
    {
        public PossessBody Body = PossessBody.Frozen;
        public bool PlayerInvulnerable;
        /// <summary>
        /// 0.2.321 (Fire 10-07): a Frozen body is ghost + god + debug fly while possessing (vanilla's own devcommands methods,
        /// restored exactly at the end), so the creature's attacks can't push it off its spot. Admin possession sets it. Off by
        /// default for class skills: vanilla marks a victim "cheated" when a Player in god/ghost/fly hits it, so kills made while
        /// possessing would drop cheated loot. Class skills keep PlayerInvulnerable instead.
        /// </summary>
        public bool BodyUntouchable;
        public bool ChannelLock;

        /// <summary>Seconds; 0 = no limit.</summary>
        public float MaxDuration;
        /// <summary>The body-to-creature leash in metres; 0 = none.</summary>
        public float MaxRange = 60f;
        /// <summary>The creature goes back to where it was taken on release (Fire's NPC rule).</summary>
        public bool ReturnToAnchor = true;
        /// <summary>Wildshape: the creature is the form's body and is destroyed at the end (no return).</summary>
        public bool ConsumeTarget;

        /// <summary>null = any creature the general rules allow; a non-null string refuses with that reason.</summary>
        public Func<Character, string> Filter;
        /// <summary>Checked each frame; false ends the possession ("released"), e.g. the Necromancer's eitr.</summary>
        public Func<bool> ContinueWhile;

        /// <summary>A separate health pool for the form (Druid); 0 = none (the creature's own health is used).</summary>
        public float FormHealth;
        /// <summary>The form's attack damage multiplier, e.g. scaled by class level; 1 = unchanged.</summary>
        public float DamageScale = 1f;

        /// <summary>The Necromancer's levitation in metres; 0 = none.</summary>
        public float ChannelHover;
        public float HoverRise = 0.6f;
        public float HoverBob = 0.15f;
        public float HoverPeriod = 2.5f;
        public float HoverDescent = 0.6f;
        public Action<Player> ChannelFxStart;
        public Action<Player> ChannelFxStop;
        /// <summary>Called in LateUpdate after the animator, for bone overrides (arms half-raised, head back).</summary>
        public Action<Player> ChannelPose;

        /// <summary>"core-admin", "rpg-druid", "rpg-necro", ... (logs, the server's validator lookup).</summary>
        public string SourceTag = "core-admin";
    }

    /// <summary>The live possession, for UI and callers.</summary>
    public sealed class PossessHandle
    {
        internal float StartTime;
        internal bool Ended;
        internal readonly List<PossessAttackInfo> AttackList = new List<PossessAttackInfo>();

        public Character Creature { get; internal set; }
        public Player Player { get; internal set; }
        public PossessOptions Options { get; internal set; }
        public string SourceTag => Options != null ? Options.SourceTag : "";
        public float Elapsed => Time.time - StartTime;
        /// <summary>Seconds left, or -1 when there is no limit.</summary>
        public float Remaining => Options != null && Options.MaxDuration > 0f ? Mathf.Max(0f, Options.MaxDuration - Elapsed) : -1f;
        public float FormHealth { get; internal set; }
        public float FormHealthMax { get; internal set; }
        public IReadOnlyList<PossessAttackInfo> Attacks => AttackList;
        public int MainIndex { get; internal set; } = -1;
        public int SpecialIndex { get; internal set; } = -1;
        public int CurrentIndex { get; internal set; } = -1;

        /// <summary>Ends this possession (a no-op once it has ended).</summary>
        public void Cancel()
        {
            if (!Ended) PossessionApi.End(this, PossessEndReason.Released);
        }
    }

    /// <summary>
    /// Server-side check for a class grant: return null to allow, or a refusal reason. Runs on the server with the
    /// requester's player id, the target's ZDO, and the target instance where the server has it loaded (else null).
    /// </summary>
    public delegate string PossessValidator(long playerId, ZDO target, Character targetOrNull);

    /// <summary>
    /// Core's creature possession (Fire 10-07). One possession per client. The engine is [ghost]'s shared
    /// Shared\FiresPossession sources (the same ones Valcast compiles internally); this facade adds the server grant
    /// (admin gate or a class validator, ownership and the "valcast_possessed" key in one step), the form health pool,
    /// damage scaling and the portal/logout release. Every event is static and safe to subscribe at any time.
    /// </summary>
    public static class PossessionApi
    {
        /// <summary>The local player's possession, or null.</summary>
        public static PossessHandle Current { get; internal set; }

        public static event Action<PossessHandle> Started;
        public static event Action<PossessHandle, PossessEndReason> Ended;
        /// <summary>Alt + mouse wheel changed the selected attack.</summary>
        public static event Action<PossessHandle> AttackChanged;

        public static bool IsPossessing(Player p, out Character creature)
        {
            var h = Current;
            if (h != null && !h.Ended && p != null && h.Player == p && h.Creature != null)
            {
                creature = h.Creature;
                return true;
            }
            creature = null;
            return false;
        }

        /// <summary>Checks the general rules and the options' filter locally, with no side effects.</summary>
        public static bool CanPossess(Character target, PossessOptions options, out string reason)
            => PossessionRules.CanPossessLocal(Player.m_localPlayer, target, options, out reason);

        /// <summary>
        /// Asks the server for the creature (a class grant: the server runs the validator registered for the options'
        /// SourceTag). Returns the handle at once; Started fires when the server has granted it and ownership arrived, or
        /// Ended(Revoked) fires when it was refused.
        /// </summary>
        public static PossessHandle Begin(Character target, PossessOptions options)
            => PossessionClient.Request(target, options ?? new PossessOptions());

        public static void End(PossessHandle handle, PossessEndReason reason)
        {
            if (handle == null || handle.Ended) return;
            PossessionClient.EndLocal(handle, reason);
        }

        /// <summary>
        /// Registers the server-side check for a SourceTag (RPGClasses: "rpg-druid", "rpg-necro"). Register on every
        /// machine at plugin start; only the server's copy is used. "core-admin" is Core's own admin gate.
        /// </summary>
        public static void RegisterValidator(string sourceTag, PossessValidator validator)
            => PossessionServer.RegisterValidator(sourceTag, validator);

        internal static void RaiseStarted(PossessHandle h) => Safe(() => Started?.Invoke(h));
        internal static void RaiseEnded(PossessHandle h, PossessEndReason r) => Safe(() => Ended?.Invoke(h, r));
        internal static void RaiseAttackChanged(PossessHandle h) => Safe(() => AttackChanged?.Invoke(h));

        private static void Safe(Action a)
        {
            try { a(); }
            catch (Exception e) { FiresCore.Logging.FiresLogger.LogWarning("[Possession] a subscriber threw: " + e.Message); }
        }
    }
}
