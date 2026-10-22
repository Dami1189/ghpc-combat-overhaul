using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using GHPC;
using GHPC.Weapons;
using UnityEngine;
using UnityEngine.Rendering;

namespace GHPCHitCam
{
    /// hit on a gun barrel on the real vehicle: the hole the round makes in the tube (and the exit on the far side) and
    /// what the hit did to the tube as a structure. tube = hollow steel beam held at the mantlet; the hit's impulse swings
    /// the muzzle part against the part behind the hit, and the damaged section there bends (plastic hinge) until that
    /// motion is used up, or tears through and lets the muzzle end go. its own weight can fold a mostly cut tube.
    /// nothing is pre-made: holes, bend and break come from the round, hit point and tube size, and get cut into the
    /// game's own barrel mesh (skinned or not, read back from the GPU when the game keeps it there), so the bent tube
    /// still elevates and traverses with the gun
    internal sealed class WorldBarrel
    {
        static readonly Dictionary<WeaponSystem, WorldBarrel> _all = new Dictionary<WeaponSystem, WorldBarrel>();

        const float Steel = 7850f;      // kg/m3
        const float Yield = 1.05e9f;    // Pa, gun tube steel
        const float Gravity = 9.81f;
        const int NPhi = 48, NRad = 4;  // tube cross-section, in cells
        const float Bin = 0.01f, U0 = -0.15f;

        public WeaponSystem Ws;
        public Unit U;
        public Transform F;             // game's muzzle point: +z out of the muzzle, tube runs back along -z
        public float Bore, Length;      // m
        float _rTube;                   // drawn tube radius near the muzzle
        float[] _prof;                  // drawn outer radius every 1 cm from the muzzle back (U0 first)
        readonly List<Part> _parts = new List<Part>();
        readonly List<Feature> _feat = new List<Feature>();
        readonly List<Wound> _wounds = new List<Wound>();
        bool _bent;
        float _bendU, _bendW, _bentAngle;
        Quaternion _bendQ = Quaternion.identity;
        float _cutU = -1f;              // broken off here (muzzle side, u < _cutU, is gone)
        float _cutBorn, _cutPhi, _tongue, _peel, _flare;
        Mesh _tear;                     // torn end of the stump (inside of the wall, rim, bore), F space
        int _cutSeed;
        bool _blown;                    // blown off by a blast (scorched, ragged), not torn by the swing
        GameObject _fx;                 // wounds, bore, caps: in F's space
        int _layer, _seed;
        string _why;

        sealed class Part { public Renderer R; public MeshCopy Mc; public Mesh Mine; }

        Matrix4x4 _wl;                  // world -> F (gun frame has no scale)
        Vector3 ToF(Vector3 w) => _wl.MultiplyPoint3x4(w);

        enum FK { Perf, Gouge, Blast }
        struct Feature { public FK Kind; public Vector3 P, D; public float R, U, Phi, Arc, Half, Depth; public bool Through; }

        enum WK { Entry, Exit, Jet, Tear, Dent }
        struct Wound { public WK Kind; public float U, Phi, Rho, Depth, Born; public int Seed; public bool Cut; }

        // ------------------------------------------------------------------ finding the tube

        public static WorldBarrel For(WeaponSystem ws, Unit u)
        {
            if (ws == null || ws.MuzzleIdentity == null || u == null) return null;
            if (_all.TryGetValue(ws, out var b)) return b._why == null ? b : null;
            b = new WorldBarrel { Ws = ws, U = u, F = ws.MuzzleIdentity, _seed = ws.GetInstanceID() };
            try { b._why = b.Analyse(); }
            catch (Exception e) { b._why = e.GetType().Name + ": " + e.Message; }
            _all[ws] = b;
            if (b._why != null) HitCamMod.Log.Msg($"World barrel {Recorder.SafeName(u)} / {ws.name}: not drawn ({b._why})");
            else if (AutoTest.Active)
                HitCamMod.Log.Msg($"World barrel {Recorder.SafeName(u)} / {ws.name}: bore {b.Bore * 1000f:0} mm, length {b.Length:0.00} m, drawn radius {b._rTube * 1000f:0} mm, " +
                                  $"walls {b.Wall(0f) * 1000f:0}..{b.Wall(b.Length) * 1000f:0} mm, mass {b.TubeMass():0} kg, in " +
                                  string.Join(", ", b._parts.Select(p => $"{p.R.name} ({p.Mc.Count} v, {(p.Mc.Skinned ? "skinned" : "static")})")));
            return b._why == null ? b : null;
        }

        public static void Reset() => _all.Clear();

