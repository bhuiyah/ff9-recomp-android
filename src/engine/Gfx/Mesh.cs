// Meshes: asset decoding (Unity 5.x uncompressed vertex streams, 16-bit indices, bone weights),
// the runtime Mesh API (NGUI builds all its geometry through it), and GPU buffers.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FF9Engine.Assets;
using FF9Engine.Core;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace FF9Engine.Gfx;

public sealed class SubMeshData {
    public int[] Indices = Array.Empty<int>();
    public MeshTopology Topology = MeshTopology.Triangles;
}

public sealed class NMesh : NObject, ICloneable {
    public Vector3[] Vertices = Array.Empty<Vector3>();
    public Vector3[]? Normals;
    public Vector4[]? Tangents;
    public Vector2[]? UV, UV2, UV3, UV4;
    public Color32[]? Colors;
    public List<SubMeshData> SubMeshes = new() { new SubMeshData() };
    public BoneWeight[]? BoneWeights;
    public Matrix4x4[]? BindPoses;
    // computed when first read after the vertices or triangles change (SFX meshes are rebuilt and
    // drawn with DrawMeshNow many times a frame and never ask for them)
    Bounds bounds;
    public bool BoundsStale;
    public Bounds Bounds { get { if (BoundsStale) RecalculateBounds(); return bounds; } set { bounds = value; BoundsStale = false; } }
    public bool Dirty = true, BoundsSet;
    public uint Vbo, Vao;
    public uint[] Ibo = Array.Empty<uint>();
    public int Stride;
    public bool Dynamic;

    public override void FreeGpu() {
        GL.DeleteBuffer(Vbo);
        foreach (var b in Ibo) GL.DeleteBuffer(b);
        GL.DeleteVertexArray(Vao);
        Vbo = Vao = 0; Ibo = Array.Empty<uint>(); Dirty = true;
    }

    public override void ForgetGpu() { Vbo = Vao = 0; Ibo = Array.Empty<uint>(); Dirty = true; }

    public object Clone() {
        var m = (NMesh)MemberwiseClone();
        m.Vbo = 0; m.Vao = 0; m.Ibo = Array.Empty<uint>(); m.Dirty = true;
        m.Vertices = (Vector3[])Vertices.Clone();
        m.Normals = (Vector3[]?)Normals?.Clone(); m.Tangents = (Vector4[]?)Tangents?.Clone();
        m.UV = (Vector2[]?)UV?.Clone(); m.UV2 = (Vector2[]?)UV2?.Clone(); m.UV3 = (Vector2[]?)UV3?.Clone(); m.UV4 = (Vector2[]?)UV4?.Clone();
        m.Colors = (Color32[]?)Colors?.Clone();
        m.SubMeshes = SubMeshes.ConvertAll(s => new SubMeshData { Indices = (int[])s.Indices.Clone(), Topology = s.Topology });
        m.BoneWeights = (BoneWeight[]?)BoneWeights?.Clone(); m.BindPoses = (Matrix4x4[]?)BindPoses?.Clone();
        return m;
    }

    public void RecalculateBounds() {
        BoundsStale = false;
        if (Vertices.Length == 0) { bounds = new Bounds(); return; }
        Vector3 mn = Vertices[0], mx = Vertices[0];
        foreach (var v in Vertices) {
            mn = new Vector3(Math.Min(mn.x, v.x), Math.Min(mn.y, v.y), Math.Min(mn.z, v.z));
            mx = new Vector3(Math.Max(mx.x, v.x), Math.Max(mx.y, v.y), Math.Max(mx.z, v.z));
        }
        bounds = new Bounds(new Vector3((mn.x + mx.x) / 2, (mn.y + mx.y) / 2, (mn.z + mx.z) / 2), new Vector3(mx.x - mn.x, mx.y - mn.y, mx.z - mn.z));
    }

    // GPU layout (interleaved): pos3 f, normal3 f, color4 ub, uv0 2f, uv1 2f, tangent4 f  = 64 bytes
    public const int AttrPos = 0, AttrNormal = 1, AttrColor = 2, AttrUV0 = 3, AttrUV1 = 4, AttrTangent = 5, AttrUV2 = 6, AttrUV3 = 7;
    const int VStride = 64;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct GpuVertex { public Vector3 P, N; public Color32 C; public Vector2 T0, T1; public Vector4 Tan; public float Pad; }   // 64 bytes

