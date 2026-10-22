using System.Collections.Generic;
using UnityEngine;

namespace GHPCHitCam
{
    /// how a plate fails under one hit: the classic classes from Backman & Goldsmith, picked by the plate's LOS thickness
    /// vs the penetrator's diameter and by round type
    internal enum PlateMode
    {
        Dent,     // held: crater + lip, dished plate, no hole
        Gouge,    // ricochet: long scrape, lip at the far end
        Petal,    // thin plate vs fat round: radial cracks, petals fold inward
        Plug,     // medium plate vs blunt round: a disc gets sheared out and flies on
        Ductile,  // thick plate vs rod: hole pushed open, crater lip, back-splash
        Jet,      // shaped charge jet: narrow hole, wide shallow crater
        Tear,     // HE through thin armor: big petals, some break off and fly in with the blast
        Trench,   // grazing hit: ricochet ploughs a groove with raised lips, grazing pen cuts a long slot
    }

    /// one armor impact WT style, as a plate that deforms and breaks in slow-mo
    /// panel is a polar mesh lying on the plate around the hit, rectangular at its border. runs on the plate's own clock:
    /// physical seconds since impact, same slow-mo clock as the round. the plate:
    /// - gets pushed in by the round. pen takes LOS / v. plastic dishing spreads at hinge speed sqrt(sigma_y / rho)
    ///   ~ 360 m/s, so a 0.3 m zone needs ~1 ms. permanent depth = energy vs plastic bending (thick) or membrane
    ///   stretching (thin)
    /// - rings with damped flexural ripples that freeze into the plastic set
    /// - fails by its mode: cracks run out at ~1.4 km/s, petals fold about their hinge chords, plugs shear out,
    ///   ductile holes get pushed open into a lip, HE petals break off and fly inward
    /// - coloured by stress, not heat: red = failure at the hole, log falloff of a 1/r field goes orange, yellow, green to
    ///   blue at the edge of the zone still bending (bulging). plastic front grows over time so the colours spread
    /// - throws chips: front-face ejecta and back-splash, or plate fragments for HE
    /// PlateWorld.cs puts the same plate, settled and steel coloured, on the real vehicles in the world
    internal partial class HoleFx
    {
        const float Yield = 1.0e9f;          // Pa, armor steel (RHA ~0.9-1.1 GPa)
        const float HingeSpeed = 357f;       // m/s, sqrt(sigma_y / rho)
        const float CrackSpeed = 1400f;      // m/s, running cracks in steel (~0.4 x Rayleigh wave speed)
        const float VisualDish = 2f;         // real dishing is mm..cm, doubled so it reads at hit cam distance

        public ArmorHit Hit;
        public float Wall;                   // session time the round reaches the plate
        public PlateMode Mode;
        public float Tau;                    // plate clock: physical seconds since impact
        public float TPen, TResp, TauTotal;  // pen time, plate response time, whole process
        public float Size;                   // panel width (m)
        public float Feature => _rFeat;      // radius of the hole / crater / petals (m)
        public float TrenchLen => Mode == PlateMode.Trench ? _tr.Len : 0f;   // groove as drawn (clamped at the plate's edge)
        public Transform T;
        /// round is through and the plate let go (spawn the physics fragments now)
        public bool Broke => Tau >= TPen * 0.8f && Mode != PlateMode.Dent && Mode != PlateMode.Gouge;

        Renderer _panel, _chips, _glow;
        LineRenderer _border;
        Mesh _mesh, _chipMesh;
        readonly List<Mesh> _owned = new List<Mesh>();
        System.Random _rnd;

        // geometry / physics of this hit
        float _hx, _hy, _R, _rFeat, _rCore, _rHole, _rCrater, _rPlug, _depthC, _lipH, _dish, _ripA, _ripL, _cE, _frontMax, _peak, _t, _elong = 1f;
        Vector2 _g = Vector2.right;          // gouge direction in the plate
        Vector2 _gn = Vector2.up;            // across it
        float _stretch = 1f;                 // oblique holes: features stretched along _g by 1/sin(angle)
        bool _stretchOn;                     // set by both builds (x-ray and world), off only while a plate is being set up
        TrenchGeo _tr;
        // the real plate (hit armour piece's face) in panel space: its triangles and outline
        bool _hasPlate;
        Vector2[] _faceTri;                  // 3 corners per triangle
        float[] _faceZ;                      // their depth along the panel normal (curved plates)
        Vector2[] _outline;                  // outline edges, 2 points each
        bool _terraced;                      // collider is a stack of slabs: its face smoothed into one surface
        readonly List<LineRenderer> _plateLines = new List<LineRenderer>();
        float _edgeBlend = 0.05f;
        float _tx0, _tx1, _ty;               // trench panel: _tx0 to _tx1 along _g, +-_ty across
        bool _low, _started, _he, _pen, _shaped;
        float _lastSim, _minWall, _age, _glowAt = -1f, _settle;
        Vector3 _camL = new Vector3(0f, 0f, -5f), _camUpL = Vector3.up;
        // stop working out shape and colours once nothing visibly changes (most of a hit cam): a plate is thousands of
        // verts, three of them took ~6 ms a frame
        int _quiet;                 // frames in a row the shape moved less than StillMove
        bool _still;                // shape done: no more vertex / normal updates
        float _paintTau = -1f;
        Vector3 _paintCam, _paintUp;
        const float StillMove = 3e-5f;   // m

        struct Vtx { public Vector2 P; public float R, Re, Th, Z0, Cover, EdgeD; public int W, Ring; public float CrackW, CrackT; public bool Hot, Off; }

        class Wedge
        {
            public float A0, A1;
            public int Cols, Rings, V0;
            public float RStart, RMid;
            public int NIn, NOut;
            public bool ToBorder, Petal, Plug, Detach;
            public Vector2 E, N;                 // mid-direction and its normal (petal frame)
            public float Ah, PhiMax;             // hinge chord distance from centre, final fold angle
            public float TBreak, Drag, Omega;
            public Vector3 Vel, Axis, C;
            public bool CSet;
            public int Grid;                     // trench panels: 0 = polar wedge; -1 / +1 = half-grid on that side of the path
        }

        struct Crack { public Vector2 A, B; public float TA, TB, W; public bool Hot; }

        struct Chip { public Vector3 P0, Vel, Axis, Scale; public float T0, Drag, Spin; public int Shape; }

        Vtx[] _v;
        List<Wedge> _w;
        Vector3[] _pos;
        Color[] _col;
        readonly List<Vector3> _nrm = new List<Vector3>();
        int[] _borderIdx;
        Vector3[] _borderPos;
        Chip[] _chip;
        Vector3[] _chipPos;
        Color[] _chipCol;
        Vector3[][] _shardV;
        Color[][] _shardC;

        // ------------------------------------------------------------------ physics helpers

        /// energy (J) the plate itself soaks up. a round that goes through keeps most of it: KE round or jet loses about
        /// LOS / pen of it (same as the game's residual-pen model). a stopped round dumps all of it into the plate, a ricochet
        /// a little, an HE charge tearing thin armor a good part of its coupled blast
        public static float Absorbed(ArmorHit ah)
        {
            float e = Mathf.Max(ah.Energy, 1000f);
            if (ah.Ricochet) return 0.15f * e;
            if (!ah.Penetrated) return e;
            if (ah.Category == AmmoType.AmmoCategory.Explosive) return 0.5f * e;
            float los = Mathf.Max(ah.Los, ah.Nominal, 1f);
            return e * Mathf.Clamp(los / Mathf.Max(ah.Pen, los), 0.01f, 1f);
        }

        /// radius (m) of the plastically bending zone: R ~ sqrt(E / (sigma_y * t))
        public static float BendRadius(ArmorHit ah)
            => Mathf.Clamp(1.2f * Mathf.Sqrt(Mathf.Max(Absorbed(ah), 500f) / (Yield * Thick(ah))), 0.06f, 1.6f);

        static float Thick(ArmorHit ah) => Mathf.Clamp(ah.Nominal / 1000f, 0.003f, 0.5f);
        static float LosT(ArmorHit ah) => Mathf.Clamp(Mathf.Max(ah.Los, ah.Nominal) / 1000f, Thick(ah), 1.5f);
        static float RodR(ArmorHit ah)
        {
            float cal = Mathf.Max(ah.Caliber * 0.5f, 0.002f);
            return ah.RodRadius > 0.0015f ? Mathf.Min(ah.RodRadius, cal) : cal;
        }

        /// below this angle to the plate (deg) a KE round skims: failure runs along the surface, not through
        public const float GrazeAngle = 20f;

        /// angle the round met the plate at: drawn surface if found, else the hit collider
        public static float ImpactAngle(ArmorHit ah) => ah.HasSurf ? ah.SurfAngle : ah.Angle;

        static bool IsKE(ArmorHit ah) => ah.Category == AmmoType.AmmoCategory.Penetrator;

        public static PlateMode ModeOf(ArmorHit ah)
        {
            if (ah.Era) return PlateMode.Dent;
            // ricochet never makes a hole, it ploughs a groove and leaves; a KE round skimming the plate cuts or gouges along it
            if (ah.Ricochet || (IsKE(ah) && ImpactAngle(ah) < GrazeAngle)) return PlateMode.Trench;
            return LegacyMode(ah);
        }

        /// point-like failure modes (also what world plates use until they draw trenches)
        static PlateMode LegacyMode(ArmorHit ah)
        {
            if (ah.Era) return PlateMode.Dent;
            if (ah.Ricochet) return PlateMode.Gouge;
            if (!ah.Penetrated) return PlateMode.Dent;
            if (ah.Category == AmmoType.AmmoCategory.Explosive) return PlateMode.Tear;
            if (ah.Category == AmmoType.AmmoCategory.ShapedCharge) return PlateMode.Jet;
            float d = RodR(ah) * 2f;
            bool rod = ah.Caliber > 0f && d < ah.Caliber * 0.6f;
            float ratio = LosT(ah) / Mathf.Max(d, 0.004f);
            if (ratio < (rod ? 0.45f : 0.6f)) return PlateMode.Petal;
            if (ratio < (rod ? 1.2f : 1.6f)) return PlateMode.Plug;
            return PlateMode.Ductile;
        }

        /// grazing hit's groove / slot, physics:
        /// - slot through a plate of thickness t at angle a is t / tan(a) long, plus the penetrator's own footprint d / sin(a)
        /// - ricochet digs in to a depth that grows with the angle (normal velocity part), then the rod slides about half its
        ///   length while turning out: L ~ 0.45 L_rod cos(a) + 2 depth / tan(a)
        /// - round that stopped at a grazing angle (crushed) ploughs in and ends in a crater
        /// rod length from its mass (KE energy / speed) and tungsten density; full-bore AP shot is ~3.2 calibres long
        public struct TrenchGeo { public float Len, HalfW, Depth, Speed; public bool Slot, Ric; }

