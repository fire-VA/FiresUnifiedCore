using System;
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
    }
}