        string Analyse()
        {
            _wl = F.worldToLocalMatrix;
            Vector3 ls = F.lossyScale;
            if (Mathf.Abs(ls.x - 1f) > 0.02f || Mathf.Abs(ls.y - 1f) > 0.02f || Mathf.Abs(ls.z - 1f) > 0.02f) return "scaled gun frame " + ls;
            var m = Regex.Match(Ws.name ?? "", @"(\d+(?:\.\d+)?)\s*mm", RegexOptions.IgnoreCase);
            if (m.Success) Bore = float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 0.001f;
            if (Bore < 0.015f && Ws.CurrentAmmoType != null && Ws.CurrentAmmoType.Caliber > 15f) Bore = Ws.CurrentAmmoType.Caliber * 0.001f;
            _layer = WorldDamage.VisibleLayer(U);

            // renderers around the tube's line, read in full (game keeps most of them on the GPU only)
            Vector3 M = F.position, a = F.forward;
            var seg = new Bounds(M, Vector3.zero);
            seg.Encapsulate(M - a * 7.5f);
            seg.Expand(0.9f);
            var cands = new List<(Renderer r, MeshCopy mc, int near)>();
            foreach (var root in WorldDamage.UnitRoots(U))
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                    bool live = r.enabled && r.gameObject.activeInHierarchy;
                    if (!live && r.name.IndexOf("lod", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (r.GetComponentInParent<AarVisual>() != null || r.GetComponentInParent<AarModel>() != null) continue;
                    if (r.bounds.size.magnitude > 24f || !r.bounds.Intersects(seg)) continue;
                    var mesh = PartKit.MeshOf(r);
                    if (mesh == null || mesh.vertexCount < 8) continue;
                    var mc = MeshCopy.Read(mesh, out string why);
                    if (mc == null) { HitCamMod.Log.Msg($"World barrel: {r.name} not readable ({why})"); continue; }
                    if (r is SkinnedMeshRenderer smr0 && smr0.bones != null && smr0.bones.Length > 0 && !mc.Skinned)
                    {
                        HitCamMod.Log.Msg($"World barrel: {r.name} is skinned but its skin weights / bind poses can't be read (" +
                                          string.Join(" ", mc.Attrs.Select(x => $"{x.attribute}:{x.format}x{x.dimension}@s{x.stream}")) + $", bindposes {mc.Bindposes?.Length ?? -1})");
                        continue;
                    }
                    mc.Pose(r);
                    int near = 0;
                    for (int v = 0; v < mc.Count; v++)
                    {
                        Vector3 q = ToF(mc.W[v]);
                        if (-q.z > -0.05f && -q.z < 7.5f && q.x * q.x + q.y * q.y < 0.35f * 0.35f) near++;
                    }
                    if (near >= 12) cands.Add((r, mc, near));
                }
            if (cands.Count == 0) return "no model around the barrel";

            // drawn radius along the tube from every triangle edge near its line (long triangles have no vertices mid-way)
            int nb = Mathf.CeilToInt((7.5f - U0) / Bin);
            _prof = new float[nb];
            for (int i = 0; i < nb; i++) _prof[i] = -1f;
            foreach (var c in cands)
            {
                var q = new Vector3[c.mc.Count];
                for (int v = 0; v < q.Length; v++) q[v] = ToF(c.mc.W[v]);
                foreach (var tl in c.mc.Tris)
                    for (int i = 0; i + 2 < tl.Count; i += 3)
                    {
                        Vector3 A = q[tl[i]], B = q[tl[i + 1]], C = q[tl[i + 2]];
                        if (RXY(A) > 0.35f || RXY(B) > 0.35f || RXY(C) > 0.35f) continue;
                        SampleEdge(A, B); SampleEdge(B, C); SampleEdge(C, A);
                    }
            }
            // gaps (between ring stations of a coarse model): straight line between the known ones
            int last = -1;
            for (int i = 0; i < nb; i++)
            {
                if (_prof[i] < 0f) continue;
                if (last >= 0 && i - last > 1)
                    for (int k = last + 1; k < i; k++) _prof[k] = Mathf.Lerp(_prof[last], _prof[i], (k - last) / (float)(i - last));
                last = i;
            }
            int first = Array.FindIndex(_prof, x => x >= 0f);
            if (first < 0) return "no tube found";
            for (int i = 0; i < first; i++) _prof[i] = -1f;
            // tube near the muzzle, then back to where mantlet / turret starts (drawn radius jumps)
            var nearMuzzle = new List<float>();
            for (float u = 0.05f; u < 0.7f; u += Bin) { float r = RawProf(u); if (r > 0f) nearMuzzle.Add(r); }
            if (nearMuzzle.Count < 5) return "no tube near the muzzle";
            nearMuzzle.Sort();
            _rTube = nearMuzzle[nearMuzzle.Count / 2];
            float jump = Mathf.Max(2.2f * _rTube, _rTube + 0.12f);
            Length = 0f;
            int run = 0;
            for (float u = 0.3f; u < 7.4f; u += Bin)
            {
                float r = RawProf(u);
                if (r < 0f) { Length = u; break; }
                if (r > jump) { if (++run >= 3) { Length = u - 2f * Bin; break; } }
                else run = 0;
                Length = u;
            }
            if (Length < 0.4f) return $"tube too short ({Length:0.00} m)";
            if (Bore <= 0f) Bore = 1.2f * _rTube;
            Bore = Mathf.Min(Bore, 1.8f * _rTube);

            // parts that draw the tube itself
            foreach (var c in cands)
            {
                int on = 0;
                for (int v = 0; v < c.mc.Count; v++)
                    if (OnTube(ToF(c.mc.W[v]), 1.35f)) on++;
                if (on >= 12) _parts.Add(new Part { R = c.r, Mc = c.mc });
            }
            if (_parts.Count == 0) return "no part draws the tube";
            return null;
        }

        static float RXY(Vector3 q) => Mathf.Sqrt(q.x * q.x + q.y * q.y);

        void SampleEdge(Vector3 A, Vector3 B)
        {
            for (int k = 0; k <= 8; k++)
            {
                Vector3 p = Vector3.Lerp(A, B, k / 8f);
                float u = -p.z;
                int i = Mathf.FloorToInt((u - U0) / Bin);
                if (i < 0 || i >= _prof.Length) continue;
                float r = RXY(p);
                if (r > _prof[i]) _prof[i] = r;
            }
        }

        float RawProf(float u)
        {
            int i = Mathf.Clamp(Mathf.FloorToInt((u - U0) / Bin), 0, _prof.Length - 1);
            return _prof[i];
        }

        /// drawn outer radius at u (m from the muzzle)
        float Rvis(float u)
        {
            float r = RawProf(Mathf.Clamp(u, 0f, Length));
            return r > 0f ? Mathf.Min(r, Mathf.Max(_rTube * 1.9f, 0.02f)) : _rTube;
        }

        bool OnTube(Vector3 q, float slack)
        {
            float u = -q.z;
            if (u < -0.1f || u > Length) return false;
            return RXY(q) < Rvis(u) * slack + 0.01f;
        }

        // ------------------------------------------------------------------ the tube as a steel beam

        float Ri => Bore * 0.5f;
        /// wall thickness: thin at the muzzle, thick toward the chamber (like real gun tubes)
        float Wall(float u) { float x = Mathf.Clamp01(u / Mathf.Max(Length, 0.1f)); return Bore * (0.17f + 0.25f * x * x); }
        /// steel outer radius (thermal sleeves and fume extractors are drawn bigger than the steel inside)
        float Ro(float u)
        {
            float ro = Ri + Wall(u), vis = Rvis(u);
            if (vis > Ri + 0.003f) ro = Mathf.Min(ro, vis);
            return Mathf.Max(ro, Ri + 0.3f * Wall(u));
        }
        float Lambda(float u) { float ro = Ro(u); return Steel * Mathf.PI * (ro * ro - Ri * Ri) * 1.12f; }   // + sleeve, fume extractor
        float TubeMass() { float m = 0f; for (float u = 0f; u < Length; u += 0.02f) m += Lambda(u) * 0.02f; return m; }

        bool Removed(Vector3 q, float u, float r, float phi, float ro)
        {
            foreach (var f in _feat)
            {
                switch (f.Kind)
                {
                    case FK.Perf:
                        {
                            Vector3 w = q - f.P;
                            float dist = (w - Vector3.Dot(w, f.D) * f.D).magnitude;
                            if (dist >= f.R) break;
                            if (f.Through) return true;
                            Vector2 d2 = new Vector2(f.D.x, f.D.y);
                            if (Vector2.Dot(new Vector2(q.x, q.y), -d2) > -0.2f * r) return true;   // entry wall only
                            break;
                        }
                    case FK.Gouge:
                        if ((q - f.P).magnitude < f.R && ro - r < f.Depth) return true;
                        break;
                    case FK.Blast:
                        if (Mathf.Abs(u - f.U) < f.Half && PartKit.AngleDiff(phi, f.Phi) < f.Arc && ro - r < f.Depth * (ro - Ri) + 1e-4f) return true;
                        break;
                }
            }
            return false;
        }

        /// plastic bending moment the section at u still carries for bending toward `dir` (and the intact value)
        float Capacity(float u, Vector2 dir, out float intact)
        {
            float ro = Ro(u), dr = (ro - Ri) / NRad, dphi = Mathf.PI * 2f / NPhi;
            var all = new List<(float y, float a)>(NPhi * NRad);
            var kept = new List<(float y, float a)>(NPhi * NRad);
            for (int j = 0; j < NPhi; j++)
            {
                float phi = (j + 0.5f) * dphi;
                float cs = Mathf.Cos(phi), sn = Mathf.Sin(phi);
                for (int k = 0; k < NRad; k++)
                {
                    float r = Ri + (k + 0.5f) * dr;
                    var q = new Vector3(r * cs, r * sn, -u);
                    float y = q.x * dir.x + q.y * dir.y, ar = r * dr * dphi;
                    all.Add((y, ar));
                    if (!Removed(q, u, r, phi, ro)) kept.Add((y, ar));
                }
            }
            intact = Plastic(all);
            return Plastic(kept);
        }

        static float Plastic(List<(float y, float a)> c)
        {
            if (c.Count == 0) return 0f;
            c.Sort((x, y) => x.y.CompareTo(y.y));
            float total = 0f;
            foreach (var e in c) total += e.a;
            float acc = 0f, y0 = c[c.Count - 1].y;
            foreach (var e in c) { acc += e.a; if (acc >= total * 0.5f) { y0 = e.y; break; } }
            float m = 0f;
            foreach (var e in c) m += e.a * Mathf.Abs(e.y - y0);
            return Yield * m;
        }

        /// muzzle part past a hinge at uh (mass, first and second moments about the hinge) and inertia of everything
        /// behind it about the mount
        void Beam(float uh, out float mf, out float s1, out float s2, out float iN)
        {
            mf = s1 = s2 = 0f;
            int n = 40;
            float du = uh / n;
            for (int i = 0; i < n; i++)
            {
                float u = (i + 0.5f) * du, l = Lambda(u) * du, w = uh - u;
                mf += l; s1 += l * w; s2 += l * w * w;
            }
            iN = 0f;
            float dx = (Length - uh) / n;
            for (int i = 0; i < n; i++)
            {
                float u = uh + (i + 0.5f) * dx, x = Length - u;
                iN += Lambda(u) * x * x * dx;
            }
            iN += 600f * Mathf.Pow(Bore / 0.125f, 3f);   // breech, cradle and recoiling parts behind the mantlet
        }

        // ------------------------------------------------------------------ a hit

        /// a round on the barrel's own hit zone (game's collider for it). positions in the drawn pose
        public void Hit(Vector3 pointW, Vector3 normalW, Vector3 dirW, bool pen, bool ric, AmmoType ammo, float speed, float holeR)
        {
            _wl = F.worldToLocalMatrix;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var part in _parts) if (part.R != null) part.Mc.Pose(part.R);
            float msPose = (float)sw.Elapsed.TotalMilliseconds;
            Vector3 p = Unbend(ToF(pointW));
            Vector3 d = F.InverseTransformDirection(dirW).normalized;
            float u = Mathf.Clamp(-p.z, 0.01f, Length - 0.02f);
            if (_cutU >= 0f && u < _cutU + 0.02f) return;      // that part of the tube is gone
            float phi = Mathf.Atan2(p.y, p.x);
            float rv = Rvis(u), ro = Ro(u), t = ro - Ri;
            Vector3 surf = new Vector3(Mathf.Cos(phi) * rv, Mathf.Sin(phi) * rv, -u);
            Vector3 nrm = new Vector3(Mathf.Cos(phi), Mathf.Sin(phi), 0f);
            if (Vector3.Dot(d, nrm) > 0f) d = -nrm;            // collider and drawn tube disagree: go straight in
            bool he = ammo.Category == AmmoType.AmmoCategory.Explosive;
            bool heat = ammo.Category == AmmoType.AmmoCategory.ShapedCharge;
            int seed = _seed++ * 7919 + Time.frameCount;

            // what the round does to the tube: material it takes out and the impulse it gives
            Vector3 J = Vector3.zero;          // impulse on the tube (N s), in F's space
            float jAtHinge = 1f;               // share applied at the hit station (rest spread over the blast's footprint)
            float half = 0.05f;                // axial half-length of the loaded zone
            float cut = 0f;                    // blast: fraction of what cutting the whole section takes
            string what = null;
            if (he || heat)
            {
                float W = Mathf.Max(ammo.TntEquivalentKg, 0.01f) * (heat ? 0.5f : 1f);
                Vector3 burst = surf + nrm * 0.05f;
                float dist = Mathf.Max(RXY(burst), ro + 0.01f);
                // shell case fragments (Gurney) and detonation products hitting the tube
                float mc = Mathf.Max(ammo.Mass - ammo.TntEquivalentKg, 0.1f * ammo.TntEquivalentKg + 0.02f);
                float cm = W / mc;
                float vFrag = 2440f * Mathf.Sqrt(cm / (1f + 0.5f * cm));
                float pTot = mc * vFrag + 0.5f * W * 2440f;
                float share = 2f * Mathf.Asin(Mathf.Clamp01(ro / dist)) / (2f * Mathf.PI) * 0.8f;
                J = -nrm * share * pTot * 1.3f;
                half = Mathf.Max(1.5f * (dist - ro) + ro, 0.08f);
                jAtHinge = 0f;
                // contact charge vs the section (FM 5-25 steel cutting: P = 3/8 A, lb and in2), shell not placed: ~35 %
                float aIn2 = Mathf.PI * (ro * ro - Ri * Ri) * 1550f;
                float pCut = 0.375f * aIn2 * 0.4536f;
                cut = 0.35f * W / Mathf.Max(pCut, 0.01f);
                float arc = Mathf.Min(Mathf.PI, Mathf.PI * Mathf.Pow(cut, 0.6f));
                float depth = Mathf.Min(1f, 2f * Mathf.Sqrt(cut));
                _feat.Add(new Feature { Kind = FK.Blast, U = u, Phi = phi, Arc = arc, Half = Mathf.Min(half, Mathf.Max(0.04f, arc * ro)), Depth = depth });
                float rho = Mathf.Clamp(arc * rv * 0.8f, 0.012f, 0.6f);
                if (depth >= 0.999f) AddWound(new Wound { Kind = WK.Tear, U = u, Phi = phi, Rho = rho, Seed = seed, Born = Time.time });
                else AddWound(new Wound { Kind = WK.Dent, U = u, Phi = phi, Rho = rho, Depth = depth * t, Seed = seed, Born = Time.time });
                what = $"blast {W:0.00} kg TNT: {cut * 100f:0}% of a cutting charge, {arc * Mathf.Rad2Deg * 2f:0} deg of the wall {(depth >= 0.999f ? "torn open" : $"{depth * 100f:0}% deep")}";
                if (heat && pen) AddPerforation(surf, d, Mathf.Max(holeR, 0.01f), true, WK.Jet, seed + 1, ref what);
            }
            else if (pen && !ric)
            {
                // long rod / AP shot through the tube: material in its path and the drag it pays going through
                float rodR = Mathf.Sqrt(Mathf.Max(ammo.SectionalArea, 1e-5f) / Mathf.PI);
                float r = Mathf.Max(rodR * 1.3f, 0.008f);
                bool through = AddPerforation(surf, d, r, true, WK.Entry, seed, ref what);
                float chord = (through ? 2f : 1f) * t / Mathf.Max(0.3f, -Vector3.Dot(d, nrm));
                float up = 0.6f * Mathf.Max(speed, 300f);
                J = d * (2f * 3.7e9f * Mathf.PI * rodR * rodR * chord / up);
            }
            else
            {
                // glanced off: a gouge plus the sideways push from turning the round
                float graze = Mathf.Asin(Mathf.Clamp01(-Vector3.Dot(d, nrm)));
                float r = Mathf.Max(holeR, 0.01f);
                float depth = Mathf.Min(0.6f * t, r);
                _feat.Add(new Feature { Kind = FK.Gouge, P = surf, R = r * 1.4f, Depth = depth });
                AddWound(new Wound { Kind = WK.Dent, U = u, Phi = phi, Rho = r * 1.4f, Depth = depth, Seed = seed, Born = Time.time });
                J = -nrm * ammo.Mass * Mathf.Max(speed, 300f) * 2f * Mathf.Sin(graze) * 0.5f;
                half = r * 2f;
                what = $"glanced off at {graze * Mathf.Rad2Deg:0} deg: gouge {depth * 1000f:0} mm deep";
            }

            // the swing: muzzle part vs the part behind the hit (two rigid links, hinged at the hit)
            Vector3 jp = new Vector3(J.x, J.y, 0f);
            float jm = jp.magnitude;
            Vector2 bdir = jm > 1e-4f ? new Vector2(jp.x, jp.y) / jm : new Vector2(-nrm.x, -nrm.y);
            Beam(u, out float mf, out float s1, out float s2, out float iN);
            float xs = Length - u;
            float jn = jm * (jAtHinge + (1f - jAtHinge) * 0.5f), jf = jm * (1f - jAtHinge) * 0.5f;
            float xn = xs - (1f - jAtHinge) * Mathf.Min(half * 0.5f, xs), wf = Mathf.Min(half * 0.5f, u);
            float M11 = iN + mf * xs * xs, M12 = xs * s1, M22 = Mathf.Max(s2, 1e-6f);
            float Q1 = jn * xn + jf * xs, Q2 = jf * wf;
            float det = Mathf.Max(M11 * M22 - M12 * M12, 1e-9f);
            float w1 = (M22 * Q1 - M12 * Q2) / det, w2 = (M11 * Q2 - M12 * Q1) / det;
            float ke = 0.5f * (M11 * w1 * w1 + 2f * M12 * w1 * w2 + M22 * w2 * w2);
            float ir = M11 + 2f * M12 + M22, pr = Q1 + Q2;
            float eh = Mathf.Max(0f, ke - pr * pr / (2f * ir));
            float mp = Capacity(u, bdir, out float mp0);
            float theta = mp > 1f ? eh / mp : 10f;
            float sgn = Mathf.Sign(w2 - w1);
            // its own weight on what's left
            Vector3 g = F.InverseTransformDirection(Vector3.down);
            Vector2 g2 = new Vector2(g.x, g.y);
            float mg = Gravity * s1 * g2.magnitude;
            float mpg = g2.sqrMagnitude > 1e-6f ? Capacity(u, g2.normalized, out _) : mp0;
            float droop = mpg < mg ? Mathf.Acos(Mathf.Clamp01(mpg / Mathf.Max(mg, 1e-3f))) : 0f;
            float frac = 0.12f + 0.55f * Mathf.Pow(Mathf.Clamp01(mp / Mathf.Max(mp0, 1f)), 1.5f);
            bool sever = cut >= 1f || mp < 2000f || theta + droop + _bentAngle > frac;

            string log = $"World barrel {Recorder.SafeName(U)}: {ammo.Name} at {u:0.00} m from the muzzle (tube {ro * 2000f:0} mm, wall {t * 1000f:0} mm): {what}; " +
                         $"impulse {jm:0} N s, swing energy {eh:0} J, section {mp / 1000f:0} of {mp0 / 1000f:0} kN m, muzzle part {mf:0} kg, " +
                         $"weight moment {mg / 1000f:0.0} kN m -> {(mp > 1f ? $"hinge {theta * Mathf.Rad2Deg:0.00} deg" : "nothing left to hold it")}{(droop > 0f ? $", droops {droop * Mathf.Rad2Deg:0} deg" : "")}, " +
                         $"tears at {frac * Mathf.Rad2Deg:0} deg: {(sever ? "BROKEN OFF" : theta + droop > 0.0035f ? "bent" : "holed, straight")}";
            HitCamMod.Log.Msg(log);

            try
            {
                WorldDamage.Sparks(pointW - dirW * 0.02f, Vector3.Lerp(F.TransformDirection(nrm), -dirW, 0.4f).normalized, he ? 160 : 70, he ? 24f : 16f, 50f, _layer);
                sw.Restart();
                CutWounds();
                float msCut = (float)sw.Elapsed.TotalMilliseconds; sw.Restart();
                if (sever) Sever(u, phi, bdir, w1, w2, xs, s1 / Mathf.Max(mf, 1e-3f), mf, eh, frac * mp, cut >= 1f);
                else if (theta + droop > 0.0035f) Bend(u, bdir, sgn * theta, g2, droop);
                float msShape = (float)sw.Elapsed.TotalMilliseconds; sw.Restart();
                Rebuild();
                HitCamMod.Log.Msg($"World barrel {Recorder.SafeName(U)}: ms pose {msPose:0.0}, holes {msCut:0.0}, {(sever ? "break" : "bend")} {msShape:0.0}, meshes {sw.Elapsed.TotalMilliseconds:0.0}");
            }
            catch (Exception e) { HitCamMod.Log.Warning("World barrel: " + e); }
        }