    static readonly bool traceVao = Environment.GetEnvironmentVariable("FF9_TRACE_VAO") != null;
    static GpuVertex[] staging = new GpuVertex[1024];

    public unsafe void Upload(Vector3[]? skinnedPos = null, Vector3[]? skinnedNormals = null) {
        if (!Dirty && skinnedPos == null && Vbo != 0) return;
        if (Vao == 0 && traceVao) Log.Info($"new VAO f{Core.Clock.Frame}: {this} n={Vertices.Length} registered={Core.Registry.ById(Id) == this}");
        if (Vao == 0) Vao = GL.GenVertexArray();
        if (Vbo == 0) Vbo = GL.GenBuffer();
        GL.glBindVertexArray(Vao);
        int n = Vertices.Length;
        // one staging array for all uploads (GL copies it in glBufferData): a new one per upload was
        // ~800 KB per SFX mesh, many times a frame, and the large-object garbage forced full GCs
        if (staging.Length < n) staging = new GpuVertex[Math.Max(n, staging.Length * 2)];
        var buf = staging;
        var pos = skinnedPos ?? Vertices;
        var nor = skinnedNormals ?? Normals;
        var white = new Color32(255, 255, 255, 255);
        for (int i = 0; i < n; i++) {
            buf[i].P = pos[i];
            buf[i].N = nor != null && i < nor.Length ? nor[i] : default;
            buf[i].C = Colors != null && i < Colors.Length ? Colors[i] : white;
            buf[i].T0 = UV != null && i < UV.Length ? UV[i] : default;
            buf[i].T1 = UV2 != null && i < UV2.Length ? UV2[i] : default;
            buf[i].Tan = Tangents != null && i < Tangents.Length ? Tangents[i] : default;
        }
        Core.GpuStats.VboUploads++;
        GL.glBindBuffer(GL.ARRAY_BUFFER, Vbo);
        fixed (GpuVertex* p = buf) GL.glBufferData(GL.ARRAY_BUFFER, n * VStride, p, Dynamic || skinnedPos != null ? GL.DYNAMIC_DRAW : GL.STATIC_DRAW);
        GL.glEnableVertexAttribArray(AttrPos); GL.glVertexAttribPointer(AttrPos, 3, GL.FLOAT, 0, VStride, 0);
        GL.glEnableVertexAttribArray(AttrNormal); GL.glVertexAttribPointer(AttrNormal, 3, GL.FLOAT, 0, VStride, 12);
        GL.glEnableVertexAttribArray(AttrColor); GL.glVertexAttribPointer(AttrColor, 4, GL.UNSIGNED_BYTE, 1, VStride, 24);
        GL.glEnableVertexAttribArray(AttrUV0); GL.glVertexAttribPointer(AttrUV0, 2, GL.FLOAT, 0, VStride, 28);
        GL.glEnableVertexAttribArray(AttrUV1); GL.glVertexAttribPointer(AttrUV1, 2, GL.FLOAT, 0, VStride, 36);
        GL.glEnableVertexAttribArray(AttrTangent); GL.glVertexAttribPointer(AttrTangent, 4, GL.FLOAT, 0, VStride, 44);
        if (Dirty || Ibo.Length != SubMeshes.Count) {
            Core.GpuStats.IboUploads++;
            if (Ibo.Length != SubMeshes.Count) {
                var keep = new uint[SubMeshes.Count];
                for (int s = 0; s < Ibo.Length; s++) { if (s < keep.Length) keep[s] = Ibo[s]; else GL.DeleteBuffer(Ibo[s]); }
                Ibo = keep;
            }
            for (int s = 0; s < SubMeshes.Count; s++) {
                if (Ibo[s] == 0) Ibo[s] = GL.GenBuffer();
                GL.glBindBuffer(GL.ELEMENT_ARRAY_BUFFER, Ibo[s]);
                var idx = SubMeshes[s].Indices;
                fixed (int* p = idx) GL.glBufferData(GL.ELEMENT_ARRAY_BUFFER, idx.Length * 4, p, GL.STATIC_DRAW);
            }
        }
        Dirty = false;
    }

