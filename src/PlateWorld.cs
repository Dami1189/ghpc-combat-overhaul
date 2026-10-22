using System.Collections.Generic;
using UnityEngine;

namespace GHPCHitCam
{
    /// sun, sky and camera for lighting the world plates on the CPU (their shader is unlit)
    internal struct SceneLight
    {
        public Vector3 L;            // towards the sun
        public Color Sun;            // linear, intensity included
        public Color AmbUp, AmbSide, AmbDown;
        public Vector3 Cam;
        public bool Linear;
    }

    /// struck plate in the game world: same failure physics as the x-ray (petals, plug, crater, cracks, grooves, slots)
    /// on a real vehicle. sits on the struck collider's own face and stops at its edges, panel is just the damaged spot
    /// (~2x the hole, or the groove + lips). laid onto the armour colliders and the drawn paint, folds in ~0.3 s real time
    /// then freezes. steel, soot and a hot edge that cools, lit like the scene
    internal partial class HoleFx
    {
        public const float WorldFold = 0.3f;     // real seconds the fold takes
        public const float WorldCool = 3.5f;     // real seconds until the hot edges are black
        const int Grid = 17;                      // conform samples across the panel

        public delegate bool HullRay(Vector3 origin, Vector3 dir, float len, out float dist);
        /// depth of the visible surface over a square (half-size `half`, `res` x `res`, row-major, +x right, +y up) as plate z
        /// (+ into the plate). NaN where nothing is drawn, -Infinity where a cover we must never draw over (camo net) is nearest,
        /// null if unavailable
        public const int ProbeRes = 32;

        float _wDish, _wFold, _wR, _wPunchR;
        Vector3 _fn, _fat;      // plate face normal and the hit on it
        float _wCoreK = 1f;     // oblique holes: punched core reaches this much further along the path
        bool _wExit;            // exit wall: seen from outside the plate bulges toward the viewer
        float[] _wJag;          // torn-edge radius factor per column angle (petals, HE tears)
        float[] _wCover, _wAlpha, _wHot, _wSpec, _gz;     // _gz: hull heights on the conform grid
        // conform grid frame in panel space: point = _cu * u + _cv * v, u in [_u0, _u1], v in [_v0, _v1]
        Vector2 _cu = Vector2.right, _cv = Vector2.up;
        float _u0, _u1, _v0, _v1, _du, _dv;
        int _gu = Grid, _gv = Grid;
        public bool OnPaint { get; private set; }     // laid on the drawn surface, not just the armour collider
        Color[] _wAlb;
        int _wDropped = -1;
        readonly List<int> _wTri = new List<int>();
        // hole into the vehicle: short bore through the plate, then a dark cup
        Vector3[] _cupPos, _cupNrm;
        Color[] _cupCol, _cupAlb;
        float[] _cupHot;

        public float Radius => _hx;
        /// radius of the opening into the vehicle, 0 for a dent or gouge
        public float Opening { get; private set; }
        /// how deep the panel, bore and cup go behind the surface (depth punch has to clear it)
        public float Depth { get; private set; }
        public int OnHull { get; private set; }
        public bool Exit => _wExit;
        /// laid on the struck armour piece's own face, not just the hull colliders around the hit
        public bool OnRealPlate => _hasPlate;
        public float GrooveLength => Mode == PlateMode.Trench ? _tr.Len : 0f;
        public bool GrooveCut => Mode == PlateMode.Trench && _tr.Slot;
        /// middle of the damage (groove's middle, not its start) and how far it reaches, for close-ups
        public Vector3 ViewCenter => T == null ? Vector3.zero : Mode == PlateMode.Trench ? T.TransformPoint(new Vector3(_g.x, _g.y, 0f) * (0.5f * _tr.Len)) : T.position;
        public float ViewRadius => Mode == PlateMode.Trench ? 0.5f * _tr.Len + _ty : _hx * _wCoreK;
        /// visible point of the damage for the occlusion test to aim at (a groove's start can be under another plate)
        public Vector3 VisiblePoint => T == null ? Vector3.zero : T.TransformPoint(_wVis);
        Vector3 _wVis;
        /// why the panel isn't on the hull, if it isn't (self-test log)
        public string ConformNote { get; private set; } = "";
        public Mesh PlateMesh => _mesh;
        public Mesh CupMesh { get; private set; }
        public Mesh PunchMesh { get; private set; }

        static readonly Color Soot = new Color(0.008f, 0.007f, 0.006f), Scorch = new Color(0.03f, 0.026f, 0.021f),
                              Steel = new Color(0.055f, 0.055f, 0.058f), Bright = new Color(0.1f, 0.1f, 0.105f),
                              Blued = new Color(0.03f, 0.032f, 0.045f);

        /// `hull` = ray to the vehicle's outer surface, `struck` = same vs the struck collider only (fallback). `exit`: round came
        /// out through this face (petals and bulge go outward). `plateTris`: struck armour piece's tris (world, physics pose) or null
        /// build is split in two so the surface picture (GPU read-back) never stalls a frame: BeginWorld lays the plate on the
        /// struck piece in the physics pose, caller moves T to the drawn pose, grabs the picture at ProbeFrame, then FinishWorld
        /// when it's back (or with null)
        public static HoleFx BeginWorld(ArmorHit ah, int seed, bool exit, HullRay hull, HullRay struck, Vector3[] plateTris, Vector3 centre)
        {
            var fx = new HoleFx { Hit = ah, _rnd = new System.Random(seed), _wExit = exit };
            if (fx.BuildWorldA(hull, struck, Unique(plateTris), centre)) return fx;
            fx.Abandon();
            return null;
        }

        /// no hull under the hit (vehicle moved away, odd collider) or the spot is covered: flat decals have to do
        public void Abandon()
        {
            if (AutoTest.Active) HitCamMod.Log.Msg("World plate on " + Hit.Name + ": no hull found under it " + ConformNote);
            if (T != null) Object.Destroy(T.gameObject);
            Dispose();
        }

