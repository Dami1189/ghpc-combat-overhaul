using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace GHPCHitCam
{
    /// A CPU copy of one of the game's meshes that keeps every vertex attribute byte for byte (UVs, colours, tangents,
    /// skin weights), so a part can be cut out of the real model and drawn with the real materials. Meshes the game keeps
    /// on the GPU only (not readable) are read back from their vertex and index buffers.
    ///
    /// Positions decide what goes where: `W` holds each vertex where it is drawn right now (skinned meshes posed by
    /// their bones), and every edit (slicing, bending) works on those, then writes back into the mesh's own space.
    internal sealed class MeshCopy
    {
        public string Name;
        public int Count;                           // vertices in use
        int _cap;
        public VertexAttributeDescriptor[] Attrs;
        public int[] Off;                           // byte offset of each attribute inside its stream
        public int[] Stride;                        // bytes per vertex, per stream
        public byte[][] Data;                       // per stream
        public List<int>[] Tris;                    // per submesh, triangle lists of absolute vertex indices
        public Matrix4x4[] Bindposes;
        public int PosA = -1, NrmA = -1, TanA = -1, BwA = -1, BiA = -1;
        public List<Vector3> W;                     // drawn (world) position of every vertex, after Pose()
        public Matrix4x4[] Skin;                    // per bone: mesh space -> world, after Pose()
        public Matrix4x4 L2W = Matrix4x4.identity;  // non-skinned: mesh space -> world

        public bool Skinned => BiA >= 0 && Bindposes != null && Bindposes.Length > 0;

        // ------------------------------------------------------------------ reading

        // what was read back, per mesh: a GPU-only mesh comes back through a stalling GetData (a track, a barrel: up to
        // ~150 ms on the frame the part broke); read once (or ahead, Warmup), copied out after
        static readonly Dictionary<Mesh, MeshCopy> _read = new Dictionary<Mesh, MeshCopy>();

        static readonly HashSet<Mesh> _asking = new HashSet<Mesh>();

        public static bool Has(Mesh m) => m != null && _read.ContainsKey(m);
        public static bool Asking(Mesh m) => m != null && _asking.Contains(m);

        /// Read ahead without a stall: a readable mesh at once, a GPU-only one by an asynchronous read-back (it arrives a
        /// frame or two later and is kept for when the part breaks).
        public static void Prewarm(Mesh m)
        {
            if (m == null || _read.ContainsKey(m) || _asking.Contains(m)) return;
            if (m.isReadable) { Read(m, out _); return; }
            try
            {
                int streams = 0;
                foreach (var a in m.GetVertexAttributes()) streams = Math.Max(streams, a.stream + 1);
                var data = new byte[streams][];
                int[] idx = null;
                int left = streams + 1;
                bool failed = false;
                _asking.Add(m);
                void Done()
                {
                    if (--left > 0) return;
                    _asking.Remove(m);
                    if (failed || m == null) return;
                    var c = ReadNow(m, out _, data, idx);
                    if (c != null) { if (_read.Count > 96) _read.Clear(); _read[m] = c; }
                }
                for (int st = 0; st < streams; st++)
                {
                    int s = st;
                    var gb = m.GetVertexBuffer(s);
                    if (gb == null) { failed = true; Done(); continue; }
                    AsyncGPUReadback.Request(gb, req =>
                    {
                        if (req.hasError) failed = true; else data[s] = req.GetData<byte>().ToArray();
                        gb.Dispose();
                        Done();
                    });
                }
                var ib = m.GetIndexBuffer();
                if (ib == null) { failed = true; Done(); return; }
                bool wide = ib.stride == 4;
                AsyncGPUReadback.Request(ib, req =>
                {
                    if (req.hasError) failed = true;
                    else if (wide) { var ix = req.GetData<uint>(); idx = new int[ix.Length]; for (int i = 0; i < idx.Length; i++) idx[i] = (int)ix[i]; }
                    else { var ix = req.GetData<ushort>(); idx = new int[ix.Length]; for (int i = 0; i < idx.Length; i++) idx[i] = ix[i]; }
                    ib.Dispose();
                    Done();
                });
            }
            catch (Exception e) { _asking.Remove(m); HitCamMod.Log.Msg("Mesh read-ahead " + m.name + ": " + e.Message); }
        }

        public static void ClearCache() { _read.Clear(); _asking.Clear(); }

        public static MeshCopy Read(Mesh m, out string why)
        {
            why = null;
            if (m == null) { why = "no mesh"; return null; }
            if (_read.TryGetValue(m, out var have)) return have?.Clone();
            var c = ReadNow(m, out why);
            if (c != null)
            {
                if (_read.Count > 96) _read.Clear();
                _read[m] = c.Clone();
            }
            return c;
        }

        /// A copy that can be posed and edited without touching this one.
        MeshCopy Clone()
        {
            var c = (MeshCopy)MemberwiseClone();
            c.Data = new byte[Data.Length][];
            for (int s = 0; s < Data.Length; s++) c.Data[s] = (byte[])Data[s].Clone();
            c.Tris = new List<int>[Tris.Length];
            for (int i = 0; i < Tris.Length; i++) c.Tris[i] = new List<int>(Tris[i]);
            c.W = null;
            c.Skin = null;
            return c;
        }

        static MeshCopy ReadNow(Mesh m, out string why, byte[][] gpuData = null, int[] gpuIdx = null)
        {
            why = null;
            try
            {
                var c = new MeshCopy { Name = m.name, Count = m.vertexCount, Attrs = m.GetVertexAttributes() };
                c._cap = c.Count;
                int streams = 0;
                foreach (var a in c.Attrs) streams = Math.Max(streams, a.stream + 1);
                c.Stride = new int[streams];
                c.Data = new byte[streams][];
                c.Off = new int[c.Attrs.Length];
                var cur = new int[streams];
                for (int i = 0; i < c.Attrs.Length; i++)
                {
                    var a = c.Attrs[i];
                    c.Off[i] = cur[a.stream];
                    cur[a.stream] += Size(a.format) * a.dimension;
                    switch (a.attribute)
                    {
                        case VertexAttribute.Position: c.PosA = i; break;
                        case VertexAttribute.Normal: c.NrmA = i; break;
                        case VertexAttribute.Tangent: c.TanA = i; break;
                        case VertexAttribute.BlendWeight: c.BwA = i; break;
                        case VertexAttribute.BlendIndices: c.BiA = i; break;
                    }
                }
                if (c.PosA < 0) { why = "no positions"; return null; }
                for (int s = 0; s < streams; s++)
                {
                    c.Stride[s] = m.GetVertexBufferStride(s);
                    if (cur[s] > c.Stride[s]) { why = $"stream {s}: attributes {cur[s]} B > stride {c.Stride[s]} B"; return null; }
                }
                var subs = new SubMeshDescriptor[m.subMeshCount];
                for (int i = 0; i < subs.Length; i++) subs[i] = m.GetSubMesh(i);
                int[] idx;
                if (gpuData != null && gpuIdx != null && gpuData.Length == streams)
                {
                    for (int s = 0; s < streams; s++) c.Data[s] = gpuData[s];
                    idx = gpuIdx;
                }
                else if (m.isReadable)
                {
                    using (var arr = Mesh.AcquireReadOnlyMeshData(m))
                    {
                        var md = arr[0];
                        for (int s = 0; s < streams; s++) c.Data[s] = md.GetVertexData<byte>(s).ToArray();
                        if (md.indexFormat == IndexFormat.UInt16)
                        {
                            var ix = md.GetIndexData<ushort>();
                            idx = new int[ix.Length];
                            for (int i = 0; i < idx.Length; i++) idx[i] = ix[i];
                        }
                        else
                        {
                            var ix = md.GetIndexData<uint>();
                            idx = new int[ix.Length];
                            for (int i = 0; i < idx.Length; i++) idx[i] = (int)ix[i];
                        }
                    }
                }
                else
                {
                    for (int s = 0; s < streams; s++)
                        using (var gb = m.GetVertexBuffer(s))
                        {
                            if (gb == null) { why = "no GPU vertex buffer " + s; return null; }
                            var d = new byte[gb.count * gb.stride];
                            gb.GetData(d);
                            c.Data[s] = d;
                        }
                    using (var ib = m.GetIndexBuffer())
                    {
                        if (ib == null) { why = "no GPU index buffer"; return null; }
                        idx = new int[ib.count];
                        if (ib.stride == 2)
                        {
                            var ix = new ushort[ib.count];
                            ib.GetData(ix);
                            for (int i = 0; i < idx.Length; i++) idx[i] = ix[i];
                        }
                        else
                        {
                            var ix = new uint[ib.count];
                            ib.GetData(ix);
                            for (int i = 0; i < idx.Length; i++) idx[i] = (int)ix[i];
                        }
                    }
                }
                for (int s = 0; s < streams; s++)
                    if (c.Data[s] == null || c.Data[s].Length < c.Count * c.Stride[s]) { why = $"stream {s} short: {c.Data[s]?.Length ?? 0} B"; return null; }
                c.Tris = new List<int>[subs.Length];
                for (int i = 0; i < subs.Length; i++)
                {
                    var sd = subs[i];
                    var l = new List<int>(sd.indexCount);
                    if (sd.topology == MeshTopology.Triangles)
                        for (int k = sd.indexStart; k < sd.indexStart + sd.indexCount && k < idx.Length; k++) l.Add(idx[k] + sd.baseVertex);
                    c.Tris[i] = l;
                }
                try { c.Bindposes = m.bindposes; } catch { c.Bindposes = null; }
                return c;
            }
            catch (Exception e) { why = e.GetType().Name + ": " + e.Message; return null; }
        }

        /// Where every vertex is drawn now: a skinned mesh posed by its bones, otherwise through the renderer's transform.
        public void Pose(Renderer r)
        {
            W = new List<Vector3>(Count + 64);
            var smr = r as SkinnedMeshRenderer;
            if (smr != null && Skinned && smr.bones != null && smr.bones.Length > 0)
            {
                var bones = smr.bones;
                Skin = new Matrix4x4[bones.Length];
                var fallback = smr.transform.localToWorldMatrix;
                for (int i = 0; i < bones.Length; i++)
                    Skin[i] = bones[i] != null && i < Bindposes.Length ? bones[i].localToWorldMatrix * Bindposes[i] : fallback;
                for (int v = 0; v < Count; v++) W.Add(SkinPoint(v, Pos(v)));
            }
            else
            {
                Skin = null;
                L2W = r.transform.localToWorldMatrix;
                for (int v = 0; v < Count; v++) W.Add(L2W.MultiplyPoint3x4(Pos(v)));
            }
        }

        /// The vertex's own blend of bone matrices (mesh space -> world).
        public Matrix4x4 VertexMatrix(int v)
        {
            if (Skin == null) return L2W;
            Vector4 bi = Get(v, BiA);
            Vector4 bw = BwA >= 0 ? Get(v, BwA) : new Vector4(1, 0, 0, 0);
            int n = Mathf.Min(Attrs[BiA].dimension, 4);
            int best = 0;
            for (int k = 1; k < n; k++) if (bw[k] > bw[best]) best = k;
            int b = Mathf.Clamp((int)bi[best], 0, Skin.Length - 1);
            return Skin[b];
        }

        Vector3 SkinPoint(int v, Vector3 p)
        {
            Vector4 bi = Get(v, BiA);
            Vector4 bw = BwA >= 0 ? Get(v, BwA) : new Vector4(1, 0, 0, 0);
            int n = Mathf.Min(Attrs[BiA].dimension, 4);
            if (BwA < 0) n = 1;
            Vector3 acc = Vector3.zero;
            float ws = 0f;
            for (int k = 0; k < n; k++)
            {
                float w = BwA >= 0 && k < Attrs[BwA].dimension ? bw[k] : (k == 0 ? 1f : 0f);
                if (w <= 0f) continue;
                int b = Mathf.Clamp((int)bi[k], 0, Skin.Length - 1);
                acc += w * Skin[b].MultiplyPoint3x4(p);
                ws += w;
            }
            return ws > 1e-6f ? acc / ws : Skin[0].MultiplyPoint3x4(p);
        }

        // ------------------------------------------------------------------ attributes

        public Vector3 Pos(int v) => Get(v, PosA);

        public Vector4 Get(int v, int a)
        {
            var at = Attrs[a];
            byte[] d = Data[at.stream];
            int o = v * Stride[at.stream] + Off[a];
            int sz = Size(at.format);
            Vector4 r = default;
            for (int k = 0; k < at.dimension && k < 4; k++) r[k] = ReadF(d, o + k * sz, at.format);
            return r;
        }

        public void Set(int v, int a, Vector4 x)
        {
            var at = Attrs[a];
            byte[] d = Data[at.stream];
            int o = v * Stride[at.stream] + Off[a];
            int sz = Size(at.format);
            for (int k = 0; k < at.dimension && k < 4; k++) WriteF(d, o + k * sz, at.format, x[k]);
        }

        void CopyAttr(int from, int to, int a)
        {
            var at = Attrs[a];
            byte[] d = Data[at.stream];
            int st = Stride[at.stream];
            Buffer.BlockCopy(d, from * st + Off[a], d, to * st + Off[a], Size(at.format) * at.dimension);
        }

        void Grow(int n)
        {
            if (n <= _cap) return;
            int cap = Math.Max(n, _cap * 2 + 16);
            for (int s = 0; s < Data.Length; s++)
            {
                var nd = new byte[cap * Stride[s]];
                Buffer.BlockCopy(Data[s], 0, nd, 0, Math.Min(Data[s].Length, Count * Stride[s]));
                Data[s] = nd;
            }
            _cap = cap;
        }

        /// A new vertex part way from a to b: every attribute interpolated, skin weights taken whole from the nearer one.
        public int AddLerp(int a, int b, float t)
        {
            int v = Count;
            Grow(v + 1);
            Count++;
            for (int i = 0; i < Attrs.Length; i++)
            {
                var at = Attrs[i];
                if (i == BwA || i == BiA || IsInt(at.format)) { CopyAttr(t < 0.5f ? a : b, v, i); continue; }
                Vector4 x = Vector4.Lerp(Get(a, i), Get(b, i), t);
                if (i == NrmA) { var n3 = ((Vector3)x).normalized; x = new Vector4(n3.x, n3.y, n3.z, x.w); }
                if (i == TanA) { var t3 = ((Vector3)x).normalized; x = new Vector4(t3.x, t3.y, t3.z, Get(t < 0.5f ? a : b, i).w); }
                Set(v, i, x);
            }
            W?.Add(Vector3.Lerp(W[a], W[b], t));
            return v;
        }

        /// Moves a vertex to a new drawn position (and turns its normal / tangent by `rot`), written back into mesh space
        /// through the vertex's own bone (rigid parts: barrels, booms).
        public void MoveTo(int v, Vector3 world, Quaternion rot)
        {
            Matrix4x4 m = VertexMatrix(v);
            Matrix4x4 inv = m.inverse;
            Vector4 p = Get(v, PosA);
            Vector3 lp = inv.MultiplyPoint3x4(world);
            Set(v, PosA, new Vector4(lp.x, lp.y, lp.z, p.w));
            if (NrmA >= 0)
            {
                Vector4 n = Get(v, NrmA);
                Vector3 nw = rot * m.MultiplyVector(n).normalized;
                Vector3 nl = inv.MultiplyVector(nw).normalized;
                Set(v, NrmA, new Vector4(nl.x, nl.y, nl.z, n.w));
            }
            if (TanA >= 0)
            {
                Vector4 t = Get(v, TanA);
                Vector3 tw = rot * m.MultiplyVector(t).normalized;
                Vector3 tl = inv.MultiplyVector(tw).normalized;
                Set(v, TanA, new Vector4(tl.x, tl.y, tl.z, t.w));
            }
            if (W != null) W[v] = world;
        }

        // ------------------------------------------------------------------ editing

        /// Splits every triangle that crosses the plane (n . x = k) so each piece lies on one side. New vertices get
        /// every attribute interpolated; an edge cut once is shared by both of its triangles (no cracks).
        public void Slice(Vector3 n, float k, Func<int, int, int, bool> triFilter = null)
        {
            var cache = new Dictionary<long, int>();
            int Cut(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (cache.TryGetValue(key, out int m)) return m;
                float da = Vector3.Dot(n, W[a]) - k, db = Vector3.Dot(n, W[b]) - k;
                float t = Mathf.Clamp01(da / (da - db));
                m = AddLerp(a, b, t);
                cache[key] = m;
                return m;
            }
            for (int s = 0; s < Tris.Length; s++)
            {
                var src = Tris[s];
                var dst = new List<int>(src.Count + 64);
                for (int i = 0; i + 2 < src.Count; i += 3)
                {
                    int a = src[i], b = src[i + 1], c = src[i + 2];
                    if (triFilter != null && !triFilter(a, b, c)) { dst.Add(a); dst.Add(b); dst.Add(c); continue; }
                    bool pa = Vector3.Dot(n, W[a]) - k >= 0f, pb = Vector3.Dot(n, W[b]) - k >= 0f, pc = Vector3.Dot(n, W[c]) - k >= 0f;
                    if (pa == pb && pb == pc) { dst.Add(a); dst.Add(b); dst.Add(c); continue; }
                    // rotate so `a` is the vertex alone on its side (keeps the winding)
                    if (pa == pb) { int t = a; a = c; c = b; b = t; }          // c alone -> (c, a, b)
                    else if (pa == pc) { int t = a; a = b; b = c; c = t; }     // b alone -> (b, c, a)
                    int ab = Cut(a, b), ac = Cut(a, c);
                    dst.Add(a); dst.Add(ab); dst.Add(ac);
                    dst.Add(ab); dst.Add(b); dst.Add(c);
                    dst.Add(ab); dst.Add(c); dst.Add(ac);
                }
                Tris[s] = dst;
            }
        }

        /// Splits triangles four ways (edge midpoints, shared between neighbours) while `needs` says so, so a hole can be
        /// cut with a ragged edge into a model made of long thin triangles.
        public void Subdivide(Func<Vector3, Vector3, Vector3, bool> needs, int maxDepth, int maxVerts = 400000)
        {
            var cache = new Dictionary<long, int>();
            int Mid(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (cache.TryGetValue(key, out int m)) return m;
                m = AddLerp(a, b, 0.5f);
                cache[key] = m;
                return m;
            }
            var stack = new Stack<(int a, int b, int c, int d)>();
            for (int s = 0; s < Tris.Length; s++)
            {
                var src = Tris[s];
                var dst = new List<int>(src.Count + 256);
                for (int i = 0; i + 2 < src.Count; i += 3)
                {
                    stack.Push((src[i], src[i + 1], src[i + 2], 0));
                    while (stack.Count > 0)
                    {
                        var (a, b, c, d) = stack.Pop();
                        if (d < maxDepth * 2 && Count < maxVerts && needs(W[a], W[b], W[c]))
                        {
                            // halve the longest edge (long thin triangles get shorter, not just thinner)
                            float lab = (W[a] - W[b]).sqrMagnitude, lbc = (W[b] - W[c]).sqrMagnitude, lca = (W[c] - W[a]).sqrMagnitude;
                            if (lbc >= lab && lbc >= lca) { int t = a; a = b; b = c; c = t; }
                            else if (lca >= lab && lca >= lbc) { int t = a; a = c; c = b; b = t; }
                            int m = Mid(a, b);
                            stack.Push((a, m, c, d + 1));
                            stack.Push((m, b, c, d + 1));
                            continue;
                        }
                        dst.Add(a); dst.Add(b); dst.Add(c);
                    }
                }
                Tris[s] = dst;
            }
            // a triangle next to a split one has a vertex in the middle of its edge (a T-junction: a hairline crack the sky
            // shows through): split it there too, until no edge has one
            for (int pass = 0; pass < 12; pass++)
            {
                bool any = false;
                for (int s = 0; s < Tris.Length; s++)
                {
                    var src = Tris[s];
                    var dst = new List<int>(src.Count + 64);
                    for (int i = 0; i + 2 < src.Count; i += 3)
                    {
                        int a = src[i], b = src[i + 1], c = src[i + 2];
                        int ab = Has(cache, a, b), bc = Has(cache, b, c), ca = Has(cache, c, a);
                        int n = (ab >= 0 ? 1 : 0) + (bc >= 0 ? 1 : 0) + (ca >= 0 ? 1 : 0);
                        if (n == 0) { dst.Add(a); dst.Add(b); dst.Add(c); continue; }
                        any = true;
                        if (n == 3) { dst.AddRange(new[] { a, ab, ca, ab, b, bc, ca, bc, c, ab, bc, ca }); continue; }
                        // rotate so the first split edge is a-b
                        if (ab < 0) { if (bc >= 0) { int t = a; a = b; b = c; c = t; } else { int t = a; a = c; c = b; b = t; } }
                        ab = Has(cache, a, b); bc = Has(cache, b, c); ca = Has(cache, c, a);
                        if (n == 1) { dst.AddRange(new[] { a, ab, c, ab, b, c }); continue; }
                        if (bc >= 0) dst.AddRange(new[] { a, ab, c, ab, b, bc, ab, bc, c });
                        else dst.AddRange(new[] { a, ab, ca, ab, b, c, ab, c, ca });
                    }
                    Tris[s] = dst;
                }
                if (!any) break;
            }
        }

        static int Has(Dictionary<long, int> cache, int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            return cache.TryGetValue(key, out int m) ? m : -1;
        }

        /// Drops the triangles `drop` picks.
        public int RemoveTris(Func<int, int, int, bool> drop)
        {
            int n = 0;
            for (int s = 0; s < Tris.Length; s++)
            {
                var src = Tris[s];
                var dst = new List<int>(src.Count);
                for (int i = 0; i + 2 < src.Count; i += 3)
                {
                    if (drop(src[i], src[i + 1], src[i + 2])) { n++; continue; }
                    dst.Add(src[i]); dst.Add(src[i + 1]); dst.Add(src[i + 2]);
                }
                Tris[s] = dst;
            }
            return n;
        }

        /// Vertices referenced by the given triangles.
        public static HashSet<int> Used(List<int>[] tris)
        {
            var h = new HashSet<int>();
            foreach (var l in tris) foreach (int v in l) h.Add(v);
            return h;
        }

        /// The same mesh, with only the given triangles (the rest of the vertex data is kept as is; skinning too).
        public Mesh Build(string name, List<int>[] tris, bool keepSkin = true)
        {
            var m = new Mesh { name = name };
            m.indexFormat = Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            m.SetVertexBufferParams(Count, Attrs);
            var fl = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
            for (int s = 0; s < Data.Length; s++) m.SetVertexBufferData(Data[s], 0, 0, Count * Stride[s], s, fl);
            int total = 0;
            foreach (var l in tris) total += l.Count;
            m.SetIndexBufferParams(total, m.indexFormat);
            if (m.indexFormat == IndexFormat.UInt16)
            {
                var ix = new ushort[total];
                int j = 0;
                foreach (var l in tris) foreach (int v in l) ix[j++] = (ushort)v;
                m.SetIndexBufferData(ix, 0, 0, total, fl);
            }
            else
            {
                var ix = new int[total];
                int j = 0;
                foreach (var l in tris) foreach (int v in l) ix[j++] = v;
                m.SetIndexBufferData(ix, 0, 0, total, fl);
            }
            m.subMeshCount = tris.Length;
            int start = 0;
            for (int i = 0; i < tris.Length; i++)
            {
                m.SetSubMesh(i, new SubMeshDescriptor(start, tris[i].Count, MeshTopology.Triangles), fl);
                start += tris[i].Count;
            }
            if (keepSkin && Bindposes != null && Bindposes.Length > 0) m.bindposes = Bindposes;
            m.RecalculateBounds();
            return m;
        }

        /// A static mesh of the given triangles as drawn now, in the space `worldToLocal` maps into: vertices compacted,
        /// skin weights dropped, positions / normals / tangents posed. For a part that leaves the vehicle.
        public Mesh BuildStatic(string name, List<int>[] tris, Matrix4x4 worldToLocal)
        {
            var c = StaticCopy(name, tris, worldToLocal, out var nt, out _);
            return c.Build(name, nt, keepSkin: false);
        }

        /// The copy BuildStatic draws (for parts whose vertices keep moving, e.g. a thrown track), with its triangles and,
        /// per new vertex, the vertex it came from.
        public MeshCopy StaticCopy(string name, List<int>[] tris, Matrix4x4 worldToLocal, out List<int>[] newTris, out List<int> source)
        {
            var used = new List<int>();
            var map = new Dictionary<int, int>();
            foreach (var l in tris)
                foreach (int v in l)
                    if (!map.ContainsKey(v)) { map[v] = used.Count; used.Add(v); }
            // the layout without skin weights, streams renumbered
            var keep = new List<int>();
            for (int i = 0; i < Attrs.Length; i++) if (i != BwA && i != BiA) keep.Add(i);
            var streamMap = new Dictionary<int, int>();
            foreach (int i in keep) if (!streamMap.ContainsKey(Attrs[i].stream)) streamMap[Attrs[i].stream] = streamMap.Count;
            var attrs = new VertexAttributeDescriptor[keep.Count];
            var off = new int[keep.Count];
            var stride = new int[streamMap.Count];
            for (int j = 0; j < keep.Count; j++)
            {
                var a = Attrs[keep[j]];
                int s = streamMap[a.stream];
                attrs[j] = new VertexAttributeDescriptor(a.attribute, a.format, a.dimension, s);
                off[j] = stride[s];
                stride[s] += Size(a.format) * a.dimension;
            }
            var data = new byte[stride.Length][];
            for (int s = 0; s < stride.Length; s++) data[s] = new byte[used.Count * stride[s]];
            var outc = new MeshCopy { Name = name, Count = used.Count, _cap = used.Count, Attrs = attrs, Off = off, Stride = stride, Data = data };
            for (int j = 0; j < attrs.Length; j++)
            {
                switch (attrs[j].attribute)
                {
                    case VertexAttribute.Position: outc.PosA = j; break;
                    case VertexAttribute.Normal: outc.NrmA = j; break;
                    case VertexAttribute.Tangent: outc.TanA = j; break;
                }
            }
            for (int nv = 0; nv < used.Count; nv++)
            {
                int v = used[nv];
                for (int j = 0; j < keep.Count; j++)
                {
                    int a = keep[j];
                    var at = Attrs[a];
                    int sz = Size(at.format) * at.dimension;
                    Buffer.BlockCopy(Data[at.stream], v * Stride[at.stream] + Off[a], data[attrs[j].stream], nv * stride[attrs[j].stream] + off[j], sz);
                }
                Matrix4x4 m = worldToLocal * VertexMatrix(v);
                Vector3 p = worldToLocal.MultiplyPoint3x4(W[v]);
                Vector4 p4 = outc.Get(nv, outc.PosA);
                outc.Set(nv, outc.PosA, new Vector4(p.x, p.y, p.z, p4.w));
                if (outc.NrmA >= 0)
                {
                    Vector4 n = outc.Get(nv, outc.NrmA);
                    Vector3 nl = m.MultiplyVector(n).normalized;
                    outc.Set(nv, outc.NrmA, new Vector4(nl.x, nl.y, nl.z, n.w));
                }
                if (outc.TanA >= 0)
                {
                    Vector4 t = outc.Get(nv, outc.TanA);
                    Vector3 tl = m.MultiplyVector(t).normalized;
                    outc.Set(nv, outc.TanA, new Vector4(tl.x, tl.y, tl.z, t.w));
                }
            }
            newTris = new List<int>[tris.Length];
            for (int i = 0; i < tris.Length; i++)
            {
                newTris[i] = new List<int>(tris[i].Count);
                foreach (int v in tris[i]) newTris[i].Add(map[v]);
            }
            source = used;
            outc.Tris = newTris;
            return outc;
        }

        /// Writes the vertex data back into a mesh made by Build (same layout), e.g. after moving vertices.
        public void Upload(Mesh m, Bounds bounds)
        {
            var fl = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
            var streams = new HashSet<int>();
            foreach (int a in new[] { PosA, NrmA, TanA }) if (a >= 0) streams.Add(Attrs[a].stream);
            foreach (int s in streams) m.SetVertexBufferData(Data[s], 0, 0, Count * Stride[s], s, fl);
            m.bounds = bounds;
        }

        // ------------------------------------------------------------------ formats

        [StructLayout(LayoutKind.Explicit)]
        struct F32 { [FieldOffset(0)] public float F; [FieldOffset(0)] public uint U; }

        public static int Size(VertexAttributeFormat f)
        {
            switch (f)
            {
                case VertexAttributeFormat.Float32:
                case VertexAttributeFormat.UInt32:
                case VertexAttributeFormat.SInt32: return 4;
                case VertexAttributeFormat.Float16:
                case VertexAttributeFormat.UNorm16:
                case VertexAttributeFormat.SNorm16:
                case VertexAttributeFormat.UInt16:
                case VertexAttributeFormat.SInt16: return 2;
                default: return 1;
            }
        }

        static bool IsInt(VertexAttributeFormat f) =>
            f == VertexAttributeFormat.UInt8 || f == VertexAttributeFormat.SInt8 || f == VertexAttributeFormat.UInt16 ||
            f == VertexAttributeFormat.SInt16 || f == VertexAttributeFormat.UInt32 || f == VertexAttributeFormat.SInt32;

        static float ReadF(byte[] d, int o, VertexAttributeFormat f)
        {
            switch (f)
            {
                case VertexAttributeFormat.Float32: return new F32 { U = (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24) }.F;
                case VertexAttributeFormat.Float16: return Mathf.HalfToFloat((ushort)(d[o] | d[o + 1] << 8));
                case VertexAttributeFormat.UNorm8: return d[o] / 255f;
                case VertexAttributeFormat.SNorm8: return Mathf.Max((sbyte)d[o] / 127f, -1f);
                case VertexAttributeFormat.UNorm16: return (ushort)(d[o] | d[o + 1] << 8) / 65535f;
                case VertexAttributeFormat.SNorm16: return Mathf.Max((short)(d[o] | d[o + 1] << 8) / 32767f, -1f);
                case VertexAttributeFormat.UInt8: return d[o];
                case VertexAttributeFormat.SInt8: return (sbyte)d[o];
                case VertexAttributeFormat.UInt16: return (ushort)(d[o] | d[o + 1] << 8);
                case VertexAttributeFormat.SInt16: return (short)(d[o] | d[o + 1] << 8);
                case VertexAttributeFormat.UInt32: return (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);
                case VertexAttributeFormat.SInt32: return d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24;
            }
            return 0f;
        }

        static void W16(byte[] d, int o, int v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); }
        static void W32(byte[] d, int o, uint v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24); }

        static void WriteF(byte[] d, int o, VertexAttributeFormat f, float x)
        {
            switch (f)
            {
                case VertexAttributeFormat.Float32: W32(d, o, new F32 { F = x }.U); break;
                case VertexAttributeFormat.Float16: W16(d, o, Mathf.FloatToHalf(x)); break;
                case VertexAttributeFormat.UNorm8: d[o] = (byte)Mathf.Clamp(Mathf.RoundToInt(x * 255f), 0, 255); break;
                case VertexAttributeFormat.SNorm8: d[o] = (byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(x * 127f), -127, 127); break;
                case VertexAttributeFormat.UNorm16: W16(d, o, Mathf.Clamp(Mathf.RoundToInt(x * 65535f), 0, 65535)); break;
                case VertexAttributeFormat.SNorm16: W16(d, o, (short)Mathf.Clamp(Mathf.RoundToInt(x * 32767f), -32767, 32767)); break;
                case VertexAttributeFormat.UInt8: d[o] = (byte)Mathf.Clamp(Mathf.RoundToInt(x), 0, 255); break;
                case VertexAttributeFormat.SInt8: d[o] = (byte)(sbyte)Mathf.Clamp(Mathf.RoundToInt(x), -128, 127); break;
                case VertexAttributeFormat.UInt16: W16(d, o, Mathf.Clamp(Mathf.RoundToInt(x), 0, 65535)); break;
                case VertexAttributeFormat.SInt16: W16(d, o, (short)Mathf.Clamp(Mathf.RoundToInt(x), -32768, 32767)); break;
                case VertexAttributeFormat.UInt32: W32(d, o, (uint)Mathf.Max(0, Mathf.RoundToInt(x))); break;
                case VertexAttributeFormat.SInt32: W32(d, o, (uint)Mathf.RoundToInt(x)); break;
            }
        }
    }

    /// Small helpers for putting cut-out parts of the game's models on screen.
    internal static class PartKit
    {
        /// A renderer's material property blocks (the game sets camouflage, scorch and per-unit values through them).
        public static void CopyLook(Renderer from, Renderer to)
        {
            if (from == null || to == null) return;
            to.sharedMaterials = from.sharedMaterials;
            to.shadowCastingMode = from.shadowCastingMode;
            to.receiveShadows = from.receiveShadows;
            try
            {
                var mpb = new MaterialPropertyBlock();
                if (from.HasPropertyBlock()) { from.GetPropertyBlock(mpb); to.SetPropertyBlock(mpb); }
                for (int i = 0; i < from.sharedMaterials.Length; i++)
                {
                    mpb.Clear();
                    from.GetPropertyBlock(mpb, i);
                    if (!mpb.isEmpty) to.SetPropertyBlock(mpb, i);
                }
            }
            catch { }
        }

        /// The mesh a renderer draws.
        public static Mesh MeshOf(Renderer r) => r is SkinnedMeshRenderer s ? s.sharedMesh : r != null ? r.GetComponent<MeshFilter>()?.sharedMesh : null;

        public static void SetMesh(Renderer r, Mesh m)
        {
            if (r is SkinnedMeshRenderer s) s.sharedMesh = m;
            else { var mf = r != null ? r.GetComponent<MeshFilter>() : null; if (mf != null) mf.sharedMesh = m; }
        }

        /// Deterministic noise in [0, 1).
        public static float Hash(int a, int b = 0)
        {
            unchecked
            {
                uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u;
                h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        public static float Smooth01(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        /// Distance from p to the triangle abc (Ericson, Real-Time Collision Detection 5.1.5).
        public static float PointTri(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return ap.magnitude;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return bp.magnitude;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return (p - (a + ab * (d1 / (d1 - d3)))).magnitude;
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return cp.magnitude;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return (p - (a + ac * (d2 / (d2 - d6)))).magnitude;
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) return (p - (b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))))).magnitude;
            float den = 1f / (va + vb + vc);
            return (p - (a + ab * (vb * den) + ac * (vc * den))).magnitude;
        }

        public static float AngleDiff(float a, float b)
        {
            float d = Mathf.Repeat(a - b + Mathf.PI, Mathf.PI * 2f) - Mathf.PI;
            return Mathf.Abs(d);
        }

        /// Simple procedural mesh building: positions, colours and triangles per submesh, normals recalculated.
        public sealed class Builder
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Color> C = new List<Color>();
            public readonly List<int>[] T;
            public Builder(int subs = 1) { T = new List<int>[subs]; for (int i = 0; i < subs; i++) T[i] = new List<int>(); }
            public int Add(Vector3 p, Color c) { V.Add(p); C.Add(c); return V.Count - 1; }
            public int Add(Vector3 p) => Add(p, Color.white);
            public void Tri(int s, int a, int b, int c) { T[s].Add(a); T[s].Add(b); T[s].Add(c); }
            public void Quad(int s, int a, int b, int c, int d) { Tri(s, a, b, c); Tri(s, a, c, d); }
            /// Both faces (thin sheet metal): the back gets its own vertices so it is lit from its own side.
            public void Tri2(int s, int a, int b, int c)
            {
                Tri(s, a, b, c);
                int a2 = Add(V[a], C[a]), b2 = Add(V[b], C[b]), c2 = Add(V[c], C[c]);
                Tri(s, a2, c2, b2);
            }
            public void Quad2(int s, int a, int b, int c, int d) { Tri2(s, a, b, c); Tri2(s, a, c, d); }
            /// A triangle facing `want` (its winding is turned if needed).
            public void TriO(int s, int a, int b, int c, Vector3 want)
            {
                if (Vector3.Dot(Vector3.Cross(V[b] - V[a], V[c] - V[a]), want) < 0f) Tri(s, a, c, b); else Tri(s, a, b, c);
            }
            public void QuadO(int s, int a, int b, int c, int d, Vector3 want) { TriO(s, a, b, c, want); TriO(s, a, c, d, want); }
            public void Map(Func<Vector3, Vector3> f) { for (int i = 0; i < V.Count; i++) V[i] = f(V[i]); }
            public bool Empty { get { foreach (var l in T) if (l.Count > 0) return false; return true; } }
            public Mesh Build(string name)
            {
                var m = new Mesh { name = name };
                if (V.Count > 65000) m.indexFormat = IndexFormat.UInt32;
                m.SetVertices(V);
                m.SetColors(C);
                m.subMeshCount = T.Length;
                for (int i = 0; i < T.Length; i++) m.SetTriangles(T[i], i, false);
                m.RecalculateNormals();
                m.RecalculateBounds();
                return m;
            }
        }

        /// A child object drawing `mesh` with `mats`, in `parent`'s space.
        public static GameObject Child(Transform parent, string name, Mesh mesh, Material[] mats, int layer, bool shadows = true)
        {
            var go = new GameObject(name) { layer = layer };
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = mats;
            r.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            return go;
        }
    }
}
