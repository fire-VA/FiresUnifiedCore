using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Materials
{
    // The game's own shader for a name is the instance its own objects draw with. A loaded copy that merely "runs" is
    // not proof: a ripped shader with no programs keeps its Fallback, reports isSupported and draws as that fallback
    // (the rip's Custom/Vegetation as Legacy Shaders/Diffuse, so FAT's skyland vines lost their cutout). So the
    // prefab and clutter lists are read before any mod injects into them, and the shaders on those objects are the
    // reference. Core-only: VanillaShaderRebind is source-linked into standalone mods, which never set its lookup.
    internal static class VanillaShaderSources
    {
        private static List<GameObject> s_vanillaPrefabs;
        private static List<GameObject> s_vanillaClutter;
        private static Dictionary<string, Shader> s_live;
        private static readonly List<Material> s_slots = new List<Material>();

        private static bool IsHeadless => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        internal static void Install() => VanillaShaderRebind.LiveShaderLookup = Live;

        private static Shader Live(string shaderName)
        {
            if (string.IsNullOrEmpty(shaderName)) return null;
            if (s_live == null)
            {
                if (s_vanillaPrefabs == null) return null;
                s_live = BuildIndex();
            }
            return s_live.TryGetValue(shaderName, out Shader shader) ? shader : null;
        }

        private static Dictionary<string, Shader> BuildIndex()
        {
            var live = new Dictionary<string, Shader>(StringComparer.Ordinal);
            foreach (var prefab in s_vanillaPrefabs) AddShaders(prefab, live);
            if (s_vanillaClutter != null)
                foreach (var prefab in s_vanillaClutter) AddShaders(prefab, live);
            return live;
        }

        private static void AddShaders(GameObject prefab, Dictionary<string, Shader> live)
        {
            if (prefab == null) return;
            foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
            {
                renderer.GetSharedMaterials(s_slots);
                foreach (var material in s_slots) Add(material, live);
            }
            foreach (var instanced in prefab.GetComponentsInChildren<InstanceRenderer>(true))
                Add(instanced.m_material, live);
        }

        private static void Add(Material material, Dictionary<string, Shader> live)
        {
            var shader = material != null ? material.shader : null;
            if (shader == null || !shader.isSupported || live.ContainsKey(shader.name)) return;
            live[shader.name] = shader;
        }

        [HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
        private static class ZNetScene_Awake_SnapshotVanillaPrefabs
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ZNetScene __instance)
            {
                if (IsHeadless || __instance == null || __instance.m_prefabs == null) return;
                s_vanillaPrefabs = new List<GameObject>(__instance.m_prefabs);
                s_live = null;
            }
        }

        [HarmonyPatch(typeof(ClutterSystem), "Awake")]
        private static class ClutterSystem_Awake_SnapshotVanillaClutter
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(ClutterSystem __instance)
            {
                if (IsHeadless || __instance == null || __instance.m_clutter == null) return;
                s_vanillaClutter = new List<GameObject>();
                foreach (var clutter in __instance.m_clutter)
                    if (clutter?.m_prefab != null) s_vanillaClutter.Add(clutter.m_prefab);
                s_live = null;
            }
        }
    }
}
