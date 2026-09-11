using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using FiresCore.Bridge;
using FiresCore.Storage;

namespace FiresCore.Npc
{
    /// <summary>
    /// Builds a static, ZDO-free mannequin from a <see cref="PlayerAppearance"/> for the leaderboard
    /// remote-player preview. It clones the registered, shader-fixed <c>CompanionNpc</c> prefab (which
    /// already carries the live <c>Custom/Player</c> body material and the player <c>m_models[]</c>,
    /// courtesy of CompanionPrefabManager.FixCompanionShaders), strips every gameplay/AI/network
    /// component, then drives vanilla <see cref="VisEquipment"/> DIRECTLY — bypassing NpcVisEquipment's
    /// ZDO gates and its T+2s Invoke init, which are wrong for a deterministic synchronous portrait.
    ///
    /// Item tokens come from Phase-1 capture: a prefab NAME, or — on this build, where the VisEquipment
    /// item fields are int hashes — the literal stable-hash as an all-digit string. Both forms route
    /// through <see cref="VisEquipmentCompat"/>: all-digit tokens are reverse-resolved to the prefab
    /// name via ObjectDB so the value hashes back to the same key; name tokens pass through verbatim.
    /// The string item fields are written directly (the vanilla Set*Item methods NRE without a ZDO),
    /// then <c>UpdateEquipmentVisuals()</c> is invoked ONCE synchronously to attach everything.
    ///
    /// Defensive throughout: every slot/hair/color step is independent. A missing prefab leaves the
    /// slot empty, missing hair leaves the head bald, an empty/partial record yields a naked default
    /// mannequin. It never throws; it returns null ONLY when the base CompanionNpc prefab or the Player
    /// source material isn't loaded yet (the caller then shows an "unavailable" state).
    /// </summary>
    public static class FiresMannequinBuilder
    {
        // Components to strip from the clone — the entire gameplay/AI/network/combat stack. Matched by
        // type name so a missing assembly type is simply skipped (no hard reference).
        private static readonly HashSet<string> StripTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "CompanionAI", "CompanionController", "CompanionCombat", "CompanionAttackBridge",
            "CompanionSkills", "CompanionStats", "CompanionProgression", "CompanionLuck",
            "CompanionAutoPickup", "CompanionDoorHandler", "CompanionRandomLoadout",
            "CompanionWeaponScaler", "CompanionInventory",
            "ArchetypeController", "ArchetypeAbilitySystem", "ArchetypeSkillSystem", "SkillDecisionSystem",
            "EnemyAttackRecognition", "ThreatAnalyzer", "CombatMemory", "CombatExperience",
            "Tameable", "MonsterAI", "BaseAI", "AnimalAI", "FootStep", "Ragdoll",
            "WildCompanionSeed", "WildCompanionDresser", "WildCompanionSquadFollower", "WildCompanionLootOnDeath",
            "ZSyncTransform", "ZSyncAnimation", "CharacterAnimEvent", "NpcVisEquipment",
        };

        // VisEquipment string item backing fields, per Fires slot.
        private const string F_Right = "m_rightItem";
        private const string F_Left = "m_leftItem";
        private const string F_LeftVariant = "m_leftItemVariant";
        private const string F_Chest = "m_chestItem";
        private const string F_Legs = "m_legItem";
        private const string F_Helmet = "m_helmetItem";
        private const string F_Shoulder = "m_shoulderItem";
        private const string F_ShoulderVariant = "m_shoulderItemVariant";
        private const string F_Utility = "m_utilityItem";
        private const string F_LeftBack = "m_leftBackItem";
        private const string F_LeftBackVariant = "m_leftBackItemVariant";
        private const string F_RightBack = "m_rightBackItem";
        private const string F_Hair = "m_hairItem";
        private const string F_Beard = "m_beardItem";

        private static GameObject _dummyNviewHolder; // permanently-inactive holder for the ZDO-less ZNetView

