using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using FiresCore.Logging;
using FiresCore.Movement;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Animation
{
    public enum ClipMode { OneShot = 0, Loop = 1, Hold = 2 }

    public sealed class ClipOptions
    {
        public ClipMode Mode = ClipMode.OneShot;
        public float FadeIn = 0.12f, FadeOut = 0.15f;  // seconds
        public float Speed = 1f;                        // playback speed
        public bool RootMotion = false;                 // off unless asked; restored after
        public bool InterruptOnAttack = true;           // an attack, dodge, stagger or death cancels it
        public bool UpperBodyOnly = false;              // v2 (needs a masked layer); ignored in v1

        internal ClipOptions Copy() => (ClipOptions)MemberwiseClone();
    }

    /// <summary>
    /// Any mod plays any AnimationClip on a player-rig body (players, Core's companions), one-shot,
    /// loop or hold, seen by every peer. Built on the climb animator's method: emote states vanilla only reaches through their emote are
    /// borrowed, the body runs its own override controller only while a clip plays, and the owner writes the clip to the ZDO; every peer
    /// drives its local animator from ZSyncAnimation.CustomFixedUpdate. A peer without the clip registered shows vanilla animation.
    /// [Movement] "Clip slots": Auto (default) = active once a mod has called <see cref="Register"/>. Contract: add-only.
    /// </summary>
    public static class ClipSlots
    {
        private const string Tag = "[ClipSlots] ";

        // ZDO keys (owner-written). Add-only.
        private static readonly int ZClip = "core_clip".GetStableHashCode();
        private static readonly int ZMode = "core_clip_mode".GetStableHashCode();
        private static readonly int ZT0 = "core_clip_t0".GetStableHashCode();
        private static readonly int ZSpeed = "core_clip_speed".GetStableHashCode();
        private static readonly int ZSeq = "core_clip_seq".GetStableHashCode();
        private static readonly int ZRoot = "core_clip_root".GetStableHashCode();

        // Host states: emote states the climb does not borrow (Movement\ClimbAnimator.cs Slots). Candidates per slot because the loop
        // emotes name their state "<X> Loop" on some controllers; the first state the controller has wins. (states, the vanilla clip name)
        private static readonly (string[] states, string original)[] Hosts =
        {
            (new[] { "Emote_flex", "Flex" }, "Flex"),                                          // A (one-shot family)
            (new[] { "Emote_comehere", "ComeHere", "Emote_come_here" }, "GetOverHere"),        // B
            (new[] { "Emote_despair", "Despair Loop", "Despair" }, "Despair"),                 // A (loop family)
            (new[] { "Emote_headbang", "Headbang Loop", "Headbang" }, "Headbang"),             // B
        };
        private const string MovementState = "Movement";
        private static readonly int MovementHash = Animator.StringToHash(MovementState);
        private const float RestoreAfterSeconds = 0.35f;

        private sealed class Entry { internal string Id; internal AnimationClip Clip; internal ClipOptions Defaults; internal List<(float t, string name)> Events; }
        private static readonly Dictionary<int, Entry> s_byHash = new Dictionary<int, Entry>();
        private static readonly Dictionary<string, Entry> s_byId = new Dictionary<string, Entry>(StringComparer.Ordinal);

        private sealed class Body
        {
            internal RuntimeAnimatorController Original;
            internal AnimatorOverrideController Override;
            internal List<KeyValuePair<AnimationClip, AnimationClip>> Pairs;
            internal int[] HostHash;              // resolved state hash per host slot
            internal int Seq = -1, ClipHash, Slot = -1, StateHash;
            internal ClipMode Mode;
            internal float EndAt = -1f, RestoreAt = -1f;
            internal float OriginalSpeed = 1f;
            internal bool OriginalRoot, Applied;
            internal Character Who;
            internal string Ending;               // owner: why it ended (for Finished + the end line)
        }
        private static readonly Dictionary<Animator, Body> s_bodies = new Dictionary<Animator, Body>();
        private static readonly HashSet<string> s_logged = new HashSet<string>();

        public static event Action<Character, string> Finished;

        /// <summary>(body, clip id, event name), owner side, once per play when the clip's normalized time passes the event's.</summary>
        public static event Action<Character, string, string> ClipEvent;

        /// <summary>Adds a timed event to a registered clip (call at registration, on every peer). normalizedTime 0-1 of one pass.</summary>
        public static bool AddEvent(string id, float normalizedTime, string evtName)
        {
            if (id == null || !s_byId.TryGetValue(id, out Entry e) || string.IsNullOrEmpty(evtName)) { Once("evt:" + id + evtName, $"{Tag}AddEvent refused for '{id}' / '{evtName}' (unknown id or empty name)"); return false; }
            (e.Events ?? (e.Events = new List<(float, string)>())).Add((Mathf.Clamp01(normalizedTime), evtName));
            e.Events.Sort((a, b) => a.t.CompareTo(b.t));
            return true;
        }

        // Owner: the events already fired for the current play, per body ZDO (seq, index of the next event).
        private static readonly Dictionary<ZDOID, (int seq, int next, int pass)> s_eventState = new Dictionary<ZDOID, (int, int, int)>();

        private static void FireEvents(Character who, ZDO zdo, int clip)
        {
            if (!s_byHash.TryGetValue(clip, out Entry e) || e.Events == null || e.Events.Count == 0) return;
            int seq = zdo.GetInt(ZSeq, 0);
            float norm = ((float)ZNet.instance.GetTimeSeconds() - zdo.GetFloat(ZT0, 0f)) * zdo.GetFloat(ZSpeed, 1f) / Mathf.Max(0.01f, e.Clip.length);
            int pass = Mathf.FloorToInt(Mathf.Max(0f, norm));
            float frac = norm - pass;
            if (!s_eventState.TryGetValue(zdo.m_uid, out var st) || st.seq != seq) st = (seq, 0, 0);
            if ((ClipMode)zdo.GetInt(ZMode, 0) == ClipMode.Loop && pass > st.pass) st = (seq, 0, pass);   // a loop fires again every pass
            else if (pass > st.pass) frac = 1f;   // one-shot / hold: past the end, fire what is left once
            while (st.next < e.Events.Count && e.Events[st.next].t <= frac)
            {
                string name = e.Events[st.next++].name;
                FiresLogger.LogDebug($"{Tag}event {e.Id}.{name} on {NameOf(who)} at {frac:0.00}");
                try { ClipEvent?.Invoke(who, e.Id, name); } catch (Exception ex) { FiresLogger.LogWarning($"{Tag}a ClipEvent handler threw: {ex.Message}"); }
            }
            s_eventState[zdo.m_uid] = st;
        }

        // ---- config ([Movement] "Clip slots") ----
        public enum Mode { Auto, On, Off }
        private static ConfigEntry<Mode> s_mode;
        private static int s_resolvedFor = -1;
        private static bool s_active;

        internal static void Bind(ConfigFile config)
        {
            MovementFeature.Bind(config);
            s_mode = config.Bind(MovementFeature.Section, MovementFeature.ClipSlots, Mode.Auto, MovementFeature.Advanced(
                "Custom animation clips other mods play on players and companions (Core's clip slots). Auto: on once a mod registers a clip; "
                + "with none, nothing changes. On / Off: forced."));
            s_mode.SettingChanged += (_, __) => s_resolvedFor = -1;
            MovementFeature.Changed += _ => s_resolvedFor = -1;
        }

        public static bool Active
        {
            get
            {
                int key = (s_mode != null ? (int)s_mode.Value : 0) * 2 + (MovementFeature.MasterOn ? 1 : 0) + (MovementFeature.HasConsumer(MovementFeature.ClipSlots) ? 10 : 0);
                if (key == s_resolvedFor) return s_active;
                s_resolvedFor = key;
                Mode m = s_mode != null ? s_mode.Value : Mode.Auto;
                string why;
                if (!MovementFeature.MasterOn) { s_active = false; why = "off (master OFF)"; }
                else if (m == Mode.On) { s_active = true; why = "On (forced)"; }
                else if (m == Mode.Off) { s_active = false; why = "Off (forced)"; }
                else if (MovementFeature.HasConsumer(MovementFeature.ClipSlots)) { s_active = true; why = "Auto -> active (consumer " + MovementFeature.ConsumersOf(MovementFeature.ClipSlots) + ")"; }
                else { s_active = false; why = "Auto -> off (no consumer)"; }
                FiresLogger.LogInfo("[Movement] Clip slots: " + why);
                return s_active;
            }
        }

        // ---- public API ----
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Register(string id, AnimationClip clip, ClipOptions defaults = null)
        {
            string consumer = System.Reflection.Assembly.GetCallingAssembly().GetName().Name;
            if (string.IsNullOrEmpty(id) || clip == null) { Once("reg-null:" + id, $"{Tag}{consumer}: Register refused ({(clip == null ? "null clip" : "empty id")}) for '{id}'"); return false; }
            if (s_byId.ContainsKey(id)) { Once("reg-dup:" + id, $"{Tag}{consumer}: Register refused, '{id}' is already registered"); return false; }
            var e = new Entry { Id = id, Clip = clip, Defaults = defaults?.Copy() ?? new ClipOptions() };
            s_byId[id] = e;
            s_byHash[id.GetStableHashCode()] = e;
            MovementFeature.Register(MovementFeature.ClipSlots, consumer);
            FiresLogger.LogDebug($"{Tag}registered '{id}' ({clip.name}, {clip.length:0.00} s) by {consumer}");
            return true;
        }

        public static bool IsRegistered(string id) => id != null && s_byId.ContainsKey(id);

        public static bool Play(Character body, string id, ClipOptions options = null)
        {
            if (!Active) return Refuse(id, "clip slots are off ([Movement] Clip slots / master)");
            if (body == null) return Refuse(id, "no body");
            if (id == null || !s_byId.TryGetValue(id, out Entry e)) return Refuse(id, "unknown id");
            ZNetView view = body.GetComponent<ZNetView>();
            if (view == null || !view.IsValid() || !view.IsOwner()) return Refuse(id, "not the body's owner");
            Animator animator = FiresCore.Movement.CrawlPose.CrawlPose.AnimatorOf(body);
            if (animator == null || animator.runtimeAnimatorController == null) return Refuse(id, "the body has no animator controller");
            if (!animator.isHuman || ResolveHosts(animator) == null) return Refuse(id, "the body's controller has no borrowable emote states (creature rig: v2)");
            ZDO zdo = view.GetZDO();
            if (zdo.GetInt(ClimbZdo.StateHash, 0) != 0) return Refuse(id, "a climb is running");
            if (body.IsDead()) return Refuse(id, "the body is dead");
            if (body is Player p && (p.IsAttached() || p.IsSitting())) return Refuse(id, "the body is attached or sitting");
            ClipOptions o = options ?? e.Defaults;
            zdo.Set(ZClip, id.GetStableHashCode());
            zdo.Set(ZMode, (int)o.Mode);
            zdo.Set(ZT0, (float)ZNet.instance.GetTimeSeconds());
            zdo.Set(ZSpeed, Mathf.Max(0.05f, o.Speed));
            zdo.Set(ZRoot, o.RootMotion ? 1 : 0);
            zdo.Set(ZSeq, zdo.GetInt(ZSeq, 0) + 1);
            s_ownerOptions[view.GetZDO().m_uid] = (id, o);
            return true;
        }

        public static void Stop(Character body, float fadeOut = -1f) => StopWith(body, "stopped");

        public static bool IsPlaying(Character body, string id = null)
        {
            ZNetView view = body != null ? body.GetComponent<ZNetView>() : null;
            if (view == null || !view.IsValid()) return false;
            int h = view.GetZDO().GetInt(ZClip, 0);
            return h != 0 && (id == null || h == id.GetStableHashCode());
        }

        // ---- owner bookkeeping ----
        private static readonly Dictionary<ZDOID, (string id, ClipOptions options)> s_ownerOptions = new Dictionary<ZDOID, (string, ClipOptions)>();

        private static void StopWith(Character body, string why)
        {
            ZNetView view = body != null ? body.GetComponent<ZNetView>() : null;
            if (view == null || !view.IsValid() || !view.IsOwner()) return;
            ZDO zdo = view.GetZDO();
            int h = zdo.GetInt(ZClip, 0);
            if (h == 0) return;
            zdo.Set(ZClip, 0);
            s_ownerOptions.Remove(zdo.m_uid);
            string id = s_byHash.TryGetValue(h, out Entry e) ? e.Id : "hash " + h;
            FiresLogger.LogDebug($"{Tag}end {id} on {NameOf(body)}: {why}");
            try { Finished?.Invoke(body, id); } catch (Exception ex) { FiresLogger.LogWarning($"{Tag}a Finished handler threw: {ex.Message}"); }
        }

        // ---- per-peer driver ----
        [HarmonyPatch(typeof(ZSyncAnimation), nameof(ZSyncAnimation.CustomFixedUpdate))]
        private static class Hook
        {
            // First: when a climb starts, the body's original controller is back before the climb animator wraps it.
            [HarmonyPriority(Priority.First)]
            private static void Postfix(ZNetView ___m_nview, Animator ___m_animator)
            {
                try { Tick(___m_nview, ___m_animator); }
                catch (Exception ex) { FiresLogger.LogWarning($"{Tag}animator update failed: {ex.Message}"); }
            }
        }

        private static bool? s_noGraphics;
        private static bool NoGraphics => s_noGraphics ?? (s_noGraphics = SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null).Value;

        private static void Tick(ZNetView view, Animator animator)
        {
            if (animator == null || view == null || !view.IsValid()) return;
            ZDO zdo = view.GetZDO();
            int clip = zdo.GetInt(ZClip, 0);
            s_bodies.TryGetValue(animator, out Body body);
            if (clip == 0 && body == null) return;   // the common case

            bool owner = view.IsOwner();
            Character who = view.GetComponent<Character>();
            int climb = zdo.GetInt(ClimbZdo.StateHash, 0);

            // Owner: interrupts and one-shot end (the ZDO is the truth for every peer).
            if (clip != 0 && owner && who != null)
            {
                s_ownerOptions.TryGetValue(zdo.m_uid, out var opt);
                bool interrupt = opt.options == null || opt.options.InterruptOnAttack;
                string why = null;
                if (climb != 0) why = "interrupted (climb)";
                else if (who.IsDead()) why = "interrupted (death)";
                else if (!Active) why = "interrupted (clip slots off)";
                else if (interrupt && who.InAttack()) why = "interrupted (attack)";
                else if (interrupt && who.IsStaggering()) why = "interrupted (stagger)";
                else if (interrupt && who is Player pl && pl.InDodge()) why = "interrupted (dodge)";
                else if ((ClipMode)zdo.GetInt(ZMode, 0) == ClipMode.OneShot && s_byHash.TryGetValue(clip, out Entry oe))
                {
                    float elapsed = (float)ZNet.instance.GetTimeSeconds() - zdo.GetFloat(ZT0, 0f);
                    if (elapsed * zdo.GetFloat(ZSpeed, 1f) >= oe.Clip.length) why = "oneshot done";
                }
                else if (!s_byHash.ContainsKey(clip)) why = "unknown hash on the owner";
                if (why == null) FireEvents(who, zdo, clip);
                else { if (why == "oneshot done") FireEvents(who, zdo, clip); StopWith(who, why); clip = 0; }
            }
            if (NoGraphics) return;
            if (climb != 0 && body != null) { Restore(animator, body, immediate: true); return; }   // the climb takes the animator

            if (clip != 0)
            {
                int seq = zdo.GetInt(ZSeq, 0);
                if (body != null && body.Seq == seq && body.ClipHash == clip && body.RestoreAt < 0f) { Hold(animator, body); return; }
                if (!s_byHash.TryGetValue(clip, out Entry e))
                {
                    Once("unknown:" + clip, $"{Tag}remote {NameOf(who)}: clip unknown hash {clip} (the mod that registered it is not installed here); vanilla animation");
                    if (body != null) Restore(animator, body, immediate: false);
                    return;
                }
                if (body == null && !Begin(animator, who, out body)) return;
                Start(animator, view, zdo, body, e, seq, clip, owner);
                return;
            }

            if (body != null) Restore(animator, body, immediate: false);
        }

        private static bool Begin(Animator animator, Character who, out Body body)
        {
            body = null;
            RuntimeAnimatorController original = animator.runtimeAnimatorController;
            if (original == null) return false;
            int[] hosts = ResolveHosts(animator);
            if (hosts == null) { Once("nohost:" + original.GetInstanceID(), $"{Tag}'{animator.gameObject.name}': its controller has no borrowable emote states; clips are not shown on it"); return false; }
            var ov = new AnimatorOverrideController(original) { name = (original.name ?? "") + " (clip slots)" };
            var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>(ov.overridesCount);
            ov.GetOverrides(pairs);
            body = new Body { Original = original, Override = ov, Pairs = pairs, HostHash = hosts, Who = who, OriginalSpeed = animator.speed, OriginalRoot = animator.applyRootMotion };
            s_bodies[animator] = body;
            if (s_bodies.Count > 64) Prune();
            return true;
        }

        private static readonly Dictionary<RuntimeAnimatorController, int[]> s_hosts = new Dictionary<RuntimeAnimatorController, int[]>();
        private static int[] ResolveHosts(Animator animator)
        {
            RuntimeAnimatorController c = animator.runtimeAnimatorController;
            if (c is AnimatorOverrideController aoc && aoc.name != null && aoc.name.EndsWith(" (clip slots)", StringComparison.Ordinal)) c = aoc.runtimeAnimatorController;
            if (s_hosts.TryGetValue(c, out int[] got)) return got;
            var hashes = new int[Hosts.Length];
            var names = new string[Hosts.Length];
            bool ok = animator.HasState(0, MovementHash);
            for (int i = 0; i < Hosts.Length && ok; i++)
            {
                foreach (string s in Hosts[i].states)
                {
                    int h = Animator.StringToHash(s);
                    if (animator.HasState(0, h)) { hashes[i] = h; names[i] = s; break; }
                }
                if (hashes[i] == 0) ok = false;
            }
            got = ok ? hashes : null;
            s_hosts[c] = got;
            FiresLogger.LogDebug($"{Tag}host states on '{c.name}': {(ok ? string.Join(", ", names) : "missing (refused)")}");
            return got;
        }

        private static void Start(Animator animator, ZNetView view, ZDO zdo, Body body, Entry e, int seq, int clipHash, bool owner)
        {
            ClipMode mode = (ClipMode)zdo.GetInt(ZMode, 0);
            float speed = zdo.GetFloat(ZSpeed, 1f);
            bool root = zdo.GetInt(ZRoot, 0) != 0;
            // Next slot: the other one of the family, so a new clip crossfades from the one playing.
            int family = mode == ClipMode.OneShot ? 0 : 2;
            int slot = (body.Slot == family) ? family + 1 : family;
            string original = Hosts[slot].original;
            int replaced = 0;
            for (int i = 0; i < body.Pairs.Count; i++)
            {
                AnimationClip own = body.Pairs[i].Key;
                if (own != null && own.name == original) { body.Pairs[i] = new KeyValuePair<AnimationClip, AnimationClip>(own, e.Clip); replaced++; }
            }
            if (replaced == 0) { Once("noclip:" + original, $"{Tag}the controller has no '{original}' clip to carry custom clips; not shown"); Restore(animator, body, true); return; }
            body.Override.ApplyOverrides(body.Pairs);
            if (!body.Applied) { ClimbAnimator.SwapKeepingParameters(animator, body.Override); body.Applied = true; }
            body.Seq = seq; body.ClipHash = clipHash; body.Slot = slot; body.Mode = mode; body.StateHash = body.HostHash[slot]; body.RestoreAt = -1f;
            animator.speed = body.OriginalSpeed * speed;
            animator.applyRootMotion = root;
            float elapsed = Mathf.Max(0f, (float)ZNet.instance.GetTimeSeconds() - zdo.GetFloat(ZT0, 0f));
            float len = Mathf.Max(0.01f, e.Clip.length);
            float norm = elapsed * speed / len;
            float fade = (s_ownerOptions.TryGetValue(zdo.m_uid, out var o) && o.options != null ? o.options.FadeIn : e.Defaults.FadeIn);
            if (norm < 0.05f) animator.CrossFadeInFixedTime(body.StateHash, fade, 0);
            else animator.CrossFadeInFixedTime(body.StateHash, fade, 0, mode == ClipMode.Loop ? (norm % 1f) * len : Mathf.Min(norm, 0.999f) * len);
            if (owner) FiresLogger.LogDebug($"{Tag}play {e.Id} on {NameOf(body.Who)}: mode {mode}, slot {(slot % 2 == 0 ? "A" : "B")} (state {Hosts[slot].states[0]}), speed {speed:0.00}, root motion {(root ? "on" : "off")}");
            else FiresLogger.LogDebug($"{Tag}remote {NameOf(body.Who)}: clip {e.Id}, mode {mode}");
        }

        // Keeps a Loop / Hold clip in its state when vanilla transitions take the animator out of it.
        private static void Hold(Animator animator, Body body)
        {
            if (body.Mode == ClipMode.OneShot || animator.IsInTransition(0)) return;
            var info = animator.GetCurrentAnimatorStateInfo(0);
            if (body.Mode == ClipMode.Hold)
            {
                if (info.shortNameHash != body.StateHash || info.normalizedTime >= 0.995f) animator.Play(body.StateHash, 0, 0.995f);
            }
            else if (info.shortNameHash != body.StateHash) animator.CrossFadeInFixedTime(body.StateHash, 0.1f, 0);
        }

        private static void Restore(Animator animator, Body body, bool immediate)
        {
            if (!immediate && body.Applied)
            {
                if (body.RestoreAt < 0f)
                {
                    body.RestoreAt = Time.time + RestoreAfterSeconds;
                    body.Seq = -1; body.ClipHash = 0;
                    animator.speed = body.OriginalSpeed;
                    animator.applyRootMotion = body.OriginalRoot;
                    animator.CrossFadeInFixedTime(MovementHash, 0.15f, 0);
                    return;
                }
                if (Time.time < body.RestoreAt) return;
            }
            animator.speed = body.OriginalSpeed;
            animator.applyRootMotion = body.OriginalRoot;
            if (body.Applied && animator.runtimeAnimatorController == body.Override) ClimbAnimator.SwapKeepingParameters(animator, body.Original);
            if (body.Override != null) UnityEngine.Object.Destroy(body.Override);
            s_bodies.Remove(animator);
        }

        private static void Prune()
        {
            var dead = new List<Animator>();
            foreach (var kv in s_bodies) if (kv.Key == null) { dead.Add(kv.Key); if (kv.Value.Override != null) UnityEngine.Object.Destroy(kv.Value.Override); }
            foreach (Animator a in dead) s_bodies.Remove(a);
        }

        private static bool Refuse(string id, string why)
        {
            Once("refuse:" + why, $"{Tag}play '{id}' refused: {why}");
            return false;
        }

        private static void Once(string key, string line)
        {
            if (s_logged.Add(key)) FiresLogger.LogDebug(line);
        }

        private static string NameOf(Character c) => c == null ? "?" : c is Player p ? p.GetPlayerName() : c.m_name;
    }
}
