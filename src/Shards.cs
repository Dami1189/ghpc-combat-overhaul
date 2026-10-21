using UnityEngine;

namespace GHPCHitCam
{
    /// Steel splinters: a few irregular, faceted, elongated shapes (unit size, long axis +Z). Each face carries a fixed
    /// shade in its vertex colour so the facets read as metal under the unlit colour shader as they tumble.
    internal static class Shards
    {
        static Mesh[] _variants;
        public const int Variants = 6;

        public static Mesh Get(int i)
        {
            if (_variants == null)
            {
                _variants = new Mesh[Variants];
                var rnd = new System.Random(77);
                for (int k = 0; k < Variants; k++) _variants[k] = Make(rnd);
            }
            return _variants[((i % Variants) + Variants) % Variants];
        }

        static Mesh Make(System.Random rnd)
        {
            float R(float a, float b) => a + (float)rnd.NextDouble() * (b - a);
            // a flattened, elongated, slightly bent splinter: tip, tail and four points around the waist
            Vector3[] p =
            {
                new Vector3(R(-0.12f, 0.12f), R(-0.05f, 0.05f), R(0.42f, 0.6f)),
                new Vector3(R(-0.12f, 0.12f), R(-0.05f, 0.05f), R(-0.5f, -0.28f)),
                new Vector3(R(0.22f, 0.42f), R(-0.08f, 0.08f), R(-0.12f, 0.18f)),
                new Vector3(R(-0.42f, -0.2f), R(-0.08f, 0.08f), R(-0.18f, 0.12f)),
                new Vector3(R(-0.1f, 0.1f), R(0.07f, 0.16f), R(-0.12f, 0.12f)),
                new Vector3(R(-0.1f, 0.1f), R(-0.16f, -0.07f), R(-0.12f, 0.12f)),
            };
            int[] f = { 0, 2, 4, 0, 4, 3, 0, 3, 5, 0, 5, 2, 1, 4, 2, 1, 3, 4, 1, 5, 3, 1, 2, 5 };
            var v = new Vector3[f.Length];
            var c = new Color[f.Length];
            var t = new int[f.Length];
            Vector3 light = new Vector3(0.4f, 0.8f, -0.45f).normalized;
            for (int i = 0; i < f.Length; i += 3)
            {
                Vector3 a = p[f[i]], b = p[f[i + 1]], cc = p[f[i + 2]];
                Vector3 n = Vector3.Cross(b - a, cc - a).normalized;
                float sh = 0.5f + 0.5f * Mathf.Abs(Vector3.Dot(n, light));
                for (int j = 0; j < 3; j++) { v[i + j] = p[f[i + j]]; c[i + j] = new Color(sh, sh, sh, 1f); t[i + j] = i + j; }
            }
            var m = new Mesh { name = "shard", vertices = v, colors = c, triangles = t };
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// Steel by temperature: 0 cold grey, ~0.33 dull red, ~0.66 orange, 1 white hot.
        public static Color Hot(float heat)
        {
            var cold = new Color(0.36f, 0.37f, 0.40f);
            if (heat <= 0f) return cold;
            var dull = new Color(0.6f, 0.1f, 0.04f);
            var orange = new Color(1f, 0.45f, 0.1f);
            var white = new Color(1f, 0.93f, 0.75f);
            if (heat < 0.33f) return Color.Lerp(cold, dull, heat / 0.33f);
            if (heat < 0.66f) return Color.Lerp(dull, orange, (heat - 0.33f) / 0.33f);
            return Color.Lerp(orange, white, Mathf.Clamp01((heat - 0.66f) / 0.34f));
        }
    }
}
