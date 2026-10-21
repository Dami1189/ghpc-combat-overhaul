using System.Collections.Generic;
using UnityEngine;

namespace GHPCHitCam
{
    /// The long rod drawn as one deforming tube: rings around a centreline (tail to nose) with a radius and a colour
    /// per ring, closed at both ends. The nose end can curl back in ragged petals (the eroding head's lip) and either
    /// end can be jagged (a fracture face). Lit per vertex from the camera. One mesh for the rod and all its pieces; a
    /// faint second pass shows the parts hidden behind armour or modules (the x-ray sees the rod inside the plate).
    internal class RodTube
    {
        readonly Mesh _mesh;
        readonly Renderer _solid, _ghost;
        readonly int _m;
        readonly List<Vector3> _v = new List<Vector3>();
        readonly List<Color> _c = new List<Color>();
        readonly List<int> _t = new List<int>();

        public RodTube(Transform parent, int ringSides)
        {
            _m = Mathf.Clamp(ringSides, 6, 24);
            var go = new GameObject("rod tube");
            go.layer = Vis.Layer;
            go.transform.SetParent(parent, false);
            _mesh = new Mesh { name = "rod tube" };
            _mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _solid = go.AddComponent<MeshRenderer>();
            _solid.sharedMaterial = Vis.Unlit;
            _ghost = Vis.Child(go.transform, "rod tube (hidden part)", _mesh, Matrix4x4.identity, Vis.ThroughHidden).GetComponent<Renderer>();
            Show(false);
        }

        public void Show(bool on) { _solid.enabled = _ghost.enabled = on; }

        public void Begin() { _v.Clear(); _c.Clear(); _t.Clear(); }

        static float Hash(int a, int b) { uint h = (uint)(a * 73856093) ^ (uint)(b * 19349663); h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15; return (h & 0xffff) / 65535f; }

        /// nodes tail -> nose; jagTail / jagNose: how ragged each end face is (m); curl: how far the nose ring's lip
        /// curls back (m, 0 = a clean end); seed varies the raggedness; phase animates the lip a little.
        public void AddTube(Vector3[] nodes, float[] rad, Color[] col, int count, Vector3 cam, float jagTail, float jagNose, float curl, int seed, float phase)
        {
            if (count < 2) return;
            int b0 = _v.Count;
            // a twist-free frame along the centreline (parallel transport)
            Vector3 t0 = (nodes[1] - nodes[0]).normalized;
            Vector3 u = Vector3.Cross(t0, Vector3.up);
            if (u.sqrMagnitude < 1e-4f) u = Vector3.Cross(t0, Vector3.right);
            u.Normalize();
            for (int i = 0; i < count; i++)
            {
                Vector3 t = i == 0 ? nodes[1] - nodes[0] : i == count - 1 ? nodes[i] - nodes[i - 1] : nodes[i + 1] - nodes[i - 1];
                t = t.sqrMagnitude > 1e-12f ? t.normalized : t0;
                u = Vector3.ProjectOnPlane(u, t);
                u = u.sqrMagnitude > 1e-8f ? u.normalized : Vector3.Cross(t, Vector3.up).normalized;
                Vector3 w = Vector3.Cross(t, u);
                Vector3 light = (cam - nodes[i]).normalized + Vector3.up * 0.35f;
                light.Normalize();
                for (int k = 0; k < _m; k++)
                {
                    float a = k * Mathf.PI * 2f / _m;
                    Vector3 radial = u * Mathf.Cos(a) + w * Mathf.Sin(a);
                    Vector3 p = nodes[i] + radial * rad[i];
                    if (i == count - 1)
                    {
                        // the nose: its lip curls back and out in ragged petals; a fracture face is jagged
                        float h = Hash(seed + k, 7);
                        float petal = 0.55f + 0.45f * Mathf.Sin(a * 3f + phase + h * 6f);
                        p += -t * (curl * (0.35f + 0.65f * h) * petal) + radial * (curl * 0.35f * petal);
                        p += t * (jagNose * (Hash(seed + k, 11) - 0.5f));
                    }
                    else if (i == 0) p += t * (jagTail * (Hash(seed + k, 13) - 0.5f));
                    _v.Add(p);
                    float lit = 0.38f + 0.62f * Mathf.Max(0f, Vector3.Dot(radial, light));
                    _c.Add(new Color(col[i].r * lit, col[i].g * lit, col[i].b * lit, 1f));
                }
            }
            for (int i = 0; i + 1 < count; i++)
                for (int k = 0; k < _m; k++)
                {
                    int a = b0 + i * _m + k, b = b0 + i * _m + (k + 1) % _m;
                    _t.Add(a); _t.Add(b); _t.Add(a + _m); _t.Add(b); _t.Add(b + _m); _t.Add(a + _m);
                }
            // end faces: the tail facing back, the nose (a little domed, or recessed when it erodes) facing forward
            Vector3 tTail = (nodes[1] - nodes[0]).normalized, tNose = (nodes[count - 1] - nodes[count - 2]).normalized;
            int cTail = _v.Count;
            _v.Add(nodes[0] - tTail * (jagTail * 0.3f)); _c.Add(col[0] * 0.75f);
            int cNose = _v.Count;
            _v.Add(nodes[count - 1] + tNose * (rad[count - 1] * (curl > 0f ? -0.15f : 0.25f))); _c.Add(col[count - 1]);
            for (int k = 0; k < _m; k++)
            {
                int a = b0 + k, b = b0 + (k + 1) % _m;
                _t.Add(cTail); _t.Add(b); _t.Add(a);
                int an = b0 + (count - 1) * _m + k, bn = b0 + (count - 1) * _m + (k + 1) % _m;
                _t.Add(cNose); _t.Add(an); _t.Add(bn);
            }
        }

        public void End(bool show)
        {
            _mesh.Clear();
            if (_v.Count > 0)
            {
                _mesh.SetVertices(_v);
                _mesh.SetColors(_c);
                _mesh.SetTriangles(_t, 0);
                _mesh.RecalculateBounds();
            }
            Show(show && _v.Count > 0);
        }

        public void Dispose() { if (_mesh != null) Object.Destroy(_mesh); }
    }
}
