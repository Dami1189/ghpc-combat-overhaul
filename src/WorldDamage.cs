using System;
using System.Collections.Generic;
using System.Linq;
using GHPC;
using GHPC.Weapons;
using UnityEngine;
using UnityEngine.Rendering;
using DC = GHPC.Equipment.DestructibleComponent;

namespace GHPCHitCam
{
    /// Keeps a world-space object glued to a moving part (turret, hull) without inheriting its scale.
    internal class StickTo : MonoBehaviour
    {
        public Transform Target;
        public Vector3 LocalPos;
        public Quaternion LocalRot;

        public void Bind(Transform target)
        {
            Target = target;
            LocalPos = target.InverseTransformPoint(transform.position);
            LocalRot = Quaternion.Inverse(target.rotation) * transform.rotation;
        }

        void LateUpdate()
        {
            if (Target == null) { Destroy(gameObject); return; }
            transform.SetPositionAndRotation(Target.TransformPoint(LocalPos), Target.rotation * LocalRot);
        }
    }

    internal class Expire : MonoBehaviour
    {
        public float At;
        void Update() { if (Time.time > At) Destroy(gameObject); }
    }

    /// Visible damage on the real vehicles in the game world. Every armour hit leaves the plate failure of PlateSim
    /// (petals, plug hole, ductile crater, HE tear, dent, gouge) as a small 3D panel on the hull, plus scorch, sparks
    /// and physics shrapnel. Where the mesh allows it a real hole is cut into the model too. Purely visual: the
    /// game's damage model is not touched.
    ///
    /// The game's own models can't be cut (not CPU-readable), so a panel that folds into the plate would be hidden
    /// behind the paint. Before each camera draws, a "depth punch" (the panel's footprint, scaled about the camera
    /// so it covers the same pixels further back, depth only) pushes the hull's depth back under the panel. The panel,
    /// the hole's bore and the dark cup inside are then drawn with normal depth. The punch ignores depth, so it's only
    /// used when nothing stands between the camera and the hole and the hole is big enough on screen to matter.
    /// Otherwise the flat decals (soot, ragged dark hole, glowing rim) are shown.
    internal static class WorldDamage
    {
        static Material _metal, _dark, _soot, _glow, _spark, _wPunch, _wCup, _wPlate;
        static int _debrisLayer = -1, _occMask = ~0;
        static Texture2D _dot;
        static readonly List<GameObject> _debris = new List<GameObject>();
        // per model piece: the untouched mesh and every hole so far, in that mesh's own space (so holes ride with turrets)
        static readonly Dictionary<MeshFilter, (Mesh original, List<(Vector3 p, float r)> holes)> _cuts = new Dictionary<MeshFilter, (Mesh, List<(Vector3, float)>)>();
        static readonly Dictionary<Unit, List<MeshFilter>> _unitMeshes = new Dictionary<Unit, List<MeshFilter>>();
        static readonly Dictionary<Unit, (HashSet<Collider> set, int mask)> _hulls = new Dictionary<Unit, (HashSet<Collider>, int)>();
        static int _seed = 1;
        static readonly List<Mesh> _ownedCuts = new List<Mesh>();
        static bool _logged, _lightLogged, _cullWarned, _hooked;

        const float MinPixels = 4f;      // plates smaller than this on screen (radius) use the flat decals
        const int MaxPerCamera = 24;     // 3D plates drawn per camera per frame

        static void Init()
        {
            if (_metal != null) return;
            Vis.Init();
            var std = Shader.Find("Standard");
            _metal = std != null ? new Material(std) : new Material(Vis.Solid);
            _metal.color = new Color(0.20f, 0.20f, 0.19f);
            if (std != null) { _metal.SetFloat("_Metallic", 0.75f); _metal.SetFloat("_Glossiness", 0.35f); }
            _metal.enableInstancing = true;

            var colored = Shader.Find("Hidden/Internal-Colored");
            _dark = new Material(colored);
            _dark.SetInt("_SrcBlend", (int)BlendMode.One); _dark.SetInt("_DstBlend", (int)BlendMode.Zero);
            _dark.SetInt("_ZWrite", 1); _dark.SetInt("_Cull", (int)CullMode.Off);
            _dark.renderQueue = 2010;
            _dark.SetFloat("_ZBias", -2f);

            // soot: alpha-blended, with a depth bias so it shows on paint that sits a little outside the armor collider
            _soot = new Material(colored) { renderQueue = 2990 };
            _soot.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha); _soot.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _soot.SetInt("_ZWrite", 0); _soot.SetInt("_Cull", (int)CullMode.Off);
            _soot.SetFloat("_ZBias", -1f);
            _glow = new Material(colored);
            _glow.SetInt("_SrcBlend", (int)BlendMode.One); _glow.SetInt("_DstBlend", (int)BlendMode.One);
            _glow.SetInt("_ZWrite", 0); _glow.SetInt("_Cull", (int)CullMode.Off);
            _glow.renderQueue = 3001;
            _glow.SetFloat("_ZBias", -2f);

            // the 3D plate, at the start of the transparent queue (after the opaque-only image effects such as the game's ambient
            // occlusion, which would otherwise read the punched depth and dither the area): depth punch (no colour), then the panel
            // (vertex colours, lit on the CPU; it writes depth even where it fades out, so the plate hides the cup), then the cup,
            // which only shows through the opening
            _wPunch = Mat(colored, BlendMode.Zero, BlendMode.One, true, CompareFunction.Always, 2985);
            _wPlate = Mat(colored, BlendMode.SrcAlpha, BlendMode.OneMinusSrcAlpha, true, CompareFunction.LessEqual, 2986);
            _wCup = Mat(colored, BlendMode.One, BlendMode.Zero, true, CompareFunction.LessEqual, 2987);

