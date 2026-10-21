using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GHPC;
using MelonLoader;
using UnityEngine;
using DC = GHPC.Equipment.DestructibleComponent;

namespace GHPCHitCam
{
    /// A track the game has broken (its track hit zone destroyed): the real track model, cut at the break, becomes a
    /// chain of links (pin-jointed, about 12 cm apart) that hangs on what is under it. The chain keeps its length; gravity
    /// pulls it; the road wheels, sprocket and idler (the space inside the track's own loop, moving with the vehicle) and
    /// the ground hold it up. With the loop open nothing keeps it tight any more: the broken ends slide off the sprocket
    /// or idler and drop, a slack top run settles on the wheels, and if the vehicle moves off it, it is left lying on the
    /// ground. The model's vertices ride on the links. It settles in a few seconds and then rests (woken again if the
    /// vehicle moves).
    internal class WorldTrack : MonoBehaviour
    {
        const float Pitch = 0.12f;
        const float Step = 1f / 60f;

        Unit _u;
        Transform _h;                       // the hull: the space the wheels move in
        Renderer _src;
        MeshCopy _mc;
        Mesh _mesh;
        Vector3 _lat;                       // the track's lateral axis (world, at the break)
        float _xMid, _halfW, _thick;
        Vector2[] _inside;                  // the solid inside the loop (wheels, sprocket, idler): hull (z, y)
        Vector3[] _x, _xp;                  // the chain's links (world)
        float[] _rest;
        // each vertex rides on a link: segment, place along it, offsets across / sideways, normal and tangent in its frame
        int[] _seg; float[] _t, _on, _ol; Vector3[] _nf, _tf; float[] _tw;
        float _acc, _awakeFor, _quiet;
        bool _asleep;
        Vector3 _sleepPos; Quaternion _sleepRot;
        float[,] _ground; Vector2 _g0; float _gStep = 0.25f; int _gw, _gh;

        public static void Break(Unit u, DC dc, Vector3? hitW)
        {
            // not inside the game's damage call, and without the stalling read-back of a GPU-only track mesh (~120 ms):
            // the mesh is asked for now and the track breaks a few frames later, when it has come back
            var best = Model(u, dc, out var lods, out float bestD);
            if (best == null || bestD > 1.2f) { HitCamMod.Log.Msg($"World track {Recorder.SafeName(u)} '{dc.Name}': no track model found"); return; }
            var mesh = PartKit.MeshOf(best);
            MeshCopy.Prewarm(mesh);
            var rb = u.GetComponent<Rigidbody>();
            Transform h = rb != null ? rb.transform : u.transform;
            Vector3? local = hitW.HasValue ? h.InverseTransformPoint(hitW.Value) : (Vector3?)null;
            MelonCoroutines.Start(Later(u, dc, best, lods, mesh, h, local));
        }

        static IEnumerator Later(Unit u, DC dc, Renderer best, List<Renderer> lods, Mesh mesh, Transform h, Vector3? local)
        {
            float t0 = Time.realtimeSinceStartup;
            yield return null;
            while (!MeshCopy.Has(mesh) && MeshCopy.Asking(mesh) && Time.realtimeSinceStartup - t0 < 0.8f) yield return null;
            if (u == null || best == null || h == null) yield break;
            if (AutoTest.Active) HitCamMod.Log.Msg($"World track {Recorder.SafeName(u)} '{dc.Name}': mesh {(MeshCopy.Has(mesh) ? "read ahead" : "read now")}, {(Time.realtimeSinceStartup - t0) * 1000f:0} ms after the hit");
            Make(u, dc, best, lods, local.HasValue ? h.TransformPoint(local.Value) : (Vector3?)null);
        }