        public static TrenchGeo TrenchOf(ArmorHit ah)
        {
            float angD = Mathf.Clamp(ImpactAngle(ah), 1f, 30f);
            float a = angD * Mathf.Deg2Rad;
            float d = 2f * RodR(ah);
            float cal = Mathf.Max(ah.Caliber, d);
            float v = ah.Speed > 50f ? ah.Speed : 900f;
            float t = Thick(ah);
            bool ke = IsKE(ah);
            float lr;
            if (ke && d < cal * 0.6f)
            {
                float m = 2f * Mathf.Max(ah.Energy, 1f) / (v * v);
                lr = Mathf.Clamp(m / (17600f * Mathf.PI * 0.25f * d * d), 10f * d, 35f * d);
            }
            else lr = (ke ? 3.2f : 4f) * cal;
            var g = new TrenchGeo { Ric = ah.Ricochet, Slot = ah.Penetrated && !ah.Ricochet };
            g.HalfW = ke ? 0.6f * d : 0.35f * cal;
            if (g.Slot)
            {
                g.Depth = t;
                g.Len = Mathf.Clamp(t / Mathf.Tan(a) + 0.5f * d / Mathf.Sin(a), 2.5f * d, 1.6f);
            }
            else
            {
                g.Depth = ke ? Mathf.Clamp(0.3f * d * (angD / 12f), 0.0015f, Mathf.Min(0.7f * d, 0.45f * t)) : 0.002f;
                g.Len = ah.Ricochet
                    ? Mathf.Clamp(ke ? 0.45f * lr * Mathf.Cos(a) + 2f * g.Depth / Mathf.Tan(a) : 1.5f * cal, 2.5f * d, 1.4f)
                    : Mathf.Clamp(2f * g.Depth / Mathf.Tan(a) + 0.3f * lr, 2.5f * d, 1.2f);
            }
            g.Speed = Mathf.Max(v * Mathf.Cos(a) * 0.85f, 100f);
            return g;
        }

        /// find the drawn armour surface at the hit: short rays from around the impact (along the round, against the collider
        /// normal, from above / below / both sides of the path) onto the x-ray copy's hull meshes, nearest face within 10 cm
        /// that the round was going into wins
        public static void ProbeSurface(ArmorHit ah, GhostPhysics phys)
        {
            if (ah.SurfDone) return;
            ah.SurfDone = true;
            if (phys == null || !phys.HasHull || ah.DirL.sqrMagnitude < 1e-6f) return;
            Vector3 d = ah.DirL.normalized;
            Vector3 nc = ah.NormalL.sqrMagnitude > 0.01f ? ah.NormalL.normalized : -d;
            Vector3 side = Vector3.Cross(d, Vector3.up);
            if (side.sqrMagnitude < 0.01f) side = Vector3.Cross(d, Vector3.forward);
            side.Normalize();
            Vector3 up = Vector3.Cross(side, d).normalized;
            Vector3[] rays = { d, -nc, -up, up, side, -side };
            float best = 0.1f;
            bool found = false;
            Vector3 bp = Vector3.zero, bn = Vector3.zero;
            foreach (var r in rays)
            {
                Vector3 o = ah.L - r * 0.3f;
                if (!phys.RaycastHull(o, r, 0.5f, out float dist, out Vector3 n) || n.sqrMagnitude < 0.5f) continue;
                n.Normalize();
                Vector3 hp = o + r * dist;
                float off = (hp - ah.L).magnitude;
                if (off >= best || Vector3.Dot(n, d) > 0.05f) continue;    // round can't have gone into a face looking away from it
                best = off; bp = hp; bn = n; found = true;
            }
            if (!found) return;
            ah.HasSurf = true;
            ah.SurfPointL = bp;
            ah.SurfNormalL = bn;
            ah.SurfAngle = Mathf.Max(0.5f, 90f - Vector3.Angle(bn, -d));
        }

        /// seconds the penetrator needs to get through the plate (the plate's fast process)
        public static float PenTime(ArmorHit ah) => PenTime(ah, ModeOf(ah));

        static float PenTime(ArmorHit ah, PlateMode mode)
        {
            float v = ah.Speed > 50f ? ah.Speed : 900f;
            float t;
            switch (mode)
            {
                case PlateMode.Trench: { var g = TrenchOf(ah); t = g.Len / g.Speed; break; }   // round slides the trench's length
                case PlateMode.Jet: t = LosT(ah) / 3000f; break;   // jet tip pen velocity ~3 km/s
                case PlateMode.Tear: t = 1.5e-4f * Mathf.Pow(Mathf.Max(ah.Tnt, 0.05f), 0.333f) + LosT(ah) / Mathf.Max(v, 250f); break;   // blast loading + shell
                default: t = LosT(ah) / Mathf.Max(v, 250f); break;
            }
            return Mathf.Clamp(t, 8e-6f, 2e-3f);
        }

        /// sim seconds worth stretching for the first plate: round going through and petals / plug / crater forming (fold
        /// runs on the hinge-speed time scale of the bending zone)
        public static float DwellSpan(ArmorHit ah)
        {
            float tPen = PenTime(ah);
            float tResp = Mathf.Clamp(BendRadius(ah) / HingeSpeed, 5e-5f, 6e-3f);
            return Mathf.Clamp(1.6f * tPen + 0.25f * tResp, 3e-5f, 8e-4f);
        }

        float Rn(float a, float b) => a + (float)_rnd.NextDouble() * (b - a);
        static Vector2 Dir(float a) => new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        static float Smooth(float e0, float e1, float x) { float u = Mathf.Clamp01((x - e0) / (e1 - e0)); return u * u * (3f - 2f * u); }
        static float Ease(float x) { x = Mathf.Clamp01(x); float u = 1f - x; return 1f - u * u * u; }
        static float Bump(float x, float a, float b) { float u = (x - a) / (b - a); return u <= 0f || u >= 1f ? 0f : Mathf.Sin(Mathf.PI * u); }

        // ------------------------------------------------------------------ build

        public static HoleFx Create(Transform parent, ArmorHit ah, float wall, int seed, bool compact, GhostPhysics phys)
        {
            var fx = new HoleFx { Hit = ah, Wall = wall, _rnd = new System.Random(seed) };
            fx.Build(parent, compact, phys);
            return fx;
        }