        /// <summary>
        /// Builds and dresses a mannequin from <paramref name="appearance"/>. Returns the built
        /// GameObject (caller stages it via <see cref="UI.PreviewStage"/>), or null only when the base
        /// CompanionNpc prefab / Player body material isn't loaded yet. A null or empty appearance still
        /// returns a valid naked default mannequin.
        /// </summary>
        public static GameObject Build(PlayerAppearance appearance)
        {
            if (UI.PreviewStage.IsHeadless) return null;

            var basePrefab = CompanionPrefabManager.GetCompanionNpcPrefab();
            if (basePrefab == null) return null;
            if (!PlayerBodyMaterialLoaded()) return null;

            GameObject clone = CloneStripped(basePrefab);
            if (clone == null) return null;

            try { Dress(clone, appearance); }
            catch (Exception ex) { Debug.LogWarning($"[FiresMannequinBuilder] Dress failed: {ex.Message}"); }

            return clone;
        }

        // ── clone + strip ────────────────────────────────────────────────────

        private static GameObject CloneStripped(GameObject basePrefab)
        {
            GameObject holder = CompanionPrefabManager.GetInactivePrefabHolder();
            bool baseWasActive = basePrefab.activeSelf;
            if (baseWasActive) basePrefab.SetActive(false);

            GameObject clone;
            try
            {
                clone = UnityEngine.Object.Instantiate(basePrefab, holder.transform);
            }
            finally
            {
                if (baseWasActive) basePrefab.SetActive(true);
            }

            clone.name = "FiresMannequin";

            // Strip gameplay/AI/network machinery while still inactive (Awake hasn't fired under the
            // inactive holder). DestroyImmediate so the components are gone before activation.
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (mb == null) continue;
                if (StripTypeNames.Contains(mb.GetType().Name))
                {
                    try { UnityEngine.Object.DestroyImmediate(mb); } catch { }
                }
            }

            // Drop the ZNetView (no networking for a portrait). VisEquipment.m_nview is rewired to a
            // ZDO-less dummy in Dress() so UpdateEquipmentVisuals reads the string item fields instead.
            var nview = clone.GetComponent<ZNetView>();
            if (nview != null)
            {
                try { UnityEngine.Object.DestroyImmediate(nview); } catch { }
            }