        /// The track model a track hit zone belongs to (and its other LODs).
        static Renderer Model(Unit u, DC dc, out List<Renderer> lods, out float bestD)
        {
            var col = dc.ComponentCollision;
            Bounds db = col != null ? WorldModules.ZoneBounds(col) : new Bounds(dc.transform.position, Vector3.one * 2f);
            Renderer best = null;
            bestD = float.MaxValue;
            var all = new List<Renderer>();
            foreach (var root in WorldDamage.UnitRoots(u))
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                    string n = r.name.ToLowerInvariant();
                    if (!(n.Contains("track") || n.Contains("tread")) || n.Contains("cutter") || n.Contains("dust")) continue;
                    if (r.GetComponentInParent<AarVisual>() != null || r.GetComponentInParent<AarModel>() != null) continue;
                    all.Add(r);
                    if (!r.enabled || !r.gameObject.activeInHierarchy || r.forceRenderingOff) continue;
                    float d = (r.bounds.center - db.center).magnitude;
                    if (Mathf.Abs(r.bounds.size.magnitude - db.size.magnitude) > 0.5f * db.size.magnitude) continue;
                    if (d < bestD) { bestD = d; best = r; }
                }
            lods = best != null ? all.Where(r => r != best && (r.bounds.center - best.bounds.center).magnitude < 0.35f).ToList() : new List<Renderer>();
            return best;
        }

        static void Make(Unit u, DC dc, Renderer best, List<Renderer> lods, Vector3? hitW)
        {
            var go = new GameObject("HitCam broken track") { layer = best.gameObject.layer };
            var wt = go.AddComponent<WorldTrack>();
            try
            {
                if (!wt.Init(u, best, hitW, out string why))
                {
                    HitCamMod.Log.Msg($"World track {Recorder.SafeName(u)} '{dc.Name}': not drawn ({why})");
                    Destroy(go);
                    return;
                }
                best.forceRenderingOff = true;
                foreach (var r in lods) r.forceRenderingOff = true;
            }
            catch (Exception e) { HitCamMod.Log.Warning("World track: " + e); Destroy(go); }
        }