        void Build(Transform parent, bool compact, GhostPhysics phys)
        {
            var ah = Hit;
            var go = new GameObject("plate " + ah.Name);
            go.layer = Vis.Layer;
            T = go.transform;
            T.SetParent(parent, false);
            ProbeSurface(ah, phys);
            // real plate's face if the armour piece's shape is known, else drawn armour, else the hit collider
            Vector3 n = ah.HasSurf ? ah.SurfNormalL : ah.NormalL.sqrMagnitude > 0.01f ? ah.NormalL.normalized : -ah.DirL.normalized;
            Vector3 at = ah.HasSurf ? ah.SurfPointL : ah.L;
            Prepare(ah, phys);
            var face = PlateFace(ah, out Vector3 fn, out Vector3 fat);
            if (face != null) { n = fn; at = fat; }
            // effect's +Z points into the plate (against the outward normal)
            T.localPosition = at - n * 0.004f;
            T.localRotation = Quaternion.LookRotation(-n, Mathf.Abs(Vector3.Dot(n, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up);

            _stretchOn = true;
            if (face != null) ToPanel(face, at);
            float v = Setup(compact);
            if (_hasPlate) ClipToPlate();
            BuildWedges(v);
            BuildMesh();
            if (_hasPlate) OnPlate();
            if (_hasPlate && _terraced) FlattenTerraces();
            Conform(phys);
            BuildCracks();
            BuildChips(v);

            var pt = Vis.Child(T, "stress panel", _mesh, Matrix4x4.identity, Vis.Panel);
            _panel = pt.GetComponent<Renderer>();
            _border = BorderLine(T);
            if (_hasPlate) PlateLines();
            if (_chipMesh != null) _chips = Vis.Child(T, "chips", _chipMesh, Matrix4x4.identity, Vis.Unlit).GetComponent<Renderer>();
            if (_pen)
            {
                // where the fragments come out: red-orange glow on the inside of the plate
                float inside = Mathf.Clamp(_t, 0.01f, 0.3f);
                _glow = Vis.Child(T, "exit glow", Vis.SoftDisc, Matrix4x4.TRS(Vector3.forward * inside, Quaternion.identity, Vector3.one * Mathf.Clamp(_R * 0.5f, 0.1f, 0.45f)), Vis.Additive).GetComponent<Renderer>();
                _glow.enabled = false;
            }
            Deform(true);
            SetVisible(false);
        }

        /// physics of this hit: failure mode, feature radii, crater, dishing, panel size, time scales
        /// T has to sit on the plate already (its rotation gives the gouge direction). returns impact speed
        float Setup(bool compact)
        {
            var ah = Hit;
            Mode = ModeOf(ah);
            _pen = ah.Penetrated && !ah.Era;
            _he = ah.Category == AmmoType.AmmoCategory.Explosive;
            _low = compact || ah.IsSpall;
            _minWall = compact ? 0.45f : 0.85f;
            _t = Thick(ah);
            float v = ah.Speed > 50f ? ah.Speed : 900f;
            float rh = Mathf.Max(ah.HoleRadius, 0.006f);
            float calR = Mathf.Max(ah.Caliber * 0.5f, 0.006f);

            TPen = PenTime(ah, Mode);
            if (Mode == PlateMode.Trench) _tr = TrenchOf(ah);
            switch (Mode)
            {
                case PlateMode.Trench:
                    _rCrater = _tr.HalfW;
                    _rCore = _tr.HalfW * 1.4f;
                    _rFeat = Mathf.Max(3f * _tr.HalfW, 0.5f * _tr.Len);
                    break;
                case PlateMode.Petal: _rFeat = rh * 1.1f; _rCore = _rFeat * 0.9f; break;
                case PlateMode.Tear: _rFeat = rh * 1.15f; _rCore = _rFeat * 0.9f; break;
                case PlateMode.Plug: _rPlug = Mathf.Max(rh, RodR(ah) * 1.1f); _rFeat = _rPlug * 1.7f; _rCore = _rPlug * 1.15f; break;
                case PlateMode.Ductile: _rHole = rh; _rCrater = rh * 2.2f; _rFeat = _rCrater * 1.3f; _rCore = rh * 1.3f; break;
                case PlateMode.Jet: _rHole = rh * 0.8f; _rCrater = rh * 2.6f; _rFeat = _rCrater * 1.3f; _rCore = _rHole * 1.6f; break;
                case PlateMode.Gouge: _rCrater = Mathf.Max(calR * 0.9f, 0.01f); _elong = 2.6f; _rFeat = _rCrater * _elong * 1.6f; _rCore = _rCrater * 0.8f; break;
                default:
                    _rCrater = _he ? Mathf.Max(rh * 0.8f, 0.03f) : Mathf.Max(calR * 0.9f, 0.01f);
                    _rFeat = _rCrater * 1.8f; _rCore = _rCrater * 0.7f; break;
            }
            _R = Mathf.Max(BendRadius(ah), _rCore * 2.5f);
            TResp = Mathf.Clamp(_R / HingeSpeed, 5e-5f, 6e-3f);
            TauTotal = Mathf.Max(3f * TPen, TResp);

            // crater: how deep the round got into a plate that held; a funnel for holes
            float got = Mathf.Min(Mathf.Max(ah.Pen, 1f), Mathf.Max(ah.Los, ah.Nominal) * 0.9f) / 1000f;
            if (Mode == PlateMode.Dent || Mode == PlateMode.Gouge)
                _depthC = Mathf.Clamp(got * 0.5f, 0.003f, Mathf.Min(0.06f, _rCrater * 1.2f));
            else
                _depthC = Mathf.Min(_t * 0.5f, (_rCrater > 0f ? _rCrater : _rFeat) * 0.6f);
            if (_he && !_pen) _depthC = Mathf.Min(_depthC, 0.012f);   // blast scoops a shallow, wide crater
            if (Mode == PlateMode.Trench) _depthC = _tr.Depth;
            _lipH = Mode == PlateMode.Ductile || Mode == PlateMode.Jet ? _rHole * 0.25f
                  : Mode == PlateMode.Trench ? 0.35f * _tr.Depth + 0.12f * _tr.HalfW
                  : _depthC * 0.3f + _rCrater * 0.06f;
            _peak = _pen ? 1f : ah.Era ? 0.5f : Mode == PlateMode.Trench ? 0.85f : Mode == PlateMode.Gouge ? 0.45f : 0.55f + 0.4f * Mathf.Clamp01(ah.Pen / Mathf.Max(ah.Los, 1f));

            // permanent dishing: far-field share of the absorbed energy (rest goes into local failure)
            // vs plastic bending (thick plates) or membrane stretching (thin plates)
            float cDish;
            if (ah.Era) cDish = 0.05f;
            else if (Mode == PlateMode.Dent || Mode == PlateMode.Gouge || (Mode == PlateMode.Trench && !_tr.Slot)) cDish = 0.6f;
            else if (Mode == PlateMode.Tear) cDish = 0.5f;
            else if (Mode == PlateMode.Jet) cDish = 0.1f;
            else cDish = 0.3f;
            float ed = Mathf.Max(Absorbed(ah), 500f) * cDish;
            float dBend = 2f * ed / (Mathf.PI * Yield * _t * _t * 4f);
            float dMem = Mathf.Sqrt(2f * ed / (Mathf.PI * Yield * _t));
            _dish = Mathf.Clamp(Mathf.Min(dBend, dMem) * VisualDish, 0.0015f, Mathf.Min(0.3f * _R, 0.22f));

            float H = Mathf.Clamp(Mathf.Max(1.2f * _R, 2.3f * _rFeat), 0.14f, 2.0f);
            _hx = H * 1.15f;
            _hy = H * 0.9f;
            Size = 2f * _hx;
            _ripL = H / 6.5f;
            _ripA = 0.0035f * H + 0.12f * _dish;
            _frontMax = 1.8f * _hx;
            _cE = _frontMax / (0.45f * TResp);
            _settle = 6f * TauTotal;

            {
                // round's path along the plate
                Vector3 d = Quaternion.Inverse(T.localRotation) * ah.DirL;
                var g = new Vector2(d.x, d.y);
                _g = g.sqrMagnitude > 1e-4f ? g.normalized : Vector2.right;
                _gn = new Vector2(-_g.y, _g.x);
            }
            // oblique hole = ellipse 1/sin(angle) long along the path (capped; below the graze angle it's a trench)
            _stretch = Mode == PlateMode.Trench || ah.Era ? 1f
                     : Mathf.Clamp(1f / Mathf.Sin(Mathf.Max(ImpactAngle(ah), GrazeAngle) * Mathf.Deg2Rad), 1f, 2.4f);
            if (Mode == PlateMode.Trench)
            {
                // panel follows the groove: from just before impact to past its end, a strip either side
                // (on a big casting a wider one drew half the turret, terraces and all, like a sheet thrown over it)
                float m = Mathf.Clamp(Mathf.Max(1.1f * _R, 6f * _tr.HalfW), 0.1f, 0.25f);
                _tx0 = -m;
                _tx1 = _tr.Len + m;
                _ty = m;
                _hx = 0.5f * (_tx1 - _tx0);
                _hy = m;
                Size = _tx1 - _tx0;
                _ripL = Mathf.Max(_ty, 0.05f) / 3.5f;
                _frontMax = 1.8f * _hx;
                _cE = _frontMax / (0.45f * TResp);
            }
            return v;
        }

        void BuildWedges(float v)
        {
            _w = new List<Wedge>();
            int ns = _low ? 40 : 96;
            int nIn = _low ? 4 : 9, nOut = _low ? 10 : 24;
            if (Mode == PlateMode.Trench)
            {
                // two half-grids meeting on the path so a slot can open between them
                int nx = _low ? 36 : 96, ny = _low ? 8 : 22;
                _w.Add(new Wedge { Grid = -1, Cols = nx, NIn = 0, NOut = ny });
                _w.Add(new Wedge { Grid = 1, Cols = nx, NIn = 0, NOut = ny });
                return;
            }
            if (Mode == PlateMode.Petal || Mode == PlateMode.Tear)
            {
                int n = Mode == PlateMode.Petal ? 4 + _rnd.Next(3) : 5 + _rnd.Next(4);
                float jit = Mode == PlateMode.Petal ? 0.15f : 0.4f;
                var widths = new float[n];
                float sum = 0f;
                for (int i = 0; i < n; i++) { widths[i] = 1f + Rn(-jit, jit); sum += widths[i]; }
                float a = Rn(0f, Mathf.PI * 2f);
                bool anyDetach = false;
                for (int i = 0; i < n; i++)
                {
                    float wa = widths[i] / sum * Mathf.PI * 2f;
                    float m = a + wa * 0.5f;
                    int cols = Mathf.Max(3, Mathf.RoundToInt(ns * wa / (Mathf.PI * 2f)));
                    // petal itself (inside hole radius) is its own block so it can tear off along the arc
                    var w = new Wedge
                    {
                        A0 = a, A1 = a + wa, Cols = cols,
                        RStart = 0f, RMid = _rFeat, NIn = nIn, NOut = 0, Petal = true,
                        E = Dir(m), N = new Vector2(-Mathf.Sin(m), Mathf.Cos(m)),
                        Ah = _rFeat * Mathf.Cos(wa * 0.5f),
                        PhiMax = Mode == PlateMode.Petal ? Rn(1.35f, 1.75f) : Rn(1.6f, 2.4f),
                    };
                    if (Mode == PlateMode.Tear && (Rn(0f, 1f) < 0.45f || (i == n - 1 && !anyDetach)))
                    {
                        // torn off at the hinge and thrown in with the blast; heavy pieces, so slow and spinning
                        anyDetach = true;
                        w.Detach = true;
                        w.TBreak = TPen * Rn(0.8f, 1.8f) + 0.15f * TResp;
                        float sp = Rn(80f, 260f);
                        w.Vel = new Vector3(w.E.x * Rn(0.1f, 0.5f) + w.N.x * Rn(-0.2f, 0.2f), w.E.y * Rn(0.1f, 0.5f) + w.N.y * Rn(-0.2f, 0.2f), 1f).normalized * sp;
                        w.Drag = 2.5e-3f;
                        w.Axis = new Vector3(w.N.x + Rn(-0.4f, 0.4f), w.N.y + Rn(-0.4f, 0.4f), Rn(-0.4f, 0.4f)).normalized;
                        w.Omega = sp / Mathf.Max(w.Ah, 0.05f) * Rn(0.3f, 0.9f);
                    }
                    _w.Add(w);
                    _w.Add(new Wedge { A0 = a, A1 = a + wa, Cols = cols, RStart = _rFeat, RMid = _rFeat, NIn = 0, NOut = nOut, ToBorder = true });
                    a += wa;
                }
            }
            else
            {
                float a0 = Rn(0f, Mathf.PI * 2f);
                _w.Add(new Wedge { A0 = a0, A1 = a0 + Mathf.PI * 2f, Cols = ns, RStart = Mode == PlateMode.Plug ? _rPlug : 0f, RMid = _rFeat, NIn = nIn, NOut = nOut, ToBorder = true });
                if (Mode == PlateMode.Plug)
                {
                    float vpl = 0.55f * v;    // plug leaves at roughly the round's residual speed
                    float ax = Rn(0f, Mathf.PI * 2f);
                    _w.Add(new Wedge
                    {
                        A0 = a0, A1 = a0 + Mathf.PI * 2f, Cols = Mathf.Max(16, ns / 2), RStart = 0f, RMid = _rPlug, NIn = Mathf.Max(3, nIn / 2), NOut = 0,
                        Plug = true, TBreak = TPen * 0.75f, Vel = Vector3.forward * vpl, Drag = 1.5e-3f,
                        Axis = new Vector3(Mathf.Cos(ax), Mathf.Sin(ax), 0f), Omega = vpl / (8f * _rPlug) * Rn(0.5f, 1f),
                    });
                }
            }
        }

        float ElliptR(Vector2 p)
        {
            if (Mode != PlateMode.Gouge) return p.magnitude;
            float x = Vector2.Dot(p, _g) - 0.5f * _rCrater * (_elong - 1f);
            float y = Vector2.Dot(p, new Vector2(-_g.y, _g.x));
            return Mathf.Sqrt(x * x / (_elong * _elong) + y * y);
        }

        void BuildMesh()
        {
            var verts = new List<Vtx>();
            var tris = new List<int>();
            var border = new List<int>();
            for (int wi = 0; wi < _w.Count; wi++)
            {
                var w = _w[wi];
                int nr = w.NIn + w.NOut;
                w.Rings = nr + 1;
                w.V0 = verts.Count;
                if (w.Grid != 0)
                {
                    for (int c = 0; c <= w.Cols; c++)
                    {
                        float x = Mathf.Lerp(_tx0, _tx1, (float)c / w.Cols);
                        for (int i = 0; i <= nr; i++)
                        {
                            // rows crowd toward the path (groove is a few cm wide on a panel tens of cm across)
                            float y = w.Grid * _ty * Mathf.Pow((float)i / nr, 1.8f);
                            var pg = _g * x + _gn * y;
                            var vx = new Vtx { P = pg, R = pg.magnitude, Th = Mathf.Atan2(pg.y, pg.x), W = wi, Ring = i };
                            vx.Re = Capsule(x, y, _tr.Len);
                            verts.Add(vx);
                        }
                    }
                    for (int c = 0; c < w.Cols; c++)
                        for (int i = 0; i < nr; i++)
                        {
                            int a = w.V0 + c * w.Rings + i, b = a + w.Rings;
                            if (w.Grid < 0) { tris.Add(a); tris.Add(b); tris.Add(a + 1); tris.Add(b); tris.Add(b + 1); tris.Add(a + 1); }
                            else { tris.Add(a); tris.Add(a + 1); tris.Add(b); tris.Add(b); tris.Add(a + 1); tris.Add(b + 1); }
                        }
                    continue;
                }
                for (int c = 0; c <= w.Cols; c++)
                {
                    float th = Mathf.Lerp(w.A0, w.A1, (float)c / w.Cols);
                    Vector2 dir = Dir(th);
                    float edge = 1f / Mathf.Max(Mathf.Abs(dir.x) / _hx, Mathf.Abs(dir.y) / _hy);
                    for (int i = 0; i <= nr; i++)
                    {
                        float r = i <= w.NIn
                            ? Mathf.Lerp(w.RStart, w.RMid, w.NIn > 0 ? (float)i / w.NIn : 1f)
                            : w.RMid + (edge - w.RMid) * Mathf.Pow((float)(i - w.NIn) / w.NOut, 1.5f);
                        var vx = new Vtx { P = dir * r, R = r, Th = th, W = wi, Ring = i };
                        vx.Re = ElliptR(vx.P);
                        verts.Add(vx);
                    }
                    if (w.ToBorder && c < w.Cols) border.Add(w.V0 + c * w.Rings + nr);
                }
                for (int c = 0; c < w.Cols; c++)
                    for (int i = 0; i < nr; i++)
                    {
                        int a = w.V0 + c * w.Rings + i, b = a + w.Rings;
                        tris.Add(a); tris.Add(a + 1); tris.Add(b);
                        tris.Add(b); tris.Add(a + 1); tris.Add(b + 1);
                    }
            }
            if (Mode == PlateMode.Trench && _w.Count == 2)
            {
                // around the rectangle: lower edge out, up the far end, upper edge back, down the near end
                Wedge lo = _w[0], hi = _w[1];
                int nr = lo.Rings - 1;
                for (int c = 0; c < lo.Cols; c++) border.Add(lo.V0 + c * lo.Rings + nr);
                for (int i = nr; i > 0; i--) border.Add(lo.V0 + lo.Cols * lo.Rings + i);
                for (int i = 0; i < nr; i++) border.Add(hi.V0 + hi.Cols * hi.Rings + i);
                for (int c = hi.Cols; c > 0; c--) border.Add(hi.V0 + c * hi.Rings + nr);
                for (int i = nr; i > 0; i--) border.Add(hi.V0 + i);
                for (int i = 0; i < nr; i++) border.Add(lo.V0 + i);
            }
            _v = verts.ToArray();
            _pos = new Vector3[_v.Length];
            _col = new Color[_v.Length];
            for (int k = 0; k < _v.Length; k++) _pos[k] = Stretch(new Vector3(_v[k].P.x, _v[k].P.y, 0f), _v[k].R);
            _borderIdx = border.ToArray();
            _borderPos = new Vector3[_borderIdx.Length];
            _mesh = new Mesh { name = "plate sim" };
            _mesh.MarkDynamic();
            _mesh.vertices = _pos;
            _mesh.colors = _col;
            _mesh.SetTriangles(tris, 0);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            _owned.Add(_mesh);
        }

        bool _conformed, _borderOk = true;

        /// lay the panel onto the vehicle's real outer surface: every vertex raycast onto the hull (x-ray copy's own
        /// colliders) so it follows a curved turret and stops where the armor ends instead of sticking out into the air as
        /// a flat sheet. verts with no armor behind them get hidden
        void Conform(GhostPhysics phys)
        {
            if (phys == null || !phys.HasHull) return;
            float probe = Mathf.Clamp(0.1f + 0.15f * Mathf.Max(_hx, _hy), 0.12f, 0.45f);
            Vector3 o = T.localPosition, inward = T.localRotation * Vector3.forward;
            if (!phys.RaycastHull(o - inward * probe, inward, probe * 2f, out float dc)) return;   // no hull mesh here (unreadable): stay flat
            float zc = dc - probe;
            int off = 0;
            for (int k = 0; k < _v.Length; k++)
            {
                var vx = _v[k];
                Vector3 p = o + T.localRotation * Stretch(new Vector3(vx.P.x, vx.P.y, 0f), vx.R);
                if (_hasPlate)
                {
                    // plate is known: follow the drawn paint only where it's within a few cm of the armour face
                    // (not on a terraced one: it stays in the hit step's plane, where the rod is)
                    if (!_terraced && !vx.Off && phys.RaycastHull(p - inward * probe, inward, probe * 2f, out float dp) && Mathf.Abs(dp - probe - zc - vx.Z0) < 0.04f) vx.Z0 = dp - probe - zc;
                }
                else if (phys.RaycastHull(p - inward * probe, inward, probe * 2f, out float d) && Mathf.Abs(d - probe - zc) < probe * 0.9f)
                    vx.Z0 = d - probe - zc;
                else { vx.Off = true; off++; }
                _v[k] = vx;
            }
            if (!_hasPlate) foreach (int i in _borderIdx) if (_v[i].Off) { _borderOk = false; break; }
            _conformed = true;
            if (AutoTest.Active) HitCamMod.Log.Msg($"  plate {Hit.Name}: conformed to the hull, {off}/{_v.Length} vertices off the armor");
        }

        void AddLine(List<Crack> list, Vector2 a, Vector2 b, float ta, float tb, bool hot, float w)
            => list.Add(new Crack { A = a, B = b, TA = ta, TB = tb, Hot = hot, W = w });

        void BuildCracks()
        {
            var cr = new List<Crack>();
            float t0 = 0.1f * TPen;
            float Birth(float r) => t0 + r / CrackSpeed;
            if (Mode == PlateMode.Petal || Mode == PlateMode.Tear)
                foreach (var w in _w)
                {
                    if (!w.Petal) continue;
                    // seam between two petals (opens as they fold), then the crack runs on into the plate
                    float th = w.A0;
                    Vector2 d = Dir(th);
                    AddLine(cr, Vector2.zero, d * _rFeat, Birth(0f), Birth(_rFeat), true, 1f);
                    float len = _rFeat * (Mode == PlateMode.Petal ? Rn(1.15f, 1.6f) : Rn(1.4f, 2.8f));
                    Vector2 p = d * _rFeat;
                    float ang = th;
                    for (int s = 0; s < 2; s++)
                    {
                        ang += Rn(-0.14f, 0.14f);
                        Vector2 q = p + Dir(ang) * (len - _rFeat) * 0.5f;
                        AddLine(cr, p, q, Birth(p.magnitude), Birth(q.magnitude), true, 1f);
                        p = q;
                    }
                    int nb = Mode == PlateMode.Tear ? 2 : (Rn(0f, 1f) < 0.5f ? 1 : 0);
                    for (int b = 0; b < nb; b++)
                    {
                        float rb = _rFeat * Rn(1.0f, 1.3f);
                        Vector2 s0 = d * rb;
                        float ba = th + (Rn(0f, 1f) < 0.5f ? -1f : 1f) * Rn(0.45f, 0.85f);
                        Vector2 s1 = s0 + Dir(ba) * _rFeat * Rn(0.3f, 0.7f);
                        AddLine(cr, s0, s1, Birth(rb), Birth(rb) + (s1 - s0).magnitude / CrackSpeed, true, 0.8f);
                    }
                }
            if (_pen && Mode != PlateMode.Jet)
            {
                // fracture network of the failing core
                float rf = Mode == PlateMode.Plug ? _rPlug * 1.3f : Mode == PlateMode.Ductile ? _rCrater : _rFeat;
                int chains = _low ? 3 : 7;
                for (int c = 0; c < chains; c++)
                {
                    float r = rf * Rn(0.25f, 1.0f), th = Rn(0f, Mathf.PI * 2f);
                    Vector2 p = Dir(th) * r;
                    float ang = th + Rn(-0.9f, 0.9f);
                    float tb = TPen * Rn(0.15f, 0.9f);
                    int segs = 2 + _rnd.Next(2);
                    for (int s = 0; s < segs; s++)
                    {
                        Vector2 q = p + Dir(ang) * rf * Rn(0.2f, 0.45f);
                        float te = tb + (q - p).magnitude / CrackSpeed;
                        AddLine(cr, p, q, tb, te, true, 0.85f);
                        tb = te;
                        p = q;
                        ang += Rn(-0.7f, 0.7f);
                    }
                }
            }
            if (Mode == PlateMode.Trench)
            {
                float w = _tr.HalfW;
                if (_tr.Slot)
                    for (int s = -1; s <= 1; s += 2)
                    {
                        // the cut's torn edges, running out with the rod, + short tears off them
                        float y = s * 0.9f * w;
                        int segs = 6;
                        for (int i = 0; i < segs; i++)
                        {
                            float x0 = _tr.Len * i / segs, x1 = _tr.Len * (i + 1) / segs;
                            AddLine(cr, _g * x0 + _gn * (y + Rn(-0.2f, 0.2f) * w), _g * x1 + _gn * (y + Rn(-0.2f, 0.2f) * w), x0 / _tr.Speed, x1 / _tr.Speed, true, 1f);
                            if (Rn(0f, 1f) < 0.4f) { float xb = Rn(x0, x1); AddLine(cr, _g * xb + _gn * y, _g * (xb + Rn(-1f, 1f) * w) + _gn * (y + s * Rn(1.5f, 3.5f) * w), xb / _tr.Speed, xb / _tr.Speed + 3f * w / CrackSpeed, true, 0.8f); }
                        }
                    }
                else
                    for (int i = 0; i < (_low ? 3 : 7); i++)
                    {
                        // plowed lips crack across
                        float x = Rn(0.1f, 0.95f) * _tr.Len, s = Rn(0f, 1f) < 0.5f ? -1f : 1f;
                        AddLine(cr, _g * x + _gn * (s * 0.8f * w), _g * (x + Rn(-0.5f, 0.5f) * w) + _gn * (s * Rn(1.8f, 2.6f) * w), x / _tr.Speed, x / _tr.Speed + 2f * w / CrackSpeed, true, 0.7f);
                    }
            }
            if (Mode == PlateMode.Plug)
            {
                // shear circle the plug comes out along
                const int n = 28;
                for (int i = 0; i < n; i++)
                {
                    float a0 = i * Mathf.PI * 2f / n, a1 = (i + 1) * Mathf.PI * 2f / n;
                    float tb = TPen * (0.3f + 0.1f * Mathf.Sin(a0 * 3f));
                    AddLine(cr, Dir(a0) * _rPlug, Dir(a1) * _rPlug, tb, tb, true, 1f);
                }
            }
            if (Mode == PlateMode.Dent && _peak > 0.75f && !_he)
                for (int i = 0; i < 4; i++)
                {
                    float th = Rn(0f, Mathf.PI * 2f);
                    float r1 = _rCrater * Rn(1.6f, 2.4f);
                    AddLine(cr, Dir(th) * _rCrater * 0.9f, Dir(th + Rn(-0.1f, 0.1f)) * r1, 0.4f * TPen, 0.4f * TPen + r1 / CrackSpeed, true, 0.9f);
                }
            if (_he && _R > 0.35f)
                for (int i = 0; i < 2; i++)
                {
                    // fold creases across a big blast-loaded plate
                    Vector2 c = Dir(Rn(0f, 6.28f)) * Rn(0f, 0.3f * _R);
                    Vector2 d = Dir(Rn(0f, 6.28f));
                    float tb = 0.4f * TResp;
                    AddLine(cr, c - d * 0.9f * _hx, c + d * 0.9f * _hx, tb, tb + 1.8f * _hx / 600f, false, 0.7f);
                }

            // bake: each vertex remembers the nearest crack, how strongly it sits on it and when the crack gets there
            for (int k = 0; k < _v.Length; k++)
            {
                var vx = _v[k];
                float wid = Mathf.Clamp((_low ? 0.009f : 0.0045f) + vx.R * (_low ? 0.09f : 0.05f), 0.004f, 0.035f);
                float best = 0f;
                foreach (var c in cr)
                {
                    // most cracks are nowhere near this vertex
                    if (vx.P.x < Mathf.Min(c.A.x, c.B.x) - wid || vx.P.x > Mathf.Max(c.A.x, c.B.x) + wid
                        || vx.P.y < Mathf.Min(c.A.y, c.B.y) - wid || vx.P.y > Mathf.Max(c.A.y, c.B.y) + wid) continue;
                    Vector2 ab = c.B - c.A;
                    float u = ab.sqrMagnitude > 1e-10f ? Mathf.Clamp01(Vector2.Dot(vx.P - c.A, ab) / ab.sqrMagnitude) : 0f;
                    float d = (c.A + ab * u - vx.P).magnitude;
                    if (d >= wid) continue;
                    float wgt = c.W * (1f - Smooth(0.35f * wid, wid, d));
                    if (wgt > best)
                    {
                        best = wgt;
                        vx.CrackW = wgt;
                        vx.CrackT = Mathf.Lerp(c.TA, c.TB, u);
                        vx.Hot = c.Hot;
                    }
                }
                _v[k] = vx;
            }
        }

        void BuildChips(float v)
        {
            int n;
            switch (Mode)
            {
                case PlateMode.Petal: n = 14; break;
                case PlateMode.Plug: n = 10; break;
                case PlateMode.Ductile: n = 24; break;
                case PlateMode.Jet: n = 14; break;
                case PlateMode.Tear: n = 40; break;
                case PlateMode.Gouge: n = 10; break;
                case PlateMode.Trench: n = _tr.Slot ? 22 : 18; break;
                default: n = !Hit.Era && !_he && Hit.Energy > 2e5f ? 8 : 0; break;
            }
            if (_low) n /= 3;
            if (n <= 0) return;
            if (Mode == PlateMode.Trench)
            {
                // flakes shaved off along the groove: thrown forward and up off the plate (and into the slot when it cuts through)
                _chip = new Chip[n];
                for (int i = 0; i < n; i++)
                {
                    float x = Rn(0.05f, 1f) * _tr.Len;
                    bool into = _tr.Slot && Rn(0f, 1f) < 0.5f;
                    var g3 = new Vector3(_g.x, _g.y, 0f);
                    var n3 = new Vector3(_gn.x, _gn.y, 0f);
                    Vector3 d = g3 * Rn(0.6f, 1f) + n3 * Rn(-0.35f, 0.35f) + Vector3.forward * (into ? Rn(0.3f, 0.8f) : -Rn(0.15f, 0.6f));
                    float size = Mathf.Clamp(_tr.HalfW * Rn(0.25f, 0.7f), 0.003f, 0.03f);
                    _chip[i] = new Chip
                    {
                        T0 = x / _tr.Speed,
                        P0 = new Vector3(_g.x * x, _g.y * x, -0.002f),
                        Vel = d.normalized * v * Rn(0.05f, 0.2f),
                        Drag = Rn(1e-3f, 3e-3f),
                        Axis = new Vector3(Rn(-1f, 1f), Rn(-1f, 1f), Rn(-1f, 1f)).normalized,
                        Spin = Rn(2000f, 9000f) * (Rn(0f, 1f) < 0.5f ? -1f : 1f),
                        Scale = new Vector3(size * Rn(1f, 1.6f), size * Rn(0.3f, 0.5f), size * Rn(2f, 3.5f)),
                        Shape = _rnd.Next(Shards.Variants),
                    };
                }
                FinishChips(n);
                return;
            }
            float rf = Mode == PlateMode.Tear || Mode == PlateMode.Petal ? _rFeat : Mode == PlateMode.Plug ? _rPlug : _rCrater > 0f ? _rCrater : _rFeat;
            _chip = new Chip[n];
            for (int i = 0; i < n; i++)
            {
                Vector2 dir = Dir(Rn(0f, Mathf.PI * 2f));
                float r0 = rf * Rn(0.4f, 1.2f);
                bool inward = Mode == PlateMode.Tear && Rn(0f, 1f) < 0.6f;
                Vector3 d = inward
                    ? new Vector3(dir.x * Rn(0.2f, 0.7f), dir.y * Rn(0.2f, 0.7f), 1f)
                    : new Vector3(dir.x * Rn(0.4f, 1.1f), dir.y * Rn(0.4f, 1.1f), -Rn(0.5f, 1.2f));   // front-face ejecta / back-splash
                float sp = Mode == PlateMode.Jet ? Rn(300f, 900f) : Mode == PlateMode.Tear ? Rn(100f, 400f) : v * Rn(0.04f, 0.14f);
                float size = Mathf.Clamp(rf * Rn(0.12f, 0.35f), 0.003f, 0.05f);
                _chip[i] = new Chip
                {
                    T0 = TPen * Rn(0.1f, 1.4f),
                    P0 = new Vector3(dir.x * r0, dir.y * r0, -0.002f),
                    Vel = d.normalized * sp,
                    Drag = Rn(1e-3f, 3e-3f),
                    Axis = new Vector3(Rn(-1f, 1f), Rn(-1f, 1f), Rn(-1f, 1f)).normalized,
                    Spin = Rn(2000f, 8000f) * (Rn(0f, 1f) < 0.5f ? -1f : 1f),
                    Scale = new Vector3(size * Rn(1.2f, 2f), size * Rn(0.35f, 0.6f), size * Rn(1.6f, 2.6f)),   // torn steel flakes: wide, thin, long
                    Shape = _rnd.Next(Shards.Variants),
                };
            }
            FinishChips(n);
        }

        void FinishChips(int n)
        {
            _shardV = new Vector3[Shards.Variants][];
            _shardC = new Color[Shards.Variants][];
            for (int k = 0; k < Shards.Variants; k++) { var sm = Shards.Get(k); _shardV[k] = sm.vertices; _shardC[k] = sm.colors; }
            int per = _shardV[0].Length;
            _chipPos = new Vector3[n * per];
            _chipCol = new Color[n * per];
            var tri = new int[n * per];
            for (int i = 0; i < tri.Length; i++) tri[i] = i;
            _chipMesh = new Mesh { name = "plate chips" };
            _chipMesh.MarkDynamic();
            _chipMesh.vertices = _chipPos;
            _chipMesh.colors = _chipCol;
            _chipMesh.triangles = tri;
            _owned.Add(_chipMesh);
        }

        static LineRenderer BorderLine(Transform parent)
        {
            var go = new GameObject("border");
            go.layer = Vis.Layer;
            go.transform.SetParent(parent, false);
            var lr = go.AddComponent<LineRenderer>();
            Vis.Quiet(lr);
            lr.useWorldSpace = false;
            lr.loop = true;
            lr.sharedMaterial = Vis.Line;
            lr.widthMultiplier = 0.0035f;
            lr.startColor = lr.endColor = new Color(1f, 1f, 1f, 0f);
            return lr;
        }

        // ------------------------------------------------------------------ plate physics as functions of its clock

        /// distance from (x along the path, y across) to the groove's axis from 0 to len
        static float Capsule(float x, float y, float len)
        {
            float cx = Mathf.Clamp(x, 0f, Mathf.Max(len, 0f));
            float dx = x - cx;
            return Mathf.Sqrt(dx * dx + y * y);
        }

        /// oblique holes: in-plane position stretched along the path by up to 1/sin(angle) near the hole, easing to a third
        /// of that far out (slow enough with radius that the mesh never folds over)
        Vector3 Stretch(Vector3 q, float r)
        {
            if (!_stretchOn || _stretch <= 1.001f) return q;
            float rf = Mathf.Max(_rFeat, 1e-3f);
            float k = Mathf.Lerp(_stretch, 1f + (_stretch - 1f) * 0.3f, Smooth(1.5f * rf, 6f * rf, r));
            var p = new Vector2(q.x, q.y);
            float a = Vector2.Dot(p, _g);
            p += _g * (a * (k - 1f));
            return new Vector3(p.x, p.y, q.z);
        }

        float _clamp = 1f;

        // ------------------------------------------------------------------ the real plate

        /// face of the hit armour piece the round met: its triangles grouped by direction, the group (flat or gently curved
        /// face) nearest the hit with the most area facing the round wins. a grazing round that touched the thin end face
        /// of a plate gets the plate's big face, which is what it really skims along
        /// vehicle's centre in the same space as the armour hits (outward = away from it), set before plates get built
        public static Vector3 CentreL;

        static List<Vector3> PlateFace(ArmorHit ah, out Vector3 normal, out Vector3 at)
        {
            if (ah.FaceDone) { normal = ah.FaceN; at = ah.FaceAt; return ah.FaceTris; }
            var res = FindFace(ah, out normal, out at);
            ah.FaceDone = true; ah.FaceTris = res; ah.FaceN = normal; ah.FaceAt = at;
            return res;
        }

        static List<Vector3> FindFace(ArmorHit ah, out Vector3 normal, out Vector3 at)
        {
            normal = Vector3.zero; at = ah.L;
            var t = ah.PlateTris;
            if (t == null || t.Length < 9) return null;
            Vector3 d = ah.DirL.sqrMagnitude > 1e-6f ? ah.DirL.normalized : -ah.NormalL.normalized;
            // collider meshes don't wind their triangles consistently: every face gets turned to look out of the plate the round
            // came in through (drawn surface if found, else against the round, else away from the vehicle's centre)
            Vector3 OutRef(Vector3 n, Vector3 c)
            {
                if (ah.HasSurf && Mathf.Abs(Vector3.Dot(n, ah.SurfNormalL)) > 0.3f) return ah.SurfNormalL;
                if (Mathf.Abs(Vector3.Dot(n, d)) > 0.2f) return -d;
                return c - CentreL;
            }
            float tmax = Mathf.Max(ah.Nominal / 1000f, 0.004f) + 0.035f;
            t = Unique(t);
            int nt = t.Length / 3;
            var tn = new Vector3[nt];
            var ta = new float[nt];
            var near = new bool[nt];
            for (int i = 0; i < nt; i++)
            {
                Vector3 a = t[3 * i], b = t[3 * i + 1], c = t[3 * i + 2];
                Vector3 cr = Vector3.Cross(b - a, c - a);
                ta[i] = cr.magnitude * 0.5f;
                tn[i] = ta[i] > 1e-9f ? cr / (2f * ta[i]) : Vector3.zero;
                if (Vector3.Dot(tn[i], OutRef(tn[i], (a + b + c) / 3f)) < 0f) tn[i] = -tn[i];
                Vector3 cp = ClosestOnTri(ah.L, a, b, c);
                near[i] = (cp - ah.L).sqrMagnitude < 2.6f;   // within 1.6 m
            }
            // clusters of similar direction (within 18 deg), seeded by the biggest triangles
            var order = new List<int>();
            for (int i = 0; i < nt; i++) if (near[i] && ta[i] > 1e-7f) order.Add(i);
            order.Sort((x, y) => ta[y].CompareTo(ta[x]));
            var cn = new List<Vector3>();
            var cl = new List<List<int>>();
            foreach (int i in order)
            {
                int best = -1;
                for (int k = 0; k < cn.Count; k++) if (Vector3.Dot(cn[k].normalized, tn[i]) > 0.951f) { best = k; break; }
                if (best < 0) { if (cn.Count >= 32) continue; cn.Add(Vector3.zero); cl.Add(new List<int>()); best = cn.Count - 1; }
                cn[best] += tn[i] * ta[i];
                cl[best].Add(i);
            }
            float bestScore = 0f;
            int pick = -1;
            for (int k = 0; k < cn.Count; k++)
            {
                Vector3 nk = cn[k].normalized;
                // only the part of the face within the plate's thickness of the hit counts
                float area = 0f, distBest = float.MaxValue;
                foreach (int i in cl[k])
                {
                    Vector3 cp = ClosestOnTri(ah.L, t[3 * i], t[3 * i + 1], t[3 * i + 2]);
                    float dist = (cp - ah.L).magnitude;
                    distBest = Mathf.Min(distBest, Mathf.Abs(Vector3.Dot(ah.L - t[3 * i], tn[i])) + Mathf.Max(0f, dist - 0.4f));
                    if (dist < 1.2f) area += ta[i];
                }
                if (distBest > tmax) continue;
                float score = area / (0.02f + distBest);
                if (score > bestScore) { bestScore = score; pick = k; }
            }
            if (pick < 0) return null;
            // face the game says was hit wins, unless it's a sliver (thin end face of a plate the round slid onto)
            if (ah.NormalL.sqrMagnitude > 0.01f)
            {
                Vector3 hn = ah.NormalL.normalized;
                int struck = -1;
                float bestDot = 0.94f, pickArea = 0f, struckArea = 0f;
                for (int k = 0; k < cn.Count; k++)
                {
                    float dt = Mathf.Abs(Vector3.Dot(cn[k].normalized, hn));
                    if (dt > bestDot) { bestDot = dt; struck = k; }
                }
                if (struck >= 0 && struck != pick)
                {
                    foreach (int i in cl[pick]) if ((ClosestOnTri(ah.L, t[3 * i], t[3 * i + 1], t[3 * i + 2]) - ah.L).sqrMagnitude < 1.44f) pickArea += ta[i];
                    foreach (int i in cl[struck])
                    {
                        Vector3 cp = ClosestOnTri(ah.L, t[3 * i], t[3 * i + 1], t[3 * i + 2]);
                        if ((cp - ah.L).sqrMagnitude < 1.44f) struckArea += ta[i];
                    }
                    // thick block has no "end face": the face hit is the face
                    if (struckArea >= 0.3f * pickArea || ah.Nominal > 60f) pick = struck;
                }
            }
            normal = cn[pick].normalized;
            // hollow part (fuel tank on the fender) has its far wall in the same cluster, turned to face the round: the panel
            // then followed both walls and stretched back half a metre. the plate is the sheet the round hit + whatever lies in
            // its layer (stacked slabs of a cast turret aren't joined but sit within a few cm of it)
            var sheet = Sheet(t, tn, cl[pick], ah.L, Mathf.Max(tmax, 0.06f), out int dropped);
            if (dropped > 0 && AutoTest.Active) HitCamMod.Log.Msg($"  plate {ah.Name}: {dropped} of {cl[pick].Count} face triangles left out (a far wall of the part, {Mathf.Max(tmax, 0.06f) * 100f:0} cm+ behind the struck sheet)");
            cl[pick] = sheet;
            // the hit, moved onto that face along its normal
            float off = float.MaxValue;
            foreach (int i in cl[pick])
            {
                float dz = Vector3.Dot(ah.L - t[3 * i], tn[i]);
                Vector3 cp = ClosestOnTri(ah.L - tn[i] * dz, t[3 * i], t[3 * i + 1], t[3 * i + 2]);
                float dist = (cp - ah.L).magnitude;
                if (dist < off) { off = dist; at = cp; }
            }
            var res = new List<Vector3>(cl[pick].Count * 3);
            foreach (int i in cl[pick]) { res.Add(t[3 * i]); res.Add(t[3 * i + 1]); res.Add(t[3 * i + 2]); }
            return res;
        }

        /// face triangles joined (by shared corners) to the one nearest the hit, + any other joined piece whose plane
        /// passes within `layer` of the hit
        static List<int> Sheet(Vector3[] t, Vector3[] tn, List<int> tris, Vector3 hit, float layer, out int dropped)
        {
            dropped = 0;
            int n = tris.Count;
            if (n < 2) return tris;
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            long Key(Vector3 p) => ((long)Mathf.RoundToInt(p.x * 500f) * 73856093L) ^ ((long)Mathf.RoundToInt(p.y * 500f) * 19349663L) ^ ((long)Mathf.RoundToInt(p.z * 500f) * 83492791L);
            var byCorner = new Dictionary<long, int>();
            int nearest = 0;
            float nearestD = float.MaxValue;
            for (int k = 0; k < n; k++)
            {
                int i = tris[k];
                for (int e = 0; e < 3; e++)
                {
                    long key = Key(t[3 * i + e]);
                    if (byCorner.TryGetValue(key, out int other)) { int a = Find(k), b = Find(other); if (a != b) parent[a] = b; }
                    else byCorner[key] = k;
                }
                float d = (ClosestOnTri(hit, t[3 * i], t[3 * i + 1], t[3 * i + 2]) - hit).sqrMagnitude;
                if (d < nearestD) { nearestD = d; nearest = k; }
            }
            int root = Find(nearest);
            var depth = new Dictionary<int, float>();
            for (int k = 0; k < n; k++)
            {
                int i = tris[k], c = Find(k);
                float dz = Mathf.Abs(Vector3.Dot(hit - t[3 * i], tn[i]));
                depth[c] = depth.TryGetValue(c, out float cur) ? Mathf.Min(cur, dz) : dz;
            }
            var keep = new List<int>(n);
            for (int k = 0; k < n; k++)
            {
                int c = Find(k);
                if (c == root || depth[c] <= layer) keep.Add(tris[k]);
                else dropped++;
            }
            return keep;
        }

        /// everything about where and how the round met the plate, before any timing: drawn surface, then the real armour
        /// piece's face (it wins, that's the plate the game simulates), and the angle the round met it at
        public static void Prepare(ArmorHit ah, GhostPhysics phys)
        {
            ProbeSurface(ah, phys);
            if (ah.PlateTris == null || ah.DirL.sqrMagnitude < 1e-6f) return;
            if (PlateFace(ah, out Vector3 fn, out Vector3 fat) == null) return;
            ah.HasSurf = true;
            ah.SurfNormalL = fn;
            ah.SurfPointL = fat;
            ah.SurfAngle = Mathf.Max(0.5f, 90f - Vector3.Angle(fn, -ah.DirL.normalized));
        }

        static Vector3 ClosestOnTri(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            // Ericson, Real-Time Collision Detection 5.1.5
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
            Vector3 cpp = p - c;
            float d5 = Vector3.Dot(ab, cpp), d6 = Vector3.Dot(ac, cpp);
            if (d6 >= 0f && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
            float den = 1f / (va + vb + vc);
            return a + ab * (vb * den) + ac * (vc * den);
        }

        /// face triangles in panel space (T is placed on the face) and the face's outline edges
        void ToPanel(List<Vector3> face, Vector3 at)
        {
            var inv = Quaternion.Inverse(T.localRotation);
            int n = face.Count / 3;
            _faceTri = new Vector2[n * 3];
            _faceZ = new float[n * 3];
            var edges = new Dictionary<long, int>();
            var ends = new Dictionary<long, (Vector2, Vector2)>();
            var endZ = new Dictionary<long, float>();
            long Key(Vector3 p) => ((long)Mathf.RoundToInt(p.x * 500f) * 73856093L) ^ ((long)Mathf.RoundToInt(p.y * 500f) * 19349663L) ^ ((long)Mathf.RoundToInt(p.z * 500f) * 83492791L);
            for (int i = 0; i < n * 3; i++)
            {
                Vector3 q = inv * (face[i] - T.localPosition);
                _faceTri[i] = new Vector2(q.x, q.y);
                _faceZ[i] = q.z;
            }
            for (int i = 0; i < n; i++)
                for (int e = 0; e < 3; e++)
                {
                    int a = 3 * i + e, b = 3 * i + (e + 1) % 3;
                    long ka = Key(face[a]), kb = Key(face[b]);
                    long k = ka < kb ? ka * 31L + kb : kb * 31L + ka;
                    edges[k] = edges.TryGetValue(k, out int c) ? c + 1 : 1;
                    ends[k] = (_faceTri[a], _faceTri[b]);
                    endZ[k] = 0.5f * (_faceZ[a] + _faceZ[b]);
                }
            var segs = new List<(Vector2 a, Vector2 b, float z)>();
            foreach (var kv in edges) if (kv.Value == 1) { var (a, b) = ends[kv.Key]; segs.Add((a, b, endZ[kv.Key])); }
            // GHPC models some cast turrets as stacked slabs: slab tops are parallel faces at different heights, and seen
            // along the normal the top edge of one step lies on the foot of the next. those aren't real plate edges (they drew
            // stair-step walls deeper than the rod through the panel), so they go and the face gets smoothed into one surface
            // in OnPlate
            var inner = new bool[segs.Count];
            for (int i = 0; i < segs.Count; i++)
                for (int j = i + 1; j < segs.Count; j++)
                    if (Mathf.Abs(segs[i].z - segs[j].z) > 0.002f && Overlap(segs[i].a, segs[i].b, segs[j].a, segs[j].b))
                        inner[i] = inner[j] = true;
            var ol = new List<Vector2>();
            int dropped = 0;
            for (int i = 0; i < segs.Count; i++) { if (inner[i]) { dropped++; continue; } ol.Add(segs[i].a); ol.Add(segs[i].b); }
            _terraced = dropped > 0;
            _outline = ol.ToArray();
            _hasPlate = _outline.Length >= 6;
            if (_terraced && AutoTest.Active) HitCamMod.Log.Msg($"  plate {Hit.Name}: terraced collider, {dropped} step edges dropped, face smoothed");
        }

        /// panel window: at least half a metre around the hit so the plate's shape shows, never past the plate itself
        void ClipToPlate()
        {
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (var p in _faceTri) { minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y); }
            float ext = Mathf.Max(Mathf.Max(-minX, maxX), Mathf.Max(-minY, maxY));
            float want = Mathf.Clamp(Mathf.Max(_hx, 0.5f), 0.2f, 1.6f);
            if (Mode == PlateMode.Trench)
            {
                // groove ends where the plate does
                float edge = RayToOutline(Vector2.zero, _g);
                if (edge < float.MaxValue) _tr.Len = Mathf.Clamp(Mathf.Min(_tr.Len, edge), 2f * _tr.HalfW, 1.2f);
                _tx1 = Mathf.Min(_tx1, Mathf.Max(_tr.Len + 0.05f, maxX + 0.02f));
                return;
            }
            float h = Mathf.Min(Mathf.Max(want, _hx), ext + 0.02f);
            _hx = Mathf.Max(h, _rFeat * 1.6f);
            _hy = _hx;
            Size = 2f * _hx;
        }

        /// two outline edges lying on top of each other in the panel (collinear within 3 mm, overlapping most of the shorter)
        static bool Overlap(Vector2 a0, Vector2 a1, Vector2 b0, Vector2 b1)
        {
            Vector2 d = a1 - a0;
            float la = d.magnitude, lb = (b1 - b0).magnitude;
            if (la < 1e-4f || lb < 1e-4f) return false;
            d /= la;
            Vector2 n = new Vector2(-d.y, d.x);
            const float tol = 0.003f;
            if (Mathf.Abs(Vector2.Dot(b0 - a0, n)) > tol || Mathf.Abs(Vector2.Dot(b1 - a0, n)) > tol) return false;
            float t0 = Vector2.Dot(b0 - a0, d), t1 = Vector2.Dot(b1 - a0, d);
            float lo = Mathf.Max(0f, Mathf.Min(t0, t1)), hi = Mathf.Min(la, Mathf.Max(t0, t1));
            return hi - lo > 0.5f * Mathf.Min(la, lb);
        }

        /// terraced face: panel lies flat in the plane of the step the round hit. that's the plane the rod slides on
        /// (RodRig.RicochetChannel) and the game's hit is on; following the other steps drew stair walls, and a surface
        /// smoothed over all of them sat below the rod, which then wore away in mid-air above its groove
        void FlattenTerraces()
        {
            for (int k = 0; k < _v.Length; k++)
            {
                var vx = _v[k];
                vx.Z0 = 0f;
                _v[k] = vx;
            }
        }

        float RayToOutline(Vector2 o, Vector2 dir)
        {
            float best = float.MaxValue;
            for (int i = 0; i + 1 < _outline.Length; i += 2)
            {
                Vector2 a = _outline[i], b = _outline[i + 1], e = b - a;
                float den = dir.x * e.y - dir.y * e.x;
                if (Mathf.Abs(den) < 1e-9f) continue;
                Vector2 ao = a - o;
                float t = (ao.x * e.y - ao.y * e.x) / den, u = (ao.x * dir.y - ao.y * dir.x) / den;
                if (t > 1e-4f && u >= 0f && u <= 1f && t < best) best = t;
            }
            return best;
        }

        /// which panel verts lie on the plate (soft over one mesh cell at its edge), how far from its edge, and the plate
        /// face's own curvature under them
        void OnPlate()
        {
            for (int k = 0; k < _v.Length; k++)
            {
                var vx = _v[k];
                Vector3 sp = Stretch(new Vector3(vx.P.x, vx.P.y, 0f), vx.R);
                var p = new Vector2(sp.x, sp.y);
                float ed = float.MaxValue;
                for (int i = 0; i + 1 < _outline.Length; i += 2) ed = Mathf.Min(ed, SegDist(p, _outline[i], _outline[i + 1]));
                bool inside = false;
                float z = 0f;
                for (int i = 0; i + 2 < _faceTri.Length; i += 3)
                    if (InTri(p, _faceTri[i], _faceTri[i + 1], _faceTri[i + 2], out float u, out float w))
                    {
                        inside = true;
                        z = _faceZ[i] + (_faceZ[i + 1] - _faceZ[i]) * u + (_faceZ[i + 2] - _faceZ[i]) * w;
                        break;
                    }
                float cell = Mathf.Clamp(0.004f + vx.R * 0.06f, 0.004f, 0.04f);
                vx.EdgeD = inside ? ed : 0f;
                vx.Cover = inside ? Smooth(0f, cell, ed) : Mathf.Max(0f, 1f - ed / cell) * 0.0f;
                vx.Off = !inside && ed > cell;
                vx.Z0 = inside ? z : 0f;
                _v[k] = vx;
            }
            float minDim = 0.6f;
            if (_outline.Length >= 2)
            {
                // edge zone a clamped plate needs to come back to flat: a fraction of its size, max 25 cm
                float ext = 0f;
                foreach (var p in _faceTri) ext = Mathf.Max(ext, p.magnitude);
                minDim = ext;
            }
            _edgeBlend = Mathf.Clamp(0.25f * minDim, 0.03f, 0.25f);
        }

        static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float u = ab.sqrMagnitude > 1e-12f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
            return (a + ab * u - p).magnitude;
        }

        static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c, out float u, out float w)
        {
            Vector2 v0 = b - a, v1 = c - a, v2 = p - a;
            float den = v0.x * v1.y - v1.x * v0.y;
            u = w = 0f;
            if (Mathf.Abs(den) < 1e-12f) return false;
            u = (v2.x * v1.y - v1.x * v2.y) / den;
            w = (v0.x * v2.y - v2.x * v0.y) / den;
            return u >= -1e-4f && w >= -1e-4f && u + w <= 1.0001f;
        }

