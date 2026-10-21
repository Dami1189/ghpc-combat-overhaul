using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GHPC;
using GHPC.Mission;
using GHPC.Weapons;
using GHPC.World;
using UnityEngine;
using UnityEngine.Rendering;
using DC = GHPC.Equipment.DestructibleComponent;

namespace GHPCHitCam
{
    /// Visible damage to a vehicle's parts in the game world, beyond the armour plates: running gear that gets chewed up
    /// instead of holed, and (driven by the game's own damage events) burning engines, ammo cook-off, bent or broken gun
    /// barrels, helicopters losing their tail, thrown tracks. Purely visual: the game's damage model is not touched.
    internal static class WorldModules
    {
        static Material _bare, _burnt;
        static readonly HashSet<(int, DC)> _seen = new HashSet<(int, DC)>();
        static readonly Dictionary<DC, (Vector3 point, Vector3 dir, AmmoType ammo, float time)> _lastHit = new Dictionary<DC, (Vector3, Vector3, AmmoType, float)>();
        static readonly HashSet<DC> _done = new HashSet<DC>();
        static readonly List<GameObject> _bodies = new List<GameObject>();
        const int MaxBodies = 16;

        /// Freshly torn / cut steel.
        internal static Material Bare
        {
            get
            {
                if (_bare == null)
                {
                    var std = Shader.Find("Standard");
                    _bare = std != null ? new Material(std) : new Material(WorldDamage.Metal);
                    _bare.color = new Color(0.52f, 0.5f, 0.47f);
                    if (std != null) { _bare.SetFloat("_Metallic", 0.9f); _bare.SetFloat("_Glossiness", 0.55f); }
                }
                return _bare;
            }
        }

        static Material _darkM;

        /// The dark inside of a bore or a torn shell (unlit near-black).
        internal static Material Dark
        {
            get
            {
                if (_darkM == null) { _darkM = new Material(WorldDamage.DarkMat) { name = "HitCam dark inside" }; _darkM.color = new Color(0.015f, 0.014f, 0.013f, 1f); }
                return _darkM;
            }
        }

        /// Steel blackened by a blast.
        internal static Material Burnt
        {
            get
            {
                if (_burnt == null)
                {
                    var std = Shader.Find("Standard");
                    _burnt = std != null ? new Material(std) : new Material(WorldDamage.Metal);
                    _burnt.color = new Color(0.07f, 0.065f, 0.06f);
                    if (std != null) { _burnt.SetFloat("_Metallic", 0.35f); _burnt.SetFloat("_Glossiness", 0.12f); }
                }
                return _burnt;
            }
        }

        public static void Reset()
        {
            _seen.Clear();
            _lastHit.Clear();
            _done.Clear();
            _bodies.Clear();
            _gear.Clear();
            _bursts.Clear();
            WorldBarrel.Reset();
            WorldHeli.Reset();
        }

        /// A big loose part (a barrel's muzzle end, a tail boom): a rigidbody without colliders (the caller adds them),
        /// with its own small cap so the cap on shrapnel never takes it.
        internal static GameObject SpawnBody(string name, Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel, float mass, float life)
        {
            var go = new GameObject(name) { layer = WorldDamage.DebrisLayer };
            go.transform.SetPositionAndRotation(pos, rot);
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = Mathf.Clamp(mass, 1f, 20000f);
            rb.velocity = vel;
            rb.angularVelocity = angVel;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            go.AddComponent<Expire>().At = Time.time + life;
            _bodies.RemoveAll(b => b == null);
            _bodies.Add(go);
            while (_bodies.Count > MaxBodies) { UnityEngine.Object.Destroy(_bodies[0]); _bodies.RemoveAt(0); }
            return go;
        }

        // ------------------------------------------------------------------ the game's own damage events

        /// The game's module hit zones (DestructibleComponent colliders) the world damage draws something for.
        public static bool Wants(Unit u, DC dc)
        {
            if (u == null || dc == null || !Settings.WorldDamage.Value) return false;
            string n = (dc.Name ?? "").ToLowerInvariant();
            if (n.Contains("'s ")) return false;                       // crew body parts
            return BarrelOf(u, dc) != null || IsGearDc(dc) || n.Contains("tail") || n.Contains("engine") || n.Contains("rotor");
        }

