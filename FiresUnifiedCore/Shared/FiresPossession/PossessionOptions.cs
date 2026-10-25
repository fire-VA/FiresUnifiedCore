using System;
using UnityEngine;

namespace FiresPossession
{
    /// <summary>How the possessor's own body is kept while possessing.</summary>
#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    enum BodyStyle
    {
        /// <summary>The body stays where it stood, with no controls; the creature's own body takes the keys (Valcast "Direct", Core admin, Necromancer).</summary>
        Frozen,
        /// <summary>The body is hidden and carried inside the creature, driving it with the whole player stack (Valcast "Proxy", Druid Wildshape). Humanoid-rig creatures only; else Frozen.</summary>
        Hidden
    }

    /// <summary>Why a possession ended: the reason the Ended event carries (lower case; the detail is in PossessionEngine.LastEndDetail).</summary>
#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    static class PossessReasons
    {
        public const string Released = "released";
        public const string Death = "death";
        public const string Range = "range";
        public const string Duration = "duration";
        public const string Logout = "logout";
        public const string Gone = "gone";
        public const string Revoked = "revoked";
        public const string Error = "error";
    }

    /// <summary>LMB fires Main, MMB fires the picked Special; Other = the rest of the creature's attacks (Alt + the wheel picks one).</summary>
#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    enum PossessAttackKind { Main, Special, Other }

    /// <summary>One of the possessed creature's attacks.</summary>
#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    struct PossessAttack
    {
        public string Name;
        public PossessAttackKind Kind;
        public bool Ranged;
    }

    /// <summary>What a caller asks for when it begins a possession. Defaults = Valcast's possession.</summary>
#if FIRES_POSSESSION_PUBLIC
    public
#else
    internal
#endif
    sealed class PossessOptions
    {
        public BodyStyle Body = BodyStyle.Frozen;
        /// <summary>The possessor's body takes no damage, stagger, knockback or push, and no AI picks it as a target.</summary>
        public bool PlayerInvulnerable;
        /// <summary>A Frozen body is pinned where it stood (nothing moves it) and plays the channel (ChannelFx*, ChannelPose, ChannelHover).</summary>
        public bool ChannelLock;
        /// <summary>Seconds; 0 = no limit (ends with "duration").</summary>
        public float MaxDuration;
        /// <summary>The body-to-creature leash in metres; 0 = none (ends with "range").</summary>
        public float MaxRange = 60f;
        /// <summary>The creature is put back where it was taken when the possession ends (NPC rule).</summary>
        public bool ReturnToAnchor;
        /// <summary>The creature's ZDO ownership goes back to its previous owner at the end.</summary>
        public bool ReturnOwnership = true;
        /// <summary>Wildshape: the creature is the form's networked body and is destroyed at the end (no return); a Hidden body comes out where the form stands.</summary>
        public bool ConsumeTarget;
        /// <summary>Hits the creature lands are credited to the possessor (attacker = the player).</summary>
        public bool HitsAsPossessor = true;
        /// <summary>
        /// Ghost, God and fly (no clip): a Frozen body gets vanilla's own
        /// devcommands flags while possessing (ghost: no AI targets it; god: no damage; debug fly: no collision, no gravity, and vanilla's
        /// RPC_Damage returns before any damage, stagger, push or status effect), restored exactly at the end. Note: vanilla marks a
        /// creature "cheated" when a player in god / ghost / fly damages it, so with HitsAsPossessor on, kills made while possessing
        /// drop cheated items.
        /// </summary>
        public bool BodyUntouchable = true;
        /// <summary>false = AUTO: the creature's own AI keeps driving it (no ownership claim, no key); the body is still frozen and the leash still holds.</summary>
        public bool Driven = true;
        /// <summary>null (or returning null) = any creature the engine's own rules allow; a non-null string refuses with that reason.</summary>
        public Func<Character, string> Filter;
        /// <summary>Checked each frame; false ends the possession ("released"), e.g. the Necromancer's eitr.</summary>
        public Func<bool> ContinueWhile;
        /// <summary>The channelling body's levitation in metres (ChannelLock); 0 = none.</summary>
        public float ChannelHover;
        /// <summary>Seconds to rise to ChannelHover; bob amplitude (m) and period (s) while up; seconds to come back down after the end.</summary>
        public float HoverRise = 0.6f, HoverBob = 0.15f, HoverPeriod = 2.5f, HoverDescent = 0.6f;
        public Action<Player> ChannelFxStart, ChannelFxStop;
        /// <summary>Called in LateUpdate after the animator while channelling, for bone overrides (arms half-raised, head back).</summary>
        public Action<Player> ChannelPose;
        /// <summary>"valcast", "core-admin", "rpg-druid", ... (logs).</summary>
        public string SourceTag = "";
        public IPossessionHost Host;
        /// <summary>null = VanillaPossessionInput (the game's keys).</summary>
        public IPossessionInput Input;
    }
}