        /// plate's real outline, drawn where War Thunder draws the panel border
        void PlateLines()
        {
            _border.positionCount = 0;
            _borderOk = false;
            // chain the outline edges into loops
            var segs = new List<(Vector2 a, Vector2 b)>();
            for (int i = 0; i + 1 < _outline.Length; i += 2) segs.Add((_outline[i], _outline[i + 1]));
            float lim = Mathf.Max(_hx, _hy) * 1.05f;
            while (segs.Count > 0 && _plateLines.Count < 6)
            {
                var loop = new List<Vector2> { segs[0].a, segs[0].b };
                segs.RemoveAt(0);
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    Vector2 end = loop[loop.Count - 1];
                    for (int i = 0; i < segs.Count; i++)
                    {
                        if ((segs[i].a - end).sqrMagnitude < 1e-6f) { loop.Add(segs[i].b); segs.RemoveAt(i); grew = true; break; }
                        if ((segs[i].b - end).sqrMagnitude < 1e-6f) { loop.Add(segs[i].a); segs.RemoveAt(i); grew = true; break; }
                    }
                }
                if (loop.Count < 3) continue;
                var lr = BorderLine(T);
                lr.loop = (loop[0] - loop[loop.Count - 1]).sqrMagnitude < 1e-6f;
                var pts = new List<Vector3>();
                foreach (var p in loop)
                {
                    if (Mathf.Abs(p.x) > lim || Mathf.Abs(p.y) > lim) continue;    // past the panel window
                    pts.Add(new Vector3(p.x, p.y, PlateZ(p) - 0.003f));
                }
                if (pts.Count < 2) { Object.Destroy(lr.gameObject); continue; }
                lr.positionCount = pts.Count;
                lr.SetPositions(pts.ToArray());
                _plateLines.Add(lr);
            }
        }

