using System.Linq;
using GHPC.Humans;
using UnityEngine;

namespace GHPCHitCam
{
    /// The game resolves a whole penetration inside one frame, so frame timestamps are useless.
    /// We rebuild a physical clock instead: distance travelled / speed of each fragment,
    /// with spall starting at the moment its parent passed the spot it was born at.
    internal static class Timeline
    {
        public static void Build(HitRecord rec)
        {
            var main = rec.Main;
            if (main != null && main.Points.Count == 0) Synthesize(rec, main);
            if (main == null || main.Points.Count == 0) return;
            Bounds b = rec.Snapshot != null ? rec.Snapshot.LocalBounds : new Bounds(Vector3.zero, new Vector3(4, 3, 8));
            b.Expand(1.0f);

            float vMain = main.StartSpeed > 50 ? main.StartSpeed : (rec.Ammo != null ? rec.Ammo.MuzzleVelocity : 1000f);

            // first impact that is actually on/near the target
            int i0 = -1;
            for (int i = 0; i < main.Points.Count; i++)
                if (main.Points[i].Impact && b.Contains(main.Points[i].L)) { i0 = i; break; }
            if (i0 < 0) i0 = main.Points.FindIndex(p => p.Impact);
            if (i0 < 0) i0 = main.Points.Count - 1;

            main.Points[i0].Sim = 0f;
            for (int k = i0 - 1; k >= 0; k--)
            {
                float d = Vector3.Distance(main.Points[k + 1].L, main.Points[k].L);
                float v = main.Points[k + 1].Speed > 50 ? main.Points[k + 1].Speed : vMain;
                main.Points[k].Sim = main.Points[k + 1].Sim - d / v;
            }
            float vLast = main.Points[i0].Speed > 50 ? main.Points[i0].Speed : vMain;
            for (int k = i0 + 1; k < main.Points.Count; k++)
            {
                float d = Vector3.Distance(main.Points[k - 1].L, main.Points[k].L);
                float v = main.Points[k - 1].Speed > 50 ? main.Points[k - 1].Speed : vLast;
                vLast = v;
                main.Points[k].Sim = main.Points[k - 1].Sim + d / Mathf.Max(v, 50f);
            }
            if (main.HasStart)
                main.StartSim = main.Points[0].Sim - Vector3.Distance(main.StartL, main.Points[0].L) / vMain;
            else
                main.StartSim = main.Points[0].Sim;
            main.EndSim = main.Points[main.Points.Count - 1].Sim;

            // spall, parents before children (creation order already guarantees that)
            foreach (var f in rec.Fragments)
            {
                if (f == main) continue;
                var parent = f.Parent ?? main;
                if (!f.HasStart && f.Points.Count > 0) { f.StartL = f.Points[0].L; f.HasStart = true; }
                f.StartSim = SimAt(parent, f.StartL);
                float v = f.StartSpeed > 50 ? f.StartSpeed : 900f;
                Vector3 prev = f.StartL;
                float prevSim = f.StartSim;
                foreach (var p in f.Points)
                {
                    float vv = p.Speed > 50 ? p.Speed : v;
                    p.Sim = prevSim + Vector3.Distance(prev, p.L) / Mathf.Max(vv, 50f);
                    prev = p.L;
                    prevSim = p.Sim;
                    v = vv;
                }
                f.EndSim = prevSim;
            }

            foreach (var ah in rec.ArmorHits) ah.Sim = ah.Frag != null ? SimAt(ah.Frag, ah.L) : 0f;
            foreach (var e in rec.Events) EnsureSim(rec, e);

            // end of the interesting part: last thing that happened inside (or right next to) the vehicle
            float end = 0f;
            foreach (var f in rec.Fragments)
                foreach (var p in f.Points)
                    if (p.Sim >= 0 && p.Sim < 0.08f && b.Contains(p.L)) end = Mathf.Max(end, p.Sim);
            foreach (var e in rec.Events)
                if (!float.IsInfinity(e.Sim) && !float.IsNaN(e.Sim)) end = Mathf.Max(end, e.Sim);
            rec.InteriorEnd = Mathf.Clamp(end * 1.08f, 0.0015f, 0.06f);
            float mainInside = 0f;
            foreach (var p in main.Points)
                if (p.Sim >= 0 && b.Contains(p.L)) mainInside = Mathf.Max(mainInside, p.Sim);
            rec.CoreEnd = Mathf.Clamp(Mathf.Max(mainInside * 1.6f, 0.0025f), 0.0015f, rec.InteriorEnd);

            // approach: the last ~7 m before impact
            float approachDist = Mathf.Clamp(b.size.magnitude * 0.7f, 5f, 10f);
            rec.ApproachStart = Mathf.Max(-approachDist / vMain, main.StartSim);

            if (!rec.HasEntry)
            {
                rec.HasEntry = true;
                rec.EntryL = main.Points[i0].L;
                rec.EntryDirL = i0 > 0 ? (main.Points[i0].L - main.Points[i0 - 1].L).normalized
                    : (main.HasStart ? (main.Points[i0].L - main.StartL).normalized : Vector3.forward);
            }
            ComputeTitle(rec);
        }

