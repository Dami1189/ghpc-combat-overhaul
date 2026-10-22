using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GHPC.Humans;
using GHPC.Weaponry;
using GHPC.Weapons;
using UnityEngine;
using DC = GHPC.Equipment.DestructibleComponent;

namespace GHPCHitCam
{
    // ------------------------------------------------------------------ vehicle as boxes

    /// one primitive in target-local space (0 cube, 1 sphere, 2 capsule; matrix scales the unit primitive)
    [Serializable] internal class AarShape { public int prim; public Vector4 c0, c1, c2, c3; }
    [Serializable] internal class AarModule { public string name; public string cat; public float hp = 100f; public List<AarShape> shapes = new List<AarShape>(); [NonSerialized] public DC comp; }
    [Serializable] internal class AarCrewPart { public string part; public AarShape shape; [NonSerialized] public DC comp; }
    [Serializable] internal class AarCrew { public string label; public string status; public List<AarCrewPart> parts = new List<AarCrewPart>(); }
    [Serializable] internal class AarAmmo { public List<AarShape> shapes = new List<AarShape>(); [NonSerialized] public AmmoVisual visual; }

    /// x-ray model cut down to primitives: every hull part as its bounding box, module/crew hit zones, ammo
    /// small (a few KB of JSON), owns no game meshes, so it survives destroyed vehicles, scene unloads and restarts
    [Serializable]
    internal class AarSchematic
    {
        public string unit;
        public Vector3 boundsCenter, boundsSize;
        public List<AarShape> hull = new List<AarShape>();
        public List<AarModule> modules = new List<AarModule>();
        public List<AarCrew> crew = new List<AarCrew>();
        public List<AarAmmo> ammo = new List<AarAmmo>();
    }

    /// what a hit's JSON keeps of the vehicle (see Schematic.ToDto)
    [Serializable]
    internal class AarBoxesDto
    {
        public string unit;
        public Vector3 center, size;
        public List<float> hull = new List<float>();
        public List<string> modName = new List<string>(), modCat = new List<string>();
        public List<float> modHp = new List<float>();
        public List<int> modCount = new List<int>();
        public List<float> modShapes = new List<float>();
        public List<string> crewLabel = new List<string>(), crewStatus = new List<string>(), crewPart = new List<string>();
        public List<int> crewCount = new List<int>();
        public List<float> crewShapes = new List<float>();
        public List<int> ammoCount = new List<int>();
        public List<float> ammoShapes = new List<float>();
    }

    internal static class Schematic
    {
        const int Cube = 0, Sphere = 1, Capsule = 2, MaxHull = 90;

        public static AarSchematic Capture(GhostSnapshot s)
        {
            if (s == null) return null;
            var sc = new AarSchematic { unit = s.UnitName, boundsCenter = s.LocalBounds.center, boundsSize = s.LocalBounds.size };
            // hull: biggest parts first (bolts, tools, tow cables don't matter in a schematic)
            var parts = new List<(AarShape sh, float vol)>();
            var fit = s.LocalBounds;
            fit.Expand(1.5f);
            foreach (var p in s.Exterior)
            {
                var sh = Shape(p);
                if (sh == null) continue;
                float v = Mathf.Abs(Mat(sh).determinant);
                var box = Aabb(Mat(sh));
                // box way bigger than the vehicle (stale bounds, effect volumes) would swallow the picture
                if (v > 2e-4f && fit.Contains(box.min) && fit.Contains(box.max)) parts.Add((sh, v));
            }
            foreach (var x in parts.OrderByDescending(x => x.vol).Take(MaxHull)) sc.hull.Add(x.sh);
            foreach (var mp in s.Modules)
            {
                var m = new AarModule { name = mp.Name, cat = mp.Cat.ToString(), hp = mp.InitialHealth, comp = mp.Comp };
                foreach (var p in mp.Shapes) { var sh = Shape(p); if (sh != null) m.shapes.Add(sh); }
                sc.modules.Add(m);
            }
            foreach (var ci in s.Crew)
            {
                var c = new AarCrew { label = ci.Label, status = ci.InitialStatus.ToString() };
                foreach (var cp in ci.Parts) { var sh = Shape(cp.Shape); if (sh != null) c.parts.Add(new AarCrewPart { part = cp.Part.ToString(), shape = sh, comp = cp.Comp }); }
                sc.crew.Add(c);
            }
            foreach (var ap in s.Ammo)
            {
                var a = new AarAmmo { visual = ap.Visual };
                foreach (var p in ap.Shapes) { var sh = Shape(p); if (sh != null) a.shapes.Add(sh); }
                if (a.shapes.Count > 0) sc.ammo.Add(a);
            }
            return sc;
        }