    public void Draw(int submesh) {
        if (submesh < 0 || submesh >= SubMeshes.Count) return;
        var sm = SubMeshes[submesh];
        if (sm.Indices.Length == 0) return;
        GL.glBindVertexArray(Vao);
        GL.glBindBuffer(GL.ELEMENT_ARRAY_BUFFER, Ibo[submesh]);
        uint mode = sm.Topology switch {
            MeshTopology.Lines => GL.LINES, MeshTopology.LineStrip => GL.LINE_STRIP, MeshTopology.Points => GL.POINTS,
            _ => GL.TRIANGLES,
        };
        if (sm.Topology == MeshTopology.Quads) { Log.Once("quads", "quad topology drawn as triangles"); }
        GL.glDrawElements(mode, sm.Indices.Length, GL.UNSIGNED_INT, IntPtr.Zero);
    }
}

public static class Meshes {
    public static void Register() => AssetDB.Readers[ClassIds.Mesh] = Read;

    public static NMesh? Of(Mesh? m) => Registry.Get(m) switch { NAsset { Data: NMesh x } => x, NMesh x => x, _ => null };
    static NMesh M(Mesh m) => Of(m) ?? throw new NullReferenceException("mesh is null");

    static void Read(NAsset a, BinReader r) {
        var m = new NMesh();
        a.Data = m;
        a.Name = m.Name = r.String();
        int nsub = r.I32();
        var subs = new List<(uint firstByte, uint count, int topo, uint firstVertex, uint vertexCount)>();
        for (int i = 0; i < nsub; i++) {
            subs.Add((r.U32(), r.U32(), r.I32(), r.U32(), r.U32()));
            r.Skip(24);
        }
        int nbv = r.I32(); r.Skip(nbv * 40L);
        int nshape = r.I32(); r.Skip(nshape * 12L);
        int nch = r.I32(); for (int i = 0; i < nch; i++) { r.String(); r.Skip(12); }
        int nfw = r.I32(); r.Skip(nfw * 4L);
        int nbind = r.I32();
        if (nbind > 0) {
            m.BindPoses = new Matrix4x4[nbind];
            for (int i = 0; i < nbind; i++) {
                // serialized e00 e01 e02 e03 e10 ... (by row); Matrix4x4's fields go by column
                var mm = new Matrix4x4();
                for (int row = 0; row < 4; row++)
                    for (int col = 0; col < 4; col++) mm[row, col] = r.F32();
                m.BindPoses[i] = mm;
            }
        }
        int nhash = r.I32(); r.Skip(nhash * 4L);
        r.U32();
        byte compression = r.U8(); r.U8(); r.U8(); r.U8(); r.Align4();
        int ibSize = r.I32();
        long ibAt = r.Pos;
        r.Skip(ibSize); r.Align4();
        int nskin = r.I32();
        if (nskin > 0) {
            m.BoneWeights = new BoneWeight[nskin];
            for (int i = 0; i < nskin; i++) {
                var bw = new BoneWeight();
                bw.weight0 = r.F32(); bw.weight1 = r.F32(); bw.weight2 = r.F32(); bw.weight3 = r.F32();
                bw.boneIndex0 = r.I32(); bw.boneIndex1 = r.I32(); bw.boneIndex2 = r.I32(); bw.boneIndex3 = r.I32();
                m.BoneWeights[i] = bw;
            }
        }
        r.U32();
        int vc = (int)r.U32();
        int nchan = r.I32();
        var chans = new (byte stream, byte offset, byte format, byte dim)[nchan];
        for (int i = 0; i < nchan; i++) chans[i] = (r.U8(), r.U8(), r.U8(), r.U8());
        int dataSize = r.I32();
        long dataAt = r.Pos;
        if (compression != 0) Log.Once("meshcomp", $"compressed mesh '{m.Name}' not decoded");
        DecodeVertices(r.BytesAt(dataAt, dataSize), 0, vc, chans, m);
        // indices (16-bit)
        var data = r.BytesAt(ibAt, ibSize);
        foreach (var s in subs) {
            var sm = new SubMeshData { Topology = (MeshTopology)s.topo, Indices = new int[s.count] };
            long at = s.firstByte;
            for (int i = 0; i < s.count; i++) sm.Indices[i] = BitConverter.ToUInt16(data, (int)(at + i * 2));
            m.SubMeshes.Add(sm);
        }
        m.SubMeshes.RemoveAt(0);
        if (m.SubMeshes.Count == 0) m.SubMeshes.Add(new SubMeshData());
        m.RecalculateBounds();
    }

