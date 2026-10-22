using System.Collections.Generic;
using UnityEngine;

namespace GHPCHitCam
{
    /// fire and explosions in the x-ray, readable at a glance like War Thunder's: all additive light (or dark alpha
    /// smoke) over the black hull, sized from the physics (fireball ~ charge^(1/3), flames from what burns)
    internal static class XrayFxMeshes
    {
        static Mesh _tongue, _ring, _puff;

        /// soft round puff for alpha blending (white, opaque in the middle, clear at the rim; tint dark for smoke), diameter 1, XY plane
        public static Mesh Puff
        {
            get
            {
                if (_puff != null) return _puff;
                const int n = 28;
                var v = new List<Vector3> { Vector3.zero };
                var c = new List<Color> { Color.white };
                var t = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    float a = i * Mathf.PI * 2f / n;
                    var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    v.Add(d * 0.22f); c.Add(new Color(1f, 1f, 1f, 0.75f));
                    v.Add(d * 0.5f); c.Add(new Color(1f, 1f, 1f, 0f));
                }
                for (int i = 0; i < n; i++)
                {
                    int a0 = 1 + i * 2, b0 = 1 + ((i + 1) % n) * 2;
                    t.Add(0); t.Add(a0); t.Add(b0);
                    t.Add(a0); t.Add(a0 + 1); t.Add(b0); t.Add(b0); t.Add(a0 + 1); t.Add(b0 + 1);
                }
                _puff = new Mesh { name = "smoke puff" };
                _puff.SetVertices(v);
                _puff.SetColors(c);
                _puff.SetTriangles(t, 0);
                _puff.RecalculateBounds();
                return _puff;
            }
        }

        /// flame tongue: vertical teardrop strip, white-yellow at the base through orange to a dark red tip, black at the
        /// sides (soft edges when drawn additive). unit height, base at origin
        public static Mesh Tongue
        {
            get
            {
                if (_tongue != null) return _tongue;
                const int rows = 12;
                var v = new List<Vector3>();
                var c = new List<Color>();
                var t = new List<int>();
                for (int i = 0; i <= rows; i++)
                {
                    float y = (float)i / rows;
                    float w = 0.5f * Mathf.Sin(Mathf.PI * Mathf.Pow(y, 0.55f)) * Mathf.Pow(1f - y, 0.25f) + 0.02f;
                    Color mid = y < 0.25f ? Color.Lerp(new Color(1f, 0.97f, 0.8f), new Color(1f, 0.75f, 0.3f), y / 0.25f)
                              : y < 0.6f ? Color.Lerp(new Color(1f, 0.75f, 0.3f), new Color(1f, 0.38f, 0.07f), (y - 0.25f) / 0.35f)
                              : Color.Lerp(new Color(1f, 0.38f, 0.07f), new Color(0.35f, 0.04f, 0.01f), (y - 0.6f) / 0.4f);
                    v.Add(new Vector3(-w, y, 0f)); c.Add(Color.black);
                    v.Add(new Vector3(0f, y, 0f)); c.Add(mid);
                    v.Add(new Vector3(w, y, 0f)); c.Add(Color.black);
                    if (i < rows)
                    {
                        int a = i * 3;
                        t.Add(a); t.Add(a + 3); t.Add(a + 1); t.Add(a + 1); t.Add(a + 3); t.Add(a + 4);
                        t.Add(a + 1); t.Add(a + 4); t.Add(a + 2); t.Add(a + 2); t.Add(a + 4); t.Add(a + 5);
                    }
                }
                _tongue = new Mesh { name = "flame tongue" };
                _tongue.SetVertices(v);
                _tongue.SetColors(c);
                _tongue.SetTriangles(t, 0);
                _tongue.RecalculateBounds();
                return _tongue;
            }
        }