            // Freeze the rigidbody so the body holds a static pose; disable the capsule so it can't push.
            var rb = clone.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = true; rb.useGravity = false; rb.detectCollisions = false; }
            foreach (var col in clone.GetComponentsInChildren<Collider>(true))
                if (col != null) col.enabled = false;

            return clone;
        }

        // ── dress ────────────────────────────────────────────────────────────

        private static void Dress(GameObject clone, PlayerAppearance a)
        {
            var vis = clone.GetComponent<VisEquipment>();
            if (vis == null) return;

            // VisEquipment was kept disabled on the companion prefab (CompanionPrefabManager) so the
            // MonoUpdater wouldn't drive it before NpcVisEquipment was ready. We drive it by hand, so:
            //  • give it a ZDO-less m_nview (UpdateEquipmentVisuals then reads the string item fields),
            //  • set m_isPlayer=true / m_isArmorStand=false so hair/beard/back slots are processed,
            //  • ensure attach points + body model + models[] are wired (ConfigureVisEquipment-style).
            EnsureZdoLessNview(vis);
            EnsureBodyAndAttachPoints(vis, clone);
            EnsureModels(vis);
            vis.m_isPlayer = true;
            vis.m_isArmorStand = false;

            int modelIndex = a != null ? Mathf.Clamp(a.ModelIndex, 0, ModelCount(vis) - 1) : 0;
            SetBodyMesh(vis, modelIndex);

            // Item slots — write the string backing fields directly (ZDO-free, hash-or-name aware).
            // Skip TrinketItem: no Fires slot. Missing/empty tokens resolve to empty (naked default).
            if (a != null)
            {
                VisEquipmentCompat.SetItemFieldDirect(vis, F_Right, a.RightItem);
                VisEquipmentCompat.SetItemFieldDirect(vis, F_Left, a.LeftItem);
                SetIntField(vis, F_LeftVariant, a.LeftItemVariant);
                VisEquipmentCompat.SetItemFieldDirect(vis, F_Chest, a.ChestItem);
                VisEquipmentCompat.SetItemFieldDirect(vis, F_Legs, a.LegItem);
                VisEquipmentCompat.SetItemFieldDirect(vis, F_Helmet, a.HelmetItem);
                VisEquipmentCompat.SetItemFieldDirect(vis, F_Shoulder, a.ShoulderItem);
                SetIntField(vis, F_ShoulderVariant, a.ShoulderItemVariant);
                VisEquipmentCompat.SetItemFieldDirect(vis, F_Utility, a.UtilityItem);
                VisEquipmentCompat.SetItemFieldDirect(vis, F_LeftBack, a.LeftBackItem);
                SetIntField(vis, F_LeftBackVariant, a.LeftBackItemVariant);
                VisEquipmentCompat.SetItemFieldDirect(vis, F_RightBack, a.RightBackItem);

                // Hair/beard via the string fields too — UpdateEquipmentVisuals spawns them when
                // m_isPlayer=true. The NpcFashionBridge ApplyHair/ApplyBeard (host-mod fashion path) is
                // invoked afterwards as the preferred styler; if no host delegate is registered it's a
                // harmless no-op and the vanilla field-driven hair still shows.
                VisEquipmentCompat.SetItemFieldDirect(vis, F_Hair, a.HairItem);
                if (modelIndex == 0) // beard on male model only
                    VisEquipmentCompat.SetItemFieldDirect(vis, F_Beard, a.BeardItem);
                else
                    VisEquipmentCompat.SetItemFieldDirect(vis, F_Beard, "");
            }

            // Force a full visual rebuild this frame: clear "current" hashes (-1 sentinel) so every slot
            // mismatches, then run UpdateEquipmentVisuals once synchronously to attach.
            ClearVisEquipmentCurrentHashes(vis);
            InvokeUpdateEquipmentVisuals(vis);

            // Colors — written on the body + hair material instances (not via SetSkinColor, which would
            // route through UpdateColors and the ZDO). Independent of slot success.
            ApplyColors(vis, clone, a);

            // Host-mod fashion styling (bone-bind, ZDO-free) — preferred when present.
            if (a != null)
            {
                string hairColorStr = a.HairColorRgba ?? "";
                if (!string.IsNullOrEmpty(a.HairItem))
                    NpcFashionBridge.ApplyHair(clone, a.HairItem, hairColorStr);
                if (modelIndex == 0 && !string.IsNullOrEmpty(a.BeardItem))
                    NpcFashionBridge.ApplyBeard(clone, a.BeardItem, hairColorStr);
            }

            // Strip Cloth from attached armor/hair so a static pose doesn't spam "Unable to skin" logs.
            StripCloth(clone);
        }

        // ── VisEquipment wiring (ZDO-free) ────────────────────────────────────

        private static void EnsureZdoLessNview(VisEquipment vis)
        {
            // A ZNetView whose Awake never fired returns null from GetZDO(). We host it on a
            // permanently-inactive GameObject so Awake stays queued, then point VisEquipment's private
            // m_nview (and the public m_nViewOverride) at it. UpdateEquipmentVisuals sees zdo==null and
            // takes the string-field branch.
            if (_dummyNviewHolder == null)
            {
                _dummyNviewHolder = new GameObject("FiresMannequinNviewHolder");
                _dummyNviewHolder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_dummyNviewHolder);
            }
            var dummy = _dummyNviewHolder.GetComponent<ZNetView>();
            if (dummy == null) dummy = _dummyNviewHolder.AddComponent<ZNetView>();

            vis.m_nViewOverride = dummy;
            var f = AccessTools.Field(typeof(VisEquipment), "m_nview");
            if (f != null) { try { f.SetValue(vis, dummy); } catch { } }

            vis.enabled = false; // we drive it manually; keep MonoUpdater out of it
        }

        private static void EnsureBodyAndAttachPoints(VisEquipment vis, GameObject clone)
        {
            Transform visual = clone.transform.Find("Visual") ?? clone.transform;

            if (vis.m_bodyModel == null)
            {
                var body = FindRecursive(visual, "body");
                if (body != null) vis.m_bodyModel = body.GetComponent<SkinnedMeshRenderer>();
                if (vis.m_bodyModel == null)
                    vis.m_bodyModel = visual.GetComponentInChildren<SkinnedMeshRenderer>(true);
            }

            if (vis.m_leftHand == null)
                vis.m_leftHand = FindRecursive(visual, "LeftHand_Attach") ?? FindRecursive(visual, "LeftHand");
            if (vis.m_rightHand == null)
                vis.m_rightHand = FindRecursive(visual, "RightHand_Attach") ?? FindRecursive(visual, "RightHand");
            if (vis.m_helmet == null)
                vis.m_helmet = FindRecursive(visual, "Helmet_attach") ?? FindRecursive(visual, "Head");
            if (vis.m_backShield == null)
                vis.m_backShield = FindRecursive(visual, "BackShield_attach");
            if (vis.m_backMelee == null)
                vis.m_backMelee = FindRecursive(visual, "BackMelee_attach");
            if (vis.m_backTwohandedMelee == null)
                vis.m_backTwohandedMelee = FindRecursive(visual, "BackTwohandedMelee_attach")
                    ?? FindRecursive(visual, "BackTwoHanded_attach");
            if (vis.m_backBow == null)
                vis.m_backBow = FindRecursive(visual, "BackBow_attach");
            if (vis.m_backTool == null)
                vis.m_backTool = FindRecursive(visual, "BackTool_attach");
            if (vis.m_backAtgeir == null)
                vis.m_backAtgeir = FindRecursive(visual, "BackAtgeir_attach");

            if (vis.m_clothColliders == null)
                vis.m_clothColliders = new List<MagicaCloth2.ColliderComponent>();
        }

        private static void EnsureModels(VisEquipment vis)
        {
            if (vis.m_models != null && vis.m_models.Length >= 2) return;

            // CompanionNpc already has the Player's m_models copied in by FixCompanionShaders, but guard
            // anyway by re-copying from the Player prefab.
            var player = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Player") : null;
            var pve = player != null ? player.GetComponent<VisEquipment>() : null;
            if (pve != null && pve.m_models != null && pve.m_models.Length >= 2)
                vis.m_models = pve.m_models;
        }

        private static int ModelCount(VisEquipment vis) =>
            (vis != null && vis.m_models != null && vis.m_models.Length > 0) ? vis.m_models.Length : 1;

        private static void SetBodyMesh(VisEquipment vis, int modelIndex)
        {
            if (vis.m_models == null || modelIndex < 0 || modelIndex >= vis.m_models.Length) return;
            var model = vis.m_models[modelIndex];
            if (model != null && model.m_mesh != null && vis.m_bodyModel != null)
                vis.m_bodyModel.sharedMesh = model.m_mesh;
        }

        // ── colors ────────────────────────────────────────────────────────────

        private static void ApplyColors(VisEquipment vis, GameObject clone, PlayerAppearance a)
        {
            if (a == null) return;

            if (TryParseRgba(a.SkinColorRgba, out Color skin) && vis.m_bodyModel != null)
            {
                try
                {
                    var bodyMat = vis.m_bodyModel.material;
                    if (bodyMat != null && bodyMat.HasProperty("_SkinColor"))
                        bodyMat.SetColor("_SkinColor", skin);
                }
                catch { }
            }

            if (TryParseRgba(a.HairColorRgba, out Color hair))
            {
                try
                {
                    Transform visual = clone.transform.Find("Visual") ?? clone.transform;
                    foreach (var renderer in visual.GetComponentsInChildren<Renderer>(true))
                    {
                        if (renderer == null) continue;
                        string n = renderer.gameObject.name.ToLowerInvariant();
                        if (!(n.StartsWith("hair") || n.StartsWith("beard")
                              || n.StartsWith("npc_hair_") || n.StartsWith("npc_beard_"))) continue;
                        var mats = renderer.materials;
                        for (int i = 0; i < mats.Length; i++)
                            if (mats[i] != null) mats[i].SetColor("_HairColor", hair);
                        renderer.materials = mats;
                    }
                    if (vis.m_bodyModel != null)
                    {
                        var bodyMat = vis.m_bodyModel.material;
                        if (bodyMat != null) bodyMat.SetColor("_HairColor", hair);
                    }
                }
                catch { }
            }
        }

        // ── reflection helpers ─────────────────────────────────────────────────

        private static MethodInfo _updateEquipmentVisuals;

        private static void InvokeUpdateEquipmentVisuals(VisEquipment vis)
        {
            try
            {
                if (_updateEquipmentVisuals == null)
                    _updateEquipmentVisuals = AccessTools.Method(typeof(VisEquipment), "UpdateEquipmentVisuals");
                _updateEquipmentVisuals?.Invoke(vis, null);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FiresMannequinBuilder] UpdateEquipmentVisuals failed: {ex.Message}");
            }
        }

        private static readonly string[] CurrentHashFieldNames =
        {
            "m_currentLeftItemHash", "m_currentRightItemHash", "m_currentChestItemHash",
            "m_currentLegItemHash", "m_currentHelmetItemHash", "m_currentShoulderItemHash",
            "m_currentUtilityItemHash", "m_currentLeftBackItemHash", "m_currentRightBackItemHash",
            "m_currentBeardItemHash", "m_currentHairItemHash",
        };

        private static void ClearVisEquipmentCurrentHashes(VisEquipment vis)
        {
            foreach (var name in CurrentHashFieldNames)
            {
                var f = AccessTools.Field(typeof(VisEquipment), name);
                if (f != null && f.FieldType == typeof(int))
                {
                    try { f.SetValue(vis, -1); } catch { }
                }
            }
        }

        private static void SetIntField(VisEquipment vis, string fieldName, int value)
        {
            var f = AccessTools.Field(typeof(VisEquipment), fieldName);
            if (f != null && f.FieldType == typeof(int))
            {
                try { f.SetValue(vis, value); } catch { }
            }
        }

        private static bool PlayerBodyMaterialLoaded()
        {
            try
            {
                var player = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Player") : null;
                var pve = player != null ? player.GetComponent<VisEquipment>() : null;
                return pve != null && pve.m_bodyModel != null && pve.m_bodyModel.sharedMaterial != null;
            }
            catch { return false; }
        }

        private static void StripCloth(GameObject clone)
        {
            foreach (var cloth in clone.GetComponentsInChildren<Cloth>(true))
            {
                if (cloth != null) { try { UnityEngine.Object.DestroyImmediate(cloth); } catch { } }
            }
        }

        private static bool TryParseRgba(string rgba, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrEmpty(rgba)) return false;
            string s = rgba.StartsWith("#") ? rgba : "#" + rgba;
            return ColorUtility.TryParseHtmlString(s, out color);
        }

        private static Transform FindRecursive(Transform parent, string name)
        {
            if (parent == null) return null;
            if (parent.name.Equals(name, StringComparison.OrdinalIgnoreCase)) return parent;
            foreach (Transform child in parent)
            {
                var found = FindRecursive(child, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}