        float PlateZ(Vector2 p)
        {
            for (int i = 0; i + 2 < _faceTri.Length; i += 3)
                if (InTri(p, _faceTri[i], _faceTri[i + 1], _faceTri[i + 2], out float u, out float w))
                    return _faceZ[i] + (_faceZ[i + 1] - _faceZ[i]) * u + (_faceZ[i + 2] - _faceZ[i]) * w;
            return 0f;
        }

        float Phi(Wedge w, float ts) => w.PhiMax * Ease((ts - 0.08f * TPen) / (1.6f * TPen + 0.25f * TResp));

        /// dishing at rest radius r: gaussian-like bulge out to the bending zone, critically damped growth with a small elastic rebound
        float Dish(float r, float ts)
        {
            float x = ts / TResp;
            float g = (1f - (1f + 3f * x) * Mathf.Exp(-3f * x)) * (1f + 0.15f * Mathf.Exp(-1.5f * x) * Mathf.Sin(5f * x));
            return _dish * Mathf.Exp(-1.7f * (r * r) / (_R * _R)) * g;
        }

        /// flexural ripples behind the outgoing wave front, they damp out and leave a faint plastic set
        float Ripple(float r, float ts)
        {
            float front = Mathf.Min(_cE * ts, _frontMax);
            float behind = 1f - Smooth(front - _ripL, front, r);
            if (behind <= 0f) return 0f;
            float wgt = Smooth(0.25f * _R, 0.7f * _R, r);
            float amp = _ripA * (0.4f + 0.6f * Mathf.Exp(-ts / (0.6f * TResp)));
            return amp * behind * wgt * Mathf.Sin(2f * Mathf.PI * (r - 0.3f * front) / _ripL);
        }

