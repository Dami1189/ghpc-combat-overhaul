using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GHPCHitCam
{
    /// saves every hit as JSON (target-local paths, armor hits, timed events) to replay / look at later
    internal static class Archive
    {
        static string _session;

        [Serializable] class PointDto { public Vector3 p; public float t; public bool impact; public bool jet; }
        [Serializable] class FragDto { public int id; public int parent; public string kind; public float power; public Vector3 start; public float startT; public List<PointDto> points = new List<PointDto>(); public bool hit; public bool damaging; public float speed; }
        [Serializable] class ArmorDto { public string name; public Vector3 p; public Vector3 normal; public Vector3 dir; public float los; public float nominal; public float pen; public float angle; public bool penetrated; public bool ricochet; public bool era; public float hole; public float t;
            public int frag; public float energy, speed, caliber, rod, tnt; public string category; public bool spall, preJet; }
        [Serializable] class EventDto { public string kind; public string text; public string subject; public Vector3 p; public float t; public float realDelay; public float before; public float after; public int severity;
            public int frag; public bool noPos; public float speed, power, mass; public bool spallHit, jetHit, hasDir; public Vector3 dir; }
        [Serializable] class HitDto
        {
            public int id; public string time; public string title; public string shooter; public string target; public bool playerShot;
            public string ammo; public string ammoCategory; public float caliber; public float penetration; public float distance;
            public Vector3 entry; public Vector3 entryDir; public float interiorEnd; public float approachStart;
            public Vector3 boundsCenter; public Vector3 boundsSize;
            // after action report (AAR): enough to replay the hit later on a box model of the vehicle
            public float coreEnd, missionTime, ammoMass, ammoArea, ammoTnt, ammoSpeed; public bool onPlayer, killed, knockedOut;
            public List<FragDto> fragments = new List<FragDto>();
            public List<ArmorDto> armor = new List<ArmorDto>();
            public List<EventDto> events = new List<EventDto>();
            public AarBoxesDto boxes;   // vehicle as boxes (flat lists), last so a problem here can't cost the fields above
        }

        /// hits only go to disk for past missions someone wants to keep, the report shows this mission and the last one
        /// from memory (self-test checks the files so it always writes them)
        public static bool Keeping => Settings.SaveRecords.Value && (Settings.PastMissionsKept.Value > 0 || AutoTest.Active);

        public static void Save(HitRecord rec)
        {
            if (rec.Saved || !Keeping) return;
            rec.Saved = true;
            try
            {
                SessionDir(true);
                var d = new HitDto
                {
                    id = rec.Id,
                    time = DateTime.Now.ToString("s"),
                    title = rec.Title,
                    shooter = rec.ShooterName,
                    target = rec.TargetName,
                    playerShot = rec.PlayerShot,
                    ammo = rec.Ammo?.Name,
                    ammoCategory = rec.Ammo != null ? rec.Ammo.Category.ToString() : "",
                    caliber = rec.Ammo != null ? rec.Ammo.Caliber : 0,
                    penetration = rec.Ammo != null ? rec.Ammo.RhaPenetration : 0,
                    distance = rec.RootShot != null ? rec.RootShot.Distance : -1,
                    entry = rec.EntryL,
                    entryDir = rec.EntryDirL,
                    interiorEnd = rec.InteriorEnd,
                    approachStart = rec.ApproachStart,
                    boundsCenter = rec.Snapshot != null ? rec.Snapshot.LocalBounds.center : Vector3.zero,
                    boundsSize = rec.Snapshot != null ? rec.Snapshot.LocalBounds.size : Vector3.zero,
                    coreEnd = rec.CoreEnd, killed = rec.TargetKilled, knockedOut = rec.TargetKnockedOut,
                    ammoMass = rec.Ammo != null ? rec.Ammo.Mass : 0, ammoArea = rec.Ammo != null ? rec.Ammo.SectionalArea : 0,
                    ammoTnt = rec.Ammo != null ? rec.Ammo.TntEquivalentKg : 0, ammoSpeed = rec.Ammo != null ? rec.Ammo.MuzzleVelocity : 0,
                    boxes = Schematic.ToDto(AfterAction.SchematicOf(rec)),
                };
                var aar = AfterAction.EntryOf(rec);
                if (aar != null) { d.onPlayer = aar.OnPlayer; d.missionTime = aar.MissionTime; }
                foreach (var f in rec.Fragments)
                {
                    var fd = new FragDto { id = f.Id, parent = f.Parent?.Id ?? 0, kind = f.Kind.ToString(), power = f.Power, start = f.StartL, startT = f.StartSim, hit = f.HitSomething, damaging = f.Damaging, speed = f.StartSpeed };
                    foreach (var p in f.Points) fd.points.Add(new PointDto { p = p.L, t = p.Sim, impact = p.Impact, jet = p.Jet });
                    d.fragments.Add(fd);
                }
                foreach (var a in rec.ArmorHits)
                    d.armor.Add(new ArmorDto { name = a.Name, p = a.L, normal = a.NormalL, dir = a.DirL, los = a.Los, nominal = a.Nominal, pen = a.Pen, angle = a.Angle, penetrated = a.Penetrated, ricochet = a.Ricochet, era = a.Era, hole = a.HoleRadius, t = a.Sim,
                        frag = a.Frag?.Id ?? 0, energy = a.Energy, speed = a.Speed, caliber = a.Caliber, rod = a.RodRadius, tnt = a.Tnt, category = a.Category.ToString(), spall = a.IsSpall, preJet = a.PreJet });
                foreach (var e in rec.Events)
                    d.events.Add(new EventDto { kind = e.Kind.ToString(), text = e.Text, subject = e.Subject, p = e.L, t = float.IsInfinity(e.Sim) || float.IsNaN(e.Sim) ? -1 : e.Sim, realDelay = e.RealT - rec.FirstImpactReal, before = e.Before, after = e.After, severity = e.Severity,
                        frag = e.Frag?.Id ?? 0, noPos = !e.HasPos, speed = e.Speed, power = e.Power, mass = e.Mass, spallHit = e.Spall, jetHit = e.Jet, hasDir = e.HasDir, dir = e.DirL });

                string file = Path.Combine(_session, $"hit_{rec.Id:0000}_{Safe(rec.TargetName)}.json");
                // JSON (hundreds of spall paths, tens of ms) is written on a worker, in the frame it hitched every time a hit cam
                // closed. JsonUtility drops nested classes (fragments, armor, events) so Newtonsoft
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { File.WriteAllText(file, AarJson.Write(d)); }
                    catch (Exception e) { _bgError = e.Message; }
                });
            }
            catch (Exception e)
            {
                HitCamMod.Log.Warning("Could not save hit: " + e.Message);
            }
            if (_bgError != null) { HitCamMod.Log.Warning("Could not save a hit: " + _bgError); _bgError = null; }
        }

        static volatile string _bgError;

        /// folder of the current mission's hits (made on demand), one per mission: NewSession() at mission start
        public static string SessionDir(bool create = false)
        {
            if (_session == null && create)
            {
                _session = Path.Combine(HitCamMod.DataDir, "hits_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
                Directory.CreateDirectory(_session);
            }
            return _session;
        }

        public static void NewSession() { _session = null; Prune(false); }

        /// keeps the disk tidy: hits of at most PastMissionsKept finished missions (newest), running mission left alone.
        /// called when the report closes and when a mission starts, deleting runs on a worker
        public static void Prune(bool missionRunning)
        {
            try
            {
                string root = HitCamMod.DataDir;
                if (!Directory.Exists(root)) return;
                int keep = Mathf.Max(0, Settings.PastMissionsKept.Value);
                string running = missionRunning ? _session : null;
                var dirs = new List<string>(Directory.GetDirectories(root, "hits_*"));
                dirs.Sort((a, b) => string.CompareOrdinal(b, a));   // newest first (names are dates)
                var drop = new List<string>();
                int kept = 0;
                foreach (var d in dirs)
                {
                    if (running != null && string.Equals(Path.GetFullPath(d), Path.GetFullPath(running), StringComparison.OrdinalIgnoreCase)) continue;
                    if (kept < keep) { kept++; continue; }
                    drop.Add(d);
                }
                if (drop.Count == 0) return;
                if (!missionRunning && _session != null && drop.Exists(d => string.Equals(Path.GetFullPath(d), Path.GetFullPath(_session), StringComparison.OrdinalIgnoreCase))) _session = null;
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    long bytes = 0;
                    int n = 0;
                    foreach (var d in drop)
                        try
                        {
                            foreach (var f in Directory.GetFiles(d, "*", SearchOption.AllDirectories)) bytes += new FileInfo(f).Length;
                            Directory.Delete(d, true);
                            n++;
                        }
                        catch { }
                    _pruned = $"{n} past missions' hits deleted ({bytes / 1e6:0.0} MB), {kept} kept";
                });
            }
            catch (Exception e) { HitCamMod.Log.Warning("Could not tidy up old hits: " + e.Message); }
        }

        static volatile string _pruned;

        /// main thread: reports what the worker cleaned up
        public static void Update()
        {
            var p = _pruned;
            if (p == null) return;
            _pruned = null;
            HitCamMod.Log.Msg("Hit files: " + p);
        }

        static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "target";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_');
        }
    }
}