    static float Half(ushort h) => (float)BitConverter.UInt16BitsToHalf(h);

    static void DecodeVertices(byte[] d, long at, int vc, (byte stream, byte offset, byte format, byte dim)[] ch, NMesh m) {
        // per-stream stride and offset (streams aligned to 16 bytes)
        int streams = 0;
        foreach (var c in ch) if (c.dim > 0) streams = Math.Max(streams, c.stream + 1);
        var stride = new int[streams];
        foreach (var c in ch) if (c.dim > 0) stride[c.stream] += c.dim * (c.format == 0 ? 4 : c.format == 1 ? 2 : 1);
        var start = new long[streams];
        long off = 0;
        for (int s = 0; s < streams; s++) { start[s] = off; off += (long)stride[s] * vc; off = (off + 15) & ~15L; }
        float[] Read(int ci, out int dim) {
            var c = ch[ci];
            dim = c.dim;
            var outp = new float[vc * dim];
            int sz = c.format == 0 ? 4 : c.format == 1 ? 2 : 1;
            for (int v = 0; v < vc; v++) {
                long p = at + start[c.stream] + (long)v * stride[c.stream] + c.offset;
                for (int k = 0; k < dim; k++) {
                    long q = p + k * sz;
                    outp[v * dim + k] = c.format switch {
                        0 => BitConverter.ToSingle(d, (int)q),
                        1 => Half(BitConverter.ToUInt16(d, (int)q)),
                        _ => d[q] / 255f,
                    };
                }
            }
            return outp;
        }
        for (int ci = 0; ci < ch.Length; ci++) {
            if (ch[ci].dim == 0) continue;
            var f = Read(ci, out int dim);
            switch (ci) {
                case 0: m.Vertices = new Vector3[vc]; for (int i = 0; i < vc; i++) m.Vertices[i] = new Vector3(f[i * dim], f[i * dim + 1], dim > 2 ? f[i * dim + 2] : 0); break;
                case 1: m.Normals = new Vector3[vc]; for (int i = 0; i < vc; i++) m.Normals[i] = new Vector3(f[i * dim], f[i * dim + 1], f[i * dim + 2]); break;
                case 2: m.Colors = new Color32[vc]; for (int i = 0; i < vc; i++) m.Colors[i] = new Color32((byte)(f[i * dim] * 255), (byte)(f[i * dim + 1] * 255), (byte)(f[i * dim + 2] * 255), (byte)(f[i * dim + 3] * 255)); break;
                case 3: m.UV = UVs(f, dim, vc); break;
                case 4: m.UV2 = UVs(f, dim, vc); break;
                case 5: m.UV3 = UVs(f, dim, vc); break;
                case 6: m.UV4 = UVs(f, dim, vc); break;
                case 7: m.Tangents = new Vector4[vc]; for (int i = 0; i < vc; i++) m.Tangents[i] = new Vector4(f[i * dim], f[i * dim + 1], f[i * dim + 2], dim > 3 ? f[i * dim + 3] : 1); break;
            }
        }
    }

    static Vector2[] UVs(float[] f, int dim, int vc) {
        var r = new Vector2[vc];
        for (int i = 0; i < vc; i++) r[i] = new Vector2(f[i * dim], f[i * dim + 1]);
        return r;
    }

    static void Touch(NMesh m) { m.Dirty = true; if (!m.BoundsSet) m.BoundsStale = true; }

    [Icall("UnityEngine.Mesh::Internal_Create")] static void Create(Mesh mono) => Registry.Add(new NMesh(), mono);

    [Icall("UnityEngine.Mesh::Clear")]
    static void Clear(Mesh self, bool keepLayout) {
        long pt = Core.Prof.T0();
        var m = M(self);
        m.Vertices = Array.Empty<Vector3>(); m.Normals = null; m.Tangents = null; m.UV = m.UV2 = m.UV3 = m.UV4 = null; m.Colors = null;
        m.SubMeshes = new() { new SubMeshData() };
        m.BoneWeights = null;
        m.Dirty = true;
        Core.Prof.Call(0, pt);
    }