        /// new wound, or a bigger one where it overlaps an existing one: hits close together read as one torn patch
        /// (before, each one's ragged rim drew a chain of rings around the barrel). stronger kind wins (dent < hole < tear)
        void AddWound(Wound w)
        {
            float r = Mathf.Max(Rvis(w.U), 0.01f);
            for (int i = 0; i < _wounds.Count; i++)
            {
                var o = _wounds[i];
                float du = o.U - w.U, ds = Mathf.DeltaAngle(o.Phi * Mathf.Rad2Deg, w.Phi * Mathf.Rad2Deg) * Mathf.Deg2Rad * r;
                if (du * du + ds * ds > Sq((o.Rho + w.Rho) * 0.75f)) continue;
                float a = o.Rho * o.Rho, b = w.Rho * w.Rho;
                o.U = (o.U * a + w.U * b) / (a + b);
                o.Phi = o.Phi - ds / r * b / (a + b);
                o.Rho = Mathf.Min(Mathf.Max(o.Rho, w.Rho) + 0.45f * Mathf.Min(o.Rho, w.Rho), 0.6f);
                if (Rank(w.Kind) > Rank(o.Kind)) o.Kind = w.Kind;
                o.Depth = Mathf.Max(o.Depth, w.Depth);
                o.Born = w.Born;
                o.Cut = false;   // cut again, bigger
                _wounds[i] = o;
                return;
            }
            _wounds.Add(w);
        }