        /// The game can crush a round on the first surface before a single point of its flight is logged. Its impact is
        /// then where its first plate is, or where the spall it threw started; its way in comes from where it was fired,
        /// else from the spall cone (thrown along the round's line). Without this the replay had no direction at all.
        static void Synthesize(HitRecord rec, Fragment main)
        {
            Vector3 at = Vector3.zero;
            bool found = false;
            var ah = rec.ArmorHits.FirstOrDefault(a => a.Frag == main && !a.IsSpall);
            if (ah != null) { at = ah.L; found = true; }
            Vector3 sum = Vector3.zero, dirSum = Vector3.zero;
            int n = 0;
            foreach (var f in rec.Fragments)
            {
                if (f == main || f.Parent != null && f.Parent != main || !f.HasStart) continue;
                sum += f.StartL; n++;
                if (f.Points.Count > 0) dirSum += (f.Points[f.Points.Count - 1].L - f.StartL).normalized;
            }
            if (!found && n > 0) { at = sum / n; found = true; }
            if (!found) return;
            Vector3 dir = ah != null && ah.DirL.sqrMagnitude > 1e-6f ? ah.DirL.normalized
                        : main.HasStart && (at - main.StartL).sqrMagnitude > 0.01f ? (at - main.StartL).normalized
                        : dirSum.sqrMagnitude > 1e-4f ? dirSum.normalized : Vector3.zero;
            if (dir == Vector3.zero) return;
            float v = main.StartSpeed > 50 ? main.StartSpeed : (rec.Ammo != null ? rec.Ammo.MuzzleVelocity : 1000f);
            main.Points.Add(new PathPoint { L = at - dir * 2f, HasL = true, Speed = v });
            main.Points.Add(new PathPoint { L = at, HasL = true, Speed = v, Impact = true });
            HitCamMod.Log.Msg($"Hit cam: the round left no path (crushed on the first surface): rebuilt from {(ah != null ? "its plate" : $"{n} spall starts")}, along {(ah != null ? "the plate hit" : main.HasStart ? "the line it was fired on" : "the spall cone")}");
        }

        /// Simulation time at which a fragment was closest to a local point.
        public static float SimAt(Fragment f, Vector3 l)
        {
            if (f.Points.Count == 0) return f.StartSim;
            float best = float.MaxValue, sim = f.Points[0].Sim;
            Vector3 a = f.HasStart ? f.StartL : f.Points[0].L;
            float aSim = f.HasStart ? f.StartSim : f.Points[0].Sim;
            for (int i = 0; i < f.Points.Count; i++)
            {
                Vector3 bp = f.Points[i].L;
                float bSim = f.Points[i].Sim;
                Vector3 ab = bp - a;
                float t = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(l - a, ab) / ab.sqrMagnitude) : 0f;
                float d = (a + ab * t - l).sqrMagnitude;
                if (d < best) { best = d; sim = Mathf.Lerp(aSim, bSim, t); }
                a = bp;
                aSim = bSim;
            }
            return sim;
        }

        public static void EnsureSim(HitRecord rec, HitEvent e)
        {
            if (!float.IsNaN(e.Sim)) return;
            if (e.Frag != null && e.HasPos && e.Frag.Points.Count > 0)
                e.Sim = SimAt(e.Frag, e.L);
            else if (e.Kind == EvKind.Kill || e.Kind == EvKind.Knockout || e.RealT - rec.FirstImpactReal > 0.05f || e.Frag == null)
                e.Sim = float.PositiveInfinity;    // happened after the round was done: aftermath
            else
                e.Sim = 0f;
        }

