using System;
using System.Collections.Generic;
using FiresCore.IO;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Movement
{
    // Plays the climb clips on every peer (route A of CLIMBING.md §1). The clips are Quaternius' Universal Animation Library
    // (CC0), imported as Humanoid and render-checked on Player.prefab's rig (Tools\ClimbClips), shipped in the embedded bundle
    // 'firesclimb'.
    //
    // An AnimatorOverrideController can only swap the clips of states that exist, so the climb borrows player states vanilla
    // never reaches while a body climbs: five that hold until vanilla's emote_stop trigger (the loops) and five one-shots that
    // return to Movement by themselves (enter, top-outs, exit). Overriding a clip changes it everywhere, which would turn a
    // player's own kneel or wave into a climb, so a body runs the override controller only while it climbs and gets its own
    // controller back afterwards. Each swap restarts the animator's state machine and resets its parameters, so the values
    // ZSyncAnimation set are copied across both swaps.
    //
    // Driven from ZSyncAnimation.CustomFixedUpdate, which runs on every peer for every animated character: the owner and the
    // remote peers read the same two ints (Climbing.cs's ClimbZdo.State and ClimbZdo.Clip, written by the climbing body's
    // owner) and each drives its own local animator. Nothing new is synced.
    internal static class ClimbAnimator
    {
        private const string Tag = "[Climb] ";
        private const string BundleName = "firesclimb";
        private const float CrossFadeSeconds = 0.15f;
        private const float RestoreAfterSeconds = 0.35f;   // the cross-fade back to Movement finishes before the controller swap
        private const string MovementState = "Movement";

        // The climb clip each borrowed state carries: (clip, the vanilla state, its own clip's name, the bundle clip's name).
        private static readonly (ClimbClip clip, string state, string original, string climb)[] Slots =
        {
            (ClimbClip.UpLoop, "Kneel Loop", "Kneel Loop", "Climb_Up_Loop"),
            (ClimbClip.IdleLoop, "Relax Loop", "Lie Down", "Climb_Idle_Loop"),
            (ClimbClip.DownLoop, "Rest Loop", "Lie Down 2", "Climb_Down_Loop"),
            (ClimbClip.LeftLoop, "Dance", "Dance", "Climb_Left_Loop"),
            (ClimbClip.RightLoop, "Emote_vibe", "Lisa Dance", "Climb_Right_Loop"),
            (ClimbClip.Enter, "Emote_wave", "Wave", "Climb_Enter"),
            (ClimbClip.Ledge, "Emote_bow", "Bow", "ClimbLedge"),
            (ClimbClip.Up1m, "Emote_point", "Point", "ClimbUp_1m"),
            (ClimbClip.Up2m, "Emote_cheer", "Cheer", "ClimbUp_2m"),
            (ClimbClip.Exit, "Emote_shrug", "Shrug", "Climb_Exit"),
        };

        private static readonly int MovementHash = Animator.StringToHash(MovementState);
        private static readonly Dictionary<ClimbClip, int> s_stateHash = new Dictionary<ClimbClip, int>();

        private sealed class Body
        {
            internal RuntimeAnimatorController Original;
            internal int Clip = -1;
            internal float RestoreAt = -1f;   // > 0: back at Movement, waiting to give the original controller back
        }

        private static readonly Dictionary<Animator, Body> s_bodies = new Dictionary<Animator, Body>();
        private static readonly Dictionary<RuntimeAnimatorController, AnimatorOverrideController> s_overrides =
            new Dictionary<RuntimeAnimatorController, AnimatorOverrideController>();
        private static readonly Dictionary<RuntimeAnimatorController, bool> s_fits = new Dictionary<RuntimeAnimatorController, bool>();
        private static Dictionary<string, AnimationClip> s_clips;
        private static bool s_loadFailed;
        private static bool? s_noGraphics;

        // A dedicated server or a -nographics client animates nothing anyone sees.
        private static bool NoGraphics => s_noGraphics ?? (s_noGraphics = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null).Value;

        [HarmonyPatch(typeof(ZSyncAnimation), nameof(ZSyncAnimation.CustomFixedUpdate))]
        private static class Hook
        {
            private static void Postfix(ZNetView ___m_nview, Animator ___m_animator)
            {
                try { Tick(___m_nview, ___m_animator); }
                catch (Exception ex) { FiresLogger.LogWarning($"{Tag}animator update failed: {ex.Message}"); }
            }
        }

        private static void Tick(ZNetView view, Animator animator)
        {
            if (animator == null || view == null || !view.IsValid() || NoGraphics) return;
            ZDO zdo = view.GetZDO();
            int state = zdo.GetInt(ClimbZdo.StateHash, 0);
            s_bodies.TryGetValue(animator, out Body body);
            if (state == 0 && body == null) return;   // the common case: not climbing and never was

            if (state != 0)
            {
                if (body == null || body.RestoreAt > 0f)
                {
                    if (body == null && !Begin(animator, out body)) return;
                    body.RestoreAt = -1f;
                }
                int clip = zdo.GetInt(ClimbZdo.ClipHash, 0);
                if (clip != body.Clip)
                {
                    body.Clip = clip;
                    if (s_stateHash.TryGetValue((ClimbClip)clip, out int hash)) animator.CrossFadeInFixedTime(hash, CrossFadeSeconds, 0);
                }
                return;
            }

            // Back to Idle: fade to Movement on the override, then give the original controller back once the fade is done.
            if (body.RestoreAt < 0f)
            {
                body.RestoreAt = Time.time + RestoreAfterSeconds;
                body.Clip = -1;
                animator.CrossFadeInFixedTime(MovementHash, CrossFadeSeconds, 0);
                return;
            }
            if (Time.time < body.RestoreAt) return;
            SwapKeepingParameters(animator, body.Original);
            s_bodies.Remove(animator);
        }

        // Puts the body on the override controller. False (and logged once per controller) when it can't climb visibly.
        private static bool Begin(Animator animator, out Body body)
        {
            body = null;
            RuntimeAnimatorController original = animator.runtimeAnimatorController;
            if (original == null || original is AnimatorOverrideController) return false;
            if (!Fits(animator, original)) return false;
            AnimatorOverrideController overrides = OverrideFor(original);
            if (overrides == null) return false;
            body = new Body { Original = original };
            s_bodies[animator] = body;
            SwapKeepingParameters(animator, overrides);
            if (s_bodies.Count > 64) Prune();
            return true;
        }

        // Whether the controller has every borrowed state on layer 0 (checked once per controller).
        private static bool Fits(Animator animator, RuntimeAnimatorController controller)
        {
            if (s_fits.TryGetValue(controller, out bool fits)) return fits;
            fits = animator.isHuman;
            foreach (var slot in Slots)
            {
                int hash = Animator.StringToHash(slot.state);
                s_stateHash[slot.clip] = hash;
                if (!animator.HasState(0, hash)) fits = false;
            }
            if (!animator.HasState(0, MovementHash)) fits = false;
            s_fits[controller] = fits;
            if (!fits) FiresLogger.LogInfo($"{Tag}'{controller.name}' lacks the player states the climb clips borrow; its bodies climb without them");
            return fits;
        }

        private static AnimatorOverrideController OverrideFor(RuntimeAnimatorController original)
        {
            if (s_overrides.TryGetValue(original, out AnimatorOverrideController made)) return made;
            Dictionary<string, AnimationClip> clips = Clips();
            if (clips == null) return null;
            var overrides = new AnimatorOverrideController(original) { name = original.name + " (climb)" };
            var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>(overrides.overridesCount);
            overrides.GetOverrides(pairs);
            int replaced = 0;
            for (int i = 0; i < pairs.Count; i++)
            {
                AnimationClip own = pairs[i].Key;
                if (own == null) continue;
                foreach (var slot in Slots)
                {
                    if (own.name != slot.original || !clips.TryGetValue(slot.climb, out AnimationClip climb)) continue;
                    pairs[i] = new KeyValuePair<AnimationClip, AnimationClip>(own, climb);
                    replaced++;
                    break;
                }
            }
            overrides.ApplyOverrides(pairs);
            s_overrides[original] = overrides;
            FiresLogger.LogInfo($"{Tag}climb clips on '{original.name}': {replaced} of {Slots.Length} borrowed clips replaced");
            return overrides;
        }

        private static Dictionary<string, AnimationClip> Clips()
        {
            if (s_clips != null || s_loadFailed) return s_clips;
            var assembly = typeof(ClimbAnimator).Assembly;
            string resource = null;
            foreach (string name in assembly.GetManifestResourceNames())
                if (name == BundleName || name.EndsWith("." + BundleName, StringComparison.Ordinal)) resource = name;
            AssetBundle bundle = resource != null ? BundleCache.LoadEmbedded(assembly, resource) : null;
            if (bundle == null)
            {
                s_loadFailed = true;
                FiresLogger.LogWarning($"{Tag}this Core carries no '{BundleName}' bundle; climbing bodies keep their placeholder animation");
                return null;
            }
            s_clips = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
            foreach (AnimationClip clip in bundle.LoadAllAssets<AnimationClip>()) s_clips[clip.name] = clip;
            FiresLogger.LogInfo($"{Tag}loaded {s_clips.Count} climb clip(s) from '{BundleName}'");
            return s_clips;
        }

        // Assigning a controller restarts the state machine and resets every parameter; ZSyncAnimation only re-sends the ones
        // that change, so the current values are copied over (triggers are one-shot and not kept).
        private static void SwapKeepingParameters(Animator animator, RuntimeAnimatorController controller)
        {
            AnimatorControllerParameter[] parameters = animator.parameters;
            var floats = new List<(int, float)>();
            var ints = new List<(int, int)>();
            var bools = new List<(int, bool)>();
            foreach (AnimatorControllerParameter p in parameters)
            {
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float: floats.Add((p.nameHash, animator.GetFloat(p.nameHash))); break;
                    case AnimatorControllerParameterType.Int: ints.Add((p.nameHash, animator.GetInteger(p.nameHash))); break;
                    case AnimatorControllerParameterType.Bool: bools.Add((p.nameHash, animator.GetBool(p.nameHash))); break;
                }
            }
            animator.runtimeAnimatorController = controller;
            foreach (var (hash, value) in floats) animator.SetFloat(hash, value);
            foreach (var (hash, value) in ints) animator.SetInteger(hash, value);
            foreach (var (hash, value) in bools) animator.SetBool(hash, value);
        }

        // Bodies whose animator was destroyed mid-climb (a logout, a zone unload) leave nothing to restore.
        private static void Prune()
        {
            var dead = new List<Animator>();
            foreach (Animator animator in s_bodies.Keys) if (animator == null) dead.Add(animator);
            foreach (Animator animator in dead) s_bodies.Remove(animator);
        }
    }
}