        static float Sq(float x) => x * x;
        static int Rank(WK k) => k == WK.Dent ? 0 : k == WK.Tear ? 2 : 1;

        /// round going through: hole where it went in and where it came out of the far wall
        bool AddPerforation(Vector3 surf, Vector3 d, float r, bool pen, WK kind, int seed, ref string what)
        {
            float u = -surf.z, phi = Mathf.Atan2(surf.y, surf.x);
            // where the line leaves the tube (2D, tube runs along z)
            Vector2 p2 = new Vector2(surf.x, surf.y), d2 = new Vector2(d.x, d.y);
            float a = Vector2.Dot(d2, d2), b = 2f * Vector2.Dot(p2, d2), rv = Rvis(u);
            float c = Vector2.Dot(p2, p2) - rv * rv;
            float disc = b * b - 4f * a * c;
            bool through = false;
            Vector3 exit = surf;
            if (a > 1e-4f && disc > 0f)
            {
                float tEx = (-b + Mathf.Sqrt(disc)) / (2f * a);
                if (tEx > 0.005f) { exit = surf + d * tEx; through = pen; }
            }
            float ue = -exit.z;
            if (ue < 0f || ue > Length) through = false;
            _feat.Add(new Feature { Kind = FK.Perf, P = surf, D = d, R = r, Through = through });
            AddWound(new Wound { Kind = kind, U = u, Phi = phi, Rho = r, Seed = seed, Born = Time.time });
            if (through) AddWound(new Wound { Kind = kind == WK.Jet ? WK.Jet : WK.Exit, U = ue, Phi = Mathf.Atan2(exit.y, exit.x), Rho = r * (kind == WK.Jet ? 1.1f : 1.35f), Seed = seed + 17, Born = Time.time });
            what = (what != null ? what + "; " : "") + $"{(kind == WK.Jet ? "jet" : "shot")} hole {r * 2000f:0} mm{(through ? $", out of the far wall {Mathf.Abs(ue - u) * 1000f:0} mm along" : "")}";
            return through;
        }

        // ------------------------------------------------------------------ the tube's geometry

        static Vector3 Cyl(float u, float phi, float r) => new Vector3(Mathf.Cos(phi) * r, Mathf.Sin(phi) * r, -u);

        /// ragged outline of a wound: its radius at angle a around its centre (on the tube surface)
        static float Jag(Wound w, float a)
        {
            int k = Mathf.FloorToInt(Mathf.Repeat(a, Mathf.PI * 2f) / (Mathf.PI * 2f) * 20f);
            float f = Mathf.Repeat(a, Mathf.PI * 2f) / (Mathf.PI * 2f) * 20f - k;
            float h0 = PartKit.Hash(w.Seed, k), h1 = PartKit.Hash(w.Seed, (k + 1) % 20);
            float h = Mathf.Lerp(h0, h1, f);
            switch (w.Kind)
            {
                case WK.Tear: return 0.6f + 0.95f * h * h;
                case WK.Exit: return 0.75f + 0.5f * h;
                case WK.Dent: return 0.85f + 0.3f * h;
                default: return 0.84f + 0.3f * h;
            }
        }

        /// surface distance of a point on the tube (F space) from a wound's centre, in units of its ragged radius
        float InWound(Wound w, Vector3 q)
        {
            float u = -q.z, phi = Mathf.Atan2(q.y, q.x);
            float du = u - w.U;
            float dt = (Mathf.Repeat(phi - w.Phi + Mathf.PI, Mathf.PI * 2f) - Mathf.PI) * Rvis(w.U);
            float dist = Mathf.Sqrt(du * du + dt * dt);
            return dist / Mathf.Max(w.Rho * Jag(w, Mathf.Atan2(dt, du)), 1e-4f);
        }

        /// cuts every wound into the tube mesh: triangles near it get split until small, the ones inside go
        void CutWounds()
        {
            foreach (var p in _parts)
            {
                var mc = p.Mc;
                if (mc.W == null || mc.W.Count != mc.Count) mc.Pose(p.R);
                foreach (var w in _wounds)
                {
                    if (w.Cut || (w.Kind == WK.Dent && w.Depth < 0.002f)) continue;
                    Vector3 c = MapBent(Cyl(w.U, w.Phi, Rvis(w.U)));
                    float reach = w.Rho * 2.2f + 0.01f;
                    float small = Mathf.Max(w.Rho * 0.35f, 0.004f);
                    mc.Subdivide((A, B, C) =>
                    {
                        Vector3 a = ToF(A), b = ToF(B), cc = ToF(C);
                        float e = Mathf.Max((a - b).sqrMagnitude, Mathf.Max((b - cc).sqrMagnitude, (cc - a).sqrMagnitude));
                        if (e < small * small) return false;
                        if (PartKit.PointTri(c, a, b, cc) > reach) return false;
                        return OnTube(Unbend(a), 1.6f) || OnTube(Unbend(b), 1.6f) || OnTube(Unbend(cc), 1.6f);
                    }, 9, mc.Count + 60000);
                    mc.RemoveTris((ia, ib, ic) =>
                    {
                        Vector3 m = Unbend(ToF((mc.W[ia] + mc.W[ib] + mc.W[ic]) / 3f));
                        if (!OnTube(m, 1.6f)) return false;
                        return InWound(w, m) < (w.Kind == WK.Dent ? 1.0f : 1.08f);
                    });
                }
            }
            for (int i = 0; i < _wounds.Count; i++) { var w = _wounds[i]; w.Cut = true; _wounds[i] = w; }
        }

