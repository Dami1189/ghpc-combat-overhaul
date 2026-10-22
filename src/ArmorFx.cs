using System.Collections.Generic;
using UnityEngine;

namespace GHPCHitCam
{
    /// procedural armor damage: glowing torn holes, petals, dents, ERA flashes, HE blasts, hull mesh cutting
    internal static class ArmorFx
    {
        /// jagged ring around a hole in the XY plane, facing +Z, inner edge hot, outer edge dark
        public static Mesh HoleRim(float r, float ragged, int seed)
        {
            var rnd = new System.Random(seed);
            int n = 28;
            var v = new List<Vector3>();
            var c = new List<Color>();
            var tri = new List<int>();
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                float jag = 1f + ((float)rnd.NextDouble() - 0.5f) * ragged;
                Vector3 dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                v.Add(dir * r * jag);
                c.Add(new Color(1f, 0.95f, 0.7f, 1f));
                v.Add(dir * r * (1.55f + (float)rnd.NextDouble() * 0.5f));
                c.Add(new Color(0.55f, 0.08f, 0.02f, 1f));
            }
            for (int i = 0; i < n; i++)
            {
                int a0 = i * 2, a1 = a0 + 1, b0 = ((i + 1) % n) * 2, b1 = b0 + 1;
                tri.Add(a0); tri.Add(b0); tri.Add(a1);
                tri.Add(b0); tri.Add(b1); tri.Add(a1);
            }
            var m = new Mesh { name = "hole rim" };
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(tri, 0);
            m.RecalculateBounds();
            return m;
        }

        /// petals: triangle shards of torn plate around the hole, bent along +Z (into the vehicle)
        public static Mesh Petals(float r, int count, float bend, int seed)
        {
            var rnd = new System.Random(seed);
            var v = new List<Vector3>();
            var c = new List<Color>();
            var tri = new List<int>();
            for (int i = 0; i < count; i++)
            {
                float a = (i + (float)rnd.NextDouble() * 0.4f) * Mathf.PI * 2f / count;
                float w = Mathf.PI / count * 0.8f;
                float len = r * (0.9f + (float)rnd.NextDouble() * 0.9f);
                Vector3 d0 = new Vector3(Mathf.Cos(a - w), Mathf.Sin(a - w), 0f);
                Vector3 d1 = new Vector3(Mathf.Cos(a + w), Mathf.Sin(a + w), 0f);
                Vector3 dm = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                int b = v.Count;
                v.Add(d0 * r * 1.05f);
                v.Add(d1 * r * 1.05f);
                // tip: pulled toward the centre and pushed inward
                v.Add(dm * r * (1.05f - 0.6f * bend) + Vector3.forward * len * bend);
                c.Add(new Color(0.9f, 0.45f, 0.12f)); c.Add(new Color(0.9f, 0.45f, 0.12f)); c.Add(new Color(1f, 0.9f, 0.6f));
                tri.Add(b); tri.Add(b + 1); tri.Add(b + 2);
                tri.Add(b); tri.Add(b + 2); tri.Add(b + 1);
            }
            var m = new Mesh { name = "petals" };
            m.SetVertices(v);
            m.SetColors(c);
            m.SetTriangles(tri, 0);
            m.RecalculateBounds();
            return m;
        }

        /// copy of a hull mesh with every tri near any hole removed (only if the game left the mesh CPU-readable)
        public static Mesh CutHoles(Mesh src, Matrix4x4 meshToLocal, List<(Vector3 p, float r)> holes)
        {
            if (src == null || !src.isReadable || holes.Count == 0) return null;
            var inv = meshToLocal.inverse;
            var verts = src.vertices;
            var localHoles = new List<(Vector3 p, float r2)>();
            float scale = Mathf.Max(meshToLocal.lossyScale.x, 1e-4f);
            foreach (var h in holes)
            {
                float rr = h.r / scale;
                localHoles.Add((inv.MultiplyPoint3x4(h.p), rr * rr));
            }
            var b = src.bounds;
            bool anyNear = false;
            foreach (var h in localHoles)
            {
                var bb = b; bb.Expand(Mathf.Sqrt(h.r2) * 2f);
                if (bb.Contains(h.p)) anyNear = true;
            }
            if (!anyNear) return null;

            var m = Object.Instantiate(src);
            bool changed = false;
            for (int s = 0; s < m.subMeshCount; s++)
            {
                var tris = m.GetTriangles(s);
                var keep = new List<int>(tris.Length);
                for (int i = 0; i < tris.Length; i += 3)
                {
                    Vector3 cen = (verts[tris[i]] + verts[tris[i + 1]] + verts[tris[i + 2]]) / 3f;
                    bool cut = false;
                    foreach (var h in localHoles)
                        if ((cen - h.p).sqrMagnitude < h.r2) { cut = true; break; }
                    if (cut) { changed = true; continue; }
                    keep.Add(tris[i]); keep.Add(tris[i + 1]); keep.Add(tris[i + 2]);
                }
                m.SetTriangles(keep, s);
            }
            if (!changed) { Object.Destroy(m); return null; }
            return m;
        }
    }
}
