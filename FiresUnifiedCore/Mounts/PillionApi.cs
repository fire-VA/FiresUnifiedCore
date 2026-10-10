using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Logging;
using FiresCore.Npc;
using FiresCore.Npc.Interactions;
using UnityEngine;

namespace FiresCore.Mounts
{
    /// <summary>
    /// Implemented by the mount mod (FiresMounts): decides whether a companion may ride behind the rider, owns the
    /// seat node, and calls <see cref="PillionApi.Seat"/> / <see cref="PillionApi.Unseat"/> so Core does the body work.
    /// </summary>
    public interface IPillionProvider
    {
        bool TryBoard(Character companion, Character mount, out string reason);
        void Leave(Character companion, string why);
        Character Seated(Character mount);
    }

    /// <summary>
    /// Pillion: one companion rides behind the player on a mount.
    /// 0.2.372: frozen API. Provider calls forward to the registered mount mod; Seat/Unseat do the companion body work.
    /// </summary>
    public static class PillionApi
    {
        private static IPillionProvider _provider;

        // Seated companion -> mount it sits on (between Seat and Unseat).
        private static readonly Dictionary<Character, Character> _seated = new Dictionary<Character, Character>();
        // Last Leave reason per companion, reported by Unseat.
        private static readonly Dictionary<Character, string> _leaveWhy = new Dictionary<Character, string>();

        public static event Action<Character, Character> Boarded;
        public static event Action<Character, Character, string> Left;

        public static void Register(IPillionProvider p)
        {
            _provider = p;
            FiresLogger.LogDebug($"[Pillion] provider registered: {(p != null ? p.GetType().FullName : "none")}");
        }

        public static bool TryBoard(Character companion, Character mount, out string reason)
        {
            reason = null;
            if (_provider == null) { reason = "no mounts mod"; return false; }
            try
            {
                return _provider.TryBoard(companion, mount, out reason);
            }
            catch (Exception ex)
            {
                reason = "provider error: " + ex.Message;
                FiresLogger.LogWarning($"[Pillion] provider TryBoard threw: {ex}");
                return false;
            }
        }

        public static void Leave(Character companion, string why)
        {
            if (companion != null) _leaveWhy[companion] = why;
            if (_provider == null) return;
            try
            {
                _provider.Leave(companion, why);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[Pillion] provider Leave threw: {ex}");
            }
        }

        public static Character Seated(Character mount)
        {
            if (_provider == null) return null;
            try
            {
                return _provider.Seated(mount);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"[Pillion] provider Seated threw: {ex}");
                return null;
            }
        }

        public static bool IsPillion(Character c)
        {
            Prune();
            return c != null && _seated.ContainsKey(c);
        }

        /// <summary>The mount a pillion companion sits on (null when not seated).</summary>
        internal static Character MountOf(Character companion)
        {
            Prune();
            return companion != null && _seated.TryGetValue(companion, out var m) ? m : null;
        }

        /// <summary>True when any companion is seated on this mount through Seat.</summary>
        internal static bool HasPillionOn(Character mount)
        {
            Prune();
            return mount != null && _seated.Values.Any(m => m == mount);
        }

        internal static IEnumerable<Character> SeatedCompanions()
        {
            Prune();
            return _seated.Keys.ToList();
        }

        public static void Seat(Character companion, Transform node, string pose)
        {
            if (companion == null || node == null)
            {
                FiresLogger.LogWarning("[Pillion] Seat called with a null companion or node");
                return;
            }
            var body = companion.GetComponent<CompanionInteractionBehavior>();
            if (body == null)
            {
                FiresLogger.LogWarning($"[Pillion] Seat: {companion.name} has no CompanionInteractionBehavior");
                return;
            }
            Character mount = node.GetComponentInParent<Character>();
            string used = body.AttachToMount(node, pose);
            if (used == null)
            {
                FiresLogger.LogDebug($"[Pillion] board refused: body attach failed for {CompanionName(companion)}");
                return;
            }
            _seated[companion] = mount;
            _leaveWhy.Remove(companion);
            FiresLogger.LogDebug($"[Pillion] board {CompanionName(companion)} on {(mount != null ? mount.name : node.name)} pose {used}");
            try { Boarded?.Invoke(companion, mount); }
            catch (Exception ex) { FiresLogger.LogWarning($"[Pillion] Boarded handler threw: {ex}"); }
        }

        public static void Unseat(Character companion, Vector3 setDown)
        {
            if (companion == null) return;
            _seated.TryGetValue(companion, out var mount);
            bool wasSeated = _seated.Remove(companion);
            string why = _leaveWhy.TryGetValue(companion, out var w) && !string.IsNullOrEmpty(w) ? w : "unseat";
            _leaveWhy.Remove(companion);

            var body = companion.GetComponent<CompanionInteractionBehavior>();
            if (body != null) body.DetachTo(setDown);
            else companion.transform.position = setDown;

            // Riding paused follow; resume it.
            if (CompanionController.TryGet(companion, out var cc) && cc != null)
            {
                try { cc.SetFollowMode(true); }
                catch (Exception ex) { FiresLogger.LogWarning($"[Pillion] resume follow threw: {ex.Message}"); }
            }

            FiresLogger.LogDebug($"[Pillion] unboard {CompanionName(companion)} at {setDown} ({why})");
            if (!wasSeated) return;
            try { Left?.Invoke(companion, mount, why); }
            catch (Exception ex) { FiresLogger.LogWarning($"[Pillion] Left handler threw: {ex}"); }
        }

        private static string CompanionName(Character c)
        {
            if (c == null) return "null";
            return CompanionController.TryGet(c, out var cc) && cc != null && !string.IsNullOrEmpty(cc.companionName)
                ? cc.companionName : c.name;
        }

        private static void Prune()
        {
            if (_seated.Count == 0) return;
            List<Character> dead = null;
            foreach (var kv in _seated)
                if (kv.Key == null) (dead ?? (dead = new List<Character>())).Add(kv.Key);
            if (dead == null) return;
            foreach (var d in dead) { _seated.Remove(d); _leaveWhy.Remove(d); }
        }
    }
}
