using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using FiresCore.Lifecycle;

using FiresCore.Logging;
namespace FiresCore.Npc
{
    // Renders every loaded NPC prefab once, off-screen, just after the player spawns in, so Unity compiles
    // shader variants and uploads meshes then instead of hitching the first frame each NPC appears. Clones
    // are built under an inactive root with every MonoBehaviour stripped before activation, so no Awake,
    // RPC or registration runs; a dedicated camera renders them on a private layer and they are destroyed.
    // The GPU-side caches survive. The work is spread across frames.
    public static class VANpcShaderPrewarm
    {
        // Dedicated culling layer. 30 picked because FAP's icon renderer
        // uses 31 — pairing them avoids stomping on each other if both run
        // in the same frame (unlikely, but cheap defense).
        private const int PrewarmLayer = 30;

        // Remote stage position — far from any active zone so ambient
        // lighting from the player's current biome doesn't bleed into the
        // shader-compilation pass. Same idea as VAPieceIconRenderer.
        private static readonly Vector3 StagePosition = new Vector3(-9000f, 3000f, -9000f);

        // Per-frame budget. NPC prefabs are heavy (skinned meshes + cloth)
        // so we yield more aggressively than the icon renderer does.
        // 1 per frame keeps per-frame cost predictable; over 30 NPCs that's
        // half a second of total prewarm, well-tolerated post-spawn.
        private const int PrewarmsPerFrame = 1;

        private static bool _initialized;
        private static bool _prewarmComplete;
        private static MonoBehaviour _host;

        // One-shot init. Idempotent — second call no-ops. Wire from plugin
        // Awake.
        public static void Init()
        {
            if (_initialized) return;
            _initialized = true;

            // Coroutine host. DontDestroyOnLoad so it survives world reloads.
            var go = new GameObject("VANpcShaderPrewarmHost");
            UnityEngine.Object.DontDestroyOnLoad(go);
            _host = go.AddComponent<HostBehaviour>();

            // Gate on PlayerSpawnGate — same predicate the icon renderer
            // uses. By the time it fires, the loading-screen is closed,
            // the player is in-world, and zone-streaming has settled.
            // That's the right window to spend a couple hundred ms on
            // shader/mesh prewarm.
            PlayerSpawnGate.RunWhenLocalReady(600f, OnLocalPlayerReady);
        }

        private class HostBehaviour : MonoBehaviour { }

        private static void OnLocalPlayerReady(Player player)
        {
            if (_prewarmComplete) return;
            if (FiresLogger.VerboseEnabled)
                Debug.Log($"[NPC Prewarm] Local player ready ('{player?.GetPlayerName()}'); scheduling prewarm pass.");
            _host.StartCoroutine(PrewarmCoroutine());
        }

        private static IEnumerator PrewarmCoroutine()
        {
            // 2-second slack so we don't compete with the spawn-finalization
            // frame, NPC-renderer first-paint, or any other startup hitches.
            // Half a second is too little if VAGN auto-tune is firing in
            // the same window; 2s is plenty.
            for (int i = 0; i < 120; i++) yield return null;

            // Pull the prefab list. Empty/null = nothing to do.
            List<GameObject> prefabs;
            try { prefabs = CompanionPrefabManager.GetLoadedCompanionPrefabs(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NPC Prewarm] GetLoadedCompanionPrefabs threw: {ex.Message}");
                _prewarmComplete = true;
                yield break;
            }

            if (prefabs == null || prefabs.Count == 0)
            {
                if (FiresLogger.VerboseEnabled)
                    Debug.Log("[NPC Prewarm] No NPC prefabs loaded — nothing to prewarm.");
                _prewarmComplete = true;
                yield break;
            }

            // Set up the off-screen camera + RT. Small RT (64×64) because
            // we don't care about the pixels — only that Render() runs and
            // forces shader/mesh GPU upload.
            GameObject camGo = null;
            Camera cam = null;
            RenderTexture renderTexture = null;
            GameObject stagingRoot = null;
            float startTime = Time.realtimeSinceStartup;
            int prewarmed = 0;
            int failed = 0;

            try
            {
                camGo = new GameObject("VANpcPrewarmCam");
                UnityEngine.Object.DontDestroyOnLoad(camGo);
                cam = camGo.AddComponent<Camera>();
                cam.enabled = false;             // we drive Render() manually
                cam.cullingMask = 1 << PrewarmLayer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 100f;
                cam.fieldOfView = 35f;
                cam.depth = -30;

                renderTexture = new RenderTexture(64, 64, 16, RenderTextureFormat.ARGB32) { name = "VANpcPrewarmRT" };
                renderTexture.Create();
                cam.targetTexture = renderTexture;

                stagingRoot = new GameObject("VANpcPrewarmInactiveStage");
                stagingRoot.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(stagingRoot);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NPC Prewarm] Setup failed: {ex.Message} — aborting prewarm.");
                CleanupResources(camGo, renderTexture, stagingRoot);
                _prewarmComplete = true;
                yield break;
            }

