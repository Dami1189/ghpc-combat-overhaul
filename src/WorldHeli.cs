using System;
using System.Collections.Generic;
using System.Linq;
using GHPC;
using GHPC.Weapons;
using UnityEngine;
using DC = GHPC.Equipment.DestructibleComponent;
using Heli = GHPC.Vehicle.HelicopterController;

namespace GHPCHitCam
{
    /// A helicopter's tail boom as a structure: a thin aluminium shell (skin and stringers) carrying the tail rotor.
    /// Every HE / HEAT burst near it tears a hole in the skin that grows with the charge (about W^(1/3)); the section's
    /// remaining bending strength is worked out from what is left of the ring. When the boom has lost most of it at some
    /// station and the game itself has lost the tail (the tail rotor drive / hub destroyed, or the helicopter killed), the
    /// boom snaps there: everything aft of the break (boom, fin, tail rotor) leaves as one physics body with the
    /// helicopter's own motion and the blast's push, torn edges and smoke at both ends. Cut out of the game's own model.
    internal sealed class WorldHeli
    {
        static readonly Dictionary<Unit, WorldHeli> _all = new Dictionary<Unit, WorldHeli>();

        Unit U;
        Transform T;                    // the helicopter's root (the boom's frame)
        Vector3 _rootL, _tipL;          // the boom's line, root to tail rotor, in T's space
        float _len;
        float[] _rad;                   // drawn boom radius every 10 cm along it
        Vector2[] _off;                 // where the boom's own middle is, off the shaft line (the shaft runs along its top)
        readonly List<Tear> _tears = new List<Tear>();
        bool _tailLost, _down, _broken;
        string _why;
        float _lastBurst = -99f;
        Vector3 _lastAt;

        struct Tear { public float S, Phi, Arc, Half; public Vector3 Push; public float W; }

        const int NPhi = 72;
        const float Need = 0.3f;        // a station keeping less than this of its bending strength fails in flight

        public static void Reset() => _all.Clear();

        static WorldHeli Get(Unit u)
        {
            if (u == null) return null;
            if (_all.TryGetValue(u, out var h)) return h._why == null ? h : null;
            h = new WorldHeli { U = u, T = u.transform };
            try { h._why = h.Analyse(); } catch (Exception e) { h._why = e.GetType().Name + ": " + e.Message; }
            _all[u] = h;
            HitCamMod.Log.Msg(h._why == null
                ? $"World heli {Recorder.SafeName(u)}: tail boom {h._len:0.0} m, radius {h.R(0.2f) * 1000f:0}..{h.R(h._len - 0.3f) * 1000f:0} mm"
                : $"World heli {Recorder.SafeName(u)}: no tail boom ({h._why})");
            return h._why == null ? h : null;
        }

