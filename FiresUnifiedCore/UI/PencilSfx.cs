using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;

namespace FiresCore.UI
{
    /// <summary>
    /// PencilSfx — pencil / marker "scratch" click sounds for Fires drafting-style UIs (the blueprint bench). Ships
    /// PROCEDURAL defaults (short band-limited shaped-noise scratches — original + license-clean, no download) and
    /// ALSO loads user-supplied 16-bit PCM WAVs from BepInEx/config/FiresUISounds/, so real sourced clips can
    /// override the defaults without a rebuild. PlayClick() plays a random variant (slight pitch variety) through a
    /// cached 2D AudioSource. Client-only. Lives in Core (shared → Core) so any Fires UI can call it.
    ///
    /// The Unity AudioClip/AudioSource API is driven by REFLECTION on purpose: a direct compile-time reference to
    /// UnityEngine.AudioModule drags in its netstandard 2.1 dependency, which breaks the net48 build (ReadOnlySpan
    /// is undefined). Reflection sidesteps that entirely — no AudioModule type appears at compile time.
    /// </summary>
    public static class PencilSfx
    {
        private static bool _init, _failed;
        private static Component _src;                 // AudioSource
        private static readonly List<object> _clips = new List<object>();   // AudioClip instances
        private static readonly System.Random _rng = new System.Random(1337);

        private static Type _tClip, _tSource;
        private static MethodInfo _clipCreate, _clipSetData, _playOneShot;
        private static PropertyInfo _pPitch, _pPlayOnAwake, _pSpatialBlend;

        private static bool IsClientOnly() => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

        /// <summary>Play a random pencil-scratch click. Safe from any UI button handler.</summary>
        public static void PlayClick(float volume = 0.55f)
        {
            if (IsClientOnly()) return;
            EnsureInit();
            if (_failed || _src == null || _clips.Count == 0) return;
            try
            {
                _pPitch?.SetValue(_src, 0.93f + (float)_rng.NextDouble() * 0.14f);
                _playOneShot.Invoke(_src, new object[] { _clips[_rng.Next(_clips.Count)], volume });
            }
            catch { }
        }

        private static void EnsureInit()
        {
            if (_init) return;
            _init = true;
            try
            {
                _tClip = Type.GetType("UnityEngine.AudioClip, UnityEngine.AudioModule") ?? Type.GetType("UnityEngine.AudioClip, UnityEngine");
                _tSource = Type.GetType("UnityEngine.AudioSource, UnityEngine.AudioModule") ?? Type.GetType("UnityEngine.AudioSource, UnityEngine");
                if (_tClip == null || _tSource == null) { _failed = true; return; }
                _clipCreate = _tClip.GetMethod("Create", new[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(bool) });
                _clipSetData = _tClip.GetMethod("SetData", new[] { typeof(float[]), typeof(int) });
                _playOneShot = _tSource.GetMethod("PlayOneShot", new[] { _tClip, typeof(float) });
                _pPitch = _tSource.GetProperty("pitch");
                _pPlayOnAwake = _tSource.GetProperty("playOnAwake");
                _pSpatialBlend = _tSource.GetProperty("spatialBlend");
                if (_clipCreate == null || _clipSetData == null || _playOneShot == null) { _failed = true; return; }

                var go = new GameObject("Fires_PencilSfx");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _src = go.AddComponent(_tSource);
                _pPlayOnAwake?.SetValue(_src, false);
                _pSpatialBlend?.SetValue(_src, 0f);

                LoadUserClips();
                if (_clips.Count == 0)
                    for (int i = 0; i < 3; i++)
                    {
                        var clip = MakeScratch(22050, 0.07f + i * 0.02f, i);
                        if (clip != null) _clips.Add(clip);
                    }
                Debug.Log($"[FiresCore] PencilSfx ready ({_clips.Count} clip(s)).");
            }
            catch (Exception ex) { _failed = true; Debug.LogWarning($"[FiresCore] PencilSfx init failed: {ex.Message}"); }
        }

