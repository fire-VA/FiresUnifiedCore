using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Npc
{
    /// <summary>
    /// Sizes attachments on Fires companion bodies to the body's scale: rigid items (hair, beards, helmets, weapons) to the
    /// attach child's authored world scale times the body's, so a giant's sword grows with the giant and a dwarf's shrinks;
    /// skinned ones (attach_skin) to the body's transform scale. VisEquipment.AttachItem keeps the item's world size and never
    /// touches scale, which pins items to player size on scaled bodies and made modded styles whose root scale is not 1
    /// render a hundred times too large. Only bodies with a CompanionController are touched; '[Companions] Core attachment
    /// rescale' turns it off for an A/B.
    ///
    /// Either way, one '[NpcAttach]' line per attached item (what vanilla attached, where, what Core changed) and once per
    /// companion body a '[NpcSkeleton]' line: its bones against the Player's, name by name and index by index.
    /// </summary>
    [HarmonyPatch(typeof(VisEquipment), "AttachItem")]
    internal static class NpcAttachmentScaleFix
    {
        private static ConfigEntry<bool> s_rescale;

        internal static void Bind(ConfigFile config, FiresCore.Sync.ConfigSync configSync)
        {
            s_rescale = config.Bind("Companions", "Core attachment rescale", true,
                "SERVER. On (default): attachments follow a scaled companion body. The body's scale is on its root, and vanilla's "
                + "attach keeps each item's WORLD size (SetParent), so without this a dwarf holds a player-size sword: rigid items get "
                + "the attach child's authored scale times the body's, skinned ones (hair, beards, capes) the body's transform scale "
                + "(Core 0.2.157). Off = vanilla's sizes, for an A/B only.");
            configSync?.AddConfigEntry(s_rescale);
        }

        private static void Postfix(VisEquipment __instance, int itemHash, Transform joint, bool backAttach, GameObject __result)
        {
            if (__result == null || __instance == null) return;
            if (__instance.GetComponent<CompanionController>() == null) return;   // our bodies only
            string core = "none";
            // Off (A/B only): vanilla's sizes.
            if (s_rescale != null && !s_rescale.Value) { Evidence(__instance, itemHash, joint, __result, "none (rescale off by cfg)"); return; }

            // Skinned attaches (attach_skin) ride the body's bones and are parented to the body model's parent, not the joint.
            // Their meshes follow the bones, but vanilla's SetParent kept the item's world scale, so on a scaled body the
            // instance sits at 1/scale. MagicaCloth hair and beards size their simulation from that transform (initScale /
            // scaleRatio), so a dwarf's cloth hair ran at full size and floated off the small head (Fire, 2026-09-29). Give the
            // instance the body's scale; the cloth follows the change.
            if (joint != null && __result.transform.parent != joint)
            {
                if (__result.transform.localScale != Vector3.one)
                {
                    core = $"inherits body scale {__instance.transform.lossyScale.y:0.00} (skinned: localScale {__result.transform.localScale} -> (1, 1, 1))";
                    __result.transform.localScale = Vector3.one;
                }
                Evidence(__instance, itemHash, joint, __result, core);
                return;
            }
            if (joint == null) { Evidence(__instance, itemHash, joint, __result, core); return; }

            var itemPrefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(itemHash) : null;
            if (itemPrefab == null) { Evidence(__instance, itemHash, joint, __result, core); return; }

            // Re-derive the same attach child vanilla picked so we read ITS authored world scale.
            Transform original = null;
            int childCount = itemPrefab.transform.childCount;
            for (int i = 0; i < childCount; i++)
            {
                Transform child = itemPrefab.transform.GetChild(i);
                if (backAttach && child.gameObject.name == "attach_back") { original = child; break; }
                if (child.gameObject.name == "attach" || (!backAttach && child.gameObject.name == "attach_skin")) { original = child; break; }
            }
            if (original != null)
            {
                Vector3 authored = original.lossyScale;
                Vector3 jointLossy = joint.lossyScale;
                if (!Mathf.Approximately(jointLossy.x, 0f) && !Mathf.Approximately(jointLossy.y, 0f) && !Mathf.Approximately(jointLossy.z, 0f))
                {
                    float bodyScale = __instance.transform.lossyScale.y;
                    Vector3 corrected = new Vector3(authored.x * bodyScale / jointLossy.x, authored.y * bodyScale / jointLossy.y, authored.z * bodyScale / jointLossy.z);
                    // Only touch it when meaningfully wrong — vanilla-authored items already match, and a
                    // no-op write every re-equip would be pointless churn.
                    Vector3 current = __result.transform.localScale;
                    if ((corrected - current).sqrMagnitude >= 0.0001f)
                    {
                        core = $"inherits body scale {bodyScale:0.00} (rigid: localScale {current} -> {corrected})";
                        __result.transform.localScale = corrected;
                    }
                }
            }
            Evidence(__instance, itemHash, joint, __result, core);
        }

        // ── Evidence ──────────────────────────────────────────────────────────────────────────────────────────────
        private static readonly HashSet<int> s_skeletonLogged = new HashSet<int>();
        private static string[] s_playerBones;

        private static string Peer()
        {
            if (ZNet.instance != null && ZNet.instance.IsDedicated()) return "server";
            return System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-firesbot") >= 0 ? "bot" : "client";
        }

        private static string NameOf(VisEquipment vis)
        {
            var controller = vis.GetComponent<CompanionController>();
            return controller != null && !string.IsNullOrEmpty(controller.companionName) ? controller.companionName : vis.name;
        }

        private static string[] PlayerBones()
        {
            if (s_playerBones != null) return s_playerBones;
            var player = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab("Player") : null;
            var bones = player != null ? player.GetComponent<VisEquipment>()?.m_bodyModel?.bones : null;
            if (bones == null) return null;
            s_playerBones = new string[bones.Length];
            for (int i = 0; i < bones.Length; i++) s_playerBones[i] = bones[i] != null ? bones[i].name : "<null>";
            return s_playerBones;
        }

        // Vanilla gives a skinned item the body's bone ARRAY by index (AttachItem: smr.bones = m_bodyModel.bones), and item
        // meshes are skinned against the Player's order: a companion skeleton whose bones differ by index skins every hair,
        // beard, cape and armour piece to the wrong bones. Once per companion body.
        private static void LogSkeleton(VisEquipment vis)
        {
            if (!s_skeletonLogged.Add(vis.GetInstanceID())) return;
            var body = vis.m_bodyModel;
            var npcBones = body != null ? body.bones : null;
            var player = PlayerBones();
            if (npcBones == null || player == null)
            {
                Debug.Log($"[NpcSkeleton] '{NameOf(vis)}' on {Peer()}: body bones {(npcBones == null ? "missing" : npcBones.Length.ToString())}, "
                          + $"Player bones {(player == null ? "missing" : player.Length.ToString())}");
                return;
            }
            int same = 0;
            var diffs = new List<string>();
            for (int i = 0; i < Mathf.Max(npcBones.Length, player.Length); i++)
            {
                string n = i < npcBones.Length ? (npcBones[i] != null ? npcBones[i].name : "<null>") : "<none>";
                string p = i < player.Length ? player[i] : "<none>";
                if (n == p) same++;
                else if (diffs.Count < 6) diffs.Add($"{i}:{n}≠{p}");
            }
            var npcSet = new HashSet<string>();
            foreach (var b in npcBones) if (b != null) npcSet.Add(b.name);
            int missing = 0;
            foreach (var p in player) if (!npcSet.Contains(p)) missing++;
            Debug.Log($"[NpcSkeleton] '{NameOf(vis)}' on {Peer()}: body bones {npcBones.Length} vs Player {player.Length}; same name at same index "
                      + $"{same}/{player.Length}; Player bones missing by name {missing}; mesh '{(body.sharedMesh != null ? body.sharedMesh.name : "-")}' "
                      + $"root '{(body.rootBone != null ? body.rootBone.name : "-")}' body scale {vis.transform.lossyScale.y:0.00} on root, attaches inherit"
                      + (diffs.Count > 0 ? $"; first differences {string.Join(", ", diffs)}" : ""));
        }

        private static void Evidence(VisEquipment vis, int itemHash, Transform joint, GameObject instance, string core)
        {
            try
            {
                LogSkeleton(vis);
                var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(itemHash) : null;
                var t = instance.transform;
                string kind = t.parent == joint ? "rigid" : "skinned";
                var line = new System.Text.StringBuilder();
                line.Append($"[NpcAttach] '{NameOf(vis)}' on {Peer()}: {(prefab != null ? prefab.name : itemHash.ToString())} {kind} '{instance.name}' ")
                    .Append($"parent '{(t.parent != null ? t.parent.name : "-")}' joint '{(joint != null ? joint.name : "-")}' ")
                    .Append($"localPos {t.localPosition} localScale {t.localScale} lossy {t.lossyScale.y:0.00}; core: {core}");
                var skeleton = vis.m_bodyModel != null && vis.m_bodyModel.bones != null ? new HashSet<Transform>(vis.m_bodyModel.bones) : null;
                foreach (var smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    int onBody = 0, n = smr.bones != null ? smr.bones.Length : 0;
                    if (skeleton != null && smr.bones != null) foreach (var b in smr.bones) if (b != null && skeleton.Contains(b)) onBody++;
                    int poses = smr.sharedMesh != null ? smr.sharedMesh.bindposes.Length : 0;
                    line.Append($" | smr '{smr.name}' bones {onBody}/{n} on body, bindposes {poses}, root '{(smr.rootBone != null ? smr.rootBone.name : "-")}'");
                    AppendMaterial(line, smr);
                }
                foreach (var mr in instance.GetComponentsInChildren<MeshRenderer>(true))
                {
                    line.Append($" | mr '{mr.name}'");
                    AppendMaterial(line, mr);
                }
                foreach (var cloth in instance.GetComponentsInChildren<MagicaCloth2.MagicaCloth>(true))
                    line.Append($" | cloth '{cloth.name}' enabled {cloth.enabled} lossy {cloth.transform.lossyScale.y:0.00}");
                Debug.Log(line.ToString());
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[NpcAttach] evidence failed for {vis.name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void AppendMaterial(System.Text.StringBuilder line, Renderer renderer)
        {
            var mat = renderer.sharedMaterial;
            if (mat == null) { line.Append(" mat none"); return; }
            line.Append($" mat '{mat.name}' shader '{(mat.shader != null ? mat.shader.name : "null")}'");
            if (mat.HasProperty("_Color")) line.Append($" _Color {mat.GetColor("_Color")}");
            if (mat.HasProperty("_HairColor")) line.Append($" _HairColor {mat.GetColor("_HairColor")}");
        }
    }
}