    static T[] ToArr<T>(object o) => o switch { T[] a => a, List<T> l => l.ToArray(), _ => Array.Empty<T>() };

    [Icall("UnityEngine.Mesh::get_vertices")] static Vector3[] GetV(Mesh s) => (Vector3[])M(s).Vertices.Clone();
    [Icall("UnityEngine.Mesh::set_vertices")] static void SetV(Mesh s, Vector3[] v) { long pt = Core.Prof.T0(); var m = M(s); m.Vertices = v ?? Array.Empty<Vector3>(); Touch(m); Core.Prof.Call(1, pt); }
    [Icall("UnityEngine.Mesh::SetVerticesInternal")] static void SetVL(Mesh s, object v) { var m = M(s); m.Vertices = ToArr<Vector3>(v); Touch(m); }
    [Icall("UnityEngine.Mesh::get_normals")] static Vector3[] GetN(Mesh s) => (Vector3[]?)M(s).Normals?.Clone() ?? Array.Empty<Vector3>();
    [Icall("UnityEngine.Mesh::set_normals")] static void SetN(Mesh s, Vector3[] v) { var m = M(s); m.Normals = v; m.Dirty = true; }
    [Icall("UnityEngine.Mesh::SetNormalsInternal")] static void SetNL(Mesh s, object v) { var m = M(s); m.Normals = ToArr<Vector3>(v); m.Dirty = true; }
    [Icall("UnityEngine.Mesh::get_tangents")] static Vector4[] GetT(Mesh s) => (Vector4[]?)M(s).Tangents?.Clone() ?? Array.Empty<Vector4>();
    [Icall("UnityEngine.Mesh::set_tangents")] static void SetT(Mesh s, Vector4[] v) { var m = M(s); m.Tangents = v; m.Dirty = true; }
    [Icall("UnityEngine.Mesh::SetTangentsInternal")] static void SetTL(Mesh s, object v) { var m = M(s); m.Tangents = ToArr<Vector4>(v); m.Dirty = true; }
    [Icall("UnityEngine.Mesh::get_uv")] static Vector2[] GetUV(Mesh s) => (Vector2[]?)M(s).UV?.Clone() ?? Array.Empty<Vector2>();
    [Icall("UnityEngine.Mesh::set_uv")] static void SetUV(Mesh s, Vector2[] v) { long pt = Core.Prof.T0(); var m = M(s); m.UV = v; m.Dirty = true; Core.Prof.Call(3, pt); }
    [Icall("UnityEngine.Mesh::get_uv2")] static Vector2[] GetUV2(Mesh s) => (Vector2[]?)M(s).UV2?.Clone() ?? Array.Empty<Vector2>();
    [Icall("UnityEngine.Mesh::set_uv2")] static void SetUV2(Mesh s, Vector2[] v) { var m = M(s); m.UV2 = v; m.Dirty = true; }
    [Icall("UnityEngine.Mesh::get_uv3")] static Vector2[] GetUV3(Mesh s) => (Vector2[]?)M(s).UV3?.Clone() ?? Array.Empty<Vector2>();
    [Icall("UnityEngine.Mesh::set_uv3")] static void SetUV3(Mesh s, Vector2[] v) { var m = M(s); m.UV3 = v; m.Dirty = true; }
    [Icall("UnityEngine.Mesh::get_uv4")] static Vector2[] GetUV4(Mesh s) => (Vector2[]?)M(s).UV4?.Clone() ?? Array.Empty<Vector2>();
    [Icall("UnityEngine.Mesh::set_uv4")] static void SetUV4(Mesh s, Vector2[] v) { var m = M(s); m.UV4 = v; m.Dirty = true; }

    [Icall("UnityEngine.Mesh::SetUVInternal")]
    static void SetUVL(Mesh s, object uvs, int channel, int dim) {
        var m = M(s);
        Vector2[] a = uvs switch {
            List<Vector2> l => l.ToArray(),
            List<Vector3> l3 => l3.ConvertAll(v => new Vector2(v.x, v.y)).ToArray(),
            List<Vector4> l4 => l4.ConvertAll(v => new Vector2(v.x, v.y)).ToArray(),
            _ => Array.Empty<Vector2>(),
        };
        switch (channel) { case 0: m.UV = a; break; case 1: m.UV2 = a; break; case 2: m.UV3 = a; break; default: m.UV4 = a; break; }
        m.Dirty = true;
    }