        /// bends the muzzle part about a plastic hinge at uh: smooth over about a tube diameter, wall on the inside of
        /// the bend buckled outward
        void Bend(float uh, Vector2 bdir, float thetaImpulse, Vector2 g2, float droop)
        {
            Vector3 z = Vector3.forward;
            Quaternion qb = Quaternion.AngleAxis(thetaImpulse * Mathf.Rad2Deg, Vector3.Cross(z, new Vector3(bdir.x, bdir.y, 0f)).normalized);
            Quaternion qg = g2.sqrMagnitude > 1e-6f && droop > 0f
                ? Quaternion.AngleAxis(droop * Mathf.Rad2Deg, Vector3.Cross(z, new Vector3(g2.x, g2.y, 0f)).normalized) : Quaternion.identity;
            Quaternion q = qg * qb;
            if (_bent)
            {
                // a second bend near the first adds to it, anywhere else the tube is already wrecked so leave it
                if (Mathf.Abs(uh - _bendU) > 0.5f) return;
                q = q * _bendQ;
                uh = _bendU;
            }
            float wz = Mathf.Max(0.8f * Ro(uh), 0.03f);
            Quaternion step = _bent ? q * Quaternion.Inverse(_bendQ) : q;
            Vector3 pivot = new Vector3(0f, 0f, -uh);
            Vector3 inside = step * Vector3.forward - Vector3.forward;
            inside.z = 0f;
            Vector2 ins = new Vector2(inside.x, inside.y).sqrMagnitude > 1e-8f ? new Vector2(inside.x, inside.y).normalized : Vector2.zero;
            float ang = Quaternion.Angle(Quaternion.identity, step) * Mathf.Deg2Rad;
            foreach (var p in _parts)
            {
                var mc = p.Mc;
                if (!_bent)
                    foreach (float off in new[] { -wz, -wz * 0.5f, 0f, wz * 0.5f, wz })
                    {
                        Vector3 nW = F.forward;
                        float k = Vector3.Dot(nW, F.TransformPoint(new Vector3(0f, 0f, -uh + off)));
                        mc.Slice(nW, k, (a, b, c) => OnTube(ToF(mc.W[a]), 1.6f) && OnTube(ToF(mc.W[b]), 1.6f) && OnTube(ToF(mc.W[c]), 1.6f));
                    }
                for (int v = 0; v < mc.Count; v++)
                {
                    Vector3 qv = ToF(mc.W[v]);
                    Vector3 qu = Unbend(qv);
                    if (!OnTube(qu, 1.6f)) continue;
                    float w = uh - (-qu.z);                    // + toward the muzzle
                    if (w < -wz) continue;
                    float s = PartKit.Smooth01((w + wz) / (2f * wz));
                    // buckle on the inside of the bend
                    float r = RXY(qv);
                    if (r > 1e-4f && ins != Vector2.zero)
                    {
                        float bump = Mathf.Max(0f, 1f - (w / wz) * (w / wz));
                        float side = Mathf.Max(0f, Vector2.Dot(new Vector2(qv.x, qv.y) / r, ins));
                        float push = 0.18f * ang * Ro(uh) * bump * bump * side;
                        qv += new Vector3(qv.x / r, qv.y / r, 0f) * push;
                    }
                    Quaternion qs = Quaternion.Slerp(Quaternion.identity, step, s);
                    Vector3 moved = pivot + qs * (qv - pivot);
                    mc.MoveTo(v, F.TransformPoint(moved), F.rotation * qs * Quaternion.Inverse(F.rotation));
                }
            }
            _bent = true;
            _bendU = uh;
            _bendW = wz;
            _bendQ = q;
            _bentAngle = Quaternion.Angle(Quaternion.identity, q) * Mathf.Deg2Rad;
        }

        /// where a point of the straight tube is now (bend applied)
        Vector3 MapBent(Vector3 q)
        {
            if (!_bent) return q;
            float w = _bendU - (-q.z);
            if (w < -_bendW) return q;
            float s = PartKit.Smooth01((w + _bendW) / (2f * _bendW));
            var pivot = new Vector3(0f, 0f, -_bendU);
            return pivot + Quaternion.Slerp(Quaternion.identity, _bendQ, s) * (q - pivot);
        }

        /// where a point of the bent tube was on the straight one
        Vector3 Unbend(Vector3 q)
        {
            if (!_bent) return q;
            var pivot = new Vector3(0f, 0f, -_bendU);
            Vector3 back = pivot + Quaternion.Inverse(_bendQ) * (q - pivot);
            float w = _bendU - (-back.z);
            if (w < -_bendW) return q;
            float s = PartKit.Smooth01((w + _bendW) / (2f * _bendW));
            return pivot + Quaternion.Inverse(Quaternion.Slerp(Quaternion.identity, _bendQ, s)) * (q - pivot);
        }

        /// tube tears through at uh and the muzzle end comes off as a physics body with what's left of the swing.
        /// tear runs in ragged tongues round the tube (~0.9 of a diameter either way when a blast tore it, ~0.35 when the
        /// swing overloaded a holed section); near the tear both broken ends get peeled back and flared out (pushed in on the
        /// side the shell burst on), so the wall shows its thickness, sooty inside and a bright torn rim
        void Sever(float uh, float phiHit, Vector2 bdir, float w1, float w2, float xs, float cmFromHinge, float mf, float eh, float eTear, bool blown)
        {
            float D = 2f * Rvis(uh);
            _cutU = uh;
            _cutSeed = _seed * 31 + 5;
            _blown = blown;
            _cutBorn = Time.time;
            _cutPhi = phiHit;
            _tongue = (blown ? 0.9f : 0.35f) * D;
            _peel = (blown ? 0.6f : 0.25f) * D;
            _flare = (blown ? 0.3f : 0.07f) * D;
            float zLo = uh - _tongue - _peel - 0.03f, zHi = uh + _tongue + _peel + 0.03f;
            bool InZone(Vector3 q) { float u = -q.z; return u > zLo && u < zHi && OnTube(q, 1.6f); }
            Vector3 cmL = MapBent(new Vector3(0f, 0f, -Mathf.Max(uh - cmFromHinge, uh * 0.5f)));
            Vector3 cmW = F.TransformPoint(cmL);
            Quaternion rotW = F.rotation * (_bent ? _bendQ : Quaternion.identity);
            var w2l = Matrix4x4.TRS(cmW, rotW, Vector3.one).inverse;
            var pieces = new List<(Renderer src, Mesh mesh)>();
            Mesh farSkin = null;
            var diag = new List<string>();
            foreach (var p in _parts)
            {
                var mc = p.Mc;
                // small triangles over the torn zone so the ragged line and the peel have something to bend
                float small = Mathf.Max(D / 10f, 0.008f);
                mc.Subdivide((A, B, C) =>
                {
                    Vector3 a = Unbend(ToF(A)), b = Unbend(ToF(B)), c = Unbend(ToF(C));
                    float e = Mathf.Max((a - b).sqrMagnitude, Mathf.Max((b - c).sqrMagnitude, (c - a).sqrMagnitude));
                    if (e < small * small) return false;
                    float lo = Mathf.Min(-a.z, Mathf.Min(-b.z, -c.z)), hi = Mathf.Max(-a.z, Mathf.Max(-b.z, -c.z));
                    if (hi < zLo || lo > zHi) return false;
                    return OnTube(a, 1.6f) || OnTube(b, 1.6f) || OnTube(c, 1.6f);
                }, 10, mc.Count + 80000);
                // each triangle goes to the side of the tear its middle is on
                var far = new List<int>[mc.Tris.Length];
                var near = new List<int>[mc.Tris.Length];
                int farN = 0, nearTube = 0;
                for (int s = 0; s < mc.Tris.Length; s++)
                {
                    far[s] = new List<int>();
                    near[s] = new List<int>();
                    var tl = mc.Tris[s];
                    for (int i = 0; i + 2 < tl.Count; i += 3)
                    {
                        Vector3 m = Unbend(ToF((mc.W[tl[i]] + mc.W[tl[i + 1]] + mc.W[tl[i + 2]]) / 3f));
                        bool isFar = -m.z > -0.15f && -m.z < BreakU(Mathf.Atan2(m.y, m.x)) && (-m.z < zLo ? RXY(m) < 0.35f : OnTube(m, 1.6f));
                        var dst = isFar ? far[s] : near[s];
                        dst.Add(tl[i]); dst.Add(tl[i + 1]); dst.Add(tl[i + 2]);
                        if (isFar) farN++;
                        else if (-m.z < uh - _tongue - 0.02f && RXY(m) < 0.4f) nearTube++;
                    }
                }
                diag.Add($"{p.R.name}: {farN} triangles leave, {near.Sum(l => l.Count) / 3} stay ({nearTube} of them muzzle-side of the tear, within 0.4 m of the axis)");
                if (farN == 0) { mc.Tris = near; continue; }
                // muzzle end's own torn lip: peeled, copied out, then the stump's vertices get put back
                var keepData = mc.Data.Select(d => (byte[])d.Clone()).ToArray();
                var keepW = new List<Vector3>(mc.W);
                var farV = MeshCopy.Used(far);
                foreach (int v in farV) Peel(mc, v, +1);
                pieces.Add((p.R, mc.BuildStatic("HitCam barrel end " + p.R.name, far, w2l)));
                var fs = TearSkin(mc, far, +1, w2l * F.localToWorldMatrix);
                if (fs != null) farSkin = fs;
                for (int s = 0; s < mc.Data.Length; s++) Buffer.BlockCopy(keepData[s], 0, mc.Data[s], 0, keepData[s].Length);
                for (int i = 0; i < keepW.Count; i++) mc.W[i] = keepW[i];
                // the stump's
                foreach (int v in MeshCopy.Used(near)) Peel(mc, v, -1);
                mc.Tris = near;
                var ns = TearSkin(mc, near, -1, Matrix4x4.identity);
                if (ns != null) { if (_tear != null) UnityEngine.Object.Destroy(_tear); _tear = ns; }
            }
            _wounds.RemoveAll(w => w.U < zHi);

            // the end that flies off: what's left of its swing plus the vehicle's motion
            var rb = U.GetComponent<Rigidbody>();
            Vector3 vVeh = rb != null ? rb.GetPointVelocity(cmW) : Vector3.zero;
            float keep = Mathf.Sqrt(Mathf.Clamp01(1f - eTear / Mathf.Max(eh, 1f)) * 0.9f + 0.1f);
            Vector3 b3 = F.TransformDirection(new Vector3(bdir.x, bdir.y, 0f));
            Vector3 vel = b3 * (xs * w1 + cmFromHinge * w2) * keep + vVeh;
            Vector3 om = F.TransformDirection(Vector3.Cross(Vector3.forward, new Vector3(bdir.x, bdir.y, 0f))) * w2 * keep;
            om = Vector3.ClampMagnitude(om, 15f);     // a 1-2 m steel tube doesn't spin much faster than that and stay in one piece
            if (blown) vel += F.TransformDirection(Vector3.forward) * 2f;
            if (pieces.Count > 0)
            {
                var go = WorldModules.SpawnBody("HitCam barrel end", cmW, rotW, vel, om + UnityEngine.Random.insideUnitSphere * 0.3f, Mathf.Max(mf, 5f), 900f);
                foreach (var (src, mesh) in pieces)
                {
                    var child = PartKit.Child(go.transform, mesh.name, mesh, src.sharedMaterials, WorldDamage.DebrisLayer);
                    PartKit.CopyLook(src, child.GetComponent<MeshRenderer>());
                }
                var cap = go.AddComponent<CapsuleCollider>();
                cap.direction = 2;
                cap.radius = Mathf.Max(Rvis(uh * 0.5f), 0.02f);
                cap.height = Mathf.Max(uh, 2f * cap.radius + 0.01f);
                cap.center = go.transform.InverseTransformPoint(F.TransformPoint(MapBent(new Vector3(0f, 0f, -uh * 0.5f))));
                if (farSkin != null)
                {
                    PartKit.Child(go.transform, "HitCam torn end", farSkin, new[] { WorldModules.Burnt, WorldModules.Bare, WorldModules.Dark }, WorldDamage.DebrisLayer);
                    if (!blown) AddGlow(go.transform, farSkin, 1, 4f, _cutBorn);
                }
                go.AddComponent<PieceLog>().Init("barrel end", 3f);
            }
            HitCamMod.Log.Msg($"World barrel {Recorder.SafeName(U)}: muzzle end ({uh:0.00} m, {mf:0} kg) breaks off at {(vel - vVeh).magnitude:0.0} m/s, {om.magnitude:0.0} rad/s; " +
                              $"tear tongues +-{_tongue * 1000f:0} mm, peeled {_peel * 1000f:0} mm, flared {_flare * 1000f:0} mm; " + string.Join("; ", diag));
        }