        static AarShape Shape(MeshPart p)
        {
            if (p == null || p.Mesh == null) return null;   // unity null: destroyed since the snapshot
            try
            {
                int prim = p.Mesh == Prims.Cube ? Cube : p.Mesh == Prims.Sphere ? Sphere : p.Mesh == Prims.Capsule ? Capsule : -1;
                Matrix4x4 m = p.M;
                if (prim < 0)
                {
                    if (p.OwnsMesh) p.Mesh.RecalculateBounds();   // baked skinned meshes keep the renderer's (often huge) bounds
                    var b = p.Mesh.bounds;
                    m = p.M * Matrix4x4.TRS(b.center, Quaternion.identity, b.size);
                    prim = Cube;
                }
                return new AarShape { prim = prim, c0 = m.GetColumn(0), c1 = m.GetColumn(1), c2 = m.GetColumn(2), c3 = m.GetColumn(3) };
            }
            catch { return null; }
        }

        static Matrix4x4 Mat(AarShape s) => new Matrix4x4(s.c0, s.c1, s.c2, s.c3);

        /// AABB of a unit cube transformed by m
        static Bounds Aabb(Matrix4x4 m)
        {
            var b = new Bounds(m.MultiplyPoint3x4(Vector3.zero), Vector3.zero);
            for (int i = 0; i < 8; i++)
                b.Encapsulate(m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? -0.5f : 0.5f, (i & 2) == 0 ? -0.5f : 0.5f, (i & 4) == 0 ? -0.5f : 0.5f)));
            return b;
        }

        // ---- JSON form: flat lists of numbers and strings (13 floats per box), compact in the hit files
        const int Floats = 13;   // prim + 3x4 affine matrix, row by row

        static void Put(List<float> to, AarShape s)
        {
            var m = Mat(s);
            to.Add(s.prim);
            for (int r = 0; r < 3; r++) for (int c = 0; c < 4; c++) to.Add(m[r, c]);
        }

        static AarShape Get(List<float> from, int i)
        {
            int o = i * Floats;
            var m = Matrix4x4.identity;
            for (int r = 0; r < 3; r++) for (int c = 0; c < 4; c++) m[r, c] = from[o + 1 + r * 4 + c];
            return new AarShape { prim = Mathf.RoundToInt(from[o]), c0 = m.GetColumn(0), c1 = m.GetColumn(1), c2 = m.GetColumn(2), c3 = m.GetColumn(3) };
        }

        public static AarBoxesDto ToDto(AarSchematic sc)
        {
            var d = new AarBoxesDto();
            if (sc == null) return d;
            d.unit = sc.unit; d.center = sc.boundsCenter; d.size = sc.boundsSize;
            foreach (var h in sc.hull) Put(d.hull, h);
            foreach (var m in sc.modules) { d.modName.Add(m.name); d.modCat.Add(m.cat); d.modHp.Add(m.hp); d.modCount.Add(m.shapes.Count); foreach (var sh in m.shapes) Put(d.modShapes, sh); }
            foreach (var c in sc.crew) { d.crewLabel.Add(c.label); d.crewStatus.Add(c.status); d.crewCount.Add(c.parts.Count); foreach (var p in c.parts) { d.crewPart.Add(p.part); Put(d.crewShapes, p.shape); } }
            foreach (var a in sc.ammo) { d.ammoCount.Add(a.shapes.Count); foreach (var sh in a.shapes) Put(d.ammoShapes, sh); }
            return d;
        }

        public static AarSchematic FromDto(AarBoxesDto d)
        {
            if (d == null || (d.hull.Count == 0 && d.modName.Count == 0 && d.crewLabel.Count == 0)) return null;
            var sc = new AarSchematic { unit = d.unit, boundsCenter = d.center, boundsSize = d.size };
            for (int i = 0; i < d.hull.Count / Floats; i++) sc.hull.Add(Get(d.hull, i));
            int k = 0;
            for (int i = 0; i < d.modName.Count && i < d.modCount.Count; i++)
            {
                var m = new AarModule { name = d.modName[i], cat = i < d.modCat.Count ? d.modCat[i] : "Other", hp = i < d.modHp.Count ? d.modHp[i] : 100f };
                for (int j = 0; j < d.modCount[i] && (k + 1) * Floats <= d.modShapes.Count; j++) m.shapes.Add(Get(d.modShapes, k++));
                sc.modules.Add(m);
            }
            k = 0;
            for (int i = 0; i < d.crewLabel.Count && i < d.crewCount.Count; i++)
            {
                var c = new AarCrew { label = d.crewLabel[i], status = i < d.crewStatus.Count ? d.crewStatus[i] : "Fine" };
                for (int j = 0; j < d.crewCount[i] && (k + 1) * Floats <= d.crewShapes.Count; j++) { c.parts.Add(new AarCrewPart { part = k < d.crewPart.Count ? d.crewPart[k] : "Torso", shape = Get(d.crewShapes, k) }); k++; }
                sc.crew.Add(c);
            }
            k = 0;
            foreach (int n in d.ammoCount)
            {
                var a = new AarAmmo();
                for (int j = 0; j < n && (k + 1) * Floats <= d.ammoShapes.Count; j++) a.shapes.Add(Get(d.ammoShapes, k++));
                sc.ammo.Add(a);
            }
            return sc;
        }

        static MeshPart Part(AarShape s, string name) =>
            new MeshPart { Mesh = s.prim == Sphere ? Prims.Sphere : s.prim == Capsule ? Prims.Capsule : Prims.Cube, M = Mat(s), Name = name };

        /// ghost snapshot built from boxes. live schematics keep their game refs (modules, crew parts, ammo) so the hit's
        /// events still find what they hit, archived ones get matched by name later
        public static GhostSnapshot ToSnapshot(AarSchematic sc, Bounds fallback)
        {
            var s = new GhostSnapshot { UnitName = sc?.unit ?? "?" };
            Bounds b = sc != null && sc.boundsSize.sqrMagnitude > 0.01f ? new Bounds(sc.boundsCenter, sc.boundsSize) : fallback;
            if (b.size.sqrMagnitude < 0.01f) b = new Bounds(new Vector3(0f, 1.2f, 0f), new Vector3(3.5f, 2.4f, 7f));
            s.LocalBounds = b;
            if (sc == null || sc.hull == null || sc.hull.Count == 0)
            {
                // only the bounds: one box hull (bounds were grown by 0.5 m around the hit zones)
                var size = Vector3.Max(b.size - Vector3.one * 0.8f, b.size * 0.6f);
                s.Exterior.Add(new MeshPart { Mesh = Prims.Cube, M = Matrix4x4.TRS(b.center, Quaternion.identity, size), Name = "hull box" });
            }
            if (sc == null) return s;
            foreach (var h in sc.hull) s.Exterior.Add(Part(h, "hull"));
            foreach (var m in sc.modules ?? new List<AarModule>())
            {
                var mp = new ModulePart { Comp = m.comp, Name = m.name, Cat = Parse(m.cat, ModCat.Other), InitialHealth = m.hp };
                foreach (var sh in m.shapes ?? new List<AarShape>()) mp.Shapes.Add(Part(sh, m.name));
                s.Modules.Add(mp);
                if (!ReferenceEquals(m.comp, null)) s.ModuleOf[m.comp] = mp;
            }
            foreach (var c in sc.crew ?? new List<AarCrew>())
            {
                var ci = new CrewInfo { Label = c.label, InitialStatus = Parse(c.status, HealthStatus.Fine) };
                foreach (var p in c.parts ?? new List<AarCrewPart>())
                {
                    if (p.shape == null) continue;
                    ci.AddPart(Part(p.shape, c.label + " " + p.part), Parse(p.part, BodyPart.Torso), p.comp);
                    if (!ReferenceEquals(p.comp, null)) s.CrewOfPart[p.comp] = ci;
                }
                ci.BuildSkeleton();
                s.Crew.Add(ci);
            }
            foreach (var a in sc.ammo ?? new List<AarAmmo>())
            {
                var ap = new AmmoPart { Visual = a.visual };
                foreach (var sh in a.shapes ?? new List<AarShape>()) ap.Shapes.Add(Part(sh, "round"));
                s.Ammo.Add(ap);
            }
            return s;
        }

        static T Parse<T>(string s, T d) where T : struct
        {
            if (string.IsNullOrEmpty(s)) return d;
            return Enum.TryParse(s, true, out T v) ? v : d;
        }

        /// true when a game mesh the snapshot borrowed got destroyed (vehicle gone, scene unloaded)
        public static bool Broken(GhostSnapshot s)
        {
            if (s == null) return true;
            bool Dead(MeshPart p) => p != null && !ReferenceEquals(p.Mesh, null) && p.Mesh == null;
            if (s.Exterior.Any(Dead)) return true;
            if (s.Modules.Any(m => m.Shapes.Any(Dead))) return true;
            if (s.Crew.Any(c => c.Shapes.Any(Dead) || c.Visuals.Any(Dead))) return true;
            return s.Ammo.Any(a => a.Shapes.Any(Dead));
        }

        /// destroys the meshes the snapshot baked itself (crew poses, skinned parts), game meshes are left alone
        public static int Release(GhostSnapshot s)
        {
            if (s == null) return 0;
            int verts = 0;
            void Kill(MeshPart p)
            {
                if (p == null || !p.OwnsMesh || p.Mesh == null) return;
                verts += p.Mesh.vertexCount;
                UnityEngine.Object.Destroy(p.Mesh);
            }
            foreach (var p in s.Exterior) Kill(p);
            foreach (var p in s.Fixtures) Kill(p);
            foreach (var mp in s.Modules) foreach (var p in mp.Detail) Kill(p);
            foreach (var c in s.Crew) { foreach (var p in c.Visuals) Kill(p); foreach (var p in c.Shapes) Kill(p); }
            foreach (var a in s.Ammo) foreach (var p in a.Shapes) Kill(p);
            return verts;
        }

        public static int OwnedVertices(GhostSnapshot s)
        {
            if (s == null) return 0;
            int n = 0;
            void Add(MeshPart p) { if (p != null && p.OwnsMesh && p.Mesh != null) n += p.Mesh.vertexCount; }
            foreach (var p in s.Exterior) Add(p);
            foreach (var p in s.Fixtures) Add(p);
            foreach (var mp in s.Modules) foreach (var p in mp.Detail) Add(p);
            foreach (var c in s.Crew) { foreach (var p in c.Visuals) Add(p); foreach (var p in c.Shapes) Add(p); }
            foreach (var a in s.Ammo) foreach (var p in a.Shapes) Add(p);
            return n;
        }
    }


    /// JSON for the hit files. unity's JsonUtility silently drops every nested class (fragments, armor hits, events were
    /// never in the saved files), so it goes through Newtonsoft.Json which the game ships
    internal static class AarJson
    {
        static Newtonsoft.Json.JsonSerializerSettings _settings;

        static Newtonsoft.Json.JsonSerializerSettings Settings => _settings ??= new Newtonsoft.Json.JsonSerializerSettings
        {
            Formatting = Newtonsoft.Json.Formatting.Indented,
            ReferenceLoopHandling = Newtonsoft.Json.ReferenceLoopHandling.Ignore,
            Converters = { new VecConverter() },
        };

        public static string Write(object o) => Newtonsoft.Json.JsonConvert.SerializeObject(o, Settings);

        public static T Read<T>(string json) => Newtonsoft.Json.JsonConvert.DeserializeObject<T>(json, Settings);

        /// vectors as {x, y, z(, w)}: default contract would walk normalized / magnitude
        class VecConverter : Newtonsoft.Json.JsonConverter
        {
            public override bool CanConvert(Type t) => t == typeof(Vector2) || t == typeof(Vector3) || t == typeof(Vector4);

            public override void WriteJson(Newtonsoft.Json.JsonWriter w, object value, Newtonsoft.Json.JsonSerializer s)
            {
                Vector4 v = value is Vector3 v3 ? (Vector4)v3 : value is Vector2 v2 ? (Vector4)v2 : (Vector4)value;
                w.WriteStartObject();
                w.WritePropertyName("x"); w.WriteValue(v.x);
                w.WritePropertyName("y"); w.WriteValue(v.y);
                if (!(value is Vector2)) { w.WritePropertyName("z"); w.WriteValue(v.z); }
                if (value is Vector4) { w.WritePropertyName("w"); w.WriteValue(v.w); }
                w.WriteEndObject();
            }

            public override object ReadJson(Newtonsoft.Json.JsonReader r, Type t, object existing, Newtonsoft.Json.JsonSerializer s)
            {
                if (r.TokenType == Newtonsoft.Json.JsonToken.Null) return Activator.CreateInstance(t);
                var o = Newtonsoft.Json.Linq.JObject.Load(r);
                float F(string k) { var tok = o[k]; return tok == null || tok.Type == Newtonsoft.Json.Linq.JTokenType.Null ? 0f : tok.ToObject<float>(); }
                if (t == typeof(Vector2)) return new Vector2(F("x"), F("y"));
                if (t == typeof(Vector3)) return new Vector3(F("x"), F("y"), F("z"));
                return new Vector4(F("x"), F("y"), F("z"), F("w"));
            }
        }
    }

    // ------------------------------------------------------------------ past missions (JSON written by Archive.Save)

    [Serializable]
    internal class AarMissionDto
    {
        public string name, started, ended, result;
        public float duration;
        public int shots, incoming, crewLostFriendly = -1, crewLostEnemy = -1, unitsLostFriendly = -1, unitsLostEnemy = -1;
        public List<string> ammoNames = new List<string>();
        public List<int> ammoShots = new List<int>();
        public List<int> killIds = new List<int>(), koIds = new List<int>(), friendlyIds = new List<int>();
    }