        string Analyse()
        {
            var hc = U.GetComponentInChildren<Heli>(true);
            if (hc == null) return "no helicopter controller";
            // the boom's line: the tail drive shaft hit zone runs down it, the tail rotor hub sits at its end
            DC shaft = null, hub = null;
            foreach (var z in hc.TailRotorHitZones ?? new DC[0])
            {
                if (z == null) continue;
                string n = (z.Name ?? "").ToLowerInvariant();
                if (n.Contains("shaft") && (shaft == null || Size(z) > Size(shaft))) shaft = z;
                else if (n.Contains("rotor") || n.Contains("hub")) hub = z;
            }
            if ((shaft == null || shaft.ComponentCollision == null) && hub == null) return "no tail zones";
            Vector3 a0, a1;
            Vector3 tail = hub != null && hub.ComponentCollision != null ? WorldModules.ZoneBounds(hub.ComponentCollision).center : Vector3.zero;
            var col = shaft != null ? shaft.ComponentCollision : null;
            Bounds lb = col != null ? LocalBounds(col) : new Bounds();
            if (col != null && lb.size.magnitude > 1.5f)
            {
                // the shaft zone's long axis
                Vector3 c = col.transform.TransformPoint(lb.center);
                Vector3[] axes = { col.transform.TransformVector(new Vector3(lb.size.x, 0f, 0f)), col.transform.TransformVector(new Vector3(0f, lb.size.y, 0f)),
                                   col.transform.TransformVector(new Vector3(0f, 0f, lb.size.z)) };
                Vector3 ax = axes.OrderByDescending(a => a.magnitude).First();
                a0 = c - ax * 0.5f; a1 = c + ax * 0.5f;
                if (hub == null) tail = a1;
            }
            else
            {
                // no shape to the shaft zone: from the main rotor's mast to the tail rotor
                if (hub == null || hub.ComponentCollision == null) return "no tail rotor zone";
                var head = U.GetComponentsInChildren<DC>(true).FirstOrDefault(z => z != null && z.ComponentCollision != null && (z.Name ?? "").ToLowerInvariant().Contains("rotor head"));
                Vector3 mast = head != null ? WorldModules.ZoneBounds(head.ComponentCollision).center : U.transform.position;
                a0 = mast; a1 = tail;
            }
            if ((a0 - tail).sqrMagnitude < (a1 - tail).sqrMagnitude) { var t = a0; a0 = a1; a1 = t; }
            // run the line on to the tail rotor
            Vector3 dir = (a1 - a0).normalized;
            float toHub = Vector3.Dot(tail - a0, dir);
            if (toHub > (a1 - a0).magnitude) a1 = a0 + dir * toHub;
            _rootL = T.InverseTransformPoint(a0);
            _tipL = T.InverseTransformPoint(a1);
            _len = (a1 - a0).magnitude;
            if (_len < 1.5f) return $"boom too short ({_len:0.0} m)";
            // the drawn radius along it
            int n10 = Mathf.CeilToInt(_len / 0.1f) + 1;
            _rad = new float[n10];
            _off = new Vector2[n10];
            var pts = new List<Vector2>[n10];
            for (int i = 0; i < n10; i++) pts[i] = new List<Vector2>();
            var near = new Bounds(a0, Vector3.zero);
            near.Encapsulate(a1);
            near.Expand(3f);
            foreach (var r in Renderers())
            {
                if (!r.bounds.Intersects(near)) continue;
                var mesh = PartKit.MeshOf(r);
                if (mesh == null) continue;
                var mc = MeshCopy.Read(mesh, out _);
                if (mc == null) continue;
                mc.Pose(r);
                foreach (var w in mc.W)
                {
                    Raw(w, out float s, out Vector2 q);
                    if (s < 0f || s > _len || q.magnitude > 1.6f) continue;
                    pts[Mathf.Clamp(Mathf.RoundToInt(s / 0.1f), 0, n10 - 1)].Add(q);
                }
            }
            // per station: the skin's own middle (medians, so a fin or a stabiliser doesn't pull it) and its radius round that
            for (int i = 0; i < n10; i++)
            {
                var l = pts[i];
                if (l.Count < 8) continue;
                var xs = l.Select(q => q.x).OrderBy(x => x).ToList();
                var ys = l.Select(q => q.y).OrderBy(y => y).ToList();
                var c = new Vector2(xs[xs.Count / 2], ys[ys.Count / 2]);
                var rs = l.Select(q => (q - c).magnitude).OrderBy(r => r).ToList();
                _off[i] = c;
                _rad[i] = rs[rs.Count / 2];
            }
            for (int i = 0; i < n10; i++) if (_rad[i] <= 0.05f) _rad[i] = 0f;
            float last = 0.3f;
            Vector2 lastO = Vector2.zero;
            for (int i = 0; i < n10; i++) { if (_rad[i] > 0f) { last = _rad[i]; lastO = _off[i]; } else { _rad[i] = last; _off[i] = lastO; } }
            return null;
        }

        static float Size(DC z) => z.ComponentCollision != null ? WorldModules.ZoneBounds(z.ComponentCollision).size.magnitude : 0f;

