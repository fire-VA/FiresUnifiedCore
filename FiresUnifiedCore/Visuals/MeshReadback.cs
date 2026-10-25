using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresCore.Visuals
{
    /// <summary>
    /// 0.8.0: a READABLE copy of any mesh. Vanilla's bundle meshes are not Read/Write in game (the rip marks them readable; the
    /// game does not), so the armour fit can't read vertices from them directly. Readable meshes are simply copied ("cpu"); the
    /// rest are read back from the GPU ("gpu"): every vertex stream, the index buffer and the skin-weight buffer, decoded by
    /// the mesh's own vertex layout and rebuilt as a new Mesh. Cached per source mesh; the caller logs which path ran.
    /// </summary>
    public static class MeshReadback
    {
        static readonly Dictionary<int, (Mesh mesh, string path)> s_cache = new Dictionary<int, (Mesh, string)>();

        /// <summary>A readable copy (cached, shared: do not modify it - Instantiate it first). path = "cpu", "gpu" or "failed: why".</summary>
        public static Mesh Readable(Mesh src, out string path)
        {
            path = "failed: no mesh";
            if (src == null) return null;
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) { path = "failed: no GPU (headless)"; return null; }
            int id = src.GetInstanceID();
            if (s_cache.TryGetValue(id, out var hit) && hit.mesh != null) { path = hit.path; return hit.mesh; }
            Mesh copy = null;
            if (src.isReadable) { copy = UnityEngine.Object.Instantiate(src); path = "cpu"; }
            else
            {
                try { copy = FromGpu(src); path = copy != null ? "gpu" : "failed: gpu readback returned nothing"; }
                catch (Exception e) { path = "failed: " + e.Message; copy = null; }
            }
            if (copy != null) copy.name = src.name + "_vcsread";
            s_cache[id] = (copy, path);
            return copy;
        }

        static Mesh FromGpu(Mesh src)
        {
            int vc = src.vertexCount;
            if (vc == 0) return null;
            try { src.vertexBufferTarget |= GraphicsBuffer.Target.Raw; src.indexBufferTarget |= GraphicsBuffer.Target.Raw; } catch { }
            var attrs = src.GetVertexAttributes();
            var streams = new Dictionary<int, byte[]>();
            foreach (var a in attrs)
            {
                if (streams.ContainsKey(a.stream)) continue;
                using (var buf = src.GetVertexBuffer(a.stream))
                {
                    if (buf == null) return null;
                    var bytes = new byte[buf.count * buf.stride];
                    buf.GetData(bytes);
                    streams[a.stream] = bytes;
                }
            }

            var dst = new Mesh { indexFormat = src.indexFormat };
            var pos = new Vector3[vc]; var nrm = new Vector3[vc]; var tan = new Vector4[vc]; var col = new Color[vc];
            var uv = new List<Vector4[]>();
            bool hasN = false, hasT = false, hasC = false;
            foreach (var a in attrs)
            {
                if (a.attribute == VertexAttribute.BlendWeight || a.attribute == VertexAttribute.BlendIndices) continue;   // skinning: own buffer below
                var data = streams[a.stream]; int stride = src.GetVertexBufferStride(a.stream); int off = OffsetOf(src, attrs, a);
                for (int v = 0; v < vc; v++)
                {
                    var x = Read(data, v * stride + off, a.format, a.dimension);
                    switch (a.attribute)
                    {
                        case VertexAttribute.Position: pos[v] = x; break;
                        case VertexAttribute.Normal: nrm[v] = x; hasN = true; break;
                        case VertexAttribute.Tangent: tan[v] = x; hasT = true; break;
                        case VertexAttribute.Color: col[v] = x; hasC = true; break;
                        default:
                            int ch = a.attribute - VertexAttribute.TexCoord0;
                            if (ch < 0 || ch > 7) break;
                            while (uv.Count <= ch) uv.Add(null);
                            if (uv[ch] == null) uv[ch] = new Vector4[vc];
                            uv[ch][v] = x;
                            break;
                    }
                }
            }
            dst.vertices = pos;
            if (hasN) dst.normals = nrm;
            if (hasT) dst.tangents = tan;
            if (hasC) dst.colors = col;
            for (int ch = 0; ch < uv.Count; ch++) if (uv[ch] != null) dst.SetUVs(ch, uv[ch]);

            // indices, per submesh
            using (var ib = src.GetIndexBuffer())
            {
                bool i16 = src.indexFormat == IndexFormat.UInt16;
                var raw = new byte[ib.count * ib.stride];
                ib.GetData(raw);
                dst.subMeshCount = src.subMeshCount;
                for (int s = 0; s < src.subMeshCount; s++)
                {
                    var d = src.GetSubMesh(s);
                    var idx = new int[d.indexCount];
                    for (int i = 0; i < d.indexCount; i++)
                    {
                        int at = (d.indexStart + i) * (i16 ? 2 : 4);
                        idx[i] = (i16 ? BitConverter.ToUInt16(raw, at) : (int)BitConverter.ToUInt32(raw, at)) + d.baseVertex;
                    }
                    dst.SetIndices(idx, d.topology, s, false);
                }
            }

            // skinning: bind poses are mesh metadata; weights come from the skin buffer (float weights, then uint indices)
            dst.bindposes = src.bindposes;
            int per = src.skinWeightBufferLayout == SkinWeights.OneBone ? 1 : src.skinWeightBufferLayout == SkinWeights.TwoBones ? 2 : src.skinWeightBufferLayout == SkinWeights.FourBones ? 4 : 0;
            if (per > 0)
            {
                using (var wb = src.GetBoneWeightBuffer(src.skinWeightBufferLayout))
                {
                    var raw = new byte[wb.count * wb.stride];
                    wb.GetData(raw);
                    int stride = raw.Length / vc;
                    var counts = new NativeArray<byte>(vc, Allocator.Temp);
                    var weights = new NativeArray<BoneWeight1>(vc * per, Allocator.Temp);
                    int bones = src.bindposes.Length;
                    for (int v = 0; v < vc; v++)
                    {
                        int b = v * stride;
                        var list = new List<BoneWeight1>(per);
                        for (int k = 0; k < per; k++)
                        {
                            float w = per == 1 ? 1f : BitConverter.ToSingle(raw, b + k * 4);
                            int bi = (int)BitConverter.ToUInt32(raw, b + (per == 1 ? 0 : per * 4) + k * 4);
                            if (w > 0f && bi >= 0 && bi < bones) list.Add(new BoneWeight1 { boneIndex = bi, weight = w });
                        }
                        list.Sort((p, q) => q.weight.CompareTo(p.weight));
                        counts[v] = (byte)list.Count;
                        for (int k = 0; k < per; k++) weights[v * per + k] = k < list.Count ? list[k] : default;
                    }
                    // SetBoneWeights wants exactly sum(counts) entries, packed
                    int total = 0; for (int v = 0; v < vc; v++) total += counts[v];
                    var packed = new NativeArray<BoneWeight1>(total, Allocator.Temp);
                    int at = 0; for (int v = 0; v < vc; v++) for (int k = 0; k < counts[v]; k++) packed[at++] = weights[v * per + k];
                    dst.SetBoneWeights(counts, packed);
                    counts.Dispose(); weights.Dispose(); packed.Dispose();
                }
            }
            dst.bounds = src.bounds;
            return dst;
        }

        static int OffsetOf(Mesh m, VertexAttributeDescriptor[] attrs, VertexAttributeDescriptor a)
        {
            int off = m.GetVertexAttributeOffset(a.attribute);
            return off >= 0 ? off : 0;
        }

        static Vector4 Read(byte[] d, int at, VertexAttributeFormat f, int dim)
        {
            var r = new Vector4(0, 0, 0, 1);
            for (int i = 0; i < dim && i < 4; i++)
            {
                float v;
                switch (f)
                {
                    case VertexAttributeFormat.Float32: v = BitConverter.ToSingle(d, at + i * 4); break;
                    case VertexAttributeFormat.Float16: v = Mathf.HalfToFloat(BitConverter.ToUInt16(d, at + i * 2)); break;
                    case VertexAttributeFormat.UNorm8: v = d[at + i] / 255f; break;
                    case VertexAttributeFormat.SNorm8: v = Mathf.Max((sbyte)d[at + i] / 127f, -1f); break;
                    case VertexAttributeFormat.UNorm16: v = BitConverter.ToUInt16(d, at + i * 2) / 65535f; break;
                    case VertexAttributeFormat.SNorm16: v = Mathf.Max(BitConverter.ToInt16(d, at + i * 2) / 32767f, -1f); break;
                    case VertexAttributeFormat.UInt8: v = d[at + i]; break;
                    case VertexAttributeFormat.UInt16: v = BitConverter.ToUInt16(d, at + i * 2); break;
                    case VertexAttributeFormat.UInt32: v = BitConverter.ToUInt32(d, at + i * 4); break;
                    default: v = 0f; break;
                }
                r[i] = v;
            }
            return r;
        }
    }
}
