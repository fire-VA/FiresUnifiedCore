using System.Collections.Generic;
using UnityEngine;

namespace FiresCore.Visuals
{
    /// <summary>
    /// Cloth capes on swapped models (they were hidden as not rebindable). A cape is a
    /// SkinnedMeshRenderer + Cloth: the mesh skinned to the player's spine/shoulders, per-vertex coefficients pinning its top edge, and
    /// capsule/sphere colliders on the player's own bones that it drapes over. On a swapped model:
    ///   mesh       ArmorRebind.Bind (same vertex count, so the coefficients still map one to one)
    ///   cloth      a Cloth on the new renderer with every setting and the coefficients copied
    ///   colliders  each source collider re-made under the model's matching humanoid bone (same layer / trigger), its radius and offset
    ///              scaled by the two bodies' measured girth at that bone (BodyProfile.Outer), so the cape hangs on the model's own body
    /// Both bodies in the same pose when it runs (as ArmorRebind.Bind). Throws like Bind; the caller hides the cape then.
    /// </summary>
    public static class CapeRebind
    {
        public struct CapeResult
        {
            public ArmorRebind.RebindResult Mesh;
            public int Capsules, Spheres, CollidersUnmatched;
            public string Note;
            public int NameAnchored;
        }

        [System.ThreadStatic] static bool s_nameAnchor;

        // Common alternative names per humanoid bone (Valheim rigs and the usual Mixamo / Unity exports).
        static readonly Dictionary<HumanBodyBones, string[]> Aliases = new Dictionary<HumanBodyBones, string[]>
        {
            { HumanBodyBones.Hips, new[] { "Hips", "Pelvis", "mixamorig:Hips", "Bip01 Pelvis" } },
            { HumanBodyBones.Spine, new[] { "Spine", "Spine1", "mixamorig:Spine", "Bip01 Spine" } },
            { HumanBodyBones.Chest, new[] { "Spine1", "Spine2", "Chest", "mixamorig:Spine1", "Bip01 Spine1" } },
            { HumanBodyBones.UpperChest, new[] { "Spine2", "UpperChest", "mixamorig:Spine2", "Bip01 Spine2" } },
            { HumanBodyBones.Neck, new[] { "Neck", "mixamorig:Neck", "Bip01 Neck" } },
            { HumanBodyBones.Head, new[] { "Head", "mixamorig:Head", "Bip01 Head" } },
            { HumanBodyBones.LeftShoulder, new[] { "LeftShoulder", "Shoulder_L", "mixamorig:LeftShoulder", "Bip01 L Clavicle" } },
            { HumanBodyBones.RightShoulder, new[] { "RightShoulder", "Shoulder_R", "mixamorig:RightShoulder", "Bip01 R Clavicle" } },
            { HumanBodyBones.LeftUpperArm, new[] { "LeftArm", "UpperArm_L", "mixamorig:LeftArm", "Bip01 L UpperArm" } },
            { HumanBodyBones.RightUpperArm, new[] { "RightArm", "UpperArm_R", "mixamorig:RightArm", "Bip01 R UpperArm" } },
            { HumanBodyBones.LeftUpperLeg, new[] { "LeftUpLeg", "Thigh_L", "mixamorig:LeftUpLeg", "Bip01 L Thigh" } },
            { HumanBodyBones.RightUpperLeg, new[] { "RightUpLeg", "Thigh_R", "mixamorig:RightUpLeg", "Bip01 R Thigh" } },
        };

        // The model's bone for a humanoid id its Animator doesn't map, by the source bone's name, then the aliases.
        static Transform ByName(ArmorRebind.BodyProfile to, HumanBodyBones id, Transform srcBone)
        {
            Transform root = to.Animator != null ? to.Animator.transform : null;
            if (root == null) return null;
            Transform hit = srcBone != null ? ArmorRebind.FindDeepIgnoreCase(root, srcBone.name) : null;
            if (hit == null && Aliases.TryGetValue(id, out var names))
                foreach (var n in names) if ((hit = ArmorRebind.FindDeepIgnoreCase(root, n)) != null) break;
            return hit;
        }