        bool Init(Unit u, Renderer src, Vector3? hitW, out string why)
        {
            _u = u; _src = src;
            var rb = u.GetComponent<Rigidbody>();
            _h = rb != null ? rb.transform : u.transform;
            var mesh = PartKit.MeshOf(src);
            var full = MeshCopy.Read(mesh, out why);
            if (full == null) return false;
            full.Pose(src);
            // the model as drawn now, in world space
            _mc = full.StaticCopy("HitCam broken track", full.Tris, Matrix4x4.identity, out var tris, out _);
            int nv = _mc.Count;
            var P = new Vector3[nv];
            for (int v = 0; v < nv; v++) P[v] = _mc.Pos(v);
            _lat = _h.right;
            // the loop in the hull's side view: (z, y)
            var hz = new Vector2[nv];
            float xs = 0f;
            for (int v = 0; v < nv; v++)
            {
                Vector3 l = _h.InverseTransformPoint(P[v]);
                hz[v] = new Vector2(l.z, l.y);
                xs += l.x;
            }
            _xMid = xs / nv;
            _halfW = 0f;
            for (int v = 0; v < nv; v++) _halfW = Mathf.Max(_halfW, Mathf.Abs(_h.InverseTransformPoint(P[v]).x - _xMid));
            Vector2 C = Vector2.zero;
            foreach (var p in hz) C += p;
            C /= nv;
            // the track's middle line: per direction from the loop's middle, halfway between its inner and outer faces
            const int NB = 360;
            var rMin = new float[NB]; var rMax = new float[NB];
            for (int i = 0; i < NB; i++) { rMin[i] = float.MaxValue; rMax[i] = -1f; }
            void Sample(Vector2 p)
            {
                Vector2 d = p - C;
                int b = Mathf.Clamp(Mathf.FloorToInt((Mathf.Atan2(d.y, d.x) + Mathf.PI) / (Mathf.PI * 2f) * NB), 0, NB - 1);
                float r = d.magnitude;
                if (r < rMin[b]) rMin[b] = r;
                if (r > rMax[b]) rMax[b] = r;
            }
            foreach (var p in hz) Sample(p);
            // along every triangle edge too: the long straight runs have few vertices
            foreach (var tl in tris)
                for (int i = 0; i + 2 < tl.Count; i += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        Vector2 a = hz[tl[i + e]], b2 = hz[tl[i + (e + 1) % 3]];
                        int steps = Mathf.Clamp(Mathf.CeilToInt((b2 - a).magnitude / 0.03f), 1, 64);
                        for (int k = 1; k < steps; k++) Sample(Vector2.Lerp(a, b2, k / (float)steps));
                    }
            var mid = new float[NB];
            var thick = new List<float>();
            for (int i = 0; i < NB; i++) if (rMax[i] >= 0f) { mid[i] = 0.5f * (rMin[i] + rMax[i]); thick.Add(rMax[i] - rMin[i]); } else mid[i] = -1f;
            if (thick.Count < NB / 3) { why = $"too few loop samples ({thick.Count} of {NB} directions)"; return false; }
            thick.Sort();
            _thick = Mathf.Clamp(thick[thick.Count / 5], 0.03f, 0.25f);
            for (int i = 0; i < NB; i++)
            {
                if (mid[i] >= 0f) continue;
                int a = i, b = i;
                while (mid[(a + NB) % NB] < 0f) a--;
                while (mid[b % NB] < 0f) b++;
                mid[i] = Mathf.Lerp(mid[(a + NB) % NB], mid[b % NB], (i - a) / (float)(b - a));
            }
            var sm = new float[NB];
            for (int i = 0; i < NB; i++) { float s = 0f; for (int k = -3; k <= 3; k++) s += mid[(i + k + NB) % NB]; sm[i] = s / 7f; }
            var line = new List<Vector2>(NB);
            for (int i = 0; i < NB; i++)
            {
                float a = (i + 0.5f) / NB * Mathf.PI * 2f - Mathf.PI;
                line.Add(C + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * sm[i]);
            }
            // links every Pitch along it
            float total = 0f;
            for (int i = 0; i < NB; i++) total += (line[(i + 1) % NB] - line[i]).magnitude;
            int n = Mathf.Max(24, Mathf.RoundToInt(total / Pitch));
            float step = total / n;
            var q = new List<Vector2>(n);
            for (int k = 0; k < n; k++) q.Add(Along(line, k * step));
            // the solid inside the loop: the middle line moved in by half the track's thickness
            var inside = new Vector2[n];
            for (int k = 0; k < n; k++)
            {
                Vector2 tng = (q[(k + 1) % n] - q[(k - 1 + n) % n]).normalized;
                Vector2 nrm = new Vector2(-tng.y, tng.x);
                if (Vector2.Dot(C - q[k], nrm) < 0f) nrm = -nrm;
                inside[k] = q[k] + nrm * (_thick * 0.5f + 0.012f);
            }
            _inside = inside;

            // the break: the link nearest the hit (else the front of the top run), and the link it destroyed
            Vector3 hit = hitW ?? _h.TransformPoint(new Vector3(_xMid, q.OrderByDescending(p => p.x + p.y).First().y, q.OrderByDescending(p => p.x + p.y).First().x));
            Vector3 hl = _h.InverseTransformPoint(hit);
            int kb = 0;
            float bd = float.MaxValue;
            for (int k = 0; k < n; k++) { float d = (q[k] - new Vector2(hl.z, hl.y)).sqrMagnitude; if (d < bd) { bd = d; kb = k; } }
            int m = n - 1;                                   // the open chain: links kb+1 .. kb-1
            _x = new Vector3[m]; _xp = new Vector3[m]; _rest = new float[m - 1];
            for (int j = 0; j < m; j++)
            {
                Vector2 p = q[(kb + 1 + j) % n];
                _x[j] = _xp[j] = _h.TransformPoint(new Vector3(_xMid, p.y, p.x));
            }
            for (int j = 0; j < m - 1; j++) _rest[j] = (_x[j + 1] - _x[j]).magnitude;

            // each vertex onto its link
            _seg = new int[nv]; _t = new float[nv]; _on = new float[nv]; _ol = new float[nv];
            _nf = new Vector3[nv]; _tf = new Vector3[nv]; _tw = new float[nv];
            var gone = new bool[nv];
            float nearKb = float.MaxValue; int onKb = 0;
            for (int v = 0; v < nv; v++)
            {
                Vector3 p = P[v];
                int bj = -1; float bt = 0f, bdist = float.MaxValue;
                for (int j = 0; j < m - 1; j++)
                {
                    Vector3 a = _x[j], b = _x[j + 1], ab = b - a;
                    Vector3 pp = p - _lat * Vector3.Dot(p - a, _lat);
                    float t = Mathf.Clamp01(Vector3.Dot(pp - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-8f));
                    float d = (pp - (a + ab * t)).sqrMagnitude;
                    if (d < bdist) { bdist = d; bj = j; bt = t; }
                }
                // vertices of the destroyed link (within about half a link of the break, along the loop) go with it
                Vector3 hv = _h.InverseTransformPoint(p);
                Vector2 v2 = new Vector2(hv.z, hv.y);
                float bestL = float.MaxValue; int li = 0; float lt = 0f;
                for (int k = 0; k < n; k++)
                {
                    Vector2 a2 = q[k], b2 = q[(k + 1) % n], ab2 = b2 - a2;
                    float t2 = Mathf.Clamp01(Vector2.Dot(v2 - a2, ab2) / Mathf.Max(ab2.sqrMagnitude, 1e-8f));
                    float d2 = (v2 - (a2 + ab2 * t2)).sqrMagnitude;
                    if (d2 < bestL) { bestL = d2; li = k; lt = t2; }
                }
                if ((li == (kb - 1 + n) % n && lt > 0.45f) || (li == kb && lt < 0.55f)) gone[v] = true;
                float dk = (v2 - q[kb]).magnitude;
                if (dk < nearKb) nearKb = dk;
                if (li == kb || li == (kb - 1 + n) % n) onKb++;
                _seg[v] = bj; _t[v] = bt;
                Vector3 a0 = _x[bj], b0 = _x[bj + 1];
                Vector3 tau = (b0 - a0).normalized, nu = Vector3.Cross(_lat, tau);
                Vector3 o = p - Vector3.Lerp(a0, b0, bt);
                _on[v] = Vector3.Dot(o, nu); _ol[v] = Vector3.Dot(o, _lat);
                if (_mc.NrmA >= 0) { Vector3 nn = _mc.Get(v, _mc.NrmA); _nf[v] = new Vector3(Vector3.Dot(nn, tau), Vector3.Dot(nn, nu), Vector3.Dot(nn, _lat)); }
                if (_mc.TanA >= 0) { Vector4 tt = _mc.Get(v, _mc.TanA); _tf[v] = new Vector3(Vector3.Dot(tt, tau), Vector3.Dot(tt, nu), Vector3.Dot(tt, _lat)); _tw[v] = tt.w; }
            }
            int dropped = 0;
            for (int s = 0; s < tris.Length; s++)
            {
                var keep = new List<int>(tris[s].Count);
                for (int i = 0; i + 2 < tris[s].Count; i += 3)
                {
                    int a = tris[s][i], b = tris[s][i + 1], c = tris[s][i + 2];
                    if (gone[a] || gone[b] || gone[c]) { dropped++; continue; }
                    keep.Add(a); keep.Add(b); keep.Add(c);
                }
                tris[s] = keep;
            }
            _mc.Tris = tris;
            _mesh = _mc.Build("HitCam broken track", tris, keepSkin: false);
            _mesh.MarkDynamic();
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var mr = gameObject.AddComponent<MeshRenderer>();
            PartKit.CopyLook(src, mr);

            // the ground under it
            Bounds wb = src.bounds;
            wb.Expand(new Vector3(4f, 0f, 4f));
            _g0 = new Vector2(wb.min.x, wb.min.z);
            _gw = Mathf.CeilToInt(wb.size.x / _gStep) + 1;
            _gh = Mathf.CeilToInt(wb.size.z / _gStep) + 1;
            _ground = new float[_gw, _gh];
            int tl0 = LayerMask.NameToLayer("Terrain");
            int terrainMask = tl0 >= 0 ? 1 << tl0 : 0;
            for (int i = 0; i < _gw; i++)
                for (int j = 0; j < _gh; j++)
                {
                    // the ground's colliders (terrain, a test pad), else the terrain height
                    Vector3 from = new Vector3(_g0.x + i * _gStep, wb.max.y + 3f, _g0.y + j * _gStep);
                    if (terrainMask != 0 && Physics.Raycast(from, Vector3.down, out var gh, 60f, terrainMask, QueryTriggerInteraction.Ignore)) { _ground[i, j] = gh.point.y; continue; }
                    var gp = GHPC.Utility.CodeUtils.GetPositionOnTerrain(new Vector3(from.x, wb.max.y + 50f, from.z), out bool ok, 400f);
                    _ground[i, j] = ok ? gp.y : wb.min.y - 0.05f;
                }
            // the broken link
            for (int i = 0; i < 2; i++)
                WorldDamage.SpawnPiece("HitCam track link", hit + _h.up * 0.05f, UnityEngine.Random.rotationUniform, new Vector3(_halfW * 2f, 0.035f, Pitch * 1.2f),
                                       (UnityEngine.Random.insideUnitSphere + _h.up) * 2f, UnityEngine.Random.insideUnitSphere * 6f, mass: 18f);
            HitCamMod.Log.Msg($"World track {Recorder.SafeName(u)} '{src.name}': broken at {hl}, loop {total:0.0} m, {m} links, {_thick * 1000f:0} mm thick, " +
                              $"{_halfW * 2000f:0} mm wide, {nv} vertices ({dropped} triangles of the broken link gone; {onKb} vertices on its two segments, nearest {nearKb * 1000f:0} mm from it)");
            return true;
        }

