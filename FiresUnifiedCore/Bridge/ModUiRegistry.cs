using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Registry of mod-owned UI screens, so cross-cutting consumers can operate over
    /// them without hard-referencing each owning mod. Lets the Marketplace UI host
    /// (GUIManager) ask "is any panel open?" for input blocking, close everything on
    /// Escape, and refresh fonts across open panels — while the panels themselves live
    /// in optional mods (companions, guilds). A mod registers its screens on init; when
    /// the mod is absent it simply never registers, and every query is a graceful no-op.
    /// </summary>
    public static class ModUiRegistry
    {
        private sealed class Entry
        {
            public Func<bool> IsOpen;
            public Action Close;
            public Func<GameObject> Root;
        }

        private static readonly Dictionary<string, Entry> _entries = new();

        /// <summary>Register (or replace) a screen by a stable key.</summary>
        /// <param name="isOpen">Returns true while the screen is visible/active.</param>
        /// <param name="close">Optional — closes the screen (called by CloseAll).</param>
        /// <param name="root">Optional — the screen's root GameObject (for font refresh).</param>
        public static void Register(string key, Func<bool> isOpen, Action close = null, Func<GameObject> root = null)
        {
            if (string.IsNullOrEmpty(key) || isOpen == null) return;
            _entries[key] = new Entry { IsOpen = isOpen, Close = close, Root = root };
        }

        public static void Unregister(string key)
        {
            if (!string.IsNullOrEmpty(key)) _entries.Remove(key);
        }

        /// <summary>True if any registered screen is currently open.</summary>
        public static bool IsAnyOpen()
        {
            bool any = false;
            foreach (var kv in _entries)
                if (IsReallyOpen(kv.Key, kv.Value)) { any = true; break; }
            NoteOpen(any);
            return any;
        }

        /// <summary>The keys of the screens open right now (diagnostics: what keeps "any open" true). Add-only.</summary>
        public static List<string> OpenKeys()
        {
            var open = new List<string>();
            foreach (var kv in _entries)
                if (IsReallyOpen(kv.Key, kv.Value)) open.Add(kv.Key);
            return open;
        }

        // A screen that
        // registered a root counts as open only while that root exists and is activeInHierarchy (its IsOpen flag can go stale when the
        // owner's close path skips it). No root registered = its IsOpen alone, as before.
        private static readonly HashSet<string> s_staleLogged = new HashSet<string>();
        private static bool IsReallyOpen(string key, Entry entry)
        {
            bool open;
            try { open = entry.IsOpen != null && entry.IsOpen(); } catch { return false; }
            if (!open || entry.Root == null) return open;
            GameObject root = null;
            try { root = entry.Root(); } catch { }
            if (root != null && root.activeInHierarchy) { s_staleLogged.Remove(key); return true; }
            if (s_staleLogged.Add(key))
                FiresCore.Logging.FiresLogger.LogDebug($"[ModUi] '{key}' says open but its root is {(root == null ? "gone" : "inactive")}: not counted as open (owner: fix its IsOpen)");
            return false;
        }

        // A Debug line naming the open screen(s) each time the set changes (IsAnyOpen runs every frame).
        private static string s_lastOpenSig = "";
        private static void NoteOpen(bool any)
        {
            string sig = any ? string.Join(", ", OpenKeys()) : "";
            if (sig == s_lastOpenSig) return;
            s_lastOpenSig = sig;
            FiresCore.Logging.FiresLogger.LogDebug(sig.Length > 0 ? $"[ModUi] open screens: {sig}" : "[ModUi] no mod screen open");
        }

        /// <summary>Closes one registered screen by key; false when it is unknown or has no close action. Add-only.</summary>
        public static bool Close(string key)
        {
            if (string.IsNullOrEmpty(key) || !_entries.TryGetValue(key, out var entry) || entry.Close == null) return false;
            try { entry.Close(); return true; } catch { return false; }
        }

        /// <summary>Close every registered screen that exposes a close action.</summary>
        public static void CloseAll()
        {
            // Snapshot first: a Close callback very commonly Unregister()s itself (or opens/replaces another
            // screen), which mutates _entries and would otherwise invalidate the live enumerator
            // ("Collection was modified; enumeration operation may not execute").
            foreach (var entry in new List<Entry>(_entries.Values))
                try { entry.Close?.Invoke(); } catch { }
        }

        /// <summary>Invoke <paramref name="action"/> with the root of each currently-open screen.</summary>
        public static void ForEachOpenRoot(Action<GameObject> action)
        {
            if (action == null) return;
            // Snapshot: the action (font refresh etc.) may touch the registry.
            foreach (var entry in new List<Entry>(_entries.Values))
            {
                try
                {
                    if (entry.IsOpen == null || !entry.IsOpen()) continue;
                    var go = entry.Root?.Invoke();
                    if (go != null) action(go);
                }
                catch { }
            }
        }
    }
}
