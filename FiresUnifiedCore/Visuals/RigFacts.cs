using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace FiresCore.Visuals
{
    /// <summary>
    /// Console command fires_rig_facts [Model ...] dumps, per
    /// model prefab, the facts vanilla's VisEquipment attach uses - read off the PREFAB ASSETS, nothing spawned:
    /// the helmet joint (VisEquipment.m_helmet: path, parent, local pos/rot/scale, lossy scale), the head bone (name match) and the
    /// helmet joint in head space, and m_bodyModel's bone list (vanilla binds skinned armour BY INDEX: bones = m_bodyModel.bones) compared
    /// with the player's: same index + same name, and the player bone names the model lacks. Writes BepInEx\FiresRigFacts.md.
    /// </summary>
    internal static class RigFacts
    {
        private static readonly string[] Default = { "Player", "Draugr", "Draugr_Elite", "Skeleton", "Dverger", "Greyling", "Goblin", "Fenring" };
        private static bool s_cmd;

        internal static string Dump(IList<string> names)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# Monster rig facts (Core {FiresUnifiedCore.PluginVersion}, {DateTime.Now:yyyy-MM-dd HH:mm}, from prefab assets via ZNetScene)");
            sb.AppendLine();
            var zs = ZNetScene.instance;
            if (zs == null) return "no ZNetScene (load a world first)";
            string[] playerBones = BonesOf(zs.GetPrefab("Player"));
            sb.AppendLine("| Model | VisEquipment | helmet joint path | joint parent | joint local pos | joint local euler | joint local scale | joint lossy scale | head bone | head lossy scale | joint in head space (pos / euler) | body bones | same index+name as player | player bones missing on model |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            var detail = new StringBuilder();
            foreach (string name in names)
            {
                var go = zs.GetPrefab(name);
                if (go == null) { sb.AppendLine($"| {name} | prefab not found |||||||||||||"); continue; }
                var vis = go.GetComponentInChildren<VisEquipment>(true);
                Transform joint = vis != null ? vis.m_helmet : null;
                var body = vis != null ? vis.m_bodyModel : null;
                Transform[] bones = body != null ? body.bones : new Transform[0];
                Transform head = bones.FirstOrDefault(b => b != null && b.name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0 && b.name.IndexOf("end", StringComparison.OrdinalIgnoreCase) < 0)
                                 ?? FindDeep(go.transform, "head");
                string inHead = "-";
                if (joint != null && head != null)
                {
                    Vector3 p = head.InverseTransformPoint(joint.position);
                    Vector3 e = (Quaternion.Inverse(head.rotation) * joint.rotation).eulerAngles;
                    inHead = $"{V(p)} / {V(e, "0")}";
                }
                string[] names2 = bones.Select(b => b != null ? b.name : "(null)").ToArray();
                int same = 0;
                for (int i = 0; i < Math.Min(names2.Length, playerBones.Length); i++) if (names2[i] == playerBones[i]) same++;
                var missing = playerBones.Where(pb => !names2.Contains(pb)).ToArray();
                sb.AppendLine($"| {name} | {(vis != null ? "yes" : "NO")} | {(joint != null ? PathOf(go.transform, joint) : "none")} | {(joint != null && joint.parent != null ? joint.parent.name : "-")} | " +
                              $"{(joint != null ? V(joint.localPosition) : "-")} | {(joint != null ? V(joint.localEulerAngles, "0") : "-")} | {(joint != null ? V(joint.localScale) : "-")} | {(joint != null ? V(joint.lossyScale) : "-")} | " +
                              $"{(head != null ? head.name : "none")} | {(head != null ? V(head.lossyScale) : "-")} | {inHead} | {names2.Length} | {same}/{playerBones.Length} | {missing.Length} |");
                detail.AppendLine($"## {name}");
                detail.AppendLine($"- body renderer: {(body != null ? PathOf(go.transform, body.transform) + $" (root bone {(body.rootBone != null ? body.rootBone.name : "none")}, lossy {V(body.transform.lossyScale)})" : "none")}");
                detail.AppendLine($"- bone list by index (vanilla binds skinned armour index i -> this bone): {string.Join(", ", names2.Select((n, i) => $"{i}:{n}{(i < playerBones.Length && playerBones[i] != n ? $" (player: {playerBones[i]})" : "")}"))}");
                detail.AppendLine($"- player bones with no same-named bone here: {(missing.Length == 0 ? "none" : string.Join(", ", missing))}");
                detail.AppendLine();
            }
            sb.AppendLine();
            sb.Append(detail);
            return sb.ToString();
        }

        private static string[] BonesOf(GameObject go)
        {
            var vis = go != null ? go.GetComponentInChildren<VisEquipment>(true) : null;
            return vis != null && vis.m_bodyModel != null ? vis.m_bodyModel.bones.Select(b => b != null ? b.name : "(null)").ToArray() : new string[0];
        }

        private static Transform FindDeep(Transform t, string part)
        {
            foreach (var c in t.GetComponentsInChildren<Transform>(true))
                if (c.name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0 && c.name.IndexOf("attach", StringComparison.OrdinalIgnoreCase) < 0) return c;
            return null;
        }

        private static string PathOf(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        private static string V(Vector3 v, string f = "0.####") => $"{v.x.ToString(f)},{v.y.ToString(f)},{v.z.ToString(f)}";

        [HarmonyPatch(typeof(Terminal), "InitTerminal")]
        private static class RigFacts_InitTerminal_Patch
        {
            [HarmonyPostfix]
            private static void Postfix()
            {
                if (s_cmd) return;
                s_cmd = true;
                new Terminal.ConsoleCommand("fires_rig_facts",
                    "[FiresUnifiedCore] fires_rig_facts [Model ...] - dump helmet joint, head bone and body bone list facts per model prefab to BepInEx\\FiresRigFacts.md.",
                    args =>
                    {
                        try
                        {
                            var names = args.Length > 1 ? args.Args.Skip(1).ToList() : Default.ToList();
                            string text = Dump(names);
                            string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "FiresRigFacts.md");
                            File.WriteAllText(path, text);
                            FiresCore.Logging.FiresLogger.LogDebug($"[RigFacts] wrote {path} ({names.Count} models, {text.Length} chars)");
                            args.Context?.AddString($"fires_rig_facts: wrote {path}");
                        }
                        catch (Exception ex) { args.Context?.AddString("fires_rig_facts failed: " + ex.Message); }
                    });
            }
        }
    }
}
