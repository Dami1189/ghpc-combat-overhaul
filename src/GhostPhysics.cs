using System.Collections.Generic;
using System.Linq;
using GHPC.Humans;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GHPCHitCam
{
    /// private physics world for the x-ray copy: crew ragdolls and torn plate fragments.
    /// own physics scene (nothing can touch the real game's physics), stepped with the hit cam's own clock so bodies
    /// move in the same slow motion as the round
    ///
    /// forces are physical, not tuned shoves:
    ///  - fragments: momentum p = m*v, mass estimated from the fragment's remaining pen, speed from the game's sim;
    ///    spall embeds (all momentum transfers), main rod / HEAT jet pass through (small fraction)
    ///  - blasts: positive-phase impulse per area from TNT mass and scaled distance (Kingery-style fit),
    ///    x3 inside a closed hull for reflections, times the part's presented area
    internal class GhostPhysics
    {
        static Scene _scene;
        static PhysicsScene _ps;

        public static bool Ensure()
        {
            if (_scene.IsValid() && _scene.isLoaded) return true;
            try
            {
                _scene = SceneManager.CreateScene("HitCamPhysics", new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                _ps = _scene.GetPhysicsScene();
                return true;
            }
            catch (System.Exception e)
            {
                HitCamMod.Log.Warning("No physics scene for the hit cam: " + e.Message);
                return false;
            }
        }

        public static void Adopt(GameObject root)
        {
            if (!Ensure() || root.transform.parent != null) return;
            SceneManager.MoveGameObjectToScene(root, _scene);
        }

        class Body { public Rigidbody Rb; public BoxCollider Box; public BodyPart Part; public float Area; public ConfigurableJoint Joint; public float NextFlail; }

        class Ragdoll
        {
            public CrewInfo Crew;
            public readonly List<Body> Bodies = new List<Body>();
            public bool Active;
            // alive in a burning compartment: thrashing in his seat (pelvis held, limbs driven by their joints)
            public bool Panic;
            public Body Anchor;
            public float PanicClock;
            public System.Random Rnd;
            public Vector3[] PanicStart;           // self-test: how much the limbs really moved
            public float PanicMaxV, PanicMaxMove;
        }

        readonly Ghost _ghost;
        readonly Dictionary<CrewInfo, Ragdoll> _dolls = new Dictionary<CrewInfo, Ragdoll>();
        readonly List<Rigidbody> _debris = new List<Rigidbody>();
        readonly List<(Renderer r, float born)> _hot = new List<(Renderer, float)>();
        bool _running;
        float _acc, _clock, _fineUntil, _nextLog;     // fine steps until this physics time (a hit or blast just landed)
        const float FixedStep = 1f / 600f;
        // once nothing flies fast anymore (fragments and blasts done, ragdolls settling, crewman thrashing in a fire) a
        // third of the steps looks the same
        const float CalmStep = 1f / 200f, FastSpeed = 8f;
        public int MeshColliders;
        public Bounds Inner;
        readonly List<string> _log = new List<string>();
        float _maxSpeed;

        /// self-test diagnostics: where every body ended up and what pushed it
        public string Report()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"walls {Inner.center}/{Inner.size}, mesh colliders {MeshColliders}, armour pieces {ArmourPieces}, max speed {_maxSpeed:0.0} m/s; ");
            foreach (var d in _dolls.Values)
            {
                var torso = d.Bodies.OrderByDescending(b => b.Rb.mass).First();
                Vector3 l = _ghost.Root.transform.InverseTransformPoint(torso.Rb.position);
                sb.Append($"{d.Crew.Label}: active={d.Active} torso {l} inside={Inner.Contains(l)} v={torso.Rb.velocity.magnitude:0.0}"
                    + (d.PanicStart != null ? $" panic={d.Panic} {d.PanicClock:0.0}s, limbs up to {d.PanicMaxV:0.0} m/s, moved up to {d.PanicMaxMove:0.00} m" : "") + "; ");
            }
            sb.Append(" | ").Append(string.Join("; ", _log.Take(12)));
            return sb.ToString();
        }

        public GhostPhysics(Ghost ghost, GhostSnapshot snap)
        {
            _ghost = ghost;
            if (!Ensure()) return;
            BuildStatic(snap);
            foreach (var ci in snap.Crew) BuildRagdoll(ci);
            Physics.SyncTransforms();
            AddSeats();
            Physics.SyncTransforms();
            ResolveStartOverlaps();
            if (AutoTest.Opt("probe", true)) Probe();
        }

        /// right after the snapshot (a hit the hit cam will show): PhysX cooks the readable hull meshes for their colliders
        /// on a worker thread (Physics.BakeMesh can run on any thread), so when the hit cam opens the colliders are already
        /// cooked instead of cooking each one in that frame
        public static void Prebake(GhostSnapshot s)
        {
            if (s == null || !Settings.XrayPhysics.Value) return;
            var ids = new List<int>();
            foreach (var p in s.Exterior) if (p.Mesh != null && p.Mesh.isReadable) ids.Add(p.Mesh.GetInstanceID());
            if (ids.Count == 0) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                foreach (int id in ids) { try { Physics.BakeMesh(id, false); } catch { } }
            });
        }

        void Probe()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"layer {Vis.Layer} self-ignore={Physics.GetIgnoreLayerCollision(Vis.Layer, Vis.Layer)} scene={_ghost.Root.scene.name} statics={_ghost.Root.GetComponentsInChildren<BoxCollider>().Length}; ");
            foreach (var d in _dolls.Values)
            {
                var torso = d.Bodies.OrderByDescending(b => b.Rb.mass).First();
                Vector3 p = torso.Rb.position;
                bool hit = _ps.Raycast(p, Vector3.down, out var h, 6f, ~0);
                sb.Append($"{d.Crew.Label} torso scene={torso.Rb.gameObject.scene.name} down-> {(hit ? h.collider.name + " @" + h.distance.ToString("0.00") : "nothing")}; ");
            }
            // drop test: a plain box and one ragdoll, 1 s of sim, do they stay on the floor?
            var torso0 = _dolls.Values.FirstOrDefault()?.Bodies.OrderByDescending(b => b.Rb.mass).First();
            if (torso0 != null)
            {
                var test = new GameObject("probe box");
                test.layer = Vis.Layer;
                test.transform.SetParent(_ghost.Root.transform, false);
                test.transform.position = torso0.Rb.position + Vector3.up * 0.1f;
                test.AddComponent<BoxCollider>().size = Vector3.one * 0.2f;
                var trb = test.AddComponent<Rigidbody>();
                Physics.SyncTransforms();
                float y0 = trb.position.y;
                for (int i = 0; i < 120; i++) _ps.Simulate(1f / 120f);
                sb.Append($"| drop box: y {y0 - Vis.Origin.y:0.00} -> {trb.position.y - Vis.Origin.y:0.00}");
                Object.DestroyImmediate(test);
                // same on a plain floor next to the ghost (game's physics broadphase ends ~5 km from map origin, past that nothing collides)
                foreach (var at in new[] { Vis.Origin + new Vector3(0f, 30f, 0f) })
                {
                    var floor = new GameObject("probe floor");
                    floor.layer = Vis.Layer;
                    SceneManager.MoveGameObjectToScene(floor, _scene);
                    floor.transform.position = at;
                    floor.AddComponent<BoxCollider>().size = new Vector3(2f, 0.2f, 2f);
                    var cube = new GameObject("probe cube");
                    cube.layer = Vis.Layer;
                    SceneManager.MoveGameObjectToScene(cube, _scene);
                    cube.transform.position = at + Vector3.up * 0.5f;
                    cube.AddComponent<BoxCollider>().size = Vector3.one * 0.2f;
                    var crb = cube.AddComponent<Rigidbody>();
                    Physics.SyncTransforms();
                    for (int i = 0; i < 120; i++) _ps.Simulate(1f / 120f);
                    sb.Append($" | drop on a floor at {at}: {crb.position.y - at.y:0.00} m above it after 1 s");
                    Object.DestroyImmediate(cube);
                    Object.DestroyImmediate(floor);
                }
            }
            HitCamMod.Log.Msg("Physics probe: " + sb);
        }

        /// the hull itself (where the mesh is readable), seats/racks/engine as boxes, and walls hugging the interior
        void BuildStatic(GhostSnapshot snap)
        {
            var root = new GameObject("colliders");
            root.layer = Vis.Layer;
            root.transform.SetParent(_ghost.Root.transform, false);

            foreach (var mr in _ghost.Hull)
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                try { var mc = mr.gameObject.AddComponent<MeshCollider>(); mc.sharedMesh = mf.sharedMesh; _statics.Add(mc); _hull.Add(mc); MeshColliders++; }
                catch { }
            }

            // game's own armour colliders: the real walls, floor and roof (most drawn hull meshes aren't readable)
            foreach (var (mesh, m, convex) in snap.ArmourMeshes)
            {
                try
                {
                    var go = Piece(root.transform, "armour", m);
                    var mc = go.AddComponent<MeshCollider>();
                    mc.convex = convex;
                    mc.sharedMesh = mesh;
                    _statics.Add(mc);
                    ArmourPieces++;
                }
                catch { }
            }
            foreach (var (c0, size, m) in snap.ArmourBoxes)
            {
                var go = Piece(root.transform, "armour box", m);
                var bc = go.AddComponent<BoxCollider>();
                bc.center = c0;
                bc.size = size;
                _statics.Add(bc);
                ArmourPieces++;
            }

            var crewBoxes = snap.Crew.SelectMany(c => c.Shapes).Select(s => s.LocalAabb()).ToList();
            bool first = true;
            Bounds inner = new Bounds();
            foreach (var mp in snap.Modules)
                foreach (var sh in mp.Shapes)
                {
                    var b = sh.LocalAabb();
                    if (mp.Cat != ModCat.Tracks && b.size.magnitude <= 4f)
                    {
                        if (first) { inner = b; first = false; } else inner.Encapsulate(b);
                    }
                    if (b.size.magnitude < 0.08f || b.size.magnitude > 4f || mp.Cat == ModCat.Tracks) continue;
                    AddBox(root.transform, b.center, b.size);
                }
            foreach (var cb in crewBoxes) { if (first) { inner = cb; first = false; } else inner.Encapsulate(cb); }
            if (first) inner = snap.LocalBounds;
            inner.Expand(0.25f);
            Inner = inner;
            // thick walls so a fast body can't tunnel out
            float t = 0.9f;
            Vector3 c = inner.center, s = inner.size, h = inner.extents;
            AddBox(root.transform, c + Vector3.down * (h.y + t * 0.5f), new Vector3(s.x + 2 * t, t, s.z + 2 * t));
            AddBox(root.transform, c + Vector3.up * (h.y + t * 0.5f), new Vector3(s.x + 2 * t, t, s.z + 2 * t));
            AddBox(root.transform, c + Vector3.left * (h.x + t * 0.5f), new Vector3(t, s.y + 2 * t, s.z + 2 * t));
            AddBox(root.transform, c + Vector3.right * (h.x + t * 0.5f), new Vector3(t, s.y + 2 * t, s.z + 2 * t));
            AddBox(root.transform, c + Vector3.back * (h.z + t * 0.5f), new Vector3(s.x + 2 * t, s.y + 2 * t, t));
            AddBox(root.transform, c + Vector3.forward * (h.z + t * 0.5f), new Vector3(s.x + 2 * t, s.y + 2 * t, t));
        }

        readonly List<Collider> _statics = new List<Collider>();
        readonly HashSet<Collider> _hull = new HashSet<Collider>();
        static readonly RaycastHit[] _rayHits = new RaycastHit[24];
        bool _synced;

        public bool HasHull => _hull.Count > 0;

        /// whether a crewman may go limp: only the ones the game took out (critical / killed). the rest stay in their seats
        /// whatever blast or fragment passed them, they're alive and holding on
        public System.Func<CrewInfo, bool> MayGoLimp;

        public bool RaycastHull(Vector3 originL, Vector3 dirL, float maxDist, out float dist) => RaycastHull(originL, dirL, maxDist, out dist, out _);

        /// distance along a ray (ghost-local) to the vehicle's real outer surface within maxDist, plus the normal at the hit
        public bool RaycastHull(Vector3 originL, Vector3 dirL, float maxDist, out float dist, out Vector3 normalL)
        {
            normalL = Vector3.zero;
            dist = 0f;
            if (_hull.Count == 0 || !Ensure()) return false;
            if (!_synced) { Physics.SyncTransforms(); _synced = true; }
            var root = _ghost.Root.transform;
            int n = _ps.Raycast(root.TransformPoint(originL), root.TransformDirection(dirL).normalized, _rayHits, maxDist, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
                if (_hull.Contains(_rayHits[i].collider) && _rayHits[i].distance < best) { best = _rayHits[i].distance; normalL = root.InverseTransformDirection(_rayHits[i].normal); }
            if (best == float.MaxValue) return false;
            dist = best;
            return true;
        }
        Transform _staticRoot;
        public int ArmourPieces;

        /// empty child placed by a target-local matrix (position, rotation, scale)
        static GameObject Piece(Transform parent, string name, Matrix4x4 m)
        {
            var go = new GameObject(name);
            go.layer = Vis.Layer;
            go.transform.SetParent(parent, false);
            Vis.ToTrs(m, out var pos, out var rot, out var scale);   // keeps a mirror (m.rotation of a mirrored one is junk)
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            go.transform.localScale = scale;
            return go;
        }

        void AddBox(Transform parent, Vector3 center, Vector3 size, string name = "box")
        {
            _staticRoot = parent;
            var go = new GameObject(name);
            go.layer = Vis.Layer;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = center;
            var bc = go.AddComponent<BoxCollider>();
            bc.size = size;
            _statics.Add(bc);
        }

        /// a seat under every seated crewman's pelvis so a body going limp sags into it instead of dropping through
        /// (seats aren't a hit zone in GHPC so they have no collider)
        void AddSeats()
        {
            if (_staticRoot == null) return;
            foreach (var d in _dolls.Values)
            {
                var pelvis = d.Bodies.FirstOrDefault(b => b.Part == BodyPart.LowerSpine) ?? d.Bodies.OrderByDescending(b => b.Rb.mass).First();
                var wb = pelvis.Box.bounds;   // world AABB of the pelvis box
                Vector3 bottomW = new Vector3(wb.center.x, wb.min.y, wb.center.z);
                Vector3 l = _ghost.Root.transform.InverseTransformPoint(bottomW);
                float w = Mathf.Clamp(Mathf.Max(wb.size.x, wb.size.z) * 1.4f, 0.32f, 0.5f);
                AddBox(_staticRoot, l + Vector3.down * 0.045f, new Vector3(w, 0.08f, w), "seat");
            }
        }

        /// anything a body starts inside of (its seat cushion, turret basket, a neighbouring limb) mustn't blow it apart on
        /// the first step: those pairs ignore each other, everything else collides
        void ResolveStartOverlaps()
        {
            int ignored = 0;
            foreach (var d in _dolls.Values)
                foreach (var b in d.Bodies)
                    foreach (var s in _statics)
                    {
                        if (s == null) continue;
                        if (Overlap(b.Box, s)) { Physics.IgnoreCollision(b.Box, s); ignored++; }
                    }
            if (AutoTest.Active) _log.Add($"start overlaps ignored: {ignored}, statics {_statics.Count}");
        }

        static bool Overlap(Collider a, Collider b)
        {
            if (!a.bounds.Intersects(b.bounds)) return false;
            try
            {
                return Physics.ComputePenetration(a, a.transform.position, a.transform.rotation, b, b.transform.position, b.transform.rotation, out _, out float dist) && dist > 0.005f;
            }
            catch { return true; }
        }

        // body segment masses (kg) and presented areas (m^2) for a ~80 kg adult
        static float Mass(BodyPart p)
        {
            switch (p)
            {
                case BodyPart.Torso: return 22f;
                case BodyPart.UpperSpine: case BodyPart.LowerSpine: return 10f;
                case BodyPart.Head: return 5f;
                case BodyPart.Neck: return 1.2f;
                case BodyPart.LeftLeg: case BodyPart.RightLeg: return 8f;
                case BodyPart.LeftArm: case BodyPart.RightArm: return 2.2f;
                case BodyPart.LeftHand: case BodyPart.RightHand: return 0.5f;
                case BodyPart.LeftFoot: case BodyPart.RightFoot: return 1.1f;
                default: return 1f;
            }
        }

        static float Area(BodyPart p)
        {
            switch (p)
            {
                case BodyPart.Torso: return 0.12f;
                case BodyPart.UpperSpine: case BodyPart.LowerSpine: return 0.06f;
                case BodyPart.Head: return 0.035f;
                case BodyPart.LeftLeg: case BodyPart.RightLeg: return 0.05f;
                case BodyPart.LeftArm: case BodyPart.RightArm: return 0.03f;
                default: return 0.01f;
            }
        }

        void BuildRagdoll(CrewInfo ci)
        {
            if (!_ghost.CrewParts.TryGetValue(ci, out var gos) || gos.Count != ci.Parts.Count) return;
            if (!ci.HasSkeleton) return;    // never simulate a body we can't hold together
            var doll = new Ragdoll { Crew = ci };
            var bodies = new Body[ci.Parts.Count];
            for (int i = 0; i < ci.Parts.Count; i++)
            {
                var p = ci.Parts[i];
                if (p.Internal) continue;
                CrewBuilder.BoxOf(p, out var center, out var size);
                var box = gos[i].AddComponent<BoxCollider>();
                box.center = center;
                box.size = Vector3.Max(size * 0.92f, Vector3.one * 0.02f);
                var rb = gos[i].AddComponent<Rigidbody>();
                rb.mass = Mass(p.Part);
                rb.drag = 0.05f;
                rb.angularDrag = 0.3f;
                rb.isKinematic = true;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                rb.maxDepenetrationVelocity = 1.5f;
                rb.solverIterations = 25;
                rb.solverVelocityIterations = 10;
                bodies[i] = new Body { Rb = rb, Box = box, Part = p.Part, Area = Area(p.Part) };
                doll.Bodies.Add(bodies[i]);
            }
            for (int i = 0; i < ci.Parts.Count; i++)
            {
                var p = ci.Parts[i];
                if (!p.Internal) continue;
                int host = p.Parent >= 0 && bodies[p.Parent] != null ? p.Parent : System.Array.FindIndex(bodies, b => b != null);
                if (host >= 0) gos[i].transform.SetParent(gos[host].transform, true);
            }
            for (int i = 0; i < ci.Parts.Count; i++)
            {
                var p = ci.Parts[i];
                if (bodies[i] == null || p.Parent < 0 || bodies[p.Parent] == null) continue;
                var j = gos[i].AddComponent<ConfigurableJoint>();
                bodies[i].Joint = j;
                j.connectedBody = bodies[p.Parent].Rb;
                Vector3 parentC = gos[p.Parent].transform.TransformPoint(bodies[p.Parent].Box.center);
                Vector3 myC = gos[i].transform.TransformPoint(bodies[i].Box.center);
                // anchor at the joint between the two boxes (midway between the facing surfaces)
                Vector3 a = Physics.ClosestPoint(parentC, bodies[i].Box, gos[i].transform.position, gos[i].transform.rotation);
                Vector3 b2 = Physics.ClosestPoint(myC, bodies[p.Parent].Box, gos[p.Parent].transform.position, gos[p.Parent].transform.rotation);
                j.anchor = gos[i].transform.InverseTransformPoint((a + b2) * 0.5f);
                j.xMotion = j.yMotion = j.zMotion = ConfigurableJointMotion.Locked;
                j.angularXMotion = j.angularYMotion = j.angularZMotion = ConfigurableJointMotion.Limited;
                // spine and neck bend way less than a hip or shoulder (and the skin across the waist stays sane)
                bool spine = p.Part == BodyPart.UpperSpine || p.Part == BodyPart.LowerSpine || p.Part == BodyPart.Neck;
                j.lowAngularXLimit = new SoftJointLimit { limit = spine ? -20f : -40f };
                j.highAngularXLimit = new SoftJointLimit { limit = spine ? 20f : 40f };
                j.angularYLimit = new SoftJointLimit { limit = spine ? 15f : 45f };
                j.angularZLimit = new SoftJointLimit { limit = spine ? 15f : 30f };
                j.projectionMode = JointProjectionMode.PositionAndRotation;
                j.projectionDistance = 0.01f;
                j.projectionAngle = 8f;
                j.enablePreprocessing = false;
                // bit of joint friction so a dead body settles instead of jittering
                j.angularXDrive = j.angularYZDrive = new JointDrive { positionSpring = 0f, positionDamper = 2f, maximumForce = float.MaxValue };
                // jointed neighbours never collide (they overlap at the joint)
                Physics.IgnoreCollision(bodies[i].Box, bodies[p.Parent].Box);
            }
            // self-collision: everything else in the body collides (arms on chest, legs on each other),
            // except pairs that already overlap in the sitting pose
            for (int x = 0; x < doll.Bodies.Count; x++)
                for (int y = x + 1; y < doll.Bodies.Count; y++)
                    if (Overlap(doll.Bodies[x].Box, doll.Bodies[y].Box)) Physics.IgnoreCollision(doll.Bodies[x].Box, doll.Bodies[y].Box);
            _dolls[ci] = doll;
        }

        void Activate(Ragdoll d)
        {
            if (d.Active) return;
            d.Active = true;
            foreach (var b in d.Bodies) b.Rb.isKinematic = false;
            _running = true;
        }

        static readonly JointDrive Friction = new JointDrive { positionSpring = 0f, positionDamper = 2f, maximumForce = float.MaxValue };

        public bool Panicking(CrewInfo ci) => _dolls.TryGetValue(ci, out var d) && d.Panic;
        public bool Panicked(CrewInfo ci) => _dolls.TryGetValue(ci, out var d) && d.PanicStart != null;

        /// where a crewman's torso is now (ghost-local), if he has a ragdoll
        public bool TorsoL(CrewInfo ci, out Vector3 l)
        {
            l = Vector3.zero;
            if (!_dolls.TryGetValue(ci, out var d) || d.Bodies.Count == 0) return false;
            var t = d.Bodies.FirstOrDefault(b => b.Part == BodyPart.Torso) ?? d.Bodies.OrderByDescending(b => b.Rb.mass).First();
            l = _ghost.Root.transform.InverseTransformPoint(t.Rb.worldCenterOfMass);
            return true;
        }
        public bool AnyPanic { get; private set; }

        /// crewman alive in a burning compartment thrashes in his seat: pelvis stays put, every joint gets driven toward a
        /// new random pose every 0.1-0.3 s within its normal range (arms flung about, legs kicking, torso twisting, head
        /// tossing), frantic at first and weaker as burns and smoke take him
        public void StartPanic(CrewInfo ci, int seed)
        {
            if (!_dolls.TryGetValue(ci, out var d) || d.Panic || d.Active || d.Bodies.Count == 0) return;
            d.Panic = true;
            AnyPanic = true;
            d.Rnd = new System.Random(seed);
            d.PanicClock = 0f;
            d.PanicStart = d.Bodies.Select(b => b.Rb.position).ToArray();
            d.Anchor = d.Bodies.FirstOrDefault(b => b.Part == BodyPart.LowerSpine) ?? d.Bodies.FirstOrDefault(b => b.Part == BodyPart.Torso) ?? d.Bodies.OrderByDescending(b => b.Rb.mass).First();
            foreach (var b in d.Bodies)
            {
                b.Rb.isKinematic = b == d.Anchor;
                b.NextFlail = 0f;
                if (b.Joint == null) continue;
                // stiff enough to hold the limb up against gravity and swing it fast, damped so it doesn't ring
                float m = b == d.Anchor && b.Joint.connectedBody != null ? b.Joint.connectedBody.mass : b.Rb.mass;   // the anchor's own joint swings the torso above it
                b.Joint.angularXDrive = b.Joint.angularYZDrive = new JointDrive { positionSpring = 60f * m, positionDamper = 3f * m, maximumForce = float.MaxValue };
            }
            _running = true;
        }

        /// he died (or passed out): drives let go and he slumps where he is
        public void EndPanic(CrewInfo ci)
        {
            if (!_dolls.TryGetValue(ci, out var d) || !d.Panic) return;
            d.Panic = false;
            foreach (var b in d.Bodies)
                if (b.Joint != null) { b.Joint.targetRotation = Quaternion.identity; b.Joint.angularXDrive = b.Joint.angularYZDrive = Friction; }
            Activate(d);
            AnyPanic = _dolls.Values.Any(x => x.Panic);
        }

        static Vector3 FlailRange(BodyPart p)
        {
            switch (p)
            {
                case BodyPart.LeftArm: case BodyPart.RightArm: case BodyPart.LeftHand: case BodyPart.RightHand: return new Vector3(40f, 45f, 30f);
                case BodyPart.LeftLeg: case BodyPart.RightLeg: return new Vector3(35f, 20f, 12f);
                case BodyPart.LeftFoot: case BodyPart.RightFoot: return new Vector3(30f, 10f, 10f);
                case BodyPart.Head: return new Vector3(25f, 25f, 15f);
                default: return new Vector3(15f, 12f, 10f);   // spine, neck, torso
            }
        }

        void UpdatePanic(float dt)
        {
            if (!AnyPanic) return;
            foreach (var d in _dolls.Values)
            {
                if (!d.Panic) continue;
                d.PanicClock += dt;
                float k = Mathf.Lerp(1f, 0.35f, Mathf.Clamp01((d.PanicClock - 4f) / 12f));
                foreach (var b in d.Bodies)
                {
                    if (b.Joint == null || d.PanicClock < b.NextFlail) continue;
                    b.NextFlail = d.PanicClock + 0.1f + 0.2f * (float)d.Rnd.NextDouble();
                    Vector3 r = FlailRange(b.Part) * k;
                    float R(float a) => ((float)d.Rnd.NextDouble() * 2f - 1f) * a;
                    b.Joint.targetRotation = Quaternion.Euler(R(r.x), R(r.y), R(r.z));
                }
                for (int i = 0; i < d.Bodies.Count; i++)
                {
                    var b = d.Bodies[i];
                    if (b == d.Anchor) continue;
                    d.PanicMaxV = Mathf.Max(d.PanicMaxV, b.Rb.velocity.magnitude);
                    d.PanicMaxMove = Mathf.Max(d.PanicMaxMove, Vector3.Distance(b.Rb.position, d.PanicStart[i]));
                }
            }
        }

        /// fragment / round hit on a crewman: impulse (N*s, target-local vector) at a target-local point
        public void Impulse(CrewInfo ci, Vector3 pointL, Vector3 impulseL)
        {
            if (impulseL.sqrMagnitude < 1e-4f) return;
            if (!_dolls.TryGetValue(ci, out var d) || d.Bodies.Count == 0) return;
            if (MayGoLimp != null && !MayGoLimp(ci)) { if (AutoTest.Active) _log.Add($"hit {ci.Label}: alive, stays seated"); return; }
            Activate(d);
            Vector3 pw = _ghost.Root.transform.TransformPoint(pointL);
            var near = d.Bodies.OrderBy(b => (b.Box.ClosestPoint(pw) - pw).sqrMagnitude).First();
            if (AutoTest.Active) _log.Add($"hit {ci.Label}/{near.Part} J={impulseL.magnitude:0.0}");
            near.Rb.AddForceAtPosition(_ghost.Root.transform.TransformDirection(impulseL), pw, ForceMode.Impulse);
            _fineUntil = Mathf.Max(_fineUntil, _clock + 0.3f);
        }

        /// positive-phase blast impulse on everything in range
        public void Blast(Vector3 centerL, float tntKg, bool confined)
        {
            if (tntKg <= 0f) return;
            Vector3 cw = _ghost.Root.transform.TransformPoint(centerL);
            float w3 = Mathf.Pow(tntKg, 1f / 3f);
            float reflect = confined ? 2f : 1f;
            foreach (var d in _dolls.Values)
            {
                if (MayGoLimp != null && !MayGoLimp(d.Crew)) continue;   // game didn't hurt him: he stays at his station
                bool any = false;
                foreach (var b in d.Bodies)
                {
                    float i = BlastImpulse(Vector3.Distance(cw, b.Rb.worldCenterOfMass), w3) * reflect;   // Pa*s
                    if (i * b.Area > 0.5f) { any = true; break; }
                }
                if (!any) continue;
                Activate(d);
                // impulse lands on each part's presented area but the body is held together: a hand alone would get ~8x the
                // torso's speed (and tear the joints apart). each part keeps 30 % of its own share and moves with the body's mean
                // for the rest, no part faster than a body can go
                float tot = 0f, mass = 0f;
                Vector3 p = Vector3.zero;
                var own = new Vector3[d.Bodies.Count];
                for (int k = 0; k < d.Bodies.Count; k++)
                {
                    var b = d.Bodies[k];
                    Vector3 to = b.Rb.worldCenterOfMass - cw;
                    float i = BlastImpulse(to.magnitude, w3) * reflect;
                    own[k] = to.normalized * i * b.Area;
                    p += own[k];
                    mass += b.Rb.mass;
                    tot += i * b.Area;
                }
                Vector3 mean = p / Mathf.Max(mass, 1f);
                for (int k = 0; k < d.Bodies.Count; k++)
                {
                    var b = d.Bodies[k];
                    b.Rb.AddForce(Vector3.ClampMagnitude(0.7f * mean + 0.3f * own[k] / b.Rb.mass, MaxBodySpeed), ForceMode.VelocityChange);
                }
                if (AutoTest.Active) _log.Add($"blast {tntKg:0.0}kg confined={confined} on {d.Crew.Label}: total J={tot:0}, body thrown at {mean.magnitude:0.0} m/s");
            }
            foreach (var rb in _debris)
                if (rb != null)
                {
                    Vector3 to = rb.worldCenterOfMass - cw;
                    rb.AddForce(to.normalized * BlastImpulse(to.magnitude, w3) * reflect * 0.004f, ForceMode.Impulse);
                }
            _fineUntil = Mathf.Max(_fineUntil, _clock + 0.3f);
            _running = true;
        }

        /// scaled positive-phase impulse of a free-air TNT burst, Pa*s (fit to Kingery-Bulmash, Z = R / W^(1/3))
        public static float BlastImpulse(float rangeM, float w3)
        {
            float z = Mathf.Max(rangeM, 0.15f) / w3;
            return 200f * w3 / Mathf.Pow(Mathf.Max(z, 0.3f), 1.2f);
        }

        /// torn plate fragments flying from a hole into the vehicle (speed in m/s, real physics speed)
        public void SpawnFragments(Vector3 pointL, Vector3 intoL, float size, int count, float speed, int seed)
        {
            var rnd = new System.Random(seed);
            float rs() => (float)rnd.NextDouble();
            for (int i = 0; i < count; i++)
            {
                var go = Vis.Child(_ghost.Root.transform, "fragment", Shards.Get(seed + i),
                    Matrix4x4.TRS(pointL + new Vector3(rs() - 0.5f, rs() - 0.5f, rs() - 0.5f) * size * 0.6f,
                        Random.rotationUniform, new Vector3(size * (0.5f + 0.6f * rs()), Mathf.Max(size * 0.22f, 0.012f), size * (0.8f + 0.7f * rs()))), Vis.Unlit);   // torn plate splinters: wide, thin, long
                var r = go.GetComponent<Renderer>();
                go.AddComponent<BoxCollider>();
                var rb = go.AddComponent<Rigidbody>();
                rb.mass = Mathf.Clamp(go.transform.localScale.x * go.transform.localScale.y * go.transform.localScale.z * 7850f, 0.01f, 5f);
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                Vector3 spread = new Vector3(rs() - 0.5f, rs() - 0.5f, rs() - 0.5f) * 0.9f;
                rb.velocity = _ghost.Root.transform.TransformDirection((intoL.normalized + spread).normalized) * speed * (0.5f + rs());
                rb.angularVelocity = Random.insideUnitSphere * 60f;
                _debris.Add(rb);
                _hot.Add((r, Time.unscaledTime));
            }
            _fineUntil = Mathf.Max(_fineUntil, _clock + 0.3f);
            _running = true;
        }

        const float MaxBodySpeed = 25f;

        void ClampSpeeds()
        {
            foreach (var d in _dolls.Values)
            {
                if (!d.Active) continue;
                float vmax = 0f;
                foreach (var b in d.Bodies) vmax = Mathf.Max(vmax, b.Rb.velocity.magnitude);
                if (vmax <= MaxBodySpeed) continue;
                float k = MaxBodySpeed / vmax;
                foreach (var b in d.Bodies) { b.Rb.velocity *= k; b.Rb.angularVelocity *= k; }
            }
        }

        /// advance the private world by dt seconds of *sim* time (caller maps the slow motion)
        public void Step(float dt)
        {
            if (!Ensure()) return;
            foreach (var (r, born) in _hot)
                if (r != null)
                {
                    float k = Mathf.Clamp01((Time.unscaledTime - born) / 2.2f);
                    Vis.SetColor(r, Color.Lerp(new Color(1f, 0.6f, 0.25f), new Color(0.22f, 0.22f, 0.24f), k));
                }
            if (!_running || dt <= 0f) return;
            long t0 = Perf.Now;
            UpdatePanic(dt);
            Perf.Lap("Physics/panic", ref t0);
            _acc += dt;
            int n = 0;
            float fastest = 0f;
            foreach (var d in _dolls.Values) if (d.Active) foreach (var b in d.Bodies) fastest = Mathf.Max(fastest, b.Rb.velocity.magnitude);
            _maxSpeed = Mathf.Max(_maxSpeed, fastest);
            float dollFast = fastest;
            int awake = 0;
            foreach (var rb in _debris) if (rb != null && !rb.IsSleeping()) { awake++; fastest = Mathf.Max(fastest, rb.velocity.magnitude); }
            if (fastest > FastSpeed) _fineUntil = Mathf.Max(_fineUntil, _clock + 0.15f);
            float step = _clock < _fineUntil ? FixedStep : CalmStep;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (_acc >= step && n++ < 24)
            {
                if (watch.ElapsedMilliseconds > 12) { _acc = 0f; break; }   // frame budget: slow the x-ray physics down instead of stalling the game
                _ps.Simulate(step);
                _acc -= step;
                _clock += step;
                ClampSpeeds();
            }
            if (_acc > step * 4f) _acc = 0f;   // never build up a backlog (frame hitch)
            Perf.Lap("Physics/simulate", ref t0);
            if (AutoTest.Active && Time.unscaledTime >= _nextLog)
            {
                _nextLog = Time.unscaledTime + 1f;
                HitCamMod.Log.Msg($"x-ray physics: step 1/{1f / step:0}, {n} steps this frame, fastest doll part {dollFast:0.0} m/s, debris {awake} awake of {_debris.Count} (fastest {fastest:0.0} m/s), dolls active {_dolls.Values.Count(d => d.Active)}, sim {watch.Elapsed.TotalMilliseconds:0.0} ms");
            }
        }
    }
}