        public static CapeResult Bind(SkinnedMeshRenderer item, ArmorRebind.BodyProfile from, ArmorRebind.BodyProfile to, Transform parent, bool tPose = false)
            => Bind(item, from, to, parent, tPose, false, false);

        public static CapeResult Bind(SkinnedMeshRenderer item, ArmorRebind.BodyProfile from, ArmorRebind.BodyProfile to, Transform parent, bool tPose, bool nameAnchorFallback)
            => Bind(item, from, to, parent, tPose, nameAnchorFallback, false);

        /// <summary><paramref name="nameAnchorFallback"/> true = a collider whose humanoid bone the
        /// model's Animator lacks is anchored to the model's transform with the source bone's name (case-insensitive), else to a known
        /// alias of that bone, instead of being dropped. Logged per collider; counted in CapeResult.NameAnchored.</summary>
        /// <summary>Add-only: <paramref name="nameAnchorUnmatched"/> passes to the mesh bind (ArmorRebind.Bind's 6-argument
        /// overload), so a cape's own bone (cape1) is anchored by name / carried root to root instead of pinned to the Hips.</summary>
        public static CapeResult Bind(SkinnedMeshRenderer item, ArmorRebind.BodyProfile from, ArmorRebind.BodyProfile to, Transform parent, bool tPose, bool nameAnchorFallback, bool nameAnchorUnmatched)
        {
            var res = new CapeResult();
            s_nameAnchor = nameAnchorFallback;
            Cloth src = item.GetComponent<Cloth>();
            res.Mesh = ArmorRebind.Bind(item, from, to, parent, tPose, nameAnchorUnmatched);
            SkinnedMeshRenderer dst = res.Mesh.Result;
            if (dst == null) { res.Note = "mesh not rebound"; return res; }
            if (src == null) { res.Note = "no Cloth on the source: plain skinned piece"; return res; }

            Cloth cloth = dst.gameObject.AddComponent<Cloth>();
            cloth.stretchingStiffness = src.stretchingStiffness;
            cloth.bendingStiffness = src.bendingStiffness;
            cloth.damping = src.damping;
            cloth.friction = src.friction;
            cloth.useGravity = src.useGravity;
            cloth.externalAcceleration = src.externalAcceleration;
            cloth.randomAcceleration = src.randomAcceleration;
            cloth.worldVelocityScale = src.worldVelocityScale;
            cloth.worldAccelerationScale = src.worldAccelerationScale;
            cloth.sleepThreshold = src.sleepThreshold;
            cloth.useTethers = src.useTethers;
            cloth.enableContinuousCollision = src.enableContinuousCollision;
            cloth.useVirtualParticles = src.useVirtualParticles;
            cloth.collisionMassScale = src.collisionMassScale;
            cloth.clothSolverFrequency = src.clothSolverFrequency;
            cloth.selfCollisionDistance = src.selfCollisionDistance;
            cloth.selfCollisionStiffness = src.selfCollisionStiffness;
            ClothSkinningCoefficient[] co = src.coefficients;
            if (co != null && co.Length == cloth.coefficients.Length) cloth.coefficients = co;
            else res.Note = $"coefficients not copied ({co?.Length ?? 0} vs {cloth.coefficients.Length}): the cloth uses Unity's defaults";

            // colliders: same humanoid bone on the model, scaled by girth there
            var made = new Dictionary<Collider, Collider>();
            var capsules = new List<CapsuleCollider>();
            foreach (CapsuleCollider c in src.capsuleColliders ?? new CapsuleCollider[0])
                if (Remake(c, from, to, made, ref res) is CapsuleCollider cc) { capsules.Add(cc); res.Capsules++; }
            cloth.capsuleColliders = capsules.ToArray();
            var pairs = new List<ClothSphereColliderPair>();
            foreach (ClothSphereColliderPair p in src.sphereColliders ?? new ClothSphereColliderPair[0])
            {
                var a = p.first != null ? Remake(p.first, from, to, made, ref res) as SphereCollider : null;
                var b = p.second != null ? Remake(p.second, from, to, made, ref res) as SphereCollider : null;
                if (a == null && b == null) continue;
                pairs.Add(new ClothSphereColliderPair(a, b));
                res.Spheres += (a != null ? 1 : 0) + (b != null ? 1 : 0);
            }
            cloth.sphereColliders = pairs.ToArray();
            return res;
        }