        /// The verdict, War Thunder style: "PENETRATION" only when the round got in AND did real damage. A hole that
        /// only scratched a stowage bin or the radio rack is "PENETRATED, NO DAMAGE"; damage without a penetration
        /// (HE blast on tracks / optics) is a "HIT".
        public static void ComputeTitle(HitRecord rec)
        {
            var ev = rec.Events;
            bool any(EvKind k) => ev.Any(e => e.Kind == k);
            bool pen = rec.ArmorHits.Any(a => a.Penetrated && !a.Era);
            bool ric = rec.ArmorHits.Any(a => a.Ricochet);
            bool critical = ev.Any(e => (e.Kind == EvKind.Module && e.After <= 0 && Matters(e)) || (e.Kind == EvKind.Crew && e.After >= (float)HealthStatus.Critical));
            bool real = ev.Any(e => e.Kind == EvKind.Crew || e.Kind == EvKind.AmmoHit || (e.Kind == EvKind.Module && Real(e)));
            Color red = new Color(1f, 0.22f, 0.18f), orange = new Color(1f, 0.6f, 0.15f), yellow = new Color(1f, 0.85f, 0.3f), white = new Color(0.92f, 0.94f, 1f), grey = new Color(0.75f, 0.78f, 0.82f);

            if (any(EvKind.TurretOff) && ev.Any(e => e.Kind == EvKind.TurretOff && e.Subject == "turret")) Set(rec, "TURRET BLOWN OFF", red);
            else if (rec.TargetKilled) Set(rec, "TARGET DESTROYED", red);
            else if (ev.Any(e => e.Kind == EvKind.Explosion && !OwnWarhead(rec, e))) Set(rec, "AMMUNITION EXPLODED", red);
            else if (rec.TargetKnockedOut) Set(rec, "CREW KNOCKED OUT", red);
            else if (any(EvKind.AmmoFire) || ev.Any(Propellant)) Set(rec, "AMMUNITION FIRE", red);
            else if (critical) Set(rec, "CRITICAL HIT", orange);
            else if (any(EvKind.Fire)) Set(rec, "FIRE", orange);
            else if (real && pen) Set(rec, "PENETRATION", yellow);
            else if (real) Set(rec, "HIT", yellow);
            else if (pen) Set(rec, "PENETRATED, NO DAMAGE", white);
            else if (ev.Any(e => e.Kind == EvKind.Story && e.Text.Contains("not armed"))) Set(rec, "FUZE NOT ARMED", grey);
            else if (ev.Any(e => e.Text != null && e.Text.StartsWith("Shattered"))) Set(rec, "SHELL SHATTERED", grey);
            else if (ric) Set(rec, "RICOCHET", grey);
            else Set(rec, "NON-PENETRATION", grey);
        }

        /// A propellant charge / case burning (the game's non-explosive ammo flammables: "3BM15 casing hit zone",
        /// "throwing charge hit zone", "propellant casing"): it vents as a jet and often cooks off the rack seconds later.
        internal static bool Propellant(HitEvent e) => e.Kind == EvKind.Fire && IsPropellantName(e.Subject);

        internal static bool IsPropellantName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            n = n.ToLowerInvariant();
            return n.Contains("casing") || n.Contains("charge") || n.Contains("propellant") || n.Contains("powder") || n.Contains("cartridge");
        }

        /// The game reports the round's own warhead going off inside a compartment as a compartment explosion (same
        /// TNT as the shell's filler, at the moment of impact): that is not the vehicle's ammunition exploding.
        internal static bool OwnWarhead(HitRecord rec, HitEvent e)
        {
            if (e.Kind != EvKind.Explosion || rec.Ammo == null) return false;
            float tnt = rec.Ammo.TntEquivalentKg;
            return tnt > 0f && Mathf.Abs(e.After - tnt) <= tnt * 0.01f + 1e-3f && e.RealT - rec.FirstImpactReal < 0.5f;
        }

        /// A module whose loss changes how the vehicle fights or moves (not stowage bins, boxes, the radio rack).
        internal static bool Matters(HitEvent e)
        {
            var cat = GhostSnapshot.Classify(e.Subject);
            return cat != ModCat.Other && cat != ModCat.Radio;
        }

        /// Real damage: a module that matters destroyed, or knocked down by at least 15 points / below 60 %.
        internal static bool Real(HitEvent e)
        {
            if (!Matters(e)) return false;
            if (e.After <= 0f) return true;
            return (e.Before >= 0f && e.Before - e.After >= 15f) || e.After < 60f;
        }

        static readonly string[] _order =
        {
            "NON-PENETRATION", "RICOCHET", "SHELL SHATTERED", "FUZE NOT ARMED", "PENETRATED, NO DAMAGE", "HIT", "PENETRATION", "FIRE",
            "CRITICAL HIT", "AMMUNITION FIRE", "CREW KNOCKED OUT", "AMMUNITION EXPLODED", "TARGET DESTROYED",
        };

        /// How bad a verdict is (for showing the worst result of a burst).
        public static int Rank(string title) => System.Array.IndexOf(_order, title ?? "");

        static void Set(HitRecord rec, string title, Color c)
        {
            rec.Title = title;
            rec.TitleColor = c;
        }
    }
}
