using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using FiresCore.Npc.IdleBehaviors;

namespace FiresCore.Npc.Debugging
{
    /// <summary>
    /// Fire 10-24 (hand-over 57): "some companions still slide around instead of actually walking. like they get stuck in some animation
    /// state?". Every peer, every 0.5 s: a companion whose body moves faster than 0.5 m/s while its animator's forward speed stays under 0.1
    /// for more than 2 s gets one "[Anim] &lt;name&gt; sliding" Debug line (at most every 10 s each), naming who drives it: this peer owns it
    /// or not (and the ZDO owner), the active sub-behavior, the AI state and the last move-goal owner, the animator's enabled / culling /
    /// controller, the ClipSlots clip, crouch / water / emote flags. Read-only; it changes nothing.
    /// </summary>
    internal static class CompanionSlideProbe
    {
        private static readonly int s_fwd = Animator.StringToHash("forward_speed"), s_side = Animator.StringToHash("sidestep_speed"),
            s_inWater = Animator.StringToHash("inWater"), s_crouch = Animator.StringToHash("crouching"), s_emote = Animator.StringToHash("emote_oneshot");
        private sealed class Track { public Vector3 Last; public float LastAt, SlideSince = -1f, NextLog; }
        private static readonly Dictionary<CompanionController, Track> s_track = new Dictionary<CompanionController, Track>();

        private static void Tick()
        {
            float now = Time.time;
            foreach (var c in CompanionController.AllCompanions)
            {
                if (c == null) continue;
                if (!s_track.TryGetValue(c, out var t)) { s_track[c] = new Track { Last = c.transform.position, LastAt = now }; continue; }
                float dt = now - t.LastAt;
                if (dt < 0.2f) continue;
                Vector3 d = c.transform.position - t.Last; d.y = 0f;
                float speed = d.magnitude / dt;
                t.Last = c.transform.position; t.LastAt = now;
                // the DRIVING animator: Character.m_animator (ZSyncAnimation feeds it; BodyModelSwap rebinds it to a swap's model and
                // disables the original Visual animator on purpose, so the first Animator in the children can be that disabled one)
                var character = c.GetComponent<Character>();
                var anim = character != null && character.m_animator != null ? character.m_animator : c.GetComponentInChildren<Animator>();
                EnsureDriving(c, anim);
                float fwd = 0f, side = 0f;
                try { if (anim != null && anim.isActiveAndEnabled) { fwd = anim.GetFloat(s_fwd); side = anim.GetFloat(s_side); } } catch { }
                bool sliding = speed > 0.5f && Mathf.Abs(fwd) < 0.1f && Mathf.Abs(side) < 0.1f;
                if (!sliding) { t.SlideSince = -1f; continue; }
                if (t.SlideSince < 0f) t.SlideSince = now;
                if (now - t.SlideSince < 2f || now < t.NextLog) continue;
                t.NextLog = now + 10f;
                var nv = c.GetComponent<ZNetView>();
                var idle = c.GetComponent<CompanionIdleBehavior>();
                var sub = idle != null ? idle.ActiveSubBehavior : null;
                var ai = c.GetComponent<FiresCore.Npc.AI.CompanionAI>();
                bool inWater = false, crouch = false, emote = false;
                try { if (anim != null) { inWater = anim.GetBool(s_inWater); crouch = anim.GetBool(s_crouch); emote = anim.GetBool(s_emote); } } catch { }
                string clip = "";
                try { clip = FiresCore.Animation.ClipSlots.IsPlaying(c.GetComponent<Character>()) ? "a ClipSlots clip" : "no clip"; } catch { }
                FiresCore.Logging.FiresLogger.LogDebug($"[Anim] {c.companionName} sliding: {speed:0.0} m/s for {now - t.SlideSince:0} s with animator forward {fwd:0.00} side {side:0.00}; " +
                    $"this peer {(nv != null && nv.IsValid() && nv.IsOwner() ? "OWNS it" : "views it")} (ZDO owner {(nv != null && nv.IsValid() ? nv.GetZDO().GetOwner().ToString() : "?")}, " +
                    $"{(ZNet.instance != null && ZNet.instance.IsServer() ? "server" : "client")}); doing {(sub != null ? $"{sub.BehaviorName} ({(sub.IsCommandInitiated ? "command" : "idle")})" : "nothing")}, " +
                    $"AI {(ai != null ? ai.CurrentState.ToString() : "-")}, last goal for {(ai != null ? ai.IntentGoalOwner : "-")}; animator {(anim == null ? "none" : $"enabled {anim.enabled}, culling {anim.cullingMode}, controller {(anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "none")}")}, " +
                    $"{clip}, water {inWater}, crouch {crouch}, emote {emote}");
            }
            if (s_track.Count > 64) { var dead = new List<CompanionController>(); foreach (var k in s_track.Keys) if (k == null) dead.Add(k); foreach (var k in dead) s_track.Remove(k); }
        }

