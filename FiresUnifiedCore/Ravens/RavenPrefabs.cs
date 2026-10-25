using System;
using System.Collections.Generic;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Ravens
{
    /// <summary>
    /// Hugin and Munin as real, networked creatures, possessable and offered as pets,
    /// synced in multiplayer (Tools\FIRES_RAVENS_PLAN.md). Vanilla's ravens are the tutorial's client-side
    /// Raven component, so each bird is built at runtime from vanilla's Bat (a flying Character with MonsterAI, ZNetView,
    /// ZSyncTransform, ZSyncAnimation) wearing the raven's model:
    /// - the Bat's own visual and animator stay (renderers hidden) and are what Character / ZSyncAnimation drive, so every
    ///   parameter the engine writes exists;
    /// - the raven model rides along with its own animator, played by <see cref="RavenAnimDriver"/>;
    /// - no weapons (no attacks), no drops, no hunting, random flight on, not a boss.
    /// Prefabs: <see cref="Hugin"/>, <see cref="Munin"/> (names are a contract: Valcast possession and FiresRPGmaker's pets).
    /// </summary>
    public static class RavenPrefabs
    {
        public const string Hugin = "FiresRaven_Hugin";
        public const string Munin = "FiresRaven_Munin";
        private const string Base = "Bat";

        private static readonly (string prefab, string display, string source)[] Birds =
        {
            (Hugin, "Hugin", "Hugin"),
            (Munin, "Munin", "Munin"),
        };

        private static GameObject _holder;
        private static readonly Dictionary<string, GameObject> Built = new Dictionary<string, GameObject>(StringComparer.Ordinal);

        /// <summary>True for the two raven prefabs (and instances named after them).</summary>
        public static bool IsRaven(string prefabName) =>
            !string.IsNullOrEmpty(prefabName) && (prefabName.StartsWith(Hugin, StringComparison.Ordinal) || prefabName.StartsWith(Munin, StringComparison.Ordinal));

        private static void EnsureRegistered(ZNetScene scene)
        {
            if (scene == null) return;
            GameObject bat = scene.GetPrefab(Base);
            if (bat == null)
            {
                FiresLogger.LogDebug($"[Ravens] not built: no '{Base}' prefab.");
                return;
            }
            foreach ((string prefab, string display, string source) in Birds)
            {
                int hash = prefab.GetStableHashCode();
                if (scene.m_namedPrefabs.ContainsKey(hash)) continue;
                if (!Built.TryGetValue(prefab, out GameObject bird) || bird == null)
                {
                    GameObject model = FindRavenModel(source);
                    if (model == null)
                    {
                        FiresLogger.LogDebug($"[Ravens] {prefab} not built: vanilla raven '{source}' model not loaded.");
                        continue;
                    }
                    bird = Build(bat, model, prefab, display);
                    Built[prefab] = bird;
                }
                scene.m_prefabs.Add(bird);
                scene.m_namedPrefabs[hash] = bird;
            }
        }

        /// <summary>Vanilla's tutorial raven prefab (Raven component) by name, then its "model" child (mesh + animator).</summary>
        private static GameObject FindRavenModel(string name)
        {
            Raven fallback = null;
            foreach (Raven raven in Resources.FindObjectsOfTypeAll<Raven>())
            {
                if (raven == null || raven.gameObject.name != name) continue;
                if (!raven.gameObject.scene.IsValid()) { fallback = raven; break; }  // the prefab asset, not a live instance
                if (fallback == null) fallback = raven;
            }
            if (fallback == null) return null;
            Transform model = fallback.transform.Find("model");
            if (model == null)
            {
                Animator anim = fallback.GetComponentInChildren<Animator>(true);
                model = anim != null ? anim.transform : null;
            }
            return model != null ? model.gameObject : null;
        }

        private static GameObject Build(GameObject bat, GameObject model, string prefab, string display)
        {
            if (_holder == null)
            {
                _holder = new GameObject("FiresRavens");
                _holder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(_holder);
            }
            GameObject bird;
            bool wasForced = ZNetView.m_forceDisableInit;
            try
            {
                ZNetView.m_forceDisableInit = true;
                bird = UnityEngine.Object.Instantiate(bat, _holder.transform, false);
            }
            finally { ZNetView.m_forceDisableInit = wasForced; }
            bird.name = prefab;

            Character character = bird.GetComponent<Character>();
            if (character != null)
            {
                character.m_name = display;
                character.m_boss = false;
                character.m_bossEvent = "";
                character.m_defeatSetGlobalKey = "";
                character.m_faction = Character.Faction.AnimalsVeg;
                character.m_flying = true;
            }
            if (character is Humanoid humanoid)
            {
                // No attacks. With nothing to swing, possession's LMB / MMB / RMB do nothing.
                humanoid.m_defaultItems = new GameObject[0];
                humanoid.m_randomWeapon = new GameObject[0];
                humanoid.m_randomArmor = new GameObject[0];
                humanoid.m_randomShield = new GameObject[0];
                humanoid.m_randomSets = new Humanoid.ItemSet[0];
                humanoid.m_unarmedWeapon = null;
            }
            MonsterAI ai = bird.GetComponent<MonsterAI>();
            if (ai != null)
            {
                ai.m_randomFly = true;
                ai.m_avoidLand = false;          // possession refuses avoid-land creatures ("swimmers")
                ai.m_enableHuntPlayer = false;
                ai.m_attackPlayerObjects = false;
                ai.m_idleSound = new EffectList();
                ai.m_alertedEffects = new EffectList();
            }
            CharacterDrop drop = bird.GetComponent<CharacterDrop>();
            if (drop != null) drop.m_drops.Clear();

            // The Bat stays as the engine's body and animator, unseen and unheard.
            foreach (Renderer r in bird.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
            foreach (AudioSource a in bird.GetComponentsInChildren<AudioSource>(true)) a.gameObject.SetActive(false);

            // The raven rides as a later sibling, so GetComponentInChildren<Animator>() still finds the Bat's first.
            GameObject raven = UnityEngine.Object.Instantiate(model, bird.transform, false);
            raven.name = "RavenModel";
            raven.transform.localPosition = Vector3.zero;
            raven.transform.localRotation = Quaternion.identity;
            raven.transform.localScale = model.transform.lossyScale;
            foreach (Component c in raven.GetComponentsInChildren<Component>(true))
            {
                if (c == null || c is Transform || c is Animator || c is Renderer || c is MeshFilter || c is LODGroup) continue;
                UnityEngine.Object.DestroyImmediate(c);
            }
            raven.AddComponent<RavenAnimDriver>();

            FiresLogger.LogDebug($"[Ravens] {prefab} built: {Base} body ({(character != null ? character.GetType().Name : "no Character")}, "
                + $"MonsterAI {(ai != null)}) wearing vanilla {display}'s model.");
            // What the bird actually wears (a pets shot showed a humanoid; the dump showed the raven rig).
            try
            {
                Animator ravenAnim = raven.GetComponent<Animator>();
                string ctrl = ravenAnim != null && ravenAnim.runtimeAnimatorController != null ? ravenAnim.runtimeAnimatorController.name : "none";
                int clips = ravenAnim != null && ravenAnim.runtimeAnimatorController != null ? ravenAnim.runtimeAnimatorController.animationClips.Length : 0;
                var parts = new List<string>();
                foreach (Renderer r in raven.GetComponentsInChildren<Renderer>(true))
                {
                    Mesh m = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                    var mats = new List<string>();
                    foreach (Material mat in r.sharedMaterials) mats.Add(mat != null ? mat.name + "/" + (mat.shader != null ? mat.shader.name : "-") : "null");
                    parts.Add($"{r.name}: {r.GetType().Name} mesh '{(m != null ? m.name : "none")}' ({(m != null ? m.vertexCount : 0)} verts) mats [{string.Join(", ", mats.ToArray())}]");
                }
                FiresLogger.LogDebug($"[Ravens] {prefab} model source '{model.transform.root.name}/{model.name}' scale {raven.transform.localScale.x:0.###}; "
                    + $"animator {(ravenAnim != null ? "on RavenModel" : "missing")} controller '{ctrl}' ({clips} clips, avatar {(ravenAnim != null && ravenAnim.avatar != null ? ravenAnim.avatar.name : "none")}); "
                    + string.Join(" | ", parts.ToArray()));
            }
            catch (Exception e) { FiresLogger.LogDebug($"[Ravens] {prefab} model report failed: {e.Message}"); }
            return bird;
        }

        [HarmonyPatch(typeof(ZNetScene), "Awake")]
        private static class ZNetSceneAwake
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(ZNetScene __instance) => EnsureRegistered(__instance);
        }
    }
}
