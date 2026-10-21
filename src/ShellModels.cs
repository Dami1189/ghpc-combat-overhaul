using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GHPCHitCam
{
    /// The flying projectile as the real thing, built from the round's own data (calibre, penetrator radius, mass,
    /// category, guidance, speed). Nose at the origin, tail toward -Z (the round's position is where its nose is).
    /// - APFSDS: the long rod (length from its mass and tungsten's density), windshield, tracer cup, six tail fins
    /// - full-bore AP / APHE: boat tail, brass driving band, ogive, ballistic cap
    /// - HEAT-FS: stand-off probe, nose cone, warhead body, tail boom, six fins
    /// - HE-FRAG: boat tail, driving band, ogive, nose fuze (115-125 mm smoothbore rounds get tail fins)
    /// - ATGMs / rockets: body, nose cone, four big fins; bombs: ogive, body, tail cone, box fins
    /// Colours follow the usual markings (black AP, olive HE, grey tungsten rod, brass bands), shading baked per face.
    internal static class ShellModels
    {
        static readonly Dictionary<AmmoType, Mesh> _cache = new Dictionary<AmmoType, Mesh>();

        public static Mesh Get(AmmoType a, out float length)
        {
            length = 0.3f;
            if (a == null) return null;
            if (_cache.TryGetValue(a, out var m) && m != null) { length = m.bounds.size.z; return m; }
            m = Build(a);
            _cache[a] = m;
            length = m.bounds.size.z;
            return m;
        }

        // ------------------------------------------------------------------ the game's own model of the round

        static Dictionary<string, GHPC.AarVisual> _rounds;
        static float _roundsAt = -999f;
        static readonly Dictionary<string, GHPC.AarVisual> _found = new Dictionary<string, GHPC.AarVisual>();

        /// Looked up ahead (Warmup, for the round you have loaded): the search below is ~30 ms.
        public static void Prewarm(AmmoType a) { if (a != null) FindRound(a); }

        /// GHPC's x-ray model of this round (what its after-action replay draws in the racks), found by designation
        /// ("M774" for "M774 APFSDS-T"). Remembered per round; the scene is searched again only for a round not seen yet.
        static GHPC.AarVisual FindRound(AmmoType a)
        {
            string key = a.Name ?? "";
            if (_found.TryGetValue(key, out var known))
            {
                if (known != null) return known;                                                              // still there
                if (ReferenceEquals(known, null) && Time.realtimeSinceStartup - _roundsAt < 30f) return null;   // none for it, looked lately
            }
            var av = Search(a);
            _found[key] = av;
            return av;
        }

        static GHPC.AarVisual Search(AmmoType a)
        {
            bool stale = _rounds != null && _rounds.Values.Any(v => v == null);
            if (_rounds == null || stale || Time.realtimeSinceStartup - _roundsAt > 30f)
            {
                _rounds = new Dictionary<string, GHPC.AarVisual>(System.StringComparer.OrdinalIgnoreCase);
                _roundsAt = Time.realtimeSinceStartup;
                foreach (var av in Resources.FindObjectsOfTypeAll<GHPC.AarVisual>())
                {
                    if (av == null || av.RenderMode != GHPC.AarVisual.AarVisualRenderMode.XrayOnly) continue;
                    string k = av.name.Trim();
                    int paren = k.IndexOf(" (");
                    if (paren > 0) k = k.Substring(0, paren);
                    if (k.Length >= 3 && !_rounds.ContainsKey(k)) _rounds[k] = av;
                }
            }
            string n = (a.Name ?? "").Trim();
            if (n.Length == 0) return null;
            string tok = n.Split(' ')[0];
            if (_rounds.TryGetValue(tok, out var hit)) return hit;
            foreach (var kv in _rounds) if (n.StartsWith(kv.Key, System.StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }

        /// The flying round from the game's own model: the projectile parts only (the case stays in the gun, sabot
        /// petals and obturator fall away at the muzzle), turned so its nose is at the origin pointing +Z.
        public static bool GameModel(AmmoType a, Transform parent, out float length)
        {
            length = 0f;
            if (a == null) return false;
            var av = FindRound(a);
            if (av == null) return false;
            Matrix4x4 inv = av.transform.worldToLocalMatrix;
            var parts = new List<(Mesh m, Matrix4x4 rel, string name)>();
            Vector3 caseC = Vector3.zero;
            bool hasCase = false;
            foreach (var r in av.GetComponentsInChildren<Renderer>(true))
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                string rn = r.name.ToLowerInvariant();
                var rel = inv * r.transform.localToWorldMatrix;
                if (rn.Contains("case") || rn.Contains("charge") || rn.Contains("propellant") || rn.Contains("cartridge")) { caseC = rel.MultiplyPoint3x4(mf.sharedMesh.bounds.center); hasCase = true; continue; }
                if (rn.Contains("petal") || rn.Contains("sabot") || rn.Contains("obturator")) continue;
                parts.Add((mf.sharedMesh, rel, r.name));
            }
            if (parts.Count == 0) return false;
            // the round lies along one of its model's axes: the longest side of its bounds; the nose is the end away from the case
            Bounds b = default;
            bool first = true;
            foreach (var (m, rel, _) in parts)
            {
                var mb = m.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1));
                    Vector3 p = rel.MultiplyPoint3x4(c);
                    if (first) { b = new Bounds(p, Vector3.zero); first = false; } else b.Encapsulate(p);
                }
            }
            Vector3 sz = b.size;
            int ax = sz.x >= sz.y && sz.x >= sz.z ? 0 : sz.y >= sz.z ? 1 : 2;
            Vector3 axis = ax == 0 ? Vector3.right : ax == 1 ? Vector3.up : Vector3.forward;
            float L = sz[ax];
            if (L < 0.02f || L > 3f) return false;
            float sign = hasCase && Vector3.Dot(b.center - caseC, axis) < 0f ? -1f : 1f;
            Vector3 nose = b.center + axis * sign * (0.5f * L);
            Quaternion R = Quaternion.FromToRotation(axis * sign, Vector3.forward);
            Matrix4x4 toShell = Matrix4x4.TRS(-(R * nose), R, Vector3.one);
            bool rod = a.Category == AmmoType.AmmoCategory.Penetrator && a.Radius * 2000f < Mathf.Max(a.Caliber, 1f) * 0.6f;
            foreach (var (m, rel, name) in parts)
            {
                var go = Vis.Child(parent, name, m, toShell * rel, Vis.Solid);
                string ln = name.ToLowerInvariant();
                Color c = rod || ln.Contains("dart") || ln.Contains("rod") ? Tungsten
                        : a.Category == AmmoType.AmmoCategory.Explosive ? Olive
                        : a.Category == AmmoType.AmmoCategory.ShapedCharge ? new Color(0.22f, 0.24f, 0.2f)
                        : Black;
                Vis.SetColor(go.GetComponent<Renderer>(), c);
            }
            length = L;
            return true;
        }

        struct Ring { public float Z, R; public Color C; public Ring(float z, float r, Color c) { Z = z; R = r; C = c; } }

        static readonly Color Tungsten = new Color(0.30f, 0.31f, 0.33f), Steel = new Color(0.55f, 0.56f, 0.58f), Alu = new Color(0.68f, 0.69f, 0.72f),
                              Black = new Color(0.10f, 0.10f, 0.11f), Brass = new Color(0.78f, 0.58f, 0.26f), Olive = new Color(0.27f, 0.30f, 0.19f),
                              RedTip = new Color(0.55f, 0.08f, 0.06f), Fuze = new Color(0.62f, 0.52f, 0.28f), Bomb = new Color(0.33f, 0.36f, 0.26f);

        static Mesh Build(AmmoType a)
        {
            float cal = Mathf.Max(a.Caliber, a.Radius * 2000f) * 0.001f;
            float d = Mathf.Max(a.Radius * 2f, 0.002f);
            var rings = new List<Ring>();
            var fins = new List<(float z0, float z1, float r0, float span, float sweep, int n, Color c)>();
            bool rod = a.Category == AmmoType.AmmoCategory.Penetrator && d < cal * 0.6f;
            bool bomb = a.Category == AmmoType.AmmoCategory.Explosive && a.MuzzleVelocity < 150f;
            bool missile = a.Guidance != 0 || (a.Category == AmmoType.AmmoCategory.ShapedCharge && a.MuzzleVelocity < 400f);
            if (cal < 0.001f) cal = 0.1f;
            if (rod)
            {
                // in flight only the rod is left (the sabot petals fall away at the muzzle); ~75 % of the listed mass
                float L = Mathf.Clamp(0.75f * Mathf.Max(a.Mass, 0.1f) / (17600f * Mathf.PI * 0.25f * d * d), 14f * d, 32f * d);
                float r = 0.5f * d;
                rings.Add(new Ring(-L, 0.25f * d, Tungsten));
                rings.Add(new Ring(-L + 0.02f * L, r, Tungsten));
                rings.Add(new Ring(-0.12f * L, r, Tungsten));
                rings.Add(new Ring(-0.05f * L, 0.32f * d, Steel));
                rings.Add(new Ring(0f, 0.04f * d, Steel));
                fins.Add((-L, -L + 3f * d, r, 1.1f * d, 1.4f * d, 6, Alu));
            }
            else if (missile)
            {
                float L = 9f * cal, r = 0.5f * cal;
                rings.Add(new Ring(-L, 0.35f * r, Black));
                rings.Add(new Ring(-L + 0.4f * cal, r, Olive));
                rings.Add(new Ring(-1.6f * cal, r, Olive));
                rings.Add(new Ring(-0.5f * cal, 0.55f * r, Olive));
                rings.Add(new Ring(0f, 0.15f * r, Steel));
                fins.Add((-L + 0.2f * cal, -L + 2.2f * cal, r, 1.1f * cal, 0.8f * cal, 4, Olive));
            }
            else if (bomb)
            {
                float D = Mathf.Max(cal, 0.27f * Mathf.Pow(Mathf.Max(a.Mass, 50f) / 250f, 1f / 3f)), r = 0.5f * D, L = 7f * D;
                rings.Add(new Ring(-L, 0.2f * r, Bomb));
                rings.Add(new Ring(-L + 1.6f * D, r, Bomb));
                rings.Add(new Ring(-1.2f * D, r, Bomb));
                rings.Add(new Ring(-0.4f * D, 0.6f * r, Bomb));
                rings.Add(new Ring(0f, 0.1f * r, Steel));
                fins.Add((-L, -L + 1.4f * D, 0.25f * r, 0.85f * D, 0.4f * D, 4, Bomb));
            }
            else if (a.Category == AmmoType.AmmoCategory.ShapedCharge)
            {
                float r = 0.5f * cal;
                rings.Add(new Ring(-5.2f * cal, 0.16f * cal, Black));
                rings.Add(new Ring(-3.7f * cal, 0.18f * cal, Black));
                rings.Add(new Ring(-3.5f * cal, r, Olive));
                rings.Add(new Ring(-1.9f * cal, r, Olive));
                rings.Add(new Ring(-0.85f * cal, 0.13f * cal, Olive));
                rings.Add(new Ring(-0.8f * cal, 0.065f * cal, Steel));
                rings.Add(new Ring(-0.06f * cal, 0.065f * cal, Steel));
                rings.Add(new Ring(0f, 0.1f * cal, Steel));
                fins.Add((-5.2f * cal, -4.3f * cal, 0.18f * cal, 0.45f * cal, 0.25f * cal, 6, Black));
            }
            else if (a.Category == AmmoType.AmmoCategory.Explosive)
            {
                float r = 0.5f * cal, L = 4.4f * cal;
                rings.Add(new Ring(-L, 0.42f * cal, Olive));
                rings.Add(new Ring(-L + 0.4f * cal, r, Olive));
                rings.Add(new Ring(-L + 0.55f * cal, r, Brass));
                rings.Add(new Ring(-L + 0.75f * cal, 0.515f * cal, Brass));
                rings.Add(new Ring(-L + 0.8f * cal, r, Olive));
                rings.Add(new Ring(-2.2f * cal, r, Olive));
                rings.Add(new Ring(-0.75f * cal, 0.27f * cal, Olive));
                rings.Add(new Ring(-0.4f * cal, 0.15f * cal, Fuze));
                rings.Add(new Ring(0f, 0.03f * cal, Fuze));
                if (cal >= 0.114f && cal <= 0.126f) fins.Add((-L, -L + 0.9f * cal, 0.4f * cal, 0.4f * cal, 0.15f * cal, 6, Olive));
            }
            else
            {
                // full-bore AP / APHE / small arms
                float r = 0.5f * cal, L = (cal < 0.02f ? 4f : 3.6f) * cal;
                Color body = cal < 0.02f ? Brass : Black;
                rings.Add(new Ring(-L, 0.42f * cal, body));
                rings.Add(new Ring(-L + 0.35f * cal, r, body));
                if (cal >= 0.02f)
                {
                    rings.Add(new Ring(-L + 0.55f * cal, r, Brass));
                    rings.Add(new Ring(-L + 0.75f * cal, 0.515f * cal, Brass));
                    rings.Add(new Ring(-L + 0.8f * cal, r, body));
                }
                rings.Add(new Ring(-1.7f * cal, r, body));
                rings.Add(new Ring(-0.6f * cal, 0.33f * cal, body));
                rings.Add(new Ring(-0.25f * cal, 0.14f * cal, cal < 0.02f ? Brass : RedTip));
                rings.Add(new Ring(0f, 0.02f * cal, cal < 0.02f ? Brass : RedTip));
            }
            return Lathe(rings, fins, a.Name ?? "shell");
        }

        static readonly Vector3 Light = new Vector3(0.45f, 0.8f, 0.35f).normalized;

        static Mesh Lathe(List<Ring> rings, List<(float z0, float z1, float r0, float span, float sweep, int n, Color c)> fins, string name)
        {
            const int N = 20;
            var v = new List<Vector3>();
            var col = new List<Color>();
            var tri = new List<int>();
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d2, Color cl)
            {
                Vector3 n = Vector3.Cross(b - a, c - a);
                float sh = 0.5f + 0.5f * Mathf.Abs(Vector3.Dot(n.sqrMagnitude > 1e-12f ? n.normalized : Vector3.up, Light));
                var cc = new Color(cl.r * sh, cl.g * sh, cl.b * sh, 1f);
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(c); v.Add(d2);
                col.Add(cc); col.Add(cc); col.Add(cc); col.Add(cc);
                tri.Add(i); tri.Add(i + 1); tri.Add(i + 2);
                tri.Add(i); tri.Add(i + 2); tri.Add(i + 3);
            }
            Vector3 P(float z, float r, int k) { float t = k * Mathf.PI * 2f / N; return new Vector3(Mathf.Cos(t) * r, Mathf.Sin(t) * r, z); }
            for (int i = 0; i + 1 < rings.Count; i++)
            {
                Ring a = rings[i], b = rings[i + 1];
                for (int k = 0; k < N; k++)
                    Quad(P(a.Z, a.R, k), P(b.Z, b.R, k), P(b.Z, b.R, k + 1), P(a.Z, a.R, k + 1), b.C);
            }
            // the base disc
            var r0 = rings[0];
            for (int k = 0; k < N; k++) Quad(new Vector3(0f, 0f, r0.Z), P(r0.Z, r0.R, k + 1), P(r0.Z, r0.R, k), new Vector3(0f, 0f, r0.Z), r0.C);
            foreach (var f in fins)
                for (int j = 0; j < f.n; j++)
                {
                    float t = (j + 0.5f) * Mathf.PI * 2f / f.n;
                    Vector3 e = new Vector3(Mathf.Cos(t), Mathf.Sin(t), 0f);
                    // root chord along the body, tip chord shorter and swept back
                    Vector3 a = e * f.r0 + Vector3.forward * f.z0, b = e * f.r0 + Vector3.forward * f.z1;
                    Vector3 c = e * (f.r0 + f.span) + Vector3.forward * Mathf.Max(f.z0, f.z1 - f.sweep);
                    Vector3 d2 = e * (f.r0 + f.span) + Vector3.forward * f.z0;
                    Quad(a, b, c, d2, f.c);
                }
            var m = new Mesh { name = "projectile " + name };
            m.SetVertices(v);
            m.SetColors(col);
            m.SetTriangles(tri, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