        static Bounds LocalBounds(Collider c)
        {
            switch (c)
            {
                case MeshCollider mc when mc.sharedMesh != null: return mc.sharedMesh.bounds;
                case BoxCollider bc: return new Bounds(bc.center, bc.size);
                case CapsuleCollider cc: return new Bounds(cc.center, Vector3.one * cc.radius * 2f + (cc.direction == 0 ? Vector3.right : cc.direction == 1 ? Vector3.up : Vector3.forward) * Mathf.Max(0f, cc.height - 2f * cc.radius));
                default: return new Bounds(c.transform.InverseTransformPoint(c.bounds.center), c.bounds.size);
            }
        }

        IEnumerable<Renderer> Renderers()
        {
            foreach (var root in WorldDamage.UnitRoots(U))
                foreach (var r in root.GetComponentsInChildren<Renderer>(false))
                {
                    if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                    if (!r.enabled || r.forceRenderingOff) continue;
                    if (r.GetComponentInParent<AarVisual>() != null || r.GetComponentInParent<AarModel>() != null) continue;
                    // the main rotor's blades (a huge disc) stay
                    if (r.bounds.size.magnitude > 30f) continue;
                    if (r.sharedMaterials.Any(m => m != null && m.shader != null && m.shader.name.Contains("Rotor")) && r.bounds.size.magnitude > 8f) continue;
                    yield return r;
                }
        }

        /// A world point against the boom: distance along it from the root, distance from its line, angle round it.
        /// A world point against the shaft line: distance along it, and its offset across it (e1, e2).
        void Raw(Vector3 w, out float s, out Vector2 q)
        {
            Vector3 a0 = T.TransformPoint(_rootL), a1 = T.TransformPoint(_tipL);
            Vector3 ax = (a1 - a0).normalized;
            Vector3 d = w - a0;
            s = Vector3.Dot(d, ax);
            Vector3 radial = d - ax * s;
            Vector3 e1 = Vector3.Cross(ax, T.up).normalized;
            if (e1.sqrMagnitude < 0.5f) e1 = Vector3.Cross(ax, T.right).normalized;
            Vector3 e2 = Vector3.Cross(e1, ax);
            q = new Vector2(Vector3.Dot(radial, e1), Vector3.Dot(radial, e2));
        }

        void Station(Vector3 w, out float s, out float r, out float phi)
        {
            if (_off != null)
            {
                // against the boom's own middle at that station
                Raw(w, out s, out Vector2 q);
                q -= _off[Mathf.Clamp(Mathf.RoundToInt(s / 0.1f), 0, _off.Length - 1)];
                r = q.magnitude;
                phi = Mathf.Atan2(q.y, q.x);
                return;
            }
            Vector3 a0 = T.TransformPoint(_rootL), a1 = T.TransformPoint(_tipL);
            Vector3 ax = (a1 - a0).normalized;
            Vector3 d = w - a0;
            s = Vector3.Dot(d, ax);
            Vector3 radial = d - ax * s;
            r = radial.magnitude;
            Vector3 e1 = Vector3.Cross(ax, T.up).normalized;
            if (e1.sqrMagnitude < 0.5f) e1 = Vector3.Cross(ax, T.right).normalized;
            Vector3 e2 = Vector3.Cross(e1, ax);
            phi = Mathf.Atan2(Vector3.Dot(radial, e2), Vector3.Dot(radial, e1));
        }

        float R(float s) => _rad[Mathf.Clamp(Mathf.RoundToInt(s / 0.1f), 0, _rad.Length - 1)];