        /// square the surface picture has to cover (world pose of its centre, looking into the plate), from where T is now
        public void ProbeFrame(out Vector3 pos, out Quaternion rot, out float half, out float back)
        {
            float uc = 0.5f * (_u0 + _u1), vc = 0.5f * (_v0 + _v1);
            half = 0.5f * Mathf.Max(_u1 - _u0, _v1 - _v0);
            var pc = Panel(uc, vc);
            pos = T.position + T.rotation * new Vector3(pc.x, pc.y, 0f);
            rot = T.rotation * Quaternion.Euler(0f, 0f, Mathf.Atan2(_cu.y, _cu.x) * Mathf.Rad2Deg);
            back = 0.6f + half;
        }

        /// second half: drawn surface (null if no picture), rest of the conform, cracks, colours, cup
        public bool FinishWorld(float[] vis)
        {
            _sw.Restart();
            bool ok = ConformFinish(vis);
            _times += Lap("finish conform");
            if (!ok) return false;
            BuildCracks();
            _times += Lap("cracks");
            WorldLook();
            Positions(1f);      // settled shape, for the hole rim and the punch depth
            BuildCup();
            _owned.Add(CupMesh);
            _owned.Add(PunchMesh);
            ShapeWorld(0f);
            _times += Lap("rest");
            if (AutoTest.Active) ConformNote += "[ms: " + _times + _v.Length + " verts] ";
            return true;
        }

        /// collider meshes can have every tri twice (once per winding), then each edge looks shared and the outline is lost.
        /// keep one copy of each
        static Vector3[] Unique(Vector3[] t)
        {
            if (t == null) return null;
            long Key(Vector3 p) => ((long)Mathf.RoundToInt(p.x * 500f) * 73856093L) ^ ((long)Mathf.RoundToInt(p.y * 500f) * 19349663L) ^ ((long)Mathf.RoundToInt(p.z * 500f) * 83492791L);
            var seen = new HashSet<(long, long, long)>();
            var o = new List<Vector3>(t.Length);
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                long a = Key(t[i]), b = Key(t[i + 1]), c = Key(t[i + 2]);
                if (a > b) (a, b) = (b, a);
                if (b > c) (b, c) = (c, b);
                if (a > b) (a, b) = (b, a);
                if (!seen.Add((a, b, c))) continue;
                o.Add(t[i]); o.Add(t[i + 1]); o.Add(t[i + 2]);
            }
            return o.ToArray();
        }

        // build time per step (self-test log)
        readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
        string _times = "";
        string Lap(string name) { float ms = (float)_sw.Elapsed.TotalMilliseconds; _sw.Restart(); return name + " " + ms.ToString("0.0") + " "; }

