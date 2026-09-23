using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc.Animation
{
    /// <summary>OneShot returns to Movement on its own; Loop plays until emote_stop; Hold is a Bool held true.</summary>
    public enum PlayerAnimKind { OneShot, Loop, Hold }

    [Flags]
    public enum PlayerAnimUse
    {
        None = 0,
        Greet = 1,
        Bye = 2,
        Interact = 4,
        Idle = 8,
        CompanionIdle = 16,
    }

    public sealed class PlayerAnimation
    {
        public readonly string Key;
        public readonly string Label;
        public readonly string Parameter;
        public readonly PlayerAnimKind Kind;
        public readonly float Seconds;
        public readonly PlayerAnimUse Uses;
        public readonly int Hash;

        public PlayerAnimation(string key, string label, string parameter, PlayerAnimKind kind, float seconds, PlayerAnimUse uses)
        {
            Key = key;
            Label = label;
            Parameter = parameter;
            Kind = kind;
            Seconds = seconds;
            Uses = uses;
            Hash = ZSyncAnimation.GetHash(parameter);
        }

        public bool IsBool => Kind == PlayerAnimKind.Hold;
        public bool IsEmote => Parameter.StartsWith(PlayerAnimationCatalog.EmotePrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every animation a player-rig NPC can play for idles, greetings and reactions, read from the 1.0
    /// Player_animator controller: parameter and type, how the state ends, and its time in seconds
    /// (clip length x exit time / state speed; for Loop and Hold, the intro plus one cycle).
    /// Keys are what NPC data stores: the vanilla emote name in lower case, or the raw parameter.
    /// </summary>
    public static class PlayerAnimationCatalog
    {
        public const string EmotePrefix = "emote_";
        public const string StopTrigger = "emote_stop";
        public const string EmoteStateTag = "emote";

        /// <summary>The station work pose is an Int: CraftingStation.m_useAnimation (1 workbench-type, 2 forge-type,
        /// 3 cauldron-type), 0 to stop. The workbench pose also needs forward_speed below 0.1.</summary>
        public const string CraftingParameter = "crafting";
        public const int NoCrafting = 0;
        public const int WorkbenchCrafting = 1;
        private static readonly int CraftingHash = ZSyncAnimation.GetHash(CraftingParameter);

        private const PlayerAnimUse Social = PlayerAnimUse.Greet | PlayerAnimUse.Interact | PlayerAnimUse.CompanionIdle;
        private const PlayerAnimUse Friendly = PlayerAnimUse.Greet | PlayerAnimUse.Bye | PlayerAnimUse.Interact | PlayerAnimUse.CompanionIdle;
        private const PlayerAnimUse Ambient = PlayerAnimUse.Interact | PlayerAnimUse.Idle | PlayerAnimUse.CompanionIdle;
        private const PlayerAnimUse Pose = PlayerAnimUse.Idle | PlayerAnimUse.CompanionIdle;

        private static readonly int StopTriggerHash = ZSyncAnimation.GetHash(StopTrigger);

        public static readonly IReadOnlyList<PlayerAnimation> All = new[]
        {
            Emote("wave", "Wave", 2.33f, Friendly),
            Emote("bow", "Bow", 3.21f, Friendly),
            Emote("blowkiss", "Blow kiss", 1.75f, PlayerAnimUse.Greet | PlayerAnimUse.Bye | PlayerAnimUse.CompanionIdle),
            Emote("loveyou", "Love you", 2.54f, PlayerAnimUse.Greet | PlayerAnimUse.Bye | PlayerAnimUse.CompanionIdle),
            Emote("thumbsup", "Thumbs up", 1.13f, Friendly),
            Emote("cheer", "Cheer", 2.21f, Friendly | PlayerAnimUse.Idle),
            Emote("toast", "Toast", 2.42f, Friendly | PlayerAnimUse.Idle),
            Emote("comehere", "Come here", 2.07f, Social),
            Emote("point", "Point", 2.00f, Social | PlayerAnimUse.Idle),
            Emote("challenge", "Challenge", 2.98f, Social | PlayerAnimUse.Idle),
            Emote("laugh", "Laugh", 2.78f, Social | PlayerAnimUse.Idle),
            Emote("flex", "Flex", 2.51f, Social | PlayerAnimUse.Idle),
            Emote("shrug", "Shrug", 2.33f, PlayerAnimUse.Bye | Ambient),
            Emote("nonono", "No no no", 1.67f, PlayerAnimUse.Interact | PlayerAnimUse.CompanionIdle),
            Emote("cry", "Cry", 3.75f, PlayerAnimUse.Bye | PlayerAnimUse.Idle | PlayerAnimUse.CompanionIdle),
            Emote("despair", "Despair", 6.33f, PlayerAnimUse.Bye | PlayerAnimUse.Idle | PlayerAnimUse.CompanionIdle),
            Emote("cower", "Cower", 2.42f, PlayerAnimUse.Interact | PlayerAnimUse.Idle),
            Emote("roar", "Roar", 1.92f, Ambient),
            Emote("drink", "Drink", 4.28f, PlayerAnimUse.Interact | PlayerAnimUse.Idle),
            Raw("eat", "Eat", PlayerAnimKind.OneShot, 1.31f, PlayerAnimUse.Interact | PlayerAnimUse.Idle),
            Raw("interact", "Use gesture", PlayerAnimKind.OneShot, 0.30f, PlayerAnimUse.Interact),
            Raw("place_feast", "Set something down", PlayerAnimKind.OneShot, 0.90f, PlayerAnimUse.Interact | PlayerAnimUse.Idle),
            Raw("gpower", "Raise arms", PlayerAnimKind.OneShot, 2.08f, PlayerAnimUse.Interact | PlayerAnimUse.Idle),

            Emote("dance", "Dance", PlayerAnimKind.Loop, 35.17f, Pose),
            Emote("vibe", "Vibe", PlayerAnimKind.Loop, 5.13f, Pose),
            Emote("headbang", "Headbang", PlayerAnimKind.Loop, 1.33f, Pose),
            Emote("kneel", "Kneel", PlayerAnimKind.Loop, 2.45f, Pose),
            Emote("rest", "Lie down (rest)", PlayerAnimKind.Loop, 4.82f, Pose),
            Emote("relax", "Lie down (relax)", PlayerAnimKind.Loop, 4.28f, PlayerAnimUse.Idle),
            Emote("sit", "Sit on the ground", PlayerAnimKind.Hold, 13.13f, Pose),

            Raw("attach_chair", "Sit (chair pose)", PlayerAnimKind.Hold, 16.70f, PlayerAnimUse.Idle),
            Raw("attach_throne", "Sit (throne pose)", PlayerAnimKind.Hold, 45.10f, PlayerAnimUse.Idle),
            Raw("attach_divan", "Lie on a divan", PlayerAnimKind.Hold, 2.67f, PlayerAnimUse.Idle),
            Raw("attach_bed", "Sleep (bed pose)", PlayerAnimKind.Hold, 2.40f, PlayerAnimUse.Idle),
            Raw("attach_sitship", "Sit (ship bench)", PlayerAnimKind.Hold, 16.67f, PlayerAnimUse.None),
            Raw("attach_mast", "Hold the mast", PlayerAnimKind.Hold, 0f, PlayerAnimUse.None),
            Raw("attach_dragon", "Hold the dragon head", PlayerAnimKind.Hold, 0f, PlayerAnimUse.None),
            Raw("attach_lox", "Ride a lox", PlayerAnimKind.Hold, 0f, PlayerAnimUse.None),
            Raw("attach_asksvin", "Ride an asksvin", PlayerAnimKind.Hold, 0f, PlayerAnimUse.None),
            Raw("attach_moose", "Ride a moose", PlayerAnimKind.Hold, 0f, PlayerAnimUse.None),
        };

        private static readonly Dictionary<PlayerAnimUse, List<PlayerAnimation>> ByUse = new Dictionary<PlayerAnimUse, List<PlayerAnimation>>();
        private static readonly Dictionary<RuntimeAnimatorController, Dictionary<int, AnimatorControllerParameterType>> ParamsByController =
            new Dictionary<RuntimeAnimatorController, Dictionary<int, AnimatorControllerParameterType>>();

        private static PlayerAnimation Emote(string key, string label, float seconds, PlayerAnimUse uses)
            => new PlayerAnimation(key, label, EmotePrefix + key, PlayerAnimKind.OneShot, seconds, uses);

        private static PlayerAnimation Emote(string key, string label, PlayerAnimKind kind, float seconds, PlayerAnimUse uses)
            => new PlayerAnimation(key, label, EmotePrefix + key, kind, seconds, uses);

        private static PlayerAnimation Raw(string parameter, string label, PlayerAnimKind kind, float seconds, PlayerAnimUse uses)
            => new PlayerAnimation(parameter, label, parameter, kind, seconds, uses);

        public static IReadOnlyList<PlayerAnimation> For(PlayerAnimUse use)
        {
            if (!ByUse.TryGetValue(use, out var list))
            {
                list = new List<PlayerAnimation>();
                foreach (var anim in All)
                    if ((anim.Uses & use) != 0) list.Add(anim);
                ByUse[use] = list;
            }
            return list;
        }

        /// <summary>Resolves a stored value: key, label, parameter, or a vanilla Emotes enum name, in any case.</summary>
        public static PlayerAnimation Find(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string v = value.Trim();
            foreach (var anim in All)
            {
                if (string.Equals(anim.Key, v, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(anim.Parameter, v, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(anim.Label, v, StringComparison.OrdinalIgnoreCase))
                    return anim;
            }
            return null;
        }

        public static bool Has(Animator animator, PlayerAnimation anim)
        {
            if (animator == null || anim == null) return false;
            var parameters = ParametersOf(animator);
            if (parameters == null || !parameters.TryGetValue(anim.Hash, out var type)) return false;
            return anim.IsBool ? type == AnimatorControllerParameterType.Bool : type == AnimatorControllerParameterType.Trigger;
        }

        /// <summary>Starts the animation the way vanilla Player.UpdateEmote does: a pending emote_stop is cleared first,
        /// triggers go through ZSyncAnimation so every client plays them, holds set their Bool.</summary>
        public static bool Play(ZSyncAnimation zanim, Animator animator, PlayerAnimation anim)
        {
            animator = Driven(zanim, animator);
            if (!Has(animator, anim)) return false;
            animator.ResetTrigger(StopTriggerHash);
            if (anim.IsBool)
            {
                if (zanim != null) zanim.SetBool(anim.Hash, true);
                else animator.SetBool(anim.Hash, true);
            }
            else if (zanim != null) zanim.SetTrigger(anim.Parameter);
            else animator.SetTrigger(anim.Hash);
            return true;
        }

        public static void Stop(ZSyncAnimation zanim, Animator animator, PlayerAnimation anim)
        {
            animator = Driven(zanim, animator);
            if (!Has(animator, anim)) return;
            if (anim.IsBool)
            {
                if (zanim != null) zanim.SetBool(anim.Hash, false);
                else animator.SetBool(anim.Hash, false);
            }
            else if (InEmoteState(animator)) SendStop(zanim, animator);
        }

        /// <summary>Ends emotes only (the sit hold goes false, an emote state gets emote_stop); seat poses stay.</summary>
        public static void StopEmotes(ZSyncAnimation zanim, Animator animator) => Release(zanim, animator, true);

        /// <summary>Releases every hold, seat poses included, and ends any emote state.</summary>
        public static void StopAll(ZSyncAnimation zanim, Animator animator) => Release(zanim, animator, false);

        /// <summary>True while an emote plays: an emote-tagged state, or an emote hold (sit) set.</summary>
        public static bool IsEmoting(Animator animator)
        {
            if (InEmoteState(animator)) return true;
            foreach (var anim in All)
                if (anim.IsBool && anim.IsEmote && Has(animator, anim) && animator.GetBool(anim.Hash)) return true;
            return false;
        }

        public static bool IsEmoting(ZSyncAnimation zanim, Animator animator) => IsEmoting(Driven(zanim, animator));

        /// <summary>emote_stop is only sent from an emote state: a trigger nothing consumes stays set and cuts the
        /// next emote short the moment it starts.</summary>
        private static void Release(ZSyncAnimation zanim, Animator animator, bool emotesOnly)
        {
            animator = Driven(zanim, animator);
            if (animator == null) return;
            foreach (var anim in All)
            {
                if (!anim.IsBool || (emotesOnly && !anim.IsEmote) || !Has(animator, anim) || !animator.GetBool(anim.Hash)) continue;
                if (zanim != null) zanim.SetBool(anim.Hash, false);
                else animator.SetBool(anim.Hash, false);
            }
            if (InEmoteState(animator)) SendStop(zanim, animator);
        }

        /// <summary>Holds (or with <see cref="NoCrafting"/> ends) a station work pose, as vanilla Player.UpdateStations does.</summary>
        public static void SetCrafting(ZSyncAnimation zanim, Animator animator, int useAnimation)
        {
            if (zanim != null) zanim.SetInt(CraftingHash, useAnimation);
            else if (animator != null) animator.SetInteger(CraftingHash, useAnimation);
        }

        public static int CraftingFor(CraftingStation station)
            => station != null && station.m_useAnimation > NoCrafting ? station.m_useAnimation : WorkbenchCrafting;

        public static bool InEmoteState(Animator animator)
        {
            if (animator == null || !animator.isActiveAndEnabled) return false;
            if (animator.GetCurrentAnimatorStateInfo(0).IsTag(EmoteStateTag)) return true;
            return animator.IsInTransition(0) && animator.GetNextAnimatorStateInfo(0).IsTag(EmoteStateTag);
        }

        /// <summary>State is read from the animator ZSyncAnimation writes to. A model override rebinds it to the new rig
        /// while behaviours keep the Animator they cached at Awake.</summary>
        private static Animator Driven(ZSyncAnimation zanim, Animator animator)
            => zanim != null && zanim.m_animator != null ? zanim.m_animator : animator;

        private static void SendStop(ZSyncAnimation zanim, Animator animator)
        {
            if (zanim != null) zanim.SetTrigger(StopTrigger);
            else animator.SetTrigger(StopTriggerHash);
        }

        private static Dictionary<int, AnimatorControllerParameterType> ParametersOf(Animator animator)
        {
            var controller = animator.runtimeAnimatorController;
            if (controller == null) return null;
            if (ParamsByController.TryGetValue(controller, out var parameters)) return parameters;
            var list = animator.parameters;
            if (list == null || list.Length == 0) return null;
            parameters = new Dictionary<int, AnimatorControllerParameterType>(list.Length);
            foreach (var p in list)
                parameters[p.nameHash] = p.type;
            ParamsByController[controller] = parameters;
            return parameters;
        }
    }
}