            _dot = new Texture2D(32, 32, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), new Vector2(15.5f, 15.5f)) / 15.5f;
                    float a = Mathf.Clamp01(1f - d);
                    _dot.SetPixel(x, y, new Color(1f, 1f, 1f, a * a));
                }
            _dot.Apply();
            var add = Shader.Find("Legacy Shaders/Particles/Additive");
            _spark = new Material(add != null ? add : colored) { mainTexture = _dot };
            PickDebrisLayer();

            // what can hide a plate from the camera: everything solid except our own debris and the x-ray layers
            _occMask = Physics.DefaultRaycastLayers & ~(1 << _debrisLayer) & ~(1 << Vis.Layer) & ~(1 << Vis.DimLayer);
            string[] seeThrough = { "Vision", "Visibility", "Thermal", "UI", "Water", "Ignore", "Trigger" };
            for (int l = 0; l < 32; l++)
            {
                string n = LayerMask.LayerToName(l);
                if (!string.IsNullOrEmpty(n) && seeThrough.Any(s => n.Contains(s))) _occMask &= ~(1 << l);
            }
            if (!_hooked) { _hooked = true; Camera.onPreCull += PreCull; }
        }

        static Material Mat(Shader s, BlendMode src, BlendMode dst, bool zwrite, CompareFunction ztest, int queue)
        {
            var m = new Material(s) { renderQueue = queue, color = Color.white };
            m.SetInt("_SrcBlend", (int)src); m.SetInt("_DstBlend", (int)dst);
            m.SetInt("_ZWrite", zwrite ? 1 : 0); m.SetInt("_ZTest", (int)ztest);
            m.SetInt("_Cull", (int)CullMode.Off);
            return m;
        }

        /// A layer the player's camera draws, that rolls on terrain, but that rounds and AI sight ignore.
        static void PickDebrisLayer()
        {
            int terrain = LayerMask.NameToLayer("Terrain");
            int roundMask = 0;
            try { roundMask = GHPC.ConstantsAndInfoManager.Instance.LiveRoundLayerMask.value; } catch { }
            int camMask = WorldCameraMask();
            string[] avoid = { "Raycast", "Vision", "Visibility", "Terrain", "Thermal", "UI", "Water", "Ignore" };
            for (int l = 0; l < 32; l++)
            {
                string n = LayerMask.LayerToName(l);
                if (string.IsNullOrEmpty(n) || avoid.Any(a => n.Contains(a))) continue;
                if ((camMask & (1 << l)) == 0 || (roundMask & (1 << l)) != 0) continue;
                if (terrain >= 0 && Physics.GetIgnoreLayerCollision(l, terrain)) continue;
                _debrisLayer = l;
                break;
            }
            if (_debrisLayer < 0) _debrisLayer = 0;
            HitCamMod.Log.Msg("World damage: debris on layer " + _debrisLayer + " (" + LayerMask.LayerToName(_debrisLayer) + ")");
        }

        /// GHPC stacks screen cameras (e.g. a world camera plus an "FP Camera" that only draws layer 6):
        /// the world camera is the one drawing the most layers.
        public static Camera MainCamera()
        {
            Camera best = null;
            int bestBits = -1;
            foreach (var c in Camera.allCameras)
            {
                if (c == null || !c.enabled || c.targetTexture != null) continue;
                int bits = Bits(c.cullingMask);
                if (bits > bestBits) { best = c; bestBits = bits; }
            }
            return best;
        }

        /// Every layer any screen camera draws.
        public static int WorldCameraMask()
        {
            int m = 0;
            foreach (var c in Camera.allCameras)
                if (c != null && c.enabled && c.targetTexture == null) m |= c.cullingMask;
            return m == 0 ? ~0 : m;
        }

        static int Bits(int v) { int n = 0; while (v != 0) { n += v & 1; v = (int)((uint)v >> 1); } return n; }

        // ------------------------------------------------------------------ entry point

        static readonly Dictionary<int, List<Vector3>> _byRound = new Dictionary<int, List<Vector3>>();
        // a HEAT shell's body touching a plate, until its jet shows up
        static readonly Dictionary<int, (Unit unit, Collider col, Vector3 point, Vector3 normal, Vector3 dir, float nominal, float los)> _preJet
            = new Dictionary<int, (Unit, Collider, Vector3, Vector3, Vector3, float, float)>();

        public static void OnArmorHit(int roundId, Unit unit, Collider col, Vector3 point, Vector3 normal, Vector3 dir, bool penetrated, bool ricochet,
                                      bool preJet, float nominalMm, float losMm, float penMm, float speed, float energy, AmmoType ammo, float holeRadius)
        {
            if (!Settings.WorldDamage.Value || unit == null || col == null || ammo == null) return;
            // a HEAT shell's body touches the plate first; the jet forms there and makes the hole. The jet's own first
            // test is often already inside the vehicle, so the plate the shell struck gets its jet hole from that.
            if (preJet)
            {
                if (_preJet.Count > 64) _preJet.Clear();
                if (!_preJet.ContainsKey(roundId)) _preJet[roundId] = (unit, col, point, normal, dir, nominalMm, losMm);
                return;
            }
            if (_preJet.TryGetValue(roundId, out var pj))
            {
                _preJet.Remove(roundId);
                if (pj.col != null && pj.unit != null && (pj.point - point).sqrMagnitude > 0.45f * 0.45f)
                    OnArmorHit(roundId, pj.unit, pj.col, pj.point, pj.normal, pj.dir, true, false, false, pj.nominal, pj.los, penMm, speed, energy, ammo, holeRadius);
            }
            try
            {
                Init();
                // a round passing several armor layers hits the same outer spot several times: one hole is enough
                if (!_byRound.TryGetValue(roundId, out var spots))
                {
                    if (_byRound.Count > 64) _byRound.Clear();
                    _byRound[roundId] = spots = new List<Vector3>();
                }
                if (spots.Any(p => (p - point).sqrMagnitude < 0.45f * 0.45f)) return;
                spots.Add(point);
                dir = dir.normalized;
                if (normal.sqrMagnitude < 0.01f) normal = -dir;
                normal.Normalize();
                if (Vector3.Dot(normal, dir) > 0f) normal = -normal;
                // the game reports some impacts (HE fuzes) a little off the plate: sit on the struck face
                if (col.Raycast(new Ray(point + normal * 0.3f, -normal), out var onFace, 0.6f) && (onFace.point - point).sqrMagnitude > 1e-4f)
                    point = onFace.point;

                // which face of the vehicle this is: the outside the round came in through, the far wall it came out of,
                // or a plate inside (a backing plate, a turret ring): nobody sees those from outside, so they get nothing.
                // A round skimming a plate under another one (a glacis under an add-on plate) is the exception: its groove
                // shows where the plate on top ends, so it gets its plate, covered wherever that other plate lies on it.
                var hull = HullColliders(unit);
                bool exit = false, under = false;
                float hitAngle = 90f - Vector3.Angle(normal, -dir);
                bool graze = ricochet || (ammo.Category == AmmoType.AmmoCategory.Penetrator && hitAngle < HoleFx.GrazeAngle);
                // every test comes in from outside toward the spot (armour colliders are often one-sided surfaces, and a ray
                // from inside a skewed path can slip between plates): any other armour piece met first covers it
                {
                    // the round came through other armour to get here, or other armour lies straight in front of the plate
                    // (the round's path can bend inside: the face's own normal catches what the path misses)
                    if (Covered(hull, col, point, dir) || Covered(hull, col, point, -normal))
                    {
                        float back = Mathf.Clamp(nominalMm * 0.003f, 0.02f, 0.6f) + 0.08f;
                        Vector3 outN = Vector3.zero;
                        bool found = col.Raycast(new Ray(point + dir * back, -dir), out var face, back);
                        if (found) outN = Vector3.Dot(face.normal, dir) > 0f ? face.normal : -face.normal;
                        // an exit face has nothing of the vehicle beyond it, along the round's path or straight out from it
                        // (only a round that went through comes out)
                        bool canExit = penetrated && found && !Covered(hull, col, face.point, -dir) && !Covered(hull, col, face.point, -outN);
                        if (canExit)
                        {
                            exit = true;
                            point = face.point;
                            normal = outN;   // outward, the way the round left
                        }
                        else if (graze && JustUnderSkin(hull, point, normal)) under = true;
                        else
                        {
                            if (AutoTest.Active) HitCamMod.Log.Msg($"World damage on {Recorder.SafeName(unit)} ({col.name}): inside the vehicle, no marks");
                            return;
                        }
                    }
                }
                bool he = ammo.Category == AmmoType.AmmoCategory.Explosive;
                bool heat = ammo.Category == AmmoType.AmmoCategory.ShapedCharge;
                float r = Mathf.Max(holeRadius, 0.012f);
                bool hole = penetrated;
                // an autocannon round with the light hit cam: its marks stay the flat ones (no cut into the model, no 3D
                // plate): each of those cost 5-30 ms, for every round of a burst
                bool light = Settings.LightAutocannonCam.Value && Mathf.Max(ammo.Caliber, ammo.Radius * 2000f) < Settings.BurstCaliberMm.Value;
                int layer = VisibleLayer(unit);
                WorldModules.Dump(unit);
                if (he || heat) WorldModules.OnBurst(unit, PhysicsToRender(col).MultiplyPoint3x4(point), ammo);
                // a plate behind the tracks / road wheels (the round came through the running gear, or the gear stands in
                // front of it) isn't seen from outside; the gear shows the hit instead
                if (!exit && WorldModules.BehindGear(unit, col, point, dir, normal, out var gearCol, out var gearAt, out var gearN))
                {
                    if (gearCol != null && spots.Count == 1)
                    {
                        var tg = PhysicsToRender(gearCol);
                        WorldModules.RunningGearHit(unit, gearCol, tg.MultiplyPoint3x4(gearAt), tg.MultiplyVector(Vector3.Dot(gearN, dir) > 0f ? -gearN : gearN), tg.MultiplyVector(dir),
                                                    he, heat, penetrated, ammo.TntEquivalentKg, Mathf.Max(ammo.Caliber, ammo.Radius * 2000f) * 0.001f, layer);
                    }
                    else if (AutoTest.Active) HitCamMod.Log.Msg($"World damage on {Recorder.SafeName(unit)} ({col.name}): behind the running gear, no marks");
                    return;
                }
                // running gear (track links, road wheels, sprockets) isn't a plate: no plate failure, damage that fits it
                if (WorldModules.IsRunningGear(unit, col))
                {
                    var tr = PhysicsToRender(col);
                    WorldModules.RunningGearHit(unit, col, tr.MultiplyPoint3x4(point), tr.MultiplyVector(normal), tr.MultiplyVector(dir), he, heat, penetrated,
                                                ammo.TntEquivalentKg, Mathf.Max(ammo.Caliber, ammo.Radius * 2000f) * 0.001f, layer);
                    return;
                }
                Vector3 sprayDir0 = ricochet ? Vector3.Reflect(dir, normal) : exit ? Vector3.Lerp(normal, dir, 0.5f).normalized : Vector3.Lerp(normal, -dir, 0.4f).normalized;

                // the same spot hit again (bursts, the game re-testing a plate): one plate is enough, unless a hole now goes through a dent
                var near = WorldPlate.Near(unit, point, Mathf.Max(0.1f, r * 0.8f));
                if (near != null)
                {
                    // a much bigger hole (an HE tear through an old rod hole) replaces the old plate
                    if ((near.Hole && r < near.HoleR * 1.5f) || !hole)
                    {
                        Sparks(point - dir * 0.03f, sprayDir0, he ? 160 : 60, he ? 26f : 18f, ricochet ? 18f : 55f, layer);
                        return;
                    }
                    UnityEngine.Object.Destroy(near.gameObject);
                }

                // the round's raycast saw the physics pose of the vehicle; its transform (what is drawn, what the decals
                // stick to) may be a step behind with interpolation. The plate is laid on the colliders in physics space, the rest uses the drawn pose.
                var toRender = PhysicsToRender(col);
                Vector3 pointR = toRender.MultiplyPoint3x4(point), dirR = toRender.MultiplyVector(dir), normalR = toRender.MultiplyVector(normal);

                // find the real outside surface of the visual model along the incoming path (for an exit: from outside, against the normal)
                long lap = Perf.Now;
                Vector3 surf = pointR;
                Vector3 sn = normalR;
                Vector3 inDir = exit ? -normalR : dirR;
                MeshFilter hitMf = null;
                var meshes = UnitMeshes(unit);
                var ray = new Ray(pointR - inDir * 0.6f, inDir);
                float bestD = 0.75f;
                foreach (var mf in meshes)
                {
                    if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                    var rr = mf.GetComponent<Renderer>();
                    if (rr == null || !rr.bounds.IntersectRay(ray)) continue;
                    var src = _cuts.TryGetValue(mf, out var cut) ? cut.original : mf.sharedMesh;
                    if (MeshCutter.Raycast(src, mf.transform.localToWorldMatrix, ray, 0.75f, out float d, out Vector3 n) && d < bestD)
                    {
                        bestD = d; surf = ray.origin + inDir * d; sn = Vector3.Dot(n, inDir) > 0 ? -n : n; hitMf = mf;
                    }
                }
                if (hitMf == null) surf = pointR - inDir * 0.03f;    // unreadable model: sit just outside the armor collider

                Perf.Lap("World/surface", ref lap);
                // real hole in the model where the game left the mesh readable
                int cutCount = 0;
                if (hole && !under && Settings.CutModels.Value && !light)
                {
                    float cutR = he ? r : r * 1.15f;
                    foreach (var mf in meshes)
                    {
                        if (mf == null || mf.sharedMesh == null) continue;
                        var rr = mf.GetComponent<Renderer>();
                        if (rr == null) continue;
                        var bb = rr.bounds; bb.Expand(cutR * 2f);
                        if (!bb.Contains(surf)) continue;
                        if (!_cuts.TryGetValue(mf, out var entry))
                        {
                            if (!mf.sharedMesh.isReadable) continue;
                            entry = (mf.sharedMesh, new List<(Vector3, float)>());
                        }
                        entry.holes.Add((mf.transform.InverseTransformPoint(surf), cutR / Mathf.Max(Mathf.Abs(mf.transform.lossyScale.x), 1e-4f)));
                        var m = MeshCutter.Cut(entry.original, Matrix4x4.identity, entry.holes, _seed++);
                        if (m == null) continue;
                        _ownedCuts.Add(m);
                        mf.sharedMesh = m;
                        _cuts[mf] = entry;
                        DarkInside(mf, m);
                        cutCount++;
                    }
                }

                Perf.Lap("World/cut", ref lap);
                var root = new GameObject("HitCam damage");
                root.transform.SetPositionAndRotation(surf + sn * 0.003f, Quaternion.LookRotation(-sn, Mathf.Abs(Vector3.Dot(sn, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up));
                // +Z of root points into the plate

                // flat decals, used when the 3D plate is too small on screen or something stands in front of it:
                // scorch / blast mark, ragged dark opening with a thin glowing edge that cools to black, or a glowing dent
                var flat = new List<Renderer>();
                float soot = exit ? r * 2f : he ? r * 4.5f : heat ? r * 6f : (hole ? r * 3.2f : r * 2.4f);
                var sootM = Matrix4x4.Scale(new Vector3(soot * 2f, soot * 2f, 1f));
                if (!under) flat.Add(Quad(root.transform, "soot", SootDisc, sootM, _soot, layer, new Color(1, 1, 1, 0.85f)));
                if (under) { }
                else if (hole)
                {
                    int hs = _seed++;
                    flat.Add(Quad(root.transform, "hole", RaggedDisc(r, he ? 0.45f : 0.2f, hs), Matrix4x4.Translate(Vector3.back * 0.001f), _dark, layer, Color.white));
                    var rim = Quad(root.transform, "rim", ThinRim(r, he ? 0.45f : 0.2f, hs), Matrix4x4.Translate(Vector3.back * 0.002f), _glow, layer, new Color(1f, 0.55f, 0.2f));
                    rim.gameObject.AddComponent<Cool>().Init(rim, new Color(1f, 0.55f, 0.2f), he ? 3f : 2.2f);
                    flat.Add(rim);
                }
                else
                {
                    var dent = Quad(root.transform, "dent", ArmorFx.HoleRim(r * 0.45f, 0.6f, _seed++), Matrix4x4.Translate(Vector3.back * 0.001f), _glow, layer, new Color(1f, 0.6f, 0.25f));
                    dent.gameObject.AddComponent<Cool>().Init(dent, new Color(1f, 0.7f, 0.35f), 3f);
                    flat.Add(dent);
                }
                // with the 3D plate the soot is a ring around it (the plate has its own scorched centre)
                // (darker than the flat soot: it sits next to the CPU-lit plate, which is shaded like the scene)
                var ring = Quad(root.transform, "soot ring", SootRing, sootM, _soot, layer, new Color(0.35f, 0.35f, 0.35f, exit ? 0.4f : hole ? 0.85f : 0.6f));
                ring.enabled = false;

                // the plate itself (the failure physics of PlateSim, settled on the hull) is built over the next frames, a few
                // milliseconds per frame, so a burst or a round through several plates doesn't stall the game. Until then the
                // flat decals show. The hit is kept relative to the struck part, so a moving vehicle or turning turret is fine.
                var plate = root.AddComponent<WorldPlate>();
                plate.Unit = unit; plate.Hole = hole; plate.HoleR = holeRadius; plate.Layer = layer;
                var ct = col.transform;
                if (!light)
                {
                    plate.Wait = new WorldPlate.Pending
                    {
                        Col = col, P = ct.InverseTransformPoint(pointR), N = ct.InverseTransformDirection(normalR), D = ct.InverseTransformDirection(dirR),
                        Exit = exit, Pen = penetrated, Ric = ricochet, Nominal = nominalMm, Los = losMm, PenMm = penMm, Speed = speed, Energy = energy,
                        Ammo = ammo, HoleR = holeRadius, Flat = flat.ToArray(), Ring = ring,
                    };
                    _pending.Enqueue(plate);
                }
                root.AddComponent<StickTo>().Bind(col.transform);

                // sparks: ricochets spray along the deflection, everything else bursts back out of the impact
                Vector3 sprayDir = ricochet ? Vector3.Reflect(dirR, sn) : exit ? Vector3.Lerp(sn, dirR, 0.5f).normalized : Vector3.Lerp(sn, -dirR, 0.4f).normalized;
                if (!under) Sparks(surf, sprayDir, he ? 220 : hole ? 120 : 70, he ? 26f : 18f, ricochet ? 18f : 55f, layer);

                // shrapnel and plate chunks with real physics
                int chunks = under || !Settings.WorldShrapnel.Value ? 0 : he ? (hole ? Mathf.Clamp(Mathf.RoundToInt(r * 90f), 10, 28) : 6) : (hole ? 4 : 2);
                float size = he ? Mathf.Clamp(r * 0.35f, 0.025f, 0.22f) : Mathf.Clamp(r * 0.8f, 0.012f, 0.05f);
                for (int i = 0; i < chunks; i++)
                {
                    Vector3 v = (Vector3.Lerp(sn, exit ? dirR : -dirR, 0.3f) + UnityEngine.Random.insideUnitSphere * 0.9f).normalized * UnityEngine.Random.Range(4f, he ? 22f : 12f);
                    SpawnChunk(surf + sn * 0.05f, v, size * UnityEngine.Random.Range(0.5f, 1.4f));
                }

                if (!_logged || AutoTest.Active)
                {
                    _logged = true;
                    HitCamMod.Log.Msg($"World damage on {Recorder.SafeName(unit)} ({col.name}): {(exit ? "exit " : under ? "under another plate, " : "")}{(hole ? "hole" : ricochet ? "ricochet" : "dent")} at {hitAngle:0.#} deg r={r * 1000f:0} mm, LOS {losMm:0} / {nominalMm:0} mm; model surface {(hitMf != null ? "found (" + hitMf.name + ")" : "not readable")}, cut {cutCount} mesh(es), {chunks} chunks");
                }
            }
            catch (Exception e) { HitCamMod.Log.Warning("World damage failed: " + e.Message); }
        }

        static readonly Queue<WorldPlate> _pending = new Queue<WorldPlate>();
        // plates laid on their armour piece, waiting for the picture of the drawn surface to come back from the GPU
        class Probing { public WorldPlate Plate; public WorldPlate.Pending W; public HoleFx Fx; public ProbeJob Job; public int Frame; public float Ms; }
        static readonly List<Probing> _probing = new List<Probing>();
        static float _msGeom;
        static int _trisGeom;
        static bool _buildLogged;

        /// Builds the queued plates, ~5 ms per frame (at least one): first the half that needs the physics (the plate's
        /// face, mesh, collider rays) and a surface picture whose read-back runs on the GPU's own time; a frame or two later
        /// the rest, once the picture is back. Nothing waits for the GPU.
        public static void Update()
        {
            if (_pending.Count == 0 && _probing.Count == 0) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n = 0;
            for (int i = 0; i < _probing.Count && (n == 0 || sw.Elapsed.TotalMilliseconds < 5.0); )
            {
                var pr = _probing[i];
                bool alive = pr.Plate != null && pr.Fx != null && pr.Fx.T != null;
                if (alive && pr.Job != null && !pr.Job.Done && Time.frameCount - pr.Frame < 10) { i++; continue; }
                _probing.RemoveAt(i);
                float[] vis = pr.Job != null ? SurfaceDepthEnd(pr.Job) : null;     // also frees its textures
                if (!alive) { pr.Fx?.Abandon(); continue; }
                n++;
                float t0 = (float)sw.Elapsed.TotalMilliseconds;
                try
                {
                    if (!pr.Fx.FinishWorld(vis)) { pr.Fx.Abandon(); continue; }
                    InitPlate(pr.Plate, pr.Fx, pr.W);
                    pr.Ms += (float)sw.Elapsed.TotalMilliseconds - t0;
                    LogPlate(pr.Plate, pr.W, pr.Ms, pr.Job != null);
                }
                catch (Exception e) { HitCamMod.Log.Warning("World plate failed: " + e); pr.Fx.Abandon(); }
            }
            while (_pending.Count > 0 && (n == 0 || sw.Elapsed.TotalMilliseconds < 5.0))
            {
                var plate = _pending.Dequeue();
                if (plate == null || plate.Wait == null) continue;
                var w = plate.Wait;
                plate.Wait = null;
                if (w.Col == null || plate.Unit == null) continue;
                n++;
                float t0 = (float)sw.Elapsed.TotalMilliseconds;
                try
                {
                    // the struck part's drawn pose now, back into the physics pose the colliders are queried in
                    var ct = w.Col.transform;
                    var toRender = PhysicsToRender(w.Col);
                    var toPhys = toRender.inverse;
                    Vector3 point = toPhys.MultiplyPoint3x4(ct.TransformPoint(w.P));
                    Vector3 normal = toPhys.MultiplyVector(ct.TransformDirection(w.N)).normalized;
                    Vector3 dir = toPhys.MultiplyVector(ct.TransformDirection(w.D)).normalized;
                    var fx = BeginPlate(plate, w, point, normal, dir, toRender);
                    if (fx == null) continue;
                    // the surface picture, where the plate now sits on the drawn vehicle
                    fx.ProbeFrame(out var pp, out var prot, out float half, out float back);
                    var job = SurfaceDepthBegin(pp, prot, half, HoleFx.ProbeRes, back, 1 << plate.Layer, plate.Unit);
                    var entry = new Probing { Plate = plate, W = w, Fx = fx, Job = job, Frame = Time.frameCount, Ms = (float)sw.Elapsed.TotalMilliseconds - t0 };
                    if (job == null || job.Sync != null)
                    {
                        // no read-back to wait for (no picture, or the GPU can't read back asynchronously)
                        float[] vis = job?.Sync;
                        if (!fx.FinishWorld(vis)) { fx.Abandon(); continue; }
                        InitPlate(plate, fx, w);
                        entry.Ms = (float)sw.Elapsed.TotalMilliseconds - t0;
                        LogPlate(plate, w, entry.Ms, false);
                    }
                    else _probing.Add(entry);
                }
                catch (Exception e) { HitCamMod.Log.Warning("World plate failed: " + e); }
            }
        }

        static void LogPlate(WorldPlate plate, WorldPlate.Pending w, float ms, bool async)
        {
            if (plate.Fx == null || (_buildLogged && !AutoTest.Active)) return;
            _buildLogged = true;
            var fx = plate.Fx;
            string groove = fx.Mode == PlateMode.Trench ? $" {(fx.GrooveCut ? "slot" : "groove")} {fx.GrooveLength * 1000f:0} mm," : "";
            HitCamMod.Log.Msg($"World plate on {Recorder.SafeName(plate.Unit)} ({(w.Col != null ? w.Col.name : "?")}): {fx.Mode}{(fx.Exit ? " (exit)" : "")},{groove} {(fx.OnRealPlate ? "on the real plate" : "on the hull colliders")}, angle {HoleFx.ImpactAngle(fx.Hit):0.#} deg, panel {fx.Radius * 2000f:0} mm, opening {fx.Opening * 1000f:0} mm, {fx.OnHull} hull samples {fx.ConformNote}, depth {fx.Depth * 100f:0} cm, built in {ms:0.0} ms of main thread{(async ? " (surface picture read back asynchronously)" : "")} (plate geometry {_msGeom:0.0} ms, {_trisGeom} tris; {_pending.Count} queued, {_probing.Count} waiting for pictures)");
        }

        /// The first half of a plate: on the struck armour piece, in the physics pose, then moved to the drawn pose under its root.
        static HoleFx BeginPlate(WorldPlate plate, WorldPlate.Pending w, Vector3 point, Vector3 normal, Vector3 dir, Matrix4x4 toRender)
        {
            var unit = plate.Unit;
            var col = w.Col;
            var ammo = w.Ammo;
            var ah = new ArmorHit
            {
                Name = col.name,
                L = point, NormalL = normal, DirL = dir,      // world space for the world plate
                Los = w.Los, Nominal = w.Nominal, Pen = w.PenMm,
                Angle = 90f - Vector3.Angle(normal, -dir),
                Energy = w.Energy, Speed = w.Speed,
                Caliber = Mathf.Max(ammo.Caliber, ammo.Radius * 2000f) * 0.001f,
                RodRadius = Mathf.Sqrt(Mathf.Max(ammo.SectionalArea, 1e-6f) / Mathf.PI),
                Tnt = ammo.TntEquivalentKg,
                Penetrated = w.Pen, Ricochet = w.Ric,
                HoleRadius = w.HoleR,
                Category = ammo.Category,
            };
            var hull = HullColliders(unit);
            HoleFx.HullRay all = (Vector3 o, Vector3 d, float len, out float dist) => HullRay(hull.set, hull.mask, o, d, len, out dist);
            HoleFx.HullRay one = (Vector3 o, Vector3 d, float len, out float dist) =>
            {
                dist = 0f;
                if (!col.Raycast(new Ray(o, d), out var h, len)) return false;
                dist = h.distance;
                return true;
            };
            // the struck armour piece itself (the game's own collider shape): the damage lies on its face and stops at its edges.
            // The collider's transform is the drawn pose; the plate is built in the physics pose.
            var gsw = System.Diagnostics.Stopwatch.StartNew();
            var tris = Recorder.PlateGeometry(col, toRender.MultiplyPoint3x4(point), null);
            if (tris != null)
            {
                var toPhys = toRender.inverse;
                for (int i = 0; i < tris.Length; i++) tris[i] = toPhys.MultiplyPoint3x4(tris[i]);
            }
            // the vehicle's middle (which way is out for faces edge-on to the round)
            var hb = new Bounds(point, Vector3.zero);
            bool hbSet = false;
            foreach (var hc in hull.set) if (hc != null) { if (!hbSet) { hb = hc.bounds; hbSet = true; } else hb.Encapsulate(hc.bounds); }
            _msGeom = (float)gsw.Elapsed.TotalMilliseconds;
            _trisGeom = tris != null ? tris.Length / 3 : 0;
            var fx = HoleFx.BeginWorld(ah, _seed++, w.Exit, all, one, tris, hb.center);
            if (fx == null) return null;
            fx.T.SetPositionAndRotation(toRender.MultiplyPoint3x4(fx.T.position), toRender.rotation * fx.T.rotation);
            fx.T.SetParent(plate.transform, true);     // rides with the vehicle while the picture comes back
            return fx;
        }

        static void InitPlate(WorldPlate plate, HoleFx fx, WorldPlate.Pending w)
        {
            var ring = w.Ring;
            // a groove runs along the plate: the round blast mark around the impact doesn't belong to it
            if (fx.Mode == PlateMode.Trench && ring != null) { UnityEngine.Object.Destroy(ring.gameObject); ring = null; }
            plate.Init(fx, w.Pen && fx.Opening > 0f, w.Flat, ring, _wPlate, _wCup, _wPunch);
            LogLight();

            // long battles: the oldest plates fall back to their flat decals
            int max = Mathf.Max(0, Settings.MaxWorldPlates.Value), n3 = 0;
            var all3 = WorldPlate.All;
            for (int i = 0; i < all3.Count; i++) if (all3[i] != null && all3[i].Has3D) n3++;
            for (int i = 0; i < all3.Count && n3 > max; i++)
                if (all3[i] != null && all3[i].Has3D) { all3[i].Drop3D(); n3--; }
        }

        // ------------------------------------------------------------------ depth picture of the drawn hull

        static Camera _probeCam;
        static RenderTexture _probeRT;
        static Texture2D _probeTex;
        static Shader _depthNormals;
        static Material _depthMat;
        static CommandBuffer _probeCb, _netCb;
        static bool _probeBroken, _probeLogged;
        // the render types the depth-normals shader has a subshader for; materials with any other (or none) are skipped by it
        static readonly HashSet<string> _depthTypes = new HashSet<string>();
        static readonly Dictionary<Material, bool> _redraw = new Dictionary<Material, bool>(), _isNet = new Dictionary<Material, bool>();
        static readonly Dictionary<Unit, List<Renderer>> _unitRenderers = new Dictionary<Unit, List<Renderer>>();
        static readonly HashSet<string> _tagsLogged = new HashSet<string>();
        static readonly List<Material> _mats = new List<Material>();

        /// The game's meshes can't be read on the CPU, but they can be drawn: a tiny orthographic camera looks at the
        /// spot along the plate normal and draws the vehicle's layer with Unity's own depth+normals shader. Returns plate z
        /// (+ into the plate) per sample, NaN where nothing is drawn.
        /// The shader only draws materials whose RenderType it knows, and alpha-tests the TransparentCutout ones on the
        /// main texture. GHPC's vehicle paint (GHPC/VehicleShader) and camo nets are tagged TransparentCutout, but their
        /// texture alpha isn't opacity, so most of the hull was clipped away. A second pass draws the unit's own renderers
        /// with those materials (and any of unknown type) as plain opaque into the same depth buffer: the nearest surface
        /// of either pass wins. Camo nets are drawn alone first (solid): where one is the nearest surface the sample is
        /// -Infinity, cover, so the plate is never drawn over a net.
        /// A surface picture in flight: its passes rendered, their read-backs requested.
        internal class ProbeJob
        {
            public RenderTexture NetRT, MainRT;
            public UnityEngine.Rendering.AsyncGPUReadbackRequest NetReq, MainReq;
            public bool HasNet;
            public float Far, Back;
            public int Res;
            public float[] Sync;      // done on the spot (no asynchronous read-back on this GPU)
            public bool Done => (!HasNet || NetReq.done) && MainReq.done;
        }

        static ProbeJob SurfaceDepthBegin(Vector3 pos, Quaternion rot, float half, int res, float back, int mask, Unit unit)
        {
            if (_probeBroken) return null;
            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                var z = SurfaceDepth(pos, rot, half, res, back, mask, unit);
                return z == null ? null : new ProbeJob { Sync = z };
            }
            if (!ProbeSetup(res)) return null;
            float far = back * 2f;
            _probeCam.transform.SetPositionAndRotation(pos - rot * Vector3.forward * back, rot);
            _probeCam.orthographicSize = half;
            _probeCam.nearClipPlane = 0.005f;
            _probeCam.farClipPlane = far;
            QueueHull(unit, pos, rot, half, back, out int draws, out int netDraws);
            var job = new ProbeJob { Far = far, Back = back, Res = res };
            try
            {
                if (netDraws > 0)
                {
                    // camo nets alone first (see SurfaceDepth)
                    job.NetRT = RenderTexture.GetTemporary(res, res, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    _probeCam.targetTexture = job.NetRT;
                    DrawProbe(_netCb, true);
                    job.NetReq = UnityEngine.Rendering.AsyncGPUReadback.Request(job.NetRT, 0, TextureFormat.RGBA32);
                    job.HasNet = true;
                }
                job.MainRT = RenderTexture.GetTemporary(res, res, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                _probeCam.targetTexture = job.MainRT;
                _probeCam.cullingMask = mask;
                _probeCam.clearFlags = CameraClearFlags.SolidColor;
                _probeCam.RenderWithShader(_depthNormals, "RenderType");
                if (draws > 0) DrawProbe(_probeCb, false);
                job.MainReq = UnityEngine.Rendering.AsyncGPUReadback.Request(job.MainRT, 0, TextureFormat.RGBA32);
            }
            finally { _probeCam.targetTexture = null; }
            return job;
        }

        /// The picture's plate depths (NaN: nothing drawn, -Infinity: a camo net), or null; frees the job's textures.
        static float[] SurfaceDepthEnd(ProbeJob job)
        {
            if (job.Sync != null) return job.Sync;
            try
            {
                if (!job.Done || job.MainReq.hasError || (job.HasNet && job.NetReq.hasError)) return null;
                int res = job.Res;
                var z = new float[res * res];
                int drawn = Decode(job.MainReq.GetData<Color32>().ToArray(), z, job.Far, job.Back), onNet = 0;
                if (job.HasNet)
                {
                    var net = new float[res * res];
                    Decode(job.NetReq.GetData<Color32>().ToArray(), net, job.Far, job.Back);
                    for (int i = 0; i < z.Length; i++)
                        if (!float.IsNaN(net[i]) && (float.IsNaN(z[i]) || net[i] <= z[i] + 0.03f)) { z[i] = float.NegativeInfinity; onNet++; }
                }
                if (!_probeLogged || AutoTest.Active)
                {
                    _probeLogged = true;
                    HitCamMod.Log.Msg($"World plates: surface picture {res}x{res} read back, {drawn} samples drawn, {onNet} on a camo net");
                }
                return drawn == 0 ? null : z;
            }
            catch (Exception e) { HitCamMod.Log.Warning("World plates: surface picture read-back failed: " + e.Message); return null; }
            finally
            {
                if (job.NetRT != null) RenderTexture.ReleaseTemporary(job.NetRT);
                if (job.MainRT != null) RenderTexture.ReleaseTemporary(job.MainRT);
                job.NetRT = job.MainRT = null;
            }
        }

        /// The depth-normals shader, the probe camera and its texture (once).
        static bool ProbeSetup(int res)
        {
            if (_depthNormals == null)
            {
                _depthNormals = Shader.Find("Hidden/Internal-DepthNormalsTexture");
                if (_depthNormals == null) { _probeBroken = true; HitCamMod.Log.Msg("World plates: no depth-normals shader, plates follow the armour colliders"); return false; }
                _depthMat = new Material(_depthNormals) { hideFlags = HideFlags.HideAndDontSave };
                _probeCb = new CommandBuffer { name = "HitCam surface probe" };
                _netCb = new CommandBuffer { name = "HitCam surface probe nets" };
                var rt = new ShaderTagId("RenderType");
                for (int i = 0; i < _depthNormals.subshaderCount; i++)
                {
                    string t = _depthNormals.FindSubshaderTagValue(i, rt).name;
                    if (!string.IsNullOrEmpty(t)) _depthTypes.Add(t);
                }
                HitCamMod.Log.Msg("World plates: depth shader render types " + string.Join(", ", _depthTypes));
            }
            if (_probeCam == null)
            {
                var go = new GameObject("HitCam surface probe") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(go);
                _probeCam = go.AddComponent<Camera>();
                _probeCam.enabled = false;
                _probeCam.orthographic = true;
                _probeCam.renderingPath = RenderingPath.Forward;
                _probeCam.allowHDR = false;
                _probeCam.allowMSAA = false;
                _probeCam.useOcclusionCulling = false;
                _probeCam.clearFlags = CameraClearFlags.SolidColor;
                _probeCam.backgroundColor = new Color(0.5f, 0.5f, 1f, 1f);    // decodes past the far plane: nothing there
                _probeCam.aspect = 1f;
            }
            if (_probeRT == null || _probeRT.width != res)
            {
                if (_probeRT != null) _probeRT.Release();
                _probeRT = new RenderTexture(res, res, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { antiAliasing = 1, filterMode = FilterMode.Point };
                _probeTex = new Texture2D(res, res, TextureFormat.RGBA32, false, true);
            }
            return true;
        }

        static float[] SurfaceDepth(Vector3 pos, Quaternion rot, float half, int res, float back, int mask, Unit unit)
        {
            if (_probeBroken || !ProbeSetup(res)) return null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool log = !_probeLogged || AutoTest.Active;
            float far = back * 2f;
            _probeCam.transform.SetPositionAndRotation(pos - rot * Vector3.forward * back, rot);
            _probeCam.orthographicSize = half;
            _probeCam.nearClipPlane = 0.005f;
            _probeCam.farClipPlane = far;
            _probeCam.targetTexture = _probeRT;
            QueueHull(unit, pos, rot, half, back, out int draws, out int netDraws);

            // camo nets alone first: the plate replaces whatever is drawn under it, and a net lies only a few cm off the paint
            float[] net = null;
            if (netDraws > 0)
            {
                DrawProbe(_netCb, true);
                net = new float[res * res];
                Decode(ReadProbe(res), net, far, back);
            }

            _probeCam.cullingMask = mask;
            _probeCam.clearFlags = CameraClearFlags.SolidColor;
            _probeCam.RenderWithShader(_depthNormals, "RenderType");
            int byType = log ? Decode(ReadProbe(res), null, far, back) : -1;

            // second pass, on top of the first (no clear, normal depth test): the unit's own cut-out and unknown-type
            // renderers again, without the alpha test
            if (draws > 0) DrawProbe(_probeCb, false);
            var z = new float[res * res];
            int drawn = Decode(ReadProbe(res), z, far, back), onNet = 0;
            // the net is cover where it is the nearest surface (or the paint shows through its slits right behind it)
            if (net != null)
                for (int i = 0; i < z.Length; i++)
                    if (!float.IsNaN(net[i]) && (float.IsNaN(z[i]) || net[i] <= z[i] + 0.03f)) { z[i] = float.NegativeInfinity; onNet++; }
            if (log)
            {
                _probeLogged = true;
                HitCamMod.Log.Msg($"World plates: surface probe {res}x{res} over {half * 200f:0} cm, {drawn} samples drawn ({byType} by render type, {draws} opaque redraws), {onNet} on a camo net, {sw.Elapsed.TotalMilliseconds:0.0} ms");
            }
            if (drawn == 0) return null;      // the layer drew nothing at all (shader not matched): use the colliders
            return z;
        }

        /// The probe camera drawing only a command buffer (nothing culled from the scene).
        static void DrawProbe(CommandBuffer cb, bool clear)
        {
            _probeCam.clearFlags = clear ? CameraClearFlags.SolidColor : CameraClearFlags.Nothing;
            _probeCam.cullingMask = 0;
            _probeCam.AddCommandBuffer(CameraEvent.AfterForwardOpaque, cb);
            try { _probeCam.Render(); }
            finally
            {
                _probeCam.RemoveCommandBuffer(CameraEvent.AfterForwardOpaque, cb);
                _probeCam.clearFlags = CameraClearFlags.SolidColor;
            }
        }

        static Color32[] ReadProbe(int res)
        {
            var prev = RenderTexture.active;
            RenderTexture.active = _probeRT;
            _probeTex.ReadPixels(new Rect(0, 0, res, res), 0, 0, false);
            RenderTexture.active = prev;
            return _probeTex.GetPixels32();
        }

        /// Plate z per sample (NaN where nothing is drawn) into z if given; returns the number of samples drawn.
        static int Decode(Color32[] px, float[] z, float far, float back)
        {
            int drawn = 0;
            for (int i = 0; i < px.Length; i++)
            {
                // EncodeDepthNormal: linear depth / far in z + w / 255
                float d01 = px[i].b / 255f + px[i].a / (255f * 255f);
                if (d01 > 0.995f) { if (z != null) z[i] = float.NaN; continue; }
                if (z != null) z[i] = d01 * far - back;
                drawn++;
            }
            return drawn;
        }

        /// Fills the probe's command buffers with the unit's renderers inside the probe box: the camo nets, and those
        /// whose materials the first pass clipped or skipped (see Redraw).
        static void QueueHull(Unit unit, Vector3 pos, Quaternion rot, float half, float back, out int draws, out int nets)
        {
            _probeCb.Clear();
            _netCb.Clear();
            draws = nets = 0;
            if (unit == null) return;
            var list = UnitRenderers(unit);
            Vector3 ax = rot * Vector3.right, ay = rot * Vector3.up, az = rot * Vector3.forward;
            Vector3 ext = new Vector3(
                (Mathf.Abs(ax.x) + Mathf.Abs(ay.x)) * half + Mathf.Abs(az.x) * back,
                (Mathf.Abs(ax.y) + Mathf.Abs(ay.y)) * half + Mathf.Abs(az.y) * back,
                (Mathf.Abs(ax.z) + Mathf.Abs(ay.z)) * half + Mathf.Abs(az.z) * back);
            var box = new Bounds(pos, ext * 2f);
            var cam = MainCamera();
            int camMask = cam != null ? cam.cullingMask : ~0;
            foreach (var r in list)
            {
                if (r == null || !r.enabled || r.forceRenderingOff || !r.gameObject.activeInHierarchy) continue;
                if (r.shadowCastingMode == ShadowCastingMode.ShadowsOnly || (camMask & (1 << r.gameObject.layer)) == 0) continue;
                if (!r.bounds.Intersects(box)) continue;
                var mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.TryGetComponent<MeshFilter>(out var mf) ? mf.sharedMesh : null;
                if (mesh == null) continue;
                r.GetSharedMaterials(_mats);
                int subs = Mathf.Min(_mats.Count, mesh.subMeshCount);
                // transparent ones (sight glass, hatch voids) are not the hull
                for (int s = 0; s < subs; s++)
                {
                    var m = _mats[s];
                    if (m == null) continue;
                    if (IsNet(m)) { _netCb.DrawRenderer(r, _depthMat, s, 0); nets++; }
                    else if (Redraw(m)) { _probeCb.DrawRenderer(r, _depthMat, s, 0); draws++; }
                }
            }
        }

        /// A camouflage net (GHPC's CamoNet shader, or a material named like one).
        static bool IsNet(Material m)
        {
            if (_isNet.TryGetValue(m, out bool b)) return b;
            string sh = m.shader != null ? m.shader.name.ToLowerInvariant() : "", mn = m.name.ToLowerInvariant();
            b = sh.Contains("camonet") || sh.Contains("camo net") || mn.Contains("camo net") || mn.Contains("camonet") || mn.Contains("lcss");
            _isNet[m] = b;
            return b;
        }

        /// Drawn again as plain opaque in the second pass: opaque-queue materials the first pass alpha-tests
        /// (TransparentCutout: the shader clips on the texture alpha, which in GHPC's vehicle shader isn't opacity)
        /// or doesn't draw at all (render types it has no subshader for).
        static bool Redraw(Material m)
        {
            if (_redraw.TryGetValue(m, out bool b)) return b;
            string t = m.GetTag("RenderType", false, "");
            b = m.renderQueue < 2500 && (t == "TransparentCutout" || !_depthTypes.Contains(t));
            _redraw[m] = b;
            return b;
        }

        /// The unit's mesh renderers (also inactive ones; LOD0 only), and once per vehicle type a log of its shaders
        /// and which of them the probe draws again as opaque.
        static List<Renderer> UnitRenderers(Unit u)
        {
            if (_unitRenderers.TryGetValue(u, out var list) && list.All(r => r != null)) return list;
            var roots = UnitRoots(u);
            var lodSkip = new HashSet<Renderer>();
            foreach (var root in roots)
                foreach (var lg in root.GetComponentsInChildren<LODGroup>(true))
                {
                    var lods = lg.GetLODs();
                    for (int i = 1; i < lods.Length; i++) foreach (var r in lods[i].renderers) if (r != null) lodSkip.Add(r);
                }
            list = new List<Renderer>();
            foreach (var root in roots)
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                    if (lodSkip.Contains(r) || r.bounds.size.magnitude > 16f) continue;
                    if (r.GetComponentInParent<AarVisual>() != null || r.GetComponentInParent<AarModel>() != null) continue;
                    list.Add(r);
                }
            _unitRenderers[u] = list;

            string name = Recorder.SafeName(u);
            if (_tagsLogged.Add(name))
            {
                var skipped = new Dictionary<string, (int n, string e)>();
                var typed = new Dictionary<string, (int n, string e)>();
                var layers = new Dictionary<string, int>();
                foreach (var r in list)
                {
                    if (r.enabled && r.gameObject.activeInHierarchy)
                    {
                        string ln = r.gameObject.layer + " " + LayerMask.LayerToName(r.gameObject.layer);
                        layers[ln] = layers.TryGetValue(ln, out int c) ? c + 1 : 1;
                    }
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        string tag = m.GetTag("RenderType", false, "");
                        string cull = m.HasProperty("_Cull") ? ", cull " + (CullMode)(int)m.GetFloat("_Cull") : "";
                        string key = $"{(m.shader != null ? m.shader.name : "?")} [RenderType {(tag.Length > 0 ? tag : "none")}, queue {m.renderQueue}{cull}]";
                        var d = IsNet(m) || Redraw(m) ? skipped : typed;
                        d[key] = d.TryGetValue(key, out var v) ? (v.n + 1, v.e) : (1, r.name + "/" + m.name);
                    }
                }
                string Top(Dictionary<string, (int n, string e)> d) => d.Count == 0 ? "none"
                    : string.Join("; ", d.OrderByDescending(kv => kv.Value.n).Take(5).Select(kv => $"{kv.Key} x{kv.Value.n} (e.g. {kv.Value.e})"));
                HitCamMod.Log.Msg($"World plates: {name} has {list.Count} renderers on layers {string.Join(", ", layers.Select(kv => kv.Key + " x" + kv.Value))}; " +
                                  $"redrawn opaque (nets first): {Top(skipped)}; render type pass only: {Top(typed)}");
            }
            return list;
        }

        static readonly RaycastHit[] _hits = new RaycastHit[24];

        /// The nearest of a vehicle's own armour colliders along a ray, with the hit (point, normal, collider): what the world
        /// plates lay themselves on. Queries the physics scene with the armour layers and keeps only that vehicle's armour.
        internal static bool ArmourHit(Unit u, Ray ray, float len, out RaycastHit hit)
        {
            hit = default;
            if (u == null) return false;
            var h = HullColliders(u);
            int n = Physics.RaycastNonAlloc(ray.origin, ray.direction, _hits, len, h.mask, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            bool any = false;
            for (int i = 0; i < n; i++)
                if (_hits[i].distance < best && h.set.Contains(_hits[i].collider)) { best = _hits[i].distance; hit = _hits[i]; any = true; }
            return any;
        }

        /// Nearest of the vehicle's own armour colliders along a ray.
        static bool HullRay(HashSet<Collider> set, int mask, Vector3 o, Vector3 d, float len, out float dist)
        {
            dist = float.MaxValue;
            int n = Physics.RaycastNonAlloc(o, d, _hits, len, mask, QueryTriggerInteraction.Ignore);
            bool any = false;
            for (int i = 0; i < n; i++)
                if (_hits[i].distance < dist && set.Contains(_hits[i].collider)) { dist = _hits[i].distance; any = true; }
            return any;
        }

        /// A plate right under the vehicle's outer skin: along its normal, whatever covers it (an add-on plate, ERA, a skirt)
        /// lies within a few cm, and past that is open air. A firewall or turret floor deep inside fails.
        static bool JustUnderSkin((HashSet<Collider> set, int mask) hull, Vector3 point, Vector3 normal)
        {
            // coming in along the normal from outside: every armour piece met lies within a few cm of the plate
            const float from = 3f;
            int n = Physics.RaycastNonAlloc(point + normal * from, -normal, _hits, from - 0.005f, hull.mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (hull.set.Contains(_hits[i].collider) && from - _hits[i].distance > 0.2f) return false;
            return true;
        }

        /// Is the spot behind another of the vehicle's armour pieces, seen from outside along `travel` (the way in)?
        static bool Covered((HashSet<Collider> set, int mask) hull, Collider struck, Vector3 point, Vector3 travel)
        {
            const float from = 6f;
            int n = Physics.RaycastNonAlloc(point - travel * from, travel, _hits, from - 0.03f, hull.mask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
                if (_hits[i].collider != struck && hull.set.Contains(_hits[i].collider)) return true;
            return false;
        }

        /// The vehicle's armour colliders (no modules, crew or triggers): the surface the plates are laid on.
        internal static (HashSet<Collider> set, int mask) HullColliders(Unit u)
        {
            if (_hulls.TryGetValue(u, out var h) && h.set.Count > 0 && h.set.All(c => c != null)) return h;
            var set = new HashSet<Collider>();
            int mask = 0;
            foreach (var root in UnitRoots(u))
                foreach (var c in root.GetComponentsInChildren<Collider>(false))
                {
                    if (c == null || !c.enabled || c.isTrigger) continue;
                    if (!c.TryGetComponent<IArmor>(out _) || c.GetComponent<DC>() != null) continue;
                    set.Add(c);
                    mask |= 1 << c.gameObject.layer;
                }
            h = (set, mask);
            _hulls[u] = h;
            // a vehicle's own armour hides plates behind it too
            if ((_occMask & mask) != mask)
            {
                HitCamMod.Log.Msg("World plates: armour layers " + string.Join(", ", Enumerable.Range(0, 32).Where(l => (mask & (1 << l)) != 0).Select(l => l + " " + LayerMask.LayerToName(l))) + " added to the occlusion test");
                _occMask |= mask;
            }
            return h;
        }

        /// World matrix from the vehicle's physics pose (what raycasts see) to its drawn pose.
        internal static Matrix4x4 PhysicsToRender(Collider col)
        {
            var rb = col.attachedRigidbody;
            if (rb == null) return Matrix4x4.identity;
            Transform t = rb.transform;
            return Matrix4x4.TRS(t.position, t.rotation, Vector3.one) * Matrix4x4.TRS(rb.position, rb.rotation, Vector3.one).inverse;
        }

        // ------------------------------------------------------------------ per camera: 3D plate or flat decals

        /// Number of plates drawn in 3D / as flat decals by the last camera that drew any (for the self-test log).
        public static int Last3D, LastFlat;

        static void PreCull(Camera cam) { using (Perf.T("WorldDamage.PreCull")) TimedPreCull(cam); }
        static void TimedPreCull(Camera cam)
        {
            var all = WorldPlate.All;
            if (all.Count == 0 || cam == null || cam == _probeCam) return;
            try
            {
                var ct = cam.transform;
                Vector3 c = ct.position, f = ct.forward;
                float tanHalf = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad), halfPx = cam.pixelHeight * 0.5f;
                int budget = MaxPerCamera, n3 = 0, nf = 0;
                for (int i = all.Count - 1; i >= 0; i--)
                {
                    var p = all[i];
                    if (p == null) { all.RemoveAt(i); continue; }
                    if ((cam.cullingMask & (1 << p.Layer)) == 0) continue;
                    bool three = p.Has3D && budget > 0 && Want3D(cam, p, c, f, tanHalf, halfPx);
                    if (three) { budget--; p.PlacePunch(cam); n3++; } else nf++;
                    p.Show(three);
                }
                if (n3 + nf > 0) { Last3D = n3; LastFlat = nf; }
            }
            catch (Exception e)
            {
                if (!_cullWarned) { _cullWarned = true; HitCamMod.Log.Warning("World plates: " + e); }
            }
        }

        static bool Want3D(Camera cam, WorldPlate p, Vector3 c, Vector3 f, float tanHalf, float halfPx)
        {
            Transform t = p.Fx.T;
            Vector3 pos = p.Fx.VisiblePoint, to = pos - c, outN = -t.forward;
            float d = to.magnitude;
            if (d < 0.02f || Vector3.Dot(to, f) <= 0f || Vector3.Dot(to, outN) >= 0f) return false;   // behind the camera, or seen from inside the plate
            float px = p.Fx.Radius * halfPx / (cam.orthographic ? Mathf.Max(cam.orthographicSize, 1e-3f) : d * tanHalf);
            if (px < MinPixels) return false;
            // anything between: a wheel, another vehicle, a building, the terrain
            Vector3 dv = pos + outN * 0.04f - c;
            float dl = dv.magnitude;
            return dl < 0.06f || !Physics.Raycast(c, dv / dl, dl - 0.03f, _occMask, QueryTriggerInteraction.Ignore);
        }

        /// The newest plate still drawn in 3D (self-test close-ups).
        public static WorldPlate LastPlate()
        {
            var all = WorldPlate.All;
            for (int i = all.Count - 1; i >= 0; i--) if (all[i] != null && all[i].Has3D) return all[i];
            return null;
        }

        // ------------------------------------------------------------------ scene lighting for the CPU-lit plates

        static SceneLight _sl;
        static int _slFrame = -1;
        static Light _sun;
        static float _sunSearch;
        static readonly Vector3[] _ambDirs = { Vector3.up, Vector3.right, Vector3.left, Vector3.forward, Vector3.back, Vector3.down };
        static readonly Color[] _ambOut = new Color[6];

        public static SceneLight Lighting()
        {
            if (_slFrame == Time.frameCount) return _sl;
            _slFrame = Time.frameCount;
            var cam = MainCamera();
            _sl.Cam = cam != null ? cam.transform.position : Vector3.zero;
            _sl.Linear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            if ((_sun == null || !_sun.isActiveAndEnabled) && Time.time >= _sunSearch)
            {
                _sunSearch = Time.time + 3f;
                _sun = RenderSettings.sun;
                if (_sun == null || !_sun.isActiveAndEnabled)
                    _sun = UnityEngine.Object.FindObjectsOfType<Light>().Where(l => l.type == LightType.Directional && l.isActiveAndEnabled).OrderByDescending(l => l.intensity).FirstOrDefault();
            }
            if (_sun != null)
            {
                _sl.L = -_sun.transform.forward;
                _sl.Sun = (_sl.Linear ? _sun.color.linear : _sun.color) * _sun.intensity;
            }
            else { _sl.L = Vector3.up; _sl.Sun = Color.black; }
            RenderSettings.ambientProbe.Evaluate(_ambDirs, _ambOut);
            _sl.AmbUp = _ambOut[0];
            _sl.AmbSide = (_ambOut[1] + _ambOut[2] + _ambOut[3] + _ambOut[4]) * 0.25f;
            _sl.AmbDown = _ambOut[5];
            if (_sl.AmbUp.maxColorComponent < 0.003f && _sl.AmbSide.maxColorComponent < 0.003f)
            {
                // no ambient probe: the flat / trilight ambient colours, or a share of the sun
                Color sky = RenderSettings.ambientSkyColor, eq = RenderSettings.ambientEquatorColor, gr = RenderSettings.ambientGroundColor;
                if (_sl.Linear) { sky = sky.linear; eq = eq.linear; gr = gr.linear; }
                float k = RenderSettings.ambientIntensity;
                _sl.AmbUp = sky * k; _sl.AmbSide = eq * k; _sl.AmbDown = gr * k;
                if (_sl.AmbUp.maxColorComponent < 0.003f) { _sl.AmbUp = _sl.Sun * 0.25f; _sl.AmbSide = _sl.Sun * 0.18f; _sl.AmbDown = _sl.Sun * 0.08f; }
            }
            _sl.AmbUp.a = _sl.AmbSide.a = _sl.AmbDown.a = 1f;
            return _sl;
        }

        static void LogLight()
        {
            if (_lightLogged) return;
            _lightLogged = true;
            var sl = Lighting();
            HitCamMod.Log.Msg($"World plates: colour space {QualitySettings.activeColorSpace}, sun {(_sun != null ? _sun.name : "none")} {sl.Sun} dir {sl.L}, ambient up {sl.AmbUp} side {sl.AmbSide} down {sl.AmbDown} ({RenderSettings.ambientMode}), autoSyncTransforms {Physics.autoSyncTransforms}, occlusion mask {_occMask:X}");
        }

        public static bool SunVisible(Vector3 p, Vector3 toSun)
            => !Physics.Raycast(p, toSun, 80f, _occMask, QueryTriggerInteraction.Ignore);

        // ------------------------------------------------------------------ pieces

        internal static int VisibleLayer(Unit u)
        {
            var mr = u.GetComponentsInChildren<MeshRenderer>(false).FirstOrDefault(r => r.enabled);
            return mr != null ? mr.gameObject.layer : 0;
        }

        /// The unit's own hierarchy plus the parts the game moves separately (LateFollow, e.g. turrets), the active ones.
        internal static List<Transform> UnitRoots(Unit u)
        {
            var roots = new List<Transform>();
            foreach (var t in GhostSnapshot.Roots(u)) if (t != null && (t == u.transform || t.gameObject.activeInHierarchy)) roots.Add(t);
            return roots;
        }

        internal static List<MeshFilter> UnitMeshes(Unit u)
        {
            if (_unitMeshes.TryGetValue(u, out var list) && list.All(m => m != null)) return list;
            var lodSkip = new HashSet<Renderer>();
            foreach (var lg in u.GetComponentsInChildren<LODGroup>(true))
            {
                var lods = lg.GetLODs();
                for (int i = 1; i < lods.Length; i++) foreach (var r in lods[i].renderers) if (r != null) lodSkip.Add(r);
            }
            list = new List<MeshFilter>();
            int camMask = WorldCameraMask();
            foreach (var root in UnitRoots(u))
                foreach (var mf in root.GetComponentsInChildren<MeshFilter>(false))
                {
                    if (mf.name == InsideName) continue;      // our own dark insides: not the vehicle's surface
                    var r = mf.GetComponent<MeshRenderer>();
                    if (r == null || lodSkip.Contains(r) || mf.sharedMesh == null || !r.enabled) continue;
                    if (r.bounds.size.magnitude > 16f || (camMask & (1 << r.gameObject.layer)) == 0) continue;
                    if (r.GetComponentInParent<AarVisual>() != null || r.GetComponentInParent<AarModel>() != null) continue;
                    list.Add(mf);
                }
            _unitMeshes[u] = list;
            return list;
        }

        internal static Renderer Quad(Transform parent, string name, Mesh mesh, Matrix4x4 m, Material mat, int layer, Color c)
        {
            var go = new GameObject(name);
            go.layer = layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = m.GetColumn(3);
            go.transform.localRotation = m.rotation;
            go.transform.localScale = m.lossyScale;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = mat == _metal;
            Vis.SetColor(r, c);
            return r;
        }

        static Mesh _sootDisc, _sootRing;
        internal static Mesh SootDisc => _sootDisc ??= MakeSoot(false);
        static Mesh SootRing => _sootRing ??= MakeSoot(true);

        /// Blast mark: a jagged dark disc, or (ring = true) the same mark with a clear centre for the 3D plate to sit in.
        static Mesh MakeSoot(bool ring)
        {
            const int n = 28;
            var v = new List<Vector3>();
            var c = new List<Color>();
            var t = new List<int>();
            var rnd = new System.Random(7);
            var outer = new float[n];
            for (int i = 0; i < n; i++) outer[i] = 0.38f + 0.12f * (float)rnd.NextDouble();
            Color dense = new Color(0.03f, 0.025f, 0.02f, 0.9f), edge = new Color(0.05f, 0.04f, 0.03f, 0f);
            if (!ring)
            {
                v.Add(Vector3.zero); c.Add(dense);
                for (int i = 0; i < n; i++)
                {
                    float a = i * Mathf.PI * 2f / n;
                    v.Add(new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * outer[i]); c.Add(edge);
                    t.Add(0); t.Add(i + 1); t.Add((i + 1) % n + 1);
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    float a = i * Mathf.PI * 2f / n;
                    var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    v.Add(d * 0.13f); c.Add(new Color(dense.r, dense.g, dense.b, 0f));
                    v.Add(d * 0.24f); c.Add(dense);
                    v.Add(d * outer[i]); c.Add(edge);
                }
                for (int i = 0; i < n; i++)
                {
                    int a = i * 3, b = ((i + 1) % n) * 3;
                    for (int k = 0; k < 2; k++)
                    {
                        t.Add(a + k); t.Add(a + k + 1); t.Add(b + k);
                        t.Add(b + k); t.Add(a + k + 1); t.Add(b + k + 1);
                    }
                }
            }
            var m = new Mesh { name = ring ? "soot ring" : "soot" };
            m.SetVertices(v); m.SetColors(c); m.SetTriangles(t, 0);
            m.RecalculateBounds();
            return m;
        }

        /// Radius factor of a torn edge at angle a: a few low-frequency lobes plus fine jaggies, same for the same seed.
        static float Torn(float a, float ragged, int seed)
        {
            var rnd = new System.Random(seed);
            float p1 = (float)rnd.NextDouble() * 6.3f, p2 = (float)rnd.NextDouble() * 6.3f, p3 = (float)rnd.NextDouble() * 6.3f;
            float lobes = 0.55f * Mathf.Sin(3f * a + p1) + 0.3f * Mathf.Sin(5f * a + p2) + 0.15f * Mathf.Sin(9f * a + p3);
            float fine = 0.5f + 0.5f * Mathf.Sin(23f * a + p1 * 3f) * Mathf.Sin(17f * a + p2 * 2f);
            return 1f + ragged * (lobes * 0.8f + (fine - 0.5f) * 0.35f);
        }

        /// Jagged opening: dark brown at the torn edge, black in the middle (vertex-coloured, unlit).
        static Mesh RaggedDisc(float r, float ragged, int seed)
        {
            const int n = 48;
            var v = new Vector3[n + 1];
            var c = new Color[n + 1];
            var t = new int[n * 3];
            v[0] = Vector3.zero; c[0] = new Color(0.01f, 0.01f, 0.01f);
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                float k = Torn(a, ragged, seed);
                v[i + 1] = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * r * k;
                c[i + 1] = new Color(0.06f, 0.045f, 0.035f);
                t[i * 3] = 0; t[i * 3 + 1] = i + 1; t[i * 3 + 2] = (i + 1) % n + 1;
            }
            var m = new Mesh { name = "ragged hole", vertices = v, colors = c, triangles = t };
            m.RecalculateBounds();
            return m;
        }

        /// Thin hot band hugging the same jagged edge (same seed as the opening).
        static Mesh ThinRim(float r, float ragged, int seed)
        {
            const int n = 48;
            var v = new List<Vector3>();
            var c = new List<Color>();
            var tri = new List<int>();
            for (int i = 0; i < n; i++)
            {
                float a = i * Mathf.PI * 2f / n;
                float k = Torn(a, ragged, seed);
                Vector3 d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                v.Add(d * r * k * 0.97f); c.Add(Color.white);
                v.Add(d * r * k * 1.18f); c.Add(Color.black);
            }
            for (int i = 0; i < n; i++)
            {
                int a0 = i * 2, a1 = a0 + 1, b0 = ((i + 1) % n) * 2, b1 = b0 + 1;
                tri.Add(a0); tri.Add(b0); tri.Add(a1);
                tri.Add(b0); tri.Add(b1); tri.Add(a1);
            }
            var m = new Mesh { name = "thin rim" };
            m.SetVertices(v); m.SetColors(c); m.SetTriangles(tri, 0);
            m.RecalculateBounds();
            return m;
        }

        internal static void Sparks(Vector3 pos, Vector3 dir, int count, float speed, float cone, int layer)
        {
            var go = new GameObject("HitCam sparks");
            go.layer = layer;
            go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(dir));
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.duration = 0.2f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.25f, 1.1f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(speed * 0.3f, speed);
            main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.06f);
            main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.85f, 0.45f), new Color(1f, 0.5f, 0.15f));
            main.gravityModifier = 1.2f;
            main.maxParticles = count;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            var em = ps.emission;
            em.rateOverTime = 0;
            em.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });
            var sh = ps.shape;
            sh.shapeType = ParticleSystemShapeType.Cone;
            sh.angle = cone;
            sh.radius = 0.02f;
            var col = ps.colorOverLifetime;
            col.enabled = true;
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(new Color(1f, 0.9f, 0.6f), 0f), new GradientColorKey(new Color(1f, 0.35f, 0.08f), 1f) },
                      new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            col.color = g;
            var rend = go.GetComponent<ParticleSystemRenderer>();
            rend.sharedMaterial = _spark;
            rend.renderMode = ParticleSystemRenderMode.Stretch;
            rend.velocityScale = 0.035f;
            rend.lengthScale = 1.5f;
            ps.Play();
            go.AddComponent<Expire>().At = Time.time + 2.5f;
        }

        internal static void EnsureInit() => Init();
        internal static Material Metal { get { Init(); return _metal; } }
        internal static Material SootMat { get { Init(); return _soot; } }
        internal static Material DarkMat { get { Init(); return _dark; } }
        internal static Material GlowMat { get { Init(); return _glow; } }

        /// A model piece the world modules now draw themselves (a cut barrel, a broken tail boom): no more holes cut into it here.
        internal static void ForgetCut(Renderer r)
        {
            var mf = r != null ? r.GetComponent<MeshFilter>() : null;
            if (mf == null) return;
            _cuts.Remove(mf);
            if (_inside.TryGetValue(mf, out var inner)) { if (inner != null) UnityEngine.Object.Destroy(inner.gameObject); _inside.Remove(mf); }
        }

        /// The cut pieces' dark insides (child of each piece).
        static readonly Dictionary<MeshFilter, MeshFilter> _inside = new Dictionary<MeshFilter, MeshFilter>();
        static Material _inner;
        internal const string InsideName = "HitCam dark interior";

        /// A hull piece with a hole cut in it: the piece again, inside out (only its back faces, the inner side of the
        /// walls) in dark soot brown. Through the hole you look into a dark hull instead of out through the far wall (whose
        /// inner side the game never draws) at the ground behind. Drawn a little behind its own depth, so where a model
        /// has double-sided panels their paint still wins.
        static void DarkInside(MeshFilter mf, Mesh m, Material walls = null)
        {
            if (mf != null && _inside.TryGetValue(mf, out var have) && have != null)
            {
                have.sharedMesh = m;
                if (walls != null) { var hr = have.GetComponent<MeshRenderer>(); var hm = hr.sharedMaterials; for (int i = 0; i < hm.Length; i++) hm[i] = walls; hr.sharedMaterials = hm; }
                return;
            }
            if (_inner == null)
            {
                _inner = Mat(Shader.Find("Hidden/Internal-Colored"), BlendMode.One, BlendMode.Zero, true, CompareFunction.LessEqual, 2001);
                _inner.SetInt("_Cull", (int)CullMode.Front);
                _inner.SetFloat("_ZBias", 1f);
                _inner.color = new Color(0.062f, 0.051f, 0.044f, 1f);   // sooted steel, not a black hole
            }
            if (mf == null) return;
            var go = new GameObject(InsideName) { layer = mf.gameObject.layer };
            go.transform.SetParent(mf.transform, false);
            var f = go.AddComponent<MeshFilter>();
            f.sharedMesh = m;
            var r = go.AddComponent<MeshRenderer>();
            var mats = new Material[Mathf.Max(1, m.subMeshCount)];
            for (int i = 0; i < mats.Length; i++) mats[i] = walls ?? _inner;
            r.sharedMaterials = mats;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            _inside[mf] = f;
            JoinLod(mf.GetComponent<Renderer>(), r);
        }

        /// Our copy shows and hides with the piece it belongs to (a LOD1 hull is a little smaller or bigger than LOD0: the
        /// LOD0 inside would poke through it at a distance).
        static void JoinLod(Renderer src, Renderer ours)
        {
            if (src == null) return;
            var lg = src.GetComponentInParent<LODGroup>();
            if (lg == null) return;
            var lods = lg.GetLODs();
            for (int i = 0; i < lods.Length; i++)
            {
                if (lods[i].renderers == null || Array.IndexOf(lods[i].renderers, src) < 0) continue;
                var list = new List<Renderer>(lods[i].renderers) { ours };
                lods[i].renderers = list.ToArray();
                lg.SetLODs(lods);
                return;
            }
        }

        /// The turret (or another big part) thrown off: the hull is open at the ring and the part underneath. Their big
        /// pieces get dark insides, so you look into a dark hull, not through it. Works on any model (it only draws the
        /// game's mesh again, nothing is read or cut).
        internal static void OpenedUp(Unit unit, Transform part)
        {
            if (!Settings.WorldDamage.Value) return;
            try
            {
                Init();
                var roots = new List<Transform>();
                if (unit != null) roots.AddRange(GhostSnapshot.Roots(unit));
                if (part != null) roots.Add(part);
                int n = 0;
                // this hull's own inside walls: lit by its fire (they flicker with it, then cool to soot)
                if (_inner == null) DarkInside(null, null);
                var walls = new Material(_inner);
                foreach (var root in roots)
                {
                    if (root == null) continue;
                    foreach (var mf in root.GetComponentsInChildren<MeshFilter>(false))
                    {
                        if (mf == null || mf.sharedMesh == null || mf.name == InsideName) continue;   // (a cut piece's inside gets the fire too)
                        var rr = mf.GetComponent<MeshRenderer>();
                        if (rr == null || !rr.enabled || rr.bounds.size.magnitude < 0.8f) continue;
                        int layer = mf.gameObject.layer;
                        if (layer == 15 || layer == 21 || layer == Vis.Layer || layer == Vis.DimLayer || layer == _debrisLayer) continue;
                        string sh = rr.sharedMaterial != null && rr.sharedMaterial.shader != null ? rr.sharedMaterial.shader.name : "";
                        if (sh.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0 || sh.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (rr.sharedMaterials.Any(m => m != null && IsNet(m))) continue;   // a net's far side would show black through its holes
                        DarkInside(mf, mf.sharedMesh, walls);
                        n++;
                    }
                }
                int burnt = CharredInterior(unit, part, out var embers);
                if (part != null && unit != null) InteriorGlow.Light(unit, part.position - unit.transform.up * 0.55f, embers, walls);
                if (n > 0 && (!_openLogged || AutoTest.Active)) { _openLogged = true; HitCamMod.Log.Msg($"World damage: {Recorder.SafeName(unit)} opened up ({part?.name}): {n} pieces dark inside, {burnt} interior parts charred"); }
                if (n > 0 && unit != null && AutoTest.Opt("capture", true)) MelonLoader.MelonCoroutines.Start(SnapOpened(unit));
            }
            catch (Exception e) { HitCamMod.Log.Warning("World damage: dark inside: " + e.Message); }
        }
        static bool _openLogged;
        static Material _charred;
        internal const string CharredName = "HitCam charred interior";

        /// What's inside an opened hull: the game's own models of the interior (seats, the gun's breech, drives, the
        /// engine, the rack and its rounds; GHPC keeps them for its x-ray in the after-action view, on a layer no game
        /// camera draws) drawn on the vehicle in burnt steel, lit by the sun through the opening and by the fire.
        static int CharredInterior(Unit unit, Transform turret, out Material embers)
        {
            embers = null;
            if (unit == null) return 0;
            // only what's under the open ring: the rest can't be seen, and some of the game's x-ray boxes (fuel tanks,
            // stowage) are bigger than the hull and came through its roof as burnt slabs
            var ut = unit.transform;
            Vector3 ringL = turret != null ? ut.InverseTransformPoint(turret.position) : Vector3.zero;
            float ringR = 1.35f;
            if (_charred == null)
            {
                var std = Shader.Find("Standard");
                _charred = std != null ? new Material(std) : new Material(Vis.Solid);
                _charred.color = new Color(0.20f, 0.17f, 0.15f);    // burnt paint and ash over steel: dark, but the shapes read
                if (std != null)
                {
                    _charred.SetFloat("_Metallic", 0.15f);
                    _charred.SetFloat("_Glossiness", 0.16f);
                    _charred.EnableKeyword("_EMISSION");
                    _charred.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                    _charred.SetColor("_EmissionColor", Color.black);
                }
            }
            // this vehicle's own copy: its embers glow and die down with its fire (InteriorGlow)
            embers = new Material(_charred);
            int layer = VisibleLayer(unit), n = 0;
            foreach (var av in GhostSnapshot.AarVisualsOf(unit))
            {
                if (av == null) continue;
                string an = av.name.ToLowerInvariant();
                // (the rack's rounds too: what's left of them and the carousel is most of what you see down there)
                var rs = av.SpecificRenderers != null && av.SpecificRenderers.Length > 0 ? av.SpecificRenderers : av.GetComponentsInChildren<Renderer>(true);
                foreach (var r in rs)
                {
                    var mf = r != null && r is MeshRenderer ? r.GetComponent<MeshFilter>() : null;
                    if (mf == null || mf.sharedMesh == null || r.transform.Find(CharredName) != null || _charredFor.Contains(mf)) continue;
                    if (turret != null)
                    {
                        Vector3 cL = ut.InverseTransformPoint(r.bounds.center), eL = r.bounds.extents;
                        if (new Vector2(cL.x - ringL.x, cL.z - ringL.z).magnitude > ringR || cL.y + eL.y > ringL.y + 0.1f) continue;
                    }
                    _charredFor.Add(mf);
                    var go = new GameObject(CharredName) { layer = layer };
                    go.transform.SetPositionAndRotation(r.transform.position, r.transform.rotation);
                    go.transform.localScale = r.transform.lossyScale;
                    go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    var mats = new Material[Mathf.Max(1, mf.sharedMesh.subMeshCount)];
                    for (int i = 0; i < mats.Length; i++) mats[i] = embers;
                    mr.sharedMaterials = mats;
                    go.AddComponent<StickTo>().Bind(r.transform);   // follows it even where the game keeps the model switched off
                    n++;
                }
            }
            return n;
        }
        static readonly HashSet<MeshFilter> _charredFor = new HashSet<MeshFilter>();

        /// Self-test: a look down into the opened hull once the turret has gone.
        static System.Collections.IEnumerator SnapOpened(Unit u)
        {
            float t0 = Time.realtimeSinceStartup;
            foreach (float at in new[] { 3f, 15f, 45f })
            {
                while (Time.realtimeSinceStartup - t0 < at) yield return null;
                if (u == null) yield break;
                Vector3 c = u.transform.position + u.transform.up * 1.2f;
                string n = "opened_" + Recorder.SafeName(u).Replace(' ', '_') + (at > 3f ? "_" + at.ToString("0") + "s" : "");
                ModuleTest.Snap(c, c + u.transform.up * 4.5f + u.transform.right * 2.5f - u.transform.forward * 1.5f, n, "inside, " + at.ToString("0") + " s after it opened");
                ModuleTest.Snap(c - u.transform.up * 0.6f, c + u.transform.up * 2.6f - u.transform.forward * 1.2f + u.transform.right * 0.8f, n + "_close", "looking in");
            }
        }
        internal static int DebrisLayer { get { Init(); return _debrisLayer; } }

        /// A loose physics piece in the world (a track link, a barrel end, a tail boom): a box collider of its mesh's bounds,
        /// steel density unless `mass` is given; counts toward MaxDebris and lives DebrisSeconds (life < 0) or `life` seconds.
        internal static GameObject SpawnPiece(string name, Vector3 pos, Quaternion rot, Vector3 scale, Vector3 vel, Vector3 angVel,
                                              Mesh mesh = null, Material[] mats = null, float mass = -1f, float life = -1f, int layer = -1)
        {
            Init();
            var go = new GameObject(name);
            go.layer = layer >= 0 ? layer : _debrisLayer;
            go.transform.SetPositionAndRotation(pos, rot);
            go.transform.localScale = scale;
            var m = mesh != null ? mesh : Prims.Cube;
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var r = go.AddComponent<MeshRenderer>();
            if (mats != null && mats.Length > 0) r.sharedMaterials = mats; else r.sharedMaterial = _metal;
            r.shadowCastingMode = ShadowCastingMode.On;
            var bc = go.AddComponent<BoxCollider>();
            bc.center = m.bounds.center;
            bc.size = Vector3.Max(m.bounds.size, Vector3.one * 0.01f);
            var rb = go.AddComponent<Rigidbody>();
            Vector3 ws = Vector3.Scale(m.bounds.size, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            rb.mass = mass > 0f ? mass : Mathf.Clamp(ws.x * ws.y * ws.z * 7800f * 0.35f, 0.05f, 4000f);
            rb.velocity = vel;
            rb.angularVelocity = angVel;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            go.AddComponent<Expire>().At = Time.time + (life > 0f ? life : Settings.DebrisSeconds.Value);
            _debris.Add(go);
            _debris.RemoveAll(d => d == null);
            while (_debris.Count > Settings.MaxDebris.Value) { UnityEngine.Object.Destroy(_debris[0]); _debris.RemoveAt(0); }
            return go;
        }

        static void SpawnChunk(Vector3 pos, Vector3 vel, float size)
        {
            var go = new GameObject("HitCam shrapnel");
            go.layer = _debrisLayer;
            go.transform.SetPositionAndRotation(pos, UnityEngine.Random.rotationUniform);
            go.transform.localScale = new Vector3(size, size * UnityEngine.Random.Range(0.4f, 1f), Mathf.Max(size * 0.25f, 0.006f));
            go.AddComponent<MeshFilter>().sharedMesh = Prims.Cube;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = _metal;
            r.shadowCastingMode = size < 0.04f ? ShadowCastingMode.Off : ShadowCastingMode.On;   // a few-cm chip's shadow is a dot
            go.AddComponent<BoxCollider>();
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = Mathf.Clamp(size * size * size * 7800f, 0.02f, 25f);
            rb.velocity = vel;
            rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 30f;
            // speculative contacts: they don't go through the ground at these speeds, and cost a fraction of full CCD in
            // the game's own physics step
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            go.AddComponent<Expire>().At = Time.time + Settings.DebrisSeconds.Value;
            // hot when torn off, cools down in a few seconds
            go.AddComponent<Cool>().Init(r, new Color(1f, 0.45f, 0.15f), 3.5f, _metal.color);
            _debris.Add(go);
            _debris.RemoveAll(d => d == null);
            while (_debris.Count > Settings.MaxDebris.Value) { UnityEngine.Object.Destroy(_debris[0]); _debris.RemoveAt(0); }
        }

        public static void Reset()
        {
            foreach (var m in _ownedCuts) if (m != null) UnityEngine.Object.Destroy(m);
            _ownedCuts.Clear();
            _byRound.Clear();
            _preJet.Clear();
            _unitMeshes.Clear();
            _hulls.Clear();
            _pending.Clear();
            foreach (var pr in _probing) if (pr.Job != null) SurfaceDepthEnd(pr.Job);
            _probing.Clear();
            _cuts.Clear();
            _inside.Clear();
            _charredFor.Clear();
            _debris.Clear();
            WorldPlate.All.Clear();
            WorldModules.Reset();
            _sun = null;
        }

        /// One unit's meshes, renderers and armour colliders, found again on the next look (another mod rebuilt it).
        public static void Forget(Unit u)
        {
            if (u == null) return;
            _unitMeshes.Remove(u);
            _hulls.Remove(u);
            _unitRenderers.Remove(u);
            WorldModules.Forget(u);
        }
    }

    /// One armour hit on a real vehicle, drawn as the 3D plate: it folds for ~0.3 s, then freezes; its hot edges cool
    /// over a few seconds and it is relit now and then as the vehicle turns. Lives on the hit's "HitCam damage" root.
    internal class WorldPlate : MonoBehaviour
    {
        public static readonly List<WorldPlate> All = new List<WorldPlate>();

        /// A hit waiting for its 3D plate to be built (positions in the struck part's own space).
        internal class Pending
        {
            public Collider Col;
            public Vector3 P, N, D;
            public bool Exit, Pen, Ric;
            public float Nominal, Los, PenMm, Speed, Energy, HoleR;
            public AmmoType Ammo;
            public Renderer[] Flat;
            public Renderer Ring;
        }

        public HoleFx Fx;
        public Pending Wait;
        public Unit Unit;
        public bool Hole;            // goes through into the vehicle (not a dent / gouge)
        public float HoleR;
        public int Layer;
        public float Born;
        Renderer _plate, _cup, _punchR, _ring;
        Transform _punch;
        Renderer[] _flat;
        float _next, _nextShape, _nextShadow, _shadow = 1f;
        bool _frozen, _failed;
        int _shown = -1;

        public bool Has3D => Fx != null && _plate != null;

        void Awake() { All.Add(this); Born = Time.time; }     // the hit; Init sets it again when the 3D plate is built

        public void Init(HoleFx fx, bool hole, Renderer[] flat, Renderer ring, Material plate, Material cup, Material punch)
        {
            Fx = fx; Hole = hole; _flat = flat; _ring = ring;
            int layer = Layer;
            Born = Time.time;
            fx.T.SetParent(transform, true);
            fx.T.gameObject.layer = layer;
            _plate = Attach(fx.T.gameObject, fx.PlateMesh, plate);
            if (fx.CupMesh != null && fx.CupMesh.vertexCount > 0)
            {
                var cg = new GameObject("cup") { layer = layer };
                cg.transform.SetParent(fx.T, false);
                _cup = Attach(cg, fx.CupMesh, cup);
            }
            var pg = new GameObject("HitCam plate punch") { layer = layer };
            _punch = pg.transform;
            _punchR = Attach(pg, fx.PunchMesh, punch);
            _shown = -1;
            Show(false);
        }

        static Renderer Attach(GameObject go, Mesh m, Material mat)
        {
            go.AddComponent<MeshFilter>().sharedMesh = m;
            var r = go.AddComponent<MeshRenderer>();
            Vis.Quiet(r);
            r.sharedMaterial = mat;
            return r;
        }

        public static WorldPlate Near(Unit u, Vector3 p, float dist)
        {
            foreach (var w in All)
                if (w != null && w.Unit == u && ((w.Has3D ? w.Fx.T.position : w.transform.position) - p).sqrMagnitude < dist * dist) return w;
            return null;
        }

        /// 3D plate (its punch placed for this camera) or the flat decals.
        public void Show(bool three)
        {
            if (!Has3D) three = false;
            int s = three ? 1 : 0;
            if (s == _shown) return;
            _shown = s;
            if (_plate != null) _plate.enabled = three;
            if (_cup != null) _cup.enabled = three;
            if (_punchR != null) _punchR.enabled = three;
            if (_ring != null) _ring.enabled = three;
            if (_flat != null) foreach (var f in _flat) if (f != null) f.enabled = !three;
        }

        /// The footprint scaled about the camera: same pixels, but behind everything the plate, bore and cup draw.
        public void PlacePunch(Camera cam)
        {
            Transform t = Fx.T;
            Vector3 p = t.position, c = cam.transform.position;
            float push = Fx.Depth + 0.05f;
            if (cam.orthographic)
            {
                _punch.SetPositionAndRotation(p + cam.transform.forward * push, t.rotation);
                _punch.localScale = Vector3.one;
                return;
            }
            float d = Mathf.Max((p - c).magnitude, 0.01f), k = (d + push) / d;
            _punch.SetPositionAndRotation(c + (p - c) * k, t.rotation);
            _punch.localScale = new Vector3(k, k, k);
        }

        void Update() { using (Perf.T("WorldPlate.Update")) TimedUpdate(); }
        void TimedUpdate()
        {
            if (!Has3D || _failed) return;
            try
            {
                float age = Time.time - Born;
                bool reshaped = false;
                // the fold at up to 30 Hz, the colours (cooling, light) at 20 Hz while hot, then a few times a second
                if (!_frozen && Time.time >= _nextShape)
                {
                    float u = age / HoleFx.WorldFold;
                    Fx.ShapeWorld(u);
                    _frozen = u >= 1f;
                    _nextShape = Time.time + 1f / 30f;
                    reshaped = true;
                }
                if (Time.time < _next && !(reshaped && _frozen)) return;
                Paint(age);
            }
            catch (Exception e)
            {
                _failed = true;
                HitCamMod.Log.Warning("World plate update failed: " + e.Message);
                Drop3D();
            }
        }

        void Paint(float age)
        {
            var sl = WorldDamage.Lighting();
            _next = Time.time + (age < HoleFx.WorldCool + 0.2f ? 0.05f : 0.25f + 0.1f * UnityEngine.Random.value);
            // far from the camera nobody sees the shading change; check again later
            if (age > HoleFx.WorldCool + 0.2f && (sl.Cam - transform.position).sqrMagnitude > 300f * 300f) { _next = Time.time + 1.5f; return; }
            if (Time.time >= _nextShadow)
            {
                _nextShadow = Time.time + 0.5f;
                _shadow = WorldDamage.SunVisible(Fx.T.position - Fx.T.forward * 0.06f, sl.L) ? 1f : 0.12f;
            }
            Fx.PaintWorld(sl, age, _shadow);
        }

        /// Back to the flat decals only (plate cap, or an error).
        public void Drop3D()
        {
            if (_punch != null) Destroy(_punch.gameObject);
            if (Fx != null)
            {
                if (Fx.T != null) Destroy(Fx.T.gameObject);
                Fx.Dispose();
            }
            Fx = null;
            _plate = _cup = _punchR = null;
            _shown = -1;
            Show(false);
        }

        void OnDestroy()
        {
            All.Remove(this);
            if (_punch != null) Destroy(_punch.gameObject);
            Fx?.Dispose();
        }
    }

    /// Fades a renderer's colour from hot to cold.
    internal class Cool : MonoBehaviour
    {
        Renderer _r;
        Color _hot, _cold;
        float _t0, _dur;

        public void Init(Renderer r, Color hot, float seconds, Color? cold = null)
        {
            _r = r; _hot = hot; _dur = seconds; _t0 = Time.time;
            _cold = cold ?? Color.black;
        }

        void Update()
        {
            if (_r == null) { Destroy(this); return; }
            float k = Mathf.Clamp01((Time.time - _t0) / _dur);
            Vis.SetColor(_r, Color.Lerp(_hot, _cold, k * k));
            if (k >= 1f) Destroy(this);
        }
    }
}

