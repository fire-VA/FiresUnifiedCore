using System;
using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// The body model swap for any
    /// Core NPC body (companions and RPGmaker NPCs). Lifted from RPGmaker's NpcController.ApplyModelOverride (FiresNPCs 0.0.716
    /// UI\NpcController.cs:1146-1364 + its helpers) so there is one implementation. Add-only contract:
    ///   Apply(body, prefab)  swap the visible model ("" = Clear); kg syntax kept: "&lt;prefab&gt;@humanoid", "Player_Female".
    ///   Clear(body)          back to the body's own model.
    ///   ZdoKey               "npc_model_override" on the body ZDO; BodyModelWatcher applies it on every peer (load, respawn, change).
    ///   Applied              raised after a swap / clear (body, prefab or "") so a mod can re-bind its own cached animator / gear.
    /// A body that never had the key set is never touched.
    /// </summary>
    public static class BodyModelSwap
    {
        public const string ZdoKey = "npc_model_override";
        private const string HiddenPrefix = "__OriginalVisual_";

        public static event Action<GameObject, string> Applied;

        /// <summary>The model the body currently shows ("" = its own).</summary>
        public static string Current(GameObject body)
        {
            var st = body != null ? body.GetComponent<BodyModelState>() : null;
            return st != null ? st.Applied : "";
        }

        public static bool Apply(GameObject body, string modelPrefabName)
        {
            if (body == null) return false;
            if (string.IsNullOrEmpty(modelPrefabName)) return Clear(body);
            var state = body.GetComponent<BodyModelState>() ?? body.AddComponent<BodyModelState>();
            if (modelPrefabName == state.Applied) return true;

            string resolvedName = modelPrefabName;
            bool forcePlayerController = false, femalePlayerModel = false;
            if (resolvedName.IndexOf("@humanoid", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                forcePlayerController = true;
                resolvedName = System.Text.RegularExpressions.Regex.Replace(resolvedName, "@humanoid", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            }
            if (string.Equals(resolvedName, "Player_Female", StringComparison.OrdinalIgnoreCase)) { resolvedName = "Player"; femalePlayerModel = true; }

            GameObject sourcePrefab = ZNetScene.instance?.GetPrefab(resolvedName);
            if (sourcePrefab == null) { Debug.LogWarning($"[BodyModelSwap] model prefab '{resolvedName}' not found"); return false; }

            try
            {
                if (!string.IsNullOrEmpty(state.Applied)) RemoveOverride(body);   // a second swap starts from the body's own model
                Transform targetVisual = body.transform.Find("Visual");
                if (targetVisual == null)
                {
                    var visualGO = new GameObject("Visual");
                    visualGO.transform.SetParent(body.transform, false);
                    targetVisual = visualGO.transform;
                }

                // VisEquipment off BEFORE hiding the old visual (its CustomUpdate reads m_bodyModel every frame)
                var existingVisEquip = body.GetComponent<VisEquipment>();
                if (existingVisEquip != null) { existingVisEquip.enabled = false; existingVisEquip.m_bodyModel = null; }

                // hide (never destroy) the original visual: Character / Humanoid keep their bone Transform references
                for (int i = targetVisual.childCount - 1; i >= 0; i--)
                {
                    var child = targetVisual.GetChild(i);
                    if (child.name.StartsWith(HiddenPrefix)) continue;
                    child.gameObject.SetActive(false);
                    child.name = HiddenPrefix + child.name;
                }
                foreach (var animEvt in body.GetComponentsInChildren<CharacterAnimEvent>(true)) if (animEvt != null) animEvt.enabled = false;

                var oldAnimator = body.GetComponent<Animator>();
                if (oldAnimator != null) UnityEngine.Object.DestroyImmediate(oldAnimator);

                Transform sourceVisual = sourcePrefab.transform.Find("Visual") ?? sourcePrefab.transform.Find("visual");
                bool sourceIsCharacter = sourcePrefab.GetComponent<Character>() != null;
                bool cloneWholeModel = false;
                if (sourceVisual == null)
                {
                    // kg parity: a creature without "Visual" clones its Animator child; a non-Character prop clones every top-level child
                    if (sourceIsCharacter) sourceVisual = sourcePrefab.GetComponentInChildren<Animator>(true)?.transform;
                    if (sourceVisual == null) cloneWholeModel = true;
                }

                if (cloneWholeModel)
                {
                    var container = new GameObject("OverrideModel");
                    container.transform.SetParent(targetVisual, false);
                    container.layer = LayerMask.NameToLayer("character");
                    for (int i = 0; i < sourcePrefab.transform.childCount; i++)
                    {
                        var srcChild = sourcePrefab.transform.GetChild(i);
                        var childClone = UnityEngine.Object.Instantiate(srcChild.gameObject, container.transform);
                        childClone.name = srcChild.name;
                        childClone.transform.localPosition = srcChild.localPosition;
                        childClone.transform.localRotation = srcChild.localRotation;
                        childClone.transform.localScale = srcChild.localScale;
                        childClone.SetActive(true);
                    }
                    StripGameplayComponentsOnly(container);
                    CleanupModelCloneArtifacts(container, false);
                }
                else if (sourceVisual != null)
                {
                    bool wasActive = sourceVisual.gameObject.activeSelf;
                    sourceVisual.gameObject.SetActive(false);   // the clone's Awake must not run against the live source
                    var clonedVisual = UnityEngine.Object.Instantiate(sourceVisual.gameObject, targetVisual);
                    sourceVisual.gameObject.SetActive(wasActive);
                    foreach (var animEvent in clonedVisual.GetComponentsInChildren<CharacterAnimEvent>(true))
                        if (animEvent != null) UnityEngine.Object.DestroyImmediate(animEvent);
                    clonedVisual.name = sourceVisual.name;
                    clonedVisual.transform.localPosition = sourceVisual.localPosition;
                    clonedVisual.transform.localRotation = sourceVisual.localRotation;
                    clonedVisual.transform.localScale = sourceVisual.localScale;
                    clonedVisual.SetActive(true);
                    StripGameplayComponentsOnly(clonedVisual);
                    CleanupPlayerModelEffects(clonedVisual);
                    CleanupModelCloneArtifacts(clonedVisual, sourceIsCharacter);
                }

                CopyAnimatorFromPrefab(body, sourcePrefab, targetVisual);
                if (forcePlayerController) ApplyPlayerAnimatorController(body, targetVisual);
                if (femalePlayerModel) ApplyFemalePlayerBodyModel(targetVisual);
                BindModelAnimator(body, targetVisual);
                EnableAllRenderers(targetVisual.gameObject);
                FiresCore.Services.VanillaAssetResolver.ApplyToPrefab(body, FiresCore.Services.VanillaAssetResolver.AssetSwapMode.Shader);

                state.Applied = modelPrefabName;
                RebindVisEquipment(body, existingVisEquip, targetVisual);
                Debug.Log($"[BodyModelSwap] '{body.name}' now shows model '{modelPrefabName}'");
                Raise(body, modelPrefabName);
                return true;
            }
            catch (Exception ex) { Debug.LogWarning($"[BodyModelSwap] swap to '{modelPrefabName}' failed: {ex.Message}"); return false; }
        }

        public static bool Clear(GameObject body)
        {
            var state = body != null ? body.GetComponent<BodyModelState>() : null;
            if (state == null || string.IsNullOrEmpty(state.Applied)) return true;
            try
            {
                RemoveOverride(body);
                var targetVisual = body.transform.Find("Visual");
                var visEquip = body.GetComponent<VisEquipment>();
                RebindVisEquipment(body, visEquip, targetVisual);
                state.Applied = "";
                Debug.Log($"[BodyModelSwap] '{body.name}' back to its own model");
                Raise(body, "");
                return true;
            }
            catch (Exception ex) { Debug.LogWarning("[BodyModelSwap] clear failed: " + ex.Message); return false; }
        }

        // the override clone goes, the hidden original comes back and drives the body again
        private static void RemoveOverride(GameObject body)
        {
            var targetVisual = body.transform.Find("Visual");
            if (targetVisual == null) return;
            for (int i = targetVisual.childCount - 1; i >= 0; i--)
            {
                var child = targetVisual.GetChild(i);
                if (child.name.StartsWith(HiddenPrefix))
                {
                    child.name = child.name.Substring(HiddenPrefix.Length);
                    child.gameObject.SetActive(true);
                }
                else UnityEngine.Object.Destroy(child.gameObject);
            }
            foreach (var animEvt in body.GetComponentsInChildren<CharacterAnimEvent>(true)) if (animEvt != null) animEvt.enabled = true;
            var own = targetVisual.GetComponent<Animator>();
            var root = body.GetComponent<Animator>();
            if (root != null && root != own) UnityEngine.Object.DestroyImmediate(root);
            if (own != null)
            {
                own.enabled = true;
                var zanim = body.GetComponent<ZSyncAnimation>(); if (zanim != null) zanim.m_animator = own;
                var character = body.GetComponent<Character>(); if (character != null) character.m_animator = own;
            }
        }

        private static void RebindVisEquipment(GameObject body, VisEquipment existingVisEquip, Transform targetVisual)
        {
            // NpcVisEquipment re-discovers the new body model and re-enables VisEquipment; without it, VisEquipment is pointed at the
            // visible body and only enabled for the Custom/Player shader (a Standard-shader creature throws in UpdateColors)
            var npcVisEquip = body.GetComponent<NpcVisEquipment>();
            if (npcVisEquip != null) npcVisEquip.ForceReinitialize();
            else if (existingVisEquip != null && targetVisual != null && TryRebindFromSource(body, existingVisEquip, targetVisual)) { }
            else if (existingVisEquip != null && targetVisual != null)
            {
                SkinnedMeshRenderer newBody = null;
                foreach (var smr in targetVisual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (smr != null && !UnderHidden(smr.transform, targetVisual)) { newBody = smr; break; }
                existingVisEquip.m_bodyModel = newBody;
                bool shaderOk = newBody != null && newBody.sharedMaterial != null && newBody.sharedMaterial.shader != null
                    && newBody.sharedMaterial.shader.name == "Custom/Player";
                existingVisEquip.enabled = shaderOk;
                if (!shaderOk) existingVisEquip.m_models = new VisEquipment.PlayerModel[0];
            }
            // a companion's worn gear is drawn by Core's CompanionInventory from its bag: redraw it on the new model
            try { body.GetComponent<CompanionInventory>()?.ApplyVisualEquipment(); } catch { }
        }

        // Hair, beards and helmets were shown half a head too low for a frame and then
        // hidden, and equipping a helmet reverted the swap. After a swap the body's VisEquipment still
        // pointed its joints (head, hands, back) at the HIDDEN player skeleton and kept the Player's m_models, so gear hung off the hidden
        // rig and every UpdateBaseModel put the player mesh back on the new body. When the model's own prefab has a VisEquipment (humanoid
        // races: Dverger, Creatures of Origins ...), its joints, body renderer and m_models are mapped onto the CLONE by path, and the
        // VisEquipment stays on: gear, hair and beards attach to the new rig like on that creature.
        private static bool TryRebindFromSource(GameObject body, VisEquipment vis, Transform targetVisual)
        {
            var state = body.GetComponent<BodyModelState>();
            if (state == null || string.IsNullOrEmpty(state.Applied)) return false;
            string name = System.Text.RegularExpressions.Regex.Replace(state.Applied, "@humanoid", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            if (string.Equals(name, "Player_Female", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Player", StringComparison.OrdinalIgnoreCase)) return false;
            var src = ZNetScene.instance?.GetPrefab(name);
            var srcVis = src != null ? src.GetComponent<VisEquipment>() : null;
            if (srcVis == null) return false;
            Transform srcRoot = src.transform.Find("Visual") ?? src.transform.Find("visual") ?? src.GetComponentInChildren<Animator>(true)?.transform;
            if (srcRoot == null) return false;
            Transform cloneRoot = null;
            for (int i = 0; i < targetVisual.childCount; i++)
            {
                var ch = targetVisual.GetChild(i);
                if (!ch.name.StartsWith(HiddenPrefix) && ch.name == srcRoot.name) { cloneRoot = ch; break; }
            }
            if (cloneRoot == null) return false;
            int mapped = 0;
            foreach (var fi in typeof(VisEquipment).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                if (fi.FieldType != typeof(Transform)) continue;
                var t = fi.GetValue(srcVis) as Transform;
                if (t == null) continue;
                var mappedT = MapByPath(t, srcRoot, cloneRoot);
                if (mappedT != null) { fi.SetValue(vis, mappedT); mapped++; }
            }
            SkinnedMeshRenderer bodyModel = srcVis.m_bodyModel != null ? MapByPath(srcVis.m_bodyModel.transform, srcRoot, cloneRoot)?.GetComponent<SkinnedMeshRenderer>() : null;
            vis.m_bodyModel = bodyModel;
            vis.m_models = srcVis.m_models ?? new VisEquipment.PlayerModel[0];
            vis.enabled = true;
            Debug.Log($"[BodyModelSwap] '{body.name}': gear joints follow the '{name}' rig ({mapped} joint(s), body {(bodyModel != null ? bodyModel.name : "none")}, {vis.m_models.Length} base model(s))");
            return true;
        }

        // the transform at the same path under cloneRoot as t has under srcRoot
        private static Transform MapByPath(Transform t, Transform srcRoot, Transform cloneRoot)
        {
            if (t == srcRoot) return cloneRoot;
            var parts = new List<string>();
            for (var c = t; c != null && c != srcRoot; c = c.parent) parts.Add(c.name);
            if (t.parent == null && srcRoot != t) { }   // not under the source visual: no mapping
            var cur = cloneRoot;
            for (int i = parts.Count - 1; i >= 0 && cur != null; i--) cur = cur.Find(parts[i]);
            return cur;
        }
        // Hair, beards and helmets on a swapped creature are fitted to its head, never hidden. Vanilla hangs hair,
        // beard and helmet on m_helmet = the Player rig's Helmet_attach (a child of its Head bone). A creature rig has none, and its head
        // bone has its own axes and size, so the old "Head" fallback put them half a head low or beside the face (the Majestic beard).
        // The fit, computed per model at swap time (no table): the Player's Head -> Helmet_attach offset and rotation, expressed in the
        // Player ROOT frame, rebuilt on the creature in ITS root frame from its head bone, scaled by the two rigs' head heights. The node
        // is parented to the creature's head bone, so it follows the head through every animation.
        public const string FitJointName = "__FitHelmet_attach";
        private static readonly Dictionary<string, (Vector3 off, Quaternion rot, float headH, float attScale)> s_playerRef = new Dictionary<string, (Vector3, Quaternion, float, float)>();

        public static Transform FitHeadAttach(GameObject body, Transform visual, HashSet<Transform> skeleton)
        {
            if (body == null || visual == null) return null;
            var head = FindHeadBone(visual, skeleton);
            if (head == null) return null;   // a beast with no head bone: nothing to fit (hidden)
            if (!PlayerHeadRef(out var off, out var rot, out float playerHeadH, out float attScale)) return null;
            var existing = head.Find(FitJointName);
            Transform fit = existing != null ? existing : new GameObject(FitJointName).transform;
            Transform root = body.transform;
            float bodyScale = Mathf.Max(0.01f, root.lossyScale.y);
            float creatureHeadH = Vector3.Dot(head.position - root.position, root.up) / bodyScale;
            float ratio = playerHeadH > 0.05f && creatureHeadH > 0.05f ? Mathf.Clamp(creatureHeadH / playerHeadH, 0.4f, 3f) : 1f;
            fit.SetParent(null, false);
            fit.position = head.position + root.rotation * (off * ratio * bodyScale);
            fit.rotation = root.rotation * rot;
            fit.localScale = Vector3.one * attScale * ratio * bodyScale;
            fit.SetParent(head, true);
            Debug.Log($"[BodyModelSwap] fit head '{body.GetComponent<BodyModelState>()?.Applied}': head bone '{head.name}', scale {ratio:0.00}, offset ({off.x:0.00},{off.y:0.00},{off.z:0.00}) m");
            return fit;
        }

        private static Transform FindHeadBone(Transform visual, HashSet<Transform> skeleton)
        {
            Transform contains = null;
            IEnumerable<Transform> pool = skeleton != null && skeleton.Count > 0 ? (IEnumerable<Transform>)skeleton : visual.GetComponentsInChildren<Transform>(true);
            foreach (var t in pool)
            {
                if (t == null || UnderHidden(t, visual)) continue;
                string n = t.name;
                if (string.Equals(n, "Head", StringComparison.OrdinalIgnoreCase) || n.EndsWith(" Head", StringComparison.OrdinalIgnoreCase)) return t;
                string l = n.ToLowerInvariant();
                if (contains == null && l.Contains("head") && !l.Contains("headband") && !l.Contains("helmet") && !l.Contains("_attach") && !l.Contains("end")) contains = t;
            }
            return contains;
        }

        // the Player prefab's Head -> Helmet_attach offset / rotation in its root frame, its head height and the attach's own scale
        private static bool PlayerHeadRef(out Vector3 off, out Quaternion rot, out float headH, out float attScale)
        {
            off = Vector3.zero; rot = Quaternion.identity; headH = 0f; attScale = 1f;
            if (s_playerRef.TryGetValue("Player", out var r)) { off = r.off; rot = r.rot; headH = r.headH; attScale = r.attScale; return true; }
            var p = ZNetScene.instance?.GetPrefab("Player");
            if (p == null) return false;
            Transform pHead = null, pAtt = null;
            foreach (var t in p.GetComponentsInChildren<Transform>(true))
            {
                if (pHead == null && t.name == "Head") pHead = t;
                if (pAtt == null && t.name == "Helmet_attach") pAtt = t;
            }
            if (pHead == null || pAtt == null) return false;
            var pr = p.transform;
            float s = Mathf.Max(0.01f, pr.lossyScale.y);
            off = Quaternion.Inverse(pr.rotation) * (pAtt.position - pHead.position) / s;
            rot = Quaternion.Inverse(pr.rotation) * pAtt.rotation;
            headH = Vector3.Dot(pHead.position - pr.position, pr.up) / s;
            attScale = pAtt.lossyScale.x / s;
            s_playerRef["Player"] = (off, rot, headH, attScale);
            return true;
        }
        private static void Raise(GameObject body, string prefab)
        {
            try { Applied?.Invoke(body, prefab); } catch (Exception ex) { Debug.LogWarning("[BodyModelSwap] Applied handler threw: " + ex.Message); }
        }

        private static bool UnderHidden(Transform t, Transform stop)
        {
            for (var check = t; check != null && check != stop; check = check.parent)
                if (check.name.StartsWith(HiddenPrefix)) return true;
            return false;
        }

        private static readonly string[] TypesToStrip =
        {
            "Character", "Humanoid", "MonsterAI", "BaseAI", "AnimalAI", "Tameable",
            "Procreation", "CharacterDrop", "CharacterTimedDestruction", "Growup",
            "LevelEffects", "CharacterAnimEvent", "FootStep", "ZNetView",
            "ZSyncTransform", "ZSyncAnimation", "Ragdoll", "Player", "PlayerController",
            "Attack", "ItemDrop", "Container", "Destructible", "WearNTear",
            "SpawnArea", "CreatureSpawner", "Spawner", "VisEquipment"
        };

        private static void StripGameplayComponentsOnly(GameObject obj)
        {
            var toRemove = new List<Component>();
            foreach (var animEvent in obj.GetComponentsInChildren<CharacterAnimEvent>(true))
                if (animEvent != null) { animEvent.enabled = false; toRemove.Add(animEvent); }
            foreach (var mono in obj.GetComponentsInChildren<MonoBehaviour>(true))
                if (mono != null && Array.IndexOf(TypesToStrip, mono.GetType().Name) >= 0) toRemove.Add(mono);
            foreach (var rb in obj.GetComponentsInChildren<Rigidbody>(true)) if (rb != null && !rb.isKinematic) toRemove.Add(rb);
            toRemove.AddRange(obj.GetComponentsInChildren<CharacterController>(true));
            toRemove.AddRange(obj.GetComponentsInChildren<CharacterJoint>(true));
            foreach (var col in obj.GetComponentsInChildren<Collider>(true)) if (col != null && !col.isTrigger) toRemove.Add(col);
            foreach (var comp in toRemove) if (comp != null) UnityEngine.Object.Destroy(comp);
            foreach (var animator in obj.GetComponentsInChildren<Animator>(true))
            {
                if (animator == null || animator.runtimeAnimatorController == null) continue;
                animator.enabled = true;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                try
                {
                    if (HasParameter(animator, "moving")) animator.SetBool("moving", false);
                    if (HasParameter(animator, "forward_speed")) animator.SetFloat("forward_speed", 0f);
                }
                catch { }
            }
        }

        private static bool HasParameter(Animator animator, string paramName)
        {
            if (animator == null || animator.parameters == null) return false;
            foreach (var param in animator.parameters) if (param.name == paramName) return true;
            return false;
        }

        private static void CopyAnimatorFromPrefab(GameObject body, GameObject sourcePrefab, Transform targetVisual)
        {
            var sourceAnimator = sourcePrefab.GetComponent<Animator>() ?? sourcePrefab.GetComponentInChildren<Animator>(true);
            if (sourceAnimator == null || sourceAnimator.runtimeAnimatorController == null) return;
            var cloneAnimator = FindCloneAnimator(targetVisual);
            if (cloneAnimator != null && cloneAnimator.runtimeAnimatorController != null)
            {
                cloneAnimator.enabled = true;
                cloneAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                cloneAnimator.applyRootMotion = false;
                return;
            }
            var targetAnimator = body.GetComponent<Animator>() ?? body.AddComponent<Animator>();
            targetAnimator.runtimeAnimatorController = sourceAnimator.runtimeAnimatorController;
            targetAnimator.avatar = sourceAnimator.avatar;
            targetAnimator.applyRootMotion = false;
            targetAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            targetAnimator.Rebind();
        }

        private static Animator FindActiveModelAnimator(GameObject body, Transform targetVisual) => FindCloneAnimator(targetVisual) ?? body.GetComponent<Animator>();

        private static Animator FindCloneAnimator(Transform targetVisual)
        {
            if (targetVisual == null) return null;
            foreach (var anim in targetVisual.GetComponentsInChildren<Animator>(true))
                if (anim != null && anim.transform != targetVisual && !UnderHidden(anim.transform, targetVisual)) return anim;
            return null;
        }

        private static void ApplyPlayerAnimatorController(GameObject body, Transform targetVisual)
        {
            try
            {
                var playerAnimator = ZNetScene.instance?.GetPrefab("Player")?.GetComponentInChildren<Animator>(true);
                if (playerAnimator == null || playerAnimator.runtimeAnimatorController == null) { Debug.LogWarning("[BodyModelSwap] @humanoid: Player animator controller unavailable"); return; }
                var anim = FindActiveModelAnimator(body, targetVisual);
                if (anim == null) return;
                anim.runtimeAnimatorController = playerAnimator.runtimeAnimatorController;
                anim.applyRootMotion = false;
                anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                anim.Rebind();
            }
            catch (Exception ex) { Debug.LogWarning("[BodyModelSwap] @humanoid controller swap failed: " + ex.Message); }
        }

        private static void ApplyFemalePlayerBodyModel(Transform targetVisual)
        {
            try
            {
                var vis = ZNetScene.instance?.GetPrefab("Player")?.GetComponent<VisEquipment>();
                if (vis == null || vis.m_models == null || vis.m_models.Length < 2) return;
                var female = vis.m_models[1];
                SkinnedMeshRenderer body = null;
                foreach (var smr in targetVisual.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    if (smr != null && !UnderHidden(smr.transform, targetVisual)) { body = smr; break; }
                if (body == null) return;
                body.sharedMesh = female.m_mesh;
                if (female.m_baseMaterial != null && body.materials.Length > 0)
                {
                    body.materials[0].SetTexture("_MainTex", female.m_baseMaterial.GetTexture("_MainTex"));
                    body.materials[0].SetTexture("_SkinBumpMap", female.m_baseMaterial.GetTexture("_SkinBumpMap"));
                }
            }
            catch (Exception ex) { Debug.LogWarning("[BodyModelSwap] Player_Female body swap failed: " + ex.Message); }
        }

        /// <summary>The swap's own model animator (null when none); the companion animator guard repoints to it.</summary>
        internal static Animator ModelAnimatorOf(GameObject body)
        {
            var targetVisual = body != null ? body.transform.Find("Visual") : null;
            return targetVisual != null ? FindActiveModelAnimator(body, targetVisual) : null;
        }

        /// <summary>Points Character / ZSyncAnimation at the swap's model animator again (the same bind a swap does).</summary>
        internal static bool RebindModelAnimator(GameObject body)
        {
            var targetVisual = body != null ? body.transform.Find("Visual") : null;
            if (targetVisual == null || FindActiveModelAnimator(body, targetVisual) == null) return false;
            BindModelAnimator(body, targetVisual);
            return true;
        }

        // ZSyncAnimation and Character cached the original rig's animator at Awake; everything moves to the animator driving the model
        private static void BindModelAnimator(GameObject body, Transform targetVisual)
        {
            var anim = FindActiveModelAnimator(body, targetVisual);
            if (anim == null) return;
            anim.logWarnings = false;
            var zanim = body.GetComponent<ZSyncAnimation>(); if (zanim != null) zanim.m_animator = anim;
            var character = body.GetComponent<Character>(); if (character != null) character.m_animator = anim;
            var original = targetVisual.GetComponent<Animator>();
            if (original != null && original != anim) original.enabled = false;
        }

        private static void CleanupModelCloneArtifacts(GameObject root, bool disableParticles)
        {
            foreach (var lod in root.GetComponentsInChildren<LODGroup>(true)) UnityEngine.Object.Destroy(lod);
            if (!disableParticles) return;
            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true)) { var em = ps.emission; em.enabled = false; }
        }

        private static void EnableAllRenderers(GameObject root)
        {
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                bool hidden = false;
                for (var check = renderer.transform; check != null && check != root.transform; check = check.parent)
                    if (!check.gameObject.activeSelf || check.name.StartsWith(HiddenPrefix)) { hidden = true; break; }
                if (!hidden) renderer.enabled = true;
            }
        }

        private static void CleanupPlayerModelEffects(GameObject root)
        {
            if (root == null) return;
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                if (child != null && child.gameObject != root && child.name == "dev_smoke") { UnityEngine.Object.Destroy(child.gameObject); break; }
        }
    }

    /// <summary>What model a body shows (BodyModelSwap.Current).</summary>
    public sealed class BodyModelState : MonoBehaviour { public string Applied = ""; }

    /// <summary>
    /// Applies the body ZDO's npc_model_override on every peer: at spawn / respawn and whenever the key changes (checked once a second
    /// against the ZDO data revision). Added by CompanionController / CompanionNpcModule Awake. A body whose key was never set is never
    /// touched. RPGmaker NPC bodies (an "NpcController" component) still apply through RPGmaker until it calls BodyModelSwap itself.
    /// </summary>
    public sealed class BodyModelWatcher : MonoBehaviour
    {
        private ZNetView _nview;
        private uint _rev = uint.MaxValue;
        private float _next;
        private bool _rpgmakerBody;

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
            foreach (var mb in GetComponents<MonoBehaviour>()) if (mb != null && mb.GetType().Name == "NpcController") { _rpgmakerBody = true; break; }
        }

        private void Update()
        {
            if (_rpgmakerBody || Time.time < _next) return;
            _next = Time.time + 1f;
            if (_nview == null || !_nview.IsValid()) return;
            var zdo = _nview.GetZDO();
            if (zdo == null || zdo.DataRevision == _rev) return;
            _rev = zdo.DataRevision;
            string want = zdo.GetString(BodyModelSwap.ZdoKey, "");
            if (want == BodyModelSwap.Current(gameObject)) return;
            if (string.IsNullOrEmpty(want)) BodyModelSwap.Clear(gameObject); else BodyModelSwap.Apply(gameObject, want);
        }
    }

    // VisEquipment.UpdateColors threw IndexOutOfRangeException every frame
    // after a barber swap: a body wearing another prefab's model keeps the Player's VisEquipment (model index, skin/hair
    // colour arrays) over a renderer set that does not have them. The exception escapes MonoUpdaters' loop every frame, so every
    // updater after it in that list was skipped too. For SWAPPED bodies only, the colour pass is skipped when it would index past
    // its arrays, and any throw from it is swallowed (logged once per body). Every other VisEquipment runs vanilla untouched.
    // A swapped body's base mesh is the model's own; vanilla's UpdateBaseModel would put the Player mesh (m_models) back on it
    // (equipping a helmet reverted the swap). Skipped for swapped bodies whose VisEquipment was not rebound to a source m_models.
    [HarmonyLib.HarmonyPatch(typeof(VisEquipment), "UpdateBaseModel")]
    internal static class BodyModelSwapBaseModelGuard
    {
        /// <summary>A BodyModelSwap onto a rig that is NOT the Player rig: NpcVisEquipment fitted (or sank) its head joint. Such a body
        /// keeps an EMPTY model table: vanilla UpdateBaseModel would put the Player body mesh on the creature's bones (Fire 10-24: "applying
        /// a shield to a model swapped companion completely broke its mesh").</summary>
        internal static bool IsNonPlayerRigSwap(VisEquipment v)
        {
            var st = v != null ? v.GetComponent<BodyModelState>() : null;
            if (st == null || string.IsNullOrEmpty(st.Applied) || v.m_helmet == null) return false;
            string n = v.m_helmet.name;
            return n == BodyModelSwap.FitJointName || n == "__ModelSwapHeadSink";
        }

        private static readonly HashSet<int> s_logged = new HashSet<int>();
        private static bool Prefix(VisEquipment __instance)
        {
            var st = __instance != null ? __instance.GetComponent<BodyModelState>() : null;
            if (st == null || string.IsNullOrEmpty(st.Applied)) return true;
            if (IsNonPlayerRigSwap(__instance))
            {
                if (s_logged.Add(__instance.GetInstanceID()))
                    UnityEngine.Debug.Log($"[BodyModelSwap] {__instance.name}: base-model update skipped (swapped to '{st.Applied}', not the Player rig; its mesh stays)");
                return false;
            }
            var m = __instance.m_models; int idx = __instance.m_modelIndex;
            return m != null && idx >= 0 && idx < m.Length && __instance.m_bodyModel != null;
        }
    }
    [HarmonyLib.HarmonyPatch(typeof(VisEquipment), "UpdateColors")]
    internal static class BodyModelSwapColorGuard
    {
        private static readonly HashSet<int> s_logged = new HashSet<int>();
        private static bool Swapped(VisEquipment v)
        {
            var st = v != null ? v.GetComponent<BodyModelState>() : null;
            return st != null && !string.IsNullOrEmpty(st.Applied);
        }
        private static bool Prefix(VisEquipment __instance)
        {
            if (!Swapped(__instance)) return true;
            var models = __instance.m_models;
            int idx = __instance.m_modelIndex;
            return models != null && idx >= 0 && idx < models.Length;
        }
        private static Exception Finalizer(VisEquipment __instance, Exception __exception)
        {
            if (__exception == null || !Swapped(__instance)) return __exception;
            if (s_logged.Add(__instance.GetInstanceID()))
                Debug.LogWarning($"[BodyModelSwap] '{__instance.name}': VisEquipment colours skipped on the swapped body ({__exception.GetType().Name}); shown once");
            return null;
        }
    }
}