        /// What's left of the boom's bending strength at s, as a fraction (worst direction).
        float Left(float s)
        {
            float worst = 1f;
            for (int dirI = 0; dirI < 12; dirI++)
            {
                float a = dirI * Mathf.PI / 12f;
                Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                var all = new List<(float y, float a)>(NPhi);
                var kept = new List<(float y, float a)>(NPhi);
                for (int j = 0; j < NPhi; j++)
                {
                    float phi = (j + 0.5f) * Mathf.PI * 2f / NPhi;
                    float y = Mathf.Cos(phi) * dir.x + Mathf.Sin(phi) * dir.y;
                    all.Add((y, 1f));
                    bool gone = false;
                    foreach (var t in _tears)
                        if (Mathf.Abs(t.S - s) < t.Half && PartKit.AngleDiff(phi, t.Phi) < t.Arc) { gone = true; break; }
                    if (!gone) kept.Add((y, 1f));
                }
                float f = Plastic(kept) / Mathf.Max(Plastic(all), 1e-6f);
                if (f < worst) worst = f;
            }
            return worst;
        }

        static float Plastic(List<(float y, float a)> c)
        {
            if (c.Count == 0) return 0f;
            c.Sort((x, y) => x.y.CompareTo(y.y));
            float total = c.Count, acc = 0f, y0 = c[c.Count - 1].y;
            foreach (var e in c) { acc += e.a; if (acc >= total * 0.5f) { y0 = e.y; break; } }
            float m = 0f;
            foreach (var e in c) m += e.a * Mathf.Abs(e.y - y0);
            return m;
        }

        // ------------------------------------------------------------------ events

        public static void Burst(Unit u, Vector3 point, AmmoType ammo)
        {
            var h = Get(u);
            if (h == null || h._broken || ammo == null) return;
            // the same burst reported twice (its fuze on armour, then its detonation): once is enough
            if (Time.time - h._lastBurst < 0.15f && (point - h._lastAt).sqrMagnitude < 1f) return;
            h._lastAt = point;
            float W = Mathf.Max(ammo.TntEquivalentKg, 0.005f) * (ammo.Category == AmmoType.AmmoCategory.ShapedCharge ? 0.6f : 1f);
            h.Station(point, out float s, out float r, out float phi);
            if (s < -0.3f || s > h._len + 0.3f) return;
            s = Mathf.Clamp(s, 0.2f, h._len - 0.2f);
            float rb = h.R(s), standoff = Mathf.Max(0f, r - rb);
            // the skin a burst tears open: about W^(1/3) across at contact, less with stand-off
            float hole = 1.0f * Mathf.Pow(W, 1f / 3f) * Mathf.Clamp01(1.2f - standoff / 1.5f);
            if (hole < 0.02f) return;
            float arc = Mathf.Min(Mathf.PI, hole * 0.5f / Mathf.Max(rb, 0.05f));
            Vector3 push = (point - h.T.TransformPoint(Vector3.Lerp(h._rootL, h._tipL, s / h._len))).normalized;
            h._tears.Add(new Tear { S = s, Phi = phi, Arc = arc, Half = hole * 0.5f, Push = -push, W = W });
            h._lastBurst = Time.time;
            float left = h.Left(s);
            HitCamMod.Log.Msg($"World heli {Recorder.SafeName(u)}: {ammo.Name} ({W:0.00} kg) {s:0.0} m down the tail boom (radius {rb * 1000f:0} mm, {standoff:0.00} m off): " +
                              $"skin torn {hole * 1000f:0} mm, {arc * 2f * Mathf.Rad2Deg:0} deg round; the boom keeps {left * 100f:0}% of its strength there" +
                              (left < Need ? (h._tailLost || h._down ? " -> it fails" : " -> fails once the tail goes") : ""));
            h.TryBreak();
        }

        public static void TailLost(Unit u)
        {
            var h = Get(u);
            if (h == null) return;
            h._tailLost = true;
            h.TryBreak();
        }

        public static void Down(Unit u)
        {
            var h = Get(u);
            if (h == null) return;
            h._down = true;
            h.TryBreak();
        }

