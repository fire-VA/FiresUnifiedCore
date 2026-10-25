using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using FiresCore.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Visuals
{
    /// <summary>
    /// Vanilla shows
    /// those by setting _ChestTex / _LegsTex on the PLAYER's body material; a monster's creature shader has only _MainTex
    /// (Tools\MONSTER_RIG_FACTS.md). With [MonsterArmorFit] "Paint armour textures on monsters" ON (client, default OFF), a registered
    /// monster's body gets a composite of its own _MainTex with the worn chest/legs armour texture painted over the matching body region:
    /// the body mesh is rasterised in UV space on the CPU, each texel's region comes from its triangle's main bones (legs = hips down,
    /// torso = spine/chest/upper arms), and the armour colour is a box projection of the item's texture in bind space. Built once per
    /// (model, chest item, legs item), cached, and put on the body renderer's own material instance; unequipping restores the original.
    /// </summary>
    internal static class ArmourPaint
    {
        private static ConfigEntry<bool> s_on;
        private static readonly int ChestTex = Shader.PropertyToID("_ChestTex"), LegsTex = Shader.PropertyToID("_LegsTex"), MainTex = Shader.PropertyToID("_MainTex");
        private static readonly Dictionary<string, Texture2D> s_cache = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private static readonly AccessTools.FieldRef<VisEquipment, int> s_chest = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentChestItemHash");
        private static readonly AccessTools.FieldRef<VisEquipment, int> s_legs = AccessTools.FieldRefAccess<VisEquipment, int>("m_currentLegItemHash");

        private sealed class State : MonoBehaviour { public string Key = ""; public Texture Original; public bool HasOriginal; }

        internal static void Bind(ConfigFile config)
        {
            s_on = config.Bind("MonsterArmorFit", "Paint armour textures on monsters", false,
                "CLIENT. Off (default): vanilla. On: armour that vanilla paints onto the player's body (leather/rags legs, the leather chest's torso, the painted part of other armour) is painted onto a registered monster's own body texture.");
        }

        [HarmonyPatch(typeof(VisEquipment), "UpdateEquipmentVisuals")]
        private static class UpdatePatch
        {
            [HarmonyPriority(Priority.Last)]
            private static void Postfix(VisEquipment __instance)
            {
                try { Sync(__instance); }
                catch (Exception ex) { FiresLogger.LogDebug($"[ArmourPaint] failed: {ex.GetType().Name}: {ex.Message}"); }
            }
        }

        private static void Sync(VisEquipment vis)
        {
            if (s_on == null || !s_on.Value || vis == null || vis.m_isPlayer || vis.m_bodyModel == null) return;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return;
            var character = vis.GetComponent<Character>();
            if (character == null || character is Player) return;
            string model = Utils.GetPrefabName(character.gameObject);
            if (!MonsterArmorFit.IsRegistered(model)) return;
            int chest = s_chest(vis), legs = s_legs(vis);
            var state = vis.GetComponent<State>() ?? vis.gameObject.AddComponent<State>();
            string key = chest + "|" + legs;
            if (key == state.Key) return;
            state.Key = key;
            var mat = vis.m_bodyModel.material;   // vanilla already uses a per-instance material on monsters (SetChestEquipped)
            if (!state.HasOriginal) { state.Original = mat.mainTexture; state.HasOriginal = true; }
            Texture chestTex = ArmourTex(chest, ChestTex), legsTex = ArmourTex(legs, LegsTex);
            if (chestTex == null && legsTex == null) { mat.mainTexture = state.Original; FiresLogger.LogDebug($"[ArmourPaint] {model}: no painted armour, original texture"); return; }
            string cacheKey = model + "|" + (chestTex != null ? chestTex.name : "-") + "|" + (legsTex != null ? legsTex.name : "-");
            if (!s_cache.TryGetValue(cacheKey, out var composite) || composite == null)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                composite = Build(vis, state.Original, chestTex, legsTex, model, out string why);
                if (composite == null) { FiresLogger.LogDebug($"[ArmourPaint] {model}: not painted ({why})"); return; }
                s_cache[cacheKey] = composite;
                FiresLogger.LogDebug($"[ArmourPaint] {model}: built {cacheKey} ({composite.width}x{composite.height}) in {sw.Elapsed.TotalMilliseconds:0} ms; {why}");
            }
            mat.mainTexture = composite;
            FiresLogger.LogDebug($"[ArmourPaint] {model} {character.GetZDOID()}: painted {cacheKey}");
        }

        private static Texture ArmourTex(int hash, int prop)
        {
            if (hash == 0 || ObjectDB.instance == null) return null;
            var go = ObjectDB.instance.GetItemPrefab(hash);
            var m = go != null ? go.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_armorMaterial : null;
            return m != null && m.HasProperty(prop) ? m.GetTexture(prop) : null;
        }

        private static Color32[] Read(Texture t, int w, int h)
        {
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(t, rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                tex.Apply(false, false);
                var px = tex.GetPixels32();
                UnityEngine.Object.Destroy(tex);
                return px;
            }
            finally { RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt); }
        }

        private enum Region : byte { None, Torso, Legs }

        private static Region RegionOf(Transform bone, HashSet<Transform> torso, HashSet<Transform> legs)
        {
            for (var t = bone; t != null; t = t.parent)
            {
                if (legs.Contains(t)) return Region.Legs;
                if (torso.Contains(t)) return Region.Torso;
            }
            return Region.None;
        }

        private static Texture2D Build(VisEquipment vis, Texture original, Texture chestTex, Texture legsTex, string model, out string why)
        {
            why = "";
            var smr = vis.m_bodyModel;
            var anim = vis.GetComponentInChildren<Animator>();
            if (original == null) { why = "body has no main texture"; return null; }
            if (anim == null || anim.avatar == null || !anim.avatar.isHuman) { why = "no humanoid avatar (regions need it)"; return null; }
            var mesh = MeshReadback.Readable(smr.sharedMesh, out string path);
            if (mesh == null) { why = "body mesh not readable (" + path + ")"; return null; }
            var uv = mesh.uv; var pos = mesh.vertices; var nrm = mesh.normals; var bw = mesh.boneWeights; var bones = smr.bones;
            if (uv == null || uv.Length != pos.Length || bw == null || bw.Length != pos.Length) { why = "mesh has no uv/weights"; return null; }

            // Regions from the humanoid map: legs = hips and below, torso = spine/chest/neck-down and upper arms.
            var legsSet = new HashSet<Transform>(); var torsoSet = new HashSet<Transform>();
            foreach (var b in new[] { HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg }) { var t = anim.GetBoneTransform(b); if (t != null) legsSet.Add(t); }
            var hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            foreach (var b in new[] { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.UpperChest, HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm })
            { var t = anim.GetBoneTransform(b); if (t != null) torsoSet.Add(t); }
            var head = anim.GetBoneTransform(HumanBodyBones.Neck) ?? anim.GetBoneTransform(HumanBodyBones.Head);
            var lowerArmL = anim.GetBoneTransform(HumanBodyBones.LeftLowerArm); var lowerArmR = anim.GetBoneTransform(HumanBodyBones.RightLowerArm);
            var region = new Region[pos.Length];
            for (int i = 0; i < pos.Length; i++)
            {
                int bi = bw[i].boneIndex0;
                var b = bi >= 0 && bi < bones.Length ? bones[bi] : null;
                if (b == null) continue;
                // neck/head and forearms/hands stay skin (the player's chest texture stops at the upper arm)
                bool excluded = false;
                for (var t = b; t != null; t = t.parent) if (t == head || t == lowerArmL || t == lowerArmR) { excluded = true; break; }
                if (excluded) continue;
                region[i] = b == hips ? Region.Legs : RegionOf(b, torsoSet, legsSet);
            }

            int w = original.width, h = original.height;
            var px = Read(original, w, h);
            Color32[] cPx = null, lPx = null; int cw = 0, ch = 0, lw = 0, lh = 0;
            if (chestTex != null) { cw = chestTex.width; ch = chestTex.height; cPx = Read(chestTex, cw, ch); }
            if (legsTex != null) { lw = legsTex.width; lh = legsTex.height; lPx = Read(legsTex, lw, lh); }

            // Box-projection frame per region: the bind-space bounds of that region's vertices.
            Bounds tb = default, lb = default; bool tAny = false, lAny = false;
            for (int i = 0; i < pos.Length; i++)
            {
                if (region[i] == Region.Torso) { if (!tAny) { tb = new Bounds(pos[i], Vector3.zero); tAny = true; } else tb.Encapsulate(pos[i]); }
                else if (region[i] == Region.Legs) { if (!lAny) { lb = new Bounds(pos[i], Vector3.zero); lAny = true; } else lb.Encapsulate(pos[i]); }
            }

            int painted = 0, tris = 0;
            var idx = mesh.GetTriangles(0);
            for (int t = 0; t < idx.Length; t += 3)
            {
                int a = idx[t], b = idx[t + 1], c = idx[t + 2];
                Region r = Majority(region[a], region[b], region[c]);
                if (r == Region.None) continue;
                Color32[] src = r == Region.Torso ? cPx : lPx;
                if (src == null) continue;
                int sw = r == Region.Torso ? cw : lw, sh = r == Region.Torso ? ch : lh;
                Bounds bb = r == Region.Torso ? tb : lb;
                tris++;
                painted += Raster(px, w, h, uv[a], uv[b], uv[c], pos[a], pos[b], pos[c], nrm != null && nrm.Length == pos.Length ? (nrm[a] + nrm[b] + nrm[c]) : Vector3.forward, src, sw, sh, bb);
            }
            if (painted == 0) { why = "no body texels in the armour regions"; return null; }
            var outTex = new Texture2D(w, h, TextureFormat.RGBA32, true, false) { name = "FiresArmourPaint_" + model, wrapMode = TextureWrapMode.Clamp };
            outTex.SetPixels32(px);
            outTex.Apply(true, true);   // mipmaps, then non-readable (GPU only)
            why = $"{tris} triangles, {painted} texels painted";
            return outTex;
        }

        private static Region Majority(Region a, Region b, Region c) => a == b || a == c ? a : b == c ? b : Region.None;

        // Fills the triangle's texels: armour colour by box projection (the plane facing the triangle's normal), alpha-blended over the body.
        private static int Raster(Color32[] dst, int w, int h, Vector2 ua, Vector2 ub, Vector2 uc, Vector3 pa, Vector3 pb, Vector3 pc, Vector3 n,
            Color32[] src, int sw, int sh, Bounds bb)
        {
            Vector2 a = new Vector2(Frac(ua.x) * w, Frac(ua.y) * h), b = new Vector2(Frac(ub.x) * w, Frac(ub.y) * h), c = new Vector2(Frac(uc.x) * w, Frac(uc.y) * h);
            int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x)))), x1 = Mathf.Min(w - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y)))), y1 = Mathf.Min(h - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
            float area = (b.x - a.x) * (c.y - a.y) - (c.x - a.x) * (b.y - a.y);
            if (Mathf.Abs(area) < 1e-6f || (x1 - x0) * (y1 - y0) > 512 * 512) return 0;   // degenerate, or a triangle spanning a UV wrap
            Vector3 an = new Vector3(Mathf.Abs(n.x), Mathf.Abs(n.y), Mathf.Abs(n.z));
            int axis = an.x >= an.y && an.x >= an.z ? 0 : an.y >= an.z ? 1 : 2;   // drop the axis the face points along
            Vector3 size = bb.size; size.x = Mathf.Max(size.x, 1e-4f); size.y = Mathf.Max(size.y, 1e-4f); size.z = Mathf.Max(size.z, 1e-4f);
            int n0 = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f);
                    float wa = ((b.x - p.x) * (c.y - p.y) - (c.x - p.x) * (b.y - p.y)) / area;
                    float wb = ((c.x - p.x) * (a.y - p.y) - (a.x - p.x) * (c.y - p.y)) / area;
                    float wc = 1f - wa - wb;
                    if (wa < -0.01f || wb < -0.01f || wc < -0.01f) continue;
                    Vector3 q = pa * wa + pb * wb + pc * wc;
                    Vector3 l = new Vector3((q.x - bb.min.x) / size.x, (q.y - bb.min.y) / size.y, (q.z - bb.min.z) / size.z);
                    float s = axis == 0 ? l.z : l.x, t = axis == 1 ? l.z : l.y;
                    int sx = Mathf.Clamp((int)(s * (sw - 1)), 0, sw - 1), sy = Mathf.Clamp((int)(t * (sh - 1)), 0, sh - 1);
                    Color32 ar = src[sy * sw + sx];
                    if (ar.a < 8) continue;
                    int i = y * w + x;
                    Color32 o = dst[i];
                    float k = ar.a / 255f;
                    dst[i] = new Color32((byte)(o.r + (ar.r - o.r) * k), (byte)(o.g + (ar.g - o.g) * k), (byte)(o.b + (ar.b - o.b) * k), o.a);
                    n0++;
                }
            return n0;
        }

        private static float Frac(float v) => v - Mathf.Floor(v);
    }
}