#pragma warning disable CS0649   // JSON DTOs, filled by Newtonsoft.Json
    internal static class AarArchive
    {
        [Serializable] class PointIn { public Vector3 p; public float t; public bool impact; public bool jet; }
        [Serializable] class FragIn { public int id; public int parent; public string kind; public float power; public Vector3 start; public float startT; public List<PointIn> points = new List<PointIn>(); public bool hit; public bool damaging; public float speed; }
        [Serializable]
        class ArmorIn
        {
            public string name; public Vector3 p; public Vector3 normal; public Vector3 dir; public float los; public float nominal; public float pen; public float angle;
            public bool penetrated; public bool ricochet; public bool era; public float hole; public float t;
            public int frag; public float energy, speed, caliber, rod, tnt; public string category; public bool spall, preJet;
        }
        [Serializable]
        class EventIn
        {
            public string kind; public string text; public string subject; public Vector3 p; public float t; public float realDelay; public float before; public float after; public int severity;
            public int frag; public bool noPos; public float speed, power, mass; public bool spallHit, jetHit, hasDir; public Vector3 dir;
        }
        [Serializable]
        class HitIn
        {
            public int id; public string time; public string title; public string shooter; public string target; public bool playerShot;
            public string ammo; public string ammoCategory; public float caliber; public float penetration; public float distance;
            public Vector3 entry; public Vector3 entryDir; public float interiorEnd; public float approachStart; public float coreEnd;
            public Vector3 boundsCenter; public Vector3 boundsSize;
            public bool onPlayer, killed, knockedOut; public float missionTime, ammoMass, ammoArea, ammoTnt, ammoSpeed;
            public List<FragIn> fragments = new List<FragIn>();
            public List<ArmorIn> armor = new List<ArmorIn>();
            public List<EventIn> events = new List<EventIn>();
            public AarBoxesDto boxes;
        }

        public class Session { public string Dir, Name, When; public int Hits; public AarMissionDto Info; }

        /// mission folders, newest first
        public static List<Session> Sessions()
        {
            var list = new List<Session>();
            try
            {
                foreach (var d in Directory.GetDirectories(HitCamMod.DataDir, "hits_*").OrderByDescending(x => x))
                {
                    int n = Directory.GetFiles(d, "hit_*.json").Length;
                    var s = new Session { Dir = d, Hits = n, When = Path.GetFileName(d).Substring(5).Replace('_', ' ') };
                    string mi = Path.Combine(d, "mission.json");
                    if (File.Exists(mi)) { try { s.Info = AarJson.Read<AarMissionDto>(File.ReadAllText(mi)); } catch { } }
                    s.Name = !string.IsNullOrEmpty(s.Info?.name) ? s.Info.name : "(mission name not saved)";
                    if (n > 0 || s.Info != null) list.Add(s);
                }
            }
            catch (Exception e) { HitCamMod.Log.Warning("AAR: could not list past missions: " + e.Message); }
            return list;
        }

        static Dictionary<string, AmmoType> _ammo;
        static int _noPaths;

        static AmmoType FindAmmo(HitIn d)
        {
            if (_ammo == null)
            {
                _ammo = new Dictionary<string, AmmoType>();
                try
                {
                    foreach (var a in Resources.FindObjectsOfTypeAll<AmmoCodexScriptable>())
                        if (a != null && a.AmmoType != null && !string.IsNullOrEmpty(a.AmmoType.Name) && !_ammo.ContainsKey(a.AmmoType.Name)) _ammo[a.AmmoType.Name] = a.AmmoType;
                }
                catch { }
            }
            if (!string.IsNullOrEmpty(d.ammo) && _ammo.TryGetValue(d.ammo, out var at)) return at;
            // not loaded right now (other theatre / main menu): stand-in with the saved numbers
            var cat = Enum.TryParse(d.ammoCategory, out AmmoType.AmmoCategory c) ? c : AmmoType.AmmoCategory.Penetrator;
            var t = new AmmoType { Name = d.ammo ?? "?", Category = cat, Caliber = d.caliber, RhaPenetration = d.penetration, TntEquivalentKg = d.ammoTnt };
            if (d.ammoMass > 0f) t.Mass = d.ammoMass;
            if (d.ammoArea > 0f) t.SectionalArea = d.ammoArea;
            else if (d.caliber > 0f) t.SectionalArea = Mathf.PI * Mathf.Pow(d.caliber * 0.0005f * (cat == AmmoType.AmmoCategory.Penetrator && d.penetration > d.caliber * 2.5f ? 0.3f : 1f), 2f);
            if (d.ammoSpeed > 0f) t.MuzzleVelocity = d.ammoSpeed;
            return t;
        }

        public static AarMission Load(Session s)
        {
            var m = new AarMission { Name = s.Name, Folder = s.Dir, Archived = true, Info = s.Info };
            _noPaths = 0;
            if (s.Info != null)
            {
                m.Shots = s.Info.shots; m.Incoming = s.Info.incoming; m.Result = s.Info.result; m.ArchivedDuration = s.Info.duration;
                m.CrewLostFriendly = s.Info.crewLostFriendly; m.CrewLostEnemy = s.Info.crewLostEnemy;
                m.UnitsLostFriendly = s.Info.unitsLostFriendly; m.UnitsLostEnemy = s.Info.unitsLostEnemy;
                for (int i = 0; i < Mathf.Min(s.Info.ammoNames.Count, s.Info.ammoShots.Count); i++) m.ShotsByAmmo[s.Info.ammoNames[i]] = s.Info.ammoShots[i];
            }
            foreach (var f in Directory.GetFiles(s.Dir, "hit_*.json").OrderBy(x => x))
            {
                try
                {
                    var d = AarJson.Read<HitIn>(File.ReadAllText(f));
                    var e = Build(d);
                    if (e != null) m.Entries.Add(e);
                }
                catch (Exception ex) { HitCamMod.Log.Warning("AAR: skipped " + Path.GetFileName(f) + ": " + ex.Message); }
            }
            if (s.Info != null)
                foreach (var e in m.Entries)
                {
                    e.CreditKill = s.Info.killIds != null && s.Info.killIds.Contains(e.Rec.Id);
                    e.CreditKo = s.Info.koIds != null && s.Info.koIds.Contains(e.Rec.Id);
                    e.Friendly = s.Info.friendlyIds != null && s.Info.friendlyIds.Contains(e.Rec.Id);
                }
            m.Unplayable = _noPaths;
            // old files have no mission time: keep file order
            if (m.Entries.All(x => x.MissionTime <= 0f)) for (int i = 0; i < m.Entries.Count; i++) m.Entries[i].MissionTime = -1f;
            return m;
        }

        static AarEntry Build(HitIn d)
        {
            if (d == null || d.fragments == null || d.fragments.Count == 0) { _noPaths++; return null; }
            var ammo = FindAmmo(d);
            var rec = new HitRecord
            {
                Id = d.id, Ammo = ammo, PlayerShot = d.playerShot, ShooterName = d.shooter ?? "?", TargetName = d.target ?? "?",
                FirstImpactReal = 0f, W2LAtImpact = Matrix4x4.identity, L2WAtImpact = Matrix4x4.identity,
                InteriorEnd = d.interiorEnd > 0f ? d.interiorEnd : 0.01f, ApproachStart = d.approachStart < 0f ? d.approachStart : -0.005f,
                EntryL = d.entry, EntryDirL = d.entryDir.sqrMagnitude > 0.01f ? d.entryDir.normalized : Vector3.forward, HasEntry = true,
                TargetKilled = d.killed, TargetKnockedOut = d.knockedOut, Ready = true, Saved = true,
            };
            var bounds = new Bounds(d.boundsCenter, d.boundsSize);
            var schem = Schematic.FromDto(d.boxes);
            rec.Snapshot = Schematic.ToSnapshot(schem, bounds);
            var byId = new Dictionary<int, Fragment>();
            foreach (var fd in d.fragments)
            {
                var f = new Fragment
                {
                    Id = fd.id, Kind = fd.kind == "Main" ? FragKind.Main : FragKind.Spall, Power = fd.power, StartL = fd.start, HasStart = true,
                    StartSim = fd.startT, HitSomething = fd.hit, Damaging = fd.damaging, Terminated = true, StartSpeed = fd.speed,
                    Caliber = fd.kind == "Main" ? d.caliber : 0f,
                };
                foreach (var p in fd.points ?? new List<PointIn>())
                    f.Points.Add(new PathPoint { W = p.p, L = p.p, HasL = true, Sim = p.t, Impact = p.impact, Jet = p.jet, Speed = -1f });
                f.EndSim = f.Points.Count > 0 ? f.Points[f.Points.Count - 1].Sim : f.StartSim;
                byId[f.Id] = f;
                rec.Fragments.Add(f);
                if (f.Kind == FragKind.Main && rec.Main == null) rec.Main = f;
            }
            if (rec.Main == null) { rec.Main = rec.Fragments[0]; rec.Main.Kind = FragKind.Main; }
            if (rec.Main.StartSpeed <= 50f) rec.Main.StartSpeed = ammo.MuzzleVelocity;
            foreach (var fd in d.fragments)
                if (fd.parent != 0 && byId.TryGetValue(fd.id, out var f) && byId.TryGetValue(fd.parent, out var par)) f.Parent = par;
            foreach (var f in rec.Fragments) if (f.Kind == FragKind.Spall && f.Parent == null) f.Parent = rec.Main;

            float cal = Mathf.Max(d.caliber, 1f) * 0.001f;
            foreach (var a in d.armor ?? new List<ArmorIn>())
            {
                var cat = Enum.TryParse(a.category, out AmmoType.AmmoCategory c) ? c : ammo.Category;
                float speed = a.speed > 1f ? a.speed : (a.spall ? 900f : Mathf.Max(ammo.MuzzleVelocity, 300f));
                var ah = new ArmorHit
                {
                    Frag = byId.TryGetValue(a.frag, out var f) ? f : rec.Main, Name = a.name, L = a.p, NormalL = a.normal, DirL = a.dir,
                    Los = a.los, Nominal = a.nominal, Pen = a.pen, Angle = a.angle, Penetrated = a.penetrated, Ricochet = a.ricochet, Era = a.era,
                    HoleRadius = a.hole, Sim = a.t, Category = cat, IsSpall = a.spall, PreJet = a.preJet, Speed = speed,
                    Caliber = a.caliber > 0f ? a.caliber : cal, RodRadius = a.rod > 0f ? a.rod : Mathf.Sqrt(Mathf.Max(ammo.SectionalArea, 1e-6f) / Mathf.PI),
                    Tnt = a.tnt > 0f ? a.tnt : ammo.TntEquivalentKg,
                };
                ah.Energy = a.energy > 0f ? a.energy
                    : cat == AmmoType.AmmoCategory.ShapedCharge ? 400f * Mathf.Max(a.pen, 1f)
                    : cat == AmmoType.AmmoCategory.Explosive ? Mathf.Max(ah.Tnt, 0.01f) * 4.184e6f * 0.15f
                    : 0.5f * Mathf.Max(ammo.Mass, 0.01f) * speed * speed;
                rec.ArmorHits.Add(ah);
            }

            var snap = rec.Snapshot;
            foreach (var ed in d.events ?? new List<EventIn>())
            {
                if (!Enum.TryParse(ed.kind, out EvKind kind)) continue;
                var e = new HitEvent
                {
                    Kind = kind, Text = ed.text ?? "", Subject = ed.subject, L = ed.p, HasPos = !ed.noPos && (ed.p.sqrMagnitude > 1e-6f || kind == EvKind.Module),
                    Sim = ed.t <= -0.5f ? float.PositiveInfinity : ed.t, RealT = ed.realDelay, Before = ed.before, After = ed.after, Severity = ed.severity,
                    Frag = byId.TryGetValue(ed.frag, out var f) ? f : null, Speed = ed.speed, Power = ed.power, Mass = ed.mass,
                    Spall = ed.spallHit, Jet = ed.jetHit, HasDir = ed.hasDir, DirL = ed.dir,
                };
                switch (kind)
                {
                    case EvKind.Module: e.Ref = snap.Modules.FirstOrDefault(x => x.Name == e.Subject); break;
                    case EvKind.Crew: e.Ref = snap.Crew.FirstOrDefault(x => x.Label == e.Subject); break;
                    case EvKind.CrewHit:
                        var ci = snap.Crew.FirstOrDefault(x => x.Label == e.Subject);
                        if (ci != null) e.Ref = new CrewHitRef { Crew = ci };
                        break;
                    case EvKind.AmmoHit: e.Ref = Nearest(snap.Ammo, e.L) ?? (object)"no rack"; break;
                }
                rec.Events.Add(e);
            }
            rec.CoreEnd = d.coreEnd > 0f ? d.coreEnd : CoreEnd(rec, bounds);
            rec.TargetKilled |= rec.Events.Any(e => e.Kind == EvKind.Kill);
            rec.TargetKnockedOut |= rec.Events.Any(e => e.Kind == EvKind.Knockout);
            Timeline.ComputeTitle(rec);
            return new AarEntry
            {
                Rec = rec, Archived = true, OnPlayer = d.onPlayer, MissionTime = d.missionTime, Distance = d.distance,
                Schem = schem, SchemSnap = snap, StampText = d.time,
            };
        }

        static AmmoPart Nearest(List<AmmoPart> ammo, Vector3 l)
        {
            AmmoPart best = null;
            float bd = 0.6f * 0.6f;
            foreach (var ap in ammo)
                foreach (var sh in ap.Shapes)
                {
                    float d = (sh.M.MultiplyPoint3x4(Vector3.zero) - l).sqrMagnitude;
                    if (d < bd) { bd = d; best = ap; }
                }
            return best;
        }

        /// same rule as Timeline.Build: main round's time inside the vehicle gets most of the slow-mo
        static float CoreEnd(HitRecord rec, Bounds b)
        {
            b.Expand(1f);
            float inside = 0f;
            foreach (var p in rec.Main.Points) if (p.Sim >= 0f && b.Contains(p.L)) inside = Mathf.Max(inside, p.Sim);
            return Mathf.Clamp(Mathf.Max(inside * 1.6f, 0.0025f), 0.0015f, rec.InteriorEnd);
        }
    }
}