namespace GHPCHitCam
{
    /// The fire inside a hull that lost its turret: an orange light down in the hull, flickering, dying down over a
    /// couple of minutes. It lights the charred interior from below the way the flames do.
    internal class InteriorGlow : MonoBehaviour
    {
        UnityEngine.Light _l;
        Material _embers, _walls;
        float _born, _seed;
        static readonly Color Soot = new Color(0.062f, 0.051f, 0.044f, 1f), FireLit = new Color(0.30f, 0.13f, 0.05f, 1f);

        public static void Light(GHPC.Unit unit, Vector3 at, Material embers = null, Material walls = null)
        {
            var go = new GameObject("HitCam interior fire light");
            go.transform.position = at;
            go.AddComponent<StickTo>().Bind(unit.transform);
            var g = go.AddComponent<InteriorGlow>();
            g._embers = embers;
            g._walls = walls;
            g._l = go.AddComponent<UnityEngine.Light>();
            g._l.type = LightType.Point;
            g._l.range = 5.5f;
            g._l.color = new Color(1f, 0.52f, 0.2f);
            g._l.shadows = LightShadows.None;
            g._born = Time.time;
            g._seed = UnityEngine.Random.value * 100f;
        }

        void Update()
        {
            float age = Time.time - _born;
            float fade = Mathf.Exp(-age / 70f);
            float flicker = 0.65f + 0.35f * Mathf.PerlinNoise(_seed, Time.time * 7f) + 0.15f * Mathf.PerlinNoise(_seed + 3f, Time.time * 23f);
            // the embers on the burnt parts glow on a while after the flames (and pulse with them)
            if (_embers != null) _embers.SetColor("_EmissionColor", new Color(0.22f, 0.07f, 0.018f) * (Mathf.Exp(-age / 150f) * (0.7f + 0.3f * flicker)));   // glowing like embers next to the flames
            // the inside walls: they take the fire's light (the drawn hull's inside is unlit, so it's painted in), then
            // cool to soot
            if (_walls != null) _walls.color = Color.Lerp(Soot, FireLit, Mathf.Clamp01(fade * (0.7f + 0.3f * flicker)));
            if (fade < 0.03f) { if (_embers != null) _embers.SetColor("_EmissionColor", Color.black); if (_walls != null) _walls.color = Soot; Destroy(gameObject); return; }
            _l.intensity = 6f * fade * flicker;
        }
    }
}
