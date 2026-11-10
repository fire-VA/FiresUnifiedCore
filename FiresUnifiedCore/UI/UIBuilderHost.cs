using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FiresCore.UI
{
    // The mod-specific inputs the shared UIBuilder SDK needs, so Core never references a consuming mod. Set once at
    // startup, before using the SDK: AssetBundle, RootObject, CoroutineHost, LayoutResourceAssembly,
    // LayoutResourcePrefix, ModAssemblyName, ModName and InputBlocker.
    public static class UIBuilderHost
    {
        // The consuming mod's asset bundle (the SDK skips it when scanning loaded bundles for captured assets).
        public static AssetBundle AssetBundle;

        // The mod's persistent root GameObject — parent for SDK-created hosts/markers that must survive scenes.
        public static GameObject RootObject;

        // A live MonoBehaviour the SDK uses to start coroutines.
        public static MonoBehaviour CoroutineHost;

        // The assembly whose embedded resources hold the mod's bundled UI-layout JSON files.
        public static Assembly LayoutResourceAssembly;

        // Embedded-resource name prefix for those layouts, e.g. "MyPlugin.BundledLayouts.".
        public static string LayoutResourcePrefix = string.Empty;

        // The mod's plugin assembly file name, e.g. "MyPlugin.dll" (used by auto-discovery).
        public static string ModAssemblyName = string.Empty;

        // The mod's short name, used to recognize the mod's own files/paths while scanning.
        public static string ModName = string.Empty;

        // Routes UI input-blocking to the mod's own input system (cursor/movement suppression).
        public static Action<bool> InputBlocker;

        // Supplies the mod's known background sprite (used by the SDK when matching a layout's named sprite).
        // Lets the SDK reach the mod's prefab factory without Core depending on it.
        public static Func<Sprite> BackgroundSpriteProvider;

        // Resolves the mod's configured font choice for a category (the mod owns its font config enum/binding).
        // When unset or returning null, UIFontConfig falls back to its built-in defaults.
        public static Func<UIFontConfig.FontCategory, UIFontConfig.FontStyle?> ConfiguredFontProvider;

        // Marks a synced config file as admin-originated so the mod's file watcher doesn't echo it back.
        // Args: (folderName, fileName, senderUid).
        public static Action<string, string, long> MarkFileAdminSynced;

        // Resolves the assembly that bundled layouts are read from, defaulting to the SDK's own assembly when a
        // consumer hasn't supplied one.
        public static Assembly ResolveLayoutAssembly() =>
            LayoutResourceAssembly ?? typeof(UIBuilderHost).Assembly;

        internal static void BlockInput(bool block)
        {
            try { InputBlocker?.Invoke(block); }
            catch { }
        }

        internal static Sprite GetBackgroundSprite()
        {
            try { return BackgroundSpriteProvider?.Invoke(); }
            catch { return null; }
        }

        // ── More than one mod ──
        // The fields above are one global, so the last mod to set them won: FiresCompanions loads after FiresRPGmaker and
        // overwrote its bundle, and the RPGmaker globe HUD's prefab was never found again. The fields stay (mods built
        // against them write them directly), but every value seen is remembered, and the SDK looks across all of them:
        // bundles, layout sources, mod names and admin-sync markers. A mod calls Register() right after setting the
        // fields; Core also remembers whatever is current each time it reads them. Lookups still try the current mod first.
        private static readonly List<AssetBundle> _bundles = new List<AssetBundle>();
        private static readonly List<KeyValuePair<Assembly, string>> _layoutSources = new List<KeyValuePair<Assembly, string>>();
        private static readonly List<string> _modNames = new List<string>();
        private static readonly List<Action<string, string, long>> _adminSyncMarkers = new List<Action<string, string, long>>();

        /// <summary>Remembers this mod's fields (bundle, layouts, name, admin-sync marker) so a mod that sets them later
        /// doesn't hide them. Call right after setting the fields. Safe to call more than once.</summary>
        public static void Register() => Remember();

        internal static void Remember()
        {
            if (AssetBundle != null && !_bundles.Contains(AssetBundle)) _bundles.Add(AssetBundle);
            if (LayoutResourceAssembly != null && !string.IsNullOrEmpty(LayoutResourcePrefix))
            {
                var source = new KeyValuePair<Assembly, string>(LayoutResourceAssembly, LayoutResourcePrefix);
                if (!_layoutSources.Contains(source)) _layoutSources.Add(source);
            }
            if (!string.IsNullOrEmpty(ModName) && !_modNames.Contains(ModName)) _modNames.Add(ModName);
            if (MarkFileAdminSynced != null && !_adminSyncMarkers.Contains(MarkFileAdminSynced)) _adminSyncMarkers.Add(MarkFileAdminSynced);
        }

        /// <summary>A bundle found holding a mod's UI assets that no mod registered (a mod built before Register existed).</summary>
        internal static void RememberBundle(AssetBundle bundle)
        {
            if (bundle != null && !_bundles.Contains(bundle)) _bundles.Add(bundle);
        }

        /// <summary>The current mod's bundle first, then every other bundle a mod has set (destroyed ones skipped).</summary>
        internal static List<AssetBundle> Bundles()
        {
            Remember();
            var list = new List<AssetBundle>(_bundles.Count);
            if (AssetBundle != null) list.Add(AssetBundle);
            foreach (AssetBundle bundle in _bundles)
                if (bundle != null && bundle != AssetBundle) list.Add(bundle);
            return list;
        }

        /// <summary>True for a bundle any mod has set as its own (the SDK skips those when scanning for captured assets).</summary>
        internal static bool IsHostBundle(AssetBundle bundle)
        {
            if (bundle == null) return false;
            Remember();
            return bundle == AssetBundle || _bundles.Contains(bundle);
        }

        /// <summary>Every (assembly, prefix) a mod has set for its bundled layouts, the current one first.</summary>
        internal static List<KeyValuePair<Assembly, string>> LayoutSources()
        {
            Remember();
            var list = new List<KeyValuePair<Assembly, string>>();
            if (LayoutResourceAssembly != null && !string.IsNullOrEmpty(LayoutResourcePrefix))
                list.Add(new KeyValuePair<Assembly, string>(LayoutResourceAssembly, LayoutResourcePrefix));
            foreach (var source in _layoutSources)
                if (!list.Contains(source)) list.Add(source);
            return list;
        }

        /// <summary>True when the path holds the current mod's name (as before: an unset name matches) or any other mod's.</summary>
        internal static bool PathHasModName(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            Remember();
            if (path.IndexOf(ModName ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            foreach (string name in _modNames)
                if (path.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Tells every mod's file watcher (each registered marker, once) that a synced file came from the admin.</summary>
        internal static void MarkAdminSynced(string folderName, string fileName, long senderUid)
        {
            Remember();
            foreach (var marker in _adminSyncMarkers.ToArray())
            {
                try { marker(folderName, fileName, senderUid); }
                catch { }
            }
        }
    }
}
