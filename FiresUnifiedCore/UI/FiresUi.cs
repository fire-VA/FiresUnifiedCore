using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace FiresCore.UI
{
    /// <summary>
    /// The Fires UI roots, so Core can fix what every Fires window shares. Scroll speed (Fire, 2026-09-29, "Core, Fires UIs only"):
    /// a ScrollRect nobody set stays at Unity's default sensitivity of 1, and Valheim hands the UI a small wheel delta, so those lists
    /// crawled. Under a registered root, a ScrollRect still at exactly 1 gets vanilla's Skills-list sensitivity x [UI] UI scroll speed.
    /// Vanilla, other mods' and deliberately tuned lists are never touched. Mods call <see cref="Register"/> on their window roots.
    /// </summary>
    public static class FiresUi
    {
        private const float UnityDefaultSensitivity = 1f;
        private const float FallbackSensitivity = 120f;

        private static readonly List<GameObject> s_roots = new List<GameObject>();
        private static readonly List<ScrollRect> s_sweep = new List<ScrollRect>();
        private static ConfigEntry<float> s_scrollSpeed;
        private static bool s_installed;

        /// <summary>Marks <paramref name="root"/> as a Fires UI root and fixes the scroll lists already under it.</summary>
        public static void Register(GameObject root)
        {
            if (root == null) return;
            s_roots.RemoveAll(r => r == null);
            if (!s_roots.Contains(root)) s_roots.Add(root);

            s_sweep.Clear();
            root.GetComponentsInChildren(true, s_sweep);
            foreach (ScrollRect scroll in s_sweep) Apply(scroll);
            s_sweep.Clear();
        }

        /// <summary>Forgets a root (optional: destroyed roots are dropped on their own).</summary>
        public static void Unregister(GameObject root)
        {
            s_roots.RemoveAll(r => r == null || r == root);
        }

        /// <summary>Whether <paramref name="t"/> sits under a registered root.</summary>
        public static bool IsFiresUi(Transform t)
        {
            if (t == null || s_roots.Count == 0) return false;
            for (Transform at = t; at != null; at = at.parent)
            {
                foreach (GameObject root in s_roots)
                    if (root != null && at == root.transform) return true;
            }
            return false;
        }

        /// <summary>The sensitivity a never-set Fires list gets: vanilla's Skills list x the config multiplier.</summary>
        public static float ScrollSensitivity
        {
            get
            {
                float vanilla = FallbackSensitivity;
                ScrollRect skills = null;
                try
                {
                    var dialog = InventoryGui.instance != null ? InventoryGui.instance.m_skillsDialog : null;
                    if (dialog != null) skills = Traverse.Create(dialog).Field<ScrollRect>("skillListScrollRect").Value;
                }
                catch { skills = null; }
                if (skills != null && skills.scrollSensitivity > UnityDefaultSensitivity) vanilla = skills.scrollSensitivity;
                return vanilla * (s_scrollSpeed != null ? Mathf.Max(0.05f, s_scrollSpeed.Value) : 1f);
            }
        }

        /// <summary>Binds [UI] UI scroll speed and, on a client, hooks ScrollRect.OnEnable (not through PatchAll: UI types stay off
        /// the dedicated server's IL import).</summary>
        internal static void Install(Harmony harmony, ConfigFile config)
        {
            s_scrollSpeed = config.Bind("UI", "UI scroll speed", 1f,
                "Scroll-wheel speed in Fires windows whose lists never set their own, as a multiple of the vanilla Skills list. Client only.");
            if (s_installed || Application.isBatchMode) return;
            s_installed = true;
            harmony.Patch(AccessTools.Method(typeof(ScrollRect), "OnEnable"),
                postfix: new HarmonyMethod(typeof(FiresUi), nameof(ScrollRectOnEnable)));
        }

        private static void ScrollRectOnEnable(ScrollRect __instance) => Apply(__instance);

        private static void Apply(ScrollRect scroll)
        {
            if (scroll == null || !Mathf.Approximately(scroll.scrollSensitivity, UnityDefaultSensitivity)) return;
            if (!IsFiresUi(scroll.transform)) return;
            scroll.scrollSensitivity = ScrollSensitivity;
        }
    }
}
