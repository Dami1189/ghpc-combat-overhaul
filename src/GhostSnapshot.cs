using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using GHPC;
using GHPC.Crew;
using GHPC.Humans;
using GHPC.Utility;
using UnityEngine;
using DC = GHPC.Equipment.DestructibleComponent;

namespace GHPCHitCam
{
    internal enum ModCat { Other, Engine, Transmission, Radiator, Tracks, Gun, TurretDrive, Optics, Fuel, Ammo, Radio }

    internal class MeshPart
    {
        public Mesh Mesh;
        public Matrix4x4 M;     // mesh space -> target-local space
        public bool OwnsMesh;   // baked copy, we have to destroy it later
        public string Name;
        public Material[] Mats; // game's own materials (for the textured x-ray hull)
        public bool Inner;      // inside walls of the crew compartment: no skin, no outline
        public Renderer Src;    // game renderer it came from (for the logs)

        /// axis-aligned box of the part in target-local space
        public Bounds LocalAabb()
        {
            var mb = Mesh.bounds;
            Bounds b = new Bounds(M.MultiplyPoint3x4(mb.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                b.Encapsulate(M.MultiplyPoint3x4(c));
            }
            return b;
        }

        /// parts parked far away or crazy big (pooled objects, effect volumes) would wreck the camera framing
        public bool Sane()
        {
            if (Mesh == null) return false;
            var b = LocalAabb();
            return b.center.magnitude < 9f && b.size.magnitude < 16f;
        }
    }

    internal class ModulePart
    {
        public DC Comp;
        public string Name;
        public ModCat Cat;
        public float InitialHealth;
        public readonly List<MeshPart> Shapes = new List<MeshPart>();     // its hit zone (damage, physics)
        public readonly List<MeshPart> Detail = new List<MeshPart>();     // game's own x-ray model of it if any (drawn instead)
    }

    internal class CrewPartInfo
    {
        public MeshPart Shape;
        public BodyPart Part;
        public DC Comp;
        public int Parent = -1;          // index into CrewInfo.Parts, anatomical parent (for ragdoll joints)
        public bool Internal;            // organs: ride with their container, no own rigidbody
    }

    internal class CrewInfo
    {
        public CrewManager.CrewMember Member;
        public string Label;
        public HealthStatus InitialStatus;
        /// skin being worked out on a worker thread for the hit cam (CrewBuilder.Prepare) until the hit cam takes it
        public CrewBuilder.SkinPlan Plan;
        public readonly List<MeshPart> Shapes = new List<MeshPart>();
        public readonly List<CrewPartInfo> Parts = new List<CrewPartInfo>();
        public readonly List<MeshPart> Visuals = new List<MeshPart>();   // game's crew model, baked at impact

        public void AddPart(MeshPart shape, BodyPart part, DC comp)
        {
            Shapes.Add(shape);
            Parts.Add(new CrewPartInfo { Shape = shape, Part = part, Comp = comp, Internal = IsOrgan(part) });
        }

        static bool IsOrgan(BodyPart p) => p == BodyPart.Brain || p == BodyPart.LeftEye || p == BodyPart.RightEye || p == BodyPart.Heart || p == BodyPart.LeftLung || p == BodyPart.RightLung || p == BodyPart.Gut;

        /// anatomical tree from the body part labels: torso is the root, limbs chain outwards by distance
        public void BuildSkeleton()
        {
            int Find(params BodyPart[] want)
            {
                foreach (var w in want)
                    for (int i = 0; i < Parts.Count; i++) if (Parts[i].Part == w && !Parts[i].Internal) return i;
                return -1;
            }
            Vector3 C(int i) => Parts[i].Shape.LocalAabb().center;
            int torso = Find(BodyPart.Torso, BodyPart.UpperSpine, BodyPart.LowerSpine);
            if (torso < 0) return;
            int lower = Find(BodyPart.LowerSpine);
            int head = Find(BodyPart.Head);
            void Chain(BodyPart limb, BodyPart end, int root)
            {
                var segs = new List<int>();
                for (int i = 0; i < Parts.Count; i++) if (Parts[i].Part == limb) segs.Add(i);
                for (int i = 0; i < Parts.Count; i++) if (Parts[i].Part == end) segs.Add(i);
                if (root < 0) return;
                int prev = root;
                var left = new List<int>(segs);
                while (left.Count > 0)
                {
                    int best = left.OrderBy(i => (C(i) - C(prev)).sqrMagnitude).First();
                    Parts[best].Parent = prev;
                    prev = best;
                    left.Remove(best);
                }
            }
            for (int i = 0; i < Parts.Count; i++)
            {
                var p = Parts[i];
                if (i == torso) continue;
                switch (p.Part)
                {
                    case BodyPart.UpperSpine: case BodyPart.LowerSpine: case BodyPart.Neck: p.Parent = torso; break;
                    case BodyPart.Head: p.Parent = Find(BodyPart.Neck) >= 0 ? Find(BodyPart.Neck) : torso; break;
                    case BodyPart.Brain: case BodyPart.LeftEye: case BodyPart.RightEye: p.Parent = head >= 0 ? head : torso; break;
                    case BodyPart.Heart: case BodyPart.LeftLung: case BodyPart.RightLung: case BodyPart.Gut: p.Parent = torso; break;
                }
            }
            Chain(BodyPart.LeftArm, BodyPart.LeftHand, torso);
            Chain(BodyPart.RightArm, BodyPart.RightHand, torso);
            Chain(BodyPart.LeftLeg, BodyPart.LeftFoot, lower >= 0 ? lower : torso);
            Chain(BodyPart.RightLeg, BodyPart.RightFoot, lower >= 0 ? lower : torso);

            // anything still loose (duplicates, weird labels) joins the nearest part already in the tree
            var inTree = new HashSet<int> { torso };
            bool grew = true;
            while (grew)
            {
                grew = false;
                for (int i = 0; i < Parts.Count; i++)
                    if (!inTree.Contains(i) && Parts[i].Parent >= 0 && inTree.Contains(Parts[i].Parent)) { inTree.Add(i); grew = true; }
            }
            for (int guard = 0; guard < Parts.Count; guard++)
            {
                int bestI = -1, bestP = -1;
                float bd = float.MaxValue;
                for (int i = 0; i < Parts.Count; i++)
                {
                    if (inTree.Contains(i)) continue;
                    foreach (int t in inTree)
                    {
                        if (Parts[t].Internal) continue;
                        float d = (C(i) - C(t)).sqrMagnitude;
                        if (d < bd) { bd = d; bestI = i; bestP = t; }
                    }
                }
                if (bestI < 0) break;
                Parts[bestI].Parent = bestP;
                inTree.Add(bestI);
            }
            HasSkeleton = inTree.Count == Parts.Count;
        }

        public bool HasSkeleton;

        public HealthStatus Status()
        {
            try
            {
                if (Member?.Human is Human h) return h.GetOverallStatus();
            }
            catch { }
            return HealthStatus.Fine;
        }
    }

    internal class AmmoPart
    {
        public AmmoVisual Visual;
        public readonly List<MeshPart> Shapes = new List<MeshPart>();
    }

