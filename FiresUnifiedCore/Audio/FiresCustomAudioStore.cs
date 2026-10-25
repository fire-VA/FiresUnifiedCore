using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace FiresCore.Audio
{
    /// <summary>
    /// Loads custom .mp3 files from a config folder into playable AudioClips, keyed by filename (no
    /// extension) — mirroring the KG Marketplace AssetStorage pattern. Client-only; a headless/dedicated
    /// server skips the loader. The per-NPC sound setting is only ever used as a dictionary key here
    /// (never to build a filesystem path), so saved NPC data cannot drive path traversal.
    /// </summary>
    public static class FiresCustomAudioStore
    {
        public static readonly Dictionary<string, AudioClip> Clips =
            new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);

        public static bool Loaded { get; private set; }

        // .wav and .ogg load too (AudioType from the extension), and RPGmaker can reload after a server sync.
        private static readonly string[] Extensions = { ".mp3", ".wav", ".ogg" };

        /// <summary>Raised on the main thread after <see cref="ReloadCoroutine"/> or <see cref="LoadFileCoroutine"/> finishes.</summary>
        public static event Action Reloaded;

        private static AudioType TypeFor(string file)
        {
            switch (Path.GetExtension(file).ToLowerInvariant())
            {
                case ".wav": return AudioType.WAV;
                case ".ogg": return AudioType.OGGVORBIS;
                case ".mp3": return AudioType.MPEG;
                default: return AudioType.UNKNOWN;
            }
        }

        private static bool Headless => SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null;

        /// <summary>Loads every *.mp3 / *.wav / *.ogg under <paramref name="folder"/> (recursive) into <see cref="Clips"/>.</summary>
        public static IEnumerator LoadAllCoroutine(string folder)
        {
            Loaded = true;
            if (Headless)
                yield break; // headless / dedicated server: no audio output, skip.
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                yield break;

            List<string> files;
            try
            {
                files = new List<string>();
                foreach (var f in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories))
                    if (Array.IndexOf(Extensions, Path.GetExtension(f).ToLowerInvariant()) >= 0) files.Add(f);
            }
            catch (Exception ex) { Debug.LogWarning($"[FiresAudio] enumerate '{folder}': {ex.Message}"); yield break; }

            foreach (var file in files)
                yield return LoadOne(file);
            Debug.Log($"[FiresAudio] custom audio store ready — {Clips.Count} clip(s) from {folder}");
        }

        /// <summary>Clears <see cref="Clips"/> and loads <paramref name="folder"/> again, then raises <see cref="Reloaded"/>.
        /// Run with StartCoroutine on any MonoBehaviour.</summary>
        public static IEnumerator ReloadCoroutine(string folder)
        {
            if (!Headless) Clips.Clear();
            yield return LoadAllCoroutine(folder);
            try { Reloaded?.Invoke(); } catch (Exception ex) { Debug.LogWarning($"[FiresAudio] Reloaded handler: {ex.Message}"); }
        }

        /// <summary>Loads or replaces one file's clip (key = filename without extension), then raises <see cref="Reloaded"/>.</summary>
        public static IEnumerator LoadFileCoroutine(string path)
        {
            if (Headless || string.IsNullOrEmpty(path) || !File.Exists(path)) yield break;
            yield return LoadOne(path);
            try { Reloaded?.Invoke(); } catch (Exception ex) { Debug.LogWarning($"[FiresAudio] Reloaded handler: {ex.Message}"); }
        }

        private static IEnumerator LoadOne(string file)
        {
            var type = TypeFor(file);
            if (type == AudioType.UNKNOWN) yield break;
            using (var req = UnityWebRequestMultimedia.GetAudioClip(file, type))
            {
                yield return req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[FiresAudio] failed to load '{file}': {req.error}");
                    yield break;
                }
                AudioClip clip = null;
                try { clip = DownloadHandlerAudioClip.GetContent(req); }
                catch (Exception ex) { Debug.LogWarning($"[FiresAudio] decode '{file}': {ex.Message}"); }
                if (clip != null)
                {
                    clip.name = Path.GetFileNameWithoutExtension(file);
                    Clips[clip.name] = clip;
                    Debug.Log($"[FiresAudio] loaded custom sound '{clip.name}' ({type})");
                }
            }
        }
    }
}
