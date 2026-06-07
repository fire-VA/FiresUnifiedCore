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
            foreach (var e in _entries.Values)
                try { if (e.IsOpen != null && e.IsOpen()) return true; } catch { }
            return false;
        }

        /// <summary>Close every registered screen that exposes a close action.</summary>
        public static void CloseAll()
        {
            foreach (var e in _entries.Values)
                try { e.Close?.Invoke(); } catch { }
        }

        /// <summary>Invoke <paramref name="action"/> with the root of each currently-open screen.</summary>
        public static void ForEachOpenRoot(Action<GameObject> action)
        {
            if (action == null) return;
            foreach (var e in _entries.Values)
            {
                try
                {
                    if (e.IsOpen == null || !e.IsOpen()) continue;
                    var go = e.Root?.Invoke();
                    if (go != null) action(go);
                }
                catch { }
            }
        }
    }
}