        /// where the tear runs at angle phi round the tube (m from the muzzle): ragged teeth either side of the break
        float BreakU(float phi)
        {
            int k = 5 + (int)(PartKit.Hash(_cutSeed, 77) * 3f);
            float x = Mathf.Repeat(phi, Mathf.PI * 2f) / (Mathf.PI * 2f) * 2f * k;
            int i = Mathf.FloorToInt(x);
            float f = x - i;
            float H(int j) { j = ((j % (2 * k)) + 2 * k) % (2 * k); return ((j & 1) == 0 ? 1f : -1f) * (0.3f + 0.7f * PartKit.Hash(_cutSeed, j)) * _tongue; }
            return _cutU + Mathf.Lerp(H(i), H(i + 1), f);
        }

        /// how far a point on one broken end is from the tear (side -1: stump, toward the mantlet; +1: muzzle end)
        float FromTear(Vector3 q, int side) => side < 0 ? -q.z - BreakU(Mathf.Atan2(q.y, q.x)) : BreakU(Mathf.Atan2(q.y, q.x)) - (-q.z);

        /// peeled lip: near the tear the wall bends outward (pushed in where the shell burst) and curls back
        float PeelR(float e, float phi)
        {
            float s = Mathf.Clamp01(1f - e / Mathf.Max(_peel, 1e-3f));
            float outward = _flare * s * s * (0.6f + 0.8f * PartKit.Hash(_cutSeed + 9, Mathf.FloorToInt(Mathf.Repeat(phi, Mathf.PI * 2f) * 3f)));
            if (!_blown) return outward;
            float crush = PartKit.Smooth01(1f - PartKit.AngleDiff(phi, _cutPhi) / (Mathf.PI / 3f));
            return Mathf.Lerp(outward, -0.7f * _flare * s * s, crush);
        }

        void Peel(MeshCopy mc, int v, int side)
        {
            Vector3 q = Unbend(ToF(mc.W[v]));
            if (!OnTube(q, 1.6f)) return;
            float e = FromTear(q, side);
            if (e < -0.01f || e > _peel) return;
            e = Mathf.Max(e, 0f);
            float r = RXY(q);
            if (r < 1e-4f) return;
            float phi = Mathf.Atan2(q.y, q.x);
            float dr = PeelR(e, phi), dr2 = PeelR(e + 0.005f, phi);
            float s = Mathf.Clamp01(1f - e / Mathf.Max(_peel, 1e-3f));
            // curls back away from the tear
            float du = side * -1f * 0.3f * Mathf.Abs(dr) * s;
            Vector3 radial = new Vector3(q.x / r, q.y / r, 0f);
            Vector3 moved = q + radial * dr + Vector3.forward * -du;
            // surface tilts with the flare: normal leans back from the tear
            float slope = (dr - dr2) / 0.005f;
            Vector3 along = side < 0 ? Vector3.forward : Vector3.back;      // toward the tear
            Vector3 n = (radial - along * slope).normalized;
            Quaternion rot = F.rotation * Quaternion.FromToRotation(radial, n) * Quaternion.Inverse(F.rotation);
            mc.MoveTo(v, F.TransformPoint(MapBent(moved)), rot);
        }

        /// torn end's own surfaces from its triangles in the torn zone: sooty inside of the wall (submesh 0, outer skin
        /// moved in by wall thickness, facing in) and the bright torn rim joining the two along the tear (submesh 1).
        /// built in F space, then through `toSpace`
        Mesh TearSkin(MeshCopy mc, List<int>[] tris, int side, Matrix4x4 toSpace)
        {
            var b = new PartKit.Builder(3);
            var inner = new Dictionary<int, int>();
            var outer = new Dictionary<int, int>();
            var edges = new Dictionary<long, int>();
            var zone = new List<(int a, int c, int d)>();
            float reach = _tongue + _peel;
            Vector3 Q(int v) => ToF(mc.W[v]);
            foreach (var tl in tris)
                for (int i = 0; i + 2 < tl.Count; i += 3)
                {
                    int a = tl[i], c = tl[i + 1], d = tl[i + 2];
                    Vector3 m = Unbend((Q(a) + Q(c) + Q(d)) / 3f);
                    if (!OnTube(m, 1.8f) || FromTear(m, side) > reach) continue;
                    zone.Add((a, c, d));
                    foreach (var (x, y) in new[] { (a, c), (c, d), (d, a) })
                    {
                        long key = x < y ? ((long)x << 32) | (uint)y : ((long)y << 32) | (uint)x;
                        edges[key] = edges.TryGetValue(key, out int cnt) ? cnt + 1 : 1;
                    }
                }
            if (zone.Count == 0) return null;
            int Out(int v)
            {
                if (outer.TryGetValue(v, out int i)) return i;
                return outer[v] = b.Add(Q(v));
            }
            int In(int v)
            {
                if (inner.TryGetValue(v, out int i)) return i;
                Vector3 q = Q(v), qu = Unbend(q);
                float r = RXY(qu);
                float wall = Mathf.Max(Rvis(-qu.z) - Ri, 0.006f);
                Vector3 radial = r > 1e-4f ? new Vector3(qu.x / r, qu.y / r, 0f) : Vector3.zero;
                return inner[v] = b.Add(MapBent(qu - radial * wall));
            }
            foreach (var (a, c, d) in zone)
            {
                Vector3 m = Unbend((Q(a) + Q(c) + Q(d)) / 3f);
                float r = RXY(m);
                Vector3 toAxis = r > 1e-4f ? -new Vector3(m.x, m.y, 0f) / r : Vector3.zero;
                b.TriO(0, In(a), In(c), In(d), toAxis);
            }
            // rim: zone edges that lie along the tear (the others join the rest of the tube)
            foreach (var (a, c, d) in zone)
                foreach (var (x, y) in new[] { (a, c), (c, d), (d, a) })
                {
                    long key = x < y ? ((long)x << 32) | (uint)y : ((long)y << 32) | (uint)x;
                    if (edges[key] != 1) continue;
                    Vector3 mid = Unbend((Q(x) + Q(y)) * 0.5f);
                    if (FromTear(mid, side) > _peel * 0.5f) continue;
                    Vector3 want = side < 0 ? Vector3.forward : Vector3.back;
                    b.QuadO(1, Out(x), Out(y), In(y), In(x), want);
                }
            // dark plug some way down the bore so the broken end isn't see-through
            float uPlug = side < 0 ? _cutU + _tongue + _peel + 0.25f : _cutU - _tongue - _peel - 0.15f;
            if (uPlug > 0.02f && uPlug < Length)
            {
                const int NP = 28;
                int c0 = b.Add(MapBent(Cyl(uPlug, 0f, 0f)));
                var ring = new int[NP];
                for (int j = 0; j < NP; j++) ring[j] = b.Add(MapBent(Cyl(uPlug, j * Mathf.PI * 2f / NP, Ri)));
                for (int j = 0; j < NP; j++) b.Tri(2, c0, ring[j], ring[(j + 1) % NP]);
                // and the bore wall between the torn zone and the plug
                float uFrom = side < 0 ? _cutU + _tongue + _peel : _cutU - _tongue - _peel;
                const int NU = 6;
                var rows = new int[NU + 1, NP];
                for (int i = 0; i <= NU; i++)
                    for (int j = 0; j < NP; j++) rows[i, j] = b.Add(MapBent(Cyl(Mathf.Lerp(uFrom, uPlug, i / (float)NU), j * Mathf.PI * 2f / NP, Ri)));
                for (int i = 0; i < NU; i++)
                    for (int j = 0; j < NP; j++) b.Quad(2, rows[i, j], rows[i, (j + 1) % NP], rows[i + 1, (j + 1) % NP], rows[i + 1, j]);
            }
            b.Map(p => toSpace.MultiplyPoint3x4(p));
            return b.Build("HitCam torn barrel end");
        }