        /// 0..1 stress at (elliptic) radius re. failure inside the core, log falloff of the 1/r field out to the plastic front,
        /// past it the elastically bending plate (blue), relaxing toward the panel edge (pale)
        // stress zone radii at one moment (same for every vertex painted then)
        float _sTs = float.NaN, _sRc, _sRp, _sLogRp, _sInvLog;

        float Stress(float re, float ts)
        {
            if (ts != _sTs)
            {
                _sTs = ts;
                _sRc = _rCore * (0.25f + 0.75f * Mathf.Sqrt(Mathf.Clamp01(ts / TPen)));
                _sRp = _sRc + (_R - _sRc) * (1f - Mathf.Exp(-ts / (0.3f * TResp)));
                _sLogRp = _sRp > 0f ? Mathf.Log(_sRp) : 0f;
                _sInvLog = _sRp > _sRc * 1.02f && _sRc > 0f ? 1f / Mathf.Log(_sRp / _sRc) : 0f;
            }
            float rc = _sRc, rp = _sRp;
            if (re <= rc) return _peak;
            const float elastic = 0.22f;
            if (re >= rp || rp <= rc * 1.02f) return elastic * Mathf.Exp(-(re - Mathf.Max(rp, rc)) / (0.5f * _R));
            float f = Mathf.Clamp01((_sLogRp - Mathf.Log(re)) * _sInvLog);
            return Mathf.Lerp(elastic, _peak, Mathf.Pow(f, 0.65f));
        }