        private static void LoadUserClips()
        {
            try
            {
                string dir = Path.Combine(Paths.ConfigPath, "FiresUISounds");
                if (!Directory.Exists(dir)) return;
                foreach (var wavPath in Directory.GetFiles(dir, "*.wav"))
                {
                    if (WavLoader.TryLoad(wavPath, out float[] buf, out int channels, out int sampleRate))
                    {
                        var clip = MakeClip(Path.GetFileNameWithoutExtension(wavPath), buf, channels, sampleRate);
                        if (clip != null) _clips.Add(clip);
                    }
                }
            }
            catch { }
        }

        private static object MakeClip(string name, float[] buf, int channels, int sampleRate)
        {
            channels = Mathf.Max(1, channels);
            var clip = _clipCreate.Invoke(null, new object[] { name, buf.Length / channels, channels, sampleRate, false });
            _clipSetData.Invoke(clip, new object[] { buf, 0 });
            return clip;
        }

        // Procedural pencil scratch: white noise band-limited to the mid-high "hiss" range (one-pole lowpass then
        // highpass), a fast attack + exponential decay, and an amplitude "stroke" wobble so it reads as a scribble.
        private static object MakeScratch(int sampleRate, float durSec, int seed)
        {
            int sampleCount = Mathf.Max(64, (int)(durSec * sampleRate));
            var buf = new float[sampleCount];
            var rng = new System.Random(4242 + seed * 17);
            float lowPass = 0f, previousLowPass = 0f, highPass = 0f;
            const float lpCoef = 0.5f, hpCoef = 0.86f;
            float strokeHz = 42f + seed * 13f;
            for (int i = 0; i < sampleCount; i++)
            {
                float time = (float)i / sampleRate;
                float x = (float)(rng.NextDouble() * 2.0 - 1.0);
                lowPass += lpCoef * (x - lowPass);
                highPass = hpCoef * (highPass + lowPass - previousLowPass);
                previousLowPass = lowPass;
                float attack = Mathf.Clamp01(time / 0.004f);
                float decay = Mathf.Exp(-time / (durSec * 0.35f));
                float wob = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(2f * Mathf.PI * strokeHz * time));
                buf[i] = highPass * attack * decay * wob * 0.9f;
            }
            return MakeClip($"pencil_scratch_{seed}", buf, 1, sampleRate);
        }
    }

    /// <summary>Minimal 16-bit PCM WAV parser → normalized float samples + format. Returns false on any parse error
    /// or unsupported format (only 16-bit PCM). The AudioClip is built by the caller via reflection.</summary>
    internal static class WavLoader
    {
        public static bool TryLoad(string path, out float[] samples, out int channels, out int sampleRate)
        {
            samples = null; channels = 0; sampleRate = 0;
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < 44 || data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return false;
                channels = BitConverter.ToInt16(data, 22);
                sampleRate = BitConverter.ToInt32(data, 24);
                int bits = BitConverter.ToInt16(data, 34);
                if (bits != 16 || channels < 1) return false;

                int chunkOffset = 12, dataOffset = -1, dataLength = 0;
                while (chunkOffset + 8 <= data.Length)
                {
                    int len = BitConverter.ToInt32(data, chunkOffset + 4);
                    if (data[chunkOffset] == 'd' && data[chunkOffset + 1] == 'a' && data[chunkOffset + 2] == 't' && data[chunkOffset + 3] == 'a') { dataOffset = chunkOffset + 8; dataLength = len; break; }
                    chunkOffset += 8 + len + (len & 1);
                }
                if (dataOffset < 0) return false;
                dataLength = Mathf.Min(dataLength, data.Length - dataOffset);

                int total = dataLength / 2;
                samples = new float[total];
                for (int i = 0; i < total; i++)
                    samples[i] = BitConverter.ToInt16(data, dataOffset + i * 2) / 32768f;
                return true;
            }
            catch { return false; }
        }
    }
}