        static Mesh TransformMesh(Mesh m, Matrix4x4 mx)
        {
            var v = m.vertices;
            for (int i = 0; i < v.Length; i++) v[i] = mx.MultiplyPoint3x4(v[i]);
            m.vertices = v;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        // ------------------------------------------------------------------ drawing it

        /// rebuilds the tube meshes and everything drawn around the wounds
        void Rebuild()
        {
            foreach (var p in _parts)
            {
                var old = p.Mine;
                p.Mine = p.Mc.Build(p.R.name + " (HitCam barrel)", p.Mc.Tris);
                PartKit.SetMesh(p.R, p.Mine);
                if (p.R is SkinnedMeshRenderer smr) smr.updateWhenOffscreen = true;
                if (old != null) UnityEngine.Object.Destroy(old);
                WorldDamage.ForgetCut(p.R);
                if (AutoTest.Active)
                {
                    int bw = 0;
                    try { bw = p.Mine.GetAllBoneWeights().Length; } catch { }
                    HitCamMod.Log.Msg($"World barrel: {p.R.name} now draws {(PartKit.MeshOf(p.R) == p.Mine ? "the cut mesh" : "SOMETHING ELSE")}: {p.Mine.vertexCount} v, " +
                                      $"{p.Mc.Tris.Sum(l => l.Count) / 3} triangles, {p.Mine.bindposes.Length} bind poses, {bw} bone weights");
                }
            }
            if (_fx != null) UnityEngine.Object.Destroy(_fx);
            _fx = new GameObject("HitCam barrel damage") { layer = _layer };
            _fx.transform.SetParent(F, false);
            float uLo = float.MaxValue, uHi = float.MinValue;
            foreach (var w in _wounds)
            {
                var b = WoundMesh(w);
                if (b == null || b.Empty) continue;
                var m = b.Build("HitCam barrel wound");
                PartKit.Child(_fx.transform, "wound", m, new[] { WorldDamage.Metal, WorldModules.Bare, WorldModules.Burnt }, _layer);
                if (w.Kind != WK.Dent) { uLo = Mathf.Min(uLo, w.U - w.Rho * 2f - 0.1f); uHi = Mathf.Max(uHi, w.U + w.Rho * 2f + 0.1f); }
                var soot = SootMesh(w);
                if (soot != null) PartKit.Child(_fx.transform, "soot", soot, new[] { WorldDamage.SootMat }, _layer, false);
                if (w.Kind == WK.Entry || w.Kind == WK.Exit || w.Kind == WK.Jet) AddGlow(_fx.transform, m, 1, 3.5f, w.Born);
            }
            if (_cutU >= 0f && _tear != null)
            {
                PartKit.Child(_fx.transform, "torn end", _tear, new[] { WorldModules.Burnt, WorldModules.Bare, WorldModules.Dark }, _layer);
                if (!_blown) AddGlow(_fx.transform, _tear, 1, 4f, _cutBorn);
            }
            if (uHi > uLo)
            {
                uLo = Mathf.Max(uLo, _cutU >= 0f ? _cutU + _tongue + _peel : 0.005f);
                uHi = Mathf.Min(uHi, Length - 0.01f);
                var bore = BoreMesh(uLo, uHi);
                if (bore != null) PartKit.Child(_fx.transform, "bore", bore, new[] { WorldModules.Dark }, _layer, false);
            }
        }

        /// hot glow over a wound's fresh edges (submesh `sub` of the mesh), cools down `seconds` after `born`
        static void AddGlow(Transform parent, Mesh src, int sub, float seconds, float born)
        {
            float left = seconds - (Time.time - born);
            if (src == null || sub >= src.subMeshCount || left < 0.2f) return;
            var tris = src.GetTriangles(sub);
            if (tris.Length == 0) return;
            var m = new Mesh { name = "HitCam hot edge" };
            m.vertices = src.vertices;
            var cols = new Color[src.vertexCount];
            for (int i = 0; i < cols.Length; i++) cols[i] = Color.white;
            m.colors = cols;
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            var go = PartKit.Child(parent, "hot", m, new[] { WorldDamage.GlowMat }, parent.gameObject.layer, false);
            float k = left / seconds;
            go.AddComponent<Cool>().Init(go.GetComponent<MeshRenderer>(), new Color(0.75f, 0.22f, 0.05f) * k, left);
        }

        /// wound around a hole: ragged lip or outward petals (submesh 0, scorched steel), the fresh cut face down through
        /// the wall (submesh 1, bright steel) and for blasts the blackened inward-bent petals (submesh 2).
        /// built on the straight tube, then bent with it
        PartKit.Builder WoundMesh(Wound w)
        {
            if (_cutU >= 0f && w.U < _cutU) return null;
            var b = new PartKit.Builder(3);
            const int K = 24;
            float ro = Ro(w.U), t = ro - Ri;
            const float lift = 0.0015f;
            float A(int k) => k * Mathf.PI * 2f / K;
            float R(int k) => w.Rho * Jag(w, A(k));
            // point near the wound: rad from its centre on the surface at angle a, at radius r from the axis
            // (NaN: on the drawn surface)
            Vector3 P(float a, float rad, float r)
            {
                float du = Mathf.Cos(a) * rad, dt = Mathf.Sin(a) * rad;
                float u = w.U + du, rvu = Rvis(u);
                return Cyl(u, w.Phi + dt / Mathf.Max(rvu, 1e-3f), float.IsNaN(r) ? rvu + lift : r);
            }
            Vector3 Out(int i) { var v = b.V[i]; return new Vector3(v.x, v.y, 0f); }
            Vector3 centre = Cyl(w.U, w.Phi, 0f);
            Vector3 radial = new Vector3(Mathf.Cos(w.Phi), Mathf.Sin(w.Phi), 0f);
            Vector3 ToHole(int i) { var v = b.V[i] - centre; return -(v - Vector3.Dot(v, radial) * radial); }
            float rv = Rvis(w.U);
            switch (w.Kind)
            {
                case WK.Entry:
                case WK.Jet:
                    {
                        // raised rolled lip (thick wall, fast round) and the cut face down to the bore
                        float lip = w.Kind == WK.Jet ? 0.05f : 0.14f;
                        var o = new int[K]; var l = new int[K]; var e = new int[K]; var e2 = new int[K]; var bt = new int[K];
                        for (int k = 0; k < K; k++)
                        {
                            float rk = R(k), a = A(k);
                            o[k] = b.Add(P(a, rk * 1.75f, float.NaN));
                            l[k] = b.Add(P(a, rk * 1.22f, rv + lip * w.Rho));
                            e[k] = b.Add(P(a, rk, rv + lip * 0.3f * w.Rho));
                            e2[k] = b.Add(b.V[e[k]]);
                            bt[k] = b.Add(P(a, rk * 0.93f, Ri - 0.002f));
                        }
                        for (int k = 0; k < K; k++)
                        {
                            int n = (k + 1) % K;
                            b.QuadO(0, o[k], o[n], l[n], l[k], Out(o[k]));
                            b.QuadO(0, l[k], l[n], e[n], e[k], Out(l[k]));
                            b.QuadO(1, e2[k], e2[n], bt[n], bt[k], ToHole(e2[k]));
                        }
                        break;
                    }
                case WK.Exit:
                    {
                        // torn outward: petals bent back the way the round went, ragged tears between them
                        var o = new int[K]; var e = new int[K]; var e2 = new int[K]; var bt = new int[K];
                        for (int k = 0; k < K; k++)
                        {
                            float rk = R(k), a = A(k);
                            o[k] = b.Add(P(a, rk * 1.6f, float.NaN));
                            e[k] = b.Add(P(a, rk, rv + 0.004f));
                            e2[k] = b.Add(b.V[e[k]]);
                            bt[k] = b.Add(P(a, rk * 0.9f, Ri - 0.002f));
                        }
                        for (int k = 0; k < K; k++)
                        {
                            int n = (k + 1) % K;
                            b.QuadO(0, o[k], o[n], e[n], e[k], Out(o[k]));
                            b.QuadO(1, e2[k], e2[n], bt[n], bt[k], ToHole(e2[k]));
                        }
                        int petals = 4 + (int)(PartKit.Hash(w.Seed, 99) * 3f);
                        for (int i = 0; i < petals; i++)
                        {
                            int k0 = (int)(i * K / (float)petals + PartKit.Hash(w.Seed, i) * 2f) % K;
                            int k1 = (k0 + 2 + (int)(PartKit.Hash(w.Seed, i + 40) * 2f)) % K;
                            float am = A(k0) + Mathf.Repeat(A(k1) - A(k0), Mathf.PI * 2f) * 0.5f;
                            float len = w.Rho * (0.55f + 0.5f * PartKit.Hash(w.Seed, i + 80));
                            int a0 = b.Add(b.V[e[k0]]), a1 = b.Add(b.V[e[k1]]);
                            int tip = b.Add(P(am, R(k0) * 0.75f, rv + len));
                            b.Tri2(0, a0, a1, tip);
                        }
                        break;
                    }
                case WK.Tear:
                    {
                        // blast through the wall: blackened petals pushed in, ragged, some torn away
                        var o = new int[K]; var e = new int[K]; var e2 = new int[K]; var bt = new int[K];
                        for (int k = 0; k < K; k++)
                        {
                            float rk = R(k), a = A(k);
                            o[k] = b.Add(P(a, rk * 1.45f, float.NaN));
                            e[k] = b.Add(P(a, rk, rv - 0.3f * t));
                            e2[k] = b.Add(b.V[e[k]]);
                            bt[k] = b.Add(P(a, rk * 0.96f, Ri - 0.002f));
                        }
                        for (int k = 0; k < K; k++)
                        {
                            int n = (k + 1) % K;
                            b.QuadO(2, o[k], o[n], e[n], e[k], Out(o[k]));
                            b.QuadO(1, e2[k], e2[n], bt[n], bt[k], ToHole(e2[k]));
                        }
                        for (int k = 0; k < K; k += 3)
                        {
                            if (PartKit.Hash(w.Seed, k + 7) < 0.35f) continue;
                            int n = (k + 2) % K;
                            int a0 = b.Add(b.V[e[k]]), a1 = b.Add(b.V[e[n]]);
                            int tip = b.Add(P(A(k + 1), R(k + 1) * 0.45f, Ri + 0.3f * t));
                            b.Tri2(2, a0, a1, tip);
                        }
                        break;
                    }
                case WK.Dent:
                    {
                        // gouge, or a blast dent that didn't go through: scraped hollow, no hole
                        const int Rn = 4;
                        var rings = new int[Rn + 1, K];
                        for (int j = 0; j <= Rn; j++)
                            for (int k = 0; k < K; k++)
                            {
                                float f = 1f - j / (float)Rn;          // 1 at the rim, 0 in the middle
                                float depth = w.Depth * (1f - f * f);
                                float r = R(k) * (f * 1.15f + 0.02f);
                                rings[j, k] = b.Add(P(A(k), r, Rvis(w.U + Mathf.Cos(A(k)) * r) + lift - depth));
                            }
                        for (int j = 0; j < Rn; j++)
                            for (int k = 0; k < K; k++)
                            {
                                int n = (k + 1) % K;
                                b.QuadO(j == 0 ? 0 : 1, rings[j, k], rings[j, n], rings[j + 1, n], rings[j + 1, k], Out(rings[j, k]));
                            }
                        break;
                    }
            }
            b.Map(MapBent);
            return b;
        }

        /// soot round a wound (alpha by distance)
        Mesh SootMesh(Wound w)
        {
            if (w.Kind == WK.Dent && w.Depth < 0.002f) return null;
            float reach = w.Rho * (w.Kind == WK.Tear || w.Kind == WK.Jet ? 4.5f : 2.6f);
            float dense = w.Kind == WK.Tear ? 0.55f : w.Kind == WK.Jet ? 0.5f : 0.35f;
            const int N = 14;
            var b = new PartKit.Builder(1);
            var idx = new int[N + 1, N + 1];
            for (int i = 0; i <= N; i++)
                for (int j = 0; j <= N; j++)
                {
                    float du = (i / (float)N * 2f - 1f) * reach, dt = (j / (float)N * 2f - 1f) * reach;
                    float u = Mathf.Clamp(w.U + du, _cutU >= 0f ? _cutU : 0f, Length);
                    float rvu = Rvis(u);
                    float d = Mathf.Sqrt(du * du + dt * dt) / reach;
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a * dense * (0.75f + 0.5f * PartKit.Hash(w.Seed + i, j));
                    idx[i, j] = b.Add(MapBent(Cyl(u, w.Phi + dt / Mathf.Max(rvu, 1e-3f), rvu + 0.0025f)), new Color(0.03f, 0.025f, 0.02f, a));
                }
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++) b.Quad(0, idx[i, j], idx[i + 1, j], idx[i + 1, j + 1], idx[i, j + 1]);
            return b.Build("HitCam barrel soot");
        }

