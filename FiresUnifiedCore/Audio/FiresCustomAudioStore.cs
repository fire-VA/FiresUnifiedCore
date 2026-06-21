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

        /// <summary>Loads every *.mp3 under <paramref name="folder"/> (recursive) into <see cref="Clips"/>.</summary>
        public static IEnumerator LoadAllCoroutine(string folder)
        {
            Loaded = true;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
                yield break; // headless / dedicated server: no audio output, skip.
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                yield break;

            string[] files;
            try { files = Directory.GetFiles(folder, "*.mp3", SearchOption.AllDirectories); }
            catch (Exception ex) { Debug.LogWarning($"[FiresAudio] enumerate '{folder}': {ex.Message}"); yield break; }

            foreach (var file in files)
            {
                using (var req = UnityWebRequestMultimedia.GetAudioClip(file, AudioType.MPEG))
                {
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogWarning($"[FiresAudio] failed to load '{file}': {req.error}");
                        continue;
                    }
                    AudioClip clip = null;
                    try { clip = DownloadHandlerAudioClip.GetContent(req); }
                    catch (Exception ex) { Debug.LogWarning($"[FiresAudio] decode '{file}': {ex.Message}"); }
                    if (clip != null)
                    {
                        clip.name = Path.GetFileNameWithoutExtension(file);
                        Clips[clip.name] = clip;
                        Debug.Log($"[FiresAudio] loaded custom sound '{clip.name}'");
                    }
                }
            }
            Debug.Log($"[FiresAudio] custom audio store ready — {Clips.Count} clip(s) from {folder}");
        }
    }
}