    /// frozen copy (meshes + transforms relative to the vehicle) of a unit at the moment it got hit
    internal class GhostSnapshot
    {
        public string UnitName;
        public Bounds LocalBounds;
        public readonly List<MeshPart> Exterior = new List<MeshPart>();
        public readonly List<ModulePart> Modules = new List<ModulePart>();
        public readonly List<CrewInfo> Crew = new List<CrewInfo>();
        public readonly List<AmmoPart> Ammo = new List<AmmoPart>();
        public readonly List<MeshPart> Fixtures = new List<MeshPart>();   // seats, brackets, boxes: interior fittings (x-ray models)
        /// GHPC's armour colliders (small readable meshes and boxes that follow the hull a few cm inside the paint) in
        /// target-local space, x-ray physics uses them as the vehicle's real walls
        public readonly List<(Mesh mesh, Matrix4x4 m, bool convex)> ArmourMeshes = new List<(Mesh, Matrix4x4, bool)>();
        public readonly List<(Vector3 c, Vector3 size, Matrix4x4 m)> ArmourBoxes = new List<(Vector3, Vector3, Matrix4x4)>();
        public int Rounds;
        public readonly Dictionary<DC, CrewInfo> CrewOfPart = new Dictionary<DC, CrewInfo>();
        public readonly Dictionary<DC, ModulePart> ModuleOf = new Dictionary<DC, ModulePart>();

        public readonly List<string> Rejected = new List<string>();

        static void Reject(GhostSnapshot s, MeshPart p)
        {
            var b = p.Mesh != null ? p.LocalAabb() : new Bounds();
            s.Rejected.Add($"{p.Name} @{b.center.magnitude:0}m size {b.size.magnitude:0}m");
            if (p.OwnsMesh && p.Mesh != null) UnityEngine.Object.Destroy(p.Mesh);
        }

        static readonly HashSet<string> _misplacedLogged = new HashSet<string>();
        static readonly HashSet<string> _wreckLogged = new HashSet<string>();
        static readonly HashSet<string> _roundDiag = new HashSet<string>();

        /// parts the game threw off the vehicle (turret after a cook-off): not the vehicle any more
        static List<Transform> Thrown(Unit u, List<Transform> roots)
        {
            var list = new List<Transform>();
            try
            {
                if (Recorder.Thrown.TryGetValue(u, out var seen)) foreach (var t in seen) if (t != null && !list.Contains(t)) list.Add(t);
                foreach (var root in roots)
                    foreach (var dp in root.GetComponentsInChildren<GHPC.Effects.DetachableParent>(true))
                        if (dp != null && dp.Detached)
                        {
                            var t = dp.Transform != null ? dp.Transform : dp.transform;
                            if (!list.Contains(t)) list.Add(t);
                        }
            }
            catch { }
            return list;
        }

        static string PathOf(Transform t)
        {
            string p = t != null ? t.name : "?";
            for (var x = t != null ? t.parent : null; x != null && p.Length < 200; x = x.parent) p = x.name + "/" + p;
            return p;
        }

        /// wreck's hull, local: the running gear's box (tracks, wheels stay with the hull) up to about the deck, a bit
        /// wider. null if it has none
        static Bounds? HullBox(SnapPlan plan, List<Transform> thrown, Matrix4x4 w2l)
        {
            bool any = false;
            var b = new Bounds();
            foreach (var dc in plan.Dcs)
            {
                if (dc == null || Classify(dc.Name) != ModCat.Tracks || Under(dc.transform, thrown)) continue;
                // from the collider's shape: a wrecked track's collider is off, its live bounds are empty
                var shape = ShapeOf(dc.ComponentCollision ?? dc.GetComponent<Collider>(), w2l);
                if (shape == null || shape.Mesh == null) continue;
                var lb = shape.LocalAabb();
                if (!any) { b = lb; any = true; } else b.Encapsulate(lb);
            }
            if (!any || b.size.x < 1f || b.size.z < 2f) return null;
            b.Encapsulate(new Vector3(b.center.x, b.max.y + 1.3f, b.center.z));   // hull deck above the tracks
            b.Expand(new Vector3(1.2f, 0.6f, 1.2f));
            return b;
        }