        static Vector2 Along(List<Vector2> line, float s)
        {
            int n = line.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = line[i], b = line[(i + 1) % n];
                float l = (b - a).magnitude;
                if (s <= l || i == n - 1) return Vector2.Lerp(a, b, l > 1e-6f ? Mathf.Clamp01(s / l) : 0f);
                s -= l;
            }
            return line[0];
        }

        float Ground(Vector3 p)
        {
            float fx = (p.x - _g0.x) / _gStep, fz = (p.z - _g0.y) / _gStep;
            int i = Mathf.Clamp(Mathf.FloorToInt(fx), 0, _gw - 2), j = Mathf.Clamp(Mathf.FloorToInt(fz), 0, _gh - 2);
            float tx = Mathf.Clamp01(fx - i), tz = Mathf.Clamp01(fz - j);
            return Mathf.Lerp(Mathf.Lerp(_ground[i, j], _ground[i + 1, j], tx), Mathf.Lerp(_ground[i, j + 1], _ground[i + 1, j + 1], tx), tz);
        }

        static bool Inside(Vector2[] poly, Vector2 p)
        {
            bool c = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].y > p.y) != (poly[j].y > p.y) && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x) c = !c;
            return c;
        }

        static Vector2 Nearest(Vector2[] poly, Vector2 p)
        {
            Vector2 best = poly[0];
            float bd = float.MaxValue;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                Vector2 a = poly[j], b = poly[i], ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-8f));
                Vector2 c = a + ab * t;
                float d = (p - c).sqrMagnitude;
                if (d < bd) { bd = d; best = c; }
            }
            return best;
        }

        void Collide(int j)
        {
            Vector3 l = _h.InverseTransformPoint(_x[j]);
            if (Mathf.Abs(l.x - _xMid) < _halfW + 0.03f)
            {
                var p = new Vector2(l.z, l.y);
                if (Inside(_inside, p))
                {
                    Vector2 c = Nearest(_inside, p);
                    Vector2 outv = c - p;
                    c += outv.sqrMagnitude > 1e-10f ? outv.normalized * 0.002f : Vector2.zero;
                    _x[j] = _h.TransformPoint(new Vector3(l.x, c.y, c.x));
                    _xp[j] = Vector3.Lerp(_xp[j], _x[j], 0.5f);      // friction on the wheels
                }
            }
            float g = Ground(_x[j]) + _thick * 0.5f;
            if (_x[j].y < g)
            {
                _x[j].y = g;
                _xp[j] = new Vector3(_x[j].x, g, _x[j].z);           // the ground holds it where it lies
            }
        }

        void Update() { using (Perf.T("WorldTrack.Update")) TimedUpdate(); }
        void TimedUpdate()
        {
            if (_u == null || _h == null || _mesh == null) { Destroy(gameObject); return; }
            if (_asleep)
            {
                // woken when the vehicle moves under it
                if ((_h.position - _sleepPos).magnitude > 0.15f || Quaternion.Angle(_h.rotation, _sleepRot) > 3f) { _asleep = false; _awakeFor = 0f; _quiet = 0f; }
                else return;
            }
            _acc += Mathf.Min(Time.deltaTime, 0.1f);
            float moved = 0f;
            int steps = 0;
            while (_acc >= Step && steps < 3)
            {
                _acc -= Step; steps++;
                Vector3 g = Physics.gravity * Step * Step;
                for (int j = 0; j < _x.Length; j++)
                {
                    Vector3 v = (_x[j] - _xp[j]) * 0.985f;
                    _xp[j] = _x[j];
                    _x[j] += v + g;
                }
                for (int it = 0; it < 10; it++)
                {
                    for (int j = 0; j < _rest.Length; j++)
                    {
                        Vector3 d = _x[j + 1] - _x[j];
                        float len = d.magnitude;
                        if (len < 1e-6f) continue;
                        Vector3 corr = d * ((len - _rest[j]) / len * 0.5f);
                        _x[j] += corr; _x[j + 1] -= corr;
                    }
                    if ((it & 1) == 1 || it == 9) for (int j = 0; j < _x.Length; j++) Collide(j);
                }
                for (int j = 0; j < _x.Length; j++) moved = Mathf.Max(moved, (_x[j] - _xp[j]).magnitude);
            }
            if (steps == 0) return;
            _awakeFor += steps * Step;
            _quiet = moved < 0.0008f ? _quiet + steps * Step : 0f;
            Draw();
            if (_quiet > 1f || _awakeFor > 12f)
            {
                _asleep = true;
                _sleepPos = _h.position; _sleepRot = _h.rotation;
                for (int j = 0; j < _x.Length; j++) _xp[j] = _x[j];
            }
        }

        void Draw()
        {
            Vector3 lo = Vector3.one * float.MaxValue, hi = Vector3.one * float.MinValue;
            for (int v = 0; v < _mc.Count; v++)
            {
                int j = _seg[v];
                Vector3 a = _x[j], b = _x[j + 1];
                Vector3 tau = (b - a).normalized, nu = Vector3.Cross(_lat, tau);
                Vector3 p = Vector3.Lerp(a, b, _t[v]) + nu * _on[v] + _lat * _ol[v];
                Vector4 p4 = _mc.Get(v, _mc.PosA);
                _mc.Set(v, _mc.PosA, new Vector4(p.x, p.y, p.z, p4.w));
                if (_mc.NrmA >= 0)
                {
                    Vector3 nn = (tau * _nf[v].x + nu * _nf[v].y + _lat * _nf[v].z).normalized;
                    Vector4 n4 = _mc.Get(v, _mc.NrmA);
                    _mc.Set(v, _mc.NrmA, new Vector4(nn.x, nn.y, nn.z, n4.w));
                }
                if (_mc.TanA >= 0)
                {
                    Vector3 tt = (tau * _tf[v].x + nu * _tf[v].y + _lat * _tf[v].z).normalized;
                    _mc.Set(v, _mc.TanA, new Vector4(tt.x, tt.y, tt.z, _tw[v]));
                }
                lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
            }
            var bb = new Bounds();
            bb.SetMinMax(lo, hi);
            _mc.Upload(_mesh, bb);
        }
    }
}