        /// thin bright ring (dark inside and out), unit radius in XY plane: the shock front seen face-on
        public static Mesh Ring
        {
            get
            {
                if (_ring != null) return _ring;
                const int n = 64;
                var v = new List<Vector3>();
                var c = new List<Color>();
                var t = new List<int>();
                float[] r = { 0.86f, 0.97f, 1f, 1.03f };
                Color[] col = { Color.black, Color.white, Color.white, Color.black };
                for (int i = 0; i <= n; i++)
                {
                    float a = i * Mathf.PI * 2f / n;
                    var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    for (int k = 0; k < 4; k++) { v.Add(d * r[k]); c.Add(col[k]); }
                    if (i < n)
                        for (int k = 0; k < 3; k++)
                        {
                            int a0 = i * 4 + k, b0 = a0 + 4;
                            t.Add(a0); t.Add(b0); t.Add(a0 + 1); t.Add(a0 + 1); t.Add(b0); t.Add(b0 + 1);
                        }
                }
                _ring = new Mesh { name = "shock ring" };
                _ring.SetVertices(v);
                _ring.SetColors(c);
                _ring.SetTriangles(t, 0);
                _ring.RecalculateBounds();
                return _ring;
            }
        }
    }

    /// camera-facing particle cloud in one dynamic mesh: sparks / embers as short streaks along their motion
    internal class Sparks
    {
        public struct P { public Vector3 P0, V; public float T0, Life, Size; public Color C; }
        readonly List<P> _p = new List<P>();
        readonly Mesh _mesh;
        Vector3[] _v;
        Color[] _c;
        readonly Renderer _r;
        public float Gravity = -2.5f, Drag = 1.6f;

        public Sparks(Transform parent, int capacity)
        {
            _mesh = new Mesh { name = "sparks" };
            _mesh.MarkDynamic();
            _v = new Vector3[capacity * 4];
            _c = new Color[capacity * 4];
            var t = new int[capacity * 6];
            for (int i = 0; i < capacity; i++)
            {
                t[i * 6] = i * 4; t[i * 6 + 1] = i * 4 + 1; t[i * 6 + 2] = i * 4 + 2;
                t[i * 6 + 3] = i * 4; t[i * 6 + 4] = i * 4 + 2; t[i * 6 + 5] = i * 4 + 3;
            }
            _mesh.vertices = _v;
            _mesh.colors = _c;
            _mesh.triangles = t;
            _r = Vis.Child(parent, "sparks", _mesh, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
        }

        public int Capacity => _v.Length / 4;
        public void Add(P p) { if (_p.Count < Capacity) _p.Add(p); else { _p[Random.Range(0, _p.Count)] = p; } }

        /// t = session time; cam = camera pos in the parent's space
        public void Tick(float t, Vector3 cam)
        {
            int n = _p.Count;
            for (int i = 0; i < Capacity; i++)
            {
                int b = i * 4;
                if (i >= n) { _v[b] = _v[b + 1] = _v[b + 2] = _v[b + 3] = Vector3.zero; continue; }
                var p = _p[i];
                float age = t - p.T0;
                if (age < 0f || age > p.Life) { _v[b] = _v[b + 1] = _v[b + 2] = _v[b + 3] = Vector3.zero; continue; }
                // drag-limited flight under gravity
                float f = (1f - Mathf.Exp(-Drag * age)) / Drag;
                Vector3 pos = p.P0 + p.V * f + Vector3.up * (0.5f * Gravity * age * age);
                Vector3 vel = p.V * Mathf.Exp(-Drag * age) + Vector3.up * (Gravity * age);
                Vector3 dir = vel.sqrMagnitude > 1e-4f ? vel.normalized : Vector3.up;
                Vector3 view = (cam - pos).normalized;
                Vector3 side = Vector3.Cross(dir, view);
                side = side.sqrMagnitude > 1e-6f ? side.normalized * p.Size : Vector3.right * p.Size;
                float len = Mathf.Clamp(vel.magnitude * 0.03f, p.Size * 2f, 0.4f);
                _v[b] = pos - side; _v[b + 1] = pos + side; _v[b + 2] = pos + side - dir * len; _v[b + 3] = pos - side - dir * len;
                float k = 1f - age / p.Life;
                Color hot = Color.Lerp(new Color(0.8f, 0.12f, 0.02f), p.C, k * k);
                _c[b] = _c[b + 1] = hot * (k * k);
                _c[b + 2] = _c[b + 3] = hot * (0.1f * k * k);
            }
            _mesh.vertices = _v;
            _mesh.colors = _c;
            _mesh.RecalculateBounds();
        }

        public void Clear() { _p.Clear(); }
        public void Dispose() { if (_mesh != null) Object.Destroy(_mesh); }
    }

    /// something burning: flame tongues licking up from a hot base glow, smoke column, rising embers.
    /// propellant burns as a fierce jet: taller, whiter, faster, more of everything
    internal class XrayFire
    {
        public Vector3 L;
        readonly Transform _t;
        readonly float _start, _size;
        readonly bool _jet;
        readonly Renderer _glow, _core;
        readonly Renderer[] _tongues;
        readonly float[] _phase, _len;
        readonly Renderer[] _smoke;
        readonly Sparks _embers;
        float _nextEmber;
        readonly System.Random _rnd;

        public float Start => _start;
        /// fire on something that moves (burning crewman)
        public void MoveTo(Vector3 l) { L = l; _t.localPosition = l; }

        public XrayFire(Transform parent, Vector3 l, float size, bool jet, float start, int seed)
        {
            L = l; _size = size; _jet = jet; _start = start;
            _rnd = new System.Random(seed);
            var go = new GameObject(jet ? "propellant fire" : "fire");
            go.layer = Vis.Layer;
            _t = go.transform;
            _t.SetParent(parent, false);
            _t.localPosition = l;
            _glow = Vis.Child(_t, "base glow", Vis.SoftDisc, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
            _core = Vis.Child(_t, "core", Vis.SoftDisc, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
            int n = jet ? 11 : 7;
            _tongues = new Renderer[n];
            _phase = new float[n];
            _len = new float[n];
            for (int i = 0; i < n; i++)
            {
                _tongues[i] = Vis.Child(_t, "tongue", XrayFxMeshes.Tongue, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
                _phase[i] = (float)_rnd.NextDouble() * 10f;
                _len[i] = 0.6f + 0.8f * (float)_rnd.NextDouble();
            }
            _smoke = new Renderer[jet ? 8 : 6];
            for (int i = 0; i < _smoke.Length; i++) _smoke[i] = Vis.Child(_t, "smoke", XrayFxMeshes.Puff, Matrix4x4.identity, Vis.Alpha).GetComponent<Renderer>();
            _embers = new Sparks(_t, jet ? 60 : 30) { Gravity = 1.2f, Drag = 1.2f };   // embers rise in the hot plume
        }

        float R() => (float)_rnd.NextDouble();

        public void Tick(float now, Vector3 camWorld)
        {
            float age = now - _start;
            float grow = Mathf.Clamp01(age / (_jet ? 0.15f : 0.35f));
            Vector3 cam = _t.InverseTransformPoint(camWorld);
            Vector3 flat = new Vector3(cam.x, 0f, cam.z);
            Quaternion yaw = flat.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(-flat.normalized, Vector3.up) : Quaternion.identity;
            Quaternion face = Quaternion.LookRotation(-cam.normalized, Vector3.up);
            float s = _size * grow;
            // hot base and its white core, pulsing
            float pulse = 0.85f + 0.15f * Mathf.Sin(now * 17f) + 0.08f * Mathf.Sin(now * 41f);
            _glow.transform.localRotation = face;
            _glow.transform.localScale = Vector3.one * s * (_jet ? 2.6f : 2.1f) * pulse;
            Vis.SetColor(_glow, new Color(1f, 0.42f, 0.08f) * 0.55f * pulse);
            _core.transform.localRotation = face;
            _core.transform.localScale = Vector3.one * s * (_jet ? 1.1f : 0.7f) * pulse;
            Vis.SetColor(_core, new Color(1f, 0.92f, 0.7f) * (_jet ? 1f : 0.7f));
            // tongues: each rises, stretches, detaches and dies on its own cycle (flames lick, they don't scale)
            float speed = _jet ? 2.6f : 1.5f;
            for (int i = 0; i < _tongues.Length; i++)
            {
                float cyc = now * speed + _phase[i];
                float u = cyc - Mathf.Floor(cyc);                    // 0..1 through this tongue's life
                float a = _phase[i] * 2.4f;
                float ring = s * (_jet ? 0.22f : 0.3f) * (0.4f + 0.6f * (i % 3) / 2f);
                float h = s * _len[i] * (_jet ? 1.8f : 1.4f) * (0.5f + 0.65f * Mathf.Sin(Mathf.PI * u)) * (0.85f + 0.3f * Mathf.PerlinNoise(now * 4f, i * 1.7f));
                float w = s * (_jet ? 0.75f : 0.75f) * (1f - 0.45f * u);
                float lift = s * 0.6f * Mathf.Max(0f, u - 0.6f) * (_jet ? 2f : 1f);
                float sway = Mathf.Sin(now * 3.1f + _phase[i]) * 0.12f * s;
                var tr = _tongues[i].transform;
                tr.localPosition = new Vector3(Mathf.Cos(a) * ring + sway, lift, Mathf.Sin(a) * ring);
                tr.localRotation = yaw * Quaternion.Euler(0f, 0f, Mathf.Sin(now * 2.3f + _phase[i]) * (_jet ? 7f : 10f) + (i % 2 == 0 ? 1f : -1f) * 6f * (i % 3));
                tr.localScale = new Vector3(w, h, 1f);
                float fade = Mathf.Sin(Mathf.PI * Mathf.Clamp01(u * 1.15f));
                Vis.SetColor(_tongues[i], Color.white * (0.55f + 0.45f * fade) * (_jet ? 1f : 0.85f) * grow);
            }
            // dark smoke column rising out of the flames
            for (int i = 0; i < _smoke.Length; i++)
            {
                float cyc = (age * 0.35f + i / (float)_smoke.Length);
                float u = cyc - Mathf.Floor(cyc);
                float up = s * (1.2f + u * 4.5f);
                var tr = _smoke[i].transform;
                tr.localPosition = new Vector3(Mathf.Sin(i * 2.1f + age * 0.7f) * 0.15f * u * s * 3f, up, Mathf.Cos(i * 1.3f) * 0.1f * s);
                tr.localRotation = face;
                tr.localScale = Vector3.one * s * (0.9f + u * 2.6f);
                float a2 = Mathf.Clamp01(u * 4f) * (1f - u) * 0.55f * Mathf.Clamp01(age / 0.8f);
                Vis.SetColor(_smoke[i], new Color(0.05f, 0.045f, 0.04f, a2));
            }
            // embers
            if (now >= _nextEmber)
            {
                _nextEmber = now + (_jet ? 0.02f : 0.06f);
                Vector3 v = new Vector3((R() - 0.5f) * 1.2f, (_jet ? 4f : 2f) + R() * 2f, (R() - 0.5f) * 1.2f) * s;
                _embers.Add(new Sparks.P { P0 = new Vector3((R() - 0.5f) * s * 0.4f, s * 0.2f, (R() - 0.5f) * s * 0.4f), V = v, T0 = now, Life = 0.6f + R() * 0.9f, Size = 0.006f + 0.008f * R(), C = new Color(1f, 0.75f, 0.3f) });
            }
            _embers.Tick(now, cam);
        }

        public void Dispose() => _embers.Dispose();
    }

    /// explosion inside the vehicle: white flash, fireball of many hot puffs growing on the blast's own time scale and
    /// cooling white -> orange -> dull red, shock ring, sparks thrown out, then a lingering glow and smoke filling the space
    internal class XrayExplosion
    {
        readonly Transform _t;
        readonly float _start, _r;
        readonly bool _big;
        readonly Renderer _flash, _glow, _ring;
        readonly Renderer[] _puff, _smoke;
        readonly Vector3[] _pOff;
        readonly float[] _pSize, _pDelay;
        readonly Sparks _sparks;
        public float Start => _start;
        public float Radius => _r;
        public bool Big => _big;

        public XrayExplosion(Transform parent, Vector3 l, float radius, bool big, float start, int seed)
        {
            _start = start; _r = radius; _big = big;
            var rnd = new System.Random(seed);
            float R() => (float)rnd.NextDouble();
            var go = new GameObject(big ? "ammo explosion" : "explosion");
            go.layer = Vis.Layer;
            _t = go.transform;
            _t.SetParent(parent, false);
            _t.localPosition = l;
            _flash = Vis.Child(_t, "flash", Vis.SoftDisc, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
            _glow = Vis.Child(_t, "glow", Vis.SoftDisc, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
            _ring = Vis.Child(_t, "shock", XrayFxMeshes.Ring, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
            int n = big ? 14 : 9;
            _puff = new Renderer[n];
            _pOff = new Vector3[n];
            _pSize = new float[n];
            _pDelay = new float[n];
            for (int i = 0; i < n; i++)
            {
                _puff[i] = Vis.Child(_t, "fireball", Vis.SoftDisc, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
                _pOff[i] = new Vector3(R() - 0.5f, R() - 0.4f, R() - 0.5f).normalized * radius * (0.15f + 0.45f * R());
                _pSize[i] = radius * (0.55f + 0.6f * R());
                _pDelay[i] = R() * (big ? 0.12f : 0.06f);
            }
            _smoke = new Renderer[big ? 8 : 5];
            for (int i = 0; i < _smoke.Length; i++) _smoke[i] = Vis.Child(_t, "smoke", XrayFxMeshes.Puff, Matrix4x4.identity, Vis.Alpha).GetComponent<Renderer>();
            _sparks = new Sparks(_t, big ? 200 : 60) { Gravity = -3f, Drag = 1.8f };
            int ns = big ? 110 : 50;
            for (int i = 0; i < ns; i++)
            {
                Vector3 d = new Vector3(R() - 0.5f, R() - 0.35f, R() - 0.5f).normalized;
                _sparks.Add(new Sparks.P { P0 = d * radius * 0.1f, V = d * radius * (3f + 7f * R()), T0 = start + R() * 0.1f, Life = 0.4f + 0.7f * R(), Size = 0.004f + 0.006f * R(), C = new Color(1f, 0.85f, 0.45f) });
            }
            if (big)
                for (int i = 0; i < 70; i++)
                {
                    // burning propellant grains and bits of the rack thrown far out, falling as they burn out
                    Vector3 d = new Vector3(R() - 0.5f, R() * 0.9f - 0.2f, R() - 0.5f).normalized;
                    _sparks.Add(new Sparks.P { P0 = d * radius * 0.15f, V = d * radius * (6f + 8f * R()), T0 = start + 0.02f + R() * 0.25f, Life = 1f + 1.2f * R(), Size = 0.006f + 0.008f * R(), C = new Color(1f, 0.7f, 0.3f) });
                }
        }

        static float Ease(float x) { x = Mathf.Clamp01(x); return 1f - (1f - x) * (1f - x) * (1f - x); }

        /// full-frame flash the UI draws (0..1)
        public float Flash(float now) { float a = now - _start; return a < 0f ? 0f : Mathf.Clamp01(1f - a / (_big ? 0.5f : 0.3f)); }

        public bool Done(float now) => now - _start > (_big ? 6f : 3.5f);

        public void Tick(float now, Vector3 camWorld)
        {
            float age = now - _start;
            Vector3 cam = _t.InverseTransformPoint(camWorld);
            Quaternion face = Quaternion.LookRotation(-cam.normalized, Vector3.up);
            // 1. white flash
            float fl = Mathf.Clamp01(1f - age / 0.15f);
            fl *= fl;
            _flash.enabled = fl > 0.01f && age >= 0f;
            _flash.transform.localRotation = face;
            _flash.transform.localScale = Vector3.one * _r * 1.7f;
            Vis.SetColor(_flash, new Color(1f, 0.97f, 0.9f) * 0.8f * fl);
            // 2. fireball: hot puffs grow fast, then cool and fade, hottest light in the middle
            float grow = _big ? 0.45f : 0.3f, life = _big ? 1.6f : 1.0f;
            for (int i = 0; i < _puff.Length; i++)
            {
                float a = age - _pDelay[i];
                var r = _puff[i];
                if (a < 0f || a > life) { r.enabled = false; continue; }
                r.enabled = true;
                float k = Ease(a / grow);
                float cool = a / life;
                Color c = cool < 0.12f ? Color.Lerp(new Color(1f, 0.97f, 0.85f), new Color(1f, 0.7f, 0.25f), cool / 0.12f)
                        : cool < 0.5f ? Color.Lerp(new Color(1f, 0.7f, 0.25f), new Color(0.95f, 0.3f, 0.05f), (cool - 0.12f) / 0.38f)
                        : Color.Lerp(new Color(0.95f, 0.3f, 0.05f), new Color(0.25f, 0.03f, 0.01f), (cool - 0.5f) / 0.5f);
                float flick = 0.85f + 0.15f * Mathf.Sin(now * 23f + i * 1.9f);
                r.transform.localPosition = _pOff[i] * (0.6f + 0.8f * k) + Vector3.up * (0.25f * _r * cool * cool);   // buoyant
                r.transform.localRotation = face;
                r.transform.localScale = Vector3.one * _pSize[i] * (0.15f + 1.25f * k);
                Vis.SetColor(r, c * (1f - cool * cool) * 0.6f * flick);
            }
            // 3. shock front: fast, thin, gone in a quarter second
            float sk = age / 0.28f;
            _ring.enabled = sk >= 0f && sk < 1f;
            if (_ring.enabled)
            {
                _ring.transform.localRotation = face;
                _ring.transform.localScale = Vector3.one * _r * (0.3f + 2.4f * Ease(sk));
                Vis.SetColor(_ring, new Color(0.9f, 0.95f, 1f) * (1f - sk) * 0.8f);
            }
            // 4. glow left behind (hot gas, burning residue)
            float g = age < 0f ? 0f : Mathf.Clamp01(age / 0.2f) * Mathf.Clamp01(1f - (age - 0.6f) / (_big ? 3.5f : 2f));
            _glow.enabled = g > 0.01f;
            _glow.transform.localRotation = face;
            _glow.transform.localScale = Vector3.one * _r * 1.6f * (0.9f + 0.1f * Mathf.Sin(now * 9f));
            Vis.SetColor(_glow, new Color(0.9f, 0.22f, 0.04f) * 0.45f * g);
            // 5. smoke filling the space
            for (int i = 0; i < _smoke.Length; i++)
            {
                float a = age - 0.25f - i * 0.12f;
                var r = _smoke[i];
                if (a < 0f) { r.enabled = false; continue; }
                r.enabled = true;
                float u = Mathf.Clamp01(a / (_big ? 4.5f : 3f));
                float ang = i * 2.39f;
                r.transform.localPosition = new Vector3(Mathf.Cos(ang), 0.3f + u * 1.2f, Mathf.Sin(ang)) * _r * (0.3f + 0.6f * u);
                r.transform.localRotation = face;
                r.transform.localScale = Vector3.one * _r * (0.8f + 1.8f * u);
                Vis.SetColor(r, new Color(0.04f, 0.035f, 0.03f, 0.5f * Mathf.Clamp01(a * 3f) * (1f - u)));
            }
            _sparks.Tick(now, cam);
        }

        public void Dispose() => _sparks.Dispose();
    }

    /// shaped-charge jet like the real thing: thin stream of hot copper with a velocity gradient (tip ~8 km/s, tail
    /// ~2 km/s) so it stretches as it flies; after a few tens of us it necks and breaks into a string of particles; a fat
    /// slow slug trails far behind. tip = where the game's jet is, every other part sits where its own speed carried it
    /// from the formation point. white-hot tip, orange body, dull red tail, soft glow around
    internal class JetFx
    {
        const int Segs = 30;
        const float TailRatio = 0.25f, SlugRatio = 0.1f;
        readonly Mesh _mesh;
        readonly Renderer _r, _form, _slug;
        readonly Vector3[] _v = new Vector3[(Segs + 1) * 8];
        readonly Color[] _c = new Color[(Segs + 1) * 8];
        readonly float _w, _breakup;
        float _formAt = -1f;

        public JetFx(Transform parent, float calibre)
        {
            _w = Mathf.Clamp(0.05f * calibre, 0.003f, 0.008f);         // a few mm of copper, drawn a bit thicker
            _breakup = 4e-4f * Mathf.Clamp(calibre / 0.1f, 0.5f, 2f) * 0.1f;   // ~40 us per 100 mm of charge
            _mesh = new Mesh { name = "heat jet" };
            _mesh.MarkDynamic();
            var t = new List<int>();
            for (int i = 0; i <= Segs; i++)
                for (int q = 0; q < 2; q++)
                {
                    int b = i * 8 + q * 4;
                    t.Add(b); t.Add(b + 1); t.Add(b + 2); t.Add(b); t.Add(b + 2); t.Add(b + 3);
                }
            _mesh.vertices = _v;
            _mesh.colors = _c;
            _mesh.SetTriangles(t, 0);
            _r = Vis.Child(parent, "heat jet", _mesh, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
            _form = Vis.Child(parent, "jet formation flash", Vis.SoftDisc, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
            _slug = Vis.Child(parent, "slug", Vis.SoftDisc, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
            _form.enabled = _slug.enabled = false;
        }

        public void Hide() { _r.enabled = _form.enabled = _slug.enabled = false; }

        /// from = formation point, tip = where the jet is now, age = sim s since it formed, now = session time,
        /// fade = 1 while alive, drops to 0 once spent. cam in the parent's space
        public void Update(Vector3 from, Vector3 tip, float age, float now, float fade, Vector3 cam)
        {
            if (_formAt < 0f) _formAt = now;
            Vector3 axis = tip - from;
            float len = axis.magnitude;
            if (len < 1e-4f || fade <= 0.001f) { _r.enabled = false; _slug.enabled = false; }
            else
            {
                _r.enabled = true;
                Vector3 dir = axis / len;
                Vector3 mid = from + axis * 0.6f;
                Vector3 side = Vector3.Cross(dir, (cam - mid).normalized);
                side = side.sqrMagnitude > 1e-6f ? side.normalized : Vector3.up;
                // after break-up the stream is a string of particles with gaps
                float broken = Mathf.Clamp01((age - _breakup) / (2f * _breakup));
                for (int i = 0; i <= Segs; i++)
                {
                    float f0 = (float)i / (Segs + 1), f1 = (float)(i + 1) / (Segs + 1);
                    float shrink = Mathf.Lerp(1f, 0.55f, broken);
                    float fm = 0.5f * (f0 + f1), half = 0.5f * (f1 - f0) * shrink;
                    // each part's distance from the formation point grows with its own speed
                    float dA = len * Mathf.Lerp(TailRatio, 1f, fm - half), dB = len * Mathf.Lerp(TailRatio, 1f, fm + half);
                    Vector3 a = from + dir * dA, b = from + dir * dB;
                    float w = _w * Mathf.Lerp(1.4f, 0.7f, fm);               // tail is thicker
                    Color core = fm > 0.9f ? Color.Lerp(new Color(1f, 0.85f, 0.55f), new Color(1f, 0.98f, 0.95f), (fm - 0.9f) / 0.1f)
                               : Color.Lerp(new Color(0.8f, 0.18f, 0.04f), new Color(1f, 0.7f, 0.25f), fm / 0.9f);
                    int k = i * 8;
                    // bright core
                    _v[k] = a - side * w; _v[k + 1] = a + side * w; _v[k + 2] = b + side * w; _v[k + 3] = b - side * w;
                    Color cc = core * fade;
                    _c[k] = _c[k + 1] = _c[k + 2] = _c[k + 3] = cc;
                    // soft glow around it
                    float g = w * 4f;
                    _v[k + 4] = a - side * g; _v[k + 5] = a + side * g; _v[k + 6] = b + side * g; _v[k + 7] = b - side * g;
                    _c[k + 4] = _c[k + 5] = _c[k + 6] = _c[k + 7] = core * (0.18f * fade);
                }
                _mesh.vertices = _v;
                _mesh.colors = _c;
                _mesh.RecalculateBounds();
                // slug: slow, fat, glowing
                _slug.enabled = true;
                _slug.transform.localPosition = from + dir * (len * SlugRatio);
                _slug.transform.localRotation = Quaternion.LookRotation(_slug.transform.localPosition - cam);
                _slug.transform.localScale = Vector3.one * _w * 9f;
                Vis.SetColor(_slug, new Color(1f, 0.45f, 0.1f) * 0.8f * fade);
            }
            // liner collapsing: white flash where the jet forms
            float fa = now - _formAt;
            float ff = Mathf.Clamp01(1f - fa / 0.18f);
            _form.enabled = ff > 0.01f;
            if (_form.enabled)
            {
                _form.transform.localPosition = from;
                _form.transform.localRotation = Quaternion.LookRotation(from - cam);
                _form.transform.localScale = Vector3.one * Mathf.Lerp(0.08f, 0.45f, 1f - ff * ff);
                Vis.SetColor(_form, new Color(1f, 0.95f, 0.85f) * ff);
            }
        }

        public void Dispose() { if (_mesh != null) Object.Destroy(_mesh); }
    }

    /// burning propellant venting from a broken case or burning charge: a roaring blowtorch. grains burn at a few hundred
    /// bar inside the case so the gas leaves the rupture at hundreds of m/s: white-hot root, yellow and orange further
    /// out, ragged turbulent puffs racing along, burning grains streaming out as sparks, dark smoke rolling off the end.
    /// flares as rack pressure builds, snuffed out when it detonates
    internal class PropellantJet
    {
        public readonly Vector3 L, Dir;
        public readonly float Start, Len;
        readonly Transform _t;
        readonly Renderer _root, _glow, _pop;
        readonly Renderer[] _core, _sheath, _flow, _smoke;
        readonly float[] _ph;
        readonly Vector3[] _wob;
        readonly Sparks _grains;
        readonly System.Random _rnd;
        readonly float _burn;      // s it roars at full strength before the charge starts burning down
        readonly float _wid;       // vent jet's narrowest width (at its hole)
        float _next, _surgeAt = float.MaxValue, _cutAt = float.MaxValue;
        /// vent jet: hot gas pushed out of a hole by hull pressure (not a charge's own case). Push = how much of its full
        /// length the pressure drives it now, width follows that, no case bursting
        public bool Vent;
        public float Push = 1f;
        /// how bright its flame is drawn (1 against the x-ray's dark, the daylit world needs more)
        public float Bright = 1f;

        public PropellantJet(Transform parent, Vector3 l, Vector3 dir, float len, float start, float burn, int seed, float width = 0f)
        {
            L = l; Dir = dir.normalized; Len = len; Start = start; _burn = burn; _wid = width;
            _rnd = new System.Random(seed);
            var go = new GameObject("propellant jet");
            go.layer = Vis.Layer;
            _t = go.transform;
            _t.SetParent(parent, false);
            _t.localPosition = l;
            _glow = Vis.Child(_t, "jet light", Vis.SoftDisc, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
            _root = Vis.Child(_t, "rupture", Vis.SoftDisc, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
            _pop = Vis.Child(_t, "case burst", Vis.SoftDisc, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
            _core = new Renderer[3];
            _sheath = new Renderer[5];
            _ph = new float[16];
            _wob = new Vector3[16];
            for (int i = 0; i < _ph.Length; i++)
            {
                _ph[i] = R();
                _wob[i] = Vector3.ProjectOnPlane(new Vector3(R() - 0.5f, R() - 0.5f, R() - 0.5f), Dir).normalized;
            }
            for (int i = 0; i < _core.Length; i++) _core[i] = Vis.Child(_t, "jet core", XrayFxMeshes.Tongue, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
            for (int i = 0; i < _sheath.Length; i++) _sheath[i] = Vis.Child(_t, "jet flame", XrayFxMeshes.Tongue, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
            _flow = new Renderer[16];
            for (int i = 0; i < _flow.Length; i++) _flow[i] = Vis.Child(_t, "jet puff", Vis.SoftDisc, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
            _smoke = new Renderer[8];
            for (int i = 0; i < _smoke.Length; i++) _smoke[i] = Vis.Child(_t, "jet smoke", XrayFxMeshes.Puff, Matrix4x4.identity, Vis.Alpha).GetComponent<Renderer>();
            _grains = new Sparks(_t, 160) { Gravity = -3f, Drag = 2.2f };
        }

        float R() => (float)_rnd.NextDouble();

        /// rack pressure builds right before it detonates: jet flares longer and brighter
        public void Surge(float at) { if (at < _surgeAt) _surgeAt = at; }
        /// detonation consumes it
        public void Cut(float at) { if (at < _cutAt) _cutAt = at; }
        public bool Cutting => _cutAt < float.MaxValue;

        /// how hard it roars now, 0..~1.5 (camera rumbles with it)
        public float Level(float now)
        {
            float age = now - Start;
            if (age < 0f) return 0f;
            float grow = Mathf.Clamp01(age / 0.22f);
            float pop = Vent ? 0f : 0.7f * Mathf.Exp(-age * 10f);                             // case bursting open
            float surge = now > _surgeAt ? 1f + 0.55f * Mathf.Clamp01((now - _surgeAt) / 0.15f) : 1f;
            float cut = now > _cutAt ? Mathf.Clamp01(1f - (now - _cutAt) / 0.1f) : 1f;
            float down = Mathf.Lerp(1f, 0.45f, Mathf.Clamp01((age - _burn) / 5f));           // charge burns down
            float flick = 0.88f + 0.12f * Mathf.PerlinNoise(now * 11f, _ph[0] * 9f);
            return (grow * flick + pop) * surge * cut * down * Push;
        }

        static Quaternion AxisFace(Vector3 axis, Vector3 pos, Vector3 cam)
        {
            Vector3 to = Vector3.ProjectOnPlane(cam - pos, axis);
            if (to.sqrMagnitude < 1e-6f) to = Vector3.ProjectOnPlane(Vector3.up, axis);
            if (to.sqrMagnitude < 1e-6f) to = Vector3.ProjectOnPlane(Vector3.right, axis);
            return Quaternion.LookRotation(-to.normalized, axis);
        }

        public void Tick(float now, Vector3 camWorld)
        {
            float age = now - Start;
            float lv = Level(now);
            bool on = age >= 0f && lv > 0.01f;
            float lenNow = Len * Mathf.Min(lv, 1.5f);
            // vent jet is as wide as how far it reaches now (torch out of a hole), a charge's own jet keeps its size
            float W = Vent ? Mathf.Max(lenNow * 0.55f, _wid) : Len;
            // brightness: vent jet glows full once the pressure drives it at all (only its reach follows the push)
            float lc = Vent ? Mathf.Min(1f, lv / Mathf.Max(Push, 1e-3f)) * Mathf.Clamp01(Push * 8f) : lv;
            Vector3 cam = _t.InverseTransformPoint(camWorld);
            Quaternion face = Quaternion.LookRotation(-cam.normalized, Vector3.up);
            _glow.enabled = _root.enabled = on;
            foreach (var r in _core) r.enabled = on;
            foreach (var r in _sheath) r.enabled = on;
            foreach (var r in _flow) r.enabled = on;
            if (on)
            {
                float len = lenNow;
                // light it throws on everything around
                _glow.transform.localPosition = Dir * len * 0.3f;
                _glow.transform.localRotation = face;
                _glow.transform.localScale = Vector3.one * len * 1.1f;
                Vis.SetColor(_glow, new Color(1f, 0.45f, 0.1f) * 0.32f * Mathf.Min(lc, 1.2f) * Bright);
                _root.transform.localRotation = face;
                _root.transform.localScale = Vector3.one * W * 0.16f * (0.9f + 0.2f * Mathf.Sin(now * 37f));
                Vis.SetColor(_root, new Color(1f, 0.97f, 0.88f) * Mathf.Min(lc, 1f) * Bright);
                // white-hot core: long thin tongues, flickering in length
                for (int i = 0; i < _core.Length; i++)
                {
                    Vector3 ax = (Dir + _wob[i] * 0.04f * Mathf.Sin(now * 13f + i)).normalized;
                    var tr = _core[i].transform;
                    tr.localPosition = Vector3.zero;
                    tr.localRotation = AxisFace(ax, Vector3.zero, cam);
                    float h = len * (0.8f + 0.12f * i) * (0.88f + 0.24f * Mathf.PerlinNoise(now * 14f, i * 3.1f));
                    tr.localScale = new Vector3(W * (0.07f + 0.025f * i), h, 1f);
                    Vis.SetColor(_core[i], Color.white * Mathf.Min(lc, 1.2f) * Bright * (i == 0 ? 1f : 0.7f));
                }
                // turbulent sheath: wider, shorter, wobbling around the axis
                for (int i = 0; i < _sheath.Length; i++)
                {
                    Vector3 w = _wob[3 + i];
                    Vector3 ax = (Dir + w * (0.08f + 0.07f * Mathf.Sin(now * (5f + i) + _ph[3 + i] * 6f))).normalized;
                    var tr = _sheath[i].transform;
                    tr.localPosition = w * W * 0.02f;
                    tr.localRotation = AxisFace(ax, Vector3.zero, cam);
                    float h = len * (0.5f + 0.3f * Mathf.PerlinNoise(now * 7f, i * 1.7f + 5f));
                    tr.localScale = new Vector3(W * (0.16f + 0.05f * (i % 3)), h, 1f);
                    Vis.SetColor(_sheath[i], Color.white * 0.5f * Mathf.Min(lc, 1.2f) * Bright);
                }
                // hot puffs racing along it, growing and cooling as they go
                for (int i = 0; i < _flow.Length; i++)
                {
                    float cyc = now * 2.4f + _ph[i];
                    float u = cyc - Mathf.Floor(cyc);
                    var tr = _flow[i].transform;
                    tr.localPosition = Dir * (len * Mathf.Pow(u, 0.8f)) + _wob[i] * W * 0.09f * u * (0.5f + Mathf.PerlinNoise(now * 3f, i));
                    tr.localRotation = face;
                    tr.localScale = Vector3.one * W * (0.06f + 0.26f * u);
                    Color c = u < 0.2f ? Color.Lerp(new Color(1f, 0.95f, 0.75f), new Color(1f, 0.7f, 0.25f), u / 0.2f)
                            : u < 0.6f ? Color.Lerp(new Color(1f, 0.7f, 0.25f), new Color(1f, 0.32f, 0.06f), (u - 0.2f) / 0.4f)
                            : Color.Lerp(new Color(1f, 0.32f, 0.06f), new Color(0.4f, 0.05f, 0.01f), (u - 0.6f) / 0.4f);
                    Vis.SetColor(_flow[i], c * (0.42f * Mathf.Pow(1f - u, 1.2f) * Mathf.Min(lc, 1.2f) * Bright));
                }
                // burning grains thrown out with the gas
                if (now >= _next && !Cutting)
                {
                    _next = now + 0.012f;
                    for (int k = 0; k < 2; k++)
                    {
                        Vector3 side = Vector3.ProjectOnPlane(new Vector3(R() - 0.5f, R() - 0.5f, R() - 0.5f), Dir);
                        _grains.Add(new Sparks.P { P0 = side * W * 0.05f, V = Dir * (Vent ? len : Len) * (5f + 6f * R()) * Mathf.Min(lc, 1.3f) + side * W * 2.5f, T0 = now, Life = 0.25f + 0.4f * R(), Size = 0.004f + 0.005f * R(), C = new Color(1f, 0.85f, 0.5f) });
                    }
                }
            }
            // case bursting open: a white pop
            float pa = age / 0.14f;
            _pop.enabled = pa >= 0f && pa < 1f && !Vent;
            if (_pop.enabled)
            {
                _pop.transform.localRotation = face;
                _pop.transform.localScale = Vector3.one * Len * (0.15f + 0.45f * pa);
                Vis.SetColor(_pop, new Color(1f, 0.96f, 0.85f) * (1f - pa) * (1f - pa));
            }
            // dark smoke rolling off the end (outlives the flame for a moment)
            float sm = age < 0f ? 0f : Mathf.Clamp01(age / 0.5f) * (_cutAt < float.MaxValue ? Mathf.Clamp01(1f - (now - _cutAt) / 1.2f) : 1f);
            if (Vent) sm *= Mathf.Clamp01(Push * 6f);
            float sl = Vent ? lenNow : Len;
            for (int i = 0; i < _smoke.Length; i++)
            {
                float cyc = age * 0.7f + i / (float)_smoke.Length;
                float u = cyc - Mathf.Floor(cyc);
                var r = _smoke[i];
                r.enabled = sm > 0.01f;
                if (!r.enabled) continue;
                var tr = r.transform;
                tr.localPosition = Dir * sl * (0.85f + 0.5f * u) + Vector3.up * sl * 0.6f * u * u + _wob[i] * W * 0.15f * u;
                tr.localRotation = face;
                tr.localScale = Vector3.one * W * (0.22f + 0.6f * u);
                Vis.SetColor(r, new Color(0.05f, 0.04f, 0.035f, 0.42f * sm * Mathf.Clamp01(u * 5f) * (1f - u)));
            }
            _grains.Tick(now, cam);
        }

        public void Dispose() => _grains.Dispose();
    }
}