        // A copy of collider c (on the player's rig) under the model's matching humanoid bone, girth-scaled. Shared colliders are made once.
        static Collider Remake(Collider c, ArmorRebind.BodyProfile from, ArmorRebind.BodyProfile to, Dictionary<Collider, Collider> made, ref CapeResult res)
        {
            if (c == null) return null;
            if (made.TryGetValue(c, out var done)) return done;
            HumanBodyBones? id = null;
            for (var t = c.transform; t != null && id == null; t = t.parent) if (from.Ids.TryGetValue(t, out var hid)) id = hid;
            Transform bone = id.HasValue ? to.Animator.GetBoneTransform(id.Value) : null;
            // the collider's offset from its bone, carried into the model bone's space and scaled
            Transform srcBone = id.HasValue ? (from.Animator.GetBoneTransform(id.Value) ?? c.transform) : c.transform;
            if (bone == null && id.HasValue && s_nameAnchor)
            {
                bone = ByName(to, id.Value, srcBone);
                FiresCore.Logging.FiresLogger.LogDebug($"[CapeRebind] collider {c.name}: model has no humanoid {id.Value}; name anchor {(bone != null ? $"'{bone.name}'" : "none found")}");
                if (bone != null) res.NameAnchored++;
            }
            if (bone == null) { res.CollidersUnmatched++; made[c] = null; return null; }

            float scale = Girth(from, to, id.Value);
            var go = new GameObject(c.name + "_vcsCape");
            go.layer = c.gameObject.layer;
            go.transform.SetParent(bone, false);
            // Everything goes through WORLD units (a cape stood up like a board on Dvergr/Skeleton otherwise). The
            // model's bones carry other scales than the player's (rig import scale, plus the model root scaled to the player's height),
            // so raw local sizes made the colliders the wrong size and the cloth was shoved up.
            Vector3 worldOffset = (c.transform.position - srcBone.position) * scale;
            go.transform.position = bone.position + worldOffset;
            go.transform.rotation = bone.rotation * Quaternion.Inverse(srcBone.rotation) * c.transform.rotation;
            float srcS = MaxAbs(c.transform.lossyScale), dstS = MaxAbs(go.transform.lossyScale);
            float k = dstS > 1e-6f ? srcS * scale / dstS : scale;   // local size on the copy = world size * girth / its own world scale
            Collider copy;
            if (c is CapsuleCollider cap)
            {
                var n = go.AddComponent<CapsuleCollider>();
                n.center = go.transform.InverseTransformVector(c.transform.TransformVector(cap.center)) * scale;
                n.radius = cap.radius * k; n.height = cap.height * k; n.direction = cap.direction;
                copy = n;
            }
            else if (c is SphereCollider sph)
            {
                var n = go.AddComponent<SphereCollider>();
                n.center = go.transform.InverseTransformVector(c.transform.TransformVector(sph.center)) * scale;
                n.radius = sph.radius * k;
                copy = n;
            }
            else { Object.Destroy(go); res.CollidersUnmatched++; made[c] = null; return null; }
            copy.isTrigger = c.isTrigger;
            made[c] = copy;
            return copy;
        }

        static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Max(Mathf.Abs(v.y), Mathf.Abs(v.z)));

        // How much wider the model is than the player at this bone (front-back and side-side averaged), 1 when unmeasured.
        static float Girth(ArmorRebind.BodyProfile from, ArmorRebind.BodyProfile to, HumanBodyBones id)
        {
            // legs fit the typical skin (as ArmorRebind), the rest the outer: a cape collider on a belly bone must clear the belly
            var fd = ArmorRebind.IsLeg(id) ? from.Typical : from.Outer; var td = ArmorRebind.IsLeg(id) ? to.Typical : to.Outer;
            if (fd == null || td == null || !fd.TryGetValue(id, out var a) || !td.TryGetValue(id, out var b)) return 1f;
            float fa = a.F + a.B, sa = a.Rt + a.Lt;
            if (fa < 0.01f || sa < 0.01f) return 1f;
            return Mathf.Clamp(((b.F + b.B) / fa + (b.Rt + b.Lt) / sa) * 0.5f, 0.6f, 3f);
        }
    }
}
