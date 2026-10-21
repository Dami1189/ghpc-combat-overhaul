using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace GHPCHitCam
{
    /// What a long-rod penetrator does on its way through armour, from terminal ballistics:
    /// - Erosion (Tate-Alekseevskii with strength): 1/2 rho_p (v-u)^2 + Y_p = 1/2 rho_t u^2 + R_t. The nose goes into the
    ///   plate at u while the rest of the rod still pushes on at v, so the nose is used up at (v-u) and flows back out of
    ///   the crater as a mushroom and a spray of debris. Target RHA: rho 7850, R_t 5.5 GPa (Anderson et al.).
    /// - A plate that stops it uses it up: the rest of the rod erodes in the crater until only a stub is left.
    /// - It bends where its channel turns (the game normalises a penetrating rod by up to 5 deg) and from the side load of
    ///   an oblique entry (~2 deg x tan(obliquity)^1.5: ~5 deg at 60, ~9 deg at 70). Bent past what its alloy takes, it breaks.
    /// - A ricochet, a shatter (the game's verdict) or heavy ERA (flyer plates) break it too.
    internal static class RodPhysics
    {
        public enum Alloy { DU, WHA, SteelWC }

        public struct Mat
        {
            public Alloy A;
            public float Rho, Yp, BendLimit;   // kg/m^3, Pa, degrees
            public string Name;
            public Color Color, Hot, Spark;
        }

        // depleted-uranium rods (U-3/4Ti) and steel-bodied rods with a carbide slug; everything else tungsten heavy alloy
        static readonly string[] Du = { "M774", "M833", "M829", "M900", "M735A1", "3BM32", "3BM48", "3BM29", "3BM59", "3BM60" };
        static readonly string[] Steel = { "3BM9", "3BM12", "3BM15", "3BM17", "3BM22" };

        public static Mat MaterialOf(AmmoType a)
        {
            string n = (a?.Name ?? "").ToUpperInvariant().Replace(" ", "");
            if (Du.Any(k => n.Contains(k)))
                return new Mat { A = Alloy.DU, Rho = 18600f, Yp = 1.3e9f, BendLimit = 12f, Name = "depleted uranium (U-3/4Ti)", Color = new Color(0.30f, 0.31f, 0.29f), Hot = new Color(1f, 0.95f, 0.75f), Spark = new Color(1f, 0.97f, 0.85f) };
            if (Steel.Any(k => n.Contains(k)))
                return new Mat { A = Alloy.SteelWC, Rho = 9300f, Yp = 1.1e9f, BendLimit = 6f, Name = "steel body, tungsten carbide slug", Color = new Color(0.48f, 0.48f, 0.50f), Hot = new Color(1f, 0.6f, 0.25f), Spark = new Color(1f, 0.55f, 0.2f) };
            return new Mat { A = Alloy.WHA, Rho = 17600f, Yp = 1.5e9f, BendLimit = 9f, Name = "tungsten heavy alloy", Color = new Color(0.55f, 0.56f, 0.58f), Hot = new Color(1f, 0.78f, 0.45f), Spark = new Color(1f, 0.72f, 0.35f) };
        }

        public const float RhoT = 7850f, Rt = 5.5e9f;

        /// The speed the nose goes into RHA at (Tate): u = (v - mu sqrt(v^2 + A)) / (1 - mu^2), mu^2 = rho_t / rho_p,
        /// A = 2 (R_t - Y_p)(1 - mu^2) / rho_t. Zero below the threshold (~670 m/s for DU): it only erodes.
        public static float PenVelocity(Mat m, float v)
        {
            float mu2 = RhoT / m.Rho, mu = Mathf.Sqrt(mu2);
            float a = 2f * (Rt - m.Yp) * (1f - mu2) / RhoT;
            return Mathf.Max(0f, (v - mu * Mathf.Sqrt(v * v + a)) / (1f - mu2));
        }
    }

    /// The rod in the x-ray from its first contact on: a body that follows its own channel through the plates (bent
    /// where the channel turns, plus the residual bend of an oblique entry), eroded from the nose by Tate's rate, a hot
    /// mushroom at the nose while it erodes, the eroded material spraying back out of the crater, the fins and the
    /// tracer at the tail, and the pieces once it breaks. Drawn over the armour (the x-ray sees the rod inside the plate,
    /// as a flash radiograph does).
    internal class RodRig
    {
        public struct Bite
        {
            public string Plate;
            public float SimStart, Dur, Loss, U, V, Los, Obliquity, Turn, Bend, Cap;
            public bool Stopped;
        }

        public readonly RodPhysics.Mat Mat;
        public readonly float L0, D;
        readonly float _finSpan, _finLen;
        public readonly List<Bite> Bites = new List<Bite>();
        public float BreakSim = float.MaxValue;
        public int BreakPieces;
        public string BreakWhy = "";
        public string Report = "";
        float _bendDeg, _bendStart = float.MaxValue, _bendEnd = float.MaxValue;
        Vector3 _bendAxis;
        float _stopSim = float.MaxValue;

        // the game's path as arc length
        Vector3[] _p;
        float[] _s, _t;
        float _vStart = 1500f, _vEnd = 1500f;
        float _endSim;
        // a ricochet: the nose slides in the groove the plate shows, then lifts off (and nothing goes under the plate)
        bool _ric;
        Vector3 _ricP, _ricN;
        float _ricSlideStart, _ricSlideEnd, _ricTurn, _ricLen, _grooveLen;

        // what the rest of the hit cam reads
        public bool Visible;
        public Vector3 Nose, Dir = Vector3.forward;
        public float Length;
        public bool Eroding;
        public bool UsedUp;

        readonly Renderer[] _fin = new Renderer[4];
        readonly Renderer _capGlow, _tracer;
        readonly Sparks _debris;
        float _nextDebris;
        readonly RodTube _tube;
        readonly int _nn;                          // centreline nodes
        readonly Vector3[] _node;
        readonly float[] _rad;
        readonly Color[] _col;
        readonly bool _deform;                     // mushroom, flexural wave, curled lip (off: a clean rigid rod)
        readonly float _flare;                     // how wide its head flares while it erodes (x radius), by alloy
        float _waveA, _waveStart = float.MaxValue; // the flexural wave an oblique entry sets off along it
        float _erodeEnd = -1f;
        // the pieces once it breaks: each keeps the shape it had, in its own frame
        class Piece { public Vector3[] Local; public float[] Rad; public Color[] Col; public int Count; public float Offset; public Vector3 Side, Axis; public float Spin; public bool Front, Back; }
        Piece[] _pieces;
        Quaternion _breakRot;
        float _breakE = -1f, _breakLen;
        public string SoftSummary { get; private set; } = "";

        /// soft: 0 = a rigid rod cut down from the nose, 1 = deforming (the live hit cam), 2 = deforming in full detail
        /// (Armor Sim, proving ground, AAR)
        public RodRig(Transform parent, HitRecord rec, AmmoType ammo, float modelLen, float v0, int soft = 0, float grooveLen = -1f)
        {
            _grooveLen = grooveLen;
            Mat = RodPhysics.MaterialOf(ammo);
            L0 = Mathf.Max(modelLen, 0.1f);
            D = Mathf.Max(ammo.Radius * 2f, 0.01f);
            float cal = Mathf.Max(ammo.Caliber, ammo.Radius * 2000f) * 0.001f;
            _finSpan = Mathf.Clamp(D * 3.2f, 0.05f, Mathf.Max(cal * 0.85f, 0.06f));
            _finLen = Mathf.Clamp(L0 * 0.12f, 0.03f, 0.12f);

            var ps = new List<Vector3>();
            var ss = new List<float>();
            var ts = new List<float>();
            float acc = 0f;
            foreach (var pt in rec.Main.Points)
            {
                if (ps.Count > 0)
                {
                    float d = Vector3.Distance(ps[ps.Count - 1], pt.L);
                    if (d < 1e-5f || pt.Sim <= ts[ts.Count - 1]) continue;
                    acc += d;
                }
                ps.Add(pt.L); ss.Add(acc); ts.Add(pt.Sim);
            }
            _p = ps.ToArray(); _s = ss.ToArray(); _t = ts.ToArray();
            int n = _p.Length;
            if (n >= 2)
            {
                _vStart = Mathf.Max((_s[1] - _s[0]) / Mathf.Max(_t[1] - _t[0], 1e-7f), 50f);
                _vEnd = Mathf.Max((_s[n - 1] - _s[n - 2]) / Mathf.Max(_t[n - 1] - _t[n - 2], 1e-7f), 50f);
            }
            _endSim = n > 0 ? _t[n - 1] : 0f;
            RicochetChannel(rec);
            Plan(rec, ammo, v0);

            var go = new GameObject("rod");
            go.layer = Vis.Layer;
            go.transform.SetParent(parent, false);
            var root = go.transform;
            _deform = soft > 0;
            _nn = soft == 2 ? 44 : soft == 1 ? 26 : 14;
            _node = new Vector3[_nn]; _rad = new float[_nn]; _col = new Color[_nn];
            _tube = new RodTube(root, soft == 2 ? 18 : 12);
            for (int i = 0; i < 4; i++) _fin[i] = Vis.Child(root, "fin", Vis.WCube, Matrix4x4.identity, Vis.Unlit).GetComponent<Renderer>();
            _capGlow = Vis.Child(root, "erosion glow", Vis.SoftDisc, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
            _tracer = Vis.Child(root, "tracer", Vis.SoftDisc, Matrix4x4.identity, Vis.Additive).GetComponent<Renderer>();
            _debris = new Sparks(root, soft == 2 ? 420 : 260) { Gravity = 0f, Drag = 0.4f };
            // a uranium rod self-sharpens (adiabatic shear sheds the head): a small mushroom; tungsten and steel flare wide
            _flare = Mat.A == RodPhysics.Alloy.DU ? 1.35f : Mat.A == RodPhysics.Alloy.WHA ? 1.8f : 1.9f;
            // the flexural wave of the most oblique entry: up to half a calibre, by how close it came to breaking
            foreach (var b in Bites)
            {
                float bend = Mathf.Max(b.Bend, b.Turn);
                if (bend < 1f) continue;
                float a = Mathf.Clamp(0.35f * D * bend / Mat.BendLimit, 0f, 0.5f * D);
                if (a > _waveA) { _waveA = a; _waveStart = b.SimStart; }
            }
            SoftSummary = _deform ? $"deforming rod: head flares to {_flare:0.00}x, flexural wave {_waveA * 1000f:0.0} mm" : "";
            Hide();
        }

        /// The game's path has at least two points in time order (what the rig needs to move along).
        public static bool HasPath(HitRecord rec)
        {
            if (rec?.Main?.Points == null) return false;
            int n = 0;
            Vector3 last = Vector3.zero;
            float lastT = float.MinValue;
            foreach (var pt in rec.Main.Points)
            {
                if (n > 0 && (Vector3.Distance(last, pt.L) < 1e-5f || pt.Sim <= lastT)) continue;
                last = pt.L; lastT = pt.Sim;
                if (++n >= 2) return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ the plan: what each plate does to it

        void Plan(HitRecord rec, AmmoType ammo, float v0)
        {
            float E = 0f, L = L0, vPhys = -1f;
            var hits = rec.ArmorHits.Where(a => a.Frag == rec.Main && !a.IsSpall && !a.PreJet).OrderBy(a => a.Sim).ToList();
            var sb = new StringBuilder($"{ammo.Name}: {Mat.Name}, rod {L0 * 1000f:0} x {D * 1000f:0} mm (L/D {L0 / D:0}); ");
            foreach (var ah in hits)
            {
                float sPlate = ArcAtSim(ah.Sim);
                // the first plate at the game's arrival speed; after it the rod's own: the game slows the round by
                // energy (v' = v sqrt(1 - LOS/pen)), a real rod mostly gets shorter and its rear only decelerates by
                // Y_p / (rho_p L) while it works through (tens of m/s per plate)
                float v = vPhys > 0f ? vPhys : Mathf.Max(ah.Speed > 50f ? ah.Speed : v0, v0 * 0.85f);
                float tStart = SimAtArc(sPlate);          // the nose is where the game's round is: it meets the plate with it
                if (ah.Ricochet)
                {
                    // the nose slides along the groove (wearing a little), the rod slaps onto the plate and whips as it
                    // turns out; it breaks at the lift-off if that turn is more than its alloy takes (or the game says
                    // it shattered), else it flies off bent
                    float slide = Mathf.Max(_ricSlideEnd - _ricSlideStart, 1e-6f);
                    var rb = new Bite { Plate = ah.Name, SimStart = tStart, V = v, Dur = slide, Obliquity = 90f - HoleFx.ImpactAngle(ah) };
                    rb.Loss = _ric ? Mathf.Min(0.12f * L, 3f * D) : 0f;
                    rb.U = v - rb.Loss / slide;
                    Bites.Add(rb);
                    E += rb.Loss; L -= rb.Loss;
                    bool shattered = rec.Events.Any(e => e.Text != null && e.Text.StartsWith("Shattered"));
                    if (_ricTurn > Mat.BendLimit || shattered)
                        Break(_ric ? _ricSlideEnd : tStart, shattered ? 4 : 2, shattered ? "shattered on the ricochet (the game's verdict)" : "ricochet off " + ah.Name + ": turned " + _ricTurn.ToString("0.0") + " deg (its alloy takes ~" + Mat.BendLimit.ToString("0") + ")");
                    else if (_ric)
                    {
                        _bendDeg = _ricTurn * 0.5f;
                        _bendStart = tStart; _bendEnd = _ricSlideEnd;
                        _bendAxis = _ricN;
                    }
                    string fate = BreakSim < float.MaxValue ? "breaks in " + BreakPieces : "flies off bent";
                    sb.Append(ah.Name + ": ricochet at " + HoleFx.ImpactAngle(ah).ToString("0") + " deg, slides " + (_ricLen * 1000f).ToString("0") + " mm along its groove in " + (slide * 1e6f).ToString("0") + " us, turns " + _ricTurn.ToString("0.0") + " deg -> " + fate + "; ");
                    break;
                }
                float los = Mathf.Max(ah.Los, ah.Nominal) * 0.001f;
                float u = RodPhysics.PenVelocity(Mat, v);
                float tate = u > 1f ? (v - u) / u * los : L;
                float obl = Mathf.Clamp(90f - HoleFx.ImpactAngle(ah), 0f, 89f);
                // the nose goes through the plate on the game's path at the game's pace (that is where the hole opens and
                // where the spall comes from); the rear keeps the rod's own speed v, so the rod gets shorter by as much as
                // the rear catches up with the nose, never more than Tate's figure for that plate. (The game slows the
                // whole round by energy at once; tied to that pace, the rear stood still in the air while the nose was
                // eaten away.)
                float tPlate = Mathf.Max(SimAtArc(sPlate + los) - tStart, 1e-6f);
                float catchUp = Mathf.Max(v * tPlate - los, 0f);
                vPhys = Mathf.Max(v - RodPhysics_Decel(L - 0.5f * Mathf.Min(tate, L)) * tPlate, 100f);
                float stub = Mathf.Min(1.5f * D, L);
                var b = new Bite { Plate = ah.Name, SimStart = tStart, U = u, V = v, Los = los, Obliquity = obl };
                if (!ah.Penetrated)
                {
                    b.Stopped = true;
                    b.Loss = Mathf.Max(L - stub, 0f);
                    b.Cap = sPlate + 0.9f * los;
                    // the nose jams in the crater; the rest of the rod keeps coming at v and is eaten there
                    b.Dur = b.Loss / Mathf.Max(v, 1f) + tPlate;
                    Bites.Add(b);
                    sb.Append($"{ah.Name} {ah.Los:0} mm RHAe at {v:0} m/s (u {u:0} m/s): stopped, the rest of the rod ({L * 1000f:0} mm) erodes in the crater in {b.Dur * 1e6f:0} us, a {stub * 1000f:0} mm stub stays; ");
                    E += b.Loss; L = stub;
                    break;
                }
                // through it: Tate's erosion, but never all of it (the game says it got through); below Tate's threshold
                // speed it punches through as a rigid body (hardly any erosion)
                if (u > 1f)
                    b.Loss = Mathf.Min(Mathf.Min(tate, catchUp), Mathf.Max(L - Mathf.Max(stub, 0.12f * L0), 0f));
                else
                    b.Loss = Mathf.Min(Mathf.Min(0.1f * D, 0.05f * L), catchUp);   // below Tate's threshold: rigid, it hardly wears
                b.Dur = tPlate;
                Vector3 dIn = ah.DirL.sqrMagnitude > 1e-6f ? ah.DirL.normalized : DirAt(sPlate - 0.05f);
                Vector3 dOut = DirAt(sPlate + los + 0.08f);
                b.Turn = Mathf.Min(Vector3.Angle(dIn, dOut), 6f);
                // the side load of an oblique entry, from a plate thick enough to hold the nose (a thin one is just cut)
                float hold = Mathf.Clamp01(Mathf.Max(ah.Nominal, 1f) * 0.001f / (1.5f * D));
                b.Bend = 2f * Mathf.Pow(Mathf.Tan(Mathf.Min(obl, 80f) * Mathf.Deg2Rad), 1.5f) * hold;
                Bites.Add(b);
                float bend = Mathf.Max(b.Turn, b.Bend);
                sb.Append($"{ah.Name} {ah.Los:0} mm RHAe ({ah.Nominal:0} mm plate) at {v:0} m/s (game {ah.Speed:0}): u {u:0} m/s, Tate {(u > 1f ? (v - u) / u : 0f):0.00} mm/mm -> {b.Loss * 1000f:0} mm eroded (Tate {Mathf.Min(tate, L) * 1000f:0}, rear catching up {catchUp * 1000f:0}) in {tPlate * 1e6f:0} us, {obl:0}° off normal, bent {bend:0.0}°, leaves at {vPhys:0} m/s; ");
                E += b.Loss; L -= b.Loss;
                if (bend > _bendDeg)
                {
                    _bendDeg = bend;
                    _bendStart = tStart;
                    _bendEnd = tStart + tPlate;
                    Vector3 nrm = ah.HasSurf ? ah.SurfNormalL : ah.NormalL;
                    _bendAxis = Vector3.ProjectOnPlane(-nrm, dIn);
                    _bendAxis = _bendAxis.sqrMagnitude > 1e-6f ? _bendAxis.normalized : Vector3.up;
                }
                if (bend > Mat.BendLimit)
                {
                    Break(_bendEnd, bend > Mat.BendLimit * 1.5f ? 3 : 2, $"bent {bend:0.0}° in {ah.Name} (its alloy takes ~{Mat.BendLimit:0}°)");
                    sb.Append("-> breaks; ");
                    break;
                }
                if (ah.Era && ah.Los >= 80f)
                {
                    Break(tStart + b.Dur, 3, "cut by the ERA's flyer plates");
                    sb.Append("-> heavy ERA cuts it; ");
                    break;
                }
            }
            if (BreakSim == float.MaxValue && rec.Events.Any(e => e.Text != null && e.Text.StartsWith("Shattered")))
            {
                Break(Bites.Count > 0 ? Bites[Bites.Count - 1].SimStart : _endSim, 6, "shattered (the game's verdict: impact angle)");
                sb.Append("shattered; ");
            }
            sb.Append(BreakSim < float.MaxValue ? $"breaks: {BreakWhy}" : Bites.Count > 0 && Bites[Bites.Count - 1].Stopped ? "used up" : $"leaves with {L * 1000f:0} mm of rod");
            Report = sb.ToString();
        }

        /// A ricochet: the game reflects the round at the impact point; the plate draws the groove it ploughs (PlateSim's
        /// TrenchOf: ~0.45 L_rod cos a + 2 depth / tan a long, slid at ~0.85 v cos a). The rod's own path is made to
        /// match: in, along the groove riding in it, then the game's way out from the groove's end (later by the slide).
        void RicochetChannel(HitRecord rec)
        {
            var ah = rec.ArmorHits.Where(a => a.Frag == rec.Main && !a.IsSpall && !a.PreJet).OrderBy(a => a.Sim).FirstOrDefault(a => a.Ricochet);
            if (ah == null || _p.Length < 2) return;
            Vector3 dIn = ah.DirL.sqrMagnitude > 1e-6f ? ah.DirL.normalized : DirAt(ArcAtSim(ah.Sim));
            Vector3 n = ah.HasSurf ? ah.SurfNormalL : ah.NormalL;
            n = n.sqrMagnitude > 1e-6f ? n.normalized : -dIn;
            if (Vector3.Dot(n, dIn) > 0f) n = -n;
            Vector3 t = Vector3.ProjectOnPlane(dIn, n);
            if (t.sqrMagnitude < 1e-6f) return;
            t.Normalize();
            var g = HoleFx.TrenchOf(ah);
            if (_grooveLen > 0f) g.Len = _grooveLen;     // the groove as the plate draws it
            float r = D * 0.5f;
            Vector3 hit = ah.HasSurf ? ah.SurfPointL : ah.L;
            Vector3 pGame = PathAt(ArcAtSim(ah.Sim), out _);
            Vector3 p0 = hit + n * (r * 0.6f);              // riding in the groove it digs
            Vector3 gEnd = p0 + t * g.Len;
            float slide = g.Len / Mathf.Max(g.Speed, 50f);
            var ps = new List<Vector3>();
            var ts = new List<float>();
            for (int i = 0; i < _p.Length; i++) if (_t[i] < ah.Sim - 1e-7f) { ps.Add(_p[i]); ts.Add(_t[i]); }
            ps.Add(p0); ts.Add(ah.Sim);
            ps.Add(gEnd); ts.Add(ah.Sim + slide);
            Vector3 shift = gEnd - pGame;
            Vector3 dOut = Vector3.zero;
            for (int i = 0; i < _p.Length; i++)
                if (_t[i] > ah.Sim + 1e-7f)
                {
                    Vector3 q = _p[i] + shift;
                    if (dOut == Vector3.zero && (q - gEnd).sqrMagnitude > 1e-4f) dOut = (q - gEnd).normalized;
                    ps.Add(q); ts.Add(_t[i] + slide);
                }
            if (dOut == Vector3.zero) { dOut = Vector3.Reflect(dIn, n); ps.Add(gEnd + dOut * 30f); ts.Add(ah.Sim + slide + 30f / Mathf.Max(_vEnd, 100f)); }
            var ss = new List<float>();
            var pp = new List<Vector3>();
            var tt = new List<float>();
            float acc = 0f;
            for (int i = 0; i < ps.Count; i++)
            {
                if (pp.Count > 0)
                {
                    float d = Vector3.Distance(pp[pp.Count - 1], ps[i]);
                    if (d < 1e-5f || ts[i] <= tt[tt.Count - 1]) continue;
                    acc += d;
                }
                pp.Add(ps[i]); ss.Add(acc); tt.Add(ts[i]);
            }
            if (pp.Count < 2) return;
            _p = pp.ToArray(); _s = ss.ToArray(); _t = tt.ToArray();
            int c = _p.Length;
            _vEnd = Mathf.Max((_s[c - 1] - _s[c - 2]) / Mathf.Max(_t[c - 1] - _t[c - 2], 1e-7f), 50f);
            _endSim = _t[c - 1];
            _ric = true; _ricP = hit; _ricN = n;
            _ricSlideStart = ah.Sim; _ricSlideEnd = ah.Sim + slide; _ricLen = g.Len;
            _ricTurn = Vector3.Angle(dIn, t) + Vector3.Angle(t, dOut);
        }

        /// Nothing of the rod goes under the plate it ricochets off.
        Vector3 AbovePlate(Vector3 p, float rad)
        {
            if (!_ric) return p;
            Vector3 q = p - _ricP;
            float h = Vector3.Dot(q, _ricN);
            if (h >= rad || Vector3.ProjectOnPlane(q, _ricN).sqrMagnitude > 1.5f * 1.5f) return p;
            return p + _ricN * (rad - h);
        }

        /// The rod's deceleration while it erodes: the plastic wave at its nose holds back the rest, a = Y_p / (rho_p L).
        float RodPhysics_Decel(float len) => Mat.Yp / (Mat.Rho * Mathf.Max(len, 2f * D));

        void Break(float sim, int pieces, string why)
        {
            if (sim >= BreakSim) return;
            BreakSim = sim; BreakPieces = pieces; BreakWhy = why;
        }

        // ------------------------------------------------------------------ path helpers

        static int Upper(float[] a, float x)
        {
            int lo = 0, hi = a.Length - 1;
            while (lo < hi) { int m = (lo + hi) >> 1; if (a[m] < x) lo = m + 1; else hi = m; }
            return lo;
        }

        float ArcAtSim(float sim)
        {
            int n = _t.Length;
            if (n == 0) return 0f;
            if (sim <= _t[0]) return _s[0] + (sim - _t[0]) * _vStart;
            if (sim >= _t[n - 1]) return _s[n - 1] + (sim - _t[n - 1]) * _vEnd;
            int i = Mathf.Max(Upper(_t, sim), 1);
            float a = _t[i - 1], b = _t[i];
            return Mathf.Lerp(_s[i - 1], _s[i], b > a ? (sim - a) / (b - a) : 1f);
        }

        float SimAtArc(float s)
        {
            int n = _s.Length;
            if (n == 0) return 0f;
            if (s <= _s[0]) return _t[0] + (s - _s[0]) / _vStart;
            if (s >= _s[n - 1]) return _t[n - 1] + (s - _s[n - 1]) / _vEnd;
            int i = Mathf.Max(Upper(_s, s), 1);
            float a = _s[i - 1], b = _s[i];
            return Mathf.Lerp(_t[i - 1], _t[i], b > a ? (s - a) / (b - a) : 1f);
        }

        Vector3 PathAt(float s, out Vector3 dir)
        {
            int n = _p.Length;
            dir = Vector3.forward;
            if (n == 0) return Vector3.zero;
            if (n == 1) return _p[0];
            if (s <= _s[0]) { dir = (_p[1] - _p[0]).normalized; return _p[0] + dir * (s - _s[0]); }
            if (s >= _s[n - 1]) { dir = (_p[n - 1] - _p[n - 2]).normalized; return _p[n - 1] + dir * (s - _s[n - 1]); }
            int i = Mathf.Max(Upper(_s, s), 1);
            dir = (_p[i] - _p[i - 1]).normalized;
            float a = _s[i - 1], b = _s[i];
            return Vector3.Lerp(_p[i - 1], _p[i], b > a ? (s - a) / (b - a) : 1f);
        }

        Vector3 DirAt(float s) { PathAt(s, out var d); return d; }

        // ------------------------------------------------------------------ per frame

        void Hide()
        {
            _tube.Show(false);
            foreach (var r in _fin) r.enabled = false;
            _capGlow.enabled = _tracer.enabled = false;
        }

        /// sim = the round's clock, now = session time, simRate = sim seconds per session second (the slow motion),
        /// cam = the camera in the parent's space.
        public void Update(float sim, float now, float simRate, Vector3 cam)
        {
            _lastSim = sim;
            float first = Bites.Count > 0 ? Bites[0].SimStart : BreakSim;
            Visible = sim >= first;
            if (!Visible) { Hide(); return; }
            bool stoppedPlan = Bites.Count > 0 && Bites[Bites.Count - 1].Stopped;
            // past the end of the game's path a rod that went through is gone; a used-up stub stays in its crater
            if (!stoppedPlan && sim > _endSim + 0.002f) { Hide(); Visible = false; return; }

            float tRef = Mathf.Min(sim, _stopSim);
            float sRef = ArcAtSim(tRef);
            float E = Erosion(tRef, sRef, out bool eroding, out bool allGone);
            if (allGone && _stopSim == float.MaxValue) _stopSim = sim;
            UsedUp = allGone;
            Eroding = eroding && !allGone;
            if (Eroding) _erodeEnd = -1f; else if (_erodeEnd < 0f && E > 0f) _erodeEnd = sim;
            Length = Mathf.Max(L0 - E, 0.004f);
            float sNose = NoseArc(tRef, sRef);
            if (AutoTest.Active && Time.frameCount != _logFrame && (_logged++ % 3 == 0 || Eroding))
            {
                _logFrame = Time.frameCount;
                HitCamMod.Log.Msg($"x-ray rod track: sim {sim * 1e6f:0.0} us, nose {sNose * 1000f:0.0} mm, tail {(sNose - Length) * 1000f:0.0} mm, eroded {E * 1000f:0.0} mm{(Eroding ? " (eroding)" : "")}");
            }

            if (sim >= BreakSim) { DrawPieces(sim, sRef, cam, now); UpdateDebris(now, simRate, cam); return; }

            Centre(sim, sNose, Length, E, cam);
            _tube.Begin();
            _tube.AddTube(_node, _rad, _col, _nn, cam, 0f, 0f, NoseCurl(E), 17, sim * 2000f);
            _tube.End(true);
            Nose = _node[_nn - 1];
            Dir = (_node[_nn - 1] - _node[_nn - 3]).normalized;
            DrawFins(_node[0], (_node[2] - _node[0]).normalized, cam);
            // the glow of the eroding head
            _capGlow.enabled = Eroding;
            if (Eroding)
            {
                _capGlow.transform.localPosition = Nose;
                _capGlow.transform.localRotation = Quaternion.LookRotation(Nose - cam);
                _capGlow.transform.localScale = Vector3.one * D * 6f * (0.85f + 0.15f * Mathf.Sin(now * 47f));
                Vis.SetColor(_capGlow, Mat.Hot * 0.65f);
            }
            UpdateDebris(now, simRate, cam);
        }

        int _logFrame = -1, _logged;

        /// How much of it the plates have eaten by tRef. While its nose is in a plate the rear keeps the rod's own speed
        /// v and the nose goes the game's pace (or sticks in the crater of a plate that stops it): what the rear gains on
        /// the nose is eaten off the front, up to that plate's share. So the rear never stalls and never speeds up.
        float Erosion(float tRef, float sRef, out bool eroding, out bool allGone)
        {
            float E = 0f;
            eroding = allGone = false;
            foreach (var b in Bites)
            {
                if (tRef < b.SimStart) break;
                float t = Mathf.Min(tRef, b.SimStart + b.Dur);
                float gained = b.V * (t - b.SimStart) - (NoseArc(t, ArcAtSim(t)) - NoseArc(b.SimStart, ArcAtSim(b.SimStart)));
                float e = Mathf.Clamp(gained, 0f, b.Loss);
                if (tRef < b.SimStart + b.Dur && e < b.Loss - 1e-5f) eroding = true;
                if (b.Stopped && e >= b.Loss - 1e-5f) allGone = true;
                E += e;
            }
            return E;
        }

        /// Where its nose is: on the game's path, held in the crater of a plate that stopped it.
        float NoseArc(float tRef, float sRef)
        {
            foreach (var b in Bites)
                if (b.Stopped && tRef >= b.SimStart) return Mathf.Min(sRef, b.Cap);
            return sRef;
        }

        /// The eroding head's lip, curling back (bigger for tungsten, small for self-sharpening uranium).
        float NoseCurl(float E)
        {
            if (!_deform || E <= 0f) return 0f;
            float k = Mathf.Clamp01(E / (1.2f * D));
            return D * (_flare - 1f) * (Eroding ? 0.9f : 0.5f) * k;
        }

        /// The centreline along its channel (bent where the channel turns), the residual bend of an oblique entry, the
        /// flexural wave it set off; the radius (the mushroom, the squeezed bulge behind it); the heat.
        void Centre(float sim, float sNose, float len, float E, Vector3 cam)
        {
            float sTail = sNose - len;
            float bendK = _bendDeg > 0.05f ? Mathf.Clamp01((sim - _bendStart) / Mathf.Max(_bendEnd - _bendStart, 1e-7f)) : 0f;
            float bendRad = _bendDeg * Mathf.Deg2Rad * bendK;
            float r0 = D * 0.5f;
            float onset = Mathf.Clamp01(E / (1.2f * D));
            float flare = !_deform ? 1f : 1f + (_flare - 1f) * onset * (Eroding ? 1f : 0.55f);
            // the flexural wave runs back from the nose at ~1.2 km/s and dies out in about half a millisecond
            float wAge = sim - _waveStart;
            float wA = _deform && wAge > 0f ? _waveA * Mathf.Exp(-wAge / 6e-4f) : 0f;
            float lambda = 7f * D, front = 1200f * Mathf.Max(wAge, 0f);
            Vector3 lateral = _bendAxis.sqrMagnitude > 0.5f ? _bendAxis : Vector3.up;
            float cool = _erodeEnd < 0f ? 1f : Mathf.Exp(-(sim - _erodeEnd) / 0.004f);
            for (int i = 0; i < _nn; i++)
            {
                float f = (float)i / (_nn - 1);
                Vector3 p = PathAt(sTail + len * f, out _);
                float g = 1f - f;   // the tail swings off the line its nose keeps
                p -= _bendAxis * (0.5f * g * g * len * bendRad);
                float dn = len * (1f - f);
                if (wA > 0f && dn < front)
                    p += lateral * (wA * Mathf.Sin(2f * Mathf.PI * (dn - front) / lambda) * Mathf.Clamp01((front - dn) / lambda));
                float r = r0;
                if (_deform)
                {
                    r = r0 * (1f + (flare - 1f) * Mathf.Exp(-dn / (0.45f * D)));
                    if (Eroding) r *= 1f + 0.06f * Mathf.Exp(-Mathf.Pow((dn - 1.6f * D) / (0.7f * D), 2f));   // squeezed behind the head
                }
                _rad[i] = r;
                _node[i] = AbovePlate(p, r);
                float heat = Mathf.Exp(-dn / (1.3f * D)) * (E > 0f ? cool : 0f) * (Eroding ? 1f : 0.6f);
                _col[i] = Heat(Mat.Color, heat);
            }
        }

        /// Hot metal: dull red, orange, yellow-white.
        static Color Heat(Color metal, float h)
        {
            if (h <= 0.02f) return metal;
            Color glow = h < 0.4f ? Color.Lerp(new Color(0.5f, 0.07f, 0.02f), new Color(0.95f, 0.32f, 0.05f), h / 0.4f)
                       : h < 0.75f ? Color.Lerp(new Color(0.95f, 0.32f, 0.05f), new Color(1f, 0.7f, 0.25f), (h - 0.4f) / 0.35f)
                       : Color.Lerp(new Color(1f, 0.7f, 0.25f), new Color(1f, 0.96f, 0.85f), (h - 0.75f) / 0.25f);
            return Color.Lerp(metal, glow, Mathf.Clamp01(h * 1.5f));
        }

        void DrawFins(Vector3 tail, Vector3 dTail, Vector3 cam)
        {
            bool fins = Length > _finLen * 1.3f;
            Vector3 r0 = Vector3.Cross(dTail, Vector3.up);
            if (r0.sqrMagnitude < 1e-4f) r0 = Vector3.Cross(dTail, Vector3.right);
            r0.Normalize();
            for (int j = 0; j < 4; j++)
            {
                var r = _fin[j];
                r.enabled = fins;
                if (!fins) continue;
                Vector3 rad = Quaternion.AngleAxis(45f + 90f * j, dTail) * r0;
                var tr = r.transform;
                tr.localPosition = tail + dTail * (_finLen * 0.5f) + rad * (D * 0.5f + (_finSpan * 0.5f - D * 0.5f) * 0.5f);
                tr.localRotation = Quaternion.LookRotation(dTail, rad);
                tr.localScale = new Vector3(Mathf.Max(D * 0.12f, 0.002f), Mathf.Max(_finSpan * 0.5f - D * 0.5f, 0.005f), _finLen);
                Vis.SetColor(r, j % 2 == 0 ? new Color(0.84f, 0.86f, 0.88f) : new Color(0.70f, 0.72f, 0.75f));   // aluminium, as on the round in flight; two faces lit, two in shade
            }
            _tracer.enabled = fins;
            if (fins)
            {
                _tracer.transform.localPosition = tail - dTail * (D * 0.3f);
                _tracer.transform.localRotation = Quaternion.LookRotation(tail - cam);
                _tracer.transform.localScale = Vector3.one * D * 3f;
                Vis.SetColor(_tracer, new Color(1f, 0.55f, 0.25f) * 0.8f);
            }
        }

        /// The eroded material leaves the crater backwards and outwards (in the lab it moves back at about (v - 2u)
        /// with a strong radial spread): hot flakes, white burning sparks for uranium (it's pyrophoric).
        void UpdateDebris(float now, float simRate, Vector3 cam)
        {
            if (Eroding && now >= _nextDebris && simRate > 0f)
            {
                _nextDebris = now + 1f / 60f;
                var b = Bites.LastOrDefault(x => x.SimStart <= _lastSim);
                float vr = Mathf.Max(b.V - b.U, 200f);
                int k = Mat.A == RodPhysics.Alloy.DU ? 6 : 4;
                for (int i = 0; i < k; i++)
                {
                    Vector3 side = Vector3.ProjectOnPlane(Random.onUnitSphere, Dir).normalized;
                    Vector3 v = (-Dir * Random.Range(0.1f, 0.45f) + side * Random.Range(0.15f, 0.5f)) * vr * simRate;
                    _debris.Add(new Sparks.P { P0 = Nose + side * D * 0.6f, V = v, T0 = now, Life = Random.Range(1.2f, 2.6f), Size = D * Random.Range(0.08f, 0.16f), C = Mat.Spark });
                }
            }
            _debris.Tick(now, cam);
        }

        float _lastSim;

        /// Broken: every piece keeps the bent shape it had (and the front one its mushroom), with jagged fracture faces,
        /// flying on along the path, spreading apart at tens of m/s and spinning.
        void DrawPieces(float sim, float sRef, Vector3 cam, float now)
        {
            foreach (var r in _fin) r.enabled = false;
            _capGlow.enabled = false;
            _tracer.enabled = false;
            int n = Mathf.Max(BreakPieces, 2);
            if (_pieces == null)
            {
                // the shape at the moment it breaks
                float tb = Mathf.Min(BreakSim, sim);
                float sRefB = ArcAtSim(Mathf.Min(tb, _stopSim));
                _breakE = Erosion(tb, sRefB, out _, out _);
                _breakLen = Mathf.Max(L0 - _breakE, 0.004f);
                Centre(tb, NoseArc(tb, sRefB), _breakLen, _breakE, cam);
                Vector3 dirB = (_node[_nn - 1] - _node[0]).normalized;
                _breakRot = Quaternion.LookRotation(dirB);
                var inv = Quaternion.Inverse(_breakRot);
                var rnd = new System.Random(n * 7 + (int)(L0 * 1000f));
                float R() => (float)rnd.NextDouble();
                // cut points: unevenly spaced along it
                var cuts = new List<int> { 0 };
                for (int j = 1; j < n; j++) cuts.Add(Mathf.Clamp(Mathf.RoundToInt((_nn - 1) * ((float)j / n + (R() - 0.5f) * 0.18f)), cuts[cuts.Count - 1] + 2, _nn - 1 - 2 * (n - j)));
                cuts.Add(_nn - 1);
                _pieces = new Piece[n];
                for (int j = 0; j < n; j++)
                {
                    int a = cuts[j], b = cuts[j + 1];
                    int count = b - a + 1;
                    var pc = new Piece { Local = new Vector3[count], Rad = new float[count], Col = new Color[count], Count = count };
                    Vector3 c = Vector3.zero;
                    for (int i = a; i <= b; i++) c += _node[i];
                    c /= count;
                    for (int i = a; i <= b; i++) { pc.Local[i - a] = inv * (_node[i] - c); pc.Rad[i - a] = _rad[i]; pc.Col[i - a] = _col[i]; }
                    // how far its middle sits behind the nose
                    pc.Offset = _breakLen * (1f - (a + b) * 0.5f / (_nn - 1));
                    pc.Side = Vector3.ProjectOnPlane(new Vector3(R() - 0.5f, R() - 0.5f, R() - 0.5f), dirB).normalized;
                    pc.Axis = new Vector3(R() - 0.5f, R() - 0.5f, R() - 0.5f).normalized;
                    pc.Spin = 300f + 1200f * R();   // rad/s: a fracture throws the pieces spinning
                    pc.Front = b == _nn - 1; pc.Back = a == 0;
                    _pieces[n - 1 - j] = pc;          // [0] = the front piece
                }
            }
            float age = Mathf.Max(sim - BreakSim, 0f);
            float spread = BreakPieces >= 6 ? 70f : 28f;
            float sNose = NoseArc(Mathf.Min(sim, _stopSim), sRef);
            _tube.Begin();
            var tmp = new Vector3[_nn];
            for (int j = 0; j < _pieces.Length; j++)
            {
                var pc = _pieces[j];
                Vector3 c = PathAt(sNose - pc.Offset, out var d) + pc.Side * (spread * (0.6f + 0.4f * j) * age);
                Quaternion rot = Quaternion.AngleAxis(pc.Spin * age * Mathf.Rad2Deg, pc.Axis) * Quaternion.FromToRotation(_breakRot * Vector3.forward, d) * _breakRot;
                for (int i = 0; i < pc.Count; i++) tmp[i] = AbovePlate(c + rot * pc.Local[i], pc.Rad[i]);
                float jag = D * 0.45f;
                _tube.AddTube(tmp, pc.Rad, pc.Col, pc.Count, cam, pc.Back ? 0f : jag, pc.Front ? 0f : jag, pc.Front ? NoseCurl(_breakE) : 0f, 31 + j * 5, sim * 2000f);
                // the tail piece keeps its fins and tracer
                if (pc.Back && pc.Count >= 3) DrawFins(tmp[0], (tmp[2] - tmp[0]).normalized, cam);
            }
            _tube.End(true);
            // what the camera follows: the way the pieces are going, not the spinning tip of the front one
            Nose = PathAt(sNose, out var dPath);
            Dir = dPath;
        }

        public void Dispose() { _debris.Dispose(); _tube.Dispose(); }
    }
}