    [Icall("UnityEngine.Mesh::INTERNAL_get_bounds")] static void GetBounds(Mesh s, ref Bounds b) => b = M(s).Bounds;
    [Icall("UnityEngine.Mesh::INTERNAL_set_bounds")] static void SetBounds(Mesh s, ref Bounds b) { var m = M(s); m.Bounds = b; m.BoundsSet = true; }

    static Color32 C32(Color c) => new((byte)Math.Clamp(c.r * 255 + 0.5f, 0, 255), (byte)Math.Clamp(c.g * 255 + 0.5f, 0, 255), (byte)Math.Clamp(c.b * 255 + 0.5f, 0, 255), (byte)Math.Clamp(c.a * 255 + 0.5f, 0, 255));

    [Icall("UnityEngine.Mesh::get_colors")] static Color[] GetC(Mesh s) => Array.ConvertAll(M(s).Colors ?? Array.Empty<Color32>(), c => new Color(c.r / 255f, c.g / 255f, c.b / 255f, c.a / 255f));
    [Icall("UnityEngine.Mesh::set_colors")] static void SetC(Mesh s, Color[] v) { var m = M(s); m.Colors = v == null ? null : Array.ConvertAll(v, C32); m.Dirty = true; }
    [Icall("UnityEngine.Mesh::SetColorsInternal")]
    static void SetCL(Mesh s, object v) {
        var m = M(s);
        m.Colors = v switch { List<Color> l => l.ConvertAll(C32).ToArray(), List<Color32> l32 => l32.ToArray(), _ => null };
        m.Dirty = true;
    }
    [Icall("UnityEngine.Mesh::get_colors32")] static Color32[] GetC32(Mesh s) => (Color32[]?)M(s).Colors?.Clone() ?? Array.Empty<Color32>();
    static readonly bool traceCol = Environment.GetEnvironmentVariable("FF9_TRACE_MESHCOL") != null;
    [Icall("UnityEngine.Mesh::set_colors32")]
    static void SetC32(Mesh s, Color32[] v) {
        long pt = Core.Prof.T0();
        var m = M(s);
        if (traceCol && v != null && v.Length == 4) Log.Info($"frame {Core.Clock.Frame} mesh#{m.Id} '{m.Name}' colors32 {v[0]}");
        m.Colors = v; m.Dirty = true;
        Core.Prof.Call(2, pt);
    }

    [Icall("UnityEngine.Mesh::RecalculateBounds")] static void RecalcBounds(Mesh s) => M(s).RecalculateBounds();

    [Icall("UnityEngine.Mesh::RecalculateNormals")]
    static void RecalcNormals(Mesh s) {
        var m = M(s);
        var n = new Vector3[m.Vertices.Length];
        foreach (var sm in m.SubMeshes) {
            if (sm.Topology != MeshTopology.Triangles) continue;
            for (int i = 0; i + 2 < sm.Indices.Length; i += 3) {
                int a = sm.Indices[i], b = sm.Indices[i + 1], c = sm.Indices[i + 2];
                if (a >= n.Length || b >= n.Length || c >= n.Length) continue;
                Vector3 pa = m.Vertices[a], pb = m.Vertices[b], pc = m.Vertices[c];
                var fn = Icalls.Maths.Cross(new Vector3(pb.x - pa.x, pb.y - pa.y, pb.z - pa.z), new Vector3(pc.x - pa.x, pc.y - pa.y, pc.z - pa.z));
                foreach (int k in new[] { a, b, c }) n[k] = new Vector3(n[k].x + fn.x, n[k].y + fn.y, n[k].z + fn.z);
            }
        }
        for (int i = 0; i < n.Length; i++) {
            float l = MathF.Sqrt(n[i].x * n[i].x + n[i].y * n[i].y + n[i].z * n[i].z);
            if (l > 1e-9f) n[i] = new Vector3(n[i].x / l, n[i].y / l, n[i].z / l);
        }
        m.Normals = n;
        m.Dirty = true;
    }