        static bool IsGearDc(DC dc)
        {
            string n = (dc.Name ?? "").ToLowerInvariant();
            return n.Contains("track") || n.Contains("wheel") || n.Contains("sprocket");
        }

        static WeaponSystem BarrelOf(Unit u, DC dc)
        {
            foreach (var root in WorldDamage.UnitRoots(u))
                foreach (var ws in root.GetComponentsInChildren<WeaponSystem>(true))
                    if (ws != null && ws.BarrelHitZone == dc) return ws;
            return null;
        }

        /// A round on one of the game's module hit zones (physics pose, as the round saw it).
        public static void OnModuleHit(int roundId, Unit u, Collider col, DC dc, Vector3 point, Vector3 normal, Vector3 dir, bool pen, bool ric,
                                       AmmoType ammo, float speed, float holeR)
        {
            try
            {
                if (u == null || dc == null || ammo == null || !_seen.Add((roundId, dc))) return;
                if (_seen.Count > 512) _seen.Clear();
                var tr = WorldDamage.PhysicsToRender(col);
                Vector3 p = tr.MultiplyPoint3x4(point), n = tr.MultiplyVector(normal).normalized, d = tr.MultiplyVector(dir).normalized;
                if (n.sqrMagnitude < 0.01f) n = -d;
                if (Vector3.Dot(n, d) > 0f) n = -n;
                _lastHit[dc] = (p, d, ammo, Time.time);
                WorldDamage.EnsureInit();
                int layer = WorldDamage.VisibleLayer(u);
                bool he = ammo.Category == AmmoType.AmmoCategory.Explosive, heat = ammo.Category == AmmoType.AmmoCategory.ShapedCharge;
                var ws = BarrelOf(u, dc);
                if (ws != null)
                {
                    var b = WorldBarrel.For(ws, u);
                    b?.Hit(p, n, d, pen, ric, ammo, speed, holeR);
                    return;
                }
                if (IsGearDc(dc))
                {
                    RunningGearHit(u, col, p, n, d, he, heat, pen, ammo.TntEquivalentKg, Mathf.Max(ammo.Caliber, ammo.Radius * 2000f) * 0.001f, layer);
                    return;
                }
                if (u.Type == UnitType.AirVehicle && (he || heat)) WorldHeli.Burst(u, p, ammo);
            }
            catch (Exception e) { HitCamMod.Log.Warning("World modules (hit): " + e); }
        }

        static readonly Dictionary<Unit, (Vector3 point, float time)> _bursts = new Dictionary<Unit, (Vector3, float)>();

        // the round a pen check is running for: the game deals a module's damage inside that check (on the collider it
        // struck, through a proxy hit zone or by the HEAT blast), before the check's postfix gets to record the hit
        static Unit _penUnit;
        static Vector3 _penPoint;
        static int _penFrame = -1;

        public static void PenStarted(Unit u, Vector3 point) { _penUnit = u; _penPoint = point; _penFrame = Time.frameCount; }
        public static void PenEnded() => _penFrame = -1;

        static Vector3 Closest(Collider c, Vector3 p)
        {
            if (!c.enabled || !c.gameObject.activeInHierarchy || (c is MeshCollider mc && !mc.convex)) return ZoneBounds(c).ClosestPoint(p);
            return c.ClosestPoint(p);
        }

