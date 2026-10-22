using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GHPCHitCam
{
    /// CPU mesh helpers for real holes: ray vs mesh and cutting ragged holes into readable meshes. game plates are often
    /// a couple of huge tris, so tris near a hole get subdivided first, then bits inside the (jittered) radius dropped -> torn edge
    internal static class MeshCutter
    {
        class Cached { public Vector3[] V; public int[] T; public Grid G; }
        static readonly Dictionary<Mesh, Cached> _cache = new Dictionary<Mesh, Cached>();

        static Cached Data(Mesh m)
        {
            if (_cache.TryGetValue(m, out var c)) return c;
            c = new Cached { V = m.vertices, T = m.triangles };
            c.G = BuildGrid(c.V, c.T);
            if (_cache.Count > 400) _cache.Clear();
            _cache[m] = c;
            return c;
        }

        /// reads a readable mesh and builds its ray grid now (warm-up) so the first hit on it doesn't have to
        public static void Prepare(Mesh m) { if (m != null && m.isReadable) Data(m); }

        /// mesh tris sorted into a uniform grid (about two a cell), built once per mesh, a ray only tests the cells it crosses.
        /// a hull mesh has tens of thousands of tris, testing all of them was 5-15 ms a ray
        sealed class Grid
        {
            public Vector3 Min, Max, Cell;
            public int Nx, Ny, Nz;
            public int[] Start, Items, Stamp;
            public int Mark;
        }

        static Grid BuildGrid(Vector3[] V, int[] T)
        {
            int nt = T.Length / 3;
            if (nt < 200 || V.Length == 0) return null;          // small meshes: testing every tri is quick anyway
            Vector3 min = V[0], max = V[0];
            for (int i = 1; i < V.Length; i++) { min = Vector3.Min(min, V[i]); max = Vector3.Max(max, V[i]); }
            Vector3 size = max - min + Vector3.one * 1e-4f;
            float cell = Mathf.Pow(Mathf.Max(size.x * size.y * size.z, 1e-9f) / (nt * 0.5f), 1f / 3f);
            int nx = Mathf.Clamp(Mathf.CeilToInt(size.x / cell), 1, 64), ny = Mathf.Clamp(Mathf.CeilToInt(size.y / cell), 1, 64), nz = Mathf.Clamp(Mathf.CeilToInt(size.z / cell), 1, 64);
            var g = new Grid { Min = min, Max = min + size, Nx = nx, Ny = ny, Nz = nz, Cell = new Vector3(size.x / nx, size.y / ny, size.z / nz), Stamp = new int[nt] };
            int cells = nx * ny * nz;
            var count = new int[cells + 1];
            void Range(int t, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1)
            {
                Vector3 a = V[T[t]], b = V[T[t + 1]], c = V[T[t + 2]];
                Vector3 lo = Vector3.Min(a, Vector3.Min(b, c)) - g.Min, hi = Vector3.Max(a, Vector3.Max(b, c)) - g.Min;
                x0 = Mathf.Clamp((int)(lo.x / g.Cell.x), 0, nx - 1); x1 = Mathf.Clamp((int)(hi.x / g.Cell.x), 0, nx - 1);
                y0 = Mathf.Clamp((int)(lo.y / g.Cell.y), 0, ny - 1); y1 = Mathf.Clamp((int)(hi.y / g.Cell.y), 0, ny - 1);
                z0 = Mathf.Clamp((int)(lo.z / g.Cell.z), 0, nz - 1); z1 = Mathf.Clamp((int)(hi.z / g.Cell.z), 0, nz - 1);
            }
            long total = 0;
            for (int t = 0; t + 2 < T.Length; t += 3)
            {
                Range(t, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1);
                for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++) count[(x * ny + y) * nz + z]++;
                total += (long)(x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);
                if (total > 4000000) return null;                // a few huge tris over everything: not worth it
            }
            g.Start = new int[cells + 1];
            for (int i = 0; i < cells; i++) g.Start[i + 1] = g.Start[i] + count[i];
            g.Items = new int[g.Start[cells]];
            var fill = new int[cells];
            System.Array.Copy(g.Start, fill, cells);
            for (int t = 0; t + 2 < T.Length; t += 3)
            {
                Range(t, out int x0, out int x1, out int y0, out int y1, out int z0, out int z1);
                for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++) g.Items[fill[(x * ny + y) * nz + z]++] = t / 3;
            }
            return g;
        }

        /// Möller-Trumbore: distance along the ray to triangle t (index of its first corner), or -1
        static float HitTri(Vector3[] V, int[] T, int t, Vector3 o, Vector3 d)
        {
            Vector3 a = V[T[t]], b = V[T[t + 1]], c = V[T[t + 2]];
            Vector3 e1 = b - a, e2 = c - a;
            Vector3 p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (det > -1e-9f && det < 1e-9f) return -1f;
            float inv = 1f / det;
            Vector3 s = o - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0f || u > 1f) return -1f;
            Vector3 q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0f || u + v > 1f) return -1f;
            return Vector3.Dot(e2, q) * inv;
        }

        /// walks the cells along the ray (Amanatides-Woo) and tests their tris, nearest cells first
        static int GridRay(Cached data, Vector3 o, Vector3 d, float len, out float best)
        {
            var g = data.G; var V = data.V; var T = data.T;
            best = float.MaxValue;
            int bestTri = -1;
            float t0 = 0f, t1 = len;
            for (int a = 0; a < 3; a++)
            {
                if (Mathf.Abs(d[a]) < 1e-12f) { if (o[a] < g.Min[a] || o[a] > g.Max[a]) return -1; continue; }
                float ta = (g.Min[a] - o[a]) / d[a], tb = (g.Max[a] - o[a]) / d[a];
                if (ta > tb) { float x = ta; ta = tb; tb = x; }
                t0 = Mathf.Max(t0, ta); t1 = Mathf.Min(t1, tb);
                if (t0 > t1) return -1;
            }
            if (++g.Mark == int.MaxValue) { System.Array.Clear(g.Stamp, 0, g.Stamp.Length); g.Mark = 1; }
            Vector3 p = o + d * t0 - g.Min;
            int ix = Mathf.Clamp((int)(p.x / g.Cell.x), 0, g.Nx - 1), iy = Mathf.Clamp((int)(p.y / g.Cell.y), 0, g.Ny - 1), iz = Mathf.Clamp((int)(p.z / g.Cell.z), 0, g.Nz - 1);
            int sx = d.x > 0f ? 1 : -1, sy = d.y > 0f ? 1 : -1, sz = d.z > 0f ? 1 : -1;
            float Next(float pos, int i, int step, float cell, float dir) => Mathf.Abs(dir) < 1e-12f ? float.MaxValue : ((i + (step > 0 ? 1 : 0)) * cell - pos) / dir + t0;
            float tx = Next(p.x, ix, sx, g.Cell.x, d.x), ty = Next(p.y, iy, sy, g.Cell.y, d.y), tz = Next(p.z, iz, sz, g.Cell.z, d.z);
            float dx = Mathf.Abs(d.x) < 1e-12f ? float.MaxValue : g.Cell.x / Mathf.Abs(d.x);
            float dy = Mathf.Abs(d.y) < 1e-12f ? float.MaxValue : g.Cell.y / Mathf.Abs(d.y);
            float dz = Mathf.Abs(d.z) < 1e-12f ? float.MaxValue : g.Cell.z / Mathf.Abs(d.z);
            while (true)
            {
                int cell = (ix * g.Ny + iy) * g.Nz + iz;
                for (int k = g.Start[cell]; k < g.Start[cell + 1]; k++)
                {
                    int tri = g.Items[k];
                    if (g.Stamp[tri] == g.Mark) continue;
                    g.Stamp[tri] = g.Mark;
                    float t = HitTri(V, T, tri * 3, o, d);
                    if (t > 0f && t < best && t <= len) { best = t; bestTri = tri * 3; }
                }
                float tn = Mathf.Min(tx, Mathf.Min(ty, tz));
                if (best <= tn || tn > t1) break;
                if (tx <= ty && tx <= tz) { ix += sx; tx += dx; if (ix < 0 || ix >= g.Nx) break; }
                else if (ty <= tz) { iy += sy; ty += dy; if (iy < 0 || iy >= g.Ny) break; }
                else { iz += sz; tz += dz; if (iz < 0 || iz >= g.Nz) break; }
            }
            return bestTri;
        }

        /// nearest hit of a world ray on a readable mesh (any face direction)
        public static bool Raycast(Mesh m, Matrix4x4 l2w, Ray worldRay, float maxDist, out float dist, out Vector3 normalW)
        {
            dist = float.MaxValue;
            normalW = Vector3.zero;
            if (m == null || !m.isReadable) return false;
            var w2l = l2w.inverse;
            Vector3 o = w2l.MultiplyPoint3x4(worldRay.origin);
            Vector3 endL = w2l.MultiplyPoint3x4(worldRay.origin + worldRay.direction * maxDist);
            Vector3 d = endL - o;
            float len = d.magnitude;
            if (len < 1e-6f) return false;
            d /= len;
            var data = Data(m);
            var V = data.V; var T = data.T;
            float best = float.MaxValue;
            int bestTri = -1;
            if (data.G != null) bestTri = GridRay(data, o, d, len, out best);
            else
                for (int i = 0; i + 2 < T.Length; i += 3)
                {
                    float t = HitTri(V, T, i, o, d);
                    if (t > 0f && t < best && t <= len) { best = t; bestTri = i; }
                }
            if (bestTri < 0) return false;
            Vector3 hitL = o + d * best;
            Vector3 n = Vector3.Cross(V[T[bestTri + 1]] - V[T[bestTri]], V[T[bestTri + 2]] - V[T[bestTri]]).normalized;
            Vector3 hitW = l2w.MultiplyPoint3x4(hitL);
            dist = Vector3.Distance(worldRay.origin, hitW);
            normalW = l2w.inverse.transpose.MultiplyVector(n).normalized;
            return true;
        }

        /// copy of `src` with ragged holes cut out, holes given in the space `meshToSpace` maps into.
        /// null if nothing got cut (or the mesh isn't CPU-readable)
        public static Mesh Cut(Mesh src, Matrix4x4 meshToSpace, List<(Vector3 p, float r)> holes, int seed)
        {
            var job = Begin(src, meshToSpace, holes, seed, false);
            return job == null ? null : Finish(job);
        }

        /// a cut in progress: mesh data read on the main thread, cutting on a worker (Begin with async), new mesh built on the
        /// main thread again (Finish, once Done)
        internal sealed class Job
        {
            public string Name;
            public List<(Vector3 p, float r)> Holes;
            public List<Vector3> V, N;
            public List<Vector4> Tg;
            public List<Color> C;
            public List<List<Vector4>> Uvs;
            public int[][] Subs;
            public int Seed;
            public bool Changed;
            public volatile bool Done;
            public string Error;
        }

        public static Job Begin(Mesh src, Matrix4x4 meshToSpace, List<(Vector3 p, float r)> holes, int seed, bool async)
        {
            if (src == null || !src.isReadable || holes.Count == 0) return null;
            var inv = meshToSpace.inverse;
            float scale = Mathf.Max(Mathf.Abs(meshToSpace.lossyScale.x), 1e-5f);
            var lh = new List<(Vector3 p, float r)>();
            var mb = src.bounds;
            foreach (var h in holes)
            {
                var lp = inv.MultiplyPoint3x4(h.p);
                float lr = h.r / scale;
                var eb = mb; eb.Expand(lr * 2.5f);
                if (eb.Contains(lp)) lh.Add((lp, lr));
            }
            if (lh.Count == 0) return null;

            // grab every attribute we need to interpolate
            var job = new Job { Name = src.name, Holes = lh, Seed = seed, V = new List<Vector3>(src.vertices), N = new List<Vector3>(src.normals),
                                Tg = new List<Vector4>(src.tangents), C = new List<Color>(src.colors), Uvs = new List<List<Vector4>>(), Subs = new int[src.subMeshCount][] };
            for (int ch = 0; ch < 4; ch++)
            {
                var l = new List<Vector4>();
                src.GetUVs(ch, l);
                job.Uvs.Add(l.Count == job.V.Count ? l : null);
            }
            for (int sub = 0; sub < job.Subs.Length; sub++) job.Subs[sub] = src.GetTriangles(sub);
            if (async) System.Threading.ThreadPool.QueueUserWorkItem(_ => Run(job));
            else Run(job);
            return job;
        }

        static void Run(Job job)
        {
            try { Compute(job); }
            catch (System.Exception e) { job.Error = e.Message; job.Changed = false; }
            job.Done = true;
        }

        /// the cutting itself: math on the copied data only (worker thread)
        static void Compute(Job job)
        {
            var V = job.V; var N = job.N; var Tg = job.Tg; var C = job.C; var uvs = job.Uvs; var lh = job.Holes;
            bool hasN = N.Count == V.Count, hasT = Tg.Count == V.Count, hasC = C.Count == V.Count;
            var rnd = new System.Random(job.Seed);
            bool changed = false;
            var midCache = new Dictionary<long, int>();

            int Mid(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (midCache.TryGetValue(key, out int m)) return m;
                m = V.Count;
                V.Add((V[a] + V[b]) * 0.5f);
                if (hasN) N.Add(((N[a] + N[b]) * 0.5f).normalized);
                if (hasT) Tg.Add((Tg[a] + Tg[b]) * 0.5f);
                if (hasC) C.Add((C[a] + C[b]) * 0.5f);
                foreach (var l in uvs) l?.Add((l[a] + l[b]) * 0.5f);
                midCache[key] = m;
                return m;
            }

            var newSubs = new List<int[]>();
            for (int sub = 0; sub < job.Subs.Length; sub++)
            {
                var tris = job.Subs[sub];
                var outT = new List<int>(tris.Length);
                var stack = new Stack<(int a, int b, int c, int depth)>();
                for (int i = 0; i < tris.Length; i += 3)
                {
                    stack.Push((tris[i], tris[i + 1], tris[i + 2], 0));
                    while (stack.Count > 0)
                    {
                        var (a, b, c, depth) = stack.Pop();
                        Vector3 pa = V[a], pb = V[b], pc = V[c];
                        Vector3 cen = (pa + pb + pc) / 3f;
                        float rad = Mathf.Sqrt(Mathf.Max((pa - cen).sqrMagnitude, Mathf.Max((pb - cen).sqrMagnitude, (pc - cen).sqrMagnitude)));
                        float edge = Mathf.Sqrt(Mathf.Max((pa - pb).sqrMagnitude, Mathf.Max((pb - pc).sqrMagnitude, (pc - pa).sqrMagnitude)));
                        bool near = false, inside = false;
                        foreach (var h in lh)
                        {
                            float dc = (cen - h.p).magnitude;
                            if (dc < h.r + rad) near = true;
                            float jag = h.r * (0.82f + 0.36f * (float)rnd.NextDouble());
                            if (dc < jag && edge < h.r * 0.6f) inside = true;
                        }
                        if (inside) { changed = true; continue; }
                        if (near && edge > lh[0].r * 0.35f && depth < 7 && V.Count < 400000)
                        {
                            int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                            stack.Push((a, ab, ca, depth + 1));
                            stack.Push((ab, b, bc, depth + 1));
                            stack.Push((ca, bc, c, depth + 1));
                            stack.Push((ab, bc, ca, depth + 1));
                            continue;
                        }
                        outT.Add(a); outT.Add(b); outT.Add(c);
                    }
                }
                newSubs.Add(outT.ToArray());
            }
            job.Subs = newSubs.ToArray();
            job.Changed = changed;
        }

        /// holed mesh of a finished job (main thread), null when nothing got cut
        public static Mesh Finish(Job job)
        {
            if (!job.Done || !job.Changed) return null;
            var V = job.V; var N = job.N; var Tg = job.Tg; var C = job.C; var uvs = job.Uvs; var newSubs = job.Subs;
            bool hasN = N.Count == V.Count, hasT = Tg.Count == V.Count, hasC = C.Count == V.Count;
            var m2 = new Mesh { name = job.Name + " (holed)" };
            if (V.Count > 65000) m2.indexFormat = IndexFormat.UInt32;
            m2.SetVertices(V);
            if (hasN) m2.SetNormals(N);
            if (hasT) m2.SetTangents(Tg);
            if (hasC) m2.SetColors(C);
            for (int ch = 0; ch < 4; ch++) if (uvs[ch] != null) m2.SetUVs(ch, uvs[ch]);
            m2.subMeshCount = newSubs.Length;
            for (int sub = 0; sub < newSubs.Length; sub++) m2.SetTriangles(newSubs[sub], sub, false);
            m2.RecalculateBounds();
            return m2;
        }
    }
}