        /// vertex position while still part of the plate (everything except rigid flight of broken pieces)
        Vector3 Attached(Wedge w, Vector2 p, float r, float re, float th, float ts)
        {
            float z = 0f;
            float pIn = Mathf.Clamp01(ts / TPen);    // how far the penetrator got through
            switch (Mode)
            {
                case PlateMode.Ductile:
                case PlateMode.Jet:
                {
                    // ductile hole growth: material pushed aside radially into a funnel and a lip
                    float rhNow = _rHole * Mathf.Sqrt(pIn);
                    float rr = r;
                    if (r < _rCrater)
                    {
                        rr = rhNow + r * (1f - rhNow / _rCrater);
                        p = Dir(th) * rr;
                    }
                    float g = Ease(pIn);
                    z += _depthC * (1f - Smooth(_rHole, _rCrater, rr)) * g;
                    z -= _lipH * g * Bump(rr, _rCrater * 0.75f, _rCrater * 1.45f);
                    break;
                }
                case PlateMode.Dent:
                case PlateMode.Gouge:
                {
                    float g = Ease(ts / (1.5f * TPen));
                    float q = re / _rCrater;
                    if (q < 1f) z += _depthC * (1f - q * q) * g;
                    float lip = _lipH * g * Bump(re, _rCrater * 0.8f, _rCrater * 1.7f);
                    if (Mode == PlateMode.Gouge) lip *= 0.3f + 1.4f * Smooth(-0.2f, 0.8f, Vector2.Dot(p, _g) / (_rCrater * _elong));
                    z -= lip;
                    break;
                }
                case PlateMode.Plug:
                    if (!w.Plug) z += 0.25f * _t * pIn * (1f - Smooth(_rPlug, _rPlug * 1.6f, r));   // shear lip dragged inward
                    break;
                case PlateMode.Trench:
                {
                    float x = Vector2.Dot(p, _g), y = Vector2.Dot(p, _gn);
                    float hw = _tr.HalfW, len = Mathf.Max(_tr.Len, 1e-4f);
                    float xf = Mathf.Min(_tr.Speed * ts, len);                 // how far the round got along the plate
                    float cut = x < -hw ? 0f : Smooth(-hw, 0.5f * hw, xf - x);  // behind the round
                    float u = x / len;
                    // digs in over the first part; a ricochet climbs out at the end, a crushed round ends deepest
                    float env = Smooth(-0.05f, 0.18f, u) * (_tr.Slot ? 1f - Smooth(1f, 1.04f, u)
                              : _tr.Ric ? 1f - Smooth(0.72f, 1.05f, u)
                              : (0.6f + 0.4f * Mathf.Clamp01(u)) * (1f - Smooth(1f, 1.12f, u)));
                    float ay = Mathf.Abs(y), k = cut * env;
                    if (_tr.Slot)
                    {
                        // cut open: the two sides get pushed apart and their edges dragged into the plate
                        float half = 0.85f * hw * k, wc = 2.6f * hw;
                        if (ay < wc && half > 0f) y = w.Grid * (half + ay * (1f - half / wc));
                        z += 0.6f * _t * k * (1f - Smooth(0f, wc, ay));
                        z -= 0.15f * hw * k * Bump(ay, 1.2f * hw, 3f * hw);
                    }
                    else
                    {
                        if (ay < hw) z += _tr.Depth * (1f - (ay / hw) * (ay / hw)) * k;
                        z -= _lipH * k * Bump(ay, 0.75f * hw, 2.1f * hw);        // plowed lips on both sides
                        if (_tr.Ric) z -= 1.3f * _lipH * cut * Bump(x, 0.82f * len, 1.08f * len) * (1f - Smooth(0f, 1.6f * hw, ay));   // shoved up where it climbs out
                    }
                    p = _g * x + _gn * y;
                    break;
                }
            }

            if (w.Petal)
            {
                // petal folds about its hinge chord, curling with constant curvature to its final angle
                float a = Vector2.Dot(p, w.E);
                if (a < w.Ah)
                {
                    float phi = Phi(w, ts);
                    if (phi > 1e-3f)
                    {
                        float s = w.Ah - a, b = Vector2.Dot(p, w.N);
                        float kap = phi / w.Ah;
                        float a2 = w.Ah - Mathf.Sin(kap * s) / kap;
                        z += (1f - Mathf.Cos(kap * s)) / kap;
                        p = w.E * a2 + w.N * b;
                    }
                }
            }
            float rd = Mode == PlateMode.Trench ? re : r;    // a groove bends the plate along its length
            z += (Dish(rd, ts) + Ripple(rd, ts)) * _clamp;
            return new Vector3(p.x, p.y, z);
        }

        Vector3 Shape(int k)
        {
            var vx = _v[k];
            var w = _w[vx.W];
            bool rigid = (w.Detach || w.Plug) && Tau > w.TBreak;
            float ts = rigid ? w.TBreak : Tau;
            _clamp = _hasPlate ? Smooth(0f, _edgeBlend, vx.EdgeD) : 1f;    // welded / bolted plate can't bulge at its edge
            Vector3 q = Stretch(Attached(w, vx.P, vx.R, vx.Re, vx.Th, ts), vx.R);
            q.z += vx.Z0;
            if (w.Plug)
            {
                // sheared through with the round, then flies on, slowly tumbling
                float zp = 0.9f * _t * Mathf.Clamp01(Tau / w.TBreak);
                float f = rigid ? w.Drag * (1f - Mathf.Exp(-(Tau - w.TBreak) / w.Drag)) : 0f;
                var c = new Vector3(0f, 0f, Dish(0f, ts));
                return c + Quaternion.AngleAxis(w.Omega * f * Mathf.Rad2Deg, w.Axis) * (q - c) + Vector3.forward * (zp + w.Vel.z * f);
            }
            if (rigid)
            {
                if (!w.CSet)
                {
                    w.CSet = true;
                    Vector2 cp = w.E * (0.5f * w.Ah);
                    w.C = Attached(w, cp, cp.magnitude, cp.magnitude, Mathf.Atan2(cp.y, cp.x), w.TBreak);
                }
                float f = w.Drag * (1f - Mathf.Exp(-(Tau - w.TBreak) / w.Drag));
                return w.C + Quaternion.AngleAxis(w.Omega * f * Mathf.Rad2Deg, w.Axis) * (q - w.C) + w.Vel * f;
            }
            return q;
        }