        /// A module hit zone's box in the world. Some of them (the T-72M1's tracks) are "Raycast Only" colliders the game
        /// switches on only while a round's ray goes past: switched off, Unity reports an empty box in the vehicle's middle.
        internal static Bounds ZoneBounds(Collider c)
        {
            if (c == null) return default;
            if (c.enabled && c.gameObject.activeInHierarchy && c.bounds.size.sqrMagnitude > 1e-6f) return c.bounds;
            Bounds lb;
            if (c is MeshCollider mc && mc.sharedMesh != null) lb = mc.sharedMesh.bounds;
            else if (c is BoxCollider bc) lb = new Bounds(bc.center, bc.size);
            else if (c is SphereCollider sc) lb = new Bounds(sc.center, Vector3.one * sc.radius * 2f);
            else if (c is CapsuleCollider cc) lb = new Bounds(cc.center, Vector3.one * Mathf.Max(cc.radius * 2f, cc.height));
            else return new Bounds(c.transform.position, Vector3.zero);
            var m = c.transform.localToWorldMatrix;
            var w = new Bounds(m.MultiplyPoint3x4(lb.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
                w.Encapsulate(m.MultiplyPoint3x4(lb.center + Vector3.Scale(lb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f))));
            return w;
        }

        /// A track that died inside the pen check of a round that touched it (or burst right beside it): where (drawn pose).
        static Vector3? PenAt(Unit u, DC dc)
        {
            if (_penFrame != Time.frameCount || (_penUnit != null && _penUnit != u) || dc.ComponentCollision == null) return null;
            var tc = dc.ComponentCollision;
            Vector3 c = Closest(tc, _penPoint);
            if ((c - _penPoint).magnitude > 1.2f) return null;
            return WorldDamage.PhysicsToRender(tc).MultiplyPoint3x4(c);
        }

        /// A round's own blast (HE on impact or fuzed, a HEAT warhead): ground vehicles beside it get it as a burst, so a
        /// track its fragments kill breaks where it was nearest. A vehicle's ammunition going off is not one of these.
        public static void OnRoundBlast(Vector3 at, AmmoType ammo)
        {
            try
            {
                if (!Settings.WorldDamage.Value || ammo == null || ammo.TntEquivalentKg <= 0f) return;
                float reach = 5f + 4f * Mathf.Pow(ammo.TntEquivalentKg, 1f / 3f);
                foreach (var u in SceneUnitsManager.Instance.AllUnitsInScene.OfType<Unit>())
                {
                    if (u == null || u.Type == UnitType.AirVehicle || u.Type == UnitType.Infantry) continue;
                    if ((u.transform.position - at).sqrMagnitude > reach * reach) continue;
                    _bursts[u] = (at, Time.time);
                }
            }
            catch (Exception e) { HitCamMod.Log.Warning("World modules (round blast): " + e.Message); }
        }

        /// An HE / HEAT burst on a vehicle's armour (drawn pose): helicopters keep track of what it did to their tail;
        /// a track destroyed right after by the blast breaks where it was nearest the burst.
        public static void OnBurst(Unit u, Vector3 point, AmmoType ammo)
        {
            try
            {
                if (u == null) return;
                _bursts[u] = (point, Time.time);
                if (u.Type == UnitType.AirVehicle) WorldHeli.Burst(u, point, ammo);
            }
            catch (Exception e) { HitCamMod.Log.Warning("World modules (burst): " + e); }
        }

        /// A round detonating anywhere: helicopters within reach get the burst (their plain hull boxes set fuzes off
        /// without being armour, so this is how a shell bursting beside a tail boom reaches it).
        public static void OnDetonation(Vector3 at, AmmoType ammo)
        {
            try
            {
                if (!Settings.WorldDamage.Value || ammo == null) return;
                float nearest = float.MaxValue;
                foreach (var u in SceneUnitsManager.Instance.AllUnitsInScene.OfType<Unit>())
                {
                    if (u == null || (u.Type != UnitType.AirVehicle && u.GetComponent<GHPC.Vehicle.HelicopterController>() == null)) continue;
                    float d = (u.transform.position - at).magnitude;
                    nearest = Mathf.Min(nearest, d);
                    if (d > 20f) continue;
                    WorldHeli.Burst(u, at, ammo);
                }
                if (AutoTest.Active) HitCamMod.Log.Msg($"World modules: {ammo.Name} detonated at {at}, nearest helicopter {(nearest < float.MaxValue ? nearest.ToString("0.0") + " m" : "none")}");
            }
            catch (Exception e) { HitCamMod.Log.Warning("World modules (detonation): " + e.Message); }
        }

        static Vector3? NearBurst(Unit u, DC dc)
        {
            if (!_bursts.TryGetValue(u, out var b) || Time.time - b.time > 2f || dc.ComponentCollision == null) return null;
            Vector3 c = Closest(dc.ComponentCollision, b.point);
            return (c - b.point).magnitude < 2.5f ? c : (Vector3?)null;
        }

        /// A module's health changed (the game's own damage model).
        public static void OnHealth(DC dc, float before, float after)
        {
            try
            {
                if (dc == null || !Settings.WorldDamage.Value || after > 0f || before <= 0f || !_done.Add(dc)) return;
                var u = dc.Unit as Unit;
                if (u == null) return;
                string n = (dc.Name ?? "").ToLowerInvariant();
                if (n.Contains("'s ")) return;
                _lastHit.TryGetValue(dc, out var hit);
                bool recent = hit.ammo != null && Time.time - hit.time < 3f;
                if (n.Contains("engine")) EngineFire.Start(u, dc, recent ? hit.ammo : null);
                else if (n.Contains("track"))
                {
                    // the game zeroes every module of a vehicle that blows up; a track breaks only where a round or a burst got it
                    // (the round being checked right now first: the game kills the track before that check's hit is recorded)
                    Vector3? at = PenAt(u, dc) ?? (recent ? hit.point : NearBurst(u, dc));
                    if (AutoTest.Active) HitCamMod.Log.Msg($"World track {Recorder.SafeName(u)} '{dc.Name}': destroyed, {(PenAt(u, dc).HasValue ? "by the round being checked" : recent ? "by a recent hit on it" : at.HasValue ? "by a burst beside it" : "nothing near it")}");
                    if (at.HasValue) WorldTrack.Break(u, dc, at);
                    else if (AutoTest.Active) HitCamMod.Log.Msg($"World track {Recorder.SafeName(u)} '{dc.Name}': destroyed with nothing near it (the vehicle blew up?), left as it is");
                }
                else if (u.Type == UnitType.AirVehicle && n.Contains("tail")) WorldHeli.TailLost(u);
            }
            catch (Exception e) { HitCamMod.Log.Warning("World modules (health): " + e); }
        }

        public static void OnUnitDestroyed(Unit u)
        {
            try { if (u != null && u.Type == UnitType.AirVehicle) WorldHeli.Down(u); }
            catch (Exception e) { HitCamMod.Log.Warning("World modules (destroyed): " + e); }
        }

        // ------------------------------------------------------------------ running gear

        static readonly string[] GearWords = { "track", "tread", "wheel", "sprocket", "idler", "roller", "bogie", "link", "rubber", "tire", "tyre" };

        /// Tracks, wheels, sprockets, idlers, rubber flaps: not plates, so no plate failure is drawn on them.
        public static bool IsRunningGear(Unit u, Collider col)
        {
            if (col == null) return false;
            string n = col.name.ToLowerInvariant();
            if (GearWords.Any(w => n.Contains(w)) && !n.Contains("skirt") && !n.Contains("fender") && !n.Contains("guard plate")) return true;
            var dc = col.GetComponent<DC>() ?? col.GetComponentInParent<DC>();
            if (dc != null)
            {
                string dn = (dc.Name ?? "").ToLowerInvariant();
                if (dn.Contains("track") || dn.Contains("wheel") || dn.Contains("sprocket")) return true;
            }
            return false;
        }

        /// A round on the running gear: sparks and a little scorch, and pieces that fit what was hit. A track throws a
        /// couple of links (more for a blast), a wheel or sprocket a few chips off its rim; nothing is holed.
        public static void RunningGearHit(Unit u, Collider col, Vector3 point, Vector3 normal, Vector3 dir, bool he, bool heat, bool pen, float tnt, float caliberM, int layer)
        {
            WorldDamage.EnsureInit();
            string n = col.name.ToLowerInvariant();
            bool track = n.Contains("track") || n.Contains("tread") || n.Contains("link");
            float blast = he ? Mathf.Clamp(Mathf.Pow(Mathf.Max(tnt, 0.05f), 0.33f), 0.4f, 3f) : heat ? 0.6f : 0.25f;
            // sparks off steel; a blast's fireball is the game's own
            Vector3 spray = Vector3.Lerp(normal, -dir, 0.4f).normalized;
            WorldDamage.Sparks(point - dir * 0.02f, spray, he ? 160 : 70, he ? 22f : 15f, 50f, layer);
            // scorch: a blast blackens the wheels / links around it, a KE hit only scuffs
            float soot = he ? 0.35f * blast : heat ? 0.18f : 0.08f;
            var q = Quaternion.LookRotation(-normal, Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up);
            var root = new GameObject("HitCam gear damage");
            root.transform.SetPositionAndRotation(point + normal * 0.01f, q);
            WorldDamage.Quad(root.transform, "soot", WorldDamage.SootDisc, Matrix4x4.Scale(new Vector3(soot * 2f, soot * 2f, 1f)), WorldDamage.SootMat, layer, new Color(1f, 1f, 1f, he ? 0.8f : 0.5f));
            root.AddComponent<StickTo>().Bind(col.transform);
            // the pieces: links are pin-jointed steel plates (pitch ~15 cm, the track's width); a wheel loses rim / rubber chips
            Bounds b = col.bounds;
            float width = Mathf.Clamp(Mathf.Min(b.size.x, b.size.z), 0.25f, 0.75f);
            int pieces = track ? (he ? Mathf.Clamp(Mathf.RoundToInt(2f * blast), 1, 5) : (pen ? 1 : 0)) : (he ? Mathf.Clamp(Mathf.RoundToInt(3f * blast), 2, 6) : 2);
            for (int i = 0; i < pieces; i++)
            {
                Vector3 v = (normal * UnityEngine.Random.Range(0.4f, 1f) + UnityEngine.Random.insideUnitSphere * 0.6f + Vector3.up * 0.3f).normalized
                          * UnityEngine.Random.Range(2f, he ? 9f * blast : 5f);
                Vector3 scale = track
                    ? new Vector3(width, 0.035f, 0.15f)                               // a link
                    : new Vector3(1f, 0.35f, 1.6f) * Mathf.Clamp(caliberM * 0.5f, 0.02f, 0.07f);   // a chip
                WorldDamage.SpawnPiece(track ? "HitCam track link" : "HitCam wheel chip", point + normal * 0.08f, UnityEngine.Random.rotationUniform, scale, v,
                                       UnityEngine.Random.insideUnitSphere * 12f, mass: track ? 18f : -1f);
            }
            if (AutoTest.Active) HitCamMod.Log.Msg($"World damage on {Recorder.SafeName(u)} ({col.name}): running gear, {(track ? "track" : "wheel")}, {pieces} pieces, no plate");
        }

        static readonly Dictionary<Unit, List<Collider>> _gear = new Dictionary<Unit, List<Collider>>();

        /// The unit's track / wheel hit zones (the game's DestructibleComponent colliders for them, and wheel colliders).
        static List<Collider> GearColliders(Unit u)
        {
            if (_gear.TryGetValue(u, out var l) && l.All(c => c != null)) return l;
            l = new List<Collider>();
            foreach (var root in WorldDamage.UnitRoots(u))
                foreach (var c in root.GetComponentsInChildren<Collider>(false))
                {
                    if (c == null || !c.enabled || c.isTrigger) continue;
                    var dc = c.GetComponent<DC>();
                    if (dc != null ? IsGearDc(dc) : (c.TryGetComponent<IArmor>(out _) && IsRunningGear(u, c))) l.Add(c);
                }
            _gear[u] = l;
            return l;
        }

        /// A plate that lies behind the tracks or wheels (the lower hull side behind the road wheels) is not seen from
        /// outside: the round met the running gear first on its way in, or the gear stands in front of the plate.
        /// `gear` / `at`: the gear the round itself went through, if it did.
        public static bool BehindGear(Unit u, Collider struck, Vector3 point, Vector3 dir, Vector3 normal, out Collider gear, out Vector3 at, out Vector3 atNormal)
        {
            gear = null; at = point; atNormal = normal;
            var list = GearColliders(u);
            if (list.Count == 0 || list.Contains(struck)) return false;
            for (int pass = 0; pass < 2; pass++)
            {
                Vector3 o = pass == 0 ? point - dir * 6f : point + normal * 4f, d = pass == 0 ? dir : -normal;
                float len = (point - o).magnitude - 0.01f;
                foreach (var c in list)
                {
                    if (!c.bounds.IntersectRay(new Ray(o, d), out float enter) || enter > len) continue;
                    if (c.Raycast(new Ray(o, d), out var h, len))
                    {
                        if (pass == 0) { gear = c; at = h.point; atNormal = h.normal; }
                        return true;
                    }
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ what the vehicles are made of (self-test)

        static readonly HashSet<Unit> _dumped = new HashSet<Unit>();

        static string Path(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var x = t; x != null && x != root; x = x.parent) parts.Add(x.name);
            if (root != null) parts.Add(root.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        /// Writes the unit's renderers, colliders, modules, guns, flammables and a GPU read-back test to UserData/HitCam (self-test only).
        static bool _dumpedAll;

        public static void Dump(Unit u)
        {
            if (!AutoTest.Opt("dump", true) || u == null) return;   // dump=false: timing runs (it writes every vehicle to disk)
            if (!_dumpedAll)
            {
                // the first time: every vehicle and aircraft in the scene
                _dumpedAll = true;
                try { foreach (var x in SceneUnitsManager.Instance.AllUnitsInScene.OfType<Unit>().ToList()) if (x != null && x.Type != UnitType.Infantry) DumpOne(x); }
                catch (Exception e) { HitCamMod.Log.Warning("World modules dump (all): " + e.Message); }
            }
            DumpOne(u);
        }

        static void DumpOne(Unit u)
        {
            if (u == null || !_dumped.Add(u)) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"UNIT {Recorder.SafeName(u)} type {u.GetType().FullName} pos {u.transform.position} rot {u.transform.eulerAngles}");
                var roots = WorldDamage.UnitRoots(u);
                foreach (var root in roots)
                {
                    sb.AppendLine("ROOT " + root.name);
                    foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                    {
                        Mesh m = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                        string bones = r is SkinnedMeshRenderer s2 ? $" bones {s2.bones?.Length ?? 0}" : "";
                        string mats = string.Join(",", r.sharedMaterials.Where(x => x != null).Select(x => x.name + "<" + (x.shader != null ? x.shader.name : "?") + ">"));
                        sb.AppendLine($"  R {r.GetType().Name} {(r.enabled && r.gameObject.activeInHierarchy ? "on" : "off")} L{r.gameObject.layer} {Path(r.transform, root)} mesh {(m != null ? $"{m.name} v{m.vertexCount} sub{m.subMeshCount} {(m.isReadable ? "readable" : "gpu")}" : "-")}{bones} c{r.bounds.center} s{r.bounds.size} [{mats}]");
                    }
                    foreach (var c in root.GetComponentsInChildren<Collider>(true))
                    {
                        string arm = c.TryGetComponent<IArmor>(out _) ? " ARMOR" : "";
                        var dc = c.GetComponent<DC>();
                        sb.AppendLine($"  C {c.GetType().Name}{(c.isTrigger ? " trigger" : "")}{(c.enabled ? "" : " off")} L{c.gameObject.layer} {Path(c.transform, root)}{arm}{(dc != null ? " DC=" + dc.Name : "")} c{c.bounds.center} s{c.bounds.size}");
                    }
                    foreach (var dc in root.GetComponentsInChildren<DC>(true))
                        sb.AppendLine($"  DC '{dc.Name}' {dc.HealthPercent:0}% at {Path(dc.transform, root)} col {(dc.ComponentCollision != null ? dc.ComponentCollision.name + " c" + dc.ComponentCollision.bounds.center + " s" + dc.ComponentCollision.bounds.size : "-")}");
                    foreach (var f in root.GetComponentsInChildren<GHPC.Effects.FlammableItem>(true))
                        sb.AppendLine($"  FLAM {f.name} explosive {f.Explosive} flame {f.FlameHeight:0.0} m at {f.transform.position}");
                    foreach (var cdm in root.GetComponentsInChildren<GHPC.Vehicle.ChassisDamageManager>(true))
                        sb.AppendLine($"  CHASSIS engine '{cdm.EngineHitZone?.Name}' L track '{cdm.LeftTrackHitZone?.Name}' R track '{cdm.RightTrackHitZone?.Name}' radiator '{cdm.RadiatorHitZone?.Name}'");
                    foreach (var hc in root.GetComponentsInChildren<GHPC.Vehicle.HelicopterController>(true))
                        sb.AppendLine($"  HELI main rotor {(hc.MainRotorVisual != null ? Path(hc.MainRotorVisual.transform, root) : "-")} animator {(hc.MainRotorAnimator != null ? Path(hc.MainRotorAnimator.transform, root) : "-")} tail zones {string.Join(", ", (hc.TailRotorHitZones ?? new DC[0]).Where(z => z != null).Select(z => z.Name + "@" + Path(z.transform, root)))}");
                    foreach (var ws in root.GetComponentsInChildren<WeaponSystem>(true))
                        sb.AppendLine($"  GUN {ws.name} barrel '{ws.BarrelHitZone?.Name}' {(ws.BarrelHitZone?.ComponentCollision != null ? "c" + ws.BarrelHitZone.ComponentCollision.bounds.center + " s" + ws.BarrelHitZone.ComponentCollision.bounds.size : "")} breech '{ws.BreechHitZone?.Name}' muzzle {(ws.MuzzleIdentity != null ? Path(ws.MuzzleIdentity, root) + " " + ws.MuzzleIdentity.position : "-")}");
                }
                // can a non-readable mesh be read back from the GPU (for cutting a barrel / a tail boom into pieces)?
                var probe = roots.SelectMany(r => r.GetComponentsInChildren<MeshFilter>(false)).FirstOrDefault(f => f.sharedMesh != null && !f.sharedMesh.isReadable);
                if (probe != null) sb.AppendLine("READBACK " + probe.sharedMesh.name + ": " + MeshReadback.Test(probe.sharedMesh));
                string file = System.IO.Path.Combine(HitCamMod.DataDir, "unitdump_" + string.Concat(Recorder.SafeName(u).Where(ch => char.IsLetterOrDigit(ch) || ch == '-')) + ".txt");
                File.WriteAllText(file, sb.ToString());
                HitCamMod.Log.Msg("World modules: dumped " + Recorder.SafeName(u) + " to " + file);
            }
            catch (Exception e) { HitCamMod.Log.Warning("World modules dump failed: " + e); }
        }
    }

    /// Reading a mesh the game keeps on the GPU only (non-readable) back into a CPU copy: its vertex streams through
    /// Mesh.GetVertexBuffer and its index buffer, decoded per attribute format.
    internal static class MeshReadback
    {
        public static string Test(Mesh m)
        {
            try
            {
                var attrs = m.GetVertexAttributes();
                string a = string.Join(" ", attrs.Select(x => $"{x.attribute}:{x.format}x{x.dimension}@s{x.stream}"));
                using (var gb = m.GetVertexBuffer(0))
                {
                    if (gb == null) return "no vertex buffer (" + a + ")";
                    var data = new byte[gb.count * gb.stride];
                    gb.GetData(data);
                    int nz = 0;
                    for (int i = 0; i < Mathf.Min(data.Length, 4096); i++) if (data[i] != 0) nz++;
                    using (var ib = m.GetIndexBuffer())
                    {
                        string ix = ib != null ? $", index buffer {ib.count} x {ib.stride} B" : ", no index buffer";
                        return $"ok: {gb.count} verts x {gb.stride} B, {nz} non-zero bytes in the first 4 kB{ix}; attributes {a}";
                    }
                }
            }
            catch (Exception e) { return "failed: " + e.GetType().Name + " " + e.Message; }
        }
    }
}
