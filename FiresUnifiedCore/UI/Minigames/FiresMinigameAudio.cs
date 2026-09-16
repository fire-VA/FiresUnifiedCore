using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FiresCore.UI.Minigames
{
    /// <summary>
    /// Reusable 2D-UI sound bank for the Fires minigame overlays. A game makes one with its own assembly, registers
    /// named clips (embedded 16-bit PCM WAVs, or synthesized samples), then plays one-shots (optionally pitched) and
    /// drives ONE intensity loop (the lock-pick scrape / dice rattle). Generalized from FDM's LockpickAudio.
    ///
    /// Two hard-won contracts are baked in so no minigame re-hits them:
    ///  • REFLECTION on the Unity AudioClip/AudioSource API — a direct compile-time ref to UnityEngine.AudioModule
    ///    drags in netstandard 2.1 and breaks the net48 build.
    ///  • 2D-UI routing (vanilla ZSFX): bypassReverbZones=true + outputAudioMixerGroup = AudioMan.m_guiMixer, or a
    ///    dungeon's reverb zone smears every click into a distant echo and the player's volume sliders are ignored.
    /// Client-only; every method is a no-op on a headless server or if init failed.
    /// </summary>
    public sealed class FiresMinigameAudio
    {
        private const float DefaultGain = 2.0f;   // matches the Fires SFX loudness baseline (applied to samples, clamped)

        // Reflection surface is process-wide → static, resolved once.
        private static bool _reflectResolved, _reflectFailed;
        private static Type _tClip, _tSource;
        private static MethodInfo _clipCreate, _clipSetData, _playOneShot, _play, _stop;
        private static PropertyInfo _pClip, _pLoop, _pSpatial, _pPlayOnAwake, _pIsPlaying, _pVolume, _pPitch, _pOutputGroup, _pBypassReverb;

        private readonly Assembly _asm;
        private readonly Dictionary<string, object> _clips = new Dictionary<string, object>();
        private GameObject _go;
        private Component _oneShot, _pitchShot, _loopSrc;
        private bool _failed, _routed;

        public FiresMinigameAudio(Assembly asm)
        {
            _asm = asm ?? Assembly.GetCallingAssembly();
            Init();
        }

        private static bool IsClientOnly() => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

        /// <summary>Register an embedded 16-bit PCM WAV under <paramref name="name"/>. Returns false if missing/unsupported.</summary>
        public bool RegisterClip(string name, string endsWith, float gain = DefaultGain)
        {
            if (_failed || string.IsNullOrEmpty(name)) return false;
            try
            {
                string res = null;
                foreach (var resourceName in _asm.GetManifestResourceNames())
                    if (resourceName.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase)) { res = resourceName; break; }
                if (res == null) { FiresCore.Logging.FiresLogger.LogError($"[FiresMinigame] audio '{endsWith}' not embedded in {_asm.GetName().Name}."); return false; }
                byte[] data;
                using (var stream = _asm.GetManifestResourceStream(res)) using (var ms = new System.IO.MemoryStream()) { stream.CopyTo(ms); data = ms.ToArray(); }
                if (!ParseWav(data, gain, out float[] buf, out int channels, out int sampleRate)) return false;
                channels = Mathf.Max(1, channels);
                var clip = _clipCreate.Invoke(null, new object[] { name, buf.Length / channels, channels, sampleRate, false });
                _clipSetData.Invoke(clip, new object[] { buf, 0 });
                _clips[name] = clip;
                return true;
            }
            catch (Exception ex) { FiresCore.Logging.FiresLogger.LogError($"[FiresMinigame] audio register '{endsWith}': {ex.Message}"); return false; }
        }

        /// <summary>Register a synthesized mono clip (e.g. a procedural thud/tick) under <paramref name="name"/>.</summary>
        public bool RegisterSynth(string name, float[] samples, int sampleRate)
        {
            if (_failed || samples == null || samples.Length == 0) return false;
            try
            {
                var clip = _clipCreate.Invoke(null, new object[] { name, samples.Length, 1, sampleRate, false });
                _clipSetData.Invoke(clip, new object[] { samples, 0 });
                _clips[name] = clip;
                return true;
            }
            catch (Exception ex) { FiresCore.Logging.FiresLogger.LogError($"[FiresMinigame] audio synth '{name}': {ex.Message}"); return false; }
        }

        public void PlayOneShot(string name, float volume = 1f)
        {
            if (_failed || _oneShot == null || !_clips.TryGetValue(name, out var clip)) return;
            EnsureRouted();
            try { _playOneShot.Invoke(_oneShot, new object[] { clip, volume }); } catch { }
        }

        /// <summary>Pitched one-shots go through a SECOND source so setting pitch never detunes a clip still ringing.</summary>
        public void PlayPitched(string name, float volume, float pitch)
        {
            if (_failed || _pitchShot == null || !_clips.TryGetValue(name, out var clip)) return;
            EnsureRouted();
            try { _pPitch?.SetValue(_pitchShot, pitch); _playOneShot.Invoke(_pitchShot, new object[] { clip, volume }); } catch { }
        }

        /// <summary>Point the intensity loop at a registered clip (call once before StartLoop/SetLoopIntensity).</summary>
        public void SetLoopClip(string name)
        {
            if (_failed || _loopSrc == null || !_clips.TryGetValue(name, out var clip)) return;
            try { _pClip?.SetValue(_loopSrc, clip); } catch { }
        }

        /// <summary>Drive the loop by a 0..1 intensity — louder + higher-pitched the nearer 1 (the scrape/rattle cue).</summary>
        public void SetLoopIntensity(float t, float volMin = 0.16f, float volMax = 1f, float pitchMin = 0.9f, float pitchMax = 1.32f)
        {
            if (_failed || _loopSrc == null || _pIsPlaying == null) return;
            EnsureRouted();
            t = Mathf.Clamp01(t);
            try
            {
                _pVolume?.SetValue(_loopSrc, Mathf.Lerp(volMin, volMax, t));
                _pPitch?.SetValue(_loopSrc, Mathf.Lerp(pitchMin, pitchMax, t));
                if (!(bool)_pIsPlaying.GetValue(_loopSrc)) _play.Invoke(_loopSrc, null);
            }
            catch { }
        }

        public void StopLoop()
        {
            if (_loopSrc == null || _pIsPlaying == null || _stop == null) return;
            try { if ((bool)_pIsPlaying.GetValue(_loopSrc)) _stop.Invoke(_loopSrc, null); } catch { }
        }

        /// <summary>Tear down the audio host GameObject. Call when the minigame overlay closes.</summary>
        public void Dispose()
        {
            StopLoop();
            if (_go != null) { UnityEngine.Object.Destroy(_go); _go = null; }
            _oneShot = _pitchShot = _loopSrc = null;
        }

        private void Init()
        {
            if (IsClientOnly()) { _failed = true; return; }
            ResolveReflection();
            if (_reflectFailed) { _failed = true; return; }
            try
            {
                _go = new GameObject("FiresMinigameAudio");
                UnityEngine.Object.DontDestroyOnLoad(_go);
                _oneShot = MakeSource(); _pitchShot = MakeSource();
                _loopSrc = MakeSource(); _pLoop?.SetValue(_loopSrc, true);
                EnsureRouted();
            }
            catch (Exception ex) { _failed = true; FiresCore.Logging.FiresLogger.LogWarning($"[FiresMinigame] audio init failed: {ex.Message}"); }
        }

        private Component MakeSource()
        {
            var src = _go.AddComponent(_tSource);
            _pPlayOnAwake?.SetValue(src, false);
            _pSpatial?.SetValue(src, 0f);
            return src;
        }

        // 2D-UI routing: bypass world reverb zones + route through AudioMan's GUI mixer (retries until AudioMan is up).
        private void EnsureRouted()
        {
            if (_routed || _failed) return;
            try
            {
                _pBypassReverb?.SetValue(_oneShot, true);
                _pBypassReverb?.SetValue(_pitchShot, true);
                _pBypassReverb?.SetValue(_loopSrc, true);
                var man = AudioMan.instance;
                object gui = man != null ? typeof(AudioMan).GetField("m_guiMixer")?.GetValue(man) : null;
                if (gui == null) return;
                _pOutputGroup?.SetValue(_oneShot, gui);
                _pOutputGroup?.SetValue(_pitchShot, gui);
                _pOutputGroup?.SetValue(_loopSrc, gui);
                _routed = true;
            }
            catch { _routed = true; }
        }

        private static void ResolveReflection()
        {
            if (_reflectResolved) return;
            _reflectResolved = true;
            try
            {
                _tClip = Type.GetType("UnityEngine.AudioClip, UnityEngine.AudioModule") ?? Type.GetType("UnityEngine.AudioClip, UnityEngine");
                _tSource = Type.GetType("UnityEngine.AudioSource, UnityEngine.AudioModule") ?? Type.GetType("UnityEngine.AudioSource, UnityEngine");
                if (_tClip == null || _tSource == null) { _reflectFailed = true; return; }
                _clipCreate = _tClip.GetMethod("Create", new[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(bool) });
                _clipSetData = _tClip.GetMethod("SetData", new[] { typeof(float[]), typeof(int) });
                _playOneShot = _tSource.GetMethod("PlayOneShot", new[] { _tClip, typeof(float) });
                _play = _tSource.GetMethod("Play", Type.EmptyTypes);
                _stop = _tSource.GetMethod("Stop", Type.EmptyTypes);
                _pClip = _tSource.GetProperty("clip");
                _pLoop = _tSource.GetProperty("loop");
                _pSpatial = _tSource.GetProperty("spatialBlend");
                _pPlayOnAwake = _tSource.GetProperty("playOnAwake");
                _pIsPlaying = _tSource.GetProperty("isPlaying");
                _pVolume = _tSource.GetProperty("volume");
                _pPitch = _tSource.GetProperty("pitch");
                _pOutputGroup = _tSource.GetProperty("outputAudioMixerGroup");
                _pBypassReverb = _tSource.GetProperty("bypassReverbZones");
                if (_clipCreate == null || _clipSetData == null || _playOneShot == null || _play == null || _stop == null) _reflectFailed = true;
            }
            catch { _reflectFailed = true; }
        }

        // Minimal 16-bit PCM WAV parse (bytes) → gained/clamped float samples + format. Mirrors FiresCore.UI.WavLoader.
        private static bool ParseWav(byte[] data, float gain, out float[] samples, out int channels, out int sampleRate)
        {
            samples = null; channels = 0; sampleRate = 0;
            if (data == null || data.Length < 44 || data[0] != 'R' || data[1] != 'I' || data[2] != 'F' || data[3] != 'F') return false;
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
                samples[i] = Mathf.Clamp(BitConverter.ToInt16(data, dataOffset + i * 2) / 32768f * gain, -1f, 1f);
            return true;
        }

        // ── Procedural fallbacks (PencilSfx idiom) — a dull thud and a short tick, for games with no sourced clip. ──
        public static float[] MakeThud(int sampleRate = 22050, float dur = 0.09f)
        {
            int sampleCount = (int)(sampleRate * dur);
            var buf = new float[sampleCount];
            var rng = new System.Random(90210);
            float phase = 0f;
            for (int i = 0; i < sampleCount; i++)
            {
                float time = (float)i / sampleRate;
                float frequency = 60f + 95f * Mathf.Exp(-time * 34f);
                phase += 2f * Mathf.PI * frequency / sampleRate;
                float noise = time < 0.006f ? (float)(rng.NextDouble() * 2.0 - 1.0) * 0.5f * (1f - time / 0.006f) : 0f;
                buf[i] = Mathf.Clamp((Mathf.Sin(phase) * 0.9f + noise) * Mathf.Exp(-time / 0.028f), -1f, 1f);
            }
            return buf;
        }

        public static float[] MakeTick(int sampleRate = 22050, float dur = 0.03f, float freq = 1150f)
        {
            int sampleCount = (int)(sampleRate * dur);
            var buf = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                float time = (float)i / sampleRate;
                buf[i] = Mathf.Sin(2f * Mathf.PI * freq * time) * Mathf.Exp(-time / 0.005f) * 0.7f;
            }
            return buf;
        }
    }
}
