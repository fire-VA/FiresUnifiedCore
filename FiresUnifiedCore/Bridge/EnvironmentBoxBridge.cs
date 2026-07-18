using System;
using UnityEngine;
using FiresCore.Utilities;

namespace FiresCore.Bridge
{
    /// <summary>
    /// Cross-mod seam for the env-box TERRAIN side. The portable env-box engine
    /// (<see cref="EnvironmentBoxController"/>) lives in Core and drives the env/skybox/enclosure atmosphere
    /// itself; the terrain/clutter/snow-on-pieces work — which depends on FiresAdminTerrain's heightmap, clutter,
    /// and snow systems — routes through this provider. FAT registers one provider; while none is registered
    /// every call is a null-safe no-op, so a Core consumer that only wants the dark-crypt atmosphere (no biome
    /// terrain override) needs no FAT at all. Mirrors <see cref="GuildBridge"/>.
    /// </summary>
    public interface IEnvironmentBoxTerrainProvider
    {
        void OnBoxEnter(EnvironmentBoxController box, Heightmap.Biome biome, bool affectsCustomPaint, bool affectsVanillaPaint);
        void OnBoxExit(EnvironmentBoxController box);
        void RefreshTerrainInBounds(Bounds b);
        void RefreshClutterInBounds(Bounds b);
        void RestoreTerrainInBounds(EnvironmentBoxController box);
        void ApplySnowToPieces(int boxId, Bounds b);
        void RemoveSnowFromPieces(int boxId);
    }

    public static class EnvironmentBoxBridge
    {
        private static IEnvironmentBoxTerrainProvider _impl;

        public static bool HasProvider => _impl != null;

        public static void Register(IEnvironmentBoxTerrainProvider impl) => _impl = impl;

        public static void Unregister(IEnvironmentBoxTerrainProvider impl)
        {
            if (_impl == impl) _impl = null;
        }

        // ── provider call wrappers (null-safe; never throw into the controller) ──

        public static void OnBoxEnter(EnvironmentBoxController box, Heightmap.Biome biome, bool affectsCustomPaint, bool affectsVanillaPaint)
        {
            try { _impl?.OnBoxEnter(box, biome, affectsCustomPaint, affectsVanillaPaint); } catch (Exception ex) { Warn(ex); }
        }

        public static void OnBoxExit(EnvironmentBoxController box)
        {
            try { _impl?.OnBoxExit(box); } catch (Exception ex) { Warn(ex); }
        }

        public static void RefreshTerrainInBounds(Bounds b)
        {
            try { _impl?.RefreshTerrainInBounds(b); } catch (Exception ex) { Warn(ex); }
        }

        public static void RefreshClutterInBounds(Bounds b)
        {
            try { _impl?.RefreshClutterInBounds(b); } catch (Exception ex) { Warn(ex); }
        }

        public static void RestoreTerrainInBounds(EnvironmentBoxController box)
        {
            try { _impl?.RestoreTerrainInBounds(box); } catch (Exception ex) { Warn(ex); }
        }

        public static void ApplySnowToPieces(int boxId, Bounds b)
        {
            try { _impl?.ApplySnowToPieces(boxId, b); } catch (Exception ex) { Warn(ex); }
        }

        public static void RemoveSnowFromPieces(int boxId)
        {
            try { _impl?.RemoveSnowFromPieces(boxId); } catch (Exception ex) { Warn(ex); }
        }

        // ── spawn helpers (Core-side: build a persistent env box at runtime) ─────