            // Iterate prefabs. Yielding the IEnumerator from inside a
            // try/catch isn't allowed in C#, so we structure each prefab's
            // work as a non-yielding helper that returns success/failure;
            // the main loop yields between calls.
            int prefabsThisFrame = 0;
            for (int i = 0; i < prefabs.Count; i++)
            {
                var prefab = prefabs[i];
                if (prefab == null) continue;

                bool ok = PrewarmOne(prefab, cam, stagingRoot);
                if (ok) prewarmed++; else failed++;

                prefabsThisFrame++;
                if (prefabsThisFrame >= PrewarmsPerFrame)
                {
                    prefabsThisFrame = 0;
                    yield return null;
                }
            }

            CleanupResources(camGo, renderTexture, stagingRoot);
            _prewarmComplete = true;

            Debug.Log($"[NPC Prewarm] Done — prewarmed {prewarmed} NPC prefab(s), "
                + $"{failed} failed, in {(Time.realtimeSinceStartup - startTime):F1}s.");
        }

        // Prewarm ONE prefab. Non-yielding so the caller can wrap in
        // try/catch and yield freely. Returns true on success.
        private static bool PrewarmOne(GameObject prefab, Camera cam, GameObject stagingRoot)
        {
            GameObject clone = null;
            try
            {
                // STEP 1: instantiate INTO the inactive staging root so no
                // Awake fires on any cloned component. This is the same
                // safe-clone pattern FAP's VAPieceIconRenderer uses.
                clone = UnityEngine.Object.Instantiate(prefab, stagingRoot.transform);
                clone.name = "Prewarm_" + prefab.name;

                // STEP 2: strip all MonoBehaviours. With the parent inactive,
                // no Awake has fired yet — DestroyImmediate is purely a
                // component removal here. Anything in assembly_valheim
                // (Character, Humanoid, ZNetView, MonsterAI, ...) gone.
                // Mod-added MonoBehaviours from bundles also stripped. What
                // survives: pure Unity-engine components (Renderer subclasses,
                // MeshFilter, SkinnedMeshRenderer, Cloth, Animator, Light,
                // AudioSource — all benign for a one-frame render).
                var monos = clone.GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
                for (int i = 0; i < monos.Length; i++)
                {
                    var behaviour = monos[i];
                    if (behaviour == null) continue;
                    try { UnityEngine.Object.DestroyImmediate(behaviour); }
                    catch { /* RequireComponent guards — ignore */ }
                }

                // STEP 3: also strip Colliders (no physics interactions
                // wanted) and AudioSources (so any OnEnable Play() bursts
                // stay silent).
                var colliders = clone.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++)
                {
                    try { UnityEngine.Object.DestroyImmediate(colliders[i]); } catch { }
                }
                var audios = clone.GetComponentsInChildren<AudioSource>(true);
                for (int i = 0; i < audios.Length; i++)
                {
                    var audioSource = audios[i];
                    if (audioSource == null) continue;
                    audioSource.playOnAwake = false;
                    audioSource.mute = true;
                    audioSource.enabled = false;
                }

                // STEP 4: tag the hierarchy with PrewarmLayer so only our
                // camera sees it, then reparent + activate.
                SetLayerRecursive(clone.transform, PrewarmLayer);
                clone.transform.SetParent(null, worldPositionStays: false);
                clone.transform.position = StagePosition;
                clone.transform.rotation = Quaternion.identity;
                clone.SetActive(true);

                // STEP 5: position the camera to see the clone, then
                // render. The render call forces Unity to:
                //   * compile every shader variant required by the
                //     materials on the visible renderers
                //   * upload skinned/static mesh data to the GPU
                //   * upload any sampled textures to the GPU
                // After Render() returns, those caches persist even after
                // we destroy the clone.
                cam.transform.position = StagePosition + new Vector3(0, 0.5f, -2.5f);
                cam.transform.LookAt(StagePosition);
                cam.Render();

                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NPC Prewarm] Failed on '{prefab?.name ?? "<null>"}': {ex.Message}");
                return false;
            }
            finally
            {
                if (clone != null)
                {
                    try { UnityEngine.Object.DestroyImmediate(clone); }
                    catch { /* destroying-while-being-destroyed — ignore */ }
                }
            }
        }

        private static void SetLayerRecursive(Transform root, int layer)
        {
            if (root == null) return;
            root.gameObject.layer = layer;
            for (int i = 0; i < root.childCount; i++)
                SetLayerRecursive(root.GetChild(i), layer);
        }

        private static void CleanupResources(GameObject camGo, RenderTexture renderTexture, GameObject stagingRoot)
        {
            try
            {
                if (renderTexture != null) { renderTexture.Release(); UnityEngine.Object.Destroy(renderTexture); }
            }
            catch { }
            try
            {
                if (camGo != null) UnityEngine.Object.Destroy(camGo);
            }
            catch { }
            try
            {
                if (stagingRoot != null) UnityEngine.Object.Destroy(stagingRoot);
            }
            catch { }
        }
    }
}