        /// dark bore between uLo and uHi (inside the wall, open where a wound goes through), closed at both ends unless
        /// the tube is broken off there
        Mesh BoreMesh(float uLo, float uHi)
        {
            const int NP = 36;
            int NU = Mathf.Clamp(Mathf.CeilToInt((uHi - uLo) / 0.01f), 2, 400);
            var b = new PartKit.Builder(1);
            var idx = new int[NU + 1, NP + 1];
            float r = Ri;
            for (int i = 0; i <= NU; i++)
                for (int j = 0; j <= NP; j++)
                    idx[i, j] = b.Add(MapBent(Cyl(Mathf.Lerp(uLo, uHi, i / (float)NU), j * Mathf.PI * 2f / NP, r)), Color.black);
            for (int i = 0; i < NU; i++)
                for (int j = 0; j < NP; j++)
                {
                    float um = Mathf.Lerp(uLo, uHi, (i + 0.5f) / NU);
                    Vector3 m = Cyl(um, (j + 0.5f) * Mathf.PI * 2f / NP, Rvis(um));
                    bool open = false;
                    foreach (var w in _wounds) if (w.Kind != WK.Dent && InWound(w, m) < 0.92f) { open = true; break; }
                    if (!open) b.Quad(0, idx[i, j], idx[i, j + 1], idx[i + 1, j + 1], idx[i + 1, j]);
                }
            foreach (float u in new[] { uLo, uHi })
            {
                if (_cutU >= 0f && Mathf.Abs(u - _cutU) < 0.02f) continue;
                int c = b.Add(MapBent(Cyl(u, 0f, 0f)), Color.black);
                var ring = new int[NP];
                for (int j = 0; j < NP; j++) ring[j] = b.Add(MapBent(Cyl(u, j * Mathf.PI * 2f / NP, r)), Color.black);
                for (int j = 0; j < NP; j++) b.Tri(0, c, ring[j], ring[(j + 1) % NP]);
            }
            return b.Empty ? null : b.Build("HitCam bore");
        }

    }

    /// self-test: logs where a loose piece is a few seconds after it came off (did it fly, fall, land?)
    internal class PieceLog : MonoBehaviour
    {
        string _what; float _at; Vector3 _p0; bool _done;
        public void Init(string what, float after) { _what = what; _at = Time.time + after; _p0 = transform.position; }
        void Update()
        {
            if (_done || Time.time < _at) return;
            _done = true;
            var rb = GetComponent<Rigidbody>();
            if (AutoTest.Active) HitCamMod.Log.Msg($"World piece '{_what}': moved {(transform.position - _p0).magnitude:0.0} m in {Time.time - _at + 3f:0.0} s, now {(rb != null ? rb.velocity.magnitude : 0f):0.0} m/s{(rb != null && rb.IsSleeping() ? " (resting)" : "")}");
            Destroy(this);
        }
    }
}