        /// <summary>
        /// Create a persistent env box by instantiating the REGISTERED ZNetScene prefab named
        /// <paramref name="prefabName"/> at <paramref name="pos"/>. Server-authoritative: spawn on the server — the
        /// box's persistent ZNetView ZDO replicates so every client reconstructs the controller and applies the
        /// atmosphere locally. The prefab MUST be registered into ZNetScene by the owning mod (the FiresMemorial
        /// recipe: bake an empty named prefab, attach a persistent ZNetView + EnvironmentBoxController at
        /// ZNetScene.Awake, RegisterByName). A bare GameObject would NOT replicate (empty/unresolvable prefab hash).
        /// Returns the controller, or null if ZNetScene or the prefab isn't ready.
        /// </summary>
        public static EnvironmentBoxController SpawnBox(string prefabName, Vector3 pos, Vector3 size, string environment,
            EnvironmentBoxController.SkyboxMode skybox, bool force, Heightmap.Biome biome = Heightmap.Biome.None,
            EnvironmentBoxController.VisibilityMode visibility = EnvironmentBoxController.VisibilityMode.Hidden)
        {
            try
            {
                var znet = ZNetScene.instance;
                if (znet == null)
                {
                    Debug.LogWarning("[FiresCore] EnvironmentBoxBridge.SpawnBox: ZNetScene not ready.");
                    return null;
                }

                // ROOT FIX: instantiate the REGISTERED prefab (resolved by the caller-supplied name) instead of a
                // bare new GameObject("FiresEnvBox"). A bare GameObject is not in m_namedPrefabs, so its ZNetView
                // ZDO records an unresolvable prefab hash and never replicates to remote/dedi clients (the empty-
                // AssetID trap) — which is why the crypt interior was a bright void. A registered prefab's ZDO hash
                // resolves on every peer, so the box replicates and each client reconstructs the controller.
                GameObject prefab = znet.GetPrefab(prefabName);
                if (prefab == null)
                {
                    Debug.LogWarning($"[FiresCore] EnvironmentBoxBridge.SpawnBox: prefab '{prefabName}' is not registered in ZNetScene.");
                    return null;
                }

                // Clone via the inactive holder, then FORCE inactive before reparenting so ZNetView.Awake (and the
                // controller Awake) do NOT run until position + config are set; otherwise the persistent ZDO is
                // created at the origin with default config. (InstantiateInactiveClone keeps it inactive only while
                // parented under the holder — SetParent(null) re-activates it.) Mirrors the wild-companion build.
                GameObject go = FiresCore.Net.NetworkObjectHelper.InstantiateInactiveClone(prefab);
                if (go == null) return null;
                go.SetActive(false);
                go.transform.SetParent(null, false);
                go.transform.position = pos;

                var box = go.GetComponent<EnvironmentBoxController>();
                if (box == null) box = go.AddComponent<EnvironmentBoxController>();
                box.BoxSize = size;
                box.EnvironmentName = environment ?? string.Empty;
                box.CurrentSkyboxMode = skybox;
                box.CurrentVisibility = visibility;   // the prefab may default Hidden; the spec decides per dungeon
                box.ForceEnvironment = force;
                box.ForcedBiome = biome;

                go.SetActive(true); // runs ZNetView.Awake (persistent ZDO at the correct pos + resolvable hash) + controller Awake
                box.ApplyConfiguredState();

                float half = size.x * 0.5f;
                Debug.Log($"[ENVBOX-DBG] SpawnBox: prefab='{prefabName}' spawnPos={pos} size={size} " +
                          $"env='{environment}' skybox={skybox} force={force} biome={biome} " +
                          $"worldMin={pos - size * 0.5f} worldMax={pos + size * 0.5f} (half={half:F1}m each axis).");
                return box;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[FiresCore] EnvironmentBoxBridge.SpawnBox failed: {ex}");
                return null;
            }
        }

        public static void SetSize(EnvironmentBoxController box, Vector3 size)
        {
            if (box == null) return;
            try { box.SetSize(size); } catch (Exception ex) { Warn(ex); }
        }

        /// <summary>
        /// Re-center + resize a box so it fully encompasses a world-space AABB (all placed dungeon rooms). Wraps
        /// <see cref="EnvironmentBoxController.ReconfigureBoxToBounds"/>. Null-safe.
        /// </summary>
        public static void ReconfigureBoxToBounds(EnvironmentBoxController box, Vector3 worldCenter, Vector3 size)
        {
            if (box == null) return;
            try { box.ReconfigureBoxToBounds(worldCenter, size); } catch (Exception ex) { Warn(ex); }
        }

        public static void Destroy(EnvironmentBoxController box)
        {
            if (box == null) return;
            try { UnityEngine.Object.Destroy(box.gameObject); } catch (Exception ex) { Warn(ex); }
        }

        private static void Warn(Exception ex) => Debug.LogWarning($"[FiresCore] EnvironmentBoxBridge: {ex.Message}");
    }
}