        void TryBreak()
        {
            if (_broken || _tears.Count == 0 || !(_tailLost || _down)) return;
            float best = 1f, at = -1f;
            for (float s = 0.2f; s < _len - 0.2f; s += 0.05f)
            {
                if (!_tears.Any(t => Mathf.Abs(t.S - s) < t.Half)) continue;
                float f = Left(s);
                if (f < best) { best = f; at = s; }
            }
            if (at < 0f || best >= Need) return;
            _broken = true;
            try { Break(at, best); }
            catch (Exception e) { HitCamMod.Log.Warning("World heli break: " + e); }
        }

        // ------------------------------------------------------------------ the break

        void Break(float s, float left)
        {
            Vector3 a0 = T.TransformPoint(_rootL), a1 = T.TransformPoint(_tipL);
            Vector3 ax = (a1 - a0).normalized;
            Vector3 cut = a0 + ax * s;
            float k = Vector3.Dot(ax, cut);
            float boomR = R(s);
            int seed = U.GetInstanceID();
            int layer = WorldDamage.VisibleLayer(U);
            var pieces = new List<(Renderer src, Mesh mesh)>();
            var hide = new List<Renderer>();
            float area = 0f;
            Vector3 cmAcc = Vector3.zero;
            float cmW = 0f;
            // the piece's frame: at the break, along the boom
            var frame = Matrix4x4.TRS(cut, Quaternion.LookRotation(ax, T.up), Vector3.one);
            var w2l = frame.inverse;
            foreach (var r in Renderers().ToList())
            {
                Bounds bb = r.bounds;
                bool allAft = true, anyAft = false;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = bb.center + Vector3.Scale(bb.extents, new Vector3((i & 1) != 0 ? 1 : -1, (i & 2) != 0 ? 1 : -1, (i & 4) != 0 ? 1 : -1));
                    float d = Vector3.Dot(ax, corner) - k;
                    if (d > 0f) anyAft = true; else allAft = false;
                }
                if (!anyAft) continue;
                Station(bb.center, out float sc, out float rc, out _);
                if (rc > 4f) continue;
                var mesh = PartKit.MeshOf(r);
                if (mesh == null) continue;
                var mc = MeshCopy.Read(mesh, out string why);
                if (mc == null) { HitCamMod.Log.Msg($"World heli: {r.name} not readable ({why})"); continue; }
                mc.Pose(r);
                bool Near(int v) { Station(mc.W[v], out float sv, out float rv, out _); return rv < Mathf.Max(2.5f * boomR, 1.2f); }
                if (!allAft)
                {
                    mc.Slice(ax, k, (x, y, z) => Near(x) || Near(y) || Near(z));
                    // the torn edge: ragged along the boom, both sides share it
                    for (int v = 0; v < mc.Count; v++)
                    {
                        if (Mathf.Abs(Vector3.Dot(ax, mc.W[v]) - k) > 1e-3f || !Near(v)) continue;
                        Station(mc.W[v], out _, out _, out float ph);
                        float jag = (PartKit.Hash(seed, Mathf.FloorToInt(Mathf.Repeat(ph, Mathf.PI * 2f) / (Mathf.PI * 2f) * 16f)) - 0.5f) * 0.22f;
                        mc.MoveTo(v, mc.W[v] + ax * jag, Quaternion.identity);
                    }
                }
                var far = new List<int>[mc.Tris.Length];
                var near = new List<int>[mc.Tris.Length];
                for (int sm = 0; sm < mc.Tris.Length; sm++)
                {
                    far[sm] = new List<int>(); near[sm] = new List<int>();
                    var tl = mc.Tris[sm];
                    for (int i = 0; i + 2 < tl.Count; i += 3)
                    {
                        Vector3 A = mc.W[tl[i]], B = mc.W[tl[i + 1]], C = mc.W[tl[i + 2]];
                        Vector3 m = (A + B + C) / 3f;
                        Station(m, out float sm2, out float rm, out _);
                        bool aft = allAft || (Vector3.Dot(ax, m) - k > 0f && rm < 3f);
                        (aft ? far[sm] : near[sm]).AddRange(new[] { tl[i], tl[i + 1], tl[i + 2] });
                        if (aft) { float ar = Vector3.Cross(B - A, C - A).magnitude * 0.5f; area += ar; cmAcc += m * ar; cmW += ar; }
                    }
                }
                if (far.Sum(l => l.Count) == 0) continue;
                pieces.Add((r, mc.BuildStatic("HitCam tail " + r.name, far, w2l)));
                if (allAft || near.Sum(l => l.Count) == 0) hide.Add(r);
                else
                {
                    mc.Tris = near;
                    PartKit.SetMesh(r, mc.Build(r.name + " (HitCam broken)", near));
                    WorldDamage.ForgetCut(r);
                }
            }
            if (pieces.Count == 0) { HitCamMod.Log.Msg("World heli: nothing aft of the break to cut"); return; }
            foreach (var r in hide) r.forceRenderingOff = true;