        void Deform(bool all)
        {
            bool any = false;
            float moved = 0f;
            for (int wi = 0; wi < _w.Count; wi++)
            {
                var w = _w[wi];
                if (!all && !((w.Detach || w.Plug) && Tau > w.TBreak)) continue;
                int end = w.V0 + (w.Cols + 1) * w.Rings;
                for (int k = w.V0; k < end; k++)
                {
                    Vector3 q = Shape(k);
                    float m = (q - _pos[k]).sqrMagnitude;
                    if (m > moved) moved = m;
                    _pos[k] = q;
                }
                any = true;
            }
            _quiet = moved < StillMove * StillMove ? _quiet + 1 : 0;
            if (!any) return;
            _mesh.vertices = _pos;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            _mesh.GetNormals(_nrm);
            if (all)
            {
                if (!_borderOk) { _border.positionCount = 0; return; }   // plate ends inside the panel: no rectangle
                for (int i = 0; i < _borderIdx.Length; i++) _borderPos[i] = _pos[_borderIdx[i]] + Vector3.back * 0.002f;
                _border.positionCount = _borderPos.Length;
                _border.SetPositions(_borderPos);
            }
        }

        void StepChips()
        {
            if (_chip == null) return;
            float shrink = 1f - Smooth(2.2f, 3.0f, _age);
            for (int i = 0; i < _chip.Length; i++)
            {
                var ch = _chip[i];
                float dt = Tau - ch.T0;
                var sv = _shardV[ch.Shape];
                var sc = _shardC[ch.Shape];
                int per = sv.Length, b = i * per;
                if (dt <= 0f || shrink <= 0f)
                {
                    for (int j = 0; j < per; j++) _chipPos[b + j] = ch.P0;
                    continue;
                }
                float f = ch.Drag * (1f - Mathf.Exp(-dt / ch.Drag));
                Vector3 c = ch.P0 + ch.Vel * f;
                var q = Quaternion.LookRotation(ch.Vel) * Quaternion.AngleAxis(ch.Spin * f * Mathf.Rad2Deg, ch.Axis);
                float grow = Mathf.Clamp01(dt / (0.15f * TPen + 1e-6f)) * shrink;
                // white-hot as they tear off, cooling through orange and dull red to grey steel
                Color col = Shards.Hot(Mathf.Lerp(1f, 0.25f, Smooth(0f, 3f, dt / TauTotal)));
                for (int j = 0; j < per; j++)
                {
                    _chipPos[b + j] = c + q * Vector3.Scale(sv[j], ch.Scale) * grow;
                    float sh = sc[j].r;
                    _chipCol[b + j] = new Color(col.r * sh, col.g * sh, col.b * sh, 1f);
                }
            }
            _chipMesh.vertices = _chipPos;
            _chipMesh.colors = _chipCol;
            _chipMesh.RecalculateBounds();
        }

        // ------------------------------------------------------------------ per frame

        public void SetVisible(bool v)
        {
            if (T != null && T.gameObject.activeSelf != v) T.gameObject.SetActive(v);
        }

        /// now = session time, sim = this hit's slow-mo clock, interior = round's clock still running
        public int VertexCount => _v != null ? _v.Length : 0;
        public bool Shaped => _still;

        public void Step(float now, float sim, bool interior, float dt)
        {
            if (now < Wall) { SetVisible(false); return; }
            if (!_started)
            {
                _started = true;
                _lastSim = Hit.Sim;
                SetVisible(true);
            }
            _age = now - Wall;
            // plate follows the round's clock but never plays its process in less than _minWall s
            float dSim = Mathf.Max(0f, sim - _lastSim);
            _lastSim = Mathf.Max(_lastSim, sim);
            float dWall = dt * TauTotal / _minWall;
            Tau += interior ? Mathf.Min(dSim, dWall) : dWall;

            if (!_still)
                using (Perf.T(Tau < _settle || !_shaped ? "Plate/deform all" : "Plate/deform pieces"))
                {
                    if (Tau < _settle || !_shaped) { Deform(true); _shaped = Tau >= _settle; }
                    else Deform(false);
                    // waves died out and broken pieces came to rest
                    if (_quiet >= 8 && Tau > 1.5f * TauTotal) { _still = true; _shaped = true; }
                }
            StepChips();

            float fade = Mathf.Clamp01(_age / 0.15f);
            _border.startColor = _border.endColor = new Color(1f, 1f, 1f, 0.85f * fade);
            foreach (var lr in _plateLines) if (lr != null) lr.startColor = lr.endColor = new Color(1f, 1f, 1f, 0.85f * fade);
            if (_glow != null)
            {
                if (_glowAt < 0f && Tau >= 0.6f * TPen) _glowAt = _age;
                float g = _glowAt < 0f ? 0f : Mathf.Clamp01(1f - (_age - _glowAt) / 1.4f) * Mathf.Clamp01((_age - _glowAt) / 0.08f);
                _glow.enabled = g > 0.01f;
                if (_glow.enabled) Vis.SetColor(_glow, new Color(1f, 0.32f, 0.1f) * g);
            }
        }

        /// colours depend on the view (lighting), so they get painted once the camera moved this frame
        public void FaceCamera(Vector3 camPos, Vector3 camUp, bool viewNow = true)
        {
            if (T == null || !T.gameObject.activeSelf) return;
            _camL = T.InverseTransformPoint(camPos);
            _camUpL = T.InverseTransformDirection(camUp);
            if (_glow != null && _glow.enabled) _glow.transform.rotation = Quaternion.LookRotation(_glow.transform.position - camPos, camUp);
            int need = NeedsPaint();
            if (need == 0 || (need == 1 && !viewNow)) return;
            using (Perf.T("Plate/paint")) Paint(need == 2);
            if (need == 2) _paintTau = Tau;
            _paintCam = _camL; _paintUp = _camUpL;
        }

        /// repainted only when it would look different: plate still changing (failing, stress spreading, hot edges cooling)
        /// or the view moved (lighting follows the camera)
        /// 0: nothing to do, 1: only the lighting (view moved), 2: the colours themselves
        int NeedsPaint()
        {
            if (_paintTau < 0f) return 2;
            float dTau = Tau - _paintTau, phase = Tau / Mathf.Max(TauTotal, 1e-6f);
            // while it fails every step shows, after that colours only creep (cooling, stress tails)
            if (phase < 3f ? dTau > 0.004f * TauTotal : phase < 8f && dTau > 0.08f * TauTotal) return 2;
            if (!_still && dTau > 0.02f * TauTotal) return 2;                // shape (and its shading) still moving
            float d = _camL.magnitude;
            if ((_camL - _paintCam).sqrMagnitude > 0.013f * 0.013f * d * d) return 1;   // view turned ~0.75 deg
            return Vector3.Dot(_paintUp, _camUpL) < 0.9999f ? 1 : 0;
        }

        Color[] _base;      // colours before lighting, as of _paintTau

        static readonly Color HotWhite = new Color(1f, 0.96f, 0.86f), Burnt = new Color(0.06f, 0.02f, 0.02f), Crease = new Color(0.05f, 0.05f, 0.08f);

        void Paint(bool state)
        {
            if (_base == null || _base.Length != _v.Length) { _base = new Color[_v.Length]; state = true; }
            float ts = Tau;
            Vector3 vc = _camL.sqrMagnitude > 1e-6f ? _camL.normalized : Vector3.back;
            Vector3 light = (vc + _camUpL * 0.8f + Vector3.Cross(_camUpL, vc) * 0.35f).normalized;   // key light above-left of the camera
            float front = Mathf.Min(_cE * ts, _frontMax);
            bool holeEdge = Mode == PlateMode.Ductile || Mode == PlateMode.Jet || Mode == PlateMode.Plug;
            float edgeHot = 1f - Smooth(0.6f, 2.5f, ts / TauTotal);
            bool hasN = _nrm.Count == _v.Length;
            for (int k = 0; k < _v.Length; k++)
            {
                Color c;
                if (!state) c = _base[k];
                else
                {
                var vx = _v[k];
                var w = _w[vx.W];
                bool torn = w.Plug || (w.Petal && Vector2.Dot(vx.P, w.E) < w.Ah && Phi(w, (w.Detach && ts > w.TBreak) ? w.TBreak : ts) > 0.05f);
                bool flying = (w.Detach || w.Plug) && ts > w.TBreak;
                if (flying) c = Shards.Hot(Mathf.Lerp(0.85f, 0.3f, Smooth(0f, 4f, (ts - w.TBreak) / TauTotal)));   // torn free: hot steel piece
                else if (torn) c = Color.Lerp(new Color(0.95f, 0.12f, 0.06f), new Color(0.45f, 0.03f, 0.02f), Mathf.Clamp01(1f - vx.R / Mathf.Max(_rFeat, 1e-4f)));   // bent, torn steel: red, darker toward the tip
                else if (Mode == PlateMode.Trench)
                {
                    // stress follows the round along the groove
                    float x = Vector2.Dot(vx.P, _g), y = Vector2.Dot(vx.P, _gn);
                    float xf = Mathf.Min(_tr.Speed * ts, _tr.Len);
                    c = Vis.Heat(1f - Stress(x < -_tr.HalfW ? 99f : Capsule(x, y, xf), ts));
                    if (_tr.Slot && vx.Ring <= 1 && x < xf)
                        c = Color.Lerp(c, new Color(1f, 0.72f, 0.32f), edgeHot * (vx.Ring == 0 ? 1f : 0.5f));
                }
                else c = Vis.Heat(1f - Stress(vx.Re, ts));
                if (holeEdge && !w.Plug && vx.Ring <= 1 && ts > 0.3f * TPen)
                    c = Color.Lerp(c, new Color(1f, 0.72f, 0.32f), edgeHot * (vx.Ring == 0 ? 1f : 0.5f));
                if (vx.CrackW > 0f && ts >= vx.CrackT)
                {
                    float age = (ts - vx.CrackT) / TauTotal;
                    Color cc = vx.Hot ? Color.Lerp(HotWhite, Burnt, Smooth(0.25f, 1.6f, age)) : Crease;
                    c = Color.Lerp(c, cc, vx.CrackW * Mathf.Clamp01(0.3f + (ts - vx.CrackT) / (0.05f * TauTotal)));
                }
                // ahead of the wave front the plate is still untouched: barely visible
                float reached = 1f - Smooth(front - _ripL, front + 0.2f * _ripL, vx.R);
                c.a = flying ? 1f : vx.Off ? 0f : Mathf.Lerp(0.04f, vx.R < _R ? 0.9f : 0.8f, Mathf.Max(reached, torn ? 1f : 0f));
                if (_hasPlate && !flying) c.a *= vx.Cover;
                _base[k] = c;
                }
                // lighting: two-sided Lambert + soft highlight so dish, ripples and petals read in 3D
                Vector3 p = _pos[k];
                Vector3 view = (_camL - p).normalized;
                Vector3 nn = hasN ? _nrm[k] : Vector3.back;
                if (Vector3.Dot(nn, view) < 0f) nn = -nn;
                float lit = 0.6f + 0.42f * Mathf.Max(0f, Vector3.Dot(nn, light));
                float hs = Mathf.Max(0f, Vector3.Dot(nn, (light + view).normalized));
                hs *= hs; hs *= hs; hs *= hs; hs *= hs; hs *= hs;                 // ^32
                float spec = 0.12f * hs;
                c.r = c.r * lit + spec;
                c.g = c.g * lit + spec;
                c.b = c.b * lit + spec;
                _col[k] = c;
            }
            _mesh.colors = _col;
        }

        public void Dispose()
        {
            foreach (var m in _owned) if (m != null) Object.Destroy(m);
            _owned.Clear();
        }
    }
}