        // Fire 10-24 (hand-over 57, anim_slide807): companions slid with "animator enabled False" on every viewer. A companion's driving
        // animator must be enabled on every peer; the guard turns it back on and says once per companion what it found.
        private static readonly HashSet<int> s_fixedLogged = new HashSet<int>();
        private static void EnsureDriving(CompanionController c, Animator anim)
        {
            // only Character.m_animator is touched; under a swap the original Visual animator stays disabled by design (never two animators)
            if (anim == null || anim.enabled || !anim.gameObject.activeInHierarchy) return;
            var swap = c.GetComponent<FiresCore.Npc.BodyModelState>();
            bool swapped = swap != null && !string.IsNullOrEmpty(swap.Applied);
            var visual = c.transform.Find("Visual");
            bool isOriginal = visual != null && anim == visual.GetComponent<Animator>();
            string what;
            if (swapped && isOriginal)
            {
                // case B: m_animator still points at the disabled original under a swap -> repoint to the swap's model animator
                bool ok = FiresCore.Npc.BodyModelSwap.RebindModelAnimator(c.gameObject);
                what = ok ? $"case B: swapped to '{swap.Applied}' but Character.m_animator pointed at the disabled original; repointed to the swap's model animator"
                          : $"case B: swapped to '{swap.Applied}' but no model animator found to repoint to; left alone";
            }
            else
            {
                // case A (no swap, the companion's own animator) or case C (the swap's own model animator) was disabled -> enable it
                anim.enabled = true;
                what = swapped ? $"case C: the swap '{swap.Applied}' model animator itself was disabled; re-enabled" : "case A: no model swap, its own animator was disabled; re-enabled";
            }
            if (!s_fixedLogged.Add(c.GetInstanceID())) return;
            var nv = c.GetComponent<ZNetView>();
            FiresCore.Logging.FiresLogger.LogInfo($"[Anim] {c.companionName}: driving animator '{anim.gameObject.name}' (controller {(anim.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "none")}) was DISABLED on this {(ZNet.instance != null && ZNet.instance.IsServer() ? "server" : "client")} " +
                $"({(nv != null && nv.IsValid() && nv.IsOwner() ? "owner" : "viewer")}) - {what} (logged once per companion; it stays guarded)");
        }

        private sealed class Pump : MonoBehaviour
        {
            private float _next;
            private void Update() { if (Time.time < _next) return; _next = Time.time + 0.5f; try { Tick(); } catch (Exception e) { Debug.LogWarning("[Anim] probe: " + e.Message); } }
        }

        [HarmonyPatch(typeof(ZNet), "Start")]
        private static class ZNet_Start_SlideProbe
        {
            private static void Postfix()
            {
                s_track.Clear();
                if (UnityEngine.Object.FindObjectOfType<Pump>() == null) { var go = new GameObject("FiresCompanionSlideProbe"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<Pump>(); }
            }
        }
    }
}
