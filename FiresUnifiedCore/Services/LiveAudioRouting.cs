using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FiresCore.Services
{
    /// <summary>
    /// Routes mod and ripped AudioSources to the live game's mixer groups, by reflection only because a net48
    /// Fires mod cannot reference AudioModule types. Bundle sources otherwise keep their serialized reference to
    /// the ripped duplicate mixer and ignore the volume sliders, and raw 2D UI sources pick up dungeon reverb.
    /// World sounds only get their mixer group rebound; UI sounds also get the GUI group and bypass reverb
    /// zones, as ZSFX does for vanilla 2D sounds.
    /// </summary>
    public static class LiveAudioRouting
    {
        private static Type _tAudioSource;
        private static PropertyInfo _pOutputGroup;
        private static PropertyInfo _pBypassReverb;
        private static bool _reflected;

        // The game's world-SFX mixer group, harvested by majority vote across every AudioSource on
        // ZNetScene's prefabs — sfx prefabs dominate that set, so the most-referenced group IS the
        // live SFX group. Name-agnostic, so a vanilla mixer rename can't silently break it.
        private static object _liveSfxGroup;
        private static object _liveGuiGroup;

        private static void EnsureReflection()
        {
            if (_reflected) return;
            _reflected = true;
            _tAudioSource = Type.GetType("UnityEngine.AudioSource, UnityEngine.AudioModule");
            if (_tAudioSource == null) return;
            _pOutputGroup = _tAudioSource.GetProperty("outputAudioMixerGroup");
            _pBypassReverb = _tAudioSource.GetProperty("bypassReverbZones");
        }

        private static void HarvestSfxGroup()
        {
            if (_liveSfxGroup != null) return;
            var scene = ZNetScene.instance;
            if (scene == null || scene.m_prefabs == null || scene.m_prefabs.Count == 0) return;
            if (_tAudioSource == null || _pOutputGroup == null) return;

            var counts = new Dictionary<object, int>();
            for (int i = 0; i < scene.m_prefabs.Count; i++)
            {
                var prefab = scene.m_prefabs[i];
                if (prefab == null) continue;
                var sources = prefab.GetComponentsInChildren(_tAudioSource, true);
                for (int sourceIndex = 0; sourceIndex < sources.Length; sourceIndex++)
                {
                    var grp = _pOutputGroup.GetValue(sources[sourceIndex], null);
                    if (grp == null) continue;
                    counts.TryGetValue(grp, out int count);
                    counts[grp] = count + 1;
                }
            }

            object best = null;
            int bestCount = 0;
            foreach (var kv in counts)
            {
                if (kv.Value <= bestCount) continue;
                best = kv.Key;
                bestCount = kv.Value;
            }
            _liveSfxGroup = best;
            if (best is UnityEngine.Object unityObject)
                Debug.Log($"[LiveAudioRouting] live SFX mixer group = '{unityObject.name}' ({bestCount} vanilla sources)");
        }

        /// <summary>
        /// Rebinds every AudioSource under <paramref name="root"/> to the live world-SFX mixer group
        /// (volume sliders + correct processing). Reverb-zone behavior is left alone — 3D world
        /// sounds are supposed to take zone reverb. Returns the number of sources rebound.
        /// </summary>
        public static int RouteWorldSfx(GameObject root)
        {
            if (root == null) return 0;
            EnsureReflection();
            if (_tAudioSource == null || _pOutputGroup == null) return 0;
            HarvestSfxGroup();
            if (_liveSfxGroup == null) return 0;

            int routed = 0;
            var sources = root.GetComponentsInChildren(_tAudioSource, true);
            for (int i = 0; i < sources.Length; i++)
            {
                var current = _pOutputGroup.GetValue(sources[i], null);
                if (ReferenceEquals(current, _liveSfxGroup)) continue;
                _pOutputGroup.SetValue(sources[i], _liveSfxGroup, null);
                routed++;
            }
            return routed;
        }

        /// <summary>
        /// Routes every AudioSource under <paramref name="root"/> as UI audio: GUI mixer group
        /// (AudioMan.m_guiMixer — GuiVol slider) + bypassReverbZones=true so dungeon reverb zones
        /// can't echo interface clicks. AudioMan.instance can be null very early — call again on
        /// use until it sticks. Returns the number of sources touched.
        /// </summary>
        public static int RouteUiSfx(GameObject root)
        {
            if (root == null) return 0;
            EnsureReflection();
            if (_tAudioSource == null || _pBypassReverb == null) return 0;

            if (_liveGuiGroup == null && AudioMan.instance != null)
                _liveGuiGroup = typeof(AudioMan).GetField("m_guiMixer")?.GetValue(AudioMan.instance);

            int routed = 0;
            var sources = root.GetComponentsInChildren(_tAudioSource, true);
            for (int i = 0; i < sources.Length; i++)
            {
                _pBypassReverb.SetValue(sources[i], true, null);
                if (_liveGuiGroup != null && _pOutputGroup != null)
                {
                    var current = _pOutputGroup.GetValue(sources[i], null);
                    if (!ReferenceEquals(current, _liveGuiGroup))
                        _pOutputGroup.SetValue(sources[i], _liveGuiGroup, null);
                }
                routed++;
            }
            return routed;
        }
    }
}
