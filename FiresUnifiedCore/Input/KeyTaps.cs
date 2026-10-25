using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using FiresCore.Logging;
using UnityEngine;

namespace FiresCore.Input
{
    /// <summary>
    /// Single and double taps of one key that two features share (e.g. G for possession and
    /// double tap G for guilds). A feature claims a key's single taps while it can act (admin possession on G). While
    /// a claim is active, the claimant gets the single tap DoubleTapWindow seconds after the press (no second press came), and
    /// every plain <see cref="Chord.IsDown"/> of that key (FiresGuilds' screen key) fires only on the second press of a double
    /// tap. With no active claim nothing changes: Chord.IsDown fires on every press.
    /// Callers ask every frame (GetKeyDown is per frame); the first ask in a frame evaluates, later asks read the result.
    /// </summary>
    public static class KeyTaps
    {
        public const float DoubleTapWindow = 0.3f;

        private sealed class Claim { public string Owner; public Func<KeyboardShortcut> Key; public Func<bool> Active; }
        private sealed class State { public int Frame = -1; public float FirstDown, SinglePress; public bool Pending, Single, Double; }

        private static readonly List<Claim> s_claims = new List<Claim>();
        private static readonly Dictionary<string, State> s_states = new Dictionary<string, State>();

        /// <summary>Claims the single taps of <paramref name="key"/> while <paramref name="active"/> returns true.</summary>
        public static void ClaimSingleTaps(string owner, Func<KeyboardShortcut> key, Func<bool> active)
        {
            if (key == null || active == null) return;
            s_claims.Add(new Claim { Owner = owner ?? "?", Key = key, Active = active });
        }

        /// <summary>True while a feature claims this key's single taps (other binds of it then fire on a double tap).</summary>
        public static bool SingleTapClaimed(KeyboardShortcut key) => ClaimFor(key) != null;

        /// <summary>A single tap of the key completed this frame (pressed once, no second press within the window).</summary>
        public static bool SingleTapped(KeyboardShortcut key) => Eval(key).Single;

        /// <summary>The second press of a double tap landed this frame.</summary>
        public static bool DoubleTapped(KeyboardShortcut key) => Eval(key).Double;

        /// <summary>When (Time.unscaledTime) the key was pressed for the single tap that completed this frame or last.</summary>
        public static float SinglePressTime(KeyboardShortcut key) => Eval(key).SinglePress;

        private static Claim ClaimFor(KeyboardShortcut key)
        {
            if (key.MainKey == KeyCode.None) return null;
            for (int i = 0; i < s_claims.Count; i++)
            {
                Claim c = s_claims[i];
                try { if (c.Active() && Same(c.Key(), key)) return c; }
                catch { }
            }
            return null;
        }

        private static bool Same(KeyboardShortcut a, KeyboardShortcut b)
        {
            if (a.MainKey != b.MainKey) return false;
            // No HashSet per call; binds carry 0-2 modifiers, so nested loops are cheap.
            int na = 0, nb = 0;
            foreach (var m in a.Modifiers) { na++; if (!HasMod(b, m)) return false; }
            foreach (var m in b.Modifiers) nb++;
            return na == nb;
        }

        private static bool HasMod(KeyboardShortcut s, KeyCode k)
        {
            foreach (var m in s.Modifiers) if (m == k) return true;
            return false;
        }

        // Eval ran key.ToString() every frame; the id is now cached per main key and rebuilt
        // only when the bind changes.
        private sealed class IdCache { public KeyboardShortcut Key; public string Id; }
        private static readonly Dictionary<int, IdCache> s_ids = new Dictionary<int, IdCache>();

        private static string IdOf(KeyboardShortcut key)
        {
            int k = (int)key.MainKey;
            if (s_ids.TryGetValue(k, out IdCache c) && Same(c.Key, key)) return c.Id;
            if (c == null) s_ids[k] = c = new IdCache();
            c.Key = key; c.Id = key.ToString();
            return c.Id;
        }

        private static State Eval(KeyboardShortcut key)
        {
            string id = IdOf(key);
            if (!s_states.TryGetValue(id, out State s)) s_states[id] = s = new State();
            if (s.Frame == Time.frameCount) return s;
            s.Frame = Time.frameCount;
            s.Single = s.Double = false;
            float now = Time.unscaledTime;
            if (Chord.IsDownRaw(key))
            {
                if (s.Pending && now - s.FirstDown <= DoubleTapWindow) { s.Pending = false; s.Double = true; Log(key, id, "double tap"); }
                else { s.Pending = true; s.FirstDown = now; }
            }
            else if (s.Pending && now - s.FirstDown > DoubleTapWindow) { s.Pending = false; s.Single = true; s.SinglePress = s.FirstDown; Log(key, id, "single tap"); }
            return s;
        }

        private static void Log(KeyboardShortcut key, string id, string what)
        {
            Claim c = ClaimFor(key);
            if (c == null) return;
            FiresLogger.LogDebug("[Input] " + id + ": " + what + (what == "single tap" ? " -> " + c.Owner : " -> the key's other binds (" + c.Owner + " skipped)"));
        }
    }
}
