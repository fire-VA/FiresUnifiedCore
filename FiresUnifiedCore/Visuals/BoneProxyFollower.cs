using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Visuals
{
    /// <summary>
    /// Fixes capes standing up like a board on swapped models. A rebound piece can carry
    /// bones of its OWN that are not humanoid (a cape's "cape1"). Something moves them at runtime on the source body, but ArmorRebind
    /// pins them to the target's hips (Unmatched), so the copy never gets that motion and stays in its modelled rest pose. This follower
    /// gives each such bone a proxy under the target's hips that replays, every frame, how the SOURCE bone has moved relative to the
    /// SOURCE hips since the bind. At bind time the proxy sits exactly on the hips, so ArmorRebind's bind poses stay valid.
    /// Source = the renderer whose bones really move (VCS: the hidden player's vanilla piece; FMO: the monster's own vanilla attach,
    /// not a T-posed reference body, whose cape bone never moves). Client-only: a no-op (0) on a headless dedi.
    /// </summary>
    public sealed class BoneProxyFollower : MonoBehaviour
    {
        private sealed class Proxy { public Transform Src, Dst; public Matrix4x4 InitRel; }
        private readonly List<Proxy> _proxies = new List<Proxy>();
        private Transform _srcHips;

        /// <summary>For every bone of <paramref name="rebound"/> that ArmorRebind pinned to <paramref name="targetHips"/> and whose source
        /// bone (same index in <paramref name="sourceBones"/>) is not under <paramref name="sourceHumanoid"/>'s humanoid bones, puts a proxy
        /// in its place and keeps it following. Returns the number of proxies made (0: nothing to follow, or headless).</summary>
        public static int Attach(SkinnedMeshRenderer rebound, Transform[] sourceBones, Animator sourceHumanoid, Transform targetHips)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) return 0;
            if (rebound == null || sourceBones == null || sourceHumanoid == null || targetHips == null) return 0;
            var srcHips = sourceHumanoid.GetBoneTransform(HumanBodyBones.Hips);
            if (srcHips == null) return 0;
            var humanoid = new HashSet<Transform>();
            foreach (HumanBodyBones h in Enum.GetValues(typeof(HumanBodyBones)))
                if (h != HumanBodyBones.LastBone) { var t = sourceHumanoid.GetBoneTransform(h); if (t != null) humanoid.Add(t); }

            var dstBones = rebound.bones;
            BoneProxyFollower f = null;
            int made = 0;
            for (int i = 0; i < sourceBones.Length && i < dstBones.Length; i++)
            {
                var b = sourceBones[i];
                if (b == null || dstBones[i] != targetHips || UnderHumanoid(b, humanoid, sourceHumanoid.transform)) continue;
                if (f == null) { f = rebound.gameObject.AddComponent<BoneProxyFollower>(); f._srcHips = srcHips; }
                var go = new GameObject("FiresProxy_" + b.name);
                go.transform.SetParent(targetHips, false);   // identity: exactly the hips now, so the bind pose holds
                f._proxies.Add(new Proxy { Src = b, Dst = go.transform, InitRel = srcHips.worldToLocalMatrix * b.localToWorldMatrix });
                dstBones[i] = go.transform;
                made++;
            }
            if (made > 0) rebound.bones = dstBones;
            return made;
        }

        private static bool UnderHumanoid(Transform b, HashSet<Transform> humanoid, Transform root)
        {
            for (var t = b; t != null && t != root; t = t.parent) if (humanoid.Contains(t)) return true;
            return false;
        }

        private void LateUpdate()
        {
            if (_srcHips == null) return;
            for (int i = _proxies.Count - 1; i >= 0; i--)
            {
                var p = _proxies[i];
                if (p.Src == null || p.Dst == null) { if (p.Dst != null) Destroy(p.Dst.gameObject); _proxies.RemoveAt(i); continue; }
                var delta = (_srcHips.worldToLocalMatrix * p.Src.localToWorldMatrix) * p.InitRel.inverse;
                var parentScale = p.Dst.parent != null ? p.Dst.parent.lossyScale.x : 1f;
                float k = Mathf.Abs(parentScale) > 1e-6f ? _srcHips.lossyScale.x / parentScale : 1f;   // source-hips units -> target-hips units
                p.Dst.localPosition = (Vector3)delta.GetColumn(3) * k;
                p.Dst.localRotation = delta.rotation;
            }
        }

        private void OnDestroy()
        {
            foreach (var p in _proxies) if (p.Dst != null) Destroy(p.Dst.gameObject);
            _proxies.Clear();
        }
    }
}
