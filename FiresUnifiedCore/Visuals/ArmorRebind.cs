using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FiresCore.Visuals
{
    /// <summary>
    /// Moves a skinned armour (Tools\VCS_MODEL_SWAP_DESIGN.md §2; working armour on swapped models is the goal)
    /// piece from one humanoid body onto another. It is
    /// FiresCore.Visuals.ArmorRebind (public, add-only) with exactly this shape - FMO monster gear and race companions call it too.
    ///   bones      player bone -> its HumanBodyBones id -> the other body's bone (nearest humanoid ancestor for helper bones)
    ///   bindposes  rebuilt so each vertex keeps its offset from its bone and follows the new bone
    ///   girth      per bone, front/back and left/right scaled separately to the bodies' measured skin, centre shifted to the new
    ///              body's skin centre (a belly pushes it forward); legs fit the typical skin so trousers don't balloon
    ///   push-out   vertices still inside the new body's skin move out along the nearest skin normal (1.2 cm), except on
    ///              "show-through" bones (e.g. the Draugr's belly hangs out over the armour)
    /// Both bodies must hold the same pose when Bind runs (HumanPoseFollower.Step just ran, or both in T-pose: tPose).
    /// </summary>
    public static class ArmorRebind
    {
        /// <summary>[MonsterArmorFit] "Split unmapped neck" (set by MonsterArmorFit.Bind; null = off).</summary>
        internal static System.Func<bool> SplitUnmappedNeck;

        internal static Transform FindDeepIgnoreCase(Transform root, string name)
        {
            if (root == null) return null;
            if (string.Equals(root.name, name, System.StringComparison.OrdinalIgnoreCase)) return root;
            for (int c = 0; c < root.childCount; c++) { var f = FindDeepIgnoreCase(root.GetChild(c), name); if (f != null) return f; }
            return null;
        }

        static string PathOf(Transform t)
        {
            if (t == null) return "(none)";
            var parts = new List<string>();
            for (var x = t; x != null && parts.Count < 12; x = x.parent) parts.Add(x.name);
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }
        static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int c = 0; c < root.childCount; c++) { var f = FindDeep(root.GetChild(c), name); if (f != null) return f; }
            return null;
        }

        const float Inflate = 1.04f, Clearance = 0.012f, MaxPush = 0.25f, Cell = 0.06f;

        public struct Ext { public float F, B, Rt, Lt; }

        public sealed class BodyProfile
        {
            public string Name;
            public Animator Animator;
            public readonly Dictionary<Transform, HumanBodyBones> Ids = new Dictionary<Transform, HumanBodyBones>();
            public Dictionary<HumanBodyBones, Ext> Outer, Typical;
            public HashSet<HumanBodyBones> ShowThrough = new HashSet<HumanBodyBones>();
            /// <summary>Fit this body's TYPICAL skin, not fur/spikes (armour was too bulky on skinny werewolves) - no extra
            /// inflation, shrinking allowed to 0.7, push-out capped at 4 cm. Set with ArmorRebind.MakeTight.</summary>
            public bool Tight;
            public float ShoulderSpan, HipSpan;
            public List<SkinnedMeshRenderer> Skin = new List<SkinnedMeshRenderer>();

            static readonly Dictionary<string, BodyProfile> s_named = new Dictionary<string, BodyProfile>(StringComparer.OrdinalIgnoreCase);
            public static void Register(BodyProfile p) { if (p != null && !string.IsNullOrEmpty(p.Name)) s_named[p.Name] = p; }
            public static bool TryGet(string name, out BodyProfile p) => s_named.TryGetValue(name ?? "", out p);
        }

        public struct RebindResult
        {
            public int Matched, ViaAncestor, Unmatched, PushedVerts;
            public string Profile, BodyPrefab;
            public string ReadPath;           // "cpu" (mesh readable), "gpu" (read back from the GPU) or "failed: ..."
            public string[] UnmatchedNames;
            /// <summary>Per used bone "itemBone>Humanoid>modelBone (n verts)", for the remap diagnostic.</summary>
            public string[] BoneMap;
            /// <summary>Per humanoid bone carrying vertices, the extent scale (sF front/back, sS side), the shift and the body turn applied.</summary>
            public string[] Scaling;
            /// <summary>How the item's Neck bone was bound (switch value, searches, result), or null.</summary>
            public string NeckDecision;
            public SkinnedMeshRenderer Result;
        }

        /// <summary>Measures a humanoid body once (bind space, so any pose). <paramref name="skin"/>: the body's own skin renderers.</summary>
        public static BodyProfile Profile(Animator body, IEnumerable<SkinnedMeshRenderer> skin, string name = null)
        {
            var p = new BodyProfile { Name = name ?? body.name, Animator = body };
            foreach (HumanBodyBones b in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (b == HumanBodyBones.LastBone) continue;
                var t = body.GetBoneTransform(b);
                if (t != null && !p.Ids.ContainsKey(t)) p.Ids[t] = b;
            }
            p.Skin = skin.Where(s => s != null && s.sharedMesh != null).ToList();
            p.Outer = Extents(p, 0.95f);
            p.Typical = Extents(p, 0.85f);
            // an UpperChest the player lacks: the player's Chest armour has to clear the bigger of the two
            foreach (var d in new[] { p.Outer, p.Typical })
                if (d.TryGetValue(HumanBodyBones.UpperChest, out var uc) && d.TryGetValue(HumanBodyBones.Chest, out var ch))
                    d[HumanBodyBones.Chest] = new Ext { F = Mathf.Max(ch.F, uc.F), B = Mathf.Max(ch.B, uc.B), Rt = Mathf.Max(ch.Rt, uc.Rt), Lt = Mathf.Max(ch.Lt, uc.Lt) };
            p.ShoulderSpan = Span(body, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm);
            p.HipSpan = Span(body, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg);
            return p;
        }

        /// <summary>0.8.5: re-measures <paramref name="p"/> at its typical skin (80th percentile) everywhere and marks it Tight.</summary>
        public static void MakeTight(BodyProfile p)
        {
            if (p == null) return;
            p.Tight = true;
            p.Outer = Extents(p, 0.8f);
            p.Typical = p.Outer;
        }

        /// <summary>Rebinds <paramref name="item"/> (skinned to <paramref name="from"/>'s rig) onto <paramref name="to"/>: a new renderer
        /// under <paramref name="parent"/>. The source renderer is left as it is (the caller hides it).</summary>
        public static RebindResult Bind(SkinnedMeshRenderer item, BodyProfile from, BodyProfile to, Transform parent, bool tPose = false)
            => Bind(item, from, to, parent, tPose, false);

        /// <summary><paramref name="nameAnchorUnmatched"/> true = an item bone with no
        /// humanoid match is not pinned to the Hips: (a) carried rigidly onto the model's humanoid bone matching the NAME of the first
        /// ancestor that is a body bone (a cape's own armature copy, cape1 under Spine2), else (b) carried root to root.</summary>
        public static RebindResult Bind(SkinnedMeshRenderer item, BodyProfile from, BodyProfile to, Transform parent, bool tPose, bool nameAnchorUnmatched)
        {
            bool was = s_nameAnchorUnmatched;
            s_nameAnchorUnmatched = nameAnchorUnmatched;
            try { return BindCore(item, from, to, parent, tPose); }
            finally { s_nameAnchorUnmatched = was; }
        }

        [System.ThreadStatic] static bool s_nameAnchorUnmatched;

        static RebindResult BindCore(SkinnedMeshRenderer item, BodyProfile from, BodyProfile to, Transform parent, bool tPose)
        {
            var r = new RebindResult { Profile = to.Name, BodyPrefab = to.Animator != null ? to.Animator.transform.root.name : "" };
            if (item == null || item.sharedMesh == null) return r;
            if (!tPose) return BindPosed(item, from, to, parent, r);
            // tPose: both rigs into the same neutral human pose (all muscles 0) for the bind, then both put back - for bodies whose
            // live poses differ (a monster's armour attach). Root position/rotation are kept, so nothing moves in the world.
            HumanPoseHandler hf = null, ht = null;
            var savedF = new HumanPose(); var savedT = new HumanPose();
            try
            {
                if (from.Animator != null && from.Animator.avatar != null && from.Animator.avatar.isHuman) { hf = new HumanPoseHandler(from.Animator.avatar, from.Animator.transform); hf.GetHumanPose(ref savedF); Neutral(hf, savedF); }
                if (to.Animator != null && to.Animator.avatar != null && to.Animator.avatar.isHuman && to.Animator != from.Animator) { ht = new HumanPoseHandler(to.Animator.avatar, to.Animator.transform); ht.GetHumanPose(ref savedT); Neutral(ht, savedT); }
                r = BindPosed(item, from, to, parent, r);
                r.ReadPath = (r.ReadPath ?? "") + " (t-pose)";
                return r;
            }
            finally
            {
                if (hf != null) { hf.SetHumanPose(ref savedF); hf.Dispose(); }
                if (ht != null) { ht.SetHumanPose(ref savedT); ht.Dispose(); }
            }
        }

        static void Neutral(HumanPoseHandler h, HumanPose keep)
        {
            var p = new HumanPose { bodyPosition = keep.bodyPosition, bodyRotation = keep.bodyRotation, muscles = new float[keep.muscles != null ? keep.muscles.Length : HumanTrait.MuscleCount] };
            h.SetHumanPose(ref p);
        }

        static RebindResult BindPosed(SkinnedMeshRenderer item, BodyProfile from, BodyProfile to, Transform parent, RebindResult r)
        {
            var unmatched = new List<string>();
            var boneMap = new List<string>();
            string neckLog = null;
            var neckIdx = new List<int>(); int headIdx = -1;
            var scaling = new List<string>(); var scaled = new HashSet<HumanBodyBones>();
            float shoulder = from.ShoulderSpan > 0.001f && to.ShoulderSpan > 0.001f ? to.ShoulderSpan / from.ShoulderSpan : 1f;
            float hip = from.HipSpan > 0.001f && to.HipSpan > 0.001f ? to.HipSpan / from.HipSpan : 1f;

            var readable = MeshReadback.Readable(item.sharedMesh, out string path);
            r.ReadPath = path;
            if (readable == null) return r;   // no readable copy (e.g. headless): Result stays null, ReadPath says why - callers need no try/catch
            var mesh = UnityEngine.Object.Instantiate(readable);
            mesh.name = item.sharedMesh.name + "_vcs";
            var src = item.bones; var oldBind = readable.bindposes;
            var bones = new Transform[src.Length]; var bind = new Matrix4x4[oldBind.Length];
            // Vertices per bone (any weight >= 0.25), for the bone-map line
            var vertsPerBone = new int[src.Length];
            foreach (var w in readable.boneWeights)
            {
                if (w.weight0 >= 0.25f && w.boneIndex0 < vertsPerBone.Length) vertsPerBone[w.boneIndex0]++;
                if (w.weight1 >= 0.25f && w.boneIndex1 < vertsPerBone.Length) vertsPerBone[w.boneIndex1]++;
                if (w.weight2 >= 0.25f && w.boneIndex2 < vertsPerBone.Length) vertsPerBone[w.boneIndex2]++;
                if (w.weight3 >= 0.25f && w.boneIndex3 < vertsPerBone.Length) vertsPerBone[w.boneIndex3]++;
            }
            var skip = new bool[src.Length];
            // A renderer can carry more bones than its mesh has bind poses ("Index was outside the bounds" on a cape);
            // only the bound ones carry vertices, the rest point at the hips and are never used
            for (int i = oldBind.Length; i < src.Length; i++) bones[i] = to.Animator.GetBoneTransform(HumanBodyBones.Hips);
            for (int i = 0; i < src.Length && i < oldBind.Length; i++)
            {
                Transform pb = src[i], mb = null;
                if (pb == null) { bones[i] = to.Animator.transform; bind[i] = oldBind[i]; r.Unmatched++; unmatched.Add("(null)"); continue; }
                HumanBodyBones? id = null;
                Transform viaAncestor = null;
                // The item's humanoid Neck when the MONSTER has no Neck
                // (the Draugr's 21-bone body: head straight off Spine2) - remembered here, re-weighted after the loop
                if (from.Ids.TryGetValue(pb, out var pidN))
                {
                    if (pidN == HumanBodyBones.Neck && to.Animator.GetBoneTransform(HumanBodyBones.Neck) == null) neckIdx.Add(i);
                    else if (pidN == HumanBodyBones.Head && to.Animator.GetBoneTransform(HumanBodyBones.Head) != null) headIdx = i;
                }
                for (var t = pb; t != null && mb == null; t = t.parent)
                    if (from.Ids.TryGetValue(t, out var hid)) { id = hid; mb = to.Animator.GetBoneTransform(hid); if (mb != null && t != pb) { r.ViaAncestor++; viaAncestor = t; } }
                if (mb == null && s_nameAnchorUnmatched)
                {
                    // The piece's own bone (cape1) hangs under a
                    // bone of its own armature copy whose NAME is a real body bone (Spine2, Neck...): carried rigidly (no girth) onto the
                    // model's matching humanoid bone, so the cape starts at the upper back and moves with the chest.
                    HumanBodyBones? anchor = null; Transform srcAnchor = null;
                    for (var t = pb.parent; t != null && anchor == null; t = t.parent)
                        foreach (var kv in from.Ids)
                            if (kv.Key != null && kv.Key.name == t.name) { anchor = kv.Value; srcAnchor = kv.Key; break; }
                    var mAnchor = anchor.HasValue ? to.Animator.GetBoneTransform(anchor.Value) : null;
                    if (mAnchor != null && srcAnchor != null)
                    {
                        Vector3 aAx = HumanAxis(from.Animator, anchor.Value);
                        Vector3 mAx = HumanAxis(to.Animator, anchor.Value);
                        Quaternion da = BodyRot(to.Animator, mAx) * Quaternion.Inverse(BodyRot(from.Animator, aAx));
                        // pb's offset from the anchor is measured in the item's own armature (where pb lives), placed on the body's anchor
                        var itemAnchor = pb.parent; for (var t = pb.parent; t != null; t = t.parent) if (t.name == srcAnchor.name) { itemAnchor = t; break; }
                        var carryA = Matrix4x4.Translate(mAnchor.position) * Matrix4x4.Rotate(da) * Matrix4x4.Rotate(srcAnchor.rotation * Quaternion.Inverse(itemAnchor.rotation))
                                     * Matrix4x4.Translate(-itemAnchor.position);
                        bones[i] = mAnchor;
                        bind[i] = mAnchor.worldToLocalMatrix * carryA * pb.localToWorldMatrix * oldBind[i];
                        r.Unmatched++; unmatched.Add(pb.name + "->" + anchor.Value);
                        if (FiresCore.Logging.FiresLogger.DebugEnabled) boneMap.Add($"{pb.name} -> name anchor {srcAnchor.name} -> {anchor.Value} -> {mAnchor.name} ({(i < vertsPerBone.Length ? vertsPerBone[i] : 0)} v, rigid)");
                        continue;
                    }
                    // A bone of the piece's OWN in the
                    // wearer's ROOT space is carried root to root (no girth), so its vertices keep their place relative to the body's root
                    var fromRoot = from.Animator.transform; var toRoot = to.Animator.transform;
                    var dRoot = toRoot.rotation * Quaternion.Inverse(fromRoot.rotation);
                    var rootCarry = Matrix4x4.Translate(toRoot.position) * Matrix4x4.Rotate(dRoot) * Matrix4x4.Translate(-fromRoot.position);
                    bones[i] = toRoot;
                    bind[i] = toRoot.worldToLocalMatrix * rootCarry * pb.localToWorldMatrix * oldBind[i];
                    r.Unmatched++; unmatched.Add(pb.name);
                    if (FiresCore.Logging.FiresLogger.DebugEnabled) boneMap.Add($"{pb.name} -> UNMAPPED -> root {toRoot.name} ({(i < vertsPerBone.Length ? vertsPerBone[i] : 0)} v, root to root)");
                    continue;
                }
                if (mb == null) { mb = to.Animator.GetBoneTransform(HumanBodyBones.Hips); r.Unmatched++; unmatched.Add(pb.name); }
                else r.Matched++;
                // Every bone that did NOT map 1:1 - name -> (ancestor) -> humanoid id -> model bone, with
                // its vertex count; printed by MonsterArmorFit after the "gear:" line
                if (FiresCore.Logging.FiresLogger.DebugEnabled && (viaAncestor != null || id == null))
                    boneMap.Add($"{pb.name}{(viaAncestor != null ? $" via {viaAncestor.name}" : "")} -> {(id.HasValue ? id.Value.ToString() : "UNMAPPED")} -> {mb.name} ({(i < vertsPerBone.Length ? vertsPerBone[i] : 0)} v)");
                bones[i] = mb;
                bool through = id.HasValue && to.ShowThrough.Contains(id.Value);
                skip[i] = through;

                Vector3 axis = id.HasValue ? HumanAxis(from.Animator, id.Value) : BoneAxis(pb);
                var lin = Matrix4x4.identity; Vector3 shift = Vector3.zero;
                float s = (id.HasValue && IsLeg(id.Value) ? hip : shoulder) * (to.Tight ? 1f : Inflate);
                for (int a = 0; a < 3; a++) for (int c = 0; c < 3; c++) lin[a, c] = (a == c ? s : 0f) + (1f - s) * axis[a] * axis[c];
                var toExt = id.HasValue && IsLeg(id.Value) ? to.Typical : to.Outer;
                if (!through && id.HasValue && from.Typical.TryGetValue(id.Value, out var pe) && toExt.TryGetValue(id.Value, out var me)
                    && pe.F + pe.B > 0.01f && pe.Rt + pe.Lt > 0.01f)
                {
                    Basis(from.Animator, id.Value, out var ax, out var fw, out var sd);
                    float sF = Mathf.Clamp((me.F + me.B) / (pe.F + pe.B), to.Tight ? 0.7f : 0.85f, 2.6f) * (to.Tight ? 1f : Inflate);
                    float sS = Mathf.Clamp((me.Rt + me.Lt) / (pe.Rt + pe.Lt), to.Tight ? 0.7f : 0.85f, 2.6f) * (to.Tight ? 1f : Inflate);
                    for (int a = 0; a < 3; a++) for (int c = 0; c < 3; c++) lin[a, c] = ax[a] * ax[c] + sF * fw[a] * fw[c] + sS * sd[a] * sd[c];
                    shift = ((me.F - me.B) * 0.5f - sF / (to.Tight ? 1f : Inflate) * (pe.F - pe.B) * 0.5f) * fw + ((me.Rt - me.Lt) * 0.5f - sS / (to.Tight ? 1f : Inflate) * (pe.Rt - pe.Lt) * 0.5f) * sd;
                    // The extent scaling per humanoid bone that carries vertices
                    if (FiresCore.Logging.FiresLogger.DebugEnabled && i < vertsPerBone.Length && vertsPerBone[i] > 0 && scaled.Add(id.Value))
                        scaling.Add($"{id.Value} sF {sF:0.00} sS {sS:0.00} shift {shift.magnitude:0.000} m");
                }
                // The player bone's frame turned into the
                // model bone's, both from their BODY-derived fronts (legs + head, not the root): identity when the bodies agree, the turn
                // when they don't (a model left facing the other way, FMO's T-pose binds). The shift turns with it.
                Vector3 maxis = id.HasValue && to.Animator.GetBoneTransform(id.Value) != null ? HumanAxis(to.Animator, id.Value) : axis;
                Quaternion d = BodyRot(to.Animator, maxis) * Quaternion.Inverse(BodyRot(from.Animator, axis));
                if (FiresCore.Logging.FiresLogger.DebugEnabled && id.HasValue && i < vertsPerBone.Length && vertsPerBone[i] > 0 && Quaternion.Angle(Quaternion.identity, d) > 5f && scaled.Add((HumanBodyBones)(1000 + (int)id.Value)))
                    scaling.Add($"{id.Value} turned {Quaternion.Angle(Quaternion.identity, d):0} deg");
                var carry = Matrix4x4.Translate(mb.position + d * shift) * Matrix4x4.Rotate(d) * lin * Matrix4x4.Translate(-pb.position);
                bind[i] = mb.worldToLocalMatrix * carry * pb.localToWorldMatrix * oldBind[i];
            }
            // Each Neck weight split 50/50
            // between the monster's Head and the bone the walk chose (Spine2), so the ring bends halfway with the head
            if (neckIdx.Count > 0)
            {
                bool on = SplitUnmappedNeck != null && SplitUnmappedNeck();
                if (on && headIdx >= 0)
                {
                    var neckSet = new HashSet<int>(neckIdx);
                    var w = mesh.boneWeights;
                    int split = 0, noSlot = 0;
                    for (int v = 0; v < w.Length; v++)
                    {
                        var bw = w[v];
                        int[] bi = { bw.boneIndex0, bw.boneIndex1, bw.boneIndex2, bw.boneIndex3 };
                        float[] wt = { bw.weight0, bw.weight1, bw.weight2, bw.weight3 };
                        float moved = 0f;
                        for (int k = 0; k < 4; k++) if (wt[k] > 0f && neckSet.Contains(bi[k])) { moved += wt[k] * 0.5f; wt[k] *= 0.5f; }
                        if (moved <= 0f) continue;
                        int slot = -1;
                        for (int k = 0; k < 4 && slot < 0; k++) if (bi[k] == headIdx && wt[k] > 0f) slot = k;
                        for (int k = 0; k < 4 && slot < 0; k++) if (wt[k] <= 0f) { slot = k; bi[k] = headIdx; }
                        if (slot < 0) { noSlot++; continue; }   // all 4 slots in use: this vertex stays as it was
                        wt[slot] += moved;
                        w[v] = new BoneWeight { boneIndex0 = bi[0], boneIndex1 = bi[1], boneIndex2 = bi[2], boneIndex3 = bi[3], weight0 = wt[0], weight1 = wt[1], weight2 = wt[2], weight3 = wt[3] };
                        split++;
                    }
                    mesh.boneWeights = w;
                    neckLog = $"neck split: {split} vert(s) moved half their Neck weight to Head ({noSlot} left: no free weight slot); the monster has no Neck";
                }
                else neckLog = $"neck split: {(on ? "skipped, the item has no Head bone to share with" : "off")} ({neckIdx.Count} Neck bone(s) unmapped on this monster)";
            }
            mesh.bindposes = bind;
            r.PushedVerts = PushOut(mesh, bones, to, skip);            mesh.RecalculateBounds();

            var go = new GameObject(item.name + "_onModel");
            go.transform.SetParent(parent, false);
            go.layer = item.gameObject.layer;
            var dst = go.AddComponent<SkinnedMeshRenderer>();
            dst.sharedMesh = mesh;
            dst.bones = bones;
            dst.rootBone = to.Animator.GetBoneTransform(HumanBodyBones.Hips);
            dst.sharedMaterials = item.sharedMaterials;
            dst.shadowCastingMode = item.shadowCastingMode;
            dst.updateWhenOffscreen = true;
            r.Result = dst;
            r.UnmatchedNames = unmatched.ToArray();
            r.BoneMap = boneMap.ToArray();
            r.Scaling = scaling.ToArray();
            r.NeckDecision = neckLog;
            return r;
        }

        // ---- measuring ----
        static readonly Dictionary<HumanBodyBones, HumanBodyBones[]> ChainNext = new Dictionary<HumanBodyBones, HumanBodyBones[]>
        {
            { HumanBodyBones.Hips, new[] { HumanBodyBones.Spine } },
            { HumanBodyBones.Spine, new[] { HumanBodyBones.Chest, HumanBodyBones.Neck, HumanBodyBones.Head } },
            { HumanBodyBones.Chest, new[] { HumanBodyBones.UpperChest, HumanBodyBones.Neck, HumanBodyBones.Head } },
            { HumanBodyBones.UpperChest, new[] { HumanBodyBones.Neck, HumanBodyBones.Head } },
            { HumanBodyBones.Neck, new[] { HumanBodyBones.Head } },
            { HumanBodyBones.LeftShoulder, new[] { HumanBodyBones.LeftUpperArm } }, { HumanBodyBones.RightShoulder, new[] { HumanBodyBones.RightUpperArm } },
            { HumanBodyBones.LeftUpperArm, new[] { HumanBodyBones.LeftLowerArm } }, { HumanBodyBones.RightUpperArm, new[] { HumanBodyBones.RightLowerArm } },
            { HumanBodyBones.LeftLowerArm, new[] { HumanBodyBones.LeftHand } }, { HumanBodyBones.RightLowerArm, new[] { HumanBodyBones.RightHand } },
            { HumanBodyBones.LeftUpperLeg, new[] { HumanBodyBones.LeftLowerLeg } }, { HumanBodyBones.RightUpperLeg, new[] { HumanBodyBones.RightLowerLeg } },
            { HumanBodyBones.LeftLowerLeg, new[] { HumanBodyBones.LeftFoot } }, { HumanBodyBones.RightLowerLeg, new[] { HumanBodyBones.RightFoot } },
            { HumanBodyBones.LeftFoot, new[] { HumanBodyBones.LeftToes } }, { HumanBodyBones.RightFoot, new[] { HumanBodyBones.RightToes } },
        };

        public static bool IsLeg(HumanBodyBones id) =>
            id == HumanBodyBones.LeftUpperLeg || id == HumanBodyBones.RightUpperLeg || id == HumanBodyBones.LeftLowerLeg || id == HumanBodyBones.RightLowerLeg
            || id == HumanBodyBones.LeftFoot || id == HumanBodyBones.RightFoot || id == HumanBodyBones.LeftToes || id == HumanBodyBones.RightToes;

        static float Span(Animator a, HumanBodyBones l, HumanBodyBones r)
        {
            var tl = a.GetBoneTransform(l); var tr = a.GetBoneTransform(r);
            return tl != null && tr != null ? Vector3.Distance(tl.position, tr.position) : 0f;
        }

        /// <summary>The way a humanoid body faces, from its bones: right = RightUpperLeg - LeftUpperLeg, up = Head - Hips, front =
        /// Cross(right, up). The root's forward when a bone is missing. VcsModelSwap.Fit compares it on both rigs to turn a model round.</summary>
        public static Vector3 BodyFront(Animator a)
        {
            Transform rl = a.GetBoneTransform(HumanBodyBones.RightUpperLeg), ll = a.GetBoneTransform(HumanBodyBones.LeftUpperLeg),
                      head = a.GetBoneTransform(HumanBodyBones.Head), hips = a.GetBoneTransform(HumanBodyBones.Hips);
            if (rl == null || ll == null || head == null || hips == null) return a.transform.forward;
            Vector3 f = Vector3.Cross(rl.position - ll.position, head.position - hips.position);
            return f.sqrMagnitude > 1e-8f ? f.normalized : a.transform.forward;
        }

        static Quaternion BodyRot(Animator a, Vector3 axis)
        {
            Vector3 f = BodyFront(a); f -= Vector3.Dot(f, axis) * axis;
            return f.sqrMagnitude > 1e-6f && axis.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(f.normalized, axis) : Quaternion.identity;
        }

        static Vector3 HumanAxis(Animator a, HumanBodyBones id)
        {
            var t = a.GetBoneTransform(id);
            if (ChainNext.TryGetValue(id, out var nexts))
                foreach (var n in nexts) { var c = a.GetBoneTransform(n); if (c != null && (c.position - t.position).sqrMagnitude > 1e-8f) return (c.position - t.position).normalized; }
            return BoneAxis(t);
        }

        static Vector3 BoneAxis(Transform b)
        {
            Transform best = null; float d = 0f;
            foreach (Transform c in b) { float cd = (c.position - b.position).sqrMagnitude; if (cd > d) { d = cd; best = c; } }
            Vector3 v = best != null && d > 1e-8f ? best.position - b.position : (b.parent != null ? b.position - b.parent.position : Vector3.up);
            return v.sqrMagnitude > 1e-10f ? v.normalized : Vector3.up;
        }

        static void Basis(Animator a, HumanBodyBones id, out Vector3 ax, out Vector3 fw, out Vector3 sd)
        {
            ax = HumanAxis(a, id);
            fw = a.transform.forward - Vector3.Dot(a.transform.forward, ax) * ax;
            if (fw.sqrMagnitude < 1e-6f) fw = a.transform.up - Vector3.Dot(a.transform.up, ax) * ax;
            fw.Normalize();
            sd = Vector3.Cross(ax, fw);
        }

        static HumanBodyBones? IdOf(BodyProfile p, Transform b)
        {
            for (var t = b; t != null; t = t.parent) if (p.Ids.TryGetValue(t, out var id)) return id;
            return null;
        }

        static Dictionary<HumanBodyBones, Ext> Extents(BodyProfile p, float pct)
        {
            var lists = new Dictionary<HumanBodyBones, List<float>[]>();
            foreach (var smr in p.Skin)
            {
                var mesh = MeshReadback.Readable(smr.sharedMesh, out _);
                if (mesh == null) continue;   // no readable copy: no girth data for this part (falls back to span ratios)
                var verts = mesh.vertices; var w = mesh.boneWeights; var bp = mesh.bindposes;
                if (w == null || w.Length != verts.Length) continue;
                for (int i = 0; i < verts.Length; i++)
                {
                    int bi = w[i].boneIndex0;
                    if (bi < 0 || bi >= smr.bones.Length || bi >= bp.Length || smr.bones[bi] == null) continue;
                    var id = IdOf(p, smr.bones[bi]);
                    if (id == null) continue;
                    var bt = p.Animator.GetBoneTransform(id.Value);
                    Vector3 v = smr.bones[bi].localToWorldMatrix.MultiplyPoint3x4(bp[bi].MultiplyPoint3x4(verts[i])) - bt.position;
                    Basis(p.Animator, id.Value, out _, out var fw, out var sd);
                    if (!lists.TryGetValue(id.Value, out var l)) lists[id.Value] = l = new[] { new List<float>(), new List<float>(), new List<float>(), new List<float>() };
                    float f = Vector3.Dot(v, fw), s = Vector3.Dot(v, sd);
                    if (f >= 0) l[0].Add(f); else l[1].Add(-f);
                    if (s >= 0) l[2].Add(s); else l[3].Add(-s);
                }
            }
            float P(List<float> l) { if (l.Count == 0) return 0f; l.Sort(); return l[Mathf.Min(l.Count - 1, (int)(l.Count * pct))]; }
            return lists.Where(kv => kv.Value.Sum(x => x.Count) >= 20)
                        .ToDictionary(kv => kv.Key, kv => new Ext { F = P(kv.Value[0]), B = P(kv.Value[1]), Rt = P(kv.Value[2]), Lt = P(kv.Value[3]) });
        }

        // ---- push-out ----
        static Matrix4x4 SkinMatrix(BoneWeight w, Transform[] bones, Matrix4x4[] bind)
        {
            var m = new Matrix4x4();
            void Add(int i, float wt) { if (wt <= 0f || i < 0 || i >= bones.Length || i >= bind.Length || bones[i] == null) return; var b = bones[i].localToWorldMatrix * bind[i]; for (int k = 0; k < 16; k++) m[k] += b[k] * wt; }
            Add(w.boneIndex0, w.weight0); Add(w.boneIndex1, w.weight1); Add(w.boneIndex2, w.weight2); Add(w.boneIndex3, w.weight3);
            return m;
        }

        static int PushOut(Mesh mesh, Transform[] bones, BodyProfile to, bool[] skip)
        {
            if (!mesh.isReadable) return 0;
            // the target skin in its CURRENT pose, in a coarse grid for nearest-point lookups
            var grid = new Dictionary<Vector3Int, List<int>>();
            var sp = new List<Vector3>(); var sn = new List<Vector3>();
            foreach (var smr in to.Skin)
            {
                var m = MeshReadback.Readable(smr.sharedMesh, out _);
                if (m == null) continue;
                var v = m.vertices; var n = m.normals; var w = m.boneWeights; var bp = m.bindposes;
                if (w.Length != v.Length || n.Length != v.Length) continue;
                for (int i = 0; i < v.Length; i++)
                {
                    var k = SkinMatrix(w[i], smr.bones, bp);
                    var p = k.MultiplyPoint3x4(v[i]);
                    var key = Key(p);
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
                    list.Add(sp.Count);
                    sp.Add(p); sn.Add(k.MultiplyVector(n[i]).normalized);
                }
            }
            if (sp.Count == 0) return 0;
            var av = mesh.vertices; var aw = mesh.boneWeights; var ab = mesh.bindposes;
            int moved = 0;
            for (int i = 0; i < av.Length; i++)
            {
                if (aw[i].boneIndex0 < skip.Length && skip[aw[i].boneIndex0]) continue;
                var k = SkinMatrix(aw[i], bones, ab);
                Vector3 a = k.MultiplyPoint3x4(av[i]);
                var c = Key(a);
                int best = -1; float bd = float.MaxValue;
                for (int x = -2; x <= 2; x++) for (int y = -2; y <= 2; y++) for (int z = -2; z <= 2; z++)
                    if (grid.TryGetValue(new Vector3Int(c.x + x, c.y + y, c.z + z), out var list))
                        foreach (int j in list) { float d = (sp[j] - a).sqrMagnitude; if (d < bd) { bd = d; best = j; } }
                if (best < 0) continue;
                float depth = Vector3.Dot(a - sp[best], sn[best]);
                if (depth >= Clearance) continue;
                av[i] += k.inverse.MultiplyVector(sn[best] * Mathf.Min(Clearance - depth, to.Tight ? 0.04f : MaxPush));
                moved++;
            }
            mesh.vertices = av;
            return moved;
        }

        static Vector3Int Key(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x / Cell), Mathf.FloorToInt(p.y / Cell), Mathf.FloorToInt(p.z / Cell));
    }
}