    [Icall("UnityEngine.Mesh::get_triangles")]
    static int[] GetTris(Mesh s) {
        var m = M(s);
        var l = new List<int>();
        foreach (var sm in m.SubMeshes) if (sm.Topology == MeshTopology.Triangles) l.AddRange(sm.Indices);
        return l.ToArray();
    }

    [Icall("UnityEngine.Mesh::set_triangles")]
    static void SetTris(Mesh s, int[] t) {
        long pt = Core.Prof.T0();
        var m = M(s);
        m.SubMeshes = new() { new SubMeshData { Indices = t ?? Array.Empty<int>() } };
        Touch(m);
        Core.Prof.Call(4, pt);
    }

    static SubMeshData Sub(NMesh m, int i) {
        while (m.SubMeshes.Count <= i) m.SubMeshes.Add(new SubMeshData());
        return m.SubMeshes[i];
    }

    [Icall("UnityEngine.Mesh::GetTriangles")] static int[] GetTrisSub(Mesh s, int sub) => (int[])Sub(M(s), sub).Indices.Clone();
    [Icall("UnityEngine.Mesh::SetTriangles")]
    static void SetTrisSub(Mesh s, int[] t, int sub) { var m = M(s); var sm = Sub(m, sub); sm.Indices = t ?? Array.Empty<int>(); sm.Topology = MeshTopology.Triangles; Touch(m); }
    [Icall("UnityEngine.Mesh::SetTrianglesInternal")]
    static void SetTrisList(Mesh s, object t, int sub) { var m = M(s); var sm = Sub(m, sub); sm.Indices = ToArr<int>(t); sm.Topology = MeshTopology.Triangles; Touch(m); }
    [Icall("UnityEngine.Mesh::GetIndices")] static int[] GetIndices(Mesh s, int sub) => (int[])Sub(M(s), sub).Indices.Clone();
    [Icall("UnityEngine.Mesh::SetIndices")]
    static void SetIndices(Mesh s, int[] idx, MeshTopology topo, int sub) { var m = M(s); var sm = Sub(m, sub); sm.Indices = idx ?? Array.Empty<int>(); sm.Topology = topo; Touch(m); }
    [Icall("UnityEngine.Mesh::GetTopology")] static MeshTopology GetTopology(Mesh s, int sub) => Sub(M(s), sub).Topology;
    [Icall("UnityEngine.Mesh::get_vertexCount")] static int VertexCount(Mesh s) => M(s).Vertices.Length;
    [Icall("UnityEngine.Mesh::get_subMeshCount")] static int SubCount(Mesh s) => M(s).SubMeshes.Count;
    [Icall("UnityEngine.Mesh::set_subMeshCount")]
    static void SetSubCount(Mesh s, int n) {
        var m = M(s);
        while (m.SubMeshes.Count < n) m.SubMeshes.Add(new SubMeshData());
        if (m.SubMeshes.Count > n) m.SubMeshes.RemoveRange(n, m.SubMeshes.Count - n);
        m.Dirty = true;
    }
    [Icall("UnityEngine.Mesh::get_boneWeights")] static BoneWeight[] GetBW(Mesh s) => (BoneWeight[]?)M(s).BoneWeights?.Clone() ?? Array.Empty<BoneWeight>();
    [Icall("UnityEngine.Mesh::set_boneWeights")] static void SetBW(Mesh s, BoneWeight[] v) => M(s).BoneWeights = v;
    [Icall("UnityEngine.Mesh::get_bindposes")] static Matrix4x4[] GetBP(Mesh s) => (Matrix4x4[]?)M(s).BindPoses?.Clone() ?? Array.Empty<Matrix4x4>();
    [Icall("UnityEngine.Mesh::set_bindposes")] static void SetBP(Mesh s, Matrix4x4[] v) => M(s).BindPoses = v;
    [Icall("UnityEngine.Mesh::MarkDynamic")] static void MarkDynamic(Mesh s) => M(s).Dynamic = true;
    [Icall("UnityEngine.Mesh::Optimize")] static void Optimize(Mesh s) { }
    [Icall("UnityEngine.Mesh::UploadMeshData")] static void UploadMeshData(Mesh s, bool noLonger) { }
    [Icall("UnityEngine.Mesh::get_isReadable")] static bool IsReadable(Mesh s) => true;
    [Icall("UnityEngine.Mesh::get_blendShapeCount")] static int BlendShapes(Mesh s) => 0;
}
