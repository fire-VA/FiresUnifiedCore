using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FiresCore.Services
{
    /// <summary>
    /// Routes mod/ripped AudioSources to the LIVE game's mixer groups. Reflection-only — a net48
    /// Fires mod must never use an AudioModule type at compile time (netstandard 2.1 build break;
    /// canonical pattern: FiresCore.UI.PencilSfx).
    ///
    /// Two failure modes this fixes, shared by every mod that ships ripped or hand-built audio
    /// (NPC effect tables, minigame UI clicks, mounts, ...):
    ///  - A bundle AudioSource keeps its serialized reference to the RIPPED duplicate mixer, so it
    ///    ignores the player's volume sliders and plays through whatever effect chain the rip
    ///    carried (heard as wrong-volume or oddly processed sfx). Vanilla never sets the group in
    ///    code — ZSFX relies on the prefab-serialized group — so ripped copies stay mis-routed
    ///    until rebound here.
    ///  - A raw 2D UI source defaults bypassReverbZones=false, so inside a dungeon/crypt reverb
    ///    zone every click gets full cave echo ("sounds far away"). Vanilla's contract: ZSFX sets
    ///    bypassReverbZones=true for all 2D sounds and GUI audio routes through AudioMan.m_guiMixer.
    ///
    /// World sfx keep bypassReverbZones as-is (3D sounds SHOULD take zone reverb, ZSFX manages the
    /// mix per-frame) — only the mixer group is rebound. UI sfx get the GUI group + reverb bypass.
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
                for (int s = 0; s < sources.Length; s++)
                {
                    var grp = _pOutputGroup.GetValue(sources[s], null);
                    if (grp == null) continue;
                    counts.TryGetValue(grp, out int n);
                    counts[grp] = n + 1;
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
            if (best is UnityEngine.Object uo)
                Debug.Log($"[LiveAudioRouting] live SFX mixer group = '{uo.name}' ({bestCount} vanilla sources)");
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
