using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Shared base setup for networked NPC/character prefabs — the components every Fires NPC needs
    /// (the Marketplace static NPC, companions, …): ZNetView, Rigidbody, collider, transform +
    /// animation sync, and a configured player-faction Humanoid. Companion-specific behavior
    /// (CompanionAI, controllers) layers on top in the companion mod; "Press-E" interaction
    /// (NpcController) layers on in the host mod. Pure vanilla Valheim — no mod dependency, so it
    /// lives in Core and both the Marketplace mod and the companion mod build their NPCs on it.
    /// </summary>
    public static class NpcPrefabSetup
    {
        /// <summary>Add + configure the shared networked-character components on <paramref name="prefab"/>.</summary>
        public static void SetupBaseComponents(GameObject prefab)
        {
            if (prefab == null) return;

            // 1. ZNetView — networking
            var zNetView = prefab.GetComponent<ZNetView>() ?? prefab.AddComponent<ZNetView>();
            zNetView.m_persistent = true;
            zNetView.m_type = ZDO.ObjectType.Default;

            // 2. Rigidbody — physics
            var rigidbody = prefab.GetComponent<Rigidbody>() ?? prefab.AddComponent<Rigidbody>();
            rigidbody.mass = 50f;
            rigidbody.useGravity = true;
            rigidbody.isKinematic = false;
            rigidbody.constraints = RigidbodyConstraints.FreezeRotation;
            rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            // 3. CapsuleCollider — collision
            var collider = prefab.GetComponent<CapsuleCollider>() ?? prefab.AddComponent<CapsuleCollider>();
            collider.height = 1.8f;
            collider.radius = 0.3f;
            collider.center = new Vector3(0, 0.9f, 0);

            // 4. ZSyncTransform — network position
            var syncTransform = prefab.GetComponent<ZSyncTransform>() ?? prefab.AddComponent<ZSyncTransform>();
            syncTransform.m_syncPosition = true;
            syncTransform.m_syncRotation = true;
            syncTransform.m_syncScale = false;
            syncTransform.m_syncBodyVelocity = true;

            // 5. ZSyncAnimation — network animation (only when an Animator is present)
            if (prefab.GetComponentInChildren<Animator>(true) != null && prefab.GetComponent<ZSyncAnimation>() == null)
                prefab.AddComponent<ZSyncAnimation>();

            // 6. Humanoid — equipment + combat base
            var humanoid = prefab.GetComponent<Humanoid>() ?? prefab.AddComponent<Humanoid>();
            ConfigureHumanoid(humanoid, rigidbody);
        }

        /// <summary>Apply the shared player-faction humanoid defaults (movement, swimming, eye/visual transforms).</summary>
        public static void ConfigureHumanoid(Humanoid humanoid, Rigidbody rigidbody)
        {
            if (humanoid == null) return;
            var prefab = humanoid.gameObject;

            humanoid.m_name = "NPC";
            humanoid.m_group = "player";
            // Dverger (attackable); owner/ally immunity is enforced in code, not by faction.
            humanoid.m_faction = Character.Faction.Dverger;
            humanoid.m_health = 200f;
            humanoid.m_walkSpeed = 2f;
            humanoid.m_speed = 4f;
            humanoid.m_runSpeed = 7f;
            humanoid.m_turnSpeed = 300f;
            humanoid.m_acceleration = 1f;
            humanoid.m_jumpForce = 8f;
            humanoid.m_staggerWhenBlocked = true;
            humanoid.m_staggerDamageFactor = 0.3f;
            humanoid.m_tolerateWater = true;
            humanoid.m_tolerateFire = false;
            humanoid.m_tolerateSmoke = true;
            humanoid.m_canSwim = true;
            humanoid.m_swimSpeed = 2f;
            humanoid.m_swimTurnSpeed = 100f;
            humanoid.m_swimAcceleration = 0.05f;

            // Respect a baked m_eye (FiresNpcPrefabBuilder wires EyePos — vanilla Player convention).
            // EyePos before Head: Character.UpdateEyeRotation writes m_eye.rotation every frame,
            // so pointing it at the Head BONE fights the animator.
            if (humanoid.m_eye == null)
            {
                Transform eyeTransform = FindTransformByNames(prefab.transform, new[] { "EyePos", "Eye", "eye", "Head", "head" });
                if (eyeTransform == null)
                {
                    var eyeObj = new GameObject("EyePos");
                    eyeObj.transform.SetParent(prefab.transform);
                    eyeObj.transform.localPosition = new Vector3(0, 1.6f, 0.1f);
                    eyeObj.transform.localRotation = Quaternion.identity;
                    eyeTransform = eyeObj.transform;
                }
                humanoid.m_eye = eyeTransform;
            }

            Transform visualTransform = FindTransformByNames(prefab.transform, new[] { "Visual", "visual" }) ?? prefab.transform;

            // Character.m_body / m_visual are protected — set via reflection.
            try
            {
                var characterType = typeof(Character);
                var bodyField = characterType.GetField("m_body", BindingFlags.NonPublic | BindingFlags.Instance);
                if (bodyField != null && bodyField.FieldType == typeof(Rigidbody))
                    bodyField.SetValue(humanoid, rigidbody);

                var visualField = characterType.GetField("m_visual", BindingFlags.NonPublic | BindingFlags.Instance);
                if (visualField != null && visualField.FieldType == typeof(Transform))
                    visualField.SetValue(humanoid, visualTransform);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NpcPrefabSetup] Failed to set protected Character fields: {ex.Message}");
            }
        }

        /// <summary>
        /// Swaps baked effect-prefab references (FootStep step table + Character/Humanoid effect lists)
        /// to the LIVE game's prefabs of the same name when ZNetScene has them. The bundle ships
        /// rip-time copies so the prefab is complete on its own; live prefabs route audio through the
        /// game's real mixer and track game updates. Ripped refs stay wherever no live match exists.
        /// </summary>
        public static void RebindEffectPrefabsToLive(GameObject prefab)
        {
            if (prefab == null || ZNetScene.instance == null) return;
            int swapped = 0;
            _repairedMaterials = 0;
            _repairedShaders = 0;
            _reroutedAudio = 0;

            var footStep = prefab.GetComponent<FootStep>();
            if (footStep != null && footStep.m_effects != null)
            {
                foreach (var step in footStep.m_effects)
                {
                    if (step == null || step.m_effectPrefabs == null) continue;
                    for (int i = 0; i < step.m_effectPrefabs.Length; i++)
                        swapped += SwapToLive(ref step.m_effectPrefabs[i]);
                }
            }

            var humanoid = prefab.GetComponent<Humanoid>();
            if (humanoid != null)
            {
                foreach (var list in new[]
                {
                    humanoid.m_hitEffects, humanoid.m_critHitEffects, humanoid.m_backstabHitEffects,
                    humanoid.m_waterEffects, humanoid.m_tarEffects, humanoid.m_slideEffects,
                    humanoid.m_jumpEffects, humanoid.m_flyingContinuousEffect, humanoid.m_lavaHeatEffects,
                    humanoid.m_pickupEffects, humanoid.m_dropEffects, humanoid.m_consumeItemEffects,
                    humanoid.m_equipEffects, humanoid.m_perfectBlockEffect
                })
                {
                    if (list == null || list.m_effectPrefabs == null) continue;
                    foreach (var entry in list.m_effectPrefabs)
                        if (entry != null) swapped += SwapToLive(ref entry.m_prefab);
                }
            }

            if (swapped > 0 || _repairedMaterials > 0 || _repairedShaders > 0 || _reroutedAudio > 0)
                Debug.Log($"[NpcPrefabSetup] {prefab.name}: rebound {swapped} effect refs to live prefabs; " +
                          $"repaired ripped effects in place ({_repairedMaterials} live materials, {_repairedShaders} live shaders, {_reroutedAudio} audio sources → live mixer)");
        }

        private static int SwapToLive(ref GameObject slot)
        {
            if (slot == null) return 0;
            var live = ZNetScene.instance.GetPrefab(slot.name);
            if (live == null || ReferenceEquals(live, slot))
            {
                // No live prefab of that name (most one-shot fx are spawned by direct reference and
                // never registered in ZNetScene) — the ripped copy stays in use, so make it render.
                RepairRippedEffectMaterials(slot);
                return 0;
            }
            slot = live;
            return 1;
        }

        // Ripped effect prefabs that stay in use keep their bundle materials, whose rip-time shaders
        // carry no usable GPU bytecode — they render magenta. Repair them in place from the shared
        // VanillaMaterialCache: the same-name live MATERIAL when one exists (exact vanilla fidelity),
        // else the live SHADER of the same name onto the ripped material. Effect assets are shared
        // across the NPC prefab tables, so each object is repaired once; headless skips (nothing
        // renders there).
        private static readonly HashSet<int> _repairedEffectObjects = new HashSet<int>();
        private static Dictionary<string, Shader> _liveShadersByName;
        private static int _repairedMaterials;
        private static int _repairedShaders;
        private static int _reroutedAudio;

        private static void RepairRippedEffectMaterials(GameObject fx)
        {
            if (fx == null) return;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            if (!_repairedEffectObjects.Add(fx.GetInstanceID())) return;

            // Ripped sfx also serialize their AudioSources against the RIPPED duplicate mixer —
            // wrong processing chain + deaf to the player's volume sliders. Rebind to the live one.
            _reroutedAudio += FiresCore.Services.LiveAudioRouting.RouteWorldSfx(fx);

            FiresCore.Services.VanillaMaterialCache.EnsureBuilt();
            if (!FiresCore.Services.VanillaMaterialCache.IsBuilt) return;

            if (_liveShadersByName == null)
            {
                _liveShadersByName = new Dictionary<string, Shader>();
                foreach (var cached in FiresCore.Services.VanillaMaterialCache.ByName.Values)
                {
                    if (cached == null || cached.shader == null) continue;
                    if (!_liveShadersByName.ContainsKey(cached.shader.name))
                        _liveShadersByName[cached.shader.name] = cached.shader;
                }
            }

            foreach (var renderer in fx.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                var mats = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var mat = mats[i];
                    if (mat == null) continue;
                    string matName = mat.name.Replace(" (Instance)", "").Trim();
                    if (FiresCore.Services.VanillaMaterialCache.ByName.TryGetValue(matName, out var liveMat)
                        && liveMat != null && !ReferenceEquals(liveMat, mat))
                    {
                        mats[i] = liveMat;
                        changed = true;
                        _repairedMaterials++;
                    }
                    else if (mat.shader != null
                             && _liveShadersByName.TryGetValue(mat.shader.name, out var liveShader)
                             && !ReferenceEquals(liveShader, mat.shader))
                    {
                        mat.shader = liveShader;
                        _repairedShaders++;
                    }
                }
                if (changed) renderer.sharedMaterials = mats;
            }
        }

        /// <summary>Depth-first search for the first child transform matching any of <paramref name="names"/> (case-insensitive).</summary>
        public static Transform FindTransformByNames(Transform root, string[] names)
        {
            foreach (var name in names)
            {
                var found = FindTransformRecursive(root, name);
                if (found != null) return found;
            }
            return null;
        }

        private static Transform FindTransformRecursive(Transform parent, string name)
        {
            if (parent.name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return parent;
            foreach (Transform child in parent)
            {
                var found = FindTransformRecursive(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