            // the tail leaves with the helicopter's motion at that point, the bursts' push and a tumble from the torn spar
            var rb = U.GetComponent<Rigidbody>();
            Vector3 cm = cmW > 0f ? cmAcc / cmW : cut + ax * 2f;
            float mass = Mathf.Clamp(area * 12f + 60f, 30f, 900f);     // ~12 kg per m2 of drawn skin (skin, stringers, frames) + gearbox, rotor
            Vector3 vel = rb != null ? rb.GetPointVelocity(cm) : Vector3.zero;
            Vector3 w = rb != null ? rb.angularVelocity : Vector3.zero;
            Vector3 kick = Vector3.zero;
            foreach (var t in _tears.Where(t => Mathf.Abs(t.S - s) < 1.5f)) kick += t.Push * Mathf.Sqrt(t.W) * 2.4f;
            vel += kick;
            w += Vector3.Cross(ax, kick).normalized * Mathf.Min(kick.magnitude * 0.8f, 6f) + UnityEngine.Random.insideUnitSphere * 0.6f;
            var go = WorldModules.SpawnBody("HitCam tail boom", cut, Quaternion.LookRotation(ax, T.up), vel, w, mass, 900f);
            Bounds pb = new Bounds();
            bool first = true;
            foreach (var (src, mesh) in pieces)
            {
                var child = PartKit.Child(go.transform, mesh.name, mesh, src.sharedMaterials, WorldDamage.DebrisLayer);
                PartKit.CopyLook(src, child.GetComponent<MeshRenderer>());
                if (first) { pb = mesh.bounds; first = false; } else pb.Encapsulate(mesh.bounds);
            }
            var bc = go.AddComponent<BoxCollider>();
            bc.center = pb.center;
            bc.size = Vector3.Max(pb.size, Vector3.one * 0.1f);
            go.GetComponent<Rigidbody>().centerOfMass = go.transform.InverseTransformPoint(cm);

            // dark insides where the shell is open, smoke from both torn ends
            var capNear = TornCap(rb, s, seed, -1);
            if (capNear != null)
            {
                var cg = PartKit.Child(T, "HitCam torn boom", capNear, new[] { WorldModules.Dark }, layer, false);
                cg.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
                cg.transform.SetParent(T, true);
            }
            var capFar = TornCap(rb, s, seed, +1);
            if (capFar != null)
            {
                var vtx = capFar.vertices;
                for (int i = 0; i < vtx.Length; i++) vtx[i] = w2l.MultiplyPoint3x4(vtx[i]);
                capFar.vertices = vtx;
                capFar.RecalculateBounds();
                PartKit.Child(go.transform, "HitCam torn boom", capFar, new[] { WorldModules.Dark }, WorldDamage.DebrisLayer, false);
            }
            Smoke.Attach(T, cut, -ax, 40f, 0.5f);
            Smoke.Attach(go.transform, cut, ax, 25f, 0.4f);
            WorldDamage.Sparks(cut, -ax, 120, 18f, 70f, layer);
            for (int i = 0; i < 6; i++)
                WorldDamage.SpawnPiece("HitCam skin shard", cut + UnityEngine.Random.insideUnitSphere * boomR, UnityEngine.Random.rotationUniform,
                                       new Vector3(UnityEngine.Random.Range(0.08f, 0.3f), 0.003f, UnityEngine.Random.Range(0.08f, 0.25f)),
                                       vel + UnityEngine.Random.insideUnitSphere * 8f, UnityEngine.Random.insideUnitSphere * 20f, mass: 0.4f);
            HitCamMod.Log.Msg($"World heli {Recorder.SafeName(U)}: TAIL BOOM BREAKS {s:0.0} m down (kept {left * 100f:0}%): {pieces.Count} parts, {mass:0} kg, leaves at {vel.magnitude:0} m/s" +
                              (rb != null ? $" ({(vel - rb.GetPointVelocity(cm)).magnitude:0.0} m/s from the blasts)" : ""));
        }

