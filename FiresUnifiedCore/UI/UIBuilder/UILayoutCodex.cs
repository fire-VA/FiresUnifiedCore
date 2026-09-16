using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// In-memory cache of all loaded UILayoutDefinitions.
    /// Scans the Config/FiresRPGmaker/UILayouts/ directory on init and provides
    /// lookup by UID and category.
    /// </summary>
    public static class UILayoutCodex
    {
        private static Dictionary<string, UILayoutDefinition> _layouts;
        private static bool _initialized;

        /// <summary>
        /// Initialize the codex by scanning all JSON files in the UILayouts directory.
        /// Safe to call multiple times - only loads once unless Reload() is called.
        /// </summary>
        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            // Extract any layouts shipped as embedded resources in the DLL
            // (e.g. companionsequipmentscreen.json). Existing files on disk
            // are left untouched so user edits survive mod updates.
            BundledLayoutExtractor.ExtractIfMissing();

            // Ensure built-in templates exist on disk
            UIBuilderTemplates.EnsureDefaultTemplates();

            Reload();
        }

        // Reload fires once per synced layout FILE at login (29 files = 29 reloads) — only say
        // something when the set actually changed.
        private static int _lastLoggedCount = -1;

        /// <summary>
        /// Force-reload all layouts from disk. Called after server sync or file changes.
        /// </summary>
        public static void Reload()
        {
            try
            {
                _layouts = UILayoutSerializer.LoadAll();
                if (_layouts.Count != _lastLoggedCount)
                {
                    _lastLoggedCount = _layouts.Count;
                    Debug.Log($"[UILayoutCodex] Loaded {_layouts.Count} layout(s)");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[UILayoutCodex] Reload error: {ex.Message}");
                if (_layouts == null) _layouts = new Dictionary<string, UILayoutDefinition>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Get a layout by its UID. Returns null if not found.
        /// </summary>
        public static UILayoutDefinition Get(string uid)
        {
            Init();
            if (string.IsNullOrEmpty(uid) || _layouts == null) return null;
            _layouts.TryGetValue(uid, out var layout);
            return layout;
        }

        /// <summary>
        /// Returns all loaded layouts.
        /// </summary>
        public static List<UILayoutDefinition> GetAll()
        {
            Init();
            return _layouts != null ? _layouts.Values.ToList() : new List<UILayoutDefinition>();
        }

        /// <summary>
        /// Returns all layouts in a specific category (e.g., "Dialogue", "Quest", "Custom").
        /// </summary>
        public static List<UILayoutDefinition> GetByCategory(string category)
        {
            Init();
            if (string.IsNullOrEmpty(category) || _layouts == null)
                return new List<UILayoutDefinition>();
            return _layouts.Values
                .Where(l => string.Equals(l.Category, category, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Returns all layout UIDs.
        /// </summary>
        public static List<string> GetAllUIDs()
        {
            Init();
            return _layouts != null ? _layouts.Keys.ToList() : new List<string>();
        }

        /// <summary>
        /// Adds or replaces a layout in the in-memory cache.
        /// Does NOT save to disk - use UILayoutSerializer.SaveToFile for persistence.
        /// </summary>
        public static void AddOrReplace(UILayoutDefinition layout)
        {
            if (layout == null || string.IsNullOrEmpty(layout.UID)) return;
            Init();
            _layouts[layout.UID] = layout;
        }

        /// <summary>
        /// Removes a layout from the in-memory cache by UID.
        /// Does NOT delete from disk.
        /// </summary>
        public static bool Remove(string uid)
        {
            Init();
            if (string.IsNullOrEmpty(uid) || _layouts == null) return false;
            return _layouts.Remove(uid);
        }

        /// <summary>
        /// Checks if a layout with the given UID exists.
        /// </summary>
        public static bool Exists(string uid)
        {
            Init();
            return !string.IsNullOrEmpty(uid) && _layouts != null && _layouts.ContainsKey(uid);
        }

        /// <summary>
        /// Returns the total count of loaded layouts.
        /// </summary>
        public static int Count
        {
            get
            {
                Init();
                return _layouts != null ? _layouts.Count : 0;
            }
        }
    }
}