        /// where the thrown parts lie now (world boxes of what they draw, a bit bigger)
        static List<Bounds> ThrownBoxes(List<Transform> thrown)
        {
            var boxes = new List<Bounds>();
            foreach (var t in thrown)
            {
                if (t == null) continue;
                bool any = false;
                var b = new Bounds();
                foreach (var r in t.GetComponentsInChildren<Renderer>(false))
                {
                    if (r == null || !r.enabled || !(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                }
                if (!any) continue;
                b.Expand(0.3f);
                boxes.Add(b);
            }
            return boxes;
        }

        static bool Under(Transform t, List<Transform> parts)
        {
            for (; t != null; t = t.parent) if (parts.Contains(t)) return true;
            return false;
        }

        /// how the game shows a renderer, for the logs
        static string Describe(Renderer r)
        {
            if (r == null) return "";
            try
            {
                string path = r.name;
                for (var t = r.transform.parent; t != null && path.Length < 200; t = t.parent) path = t.name + "/" + path;
                var lod = r.GetComponentInParent<LODGroup>();
                return $"; game renderer {path}, {(r is SkinnedMeshRenderer ? "skin" : "mesh")}, enabled {r.enabled}, visible {r.isVisible}, shadows {r.shadowCastingMode}, " +
                    $"forced off {r.forceRenderingOff}, layer {LayerMask.LayerToName(r.gameObject.layer)}, lod group {(lod != null ? lod.name : "none")}, game bounds {r.bounds.center}";
            }
            catch (Exception e) { return "; " + e.Message; }
        }

        /// distance between two boxes, 0 when they touch
        static float GapBetween(Bounds a, Bounds b)
        {
            Vector3 d = Vector3.Max(Vector3.zero, Vector3.Max(a.min - b.max, b.min - a.max));
            return d.magnitude;
        }

        static readonly HashSet<string> _flatLogged = new HashSet<string>();

        /// skewed part (unevenly scaled parent, turned child) a transform can't hold: when the nearest one would put it
        /// more than ~2 cm off, the matrix gets baked into a copy of the mesh (Leopard 2KA3 slats were 1.5 m off). mesh not
        /// readable: a few cm off stays (decals), false when it's way off, better left out than drawn in the wrong spot
        static bool Flatten(GhostSnapshot s, MeshPart p)
        {
            if (p.Mesh == null || Vis.ToTrs(p.M, out _, out _, out _)) return true;
            var src = p.Mesh;
            float err = Vis.TrsError(p.M, src.bounds);
            if (err < 0.02f || (!src.isReadable && err < 0.25f)) return true;
            try
            {
                if (!src.isReadable) throw new Exception("mesh not readable");
                var m = p.M;
                var mesh = new Mesh { name = src.name + " (flat)" };
                if (src.vertexCount > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                var v = src.vertices;
                for (int i = 0; i < v.Length; i++) v[i] = m.MultiplyPoint3x4(v[i]);
                mesh.vertices = v;
                var nrm = src.normals;
                if (nrm.Length == v.Length)
                {
                    var nm = m.inverse.transpose;
                    for (int i = 0; i < nrm.Length; i++) nrm[i] = nm.MultiplyVector(nrm[i]).normalized;
                    mesh.normals = nrm;
                }
                var uv = src.uv;
                if (uv.Length == v.Length) mesh.uv = uv;
                bool flip = m.determinant < 0f;   // mirrored: triangles wind the other way
                mesh.subMeshCount = src.subMeshCount;
                for (int sm = 0; sm < src.subMeshCount; sm++)
                {
                    var tris = src.GetTriangles(sm);
                    if (flip) for (int i = 0; i + 2 < tris.Length; i += 3) { int t = tris[i + 1]; tris[i + 1] = tris[i + 2]; tris[i + 2] = t; }
                    mesh.SetTriangles(tris, sm, false);
                }
                mesh.RecalculateBounds();
                if (p.OwnsMesh) UnityEngine.Object.Destroy(src);
                p.Mesh = mesh;
                p.M = Matrix4x4.identity;
                p.OwnsMesh = true;
                if (_flatLogged.Add(s.UnitName + "/" + p.Name)) HitCamMod.Log.Msg($"X-ray copy of {s.UnitName}: '{p.Name}' sits under an unevenly scaled parent ({err:0.00} m off as it was), its mesh got flattened ({v.Length} vertices)");
                return true;
            }
            catch (Exception e)
            {
                if (_flatLogged.Add(s.UnitName + "/" + p.Name)) HitCamMod.Log.Warning($"X-ray copy of {s.UnitName}: '{p.Name}' sits under an unevenly scaled parent ({err:0.00} m off) and can't be copied ({e.Message}), left out of the x-ray");
                if (p.OwnsMesh && src != null) UnityEngine.Object.Destroy(src);
                return false;
            }
        }

        /// copy drawn away from where the game draws the part (a tester's Leopard 2KA3 had a skirt floating next to the
        /// tank in the Armor Sim): says which part and how far, once per vehicle and part
        static void Misplaced(GhostSnapshot s, Renderer r, MeshPart part, Matrix4x4 w2l)
        {
            try
            {
                var game = r.bounds;
                if (game.size.sqrMagnitude < 1e-6f) return;
                Vector3 want = w2l.MultiplyPoint3x4(game.center);
                var ours = part.LocalAabb();
                float off = (ours.center - want).magnitude;
                if (off < 0.4f + 0.3f * game.size.magnitude) return;
                if (_misplacedLogged.Count > 60 || !_misplacedLogged.Add(s.UnitName + "/" + r.name)) return;
                string kind = r is SkinnedMeshRenderer sk ? $"skin, root bone {(sk.rootBone != null ? sk.rootBone.name : "none")}, {sk.bones?.Length ?? 0} bones" : "mesh";
                string path = r.name;
                for (var t = r.transform.parent; t != null && path.Length < 160; t = t.parent) path = t.name + "/" + path;
                HitCamMod.Log.Warning($"X-ray copy of {s.UnitName}: '{path}' ({kind}) comes out {off:0.00} m from where the game draws it (ours {ours.center}, game {want}, size {game.size})");
            }
            catch { }
        }

        static readonly Dictionary<Unit, List<Transform>> _followerCache = new Dictionary<Unit, List<Transform>>();
        static readonly HashSet<string> _bonelessLogged = new HashSet<string>();
        static bool _loggedOnce;

        /// skinned mesh with bones that are all gone
        static bool Boneless(SkinnedMeshRenderer smr)
        {
            var bones = smr.bones;
            if (bones == null || bones.Length == 0) return false;   // plain skin (blend shapes only): drawn at its own transform
            foreach (var b in bones) if (b != null) return false;
            return true;
        }

        static bool InteriorLook(Material[] mats)
        {
            if (mats == null || mats.Length == 0) return false;
            foreach (var m in mats)
            {
                if (m == null) continue;
                string n = m.name.ToLowerInvariant();
                if (!n.Contains("interior") && !n.EndsWith("_int") && !n.Contains("_int ")) return false;
            }
            return true;
        }

        public static GhostSnapshot Capture(Unit u)
        {
            var t0 = Time.realtimeSinceStartup;
            long lap = Perf.Now;
            var s = new GhostSnapshot { UnitName = Recorder.SafeName(u) };
            Matrix4x4 w2l = u.transform.worldToLocalMatrix;
            var roots = Roots(u);
            var thrown = Thrown(u, roots);
            var thrownBoxes = ThrownBoxes(thrown);
            Bounds? hull = null;
            // a part of the thrown turret: under it, or one of the bits that only follow it (FEP's add-on armour, the
            // game's visual blockers) lying where it lies or anywhere off the hull
            bool Off(Transform t, Vector3 at) => thrown.Count > 0 && (Under(t, thrown) || thrownBoxes.Any(b => b.Contains(at))
                || (hull.HasValue && !hull.Value.Contains(w2l.MultiplyPoint3x4(at))));
            int leftOut = 0, crewOut = 0;

            // --- crew body parts (hit zones) first so modules can skip them
            var crewParts = new HashSet<DC>();
            if (u.CrewManager != null && u.CrewManager.CrewMembers != null)
            {
                foreach (var cm in u.CrewManager.CrewMembers)
                {
                    if (cm == null || cm.Evacuated) continue;
                    var ci = new CrewInfo { Member = cm, Label = CrewLabel(cm.Position) };
                    ci.InitialStatus = ci.Status();
                    var reg = cm.BodyPartRegistry;
                    // wreck that lost its turret: hull only, the dead crew (and whoever went with the turret) left out
                    if (thrown.Count > 0 && (ci.InitialStatus == HealthStatus.Dead || reg?.BodyParts == null
                        || reg.BodyParts.Any(bp => bp?.Component != null && Under(bp.Component.transform, thrown))))
                    {
                        if (reg?.BodyParts != null) foreach (var bp in reg.BodyParts) if (bp?.Component != null) crewParts.Add(bp.Component);
                        crewOut++;
                        continue;
                    }
                    if (reg != null && reg.BodyParts != null)
                    {
                        foreach (var bp in reg.BodyParts)
                        {
                            if (bp?.Component == null) continue;
                            crewParts.Add(bp.Component);
                            s.CrewOfPart[bp.Component] = ci;
                            var shape = ShapeOf(bp.Component.ComponentCollision ?? bp.Component.GetComponent<Collider>(), w2l);
                            if (shape != null)
                            {
                                shape.Name = ci.Label + " " + bp.Part;
                                if (shape.Sane()) ci.AddPart(shape, bp.Part, bp.Component); else Reject(s, shape);
                            }
                        }
                    }
                    if (Settings.DetailedCrew.Value) CaptureCrewModel(cm, ci, w2l);
                    s.Crew.Add(ci);
                }
            }

            Perf.Lap("Snapshot/crew", ref lap);
            // --- what to copy was found ahead (Warmup) for the unit you aim at, here we only read their current transforms
            var plan = PlanOf(u, roots);
            if (thrown.Count > 0) hull = HullBox(plan, thrown, w2l);
            Perf.Lap("Snapshot/plan", ref lap);
            foreach (var (r, ammoVis, inner) in plan.Renderers)
            {
                if (r == null || !r.gameObject.activeInHierarchy) continue;
                if (!r.enabled && !plan.Lod0.Contains(r)) continue;
                if (Off(r.transform, r.bounds.center)) { leftOut++; continue; }
                var part = MeshOf(r, w2l);
                if (part == null) continue;
                part.Name = r.name;
                part.Mats = r.sharedMaterials;
                part.Src = r;
                if (!part.Sane()) { Reject(s, part); continue; }
                Misplaced(s, r, part, w2l);
                if (!Flatten(s, part)) continue;
                if (ammoVis != null)
                {
                    var ap = s.Ammo.FirstOrDefault(a => a.Visual == ammoVis);
                    if (ap == null) { ap = new AmmoPart { Visual = ammoVis }; s.Ammo.Add(ap); }
                    ap.Shapes.Add(part);
                }
                else
                {
                    // T-72M1 and M1IP have a painted interior (hull walls, turret lining, fire wall). skinned with its own
                    // white paint it covered the outside of the tank in the replay
                    if (inner) { part.Mats = null; part.Inner = true; }
                    s.Exterior.Add(part);
                }
            }
            Perf.Lap("Snapshot/renderers", ref lap);

            // rack rounds: game's own detailed AAR models (projectile + charge) instead of the plain hit-zone shapes
            // the ammo visuals carry
            foreach (var (av, rs) in plan.AmmoModels)
            {
                if (av == null || !av.gameObject.activeInHierarchy) continue;
                if (Off(av.transform, av.transform.position)) continue;
                var shapes = new List<MeshPart>();
                foreach (var r in rs)
                {
                    if (r == null) continue;
                    var part = MeshOf(r, w2l);
                    if (part == null || !part.Sane()) continue;
                    part.Name = r.name;
                    part.Mats = r.sharedMaterials;
                    if (!Flatten(s, part)) continue;
                    shapes.Add(part);
                }
                if (AutoTest.Active && _aarAmmo < 3)
                    HitCamMod.Log.Msg($"  ammo visual {av.name}: AAR shapes " + string.Join(", ", shapes.Select(sp => sp.Name + "/" + sp.Mesh.vertexCount + "v")));
                if (shapes.Count == 0) continue;
                var ap = s.Ammo.FirstOrDefault(a => a.Visual == av);
                if (ap == null) { ap = new AmmoPart { Visual = av }; s.Ammo.Add(ap); }
                ap.Shapes.Clear();
                ap.Shapes.AddRange(shapes);
                _aarAmmo++;
            }
            Perf.Lap("Snapshot/ammo", ref lap);

            var seenDC = new HashSet<DC>();
            foreach (var dc in plan.Dcs)
            {
                if (dc == null || !dc.gameObject.activeInHierarchy || dc.Hidden || crewParts.Contains(dc) || !seenDC.Add(dc)) continue;
                var dcCol = dc.ComponentCollision ?? dc.GetComponent<Collider>();
                if (Off(dc.transform, dcCol != null && dcCol.enabled && dcCol.gameObject.activeInHierarchy ? dcCol.bounds.center : dc.transform.position)) { leftOut++; continue; }
                if (CrewPartName(dc.Name, out string who, out BodyPart bpart))
                {
                    var owner = s.Crew.FirstOrDefault(c => c.Label.Equals(who, StringComparison.OrdinalIgnoreCase));
                    if (owner != null) s.CrewOfPart[dc] = owner;
                    continue;
                }
                var mp = new ModulePart { Comp = dc, Name = dc.Name, Cat = Classify(dc.Name), InitialHealth = dc.HealthPercent };
                var col = dc.ComponentCollision ?? dc.GetComponent<Collider>();
                var shape = ShapeOf(col, w2l);
                if (shape != null)
                {
                    shape.Name = dc.Name;
                    if (shape.Sane()) mp.Shapes.Add(shape); else Reject(s, shape);
                }
                s.Modules.Add(mp);
                s.ModuleOf[dc] = mp;
            }
            Perf.Lap("Snapshot/modules", ref lap);

            // GHPC's own x-ray models (AarVisual, what its AAR replay draws): detailed modules, every round in the racks
            // (case, dart, sabot petals...) and the interior fittings. crew and armour have their own
            var crewAv = new HashSet<AarVisual>();
            foreach (var ci in s.Crew)
                try { if (ci.Member?.AllAarVisuals != null) foreach (var av in ci.Member.AllAarVisuals) if (av != null) crewAv.Add(av); } catch { }
            // ammo modules' boxes, once (every rack round gets matched against them)
            var ammoBoxes = new List<(ModulePart mp, Bounds b)>();
            foreach (var mp in s.Modules) if (mp.Cat == ModCat.Ammo) foreach (var sh in mp.Shapes) ammoBoxes.Add((mp, sh.LocalAabb()));
            int rIn = 0, rCrew = 0, rOff = 0, rHidden = 0, rNoShape = 0;
            foreach (var (av, rs, round, dcAv) in plan.Aar)
            {
                if (round) rIn++;
                if (av == null || crewAv.Contains(av)) { if (round) rCrew++; continue; }
                // whatever the vehicle (or a mod reworking it, e.g. a KWS III mod drops the loader and his rack) switched off
                // or hid isn't there: only the x-ray models the game itself would show
                if (!av.isActiveAndEnabled || av.Hidden)
                {
                    if (round) { if (av.Hidden) rHidden++; else rOff++; }
                    if (round && AutoTest.Active && _roundDiag.Add(s.UnitName + "/" + av.name))
                        HitCamMod.Log.Msg($"  round model '{av.name}': active {av.gameObject.activeInHierarchy}, enabled {av.enabled}, hidden {av.Hidden}, renderers on {rs.Count(r => r != null && r.enabled && r.gameObject.activeInHierarchy)}/{rs.Count}");
                    continue;
                }
                if (dcAv != null && (!dcAv.gameObject.activeInHierarchy || dcAv.Hidden)) continue;
                if (Off(av.transform, av.transform.position)) continue;
                var shapes = new List<MeshPart>();
                foreach (var r in rs)
                {
                    if (r == null) continue;
                    var part = MeshOf(r, w2l);
                    if (part == null || !part.Sane()) continue;
                    part.Name = r.name;
                    part.Mats = r.sharedMaterials;
                    if (!round) Misplaced(s, r, part, w2l);
                    if (!Flatten(s, part)) continue;
                    shapes.Add(part);
                }
                if (shapes.Count == 0) { if (round) rNoShape++; continue; }
                if (round)
                {
                    s.Rounds++;
                    var mp = NearestAmmoModule(ammoBoxes, shapes);
                    if (mp != null) mp.Detail.AddRange(shapes);
                    else { var ap = new AmmoPart(); ap.Shapes.AddRange(shapes); s.Ammo.Add(ap); }
                    continue;
                }
                if (dcAv != null && s.ModuleOf.TryGetValue(dcAv, out var mod)) { mod.Detail.AddRange(shapes); continue; }
                s.Fixtures.AddRange(shapes);
            }

            if (AutoTest.Active && rIn > 0 && _roundDiag.Add(s.UnitName))
                HitCamMod.Log.Msg($"  rounds of {s.UnitName}: {rIn} round models, {s.Rounds} used; skipped: crew {rCrew}, switched off {rOff}, hidden {rHidden}, no shape {rNoShape}");
            Perf.Lap("Snapshot/aar", ref lap);
            foreach (var ci in s.Crew) ci.BuildSkeleton();

            // --- the armour itself, for the x-ray physics walls
            try
            {
                foreach (var c in WorldDamage.HullColliders(u).set)
                {
                    if (c == null || Off(c.transform, c.enabled && c.gameObject.activeInHierarchy ? c.bounds.center : c.transform.position)) continue;
                    Matrix4x4 m = w2l * c.transform.localToWorldMatrix;
                    if (c is MeshCollider mc && mc.sharedMesh != null && mc.sharedMesh.isReadable) s.ArmourMeshes.Add((mc.sharedMesh, m, mc.convex));
                    else if (c is BoxCollider bc) s.ArmourBoxes.Add((bc.center, bc.size, m));
                }
            }
            catch (Exception e) { HitCamMod.Log.Warning("snapshot armour: " + e.Message); }
            Perf.Lap("Snapshot/armour", ref lap);

            // --- bounds from the exterior + every hit zone (those are always inside the vehicle)
            bool first = true;
            Bounds b = new Bounds();
            var all = new List<MeshPart>();
            foreach (var mp in s.Modules) all.AddRange(mp.Shapes);
            foreach (var ci in s.Crew) all.AddRange(ci.Shapes);
            if (all.Count < 3) all.AddRange(s.Exterior);
            foreach (var p in all)
            {
                var pb = p.LocalAabb();
                if (first) { b = pb; first = false; }
                else b.Encapsulate(pb);
            }
            if (first) b = new Bounds(new Vector3(0, 1.2f, 0), new Vector3(3.5f, 2.4f, 7f));
            else b.Expand(0.5f);   // hit zones sit inside the hull, grow a bit to cover the plates
            s.LocalBounds = b;
            if (thrown.Count > 0 && AutoTest.Active)
                HitCamMod.Log.Msg($"  wreck copy of {s.UnitName}: {thrown.Count} thrown, {thrownBoxes.Count} boxes, hull box {(hull.HasValue ? hull.Value.center + " " + hull.Value.size : "none")}, {leftOut} parts left out");
            if (thrown.Count > 0 && _wreckLogged.Add(s.UnitName))
            {
                HitCamMod.Log.Msg($"X-ray copy of {s.UnitName}: the game threw off its {string.Join(", ", thrown.Select(t => PathOf(t)))}, the copy is the hull ({leftOut} parts and {crewOut} dead crew left out)");
                if (AutoTest.Active)
                    foreach (var (r, _, _) in plan.Renderers.Where(x => x.r != null && (x.r.bounds.center - u.transform.position).magnitude > 5f).Take(6))
                        HitCamMod.Log.Msg($"  wreck: '{PathOf(r.transform)}' {(r.bounds.center - u.transform.position).magnitude:0.0} m off, under the thrown part: {Under(r.transform, thrown)}");
            }
            // self-test: bits floating clear of the vehicle (barrels and antennas stick out but still touch it)
            if (AutoTest.Active)
                foreach (var p in s.Exterior.Concat(s.Fixtures).Concat(s.Modules.SelectMany(m => m.Detail)))
                {
                    var pb = p.LocalAabb();
                    float gap = GapBetween(pb, b);
                    if (gap > 0.3f && _misplacedLogged.Add($"{s.UnitName}/{p.Name}/{pb.center.z:0.0}"))
                        HitCamMod.Log.Msg($"  x-ray copy of {s.UnitName}: '{p.Name}' floats {gap:0.00} m clear of the vehicle (center {pb.center}, size {pb.size}, vehicle box {b.center} {b.size}){Describe(p.Src)}");
                }

            Perf.Lap("Snapshot/armour+bounds", ref lap);
            float ms = (Time.realtimeSinceStartup - t0) * 1000f;
            if (AutoTest.Active && _aarLogged.Add(s.UnitName))
                foreach (var root in roots)
                    foreach (var av in root.GetComponentsInChildren<AarVisual>(true))
                    {
                        var rs = av.SpecificRenderers != null && av.SpecificRenderers.Length > 0 ? av.SpecificRenderers : av.GetComponentsInChildren<Renderer>(true);
                        var dc = av.GetComponentInParent<DC>() ?? av.GetComponentInChildren<DC>(true);
                        HitCamMod.Log.Msg($"  AarVisual '{av.name}' mode {av.RenderMode} dc '{dc?.Name}' renderers: " + string.Join(", ", rs.Where(r => r != null).Take(4).Select(r =>
                            r.name + "/" + (r is SkinnedMeshRenderer sk ? (sk.sharedMesh != null ? sk.sharedMesh.vertexCount + "v skin" : "-") : r.GetComponent<MeshFilter>()?.sharedMesh != null ? r.GetComponent<MeshFilter>().sharedMesh.vertexCount + "v" : "-"))));
                    }
            HitCamMod.Log.Msg($"Snapshot {s.UnitName}: {s.Exterior.Count} hull meshes, {s.Modules.Count} modules, {s.Crew.Count} crew, {s.Ammo.Count} rounds, armour {s.ArmourMeshes.Count} meshes + {s.ArmourBoxes.Count} boxes, bounds {b.size}, {ms:0.0} ms" + (s.Rejected.Count > 0 ? ", dropped: " + string.Join(", ", s.Rejected.Take(6)) : ""));
            if (plan.Boneless.Count > 0 && (AutoTest.Active || _bonelessLogged.Add(s.UnitName)))
                HitCamMod.Log.Msg($"  {s.UnitName}: skipped {plan.Boneless.Count} skins whose bones are gone (the game draws them at the map origin, not on the vehicle): " + string.Join(", ", plan.Boneless));
            if (!_loggedOnce || AutoTest.Active)
            {
                _loggedOnce = true;
                HitCamMod.Log.Msg("  modules: " + string.Join(", ", s.Modules.Select(m => m.Name + "[" + m.Cat + (m.Shapes.Count == 0 ? ",noshape" : "") + "]")));
                HitCamMod.Log.Msg("  crew: " + string.Join(", ", s.Crew.Select(c => c.Label + "(" + c.Shapes.Count + " parts)")));
            }
            return s;
        }

        static bool _crewLogged;

        static List<Renderer> CrewRenderers(CrewManager.CrewMember cm)
        {
            var seen = new HashSet<Renderer>();
            var rs = new List<Renderer>();
            if (cm.AllAarVisuals != null)
                foreach (var av in cm.AllAarVisuals)
                    if (av != null) foreach (var r in av.GetComponentsInChildren<Renderer>(true)) if (seen.Add(r)) rs.Add(r);
            var rig = cm.AutoBoneRig;
            if (rig != null && rig.CharacterRoot != null)
                foreach (var r in rig.CharacterRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (seen.Add(r)) rs.Add(r);
            return rs;
        }

        static bool IsHitZone(Renderer r)
        {
            string rn = r.name.ToLowerInvariant();
            return rn.Contains("hitzone") || rn.Contains("hit zone") || rn.Contains("collider");
        }

        static void CaptureCrewModel(CrewManager.CrewMember cm, CrewInfo ci, Matrix4x4 w2l)
        {
            foreach (var r in CrewRenderers(cm))
            {
                if (!(r is SkinnedMeshRenderer) && !(r is MeshRenderer)) continue;
                if (IsHitZone(r)) continue;
                var part = MeshOf(r, w2l, forceBake: true);
                if (part == null || part.Mesh == null || !part.Sane()) continue;
                part.Name = r.name;
                ci.Visuals.Add(part);
            }
            if (!_crewLogged || AutoTest.Active)
            {
                _crewLogged = true;
                HitCamMod.Log.Msg($"  crew model {ci.Label}: " + (ci.Visuals.Count == 0 ? "none (capsules)" : string.Join(", ", ci.Visuals.Select(v => v.Name + "/" + v.Mesh.vertexCount + "v" + (v.Mesh.isReadable ? "" : "(unreadable)")))));
            }
        }

        static readonly Dictionary<Unit, List<AarVisual>> _aarOf = new Dictionary<Unit, List<AarVisual>>();

        /// game's own x-ray models (AarVisual) of a unit that the snapshot uses: not under an armour piece, not another
        /// unit's. searched once per unit (they don't come and go)
        internal static List<AarVisual> AarVisualsOf(Unit u)
        {
            if (_aarOf.TryGetValue(u, out var list) && !list.Any(a => a == null)) return list;
            list = new List<AarVisual>();
            foreach (var root in Roots(u))
                foreach (var av in root.GetComponentsInChildren<AarVisual>(true))
                {
                    if (av == null || av.GetComponentInParent<IArmor>() != null) continue;
                    if (av.GetComponentInParent<Unit>() is Unit owner && owner != u) continue;
                    list.Add(av);
                }
            _aarOf[u] = list;
            return list;
        }

        /// the unit + objects that follow it without being its children (drawn hull, turret...): game's own lists of them
        /// (LateFollowTarget.LateFollowers, on the unit's moving parts). unit without those lists gets a scene scan for
        /// followers (10-30 ms in a big mission), one scan per unit
        internal static List<Transform> Roots(Unit u)
        {
            if (_followerCache.TryGetValue(u, out var list) && !list.Any(t => t == null)) return list;
            list = new List<Transform> { u.transform };
            var targets = u.GetComponentsInChildren<LateFollowTarget>(true);
            foreach (var tg in targets)
            {
                var fl = tg != null ? tg.LateFollowers : null;
                if (fl == null) continue;
                for (int i = 0; i < fl.Count; i++)
                {
                    var lf = fl[i];
                    if (lf != null && !lf.transform.IsChildOf(u.transform) && !list.Contains(lf.transform)) list.Add(lf.transform);
                }
            }
            if (targets.Length == 0 || (AutoTest.Active && _rootsChecked.Add(Recorder.SafeName(u))))
            {
                // no lists (or the self-test checking them once per vehicle type): scene scan
                if (_scanFrame != Time.frameCount) ScanFollowers();
                if (_scanned.TryGetValue(u, out var found))
                {
                    if (targets.Length == 0) list = found;
                    else if (found.Count != list.Count || found.Any(t => !list.Contains(t)))
                        HitCamMod.Log.Warning($"Followers of {Recorder.SafeName(u)}: the game's lists give {list.Count - 1}, the scene scan {found.Count - 1}");
                    else HitCamMod.Log.Msg($"Followers of {Recorder.SafeName(u)}: the game's lists and the scene scan agree ({list.Count - 1})");
                }
            }
            _followerCache[u] = list;
            return list;
        }

        static readonly HashSet<string> _rootsChecked = new HashSet<string>();
        static readonly Dictionary<Unit, List<Transform>> _scanned = new Dictionary<Unit, List<Transform>>();

        static int _scanFrame = -1;

        static void ScanFollowers()
        {
            _scanFrame = Time.frameCount;
            _scanned.Clear();
            foreach (var lf in UnityEngine.Object.FindObjectsOfType<LateFollow>(true))
            {
                var pu = lf != null ? lf.ParentUnit as Unit : null;
                if (pu == null || lf.transform.IsChildOf(pu.transform)) continue;
                if (!_scanned.TryGetValue(pu, out var l)) _scanned[pu] = l = new List<Transform> { pu.transform };
                l.Add(lf.transform);
            }
        }

        public static void ClearCache()
        {
            _followerCache.Clear();
            _scanned.Clear();
            _aarOf.Clear();
            _plans.Clear();
            foreach (var b in _bakes.Values) if (b.Mesh != null) UnityEngine.Object.Destroy(b.Mesh);
            _bakes.Clear();
        }

        /// what was found on one unit, dropped: another mod rebuilt it (new parts, armour, models) so look again
        public static void Forget(Unit u)
        {
            if (u == null) return;
            _followerCache.Remove(u);
            _scanned.Remove(u);
            _aarOf.Remove(u);
            _plans.Remove(u);
        }

        // ------------------------------------------------------------------ crew bakes made ahead (Warmup)

        /// crew model baked before the hit in the pose it had then (bones in the renderer's space). next snapshot
        /// takes it over (and owns it from then on) if the pose is still the same
        class Bake { public Mesh Mesh; public bool NoScale; public Vector3[] Pose; }
        static readonly Dictionary<SkinnedMeshRenderer, Bake> _bakes = new Dictionary<SkinnedMeshRenderer, Bake>();

        static Vector3[] PoseOf(SkinnedMeshRenderer smr)
        {
            var bones = smr.bones;
            var inv = smr.transform.worldToLocalMatrix;
            var p = new Vector3[bones.Length];
            for (int i = 0; i < bones.Length; i++) p[i] = bones[i] != null ? inv.MultiplyPoint3x4(bones[i].position) : Vector3.zero;
            return p;
        }

        static bool SamePose(Vector3[] a, Vector3[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if ((a[i] - b[i]).sqrMagnitude > 1e-6f) return false;   // within a mm
            return true;
        }

        /// crew model renderers of a unit (what CaptureCrewModel bakes)
        internal static List<SkinnedMeshRenderer> CrewSkins(Unit u)
        {
            var list = new List<SkinnedMeshRenderer>();
            if (u == null || u.CrewManager == null || u.CrewManager.CrewMembers == null) return list;
            foreach (var cm in u.CrewManager.CrewMembers)
                if (cm != null && !cm.Evacuated)
                    foreach (var r in CrewRenderers(cm))
                        if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null && !IsHitZone(r)) list.Add(smr);
            return list;
        }

        /// bake a crew model now, unless an untaken bake in the same pose is already waiting
        internal static void Prebake(SkinnedMeshRenderer smr)
        {
            if (smr == null || smr.sharedMesh == null) return;
            var pose = PoseOf(smr);
            if (_bakes.TryGetValue(smr, out var have) && have.Mesh != null && SamePose(have.Pose, pose)) return;
            if (have != null && have.Mesh != null) UnityEngine.Object.Destroy(have.Mesh);    // never handed out, nobody else holds it
            _bakes.Remove(smr);
            var baked = new Mesh();
            smr.BakeMesh(baked, false);
            if (baked.vertexCount == 0) { UnityEngine.Object.Destroy(baked); return; }
            baked.RecalculateBounds();
            _bakes[smr] = new Bake { Mesh = baked, Pose = pose, NoScale = WantsNoScale(smr, baked) };
            if (_bakes.Count > 120) { foreach (var b in _bakes.Values) if (b.Mesh != null) UnityEngine.Object.Destroy(b.Mesh); _bakes.Clear(); }
        }

        /// bake made ahead for this renderer, if its pose still holds: handed over (caller owns it now)
        static Bake TakeBake(SkinnedMeshRenderer smr)
        {
            if (!_bakes.TryGetValue(smr, out var b)) return null;
            _bakes.Remove(smr);
            if (b.Mesh != null && SamePose(b.Pose, PoseOf(smr))) return b;
            if (b.Mesh != null) UnityEngine.Object.Destroy(b.Mesh);
            return null;
        }

        /// most parts have scale 1 and both readings agree, scaled ones come out too big/small with the scale applied twice
        /// (T-72M1 hull / turret skins 10x, BMP-2 turret 0.77x, T-80B NSV 0.92x): pose matching the renderer's own world bounds wins
        static bool WantsNoScale(SkinnedMeshRenderer smr, Mesh baked)
        {
            float want = smr.bounds.size.magnitude;
            if (want <= 0.05f) return false;
            var tr = smr.transform;
            var noScale = Matrix4x4.TRS(tr.position, tr.rotation, Vector3.one);
            float ea = Mathf.Abs(Mathf.Log(Mathf.Max(SizeUnder(baked.bounds, tr.localToWorldMatrix), 1e-4f) / want));
            float eb = Mathf.Abs(Mathf.Log(Mathf.Max(SizeUnder(baked.bounds, noScale), 1e-4f) / want));
            return ea > 0.05f && eb < ea;
        }

        /// ammo module (warhead / propellant / round hit zone) a round's model lies in, so it takes that module's state
        /// (hit: red, cooked off: dark)
        static ModulePart NearestAmmoModule(List<(ModulePart mp, Bounds b)> boxes, List<MeshPart> shapes)
        {
            Bounds rb = shapes[0].LocalAabb();
            foreach (var sp in shapes) rb.Encapsulate(sp.LocalAabb());
            ModulePart best = null;
            float bestScore = 0f;
            foreach (var (mp, hb) in boxes)
            {
                if (!hb.Intersects(rb)) continue;
                Vector3 lo = Vector3.Max(hb.min, rb.min), hi = Vector3.Min(hb.max, rb.max), sz = hi - lo;
                float score = Mathf.Max(sz.x, 0f) * Mathf.Max(sz.y, 0f) * Mathf.Max(sz.z, 0f);
                if (score > bestScore) { bestScore = score; best = mp; }
            }
            return best;
        }

        // ------------------------------------------------------------------ the snapshot plan, found ahead

        /// what a snapshot of a unit copies: drawn parts (with the filters that don't change: LOD, layers, shaders, skins
        /// without bones, which rack they belong to), the racks' x-ray models, modules and the game's x-ray models.
        /// searching the vehicle for them (components, parents of each renderer) was most of the snapshot cost on the hit
        /// frame, so Warmup finds them while you aim and the hit only reads where they are now
        internal class SnapPlan
        {
            public int Roots;
            public readonly HashSet<Renderer> Lod0 = new HashSet<Renderer>();
            public readonly List<(Renderer r, AmmoVisual ammo, bool inner)> Renderers = new List<(Renderer, AmmoVisual, bool)>();
            public readonly List<string> Boneless = new List<string>();
            public readonly List<(AmmoVisual av, List<Renderer> rs)> AmmoModels = new List<(AmmoVisual, List<Renderer>)>();
            public readonly List<DC> Dcs = new List<DC>();
            public readonly List<(AarVisual av, List<Renderer> rs, bool round, DC dc)> Aar = new List<(AarVisual, List<Renderer>, bool, DC)>();
        }
        static readonly Dictionary<Unit, SnapPlan> _plans = new Dictionary<Unit, SnapPlan>();

        internal static bool HasPlan(Unit u) => u != null && _plans.TryGetValue(u, out var p) && p.Roots == Roots(u).Count;

        internal static SnapPlan PlanOf(Unit u, List<Transform> roots = null)
        {
            roots = roots ?? Roots(u);
            if (_plans.TryGetValue(u, out var have) && have.Roots == roots.Count) return have;
            var p = new SnapPlan { Roots = roots.Count };
            var lodExclude = new HashSet<Renderer>();
            foreach (var root in roots)
                foreach (var lg in root.GetComponentsInChildren<LODGroup>(true))
                {
                    var lods = lg.GetLODs();
                    for (int i = 0; i < lods.Length; i++)
                        foreach (var r in lods[i].renderers)
                            if (r != null) { if (i == 0) p.Lod0.Add(r); else lodExclude.Add(r); }
                }
            foreach (var root in roots)
            {
                // inactive ones too (hatch, light): whether each is shown gets read at the hit
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || lodExclude.Contains(r)) continue;
                    if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                    if (r.name == WorldDamage.InsideName || r.name == WorldDamage.CharredName) continue;   // our own
                    int layer = r.gameObject.layer;
                    if (layer == 15 || layer == 21) continue; // game's own AAR / x-ray-only layers
                    string ln = LayerMask.LayerToName(layer) ?? "";
                    if (ln.Contains("Thermal") || ln.Contains("Raycast") || ln.Contains("Vision") || ln.Contains("Visibility")) continue;
                    if (r.GetComponentInParent<AarVisual>(true) != null || r.GetComponentInParent<AarModel>(true) != null) continue;
                    var mat = r.sharedMaterial;
                    string sh = mat != null && mat.shader != null ? mat.shader.name : "";
                    if (sh.IndexOf("Decal", StringComparison.OrdinalIgnoreCase) >= 0 || sh.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    // camo nets: cloth, not the vehicle (their packed mask texture drew as a purple rag over the gun)
                    if (sh.IndexOf("CamoNet", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    // skin with all bones gone gets identity bones, i.e. drawn around the map origin: Leopard 2A3 has a leftover
                    // M1 track pair like that (abrams_tracks/TRACK_L/R) next to its own LEO2_tracks
                    if (r is SkinnedMeshRenderer sk && Boneless(sk)) { p.Boneless.Add(r.name); continue; }
                    p.Renderers.Add((r, r.GetComponentInParent<AmmoVisual>(true), InteriorLook(r.sharedMaterials)));
                }
                foreach (var av in root.GetComponentsInChildren<AmmoVisual>(true))
                {
                    var models = new List<AarModel>();
                    try
                    {
                        foreach (var am in new[] { av.GetAarModel(), av.GetChargeAarModel(), av.VisualModel, av.ChargeVisualModel })
                            if (am != null && !models.Contains(am)) models.Add(am);
                    }
                    catch { }
                    var rs = new List<Renderer>();
                    foreach (var am in models)
                        foreach (var r in am.GetComponentsInChildren<Renderer>(true))
                            if (r is MeshRenderer || r is SkinnedMeshRenderer) rs.Add(r);
                    if (rs.Count > 0) p.AmmoModels.Add((av, rs));
                }
                foreach (var dc in root.GetComponentsInChildren<DC>(true))
                    if (dc != null && (dc.Unit == null || dc.Unit == u)) p.Dcs.Add(dc);
            }
            foreach (var av in AarVisualsOf(u))
            {
                if (av == null) continue;
                bool specific = av.SpecificRenderers != null && av.SpecificRenderers.Length > 0;
                var rs = new List<Renderer>();
                foreach (var r in specific ? av.SpecificRenderers : av.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer))) continue;
                    if (!specific && r.GetComponentInParent<AarVisual>(true) != av) continue;   // a nested model of its own
                    rs.Add(r);
                }
                if (rs.Count == 0) continue;
                string an = av.name.ToLowerInvariant();
                bool round = av.RenderMode == AarVisual.AarVisualRenderMode.XrayOnly && !an.Contains("door") && !an.Contains("cover") && !an.Contains("hatch");
                p.Aar.Add((av, rs, round, round ? null : av.GetComponentInParent<DC>(true) ?? av.GetComponentInChildren<DC>(true)));
            }
            _plans[u] = p;
            return p;
        }

        static int _aarAmmo;
        public static int BakesReused;
        static readonly HashSet<string> _aarLogged = new HashSet<string>();
        public static int AarAmmoCount => _aarAmmo;

        static MeshPart MeshOf(Renderer r, Matrix4x4 w2l, bool forceBake = false)
        {
            if (r is SkinnedMeshRenderer smr)
            {
                if (smr.sharedMesh == null) return null;
                if (forceBake)
                {
                    var ahead = TakeBake(smr);
                    if (ahead != null)
                    {
                        var tr0 = smr.transform;
                        var m0 = ahead.NoScale ? Matrix4x4.TRS(tr0.position, tr0.rotation, Vector3.one) : tr0.localToWorldMatrix;
                        BakesReused++;
                        return new MeshPart { Mesh = ahead.Mesh, M = w2l * m0, OwnsMesh = true };
                    }
                }
                try
                {
                    var baked = new Mesh();
                    smr.BakeMesh(baked, false);   // renderer's own scale stays in the vertices (useScale: false)
                    if (baked.vertexCount > 0)
                    {
                        var tr = smr.transform;
                        Matrix4x4 m = tr.localToWorldMatrix;
                        // most parts have scale 1 and both readings agree, scaled ones come out too big/small with the scale applied twice
                        // (T-72M1 hull / turret skins 10x, BMP-2 turret 0.77x, T-80B NSV 0.92x): keep the pose matching the renderer's own world bounds
                        float want = smr.bounds.size.magnitude;
                        if (want > 0.05f)
                        {
                            baked.RecalculateBounds();
                            var noScale = Matrix4x4.TRS(tr.position, tr.rotation, Vector3.one);
                            float ea = Mathf.Abs(Mathf.Log(Mathf.Max(SizeUnder(baked.bounds, m), 1e-4f) / want));
                            float eb = Mathf.Abs(Mathf.Log(Mathf.Max(SizeUnder(baked.bounds, noScale), 1e-4f) / want));
                            if (ea > 0.05f && eb < ea) m = noScale;
                        }
                        return new MeshPart { Mesh = baked, M = w2l * m, OwnsMesh = true };
                    }
                    UnityEngine.Object.Destroy(baked);
                }
                catch { }
                return new MeshPart { Mesh = smr.sharedMesh, M = w2l * smr.transform.localToWorldMatrix };
            }
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return null;
            return new MeshPart { Mesh = mf.sharedMesh, M = w2l * r.transform.localToWorldMatrix };
        }

        /// world size (diagonal) of a local box under a matrix
        static float SizeUnder(Bounds lb, Matrix4x4 m)
        {
            var b = new Bounds(m.MultiplyPoint3x4(lb.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                b.Encapsulate(m.MultiplyPoint3x4(lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
            return b.size.magnitude;
        }

        internal static MeshPart ShapeOf(Collider col, Matrix4x4 w2l)
        {
            if (col == null) return null;
            var t = col.transform;
            switch (col)
            {
                case BoxCollider box:
                    return new MeshPart { Mesh = Prims.Cube, M = w2l * t.localToWorldMatrix * Matrix4x4.TRS(box.center, Quaternion.identity, box.size) };
                case SphereCollider sph:
                    return new MeshPart { Mesh = Prims.Sphere, M = w2l * t.localToWorldMatrix * Matrix4x4.TRS(sph.center, Quaternion.identity, Vector3.one * sph.radius * 2f) };
                case CapsuleCollider cap:
                {
                    Quaternion rot = cap.direction == 0 ? Quaternion.Euler(0, 0, 90) : cap.direction == 2 ? Quaternion.Euler(90, 0, 0) : Quaternion.identity;
                    float d = cap.radius * 2f;
                    return new MeshPart { Mesh = Prims.Capsule, M = w2l * t.localToWorldMatrix * Matrix4x4.TRS(cap.center, rot, new Vector3(d, Mathf.Max(cap.height, d) * 0.5f, d)) };
                }
                case MeshCollider mc:
                    if (mc.sharedMesh == null) return null;
                    return new MeshPart { Mesh = mc.sharedMesh, M = w2l * t.localToWorldMatrix };
            }
            return null;
        }

        static readonly string[] CrewWords = { "commander's ", "gunner's ", "driver's ", "loader's ", "operator's ", "crewman's " };
        static readonly (string word, BodyPart part)[] BodyWords =
        {
            ("brain", BodyPart.Brain), ("head", BodyPart.Head), ("neck", BodyPart.Neck), ("upper spine", BodyPart.UpperSpine),
            ("lower spine", BodyPart.LowerSpine), ("spine", BodyPart.UpperSpine), ("torso", BodyPart.Torso), ("chest", BodyPart.Torso),
            ("left hand", BodyPart.LeftHand), ("right hand", BodyPart.RightHand), ("left arm", BodyPart.LeftArm), ("right arm", BodyPart.RightArm),
            ("left foot", BodyPart.LeftFoot), ("right foot", BodyPart.RightFoot), ("left leg", BodyPart.LeftLeg), ("right leg", BodyPart.RightLeg),
            ("heart", BodyPart.Heart), ("lung", BodyPart.LeftLung), ("gut", BodyPart.Gut), ("eye", BodyPart.LeftEye),
        };

        /// "gunner's left arm" -> ("Gunner", LeftArm)
        internal static bool CrewPartName(string name, out string who, out BodyPart part)
        {
            who = null; part = BodyPart.Torso;
            string n = (name ?? "").ToLowerInvariant();
            foreach (var cw in CrewWords)
            {
                if (!n.StartsWith(cw)) continue;
                foreach (var (word, bp) in BodyWords)
                    if (n.Contains(word)) { who = char.ToUpper(cw[0]) + cw.Substring(1, cw.Length - 4); part = bp; return true; }
            }
            return false;
        }

        internal static ModCat Classify(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (n.Contains("engine") || n.Contains("motor")) return ModCat.Engine;
            if (n.Contains("transmission") || n.Contains("gearbox") || n.Contains("final drive")) return ModCat.Transmission;
            if (n.Contains("radiator")) return ModCat.Radiator;
            if (n.Contains("track") || n.Contains("wheel") || n.Contains("sprocket") || n.Contains("idler") || n.Contains("tyre") || n.Contains("tire") || n.Contains("suspension") || n.Contains("torsion bar")) return ModCat.Tracks;
            if (n.Contains("fuel")) return ModCat.Fuel;
            if (n.Contains("ammo") || n.Contains("ammunition") || n.Contains("rack") || n.Contains("propellant") || n.Contains("charge") || n.Contains("carousel") || n.Contains("warhead") || n.Contains("projectile") || n.Contains("round")) return ModCat.Ammo;
            if (n.Contains("optic") || n.Contains("sight") || n.Contains("fire control") || n.Contains("fcs") || n.Contains("periscope") || n.Contains("rangefinder") || n.Contains("laser") || n.Contains("thermal") || n.Contains("computer")) return ModCat.Optics;
            if (n.Contains("breech") || n.Contains("barrel") || n.Contains("gun") || n.Contains("cannon") || n.Contains("autoloader") || n.Contains("launcher") || n.Contains("weapon") || n.Contains("coax")) return ModCat.Gun;
            if (n.Contains("turret") || n.Contains("traverse") || n.Contains("elevation") || (n.Contains("drive") && !n.Contains("driver")) || n.Contains("ring") || n.Contains("hydraulic")) return ModCat.TurretDrive;
            if (n.Contains("radio")) return ModCat.Radio;
            return ModCat.Other;
        }

        internal static string CrewLabel(CrewPosition p)
        {
            try
            {
                switch (p)
                {
                    case CrewPosition.Commander: return "Commander";
                    case CrewPosition.Driver: return "Driver";
                    case CrewPosition.Gunner: return "Gunner";
                    case CrewPosition.Loader: return "Loader";
                }
                return p.ToString();
            }
            catch { return "Crew"; }
        }

        public void Dispose()
        {
            foreach (var p in Exterior.Concat(Fixtures).Concat(Modules.SelectMany(m => m.Detail)))
                if (p.OwnsMesh && p.Mesh != null) UnityEngine.Object.Destroy(p.Mesh);
        }
    }

    /// unity primitive meshes, borrowed once
    internal static class Prims
    {
        static Mesh _cube, _sphere, _capsule, _cylinder, _quad;
        public static Mesh Cube => _cube ??= Get(PrimitiveType.Cube);
        public static Mesh Sphere => _sphere ??= Get(PrimitiveType.Sphere);
        public static Mesh Capsule => _capsule ??= Get(PrimitiveType.Capsule);
        public static Mesh Cylinder => _cylinder ??= Get(PrimitiveType.Cylinder);
        public static Mesh Quad => _quad ??= Get(PrimitiveType.Quad);

        static Mesh Get(PrimitiveType t)
        {
            var go = GameObject.CreatePrimitive(t);
            var m = go.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.DestroyImmediate(go);   // may run in the middle of a physics query, don't leave a collider around
            return m;
        }
    }
}