        /// A dark jagged disc closing the open shell at a break (so one doesn't look through the drawn skin), world space.
        Mesh TornCap(Rigidbody rb, float s, int seed, int side)
        {
            Vector3 a0 = T.TransformPoint(_rootL), a1 = T.TransformPoint(_tipL);
            Vector3 ax = (a1 - a0).normalized;
            Vector3 c = a0 + ax * (s + side * 0.06f);
            Vector3 e1 = Vector3.Cross(ax, T.up).normalized;
            if (e1.sqrMagnitude < 0.5f) e1 = Vector3.Cross(ax, T.right).normalized;
            Vector3 e2 = Vector3.Cross(e1, ax);
            float r = R(s) * 0.97f;
            const int N = 24;
            var b = new PartKit.Builder(1);
            int ci = b.Add(c, Color.black);
            var ring = new int[N];
            for (int i = 0; i < N; i++)
            {
                float ph = i * Mathf.PI * 2f / N;
                float jag = (PartKit.Hash(seed, Mathf.FloorToInt(ph / (Mathf.PI * 2f) * 16f)) - 0.5f) * 0.22f * 0.6f;
                ring[i] = b.Add(c + (e1 * Mathf.Cos(ph) + e2 * Mathf.Sin(ph)) * r * (0.9f + 0.1f * PartKit.Hash(seed + 1, i)) + ax * jag, Color.black);
            }
            for (int i = 0; i < N; i++) b.Tri(0, ci, ring[i], ring[(i + 1) % N]);
            return b.Build("HitCam torn boom");
        }
    }

    /// Smoke from a torn or burning part, with the game's own smoke (the effect it uses for small holes / hatches).
    internal static class Smoke
    {
        public static GameObject Attach(Transform parent, Vector3 at, Vector3 dir, float seconds, float ratio)
        {
            try
            {
                var pem = GHPC.Effects.ParticleEffectsManager.Instance;
                if (pem == null) return null;
                GameObject prefab = null;
                foreach (var t in new[] { GHPC.Effects.FlammableSourceType.SmallHole, GHPC.Effects.FlammableSourceType.SmallHatch, GHPC.Effects.FlammableSourceType.Surface })
                    if ((prefab = pem.GetSmokePrefab(t)) != null) break;
                if (prefab == null) return null;
                var go = UnityEngine.Object.Instantiate(prefab, at, Quaternion.identity, parent);
                go.name = "HitCam smoke";
                var fps = go.GetComponentInChildren<GHPC.Effects.IFireParticleSystem>();
                if (fps != null && fps.Orientation == GHPC.Effects.FireParticleSystemOrientation.YOut) go.transform.up = dir;
                else go.transform.forward = dir;
                fps?.SetAllRatios(ratio);
                fps?.Play();
                go.AddComponent<Expire>().At = Time.time + seconds;
                return go;
            }
            catch (Exception e) { HitCamMod.Log.Warning("World smoke: " + e.Message); return null; }
        }
    }
}
