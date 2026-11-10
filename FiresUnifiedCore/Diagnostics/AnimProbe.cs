using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Diagnostics
{
    /// <summary>
    /// fires_anim_probe [radius] (diagnoses characters stuck in an animation, e.g. climbing): what each character near the local
    /// player is being POSED with on this client. One line per character: its controller (and what an override wraps), the layer-0 state and
    /// the clips playing in it, every filled override slot (original -> clip), the crouching bool, and the pose flags on its ZDO (VikHavn
    /// prone, Core prone, companion prone, Core climb state + clip, emote). Read-only. Lines go to the console and the log (Debug; at Info
    /// too while Core's Debug lines are off, so the evidence reaches LogOutput.log).
    /// </summary>
    internal static class AnimProbe
    {
        private const string Command = "fires_anim_probe";
        private const string Tag = "[AnimProbe] ";
        private const float DefaultRadius = 20f;

        private static readonly int VikProne = "VikHavnCombat_Prone".GetStableHashCode();
        private static readonly int CoreProne = "FiresCore_Prone".GetStableHashCode();
        private static readonly int CompanionProne = "FiresCompanion_Prone".GetStableHashCode();
        private static readonly int Crouching = ZSyncAnimation.GetHash("crouching");
        private static readonly AccessTools.FieldRef<Character, ZSyncAnimation> ZAnim = AccessTools.FieldRefAccess<Character, ZSyncAnimation>("m_zanim");
        private static readonly AccessTools.FieldRef<Character, ZNetView> NView = AccessTools.FieldRefAccess<Character, ZNetView>("m_nview");

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class AnimProbe_InitTerminal
        {
            private static bool s_done;
            private static void Postfix()
            {
                if (s_done) return;
                s_done = true;
                new Terminal.ConsoleCommand(Command,
                    "[FiresUnifiedCore] fires_anim_probe [radius] - DIAGNOSTIC (admin): the animator state, override clips and pose flags of every character within radius (default 20 m) on this client.",
                    args =>
                    {
                        float radius = args.Args.Length > 1 && float.TryParse(args.Args[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float r) ? r : DefaultRadius;
                        foreach (string line in Probe(radius)) { args.Context?.AddString(line); Write(line); }
                    }, isCheat: true);
            }
        }

        private static void Write(string line)
        {
            FiresCore.Logging.FiresLogger.LogDebug(line);
            if (!FiresCore.Logging.FiresLogger.DebugEnabled) FiresCore.Logging.FiresLogger.LogInfo(line);
        }

        internal static List<string> Probe(float radius)
        {
            var lines = new List<string>();
            Player me = Player.m_localPlayer;
            if (me == null) { lines.Add(Tag + "no local player"); return lines; }
            var near = Character.GetAllCharacters().Where(c => c != null && Vector3.Distance(c.transform.position, me.transform.position) <= radius)
                .OrderBy(c => Vector3.Distance(c.transform.position, me.transform.position)).ToList();
            lines.Add($"{Tag}{near.Count} character(s) within {radius:0} m of {me.GetPlayerName()} at {Time.time:0.0} s");
            foreach (Character c in near)
            {
                try { lines.Add(Tag + Describe(c, me)); }
                catch (Exception ex) { lines.Add($"{Tag}{c.name}: probe failed: {ex.Message}"); }
            }
            return lines;
        }

        private static string Describe(Character c, Player me)
        {
            string who = c is Player p ? $"player '{p.GetPlayerName()}'" : $"'{c.m_name}' ({Utils.GetPrefabName(c.gameObject)})";
            ZNetView view = NView(c);
            ZDO zdo = view != null && view.IsValid() ? view.GetZDO() : null;
            string owner = view != null && view.IsValid() ? (view.IsOwner() ? "owned here" : "remote") : "no view";
            ZSyncAnimation zanim = ZAnim(c);
            Animator animator = zanim != null ? zanim.GetComponentInChildren<Animator>() : c.GetComponentInChildren<Animator>();
            string anim = animator == null ? "no animator" : DescribeAnimator(animator);
            string flags = zdo == null ? "no ZDO" :
                $"VikHavn prone {zdo.GetBool(VikProne)}, Core prone {zdo.GetBool(CoreProne)}, companion prone {zdo.GetBool(CompanionProne)}, " +
                $"climb state {zdo.GetInt(FiresCore.Movement.ClimbZdo.StateHash, 0)} clip {zdo.GetInt(FiresCore.Movement.ClimbZdo.ClipHash, 0)}, " +
                $"emote '{zdo.GetString(ZDOVars.s_emote, "")}'";
            return $"{who} {Vector3.Distance(c.transform.position, me.transform.position):0.0} m, {owner}: {anim}; {flags}";
        }

        private static string DescribeAnimator(Animator animator)
        {
            RuntimeAnimatorController rc = animator.runtimeAnimatorController;
            string controller = Named(rc);
            AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
            string playing = string.Join(", ", animator.GetCurrentAnimatorClipInfo(0).Select(ci => $"{(ci.clip != null ? ci.clip.name : "null")} {ci.weight:0.00}"));
            string slots = "";
            if (rc is AnimatorOverrideController aoc)
            {
                var list = new List<KeyValuePair<AnimationClip, AnimationClip>>();
                aoc.GetOverrides(list);
                var filled = list.Where(kv => kv.Key != null && kv.Value != null).Select(kv => $"{kv.Key.name}->{kv.Value.name}").ToList();
                slots = $"; {filled.Count} override slot(s){(filled.Count > 0 ? ": " + string.Join(", ", filled.Take(12)) + (filled.Count > 12 ? ", ..." : "") : "")}";
            }
            bool crouch = animator.parameters.Any(pr => pr.nameHash == Crouching) && animator.GetBool(Crouching);
            return $"controller {controller}, state {info.shortNameHash} (t {info.normalizedTime:0.00}{(animator.IsInTransition(0) ? ", in transition" : "")}), playing [{playing}], crouching {crouch}, speed {animator.speed:0.00}{slots}";
        }

        private static string Named(RuntimeAnimatorController c)
        {
            if (c == null) return "none";
            if (c is AnimatorOverrideController o) return $"override '{o.name}' over {Named(o.runtimeAnimatorController)}";
            return "'" + c.name + "'";
        }
    }
}