        bool BuildWorldA(HullRay hull, HullRay struck, Vector3[] plateTris, Vector3 centre)
        {
            _sw.Restart();
            var ah = Hit;
            var go = new GameObject("HitCam plate " + ah.Name);
            T = go.transform;
            // world space: L / NormalL / DirL are world vectors here, +Z into the plate. plate = face of the struck armour piece the
            // round met (on exit: the face it came out of), that also gives the real angle: below graze angle it ploughs along it
            Vector3 n = ah.NormalL.normalized, at = ah.L;
            List<Vector3> face = null;
            if (plateTris != null)
            {
                ah.PlateTris = plateTris;
                Vector3 keep = ah.DirL;
                if (_wExit) ah.DirL = -keep;
                // which way is out: struck collider's normal at the hit, and for faces edge-on to it the vehicle centre, world space
                // (PlateFace's centre is the x-ray's, target-local)
                ah.HasSurf = true;
                ah.SurfNormalL = n;
                Vector3 keepCentre = CentreL;
                CentreL = centre;
                try { face = PlateFace(ah, out Vector3 fn0, out Vector3 fat0); if (face != null) { _fn = fn0; _fat = fat0; } }
                finally { CentreL = keepCentre; }
                ah.HasSurf = false;
                Vector3 fn = _fn, fat = _fat;
                if (face != null)
                {
                    n = fn; at = fat;
                    ah.SurfDone = ah.HasSurf = true;
                    ah.SurfNormalL = fn;
                    ah.SurfPointL = fat;
                    ah.SurfAngle = Mathf.Max(0.5f, 90f - Vector3.Angle(fn, -ah.DirL.normalized));
                }
                ah.DirL = keep;
            }
            T.SetPositionAndRotation(at, Quaternion.LookRotation(-n, Mathf.Abs(Vector3.Dot(n, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up));
            if (face != null) ToPanel(face, at);
            else ConformNote += "[no plate shape] ";
            _times += Lap("face");
            float v = Setup(false);
            float rh = Mathf.Max(ah.HoleRadius, 0.006f);
            _stretchOn = true;
            if (Mode == PlateMode.Trench)
            {
                // groove (or slot), plowed lips and scuffing a few widths each side, from just before impact to past where the rod
                // climbs out, cut off where the plate ends
                float w = _tr.HalfW;
                _wPunchR = Mathf.Clamp(3.2f * w, 0.02f, 0.3f);
                _ty = _wPunchR * 1.08f;
                _tx0 = -Mathf.Max(1.5f * w, 0.012f);
                float end = Mathf.Max(1.1f * _tr.Len, _tr.Len + 2.5f * w) + 0.01f;
                _tx1 = end;
                if (_hasPlate) ClipToPlate();
                _tx1 = Mathf.Min(_tx1, Mathf.Max(1.1f * _tr.Len, _tr.Len + 2.5f * w) + 0.01f);
                _hx = 0.5f * (_tx1 - _tx0);
                _hy = _ty;
                Size = _tx1 - _tx0;
                _low = _tx1 - _tx0 < 0.25f && _ty < 0.05f;
                _cu = _g; _cv = _gn;
                _u0 = _tx0; _u1 = _tx1; _v0 = -_ty; _v1 = _ty;
            }
            else
            {
                // only the damaged spot (~2x the hole) gets drawn over the paint (depth punch), the flat soot ring does the blast mark
                // around it so the punch can't paint over stuff in front of the hull (nets, handles). panel is just big enough for that
                _wPunchR = Mathf.Clamp(Mathf.Max(1.45f * _rFeat, 2f * rh), 0.04f, 0.85f);
                _hx = _hy = _wPunchR * 1.08f;
                Size = 2f * _hx;
                _low = _wPunchR < 0.1f;     // autocannon size: coarser mesh is plenty (and 5x cheaper)
                // oblique hole is stretched along the path (Stretch), so core, grid and punch are too
                float rf = Mathf.Max(_rFeat, 1e-3f);
                _wCoreK = _stretch <= 1.001f ? 1f : Mathf.Lerp(_stretch, 1f + (_stretch - 1f) * 0.3f, Smooth(1.5f * rf, 6f * rf, _wPunchR));
                _cu = _g; _cv = _gn;
                _u0 = -_hx * Mathf.Max(_stretch, 1f); _u1 = -_u0; _v0 = -_hx; _v1 = _hx;
            }
            _gv = Grid;
            _gu = Mathf.Clamp(Mathf.RoundToInt(Grid * (_u1 - _u0) / (_v1 - _v0)), Grid, 45) | 1;    // odd so there's a node on the hit
            // far-field dishing is mostly outside this small panel: keep its real (not doubled) shape vs the panel edge
            _wDish = Mode == PlateMode.Trench ? 0f : _dish / VisualDish;
            _wR = _R;
            _dish = 0f;
            _ripA = 0f;
            _wFold = 1.8f * TPen + 0.3f * TResp;
            if (_hasPlate) NearFace();
            BuildWedges(v);
            BuildMesh();
            TearEdge();
            _times += Lap("mesh");
            // (x-ray's OnPlate tests every vertex vs every face tri, here the conform grid knows the plate and cover / edge
            // distance get interpolated per vertex)
            if (_hasPlate)
            {
                float ext = 0f;
                foreach (var fp in _faceTri) ext = Mathf.Max(ext, fp.magnitude);
                _edgeBlend = Mathf.Clamp(0.25f * ext, 0.03f, 0.25f);
            }
            _times += Lap("onplate");
            bool ok = ConformRays(hull, struck);
            _times += Lap("rays");
            return ok;
        }

        float WorldDish(float r)
        {
            if (_wDish <= 0f) return 0f;
            float k = -1.7f / (_wR * _wR);
            return Mathf.Max(0f, _wDish * (Mathf.Exp(k * r * r) - Mathf.Exp(k * _hx * _hx)));
        }

        /// big armour pieces have hundreds of tris, keep only the ones under the panel (testing all of them per vertex costs tens of ms)
        void NearFace()
        {
            float m = 0.05f, eu0 = _u0 - m, eu1 = _u1 + m, ev0 = _v0 - m, ev1 = _v1 + m;
            bool Overlaps(Vector2 a, Vector2 b, Vector2 c)
            {
                var ua = GridUV(a); var ub = GridUV(b); var uc = GridUV(c);
                return Mathf.Max(ua.x, Mathf.Max(ub.x, uc.x)) >= eu0 && Mathf.Min(ua.x, Mathf.Min(ub.x, uc.x)) <= eu1
                    && Mathf.Max(ua.y, Mathf.Max(ub.y, uc.y)) >= ev0 && Mathf.Min(ua.y, Mathf.Min(ub.y, uc.y)) <= ev1;
            }
            var tri = new List<Vector2>();
            var tz = new List<float>();
            for (int i = 0; i + 2 < _faceTri.Length; i += 3)
                if (Overlaps(_faceTri[i], _faceTri[i + 1], _faceTri[i + 2]))
                    for (int e = 0; e < 3; e++) { tri.Add(_faceTri[i + e]); tz.Add(_faceZ[i + e]); }
            var ol = new List<Vector2>();
            for (int i = 0; i + 1 < _outline.Length; i += 2)
                if (Overlaps(_outline[i], _outline[i + 1], _outline[i + 1])) { ol.Add(_outline[i]); ol.Add(_outline[i + 1]); }
            if (tri.Count == 0) return;      // panel is past the face (shouldn't happen): keep everything
            _faceTri = tri.ToArray();
            _faceZ = tz.ToArray();
            _outline = ol.ToArray();
        }

        /// panel point (stretched for oblique holes) -> conform grid coords
        Vector2 GridUV(Vector2 p) => new Vector2(Vector2.Dot(p, _cu), Vector2.Dot(p, _cv));
        Vector2 Panel(float u, float v) => _cu * u + _cv * v;

        bool OnFace(Vector2 p)
        {
            for (int i = 0; i + 2 < _faceTri.Length; i += 3)
                if (InTri(p, _faceTri[i], _faceTri[i + 1], _faceTri[i + 2], out _, out _)) return true;
            return false;
        }

        /// heights of the struck plate under the panel, rays along its normal. only that plate counts: damage never runs past
        /// its edge, and where another armour piece sits on top (add-on plate, skirt, ERA) that part is covered and not drawn.
        /// surface is followed out from the hit only while it stays continuous, verts with no plate under them fade out.
        /// `hull` hits any of the vehicle's armour, `struck` only the struck collider
        // conform grid, kept between the two build halves
        float[] _cz, _cedge;
        bool[] _chit;
        int _cc;

        bool ConformRays(HullRay hull, HullRay struck)
        {
            int GU = _gu, GV = _gv, N = GU * GV;
            float du = (_u1 - _u0) / (GU - 1), dv = (_v1 - _v0) / (GV - 1);
            float probe = Mathf.Clamp(0.5f * Mathf.Max(_v1, 0.5f * (_u1 - _u0)) + 0.05f, 0.08f, 0.5f);
            var z = new float[N];
            var hit = new bool[N];
            var edge = new float[N];      // distance to the real plate edge (0 off it)
            Vector3 o = T.position, fwd = T.forward;
            Quaternion q = T.rotation;
            bool round = Mode != PlateMode.Trench;
            float ru = Mathf.Max(_u1, 1e-4f), rv = Mathf.Max(_v1, 1e-4f);
            int inside = 0, onStruck = 0, covered = 0;
            for (int j = 0; j < GV; j++)
                for (int i = 0; i < GU; i++)
                {
                    float u = _u0 + i * du, vv = _v0 + j * dv;
                    if (round && (u / ru) * (u / ru) + (vv / rv) * (vv / rv) > 1.17f) continue;    // outside the round (stretched) panel
                    Vector2 p = Panel(u, vv);
                    if (_hasPlate && !OnFace(p)) continue;                                           // past the real plate edge
                    inside++;
                    if (_hasPlate)
                    {
                        float ed = float.MaxValue;
                        for (int s = 0; s + 1 < _outline.Length; s += 2) ed = Mathf.Min(ed, SegDist(p, _outline[s], _outline[s + 1]));
                        edge[j * GU + i] = ed;
                    }
                    Vector3 from = o + q * new Vector3(p.x, p.y, -probe);
                    bool hs = struck(from, fwd, 2f * probe, out float ds);
                    bool hh = hull(from, fwd, 2f * probe, out float dh);
                    int k = j * GU + i;
                    if (hs)
                    {
                        onStruck++;
                        // another armour piece in front of the struck plate here
                        if (hh && dh < ds - 0.012f) { covered++; continue; }
                        hit[k] = true; z[k] = ds - probe;
                    }
                    else if (!_hasPlate && hh && Mathf.Abs(dh - probe) < 0.05f + 0.3f * Mathf.Sqrt(u * u + vv * vv))
                    {
                        // no plate shape and the struck collider's ray missed (odd mesh): use the hull there
                        hit[k] = true; z[k] = dh - probe;
                    }
                }
            // hit's node; a groove starting at the plate edge or under another plate takes the first visible node along its path
            int jc = Mathf.Clamp(Mathf.RoundToInt(-_v0 / dv), 0, GV - 1), ic = Mathf.Clamp(Mathf.RoundToInt(-_u0 / du), 0, GU - 1);
            int c = jc * GU + ic;
            if (!round && !hit[c])
                for (int i = ic + 1; i < GU; i++) if (hit[jc * GU + i]) { c = jc * GU + i; break; }
            if (!hit[c] || Mathf.Abs(z[c]) > 0.15f)
            {
                ConformNote += $"[centre {(hit[c] ? $"at {z[c] * 100f:0.0} cm" : "not on view")}, {onStruck}/{inside} on the plate, {covered} covered by other armour] ";
                return false;
            }
            if (covered > 0) ConformNote += $"[{covered} covered by other armour] ";
            _wVis = new Vector3(Panel(_u0 + (c % GU) * du, _v0 + (c / GU) * dv).x, Panel(_u0 + (c % GU) * du, _v0 + (c / GU) * dv).y, z[c]);
            if (round && Mathf.Abs(z[c]) > 1e-4f)
            {
                // reported impact point is off the collider face: sit on the face
                ConformNote += $"[re-centred {z[c] * 100f:0.0} cm] ";
                float zc = z[c];
                T.position += fwd * zc;
                for (int k = 0; k < N; k++) z[k] -= zc;
                _wVis.z = 0f;
            }
            _cz = z; _chit = hit; _cedge = edge; _cc = c;
            _du = du; _dv = dv;
            return true;
        }

        bool ConformFinish(float[] vis)
        {
            int GU = _gu, GV = _gv, N = GU * GV;
            float du = _du, dv = _dv;
            var z = _cz; var hit = _chit; var edge = _cedge;
            int c = _cc;
            var on = new bool[N];
            bool round = Mode != PlateMode.Trench;
            if (vis != null) Paintwork(vis, z, hit, GU, GV, du, dv, c);
            if (!hit[c])
            {
                ConformNote += "[covered at the centre] ";
                return false;
            }
            float step = Mathf.Max(du, dv) + 0.006f;   // up to ~45 deg off the impact plane
            var queue = new Queue<int>();
            on[c] = true;
            queue.Enqueue(c);
            int count = 1;
            while (queue.Count > 0)
            {
                int k = queue.Dequeue(), ki = k % GU, kj = k / GU;
                for (int s = 0; s < 4; s++)
                {
                    int ni = ki + (s == 0 ? 1 : s == 1 ? -1 : 0), nj = kj + (s == 2 ? 1 : s == 3 ? -1 : 0);
                    if (ni < 0 || nj < 0 || ni >= GU || nj >= GV) continue;
                    int nb = nj * GU + ni;
                    if (on[nb] || !hit[nb] || Mathf.Abs(z[nb] - z[k]) > step) continue;
                    on[nb] = true;
                    count++;
                    queue.Enqueue(nb);
                }
            }
            // nodes off the hull take their neighbour's height, else the faded edge spikes
            var set = (bool[])on.Clone();
            for (int pass = 0; pass < GU + GV; pass++)
            {
                bool any = false;
                for (int k = 0; k < N; k++)
                {
                    if (set[k]) continue;
                    int ki = k % GU, kj = k / GU;
                    float sum = 0f; int nn = 0;
                    if (ki > 0 && set[k - 1]) { sum += z[k - 1]; nn++; }
                    if (ki < GU - 1 && set[k + 1]) { sum += z[k + 1]; nn++; }
                    if (kj > 0 && set[k - GU]) { sum += z[k - GU]; nn++; }
                    if (kj < GV - 1 && set[k + GU]) { sum += z[k + GU]; nn++; }
                    if (nn == 0) continue;
                    z[k] = sum / nn;
                    set[k] = true;
                    any = true;
                }
                if (!any) break;
            }

            _gz = z;
            _du = du; _dv = dv;
            _wCover = new float[_v.Length];
            for (int k = 0; k < _v.Length; k++)
            {
                var vx = _v[k];
                Vector3 sp = Stretch(new Vector3(vx.P.x, vx.P.y, 0f), vx.R);
                var uv = GridUV(new Vector2(sp.x, sp.y));
                float gx = Mathf.Clamp((uv.x - _u0) / du, 0f, GU - 1.001f), gy = Mathf.Clamp((uv.y - _v0) / dv, 0f, GV - 1.001f);
                int i0 = (int)gx, j0 = (int)gy;
                float fx = gx - i0, fy = gy - j0;
                int a = j0 * GU + i0, b = a + 1, cc = a + GU, d = cc + 1;
                vx.Z0 = Mathf.Lerp(Mathf.Lerp(z[a], z[b], fx), Mathf.Lerp(z[cc], z[d], fx), fy);
                float cov = Mathf.Lerp(Mathf.Lerp(on[a] ? 1f : 0f, on[b] ? 1f : 0f, fx), Mathf.Lerp(on[cc] ? 1f : 0f, on[d] ? 1f : 0f, fx), fy);
                vx.Off = cov < 0.5f;
                vx.Cover = cov;
                if (_hasPlate) vx.EdgeD = Mathf.Lerp(Mathf.Lerp(edge[a], edge[b], fx), Mathf.Lerp(edge[cc], edge[d], fx), fy);
                _wCover[k] = cov;
                _v[k] = vx;
            }

            // depth punch: the damaged core's footprint on the hull (see WorldPlate)
            var pv = new Vector3[N];
            for (int k = 0; k < N; k++)
            {
                var p = Panel(_u0 + (k % GU) * du, _v0 + (k / GU) * dv);
                pv[k] = new Vector3(p.x, p.y, z[k]);
            }
            var pt = new List<int>();
            float pr = _wPunchR, pk = _wPunchR * _wCoreK;
            for (int j = 0; j < GV - 1; j++)
                for (int i = 0; i < GU - 1; i++)
                {
                    int a = j * GU + i, b = a + 1, cc = a + GU, d = cc + 1;
                    if (!on[a] || !on[b] || !on[cc] || !on[d]) continue;
                    float uc = _u0 + (i + 0.5f) * du, vc = _v0 + (j + 0.5f) * dv;
                    bool core = round ? (uc / pk) * (uc / pk) + (vc / pr) * (vc / pr) <= 1f : Capsule(uc, vc, _tr.Len) <= pr;
                    if (!core) continue;
                    pt.Add(a); pt.Add(cc); pt.Add(b);
                    pt.Add(b); pt.Add(cc); pt.Add(d);
                }
            if (PunchMesh != null) Object.Destroy(PunchMesh);
            PunchMesh = new Mesh { name = "plate punch" };
            if (N > 65000) PunchMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            PunchMesh.vertices = pv;
            PunchMesh.SetTriangles(pt, 0);
            PunchMesh.RecalculateBounds();
            OnHull = count;
            return true;
        }

        /// armour colliders only roughly follow the drawn hull (paint can be a few cm off) and stuff without colliders sits on
        /// top (camo nets, stowage, cables). a depth picture of the visible surface moves the panel onto the paint and drops
        /// it where something else covers the armour or nothing is drawn. `vis`: picture from ProbeFrame (ProbeRes x ProbeRes)
        void Paintwork(float[] vis, float[] z, bool[] hit, int GU, int GV, float du, float dv, int c)
        {
            const int R = ProbeRes;
            // square over the grid's rectangle, turned with it (see ProbeFrame)
            float uc = 0.5f * (_u0 + _u1), vc = 0.5f * (_v0 + _v1), half = 0.5f * Mathf.Max(_u1 - _u0, _v1 - _v0);
            float px = 2f * half / R;
            float Vis(int k)
            {
                float u = _u0 + (k % GU) * du - uc, v = _v0 + (k / GU) * dv - vc;
                int i = Mathf.Clamp((int)((u + half) / px), 0, R - 1), j = Mathf.Clamp((int)((v + half) / px), 0, R - 1);
                return vis[j * R + i];
            }
            int N = GU * GV;
            // not every material draws with the depth shader (custom render types): where nothing came back colliders decide
            int seen = 0, nodes = 0;
            for (int k = 0; k < N; k++) if (hit[k]) { nodes++; if (!float.IsNaN(Vis(k))) seen++; }
            int covered = 0, moved = 0;
            float centre = Vis(c) - z[c];
            // picture covers most of the spot: follow the paint point by point. only some of it: shift the whole panel by the
            // typical paint offset (point by point would leave steps between samples that came back and ones that didn't)
            bool full = seen >= nodes * 0.8f;
            var offs = new List<float>();
            for (int k = 0; k < N; k++)
            {
                if (!hit[k]) continue;
                float zv = Vis(k);
                if (float.IsNaN(zv)) continue;
                // drawn well in front of the armour: wheel, box, fender (or a camo net at any distance, see SurfaceProbe)
                if (zv < z[k] - 0.12f) { hit[k] = false; covered++; continue; }
                if (zv > z[k] + 0.12f) continue;      // drawn surface far behind the collider: trust the collider
                if (full) { z[k] = zv; moved++; }
                else offs.Add(zv - z[k]);
            }
            if (!full && offs.Count >= 3)
            {
                offs.Sort();
                float off = offs[offs.Count / 2];
                for (int k = 0; k < N; k++) if (hit[k]) z[k] += off;
                moved = -offs.Count;
            }
            OnPaint = moved != 0;
            ConformNote += $"[paint: {(moved < 0 ? "shifted by " + -moved + " samples" : moved + " on")}, {covered} covered, {seen}/{nodes} seen, centre {(float.IsNaN(centre) ? "-" : float.IsNegativeInfinity(centre) ? "net" : (centre * 100f).ToString("0.0") + " cm")}] ";
        }

        /// petals and HE tears aren't a circle: each petal gets its own length and the edge between them is ragged
        void TearEdge()
        {
            if (Mode != PlateMode.Petal && Mode != PlateMode.Tear) return;
            float amp = Mode == PlateMode.Tear ? 0.3f : 0.14f;
            float p1 = Rn(0f, 6.3f), p2 = Rn(0f, 6.3f), p3 = Rn(0f, 6.3f);
            float Jag(float th) => 1f + amp * (0.55f * Mathf.Sin(3f * th + p1) + 0.3f * Mathf.Sin(7f * th + p2) + 0.2f * Mathf.Sin(13f * th + p3));
            // petal shorter than its sector leaves the middle open (round punched it out)
            var petalLen = new Dictionary<int, float>();
            for (int wi = 0; wi < _w.Count; wi++) if (_w[wi].Petal) petalLen[wi] = 1f - Rn(0f, Mode == PlateMode.Tear ? 0.35f : 0.2f);
            float r1 = _rFeat * 1.7f;
            for (int k = 0; k < _v.Length; k++)
            {
                var vx = _v[k];
                if (vx.R >= r1) continue;
                // full jag up to the torn edge, blends back into the plate further out
                float w = 1f - Smooth(_rFeat, r1, vx.R);
                vx.P *= 1f + (Jag(vx.Th) - 1f) * w;
                var wd = _w[vx.W];
                if (petalLen.TryGetValue(vx.W, out float pl))
                {
                    float a = Vector2.Dot(vx.P, wd.E);
                    if (a < wd.Ah) vx.P += wd.E * ((wd.Ah - a) * (1f - pl));
                }
                vx.R = vx.P.magnitude;
                vx.Re = ElliptR(vx.P);
                _v[k] = vx;
            }
            const int m = 64;
            _wJag = new float[m];
            for (int i = 0; i < m; i++) _wJag[i] = Jag(i * Mathf.PI * 2f / m);
        }

        /// what each vertex is made of: scorched paint fading into real paint at the edge, soot toward the hole, bare torn /
        /// sheared / pushed / scraped steel at the failure, and which parts glow
        void WorldLook()
        {
            int n = _v.Length;
            _wAlb = new Color[n];
            _wSpec = new float[n];
            _wHot = new float[n];
            _wAlpha = new float[n];
            if (_wCover == null) { _wCover = new float[n]; for (int k = 0; k < n; k++) _wCover[k] = 1f; }
            bool trench = Mode == PlateMode.Trench;
            float hw = trench ? _tr.HalfW : 0f;
            // panel fades out just inside the punched core (past it it fights the paint), a groove's core is measured from its axis
            float fade1 = _wPunchR * 0.85f, fade0 = trench ? Mathf.Min(1.4f * hw, fade1 * 0.6f) : Mathf.Min(Mathf.Max(_rFeat * 1.1f, fade1 * 0.6f), fade1 * 0.9f);
            for (int k = 0; k < n; k++)
            {
                var vx = _v[k];
                var w = _w[vx.W];
                float r = vx.R;
                // no blast on the exit side or along a skimming rod's groove: scuffed paint, not soot
                Color alb = _wExit || trench ? Scorch * 1.3f : Color.Lerp(Scorch, Soot, 1f - Smooth(_rFeat * 0.8f, _hx * 0.8f, r));
                float spec = 0.03f, hot = 0f;
                float alpha = 1f - Smooth(fade0, fade1, trench ? vx.Re : r);
                switch (Mode)
                {
                    case PlateMode.Petal:
                    case PlateMode.Tear:
                        if (w.Petal)
                        {
                            float a = Vector2.Dot(vx.P, w.E);
                            if (a < w.Ah)
                            {
                                // petal: scorched paint at the hinge, torn bright steel toward the tip
                                float tip = 1f - Mathf.Clamp01(a / Mathf.Max(w.Ah, 1e-4f));
                                alb = Color.Lerp(Scorch * 1.4f, Color.Lerp(Blued, Steel, tip), 0.25f + 0.65f * tip);
                                spec = 0.08f + 0.3f * tip;
                                hot = Smooth(0.45f, 1f, tip) * (Mode == PlateMode.Tear ? 1f : 0.85f);
                                alpha = 1f;
                            }
                        }
                        break;
                    case PlateMode.Plug:
                        if (w.Plug) { alb = Steel; spec = 0.3f; alpha = 1f; }
                        else if (r < _rPlug * 1.6f)
                        {
                            // sheared edge and the lip dragged in with the plug
                            float e = Smooth(_rPlug, _rPlug * 1.6f, r);
                            alb = Color.Lerp(Bright, alb, e);
                            spec = Mathf.Lerp(0.7f, spec, e);
                            hot = vx.Ring == 0 ? 1f : vx.Ring == 1 ? 0.6f : 0.3f * (1f - e);
                            alpha = 1f;
                        }
                        break;
                    case PlateMode.Ductile:
                    case PlateMode.Jet:
                        if (r < _rCrater * 1.45f)
                        {
                            // metal pushed into the funnel and the raised lip: bright where it flowed, hottest at the hole
                            float f = Mathf.Clamp01(r / _rCrater);
                            Color pushed = Mode == PlateMode.Jet ? Color.Lerp(Steel * 0.6f, Scorch, f) : Color.Lerp(Bright, Steel, f);
                            alb = r < _rCrater ? pushed : Color.Lerp(Steel, alb, Smooth(_rCrater, _rCrater * 1.45f, r));
                            spec = r < _rCrater ? Mathf.Lerp(0.75f, 0.4f, f) : 0.3f;
                            hot = 1f - Smooth(0f, _rCrater * 0.7f, r);
                            alpha = 1f;
                        }
                        break;
                    case PlateMode.Dent:
                    case PlateMode.Gouge:
                    {
                        float qd = vx.Re / Mathf.Max(_rCrater, 1e-4f);
                        if (qd < 1.7f)
                        {
                            // KE round leaves a bright scuffed crater, a blast a sooty one
                            bool clean = !_he;
                            Color floor = clean ? Color.Lerp(Bright, Steel, Mathf.Clamp01(qd)) : Soot;
                            alb = qd < 1f ? floor : Color.Lerp(clean ? Steel : Soot, alb, Smooth(1f, 1.7f, qd));
                            spec = qd < 1f && clean ? 0.7f : 0.15f;
                            hot = clean && Hit.Energy > 2e5f ? 0.6f * (1f - Smooth(0.2f, 1.1f, qd)) : 0f;
                            alpha = 1f;
                        }
                        break;
                    }
                    case PlateMode.Trench:
                    {
                        float x = Vector2.Dot(vx.P, _g), ay = Mathf.Abs(Vector2.Dot(vx.P, _gn));
                        bool along = x > -0.5f * hw && x < _tr.Len;
                        if (_tr.Slot)
                        {
                            if (along && ay < 1.6f * hw)
                            {
                                // cut edges: thin face of fresh sheared steel, hot where the rod went through; edge dragged in beside it is scuffed paint
                                float e = Smooth(0.95f * hw, 1.6f * hw, ay);
                                alb = Color.Lerp(Bright, alb, e);
                                spec = Mathf.Lerp(0.6f, 0.05f, e);
                                hot = vx.Ring <= 1 ? 1f : 0.7f * (1f - e);
                                if (ay < 1.3f * hw) alpha = 1f;
                            }
                        }
                        else
                        {
                            float qd = vx.Re / Mathf.Max(hw, 1e-4f);
                            if (qd < 2.2f)
                            {
                                // groove floor scraped bright and hot by the rod, plowed lips, mound where it climbed out
                                alb = qd < 1f ? Color.Lerp(Bright, Steel, qd) : Color.Lerp(Steel, alb, Smooth(1f, 2.2f, qd));
                                spec = qd < 1f ? 0.8f : 0.3f * (1f - Smooth(1f, 2.2f, qd));
                                hot = (1f - Smooth(0.3f, 1.1f, qd)) * (_tr.Ric ? 0.7f : 0.9f);
                                if (qd < 1.5f) alpha = 1f;
                            }
                        }
                        break;
                    }
                }
                _wAlb[k] = alb;
                _wSpec[k] = spec;
                _wHot[k] = hot;
                _wAlpha[k] = alpha * Smooth(0.25f, 0.85f, _wCover[k]);
            }
        }

        /// plate shape at fold fraction u (0 = untouched, 1 = settled), torn-off petals and the plug leave the plate
        public void ShapeWorld(float u)
        {
            int dropped = Positions(u);
            _mesh.vertices = _pos;
            if (dropped != _wDropped)
            {
                _wDropped = dropped;
                _wTri.Clear();
                foreach (var w in _w)
                {
                    if ((w.Detach || w.Plug) && Tau > w.TBreak) continue;
                    int nr = w.Rings - 1;
                    for (int c = 0; c < w.Cols; c++)
                        for (int i = 0; i < nr; i++)
                        {
                            int a = w.V0 + c * w.Rings + i, b = a + w.Rings;
                            if (w.Grid < 0) { _wTri.Add(a); _wTri.Add(b); _wTri.Add(a + 1); _wTri.Add(b); _wTri.Add(b + 1); _wTri.Add(a + 1); }
                            else { _wTri.Add(a); _wTri.Add(a + 1); _wTri.Add(b); _wTri.Add(b); _wTri.Add(a + 1); _wTri.Add(b + 1); }
                        }
                }
                _mesh.SetTriangles(_wTri, 0);
            }
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            _mesh.GetNormals(_nrm);
        }

        /// vertex positions at fold fraction u, returns how many pieces left the plate
        int Positions(float u)
        {
            u = Mathf.Clamp01(u);
            Tau = _wFold * u;
            float g = Ease(u);
            int dropped = 0;
            for (int wi = 0; wi < _w.Count; wi++)
            {
                var w = _w[wi];
                bool gone = (w.Detach || w.Plug) && Tau > w.TBreak;
                if (gone) dropped++;
                int end = w.V0 + (w.Cols + 1) * w.Rings;
                for (int k = w.V0; k < end; k++)
                {
                    if (gone) { _pos[k] = Vector3.zero; continue; }
                    var q = Shape(k);
                    var vx = _v[k];
                    // a welded / bolted plate can't dish at its edge
                    float dish = WorldDish(vx.R) * g * (_hasPlate ? Smooth(0f, _edgeBlend, vx.EdgeD) : 1f);
                    float dz = q.z - vx.Z0 + dish;
                    q.z = vx.Z0 + (_wExit ? -dz : dz);
                    _pos[k] = q;
                }
            }
            return dropped;
        }

        /// way into the vehicle: hole wall through the plate (bright sheared or pushed steel), then a dark cup.
        /// only seen through the opening, the panel hides it everywhere else
        void BuildCup()
        {
            CupMesh = new Mesh { name = "plate cup" };
            Depth = Mathf.Max(_depthC, 0.02f) + 0.03f;
            Opening = 0f;
            var rim = new List<Vector3>();
            float bore;
            switch (Mode)
            {
                case PlateMode.Petal:
                case PlateMode.Tear:
                {
                    // just outside the petal hinges, under the flat plate between them
                    Opening = _rFeat;
                    const int m = 32;
                    for (int i = 0; i < m; i++)
                    {
                        float jag = _wJag != null ? Mathf.Min(_wJag[i * 2], 1.05f) * 0.97f : 1f;
                        Vector2 p = Dir(i * Mathf.PI * 2f / m) * _rFeat * jag;
                        Vector3 sp = Stretch(new Vector3(p.x, p.y, 0f), p.magnitude);
                        rim.Add(new Vector3(sp.x, sp.y, HullZ(new Vector2(sp.x, sp.y)) + 0.002f));
                    }
                    bore = 0f;
                    break;
                }
                case PlateMode.Plug:
                case PlateMode.Ductile:
                case PlateMode.Jet:
                {
                    // hole edge as the plate has it: ring 0 of the main block
                    var w = _w.Find(x => x.ToBorder);
                    Opening = Mode == PlateMode.Plug ? _rPlug : _rHole;
                    int stepC = Mathf.Max(1, w.Cols / 48);
                    for (int c = 0; c < w.Cols; c += stepC) rim.Add(_pos[w.V0 + c * w.Rings]);
                    bore = Mathf.Max(_t - (Mode == PlateMode.Plug ? 0.25f * _t : _depthC), 0.01f);
                    break;
                }
                case PlateMode.Trench when _tr.Slot && _w.Count == 2:
                {
                    // slot: between the cut's two edges (path-side rows of the two half-grids), out along one and back along the other
                    Wedge lo = _w[0], hi = _w[1];
                    Opening = 0.85f * _tr.HalfW;
                    for (int c = 0; c <= lo.Cols; c++) rim.Add(_pos[lo.V0 + c * lo.Rings]);
                    for (int c = hi.Cols; c >= 0; c--) rim.Add(_pos[hi.V0 + c * hi.Rings]);
                    bore = Mathf.Max(_t * 0.4f, 0.006f);
                    break;
                }
                default:
                    PunchDepth();
                    return;
            }
            int nR = rim.Count;
            float cupD = Mathf.Clamp(2.5f * Opening, 0.08f, 0.4f);
            Vector3 cen = Vector3.zero;
            foreach (var p in rim) cen += p;
            cen /= nR;
            // rings: rim, bore bottom, cup wall bottom, then the cup floor centre
            var pos = new List<Vector3>();
            var alb = new List<Color>();
            var hot = new List<float>();
            foreach (var p in rim) { pos.Add(p); alb.Add(bore > 0f ? Bright : Steel); hot.Add(bore > 0f ? 1f : 0.4f); }
            foreach (var p in rim) { pos.Add(p + Vector3.forward * bore); alb.Add(bore > 0f ? Steel * 0.5f : Soot); hot.Add(bore > 0f ? 0.5f : 0f); }
            foreach (var p in rim) { var e = new Vector3(cen.x + (p.x - cen.x) * 0.8f, cen.y + (p.y - cen.y) * 0.8f, p.z + bore + cupD); pos.Add(e); alb.Add(Color.black); hot.Add(0f); }
            pos.Add(new Vector3(cen.x, cen.y, cen.z + bore + cupD * 1.1f)); alb.Add(Color.black); hot.Add(0f);
            var tri = new List<int>();
            for (int ring = 0; ring < 2; ring++)
                for (int i = 0; i < nR; i++)
                {
                    int a = ring * nR + i, b = ring * nR + (i + 1) % nR;
                    tri.Add(a); tri.Add(b); tri.Add(a + nR);
                    tri.Add(b); tri.Add(b + nR); tri.Add(a + nR);
                }
            int ctr = 3 * nR;
            for (int i = 0; i < nR; i++) { tri.Add(2 * nR + i); tri.Add(2 * nR + (i + 1) % nR); tri.Add(ctr); }
            CupMesh.SetVertices(pos);
            CupMesh.SetTriangles(tri, 0);
            CupMesh.RecalculateNormals();
            CupMesh.RecalculateBounds();
            _cupPos = pos.ToArray();
            _cupNrm = CupMesh.normals;
            _cupAlb = alb.ToArray();
            _cupHot = hot.ToArray();
            _cupCol = new Color[_cupPos.Length];
            Depth = Mathf.Max(Depth, rim.Count > 0 ? cen.z + bore + cupD * 1.1f : 0f);
            PunchDepth();
        }

        void PunchDepth()
        {
            // deepest the folded petals or the slot's dragged-in edges go
            float zMax = 0f;
            for (int k = 0; k < _pos.Length; k++) if (_pos[k].z > zMax) zMax = _pos[k].z;
            Depth = Mathf.Max(Depth, zMax) + 0.05f;
        }

        /// hull height (panel z) under a panel point, from the conform grid
        float HullZ(Vector2 p)
        {
            if (_gz == null) return 0f;
            int GU = _gu, GV = _gv;
            var uv = GridUV(p);
            float gx = Mathf.Clamp((uv.x - _u0) / _du, 0f, GU - 1.001f), gy = Mathf.Clamp((uv.y - _v0) / _dv, 0f, GV - 1.001f);
            int i0 = (int)gx, j0 = (int)gy, a = j0 * GU + i0;
            float fx = gx - i0, fy = gy - j0;
            return Mathf.Lerp(Mathf.Lerp(_gz[a], _gz[a + 1], fx), Mathf.Lerp(_gz[a + GU], _gz[a + GU + 1], fx), fy);
        }

        static Color Glow(float h)
        {
            // steel by temperature, as emitted light (linear HDR): dull red, orange, yellow-white
            var dull = new Color(0.9f, 0.07f, 0.01f);
            var orange = new Color(3.2f, 0.9f, 0.12f);
            var white = new Color(6f, 4f, 2f);
            if (h <= 0f) return Color.clear;
            if (h < 0.33f) return dull * (h / 0.33f);
            if (h < 0.66f) return Color.Lerp(dull, orange, (h - 0.33f) / 0.33f);
            return Color.Lerp(orange, white, Mathf.Clamp01((h - 0.66f) / 0.34f));
        }

        static Color Ambient(in SceneLight sl, Vector3 n)
            => n.y >= 0f ? Color.Lerp(sl.AmbSide, sl.AmbUp, n.y) : Color.Lerp(sl.AmbSide, sl.AmbDown, -n.y);

        /// lit colours for the scene's sun and sky. `age` = real s since impact (hot parts cool), `shadow` = 0..1 sun visibility
        public void PaintWorld(in SceneLight sl, float age, float shadow)
        {
            float heat = Mathf.Pow(Mathf.Clamp01(1f - age / WorldCool), 1.6f);
            float ts = Tau;
            Quaternion q = T.rotation;
            Vector3 o = T.position;
            bool hasN = _nrm.Count == _v.Length;
            for (int k = 0; k < _v.Length; k++)
            {
                var vx = _v[k];
                Color a = _wAlb[k];
                float h = _wHot[k];
                if (vx.CrackW > 0f && ts >= vx.CrackT)
                {
                    a = Color.Lerp(a, Soot, vx.CrackW * 0.85f);
                    if (vx.Hot) h = Mathf.Max(h, vx.CrackW * 0.8f);
                }
                // what glowed ends up dark with oxide and soot
                if (h > 0f) a = Color.Lerp(a, Soot * 2f, h * 0.65f * (1f - heat));
                Vector3 pW = o + q * _pos[k];
                Vector3 nW = q * (hasN ? _nrm[k] : Vector3.back);
                Color c = Lit(sl, pW, nW, a, _wSpec[k], shadow);
                if (h > 0f && heat > 0f) c += Glow(h * heat);
                c.a = _wAlpha[k];
                _col[k] = sl.Linear ? c : c.gamma;
            }
            _mesh.colors = _col;

            if (_cupPos == null) return;
            for (int k = 0; k < _cupPos.Length; k++)
            {
                Color a = _cupAlb[k];
                float h = _cupHot[k];
                if (h > 0f) a = Color.Lerp(a, Soot * 2f, h * 0.65f * (1f - heat));
                // inside the vehicle only a little light gets in
                Color c = Lit(sl, o + q * _cupPos[k], q * _cupNrm[k], a, h > 0.9f ? 0.5f : 0.1f, shadow * 0.5f) * 0.7f;
                if (h > 0f && heat > 0f) c += Glow(h * heat);
                c.a = 1f;
                _cupCol[k] = sl.Linear ? c : c.gamma;
            }
            CupMesh.colors = _cupCol;
        }

        static Color Lit(in SceneLight sl, Vector3 pW, Vector3 nW, Color alb, float spec, float shadow)
        {
            Vector3 view = (sl.Cam - pW).normalized;
            if (Vector3.Dot(nW, view) < 0f) nW = -nW;    // two-sided sheet: use the side we see
            float ndl = Mathf.Max(0f, Vector3.Dot(nW, sl.L));
            Color light = sl.Sun * (ndl * shadow) + Ambient(sl, nW);
            Color c = new Color(alb.r * light.r, alb.g * light.g, alb.b * light.b, 1f);
            if (spec > 0f && ndl > 0f)
            {
                float s = spec * shadow * Mathf.Pow(Mathf.Max(0f, Vector3.Dot(nW, (sl.L + view).normalized)), 32f);
                c.r += sl.Sun.r * s; c.g += sl.Sun.g * s; c.b += sl.Sun.b * s;
            }
            return c;
        }
    }
}
