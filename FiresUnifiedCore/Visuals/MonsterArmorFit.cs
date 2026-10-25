using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Visuals
{
    /// <summary>
    /// Skinned armour that vanilla
    /// VisEquipment attaches to a REGISTERED non-player humanoid is refitted onto that body on every client, so the visuals come free from
    /// the vanilla ZDO item hashes. Vanilla's own attach copies the wearer's bones by index (wrong on a non-player rig), so each piece is
    /// rebuilt from a hidden player-rig reference body and moved onto the monster with ArmorRebind.Bind(tPose: true). Opt-in per prefab
    /// (RegisterPrefab); nothing happens for other creatures, players or on a headless dedi.
    /// </summary>
    public static class MonsterArmorFit
    {
        private static readonly Dictionary<string, HashSet<HumanBodyBones>> s_prefabs = new Dictionary<string, HashSet<HumanBodyBones>>(StringComparer.Ordinal);
        private static ArmorRebind.BodyProfile s_reference;
        private static SkinnedMeshRenderer s_referenceBody;
        private static bool s_referenceFailed;

        /// <summary>Opts a prefab in (call any time, e.g. at Awake; safe by reflection). showThrough: HumanBodyBones names, comma separated, whose
        /// armour is left unpushed (e.g. "Spine" for a Draugr belly). Returns true when registered.</summary>
        public static bool RegisterPrefab(string prefabName, string showThrough = null)
        {
            if (string.IsNullOrEmpty(prefabName)) return false;
            var set = new HashSet<HumanBodyBones>();
            foreach (var part in (showThrough ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (Enum.TryParse(part.Trim(), true, out HumanBodyBones b)) set.Add(b);
            s_prefabs[prefabName] = set;
            FiresLogger.LogDebug($"[MonsterArmorFit] registered {prefabName}" + (set.Count > 0 ? $" (show-through {string.Join(",", set)})" : ""));
            return true;
        }

        public static bool IsRegistered(string prefabName) => prefabName != null && s_prefabs.ContainsKey(prefabName);

        [HarmonyPatch(typeof(VisEquipment), "AttachArmor")]
        private static class AttachArmorPatch
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(VisEquipment __instance, int itemHash, List<GameObject> __result)
            {
                try
                {
                    if (s_prefabs.Count == 0) return;
                    if (__result == null || __result.Count == 0)
                    {
                        // An armour with no attach_* child (ArmorLeatherLegs,
                        // ArmorRagsLegs...) is only a texture vanilla paints on the PLAYER's body material; a monster body can't show it.
                        var who = __instance.GetComponent<Character>();
                        if (who != null && !(who is Player) && s_prefabs.ContainsKey(Utils.GetPrefabName(who.gameObject)))
                            FiresLogger.LogDebug($"[MonsterArmorFit] texture-only: {Utils.GetPrefabName(who.gameObject)} {(ObjectDB.instance?.GetItemPrefab(itemHash)?.name ?? itemHash.ToString())} has no mesh (vanilla paints it on the player body); not visible on this model");
                        return;
                    }
                    if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
                    var character = __instance.GetComponent<Character>();
                    if (character == null || character is Player) return;
                    string prefab = Utils.GetPrefabName(character.gameObject);
                    if (!s_prefabs.ContainsKey(prefab)) return;
                    foreach (var go in __result)
                        if (go != null && go.GetComponent<PendingFit>() == null)
                            go.AddComponent<PendingFit>().Init(__instance, character, prefab, itemHash);
                }
                catch (Exception ex) { FiresLogger.LogDebug($"[MonsterArmorFit] attach postfix failed: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        // Helmets (and other single-object attachments) go through
        // VisEquipment.AttachItem, not AttachArmor. A SKINNED one (it has SkinnedMeshRenderers) gets the same refit; rigid helmets sit on
        // the head bone already and are left alone.
        [HarmonyPatch]
        private static class AttachItemPatch
        {
            private static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(VisEquipment), "AttachItem");

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(VisEquipment __instance, int itemHash, GameObject __result)
            {
                try
                {
                    if (__result == null || s_prefabs.Count == 0) return;
                    if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
                    if (__result.GetComponentInChildren<SkinnedMeshRenderer>(true) == null) { NudgeRigidHelmet(__instance, __result); return; }
                    var character = __instance.GetComponent<Character>();
                    if (character == null || character is Player) return;
                    string prefab = Utils.GetPrefabName(character.gameObject);
                    if (!s_prefabs.ContainsKey(prefab)) return;
                    if (__result.GetComponent<PendingFit>() == null)
                        __result.AddComponent<PendingFit>().Init(__instance, character, prefab, itemHash);
                }
                catch (Exception ex) { FiresLogger.LogDebug($"[MonsterArmorFit] attach-item postfix failed: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        // The ONE addition - a rigid helmet
        // on a model with an AttachFit "<Model>|MonsterHelmet" row (FiresAttachFit.txt) is moved by it in the same frame as vanilla's
        // attach (never seen before the move), in METRES along the joint's axes (Off x up, y forward, z right). No row: vanilla, untouched.
        private static void NudgeRigidHelmet(VisEquipment vis, GameObject item)
        {
            if (vis == null || vis.m_helmet == null || item.transform.parent != vis.m_helmet) return;
            var character = vis.GetComponent<Character>();
            if (character == null || character is Player) return;
            string prefab = Utils.GetPrefabName(character.gameObject);
            // A per-helmet row "<Model>|<HelmetItem>" (EXACT key)
            // wins over the per-model "<Model>|MonsterHelmet" row.
            string helmetItem = item.name.EndsWith("(Clone)") ? null : item.name;
            int hash = 0;
            try { hash = vis.m_currentHelmetItemHash; } catch { }
            var helmetPrefab = hash != 0 && ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(hash) : null;
            if (helmetPrefab != null) helmetItem = helmetPrefab.name;
            AttachFit.Tune t = default; string matched = null;
            if (helmetItem != null) { t = AttachFit.For(prefab, helmetItem, out matched); if (matched != prefab + "|" + helmetItem) matched = null; }
            if (matched == null) t = AttachFit.For(prefab, "MonsterHelmet", out matched);
            if (matched == null) return;
            Vector3 axes = new Vector3(t.Off.z, t.Off.x, t.Off.y);
            item.transform.position += vis.m_helmet.rotation * axes;
            if (t.Euler != Vector3.zero) item.transform.localRotation = Quaternion.Euler(t.Euler) * item.transform.localRotation;
            if (t.Scale > 0f && !Mathf.Approximately(t.Scale, 1f)) item.transform.localScale *= t.Scale;
            // The row, the bone axes it moved along and the FINAL local transform
            if (!FiresLogger.DebugEnabled) return;   // Build the proof line only when Debug lines print
            var it = item.transform;
            // What the rigid helmet will draw
            int rends = 0, on = 0; Bounds hb = default; bool hbSet = false;
            foreach (var rr in item.GetComponentsInChildren<Renderer>(true))
            {
                rends++;
                if (!rr.enabled || !rr.gameObject.activeInHierarchy) continue;
                on++;
                if (hbSet) hb.Encapsulate(rr.bounds); else { hb = rr.bounds; hbSet = true; }
            }
            FiresLogger.LogDebug($"[MonsterArmorFit] helmet render: {prefab} {helmetItem ?? item.name}: {on}/{rends} renderer(s) enabled+active, item active {item.activeInHierarchy}, "
                + (hbSet ? $"bounds size {hb.size.ToString("F2")}, centre {Vector3.Distance(hb.center, vis.m_helmet.position):0.00} m from the helmet bone" : "no bounds"));
            FiresLogger.LogDebug($"[MonsterArmorFit] helmet nudge: {prefab} {item.name} (item {helmetItem ?? "?"}) by row {matched} from {AttachFit.Source}: off {t.Off.x:0.###},{t.Off.y:0.###},{t.Off.z:0.###} m, rot {t.Euler}, scale {t.Scale:0.###}; "
                + $"moved {(vis.m_helmet.rotation * axes).ToString("F3")} world (bone {vis.m_helmet.name} up {vis.m_helmet.up.ToString("F2")} fwd {vis.m_helmet.forward.ToString("F2")} right {vis.m_helmet.right.ToString("F2")}); "
                + $"final local pos {it.localPosition.ToString("F3")} rot {it.localEulerAngles.ToString("F1")} scale {it.localScale.ToString("F3")}; "
                // The bone's axes in the MODEL's own frame (x right, y up, z forward)
                + $"in model frame: field1 -> {character.transform.InverseTransformDirection(vis.m_helmet.up).ToString("F2")}, field2 -> {character.transform.InverseTransformDirection(vis.m_helmet.forward).ToString("F2")}, field3 -> {character.transform.InverseTransformDirection(vis.m_helmet.right).ToString("F2")}");
        }

        // New AttachFit rows on live gear without a restart. No fitted-instance cache exists
        // (fits run in the attach postfixes), so each non-player character's armour slots are re-attached by vanilla: its
        // m_current*ItemHash values are zeroed and the next UpdateEquipmentVisuals attaches the same ZDO items again, running
        // NudgeRigidHelmet / PendingFit with the new table. Players are left to VCS and other mods.
        internal static int RefitLive()
        {
            if (FiresCore.UI.HeadlessGuard.IsHeadless) return 0;
            int n = 0;
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c is Player) continue;
                var vis = c.GetComponent<VisEquipment>();
                if (vis == null || !FiresCore.Npc.VisEquipmentCompat.IsReady(vis)) continue;
                if (FiresCore.Npc.VisEquipmentCompat.ResetArmorHashes(vis) > 0) n++;
            }
            return n;
        }

        private static bool s_reloadCmd;

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class AttachFitReload_InitTerminal_Patch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (s_reloadCmd) return;
                s_reloadCmd = true;
                new Terminal.ConsoleCommand("fires_attachfit_reload",
                    "[FiresUnifiedCore] fires_attachfit_reload - re-read BepInEx\\config\\FiresAttachFit.txt and re-attach the armour/helmets on live monsters so the new rows apply.",
                    args =>
                    {
                        try
                        {
                            if (FiresCore.UI.HeadlessGuard.IsHeadless) { args.Context?.AddString("fires_attachfit_reload: headless, nothing to refit"); return; }
                            int rows = AttachFit.Reload();
                            int chars = RefitLive();
                            string msg = $"[AttachFit] reload: {rows} row(s); refit {chars} character(s)";
                            FiresLogger.LogDebug(msg);
                            args.Context?.AddString(msg);
                        }
                        catch (Exception ex) { args.Context?.AddString("fires_attachfit_reload failed: " + ex.Message); }
                    });
            }
        }

        /// <summary>Add-only contract (FMO calls it). Fits are built on demand, so this is a no-op and returns 0.</summary>
        public static int Prewarm(string monsterPrefab, params string[] itemPrefabs)
        {
            FiresLogger.LogDebug($"[MonsterArmorFit] prewarm not used (0.2.431 path): {monsterPrefab}, {(itemPrefabs != null ? itemPrefabs.Length : 0)} items");
            return 0;
        }

        // Runs once, a frame after the attach, so the monster's Animator has evaluated (valid bone transforms).
        private sealed class PendingFit : MonoBehaviour
        {
            private VisEquipment _vis; private Character _character; private string _prefab; private int _itemHash; private int _frames;

            internal void Init(VisEquipment vis, Character character, string prefab, int itemHash)
            { _vis = vis; _character = character; _prefab = prefab; _itemHash = itemHash; }

            private void LateUpdate()
            {
                if (++_frames < 2) return;
                try { Fit(_vis, _character, _prefab, _itemHash, gameObject); }
                catch (Exception ex) { FiresLogger.LogDebug($"[MonsterArmorFit] {_prefab}: fit failed: {ex.GetType().Name}: {ex.Message}"); }
                Destroy(this);
            }
        }

        private static void Fit(VisEquipment vis, Character character, string prefab, int itemHash, GameObject attached)
        {
            if (vis == null || character == null || attached == null) return;
            var animator = character.GetComponentInChildren<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman)
            {
                FiresLogger.LogDebug($"[MonsterArmorFit] {prefab}: no humanoid avatar; armour left as vanilla attached it");
                return;
            }
            var reference = Reference();
            if (reference == null) return;
            var target = MonsterProfile(prefab, animator, vis);
            // Bind carries vertex offsets as world vectors and T-pose keeps each rig's own body rotation, so
            // the reference rig must face the same way as the monster, or the armour turns by the monster's yaw.
            reference.Animator.transform.root.rotation = animator.transform.rotation;
            // A monster spawned off-screen may not have evaluated its bones yet (CullUpdateTransforms): evaluate once.
            animator.Update(0f);
            string item =ObjectDB.instance?.GetItemPrefab(itemHash)?.name ?? itemHash.ToString();

            foreach (var smr in attached.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr == null || smr.sharedMesh == null) continue;
                // Cloth (capes) is simulated against the wearer's bones; a rebind would break it (CapeRebind handles capes in VCS).
                if (smr.GetComponent<Cloth>() != null)
                {
                    FiresLogger.LogDebug($"[MonsterArmorFit] {prefab}: {smr.name} is cloth; left to vanilla (cape fit is a later step)");
                    continue;
                }
                // On a rig whose bone list IS the player's (same names, same order: the Skeleton), vanilla's
                // index bind is already exact (Tools\MONSTER_RIG_FACTS.md), so Core keeps it ([MonsterArmorFit] "Vanilla bind on identical rigs").
                if (VanillaBindOnIdentical && SameRigAsPlayer(prefab, smr))
                {
                    if (FiresLogger.DebugEnabled) FiresLogger.LogDebug($"[MonsterArmorFit] gear: {prefab} {character.GetZDOID()} wears {item} ({smr.sharedMesh.name}): identical rig, vanilla bind kept");
                    continue;
                }
                var temp = new GameObject("FiresArmorFitTemp");
                try
                {
                    temp.SetActive(false);
                    var src = temp.AddComponent<SkinnedMeshRenderer>();
                    src.sharedMesh = smr.sharedMesh;
                    src.bones = s_referenceBody.bones;            // vanilla's own by-index scheme, onto the player layout
                    src.rootBone = s_referenceBody.rootBone;
                    src.sharedMaterials = smr.sharedMaterials;
                    src.shadowCastingMode = smr.shadowCastingMode;
                    temp.layer = smr.gameObject.layer;
                    var r = ArmorRebind.Bind(src, reference, target, smr.transform.parent, tPose: true);
                    if (r.Result != null) { NudgeSkinned(r.Result, animator, prefab, item); smr.enabled = false; }
                    if (FiresLogger.DebugEnabled) FiresLogger.LogDebug($"[MonsterArmorFit] gear: {prefab} {character.GetZDOID()} wears {item} ({smr.sharedMesh.name}) bound {r.Matched}/{r.Matched + r.Unmatched} bones ({r.ViaAncestor} via an ancestor, {r.Unmatched} unmatched), pushed {r.PushedVerts}, read {r.ReadPath} (profile {r.Profile})" +
                        (r.Unmatched > 0 ? $" unmatched [{string.Join(", ", r.UnmatchedNames ?? new string[0])}]" : "") + (r.Result == null ? " - NOT fitted, vanilla renderer kept" : ""));
                    // Each bone that did not map 1:1, with its vertex count (the Draugr coif: 34 via an ancestor)
                    // The extent scaling / body turn per bone with vertices (why a piece sits wider or turned)
                    if (FiresLogger.DebugEnabled && r.Scaling != null && r.Scaling.Length > 0)
                        FiresLogger.LogDebug($"[MonsterArmorFit] scaling: {prefab} {item} pushed {r.PushedVerts}: {string.Join("; ", r.Scaling)}");
                    // The neck decision made in THIS bind (switch, searches, result)
                    if (FiresLogger.DebugEnabled && r.NeckDecision != null)
                        FiresLogger.LogDebug($"[MonsterArmorFit] {prefab} {item}: {r.NeckDecision}");
                    if (FiresLogger.DebugEnabled && r.BoneMap != null && r.BoneMap.Length > 0)
                        FiresLogger.LogDebug($"[MonsterArmorFit] bone map: {prefab} {item} ({r.BoneMap.Length} not 1:1): {string.Join("; ", r.BoneMap)}");
                    // Diagnostic only - 10 frames later, log
                    // whether the fitted renderer is enabled, visible, has its mesh/bones, and where its bounds sit against the body.
                    if (r.Result != null) r.Result.gameObject.AddComponent<RenderCheck>().Init(prefab, item, character);
                }
                finally { UnityEngine.Object.Destroy(temp); }
            }
        }

        private sealed class RenderCheck : MonoBehaviour
        {
            private string _prefab, _item; private Character _who; private int _at;
            internal void Init(string prefab, string item, Character who) { _prefab = prefab; _item = item; _who = who; _at = Time.frameCount + 10; }
            private void LateUpdate()
            {
                if (Time.frameCount < _at) return;
                try
                {
                    var smr = GetComponent<SkinnedMeshRenderer>();
                    var mesh = smr != null ? smr.sharedMesh : null;
                    int nullBones = 0; if (smr != null && smr.bones != null) foreach (var b in smr.bones) if (b == null) nullBones++;
                    Vector3 body = _who != null ? _who.transform.position + Vector3.up * 1f : transform.position;
                    FiresLogger.LogDebug($"[MonsterArmorFit] render check: {_prefab} {_item}: renderer {(smr == null ? "MISSING" : smr.enabled ? "enabled" : "DISABLED")}, " +
                        $"active {gameObject.activeInHierarchy}, visible {(smr != null && smr.isVisible)}, mesh {(mesh != null ? mesh.name + " " + mesh.vertexCount + " verts" : "NONE")}, " +
                        $"bones {(smr != null && smr.bones != null ? smr.bones.Length : 0)} ({nullBones} null), root {(smr != null && smr.rootBone != null ? smr.rootBone.name : "none")}, " +
                        $"materials {(smr != null ? smr.sharedMaterials.Length : 0)}, bounds centre {(smr != null ? Vector3.Distance(smr.bounds.center, body).ToString("0.00") : "-")} m from the body, size {(smr != null ? smr.bounds.size.ToString("F2") : "-")}");
                }
                catch (Exception ex) { FiresLogger.LogDebug($"[MonsterArmorFit] render check failed: {ex.Message}"); }
                Destroy(this);
            }
        }

        // The ONE per-(model, item) skinned offset, from FiresAttachFit.txt
        // ("Draugr|ArmorWolfChest|-0.08,0,0|0,0,0|1": Off x up, y forward, z right, WORLD metres in the monster's frame). EXACT key only
        // (no model-family fallback); no row = the base fit untouched. Baked into this fit's own rebuilt mesh (never the item's).
        private static BepInEx.Configuration.ConfigEntry<bool> s_vanillaBindOnIdentical;
        private static bool VanillaBindOnIdentical => s_vanillaBindOnIdentical != null && s_vanillaBindOnIdentical.Value;

        internal static void Bind(BepInEx.Configuration.ConfigFile config)
        {
            s_vanillaBindOnIdentical = config.Bind("MonsterArmorFit", "Vanilla bind on identical rigs", false,
                "CLIENT. On: a monster whose skeleton has the player's exact bone list (the Skeleton) keeps vanilla's own armour bind (exact there) instead of Core's refit. Off (default for now): Core refits it like every other monster.");
            // Golden
            // rule, default OFF until the OFF/ON pair is visually approved
            var splitNeck = config.Bind("MonsterArmorFit", "Split unmapped neck", false,
                "CLIENT. On: on a monster with no neck bone (the Draugr), an armour's neck vertices share their weight half with the head and half with the upper chest, so collars and coifs bend with the head. Off (default): they ride the upper chest only.");
            ArmorRebind.SplitUnmappedNeck = () => splitNeck.Value;
        }

        private static readonly Dictionary<string, bool> s_sameRig = new Dictionary<string, bool>(StringComparer.Ordinal);

        // The renderer's bones (vanilla gave it the model's m_bodyModel.bones) against the player reference body's, name by name, index for index.
        private static bool SameRigAsPlayer(string prefab, SkinnedMeshRenderer smr)
        {
            if (s_sameRig.TryGetValue(prefab, out bool same)) return same;
            var mine = smr.bones; var player = s_referenceBody != null ? s_referenceBody.bones : null;
            if (player == null) Reference();
            player = s_referenceBody != null ? s_referenceBody.bones : null;
            same = mine != null && player != null && mine.Length == player.Length && mine.Length > 0;
            for (int i = 0; same && i < mine.Length; i++) same = mine[i] != null && player[i] != null && mine[i].name == player[i].name;
            if (player != null) s_sameRig[prefab] = same;
            FiresLogger.LogDebug($"[MonsterArmorFit] rig check: {prefab} {(mine != null ? mine.Length : 0)} bones vs the player's {(player != null ? player.Length : 0)}: {(same ? "SAME list" : "different")}");
            return same;
        }

        private static void NudgeSkinned(SkinnedMeshRenderer fitted, Animator animator, string prefab, string item)
        {
            string key = prefab + "|" + item;
            var tune = AttachFit.For(prefab, item, out var matched);
            if (matched != key || tune.Off == Vector3.zero) return;
            var mesh = fitted.sharedMesh; var bones = fitted.bones;
            if (mesh == null || !mesh.isReadable || bones == null || bones.Length == 0 || bones[0] == null || mesh.bindposes.Length == 0 || animator == null) return;
            Transform root = animator.transform;
            Vector3 world = root.up * tune.Off.x + root.forward * tune.Off.y + root.right * tune.Off.z;
            Vector3 d = (bones[0].localToWorldMatrix * mesh.bindposes[0]).inverse.MultiplyVector(world);
            var v = mesh.vertices;
            for (int i = 0; i < v.Length; i++) v[i] += d;
            mesh.vertices = v;
            mesh.RecalculateBounds();
            FiresLogger.LogDebug($"[MonsterArmorFit] fit tune: {prefab} {item} ({mesh.name}) moved {tune.Off} m (up, fwd, right) by row {matched}");
        }

        private static ArmorRebind.BodyProfile MonsterProfile(string prefab, Animator animator, VisEquipment vis)
        {
            string name = "Monster:" + prefab;
            ArmorRebind.BodyProfile.TryGet(name, out var cached);
            if (cached != null && cached.Animator == animator) return cached;
            var skin = new List<SkinnedMeshRenderer>();
            if (vis.m_bodyModel != null) skin.Add(vis.m_bodyModel);
            ArmorRebind.BodyProfile p;
            if (cached != null && cached.Outer != null)
            {
                // The extents are bind-space and the same for every instance of a prefab; only the bones and skin are this instance's.
                p = new ArmorRebind.BodyProfile
                {
                    Name = name, Animator = animator, Outer = cached.Outer, Typical = cached.Typical,
                    ShoulderSpan = cached.ShoulderSpan, HipSpan = cached.HipSpan, Skin = skin,
                };
                foreach (HumanBodyBones b in Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (b == HumanBodyBones.LastBone) continue;
                    var t = animator.GetBoneTransform(b);
                    if (t != null && !p.Ids.ContainsKey(t)) p.Ids[t] = b;
                }
            }
            else p = ArmorRebind.Profile(animator, skin, name);
            p.ShowThrough = new HashSet<HumanBodyBones>(s_prefabs[prefab]);
            ArmorRebind.BodyProfile.Register(p);
            return p;
        }

        // One hidden player rig per client: the Player prefab's Visual (Animator) with scripts, colliders and bodies stripped, renderers off.
        private static ArmorRebind.BodyProfile Reference()
        {
            if (s_reference != null && s_reference.Animator != null) return s_reference;
            if (s_referenceFailed) return null;
            try
            {
                var player = ZNetScene.instance?.GetPrefab("Player");
                var playerVis = player != null ? player.GetComponent<VisEquipment>() : null;
                var visualSrc = player != null ? player.GetComponentsInChildren<Animator>(true).FirstOrDefault(a => a.avatar != null && a.avatar.isHuman) : null;
                if (playerVis == null || visualSrc == null || playerVis.m_bodyModel == null) throw new InvalidOperationException("Player prefab has no humanoid Visual / body model");

                var holder = new GameObject("FiresArmorFitReference");
                holder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(holder);
                holder.transform.position = new Vector3(0f, -5000f, 0f);
                var visual = UnityEngine.Object.Instantiate(visualSrc.gameObject, holder.transform, false);
                foreach (var mb in visual.GetComponentsInChildren<MonoBehaviour>(true)) UnityEngine.Object.DestroyImmediate(mb);
                foreach (var c in visual.GetComponentsInChildren<Collider>(true)) UnityEngine.Object.DestroyImmediate(c);
                foreach (var rb in visual.GetComponentsInChildren<Rigidbody>(true)) UnityEngine.Object.DestroyImmediate(rb);
                foreach (var rend in visual.GetComponentsInChildren<Renderer>(true)) rend.enabled = false;
                var anim = visual.GetComponent<Animator>();
                anim.runtimeAnimatorController = null;
                holder.SetActive(true);

                s_referenceBody = visual.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(s => s.name == playerVis.m_bodyModel.name);
                if (s_referenceBody == null) throw new InvalidOperationException("reference body model not found: " + playerVis.m_bodyModel.name);
                s_reference = ArmorRebind.Profile(anim, new[] { s_referenceBody }, "Player");
                ArmorRebind.BodyProfile.Register(s_reference);
                FiresLogger.LogDebug($"[MonsterArmorFit] reference player rig built ({s_reference.Ids.Count} humanoid bones, body {s_referenceBody.name})");
                return s_reference;
            }
            catch (Exception ex)
            {
                s_referenceFailed = true;
                FiresLogger.LogWarning($"[MonsterArmorFit] no reference player rig ({ex.Message}); registered monsters keep vanilla's armour attach.");
                return null;
            }
        }
    }
}
